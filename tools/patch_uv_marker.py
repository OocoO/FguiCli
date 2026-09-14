# -*- coding: utf-8 -*-
import io, sys
path = r"D:\FguiCli\Assets\FguiEditor\FairyGuiScripts\Core\Text\TextField.cs"
with io.open(path, "r", encoding="utf-8-sig", newline="") as f:
    raw = f.read()
crlf = "\r\n" in raw
text = raw.replace("\r\n", "\n")

old_stroke = "\t\t\t\t\t\t\tif (_font.canOutline)\n\t\t\t\t\t\t\tuvBuf[start] = u;\n"
new_stroke = ("\t\t\t\t\t\t\tif (_font.canOutline)\n"
              "\t\t\t\t\t\t\t{\n"
              "\t\t\t\t\t\t\t\t// \u63cf\u8fb9\u6807\u8bb0\uff1auv.y+10\uff0c\u7740\u8272\u5668\u636e\u6b64\u5c06\u63cf\u8fb9\u9510\u5316\u4e3a\u5b9e\u8272\u8f6e\u5ed3\n"
              "\t\t\t\t\t\t\t\tu.y += 10f;\n"
              "\t\t\t\t\t\t\t\tuvBuf[start] = u;\n"
              "\t\t\t\t\t\t\t}\n")
if text.count(old_stroke) != 1:
    print("stroke pattern count:", text.count(old_stroke)); sys.exit(1)
text = text.replace(old_stroke, new_stroke)

old_shadow = "\t\t\t\t\t\tif (_font.canOutline)\n\t\t\t\t\t\t\tuvBuf[s] = u;\n"
new_shadow = ("\t\t\t\t\t\tif (_font.canOutline)\n"
              "\t\t\t\t\t\t{\n"
              "\t\t\t\t\t\t\t// \u9634\u5f71\u6807\u8bb0\uff1auv.y+20\uff0c\u7740\u8272\u5668\u636e\u6b64\u5c06\u9634\u5f71\u9510\u5316\u4e3a\u5b9e\u8272\u8f6e\u5ed3\n"
              "\t\t\t\t\t\t\tu.y += 20f;\n"
              "\t\t\t\t\t\t\tuvBuf[s] = u;\n"
              "\t\t\t\t\t\t}\n")
if text.count(old_shadow) != 1:
    print("shadow pattern count:", text.count(old_shadow)); sys.exit(1)
text = text.replace(old_shadow, new_shadow)

if crlf:
    text = text.replace("\n", "\r\n")
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(text)
print("TextField patched OK")
