from __future__ import annotations

import atexit
import os
import subprocess
import sys
import time
from pathlib import Path

from mcp.server.fastmcp import FastMCP

from fgui_render_client import DEFAULT_SERVER, RenderRequest, health, render_page

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
    result = health(_get_server_url())
    return (
        f"ok={result.ok}, message={result.message}, "
        f"pendingJobs={result.pending_jobs}, hasActiveJob={result.has_active_job}"
    )


@mcp.tool()
def render_page_tool(
    projectRootDir: str,
    packageName: str,
    componentName: str,
    outPng: str,
    branchTag: str = "",
    width: int = 1920,
    height: int = 1080,
    timeoutSec: int = 120,
) -> str:
    req = RenderRequest(
        project_root_dir=projectRootDir,
        package_name=packageName,
        component_name=componentName,
        out_png=outPng,
        branch_tag=branchTag,
        width=width,
        height=height,
        timeout_sec=timeoutSec,
    )
    result = render_page(_get_server_url(), req, timeout_sec=timeoutSec + 5)
    return (
        f"ok={result.ok}, message={result.message}, jobId={result.job_id}, "
        f"pngPath={result.png_path}, width={result.width}, height={result.height}, "
        f"durationMs={result.duration_ms}"
    )


def _get_server_url() -> str:
    return os.environ.get("FGUI_RENDER_SERVER_URL", DEFAULT_SERVER)


def main() -> int:
    _start_render_server()
    mcp.run(transport="stdio")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())