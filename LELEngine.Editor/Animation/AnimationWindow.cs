using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Animation
{
	/// <summary>The clip open in the Animation window: dirty state, undo snapshots, saving (as <see cref="AnimatorDocument" />).</summary>
	internal sealed class AnimationClipDocument
	{
		#region PublicFields

		public AnimationClip Clip { get; }
		public string AssetPath => Clip.AssetPath;
		public bool Dirty { get; private set; }
		public event Action Restored;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private byte[] editBefore;
		private string editName;

		#endregion

		#region Constructors

		public AnimationClipDocument(EditorApplication editor, AnimationClip clip)
		{
			this.editor = editor;
			Clip = clip;
		}

		#endregion

		#region PublicMethods

		public void BeginEdit(string name)
		{
			if (editBefore != null)
			{
				return;
			}

			editBefore = Clip.ToBytes();
			editName = name;
		}

		public void EndEdit()
		{
			if (editBefore == null)
			{
				return;
			}

			byte[] after = Clip.ToBytes();
			byte[] before = editBefore;
			editBefore = null;
			if (!EditorSerialization.SameState(before, after))
			{
				editor.Undo.Push(new AnimationClipRecord { Name = editName ?? "Edit clip", AssetPath = AssetPath, Before = before, After = after });
				Changed();
			}
		}

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

		public void Changed()
		{
			Dirty = true;
			Clip.MarkChanged();
			editor.NotifySceneChanged();
		}

		public void Save()
		{
			if (string.IsNullOrEmpty(AssetPath))
			{
				return;
			}

			Clip.Save();
			Dirty = false;
			Debug.Log("[Animation] Saved " + AssetPath);
		}

		public void Revert()
		{
			if (string.IsNullOrEmpty(AssetPath))
			{
				return;
			}

			try
			{
				Clip.ApplyBytes(File.ReadAllBytes(AssetDatabase.ToAbsolute(AssetPath)));
				Dirty = false;
				editor.NotifySceneChanged();
				Restored?.Invoke();
			}
			catch (Exception e)
			{
				Debug.LogError("[Animation] Cannot revert " + AssetPath + ": " + e.Message);
			}
		}

		public void OnRestored()
		{
			Dirty = true;
			Restored?.Invoke();
		}

		#endregion
	}

	/// <summary>
	///     The Animation window (Window > Animation, or open a .anim asset): the clip editor. The animated properties
	///     on the left (any serialized field, property or transform channel of the target hierarchy, added through
	///     the property picker or by recording), a dope sheet or the curves on the right with a ruler, the playhead and
	///     draggable keys, an events lane at the bottom with the event's inspector. Preview samples the clip on the
	///     target object in edit mode (restored when preview ends); Record keys every change made to animated
	///     properties (inspector, scene view) at the playhead. Edits are undoable; Ctrl+S saves the asset.
	/// </summary>
	internal sealed class AnimationWindow : IEditorPanel
	{
		#region PublicFields

		public const string Title = "Animation";
		public bool Open;

		string IEditorPanel.PanelName => Title;
		string IEditorPanel.ImGuiName => "###animation";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(1200, 600);
		void IEditorPanel.Focus() => editor.OpenAnimationWindow(document?.AssetPath);
		public bool IsFocused { get; private set; }
		public AnimationClipDocument Document => document;

		/// <summary>The clip's playhead in seconds.</summary>
		public float Time { get; private set; }

		public GameObject Target => target;

		#endregion

		#region PrivateFields

		private enum ViewMode
		{
			DopeSheet,
			Curves
		}

		private struct KeyRef : IEquatable<KeyRef>
		{
			public int Binding;
			public int Channel;
			public float Time;

			public bool Equals(KeyRef other) => Binding == other.Binding && Channel == other.Channel && Math.Abs(Time - other.Time) < 1e-6f;
			public override bool Equals(object obj) => obj is KeyRef other && Equals(other);
			public override int GetHashCode() => Binding * 397 ^ Channel;
		}

		private const float LeftWidth = 320f;
		private const float RowHeight = 22f;
		private const float RulerHeight = 26f;
		private const float EventsHeight = 24f;
		private const float KeySize = 5f;

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private AnimationClipDocument document;
		private GameObject target;
		private bool lockTarget;
		private bool preview;
		private bool record;
		private bool playing;
		private bool snapToFrames = true;
		private ViewMode mode = ViewMode.DopeSheet;
		private float pixelsPerSecond = 200f;
		private float scrollTime;
		private float valueMin = -1f;
		private float valueMax = 1f;
		private bool valueRangeDirty = true;
		private readonly HashSet<KeyRef> selectedKeys = new HashSet<KeyRef>();
		private readonly HashSet<int> selectedRows = new HashSet<int>();
		private readonly HashSet<int> expandedRows = new HashSet<int>();
		private int selectedEvent = -1;
		private bool focusRequested;
		private bool closeRequested;
		private bool openUnsavedPopup;
		private bool draggingPlayhead;
		private bool draggingKeys;
		private bool keysMoved;
		private float dragStartTime;
		private NVector2 dragStartMouse;
		private readonly Dictionary<KeyRef, (float time, float value)> dragOrigins = new Dictionary<KeyRef, (float, float)>();
		private bool boxSelecting;
		private NVector2 boxStart;
		private int draggingEvent = -1;
		private float eventDragStart;
		private bool panning;
		private NVector2 panStartMouse;
		private float panStartScroll;
		private float panStartValueMin;
		private float panStartValueMax;
		private int tangentDrag = -1;
		private KeyRef tangentKey;
		private PreviewSession previewSession;
		private string propertySearch = "";
		private string eventFunctionBuffer;
		private int eventFunctionFor = -1;
		private byte[] reloadSnapshot;
		private bool reloadDirty;
		private string reloadPath;
		private float timeInput;
		private int frameInput;
		private NVector2 timelineMin;
		private NVector2 timelineSize;
		private NVector2 rowsMin;
		private readonly List<(int binding, int channel, float y)> rowLayout = new List<(int, int, float)>();
		private static readonly FieldInfo PayloadField = typeof(AnimationEvent).GetField(nameof(AnimationEvent.payload));
		private static readonly NVector4[] ChannelColors = { new NVector4(0.95f, 0.4f, 0.4f, 1f), new NVector4(0.45f, 0.9f, 0.45f, 1f), new NVector4(0.45f, 0.6f, 1f, 1f), new NVector4(0.95f, 0.85f, 0.4f, 1f) };

		#endregion

		#region Constructors

		public AnimationWindow(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		public void Show(string assetPath)
		{
			Open = true;
			focusRequested = true;
			if (assetPath != null && (document == null || !string.Equals(document.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase)))
			{
				SetDocument(AnimationClip.Load(assetPath));
			}
		}

		public void Close()
		{
			closeRequested = true;
		}

		public void OnClipRestored(AnimationClip clip)
		{
			if (document != null && document.Clip == clip)
			{
				document.OnRestored();
				valueRangeDirty = true;
			}
		}

		public void OnClipEdited(AnimationClip clip)
		{
			if (document != null && document.Clip == clip)
			{
				document.Changed();
				valueRangeDirty = true;
			}
		}

		public void Reset()
		{
			EndPreview();
			document = null;
			target = null;
			Open = false;
			selectedKeys.Clear();
			selectedRows.Clear();
		}

		public void OnDomainUnloading()
		{
			EndPreview();
			reloadSnapshot = document != null && document.Dirty ? document.Clip.ToBytes() : null;
			reloadDirty = document != null && document.Dirty;
			reloadPath = document?.AssetPath;
			document = null;
			target = null;
		}

		public void OnDomainLoaded()
		{
			if (reloadPath == null)
			{
				return;
			}

			AnimationClip clip = AnimationClip.Load(reloadPath);
			if (clip != null && reloadSnapshot != null)
			{
				clip.ApplyBytes(reloadSnapshot);
			}

			SetDocument(clip);
			if (reloadDirty)
			{
				document?.Changed();
			}

			reloadSnapshot = null;
			reloadPath = null;
		}

		public void Draw()
		{
			if (!Open || editor.Project == null)
			{
				IsFocused = false;
				if (previewSession != null)
				{
					EndPreview();
				}

				return;
			}

			ImGui.SetNextWindowSize(new NVector2(1200, 600), ImGuiCond.FirstUseEver);
			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			bool open = Open;
			string title = Title + (document != null ? " - " + Path.GetFileNameWithoutExtension(document.AssetPath ?? "untitled") + (document.Dirty ? " *" : "") : "") + "###animation";
			bool visible = ImGui.Begin(title, ref open, ImGuiWindowFlags.MenuBar);
			if (!Automation.WindowCapture.Active)
			{
				IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			}

			if (visible)
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			ImGui.End();
			if (!open)
			{
				closeRequested = true;
			}

			if (closeRequested)
			{
				closeRequested = false;
				if (document != null && document.Dirty)
				{
					openUnsavedPopup = true;
					Open = true;
				}
				else
				{
					EndPreview();
					Open = false;
				}
			}

			DrawUnsavedPopup();
		}

		/// <summary>Moves the playhead (seconds), snapped to frames when enabled.</summary>
		public void SetTime(float seconds)
		{
			if (document == null)
			{
				return;
			}

			float rate = Math.Max(document.Clip.frameRate, 1f);
			Time = Math.Max(0f, snapToFrames ? (float)Math.Round(seconds * rate) / rate : seconds);
		}

		#endregion

		#region PrivateMethods

		private AnimationClip Clip => document?.Clip;

		private void SetDocument(AnimationClip clip)
		{
			EndPreview();
			selectedKeys.Clear();
			selectedRows.Clear();
			expandedRows.Clear();
			selectedEvent = -1;
			Time = 0f;
			playing = false;
			record = false;
			if (clip == null)
			{
				document = null;
				return;
			}

			document = new AnimationClipDocument(editor, clip);
			document.Restored += () =>
			{
				selectedKeys.Clear();
				valueRangeDirty = true;
			};
			valueRangeDirty = true;
		}

		private void DrawContent()
		{
			DrawMenuBar();
			if (document == null)
			{
				ImGui.TextDisabled("No clip open: double click a .anim asset in the Project panel, or create one (right click > Create Animation Clip).");
				return;
			}

			context.Scene = editor.Scene;
			context.UndoTarget = Clip;
			context.LabelWidth = 110f;
			UpdateTarget();
			HandleShortcuts();
			DrawToolbar();
			AdvancePlayback();
			if (record && previewSession != null)
			{
				DetectRecordedChanges();
			}

			float eventsPane = selectedEvent >= 0 && selectedEvent < Clip.events.Count ? 150f : 0f;
			NVector2 avail = ImGui.GetContentRegionAvail();
			float height = Math.Max(120f, avail.Y - eventsPane);
			if (ImGui.BeginChild("##anim_rows", new NVector2(LeftWidth, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar))
			{
				DrawRows();
			}

			ImGui.EndChild();
			ImGui.SameLine();
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			bool timelineOpen = ImGui.BeginChild("##anim_timeline", new NVector2(0, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
			ImGui.PopStyleVar();
			if (timelineOpen)
			{
				DrawTimeline();
			}

			ImGui.EndChild();
			if (eventsPane > 0f)
			{
				DrawEventInspector();
			}

			ApplyPreview();
		}

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMenuBar())
			{
				return;
			}

			if (EditorMenu.Begin("File"))
			{
				if (EditorMenu.Item("Save", "Ctrl+S", false, document != null && document.Dirty)) document.Save();
				if (EditorMenu.Item("Revert", "", false, document != null && document.Dirty)) document.Revert();
				ImGui.Separator();
				if (EditorMenu.Item("Close")) closeRequested = true;
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Edit", document != null))
			{
				if (EditorMenu.Item("Undo", "Ctrl+Z", false, editor.Undo.CanUndo)) editor.UndoLast();
				if (EditorMenu.Item("Redo", "Ctrl+Y", false, editor.Undo.CanRedo)) editor.RedoLast();
				ImGui.Separator();
				if (EditorMenu.Item("Key All Properties", "K")) KeyAll();
				if (EditorMenu.Item("Delete Selected Keys", "Del", false, selectedKeys.Count > 0)) DeleteSelectedKeys();
				if (EditorMenu.Item("Select All Keys", "Ctrl+A")) SelectAllKeys();
				ImGui.Separator();
				if (EditorMenu.Begin("Tangents", selectedKeys.Count > 0))
				{
					if (EditorMenu.Item("Clamped Auto")) SetTangentMode(TangentMode.ClampedAuto);
					if (EditorMenu.Item("Linear")) SetTangentMode(TangentMode.Linear);
					if (EditorMenu.Item("Constant (step)")) SetTangentMode(TangentMode.Constant);
					if (EditorMenu.Item("Free")) SetTangentMode(TangentMode.Free);
					if (EditorMenu.Item("Broken")) SetTangentMode(TangentMode.Broken);
					EditorMenu.End();
				}

				ImGui.Separator();
				if (EditorMenu.Item("Reverse Clip")) ReverseClip();
				if (EditorMenu.Item("Trim to Last Key")) document.Edit("Trim clip", () => Clip.lengthOverride = 0f);
				EditorMenu.End();
			}

			if (EditorMenu.Begin("View"))
			{
				if (EditorMenu.Item("Dope Sheet", "", mode == ViewMode.DopeSheet)) mode = ViewMode.DopeSheet;
				if (EditorMenu.Item("Curves", "", mode == ViewMode.Curves)) { mode = ViewMode.Curves; valueRangeDirty = true; }
				ImGui.Separator();
				EditorMenu.Item("Snap to Frames", "", ref snapToFrames);
				if (EditorMenu.Item("Frame All", "F")) FrameAll();
				EditorMenu.End();
			}

			ImGui.EndMenuBar();
		}

		private void DrawToolbar()
		{
			AnimationClip clip = Clip;
			ImGui.BeginDisabled(!document.Dirty);
			if (EditorGui.Button("Save")) document.Save();
			ImGui.EndDisabled();
			ImGui.SameLine();
			ImGui.TextDisabled(document.AssetPath ?? "");
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(8, 0));
			ImGui.SameLine();

			bool canPreview = target != null && !editor.IsPlaying;
			ImGui.BeginDisabled(!canPreview);
			bool previewValue = preview && previewSession != null;
			if (previewValue) ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
			if (EditorGui.Button("Preview"))
			{
				if (previewValue) EndPreview();
				else BeginPreview();
			}

			if (previewValue) ImGui.PopStyleColor();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip(canPreview ? "Sample the clip on the target object at the playhead (restored when off)" : "Select a GameObject (with the animated hierarchy) in edit mode to preview");
			ImGui.SameLine();
			if (record) ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Error);
			if (EditorGui.Button("Record"))
			{
				record = !record;
				if (record && previewSession == null) BeginPreview();
			}

			if (record) ImGui.PopStyleColor();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every change to an animated property (inspector, scene view) becomes a key at the playhead. Add properties with Add Property first.");
			ImGui.EndDisabled();
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(8, 0));
			ImGui.SameLine();

			if (EditorGui.Button("|<")) SetTime(0f);
			ImGui.SameLine();
			if (EditorGui.Button("<")) SetTime(Time - 1f / Math.Max(clip.frameRate, 1f));
			ImGui.SameLine();
			if (playing) ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
			if (EditorGui.Button(playing ? "||" : ">"))
			{
				playing = !playing;
				if (playing && previewSession == null && canPreview) BeginPreview();
			}

			if (playing) ImGui.PopStyleColor();
			ImGui.SameLine();
			if (EditorGui.Button(">##next")) SetTime(Time + 1f / Math.Max(clip.frameRate, 1f));
			ImGui.SameLine();
			if (EditorGui.Button(">|")) SetTime(clip.length);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(70f);
			if (!ImGui.IsItemActive()) timeInput = Time;
			EditorGui.InputFloat("##time", ref timeInput, 0f, 0f, "%.3f s");
			if (ImGui.IsItemDeactivatedAfterEdit()) SetTime(timeInput);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(60f);
			frameInput = clip.TimeToFrame(Time);
			EditorGui.InputInt("##frame", ref frameInput, 0, 0);
			if (ImGui.IsItemDeactivatedAfterEdit()) SetTime(clip.FrameToTime(frameInput));
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Frame");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(60f);
			float rate = clip.frameRate;
			EditorGui.InputFloat("##rate", ref rate, 0f, 0f, "%.0f fps");
			if (ImGui.IsItemDeactivatedAfterEdit() && Math.Abs(rate - clip.frameRate) > 1e-4f) document.Edit("Change frame rate", () => clip.frameRate = Math.Max(1f, rate));
			ImGui.SameLine();
			bool loop = clip.loop;
			if (EditorGui.Checkbox("Loop", ref loop)) document.Edit("Toggle loop", () => clip.loop = loop);
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(8, 0));
			ImGui.SameLine();
			ImGui.BeginDisabled(target == null);
			if (EditorGui.Button("Add Property")) ImGui.OpenPopup("##add_property");
			ImGui.EndDisabled();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip(target != null ? "Animate a property of '" + target.Name + "' or its children" : "Select a GameObject to add its properties");
			DrawAddPropertyPopup();
			ImGui.SameLine();
			if (EditorGui.Button("Add Event")) AddEvent(Time);
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(8, 0));
			ImGui.SameLine();
			if (EditorGui.Button(mode == ViewMode.DopeSheet ? "Dope Sheet" : "Curves"))
			{
				mode = mode == ViewMode.DopeSheet ? ViewMode.Curves : ViewMode.DopeSheet;
				valueRangeDirty = true;
			}

			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Switch between the dope sheet and the curves");
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(8, 0));
			ImGui.SameLine();
			ImGui.TextDisabled("Target:");
			ImGui.SameLine();
			ImGui.TextUnformatted(target != null ? target.Name : "(select a GameObject)");
			ImGui.SameLine();
			if (EditorGui.Checkbox("lock##target", ref lockTarget) && !lockTarget)
			{
				UpdateTarget();
			}

			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep this target while selecting other objects");
			ImGui.Separator();
		}

		// ---------------------------------------------------------------- target, preview, record

		private void UpdateTarget()
		{
			if (lockTarget && target != null && !target.IsDestroyed)
			{
				return;
			}

			GameObject selected = editor.Selected;
			if (selected == null || selected.IsDestroyed)
			{
				if (target != null && target.IsDestroyed)
				{
					EndPreview();
					target = null;
				}

				return;
			}

			// The animator's object when the selection is inside an animated hierarchy.
			Animator animator = selected.GetComponentInParent<Animator>(true);
			GameObject candidate = animator != null ? animator.gameObject : selected;
			if (candidate != target)
			{
				EndPreview();
				target = candidate;
			}
		}

		private void BeginPreview()
		{
			if (target == null || target.IsDestroyed || editor.IsPlaying || Clip == null)
			{
				return;
			}

			EndPreview();
			previewSession = new PreviewSession(Clip, target);
			preview = true;
		}

		private void EndPreview()
		{
			previewSession?.Restore();
			previewSession = null;
			preview = false;
			record = false;
			playing = false;
		}

		private void ApplyPreview()
		{
			if (previewSession == null)
			{
				return;
			}

			if (editor.IsPlaying || target == null || target.IsDestroyed)
			{
				EndPreview();
				return;
			}

			previewSession.Sample(Time);
			editor.NotifySceneChanged();
		}

		private void AdvancePlayback()
		{
			if (!playing)
			{
				return;
			}

			float length = Math.Max(Clip.length, 1f / Math.Max(Clip.frameRate, 1f));
			float next = Time + ImGui.GetIO().DeltaTime;
			if (next > length)
			{
				if (Clip.loop)
				{
					next -= length;
				}
				else
				{
					next = length;
					playing = false;
				}
			}

			Time = next;
		}

		/// <summary>Record mode: properties that differ from what the preview wrote last get keys at the playhead.</summary>
		private void DetectRecordedChanges()
		{
			List<PreviewSession.Change> changes = previewSession.DetectChanges();
			if (changes.Count == 0)
			{
				return;
			}

			AnimationClip clip = Clip;
			document.Edit("Record keys", () =>
			{
				foreach (PreviewSession.Change change in changes)
				{
					AnimationBinding binding = clip.bindings[change.Binding];
					if (binding.kind == AnimationValueKind.String)
					{
						SetDiscreteKey(binding, Time, change.Discrete as string, null);
					}
					else if (binding.kind == AnimationValueKind.Reference)
					{
						SetDiscreteKey(binding, Time, null, change.Discrete);
					}
					else
					{
						for (int c = 0; c < change.Values.Length; c++)
						{
							binding.channels[c].AddKey(Time, change.Values[c]);
						}
					}
				}
			});
			valueRangeDirty = true;
		}

		private static void SetDiscreteKey(AnimationBinding binding, float time, string text, object reference)
		{
			int index = binding.FindDiscreteKey(time);
			if (binding.kind == AnimationValueKind.String)
			{
				if (index >= 0) binding.stringKeys[index].value = text ?? "";
				else binding.stringKeys.Add(new StringKey { time = time, value = text ?? "" });
			}
			else
			{
				var key = index >= 0 ? binding.referenceKeys[index] : new ReferenceKey { time = time };
				key.reference = reference as EngineObject;
				key.asset = reference as IAsset;
				if (index < 0) binding.referenceKeys.Add(key);
			}

			binding.SortDiscreteKeys();
		}

		// ---------------------------------------------------------------- rows (left pane)

		private void DrawRows()
		{
			AnimationClip clip = Clip;
			rowLayout.Clear();
			ImGui.Dummy(new NVector2(0, RulerHeight - ImGui.GetStyle().ItemSpacing.Y));
			rowsMin = ImGui.GetCursorScreenPos();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			float width = ImGui.GetContentRegionAvail().X;
			float y = 0f;
			if (clip.bindings.Count == 0)
			{
				ImGui.TextDisabled("No properties yet.");
				ImGui.TextWrapped("Add Property picks fields of the target hierarchy; Record keys what you change.");
				return;
			}

			for (int b = 0; b < clip.bindings.Count; b++)
			{
				AnimationBinding binding = clip.bindings[b];
				ImGui.PushID(b);
				bool selected = selectedRows.Contains(b);
				bool expandable = binding.ChannelCount > 1;
				bool expanded = expandable && expandedRows.Contains(b);
				NVector2 rowMin = ImGui.GetCursorScreenPos();
				if (selected)
				{
					NVector4 selection = EditorStyle.Selection;
					draw.AddRectFilled(rowMin, rowMin + new NVector2(width, RowHeight), ImGui.GetColorU32(new NVector4(selection.X, selection.Y, selection.Z, 0.35f)));
				}

				if (expandable)
				{
					if (EditorGui.ArrowButton("##expand", expanded ? ImGuiDir.Down : ImGuiDir.Right))
					{
						if (expanded) expandedRows.Remove(b);
						else expandedRows.Add(b);
					}

					ImGui.SameLine();
				}
				else
				{
					ImGui.Dummy(new NVector2(ImGui.GetFrameHeight(), 0));
					ImGui.SameLine();
				}

				string label = RowLabel(binding);
				bool missing = previewSession != null && previewSession.IsUnresolved(b);
				if (missing) ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Warning);
				if (EditorGui.Selectable(label + "##row", selected, ImGuiSelectableFlags.AllowOverlap, new NVector2(width - ImGui.GetFrameHeight() - 90f, RowHeight - 4f)))
				{
					bool additive = ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift;
					if (!additive) selectedRows.Clear();
					if (additive && selected) selectedRows.Remove(b);
					else selectedRows.Add(b);
					valueRangeDirty = true;
				}

				if (missing) ImGui.PopStyleColor();
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(label + "\n" + binding.kind + (missing ? "\nNot found on the target" : "") + "\nRight click for options");
				}

				if (EditorGui.BeginPopupContextItem("##row_context"))
				{
					if (EditorMenu.Item("Key Current Value", "", false, previewSession != null)) KeyBinding(b);
					if (EditorMenu.Item("Add Key at Playhead")) AddKeyAtPlayhead(b);
					if (EditorMenu.Item("Remove Property")) RemoveBinding(b);
					ImGui.EndPopup();
				}

				ImGui.SameLine();
				DrawRowValue(binding, b, -1, 86f);
				rowLayout.Add((b, -1, rowMin.Y - rowsMin.Y));
				y += RowHeight;
				if (expanded)
				{
					for (int c = 0; c < binding.ChannelCount; c++)
					{
						ImGui.PushID(c);
						NVector2 channelMin = ImGui.GetCursorScreenPos();
						ImGui.Dummy(new NVector2(ImGui.GetFrameHeight() * 2f, 0));
						ImGui.SameLine();
						ImGui.PushStyleColor(ImGuiCol.Text, ChannelColors[c % ChannelColors.Length]);
						ImGui.TextUnformatted(AnimationBinding.ChannelName(binding.kind, c));
						ImGui.PopStyleColor();
						ImGui.SameLine(width - 90f);
						DrawRowValue(binding, b, c, 86f);
						rowLayout.Add((b, c, channelMin.Y - rowsMin.Y));
						y += RowHeight;
						ImGui.PopID();
					}
				}

				ImGui.PopID();
			}
		}

		/// <summary>"Body : Position" for transform channels, "Body : Type.property" otherwise (the tooltip has the full name).</summary>
		private static string RowLabel(AnimationBinding binding)
		{
			string prefix = string.IsNullOrEmpty(binding.path) ? "" : binding.path + " : ";
			if (AnimationBinder.IsTransformProperty(binding.type, binding.property))
			{
				switch (binding.property)
				{
					case AnimationBinder.LocalPosition: return prefix + "Position";
					case AnimationBinder.LocalRotation: return prefix + "Rotation (quaternion)";
					case AnimationBinder.LocalEulerAngles: return prefix + "Rotation";
					case AnimationBinder.LocalScale: return prefix + "Scale";
				}
			}

			string type = string.IsNullOrEmpty(binding.type) ? "" : AnimationBinder.ShortTypeName(binding.type) + ".";
			return prefix + type + binding.property;
		}

		/// <summary>The value at the playhead of a binding (or one channel): an editable field writing a key.</summary>
		private void DrawRowValue(AnimationBinding binding, int bindingIndex, int channel, float width)
		{
			ImGui.SetNextItemWidth(width);
			if (binding.kind == AnimationValueKind.String)
			{
				string value = binding.EvaluateString(Time) ?? "";
				if (EditorGui.InputText("##value", ref value, 256, ImGuiInputTextFlags.EnterReturnsTrue))
				{
					document.Edit("Set key", () => SetDiscreteKey(binding, Time, value, null));
				}

				return;
			}

			if (binding.kind == AnimationValueKind.Reference)
			{
				object value = binding.EvaluateReference(Time);
				ImGui.PushItemWidth(width);
				bool changed = ObjectField.Draw(typeof(EngineObject), ref value, context);
				ImGui.PopItemWidth();
				if (changed)
				{
					document.Edit("Set key", () => SetDiscreteKey(binding, Time, null, value));
				}

				return;
			}

			if (binding.ChannelCount == 1 || channel >= 0)
			{
				int c = Math.Max(channel, 0);
				FloatCurve curve = binding.channels[c];
				float value = curve.Length > 0 ? curve.Evaluate(Time) : 0f;
				bool hasKey = curve.IndexOf(Time) >= 0;
				if (hasKey) ImGui.PushStyleColor(ImGuiCol.FrameBg, new NVector4(0.5f, 0.35f, 0.15f, 0.6f));
				bool changed = EditorGui.DragFloat("##value", ref value, 0.01f, 0f, 0f, binding.kind == AnimationValueKind.Bool ? "%.0f" : "%.3f");
				if (hasKey) ImGui.PopStyleColor();
				if (ImGui.IsItemActivated()) document.BeginEdit("Edit key");
				if (changed)
				{
					curve.AddKey(Time, value);
					binding.EnsureChannels();
					Clip.MarkChanged();
					valueRangeDirty = true;
				}

				if (ImGui.IsItemDeactivatedAfterEdit()) document.EndEdit();
				return;
			}

			// Several channels: a summary, click keys all.
			var text = new System.Text.StringBuilder();
			for (int c = 0; c < binding.ChannelCount; c++)
			{
				if (c > 0) text.Append(' ');
				text.Append(binding.channels[c].Length > 0 ? binding.channels[c].Evaluate(Time).ToString("0.##") : "-");
			}

			if (EditorGui.SmallButton(text.ToString() + "##summary"))
			{
				AddKeyAtPlayhead(bindingIndex);
			}

			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click: key these values at the playhead (expand the row to edit channels)");
		}

		// ---------------------------------------------------------------- timeline

		private void DrawTimeline()
		{
			AnimationClip clip = Clip;
			timelineMin = ImGui.GetCursorScreenPos();
			timelineSize = ImGui.GetContentRegionAvail();
			if (timelineSize.X < 20f || timelineSize.Y < 20f)
			{
				return;
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(timelineMin, timelineMin + timelineSize, ImGui.GetColorU32(new NVector4(0.1f, 0.1f, 0.11f, 1f)));
			EditorGui.InvisibleButton("##timeline_canvas", timelineSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
			bool hovered = ImGui.IsItemHovered();
			bool focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			draw.PushClipRect(timelineMin, timelineMin + timelineSize, true);
			float length = Math.Max(clip.length, 1f / Math.Max(clip.frameRate, 1f));
			float eventsTop = timelineMin.Y + timelineSize.Y - EventsHeight;

			// Clip range shading and the frame grid.
			float startX = TimeToX(0f);
			float endX = TimeToX(length);
			draw.AddRectFilled(new NVector2(Math.Max(startX, timelineMin.X), timelineMin.Y + RulerHeight), new NVector2(Math.Min(endX, timelineMin.X + timelineSize.X), eventsTop), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.03f)));
			DrawRuler(draw, clip, eventsTop);
			if (mode == ViewMode.DopeSheet)
			{
				DrawDopeSheet(draw, clip, eventsTop);
			}
			else
			{
				DrawCurves(draw, clip, eventsTop);
			}

			DrawEventsLane(draw, clip, eventsTop);

			// Playhead.
			float playX = TimeToX(Time);
			draw.AddLine(new NVector2(playX, timelineMin.Y), new NVector2(playX, timelineMin.Y + timelineSize.Y), ImGui.GetColorU32(EditorStyle.Accent), 2f);
			draw.AddTriangleFilled(new NVector2(playX - 6f, timelineMin.Y), new NVector2(playX + 6f, timelineMin.Y), new NVector2(playX, timelineMin.Y + 8f), ImGui.GetColorU32(EditorStyle.Accent));
			string timeText = Time.ToString("0.000") + " s  f" + clip.TimeToFrame(Time);
			NVector2 timeSize = ImGui.CalcTextSize(timeText);
			var timeMin = new NVector2(playX + 6f, eventsTop - timeSize.Y - 4f);
			draw.AddRectFilled(timeMin - new NVector2(3f, 1f), timeMin + timeSize + new NVector2(3f, 1f), ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.6f)), 3f);
			draw.AddText(timeMin, ImGui.GetColorU32(EditorStyle.Accent), timeText);
			if (boxSelecting)
			{
				NVector2 a = NVector2.Min(boxStart, ImGui.GetMousePos());
				NVector2 b = NVector2.Max(boxStart, ImGui.GetMousePos());
				draw.AddRectFilled(a, b, ImGui.GetColorU32(new NVector4(0.3f, 0.5f, 0.9f, 0.15f)));
				draw.AddRect(a, b, ImGui.GetColorU32(new NVector4(0.3f, 0.5f, 0.9f, 0.8f)));
			}

			draw.PopClipRect();
			HandleTimelineInput(hovered, focused, eventsTop);
			DrawKeyContextMenu();
		}

		private float TimeToX(float time)
		{
			return timelineMin.X + 10f + (time - scrollTime) * pixelsPerSecond;
		}

		private float XToTime(float x)
		{
			return (x - timelineMin.X - 10f) / pixelsPerSecond + scrollTime;
		}

		private float ValueToY(float value, float top, float bottom)
		{
			float range = Math.Max(valueMax - valueMin, 1e-5f);
			return bottom - (value - valueMin) / range * (bottom - top);
		}

		private float YToValue(float y, float top, float bottom)
		{
			float range = Math.Max(valueMax - valueMin, 1e-5f);
			return valueMin + (bottom - y) / (bottom - top) * range;
		}

		private void DrawRuler(ImDrawListPtr draw, AnimationClip clip, float eventsTop)
		{
			draw.AddRectFilled(timelineMin, new NVector2(timelineMin.X + timelineSize.X, timelineMin.Y + RulerHeight), ImGui.GetColorU32(new NVector4(0.16f, 0.16f, 0.18f, 1f)));
			float rate = Math.Max(clip.frameRate, 1f);
			float frameWidth = pixelsPerSecond / rate;
			int frameStep = 1;
			while (frameWidth * frameStep < 6f)
			{
				frameStep *= 2;
			}

			int labelStep = frameStep;
			while (frameWidth * labelStep < 50f)
			{
				labelStep *= 2;
			}

			int firstFrame = Math.Max(0, (int)Math.Floor(XToTime(timelineMin.X) * rate));
			int lastFrame = (int)Math.Ceiling(XToTime(timelineMin.X + timelineSize.X) * rate);
			uint minor = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.08f));
			uint major = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.18f));
			uint text = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.6f));
			for (int frame = firstFrame - firstFrame % frameStep; frame <= lastFrame; frame += frameStep)
			{
				float x = TimeToX(frame / rate);
				bool label = frame % labelStep == 0;
				draw.AddLine(new NVector2(x, timelineMin.Y + (label ? RulerHeight * 0.4f : RulerHeight * 0.7f)), new NVector2(x, timelineMin.Y + RulerHeight), label ? major : minor);
				if (label)
				{
					draw.AddLine(new NVector2(x, timelineMin.Y + RulerHeight), new NVector2(x, eventsTop), minor);
					float seconds = frame / rate;
					draw.AddText(new NVector2(x + 3f, timelineMin.Y + 2f), text, seconds.ToString("0.##") + "s");
				}
			}

			float endX = TimeToX(Math.Max(clip.length, 0f));
			draw.AddLine(new NVector2(endX, timelineMin.Y), new NVector2(endX, eventsTop), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.35f)), 1f);
		}

		private void DrawDopeSheet(ImDrawListPtr draw, AnimationClip clip, float eventsTop)
		{
			uint keyColor = ImGui.GetColorU32(new NVector4(0.85f, 0.85f, 0.9f, 1f));
			uint selectedColor = ImGui.GetColorU32(EditorStyle.Accent);
			uint rowLine = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.05f));
			foreach ((int b, int channel, float y) in rowLayout)
			{
				float rowY = timelineMin.Y + RulerHeight + y + RowHeight * 0.5f;
				if (rowY > eventsTop)
				{
					break;
				}

				draw.AddLine(new NVector2(timelineMin.X, rowY + RowHeight * 0.5f), new NVector2(timelineMin.X + timelineSize.X, rowY + RowHeight * 0.5f), rowLine);
				AnimationBinding binding = clip.bindings[b];
				if (channel < 0)
				{
					// Combined row: a key wherever any channel (or discrete key) has one.
					var times = new SortedSet<float>();
					foreach (FloatCurve curve in binding.channels)
					{
						foreach (float t in curve.Times) times.Add(t);
					}

					foreach (StringKey key in binding.stringKeys) times.Add(key.time);
					foreach (ReferenceKey key in binding.referenceKeys) times.Add(key.time);
					foreach (float t in times)
					{
						bool selected = IsRowKeySelected(b, t);
						DrawKey(draw, new NVector2(TimeToX(t), rowY), selected ? selectedColor : keyColor, binding.ChannelCount > 1 ? KeySize + 1f : KeySize);
					}
				}
				else
				{
					FloatCurve curve = binding.channels[channel];
					uint color = ImGui.GetColorU32(ChannelColors[channel % ChannelColors.Length]);
					for (int k = 0; k < curve.Length; k++)
					{
						bool selected = selectedKeys.Contains(new KeyRef { Binding = b, Channel = channel, Time = curve.Times[k] });
						DrawKey(draw, new NVector2(TimeToX(curve.Times[k]), rowY), selected ? selectedColor : color, KeySize);
					}
				}
			}
		}

		private static void DrawKey(ImDrawListPtr draw, NVector2 center, uint color, float size)
		{
			draw.AddQuadFilled(center + new NVector2(0f, -size), center + new NVector2(size, 0f), center + new NVector2(0f, size), center + new NVector2(-size, 0f), color);
			draw.AddQuad(center + new NVector2(0f, -size), center + new NVector2(size, 0f), center + new NVector2(0f, size), center + new NVector2(-size, 0f), ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.6f)), 1f);
		}

		private bool IsRowKeySelected(int binding, float time)
		{
			AnimationBinding b = Clip.bindings[binding];
			int channels = Math.Max(b.ChannelCount, 1);
			for (int c = 0; c < channels; c++)
			{
				if (selectedKeys.Contains(new KeyRef { Binding = binding, Channel = c, Time = time }))
				{
					return true;
				}
			}

			return false;
		}

		private void DrawCurves(ImDrawListPtr draw, AnimationClip clip, float eventsTop)
		{
			float top = timelineMin.Y + RulerHeight + 10f;
			float bottom = eventsTop - 10f;
			if (valueRangeDirty)
			{
				FitValueRange(clip);
				valueRangeDirty = false;
			}

			// Value grid.
			uint grid = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.06f));
			uint text = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.45f));
			float range = Math.Max(valueMax - valueMin, 1e-5f);
			float step = (float)Math.Pow(10, Math.Floor(Math.Log10(range / 4f)));
			if (range / step > 8f) step *= 2f;
			if (range / step > 8f) step *= 2.5f;
			for (float v = (float)Math.Ceiling(valueMin / step) * step; v <= valueMax; v += step)
			{
				float y = ValueToY(v, top, bottom);
				draw.AddLine(new NVector2(timelineMin.X, y), new NVector2(timelineMin.X + timelineSize.X, y), Math.Abs(v) < 1e-6f ? ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.2f)) : grid);
				draw.AddText(new NVector2(timelineMin.X + 4f, y - ImGui.GetTextLineHeight()), text, v.ToString("0.###"));
			}

			float visibleStart = XToTime(timelineMin.X);
			float visibleEnd = XToTime(timelineMin.X + timelineSize.X);
			uint selectedColor = ImGui.GetColorU32(EditorStyle.Accent);
			KeyRef? single = selectedKeys.Count == 1 ? (KeyRef?)new List<KeyRef>(selectedKeys)[0] : null;
			foreach (int b in VisibleCurveBindings())
			{
				AnimationBinding binding = clip.bindings[b];
				for (int c = 0; c < binding.ChannelCount; c++)
				{
					FloatCurve curve = binding.channels[c];
					if (curve.Length == 0)
					{
						continue;
					}

					NVector4 colorV = binding.ChannelCount > 1 ? ChannelColors[c % ChannelColors.Length] : new NVector4(0.45f, 0.8f, 1f, 1f);
					if (selectedRows.Count > 0 && !selectedRows.Contains(b))
					{
						colorV.W = 0.35f;
					}

					uint color = ImGui.GetColorU32(colorV);
					// The curve as a polyline (segment by segment for the steps).
					int samples = Math.Max(16, (int)(timelineSize.X / 3f));
					NVector2 previous = NVector2.Zero;
					for (int i = 0; i <= samples; i++)
					{
						float t = visibleStart + (visibleEnd - visibleStart) * i / samples;
						float value = curve.Evaluate(t);
						var point = new NVector2(TimeToX(t), ValueToY(value, top, bottom));
						if (i > 0)
						{
							draw.AddLine(previous, point, color, 1.5f);
						}

						previous = point;
					}

					for (int k = 0; k < curve.Length; k++)
					{
						var key = new KeyRef { Binding = b, Channel = c, Time = curve.Times[k] };
						bool selected = selectedKeys.Contains(key);
						var center = new NVector2(TimeToX(curve.Times[k]), ValueToY(curve.Values[k], top, bottom));
						DrawKey(draw, center, selected ? selectedColor : color, KeySize);
						if (single != null && single.Value.Equals(key) && (TangentMode)curve.Modes[k] != TangentMode.Constant)
						{
							// Tangent handles of the one selected key.
							float handle = 50f;
							NVector2 inDirection = NVector2.Normalize(new NVector2(pixelsPerSecond, -curve.InTangents[k] * pixelsPerSecond / range * (bottom - top) / pixelsPerSecond * 1f));
							NVector2 outDirection = NVector2.Normalize(new NVector2(pixelsPerSecond, -curve.OutTangents[k] * pixelsPerSecond / range * (bottom - top) / pixelsPerSecond * 1f));
							inDirection = TangentDirection(curve.InTangents[k], top, bottom);
							outDirection = TangentDirection(curve.OutTangents[k], top, bottom);
							NVector2 inHandle = center - inDirection * handle;
							NVector2 outHandle = center + outDirection * handle;
							uint handleColor = ImGui.GetColorU32(new NVector4(0.85f, 0.85f, 0.85f, 0.9f));
							if (k > 0) { draw.AddLine(center, inHandle, handleColor); draw.AddCircleFilled(inHandle, 4f, handleColor, 10); }
							if (k < curve.Length - 1) { draw.AddLine(center, outHandle, handleColor); draw.AddCircleFilled(outHandle, 4f, handleColor, 10); }
						}
					}
				}
			}
		}

		private NVector2 TangentDirection(float slope, float top, float bottom)
		{
			// Slope in value per second to screen: x per second = pixelsPerSecond, y per value = (bottom - top) / range.
			float range = Math.Max(valueMax - valueMin, 1e-5f);
			var direction = new NVector2(pixelsPerSecond, -slope * (bottom - top) / range);
			float length = direction.Length();
			return length > 1e-6f ? direction / length : new NVector2(1f, 0f);
		}

		private IEnumerable<int> VisibleCurveBindings()
		{
			AnimationClip clip = Clip;
			for (int b = 0; b < clip.bindings.Count; b++)
			{
				if (clip.bindings[b].ChannelCount > 0 && (selectedRows.Count == 0 || selectedRows.Contains(b)))
				{
					yield return b;
				}
			}
		}

		private void FitValueRange(AnimationClip clip)
		{
			float min = float.MaxValue;
			float max = float.MinValue;
			foreach (int b in VisibleCurveBindings())
			{
				if (selectedRows.Count > 0 && !selectedRows.Contains(b))
				{
					continue;
				}

				foreach (FloatCurve curve in clip.bindings[b].channels)
				{
					foreach (float v in curve.Values)
					{
						min = Math.Min(min, v);
						max = Math.Max(max, v);
					}
				}
			}

			if (min > max)
			{
				min = -1f;
				max = 1f;
			}

			float pad = Math.Max((max - min) * 0.15f, 0.1f);
			valueMin = min - pad;
			valueMax = max + pad;
		}

		private void DrawEventsLane(ImDrawListPtr draw, AnimationClip clip, float eventsTop)
		{
			draw.AddRectFilled(new NVector2(timelineMin.X, eventsTop), timelineMin + timelineSize, ImGui.GetColorU32(new NVector4(0.14f, 0.14f, 0.16f, 1f)));
			draw.AddLine(new NVector2(timelineMin.X, eventsTop), new NVector2(timelineMin.X + timelineSize.X, eventsTop), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.15f)));
			draw.AddText(new NVector2(timelineMin.X + 4f, eventsTop + 4f), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.35f)), "events");
			for (int i = 0; i < clip.events.Count; i++)
			{
				AnimationEvent e = clip.events[i];
				float x = TimeToX(e.time);
				bool selected = selectedEvent == i;
				uint color = ImGui.GetColorU32(selected ? EditorStyle.Accent : new NVector4(0.95f, 0.75f, 0.35f, 1f));
				float cy = eventsTop + EventsHeight * 0.5f;
				draw.AddTriangleFilled(new NVector2(x - 6f, cy - 6f), new NVector2(x + 6f, cy - 6f), new NVector2(x, cy + 6f), color);
				// The name, unless the next marker would cover it (hover shows it anyway).
				float room = i + 1 < clip.events.Count ? TimeToX(clip.events[i + 1].time) - x - 10f : float.MaxValue;
				float textWidth = ImGui.CalcTextSize(e.function).X;
				if (selected || textWidth + 8f <= room)
				{
					draw.AddText(new NVector2(x + 8f, cy - ImGui.GetTextLineHeight() * 0.5f), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, selected ? 1f : 0.8f)), e.function);
				}

				if (Math.Abs(ImGui.GetMousePos().X - x) <= 8f && ImGui.GetMousePos().Y >= eventsTop && ImGui.IsWindowHovered())
				{
					ImGui.SetTooltip(e.function + " @ " + e.time.ToString("0.###") + " s");
				}
			}
		}

		// ---------------------------------------------------------------- timeline input

		private enum TimelineHit
		{
			None,
			Ruler,
			Key,
			Event,
			EventsLane,
			TangentIn,
			TangentOut
		}

		private struct Hit
		{
			public TimelineHit Kind;
			public KeyRef Key;
			public int Event;
		}

		private Hit HitTest(NVector2 mouse, float eventsTop)
		{
			AnimationClip clip = Clip;
			if (mouse.Y < timelineMin.Y + RulerHeight)
			{
				return new Hit { Kind = TimelineHit.Ruler };
			}

			if (mouse.Y >= eventsTop)
			{
				for (int i = clip.events.Count - 1; i >= 0; i--)
				{
					if (Math.Abs(TimeToX(clip.events[i].time) - mouse.X) <= 8f)
					{
						return new Hit { Kind = TimelineHit.Event, Event = i };
					}
				}

				return new Hit { Kind = TimelineHit.EventsLane };
			}

			if (mode == ViewMode.DopeSheet)
			{
				foreach ((int b, int channel, float y) in rowLayout)
				{
					float rowY = timelineMin.Y + RulerHeight + y + RowHeight * 0.5f;
					if (Math.Abs(mouse.Y - rowY) > RowHeight * 0.5f)
					{
						continue;
					}

					AnimationBinding binding = clip.bindings[b];
					if (channel < 0)
					{
						var times = new SortedSet<float>();
						foreach (FloatCurve curve in binding.channels) foreach (float t in curve.Times) times.Add(t);
						foreach (StringKey key in binding.stringKeys) times.Add(key.time);
						foreach (ReferenceKey key in binding.referenceKeys) times.Add(key.time);
						foreach (float t in times)
						{
							if (Math.Abs(TimeToX(t) - mouse.X) <= KeySize + 3f)
							{
								return new Hit { Kind = TimelineHit.Key, Key = new KeyRef { Binding = b, Channel = -1, Time = t } };
							}
						}
					}
					else
					{
						foreach (float t in binding.channels[channel].Times)
						{
							if (Math.Abs(TimeToX(t) - mouse.X) <= KeySize + 3f)
							{
								return new Hit { Kind = TimelineHit.Key, Key = new KeyRef { Binding = b, Channel = channel, Time = t } };
							}
						}
					}
				}
			}
			else
			{
				float top = timelineMin.Y + RulerHeight + 10f;
				float bottom = eventsTop - 10f;
				if (selectedKeys.Count == 1)
				{
					KeyRef single = new List<KeyRef>(selectedKeys)[0];
					FloatCurve curve = clip.bindings[single.Binding].channels[single.Channel];
					int k = curve.IndexOf(single.Time);
					if (k >= 0)
					{
						var center = new NVector2(TimeToX(curve.Times[k]), ValueToY(curve.Values[k], top, bottom));
						NVector2 inHandle = center - TangentDirection(curve.InTangents[k], top, bottom) * 50f;
						NVector2 outHandle = center + TangentDirection(curve.OutTangents[k], top, bottom) * 50f;
						if (k > 0 && NVector2.DistanceSquared(mouse, inHandle) <= 49f) return new Hit { Kind = TimelineHit.TangentIn, Key = single };
						if (k < curve.Length - 1 && NVector2.DistanceSquared(mouse, outHandle) <= 49f) return new Hit { Kind = TimelineHit.TangentOut, Key = single };
					}
				}

				foreach (int b in VisibleCurveBindings())
				{
					AnimationBinding binding = clip.bindings[b];
					for (int c = 0; c < binding.ChannelCount; c++)
					{
						FloatCurve curve = binding.channels[c];
						for (int k = 0; k < curve.Length; k++)
						{
							var center = new NVector2(TimeToX(curve.Times[k]), ValueToY(curve.Values[k], top, bottom));
							if (NVector2.DistanceSquared(mouse, center) <= (KeySize + 3f) * (KeySize + 3f))
							{
								return new Hit { Kind = TimelineHit.Key, Key = new KeyRef { Binding = b, Channel = c, Time = curve.Times[k] } };
							}
						}
					}
				}
			}

			return new Hit { Kind = TimelineHit.None };
		}

		private void HandleTimelineInput(bool hovered, bool focused, float eventsTop)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			NVector2 mouse = ImGui.GetMousePos();
			AnimationClip clip = Clip;
			float top = timelineMin.Y + RulerHeight + 10f;
			float bottom = eventsTop - 10f;

			// Zoom (time; Ctrl: values in curve mode) around the cursor, pan with the middle button.
			if (hovered && Math.Abs(io.MouseWheel) > 0f)
			{
				if (mode == ViewMode.Curves && io.KeyCtrl)
				{
					float center = YToValue(mouse.Y, top, bottom);
					float factor = io.MouseWheel > 0f ? 1f / 1.15f : 1.15f;
					valueMin = center + (valueMin - center) * factor;
					valueMax = center + (valueMax - center) * factor;
				}
				else
				{
					float before = XToTime(mouse.X);
					pixelsPerSecond = Math.Clamp(pixelsPerSecond * (io.MouseWheel > 0f ? 1.15f : 1f / 1.15f), 20f, 4000f);
					float after = XToTime(mouse.X);
					scrollTime += before - after;
				}
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
			{
				panning = true;
				panStartMouse = mouse;
				panStartScroll = scrollTime;
				panStartValueMin = valueMin;
				panStartValueMax = valueMax;
			}

			if (panning && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
			{
				scrollTime = panStartScroll - (mouse.X - panStartMouse.X) / pixelsPerSecond;
				if (mode == ViewMode.Curves)
				{
					float perPixel = (valueMax - valueMin) / Math.Max(bottom - top, 1f);
					float delta = (mouse.Y - panStartMouse.Y) * perPixel;
					valueMin = panStartValueMin + delta;
					valueMax = panStartValueMax + delta;
				}
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
			{
				panning = false;
			}

			scrollTime = Math.Max(scrollTime, -0.2f);

			// Left button.
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				Hit hit = HitTest(mouse, eventsTop);
				bool additive = io.KeyCtrl || io.KeyShift;
				switch (hit.Kind)
				{
					case TimelineHit.Ruler:
						draggingPlayhead = true;
						SetTime(XToTime(mouse.X));
						break;
					case TimelineHit.Key:
					{
						var keys = new List<KeyRef>();
						if (hit.Key.Channel < 0)
						{
							int channels = Math.Max(clip.bindings[hit.Key.Binding].ChannelCount, 1);
							for (int c = 0; c < channels; c++)
							{
								keys.Add(new KeyRef { Binding = hit.Key.Binding, Channel = c, Time = hit.Key.Time });
							}
						}
						else
						{
							keys.Add(hit.Key);
						}

						bool anySelected = keys.Exists(selectedKeys.Contains);
						if (additive)
						{
							foreach (KeyRef key in keys)
							{
								if (anySelected) selectedKeys.Remove(key);
								else selectedKeys.Add(key);
							}
						}
						else if (!anySelected)
						{
							selectedKeys.Clear();
							foreach (KeyRef key in keys) selectedKeys.Add(key);
						}

						selectedEvent = -1;
						if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							SetTime(hit.Key.Time);
						}
						else
						{
							BeginKeyDrag(mouse);
						}

						break;
					}
					case TimelineHit.TangentIn:
					case TimelineHit.TangentOut:
						tangentDrag = hit.Kind == TimelineHit.TangentIn ? 0 : 1;
						tangentKey = hit.Key;
						document.BeginEdit("Edit tangent");
						break;
					case TimelineHit.Event:
						selectedEvent = hit.Event;
						selectedKeys.Clear();
						draggingEvent = hit.Event;
						eventDragStart = clip.events[hit.Event].time;
						dragStartMouse = mouse;
						if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							SetTime(clip.events[hit.Event].time);
						}

						break;
					case TimelineHit.EventsLane:
						if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							AddEvent(Snap(XToTime(mouse.X)));
						}
						else
						{
							selectedEvent = -1;
						}

						break;
					default:
						if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && mode == ViewMode.DopeSheet)
						{
							// Double click on a row: a key at that time with the value there.
							foreach ((int b, int channel, float y) in rowLayout)
							{
								float rowY = timelineMin.Y + RulerHeight + y + RowHeight * 0.5f;
								if (Math.Abs(mouse.Y - rowY) <= RowHeight * 0.5f)
								{
									AddKeyAt(b, channel, Snap(XToTime(mouse.X)));
									break;
								}
							}
						}
						else
						{
							if (!additive)
							{
								selectedKeys.Clear();
								selectedEvent = -1;
							}

							boxSelecting = true;
							boxStart = mouse;
						}

						break;
				}
			}

			if (draggingPlayhead && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				SetTime(XToTime(mouse.X));
			}

			if (draggingKeys && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				DragKeys(mouse, top, bottom);
			}

			if (tangentDrag >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				DragTangent(mouse, top, bottom);
			}

			if (draggingEvent >= 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left) && draggingEvent < clip.events.Count)
			{
				float time = Snap(Math.Max(0f, eventDragStart + (mouse.X - dragStartMouse.X) / pixelsPerSecond));
				if (Math.Abs(clip.events[draggingEvent].time - time) > 1e-6f)
				{
					document.BeginEdit("Move event");
					clip.events[draggingEvent].time = time;
				}
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				draggingPlayhead = false;
				if (draggingKeys)
				{
					EndKeyDrag();
				}

				if (tangentDrag >= 0)
				{
					tangentDrag = -1;
					document.EndEdit();
				}

				if (draggingEvent >= 0)
				{
					int index = draggingEvent;
					draggingEvent = -1;
					AnimationEvent moved = index < clip.events.Count ? clip.events[index] : null;
					document.EndEdit();
					if (moved != null)
					{
						clip.events.Sort((a, b) => a.time.CompareTo(b.time));
						selectedEvent = clip.events.IndexOf(moved);
					}
				}

				if (boxSelecting)
				{
					boxSelecting = false;
					BoxSelect(NVector2.Min(boxStart, mouse), NVector2.Max(boxStart, mouse), top, bottom);
				}
			}

			// Right click: key menu.
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				Hit hit = HitTest(mouse, eventsTop);
				if (hit.Kind == TimelineHit.Key)
				{
					if (!IsRowKeySelected(hit.Key.Binding, hit.Key.Time))
					{
						selectedKeys.Clear();
						int channels = hit.Key.Channel < 0 ? Math.Max(clip.bindings[hit.Key.Binding].ChannelCount, 1) : 1;
						for (int c = 0; c < channels; c++)
						{
							selectedKeys.Add(new KeyRef { Binding = hit.Key.Binding, Channel = hit.Key.Channel < 0 ? c : hit.Key.Channel, Time = hit.Key.Time });
						}
					}

					ImGui.OpenPopup("##key_context");
				}
				else if (hit.Kind == TimelineHit.Event)
				{
					selectedEvent = hit.Event;
					ImGui.OpenPopup("##event_context");
				}
				else if (hit.Kind == TimelineHit.None || hit.Kind == TimelineHit.EventsLane)
				{
					SetTime(XToTime(mouse.X));
					ImGui.OpenPopup("##timeline_context");
				}
			}

			// Keys.
			if (focused && hovered && !io.WantTextInput)
			{
				if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false))
				{
					if (selectedEvent >= 0) DeleteEvent(selectedEvent);
					else DeleteSelectedKeys();
				}
				else if (ImGui.IsKeyPressed(ImGuiKey.Space, false)) playing = !playing;
				else if (ImGui.IsKeyPressed(ImGuiKey.K, false)) KeyAll();
				else if (ImGui.IsKeyPressed(ImGuiKey.F, false)) FrameAll();
				else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, false)) SelectAllKeys();
				else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, false)) SetTime(Time - 1f / Math.Max(clip.frameRate, 1f));
				else if (ImGui.IsKeyPressed(ImGuiKey.RightArrow, false)) SetTime(Time + 1f / Math.Max(clip.frameRate, 1f));
				else if (ImGui.IsKeyPressed(ImGuiKey.Home, false)) SetTime(0f);
				else if (ImGui.IsKeyPressed(ImGuiKey.End, false)) SetTime(clip.length);
			}
		}

		private float Snap(float time)
		{
			if (!snapToFrames)
			{
				return Math.Max(0f, time);
			}

			float rate = Math.Max(Clip.frameRate, 1f);
			return Math.Max(0f, (float)Math.Round(time * rate) / rate);
		}

		private void BeginKeyDrag(NVector2 mouse)
		{
			draggingKeys = true;
			keysMoved = false;
			dragStartMouse = mouse;
			dragOrigins.Clear();
			AnimationClip clip = Clip;
			foreach (KeyRef key in selectedKeys)
			{
				AnimationBinding binding = clip.bindings[key.Binding];
				if (key.Channel < binding.channels.Count)
				{
					int index = binding.channels[key.Channel].IndexOf(key.Time);
					dragOrigins[key] = (key.Time, index >= 0 ? binding.channels[key.Channel].Values[index] : 0f);
				}
				else
				{
					dragOrigins[key] = (key.Time, 0f);
				}
			}
		}

		private void DragKeys(NVector2 mouse, float top, float bottom)
		{
			float deltaTime = (mouse.X - dragStartMouse.X) / pixelsPerSecond;
			float deltaValue = mode == ViewMode.Curves ? -(mouse.Y - dragStartMouse.Y) * (valueMax - valueMin) / Math.Max(bottom - top, 1f) : 0f;
			if (!keysMoved && Math.Abs(mouse.X - dragStartMouse.X) < 3f && Math.Abs(mouse.Y - dragStartMouse.Y) < 3f)
			{
				return;
			}

			if (!keysMoved)
			{
				document.BeginEdit("Move keys");
				keysMoved = true;
			}

			AnimationClip clip = Clip;
			// Every drag frame rebuilds from the origins (so snapping never drifts).
			var newSelection = new HashSet<KeyRef>();
			var byChannel = new Dictionary<(int, int), List<(float from, float to, float value)>>();
			foreach (KeyValuePair<KeyRef, (float time, float value)> entry in dragOrigins)
			{
				float to = Snap(entry.Value.time + deltaTime);
				var channelKey = (entry.Key.Binding, entry.Key.Channel);
				if (!byChannel.TryGetValue(channelKey, out List<(float, float, float)> list))
				{
					list = new List<(float, float, float)>();
					byChannel[channelKey] = list;
				}

				list.Add((entry.Value.time, to, entry.Value.value + deltaValue));
			}

			foreach (KeyValuePair<(int, int), List<(float from, float to, float value)>> entry in byChannel)
			{
				AnimationBinding binding = clip.bindings[entry.Key.Item1];
				if (binding.kind == AnimationValueKind.String || binding.kind == AnimationValueKind.Reference)
				{
					foreach ((float from, float to, float _) in entry.Value)
					{
						int index = binding.FindDiscreteKey(CurrentTimeOf(entry.Key, from));
						if (index >= 0)
						{
							if (binding.kind == AnimationValueKind.String) binding.stringKeys[index].time = to;
							else binding.referenceKeys[index].time = to;
						}

						newSelection.Add(new KeyRef { Binding = entry.Key.Item1, Channel = entry.Key.Item2, Time = to });
					}

					binding.SortDiscreteKeys();
					continue;
				}

				FloatCurve curve = binding.channels[entry.Key.Item2];
				// Remove the dragged keys (by their current times) and add them at the new times.
				var moved = new List<Keyframe>();
				foreach ((float from, float to, float value) in entry.Value)
				{
					int index = curve.IndexOf(CurrentTimeOf(entry.Key, from));
					if (index < 0)
					{
						continue;
					}

					Keyframe key = curve.GetKey(index);
					key.time = to;
					key.value = mode == ViewMode.Curves ? value : key.value;
					moved.Add(key);
					curve.RemoveKey(index);
				}

				foreach (Keyframe key in moved)
				{
					curve.AddKey(key);
					newSelection.Add(new KeyRef { Binding = entry.Key.Item1, Channel = entry.Key.Item2, Time = key.time });
				}

				curve.RecalculateTangents();
			}

			// The origins map to the new times for the next frame.
			var newOrigins = new Dictionary<KeyRef, (float, float)>();
			foreach (KeyValuePair<KeyRef, (float time, float value)> entry in dragOrigins)
			{
				newOrigins[new KeyRef { Binding = entry.Key.Binding, Channel = entry.Key.Channel, Time = Snap(entry.Value.time + deltaTime) }] = entry.Value;
			}

			currentTimes.Clear();
			foreach (KeyValuePair<KeyRef, (float time, float value)> entry in dragOrigins)
			{
				currentTimes[(entry.Key.Binding, entry.Key.Channel, entry.Value.time)] = Snap(entry.Value.time + deltaTime);
			}

			selectedKeys.Clear();
			foreach (KeyRef key in newSelection) selectedKeys.Add(key);
			Clip.MarkChanged();
		}

		private readonly Dictionary<(int, int, float), float> currentTimes = new Dictionary<(int, int, float), float>();

		private float CurrentTimeOf((int, int) channel, float originTime)
		{
			return currentTimes.TryGetValue((channel.Item1, channel.Item2, originTime), out float current) ? current : originTime;
		}

		private void EndKeyDrag()
		{
			draggingKeys = false;
			if (keysMoved)
			{
				document.EndEdit();
				valueRangeDirty = true;
			}

			currentTimes.Clear();
			dragOrigins.Clear();
			keysMoved = false;
		}

		private void DragTangent(NVector2 mouse, float top, float bottom)
		{
			AnimationClip clip = Clip;
			if (tangentKey.Binding >= clip.bindings.Count)
			{
				return;
			}

			FloatCurve curve = clip.bindings[tangentKey.Binding].channels[tangentKey.Channel];
			int k = curve.IndexOf(tangentKey.Time);
			if (k < 0)
			{
				return;
			}

			var center = new NVector2(TimeToX(curve.Times[k]), ValueToY(curve.Values[k], top, bottom));
			NVector2 delta = mouse - center;
			if (tangentDrag == 0) delta = -delta;
			if (delta.X < 1f) delta.X = 1f;
			float range = Math.Max(valueMax - valueMin, 1e-5f);
			float slope = -delta.Y / delta.X * range / Math.Max(bottom - top, 1f) * pixelsPerSecond;
			bool broken = (TangentMode)curve.Modes[k] == TangentMode.Broken;
			if (tangentDrag == 0) curve.InTangents[k] = slope;
			else curve.OutTangents[k] = slope;
			if (!broken)
			{
				curve.InTangents[k] = slope;
				curve.OutTangents[k] = slope;
				curve.Modes[k] = (byte)TangentMode.Free;
			}

			Clip.MarkChanged();
		}

		private void BoxSelect(NVector2 a, NVector2 b, float top, float bottom)
		{
			if (NVector2.DistanceSquared(a, b) < 16f)
			{
				return;
			}

			AnimationClip clip = Clip;
			if (mode == ViewMode.DopeSheet)
			{
				foreach ((int bi, int channel, float y) in rowLayout)
				{
					float rowY = timelineMin.Y + RulerHeight + y + RowHeight * 0.5f;
					if (rowY < a.Y || rowY > b.Y)
					{
						continue;
					}

					AnimationBinding binding = clip.bindings[bi];
					int channels = Math.Max(binding.ChannelCount, 1);
					for (int c = 0; c < channels; c++)
					{
						if (channel >= 0 && c != channel) continue;
						if (c < binding.channels.Count)
						{
							foreach (float t in binding.channels[c].Times)
							{
								float x = TimeToX(t);
								if (x >= a.X && x <= b.X) selectedKeys.Add(new KeyRef { Binding = bi, Channel = c, Time = t });
							}
						}
					}

					foreach (StringKey key in binding.stringKeys) if (TimeToX(key.time) >= a.X && TimeToX(key.time) <= b.X) selectedKeys.Add(new KeyRef { Binding = bi, Channel = 0, Time = key.time });
					foreach (ReferenceKey key in binding.referenceKeys) if (TimeToX(key.time) >= a.X && TimeToX(key.time) <= b.X) selectedKeys.Add(new KeyRef { Binding = bi, Channel = 0, Time = key.time });
				}
			}
			else
			{
				foreach (int bi in VisibleCurveBindings())
				{
					AnimationBinding binding = clip.bindings[bi];
					for (int c = 0; c < binding.ChannelCount; c++)
					{
						FloatCurve curve = binding.channels[c];
						for (int k = 0; k < curve.Length; k++)
						{
							var p = new NVector2(TimeToX(curve.Times[k]), ValueToY(curve.Values[k], top, bottom));
							if (p.X >= a.X && p.X <= b.X && p.Y >= a.Y && p.Y <= b.Y) selectedKeys.Add(new KeyRef { Binding = bi, Channel = c, Time = curve.Times[k] });
						}
					}
				}
			}
		}

		private void DrawKeyContextMenu()
		{
			AnimationClip clip = Clip;
			if (ImGui.BeginPopup("##key_context"))
			{
				if (EditorMenu.Item("Clamped Auto")) SetTangentMode(TangentMode.ClampedAuto);
				if (EditorMenu.Item("Linear")) SetTangentMode(TangentMode.Linear);
				if (EditorMenu.Item("Constant (step)")) SetTangentMode(TangentMode.Constant);
				if (EditorMenu.Item("Free (smooth)")) SetTangentMode(TangentMode.Free);
				if (EditorMenu.Item("Broken")) SetTangentMode(TangentMode.Broken);
				ImGui.Separator();
				if (EditorMenu.Item("Delete", "Del")) DeleteSelectedKeys();
				ImGui.EndPopup();
			}

			if (ImGui.BeginPopup("##event_context"))
			{
				if (EditorMenu.Item("Go to Event") && selectedEvent >= 0 && selectedEvent < clip.events.Count) SetTime(clip.events[selectedEvent].time);
				if (EditorMenu.Item("Duplicate") && selectedEvent >= 0 && selectedEvent < clip.events.Count)
				{
					AnimationEvent copy = clip.events[selectedEvent].Clone();
					copy.time = Snap(copy.time + 1f / Math.Max(clip.frameRate, 1f));
					document.Edit("Duplicate event", () => { clip.events.Add(copy); clip.events.Sort((x, y) => x.time.CompareTo(y.time)); });
					selectedEvent = clip.events.IndexOf(copy);
				}

				if (EditorMenu.Item("Delete", "Del")) DeleteEvent(selectedEvent);
				ImGui.EndPopup();
			}

			if (ImGui.BeginPopup("##timeline_context"))
			{
				if (EditorMenu.Item("Add Event Here")) AddEvent(Time);
				if (EditorMenu.Item("Key All Properties Here", "K")) KeyAll();
				if (EditorMenu.Item("Frame All", "F")) FrameAll();
				ImGui.EndPopup();
			}
		}

		// ---------------------------------------------------------------- editing commands

		private void SetTangentMode(TangentMode tangentMode)
		{
			if (selectedKeys.Count == 0)
			{
				return;
			}

			AnimationClip clip = Clip;
			document.Edit("Set tangents", () =>
			{
				foreach (KeyRef key in selectedKeys)
				{
					if (key.Binding >= clip.bindings.Count) continue;
					AnimationBinding binding = clip.bindings[key.Binding];
					if (key.Channel >= binding.channels.Count) continue;
					FloatCurve curve = binding.channels[key.Channel];
					int index = curve.IndexOf(key.Time);
					if (index < 0) continue;
					curve.Modes[index] = (byte)tangentMode;
					if (tangentMode == TangentMode.Free || tangentMode == TangentMode.Broken)
					{
						float slope = (curve.InTangents[index] + curve.OutTangents[index]) * 0.5f;
						if (tangentMode == TangentMode.Free)
						{
							curve.InTangents[index] = slope;
							curve.OutTangents[index] = slope;
						}
					}

					curve.RecalculateTangents();
				}
			});
		}

		private void DeleteSelectedKeys()
		{
			if (selectedKeys.Count == 0)
			{
				return;
			}

			AnimationClip clip = Clip;
			document.Edit("Delete keys", () =>
			{
				foreach (KeyRef key in selectedKeys)
				{
					if (key.Binding >= clip.bindings.Count) continue;
					AnimationBinding binding = clip.bindings[key.Binding];
					if (binding.kind == AnimationValueKind.String || binding.kind == AnimationValueKind.Reference)
					{
						int d = binding.FindDiscreteKey(key.Time);
						if (d >= 0)
						{
							if (binding.kind == AnimationValueKind.String) binding.stringKeys.RemoveAt(d);
							else binding.referenceKeys.RemoveAt(d);
						}

						continue;
					}

					if (key.Channel >= binding.channels.Count) continue;
					FloatCurve curve = binding.channels[key.Channel];
					int index = curve.IndexOf(key.Time);
					if (index >= 0)
					{
						curve.RemoveKey(index);
						curve.RecalculateTangents();
					}
				}
			});
			selectedKeys.Clear();
			valueRangeDirty = true;
		}

		private void SelectAllKeys()
		{
			AnimationClip clip = Clip;
			selectedKeys.Clear();
			for (int b = 0; b < clip.bindings.Count; b++)
			{
				AnimationBinding binding = clip.bindings[b];
				for (int c = 0; c < binding.channels.Count; c++)
				{
					foreach (float t in binding.channels[c].Times) selectedKeys.Add(new KeyRef { Binding = b, Channel = c, Time = t });
				}

				foreach (StringKey key in binding.stringKeys) selectedKeys.Add(new KeyRef { Binding = b, Channel = 0, Time = key.time });
				foreach (ReferenceKey key in binding.referenceKeys) selectedKeys.Add(new KeyRef { Binding = b, Channel = 0, Time = key.time });
			}
		}

		/// <summary>Keys every binding at the playhead with the target's current values (or the curve's value without a target).</summary>
		private void KeyAll()
		{
			AnimationClip clip = Clip;
			document.Edit("Key all", () =>
			{
				for (int b = 0; b < clip.bindings.Count; b++)
				{
					KeyBindingNow(b);
				}
			});
			valueRangeDirty = true;
		}

		private void KeyBinding(int bindingIndex)
		{
			document.Edit("Key property", () => KeyBindingNow(bindingIndex));
			valueRangeDirty = true;
		}

		private void KeyBindingNow(int bindingIndex)
		{
			AnimationClip clip = Clip;
			AnimationBinding binding = clip.bindings[bindingIndex];
			if (previewSession != null && previewSession.TryReadCurrent(bindingIndex, out float[] values, out object discrete))
			{
				if (binding.kind == AnimationValueKind.String) SetDiscreteKey(binding, Time, discrete as string, null);
				else if (binding.kind == AnimationValueKind.Reference) SetDiscreteKey(binding, Time, null, discrete);
				else
				{
					for (int c = 0; c < binding.ChannelCount && c < values.Length; c++)
					{
						binding.channels[c].AddKey(Time, values[c]);
					}
				}

				return;
			}

			AddKeyAtNow(bindingIndex, -1, Time);
		}

		private void AddKeyAtPlayhead(int bindingIndex)
		{
			AddKeyAt(bindingIndex, -1, Time);
		}

		private void AddKeyAt(int bindingIndex, int channel, float time)
		{
			document.Edit("Add key", () => AddKeyAtNow(bindingIndex, channel, time));
			valueRangeDirty = true;
		}

		/// <summary>A key at a time holding the curve's value there (no change of the motion, a handle to edit).</summary>
		private void AddKeyAtNow(int bindingIndex, int channel, float time)
		{
			AnimationBinding binding = Clip.bindings[bindingIndex];
			if (binding.kind == AnimationValueKind.String)
			{
				SetDiscreteKey(binding, time, binding.EvaluateString(time) ?? "", null);
				return;
			}

			if (binding.kind == AnimationValueKind.Reference)
			{
				SetDiscreteKey(binding, time, null, binding.EvaluateReference(time));
				return;
			}

			for (int c = 0; c < binding.ChannelCount; c++)
			{
				if (channel >= 0 && c != channel) continue;
				FloatCurve curve = binding.channels[c];
				curve.AddKey(time, curve.Length > 0 ? curve.Evaluate(time) : 0f);
			}
		}

		private void RemoveBinding(int bindingIndex)
		{
			AnimationClip clip = Clip;
			document.Edit("Remove property", () => clip.bindings.RemoveAt(bindingIndex));
			selectedRows.Clear();
			selectedKeys.Clear();
			expandedRows.Clear();
			previewSession?.Rebind();
		}

		private void ReverseClip()
		{
			AnimationClip clip = Clip;
			float length = clip.length;
			document.Edit("Reverse clip", () =>
			{
				foreach (AnimationBinding binding in clip.bindings)
				{
					foreach (FloatCurve curve in binding.channels)
					{
						var keys = new List<Keyframe>();
						for (int k = 0; k < curve.Length; k++)
						{
							Keyframe key = curve.GetKey(k);
							key.time = length - key.time;
							(key.inTangent, key.outTangent) = (-key.outTangent, -key.inTangent);
							keys.Add(key);
						}

						curve.SetKeys(keys);
					}

					foreach (StringKey key in binding.stringKeys) key.time = length - key.time;
					foreach (ReferenceKey key in binding.referenceKeys) key.time = length - key.time;
					binding.SortDiscreteKeys();
				}

				foreach (AnimationEvent e in clip.events) e.time = length - e.time;
			});
		}

		private void FrameAll()
		{
			float length = Math.Max(Clip.length, 0.5f);
			pixelsPerSecond = Math.Clamp((timelineSize.X - 40f) / length, 20f, 4000f);
			scrollTime = 0f;
			valueRangeDirty = true;
		}

		private void AddEvent(float time)
		{
			AnimationClip clip = Clip;
			AnimationEvent created = null;
			document.Edit("Add event", () => created = clip.AddEvent(Snap(time), "OnEvent"));
			selectedEvent = clip.events.IndexOf(created);
		}

		private void DeleteEvent(int index)
		{
			AnimationClip clip = Clip;
			if (index < 0 || index >= clip.events.Count)
			{
				return;
			}

			document.Edit("Delete event", () => clip.events.RemoveAt(index));
			selectedEvent = -1;
		}

		// ---------------------------------------------------------------- event inspector

		private void DrawEventInspector()
		{
			AnimationClip clip = Clip;
			if (!ImGui.BeginChild("##anim_event", new NVector2(0, 0), ImGuiChildFlags.Borders))
			{
				ImGui.EndChild();
				return;
			}

			AnimationEvent e = clip.events[selectedEvent];
			ImGui.TextUnformatted("Event");
			ImGui.SameLine();
			if (EditorGui.SmallButton("Delete")) { DeleteEvent(selectedEvent); ImGui.EndChild(); return; }
			ImGui.SameLine();
			if (EditorGui.SmallButton("Close")) { selectedEvent = -1; ImGui.EndChild(); return; }
			ImGui.Columns(2, "##eventcolumns", false);
			object time = e.time;
			if (FieldDrawer.DrawValue("Time", typeof(float), ref time, null, context, 0))
			{
				e.time = Snap((float)time);
			}

			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				clip.events.Sort((a, b) => a.time.CompareTo(b.time));
				selectedEvent = clip.events.IndexOf(e);
			}

			// Function: free text with the target's methods as suggestions.
			FieldDrawer.Label("Function", null, context);
			if (eventFunctionFor != selectedEvent) { eventFunctionFor = selectedEvent; eventFunctionBuffer = e.function; }
			ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 26f);
			bool changed = EditorGui.InputText("##function", ref eventFunctionBuffer, 128);
			context.Track(changed);
			if (changed) e.function = eventFunctionBuffer;
			ImGui.SameLine();
			if (EditorGui.ArrowButton("##functions", ImGuiDir.Down)) ImGui.OpenPopup("##function_list");
			if (ImGui.BeginPopup("##function_list"))
			{
				List<string> methods = TargetMethods();
				if (methods.Count == 0) ImGui.TextDisabled(target != null ? "No public methods on the target's components" : "No target selected");
				foreach (string method in methods)
				{
					if (EditorGui.Selectable(method))
					{
						string chosen = method;
						document.Edit("Set event function", () => e.function = chosen);
						eventFunctionBuffer = chosen;
					}
				}

				ImGui.EndPopup();
			}

			object f = e.floatParameter;
			if (FieldDrawer.DrawValue("Float", typeof(float), ref f, null, context, 0)) e.floatParameter = (float)f;
			object i = e.intParameter;
			if (FieldDrawer.DrawValue("Int", typeof(int), ref i, null, context, 0)) e.intParameter = (int)i;
			ImGui.NextColumn();
			object s = e.stringParameter;
			if (FieldDrawer.DrawValue("String", typeof(string), ref s, null, context, 0)) e.stringParameter = (string)s;
			object o = e.objectReference;
			if (FieldDrawer.DrawValue("Object", typeof(EngineObject), ref o, null, context, 0)) e.objectReference = o as EngineObject;
			object payload = e.payload;
			if (FieldDrawer.DrawValue("Payload", typeof(IAnimationEventPayload), ref payload, PayloadField, context, 0)) e.payload = payload as IAnimationEventPayload;
			object weight = e.minimumWeight;
			if (FieldDrawer.DrawValue("Min Weight", typeof(float), ref weight, typeof(AnimationEvent).GetField(nameof(AnimationEvent.minimumWeight)), context, 0)) e.minimumWeight = Math.Clamp((float)weight, 0f, 1f);
			ImGui.Columns(1);
			ImGui.EndChild();
		}

		private List<string> TargetMethods()
		{
			var result = new List<string>();
			if (target == null || target.IsDestroyed)
			{
				return result;
			}

			foreach (Behaviour component in target.Components)
			{
				if (component is Animator || component is Transform)
				{
					continue;
				}

				foreach (MethodInfo method in component.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
				{
					if (method.IsSpecialName || method.GetParameters().Length > 1 || method.ReturnType != typeof(void))
					{
						continue;
					}

					string name = method.Name;
					if (!result.Contains(name))
					{
						result.Add(name);
					}
				}
			}

			result.Sort(StringComparer.Ordinal);
			return result;
		}

		// ---------------------------------------------------------------- add property

		private void DrawAddPropertyPopup()
		{
			if (!ImGui.BeginPopup("##add_property"))
			{
				return;
			}

			if (target == null || target.IsDestroyed)
			{
				ImGui.TextDisabled("No target.");
				ImGui.EndPopup();
				return;
			}

			if (ImGui.IsWindowAppearing())
			{
				propertySearch = "";
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(360f);
			EditorGui.InputTextWithHint("##property_search", "Search properties", ref propertySearch, 64);
			ImGui.Separator();
			if (ImGui.BeginChild("##property_tree", new NVector2(420f, 360f)))
			{
				DrawPropertyObject(target.transform, target.transform, 0);
			}

			ImGui.EndChild();
			ImGui.EndPopup();
		}

		private void DrawPropertyObject(Transform root, Transform transform, int depth)
		{
			string path = AnimationBinder.PathOf(root, transform) ?? "";
			GameObject go = transform.gameObject;
			ImGui.PushID(go.Id.GetHashCode());
			bool filtered = propertySearch.Length > 0;
			bool open = filtered || EditorGui.TreeNodeEx(go.Name + (path.Length == 0 ? "  (root)" : ""), depth == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
			if (open)
			{
				// GameObject properties, then each component's.
				DrawPropertyList(path, "", "GameObject", AnimationBinder.GetAnimatableProperties(go), filtered);
				foreach (Behaviour component in go.Components)
				{
					if (component is Animator)
					{
						continue;
					}

					string typeName = AnimationBinder.TypeName(component.GetType());
					DrawPropertyList(path, typeName, component.GetType().Name, AnimationBinder.GetAnimatableProperties(component), filtered);
				}

				foreach (Transform child in transform.Children)
				{
					DrawPropertyObject(root, child, depth + 1);
				}

				if (!filtered)
				{
					ImGui.TreePop();
				}
			}

			ImGui.PopID();
		}

		private void DrawPropertyList(string path, string typeName, string typeLabel, List<AnimatableProperty> properties, bool filtered)
		{
			AnimationClip clip = Clip;
			ImGui.PushID(typeLabel);
			bool open = filtered || EditorGui.TreeNodeEx(typeLabel, ImGuiTreeNodeFlags.None);
			if (open)
			{
				foreach (AnimatableProperty property in properties)
				{
					string label = (filtered ? (path.Length > 0 ? path + " : " : "") + typeLabel + "." : "") + property.DisplayName;
					if (filtered && label.IndexOf(propertySearch, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					bool exists = clip.FindBinding(path, typeName, property.Property) != null;
					ImGui.BeginDisabled(exists);
					if (EditorGui.Selectable(label + (exists ? "  (added)" : "") + "##" + property.Property + property.Kind))
					{
						AddBinding(path, typeName, property);
					}

					ImGui.EndDisabled();
					if (ImGui.IsItemHovered()) ImGui.SetTooltip(property.Property + "  (" + property.Kind + ")");
				}

				if (!filtered)
				{
					ImGui.TreePop();
				}
			}

			ImGui.PopID();
		}

		private void AddBinding(string path, string typeName, AnimatableProperty property)
		{
			AnimationClip clip = Clip;
			float at = Time;
			document.Edit("Add property", () =>
			{
				AnimationBinding binding = clip.EnsureBinding(path, typeName, property.Property, property.Kind);
				WriteCurrentValue(binding, property.CurrentValue, at);
				if (at > 0f)
				{
					WriteCurrentValue(binding, property.CurrentValue, 0f);
				}
			});
			previewSession?.Rebind();
			valueRangeDirty = true;
		}

		private static void WriteCurrentValue(AnimationBinding binding, object value, float time)
		{
			switch (binding.kind)
			{
				case AnimationValueKind.String:
					SetDiscreteKey(binding, time, value as string ?? "", null);
					return;
				case AnimationValueKind.Reference:
					SetDiscreteKey(binding, time, null, value);
					return;
			}

			float[] channels = ChannelsOf(binding.kind, value);
			for (int c = 0; c < binding.ChannelCount && c < channels.Length; c++)
			{
				binding.channels[c].AddKey(time, channels[c]);
			}
		}

		private static float[] ChannelsOf(AnimationValueKind kind, object value)
		{
			switch (value)
			{
				case float f: return new[] { f };
				case double d: return new[] { (float)d };
				case int i: return new[] { (float)i };
				case bool b: return new[] { b ? 1f : 0f };
				case Enum e: return new[] { (float)Convert.ToInt64(e) };
				case Vector2 v: return new[] { v.X, v.Y };
				case Vector3 v: return new[] { v.X, v.Y, v.Z };
				case Vector4 v: return new[] { v.X, v.Y, v.Z, v.W };
				case Quaternion q:
					if (kind == AnimationValueKind.Euler)
					{
						Vector3 euler = FieldDrawer.ToEuler(q);
						return new[] { euler.X, euler.Y, euler.Z };
					}

					return new[] { q.X, q.Y, q.Z, q.W };
				case Color4 c: return new[] { c.R, c.G, c.B, c.A };
				case Vector2i v: return new[] { (float)v.X, v.Y };
				case Vector3i v: return new[] { (float)v.X, v.Y, v.Z };
				case Vector4i v: return new[] { (float)v.X, v.Y, v.Z, v.W };
				default: return new float[AnimationBinding.ChannelCountOf(kind)];
			}
		}

		// ---------------------------------------------------------------- misc

		private void HandleShortcuts()
		{
			if (!IsFocused || ImGui.GetIO().WantTextInput)
			{
				return;
			}

			if (ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false) && document.Dirty)
			{
				document.Save();
			}
		}

		private void DrawUnsavedPopup()
		{
			if (openUnsavedPopup)
			{
				ImGui.OpenPopup("Unsaved animation clip");
				openUnsavedPopup = false;
			}

			bool open = true;
			if (!ImGui.BeginPopupModal("Unsaved animation clip", ref open, ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			ImGui.TextUnformatted("The clip " + Path.GetFileName(document?.AssetPath ?? "") + " has unsaved changes.");
			ImGui.Spacing();
			if (EditorGui.Button("Save", new NVector2(100, 0)))
			{
				document.Save();
				EndPreview();
				Open = false;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Discard", new NVector2(100, 0)))
			{
				document.Revert();
				EndPreview();
				Open = false;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Cancel", new NVector2(100, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		#endregion

		#region NestedTypes

		/// <summary>
		///     The clip sampled on the target in edit mode: a pose layout bound to the hierarchy, the objects' values
		///     before (restored when the session ends) and what was written last (record mode compares against it).
		/// </summary>
		private sealed class PreviewSession
		{
			public struct Change
			{
				public int Binding;
				public float[] Values;
				public object Discrete;
			}

			private readonly AnimationClip clip;
			private readonly GameObject root;
			private PoseLayout layout;
			private Pose pose;
			private ClipSampler sampler;
			private int version = -1;
			private readonly float[] scratch = new float[4];

			public PreviewSession(AnimationClip clip, GameObject root)
			{
				this.clip = clip;
				this.root = root;
				Rebind();
			}

			public void Rebind()
			{
				float[] oldDefaults = layout?.DefaultValues;
				object[] oldDiscrete = layout?.DefaultDiscrete;
				List<PoseSlot> oldSlots = layout?.Slots;
				var fresh = new PoseLayout();
				fresh.AddClip(clip);
				// Restore the old slots first, so the new defaults are the objects' own values, not the preview's.
				if (layout != null)
				{
					layout.RestoreDefaults();
				}

				fresh.Bind(root);
				layout = fresh;
				pose = new Pose(layout);
				sampler = new ClipSampler(clip, layout);
				version = clip.Version;
			}

			public bool IsUnresolved(int bindingIndex)
			{
				return bindingIndex < clip.bindings.Count && layout.SlotOf(clip.bindings[bindingIndex]) is int s && (s < 0 || layout.Slots[s].Target == null);
			}

			public void Sample(float time)
			{
				if (clip.Version != version || root == null || root.IsDestroyed)
				{
					if (root == null || root.IsDestroyed)
					{
						return;
					}

					Rebind();
				}

				pose.Clear();
				sampler.Sample(time, 1f, pose);
				pose.Apply(layout, false);
			}

			public void Restore()
			{
				if (root != null && !root.IsDestroyed)
				{
					layout.RestoreDefaults();
				}
			}

			/// <summary>Bound properties whose current value differs from what the preview wrote last.</summary>
			public List<Change> DetectChanges()
			{
				var changes = new List<Change>();
				for (int b = 0; b < clip.bindings.Count; b++)
				{
					AnimationBinding binding = clip.bindings[b];
					int s = layout.SlotOf(binding);
					if (s < 0)
					{
						continue;
					}

					PoseSlot slot = layout.Slots[s];
					if (slot.Target == null || !slot.Target.IsAlive)
					{
						continue;
					}

					if (slot.IsDiscrete)
					{
						object current = slot.Target.ReadDiscrete();
						object last = layout.LastWrittenDiscrete[slot.DiscreteIndex];
						if (!Equals(current, last))
						{
							changes.Add(new Change { Binding = b, Discrete = current });
							layout.LastWrittenDiscrete[slot.DiscreteIndex] = current;
						}

						continue;
					}

					slot.Target.ReadChannels(scratch, 0);
					bool differs = false;
					for (int c = 0; c < slot.Channels; c++)
					{
						if (Math.Abs(scratch[c] - layout.LastWritten[slot.Offset + c]) > 1e-5f)
						{
							differs = true;
						}
					}

					if (!differs)
					{
						continue;
					}

					var values = new float[binding.ChannelCount];
					if (binding.kind == AnimationValueKind.Euler)
					{
						Vector3 euler = FieldDrawer.ToEuler(new Quaternion(scratch[0], scratch[1], scratch[2], scratch[3]));
						values[0] = euler.X;
						values[1] = euler.Y;
						values[2] = euler.Z;
					}
					else
					{
						Array.Copy(scratch, values, Math.Min(values.Length, slot.Channels));
					}

					changes.Add(new Change { Binding = b, Values = values });
					Array.Copy(scratch, 0, layout.LastWritten, slot.Offset, slot.Channels);
				}

				return changes;
			}

			/// <summary>The object's current value of a binding's property, as channels (or a discrete value).</summary>
			public bool TryReadCurrent(int bindingIndex, out float[] values, out object discrete)
			{
				values = null;
				discrete = null;
				AnimationBinding binding = clip.bindings[bindingIndex];
				int s = layout.SlotOf(binding);
				if (s < 0 || layout.Slots[s].Target == null || !layout.Slots[s].Target.IsAlive)
				{
					return false;
				}

				PoseSlot slot = layout.Slots[s];
				if (slot.IsDiscrete)
				{
					discrete = slot.Target.ReadDiscrete();
					return true;
				}

				slot.Target.ReadChannels(scratch, 0);
				values = new float[binding.ChannelCount];
				if (binding.kind == AnimationValueKind.Euler)
				{
					Vector3 euler = FieldDrawer.ToEuler(new Quaternion(scratch[0], scratch[1], scratch[2], scratch[3]));
					values[0] = euler.X;
					values[1] = euler.Y;
					values[2] = euler.Z;
				}
				else
				{
					Array.Copy(scratch, values, Math.Min(values.Length, slot.Channels));
				}

				return true;
			}
		}

		#endregion
	}
}
