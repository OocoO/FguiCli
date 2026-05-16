from __future__ import annotations

import json
import sys
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from threading import Thread

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from fgui_render_client import RenderRequest, health, render_page


class _MockHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/health":
            self._write_json(
                200,
                {
                    "ok": True,
                    "message": "ready",
                    "pendingJobs": 0,
                    "hasActiveJob": False,
                },
            )
            return
        self._write_json(404, {"ok": False})

    def do_POST(self) -> None:  # noqa: N802
        if self.path == "/render_page":
            size = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(size).decode("utf-8")
            req = json.loads(body)
            self._write_json(
                200,
                {
                    "ok": True,
                    "message": "ok",
                    "jobId": "job-123",
                    "pngPath": req.get("outPng", ""),
                    "width": req.get("width", 0),
                    "height": req.get("height", 0),
                    "durationMs": 6,
                },
            )
            return
        self._write_json(404, {"ok": False})

    def log_message(self, format: str, *args) -> None:  # noqa: A003
        return

    def _write_json(self, status: int, payload: dict) -> None:
        data = json.dumps(payload, ensure_ascii=True).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class RenderClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.server = HTTPServer(("127.0.0.1", 0), _MockHandler)
        cls.base_url = f"http://127.0.0.1:{cls.server.server_port}"
        cls.thread = Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls) -> None:
        cls.server.shutdown()
        cls.thread.join(timeout=2)

    def test_health(self) -> None:
        result = health(self.base_url)
        self.assertTrue(result.ok)
        self.assertEqual(result.message, "ready")

    def test_render_page(self) -> None:
        result = render_page(
            self.base_url,
            RenderRequest(
                project_root_dir="D:/ProjectGit/AirLegion/fgui_airLegion",
                package_name="BattleUI",
                component_name="main_FormationSelect.xml",
                out_png="D:/render/output.png",
                width=1280,
                height=720,
            ),
            timeout_sec=10,
        )
        self.assertTrue(result.ok)
        self.assertEqual(result.job_id, "job-123")
        self.assertEqual(result.png_path, "D:/render/output.png")
        self.assertEqual(result.width, 1280)
        self.assertEqual(result.height, 720)


if __name__ == "__main__":
    unittest.main()

