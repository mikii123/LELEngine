using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Hexa.NET.ImGui;
using LELEngine.Navigation;

namespace LELEngine.Editor
{
	internal sealed partial class EditorApplication
	{
		#region PublicFields

		/// <summary>What the last navigation bake of this session made, per agent type (the Navigation window shows it).</summary>
		public IReadOnlyList<SceneNavMeshBakeResult> LastNavigationBake { get; private set; } = Array.Empty<SceneNavMeshBakeResult>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     Bakes the open scene's navigation (Window > Navigation: its settings, every agent type it names) into its
		///     navmesh asset: the one the scene names, else "Scene/NavMesh.asset" in the scene's folder, made now (the scene
		///     then names it: save the scene). The navmeshes are part of the world at once. Throws
		///     <see cref="InvalidOperationException" /> when the game plays or the scene has no file yet.
		/// </summary>
		public IReadOnlyList<SceneNavMeshBakeResult> BakeSceneNavigation()
		{
			Scene scene = Scene;
			if (scene == null)
			{
				throw new InvalidOperationException("No scene is open.");
			}

			if (IsPlaying)
			{
				throw new InvalidOperationException("Stop the game first: a bake writes the scene's navmesh asset.");
			}

			if (string.IsNullOrEmpty(scene.Path))
			{
				throw new InvalidOperationException("Save the scene first: its navmeshes are written next to it.");
			}

			Edits.Commit();
			SceneNavigationSettings settings = scene.Settings.Navigation ??= new SceneNavigationSettings();
			List<NavMeshAgentType> agentTypes = SceneNavigation.AgentTypesOf(settings);
			if (agentTypes.Count == 0)
			{
				throw new InvalidOperationException("No agent type to bake: add one in Window > Navigation (Agent Types), or tick one for the scene.");
			}

			using Progress.Scope progress = Progress.Begin("Baking navigation");
			var results = new List<SceneNavMeshBakeResult>();
			var navMeshes = new List<SceneNavMesh>();
			foreach (NavMeshAgentType agentType in agentTypes)
			{
				SceneNavMeshBakeResult result = SceneNavigation.Build(scene, settings, agentType, out LELEngine.Mathematics.Quaternion rotation);
				results.Add(result);
				navMeshes.Add(new SceneNavMesh { AgentTypeId = agentType.Id, Rotation = rotation, Data = result.Data });
			}

			SceneNavMeshAsset asset = settings.NavMesh;
			string path = asset?.AssetPath;
			if (asset == null || string.IsNullOrEmpty(path))
			{
				string sceneFolder = Path.GetDirectoryName(scene.Path)?.Replace('\\', '/') ?? "";
				string folder = CombineAsset(sceneFolder, Path.GetFileNameWithoutExtension(scene.Path));
				Directory.CreateDirectory(AssetDatabase.ToAbsolute(folder));
				path = CombineAsset(folder, "NavMesh" + ScriptableObjectAssets.Extension);
				SceneNavMeshAsset existing = File.Exists(AssetDatabase.ToAbsolute(path)) && ScriptableObjectAssets.IsOfType(path, typeof(SceneNavMeshAsset))
					? ScriptableObjectAssets.Load(path) as SceneNavMeshAsset
					: null;
				asset = existing ?? ScriptableObject.CreateInstance<SceneNavMeshAsset>();
				if (existing == null)
				{
					path = UniqueAssetPath(path);
				}
			}

			asset.NavMeshes = navMeshes;
			ScriptableObjectAssets.Save(asset, path);
			if (!ReferenceEquals(settings.NavMesh, asset))
			{
				// The scene names its navmeshes from now on (an undoable scene edit; save the scene).
				Edits.Begin(scene.Settings, null);
				settings.NavMesh = asset;
				Edits.RequestCommit();
				MarkDirty();
			}

			SceneNavigation.Sync(scene);
			LastNavigationBake = results;
			AssetDatabase.Refresh(true);
			NotifyAssetsChanged();
			NotifySceneChanged();
			Debug.Log(DescribeBake(scene, path, results));
			return results;
		}

		/// <summary>Opens Window > Navigation in front (its Areas: where an area picker's Add Area... leads).</summary>
		public void ShowNavigationWindow()
		{
			navigationPanel.Open = true;
			ImGui.SetWindowFocus("Navigation");
		}

		/// <summary>Empties the open scene's navmesh asset (the file stays, holding none): no navmesh of the scene is left in the world.</summary>
		public void ClearSceneNavigation()
		{
			Scene scene = Scene;
			SceneNavMeshAsset asset = scene?.Settings.Navigation?.NavMesh;
			if (asset == null)
			{
				return;
			}

			if (IsPlaying)
			{
				throw new InvalidOperationException("Stop the game first: clearing writes the scene's navmesh asset.");
			}

			asset.NavMeshes = new List<SceneNavMesh>();
			if (!string.IsNullOrEmpty(asset.AssetPath))
			{
				ScriptableObjectAssets.Save(asset);
			}

			SceneNavigation.Sync(scene);
			LastNavigationBake = Array.Empty<SceneNavMeshBakeResult>();
			NotifySceneChanged();
			Debug.Log($"[Navigation] {scene.Name}: navmeshes cleared ({asset.AssetPath})");
		}

		#endregion

		#region PrivateMethods

		private static string DescribeBake(Scene scene, string path, IReadOnlyList<SceneNavMeshBakeResult> results)
		{
			var text = new StringBuilder();
			text.Append("[Navigation] ").Append(scene.Name).Append(" baked into ").Append(path).Append(':');
			foreach (SceneNavMeshBakeResult result in results)
			{
				text.Append("\n  ").Append(result.AgentName).Append(" (id ").Append(result.AgentTypeId).Append("): ");
				if (result.Problem != null)
				{
					text.Append("nothing (").Append(result.Problem).Append(')');
				}
				else
				{
					text.Append(result.Data.PolygonCount).Append(" polygons in ").Append(result.Data.Tiles.Length).Append(" tiles, ")
						.Append(result.Data.DataSize / 1024).Append(" KB, ").Append(result.Obstacles).Append(" obstacle pieces");
				}

				text.Append(", ").Append(result.Milliseconds.ToString("F0")).Append(" ms");
			}

			return text.ToString();
		}

		#endregion
	}
}
