using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FairyGUI;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace FguiRenderServer
{
    public sealed class FguiRenderBootstrap : MonoBehaviour
    {
        private const string ResultPrefix = "[FGUI_RENDER_RESULT]";
        private FguiRenderRequest _request;
        private Action<FguiRenderResult> _externalOnComplete;
        private bool _quitAfterRender;
        private bool _isRendering;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStartInPlayer()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (!CommandLineArgs.TryGetFlag(args, "--render-once"))
            {
                return;
            }

            GameObject host = new GameObject("FguiRenderBootstrap");
            DontDestroyOnLoad(host);
            FguiRenderBootstrap bootstrap = host.AddComponent<FguiRenderBootstrap>();
            bootstrap.TryStartRender(CommandLineArgs.ParseRequest(args), null, true);
        }

        public static bool TryRenderInPlayMode(FguiRenderRequest request, Action<FguiRenderResult> onDone)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            FguiRenderBootstrap bootstrap = GetOrCreateBootstrap();
            Application.runInBackground = true;
            return bootstrap.TryStartRender(request, onDone, false);
        }

        /// <summary>
        /// Loads all published packages from <paramref name="allPackagesDir"/> once, then renders
        /// every request in <paramref name="requests"/> sequentially, writes PNG files, and
        /// invokes <paramref name="onDone"/> with the full result list when finished.
        /// </summary>
        public static bool TryBatchRenderInPlayMode(
            string allPackagesDir,
            List<FguiRenderRequest> requests,
            Action<List<FguiRenderResult>> onDone)
        {
            if (requests == null || requests.Count == 0)
                throw new ArgumentException("requests must not be null or empty", "requests");

            FguiRenderBootstrap bootstrap = GetOrCreateBootstrap();
            if (bootstrap._isRendering)
                return false;

            Application.runInBackground = true;
            bootstrap.StartCoroutine(bootstrap.RunBatchRender(allPackagesDir, requests, onDone));
            return true;
        }

        private static FguiRenderBootstrap GetOrCreateBootstrap()
        {
            GameObject host = GameObject.Find("FguiRenderBootstrap");
            if (host == null)
            {
                host = new GameObject("FguiRenderBootstrap");
                DontDestroyOnLoad(host);
            }

            FguiRenderBootstrap bootstrap = host.GetComponent<FguiRenderBootstrap>();
            if (bootstrap == null)
                bootstrap = host.AddComponent<FguiRenderBootstrap>();
            return bootstrap;
        }

        private void Start()
        {
            TryKickoffPendingRender();
        }

        private bool TryStartRender(FguiRenderRequest request, Action<FguiRenderResult> onDone, bool quitAfterRender)
        {
            if (_isRendering || _request != null)
            {
                return false;
            }

            _request = request;
            _externalOnComplete = onDone;
            _quitAfterRender = quitAfterRender;
            TryKickoffPendingRender();
            return true;
        }

        private void TryKickoffPendingRender()
        {
            if (_isRendering || _request == null || !isActiveAndEnabled)
            {
                return;
            }

            FguiRenderRequest pendingRequest = _request;
            _request = null;
            StartCoroutine(RunRender(pendingRequest));
        }

        private IEnumerator RunRender(FguiRenderRequest request)
        {
            _isRendering = true;
            Application.runInBackground = true;
            yield return StartCoroutine(RenderOnce(request));
            _isRendering = false;
            _quitAfterRender = false;
        }

        /// <summary>
        /// Batch coroutine: loads all packages once, renders every request, then cleans up.
        /// </summary>
        private IEnumerator RunBatchRender(
            string allPackagesDir,
            List<FguiRenderRequest> requests,
            Action<List<FguiRenderResult>> onDone)
        {
            _isRendering = true;
            Application.runInBackground = true;

            var results = new List<FguiRenderResult>(requests.Count);

            yield return null;
            yield return new WaitForEndOfFrame();

            // ── 1. Load all packages once ──────────────────────────────────────────
            try
            {
                LoadAllPublishedPackages(allPackagesDir);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _isRendering = false;
                onDone?.Invoke(results);
                yield break;
            }

            // ── 2. Render each component (packages stay loaded) ───────────────────
            for (int i = 0; i < requests.Count; i++)
            {
                FguiRenderRequest req = requests[i];
                Stopwatch sw = Stopwatch.StartNew();
                FguiRenderResult result = new FguiRenderResult();
                GComponent rootChild = null;

                yield return new WaitForEndOfFrame();

                try
                {
                    GObject created = UIPackage.CreateObject(req.packageName, req.componentName);
                    rootChild = created?.asCom;
                    if (rootChild == null)
                        throw new InvalidOperationException(
                            $"'{req.packageName}/{req.componentName}' is not a GComponent or CreateObject returned null.");

                    rootChild.SetPosition(0, 0, 0);
                    Stage.Instantiate();
                    GRoot root = GRoot.inst;
                    root.RemoveChildren(0, -1, true);
                    root.SetContentScaleFactor((int)rootChild.width, (int)rootChild.height);
                    root.AddChild(rootChild);

                    Texture2D capture = rootChild.displayObject.GetScreenShot(null, 1f);
                    if (capture == null)
                        throw new InvalidOperationException("Screenshot texture is null.");

                    string outputPath = Path.GetFullPath(req.outPng);
                    string outputDir  = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outputDir))
                        Directory.CreateDirectory(outputDir);

                    File.WriteAllBytes(outputPath, capture.EncodeToPNG());
                    Destroy(capture);

                    sw.Stop();
                    result.ok        = true;
                    result.message   = "ok";
                    result.pngPath   = outputPath;
                    result.durationMs = sw.ElapsedMilliseconds;
                    Debug.Log($"[FguiRenderBootstrap] [{i + 1}/{requests.Count}] ✓ " +
                              $"'{req.packageName}/{req.componentName}' → {outputPath} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    result.ok        = false;
                    result.message   = ex.Message;
                    result.durationMs = sw.ElapsedMilliseconds;
                    Debug.LogError($"[FguiRenderBootstrap] [{i + 1}/{requests.Count}] ✗ " +
                                   $"'{req.packageName}/{req.componentName}': {ex.Message}");
                }
                finally
                {
                    if (rootChild != null)
                    {
                        rootChild.RemoveFromParent();
                        rootChild.Dispose();
                    }
                }

                results.Add(result);
            }

            // ── 3. Cleanup ─────────────────────────────────────────────────────────
            UIPackage.RemoveAllPackages();
            _isRendering = false;

            onDone?.Invoke(results);
        }

        private void OnComplete(FguiRenderResult result)
        {
            string json = JsonUtility.ToJson(result);
            Debug.Log(ResultPrefix + json);

            Action<FguiRenderResult> onComplete = _externalOnComplete;
            _externalOnComplete = null;
            if (onComplete != null)
            {
                onComplete(result);
            }

#if !UNITY_EDITOR
            if (_quitAfterRender)
            {
                Application.Quit();
            }
#endif
        }

        private void LoadAllPackage(FguiRenderRequest request)
        {
            Stopwatch sw = Stopwatch.StartNew();
            FguiRenderResult result = new FguiRenderResult();
            // ─────────────────────────────────────────────────────────────────────────────
            try
            {
                ValidateRequest(request);

                if (!request.transparent && StageCamera.main != null)
                {
                    StageCamera.main.clearFlags = CameraClearFlags.SolidColor;
                    StageCamera.main.backgroundColor = Color.white;
                }

                LoadAllPublishedPackages(request.allPackageRootDir);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                sw.Stop();
                result.ok = false;
                result.message = ex.Message;
                result.durationMs = sw.ElapsedMilliseconds;

                _externalOnComplete?.Invoke(result);
            }
        }

        private IEnumerator RenderOnce(FguiRenderRequest request)
        {
            Stopwatch sw = Stopwatch.StartNew();
            FguiRenderResult result = new FguiRenderResult();
            GComponent rootChild = null;

            yield return null;
            yield return new WaitForEndOfFrame();

            try
            {
                // Ensure all published packages are loaded before creating objects.
                if (!string.IsNullOrWhiteSpace(request.allPackageRootDir))
                    LoadAllPublishedPackages(request.allPackageRootDir);

                if (!request.transparent && StageCamera.main != null)
                {
                    StageCamera.main.clearFlags = CameraClearFlags.SolidColor;
                    StageCamera.main.backgroundColor = Color.white;
                }

                GObject created = UIPackage.CreateObject(request.packageName, request.componentName);

                rootChild = created.asCom;
                if (rootChild == null)
                {
                    throw new InvalidOperationException("Target component is not a GComponent.");
                }

                rootChild.SetPosition(0, 0, 0);
                
                Stage.Instantiate();
                GRoot root = GRoot.inst;
                root.RemoveChildren(0, -1, true);
                
                root.SetContentScaleFactor((int)rootChild.width, (int)rootChild.height);
                
                root.AddChild(rootChild);
                
                Texture2D capture = rootChild.displayObject.GetScreenShot(null, 1f);
                if (capture == null)
                {
                    throw new InvalidOperationException("Capture failed because screenshot texture is null.");
                }

                string outputPath = Path.GetFullPath(request.outPng);
                string outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                File.WriteAllBytes(outputPath, capture.EncodeToPNG());
                Destroy(capture);

                sw.Stop();
                result.ok = true;
                result.message = "ok";
                result.pngPath = outputPath;
                result.durationMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ok = false;
                result.message = ex.Message;
                result.durationMs = sw.ElapsedMilliseconds;
            }
            finally
            {
                CleanupLoadedObjects(rootChild);
            }

            _externalOnComplete?.Invoke(result);
        }

        private static void TryDeleteTempDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* best-effort */ }
        }


        private static void LoadAllPublishedPackages(string packageDir)
        {
            string[] descriptorPaths = Directory.GetFiles(packageDir, "*_fui.bytes", SearchOption.AllDirectories);
            foreach (string descriptorPath in descriptorPaths)
            {
                string packageName = Path.GetFileNameWithoutExtension(descriptorPath);
                if (packageName.EndsWith("_fui", StringComparison.OrdinalIgnoreCase))
                {
                    packageName = packageName.Substring(0, packageName.Length - 4);
                }

                if (UIPackage.GetByName(packageName) != null)
                {
                    continue;
                }

                byte[] descData = File.ReadAllBytes(descriptorPath);
                var packagePath = Directory.GetParent(descriptorPath).FullName;
                FguiPackageFileLoader loader = new FguiPackageFileLoader(packagePath);
                UIPackage loaded = UIPackage.AddPackage(descData, packageName, loader.Load);
                if (loaded == null)
                {
                    throw new InvalidOperationException(
                        string.Format("Failed to load published dependency package '{0}'.", packageName));
                }
            }
        }

        private static void CleanupLoadedObjects(GComponent rootChild)
        {
            if (rootChild != null)
            {
                rootChild.RemoveFromParent();
                rootChild.Dispose();
            }

            UIPackage.RemoveAllPackages();
        }

        private static void ValidateRequest(FguiRenderRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            bool hasSourceDir = !string.IsNullOrWhiteSpace(request.packageSourceDir);
            bool hasPackageDir = !string.IsNullOrWhiteSpace(request.allPackageRootDir);

            if (!hasSourceDir && !hasPackageDir)
            {
                throw new ArgumentException("Either --package-source-dir or --package-dir is required.");
            }

            if (hasSourceDir && !Directory.Exists(request.packageSourceDir))
            {
                throw new DirectoryNotFoundException("packageSourceDir does not exist: " + request.packageSourceDir);
            }

            if (!hasSourceDir)
            {
                if (!Directory.Exists(request.allPackageRootDir))
                {
                    throw new DirectoryNotFoundException("packageDir does not exist: " + request.allPackageRootDir);
                }

                if (string.IsNullOrWhiteSpace(request.packageName))
                {
                    throw new ArgumentException("--package-name is required when --package-source-dir is not used.");
                }
            }

            if (string.IsNullOrWhiteSpace(request.componentName))
            {
                throw new ArgumentException("--component-name is required.");
            }

            if (string.IsNullOrWhiteSpace(request.outPng))
            {
                throw new ArgumentException("--out-png is required.");
            }
        }

        private static string ResolveBinaryDescriptorPath(string packageDir, string packageName)
        {
            string binaryPath = Path.Combine(packageDir, packageName + "_fui.bytes");
            if (File.Exists(binaryPath))
            {
                return binaryPath;
            }

            throw new InvalidOperationException(
                "Runtime rendering requires FairyGUI binary descriptor '_fui.bytes'. " +
                "Please publish the package with the official FairyGUI runtime format before rendering.");
        }

        private static bool HasFguiBinaryHeader(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    if (fs.Length < 4)
                    {
                        return false;
                    }

                    int b0 = fs.ReadByte();
                    int b1 = fs.ReadByte();
                    int b2 = fs.ReadByte();
                    int b3 = fs.ReadByte();
                    return b0 == 0x46 && b1 == 0x47 && b2 == 0x55 && b3 == 0x49; // "FGUI"
                }
            }
            catch
            {
                return false;
            }
        }
    }
}