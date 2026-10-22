using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>A node of a model's hierarchy: a transform relative to its parent and optionally a mesh.</summary>
	public sealed class ModelNode
	{
		#region PublicFields

		public string Name = "";

		/// <summary>Index of the parent node (-1: the root).</summary>
		public int Parent = -1;

		public Vector3 LocalPosition;
		public Quaternion LocalRotation = Quaternion.Identity;
		public Vector3 LocalScale = Vector3.One;

		/// <summary>Index into <see cref="ModelData.Meshes" />, or -1.</summary>
		public int Mesh = -1;

		#endregion
	}

	/// <summary>Geometry of one mesh of a model, with the material of each sub-mesh.</summary>
	public sealed class ModelMeshData
	{
		#region PublicFields

		/// <summary>Unique within the model: the sub-asset name scenes reference the mesh by.</summary>
		public string Name = "";

		public Vertex[] Vertices = Array.Empty<Vertex>();
		public uint[] Indices = Array.Empty<uint>();
		public SubMesh[] SubMeshes = Array.Empty<SubMesh>();

		/// <summary>Index into <see cref="ModelData.Materials" /> of each sub-mesh (-1: the project's default material).</summary>
		public int[] Materials = Array.Empty<int>();

		#endregion
	}

	/// <summary>A texture a model's material uses: a project file, or the bytes of a texture embedded in the model file.</summary>
	public sealed class ModelTexture
	{
		#region PublicFields

		/// <summary>Asset path of an image file of the project (null: embedded).</summary>
		public string AssetPath;

		/// <summary>Encoded image (png, jpg, ...) embedded in the model file.</summary>
		public byte[] Data;

		/// <summary>File extension of <see cref="Data" /> (".png").</summary>
		public string Extension = ".png";

		/// <summary>Name for the extracted file.</summary>
		public string Name = "";

		#endregion
	}

	/// <summary>
	///     A material of a model file as the importer read it (colors, textures). The editor turns it into a .material
	///     asset (<see cref="AssetPath" />); only the name and that path are kept with the imported data.
	/// </summary>
	public sealed class ModelMaterial
	{
		#region PublicFields

		public string Name = "";
		public Vector4 Color = Vector4.One;

		/// <summary>rgb color, a intensity (the Lit shaders' convention).</summary>
		public Vector4 Emissive;

		public float Roughness = 0.6f;
		public ModelTexture AlbedoMap;
		public ModelTexture NormalMap;

		/// <summary>The .material asset created for it (null: not imported, the default material is used).</summary>
		public string AssetPath;

		#endregion
	}

	/// <summary>An imported model: its node hierarchy (nodes in parent-first order, node 0 is the root), meshes and materials.</summary>
	public sealed class ModelData
	{
		#region PublicFields

		public readonly List<ModelNode> Nodes = new List<ModelNode>();
		public readonly List<ModelMeshData> Meshes = new List<ModelMeshData>();
		public readonly List<ModelMaterial> Materials = new List<ModelMaterial>();

		/// <summary>Size of the file's unit in meters (FBX files in centimeters: 0.01); applied with the import settings.</summary>
		public float UnitScale = 1f;

		#endregion
	}

	/// <summary>
	///     A model file (.fbx, .gltf, .glb, .obj, ...) as an asset: the node hierarchy and its meshes, which are
	///     sub-assets of the file (a MeshRenderer references "Car.fbx" + "Wheel"). <see cref="Instantiate" /> builds the
	///     hierarchy as GameObjects with MeshRenderers and the materials of the import. Loaded through
	///     <see cref="ModelAssets" />.
	/// </summary>
	public sealed class Model : IAsset
	{
		#region Constructors

		internal Model(string assetPath, ModelData data)
		{
			AssetPath = assetPath;
			Data = data;
			var meshes = new Mesh[data.Meshes.Count];
			for (int i = 0; i < meshes.Length; i++)
			{
				ModelMeshData m = data.Meshes[i];
				meshes[i] = new Mesh(m.Name, m.Vertices, m.Indices, m.SubMeshes, assetPath, m.Name);
			}

			Meshes = meshes;
		}

		#endregion

		#region PublicFields

		/// <summary>The material of sub-meshes without an imported material (the project template's default material).</summary>
		public const string DefaultMaterialPath = "Materials/Default.material";

		public string AssetPath { get; internal set; }

		public string name => Path.GetFileNameWithoutExtension(AssetPath ?? "Model");

		public IReadOnlyList<ModelNode> Nodes => Data.Nodes;
		public IReadOnlyList<Mesh> Meshes { get; }

		/// <summary>The imported data (node hierarchy, mesh data, materials).</summary>
		public ModelData Data { get; }

		#endregion

		#region PublicMethods

		/// <summary>The mesh with the sub-asset name, or null.</summary>
		public Mesh FindMesh(string meshName)
		{
			foreach (Mesh mesh in Meshes)
			{
				if (mesh.SubAssetName == meshName)
				{
					return mesh;
				}
			}

			return null;
		}

		/// <summary>Axis-aligned bounds of every mesh in the model's space (the root's frame); false without geometry.</summary>
		public bool GetBounds(out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.MaxValue);
			max = new Vector3(float.MinValue);
			var worlds = new Matrix4[Nodes.Count];
			bool any = false;
			for (int i = 0; i < Nodes.Count; i++)
			{
				ModelNode node = Nodes[i];
				Matrix4 local = Matrix4.CreateScale(node.LocalScale) * Matrix4.CreateFromQuaternion(node.LocalRotation) * Matrix4.CreateTranslation(node.LocalPosition);
				// The root's own transform is where the model is placed, not part of its shape.
				worlds[i] = i == 0 ? Matrix4.Identity : node.Parent >= 0 && node.Parent < i ? local * worlds[node.Parent] : local;
				if (node.Mesh < 0 || node.Mesh >= Meshes.Count || Meshes[node.Mesh].Vertices.Length == 0)
				{
					continue;
				}

				Mesh mesh = Meshes[node.Mesh];
				for (int corner = 0; corner < 8; corner++)
				{
					var local3 = new Vector3((corner & 1) != 0 ? mesh.BoundsMax.X : mesh.BoundsMin.X, (corner & 2) != 0 ? mesh.BoundsMax.Y : mesh.BoundsMin.Y, (corner & 4) != 0 ? mesh.BoundsMax.Z : mesh.BoundsMin.Z);
					Vector3 world = Vector3.TransformPosition(local3, worlds[i]);
					min = Vector3.ComponentMin(min, world);
					max = Vector3.ComponentMax(max, world);
				}

				any = true;
			}

			return any;
		}

		/// <summary>
		///     The materials of a mesh's sub-meshes: the imported .material assets, the project's default material
		///     (<see cref="DefaultMaterialPath" />) where there is none.
		/// </summary>
		public List<Shaders.Material> LoadMaterials(Mesh mesh)
		{
			var result = new List<Shaders.Material>();
			int index = -1;
			for (int i = 0; i < Meshes.Count; i++)
			{
				if (Meshes[i] == mesh)
				{
					index = i;
				}
			}

			if (index < 0)
			{
				return result;
			}

			Shaders.Material fallback = null;
			foreach (int materialIndex in Data.Meshes[index].Materials)
			{
				string path = materialIndex >= 0 && materialIndex < Data.Materials.Count ? Data.Materials[materialIndex].AssetPath : null;
				Shaders.Material material = null;
				if (!string.IsNullOrEmpty(path) && File.Exists(AssetDatabase.ToAbsolute(path)))
				{
					material = InternalStorage.GetOrCreateMaterial(path);
				}

				if (material == null && File.Exists(AssetDatabase.ToAbsolute(DefaultMaterialPath)))
				{
					material = fallback ??= InternalStorage.GetOrCreateMaterial(DefaultMaterialPath);
				}

				result.Add(material);
			}

			return result;
		}

		/// <summary>
		///     Builds the model in the scene: one GameObject per node (the root named after the file) under
		///     <paramref name="parent" />, a MeshRenderer with the import's materials on nodes with geometry. Returns
		///     the root.
		/// </summary>
		public GameObject Instantiate(Scene scene, Transform parent = null)
		{
			if (scene == null || Nodes.Count == 0)
			{
				return null;
			}

			var objects = new GameObject[Nodes.Count];
			for (int i = 0; i < Nodes.Count; i++)
			{
				ModelNode node = Nodes[i];
				GameObject go = scene.CreateGameObject(i == 0 ? name : node.Name);
				Transform parentTransform = node.Parent >= 0 && node.Parent < i ? objects[node.Parent].transform : parent;
				if (parentTransform != null)
				{
					go.transform.SetParent(parentTransform, false);
				}

				go.transform.localPosition = node.LocalPosition;
				go.transform.localRotation = node.LocalRotation;
				go.transform.localScale = node.LocalScale;
				objects[i] = go;

				if (node.Mesh >= 0 && node.Mesh < Meshes.Count)
				{
					MeshRenderer renderer = go.AddComponent<MeshRenderer>();
					renderer.SetMesh(Meshes[node.Mesh]);
					renderer.SetMaterials(LoadMaterials(Meshes[node.Mesh]));
				}
			}

			return objects[0];
		}

		#endregion
	}
}
