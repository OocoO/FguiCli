from __future__ import annotations
import argparse
from fgui_render_client import RenderRequest, render_once
def main() -> None:
    parser = argparse.ArgumentParser(description="Call FGUI Unity renderer once.")
    parser.add_argument("--exe", required=True)
    parser.add_argument("--package-dir", required=True)
    parser.add_argument("--package-name", required=True)
    parser.add_argument("--component-name", required=True)
    parser.add_argument("--out-png", required=True)
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--scale", type=float, default=1.0)
    parser.add_argument("--transparent", action="store_true")
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()
    request = RenderRequest(
        package_dir=args.package_dir,
        package_name=args.package_name,
        component_name=args.component_name,
        out_png=args.out_png,
        width=args.width,
        height=args.height,
        scale=args.scale,
        transparent=args.transparent,
    )
    result = render_once(args.exe, request, timeout_sec=args.timeout)
    print(result)
if __name__ == "__main__":
    main()
