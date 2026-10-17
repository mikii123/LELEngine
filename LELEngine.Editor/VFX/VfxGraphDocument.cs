using System;
using LELEngine.VFX;

namespace LELEngine.Editor.VFX
{
	/// <summary>
	///     The graph open in the VFX Graph window: its asset, dirty state, undo snapshots (one record per edit, the
	///     whole graph before and after) and saving. Edits apply live: the graph bumps its version and every
	///     component playing it recompiles on its next frame.
	/// </summary>
	internal sealed class VfxGraphDocument
	{
		#region PublicFields

		public VfxGraph Graph { get; }
		public string AssetPath => Graph.AssetPath;
		public bool Dirty { get; private set; }
		public bool Editing => editBefore != null;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private byte[] editBefore;
		private string editName;
		private bool editLayoutOnly;

		#endregion

		#region Constructors

		public VfxGraphDocument(EditorApplication editor, VfxGraph graph)
		{
			this.editor = editor;
			Graph = graph;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Captures the graph before an edit (nested calls are ignored until <see cref="EndEdit" />). A layout-only
		///     edit (node positions, titles) is undoable and dirties the asset but does not recompile the players.
		/// </summary>
		public void BeginEdit(string name, bool layoutOnly = false)
		{
			if (editBefore != null)
			{
				return;
			}

			editBefore = Graph.ToBytes();
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

			byte[] after = Graph.ToBytes();
			byte[] before = editBefore;
			editBefore = null;
			if (!EditorSerialization.SameState(before, after))
			{
				editor.Undo.Push(new VfxGraphRecord { Name = editName ?? "Edit VFX graph", AssetPath = AssetPath, Before = before, After = after, LayoutOnly = editLayoutOnly });
				if (editLayoutOnly)
				{
					Dirty = true;
				}
				else
				{
					Changed();
				}
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

		/// <summary>Runs a layout change (positions, titles) as one undo step without recompiling.</summary>
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

		/// <summary>Marks the graph changed (dirty, recompiled by its players, views refreshed).</summary>
		public void Changed()
		{
			Dirty = true;
			Graph.MarkChanged();
			editor.NotifySceneChanged();
		}

		public void Save()
		{
			if (string.IsNullOrEmpty(AssetPath))
			{
				return;
			}

			Graph.Save();
			AssetDatabase.EnsureMeta(AssetPath);
			Dirty = false;
			Debug.Log("[VFX] Saved " + AssetPath);
		}

		/// <summary>Reloads the graph from its file (discarding unsaved changes).</summary>
		public void Revert()
		{
			if (string.IsNullOrEmpty(AssetPath))
			{
				return;
			}

			try
			{
				byte[] data = System.IO.File.ReadAllBytes(AssetDatabase.ToAbsolute(AssetPath));
				Graph.ApplyBytes(data);
				Dirty = false;
				editor.NotifySceneChanged();
			}
			catch (Exception e)
			{
				Debug.LogError("[VFX] Cannot revert " + AssetPath + ": " + e.Message);
			}
		}

		/// <summary>An undo or redo restored another state of the graph: it is dirty again.</summary>
		public void Restored()
		{
			Dirty = true;
		}

		#endregion
	}

	/// <summary>The whole graph before and after an edit in the VFX Graph window.</summary>
	internal sealed class VfxGraphRecord : UndoRecord
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
			VfxGraph graph = VfxGraph.FindLoaded(AssetPath);
			if (graph == null || data == null)
			{
				return;
			}

			graph.ApplyBytes(data, !LayoutOnly);
			editor.NotifySceneChanged();
			editor.VfxGraphWindow?.OnGraphRestored(graph);
		}

		#endregion
	}
}
