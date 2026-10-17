using LELEngine;
using LELEngine.Shaders;

namespace TestGame.Scenes
{
	/// <summary>
	///     Objects of the test scenes as instances of the harness's model assets (<see cref="Model.Instantiate" />, the
	///     way the editor builds models), and the harness's materials by asset path.
	/// </summary>
	internal static class SceneModels
	{
		#region PublicFields

		public const string Cube = "Meshes/cube.obj";
		public const string Sphere = "Meshes/sphere.obj";

		#endregion

		#region PublicMethods

		/// <summary>An instance of a model asset, named <paramref name="name" />.</summary>
		public static GameObject Instantiate(Scene scene, string model, string name)
		{
			GameObject go = ModelAssets.Load(model).Instantiate(scene);
			go.Name = name;
			return go;
		}

		/// <summary>A material of the harness (Materials/<paramref name="fileName" />).</summary>
		public static Material Material(string fileName)
		{
			return InternalStorage.GetOrCreateMaterial("Materials/" + fileName);
		}

		#endregion
	}
}
