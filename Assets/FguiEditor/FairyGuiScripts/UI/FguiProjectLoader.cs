using System;
using System.Collections.Generic;
using System.IO;
using FairyGUI.Utils;
using UnityEngine;

namespace FairyGUI
{
	/// <summary>
	/// Loads a FairyGUI project root, merges branch resources, and creates per-package data for UIPackage.
	/// </summary>
	public sealed class FguiProjectLoader
	{
		public sealed class ProjectResourceData
		{
			public string type;
			public string id;
			public string name;
			public string path;
			public string relativeFile;
			public string absoluteFile;
			public string branchTag;
			public bool exported;
			public int width;
			public int height;
			public Rect? scale9Grid;
			public bool scaleByTile;
			public int tileGridIndice;
		}

		public sealed class ProjectPackageData
		{
			public string projectRootDirectory;
			public string packageDirectory;
			public string packageId;
			public string packageName;
			public string activeBranchTag;
			public readonly List<ProjectResourceData> resources = new List<ProjectResourceData>();
		}

		readonly string _projectRootDirectory;
		readonly string _activeBranchTag;
		readonly Dictionary<string, ProjectPackageData> _packagesById;
		readonly Dictionary<string, ProjectPackageData> _packagesByName;
		readonly HashSet<string> _loadedPackageIds;
		readonly List<string> _branchTags;

		FguiProjectLoader(string projectRootDirectory, string activeBranchTag)
		{
			_projectRootDirectory = Path.GetFullPath(projectRootDirectory);
			_activeBranchTag = string.IsNullOrEmpty(activeBranchTag) ? null : activeBranchTag;
			_packagesById = new Dictionary<string, ProjectPackageData>();
			_packagesByName = new Dictionary<string, ProjectPackageData>(StringComparer.OrdinalIgnoreCase);
			_loadedPackageIds = new HashSet<string>();
			_branchTags = new List<string>();
		}

		public string projectRootDirectory
		{
			get { return _projectRootDirectory; }
		}

		public string activeBranchTag
		{
			get { return _activeBranchTag; }
		}

		public IList<string> branchTags
		{
			get { return _branchTags.AsReadOnly(); }
		}

		public static FguiProjectLoader LoadProject(string projectRootDirectory, string activeBranchTag)
		{
			if (string.IsNullOrEmpty(projectRootDirectory))
				throw new ArgumentNullException("projectRootDirectory");
			if (!Directory.Exists(projectRootDirectory))
				throw new DirectoryNotFoundException("FairyGUI project root not found: " + projectRootDirectory);

			FguiProjectLoader loader = new FguiProjectLoader(projectRootDirectory, activeBranchTag);
			loader.ScanPackages();
			loader.LoadAllPackages();
			return loader;
		}

		public UIPackage GetPackage(string packageNameOrId)
		{
			ProjectPackageData data;
			if (_packagesByName.TryGetValue(packageNameOrId, out data)
				|| _packagesById.TryGetValue(packageNameOrId, out data))
			{
				EnsurePackageLoaded(data);
				return UIPackage.GetById(data.packageId);
			}

			return null;
		}

		public void LoadAllPackages()
		{
			foreach (ProjectPackageData packageData in _packagesById.Values)
				EnsurePackageLoaded(packageData);
		}

		void EnsurePackageLoaded(ProjectPackageData packageData)
		{
			if (packageData == null || string.IsNullOrEmpty(packageData.packageId))
				return;
			if (_loadedPackageIds.Contains(packageData.packageId))
				return;

			UIPackage existing = UIPackage.GetById(packageData.packageId);
			if (existing != null)
			{
				_loadedPackageIds.Add(packageData.packageId);
				return;
			}

			UIPackage.AddPackage(packageData);
			_loadedPackageIds.Add(packageData.packageId);
		}

		void ScanPackages()
		{
			ScanMainPackages();
			ScanBranchPackages();
		}

		void ScanMainPackages()
		{
			string assetsDirectory = Path.Combine(_projectRootDirectory, "assets");
			if (!Directory.Exists(assetsDirectory))
				throw new DirectoryNotFoundException("FairyGUI assets directory not found: " + assetsDirectory);

			string[] packageDirectories = Directory.GetDirectories(assetsDirectory);
			foreach (string packageDirectory in packageDirectories)
			{
				string packageXmlPath = Path.Combine(packageDirectory, "package.xml");
				if (!File.Exists(packageXmlPath))
					continue;

				string packageName = Path.GetFileName(packageDirectory);
				XML xml = new XML(File.ReadAllText(packageXmlPath));
				string packageId = xml.GetAttribute("id");
				if (string.IsNullOrEmpty(packageId))
				{
					Debug.LogWarning("FairyGUI: package id missing in '" + packageXmlPath + "'");
					continue;
				}

				ProjectPackageData packageData = new ProjectPackageData();
				packageData.projectRootDirectory = _projectRootDirectory;
				packageData.packageDirectory = packageDirectory;
				packageData.packageId = packageId;
				packageData.packageName = packageName;
				packageData.activeBranchTag = _activeBranchTag;

				_packagesById[packageId] = packageData;
				_packagesByName[packageName] = packageData;

				AppendResources(packageData, xml.GetNode("resources"), packageDirectory, null);
			}
		}

		void ScanBranchPackages()
		{
			string[] candidateDirectories = Directory.GetDirectories(_projectRootDirectory, "assets_*", SearchOption.TopDirectoryOnly);
			foreach (string branchAssetsDirectory in candidateDirectories)
			{
				string directoryName = Path.GetFileName(branchAssetsDirectory);
				if (string.IsNullOrEmpty(directoryName) || !directoryName.StartsWith("assets_", StringComparison.OrdinalIgnoreCase))
					continue;

				string branchTag = directoryName.Substring("assets_".Length);
				if (string.IsNullOrEmpty(branchTag))
					continue;
				if (!_branchTags.Contains(branchTag))
					_branchTags.Add(branchTag);

				string[] packageDirectories = Directory.GetDirectories(branchAssetsDirectory);
				foreach (string packageDirectory in packageDirectories)
				{
					string packageName = Path.GetFileName(packageDirectory);
					string branchPackageXmlPath = Path.Combine(packageDirectory, "package_branch.xml");
					if (!File.Exists(branchPackageXmlPath))
						continue;

					ProjectPackageData packageData;
					if (!_packagesByName.TryGetValue(packageName, out packageData))
					{
						Debug.LogWarning("FairyGUI: branch package '" + packageName + "' has no main package in project root.");
						continue;
					}

					XML xml = new XML(File.ReadAllText(branchPackageXmlPath));
					AppendResources(packageData, xml.GetNode("resources"), packageDirectory, branchTag);
				}
			}
		}

		void AppendResources(ProjectPackageData packageData, XML resourcesNode, string packageDirectory, string branchTag)
		{
			if (resourcesNode == null)
				return;

			XMLList resources = resourcesNode.Elements();
			foreach (XML resourceXml in resources)
			{
				if (resourceXml == null || resourceXml.name == "folder")
					continue;

				string id = resourceXml.GetAttribute("id");
				string name = resourceXml.GetAttribute("name");
				if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
					continue;

				ProjectResourceData resourceData = new ProjectResourceData();
				resourceData.type = resourceXml.name;
				resourceData.id = id;
				resourceData.name = name;
				resourceData.path = resourceXml.GetAttribute("path") ?? "/";
				resourceData.relativeFile = CombineRelativeFile(resourceData.path, resourceData.name);
				resourceData.absoluteFile = Path.Combine(packageDirectory, resourceData.relativeFile.Replace('/', Path.DirectorySeparatorChar));
				resourceData.branchTag = branchTag;
				resourceData.exported = resourceXml.GetAttributeBool("exported");
				resourceData.tileGridIndice = resourceXml.GetAttributeInt("gridTile");

				string size = resourceXml.GetAttribute("size");
				if (!string.IsNullOrEmpty(size))
				{
					string[] arr = size.Split(',');
					if (arr.Length >= 2)
					{
						int.TryParse(arr[0], out resourceData.width);
						int.TryParse(arr[1], out resourceData.height);
					}
				}
				else
				{
					int.TryParse(resourceXml.GetAttribute("width"), out resourceData.width);
					int.TryParse(resourceXml.GetAttribute("height"), out resourceData.height);
				}

				string scale = resourceXml.GetAttribute("scale");
				if (scale == "tile")
					resourceData.scaleByTile = true;
				else if (scale == "9grid")
				{
					string[] arr = resourceXml.GetAttributeArray("scale9grid");
					if (arr != null && arr.Length >= 4)
					{
						Rect rect = new Rect();
						float value;
						if (float.TryParse(arr[0], out value))
							rect.x = value;
						if (float.TryParse(arr[1], out value))
							rect.y = value;
						if (float.TryParse(arr[2], out value))
							rect.width = value;
						if (float.TryParse(arr[3], out value))
							rect.height = value;
						resourceData.scale9Grid = rect;
					}
				}

				packageData.resources.Add(resourceData);
			}
		}

		static string CombineRelativeFile(string resourcePath, string fileName)
		{
			string normalizedPath = NormalizeResourcePath(resourcePath);
			if (string.IsNullOrEmpty(normalizedPath))
				return fileName;

			return normalizedPath + "/" + fileName;
		}

		static string NormalizeResourcePath(string resourcePath)
		{
			if (string.IsNullOrEmpty(resourcePath))
				return string.Empty;

			return resourcePath.Replace('\\', '/').Trim('/');
		}
	}
}


