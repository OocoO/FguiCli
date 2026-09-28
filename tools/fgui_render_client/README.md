# FGUI Render MCP Wrapper (Python)

This folder contains a Python MCP server that wraps the Unity render service (`FguiRenderServer.exe`) over HTTP.

## Files

- `fgui_render_client.py`: HTTP client for `GET /health` and `POST /render_page`
- `fgui_render_mcp_server.py`: MCP stdio server (`tools/list`, `tools/call`)
- `run_mcp_server.py`: tiny runner for local start
- `requirements.txt`: dependencies (stdlib only)
- `tests/test_client.py`: HTTP client tests (mock server)
- `tests/test_mcp_server.py`: MCP handler tests

## `render_page_tool` Input Spec

Common required params:

- `projectRootDir`: FGUI project root path
- `packageName`: package name (for example `BattleUI`)
- `outPng`: output png absolute path

Component selector params (exactly one must be set, never multiple):

- `componentName`: component name, e.g. `SoldierSkillUpgradePanel`
- `componentPath`: package-relative xml path, e.g. `Main/SoldierListPanel.xml`
- `componentId`: FairyGUI URL id, must start with `ui://`, e.g. `ui://3qbfu3hkscr325`

Optional params:

- `branchTag`: defaults to `""`
- `width` / `height`: output PNG size, defaults to `0` (= follow `screenWidth`/`screenHeight`)
- `screenWidth` / `screenHeight`: simulated device screen (game resolution) used for UI adaptation,
  defaults to `0` (= follow `width`/`height`, then `1920x1080`). This is the only knob that changes
  the UI layout, so any resolution can be tested on any monitor.
- `designResolutionX` / `designResolutionY`: defaults to `0` (= read the project's `settings/Adaptation.json`)
- `scaleMode`: `ConstantPixelSize` / `ScaleWithScreenSize` / `ConstantPhysicalSize`, `""` = project config
- `screenMatchMode`: `MatchWidthOrHeight` / `MatchWidth` / `MatchHeight`, `""` = project config
- `ignoreOrientation`: defaults to `false`
- `windowHeight` / `windowWidth`: the renderer's own window, defaults to `0` = auto. It keeps a
  constant height (720) and derives its width from the simulated screen aspect ratio, so the visible
  window has the same shape as the rendered frame (1707x720 for 2560x1080, 405x720 for portrait).
- `keepWindowSize`: defaults to `false`; `true` leaves the window alone
- `keepFullFrame`: defaults to `false`; `true` keeps the full capture frame instead of trimming
  transparent borders
- `timeoutSec`: defaults to `120`

Example: render the same page as a 21:9 ultrawide screenshot.

```python
RenderRequest(
	project_root_dir="D:/ProjectGit/AirLegion/fgui_airLegion",
	package_name="BattleUI",
	out_png="D:/render/ultrawide.png",
	component_name="main_FormationSelect.xml",
	screen_width=2560,
	screen_height=1080,
)
```

## Quick test

```powershell
Set-Location -LiteralPath 'D:\Project\FguiCli'
$env:PYTHONDONTWRITEBYTECODE = '1'
python -m unittest discover -s tools/fgui_render_client/tests -v
```

## Start Unity render server

```powershell
Set-Location -LiteralPath 'D:\Project\FguiCli\tools\fgui_render_client\FguiRenderServer'
.\FguiRenderServer.exe
```

## Start MCP wrapper (stdio)

```powershell
Set-Location -LiteralPath 'D:\Project\FguiCli\tools\fgui_render_client'
$env:FGUI_RENDER_SERVER_URL = 'http://127.0.0.1:18765'
python .\run_mcp_server.py
```

## Optional: direct one-shot HTTP call

```powershell
Set-Location -LiteralPath 'D:\Project\FguiCli\tools\fgui_render_client'
python -c "from fgui_render_client import RenderRequest, render_page; print(render_page('http://127.0.0.1:18765', RenderRequest(project_root_dir='D:/ProjectGit/AirLegion/fgui_airLegion', package_name='BattleUI', component_name='main_FormationSelect.xml', out_png='D:/render/output.png', branch_tag='eng')))"
```

Examples for three selector styles:

```python
RenderRequest(
	project_root_dir="D:/ProjectGit/AirLegion/fgui_airLegion",
	package_name="BattleUI",
	out_png="D:/render/by_name.png",
	component_name="SoldierSkillUpgradePanel",
)

RenderRequest(
	project_root_dir="D:/ProjectGit/AirLegion/fgui_airLegion",
	package_name="BattleUI",
	out_png="D:/render/by_path.png",
	component_path="Main/SoldierListPanel.xml",
)

RenderRequest(
	project_root_dir="D:/ProjectGit/AirLegion/fgui_airLegion",
	package_name="BattleUI",
	out_png="D:/render/by_id.png",
	component_id="ui://3qbfu3hkscr325",
)
```

