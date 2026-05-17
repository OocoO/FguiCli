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
- `width`: defaults to `1920`
- `height`: defaults to `1080`
- `timeoutSec`: defaults to `120`

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

