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
            Application.runInBackground = true;
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

            var results = new List<FguiRenderResult>(requests.Count);

            yield return null;

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
            
            yield return null;

            // ── 2. Render each component (packages stay loaded) ───────────────────
            for (int i = 0; i < requests.Count; i++)
            {
                FguiRenderRequest req = requests[i];
                Stopwatch sw = Stopwatch.StartNew();
                FguiRenderResult result = new FguiRenderResult();

                yield return null;
                GComponent rootChild = null;
                try
                {
                    GObject created = UIPackage.CreateObject(req.packageName, req.componentName);
                    rootChild = created?.asCom;
                    if (rootChild == null)
                        throw new InvalidOperationException(
                            $"'{req.packageName}/{req.componentName}' is not a GComponent or CreateObject returned null.");
                }
                catch (Exception e)
                {
                    Debug.LogError(e);
                    continue;
                }
                
                Stage.Instantiate();
                GRoot root = GRoot.inst;
                root.RemoveChildren(0, -1, true);
                root.SetContentScaleFactor((int)rootChild.width, (int)rootChild.height);

                rootChild.SetPosition(0, 0, 0);
                root.AddChild(rootChild);
                
                yield return null;

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
                    
                rootChild.RemoveFromParent();
                rootChild.Dispose();

                results.Add(result);
            }

            // ── 3. Cleanup ─────────────────────────────────────────────────────────
            _isRendering = false;

            onDone?.Invoke(results);
        }

        private IEnumerator RenderOnce(FguiRenderRequest request)
        {
            Stopwatch sw = Stopwatch.StartNew();
            FguiRenderResult result = new FguiRenderResult();
            GComponent rootChild = null;

            yield return null;

            // Ensure all published packages are loaded before creating objects.
            if (!string.IsNullOrWhiteSpace(request.allPackageRootDir))
                LoadAllPublishedPackages(request.allPackageRootDir);

            if (!request.transparent && StageCamera.main != null)
            {
                StageCamera.main.clearFlags = CameraClearFlags.SolidColor;
                StageCamera.main.backgroundColor = Color.white;
            }

            try
            {
                GObject created = UIPackage.CreateObject(request.packageName, request.componentName);

                rootChild = created.asCom;
                if (rootChild == null)
                {
                    throw new InvalidOperationException("Target component is not a GComponent.");
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                sw.Stop();
                result.ok = false;
                result.message = e.Message;
                result.durationMs = sw.ElapsedMilliseconds;

                if (rootChild != null)
                {
                    rootChild.RemoveFromParent();
                    rootChild.Dispose();
                }
                
                _externalOnComplete?.Invoke(result);
                yield break;
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

            rootChild.RemoveFromParent();
            rootChild.Dispose();

            _externalOnComplete?.Invoke(result);
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

                Debug.Log($"Load Package -- {packageName}");
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
    }
}