using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>One undoable editor operation.</summary>
	internal abstract class UndoRecord
	{
		#region PublicFields

		public string Name;

		#endregion

		#region PublicMethods

		public abstract void Undo(EditorApplication editor);
		public abstract void Redo(EditorApplication editor);

		#endregion
	}

	/// <summary>Field values of one component, before and after an edit (inspector, gizmo).</summary>
	internal sealed class ComponentStateRecord : UndoRecord
	{
		#region PublicFields

		public ulong ComponentId;
		public byte[] Before;
		public byte[] After;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			Apply(editor, Before);
		}

		public override void Redo(EditorApplication editor)
		{
			Apply(editor, After);
		}

		#endregion

		#region PrivateMethods

		private void Apply(EditorApplication editor, byte[] data)
		{
			Behaviour component = editor.Scene?.FindComponentById(ComponentId);
			if (component == null)
			{
				return;
			}

			EditorSerialization.ApplyComponent(component, data);
			editor.NotifyChanged(component.gameObject);
		}

		#endregion
	}

	/// <summary>Name / active flag of a GameObject.</summary>
	internal sealed class GameObjectStateRecord : UndoRecord
	{
		#region PublicFields

		public ulong ObjectId;
		public string NameBefore;
		public string NameAfter;
		public bool ActiveBefore;
		public bool ActiveAfter;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			Apply(editor, NameBefore, ActiveBefore);
		}

		public override void Redo(EditorApplication editor)
		{
			Apply(editor, NameAfter, ActiveAfter);
		}

		#endregion

		#region PrivateMethods

		private void Apply(EditorApplication editor, string name, bool active)
		{
			GameObject go = editor.Scene?.FindObjectById(ObjectId);
			if (go == null)
			{
				return;
			}

			go.Name = name;
			go.SetActive(active);
			editor.NotifyChanged(go);
		}

		#endregion
	}

	/// <summary>Scene lighting settings (environment, shadow reach, the GI volume).</summary>
	internal sealed class SettingsRecord : UndoRecord
	{
		#region PublicFields

		public byte[] Before;
		public byte[] After;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			Apply(editor, Before);
		}

		public override void Redo(EditorApplication editor)
		{
			Apply(editor, After);
		}

		#endregion

		#region PrivateMethods

		private static void Apply(EditorApplication editor, byte[] data)
		{
			Scene scene = editor.Scene;
			if (scene == null)
			{
				return;
			}

			EditorSerialization.ApplyObject(scene.Settings, data, scene);
			Lighting.GI.InvalidateStatic();
			editor.MarkDirty();
		}

		#endregion
	}

	/// <summary>Project settings (physics, lighting quality), before and after an edit; applying also saves the file.</summary>
	internal sealed class ProjectSettingsRecord : UndoRecord
	{
		#region PublicFields

		public byte[] Before;
		public byte[] After;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			Apply(editor, Before);
		}

		public override void Redo(EditorApplication editor)
		{
			Apply(editor, After);
		}

		#endregion

		#region PrivateMethods

		private static void Apply(EditorApplication editor, byte[] data)
		{
			EditorSerialization.ApplyObject(ProjectSettings.Current, data, null);
			Lighting.GI.InvalidateStatic();
			editor.SaveProjectSettings();
		}

		#endregion
	}

	/// <summary>Values of a document asset (material, physics material), before and after an edit; applying also saves the file.</summary>
	internal sealed class AssetStateRecord : UndoRecord
	{
		#region PublicFields

		public string AssetPath;
		public byte[] Before;
		public byte[] After;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			Apply(editor, Before);
		}

		public override void Redo(EditorApplication editor)
		{
			Apply(editor, After);
		}

		/// <summary>Writes a document asset's state into its loaded instance and its file.</summary>
		public static void Apply(EditorApplication editor, string assetPath, byte[] data)
		{
			if (assetPath == null || data == null)
			{
				return;
			}

			Type type = AssetDatabase.GetAssetType(assetPath);
			if (type == typeof(Shaders.Material))
			{
				Shaders.Material material = InternalStorage.GetOrCreateMaterial(assetPath);
				material.ApplyBytes(data);
				material.Save();
				MaterialEditor.MaterialChanged(editor);
			}
			else if (type == typeof(PhysicsMaterial2D))
			{
				PhysicsMaterial2D material = PhysicsMaterial2D.Load(assetPath);
				Serialization.Documents.ReadObject(data, typeof(PhysicsMaterial2D), material);
				material.Save();
				editor.NotifySceneChanged();
			}
			else if (type == typeof(ScriptableObject))
			{
				ScriptableObject asset = ScriptableObjectAssets.Load(assetPath);
				if (asset != null)
				{
					ScriptableObjectAssets.Apply(asset, data);
					ScriptableObjectAssets.Save(asset);
					asset.Validate();
					editor.NotifySceneChanged();
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void Apply(EditorApplication editor, byte[] data)
		{
			Apply(editor, AssetPath, data);
		}

		#endregion
	}

	/// <summary>Several records as one step (the transforms of a selection moved together): undone last first.</summary>
	internal sealed class GroupRecord : UndoRecord
	{
		#region PublicFields

		public readonly List<UndoRecord> Records = new List<UndoRecord>();

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			for (int i = Records.Count - 1; i >= 0; i--)
			{
				Records[i].Undo(editor);
			}
		}

		public override void Redo(EditorApplication editor)
		{
			foreach (UndoRecord record in Records)
			{
				record.Redo(editor);
			}
		}

		#endregion
	}

	/// <summary>
	///     Structural change (objects created, deleted, re-parented, components added or removed): the whole scene
	///     before and after. Undo reloads the snapshot.
	/// </summary>
	internal sealed class SceneSnapshotRecord : UndoRecord
	{
		#region PublicFields

		public byte[] Before;
		public byte[] After;
		public ulong[] SelectionBefore;
		public ulong[] SelectionAfter;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			editor.RestoreSnapshot(Before, SelectionBefore);
		}

		public override void Redo(EditorApplication editor)
		{
			editor.RestoreSnapshot(After, SelectionAfter);
		}

		#endregion
	}

	internal sealed class UndoStack
	{
		#region PublicFields

		public bool CanUndo => undo.Count > 0;
		public bool CanRedo => redo.Count > 0;
		public string UndoName => undo.Count > 0 ? undo[undo.Count - 1].Name : null;
		public string RedoName => redo.Count > 0 ? redo[redo.Count - 1].Name : null;

		#endregion

		#region PrivateFields

		private const int Limit = 200;
		private readonly List<UndoRecord> undo = new List<UndoRecord>();
		private readonly List<UndoRecord> redo = new List<UndoRecord>();

		#endregion

		#region PublicMethods

		public void Push(UndoRecord record)
		{
			undo.Add(record);
			if (undo.Count > Limit)
			{
				undo.RemoveAt(0);
			}

			redo.Clear();
		}

		public void Undo(EditorApplication editor)
		{
			if (undo.Count == 0)
			{
				return;
			}

			UndoRecord record = undo[undo.Count - 1];
			undo.RemoveAt(undo.Count - 1);
			record.Undo(editor);
			redo.Add(record);
		}

		public void Redo(EditorApplication editor)
		{
			if (redo.Count == 0)
			{
				return;
			}

			UndoRecord record = redo[redo.Count - 1];
			redo.RemoveAt(redo.Count - 1);
			record.Redo(editor);
			undo.Add(record);
		}

		public void Clear()
		{
			undo.Clear();
			redo.Clear();
		}

		/// <summary>Takes the records out (the stack is empty afterwards): a prefab editing stage has its own history.</summary>
		public (List<UndoRecord> undo, List<UndoRecord> redo) Take()
		{
			var taken = (new List<UndoRecord>(undo), new List<UndoRecord>(redo));
			Clear();
			return taken;
		}

		/// <summary>Puts back records taken with <see cref="Take" /> (replacing the current ones).</summary>
		public void Restore((List<UndoRecord> undo, List<UndoRecord> redo) records)
		{
			Clear();
			undo.AddRange(records.undo ?? new List<UndoRecord>());
			redo.AddRange(records.redo ?? new List<UndoRecord>());
		}

		#endregion
	}

	/// <summary>
	///     Prefab files written by an operation (apply to prefab, create prefab), with the scene before and after it:
	///     undo writes the files back and reloads the scene's state from before.
	/// </summary>
	internal sealed class PrefabFilesRecord : UndoRecord
	{
		#region PublicFields

		public readonly List<(string path, byte[] before, byte[] after)> Files = new List<(string, byte[], byte[])>();
		public byte[] SceneBefore;
		public byte[] SceneAfter;
		public ulong[] SelectionBefore;
		public ulong[] SelectionAfter;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			Apply(editor, false);
		}

		public override void Redo(EditorApplication editor)
		{
			Apply(editor, true);
		}

		#endregion

		#region PrivateMethods

		private void Apply(EditorApplication editor, bool after)
		{
			var prefabs = new List<string>();
			foreach ((string path, byte[] before, byte[] afterBytes) in Files)
			{
				byte[] bytes = after ? afterBytes : before;
				string file = AssetDatabase.ToAbsolute(path);
				if (bytes == null)
				{
					if (System.IO.File.Exists(file))
					{
						System.IO.File.Delete(file);
					}
				}
				else
				{
					System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
					System.IO.File.WriteAllBytes(file, bytes);
				}

				if (!path.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase))
				{
					prefabs.Add(path);
				}
			}

			AssetDatabase.Refresh(true);
			Serialization.PrefabCache.Invalidate(prefabs);
			editor.RestoreSnapshot(after ? SceneAfter : SceneBefore, after ? SelectionAfter : SelectionBefore);
			editor.NotifyAssetsChanged();
		}

		#endregion
	}

	/// <summary>
	///     Turns continuous edits (dragging a value, typing, moving a gizmo) into single undo records: the state
	///     before the first change and after the last.
	/// </summary>
	internal sealed class EditTracker
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private object target;
		private byte[] before;
		private string name;
		private bool commitRequested;
		private readonly HashSet<object> savesWaiting = new HashSet<object>();

		// Objects edited together with the target (the inspector's multi-object editing), and their states before.
		private readonly List<object> group = new List<object>();
		private readonly List<byte[]> groupBefore = new List<byte[]>();

		#endregion

		#region Constructors

		public EditTracker(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		/// <summary>Called before the target changes; captures its state once per edit.</summary>
		/// <param name="editName">Undo menu text of a component edit (null: "Edit &lt;Type&gt;").</param>
		public void Begin(object editTarget, string editName = null)
		{
			Begin(editTarget, null, editName);
		}

		/// <summary>
		///     Called before the target and <paramref name="others" /> (objects edited together with it: several selected
		///     objects) change; captures their states once per edit. The edit is one undo step for all of them.
		/// </summary>
		public void Begin(object editTarget, IReadOnlyList<object> others, string editName = null)
		{
			if (target == editTarget && SameGroup(others))
			{
				return;
			}

			if (target != null)
			{
				Commit();
			}

			target = editTarget;
			name = editName;
			before = Capture(editTarget);
			group.Clear();
			groupBefore.Clear();
			if (others == null || editTarget == null)
			{
				return;
			}

			foreach (object other in others)
			{
				if (other != null && other != editTarget && !group.Contains(other))
				{
					group.Add(other);
					groupBefore.Add(Capture(other));
				}
			}
		}

		/// <summary>The edit ends once the current values were written back (see <see cref="Flush" />).</summary>
		public void RequestCommit()
		{
			commitRequested = true;
		}

		/// <summary>Call after all values of the frame were applied.</summary>
		public void Flush()
		{
			if (commitRequested)
			{
				commitRequested = false;
				Commit();
			}
		}

		public void Commit()
		{
			if (target == null)
			{
				return;
			}

			var records = new List<UndoRecord>();
			AddRecord(records, target, before);
			for (int i = 0; i < group.Count; i++)
			{
				if (!(group[i] is EngineObject engineObject && engineObject.IsDestroyed))
				{
					AddRecord(records, group[i], groupBefore[i]);
				}
			}

			if (records.Count == 1)
			{
				editor.Undo.Push(records[0]);
			}
			else if (records.Count > 1)
			{
				// One step for all the objects edited together, named after the edit and how many they were.
				var step = new GroupRecord { Name = (name ?? records[0].Name) + " (" + records.Count + " objects)" };
				step.Records.AddRange(records);
				editor.Undo.Push(step);
			}

			target = null;
			before = null;
			name = null;
			group.Clear();
			groupBefore.Clear();
		}

		/// <summary>
		///     An edit of a window's asset ended: the file follows now, or when nothing is held down any more if a widget or
		///     a drag goes on editing it (recording a dragged value is an edit per frame).
		/// </summary>
		public void SaveAfterEdit(object asset)
		{
			if (editor.HasUI && (ImGui.IsAnyItemActive() || ImGui.IsAnyMouseDown()))
			{
				savesWaiting.Add(asset);
			}
			else
			{
				SaveAsset(asset);
			}
		}

		/// <summary>Every frame: the files waiting for a release.</summary>
		public void UpdateSaves()
		{
			if (savesWaiting.Count > 0 && !ImGui.IsAnyItemActive() && !ImGui.IsAnyMouseDown())
			{
				SaveWaiting();
			}
		}

		/// <summary>The files waiting for a release, now (the scripts reload, the project closes).</summary>
		public void SaveWaiting()
		{
			foreach (object asset in savesWaiting)
			{
				SaveAsset(asset);
			}

			savesWaiting.Clear();
		}

		/// <summary>Writes an asset's file: most assets save as soon as an edit of them ends (see "When things are written" in CLAUDE.md for those that wait).</summary>
		public static void SaveAsset(object asset)
		{
			try
			{
				switch (asset)
				{
					case Shaders.Material material when material.AssetPath != null:
						material.Save();
						break;
					case PhysicsMaterial2D physicsMaterial when physicsMaterial.AssetPath != null:
						physicsMaterial.Save();
						break;
					case ScriptableObject scriptableObject when scriptableObject.AssetPath != null:
						ScriptableObjectAssets.Save(scriptableObject);
						break;
					case LELEngine.Animation.AnimatorController controller when !string.IsNullOrEmpty(controller.AssetPath):
						controller.Save();
						break;
					case LELEngine.Animation.AnimationClip clip when !string.IsNullOrEmpty(clip.AssetPath):
						clip.Save();
						break;
					case LELEngine.Animation.TimelineAsset timeline when !string.IsNullOrEmpty(timeline.AssetPath):
						timeline.Save();
						break;
				}
			}
			catch (Exception e)
			{
				Debug.LogError("[Editor] Cannot save the asset: " + e.Message);
			}
		}

		public void Cancel()
		{
			target = null;
			before = null;
			name = null;
			commitRequested = false;
			group.Clear();
			groupBefore.Clear();
		}

		#endregion

		#region PrivateMethods

		/// <summary>The edit's group is <paramref name="others" /> (the same objects; none and an empty list alike).</summary>
		private bool SameGroup(IReadOnlyList<object> others)
		{
			int count = 0;
			if (others != null)
			{
				foreach (object other in others)
				{
					if (other == null || other == target)
					{
						continue;
					}

					if (count >= group.Count || !ReferenceEquals(group[count], other))
					{
						return false;
					}

					count++;
				}
			}

			return count == group.Count;
		}

		/// <summary>The undo record of one edited object whose state changed (assets are saved, windows told); none when it did not.</summary>
		private void AddRecord(List<UndoRecord> records, object edited, byte[] stateBefore)
		{
			byte[] after = Capture(edited);
			if (EditorSerialization.SameState(after, stateBefore))
			{
				return;
			}

			switch (edited)
			{
				case Behaviour component:
					records.Add(new ComponentStateRecord { Name = name ?? "Edit " + component.GetType().Name, ComponentId = component.Id, Before = stateBefore, After = after });
					break;
				case SceneSettings _:
					records.Add(new SettingsRecord { Name = "Edit scene settings", Before = stateBefore, After = after });
					break;
				case ProjectSettings _:
					// Saved when the edit ends, like assets.
					records.Add(new ProjectSettingsRecord { Name = "Edit project settings", Before = stateBefore, After = after });
					editor.SaveProjectSettings();
					break;
				case Shaders.Material material:
					// Assets save when the edit ends (Unity writes material changes to disk as they happen).
					records.Add(new AssetStateRecord { Name = name ?? "Edit " + material.name, AssetPath = material.AssetPath, Before = stateBefore, After = after });
					SaveAsset(material);
					break;
				case PhysicsMaterial2D physicsMaterial:
					records.Add(new AssetStateRecord { Name = name ?? "Edit " + physicsMaterial.name, AssetPath = physicsMaterial.AssetPath, Before = stateBefore, After = after });
					SaveAsset(physicsMaterial);
					break;
				case ScriptableObject scriptableObject:
					records.Add(new AssetStateRecord { Name = name ?? "Edit " + scriptableObject.name, AssetPath = scriptableObject.AssetPath, Before = stateBefore, After = after });
					SaveAsset(scriptableObject);
					break;
				case PrefabPreview preview:
					// An inline editor's prefab copy: written to the file as one undo step (files and scene before and
					// after), after the frame, since the scene is loaded again so its instances follow.
					string previewName = name ?? "Edit " + preview.Name;
					editor.Defer(() => editor.RecordPrefabFiles(previewName, new[] { preview.AssetPath }, preview.Save));
					break;
				case LELEngine.Animation.AnimatorController controller:
					// Edited in the Animator window (and its inspectors): players rebuild live, the file follows.
					records.Add(new Animation.AnimatorRecord { Name = name ?? "Edit " + controller.name, AssetPath = controller.AssetPath, Before = stateBefore, After = after });
					controller.MarkChanged();
					SaveAsset(controller);
					editor.AnimatorWindow?.OnControllerEdited(controller);
					break;
				case LELEngine.Animation.AnimationClip clip:
					records.Add(new Animation.AnimationClipRecord { Name = name ?? "Edit " + clip.name, AssetPath = clip.AssetPath, Before = stateBefore, After = after });
					clip.MarkChanged();
					SaveAsset(clip);
					editor.AnimationWindow?.OnClipEdited(clip);
					break;
			}
		}

		private byte[] Capture(object editTarget)
		{
			switch (editTarget)
			{
				case Behaviour component:
					return EditorSerialization.CaptureComponent(component);
				case SceneSettings settings:
					return EditorSerialization.CaptureObject(settings, editor.Scene);
				case ProjectSettings projectSettings:
					return EditorSerialization.CaptureObject(projectSettings, null);
				case Shaders.Material material:
					return material.ToBytes();
				case PhysicsMaterial2D physicsMaterial:
					return Serialization.Documents.WriteObject(physicsMaterial, Serialization.DocumentFormat.Binary);
				case ScriptableObject scriptableObject:
					return Serialization.Documents.WriteObject(scriptableObject, Serialization.DocumentFormat.Binary);
				case PrefabPreview preview:
					return preview.Capture();
				case LELEngine.Animation.AnimatorController controller:
					return controller.ToBytes();
				case LELEngine.Animation.AnimationClip clip:
					return clip.ToBytes();
				default:
					return null;
			}
		}

		#endregion
	}
}
