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
    width: int = 1920
    height: int = 1080
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


