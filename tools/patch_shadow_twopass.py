# -*- coding: utf-8 -*-
import io, sys
path = r"D:\FguiCli\Assets\FguiEditor\FairyGuiScripts\Core\Text\TextField.cs"
with io.open(path, "r", encoding="utf-8-sig", newline="") as f:
    raw = f.read()
crlf = "\r\n" in raw
text = raw.replace("\r\n", "\n")

# 1. body: remove uv.y+30 marker -> plain copy
old_body = ("\t\t\t\t// \u6587\u5b57\u672c\u4f53\u6807\u8bb0\uff1auv.y+30\uff0c\u7740\u8272\u5668\u636e\u6b64\u5c06\u6b63\u6587\u4e5f\u9510\u5316\u4e3a\u5b9e\u8272\uff0c\n"
"\t\t\t\t// \u4fdd\u8bc1\u6b63\u6587\u5b8c\u5168\u8986\u76d6\u5e95\u4e0b\u7684\u63cf\u8fb9/\u9634\u5f71\uff0c\u63cf\u8fb9\u53ea\u9732\u51fa\u5916\u73af\uff08StrokeDemo \u8bed\u4e49\uff09\n"
"\t\t\t\tfor (int i = 0; i < count; i++)\n"
"\t\t\t\t{\n"
"\t\t\t\t\tVector2 u = uvList[i];\n"
"\t\t\t\t\tif (_font.canOutline)\n"
"\t\t\t\t\t\tu.y += 30f;\n"
"\t\t\t\t\tuvBuf[start + i] = u;\n"
"\t\t\t\t}\n")
new_body = ("\t\t\t\t// \u6b63\u6587\u6b63\u5e38\u6e32\u67d3\uff08\u65e0\u4efb\u4f55 uv \u6807\u8bb0\uff09\uff0c\u76f4\u63a5\u8986\u76d6\u5728\u9634\u5f71/\u63cf\u8fb9\u4e4b\u4e0a\n"
"\t\t\t\tuvList.CopyTo(0, uvBuf, start, count);\n")
if text.count(old_body) != 1:
    print("body pattern count:", text.count(old_body)); sys.exit(1)
text = text.replace(old_body, new_body)

# 2. shadow: remove uv.y+20 marker -> plain uv copy (two-pass semantics)
old_shadow = ("\t\t\t\tif (hasShadow)\n"
"\t\t\t\t{\n"
"\t\t\t\t\t// \u5355\u5c42\u5b9e\u8fb9\u9634\u5f71\uff1a\u6309 shadowOffset \u504f\u79fb\uff0c\u4f7f\u7528 shadowColor \u539f\u8272\uff08\u65e0\u6a21\u7cca\uff09\n"
"\t\t\t\t\t// \u4e0e FairyGUI \u5b98\u65b9 VertexBuffer.GenerateShadow \u884c\u4e3a\u4e00\u81f4\n"
"\t\t\t\t\tfloat shadowX = _shadowOffset.x;\n"
"\t\t\t\t\tfloat shadowY = _shadowOffset.y;\n"
"\t\t\t\t\tint s = 0;\n"
"\t\t\t\t\tfor (int i = 0; i < count; i++)\n"
"\t\t\t\t\t{\n"
"\t\t\t\t\t\tVector3 vert = vertList[i];\n"
"\t\t\t\t\t\tVector2 u = uvList[i];\n"
"\n"
"\t\t\t\t\t\tif (_font.canOutline)\n"
"\t\t\t\t\t\t{\n"
"\t\t\t\t\t\t\t// \u9634\u5f71\u6807\u8bb0\uff1auv.y+20\uff0c\u7740\u8272\u5668\u636e\u6b64\u5c06\u9634\u5f71\u9510\u5316\u4e3a\u5b9e\u8272\u8f6e\u5ed3\n"
"\t\t\t\t\t\t\tu.y += 20f;\n"
"\t\t\t\t\t\t\tuvBuf[s] = u;\n"
"\t\t\t\t\t\t}\n"
"\t\t\t\t\t\tvertBuf[s] = new Vector3(vert.x + shadowX, vert.y - shadowY, 0);\n"
"\t\t\t\t\t\tcolBuf[s] = shadowColor;\n"
"\t\t\t\t\t\ts++;\n"
"\t\t\t\t\t}\n"
"\t\t\t\t}\n")
new_shadow = ("\t\t\t\tif (hasShadow)\n"
"\t\t\t\t{\n"
"\t\t\t\t\t// \u9634\u5f71 = \u7b2c\u4e00\u904d\u5b57\u4f53\u6e32\u67d3\uff1a\u4f4d\u7f6e + shadowOffset\uff0c\u989c\u8272 = shadowColor\uff0c\n"
"\t\t\t\t\t// uv/\u900f\u660e\u5ea6\u5747\u4e0e\u6b63\u5e38\u5b57\u4f53\u4e00\u81f4\uff08\u65e0\u6807\u8bb0\u3001\u65e0\u786c\u8fb9\u5316\uff09\uff1b\n"
"\t\t\t\t\t// \u6b63\u6587\u4f5c\u4e3a\u7b2c\u4e8c\u904d\u6e32\u67d3\u76f4\u63a5\u8986\u76d6\u5176\u4e0a\uff0c\u91cd\u53e0\u5dee\u5373\u9634\u5f71\u3002\n"
"\t\t\t\t\t// \u4e0e FairyGUI \u5b98\u65b9 VertexBuffer.GenerateShadow \u884c\u4e3a\u4e00\u81f4\n"
"\t\t\t\t\tfloat shadowX = _shadowOffset.x;\n"
"\t\t\t\t\tfloat shadowY = _shadowOffset.y;\n"
"\t\t\t\t\tint s = 0;\n"
"\t\t\t\t\tfor (int i = 0; i < count; i++)\n"
"\t\t\t\t\t{\n"
"\t\t\t\t\t\tVector3 vert = vertList[i];\n"
"\t\t\t\t\t\tuvBuf[s] = uvList[i];\n"
"\t\t\t\t\t\tvertBuf[s] = new Vector3(vert.x + shadowX, vert.y - shadowY, 0);\n"
"\t\t\t\t\t\tcolBuf[s] = shadowColor;\n"
"\t\t\t\t\t\ts++;\n"
"\t\t\t\t\t}\n"
"\t\t\t\t}\n")
if text.count(old_shadow) != 1:
    print("shadow pattern count:", text.count(old_shadow)); sys.exit(1)
text = text.replace(old_shadow, new_shadow)

if crlf:
    text = text.replace("\n", "\r\n")
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(text)
print("TextField.cs patched OK")
