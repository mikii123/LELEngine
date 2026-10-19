using System;
using System.IO;
using System.Text;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Turns the materials of an import into project assets: Materials/&lt;material name&gt;.material next to the model
	///     (the engine's LitTextured for opaque materials, LitTransparent below full opacity) and the embedded textures as files in
	///     Textures/. Existing files are never overwritten: a material edited in the editor keeps its edits on reimport,
	///     and models in one folder share materials of the same name. Normal maps found this way get the Normal Map
	///     texture type unless their import settings were set already.
	/// </summary>
	internal sealed class ModelMaterialExtractor : IModelMaterialExtractor
	{
		#region PrivateFields

		private const string OpaqueShader = "Engine/LitTextured.shader";
		private const string TransparentShader = "Engine/LitTransparent.shader";

		#endregion

		#region PublicMethods

		public void Extract(string assetPath, ModelData data)
		{
			string folder = Folder(assetPath);
			string modelName = Path.GetFileNameWithoutExtension(assetPath);
			bool created = false;
			for (int i = 0; i < data.Materials.Count; i++)
			{
				ModelMaterial material = data.Materials[i];
				string name = FileName(material.Name, modelName + " Material " + i);
				string materialPath = Combine(Combine(folder, "Materials"), name + Material.Extension);
				material.AssetPath = materialPath;
				if (File.Exists(AssetDatabase.ToAbsolute(materialPath)))
				{
					continue;
				}

				try
				{
					created |= Create(material, materialPath, folder, modelName);
				}
				catch (Exception e)
				{
					Debug.LogError($"[Model] Cannot create the material {materialPath}: {e.Message}");
					material.AssetPath = null;
				}
			}

			if (created)
			{
				AssetDatabase.Refresh(true);
			}
		}

		#endregion

		#region PrivateMethods

		private static bool Create(ModelMaterial source, string materialPath, string folder, string modelName)
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
				string albedo = TexturePath(source.AlbedoMap, folder, modelName, false);
				if (albedo != null)
				{
					material.SetTexture("DiffuseMap", AssetDatabase.Load<Texture2D>(albedo));
				}

				string normal = TexturePath(source.NormalMap, folder, modelName, true);
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

		/// <summary>A project texture as is; an embedded one written to Textures/ (once).</summary>
		private static string TexturePath(ModelTexture texture, string folder, string modelName, bool normalMap)
		{
			if (texture == null)
			{
				return null;
			}

			string assetPath = texture.AssetPath;
			if (assetPath == null && texture.Data != null)
			{
				assetPath = Combine(Combine(folder, "Textures"), FileName(texture.Name, modelName) + texture.Extension);
				string file = AssetDatabase.ToAbsolute(assetPath);
				if (!File.Exists(file))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(file));
					File.WriteAllBytes(file, texture.Data);
				}
			}

			if (assetPath == null)
			{
				return null;
			}

			AssetMeta meta = AssetDatabase.GetMeta(assetPath);
			if (meta.importer == null)
			{
				TextureImporter importer = TextureImporter.CreateDefault(assetPath);
				if (normalMap)
				{
					importer.textureType = TextureImporterType.NormalMap;
				}

				meta.importer = importer;
				AssetDatabase.SaveMeta(assetPath, meta);
			}

			return assetPath;
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
