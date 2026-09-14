# -*- coding: utf-8 -*-
import io, sys
p = r"D:\FguiCli\Assets\FguiEditor\FairyGuiScripts\Resources\Shaders\FairyGUI-BMFont.shader"
with io.open(p, "r", encoding="utf-8-sig", newline="") as f:
    raw = f.read()
crlf = "\r\n" in raw
text = raw.replace("\r\n", "\n")

old_v2f = "\t\t\t\t\tfixed4 color : COLOR;\n\t\t\t\t\tfloat2 texcoord : TEXCOORD0;\n\t\t\t\t\tfixed2 flags : TEXCOORD1;"
new_v2f = ("\t\t\t\t\tfixed4 color : COLOR;\n\t\t\t\t\tfloat2 texcoord : TEXCOORD0;\n"
           "\t\t\t\t\t// flags.x=channel, flags.y=grayed, flags.z=effect(0 normal,1 stroke,2 shadow)\n"
           "\t\t\t\t\tfixed3 flags : TEXCOORD1;")
assert text.count(old_v2f) == 1, "bm v2f"
text = text.replace(old_v2f, new_v2f)

old_vert = ("\t\t\t\t\tfloat2 texcoord = v.texcoord;\n"
            "\t\t\t\t\to.flags.x = floor(texcoord.x/10);\n"
            "\t\t\t\t\ttexcoord.x = texcoord.x - o.flags.x*10;\n"
            "\t\t\t\t\t\t\n"
            "\t\t\t\t\t#ifdef GRAYED\n"
            "\t\t\t\t\tif(texcoord.y >1)\n"
            "\t\t\t\t\t{\n"
            "\t\t\t\t\t\ttexcoord.y = texcoord.y - 10;\n"
            "\t\t\t\t\t\to.flags.y = 1;\n"
            "\t\t\t\t\t}\n"
            "\t\t\t\t\telse\n"
            "\t\t\t\t\t\to.flags.y = 0;\n"
            "\t\t\t\t\t#else\n"
            "\t\t\t\t\t\to.flags.y = 0;\n"
            "\t\t\t\t\t#endif\n"
            "\t\t\t\t\t\n"
            "\t\t\t\t\to.texcoord = texcoord;")
new_vert = ("\t\t\t\t\tfloat2 texcoord = v.texcoord;\n"
            "\t\t\t\t\to.flags.x = floor(texcoord.x/10);\n"
            "\t\t\t\t\ttexcoord.x = texcoord.x - o.flags.x*10;\n"
            "\t\t\t\t\t\t\n"
            "\t\t\t\t\t// decode effect marker: uv.y+10 = stroke, uv.y+20 = shadow\n"
            "\t\t\t\t\tfloat effectFlag = 0;\n"
            "\t\t\t\t\tif(texcoord.y >= 20)\n"
            "\t\t\t\t\t{\n"
            "\t\t\t\t\t\ttexcoord.y = texcoord.y - 20;\n"
            "\t\t\t\t\t\teffectFlag = 2;\n"
            "\t\t\t\t\t}\n"
            "\t\t\t\t\telse if(texcoord.y >= 10)\n"
            "\t\t\t\t\t{\n"
            "\t\t\t\t\t\ttexcoord.y = texcoord.y - 10;\n"
            "\t\t\t\t\t\teffectFlag = 1;\n"
            "\t\t\t\t\t}\n"
            "\t\t\t\t\t\t\n"
            "\t\t\t\t\t#ifdef GRAYED\n"
            "\t\t\t\t\tif(effectFlag == 1)\n"
            "\t\t\t\t\t\to.flags.y = 1;\n"
            "\t\t\t\t\telse\n"
            "\t\t\t\t\t\to.flags.y = 0;\n"
            "\t\t\t\t\t#else\n"
            "\t\t\t\t\t\to.flags.y = 0;\n"
            "\t\t\t\t\t#endif\n"
            "\t\t\t\t\to.flags.z = effectFlag;\n"
            "\t\t\t\t\t\n"
            "\t\t\t\t\to.texcoord = texcoord;")
assert text.count(old_vert) == 1, "bm vert"
text = text.replace(old_vert, new_vert)

old_frag = ("\t\t\t\t\tfixed4 col = i.color;\n"
            "\t\t\t\t\tfixed4 tcol = tex2D(_MainTex, i.texcoord);\n"
            "\t\t\t\t\tcol.a *= tcol[i.flags.x];")
new_frag = ("\t\t\t\t\tfixed4 col = i.color;\n"
            "\t\t\t\t\tfixed4 tcol = tex2D(_MainTex, i.texcoord);\n"
            "\t\t\t\t\tfixed ta = tcol[i.flags.x];\n"
            "\t\t\t\t\tcol.a *= ta;\n"
            "\t\t\t\t\t// stroke/shadow: solidify glyph alpha into crisp silhouette (no blur)\n"
            "\t\t\t\t\tif(i.flags.z > 0.5)\n"
            "\t\t\t\t\t\tcol.a = i.color.a * step(0.4, ta);")
assert text.count(old_frag) == 1, "bm frag"
text = text.replace(old_frag, new_frag)

if crlf:
    text = text.replace("\n", "\r\n")
with io.open(p, "w", encoding="utf-8", newline="") as f:
    f.write(text)
print("BMFont shader patched")
