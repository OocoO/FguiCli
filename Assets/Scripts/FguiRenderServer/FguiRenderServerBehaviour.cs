using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FairyGUI;
using UnityEngine;

namespace FguiRenderServer
{
    public sealed class FguiRenderServerBehaviour : MonoBehaviour
    {
        const string ResultPrefix = "[FGUI_RENDER_RESULT]";
        const string DefaultHost = "127.0.0.1";
        const int DefaultPort = 18765;

        // 渲染器自身窗口的默认尺寸，可用 --window-width / --window-height 覆盖。
        // 保持小窗口：渲染输出走离屏贴图，与窗口尺寸无关；需要测什么分辨率请用 screenWidth/screenHeight。
        const int DefaultWindowWidth = 1280;
        const int DefaultWindowHeight = 720;

        // 默认模拟屏幕（游戏分辨率）与默认输出 PNG 尺寸。
        const int DefaultScreenWidth = 1920;
        const int DefaultScreenHeight = 1080;

        const string UiUrlPrefix = "ui://";

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        const int SwMinimize = 6;

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
#endif

        readonly Queue<RenderJob> _pendingJobs = new Queue<RenderJob>();
        readonly object _pendingLock = new object();

        HttpListener _listener;
        CancellationTokenSource _listenerCancellation;
        RenderJob _activeJob;

        bool _oneShotMode;
        bool _autoMinimize = true;
        string _host = DefaultHost;
        int _port = DefaultPort;

        // 启动时的窗口尺寸，作为不指定 windowHeight 时的恒定窗口高度基准。
        int _bootWindowHeight = DefaultWindowHeight;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);

            Dictionary<string, string> args = ParseCommandLineArguments(Environment.GetCommandLineArgs());

            int windowWidth = DefaultWindowWidth;
            int windowHeight = DefaultWindowHeight;
            if (TryReadInt(args, "window-width", out int argWindowWidth) && argWindowWidth > 0)
            {
                windowWidth = argWindowWidth;
            }
            if (TryReadInt(args, "window-height", out int argWindowHeight) && argWindowHeight > 0)
            {
                windowHeight = argWindowHeight;
            }

            _bootWindowHeight = windowHeight;
            Screen.SetResolution(windowWidth, windowHeight, false);
            UIConfig.renderingTextBrighterOnDesktop = false;
            Stage.Instantiate();
            // 基准缩放；每次渲染会按请求里的模拟屏幕重新计算。
            GRoot.inst.SetContentScaleFactor(
                FguiAdaptationSettings.DefaultDesignResolutionX,
                FguiAdaptationSettings.DefaultDesignResolutionY);

            _oneShotMode = args.ContainsKey("render-once");

            // Server mode defaults: run in background, windowed. The window stays small
            // (--window-width / --window-height, default 1280x720) and may be minimized; the
            // rendered resolution comes from the simulated screen, not from this window.
            Application.runInBackground = true;

            if (args.ContainsKey("no-minimize"))
            {
                _autoMinimize = false;
            }

            if (_autoMinimize)
            {
                StartCoroutine(MinimizeWindowNextFrame());
            }

            if (TryReadInt(args, "port", out int port) && port > 0)
            {
                _port = port;
            }

            if (args.TryGetValue("host", out string host) && !string.IsNullOrWhiteSpace(host))
            {
                _host = host.Trim();
            }

            if (_oneShotMode)
            {
                RenderRequest request = BuildOneShotRequest(args);
                EnqueueJob(request);
                StartCoroutine(WaitForOneShotAndExit());
                return;
            }

            StartListener();
        }

        System.Collections.IEnumerator MinimizeWindowNextFrame()
        {
            // Wait one frame to ensure native window is created before minimizing.
            yield return null;

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            IntPtr hwnd = GetActiveWindow();
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SwMinimize);
            }
#endif
        }

        void Update()
        {
            if (_activeJob == null)
            {
                lock (_pendingLock)
                {
                    if (_pendingJobs.Count > 0)
                    {
                        _activeJob = _pendingJobs.Dequeue();
                    }
                }

                if (_activeJob != null)
                {
                    StartCoroutine(RunRenderJob(_activeJob));
                }
            }
        }

        void OnDestroy()
        {
            StopListener();
        }

        void StartListener()
        {
            if (_listener != null)
            {
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add(string.Format("http://{0}:{1}/", _host, _port));
            _listener.Start();

            _listenerCancellation = new CancellationTokenSource();
            _ = Task.Run(() => ListenLoopAsync(_listenerCancellation.Token));

            UnityEngine.Debug.Log(string.Format("FGUI Render Server listening on http://{0}:{1}/", _host, _port));
        }

        void StopListener()
        {
            if (_listenerCancellation != null)
            {
                _listenerCancellation.Cancel();
                _listenerCancellation.Dispose();
                _listenerCancellation = null;
            }

            if (_listener != null)
            {
                try
                {
                    _listener.Stop();
                    _listener.Close();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("FGUI Render Server stop listener failed: " + ex.Message);
                }
                finally
                {
                    _listener = null;
                }
            }
        }

        async Task ListenLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        UnityEngine.Debug.LogWarning("FGUI Render Server listener error: " + ex.Message);
                    }
                    continue;
                }

                _ = Task.Run(() => HandleContextAsync(context, cancellationToken), cancellationToken);
            }
        }

        async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                string path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath.ToLowerInvariant();

                if (context.Request.HttpMethod == "GET" && path == "/health")
                {
                    await WriteJsonAsync(context, new HealthResponse
                    {
                        ok = true,
                        message = "ready",
                        pendingJobs = GetPendingCount(),
                        hasActiveJob = _activeJob != null,
                    });
                    return;
                }

                if (context.Request.HttpMethod == "POST" && path == "/render_page")
                {
                    string body = ReadRequestBody(context.Request);
                    RenderRequest request = JsonUtility.FromJson<RenderRequest>(body);
                    string validationError = ValidateRequest(request);
                    if (validationError != null)
                    {
                        await WriteJsonAsync(context, new RenderResult
                        {
                            ok = false,
                            message = validationError,
                        }, 400);
                        return;
                    }

                    RenderJob job = EnqueueJob(request);
                    RenderResult result;
                    int timeoutSec = request.timeoutSec <= 0 ? 120 : request.timeoutSec;

                    try
                    {
                        Task completed = await Task.WhenAny(job.Completion.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec), cancellationToken));
                        if (completed != job.Completion.Task)
                        {
                            result = new RenderResult
                            {
                                ok = false,
                                message = "render timeout",
                                jobId = job.jobId,
                            };
                        }
                        else
                        {
                            result = job.Completion.Task.Result;
                        }
                    }
                    catch (Exception ex)
                    {
                        result = new RenderResult
                        {
                            ok = false,
                            message = "render request failed: " + ex.Message,
                            jobId = job.jobId,
                        };
                    }

                    await WriteJsonAsync(context, result, result.ok ? 200 : 500);
                    return;
                }

                await WriteJsonAsync(context, new RenderResult
                {
                    ok = false,
                    message = "endpoint not found",
                }, 404);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                if (context.Response.OutputStream.CanWrite)
                {
                    await WriteJsonAsync(context, new RenderResult
                    {
                        ok = false,
                        message = "internal error: " + ex.Message,
                    }, 500);
                }
            }
            finally
            {
                try
                {
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // Ignore stream close errors.
                }
            }
        }

        RenderJob EnqueueJob(RenderRequest request)
        {
            RenderJob job = CreateRenderJob(request);

            lock (_pendingLock)
            {
                _pendingJobs.Enqueue(job);
            }

            return job;
        }

        public Coroutine StartRenderRequest(RenderRequest request, Action<RenderResult> onCompleted = null)
        {
            string validationError = ValidateRequest(request);
            if (validationError != null)
            {
                throw new ArgumentException(validationError, nameof(request));
            }

            RenderJob job = EnqueueJob(request);
            return StartCoroutine(WaitForRenderJob(job, onCompleted));
        }

        RenderJob CreateRenderJob(RenderRequest request)
        {
            return new RenderJob
            {
                jobId = Guid.NewGuid().ToString("N"),
                request = request,
                Completion = new TaskCompletionSource<RenderResult>(),
            };
        }

        int GetPendingCount()
        {
            lock (_pendingLock)
            {
                return _pendingJobs.Count;
            }
        }

        System.Collections.IEnumerator WaitForOneShotAndExit()
        {
            while (_activeJob == null && GetPendingCount() > 0)
            {
                yield return null;
            }

            while (_activeJob != null)
            {
                yield return null;
            }

            lock (_pendingLock)
            {
                if (_pendingJobs.Count == 0)
                {
                    Application.Quit();
                }
            }
        }

        System.Collections.IEnumerator RunRenderJob(RenderJob job)
        {
            yield return ExecuteRenderJob(job, true, _oneShotMode);
        }

        System.Collections.IEnumerator WaitForRenderJob(RenderJob job, Action<RenderResult> onCompleted)
        {
            while (!job.Completion.Task.IsCompleted)
            {
                yield return null;
            }

            if (onCompleted != null)
            {
                onCompleted(job.Completion.Task.Result);
            }
        }

        System.Collections.IEnumerator ExecuteRenderJob(RenderJob job, bool clearActiveJob, bool emitOneShotResult)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            RenderResult result = new RenderResult
            {
                ok = false,
                jobId = job.jobId,
            };
            Exception error = null;
            string pngPath = null;
            GObject panel = null;

            RenderRequest request = job.request;
            ScreenSetup setup = ScreenSetup.Default;

            // Resolve the simulated screen first: the player window has to be reshaped before the
            // packages are torn down and the panel is built.
            try
            {
                FguiAdaptationSettings adaptation = FguiAdaptationSettings.Load(request.projectRootDir);
                setup = ScreenSetup.Resolve(request, adaptation);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            // Keep the player window in step with the simulated screen so what you see has the same
            // shape as what gets rendered: constant height, width derived from the screen aspect.
            // This stays outside the try block below because C# forbids yielding from a try block
            // that has a catch clause.
            if (error == null && !request.keepWindowSize)
            {
                int targetWindowHeight = request.windowHeight > 0 ? request.windowHeight : _bootWindowHeight;
                int targetWindowWidth = request.windowWidth > 0
                    ? request.windowWidth
                    : Mathf.Max(1, Mathf.RoundToInt((float)targetWindowHeight * setup.screenWidth / setup.screenHeight));

                if (targetWindowWidth != Screen.width || targetWindowHeight != Screen.height)
                {
                    Screen.SetResolution(targetWindowWidth, targetWindowHeight, false);
                    // Let the player actually apply the new window size; the screen simulation is
                    // re-asserted after this and again right before capture.
                    yield return null;
                    yield return null;
                }
            }

            try
            {
                //Teardown order matters: detach and dispose the previous panel *before* releasing the
                //packages. A text that never got laid out (typically a richtext hidden by a controller
                //gear) builds its lines lazily from RichTextField.Dispose -> CleanupObjects, and that
                //build rasterizes glyphs. If the packages are gone first, UIPackage.Dispose has already
                //destroyed the ExternalFont atlas and the rasterizer is called with a dead texture,
                //which is a native crash inside FontEngine.TryAddGlyphToTexture_Internal.
                GRoot.inst.RemoveChildren(0, -1, true);
                UIPackage.RemoveAllPackages(true);

                ApplyScreenSimulation(setup);

                FguiProjectLoader loader = FguiProjectLoader.LoadProject(request.projectRootDir, request.branchTag);
                UIPackage package = loader.GetPackage(request.packageName);
                if (package == null)
                {
                    throw new InvalidOperationException("package not found: " + request.packageName);
                }

                panel = CreatePanelFromRequest(request);
                ApplyDisplayOverrides(panel, request.overrides);

                PreparePanelForCapture(panel, setup.designResolutionX, setup.designResolutionY);
                panel.position = Vector3.zero;
                GRoot.inst.AddChild(panel);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (error == null)
            {
                // Wait one frame so FairyGUI layout and textures are ready before capture.
                yield return null;
                yield return null;

                try
                {
                    // Re-assert the simulation right before capture: StageCamera resets the stage
                    // scale/camera whenever it observes a real screen size change, and that must
                    // never leak into the simulated screen we are about to shoot.
                    ApplyScreenSimulation(setup);

                    Texture2D outputTexture = CaptureScreen(setup.captureWidth, setup.captureHeight, setup);
                    if (outputTexture == null)
                    {
                        throw new InvalidOperationException("capture failed: screenshot texture is null");
                    }

                    pngPath = Path.GetFullPath(request.outPng);
                    string pngDirectory = Path.GetDirectoryName(pngPath);
                    if (!string.IsNullOrEmpty(pngDirectory))
                    {
                        Directory.CreateDirectory(pngDirectory);
                    }

                    Texture2D finalTexture = outputTexture;
                    if (!request.keepFullFrame)
                    {
                        // Trim transparent border pixels.
                        RectInt opaqueBounds = CalculateOpaqueBounds(outputTexture,
                            new RectInt(0, 0, outputTexture.width, outputTexture.height));
                        finalTexture = CropTexture(outputTexture, opaqueBounds);
                        if (finalTexture != outputTexture)
                        {
                            Destroy(outputTexture);
                        }
                    }

                    byte[] pngBytes = finalTexture.EncodeToPNG();
                    result.width = finalTexture.width;
                    result.height = finalTexture.height;

                    Destroy(finalTexture);
                    File.WriteAllBytes(pngPath, pngBytes);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }

            stopwatch.Stop();
            result.durationMs = (int)stopwatch.ElapsedMilliseconds;
            result.screenWidth = setup.screenWidth;
            result.screenHeight = setup.screenHeight;
            result.logicalWidth = setup.logicalWidth;
            result.logicalHeight = setup.logicalHeight;
            result.contentScaleFactor = setup.contentScaleFactor;
            result.designResolutionX = setup.designResolutionX;
            result.designResolutionY = setup.designResolutionY;
            result.screenMatchMode = setup.screenMatchMode.ToString();
            result.windowWidth = Screen.width;
            result.windowHeight = Screen.height;

            if (error == null)
            {
                result.ok = true;
                result.message = "ok";
                result.pngPath = pngPath;
            }
            else
            {
                result.ok = false;
                result.message = error.Message;
            }

            job.Completion.TrySetResult(result);
            if (clearActiveJob)
            {
                _activeJob = null;
            }

            if (emitOneShotResult)
            {
                Debug.Log(ResultPrefix + JsonUtility.ToJson(result));
            }
        }

        RenderRequest BuildOneShotRequest(Dictionary<string, string> args)
        {
            RenderRequest request = new RenderRequest();

            if (!args.TryGetValue("project-root-dir", out request.projectRootDir))
            {
                if (args.TryGetValue("package-dir", out string packageDir) && !string.IsNullOrWhiteSpace(packageDir))
                {
                    request.projectRootDir = TryInferProjectRootFromPackageDir(packageDir);
                    if (!args.ContainsKey("package-name"))
                    {
                        request.packageName = Path.GetFileName(Path.GetFullPath(packageDir));
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(request.packageName))
            {
                args.TryGetValue("package-name", out request.packageName);
            }
            args.TryGetValue("component-name", out request.componentName);
            args.TryGetValue("component-path", out request.componentPath);
            args.TryGetValue("component-id", out request.componentId);
            args.TryGetValue("out-png", out request.outPng);
            args.TryGetValue("branch", out request.branchTag);

            // 0 表示自动：输出尺寸跟随模拟屏幕，模拟屏幕跟随内置默认值。
            if (!TryReadInt(args, "width", out request.width))
            {
                request.width = 0;
            }

            if (!TryReadInt(args, "height", out request.height))
            {
                request.height = 0;
            }

            if (!TryReadInt(args, "screen-width", out request.screenWidth))
            {
                request.screenWidth = 0;
            }

            if (!TryReadInt(args, "screen-height", out request.screenHeight))
            {
                request.screenHeight = 0;
            }

            if (!TryReadInt(args, "design-width", out request.designResolutionX))
            {
                request.designResolutionX = 0;
            }

            if (!TryReadInt(args, "design-height", out request.designResolutionY))
            {
                request.designResolutionY = 0;
            }

            if (args.TryGetValue("match", out string matchMode))
            {
                request.screenMatchMode = matchMode;
            }

            if (args.TryGetValue("scale-mode", out string scaleMode))
            {
                request.scaleMode = scaleMode;
            }

            request.ignoreOrientation = args.ContainsKey("ignore-orientation");
            request.keepFullFrame = args.ContainsKey("keep-full-frame");
            request.keepWindowSize = args.ContainsKey("keep-window-size");

            if (!TryReadInt(args, "window-height", out request.windowHeight))
            {
                request.windowHeight = 0;
            }

            if (!TryReadInt(args, "window-width", out request.windowWidth))
            {
                request.windowWidth = 0;
            }

            if (!TryReadInt(args, "timeout", out request.timeoutSec))
            {
                request.timeoutSec = 120;
            }

            string validationError = ValidateRequest(request);
            if (validationError != null)
            {
                throw new ArgumentException(validationError);
            }

            return request;
        }

        static string TryInferProjectRootFromPackageDir(string packageDir)
        {
            string fullPackageDir = Path.GetFullPath(packageDir);
            DirectoryInfo packageDirectory = new DirectoryInfo(fullPackageDir);
            DirectoryInfo assetsDirectory = packageDirectory.Parent;
            if (assetsDirectory != null && string.Equals(assetsDirectory.Name, "assets", StringComparison.OrdinalIgnoreCase))
            {
                DirectoryInfo rootDirectory = assetsDirectory.Parent;
                if (rootDirectory != null)
                {
                    return rootDirectory.FullName;
                }
            }

            return fullPackageDir;
        }

        static bool TryReadInt(Dictionary<string, string> args, string key, out int value)
        {
            value = 0;
            return args.TryGetValue(key, out string raw)
                   && !string.IsNullOrWhiteSpace(raw)
                   && int.TryParse(raw.Trim(), out value);
        }

        static Dictionary<string, string> ParseCommandLineArguments(string[] args)
        {
            Dictionary<string, string> parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string token = args[i];
                if (string.IsNullOrEmpty(token) || !token.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string key = token.Substring(2);
                string value = "true";

                if (i + 1 < args.Length)
                {
                    string next = args[i + 1];
                    if (!next.StartsWith("--", StringComparison.Ordinal))
                    {
                        value = next;
                        i += 1;
                    }
                }

                parsed[key] = value;
            }

            return parsed;
        }

        static string ValidateRequest(RenderRequest request)
        {
            if (request == null)
            {
                return "request body is required";
            }

            if (string.IsNullOrWhiteSpace(request.projectRootDir))
            {
                return "projectRootDir is required";
            }

            if (string.IsNullOrWhiteSpace(request.packageName))
            {
                return "packageName is required";
            }

            int selectorCount = 0;
            if (!string.IsNullOrWhiteSpace(request.componentName))
            {
                selectorCount += 1;
            }
            if (!string.IsNullOrWhiteSpace(request.componentPath))
            {
                selectorCount += 1;
            }
            if (!string.IsNullOrWhiteSpace(request.componentId))
            {
                selectorCount += 1;
            }

            if (selectorCount == 0)
            {
                return "one of componentName / componentPath / componentId is required";
            }

            if (selectorCount > 1)
            {
                return "only one of componentName / componentPath / componentId can be set";
            }

            if (!string.IsNullOrWhiteSpace(request.componentId)
                && !request.componentId.Trim().StartsWith(UiUrlPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "componentId must start with ui://";
            }

            if (string.IsNullOrWhiteSpace(request.outPng))
            {
                return "outPng is required";
            }

            if (request.width < 0 || request.height < 0)
            {
                return "width / height must not be negative (0 means auto)";
            }

            if (request.screenWidth < 0 || request.screenHeight < 0)
            {
                return "screenWidth / screenHeight must not be negative (0 means auto)";
            }

            if (request.designResolutionX < 0 || request.designResolutionY < 0)
            {
                return "designResolutionX / designResolutionY must not be negative (0 means auto)";
            }

            if (request.windowWidth < 0 || request.windowHeight < 0)
            {
                return "windowWidth / windowHeight must not be negative (0 means auto)";
            }

            int effectiveScreenWidth = request.screenWidth > 0
                ? request.screenWidth
                : (request.width > 0 ? request.width : DefaultScreenWidth);
            int effectiveScreenHeight = request.screenHeight > 0
                ? request.screenHeight
                : (request.height > 0 ? request.height : DefaultScreenHeight);

            if (effectiveScreenWidth < 16 || effectiveScreenHeight < 16)
            {
                return "screen resolution is too small: " + effectiveScreenWidth + "x" + effectiveScreenHeight;
            }

            // 越界的分辨率会先尝试分配一张巨大的 RenderTexture，直接给出可读的错误更友好。
            long pixelCount = (long)effectiveScreenWidth * effectiveScreenHeight;
            if (pixelCount > 8192L * 8192L)
            {
                return "screen resolution is too large: " + effectiveScreenWidth + "x" + effectiveScreenHeight;
            }

            return null;
        }

        static string ReadRequestBody(HttpListenerRequest request)
        {
            using (StreamReader reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        static async Task WriteJsonAsync(HttpListenerContext context, object payload, int statusCode = 200)
        {
            string body = JsonUtility.ToJson(payload);
            byte[] bytes = Encoding.UTF8.GetBytes(body);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        static RectInt CalculateCaptureRect(GObject panel, int captureWidth, int captureHeight)
        {
            if (panel == null || panel.displayObject == null)
            {
                return new RectInt(0, 0, captureWidth, captureHeight);
            }

            Rect stageBounds = panel.displayObject.GetBounds(Stage.inst);
            if (!IsFiniteRect(stageBounds) || stageBounds.width <= 0 || stageBounds.height <= 0)
            {
                return new RectInt(0, 0, captureWidth, captureHeight);
            }

            float screenWidth = Mathf.Max(1, Screen.width);
            float screenHeight = Mathf.Max(1, Screen.height);
            float scaleX = captureWidth / screenWidth;
            float scaleY = captureHeight / screenHeight;

            int minX = Mathf.Clamp(Mathf.FloorToInt(stageBounds.xMin * scaleX), 0, captureWidth);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(stageBounds.xMax * scaleX), 0, captureWidth);
            int minY = Mathf.Clamp(Mathf.FloorToInt((screenHeight - stageBounds.yMax) * scaleY), 0, captureHeight);
            int maxY = Mathf.Clamp(Mathf.CeilToInt((screenHeight - stageBounds.yMin) * scaleY), 0, captureHeight);

            int width = maxX - minX;
            int height = maxY - minY;
            if (width <= 0 || height <= 0)
            {
                return new RectInt(0, 0, captureWidth, captureHeight);
            }

            return new RectInt(minX, minY, width, height);
        }

        /// <summary>
        /// 一次渲染使用的模拟屏幕参数：既决定 FGUI 的自适应缩放（游戏分辨率），
        /// 也决定离屏渲染的输出尺寸。渲染期间真实窗口尺寸不参与计算，
        /// 因此可以在 1080p 显示器上直接预览 4K / 带鱼屏 / 竖屏等分辨率。
        /// </summary>
        public struct ScreenSetup
        {
            public int screenWidth;
            public int screenHeight;
            public int captureWidth;
            public int captureHeight;
            public int logicalWidth;
            public int logicalHeight;
            public int designResolutionX;
            public int designResolutionY;
            public float contentScaleFactor;
            public UIContentScaler.ScaleMode scaleMode;
            public UIContentScaler.ScreenMatchMode screenMatchMode;
            public bool ignoreOrientation;
            public float constantScaleFactor;

            public static ScreenSetup Default
            {
                get
                {
                    return new ScreenSetup
                    {
                        screenWidth = DefaultScreenWidth,
                        screenHeight = DefaultScreenHeight,
                        captureWidth = DefaultScreenWidth,
                        captureHeight = DefaultScreenHeight,
                        logicalWidth = DefaultScreenWidth,
                        logicalHeight = DefaultScreenHeight,
                        designResolutionX = FguiAdaptationSettings.DefaultDesignResolutionX,
                        designResolutionY = FguiAdaptationSettings.DefaultDesignResolutionY,
                        contentScaleFactor = 1,
                        scaleMode = UIContentScaler.ScaleMode.ScaleWithScreenSize,
                        screenMatchMode = UIContentScaler.ScreenMatchMode.MatchWidthOrHeight,
                        ignoreOrientation = false,
                        constantScaleFactor = 1,
                    };
                }
            }

            /// <summary>
            /// 合并请求参数与项目 settings/Adaptation.json：请求里显式给出的值优先，
            /// 其余取项目配置，项目配置缺失时回退到内置默认值。
            /// </summary>
            public static ScreenSetup Resolve(RenderRequest request, FguiAdaptationSettings adaptation)
            {
                if (adaptation == null)
                {
                    adaptation = FguiAdaptationSettings.CreateDefault();
                }

                ScreenSetup setup = Default;

                // 模拟屏幕分辨率：显式 screenWidth/Height > 输出尺寸 > 内置默认。
                setup.screenWidth = request.screenWidth > 0
                    ? request.screenWidth
                    : (request.width > 0 ? request.width : DefaultScreenWidth);
                setup.screenHeight = request.screenHeight > 0
                    ? request.screenHeight
                    : (request.height > 0 ? request.height : DefaultScreenHeight);

                // 输出 PNG 尺寸：未指定时跟随模拟屏幕，保证 1:1 的“设备截图”。
                setup.captureWidth = request.width > 0 ? request.width : setup.screenWidth;
                setup.captureHeight = request.height > 0 ? request.height : setup.screenHeight;

                setup.designResolutionX = request.designResolutionX > 0
                    ? request.designResolutionX
                    : adaptation.designResolutionX;
                setup.designResolutionY = request.designResolutionY > 0
                    ? request.designResolutionY
                    : adaptation.designResolutionY;

                setup.scaleMode = FguiAdaptationSettings.ParseScaleMode(request.scaleMode, adaptation.scaleMode);
                setup.screenMatchMode = FguiAdaptationSettings.ParseScreenMatchMode(
                    request.screenMatchMode, adaptation.screenMatchMode);
                setup.ignoreOrientation = request.ignoreOrientation || adaptation.ignoreOrientation;
                setup.constantScaleFactor = adaptation.constantScaleFactor;

                FguiAdaptationSettings probe = new FguiAdaptationSettings
                {
                    scaleMode = setup.scaleMode,
                    screenMatchMode = setup.screenMatchMode,
                    designResolutionX = setup.designResolutionX,
                    designResolutionY = setup.designResolutionY,
                    ignoreOrientation = setup.ignoreOrientation,
                    constantScaleFactor = setup.constantScaleFactor,
                };
                setup.contentScaleFactor = probe.ComputeScaleFactor(setup.screenWidth, setup.screenHeight);

                // GRoot 的逻辑尺寸 = 屏幕尺寸 / 缩放系数，即 UI 实际能用的“设计像素”画布。
                setup.logicalWidth = Mathf.Max(1, Mathf.CeilToInt(setup.screenWidth / setup.contentScaleFactor));
                setup.logicalHeight = Mathf.Max(1, Mathf.CeilToInt(setup.screenHeight / setup.contentScaleFactor));

                return setup;
            }
        }

        /// <summary>
        /// 按模拟屏幕重算 FGUI 的缩放链路：内容缩放系数、Stage 的世界单位、
        /// GRoot 的逻辑尺寸。全部用显式数值驱动，不依赖真实窗口的 Screen.width/height。
        /// </summary>
        static void ApplyScreenSimulation(ScreenSetup setup)
        {
            // 与 UIContentScaler.ApplyChange / GRoot.ApplyContentScaleFactor 保持一致，
            // 但屏幕尺寸来自请求参数而不是真实窗口。
            UIContentScaler scaler = Stage.inst.gameObject.GetComponent<UIContentScaler>();
            if (scaler != null)
            {
                scaler.scaleMode = setup.scaleMode;
                scaler.designResolutionX = setup.designResolutionX;
                scaler.designResolutionY = setup.designResolutionY;
                scaler.screenMatchMode = setup.screenMatchMode;
                scaler.ignoreOrientation = setup.ignoreOrientation;
                scaler.constantScaleFactor = setup.constantScaleFactor;
            }

            UIContentScaler.scaleFactor = setup.contentScaleFactor;

            // 世界单位/屏幕像素：StageCamera 在 constantSize 下固定显示 10 个世界单位的高度，
            // 因此按模拟屏幕高度换算，整个模拟屏幕正好铺满相机视野。
            StageCamera.UnitsPerPixel = StageCamera.DefaultCameraSize * 2f / setup.screenHeight;

            if (Stage.inst != null && Stage.inst.cachedTransform != null)
            {
                Stage.inst.cachedTransform.localScale = new Vector3(
                    StageCamera.UnitsPerPixel, StageCamera.UnitsPerPixel, StageCamera.UnitsPerPixel);
            }

            ConfigureStageCamera(setup.screenWidth, setup.screenHeight, StageCamera.DefaultCameraSize);
            StageCamera.screenSizeVer++;

            GRoot.inst.SetSize(setup.logicalWidth, setup.logicalHeight);
            GRoot.inst.SetScale(setup.contentScaleFactor, setup.contentScaleFactor);
        }

        /// <summary>
        /// 把 Stage 相机摆到“左下角对齐世界原点”的位置，并让它覆盖 viewWidth:viewHeight 比例、
        /// 半高为 halfHeight 的世界范围。StageCamera 原生逻辑等价于 halfHeight = DefaultCameraSize。
        /// </summary>
        static void ConfigureStageCamera(int viewWidth, int viewHeight, float halfHeight)
        {
            Camera camera = StageCamera.main;
            if (camera == null || viewHeight <= 0 || viewWidth <= 0)
            {
                return;
            }

            float aspect = (float)viewWidth / viewHeight;
            camera.orthographicSize = halfHeight;
            camera.transform.localPosition = new Vector3(
                halfHeight * aspect,
                -halfHeight,
                camera.transform.localPosition.z);
        }

        static void PreparePanelForCapture(GObject panel, int designResolutionX, int designResolutionY)
        {
            if (panel == null)
            {
                return;
            }

            if (ShouldMakeFullScreen(panel, designResolutionX, designResolutionY))
            {
                panel.MakeFullScreen();
            }
        }

        /// <summary>
        /// 与设计分辨率比较，而不是与当前屏幕的逻辑尺寸比较：一个按 1920x1080 设计的全屏页面
        /// 在任何模拟分辨率下都应该铺满屏幕，否则换分辨率后它会缩成一块，测不出自适应的真实表现。
        /// </summary>
        static bool ShouldMakeFullScreen(GObject panel, int designResolutionX, int designResolutionY)
        {
            if (panel == null)
            {
                return false;
            }

            float panelWidth = panel.initWidth > 0 ? panel.initWidth : panel.width;
            float panelHeight = panel.initHeight > 0 ? panel.initHeight : panel.height;

            if (panelWidth <= 0 || panelHeight <= 0 || designResolutionX <= 0 || designResolutionY <= 0)
            {
                return false;
            }

            float widthRatio = panelWidth / designResolutionX;
            float heightRatio = panelHeight / designResolutionY;
            return widthRatio >= 0.85f && heightRatio >= 0.85f;
        }

        static bool IsFiniteRect(Rect rect)
        {
            return IsFinite(rect.xMin)
                   && IsFinite(rect.xMax)
                   && IsFinite(rect.yMin)
                   && IsFinite(rect.yMax);
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static Texture2D CropTexture(Texture2D source, RectInt cropRect)
        {
            if (source == null)
            {
                return null;
            }

            RectInt clamped = new RectInt(
                Mathf.Clamp(cropRect.x, 0, source.width),
                Mathf.Clamp(cropRect.y, 0, source.height),
                Mathf.Clamp(cropRect.width, 0, source.width),
                Mathf.Clamp(cropRect.height, 0, source.height));

            if (clamped.x + clamped.width > source.width)
            {
                clamped.width = source.width - clamped.x;
            }

            if (clamped.y + clamped.height > source.height)
            {
                clamped.height = source.height - clamped.y;
            }

            if (clamped.width <= 0 || clamped.height <= 0)
            {
                return source;
            }

            if (clamped.x == 0
                && clamped.y == 0
                && clamped.width == source.width
                && clamped.height == source.height)
            {
                return source;
            }

            Texture2D cropped = new Texture2D(clamped.width, clamped.height, source.format, false);
            cropped.SetPixels(source.GetPixels(clamped.x, clamped.y, clamped.width, clamped.height));
            cropped.Apply();
            return cropped;
        }

        static RectInt CalculateOpaqueBounds(Texture2D source, RectInt fallbackRect)
        {
            if (source == null)
            {
                return fallbackRect;
            }

            Color32[] pixels = source.GetPixels32();
            int minX = source.width;
            int minY = source.height;
            int maxX = -1;
            int maxY = -1;

            for (int y = 0; y < source.height; y++)
            {
                int rowOffset = y * source.width;
                for (int x = 0; x < source.width; x++)
                {
                    if (pixels[rowOffset + x].a == 0)
                    {
                        continue;
                    }

                    if (x < minX)
                    {
                        minX = x;
                    }
                    if (x > maxX)
                    {
                        maxX = x;
                    }
                    if (y < minY)
                    {
                        minY = y;
                    }
                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return fallbackRect;
            }

            return new RectInt(minX, minY, (maxX - minX) + 1, (maxY - minY) + 1);
        }

        [Serializable]
        public sealed class RenderRequest
        {
            public string projectRootDir;
            public string packageName;
            public string componentName;
            public string componentPath;
            public string componentId;
            public string outPng;
            public string branchTag;

            /// <summary>输出 PNG 尺寸；0 = 跟随模拟屏幕分辨率。</summary>
            public int width;
            /// <summary>输出 PNG 尺寸；0 = 跟随模拟屏幕分辨率。</summary>
            public int height;

            /// <summary>模拟屏幕宽（游戏分辨率）。0 = 跟随 width，width 也为 0 时用 1920。</summary>
            public int screenWidth;
            /// <summary>模拟屏幕高（游戏分辨率）。0 = 跟随 height，height 也为 0 时用 1080。</summary>
            public int screenHeight;

            /// <summary>设计分辨率 X。0 = 取项目 settings/Adaptation.json。</summary>
            public int designResolutionX;
            /// <summary>设计分辨率 Y。0 = 取项目 settings/Adaptation.json。</summary>
            public int designResolutionY;

            /// <summary>ConstantPixelSize / ScaleWithScreenSize / ConstantPhysicalSize。空 = 取项目配置。</summary>
            public string scaleMode;
            /// <summary>MatchWidthOrHeight / MatchWidth / MatchHeight。空 = 取项目配置。</summary>
            public string screenMatchMode;
            /// <summary>true 时忽略设计分辨率的横竖屏方向修正。</summary>
            public bool ignoreOrientation;

            /// <summary>
            /// 渲染器窗口高度；0 = 用启动时的高度（默认 720）。
            /// 窗口宽度默认按模拟屏幕比例算出，保证“看到的窗口”和“渲染结果”是同一个形状。
            /// </summary>
            public int windowHeight;

            /// <summary>渲染器窗口宽度；0 = 按模拟屏幕比例自动计算 = windowHeight * screenWidth / screenHeight。</summary>
            public int windowWidth;

            /// <summary>true 时完全不动窗口，保持启动尺寸。</summary>
            public bool keepWindowSize;

            /// <summary>true 时保留完整画面，不裁掉四周透明像素（默认 false = 裁掉）。</summary>
            public bool keepFullFrame;

            public int timeoutSec = 120;
            public List<DisplayOverride> overrides;
        }

        [Serializable]
        public sealed class DisplayOverride
        {
            public List<string> path;
            public string controller;
            public int page = -1;
            public bool forceVisible;
        }

        static void ApplyDisplayOverrides(GObject panel, List<DisplayOverride> overrides)
        {
            if (panel == null || overrides == null || overrides.Count == 0)
            {
                return;
            }

            foreach (DisplayOverride ov in overrides)
            {
                if (ov == null)
                {
                    continue;
                }

                List<string> path = ov.path ?? new List<string>();
                int containerDepth = path.Count;
                if (ov.forceVisible && containerDepth > 0)
                {
                    containerDepth -= 1;
                }

                GComponent container = panel as GComponent;
                bool ok = container != null;
                for (int i = 0; ok && i < containerDepth; i++)
                {
                    GObject child = container.GetChild(path[i]);
                    container = child as GComponent;
                    ok = container != null;
                    if (!ok)
                    {
                        Debug.LogWarning(string.Format("override skipped: path segment not a component: {0}", path[i]));
                    }
                }

                if (!ok)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(ov.controller))
                {
                    Controller ctrl = container.GetController(ov.controller);
                    if (ctrl != null && ov.page >= 0 && ov.page < ctrl.pageCount)
                    {
                        ctrl.selectedIndex = ov.page;
                    }
                    else
                    {
                        Debug.LogWarning(string.Format("override skipped: controller/page invalid: {0} page={1}", ov.controller, ov.page));
                    }
                }

                if (ov.forceVisible && path.Count > 0)
                {
                    GObject target = container.GetChild(path[path.Count - 1]);
                    if (target != null)
                    {
                        target.visible = true;
                    }
                    else
                    {
                        Debug.LogWarning(string.Format("override skipped: child not found: {0}", path[path.Count - 1]));
                    }
                }
            }
        }
        static GObject CreatePanelFromRequest(RenderRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.componentId))
            {
                string componentId = request.componentId.Trim();
                GObject panelById = UIPackage.CreateObjectFromURL(componentId);
                if (panelById == null)
                {
                    throw new InvalidOperationException("component not found by id: " + componentId);
                }

                return panelById;
            }
            
            string componentName = request.componentName;
            if (!string.IsNullOrEmpty(componentName))
            {
                if (!componentName.EndsWith(".xml"))
                {
                    componentName += ".xml";
                }
            }
            
            if (string.IsNullOrWhiteSpace(componentName) && !string.IsNullOrWhiteSpace(request.componentPath))
            {
                componentName = ExtractComponentNameFromPath(request.componentPath);
            }

            GObject panel = UIPackage.CreateObject(request.packageName, componentName);
            if (panel == null)
            {
                throw new InvalidOperationException("component not found: " + componentName);
            }

            return panel;
        }

        static string ExtractComponentNameFromPath(string componentPath)
        {
            string normalizedPath = componentPath.Trim().Replace('\\', '/');
            string fileName = Path.GetFileName(normalizedPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("invalid componentPath: " + componentPath);
            }

            return fileName;
        }

        [Serializable]
        public sealed class RenderResult
        {
            public bool ok;
            public string message;
            public string jobId;
            public string pngPath;
            public int width;
            public int height;
            public int durationMs;

            // 本次渲染实际使用的自适应参数，便于确认测试条件。
            public int screenWidth;
            public int screenHeight;
            public int logicalWidth;
            public int logicalHeight;
            public float contentScaleFactor;
            public int designResolutionX;
            public int designResolutionY;
            public string screenMatchMode;

            /// <summary>渲染器真实窗口尺寸（不参与自适应计算，仅供确认窗口没被改动）。</summary>
            public int windowWidth;
            public int windowHeight;
        }

        [Serializable]
        sealed class HealthResponse
        {
            public bool ok;
            public string message;
            public int pendingJobs;
            public bool hasActiveJob;
        }

        /// <summary>
        /// 把模拟屏幕离屏渲染到一张 captureWidth x captureHeight 的贴图上。
        /// 若输出比例与模拟屏幕比例不同，则整体缩小到完整可见（多出来的透明边随后会被裁掉），
        /// 保证不会因为比例不一致而裁掉 UI 内容。
        /// </summary>
        public static Texture2D CaptureScreen(int captureWidth, int captureHeight, ScreenSetup setup)
        {
            int width = Mathf.Max(1, captureWidth);
            int height = Mathf.Max(1, captureHeight);

            float unitsPerPixel = StageCamera.UnitsPerPixel > 0
                ? StageCamera.UnitsPerPixel
                : StageCamera.DefaultCameraSize * 2f / Mathf.Max(1, setup.screenHeight);
            float screenWorldWidth = Mathf.Max(1, setup.screenWidth) * unitsPerPixel;
            float screenWorldHeight = Mathf.Max(1, setup.screenHeight) * unitsPerPixel;
            float captureAspect = (float)width / height;

            // 完整覆盖模拟屏幕所需的最小正交半高（其中一个方向正好贴边）。
            float halfHeight = Mathf.Max(screenWorldHeight * 0.5f, screenWorldWidth * 0.5f / captureAspect);
            ConfigureStageCamera(width, height, halfHeight);

            return CaptureToTexture(width, height);
        }

        /// <summary>
        /// 兼容旧用法：不改变相机取景，按默认 1920x1080 直接截图。
        /// 编辑器里的整包导出（FguiProjectLoaderTestMenu）依赖这个行为。
        /// </summary>
        public static Texture2D CaptureScreen()
        {
            return CaptureToTexture(DefaultScreenWidth, DefaultScreenHeight);
        }

        static Texture2D CaptureToTexture(int width, int height)
        {
            Camera camera = StageCamera.main;
            if (camera == null)
            {
                throw new InvalidOperationException("capture failed: StageCamera.main is null");
            }

            RenderTexture rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previousRT = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            camera.targetTexture = rt;
            RenderTexture.active = rt;
            camera.ResetAspect();
            GL.Clear(true, true, Color.clear);
            camera.Render();

            Texture2D tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();

            camera.targetTexture = previousRT;
            camera.ResetAspect();
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(rt);

            return tex;
        }
      
        sealed class RenderJob
        {
            public string jobId;
            public RenderRequest request;
            public TaskCompletionSource<RenderResult> Completion;
        }
    }
}


