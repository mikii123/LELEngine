using System;
using System.Collections.Generic;
using LELEngine.Shaders;

namespace LELEngine
{
	public sealed class InternalStorage
	{
		#region PublicFields

		/// <summary>
		///     Contains all of the loaded shaders
		/// </summary>
		public static Dictionary<string, ShaderProgram> Shaders { get; } = new Dictionary<string, ShaderProgram>();

		/// <summary>
		///     Contains all of the loaded materials
		/// </summary>
		public static Dictionary<string, Material> Materials { get; } = new Dictionary<string, Material>();

		/// <summary>
		///     Contains all of the loaded meshes
		/// </summary>
		public static Dictionary<string, Mesh> Meshes { get; } = new Dictionary<string, Mesh>();

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
			Meshes.Clear();
			Rendering.GeometryPool.Clear();
		}

		/// <summary>
		///     Gets the shader specified by name. Returns null if it doesn't exist
		/// </summary>
		/// <param name="name">Name of the shader (with .*)</param>
		/// <returns></returns>
		public static ShaderProgram GetShader(string name)
		{
			ShaderProgram sp = null;
			Shaders.TryGetValue(name, out sp);

			return sp;
		}

		/// <summary>
		///     Gets a shader specified by name. Creates new one if it doesn't exist.
		/// </summary>
		/// <param name="name">Name of the shader (with .*)</param>
		/// <returns></returns>
		public static ShaderProgram GetOrCreateShader(string name)
		{
			ShaderProgram sp = null;
			Shaders.TryGetValue(name, out sp);
			if (sp == null)
			{
				Console.WriteLine("Creating Shader " + name);
				sp = new ShaderProgram(name);
				Shaders.Add(name, sp);
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
				mat = new Material(assetPath);
				Materials.Add(assetPath, mat);
			}

			return mat;
		}

		/// <summary>
		///     Gets a mesh by asset path or file name. Loads it on first use; meshes are cached by their asset path.
		/// </summary>
		/// <param name="name">Asset path or file name of the mesh (with .*)</param>
		public static Mesh GetOrCreateMesh(string name)
		{
			string assetPath = AssetDatabase.Resolve(name, "Meshes") ?? name;
			Mesh mesh;
			if (!Meshes.TryGetValue(assetPath, out mesh))
			{
				Console.WriteLine("Creating Mesh " + assetPath);
				mesh = new Mesh(assetPath);
				Meshes.Add(assetPath, mesh);
			}

			return mesh;
		}

		#endregion
	}
}
