using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Xml.Linq;
using FguiRenderServer;
using UnityEngine;
using UnityEditor;

namespace Editor
{
/// <summary>
/// Editor-side helper that publishes a FGUI source package then launches the pre-built
/// render server player to capture a component as PNG.
/// In the built player, use FguiRenderBootstrap with --package-source-dir instead.
/// </summary>
public static class FguiPublishAndRender
{
    public static void PublishAndRender(FguiRenderRequest request, Action<FguiRenderResult> onComplete)
    {
        if (string.IsNullOrWhiteSpace(request.packageSourceDir))
            throw new ArgumentException("packageSourceDir must be set.");

        PublishOnceThenRender(request, onComplete);
    }

    /// <summary>
    /// Publish a FGUI source package then render every exported component to a separate PNG
    /// inside <paramref name="outPngDir"/>.  Components are rendered one after another.
    /// </summary>
    public static void PublishAndRenderAll(
        string packageSourceDir,
        string outPngDir,
        int width = 1920,
        int height = 1080,
        float scale = 1f,
        bool transparent = true)
    {
        if (string.IsNullOrWhiteSpace(packageSourceDir))
            throw new ArgumentException("packageSourceDir must be set.");

        List<string> componentNames = FguiPackagePublisher.GetExportedComponentNames(packageSourceDir);
        if (componentNames.Count == 0)
        {
            Debug.LogWarning("[FguiPublishAndRender] No exported components found in package.");
            return;
        }

        PublishPackageOnce(packageSourceDir, out string tempPublishDir, out string packageName);
        Debug.Log($"[FguiPublishAndRender] Rendering {componentNames.Count} exported component(s) from '{packageSourceDir}' using published package '{packageName}'...");
        RenderNextPublished(tempPublishDir, packageName, packageSourceDir, outPngDir, componentNames, 0, width, height, scale, transparent);
    }

    private static void RenderNextPublished(
        string packageDir,
        string packageName,
        string packageSourceDir,
        string outPngDir,
        List<string> componentNames,
        int index,
        int width,
        int height,
        float scale,
        bool transparent)
    {
        if (index >= componentNames.Count)
        {
            Debug.Log($"[FguiPublishAndRender] All {componentNames.Count} component(s) rendered.");
            AssetDatabase.Refresh();
            TryDeleteTempDir(packageDir);
            return;
        }

        string componentName = componentNames[index];
        string outPng = Path.Combine(outPngDir, componentName + ".png");
        // Determine component-specific size from the package. If the component XML
        // contains a size attribute ("width,height"), use that as the render size.
        // Fall back to the provided width/height parameters.
        int compW = width;
        int compH = height;
        try
        {
            if (TryGetComponentSize(packageSourceDir, componentName, out int w, out int h))
            {
                compW = w;
                compH = h;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[FguiPublishAndRender] Failed to read component size for '{componentName}': {ex.Message}");
        }

        RenderPublishedOnce(
            new FguiRenderRequest
            {
                packageDir       = packageDir,
                packageName      = packageName,
                componentName    = componentName,
                outPng           = outPng,
                width            = compW,
                height           = compH,
                scale            = scale,
                transparent      = transparent
            },
            result =>
            {
                if (result != null && result.ok)
                    Debug.Log($"[FguiPublishAndRender] [{index + 1}/{componentNames.Count}] '{componentName}' -> {result.pngPath}");
                else
                    Debug.LogWarning($"[FguiPublishAndRender] [{index + 1}/{componentNames.Count}] '{componentName}' failed: {result?.message}");

                RenderNextPublished(packageDir, packageName, packageSourceDir, outPngDir, componentNames, index + 1, width, height, scale, transparent);
            });
    }

    private static void RenderPublishedOnce(FguiRenderRequest request, Action<FguiRenderResult> onComplete)
    {
        if (!EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = true;
        }

        FguiRenderRequest runtimeRequest = CloneRequest(request);
        runtimeRequest.packageSourceDir = string.Empty;

        bool accepted = FguiRenderBootstrap.TryRenderInPlayMode(runtimeRequest, result =>
        {
            if (result != null && result.ok)
            {
                Debug.Log($"[FguiPublishAndRender] Done (PlayMode) -> {result.pngPath}");
                AssetDatabase.Refresh();
            }
            else
            {
                string err = string.IsNullOrEmpty(result?.message) ? "Unknown render error." : result.message;
                Debug.LogError($"[FguiPublishAndRender] Render {request.componentName} failed: " + err);
            }

            onComplete?.Invoke(result);
        });

        if (!accepted)
        {
            Debug.LogError("[FguiPublishAndRender] Renderer is busy or Play Mode host is unavailable.");
        }
    }

    private static void PublishOnceThenRender(FguiRenderRequest request, Action<FguiRenderResult> onComplete)
    {
        PublishPackageOnce(request.packageSourceDir, out string tempPublishDir, out string packageName);

        FguiRenderRequest runtimeRequest = CloneRequest(request);
        runtimeRequest.packageDir = tempPublishDir;
        runtimeRequest.packageName = packageName;
        runtimeRequest.packageSourceDir = string.Empty;

        if (!EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = true;
        }

        bool accepted = FguiRenderBootstrap.TryRenderInPlayMode(runtimeRequest, result =>
        {
            try
            {
                if (result != null && result.ok)
                {
                    Debug.Log($"[FguiPublishAndRender] Done (PlayMode) -> {result.pngPath}");
                    AssetDatabase.Refresh();
                }
                else
                {
                    string err = result?.message;
                    if (string.IsNullOrEmpty(err))
                    {
                        err = "Unknown render error.";
                    }
                    Debug.LogError($"[FguiPublishAndRender] Render {request.componentName} failed: " + err);
                }

                onComplete?.Invoke(result);
            }
            finally
            {
                TryDeleteTempDir(tempPublishDir);
            }
        });

        if (!accepted)
        {
            TryDeleteTempDir(tempPublishDir);
            Debug.LogError("[FguiPublishAndRender] Renderer is busy or Play Mode host is unavailable.");
        }
    }

    private static void PublishPackageOnce(string packageSourceDir, out string tempPublishDir, out string packageName)
    {
        tempPublishDir = Path.Combine(Path.GetTempPath(), "fgui_render_" + Guid.NewGuid().ToString("N"));
        packageName = FguiPackagePublisher.GetPackageName(packageSourceDir);
    }

    private static void TryDeleteTempDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* best-effort */ }
    }

    private static bool TryGetComponentSize(string packageSourceDir, string componentName, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(packageSourceDir) || string.IsNullOrWhiteSpace(componentName))
            return false;

        // Load package.xml and locate the component entry to resolve the component XML path
        string fullPackageDir = Path.GetFullPath(packageSourceDir);
        string packageXmlPath = Path.Combine(fullPackageDir, "package.xml");
        if (!File.Exists(packageXmlPath))
            return false;

        XDocument pkgDoc = XDocument.Load(packageXmlPath);
        XElement resources = pkgDoc.Root?.Element("resources");
        if (resources == null)
            return false;

        XElement componentElement = null;
        foreach (XElement el in resources.Elements("component"))
        {
            string nameAttr = el.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(nameAttr))
                continue;

            string nameNoExt = Path.GetFileNameWithoutExtension(nameAttr);
            if (string.Equals(nameNoExt, componentName, StringComparison.OrdinalIgnoreCase))
            {
                componentElement = el;
                break;
            }
        }

        if (componentElement == null)
            return false;

        string fileName = componentElement.Attribute("name")?.Value;
        string pathAttr = componentElement.Attribute("path")?.Value ?? "/";
        string rel = pathAttr.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        string compPath = Path.GetFullPath(Path.Combine(fullPackageDir, rel, fileName));
        if (!File.Exists(compPath))
            return false;

        XDocument doc = XDocument.Load(compPath);
        XElement root = doc.Root;
        if (root == null)
            return false;

        string sizeText = root.Attribute("size")?.Value;
        if (string.IsNullOrWhiteSpace(sizeText))
            return false;

        string[] parts = sizeText.Split(',');
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width))
            return false;
        if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            return false;

        return true;
    }

    private static FguiRenderRequest CloneRequest(FguiRenderRequest request)
    {
        return new FguiRenderRequest
        {
            packageSourceDir = request.packageSourceDir,
            packageDir = request.packageDir,
            packageName = request.packageName,
            componentName = request.componentName,
            outPng = request.outPng,
            width = request.width,
            height = request.height,
            scale = request.scale,
            transparent = request.transparent
        };
    }
}
}
