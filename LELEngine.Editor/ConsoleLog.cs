using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine.Editor
{
	/// <summary>Who logged a console message.</summary>
	internal enum ConsoleSource
	{
		/// <summary>The editor and the engine outside the game (imports, rendering setup, ...).</summary>
		Editor,

		/// <summary>The game: the project's scripts (a frame of theirs in the call stack) or anything while game code runs.</summary>
		Game
	}

	/// <summary>A message of the console: its text, the call stack that logged it, where and when.</summary>
	internal sealed class ConsoleEntry
	{
		#region PublicFields

		/// <summary>Order of arrival (selection and collapsing keep to it across clears).</summary>
		public long Id;

		public ConsoleSource Source;

		public string Text;

		/// <summary>The call stack ("   at Type.Method(...) in file:line N" lines), or null.</summary>
		public string StackTrace;

		public LogType Type;

		/// <summary>A build diagnostic's source location (its only frame), or null.</summary>
		public string File;

		public int Line;
		public DateTime Time;

		/// <summary>Time.frameCount when logged in play mode, else -1.</summary>
		public long Frame = -1;

		/// <summary>The object the message is about (selected when the message is); weak: unloaded scripts' objects can go.</summary>
		public WeakReference<object> Context;

		/// <summary>
		///     Script build diagnostics (Unity's compile errors): no clear and no trimming removes them, only the next
		///     build's result replaces them.
		/// </summary>
		public bool Sticky;

		public bool IsError => Type == LogType.Error || Type == LogType.Exception;

		/// <summary>The message's first line (list rows).</summary>
		public string FirstLine
		{
			get
			{
				if (firstLine == null)
				{
					int newline = Text.IndexOf('\n');
					firstLine = newline >= 0 ? Text.Substring(0, newline).TrimEnd('\r') : Text;
				}

				return firstLine;
			}
		}

		/// <summary>The engine's [Tag] the message starts with ("[Model]"), or null.</summary>
		public string Tag
		{
			get
			{
				if (tag == null)
				{
					int close = Text.Length > 2 && Text[0] == '[' ? Text.IndexOf(']', 1) : -1;
					tag = close > 1 && close <= 32 && Text.IndexOf(' ', 1, close - 1) < 0 ? Text.Substring(0, close + 1) : "";
				}

				return tag.Length > 0 ? tag : null;
			}
		}

		public IReadOnlyList<ConsoleFrame> Frames => frames ??= ConsoleStackTrace.Parse(this);

		/// <summary>The same text, type and stack: one row when collapsed.</summary>
		public string CollapseKey => collapseKey ??= (int)Type + "\u0001" + Text + "\u0001" + StackTrace + "\u0001" + File + ":" + Line;

		#endregion

		#region PrivateFields

		private string firstLine;
		private string tag;
		private IReadOnlyList<ConsoleFrame> frames;
		private string collapseKey;

		#endregion

		#region PublicMethods

		public object GetContext()
		{
			return Context != null && Context.TryGetTarget(out object target) ? target : null;
		}

		/// <summary>The message and its call stack as text (copy, export).</summary>
		public string ToFullText()
		{
			string location = File != null ? "\n   at " + File + ":line " + Line : "";
			return Text + (StackTrace != null ? "\n" + StackTrace : location);
		}

		#endregion
	}

	/// <summary>
	///     The console's messages: the engine's log (<see cref="Debug.MessageLogged" />, with call stacks and context
	///     objects), script build diagnostics (<see cref="Add" />) and everything else written to the console (shader
	///     compiler messages, build output, Console.WriteLine of scripts, with the writer's call stack). It still writes all
	///     of it to the original output, each message with its call stack (or a diagnostic's source line) on the lines after
	///     it (<see cref="ConsoleLogWriter" />): only the console window folds the stack away. Every message is the game's or
	///     the editor's (<see cref="ConsoleSource" />). Thread safe: script builds and game threads report from the background.
	/// </summary>
	internal sealed class ConsoleLog : ConsoleLogWriter
	{
		#region PublicFields

		/// <summary>Bumped on every change (panels re-filter, and scroll to the bottom on new messages).</summary>
		public int Version { get; private set; }

		/// <summary>Errors and exceptions logged since the editor started (Error Pause notices new ones).</summary>
		public int ErrorsLogged { get; private set; }

		/// <summary>Bumped when messages go or change (clear, the oldest trimmed): views rebuild instead of adding the new ones.</summary>
		public int RemovalVersion { get; private set; }

		/// <summary>
		///     Every message as it arrives, on the thread that logged it and under the log's lock (the play session's recorder
		///     queues it): it must only copy what it needs and never log. An entry whose call stack lines came after it in the
		///     same write is raised again, whole, with the same <see cref="ConsoleEntry.Id" />.
		/// </summary>
		public Action<ConsoleEntry> EntryAdded;

		#endregion

		#region PrivateFields

		private readonly List<ConsoleEntry> entries = new List<ConsoleEntry>();
		private readonly object sync = new object();
		private long nextId;

		/// <summary>Game code runs on this thread (<see cref="BeginGame" />): what it logs is the game's.</summary>
		[ThreadStatic] private static int gameDepth;

		// The project folder as stack traces write it (with a separator at the end), for the one it was made from.
		private string projectRoot;
		private string projectRootSource;

		#endregion

		#region Constructors

		private ConsoleLog(TextWriter original) : base(original)
		{
		}

		#endregion

		#region PublicMethods

		public new static ConsoleLog Install()
		{
			// In place of the log writer the host installed (the same output under it), not over it.
			var log = new ConsoleLog(ConsoleOutput);
			Console.SetOut(log);
			// The engine's messages come with their call stack, in their text and in the event: the text goes straight out.
			Debug.Output = log.Output;
			Debug.MessageLogged += log.OnMessageLogged;
			return log;
		}

		/// <summary>
		///     Game code starts running on this thread (play mode's frames, scene loads, [ExecuteAlways] components in edit
		///     mode): what is logged until <see cref="EndGame" /> is the game's, engine messages included.
		/// </summary>
		public static void BeginGame()
		{
			gameDepth++;
		}

		public static void EndGame()
		{
			gameDepth = Math.Max(0, gameDepth - 1);
		}

		/// <summary>Adds an entry directly (build diagnostics with a source location; <paramref name="sticky" />: kept by Clear).</summary>
		public void Add(string text, LogType type, string file = null, int lineNumber = 0, bool sticky = false)
		{
			lock (sync)
			{
				AddEntry(new ConsoleEntry { Text = text, Type = type, File = file, Line = lineNumber, Sticky = sticky });
			}

			// Prefixed as Debug writes its messages ("[Error] ..."), with the source line as their stack: batch logs show these
			// errors like any other.
			Output.WriteLine((type == LogType.Log ? text : "[" + type + "] " + text) + (file != null ? "\n   at " + file + ":line " + lineNumber : ""));
		}

		public List<ConsoleEntry> Snapshot()
		{
			return Snapshot(out _);
		}

		/// <summary>The messages, and the <see cref="RemovalVersion" /> they are of.</summary>
		public List<ConsoleEntry> Snapshot(out int removalVersion)
		{
			lock (sync)
			{
				removalVersion = RemovalVersion;
				return new List<ConsoleEntry>(entries);
			}
		}

		/// <summary>Removes every message but the sticky ones (script build errors stay until the next build).</summary>
		public void Clear()
		{
			lock (sync)
			{
				entries.RemoveAll(entry => !entry.Sticky);
				Version++;
				RemovalVersion++;
			}
		}

		/// <summary>The previous build's diagnostics go (called when a build finished, before it reports its own).</summary>
		public void RemoveSticky()
		{
			lock (sync)
			{
				if (entries.RemoveAll(entry => entry.Sticky) > 0)
				{
					Version++;
					RemovalVersion++;
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void OnMessageLogged(string message, string stackTrace, LogType type, object context)
		{
			lock (sync)
			{
				AddEntry(new ConsoleEntry
				{
					Text = message ?? "Null",
					StackTrace = stackTrace,
					Type = type,
					Context = context != null ? new WeakReference<object>(context) : null
				});
			}
		}

		/// <summary>
		///     Written to the console directly (Console.WriteLine): a message per line, each with the writer's call stack (past
		///     the runtime's writer frames; null when the text brings its own: its "   at" lines join the message before them).
		/// </summary>
		/// <summary>The console shows any message's stack on demand, plain ones too.</summary>
		protected override bool KeepsPlainStackTraces => true;

		protected override void OnMessage(IReadOnlyList<string> lines, string stackTrace)
		{
			lock (sync)
			{
				foreach (string text in lines)
				{
					if (text.Length == 0)
					{
						continue;
					}

					if (IsStackTraceLine(text) && entries.Count > 0)
					{
						// Stack trace line of something printed to the console: belongs to the previous entry.
						ConsoleEntry last = entries[entries.Count - 1];
						ConsoleEntry whole = new ConsoleEntry
						{
							Id = last.Id, Source = last.Source, Text = last.Text, Type = last.Type, Time = last.Time, Frame = last.Frame, Context = last.Context, Sticky = last.Sticky,
							File = last.File, Line = last.Line, StackTrace = last.StackTrace != null ? last.StackTrace + "\n" + text : text
						};
						entries[entries.Count - 1] = whole;
						Version++;
						RemovalVersion++;
						Raise(whole);
						continue;
					}

					AddEntry(new ConsoleEntry { Text = text, Type = TypeOf(text), StackTrace = stackTrace });
				}
			}
		}

		/// <summary>The project's scripts logged it (a frame of theirs in the call stack, a diagnostic of their file) or game code was running.</summary>
		private ConsoleSource SourceOf(ConsoleEntry entry)
		{
			if (gameDepth > 0)
			{
				return ConsoleSource.Game;
			}

			string root = EditorApplication.Instance?.Project?.RootPath;
			if (root == null)
			{
				return ConsoleSource.Editor;
			}

			if (root != projectRootSource)
			{
				projectRootSource = root;
				projectRoot = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
			}

			bool inProject = (entry.StackTrace != null && entry.StackTrace.Contains(projectRoot, StringComparison.OrdinalIgnoreCase))
				|| (entry.File != null && entry.File.Replace('/', '\\').StartsWith(projectRoot.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
			return inProject ? ConsoleSource.Game : ConsoleSource.Editor;
		}

		private void AddEntry(ConsoleEntry entry)
		{
			entry.Id = nextId++;
			entry.Time = DateTime.Now;
			entry.Source = SourceOf(entry);
			EditorApplication editor = EditorApplication.Instance;
			entry.Frame = editor != null && editor.IsPlaying ? Time.frameCount : -1;
			entries.Add(entry);
			int max = EditorPreferences.Current.Console.MaxEntries;
			if (entries.Count > max)
			{
				// The oldest messages go, never the script build's: a flood of game messages cannot push compile errors out.
				int excess = entries.Count - max;
				entries.RemoveAll(old => !old.Sticky && excess-- > 0);
				RemovalVersion++;
			}

			if (entry.IsError)
			{
				ErrorsLogged++;
			}

			Version++;
			Raise(entry);
		}

		private void Raise(ConsoleEntry entry)
		{
			try
			{
				EntryAdded?.Invoke(entry);
			}
			catch (Exception)
			{
				// A listener's failure must not lose the message for the console (and it cannot be logged from here).
			}
		}

		#endregion
	}
}
