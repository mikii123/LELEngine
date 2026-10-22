using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     A prefab file (.prefab): a GameObject hierarchy saved as an asset, instantiated any number of times (in the
	///     editor and by scripts: <see cref="Instantiate(Transform)" />). A prefab may contain instances of other
	///     prefabs (nested prefabs), and a prefab whose root is an instance of another prefab is a variant of it: it
	///     keeps everything of its base and stores only what it changes. Instances in scenes store the same kind of
	///     changes (overrides), so a change of the prefab reaches every instance and variant that does not override it.
	///     <para>
	///         The file is a scene document of kind "Prefab" (<see cref="SceneSerializer" />): its objects with one root
	///         and its nested prefab instances. A field of type <see cref="Prefab" /> references a prefab file.
	///     </para>
	/// </summary>
	public sealed class Prefab : IAsset
	{
		#region PublicFields

		public const string Extension = ".prefab";

		public string AssetPath { get; internal set; }

		public string name => Path.GetFileNameWithoutExtension(AssetPath);

		/// <summary>The prefab's root is an instance of another prefab (<see cref="BasePrefab" />).</summary>
		public bool IsVariant => Resolved?.Base != null;

		/// <summary>The prefab a variant is based on (null for other prefabs).</summary>
		public Prefab BasePrefab
		{
			get
			{
				NestedPrefab basePrefab = Resolved?.Base;
				return basePrefab != null ? Load(basePrefab.PrefabPath) : null;
			}
		}

		#endregion

		#region InternalFields

		/// <summary>The prefab with its nested prefabs and variant bases expanded (cached until a file it is built from changes).</summary>
		internal ResolvedPrefab Resolved => PrefabCache.Get(AssetPath);

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly Dictionary<string, Prefab> loaded = new Dictionary<string, Prefab>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region Constructors

		private Prefab(string assetPath)
		{
			AssetPath = assetPath;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     The prefab at an asset path; with <paramref name="resolve" /> its nested prefabs and variant bases are read
		///     now (a background load: Resources.LoadAsync, a scene loading in the background) instead of at the first
		///     instantiation.
		/// </summary>
		public static Prefab Load(string assetPath, bool resolve)
		{
			Prefab prefab = Load(assetPath);
			if (resolve && prefab != null)
			{
				_ = prefab.Resolved;
			}

			return prefab;
		}

		/// <summary>The prefab at an asset path (one object per file), or null when there is no such file.</summary>
		public static Prefab Load(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			string path = PrefabCache.Normalize(assetPath);
			lock (sync)
			{
				if (loaded.TryGetValue(path, out Prefab prefab))
				{
					return prefab;
				}

				if (!File.Exists(AssetDatabase.ToAbsolute(path)))
				{
					return null;
				}

				prefab = new Prefab(path);
				loaded[path] = prefab;
				return prefab;
			}
		}

		/// <summary>
		///     Creates an instance in the scene of <paramref name="parent" /> (the active scene without a parent), its
		///     root under <paramref name="parent" /> with the prefab's local transform. The components' Awake sees the
		///     prefab's values; in a playing scene the lifecycle starts at once.
		/// </summary>
		public GameObject Instantiate(Transform parent = null)
		{
			return Instantiate(parent?.gameObject.scene ?? SceneManager.ActiveScene, parent, null, null);
		}

		/// <summary>Creates an instance with its root at a world position and rotation.</summary>
		public GameObject Instantiate(Vector3 position, Quaternion rotation, Transform parent = null)
		{
			return Instantiate(parent?.gameObject.scene ?? SceneManager.ActiveScene, parent, position, rotation);
		}

		/// <summary>Creates an instance in a given scene.</summary>
		public GameObject Instantiate(Scene scene, Transform parent = null)
		{
			return Instantiate(scene, parent, null, null);
		}

		public override string ToString()
		{
			return name;
		}

		#endregion

		#region InternalMethods

		/// <summary>A file or folder moved: the loaded prefabs and their resolved data keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (sync)
			{
				AssetDatabase.RekeyMoved(loaded, fromPath, toPath, (prefab, path) => prefab.AssetPath = path);
			}

			PrefabCache.AssetMoved(fromPath, toPath);
		}

		/// <summary>Drops the objects of prefab files that no longer exist (deleted or moved).</summary>
		internal static void ForgetMissing(IEnumerable<string> assetPaths)
		{
			lock (sync)
			{
				foreach (string path in assetPaths)
				{
					if (path != null && !File.Exists(AssetDatabase.ToAbsolute(path)))
					{
						loaded.Remove(PrefabCache.Normalize(path));
					}
				}
			}
		}

		#endregion

		#region PrivateMethods

		private GameObject Instantiate(Scene scene, Transform parent, Vector3? position, Quaternion? rotation)
		{
			if (scene == null)
			{
				throw new InvalidOperationException("Prefab.Instantiate: there is no scene to instantiate '" + name + "' into.");
			}

			ResolvedPrefab resolved = Resolved;
			if (resolved?.Root == null)
			{
				Debug.LogError($"[Prefab] Cannot instantiate {AssetPath}: {resolved?.Error ?? "it cannot be read"}");
				return null;
			}

			PrefabInstance instance = SceneSerializer.InstantiatePrefab(this, resolved, scene, parent, root =>
			{
				if (position.HasValue)
				{
					root.transform.position = position.Value;
				}

				if (rotation.HasValue)
				{
					root.transform.rotation = rotation.Value;
				}
			});
			return instance?.Root;
		}

		#endregion
	}
}
