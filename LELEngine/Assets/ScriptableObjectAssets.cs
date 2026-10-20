using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>
	///     The ".asset" files holding ScriptableObjects: loaded once per path and shared, saved by the editor. A load
	///     reads the stored type name first (<see cref="GetTypeName" />, also used to list and filter assets without
	///     loading them), creates the instance, caches it before its fields are read (assets referencing each other
	///     resolve to the same instances), reads the fields, then runs Awake and OnEnable (a background load holds them
	///     back for the main thread: <see cref="AssetLoadScope.DefersCallbacks" />).
	///     <para>
	///         Instances hold types of the game scripts: the editor drops them all (<see cref="UnloadAll" />, OnDisable
	///         first) before the scripts reload; they load again from their files on the next use.
	///     </para>
	/// </summary>
	public static class ScriptableObjectAssets
	{
		#region PublicFields

		public const string Extension = ".asset";

		#endregion

		#region PrivateFields

		// One lock for loading (re-entrant: an asset's fields may load other assets).
		private static readonly object sync = new object();
		private static readonly Dictionary<string, ScriptableObject> cache = new Dictionary<string, ScriptableObject>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, (long stamp, string typeName)> typeNames = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);

		// Write time of each loaded asset's file when it was last read or written here (changes made elsewhere reload).
		private static readonly Dictionary<string, DateTime> fileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		public static bool IsAssetFile(string assetPath)
		{
			return assetPath != null && assetPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>The loaded (or newly loaded) asset at a root-relative path; null when the file or its type is missing.</summary>
		public static ScriptableObject Load(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			assetPath = Normalize(assetPath);
			AssetLoadScope scope = AssetLoadScope.Current;
			bool background = scope != null && scope.DefersCallbacks;
			lock (sync)
			{
				ScriptableObject cached;
				if (cache.TryGetValue(assetPath, out cached))
				{
					if (cached.heldBack)
					{
						// Read by a background load not completed yet: this load completes with it (the first one to
						// complete runs the callbacks), a load on the main thread runs them now.
						if (background)
						{
							scope.Defer(cached);
						}
						else
						{
							EnableHeld(cached);
						}
					}

					return cached;
				}

				string file = AssetDatabase.ToAbsolute(assetPath);
				if (!File.Exists(file))
				{
					Debug.LogWarning("[Assets] ScriptableObject asset not found: " + assetPath);
					return null;
				}

				byte[] data = File.ReadAllBytes(file);
				string typeName = Documents.RootTypeName(data);
				Type type = TypeRegistry.Resolve(typeName);
				if (type == null || type.IsAbstract || !typeof(ScriptableObject).IsAssignableFrom(type))
				{
					Debug.LogWarning($"[Assets] {assetPath}: the ScriptableObject type '{typeName}' was not found (renamed, deleted, or the scripts do not compile)");
					return null;
				}

				ScriptableObject instance = ScriptableObject.CreateUninitialized(type);
				instance.AssetPath = assetPath;
				instance.name = Path.GetFileNameWithoutExtension(assetPath);
				cache[assetPath] = instance;
				fileTimes[assetPath] = File.GetLastWriteTimeUtc(file);
				ReadFields(instance, data, assetPath);
				if (background)
				{
					// Script callbacks run on the main thread: when the load completes (or a main-thread load meets it).
					instance.heldBack = true;
					scope.Defer(instance);
				}
				else
				{
					instance.Enable(true);
				}

				return instance;
			}
		}

		/// <summary>Main thread: Awake and OnEnable of an asset a background load read (nothing when they ran already or it was unloaded).</summary>
		internal static void EnableHeld(ScriptableObject asset)
		{
			lock (sync)
			{
				if (asset == null || !asset.heldBack)
				{
					return;
				}

				asset.heldBack = false;
			}

			asset.Enable(true);
		}

		/// <summary>
		///     Reads again the loaded assets whose files changed since they were read or saved here (a checkout, a text
		///     editor) and runs their OnValidate; returns how many were reloaded.
		/// </summary>
		public static int ReloadChanged()
		{
			var changed = new List<ScriptableObject>();
			lock (sync)
			{
				foreach (KeyValuePair<string, ScriptableObject> entry in cache)
				{
					string file = AssetDatabase.ToAbsolute(entry.Key);
					DateTime known;
					if (File.Exists(file) && fileTimes.TryGetValue(entry.Key, out known) && File.GetLastWriteTimeUtc(file) != known)
					{
						changed.Add(entry.Value);
					}
				}

				foreach (ScriptableObject asset in changed)
				{
					string file = AssetDatabase.ToAbsolute(asset.AssetPath);
					fileTimes[asset.AssetPath] = File.GetLastWriteTimeUtc(file);
					ReadFields(asset, File.ReadAllBytes(file), asset.AssetPath);
				}
			}

			foreach (ScriptableObject asset in changed)
			{
				asset.Validate();
			}

			return changed.Count;
		}

		/// <summary>The loaded instance of an asset, or null when it is not loaded (nothing is read).</summary>
		public static ScriptableObject GetLoaded(string assetPath)
		{
			lock (sync)
			{
				ScriptableObject cached;
				return assetPath != null && cache.TryGetValue(Normalize(assetPath), out cached) ? cached : null;
			}
		}

		/// <summary>Every loaded asset instance.</summary>
		public static List<ScriptableObject> GetLoadedAssets()
		{
			lock (sync)
			{
				return new List<ScriptableObject>(cache.Values);
			}
		}

		/// <summary>
		///     The type name stored in an asset file ("Namespace.Type, Assembly") without loading it; cached while the
		///     file stays the same. Null when the file is missing or not an object document.
		/// </summary>
		public static string GetTypeName(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			assetPath = Normalize(assetPath);
			string file = AssetDatabase.ToAbsolute(assetPath);
			long stamp;
			try
			{
				var info = new FileInfo(file);
				if (!info.Exists)
				{
					return null;
				}

				stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
			}
			catch (Exception)
			{
				return null;
			}

			lock (typeNames)
			{
				(long stamp, string typeName) entry;
				if (typeNames.TryGetValue(assetPath, out entry) && entry.stamp == stamp)
				{
					return entry.typeName;
				}
			}

			string name;
			try
			{
				name = Documents.RootTypeName(File.ReadAllBytes(file));
			}
			catch (Exception)
			{
				name = null;
			}

			lock (typeNames)
			{
				typeNames[assetPath] = (stamp, name);
			}

			return name;
		}

		/// <summary>The live type stored in an asset file, or null (missing file or script).</summary>
		public static Type GetStoredType(string assetPath)
		{
			ScriptableObject loaded = GetLoaded(assetPath);
			if (loaded != null)
			{
				return loaded.GetType();
			}

			Type type = TypeRegistry.Resolve(GetTypeName(assetPath));
			return type != null && typeof(ScriptableObject).IsAssignableFrom(type) ? type : null;
		}

		/// <summary>True when the asset file holds an object assignable to <paramref name="type" /> (a class or an interface it implements).</summary>
		public static bool IsOfType(string assetPath, Type type)
		{
			Type stored = GetStoredType(assetPath);
			return stored != null && type != null && type.IsAssignableFrom(stored);
		}

		/// <summary>Writes an asset to its file (<paramref name="assetPath" />: a new file for an instance made in code) and caches it.</summary>
		public static void Save(ScriptableObject asset, string assetPath = null)
		{
			if (asset is null)
			{
				throw new ArgumentNullException(nameof(asset));
			}

			assetPath = Normalize(assetPath ?? asset.AssetPath ?? throw new InvalidOperationException("The ScriptableObject is not an asset: give it a path"));
			lock (sync)
			{
				if (asset.AssetPath != null && !string.Equals(asset.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase))
				{
					ScriptableObject previous;
					if (cache.TryGetValue(asset.AssetPath, out previous) && ReferenceEquals(previous, asset))
					{
						cache.Remove(asset.AssetPath);
					}
				}

				asset.AssetPath = assetPath;
				asset.name = Path.GetFileNameWithoutExtension(assetPath);
				string file = AssetDatabase.ToAbsolute(assetPath);
				Documents.WriteObjectFile(file, asset);
				cache[assetPath] = asset;
				fileTimes[assetPath] = File.GetLastWriteTimeUtc(file);
			}

			// A new asset gets its GUID now: references saved right after it (other assets, the scene) use it.
			AssetDatabase.EnsureMeta(assetPath);
		}

		/// <summary>
		///     Reads an asset's file again into its loaded instance (an undo, a change made outside the editor); nothing
		///     happens when it is not loaded. Returns the instance.
		/// </summary>
		public static ScriptableObject Reload(string assetPath)
		{
			ScriptableObject loaded = GetLoaded(assetPath);
			if (loaded == null)
			{
				return null;
			}

			lock (sync)
			{
				ReadFields(loaded, File.ReadAllBytes(AssetDatabase.ToAbsolute(loaded.AssetPath)), loaded.AssetPath);
			}

			return loaded;
		}

		/// <summary>Reads a serialized state (an undo record) into the instance; the caller saves it.</summary>
		public static void Apply(ScriptableObject asset, byte[] data)
		{
			lock (sync)
			{
				ReadFields(asset, data, asset.AssetPath ?? asset.name);
			}
		}

		/// <summary>Drops an asset from the cache (a destroyed or deleted asset); the next load reads its file again.</summary>
		public static void Forget(ScriptableObject asset)
		{
			if (asset?.AssetPath == null)
			{
				return;
			}

			lock (sync)
			{
				ScriptableObject cached;
				if (cache.TryGetValue(asset.AssetPath, out cached) && ReferenceEquals(cached, asset))
				{
					cache.Remove(asset.AssetPath);
				}
			}
		}

		/// <summary>
		///     Runs OnDisable on every loaded asset and drops them all (before the game scripts unload, when the project
		///     closes); they load again from their files on the next use.
		/// </summary>
		public static void UnloadAll()
		{
			List<ScriptableObject> loaded;
			lock (sync)
			{
				loaded = new List<ScriptableObject>(cache.Values);
				foreach (ScriptableObject asset in loaded)
				{
					// A background load still holding its callbacks back drops them.
					asset.heldBack = false;
				}

				cache.Clear();
				fileTimes.Clear();
			}

			foreach (ScriptableObject asset in loaded)
			{
				asset.Disable();
			}

			lock (typeNames)
			{
				typeNames.Clear();
			}
		}

		/// <summary>
		///     The assets the object's serialized fields reference, nested objects and collections included (a
		///     ScriptableObject's textures, meshes, other assets): scene loads record them as used by the scene.
		/// </summary>
		public static void CollectReferencedAssets(object target, Action<object> found)
		{
			if (target != null)
			{
				CollectFields(target, found, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
			}
		}

		#endregion

		#region PrivateMethods

		private static void ReadFields(ScriptableObject instance, byte[] data, string location)
		{
			try
			{
				Documents.ReadObject(data, instance.GetType(), instance, new SerializationContext { Location = location });
			}
			catch (Exception e)
			{
				Debug.LogError($"[Assets] Cannot read {location}: {e.Message}");
			}
		}

		private static void CollectFields(object target, Action<object> found, int depth, HashSet<object> visited)
		{
			if (depth > SerializationUtility.MaxDepth || !visited.Add(target))
			{
				return;
			}

			foreach (SerializedField field in SerializationUtility.GetFields(target.GetType()))
			{
				if (field.Tag == ValueTag.Asset || field.Tag == ValueTag.Object || field.Tag == ValueTag.Array || field.Tag == ValueTag.Dictionary)
				{
					CollectValue(field.GetValue(target), found, depth, visited);
				}
			}
		}

		private static void CollectValue(object value, Action<object> found, int depth, HashSet<object> visited)
		{
			switch (value)
			{
				case null:
				case string _:
				case GameObject _:
				case Behaviour _:
					return;
				case IAsset asset:
					found(asset);
					return;
				case IDictionary dictionary:
					foreach (DictionaryEntry entry in dictionary)
					{
						CollectValue(entry.Key, found, depth + 1, visited);
						CollectValue(entry.Value, found, depth + 1, visited);
					}

					return;
				case IList list:
				{
					Type element = SerializationUtility.GetElementType(value.GetType());
					if (element == null || !(element.IsPrimitive || element.IsEnum || element == typeof(string) || SerializationUtility.IsMathType(element)))
					{
						foreach (object item in list)
						{
							CollectValue(item, found, depth + 1, visited);
						}
					}

					return;
				}
			}

			Type type = value.GetType();
			if (!type.IsPrimitive && !type.IsEnum && !SerializationUtility.IsMathType(type))
			{
				CollectFields(value, found, depth + 1, visited);
			}
		}

		private static string Normalize(string path)
		{
			return path.Replace('\\', '/').TrimStart('/');
		}

		#endregion
	}
}
