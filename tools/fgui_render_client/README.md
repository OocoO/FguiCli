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
- `fullScreen`: panel sizing strategy, `""` = `auto`. Values: `auto` (legacy heuristic: a panel close
  to the design resolution is made full screen), `none` (keep the packaged design size), `full`
  (`MakeFullScreen()`), `verticalCenter` (copy of `PanelManager.MakeFullScreenVerticalCenter()`).
  Use it to reproduce the game's layout: the game resizes the panel to the screen, and that
  `SetSize` is what triggers relation / `GGroup` movement — rendering the design size never shows it.
- `controllers`: controller pages to switch before the capture, defaults to `[]`. Each entry is
  `{name, page, path}`:
  - `name` (required): controller name from the component XML (`<controller name="...">`).
  - `page`: page index (`"1"`) or page name (`"可升星"`); empty keeps the current page.
  - `path`: child name chain from the panel root; empty means the panel itself.
  Switching happens after the panel is on stage, so controller actions (`play_transition`) run too.
  An unknown controller or page fails the render instead of returning an unchanged screenshot.
- `transitionMode`: `settle` (default) jumps every playing transition to its end state and completes
  gear tweens before the capture, so a capture never lands in the middle of an opening animation
  (MovieClips that animate on their own still advance, so they can differ slightly between renders);
  `raw` keeps the old behaviour where the captured frame depends on frame timing. There is no
  frame-accurate animation sampling.
- `timeoutSec`: defaults to `120`

The result also reports `availableControllers` (every controller on the panel root, with its pages
and current page), `appliedControllers` (what this render switched), `transitionMode` and
`settledTransitions`.

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

Example: render a controller's other page (the "可升星" tab of the dark lord talent panel).

```python
RenderRequest(
	project_root_dir="D:/ProjectGit/AirLegion/fgui_airLegion",
	package_name="DarkLord",
	out_png="D:/render/talent_starable.png",
	component_name="DarkLordTalentPanel.xml",
	full_screen="verticalCenter",
	controllers=[{"name": "StateFilter", "page": "可升星"}],
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

