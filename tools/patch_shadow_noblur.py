# -*- coding: utf-8 -*-
import io, sys
path = r"D:\FguiCli\Assets\FguiEditor\FairyGuiScripts\Core\Text\TextField.cs"
with io.open(path, "r", encoding="utf-8-sig") as f:
    lines = f.readlines()

out = []
i = 0
patched_alloc = False
while i < len(lines):
    line = lines[i]
    if ((not patched_alloc) and line.strip() == "if (hasShadow)"
            and i + 1 < len(lines) and lines[i+1].strip() == "allocCount += count * 4;"):
        out.append(line)
        out.append(lines[i+1].replace("count * 4", "count"))
        patched_alloc = True
        i += 2
        continue
    out.append(line)
    i += 1
if not patched_alloc:
    print("alloc patch failed"); sys.exit(1)

text = "".join(out)
start_marker = "\t\t\t\tif (hasShadow)\n\t\t\t\t{\n\t\t\t\t\tfloat shadowX"
si = text.find(start_marker)
if si == -1:
    print("shadow start not found"); sys.exit(1)
end_marker = "\t\t\t}\n\t\t\telse\n"
ei = text.find(end_marker, si)
if ei == -1:
    print("shadow end not found"); sys.exit(1)

new_block = (
"\t\t\t\tif (hasShadow)\n"
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
"\t\t\t\t\t\t\tuvBuf[s] = u;\n"
"\t\t\t\t\t\tvertBuf[s] = new Vector3(vert.x + shadowX, vert.y - shadowY, 0);\n"
"\t\t\t\t\t\tcolBuf[s] = shadowColor;\n"
"\t\t\t\t\t\ts++;\n"
"\t\t\t\t\t}\n"
"\t\t\t\t}\n"
)
text = text[:si] + new_block + text[ei:]
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(text)
print("patched OK")
