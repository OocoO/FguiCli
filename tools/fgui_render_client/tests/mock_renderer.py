#!/usr/bin/env python3
from __future__ import annotations
import json
import sys
from pathlib import Path
def main() -> None:
    out_path = None
    for index, token in enumerate(sys.argv):
        if token == "--out-png" and index + 1 < len(sys.argv):
            out_path = Path(sys.argv[index + 1]).resolve()
            break
    if out_path is None:
        print("missing --out-png")
        sys.exit(1)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    # Small valid PNG header + empty IHDR/IDAT/IEND is overkill for mock; file existence is enough.
    out_path.write_bytes(b"mock")
    payload = {
        "ok": True,
        "message": "ok",
        "pngPath": str(out_path),
        "width": 100,
        "height": 50,
        "durationMs": 3,
    }
    print("[FGUI_RENDER_RESULT]" + json.dumps(payload, ensure_ascii=True))
if __name__ == "__main__":
    main()
