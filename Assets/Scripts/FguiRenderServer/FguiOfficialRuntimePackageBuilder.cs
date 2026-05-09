using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using FairyGUI;
using UnityEngine;
using PackageSource = FguiRenderServer.FguiPackagePublisher.PackageSource;
using ResourceEntry = FguiRenderServer.FguiPackagePublisher.ResourceEntry;
using ResourceKind = FguiRenderServer.FguiPackagePublisher.ResourceKind;
using AtlasOutput = FguiRenderServer.FguiPackagePublisher.AtlasOutput;
using AtlasPlacement = FguiRenderServer.FguiPackagePublisher.AtlasPlacement;

namespace FguiRenderServer
{
    internal static class FguiOfficialRuntimePackageBuilder
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static OfficialRuntimePublishResult Build(PackageSource package, List<ResourceEntry> includedResources, string outputDir)
        {
            List<ResourceEntry> components = includedResources
                .Where(resource => resource.Kind == ResourceKind.Component)
                .OrderBy(resource => resource.Id, StringComparer.Ordinal)
                .ToList();
            List<ResourceEntry> images = includedResources
                .Where(resource => resource.Kind == ResourceKind.Image)
                .OrderBy(resource => resource.Id, StringComparer.Ordinal)
                .ToList();

            var atlasInputs = FguiPackagePublisher.LoadIncludedImages(package, images);
            List<AtlasOutput> atlases = FguiPackagePublisher.BuildAtlases(atlasInputs);
            Dictionary<string, AtlasPlacement> spriteMap = atlases
                .SelectMany(atlas => atlas.Placements)
                .ToDictionary(placement => placement.ResourceId, StringComparer.Ordinal);

            PublishContext context = new PublishContext(package, components, images, atlases, spriteMap);
            byte[] descriptorBytes = context.BuildDescriptor();

            Directory.CreateDirectory(outputDir);
            string descriptorPath = Path.Combine(outputDir, package.PackageName + "_fui.bytes");
            File.WriteAllBytes(descriptorPath, descriptorBytes);

            foreach (AtlasOutput atlas in atlases)
            {
                string atlasPath = Path.Combine(outputDir, package.PackageName + "_atlas" + atlas.AtlasIndex.ToString(CultureInfo.InvariantCulture) + ".png");
                File.WriteAllBytes(atlasPath, atlas.PngBytes);
            }

            return new OfficialRuntimePublishResult(package.PackageName, outputDir, context.ItemCount, atlases.Count);
        }

        internal sealed class OfficialRuntimePublishResult
        {
            public OfficialRuntimePublishResult(string packageName, string outputDirectory, int itemCount, int atlasCount)
            {
                PackageName = packageName;
                OutputDirectory = outputDirectory;
                ItemCount = itemCount;
                AtlasCount = atlasCount;
            }

            public string PackageName { get; }
            public string OutputDirectory { get; }
            public int ItemCount { get; }
            public int AtlasCount { get; }
        }

        private sealed class PublishContext
        {
            private readonly PackageSource _package;
            private readonly List<ResourceEntry> _components;
            private readonly List<ResourceEntry> _images;
            private readonly List<AtlasOutput> _atlases;
            private readonly Dictionary<string, AtlasPlacement> _spriteMap;
            private readonly Dictionary<string, XElement> _componentRoots;
            private readonly Dictionary<string, ObjectType> _componentObjectTypes;
            private readonly Dictionary<string, ComponentModel> _componentModels;
            private readonly List<PackageItemModel> _items;
            private readonly StringTable _strings;

            public PublishContext(
                PackageSource package,
                List<ResourceEntry> components,
                List<ResourceEntry> images,
                List<AtlasOutput> atlases,
                Dictionary<string, AtlasPlacement> spriteMap)
            {
                _package = package;
                _components = components;
                _images = images;
                _atlases = atlases;
                _spriteMap = spriteMap;
                _componentRoots = new Dictionary<string, XElement>(StringComparer.Ordinal);
                _componentObjectTypes = new Dictionary<string, ObjectType>(StringComparer.Ordinal);
                _componentModels = new Dictionary<string, ComponentModel>(StringComparer.Ordinal);
                _items = new List<PackageItemModel>();
                _strings = new StringTable();

                InitializeComponentRoots();
                InitializeComponentModels();
                InitializePackageItems();
                CollectStrings();
            }

            public int ItemCount => _items.Count;

            public byte[] BuildDescriptor()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteUInt(0x46475549u);
                writer.WriteInt(2);
                writer.WriteBool(false);
                writer.WriteStringRaw(_package.PackageId);
                writer.WriteStringRaw(_package.PackageName);
                writer.WriteBytes(new byte[20]);

                byte[] dependenciesBlock = BuildDependenciesBlock();
                byte[] itemsBlock = BuildItemsBlock();
                byte[] spritesBlock = BuildSpritesBlock();
                byte[] hitTestBlock = BuildHitTestBlock();
                byte[] stringTableBlock = BuildStringTableBlock();
                byte[] packageBlocks = IndexedBlockWriter.BuildIndexedBuffer(
                    dependenciesBlock,
                    itemsBlock,
                    spritesBlock,
                    hitTestBlock,
                    stringTableBlock,
                    null);

                writer.WriteBytes(packageBlocks);
                return writer.ToArray();
            }

            private void InitializeComponentRoots()
            {
                foreach (ResourceEntry component in _components)
                {
                    string componentPath = component.GetSourcePath(_package.PackageDirectory);
                    XDocument doc = XDocument.Load(componentPath);
                    XElement root = doc.Root ?? throw new InvalidOperationException("Component XML root is missing: " + componentPath);
                    _componentRoots[component.Id] = root;
                    _componentObjectTypes[component.Id] = ParseComponentObjectType(root.Attribute("extention")?.Value ?? root.Attribute("extension")?.Value);
                }
            }

            private void InitializeComponentModels()
            {
                foreach (ResourceEntry component in _components)
                {
                    _componentModels[component.Id] = ParseComponent(component, _componentRoots[component.Id]);
                }
            }

            private void InitializePackageItems()
            {
                foreach (ResourceEntry component in _components)
                {
                    ComponentModel model = _componentModels[component.Id];
                    PackageItemModel item = new PackageItemModel
                    {
                        ItemType = PackageItemType.Component,
                        ObjectType = model.ObjectType,
                        Id = component.Id,
                        Name = Path.GetFileNameWithoutExtension(component.Name),
                        Exported = component.Exported,
                        Width = model.Width,
                        Height = model.Height,
                        Component = model
                    };
                    AddItem(item);
                }

                foreach (ResourceEntry image in _images)
                {
                    AtlasPlacement placement;
                    if (!_spriteMap.TryGetValue(image.Id, out placement))
                    {
                        throw new InvalidOperationException("Missing atlas placement for image resource: " + image.Id);
                    }

                    PackageItemModel item = new PackageItemModel
                    {
                        ItemType = PackageItemType.Image,
                        ObjectType = ObjectType.Image,
                        Id = image.Id,
                        Name = Path.GetFileNameWithoutExtension(image.Name),
                        Exported = image.Exported,
                        Width = placement.Rotated ? placement.Height : placement.Width,
                        Height = placement.Rotated ? placement.Width : placement.Height
                    };
                    AddItem(item);
                }

                foreach (AtlasOutput atlas in _atlases.OrderBy(item => item.AtlasIndex))
                {
                    string atlasId = GetAtlasItemId(atlas.AtlasIndex);
                    PackageItemModel item = new PackageItemModel
                    {
                        ItemType = PackageItemType.Atlas,
                        ObjectType = ObjectType.Image,
                        Id = atlasId,
                        Name = atlasId,
                        Exported = false,
                        Width = atlas.Width,
                        Height = atlas.Height,
                        File = "atlas" + atlas.AtlasIndex.ToString(CultureInfo.InvariantCulture) + ".png"
                    };
                    AddItem(item);
                }
            }

            private void AddItem(PackageItemModel item)
            {
                _items.Add(item);
            }

            private void CollectStrings()
            {
                foreach (PackageItemModel item in _items)
                {
                    item.CollectStrings(_strings);
                }
            }

            private byte[] BuildDependenciesBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort(0);
                writer.WriteShort(0);
                return writer.ToArray();
            }

            private byte[] BuildItemsBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort((short)_items.Count);
                foreach (PackageItemModel item in _items)
                {
                    byte[] itemBytes = item.BuildBytes(this, _strings);
                    writer.WriteInt(itemBytes.Length);
                    writer.WriteBytes(itemBytes);
                }

                return writer.ToArray();
            }

            private byte[] BuildSpritesBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                List<ResourceEntry> orderedImages = _images.OrderBy(resource => resource.Id, StringComparer.Ordinal).ToList();
                writer.WriteShort((short)orderedImages.Count);
                foreach (ResourceEntry image in orderedImages)
                {
                    AtlasPlacement placement = _spriteMap[image.Id];
                    BigEndianWriter record = new BigEndianWriter();
                    record.WriteS(_strings, image.Id);
                    record.WriteS(_strings, GetAtlasItemId(placement.AtlasIndex));
                    record.WriteInt(placement.X);
                    record.WriteInt(placement.Y);
                    record.WriteInt(placement.Width);
                    record.WriteInt(placement.Height);
                    record.WriteBool(placement.Rotated);
                    record.WriteBool(false);

                    byte[] recordBytes = record.ToArray();
                    writer.WriteUShort((ushort)recordBytes.Length);
                    writer.WriteBytes(recordBytes);
                }

                return writer.ToArray();
            }

            private byte[] BuildHitTestBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort(0);
                return writer.ToArray();
            }

            private byte[] BuildStringTableBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteInt(_strings.Count);
                foreach (string value in _strings.Values)
                {
                    writer.WriteStringRaw(value);
                }

                return writer.ToArray();
            }

            public ObjectType ResolveSourceObjectType(ChildModel child)
            {
                if (child.Type != ObjectType.Component)
                {
                    return child.Type;
                }

                if (child.ExtensionOverride != null)
                {
                    return child.ExtensionOverride.ObjectType;
                }

                if (!string.IsNullOrEmpty(child.SourceId)
                    && string.IsNullOrEmpty(child.PackageId)
                    && _componentObjectTypes.TryGetValue(child.SourceId, out ObjectType localType))
                {
                    return localType;
                }

                return ObjectType.Component;
            }

            private ComponentModel ParseComponent(ResourceEntry component, XElement root)
            {
                Vector2Int size = ParseInt2(root.Attribute("size")?.Value, component.Id + " root size");
                ComponentModel model = new ComponentModel(component.Id)
                {
                    ObjectType = ParseComponentObjectType(root.Attribute("extention")?.Value ?? root.Attribute("extension")?.Value),
                    Width = size.x,
                    Height = size.y,
                    Pivot = ParseOptionalFloat2(root.Attribute("pivot")?.Value),
                    PivotAsAnchor = ParseBool(root.Attribute("anchor")?.Value),
                    Margin = ParseOptionalInt4(root.Attribute("margin")?.Value),
                    Overflow = ParseOverflow(root.Attribute("overflow")?.Value),
                    ScrollType = ParseScrollType(root.Attribute("scroll")?.Value),
                    Opaque = ParseBool(root.Attribute("opaque")?.Value)
                };

                foreach (XElement controller in root.Elements("controller"))
                {
                    model.Controllers.Add(ParseController(controller));
                }

                XElement displayList = root.Element("displayList");
                if (displayList != null)
                {
                    foreach (XElement child in displayList.Elements())
                    {
                        model.Children.Add(ParseChild(child));
                    }
                }

                XElement extensionElement = root.Elements().FirstOrDefault(IsExtensionElement);
                if (extensionElement != null)
                {
                    model.Extension = ParseComponentExtension(extensionElement);
                }

                component.SetSize(model.Width, model.Height);
                return model;
            }

            private ControllerModel ParseController(XElement element)
            {
                ControllerModel controller = new ControllerModel
                {
                    Name = element.Attribute("name")?.Value ?? string.Empty,
                    SelectedIndex = ParseInt(element.Attribute("selected")?.Value, 0),
                    AutoRadioGroupDepth = ParseBool(element.Attribute("autoRadioGroupDepth")?.Value)
                };

                string[] parts = SplitComma(element.Attribute("pages")?.Value);
                for (int i = 0; i + 1 < parts.Length; i += 2)
                {
                    controller.PageIds.Add(parts[i]);
                    controller.PageNames.Add(parts[i + 1]);
                }

                return controller;
            }

            private ChildModel ParseChild(XElement element)
            {
                string tagName = element.Name.LocalName;
                ChildModel child = new ChildModel
                {
                    Id = element.Attribute("id")?.Value ?? string.Empty,
                    Name = element.Attribute("name")?.Value,
                    SourceId = element.Attribute("src")?.Value,
                    PackageId = element.Attribute("pkg")?.Value,
                    X = ParseInt2(element.Attribute("xy")?.Value, tagName + " xy").x,
                    Y = ParseInt2(element.Attribute("xy")?.Value, tagName + " xy").y,
                    Size = ParseOptionalInt2(element.Attribute("size")?.Value),
                    Scale = ParseOptionalFloat2(element.Attribute("scale")?.Value),
                    Pivot = ParseOptionalFloat2(element.Attribute("pivot")?.Value),
                    PivotAsAnchor = ParseBool(element.Attribute("anchor")?.Value),
                    Alpha = ParseFloat(element.Attribute("alpha")?.Value, 1f),
                    Visible = !string.Equals(element.Attribute("visible")?.Value, "false", StringComparison.OrdinalIgnoreCase),
                    Touchable = !string.Equals(element.Attribute("touchable")?.Value, "false", StringComparison.OrdinalIgnoreCase),
                    GroupId = element.Attribute("group")?.Value,
                    Controller = element.Attribute("controller")?.Value
                };

                if (tagName == "component")
                {
                    child.Type = ResolveSourceObjectTypeFromElement(element);
                    child.Component = new ComponentInstanceModel();
                    if (!string.IsNullOrWhiteSpace(child.Controller))
                    {
                        string[] controllerTokens = SplitComma(child.Controller);
                        for (int i = 0; i + 1 < controllerTokens.Length; i += 2)
                        {
                            child.Component.ControllerStates.Add(new ControllerStateModel(controllerTokens[i], controllerTokens[i + 1]));
                        }
                    }
                }
                else if (tagName == "text")
                {
                    child.Type = ParseBool(element.Attribute("input")?.Value) ? ObjectType.InputText : ObjectType.Text;
                    child.Text = ParseTextModel(element, child.Type == ObjectType.InputText);
                }
                else if (tagName == "graph")
                {
                    child.Type = ObjectType.Graph;
                    child.Graph = ParseGraphModel(element);
                }
                else if (tagName == "image")
                {
                    child.Type = ObjectType.Image;
                    child.Image = new ImageModel
                    {
                        Color = ParseOptionalColor(element.Attribute("color")?.Value),
                        FillMethod = FillMethod.None
                    };
                }
                else if (tagName == "loader")
                {
                    child.Type = ObjectType.Loader;
                    child.Loader = ParseLoaderModel(element);
                }
                else if (tagName == "group")
                {
                    child.Type = ObjectType.Group;
                    child.Group = ParseGroupModel(element);
                }
                else if (tagName == "list")
                {
                    child.Type = ObjectType.List;
                    child.List = ParseListModel(element);
                }
                else
                {
                    throw new InvalidOperationException("Unsupported display-list tag in official runtime exporter: <" + tagName + ">.");
                }

                foreach (XElement nested in element.Elements())
                {
                    string name = nested.Name.LocalName;
                    if (name == "relation")
                    {
                        child.Relations.Add(ParseRelation(nested));
                    }
                    else if (IsGearElement(nested))
                    {
                        child.Gears.Add(ParseGear(nested));
                    }
                    else if (IsExtensionElement(nested))
                    {
                        child.ExtensionOverride = ParseExtensionOverride(nested);
                    }
                }

                return child;
            }

            private ObjectType ResolveSourceObjectTypeFromElement(XElement element)
            {
                XElement extensionElement = element.Elements().FirstOrDefault(IsExtensionElement);
                if (extensionElement != null)
                {
                    return ParseComponentObjectType(extensionElement.Name.LocalName);
                }

                string srcId = element.Attribute("src")?.Value;
                string pkgId = element.Attribute("pkg")?.Value;
                if (!string.IsNullOrEmpty(srcId)
                    && string.IsNullOrEmpty(pkgId)
                    && _componentObjectTypes.TryGetValue(srcId, out ObjectType objectType))
                {
                    return objectType;
                }

                return ObjectType.Component;
            }

            private TextModel ParseTextModel(XElement element, bool isInput)
            {
                TextModel text = new TextModel
                {
                    Font = element.Attribute("font")?.Value,
                    FontSize = ParseInt(element.Attribute("fontSize")?.Value, 12),
                    Color = ParseColor(element.Attribute("color")?.Value, new Color32(0, 0, 0, 255)),
                    Align = ParseAlign(element.Attribute("align")?.Value),
                    VerticalAlign = ParseVertAlign(element.Attribute("vAlign")?.Value),
                    AutoSize = ParseAutoSize(element.Attribute("autoSize")?.Value),
                    Bold = ParseBool(element.Attribute("bold")?.Value),
                    Italic = ParseBool(element.Attribute("italic")?.Value),
                    Underline = ParseBool(element.Attribute("underline")?.Value),
                    SingleLine = ParseBool(element.Attribute("singleLine")?.Value),
                    Text = element.Attribute("text")?.Value,
                    Restrict = element.Attribute("restrict")?.Value,
                    KeyboardType = ParseInt(element.Attribute("keyboardType")?.Value, 0),
                    MaxLength = ParseInt(element.Attribute("maxLength")?.Value, 0),
                    Password = ParseBool(element.Attribute("password")?.Value),
                    Prompt = element.Attribute("prompt")?.Value
                };

                string strokeColor = element.Attribute("strokeColor")?.Value;
                if (!string.IsNullOrWhiteSpace(strokeColor))
                {
                    text.OutlineColor = ParseColor(strokeColor, new Color32(0, 0, 0, 255));
                    text.OutlineSize = ParseFloat(element.Attribute("strokeSize")?.Value, 1f);
                }

                if (isInput && string.IsNullOrEmpty(text.Prompt) && string.IsNullOrEmpty(text.Text))
                {
                    text.Text = string.Empty;
                }

                return text;
            }

            private LoaderModel ParseLoaderModel(XElement element)
            {
                LoaderModel loader = new LoaderModel
                {
                    Url = element.Attribute("url")?.Value,
                    Align = ParseAlign(element.Attribute("align")?.Value),
                    VerticalAlign = ParseVertAlign(element.Attribute("vAlign")?.Value),
                    Fill = ParseFillType(element.Attribute("fill")?.Value),
                    Color = ParseOptionalColor(element.Attribute("color")?.Value)
                };
                return loader;
            }

            private GraphModel ParseGraphModel(XElement element)
            {
                GraphModel graph = new GraphModel
                {
                    ShapeType = ParseGraphShape(element.Attribute("type")?.Value),
                    LineSize = ParseInt(element.Attribute("lineSize")?.Value, element.Attribute("lineColor") != null ? 1 : 0),
                    LineColor = ParseColor(element.Attribute("lineColor")?.Value, new Color32(0, 0, 0, 0)),
                    FillColor = ParseColor(element.Attribute("fillColor")?.Value, new Color32(0, 0, 0, 0)),
                    CornerRadii = ParseCornerRadii(element.Attribute("corner")?.Value),
                    Points = ParsePoints(element.Attribute("points")?.Value)
                };
                return graph;
            }

            private GroupModel ParseGroupModel(XElement element)
            {
                return new GroupModel
                {
                    Layout = ParseGroupLayout(element.Attribute("layout")?.Value),
                    LineGap = ParseInt(element.Attribute("lineGap")?.Value, 0),
                    ColumnGap = ParseInt(element.Attribute("colGap")?.Value, 0),
                    ExcludeInvisibles = ParseBool(element.Attribute("excludeInvisibles")?.Value),
                    AutoSizeDisabled = ParseBool(element.Attribute("autoSizeDisabled")?.Value),
                    MainGridIndex = ParseInt(element.Attribute("mainGridIndex")?.Value, -1)
                };
            }

            private ListModel ParseListModel(XElement element)
            {
                ListModel list = new ListModel
                {
                    Layout = ParseListLayout(element.Attribute("layout")?.Value),
                    SelectionMode = ParseListSelectionMode(element.Attribute("selectionMode")?.Value),
                    Align = ParseAlign(element.Attribute("align")?.Value),
                    VerticalAlign = ParseVertAlign(element.Attribute("vAlign")?.Value),
                    LineGap = ParseInt(element.Attribute("lineGap")?.Value, 0),
                    ColumnGap = ParseInt(element.Attribute("colGap")?.Value, 0),
                    LineCount = ParseInt(element.Attribute("lineCount")?.Value, ParseInt(element.Attribute("lineItemCount")?.Value, 0)),
                    ColumnCount = ParseInt(element.Attribute("columnCount")?.Value, 0),
                    AutoResizeItem = !string.Equals(element.Attribute("autoResizeItem")?.Value, "false", StringComparison.OrdinalIgnoreCase),
                    DefaultItem = element.Attribute("defaultItem")?.Value,
                    Margin = ParseOptionalInt4(element.Attribute("margin")?.Value),
                    Overflow = ParseOverflow(element.Attribute("overflow")?.Value),
                    ScrollType = ParseScrollType(element.Attribute("scroll")?.Value),
                    ScrollItemToViewOnClick = !string.Equals(element.Attribute("scrollItemToViewOnClick")?.Value, "false", StringComparison.OrdinalIgnoreCase),
                    FoldInvisibleItems = ParseBool(element.Attribute("foldInvisibleItems")?.Value)
                };

                foreach (XElement item in element.Elements("item"))
                {
                    list.Items.Add(ParseListItem(item));
                }

                return list;
            }

            private ListItemModel ParseListItem(XElement element)
            {
                ListItemModel item = new ListItemModel();
                string controllers = element.Attribute("controllers")?.Value;
                if (!string.IsNullOrWhiteSpace(controllers))
                {
                    string[] tokens = SplitComma(controllers);
                    for (int i = 0; i + 1 < tokens.Length; i += 2)
                    {
                        item.Controllers.Add(new ControllerStateModel(tokens[i], tokens[i + 1]));
                    }
                }

                return item;
            }

            private RelationModel ParseRelation(XElement element)
            {
                RelationModel relation = new RelationModel
                {
                    TargetId = element.Attribute("target")?.Value
                };

                foreach (string pair in SplitComma(element.Attribute("sidePair")?.Value))
                {
                    string token = pair == null ? string.Empty : pair.Trim();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    bool usePercent = token.EndsWith("%", StringComparison.Ordinal);
                    string normalized = usePercent ? token.Substring(0, token.Length - 1) : token;
                    relation.Sides.Add(new RelationSideModel(ParseRelationType(normalized), usePercent));
                }

                return relation;
            }

            private GearModel ParseGear(XElement element)
            {
                GearModel gear = new GearModel
                {
                    Kind = element.Name.LocalName,
                    Controller = element.Attribute("controller")?.Value,
                    Pages = SplitComma(element.Attribute("pages")?.Value).Where(value => !string.IsNullOrEmpty(value)).ToList(),
                    Tween = ParseBool(element.Attribute("tween")?.Value),
                    EaseType = ParseEaseType(element.Attribute("ease")?.Value),
                    Duration = ParseFloat(element.Attribute("duration")?.Value, 0.3f),
                    Delay = ParseFloat(element.Attribute("delay")?.Value, 0f)
                };

                if (gear.Kind == "gearDisplay")
                {
                    return gear;
                }

                string[] values = (element.Attribute("values")?.Value ?? string.Empty)
                    .Split(new[] { '|' }, StringSplitOptions.None);
                string defaultValue = element.Attribute("default")?.Value;

                if (gear.Kind == "gearText" || gear.Kind == "gearIcon")
                {
                    for (int i = 0; i < gear.Pages.Count && i < values.Length; i++)
                    {
                        gear.Values[gear.Pages[i]] = values[i];
                    }
                    gear.DefaultValue = defaultValue;
                }
                else if (gear.Kind == "gearColor")
                {
                    for (int i = 0; i < gear.Pages.Count && i < values.Length; i++)
                    {
                        gear.ColorValues[gear.Pages[i]] = ParseColor(values[i], new Color32(255, 255, 255, 255));
                    }
                    if (!string.IsNullOrWhiteSpace(defaultValue))
                    {
                        gear.DefaultColor = ParseColor(defaultValue, new Color32(255, 255, 255, 255));
                    }
                }
                else if (gear.Kind == "gearXY")
                {
                    for (int i = 0; i < gear.Pages.Count && i < values.Length; i++)
                    {
                        gear.PointValues[gear.Pages[i]] = ParseInt2(values[i], gear.Kind + " value");
                    }
                    if (!string.IsNullOrWhiteSpace(defaultValue))
                    {
                        gear.DefaultPoint = ParseInt2(defaultValue, gear.Kind + " default");
                    }
                }
                else if (gear.Kind == "gearSize")
                {
                    for (int i = 0; i < gear.Pages.Count && i < values.Length; i++)
                    {
                        gear.SizeValues[gear.Pages[i]] = ParseSizeGearValue(values[i]);
                    }
                    if (!string.IsNullOrWhiteSpace(defaultValue))
                    {
                        gear.DefaultSize = ParseSizeGearValue(defaultValue);
                    }
                }
                else
                {
                    throw new InvalidOperationException("Unsupported gear tag in official runtime exporter: <" + gear.Kind + ">.");
                }

                return gear;
            }

            private ComponentExtensionModel ParseComponentExtension(XElement element)
            {
                string tag = element.Name.LocalName;
                if (tag == "Button")
                {
                    return new ComponentExtensionModel
                    {
                        ObjectType = ObjectType.Button,
                        ButtonMode = ParseButtonMode(element.Attribute("mode")?.Value),
                        Sound = element.Attribute("sound")?.Value,
                        SoundVolumeScale = ParseFloat(element.Attribute("soundVolumeScale")?.Value, 1f),
                        DownEffect = ParseButtonDownEffect(element.Attribute("downEffect")?.Value),
                        DownEffectValue = ParseFloat(element.Attribute("downEffectValue")?.Value, 0.8f)
                    };
                }

                if (tag == "Slider")
                {
                    return new ComponentExtensionModel
                    {
                        ObjectType = ObjectType.Slider,
                        SliderTitleType = ParseProgressTitleType(element.Attribute("titleType")?.Value),
                        SliderReverse = ParseBool(element.Attribute("reverse")?.Value),
                        SliderWholeNumbers = ParseBool(element.Attribute("wholeNumbers")?.Value),
                        SliderChangeOnClick = !string.Equals(element.Attribute("changeOnClick")?.Value, "false", StringComparison.OrdinalIgnoreCase)
                    };
                }

                if (tag == "Label")
                {
                    return new ComponentExtensionModel { ObjectType = ObjectType.Label };
                }

                throw new InvalidOperationException("Unsupported component extension tag in official runtime exporter: <" + tag + ">.");
            }

            private ExtensionOverrideModel ParseExtensionOverride(XElement element)
            {
                string tag = element.Name.LocalName;
                ExtensionOverrideModel model = new ExtensionOverrideModel
                {
                    ObjectType = ParseComponentObjectType(tag),
                    Title = element.Attribute("title")?.Value,
                    SelectedTitle = element.Attribute("selectedTitle")?.Value,
                    Icon = element.Attribute("icon")?.Value,
                    SelectedIcon = element.Attribute("selectedIcon")?.Value,
                    TitleColor = ParseOptionalColor(element.Attribute("titleColor")?.Value),
                    TitleFontSize = ParseInt(element.Attribute("titleFontSize")?.Value, 0),
                    Sound = element.Attribute("sound")?.Value,
                    SoundVolumeScale = ParseOptionalFloat(element.Attribute("soundVolumeScale")?.Value),
                    Selected = ParseBool(element.Attribute("selected")?.Value),
                    Value = ParseInt(element.Attribute("value")?.Value, 50),
                    Max = ParseInt(element.Attribute("max")?.Value, 100),
                    Min = ParseInt(element.Attribute("min")?.Value, 0)
                };
                return model;
            }

            private static bool IsGearElement(XElement element)
            {
                string name = element.Name.LocalName;
                return name == "gearDisplay"
                    || name == "gearText"
                    || name == "gearColor"
                    || name == "gearXY"
                    || name == "gearIcon"
                    || name == "gearSize";
            }

            private static bool IsExtensionElement(XElement element)
            {
                string name = element.Name.LocalName;
                return name == "Button" || name == "Label" || name == "Slider";
            }
        }

        private sealed class PackageItemModel
        {
            public PackageItemType ItemType;
            public ObjectType ObjectType;
            public string Id;
            public string Name;
            public string File;
            public bool Exported;
            public int Width;
            public int Height;
            public ComponentModel Component;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(Id);
                strings.Add(Name);
                strings.Add(File);
                if (Component != null)
                {
                    Component.CollectStrings(strings);
                }
            }

            public byte[] BuildBytes(PublishContext context, StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)ItemType);
                writer.WriteS(strings, Id);
                writer.WriteS(strings, Name);
                writer.WriteS(strings, null);
                writer.WriteS(strings, File);
                writer.WriteBool(Exported);
                writer.WriteInt(Width);
                writer.WriteInt(Height);

                if (ItemType == PackageItemType.Image)
                {
                    writer.WriteByte(0);
                    writer.WriteBool(false);
                }
                else if (ItemType == PackageItemType.Component)
                {
                    writer.WriteByte((byte)(ObjectType == ObjectType.Component ? 0 : (int)ObjectType));
                    writer.WriteBuffer(Component.BuildRawData(context, strings));
                }
                writer.WriteS(strings, null);
                writer.WriteByte(0);
                writer.WriteByte(0);
                return writer.ToArray();
            }
        }

        private sealed class ComponentModel
        {
            public ComponentModel(string resourceId)
            {
                Controllers = new List<ControllerModel>();
                Children = new List<ChildModel>();
            }

            public ObjectType ObjectType;
            public int Width;
            public int Height;
            public Vector2? Pivot;
            public bool PivotAsAnchor;
            public Int4? Margin;
            public OverflowType Overflow;
            public ScrollType ScrollType;
            public bool Opaque;
            public ComponentExtensionModel Extension;
            public List<ControllerModel> Controllers;
            public List<ChildModel> Children;

            public void CollectStrings(StringTable strings)
            {
                Extension?.CollectStrings(strings);

                foreach (ControllerModel controller in Controllers)
                {
                    controller.CollectStrings(strings);
                }

                foreach (ChildModel child in Children)
                {
                    child.CollectStrings(strings);
                }
            }

            public byte[] BuildRawData(PublishContext context, StringTable strings)
            {
                Dictionary<string, int> childIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Children.Count; i++)
                {
                    if (!string.IsNullOrEmpty(Children[i].Id))
                    {
                        childIndexById[Children[i].Id] = i;
                    }
                }

                byte[] block0 = BuildBlock0();
                byte[] block1 = BuildControllersBlock(strings);
                byte[] block2 = BuildChildrenBlock(context, strings, childIndexById);
                byte[] block3 = BuildComponentRelationsBlock();
                byte[] block4 = BuildComponentBlock4(strings);
                byte[] block5 = BuildTransitionsBlock();
                byte[] block6 = Extension != null ? Extension.BuildBlock(strings) : null;
                byte[] block7 = Overflow == OverflowType.Scroll ? BuildScrollBlock(strings, ScrollType) : null;

                return IndexedBlockWriter.BuildIndexedBuffer(block0, block1, block2, block3, block4, block5, block6, block7, null);
            }

            private byte[] BuildBlock0()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteInt(Width);
                writer.WriteInt(Height);
                writer.WriteBool(false);
                writer.WriteBool(Pivot.HasValue);
                if (Pivot.HasValue)
                {
                    writer.WriteFloat(Pivot.Value.x);
                    writer.WriteFloat(Pivot.Value.y);
                    writer.WriteBool(PivotAsAnchor);
                }

                writer.WriteBool(Margin.HasValue);
                if (Margin.HasValue)
                {
                    Int4 margin = Margin.Value;
                    writer.WriteInt(margin.Top);
                    writer.WriteInt(margin.Bottom);
                    writer.WriteInt(margin.Left);
                    writer.WriteInt(margin.Right);
                }

                writer.WriteByte((byte)Overflow);
                writer.WriteBool(false);
                return writer.ToArray();
            }

            private byte[] BuildControllersBlock(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort((short)Controllers.Count);
                foreach (ControllerModel controller in Controllers)
                {
                    byte[] record = controller.BuildBytes(strings);
                    writer.WriteUShort((ushort)record.Length);
                    writer.WriteBytes(record);
                }
                return writer.ToArray();
            }

            private byte[] BuildChildrenBlock(PublishContext context, StringTable strings, Dictionary<string, int> childIndexById)
            {
                Dictionary<string, int> controllerIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < Controllers.Count; i++)
                {
                    controllerIndexByName[Controllers[i].Name] = i;
                }

                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort((short)Children.Count);
                foreach (ChildModel child in Children)
                {
                    byte[] record = child.BuildBytes(context, strings, childIndexById, controllerIndexByName);
                    writer.WriteShort((short)record.Length);
                    writer.WriteBytes(record);
                }
                return writer.ToArray();
            }

            private static byte[] BuildComponentRelationsBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte(0);
                return writer.ToArray();
            }

            private byte[] BuildComponentBlock4(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort(0);
                writer.WriteBool(Opaque);
                writer.WriteShort(-1);
                writer.WriteS(strings, null);
                writer.WriteInt(0);
                writer.WriteInt(-1);
                return writer.ToArray();
            }

            private static byte[] BuildTransitionsBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort(0);
                return writer.ToArray();
            }

            private static byte[] BuildScrollBlock(StringTable strings, ScrollType scrollType)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)scrollType);
                writer.WriteByte((byte)ScrollBarDisplayType.Hidden);
                writer.WriteInt(0);
                writer.WriteBool(false);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                return writer.ToArray();
            }
        }

        private sealed class ControllerModel
        {
            public ControllerModel()
            {
                PageIds = new List<string>();
                PageNames = new List<string>();
            }

            public string Name;
            public bool AutoRadioGroupDepth;
            public int SelectedIndex;
            public List<string> PageIds;
            public List<string> PageNames;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(Name);
                foreach (string id in PageIds)
                {
                    strings.Add(id);
                }
                foreach (string name in PageNames)
                {
                    strings.Add(name);
                }
            }

            public byte[] BuildBytes(StringTable strings)
            {
                BigEndianWriter block0 = new BigEndianWriter();
                block0.WriteS(strings, Name);
                block0.WriteBool(AutoRadioGroupDepth);

                BigEndianWriter block1 = new BigEndianWriter();
                block1.WriteShort((short)PageIds.Count);
                for (int i = 0; i < PageIds.Count; i++)
                {
                    block1.WriteS(strings, PageIds[i]);
                    block1.WriteS(strings, i < PageNames.Count ? PageNames[i] : string.Empty);
                }
                block1.WriteByte(1);
                block1.WriteShort((short)SelectedIndex);

                BigEndianWriter block2 = new BigEndianWriter();
                block2.WriteShort(0);

                return IndexedBlockWriter.BuildIndexedBuffer(block0.ToArray(), block1.ToArray(), block2.ToArray());
            }
        }

        private sealed class ChildModel
        {
            public ChildModel()
            {
                Relations = new List<RelationModel>();
                Gears = new List<GearModel>();
            }

            public ObjectType Type;
            public string Id;
            public string Name;
            public string SourceId;
            public string PackageId;
            public int X;
            public int Y;
            public Vector2Int? Size;
            public Vector2? Scale;
            public Vector2? Pivot;
            public bool PivotAsAnchor;
            public float Alpha = 1f;
            public bool Visible = true;
            public bool Touchable = true;
            public string GroupId;
            public string Controller;
            public TextModel Text;
            public ImageModel Image;
            public LoaderModel Loader;
            public GraphModel Graph;
            public GroupModel Group;
            public ListModel List;
            public ComponentInstanceModel Component;
            public ExtensionOverrideModel ExtensionOverride;
            public List<RelationModel> Relations;
            public List<GearModel> Gears;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(SourceId);
                strings.Add(PackageId);
                strings.Add(Id);
                strings.Add(Name);
                strings.Add(GroupId);
                if (Text != null)
                {
                    Text.CollectStrings(strings);
                }
                if (Loader != null)
                {
                    Loader.CollectStrings(strings);
                }
                if (List != null)
                {
                    List.CollectStrings(strings);
                }
                if (Component != null)
                {
                    Component.CollectStrings(strings);
                }
                if (ExtensionOverride != null)
                {
                    ExtensionOverride.CollectStrings(strings);
                }
                foreach (RelationModel relation in Relations)
                {
                    relation.CollectStrings(strings);
                }
                foreach (GearModel gear in Gears)
                {
                    gear.CollectStrings(strings);
                }
            }

            public byte[] BuildBytes(PublishContext context, StringTable strings, Dictionary<string, int> childIndexById, Dictionary<string, int> controllerIndexByName)
            {
                byte[] block0 = BuildBlock0(context, strings);
                byte[] block1 = BuildBlock1(strings, childIndexById);
                byte[] block2 = BuildBlock2(strings, controllerIndexByName);
                byte[] block3 = BuildBlock3(childIndexById);
                byte[] block4 = BuildBlock4(strings);
                byte[] block5 = BuildBlock5(strings);
                byte[] block6 = BuildBlock6(strings);
                byte[] block7 = List != null && List.Overflow == OverflowType.Scroll ? BuildScrollBlock(strings, List.ScrollType) : null;
                byte[] block8 = List != null ? List.BuildItemsBlock(strings) : null;
                return IndexedBlockWriter.BuildIndexedBuffer(block0, block1, block2, block3, block4, block5, block6, block7, block8);
            }

            private byte[] BuildBlock0(PublishContext context, StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                ObjectType effectiveType = context.ResolveSourceObjectType(this);
                writer.WriteByte((byte)effectiveType);
                writer.WriteS(strings, SourceId);
                writer.WriteS(strings, PackageId);
                writer.WriteS(strings, Id);
                writer.WriteS(strings, Name);
                writer.WriteInt(X);
                writer.WriteInt(Y);
                writer.WriteBool(Size.HasValue);
                if (Size.HasValue)
                {
                    writer.WriteInt(Size.Value.x);
                    writer.WriteInt(Size.Value.y);
                }

                writer.WriteBool(false);
                writer.WriteBool(Scale.HasValue);
                if (Scale.HasValue)
                {
                    writer.WriteFloat(Scale.Value.x);
                    writer.WriteFloat(Scale.Value.y);
                }

                writer.WriteBool(false);
                writer.WriteBool(Pivot.HasValue);
                if (Pivot.HasValue)
                {
                    writer.WriteFloat(Pivot.Value.x);
                    writer.WriteFloat(Pivot.Value.y);
                    writer.WriteBool(PivotAsAnchor);
                }

                writer.WriteFloat(Alpha);
                writer.WriteFloat(0f);
                writer.WriteBool(Visible);
                writer.WriteBool(Touchable);
                writer.WriteBool(false);
                writer.WriteByte((byte)BlendMode.Normal);
                writer.WriteByte(0);
                writer.WriteS(strings, null);
                return writer.ToArray();
            }

            private byte[] BuildBlock1(StringTable strings, Dictionary<string, int> childIndexById)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteS(strings, null);
                writer.WriteShort(ResolveGroupIndex(childIndexById));
                return writer.ToArray();
            }

            private byte[] BuildBlock2(StringTable strings, Dictionary<string, int> controllerIndexByName)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteShort((short)Gears.Count);
                foreach (GearModel gear in Gears)
                {
                    byte[] gearBytes = gear.BuildBytes(strings, controllerIndexByName);
                    writer.WriteUShort((ushort)gearBytes.Length);
                    writer.WriteBytes(gearBytes);
                }
                return writer.ToArray();
            }

            private byte[] BuildBlock3(Dictionary<string, int> childIndexById)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)Relations.Count);
                foreach (RelationModel relation in Relations)
                {
                    writer.WriteShort((short)ResolveTargetIndex(relation.TargetId, childIndexById));
                    writer.WriteByte((byte)relation.Sides.Count);
                    foreach (RelationSideModel side in relation.Sides)
                    {
                        writer.WriteByte((byte)side.Type);
                        writer.WriteBool(side.UsePercent);
                    }
                }
                return writer.ToArray();
            }

            private byte[] BuildBlock4(StringTable strings)
            {
                if (Type == ObjectType.Component)
                {
                    BigEndianWriter writer = new BigEndianWriter();
                    writer.WriteShort(-1);
                    int count = Component == null ? 0 : Component.ControllerStates.Count;
                    writer.WriteShort((short)count);
                    if (Component != null)
                    {
                        foreach (ControllerStateModel state in Component.ControllerStates)
                        {
                            writer.WriteS(strings, state.ControllerName);
                            writer.WriteS(strings, state.PageId);
                        }
                    }
                    writer.WriteShort(0);
                    return writer.ToArray();
                }

                if (Type == ObjectType.InputText)
                {
                    BigEndianWriter writer = new BigEndianWriter();
                    writer.WriteS(strings, Text == null ? null : Text.Prompt);
                    writer.WriteS(strings, Text == null ? null : Text.Restrict);
                    writer.WriteInt(Text == null ? 0 : Text.MaxLength);
                    writer.WriteInt(Text == null ? 0 : Text.KeyboardType);
                    writer.WriteBool(Text != null && Text.Password);
                    return writer.ToArray();
                }

                return null;
            }

            private byte[] BuildBlock5(StringTable strings)
            {
                if (Type == ObjectType.Text || Type == ObjectType.InputText)
                {
                    return Text.BuildStyleBlock(strings);
                }
                if (Type == ObjectType.Image)
                {
                    return Image.BuildBlock();
                }
                if (Type == ObjectType.Loader)
                {
                    return Loader.BuildBlock(strings);
                }
                if (Type == ObjectType.Graph)
                {
                    return Graph.BuildBlock();
                }
                if (Type == ObjectType.Group)
                {
                    return Group.BuildBlock();
                }
                if (Type == ObjectType.List)
                {
                    return List.BuildBlock(strings);
                }
                return null;
            }

            private byte[] BuildBlock6(StringTable strings)
            {
                if (Type == ObjectType.Text || Type == ObjectType.InputText)
                {
                    BigEndianWriter writer = new BigEndianWriter();
                    writer.WriteS(strings, Text == null ? null : Text.Text);
                    return writer.ToArray();
                }

                if (Type == ObjectType.Component && ExtensionOverride != null)
                {
                    return ExtensionOverride.BuildBlock(strings);
                }

                if (Type == ObjectType.List)
                {
                    BigEndianWriter writer = new BigEndianWriter();
                    writer.WriteShort(-1);
                    return writer.ToArray();
                }

                return null;
            }

            private static byte[] BuildScrollBlock(StringTable strings, ScrollType scrollType)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)scrollType);
                writer.WriteByte((byte)ScrollBarDisplayType.Hidden);
                writer.WriteInt(0);
                writer.WriteBool(false);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                return writer.ToArray();
            }

            private int ResolveTargetIndex(string targetId, Dictionary<string, int> childIndexById)
            {
                if (string.IsNullOrEmpty(targetId))
                {
                    return -1;
                }

                return childIndexById.TryGetValue(targetId, out int index) ? index : -1;
            }

            private short ResolveGroupIndex(Dictionary<string, int> childIndexById)
            {
                if (string.IsNullOrEmpty(GroupId))
                {
                    return -1;
                }

                return childIndexById.TryGetValue(GroupId, out int index) ? (short)index : (short)-1;
            }
        }

        private sealed class TextModel
        {
            public string Font;
            public int FontSize;
            public Color32 Color;
            public AlignType Align;
            public VertAlignType VerticalAlign;
            public AutoSizeType AutoSize;
            public bool Underline;
            public bool Italic;
            public bool Bold;
            public bool SingleLine;
            public string Text;
            public string Prompt;
            public string Restrict;
            public int MaxLength;
            public int KeyboardType;
            public bool Password;
            public Color32? OutlineColor;
            public float OutlineSize;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(Font);
                strings.Add(Text);
                strings.Add(Prompt);
                strings.Add(Restrict);
            }

            public byte[] BuildStyleBlock(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteS(strings, Font);
                writer.WriteShort((short)FontSize);
                writer.WriteColor(Color);
                writer.WriteByte((byte)Align);
                writer.WriteByte((byte)VerticalAlign);
                writer.WriteShort(0);
                writer.WriteShort(0);
                writer.WriteBool(false);
                writer.WriteByte((byte)AutoSize);
                writer.WriteBool(Underline);
                writer.WriteBool(Italic);
                writer.WriteBool(Bold);
                writer.WriteBool(SingleLine);
                writer.WriteBool(OutlineColor.HasValue);
                if (OutlineColor.HasValue)
                {
                    writer.WriteColor(OutlineColor.Value);
                    writer.WriteFloat(OutlineSize <= 0 ? 1f : OutlineSize);
                }
                writer.WriteBool(false);
                writer.WriteBool(false);
                return writer.ToArray();
            }
        }

        private sealed class ImageModel
        {
            public Color32? Color;
            public FillMethod FillMethod;

            public byte[] BuildBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteBool(Color.HasValue);
                if (Color.HasValue)
                {
                    writer.WriteColor(Color.Value);
                }
                writer.WriteByte((byte)FlipType.None);
                writer.WriteByte((byte)FillMethod);
                return writer.ToArray();
            }
        }

        private sealed class LoaderModel
        {
            public string Url;
            public AlignType Align;
            public VertAlignType VerticalAlign;
            public FillType Fill;
            public Color32? Color;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(Url);
            }

            public byte[] BuildBlock(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteS(strings, Url);
                writer.WriteByte((byte)Align);
                writer.WriteByte((byte)VerticalAlign);
                writer.WriteByte((byte)Fill);
                writer.WriteBool(false);
                writer.WriteBool(false);
                writer.WriteBool(false);
                writer.WriteBool(false);
                writer.WriteInt(0);
                writer.WriteBool(Color.HasValue);
                if (Color.HasValue)
                {
                    writer.WriteColor(Color.Value);
                }
                writer.WriteByte((byte)FillMethod.None);
                return writer.ToArray();
            }
        }

        private sealed class GraphModel
        {
            public byte ShapeType;
            public int LineSize;
            public Color32 LineColor;
            public Color32 FillColor;
            public Vector4? CornerRadii;
            public List<Vector2> Points;

            public byte[] BuildBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte(ShapeType);
                if (ShapeType != 0)
                {
                    writer.WriteInt(LineSize);
                    writer.WriteColor(LineColor);
                    writer.WriteColor(FillColor);
                    writer.WriteBool(CornerRadii.HasValue);
                    if (CornerRadii.HasValue)
                    {
                        Vector4 radii = CornerRadii.Value;
                        writer.WriteFloat(radii.x);
                        writer.WriteFloat(radii.y);
                        writer.WriteFloat(radii.z);
                        writer.WriteFloat(radii.w);
                    }
                    if (ShapeType == 3)
                    {
                        int count = Points == null ? 0 : Points.Count;
                        writer.WriteShort((short)(count * 2));
                        if (Points != null)
                        {
                            foreach (Vector2 point in Points)
                            {
                                writer.WriteFloat(point.x);
                                writer.WriteFloat(point.y);
                            }
                        }
                    }
                }
                return writer.ToArray();
            }
        }

        private sealed class GroupModel
        {
            public GroupLayoutType Layout;
            public int LineGap;
            public int ColumnGap;
            public bool ExcludeInvisibles;
            public bool AutoSizeDisabled;
            public int MainGridIndex;

            public byte[] BuildBlock()
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)Layout);
                writer.WriteInt(LineGap);
                writer.WriteInt(ColumnGap);
                writer.WriteBool(ExcludeInvisibles);
                writer.WriteBool(AutoSizeDisabled);
                writer.WriteShort((short)MainGridIndex);
                return writer.ToArray();
            }
        }

        private sealed class ListModel
        {
            public ListModel()
            {
                Items = new List<ListItemModel>();
            }

            public ListLayoutType Layout;
            public ListSelectionMode SelectionMode;
            public AlignType Align;
            public VertAlignType VerticalAlign;
            public int LineGap;
            public int ColumnGap;
            public int LineCount;
            public int ColumnCount;
            public bool AutoResizeItem;
            public string DefaultItem;
            public Int4? Margin;
            public OverflowType Overflow;
            public ScrollType ScrollType;
            public bool ScrollItemToViewOnClick;
            public bool FoldInvisibleItems;
            public List<ListItemModel> Items;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(DefaultItem);
                foreach (ListItemModel item in Items)
                {
                    item.CollectStrings(strings);
                }
            }

            public byte[] BuildBlock(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)Layout);
                writer.WriteByte((byte)SelectionMode);
                writer.WriteByte((byte)Align);
                writer.WriteByte((byte)VerticalAlign);
                writer.WriteShort((short)LineGap);
                writer.WriteShort((short)ColumnGap);
                writer.WriteShort((short)LineCount);
                writer.WriteShort((short)ColumnCount);
                writer.WriteBool(AutoResizeItem);
                writer.WriteByte((byte)ChildrenRenderOrder.Ascent);
                writer.WriteShort(0);
                writer.WriteBool(Margin.HasValue);
                if (Margin.HasValue)
                {
                    Int4 margin = Margin.Value;
                    writer.WriteInt(margin.Top);
                    writer.WriteInt(margin.Bottom);
                    writer.WriteInt(margin.Left);
                    writer.WriteInt(margin.Right);
                }
                writer.WriteByte((byte)Overflow);
                writer.WriteBool(false);
                writer.WriteBool(ScrollItemToViewOnClick);
                writer.WriteBool(FoldInvisibleItems);
                return writer.ToArray();
            }

            public byte[] BuildItemsBlock(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteS(strings, DefaultItem);
                writer.WriteShort((short)Items.Count);
                foreach (ListItemModel item in Items)
                {
                    byte[] record = item.BuildBytes(strings);
                    writer.WriteUShort((ushort)record.Length);
                    writer.WriteBytes(record);
                }
                return writer.ToArray();
            }
        }

        private sealed class ComponentInstanceModel
        {
            public ComponentInstanceModel()
            {
                ControllerStates = new List<ControllerStateModel>();
            }

            public List<ControllerStateModel> ControllerStates;

            public void CollectStrings(StringTable strings)
            {
                foreach (ControllerStateModel state in ControllerStates)
                {
                    state.CollectStrings(strings);
                }
            }
        }

        private sealed class ComponentExtensionModel
        {
            public ObjectType ObjectType;
            public ButtonMode ButtonMode;
            public string Sound;
            public float SoundVolumeScale = 1f;
            public byte DownEffect;
            public float DownEffectValue = 0.8f;
            public ProgressTitleType SliderTitleType;
            public bool SliderReverse;
            public bool SliderWholeNumbers;
            public bool SliderChangeOnClick = true;

            public void CollectStrings(StringTable strings)
            {
                if (ObjectType == ObjectType.Button)
                {
                    strings.Add(Sound);
                }
            }

            public byte[] BuildBlock(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                if (ObjectType == ObjectType.Button)
                {
                    writer.WriteByte((byte)ButtonMode);
                    writer.WriteS(strings, Sound);
                    writer.WriteFloat(SoundVolumeScale);
                    writer.WriteByte(DownEffect);
                    writer.WriteFloat(DownEffectValue);
                    return writer.ToArray();
                }
                if (ObjectType == ObjectType.Slider)
                {
                    writer.WriteByte((byte)SliderTitleType);
                    writer.WriteBool(SliderReverse);
                    writer.WriteBool(SliderWholeNumbers);
                    writer.WriteBool(SliderChangeOnClick);
                    return writer.ToArray();
                }
                return null;
            }
        }

        private sealed class ExtensionOverrideModel
        {
            public ObjectType ObjectType;
            public string Title;
            public string SelectedTitle;
            public string Icon;
            public string SelectedIcon;
            public Color32? TitleColor;
            public int TitleFontSize;
            public string Sound;
            public float? SoundVolumeScale;
            public bool Selected;
            public int Value = 50;
            public int Max = 100;
            public int Min;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(Title);
                strings.Add(SelectedTitle);
                strings.Add(Icon);
                strings.Add(SelectedIcon);
                strings.Add(Sound);
            }

            public byte[] BuildBlock(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)ObjectType);
                if (ObjectType == ObjectType.Label)
                {
                    writer.WriteS(strings, Title);
                    writer.WriteS(strings, Icon);
                    writer.WriteBool(TitleColor.HasValue);
                    if (TitleColor.HasValue)
                    {
                        writer.WriteColor(TitleColor.Value);
                    }
                    writer.WriteInt(TitleFontSize);
                    writer.WriteBool(false);
                    return writer.ToArray();
                }
                if (ObjectType == ObjectType.Button)
                {
                    writer.WriteS(strings, Title);
                    writer.WriteS(strings, SelectedTitle);
                    writer.WriteS(strings, Icon);
                    writer.WriteS(strings, SelectedIcon);
                    writer.WriteBool(TitleColor.HasValue);
                    if (TitleColor.HasValue)
                    {
                        writer.WriteColor(TitleColor.Value);
                    }
                    writer.WriteInt(TitleFontSize);
                    writer.WriteShort(-1);
                    writer.WriteS(strings, null);
                    writer.WriteS(strings, Sound);
                    writer.WriteBool(SoundVolumeScale.HasValue);
                    if (SoundVolumeScale.HasValue)
                    {
                        writer.WriteFloat(SoundVolumeScale.Value);
                    }
                    writer.WriteBool(Selected);
                    return writer.ToArray();
                }
                if (ObjectType == ObjectType.Slider)
                {
                    writer.WriteInt(Value);
                    writer.WriteInt(Max);
                    writer.WriteInt(Min);
                    return writer.ToArray();
                }
                return null;
            }
        }

        private sealed class ListItemModel
        {
            public ListItemModel()
            {
                Controllers = new List<ControllerStateModel>();
            }

            public List<ControllerStateModel> Controllers;

            public void CollectStrings(StringTable strings)
            {
                foreach (ControllerStateModel controller in Controllers)
                {
                    controller.CollectStrings(strings);
                }
            }

            public byte[] BuildBytes(StringTable strings)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteS(strings, null);
                writer.WriteShort((short)Controllers.Count);
                foreach (ControllerStateModel controller in Controllers)
                {
                    writer.WriteS(strings, controller.ControllerName);
                    writer.WriteS(strings, controller.PageId);
                }
                writer.WriteShort(0);
                return writer.ToArray();
            }
        }

        private sealed class ControllerStateModel
        {
            public ControllerStateModel(string controllerName, string pageId)
            {
                ControllerName = controllerName;
                PageId = pageId;
            }

            public string ControllerName { get; }
            public string PageId { get; }

            public void CollectStrings(StringTable strings)
            {
                strings.Add(ControllerName);
                strings.Add(PageId);
            }
        }

        private sealed class RelationModel
        {
            public RelationModel()
            {
                Sides = new List<RelationSideModel>();
            }

            public string TargetId;
            public List<RelationSideModel> Sides;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(TargetId);
            }
        }

        private sealed class RelationSideModel
        {
            public RelationSideModel(RelationType type, bool usePercent)
            {
                Type = type;
                UsePercent = usePercent;
            }

            public RelationType Type { get; }
            public bool UsePercent { get; }
        }

        private sealed class GearModel
        {
            public GearModel()
            {
                Pages = new List<string>();
                Values = new Dictionary<string, string>(StringComparer.Ordinal);
                ColorValues = new Dictionary<string, Color32>(StringComparer.Ordinal);
                PointValues = new Dictionary<string, Vector2Int>(StringComparer.Ordinal);
                SizeValues = new Dictionary<string, SizeGearValue>(StringComparer.Ordinal);
            }

            public string Kind;
            public string Controller;
            public List<string> Pages;
            public Dictionary<string, string> Values;
            public Dictionary<string, Color32> ColorValues;
            public Dictionary<string, Vector2Int> PointValues;
            public Dictionary<string, SizeGearValue> SizeValues;
            public string DefaultValue;
            public Color32? DefaultColor;
            public Vector2Int? DefaultPoint;
            public SizeGearValue? DefaultSize;
            public bool Tween;
            public EaseType EaseType;
            public float Duration;
            public float Delay;

            public void CollectStrings(StringTable strings)
            {
                strings.Add(Controller);
                foreach (string page in Pages)
                {
                    strings.Add(page);
                }
                foreach (string value in Values.Values)
                {
                    strings.Add(value);
                }
                strings.Add(DefaultValue);
            }

            public byte[] BuildBytes(StringTable strings, Dictionary<string, int> controllerIndexByName)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)ResolveGearIndex());
                int controllerIndex = FindControllerIndex(controllerIndexByName, Controller);
                writer.WriteShort((short)controllerIndex);

                if (Kind == "gearDisplay")
                {
                    writer.WriteShort((short)Pages.Count);
                    foreach (string page in Pages)
                    {
                        writer.WriteS(strings, page);
                    }
                    writer.WriteBool(false);
                    return writer.ToArray();
                }

                writer.WriteShort((short)Pages.Count);
                foreach (string page in Pages)
                {
                    writer.WriteS(strings, page);
                    WriteStatus(writer, strings, page);
                }

                bool hasDefault = DefaultValue != null || DefaultColor.HasValue || DefaultPoint.HasValue || DefaultSize.HasValue;
                writer.WriteBool(hasDefault);
                if (hasDefault)
                {
                    WriteDefaultStatus(writer, strings);
                }

                writer.WriteBool(Tween);
                if (Tween)
                {
                    writer.WriteByte((byte)EaseType);
                    writer.WriteFloat(Duration);
                    writer.WriteFloat(Delay);
                }

                if (Kind == "gearXY")
                {
                    writer.WriteBool(false);
                }

                return writer.ToArray();
            }

            private int ResolveGearIndex()
            {
                switch (Kind)
                {
                    case "gearDisplay": return 0;
                    case "gearXY": return 1;
                    case "gearSize": return 2;
                    case "gearColor": return 4;
                    case "gearText": return 6;
                    case "gearIcon": return 7;
                    default: throw new InvalidOperationException("Unsupported gear kind: " + Kind);
                }
            }

            private static int FindControllerIndex(Dictionary<string, int> controllerIndexByName, string controllerName)
            {
                if (controllerIndexByName.TryGetValue(controllerName ?? string.Empty, out int index))
                {
                    return index;
                }

                throw new InvalidOperationException("Gear references unknown controller: " + controllerName);
            }

            private void WriteStatus(BigEndianWriter writer, StringTable strings, string page)
            {
                if (Kind == "gearText" || Kind == "gearIcon")
                {
                    writer.WriteS(strings, Values.TryGetValue(page, out string value) ? value : string.Empty);
                    return;
                }

                if (Kind == "gearColor")
                {
                    Color32 color = ColorValues.TryGetValue(page, out Color32 value) ? value : new Color32(255, 255, 255, 255);
                    writer.WriteColor(color);
                    writer.WriteColor(new Color32(0, 0, 0, 0));
                    return;
                }

                if (Kind == "gearXY")
                {
                    Vector2Int point = PointValues.TryGetValue(page, out Vector2Int value) ? value : Vector2Int.zero;
                    writer.WriteInt(point.x);
                    writer.WriteInt(point.y);
                    return;
                }

                if (Kind == "gearSize")
                {
                    SizeGearValue size = SizeValues.TryGetValue(page, out SizeGearValue value) ? value : new SizeGearValue(0, 0, 1f, 1f);
                    writer.WriteInt(size.Width);
                    writer.WriteInt(size.Height);
                    writer.WriteFloat(size.ScaleX);
                    writer.WriteFloat(size.ScaleY);
                    return;
                }
            }

            private void WriteDefaultStatus(BigEndianWriter writer, StringTable strings)
            {
                if (Kind == "gearText" || Kind == "gearIcon")
                {
                    writer.WriteS(strings, DefaultValue);
                    return;
                }

                if (Kind == "gearColor")
                {
                    Color32 color = DefaultColor ?? new Color32(255, 255, 255, 255);
                    writer.WriteColor(color);
                    writer.WriteColor(new Color32(0, 0, 0, 0));
                    return;
                }

                if (Kind == "gearXY")
                {
                    Vector2Int point = DefaultPoint ?? Vector2Int.zero;
                    writer.WriteInt(point.x);
                    writer.WriteInt(point.y);
                    return;
                }

                if (Kind == "gearSize")
                {
                    SizeGearValue size = DefaultSize ?? new SizeGearValue(0, 0, 1f, 1f);
                    writer.WriteInt(size.Width);
                    writer.WriteInt(size.Height);
                    writer.WriteFloat(size.ScaleX);
                    writer.WriteFloat(size.ScaleY);
                    return;
                }
            }
        }

        private struct SizeGearValue
        {
            public SizeGearValue(int width, int height, float scaleX, float scaleY)
            {
                Width = width;
                Height = height;
                ScaleX = scaleX;
                ScaleY = scaleY;
            }

            public int Width;
            public int Height;
            public float ScaleX;
            public float ScaleY;
        }

        private struct Int4
        {
            public Int4(int top, int right, int bottom, int left)
            {
                Top = top;
                Right = right;
                Bottom = bottom;
                Left = left;
            }

            public int Top;
            public int Right;
            public int Bottom;
            public int Left;
        }

        private static string GetAtlasItemId(int atlasIndex)
        {
            return "atlas" + atlasIndex.ToString(CultureInfo.InvariantCulture);
        }

        private static ObjectType ParseComponentObjectType(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ObjectType.Component;
            }

            switch (text.Trim())
            {
                case "Button": return ObjectType.Button;
                case "Label": return ObjectType.Label;
                case "Slider": return ObjectType.Slider;
                default: return ObjectType.Component;
            }
        }

        private static OverflowType ParseOverflow(string text)
        {
            if (string.Equals(text, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                return OverflowType.Hidden;
            }
            if (string.Equals(text, "scroll", StringComparison.OrdinalIgnoreCase))
            {
                return OverflowType.Scroll;
            }
            return OverflowType.Visible;
        }

        private static ScrollType ParseScrollType(string text)
        {
            if (string.Equals(text, "horizontal", StringComparison.OrdinalIgnoreCase))
            {
                return ScrollType.Horizontal;
            }
            if (string.Equals(text, "both", StringComparison.OrdinalIgnoreCase))
            {
                return ScrollType.Both;
            }
            return ScrollType.Vertical;
        }

        private static AlignType ParseAlign(string text)
        {
            if (string.Equals(text, "center", StringComparison.OrdinalIgnoreCase))
            {
                return AlignType.Center;
            }
            if (string.Equals(text, "right", StringComparison.OrdinalIgnoreCase))
            {
                return AlignType.Right;
            }
            return AlignType.Left;
        }

        private static VertAlignType ParseVertAlign(string text)
        {
            if (string.Equals(text, "middle", StringComparison.OrdinalIgnoreCase))
            {
                return VertAlignType.Middle;
            }
            if (string.Equals(text, "bottom", StringComparison.OrdinalIgnoreCase))
            {
                return VertAlignType.Bottom;
            }
            return VertAlignType.Top;
        }

        private static AutoSizeType ParseAutoSize(string text)
        {
            if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            {
                return AutoSizeType.None;
            }
            if (string.Equals(text, "height", StringComparison.OrdinalIgnoreCase))
            {
                return AutoSizeType.Height;
            }
            if (string.Equals(text, "shrink", StringComparison.OrdinalIgnoreCase))
            {
                return AutoSizeType.Shrink;
            }
            if (string.Equals(text, "ellipsis", StringComparison.OrdinalIgnoreCase))
            {
                return AutoSizeType.Ellipsis;
            }
            return AutoSizeType.Both;
        }

        private static FillType ParseFillType(string text)
        {
            if (string.Equals(text, "scale", StringComparison.OrdinalIgnoreCase))
            {
                return FillType.Scale;
            }
            if (string.Equals(text, "scaleMatchHeight", StringComparison.OrdinalIgnoreCase))
            {
                return FillType.ScaleMatchHeight;
            }
            if (string.Equals(text, "scaleMatchWidth", StringComparison.OrdinalIgnoreCase))
            {
                return FillType.ScaleMatchWidth;
            }
            if (string.Equals(text, "scaleFree", StringComparison.OrdinalIgnoreCase))
            {
                return FillType.ScaleFree;
            }
            if (string.Equals(text, "scaleNoBorder", StringComparison.OrdinalIgnoreCase))
            {
                return FillType.ScaleNoBorder;
            }
            return FillType.None;
        }

        private static ListLayoutType ParseListLayout(string text)
        {
            if (string.Equals(text, "row", StringComparison.OrdinalIgnoreCase))
            {
                return ListLayoutType.SingleRow;
            }
            if (string.Equals(text, "flow_hz", StringComparison.OrdinalIgnoreCase))
            {
                return ListLayoutType.FlowHorizontal;
            }
            if (string.Equals(text, "flow_vt", StringComparison.OrdinalIgnoreCase))
            {
                return ListLayoutType.FlowVertical;
            }
            if (string.Equals(text, "pagination", StringComparison.OrdinalIgnoreCase))
            {
                return ListLayoutType.Pagination;
            }
            return ListLayoutType.SingleColumn;
        }

        private static ListSelectionMode ParseListSelectionMode(string text)
        {
            if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
            {
                return ListSelectionMode.None;
            }
            if (string.Equals(text, "multiple", StringComparison.OrdinalIgnoreCase))
            {
                return ListSelectionMode.Multiple;
            }
            if (string.Equals(text, "multiple_singleclick", StringComparison.OrdinalIgnoreCase))
            {
                return ListSelectionMode.Multiple_SingleClick;
            }
            return ListSelectionMode.Single;
        }

        private static GroupLayoutType ParseGroupLayout(string text)
        {
            if (string.Equals(text, "horizontal", StringComparison.OrdinalIgnoreCase))
            {
                return GroupLayoutType.Horizontal;
            }
            if (string.Equals(text, "vertical", StringComparison.OrdinalIgnoreCase))
            {
                return GroupLayoutType.Vertical;
            }
            return GroupLayoutType.None;
        }

        private static RelationType ParseRelationType(string text)
        {
            switch (text)
            {
                case "left-left": return RelationType.Left_Left;
                case "left-center": return RelationType.Left_Center;
                case "left-right": return RelationType.Left_Right;
                case "center-center": return RelationType.Center_Center;
                case "right-left": return RelationType.Right_Left;
                case "right-center": return RelationType.Right_Center;
                case "right-right": return RelationType.Right_Right;
                case "top-top": return RelationType.Top_Top;
                case "top-middle": return RelationType.Top_Middle;
                case "top-bottom": return RelationType.Top_Bottom;
                case "middle-middle": return RelationType.Middle_Middle;
                case "bottom-top": return RelationType.Bottom_Top;
                case "bottom-middle": return RelationType.Bottom_Middle;
                case "bottom-bottom": return RelationType.Bottom_Bottom;
                case "width": return RelationType.Width;
                case "width-width": return RelationType.Width;
                case "height": return RelationType.Height;
                case "height-height": return RelationType.Height;
                case "size": return RelationType.Size;
                default: throw new InvalidOperationException("Unsupported relation sidePair in official runtime exporter: " + text);
            }
        }

        private static byte ParseGraphShape(string text)
        {
            if (string.Equals(text, "rect", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(text))
            {
                return 1;
            }
            if (string.Equals(text, "eclipse", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "ellipse", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            if (string.Equals(text, "polygon", StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }
            throw new InvalidOperationException("Unsupported graph type in official runtime exporter: " + text);
        }

        private static ButtonMode ParseButtonMode(string text)
        {
            if (string.Equals(text, "Check", StringComparison.OrdinalIgnoreCase))
            {
                return ButtonMode.Check;
            }
            if (string.Equals(text, "Radio", StringComparison.OrdinalIgnoreCase))
            {
                return ButtonMode.Radio;
            }
            return ButtonMode.Common;
        }

        private static byte ParseButtonDownEffect(string text)
        {
            if (string.Equals(text, "scale", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
            return 0;
        }

        private static ProgressTitleType ParseProgressTitleType(string text)
        {
            if (string.Equals(text, "valueAndMax", StringComparison.OrdinalIgnoreCase))
            {
                return ProgressTitleType.ValueAndMax;
            }
            if (string.Equals(text, "value", StringComparison.OrdinalIgnoreCase))
            {
                return ProgressTitleType.Value;
            }
            if (string.Equals(text, "max", StringComparison.OrdinalIgnoreCase))
            {
                return ProgressTitleType.Max;
            }
            return ProgressTitleType.Percent;
        }

        private static EaseType ParseEaseType(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return EaseType.QuadOut;
            }

            string normalized = text.Replace(".", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Trim();
            foreach (EaseType value in Enum.GetValues(typeof(EaseType)))
            {
                if (string.Equals(value.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }

            return EaseType.QuadOut;
        }

        private static string[] SplitComma(string value)
        {
            return string.IsNullOrEmpty(value)
                ? Array.Empty<string>()
                : value.Split(new[] { ',' }, StringSplitOptions.None);
        }

        private static Vector2Int ParseInt2(string value, string fieldName)
        {
            string[] parts = SplitComma(value);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("Expected 'x,y' format for " + fieldName + ", got: " + value);
            }
            return new Vector2Int(ParseInt(parts[0], 0), ParseInt(parts[1], 0));
        }

        private static Vector2Int? ParseOptionalInt2(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (Vector2Int?)null : ParseInt2(value, "int2");
        }

        private static Vector2 ParseFloat2(string value, string fieldName)
        {
            string[] parts = SplitComma(value);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException("Expected 'x,y' format for " + fieldName + ", got: " + value);
            }
            return new Vector2(ParseFloat(parts[0], 0f), ParseFloat(parts[1], 0f));
        }

        private static Vector2? ParseOptionalFloat2(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (Vector2?)null : ParseFloat2(value, "float2");
        }

        private static Int4? ParseOptionalInt4(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string[] parts = SplitComma(value);
            if (parts.Length != 4)
            {
                throw new InvalidOperationException("Expected 'top,right,bottom,left' format, got: " + value);
            }

            return new Int4(
                ParseInt(parts[0], 0),
                ParseInt(parts[3], 0),
                ParseInt(parts[1], 0),
                ParseInt(parts[2], 0));
        }

        private static Vector4? ParseCornerRadii(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string[] parts = SplitComma(value);
            if (parts.Length == 1)
            {
                float radius = ParseFloat(parts[0], 0f);
                return new Vector4(radius, radius, radius, radius);
            }
            if (parts.Length == 4)
            {
                return new Vector4(
                    ParseFloat(parts[0], 0f),
                    ParseFloat(parts[1], 0f),
                    ParseFloat(parts[2], 0f),
                    ParseFloat(parts[3], 0f));
            }

            throw new InvalidOperationException("Unsupported graph corner format: " + value);
        }

        private static List<Vector2> ParsePoints(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string[] parts = SplitComma(value);
            if (parts.Length % 2 != 0)
            {
                throw new InvalidOperationException("Unsupported polygon points format: " + value);
            }

            List<Vector2> result = new List<Vector2>(parts.Length / 2);
            for (int i = 0; i < parts.Length; i += 2)
            {
                result.Add(new Vector2(ParseFloat(parts[i], 0f), ParseFloat(parts[i + 1], 0f)));
            }
            return result;
        }

        private static SizeGearValue ParseSizeGearValue(string value)
        {
            string[] parts = value.Split(',');
            if (parts.Length != 4)
            {
                throw new InvalidOperationException("Unsupported gearSize value format: " + value);
            }
            return new SizeGearValue(
                ParseInt(parts[0], 0),
                ParseInt(parts[1], 0),
                ParseFloat(parts[2], 1f),
                ParseFloat(parts[3], 1f));
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }

        private static float ParseFloat(string value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : fallback;
        }

        private static float? ParseOptionalFloat(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (float?)null : ParseFloat(value, 0f);
        }

        private static bool ParseBool(string value)
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);
        }

        private static Color32 ParseColor(string value, Color32 fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            string text = value.Trim().TrimStart('#');
            if (text.Length == 6)
            {
                byte r = byte.Parse(text.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new Color32(r, g, b, 255);
            }
            if (text.Length == 8)
            {
                byte a = byte.Parse(text.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte r = byte.Parse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(text.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new Color32(r, g, b, a);
            }

            return fallback;
        }

        private static Color32? ParseOptionalColor(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (Color32?)null : ParseColor(value, new Color32(255, 255, 255, 255));
        }

        private sealed class StringTable
        {
            private readonly Dictionary<string, ushort> _indices = new Dictionary<string, ushort>(StringComparer.Ordinal);
            private readonly List<string> _values = new List<string>();

            public IEnumerable<string> Values => _values;
            public int Count => _values.Count;

            public void Add(string value)
            {
                if (string.IsNullOrEmpty(value) || _indices.ContainsKey(value))
                {
                    return;
                }

                ushort index = (ushort)_values.Count;
                _values.Add(value);
                _indices[value] = index;
            }

            public ushort GetIndex(string value)
            {
                if (value == null)
                {
                    return 65534;
                }
                if (value.Length == 0)
                {
                    return 65533;
                }
                return _indices[value];
            }
        }

        private sealed class BigEndianWriter
        {
            private readonly MemoryStream _stream = new MemoryStream();

            public void WriteByte(byte value)
            {
                _stream.WriteByte(value);
            }

            public void WriteBytes(byte[] bytes)
            {
                if (bytes != null)
                {
                    _stream.Write(bytes, 0, bytes.Length);
                }
            }

            public void WriteBool(bool value)
            {
                WriteByte(value ? (byte)1 : (byte)0);
            }

            public void WriteShort(int value)
            {
                WriteUShort((ushort)(short)value);
            }

            public void WriteUShort(ushort value)
            {
                WriteByte((byte)((value >> 8) & 0xFF));
                WriteByte((byte)(value & 0xFF));
            }

            public void WriteInt(int value)
            {
                WriteByte((byte)((value >> 24) & 0xFF));
                WriteByte((byte)((value >> 16) & 0xFF));
                WriteByte((byte)((value >> 8) & 0xFF));
                WriteByte((byte)(value & 0xFF));
            }

            public void WriteUInt(uint value)
            {
                WriteInt(unchecked((int)value));
            }

            public void WriteFloat(float value)
            {
                byte[] bytes = BitConverter.GetBytes(value);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(bytes);
                }
                WriteBytes(bytes);
            }

            public void WriteColor(Color32 color)
            {
                WriteByte(color.r);
                WriteByte(color.g);
                WriteByte(color.b);
                WriteByte(color.a);
            }

            public void WriteStringRaw(string value)
            {
                byte[] bytes = Utf8NoBom.GetBytes(value ?? string.Empty);
                WriteUShort((ushort)bytes.Length);
                WriteBytes(bytes);
            }

            public void WriteS(StringTable strings, string value)
            {
                WriteUShort(strings.GetIndex(value));
            }

            public void WriteBuffer(byte[] bytes)
            {
                WriteInt(bytes.Length);
                WriteBytes(bytes);
            }

            public byte[] ToArray()
            {
                return _stream.ToArray();
            }
        }

        private static class IndexedBlockWriter
        {
            public static byte[] BuildIndexedBuffer(params byte[][] blocks)
            {
                BigEndianWriter writer = new BigEndianWriter();
                writer.WriteByte((byte)blocks.Length);
                writer.WriteByte(0);

                int headerSize = 2 + blocks.Length * 4;
                int offset = headerSize;
                for (int i = 0; i < blocks.Length; i++)
                {
                    byte[] block = blocks[i];
                    writer.WriteInt(block == null ? 0 : offset);
                    if (block != null)
                    {
                        offset += block.Length;
                    }
                }

                for (int i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i] != null)
                    {
                        writer.WriteBytes(blocks[i]);
                    }
                }

                return writer.ToArray();
            }
        }
    }
}

