using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>A prefab open for editing (prefab mode): its file and how it is saved.</summary>
	internal sealed class PrefabStage
	{
		public string AssetPath;
		public string Name;

		/// <summary>Changes are written to the file as they are made (as in Unity's prefab mode).</summary>
		public bool AutoSave = true;
	}

	/// <summary>What a prefab stage replaced (the scene, or the stage of the prefab it was opened from), restored when it closes.</summary>
	internal sealed class StageFrame
	{
		public PrefabStage Stage;
		public byte[] Snapshot;
		public string Path;
		public string Name;
		public bool Dirty;
		public ulong Selection;
		public (List<UndoRecord> undo, List<UndoRecord> redo) Undo;
	}

	internal sealed partial class EditorApplication
	{
		#region PublicFields

		/// <summary>The prefab being edited (prefab mode), or null when the scene is.</summary>
		public PrefabStage Stage => stageFrames.Count > 0 ? stageFrames[stageFrames.Count - 1].Stage : null;

		/// <summary>The open prefab stages, outermost first (the breadcrumb: the scene, then each prefab).</summary>
		public IReadOnlyList<StageFrame> StageFrames => stageFrames;

		/// <summary>The edited prefab's root object (prefab mode), or null.</summary>
		public GameObject StageRoot
		{
			get
			{
				if (Stage == null || Scene == null)
				{
					return null;
				}

				foreach (GameObject root in Scene.GetRootGameObjects())
				{
					if (!root.dead && (root.hideFlags & HideFlags.DontSave) == 0)
					{
						return root;
					}
				}

				return null;
			}
		}

		#endregion

		#region PrivateFields

		private readonly List<StageFrame> stageFrames = new List<StageFrame>();

		/// <summary>The stage with unsaved changes asks before it closes; what runs after the answer.</summary>
		private bool openStagePopup;

		private Action pendingAfterStage;

		/// <summary>An object dropped on the project panel that is a prefab instance's root: the user picks original or variant.</summary>
		private GameObject pendingPrefabObject;

		private string pendingPrefabFolder;
		private bool openCreatePrefabPopup;
		private double lastEditTime;
		private readonly List<Action> deferred = new List<Action>();

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- prefab mode

		/// <summary>
		///     Opens a prefab for editing in place of the scene (prefab mode): its objects are a scene of their own, prefabs
		///     nested in it (and a variant's base) are instances. The scene comes back when the stage closes. Opening another
		///     prefab from a stage stacks a stage on it (the stage below is saved first).
		/// </summary>
		public void OpenPrefab(string assetPath)
		{
			if (IsPlaying)
			{
				Debug.LogWarning("[Editor] Exit play mode to edit a prefab.");
				return;
			}

			assetPath = assetPath?.Replace('\\', '/');
			if (string.IsNullOrEmpty(assetPath) || !File.Exists(AssetDatabase.ToAbsolute(assetPath)) || Scene == null)
			{
				return;
			}

			int open = stageFrames.FindIndex(frame => string.Equals(frame.Stage.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase));
			if (open >= 0)
			{
				// Already open further down the breadcrumb: go back to it.
				LeaveStagesAbove(open, null);
				return;
			}

			Edits.Commit();
			if (Stage != null && SceneDirty)
			{
				SavePrefabStage();
			}

			Scene stage;
			try
			{
				stage = SceneSerializer.Load(AssetDatabase.ToAbsolute(assetPath));
			}
			catch (Exception e)
			{
				Debug.LogError($"[Editor] Cannot open the prefab {assetPath}: {e.Message}");
				return;
			}

			stageFrames.Add(new StageFrame
			{
				Stage = new PrefabStage { AssetPath = assetPath, Name = Path.GetFileNameWithoutExtension(assetPath), AutoSave = Stage?.AutoSave ?? true },
				Snapshot = SceneSerializer.SaveToBytes(Scene),
				Path = Scene.Path,
				Name = Scene.Name,
				Dirty = SceneDirty,
				Selection = Selected?.Id ?? 0,
				Undo = Undo.Take()
			});

			stage.Path = null;
			stage.Name = Path.GetFileNameWithoutExtension(assetPath);
			LELEngine.Audio.AudioSystem.StopAll();
			ReplaceScene(stage, false);
			SceneDirty = false;
			Selected = StageRoot;
			if (Selected != null)
			{
				SceneView.FrameObject(Selected);
			}

			Debug.Log("[Editor] Editing prefab " + assetPath);
		}

		/// <summary>Writes the edited prefab to its file (prefab mode).</summary>
		public void SavePrefabStage()
		{
			PrefabStage stage = Stage;
			if (stage == null || Scene == null)
			{
				return;
			}

			Edits.Commit();
			try
			{
				PrefabUtility.SavePrefabScene(Scene, stage.AssetPath);
			}
			catch (Exception e)
			{
				Debug.LogError($"[Editor] Cannot save the prefab {stage.AssetPath}: {e.Message}");
				return;
			}

			SceneDirty = false;
			NotifyAssetsChanged();
			if (!stage.AutoSave)
			{
				Debug.Log("[Editor] Saved prefab " + stage.AssetPath);
			}
		}

		/// <summary>Closes the top prefab stage (asking about unsaved changes when it does not save automatically).</summary>
		public void ClosePrefabStage()
		{
			ConfirmLeaveStage(ClosePrefabStageNow);
		}

		/// <summary>Closes every prefab stage (back to the scene), then runs <paramref name="continuation" />.</summary>
		public void LeaveAllStages(Action continuation)
		{
			LeaveStagesAbove(-1, continuation);
		}

		/// <summary>Goes back to a level of the breadcrumb: -1 the scene, else the stage at that index.</summary>
		public void GoToStage(int index)
		{
			LeaveStagesAbove(index, null);
		}

		/// <summary>The project panel's current folder (where "Save as Prefab" puts new prefabs).</summary>
		public string ProjectFolder => projectPanel.Folder;

		/// <summary>
		///     Runs an action after every panel of the frame is drawn: operations that reload the scene (prefab apply, prefab
		///     mode) chosen in a menu must not replace the objects a panel is still drawing.
		/// </summary>
		public void Defer(Action action)
		{
			if (action != null)
			{
				deferred.Add(action);
			}
		}

		// ---------------------------------------------------------------- prefab operations (undoable)

		/// <summary>Creates an instance of a prefab file as one undo step (at the scene view's spawn point or under <paramref name="parent" />) and selects it.</summary>
		public GameObject InstantiatePrefab(string assetPath, Transform parent = null, Vector3? position = null)
		{
			Prefab prefab = Prefab.Load(assetPath);
			if (prefab == null || Scene == null)
			{
				return null;
			}

			if (Stage != null && PrefabCache.DependsOn(assetPath, Stage.AssetPath))
			{
				Debug.LogError($"[Editor] '{prefab.name}' cannot go into the prefab '{Stage.Name}': it contains it (a prefab cannot contain itself).");
				return null;
			}

			parent ??= StageRoot?.transform;
			GameObject root = null;
			RecordStructural("Instantiate " + prefab.name, () =>
			{
				root = prefab.Instantiate(Scene, parent);
				if (root != null && parent == null)
				{
					root.transform.position = position ?? SceneView.DefaultSpawnPoint();
				}
				else if (root != null && position.HasValue)
				{
					root.transform.position = position.Value;
				}

				Selected = root;
			});
			return root;
		}

		/// <summary>
		///     Saves a scene object (with its descendants) as a prefab in a project folder; the object becomes its instance.
		///     A prefab instance's root asks whether the new prefab is an original or a variant of its prefab
		///     (<paramref name="variant" /> null), as in Unity.
		/// </summary>
		public void CreatePrefab(GameObject go, string folder, bool? variant = null)
		{
			if (go == null || go.dead || Scene == null)
			{
				return;
			}

			if (go.prefabInstance != null && go.prefabInstance.Root != go)
			{
				Debug.LogError($"[Editor] '{go.Name}' is part of the prefab instance '{go.prefabInstance.Root?.Name}': unpack it first (or drag its root).");
				return;
			}

			if (variant == null && PrefabUtility.IsPrefabInstanceRoot(go) && !go.prefabInstance.IsMissing)
			{
				pendingPrefabObject = go;
				pendingPrefabFolder = folder;
				openCreatePrefabPopup = true;
				return;
			}

			if (Stage != null && go == StageRoot)
			{
				Debug.LogError("[Editor] The edited prefab's root cannot be saved as another prefab here; create a variant of the prefab instead.");
				return;
			}

			string path = UniqueAssetPath(CombineAsset(folder, SafeFileName(go.Name) + Prefab.Extension));
			bool asVariant = variant == true;
			RecordPrefabFiles((asVariant ? "Create Prefab Variant " : "Create Prefab ") + Path.GetFileNameWithoutExtension(path), new[] { path }, () =>
			{
				if (PrefabUtility.IsPrefabInstanceRoot(go) && !asVariant)
				{
					PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot);
				}

				PrefabUtility.SaveAsPrefabAsset(go, path);
			});
			projectPanel.Ping(path);
			Debug.Log($"[Editor] Created {(asVariant ? "prefab variant" : "prefab")} {path}");
		}

		/// <summary>Creates a variant of a prefab file next to it (one undo step).</summary>
		public string CreatePrefabVariant(string baseAssetPath)
		{
			Prefab basePrefab = Prefab.Load(baseAssetPath);
			if (basePrefab == null)
			{
				return null;
			}

			string folder = Path.GetDirectoryName(baseAssetPath)?.Replace('\\', '/') ?? "";
			string path = UniqueAssetPath(CombineAsset(folder, basePrefab.name + " Variant" + Prefab.Extension));
			RecordPrefabFiles("Create Prefab Variant " + Path.GetFileNameWithoutExtension(path), new[] { path }, () => PrefabUtility.CreateVariant(basePrefab, path));
			SelectAsset(path);
			projectPanel.Ping(path);
			return path;
		}

		/// <summary>Creates an empty prefab (a root with a Transform) in a folder.</summary>
		public string CreateEmptyPrefab(string folder, string name)
		{
			string path = UniqueAssetPath(CombineAsset(folder, SafeFileName(name) + Prefab.Extension));
			var scene = new Scene("Prefab");
			GameObject root = scene.CreateGameObject(Path.GetFileNameWithoutExtension(path));
			RecordPrefabFiles("Create Prefab " + root.Name, new[] { path }, () => PrefabUtility.SaveAsPrefabAsset(root, path, false));
			return path;
		}

		/// <summary>Breaks an instance's link to its prefab (one undo step).</summary>
		public void UnpackPrefab(GameObject root, PrefabUnpackMode mode)
		{
			if (!PrefabUtility.IsPrefabInstanceRoot(root))
			{
				return;
			}

			RecordStructural((mode == PrefabUnpackMode.Completely ? "Unpack Completely " : "Unpack ") + root.Name, () => PrefabUtility.UnpackPrefabInstance(root, mode));
		}

		/// <summary>
		///     Runs an operation that writes prefab files as one undo step: the files (and their meta files) before and
		///     after, the scene before and after; then the scene is loaded again so every instance shows the files' state.
		/// </summary>
		public void RecordPrefabFiles(string name, IEnumerable<string> prefabPaths, Action operation)
		{
			if (Scene == null)
			{
				return;
			}

			Edits.Commit();
			var record = new PrefabFilesRecord { Name = name, SceneBefore = SceneSerializer.SaveToBytes(Scene), SelectionBefore = Selected?.Id ?? 0 };
			var files = new List<string>();
			foreach (string path in prefabPaths)
			{
				if (path != null)
				{
					files.Add(path);
					files.Add(path + AssetDatabase.MetaExtension);
				}
			}

			var before = new List<byte[]>();
			foreach (string path in files)
			{
				before.Add(ReadIfExists(path));
			}

			try
			{
				operation();
			}
			catch (Exception e)
			{
				Debug.LogError("[Editor] " + name + " failed: " + e.Message);
				Debug.LogException(e);
			}

			record.SceneAfter = SceneSerializer.SaveToBytes(Scene);
			record.SelectionAfter = Selected?.Id ?? 0;
			for (int i = 0; i < files.Count; i++)
			{
				record.Files.Add((files[i], before[i], ReadIfExists(files[i])));
			}

			var prefabs = new List<string>(prefabPaths);
			PrefabCache.Invalidate(prefabs);
			RestoreSnapshot(record.SceneAfter, record.SelectionAfter);
			Undo.Push(record);
			NotifyAssetsChanged();
		}

		#endregion

		#region PrivateMethods

		private void RunDeferred()
		{
			if (deferred.Count == 0)
			{
				return;
			}

			var actions = new List<Action>(deferred);
			deferred.Clear();
			foreach (Action action in actions)
			{
				try
				{
					action();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}

		private void ClosePrefabStageNow()
		{
			if (stageFrames.Count == 0)
			{
				return;
			}

			StageFrame frame = stageFrames[stageFrames.Count - 1];
			stageFrames.RemoveAt(stageFrames.Count - 1);
			Edits.Cancel();
			LELEngine.Audio.AudioSystem.StopAll();
			Scene restored = SceneSerializer.LoadFromBytes(frame.Snapshot, frame.Name);
			restored.Path = frame.Path;
			restored.Name = frame.Name;
			ReplaceScene(restored, false);
			SceneDirty = frame.Dirty;
			Undo.Restore(frame.Undo);
			Selected = frame.Selection != 0 ? restored.FindObjectById(frame.Selection) : null;
			if (Selected != null)
			{
				hierarchy.Ping(Selected);
			}
		}

		/// <summary>Closes the stages above index <paramref name="keep" /> (-1: all), saving or asking as each one needs.</summary>
		private void LeaveStagesAbove(int keep, Action continuation)
		{
			if (stageFrames.Count - 1 <= keep)
			{
				continuation?.Invoke();
				return;
			}

			ConfirmLeaveStage(() =>
			{
				ClosePrefabStageNow();
				LeaveStagesAbove(keep, continuation);
			});
		}

		/// <summary>Runs <paramref name="continuation" /> once the top stage's changes are saved (automatically, or as the user answers).</summary>
		private void ConfirmLeaveStage(Action continuation)
		{
			if (Stage == null)
			{
				continuation();
				return;
			}

			if (!SceneDirty)
			{
				continuation();
				return;
			}

			if (Stage.AutoSave)
			{
				SavePrefabStage();
				continuation();
				return;
			}

			pendingAfterStage = continuation;
			openStagePopup = true;
		}

		/// <summary>Prefab mode, every frame: automatic saving once an edit settles (no button held, nothing being typed).</summary>
		private void UpdatePrefabStage()
		{
			PrefabStage stage = Stage;
			if (stage == null || !stage.AutoSave || !SceneDirty || IsPlaying)
			{
				return;
			}

			ImGuiIOPtr io = ImGui.GetIO();
			if (ImGui.IsAnyItemActive() || io.MouseDown[0] || io.MouseDown[1] || Time.timeD - lastEditTime < 0.35)
			{
				return;
			}

			SavePrefabStage();
		}

		/// <summary>Prefab files changed outside the editor (git, a text editor): the instances of the scene follow.</summary>
		private void RefreshPrefabsIfChanged()
		{
			List<string> changed = PrefabCache.ReloadChanged();
			if (changed.Count == 0 || Scene == null || IsPlaying)
			{
				return;
			}

			bool used = false;
			foreach (GameObject go in Scene.GameObjects)
			{
				PrefabInstance instance = go.prefabInstance;
				if (instance != null && instance.Root == go)
				{
					foreach (string path in changed)
					{
						if (PrefabCache.DependsOn(instance.PrefabPath, path))
						{
							used = true;
							break;
						}
					}
				}

				if (used)
				{
					break;
				}
			}

			if (used)
			{
				bool dirty = SceneDirty;
				ReloadScene(() => { });
				SceneDirty = dirty;
				Debug.Log("[Editor] Prefabs changed on disk: " + string.Join(", ", changed) + "; their instances were rebuilt");
			}
		}

		private void DrawPrefabModals()
		{
			if (openStagePopup)
			{
				ImGui.OpenPopup("Unsaved prefab");
				openStagePopup = false;
			}

			if (openCreatePrefabPopup)
			{
				ImGui.OpenPopup("Create Prefab");
				openCreatePrefabPopup = false;
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Unsaved prefab", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text($"The prefab '{Stage?.Name}' has unsaved changes.");
				ImGui.Spacing();
				if (EditorGui.Button("Save", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					SavePrefabStage();
					Action next = pendingAfterStage;
					pendingAfterStage = null;
					next?.Invoke();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Don't save", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					SceneDirty = false;
					Action next = pendingAfterStage;
					pendingAfterStage = null;
					next?.Invoke();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					pendingAfterStage = null;
				}

				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Create Prefab", ImGuiWindowFlags.AlwaysAutoResize))
			{
				GameObject go = pendingPrefabObject;
				if (go == null || go.dead)
				{
					ImGui.CloseCurrentPopup();
				}
				else
				{
					ImGui.Text($"'{go.Name}' is an instance of the prefab '{go.prefabInstance?.Prefab?.name}'.");
					ImGui.Text("Create a new original prefab, or a variant of that prefab?");
					ImGui.Spacing();
					if (EditorGui.Button("Original Prefab", new NVector2(140, 0)))
					{
						ImGui.CloseCurrentPopup();
						string folder = pendingPrefabFolder;
						Defer(() => CreatePrefab(go, folder, false));
						pendingPrefabObject = null;
					}

					TestAnchors.RecordItem("button:OriginalPrefab");

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("A prefab of its own: the instance is unpacked first (prefabs nested in it stay nested).");
					}

					ImGui.SameLine();
					if (EditorGui.Button("Prefab Variant", new NVector2(140, 0)))
					{
						ImGui.CloseCurrentPopup();
						string folder = pendingPrefabFolder;
						Defer(() => CreatePrefab(go, folder, true));
						pendingPrefabObject = null;
					}

					TestAnchors.RecordItem("button:PrefabVariant");

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Keeps everything of its prefab and stores only what this instance changes (its overrides).");
					}

					ImGui.SameLine();
					if (EditorGui.Button("Cancel", new NVector2(100, 0)))
					{
						ImGui.CloseCurrentPopup();
						pendingPrefabObject = null;
					}
				}

				ImGui.EndPopup();
			}
		}

		private static byte[] ReadIfExists(string assetPath)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			return File.Exists(file) ? File.ReadAllBytes(file) : null;
		}

		private static string CombineAsset(string folder, string file)
		{
			return string.IsNullOrEmpty(folder) ? file : folder.TrimEnd('/') + "/" + file;
		}

		/// <summary>The path, or with " 1", " 2"... before the extension when a file has it.</summary>
		private static string UniqueAssetPath(string assetPath)
		{
			if (!File.Exists(AssetDatabase.ToAbsolute(assetPath)))
			{
				return assetPath;
			}

			string extension = Path.GetExtension(assetPath);
			string stem = assetPath.Substring(0, assetPath.Length - extension.Length);
			for (int i = 1; ; i++)
			{
				string candidate = stem + " " + i + extension;
				if (!File.Exists(AssetDatabase.ToAbsolute(candidate)))
				{
					return candidate;
				}
			}
		}

		private static string SafeFileName(string name)
		{
			foreach (char c in Path.GetInvalidFileNameChars())
			{
				name = name.Replace(c, '_');
			}

			return string.IsNullOrWhiteSpace(name) ? "Prefab" : name.Trim();
		}

		#endregion
	}
}
