using System;
using System.Collections.Generic;

namespace LELEngine.Shaders
{
	/// <summary>
	///     The #if / #ifdef / #ifndef / #elif / #else / #endif lines of a .shader file's Properties block, evaluated over a
	///     variant's keywords (its render state lines can differ per variant; GLSL stages leave this to the GLSL
	///     compiler). Conditions: a keyword (true when defined), defined(KEYWORD) or defined KEYWORD, numbers, !, &amp;&amp;,
	///     || and parentheses.
	/// </summary>
	internal static class ShaderConditionals
	{
		#region PrivateFields

		private struct Frame
		{
			public bool ParentActive;
			public bool Taken;
			public bool Active;
		}

		#endregion

		#region PublicMethods

		/// <summary>The lines left once the conditional blocks are evaluated with <paramref name="defines" /> (directive lines removed).</summary>
		public static List<string> Filter(IEnumerable<string> lines, ICollection<string> defines, string source)
		{
			var result = new List<string>();
			var stack = new Stack<Frame>();
			bool active = true;
			foreach (string line in lines)
			{
				string trimmed = line.Trim();
				if (!trimmed.StartsWith("#", StringComparison.Ordinal))
				{
					if (active)
					{
						result.Add(line);
					}

					continue;
				}

				string directive = Word(trimmed, 1, out int end);
				string argument = trimmed.Substring(end).Trim();
				switch (directive)
				{
					case "if":
					case "ifdef":
					case "ifndef":
					{
						bool value = directive == "ifdef" ? defines.Contains(argument) : directive == "ifndef" ? !defines.Contains(argument) : Evaluate(argument, defines, source);
						stack.Push(new Frame { ParentActive = active, Taken = value, Active = active && value });
						active = active && value;
						break;
					}
					case "elif":
					case "else":
					{
						if (stack.Count == 0)
						{
							Console.WriteLine("[Shader] #" + directive + " without #if in the Properties block of " + source);
							break;
						}

						Frame frame = stack.Pop();
						bool value = !frame.Taken && (directive == "else" || Evaluate(argument, defines, source));
						frame.Taken |= value;
						frame.Active = frame.ParentActive && value;
						stack.Push(frame);
						active = frame.Active;
						break;
					}
					case "endif":
						if (stack.Count == 0)
						{
							Console.WriteLine("[Shader] #endif without #if in the Properties block of " + source);
							break;
						}

						active = stack.Pop().ParentActive;
						break;
					default:
						// Not a conditional (a comment such as "#comment" is not GLSL here either): kept like any line.
						if (active)
						{
							result.Add(line);
						}

						break;
				}
			}

			if (stack.Count > 0)
			{
				Console.WriteLine("[Shader] #if without #endif in the Properties block of " + source);
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private static string Word(string text, int start, out int end)
		{
			end = start;
			while (end < text.Length && char.IsLetter(text[end]))
			{
				end++;
			}

			return text.Substring(start, end - start);
		}

		private static bool Evaluate(string expression, ICollection<string> defines, string source)
		{
			int position = 0;
			try
			{
				bool value = ParseOr(expression, ref position, defines);
				SkipSpaces(expression, ref position);
				if (position != expression.Length)
				{
					throw new FormatException("unexpected '" + expression.Substring(position) + "'");
				}

				return value;
			}
			catch (FormatException e)
			{
				Console.WriteLine("[Shader] Cannot evaluate '#if " + expression + "' in the Properties block of " + source + ": " + e.Message);
				return false;
			}
		}

		private static bool ParseOr(string text, ref int position, ICollection<string> defines)
		{
			bool value = ParseAnd(text, ref position, defines);
			while (Accept(text, ref position, "||"))
			{
				value |= ParseAnd(text, ref position, defines);
			}

			return value;
		}

		private static bool ParseAnd(string text, ref int position, ICollection<string> defines)
		{
			bool value = ParseUnary(text, ref position, defines);
			while (Accept(text, ref position, "&&"))
			{
				value &= ParseUnary(text, ref position, defines);
			}

			return value;
		}

		private static bool ParseUnary(string text, ref int position, ICollection<string> defines)
		{
			if (Accept(text, ref position, "!"))
			{
				return !ParseUnary(text, ref position, defines);
			}

			if (Accept(text, ref position, "("))
			{
				bool value = ParseOr(text, ref position, defines);
				if (!Accept(text, ref position, ")"))
				{
					throw new FormatException("missing ')'");
				}

				return value;
			}

			string name = Identifier(text, ref position);
			if (name == "defined")
			{
				bool parenthesized = Accept(text, ref position, "(");
				string keyword = Identifier(text, ref position);
				if (parenthesized && !Accept(text, ref position, ")"))
				{
					throw new FormatException("missing ')' after defined(" + keyword);
				}

				return defines.Contains(keyword);
			}

			if (name.Length > 0 && char.IsDigit(name[0]))
			{
				return int.TryParse(name, out int number) ? number != 0 : throw new FormatException("bad number '" + name + "'");
			}

			return defines.Contains(name);
		}

		private static string Identifier(string text, ref int position)
		{
			SkipSpaces(text, ref position);
			int start = position;
			while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
			{
				position++;
			}

			if (position == start)
			{
				throw new FormatException(position < text.Length ? "unexpected '" + text[position] + "'" : "the condition ends early");
			}

			return text.Substring(start, position - start);
		}

		private static bool Accept(string text, ref int position, string token)
		{
			SkipSpaces(text, ref position);
			if (string.CompareOrdinal(text, position, token, 0, token.Length) != 0)
			{
				return false;
			}

			position += token.Length;
			return true;
		}

		private static void SkipSpaces(string text, ref int position)
		{
			while (position < text.Length && char.IsWhiteSpace(text[position]))
			{
				position++;
			}
		}

		#endregion
	}
}
