using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.UIElements
{
	/// <summary>Placement and metrics of one glyph in the font's SDF atlas.</summary>
	public struct GlyphInfo
	{
		/// <summary>Atlas page; -1 when the glyph has no visible outline (space).</summary>
		public int page;

		public float u0, v0, u1, v1;

		/// <summary>Bitmap size in atlas pixels (at <see cref="FontAsset.SdfFontSize" />).</summary>
		public int width, height;

		/// <summary>Offset of the bitmap's top-left corner from the pen position, in atlas pixels (y down).</summary>
		public float bearingX, bearingY;

		/// <summary>Horizontal advance in font units.</summary>
		public int advance;

		public int glyphIndex;
		public bool hasBitmap => page >= 0;
	}

	/// <summary>
	///     A font usable by the UI: a TrueType file plus a dynamically filled signed distance field atlas shared by
	///     every size the font is drawn at. Glyphs are rasterized on first use. Loaded from a .ttf / .otf asset, or
	///     from the system fonts for the default font. Glyph lookups may run on any thread (the loading thread
	///     rasterizes the glyphs of UI it loads in the background, <see cref="WarmUp" />); the atlas textures upload on
	///     the GL thread (<see cref="GetPageTexture" />).
	/// </summary>
	public sealed class FontAsset : IAsset
	{
		#region PublicFields

		/// <summary>Pixel size glyphs are rasterized at into the atlas.</summary>
		public const float SdfFontSize = 48f;

		/// <summary>Distance range stored around glyph edges, in atlas pixels (also the bitmap padding).</summary>
		public const float SdfSpread = 8f;

		public const int AtlasSize = 1024;

		public string AssetPath { get; internal set; }
		public string name;
		public string familyName => font?.FamilyName ?? name;
		public bool isBold { get; private set; }
		public bool isItalic { get; private set; }

		/// <summary>Font units per em.</summary>
		public int unitsPerEm => font.UnitsPerEm;

		/// <summary>Ascender, descender (negative) and line gap in font units.</summary>
		public int ascender => font.Ascender;

		public int descender => font.Descender;
		public int lineGap => font.LineGap;

		/// <summary>Line height for a font size, in pixels.</summary>
		public float LineHeight(float fontSize)
		{
			return (font.Ascender - font.Descender + font.LineGap) * fontSize / font.UnitsPerEm;
		}

		/// <summary>Fonts tried for characters this font lacks.</summary>
		public List<FontAsset> fallbacks = new List<FontAsset>();

		/// <summary>Variants used by -unity-font-style (null: synthesized from this face).</summary>
		public FontAsset boldVariant;

		public FontAsset italicVariant;
		public FontAsset boldItalicVariant;

		public IReadOnlyList<AtlasPage> pages => pageList;

		/// <summary>Bumped when a page's pixels changed (the renderer re-uploads the dirty region).</summary>
		public int atlasVersion { get; private set; }

		/// <summary>The engine's default font (Segoe UI / Arial / DejaVu Sans, whatever the system has).</summary>
		public static FontAsset Default
		{
			get
			{
				FontAsset loaded = defaultFont;
				if (loaded != null)
				{
					return loaded;
				}

				lock (systemFonts)
				{
					return defaultFont ??= LoadDefault();
				}
			}
			set => defaultFont = value;
		}

		/// <summary>The printable ASCII characters: the glyphs <see cref="WarmUp" /> rasterizes for every font.</summary>
		public const string BasicCharacters = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

		#endregion

		#region PrivateFields

		private static volatile FontAsset defaultFont;
		private static readonly Dictionary<string, FontAsset> systemFonts = new Dictionary<string, FontAsset>(StringComparer.OrdinalIgnoreCase);
		[ThreadStatic] private static List<GlyphContour> contourBuffer;
		private TrueTypeFont font;

		// Guards the glyph table, the atlas pages and the TrueType reader's caches. Held briefly: outlines and distance
		// fields are computed outside it, so the main thread never waits for the loading thread's rasterization.
		private readonly object sync = new object();
		private readonly Dictionary<int, GlyphInfo> glyphs = new Dictionary<int, GlyphInfo>();
		private readonly List<AtlasPage> pageList = new List<AtlasPage>();

		#endregion

		#region Constructors

		private FontAsset()
		{ }

		#endregion

		#region PublicMethods

		/// <summary>Loads a font file; null when the file cannot be read or has no TrueType outlines.</summary>
		public static FontAsset LoadFromFile(string path, string assetPath = null)
		{
			try
			{
				TrueTypeFont parsed = TrueTypeFont.Load(path);
				if (!parsed.HasGlyphOutlines)
				{
					Debug.LogWarning("[UI] Font " + path + " has no TrueType outlines (CFF fonts are not supported)");
					return null;
				}

				var asset = new FontAsset { font = parsed, AssetPath = assetPath, name = Path.GetFileNameWithoutExtension(path) };
				string style = parsed.StyleName ?? "";
				asset.isBold = style.IndexOf("bold", StringComparison.OrdinalIgnoreCase) >= 0;
				asset.isItalic = style.IndexOf("italic", StringComparison.OrdinalIgnoreCase) >= 0 || style.IndexOf("oblique", StringComparison.OrdinalIgnoreCase) >= 0;
				return asset;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[UI] Cannot load font " + path + ": " + e.Message);
				return null;
			}
		}

		/// <summary>A font from the operating system's font folder by family name ("Segoe UI", "Arial", ...), or null.</summary>
		public static FontAsset FindSystemFont(string familyName)
		{
			if (string.IsNullOrWhiteSpace(familyName))
			{
				return null;
			}

			familyName = familyName.Trim().Trim('"', '\'');
			lock (systemFonts)
			{
				FontAsset cached;
				if (systemFonts.TryGetValue(familyName, out cached))
				{
					return cached;
				}

				string file = SystemFontFile(familyName);
				FontAsset asset = file != null ? LoadFromFile(file) : null;
				if (asset != null)
				{
					asset.LinkSystemVariants(file);
					if (defaultFont != null && asset != defaultFont && !asset.fallbacks.Contains(defaultFont))
					{
						asset.fallbacks.Add(defaultFont);
					}
				}

				systemFonts[familyName] = asset;
				return asset;
			}
		}

		/// <summary>The face to use for a font style: the variant when the font has one, else this face (synthesized).</summary>
		public FontAsset GetVariant(FontStyle style, out bool synthesizeBold, out bool synthesizeItalic)
		{
			synthesizeBold = false;
			synthesizeItalic = false;
			bool wantBold = style == FontStyle.Bold || style == FontStyle.BoldAndItalic;
			bool wantItalic = style == FontStyle.Italic || style == FontStyle.BoldAndItalic;
			if (wantBold && wantItalic && boldItalicVariant != null) return boldItalicVariant;
			if (wantBold && !wantItalic && boldVariant != null) return boldVariant;
			if (wantItalic && !wantBold && italicVariant != null) return italicVariant;
			if (wantBold && wantItalic)
			{
				if (boldVariant != null)
				{
					synthesizeItalic = !boldVariant.isItalic;
					return boldVariant;
				}

				if (italicVariant != null)
				{
					synthesizeBold = !italicVariant.isBold;
					return italicVariant;
				}
			}

			synthesizeBold = wantBold && !isBold;
			synthesizeItalic = wantItalic && !isItalic;
			return this;
		}

		/// <summary>The glyph for a code point in this font or a fallback; <paramref name="owner" /> is the font that has it.</summary>
		public bool TryGetGlyph(int codepoint, out GlyphInfo glyph, out FontAsset owner)
		{
			int index = GlyphIndexOf(codepoint);
			if (index != 0)
			{
				glyph = GetGlyphByIndex(index);
				owner = this;
				return true;
			}

			for (int i = 0; i < fallbacks.Count; i++)
			{
				FontAsset fallback = fallbacks[i];
				if (fallback == null || fallback == this)
				{
					continue;
				}

				int fallbackIndex = fallback.GlyphIndexOf(codepoint);
				if (fallbackIndex != 0)
				{
					glyph = fallback.GetGlyphByIndex(fallbackIndex);
					owner = fallback;
					return true;
				}
			}

			glyph = GetGlyphByIndex(0);
			owner = this;
			return false;
		}

		public GlyphInfo GetGlyphByIndex(int glyphIndex)
		{
			GlyphInfo info;
			lock (sync)
			{
				if (glyphs.TryGetValue(glyphIndex, out info))
				{
					return info;
				}

				info = new GlyphInfo { page = -1, glyphIndex = glyphIndex, advance = font.GetAdvance(glyphIndex) };
			}

			// The outline and its distance field are computed outside the lock; two threads may both rasterize a
			// glyph, the first one placed in the atlas is kept.
			GlyphBitmap bitmap = Rasterize(glyphIndex);
			lock (sync)
			{
				GlyphInfo raced;
				if (glyphs.TryGetValue(glyphIndex, out raced))
				{
					return raced;
				}

				if (bitmap.pixels != null)
				{
					Place(ref info, bitmap);
				}

				glyphs[glyphIndex] = info;
				return info;
			}
		}

		/// <summary>The kerning tables are read when the font loads and never change: no lock.</summary>
		public int GetKerning(int leftGlyphIndex, int rightGlyphIndex)
		{
			return font.GetKerning(leftGlyphIndex, rightGlyphIndex);
		}

		/// <summary>
		///     Rasterizes the glyphs of <paramref name="text" /> (and of the fallbacks that have the characters this font
		///     lacks) into the atlas: the loading thread does it for UI it loads, so the first frame showing the UI only
		///     uploads the atlas. Any thread.
		/// </summary>
		public void WarmUp(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			for (int i = 0; i < text.Length; i++)
			{
				int codepoint = text[i];
				if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				{
					codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
					i++;
				}
				else if (char.IsSurrogate(text[i]) || codepoint < 32)
				{
					continue;
				}

				TryGetGlyph(codepoint, out _, out _);
			}
		}

		/// <summary>GL texture of an atlas page, uploading the glyphs added since the last call (GL thread); 0 when the page does not exist.</summary>
		public int GetPageTexture(int page)
		{
			lock (sync)
			{
				return page >= 0 && page < pageList.Count ? pageList[page].GetTexture() : 0;
			}
		}

		/// <summary>Uploads every atlas page that has glyphs the GPU lacks (GL thread).</summary>
		public void UploadPages()
		{
			lock (sync)
			{
				foreach (AtlasPage page in pageList)
				{
					page.GetTexture();
				}
			}
		}

		/// <summary>Releases the GL textures (the glyphs are re-uploaded on next use).</summary>
		public void Release()
		{
			lock (sync)
			{
				foreach (AtlasPage page in pageList)
				{
					page.Release();
				}
			}
		}

		public override string ToString()
		{
			return "FontAsset(" + (AssetPath ?? name) + ")";
		}

		#endregion

		#region PrivateMethods

		/// <summary>A glyph's distance field before it has a place in the atlas (no pixels: nothing to draw).</summary>
		private struct GlyphBitmap
		{
			public byte[] pixels;
			public int left, top, width, height;
		}

		/// <summary>The glyph's outline and distance field; reads only the font file's bytes (any thread, no lock).</summary>
		private GlyphBitmap Rasterize(int glyphIndex)
		{
			List<GlyphContour> contours = contourBuffer ??= new List<GlyphContour>();
			TrueTypeFont.GlyphBox box;
			if (!font.GetGlyphOutline(glyphIndex, contours, out box))
			{
				return default;
			}

			float scale = SdfFontSize / font.UnitsPerEm;
			int pad = (int)Math.Ceiling(SdfSpread) + 1;
			float minX = box.xMin * scale, maxX = box.xMax * scale;
			float minY = box.yMin * scale, maxY = box.yMax * scale;
			int left = (int)Math.Floor(minX) - pad;
			int top = (int)Math.Ceiling(maxY) + pad;
			int width = (int)Math.Ceiling(maxX) + pad - left;
			int height = top - ((int)Math.Floor(minY) - pad);
			if (width <= 0 || height <= 0 || width > AtlasSize || height > AtlasSize)
			{
				return default;
			}

			return new GlyphBitmap
			{
				pixels = GlyphSdf.Render(contours, scale, left, top, width, height, SdfSpread),
				left = left,
				top = top,
				width = width,
				height = height
			};
		}

		/// <summary>Copies a rasterized glyph into the atlas (under <see cref="sync" />).</summary>
		private void Place(ref GlyphInfo info, GlyphBitmap bitmap)
		{
			int x, y;
			AtlasPage page = Allocate(bitmap.width, bitmap.height, out x, out y);
			page.Write(bitmap.pixels, x, y, bitmap.width, bitmap.height);
			atlasVersion++;

			info.page = pageList.IndexOf(page);
			info.width = bitmap.width;
			info.height = bitmap.height;
			info.u0 = x / (float)AtlasSize;
			info.v0 = y / (float)AtlasSize;
			info.u1 = (x + bitmap.width) / (float)AtlasSize;
			info.v1 = (y + bitmap.height) / (float)AtlasSize;
			info.bearingX = bitmap.left;
			// Pen y is the baseline; the bitmap top is `top` above it (y up in glyph space).
			info.bearingY = -bitmap.top;
		}

		private int GlyphIndexOf(int codepoint)
		{
			lock (sync)
			{
				return font.GetGlyphIndex(codepoint);
			}
		}

		private AtlasPage Allocate(int width, int height, out int x, out int y)
		{
			for (int i = 0; i < pageList.Count; i++)
			{
				if (pageList[i].TryAllocate(width, height, out x, out y))
				{
					return pageList[i];
				}
			}

			var page = new AtlasPage(AtlasSize);
			pageList.Add(page);
			page.TryAllocate(width, height, out x, out y);
			return page;
		}

		private static FontAsset LoadDefault()
		{
			string[] candidates =
			{
				SystemFontFile("Segoe UI"),
				SystemFontFile("Arial"),
				SystemFontFile("Tahoma"),
				"/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
				"/usr/share/fonts/TTF/DejaVuSans.ttf",
				"/System/Library/Fonts/Supplemental/Arial.ttf",
				"/Library/Fonts/Arial.ttf"
			};

			foreach (string candidate in candidates)
			{
				if (candidate != null && File.Exists(candidate))
				{
					FontAsset asset = LoadFromFile(candidate);
					if (asset != null)
					{
						asset.LinkSystemVariants(candidate);
						// Symbols and other scripts the UI font lacks.
						foreach (string extra in new[] { "seguisym.ttf", "seguiemj.ttf", "arial.ttf" })
						{
							string path = Path.Combine(SystemFontsFolder, extra);
							if (File.Exists(path) && !string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase))
							{
								FontAsset fallback = LoadFromFile(path);
								if (fallback != null)
								{
									asset.fallbacks.Add(fallback);
								}
							}
						}

						return asset;
					}
				}
			}

			Debug.LogError("[UI] No default font found; text will not render. Add a .ttf asset and set it with -unity-font.");
			return null;
		}

		private static string SystemFontsFolder
		{
			get
			{
				if (OperatingSystem.IsWindows())
				{
					string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
					return Path.Combine(string.IsNullOrEmpty(windows) ? "C:\\Windows" : windows, "Fonts");
				}

				return OperatingSystem.IsMacOS() ? "/System/Library/Fonts/Supplemental" : "/usr/share/fonts/truetype";
			}
		}

		/// <summary>Font file for a family name: a small table of common fonts, else "<name>.ttf" in the font folder.</summary>
		private static string SystemFontFile(string family)
		{
			string folder = SystemFontsFolder;
			string file;
			switch (family.ToLowerInvariant().Replace(" ", ""))
			{
				case "segoeui": file = "segoeui.ttf"; break;
				case "arial": file = "arial.ttf"; break;
				case "arialblack": file = "ariblk.ttf"; break;
				case "verdana": file = "verdana.ttf"; break;
				case "tahoma": file = "tahoma.ttf"; break;
				case "calibri": file = "calibri.ttf"; break;
				case "cambria": file = "cambria.ttc"; break;
				case "consolas": file = "consola.ttf"; break;
				case "couriernew": file = "cour.ttf"; break;
				case "timesnewroman": file = "times.ttf"; break;
				case "georgia": file = "georgia.ttf"; break;
				case "trebuchetms": file = "trebuc.ttf"; break;
				case "impact": file = "impact.ttf"; break;
				case "comicsansms": file = "comic.ttf"; break;
				case "segoeuisymbol": file = "seguisym.ttf"; break;
				case "segoeuiemoji": file = "seguiemj.ttf"; break;
				case "dejavusans": file = "dejavu/DejaVuSans.ttf"; break;
				default: file = family + ".ttf"; break;
			}

			string path = Path.Combine(folder, file);
			if (File.Exists(path))
			{
				return path;
			}

			path = Path.Combine(folder, family.Replace(" ", "") + ".ttf");
			return File.Exists(path) ? path : null;
		}

		/// <summary>Bold / italic faces of system fonts follow a naming scheme; link the ones that exist.</summary>
		private void LinkSystemVariants(string file)
		{
			string folder = Path.GetDirectoryName(file) ?? "";
			string baseName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
			string[][] schemes =
			{
				new[] { baseName + "b.ttf", baseName + "i.ttf", baseName + "z.ttf" },
				new[] { baseName + "bd.ttf", baseName + "i.ttf", baseName + "bi.ttf" },
				new[] { baseName + "-bold.ttf", baseName + "-italic.ttf", baseName + "-bolditalic.ttf" }
			};

			foreach (string[] scheme in schemes)
			{
				string bold = Path.Combine(folder, scheme[0]);
				string italic = Path.Combine(folder, scheme[1]);
				string boldItalic = Path.Combine(folder, scheme[2]);
				if (File.Exists(bold) && boldVariant == null)
				{
					boldVariant = LoadFromFile(bold);
					if (boldVariant != null) boldVariant.isBold = true;
				}

				if (File.Exists(italic) && italicVariant == null)
				{
					italicVariant = LoadFromFile(italic);
					if (italicVariant != null) italicVariant.isItalic = true;
				}

				if (File.Exists(boldItalic) && boldItalicVariant == null)
				{
					boldItalicVariant = LoadFromFile(boldItalic);
					if (boldItalicVariant != null)
					{
						boldItalicVariant.isBold = true;
						boldItalicVariant.isItalic = true;
					}
				}
			}

			foreach (FontAsset variant in new[] { boldVariant, italicVariant, boldItalicVariant })
			{
				if (variant != null)
				{
					variant.fallbacks.Add(this);
				}
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>One atlas texture (R8) with a shelf packer and a dirty region for incremental uploads.</summary>
		public sealed class AtlasPage
		{
			public readonly int size;
			public readonly byte[] pixels;

			private int handle;
			private int shelfY;
			private int shelfHeight;
			private int cursorX;
			private int dirtyMinX = int.MaxValue, dirtyMinY = int.MaxValue, dirtyMaxX = -1, dirtyMaxY = -1;
			private bool uploaded;

			public AtlasPage(int size)
			{
				this.size = size;
				pixels = new byte[size * size];
			}

			public bool TryAllocate(int width, int height, out int x, out int y)
			{
				x = y = 0;
				if (cursorX + width > size)
				{
					// New shelf.
					shelfY += shelfHeight;
					shelfHeight = 0;
					cursorX = 0;
				}

				if (shelfY + height > size)
				{
					return false;
				}

				x = cursorX;
				y = shelfY;
				cursorX += width;
				shelfHeight = Math.Max(shelfHeight, height);
				return true;
			}

			public void Write(byte[] glyph, int x, int y, int width, int height)
			{
				for (int row = 0; row < height; row++)
				{
					System.Buffer.BlockCopy(glyph, row * width, pixels, (y + row) * size + x, width);
				}

				dirtyMinX = Math.Min(dirtyMinX, x);
				dirtyMinY = Math.Min(dirtyMinY, y);
				dirtyMaxX = Math.Max(dirtyMaxX, x + width);
				dirtyMaxY = Math.Max(dirtyMaxY, y + height);
			}

			/// <summary>
			///     GL texture of the page, uploading the pixels changed since the last call. The font's
			///     <see cref="FontAsset.GetPageTexture" /> calls it under the lock that guards the pixels.
			/// </summary>
			public int GetTexture()
			{
				if (handle == 0)
				{
					handle = GL.GenTexture();
					GL.BindTexture(TextureTarget.Texture2D, handle);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
					uploaded = false;
				}

				if (!uploaded)
				{
					GL.BindTexture(TextureTarget.Texture2D, handle);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
					GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, size, size, 0, PixelFormat.Red, PixelType.UnsignedByte, pixels);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
					uploaded = true;
					ClearDirty();
				}
				else if (dirtyMaxX > dirtyMinX)
				{
					GL.BindTexture(TextureTarget.Texture2D, handle);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, size);
					GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, dirtyMinX);
					GL.PixelStore(PixelStoreParameter.UnpackSkipRows, dirtyMinY);
					GL.TexSubImage2D(TextureTarget.Texture2D, 0, dirtyMinX, dirtyMinY, dirtyMaxX - dirtyMinX, dirtyMaxY - dirtyMinY, PixelFormat.Red, PixelType.UnsignedByte, pixels);
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
					GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
					GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
					ClearDirty();
				}

				return handle;
			}

			public void Release()
			{
				if (handle != 0)
				{
					GL.DeleteTexture(handle);
					handle = 0;
				}

				uploaded = false;
			}

			private void ClearDirty()
			{
				dirtyMinX = dirtyMinY = int.MaxValue;
				dirtyMaxX = dirtyMaxY = -1;
			}
		}

		#endregion
	}
}
