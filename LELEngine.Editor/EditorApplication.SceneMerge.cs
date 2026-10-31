using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     A merge of the open scene with its file changed on disk, waiting for the user to settle its conflicts
	///     ("Scene changed on disk"): the four documents of <see cref="SceneMerge" /> and the file as read.
	/// </summary>
	internal sealed class PendingSceneMerge
	{
		public string Path;
		public SceneDocument MineBase;
		public SceneDocument Mine;
		public SceneDocument TheirsBase;
		public SceneDocument Theirs;
		public byte[] TheirsBytes;
		public List<SceneMergeConflict> Conflicts;

		/// <summary>The side the user keeps per conflict (by <see cref="SceneMergeConflict.Key" />).</summary>
		public readonly Dictionary<string, MergeSide> Choices = new Dictionary<string, MergeSide>(StringComparer.Ordinal);
	}

	/// <summary>
	///     The open scene's file changed outside the editor (git, a text editor, another tool): with no unsaved changes the
	///     scene reloads; with unsaved changes the file's changes merge into the scene value by value
	///     (<see cref="SceneMerge" />: the editor's changes are measured from the file as it read or wrote it last, the
	///     file's from the same bytes), and only values both changed differently ask the user. The check waits for play
	///     mode, prefab mode and script builds (a checkout changing scripts and the scene loads the scene with the new
	///     scripts). A script reload rebuilds the scene from its file the same way, so data the old scripts could not read
	///     (a field renamed in the new ones) is not lost.
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PrivateFields

		/// <summary>The open scene's file as the editor read or wrote it last (null: a new scene, never saved).</summary>
		private byte[] sceneFileBase;

		/// <summary>A merge with conflicts the user settles in the "Scene changed on disk" popup.</summary>
		private PendingSceneMerge pendingMerge;

		#endregion

		#region PrivateMethods

		/// <summary>The edit scene's path: the scene played in play mode, the one below the prefab stages in prefab mode.</summary>
		private string EditScenePath => IsPlaying ? playScenePath : stageFrames.Count > 0 ? stageFrames[0].Path : Scene?.Path;

		/// <summary>The open scene's file is not the one the editor read or wrote last (git, Explorer): note it for merging.</summary>
		private void CheckSceneOnDisk()
		{
			string path = EditScenePath;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			string file = AssetDatabase.ToAbsolute(path);
			if (!File.Exists(file))
			{
				return;
			}

			FileStamp stamp = FileStamp.Of(file);
			if (stamp != sceneStamp)
			{
				sceneStamp = stamp;
				sceneChangedOnDisk = true;
			}
		}

		/// <summary>
		///     Brings in the open scene's file changed on disk: reloaded without unsaved changes (keeping the selection), else
		///     merged (conflicts: the user chooses). Waits for play mode, prefab mode, script builds and a merge being settled.
		/// </summary>
		private void ReloadSceneIfChangedOnDisk()
		{
			if (!sceneChangedOnDisk || IsPlaying || Stage != null || pendingMerge != null || (Scripts != null && !Scripts.IsIdle))
			{
				return;
			}

			sceneChangedOnDisk = false;
			string path = Scene?.Path;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			string file = AssetDatabase.ToAbsolute(path);
			byte[] theirs;
			try
			{
				theirs = File.ReadAllBytes(file);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// Being written (or deleted): the next write is noticed again.
				return;
			}

			if (sceneFileBase != null && theirs.AsSpan().SequenceEqual(sceneFileBase))
			{
				// Touched only (a checkout writing the same bytes).
				return;
			}

			if (!SceneDirty)
			{
				ReloadSceneFromDisk();
				return;
			}

			PendingSceneMerge merge;
			try
			{
				merge = PrepareMerge(path, theirs, SceneSerializer.SaveToBytes(Scene), sceneFileBase != null ? NormalizedScene(sceneFileBase) : null);
			}
			catch (Exception e)
			{
				// Half written, or not a scene any more: the next write is noticed again.
				Debug.LogWarning($"[Editor] {path} changed on disk but cannot be read ({e.Message}); the scene stays as it is here.");
				return;
			}

			SceneMergeResult result = Merge(merge, null);
			if (merge.MineBase == null || result.Conflicts.Count > 0)
			{
				if (HasUI)
				{
					OfferMerge(merge, result);
					return;
				}

				LogConflictsKept(merge, result);
				if (merge.MineBase == null)
				{
					sceneFileBase = merge.TheirsBytes;
					return;
				}

				result = Merge(merge, c => MergeSide.Mine);
			}

			ApplyMerge(merge, result);
		}

		private void ReloadSceneFromDisk()
		{
			string path = Scene?.Path;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			ulong selected = Selected?.Id ?? 0;
			OpenSceneNow(path);
			GameObject reselected = selected != 0 ? Scene?.FindObjectById(selected) : null;
			if (reselected != null)
			{
				Select(reselected);
			}

			Debug.Log("[Editor] Reloaded " + path + " (changed on disk)");
		}

		/// <summary>
		///     The documents of a merge of the scene's file <paramref name="theirs" /> with the editor's scene
		///     <paramref name="mine" /> (bytes of the live scene); <paramref name="mineBase" /> is the file the editor read
		///     last as the same scripts write it (null when there is none: the user can only reload or keep the scene).
		/// </summary>
		private PendingSceneMerge PrepareMerge(string path, byte[] theirs, byte[] mine, byte[] mineBase)
		{
			var merge = new PendingSceneMerge
			{
				Path = path,
				Theirs = SceneSerializer.ReadDocument(theirs),
				TheirsBytes = theirs,
				Mine = SceneSerializer.ReadDocument(mine)
			};

			if (mineBase != null && sceneFileBase != null)
			{
				merge.MineBase = SceneSerializer.ReadDocument(mineBase);
				merge.TheirsBase = SceneSerializer.ReadDocument(sceneFileBase);
			}

			return merge;
		}

		/// <summary>A scene document as the loaded scripts write it (fields they lack dropped, fields they add at their defaults).</summary>
		private static byte[] NormalizedScene(byte[] data)
		{
			Scene scene = SceneSerializer.LoadFromBytes(data);
			try
			{
				return SceneSerializer.SaveToBytes(scene);
			}
			finally
			{
				scene.Unload();
			}
		}

		private static SceneMergeResult Merge(PendingSceneMerge merge, Func<SceneMergeConflict, MergeSide> resolve)
		{
			if (merge.MineBase == null)
			{
				return new SceneMergeResult { Document = merge.Mine };
			}

			return SceneMerge.Merge(merge.MineBase, merge.Mine, merge.TheirsBase, merge.Theirs, resolve);
		}

		/// <summary>Conflicts: the "Scene changed on disk" popup asks, the editor's values chosen to begin with.</summary>
		private void OfferMerge(PendingSceneMerge merge, SceneMergeResult result)
		{
			merge.Conflicts = result.Conflicts;
			foreach (SceneMergeConflict conflict in result.Conflicts)
			{
				merge.Choices[conflict.Key] = MergeSide.Mine;
			}

			pendingMerge = merge;
			openSceneChangedPopup = true;
		}

		/// <summary>Batch mode has nobody to ask: the editor's values are kept, and listed.</summary>
		private static void LogConflictsKept(PendingSceneMerge merge, SceneMergeResult result)
		{
			if (merge.MineBase == null)
			{
				Debug.LogWarning($"[Editor] {merge.Path} changed on disk; its unsaved changes here are kept (saving overwrites the file).");
			}

			foreach (SceneMergeConflict conflict in result.Conflicts)
			{
				Debug.LogWarning($"[Editor] {merge.Path} changed on disk: {conflict} (kept here)");
			}
		}

		/// <summary>
		///     Loads the merged scene in place of the open one (one undo step back to the scene as it was); the file read
		///     is the new base, and the scene is dirty when it keeps changes of its own.
		/// </summary>
		private void ApplyMerge(PendingSceneMerge merge, SceneMergeResult result)
		{
			Edits.Commit();
			byte[] before = SceneSerializer.SaveToBytes(Scene);
			ulong selection = Selected?.Id ?? 0;
			Scene merged = LoadMerged(merge, result);
			LELEngine.Audio.AudioSystem.StopAll();
			ReplaceScene(merged, false);
			sceneFileBase = merge.TheirsBytes;
			pendingMerge = null;
			SceneDirty = result.MineKept > 0;
			Undo.Push(new SceneSnapshotRecord { Name = "Merge Changes From Disk", Before = before, After = SceneSerializer.SaveToBytes(merged), SelectionBefore = selection, SelectionAfter = Selected?.Id ?? 0 });
			Lighting.GI.InvalidateStatic();
			string conflicts = result.Conflicts.Count > 0 ? $", {result.Conflicts.Count} conflict(s) settled" : "";
			Debug.Log($"[Editor] Merged {merge.Path} (changed on disk): {result.TheirsTaken} change(s) from the file, {result.MineKept} unsaved change(s) kept{conflicts}");
		}

		private static Scene LoadMerged(PendingSceneMerge merge, SceneMergeResult result)
		{
			// Nothing of the editor's left: the file itself.
			byte[] data = result.MineKept > 0 ? SceneSerializer.WriteDocument(result.Document, DocumentFormat.Binary) : merge.TheirsBytes;
			Scene scene = SceneSerializer.LoadFromBytes(data, Path.GetFileNameWithoutExtension(merge.Path));
			scene.Path = merge.Path;
			return scene;
		}

		/// <summary>
		///     A script reload (<see cref="ReloadSceneAround" />): the scene from its file with the editor's changes on top
		///     (<paramref name="mineBase" />: the file as the old scripts wrote it, taken before the reload), so what the old
		///     scripts dropped of the file comes back. Null when it cannot (no file, conflicts with changes on disk: the
		///     popup asks, the snapshot is loaded meanwhile).
		/// </summary>
		private Scene RebuildFromFile(string path, byte[] snapshot, byte[] mineBase)
		{
			string file = AssetDatabase.ToAbsolute(path);
			try
			{
				FileStamp stamp = FileStamp.Of(file);
				byte[] theirs = File.ReadAllBytes(file);
				bool changed = !theirs.AsSpan().SequenceEqual(sceneFileBase);
				if (!SceneDirty)
				{
					Scene scene = SceneSerializer.LoadFromBytes(theirs, Path.GetFileNameWithoutExtension(path));
					scene.Path = path;
					sceneFileBase = theirs;
					sceneStamp = stamp;
					sceneChangedOnDisk = false;
					if (changed)
					{
						Debug.Log("[Editor] Reloaded " + path + " (changed on disk)");
					}

					return scene;
				}

				PendingSceneMerge merge = PrepareMerge(path, theirs, snapshot, mineBase);
				SceneMergeResult result = Merge(merge, null);
				if (result.Conflicts.Count > 0)
				{
					sceneStamp = stamp;
					sceneChangedOnDisk = false;
					if (HasUI)
					{
						OfferMerge(merge, result);
						return null;
					}

					LogConflictsKept(merge, result);
					result = Merge(merge, c => MergeSide.Mine);
				}

				Scene merged = LoadMerged(merge, result);
				sceneFileBase = theirs;
				sceneStamp = stamp;
				sceneChangedOnDisk = false;
				SceneDirty = result.MineKept > 0;
				if (changed)
				{
					Debug.Log($"[Editor] Merged {path} (changed on disk): {result.TheirsTaken} change(s) from the file, {result.MineKept} unsaved change(s) kept");
				}

				return merged;
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Editor] Cannot rebuild {path} from its file after the script reload ({e.Message}); the scene comes back as it was.");
				return null;
			}
		}

		private void DrawSceneMergeModal()
		{
			if (openSceneChangedPopup)
			{
				ImGui.OpenPopup("Scene changed on disk");
				openSceneChangedPopup = false;
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (!ImGui.BeginPopupModal("Scene changed on disk", ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			PendingSceneMerge merge = pendingMerge;
			if (merge == null)
			{
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.Text($"The scene '{Scene?.Name}' was changed outside the editor (git, another program) while it has unsaved changes here.");
			if (merge.MineBase != null)
			{
				ImGui.Text($"Everything else merges; {merge.Conflicts.Count} value(s) were changed on both sides. Choose the one to keep:");
				ImGui.Spacing();
				DrawConflicts(merge);
			}
			else
			{
				ImGui.Text("The scene's file as the editor read it is not known: only reloading or keeping the scene is possible.");
			}

			ImGui.Spacing();
			if (merge.MineBase != null)
			{
				if (EditorGui.Button("Merge", new NVector2(140, 0)))
				{
					ImGui.CloseCurrentPopup();
					ApplyMerge(merge, Merge(merge, c => merge.Choices.TryGetValue(c.Key, out MergeSide side) ? side : MergeSide.Mine));
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Merge the file's changes into the scene, the conflicting values as chosen above; one undo step.");
				}

				ImGui.SameLine();
			}

			if (EditorGui.Button("Reload from disk", new NVector2(140, 0)))
			{
				ImGui.CloseCurrentPopup();
				pendingMerge = null;
				ReloadSceneFromDisk();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Load the file from disk; the changes made here are lost.");
			}

			ImGui.SameLine();
			if (EditorGui.Button("Keep mine", new NVector2(140, 0)))
			{
				ImGui.CloseCurrentPopup();
				// The file's version is turned down: the scene stays as it is here, and saving it overwrites the file.
				sceneFileBase = merge.TheirsBytes;
				pendingMerge = null;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Keep the scene as it is here, ignoring the file's changes; saving it overwrites the file on disk.");
			}

			ImGui.EndPopup();
		}

		private static void DrawConflicts(PendingSceneMerge merge)
		{
			if (EditorGui.Button("Keep all mine"))
			{
				SetAll(merge, MergeSide.Mine);
			}

			ImGui.SameLine();
			if (EditorGui.Button("Keep all from disk"))
			{
				SetAll(merge, MergeSide.Theirs);
			}

			float height = Math.Min(merge.Conflicts.Count * ImGui.GetFrameHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing() + 8f, 360f);
			ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
			if (!ImGui.BeginTable("##conflicts", 5, flags, new NVector2(860f, height)))
			{
				return;
			}

			ImGui.TableSetupScrollFreeze(0, 1);
			ImGui.TableSetupColumn("Object", ImGuiTableColumnFlags.WidthFixed, 200f);
			ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 220f);
			ImGui.TableSetupColumn("Here", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("On disk", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 130f);
			ImGui.TableHeadersRow();
			for (int i = 0; i < merge.Conflicts.Count; i++)
			{
				SceneMergeConflict conflict = merge.Conflicts[i];
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(conflict.Component != null ? conflict.ObjectName + " / " + conflict.Component : conflict.ObjectName);
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(conflict.What);
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(conflict.Mine);
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(conflict.Theirs);
				ImGui.TableNextColumn();
				MergeSide side = merge.Choices.TryGetValue(conflict.Key, out MergeSide chosen) ? chosen : MergeSide.Mine;
				if (EditorGui.RadioButton("Here##" + i, side == MergeSide.Mine))
				{
					merge.Choices[conflict.Key] = MergeSide.Mine;
				}

				ImGui.SameLine();
				if (EditorGui.RadioButton("Disk##" + i, side == MergeSide.Theirs))
				{
					merge.Choices[conflict.Key] = MergeSide.Theirs;
				}
			}

			ImGui.EndTable();
		}

		private static void SetAll(PendingSceneMerge merge, MergeSide side)
		{
			foreach (SceneMergeConflict conflict in merge.Conflicts)
			{
				merge.Choices[conflict.Key] = side;
			}
		}

		#endregion
	}
}
