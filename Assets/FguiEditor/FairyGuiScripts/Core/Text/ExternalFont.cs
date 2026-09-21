using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace FairyGUI
{
	/// <summary>
	/// Loads a ttf/otf file directly from disk at runtime through Unity's TextCore FontEngine —
	/// the same mechanism the FairyGUI editor (a built Unity player) uses for project fonts.
	/// A built player cannot create a UnityEngine.Font from a file: legacy dynamic fonts only
	/// resolve imported assets or OS-installed (registry) fonts. FontEngine goes through its own
	/// font stack, so any path works. Glyphs are rasterized into an atlas we own and exposed
	/// through the BaseFont contract (mirroring DynamicFont), so TextField uses it unchanged.
	/// </summary>
	public class ExternalFont : BaseFont
	{
		//TextCore raster atlas settings. The atlas is Alpha8 because the rasterizer writes coverage
		//into the alpha channel for that format, which is exactly what the FairyGUI text shader
		//samples (FairyGUI-Text.shader: col.a *= tex2D(_MainTex, i.texcoord).a).
		const int ATLAS_START = 1024;
		const int ATLAS_MAX = 4096;
		const int PADDING = 0;
		const int PACKING_MODIFIER = 1;   //see CreateAtlas
		const GlyphRenderMode RENDER_MODE = GlyphRenderMode.SMOOTH_HINTED;

		static bool sEngineInited;
		static readonly HashSet<string> sLoggedPaths = new HashSet<string>();

		/// <summary>
		/// FontEngine's atlas members (TryAddGlyphToTexture / ResetAtlasTexture / GetGlyphIndex) are
		/// internal: Unity exposes them to TextMeshPro through InternalsVisibleTo, not as public API.
		/// They are therefore reached through delegates created once by reflection.
		/// </summary>
		static class Internal
		{
			public delegate bool TryAddGlyphToTextureFn(uint glyphIndex, int padding, GlyphPackingMode packingMode,
				List<GlyphRect> freeGlyphRects, List<GlyphRect> usedGlyphRects, GlyphRenderMode renderMode,
				Texture2D texture, out Glyph glyph);

			public delegate void ResetAtlasTextureFn(Texture2D texture);

			public static readonly TryAddGlyphToTextureFn TryAddGlyphToTexture;
			public static readonly ResetAtlasTextureFn ResetAtlasTexture;
			public static readonly string Error;

			static Internal()
			{
				try
				{
					Type t = typeof(FontEngine);
					const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

					//The parameter list identifies the overload — TryAddGlyphToTexture has several.
					MethodInfo add = t.GetMethod("TryAddGlyphToTexture", Flags, null, new[]
					{
						typeof(uint), typeof(int), typeof(GlyphPackingMode), typeof(List<GlyphRect>),
						typeof(List<GlyphRect>), typeof(GlyphRenderMode), typeof(Texture2D), typeof(Glyph).MakeByRefType()
					}, null);
					MethodInfo reset = t.GetMethod("ResetAtlasTexture", Flags, null, new[] { typeof(Texture2D) }, null);

					if (add == null || reset == null)
					{
						Error = "glyph atlas methods were not found on " + t.FullName;
						return;
					}

					TryAddGlyphToTexture = (TryAddGlyphToTextureFn)Delegate.CreateDelegate(typeof(TryAddGlyphToTextureFn), add);
					ResetAtlasTexture = (ResetAtlasTextureFn)Delegate.CreateDelegate(typeof(ResetAtlasTextureFn), reset);
				}
				catch (Exception e)
				{
					Error = e.Message;
				}
			}

			public static bool Available
			{
				get { return TryAddGlyphToTexture != null && ResetAtlasTexture != null; }
			}
		}

		class GlyphData
		{
			public uint glyphIndex;
			public int x, y, w, h;   //packed rect in atlas pixel space (y from top)
			public float advance;
			public float bearingX;   //left of bitmap relative to pen, y-up from baseline
			public float bearingY;   //top of bitmap relative to baseline
			public bool empty;       //no bitmap (space etc.) or never packed
		}

		class SizeEntry
		{
			public int size;   //the raster size this entry caches, for face (re)loading
			public Dictionary<uint, GlyphData> glyphs = new Dictionary<uint, GlyphData>();
			public HashSet<uint> missing = new HashSet<uint>();
			public int yIndent;   //baseline distance from the line top (= point size), see Measure
			public int height;    //line height (= 1.25 * point size), see Measure
			public bool measured;
		}

		readonly string _filePath;
		readonly int _faceIndex;
		readonly Dictionary<int, SizeEntry> _sizes = new Dictionary<int, SizeEntry>();

		int _size;          //current raster size, set in SetFormat

		//FontEngine holds a single current face for the whole process, not one per font object, so
		//the loaded face has to be tracked globally. Otherwise two fonts (or two sizes) rendered in
		//the same frame would each believe their own face was still loaded and rasterize glyphs out
		//of whichever face was loaded last.
		static string sLoadedPath;
		static int sLoadedSize = -1;
		static int sLoadedFace = -1;

		Texture2D _atlasTex;    //Alpha8, packed by FontEngine, sampled by mainTexture
		readonly List<GlyphRect> _freeRects = new List<GlyphRect>();
		readonly List<GlyphRect> _usedRects = new List<GlyphRect>();
		int _atlasSize;
		bool _dirty;            //atlas has new pixels not yet uploaded

		public ExternalFont(string name, string filePath, int faceIndex)
		{
			this.name = name;
			_filePath = filePath;
			_faceIndex = faceIndex;

			this.canTint = true;
			this.canOutline = true;
			this.hasChannel = false;
			this.keepCrisp = true;

			if (UIConfig.renderingTextBrighterOnDesktop && !Application.isMobilePlatform)
			{
				this.shader = ShaderConfig.textBrighterShader;
				this.canLight = true;
			}
			else
				this.shader = ShaderConfig.textShader;

			//Bold/italic are selected by the project through separate font files (Font_Normal vs
			//Font_Bold style items); the TextCore stack has no synthetic style, so the TextFormat
			//bold/italic flags do not change rasterization here (mirrors bitmap-font behavior).
			this.customBold = false;

			CreateAtlas(ATLAS_START);
		}

		/// <summary>
		/// Returns null (with a neutral log) when the file cannot be opened by the font engine.
		/// </summary>
		public static ExternalFont TryCreate(string name, string filePath, int faceIndex = 0)
		{
			if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
				return null;

			if (!Internal.Available)
			{
				Debug.LogWarning("ExternalFont: the glyph atlas is not available in this player, rendering will use the default font - "
					+ (Internal.Error ?? "unknown reason"));
				return null;
			}

			try
			{
				if (!sEngineInited)
				{
					FontEngine.InitializeFontEngine();
					sEngineInited = true;
				}

				if (FontEngine.LoadFontFace(filePath, 16, faceIndex) != FontEngineError.Success)
				{
					Debug.LogWarning("ExternalFont: the font engine could not open this font file, rendering will use the default font - " + filePath);
					InvalidateFace();
					return null;
				}

				//The probe load above replaced whatever face was current; drop the tracked state so
				//the font that thought it was loaded reloads instead of rasterizing from ours.
				InvalidateFace();
			}
			catch (Exception e)
			{
				Debug.LogWarning("ExternalFont: font engine init failed, rendering will use the default font - " + filePath + " (" + e.Message + ")");
				return null;
			}

			//One line per font file per process: the render server is long-lived and gets many
			//requests, and knowing which fonts came from disk (rather than a fallback) is the
			//only way to tell the two apart from the outside.
			if (sLoggedPaths.Add(filePath))
				Debug.Log("ExternalFont: loaded " + name + " from " + filePath + " (face " + faceIndex + ")");

			return new ExternalFont(name, filePath, faceIndex);
		}

		/// <summary>Destroy works at runtime but not in edit mode, where this code also runs.</summary>
		static void DestroyTexture(Texture2D tex)
		{
			if (tex == null)
				return;
			if (Application.isPlaying)
				UnityEngine.Object.Destroy(tex);
			else
				UnityEngine.Object.DestroyImmediate(tex);
		}

		void CreateAtlas(int size)
		{
			DestroyTexture(_atlasTex);
			_atlasSize = size;
			_atlasTex = new Texture2D(size, size, TextureFormat.Alpha8, false);
			_atlasTex.hideFlags = DisplayOptions.hideFlags;

			Internal.ResetAtlasTexture(_atlasTex);

			_freeRects.Clear();
			_usedRects.Clear();
			//The usable region stops one pixel short of the far edge, leaving the top-right pixel
			//permanently clear. Zero-bitmap glyphs (space etc.) are pointed at it so their quad
			//samples nothing. Same margin TMP applies to non-bitmap raster modes.
			_freeRects.Add(new GlyphRect(0, 0, size - PACKING_MODIFIER, size - PACKING_MODIFIER));

			if (mainTexture != null)
				mainTexture.Dispose(true);
			mainTexture = new NTexture(_atlasTex);
			_dirty = false;
		}

		public override void SetFormat(TextFormat format, float fontSizeScale)
		{
			if (keepCrisp)
				_size = Mathf.FloorToInt((float)format.size * fontSizeScale * UIContentScaler.scaleFactor);
			else
			{
				if (fontSizeScale == 1)
					_size = format.size;
				else
					_size = Mathf.FloorToInt((float)format.size * fontSizeScale);
			}
		}

		public override void PrepareCharacters(string text)
		{
			if (string.IsNullOrEmpty(text))
				return;

			EnsureFace(_size);
			SizeEntry se = GetSizeEntry(_size);

			int cnt = text.Length;
			for (int i = 0; i < cnt; i++)
			{
				char ch = text[i];
				if (ch == '\r' || ch == '\n')
					continue;
				EnsureGlyph(se, ch);
			}

			FlushAtlas();
		}

		public override bool GetGlyphSize(char ch, out float width, out float height)
		{
			GlyphData gd = GetGlyphData(ch);
			if (gd == null)
			{
				width = 0;
				height = 0;
				return false;
			}

			SizeEntry se = Measure(_size);
			width = gd.advance;
			height = se.height;
			if (keepCrisp)
			{
				width /= UIContentScaler.scaleFactor;
				height /= UIContentScaler.scaleFactor;
			}
			return true;
		}

		public override bool GetGlyph(char ch, GlyphInfo glyph)
		{
			GlyphData gd = GetGlyphData(ch);
			if (gd == null)
				return false;

			SizeEntry se = Measure(_size);

			//Mirror the legacy DynamicFont/CharacterInfo conventions (pixel rect relative to the
			//pen on the baseline, y up; uv corners bottom-left..bottom-right).
			glyph.vert.xMin = Mathf.RoundToInt(gd.bearingX);
			glyph.vert.yMin = Mathf.RoundToInt(gd.bearingY) - gd.h - se.yIndent;
			glyph.vert.xMax = gd.w == 0 ? glyph.vert.xMin + _size / 2 : Mathf.RoundToInt(gd.bearingX) + gd.w;
			glyph.vert.yMax = Mathf.RoundToInt(gd.bearingY) - se.yIndent;

			AssignUV(gd, glyph);

			glyph.width = gd.advance;
			glyph.height = se.height;

			if (keepCrisp)
			{
				glyph.vert.xMin /= UIContentScaler.scaleFactor;
				glyph.vert.yMin /= UIContentScaler.scaleFactor;
				glyph.vert.xMax /= UIContentScaler.scaleFactor;
				glyph.vert.yMax /= UIContentScaler.scaleFactor;
				glyph.width /= UIContentScaler.scaleFactor;
				glyph.height /= UIContentScaler.scaleFactor;
			}
			return true;
		}

		public void Dispose()
		{
			if (mainTexture != null)
			{
				//NTexture.Dispose destroys the texture it wraps, so the atlas is released here.
				mainTexture.Dispose(true);
				mainTexture = null;
			}
			_atlasTex = null;
		}

		GlyphData GetGlyphData(char ch)
		{
			EnsureFace(_size);
			SizeEntry se = GetSizeEntry(_size);
			GlyphData gd = EnsureGlyph(se, ch);
			FlushAtlas();
			return gd;
		}

		void EnsureFace(int pointSize)
		{
			if (pointSize < 1)
				pointSize = 1;
			if (sLoadedSize == pointSize && sLoadedFace == _faceIndex && sLoadedPath == _filePath)
				return;

			if (FontEngine.LoadFontFace(_filePath, pointSize, _faceIndex) != FontEngineError.Success)
			{
				Debug.LogWarning("ExternalFont: could not switch to point size " + pointSize + " for " + _filePath);
				InvalidateFace();
				return;
			}

			sLoadedPath = _filePath;
			sLoadedSize = pointSize;
			sLoadedFace = _faceIndex;
		}

		static void InvalidateFace()
		{
			sLoadedPath = null;
			sLoadedSize = -1;
			sLoadedFace = -1;
		}

		SizeEntry GetSizeEntry(int size)
		{
			SizeEntry se;
			if (!_sizes.TryGetValue(size, out se))
			{
				se = new SizeEntry();
				se.size = size;
				_sizes[size] = se;
			}
			return se;
		}

		GlyphData EnsureGlyph(SizeEntry se, uint ch)
		{
			GlyphData gd;
			if (se.glyphs.TryGetValue(ch, out gd))
				return gd.empty ? null : gd;

			if (se.missing.Contains(ch))
				return null;

			//The atlas is owned by the package and destroyed with it (UIPackage.Dispose). Anything
			//still holding this font afterwards - a UI tree being torn down, for instance - must not
			//reach the native rasterizer with a dead texture: that is a hard crash inside
			//FontEngine.TryAddGlyphToTexture_Internal, not a managed exception.
			if (_atlasTex == null)
			{
				se.missing.Add(ch);
				return null;
			}

			uint glyphIndex;
			if (!FontEngine.TryGetGlyphIndex(ch, out glyphIndex) || glyphIndex == 0)
			{
				//Same alternative mappings the font stack uses for these code points.
				switch (ch)
				{
					case 0xA0:
						glyphIndex = LookupIndex(0x20);
						break;
					case 0xAD:
					case 0x2011:
						glyphIndex = LookupIndex(0x2D);
						break;
					default:
						glyphIndex = 0;
						break;
				}
				if (glyphIndex == 0)
				{
					se.missing.Add(ch);
					return null;
				}
			}

			//Packing rasterizes from the currently loaded face, which any other font may have
			//changed since a caller last called EnsureFace — so bind this entry's size here.
			EnsureFace(se.size);

			Glyph glyph;
			bool added = Internal.TryAddGlyphToTexture(glyphIndex, PADDING, GlyphPackingMode.BestShortSideFit,
				_freeRects, _usedRects, RENDER_MODE, _atlasTex, out glyph);
			if (!added && _atlasSize < ATLAS_MAX)
			{
				GrowAtlas();
				EnsureFace(se.size); //GrowAtlas repacks every size, leaving an arbitrary face loaded
				added = Internal.TryAddGlyphToTexture(glyphIndex, PADDING, GlyphPackingMode.BestShortSideFit,
					_freeRects, _usedRects, RENDER_MODE, _atlasTex, out glyph);
			}
			if (!added)
			{
				se.missing.Add(ch);
				Debug.LogWarning("ExternalFont: glyph atlas is full and could not grow, some characters will use the fallback drawing - " + _filePath);
				return null;
			}

			gd = new GlyphData();
			gd.glyphIndex = glyphIndex;
			GlyphRect r = glyph.glyphRect;
			gd.x = r.x;
			gd.y = r.y;
			gd.w = r.width;
			gd.h = r.height;
			gd.advance = glyph.metrics.horizontalAdvance;
			gd.bearingX = glyph.metrics.horizontalBearingX;
			gd.bearingY = glyph.metrics.horizontalBearingY;
			se.glyphs[ch] = gd;

			_dirty = true;
			//A zero-bitmap glyph (space) keeps gd.empty=false with w=h=0: it is advance-only, and
			//GetGlyph takes the same zero-width path the legacy CharacterInfo branch uses.
			return gd;
		}

		static uint LookupIndex(uint unicode)
		{
			uint index;
			return FontEngine.TryGetGlyphIndex(unicode, out index) ? index : 0;
		}

		void GrowAtlas()
		{
			int oldSize = _atlasSize;
			int newSize = Mathf.Min(oldSize * 2, ATLAS_MAX);

			//Repack every cached glyph into the new atlas: glyph rects are the only state that
			//becomes invalid on growth; metrics/advances carry over.
			var pending = new List<KeyValuePair<int, List<GlyphData>>>();
			foreach (KeyValuePair<int, SizeEntry> kv in _sizes)
			{
				List<GlyphData> list = new List<GlyphData>();
				foreach (KeyValuePair<uint, GlyphData> ge in kv.Value.glyphs)
				{
					if (ge.Value.w > 0 && ge.Value.h > 0)
						list.Add(ge.Value);
				}
				if (list.Count > 0)
					pending.Add(new KeyValuePair<int, List<GlyphData>>(kv.Key, list));
			}

			CreateAtlas(newSize);
			foreach (KeyValuePair<int, List<GlyphData>> kv in pending)
			{
				EnsureFace(kv.Key);
				foreach (GlyphData gd in kv.Value)
				{
					Glyph glyph;
					if (Internal.TryAddGlyphToTexture(gd.glyphIndex, PADDING, GlyphPackingMode.BestShortSideFit,
						_freeRects, _usedRects, RENDER_MODE, _atlasTex, out glyph))
					{
						GlyphRect r = glyph.glyphRect;
						gd.x = r.x;
						gd.y = r.y;
						gd.w = r.width;
						gd.h = r.height;
						_dirty = true;
					}
					else
					{
						gd.empty = true;
						gd.w = 0;
						gd.h = 0;
					}
				}
			}

			//The repack loop left an arbitrary face loaded; force the next request to reload.
			InvalidateFace();
		}

		/// <summary>
		/// FontEngine renders into the texture on the CPU side; upload it once per glyph batch
		/// rather than once per glyph.
		/// </summary>
		void FlushAtlas()
		{
			if (!_dirty)
				return;
			_dirty = false;
			if (_atlasTex != null)
				_atlasTex.Apply(false, false);
		}

		void AssignUV(GlyphData gd, GlyphInfo glyph)
		{
			if (gd.w == 0 || gd.h == 0)
			{
				//Advance-only glyph: aim the quad at the reserved clear pixel (see CreateAtlas).
				Vector2 reserved = new Vector2((_atlasSize - 0.5f) / _atlasSize, (_atlasSize - 0.5f) / _atlasSize);
				glyph.uv[0] = reserved;
				glyph.uv[1] = reserved;
				glyph.uv[2] = reserved;
				glyph.uv[3] = reserved;
				return;
			}

			//No y flip: glyphRect is already expressed in the texture's uv space, where row 0 is
			//uv.y 0 (this is exactly how TMP derives its uvs from the same struct).
			float w = _atlasSize;
			float xMin = gd.x / w;
			float xMax = (gd.x + gd.w) / w;
			float yMin = gd.y / w;
			float yMax = (gd.y + gd.h) / w;

			glyph.uv[0].Set(xMin, yMin);
			glyph.uv[1].Set(xMin, yMax);
			glyph.uv[2].Set(xMax, yMax);
			glyph.uv[3].Set(xMax, yMin);
		}

		/// <summary>
		/// Same line box convention as DynamicFont.GetRenderInfo: baseline = em size, line height = 1.25 em.
		/// Kept in sync with FairyGUI-unity 5.2.0's DynamicFont (_ascent = font size, _lineHeight = 1.25 * font size),
		/// so text sits on the same baseline the editor/game builds produce.
		/// </summary>
		SizeEntry Measure(int size)
		{
			SizeEntry se = GetSizeEntry(size);
			if (se.measured)
				return se;
			se.measured = true;

			se.yIndent = size;
			se.height = Mathf.RoundToInt(size * DynamicFont.LINE_HEIGHT_RATIO);
			return se;
		}
	}
}
