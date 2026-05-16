using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FguiRenderServer.Editor
{
    public static class FguiRenderBuild
    {
        const string DefaultOutputRelativePath = "tools/fgui_render_client/FguiRenderServer/FguiRenderServer.exe";

        [MenuItem("Tools/Fgui Render/Build Windows Player To Render Client")]
        public static void BuildForRenderClientToolFromMenu()
        {
            string projectRoot = GetProjectRoot();
            string outputPath = Path.GetFullPath(Path.Combine(projectRoot, DefaultOutputRelativePath));
            BuildWindowsPlayer(outputPath);
        }

        // Command line entry.
        // Unity.exe -batchmode -quit -projectPath <path> -executeMethod Editor.FguiRenderBuild.BuildForRenderClientToolFromCommandLine
        public static void BuildForRenderClientToolFromCommandLine()
        {
            string outputPath = GetArgumentValue("--output");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                string projectRoot = GetProjectRoot();
                outputPath = Path.GetFullPath(Path.Combine(projectRoot, DefaultOutputRelativePath));
            }

            BuildWindowsPlayer(outputPath);
        }

        static void BuildWindowsPlayer(string outputExePath)
        {
            if (string.IsNullOrWhiteSpace(outputExePath))
            {
                throw new ArgumentException("outputExePath is required", nameof(outputExePath));
            }

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                throw new InvalidOperationException("No enabled scenes in EditorBuildSettings.");
            }

            string outputDirectory = Path.GetDirectoryName(outputExePath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = outputExePath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException("Build failed: " + summary.result + ", output=" + outputExePath);
            }

            UnityEngine.Debug.Log("FGUI Render Server build success: " + outputExePath);
        }

        static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        static string GetArgumentValue(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}


