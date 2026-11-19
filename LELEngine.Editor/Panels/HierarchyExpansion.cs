using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LELEngine.Editor
{
	/// <summary>
	///     The hierarchy's state per user, per project folder and scene (or prefab): which rows the user unfolded (rows start
	///     folded) and which objects the scene view hides (<see cref="SceneVisibility" />). hierarchy.json in the editor's data
	///     folder (%APPDATA%/LELEngine/Editor), next to layout.ini, never in the project. Objects are known by their scene id,
	///     so the state survives reloads, play mode and restarts. Automated runs read the user's file and save nothing,
	///     unless hierarchyfile=path names a file of their own.
	/// </summary>
	internal static class HierarchyExpansion
	{
		#region PublicFields

		public static string DefaultFile => Path.Combine(RecentProjects.EditorDataFolder, "hierarchy.json");

		#endregion

		#region PrivateFields

		// Scenes remembered at most: the least recently used go first.
		private const int MaxEntries = 200;

		private static string file;
		private static bool writable;

		#endregion

		#region PublicMethods

		public static void Initialize(string[] args)
		{
			string named = null;
			foreach (string arg in args)
			{
				if (arg.StartsWith("hierarchyfile=", StringComparison.OrdinalIgnoreCase))
				{
					named = arg.Substring("hierarchyfile=".Length).Trim('"');
				}
			}

			file = named ?? DefaultFile;
			writable = named != null || !EditorApplication.IsAutomatedRun(args);
		}

		/// <summary>The key of a scene (or prefab) of a project; null when it has no file yet (an unsaved scene).</summary>
		public static string Key(string projectRoot, string assetPath)
		{
			return projectRoot == null || string.IsNullOrEmpty(assetPath) ? null : Path.GetFullPath(projectRoot).TrimEnd('\\', '/') + "|" + assetPath;
		}

		/// <summary>The ids of the unfolded objects of the scene; empty when none were saved.</summary>
		public static HashSet<ulong> Load(string key)
		{
			return key != null && Read().TryGetValue(key, out Entry entry) && entry.Expanded != null ? new HashSet<ulong>(entry.Expanded) : new HashSet<ulong>();
		}

		/// <summary>The ids of the objects the scene view hides in the scene; empty when none were saved.</summary>
		public static HashSet<ulong> LoadHidden(string key)
		{
			return key != null && Read().TryGetValue(key, out Entry entry) && entry.Hidden != null ? new HashSet<ulong>(entry.Hidden) : new HashSet<ulong>();
		}

		/// <summary>Writes the scene's unfolded objects (read first: other scenes and other editors' changes stay).</summary>
		public static void Save(string key, HashSet<ulong> expanded)
		{
			Update(key, entry => entry.Expanded = expanded.Count == 0 ? null : expanded.OrderBy(id => id).ToList());
		}

		/// <summary>Writes the objects the scene view hides in the scene (read first, as <see cref="Save" />).</summary>
		public static void SaveHidden(string key, HashSet<ulong> hidden)
		{
			Update(key, entry => entry.Hidden = hidden.Count == 0 ? null : hidden.OrderBy(id => id).ToList());
		}

		#endregion

		#region PrivateMethods

		private static void Update(string key, Action<Entry> change)
		{
			if (!writable || key == null || file == null)
			{
				return;
			}

			Dictionary<string, Entry> entries = Read();
			if (!entries.TryGetValue(key, out Entry entry))
			{
				entry = new Entry();
			}

			change(entry);
			entry.Used = DateTime.UtcNow;
			if ((entry.Expanded == null || entry.Expanded.Count == 0) && (entry.Hidden == null || entry.Hidden.Count == 0))
			{
				entries.Remove(key);
			}
			else
			{
				entries[key] = entry;
			}

			if (entries.Count > MaxEntries)
			{
				entries = entries.OrderByDescending(pair => pair.Value.Used).Take(MaxEntries).ToDictionary(pair => pair.Key, pair => pair.Value);
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file));
				File.WriteAllText(file, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Debug.LogWarning("[Editor] Cannot save the hierarchy's state: " + e.Message);
			}
		}

		private static Dictionary<string, Entry> Read()
		{
			try
			{
				if (file != null && File.Exists(file))
				{
					return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new Dictionary<string, Entry>();
				}
			}
			catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
			{ }

			return new Dictionary<string, Entry>();
		}

		#endregion

		#region Nested type: Entry

		private sealed class Entry
		{
			/// <summary>The unfolded objects (entries of the earlier format, which listed folded ones, read as empty).</summary>
			public List<ulong> Expanded { get; set; }

			/// <summary>The objects hidden in the scene view.</summary>
			public List<ulong> Hidden { get; set; }

			/// <summary>Last saved (the oldest entries are dropped).</summary>
			public DateTime Used { get; set; }
		}

		#endregion
	}
}
