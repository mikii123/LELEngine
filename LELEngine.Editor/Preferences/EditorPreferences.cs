using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The user's editor preferences, the same for every project (Edit > Preferences...): the theme of every editor
	///     window, the console, the external script editor. Saved as preferences.json in the editor's data folder
	///     (%APPDATA%/LELEngine/Editor), outside projects, a moment after a change (<see cref="Update" />); another
	///     editor's change to the file is read back. Automated runs use the defaults and save nothing, unless
	///     preferencesfile=path names a file of their own.
	/// </summary>
	internal sealed class EditorPreferences
	{
		#region PublicFields

		public ThemePreferences Theme = new ThemePreferences();
		public ConsolePreferences Console = new ConsolePreferences();
		public ExternalToolsPreferences ExternalTools = new ExternalToolsPreferences();

		public static EditorPreferences Current { get; private set; } = new EditorPreferences();

		/// <summary>Bumped on every change (the console re-filters its entries).</summary>
		public static int Version { get; private set; }

		/// <summary>Bumped when the theme changes: every editor window applies it again before its next frame.</summary>
		public static int ThemeVersion { get; private set; }

		public static string DefaultFile => Path.Combine(RecentProjects.EditorDataFolder, "preferences.json");

		/// <summary>The file read and written (null: an automated run without preferencesfile=, nothing is saved).</summary>
		public static string File { get; private set; }

		#endregion

		#region PrivateFields

		private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
		{
			IncludeFields = true,
			WriteIndented = true,
			Converters = { new JsonStringEnumConverter() }
		};

		private const double SaveDelay = 0.4;
		private const double ReadBackInterval = 2.0;

		private static bool dirty;
		private static double changedAt;
		private static double checkedAt;
		private static DateTime fileStamp;

		#endregion

		#region PublicMethods

		/// <summary>Reads the preferences (before the first window opens: the theme applies to it).</summary>
		public static void Initialize(string[] args)
		{
			string named = null;
			foreach (string arg in args)
			{
				if (arg.StartsWith("preferencesfile=", StringComparison.OrdinalIgnoreCase))
				{
					named = arg.Substring("preferencesfile=".Length).Trim('"');
				}
			}

			File = named ?? (EditorApplication.IsAutomatedRun(args) ? null : DefaultFile);
			Current = Read(File) ?? new EditorPreferences();
			Current.Normalize();
			Current.ApplyToEngine();
			Version++;
			ThemeVersion++;
		}

		/// <summary>Something changed (saved a moment later); <paramref name="theme" />: the windows apply the theme again.</summary>
		public static void MarkChanged(bool theme = false)
		{
			Version++;
			if (theme)
			{
				ThemeVersion++;
			}

			Current.ApplyToEngine();
			dirty = true;
			changedAt = Now;
		}

		/// <summary>Every frame of the editor: saves a moment after the last change, reads another editor's changes back.</summary>
		public static void Update()
		{
			if (File == null)
			{
				return;
			}

			double now = Now;
			if (dirty)
			{
				if (now - changedAt >= SaveDelay)
				{
					Save();
				}

				return;
			}

			if (now - checkedAt < ReadBackInterval)
			{
				return;
			}

			checkedAt = now;
			DateTime stamp = Stamp(File);
			if (stamp != fileStamp && stamp != DateTime.MinValue)
			{
				EditorPreferences read = Read(File);
				if (read != null)
				{
					read.Normalize();
					Current = read;
					Current.ApplyToEngine();
					Version++;
					ThemeVersion++;
				}
			}
		}

		/// <summary>Saves a pending change now (the editor closes).</summary>
		public static void Flush()
		{
			if (dirty && File != null)
			{
				Save();
			}
		}

		#endregion

		#region PrivateMethods

		private static double Now => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

		private static EditorPreferences Read(string file)
		{
			if (file == null || !System.IO.File.Exists(file))
			{
				return null;
			}

			try
			{
				fileStamp = Stamp(file);
				return JsonSerializer.Deserialize<EditorPreferences>(System.IO.File.ReadAllText(file), Json);
			}
			catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException || e is NotSupportedException)
			{
				System.Console.WriteLine("[Preferences] Cannot read " + file + ": " + e.Message);
				return null;
			}
		}

		private static void Save()
		{
			dirty = false;
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(File));
				System.IO.File.WriteAllText(File, JsonSerializer.Serialize(Current, Json));
				fileStamp = Stamp(File);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				System.Console.WriteLine("[Preferences] Cannot save " + File + ": " + e.Message);
			}
		}

		private static DateTime Stamp(string file)
		{
			try
			{
				return System.IO.File.Exists(file) ? System.IO.File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
			}
			catch (IOException)
			{
				return DateTime.MinValue;
			}
		}

		/// <summary>Sections and lists a hand-edited or older file lacks, values back in range.</summary>
		private void Normalize()
		{
			Theme ??= new ThemePreferences();
			Console ??= new ConsolePreferences();
			ExternalTools ??= new ExternalToolsPreferences();
			Theme.Colors ??= new Dictionary<string, string>();
			Theme.FontSize = Math.Clamp(Theme.FontSize, 10f, 32f);
			Theme.CodeFontSize = Math.Clamp(Theme.CodeFontSize, 8f, 32f);
			Console.Filters ??= new List<ConsoleFilter>();
			Console.HiddenTags ??= new List<string>();
			Console.HiddenFrames ??= new List<string>();
			Console.LinesPerEntry = Math.Clamp(Console.LinesPerEntry, 1, 3);
			Console.MaxEntries = Math.Clamp(Console.MaxEntries, 100, 100000);
			Console.ListFraction = Math.Clamp(Console.ListFraction, 0.1f, 0.9f);
			Console.SourceContextLines = Math.Clamp(Console.SourceContextLines, 0, 10);
			foreach (ConsoleFilter filter in Console.Filters)
			{
				filter.Name ??= "";
				filter.Pattern ??= "";
				filter.Color ??= ConsoleFilter.DefaultColor;
			}

			ExternalTools.CustomPath ??= "";
			ExternalTools.CustomArguments ??= ExternalToolsPreferences.DefaultArguments;
		}

		/// <summary>The engine settings the preferences hold (how much of the call stack log messages record).</summary>
		private void ApplyToEngine()
		{
			Debug.SetStackTraceLogType(LogType.Log, Console.LogStackTrace);
			Debug.SetStackTraceLogType(LogType.Warning, Console.WarningStackTrace);
			Debug.SetStackTraceLogType(LogType.Error, Console.ErrorStackTrace);
		}

		#endregion
	}

	/// <summary>The look of every editor window (<see cref="EditorStyle" />).</summary>
	internal sealed class ThemePreferences
	{
		/// <summary>One of <see cref="EditorStyle.Presets" />: the colors the overrides start from.</summary>
		public string Preset = "Dark";

		/// <summary>Size of the editor's text, in pixels.</summary>
		public float FontSize = 17f;

		/// <summary>Size of source code (the console's source view).</summary>
		public float CodeFontSize = 15f;

		/// <summary>Colors changed from the preset's, by name (ImGui's: WindowBg, Button, ...; the editor's: Warning, CodeKeyword, ...) as #RRGGBBAA.</summary>
		public Dictionary<string, string> Colors = new Dictionary<string, string>();
	}

	/// <summary>The Console panel: its toolbar's toggles, list, stack traces, source view and custom filters.</summary>
	internal sealed class ConsolePreferences
	{
		/// <summary>
		///     The editor's own messages (imports, rendering setup, ...: <see cref="ConsoleSource.Editor" />); off hides
		///     its plain messages, its warnings and errors always show. The game's are filtered in the console.
		/// </summary>
		public bool ShowEditorMessages = true;

		public bool ShowLogs = true;
		public bool ShowWarnings = true;
		public bool ShowErrors = true;

		/// <summary>Identical messages (text and stack trace) as one row with a count.</summary>
		public bool Collapse;

		public bool ClearOnPlay = true;
		public bool ClearOnRecompile;

		/// <summary>An error pauses play mode.</summary>
		public bool ErrorPause;

		public bool SearchRegex;
		public bool SearchMatchCase;

		/// <summary>The search looks in stack traces too.</summary>
		public bool SearchStackTraces;

		public bool ShowTimestamps = true;

		/// <summary>The frame (Time.frameCount) a message was logged on, in play mode.</summary>
		public bool ShowFrames;

		/// <summary>The file and line a message came from (its first frame in a source file).</summary>
		public bool ShowSourceColumn = true;

		/// <summary>Lines of a message the list shows per row (1 to 3).</summary>
		public int LinesPerEntry = 1;

		/// <summary>Older messages go beyond this many.</summary>
		public int MaxEntries = 5000;

		/// <summary>Height of the list (the rest is the selected message and its call stack).</summary>
		public float ListFraction = 0.6f;

		public StackTraceLogType LogStackTrace = StackTraceLogType.ScriptOnly;
		public StackTraceLogType WarningStackTrace = StackTraceLogType.ScriptOnly;
		public StackTraceLogType ErrorStackTrace = StackTraceLogType.ScriptOnly;

		/// <summary>Frames of .NET and third-party libraries (exceptions record them) are listed too.</summary>
		public bool ShowLibraryFrames;

		/// <summary>
		///     Frames whose method starts with one of these (Namespace.Type.Method, Namespace.Type.) are folded and never the
		///     frame a message opens: logging wrappers.
		/// </summary>
		public List<string> HiddenFrames = new List<string>();

		/// <summary>The code around a frame's line under it in the call stack (the project's frames and the one a message opens).</summary>
		public bool ShowSourceCode = true;

		/// <summary>Lines of code above and below a frame's line (1: three lines).</summary>
		public int SourceContextLines = 1;

		/// <summary>The toolbar's filters: a toggle each (off hides what it matches), matches marked with its color.</summary>
		public List<ConsoleFilter> Filters = new List<ConsoleFilter>();

		/// <summary>[Tag]s whose messages are hidden (the Tags menu).</summary>
		public List<string> HiddenTags = new List<string>();
	}

	/// <summary>A custom console filter (Console Pro's): what it matches, its color and whether its matches are shown.</summary>
	internal sealed class ConsoleFilter
	{
		public const string DefaultColor = "#4FA3FFFF";

		public string Name = "";
		public string Pattern = "";
		public bool Regex;
		public bool MatchCase;

		/// <summary>Matches in stack traces too (a class or file of the call stack).</summary>
		public bool InStackTrace;

		public string Color = DefaultColor;

		/// <summary>The toolbar toggle: off hides the messages it matches.</summary>
		public bool Shown = true;
	}

	internal enum ScriptEditorKind
	{
		/// <summary>The first installed of Rider, Visual Studio, Visual Studio Code; else the system's.</summary>
		Auto,

		/// <summary>The application the system opens .cs files with (no line).</summary>
		SystemDefault,
		VisualStudioCode,
		Rider,
		VisualStudio,

		/// <summary><see cref="ExternalToolsPreferences.CustomPath" /> with <see cref="ExternalToolsPreferences.CustomArguments" />.</summary>
		Custom
	}

	/// <summary>The application that opens scripts at a line (console stack traces, script assets).</summary>
	internal sealed class ExternalToolsPreferences
	{
		public const string DefaultArguments = "\"$(File)\"";

		public ScriptEditorKind ScriptEditor = ScriptEditorKind.Auto;
		public string CustomPath = "";

		/// <summary>Arguments of the custom editor: $(File), $(Line), $(Column), $(ProjectPath) are replaced.</summary>
		public string CustomArguments = DefaultArguments;
	}

	/// <summary>Colors as #RRGGBBAA text (preferences files).</summary>
	internal static class ColorHex
	{
		public static string ToHex(NVector4 color)
		{
			return "#" + Byte(color.X) + Byte(color.Y) + Byte(color.Z) + Byte(color.W);
		}

		public static bool TryParse(string text, out NVector4 color)
		{
			color = default;
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			string hex = text.TrimStart('#');
			if ((hex.Length != 6 && hex.Length != 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
			{
				return false;
			}

			if (hex.Length == 6)
			{
				value = (value << 8) | 0xFF;
			}

			color = new NVector4((value >> 24 & 0xFF) / 255f, (value >> 16 & 0xFF) / 255f, (value >> 8 & 0xFF) / 255f, (value & 0xFF) / 255f);
			return true;
		}

		public static NVector4 Parse(string text, NVector4 fallback)
		{
			return TryParse(text, out NVector4 color) ? color : fallback;
		}

		private static string Byte(float channel)
		{
			return ((int)MathF.Round(Math.Clamp(channel, 0f, 1f) * 255f)).ToString("X2", CultureInfo.InvariantCulture);
		}
	}
}
