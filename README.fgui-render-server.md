# FGUI Render Server

Unity 内置常驻渲染服务，程序启动后监听本地端口并响应渲染请求。

## Unity scripts

- `Assets/Scripts/FguiRenderServer/Main.cs`
  - Player 启动时自动创建 `FguiRenderServerBehaviour`
- `Assets/Scripts/FguiRenderServer/FguiRenderServerBehaviour.cs`
  - 常驻 HTTP 服务（默认 `127.0.0.1:18765`）
  - 串行渲染队列（常驻进程低延迟）
  - `POST /render_page` 与 `GET /health`
  - 兼容 `--render-once` 一次性调用，并输出 `[FGUI_RENDER_RESULT]`

## API contract

### Health

- `GET /health`

响应：

```json
{
  "ok": true,
  "message": "ready",
  "pendingJobs": 0,
  "hasActiveJob": false
}
```

### Render

- `POST /render_page`
- Content-Type: `application/json`

请求：

```json
{
  "projectRootDir": "D:/ProjectGit/AirLegion/fgui_airLegion",
  "packageName": "BattleUI",
  "componentName": "main_FormationSelect.xml",
  "outPng": "D:/render/output.png",
  "branchTag": "eng",
  "width": 1920,
  "height": 1080,
  "timeoutSec": 120
}
```

> `width` / `height` 表示离屏渲染输出尺寸，不会把运行窗口改成对应分辨率。当前服务端窗口仍固定为 `1280x720`，例如可以在 `720p` 窗口下输出 `1920x1080` 的 PNG。

响应：

```json
{
  "ok": true,
  "message": "ok",
  "jobId": "...",
  "pngPath": "D:/render/output.png",
  "width": 1920,
  "height": 1080,
  "durationMs": 133
}
```

> `width` / `height` 为最终导出 PNG 的实际尺寸；当组件本身较小且被自动裁剪掉透明空白后，这两个值可能小于请求里的输出尺寸。

> 当前传回方式为本地路径 `pngPath`，适合直接被支持读图能力的 agent 消费。

## One-shot mode (compat)

```powershell
Set-Location -LiteralPath 'D:\Project\FguiCli'
.\Build\FguiRenderServer\FguiRenderServer.exe `
  --render-once `
  --project-root-dir 'D:\ProjectGit\AirLegion\fgui_airLegion' `
  --package-name 'BattleUI' `
  --component-name 'main_FormationSelect.xml' `
  --out-png 'D:\render\output.png' `
  --width 1920 `
  --height 1080 `
  --branch eng
```

输出日志行：

- `[FGUI_RENDER_RESULT]{...json...}`

## HTTP call examples (PowerShell)

```powershell
Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:18765/health'
```

```powershell
$body = @{
  projectRootDir = 'D:/ProjectGit/AirLegion/fgui_airLegion'
  packageName = 'BattleUI'
  componentName = 'main_FormationSelect.xml'
  outPng = 'D:/render/output.png'
  branchTag = 'eng'
  width = 1920
  height = 1080
  timeoutSec = 120
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:18765/render_page' -ContentType 'application/json' -Body $body
```

## Test helper

- `tools/fgui_render_client/invoke_render_page.ps1`

## Build & packaging helper

- `tools/fgui_render_client/package_fgui_render_server.ps1`
- Unity build entry: `Assets/Scripts/Editor/FguiRenderBuild.cs`

## Python wrapper

- `tools/fgui_render_client/README.md`
- `tools/fgui_render_client/fgui_render_mcp_server.py`
