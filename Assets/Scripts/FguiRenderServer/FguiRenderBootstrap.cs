using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FairyGUI;
using UnityEngine;
using Debug = System.Diagnostics.Debug;

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

            GameObject host = GameObject.Find("FguiRenderBootstrap");
            if (host == null)
            {
                host = new GameObject("FguiRenderBootstrap");
                DontDestroyOnLoad(host);
            }

            FguiRenderBootstrap bootstrap = host.GetComponent<FguiRenderBootstrap>();
            if (bootstrap == null)
            {
                bootstrap = host.AddComponent<FguiRenderBootstrap>();
            }

            Application.runInBackground = true;
            return bootstrap.TryStartRender(request, onDone, false);
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
            yield return StartCoroutine(RenderOnce(request, OnComplete));
            _isRendering = false;
            _quitAfterRender = false;
        }

        private void OnComplete(FguiRenderResult result)
        {
            string json = JsonUtility.ToJson(result);
            UnityEngine.Debug.Log(ResultPrefix + json);

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

        private IEnumerator RenderOnce(FguiRenderRequest request, Action<FguiRenderResult> onDone)
        {
            Stopwatch sw = Stopwatch.StartNew();
            FguiRenderResult result = new FguiRenderResult();
            string requestedOutPng = request == null ? string.Empty : (request.outPng ?? string.Empty);
            UIPackage loadedPackage = null;
            List<UIPackage> loadedPackages = new List<UIPackage>();
            GComponent rootChild = null;
            string tempPublishDir = null;

            // ── Pre-step: if a source dir is given, publish to a temp dir first ──────────
            if (request != null && !string.IsNullOrWhiteSpace(request.packageSourceDir))
            {
                try
                {
                    tempPublishDir = Path.Combine(
                        Path.GetTempPath(),
                        "fgui_render_" + Guid.NewGuid().ToString("N"));

                    string pkgName = FguiPackagePublisher.GetPackageName(request.packageSourceDir);
                    request.allPackageRootDir  = tempPublishDir;
                    request.packageName = pkgName;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                    sw.Stop();
                    result.ok        = false;
                    result.message   = "Publish step failed: " + ex.Message;
                    result.pngPath   = requestedOutPng;
                    result.durationMs = sw.ElapsedMilliseconds;
                    TryDeleteTempDir(tempPublishDir);
                    onDone(result);
                    yield break;
                }
            }
            // ─────────────────────────────────────────────────────────────────────────────

            try
            {
                FguiRenderRequest validRequest = request;
                if (validRequest == null)
                {
                    throw new ArgumentNullException("request");
                }

                ValidateRequest(validRequest);

                Stage.Instantiate();
                GRoot root = GRoot.inst;
                root.RemoveChildren(0, -1, true);
                root.SetContentScaleFactor(Mathf.Max(1, validRequest.width), Mathf.Max(1, validRequest.height));

                if (!validRequest.transparent && StageCamera.main != null)
                {
                    StageCamera.main.clearFlags = CameraClearFlags.SolidColor;
                    StageCamera.main.backgroundColor = Color.white;
                }

                LoadAllPublishedPackages(validRequest.allPackageRootDir, loadedPackages);
                loadedPackage = UIPackage.GetByName(validRequest.packageName);
                if (loadedPackage == null)
                {
                    throw new InvalidOperationException(
                        string.Format("Package '{0}' was not found after loading published packages from '{1}'.", validRequest.packageName, validRequest.allPackageRootDir));
                }

                GObject created = UIPackage.CreateObject(loadedPackage.name, validRequest.componentName);
                if (created == null)
                {
                    throw new InvalidOperationException(
                        string.Format("Component '{0}' was not found in package '{1}'", validRequest.componentName, loadedPackage.name));
                }

                rootChild = created.asCom;
                if (rootChild == null)
                {
                    throw new InvalidOperationException("Target component is not a GComponent.");
                }

                rootChild.SetPosition(0, 0, 0);
                if (validRequest.width > 0 && validRequest.height > 0)
                {
                    rootChild.SetSize(validRequest.width, validRequest.height);
                }

                root.AddChild(rootChild);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
                sw.Stop();
                result.ok = false;
                result.message = ex.Message;
                result.pngPath = requestedOutPng;
                result.durationMs = sw.ElapsedMilliseconds;

                CleanupLoadedObjects(rootChild, loadedPackages);
                onDone(result);
                yield break;
            }

            yield return null;
            yield return new WaitForEndOfFrame();

            try
            {
                FguiRenderRequest validRequest = request;
                if (validRequest == null)
                {
                    throw new ArgumentNullException("request");
                }

                Texture2D capture = rootChild.displayObject.GetScreenShot(null, Mathf.Max(0.01f, validRequest.scale));
                if (capture == null)
                {
                    throw new InvalidOperationException("Capture failed because screenshot texture is null.");
                }

                string outputPath = Path.GetFullPath(validRequest.outPng);
                string outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                int outputWidth = capture.width;
                int outputHeight = capture.height;
                File.WriteAllBytes(outputPath, capture.EncodeToPNG());
                UnityEngine.Object.Destroy(capture);

                sw.Stop();
                result.ok = true;
                result.message = "ok";
                result.pngPath = outputPath;
                result.width = outputWidth;
                result.height = outputHeight;
                result.durationMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ok = false;
                result.message = ex.Message;
                result.pngPath = requestedOutPng;
                result.durationMs = sw.ElapsedMilliseconds;
            }
            finally
            {
                CleanupLoadedObjects(rootChild, loadedPackages);
            }

            onDone(result);
            TryDeleteTempDir(tempPublishDir);
        }

        private static void TryDeleteTempDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* best-effort */ }
        }


        private static void LoadAllPublishedPackages(
            string packageDir,
            List<UIPackage> loadedPackages)
        {
            string[] descriptorPaths = Directory.GetFiles(packageDir, "*_fui.bytes", SearchOption.TopDirectoryOnly);
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

                loadedPackages.Add(loaded);
            }
        }

        private static void CleanupLoadedObjects(GComponent rootChild, List<UIPackage> loadedPackages)
        {
            if (rootChild != null)
            {
                rootChild.RemoveFromParent();
                rootChild.Dispose();
            }

            if (loadedPackages != null)
            {
                for (int i = loadedPackages.Count - 1; i >= 0; i--)
                {
                    UIPackage pkg = loadedPackages[i];
                    if (pkg == null)
                    {
                        continue;
                    }

                    if (UIPackage.GetById(pkg.id) != null)
                    {
                        UIPackage.RemovePackage(pkg.id);
                    }
                }
            }
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