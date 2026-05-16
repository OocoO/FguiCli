# FGUI Render Client (Python)
This folder provides a minimal Python wrapper for the Unity renderer executable.
## Files
- `invoke_render_page.ps1`: call always-on HTTP render server
## Quick test (no Unity needed)
```bash
python -m unittest discover -s tools/fgui_render_client/tests -v
```
## Example call with real renderer exe
```bash
python tools/fgui_render_client/run_render_once.py \
  --exe Build/FguiRenderServer/FguiRenderServer.exe \
  --package-dir C:/fgui-export/MyPkg \
  --package-name MyPkg \
  --component-name MainPanel \
  --out-png C:/tmp/main-panel.png \
  --width 1920 \
  --height 1080 \
  --scale 1.0 \
  --transparent
```

## Always-on HTTP server call (PowerShell)

```powershell
& "tools/fgui_render_client/invoke_render_page.ps1" `
  -ProjectRootDir "D:/ProjectGit/AirLegion/fgui_airLegion" `
  -PackageName "BattleUI" `
  -ComponentName "main_FormationSelect.xml" `
  -OutPng "D:/render/output.png" `
  -BranchTag "eng"
```
