using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LELEngine.Rendering;
using LELEngine.Rendering.DistanceField;
using LELEngine.Serialization;
using OpenTK.Mathematics;

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
		///     the game may instantiate (those the scenes reference, those in Resources folders, at the scales inside them):
		///     baked now when this thread has the GL context, else from the cache.
		/// </summary>
		public static Result Collect(ProjectInfo project, IEnumerable<string> scenes, Action<string> log)
		{
			var result = new Result();
			var requests = new Dictionary<string, Request>(StringComparer.OrdinalIgnoreCase);
			var prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string scene in scenes)
			{
				try
				{
					CollectRequests(File.ReadAllBytes(AssetDatabase.ToAbsolute(scene)), requests, prefabs);
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

			var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var queue = new Queue<string>(prefabs);
			while (queue.Count > 0)
			{
				string path = queue.Dequeue();
				if (!done.Add(path))
				{
					continue;
				}

				try
				{
					var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					CollectPrefabRequests(path, requests, referenced);
					foreach (string other in referenced)
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

						atlas ??= new DistanceFieldAtlas(DistanceFieldAtlas.AtlasXY);
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
						string path = PrefabCache.PathOf(reference);
						if (path != null && path.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
						{
							Prefabs.Add(path);
						}

						break;
				}
			}
		}

		/// <summary>
		///     The GI meshes of a scene document (its prefab instances' too) and their renderers' world scales (the product
		///     of the local scales up the hierarchy, as <see cref="Transform.lossyScale" /> computes it), plus scale 1 for
		///     each mesh; the prefab files the scene uses go into <paramref name="prefabs" />.
		/// </summary>
		private static void CollectRequests(byte[] data, Dictionary<string, Request> requests, HashSet<string> prefabs)
		{
			var tree = new ScaleTree { Prefabs = prefabs };
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
		private static void CollectPrefabRequests(string prefabPath, Dictionary<string, Request> requests, HashSet<string> prefabs)
		{
			ResolvedPrefab prefab = PrefabCache.Get(prefabPath);
			if (prefab?.Root == null)
			{
				return;
			}

			var tree = new ScaleTree { Prefabs = prefabs };
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
			// Every uniform scale is one shape: one field per mesh for all of them.
			Vector3i key = DistanceFieldAtlas.ShapeKey(scale);
			requests[$"{modelPath}|{meshName}|{key.X},{key.Y},{key.Z}"] = new Request { ModelPath = modelPath, MeshName = meshName, Shape = key };
		}

		private static string CacheFile(ProjectInfo project, Request request, Vector3i key)
		{
			string guid = AssetDatabase.GetGuid(request.ModelPath) ?? Hash(request.ModelPath);
			return Path.Combine(project.LibraryPath, "DistanceFieldCache", $"{guid}-{Hash(request.MeshName ?? "")}-{key.X}_{key.Y}_{key.Z}{DistanceFieldFile.Extension}");
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
