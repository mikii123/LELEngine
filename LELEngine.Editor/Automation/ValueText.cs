using System;
using System.Globalization;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     Values of value fields as text, both ways: what the element list shows and what <see cref="EditorAutomation.Input" />
	///     takes. Numbers in the invariant culture; vectors and colors as their components ("1, 2.5, 0", "(1 2 3)"); colors
	///     also "#RRGGBB" / "#RRGGBBAA"; booleans true / false (also 1 / 0, on / off, yes / no).
	/// </summary>
	internal static class ValueText
	{
		#region PublicMethods

		public static string Format(float value)
		{
			return value.ToString("0.#####", CultureInfo.InvariantCulture);
		}

		public static string Format(double value)
		{
			return value.ToString("0.#########", CultureInfo.InvariantCulture);
		}

		public static string Format(int value)
		{
			return value.ToString(CultureInfo.InvariantCulture);
		}

		public static string Format(bool value)
		{
			return value ? "true" : "false";
		}

		/// <summary>Components joined: "1, 2.5, 0".</summary>
		public static string Format(ReadOnlySpan<float> values)
		{
			var text = new System.Text.StringBuilder();
			for (int i = 0; i < values.Length; i++)
			{
				text.Append(i > 0 ? ", " : "").Append(Format(values[i]));
			}

			return text.ToString();
		}

		public static string Format(ReadOnlySpan<int> values)
		{
			var text = new System.Text.StringBuilder();
			for (int i = 0; i < values.Length; i++)
			{
				text.Append(i > 0 ? ", " : "").Append(Format(values[i]));
			}

			return text.ToString();
		}

		/// <summary>A text field's text for the element list: one line, at most 80 characters.</summary>
		public static string FormatText(string text)
		{
			if (text == null)
			{
				return "";
			}

			string line = text.Replace("\r", "").Replace("\n", "\\n");
			return line.Length > 80 ? line.Substring(0, 77) + "..." : line;
		}

		public static bool TryParse(string text, out float value)
		{
			return float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}

		public static bool TryParse(string text, out double value)
		{
			return double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}

		/// <summary>An integer ("12"; "12.0" too, a whole number written as a float).</summary>
		public static bool TryParse(string text, out int value)
		{
			text = text?.Trim();
			if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
			{
				return true;
			}

			if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
			{
				value = (int)d;
				return true;
			}

			return false;
		}

		public static bool TryParse(string text, out bool value)
		{
			switch (text?.Trim().ToLowerInvariant())
			{
				case "true":
				case "1":
				case "on":
				case "yes":
					value = true;
					return true;
				case "false":
				case "0":
				case "off":
				case "no":
					value = false;
					return true;
				default:
					value = false;
					return false;
			}
		}

		/// <summary>Exactly <paramref name="values" />.Length numbers, separated by commas, semicolons or spaces, optionally in parentheses.</summary>
		public static bool TryParse(string text, Span<float> values)
		{
			string[] parts = Components(text);
			if (parts.Length != values.Length)
			{
				return false;
			}

			for (int i = 0; i < parts.Length; i++)
			{
				if (!TryParse(parts[i], out values[i]))
				{
					return false;
				}
			}

			return true;
		}

		public static bool TryParse(string text, Span<int> values)
		{
			string[] parts = Components(text);
			if (parts.Length != values.Length)
			{
				return false;
			}

			for (int i = 0; i < parts.Length; i++)
			{
				if (!TryParse(parts[i], out values[i]))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		///     A color into <paramref name="rgba" /> (3 or 4 floats): "#RRGGBB" / "#RRGGBBAA" (alpha 1 when left out), or its
		///     components ("1, 0.5, 0" / "1, 0.5, 0, 1"; with four places three components keep alpha 1).
		/// </summary>
		public static bool TryParseColor(string text, Span<float> rgba)
		{
			text = text?.Trim() ?? "";
			if (text.StartsWith("#", StringComparison.Ordinal))
			{
				string hex = text.Substring(1);
				if ((hex.Length != 6 && hex.Length != 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
				{
					return false;
				}

				if (hex.Length == 6)
				{
					bits = (bits << 8) | 0xFF;
				}

				Span<float> parsed = stackalloc float[4] { (bits >> 24) / 255f, ((bits >> 16) & 0xFF) / 255f, ((bits >> 8) & 0xFF) / 255f, (bits & 0xFF) / 255f };
				parsed.Slice(0, rgba.Length).CopyTo(rgba);
				return true;
			}

			string[] parts = Components(text);
			if (parts.Length != rgba.Length && !(rgba.Length == 4 && parts.Length == 3))
			{
				return false;
			}

			for (int i = 0; i < parts.Length; i++)
			{
				if (!TryParse(parts[i], out rgba[i]))
				{
					return false;
				}
			}

			if (parts.Length < rgba.Length)
			{
				rgba[3] = 1f;
			}

			return true;
		}

		#endregion

		#region PrivateMethods

		private static string[] Components(string text)
		{
			return (text ?? "").Trim().Trim('(', ')', '[', ']').Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		}

		#endregion
	}
}
