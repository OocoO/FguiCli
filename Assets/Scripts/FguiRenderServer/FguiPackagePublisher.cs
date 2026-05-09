using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace FguiRenderServer
{
    public static class FguiPackagePublisher
{
    private const int AtlasPadding = 2;
    private const int MaxAtlasSize = 2048;

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

        string descriptorPath;
        if (TryFindRuntimeDescriptor(fullPackageDir, out descriptorPath))
        {
            CopyDirectoryRecursive(fullPackageDir, fullOutputDir);
            EnsureRuntimeDescriptorExists(fullOutputDir);
            return;
        }

        PackageSource package = PackageSource.Load(fullPackageDir);
        PackageDirectoryContext.Current = package.PackageDirectory;
        try
        {
            HashSet<string> includedIds = CollectIncludedResourceIds(package);
            List<ResourceEntry> includedResources = package.Resources
                .Where(resource => includedIds.Contains(resource.Id))
                .ToList();

            FguiOfficialRuntimePackageBuilder.OfficialRuntimePublishResult result =
                FguiOfficialRuntimePackageBuilder.Build(package, includedResources, fullOutputDir);
            Debug.Log($"Published official FairyGUI package '{result.PackageName}' to {result.OutputDirectory}. " +
                      $"Items={result.ItemCount}, Atlases={result.AtlasCount}");
#if UNITY_EDITOR
            AssetDatabase.Refresh();
#endif
        }
        finally
        {
            PackageDirectoryContext.Current = null;
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

    private static string EnsureRuntimeDescriptorExists(string dir)
    {
        string descriptorPath;
        if (!TryFindRuntimeDescriptor(dir, out descriptorPath))
        {
            throw new InvalidOperationException("Official exporter completed but no '*_fui.bytes' file was found in: " + dir);
        }

        string packageName = Path.GetFileNameWithoutExtension(descriptorPath);
        return packageName.EndsWith("_fui", StringComparison.OrdinalIgnoreCase)
            ? packageName.Substring(0, packageName.Length - 4)
            : packageName;
    }

#if UNITY_EDITOR
#endif

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
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

    internal static List<AtlasImageInput> LoadIncludedImages(PackageSource package, List<ResourceEntry> images)
    {
        List<AtlasImageInput> loadedImages = new List<AtlasImageInput>();
        int maxExplicitAtlasIndex = images
            .Select(resource => TryParseAtlasIndex(resource.AtlasTag))
            .Where(index => index.HasValue)
            .Select(index => index.Value)
            .DefaultIfEmpty(0)
            .Max();

        int nextSyntheticAtlasIndex = Math.Max(1, maxExplicitAtlasIndex + 1);

        foreach (ResourceEntry image in images)
        {
            string imagePath = image.GetSourcePath(package.PackageDirectory);
            if (!File.Exists(imagePath))
            {
                throw new FileNotFoundException("Cannot find image resource file", imagePath);
            }

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            byte[] imageBytes = File.ReadAllBytes(imagePath);
            if (!texture.LoadImage(imageBytes, false))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("Failed to decode image file: " + imagePath);
            }

            int atlasIndex;
            int? explicitAtlasIndex = TryParseAtlasIndex(image.AtlasTag);
            if (explicitAtlasIndex.HasValue)
            {
                atlasIndex = explicitAtlasIndex.Value;
            }
            else if (string.Equals(image.AtlasTag, "alone_npot", StringComparison.OrdinalIgnoreCase))
            {
                atlasIndex = nextSyntheticAtlasIndex++;
            }
            else if (string.IsNullOrWhiteSpace(image.AtlasTag))
            {
                atlasIndex = 0;
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("Unsupported atlas value '" + image.AtlasTag + "' for image resource: " + image.Id);
            }

            image.SetSize(texture.width, texture.height);
            loadedImages.Add(new AtlasImageInput(image, texture, atlasIndex));
        }

        return loadedImages;
    }

    internal static List<AtlasOutput> BuildAtlases(List<AtlasImageInput> atlasInputs)
    {
        List<AtlasOutput> atlases = new List<AtlasOutput>();

        foreach (IGrouping<int, AtlasImageInput> group in atlasInputs.GroupBy(input => input.AtlasIndex).OrderBy(group => group.Key))
        {
            List<AtlasImageInput> images = group
                .OrderByDescending(input => Math.Max(input.Width, input.Height))
                .ThenByDescending(input => input.Width * input.Height)
                .ThenBy(input => input.Resource.Id, StringComparer.Ordinal)
                .ToList();

            AtlasLayout layout = TryPackAtlas(images, group.Key);
            byte[] atlasPng = RenderAtlas(layout);
            atlases.Add(new AtlasOutput(group.Key, layout.Width, layout.Height, atlasPng, layout.Placements));
        }

        foreach (AtlasImageInput input in atlasInputs)
        {
            UnityEngine.Object.DestroyImmediate(input.Texture);
        }

        return atlases;
    }

    private static AtlasLayout TryPackAtlas(List<AtlasImageInput> images, int atlasIndex)
    {
        if (images.Count == 0)
        {
            return new AtlasLayout(32, 32, new List<AtlasPlacement>(), images);
        }

        int maxItemSide = images.Max(image => Math.Max(image.Width, image.Height));
        int minPow = Math.Max(32, NextPowerOfTwo(maxItemSide));
        long estimatedArea = images.Sum(image => (long)(image.Width + AtlasPadding) * (image.Height + AtlasPadding));

        List<Vector2Int> candidates = new List<Vector2Int>();
        for (int width = minPow; width <= MaxAtlasSize; width <<= 1)
        {
            for (int height = minPow; height <= MaxAtlasSize; height <<= 1)
            {
                long area = (long)width * height;
                if (area < estimatedArea)
                {
                    continue;
                }

                candidates.Add(new Vector2Int(width, height));
            }
        }

        foreach (Vector2Int candidate in candidates.OrderBy(size => (long)size.x * size.y)
                     .ThenBy(size => Math.Abs(size.x - size.y))
                     .ThenBy(size => size.x)
                     .ThenBy(size => size.y))
        {
            if (TryShelfPack(images, atlasIndex, candidate.x, candidate.y, out AtlasLayout layout))
            {
                return layout;
            }
        }

        throw new InvalidOperationException("Failed to pack atlas " + atlasIndex.ToString(CultureInfo.InvariantCulture) + " within " + MaxAtlasSize + "x" + MaxAtlasSize + ".");
    }

    private static bool TryShelfPack(List<AtlasImageInput> images, int atlasIndex, int atlasWidth, int atlasHeight, out AtlasLayout layout)
    {
        List<Shelf> shelves = new List<Shelf>();
        List<AtlasPlacement> placements = new List<AtlasPlacement>(images.Count);

        foreach (AtlasImageInput image in images)
        {
            PlacementChoice? bestChoice = null;
            int nextShelfY = shelves.Count == 0 ? 0 : shelves[shelves.Count - 1].Y + shelves[shelves.Count - 1].Height;

            EvaluatePlacementOption(image, atlasWidth, atlasHeight, shelves, nextShelfY, rotated: false, ref bestChoice);
            if (image.Width != image.Height)
            {
                EvaluatePlacementOption(image, atlasWidth, atlasHeight, shelves, nextShelfY, rotated: true, ref bestChoice);
            }

            if (!bestChoice.HasValue)
            {
                layout = null;
                return false;
            }

            PlacementChoice choice = bestChoice.Value;
            if (choice.UseExistingShelf)
            {
                Shelf shelf = shelves[choice.ShelfIndex];
                placements.Add(new AtlasPlacement(image.Resource.Id, atlasIndex, shelf.X, shelf.Y, choice.Width, choice.Height, choice.Rotated));
                shelves[choice.ShelfIndex] = new Shelf(shelf.Y, shelf.Height, shelf.X + choice.Width + AtlasPadding);
            }
            else
            {
                Shelf newShelf = new Shelf(nextShelfY, choice.Height, choice.Width + AtlasPadding);
                shelves.Add(newShelf);
                placements.Add(new AtlasPlacement(image.Resource.Id, atlasIndex, 0, nextShelfY, choice.Width, choice.Height, choice.Rotated));
            }
        }

        layout = new AtlasLayout(atlasWidth, atlasHeight, placements, images);
        return true;
    }

    private static void EvaluatePlacementOption(
        AtlasImageInput image,
        int atlasWidth,
        int atlasHeight,
        List<Shelf> shelves,
        int nextShelfY,
        bool rotated,
        ref PlacementChoice? bestChoice)
    {
        int placedWidth = rotated ? image.Height : image.Width;
        int placedHeight = rotated ? image.Width : image.Height;
        if (placedWidth > atlasWidth || placedHeight > atlasHeight)
        {
            return;
        }

        for (int i = 0; i < shelves.Count; i++)
        {
            Shelf shelf = shelves[i];
            if (placedHeight > shelf.Height)
            {
                continue;
            }

            if (shelf.X + placedWidth > atlasWidth)
            {
                continue;
            }

            PlacementChoice choice = new PlacementChoice(
                i,
                placedWidth,
                placedHeight,
                rotated,
                useExistingShelf: true,
                score: ((long)shelf.Y << 32) + ((long)(shelf.Height - placedHeight) << 16) + shelf.X);

            if (!bestChoice.HasValue || choice.Score < bestChoice.Value.Score)
            {
                bestChoice = choice;
            }
        }

        if (nextShelfY + placedHeight > atlasHeight)
        {
            return;
        }

        PlacementChoice newShelfChoice = new PlacementChoice(
            shelfIndex: shelves.Count,
            placedWidth,
            placedHeight,
            rotated,
            useExistingShelf: false,
            score: ((long)nextShelfY << 32) + ((long)placedHeight << 16));

        if (!bestChoice.HasValue || newShelfChoice.Score < bestChoice.Value.Score)
        {
            bestChoice = newShelfChoice;
        }
    }

    private static byte[] RenderAtlas(AtlasLayout layout)
    {
        Texture2D atlasTexture = new Texture2D(layout.Width, layout.Height, TextureFormat.RGBA32, false, true);
        Color32[] atlasPixels = Enumerable.Repeat(new Color32(0, 0, 0, 0), layout.Width * layout.Height).ToArray();

        foreach (AtlasPlacement placement in layout.Placements)
        {
            AtlasImageInput input = layout.FindInput(placement.ResourceId);
            CopyPixels(input.Texture, atlasPixels, layout.Width, placement);
        }

        atlasTexture.SetPixels32(atlasPixels);
        atlasTexture.Apply(false, false);
        byte[] pngBytes = atlasTexture.EncodeToPNG();
        UnityEngine.Object.DestroyImmediate(atlasTexture);
        return pngBytes;
    }

    private static void CopyPixels(Texture2D source, Color32[] atlasPixels, int atlasWidth, AtlasPlacement placement)
    {
        Color32[] sourcePixels = source.GetPixels32();
        int sourceWidth = source.width;
        int sourceHeight = source.height;

        for (int y = 0; y < sourceHeight; y++)
        {
            for (int x = 0; x < sourceWidth; x++)
            {
                int sourceIndex = y * sourceWidth + x;

                int targetX;
                int targetY;
                if (placement.Rotated)
                {
                    targetX = placement.X + (sourceHeight - 1 - y);
                    targetY = placement.Y + x;
                }
                else
                {
                    targetX = placement.X + x;
                    targetY = placement.Y + y;
                }

                int targetIndex = targetY * atlasWidth + targetX;
                atlasPixels[targetIndex] = sourcePixels[sourceIndex];
            }
        }
    }

    private static HashSet<string> CollectIncludedResourceIds(PackageSource package)
    {
        Queue<string> pending = new Queue<string>(package.Resources.Where(resource => resource.Exported).Select(resource => resource.Id));
        HashSet<string> includedIds = new HashSet<string>(StringComparer.Ordinal);

        while (pending.Count > 0)
        {
            string resourceId = pending.Dequeue();
            if (!includedIds.Add(resourceId))
            {
                continue;
            }

            if (!package.ResourcesById.TryGetValue(resourceId, out ResourceEntry resource) || resource.Kind != ResourceKind.Component)
            {
                continue;
            }

            foreach (string dependencyId in resource.GetDependencyIds(package.PackageId))
            {
                if (package.ResourcesById.ContainsKey(dependencyId))
                {
                    pending.Enqueue(dependencyId);
                }
            }
        }

        return includedIds;
    }

    private static int? TryParseAtlasIndex(string atlasValue)
    {
        if (string.IsNullOrWhiteSpace(atlasValue))
        {
            return null;
        }

        return int.TryParse(atlasValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static string SerializeXml(XElement element)
    {
        XmlWriterSettings settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.None
        };

        StringBuilder builder = new StringBuilder();
        using (StringWriter writer = new StringWriter(builder, CultureInfo.InvariantCulture))
        using (XmlWriter xmlWriter = XmlWriter.Create(writer, settings))
        {
            element.WriteTo(xmlWriter);
        }

        return builder.ToString();
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

    internal sealed class PackageSource
    {
        public string PackageDirectory { get; }
        public string PackageId { get; }
        public string PackageName { get; }
        public IReadOnlyList<XAttribute> RootAttributes { get; }
        public List<ResourceEntry> Resources { get; }
        public Dictionary<string, ResourceEntry> ResourcesById { get; }

        private PackageSource(string packageDirectory, string packageId, string packageName, IReadOnlyList<XAttribute> rootAttributes, List<ResourceEntry> resources)
        {
            PackageDirectory = packageDirectory;
            PackageId = packageId;
            PackageName = packageName;
            RootAttributes = rootAttributes;
            Resources = resources;
            ResourcesById = resources.ToDictionary(resource => resource.Id, StringComparer.Ordinal);
        }

        public static PackageSource Load(string packageDir)
        {
            string fullPackageDir = Path.GetFullPath(packageDir);
            string packageXmlPath = Path.Combine(fullPackageDir, "package.xml");
            if (!File.Exists(packageXmlPath))
            {
                throw new FileNotFoundException("Cannot find FairyGUI package.xml", packageXmlPath);
            }

            XDocument doc = XDocument.Load(packageXmlPath);
            XElement root = doc.Root ?? throw new InvalidOperationException("package.xml is missing root element.");
            XElement resourcesElement = root.Element("resources") ?? throw new InvalidOperationException("package.xml is missing <resources>.");

            string packageId = RequiredAttribute(root, "id");
            string packageName = root.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(packageName))
            {
                packageName = new DirectoryInfo(fullPackageDir).Name;
            }

            List<XAttribute> rootAttributes = root.Attributes()
                .Where(attribute => attribute.Name.LocalName != "name")
                .Select(attribute => new XAttribute(attribute))
                .ToList();

            List<ResourceEntry> resources = new List<ResourceEntry>();
            foreach (XElement element in resourcesElement.Elements())
            {
                string localName = element.Name.LocalName;
                if (localName != "component" && localName != "image")
                {
                    continue;
                }

                resources.Add(ResourceEntry.FromPackageElement(element));
            }

            return new PackageSource(fullPackageDir, packageId, packageName, rootAttributes, resources);
        }

        private static string RequiredAttribute(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute == null || string.IsNullOrWhiteSpace(attribute.Value))
            {
                throw new InvalidOperationException("Missing required attribute '" + name + "' on <" + element.Name.LocalName + ">.");
            }

            return attribute.Value;
        }
    }

    internal sealed class ResourceEntry
    {
        private static readonly string[] RemovedComponentAttributes = { "fileName", "bgColor", "exported" };

        private readonly List<XAttribute> _extraAttributes;
        private XDocument _componentDocument;
        private string _sanitizedComponentXml;
        private HashSet<string> _dependencyIds;
        private Vector2Int? _size;

        public ResourceKind Kind { get; }
        public string Id { get; }
        public string Name { get; }
        public string PathInPackage { get; }
        public bool Exported { get; }
        public string AtlasTag { get; }

        private ResourceEntry(ResourceKind kind, string id, string name, string pathInPackage, bool exported, string atlasTag, List<XAttribute> extraAttributes)
        {
            Kind = kind;
            Id = id;
            Name = name;
            PathInPackage = pathInPackage;
            Exported = exported;
            AtlasTag = atlasTag;
            _extraAttributes = extraAttributes;
        }

        public static ResourceEntry FromPackageElement(XElement element)
        {
            ResourceKind kind = element.Name.LocalName == "component" ? ResourceKind.Component : ResourceKind.Image;
            string id = RequiredAttribute(element, "id");
            string name = RequiredAttribute(element, "name");
            string path = element.Attribute("path")?.Value ?? "/";
            bool exported = string.Equals(element.Attribute("exported")?.Value, "true", StringComparison.OrdinalIgnoreCase);
            string atlasTag = element.Attribute("atlas")?.Value;

            List<XAttribute> extraAttributes = element.Attributes()
                .Where(attribute => attribute.Name.LocalName != "id"
                                    && attribute.Name.LocalName != "name"
                                    && attribute.Name.LocalName != "path"
                                    && attribute.Name.LocalName != "exported"
                                    && attribute.Name.LocalName != "atlas"
                                    && attribute.Name.LocalName != "size")
                .Select(attribute => new XAttribute(attribute))
                .ToList();

            ResourceEntry resource = new ResourceEntry(kind, id, name, path, exported, atlasTag, extraAttributes);
            string sizeText = element.Attribute("size")?.Value;
            if (!string.IsNullOrWhiteSpace(sizeText))
            {
                resource._size = ParseSize(sizeText);
            }

            return resource;
        }

        public XElement CreatePublishedPackageElement()
        {
            string elementName = Kind == ResourceKind.Component ? "component" : "image";
            XElement element = new XElement(elementName,
                new XAttribute("id", Id),
                new XAttribute("name", Path.GetFileNameWithoutExtension(Name)),
                new XAttribute("path", PathInPackage));

            if (_size.HasValue)
            {
                element.SetAttributeValue("size", _size.Value.x.ToString(CultureInfo.InvariantCulture) + "," + _size.Value.y.ToString(CultureInfo.InvariantCulture));
            }

            if (Exported)
            {
                element.SetAttributeValue("exported", "true");
            }

            foreach (XAttribute attribute in _extraAttributes)
            {
                element.SetAttributeValue(attribute.Name, attribute.Value);
            }

            return element;
        }

        public string GetSourcePath(string packageDirectory)
        {
            string relativePath = PathInPackage.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(packageDirectory, relativePath, Name));
        }

        public string GetSanitizedComponentXml()
        {
            if (Kind != ResourceKind.Component)
            {
                throw new InvalidOperationException("Only component resources have XML content: " + Id);
            }

            if (_sanitizedComponentXml != null)
            {
                return _sanitizedComponentXml;
            }

            XElement sanitizedRoot = LoadComponentDocument().Root ?? throw new InvalidOperationException("Component XML root is missing: " + Id);
            SanitizeComponentElement(sanitizedRoot);

            string sizeText = sanitizedRoot.Attribute("size")?.Value;
            if (!string.IsNullOrWhiteSpace(sizeText))
            {
                _size = ParseSize(sizeText);
            }

            _sanitizedComponentXml = SerializeXml(sanitizedRoot);
            return _sanitizedComponentXml;
        }

        public IEnumerable<string> GetDependencyIds(string packageId)
        {
            if (_dependencyIds != null)
            {
                return _dependencyIds;
            }

            _dependencyIds = new HashSet<string>(StringComparer.Ordinal);
            XElement root = LoadComponentDocument().Root;
            if (root == null)
            {
                return _dependencyIds;
            }

            Regex uiUrlRegex = new Regex(Regex.Escape("ui://" + packageId) + "([A-Za-z0-9]+)", RegexOptions.Compiled);
            foreach (XElement element in root.DescendantsAndSelf())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    if (string.Equals(attribute.Name.LocalName, "src", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        _dependencyIds.Add(attribute.Value.Trim());
                    }

                    foreach (Match match in uiUrlRegex.Matches(attribute.Value))
                    {
                        if (match.Success && match.Groups.Count > 1 && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                        {
                            _dependencyIds.Add(match.Groups[1].Value);
                        }
                    }
                }
            }

            return _dependencyIds;
        }

        public void SetSize(int width, int height)
        {
            _size = new Vector2Int(width, height);
        }

        private XDocument LoadComponentDocument()
        {
            if (_componentDocument != null)
            {
                return _componentDocument;
            }

            string packageDirectory = PackageDirectoryContext.Current ?? throw new InvalidOperationException("Package directory context is not set.");
            string componentPath = GetSourcePath(packageDirectory);
            if (!File.Exists(componentPath))
            {
                throw new FileNotFoundException("Cannot find component XML file", componentPath);
            }

            _componentDocument = XDocument.Load(componentPath);
            return _componentDocument;
        }

        private static void SanitizeComponentElement(XElement element)
        {
            foreach (string attributeName in RemovedComponentAttributes)
            {
                element.Attribute(attributeName)?.Remove();
            }

            foreach (XElement remark in element.Elements("remark").ToList())
            {
                remark.Remove();
            }

            foreach (XElement child in element.Elements())
            {
                SanitizeComponentElement(child);
            }
        }

        private static string RequiredAttribute(XElement element, string name)
        {
            XAttribute attribute = element.Attribute(name);
            if (attribute == null || string.IsNullOrWhiteSpace(attribute.Value))
            {
                throw new InvalidOperationException("Missing required attribute '" + name + "' on package resource <" + element.Name.LocalName + ">.");
            }

            return attribute.Value;
        }

        private static Vector2Int ParseSize(string sizeText)
        {
            string[] parts = sizeText.Split(',');
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("Invalid size format: " + sizeText);
            }

            return new Vector2Int(
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture));
        }
    }

    internal static class PackageDirectoryContext
    {
        [ThreadStatic]
        public static string Current;
    }

    internal enum ResourceKind
    {
        Component,
        Image
    }

    private readonly struct ContainerEntry
    {
        public ContainerEntry(string name, string content)
        {
            Name = name;
            Content = content;
        }

        public string Name { get; }
        public string Content { get; }
    }

    internal sealed class AtlasImageInput
    {
        public AtlasImageInput(ResourceEntry resource, Texture2D texture, int atlasIndex)
        {
            Resource = resource;
            Texture = texture;
            AtlasIndex = atlasIndex;
        }

        public ResourceEntry Resource { get; }
        public Texture2D Texture { get; }
        public int AtlasIndex { get; }
        public int Width => Texture.width;
        public int Height => Texture.height;
    }

    internal sealed class AtlasOutput
    {
        public AtlasOutput(int atlasIndex, int width, int height, byte[] pngBytes, List<AtlasPlacement> placements)
        {
            AtlasIndex = atlasIndex;
            Width = width;
            Height = height;
            PngBytes = pngBytes;
            Placements = placements;
        }

        public int AtlasIndex { get; }
        public int Width { get; }
        public int Height { get; }
        public byte[] PngBytes { get; }
        public List<AtlasPlacement> Placements { get; }
    }

    private sealed class AtlasLayout
    {
        private readonly Dictionary<string, AtlasImageInput> _inputsById;

        public AtlasLayout(int width, int height, List<AtlasPlacement> placements, IEnumerable<AtlasImageInput> inputs)
        {
            Width = width;
            Height = height;
            Placements = placements;
            _inputsById = inputs.ToDictionary(input => input.Resource.Id, StringComparer.Ordinal);
        }

        public int Width { get; }
        public int Height { get; }
        public List<AtlasPlacement> Placements { get; }

        public AtlasImageInput FindInput(string resourceId)
        {
            return _inputsById[resourceId];
        }
    }

    internal readonly struct AtlasPlacement
    {
        public AtlasPlacement(string resourceId, int atlasIndex, int x, int y, int width, int height, bool rotated)
        {
            ResourceId = resourceId;
            AtlasIndex = atlasIndex;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Rotated = rotated;
        }

        public string ResourceId { get; }
        public int AtlasIndex { get; }
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public bool Rotated { get; }
    }

    private readonly struct Shelf
    {
        public Shelf(int y, int height, int x)
        {
            Y = y;
            Height = height;
            X = x;
        }

        public int Y { get; }
        public int Height { get; }
        public int X { get; }
    }

    private readonly struct PlacementChoice
    {
        public PlacementChoice(int shelfIndex, int width, int height, bool rotated, bool useExistingShelf, long score)
        {
            ShelfIndex = shelfIndex;
            Width = width;
            Height = height;
            Rotated = rotated;
            UseExistingShelf = useExistingShelf;
            Score = score;
        }

        public int ShelfIndex { get; }
        public int Width { get; }
        public int Height { get; }
        public bool Rotated { get; }
        public bool UseExistingShelf { get; }
        public long Score { get; }
    }

    private readonly struct PublishResult
    {
        public PublishResult(string packageName, string outputDirectory, List<ContainerEntry> containerEntries, List<AtlasOutput> atlases, List<string> spriteLines)
        {
            PackageName = packageName;
            OutputDirectory = outputDirectory;
            ContainerEntries = containerEntries;
            Atlases = atlases;
            SpriteLines = spriteLines;
        }

        public string PackageName { get; }
        public string OutputDirectory { get; }
        public List<ContainerEntry> ContainerEntries { get; }
        public List<AtlasOutput> Atlases { get; }
        public List<string> SpriteLines { get; }
    }
}
}


