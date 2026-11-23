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

		/// <summary>
		///     Extension of a mesh asset: a mesh made in the engine (<see cref="SaveMesh" />, e.g. a combined mesh), kept as
		///     imported model data of one node. It needs no importer and has no import settings.
		/// </summary>
		public const string MeshExtension = ".mesh";

		/// <summary>File extensions loaded as models (their meshes are sub-assets).</summary>
		public static readonly string[] Extensions = { ".obj", ".fbx", ".gltf", ".glb", ".dae", ".3ds", ".blend", ".ply", ".stl", MeshExtension };

		/// <summary>Folder of the import cache (null: no cache, every load imports).</summary>
		public static string CacheFolder { get; set; }

		/// <summary>Creates the material assets of imports (null: imported models use the default material).</summary>
		public static IModelMaterialExtractor MaterialExtractor { get; set; }

		/// <summary>Raised after a model was imported (not when it came from the cache).</summary>
		public static event Action<string> Imported;

		#endregion

		#region PrivateFields

		// 2 (2026-10-25): vertices carry a second UV set (Vertex.texcoord2).
		// Mesh assets (MeshExtension) are project files in this format, not a cache that an import makes again: a version
		// after 2 has to go on reading the versions mesh assets were written in.
		private const int FormatVersion = 2;
		private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LELMODEL");
		private static readonly object sync = new object();
		private static readonly Dictionary<string, Model> models = new Dictionary<string, Model>(StringComparer.OrdinalIgnoreCase);

		// The loaded mesh assets' files as they were read or written: no import notices one changed outside the editor.
		private static readonly AssetFileStamps meshFileStamps = new AssetFileStamps();
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

		/// <summary>The file is a mesh asset (<see cref="MeshExtension" />): a mesh made in the engine, without a source file or import settings.</summary>
		public static bool IsMeshFile(string path)
		{
			return path != null && path.EndsWith(MeshExtension, StringComparison.OrdinalIgnoreCase);
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

			// Not the static batches' meshes: they are the engine's own files, named by what they hold, never changed in place
			// (their times move when a cache uses them).
			if (IsMeshFile(assetPath) && !assetPath.StartsWith(AssetDatabase.StaticBatchPrefix, StringComparison.OrdinalIgnoreCase))
			{
				// Before it is read: a write in between counts as a change.
				meshFileStamps.Note(assetPath);
			}

			long started = FrameProfiler.Timestamp;
			ModelData data = LoadData(assetPath);
			var model = new Model(assetPath, data);
			AttachBakedDistanceFields(model, assetPath);
			lock (sync)
			{
				models[assetPath] = model;
			}

			// Only with the hitch profiler on (its text is not built otherwise).
			if (started != 0)
			{
				FrameProfiler.EventSince(started, "model load", assetPath, model.Meshes.Count + " meshes");
			}
			return model;
		}

		/// <summary>The distance fields a game build baked for the model's meshes ("model.ext.sdf" next to it), when there are some.</summary>
		private static void AttachBakedDistanceFields(Model model, string assetPath)
		{
			// Under the root also for built-in models: the build writes their fields into its Data folder.
			string file = AssetDatabase.ToRootAbsolute(assetPath) + Rendering.DistanceField.DistanceFieldFile.Extension;
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
			if (mesh == null && IsMeshFile(assetPath) && model.Meshes.Count == 1)
			{
				// A mesh asset is its one mesh, whatever the reference calls it (the file replaced by another mesh asset).
				return model.Meshes[0];
			}

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

			meshFileStamps.Forget(assetPath);
		}

		public static void Clear()
		{
			lock (sync)
			{
				models.Clear();
			}

			meshFileStamps.Clear();
		}

		/// <summary>
		///     Writes <paramref name="mesh" /> as a mesh asset (<paramref name="assetPath" /> ends with
		///     <see cref="MeshExtension" />; its meta file is made when it has none) and returns the asset's mesh: the object
		///     scenes reference from then on. A mesh asset holds the geometry and its sub-meshes, no materials: those are the
		///     renderer's. Written over an existing mesh asset, the mesh keeps the name references know it by, and the model
		///     loaded from the old file is forgotten (a renderer still holding the old mesh draws it until it is given the
		///     new one). Main thread.
		/// </summary>
		public static Mesh SaveMesh(string assetPath, Mesh mesh)
		{
			if (mesh == null)
			{
				throw new ArgumentNullException(nameof(mesh));
			}

			assetPath = (assetPath ?? "").Replace('\\', '/').TrimStart('/');
			if (!IsMeshFile(assetPath) || Path.GetFileNameWithoutExtension(assetPath).Length == 0)
			{
				throw new ArgumentException("A mesh asset's path ends with " + MeshExtension + " (got '" + assetPath + "')", nameof(assetPath));
			}

			if (assetPath.StartsWith(AssetDatabase.EnginePrefix, StringComparison.OrdinalIgnoreCase))
			{
				throw new ArgumentException("Engine/ holds the engine's own assets: a mesh asset goes elsewhere (got '" + assetPath + "')", nameof(assetPath));
			}

			string file = AssetDatabase.ToRootAbsolute(assetPath);
			// References name the mesh inside its file: the one a file already there has stays.
			WriteMeshFile(file, mesh, FirstMeshName(file) ?? Path.GetFileNameWithoutExtension(assetPath));
			AssetDatabase.EnsureMeta(assetPath);

			Model previous;
			lock (sync)
			{
				models.Remove(assetPath, out previous);
			}

			if (previous != null)
			{
				foreach (Mesh old in previous.Meshes)
				{
					Rendering.GeometryPool.Release(old);
				}
			}

			Model model = Load(assetPath);
			return model.Meshes[0];
		}

		/// <summary>
		///     Forgets the loaded models whose paths start with <paramref name="prefix" /> and gives their geometry back to
		///     the pool (the GL thread): the next load reads their files again. What still holds one of their meshes draws
		///     it until it lets go. The editor does this to the static batches' meshes when play mode ends.
		/// </summary>
		public static void UnloadUnder(string prefix)
		{
			var removed = new List<Model>();
			lock (sync)
			{
				var paths = new List<string>();
				foreach (string path in models.Keys)
				{
					if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					{
						paths.Add(path);
					}
				}

				foreach (string path in paths)
				{
					if (models.Remove(path, out Model model))
					{
						removed.Add(model);
					}

					meshFileStamps.Forget(path);
				}
			}

			foreach (Model model in removed)
			{
				foreach (Mesh mesh in model.Meshes)
				{
					Rendering.GeometryPool.Release(mesh);
				}
			}
		}

		/// <summary>
		///     Writes a mesh as a mesh file's data (the <see cref="MeshExtension" /> format: one node and one mesh, both
		///     named <paramref name="name" />, no materials) into a stream: the same mesh and name give the same bytes. No
		///     asset is registered or loaded (<see cref="SaveMesh" /> does that for a project's mesh asset); the static
		///     batcher keeps its batches' meshes with this. Any thread.
		/// </summary>
		public static void WriteMesh(Stream stream, Mesh mesh, string name)
		{
			Write(stream, MeshData(mesh, name, name), null);
		}

		// A mesh file (one node named as the file, one mesh named meshName, no materials) at a path, whole or not at all.
		private static void WriteMeshFile(string file, Mesh mesh, string meshName)
		{
			ModelData data = MeshData(mesh, Path.GetFileNameWithoutExtension(file), meshName);

			// Whole or not at all: written beside it (a dot file is no asset) and put in its place.
			string folder = Path.GetDirectoryName(file);
			Directory.CreateDirectory(folder);
			string temporary = Path.Combine(folder, "." + Path.GetFileName(file) + ".writing");
			try
			{
				using (FileStream stream = File.Create(temporary))
				{
					Write(stream, data, null);
				}

				File.Move(temporary, file, true);
			}
			finally
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
		}

		// A mesh as the data of a mesh file: one node, one mesh (named meshName, else as the node), no materials.
		private static ModelData MeshData(Mesh mesh, string nodeName, string meshName)
		{
			if (mesh == null)
			{
				throw new ArgumentNullException(nameof(mesh));
			}

			var subMeshes = new SubMesh[mesh.SubMeshCount];
			var materials = new int[subMeshes.Length];
			for (int i = 0; i < subMeshes.Length; i++)
			{
				subMeshes[i] = mesh.SubMeshes[i];
				materials[i] = -1;
			}

			var data = new ModelData();
			data.Nodes.Add(new ModelNode { Name = nodeName, Mesh = 0 });
			data.Meshes.Add(new ModelMeshData
			{
				Name = string.IsNullOrEmpty(meshName) ? nodeName : meshName,
				Vertices = mesh.Vertices,
				Indices = mesh.Indices,
				SubMeshes = subMeshes,
				Materials = materials
			});
			return data;
		}

		/// <summary>
		///     The loaded mesh assets whose files changed since they were read or written (a checkout, another program), each
		///     reported once. Their meshes are the old files': the caller unloads them and loads what uses them again.
		/// </summary>
		public static List<string> TakeChangedMeshFiles()
		{
			List<string> loaded;
			lock (sync)
			{
				loaded = new List<string>();
				foreach (string path in models.Keys)
				{
					if (IsMeshFile(path))
					{
						loaded.Add(path);
					}
				}
			}

			return meshFileStamps.TakeChanged(loaded);
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
				meshFileStamps.Moved(fromPath, toPath);

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

			// The progress bar's task begins with the first model that needs an import (a check alone shows nothing).
			Progress.Scope progress = null;
			List<string> paths = AssetDatabase.GetAllAssetPaths(Extensions);
			try
			{
				for (int i = 0; i < paths.Count; i++)
				{
					string assetPath = paths[i];
					try
					{
						if (NeedsImport(assetPath, verifyMaterials))
						{
							progress ??= Progress.Begin("Importing models");
							progress.Report(i, paths.Count, assetPath);
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
			}
			finally
			{
				progress?.Dispose();
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
				throw new InvalidOperationException(IsMeshFile(assetPath)
					? assetPath + " is not a mesh asset the engine wrote (missing, cut short or another file under that name)."
					: assetPath + " is a source model file that only the editor imports (builds contain the imported data).");
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
			if (settings.importMaterials && MaterialExtractor != null && !AssetDatabase.IsEngineResource(assetPath))
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

		/// <summary>The name of the first mesh of an imported file, read from its start (the geometry is not); null when the file is none.</summary>
		private static string FirstMeshName(string file)
		{
			if (!File.Exists(file))
			{
				return null;
			}

			try
			{
				using (var reader = new BinaryReader(File.OpenRead(file), Encoding.UTF8))
				{
					byte[] magic = reader.ReadBytes(Magic.Length);
					if (!magic.AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
					{
						return null;
					}

					reader.ReadString();
					int nodes = reader.ReadInt32();
					for (int i = 0; i < nodes; i++)
					{
						reader.ReadString();
						// Parent, position, rotation, scale, mesh.
						reader.BaseStream.Seek(4 + 12 + 16 + 12 + 4, SeekOrigin.Current);
					}

					int materials = reader.ReadInt32();
					for (int i = 0; i < materials; i++)
					{
						reader.ReadString();
						reader.ReadString();
					}

					string name = reader.ReadInt32() > 0 ? reader.ReadString() : null;
					return string.IsNullOrEmpty(name) ? null : name;
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
			{
				return null;
			}
		}

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
						mesh.Vertices[i] = new Vertex(v.position * scale, v.normal, v.texcoord, v.tangent, v.bitangent, v.color, v.texcoord2);
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
