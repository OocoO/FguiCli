# -*- coding: utf-8 -*-
import io, sys

def patch(path, old, new):
    with io.open(path, "r", encoding="utf-8-sig", newline="") as f:
        raw = f.read()
    crlf = "\r\n" in raw
    text = raw.replace("\r\n", "\n")
    if text.count(old) != 1:
        print(path, "pattern count:", text.count(old)); sys.exit(1)
    text = text.replace(old, new)
    if crlf:
        text = text.replace("\n", "\r\n")
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)
    print(path, "patched OK")

base = r"D:\FguiCli\Assets\FguiEditor\FairyGuiScripts\Resources\Shaders"

# FairyGUI-Text.shader: solidify only stroke (effect==1); shadow/body render normally
patch(base + r"\FairyGUI-Text.shader",
"\t\t\t\t\t// stroke/shadow: solidify glyph alpha into crisp silhouette (no blur)\n"
"\t\t\t\t\tif(i.effect > 0.5)\n"
"\t\t\t\t\t\tcol.a = i.color.a * step(0.4, ta);\n",
"\t\t\t\t\t// stroke only: solidify glyph alpha into crisp silhouette (no blur)\n"
"\t\t\t\t\t// shadow/body render as normal glyphs (two-pass text semantics)\n"
"\t\t\t\t\tif(i.effect > 0.5 && i.effect < 1.5)\n"
"\t\t\t\t\t\tcol.a = i.color.a * step(0.4, ta);\n")

# FairyGUI-BMFont.shader: same, via flags.z
patch(base + r"\FairyGUI-BMFont.shader",
"\t\t\t\t\t// stroke/shadow: solidify glyph alpha into crisp silhouette (no blur)\n"
"\t\t\t\t\tif(i.flags.z > 0.5)\n"
"\t\t\t\t\t\tcol.a = i.color.a * step(0.4, ta);\n",
"\t\t\t\t\t// stroke only: solidify glyph alpha into crisp silhouette (no blur)\n"
"\t\t\t\t\t// shadow/body render as normal glyphs (two-pass text semantics)\n"
"\t\t\t\t\tif(i.flags.z > 0.5 && i.flags.z < 1.5)\n"
"\t\t\t\t\t\tcol.a = i.color.a * step(0.4, ta);\n")
