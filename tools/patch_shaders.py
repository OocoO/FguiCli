# -*- coding: utf-8 -*-
import io, sys

def rw(path):
    with io.open(path, "r", encoding="utf-8-sig", newline="") as f:
        raw = f.read()
    crlf = "\r\n" in raw
    return raw.replace("\r\n", "\n"), crlf

def w(path, text, crlf):
    if crlf:
        text = text.replace("\n", "\r\n")
    with io.open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)

# ---------- FairyGUI-Text.shader ----------
p = r"D:\FguiCli\Assets\FguiEditor\FairyGuiScripts\Resources\Shaders\FairyGUI-Text.shader"
text, crlf = rw(p)

old_v2f = """\t\t\t\t\t#ifdef GRAYED
\t\t\t\t\tfixed flag : TEXCOORD2;
\t\t\t\t\t#endif
\t\t\t\t};"""
new_v2f = """\t\t\t\t\t#ifdef GRAYED
\t\t\t\t\tfixed flag : TEXCOORD2;
\t\t\t\t\t#endif
\t\t\t\t\t// 0=normal, 1=stroke, 2=shadow
\t\t\t\t\tfixed effect : TEXCOORD3;
\t\t\t\t};"""
assert text.count(old_v2f) == 1, "text v2f"
text = text.replace(old_v2f, new_v2f)

old_vert = """\t\t\t\t\t#ifdef GRAYED
\t\t\t\t\tfloat2 texcoord = v.texcoord;
\t\t\t\t\tif(texcoord.y >1)
\t\t\t\t\t{
\t\t\t\t\t\ttexcoord.y = texcoord.y - 10;
\t\t\t\t\t\to.flag = 1;
\t\t\t\t\t}
\t\t\t\t\telse
\t\t\t\t\t\to.flag = 0;
\t\t\t\t\to.texcoord = texcoord;
\t\t\t\t\t#else
\t\t\t\t\to.texcoord = v.texcoord;
\t\t\t\t\t#endif"""
new_vert = """\t\t\t\t\t// decode effect marker: uv.y+10 = stroke, uv.y+20 = shadow
\t\t\t\t\tfloat2 texcoord = v.texcoord;
\t\t\t\t\tfloat effectFlag = 0;
\t\t\t\t\tif(texcoord.y >= 20)
\t\t\t\t\t{
\t\t\t\t\t\ttexcoord.y = texcoord.y - 20;
\t\t\t\t\t\teffectFlag = 2;
\t\t\t\t\t}
\t\t\t\t\telse if(texcoord.y >= 10)
\t\t\t\t\t{
\t\t\t\t\t\ttexcoord.y = texcoord.y - 10;
\t\t\t\t\t\teffectFlag = 1;
\t\t\t\t\t}
\t\t\t\t\t#ifdef GRAYED
\t\t\t\t\tif(effectFlag == 1)
\t\t\t\t\t\to.flag = 1;
\t\t\t\t\telse
\t\t\t\t\t\to.flag = 0;
\t\t\t\t\t#endif
\t\t\t\t\to.effect = effectFlag;
\t\t\t\t\to.texcoord = texcoord;"""
assert text.count(old_vert) == 1, "text vert"
text = text.replace(old_vert, new_vert)

old_frag = """\t\t\t\t\tfixed4 col = i.color;
\t\t\t\t\tcol.a *= tex2D(_MainTex, i.texcoord).a;"""
new_frag = """\t\t\t\t\tfixed4 col = i.color;
\t\t\t\t\tfixed ta = tex2D(_MainTex, i.texcoord).a;
\t\t\t\t\tcol.a *= ta;
\t\t\t\t\t// stroke/shadow: solidify glyph alpha into crisp silhouette (no blur)
\t\t\t\t\tif(i.effect > 0.5)
\t\t\t\t\t\tcol.a = i.color.a * step(0.4, ta);"""
assert text.count(old_frag) == 1, "text frag"
text = text.replace(old_frag, new_frag)
w(p, text, crlf)
print("Text shader patched")

# ---------- FairyGUI-BMFont.shader ----------
p = r"D:\FguiCli\Assets\FguiEditor\FairyGuiScripts\Resources\Shaders\FairyGUI-BMFont.shader"
text, crlf = rw(p)

old_v2f = """\t\t\t\t\tfixed4 color : COLOR;
\t\t\t\t\tfloat2 texcoord : TEXCOORD0;
\t\t\t\t\tfixed2 flags : TEXCOORD1;"""
new_v2f = """\t\t\t\t\tfixed4 color : COLOR;
\t\t\t\t\tfloat2 texcoord : TEXCOORD0;
\t\t\t\t\t// flags.x=channel, flags.y=grayed, flags.z=effect(0 normal,1 stroke,2 shadow)
\t\t\t\t\tfixed3 flags : TEXCOORD1;"""
assert text.count(old_v2f) == 1, "bm v2f"
text = text.replace(old_v2f, new_v2f)

old_vert = """\t\t\t\t\tfloat2 texcoord = v.texcoord;
\t\t\t\t\to.flags.x = floor(texcoord.x/10);
\t\t\t\t\ttexcoord.x = texcoord.x - o.flags.x*10;
\t\t\t\t\t
\t\t\t\t\t#ifdef GRAYED
\t\t\t\t\tif(texcoord.y >1)
\t\t\t\t\t{
\t\t\t\t\t\ttexcoord.y = texcoord.y - 10;
\t\t\t\t\t\to.flags.y = 1;
\t\t\t\t\t}
\t\t\t\t\telse
\t\t\t\t\t\to.flags.y = 0;
\t\t\t\t\t#else
\t\t\t\t\t\to.flags.y = 0;
\t\t\t\t\t#endif
\t\t\t\t\t
\t\t\t\t\to.texcoord = texcoord;"""
new_vert = """\t\t\t\t\tfloat2 texcoord = v.texcoord;
\t\t\t\t\to.flags.x = floor(texcoord.x/10);
\t\t\t\t\ttexcoord.x = texcoord.x - o.flags.x*10;
\t\t\t\t\t
\t\t\t\t\t// decode effect marker: uv.y+10 = stroke, uv.y+20 = shadow
\t\t\t\t\tfloat effectFlag = 0;
\t\t\t\t\tif(texcoord.y >= 20)
\t\t\t\t\t{
\t\t\t\t\t\ttexcoord.y = texcoord.y - 20;
\t\t\t\t\t\teffectFlag = 2;
\t\t\t\t\t}
\t\t\t\t\telse if(texcoord.y >= 10)
\t\t\t\t\t{
\t\t\t\t\t\ttexcoord.y = texcoord.y - 10;
\t\t\t\t\t\teffectFlag = 1;
\t\t\t\t\t}
\t\t\t\t\t
\t\t\t\t\t#ifdef GRAYED
\t\t\t\t\tif(effectFlag == 1)
\t\t\t\t\t\to.flags.y = 1;
\t\t\t\t\telse
\t\t\t\t\t\to.flags.y = 0;
\t\t\t\t\t#else
\t\t\t\t\t\to.flags.y = 0;
\t\t\t\t\t#endif
\t\t\t\t\to.flags.z = effectFlag;
\t\t\t\t\t
\t\t\t\t\to.texcoord = texcoord;"""
assert text.count(old_vert) == 1, "bm vert"
text = text.replace(old_vert, new_vert)

old_frag = """\t\t\t\t\tfixed4 col = i.color;
\t\t\t\t\tfixed4 tcol = tex2D(_MainTex, i.texcoord);
\t\t\t\t\tcol.a *= tcol[i.flags.x];"""
new_frag = """\t\t\t\t\tfixed4 col = i.color;
\t\t\t\t\tfixed4 tcol = tex2D(_MainTex, i.texcoord);
\t\t\t\t\tfixed ta = tcol[i.flags.x];
\t\t\t\t\tcol.a *= ta;
\t\t\t\t\t// stroke/shadow: solidify glyph alpha into crisp silhouette (no blur)
\t\t\t\t\tif(i.flags.z > 0.5)
\t\t\t\t\t\tcol.a = i.color.a * step(0.4, ta);"""
assert text.count(old_frag) == 1, "bm frag"
text = text.replace(old_frag, new_frag)
w(p, text, crlf)
print("BMFont shader patched")
