from __future__ import annotations

import os

from mcp.server.fastmcp import FastMCP

from fgui_render_client import DEFAULT_SERVER, RenderRequest, health, render_page

mcp = FastMCP("fgui-render")


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
    mcp.run(transport="stdio")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())