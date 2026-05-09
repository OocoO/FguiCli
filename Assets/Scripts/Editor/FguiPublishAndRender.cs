using System;
using System.Collections.Generic;
using System.IO;
using FguiRenderServer;
using UnityEngine;
using UnityEditor;

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

        if (!EditorApplication.isPlaying)
        {
            EditorApplication.isPlaying = true;
        }

        FguiRenderRequest runtimeRequest = CloneRequest(request);
        bool accepted = FguiRenderBootstrap.TryRenderInPlayMode(runtimeRequest, result =>
        {
            if (result != null && result.ok)
            {
                Debug.Log($"[FguiPublishAndRender] Done (PlayMode) -> {result.pngPath}");
                AssetDatabase.Refresh();
            }
            else
            {
                string err = result == null ? "Unknown render error." : result.message;
                Debug.LogError("[FguiPublishAndRender] Render failed in Play Mode: " + err);
            }

            onComplete?.Invoke(result);
        });

        if (!accepted)
        {
            Debug.LogError("[FguiPublishAndRender] Renderer is busy or Play Mode host is unavailable.");
        }
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
        List<string> componentNames = FguiPackagePublisher.GetExportedComponentNames(packageSourceDir);
        if (componentNames.Count == 0)
        {
            Debug.LogWarning("[FguiPublishAndRender] No exported components found in package.");
            return;
        }

        Debug.Log($"[FguiPublishAndRender] Rendering {componentNames.Count} exported component(s) from '{packageSourceDir}'...");
        RenderNext(packageSourceDir, outPngDir, componentNames, 0, width, height, scale, transparent);
    }

    private static void RenderNext(
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
            return;
        }

        string componentName = componentNames[index];
        string outPng = Path.Combine(outPngDir, componentName + ".png");

        PublishAndRender(
            new FguiRenderRequest
            {
                packageSourceDir = packageSourceDir,
                componentName    = componentName,
                outPng           = outPng,
                width            = width,
                height           = height,
                scale            = scale,
                transparent      = transparent
            },
            result =>
            {
                if (result != null && result.ok)
                    Debug.Log($"[FguiPublishAndRender] [{index + 1}/{componentNames.Count}] '{componentName}' -> {result.pngPath}");
                else
                    Debug.LogWarning($"[FguiPublishAndRender] [{index + 1}/{componentNames.Count}] '{componentName}' failed: {result?.message}");

                RenderNext(packageSourceDir, outPngDir, componentNames, index + 1, width, height, scale, transparent);
            });
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
