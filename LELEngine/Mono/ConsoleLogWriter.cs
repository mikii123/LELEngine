using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LELEngine
{
	/// <summary>
	///     The console as a log, written as <see cref="Debug" /> writes its messages: each message (the lines one write
	///     completes: a WriteLine, an exception written whole) that is a warning or an error (<see cref="TypeOf" />,
	///     <see cref="Debug.WritesStackTrace" />) is followed by the call stack of the code that wrote it, as its type records
	///     it (<see cref="Debug.GetStackTraceLogType" />); plain messages are written alone. A message that brings its own stack ("   at ..." lines)
	///     is written as it is, and so are empty lines. Hosts put it over the console (<see cref="Install" />): the engine's
	///     direct console writes and the game's Console.WriteLine then show where they come from in every text log (log
	///     files, a batch run's output), as Debug's messages do. The editor's console log is one too: its console keeps the
	///     stack apart, everything written out shows it.
	/// </summary>
	public class ConsoleLogWriter : TextWriter
	{
		#region PrivateFields

		/// <summary>What <see cref="Install" /> put in place (the console's synchronized wrapper of it) and what that writes to.</summary>
		private static TextWriter installedConsoleOut;

		private static TextWriter installedOutput;

		private readonly StringBuilder line = new StringBuilder();
		private readonly List<string> completed = new List<string>();
		private readonly object writeSync = new object();

		/// <summary>Characters of the open line already written out by a <see cref="Flush" />.</summary>
		private int lineWritten;

		#endregion

		#region Constructors

		public ConsoleLogWriter(TextWriter output)
		{
			Output = output ?? throw new ArgumentNullException(nameof(output));
		}

		#endregion

		#region PublicFields

		public override Encoding Encoding => Output.Encoding;

		/// <summary>
		///     The console's output under the installed log writer while it is still the console's (<see cref="Install" />),
		///     else <see cref="Console.Out" />: a writer replacing it (the editor's console log) writes there, not through it.
		/// </summary>
		public static TextWriter ConsoleOutput => installedConsoleOut != null && ReferenceEquals(Console.Out, installedConsoleOut) ? installedOutput : Console.Out;

		#endregion

		#region ProtectedFields

		/// <summary>Where the messages and their call stacks are written.</summary>
		protected TextWriter Output { get; }

		#endregion

		#region PublicMethods

		/// <summary>
		///     Puts a log writer over <see cref="Console.Out" /> (players, batch runs, the editor before its console log). Debug
		///     writes its messages, which carry their stack already, to the output under it.
		/// </summary>
		public static void Install()
		{
			TextWriter output = ConsoleOutput;
			Console.SetOut(new ConsoleLogWriter(output));
			installedConsoleOut = Console.Out;
			installedOutput = output;
			Debug.Output = output;
		}

		/// <summary>The type a console line logs: Debug's prefixes ("[Error] ", "[Exception] ", "[Warning] "), a shader message with "error".</summary>
		public static LogType TypeOf(string text)
		{
			if (text.StartsWith("[Exception]", StringComparison.Ordinal) || text.StartsWith("[Error]", StringComparison.Ordinal)
				|| (text.StartsWith("[Shader]", StringComparison.Ordinal) && text.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0))
			{
				return LogType.Error;
			}

			return text.StartsWith("[Warning]", StringComparison.Ordinal) ? LogType.Warning : LogType.Log;
		}

		/// <summary>A line of a call stack ("   at Type.Method(...) in file:line N").</summary>
		public static bool IsStackTraceLine(string text)
		{
			return text.StartsWith("   at ", StringComparison.Ordinal);
		}

		public override void Write(char value)
		{
			lock (writeSync)
			{
				Append(value);
				WriteCompleted();
			}
		}

		public override void Write(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return;
			}

			lock (writeSync)
			{
				foreach (char c in value)
				{
					Append(c);
				}

				WriteCompleted();
			}
		}

		public override void Write(char[] buffer, int index, int count)
		{
			Write(new string(buffer, index, count));
		}

		/// <summary>One write: the text and its line end are one message (TextWriter's own writes them apart).</summary>
		public override void WriteLine(string value)
		{
			Write((value ?? "") + NewLine);
		}

		/// <summary>Writes out the open line too (without a stack: it is not finished), then flushes the output.</summary>
		public override void Flush()
		{
			lock (writeSync)
			{
				if (line.Length > lineWritten)
				{
					Output.Write(line.ToString(lineWritten, line.Length - lineWritten));
					lineWritten = line.Length;
				}

				Output.Flush();
			}
		}

		#endregion

		/// <summary>Whether plain messages' stacks are captured for <see cref="OnMessage" /> too (they are never written out).</summary>
		protected virtual bool KeepsPlainStackTraces => false;

		#region ProtectedMethods

		/// <summary>
		///     A message was written (called before it is written out): its lines and the writer's call stack, null when the
		///     message brings its own, has no text or its type records none (or it is a plain message and
		///     <see cref="KeepsPlainStackTraces" /> is off).
		/// </summary>
		protected virtual void OnMessage(IReadOnlyList<string> lines, string stackTrace)
		{
		}

		#endregion

		#region PrivateMethods

		private void Append(char c)
		{
			if (c == '\n')
			{
				completed.Add(line.ToString());
				line.Clear();
			}
			else if (c != '\r')
			{
				line.Append(c);
			}
		}

		private void WriteCompleted()
		{
			if (completed.Count == 0)
			{
				return;
			}

			string[] lines = completed.ToArray();
			completed.Clear();
			string first = FirstText(lines);
			LogType type = first != null ? TypeOf(first) : LogType.Log;
			bool writesStack = Debug.WritesStackTrace(type);
			string stackTrace = first != null && (writesStack || KeepsPlainStackTraces) && !BringsStackTrace(lines)
				? Debug.CaptureStackTrace(Debug.GetStackTraceLogType(type), IsWriterFrame)
				: null;
			OnMessage(lines, stackTrace);
			for (int i = 0; i < lines.Length; i++)
			{
				// The start of the first line may be out already (a flush while it was open).
				Output.WriteLine(i == 0 && lineWritten > 0 ? lines[0].Substring(Math.Min(lineWritten, lines[0].Length)) : lines[i]);
			}

			lineWritten = 0;
			if (stackTrace != null && writesStack)
			{
				Output.WriteLine(stackTrace);
			}
		}

		/// <summary>The first line with text, or null.</summary>
		private static string FirstText(string[] lines)
		{
			foreach (string text in lines)
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
			}

			return null;
		}

		private static bool BringsStackTrace(string[] lines)
		{
			foreach (string text in lines)
			{
				if (IsStackTraceLine(text))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Frames between the writer and this one: log writers' own and the runtime's (Console, its synchronized writer).</summary>
		private static bool IsWriterFrame(Type type)
		{
			return typeof(ConsoleLogWriter).IsAssignableFrom(type)
				|| (type.Namespace != null && (type.Namespace == "System" || type.Namespace.StartsWith("System.", StringComparison.Ordinal)));
		}

		#endregion
	}
}
