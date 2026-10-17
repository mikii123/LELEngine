using System;
using System.Collections.Generic;
using LELEngine.Shaders;

namespace LELEngine
{
	public sealed class InternalStorage
	{
		#region PublicFields

		/// <summary>
		///     Contains all of the loaded shaders (by asset path, or engine-relative path for engine shaders)
		/// </summary>
		public static Dictionary<string, ShaderProgram> Shaders { get; } = new Dictionary<string, ShaderProgram>();

		/// <summary>
		///     Contains all of the loaded materials (by asset path)
		/// </summary>
		public static Dictionary<string, Material> Materials { get; } = new Dictionary<string, Material>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     Releases every loaded project asset (another project was opened: the same asset paths now mean other
		///     files). Renderers that still use them must be destroyed first.
		/// </summary>
		public static void Clear()
		{
			foreach (ShaderProgram shader in Shaders.Values)
			{
				shader.Delete();
			}

			Shaders.Clear();
			Materials.Clear();
			ModelAssets.Clear();
			TextureAssets.Clear();
			PhysicsMaterial2D.ClearCache();
			Rendering.GeometryPool.Clear();
			Navigation.NavMeshSurface.ClearCaches();
			UIElements.UIAssets.Clear();
			Rendering.SkyEnvironment.Clear();
			VFX.VfxRuntime.Clear();
			VFX.VfxGraph.ClearCache();
		}

		/// <summary>
		///     Gets the shader specified by name. Returns null if it doesn't exist
		/// </summary>
		/// <param name="name">Name of the shader (with .*)</param>
		public static ShaderProgram GetShader(string name)
		{
			ShaderProgram sp = null;
			Shaders.TryGetValue(ShaderKey(name), out sp);

			return sp;
		}

		/// <summary>
		///     Gets a shader by asset path, file name or engine path ("Engine/Skybox.shader"). Creates it on first use;
		///     programs are cached by their resolved path, so "Lit.shader" and "Shaders/Lit.shader" are the same program.
		/// </summary>
		public static ShaderProgram GetOrCreateShader(string name)
		{
			string key = ShaderKey(name);
			ShaderProgram sp = null;
			Shaders.TryGetValue(key, out sp);
			if (sp == null)
			{
				Console.WriteLine("Creating Shader " + key);
				sp = new ShaderProgram(key);
				Shaders.Add(key, sp);
			}

			return sp;
		}

		/// <summary>
		///     Gets a material by asset path or file name (see <see cref="AssetDatabase.Resolve" />). Loads it on first
		///     use; materials are cached by their asset path.
		/// </summary>
		/// <param name="name">Asset path or file name of the material (with .*)</param>
		public static Material GetOrCreateMaterial(string name)
		{
			string assetPath = AssetDatabase.Resolve(name, "Materials") ?? name;
			Material mat;
			if (!Materials.TryGetValue(assetPath, out mat))
			{
				Console.WriteLine("Creating Material " + assetPath);
				mat = Material.Load(assetPath);
				Materials.Add(assetPath, mat);
			}

			return mat;
		}

		/// <summary>Registers a material object under its asset path (a material created and saved by the editor).</summary>
		public static void RegisterMaterial(Material material)
		{
			if (material?.AssetPath != null)
			{
				Materials[material.AssetPath] = material;
			}
		}

		/// <summary>Drops a cached material (its file was deleted or replaced) so the next use reloads it.</summary>
		public static void ForgetMaterial(string assetPath)
		{
			Materials.Remove(assetPath);
		}

		#endregion

		#region PrivateMethods

		private static string ShaderKey(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return name;
			}

			return AssetDatabase.Resolve(name, "Shaders") ?? name.Replace('\\', '/');
		}

		#endregion
	}
}
