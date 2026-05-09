# FGUI Render Server Skeleton
This project now includes a Unity runtime skeleton that can render one FairyGUI panel to a PNG file from command-line arguments.
## Unity scripts
- `Assets/Scripts/FguiRenderServer/FguiRenderModels.cs`
  - Parses command-line args (`--render-once`)
  - Loads exported FGUI package files from disk
  - Creates a target component and captures a PNG
  - Prints one result line with prefix `[FGUI_RENDER_RESULT]`
- `Assets/Scripts/Editor/FguiRenderBuild.cs`
  - Adds menu item `Tools/Fgui Render/Build Windows Player`
## Expected package input
Point `--package-dir` to a FairyGUI export directory that contains at least:
- `<PackageName>_fui.bytes`
- Atlas/image resources referenced by the package description

If using `packageSourceDir` pre-publish flow, the publisher must run in `OfficialRuntime` mode and produce `*_fui.bytes` before render starts.
## Build
Open Unity, then use menu:
- `Tools/Fgui Render/Build Windows Player`
Default output:
- `Build/FguiRenderServer/FguiRenderServer.exe`
## Run (PowerShell)
```powershell
Set-Location -LiteralPath 'D:\Project\FguiCli'
.\Build\FguiRenderServer\FguiRenderServer.exe `
  --render-once `
  --package-dir 'C:\fgui-export\MyPkg' `
  --package-name 'MyPkg' `
  --component-name 'MainPanel' `
  --out-png 'C:\temp\MainPanel.png' `
  --width 1920 `
  --height 1080 `
  --scale 1.0 `
  --transparent true
```
## Result contract
The player writes one log line:
- `[FGUI_RENDER_RESULT]{...json...}`
Fields:
- `ok` (bool)
- `message` (string)
- `pngPath` (string)
- `width` (int)
- `height` (int)
- `durationMs` (int)
## Python wrapper
See:
- `tools/fgui_render_client/README.md`
