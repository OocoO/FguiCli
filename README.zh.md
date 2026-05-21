# FguiCli

> 一个基于 Unity 的工具，用于读取 FairyGUI XML 定义并将 UI 组件渲染为 PNG 图片。
>
> English version: [README.md](README.md)

## 简介

FguiCli 加载 FairyGUI UI 包（由 XML 和图片资源定义），在运行时实例化组件，并将其捕获为 RenderTexture/PNG 输出。它的设计目的是在不运行 FairyGUI 完整编辑器的情况下，批量导出或预览 FairyGUI UI 设计。

## 代码来源

本项目的 FairyGUI 运行时代码源自两个上游开源仓库：

- **[OpenFairyGUI](https://github.com/OpenFairyGUI/OpenFairyGUI)** — FairyGUI 运行时的开源重新实现。
- **[FairyGUI-unity](https://github.com/fairygui/FairyGUI-unity)** — FairyGUI 官方 Unity SDK。

`Assets/FguiEditor/FairyGuiScripts/` 目录下的代码改编自上述仓库。许可证请参考原始仓库。

## 功能

- **XML 解析** — 读取 `package.xml` 和组件 XML 定义，重建 UI 层级结构，包括控制器、过渡、Gears 和关联关系。
- **资源加载** — 通过 `FguiProjectLoader` 从 FairyGUI 包中加载图集、字体、MovieClip 等资源。
- **组件渲染** — 使用 FairyGUI 渲染管线从包资源实例化 `GComponent` 对象。
- **PNG 导出** — 通过 `CaptureCamera` 将渲染后的组件捕获为 `RenderTexture`，可保存为 PNG 文件。
- **分支支持** — 支持 FairyGUI 分支包（`assets_*` 目录），用于变体管理。
- **编辑模式 & 运行模式** — 既可在 Unity 编辑器编辑模式下工作（通过 `EMRenderSupport`），也可在运行时运行。

## 项目结构

```
Assets/FguiEditor/
├── FairyGuiScripts/
│   ├── Core/               # 渲染核心：Container、DisplayObject、Image、Shape 等
│   ├── UI/                 # UI 组件：GComponent、GButton、GList、UIPackage 等
│   ├── Event/              # 事件系统
│   ├── Filter/             # 视觉滤镜（模糊、色彩）
│   ├── Gesture/            # 触摸手势
│   └── Editor/             # Unity 编辑器扩展（UIPanelEditor、UIPainterEditor 等）
└── ...
```

关键文件：

| 文件 | 说明 |
|------|------|
| `UI/UIPackage.cs` | 包加载、XML 解析、资源管理 |
| `UI/FguiProjectLoader.cs` | 从文件系统扫描并加载 FairyGUI 项目 |
| `Core/CaptureCamera.cs` | 将 DisplayObject 层级渲染为 RenderTexture |
| `UI/UIPainter.cs` | 将 UI 组件绘制到带有捕获纹理的 Mesh 上 |
| `UI/UIPanel.cs` | 用于嵌入 FairyGUI 面板的 Unity MonoBehaviour 封装 |

## 使用方法

### 在 Unity 中打开

1. 克隆本仓库。
2. 用 Unity 打开项目（推荐 2021.3+）。
3. 确保 FairyGUI 项目资源（包含 `package.xml`、图片等的包）放在正确的目录。

### 加载包

```csharp
// 从 FairyGUI 项目目录加载所有包
FguiProjectLoader loader = FguiProjectLoader.LoadProject("/path/to/fgui/project", "");

// 获取指定包
UIPackage pkg = loader.GetPackage("包名");

// 创建组件
GComponent comp = (GComponent)UIPackage.CreateObject("包名", "组件名");
```

### 渲染为纹理

```csharp
// 创建与组件尺寸匹配的 RenderTexture
int width = Mathf.RoundToInt(comp.width);
int height = Mathf.RoundToInt(comp.height);
RenderTexture rt = CaptureCamera.CreateRenderTexture(width, height, false);

// 将组件捕获到纹理
CaptureCamera.Capture(comp.displayObject, rt, Vector2.zero);
```

## 环境要求

- **Unity 2021.3** 或更高版本
- 已配置 **Universal Render Pipeline (URP)**
- 定义两个 Unity Layer：`VUI`（layer 30）和 `Hidden VUI`（layer 31）

## 许可证

FairyGUI 运行时代码派生自上游开源项目，许可证请参考原始仓库：

- [OpenFairyGUI 许可证](https://github.com/OpenFairyGUI/OpenFairyGUI)
- [FairyGUI-unity 许可证](https://github.com/fairygui/FairyGUI-unity)

本项目所做的修改按原样提供。
