using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine
{
	/// <summary>
	///     Loads assets by name from the project's Resources folders (Unity's Resources): any folder named "Resources"
	///     (Assets/Resources, Assets/Enemies/Resources, ...). The path is relative to that folder, without the file's
	///     extension: Assets/Resources/Enemies/Goblin.asset loads with <c>Resources.Load&lt;EnemyConfig&gt;("Enemies/Goblin")</c>.
	///     <para>
	///         Game builds include only the assets their scenes use, plus everything in Resources folders (and what
	///         that uses): an asset a script loads by name must lie in a Resources folder.
	///     </para>
	///     <para>
	///         <see cref="LoadAsync{T}" /> loads in the background (<see cref="ResourceRequest" />): files, decoding and
	///         parsing on the loading thread, only the GPU uploads on the main thread, a few milliseconds per frame.
	///     </para>
	/// </summary>
	public static class Resources
	{
		#region PublicFields

		/// <summary>Name of the folders whose assets load by name and always go into builds.</summary>
		public const string FolderName = "Resources";

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();

		// Resource path (no extension, lower case) -> asset paths; rebuilt after the asset index changes.
		private static Dictionary<string, List<string>> index;

		#endregion

		#region Constructors

		static Resources()
		{
			AssetDatabase.Refreshed += () =>
			{
				lock (sync)
				{
					index = null;
				}
			};
		}

		#endregion

		#region PublicMethods

		/// <summary>The asset at <paramref name="path" /> (relative to a Resources folder, no extension) loaded as <typeparamref name="T" />, or null.</summary>
		public static T Load<T>(string path)
			where T : class
		{
			return Load(path, typeof(T)) as T;
		}

		/// <summary>The asset at <paramref name="path" /> loaded as <paramref name="type" /> (a class or an interface it implements), or null.</summary>
		public static object Load(string path, Type type)
		{
			foreach (string assetPath in FindAssetPaths(path))
			{
				object asset = LoadAs(assetPath, type);
				if (asset != null)
				{
					return asset;
				}
			}

			return null;
		}

		/// <summary>The asset at <paramref name="path" /> as what its file is (a texture, a material, a ScriptableObject...), or null.</summary>
		public static object Load(string path)
		{
			foreach (string assetPath in FindAssetPaths(path))
			{
				Type type = AssetDatabase.GetAssetType(assetPath);
				if (type != null)
				{
					object asset = AssetDatabase.Load(type, assetPath);
					if (asset != null)
					{
						return asset;
					}
				}
			}

			return null;
		}

		/// <summary>
		///     Loads the asset at <paramref name="path" /> as <typeparamref name="T" /> in the background; await the request
		///     or read it when <see cref="ResourceRequest.IsDone" /> (see <see cref="ResourceRequest" />). Main thread.
		/// </summary>
		public static ResourceRequest<T> LoadAsync<T>(string path)
			where T : class
		{
			var request = new ResourceRequest<T>(path);
			SceneManager.QueueResourceRequest(request);
			return request;
		}

		/// <summary>Loads the asset at <paramref name="path" /> as <paramref name="type" /> in the background (see <see cref="LoadAsync{T}" />).</summary>
		public static ResourceRequest LoadAsync(string path, Type type)
		{
			var request = new ResourceRequest(path, type ?? throw new ArgumentNullException(nameof(type)));
			SceneManager.QueueResourceRequest(request);
			return request;
		}

		/// <summary>Loads the asset at <paramref name="path" /> as what its file is, in the background (see <see cref="LoadAsync{T}" />).</summary>
		public static ResourceRequest LoadAsync(string path)
		{
			var request = new ResourceRequest(path, null);
			SceneManager.QueueResourceRequest(request);
			return request;
		}

		/// <summary>Every asset loadable as <typeparamref name="T" /> in the folder <paramref name="folder" /> (relative to the Resources folders; "" for all) and below.</summary>
		public static T[] LoadAll<T>(string folder)
			where T : class
		{
			var result = new List<T>();
			foreach (string assetPath in FindAssetPathsIn(folder))
			{
				if (LoadAs(assetPath, typeof(T)) is T asset)
				{
					result.Add(asset);
				}
			}

			return result.ToArray();
		}

		/// <summary>True when the asset lies in a Resources folder (it goes into every build).</summary>
		public static bool IsInResources(string assetPath)
		{
			return ResourcePathOf(assetPath) != null;
		}

		/// <summary>The path an asset loads with (relative to its Resources folder, no extension), or null outside Resources folders.</summary>
		public static string ResourcePathOf(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			string normalized = assetPath.Replace('\\', '/');
			string marker = FolderName + "/";
			int start = -1;
			if (normalized.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
			{
				start = marker.Length;
			}

			int inner = normalized.LastIndexOf("/" + marker, StringComparison.OrdinalIgnoreCase);
			if (inner >= 0)
			{
				start = inner + marker.Length + 1;
			}

			if (start < 0 || start >= normalized.Length)
			{
				return null;
			}

			string relative = normalized.Substring(start);
			string extension = Path.GetExtension(relative);
			return extension.Length > 0 ? relative.Substring(0, relative.Length - extension.Length) : relative;
		}

		#endregion

		#region PrivateMethods

		private static object LoadAs(string assetPath, Type type)
		{
			if (!AssetDatabase.AcceptsFile(type, assetPath))
			{
				return null;
			}

			object asset = AssetDatabase.Load(type, assetPath);
			return asset != null && type.IsInstanceOfType(asset) ? asset : null;
		}

		private static List<string> FindAssetPaths(string path)
		{
			string key = NormalizeKey(path);
			Dictionary<string, List<string>> current = GetIndex();
			List<string> paths;
			if (current.TryGetValue(key, out paths))
			{
				return paths;
			}

			// With the extension given ("Enemies/Goblin.asset").
			string extension = Path.GetExtension(key);
			if (extension.Length > 0 && current.TryGetValue(key.Substring(0, key.Length - extension.Length), out paths))
			{
				return paths.FindAll(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
			}

			return new List<string>();
		}

		private static List<string> FindAssetPathsIn(string folder)
		{
			string prefix = NormalizeKey(folder);
			var result = new List<string>();
			foreach (KeyValuePair<string, List<string>> entry in GetIndex())
			{
				if (prefix.Length == 0 || entry.Key == prefix || entry.Key.StartsWith(prefix + "/", StringComparison.Ordinal))
				{
					result.AddRange(entry.Value);
				}
			}

			result.Sort(StringComparer.OrdinalIgnoreCase);
			return result;
		}

		private static Dictionary<string, List<string>> GetIndex()
		{
			lock (sync)
			{
				if (index != null)
				{
					return index;
				}

				var built = new Dictionary<string, List<string>>(StringComparer.Ordinal);
				foreach (string assetPath in AssetDatabase.GetAllAssetPaths())
				{
					string resourcePath = ResourcePathOf(assetPath);
					if (resourcePath == null)
					{
						continue;
					}

					string key = NormalizeKey(resourcePath);
					List<string> paths;
					if (!built.TryGetValue(key, out paths))
					{
						paths = new List<string>();
						built[key] = paths;
					}

					paths.Add(assetPath);
				}

				index = built;
				return index;
			}
		}

		private static string NormalizeKey(string path)
		{
			return (path ?? "").Replace('\\', '/').Trim('/').ToLowerInvariant();
		}

		#endregion
	}
}
