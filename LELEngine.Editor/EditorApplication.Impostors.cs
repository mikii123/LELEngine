using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Rendering.Impostors;

namespace LELEngine.Editor
{
	/// <summary>
	///     Impostors in the editor: an impostor asset made of a prefab or a model (Create Impostor on the file, Assets >
	///     Create Impostor), baked into files (<see cref="ImpostorAssets" />; the asset's inspector, Assets > Bake
	///     Impostor / Bake All Impostors) and added to a LOD group as its last level (the group's inspector).
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PublicMethods

		/// <summary>The project file an impostor can be made of: a prefab or a model.</summary>
		public static bool IsImpostorSource(string assetPath)
		{
			return !string.IsNullOrEmpty(assetPath) && !AssetDatabase.IsEngineResource(assetPath)
			       && (assetPath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase) || ModelAssets.IsModelFile(assetPath));
		}

		/// <summary>The impostor asset at a path (loaded when it is not), or null when the file is no impostor.</summary>
		public static Impostor LoadImpostor(string assetPath)
		{
			return ScriptableObjectAssets.IsAssetFile(assetPath) && ScriptableObjectAssets.IsOfType(assetPath, typeof(Impostor))
				? ScriptableObjectAssets.Load(assetPath) as Impostor
				: null;
		}

		/// <summary>
		///     Makes an impostor asset of a prefab or model file next to it ("Name Impostor.asset"), bakes it with the default
		///     settings and selects it. The asset is made even when the bake has nothing to draw (the reason is logged);
		///     returns its path. Throws <see cref="InvalidOperationException" /> when the file is no source or the game plays.
		/// </summary>
		public string CreateImpostor(string sourcePath)
		{
			if (!IsImpostorSource(sourcePath) || !File.Exists(AssetDatabase.ToAbsolute(sourcePath)))
			{
				throw new InvalidOperationException("An impostor is made of a prefab or a model file of the project" + (string.IsNullOrEmpty(sourcePath) ? "." : ", not of " + sourcePath + "."));
			}

			if (IsPlaying)
			{
				throw new InvalidOperationException("Stop the game first: a bake writes assets.");
			}

			Edits.Commit();
			Impostor impostor = ScriptableObject.CreateInstance<Impostor>();
			if (sourcePath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
			{
				impostor.SourcePrefab = Prefab.Load(sourcePath);
			}
			else
			{
				impostor.SourceModel = ModelAssets.Load(sourcePath);
			}

			string folder = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "";
			string path = UniqueAssetPath(CombineAsset(folder, Path.GetFileNameWithoutExtension(sourcePath) + " Impostor" + ScriptableObjectAssets.Extension));
			ScriptableObjectAssets.Save(impostor, path);
			try
			{
				ImpostorAssets.Bake(impostor, this);
			}
			catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IOException)
			{
				Debug.LogWarning($"[Impostor] {path} was made but not baked: {e.Message}");
			}

			AssetDatabase.Refresh(true);
			NotifyAssetsChanged();
			SelectAsset(path);
			projectPanel?.Ping(path);
			return path;
		}

		/// <summary>
		///     Bakes an impostor asset into its files (<see cref="ImpostorAssets.Bake" />). Throws with the reason when it
		///     cannot (no source, nothing to draw, the game plays).
		/// </summary>
		public ImpostorBake BakeImpostor(Impostor impostor)
		{
			Edits.Commit();
			return ImpostorAssets.Bake(impostor, this);
		}

		/// <summary>Bakes every impostor asset of the project that has a source; the ones that fail are logged. Returns how many were baked.</summary>
		public int BakeAllImpostors()
		{
			if (IsPlaying)
			{
				throw new InvalidOperationException("Stop the game first: a bake writes assets.");
			}

			Edits.Commit();
			int baked = 0;
			List<string> paths = ImpostorAssets.FindAll();
			foreach (string path in paths)
			{
				Impostor impostor = LoadImpostor(path);
				if (impostor == null || (impostor.SourcePrefab == null && impostor.SourceModel == null))
				{
					Debug.LogWarning($"[Impostor] {path} has no source: not baked.");
					continue;
				}

				try
				{
					ImpostorAssets.Bake(impostor, this);
					baked++;
				}
				catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IOException)
				{
					Debug.LogError($"[Impostor] {path} was not baked: {e.Message}");
				}
			}

			Debug.Log($"[Impostor] Baked {baked} of {paths.Count} impostor assets.");
			return baked;
		}

		/// <summary>
		///     Adds a baked impostor to an object's LOD group as its last level, as one undo step
		///     (<see cref="Impostor.AddToLODGroup" />: a child object draws it below the last level's height). An object
		///     without a group gets one, its own renderers the first level. Returns the new child. Throws
		///     <see cref="InvalidOperationException" /> when the impostor is not baked or the object cannot be edited.
		/// </summary>
		public GameObject AddImpostorLevel(GameObject target, Impostor impostor)
		{
			if (target == null || impostor == null || Scene == null || target.scene != Scene)
			{
				throw new InvalidOperationException("An impostor level is added to an object of the open scene.");
			}

			if (!impostor.IsBaked)
			{
				throw new InvalidOperationException($"{impostor.name} is not baked yet: bake it first.");
			}

			TempObjects.RequireEditable(target);
			GameObject child = null;
			RecordStructural("Add Impostor Level " + impostor.name, () =>
			{
				LODGroup group = target.GetComponent<LODGroup>() ?? target.AddComponent<LODGroup>();
				MeshRenderer renderer = impostor.AddToLODGroup(group);
				child = renderer != null ? renderer.gameObject : null;
			});
			return child;
		}

		#endregion

		#region PrivateMethods

		// The impostor the Assets menu's items act on: the selected asset.
		private Impostor SelectedImpostor()
		{
			return SelectedAsset != null ? LoadImpostor(SelectedAsset) : null;
		}

		private void AddImpostorActions(EditorActions all)
		{
			all.Add(new EditorAction("Assets/Create Impostor", () => CreateImpostor(SelectedAsset))
			{
				Enabled = () => !IsPlaying && IsImpostorSource(SelectedAsset), SeparatorBefore = true
			});
			all.Add(new EditorAction("Assets/Bake Impostor", () =>
			{
				Impostor impostor = SelectedImpostor() ?? throw new InvalidOperationException("Select an impostor asset first.");
				BakeImpostor(impostor);
			})
			{
				Enabled = () => !IsPlaying && SelectedAsset != null && ScriptableObjectAssets.IsAssetFile(SelectedAsset) && ScriptableObjectAssets.IsOfType(SelectedAsset, typeof(Impostor))
			});
			all.Add(new EditorAction("Assets/Bake All Impostors", () => BakeAllImpostors()) { Enabled = () => !IsPlaying });
		}

		#endregion
	}
}
