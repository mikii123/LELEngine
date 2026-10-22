using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Turns the materials of an import into project assets: &lt;material name&gt;.material next to the model (the
	///     project panel lists them under it; the engine's LitTextured for opaque materials, LitTransparent below full
	///     opacity) and the embedded textures as image files next to it too (listed under it the same way). Each asset is
	///     recorded in the model's import settings by GUID (<see cref="ModelImporter.materialAssets" />,
	///     <see cref="ModelImporter.textureAssets" />): renamed or moved, it stays the model's. Existing files are never
	///     overwritten: a material edited in the editor keeps its edits on reimport, and models in one folder share
	///     materials and textures of the same name. What an older import made in Materials/ and Textures/ moves next to the
	///     model (GUIDs, so references, stay); the emptied folders go. Normal maps found this way get the Normal Map
	///     texture type unless their import settings were set already.
	/// </summary>
	internal sealed class ModelMaterialExtractor : IModelMaterialExtractor
	{
		#region PrivateFields

		private const string OpaqueShader = "Engine/LitTextured.shader";
		private const string TransparentShader = "Engine/LitTransparent.shader";

		/// <summary>Where imports put their materials and embedded textures before 2026-10-22 (folders next to the model).</summary>
		private const string LegacyMaterialFolder = "Materials";

		private const string LegacyTextureFolder = "Textures";

		#endregion

		#region PublicMethods

		public void Extract(string assetPath, ModelData data)
		{
			string folder = Folder(assetPath);
			string modelName = Path.GetFileNameWithoutExtension(assetPath);
			AssetMeta meta = AssetDatabase.GetMeta(assetPath);
			var importer = meta.importer as ModelImporter ?? AssetDatabase.GetImporter<ModelImporter>(assetPath);
			importer.materialAssets ??= new List<ModelExtractedAsset>();
			importer.textureAssets ??= new List<ModelExtractedAsset>();
			bool created = false;
			bool recorded = false;

			// Only what a mesh uses: Assimp adds a default material to every glTF file, used or not.
			var used = new bool[data.Materials.Count];
			foreach (ModelMeshData mesh in data.Meshes)
			{
				foreach (int index in mesh.Materials)
				{
					if (index >= 0 && index < used.Length)
					{
						used[index] = true;
					}
				}
			}

			// Embedded images first: the materials made below use them as project textures.
			var embedded = new Dictionary<string, string>(StringComparer.Ordinal);
			for (int i = 0; i < data.Materials.Count; i++)
			{
				if (!used[i])
				{
					continue;
				}

				ModelMaterial material = data.Materials[i];
				foreach ((ModelTexture texture, bool normalMap) in new[] { (material.AlbedoMap, false), (material.NormalMap, true) })
				{
					if (texture?.Data == null || texture.AssetPath != null)
					{
						continue;
					}

					if (!embedded.TryGetValue(texture.Name ?? "", out string texturePath))
					{
						texturePath = Place(assetPath, importer.textureAssets, texture.Name ?? "", FileName(texture.Name, modelName) + texture.Extension, LegacyTextureFolder, target =>
						{
							File.WriteAllBytes(AssetDatabase.ToAbsolute(target), texture.Data);
							return true;
						}, ref recorded, ref created);
						embedded[texture.Name ?? ""] = texturePath;
					}

					texture.AssetPath = texturePath;
					if (texturePath != null)
					{
						EnsureTextureImporter(texturePath, normalMap);
					}
				}
			}

			for (int i = 0; i < data.Materials.Count; i++)
			{
				if (!used[i])
				{
					continue;
				}

				ModelMaterial material = data.Materials[i];
				material.AssetPath = Place(assetPath, importer.materialAssets, material.Name, FileName(material.Name, modelName + " Material " + i) + Material.Extension, LegacyMaterialFolder, target => Create(material, target), ref recorded, ref created);
			}

			if (recorded)
			{
				meta.importer = importer;
				AssetDatabase.SaveMeta(assetPath, meta);
			}

			DeleteEmptyLegacyFolder(folder, LegacyMaterialFolder);
			DeleteEmptyLegacyFolder(folder, LegacyTextureFolder);
			if (created)
			{
				AssetDatabase.Refresh(true);
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     Where a material or embedded texture of the model lives: the asset recorded for it (by GUID, wherever it is
		///     now), else <paramref name="fileName" /> next to the model (moved there from an older import's legacy folder,
		///     or made by <paramref name="create" />). Records it; null when it could not be made.
		/// </summary>
		private static string Place(string modelPath, List<ModelExtractedAsset> records, string recordName, string fileName, string legacyFolder, Func<string, bool> create, ref bool recorded, ref bool created)
		{
			string guid = ModelImporter.FindExtracted(records, recordName);
			string known = guid != null ? AssetDatabase.GetPath(guid) : null;
			if (known != null && File.Exists(AssetDatabase.ToAbsolute(known)))
			{
				return known;
			}

			string folder = Folder(modelPath);
			string target = Combine(folder, fileName);
			string legacy = Combine(Combine(folder, legacyFolder), fileName);
			if (!File.Exists(AssetDatabase.ToAbsolute(target)) && File.Exists(AssetDatabase.ToAbsolute(legacy)))
			{
				string error = AssetDatabase.MoveAsset(legacy, target);
				if (error == null)
				{
					Debug.Log($"[Model] {modelPath}: {legacy} moved next to the model ({target})");
				}
				else
				{
					Debug.LogWarning($"[Model] Cannot move {legacy} next to the model: {error}");
					target = legacy;
				}
			}

			if (!File.Exists(AssetDatabase.ToAbsolute(target)))
			{
				try
				{
					if (!create(target))
					{
						return null;
					}

					created = true;
				}
				catch (Exception e)
				{
					Debug.LogError($"[Model] Cannot create {target}: {e.Message}");
					return null;
				}
			}

			recorded |= ModelImporter.RecordExtracted(records, recordName, AssetDatabase.EnsureMeta(target));
			return target;
		}

		private static bool Create(ModelMaterial source, string materialPath)
		{
			bool transparent = source.Color.W < 0.999f;
			string shaderPath = transparent ? TransparentShader : OpaqueShader;
			if (!AssetDatabase.IsEngineResource(shaderPath))
			{
				Debug.LogWarning($"[Model] {materialPath}: the engine has no {shaderPath}; the default material is used");
				source.AssetPath = null;
				return false;
			}

			var material = new Material(InternalStorage.GetOrCreateShader(shaderPath));
			material.SetColor("Color", new Color4(source.Color.X, source.Color.Y, source.Color.Z, source.Color.W));
			material.SetVector4("Emissive", source.Emissive);
			material.SetFloat("Roughness", source.Roughness);
			if (!transparent)
			{
				string albedo = TexturePath(source.AlbedoMap, false);
				if (albedo != null)
				{
					material.SetTexture("DiffuseMap", AssetDatabase.Load<Texture2D>(albedo));
				}

				string normal = TexturePath(source.NormalMap, true);
				if (normal != null)
				{
					material.SetTexture("NormalMap", AssetDatabase.Load<Texture2D>(normal));
				}
			}

			Directory.CreateDirectory(Path.GetDirectoryName(AssetDatabase.ToAbsolute(materialPath)));
			material.Save(materialPath);
			AssetDatabase.EnsureMeta(materialPath);
			InternalStorage.RegisterMaterial(material);
			return true;
		}

		/// <summary>A material's texture as a project file (embedded images were written out first), or null.</summary>
		private static string TexturePath(ModelTexture texture, bool normalMap)
		{
			if (texture?.AssetPath == null)
			{
				return null;
			}

			EnsureTextureImporter(texture.AssetPath, normalMap);
			return texture.AssetPath;
		}

		/// <summary>Import settings for a texture a model's material uses: normal maps get the Normal Map type (unless set already).</summary>
		private static void EnsureTextureImporter(string assetPath, bool normalMap)
		{
			AssetMeta meta = AssetDatabase.GetMeta(assetPath);
			if (meta.importer != null)
			{
				return;
			}

			TextureImporter importer = TextureImporter.CreateDefault(assetPath);
			if (normalMap)
			{
				importer.textureType = TextureImporterType.NormalMap;
			}

			meta.importer = importer;
			AssetDatabase.SaveMeta(assetPath, meta);
		}

		/// <summary>A folder older imports made (Materials, Textures), once nothing is left in it.</summary>
		private static void DeleteEmptyLegacyFolder(string folder, string legacyFolder)
		{
			string legacy = AssetDatabase.ToAbsolute(Combine(folder, legacyFolder));
			try
			{
				if (Directory.Exists(legacy) && Directory.GetFileSystemEntries(legacy).Length == 0)
				{
					Directory.Delete(legacy);
					if (File.Exists(legacy + AssetDatabase.MetaExtension))
					{
						File.Delete(legacy + AssetDatabase.MetaExtension);
					}

					Debug.Log("[Model] Removed the empty folder " + Combine(folder, legacyFolder));
				}
			}
			catch (IOException e)
			{
				Debug.LogWarning("[Model] Cannot remove " + legacy + ": " + e.Message);
			}
		}

		private static string Folder(string assetPath)
		{
			int slash = assetPath.LastIndexOf('/');
			return slash >= 0 ? assetPath.Substring(0, slash) : "";
		}

		private static string Combine(string a, string b)
		{
			return a.Length == 0 ? b : a + "/" + b;
		}

		/// <summary>A file name from a material or texture name (characters files cannot have dropped).</summary>
		private static string FileName(string name, string fallback)
		{
			var builder = new StringBuilder();
			foreach (char c in name ?? "")
			{
				if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && c != '/' && c != '\\')
				{
					builder.Append(c);
				}
			}

			string result = builder.ToString().Trim().TrimEnd('.');
			return result.Length > 0 ? result : fallback;
		}

		#endregion
	}
}
