#if UNITY_EDITOR
using UnityEditor;
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
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
            // fGuiProjectRoot is typically the "assets" folder inside the .fairy project directory.
            // Locate the .fairy project root and publish everything in one ofgui call.
            string fairyRoot = FindFairyProjectRoot(fGuiProjectRoot)
                ?? throw new InvalidOperationException("Cannot find a .fairy project file searching from: " + fGuiProjectRoot);

            Directory.CreateDirectory(outputRoot);
            RunOfguiPublish(fairyRoot, outputRoot, packageName: null);
            Debug.Log($"Published all FairyGUI packages via ofgui to {outputRoot}.");
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
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

            string packageName = GetPackageName(fullPackageDir);
            string fairyRoot = FindFairyProjectRoot(fullPackageDir)
                ?? throw new InvalidOperationException("Cannot find a .fairy project file searching from: " + fullPackageDir);

            RunOfguiPublish(fairyRoot, fullOutputDir, packageName);
            Debug.Log($"Published FairyGUI package '{packageName}' via ofgui to {fullOutputDir}.");
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
        }

        /// <summary>
        /// Walks up the directory tree from <paramref name="startDir"/> looking for a *.fairy file.
        /// Returns the directory that contains the .fairy file, or null if not found within 6 levels.
        /// </summary>
        private static string FindFairyProjectRoot(string startDir)
        {
            string dir = Path.GetFullPath(startDir);
            for (int depth = 0; depth < 6; depth++)
            {
                if (Directory.GetFiles(dir, "*.fairy", SearchOption.TopDirectoryOnly).Length > 0)
                    return dir;
                string parent = Path.GetDirectoryName(dir);
                if (parent == null || parent == dir)
                    break;
                dir = parent;
            }
            return null;
        }

        private static void RunOfguiPublish(string fairyProjectRoot, string outputDir, string packageName)
        {
            string ofguiArgs = "publish " + QuoteArgument(fairyProjectRoot) +
                               " --output " + QuoteArgument(outputDir) +
                               " --project-type unity";

            if (!string.IsNullOrWhiteSpace(packageName))
            {
                ofguiArgs += " --packages " + QuoteArgument(packageName);
            }

            // npm global .cmd scripts require the shell (cmd.exe) to resolve correctly.
            // Unity's editor process does not inherit the user's PATH, so we invoke via cmd /c.
            string fileName;
            string arguments;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                fileName = "cmd.exe";
                arguments = "/c ofgui " + ofguiArgs;
            }
            else
            {
                fileName = "/bin/sh";
                arguments = "-c \"ofgui " + ofguiArgs.Replace("\"", "\\\"") + "\"";
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            Debug.Log($"Calling Cmd: {fileName} {arguments}");

            Process process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start ofgui process. Make sure @openfairygui/cli is installed globally: npm install --global @openfairygui/cli");
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

            // Remove trailing directory separators: on Windows, a quoted path ending with a
            // backslash (e.g. "C:\foo\") causes cmd.exe to treat \" as an escaped quote,
            // breaking argument parsing entirely.
            value = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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

