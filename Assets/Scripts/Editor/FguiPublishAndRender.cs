using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>
    /// Publish a FGUI source package then render every exported component to a separate PNG
    /// inside <paramref name="outPngDir"/>.  Components are rendered one after another.
    /// </summary>
    public static void RenderAll(
        string publishDir,
        string packageSourceDir,
        string outPngDir,
        bool transparent = true)
    {
        List<string> componentNames = FguiPackagePublisher.GetExportedComponentNames(packageSourceDir);
        if (componentNames.Count == 0)
        {
            Debug.LogWarning("[FguiPublishAndRender] No exported components found in package.");
            return;
        }

        var packageName = FguiPackagePublisher.GetPackageName(packageSourceDir);
        Debug.Log($"[FguiPublishAndRender] Rendering {componentNames.Count} exported component(s) from '{packageSourceDir}' using published package '{packageName}'...");
        RenderNext(publishDir, packageName, outPngDir, componentNames, 0, transparent);
    }

    private static void RenderNext(
        string tempAllPackageDir,
        string packageName,
        string outPngDir,
        List<string> componentNames,
        int index,
        bool transparent)
    {
        if (index >= componentNames.Count)
        {
            Debug.Log($"[FguiPublishAndRender] All {componentNames.Count} component(s) rendered.");
            AssetDatabase.Refresh();
            return;
        }

        string componentName = componentNames[index];
        string outPng = Path.Combine(outPngDir, componentName + ".png");

        Render(
            new FguiRenderRequest
            {
                allPackageRootDir       = tempAllPackageDir,
                packageName      = packageName,
                componentName    = componentName,
                outPng           = outPng,
                transparent      = transparent
            },
            result =>
            {
                if (result != null && result.ok)
                    Debug.Log($"[FguiPublishAndRender] [{index + 1}/{componentNames.Count}] '{componentName}' -> {result.pngPath}");
                else
                    Debug.LogWarning($"[FguiPublishAndRender] [{index + 1}/{componentNames.Count}] '{componentName}' failed: {result?.message}");

                RenderNext(tempAllPackageDir, packageName, outPngDir, componentNames, index + 1, transparent);
            });
    }

    private static void Render(FguiRenderRequest request, Action<FguiRenderResult> onComplete)
    {
        if (!EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = true;
            Debug.Log($"[FguiPublishAndRender] Publishing Starting");
            return;
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

    public static void PublishPackage(string packageSourceDir, out string tempPublishDir)
    {
        tempPublishDir = Path.Combine(Path.GetTempPath(), "fgui_render_" + Guid.NewGuid().ToString("N"));
        
        // temp: decode with packageSourceDir
        var fguiProjectRoot = new DirectoryInfo(packageSourceDir).Parent.FullName;
        FguiPackagePublisher.PublishPackageAll(fguiProjectRoot, tempPublishDir);
    }

    private static void TryDeleteTempDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* best-effort */ }
    }

    private static FguiRenderRequest CloneRequest(FguiRenderRequest request)
    {
        return new FguiRenderRequest
        {
            packageSourceDir = request.packageSourceDir,
            allPackageRootDir = request.allPackageRootDir,
            packageName = request.packageName,
            componentName = request.componentName,
            outPng = request.outPng,
            transparent = request.transparent
        };
    }
}
}
