using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LELEngine.Editor
{
	internal struct ConsoleEntry
	{
		public string Text;
		public LogType Type;
		public string File;
		public int Line;
	}

	/// <summary>
	///     Captures everything written to the console (engine logs, shader compiler messages, script build output)
	///     for the Console panel, and still forwards it to the original output. Thread safe: script builds report
	///     from a background thread.
	/// </summary>
	internal sealed class ConsoleLog : TextWriter
	{
		#region PublicFields

		public override Encoding Encoding => Encoding.UTF8;

		/// <summary>Bumped on every new entry (panels scroll to the bottom when it changes).</summary>
		public int Version { get; private set; }

		#endregion

		#region PrivateFields

		private const int MaxEntries = 5000;
		private readonly TextWriter original;
		private readonly StringBuilder line = new StringBuilder();
		private readonly List<ConsoleEntry> entries = new List<ConsoleEntry>();
		private readonly object sync = new object();

		#endregion

		#region Constructors

		private ConsoleLog(TextWriter original)
		{
			this.original = original;
		}

		#endregion

		#region PublicMethods

		public static ConsoleLog Install()
		{
			var log = new ConsoleLog(Console.Out);
			Console.SetOut(log);
			return log;
		}

		public override void Write(char value)
		{
			original.Write(value);
			lock (sync)
			{
				if (value == '\n')
				{
					FlushLine();
				}
				else if (value != '\r')
				{
					line.Append(value);
				}
			}
		}

		public override void Write(string value)
		{
			if (value == null)
			{
				return;
			}

			original.Write(value);
			lock (sync)
			{
				foreach (char c in value)
				{
					if (c == '\n')
					{
						FlushLine();
					}
					else if (c != '\r')
					{
						line.Append(c);
					}
				}
			}
		}

		/// <summary>Adds an entry directly (build diagnostics with a source location).</summary>
		public void Add(string text, LogType type, string file = null, int lineNumber = 0)
		{
			lock (sync)
			{
				AddEntry(new ConsoleEntry { Text = text, Type = type, File = file, Line = lineNumber });
			}

			original.WriteLine(text);
		}

		public List<ConsoleEntry> Snapshot()
		{
			lock (sync)
			{
				return new List<ConsoleEntry>(entries);
			}
		}

		public void Clear()
		{
			lock (sync)
			{
				entries.Clear();
				Version++;
			}
		}

		#endregion

		#region PrivateMethods

		private void FlushLine()
		{
			string text = line.ToString();
			line.Clear();
			if (text.Length == 0)
			{
				return;
			}

			LogType type = LogType.Log;
			if (text.StartsWith("[Exception]", StringComparison.Ordinal) || text.StartsWith("[Error]", StringComparison.Ordinal)
				|| text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 && text.StartsWith("[Shader]", StringComparison.Ordinal))
			{
				type = LogType.Error;
			}
			else if (text.StartsWith("[Warning]", StringComparison.Ordinal))
			{
				type = LogType.Warning;
			}
			else if (text.StartsWith("   at ", StringComparison.Ordinal) && entries.Count > 0)
			{
				// Stack trace line: belongs to the previous entry.
				ConsoleEntry last = entries[entries.Count - 1];
				last.Text += "\n" + text;
				entries[entries.Count - 1] = last;
				Version++;
				return;
			}

			AddEntry(new ConsoleEntry { Text = text, Type = type });
		}

		private void AddEntry(ConsoleEntry entry)
		{
			entries.Add(entry);
			if (entries.Count > MaxEntries)
			{
				entries.RemoveRange(0, entries.Count - MaxEntries);
			}

			Version++;
		}

		#endregion
	}
}
