from __future__ import annotations

import asyncio
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from _client import RenderResult
from fgui_render_mcp_server import mcp
from mcp.server.fastmcp.exceptions import ToolError


class McpServerTests(unittest.TestCase):
    def test_tools_expose_controller_options(self) -> None:
        tools = asyncio.run(mcp.list_tools())
        names = {tool.name for tool in tools}
        self.assertIn("check_health", names)
        self.assertIn("render_page_tool", names)

        render = next(tool for tool in tools if tool.name == "render_page_tool")
        properties = render.inputSchema["properties"]
        self.assertIn("controllers", properties)
        self.assertIn("transitionMode", properties)

    @patch("fgui_render_mcp_server._start_render_server")
    @patch("fgui_render_mcp_server.render_page")
    def test_render_page_forwards_controllers(self, mock_render_page, _mock_start) -> None:
        mock_render_page.return_value = RenderResult(
            ok=True,
            message="ok",
            job_id="job-1",
            png_path="D:/render/output.png",
            width=1920,
            height=1080,
            duration_ms=10,
            available_controllers=[
                {
                    "name": "StateFilter",
                    "selectedIndex": 1,
                    "selectedPage": "可升星",
                    "pages": ["全部", "可升星", "可升级"],
                }
            ],
            applied_controllers=[
                {
                    "path": [],
                    "name": "StateFilter",
                    "pageCount": 3,
                    "selectedIndex": 1,
                    "selectedPage": "可升星",
                }
            ],
            transition_mode="settle",
            settled_transitions=1,
        )

        blocks, _ = asyncio.run(
            mcp.call_tool(
                "render_page_tool",
                {
                    "projectRootDir": "D:/ProjectGit/AirLegion/fgui_airLegion",
                    "packageName": "DarkLord",
                    "componentName": "DarkLordTalentPanel.xml",
                    "outPng": "D:/render/output.png",
                    "controllers": [{"name": "StateFilter", "page": "可升星"}],
                },
            )
        )
        text = "".join(getattr(block, "text", "") for block in blocks)
        self.assertIn("StateFilter", text)
        self.assertIn("settle", text)

        request = mock_render_page.call_args[0][1]
        self.assertEqual(
            request.controllers,
            [{"name": "StateFilter", "page": "可升星", "path": []}],
        )

    def test_component_selector_validation(self) -> None:
        with self.assertRaisesRegex(ToolError, "Exactly one"):
            asyncio.run(
                mcp.call_tool(
                    "render_page_tool",
                    {
                        "projectRootDir": "D:/ProjectGit/AirLegion/fgui_airLegion",
                        "packageName": "DarkLord",
                        "outPng": "D:/render/output.png",
                    },
                )
            )


if __name__ == "__main__":
    unittest.main()
