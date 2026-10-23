using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DG.Tweening;
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
                availableControllers = new List<ControllerState>(),
                appliedControllers = new List<AppliedController>(),
            };
            Exception error = null;
            string pngPath = null;
            GObject panel = null;

            RenderRequest request = job.request;
            ScreenSetup setup = ScreenSetup.Default;
            FullScreenMode appliedFullScreenMode = FullScreenMode.Auto;

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

                // 先归零再定尺寸：verticalCenter 会在定尺寸之后写 letterbox 的 y，不能被这行冲掉。
                panel.position = Vector3.zero;
                appliedFullScreenMode = PreparePanelForCapture(panel, request, setup);
                GRoot.inst.AddChild(panel);

                // 控制器在上舞台之后才切：控制器动作里的 play_transition 只有 onStage 的组件才会播。
                ApplyDisplayOverrides(panel, request.overrides);
                ApplyControllers(panel, request.controllers, result.appliedControllers);

                // 入场动效（autoPlay transition）和控制器触发的补间都按帧推进，截图时机取决于当帧预算，
                // 同一个请求会拍到不同的中间态。静态截图默认取终态：补间直接完成，正在播的过渡跳到最后一帧。
                result.transitionMode = GetTransitionModeName(request.transitionMode);
                result.settledTransitions = SettleVisualState(panel, request.transitionMode);
                result.availableControllers = CollectControllers(panel);
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
            result.fullScreen = GetFullScreenModeName(appliedFullScreenMode);
            if (panel != null)
            {
                result.panelWidth = Mathf.RoundToInt(panel.width);
                result.panelHeight = Mathf.RoundToInt(panel.height);
            }

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

            // --full-screen 不带值时解析成 "true"，即 full。
            if (args.TryGetValue("full-screen", out string fullScreenMode))
            {
                request.fullScreen = fullScreenMode;
            }

            if (args.TryGetValue("transition-mode", out string transitionMode))
            {
                request.transitionMode = transitionMode;
            }

            // --controllers "StateFilter=1;BloodMax=满级"，分号分隔，name 后不带 =page 时保持当前页。
            if (args.TryGetValue("controllers", out string controllersSpec) && !string.IsNullOrWhiteSpace(controllersSpec))
            {
                request.controllers = new List<ControllerSelection>();
                foreach (string entry in controllersSpec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int separator = entry.IndexOf('=');
                    ControllerSelection selection = new ControllerSelection
                    {
                        name = separator < 0 ? entry.Trim() : entry.Substring(0, separator).Trim(),
                        page = separator < 0 ? string.Empty : entry.Substring(separator + 1).Trim(),
                    };
                    request.controllers.Add(selection);
                }
            }

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

            if (!IsKnownFullScreenValue(request.fullScreen))
            {
                return "fullScreen must be one of: auto, none, full, verticalCenter";
            }

            if (!IsKnownTransitionMode(request.transitionMode))
            {
                return "transitionMode must be one of: settle, raw";
            }

            if (request.controllers != null)
            {
                for (int i = 0; i < request.controllers.Count; i++)
                {
                    ControllerSelection selection = request.controllers[i];
                    if (selection == null || string.IsNullOrWhiteSpace(selection.name))
                    {
                        return "controllers[" + i + "].name is required";
                    }
                }
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

        /// <summary>
        /// 面板尺寸策略。渲染器默认只把模拟屏幕用在缩放/画布上，组件本身保持包内设计尺寸；
        /// 但游戏里 `PanelManager` 会先把面板按屏幕尺寸重设（`MakeFullScreenVerticalCenter`），
        /// 而 `GObject.SetSize` 会触发子件的 relation —— 只靠设计尺寸渲染，relation 驱动的布局
        /// （尤其是 GGroup 的平移）根本不会发生，看起来就“渲染图和游戏不一致”。
        /// </summary>
        enum FullScreenMode
        {
            /// <summary>原有启发式：接近设计分辨率的页面按全屏处理。</summary>
            Auto,
            /// <summary>不动组件，保留包内设计尺寸。</summary>
            None,
            /// <summary>MakeFullScreen()：铺满 GRoot 逻辑画布。</summary>
            Full,
            /// <summary>复刻 PanelManager.MakeFullScreenVerticalCenter()。</summary>
            VerticalCenter,
        }

        static FullScreenMode PreparePanelForCapture(GObject panel, RenderRequest request, ScreenSetup setup)
        {
            FullScreenMode appliedMode = ParseFullScreenMode(request.fullScreen);

            if (panel == null)
            {
                return appliedMode;
            }

            if (appliedMode == FullScreenMode.Auto)
            {
                if (!ShouldMakeFullScreen(panel, setup.designResolutionX, setup.designResolutionY))
                {
                    appliedMode = FullScreenMode.None;
                    return appliedMode;
                }

                panel.MakeFullScreen();
                appliedMode = FullScreenMode.Full;
                return appliedMode;
            }

            if (appliedMode == FullScreenMode.Full)
            {
                panel.MakeFullScreen();
                return appliedMode;
            }

            if (appliedMode == FullScreenMode.VerticalCenter)
            {
                ApplyFullScreenVerticalCenter(panel, setup);
            }

            return appliedMode;
        }

        /// <summary>
        /// 复刻客户端 `PanelManager.MakeFullScreenVerticalCenter`：宽度铺满逻辑画布；屏幕比设计比例
        /// 更“高”（宽高比更小）时把高度收成设计比例并垂直居中，上下留黑边；最后挂三条跟随 GRoot 的关系。
        /// 关系在静态截图里不改布局，但和游戏一致 —— 后续再改窗口/画布尺寸时行为相同。
        /// </summary>
        static void ApplyFullScreenVerticalCenter(GObject panel, ScreenSetup setup)
        {
            float screenWidth = GRoot.inst.width;
            float screenHeight = GRoot.inst.height;

            float designAspect = setup.designResolutionX > 0 && setup.designResolutionY > 0
                ? (float)setup.designResolutionX / setup.designResolutionY
                : 16f / 9f;

            float targetHeight = screenHeight;
            if (screenWidth / screenHeight < designAspect)
            {
                targetHeight = screenWidth / designAspect;
            }

            panel.SetSize(screenWidth, targetHeight);
            panel.y = (screenHeight - targetHeight) / 2f;

            panel.AddRelation(GRoot.inst, RelationType.Width);
            panel.AddRelation(GRoot.inst, RelationType.Height);
            panel.AddRelation(GRoot.inst, RelationType.Center_Center);
        }

        static FullScreenMode ParseFullScreenMode(string raw)
        {
            return TryParseFullScreenMode(raw, out FullScreenMode mode) ? mode : FullScreenMode.Auto;
        }

        static bool IsKnownFullScreenValue(string raw)
        {
            return TryParseFullScreenMode(raw, out _);
        }

        static bool TryParseFullScreenMode(string raw, out FullScreenMode mode)
        {
            string value = string.IsNullOrEmpty(raw) ? string.Empty : raw.Trim().ToLowerInvariant();
            switch (value)
            {
                case "":
                case "auto":
                    mode = FullScreenMode.Auto;
                    return true;
                case "none":
                case "off":
                case "false":
                case "0":
                    mode = FullScreenMode.None;
                    return true;
                case "full":
                case "screen":
                case "true":
                case "1":
                    mode = FullScreenMode.Full;
                    return true;
                case "verticalcenter":
                case "vertical-center":
                case "verticalcentered":
                case "letterbox":
                    mode = FullScreenMode.VerticalCenter;
                    return true;
                default:
                    mode = FullScreenMode.Auto;
                    return false;
            }
        }

        static string GetFullScreenModeName(FullScreenMode mode)
        {
            switch (mode)
            {
                case FullScreenMode.None:
                    return "none";
                case FullScreenMode.Full:
                    return "full";
                case FullScreenMode.VerticalCenter:
                    return "verticalCenter";
                default:
                    return "auto";
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

            /// <summary>
            /// 面板尺寸策略，决定“模拟屏幕”要不要作用到被渲染的组件上（游戏里由 PanelManager 干这事）：
            /// auto = 保持原有启发式（接近设计分辨率的页面按全屏处理）；
            /// none = 不动组件，渲染包内设计尺寸（关系/自适应完全不触发）；
            /// full = MakeFullScreen()，宽高铺满 GRoot 逻辑画布；
            /// verticalCenter = 复刻 PanelManager.MakeFullScreenVerticalCenter()（窄屏竖向 letterbox 居中 + 跟随 GRoot 的关系）。
            /// 空 = auto。
            /// </summary>
            public string fullScreen;

            public int timeoutSec = 120;
            public List<DisplayOverride> overrides;

            /// <summary>
            /// 渲染前要切换的控制器。path 为空 = 面板根组件，否则按名字逐级找子组件；
            /// page 填页面索引或页面名，空 = 保持当前页。
            /// </summary>
            public List<ControllerSelection> controllers;

            /// <summary>
            /// 过渡处理方式：
            /// settle（默认）= 正在播的过渡跳到终态、动画补间直接完成；
            /// raw = 不干预，截到第几帧就是第几帧。
            /// </summary>
            public string transitionMode;
        }

        /// <summary>
        /// 一次控制器切换：把组件上某个控制器切到指定页面。
        /// </summary>
        [Serializable]
        public sealed class ControllerSelection
        {
            /// <summary>从面板根组件开始的child名字链；空 = 面板根本身。</summary>
            public List<string> path;
            /// <summary>控制器名（组件上 &lt;controller name="..."&gt;）。</summary>
            public string name;
            /// <summary>页面索引（"1"）或页面名（"可升星"）；空 = 保持当前页。</summary>
            public string page;
        }

        /// <summary>
        /// 结果里回显的控制器切换记录：切换后的真实状态，便于确认请求是否按预期生效。
        /// </summary>
        [Serializable]
        public sealed class AppliedController
        {
            public List<string> path;
            public string name;
            public int pageCount;
            public int selectedIndex;
            public string selectedPage;
        }

        /// <summary>
        /// 面板根组件上的控制器清单，供调用方在不读 XML 的情况下逐个换页试样式。
        /// </summary>
        [Serializable]
        public sealed class ControllerState
        {
            public string name;
            public int selectedIndex;
            public string selectedPage;
            public List<string> pages;
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
        /// <summary>
        /// 按请求切换控制器。名字或页面不存在时整体失败并报出具体名字，
        /// 避免拼错一个控制器名却静默渲染出没切换的画面。
        /// </summary>
        static void ApplyControllers(GObject panel, List<ControllerSelection> selections, List<AppliedController> applied)
        {
            if (panel == null || selections == null || selections.Count == 0)
            {
                return;
            }

            foreach (ControllerSelection selection in selections)
            {
                if (selection == null || string.IsNullOrWhiteSpace(selection.name))
                {
                    continue;
                }

                string name = selection.name.Trim();
                List<string> path = selection.path ?? new List<string>();
                GComponent target = ResolveComponent(panel, path);
                if (target == null)
                {
                    throw new InvalidOperationException("controller target not found: " + FormatControllerPath(path));
                }

                Controller controller = target.GetController(name);
                if (controller == null)
                {
                    throw new InvalidOperationException("controller not found: " + name + " on " + FormatControllerPath(path));
                }

                int page = ResolveControllerPage(controller, selection.page, name);
                if (page >= 0)
                {
                    controller.selectedIndex = page;
                    SyncSelectionControllers(target, controller, page);
                }

                applied.Add(new AppliedController
                {
                    path = path,
                    name = name,
                    pageCount = controller.pageCount,
                    selectedIndex = controller.selectedIndex,
                    selectedPage = controller.selectedPage,
                });
            }
        }

        /// <summary>
        /// 把绑定了该控制器的列表一起切到同一页。GList 的 selectionController 只在列表 -> 控制器方向同步
        /// （ApplyController 期间控制器处于 changing，列表会跳过反向同步），游戏里点的是页签本身，
        /// 所以程序化换页时页签高亮默认不会跟；这里补一次控制器 -> 列表，让页签和内容对得上。
        /// </summary>
        static void SyncSelectionControllers(GComponent owner, Controller controller, int page)
        {
            if (owner == null)
            {
                return;
            }

            for (int i = 0; i < owner.numChildren; i++)
            {
                GList list = owner.GetChildAt(i) as GList;
                if (list == null || list.selectionController != controller)
                {
                    continue;
                }

                if (page < list.numItems)
                {
                    list.selectedIndex = page;
                }
            }
        }

        static GComponent ResolveComponent(GObject root, List<string> path)
        {
            GComponent component = root as GComponent;
            for (int i = 0; component != null && i < path.Count; i++)
            {
                component = component.GetChild(path[i]) as GComponent;
            }

            return component;
        }

        /// <summary>
        /// 解析 page 参数：空 = 保持当前页（返回 -1）；纯数字 = 页面索引；其余按页面名匹配。
        /// </summary>
        static int ResolveControllerPage(Controller controller, string page, string controllerName)
        {
            if (string.IsNullOrWhiteSpace(page))
            {
                return -1;
            }

            string value = page.Trim();
            if (int.TryParse(value, out int index))
            {
                if (index >= 0 && index < controller.pageCount)
                {
                    return index;
                }

                throw new InvalidOperationException(string.Format(
                    "controller page index out of range: {0} page={1} pageCount={2}",
                    controllerName, value, controller.pageCount));
            }

            for (int i = 0; i < controller.pageCount; i++)
            {
                if (string.Equals(controller.GetPageName(i), value, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            throw new InvalidOperationException("controller page name not found: " + controllerName + " page=" + value);
        }

        static string FormatControllerPath(List<string> path)
        {
            return path == null || path.Count == 0 ? "<root>" : string.Join("/", path.ToArray());
        }

        static List<ControllerState> CollectControllers(GObject panel)
        {
            List<ControllerState> states = new List<ControllerState>();
            GComponent component = panel as GComponent;
            if (component == null)
            {
                return states;
            }

            List<Controller> controllers = component.Controllers;
            for (int i = 0; i < controllers.Count; i++)
            {
                Controller controller = controllers[i];
                ControllerState state = new ControllerState
                {
                    name = controller.name,
                    selectedIndex = controller.selectedIndex,
                    selectedPage = controller.selectedPage,
                    pages = new List<string>(),
                };

                for (int page = 0; page < controller.pageCount; page++)
                {
                    state.pages.Add(controller.GetPageName(page));
                }

                states.Add(state);
            }

            return states;
        }

        /// <summary>
        /// 把画面归位到各段动效的终值：正在播的过渡（含 autoPlay 入场动效）跳到最后一帧，
        /// 再把控制器切换留下的 gear 补间直接完成。这样截图不会停在入场动画的中间态。
        /// 顺序不能反：过渡里的延时项也是 DOTween 的 DelayedCall，先 CompleteAll 会把它们提前触发成新的
        /// 淡入补间，过渡自身反而被判定成播完，随后的 Stop 不再改任何值，画面就停在新补间的起始值上。
        /// 组件里自播的 movieclip 动画（循环光效等）不在这里处理，仍按帧推进。
        /// </summary>
        static int SettleVisualState(GObject panel, string transitionMode)
        {
            if (!IsTransitionSettleEnabled(transitionMode))
            {
                return 0;
            }

            int settled = SettleTransitions(panel);
            DOTween.CompleteAll();
            return settled;
        }

        static int SettleTransitions(GObject obj)
        {
            GComponent component = obj as GComponent;
            if (component == null)
            {
                return 0;
            }

            int settled = 0;
            for (int i = 0; i < component.numTransitions; i++)
            {
                Transition transition = component.GetTransitionAt(i);
                if (transition.playing)
                {
                    transition.Stop(true, false);
                    settled += 1;
                }
            }

            for (int i = 0; i < component.numChildren; i++)
            {
                settled += SettleTransitions(component.GetChildAt(i));
            }

            return settled;
        }

        static bool IsTransitionSettleEnabled(string raw)
        {
            return !string.Equals(GetTransitionModeName(raw), "raw", StringComparison.Ordinal);
        }

        static string GetTransitionModeName(string raw)
        {
            string value = string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim().ToLowerInvariant();
            switch (value)
            {
                case "raw":
                case "none":
                case "off":
                case "false":
                case "0":
                    return "raw";
                default:
                    return "settle";
            }
        }

        static bool IsKnownTransitionMode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "settle":
                case "end":
                case "complete":
                case "raw":
                case "none":
                case "off":
                case "false":
                case "0":
                    return true;
                default:
                    return false;
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

            /// <summary>本次实际应用的面板尺寸策略（auto / none / full / verticalCenter）。</summary>
            public string fullScreen;

            /// <summary>策略应用后的面板逻辑尺寸，便于核对自适应/关系是否按预期生效。</summary>
            public int panelWidth;
            public int panelHeight;

            /// <summary>面板根组件上的控制器清单（切换之后的状态）。</summary>
            public List<ControllerState> availableControllers;

            /// <summary>本次请求实际切换过的控制器，含切换后的页面索引与页面名。</summary>
            public List<AppliedController> appliedControllers;

            /// <summary>本次生效的过渡处理方式（settle / raw）。</summary>
            public string transitionMode;

            /// <summary>本次归位到终态的过渡数量；raw 模式下恒为 0。</summary>
            public int settledTransitions;
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


