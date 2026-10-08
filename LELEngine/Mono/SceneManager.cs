using System.IO;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>Owns the active scene (the one the host updates and renders).</summary>
	public static class SceneManager
	{
		#region PublicFields

		public static Scene ActiveScene { get; private set; }

		#endregion

		#region PublicMethods

		public static Scene CreateScene(string name)
		{
			return new Scene(name);
		}

		/// <summary>Makes the scene active; its settings become the live lighting / GI settings.</summary>
		public static void SetActiveScene(Scene scene)
		{
			ActiveScene = scene;
			scene?.Settings.Activate();
		}

		/// <summary>
		///     Loads a scene file (asset path, or absolute path), unloads the previous active scene, activates the new
		///     one and starts its lifecycle in play mode (<paramref name="playing" />) or edit mode.
		/// </summary>
		public static Scene LoadScene(string path, bool playing = true)
		{
			string absolute = Path.IsPathRooted(path) ? path : AssetDatabase.ToAbsolute(AssetDatabase.Resolve(path, null) ?? path);
			Scene scene = SceneSerializer.Load(absolute);

			Scene previous = ActiveScene;
			previous?.Unload();
			SetActiveScene(scene);
			scene.StartLifecycle(playing);
			return scene;
		}

		#endregion
	}
}
