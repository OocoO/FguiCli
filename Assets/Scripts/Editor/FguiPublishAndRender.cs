using System;
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
    public static void PublishAndRender(FguiRenderRequest request)
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
        });

        if (!accepted)
        {
            Debug.LogError("[FguiPublishAndRender] Renderer is busy or Play Mode host is unavailable.");
        }
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
