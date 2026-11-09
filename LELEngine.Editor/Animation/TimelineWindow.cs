using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using LELEngine.Audio;
using LELEngine.Editor.Audio;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The Timeline window (Window > Timeline, or open a .timeline asset, or select an object with a Timeline): Unity's
	///     Timeline with an audio tool's logic. A channel per animated object of the scene (bound by the object's id, any
	///     object, not only children), the clips on them (move, trim, overlap to crossfade, fades), the properties they
	///     have keys for, and over them the logic track: loop regions, transition regions and markers, destination markers,
	///     sustain points and tempo, with parameters as sliders in the toolbar. In edit mode moving the playhead previews on
	///     the objects (put back by Stop); in play mode the window follows the component's playback. Record keys what
	///     changes on the armed channels' objects: any exposed value of any script, not only transforms.
	/// </summary>
	internal sealed partial class TimelineWindow : IEditorPanel
	{
		#region PublicFields

		public const string Title = "Timeline";
		public bool Open;

		string IEditorPanel.PanelName => Title;
		string IEditorPanel.ImGuiName => "###timeline";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(1280, 620);
		void IEditorPanel.Focus() => editor.OpenTimeline(document?.AssetPath);

		public bool IsFocused { get; private set; }
		public TimelineDocument Document => document;

		/// <summary>The component whose bindings the channels show (null: the asset is open without one).</summary>
		public Timeline Host => host;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private readonly TimelineRecorder recorder;
		private readonly Dictionary<string, float> previewParameters = new Dictionary<string, float>(StringComparer.Ordinal);
		private TimelineDocument document;
		private Timeline host;
		private ulong hostId;
		private bool lockHost;
		private TimelinePlayer preview;
		private double playhead;
		private double playStart;
		private bool focusRequested;
		private bool closeRequested;
		private string reloadPath;
		private ulong reloadHostId;

		#endregion

		#region Constructors

		public TimelineWindow(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
			recorder = new TimelineRecorder(this, editor);
			TimelinePlayer.AuditionSolo = true;
			snap = EditorPreferences.GetToggle("Timeline.Snap", snap);
			followPlayhead = EditorPreferences.GetToggle("Timeline.FollowPlayhead", followPlayhead);
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens the window on a .timeline asset (null: what it last showed).</summary>
		public void Show(string assetPath)
		{
			Open = true;
			focusRequested = true;
			if (assetPath != null && (document == null || !string.Equals(document.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase)))
			{
				SetDocument(TimelineAsset.Load(assetPath), null);
			}
		}

		/// <summary>Opens the window on a component: its asset, its bindings.</summary>
		public void Show(Timeline timeline)
		{
			Open = true;
			focusRequested = true;
			if (timeline != null)
			{
				lockHost = false;
				SetDocument(timeline.asset, timeline);
			}
		}

		public void Close()
		{
			closeRequested = true;
		}

		public void Reset()
		{
			EndPreview();
			document = null;
			host = null;
			hostId = 0;
			Open = false;
			keyTimeCache.Clear();
			propertyBindings.Clear();
			ClearSelection();
			context.Release();
		}

		/// <summary>Scripts reload: the document may hold nothing of them, but the scene's objects go away.</summary>
		public void OnDomainUnloading()
		{
			EndPreview();
			document?.EndEdit();
			editor.Edits.SaveWaiting();
			reloadPath = document?.AssetPath;
			reloadHostId = hostId;
			document = null;
			host = null;
			clipboardClip = null;
			keyTimeCache.Clear();
			propertyBindings.Clear();
			popupClip = null;
			popupClipChannel = null;
			popupMarker = null;
			TimelineAsset.ClearCache();
			// The fields' context keeps the scene it last drew with: a closed window kept the unloaded scene (and with it the
			// old scripts) alive.
			context.Release();
		}

		public void OnDomainLoaded()
		{
			if (reloadPath == null)
			{
				return;
			}

			SetDocument(TimelineAsset.Load(reloadPath), null);
			hostId = reloadHostId;
			reloadPath = null;
		}

		/// <summary>An undo or redo restored another state of a timeline.</summary>
		public void OnTimelineRestored(TimelineAsset asset)
		{
			if (document != null && document.Asset == asset)
			{
				document.OnRestored();
			}
		}

		/// <summary>The scene is about to be written or copied: nothing of the preview may go with it.</summary>
		public void BeforeSceneWrite()
		{
			preview?.Restore();
		}

		/// <summary>The scene was written: the preview poses the objects again.</summary>
		public void AfterSceneWrite()
		{
			preview?.ApplyPose();
		}

		/// <summary>Ends the preview: every property written goes back to its own value.</summary>
		public void EndPreview()
		{
			recorder.StopRecording();
			preview?.Restore();
			preview = null;
		}

		public void Draw()
		{
			if (!Open || editor.Project == null)
			{
				IsFocused = false;
				if (preview != null)
				{
					EndPreview();
				}

				return;
			}

			ImGui.SetNextWindowSize(new NVector2(1280, 560), ImGuiCond.FirstUseEver);
			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			bool open = Open;
			string title = Title + (document != null ? " - " + document.Asset.name : "") + "###timeline";
			bool visible = ImGui.Begin(title, ref open, ImGuiWindowFlags.MenuBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
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
				EndPreview();
				Open = false;
			}
		}

		#endregion

		#region InternalMethods

		/// <summary>The player the window shows: the component's in play mode, its own preview in edit mode.</summary>
		internal TimelinePlayer Shown => editor.IsPlaying ? host?.Player : preview;

		internal TimelineAsset Asset => document?.Asset;

		/// <summary>The object a channel is bound to on the host.</summary>
		internal GameObject TargetOf(TimelineChannel channel)
		{
			return EngineObject.IsAlive(host) ? host.ResolveTarget(channel) : null;
		}

		/// <summary>The time the window shows: the playback's while there is one.</summary>
		internal double ShownTime => playhead;

		#endregion

		#region PrivateMethods

		private void SetDocument(TimelineAsset asset, Timeline timeline)
		{
			if (document?.Asset == asset && host == timeline)
			{
				return;
			}

			bool sameAsset = document?.Asset == asset;
			EndPreview();
			if (!sameAsset)
			{
				ClearSelection();
				previewParameters.Clear();
				playhead = 0.0;
				playStart = 0.0;
				document = asset != null ? new TimelineDocument(editor, asset) : null;
				if (document != null)
				{
					document.Restored += OnDocumentRestored;
				}
			}

			host = timeline;
			hostId = timeline?.Id ?? 0;
		}

		private void OnDocumentRestored()
		{
			if (selectionKind == SelectionKind.Clip && Asset.FindClip(selectedId, out _) == null || selectionKind == SelectionKind.Marker && Asset.FindMarker(selectedId) == null || selectionKind == SelectionKind.Channel && Asset.FindChannel(selectedId) == null)
			{
				ClearSelection();
			}

			selectedKeyClip = null;
		}

		/// <summary>Which component and asset the window shows: the selection's Timeline, unless locked; a component that went away (play mode, a reload) is found again by its id.</summary>
		private void UpdateHost()
		{
			if (!EngineObject.IsAlive(host))
			{
				host = null;
				if (hostId != 0)
				{
					host = editor.Scene?.FindComponentById(hostId) as Timeline;
				}
			}

			if (!lockHost)
			{
				GameObject selected = editor.Selected;
				Timeline found = selected != null && !selected.IsDestroyed ? selected.GetComponentInParent<Timeline>(true) : null;
				if (found != null && (found != host || found.asset != Asset) && found.asset != null)
				{
					SetDocument(found.asset, found);
				}
				else if (found != null && found != host && found.asset == null)
				{
					// A Timeline without an asset yet: the window offers to create one.
					EndPreview();
					document = null;
					host = found;
					hostId = found.Id;
				}
			}

			// The asset open without a component: the scene's Timeline playing it, when there is one.
			if (host == null && document != null && editor.Scene != null)
			{
				foreach (Timeline candidate in editor.Scene.FindObjectsOfType<Timeline>())
				{
					if (candidate.asset == Asset)
					{
						host = candidate;
						hostId = candidate.Id;
						break;
					}
				}
			}

			if (host != null && document != null && host.asset != Asset)
			{
				// The component's asset was changed in the inspector.
				if (host.asset != null)
				{
					SetDocument(host.asset, host);
				}
				else
				{
					EndPreview();
					host = null;
					hostId = 0;
				}
			}
		}

		private void DrawContent()
		{
			context.Scene = editor.Scene;
			UpdateHost();
			DrawMenuBar();
			if (document == null)
			{
				DrawNoDocument();
				return;
			}

			HandleShortcuts();
			DrawToolbar();
			DrawParameters();
			UpdatePlayback();
			DrawBody();
		}

		/// <summary>Nothing to show: says how to begin, and offers to make the Timeline and its asset for the selection.</summary>
		private void DrawNoDocument()
		{
			GameObject target = host != null ? host.gameObject : editor.Selected;
			ImGui.Spacing();
			if (target == null || target.IsDestroyed)
			{
				ImGui.TextDisabled("Select a GameObject to begin a timeline with it, select one with a Timeline, or double click a .timeline asset.");
				return;
			}

			ImGui.TextUnformatted(host != null ? "'" + target.Name + "' has a Timeline without an asset." : "To begin a timeline on '" + target.Name + "', create a Timeline component and its asset.");
			ImGui.Spacing();
			ImGui.BeginDisabled(editor.IsPlaying);
			if (EditorGui.Button("Create Timeline"))
			{
				CreateTimeline(target);
			}

			ImGui.EndDisabled();
		}

		private void CreateTimeline(GameObject target)
		{
			string folder = "Timelines";
			Directory.CreateDirectory(AssetDatabase.ToAbsolute(folder));
			string path = TimelineRecorder.UniquePath(folder, target.Name, TimelineAsset.Extension);
			var asset = new TimelineAsset { name = Path.GetFileNameWithoutExtension(path) };
			asset.Save(path);
			AddTimeline(target, asset);
			editor.Ping(asset);
		}

		/// <summary>Plays an asset on an object: its Timeline, added when it has none (one undo step).</summary>
		private void AddTimeline(GameObject target, TimelineAsset asset)
		{
			Timeline timeline = null;
			editor.RecordStructural("Add Timeline", () =>
			{
				timeline = target.GetComponent<Timeline>() ?? target.AddComponent<Timeline>();
				timeline.asset = asset;
			});
			SetDocument(asset, timeline);
		}

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMenuBar())
			{
				return;
			}

			if (EditorMenu.Begin("File"))
			{
				if (EditorMenu.Item("Close")) closeRequested = true;
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Edit", document != null))
			{
				if (EditorMenu.Item("Undo", "Ctrl+Z", false, editor.Undo.CanUndo)) editor.UndoLast();
				if (EditorMenu.Item("Redo", "Ctrl+Y", false, editor.Undo.CanRedo)) editor.RedoLast();
				ImGui.Separator();
				if (EditorMenu.Item("Duplicate", "Ctrl+D", false, selectionKind == SelectionKind.Clip || selectionKind == SelectionKind.Marker)) DuplicateSelection();
				if (EditorMenu.Item("Delete", "Del", false, selectionKind != SelectionKind.None || selectedKeyClip != null)) DeleteSelection();
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Add", document != null))
			{
				if (EditorMenu.Item("Channel From Selection", "", false, editor.Selected != null && host != null)) AddChannelFromSelection();
				if (EditorMenu.Item("Empty Channel")) AddChannel(null);
				if (EditorMenu.Item("Parameter")) AddParameter();
				ImGui.Separator();
				DrawAddMarkerItems(playhead);
				EditorMenu.End();
			}

			if (EditorMenu.Begin("View"))
			{
				EditorMenu.Toggle("Snap", "Timeline.Snap", ref snap);
				EditorMenu.Toggle("Follow Playhead", "Timeline.FollowPlayhead", ref followPlayhead);
				if (EditorMenu.Item("Frame All", "F", false, document != null)) FrameAll();
				EditorMenu.End();
			}

			ImGui.EndMenuBar();
		}

		private void DrawToolbar()
		{
			TimelinePlayer shown = Shown;
			bool playing = shown != null && shown.IsPlaying && !shown.Paused;
			double frame = 1.0 / Math.Max(Asset.frameRate, 1f);
			if (EditorGui.Button("|<")) SeekTo(0.0);
			ImGui.SameLine();
			if (EditorGui.Button("<")) SeekTo(SnapToFrame(playhead) - frame);
			ImGui.SameLine();
			if (playing) ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
			bool pressPlay = EditorGui.Button(playing ? "||" : ">");
			if (playing) ImGui.PopStyleColor();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play / pause (Space)");
			if (pressPlay) TogglePlay();
			ImGui.SameLine();
			bool active = shown != null && shown.IsPlaying;
			ImGui.BeginDisabled(!active && preview == null);
			if (EditorGui.Button("Stop")) StopPlayback();
			ImGui.EndDisabled();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(editor.IsPlaying ? "Stop the Timeline" : "End the preview: the objects get their own values back");
			ImGui.SameLine();
			if (EditorGui.Button(">##next")) SeekTo(SnapToFrame(playhead) + frame);
			ImGui.SameLine();
			if (EditorGui.Button(">|")) SeekTo(Asset.Duration);
			ImGui.SameLine();
			bool recording = recorder.Recording;
			if (recording) ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Error);
			bool pressRecord = EditorGui.Button("Record");
			if (recording) ImGui.PopStyleColor();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Paused: changing a value of an armed channel's object (inspector, scene view) sets a key at the playhead.\nPlaying: what the armed objects do is recorded as it happens.\nAny exposed value counts: fields of your scripts, [Animatable] members.");
			if (pressRecord) ToggleRecord();
			ImGui.SameLine();
			if (shown != null && shown.State == TimelinePlaybackState.Sustaining)
			{
				if (EditorGui.Button("Key Off")) shown.KeyOff();
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("The cursor waits at a sustain point: move on");
				ImGui.SameLine();
			}

			ImGui.Dummy(new NVector2(6, 0));
			ImGui.SameLine();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(FormatTime(playhead) + "  f" + (long)Math.Round(playhead * Asset.frameRate));
			ImGui.SameLine();
			ImGui.TextDisabled(StateText(shown));
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(6, 0));
			ImGui.SameLine();
			if (EditorGui.Button("Fit")) FrameAll();
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the whole timeline (F). Ctrl + wheel: zoom, Shift + wheel or middle drag: scroll.");
			ImGui.SameLine();
			if (EditorGui.Checkbox("Snap", ref snap)) EditorPreferences.SetToggle("Timeline.Snap", snap);
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clips, markers and the playhead snap to frames and to each other");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(70f);
			float rate = Asset.frameRate;
			bool rateChanged = EditorGui.DragFloat("##rate", ref rate, 0.5f, 1f, 240f, "%.0f fps");
			AfterField(rateChanged, "Change frame rate", () => Asset.frameRate = Math.Max(1f, rate));
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(6, 0));
			ImGui.SameLine();
			ImGui.AlignTextToFramePadding();
			if (host != null)
			{
				ImGui.TextDisabled("On");
				ImGui.SameLine();
				ImGui.TextUnformatted("'" + host.gameObject.Name + "'");
				ImGui.SameLine();
				EditorGui.Checkbox("Lock", ref lockHost);
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Keep this Timeline while selecting other objects");
			}
			else if (editor.Selected != null && !editor.IsPlaying)
			{
				ImGui.TextColored(EditorStyle.Warning, "No Timeline in the scene plays this asset.");
				ImGui.SameLine();
				if (EditorGui.SmallButton("Add to '" + editor.Selected.Name + "'"))
				{
					AddTimeline(editor.Selected, Asset);
				}
			}
			else
			{
				ImGui.TextColored(EditorStyle.Warning, "No Timeline in the scene plays this asset.");
			}
		}

		private string StateText(TimelinePlayer shown)
		{
			if (shown == null || !shown.IsPlaying)
			{
				return editor.IsPlaying ? "stopped" : preview != null ? "preview" : "";
			}

			string state = shown.Paused ? "paused" : shown.State == TimelinePlaybackState.Sustaining ? "sustaining" : "playing";
			return editor.IsPlaying ? "live, " + state : state;
		}

		/// <summary>Each parameter of the timeline as a slider (right click edits it); moving one drives the preview or, in play mode, the component.</summary>
		private void DrawParameters()
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextDisabled("Parameters");
			ImGui.SameLine();
			for (int i = 0; i < Asset.parameters.Count; i++)
			{
				AudioParameter parameter = Asset.parameters[i];
				ImGui.PushID(parameter.Id);
				float value = ParameterValue(parameter);
				float min = Math.Min(parameter.Min, parameter.Max);
				float max = Math.Max(parameter.Min, parameter.Max);
				ImGui.SetNextItemWidth(150f);
				string format = parameter.Name.Replace("%", "%%") + "  " + (parameter.Type == AudioParameterType.Continuous ? "%.2f" : "%.0f");
				TestAnchors.NameNextElement(parameter.Name);
				if (EditorGui.SliderFloat("##value", ref value, min, max, format))
				{
					SetParameterValue(parameter, parameter.Clamp(value));
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Parameter '" + parameter.Name + "': the game sets it with timeline.SetParameter.\nConditions of regions, transitions and clips read it. Right click: edit.");
				}

				if (EditorGui.BeginPopupContextItem("##parameter_menu"))
				{
					DrawParameterPopup(parameter);
					ImGui.EndPopup();
				}

				ImGui.SameLine();
				ImGui.PopID();
			}

			if (EditorGui.SmallButton("+##parameter"))
			{
				AddParameter();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Add a parameter");
			}
		}

		private float ParameterValue(AudioParameter parameter)
		{
			TimelinePlayer shown = Shown;
			if (shown != null)
			{
				float value = shown.GetParameter(parameter.Id);
				if (!float.IsNaN(value))
				{
					return value;
				}
			}

			return previewParameters.TryGetValue(parameter.Id, out float stored) ? stored : parameter.Default;
		}

		private void SetParameterValue(AudioParameter parameter, float value)
		{
			previewParameters[parameter.Id] = value;
			if (editor.IsPlaying && host != null)
			{
				host.SetParameter(parameter.Name, value, true);
			}
			else
			{
				EnsurePreview();
				preview.SetParameter(parameter.Id, value, true);
			}
		}

		// ---------------------------------------------------------------- playback

		private void EnsurePreview()
		{
			if (editor.IsPlaying || preview != null || document == null)
			{
				return;
			}

			preview = new TimelinePlayer(Asset, TargetOf) { Preview = true };
			foreach (KeyValuePair<string, float> parameter in previewParameters)
			{
				preview.SetParameter(parameter.Key, parameter.Value, true);
			}

			preview.Play(playhead);
			preview.Paused = true;
			preview.ApplyPose();
			editor.NotifySceneChanged();
		}

		private void TogglePlay()
		{
			if (editor.IsPlaying)
			{
				if (host == null)
				{
					return;
				}

				if (host.isPlaying)
				{
					host.paused = !host.paused;
				}
				else
				{
					playStart = playhead;
					host.Play(playhead >= Asset.Duration ? 0.0 : playhead);
				}

				return;
			}

			EnsurePreview();
			if (preview.IsPlaying)
			{
				if (preview.Paused)
				{
					playStart = playhead;
				}

				preview.Paused = !preview.Paused;
			}
			else
			{
				playStart = playhead >= Asset.Duration ? 0.0 : playhead;
				preview.Play(playStart);
			}
		}

		private void StopPlayback()
		{
			if (editor.IsPlaying)
			{
				host?.Stop();
				return;
			}

			EndPreview();
			playhead = playStart;
			playStart = 0.0;
			editor.NotifySceneChanged();
		}

		private void ToggleRecord()
		{
			if (recorder.Recording)
			{
				recorder.StopRecording();
				return;
			}

			if (host == null)
			{
				Debug.LogWarning("[Timeline] Recording needs a Timeline in the scene: the channels' objects are bound on it.");
				return;
			}

			if (editor.IsPlaying && !host.isPlaying)
			{
				host.Play(playhead);
				host.paused = true;
			}

			EnsurePreview();
			recorder.StartRecording();
		}

		/// <summary>Moves the playhead (scrubbing: no transition or sustain point acts at the spot).</summary>
		private void SeekTo(double time)
		{
			time = Math.Max(0.0, time);
			playhead = time;
			if (editor.IsPlaying)
			{
				if (host == null || host.asset == null)
				{
					return;
				}

				if (!host.isPlaying)
				{
					host.Play(time);
					host.paused = true;
				}
				else
				{
					host.Player.Seek(time, false);
				}

				host.Player.ApplyPose();
				recorder.Resync();
				return;
			}

			EnsurePreview();
			if (preview.IsPlaying)
			{
				preview.Seek(time, false);
			}
			else
			{
				preview.Play(time);
				preview.Paused = true;
			}

			preview.ApplyPose();
			recorder.Resync();
			editor.NotifySceneChanged();
		}

		/// <summary>Every frame: what recording sees, the preview's step and pose, the playhead.</summary>
		private void UpdatePlayback()
		{
			if (editor.IsPlaying && preview != null)
			{
				// Play mode started with a preview on (the editor ends it first; this is the guard).
				preview = null;
				recorder.StopRecording();
			}

			TimelinePlayer shown = Shown;
			recorder.BeforePose(shown);
			if (!editor.IsPlaying && preview != null)
			{
				if (preview.IsPlaying)
				{
					preview.Advance(ImGui.GetIO().DeltaTime);
					playhead = preview.Time;
				}

				editor.NotifySceneChanged();
			}
			else if (shown != null && shown.IsPlaying)
			{
				playhead = shown.Time;
			}

			recorder.AfterPose();
		}

		// ---------------------------------------------------------------- misc

		private static string FormatTime(double seconds)
		{
			seconds = Math.Max(0.0, seconds);
			int minutes = (int)(seconds / 60.0);
			return minutes + ":" + (seconds - minutes * 60.0).ToString("00.000");
		}

		private double SnapToFrame(double time)
		{
			double rate = Math.Max(Asset.frameRate, 1f);
			return Math.Round(time * rate) / rate;
		}

		private void HandleShortcuts()
		{
			if (!IsFocused || ImGui.GetIO().WantTextInput)
			{
				return;
			}

			ImGuiIOPtr io = ImGui.GetIO();
			if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
			{
				DuplicateSelection();
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
			{
				DeleteSelection();
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
			{
				TogglePlay();
			}
			else if (!io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false))
			{
				FrameAll();
			}
			else if (!io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.K, false))
			{
				KeyAllAtPlayhead();
			}
		}

		#endregion
	}
}
