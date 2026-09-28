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
  "width": 0,
  "height": 0,
  "screenWidth": 2560,
  "screenHeight": 1080,
  "timeoutSec": 120
}
```

响应：

```json
{
  "ok": true,
  "message": "ok",
  "jobId": "...",
  "pngPath": "D:/render/output.png",
  "width": 2560,
  "height": 1080,
  "durationMs": 133,
  "screenWidth": 2560,
  "screenHeight": 1080,
  "logicalWidth": 2560,
  "logicalHeight": 1080,
  "contentScaleFactor": 1,
  "designResolutionX": 1920,
  "designResolutionY": 1080,
  "screenMatchMode": "MatchWidthOrHeight"
}
```

> `width` / `height` 为最终导出 PNG 的实际尺寸；`width` / `height` 传 0 时跟随模拟屏幕，
> 并且当组件本身较小、透明空白被裁掉后，这两个值可能小于请求里的输出尺寸
> （用 `keepFullFrame: true` 可以保留完整画面）。

## 屏幕自适应测试（分辨率模拟）

用于验证 UI 在不同屏幕下的自适应表现。核心概念是**模拟屏幕**（即游戏分辨率），
它决定 FGUI 的内容缩放系数和 `GRoot` 的逻辑画布尺寸，与渲染器自身窗口无关，
所以可以在 1080p 显示器上直接预览 4K、带鱼屏、竖屏等分辨率。

渲染器窗口会**跟着模拟屏幕变形**：高度恒定（默认 `720`），宽度 = `高度 × screenWidth / screenHeight`，
这样看到的窗口和渲染出来的画面是同一个形状。想彻底不动窗口就传 `keepWindowSize: true`。
窗口尺寸本身不参与自适应计算：渲染始终走离屏贴图，输出像素与窗口无关。

| 模拟屏幕 | 窗口尺寸（高度 720） | 窗口比例 |
|----------|----------------------|----------|
| 1920 x 1080 | 1280 x 720 | 16:9 |
| 2560 x 1080 | 1707 x 720 | 21:9 |
| 2048 x 1536 | 960 x 720 | 4:3 |
| 1080 x 1920 | 405 x 720 | 9:16 |

窗口默认会自动最小化；想看窗口形状就用 `--no-minimize` 启动服务。

### 请求参数

| 字段 | 默认 | 说明 |
|------|------|------|
| `screenWidth` / `screenHeight` | 0 | 模拟屏幕（游戏分辨率）；0 = 跟随 `width`/`height`，再退到 `1920x1080` |
| `width` / `height` | 0 | 输出 PNG 尺寸；0 = 跟随模拟屏幕（即 1:1 的设备截图） |
| `designResolutionX` / `designResolutionY` | 0 | 设计分辨率；0 = 读项目 `settings/Adaptation.json` |
| `scaleMode` | `""` | `ConstantPixelSize` / `ScaleWithScreenSize` / `ConstantPhysicalSize`；空 = 读项目配置 |
| `screenMatchMode` | `""` | `MatchWidthOrHeight` / `MatchWidth` / `MatchHeight`；空 = 读项目配置 |
| `ignoreOrientation` | `false` | 忽略设计分辨率的横竖屏方向修正（与项目配置取或） |
| `windowHeight` | 0 | 渲染器窗口高度；0 = 用启动高度（默认 `720`） |
| `windowWidth` | 0 | 渲染器窗口宽度；0 = 按模拟屏幕比例自动计算 |
| `keepWindowSize` | `false` | `true` = 完全不动窗口，保持启动尺寸 |
| `keepFullFrame` | `false` | `true` = 不裁掉四周透明像素 |

### 自适应链路说明

- 缩放系数按 `UIContentScaler.ApplyChange` 的规则计算，但屏幕尺寸取自 `screenWidth` / `screenHeight`：
  - `MatchWidthOrHeight`：`min(screenW / designW, screenH / designH)`
  - `MatchWidth`：`screenW / designW`；`MatchHeight`：`screenH / designH`
- `GRoot` 逻辑尺寸 = `ceil(screenW / scaleFactor)` × `ceil(screenH / scaleFactor)`，即 UI 真正拿到的画布。
- 全屏页面判定改为与**设计分辨率**比较（而不是与当前屏幕的逻辑尺寸比较），
  这样按 1920x1080 设计的全屏页面在任何模拟分辨率下都会铺满屏幕。
- 相机视野按模拟屏幕重新摆放；输出比例与模拟屏幕比例不一致时整体缩放到完整可见，
  多出来的透明边随后会被裁掉，不会因为比例不同而切掉 UI。

### 常见测试分辨率

| 场景 | screenWidth x screenHeight | MatchWidthOrHeight 下的逻辑画布 |
|------|----------------------------|----------------------------------|
| 设计基准 | 1920 x 1080 | 1920 x 1080（f=1） |
| 720p | 1280 x 720 | 1920 x 1080（f=0.667） |
| 2K 手机（20:9） | 2400 x 1080 | 2400 x 1080（f=1） |
| 带鱼屏（21:9） | 2560 x 1080 | 2560 x 1080（f=1） |
| iPad 4:3 | 2048 x 1536 | 1920 x 1440（f=1.067） |
| 竖屏 | 1080 x 1920 | 1080 x 1920（f=1，设计分辨率会做方向修正） |

### 调用示例（PowerShell）

```powershell
$body = @{
  projectRootDir = 'D:/ProjectGit/AirLegion/fgui_airLegion'
  packageName = 'BattleUI'
  componentName = 'main_FormationSelect.xml'
  outPng = 'D:/render/ultrawide.png'
  screenWidth = 2560
  screenHeight = 1080
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:18765/render_page' -ContentType 'application/json' -Body $body
```

## One-shot mode (compat)

```powershell
Set-Location -LiteralPath 'D:\Project\FguiCli'
.\Build\FguiRenderServer\FguiRenderServer.exe `
  --render-once `
  --project-root-dir 'D:\ProjectGit\AirLegion\fgui_airLegion' `
  --package-name 'BattleUI' `
  --component-name 'main_FormationSelect.xml' `
  --out-png 'D:\render\output.png' `
  --screen-width 2560 `
  --screen-height 1080 `
  --branch eng
```

一次性模式可用参数：`--width` / `--height`（输出尺寸）、`--screen-width` / `--screen-height`（模拟屏幕）、
`--design-width` / `--design-height`、`--match`、`--scale-mode`、`--ignore-orientation`、
`--keep-full-frame`、`--window-height` / `--window-width` / `--keep-window-size`（窗口尺寸，默认按比例自适应）。

> 玩家设置里 `forceSingleInstance = 1`，所以 `--render-once` 只能在**没有常驻渲染服务在跑**的时候使用，
> 否则新进程会直接退出且不产出文件。

输出日志行约定为 `[FGUI_RENDER_RESULT]{...json...}`，实际写在 Unity 玩家日志里
（`%USERPROFILE%\AppData\LocalLow\DefaultCompany\FguiCli\Player.log`），而不是标准输出。

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
