using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Debug = UnityEngine.Debug;

namespace FguiRenderServer
{
    public static class FguiPackagePublisher
    {
        public static void PublishFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string packageDir = GetArg(args, "-packageDir") ?? GetArg(args, "--packageDir");
            string outputDir = GetArg(args, "-outputDir") ?? GetArg(args, "--outputDir");

            if (string.IsNullOrWhiteSpace(packageDir) || string.IsNullOrWhiteSpace(outputDir))
            {
                throw new ArgumentException("Missing required arguments. Expected -packageDir <path> and -outputDir <path>.");
            }

            PublishPackage(packageDir, outputDir);
        }

        /// <summary>
        /// Returns the names (without .xml extension) of all exported components in the package.
        /// </summary>
        public static List<string> GetExportedComponentNames(string packageDir)
        {
            string fullDir = Path.GetFullPath(packageDir);
            string xmlPath = Path.Combine(fullDir, "package.xml");
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("Cannot find FairyGUI package.xml", xmlPath);

            XDocument doc = XDocument.Load(xmlPath);
            XElement resources = doc.Root?.Element("resources");
            if (resources == null)
                return new List<string>();

            return resources.Elements("component")
                .Where(e => string.Equals(e.Attribute("exported")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attribute("name")?.Value)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => Path.GetFileNameWithoutExtension(n))
                .ToList();
        }

        /// <summary>Returns the package name declared in package.xml (or folder name as fallback).</summary>
        public static string GetPackageName(string packageDir)
        {
            string fullDir = Path.GetFullPath(packageDir);
            string xmlPath = Path.Combine(fullDir, "package.xml");
            if (File.Exists(xmlPath))
            {
                XDocument doc = XDocument.Load(xmlPath);
                string name = doc.Root?.Attribute("name")?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return new DirectoryInfo(fullDir).Name;
        }

        public static void PublishPackageAll(string fGuiProjectRoot, string outputRoot)
        {
            // D:\ProjectGit\AirLegion\fgui_airLegion\assets
            var packagePath = Directory.GetDirectories(fGuiProjectRoot, "*");
            foreach (var path in packagePath)
            {
                var packageName = Path.GetFileName(path);
                var outputDir = Path.Combine(outputRoot, packageName);
                PublishPackage(path, outputDir);
            }
        }

        public static void PublishPackage(string packageDir, string outputDir)
        {
            string fullPackageDir = Path.GetFullPath(packageDir);
            string fullOutputDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(fullOutputDir);

            if (TryFindRuntimeDescriptor(fullPackageDir, out _))
            {
                CopyDirectoryRecursive(fullPackageDir, fullOutputDir);
                EnsureRuntimeDescriptorExists(fullOutputDir);
                return;
            }

            RunOfguiPublish(fullPackageDir, fullOutputDir);
            Debug.Log($"Published FairyGUI package via ofgui to {fullOutputDir}.");
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
        }

        private static void RunOfguiPublish(string packageDir, string outputDir)
        {
            string arguments = "publish " + QuoteArgument(packageDir) +
                               " --output " + QuoteArgument(outputDir) +
                               " --project-type unity";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "ofgui",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Process process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start ofgui process. Make sure @openfairygui/cli is installed: npm install --global @openfairygui/cli");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Debug.Log("[ofgui] " + stdout);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "ofgui publish failed (exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "):\n" + stderr);
            }
            else if (!string.IsNullOrWhiteSpace(stderr))
            {
                Debug.LogWarning("[ofgui] " + stderr);
            }
        }

        private static bool TryFindRuntimeDescriptor(string dir, out string descriptorPath)
        {
            descriptorPath = null;
            if (!Directory.Exists(dir))
            {
                return false;
            }

            string[] matches = Directory.GetFiles(dir, "*_fui.bytes", SearchOption.TopDirectoryOnly);
            if (matches.Length == 0)
            {
                return false;
            }

            descriptorPath = matches[0];
            return true;
        }

        private static void EnsureRuntimeDescriptorExists(string dir)
        {
            if (!TryFindRuntimeDescriptor(dir, out _))
            {
                throw new InvalidOperationException("No '*_fui.bytes' file was found in: " + dir);
            }
        }

        private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            foreach (string filePath in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(filePath);
                string destPath = Path.Combine(destinationDir, fileName);
                File.Copy(filePath, destPath, true);
            }

            foreach (string childDir in Directory.GetDirectories(sourceDir))
            {
                string childName = Path.GetFileName(childDir);
                string childDest = Path.Combine(destinationDir, childName);
                CopyDirectoryRecursive(childDir, childDest);
            }
        }

        private static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string GetArg(string[] args, string key)
        {
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

