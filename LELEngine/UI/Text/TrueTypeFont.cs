using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Minimal TrueType / OpenType (TrueType outlines) reader: character map, glyph outlines (simple and composite),
	///     horizontal metrics and pair kerning (kern table and GPOS pair adjustment). Enough to lay out and rasterize
	///     text; no hinting, no CFF outlines, no complex shaping.
	/// </summary>
	internal sealed class TrueTypeFont
	{
		#region PublicFields

		public int UnitsPerEm { get; private set; } = 1000;
		public int Ascender { get; private set; }
		public int Descender { get; private set; }
		public int LineGap { get; private set; }
		public int GlyphCount { get; private set; }
		public string FamilyName { get; private set; }
		public string StyleName { get; private set; }
		public bool HasGlyphOutlines => glyfOffset > 0 && locaOffset > 0;

		/// <summary>Glyph bounding box in font units (xMin, yMin, xMax, yMax).</summary>
		public struct GlyphBox
		{
			public int xMin, yMin, xMax, yMax;
		}

		#endregion

		#region PrivateFields

		private byte[] data;
		private readonly Dictionary<string, (int offset, int length)> tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
		private int locaOffset, glyfOffset, glyfLength, hmtxOffset, numberOfHMetrics;
		private bool longLoca;
		private int cmapOffset;
		private int cmapFormat;
		private readonly Dictionary<int, int> cmapCache = new Dictionary<int, int>();
		private Dictionary<uint, short> kernPairs;
		private List<GposPairSubtable> gposPairs;
		private readonly Dictionary<int, int> advanceCache = new Dictionary<int, int>();

		#endregion

		#region PublicMethods

		public static TrueTypeFont Load(string path)
		{
			byte[] bytes = File.ReadAllBytes(path);
			return Parse(bytes);
		}

		public static TrueTypeFont Parse(byte[] bytes)
		{
			var font = new TrueTypeFont { data = bytes };
			font.ParseTables();
			return font;
		}

		/// <summary>Glyph index of a Unicode code point (0 = missing glyph).</summary>
		public int GetGlyphIndex(int codepoint)
		{
			int index;
			if (cmapCache.TryGetValue(codepoint, out index))
			{
				return index;
			}

			index = LookupCmap(codepoint);
			cmapCache[codepoint] = index;
			return index;
		}

		/// <summary>Horizontal advance of a glyph in font units.</summary>
		public int GetAdvance(int glyphIndex)
		{
			int advance;
			if (advanceCache.TryGetValue(glyphIndex, out advance))
			{
				return advance;
			}

			if (hmtxOffset == 0 || numberOfHMetrics == 0)
			{
				advance = UnitsPerEm / 2;
			}
			else
			{
				int index = Math.Min(glyphIndex, numberOfHMetrics - 1);
				advance = ReadUInt16(hmtxOffset + index * 4);
			}

			advanceCache[glyphIndex] = advance;
			return advance;
		}

		/// <summary>Left side bearing of a glyph in font units.</summary>
		public int GetLeftSideBearing(int glyphIndex)
		{
			if (hmtxOffset == 0 || numberOfHMetrics == 0)
			{
				return 0;
			}

			if (glyphIndex < numberOfHMetrics)
			{
				return ReadInt16(hmtxOffset + glyphIndex * 4 + 2);
			}

			return ReadInt16(hmtxOffset + numberOfHMetrics * 4 + (glyphIndex - numberOfHMetrics) * 2);
		}

		/// <summary>Kerning adjustment (font units, added to the first glyph's advance) for a glyph pair.</summary>
		public int GetKerning(int left, int right)
		{
			if (gposPairs != null)
			{
				for (int i = 0; i < gposPairs.Count; i++)
				{
					int value;
					if (gposPairs[i].TryGet(this, left, right, out value))
					{
						return value;
					}
				}
			}

			if (kernPairs != null)
			{
				short value;
				if (kernPairs.TryGetValue(((uint)left << 16) | (uint)right, out value))
				{
					return value;
				}
			}

			return 0;
		}

		/// <summary>
		///     Outline of a glyph as contours of points in font units; quadratic control points are marked off-curve.
		///     Returns false for empty glyphs (spaces) and unsupported outlines.
		/// </summary>
		public bool GetGlyphOutline(int glyphIndex, List<GlyphContour> contours, out GlyphBox box)
		{
			box = default;
			contours.Clear();
			if (!HasGlyphOutlines || glyphIndex < 0 || glyphIndex >= GlyphCount)
			{
				return false;
			}

			int offset, length;
			if (!GetGlyphLocation(glyphIndex, out offset, out length) || length < 10)
			{
				return false;
			}

			box = new GlyphBox { xMin = ReadInt16(offset + 2), yMin = ReadInt16(offset + 4), xMax = ReadInt16(offset + 6), yMax = ReadInt16(offset + 8) };
			AppendGlyph(glyphIndex, contours, 1f, 0f, 0f, 1f, 0f, 0f, 0);
			return contours.Count > 0;
		}

		#endregion

		#region PrivateMethods

		// ------------------------------------------------------------------ tables

		private void ParseTables()
		{
			uint tag = ReadUInt32(0);
			int offset = 0;
			if (tag == 0x74746366) // 'ttcf': font collection, use the first font
			{
				offset = (int)ReadUInt32(12);
			}

			int numTables = ReadUInt16(offset + 4);
			int record = offset + 12;
			for (int i = 0; i < numTables; i++, record += 16)
			{
				string name = Encoding.ASCII.GetString(data, record, 4);
				int tableOffset = (int)ReadUInt32(record + 8);
				int tableLength = (int)ReadUInt32(record + 12);
				if (tableOffset >= 0 && tableOffset + Math.Max(0, tableLength) <= data.Length)
				{
					tables[name] = (tableOffset, tableLength);
				}
			}

			(int offset, int length) head;
			if (tables.TryGetValue("head", out head))
			{
				UnitsPerEm = ReadUInt16(head.offset + 18);
				if (UnitsPerEm == 0) UnitsPerEm = 1000;
				longLoca = ReadInt16(head.offset + 50) != 0;
			}

			(int offset, int length) maxp;
			if (tables.TryGetValue("maxp", out maxp))
			{
				GlyphCount = ReadUInt16(maxp.offset + 4);
			}

			(int offset, int length) hhea;
			if (tables.TryGetValue("hhea", out hhea))
			{
				Ascender = ReadInt16(hhea.offset + 4);
				Descender = ReadInt16(hhea.offset + 6);
				LineGap = ReadInt16(hhea.offset + 8);
				numberOfHMetrics = ReadUInt16(hhea.offset + 34);
			}

			(int offset, int length) os2;
			if (tables.TryGetValue("OS/2", out os2) && os2.length >= 78)
			{
				int fsSelection = ReadUInt16(os2.offset + 62);
				bool useTypoMetrics = (fsSelection & 0x80) != 0;
				int typoAscender = ReadInt16(os2.offset + 68);
				int typoDescender = ReadInt16(os2.offset + 70);
				int typoLineGap = ReadInt16(os2.offset + 72);
				if (useTypoMetrics || (Ascender == 0 && Descender == 0))
				{
					Ascender = typoAscender;
					Descender = typoDescender;
					LineGap = typoLineGap;
				}
			}

			if (Ascender == 0 && Descender == 0)
			{
				Ascender = (int)(UnitsPerEm * 0.8f);
				Descender = -(int)(UnitsPerEm * 0.2f);
			}

			(int offset, int length) hmtx;
			if (tables.TryGetValue("hmtx", out hmtx))
			{
				hmtxOffset = hmtx.offset;
			}

			(int offset, int length) loca, glyf;
			if (tables.TryGetValue("loca", out loca) && tables.TryGetValue("glyf", out glyf))
			{
				locaOffset = loca.offset;
				glyfOffset = glyf.offset;
				glyfLength = glyf.length;
			}

			ParseCmap();
			ParseKern();
			ParseGpos();
			ParseNames();
		}

		private void ParseNames()
		{
			(int offset, int length) name;
			if (!tables.TryGetValue("name", out name))
			{
				return;
			}

			int count = ReadUInt16(name.offset + 2);
			int stringOffset = name.offset + ReadUInt16(name.offset + 4);
			int record = name.offset + 6;
			for (int i = 0; i < count; i++, record += 12)
			{
				int platform = ReadUInt16(record);
				int nameId = ReadUInt16(record + 6);
				int length = ReadUInt16(record + 8);
				int off = ReadUInt16(record + 10);
				if ((nameId != 1 && nameId != 2) || stringOffset + off + length > data.Length)
				{
					continue;
				}

				string value;
				if (platform == 3 || platform == 0)
				{
					value = Encoding.BigEndianUnicode.GetString(data, stringOffset + off, length);
				}
				else
				{
					value = Encoding.ASCII.GetString(data, stringOffset + off, length);
				}

				if (nameId == 1 && (FamilyName == null || platform == 3)) FamilyName = value;
				if (nameId == 2 && (StyleName == null || platform == 3)) StyleName = value;
			}
		}

		// ------------------------------------------------------------------ cmap

		private void ParseCmap()
		{
			(int offset, int length) cmap;
			if (!tables.TryGetValue("cmap", out cmap))
			{
				return;
			}

			int count = ReadUInt16(cmap.offset + 2);
			int best = -1;
			int bestScore = -1;
			for (int i = 0; i < count; i++)
			{
				int record = cmap.offset + 4 + i * 8;
				int platform = ReadUInt16(record);
				int encoding = ReadUInt16(record + 2);
				int subtable = cmap.offset + (int)ReadUInt32(record + 4);
				if (subtable >= data.Length - 4)
				{
					continue;
				}

				int format = ReadUInt16(subtable);
				int score;
				if (platform == 3 && encoding == 10 && format == 12) score = 5;
				else if (platform == 0 && format == 12) score = 4;
				else if (platform == 3 && encoding == 1 && format == 4) score = 3;
				else if (platform == 0 && format == 4) score = 2;
				else if (platform == 3 && encoding == 0) score = 1;
				else if (format == 0 || format == 4 || format == 6 || format == 12) score = 0;
				else continue;

				if (score > bestScore)
				{
					bestScore = score;
					best = subtable;
					cmapFormat = format;
				}
			}

			cmapOffset = best;
		}

		private int LookupCmap(int codepoint)
		{
			if (cmapOffset <= 0)
			{
				return 0;
			}

			switch (cmapFormat)
			{
				case 0:
					return codepoint < 256 ? data[cmapOffset + 6 + codepoint] : 0;
				case 4:
					return LookupFormat4(codepoint);
				case 6:
				{
					int first = ReadUInt16(cmapOffset + 6);
					int count = ReadUInt16(cmapOffset + 8);
					int index = codepoint - first;
					return index >= 0 && index < count ? ReadUInt16(cmapOffset + 10 + index * 2) : 0;
				}
				case 12:
					return LookupFormat12(codepoint);
				default:
					return 0;
			}
		}

		private int LookupFormat4(int codepoint)
		{
			if (codepoint > 0xFFFF)
			{
				return 0;
			}

			int segCountX2 = ReadUInt16(cmapOffset + 6);
			int segCount = segCountX2 / 2;
			int endCodes = cmapOffset + 14;
			int startCodes = endCodes + segCountX2 + 2;
			int idDeltas = startCodes + segCountX2;
			int idRangeOffsets = idDeltas + segCountX2;

			int lo = 0, hi = segCount - 1;
			while (lo <= hi)
			{
				int mid = (lo + hi) >> 1;
				int end = ReadUInt16(endCodes + mid * 2);
				if (codepoint > end)
				{
					lo = mid + 1;
					continue;
				}

				int start = ReadUInt16(startCodes + mid * 2);
				if (codepoint < start)
				{
					hi = mid - 1;
					continue;
				}

				int delta = ReadInt16(idDeltas + mid * 2);
				int rangeOffset = ReadUInt16(idRangeOffsets + mid * 2);
				if (rangeOffset == 0)
				{
					return (codepoint + delta) & 0xFFFF;
				}

				int glyphAddress = idRangeOffsets + mid * 2 + rangeOffset + (codepoint - start) * 2;
				if (glyphAddress + 1 >= data.Length)
				{
					return 0;
				}

				int glyph = ReadUInt16(glyphAddress);
				return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
			}

			return 0;
		}

		private int LookupFormat12(int codepoint)
		{
			int groups = (int)ReadUInt32(cmapOffset + 12);
			int lo = 0, hi = groups - 1;
			while (lo <= hi)
			{
				int mid = (lo + hi) >> 1;
				int record = cmapOffset + 16 + mid * 12;
				uint start = ReadUInt32(record);
				uint end = ReadUInt32(record + 4);
				if (codepoint < start) hi = mid - 1;
				else if (codepoint > end) lo = mid + 1;
				else return (int)(ReadUInt32(record + 8) + (codepoint - start));
			}

			return 0;
		}

		// ------------------------------------------------------------------ kerning

		private void ParseKern()
		{
			(int offset, int length) kern;
			if (!tables.TryGetValue("kern", out kern) || kern.length < 4)
			{
				return;
			}

			int version = ReadUInt16(kern.offset);
			int subtableCount;
			int position;
			if (version == 0)
			{
				subtableCount = ReadUInt16(kern.offset + 2);
				position = kern.offset + 4;
			}
			else
			{
				// Apple format: 32-bit version and count.
				subtableCount = (int)ReadUInt32(kern.offset + 4);
				position = kern.offset + 8;
			}

			for (int i = 0; i < subtableCount && position + 6 <= data.Length; i++)
			{
				int subtableLength;
				int format;
				int coverage;
				if (version == 0)
				{
					subtableLength = ReadUInt16(position + 2);
					coverage = ReadUInt16(position + 4);
					format = coverage >> 8;
					bool horizontal = (coverage & 1) != 0;
					if (format == 0 && horizontal)
					{
						ReadKernFormat0(position + 6);
					}
				}
				else
				{
					subtableLength = (int)ReadUInt32(position);
					coverage = ReadUInt16(position + 4);
					format = coverage & 0xFF;
					if (format == 0 && (coverage & 0x8000) == 0)
					{
						ReadKernFormat0(position + 8);
					}
				}

				if (subtableLength <= 0)
				{
					break;
				}

				position += subtableLength;
			}
		}

		private void ReadKernFormat0(int position)
		{
			int pairCount = ReadUInt16(position);
			int pair = position + 8;
			kernPairs ??= new Dictionary<uint, short>(pairCount);
			for (int i = 0; i < pairCount && pair + 6 <= data.Length; i++, pair += 6)
			{
				uint left = (uint)ReadUInt16(pair);
				uint right = (uint)ReadUInt16(pair + 2);
				short value = ReadInt16(pair + 4);
				kernPairs[(left << 16) | right] = value;
			}
		}

		private void ParseGpos()
		{
			(int offset, int length) gpos;
			if (!tables.TryGetValue("GPOS", out gpos) || gpos.length < 10)
			{
				return;
			}

			try
			{
				int scriptList = gpos.offset + ReadUInt16(gpos.offset + 4);
				int featureList = gpos.offset + ReadUInt16(gpos.offset + 6);
				int lookupList = gpos.offset + ReadUInt16(gpos.offset + 8);

				// Feature indices of 'kern' in the default language systems of every script.
				var lookupIndices = new HashSet<int>();
				int scriptCount = ReadUInt16(scriptList);
				for (int s = 0; s < scriptCount; s++)
				{
					int script = scriptList + ReadUInt16(scriptList + 2 + s * 6 + 4);
					int defaultLangSys = ReadUInt16(script);
					if (defaultLangSys == 0)
					{
						int langSysCount = ReadUInt16(script + 2);
						if (langSysCount == 0) continue;
						defaultLangSys = ReadUInt16(script + 4 + 4);
					}

					int langSys = script + defaultLangSys;
					int featureCount = ReadUInt16(langSys + 4);
					for (int f = 0; f < featureCount; f++)
					{
						int featureIndex = ReadUInt16(langSys + 6 + f * 2);
						int featureRecord = featureList + 2 + featureIndex * 6;
						string tag = Encoding.ASCII.GetString(data, featureRecord, 4);
						if (tag != "kern")
						{
							continue;
						}

						int feature = featureList + ReadUInt16(featureRecord + 4);
						int lookupCount = ReadUInt16(feature + 2);
						for (int l = 0; l < lookupCount; l++)
						{
							lookupIndices.Add(ReadUInt16(feature + 4 + l * 2));
						}
					}
				}

				if (lookupIndices.Count == 0)
				{
					return;
				}

				var sorted = new List<int>(lookupIndices);
				sorted.Sort();
				foreach (int lookupIndex in sorted)
				{
					int lookup = lookupList + ReadUInt16(lookupList + 2 + lookupIndex * 2);
					int type = ReadUInt16(lookup);
					int subtableCount = ReadUInt16(lookup + 4);
					for (int st = 0; st < subtableCount; st++)
					{
						int subtable = lookup + ReadUInt16(lookup + 6 + st * 2);
						int subtableType = type;
						if (type == 9)
						{
							subtableType = ReadUInt16(subtable + 2);
							subtable += (int)ReadUInt32(subtable + 4);
						}

						if (subtableType == 2)
						{
							GposPairSubtable parsed = GposPairSubtable.Parse(this, subtable);
							if (parsed != null)
							{
								(gposPairs ??= new List<GposPairSubtable>()).Add(parsed);
							}
						}
					}
				}
			}
			catch (Exception)
			{
				gposPairs = null;
			}
		}

		// ------------------------------------------------------------------ glyphs

		private bool GetGlyphLocation(int glyphIndex, out int offset, out int length)
		{
			int start, end;
			if (longLoca)
			{
				start = (int)ReadUInt32(locaOffset + glyphIndex * 4);
				end = (int)ReadUInt32(locaOffset + glyphIndex * 4 + 4);
			}
			else
			{
				start = ReadUInt16(locaOffset + glyphIndex * 2) * 2;
				end = ReadUInt16(locaOffset + glyphIndex * 2 + 2) * 2;
			}

			offset = glyfOffset + start;
			length = end - start;
			return length > 0 && start >= 0 && end <= glyfLength;
		}

		private void AppendGlyph(int glyphIndex, List<GlyphContour> contours, float a, float b, float c, float d, float dx, float dy, int depth)
		{
			if (depth > 8 || glyphIndex < 0 || glyphIndex >= GlyphCount)
			{
				return;
			}

			int offset, length;
			if (!GetGlyphLocation(glyphIndex, out offset, out length) || length < 10)
			{
				return;
			}

			int contourCount = ReadInt16(offset);
			if (contourCount >= 0)
			{
				AppendSimpleGlyph(offset, contourCount, contours, a, b, c, d, dx, dy);
			}
			else
			{
				AppendCompositeGlyph(offset, contours, a, b, c, d, dx, dy, depth);
			}
		}

		private void AppendSimpleGlyph(int offset, int contourCount, List<GlyphContour> contours, float a, float b, float c, float d, float dx, float dy)
		{
			int position = offset + 10;
			var endPoints = new int[contourCount];
			for (int i = 0; i < contourCount; i++)
			{
				endPoints[i] = ReadUInt16(position);
				position += 2;
			}

			int pointCount = contourCount > 0 ? endPoints[contourCount - 1] + 1 : 0;
			int instructionLength = ReadUInt16(position);
			position += 2 + instructionLength;

			var flags = new byte[pointCount];
			for (int i = 0; i < pointCount;)
			{
				byte flag = data[position++];
				flags[i++] = flag;
				if ((flag & 8) != 0)
				{
					int repeat = data[position++];
					for (int r = 0; r < repeat && i < pointCount; r++)
					{
						flags[i++] = flag;
					}
				}
			}

			var xs = new int[pointCount];
			int x = 0;
			for (int i = 0; i < pointCount; i++)
			{
				byte flag = flags[i];
				if ((flag & 2) != 0)
				{
					int delta = data[position++];
					x += (flag & 16) != 0 ? delta : -delta;
				}
				else if ((flag & 16) == 0)
				{
					x += ReadInt16(position);
					position += 2;
				}

				xs[i] = x;
			}

			var ys = new int[pointCount];
			int y = 0;
			for (int i = 0; i < pointCount; i++)
			{
				byte flag = flags[i];
				if ((flag & 4) != 0)
				{
					int delta = data[position++];
					y += (flag & 32) != 0 ? delta : -delta;
				}
				else if ((flag & 32) == 0)
				{
					y += ReadInt16(position);
					position += 2;
				}

				ys[i] = y;
			}

			int start = 0;
			for (int ci = 0; ci < contourCount; ci++)
			{
				int end = endPoints[ci];
				if (end < start)
				{
					start = end + 1;
					continue;
				}

				var contour = new GlyphContour();
				for (int i = start; i <= end; i++)
				{
					float px = xs[i], py = ys[i];
					contour.points.Add(new GlyphPoint
					{
						x = a * px + c * py + dx,
						y = b * px + d * py + dy,
						onCurve = (flags[i] & 1) != 0
					});
				}

				contours.Add(contour);
				start = end + 1;
			}
		}

		private void AppendCompositeGlyph(int offset, List<GlyphContour> contours, float a, float b, float c, float d, float dx, float dy, int depth)
		{
			int position = offset + 10;
			while (true)
			{
				int flags = ReadUInt16(position);
				int componentIndex = ReadUInt16(position + 2);
				position += 4;
				float argX, argY;
				if ((flags & 1) != 0)
				{
					argX = ReadInt16(position);
					argY = ReadInt16(position + 2);
					position += 4;
				}
				else
				{
					argX = (sbyte)data[position];
					argY = (sbyte)data[position + 1];
					position += 2;
				}

				float ca = 1f, cb = 0f, cc = 0f, cd = 1f;
				if ((flags & 8) != 0)
				{
					ca = cd = ReadF2Dot14(position);
					position += 2;
				}
				else if ((flags & 0x40) != 0)
				{
					ca = ReadF2Dot14(position);
					cd = ReadF2Dot14(position + 2);
					position += 4;
				}
				else if ((flags & 0x80) != 0)
				{
					ca = ReadF2Dot14(position);
					cb = ReadF2Dot14(position + 2);
					cc = ReadF2Dot14(position + 4);
					cd = ReadF2Dot14(position + 6);
					position += 8;
				}

				// Point-matching offsets (ARGS_ARE_XY_VALUES clear) are rare; treated as offsets of 0.
				float ox = (flags & 2) != 0 ? argX : 0f;
				float oy = (flags & 2) != 0 ? argY : 0f;

				// Combined transform: outer * component.
				float na = a * ca + c * cb;
				float nb = b * ca + d * cb;
				float nc = a * cc + c * cd;
				float nd = b * cc + d * cd;
				float ndx = a * ox + c * oy + dx;
				float ndy = b * ox + d * oy + dy;
				AppendGlyph(componentIndex, contours, na, nb, nc, nd, ndx, ndy, depth + 1);

				if ((flags & 0x20) == 0)
				{
					break;
				}
			}
		}

		// ------------------------------------------------------------------ reading

		internal int ReadUInt16(int offset)
		{
			return offset + 1 < data.Length ? (data[offset] << 8) | data[offset + 1] : 0;
		}

		internal short ReadInt16(int offset)
		{
			return (short)ReadUInt16(offset);
		}

		internal uint ReadUInt32(int offset)
		{
			return offset + 3 < data.Length ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3] : 0u;
		}

		private float ReadF2Dot14(int offset)
		{
			return ReadInt16(offset) / 16384f;
		}

		#endregion

		#region NestedTypes

		/// <summary>GPOS lookup type 2 (pair adjustment), formats 1 and 2; only the first glyph's x advance is used.</summary>
		private sealed class GposPairSubtable
		{
			private Coverage coverage;
			private int format;
			// Format 1
			private Dictionary<int, Dictionary<int, int>> pairSets;
			// Format 2
			private ClassDef classDef1, classDef2;
			private int[,] classValues;

			public static GposPairSubtable Parse(TrueTypeFont font, int offset)
			{
				var result = new GposPairSubtable { format = font.ReadUInt16(offset) };
				result.coverage = Coverage.Parse(font, offset + font.ReadUInt16(offset + 2));
				int valueFormat1 = font.ReadUInt16(offset + 4);
				int valueFormat2 = font.ReadUInt16(offset + 6);
				int size1 = ValueRecordSize(valueFormat1);
				int size2 = ValueRecordSize(valueFormat2);
				int advanceOffset = XAdvanceOffset(valueFormat1);
				if (advanceOffset < 0)
				{
					return null;
				}

				if (result.format == 1)
				{
					int pairSetCount = font.ReadUInt16(offset + 8);
					result.pairSets = new Dictionary<int, Dictionary<int, int>>();
					List<int> glyphs = result.coverage.Glyphs();
					for (int i = 0; i < pairSetCount && i < glyphs.Count; i++)
					{
						int pairSet = offset + font.ReadUInt16(offset + 10 + i * 2);
						int pairCount = font.ReadUInt16(pairSet);
						var map = new Dictionary<int, int>(pairCount);
						int record = pairSet + 2;
						for (int p = 0; p < pairCount; p++)
						{
							int second = font.ReadUInt16(record);
							int value = font.ReadInt16(record + 2 + advanceOffset);
							if (value != 0)
							{
								map[second] = value;
							}

							record += 2 + size1 + size2;
						}

						result.pairSets[glyphs[i]] = map;
					}

					return result;
				}

				if (result.format == 2)
				{
					result.classDef1 = ClassDef.Parse(font, offset + font.ReadUInt16(offset + 8));
					result.classDef2 = ClassDef.Parse(font, offset + font.ReadUInt16(offset + 10));
					int class1Count = font.ReadUInt16(offset + 12);
					int class2Count = font.ReadUInt16(offset + 14);
					result.classValues = new int[class1Count, class2Count];
					int record = offset + 16;
					for (int c1 = 0; c1 < class1Count; c1++)
					{
						for (int c2 = 0; c2 < class2Count; c2++)
						{
							result.classValues[c1, c2] = font.ReadInt16(record + advanceOffset);
							record += size1 + size2;
						}
					}

					return result;
				}

				return null;
			}

			public bool TryGet(TrueTypeFont font, int left, int right, out int value)
			{
				value = 0;
				if (!coverage.Contains(left))
				{
					return false;
				}

				if (format == 1)
				{
					Dictionary<int, int> map;
					return pairSets.TryGetValue(left, out map) && map.TryGetValue(right, out value);
				}

				int c1 = classDef1.ClassOf(left);
				int c2 = classDef2.ClassOf(right);
				if (c1 < classValues.GetLength(0) && c2 < classValues.GetLength(1))
				{
					value = classValues[c1, c2];
					return value != 0;
				}

				return false;
			}

			private static int ValueRecordSize(int format)
			{
				int size = 0;
				for (int bit = 0; bit < 8; bit++)
				{
					if ((format & (1 << bit)) != 0) size += 2;
				}

				return size;
			}

			private static int XAdvanceOffset(int format)
			{
				if ((format & 4) == 0)
				{
					return -1;
				}

				int offset = 0;
				if ((format & 1) != 0) offset += 2;
				if ((format & 2) != 0) offset += 2;
				return offset;
			}
		}

		private sealed class Coverage
		{
			private HashSet<int> glyphSet;
			private List<(int start, int end)> ranges;
			private List<int> ordered;

			public static Coverage Parse(TrueTypeFont font, int offset)
			{
				var coverage = new Coverage();
				int format = font.ReadUInt16(offset);
				int count = font.ReadUInt16(offset + 2);
				coverage.ordered = new List<int>();
				if (format == 1)
				{
					coverage.glyphSet = new HashSet<int>();
					for (int i = 0; i < count; i++)
					{
						int glyph = font.ReadUInt16(offset + 4 + i * 2);
						coverage.glyphSet.Add(glyph);
						coverage.ordered.Add(glyph);
					}
				}
				else
				{
					coverage.ranges = new List<(int, int)>(count);
					for (int i = 0; i < count; i++)
					{
						int record = offset + 4 + i * 6;
						int start = font.ReadUInt16(record);
						int end = font.ReadUInt16(record + 2);
						coverage.ranges.Add((start, end));
						for (int g = start; g <= end && g - start < 4096; g++)
						{
							coverage.ordered.Add(g);
						}
					}
				}

				return coverage;
			}

			public bool Contains(int glyph)
			{
				if (glyphSet != null)
				{
					return glyphSet.Contains(glyph);
				}

				foreach ((int start, int end) in ranges)
				{
					if (glyph >= start && glyph <= end) return true;
				}

				return false;
			}

			public List<int> Glyphs()
			{
				return ordered;
			}
		}

		private sealed class ClassDef
		{
			private int startGlyph;
			private int[] classes;
			private List<(int start, int end, int cls)> ranges;

			public static ClassDef Parse(TrueTypeFont font, int offset)
			{
				var def = new ClassDef();
				int format = font.ReadUInt16(offset);
				if (format == 1)
				{
					def.startGlyph = font.ReadUInt16(offset + 2);
					int count = font.ReadUInt16(offset + 4);
					def.classes = new int[count];
					for (int i = 0; i < count; i++)
					{
						def.classes[i] = font.ReadUInt16(offset + 6 + i * 2);
					}
				}
				else
				{
					int count = font.ReadUInt16(offset + 2);
					def.ranges = new List<(int, int, int)>(count);
					for (int i = 0; i < count; i++)
					{
						int record = offset + 4 + i * 6;
						def.ranges.Add((font.ReadUInt16(record), font.ReadUInt16(record + 2), font.ReadUInt16(record + 4)));
					}
				}

				return def;
			}

			public int ClassOf(int glyph)
			{
				if (classes != null)
				{
					int index = glyph - startGlyph;
					return index >= 0 && index < classes.Length ? classes[index] : 0;
				}

				foreach ((int start, int end, int cls) in ranges)
				{
					if (glyph >= start && glyph <= end) return cls;
				}

				return 0;
			}
		}

		#endregion
	}

	internal struct GlyphPoint
	{
		public float x;
		public float y;
		public bool onCurve;
	}

	internal sealed class GlyphContour
	{
		public readonly List<GlyphPoint> points = new List<GlyphPoint>(16);
	}
}
