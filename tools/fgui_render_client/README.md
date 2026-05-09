# FGUI Render Client (Python)
This folder provides a minimal Python wrapper for the Unity renderer executable.
## Files
- `fgui_render_client.py`: wrapper API (`render_once`)
- `run_render_once.py`: command-line runner
- `tests/mock_renderer.py`: mock renderer for parser/protocol tests
- `tests/test_client.py`: unittest harness
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

# 方式 A：直接指定已发布目录（原有用法不变）
FguiRenderServer.exe --render-once `
  --package-dir "D:\out\BattleUI" `
--package-name "BattleUI" `
  --component-name "com_damageFloat" `
--out-png "D:\render\output.png"

# 方式 B：指定源目录，自动发布再渲染（新功能）
FguiRenderServer.exe --render-once `
  --package-source-dir "D:\ProjectGit\AirLegion\fgui_airLegion\assets\BattleUI" `
--component-name "com_damageFloat" `
--out-png "D:\render\output.png"
