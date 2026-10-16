using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	public enum StyleValueType : byte
	{
		Invalid,

		/// <summary>auto, none, initial, inherit (the keyword is in <see cref="StyleValue.keyword" />).</summary>
		Keyword,
		Float,

		/// <summary>A number with a unit (px, %, s, ms, deg, rad, grad, turn).</summary>
		Dimension,
		Color,
		String,

		/// <summary>An identifier (enum value name such as flex-start, or a function argument).</summary>
		Identifier,

		/// <summary>url("...") / resource("..."): the path is in text, the loaded asset (if any) in reference.</summary>
		ResourcePath,

		/// <summary>A function call (translate, rotate, scale, var, rgb...): name in text, arguments follow until the matching FunctionEnd.</summary>
		Function,
		FunctionEnd,
		Comma,

		/// <summary>An object set from code (texture, font, style sheet).</summary>
		Reference
	}

	public enum StyleUnit : byte
	{
		None,
		Pixel,
		Percent,
		Second,
		Millisecond,
		Degree,
		Radian,
		Gradian,
		Turn
	}

	/// <summary>
	///     One token of a USS declaration value (a declaration is an array of them): a number, a dimension, a color,
	///     a keyword, a string or a function. Inline styles set from code are converted to the same representation, so
	///     the cascade applies both the same way.
	/// </summary>
	public struct StyleValue
	{
		#region PublicFields

		public StyleValueType type;
		public StyleUnit unit;
		public StyleKeyword keyword;
		public float number;
		public Color4 color;
		public string text;
		public object reference;

		#endregion

		#region PublicMethods

		public static StyleValue Keyword(StyleKeyword keyword)
		{
			return new StyleValue { type = StyleValueType.Keyword, keyword = keyword };
		}

		public static StyleValue Float(float value)
		{
			return new StyleValue { type = StyleValueType.Float, number = value };
		}

		public static StyleValue Dimension(float value, StyleUnit unit)
		{
			return new StyleValue { type = StyleValueType.Dimension, number = value, unit = unit };
		}

		public static StyleValue Pixels(float value)
		{
			return Dimension(value, StyleUnit.Pixel);
		}

		public static StyleValue Percent(float value)
		{
			return Dimension(value, StyleUnit.Percent);
		}

		public static StyleValue FromLength(Length length)
		{
			if (length.IsAuto) return Keyword(StyleKeyword.Auto);
			if (length.IsNone) return Keyword(StyleKeyword.None);
			return Dimension(length.value, length.unit == LengthUnit.Percent ? StyleUnit.Percent : StyleUnit.Pixel);
		}

		public static StyleValue FromAngle(Angle angle)
		{
			StyleUnit unit;
			switch (angle.unit)
			{
				case AngleUnit.Radian: unit = StyleUnit.Radian; break;
				case AngleUnit.Gradian: unit = StyleUnit.Gradian; break;
				case AngleUnit.Turn: unit = StyleUnit.Turn; break;
				default: unit = StyleUnit.Degree; break;
			}

			return Dimension(angle.value, unit);
		}

		public static StyleValue FromTime(TimeValue time)
		{
			return Dimension(time.value, time.unit == TimeUnit.Millisecond ? StyleUnit.Millisecond : StyleUnit.Second);
		}

		public static StyleValue FromColor(Color4 color)
		{
			return new StyleValue { type = StyleValueType.Color, color = color };
		}

		public static StyleValue Identifier(string name)
		{
			return new StyleValue { type = StyleValueType.Identifier, text = name };
		}

		public static StyleValue String(string value)
		{
			return new StyleValue { type = StyleValueType.String, text = value };
		}

		public static StyleValue Resource(string path, object loaded)
		{
			return new StyleValue { type = StyleValueType.ResourcePath, text = path, reference = loaded };
		}

		public static StyleValue Reference(object value)
		{
			return new StyleValue { type = StyleValueType.Reference, reference = value };
		}

		public static StyleValue Function(string name)
		{
			return new StyleValue { type = StyleValueType.Function, text = name };
		}

		public static StyleValue FunctionEnd()
		{
			return new StyleValue { type = StyleValueType.FunctionEnd };
		}

		public static StyleValue Comma()
		{
			return new StyleValue { type = StyleValueType.Comma };
		}

		public bool IsKeyword(StyleKeyword value)
		{
			return type == StyleValueType.Keyword && keyword == value;
		}

		public bool IsIdentifier(string name)
		{
			return type == StyleValueType.Identifier && string.Equals(text, name, StringComparison.OrdinalIgnoreCase);
		}

		public bool IsNumber => type == StyleValueType.Float || type == StyleValueType.Dimension;

		public bool IsLength => type == StyleValueType.Float || (type == StyleValueType.Dimension && (unit == StyleUnit.Pixel || unit == StyleUnit.Percent));

		public bool IsAngle => type == StyleValueType.Float || (type == StyleValueType.Dimension && (unit == StyleUnit.Degree || unit == StyleUnit.Radian || unit == StyleUnit.Gradian || unit == StyleUnit.Turn));

		public bool IsTime => type == StyleValueType.Dimension && (unit == StyleUnit.Second || unit == StyleUnit.Millisecond);

		public Length ToLength()
		{
			if (type == StyleValueType.Keyword)
			{
				if (keyword == StyleKeyword.Auto) return Length.Auto();
				if (keyword == StyleKeyword.None) return Length.None();
			}

			if (type == StyleValueType.Dimension && unit == StyleUnit.Percent)
			{
				return Length.Percent(number);
			}

			return Length.Pixels(number);
		}

		public Angle ToAngle()
		{
			switch (unit)
			{
				case StyleUnit.Radian: return Angle.Radians(number);
				case StyleUnit.Gradian: return new Angle(number, AngleUnit.Gradian);
				case StyleUnit.Turn: return Angle.Turns(number);
				default: return Angle.Degrees(number);
			}
		}

		public TimeValue ToTime()
		{
			return unit == StyleUnit.Millisecond ? new TimeValue(number, TimeUnit.Millisecond) : new TimeValue(number, TimeUnit.Second);
		}

		public override string ToString()
		{
			switch (type)
			{
				case StyleValueType.Keyword: return keyword.ToString().ToLowerInvariant();
				case StyleValueType.Float: return number.ToString(CultureInfo.InvariantCulture);
				case StyleValueType.Dimension: return number.ToString(CultureInfo.InvariantCulture) + UnitSuffix(unit);
				case StyleValueType.Color: return StyleColorUtility.ToString(color);
				case StyleValueType.String: return "\"" + text + "\"";
				case StyleValueType.Identifier: return text;
				case StyleValueType.ResourcePath: return "url(\"" + text + "\")";
				case StyleValueType.Function: return text + "(";
				case StyleValueType.FunctionEnd: return ")";
				case StyleValueType.Comma: return ",";
				case StyleValueType.Reference: return reference?.ToString() ?? "null";
				default: return "<invalid>";
			}
		}

		public static string UnitSuffix(StyleUnit unit)
		{
			switch (unit)
			{
				case StyleUnit.Pixel: return "px";
				case StyleUnit.Percent: return "%";
				case StyleUnit.Second: return "s";
				case StyleUnit.Millisecond: return "ms";
				case StyleUnit.Degree: return "deg";
				case StyleUnit.Radian: return "rad";
				case StyleUnit.Gradian: return "grad";
				case StyleUnit.Turn: return "turn";
				default: return "";
			}
		}

		public static string ToString(StyleValue[] values)
		{
			if (values == null)
			{
				return "";
			}

			var builder = new System.Text.StringBuilder();
			for (int i = 0; i < values.Length; i++)
			{
				if (i > 0 && values[i].type != StyleValueType.Comma && values[i].type != StyleValueType.FunctionEnd && values[i - 1].type != StyleValueType.Function)
				{
					builder.Append(' ');
				}

				builder.Append(values[i].ToString());
			}

			return builder.ToString();
		}

		#endregion
	}

	/// <summary>Color parsing and formatting shared by the USS parser, the inline style API and UXML attributes.</summary>
	public static class StyleColorUtility
	{
		#region PrivateFields

		private static readonly Dictionary<string, Color4> named = new Dictionary<string, Color4>(StringComparer.OrdinalIgnoreCase)
		{
			{ "transparent", new Color4(0f, 0f, 0f, 0f) },
			{ "black", new Color4(0f, 0f, 0f, 1f) },
			{ "white", new Color4(1f, 1f, 1f, 1f) },
			{ "red", new Color4(1f, 0f, 0f, 1f) },
			{ "green", new Color4(0f, 128f / 255f, 0f, 1f) },
			{ "lime", new Color4(0f, 1f, 0f, 1f) },
			{ "blue", new Color4(0f, 0f, 1f, 1f) },
			{ "yellow", new Color4(1f, 1f, 0f, 1f) },
			{ "cyan", new Color4(0f, 1f, 1f, 1f) },
			{ "aqua", new Color4(0f, 1f, 1f, 1f) },
			{ "magenta", new Color4(1f, 0f, 1f, 1f) },
			{ "fuchsia", new Color4(1f, 0f, 1f, 1f) },
			{ "gray", new Color4(128f / 255f, 128f / 255f, 128f / 255f, 1f) },
			{ "grey", new Color4(128f / 255f, 128f / 255f, 128f / 255f, 1f) },
			{ "silver", new Color4(192f / 255f, 192f / 255f, 192f / 255f, 1f) },
			{ "maroon", new Color4(128f / 255f, 0f, 0f, 1f) },
			{ "olive", new Color4(128f / 255f, 128f / 255f, 0f, 1f) },
			{ "navy", new Color4(0f, 0f, 128f / 255f, 1f) },
			{ "purple", new Color4(128f / 255f, 0f, 128f / 255f, 1f) },
			{ "teal", new Color4(0f, 128f / 255f, 128f / 255f, 1f) },
			{ "orange", new Color4(1f, 165f / 255f, 0f, 1f) },
			{ "brown", new Color4(165f / 255f, 42f / 255f, 42f / 255f, 1f) },
			{ "pink", new Color4(1f, 192f / 255f, 203f / 255f, 1f) },
			{ "gold", new Color4(1f, 215f / 255f, 0f, 1f) },
			{ "coral", new Color4(1f, 127f / 255f, 80f / 255f, 1f) },
			{ "salmon", new Color4(250f / 255f, 128f / 255f, 114f / 255f, 1f) },
			{ "tomato", new Color4(1f, 99f / 255f, 71f / 255f, 1f) },
			{ "crimson", new Color4(220f / 255f, 20f / 255f, 60f / 255f, 1f) },
			{ "indigo", new Color4(75f / 255f, 0f, 130f / 255f, 1f) },
			{ "violet", new Color4(238f / 255f, 130f / 255f, 238f / 255f, 1f) },
			{ "khaki", new Color4(240f / 255f, 230f / 255f, 140f / 255f, 1f) },
			{ "ivory", new Color4(1f, 1f, 240f / 255f, 1f) },
			{ "beige", new Color4(245f / 255f, 245f / 255f, 220f / 255f, 1f) },
			{ "tan", new Color4(210f / 255f, 180f / 255f, 140f / 255f, 1f) },
			{ "turquoise", new Color4(64f / 255f, 224f / 255f, 208f / 255f, 1f) },
			{ "skyblue", new Color4(135f / 255f, 206f / 255f, 235f / 255f, 1f) },
			{ "steelblue", new Color4(70f / 255f, 130f / 255f, 180f / 255f, 1f) },
			{ "royalblue", new Color4(65f / 255f, 105f / 255f, 225f / 255f, 1f) },
			{ "dodgerblue", new Color4(30f / 255f, 144f / 255f, 1f, 1f) },
			{ "deepskyblue", new Color4(0f, 191f / 255f, 1f, 1f) },
			{ "slategray", new Color4(112f / 255f, 128f / 255f, 144f / 255f, 1f) },
			{ "darkgray", new Color4(169f / 255f, 169f / 255f, 169f / 255f, 1f) },
			{ "darkgrey", new Color4(169f / 255f, 169f / 255f, 169f / 255f, 1f) },
			{ "lightgray", new Color4(211f / 255f, 211f / 255f, 211f / 255f, 1f) },
			{ "lightgrey", new Color4(211f / 255f, 211f / 255f, 211f / 255f, 1f) },
			{ "dimgray", new Color4(105f / 255f, 105f / 255f, 105f / 255f, 1f) },
			{ "whitesmoke", new Color4(245f / 255f, 245f / 255f, 245f / 255f, 1f) },
			{ "gainsboro", new Color4(220f / 255f, 220f / 255f, 220f / 255f, 1f) },
			{ "darkred", new Color4(139f / 255f, 0f, 0f, 1f) },
			{ "darkgreen", new Color4(0f, 100f / 255f, 0f, 1f) },
			{ "darkblue", new Color4(0f, 0f, 139f / 255f, 1f) },
			{ "darkorange", new Color4(1f, 140f / 255f, 0f, 1f) },
			{ "limegreen", new Color4(50f / 255f, 205f / 255f, 50f / 255f, 1f) },
			{ "forestgreen", new Color4(34f / 255f, 139f / 255f, 34f / 255f, 1f) },
			{ "seagreen", new Color4(46f / 255f, 139f / 255f, 87f / 255f, 1f) },
			{ "springgreen", new Color4(0f, 1f, 127f / 255f, 1f) },
			{ "chartreuse", new Color4(127f / 255f, 1f, 0f, 1f) },
			{ "orchid", new Color4(218f / 255f, 112f / 255f, 214f / 255f, 1f) },
			{ "plum", new Color4(221f / 255f, 160f / 255f, 221f / 255f, 1f) },
			{ "lavender", new Color4(230f / 255f, 230f / 255f, 250f / 255f, 1f) },
			{ "hotpink", new Color4(1f, 105f / 255f, 180f / 255f, 1f) },
			{ "deeppink", new Color4(1f, 20f / 255f, 147f / 255f, 1f) },
			{ "wheat", new Color4(245f / 255f, 222f / 255f, 179f / 255f, 1f) },
			{ "chocolate", new Color4(210f / 255f, 105f / 255f, 30f / 255f, 1f) },
			{ "sienna", new Color4(160f / 255f, 82f / 255f, 45f / 255f, 1f) },
			{ "firebrick", new Color4(178f / 255f, 34f / 255f, 34f / 255f, 1f) },
			{ "midnightblue", new Color4(25f / 255f, 25f / 255f, 112f / 255f, 1f) },
			{ "cornflowerblue", new Color4(100f / 255f, 149f / 255f, 237f / 255f, 1f) },
			{ "mediumpurple", new Color4(147f / 255f, 112f / 255f, 219f / 255f, 1f) },
			{ "slateblue", new Color4(106f / 255f, 90f / 255f, 205f / 255f, 1f) },
			{ "cadetblue", new Color4(95f / 255f, 158f / 255f, 160f / 255f, 1f) },
			{ "aquamarine", new Color4(127f / 255f, 1f, 212f / 255f, 1f) },
			{ "mintcream", new Color4(245f / 255f, 1f, 250f / 255f, 1f) },
			{ "snow", new Color4(1f, 250f / 255f, 250f / 255f, 1f) },
			{ "linen", new Color4(250f / 255f, 240f / 255f, 230f / 255f, 1f) },
			{ "peru", new Color4(205f / 255f, 133f / 255f, 63f / 255f, 1f) },
			{ "goldenrod", new Color4(218f / 255f, 165f / 255f, 32f / 255f, 1f) },
			{ "darkslategray", new Color4(47f / 255f, 79f / 255f, 79f / 255f, 1f) },
			{ "lightblue", new Color4(173f / 255f, 216f / 255f, 230f / 255f, 1f) },
			{ "lightgreen", new Color4(144f / 255f, 238f / 255f, 144f / 255f, 1f) },
			{ "lightyellow", new Color4(1f, 1f, 224f / 255f, 1f) },
			{ "lightcoral", new Color4(240f / 255f, 128f / 255f, 128f / 255f, 1f) },
			{ "lightpink", new Color4(1f, 182f / 255f, 193f / 255f, 1f) },
			{ "lightseagreen", new Color4(32f / 255f, 178f / 255f, 170f / 255f, 1f) },
			{ "lightskyblue", new Color4(135f / 255f, 206f / 255f, 250f / 255f, 1f) },
			{ "lightslategray", new Color4(119f / 255f, 136f / 255f, 153f / 255f, 1f) },
			{ "lightsteelblue", new Color4(176f / 255f, 196f / 255f, 222f / 255f, 1f) },
			{ "mediumseagreen", new Color4(60f / 255f, 179f / 255f, 113f / 255f, 1f) },
			{ "mediumslateblue", new Color4(123f / 255f, 104f / 255f, 238f / 255f, 1f) },
			{ "mediumvioletred", new Color4(199f / 255f, 21f / 255f, 133f / 255f, 1f) },
			{ "palegreen", new Color4(152f / 255f, 251f / 255f, 152f / 255f, 1f) },
			{ "paleturquoise", new Color4(175f / 255f, 238f / 255f, 238f / 255f, 1f) },
			{ "palevioletred", new Color4(219f / 255f, 112f / 255f, 147f / 255f, 1f) },
			{ "rebeccapurple", new Color4(102f / 255f, 51f / 255f, 153f / 255f, 1f) },
			{ "darkcyan", new Color4(0f, 139f / 255f, 139f / 255f, 1f) },
			{ "darkmagenta", new Color4(139f / 255f, 0f, 139f / 255f, 1f) },
			{ "darkviolet", new Color4(148f / 255f, 0f, 211f / 255f, 1f) },
			{ "darkolivegreen", new Color4(85f / 255f, 107f / 255f, 47f / 255f, 1f) },
			{ "darkkhaki", new Color4(189f / 255f, 183f / 255f, 107f / 255f, 1f) },
			{ "darkgoldenrod", new Color4(184f / 255f, 134f / 255f, 11f / 255f, 1f) },
			{ "darkslateblue", new Color4(72f / 255f, 61f / 255f, 139f / 255f, 1f) },
			{ "darkturquoise", new Color4(0f, 206f / 255f, 209f / 255f, 1f) },
			{ "darkseagreen", new Color4(143f / 255f, 188f / 255f, 143f / 255f, 1f) },
			{ "darksalmon", new Color4(233f / 255f, 150f / 255f, 122f / 255f, 1f) },
			{ "darkorchid", new Color4(153f / 255f, 50f / 255f, 204f / 255f, 1f) },
			{ "orangered", new Color4(1f, 69f / 255f, 0f, 1f) },
			{ "yellowgreen", new Color4(154f / 255f, 205f / 255f, 50f / 255f, 1f) },
			{ "greenyellow", new Color4(173f / 255f, 1f, 47f / 255f, 1f) },
			{ "olivedrab", new Color4(107f / 255f, 142f / 255f, 35f / 255f, 1f) },
			{ "lawngreen", new Color4(124f / 255f, 252f / 255f, 0f, 1f) },
			{ "mediumaquamarine", new Color4(102f / 255f, 205f / 255f, 170f / 255f, 1f) },
			{ "mediumblue", new Color4(0f, 0f, 205f / 255f, 1f) },
			{ "mediumorchid", new Color4(186f / 255f, 85f / 255f, 211f / 255f, 1f) },
			{ "mediumspringgreen", new Color4(0f, 250f / 255f, 154f / 255f, 1f) },
			{ "mediumturquoise", new Color4(72f / 255f, 209f / 255f, 204f / 255f, 1f) },
			{ "powderblue", new Color4(176f / 255f, 224f / 255f, 230f / 255f, 1f) },
			{ "sandybrown", new Color4(244f / 255f, 164f / 255f, 96f / 255f, 1f) },
			{ "rosybrown", new Color4(188f / 255f, 143f / 255f, 143f / 255f, 1f) },
			{ "saddlebrown", new Color4(139f / 255f, 69f / 255f, 19f / 255f, 1f) },
			{ "burlywood", new Color4(222f / 255f, 184f / 255f, 135f / 255f, 1f) },
			{ "thistle", new Color4(216f / 255f, 191f / 255f, 216f / 255f, 1f) },
			{ "honeydew", new Color4(240f / 255f, 1f, 240f / 255f, 1f) },
			{ "azure", new Color4(240f / 255f, 1f, 1f, 1f) },
			{ "aliceblue", new Color4(240f / 255f, 248f / 255f, 1f, 1f) },
			{ "ghostwhite", new Color4(248f / 255f, 248f / 255f, 1f, 1f) },
			{ "seashell", new Color4(1f, 245f / 255f, 238f / 255f, 1f) },
			{ "oldlace", new Color4(253f / 255f, 245f / 255f, 230f / 255f, 1f) },
			{ "floralwhite", new Color4(1f, 250f / 255f, 240f / 255f, 1f) },
			{ "cornsilk", new Color4(1f, 248f / 255f, 220f / 255f, 1f) },
			{ "lemonchiffon", new Color4(1f, 250f / 255f, 205f / 255f, 1f) },
			{ "papayawhip", new Color4(1f, 239f / 255f, 213f / 255f, 1f) },
			{ "blanchedalmond", new Color4(1f, 235f / 255f, 205f / 255f, 1f) },
			{ "bisque", new Color4(1f, 228f / 255f, 196f / 255f, 1f) },
			{ "moccasin", new Color4(1f, 228f / 255f, 181f / 255f, 1f) },
			{ "navajowhite", new Color4(1f, 222f / 255f, 173f / 255f, 1f) },
			{ "peachpuff", new Color4(1f, 218f / 255f, 185f / 255f, 1f) },
			{ "mistyrose", new Color4(1f, 228f / 255f, 225f / 255f, 1f) },
			{ "lavenderblush", new Color4(1f, 240f / 255f, 245f / 255f, 1f) },
			{ "antiquewhite", new Color4(250f / 255f, 235f / 255f, 215f / 255f, 1f) },
			{ "indianred", new Color4(205f / 255f, 92f / 255f, 92f / 255f, 1f) }
		};

		#endregion

		#region PublicMethods

		/// <summary>Parses #rgb, #rgba, #rrggbb, #rrggbbaa or a CSS color name.</summary>
		public static bool TryParse(string text, out Color4 color)
		{
			color = default;
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			text = text.Trim();
			if (text[0] == '#')
			{
				return TryParseHex(text.Substring(1), out color);
			}

			return named.TryGetValue(text, out color);
		}

		public static bool TryParseHex(string hex, out Color4 color)
		{
			color = default;
			int r, g, b, a = 255;
			switch (hex.Length)
			{
				case 3:
				case 4:
					if (!Hex(hex[0], out r) || !Hex(hex[1], out g) || !Hex(hex[2], out b)) return false;
					r = r * 17;
					g = g * 17;
					b = b * 17;
					if (hex.Length == 4)
					{
						if (!Hex(hex[3], out a)) return false;
						a *= 17;
					}
					break;
				case 6:
				case 8:
					if (!Hex2(hex, 0, out r) || !Hex2(hex, 2, out g) || !Hex2(hex, 4, out b)) return false;
					if (hex.Length == 8 && !Hex2(hex, 6, out a)) return false;
					break;
				default:
					return false;
			}

			color = new Color4(r / 255f, g / 255f, b / 255f, a / 255f);
			return true;
		}

		public static string ToString(Color4 color)
		{
			int r = (int)Math.Round(Math.Clamp(color.R, 0f, 1f) * 255f);
			int g = (int)Math.Round(Math.Clamp(color.G, 0f, 1f) * 255f);
			int b = (int)Math.Round(Math.Clamp(color.B, 0f, 1f) * 255f);
			int a = (int)Math.Round(Math.Clamp(color.A, 0f, 1f) * 255f);
			return a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{r:X2}{g:X2}{b:X2}{a:X2}";
		}

		#endregion

		#region PrivateMethods

		private static bool Hex(char c, out int value)
		{
			if (c >= '0' && c <= '9') { value = c - '0'; return true; }
			if (c >= 'a' && c <= 'f') { value = c - 'a' + 10; return true; }
			if (c >= 'A' && c <= 'F') { value = c - 'A' + 10; return true; }
			value = 0;
			return false;
		}

		private static bool Hex2(string s, int index, out int value)
		{
			int hi, lo;
			value = 0;
			if (!Hex(s[index], out hi) || !Hex(s[index + 1], out lo))
			{
				return false;
			}

			value = hi * 16 + lo;
			return true;
		}

		#endregion
	}
}
