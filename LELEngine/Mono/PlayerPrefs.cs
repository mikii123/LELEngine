using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LELEngine
{
	/// <summary>
	///     Small values a game keeps between runs, by key (Unity's PlayerPrefs): ints, floats and strings. Kept in
	///     <see cref="FileName" /> in <see cref="Application.persistentDataPath" /> (a built game: the user's local app
	///     data; the editor: the project's PersistentData folder), a plain JSON file, never the registry. Read when first
	///     asked; written by <see cref="Save" />, which also runs when the game quits and when the editor's play mode ends.
	///     A key holds one type: setting it as another type replaces it, and reading it as another type gives the default.
	///     Thread safe.
	/// </summary>
	public static class PlayerPrefs
	{
		#region PublicFields

		public const string FileName = "PlayerPrefs.json";

		/// <summary>The file the values are kept in.</summary>
		public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly Dictionary<string, int> ints = new Dictionary<string, int>(StringComparer.Ordinal);
		private static readonly Dictionary<string, float> floats = new Dictionary<string, float>(StringComparer.Ordinal);
		private static readonly Dictionary<string, string> strings = new Dictionary<string, string>(StringComparer.Ordinal);
		private static string loadedFrom;
		private static bool dirty;

		#endregion

		#region PublicMethods

		public static void SetInt(string key, int value)
		{
			lock (sync)
			{
				if (Unchanged(key, ints, value))
				{
					return;
				}

				Prepare(key);
				ints[key] = value;
			}
		}

		public static int GetInt(string key, int defaultValue = 0)
		{
			lock (sync)
			{
				Load();
				return key != null && ints.TryGetValue(key, out int value) ? value : defaultValue;
			}
		}

		/// <summary>A finite value (JSON has no NaN or infinity).</summary>
		public static void SetFloat(string key, float value)
		{
			if (!float.IsFinite(value))
			{
				throw new ArgumentOutOfRangeException(nameof(value), value, "PlayerPrefs keep finite floats only");
			}

			lock (sync)
			{
				if (Unchanged(key, floats, value))
				{
					return;
				}

				Prepare(key);
				floats[key] = value;
			}
		}

		public static float GetFloat(string key, float defaultValue = 0f)
		{
			lock (sync)
			{
				Load();
				return key != null && floats.TryGetValue(key, out float value) ? value : defaultValue;
			}
		}

		public static void SetString(string key, string value)
		{
			lock (sync)
			{
				if (Unchanged(key, strings, value ?? ""))
				{
					return;
				}

				Prepare(key);
				strings[key] = value ?? "";
			}
		}

		public static string GetString(string key, string defaultValue = "")
		{
			lock (sync)
			{
				Load();
				return key != null && strings.TryGetValue(key, out string value) ? value : defaultValue;
			}
		}

		public static bool HasKey(string key)
		{
			lock (sync)
			{
				Load();
				return key != null && (ints.ContainsKey(key) || floats.ContainsKey(key) || strings.ContainsKey(key));
			}
		}

		public static void DeleteKey(string key)
		{
			lock (sync)
			{
				Load();
				if (key != null && (ints.Remove(key) | floats.Remove(key) | strings.Remove(key)))
				{
					dirty = true;
				}
			}
		}

		/// <summary>Removes every key (the file is emptied by the next <see cref="Save" />).</summary>
		public static void DeleteAll()
		{
			lock (sync)
			{
				Load();
				if (ints.Count + floats.Count + strings.Count > 0)
				{
					ints.Clear();
					floats.Clear();
					strings.Clear();
					dirty = true;
				}
			}
		}

		/// <summary>The keys there are, sorted (tools, debugging).</summary>
		public static string[] GetKeys()
		{
			lock (sync)
			{
				Load();
				return ints.Keys.Concat(floats.Keys).Concat(strings.Keys).OrderBy(k => k, StringComparer.Ordinal).ToArray();
			}
		}

		/// <summary>
		///     Writes the changes to <see cref="FilePath" /> (nothing when nothing changed): to a file beside it first, then
		///     moved over it, so a crash mid-write never leaves half a file. A failure is logged and the changes stay to be
		///     written by the next call.
		/// </summary>
		public static void Save()
		{
			lock (sync)
			{
				if (!dirty || loadedFrom == null)
				{
					return;
				}

				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(loadedFrom) ?? ".");
					string temporary = loadedFrom + ".tmp";
					File.WriteAllBytes(temporary, Write());
					File.Move(temporary, loadedFrom, true);
					dirty = false;
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[PlayerPrefs] Cannot write {loadedFrom}: {e.Message}");
				}
			}
		}

		#endregion

		#region InternalMethods

		/// <summary>Forgets what was read (<see cref="Application.persistentDataPath" /> changed; it saved first): the next call reads the new file.</summary>
		internal static void Unload()
		{
			lock (sync)
			{
				ints.Clear();
				floats.Clear();
				strings.Clear();
				loadedFrom = null;
				dirty = false;
			}
		}

		#endregion

		#region PrivateMethods

		// The key holds this value already (the file need not be written again).
		private static bool Unchanged<T>(string key, Dictionary<string, T> values, T value)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key));
			}

			Load();
			return values.TryGetValue(key, out T current) && EqualityComparer<T>.Default.Equals(current, value);
		}

		private static void Prepare(string key)
		{
			if (key == null)
			{
				throw new ArgumentNullException(nameof(key));
			}

			Load();
			ints.Remove(key);
			floats.Remove(key);
			strings.Remove(key);
			dirty = true;
		}

		private static void Load()
		{
			if (loadedFrom != null)
			{
				return;
			}

			loadedFrom = FilePath;
			if (!File.Exists(loadedFrom))
			{
				return;
			}

			try
			{
				using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(loadedFrom));
				JsonElement root = document.RootElement;
				if (root.TryGetProperty("ints", out JsonElement intValues))
				{
					foreach (JsonProperty entry in intValues.EnumerateObject())
					{
						if (entry.Value.TryGetInt32(out int value))
						{
							ints[entry.Name] = value;
						}
					}
				}

				if (root.TryGetProperty("floats", out JsonElement floatValues))
				{
					foreach (JsonProperty entry in floatValues.EnumerateObject())
					{
						if (entry.Value.TryGetSingle(out float value))
						{
							floats[entry.Name] = value;
						}
					}
				}

				if (root.TryGetProperty("strings", out JsonElement stringValues))
				{
					foreach (JsonProperty entry in stringValues.EnumerateObject())
					{
						if (entry.Value.ValueKind == JsonValueKind.String)
						{
							strings[entry.Name] = entry.Value.GetString();
						}
					}
				}
			}
			catch (Exception e)
			{
				// Kept aside, not overwritten by the next save: the values start empty.
				Debug.LogWarning($"[PlayerPrefs] Cannot read {loadedFrom} ({e.Message}): starting without its values; the file is kept as {FileName}.unreadable");
				ints.Clear();
				floats.Clear();
				strings.Clear();
				try
				{
					File.Copy(loadedFrom, loadedFrom + ".unreadable", true);
				}
				catch (Exception)
				{
					// The warning says enough.
				}
			}
		}

		// { "ints": { ... }, "floats": { ... }, "strings": { ... } }, keys sorted: the same values give the same file.
		private static byte[] Write()
		{
			using var stream = new MemoryStream();
			// Text as it is (quotes and letters like "ą" not turned into \u escapes): a local file, never HTML.
			using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
			{
				writer.WriteStartObject();
				writer.WriteStartObject("ints");
				foreach (KeyValuePair<string, int> entry in ints.OrderBy(e => e.Key, StringComparer.Ordinal))
				{
					writer.WriteNumber(entry.Key, entry.Value);
				}

				writer.WriteEndObject();
				writer.WriteStartObject("floats");
				foreach (KeyValuePair<string, float> entry in floats.OrderBy(e => e.Key, StringComparer.Ordinal))
				{
					writer.WriteNumber(entry.Key, entry.Value);
				}

				writer.WriteEndObject();
				writer.WriteStartObject("strings");
				foreach (KeyValuePair<string, string> entry in strings.OrderBy(e => e.Key, StringComparer.Ordinal))
				{
					writer.WriteString(entry.Key, entry.Value);
				}

				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			stream.WriteByte((byte)'\n');
			return stream.ToArray();
		}

		#endregion
	}
}
