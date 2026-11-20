using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.Rendering.Impostors;
using LELEngine.Shaders;
using OpenTK.Mathematics;

/// <summary>
///     An impostor: a billboard that stands for an object far away. The object's surface is baked from a grid of
///     directions into atlases (<see cref="ImpostorBaker" />), and a mesh renderer with the impostor's
///     <see cref="Mesh" /> and <see cref="Material" /> (Engine/Impostor.shader) draws six triangles that look like the
///     object from any side: lit by the scene's lights and GI, casting and receiving shadows, at its own depth.
///     <para>
///         As an asset (Create > Impostor, or Create Impostor on a prefab or model): <see cref="SourcePrefab" /> or
///         <see cref="SourceModel" /> is what it is baked from and <see cref="Settings" /> how; the editor's Bake writes
///         the atlases, the proxy mesh and the material next to the asset. Usually the impostor is the last level of the
///         object's <see cref="LODGroup" /> (<see cref="AddToLODGroup" />, the group inspector's Add Impostor).
///     </para>
///     <para>
///         At runtime: <see cref="Bake(GameObject, ImpostorSettings)" /> makes one of any object in memory (an object the
///         game builds itself), <see cref="AddRenderer" /> / <see cref="Instantiate" /> draw it.
///     </para>
///     An impostor is baked in the space of the object it was made of: its renderer belongs on an object with that
///     object's transform (a child of it at the origin). It takes no part in the GI's volumes (the group's first level
///     does, as for every level of detail).
/// </summary>
[CreateAssetMenu(menuName = "Impostor", fileName = "New Impostor")]
public sealed class Impostor : ScriptableObject
{
	#region PublicFields

	[Header("Source")]
	[Tooltip("The prefab the impostor is baked from: its mesh renderers, of LOD groups the first level")]
	public Prefab SourcePrefab;

	[Tooltip("The model the impostor is baked from when there is no prefab")]
	public Model SourceModel;

	[Header("Bake")]
	public ImpostorSettings Settings = new ImpostorSettings();

	/// <summary>The proxy the impostor draws with: the box of the object's bounds (<see cref="ImpostorMesh" />).</summary>
	public Mesh Mesh => m_Mesh;

	/// <summary>The material of Engine/Impostor.shader with the baked atlases.</summary>
	public Material Material => m_Material;

	/// <summary>Normal, depth and coverage (<see cref="ImpostorBake.Shape" />).</summary>
	public Texture2D ShapeMap => m_ShapeMap;

	/// <summary>Colour and smoothness or occlusion (<see cref="ImpostorBake.Base" />).</summary>
	public Texture2D BaseMap => m_BaseMap;

	/// <summary>Specular colour and occlusion (<see cref="ImpostorBake.Specular" />); null for an impostor lit with the scene's highlight.</summary>
	public Texture2D SpecularMap => m_SpecularMap;

	/// <summary>Emission (<see cref="ImpostorBake.Emission" />); null when nothing emits.</summary>
	public Texture2D EmissionMap => m_EmissionMap;

	/// <summary>It was baked: there is something to draw.</summary>
	public bool IsBaked => m_Mesh != null && m_Material != null;

	/// <summary>Views along a side of the baked grid.</summary>
	public int Frames => m_Frames;

	/// <summary>Texels along a side of one baked view.</summary>
	public int FrameSize => m_FrameSize;

	/// <summary>Baked from above the horizon only.</summary>
	public bool Hemisphere => m_Hemisphere;

	/// <summary>The centre of the sphere the views look at, in the object's space.</summary>
	public Vector3 Center => m_Center;

	/// <summary>Half the width of a view (the bounding sphere's radius and the border), in the object's space.</summary>
	public float Radius => m_Radius;

	/// <summary>The baked object's bounds in its space.</summary>
	public Vector3 BoundsMin => m_BoundsMin;

	public Vector3 BoundsMax => m_BoundsMax;

	/// <summary>Triangles of the meshes the impostor stands for.</summary>
	public int SourceTriangles => m_SourceTriangles;

	#endregion

	#region PrivateFields

	[SerializeField] [HideInInspector] private Mesh m_Mesh;
	[SerializeField] [HideInInspector] private Material m_Material;
	[SerializeField] [HideInInspector] private Texture2D m_ShapeMap;
	[SerializeField] [HideInInspector] private Texture2D m_BaseMap;
	[SerializeField] [HideInInspector] private Texture2D m_SpecularMap;
	[SerializeField] [HideInInspector] private Texture2D m_EmissionMap;
	[SerializeField] [HideInInspector] private int m_Frames;
	[SerializeField] [HideInInspector] private int m_FrameSize;
	[SerializeField] [HideInInspector] private bool m_Hemisphere;
	[SerializeField] [HideInInspector] private Vector3 m_Center;
	[SerializeField] [HideInInspector] private float m_Radius;
	[SerializeField] [HideInInspector] private Vector3 m_BoundsMin;
	[SerializeField] [HideInInspector] private Vector3 m_BoundsMax;
	[SerializeField] [HideInInspector] private int m_SourceTriangles;

	#endregion

	#region PublicMethods

	/// <summary>
	///     Bakes an impostor of an object in memory (on the thread with the GL context): its enabled mesh renderers, of
	///     LOD groups the first level (<see cref="ImpostorBaker.CollectRenderers" />). Not an asset: it lives while it
	///     is referenced.
	/// </summary>
	public static Impostor Bake(GameObject root, ImpostorSettings settings = null)
	{
		if (root == null)
		{
			throw new ArgumentNullException(nameof(root));
		}

		return Create(ImpostorBaker.Bake(root, settings), root.Name);
	}

	/// <summary>Bakes an impostor of renderers in memory (<see cref="ImpostorBaker.Bake(IReadOnlyList{MeshRenderer}, Matrix4, ImpostorSettings)" />).</summary>
	public static Impostor Bake(IReadOnlyList<MeshRenderer> renderers, Matrix4 worldToObject, ImpostorSettings settings = null, string name = null)
	{
		return Create(ImpostorBaker.Bake(renderers, worldToObject, settings), name);
	}

	/// <summary>An impostor in memory from a bake's result: its atlases as textures, its material and its proxy mesh.</summary>
	public static Impostor Create(ImpostorBake bake, string name = null)
	{
		if (bake == null)
		{
			throw new ArgumentNullException(nameof(bake));
		}

		Impostor impostor = CreateInstance<Impostor>();
		impostor.name = string.IsNullOrEmpty(name) ? "Impostor" : name + " Impostor";
		impostor.Settings = new ImpostorSettings { Frames = bake.Frames, FrameSize = bake.FrameSize, Hemisphere = bake.Hemisphere };
		Texture2D shape = CreateTexture(impostor.name + " Shape", bake.AtlasSize, bake.Shape, false);
		Texture2D colour = CreateTexture(impostor.name + " Base", bake.AtlasSize, bake.Base, true);
		Texture2D specular = bake.Specular != null ? CreateTexture(impostor.name + " Specular", bake.AtlasSize, bake.Specular, true) : null;
		Texture2D emission = bake.Emission != null ? CreateTexture(impostor.name + " Emission", bake.AtlasSize, bake.Emission, true) : null;
		var material = new Material(InternalStorage.GetOrCreateShader(ImpostorBaker.ShaderPath)) { name = impostor.name };
		SetupMaterial(material, bake, shape, colour, specular, emission);
		impostor.SetBaked(bake, ImpostorMesh.Create(bake.BoundsMin, bake.BoundsMax, impostor.name), material, shape, colour, specular, emission);
		return impostor;
	}

	/// <summary>
	///     Sets on a material of Engine/Impostor.shader what a bake gave: the atlases (a null specular or emission
	///     atlas switches that part off) and the values the shader reads them with. The tint, the cutoff, the parallax
	///     and the shadow bias keep their values.
	/// </summary>
	public static void SetupMaterial(Material material, ImpostorBake bake, Texture shape, Texture colour, Texture specular, Texture emission)
	{
		if (material == null || bake == null)
		{
			throw new ArgumentNullException(material == null ? nameof(material) : nameof(bake));
		}

		material.SetTexture("ShapeMap", shape);
		material.SetTexture("BaseMap", colour);
		material.SetTexture("SpecularMap", specular);
		material.SetTexture("EmissionMap", emission);
		material.SetVector("ImpostorSphere", new Vector4(bake.Center, bake.Radius));
		material.SetVector("ImpostorFrames", new Vector4(bake.Frames, bake.FrameSize, bake.Hemisphere ? 1f : 0f, 0f));
		material.SetFloat("SceneHighlights", specular != null && bake.SceneHighlights ? 1f : 0f);
		material.SetFloat("Roughness", bake.Roughness);
		// The emission atlas holds the radiance as a share of the brightest: the intensity brings it back.
		material.SetColor("Emissive", new Color4(1f, 1f, 1f, emission != null ? bake.EmissionScale : 0f));
	}

	/// <summary>
	///     Adds a mesh renderer drawing the impostor to <paramref name="gameObject" />: casting and receiving shadows, lit
	///     by the GI and out of its volumes. The object must have the transform of the object the impostor was baked
	///     of. Null when the impostor is not baked.
	/// </summary>
	public MeshRenderer AddRenderer(GameObject gameObject)
	{
		if (gameObject == null)
		{
			throw new ArgumentNullException(nameof(gameObject));
		}

		if (!IsBaked)
		{
			return null;
		}

		MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
		Setup(renderer);
		return renderer;
	}

	/// <summary>Makes <paramref name="renderer" /> draw the impostor (its mesh, its material, and out of the GI's volumes).</summary>
	public void Setup(MeshRenderer renderer)
	{
		if (renderer == null)
		{
			throw new ArgumentNullException(nameof(renderer));
		}

		renderer.SetMesh(m_Mesh);
		renderer.SetMaterials(m_Material);
		renderer.ContributesToGI = false;
	}

	/// <summary>
	///     A new object drawing the impostor, named after it, at the origin of <paramref name="parent" /> (the object the
	///     impostor was baked of, or one with its transform) or of the scene. Null when the impostor is not baked.
	/// </summary>
	public GameObject Instantiate(Scene scene, Transform parent = null)
	{
		if (scene == null)
		{
			throw new ArgumentNullException(nameof(scene));
		}

		if (!IsBaked)
		{
			return null;
		}

		GameObject gameObject = scene.CreateGameObject(string.IsNullOrEmpty(name) ? "Impostor" : name);
		if (parent != null)
		{
			gameObject.transform.SetParent(parent, false);
			gameObject.layer = parent.gameObject.layer;
		}

		AddRenderer(gameObject);
		return gameObject;
	}

	/// <summary>
	///     Adds the impostor to a LOD group as its last level: a child object of the group's draws it while the group is
	///     smaller on screen than its last level so far and at least <paramref name="screenRelativeHeight" /> of the view's
	///     height (below that nothing draws). The group must be on the object the impostor was baked of. A group
	///     without levels gets its own mesh renderers (those the impostor stands for) as the first level, down to
	///     <paramref name="firstLevelHeight" />. Returns the impostor's renderer; null when the impostor is not baked.
	/// </summary>
	public MeshRenderer AddToLODGroup(LODGroup group, float screenRelativeHeight = 0.01f, float firstLevelHeight = 0.15f)
	{
		if (group == null)
		{
			throw new ArgumentNullException(nameof(group));
		}

		if (!IsBaked)
		{
			return null;
		}

		var levels = new List<LOD>(group.GetLODs());
		levels.RemoveAll(level => level.renderers == null || level.renderers.Count == 0);
		bool fresh = levels.Count == 0;
		if (fresh)
		{
			levels.Add(new LOD(firstLevelHeight, ImpostorBaker.CollectRenderers(group.gameObject).ToArray()));
		}

		GameObject child = Instantiate(group.gameObject.scene, group.transform);
		MeshRenderer renderer = child.GetComponent<MeshRenderer>();
		float last = levels[levels.Count - 1].screenRelativeTransitionHeight;
		levels.Add(new LOD(Math.Clamp(screenRelativeHeight, 0f, last * 0.999f), new[] { renderer }));
		group.SetLODs(levels.ToArray());
		if (fresh)
		{
			group.RecalculateBounds();
		}

		return renderer;
	}

	#endregion

	#region InternalMethods

	/// <summary>What a bake made (the editor: the assets it wrote; <see cref="Create" />: objects in memory).</summary>
	internal void SetBaked(ImpostorBake bake, Mesh mesh, Material material, Texture2D shape, Texture2D colour, Texture2D specular, Texture2D emission)
	{
		m_Mesh = mesh;
		m_Material = material;
		m_ShapeMap = shape;
		m_BaseMap = colour;
		m_SpecularMap = specular;
		m_EmissionMap = emission;
		m_Frames = bake.Frames;
		m_FrameSize = bake.FrameSize;
		m_Hemisphere = bake.Hemisphere;
		m_Center = bake.Center;
		m_Radius = bake.Radius;
		m_BoundsMin = bake.BoundsMin;
		m_BoundsMax = bake.BoundsMax;
		m_SourceTriangles = bake.SourceTriangles;
	}

	#endregion

	#region PrivateMethods

	// An atlas as a texture in memory: rows bottom first as baked, mip levels, clamped, no anisotropy (the shader
	// names the level it reads).
	private static Texture2D CreateTexture(string name, int size, byte[] texels, bool srgb)
	{
		var texture = new Texture2D { name = name };
		texture.SetImage(new ImageData(size, size, texels), srgb, true, true);
		texture.SetSamplerState(FilterMode.Trilinear, TextureWrap.Clamp, 1);
		return texture;
	}

	#endregion
}
