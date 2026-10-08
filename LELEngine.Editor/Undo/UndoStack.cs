using System.Collections.Generic;

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
		public string Before;
		public string After;

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

		private void Apply(EditorApplication editor, string json)
		{
			Behaviour component = editor.Scene?.FindComponentById(ComponentId);
			if (component == null)
			{
				return;
			}

			EditorSerialization.ApplyComponent(component, json);
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

	/// <summary>Scene settings (environment, shadows, GI).</summary>
	internal sealed class SettingsRecord : UndoRecord
	{
		#region PublicFields

		public string Before;
		public string After;

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

		private static void Apply(EditorApplication editor, string json)
		{
			Scene scene = editor.Scene;
			if (scene == null)
			{
				return;
			}

			EditorSerialization.ApplyObject(scene.Settings, json, scene);
			scene.Settings.GI.InvalidateStatic();
			editor.MarkDirty();
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

		public string Before;
		public string After;
		public ulong SelectionBefore;
		public ulong SelectionAfter;

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
		private string before;
		private bool commitRequested;

		#endregion

		#region Constructors

		public EditTracker(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		/// <summary>Called before the target changes; captures its state once per edit.</summary>
		public void Begin(object editTarget)
		{
			if (target == editTarget)
			{
				return;
			}

			if (target != null)
			{
				Commit();
			}

			target = editTarget;
			before = Capture(editTarget);
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

			string after = Capture(target);
			if (after != before)
			{
				switch (target)
				{
					case Behaviour component:
						editor.Undo.Push(new ComponentStateRecord { Name = "Edit " + component.GetType().Name, ComponentId = component.Id, Before = before, After = after });
						break;
					case SceneSettings _:
						editor.Undo.Push(new SettingsRecord { Name = "Edit scene settings", Before = before, After = after });
						break;
				}
			}

			target = null;
			before = null;
		}

		public void Cancel()
		{
			target = null;
			before = null;
			commitRequested = false;
		}

		#endregion

		#region PrivateMethods

		private string Capture(object editTarget)
		{
			switch (editTarget)
			{
				case Behaviour component:
					return EditorSerialization.CaptureComponent(component);
				case SceneSettings settings:
					return EditorSerialization.CaptureObject(settings, editor.Scene);
				default:
					return null;
			}
		}

		#endregion
	}
}
