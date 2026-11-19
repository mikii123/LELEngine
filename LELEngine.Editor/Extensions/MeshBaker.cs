using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>What becomes of the objects a bake merged.</summary>
	public enum MeshBakeOriginals
	{
		/// <summary>They are deleted: the baked object stands in their place.</summary>
		Delete,

		/// <summary>They stay in the scene, switched off (their colliders and scripts with them).</summary>
		Deactivate,

		/// <summary>They stay as they are, drawn together with the baked object.</summary>
		Keep
	}

	/// <summary>How <see cref="MeshBaker" /> bakes.</summary>
	public sealed class MeshBakeOptions
	{
		#region PublicFields

		/// <summary>
		///     Name of the baked object and of its mesh asset. Empty: the object's own name when one object is baked, "Baked
		///     Mesh" for several.
		/// </summary>
		public string Name;

		/// <summary>
		///     Folder of the mesh asset, relative to Assets ("Meshes/Baked"; made when missing). Empty: the folder of the
		///     scene (of the prefab, in prefab mode).
		/// </summary>
		public string Folder;

		/// <summary>The level baked of objects under a LOD Group (0: the most detailed; a group with fewer levels gives its last).</summary>
		public int LodLevel;

		/// <summary>
		///     Bakes renderers with a transparent material too (on by default). The transparent parts then sort as one object:
		///     where two of them overlap on screen, their triangles blend in the mesh's order, not by distance.
		/// </summary>
		public bool IncludeTransparent = true;

		public MeshBakeOriginals Originals = MeshBakeOriginals.Delete;

		#endregion
	}

	/// <summary>What a bake of some objects takes (<see cref="MeshBaker.Describe" />).</summary>
	public sealed class MeshBakeSummary
	{
		#region PublicFields

		/// <summary>The objects baked: those given, without the ones under another of them.</summary>
		public int Objects;

		/// <summary>The meshes merged (one per renderer taken).</summary>
		public int Meshes;

		public long Vertices;
		public long Triangles;

		/// <summary>The materials of the meshes merged: the sub-meshes (draw calls) of the result.</summary>
		public int Materials;

		/// <summary>Renderers left out because a material of theirs is transparent.</summary>
		public int Transparent;

		/// <summary>Renderers left out because they belong to other levels of a LOD Group.</summary>
		public int OtherLevels;

		/// <summary>Why nothing can be baked (null: a bake can run).</summary>
		public string Problem;

		/// <summary>Where the mesh asset would be written (relative to Assets).</summary>
		public string AssetPath;

		/// <summary>The name the baked object would get.</summary>
		public string Name;

		#endregion
	}

	/// <summary>
	///     The Mesh Baker (Window > Mesh Baker) for editor scripts: merges the meshes of objects and everything under them
	///     into one mesh, saved as a mesh asset (.mesh), and puts one object drawing it in their place. The mesh has one
	///     sub-mesh per material, so the baked object draws with one call per material however many objects it was.
	/// </summary>
	public static class MeshBaker
	{
		#region PublicMethods

		/// <summary>What a bake of these objects would take and make, without baking.</summary>
		public static MeshBakeSummary Describe(IEnumerable<GameObject> objects, MeshBakeOptions options = null)
		{
			return MeshBaking.Describe(EditorApplication.Instance, objects, options ?? new MeshBakeOptions());
		}

		/// <summary>
		///     Bakes the objects (each with everything under it) into one: the mesh asset is written, an object with a
		///     MeshRenderer drawing it is made where they were (one object: at its place, with its pivot; several: at the
		///     middle of their bounds) and selected, and the originals go as <see cref="MeshBakeOptions.Originals" /> says.
		///     One undo step (undo also takes the mesh asset away). Edit mode only. Returns the baked object, or null when
		///     nothing was baked (the reason is logged).
		/// </summary>
		public static GameObject Bake(IEnumerable<GameObject> objects, MeshBakeOptions options = null)
		{
			return MeshBaking.Bake(EditorApplication.Instance, objects, options ?? new MeshBakeOptions());
		}

		/// <summary>Bakes the selected objects (<see cref="Selection.gameObjects" />).</summary>
		public static GameObject BakeSelection(MeshBakeOptions options = null)
		{
			EditorApplication editor = EditorApplication.Instance;
			return MeshBaking.Bake(editor, editor?.SelectedObjects, options ?? new MeshBakeOptions());
		}

		#endregion
	}
}
