using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>An object loaded from an asset file (mesh, material, texture, ...). Scenes reference it by the file's GUID.</summary>
	public interface IAsset
	{
		/// <summary>Path of the source file relative to <see cref="AssetDatabase.Root" /> ("/" separated), null for runtime objects.</summary>
		string AssetPath { get; }

		/// <summary>Name of this object inside its file when the file holds several (a sprite of a sheet); null for the main asset.</summary>
		string SubAssetName => null;
	}

	/// <summary>
	///     Asset files and their identities. Every asset file has a sidecar "file.ext.meta" (an <see cref="AssetMeta" />
	///     document) holding its GUID and import settings, so references survive renames and moves (the meta file
	///     moves with it). The root is the folder all asset paths are relative to: a project's Assets folder in the
	///     editor, the Data folder of a build, or the executable's folder for the legacy layout (Meshes/, Materials/,
	///     Textures/, Shaders/ next to the exe).
	///
	///     Asset types register their file extensions and loader (<see cref="RegisterAssetType" />); scenes and
	///     materials reference assets through <see cref="Load" /> / <see cref="LoadByGuid" />. Engine shaders (the GLSL
	///     library and engine passes) are engine resources, not project assets: they are always looked up in
	///     <see cref="EngineShadersRoot" /> first.
	/// </summary>
	public static class AssetDatabase
	{
		#region PublicFields

		public const string MetaExtension = ".meta";

		/// <summary>
		///     The GUID index a game build writes into its asset root (<see cref="WriteIndex" />): read instead of every
		///     meta file when the root is indexed (a dot file: never an asset itself).
		/// </summary>
		public const string IndexFileName = ".assetindex";

		/// <summary>Absolute path of the asset root.</summary>
		public static string Root
		{
			get
			{
				EnsureInitialized();
				return root;
			}
		}

		/// <summary>Folder holding the engine's shader library (Shaders/Engine/...), next to the engine binaries.</summary>
		public static string EngineShadersRoot => Path.Combine(AppContext.BaseDirectory, "Shaders");

		/// <summary>Raised after the index was rebuilt (files added, moved or deleted).</summary>
		public static event Action Refreshed;

		#endregion

		#region PrivateFields

		private sealed class AssetTypeInfo
		{
			public Type Type;
			public string[] Extensions;
			public Func<Type, string, string, object> Loader;
		}

		private static readonly object sync = new object();
		private static string root;
		private static readonly Dictionary<string, string> pathsByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, string> guidsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, List<string>> pathsByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, AssetMeta> metaCache = new Dictionary<string, AssetMeta>(StringComparer.OrdinalIgnoreCase);
		private static readonly List<AssetTypeInfo> assetTypes = new List<AssetTypeInfo>();
		private static readonly byte[] IndexMagic = System.Text.Encoding.ASCII.GetBytes("LELIDX01");

		#endregion

		#region Constructors

		static AssetDatabase()
		{
			RegisterBuiltinTypes();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Sets the asset root and indexes its meta files. With <paramref name="createMissingMeta" /> every asset
		///     file without a meta file gets one (editor); builds and players only read.
		/// </summary>
		public static void Initialize(string assetRoot, bool createMissingMeta = false)
		{
			lock (sync)
			{
				root = Path.GetFullPath(assetRoot);
				Rebuild(createMissingMeta);
			}

			Refreshed?.Invoke();
		}

		/// <summary>Re-reads the meta files under the root (after files were added, moved or deleted).</summary>
		public static void Refresh(bool createMissingMeta = false)
		{
			lock (sync)
			{
				EnsureInitialized();
				Rebuild(createMissingMeta);
			}

			Refreshed?.Invoke();
		}

		// ---------------------------------------------------------------- asset types

		/// <summary>
		///     Registers a type loadable from files with the given extensions. The loader receives the requested type
		///     (the registered type or a base of it), the root-relative path and the sub-asset name (or null).
		/// </summary>
		public static void RegisterAssetType(Type type, string[] extensions, Func<Type, string, string, object> loader)
		{
			lock (sync)
			{
				assetTypes.RemoveAll(info => info.Type == type);
				assetTypes.Add(new AssetTypeInfo { Type = type, Extensions = extensions ?? Array.Empty<string>(), Loader = loader });
			}
		}

		/// <summary>
		///     File extensions of the assets a field of this type accepts (empty when the type is not an asset type). A
		///     ScriptableObject type, an interface or <see cref="EngineObject" /> accepts ".asset" files: which of them fit
		///     depends on the type each one stores (<see cref="AcceptsFile" />, <see cref="FindAssets" /> check it).
		/// </summary>
		public static string[] GetExtensions(Type type)
		{
			lock (sync)
			{
				foreach (AssetTypeInfo info in assetTypes)
				{
					if (info.Type == type)
					{
						return info.Extensions;
					}
				}

				// A base type (Texture, IAsset): every registered type assignable to it.
				var union = new List<string>();
				foreach (AssetTypeInfo info in assetTypes)
				{
					if (type.IsAssignableFrom(info.Type))
					{
						foreach (string extension in info.Extensions)
						{
							if (!union.Contains(extension))
							{
								union.Add(extension);
							}
						}
					}
				}

				// A ScriptableObject class, or a slot a ScriptableObject may fill.
				if ((typeof(ScriptableObject).IsAssignableFrom(type) || Serialization.SerializationUtility.CanHoldReference(type)) && !union.Contains(ScriptableObjectAssets.Extension))
				{
					union.Add(ScriptableObjectAssets.Extension);
				}

				return union.ToArray();
			}
		}

		/// <summary>The registered asset type whose extensions include the file's, or null (scripts, unknown files).</summary>
		public static Type GetAssetType(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			lock (sync)
			{
				foreach (AssetTypeInfo info in assetTypes)
				{
					foreach (string extension in info.Extensions)
					{
						if (assetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
						{
							return info.Type;
						}
					}
				}
			}

			return null;
		}

		/// <summary>
		///     True when the file loads as the given type (or a type assignable to it): by its extension, and for
		///     ScriptableObject assets by the type the file stores.
		/// </summary>
		public static bool AcceptsFile(Type type, string assetPath)
		{
			if (type == null || string.IsNullOrEmpty(assetPath))
			{
				return false;
			}

			foreach (string extension in GetExtensions(type))
			{
				if (assetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				{
					return ContentMatches(type, assetPath);
				}
			}

			return false;
		}

		/// <summary>
		///     Loads the file as what it is: its registered asset type (a ScriptableObject asset as its stored class), or
		///     a sub-asset of it by name. Null for files that are no asset (scripts, unknown files).
		/// </summary>
		public static object LoadMainAsset(string assetPath, string subAsset = null)
		{
			Type type = GetAssetType(assetPath);
			return type != null ? Load(type, assetPath, subAsset) : null;
		}

		// ---------------------------------------------------------------- identity

		/// <summary>GUID of the asset at a root-relative path, or null when it has no meta file.</summary>
		public static string GetGuid(string assetPath)
		{
			if (assetPath == null)
			{
				return null;
			}

			lock (sync)
			{
				EnsureInitialized();
				string guid;
				return guidsByPath.TryGetValue(Normalize(assetPath), out guid) ? guid : null;
			}
		}

		/// <summary>Root-relative path of the asset with this GUID, or null.</summary>
		public static string GetPath(string guid)
		{
			if (string.IsNullOrEmpty(guid))
			{
				return null;
			}

			lock (sync)
			{
				EnsureInitialized();
				string path;
				return pathsByGuid.TryGetValue(guid, out path) ? path : null;
			}
		}

		public static string ToAbsolute(string assetPath)
		{
			return Path.Combine(Root, assetPath.Replace('/', Path.DirectorySeparatorChar));
		}

		/// <summary>Root-relative path of an absolute path under the root, or null when it is outside.</summary>
		public static string ToAssetPath(string absolutePath)
		{
			string full = Path.GetFullPath(absolutePath);
			string rootFull = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return Normalize(full.Substring(rootFull.Length));
		}

		/// <summary>
		///     The engine shader standing in for a project shader that is not there ("Shaders/Lit.shader" ->
		///     "Engine/Lit.shader"): the lit shaders the GI depends on moved into the engine, and materials of older
		///     projects still name their project copies. Null when the engine has no shader of that name.
		/// </summary>
		public static string EngineShaderFallback(string path)
		{
			if (string.IsNullOrEmpty(path) || !path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			string engine = "Engine/" + Path.GetFileName(path.Replace('\\', '/'));
			return IsEngineResource(engine) ? engine : null;
		}

		/// <summary>True when the path names an engine resource shipped next to the binaries (engine shaders) rather than a project asset.</summary>
		public static bool IsEngineResource(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}

			try
			{
				return File.Exists(Path.Combine(EngineShadersRoot, path.Replace('/', Path.DirectorySeparatorChar)));
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>
		///     Finds an asset file from a root-relative path, a path relative to the legacy type folder
		///     (<paramref name="legacyFolder" />, e.g. "Meshes"), or a bare file name that is unique under the root.
		///     Returns the root-relative path, or null.
		/// </summary>
		public static string Resolve(string nameOrPath, string legacyFolder)
		{
			if (string.IsNullOrWhiteSpace(nameOrPath))
			{
				return null;
			}

			string normalized = Normalize(nameOrPath);
			if (File.Exists(ToAbsolute(normalized)))
			{
				return normalized;
			}

			if (legacyFolder != null)
			{
				string legacy = legacyFolder + "/" + normalized;
				if (File.Exists(ToAbsolute(legacy)))
				{
					return legacy;
				}
			}

			lock (sync)
			{
				List<string> candidates;
				if (pathsByFileName.TryGetValue(Path.GetFileName(normalized), out candidates) && candidates.Count > 0)
				{
					if (candidates.Count > 1)
					{
						Debug.LogWarning($"[Assets] '{nameOrPath}' is ambiguous ({string.Join(", ", candidates)}); using {candidates[0]}");
					}

					return candidates[0];
				}
			}

			return null;
		}

		/// <summary>Every indexed asset path (assets with a meta file), optionally only those with one of the extensions.</summary>
		public static List<string> GetAllAssetPaths(params string[] extensions)
		{
			lock (sync)
			{
				EnsureInitialized();
				var result = new List<string>();
				foreach (string path in guidsByPath.Keys)
				{
					if (extensions == null || extensions.Length == 0)
					{
						result.Add(path);
						continue;
					}

					foreach (string extension in extensions)
					{
						if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
						{
							result.Add(path);
							break;
						}
					}
				}

				result.Sort(StringComparer.OrdinalIgnoreCase);
				return result;
			}
		}

		/// <summary>Indexed asset paths of files loadable as the type (ScriptableObject assets: those storing a fitting type).</summary>
		public static List<string> FindAssets(Type type)
		{
			string[] extensions = GetExtensions(type);
			if (extensions.Length == 0)
			{
				return new List<string>();
			}

			List<string> paths = GetAllAssetPaths(extensions);
			paths.RemoveAll(path => !ContentMatches(type, path));
			return paths;
		}

		/// <summary>A ScriptableObject asset fits a type when the class it stores does; other files fit by their extension.</summary>
		private static bool ContentMatches(Type type, string assetPath)
		{
			return !ScriptableObjectAssets.IsAssetFile(assetPath) || ScriptableObjectAssets.IsOfType(assetPath, type);
		}

		/// <summary>
		///     Writes the GUID index of the assets <paramref name="include" /> accepts to <paramref name="file" /> (a game
		///     build's <see cref="IndexFileName" />): the player reads it instead of every meta file.
		/// </summary>
		public static void WriteIndex(string file, Func<string, bool> include)
		{
			var entries = new List<KeyValuePair<string, string>>();
			lock (sync)
			{
				EnsureInitialized();
				foreach (KeyValuePair<string, string> entry in pathsByGuid)
				{
					if (include == null || include(entry.Value))
					{
						entries.Add(entry);
					}
				}
			}

			using (var writer = new BinaryWriter(File.Create(file)))
			{
				writer.Write(IndexMagic);
				writer.Write(entries.Count);
				foreach (KeyValuePair<string, string> entry in entries)
				{
					writer.Write(entry.Key);
					writer.Write(entry.Value);
				}
			}
		}

		// ---------------------------------------------------------------- meta files

		/// <summary>Creates the meta file of an asset if it is missing; returns the asset's GUID.</summary>
		public static string EnsureMeta(string assetPath)
		{
			string normalized = Normalize(assetPath);
			string existing = GetGuid(normalized);
			if (existing != null)
			{
				return existing;
			}

			var meta = new AssetMeta { guid = Guid.NewGuid().ToString("N") };
			WriteMeta(ToAbsolute(normalized) + MetaExtension, meta);
			lock (sync)
			{
				Register(normalized, meta.guid);
				metaCache[normalized] = meta;
			}

			return meta.guid;
		}

		/// <summary>Creates missing meta files for every asset file below a folder (any folder, not only the root).</summary>
		public static int CreateMissingMetaFiles(string folder)
		{
			int created = 0;
			foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				if (!IsAssetFile(file) || File.Exists(file + MetaExtension))
				{
					continue;
				}

				WriteMeta(file + MetaExtension, new AssetMeta { guid = Guid.NewGuid().ToString("N") });
				created++;
			}

			return created;
		}

		/// <summary>The meta document of an asset (GUID and import settings); a default one for files without a meta file.</summary>
		public static AssetMeta GetMeta(string assetPath)
		{
			string normalized = Normalize(assetPath);
			lock (sync)
			{
				EnsureInitialized();
				AssetMeta meta;
				if (metaCache.TryGetValue(normalized, out meta))
				{
					return meta;
				}

				string file = ToAbsolute(normalized) + MetaExtension;
				meta = File.Exists(file) ? ReadMeta(file) : null;
				meta = meta ?? new AssetMeta { guid = GetGuid(normalized) };
				metaCache[normalized] = meta;
				return meta;
			}
		}

		/// <summary>
		///     Import settings of an asset: the importer stored in its meta file, or a new default importer of
		///     <typeparamref name="T" /> (not saved until <see cref="SaveMeta" />).
		/// </summary>
		public static T GetImporter<T>(string assetPath)
			where T : AssetImporter, new()
		{
			AssetMeta meta = GetMeta(assetPath);
			T importer = meta.importer as T;
			if (importer == null)
			{
				importer = new T();
				meta.importer = importer;
			}

			return importer;
		}

		/// <summary>Writes an asset's meta document (import settings changed in the editor).</summary>
		public static void SaveMeta(string assetPath, AssetMeta meta)
		{
			string normalized = Normalize(assetPath);
			if (string.IsNullOrEmpty(meta.guid))
			{
				meta.guid = GetGuid(normalized) ?? Guid.NewGuid().ToString("N");
			}

			WriteMeta(ToAbsolute(normalized) + MetaExtension, meta);
			lock (sync)
			{
				Register(normalized, meta.guid);
				metaCache[normalized] = meta;
			}
		}

		// ---------------------------------------------------------------- loading

		/// <summary>Loads (or returns the cached) asset of the given type from a root-relative path.</summary>
		public static object Load(Type type, string assetPath)
		{
			return Load(type, assetPath, null);
		}

		/// <summary>Loads an asset, or one of its sub-assets by name (a sprite of a sheet).</summary>
		public static object Load(Type type, string assetPath, string subAsset)
		{
			if (assetPath == null || type == null)
			{
				return null;
			}

			AssetTypeInfo info = FindLoader(type, assetPath);
			if (info == null && (type.IsInterface || type == typeof(EngineObject)))
			{
				// An interface / EngineObject slot: the file loads as what it is and must implement the slot's type.
				info = FindLoader(GetAssetType(assetPath), assetPath);
			}

			if (info == null)
			{
				Debug.LogWarning($"[Assets] No loader for {type.Name} ({assetPath})");
				return null;
			}

			// A scene load records what it uses (AssetLoadScope).
			AssetLoadScope scope = AssetLoadScope.Current;
			long start = scope?.BeginLoad() ?? 0;
			object result = null;
			try
			{
				result = info.Loader(type, Normalize(assetPath), subAsset);
				if (result != null && !type.IsInstanceOfType(result))
				{
					Debug.LogWarning($"[Assets] {assetPath} loaded as {result.GetType().Name}, which is not a {type.Name}");
					result = null;
				}

				return result;
			}
			catch (Exception e)
			{
				Debug.LogError($"[Assets] Cannot load {assetPath} as {type.Name}: {e.Message}");
				return null;
			}
			finally
			{
				scope?.EndLoad(start, type, result);
			}
		}

		public static T Load<T>(string assetPath, string subAsset = null)
			where T : class
		{
			return Load(typeof(T), assetPath, subAsset) as T;
		}

		/// <summary>Loads an asset by GUID; falls back to the path hint when the GUID is unknown.</summary>
		public static object LoadByGuid(Type type, string guid, string pathHint, string subAsset = null)
		{
			string path = GetPath(guid);
			if (path == null && pathHint != null)
			{
				path = Resolve(pathHint, null);
				if (path != null)
				{
					Debug.LogWarning($"[Assets] GUID {guid} not found, using '{path}' from the path hint");
				}
				else if (IsEngineResource(pathHint))
				{
					path = pathHint;
				}
				else
				{
					// A project copy of a shader the engine has now.
					path = EngineShaderFallback(pathHint);
				}
			}

			if (path == null)
			{
				Debug.LogWarning($"[Assets] Missing {type.Name} asset (guid {guid}, last known path '{pathHint}')");
				return null;
			}

			return Load(type, path, subAsset);
		}

		#endregion

		#region PrivateMethods

		private static void RegisterBuiltinTypes()
		{
			RegisterAssetType(typeof(Model), ModelAssets.Extensions, (type, path, sub) => ModelAssets.Load(path));
			RegisterAssetType(typeof(Mesh), ModelAssets.Extensions, (type, path, sub) => ModelAssets.LoadMesh(path, sub));
			RegisterAssetType(typeof(Shaders.Material), new[] { ".material" }, (type, path, sub) => InternalStorage.GetOrCreateMaterial(path));
			RegisterAssetType(typeof(Shaders.ShaderProgram), new[] { ".shader" }, (type, path, sub) => InternalStorage.GetOrCreateShader(path));
			RegisterAssetType(typeof(Texture), TextureAssets.ImageExtensions, TextureAssets.Load);
			RegisterAssetType(typeof(Texture2D), TextureAssets.ImageExtensions, TextureAssets.Load);
			RegisterAssetType(typeof(Texture3D), TextureAssets.ImageExtensions, TextureAssets.Load);
			RegisterAssetType(typeof(Cubemap), TextureAssets.ImageExtensions, TextureAssets.Load);
			RegisterAssetType(typeof(Sprite), TextureAssets.ImageExtensions, TextureAssets.Load);
			RegisterAssetType(typeof(UIElements.StyleSheet), UIElements.UIAssets.StyleSheetExtensions, (type, path, sub) => UIElements.UIAssets.Load(type, path));
			RegisterAssetType(typeof(UIElements.VisualTreeAsset), UIElements.UIAssets.VisualTreeExtensions, (type, path, sub) => UIElements.UIAssets.Load(type, path));
			RegisterAssetType(typeof(UIElements.FontAsset), UIElements.UIAssets.FontExtensions, (type, path, sub) => UIElements.UIAssets.Load(type, path));
			RegisterAssetType(typeof(PhysicsMaterial2D), new[] { PhysicsMaterial2D.Extension }, (type, path, sub) => PhysicsMaterial2D.Load(path));
			RegisterAssetType(typeof(VFX.VfxGraph), new[] { VFX.VfxGraph.Extension }, (type, path, sub) => VFX.VfxGraph.Load(path));
			RegisterAssetType(typeof(Audio.AudioProject), new[] { Audio.AudioProject.Extension }, (type, path, sub) => Audio.AudioProject.Load(path));
			RegisterAssetType(typeof(Audio.AudioClip), Audio.AudioClip.Extensions, (type, path, sub) => Audio.AudioClip.Load(path));
			RegisterAssetType(typeof(ScriptableObject), new[] { ScriptableObjectAssets.Extension }, (type, path, sub) => ScriptableObjectAssets.Load(path));
			RegisterAssetType(typeof(Prefab), new[] { Prefab.Extension }, (type, path, sub) => Prefab.Load(path, AssetLoadScope.Current != null));
		}

		private static AssetTypeInfo FindLoader(Type type, string assetPath)
		{
			if (type == null)
			{
				return null;
			}

			lock (sync)
			{
				// The exact type first (Texture2D), then a registered subtype matching the file (Texture -> images), then a base.
				foreach (AssetTypeInfo info in assetTypes)
				{
					if (info.Type == type)
					{
						return info;
					}
				}

				foreach (AssetTypeInfo info in assetTypes)
				{
					if (type.IsAssignableFrom(info.Type) && MatchesExtension(info, assetPath))
					{
						return info;
					}
				}

				foreach (AssetTypeInfo info in assetTypes)
				{
					if (info.Type.IsAssignableFrom(type))
					{
						return info;
					}
				}
			}

			return null;
		}

		private static bool MatchesExtension(AssetTypeInfo info, string assetPath)
		{
			foreach (string extension in info.Extensions)
			{
				if (assetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		private static void EnsureInitialized()
		{
			if (root != null)
			{
				return;
			}

			lock (sync)
			{
				if (root == null)
				{
					root = Path.GetFullPath(Directory.GetCurrentDirectory());
					Rebuild(false);
				}
			}
		}

		private static void Rebuild(bool createMissingMeta)
		{
			pathsByGuid.Clear();
			guidsByPath.Clear();
			pathsByFileName.Clear();
			metaCache.Clear();
			if (!Directory.Exists(root))
			{
				return;
			}

			if (createMissingMeta)
			{
				int created = CreateMissingMetaFiles(root);
				if (created > 0)
				{
					Console.WriteLine($"[Assets] Created {created} meta files");
				}
			}
			else if (ReadIndex(Path.Combine(root, IndexFileName)))
			{
				// A game build: one file instead of reading every meta file (their import settings load on use).
				return;
			}

			foreach (string meta in Directory.EnumerateFiles(root, "*" + MetaExtension, SearchOption.AllDirectories))
			{
				string assetFile = meta.Substring(0, meta.Length - MetaExtension.Length);
				if (!File.Exists(assetFile) && !Directory.Exists(assetFile))
				{
					continue;
				}

				string guid = ReadGuid(meta);
				if (guid == null)
				{
					Debug.LogWarning("[Assets] Unreadable meta file " + meta);
					continue;
				}

				string assetPath = Normalize(assetFile.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1));
				string other;
				if (pathsByGuid.TryGetValue(guid, out other))
				{
					Debug.LogWarning($"[Assets] Duplicate GUID {guid}: {other} and {assetPath} (a copied meta file?)");
					continue;
				}

				Register(assetPath, guid);
			}
		}

		/// <summary>Registers the GUIDs of a build's index file; false when there is none (or it is unreadable: the meta files are read instead).</summary>
		private static bool ReadIndex(string file)
		{
			if (!File.Exists(file))
			{
				return false;
			}

			try
			{
				using (var reader = new BinaryReader(File.OpenRead(file)))
				{
					byte[] magic = reader.ReadBytes(IndexMagic.Length);
					if (!magic.AsSpan().SequenceEqual(IndexMagic))
					{
						return false;
					}

					int count = reader.ReadInt32();
					for (int i = 0; i < count; i++)
					{
						string guid = reader.ReadString();
						Register(reader.ReadString(), guid);
					}
				}

				return true;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Assets] Unreadable asset index " + file + " (" + e.Message + "); reading the meta files");
				pathsByGuid.Clear();
				guidsByPath.Clear();
				pathsByFileName.Clear();
				return false;
			}
		}

		private static void Register(string assetPath, string guid)
		{
			pathsByGuid[guid] = assetPath;
			guidsByPath[assetPath] = guid;
			string fileName = Path.GetFileName(assetPath);
			List<string> list;
			if (!pathsByFileName.TryGetValue(fileName, out list))
			{
				list = new List<string>();
				pathsByFileName[fileName] = list;
			}

			if (!list.Contains(assetPath))
			{
				list.Add(assetPath);
			}
		}

		private static bool IsAssetFile(string file)
		{
			string name = Path.GetFileName(file);
			return !name.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(".", StringComparison.Ordinal);
		}

		/// <summary>The GUID of a meta file: a quick look at the "guid" property of a JSON meta, the whole document for a binary one.</summary>
		private static string ReadGuid(string metaFile)
		{
			try
			{
				byte[] data = File.ReadAllBytes(metaFile);
				if (Documents.IsBinary(data))
				{
					return Documents.ReadObject<AssetMeta>(data)?.guid;
				}

				using (JsonDocument document = JsonDocument.Parse(data, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
				{
					JsonElement guid;
					return document.RootElement.TryGetProperty("guid", out guid) && guid.ValueKind == JsonValueKind.String ? guid.GetString() : null;
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static AssetMeta ReadMeta(string metaFile)
		{
			try
			{
				return Documents.ReadObject(File.ReadAllBytes(metaFile), typeof(AssetMeta), new AssetMeta()) as AssetMeta;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Assets] Cannot read " + metaFile + ": " + e.Message);
				return null;
			}
		}

		private static void WriteMeta(string metaFile, AssetMeta meta)
		{
			Documents.WriteObjectFile(metaFile, meta);
		}

		private static string Normalize(string path)
		{
			return path.Replace('\\', '/').TrimStart('/');
		}

		#endregion
	}
}
