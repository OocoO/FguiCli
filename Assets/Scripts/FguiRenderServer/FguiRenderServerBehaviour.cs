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
        const int DefaultWindowWidth = 1280;
        const int DefaultWindowHeight = 720;

        // Keep capture defaults at 1080p while runtime window can stay smaller.
        const int DefaultRenderWidth = 1920;
        const int DefaultRenderHeight = 1080;

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

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Stage.Instantiate();
            GRoot.inst.SetContentScaleFactor(1920, 1080);

            Dictionary<string, string> args = ParseCommandLineArguments(Environment.GetCommandLineArgs());
            _oneShotMode = args.ContainsKey("render-once");

            // Server mode defaults: run in background, windowed, and start with a small window.
            Application.runInBackground = true;
            Screen.fullScreenMode = FullScreenMode.Windowed;
            Screen.SetResolution(DefaultWindowWidth, DefaultWindowHeight, FullScreenMode.Windowed);

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
            RenderJob job = new RenderJob
            {
                jobId = Guid.NewGuid().ToString("N"),
                request = request,
                Completion = new TaskCompletionSource<RenderResult>(),
            };

            lock (_pendingLock)
            {
                _pendingJobs.Enqueue(job);
            }

            return job;
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
            int captureWidth = request.width > 0 ? request.width : DefaultRenderWidth;
            int captureHeight = request.height > 0 ? request.height : DefaultRenderHeight;
            try
            {
                UIPackage.RemoveAllPackages(true);
                GRoot.inst.RemoveChildren(0, -1, true);

                FguiProjectLoader loader = FguiProjectLoader.LoadProject(request.projectRootDir, request.branchTag);
                UIPackage package = loader.GetPackage(request.packageName);
                if (package == null)
                {
                    throw new InvalidOperationException("package not found: " + request.packageName);
                }

                panel = CreatePanelFromRequest(request);

                PreparePanelForCapture(panel);
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
                yield return new WaitForEndOfFrame();

                try
                {
                    RectInt cropRect = CalculateCaptureRect(panel, captureWidth, captureHeight);
                    Texture2D screenshot = CaptureUiTexture(captureWidth, captureHeight);
                    if (screenshot == null)
                    {
                        throw new InvalidOperationException("capture failed: screenshot texture is null");
                    }

                    Texture2D outputTexture = CropTexture(screenshot, cropRect);

                    pngPath = Path.GetFullPath(request.outPng);
                    string pngDirectory = Path.GetDirectoryName(pngPath);
                    if (!string.IsNullOrEmpty(pngDirectory))
                    {
                        Directory.CreateDirectory(pngDirectory);
                    }

                    byte[] pngBytes = outputTexture.EncodeToPNG();
                    result.width = outputTexture.width;
                    result.height = outputTexture.height;

                    if (!ReferenceEquals(outputTexture, screenshot))
                    {
                        Destroy(outputTexture);
                    }

                    Destroy(screenshot);
                    File.WriteAllBytes(pngPath, pngBytes);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            }

            stopwatch.Stop();
            result.durationMs = (int)stopwatch.ElapsedMilliseconds;

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
            _activeJob = null;

            if (_oneShotMode)
            {
                UnityEngine.Debug.Log(ResultPrefix + JsonUtility.ToJson(result));
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

            if (!TryReadInt(args, "width", out request.width))
            {
                request.width = DefaultRenderWidth;
            }

            if (!TryReadInt(args, "height", out request.height))
            {
                request.height = DefaultRenderHeight;
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

        static void PreparePanelForCapture(GObject panel)
        {
            if (panel == null)
            {
                return;
            }

            if (ShouldMakeFullScreen(panel))
            {
                panel.MakeFullScreen();
            }
        }

        static bool ShouldMakeFullScreen(GObject panel)
        {
            if (panel == null)
            {
                return false;
            }

            float panelWidth = panel.initWidth > 0 ? panel.initWidth : panel.width;
            float panelHeight = panel.initHeight > 0 ? panel.initHeight : panel.height;
            float rootWidth = GRoot.inst.width;
            float rootHeight = GRoot.inst.height;

            if (panelWidth <= 0 || panelHeight <= 0 || rootWidth <= 0 || rootHeight <= 0)
            {
                return false;
            }

            float widthRatio = panelWidth / rootWidth;
            float heightRatio = panelHeight / rootHeight;
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

        [Serializable]
        sealed class RenderRequest
        {
            public string projectRootDir;
            public string packageName;
            public string componentName;
            public string componentPath;
            public string componentId;
            public string outPng;
            public string branchTag;
            public int width = DefaultRenderWidth;
            public int height = DefaultRenderHeight;
            public int timeoutSec = 120;
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
        sealed class RenderResult
        {
            public bool ok;
            public string message;
            public string jobId;
            public string pngPath;
            public int width;
            public int height;
            public int durationMs;
        }

        [Serializable]
        sealed class HealthResponse
        {
            public bool ok;
            public string message;
            public int pendingJobs;
            public bool hasActiveJob;
        }

        static Texture2D CaptureUiTexture(int width, int height)
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
            GL.Clear(true, true, Color.clear);
            camera.Render();
            camera.targetTexture = previousRT;

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
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


