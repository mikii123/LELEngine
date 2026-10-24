using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;

namespace LELEngine.Shaders
{
	public enum ShaderPropertyType
	{
		Float,
		Int,
		Range,
		Color,
		Vector,
		Texture2D,
		Cubemap,
		Texture3D
	}

	/// <summary>
	///     A material parameter declared by a shader in its Properties block, with the display name, type, range and
	///     default the material editor shows (Unity syntax):
	///     <code>
	///     //Properties
	///     Color ("Color", Color) = (1, 1, 1, 1)
	///     [HDR] Emissive ("Emissive", Color) = (0, 0, 0, 0)
	///     Roughness ("Roughness", Range(0, 1)) = 0.6
	///     DiffuseMap ("Albedo", 2D) = "white" {}
	///     Sky ("Cubemap", Cube) = "" {}
	///     Speed ("Speed", Float) = 1
	///     Count ("Count", Int) = 2
	///     Params ("Params", Vector) = (0, 0, 0, 0)
	///     /////Properties
	///     </code>
	///     Attributes: [HDR] (color picker with intensity), [HideInInspector], [NoScaleOffset] and [Normal] (accepted).
	///     Keyword attributes (Unity's) on Float / Int / Range properties pick the shader variant a material draws with
	///     (see <see cref="ShaderProgram.GetVariant" />):
	///     <code>
	///     [Toggle] Glow ("Glow", Float) = 0                         // keyword GLOW_ON while the value is not 0
	///     [Toggle(ALPHA_CLIP)] AlphaClip ("Alpha Clipping", Float) = 0   // keyword ALPHA_CLIP
	///     [KeywordEnum(Alpha, Soft Additive)] Mode ("Mode", Float) = 0   // MODE_ALPHA or MODE_SOFT_ADDITIVE (value = index)
	///     </code>
	///     The same block takes the shader's render state lines (Blend, ZWrite, ZTest, Cull, Queue, ShadowCaster; see
	///     <see cref="ShaderRenderState" />), which may sit in #if / #ifdef blocks over the variant's keywords.
	/// </summary>
	public sealed class ShaderProperty
	{
		#region PublicFields

		public string Name;
		public string DisplayName;
		public ShaderPropertyType Type;
		public float RangeMin;
		public float RangeMax = 1f;

		/// <summary>Default of float / int (x), range (x), vector and color properties.</summary>
		public Vector4 DefaultValue;

		/// <summary>Default texture of texture properties: "white", "black", "gray", "bump" / "normal" or empty.</summary>
		public string DefaultTexture = "";

		public bool Hdr;
		public bool HideInInspector;

		/// <summary>
		///     Keywords the value enables, one at a time: [Toggle] has one (on while the value is not 0), [KeywordEnum]
		///     one per option (the option at the value's index). Null for properties that pick no variant.
		/// </summary>
		public string[] Keywords;

		/// <summary>Display names of a [KeywordEnum]'s options (null otherwise).</summary>
		public string[] EnumOptions;

		public bool IsToggle => Keywords != null && EnumOptions == null;
		public bool IsKeywordEnum => EnumOptions != null;

		public bool IsTexture => Type == ShaderPropertyType.Texture2D || Type == ShaderPropertyType.Cubemap || Type == ShaderPropertyType.Texture3D;

		/// <summary>The declared default as a material value: what a material without this property draws with.</summary>
		internal MaterialProperty DefaultValueProperty => defaultValueProperty ??= Material.CreateDefault(this);

		#endregion

		#region PublicMethods

		/// <summary>Parses the lines of a Properties block (comment lines and blanks are skipped).</summary>
		public static List<ShaderProperty> Parse(IEnumerable<string> lines, string source = null)
		{
			var result = new List<ShaderProperty>();
			foreach (string rawLine in lines)
			{
				string line = rawLine.Trim();
				if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
				{
					continue;
				}

				ShaderProperty property = ParseLine(line);
				if (property != null)
				{
					result.Add(property);
				}
				else
				{
					Console.WriteLine("[Shader] Cannot parse the property line '" + line + "'" + (source != null ? " in " + source : ""));
				}
			}

			return result;
		}

		public static ShaderProperty ParseLine(string line)
		{
			Match match = Pattern.Match(line);
			if (!match.Success)
			{
				return null;
			}

			var property = new ShaderProperty { Name = match.Groups["name"].Value, DisplayName = match.Groups["display"].Value };
			foreach (Capture attribute in match.Groups["attr"].Captures)
			{
				string text = attribute.Value.Trim().Trim('[', ']').Trim();
				if (text.Equals("HDR", StringComparison.OrdinalIgnoreCase)) property.Hdr = true;
				else if (text.Equals("HideInInspector", StringComparison.OrdinalIgnoreCase)) property.HideInInspector = true;
				else ParseKeywordAttribute(property, text);
			}

			string type = match.Groups["type"].Value;
			string defaults = match.Groups["default"].Value.Trim();
			switch (type.ToLowerInvariant())
			{
				case "float":
					property.Type = ShaderPropertyType.Float;
					property.DefaultValue = new Vector4(ParseFloat(defaults), 0f, 0f, 0f);
					break;
				case "int":
				case "integer":
					property.Type = ShaderPropertyType.Int;
					property.DefaultValue = new Vector4((int)ParseFloat(defaults), 0f, 0f, 0f);
					break;
				case "range":
					property.Type = ShaderPropertyType.Range;
					property.RangeMin = ParseFloat(match.Groups["min"].Value);
					property.RangeMax = ParseFloat(match.Groups["max"].Value);
					property.DefaultValue = new Vector4(ParseFloat(defaults), 0f, 0f, 0f);
					break;
				case "color":
					property.Type = ShaderPropertyType.Color;
					property.DefaultValue = ParseVector(defaults, new Vector4(1f, 1f, 1f, 1f));
					break;
				case "vector":
					property.Type = ShaderPropertyType.Vector;
					property.DefaultValue = ParseVector(defaults, Vector4.Zero);
					break;
				case "2d":
					property.Type = ShaderPropertyType.Texture2D;
					property.DefaultTexture = ParseTextureDefault(defaults);
					break;
				case "cube":
					property.Type = ShaderPropertyType.Cubemap;
					property.DefaultTexture = ParseTextureDefault(defaults);
					break;
				case "3d":
					property.Type = ShaderPropertyType.Texture3D;
					property.DefaultTexture = ParseTextureDefault(defaults);
					break;
				default:
					return null;
			}

			if (property.Keywords != null && property.Type != ShaderPropertyType.Float && property.Type != ShaderPropertyType.Int && property.Type != ShaderPropertyType.Range)
			{
				// Only numbers pick variants.
				property.Keywords = null;
				property.EnumOptions = null;
			}

			return property;
		}

		/// <summary>The keyword a value of this property enables (the option at its index, or the toggle's when not 0); null for none.</summary>
		public string KeywordFor(float value)
		{
			if (Keywords == null)
			{
				return null;
			}

			if (IsKeywordEnum)
			{
				return Keywords[OptionIndex(value)];
			}

			return value != 0f ? Keywords[0] : null;
		}

		/// <summary>The [KeywordEnum] option a value selects (rounded, clamped to the options).</summary>
		public int OptionIndex(float value)
		{
			int count = EnumOptions?.Length ?? 1;
			return Math.Clamp((int)MathF.Round(value), 0, count - 1);
		}

		#endregion

		#region PrivateFields

		private MaterialProperty defaultValueProperty;

		private static readonly Regex Pattern = new Regex(
			@"^(?<attr>\[[^\]]*\]\s*)*(?<name>[A-Za-z_]\w*)\s*\(\s*""(?<display>[^""]*)""\s*,\s*(?<type>[A-Za-z0-9]+)(\s*\(\s*(?<min>[-+0-9.eE]+)\s*,\s*(?<max>[-+0-9.eE]+)\s*\))?\s*\)\s*=\s*(?<default>.*)$",
			RegexOptions.Compiled);

		#endregion

		#region PrivateMethods

		// [Toggle] -> NAME_ON, [Toggle(KEYWORD)] -> KEYWORD, [KeywordEnum(A, B c)] -> NAME_A, NAME_B_C (Unity's names).
		private static void ParseKeywordAttribute(ShaderProperty property, string text)
		{
			int open = text.IndexOf('(');
			string kind = (open < 0 ? text : text.Substring(0, open)).Trim();
			string[] arguments = open < 0 ? Array.Empty<string>() : text.Substring(open + 1).TrimEnd(')').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (kind.Equals("Toggle", StringComparison.OrdinalIgnoreCase))
			{
				property.Keywords = new[] { arguments.Length > 0 ? KeywordName(arguments[0]) : KeywordName(property.Name) + "_ON" };
			}
			else if (kind.Equals("KeywordEnum", StringComparison.OrdinalIgnoreCase) && arguments.Length > 0)
			{
				property.EnumOptions = arguments;
				property.Keywords = new string[arguments.Length];
				for (int i = 0; i < arguments.Length; i++)
				{
					property.Keywords[i] = KeywordName(property.Name) + "_" + KeywordName(arguments[i]);
				}
			}
		}

		private static string KeywordName(string text)
		{
			var name = new System.Text.StringBuilder(text.Length);
			foreach (char c in text.Trim())
			{
				name.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
			}

			return name.ToString();
		}

		private static float ParseFloat(string text)
		{
			float value;
			return float.TryParse(text.Trim().TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0f;
		}

		private static Vector4 ParseVector(string text, Vector4 fallback)
		{
			text = text.Trim().TrimStart('(').TrimEnd(')', ' ', '{', '}');
			string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				return fallback;
			}

			var result = fallback;
			if (parts.Length > 0) result.X = ParseFloat(parts[0]);
			if (parts.Length > 1) result.Y = ParseFloat(parts[1]);
			if (parts.Length > 2) result.Z = ParseFloat(parts[2]);
			if (parts.Length > 3) result.W = ParseFloat(parts[3]);
			return result;
		}

		private static string ParseTextureDefault(string text)
		{
			int start = text.IndexOf('"');
			int end = text.LastIndexOf('"');
			return start >= 0 && end > start ? text.Substring(start + 1, end - start - 1).Trim().ToLowerInvariant() : "";
		}

		#endregion
	}
}
