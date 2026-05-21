# FguiCli

> A Unity-based tool for reading FairyGUI XML definitions and rendering UI components to PNG images.
>
> 中文版请查看 [README.zh.md](README.zh.md)

## Overview

FguiCli loads FairyGUI UI packages (defined via XML and image resources), instantiates components at runtime, and captures them as RenderTexture/PNG output. It is designed for batch exporting or previewing FairyGUI UI designs without running the full FairyGUI editor.

## Code Origins

The FairyGUI runtime code in this project is derived from two upstream open-source repositories:

- **[OpenFairyGUI](https://github.com/OpenFairyGUI/OpenFairyGUI)** — an open-source re-implementation of the FairyGUI runtime.
- **[FairyGUI-unity](https://github.com/fairygui/FairyGUI-unity)** — the official FairyGUI Unity SDK.

The code in `Assets/FguiEditor/FairyGuiScripts/` is adapted from these sources. Please refer to the original repositories for their respective licenses.

## Features

- **XML Parsing** — Reads `package.xml` and component XML definitions to reconstruct the UI hierarchy, including controllers, transitions, gears, and relations.
- **Resource Loading** — Loads atlas images, fonts, movie clips, and other assets from FairyGUI packages via `FguiProjectLoader`.
- **Component Rendering** — Instantiates `GComponent` objects from package resources using the FairyGUI rendering pipeline.
- **PNG Export** — Captures rendered components to `RenderTexture` via `CaptureCamera`, which can be saved as PNG files.
- **Branch Support** — Supports FairyGUI branch packages (`assets_*` directories) for variant management.
- **Edit Mode & Play Mode** — Works both in Unity Editor edit mode (via `EMRenderSupport`) and at runtime.

## Project Structure

```
Assets/FguiEditor/
├── FairyGuiScripts/
│   ├── Core/               # Rendering core: Container, DisplayObject, Image, Shape, etc.
│   ├── UI/                 # UI components: GComponent, GButton, GList, UIPackage, etc.
│   ├── Event/              # Event system
│   ├── Filter/             # Visual filters (blur, color)
│   ├── Gesture/            # Touch gestures
│   └── Editor/             # Unity Editor extensions (UIPanelEditor, UIPainterEditor, etc.)
└── ...
```

Key files:

| File | Description |
|------|-------------|
| `UI/UIPackage.cs` | Package loading, XML parsing, asset management |
| `UI/FguiProjectLoader.cs` | Scans and loads FairyGUI project from filesystem |
| `Core/CaptureCamera.cs` | Renders DisplayObject hierarchy to RenderTexture |
| `UI/UIPainter.cs` | Paints UI components onto a mesh with captured texture |
| `UI/UIPanel.cs` | Unity MonoBehaviour wrapper for embedding FairyGUI panels |

## Usage

### Opening in Unity

1. Clone this repository.
2. Open the project in Unity (2021.3+ recommended).
3. Ensure the FairyGUI project assets (packages with `package.xml`, images, etc.) are placed in the appropriate directory.

### Loading a Package

```csharp
// Load all packages from a FairyGUI project directory
FguiProjectLoader loader = FguiProjectLoader.LoadProject("/path/to/fgui/project", "");

// Get a specific package
UIPackage pkg = loader.GetPackage("PackageName");

// Create a component
GComponent comp = (GComponent)UIPackage.CreateObject("PackageName", "ComponentName");
```

### Rendering to Texture

```csharp
// Create a RenderTexture matching the component size
int width = Mathf.RoundToInt(comp.width);
int height = Mathf.RoundToInt(comp.height);
RenderTexture rt = CaptureCamera.CreateRenderTexture(width, height, false);

// Capture the component to the texture
CaptureCamera.Capture(comp.displayObject, rt, Vector2.zero);
```

## Requirements

- **Unity 2021.3** or later
- **Universal Render Pipeline (URP)** configured
- Two Unity layers defined: `VUI` (layer 30) and `Hidden VUI` (layer 31)

## License

The FairyGUI runtime code is derived from upstream open-source projects. Please consult the original repositories for licensing details:

- [OpenFairyGUI License](https://github.com/OpenFairyGUI/OpenFairyGUI)
- [FairyGUI-unity License](https://github.com/fairygui/FairyGUI-unity)

Any modifications specific to this project are provided as-is.
