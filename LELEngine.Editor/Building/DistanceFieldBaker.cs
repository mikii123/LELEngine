using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LELEngine.Rendering;
using LELEngine.Rendering.DistanceField;
using LELEngine.Serialization;
using LELEngine.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Bakes the mesh distance fields of a game build: every mesh its scenes draw for the GI, at the shape of each
	///     renderer's scale (every uniform scale is one shape; plus shape 1, for copies made at runtime), with the
	///     renderer's own GPU build. The player
	///     uploads them instead of building them, which takes seconds for big meshes right when a scene appears.
	///     Baking needs the GL context on the calling thread; without it only cached fields are used. Fields are cached
	///     in Library/DistanceFieldCache, valid while the model file, its meta file and the field layout are the same.
	/// </summary>
	internal static class DistanceFieldBaker
	{
		#region PublicFields

		/// <summary>What one collection found.</summary>
		public sealed class Result
		{
			/// <summary>Per model asset path, the fields of its meshes by mesh name.</summary>
			public readonly Dictionary<string, List<KeyValuePair<string, BakedDistanceField>>> Models = new Dictionary<string, List<KeyValuePair<string, BakedDistanceField>>>(StringComparer.OrdinalIgnoreCase);

			public int Baked;
			public int Cached;

			/// <summary>Fields neither cached nor bakeable here (no GL context): the player builds them.</summary>
			public int Missing;

			public override string ToString()
			{
				return $"Distance fields: {Baked + Cached} ({Baked} baked, {Cached} from the cache{(Missing > 0 ? $", {Missing} left to the player" : "")}).";
			}
		}

		#endregion

		#region PrivateFields

		private const int Version = 1;

		private struct Request
		{
			public string ModelPath;
			public string MeshName;

			/// <summary>The shape of the renderers' scale (<see cref="DistanceFieldAtlas.ShapeKey" />).</summary>
			public Vector3i Shape;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     The fields of the meshes the scenes (asset paths) draw, their prefab instances included, and of the prefabs
		///     the game may instantiate, at the scales inside them: those the scenes reference, straight or through
		///     ScriptableObject assets at any depth (a ship's model and wreck named by its data asset), those in Resources
		///     folders (and what their ScriptableObjects reference), and <paramref name="shippedPrefabs" /> (every prefab a
		///     build ships). Baked now when this thread has the GL context, else from the cache.
		/// </summary>
		public static Result Collect(ProjectInfo project, IEnumerable<string> scenes, Action<string> log, IEnumerable<string> shippedPrefabs = null)
		{
			var result = new Result();
			var requests = new Dictionary<string, Request>(StringComparer.OrdinalIgnoreCase);
			var prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (shippedPrefabs != null)
			{
				foreach (string path in shippedPrefabs)
				{
					if (path != null && path.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
					{
						prefabs.Add(path);
					}
				}
			}

			foreach (string scene in scenes)
			{
				try
				{
					CollectRequests(File.ReadAllBytes(AssetDatabase.ToAbsolute(scene)), requests, prefabs, assets);
				}
				catch (Exception e)
				{
					log("Warning: the distance fields of " + scene + " could not be listed (" + e.Message + ").");
				}
			}

			foreach (string path in AssetDatabase.GetAllAssetPaths(Prefab.Extension))
			{
				if (Resources.IsInResources(path))
				{
					prefabs.Add(path);
				}
			}

			foreach (string path in AssetDatabase.GetAllAssetPaths(ScriptableObjectAssets.Extension))
			{
				if (Resources.IsInResources(path))
				{
					assets.Add(path);
				}
			}

			// Prefabs reference prefabs and ScriptableObjects, ScriptableObjects reference both: until nothing new turns up.
			var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var queue = new Queue<string>(prefabs);
			foreach (string asset in assets)
			{
				queue.Enqueue(asset);
			}

			while (queue.Count > 0)
			{
				string path = queue.Dequeue();
				if (!done.Add(path))
				{
					continue;
				}

				try
				{
					var referencedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					if (path.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
					{
						CollectPrefabRequests(path, requests, referencedPrefabs, referencedAssets);
					}
					else
					{
						CollectAssetReferences(path, referencedPrefabs, referencedAssets);
					}

					foreach (string other in referencedPrefabs)
					{
						queue.Enqueue(other);
					}

					foreach (string other in referencedAssets)
					{
						queue.Enqueue(other);
					}
				}
				catch (Exception e)
				{
					log("Warning: the distance fields of " + path + " could not be listed (" + e.Message + ").");
				}
			}

			DistanceFieldAtlas atlas = null;
			try
			{
				// One stamp per model: its meshes and shapes are requests of their own, and the stamp reads the whole file.
				var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
				foreach (Request request in requests.Values)
				{
					Vector3i key = request.Shape;
					string cacheFile = CacheFile(project, request, key);
					if (!stamps.TryGetValue(request.ModelPath, out string stamp))
					{
						stamp = Stamp(request.ModelPath);
						stamps[request.ModelPath] = stamp;
					}

					BakedDistanceField field = ReadCache(cacheFile, stamp);
					if (field != null)
					{
						result.Cached++;
					}
					else if (GraphicsThread.HasContext)
					{
						Mesh mesh = ModelAssets.LoadMesh(request.ModelPath, request.MeshName);
						if (mesh == null || mesh.TriangleCount == 0)
						{
							continue;
						}

						// The baker reads each field itself and writes its own cache (no copy on the mesh, no store), so a
						// field is built whole when asked for (still in short dispatches: no TDR with a huge mesh).
						atlas ??= new DistanceFieldAtlas(DistanceFieldAtlas.AtlasXY) { KeepsFields = false, BuildsOverFrames = false };
						atlas.Reset();
						MeshDistanceField built = atlas.GetOrBuild(mesh, key);
						if (built == null)
						{
							continue;
						}

						field = new BakedDistanceField { ShapeKey = key, Size = built.Size, Voxels = atlas.Read(built) };
						DistanceFieldFile.Write(cacheFile, stamp, new[] { new KeyValuePair<string, BakedDistanceField>(request.MeshName, field) });
						result.Baked++;
					}
					else
					{
						result.Missing++;
						continue;
					}

					if (!result.Models.TryGetValue(request.ModelPath, out List<KeyValuePair<string, BakedDistanceField>> fields))
					{
						fields = new List<KeyValuePair<string, BakedDistanceField>>();
						result.Models[request.ModelPath] = fields;
					}

					fields.Add(new KeyValuePair<string, BakedDistanceField>(request.MeshName, field));
				}
			}
			finally
			{
				atlas?.Delete();
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		/// <summary>The hierarchy of a document as far as the fields need it: parents, local scales, GI renderers' meshes.</summary>
		private sealed class ScaleTree
		{
			public readonly Dictionary<ulong, ulong> Parents = new Dictionary<ulong, ulong>();
			public readonly Dictionary<ulong, Vector3> LocalScales = new Dictionary<ulong, Vector3>();
			public readonly List<(ulong Owner, string ModelPath, string MeshName)> Renderers = new List<(ulong, string, string)>();

			/// <summary>Prefab files the components reference (prefab fields: what scripts may instantiate).</summary>
			public HashSet<string> Prefabs;

			/// <summary>ScriptableObject assets the components reference (their prefab fields are followed in turn).</summary>
			public HashSet<string> Assets;

			public void AddObject(ulong id, ulong parent)
			{
				Parents[id] = parent;
			}

			/// <summary>A component's generic data: a Transform's scale, a MeshRenderer's mesh, prefab references anywhere.</summary>
			public void AddComponent(ulong owner, SerializedNode data)
			{
				string type = data?.TypeName ?? "";
				if (type.StartsWith("Transform,", StringComparison.Ordinal) && data.Find("m_LocalScale")?.Value is Vector3 scale)
				{
					LocalScales[owner] = scale;
				}
				else if (type.StartsWith("MeshRenderer,", StringComparison.Ordinal) && !(data.Find("ContributesToGI")?.Value is bool gi && !gi)
					&& data.Find("mesh")?.Value is AssetReference mesh && !mesh.IsNull)
				{
					string modelPath = AssetDatabase.GetPath(mesh.Guid) ?? mesh.Path;
					if (modelPath != null && ModelAssets.IsModelFile(modelPath))
					{
						Renderers.Add((owner, modelPath, mesh.Name));
					}
				}

				CollectPrefabReferences(data, 0);
			}

			/// <summary>The objects of a prefab instance (its resolved prefab with the instance's changes) in the document's ids.</summary>
			public void AddInstance(PrefabInstanceRecord record)
			{
				string path = PrefabCache.PathOf(record.Prefab);
				ResolvedPrefab prefab = path != null ? PrefabCache.Get(path) : null;
				if (prefab?.Root == null)
				{
					return;
				}

				Prefabs?.Add(path);
				var removedObjects = new HashSet<ulong>(record.RemovedObjects);
				var removedComponents = new HashSet<ulong>(record.RemovedComponents);
				Dictionary<ulong, List<PropertyModification>> modifications = PrefabResolver.GroupByTarget(record.Modifications);
				var skipped = new HashSet<ulong>();
				foreach (PrefabObject obj in prefab.Objects)
				{
					if (removedObjects.Contains(obj.Id) || (obj.Parent != 0 && skipped.Contains(obj.Parent)))
					{
						skipped.Add(obj.Id);
						continue;
					}

					ulong id = record.Id ^ obj.Id;
					AddObject(id, obj.Parent != 0 ? record.Id ^ obj.Parent : record.Parent);
					foreach (PrefabComponent component in obj.Components)
					{
						if (removedComponents.Contains(component.Id))
						{
							continue;
						}

						SerializedNode data = component.Data;
						if (modifications.TryGetValue(component.Id, out List<PropertyModification> changes))
						{
							data = GenericValues.CloneNode(data);
							foreach (PropertyModification change in changes)
							{
								GenericValues.Apply(data, change);
							}
						}

						AddComponent(id, data);
					}
				}

				foreach (DocumentComponent added in record.AddedComponents)
				{
					AddComponent(record.Id ^ added.GameObject, added.Data);
				}
			}

			public void Request(Dictionary<string, Request> requests)
			{
				foreach ((ulong owner, string modelPath, string meshName) in Renderers)
				{
					Add(requests, modelPath, meshName, WorldScale(owner, Parents, LocalScales, 0));
					Add(requests, modelPath, meshName, Vector3.One);
				}
			}

			private void CollectPrefabReferences(object value, int depth)
			{
				if (Prefabs == null || depth > 16)
				{
					return;
				}

				switch (value)
				{
					case SerializedNode node:
						foreach (SerializedProperty property in node.Properties)
						{
							CollectPrefabReferences(property.Value, depth + 1);
						}

						break;
					case SerializedArray array when array.Bytes == null:
						foreach (object item in array.Items)
						{
							CollectPrefabReferences(item, depth + 1);
						}

						break;
					case SerializedDictionary dictionary:
						foreach (KeyValuePair<object, object> entry in dictionary.Entries)
						{
							CollectPrefabReferences(entry.Value, depth + 1);
						}

						break;
					case AssetReference reference when !reference.IsNull:
						Classify(PrefabCache.PathOf(reference), Prefabs, Assets);
						break;
				}
			}
		}

		/// <summary>A referenced file that can lead to GI meshes: a prefab, or a ScriptableObject asset (its references are followed).</summary>
		private static void Classify(string path, HashSet<string> prefabs, HashSet<string> assets)
		{
			if (path == null)
			{
				return;
			}

			if (path.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
			{
				prefabs?.Add(path);
			}
			else if (path.EndsWith(ScriptableObjectAssets.Extension, StringComparison.OrdinalIgnoreCase))
			{
				assets?.Add(path);
			}
		}

		/// <summary>The prefabs and ScriptableObject assets a ScriptableObject asset references (as generic data: no type is loaded).</summary>
		private static void CollectAssetReferences(string assetPath, HashSet<string> prefabs, HashSet<string> assets)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			if (!File.Exists(file))
			{
				return;
			}

			byte[] data = File.ReadAllBytes(file);
			if (!Documents.IsDocument(data))
			{
				return;
			}

			foreach (AssetReference reference in DocumentReferences.Collect(data).Assets)
			{
				Classify(PrefabCache.PathOf(reference), prefabs, assets);
			}
		}

		/// <summary>
		///     The GI meshes of a scene document (its prefab instances' too) and their renderers' world scales (the product
		///     of the local scales up the hierarchy, as <see cref="Transform.lossyScale" /> computes it), plus scale 1 for
		///     each mesh; the prefab files the scene uses go into <paramref name="prefabs" />.
		/// </summary>
		private static void CollectRequests(byte[] data, Dictionary<string, Request> requests, HashSet<string> prefabs, HashSet<string> assets)
		{
			var tree = new ScaleTree { Prefabs = prefabs, Assets = assets };
			foreach (DocumentEntry entry in SceneSerializer.ReadDocument(data).Entries)
			{
				if (entry.Instance != null)
				{
					tree.AddInstance(entry.Instance);
					continue;
				}

				tree.AddObject(entry.Object.Id, entry.Object.Parent);
				foreach (DocumentComponent component in entry.Object.Components)
				{
					tree.AddComponent(entry.Object.Id, component.Data);
				}
			}

			tree.Request(requests);
		}

		/// <summary>The GI meshes of a prefab at the scales inside it (an instance made at runtime keeps the prefab's root scale).</summary>
		private static void CollectPrefabRequests(string prefabPath, Dictionary<string, Request> requests, HashSet<string> prefabs, HashSet<string> assets)
		{
			ResolvedPrefab prefab = PrefabCache.Get(prefabPath);
			if (prefab?.Root == null)
			{
				return;
			}

			var tree = new ScaleTree { Prefabs = prefabs, Assets = assets };
			foreach (PrefabObject obj in prefab.Objects)
			{
				tree.AddObject(obj.Id, obj.Parent);
				foreach (PrefabComponent component in obj.Components)
				{
					tree.AddComponent(obj.Id, component.Data);
				}
			}

			tree.Request(requests);
		}

		private static Vector3 WorldScale(ulong id, Dictionary<ulong, ulong> parents, Dictionary<ulong, Vector3> localScales, int depth)
		{
			Vector3 local = localScales.TryGetValue(id, out Vector3 scale) ? scale : Vector3.One;
			ulong parent = parents.TryGetValue(id, out ulong p) ? p : 0;
			return parent == 0 || depth > 64 || !parents.ContainsKey(parent) ? local : WorldScale(parent, parents, localScales, depth + 1) * local;
		}

		private static void Add(Dictionary<string, Request> requests, string modelPath, string meshName, Vector3 scale)
		{
			if (meshName == null)
			{
				// A reference without a mesh name means the model's first mesh: asked for under its name, the one the
				// player's loader looks for in the model's field file (and the editor's own cache at run time).
				try
				{
					Model model = ModelAssets.Load(modelPath);
					meshName = model.Meshes.Count > 0 ? model.Meshes[0].SubAssetName : null;
				}
				catch (Exception)
				{
					// Unreadable model: no field for it.
				}

				if (meshName == null)
				{
					return;
				}
			}

			// Every uniform scale is one shape: one field per mesh for all of them.
			Vector3i key = DistanceFieldAtlas.ShapeKey(scale);
			requests[$"{modelPath}|{meshName}|{key.X},{key.Y},{key.Z}"] = new Request { ModelPath = modelPath, MeshName = meshName, Shape = key };
		}

		private static string CacheFile(ProjectInfo project, Request request, Vector3i key)
		{
			return CacheFile(project, request.ModelPath, request.MeshName, key);
		}

		private static string CacheFile(ProjectInfo project, string modelPath, string meshName, Vector3i key)
		{
			string guid = AssetDatabase.GetGuid(modelPath) ?? Hash(modelPath);
			return Path.Combine(project.LibraryPath, "DistanceFieldCache", $"{guid}-{Hash(meshName ?? "")}-{key.X}_{key.Y}_{key.Z}{DistanceFieldFile.Extension}");
		}

		/// <summary>
		///     The editor's distance fields at run time (edit and play mode): the renderers' atlases take a mesh's field from
		///     the baker's cache in the project's Library instead of building it, and what they do build goes into it, so a
		///     field is computed once per mesh and shape, not in every editor session, and a game build finds it baked.
		///     Meshes of model files only (a mesh built in code has no file to tell when it changed).
		/// </summary>
		public sealed class Store : IDistanceFieldStore
		{
			private readonly ProjectInfo project;
			private readonly object sync = new object();

			// Per model path: the files' sizes and times the content stamp was taken at (so it is not hashed again).
			private readonly Dictionary<string, (long Length, long Ticks, long MetaLength, long MetaTicks, string Stamp)> stamps
				= new Dictionary<string, (long, long, long, long, string)>(StringComparer.OrdinalIgnoreCase);

			private bool indexRead;
			private bool indexDirty;

			// The models whose meshes all have their field at the uniform shape (the full import, ModelFieldsCurrent): the
			// model's stamp then and the field files' names.
			private readonly Dictionary<string, (string Stamp, string[] Files)> imported = new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase);
			private bool importedRead;
			private bool importedDirty;

			public Store(ProjectInfo project)
			{
				this.project = project;
			}

			private string IndexFile => Path.Combine(project.LibraryPath, "DistanceFieldCache", "stamps.txt");
			private string ImportedFile => Path.Combine(project.LibraryPath, "DistanceFieldCache", "models.txt");

			/// <summary>The shape of every uniform scale: the field every renderer without a stretched scale uses.</summary>
			public static Vector3i UniformShape => DistanceFieldAtlas.ShapeKey(Vector3.One);

			/// <summary>
			///     Every mesh of the model has its field at the uniform shape for the model's current files (a record of the
			///     full import, <see cref="BuildModelFields" />, and the field files there). Any thread; a model that cannot be
			///     read counts as current (its import reports it).
			/// </summary>
			public bool ModelFieldsCurrent(string modelPath)
			{
				string stamp;
				try
				{
					stamp = StampOf(modelPath);
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					return true;
				}

				(string Stamp, string[] Files) record;
				lock (sync)
				{
					ReadImported();
					if (!imported.TryGetValue(modelPath, out record) || record.Stamp != stamp)
					{
						return false;
					}
				}

				string folder = Path.Combine(project.LibraryPath, "DistanceFieldCache");
				foreach (string file in record.Files)
				{
					if (!File.Exists(Path.Combine(folder, file)))
					{
						return false;
					}
				}

				return true;
			}

			/// <summary>
			///     The model's full import of distance fields: the field of every mesh at the uniform shape, built on the GPU when
			///     the cache has none for the model's current files (with <paramref name="atlas" />, made on first need: the
			///     caller deletes it). Then recorded for <see cref="ModelFieldsCurrent" /> (<see cref="SaveImported" /> writes the
			///     record). Main thread with the graphics context. Returns the fields built.
			/// </summary>
			public int BuildModelFields(string modelPath, ref DistanceFieldAtlas atlas)
			{
				string stamp = StampOf(modelPath);
				// A model in use gives its meshes; else they are read from its import and dropped after (the GPU never gets them).
				Model model = ModelAssets.IsLoaded(modelPath) ? ModelAssets.Load(modelPath) : new Model(modelPath, ModelAssets.LoadData(modelPath));
				Vector3i key = UniformShape;
				var files = new List<string>();
				int built = 0;
				foreach (Mesh mesh in model.Meshes)
				{
					if (!IsStored(mesh) || mesh.TriangleCount == 0)
					{
						continue;
					}

					string file = CacheFile(project, modelPath, mesh.SubAssetName, key);
					files.Add(Path.GetFileName(file));
					if (ReadCache(file, stamp) != null)
					{
						continue;
					}

					// The build's own atlas (one field at a time, built whole, read back here), as a game build bakes them.
					atlas ??= new DistanceFieldAtlas(DistanceFieldAtlas.AtlasXY) { KeepsFields = false, BuildsOverFrames = false };
					atlas.Reset();
					MeshDistanceField field = atlas.GetOrBuild(mesh, key);
					if (field == null)
					{
						files.RemoveAt(files.Count - 1);
						continue;
					}

					var baked = new BakedDistanceField { ShapeKey = key, Size = field.Size, Voxels = atlas.Read(field) };
					DistanceFieldFile.Write(file, stamp, new[] { new KeyValuePair<string, BakedDistanceField>(mesh.SubAssetName, baked) });
					built++;
				}

				lock (sync)
				{
					ReadImported();
					imported[modelPath] = (stamp, files.ToArray());
					importedDirty = true;
				}

				return built;
			}

			/// <summary>Writes the record of the models' full imports of fields and the models' stamps when they changed (after an import).</summary>
			public void SaveImported()
			{
				bool stampsChanged;
				lock (sync)
				{
					stampsChanged = indexDirty;
				}

				if (stampsChanged)
				{
					WriteIndex();
				}

				var lines = new List<string>();
				lock (sync)
				{
					if (!importedDirty)
					{
						return;
					}

					importedDirty = false;
					foreach (KeyValuePair<string, (string Stamp, string[] Files)> entry in imported)
					{
						lines.Add(string.Join("\t", entry.Key, entry.Value.Stamp, string.Join("|", entry.Value.Files)));
					}
				}

				try
				{
					lines.Sort(StringComparer.Ordinal);
					Directory.CreateDirectory(Path.GetDirectoryName(ImportedFile));
					string temporary = ImportedFile + ".tmp";
					File.WriteAllLines(temporary, lines);
					File.Move(temporary, ImportedFile, true);
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					// Only a record: without it the next import reads the fields' files to find them current.
					Debug.LogWarning("[SDF] Cannot write " + ImportedFile + ": " + e.Message);
				}
			}

			private void ReadImported()
			{
				if (importedRead)
				{
					return;
				}

				importedRead = true;
				try
				{
					if (!File.Exists(ImportedFile))
					{
						return;
					}

					foreach (string line in File.ReadAllLines(ImportedFile))
					{
						string[] parts = line.Split('\t');
						if (parts.Length == 3)
						{
							imported[parts[0]] = (parts[1], parts[2].Length > 0 ? parts[2].Split('|') : Array.Empty<string>());
						}
					}
				}
				catch (Exception)
				{
					// An unreadable record: the models' fields are looked at again.
					imported.Clear();
				}
			}

			public BakedDistanceField Load(Mesh mesh, Vector3i shapeKey, Vector3i size)
			{
				if (!IsStored(mesh))
				{
					return null;
				}

				string file = CacheFile(project, mesh.AssetPath, mesh.SubAssetName, shapeKey);
				if (!File.Exists(file))
				{
					return null;
				}

				BakedDistanceField field = ReadCache(file, StampOf(mesh.AssetPath));
				return field != null && field.Size == size ? field : null;
			}

			public void Save(Mesh mesh, BakedDistanceField field)
			{
				if (!IsStored(mesh) || field?.Voxels == null)
				{
					return;
				}

				string modelPath = mesh.AssetPath;
				string meshName = mesh.SubAssetName;
				string file = CacheFile(project, modelPath, meshName, field.ShapeKey);
				// Written beside the frames (a file of tens to hundreds of KB, and the model's stamp when it is not known yet).
				System.Threading.Tasks.Task.Run(() =>
				{
					try
					{
						DistanceFieldFile.Write(file, StampOf(modelPath), new[] { new KeyValuePair<string, BakedDistanceField>(meshName, field) });
					}
					catch (Exception e)
					{
						Debug.LogWarning($"[SDF] The distance field of {modelPath}#{meshName} could not be cached ({e.Message}).");
					}
				});
			}

			private static bool IsStored(Mesh mesh)
			{
				return mesh != null && mesh.SubAssetName != null && !string.IsNullOrEmpty(mesh.AssetPath) && mesh.MirrorSource == null && ModelAssets.IsModelFile(mesh.AssetPath);
			}

			// The baker's stamp of the model (its content and import settings), hashed again only when a file changed.
			private string StampOf(string modelPath)
			{
				string absolute = AssetDatabase.ToAbsolute(modelPath);
				var model = new FileInfo(absolute);
				var meta = new FileInfo(absolute + AssetDatabase.MetaExtension);
				long length = model.Exists ? model.Length : -1;
				long ticks = model.Exists ? model.LastWriteTimeUtc.Ticks : 0;
				long metaLength = meta.Exists ? meta.Length : -1;
				long metaTicks = meta.Exists ? meta.LastWriteTimeUtc.Ticks : 0;
				lock (sync)
				{
					ReadIndex();
					if (stamps.TryGetValue(modelPath, out (long Length, long Ticks, long MetaLength, long MetaTicks, string Stamp) known)
						&& known.Length == length && known.Ticks == ticks && known.MetaLength == metaLength && known.MetaTicks == metaTicks)
					{
						return known.Stamp;
					}
				}

				string stamp = Stamp(modelPath);
				lock (sync)
				{
					stamps[modelPath] = (length, ticks, metaLength, metaTicks, stamp);
					if (!indexDirty)
					{
						indexDirty = true;
						// One write for the stamps of a burst (a scene's meshes asked for in one frame).
						System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => WriteIndex());
					}
				}

				return stamp;
			}

			private void ReadIndex()
			{
				if (indexRead)
				{
					return;
				}

				indexRead = true;
				try
				{
					if (!File.Exists(IndexFile))
					{
						return;
					}

					foreach (string line in File.ReadAllLines(IndexFile))
					{
						string[] parts = line.Split('\t');
						if (parts.Length == 6 && long.TryParse(parts[1], out long length) && long.TryParse(parts[2], out long ticks)
							&& long.TryParse(parts[3], out long metaLength) && long.TryParse(parts[4], out long metaTicks))
						{
							stamps[parts[0]] = (length, ticks, metaLength, metaTicks, parts[5]);
						}
					}
				}
				catch (Exception)
				{
					// An unreadable index: the stamps are taken again.
					stamps.Clear();
				}
			}

			private void WriteIndex()
			{
				try
				{
					var lines = new List<string>();
					lock (sync)
					{
						indexDirty = false;
						foreach (KeyValuePair<string, (long Length, long Ticks, long MetaLength, long MetaTicks, string Stamp)> entry in stamps)
						{
							lines.Add(string.Join("\t", entry.Key, entry.Value.Length, entry.Value.Ticks, entry.Value.MetaLength, entry.Value.MetaTicks, entry.Value.Stamp));
						}
					}

					lines.Sort(StringComparer.Ordinal);
					Directory.CreateDirectory(Path.GetDirectoryName(IndexFile));
					string temporary = IndexFile + ".tmp";
					File.WriteAllLines(temporary, lines);
					File.Move(temporary, IndexFile, true);
				}
				catch (Exception)
				{
					// The index is only a shortcut: without it the stamps are hashed again next time.
				}
			}
		}

		/// <summary>
		///     What a field depends on: the model file's content and its import settings, the field layout and this baker.
		///     Contents, not file times: a fresh checkout (CI) gives every file a new time, which made every cached field
		///     stale.
		/// </summary>
		private static string Stamp(string modelPath)
		{
			string absolute = AssetDatabase.ToAbsolute(modelPath);
			string meta = absolute + AssetDatabase.MetaExtension;
			string metaHash = File.Exists(meta) ? Hash(File.ReadAllText(meta)) : "";
			using FileStream model = File.OpenRead(absolute);
			string modelHash = Convert.ToHexString(SHA256.HashData(model));
			return $"{Version}|{DistanceFieldAtlas.TexelsAlongMaxAxis}|{DistanceFieldAtlas.Padding}|{DistanceFieldAtlas.AtlasXY}|{model.Length}|{modelHash}|{metaHash}";
		}

		private static BakedDistanceField ReadCache(string file, string stamp)
		{
			if (!File.Exists(file))
			{
				return null;
			}

			try
			{
				List<KeyValuePair<string, BakedDistanceField>> fields = DistanceFieldFile.Read(file, out string cachedStamp);
				return fields != null && fields.Count == 1 && cachedStamp == stamp ? fields[0].Value : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static string Hash(string text)
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
		}

		#endregion
	}
}
