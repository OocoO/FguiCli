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
  "fullScreen": "verticalCenter",
  "controllers": [
    { "name": "StateFilter", "page": "1" }
  ],
  "transitionMode": "settle",
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
  "screenMatchMode": "MatchWidthOrHeight",
  "fullScreen": "verticalCenter",
  "panelWidth": 2560,
  "panelHeight": 1080,
  "transitionMode": "settle",
  "settledTransitions": 1,
  "availableControllers": [
    {
      "name": "StateFilter",
      "selectedIndex": 1,
      "selectedPage": "可升星",
      "pages": ["全部", "可升星", "可升级"]
    }
  ],
  "appliedControllers": [
    {
      "path": [],
      "name": "StateFilter",
      "pageCount": 3,
      "selectedIndex": 1,
      "selectedPage": "可升星"
    }
  ]
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
| `fullScreen` | `""`（= `auto`） | 面板尺寸策略，见下节 |
| `controllers` | `[]` | 抓图前要切换的控制器，见「控制器切换」 |
| `transitionMode` | `""`（= `settle`） | 过渡处理方式，见「过渡与动画」 |

### 模拟屏幕 vs 面板尺寸（`fullScreen`）

模拟屏幕（`screenWidth` / `screenHeight`）只决定 FGUI 的内容缩放系数和 `GRoot` 逻辑画布，
**组件本身默认仍按包内设计尺寸渲染**。而客户端打开面板时 `PanelManager` 会先把面板按屏幕
重设一次（`MakeFullScreenVerticalCenter`），这个 `SetSize` 会触发子件的 relation ——
所以带 `right-right` / `center-center` 或 `GGroup` relation 的页面，只按设计尺寸渲染
是永远看不到问题的（编辑器里也一样）。

`fullScreen` 用来把这一步补上：

| 取值 | 行为 |
|------|------|
| `auto`（默认） | 旧行为：组件尺寸 ≥ 设计分辨率的 85% 时按 `full` 处理，否则不动 |
| `none` | 完全不动组件，渲染包内设计尺寸（= 编辑器设计态；relation 不触发） |
| `full` | `panel.MakeFullScreen()`：宽高都铺满 `GRoot` 逻辑画布 |
| `verticalCenter` | 复刻 `PanelManager.MakeFullScreenVerticalCenter()`：宽铺满、按设计比例收高度、垂直居中，并挂三条跟随 `GRoot` 的关系 |

响应里的 `fullScreen` 是**实际生效**的策略（`auto` 会解析成 `full` 或 `none`），
`panelWidth` / `panelHeight` 是定尺寸之后面板的逻辑尺寸，用来核对自适应结果。
想看“关系有没有把内容带偏”，同一分辨率分别用 `none` 和 `verticalCenter` 各渲一张对比即可。

### 控制器切换（同一组件的其它状态）

按钮选中态、页签、空态/满级、列表筛选……在 FGUI 里都是控制器 + `gearDisplay` 驱动的，
固定按设计态渲染只能看到默认那一页。`controllers` 用来在抓图前把控制器切到指定页面。

请求里的每一项：

```json
{ "name": "StateFilter", "page": "1", "path": ["main", "talentList"] }
```

| 字段 | 说明 |
|------|------|
| `name` | 控制器名（组件 XML 里的 `<controller name="...">`），必填 |
| `page` | 页面索引（`"1"`）或页面名（`"可升星"`）；空 = 保持当前页 |
| `path` | 从面板根开始逐级往下找子组件的名字链；空 = 面板根本身 |

控制器在面板上舞台之后才切换，所以 `onChanged`、控制器动作里的 `play_transition` 都会照常触发。
绑定了该控制器的列表（`selectionController`，页签列表就是这种）会一起切到同一页，
页签高亮和页面内容对得上。
名字或页面找不到时整个请求失败（`ok=false`，`message` 里带具体名字和路径）——
拼错一个控制器名不会静默渲染出一张没切换的图。

响应里顺带回两份清单，方便不读 XML 也能换页：

- `availableControllers`：面板根组件上的控制器清单（名字、当前页、全部页面名）。
  先渲一张拿到页面名，再按页面名逐张换。
- `appliedControllers`：本次实际切换过的记录（path / name / pageCount / 切换后索引 / 切换后页面名）。

```powershell
$body = @{
  projectRootDir = 'D:/ProjectGit/AirLegion/fgui_airLegion'
  packageName = 'DarkLord'
  componentName = 'DarkLordTalentPanel.xml'
  outPng = 'D:/render/talent-starable.png'
  fullScreen = 'verticalCenter'
  controllers = @(@{ name = 'StateFilter'; page = '可升星' })
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:18765/render_page' -ContentType 'application/json' -Body $body
```

### 过渡与动画（`transitionMode`）

FairyGUI 的 transition 是**按帧推进**的：`autoPlay` 的入场动效、控制器动作里的
`play_transition`（页签切换那一段淡入/位移）都在组件上舞台后开始播，每帧走一点。

原来的渲染器在上舞台后固定等两帧再抓图，抓到的就是“当前播到第几帧”。
帧预算一变（首帧要编译 shader、加载纹理），同一份请求就会产出不同的图——
带入场动效的面板连渲两次都可能一张全空、一张半透明。

`transitionMode` 决定抓图前怎么处理这些动效：

| 取值 | 行为 |
|------|------|
| `settle`（默认） | 正在播的 transition 全部跳到最后一帧（`Transition.Stop(true, false)`），控制器 gear 的补间随后直接完成（`DOTween.CompleteAll`） |
| `raw` | 不干预，抓图时播到第几帧就是第几帧（旧行为，用来复现/对比抖动） |

响应里的 `transitionMode` 是实际生效的方式，`settledTransitions` 是被归位到终态的 transition 数量
（`raw` 时恒为 0）。

`settle` 之后，入场动效、页签切换的淡入/位移都停在终态，同一份请求的静态部分每次一致。
组件内自播的 movieclip 动画（等级徽章的循环光效之类）不受影响，仍按帧推进，
两张截图之间会有这一小块区域的细微差别。

只做“跳到终态”，不做按帧采样：一次请求产出一张静止图，看不到动画的中间过程。

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
`--full-screen <auto|none|full|vertical-center>`（不带值等于 `full`）、
`--controllers "StateFilter=1;BloodMax=满级"`（分号分隔，`name=页面索引或页面名`）、
`--transition-mode <settle|raw>`、
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

- `tools/fgui_render_client/rebuild_render_server.ps1`：停掉常驻服务 → Unity 批量构建 → 重新拉起服务。
  **必须先关掉 FguiCli 的 Unity 编辑器**（批量构建打不开被占用的工程），脚本会自己检查锁文件。
  编辑器开着时也可以直接用菜单 `Tools/Fgui Render/Build Windows Player To Render Client`（要先停掉常驻服务，
  否则运行中的 exe 被锁定、构建写不进去）。
- Unity build entry: `Assets/Scripts/Editor/FguiRenderBuild.cs`

## Python wrapper

- `tools/fgui_render_client/README.md`
- `tools/fgui_render_client/fgui_render_mcp_server.py`
