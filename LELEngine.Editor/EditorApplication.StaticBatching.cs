using System;
using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>
	///     Static batching in play mode (the user's ask, 2026-11-20: it has to work in the editor at run time too, not
	///     only in builds): with Project Settings > Graphics > Static Batching on, the scene play mode starts and every
	///     scene the game then loads get the batches a build of them would have (<see cref="StaticBatcher" />: the same
	///     analysis, from the same cache in the project's Library, <see cref="StaticBatchCache" />), added to the loaded
	///     scene before it starts. The edit scene never has them: stopping brings it back from its snapshot, and the
	///     batches' meshes are unloaded.
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PrivateMethods

		/// <summary>
		///     The batches of the game's copy of the edit scene (read from <paramref name="snapshot" />), added to it before it
		///     starts; from here on the scenes the game loads are batched as they are read.
		/// </summary>
		private void BatchPlayScene(Scene play, byte[] snapshot, string scenePath)
		{
			SceneManager.SceneRead = null;
			if (Project == null || !(ProjectSettings.Current.Graphics?.StaticBatching ?? true))
			{
				return;
			}

			SceneManager.SceneRead = BatchLoadedScene;
			// Nothing static to draw: not even the document's hash is worked out.
			if (HasStaticRenderers(play))
			{
				BatchScene(play, snapshot, scenePath);
			}
		}

		/// <summary>Play mode ends: scenes read from now on are the editor's, and the batches' meshes go.</summary>
		private void EndPlayBatching()
		{
			SceneManager.SceneRead = null;
			ModelAssets.UnloadUnder(AssetDatabase.StaticBatchPrefix);
		}

		// A scene the running game loaded, on the thread that read it (SceneManager.SceneRead).
		private void BatchLoadedScene(Scene scene, byte[] file, string scenePath)
		{
			if (HasStaticRenderers(scene))
			{
				BatchScene(scene, file, scenePath);
			}
		}

		private void BatchScene(Scene scene, byte[] document, string scenePath)
		{
			ProjectInfo project = Project;
			if (project == null)
			{
				return;
			}

			try
			{
				StaticBatchCache.Result result = StaticBatchCache.Get(project, document, scenePath);
				int added = StaticBatcher.Apply(scene, result.Batches);
				Debug.Log($"[Editor] Static batching, {scenePath ?? scene.Name}: {result.Summary} ({result.Milliseconds:F0} ms{(result.FromCache ? ", from the Library" : "")})"
					+ (added != result.Batches.Count ? $"; {result.Batches.Count - added} batches left out (their meshes are missing)" : ""));
			}
			catch (Exception e)
			{
				// The scene plays as it is: its objects draw on their own.
				Debug.LogWarning($"[Editor] Static batching, {scenePath ?? scene.Name}: {e.Message}; the scene plays without batches");
			}
		}

		private static bool HasStaticRenderers(Scene scene)
		{
			foreach (GameObject go in scene.GameObjects)
			{
				if (go.isStatic && go.GetComponent<MeshRenderer>() != null)
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}
}
