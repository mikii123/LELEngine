using System;
using System.Collections.Generic;
using System.IO;
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

		// The material files as read or written here (stamp of time and length), by asset path: changes on disk show.
		private static readonly Dictionary<string, long> materialStamps = new Dictionary<string, long>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     An asset file or folder moved (<see cref="AssetDatabase.MoveAsset" />): every asset cache keeps its loaded
		///     objects under their new paths, so the scenes and editor windows holding them see the move.
		/// </summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (Sync)
			{
				AssetDatabase.RekeyMoved(Materials, fromPath, toPath, (material, path) => material.AssetPath = path);
				AssetDatabase.RekeyMoved(materialStamps, fromPath, toPath, (stamp, path) => { });
				AssetDatabase.RekeyMoved(Shaders, fromPath, toPath, (shader, path) => shader.AssetPath = path);
			}

			TextureAssets.AssetMoved(fromPath, toPath);
			ModelAssets.AssetMoved(fromPath, toPath);
			Prefab.AssetMoved(fromPath, toPath);
			ScriptableObjectAssets.AssetMoved(fromPath, toPath);
			PhysicsMaterial2D.AssetMoved(fromPath, toPath);
			Audio.AudioClip.AssetMoved(fromPath, toPath);
			Audio.AudioProject.AssetMoved(fromPath, toPath);
			UIElements.UIAssets.AssetMoved(fromPath, toPath);
			VFX.VfxGraph.AssetMoved(fromPath, toPath);
			Animation.AnimationClip.AssetMoved(fromPath, toPath);
			Animation.AnimatorController.AssetMoved(fromPath, toPath);
		}

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
				materialStamps.Clear();
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
			Animation.AnimationClip.ClearCache();
			Animation.AnimatorController.ClearCache();
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
					long stamp = FileStampOf(AssetDatabase.ToAbsolute(assetPath));
					mat = Material.Load(assetPath);
					Materials.Add(assetPath, mat);
					materialStamps[assetPath] = stamp;
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
					materialStamps[material.AssetPath] = FileStampOf(AssetDatabase.ToAbsolute(material.AssetPath));
				}
			}
		}

		/// <summary>A material file was written here (<see cref="Material.Save" />): no change on disk to read back.</summary>
		internal static void MaterialWritten(string assetPath)
		{
			lock (Sync)
			{
				if (Materials.ContainsKey(assetPath))
				{
					materialStamps[assetPath] = FileStampOf(AssetDatabase.ToAbsolute(assetPath));
				}
			}
		}

		/// <summary>Drops a cached material (its file was deleted or replaced) so the next use reloads it.</summary>
		public static void ForgetMaterial(string assetPath)
		{
			lock (Sync)
			{
				Materials.Remove(assetPath);
				materialStamps.Remove(assetPath);
			}
		}

		/// <summary>
		///     Reads again, into the same objects, the loaded project materials whose files changed since they were read or
		///     written here (a checkout, a text editor); returns them.
		/// </summary>
		public static List<Material> ReloadChangedMaterials()
		{
			var changed = new List<KeyValuePair<string, Material>>();
			lock (Sync)
			{
				foreach (KeyValuePair<string, Material> entry in Materials)
				{
					if (materialStamps.TryGetValue(entry.Key, out long known) && !AssetDatabase.IsEngineResource(entry.Key))
					{
						string file = AssetDatabase.ToAbsolute(entry.Key);
						if (File.Exists(file) && FileStampOf(file) != known)
						{
							changed.Add(entry);
						}
					}
				}
			}

			var reloaded = new List<Material>();
			foreach (KeyValuePair<string, Material> entry in changed)
			{
				string file = AssetDatabase.ToAbsolute(entry.Key);
				long stamp = FileStampOf(file);
				byte[] data;
				try
				{
					data = File.ReadAllBytes(file);
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					// Being written: the next change is noticed again.
					continue;
				}

				lock (Sync)
				{
					materialStamps[entry.Key] = stamp;
				}

				try
				{
					entry.Value.ApplyBytes(data);
					entry.Value.name = Path.GetFileNameWithoutExtension(entry.Key);
					reloaded.Add(entry.Value);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Material] {entry.Key} changed on disk but cannot be read ({e.Message}); it stays as it was");
				}
			}

			return reloaded;
		}

		/// <summary>
		///     Rebuilds in place the loaded shader programs whose files (the .shader or an include) changed on disk; the
		///     loaded materials of those shaders get the properties they add. Returns the programs rebuilt.
		/// </summary>
		public static List<ShaderProgram> ReloadChangedShaders()
		{
			var reloaded = new List<ShaderProgram>();
			foreach (ShaderProgram shader in shaderList)
			{
				if (shader.SourceChanged && shader.Reload())
				{
					reloaded.Add(shader);
				}
			}

			if (reloaded.Count > 0)
			{
				List<Material> materials;
				lock (Sync)
				{
					materials = new List<Material>(Materials.Values);
				}

				foreach (Material material in materials)
				{
					if (reloaded.Contains(material.shader))
					{
						material.ResetToShaderDefaults(false);
					}
				}
			}

			return reloaded;
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

		private static long FileStampOf(string file)
		{
			var info = new FileInfo(file);
			return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
		}

		private static string ShaderKey(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return name;
			}

			string asset = AssetDatabase.Resolve(name, "Shaders");
			if (asset != null || AssetDatabase.IsEngineResource(name))
			{
				return asset ?? name.Replace('\\', '/');
			}

			// Neither a project asset nor an engine path: a project copy of an engine shader that was removed.
			return AssetDatabase.EngineShaderFallback(name) ?? name.Replace('\\', '/');
		}

		#endregion
	}
}
