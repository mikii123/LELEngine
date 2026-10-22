using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LELEngine.Editor
{
	/// <summary>Where a frame's code is from: the console shows the project's in full, the engine's dimmed, libraries' hidden.</summary>
	internal enum ConsoleFrameKind
	{
		/// <summary>A line of the trace that is no frame ("--- Caused by ...", "--- End of stack trace ...").</summary>
		Note,

		/// <summary>The project's scripts (a source file in the project folder).</summary>
		Project,

		/// <summary>The engine and the editor (LELEngine.*).</summary>
		Engine,

		/// <summary>.NET and third-party libraries (System.*, OpenTK.*, ...).</summary>
		Library
	}

	/// <summary>A line of a message's call stack.</summary>
	internal sealed class ConsoleFrame
	{
		/// <summary>Namespace.Type.Method(Parameters) (a note's text for <see cref="ConsoleFrameKind.Note" />).</summary>
		public string Method;

		/// <summary>The source file (absolute), or null.</summary>
		public string File;

		public int Line;
		public ConsoleFrameKind Kind;

		/// <summary>The file exists here (the source view can show it, the script editor open it).</summary>
		public bool HasSource;

		/// <summary>The file as the console shows it: relative to the project, else its name.</summary>
		public string DisplayFile;
	}

	/// <summary>Reads .NET stack traces ("   at Type.Method(...) in file:line N") into frames.</summary>
	internal static class ConsoleStackTrace
	{
		#region PrivateFields

		private static readonly Regex FrameLine = new Regex(@"^\s*at (?<method>.+?)(?: in (?<file>.+):line (?<line>\d+))?\s*$", RegexOptions.Compiled);

		private static readonly string[] LibraryNamespaces = { "System.", "Microsoft.", "Internal.", "OpenTK.", "Hexa.", "Silk.", "Mono.Cecil." };

		/// <summary>The type, then a closure class (&lt;&gt;c, &lt;&gt;c__DisplayClass3_0) or not, then &lt;Method&gt;kind__..., what follows.</summary>
		private static readonly Regex GeneratedName = new Regex(@"^(?<type>.+?)(?:\.<>c(?:__DisplayClass[\d_]+)?)?\.<(?<method>[^>]+)>(?<kind>[a-z])__(?:(?<local>[^|.]+)\|)?[^.]*(?:\..*)?$", RegexOptions.Compiled);

		#endregion

		#region PublicMethods

		public static IReadOnlyList<ConsoleFrame> Parse(ConsoleEntry entry)
		{
			var frames = new List<ConsoleFrame>();
			if (entry.StackTrace == null && entry.File != null)
			{
				// A build diagnostic: its source location is its only frame.
				frames.Add(Frame("(" + Path.GetFileName(entry.File) + ")", entry.File, entry.Line));
				return frames;
			}

			if (entry.StackTrace == null)
			{
				return frames;
			}

			foreach (string raw in entry.StackTrace.Split('\n'))
			{
				string text = raw.TrimEnd('\r');
				if (text.Trim().Length == 0)
				{
					continue;
				}

				Match match = FrameLine.Match(text);
				if (!match.Success)
				{
					frames.Add(new ConsoleFrame { Method = text.Trim(), Kind = ConsoleFrameKind.Note });
					continue;
				}

				string file = match.Groups["file"].Success ? match.Groups["file"].Value : null;
				int line = match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) : 0;
				frames.Add(Frame(match.Groups["method"].Value, file, line));
			}

			return frames;
		}

		/// <summary>
		///     The frame a message opens and shows the source of: the first with source that is no logging wrapper
		///     (<see cref="ConsolePreferences.HiddenFrames" />), the project's before the engine's; -1 when none has source.
		/// </summary>
		public static int SourceFrame(IReadOnlyList<ConsoleFrame> frames)
		{
			int engine = -1;
			for (int i = 0; i < frames.Count; i++)
			{
				ConsoleFrame frame = frames[i];
				if (!frame.HasSource || IsHidden(frame))
				{
					continue;
				}

				if (frame.Kind == ConsoleFrameKind.Project)
				{
					return i;
				}

				if (engine < 0)
				{
					engine = i;
				}
			}

			return engine;
		}

		/// <summary>A logging wrapper's frame (<see cref="ConsolePreferences.HiddenFrames" />).</summary>
		public static bool IsHidden(ConsoleFrame frame)
		{
			if (frame.Kind == ConsoleFrameKind.Note)
			{
				return false;
			}

			foreach (string prefix in EditorPreferences.Current.Console.HiddenFrames)
			{
				if (!string.IsNullOrWhiteSpace(prefix) && frame.Method.StartsWith(prefix.Trim(), StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		///     Type.Method() of a frame, without the namespace and parameters (compact rows); the compiler's names of lambdas,
		///     local functions, iterators and async methods as the method they are in ("Player.Update() (lambda)").
		/// </summary>
		public static string ShortMethod(string method)
		{
			int paren = method.IndexOf('(');
			string name = paren >= 0 ? method.Substring(0, paren) : method;
			string suffix = "";
			Match generated = GeneratedName.Match(name);
			if (generated.Success)
			{
				// Namespace.Type.<>c.<Update>b__3_0, Namespace.Type.<Load>d__5.MoveNext, Namespace.Type.<Update>g__Local|3_0.
				string kind = generated.Groups["kind"].Value;
				string local = generated.Groups["local"].Value;
				suffix = kind == "b" ? " (lambda)" : kind == "g" ? "." + local : kind == "d" ? " (async / iterator)" : "";
				name = generated.Groups["type"].Value + "." + generated.Groups["method"].Value;
			}

			int dot = name.LastIndexOf('.');
			int typeDot = dot > 0 ? name.LastIndexOf('.', dot - 1) : -1;
			return (typeDot >= 0 ? name.Substring(typeDot + 1) : name) + "()" + suffix;
		}

		#endregion

		#region PrivateMethods

		private static ConsoleFrame Frame(string method, string file, int line)
		{
			string root = EditorApplication.Instance?.Project?.RootPath;
			bool inProject = file != null && root != null && IsUnder(file, root);
			ConsoleFrameKind kind = inProject ? ConsoleFrameKind.Project : IsLibrary(method) ? ConsoleFrameKind.Library : method.StartsWith("LELEngine.", StringComparison.Ordinal) ? ConsoleFrameKind.Engine : ConsoleFrameKind.Project;
			string display = file == null ? null : inProject ? Path.GetRelativePath(root, file).Replace('\\', '/') : Path.GetFileName(file);
			return new ConsoleFrame { Method = method, File = file, Line = line, Kind = kind, HasSource = file != null && SourceFiles.Exists(file), DisplayFile = display };
		}

		private static bool IsUnder(string file, string folder)
		{
			try
			{
				string full = Path.GetFullPath(file);
				string root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
				return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
			}
			catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
			{
				return false;
			}
		}

		private static bool IsLibrary(string method)
		{
			foreach (string prefix in LibraryNamespaces)
			{
				if (method.StartsWith(prefix, StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}
}
