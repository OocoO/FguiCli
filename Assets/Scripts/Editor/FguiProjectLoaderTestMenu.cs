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
		const string FontAlignProjectRoot = @"D:\ProjectGit\Mainland\fgui_idle_dev_2022\FGUIProject";
		const string FontAlignEditorOutputRoot = @"D:\Project\FguiCli\Temp\fontAlignEditor";
		// 与 CLI 打包渲染输出 (Temp\fontAlignCli) 文件名保持一致，方便左右并排对比
		static readonly string[][] FontAlignTargets =
		{
			// { packageName, componentName, outFileName }
			new[] { "Tips", "confirmBtn.xml", "Tips__confirmBtn.png" },
			new[] { "Tips", "YesBtnText.xml", "Tips__YesBtnText.png" },
			new[] { "Tips", "YesBtn.xml", "Tips__YesBtn.png" },
			new[] { "Tips", "YesBtn_Large.xml", "Tips__YesBtn_Large.png" },
			new[] { "AccountInfo", "confirmBtn.xml", "AccountInfo__confirmBtn_richtext.png" },
			new[] { "Lottery", "ConfirmBtn.xml", "Lottery__ConfirmBtn_defaultFont.png" },
			new[] { "SoldierCultivate", "confirmBtn.xml", "SoldierCultivate__confirmBtn.png" },
		};
		static readonly string[] KnownPackageRoots =
		{
			"assets",
			"assets_eng",
			"assets_hk",
			"assets_mo",
			"assets_tw",
			"assets_zh_tc",
		};

		// 一次性诊断:Unity 侧 OS 字体枚举与 CreateDynamicFontFromOSFont 对思源黑体各名字的解析行为
		[MenuItem("Tools/Fgui Render/TTF Font Probe")]
		public static void TtfFontProbe()
		{
			var sb = new StringBuilder();
			const string heavyPath = FontAlignProjectRoot + @"\assets\PublicResources\NewStyle\DiyFont\SourceHanSansCN-Heavy.ttf";
			const string boldPath = FontAlignProjectRoot + @"\assets\PublicResources\NewStyle\DiyFont\SourceHanSansCN-Bold.ttf";

			string[] osNames = Font.GetOSInstalledFontNames();
			sb.AppendLine("GetOSInstalledFontNames total=" + (osNames == null ? -1 : osNames.Length));
			if (osNames != null)
			{
				foreach (string n in osNames)
				{
					if (n.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0
						|| n.IndexOf("思源", StringComparison.Ordinal) >= 0
						|| n.IndexOf("Han", StringComparison.OrdinalIgnoreCase) >= 0)
						sb.AppendLine("  os> [" + n + "]");
				}
			}

			foreach (string path in new[] { heavyPath, boldPath })
			{
				sb.AppendLine("---- " + Path.GetFileName(path));
				List<string> fams = ProjectTtfFontLoader.ParseFamilyNames(path);
				sb.AppendLine("  parsed families> " + string.Join(" / ", fams.ConvertAll(f => "[" + f + "]").ToArray()));
				foreach (string fam in fams)
					ProbeOneFont(sb, fam);
			}
			ProbeOneFont(sb, "Microsoft YaHei");
			//对照:系统回退链路最终用的内置字体基线
			ProbeFont(sb, "builtinArial", Resources.GetBuiltinResource<Font>("Arial.ttf"));

			string outTxt = Path.Combine(Application.dataPath, "../Temp/fontProbe.txt");
			File.WriteAllText(outTxt, sb.ToString());
			Debug.Log("TTF Font Probe written to " + outTxt + "\n" + sb);
		}

		static void ProbeOneFont(StringBuilder sb, string familyName)
		{
			Font f = Font.CreateDynamicFontFromOSFont(familyName, 33);
			if (f == null)
			{
				sb.AppendLine("  Create(" + familyName + ") = null");
				return;
			}
			ProbeFont(sb, familyName, f);
		}

		static void ProbeFont(StringBuilder sb, string label, Font f)
		{
			// 拉丁字符在思源/雅黑/系统回退字体下度量差异明显，全角汉字无法区分
			const string probe = "Wi@0确认";
			f.RequestCharactersInTexture(probe, 33, FontStyle.Normal);
			var line = new StringBuilder("  [" + label + "] name='" + f.name + "'");
			for (int i = 0; i < probe.Length; i++)
			{
				char ch = probe[i];
				CharacterInfo ci;
				if (f.GetCharacterInfo(ch, out ci, 33, FontStyle.Normal))
					line.Append(" '" + ch + "'=" + ci.advance + "/" + ci.glyphWidth + "x" + ci.glyphHeight);
				else
					line.Append(" '" + ch + "'=missing");
			}
			sb.AppendLine(line.ToString());
		}

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

		// 排查字体居中 bug：走与 CLI 打包版完全相同的 StartRenderRequest 管线，
		// 在 Editor 内批量渲染 Mainland 项目的确认类按钮。
		// 输出到 Temp\fontAlignEditor，文件名与 Temp\fontAlignCli（打包版输出）一一对应，直接并排对比。
		[MenuItem("Tools/Fgui Render/Font Align Compare (Mainland Buttons)")]
		public static void FontAlignCompareMenu()
		{
			var component = Object.FindObjectOfType<FguiRenderServerBehaviour>();
			if (component == null)
			{
				Debug.LogError("FairyGUI: font align compare failed, FguiRenderServerBehaviour not found in scene.");
				return;
			}

			component.StartCoroutine(RunFontAlignCompare(component));
		}

		static System.Collections.IEnumerator RunFontAlignCompare(FguiRenderServerBehaviour component)
		{
			if (!Directory.Exists(FontAlignProjectRoot))
			{
				Debug.LogError("FairyGUI: project root not found - " + FontAlignProjectRoot);
				yield break;
			}

			Directory.CreateDirectory(FontAlignEditorOutputRoot);

			int successCount = 0;
			foreach (string[] target in FontAlignTargets)
			{
				string packageName = target[0];
				string componentName = target[1];
				string pngPath = Path.Combine(FontAlignEditorOutputRoot, target[2]);

				FguiRenderServerBehaviour.RenderResult result = null;
				component.StartRenderRequest(
					new FguiRenderServerBehaviour.RenderRequest
					{
						projectRootDir = FontAlignProjectRoot,
						packageName = packageName,
						componentName = componentName,
						outPng = pngPath,
						branchTag = string.Empty,
					},
					r => result = r);

				while (result == null)
				{
					yield return null;
				}

				if (result.ok)
				{
					successCount += 1;
					Debug.Log(string.Format(
						"FairyGUI: font align render ok. {0}/{1} -> {2} ({3}x{4}, {5}ms)",
						packageName, componentName, pngPath, result.width, result.height, result.durationMs));
				}
				else
				{
					Debug.LogError(string.Format(
						"FairyGUI: font align render failed. {0}/{1} - {2}",
						packageName, componentName, result.message));
				}
			}

			AssetDatabase.Refresh();
			Debug.Log(string.Format(
				"FairyGUI: font align compare finished. success={0}/{1}, editorOutput={2}, cliOutput={3}",
				successCount, FontAlignTargets.Length, FontAlignEditorOutputRoot,
				Path.Combine(Path.GetDirectoryName(FullTestOutputRoot), "fontAlignCli")));
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
	
					var screenshot = FguiRenderServerBehaviour.CaptureScreen();
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
					Object.Destroy(screenshot);
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
