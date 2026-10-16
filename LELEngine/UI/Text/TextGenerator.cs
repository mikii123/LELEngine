using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>Everything that influences how a piece of text lays out and looks.</summary>
	internal struct TextSettings
	{
		public string text;
		public FontAsset font;
		public float fontSize;
		public FontStyle fontStyle;
		public Color4 color;
		public WhiteSpace whiteSpace;
		public TextAnchor anchor;
		public float letterSpacing;
		public float wordSpacing;
		public float paragraphSpacing;
		public bool richText;
		public TextOverflow overflow;
		public TextOverflowPosition overflowPosition;

		/// <summary>The element clips its overflow: text-overflow applies.</summary>
		public bool clipOverflow;

		public bool wantCharPositions;
	}

	/// <summary>One glyph to draw, in text-area coordinates (origin at the top-left of the text area, y down).</summary>
	internal struct TextGlyphQuad
	{
		public float x, y, width, height;
		public float u0, v0, u1, v1;
		public FontAsset font;
		public int page;
		public Color4 color;

		/// <summary>Baseline of the glyph's line (italic shear pivots around it).</summary>
		public float baseline;

		/// <summary>Horizontal shear per vertical pixel above the baseline (synthesized italics), 0 for none.</summary>
		public float shear;

		/// <summary>Extra weight in SDF units (synthesized bold), 0 for none.</summary>
		public float boldness;

		public int charIndex;
	}

	/// <summary>Underline or strikethrough bar.</summary>
	internal struct TextDecoration
	{
		public float x, y, width, height;
		public Color4 color;
	}

	internal struct TextLineInfo
	{
		public int startChar;
		public int endChar;
		public float y;
		public float height;
		public float width;
		public float baseline;
		public float x;
	}

	internal struct TextCharPosition
	{
		public int line;
		public float x;
		public float advance;
		public bool visible;
	}

	/// <summary>Output of the text generator for one text and one set of constraints.</summary>
	internal sealed class TextLayoutResult
	{
		public readonly List<TextGlyphQuad> glyphs = new List<TextGlyphQuad>();
		public readonly List<TextDecoration> decorations = new List<TextDecoration>();
		public readonly List<TextLineInfo> lines = new List<TextLineInfo>();
		public List<TextCharPosition> charPositions;

		/// <summary>Natural content size: widest line and total height.</summary>
		public Vector2 size;

		public bool elided;
		public float constraintWidth, constraintHeight;
		public VisualElement.MeasureMode widthMode, heightMode;
		public int settingsHash;
		public string text;
	}

	/// <summary>
	///     Lays out text: rich text tags, glyph shaping with kerning, wrapping, alignment, ellipsis and the geometry
	///     the renderer draws (SDF glyph quads). One instance is reusable; results are allocated per call.
	/// </summary>
	internal static class TextGenerator
	{
		#region PrivateFields

		private struct CharRun
		{
			public int codepoint;
			public int sourceIndex;
			public byte style; // 1 bold, 2 italic, 4 underline, 8 strike
			public bool hasColor;
			public Color4 color;
			public float size; // 0 = default
			public bool isNewline;
			public bool isSpace;

			// Shaped
			public FontAsset face;
			public GlyphInfo glyph;
			public float advance;
			public float scale;
			public bool synthBold;
			public bool synthItalic;
			public float ascent;
			public float descent;
			public float lineGap;
		}

		[ThreadStatic] private static List<CharRun> charBuffer;
		[ThreadStatic] private static List<(int start, int end, bool hardBreak)> lineBuffer;

		private const int Ellipsis = 0x2026;

		#endregion

		#region PublicMethods

		public static TextLayoutResult Generate(in TextSettings settings, float width, VisualElement.MeasureMode widthMode, float height, VisualElement.MeasureMode heightMode)
		{
			var result = new TextLayoutResult
			{
				constraintWidth = width,
				constraintHeight = height,
				widthMode = widthMode,
				heightMode = heightMode,
				text = settings.text
			};

			FontAsset font = settings.font ?? FontAsset.Default;
			if (font == null || string.IsNullOrEmpty(settings.text) || settings.fontSize <= 0f)
			{
				float emptyHeight = font != null && settings.fontSize > 0f ? font.LineHeight(settings.fontSize) : 0f;
				result.size = new Vector2(0f, string.IsNullOrEmpty(settings.text) ? 0f : emptyHeight);
				if (settings.wantCharPositions)
				{
					// An empty text still has a caret position (index 0 on one empty line).
					result.charPositions = new List<TextCharPosition> { new TextCharPosition { line = 0, x = 0f, advance = 0f, visible = true } };
					result.lines.Add(new TextLineInfo { height = emptyHeight, baseline = font != null ? font.ascender * settings.fontSize / font.unitsPerEm : 0f });
				}

				return result;
			}

			List<CharRun> chars = charBuffer ??= new List<CharRun>(256);
			chars.Clear();
			ParseText(settings, chars);
			Shape(settings, font, chars);

			bool canWrap = settings.whiteSpace == WhiteSpace.Normal || settings.whiteSpace == WhiteSpace.PreWrap;
			bool hasWidth = widthMode != VisualElement.MeasureMode.Undefined && !float.IsNaN(width);
			float wrapWidth = canWrap && hasWidth ? Math.Max(0f, width) : float.PositiveInfinity;

			List<(int start, int end, bool hardBreak)> lines = lineBuffer ??= new List<(int, int, bool)>(16);
			lines.Clear();
			BreakLines(chars, wrapWidth, lines);

			// Line metrics.
			float defaultScale = settings.fontSize / font.unitsPerEm;
			float defaultAscent = font.ascender * defaultScale;
			float defaultDescent = -font.descender * defaultScale;
			float defaultGap = font.lineGap * defaultScale;

			var lineInfos = result.lines;
			float totalHeight = 0f;
			float maxWidth = 0f;
			for (int li = 0; li < lines.Count; li++)
			{
				(int start, int end, bool hardBreak) = lines[li];
				float ascent = defaultAscent, descent = defaultDescent, gap = defaultGap;
				float lineWidth = 0f;
				float trailingSpace = 0f;
				for (int i = start; i < end; i++)
				{
					CharRun c = chars[i];
					if (c.isNewline)
					{
						continue;
					}

					ascent = Math.Max(ascent, c.ascent);
					descent = Math.Max(descent, c.descent);
					gap = Math.Max(gap, c.lineGap);
					lineWidth += c.advance;
					trailingSpace = c.isSpace ? trailingSpace + c.advance : 0f;
				}

				// Trailing spaces hang past the edge when the line wrapped.
				bool wrapped = li < lines.Count - 1 && !hardBreak;
				float visibleWidth = wrapped ? lineWidth - trailingSpace : lineWidth;
				float lineHeight = ascent + descent + gap;
				lineInfos.Add(new TextLineInfo { startChar = start, endChar = end, y = totalHeight, height = lineHeight, width = visibleWidth, baseline = totalHeight + ascent + gap * 0.5f });
				totalHeight += lineHeight;
				if (hardBreak && li < lines.Count - 1)
				{
					totalHeight += settings.paragraphSpacing;
				}

				maxWidth = Math.Max(maxWidth, visibleWidth);
			}

			// Ellipsis: drop lines past the height and trim the last visible line when the element clips.
			bool hasHeight = heightMode != VisualElement.MeasureMode.Undefined && !float.IsNaN(height);
			bool ellipsis = settings.clipOverflow && settings.overflow == TextOverflow.Ellipsis;
			int visibleLines = lineInfos.Count;
			if (ellipsis && hasHeight && lineInfos.Count > 1)
			{
				for (int li = 1; li < lineInfos.Count; li++)
				{
					if (lineInfos[li].y + lineInfos[li].height > height + 0.5f)
					{
						visibleLines = li;
						break;
					}
				}
			}

			var elidedLines = new Dictionary<int, List<CharRun>>();
			if (ellipsis && (visibleLines < lineInfos.Count || (hasWidth && !canWrap) || (hasWidth && lineInfos.Count > 0)))
			{
				bool moreLines = visibleLines < lineInfos.Count;
				for (int li = 0; li < visibleLines; li++)
				{
					TextLineInfo line = lineInfos[li];
					bool last = li == visibleLines - 1;
					bool overflowsWidth = hasWidth && line.width > width + 0.5f;
					if ((last && moreLines) || overflowsWidth)
					{
						List<CharRun> elided = Elide(settings, font, chars, line.startChar, line.endChar, hasWidth ? width : float.PositiveInfinity, last && moreLines, out float newWidth);
						elidedLines[li] = elided;
						line.width = newWidth;
						lineInfos[li] = line;
						result.elided = true;
					}
				}

				if (moreLines)
				{
					result.elided = true;
					lineInfos.RemoveRange(visibleLines, lineInfos.Count - visibleLines);
					totalHeight = lineInfos[visibleLines - 1].y + lineInfos[visibleLines - 1].height;
				}

				maxWidth = 0f;
				foreach (TextLineInfo line in lineInfos)
				{
					maxWidth = Math.Max(maxWidth, line.width);
				}
			}

			result.size = new Vector2(maxWidth, totalHeight);

			// Alignment.
			float alignWidth = hasWidth ? width : maxWidth;
			float alignHeight = hasHeight ? height : totalHeight;
			float yOffset = 0f;
			switch (settings.anchor)
			{
				case TextAnchor.MiddleLeft:
				case TextAnchor.MiddleCenter:
				case TextAnchor.MiddleRight:
					yOffset = (alignHeight - totalHeight) * 0.5f;
					break;
				case TextAnchor.LowerLeft:
				case TextAnchor.LowerCenter:
				case TextAnchor.LowerRight:
					yOffset = alignHeight - totalHeight;
					break;
			}

			yOffset = (float)Math.Round(yOffset);

			if (settings.wantCharPositions)
			{
				result.charPositions = new List<TextCharPosition>(chars.Count + 1);
				for (int i = 0; i <= chars.Count; i++)
				{
					result.charPositions.Add(new TextCharPosition { line = -1 });
				}
			}

			// Geometry.
			for (int li = 0; li < lineInfos.Count; li++)
			{
				TextLineInfo line = lineInfos[li];
				float xOffset = 0f;
				switch (settings.anchor)
				{
					case TextAnchor.UpperCenter:
					case TextAnchor.MiddleCenter:
					case TextAnchor.LowerCenter:
						xOffset = (alignWidth - line.width) * 0.5f;
						break;
					case TextAnchor.UpperRight:
					case TextAnchor.MiddleRight:
					case TextAnchor.LowerRight:
						xOffset = alignWidth - line.width;
						break;
				}

				xOffset = (float)Math.Round(xOffset);
				line.x = xOffset;
				line.y += yOffset;
				line.baseline += yOffset;
				lineInfos[li] = line;

				List<CharRun> elided;
				if (elidedLines.TryGetValue(li, out elided))
				{
					EmitLine(settings, result, elided, 0, elided.Count, xOffset, line.baseline, li, false);
				}
				else
				{
					EmitLine(settings, result, chars, line.startChar, line.endChar, xOffset, line.baseline, li, settings.wantCharPositions);
				}
			}

			if (settings.wantCharPositions && lineInfos.Count > 0)
			{
				// The position after the last character (caret at the end).
				TextLineInfo last = lineInfos[lineInfos.Count - 1];
				float endX = last.x;
				for (int i = last.startChar; i < last.endChar; i++)
				{
					if (!chars[i].isNewline) endX += chars[i].advance;
				}

				result.charPositions[chars.Count] = new TextCharPosition { line = lineInfos.Count - 1, x = endX, advance = 0f, visible = true };
				// Characters that were never emitted (newlines) take the start of the following line.
				for (int i = 0; i < chars.Count; i++)
				{
					if (result.charPositions[i].line < 0)
					{
						int lineIndex = Math.Min(LineOf(lineInfos, i), lineInfos.Count - 1);
						result.charPositions[i] = new TextCharPosition { line = lineIndex, x = lineInfos[lineIndex].x + WidthBefore(chars, lineInfos[lineIndex].startChar, i), advance = 0f, visible = false };
					}
				}
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private static int LineOf(List<TextLineInfo> lines, int charIndex)
		{
			for (int li = 0; li < lines.Count; li++)
			{
				if (charIndex >= lines[li].startChar && charIndex < lines[li].endChar)
				{
					return li;
				}
			}

			return lines.Count - 1;
		}

		private static float WidthBefore(List<CharRun> chars, int start, int index)
		{
			float x = 0f;
			for (int i = start; i < index && i < chars.Count; i++)
			{
				if (!chars[i].isNewline) x += chars[i].advance;
			}

			return x;
		}

		// ------------------------------------------------------------------ rich text

		private static void ParseText(in TextSettings settings, List<CharRun> chars)
		{
			string text = settings.text;
			byte style = 0;
			if (settings.fontStyle == FontStyle.Bold || settings.fontStyle == FontStyle.BoldAndItalic) style |= 1;
			if (settings.fontStyle == FontStyle.Italic || settings.fontStyle == FontStyle.BoldAndItalic) style |= 2;

			var styleStack = new Stack<byte>();
			var colorStack = new Stack<Color4>();
			var sizeStack = new Stack<float>();
			bool hasColor = false;
			Color4 color = settings.color;
			float size = 0f;
			bool noParse = false;

			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (settings.richText && c == '<')
				{
					int close = text.IndexOf('>', i + 1);
					if (close > i)
					{
						string tag = text.Substring(i + 1, close - i - 1);
						if (noParse)
						{
							if (string.Equals(tag, "/noparse", StringComparison.OrdinalIgnoreCase))
							{
								noParse = false;
								i = close;
								continue;
							}
						}
						else if (ApplyTag(tag, ref style, ref hasColor, ref color, ref size, ref noParse, styleStack, colorStack, sizeStack, settings.color, chars))
						{
							i = close;
							continue;
						}
					}
				}

				if (c == '\r')
				{
					continue;
				}

				int codepoint = c;
				if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
				{
					codepoint = char.ConvertToUtf32(c, text[i + 1]);
					i++;
				}

				if (c == '\t')
				{
					for (int t = 0; t < 4; t++)
					{
						chars.Add(new CharRun { codepoint = ' ', sourceIndex = i, style = style, hasColor = hasColor, color = color, size = size, isSpace = true });
					}

					continue;
				}

				chars.Add(new CharRun
				{
					codepoint = codepoint,
					sourceIndex = i,
					style = style,
					hasColor = hasColor,
					color = color,
					size = size,
					isNewline = c == '\n',
					isSpace = c == ' ' || c == ' '
				});
			}
		}

		private static bool ApplyTag(string tag, ref byte style, ref bool hasColor, ref Color4 color, ref float size, ref bool noParse, Stack<byte> styleStack, Stack<Color4> colorStack, Stack<float> sizeStack, Color4 defaultColor, List<CharRun> chars)
		{
			string name = tag;
			string argument = null;
			int eq = tag.IndexOf('=');
			if (eq > 0)
			{
				name = tag.Substring(0, eq).Trim();
				argument = tag.Substring(eq + 1).Trim().Trim('"', '\'');
			}

			name = name.Trim();
			switch (name.ToLowerInvariant())
			{
				case "b": styleStack.Push(style); style |= 1; return true;
				case "/b": style = Pop(styleStack, style); return true;
				case "i": styleStack.Push(style); style |= 2; return true;
				case "/i": style = Pop(styleStack, style); return true;
				case "u": styleStack.Push(style); style |= 4; return true;
				case "/u": style = Pop(styleStack, style); return true;
				case "s": styleStack.Push(style); style |= 8; return true;
				case "/s": style = Pop(styleStack, style); return true;
				case "br":
					chars.Add(new CharRun { codepoint = '\n', isNewline = true, style = style, color = color, hasColor = hasColor, size = size, sourceIndex = -1 });
					return true;
				case "noparse": noParse = true; return true;
				case "color":
				{
					Color4 parsed;
					if (argument != null && StyleColorUtility.TryParse(argument, out parsed))
					{
						colorStack.Push(hasColor ? color : defaultColor);
						color = parsed;
						hasColor = true;
						return true;
					}

					return false;
				}

				case "/color":
					color = colorStack.Count > 0 ? colorStack.Pop() : defaultColor;
					hasColor = colorStack.Count > 0 || color != defaultColor;
					return true;
				case "alpha":
				{
					int a;
					if (argument != null && argument.StartsWith("#") && int.TryParse(argument.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
					{
						colorStack.Push(hasColor ? color : defaultColor);
						color = new Color4(color.R, color.G, color.B, a / 255f);
						hasColor = true;
						return true;
					}

					return false;
				}

				case "/alpha":
					color = colorStack.Count > 0 ? colorStack.Pop() : defaultColor;
					return true;
				case "size":
				{
					float value;
					if (argument == null)
					{
						return false;
					}

					// Encoding (see ResolveSize): absolute px > 0, percent as -factor, "+n" / "-n" offsets below -50000.
					float encoded;
					if (argument.EndsWith("%") && float.TryParse(argument.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
					{
						encoded = -value / 100f;
					}
					else if ((argument.StartsWith("+") || argument.StartsWith("-")) && float.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
					{
						encoded = EncodeOffset(value);
					}
					else if (float.TryParse(argument.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
					{
						encoded = value;
					}
					else
					{
						return false;
					}

					sizeStack.Push(size);
					size = encoded;
					return true;
				}

				case "/size":
					size = sizeStack.Count > 0 ? sizeStack.Pop() : 0f;
					return true;
				case "font-weight":
				case "/font-weight":
				case "mark":
				case "/mark":
				case "align":
				case "/align":
				case "sup":
				case "/sup":
				case "sub":
				case "/sub":
				case "link":
				case "/link":
				case "nobr":
				case "/nobr":
					return true;
				default:
					return false;
			}
		}

		private static byte Pop(Stack<byte> stack, byte current)
		{
			return stack.Count > 0 ? stack.Pop() : current;
		}

		/// <summary>Relative sizes ("+2", "-3") are encoded as values below -1000 so they survive in a float.</summary>
		private static float EncodeOffset(float offset)
		{
			return -100000f + offset;
		}

		private static float ResolveSize(float encoded, float defaultSize)
		{
			if (encoded == 0f) return defaultSize;
			if (encoded < -50000f) return Math.Max(1f, defaultSize + (encoded + 100000f));
			if (encoded < 0f) return Math.Max(1f, defaultSize * -encoded);
			return encoded;
		}

		// ------------------------------------------------------------------ shaping

		private static void Shape(in TextSettings settings, FontAsset font, List<CharRun> chars)
		{
			FontAsset previousFace = null;
			int previousGlyph = -1;
			float previousScale = 0f;
			for (int i = 0; i < chars.Count; i++)
			{
				CharRun c = chars[i];
				float size = ResolveSize(c.size, settings.fontSize);
				FontStyle style = (c.style & 3) switch
				{
					1 => FontStyle.Bold,
					2 => FontStyle.Italic,
					3 => FontStyle.BoldAndItalic,
					_ => FontStyle.Normal
				};

				bool synthBold, synthItalic;
				FontAsset face = font.GetVariant(style, out synthBold, out synthItalic);
				if (c.isNewline)
				{
					c.face = face;
					c.scale = size / face.unitsPerEm;
					c.ascent = face.ascender * c.scale;
					c.descent = -face.descender * c.scale;
					c.lineGap = face.lineGap * c.scale;
					c.advance = 0f;
					chars[i] = c;
					previousGlyph = -1;
					continue;
				}

				GlyphInfo glyph;
				FontAsset owner;
				face.TryGetGlyph(c.codepoint, out glyph, out owner);
				float scale = size / owner.unitsPerEm;
				c.face = owner;
				c.glyph = glyph;
				c.scale = scale;
				c.synthBold = synthBold;
				c.synthItalic = synthItalic;
				c.ascent = owner.ascender * scale;
				c.descent = -owner.descender * scale;
				c.lineGap = owner.lineGap * scale;
				c.advance = glyph.advance * scale + settings.letterSpacing;
				if (c.isSpace)
				{
					c.advance += settings.wordSpacing;
				}

				if (synthBold)
				{
					c.advance += size * 0.03f;
				}

				// Kerning adjusts the previous glyph's advance.
				if (previousGlyph >= 0 && ReferenceEquals(previousFace, owner) && previousScale == scale && i > 0)
				{
					int kern = owner.GetKerning(previousGlyph, glyph.glyphIndex);
					if (kern != 0)
					{
						CharRun previous = chars[i - 1];
						previous.advance += kern * scale;
						chars[i - 1] = previous;
					}
				}

				chars[i] = c;
				previousFace = owner;
				previousGlyph = glyph.glyphIndex;
				previousScale = scale;
			}
		}

		// ------------------------------------------------------------------ line breaking

		private static void BreakLines(List<CharRun> chars, float wrapWidth, List<(int start, int end, bool hardBreak)> lines)
		{
			int lineStart = 0;
			float lineWidth = 0f;
			int lastBreak = -1; // index of the first char of the next line if we break at the last opportunity
			float widthAtBreak = 0f;

			for (int i = 0; i < chars.Count; i++)
			{
				CharRun c = chars[i];
				if (c.isNewline)
				{
					lines.Add((lineStart, i + 1, true));
					lineStart = i + 1;
					lineWidth = 0f;
					lastBreak = -1;
					continue;
				}

				bool fits = lineWidth + c.advance <= wrapWidth + 0.01f || i == lineStart;
				if (!fits && !c.isSpace)
				{
					if (lastBreak > lineStart)
					{
						lines.Add((lineStart, lastBreak, false));
						lineStart = lastBreak;
						lineWidth -= widthAtBreak;
						lastBreak = -1;
						// Re-test this char on the new line.
						i--;
						continue;
					}

					// No break opportunity: break before this character.
					lines.Add((lineStart, i, false));
					lineStart = i;
					lineWidth = 0f;
					lastBreak = -1;
					i--;
					continue;
				}

				lineWidth += c.advance;
				if (c.isSpace || IsBreakAfter(c.codepoint) || (i + 1 < chars.Count && IsBreakBefore(chars[i + 1].codepoint)))
				{
					lastBreak = i + 1;
					widthAtBreak = lineWidth;
				}
			}

			lines.Add((lineStart, chars.Count, true));
		}

		private static bool IsBreakAfter(int codepoint)
		{
			return codepoint == '-' || codepoint == '‐' || codepoint == '–' || IsCjk(codepoint);
		}

		private static bool IsBreakBefore(int codepoint)
		{
			return IsCjk(codepoint);
		}

		private static bool IsCjk(int codepoint)
		{
			return (codepoint >= 0x4E00 && codepoint <= 0x9FFF) || (codepoint >= 0x3040 && codepoint <= 0x30FF) || (codepoint >= 0xAC00 && codepoint <= 0xD7AF) || (codepoint >= 0x3400 && codepoint <= 0x4DBF);
		}

		// ------------------------------------------------------------------ ellipsis

		private static List<CharRun> Elide(in TextSettings settings, FontAsset font, List<CharRun> chars, int start, int end, float width, bool forceEllipsis, out float newWidth)
		{
			var visible = new List<CharRun>();
			for (int i = start; i < end; i++)
			{
				if (!chars[i].isNewline)
				{
					visible.Add(chars[i]);
				}
			}

			// Trailing spaces never count.
			while (visible.Count > 0 && visible[visible.Count - 1].isSpace)
			{
				visible.RemoveAt(visible.Count - 1);
			}

			CharRun ellipsisChar = MakeEllipsis(settings, font, visible.Count > 0 ? visible[0] : default);
			float ellipsisWidth = ellipsisChar.advance;
			float total = 0f;
			foreach (CharRun c in visible) total += c.advance;
			if (total <= width && !forceEllipsis)
			{
				newWidth = total;
				return visible;
			}

			float budget = width - ellipsisWidth;
			var result = new List<CharRun>();
			switch (settings.overflowPosition)
			{
				case TextOverflowPosition.Start:
				{
					float used = 0f;
					int index = visible.Count - 1;
					var tail = new List<CharRun>();
					while (index >= 0 && used + visible[index].advance <= budget)
					{
						used += visible[index].advance;
						tail.Add(visible[index]);
						index--;
					}

					tail.Reverse();
					result.Add(ellipsisChar);
					result.AddRange(tail);
					newWidth = used + ellipsisWidth;
					break;
				}

				case TextOverflowPosition.Middle:
				{
					float used = 0f;
					int left = 0, right = visible.Count - 1;
					var head = new List<CharRun>();
					var tail = new List<CharRun>();
					bool takeLeft = true;
					while (left <= right)
					{
						CharRun candidate = takeLeft ? visible[left] : visible[right];
						if (used + candidate.advance > budget)
						{
							break;
						}

						used += candidate.advance;
						if (takeLeft)
						{
							head.Add(candidate);
							left++;
						}
						else
						{
							tail.Add(candidate);
							right--;
						}

						takeLeft = !takeLeft;
					}

					tail.Reverse();
					result.AddRange(head);
					result.Add(ellipsisChar);
					result.AddRange(tail);
					newWidth = used + ellipsisWidth;
					break;
				}

				default:
				{
					float used = 0f;
					int index = 0;
					while (index < visible.Count && used + visible[index].advance <= budget)
					{
						used += visible[index].advance;
						result.Add(visible[index]);
						index++;
					}

					// Do not end on a space before the ellipsis.
					while (result.Count > 0 && result[result.Count - 1].isSpace)
					{
						used -= result[result.Count - 1].advance;
						result.RemoveAt(result.Count - 1);
					}

					result.Add(ellipsisChar);
					newWidth = used + ellipsisWidth;
					break;
				}
			}

			return result;
		}

		private static CharRun MakeEllipsis(in TextSettings settings, FontAsset font, CharRun template)
		{
			float size = ResolveSize(template.size, settings.fontSize);
			bool synthBold, synthItalic;
			FontAsset face = font.GetVariant(FontStyle.Normal, out synthBold, out synthItalic);
			GlyphInfo glyph;
			FontAsset owner;
			int codepoint = Ellipsis;
			if (!face.TryGetGlyph(Ellipsis, out glyph, out owner))
			{
				codepoint = '.';
				face.TryGetGlyph('.', out glyph, out owner);
			}

			float scale = size / owner.unitsPerEm;
			var run = new CharRun
			{
				codepoint = codepoint,
				sourceIndex = -1,
				style = template.style,
				hasColor = template.hasColor,
				color = template.color,
				face = owner,
				glyph = glyph,
				scale = scale,
				ascent = owner.ascender * scale,
				descent = -owner.descender * scale,
				lineGap = owner.lineGap * scale,
				advance = glyph.advance * scale * (codepoint == '.' ? 3f : 1f)
			};
			return run;
		}

		// ------------------------------------------------------------------ geometry

		private static void EmitLine(in TextSettings settings, TextLayoutResult result, List<CharRun> chars, int start, int end, float x, float baseline, int lineIndex, bool recordPositions)
		{
			float penX = x;
			float roundedBaseline = (float)Math.Round(baseline);
			int underlineStart = -1, strikeStart = -1;
			float underlineX = 0f, strikeX = 0f;
			Color4 underlineColor = default, strikeColor = default;
			float decorationSize = settings.fontSize;

			for (int i = start; i < end; i++)
			{
				CharRun c = chars[i];
				if (recordPositions && i < result.charPositions.Count && i >= 0)
				{
					result.charPositions[i] = new TextCharPosition { line = lineIndex, x = penX, advance = c.advance, visible = true };
				}

				if (c.isNewline)
				{
					continue;
				}

				Color4 color = c.hasColor ? c.color : settings.color;
				float size = ResolveSize(c.size, settings.fontSize);
				int repeats = c.codepoint == '.' && c.sourceIndex == -1 && c.advance > 0f ? 3 : 1;
				float glyphAdvance = repeats > 1 ? c.advance / repeats : c.advance;
				for (int r = 0; r < repeats; r++)
				{
					if (c.glyph.hasBitmap)
					{
						float s = size / FontAsset.SdfFontSize;
						float gx = (float)Math.Round(penX + r * glyphAdvance) + c.glyph.bearingX * s;
						float gy = roundedBaseline + c.glyph.bearingY * s;
						result.glyphs.Add(new TextGlyphQuad
						{
							x = gx,
							y = gy,
							width = c.glyph.width * s,
							height = c.glyph.height * s,
							u0 = c.glyph.u0,
							v0 = c.glyph.v0,
							u1 = c.glyph.u1,
							v1 = c.glyph.v1,
							font = c.face,
							page = c.glyph.page,
							color = color,
							baseline = roundedBaseline,
							shear = c.synthItalic ? 0.2f : 0f,
							boldness = c.synthBold ? 0.08f : 0f,
							charIndex = c.sourceIndex
						});
					}
				}

				// Underline / strikethrough runs.
				bool underline = (c.style & 4) != 0;
				bool strike = (c.style & 8) != 0;
				if (underline && underlineStart < 0)
				{
					underlineStart = i;
					underlineX = penX;
					underlineColor = color;
					decorationSize = size;
				}
				else if (!underline && underlineStart >= 0)
				{
					result.decorations.Add(Decoration(underlineX, penX, roundedBaseline + decorationSize * 0.1f, decorationSize, underlineColor));
					underlineStart = -1;
				}

				if (strike && strikeStart < 0)
				{
					strikeStart = i;
					strikeX = penX;
					strikeColor = color;
					decorationSize = size;
				}
				else if (!strike && strikeStart >= 0)
				{
					result.decorations.Add(Decoration(strikeX, penX, roundedBaseline - decorationSize * 0.3f, decorationSize, strikeColor));
					strikeStart = -1;
				}

				penX += c.advance;
			}

			if (underlineStart >= 0)
			{
				result.decorations.Add(Decoration(underlineX, penX, roundedBaseline + decorationSize * 0.1f, decorationSize, underlineColor));
			}

			if (strikeStart >= 0)
			{
				result.decorations.Add(Decoration(strikeX, penX, roundedBaseline - decorationSize * 0.3f, decorationSize, strikeColor));
			}
		}

		private static TextDecoration Decoration(float x0, float x1, float y, float size, Color4 color)
		{
			float thickness = Math.Max(1f, (float)Math.Round(size * 0.06f));
			return new TextDecoration { x = x0, y = (float)Math.Round(y), width = x1 - x0, height = thickness, color = color };
		}

		#endregion
	}

	/// <summary>Measures text with an element's text style (used by the layout engine and <see cref="VisualElement.MeasureTextSize" />).</summary>
	internal static class TextMeasurement
	{
		public static Vector2 Measure(VisualElement element, string text, float width, VisualElement.MeasureMode widthMode, float height, VisualElement.MeasureMode heightMode)
		{
			TextSettings settings = BuildSettings(element, text);
			TextLayoutResult result = TextGenerator.Generate(settings, width, widthMode, height, heightMode);
			return result.size;
		}

		/// <summary>Text settings from an element's resolved style.</summary>
		public static TextSettings BuildSettings(VisualElement element, string text)
		{
			ComputedStyle style = element.computedStyle;
			InheritedStyle inherited = style.inherited;
			float fontSize = inherited.fontSize.Resolve(12f);
			if (float.IsNaN(fontSize) || fontSize <= 0f) fontSize = 12f;
			var settings = new TextSettings
			{
				text = text,
				font = inherited.ResolveFont(),
				fontSize = fontSize,
				fontStyle = inherited.unityFontStyleAndWeight,
				color = inherited.color,
				whiteSpace = inherited.whiteSpace,
				anchor = inherited.unityTextAlign,
				letterSpacing = Value(inherited.letterSpacing, fontSize),
				wordSpacing = Value(inherited.wordSpacing, fontSize),
				paragraphSpacing = Value(inherited.unityParagraphSpacing, fontSize),
				richText = true,
				overflow = style.textOverflow,
				overflowPosition = inherited.unityTextOverflowPosition,
				clipOverflow = style.overflow == Overflow.Hidden
			};

			if (element is TextElement textElement)
			{
				settings.richText = textElement.enableRichText;
			}

			return settings;
		}

		private static float Value(Length length, float reference)
		{
			float v = length.Resolve(reference);
			return float.IsNaN(v) ? 0f : v;
		}
	}
}
