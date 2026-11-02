using System;
using System.Collections.Generic;
using LELEngine.Animation;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The timeline open in the Timeline window: its asset and undo snapshots (one record per edit, the whole timeline
	///     before and after, plus the clips the edit keyed). Edits apply live: the asset bumps its version and the players
	///     rebuild on their next frame. The files, the timeline's and the keyed clips', are written when an edit ends.
	/// </summary>
	internal sealed class TimelineDocument
	{
		#region PublicFields

		public TimelineAsset Asset { get; }
		public string AssetPath => Asset.AssetPath;
		public bool Editing => editBefore != null;

		/// <summary>Raised after an undo or redo restored another state (the window drops stale selections).</summary>
		public event Action Restored;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly Dictionary<AnimationClip, byte[]> clipsBefore = new Dictionary<AnimationClip, byte[]>();
		private byte[] editBefore;
		private string editName;
		private Timeline editHost;
		private byte[] editHostBefore;

		#endregion

		#region Constructors

		public TimelineDocument(EditorApplication editor, TimelineAsset asset)
		{
			this.editor = editor;
			Asset = asset;
		}

		#endregion

		#region PublicMethods

		/// <summary>Captures the timeline before an edit (nested calls are ignored until <see cref="EndEdit" />).</summary>
		public void BeginEdit(string name)
		{
			if (editBefore != null)
			{
				return;
			}

			editBefore = Asset.ToBytes();
			editName = name;
		}

		/// <summary>An animation clip is about to change in the current edit (recording, key edits): its state goes into the edit's undo step.</summary>
		public void TouchClip(AnimationClip clip)
		{
			if (clip != null && editBefore != null && !clipsBefore.ContainsKey(clip))
			{
				clipsBefore[clip] = clip.ToBytes();
			}
		}

		/// <summary>The component's bindings are about to change in the current edit: they go into the edit's undo step.</summary>
		public void TouchHost(Timeline host)
		{
			if (host != null && editBefore != null && editHost == null)
			{
				editHost = host;
				editHostBefore = EditorSerialization.CaptureComponent(host);
			}
		}

		/// <summary>Ends the edit: when something changed, one undo record and the files written.</summary>
		public void EndEdit()
		{
			if (editBefore == null)
			{
				return;
			}

			byte[] after = Asset.ToBytes();
			byte[] before = editBefore;
			editBefore = null;
			var record = new TimelineRecord { Name = editName ?? "Edit timeline", AssetPath = AssetPath, Before = before, After = after };
			bool assetChanged = !EditorSerialization.SameState(before, after);
			bool changed = assetChanged;
			foreach (KeyValuePair<AnimationClip, byte[]> pair in clipsBefore)
			{
				byte[] clipAfter = pair.Key.ToBytes();
				if (!EditorSerialization.SameState(pair.Value, clipAfter))
				{
					record.Clips.Add(new TimelineRecord.ClipChange { AssetPath = pair.Key.AssetPath, Before = pair.Value, After = clipAfter });
					pair.Key.MarkChanged();
					editor.Edits.SaveAfterEdit(pair.Key);
					editor.AnimationWindow?.OnClipEdited(pair.Key);
					changed = true;
				}
			}

			clipsBefore.Clear();
			if (editHost != null)
			{
				byte[] hostAfter = EditorSerialization.CaptureComponent(editHost);
				if (!EditorSerialization.SameState(editHostBefore, hostAfter))
				{
					record.HostId = editHost.Id;
					record.HostBefore = editHostBefore;
					record.HostAfter = hostAfter;
					editor.MarkDirty();
					changed = true;
				}

				editHost = null;
				editHostBefore = null;
			}

			if (changed)
			{
				editor.Undo.Push(record);
				Changed();
				if (assetChanged)
				{
					editor.Edits.SaveAfterEdit(Asset);
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

		/// <summary>The timeline changed: the players rebuild, views refresh.</summary>
		public void Changed()
		{
			Asset.MarkChanged();
			editor.NotifySceneChanged();
		}

		/// <summary>An undo or redo restored another state of the timeline.</summary>
		public void OnRestored()
		{
			Restored?.Invoke();
		}

		#endregion
	}

	/// <summary>The whole timeline before and after an edit in the Timeline window, and the clips it keyed.</summary>
	internal sealed class TimelineRecord : UndoRecord
	{
		#region PublicFields

		public sealed class ClipChange
		{
			public string AssetPath;
			public byte[] Before;
			public byte[] After;
		}

		public string AssetPath;
		public byte[] Before;
		public byte[] After;
		public readonly List<ClipChange> Clips = new List<ClipChange>();

		/// <summary>The Timeline component whose bindings the edit changed (0: none).</summary>
		public ulong HostId;

		public byte[] HostBefore;
		public byte[] HostAfter;

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
			foreach (ClipChange change in Clips)
			{
				AnimationClip clip = AnimationClip.FindLoaded(change.AssetPath);
				if (clip != null)
				{
					clip.ApplyBytes(after ? change.After : change.Before);
					EditTracker.SaveAsset(clip);
					editor.AnimationWindow?.OnClipRestored(clip);
				}
			}

			if (HostId != 0 && editor.Scene?.FindComponentById(HostId) is Behaviour host)
			{
				EditorSerialization.ApplyComponent(host, after ? HostAfter : HostBefore);
				editor.NotifyChanged(host.gameObject);
			}

			TimelineAsset asset = TimelineAsset.FindLoaded(AssetPath);
			if (asset == null)
			{
				return;
			}

			asset.ApplyBytes(after ? After : Before);
			EditTracker.SaveAsset(asset);
			editor.NotifySceneChanged();
			editor.TimelineWindow?.OnTimelineRestored(asset);
		}

		#endregion
	}
}
