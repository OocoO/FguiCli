from __future__ import annotations

import atexit
import json
import os
import subprocess
import sys
import time
from pathlib import Path

from mcp.server.fastmcp import FastMCP
from pydantic import BaseModel, Field

from _client import DEFAULT_SERVER, RenderRequest, health, render_page


class ControllerSelection(BaseModel):
    """One controller page switch applied before the capture."""

    name: str = Field(description="Controller name on the target component")
    page: str = Field(
        default="",
        description='Page index ("1") or page name ("可升星"); empty = keep the current page',
    )
    path: list[str] = Field(
        default_factory=list,
        description="Child name chain from the panel root; empty = the panel itself",
    )

_EXE_DIR = Path(__file__).parent / "FguiRenderServer"
_EXE_PATH = _EXE_DIR / "FguiRenderServer.exe"
_PID_FILE = _EXE_DIR / "fgui_render_server.pid"
_POLL_INTERVAL = 0.5
_START_TIMEOUT = 30

mcp = FastMCP("fgui-render")

_render_process: subprocess.Popen[str] | None = None


def _is_server_alive(url: str) -> bool:
    try:
        result = health(url, timeout_sec=2)
        return result.ok or bool(result.message)
    except Exception:
        return False


def _start_render_server() -> None:
    global _render_process
    url = _get_server_url()

    if _is_server_alive(url):
        print(f"[fgui-render] server already running at {url}", file=sys.stderr)
        if _PID_FILE.exists():
            try:
                pid = int(_PID_FILE.read_text().strip())
                print(f"[fgui-render] existing server PID: {pid}", file=sys.stderr)
            except (ValueError, OSError):
                pass
        return

    _PID_FILE.unlink(missing_ok=True)

    if not _EXE_PATH.exists():
        print(f"[fgui-render] exe not found: {_EXE_PATH}", file=sys.stderr)
        return

    _render_process = subprocess.Popen(
        [str(_EXE_PATH)],
        cwd=str(_EXE_DIR),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    try:
        _PID_FILE.write_text(str(_render_process.pid))
    except OSError:
        pass

    atexit.register(_stop_render_server)

    deadline = time.monotonic() + _START_TIMEOUT
    while time.monotonic() < deadline:
        if _is_server_alive(url):
            print(f"[fgui-render] server ready at {url}", file=sys.stderr)
            return
        time.sleep(_POLL_INTERVAL)

    print(
        f"[fgui-render] server did not become ready within {_START_TIMEOUT}s",
        file=sys.stderr,
    )


def _stop_render_server() -> None:
    global _render_process
    if _render_process is not None:
        _render_process.terminate()
        try:
            _render_process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            _render_process.kill()
            _render_process.wait()
        _render_process = None
    try:
        _PID_FILE.unlink(missing_ok=True)
    except OSError:
        pass


@mcp.tool()
def check_health() -> str:
    _start_render_server()
    result = health(_get_server_url())
    return (
        f"ok={result.ok}, message={result.message}, "
        f"pendingJobs={result.pending_jobs}, hasActiveJob={result.has_active_job}"
    )


@mcp.tool()
def render_page_tool(
    projectRootDir: str,
    packageName: str,
    outPng: str,
    componentName: str = "",
    componentPath: str = "",
    componentId: str = "",
    branchTag: str = "",
    width: int = 0,
    height: int = 0,
    screenWidth: int = 0,
    screenHeight: int = 0,
    designResolutionX: int = 0,
    designResolutionY: int = 0,
    scaleMode: str = "",
    screenMatchMode: str = "",
    ignoreOrientation: bool = False,
    windowHeight: int = 0,
    windowWidth: int = 0,
    keepWindowSize: bool = False,
    keepFullFrame: bool = False,
    fullScreen: str = "",
    controllers: list[ControllerSelection] | None = None,
    transitionMode: str = "",
    timeoutSec: int = 120,
) -> str:
    """Render one component.

    Provide exactly one selector:
    - componentName: e.g. SoldierSkillUpgradePanel
    - componentPath: e.g. Main/SoldierListPanel.xml
    - componentId: e.g. ui://3qbfu3hkscr325

    Controller variants (how to see the other states of the same component):
    - controllers: switch controller pages before the capture, e.g.
      [{"name": "StateFilter", "page": "1"}] or [{"name": "StateFilter", "page": "可升星"}].
      * "page" accepts a page index or a page name; omit it to keep the current page.
      * "path" optionally points at a nested component (child name chain from the panel root);
        omit it to target the panel itself.
      * An unknown controller / page fails the render with a message naming it, so a typo never
        silently produces an unchanged screenshot.
    - The response lists "availableControllers" (every controller on the panel root with its pages
      and current page) and "appliedControllers" (what this render switched), so render once to
      discover the names, then re-render per page.
    - Components hide/show through controller gears, so switching pages is how you see the other
      tabs / empty states / level variants of the same panel.

    Transitions and animations:
    - FairyGUI transitions do run, but a still capture lands on whatever frame the render loop
      happens to reach, so the same request can produce different images (an autoPlay opening
      animation fades the panel in).
    - transitionMode="settle" (default) jumps every playing transition to its end state and
      completes gear tweens before the capture, so a capture never lands in the middle of an
      opening animation. MovieClips that animate on their own still advance between renders.
      Use "raw" to keep the old racing behaviour. There is no frame-accurate animation sampling:
      this renders one still frame per call.

    Screen adaptation testing:
    - screenWidth/screenHeight: simulated device screen (the game resolution the UI adapts
      to). This is the only thing that changes the UI layout. 0 = follow width/height, then
      1920x1080.
    - width/height: output PNG size. 0 = follow screenWidth/screenHeight (a native-resolution
      device screenshot). When the aspect ratio differs from the screen, the whole screen is
      scaled to fit and the leftover transparent border is trimmed.
    - designResolutionX/Y, scaleMode, screenMatchMode, ignoreOrientation: adaptation settings.
      Left at 0/empty they are read from the project's settings/Adaptation.json.
    - windowHeight/windowWidth: the renderer's own window. By default it keeps a constant height
      (720) and derives its width from the simulated screen aspect ratio, so the visible window
      has the same shape as the rendered frame (1707x720 for 2560x1080, 405x720 for portrait).
      Pass keepWindowSize=True to leave the window alone.
    - keepFullFrame: keep the full capture frame instead of trimming transparent borders.

    Panel sizing (what the game does to the panel before showing it):
    - fullScreen: "auto" (default) / "none" / "full" / "verticalCenter".
      * "auto"           - legacy: a panel close to the design resolution is made full screen.
      * "none"           - keep the packaged design size, so no relation fires (pure design state).
      * "full"           - MakeFullScreen(): fill the GRoot logical canvas.
      * "verticalCenter" - copy of PanelManager.MakeFullScreenVerticalCenter(): full width,
        letterboxed height, vertically centered, plus the GRoot relations.
      Use "verticalCenter" to reproduce the game's layout when the panel has right/bottom
      relations or GGroup relations: those only move when the panel is resized.

    Examples: 2560x1080 ultrawide -> screenWidth=2560, screenHeight=1080;
    1080x1920 portrait -> screenWidth=1080, screenHeight=1920.
    """
    _validate_component_selector(componentName, componentPath, componentId)
    _start_render_server()

    req = RenderRequest(
        project_root_dir=projectRootDir,
        package_name=packageName,
        out_png=outPng,
        component_name=componentName,
        component_path=componentPath,
        component_id=componentId,
        branch_tag=branchTag,
        width=width,
        height=height,
        screen_width=screenWidth,
        screen_height=screenHeight,
        design_resolution_x=designResolutionX,
        design_resolution_y=designResolutionY,
        scale_mode=scaleMode,
        screen_match_mode=screenMatchMode,
        ignore_orientation=ignoreOrientation,
        window_height=windowHeight,
        window_width=windowWidth,
        keep_window_size=keepWindowSize,
        keep_full_frame=keepFullFrame,
        full_screen=fullScreen,
        controllers=[selection.model_dump() for selection in controllers] if controllers else [],
        transition_mode=transitionMode,
        timeout_sec=timeoutSec,
    )
    result = render_page(_get_server_url(), req, timeout_sec=timeoutSec + 5)
    return (
        f"ok={result.ok}, message={result.message}, jobId={result.job_id}, "
        f"pngPath={result.png_path}, width={result.width}, height={result.height}\n"
        f"screen={result.screen_width}x{result.screen_height}, "
        f"logical={result.logical_width}x{result.logical_height}, "
        f"contentScaleFactor={result.content_scale_factor}, "
        f"design={result.design_resolution_x}x{result.design_resolution_y}, "
        f"matchMode={result.screen_match_mode}\n"
        f"fullScreen={result.full_screen}, "
        f"panel={result.panel_width}x{result.panel_height}, "
        f"window={result.window_width}x{result.window_height}, "
        f"durationMs={result.duration_ms}\n"
        f"transitionMode={result.transition_mode}, settledTransitions={result.settled_transitions}\n"
        f"availableControllers={json.dumps(result.available_controllers, ensure_ascii=False)}\n"
        f"appliedControllers={json.dumps(result.applied_controllers, ensure_ascii=False)}"
    )


def _get_server_url() -> str:
    return os.environ.get("FGUI_RENDER_SERVER_URL", DEFAULT_SERVER)


def _validate_component_selector(component_name: str, component_path: str, component_id: str) -> None:
    selectors = [bool(component_name.strip()), bool(component_path.strip()), bool(component_id.strip())]
    count = sum(selectors)
    if count != 1:
        raise ValueError("Exactly one of componentName / componentPath / componentId must be provided")
    if component_id.strip() and not component_id.strip().startswith("ui://"):
        raise ValueError("componentId must start with 'ui://'")


def main() -> int:
    mcp.run(transport="stdio")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())