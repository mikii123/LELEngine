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

		public bool IsTexture => Type == ShaderPropertyType.Texture2D || Type == ShaderPropertyType.Cubemap || Type == ShaderPropertyType.Texture3D;

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
				string text = attribute.Value.Trim('[', ']', ' ');
				if (text.Equals("HDR", StringComparison.OrdinalIgnoreCase)) property.Hdr = true;
				else if (text.Equals("HideInInspector", StringComparison.OrdinalIgnoreCase)) property.HideInInspector = true;
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

			return property;
		}

		#endregion

		#region PrivateFields

		private static readonly Regex Pattern = new Regex(
			@"^(?<attr>\[[^\]]*\]\s*)*(?<name>[A-Za-z_]\w*)\s*\(\s*""(?<display>[^""]*)""\s*,\s*(?<type>[A-Za-z0-9]+)(\s*\(\s*(?<min>[-+0-9.eE]+)\s*,\s*(?<max>[-+0-9.eE]+)\s*\))?\s*\)\s*=\s*(?<default>.*)$",
			RegexOptions.Compiled);

		#endregion

		#region PrivateMethods

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
