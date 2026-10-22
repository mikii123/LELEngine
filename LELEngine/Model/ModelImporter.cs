using System;
using System.Collections.Generic;
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
		///     Creates a .material asset per material of the file and writes its embedded textures out (both next to the
		///     model, never overwritten; the project panel lists them under it); off: the project's default material. The import runs as soon as the file is in the project (the editor imports new and changed models).
		/// </summary>
		[Header("Materials")]
		[Tooltip("Create a material asset per material of the model (next to it, listed under it, editable, never overwritten); off: the default material")]
		public bool importMaterials = true;

		/// <summary>Reorders triangles and vertices for the GPU's vertex cache.</summary>
		public bool optimizeMesh = true;

		/// <summary>
		///     The material assets the import made, by the file's material name, kept by GUID (Unity's external object
		///     remap): a material renamed or moved stays the model's. Written by the import, not an import setting (the
		///     import's stamp leaves it out).
		/// </summary>
		[HideInInspector] public List<ModelExtractedAsset> materialAssets = new List<ModelExtractedAsset>();

		/// <summary>The texture files the import wrote out of the model file (its embedded images), kept the same way.</summary>
		[HideInInspector] public List<ModelExtractedAsset> textureAssets = new List<ModelExtractedAsset>();

		#endregion

		#region PublicMethods

		/// <summary>The GUID of the asset made for a material or texture of the file (by its name), or null.</summary>
		public static string FindExtracted(List<ModelExtractedAsset> list, string name)
		{
			return list?.Find(m => m.name == name)?.guid;
		}

		/// <summary>Records the asset made for a material or texture of the file; true when that changed the list.</summary>
		public static bool RecordExtracted(List<ModelExtractedAsset> list, string name, string guid)
		{
			ModelExtractedAsset entry = list.Find(m => m.name == name);
			if (entry == null)
			{
				list.Add(new ModelExtractedAsset { name = name, guid = guid });
				return true;
			}

			if (entry.guid == guid)
			{
				return false;
			}

			entry.guid = guid;
			return true;
		}

		/// <summary>Every asset the import made (materials, then textures), as GUIDs.</summary>
		public IEnumerable<string> ExtractedGuids()
		{
			foreach (List<ModelExtractedAsset> list in new[] { materialAssets, textureAssets })
			{
				foreach (ModelExtractedAsset entry in list ?? new List<ModelExtractedAsset>())
				{
					if (entry.guid != null)
					{
						yield return entry.guid;
					}
				}
			}
		}

		#endregion
	}

	/// <summary>
	///     A material or embedded texture of a model file and the asset made from it (<see cref="ModelImporter.materialAssets" />,
	///     <see cref="ModelImporter.textureAssets" />).
	/// </summary>
	[Serializable]
	public sealed class ModelExtractedAsset
	{
		public string name;
		public string guid;
	}
}
