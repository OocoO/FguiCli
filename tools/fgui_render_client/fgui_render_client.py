from __future__ import annotations

import dataclasses
import json
import subprocess
from typing import Optional, Sequence

RESULT_PREFIX = "[FGUI_RENDER_RESULT]"


@dataclasses.dataclass
class RenderRequest:
    package_dir: str
    package_name: str
    component_name: str
    out_png: str
    width: int = 1920
    height: int = 1080
    scale: float = 1.0
    transparent: bool = True


@dataclasses.dataclass
class RenderResult:
    ok: bool
    message: str
    png_path: str
    width: int
    height: int
    duration_ms: int


def _build_args(exe_path: str, request: RenderRequest, pre_args: Optional[Sequence[str]]) -> list[str]:
    args = [exe_path]
    if pre_args:
        args.extend(pre_args)

    args.extend(
        [
            "--render-once",
            "--package-dir",
            request.package_dir,
            "--package-name",
            request.package_name,
            "--component-name",
            request.component_name,
            "--out-png",
            request.out_png,
            "--width",
            str(request.width),
            "--height",
            str(request.height),
            "--scale",
            str(request.scale),
            "--transparent",
            str(request.transparent).lower(),
        ]
    )

    return args


def render_once(
    exe_path: str,
    request: RenderRequest,
    timeout_sec: int = 120,
    pre_args: Optional[Sequence[str]] = None,
) -> RenderResult:
    process = subprocess.run(
        _build_args(exe_path, request, pre_args),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        timeout=timeout_sec,
        check=False,
    )

    parsed = _parse_result(process.stdout)
    if parsed is None:
        raise RuntimeError("Renderer did not emit a result line. Output:\n" + process.stdout)

    return parsed


def _parse_result(output: str) -> Optional[RenderResult]:
    for line in output.splitlines():
        if RESULT_PREFIX in line:
            payload = line.split(RESULT_PREFIX, 1)[1].strip()
            data = json.loads(payload)
            return RenderResult(
                ok=bool(data.get("ok", False)),
                message=str(data.get("message", "")),
                png_path=str(data.get("pngPath", "")),
                width=int(data.get("width", 0)),
                height=int(data.get("height", 0)),
                duration_ms=int(data.get("durationMs", 0)),
            )

    return None


if __name__ == "__main__":
    # Tiny local sanity check for parser only.
    sample = '[FGUI_RENDER_RESULT]{"ok":true,"message":"ok","pngPath":"out.png","width":100,"height":50,"durationMs":10}'
    result = _parse_result(sample)
    assert result and result.ok
    print("parser_ok")
