# Rebuild FguiRenderServer.exe (the resident FGUI render player) and restart it.
#
# Two things lock the output exe:
#   1. the resident render server process itself (it runs FguiRenderServer.exe), and
#   2. the FguiCli Unity editor: a batch build cannot open a project another instance has open.
# Close the editor first, then run this script from any shell.

[CmdletBinding()]
param(
    [string]$UnityExe = 'C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe',
    [switch]$NoRestart,
    # 只停掉常驻服务（给“在编辑器里点菜单构建”用），不做构建。
    [switch]$StopServerOnly
)

$ErrorActionPreference = 'Stop'

$clientDir = $PSScriptRoot
$projectRoot = Split-Path -Parent (Split-Path -Parent $clientDir)
$serverDir = Join-Path $clientDir 'FguiRenderServer'
$serverExe = Join-Path $serverDir 'FguiRenderServer.exe'
$pidFile = Join-Path $serverDir 'fgui_render_server.pid'

$lockFile = Join-Path $projectRoot 'Temp\UnityLockfile'

if (-not $StopServerOnly -and (Test-Path $lockFile)) {
    throw "FguiCli 的 Unity 编辑器还开着（$lockFile 存在）。先关掉编辑器（或直接用菜单 Tools/Fgui Render/Build Windows Player To Render Client 构建）再跑本脚本。"
}

if (-not $StopServerOnly -and -not (Test-Path $UnityExe)) {
    throw "找不到 Unity.exe：$UnityExe（用 -UnityExe 指定）"
}

# 1. 停掉常驻渲染服务：运行中的 exe 被 Windows 锁定，构建会写不进去。
$stopped = @()
if (Test-Path $pidFile) {
    $rawPid = (Get-Content -LiteralPath $pidFile -Raw).Trim()
    if ($rawPid -match '^\d+$') {
        $proc = Get-Process -Id ([int]$rawPid) -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -like 'FguiRenderServer*') {
            Stop-Process -Id $proc.Id -Force
            $stopped += $proc.Id
        }
    }
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}
Get-Process -Name 'FguiRenderServer' -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.Id -Force
    $stopped += $_.Id
}
if ($stopped.Count -gt 0) {
    "已停止渲染服务进程：$($stopped -join ', ')"
    Start-Sleep -Milliseconds 800
}

if ($StopServerOnly) {
    "服务已停止。现在可以在编辑器里点 Tools/Fgui Render/Build Windows Player To Render Client 构建。"
    return
}

# 2. 批量构建（构建入口见 Assets/Scripts/Editor/FguiRenderBuild.cs）。
$previousExeTime = if (Test-Path $serverExe) { (Get-Item $serverExe).LastWriteTimeUtc } else { [datetime]::MinValue }
$logFile = Join-Path $env:TEMP 'fgui_render_server_build.log'

# Unity.exe 是 GUI 子系统程序，直接 & 调用不会等待，必须 Start-Process -Wait。
$build = Start-Process -FilePath $UnityExe -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
    '-batchmode', '-quit',
    '-projectPath', $projectRoot,
    '-executeMethod', 'FguiRenderServer.Editor.FguiRenderBuild.BuildForRenderClientToolFromCommandLine',
    '-logFile', $logFile
)

if ($build.ExitCode -ne 0) {
    "构建退出码：$($build.ExitCode)，日志：$logFile"
    if (Test-Path $logFile) {
        Get-Content $logFile -Tail 40
    }
    throw 'FguiRenderServer 构建失败'
}

if (-not (Test-Path $serverExe) -or (Get-Item $serverExe).LastWriteTimeUtc -le $previousExeTime) {
    throw "构建没有产出新的 exe：$serverExe（看日志 $logFile）"
}

"构建完成：$serverExe（$((Get-Item $serverExe).LastWriteTime)）"

# 3. 重新拉起常驻服务，和 MCP server 的启动方式保持一致（隐藏窗口 + 写 pid 文件）。
if (-not $NoRestart) {
    $process = Start-Process -FilePath $serverExe -WorkingDirectory $serverDir -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath $pidFile -Value $process.Id
    "已启动渲染服务：pid $($process.Id)（日志见 Player.log）"
}
