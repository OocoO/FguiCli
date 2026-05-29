using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FairyGUI;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FguiRenderServer.Editor
{
	public static class FguiProjectLoaderTestMenu
	{
		private readonly struct SmokeTestTarget
		{
			public SmokeTestTarget(string projectRoot, string packageName, string componentName)
			{
				ProjectRoot = projectRoot;
				PackageName = packageName;
				ComponentName = componentName;
			}

			public string ProjectRoot { get; }
			public string PackageName { get; }
			public string ComponentName { get; }
		}

		const string DefaultProjectRoot = @"D:\ProjectGit\AirLegion\fgui_airLegion";
		const string DefaultBranchTag = "eng";
		const string SmokeTestOutputFolderName = "FguiSmokeTestOutput";
		const string FullTestProjectRoot = @"D:\ProjectGit\fgui_idle_dev\FGUIProject";
		const string FullTestOutputRoot = @"D:\Project\FguiCli\Temp\renderTest";
		const string SmokeTestXmlPathPrefKey = "FguiRenderServer.Editor.FguiProjectLoaderTestMenu.SmokeTestXmlPath";
		static readonly string[] KnownPackageRoots =
		{
			"assets",
			"assets_eng",
			"assets_hk",
			"assets_mo",
			"assets_tw",
			"assets_zh_tc",
		};

		[MenuItem("Tools/Fgui Render/Smoke Test")]
		public static void SmokeTest()
		{
			try
			{
				string xmlPath = PromptForSmokeTestXmlPath();
				if (string.IsNullOrWhiteSpace(xmlPath))
				{
					Debug.Log("FairyGUI: smoke test cancelled, no XML selected.");
					return;
				}

				SmokeTestTarget target = ResolveSmokeTestTarget(xmlPath);

				var component = Object.FindObjectOfType<FguiRenderServerBehaviour>();
				if (component == null)
				{
					Debug.LogError("FairyGUI: smoke test failed, FguiRenderServerBehaviour not found in scene.");
					return;
				}

				string outputDir = Path.Combine(Application.dataPath, SmokeTestOutputFolderName);
				Directory.CreateDirectory(outputDir);

				string pngName = string.Format(
					"{0}_{1}_{2}.png",
								SanitizeFileName(target.PackageName),
								SanitizeFileName(Path.GetFileNameWithoutExtension(target.ComponentName)),
					DateTime.Now.ToString("yyyyMMdd_HHmmss"));
				string pngPath = Path.Combine(outputDir, pngName);

				component.StartRenderRequest(
					new FguiRenderServerBehaviour.RenderRequest
					{
								projectRootDir = target.ProjectRoot,
								packageName = target.PackageName,
								componentName = target.ComponentName,
						outPng = pngPath,
						branchTag = DefaultBranchTag,
					},
					result =>
					{
						AssetDatabase.Refresh();
						if (result == null)
						{
							Debug.LogError("FairyGUI: smoke test failed, render result is null");
							return;
						}

						if (!result.ok)
						{
							Debug.LogError("FairyGUI: smoke test failed - " + result.message);
							return;
						}

						Debug.Log(string.Format(
							"FairyGUI: smoke test success. package={0}, branch={1}, png={2}, size={3}x{4}, durationMs={5}",
											target.PackageName,
							DefaultBranchTag,
							result.pngPath,
							result.width,
							result.height,
							result.durationMs));
					});
			}
			catch (InvalidOperationException ex)
			{
				Debug.LogError("FairyGUI: smoke test failed - " + ex.Message);
			}
			catch (Exception ex)
			{
				Debug.LogException(ex);
			}
		}

		[MenuItem("Tools/Fgui Render/Full Export Test (IdleDev -> renderTest)")]
		public static void FullExportIdleDevProjectInEditor()
		{
			var component = Object.FindObjectOfType<FguiRenderServerBehaviour>();
			component.StartCoroutine(RunFullExportInEditor(FullTestProjectRoot, FullTestOutputRoot, null));
		}

		static string PromptForSmokeTestXmlPath()
		{
			string initialDirectory = EditorPrefs.GetString(SmokeTestXmlPathPrefKey, DefaultProjectRoot);
			if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
			{
				initialDirectory = DefaultProjectRoot;
			}

			string selectedPath = EditorUtility.OpenFilePanel("Select FGUI XML for Smoke Test", initialDirectory, "xml");
			if (string.IsNullOrWhiteSpace(selectedPath))
			{
				return null;
			}

			selectedPath = Path.GetFullPath(selectedPath);
			EditorPrefs.SetString(SmokeTestXmlPathPrefKey, Path.GetDirectoryName(selectedPath));
			return selectedPath;
		}

		static SmokeTestTarget ResolveSmokeTestTarget(string xmlPath)
		{
			if (string.IsNullOrWhiteSpace(xmlPath))
			{
				throw new InvalidOperationException("selected XML path is empty");
			}

			if (!File.Exists(xmlPath))
			{
				throw new InvalidOperationException("selected XML file not found - " + xmlPath);
			}

			string componentName = Path.GetFileName(xmlPath);
			if (string.IsNullOrWhiteSpace(componentName))
			{
				throw new InvalidOperationException("selected XML file name is empty - " + xmlPath);
			}

			string selectedDirectory = Path.GetFullPath(Path.GetDirectoryName(xmlPath) ?? string.Empty);
			string packageRootName = null;
			string projectRoot = null;
			foreach (string rootName in KnownPackageRoots)
			{
				string current = selectedDirectory;
				while (!string.IsNullOrWhiteSpace(current))
				{
					string candidate = Path.Combine(current, rootName);
					if (Directory.Exists(candidate) && xmlPath.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
					{
						projectRoot = current;
						packageRootName = rootName;
						break;
					}

					DirectoryInfo parent = Directory.GetParent(current);
					if (parent == null)
					{
						break;
					}

					current = parent.FullName;
				}

				if (!string.IsNullOrWhiteSpace(projectRoot))
				{
					break;
				}
 			}

			if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(packageRootName))
			{
				throw new InvalidOperationException("could not determine FGUI project root from selected XML path - " + xmlPath);
			}

			string packageRootPath = Path.Combine(projectRoot, packageRootName);
			if (!selectedDirectory.StartsWith(packageRootPath, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("selected XML is not inside a recognized package root - " + xmlPath);
			}

			string relativePath = selectedDirectory.Substring(packageRootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.IsNullOrWhiteSpace(relativePath))
			{
				throw new InvalidOperationException("selected XML must be inside a package folder - " + xmlPath);
			}

			string[] parts = relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				throw new InvalidOperationException("could not determine package name from selected XML path - " + xmlPath);
			}

			string packageName = parts[0];
			return new SmokeTestTarget(projectRoot, packageName, componentName);
		}

		private static IEnumerator RunFullExportInEditor(string projectRoot, string outputRoot, string branchTag)
		{
			if (!Directory.Exists(projectRoot))
			{
				Debug.LogError("FairyGUI: full export failed, project root not found - " + projectRoot);
				yield break;
			}

			Directory.CreateDirectory(outputRoot);
			Stage.Instantiate();
			UIPackage.RemoveAllPackages(true);
			GRoot.inst.RemoveChildren(0, -1, true);

			FguiProjectLoader.LoadProject(projectRoot, branchTag);
			List<UIPackage> packages = UIPackage.GetPackages();
			if (packages == null || packages.Count == 0)
			{
				Debug.LogError("FairyGUI: full export failed, no packages loaded from project - " + projectRoot);
				yield break;
			}

			int totalExportedComponents = CountExportedComponents(packages);
			if (totalExportedComponents <= 0)
			{
				Debug.LogWarning("FairyGUI: no exported=true component found.");
				yield break;
			}

			int processedComponents = 0;
			int successCount = 0;
			List<string> failures = new List<string>();

			foreach (UIPackage pkg in packages)
			{
				if (pkg == null)
					continue;

				string packageOutputDir = Path.Combine(outputRoot, SanitizeFileName(pkg.name));
				Directory.CreateDirectory(packageOutputDir);

				List<PackageItem> items = pkg.GetItems();
				if (items == null)
					continue;

				foreach (PackageItem item in items)
				{
					if (item == null || item.type != PackageItemType.Component || !item.exported)
						continue;

					processedComponents += 1;
					
					GRoot.inst.RemoveChildren(0, -1, true);
					string resourceName = item.name;
					GObject panel = null;
					Texture2D screenshot = null;
					List<string> componentLogs = new List<string>();

					panel = UIPackage.CreateObject(pkg.name, resourceName);
					if (panel == null)
					{
						failures.Add(FormatFailure(pkg.name, resourceName, "create object failed", componentLogs));
						continue;
					}

					panel.MakeFullScreen();
					GRoot.inst.AddChild(panel);
	
					yield return null;
					yield return new WaitForEndOfFrame();
	
					screenshot = FguiProjectLoaderTestMenu.CaptureUiTexture(Screen.width, Screen.height);
					if (screenshot == null)
					{
						failures.Add(FormatFailure(pkg.name, resourceName, "screenshot is null", componentLogs));
						continue;
					}

					string pngName = item.id + "_" + SanitizeFileName(Path.GetFileNameWithoutExtension(resourceName)) + ".png";
					string pngPath = Path.Combine(packageOutputDir, pngName);
					byte[] pngBytes = screenshot.EncodeToPNG();
					Debug.Log($"Export Png -- {pngPath}");
					File.WriteAllBytes(pngPath, pngBytes);
					successCount += 1;
				}
			}

			string reportPath = Path.Combine(outputRoot, "full_export_report.txt");
			List<string> reportLines = new List<string>();
			reportLines.Add("projectRoot=" + projectRoot);
			reportLines.Add("branchTag=" + (string.IsNullOrEmpty(branchTag) ? "<none>" : branchTag));
			reportLines.Add("totalExportedComponents=" + totalExportedComponents);
			reportLines.Add("processedComponents=" + processedComponents);
			reportLines.Add("successCount=" + successCount);
			reportLines.Add("failureCount=" + failures.Count);
			reportLines.Add(string.Empty);

			if (failures.Count > 0)
			{
				reportLines.Add("=== Failures ===");
				reportLines.AddRange(failures);
			}
			else
			{
				reportLines.Add("No failures.");
			}

			File.WriteAllLines(reportPath, reportLines, Encoding.UTF8);
			Debug.Log(string.Format(
				"FairyGUI: full export finished. exported={0}, processed={1}, success={2}, failed={3}, output={4}, report={5}",
				totalExportedComponents,
				processedComponents,
				successCount,
				failures.Count,
				outputRoot,
				reportPath));
		}

		static int CountExportedComponents(List<UIPackage> packages)
		{
			int count = 0;
			foreach (UIPackage pkg in packages)
			{
				if (pkg == null)
					continue;

				List<PackageItem> items = pkg.GetItems();
				if (items == null)
					continue;

				foreach (PackageItem item in items)
				{
					if (item != null && item.type == PackageItemType.Component && item.exported)
					{
						count += 1;
					}
				}
			}

			return count;
		}

		static string FormatFailure(string packageName, string resourceName, string error, List<string> componentLogs)
		{
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("---");
			sb.AppendLine("component=" + packageName + "/" + resourceName);
			sb.AppendLine("error=" + Shorten(error, 2000));
			sb.AppendLine("logSummary=");
			if (componentLogs == null || componentLogs.Count == 0)
			{
				sb.AppendLine("  <no log>");
			}
			else
			{
				for (int i = 0; i < componentLogs.Count; i++)
				{
					sb.AppendLine("  " + componentLogs[i]);
				}
			}

			return sb.ToString().TrimEnd();
		}

		static Texture2D CaptureUiTexture(int width, int height)
		{
			Camera camera = StageCamera.main;
			if (camera == null)
			{
				Debug.LogError("FairyGUI: CaptureUiTexture failed, StageCamera.main is null");
				return null;
			}

			RenderTexture rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
			RenderTexture previousRT = camera.targetTexture;
			RenderTexture previousActive = RenderTexture.active;

			camera.targetTexture = rt;
			camera.Render();
			camera.targetTexture = previousRT;

			RenderTexture.active = rt;
			Texture2D tex = new Texture2D(width, height, TextureFormat.ARGB32, false);
			tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
			tex.Apply();
			RenderTexture.active = previousActive;
			RenderTexture.ReleaseTemporary(rt);

			return tex;
		}


		static string Shorten(string value, int maxLength)
		{
			if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
			{
				return value;
			}

			return value.Substring(0, maxLength) + "...";
		}

		static string SanitizeFileName(string fileName)
		{
			if (string.IsNullOrEmpty(fileName))
			{
				return "unnamed";
			}

			char[] invalidChars = Path.GetInvalidFileNameChars();
			char[] chars = fileName.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				if (Array.IndexOf(invalidChars, chars[i]) >= 0)
				{
					chars[i] = '_';
				}
			}

			return new string(chars).Trim();
		}
	}
}
