using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Rendering.Impostors;
using LELEngine.Shaders;

namespace LELEngine.Editor
{
	/// <summary>
	///     Impostor assets of a project: a bake written out as files. The atlases become PNG images with their import
	///     settings, the proxy mesh an .obj model and the material a .material file, next to the asset on the first bake
	///     and wherever the asset's own references point after that (files the user moved or renamed stay theirs). The
	///     asset references them, so scenes, prefabs and game builds use them as any other asset.
	/// </summary>
	internal static class ImpostorAssets
	{
		#region PrivateFields

		// The first line of a proxy model the bake wrote (ImpostorMesh.ToObj): only such a file is written again.
		private const string ProxyMark = "# Impostor proxy";

		#endregion

		#region PublicFields

		/// <summary>Counts the bakes written: what shows a bake (the inspector's preview) is made again when it changes.</summary>
		public static int Version { get; private set; }

		#endregion

		#region PublicMethods

		/// <summary>Why the asset cannot be baked now, or null when it can.</summary>
		public static string WhyNot(Impostor impostor, EditorApplication editor)
		{
			if (impostor == null)
			{
				return "No impostor.";
			}

			if (string.IsNullOrEmpty(impostor.AssetPath) || AssetDatabase.IsEngineResource(impostor.AssetPath))
			{
				return "Only an impostor asset of the project bakes into files.";
			}

			if (impostor.SourcePrefab == null && impostor.SourceModel == null)
			{
				return "Set the Source Prefab or the Source Model first.";
			}

			if (editor != null && editor.IsPlaying)
			{
				return "Stop the game first: a bake replaces assets the scene uses.";
			}

			return null;
		}

		/// <summary>Every impostor asset of the project (their files are not loaded).</summary>
		public static List<string> FindAll()
		{
			var paths = new List<string>();
			foreach (string path in AssetDatabase.GetAllAssetPaths(ScriptableObjectAssets.Extension))
			{
				if (ScriptableObjectAssets.IsOfType(path, typeof(Impostor)))
				{
					paths.Add(path);
				}
			}

			paths.Sort(StringComparer.OrdinalIgnoreCase);
			return paths;
		}

		/// <summary>
		///     Bakes the asset from its source with its settings and writes the result: the atlases, the proxy model, the
		///     material and the asset itself. Loaded textures and the material are updated in place; when the proxy's box
		///     changed, <paramref name="editor" />'s scene is rebuilt around the model's reload (its renderers hold the old
		///     mesh). Throws <see cref="InvalidOperationException" /> / <see cref="ArgumentException" /> with the reason
		///     when there is nothing to bake.
		/// </summary>
		public static ImpostorBake Bake(Impostor impostor, EditorApplication editor)
		{
			string reason = WhyNot(impostor, editor);
			if (reason != null)
			{
				throw new InvalidOperationException(reason);
			}

			ImpostorBake bake = BakeSource(impostor);
			string assetPath = impostor.AssetPath;
			string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
			string stem = Path.GetFileNameWithoutExtension(assetPath);
			bool created = false;

			// The shape holds directions, depths and distances: every level of it matters, so builds keep it exact
			// unless the user chooses otherwise in its import settings. The colours compress as any colour map.
			Texture2D shape = WriteAtlas(impostor.ShapeMap, folder, stem, "Shape", bake.Shape, bake.AtlasSize, false, TextureCompression.None, ref created);
			Texture2D colour = WriteAtlas(impostor.BaseMap, folder, stem, "Base", bake.Base, bake.AtlasSize, true, TextureCompression.BC7, ref created);
			Texture2D specular = bake.Specular != null
				? WriteAtlas(impostor.SpecularMap, folder, stem, "Specular", bake.Specular, bake.AtlasSize, true, TextureCompression.BC7, ref created)
				: Unused(impostor.SpecularMap, "specular");
			Texture2D emission = bake.Emission != null
				? WriteAtlas(impostor.EmissionMap, folder, stem, "Emission", bake.Emission, bake.AtlasSize, true, TextureCompression.BC7, ref created)
				: Unused(impostor.EmissionMap, "emission");
			if (shape == null || colour == null || (bake.Specular != null && specular == null) || (bake.Emission != null && emission == null))
			{
				throw new InvalidOperationException("The atlases were written but do not load as textures (see the console).");
			}

			string proxyPath = WriteProxy(impostor.Mesh, folder, stem, bake, out bool proxyChanged, ref created);
			if (proxyChanged && ModelAssets.IsLoaded(proxyPath))
			{
				// Renderers hold the old box: the scene is rebuilt from its saved state with the new one.
				if (editor != null)
				{
					editor.ReloadScene(() => ModelAssets.Unload(proxyPath));
				}
				else
				{
					ModelAssets.Unload(proxyPath);
				}
			}

			Mesh mesh = ModelAssets.LoadMesh(proxyPath);
			if (mesh == null)
			{
				throw new InvalidOperationException("The proxy model " + proxyPath + " does not import.");
			}

			Material material = WriteMaterial(impostor.Material, folder, stem, bake, shape, colour, specular, emission, ref created);
			impostor.SetBaked(bake, mesh, material, shape, colour, specular, emission);
			ScriptableObjectAssets.Save(impostor);
			if (created)
			{
				AssetDatabase.Refresh(true);
			}

			Version++;
			editor?.NotifyAssetsChanged();
			string maps = "Shape, Base" + (specular != null ? ", Specular" : "") + (emission != null ? ", Emission" : "");
			Debug.Log($"[Impostor] Baked {assetPath}: {bake.Frames} x {bake.Frames} views of {bake.FrameSize} texels" +
			          $"{(bake.Hemisphere ? " (upper half)" : "")}, atlases of {bake.AtlasSize} x {bake.AtlasSize} ({maps}), " +
			          $"{bake.SourceTriangles} triangles drawn as 12, in {bake.Milliseconds:0} ms");
			return bake;
		}

		#endregion

		#region PrivateMethods

		// The source in a scene of its own that nothing runs or draws; the bake reads its renderers.
		private static ImpostorBake BakeSource(Impostor impostor)
		{
			var scene = new Scene("Impostor Bake");
			try
			{
				GameObject root = impostor.SourcePrefab != null ? impostor.SourcePrefab.Instantiate(scene) : impostor.SourceModel.Instantiate(scene);
				if (root == null)
				{
					throw new InvalidOperationException("The source of " + impostor.AssetPath + " cannot be instantiated.");
				}

				return ImpostorBaker.Bake(root, impostor.Settings);
			}
			finally
			{
				scene.Unload();
			}
		}

		// An atlas as a PNG (the asset's own file when it has one) imported as the shader reads it: every mip level,
		// clamped, no anisotropy (the shader names the level), the file's size. The compression of builds is set once,
		// then it is the user's.
		private static Texture2D WriteAtlas(Texture2D current, string folder, string stem, string kind, byte[] texels, int size, bool srgb, TextureCompression compression, ref bool created)
		{
			string path = OwnFile(current?.AssetPath, ".png");
			if (path == null)
			{
				path = UniquePath(Combine(folder, stem + " " + kind + ".png"));
				created = true;
			}

			File.WriteAllBytes(AssetDatabase.ToAbsolute(path), Png.Encode(texels, size, size, true));
			AssetDatabase.EnsureMeta(path);
			AssetMeta meta = AssetDatabase.GetMeta(path);
			var importer = meta.importer as TextureImporter;
			if (importer == null)
			{
				importer = new TextureImporter { compression = compression };
			}

			importer.textureType = TextureImporterType.Default;
			importer.shape = TextureImporterShape.Texture2D;
			importer.sRGB = srgb;
			importer.generateMipmaps = true;
			importer.filterMode = FilterMode.Trilinear;
			importer.wrapMode = TextureWrap.Clamp;
			importer.anisoLevel = 1;
			importer.maxSize = 0;
			importer.version++;
			meta.importer = importer;
			AssetDatabase.SaveMeta(path, meta);
			// A loaded texture takes the new image in place (materials keep the object); else it loads now.
			TextureAssets.Reimport(path);
			AssetThumbnails.Invalidate(path);
			return TextureAssets.LoadTexture(path) as Texture2D;
		}

		// An atlas of an earlier bake this one has no use for: the file stays (it may be used elsewhere), the asset
		// lets go of it.
		private static Texture2D Unused(Texture2D previous, string kind)
		{
			if (previous?.AssetPath != null)
			{
				Debug.Log($"[Impostor] {previous.AssetPath} is no longer used: this bake has no {kind} atlas.");
			}

			return null;
		}

		// The proxy box as an .obj model (the asset's own when the bake wrote it). Written only when the box changed:
		// a model written again imports again, and scenes using it reload.
		private static string WriteProxy(Mesh current, string folder, string stem, ImpostorBake bake, out bool changed, ref bool created)
		{
			string path = OwnFile(current?.AssetPath, ".obj");
			if (path != null && !IsProxy(AssetDatabase.ToAbsolute(path)))
			{
				path = null;
			}

			if (path == null)
			{
				path = UniquePath(Combine(folder, stem + ".obj"));
				created = true;
			}

			string file = AssetDatabase.ToAbsolute(path);
			string text = ImpostorMesh.ToObj(bake.BoundsMin, bake.BoundsMax);
			changed = !File.Exists(file) || File.ReadAllText(file) != text;
			if (changed)
			{
				File.WriteAllText(file, text);
			}

			AssetDatabase.EnsureMeta(path);
			AssetMeta meta = AssetDatabase.GetMeta(path);
			if (!(meta.importer is ModelImporter))
			{
				// Nothing to make materials of: the box draws with the impostor's material.
				meta.importer = new ModelImporter { importMaterials = false };
				AssetDatabase.SaveMeta(path, meta);
			}

			return path;
		}

		private static bool IsProxy(string file)
		{
			try
			{
				using (var reader = new StreamReader(file))
				{
					string first = reader.ReadLine();
					return first != null && first.StartsWith(ProxyMark, StringComparison.Ordinal);
				}
			}
			catch (IOException)
			{
				return false;
			}
		}

		// The asset's material with the bake's atlases and values (its tint, cutoff, parallax and bias stay as set), or
		// a new one.
		private static Material WriteMaterial(Material current, string folder, string stem, ImpostorBake bake, Texture2D shape, Texture2D colour, Texture2D specular, Texture2D emission, ref bool created)
		{
			Material material = current;
			string path = OwnFile(current?.AssetPath, Material.Extension);
			ShaderProgram shader = InternalStorage.GetOrCreateShader(ImpostorBaker.ShaderPath);
			if (path == null)
			{
				path = UniquePath(Combine(folder, stem + Material.Extension));
				material = new Material(shader);
				created = true;
			}
			else if (!ImpostorBaker.IsImpostor(material))
			{
				material.shader = shader;
			}

			Impostor.SetupMaterial(material, bake, shape, colour, specular, emission);
			material.Save(path);
			// Its GUID now: the asset saved next references it by it.
			AssetDatabase.EnsureMeta(path);
			InternalStorage.RegisterMaterial(material);
			return material;
		}

		// A file of the project the asset references already: the bake writes into it again.
		private static string OwnFile(string assetPath, string extension)
		{
			if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsEngineResource(assetPath) || !assetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			return File.Exists(AssetDatabase.ToAbsolute(assetPath)) ? assetPath : null;
		}

		private static string Combine(string folder, string file)
		{
			return string.IsNullOrEmpty(folder) ? file : folder.TrimEnd('/') + "/" + file;
		}

		// The path, or with " 1", " 2"... before the extension when a file has it: a bake never writes over a file
		// that is not the asset's.
		private static string UniquePath(string assetPath)
		{
			if (!File.Exists(AssetDatabase.ToAbsolute(assetPath)))
			{
				return assetPath;
			}

			string extension = Path.GetExtension(assetPath);
			string stem = assetPath.Substring(0, assetPath.Length - extension.Length);
			for (int i = 1;; i++)
			{
				string candidate = stem + " " + i + extension;
				if (!File.Exists(AssetDatabase.ToAbsolute(candidate)))
				{
					return candidate;
				}
			}
		}

		#endregion
	}
}
