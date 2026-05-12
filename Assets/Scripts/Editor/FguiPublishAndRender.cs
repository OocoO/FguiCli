using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FguiRenderServer;
using UnityEngine;
using UnityEditor;

namespace Editor
{
    /// <summary>
    /// Editor-side helper that publishes a FairyGUI project then batch-renders
    /// all exported components to PNG files.
    /// </summary>
    public static class FguiPublishAndRender
    {
        // Pending batch state – stored before entering play mode, consumed on EnteredPlayMode.
        private static string _pendingPublishDir;
        private static List<FguiRenderRequest> _pendingRequests;
        private static Action<List<FguiRenderResult>> _pendingCallback;

        /// <summary>
        /// Discovers all exported components in every package under <paramref name="fguiProjectRoot"/>,
        /// loads them all at once from <paramref name="publishDir"/>, then renders each one to
        /// <paramref name="outPngDir"/>/<em>packageName</em>/<em>componentName</em>.png.
        /// </summary>
        public static void RenderAll(
            string publishDir,
            string fguiProjectRoot,
            string outPngDir)
        {
            string assetsDir = ResolveAssetsDir(fguiProjectRoot);
            if (assetsDir == null)
            {
                Debug.LogWarning("[FguiPublishAndRender] Cannot find a packages folder under: " + fguiProjectRoot);
                return;
            }

            var requests = new List<FguiRenderRequest>();
            foreach (string pkgDir in Directory.GetDirectories(assetsDir))
            {
                if (!File.Exists(Path.Combine(pkgDir, "package.xml")))
                    continue;

                string packageName = FguiPackagePublisher.GetPackageName(pkgDir);
                List<string> components = FguiPackagePublisher.GetExportedComponentNames(pkgDir);
                if (components.Count == 0)
                    continue;

                Debug.Log($"[FguiPublishAndRender] Package '{packageName}': {components.Count} exported component(s).");
                foreach (string comp in components)
                {
                    requests.Add(new FguiRenderRequest
                    {
                        allPackageRootDir = publishDir,
                        packageName       = packageName,
                        componentName     = comp,
                        outPng            = Path.Combine(outPngDir, packageName, comp + ".png"),
                        transparent       = true,
                    });
                }
            }

            if (requests.Count == 0)
            {
                Debug.LogWarning("[FguiPublishAndRender] No exported components found in any package.");
                return;
            }

            Debug.Log($"[FguiPublishAndRender] Queued {requests.Count} render(s) across all packages.");
            ScheduleBatchRender(publishDir, requests, results =>
            {
                int ok = results.Count(r => r.ok);
                Debug.Log($"[FguiPublishAndRender] Batch complete — {ok}/{results.Count} succeeded.");
                AssetDatabase.Refresh();
            });
        }

        /// <summary>Publish all packages in <paramref name="fguiProjectRoot"/> to a fresh temp directory.</summary>
        public static void PublishPackage(string fguiProjectRoot, out string tempPublishDir)
        {
            tempPublishDir = Path.Combine(Path.GetTempPath(), "fgui_render_" + Guid.NewGuid().ToString("N"));
            FguiPackagePublisher.PublishPackageAll(fguiProjectRoot, tempPublishDir);
        }

        // ── Private helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// If already in play mode, dispatch immediately; otherwise store state and
        /// enter play mode — the batch fires on <see cref="PlayModeStateChange.EnteredPlayMode"/>.
        /// </summary>
        private static void ScheduleBatchRender(
            string allPackagesDir,
            List<FguiRenderRequest> requests,
            Action<List<FguiRenderResult>> onDone)
        {
            if (EditorApplication.isPlaying)
            {
                DispatchBatch(allPackagesDir, requests, onDone);
                return;
            }

            _pendingPublishDir = allPackagesDir;
            _pendingRequests   = requests;
            _pendingCallback   = onDone;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.isPlaying = true;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
                return;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            string dir = _pendingPublishDir;
            var reqs   = _pendingRequests;
            var cb     = _pendingCallback;
            _pendingPublishDir = null;
            _pendingRequests   = null;
            _pendingCallback   = null;

            if (reqs != null && reqs.Count > 0)
                DispatchBatch(dir, reqs, cb);
        }

        private static void DispatchBatch(
            string allPackagesDir,
            List<FguiRenderRequest> requests,
            Action<List<FguiRenderResult>> onDone)
        {
            bool accepted = FguiRenderBootstrap.TryBatchRenderInPlayMode(allPackagesDir, requests, onDone);
            if (!accepted)
                Debug.LogError("[FguiPublishAndRender] Renderer is busy — batch render not started.");
        }

        /// <summary>
        /// Returns the directory that directly contains the FairyGUI package sub-folders.
        /// Checks <c>assets/</c> first (FairyGUI convention), then falls back to the root itself.
        /// </summary>
        private static string ResolveAssetsDir(string fguiProjectRoot)
        {
            string conventional = Path.Combine(fguiProjectRoot, "assets");
            if (Directory.Exists(conventional))
                return conventional;

            // Root itself may contain package sub-folders directly.
            if (Directory.GetDirectories(fguiProjectRoot)
                .Any(d => File.Exists(Path.Combine(d, "package.xml"))))
                return fguiProjectRoot;

            return null;
        }
    }
}
