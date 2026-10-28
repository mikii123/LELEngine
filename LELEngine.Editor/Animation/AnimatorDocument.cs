using System;
using LELEngine.Animation;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The controller open in the Animator window: its asset, dirty state, undo snapshots (one record per edit,
	///     the whole controller before and after) and saving. Edits apply live: the controller bumps its version and
	///     every animator playing it rebuilds on its next update, keeping its state and parameter values.
	/// </summary>
	internal sealed class AnimatorDocument
	{
		#region PublicFields

		public AnimatorController Controller { get; }
		public string AssetPath => Controller.AssetPath;
		public bool Dirty { get; private set; }
		public bool Editing => editBefore != null;

		/// <summary>Raised after an undo or redo restored another state (the window drops stale selections).</summary>
		public event Action Restored;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private byte[] editBefore;
		private string editName;
		private bool editLayoutOnly;

		#endregion

		#region Constructors

		public AnimatorDocument(EditorApplication editor, AnimatorController controller)
		{
			this.editor = editor;
			Controller = controller;
		}

		#endregion

		#region PublicMethods

		/// <summary>Captures the controller before an edit (nested calls are ignored until <see cref="EndEdit" />).</summary>
		public void BeginEdit(string name, bool layoutOnly = false)
		{
			if (editBefore != null)
			{
				return;
			}

			editBefore = Controller.ToBytes();
			editName = name;
			editLayoutOnly = layoutOnly;
		}

		/// <summary>Ends the edit: pushes an undo record when something changed and applies the change to the players.</summary>
		public void EndEdit()
		{
			if (editBefore == null)
			{
				return;
			}

			byte[] after = Controller.ToBytes();
			byte[] before = editBefore;
			editBefore = null;
			if (!EditorSerialization.SameState(before, after))
			{
				editor.Undo.Push(new AnimatorRecord { Name = editName ?? "Edit animator", AssetPath = AssetPath, Before = before, After = after, LayoutOnly = editLayoutOnly });
				Changed(!editLayoutOnly);
			}
		}

		/// <summary>Runs a change as one undo step.</summary>
		public void Edit(string name, Action change)
		{
			BeginEdit(name);
			try
			{
				change();
			}
			finally
			{
				EndEdit();
			}
		}

		/// <summary>Runs a layout change (positions, view) as one undo step without rebuilding the players.</summary>
		public void EditLayout(string name, Action change)
		{
			BeginEdit(name, true);
			try
			{
				change();
			}
			finally
			{
				EndEdit();
			}
		}

		/// <summary>Marks the controller changed (dirty; with <paramref name="rebuild" /> the players rebuild).</summary>
		public void Changed(bool rebuild = true)
		{
			Dirty = true;
			if (rebuild)
			{
				Controller.MarkChanged();
			}

			editor.NotifySceneChanged();
		}

		public void Save()
		{
			if (string.IsNullOrEmpty(AssetPath))
			{
				return;
			}

			Controller.Save();
			Dirty = false;
			Debug.Log("[Animator] Saved " + AssetPath);
		}

		/// <summary>Reloads the controller from its file (discarding unsaved changes).</summary>
		public void Revert()
		{
			if (string.IsNullOrEmpty(AssetPath))
			{
				return;
			}

			try
			{
				byte[] data = System.IO.File.ReadAllBytes(AssetDatabase.ToAbsolute(AssetPath));
				Controller.ApplyBytes(data);
				Dirty = false;
				editor.NotifySceneChanged();
				Restored?.Invoke();
			}
			catch (Exception e)
			{
				Debug.LogError("[Animator] Cannot revert " + AssetPath + ": " + e.Message);
			}
		}

		/// <summary>An undo or redo restored another state of the controller: it is dirty again.</summary>
		public void OnRestored()
		{
			Dirty = true;
			Restored?.Invoke();
		}

		#endregion
	}

	/// <summary>The whole controller before and after an edit in the Animator window (or its inspectors).</summary>
	internal sealed class AnimatorRecord : UndoRecord
	{
		#region PublicFields

		public string AssetPath;
		public byte[] Before;
		public byte[] After;
		public bool LayoutOnly;

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
			AnimatorController controller = AnimatorController.FindLoaded(AssetPath);
			if (controller == null || data == null)
			{
				return;
			}

			controller.ApplyBytes(data);
			editor.NotifySceneChanged();
			editor.AnimatorWindow?.OnControllerRestored(controller);
		}

		#endregion
	}

	/// <summary>The whole clip before and after an edit in the Animation window (or its inspectors).</summary>
	internal sealed class AnimationClipRecord : UndoRecord
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

		#endregion

		#region PrivateMethods

		private void Apply(EditorApplication editor, byte[] data)
		{
			AnimationClip clip = AnimationClip.FindLoaded(AssetPath);
			if (clip == null || data == null)
			{
				return;
			}

			clip.ApplyBytes(data);
			editor.NotifySceneChanged();
			editor.AnimationWindow?.OnClipRestored(clip);
		}

		#endregion
	}
}
