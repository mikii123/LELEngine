using System;
using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>
	///     A line-by-line C# highlighter for the console's source view: keywords, flow keywords, strings (regular,
	///     verbatim, interpolated, raw), characters, numbers, comments, preprocessor lines, and types and methods by
	///     their shape (Name. / Name&lt;, Name( ). Multi-line comments and strings carry over as the line's end
	///     <see cref="State" />.
	/// </summary>
	internal static class CSharpLexer
	{
		#region PublicFields

		public enum State : byte
		{
			Code,
			BlockComment,
			VerbatimString,
			RawString
		}

		public enum TokenKind : byte
		{
			Text,
			Keyword,
			Control,
			Type,
			Method,
			String,
			Number,
			Comment,
			Preprocessor
		}

		/// <summary>A highlighted span of a line (what no token covers is plain text).</summary>
		public struct Token
		{
			public int Start;
			public int Length;
			public TokenKind Kind;
		}

		#endregion

		#region PrivateFields

		private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
		{
			"abstract", "as", "base", "bool", "byte", "char", "checked", "class", "const", "decimal", "delegate", "double", "enum", "event",
			"explicit", "extern", "false", "fixed", "float", "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
			"namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
			"ref", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "this", "true", "typeof", "uint",
			"ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "var", "dynamic", "async", "get", "set",
			"init", "value", "partial", "where", "record", "nameof", "global", "add", "remove", "nint", "nuint", "not", "and", "or",
			"with", "required", "file", "scoped", "managed", "unmanaged", "default"
		};

		private static readonly HashSet<string> ControlKeywords = new HashSet<string>(StringComparer.Ordinal)
		{
			"if", "else", "switch", "case", "for", "foreach", "while", "do", "break", "continue", "return", "goto", "throw", "try",
			"catch", "finally", "yield", "await", "when"
		};

		/// <summary>Words after which a name is a type (declarations, new, casts by is / as).</summary>
		private static readonly HashSet<string> TypeIntroducers = new HashSet<string>(StringComparer.Ordinal)
		{
			"class", "struct", "interface", "enum", "record", "new", "typeof", "is", "as", "catch"
		};

		#endregion

		#region PublicMethods

		/// <summary>The state the next line starts in.</summary>
		public static State EndState(string line, State start)
		{
			return Tokenize(line, start, null);
		}

		/// <summary>Adds the line's highlighted spans to <paramref name="tokens" /> (when given); returns the state the next line starts in.</summary>
		public static State Tokenize(string line, State state, List<Token> tokens)
		{
			int n = line.Length;
			int i = 0;
			switch (state)
			{
				case State.BlockComment:
				{
					int end = line.IndexOf("*/", StringComparison.Ordinal);
					if (end < 0)
					{
						Add(tokens, 0, n, TokenKind.Comment);
						return State.BlockComment;
					}

					Add(tokens, 0, end + 2, TokenKind.Comment);
					i = end + 2;
					break;
				}
				case State.VerbatimString:
				{
					int end = VerbatimEnd(line, 0);
					if (end < 0)
					{
						Add(tokens, 0, n, TokenKind.String);
						return State.VerbatimString;
					}

					Add(tokens, 0, end + 1, TokenKind.String);
					i = end + 1;
					break;
				}
				case State.RawString:
				{
					int end = line.IndexOf("\"\"\"", StringComparison.Ordinal);
					if (end < 0)
					{
						Add(tokens, 0, n, TokenKind.String);
						return State.RawString;
					}

					Add(tokens, 0, end + 3, TokenKind.String);
					i = end + 3;
					break;
				}
				default:
				{
					int first = 0;
					while (first < n && char.IsWhiteSpace(line[first]))
					{
						first++;
					}

					if (first < n && line[first] == '#')
					{
						Add(tokens, first, n - first, TokenKind.Preprocessor);
						return State.Code;
					}

					break;
				}
			}

			string previousWord = null;
			char previousChar = '\0';
			while (i < n)
			{
				char c = line[i];
				if (char.IsWhiteSpace(c))
				{
					i++;
					continue;
				}

				if (c == '/' && i + 1 < n && line[i + 1] == '/')
				{
					Add(tokens, i, n - i, TokenKind.Comment);
					return State.Code;
				}

				if (c == '/' && i + 1 < n && line[i + 1] == '*')
				{
					int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
					if (end < 0)
					{
						Add(tokens, i, n - i, TokenKind.Comment);
						return State.BlockComment;
					}

					Add(tokens, i, end + 2 - i, TokenKind.Comment);
					i = end + 2;
					previousChar = '/';
					continue;
				}

				// Strings: $ and @ prefixes, raw """ strings, verbatim @"" strings, regular and character literals.
				int quote = i;
				bool verbatim = false;
				while (quote < n && quote - i < 3 && (line[quote] == '$' || line[quote] == '@'))
				{
					verbatim |= line[quote] == '@';
					quote++;
				}

				if (quote < n && line[quote] == '"')
				{
					if (quote + 2 < n && line[quote + 1] == '"' && line[quote + 2] == '"')
					{
						int end = line.IndexOf("\"\"\"", quote + 3, StringComparison.Ordinal);
						if (end < 0)
						{
							Add(tokens, i, n - i, TokenKind.String);
							return State.RawString;
						}

						Add(tokens, i, end + 3 - i, TokenKind.String);
						i = end + 3;
					}
					else if (verbatim)
					{
						int end = VerbatimEnd(line, quote + 1);
						if (end < 0)
						{
							Add(tokens, i, n - i, TokenKind.String);
							return State.VerbatimString;
						}

						Add(tokens, i, end + 1 - i, TokenKind.String);
						i = end + 1;
					}
					else
					{
						int end = QuotedEnd(line, quote + 1, '"');
						Add(tokens, i, end - i, TokenKind.String);
						i = end;
					}

					previousChar = '"';
					previousWord = null;
					continue;
				}

				if (c == '\'')
				{
					int end = QuotedEnd(line, i + 1, '\'');
					Add(tokens, i, end - i, TokenKind.String);
					i = end;
					previousChar = '\'';
					continue;
				}

				if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(line[i + 1]) && !IsWordChar(previousChar)))
				{
					int start = i;
					while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || (line[i] == '.' && i + 1 < n && char.IsDigit(line[i + 1]))))
					{
						i++;
					}

					Add(tokens, start, i - start, TokenKind.Number);
					previousChar = '0';
					continue;
				}

				if (char.IsLetter(c) || c == '_' || (c == '@' && i + 1 < n && (char.IsLetter(line[i + 1]) || line[i + 1] == '_')))
				{
					int start = i;
					i++;
					while (i < n && IsWordChar(line[i]))
					{
						i++;
					}

					string word = line.Substring(start, i - start);
					TokenKind kind = Classify(line, word, i, previousWord, previousChar);
					if (kind != TokenKind.Text)
					{
						Add(tokens, start, i - start, kind);
					}

					previousWord = word;
					previousChar = 'a';
					continue;
				}

				previousChar = c;
				if (c != '?' && c != '[' && c != ']')
				{
					previousWord = c == '.' ? previousWord : null;
				}

				i++;
			}

			return State.Code;
		}

		#endregion

		#region PrivateMethods

		private static TokenKind Classify(string line, string word, int end, string previousWord, char previousChar)
		{
			if (ControlKeywords.Contains(word))
			{
				return TokenKind.Control;
			}

			if (Keywords.Contains(word))
			{
				return TokenKind.Keyword;
			}

			char next = NextChar(line, end);
			bool member = previousChar == '.';
			if (next == '(')
			{
				return previousWord == "new" && !member ? TokenKind.Type : TokenKind.Method;
			}

			if (!member && previousWord != null && TypeIntroducers.Contains(previousWord))
			{
				return TokenKind.Type;
			}

			// Type-shaped: Name.Member, Name<T>, Name name (a declaration), Name[] / Name? before a name.
			if (!member && char.IsUpper(word[0]) && (next == '.' || next == '<' || char.IsLetter(next) || next == '_' || next == '[' || next == '?'))
			{
				return TokenKind.Type;
			}

			return TokenKind.Text;
		}

		private static char NextChar(string line, int from)
		{
			while (from < line.Length && char.IsWhiteSpace(line[from]))
			{
				from++;
			}

			return from < line.Length ? line[from] : '\0';
		}

		private static bool IsWordChar(char c)
		{
			return char.IsLetterOrDigit(c) || c == '_';
		}

		/// <summary>Index of the quote closing a verbatim string ("" is an escaped quote), or -1 when it goes on.</summary>
		private static int VerbatimEnd(string line, int from)
		{
			for (int j = from; j < line.Length; j++)
			{
				if (line[j] != '"')
				{
					continue;
				}

				if (j + 1 < line.Length && line[j + 1] == '"')
				{
					j++;
					continue;
				}

				return j;
			}

			return -1;
		}

		/// <summary>The index after the closing quote of a regular string or character (backslash escapes), or the line's end.</summary>
		private static int QuotedEnd(string line, int from, char quote)
		{
			for (int j = from; j < line.Length; j++)
			{
				if (line[j] == '\\')
				{
					j++;
					continue;
				}

				if (line[j] == quote)
				{
					return j + 1;
				}
			}

			return line.Length;
		}

		private static void Add(List<Token> tokens, int start, int length, TokenKind kind)
		{
			if (tokens != null && length > 0)
			{
				tokens.Add(new Token { Start = start, Length = length, Kind = kind });
			}
		}

		#endregion
	}
}
