from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from fgui_render_client import RenderRequest, render_once


class RenderClientTests(unittest.TestCase):
    def test_render_once_parses_result(self) -> None:
        mock_script = ROOT / "tests" / "mock_renderer.py"

        with tempfile.TemporaryDirectory() as tmp_dir:
            tmp_dir_path = Path(tmp_dir)
            output = tmp_dir_path / "out" / "panel.png"
            request = RenderRequest(
                package_dir=str(tmp_dir_path),
                package_name="Pkg",
                component_name="Panel",
                out_png=str(output),
                width=100,
                height=50,
            )

            result = render_once(
                sys.executable,
                request=request,
                timeout_sec=10,
                pre_args=[str(mock_script)],
            )

            self.assertTrue(result.ok)
            self.assertTrue(output.exists())


if __name__ == "__main__":
    unittest.main()
