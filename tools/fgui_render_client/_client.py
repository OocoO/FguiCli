from __future__ import annotations

import dataclasses
import json
from typing import Any
from urllib import error, request

DEFAULT_SERVER = "http://127.0.0.1:18765"


@dataclasses.dataclass
class RenderRequest:
    project_root_dir: str
    package_name: str
    out_png: str
    component_name: str = ""
    component_path: str = ""
    component_id: str = ""
    branch_tag: str = ""
    # Output PNG size. 0 = follow the simulated screen resolution.
    width: int = 0
    height: int = 0
    # Simulated device screen (game resolution) used for UI adaptation.
    # 0 = follow width/height, falling back to 1920x1080.
    screen_width: int = 0
    screen_height: int = 0
    # Design resolution. 0 = read settings/Adaptation.json from the project.
    design_resolution_x: int = 0
    design_resolution_y: int = 0
    # Adaptation mode. Empty = read settings/Adaptation.json from the project.
    scale_mode: str = ""
    screen_match_mode: str = ""
    ignore_orientation: bool = False
    # Renderer window. 0 = auto: constant height (720 by default) with the width derived from the
    # simulated screen aspect ratio, so the visible window matches the rendered frame.
    window_height: int = 0
    window_width: int = 0
    keep_window_size: bool = False
    # Keep the full capture frame instead of trimming transparent borders.
    keep_full_frame: bool = False
    # Panel sizing strategy (what the game's PanelManager does to the panel before showing it):
    #   "auto"           - legacy heuristic: panels close to the design resolution go full screen
    #   "none"           - keep the packaged design size (relations never fire)
    #   "full"           - MakeFullScreen(): fill the GRoot logical canvas
    #   "verticalCenter" - copy of PanelManager.MakeFullScreenVerticalCenter():
    #                      full width, letterboxed height, vertically centered
    full_screen: str = ""
    # Controller pages to switch before the capture, e.g.
    #   [{"name": "StateFilter", "page": "1"}]
    #   [{"path": ["main"], "name": "Tab", "page": "可升星"}]
    # `path` is the child name chain from the panel root (empty = the panel itself);
    # `page` is a page index ("1") or a page name ("可升星"); empty keeps the current page.
    controllers: list[dict[str, Any]] = dataclasses.field(default_factory=list)
    # How transitions are handled before the capture:
    #   "settle" (default) - jump every playing transition to its end state and complete tweens
    #   "raw"              - leave them running, so the captured frame depends on frame timing
    transition_mode: str = ""
    timeout_sec: int = 120


@dataclasses.dataclass
class RenderResult:
    ok: bool
    message: str
    job_id: str
    png_path: str
    width: int
    height: int
    duration_ms: int
    screen_width: int = 0
    screen_height: int = 0
    logical_width: int = 0
    logical_height: int = 0
    content_scale_factor: float = 0.0
    design_resolution_x: int = 0
    design_resolution_y: int = 0
    screen_match_mode: str = ""
    window_width: int = 0
    window_height: int = 0
    # Panel sizing actually applied (auto / none / full / verticalCenter) and the resulting size.
    full_screen: str = ""
    panel_width: int = 0
    panel_height: int = 0
    # Controllers found on the panel root (with their pages) and the switches this render applied.
    available_controllers: list[dict[str, Any]] = dataclasses.field(default_factory=list)
    applied_controllers: list[dict[str, Any]] = dataclasses.field(default_factory=list)
    # Transition handling actually applied (settle / raw) and how many transitions were settled.
    transition_mode: str = ""
    settled_transitions: int = 0


@dataclasses.dataclass
class HealthResult:
    ok: bool
    message: str
    pending_jobs: int
    has_active_job: bool


def render_page(server_url: str, req: RenderRequest, timeout_sec: int | None = None) -> RenderResult:
    _validate_component_selector(req)
    payload = {
        "projectRootDir": req.project_root_dir,
        "packageName": req.package_name,
        "componentName": req.component_name,
        "componentPath": req.component_path,
        "componentId": req.component_id,
        "outPng": req.out_png,
        "branchTag": req.branch_tag,
        "width": req.width,
        "height": req.height,
        "screenWidth": req.screen_width,
        "screenHeight": req.screen_height,
        "designResolutionX": req.design_resolution_x,
        "designResolutionY": req.design_resolution_y,
        "scaleMode": req.scale_mode,
        "screenMatchMode": req.screen_match_mode,
        "ignoreOrientation": req.ignore_orientation,
        "windowHeight": req.window_height,
        "windowWidth": req.window_width,
        "keepWindowSize": req.keep_window_size,
        "keepFullFrame": req.keep_full_frame,
        "fullScreen": req.full_screen,
        "controllers": req.controllers,
        "transitionMode": req.transition_mode,
        "timeoutSec": req.timeout_sec,
    }
    data = _post_json(server_url.rstrip("/") + "/render_page", payload, timeout_sec=timeout_sec)
    return RenderResult(
        ok=bool(data.get("ok", False)),
        message=str(data.get("message", "")),
        job_id=str(data.get("jobId", "")),
        png_path=str(data.get("pngPath", "")),
        width=int(data.get("width", 0)),
        height=int(data.get("height", 0)),
        duration_ms=int(data.get("durationMs", 0)),
        screen_width=int(data.get("screenWidth", 0)),
        screen_height=int(data.get("screenHeight", 0)),
        logical_width=int(data.get("logicalWidth", 0)),
        logical_height=int(data.get("logicalHeight", 0)),
        content_scale_factor=float(data.get("contentScaleFactor", 0.0)),
        design_resolution_x=int(data.get("designResolutionX", 0)),
        design_resolution_y=int(data.get("designResolutionY", 0)),
        screen_match_mode=str(data.get("screenMatchMode", "")),
        window_width=int(data.get("windowWidth", 0)),
        window_height=int(data.get("windowHeight", 0)),
        full_screen=str(data.get("fullScreen", "")),
        panel_width=int(data.get("panelWidth", 0)),
        panel_height=int(data.get("panelHeight", 0)),
        available_controllers=list(data.get("availableControllers") or []),
        applied_controllers=list(data.get("appliedControllers") or []),
        transition_mode=str(data.get("transitionMode", "")),
        settled_transitions=int(data.get("settledTransitions", 0)),
    )


def health(server_url: str, timeout_sec: int = 10) -> HealthResult:
    data = _get_json(server_url.rstrip("/") + "/health", timeout_sec=timeout_sec)
    return HealthResult(
        ok=bool(data.get("ok", False)),
        message=str(data.get("message", "")),
        pending_jobs=int(data.get("pendingJobs", 0)),
        has_active_job=bool(data.get("hasActiveJob", False)),
    )


def _post_json(url: str, payload: dict[str, Any], timeout_sec: int | None) -> dict[str, Any]:
    req = request.Request(
        url=url,
        method="POST",
        data=json.dumps(payload, ensure_ascii=True).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    timeout = timeout_sec if timeout_sec is not None else max(int(payload.get("timeoutSec", 120)) + 5, 10)
    return _send(req, timeout)


def _get_json(url: str, timeout_sec: int) -> dict[str, Any]:
    req = request.Request(url=url, method="GET")
    return _send(req, timeout_sec)


def _send(req: request.Request, timeout_sec: int) -> dict[str, Any]:
    try:
        with request.urlopen(req, timeout=timeout_sec) as resp:
            text = resp.read().decode("utf-8")
            return json.loads(text)
    except error.HTTPError as ex:
        body = ex.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {ex.code}: {body}") from ex
    except error.URLError as ex:
        raise RuntimeError(f"Render server request failed: {ex}") from ex


def _validate_component_selector(req: RenderRequest) -> None:
    selectors = [
        bool(req.component_name and req.component_name.strip()),
        bool(req.component_path and req.component_path.strip()),
        bool(req.component_id and req.component_id.strip()),
    ]
    count = sum(selectors)
    if count != 1:
        raise ValueError("Exactly one of component_name / component_path / component_id must be set")
    if req.component_id and req.component_id.strip() and not req.component_id.strip().startswith("ui://"):
        raise ValueError("component_id must start with 'ui://'")


