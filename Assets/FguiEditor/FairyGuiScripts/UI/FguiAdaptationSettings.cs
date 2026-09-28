using System;
using System.IO;
using UnityEngine;

namespace FairyGUI
{
	/// <summary>
	/// Mirrors the FairyGUI editor's screen adaptation settings (settings/Adaptation.json).
	/// The render server uses it so a preview is scaled exactly like the project it renders:
	/// the scale factor is derived from the simulated screen size and these design settings.
	/// </summary>
	public sealed class FguiAdaptationSettings
	{
		public const int DefaultDesignResolutionX = 1920;
		public const int DefaultDesignResolutionY = 1080;

		/// <summary>True when the values came from a project file; false for the built-in fallback.</summary>
		public bool loadedFromProject;

		/// <summary>Path the settings were read from (empty when the built-in fallback is used).</summary>
		public string sourceFile;

		public UIContentScaler.ScaleMode scaleMode = UIContentScaler.ScaleMode.ScaleWithScreenSize;
		public UIContentScaler.ScreenMatchMode screenMatchMode = UIContentScaler.ScreenMatchMode.MatchWidthOrHeight;
		public int designResolutionX = DefaultDesignResolutionX;
		public int designResolutionY = DefaultDesignResolutionY;
		public bool ignoreOrientation;
		public float constantScaleFactor = 1;

		public static FguiAdaptationSettings CreateDefault()
		{
			return new FguiAdaptationSettings();
		}

		/// <summary>
		/// Load settings/Adaptation.json from a FairyGUI project root. Falls back to the
		/// built-in 1920x1080 / ScaleWithScreenSize / MatchWidthOrHeight defaults when the
		/// file is missing or unreadable, so rendering never fails because of it.
		/// </summary>
		public static FguiAdaptationSettings Load(string projectRootDirectory)
		{
			FguiAdaptationSettings settings = CreateDefault();
			if (string.IsNullOrEmpty(projectRootDirectory))
				return settings;

			string settingsFile;
			try
			{
				settingsFile = Path.Combine(Path.GetFullPath(projectRootDirectory), "settings", "Adaptation.json");
			}
			catch (Exception e)
			{
				Debug.LogWarning("Could not resolve adaptation settings path for '" + projectRootDirectory + "' (" + e.Message + ")");
				return settings;
			}

			if (!File.Exists(settingsFile))
				return settings;

			try
			{
				AdaptationJson json = JsonUtility.FromJson<AdaptationJson>(File.ReadAllText(settingsFile));
				if (json == null)
					return settings;

				settings.loadedFromProject = true;
				settings.sourceFile = settingsFile;
				settings.scaleMode = ParseScaleMode(json.scaleMode, settings.scaleMode);
				settings.screenMatchMode = ParseScreenMatchMode(json.screenMathMode, settings.screenMatchMode);
				if (json.designResolutionX > 0)
					settings.designResolutionX = json.designResolutionX;
				if (json.designResolutionY > 0)
					settings.designResolutionY = json.designResolutionY;
				settings.ignoreOrientation = json.ignoreOrientation;
				if (json.constantScaleFactor > 0)
					settings.constantScaleFactor = json.constantScaleFactor;
			}
			catch (Exception e)
			{
				Debug.LogWarning("Could not read adaptation settings from " + settingsFile + " (" + e.Message + ")");
			}

			return settings;
		}

		public static UIContentScaler.ScaleMode ParseScaleMode(string value, UIContentScaler.ScaleMode fallback)
		{
			if (string.IsNullOrEmpty(value))
				return fallback;

			try
			{
				return (UIContentScaler.ScaleMode)Enum.Parse(typeof(UIContentScaler.ScaleMode), value.Trim(), true);
			}
			catch (ArgumentException)
			{
				Debug.LogWarning("Unknown scaleMode '" + value + "', using " + fallback);
				return fallback;
			}
		}

		public static UIContentScaler.ScreenMatchMode ParseScreenMatchMode(string value, UIContentScaler.ScreenMatchMode fallback)
		{
			if (string.IsNullOrEmpty(value))
				return fallback;

			try
			{
				return (UIContentScaler.ScreenMatchMode)Enum.Parse(typeof(UIContentScaler.ScreenMatchMode), value.Trim(), true);
			}
			catch (ArgumentException)
			{
				Debug.LogWarning("Unknown screenMatchMode '" + value + "', using " + fallback);
				return fallback;
			}
		}

		/// <summary>
		/// Computes the content scale factor for a screen, replicating UIContentScaler.ApplyChange
		/// but against an explicit screen size instead of the real Unity player window.
		/// </summary>
		public float ComputeScaleFactor(int screenWidth, int screenHeight)
		{
			float factor;

			if (scaleMode == UIContentScaler.ScaleMode.ScaleWithScreenSize)
			{
				int designX = designResolutionX;
				int designY = designResolutionY;
				if (designX <= 0 || designY <= 0)
					return 1;

				if (!ignoreOrientation
					&& (screenWidth > screenHeight && designX < designY
						|| screenWidth < screenHeight && designX > designY))
				{
					//Scale should not change when the screen orientation flips.
					int swap = designX;
					designX = designY;
					designY = swap;
				}

				if (screenMatchMode == UIContentScaler.ScreenMatchMode.MatchWidth)
					factor = (float)screenWidth / designX;
				else if (screenMatchMode == UIContentScaler.ScreenMatchMode.MatchHeight)
					factor = (float)screenHeight / designY;
				else
					factor = Mathf.Min((float)screenWidth / designX, (float)screenHeight / designY);
			}
			else if (scaleMode == UIContentScaler.ScaleMode.ConstantPhysicalSize)
			{
				float dpi = Screen.dpi;
				if (dpi == 0)
					dpi = 96;
				factor = dpi / 96f;
			}
			else
			{
				factor = constantScaleFactor > 0 ? constantScaleFactor : 1;
			}

			if (factor > 10)
				factor = 10;
			if (factor <= 0.001f)
				factor = 0.001f;

			return factor;
		}

		[Serializable]
		sealed class AdaptationJson
		{
			//The editor writes "screenMathMode"; Unity's own enum is ScreenMatchMode.
			public string scaleMode;
			public string screenMathMode;
			public int designResolutionX;
			public int designResolutionY;
			public bool ignoreOrientation;
			public float constantScaleFactor;
		}
	}
}
