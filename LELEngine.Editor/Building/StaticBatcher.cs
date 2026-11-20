using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LELEngine.Serialization;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Static batching (Project Settings > Graphics > Static Batching) of a scene document, for a game build (every
	///     scene it ships) and for the editor's play mode (the scene it starts and those the game loads), through
	///     <see cref="StaticBatchCache" />: the static objects' renderers (<see cref="GameObject.isStatic" />) that the
	///     renderer would draw one by one are merged into batches: a new object per batch with the merged mesh
	///     (<see cref="MeshCombiner" />), a renderer for it and a <see cref="StaticBatch" /> naming its parts. The scene in
	///     the project is not touched, and nothing of the batched objects changes in the batched scene: the batch takes
	///     their renderers over when the scene loads (see <see cref="StaticBatch" />: the GI keeps the objects themselves, a
	///     part that is disabled or destroyed disappears from the batch).
	///     <para>
	///         What is merged. A renderer of a static object, enabled on an object active in the scene, with a mesh of a
	///         model file, whose every material is drawn one by one:
	///         <list type="bullet">
	///             <item>transparent materials, however they blend: the transparent pass sorts a batch part by part, where
	///             each part's own renderer sorted, so the picture is the unbatched one (a first version sorted a batch as
	///             one object: alpha blended quads behind an additive batch came out in front of it, measured);</item>
	///             <item>opaque materials whose shader has no GPU-driven variant (no Engine/Instancing.glsl).</item>
	///         </list>
	///         Left as they are: opaque materials the GPU-driven path draws (one multi-draw per material whatever the
	///         number of objects: merging would only copy their vertices; measured on 4,096 static lit cubes, a frame's
	///         CPU work was 11.5 ms as they are and 10.4 to 13.5 ms merged into two batches, the GPU's 17.2 and 16.7 ms:
	///         nothing to gain), renderers of LOD groups (a level is switched per object), shaders that say Batching Off
	///         or draw their own depth passes or grab the scene, renderers mixing the kinds above.
	///     </para>
	///     <para>
	///         How. Renderers are grouped by layer, shadow and GI flags and by kind (the opaque ones together, a sub-mesh
	///         per material; transparent ones per list of materials, so a batch sorts as its materials' queues say). A group
	///         is put in order along a space-filling curve and cut into batches of at most <see cref="MaxVertices" />
	///         vertices: a batch is a cluster of neighbours, small where the objects are dense, so the cameras still cull
	///         and sort by places and no draw is larger than that. The documents are read as generic data (no type is
	///         loaded, as the rest of the build): prefab instances are followed through their resolved prefabs.
	///         The same scene gives the same files and ids.
	///     </para>
	/// </summary>
	internal static class StaticBatcher
	{
		#region PublicFields

		/// <summary>The most vertices a batch has (a renderer with more stays on its own).</summary>
		public const int MaxVertices = 65536;

		/// <summary>What the batching of one scene did.</summary>
		public sealed class Summary
		{
			/// <summary>Static renderers found (enabled, on active objects, with a mesh).</summary>
			public int StaticRenderers;

			public int Batches;

			/// <summary>Renderers that are parts of batches.</summary>
			public int Batched;

			public int Vertices;
			public int Triangles;

			/// <summary>Why the others stay as they are, with their counts (in the order first met).</summary>
			public readonly List<KeyValuePair<string, int>> Left = new List<KeyValuePair<string, int>>();

			/// <summary>The batches' meshes (the asset paths the scene names them by).</summary>
			public readonly List<string> Files = new List<string>();

			public void Leave(string reason)
			{
				for (int i = 0; i < Left.Count; i++)
				{
					if (Left[i].Key == reason)
					{
						Left[i] = new KeyValuePair<string, int>(reason, Left[i].Value + 1);
						return;
					}
				}

				Left.Add(new KeyValuePair<string, int>(reason, 1));
			}

			public override string ToString()
			{
				var text = new StringBuilder();
				text.Append(StaticRenderers.ToString(CultureInfo.InvariantCulture)).Append(" static renderer").Append(StaticRenderers == 1 ? "" : "s");
				if (Batches > 0)
				{
					text.Append(": ").Append(Batched).Append(" merged into ").Append(Batches).Append(Batches == 1 ? " batch (" : " batches (")
						.Append(Vertices.ToString("N0", CultureInfo.InvariantCulture)).Append(" vertices, ").Append(Triangles.ToString("N0", CultureInfo.InvariantCulture)).Append(" triangles)");
				}
				else
				{
					text.Append(": nothing to merge");
				}

				if (Left.Count > 0)
				{
					text.Append("; left as they are: ");
					for (int i = 0; i < Left.Count; i++)
					{
						text.Append(i > 0 ? ", " : "").Append(Left[i].Value).Append(' ').Append(Left[i].Key);
					}
				}

				return text.ToString();
			}
		}

		#endregion

		#region PrivateFields

		private const string GpuDriven = "drawn GPU-driven (one draw per material already)";

		private sealed class Node
		{
			public ulong Id;
			public ulong Parent;
			public bool Active = true;
			public bool Static;
			public int Layer;
			public Vector3 Position;
			public Quaternion Rotation = Quaternion.Identity;
			public Vector3 Scale = Vector3.One;
			public readonly List<RendererData> Renderers = new List<RendererData>();

			// Worked out on demand (the parents first).
			public bool Resolved;
			public Matrix4 World;
			public bool ActiveInHierarchy;
		}

		private sealed class RendererData
		{
			public ulong Id;
			public bool Enabled = true;
			public SerializedNode Data;
			public Node Owner;
		}

		// A renderer that can be merged: what it draws and where.
		private sealed class Part
		{
			public RendererData Renderer;
			public Mesh Mesh;
			public List<Material> Materials;
			public List<AssetReference> MaterialReferences;
			public Matrix4 World;
			public Vector3 Centre;
			public ulong Curve;
		}

		private sealed class Group
		{
			public string Key;
			public int Layer;
			public bool CastShadows;
			public bool ReceiveShadows;
			public bool ReceiveGI;
			public readonly List<Part> Parts = new List<Part>();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Keeps a batch's mesh and gives the reference the scene names it by: given the mesh, the batch's number in its
		///     scene and its name (<see cref="StaticBatchCache" /> writes it into the project's Library).
		/// </summary>
		public delegate AssetReference MeshStore(Mesh mesh, int index, string name);

		/// <summary>
		///     The batches of one scene document (JSON or binary; none for another kind of document): their meshes are merged
		///     and go to <paramref name="store" />, what comes back describes each batch's object (<see cref="Append" /> puts
		///     them into the document, <see cref="Apply" /> into the scene loaded from it). <paramref name="sceneAssetPath" />
		///     names the scene (its batches' ids). <paramref name="inputs" /> gets the asset files the result depends on
		///     besides the document: the prefabs of its instances, the models and the materials of its static renderers and
		///     their shaders (what a cache of the result checks).
		/// </summary>
		public static List<StaticBatchData> Analyze(byte[] scene, string sceneAssetPath, MeshStore store, ISet<string> inputs, out Summary summary)
		{
			summary = new Summary();
			var batches = new List<StaticBatchData>();
			inputs ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!Documents.IsDocument(scene))
			{
				return batches;
			}

			SceneDocument document = SceneSerializer.ReadDocument(scene);
			if (document.Kind != BinaryDocument.KindScene)
			{
				return batches;
			}

			// 1. The scene's objects, those of its prefab instances included, with what the batching reads of them.
			var nodes = new Dictionary<ulong, Node>();
			var order = new List<Node>();
			var lodRenderers = new HashSet<ulong>();
			var usedIds = new HashSet<ulong>();
			foreach (DocumentEntry entry in document.Entries)
			{
				if (entry.Instance != null)
				{
					AddInstance(entry.Instance, nodes, order, lodRenderers, usedIds, inputs);
				}
				else if (entry.Object != null)
				{
					AddObject(entry.Object, nodes, order, lodRenderers, usedIds);
				}
			}

			// 2. The renderers that can be merged, by group.
			var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
			var groupOrder = new List<Group>();
			foreach (Node node in order)
			{
				foreach (RendererData renderer in node.Renderers)
				{
					Consider(renderer, nodes, lodRenderers, groups, groupOrder, inputs, summary);
				}
			}

			// 3. Each group cut into batches of neighbours; their meshes.
			foreach (Group group in groupOrder)
			{
				foreach (List<Part> batch in Cut(group, summary))
				{
					batches.Add(MakeBatch(group, batch, batches.Count, sceneAssetPath ?? "Scene", store, usedIds, summary));
				}
			}

			return batches;
		}

		/// <summary>
		///     A scene document with the batches' objects added (a build's scene), as a binary document; the bytes given
		///     when there are none.
		/// </summary>
		public static byte[] Append(byte[] scene, IReadOnlyList<StaticBatchData> batches)
		{
			if (batches == null || batches.Count == 0)
			{
				return scene;
			}

			SceneDocument document = SceneSerializer.ReadDocument(scene);
			foreach (StaticBatchData batch in batches)
			{
				document.Entries.Add(new DocumentEntry { Object = ObjectOf(batch) });
			}

			return SceneSerializer.WriteDocument(document, DocumentFormat.Binary);
		}

		/// <summary>
		///     The batches' objects added to the scene loaded from the document they were worked out for, before it starts
		///     (the editor's play mode: the scene read from the snapshot or from its file): each with its renderer of the
		///     merged mesh and its <see cref="StaticBatch" /> taking over the parts' renderers, found by their ids. Returns how
		///     many were added.
		/// </summary>
		public static int Apply(Scene scene, IReadOnlyList<StaticBatchData> batches)
		{
			if (scene == null || batches == null || batches.Count == 0)
			{
				return 0;
			}

			var renderers = new Dictionary<ulong, MeshRenderer>();
			foreach (GameObject go in scene.GameObjects)
			{
				foreach (Behaviour component in go.Components)
				{
					if (component is MeshRenderer renderer)
					{
						renderers[renderer.Id] = renderer;
					}
				}
			}

			int added = 0;
			foreach (StaticBatchData data in batches)
			{
				Mesh mesh = ModelAssets.LoadMesh(data.Mesh.Path, data.Mesh.Name);
				if (mesh == null)
				{
					Debug.LogWarning($"[StaticBatching] {data.Name}: its mesh {data.Mesh.Path} is missing; its parts draw on their own");
					continue;
				}

				var parts = new MeshRenderer[data.Renderers.Length];
				for (int i = 0; i < parts.Length; i++)
				{
					renderers.TryGetValue(data.Renderers[i], out parts[i]);
				}

				var materials = new List<Material>(data.Materials.Length);
				foreach (AssetReference reference in data.Materials)
				{
					string path = reference.IsNull ? null : AssetDatabase.GetPath(reference.Guid) ?? reference.Path;
					materials.Add(path != null ? InternalStorage.GetOrCreateMaterial(path) : null);
				}

				var batchObject = new GameObject(data.Name, scene) { layer = data.Layer, isStatic = true };
				batchObject.transform.position = data.Pivot;
				MeshRenderer batchRenderer = batchObject.AddComponent<MeshRenderer>();
				batchRenderer.SetMesh(mesh);
				batchRenderer.SetMaterials(materials);
				batchRenderer.CastShadows = data.CastShadows;
				batchRenderer.ReceiveShadows = data.ReceiveShadows;
				batchRenderer.ContributesToGI = false;
				batchRenderer.ReceiveGI = data.ReceiveGI;
				batchObject.AddComponent<StaticBatch>().SetParts(parts, data.PartRanges, data.Ranges, data.Centres, data.Flat);
				added++;
			}

			return added;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- the scene as objects

		private static void AddObject(DocumentObject obj, Dictionary<ulong, Node> nodes, List<Node> order, HashSet<ulong> lodRenderers, HashSet<ulong> usedIds)
		{
			var node = new Node { Id = obj.Id, Parent = obj.Parent, Active = obj.Active, Static = obj.Static, Layer = obj.Layer };
			usedIds.Add(obj.Id);
			foreach (DocumentComponent component in obj.Components)
			{
				usedIds.Add(component.Id);
				AddComponent(node, component.Id, component.Enabled, component.Data, lodRenderers);
			}

			nodes[node.Id] = node;
			order.Add(node);
		}

		/// <summary>The objects of a prefab instance: its resolved prefab with the instance's changes, in the scene's ids.</summary>
		private static void AddInstance(PrefabInstanceRecord record, Dictionary<ulong, Node> nodes, List<Node> order, HashSet<ulong> lodRenderers, HashSet<ulong> usedIds, ISet<string> inputs)
		{
			usedIds.Add(record.Id);
			string path = PrefabCache.PathOf(record.Prefab);
			ResolvedPrefab prefab = path != null ? PrefabCache.Get(path) : null;
			if (path != null)
			{
				inputs.Add(path);
			}

			if (prefab?.Root == null)
			{
				// A missing prefab: the player shows a placeholder, nothing to draw.
				return;
			}

			// The prefabs nested in it and the bases of a variant are in what it resolved to.
			foreach (string dependency in prefab.Dependencies)
			{
				inputs.Add(dependency);
			}

			var removedObjects = new HashSet<ulong>(record.RemovedObjects);
			var removedComponents = new HashSet<ulong>(record.RemovedComponents);
			Dictionary<ulong, List<PropertyModification>> modifications = PrefabResolver.GroupByTarget(record.Modifications);
			LegacyStatic.MoveModifications(prefab, modifications);
			Func<ulong, ulong> map = id => record.Id ^ id;
			var skipped = new HashSet<ulong>();
			foreach (PrefabObject obj in prefab.Objects)
			{
				if (removedObjects.Contains(obj.Id) || (obj.Parent != 0 && skipped.Contains(obj.Parent)))
				{
					skipped.Add(obj.Id);
					continue;
				}

				var node = new Node { Id = record.Id ^ obj.Id, Parent = obj.Parent != 0 ? record.Id ^ obj.Parent : record.Parent, Active = obj.Active, Static = obj.Static, Layer = obj.Layer };
				if (modifications.TryGetValue(obj.Id, out List<PropertyModification> header))
				{
					foreach (PropertyModification modification in header)
					{
						switch (modification.Path)
						{
							case PropertyModification.ActiveProperty:
								node.Active = modification.Value is bool active && active;
								break;
							case PropertyModification.LayerProperty:
								node.Layer = modification.Value is int layer ? layer : node.Layer;
								break;
							case PropertyModification.StaticProperty:
								node.Static = modification.Value is bool isStatic && isStatic;
								break;
						}
					}
				}

				usedIds.Add(node.Id);
				foreach (PrefabComponent component in obj.Components)
				{
					usedIds.Add(record.Id ^ component.Id);
					if (removedComponents.Contains(component.Id) || !IsRead(component.TypeName))
					{
						continue;
					}

					// In the scene's ids (a LOD group's renderers), with what the instance changes.
					SerializedNode data = GenericValues.CloneNode(component.Data, map);
					bool enabled = component.Enabled;
					if (modifications.TryGetValue(component.Id, out List<PropertyModification> changes))
					{
						foreach (PropertyModification change in changes)
						{
							if (change.Path == PropertyModification.EnabledProperty && !change.HasKey)
							{
								enabled = change.Value is bool flag && flag;
							}
							else
							{
								GenericValues.Apply(data, change);
							}
						}
					}

					AddComponent(node, record.Id ^ component.Id, enabled, data, lodRenderers);
				}

				nodes[node.Id] = node;
				order.Add(node);
			}

			foreach (DocumentComponent added in record.AddedComponents)
			{
				usedIds.Add(added.Id);
				if (nodes.TryGetValue(record.Id ^ added.GameObject, out Node host))
				{
					AddComponent(host, added.Id, added.Enabled, added.Data, lodRenderers);
				}
			}
		}

		// The components the batching reads.
		private static bool IsRead(string typeName)
		{
			return typeName != null && (typeName.StartsWith("Transform,", StringComparison.Ordinal) || typeName.StartsWith("MeshRenderer,", StringComparison.Ordinal)
				|| typeName.StartsWith("LODGroup,", StringComparison.Ordinal));
		}

		private static void AddComponent(Node node, ulong id, bool enabled, SerializedNode data, HashSet<ulong> lodRenderers)
		{
			string type = data?.TypeName ?? "";
			if (type.StartsWith("Transform,", StringComparison.Ordinal))
			{
				if (data.Find("m_LocalPosition")?.Value is Vector3 position)
				{
					node.Position = position;
				}

				if (data.Find("m_LocalRotation")?.Value is Quaternion rotation)
				{
					node.Rotation = rotation;
				}

				if (data.Find("m_LocalScale")?.Value is Vector3 scale)
				{
					node.Scale = scale;
				}
			}
			else if (type.StartsWith("MeshRenderer,", StringComparison.Ordinal))
			{
				node.Renderers.Add(new RendererData { Id = id, Enabled = enabled, Data = data, Owner = node });
			}
			else if (type.StartsWith("LODGroup,", StringComparison.Ordinal))
			{
				// Every level's renderers, of a disabled group too: it may be enabled while the game runs.
				if (data.Find("m_LODs")?.Value is SerializedArray levels && levels.Bytes == null)
				{
					foreach (object level in levels.Items)
					{
						if (level is SerializedNode lod && lod.Find("renderers")?.Value is SerializedArray renderers && renderers.Bytes == null)
						{
							foreach (object renderer in renderers.Items)
							{
								if (renderer is ulong reference && reference != 0)
								{
									lodRenderers.Add(reference);
								}
							}
						}
					}
				}
			}
		}

		private static void Resolve(Node node, Dictionary<ulong, Node> nodes, int depth)
		{
			if (node.Resolved)
			{
				return;
			}

			node.Resolved = true;
			Matrix4 local = Matrix4.CreateScale(node.Scale) * Matrix4.CreateFromQuaternion(node.Rotation) * Matrix4.CreateTranslation(node.Position);
			if (node.Parent != 0 && depth < 256 && nodes.TryGetValue(node.Parent, out Node parent) && parent != node)
			{
				Resolve(parent, nodes, depth + 1);
				node.World = local * parent.World;
				node.ActiveInHierarchy = node.Active && parent.ActiveInHierarchy;
			}
			else
			{
				node.World = local;
				node.ActiveInHierarchy = node.Active;
			}
		}

		// ---------------------------------------------------------------- what can be merged

		private static void Consider(RendererData renderer, Dictionary<ulong, Node> nodes, HashSet<ulong> lodRenderers, Dictionary<string, Group> groups, List<Group> groupOrder, ISet<string> inputs, Summary summary)
		{
			Node owner = renderer.Owner;
			if (!owner.Static || !renderer.Enabled)
			{
				return;
			}

			Resolve(owner, nodes, 0);
			if (!owner.ActiveInHierarchy || !(renderer.Data.Find("mesh")?.Value is AssetReference meshReference) || meshReference.IsNull)
			{
				return;
			}

			summary.StaticRenderers++;
			if (lodRenderers.Contains(renderer.Id))
			{
				summary.Leave("in LOD groups");
				return;
			}

			string modelPath = AssetDatabase.GetPath(meshReference.Guid) ?? meshReference.Path;
			Mesh mesh = null;
			if (modelPath != null && ModelAssets.IsModelFile(modelPath))
			{
				inputs.Add(modelPath);
				try
				{
					mesh = ModelAssets.LoadMesh(modelPath, meshReference.Name);
				}
				catch (Exception)
				{
					mesh = null;
				}
			}

			if (mesh == null || mesh.TriangleCount == 0 || mesh.Vertices.Length == 0)
			{
				summary.Leave("without a mesh to merge");
				return;
			}

			// The materials by slot, as the renderer draws them (fewer than the sub-meshes: the last repeats).
			var materials = new List<Material>();
			var references = new List<AssetReference>();
			if (renderer.Data.Find("materials")?.Value is SerializedArray slots && slots.Bytes == null)
			{
				foreach (object slot in slots.Items)
				{
					AssetReference reference = slot is AssetReference stored ? stored : default;
					string path = reference.IsNull ? null : AssetDatabase.GetPath(reference.Guid) ?? reference.Path;
					Material material = null;
					if (path != null)
					{
						inputs.Add(path);
						try
						{
							material = InternalStorage.GetOrCreateMaterial(path);
						}
						catch (Exception)
						{
							material = null;
						}

						// How a material draws (its queue, blending, Batching Off, a GPU-driven variant) is its shader's.
						if (material?.ShaderPath != null)
						{
							inputs.Add(material.ShaderPath);
						}
					}

					materials.Add(material);
					references.Add(reference);
				}
			}

			string kind = Classify(mesh, materials, references, out string reason);
			if (kind == null)
			{
				summary.Leave(reason);
				return;
			}

			if (mesh.Vertices.Length > MaxVertices)
			{
				summary.Leave("with more than " + MaxVertices.ToString("N0", CultureInfo.InvariantCulture) + " vertices");
				return;
			}

			bool castShadows = Flag(renderer.Data, "CastShadows");
			bool receiveShadows = Flag(renderer.Data, "ReceiveShadows");
			bool receiveGI = Flag(renderer.Data, "ReceiveGI");
			string key = kind + "|" + owner.Layer.ToString(CultureInfo.InvariantCulture) + (castShadows ? "|c" : "|-") + (receiveShadows ? "s" : "-") + (receiveGI ? "g" : "-");
			if (!groups.TryGetValue(key, out Group group))
			{
				group = new Group { Key = key, Layer = owner.Layer, CastShadows = castShadows, ReceiveShadows = receiveShadows, ReceiveGI = receiveGI };
				groups[key] = group;
				groupOrder.Add(group);
			}

			Vector3 centre = Vector3.TransformPosition((mesh.BoundsMin + mesh.BoundsMax) * 0.5f, owner.World);
			group.Parts.Add(new Part { Renderer = renderer, Mesh = mesh, Materials = materials, MaterialReferences = references, World = owner.World, Centre = centre });
		}

		private static bool Flag(SerializedNode data, string name)
		{
			return !(data.Find(name)?.Value is bool value) || value;
		}

		/// <summary>
		///     The group kind of a renderer whose every sub-mesh can be merged ("O": opaque, drawn one by one; "T|materials":
		///     transparent, with that list of materials), or null with the reason it stays as it is.
		/// </summary>
		private static string Classify(Mesh mesh, List<Material> materials, List<AssetReference> references, out string reason)
		{
			reason = null;
			bool opaque = false;
			bool transparent = false;
			bool gpuDriven = false;
			var list = new StringBuilder("T");
			for (int i = 0; i < mesh.SubMeshCount; i++)
			{
				if (mesh.SubMeshes[i].IndexCount < 3)
				{
					continue;
				}

				int slot = materials.Count == 0 ? -1 : i < materials.Count ? i : materials.Count - 1;
				Material material = slot >= 0 ? materials[slot] : null;
				ShaderProgram program = material?.Program;
				if (program == null)
				{
					reason = "without a material";
					return null;
				}

				ShaderRenderState state = material.RenderState;
				if (!state.Batching)
				{
					reason = "of shaders that say Batching Off";
					return null;
				}

				if (material.IsTransparent)
				{
					// However it blends: the transparent pass sorts a batch part by part, as it sorted the objects.
					if (state.GrabPass)
					{
						reason = "reading the scene behind them (GrabPass)";
						return null;
					}

					transparent = true;
					list.Append('|').Append(references[slot].Guid ?? references[slot].Path);
				}
				else if (program.SupportsInstancing)
				{
					gpuDriven = true;
				}
				else if (state.CustomShadowCaster || state.CustomGeometryPrepass)
				{
					reason = "of shaders drawing their own depth passes";
					return null;
				}
				else
				{
					opaque = true;
				}
			}

			if (gpuDriven)
			{
				reason = GpuDriven;
				return null;
			}

			if (opaque == transparent)
			{
				reason = opaque ? "mixing opaque and transparent materials" : "without triangles";
				return null;
			}

			return opaque ? "O" : list.ToString();
		}

		/// <summary>
		///     Every triangle of the mesh faces the same way (a sheet: a quad, a flat strip): drawn from both sides it shows
		///     either its front or its back, never one behind the other, so it needs no back-faces-first pass.
		/// </summary>
		private static bool IsFlat(Mesh mesh)
		{
			Vertex[] vertices = mesh.Vertices;
			uint[] indices = mesh.Indices;
			Vector3 facing = Vector3.Zero;
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				Vector3 a = vertices[indices[i]].position;
				Vector3 normal = Vector3.Cross(vertices[indices[i + 1]].position - a, vertices[indices[i + 2]].position - a);
				float length = normal.Length;
				if (length < 1e-12f)
				{
					continue;
				}

				normal /= length;
				if (facing == Vector3.Zero)
				{
					facing = normal;
				}
				else if (Vector3.Dot(facing, normal) < 0.9999f)
				{
					return false;
				}
			}

			return facing != Vector3.Zero;
		}

		// ---------------------------------------------------------------- batches

		/// <summary>
		///     A group's parts in the order of a Morton curve through their centres (neighbours in space are neighbours on the
		///     curve), cut where the next part would take a batch past <see cref="MaxVertices" />; a part left alone is no batch.
		/// </summary>
		private static List<List<Part>> Cut(Group group, Summary summary)
		{
			var batches = new List<List<Part>>();
			if (group.Parts.Count < 2)
			{
				if (group.Parts.Count == 1)
				{
					summary.Leave("alone in their group (nothing to merge with)");
				}

				return batches;
			}

			var min = new Vector3(float.MaxValue);
			var max = new Vector3(float.MinValue);
			foreach (Part part in group.Parts)
			{
				min = Vector3.ComponentMin(min, part.Centre);
				max = Vector3.ComponentMax(max, part.Centre);
			}

			Vector3 size = max - min;
			float extent = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
			foreach (Part part in group.Parts)
			{
				Vector3 at = extent > 1e-6f ? (part.Centre - min) / extent : Vector3.Zero;
				part.Curve = Morton(at);
			}

			// Ties (and equal places) by the renderers' ids: the same scene, the same batches.
			group.Parts.Sort((a, b) => a.Curve != b.Curve ? a.Curve.CompareTo(b.Curve) : a.Renderer.Id.CompareTo(b.Renderer.Id));

			var current = new List<Part>();
			int vertices = 0;
			void Close()
			{
				if (current.Count > 1)
				{
					batches.Add(current);
				}
				else if (current.Count == 1)
				{
					summary.Leave("alone in their group (nothing to merge with)");
				}

				current = new List<Part>();
				vertices = 0;
			}

			foreach (Part part in group.Parts)
			{
				if (vertices + part.Mesh.Vertices.Length > MaxVertices)
				{
					Close();
				}

				current.Add(part);
				vertices += part.Mesh.Vertices.Length;
			}

			Close();
			return batches;
		}

		// 21 bits per axis, interleaved (x lowest).
		private static ulong Morton(Vector3 unit)
		{
			return Spread(Quantize(unit.X)) | Spread(Quantize(unit.Y)) << 1 | Spread(Quantize(unit.Z)) << 2;
		}

		private static ulong Quantize(float value)
		{
			return (ulong)Math.Clamp((long)(Math.Clamp(value, 0f, 1f) * 2097151f), 0L, 2097151L);
		}

		private static ulong Spread(ulong value)
		{
			value &= 0x1FFFFF;
			value = (value | value << 32) & 0x1F00000000FFFF;
			value = (value | value << 16) & 0x1F0000FF0000FF;
			value = (value | value << 8) & 0x100F00F00F00F00F;
			value = (value | value << 4) & 0x10C30C30C30C30C3;
			value = (value | value << 2) & 0x1249249249249249;
			return value;
		}

		/// <summary>One batch: its mesh (around the middle of its parts, kept by the store), and what its object in the scene is made of.</summary>
		private static StaticBatchData MakeBatch(Group group, List<Part> parts, int index, string sceneAssetPath, MeshStore store, HashSet<ulong> usedIds, Summary summary)
		{
			var min = new Vector3(float.MaxValue);
			var max = new Vector3(float.MinValue);
			foreach (Part part in parts)
			{
				min = Vector3.ComponentMin(min, part.Centre);
				max = Vector3.ComponentMax(max, part.Centre);
			}

			// The batch's object stands in the middle of its parts: the mesh is in its space (small numbers), and what
			// sorts or measures by an object's place gets a place.
			Vector3 pivot = (min + max) * 0.5f;
			Matrix4 toBatch = Matrix4.CreateTranslation(-pivot);
			var instances = new List<CombineInstance>(parts.Count);
			var referenceOf = new Dictionary<Material, AssetReference>(ReferenceEqualityComparer.Instance);
			foreach (Part part in parts)
			{
				instances.Add(new CombineInstance(part.Mesh, part.World * toBatch, part.Materials));
				for (int i = 0; i < part.Materials.Count; i++)
				{
					if (part.Materials[i] != null && !referenceOf.ContainsKey(part.Materials[i]))
					{
						referenceOf[part.Materials[i]] = part.MaterialReferences[i];
					}
				}
			}

			string name = "Static Batch " + index.ToString(CultureInfo.InvariantCulture);
			CombinedMesh combined = MeshCombiner.Combine(instances, name);

			AssetReference meshReference = store(combined.Mesh, index, name);
			summary.Files.Add(meshReference.Path);

			// The parts' places in the mesh's indices (for the batch to hide those that go away, and for the transparent pass
			// to draw them in their order), their centres in the batch's space (where each sorts) and which are flat.
			var renderers = new ulong[parts.Count];
			var partRanges = new int[parts.Count + 1];
			var ranges = new List<int>();
			var centres = new Vector3[parts.Count];
			var flat = new bool[parts.Count];
			for (int i = 0; i < parts.Count; i++)
			{
				renderers[i] = parts[i].Renderer.Id;
				centres[i] = parts[i].Centre - pivot;
				flat[i] = IsFlat(parts[i].Mesh);
				partRanges[i] = ranges.Count / 2;
				foreach ((int first, int count) in combined.InstanceRanges[i] ?? Array.Empty<(int, int)>())
				{
					ranges.Add(first);
					ranges.Add(count);
				}
			}

			partRanges[parts.Count] = ranges.Count / 2;
			var materials = new AssetReference[combined.Materials.Count];
			for (int i = 0; i < materials.Length; i++)
			{
				Material material = combined.Materials[i];
				materials[i] = material != null && referenceOf.TryGetValue(material, out AssetReference reference) ? reference : default;
			}

			summary.Batches++;
			summary.Batched += parts.Count;
			summary.Vertices += combined.Mesh.Vertices.Length;
			summary.Triangles += combined.Mesh.TriangleCount;

			// Ids from the scene's name and the batch's number: the same scene, the same document.
			string seed = sceneAssetPath + "#" + name;
			return new StaticBatchData
			{
				Name = name,
				ObjectId = NewId(seed, usedIds),
				TransformId = NewId(seed + "/Transform", usedIds),
				RendererId = NewId(seed + "/MeshRenderer", usedIds),
				BatchId = NewId(seed + "/StaticBatch", usedIds),
				Layer = group.Layer,
				CastShadows = group.CastShadows,
				ReceiveShadows = group.ReceiveShadows,
				ReceiveGI = group.ReceiveGI,
				Pivot = pivot,
				Mesh = meshReference,
				Materials = materials,
				Renderers = renderers,
				PartRanges = partRanges,
				Ranges = ranges.ToArray(),
				Centres = centres,
				Flat = flat
			};
		}

		/// <summary>
		///     The object of a batch as a document's entry: a transform at the pivot, the renderer of the merged mesh (out of
		///     the GI: the parts stay in it), the batch naming its parts.
		/// </summary>
		private static DocumentObject ObjectOf(StaticBatchData data)
		{
			var batch = new DocumentObject { Id = data.ObjectId, Name = data.Name, Layer = data.Layer, Static = true };
			SerializedNode transform = NodeOf(typeof(Transform));
			Set(transform, "m_LocalPosition", data.Pivot);
			batch.Components.Add(new DocumentComponent { Id = data.TransformId, Data = transform });

			var materials = new SerializedArray { ElementTag = ValueTag.Asset };
			foreach (AssetReference material in data.Materials)
			{
				materials.Items.Add(material);
			}

			SerializedNode renderer = NodeOf(typeof(MeshRenderer));
			Set(renderer, "CastShadows", data.CastShadows);
			Set(renderer, "ReceiveShadows", data.ReceiveShadows);
			Set(renderer, "ContributesToGI", false);
			Set(renderer, "ReceiveGI", data.ReceiveGI);
			Set(renderer, "mesh", data.Mesh);
			Set(renderer, "materials", materials);
			batch.Components.Add(new DocumentComponent { Id = data.RendererId, Data = renderer });

			var renderers = new SerializedArray { ElementTag = ValueTag.SceneRef };
			foreach (ulong id in data.Renderers)
			{
				renderers.Items.Add(id);
			}

			SerializedNode component = NodeOf(typeof(StaticBatch));
			Set(component, "m_Renderers", renderers);
			Set(component, "m_PartRanges", IntArray(data.PartRanges));
			Set(component, "m_Ranges", IntArray(data.Ranges));
			var centres = new SerializedArray { ElementTag = ValueTag.Vector3 };
			foreach (Vector3 centre in data.Centres)
			{
				centres.Items.Add(centre);
			}

			var flat = new SerializedArray { ElementTag = ValueTag.Bool };
			foreach (bool value in data.Flat)
			{
				flat.Items.Add(value);
			}

			Set(component, "m_Centres", centres);
			Set(component, "m_Flat", flat);
			batch.Components.Add(new DocumentComponent { Id = data.BatchId, Data = component });
			return batch;
		}

		private static SerializedArray IntArray(int[] values)
		{
			var array = new SerializedArray { ElementTag = ValueTag.Int32 };
			foreach (int value in values)
			{
				array.Items.Add(value);
			}

			return array;
		}

		/// <summary>The generic data of a new component of an engine type (its own copy: the fields are set on it).</summary>
		private static SerializedNode NodeOf(Type type)
		{
			string name = TypeRegistry.GetName(type);
			return GenericValues.CloneNode(GenericValues.DefaultNode(name)) ?? throw new InvalidOperationException("No data layout for " + name);
		}

		private static void Set(SerializedNode node, string field, object value)
		{
			SerializedProperty property = node.Find(field) ?? throw new InvalidOperationException(node.TypeName + " has no field " + field);
			property.Value = value;
		}

		// An id from a text, not one the scene has (the text's hash; hashed on while taken).
		private static ulong NewId(string seed, HashSet<ulong> used)
		{
			for (int salt = 0; ; salt++)
			{
				byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(salt == 0 ? seed : seed + "#" + salt.ToString(CultureInfo.InvariantCulture)));
				ulong id = BitConverter.ToUInt64(hash, 0);
				if (id != 0 && used.Add(id))
				{
					return id;
				}
			}
		}

		#endregion
	}
}
