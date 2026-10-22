using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>Turns model files of some formats into <see cref="ModelData" />.</summary>
	public interface IModelImportBackend
	{
		/// <summary>Bumped when the import code changes: cached imports of an older version are imported again.</summary>
		int Version { get; }

		/// <param name="extension">Lower-case file extension with the dot.</param>
		bool CanImport(string extension);

		/// <summary>
		///     Reads the file into nodes, meshes and material descriptions (normals, UV flip and mesh optimization of
		///     <paramref name="settings" /> applied; scale and rotation are applied afterwards for every format).
		/// </summary>
		/// <param name="assetPath">Root-relative path of the model file.</param>
		ModelData Import(string assetPath, ModelImporter settings);
	}

	/// <summary>Creates the .material assets of an import (the editor; players never import).</summary>
	public interface IModelMaterialExtractor
	{
		/// <summary>Sets <see cref="ModelMaterial.AssetPath" /> of the materials of <paramref name="data" />.</summary>
		void Extract(string assetPath, ModelData data);
	}

	/// <summary>
	///     Loads model files. A model file is either imported data (the binary written by <see cref="Write" />, which is
	///     what player builds ship under the model's own name) or a source file that a backend imports: the built-in
	///     .obj reader, and the editor's Assimp importer for the other formats (<see cref="RegisterBackend" />).
	///     Imports are cached in <see cref="CacheFolder" /> (the editor's Library/ModelCache) per asset GUID and
	///     redone when the file, its import settings or the backend changed.
	/// </summary>
	public static class ModelAssets
	{
		#region PublicFields

		/// <summary>File extensions loaded as models (their meshes are sub-assets).</summary>
		public static readonly string[] Extensions = { ".obj", ".fbx", ".gltf", ".glb", ".dae", ".3ds", ".blend", ".ply", ".stl" };

		/// <summary>Folder of the import cache (null: no cache, every load imports).</summary>
		public static string CacheFolder { get; set; }

		/// <summary>Creates the material assets of imports (null: imported models use the default material).</summary>
		public static IModelMaterialExtractor MaterialExtractor { get; set; }

		/// <summary>Raised after a model was imported (not when it came from the cache).</summary>
		public static event Action<string> Imported;

		#endregion

		#region PrivateFields

		private const int FormatVersion = 1;
		private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LELMODEL");
		private static readonly object sync = new object();
		private static readonly Dictionary<string, Model> models = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);
		private static readonly List<IModelImportBackend> backends = new List<IModelImportBackend> { new ObjModelBackend() };

		#endregion

		#region PublicMethods

		/// <summary>Adds an importer; for a format two backends can read, the one registered last is used.</summary>
		public static void RegisterBackend(IModelImportBackend backend)
		{
			lock (sync)
			{
				backends.Remove(backend);
				backends.Add(backend);
			}
		}

		public static bool IsModelFile(string path)
		{
			string extension = Path.GetExtension(path ?? "");
			return Array.Exists(Extensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>The model of a file (cached after the first load).</summary>
		public static Model Load(string assetPath)
		{
			lock (sync)
			{
				if (models.TryGetValue(assetPath, out Model cached))
				{
					return cached;
				}
			}

			ModelData data = LoadData(assetPath);
			var model = new Model(assetPath, data);
			AttachBakedDistanceFields(model, assetPath);
			lock (sync)
			{
				models[assetPath] = model;
			}

			return model;
		}

		/// <summary>The distance fields a game build baked for the model's meshes ("model.ext.sdf" next to it), when there are some.</summary>
		private static void AttachBakedDistanceFields(Model model, string assetPath)
		{
			string file = AssetDatabase.ToAbsolute(assetPath) + Rendering.DistanceField.DistanceFieldFile.Extension;
			if (!File.Exists(file))
			{
				return;
			}

			try
			{
				List<KeyValuePair<string, Rendering.DistanceField.BakedDistanceField>> fields = Rendering.DistanceField.DistanceFieldFile.Read(file, out _);
				if (fields == null)
				{
					return;
				}

				foreach (KeyValuePair<string, Rendering.DistanceField.BakedDistanceField> entry in fields)
				{
					Mesh mesh = model.FindMesh(entry.Key);
					if (mesh != null)
					{
						(mesh.BakedDistanceFields ??= new List<Rendering.DistanceField.BakedDistanceField>()).Add(entry.Value);
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Model] Cannot read the baked distance fields of {assetPath}: {e.Message}");
			}
		}

		/// <summary>
		///     A mesh of a model file by sub-asset name (null: the first mesh). <paramref name="nameOrPath" /> is an asset
		///     path or a file name that is unique in the project.
		/// </summary>
		public static Mesh LoadMesh(string nameOrPath, string meshName = null)
		{
			string assetPath = AssetDatabase.Resolve(nameOrPath, null) ?? nameOrPath;
			Model model = Load(assetPath);
			if (meshName == null)
			{
				return model.Meshes.Count > 0 ? model.Meshes[0] : null;
			}

			Mesh mesh = model.FindMesh(meshName);
			if (mesh == null)
			{
				Debug.LogWarning($"[Model] {assetPath} has no mesh '{meshName}'");
			}

			return mesh;
		}

		/// <summary>Forgets a loaded model (it was reimported): the next load reads the new import.</summary>
		public static void Unload(string assetPath)
		{
			lock (sync)
			{
				models.Remove(assetPath);
			}
		}

		public static void Clear()
		{
			lock (sync)
			{
				models.Clear();
			}
		}

		/// <summary>
		///     A file or folder moved: the loaded models and their meshes keep their identity under the new path, and the
		///     models whose material assets moved use them where they are.
		/// </summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (sync)
			{
				AssetDatabase.RekeyMoved(models, fromPath, toPath, (model, path) =>
				{
					model.AssetPath = path;
					foreach (Mesh mesh in model.Meshes)
					{
						if (mesh != null && AssetDatabase.MovedPath(mesh.AssetPath, fromPath, toPath) is string meshPath)
						{
							mesh.AssetPath = meshPath;
						}
					}
				});

				foreach (Model model in models.Values)
				{
					foreach (ModelMaterial material in model.Data.Materials)
					{
						if (AssetDatabase.MovedPath(material.AssetPath, fromPath, toPath) is string materialPath)
						{
							material.AssetPath = materialPath;
						}
					}
				}
			}
		}

		/// <summary>
		///     Imports every model of the project whose cached import is missing or outdated (new file, changed file,
		///     settings or importer), materials included: the editor runs it when a project opens and after files change,
		///     so a model copied in from outside (Explorer, a script, the command line) is complete before anything uses
		///     it. Needs the import cache (<see cref="CacheFolder" />). With <paramref name="verifyMaterials" /> (a project
		///     opening) an import whose extracted material files are gone counts as outdated too (it reads the cached
		///     imports). Returns the paths imported.
		/// </summary>
		public static List<string> ImportOutdated(bool verifyMaterials = false)
		{
			var imported = new List<string>();
			if (CacheFolder == null)
			{
				return imported;
			}

			foreach (string assetPath in AssetDatabase.GetAllAssetPaths(Extensions))
			{
				try
				{
					if (NeedsImport(assetPath, verifyMaterials))
					{
						// A fresh import: the cached one may match the file and still miss its material files.
						LoadData(assetPath, true);
						imported.Add(assetPath);
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Model] Cannot import {assetPath}: {e.Message}");
				}
			}

			return imported;
		}

		/// <summary>The model is loaded (its meshes may be in use).</summary>
		public static bool IsLoaded(string assetPath)
		{
			lock (sync)
			{
				return models.ContainsKey(assetPath);
			}
		}

		/// <summary>The imported data of a model file, importing it when there is no valid cached import.</summary>
		public static ModelData LoadData(string assetPath)
		{
			return LoadData(assetPath, false);
		}

		/// <summary>The imported data of a model file; <paramref name="reimport" /> imports it even when the cached import is valid.</summary>
		public static ModelData LoadData(string assetPath, bool reimport)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			if (IsImportedFile(file))
			{
				using (FileStream stream = File.OpenRead(file))
				{
					return Read(stream, out _);
				}
			}

			IModelImportBackend backend = FindBackend(assetPath);
			if (backend == null)
			{
				throw new InvalidOperationException(assetPath + " is a source model file that only the editor imports (builds contain the imported data).");
			}

			ModelImporter settings = AssetDatabase.GetImporter<ModelImporter>(assetPath);
			string stamp = Stamp(file, settings, backend);
			string cacheFile = CacheFile(assetPath);
			if (cacheFile != null && File.Exists(cacheFile) && !reimport)
			{
				try
				{
					using (FileStream stream = File.OpenRead(cacheFile))
					{
						ModelData cached = Read(stream, out string cachedStamp);
						if (cachedStamp == stamp)
						{
							return cached;
						}
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Model] Cannot read the cached import of {assetPath} ({e.Message}); importing again");
				}
			}

			var watch = System.Diagnostics.Stopwatch.StartNew();
			ModelData data = backend.Import(assetPath, settings);
			ApplySettings(data, settings);
			if (settings.importMaterials && MaterialExtractor != null)
			{
				MaterialExtractor.Extract(assetPath, data);
			}

			int triangles = 0;
			foreach (ModelMeshData mesh in data.Meshes)
			{
				triangles += mesh.Indices.Length / 3;
			}

			if (cacheFile != null)
			{
				string unit = Math.Abs(data.UnitScale - 1f) > 1e-6f ? $", file unit {data.UnitScale:0.####} m" : "";
				Debug.Log($"[Model] Imported {assetPath}: {data.Nodes.Count} nodes, {data.Meshes.Count} meshes, {triangles} triangles, {data.Materials.Count} materials{unit} ({watch.ElapsedMilliseconds} ms)");
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
					using (FileStream stream = File.Create(cacheFile))
					{
						Write(stream, data, stamp);
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Model] Cannot cache the import of {assetPath}: {e.Message}");
				}
			}

			Imported?.Invoke(assetPath);
			return data;
		}

		/// <summary>The file holds imported data (it starts with the format's magic bytes).</summary>
		public static bool IsImportedFile(string file)
		{
			try
			{
				using (FileStream stream = File.OpenRead(file))
				{
					var header = new byte[Magic.Length];
					return stream.Read(header, 0, header.Length) == header.Length && header.AsSpan().SequenceEqual(Magic);
				}
			}
			catch (IOException)
			{
				return false;
			}
		}

		/// <summary>Writes imported data (the cache format, also what builds ship): geometry, hierarchy, material names and assets.</summary>
		public static void Write(Stream stream, ModelData data, string stamp)
		{
			using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
			{
				writer.Write(Magic);
				writer.Write(FormatVersion);
				writer.Write(stamp ?? "");
				writer.Write(data.Nodes.Count);
				foreach (ModelNode node in data.Nodes)
				{
					writer.Write(node.Name ?? "");
					writer.Write(node.Parent);
					WriteVector(writer, node.LocalPosition);
					writer.Write(node.LocalRotation.X);
					writer.Write(node.LocalRotation.Y);
					writer.Write(node.LocalRotation.Z);
					writer.Write(node.LocalRotation.W);
					WriteVector(writer, node.LocalScale);
					writer.Write(node.Mesh);
				}

				writer.Write(data.Materials.Count);
				foreach (ModelMaterial material in data.Materials)
				{
					writer.Write(material.Name ?? "");
					writer.Write(material.AssetPath ?? "");
				}

				writer.Write(data.Meshes.Count);
				foreach (ModelMeshData mesh in data.Meshes)
				{
					writer.Write(mesh.Name ?? "");
					writer.Write(mesh.Vertices.Length);
					writer.Write(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()));
					writer.Write(mesh.Indices.Length);
					writer.Write(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
					writer.Write(mesh.SubMeshes.Length);
					for (int i = 0; i < mesh.SubMeshes.Length; i++)
					{
						writer.Write(mesh.SubMeshes[i].FirstIndex);
						writer.Write(mesh.SubMeshes[i].IndexCount);
						writer.Write(i < mesh.Materials.Length ? mesh.Materials[i] : -1);
					}
				}
			}
		}

		public static ModelData Read(Stream stream, out string stamp)
		{
			using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
			{
				byte[] magic = reader.ReadBytes(Magic.Length);
				if (!magic.AsSpan().SequenceEqual(Magic))
				{
					throw new InvalidDataException("Not an imported model");
				}

				int version = reader.ReadInt32();
				if (version != FormatVersion)
				{
					throw new InvalidDataException("Imported model format " + version + " (expected " + FormatVersion + ")");
				}

				stamp = reader.ReadString();
				var data = new ModelData();
				int nodeCount = reader.ReadInt32();
				for (int i = 0; i < nodeCount; i++)
				{
					data.Nodes.Add(new ModelNode
					{
						Name = reader.ReadString(),
						Parent = reader.ReadInt32(),
						LocalPosition = ReadVector(reader),
						LocalRotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
						LocalScale = ReadVector(reader),
						Mesh = reader.ReadInt32()
					});
				}

				int materialCount = reader.ReadInt32();
				for (int i = 0; i < materialCount; i++)
				{
					string name = reader.ReadString();
					string assetPath = reader.ReadString();
					data.Materials.Add(new ModelMaterial { Name = name, AssetPath = assetPath.Length > 0 ? assetPath : null });
				}

				int meshCount = reader.ReadInt32();
				for (int i = 0; i < meshCount; i++)
				{
					var mesh = new ModelMeshData { Name = reader.ReadString() };
					mesh.Vertices = new Vertex[reader.ReadInt32()];
					reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()));
					mesh.Indices = new uint[reader.ReadInt32()];
					reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()));
					int subMeshCount = reader.ReadInt32();
					mesh.SubMeshes = new SubMesh[subMeshCount];
					mesh.Materials = new int[subMeshCount];
					for (int s = 0; s < subMeshCount; s++)
					{
						mesh.SubMeshes[s] = new SubMesh(reader.ReadInt32(), reader.ReadInt32());
						mesh.Materials[s] = reader.ReadInt32();
					}

					data.Meshes.Add(mesh);
				}

				return data;
			}
		}

		#endregion

		#region PrivateMethods

		private static IModelImportBackend FindBackend(string assetPath)
		{
			string extension = Path.GetExtension(assetPath).ToLowerInvariant();
			lock (sync)
			{
				for (int i = backends.Count - 1; i >= 0; i--)
				{
					if (backends[i].CanImport(extension))
					{
						return backends[i];
					}
				}
			}

			return null;
		}

		/// <summary>
		///     The settings every format shares: the scale (import scale times the file's unit, when converted) baked
		///     into the vertices and node positions, the rotation on the root node.
		/// </summary>
		private static void ApplySettings(ModelData data, ModelImporter settings)
		{
			float scale = settings.scaleFactor * (settings.convertUnits ? data.UnitScale : 1f);
			if (scale > 0f && Math.Abs(scale - 1f) > 1e-6f)
			{
				foreach (ModelMeshData mesh in data.Meshes)
				{
					for (int i = 0; i < mesh.Vertices.Length; i++)
					{
						Vertex v = mesh.Vertices[i];
						mesh.Vertices[i] = new Vertex(v.position * scale, v.normal, v.texcoord, v.tangent, v.bitangent, v.color);
					}
				}

				foreach (ModelNode node in data.Nodes)
				{
					node.LocalPosition *= scale;
				}
			}

			if (data.Nodes.Count > 0 && settings.rotation != Vector3.Zero)
			{
				ModelNode root = data.Nodes[0];
				root.LocalPosition = Vector3.Transform(root.LocalPosition, QuaternionHelper.Euler(settings.rotation.X, settings.rotation.Y, settings.rotation.Z));
				root.LocalRotation = QuaternionHelper.Euler(settings.rotation.X, settings.rotation.Y, settings.rotation.Z) * root.LocalRotation;
			}
		}

		/// <summary>
		///     Identity of an import: the source file (size, time), its settings and the backend's version. The cache is
		///     valid while it matches.
		/// </summary>
		private static string Stamp(string file, ModelImporter settings, IModelImportBackend backend)
		{
			var info = new FileInfo(file);
			// The import's own record (the material assets it made) is not a setting.
			int version = settings.version;
			List<ModelExtractedAsset> materialAssets = settings.materialAssets;
			List<ModelExtractedAsset> textureAssets = settings.textureAssets;
			settings.version = 0;
			settings.materialAssets = null;
			settings.textureAssets = null;
			byte[] settingsBytes = Serialization.Documents.WriteObject(settings, Serialization.DocumentFormat.Binary);
			settings.version = version;
			settings.materialAssets = materialAssets;
			settings.textureAssets = textureAssets;
			string settingsHash = Convert.ToHexString(SHA1.HashData(settingsBytes));
			// Extractor layout 3 (2026-10-22): materials and embedded textures next to the model, kept by GUID (older
			// imports redo once).
			return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{settingsHash}:{backend.GetType().Name}:{backend.Version}:{(MaterialExtractor != null ? 3 : 0)}";
		}

		// A source model without a cached import of its current file, settings and importer (or, verifying materials,
		// whose import should have material files that are missing).
		private static bool NeedsImport(string assetPath, bool verifyMaterials)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			IModelImportBackend backend = FindBackend(assetPath);
			string cacheFile = CacheFile(assetPath);
			if (backend == null || cacheFile == null || IsImportedFile(file))
			{
				return false;
			}

			if (!File.Exists(cacheFile))
			{
				return true;
			}

			try
			{
				using (var reader = new BinaryReader(File.OpenRead(cacheFile), Encoding.UTF8))
				{
					byte[] magic = reader.ReadBytes(Magic.Length);
					if (!magic.AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
					{
						return true;
					}

					ModelImporter settings = AssetDatabase.GetImporter<ModelImporter>(assetPath);
					if (reader.ReadString() != Stamp(file, settings, backend))
					{
						return true;
					}

					if (!verifyMaterials || !settings.importMaterials || MaterialExtractor == null)
					{
						return false;
					}
				}

				using (FileStream stream = File.OpenRead(cacheFile))
				{
					// The materials a mesh uses (the extractor makes no asset for the rest).
					ModelData data = Read(stream, out _);
					foreach (ModelMeshData mesh in data.Meshes)
					{
						foreach (int index in mesh.Materials)
						{
							string materialPath = index >= 0 && index < data.Materials.Count ? data.Materials[index].AssetPath : "";
							if (materialPath == null || (materialPath.Length > 0 && !File.Exists(AssetDatabase.ToAbsolute(materialPath))))
							{
								return true;
							}
						}
					}
				}

				return false;
			}
			catch (Exception e) when (e is IOException || e is EndOfStreamException || e is InvalidDataException)
			{
				return true;
			}
		}

		private static string CacheFile(string assetPath)
		{
			if (CacheFolder == null)
			{
				return null;
			}

			string guid = AssetDatabase.GetGuid(assetPath) ?? Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(assetPath.ToLowerInvariant())));
			return Path.Combine(CacheFolder, guid + ".lelmodel");
		}

		private static void WriteVector(BinaryWriter writer, Vector3 value)
		{
			writer.Write(value.X);
			writer.Write(value.Y);
			writer.Write(value.Z);
		}

		private static Vector3 ReadVector(BinaryReader reader)
		{
			return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
		}

		#endregion
	}
}
