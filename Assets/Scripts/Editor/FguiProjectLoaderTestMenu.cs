using System;
using FairyGUI;
using UnityEditor;
using UnityEngine;

namespace FguiRenderServer.Editor
{
	public static class FguiProjectLoaderTestMenu
	{
		const string DefaultProjectRoot = @"D:\ProjectGit\AirLegion\fgui_airLegion";
		const string DefaultPackageName = "BattleUI";
		const string DefaultComponentName = "main_FormationSelect.xml";
		const string DefaultBranchTag = "eng";

		[MenuItem("Tools/Fgui Render/Smoke Test")]
		public static void LoadAirLegionBattleUi()
		{
			try
			{
				UIPackage.RemoveAllPackages(true);
				FguiProjectLoader loader = FguiProjectLoader.LoadProject(DefaultProjectRoot, DefaultBranchTag);
				UIPackage pkg = loader.GetPackage(DefaultPackageName);
				if (pkg == null)
				{
					Debug.LogError("FairyGUI: smoke test failed, package not loaded - " + DefaultPackageName);
					return;
				}

				GObject panel = UIPackage.CreateObject(DefaultPackageName, DefaultComponentName);
				Stage.Instantiate();
				if (panel == null)
				{
					Debug.LogError("FairyGUI: smoke test failed, component not created - " + DefaultComponentName);
					return;
				}
				
				GRoot.inst.AddChild(panel);
				Debug.Log(string.Format(
					"FairyGUI: smoke test success. package={0}, packageId={1}, branch={2}, objectType={3}",
					pkg.name,
					pkg.id,
					DefaultBranchTag,
					panel.GetType().Name));
			}
			catch (Exception ex)
			{
				Debug.LogException(ex);
			}
		}
	}
}

