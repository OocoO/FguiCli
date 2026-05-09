using System;
using System.IO;
using FguiRenderServer;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Editor
{
public static class FguiRenderBuild
{
    [MenuItem("Tools/Fgui Render/Build Windows Player")]
    public static void BuildWindowsFromMenu()
    {
        string outputPath = Path.GetFullPath("Build/FguiRenderServer/FguiRenderServer.exe");
        BuildWindowsPlayer(outputPath);
    }

    public static void BuildWindowsPlayer(string outputPath)
    {
        string scenePath = "Assets/Scenes/SampleScene.unity";
        if (!File.Exists(scenePath))
        {
            throw new FileNotFoundException("Cannot find scene for build", scenePath);
        }

        string outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new Exception("Build failed: " + report.summary.result);
        }

        Debug.Log("Build succeeded: " + outputPath);

        // copy build result to ./tools/fgui_render_client/FguiRenderServer
        string destDir = Path.GetFullPath("tools/fgui_render_client/FguiRenderServer");
        Directory.CreateDirectory(destDir);
        try
        {
            CopyDirectory(outputDir, destDir, overwrite: true);
            Debug.Log($"Copied build files from {outputDir} to {destDir}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to copy build output: {ex}");
            throw;
        }

        // ensure Unity notices any new/changed files under the project
        AssetDatabase.Refresh();
    }

    private static void CopyDirectory(string sourceDir, string destinationDir, bool overwrite)
    {
        // Normalize paths
        sourceDir = Path.GetFullPath(sourceDir);
        destinationDir = Path.GetFullPath(destinationDir);

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");

        // Create destination root if missing
        Directory.CreateDirectory(destinationDir);

        // Copy files in root
        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(filePath);
            var destFile = Path.Combine(destinationDir, fileName);
            File.Copy(filePath, destFile, overwrite);
        }

        // Recurse into subdirectories
        foreach (var dirPath in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dirPath);
            var destSubDir = Path.Combine(destinationDir, dirName);
            CopyDirectory(dirPath, destSubDir, overwrite);
        }
    }
    
    [MenuItem("Tools/Fgui Package/Test Publish Folder...")]
    public static void PublishFolderFromMenu()
    {
        string packageSourceDir = "D:/ProjectGit/AirLegion/fgui_airLegion/assets/BattleUI";
        string outputDir = "D:/Project/FguiCli/Assets/FguiEditor/Diff/BattleUI";
        
        FguiPackagePublisher.PublishPackage(packageSourceDir, outputDir);
    }

    /// <summary>
    /// Publish from a FGUI source dir then render all exported components to PNG (Editor shortcut).
    /// </summary>
    [MenuItem("Tools/Fgui Package/Test Publish and Render Component...")]
    public static void PublishAndRenderFromMenu()
    {
        string packageSourceDir = "D:/ProjectGit/AirLegion/fgui_airLegion/assets/BattleUI";
        string outPngDir        = "D:/Project/FguiCli/Assets/FguiEditor/Diff/BattleUI";

        FguiPublishAndRender.PublishAndRenderAll(
            packageSourceDir,
            outPngDir,
            scale: 1f,
            transparent: true);
    }
}
}
