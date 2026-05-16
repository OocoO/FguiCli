from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from fgui_render_client import HealthResult, RenderResult
from fgui_render_mcp_server import FguiRenderMcpServer


class McpServerTests(unittest.TestCase):
    def test_tools_list(self) -> None:
        server = FguiRenderMcpServer("http://127.0.0.1:18765")
        response = server.handle_request({"jsonrpc": "2.0", "id": 1, "method": "tools/list"})
        self.assertIsNotNone(response)
        tools = response["result"]["tools"]
        names = {tool["name"] for tool in tools}
        self.assertIn("health", names)
        self.assertIn("render_page", names)

    @patch("fgui_render_mcp_server.health")
    def test_health_call(self, mock_health) -> None:
        mock_health.return_value = HealthResult(
            ok=True,
            message="ready",
            pending_jobs=0,
            has_active_job=False,
        )
        server = FguiRenderMcpServer("http://127.0.0.1:18765")
        response = server.handle_request(
            {
                "jsonrpc": "2.0",
                "id": 2,
                "method": "tools/call",
                "params": {"name": "health", "arguments": {}},
            }
        )
        self.assertIsNotNone(response)
        payload = json.loads(response["result"]["content"][0]["text"])
        self.assertTrue(payload["ok"])

    @patch("fgui_render_mcp_server.render_page")
    def test_render_page_call(self, mock_render_page) -> None:
        mock_render_page.return_value = RenderResult(
            ok=True,
            message="ok",
            job_id="job-1",
            png_path="D:/render/output.png",
            width=1920,
            height=1080,
            duration_ms=10,
        )
        server = FguiRenderMcpServer("http://127.0.0.1:18765")
        response = server.handle_request(
            {
                "jsonrpc": "2.0",
                "id": 3,
                "method": "tools/call",
                "params": {
                    "name": "render_page",
                    "arguments": {
                        "projectRootDir": "D:/ProjectGit/AirLegion/fgui_airLegion",
                        "packageName": "BattleUI",
                        "componentName": "main_FormationSelect.xml",
                        "outPng": "D:/render/output.png",
                    },
                },
            }
        )
        self.assertIsNotNone(response)
        payload = json.loads(response["result"]["content"][0]["text"])
        self.assertEqual(payload["jobId"], "job-1")


if __name__ == "__main__":
    unittest.main()

