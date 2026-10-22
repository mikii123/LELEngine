using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LELEngine.Editor
{
	/// <summary>A source file as the console's source view shows it: lines (tabs expanded) and the C# lexer state each starts in.</summary>
	internal sealed class SourceFile
	{
		public string Path;
		public string[] Lines;

		/// <summary>What the line before left open (a block comment, a verbatim or raw string).</summary>
		public CSharpLexer.State[] LineStates;

		/// <summary>The longest line, in characters (horizontal scrolling).</summary>
		public int Width;

		/// <summary>Why the file is not shown (too large, unreadable), or null.</summary>
		public string Error;

		public DateTime Stamp;
	}

	/// <summary>
	///     Source files stack traces point at, read once and again when they change (checked at most every second);
	///     whether a file exists is remembered for a few seconds (stack traces name many files).
	/// </summary>
	internal static class SourceFiles
	{
		#region PrivateFields

		private const long MaxBytes = 4 * 1024 * 1024;
		private const int TabSize = 4;
		private static readonly Dictionary<string, (SourceFile file, long checkedAt)> files = new Dictionary<string, (SourceFile, long)>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, (bool exists, long checkedAt)> existing = new Dictionary<string, (bool, long)>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		public static bool Exists(string path)
		{
			lock (existing)
			{
				long now = Stopwatch.GetTimestamp();
				if (existing.TryGetValue(path, out var cached) && now - cached.checkedAt < Stopwatch.Frequency * 5)
				{
					return cached.exists;
				}

				bool exists = File.Exists(path);
				existing[path] = (exists, now);
				return exists;
			}
		}

		public static SourceFile Get(string path)
		{
			long now = Stopwatch.GetTimestamp();
			if (files.TryGetValue(path, out var cached) && now - cached.checkedAt < Stopwatch.Frequency)
			{
				return cached.file;
			}

			DateTime stamp = Stamp(path);
			SourceFile file = cached.file != null && cached.file.Stamp == stamp ? cached.file : Read(path, stamp);
			files[path] = (file, now);
			return file;
		}

		#endregion

		#region PrivateMethods

		private static DateTime Stamp(string path)
		{
			try
			{
				return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
			}
			catch (IOException)
			{
				return DateTime.MinValue;
			}
		}

		private static SourceFile Read(string path, DateTime stamp)
		{
			var file = new SourceFile { Path = path, Stamp = stamp, Lines = Array.Empty<string>(), LineStates = Array.Empty<CSharpLexer.State>() };
			try
			{
				var info = new FileInfo(path);
				if (!info.Exists)
				{
					file.Error = "The file is not here: " + path;
					return file;
				}

				if (info.Length > MaxBytes)
				{
					file.Error = $"The file is too large to show ({info.Length / 1024} KB)";
					return file;
				}

				string[] lines = File.ReadAllLines(path);
				file.Lines = new string[lines.Length];
				file.LineStates = new CSharpLexer.State[lines.Length];
				CSharpLexer.State state = CSharpLexer.State.Code;
				for (int i = 0; i < lines.Length; i++)
				{
					string line = ExpandTabs(lines[i]);
					file.Lines[i] = line;
					file.LineStates[i] = state;
					file.Width = Math.Max(file.Width, line.Length);
					state = CSharpLexer.EndState(line, state);
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				file.Error = "Cannot read " + path + ": " + e.Message;
			}

			return file;
		}

		private static string ExpandTabs(string line)
		{
			if (line.IndexOf('\t') < 0)
			{
				return line;
			}

			var builder = new StringBuilder(line.Length + 16);
			foreach (char c in line)
			{
				if (c == '\t')
				{
					builder.Append(' ', TabSize - builder.Length % TabSize);
				}
				else
				{
					builder.Append(c);
				}
			}

			return builder.ToString();
		}

		#endregion
	}
}
