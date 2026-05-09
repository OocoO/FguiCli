using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using FairyGUI;
using UnityEngine;

namespace FguiRenderServer
{
    [Serializable]
    public class FguiRenderRequest
    {
        public string packageDir;
        public string packageName;
        public string componentName;
        public string outPng;
        public int width = 1920;
        public int height = 1080;
        public float scale = 1f;
        public bool transparent = true;
    }

    [Serializable]
    public class FguiRenderResult
    {
        public bool ok;
        public string message;
        public string pngPath;
        public int width;
        public int height;
        public long durationMs;
    }

    public sealed class FguiRenderBootstrap : MonoBehaviour
    {
        private const string ResultPrefix = "[FGUI_RENDER_RESULT]";
        private FguiRenderRequest _request;

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
            bootstrap._request = CommandLineArgs.ParseRequest(args);
        }

        private IEnumerator Start()
        {
            if (_request == null)
            {
                yield break;
            }

            Application.runInBackground = true;
            yield return StartCoroutine(RenderOnce(_request, OnComplete));
        }

        private void OnComplete(FguiRenderResult result)
        {
            string json = JsonUtility.ToJson(result);
            UnityEngine.Debug.Log(ResultPrefix + json);

#if !UNITY_EDITOR
            Application.Quit();
#endif
        }

        private IEnumerator RenderOnce(FguiRenderRequest request, Action<FguiRenderResult> onDone)
        {
            Stopwatch sw = Stopwatch.StartNew();
            FguiRenderResult result = new FguiRenderResult();
            string requestedOutPng = request == null ? string.Empty : (request.outPng ?? string.Empty);
            UIPackage loadedPackage = null;
            GComponent rootChild = null;

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

                string descPath = Path.Combine(validRequest.packageDir, validRequest.packageName + "_fui.bytes");
                if (!File.Exists(descPath))
                {
                    throw new FileNotFoundException("Cannot find package description file", descPath);
                }

                byte[] descData = File.ReadAllBytes(descPath);
                FguiPackageFileLoader loader = new FguiPackageFileLoader(validRequest.packageDir);
                loadedPackage = UIPackage.AddPackage(descData, validRequest.packageName, loader.Load);
                if (loadedPackage == null)
                {
                    throw new InvalidOperationException("UIPackage.AddPackage returned null. Check package files and texture import format.");
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
                sw.Stop();
                result.ok = false;
                result.message = ex.Message;
                result.pngPath = requestedOutPng;
                result.durationMs = sw.ElapsedMilliseconds;

                CleanupLoadedObjects(rootChild, loadedPackage);
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
                CleanupLoadedObjects(rootChild, loadedPackage);
            }

            onDone(result);
        }

        private static void CleanupLoadedObjects(GComponent rootChild, UIPackage loadedPackage)
        {
            if (rootChild != null)
            {
                rootChild.RemoveFromParent();
                rootChild.Dispose();
            }

            if (loadedPackage != null)
            {
                UIPackage.RemovePackage(loadedPackage.id);
            }
        }

        private static void ValidateRequest(FguiRenderRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            if (string.IsNullOrWhiteSpace(request.packageDir))
            {
                throw new ArgumentException("--package-dir is required.");
            }

            if (!Directory.Exists(request.packageDir))
            {
                throw new DirectoryNotFoundException("packageDir does not exist: " + request.packageDir);
            }

            if (string.IsNullOrWhiteSpace(request.packageName))
            {
                throw new ArgumentException("--package-name is required.");
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
    }

    internal static class CommandLineArgs
    {
        public static bool TryGetFlag(string[] args, string key)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static FguiRenderRequest ParseRequest(string[] args)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string token = args[i];
                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                string value = "true";
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[i + 1];
                    i++;
                }

                map[token] = value;
            }

            FguiRenderRequest req = new FguiRenderRequest
            {
                packageDir = GetString(map, "--package-dir", string.Empty),
                packageName = GetString(map, "--package-name", string.Empty),
                componentName = GetString(map, "--component-name", string.Empty),
                outPng = GetString(map, "--out-png", string.Empty),
                width = GetInt(map, "--width", 1920),
                height = GetInt(map, "--height", 1080),
                scale = GetFloat(map, "--scale", 1f),
                transparent = GetBool(map, "--transparent", true)
            };
            return req;
        }

        private static string GetString(Dictionary<string, string> map, string key, string fallback)
        {
            return map.TryGetValue(key, out string value) ? value : fallback;
        }

        private static int GetInt(Dictionary<string, string> map, string key, int fallback)
        {
            if (!map.TryGetValue(key, out string value))
            {
                return fallback;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }

        private static float GetFloat(Dictionary<string, string> map, string key, float fallback)
        {
            if (!map.TryGetValue(key, out string value))
            {
                return fallback;
            }

            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : fallback;
        }

        private static bool GetBool(Dictionary<string, string> map, string key, bool fallback)
        {
            if (!map.TryGetValue(key, out string value))
            {
                return fallback;
            }

            return bool.TryParse(value, out bool parsed) ? parsed : fallback;
        }
    }

    internal sealed class FguiPackageFileLoader
    {
        private readonly string _packageDir;
        private Dictionary<string, string> _fallbackFileMap;

        public FguiPackageFileLoader(string packageDir)
        {
            _packageDir = packageDir;
        }

        public object Load(string name, string extension, Type type, out DestroyMethod destroyMethod)
        {
            string resolvedPath = ResolvePath(name, extension);
            if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
            {
                destroyMethod = DestroyMethod.None;
                return null;
            }

            if (type == typeof(Texture) || type == typeof(Texture2D))
            {
                byte[] bytes = File.ReadAllBytes(resolvedPath);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                if (!tex.LoadImage(bytes, false))
                {
                    UnityEngine.Object.Destroy(tex);
                    destroyMethod = DestroyMethod.None;
                    return null;
                }

                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                destroyMethod = DestroyMethod.Destroy;
                return tex;
            }

            if (type == typeof(TextAsset))
            {
                destroyMethod = DestroyMethod.None;
                return File.ReadAllBytes(resolvedPath);
            }

            destroyMethod = DestroyMethod.None;
            return null;
        }

        private string ResolvePath(string name, string extension)
        {
            string normalizedName = (name ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            string directPath = Path.Combine(_packageDir, normalizedName + extension);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            if (_fallbackFileMap == null)
            {
                BuildFallbackIndex();
            }

            string key = (normalizedName + extension)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .ToLowerInvariant();

            if (_fallbackFileMap == null)
            {
                return null;
            }

            return _fallbackFileMap.TryGetValue(key, out string mappedPath) ? mappedPath : null;
        }

        private void BuildFallbackIndex()
        {
            _fallbackFileMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(_packageDir))
            {
                return;
            }

            string[] files = Directory.GetFiles(_packageDir, "*", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                string relative = file.Substring(_packageDir.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string key = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .ToLowerInvariant();
                _fallbackFileMap[key] = file;
            }
        }
    }
}


