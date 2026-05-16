from __future__ import annotations

import json
import os
import sys
from typing import Any

from fgui_render_client import DEFAULT_SERVER, RenderRequest, health, render_page


class FguiRenderMcpServer:
    def __init__(self, server_url: str) -> None:
        self.server_url = server_url

    def handle_request(self, message: dict[str, Any]) -> dict[str, Any] | None:
        method = message.get("method")
        req_id = message.get("id")

        if method == "notifications/initialized":
            return None

        if method == "initialize":
            return {
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "fgui-render-mcp", "version": "0.1.0"},
                },
            }

        if method == "ping":
            return {"jsonrpc": "2.0", "id": req_id, "result": {}}

        if method == "tools/list":
            return {
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {
                    "tools": [
                        {
                            "name": "health",
                            "description": "Check Unity render server status.",
                            "inputSchema": {
                                "type": "object",
                                "properties": {},
                                "additionalProperties": False,
                            },
                        },
                        {
                            "name": "render_page",
                            "description": "Render a FairyGUI page and return local pngPath.",
                            "inputSchema": {
                                "type": "object",
                                "properties": {
                                    "projectRootDir": {"type": "string"},
                                    "packageName": {"type": "string"},
                                    "componentName": {"type": "string"},
                                    "outPng": {"type": "string"},
                                    "branchTag": {"type": "string"},
                                    "width": {"type": "integer", "default": 1920},
                                    "height": {"type": "integer", "default": 1080},
                                    "timeoutSec": {"type": "integer", "default": 120},
                                },
                                "required": ["projectRootDir", "packageName", "componentName", "outPng"],
                                "additionalProperties": False,
                            },
                        },
                    ]
                },
            }

        if method == "tools/call":
            params = message.get("params") or {}
            name = params.get("name")
            arguments = params.get("arguments") or {}
            try:
                payload = self._call_tool(name=name, arguments=arguments)
                return {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "result": {
                        "content": [
                            {
                                "type": "text",
                                "text": json.dumps(payload, ensure_ascii=True),
                            }
                        ]
                    },
                }
            except Exception as ex:
                return {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "error": {"code": -32000, "message": str(ex)},
                }

        return {
            "jsonrpc": "2.0",
            "id": req_id,
            "error": {"code": -32601, "message": f"Method not found: {method}"},
        }

    def _call_tool(self, name: str, arguments: dict[str, Any]) -> dict[str, Any]:
        if name == "health":
            result = health(self.server_url)
            return {
                "ok": result.ok,
                "message": result.message,
                "pendingJobs": result.pending_jobs,
                "hasActiveJob": result.has_active_job,
            }

        if name == "render_page":
            req = RenderRequest(
                project_root_dir=str(arguments.get("projectRootDir", "")),
                package_name=str(arguments.get("packageName", "")),
                component_name=str(arguments.get("componentName", "")),
                out_png=str(arguments.get("outPng", "")),
                branch_tag=str(arguments.get("branchTag", "")),
                width=int(arguments.get("width", 1920)),
                height=int(arguments.get("height", 1080)),
                timeout_sec=int(arguments.get("timeoutSec", 120)),
            )
            result = render_page(self.server_url, req, timeout_sec=req.timeout_sec + 5)
            return {
                "ok": result.ok,
                "message": result.message,
                "jobId": result.job_id,
                "pngPath": result.png_path,
                "width": result.width,
                "height": result.height,
                "durationMs": result.duration_ms,
            }

        raise ValueError(f"Unknown tool: {name}")


def _read_message(input_stream: Any) -> dict[str, Any] | None:
    headers: dict[str, str] = {}

    while True:
        line = input_stream.readline()
        if not line:
            return None
        if line in (b"\r\n", b"\n"):
            break

        text = line.decode("utf-8", errors="replace").strip()
        if not text:
            continue

        parts = text.split(":", 1)
        if len(parts) == 2:
            headers[parts[0].strip().lower()] = parts[1].strip()

    content_length = int(headers.get("content-length", "0"))
    if content_length <= 0:
        raise ValueError("Missing or invalid Content-Length")

    body = input_stream.read(content_length)
    if len(body) != content_length:
        raise ValueError("Unexpected EOF while reading request body")

    return json.loads(body.decode("utf-8"))


def _write_message(output_stream: Any, message: dict[str, Any]) -> None:
    payload = json.dumps(message, ensure_ascii=True).encode("utf-8")
    header = f"Content-Length: {len(payload)}\r\n\r\n".encode("ascii")
    output_stream.write(header)
    output_stream.write(payload)
    output_stream.flush()


def serve_stdio(server_url: str) -> int:
    server = FguiRenderMcpServer(server_url=server_url)

    while True:
        try:
            message = _read_message(sys.stdin.buffer)
            if message is None:
                return 0

            response = server.handle_request(message)
            if response is not None:
                _write_message(sys.stdout.buffer, response)
        except Exception as ex:
            error = {
                "jsonrpc": "2.0",
                "id": None,
                "error": {
                    "code": -32700,
                    "message": f"Parse/handle error: {ex}",
                },
            }
            _write_message(sys.stdout.buffer, error)


def main() -> int:
    url = os.environ.get("FGUI_RENDER_SERVER_URL", DEFAULT_SERVER)
    return serve_stdio(url)


if __name__ == "__main__":
    raise SystemExit(main())

