using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LELEngine
{
	/// <summary>An object loaded from an asset file (mesh, material, ...). Scenes reference it by the file's GUID.</summary>
	public interface IAsset
	{
		/// <summary>Path of the source file relative to <see cref="AssetDatabase.Root" /> ("/" separated), null for runtime objects.</summary>
		string AssetPath { get; }
	}

	/// <summary>
	///     Asset files and their identities. Every asset file has a sidecar "file.ext.meta" holding its GUID, so
	///     references survive renames and moves (the meta file moves with it). The root is the folder all asset
	///     paths are relative to: a project's Assets folder in the editor, the Data folder of a build, or the
	///     executable's folder for the legacy layout (Meshes/, Materials/, Textures/, Shaders/ next to the exe).
	///
	///     Engine shaders (the GLSL library and engine passes) are engine resources, not project assets: they are
	///     always looked up in <see cref="EngineShadersRoot" /> first.
	/// </summary>
	public static class AssetDatabase
	{
		#region PublicFields

		public const string MetaExtension = ".meta";

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

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static string root;
		private static readonly Dictionary<string, string> pathsByGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, string> guidsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, List<string>> pathsByFileName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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
		}

		/// <summary>Re-reads the meta files under the root (after files were added, moved or deleted).</summary>
		public static void Refresh(bool createMissingMeta = false)
		{
			lock (sync)
			{
				EnsureInitialized();
				Rebuild(createMissingMeta);
			}
		}

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

		/// <summary>Creates the meta file of an asset if it is missing; returns the asset's GUID.</summary>
		public static string EnsureMeta(string assetPath)
		{
			string normalized = Normalize(assetPath);
			string existing = GetGuid(normalized);
			if (existing != null)
			{
				return existing;
			}

			string guid = Guid.NewGuid().ToString("N");
			WriteMeta(ToAbsolute(normalized) + MetaExtension, guid);
			lock (sync)
			{
				Register(normalized, guid);
			}

			return guid;
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

				WriteMeta(file + MetaExtension, Guid.NewGuid().ToString("N"));
				created++;
			}

			return created;
		}

		/// <summary>Loads (or returns the cached) asset of the given type from a root-relative path.</summary>
		public static object Load(Type type, string assetPath)
		{
			if (assetPath == null)
			{
				return null;
			}

			if (type == typeof(Mesh))
			{
				return InternalStorage.GetOrCreateMesh(assetPath);
			}

			if (type == typeof(Shaders.Material))
			{
				return InternalStorage.GetOrCreateMaterial(assetPath);
			}

			Debug.LogWarning($"[Assets] No loader for {type.Name} ({assetPath})");
			return null;
		}

		/// <summary>Loads an asset by GUID; falls back to the path hint when the GUID is unknown.</summary>
		public static object LoadByGuid(Type type, string guid, string pathHint)
		{
			string path = GetPath(guid);
			if (path == null && pathHint != null)
			{
				path = Resolve(pathHint, null);
				if (path != null)
				{
					Debug.LogWarning($"[Assets] GUID {guid} not found, using '{path}' from the path hint");
				}
			}

			if (path == null)
			{
				Debug.LogWarning($"[Assets] Missing {type.Name} asset (guid {guid}, last known path '{pathHint}')");
				return null;
			}

			return Load(type, path);
		}

		#endregion

		#region PrivateMethods

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

		private static string ReadGuid(string metaFile)
		{
			try
			{
				using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(metaFile)))
				{
					JsonElement guid;
					return document.RootElement.TryGetProperty("guid", out guid) ? guid.GetString() : null;
				}
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static void WriteMeta(string metaFile, string guid)
		{
			File.WriteAllText(metaFile, "{\n  \"guid\": \"" + guid + "\"\n}\n");
		}

		private static string Normalize(string path)
		{
			return path.Replace('\\', '/').TrimStart('/');
		}

		#endregion
	}
}
