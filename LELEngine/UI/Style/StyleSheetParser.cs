using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Hand-written USS parser: rules (selector lists and declaration blocks), @import, comments, strings, numbers
	///     with units, hex and named colors, functions (url, resource, var, rgb, rgba, translate, rotate, scale, ...).
	///     Errors are collected in the sheet; a bad declaration is skipped, the rest of the sheet still loads.
	/// </summary>
	internal sealed class StyleSheetParser
	{
		#region PrivateFields

		private readonly StyleSheet sheet;
		private readonly string text;
		private int position;
		private int line = 1;
		private readonly List<StyleValue> valueBuffer = new List<StyleValue>();

		// Problems go to the sheet only, not to the log (a parse the main thread does again later reports them).
		private bool quiet;

		#endregion

		#region Constructors

		public StyleSheetParser(StyleSheet sheet, string text)
		{
			this.sheet = sheet;
			this.text = text ?? "";
		}

		#endregion

		#region PublicMethods

		public void Parse()
		{
			while (true)
			{
				SkipWhitespaceAndComments();
				if (position >= text.Length)
				{
					break;
				}

				char c = text[position];
				if (c == '@')
				{
					ParseAtRule();
					continue;
				}

				if (c == '}')
				{
					Error("Unexpected '}'");
					position++;
					continue;
				}

				ParseRule();
			}
		}

		/// <summary>Parses a declaration list ("prop: value; prop2: value") such as a UXML style attribute.</summary>
		public List<StyleDeclaration> ParseDeclarationList()
		{
			var result = new List<StyleDeclaration>();
			ParseDeclarations(result, false);
			return result;
		}

		/// <summary>Parses a selector list from text (code-built selectors).</summary>
		public static StyleSelector[] ParseSelectors(string selectorText, StyleSheet sheet = null)
		{
			var parser = new StyleSheetParser(sheet ?? new StyleSheet(), selectorText);
			return parser.ParseSelectorList(selectorText.Length);
		}

		/// <summary>Parses one value string (inline style attribute values set from UXML or code).</summary>
		public static StyleValue[] ParseValue(string valueText, string assetPath = null)
		{
			var parser = new StyleSheetParser(new StyleSheet { AssetPath = assetPath }, valueText);
			bool usesVariables;
			return parser.ParseValues(valueText.Length, out usesVariables);
		}

		/// <summary>
		///     Loads the assets the url() / resource() values of a declaration list name (a UXML style attribute read on the
		///     loading thread: instantiating the document finds them cached) and reports them; problems are not logged.
		/// </summary>
		public static void CollectResources(string declarations, string assetPath, Action<object> found)
		{
			var parser = new StyleSheetParser(new StyleSheet { AssetPath = assetPath, name = "inline" }, declarations) { quiet = true };
			foreach (StyleDeclaration declaration in parser.ParseDeclarationList())
			{
				if (declaration.values == null)
				{
					continue;
				}

				foreach (StyleValue value in declaration.values)
				{
					if (value.reference != null)
					{
						found(value.reference);
					}
				}
			}
		}

		#endregion

		#region PrivateMethods

		// ------------------------------------------------------------------ rules

		private void ParseRule()
		{
			int ruleLine = line;
			int blockStart = text.IndexOf('{', position);
			if (blockStart < 0)
			{
				Error("Expected '{'");
				position = text.Length;
				return;
			}

			StyleSelector[] selectors = ParseSelectorList(blockStart);
			position = blockStart + 1;

			var declarations = new List<StyleDeclaration>();
			ParseDeclarations(declarations, true);

			if (selectors.Length == 0)
			{
				return;
			}

			sheet.rules.Add(new StyleRule
			{
				selectors = selectors,
				declarations = declarations.ToArray(),
				order = sheet.rules.Count,
				sheet = sheet,
				line = ruleLine
			});
		}

		private void ParseAtRule()
		{
			int start = position;
			position++;
			string name = ReadIdentifier();
			SkipWhitespaceAndComments();
			if (string.Equals(name, "import", StringComparison.OrdinalIgnoreCase))
			{
				int end = text.IndexOf(';', position);
				if (end < 0)
				{
					end = text.Length;
				}

				bool usesVariables;
				StyleValue[] values = ParseValues(end, out usesVariables);
				position = Math.Min(end + 1, text.Length);
				string path = null;
				foreach (StyleValue value in values)
				{
					if (value.type == StyleValueType.ResourcePath || value.type == StyleValueType.String)
					{
						path = value.text;
						break;
					}
				}

				if (path == null)
				{
					Error("@import needs a url(\"...\") or a string");
					return;
				}

				StyleSheet imported = UIAssets.LoadStyleSheet(ResolvePath(path));
				if (imported == null)
				{
					Error("@import: style sheet not found: " + path);
				}
				else if (imported != sheet && !sheet.imports.Contains(imported))
				{
					sheet.imports.Add(imported);
				}

				return;
			}

			// Unknown at-rule: skip its block or statement.
			Error("Unsupported at-rule @" + name);
			int depth = 0;
			while (position < text.Length)
			{
				char c = text[position++];
				if (c == '{') depth++;
				else if (c == '}')
				{
					if (--depth <= 0) break;
				}
				else if (c == ';' && depth == 0) break;
				else if (c == '\n') line++;
			}
		}

		// ------------------------------------------------------------------ selectors

		private StyleSelector[] ParseSelectorList(int end)
		{
			var result = new List<StyleSelector>();
			var parts = new List<SimpleSelector>();
			var combinators = new List<SelectorCombinator>();
			SimpleSelector current = null;
			bool pendingDescendant = false;
			bool pendingChild = false;

			void FinishSimple()
			{
				if (current == null)
				{
					return;
				}

				if (parts.Count > 0)
				{
					combinators.Add(pendingChild ? SelectorCombinator.Child : SelectorCombinator.Descendant);
				}

				parts.Add(current);
				current = null;
				pendingDescendant = false;
				pendingChild = false;
			}

			void FinishComplex()
			{
				FinishSimple();
				if (parts.Count > 0)
				{
					var selector = new StyleSelector { parts = parts.ToArray(), combinators = combinators.ToArray() };
					selector.specificity = ComputeSpecificity(selector);
					result.Add(selector);
				}

				parts.Clear();
				combinators.Clear();
			}

			while (position < end)
			{
				char c = text[position];
				if (c == '/' && position + 1 < end && text[position + 1] == '*')
				{
					SkipComment();
					continue;
				}

				if (char.IsWhiteSpace(c))
				{
					if (c == '\n') line++;
					if (current != null)
					{
						FinishSimple();
						pendingDescendant = true;
					}

					position++;
					continue;
				}

				if (c == ',')
				{
					FinishComplex();
					position++;
					continue;
				}

				if (c == '>')
				{
					FinishSimple();
					pendingChild = true;
					position++;
					continue;
				}

				if (c == '*')
				{
					current ??= new SimpleSelector();
					position++;
					continue;
				}

				if (c == '.')
				{
					position++;
					string name = ReadIdentifier();
					if (name.Length == 0)
					{
						Error("Expected class name after '.'");
						continue;
					}

					current ??= new SimpleSelector();
					string[] classes = current.classes;
					Array.Resize(ref classes, classes.Length + 1);
					classes[classes.Length - 1] = name;
					current.classes = classes;
					continue;
				}

				if (c == '#')
				{
					position++;
					string name = ReadIdentifier();
					if (name.Length == 0)
					{
						Error("Expected name after '#'");
						continue;
					}

					current ??= new SimpleSelector();
					current.name = name;
					continue;
				}

				if (c == ':')
				{
					position++;
					string pseudo = ReadIdentifier();
					current ??= new SimpleSelector();
					ApplyPseudoClass(current, pseudo);
					continue;
				}

				if (IsIdentifierStart(c))
				{
					string typeName = ReadIdentifier();
					current ??= new SimpleSelector();
					current.type = typeName;
					continue;
				}

				Error("Unexpected character '" + c + "' in selector");
				position++;
			}

			FinishComplex();
			return result.ToArray();
		}

		private void ApplyPseudoClass(SimpleSelector selector, string pseudo)
		{
			switch (pseudo.ToLowerInvariant())
			{
				case "hover": selector.pseudoMask |= PseudoStates.Hover; break;
				case "active": selector.pseudoMask |= PseudoStates.Active; break;
				case "focus": selector.pseudoMask |= PseudoStates.Focus; break;
				case "disabled": selector.pseudoMask |= PseudoStates.Disabled; break;
				case "enabled": selector.negatedPseudoMask |= PseudoStates.Disabled; break;
				case "checked": selector.pseudoMask |= PseudoStates.Checked; break;
				case "root": selector.pseudoMask |= PseudoStates.Root; break;
				case "inactive":
					// Unity: an element selected while its panel has no focus. Not tracked here.
					selector.neverMatches = true;
					break;
				default:
					Error("Unknown pseudo-class :" + pseudo);
					selector.neverMatches = true;
					break;
			}
		}

		private static int ComputeSpecificity(StyleSelector selector)
		{
			int ids = 0, classes = 0, types = 0;
			foreach (SimpleSelector part in selector.parts)
			{
				if (part.name != null) ids++;
				classes += part.classes.Length;
				classes += CountBits((int)part.pseudoMask) + CountBits((int)part.negatedPseudoMask);
				if (part.type != null) types++;
			}

			return ids * 10000 + classes * 100 + types;
		}

		private static int CountBits(int value)
		{
			int count = 0;
			while (value != 0)
			{
				count += value & 1;
				value >>= 1;
			}

			return count;
		}

		// ------------------------------------------------------------------ declarations

		private void ParseDeclarations(List<StyleDeclaration> declarations, bool untilBrace)
		{
			while (true)
			{
				SkipWhitespaceAndComments();
				if (position >= text.Length)
				{
					if (untilBrace)
					{
						Error("Expected '}'");
					}

					return;
				}

				char c = text[position];
				if (c == '}')
				{
					position++;
					return;
				}

				if (c == ';')
				{
					position++;
					continue;
				}

				int declarationLine = line;
				string name = ReadIdentifier();
				if (name.Length == 0)
				{
					Error("Expected a property name");
					SkipToDeclarationEnd();
					continue;
				}

				SkipWhitespaceAndComments();
				if (position >= text.Length || text[position] != ':')
				{
					Error("Expected ':' after '" + name + "'");
					SkipToDeclarationEnd();
					continue;
				}

				position++;
				int end = FindDeclarationEnd();
				bool usesVariables;
				StyleValue[] values = ParseValues(end, out usesVariables);
				position = end;
				if (position < text.Length && text[position] == ';')
				{
					position++;
				}

				StylePropertyId id = StylePropertyRegistry.FromName(name);
				if (id == StylePropertyId.Unknown)
				{
					Error("Unknown property '" + name + "'", declarationLine);
					continue;
				}

				if (values.Length == 0)
				{
					Error("Property '" + name + "' has no value", declarationLine);
					continue;
				}

				declarations.Add(new StyleDeclaration { id = id, name = name, values = values, usesVariables = usesVariables, line = declarationLine });
			}
		}

		/// <summary>Index of the ';' or '}' ending the current declaration (strings and parentheses are skipped).</summary>
		private int FindDeclarationEnd()
		{
			int depth = 0;
			int i = position;
			while (i < text.Length)
			{
				char c = text[i];
				if (c == '"' || c == '\'')
				{
					i = SkipString(i);
					continue;
				}

				if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
				{
					int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
					i = close < 0 ? text.Length : close + 2;
					continue;
				}

				if (c == '(') depth++;
				else if (c == ')') depth = Math.Max(0, depth - 1);
				else if ((c == ';' || c == '}') && depth == 0) return i;
				i++;
			}

			return text.Length;
		}

		private int SkipString(int i)
		{
			char quote = text[i++];
			while (i < text.Length && text[i] != quote)
			{
				if (text[i] == '\\') i++;
				i++;
			}

			return Math.Min(i + 1, text.Length);
		}

		private void SkipToDeclarationEnd()
		{
			position = FindDeclarationEnd();
			if (position < text.Length && text[position] == ';')
			{
				position++;
			}
		}

		// ------------------------------------------------------------------ values

		private StyleValue[] ParseValues(int end, out bool usesVariables)
		{
			valueBuffer.Clear();
			usesVariables = false;
			ParseValueTokens(end, valueBuffer, ref usesVariables);
			return valueBuffer.ToArray();
		}

		private void ParseValueTokens(int end, List<StyleValue> output, ref bool usesVariables)
		{
			while (true)
			{
				SkipWhitespaceAndComments(end);
				if (position >= end)
				{
					return;
				}

				char c = text[position];
				if (c == ',')
				{
					output.Add(StyleValue.Comma());
					position++;
					continue;
				}

				if (c == ')')
				{
					// Closes the enclosing function (the caller consumes it).
					return;
				}

				if (c == '"' || c == '\'')
				{
					output.Add(StyleValue.String(ReadString()));
					continue;
				}

				if (c == '#')
				{
					position++;
					string hex = ReadWhile(ch => Uri.IsHexDigit(ch));
					Color4 color;
					if (StyleColorUtility.TryParseHex(hex, out color))
					{
						output.Add(StyleValue.FromColor(color));
					}
					else
					{
						Error("Invalid color #" + hex);
					}

					continue;
				}

				if (c == '!')
				{
					position++;
					string flag = ReadIdentifier();
					if (!string.Equals(flag, "important", StringComparison.OrdinalIgnoreCase))
					{
						Error("Unexpected '!" + flag + "'");
					}

					continue;
				}

				if (char.IsDigit(c) || c == '.' || ((c == '-' || c == '+') && position + 1 < end && (char.IsDigit(text[position + 1]) || text[position + 1] == '.')))
				{
					output.Add(ReadNumber());
					continue;
				}

				if (IsIdentifierStart(c))
				{
					string identifier = ReadIdentifier();
					if (position < end && text[position] == '(')
					{
						position++;
						ParseFunction(identifier, end, output, ref usesVariables);
						continue;
					}

					output.Add(IdentifierValue(identifier));
					continue;
				}

				Error("Unexpected character '" + c + "' in value");
				position++;
			}
		}

		private static StyleValue IdentifierValue(string identifier)
		{
			switch (identifier.ToLowerInvariant())
			{
				case "auto": return StyleValue.Keyword(StyleKeyword.Auto);
				case "none": return StyleValue.Keyword(StyleKeyword.None);
				case "initial": return StyleValue.Keyword(StyleKeyword.Initial);
				case "inherit": return StyleValue.Keyword(StyleKeyword.Inherit);
			}

			Color4 color;
			if (StyleColorUtility.TryParse(identifier, out color))
			{
				return StyleValue.FromColor(color);
			}

			return StyleValue.Identifier(identifier);
		}

		private void ParseFunction(string name, int end, List<StyleValue> output, ref bool usesVariables)
		{
			string lower = name.ToLowerInvariant();
			if (lower == "url" || lower == "resource")
			{
				string path = ReadFunctionArgumentText(end).Trim();
				if ((path.StartsWith("\"") && path.EndsWith("\"")) || (path.StartsWith("'") && path.EndsWith("'")))
				{
					path = path.Length >= 2 ? path.Substring(1, path.Length - 2) : "";
				}

				string resolved = lower == "resource" ? ResolveResource(path) : ResolvePath(path);
				output.Add(StyleValue.Resource(resolved ?? path, resolved != null ? UIAssets.LoadResource(resolved) : null));
				return;
			}

			var arguments = new List<StyleValue>();
			ParseValueTokens(end, arguments, ref usesVariables);
			if (position < end && text[position] == ')')
			{
				position++;
			}
			else
			{
				Error("Expected ')' closing " + name + "(");
			}

			if (lower == "rgb" || lower == "rgba")
			{
				output.Add(ParseRgb(arguments, lower == "rgba"));
				return;
			}

			if (lower == "var")
			{
				usesVariables = true;
			}

			output.Add(StyleValue.Function(lower));
			output.AddRange(arguments);
			output.Add(StyleValue.FunctionEnd());
		}

		private StyleValue ParseRgb(List<StyleValue> arguments, bool alpha)
		{
			var numbers = new List<float>();
			foreach (StyleValue argument in arguments)
			{
				if (argument.type == StyleValueType.Float)
				{
					numbers.Add(argument.number);
				}
				else if (argument.type == StyleValueType.Dimension && argument.unit == StyleUnit.Percent)
				{
					numbers.Add(argument.number * 2.55f);
				}
			}

			if (numbers.Count < 3)
			{
				Error("rgb() needs three components");
				return StyleValue.FromColor(new Color4(0f, 0f, 0f, 1f));
			}

			float a = numbers.Count > 3 ? numbers[3] : 1f;
			return StyleValue.FromColor(new Color4(numbers[0] / 255f, numbers[1] / 255f, numbers[2] / 255f, a));
		}

		/// <summary>Raw text up to the closing parenthesis (url arguments may be unquoted).</summary>
		private string ReadFunctionArgumentText(int end)
		{
			int start = position;
			int depth = 0;
			while (position < end)
			{
				char c = text[position];
				if (c == '"' || c == '\'')
				{
					position = SkipString(position);
					continue;
				}

				if (c == '(') depth++;
				if (c == ')')
				{
					if (depth == 0)
					{
						string result = text.Substring(start, position - start);
						position++;
						return result;
					}

					depth--;
				}

				position++;
			}

			Error("Expected ')'");
			return text.Substring(start, position - start);
		}

		private StyleValue ReadNumber()
		{
			int start = position;
			if (text[position] == '-' || text[position] == '+')
			{
				position++;
			}

			while (position < text.Length && (char.IsDigit(text[position]) || text[position] == '.'))
			{
				position++;
			}

			if (position < text.Length && (text[position] == 'e' || text[position] == 'E') && position + 1 < text.Length && (char.IsDigit(text[position + 1]) || text[position + 1] == '-' || text[position + 1] == '+'))
			{
				position += 2;
				while (position < text.Length && char.IsDigit(text[position]))
				{
					position++;
				}
			}

			float number;
			if (!float.TryParse(text.Substring(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
			{
				Error("Invalid number '" + text.Substring(start, position - start) + "'");
			}

			if (position < text.Length && text[position] == '%')
			{
				position++;
				return StyleValue.Dimension(number, StyleUnit.Percent);
			}

			string unit = ReadWhile(char.IsLetter);
			switch (unit.ToLowerInvariant())
			{
				case "": return StyleValue.Float(number);
				case "px": return StyleValue.Dimension(number, StyleUnit.Pixel);
				case "s": return StyleValue.Dimension(number, StyleUnit.Second);
				case "ms": return StyleValue.Dimension(number, StyleUnit.Millisecond);
				case "deg": return StyleValue.Dimension(number, StyleUnit.Degree);
				case "rad": return StyleValue.Dimension(number, StyleUnit.Radian);
				case "grad": return StyleValue.Dimension(number, StyleUnit.Gradian);
				case "turn": return StyleValue.Dimension(number, StyleUnit.Turn);
				default:
					Error("Unknown unit '" + unit + "'");
					return StyleValue.Float(number);
			}
		}

		private string ReadString()
		{
			char quote = text[position++];
			var builder = new System.Text.StringBuilder();
			while (position < text.Length && text[position] != quote)
			{
				char c = text[position++];
				if (c == '\\' && position < text.Length)
				{
					c = text[position++];
				}

				if (c == '\n') line++;
				builder.Append(c);
			}

			position++;
			return builder.ToString();
		}

		// ------------------------------------------------------------------ paths

		/// <summary>A url() path: relative to the sheet's folder, or to the asset root when it starts with '/'.</summary>
		private string ResolvePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return null;
			}

			path = path.Replace('\\', '/');
			if (path.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
			{
				path = path.Substring("project:/".Length);
				if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
				{
					path = path.Substring("Assets/".Length);
				}
			}
			else if (path.StartsWith("/"))
			{
				path = path.TrimStart('/');
			}
			else if (sheet.AssetPath != null)
			{
				string folder = System.IO.Path.GetDirectoryName(sheet.AssetPath)?.Replace('\\', '/') ?? "";
				path = folder.Length > 0 ? folder + "/" + path : path;
			}

			return NormalizePath(path);
		}

		/// <summary>A resource() path: relative to the asset root (any extension).</summary>
		private static string ResolveResource(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return null;
			}

			string normalized = NormalizePath(path.Replace('\\', '/').TrimStart('/'));
			string resolved = AssetDatabase.Resolve(normalized, null);
			if (resolved != null)
			{
				return resolved;
			}

			foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".ttf", ".otf", ".uss" })
			{
				resolved = AssetDatabase.Resolve(normalized + extension, null);
				if (resolved != null)
				{
					return resolved;
				}
			}

			return normalized;
		}

		private static string NormalizePath(string path)
		{
			var parts = new List<string>();
			foreach (string part in path.Split('/'))
			{
				if (part.Length == 0 || part == ".")
				{
					continue;
				}

				if (part == "..")
				{
					if (parts.Count > 0)
					{
						parts.RemoveAt(parts.Count - 1);
					}

					continue;
				}

				parts.Add(part);
			}

			return string.Join("/", parts);
		}

		// ------------------------------------------------------------------ lexing helpers

		private static bool IsIdentifierStart(char c)
		{
			return char.IsLetter(c) || c == '_' || c == '-';
		}

		private static bool IsIdentifierChar(char c)
		{
			return char.IsLetterOrDigit(c) || c == '_' || c == '-';
		}

		private string ReadIdentifier()
		{
			return ReadWhile(IsIdentifierChar);
		}

		private string ReadWhile(Func<char, bool> predicate)
		{
			int start = position;
			while (position < text.Length && predicate(text[position]))
			{
				position++;
			}

			return text.Substring(start, position - start);
		}

		private void SkipWhitespaceAndComments()
		{
			SkipWhitespaceAndComments(text.Length);
		}

		private void SkipWhitespaceAndComments(int end)
		{
			while (position < end)
			{
				char c = text[position];
				if (c == '\n')
				{
					line++;
					position++;
				}
				else if (char.IsWhiteSpace(c))
				{
					position++;
				}
				else if (c == '/' && position + 1 < end && text[position + 1] == '*')
				{
					SkipComment();
				}
				else if (c == '/' && position + 1 < end && text[position + 1] == '/')
				{
					// Line comments are not CSS, but common in hand-written sheets.
					while (position < end && text[position] != '\n')
					{
						position++;
					}
				}
				else
				{
					return;
				}
			}
		}

		private void SkipComment()
		{
			int close = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
			int stop = close < 0 ? text.Length : close + 2;
			for (int i = position; i < stop; i++)
			{
				if (text[i] == '\n') line++;
			}

			position = stop;
		}

		private void Error(string message)
		{
			Error(message, line);
		}

		private void Error(string message, int atLine)
		{
			sheet.errors.Add(sheet.name + ":" + atLine + ": " + message);
			if (!quiet)
			{
				Debug.LogWarning("[USS] " + sheet.name + ":" + atLine + ": " + message);
			}
		}

		#endregion
	}
}
