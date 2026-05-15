using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace FguiRenderServer
{
    [Serializable]
    public class FguiRenderRequest
    {
        /// <summary>
        /// When set, the pipeline will first publish the FGUI source package at this directory
        /// to a temp folder, then render from there. Leave empty to use a pre-published packageDir.
        /// </summary>
        public string packageSourceDir;
        public string allPackageRootDir;
        public string packageName;
        public string componentName;
        public string outPng;
        public bool transparent = true;
    }

    [Serializable]
    public class FguiRenderResult
    {
        public bool ok;
        public string message;
        public string pngPath;
        public long durationMs;
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
                packageSourceDir = GetString(map, "--package-source-dir", string.Empty),
                allPackageRootDir = GetString(map, "--package-dir", string.Empty),
                packageName = GetString(map, "--package-name", string.Empty),
                componentName = GetString(map, "--component-name", string.Empty),
                outPng = GetString(map, "--out-png", string.Empty),
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
                Debug.LogError($"[FguiPackageFileLoader] File doesn't exist: {resolvedPath}");
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
                    Debug.LogError($"[FguiPackageFileLoader] Failed to load texture: {resolvedPath}");
                    return null;
                }

                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                destroyMethod = DestroyMethod.Destroy;
                Debug.Log($"[FguiPackageFileLoader] Load Texture Success: {resolvedPath}");
                return tex;
            }

            if (type == typeof(TextAsset))
            {
                destroyMethod = DestroyMethod.None;
                Debug.Log($"[FguiPackageFileLoader] Load Binary Success: {resolvedPath}");
                return File.ReadAllBytes(resolvedPath);
            }

            Debug.LogError($"[FguiPackageFileLoader] Failed to load {type.Name}: {resolvedPath}");
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

            return null;
        }
    }
}


