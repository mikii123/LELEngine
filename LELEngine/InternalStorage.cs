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

		/// <summary>Guards <see cref="Shaders" /> and <see cref="Materials" /> (scenes load on a background thread too).</summary>
		public static readonly object Sync = new object();

		/// <summary>The loaded shaders in load order, a snapshot replaced on every change: the renderer iterates it while a scene loads.</summary>
		public static IReadOnlyList<ShaderProgram> ShaderList => shaderList;

		#endregion

		#region PrivateFields

		private static ShaderProgram[] shaderList = Array.Empty<ShaderProgram>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     Releases every loaded project asset (another project was opened: the same asset paths now mean other
		///     files). Renderers that still use them must be destroyed first.
		/// </summary>
		public static void Clear()
		{
			lock (Sync)
			{
				foreach (ShaderProgram shader in Shaders.Values)
				{
					shader.Delete();
				}

				Shaders.Clear();
				Materials.Clear();
				shaderList = Array.Empty<ShaderProgram>();
			}

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
			string key = ShaderKey(name);
			lock (Sync)
			{
				ShaderProgram sp;
				Shaders.TryGetValue(key, out sp);
				return sp;
			}
		}

		/// <summary>
		///     Gets a shader by asset path, file name or engine path ("Engine/Skybox.shader"). Creates it on first use;
		///     programs are cached by their resolved path, so "Lit.shader" and "Shaders/Lit.shader" are the same program.
		/// </summary>
		public static ShaderProgram GetOrCreateShader(string name)
		{
			string key = ShaderKey(name);
			lock (Sync)
			{
				ShaderProgram sp;
				Shaders.TryGetValue(key, out sp);
				if (sp == null)
				{
					Console.WriteLine("Creating Shader " + key);
					sp = new ShaderProgram(key);
					Shaders.Add(key, sp);
					shaderList = new List<ShaderProgram>(Shaders.Values).ToArray();
				}

				return sp;
			}
		}

		/// <summary>
		///     Gets a material by asset path or file name (see <see cref="AssetDatabase.Resolve" />). Loads it on first
		///     use; materials are cached by their asset path.
		/// </summary>
		/// <param name="name">Asset path or file name of the material (with .*)</param>
		public static Material GetOrCreateMaterial(string name)
		{
			string assetPath = AssetDatabase.Resolve(name, "Materials") ?? name;
			// Held while the material loads (its shader and textures): a scene loading in the background and the game
			// asking for the same material get one object.
			lock (Sync)
			{
				Material mat;
				if (!Materials.TryGetValue(assetPath, out mat))
				{
					Console.WriteLine("Creating Material " + assetPath);
					mat = Material.Load(assetPath);
					Materials.Add(assetPath, mat);
				}

				return mat;
			}
		}

		/// <summary>Registers a material object under its asset path (a material created and saved by the editor).</summary>
		public static void RegisterMaterial(Material material)
		{
			if (material?.AssetPath != null)
			{
				lock (Sync)
				{
					Materials[material.AssetPath] = material;
				}
			}
		}

		/// <summary>Drops a cached material (its file was deleted or replaced) so the next use reloads it.</summary>
		public static void ForgetMaterial(string assetPath)
		{
			lock (Sync)
			{
				Materials.Remove(assetPath);
			}
		}

		/// <summary>Drops a cached shader program and deletes it (no loaded scene uses it any more).</summary>
		public static void ForgetShader(ShaderProgram shader)
		{
			lock (Sync)
			{
				foreach (KeyValuePair<string, ShaderProgram> entry in Shaders)
				{
					if (entry.Value == shader)
					{
						Shaders.Remove(entry.Key);
						break;
					}
				}

				shaderList = new List<ShaderProgram>(Shaders.Values).ToArray();
			}

			shader.Delete();
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
