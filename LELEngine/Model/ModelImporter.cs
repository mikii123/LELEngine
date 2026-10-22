using System;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     Where the normals of an imported model come from. Computed normals are smooth up to
	///     <see cref="ModelImporter.smoothingAngle" /> (Assimp formats); the .obj reader smooths every corner that shares a
	///     position.
	/// </summary>
	public enum ModelNormals
	{
		/// <summary>The file's normals; computed where it has none.</summary>
		Import,

		/// <summary>Always computed.</summary>
		Calculate
	}

	/// <summary>
	///     Import settings of a model file (.fbx, .gltf, .glb, .dae, .3ds, .blend, .ply, .stl), stored in its meta
	///     file. The editor imports models with Assimp into Library/ModelCache; player builds get the imported data.
	/// </summary>
	[Serializable]
	public sealed class ModelImporter : AssetImporter
	{
		#region PublicFields

		/// <summary>Uniform scale applied to the whole model (after the file's unit conversion).</summary>
		public float scaleFactor = 1f;

		/// <summary>Converts the file's units to meters (FBX files store their unit; centimeters by default).</summary>
		public bool convertUnits = true;

		/// <summary>Extra rotation of the model's root, in degrees (e.g. -90 on X for a Z-up model).</summary>
		public Vector3 rotation;

		public ModelNormals normals = ModelNormals.Import;

		[Range(0, 180)] public float smoothingAngle = 60f;

		/// <summary>Flip the V texture coordinate (for files that store top-left UV origins).</summary>
		[Tooltip("Flip the V texture coordinate (files with a top-left UV origin)")]
		public bool flipV;

		/// <summary>
		///     Creates a .material asset per material of the file (Materials folder next to the model, never
		///     overwritten) and writes embedded textures out (Textures folder); off: the project's default material.
		///     The import runs as soon as the file is in the project (the editor imports new and changed models).
		/// </summary>
		[Header("Materials")]
		[Tooltip("Create a material asset per material of the model (Materials/ next to it, editable, never overwritten); off: the default material")]
		public bool importMaterials = true;

		/// <summary>Reorders triangles and vertices for the GPU's vertex cache.</summary>
		public bool optimizeMesh = true;

		#endregion
	}
}
