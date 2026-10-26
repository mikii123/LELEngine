using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     The Audio Studio: edits the game's audio project the way FMOD Studio edits its projects. Events in folders
	///     (the Events panel), each with tracks of instruments on a timeline and on parameter sheets (the Event Editor),
	///     the logic markers (loops, transitions, tempo, sustain points), the properties of whatever is selected (the
	///     Deck), the event's own settings and its parameters with live sliders (the Overview), the mixer buses with
	///     faders and meters (the Mixer), global parameters, banks and the project's audio files (the Assets panel).
	///     Every edit is undoable and applies live: the audio system rebuilds the event and playing instances follow.
	///     Lives in its own window (<see cref="AudioStudioWindow" />).
	/// </summary>
	internal sealed partial class AudioStudio : IDisposable
	{
		#region PublicFields

		public AudioProject Project { get; private set; }
		public string AssetPath => Project?.AssetPath;
		public bool Dirty { get; private set; }
		public string Title => (AssetPath ?? "No audio project") + (Dirty ? " *" : "");

		/// <summary>The event open in the editor.</summary>
		public AudioEvent SelectedEvent => Project?.FindEvent(selectedEventId);

		/// <summary>The instance previewing the selected event, while it plays.</summary>
		public EventInstance Preview => preview != null && preview.IsValid ? preview : null;

		#endregion

		#region PrivateFields

		private enum SelectionKind
		{
			None,
			Event,
			Track,
			Master,
			Instrument,
			Marker,
			Bus,
			GlobalParameter,
			LocalParameter,
			Bank,
			Asset,
			Snapshot
		}

		private struct Selection
		{
			public SelectionKind Kind;
			public string Id;

			public static Selection Of(SelectionKind kind, string id)
			{
				return new Selection { Kind = kind, Id = id };
			}

			public bool Is(SelectionKind kind, string id)
			{
				return Kind == kind && Id == id;
			}
		}

		private const string ClipPayload = "LEL_AUDIO_CLIP";
		private const string EventPayload = "LEL_AUDIO_EVENT";
		private const string TrackPayload = "LEL_AUDIO_TRACK";

		private readonly EditorApplication editor;
		private readonly AudioStudioWindow window;
		private readonly bool layoutFileExisted;
		private bool layoutBuilt;
		private bool resetLayoutRequested;

		// Document state.
		private string selectedEventId;
		private Selection selection;
		private readonly List<byte[]> undoStack = new List<byte[]>();
		private readonly List<byte[]> redoStack = new List<byte[]>();
		private byte[] editSnapshot;
		private byte[] savedState;
		private bool liveUpdatePending;
		private string status = "";
		private double statusAt;

		// Panels.
		private bool showEvents = true;
		private bool showBanks = true;
		private bool showParameters = true;
		private bool showAssets = true;
		private bool showMixer = true;
		private bool showDeck = true;
		private bool showOverview = true;
		private bool showSnapshots = true;

		// Preview.
		private EventInstance preview;
		private readonly Dictionary<string, float> previewParameters = new Dictionary<string, float>(StringComparer.Ordinal);
		private float previewDistance = 2f;
		private float previewAngle;
		private bool followPlayhead = true;

		// Popups.
		private bool openCloseConfirm;
		private bool closeConfirmed;
		private bool openNewEventPopup;
		private string newEventPath = "New Event";
		private bool openRenamePopup;
		private string renameBuffer = "";
		private Selection renameTarget;
		private bool openNewProjectPopup;
		private string newProjectPath = "Audio/Audio.lelaudio";
		private bool openNewFolderPopup;
		private string newFolderPath = "Folder";
		private string pendingDeleteEvent;

		// Test input: injected ImGui input events, one per frame.
		private readonly Queue<Action> injectedInput = new Queue<Action>();

		#endregion

		#region Constructors

		public AudioStudio(EditorApplication editor, AudioStudioWindow window, bool layoutFileExisted)
		{
			this.editor = editor;
			this.window = window;
			this.layoutFileExisted = layoutFileExisted;
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens the audio project at an asset path (the audio system's project, so edits apply live).</summary>
		public void Open(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return;
			}

			StopPreview();
			AudioProject project = AudioProject.Load(assetPath);
			if (project == null)
			{
				SetStatus("Cannot open " + assetPath);
				return;
			}

			Project = project;
			savedState = project.ToBytes();
			Dirty = false;
			undoStack.Clear();
			redoStack.Clear();
			editSnapshot = null;
			selectedEventId = project.Events.Count > 0 ? project.Events[0].Id : null;
			selection = selectedEventId != null ? Selection.Of(SelectionKind.Event, selectedEventId) : default;
			currentSheet = null;
			expandedTransitionId = null;
			if (AudioSystem.SourceProject != project)
			{
				AudioSystem.UseProject(project);
				AudioSystem.ReloadFromProject();
			}

			EnsureBanksLoaded();
			SetStatus("Opened " + assetPath);
		}

		/// <summary>Creates an audio project at the asset path and opens it.</summary>
		public void CreateProject(string assetPath)
		{
			assetPath = assetPath.Replace('\\', '/').TrimStart('/');
			if (!assetPath.EndsWith(AudioProject.Extension, StringComparison.OrdinalIgnoreCase))
			{
				assetPath += AudioProject.Extension;
			}

			string absolute = AssetDatabase.ToAbsolute(assetPath);
			if (File.Exists(absolute))
			{
				SetStatus("A file with this name exists already: " + assetPath);
				return;
			}

			AudioProject project = AudioProject.CreateDefault(Path.GetFileNameWithoutExtension(assetPath));
			project.CreateEvent("New Event");
			project.Save(assetPath);
			AssetDatabase.EnsureMeta(assetPath);
			editor.NotifyAssetsChanged();
			AudioProject.Forget(assetPath);
			Open(assetPath);
		}

		public bool Save()
		{
			if (Project == null)
			{
				return false;
			}

			try
			{
				Project.Save();
				AssetDatabase.EnsureMeta(Project.AssetPath);
				savedState = Project.ToBytes();
				Dirty = false;
				editor.NotifyAssetsChanged();
				SetStatus("Saved " + Project.AssetPath);
				return true;
			}
			catch (Exception e)
			{
				SetStatus("Save failed: " + e.Message);
				Debug.LogError("[Audio Studio] " + status);
				return false;
			}
		}

		/// <summary>Reloads the project from its file, discarding the unsaved changes.</summary>
		public void Revert()
		{
			if (Project == null || string.IsNullOrEmpty(Project.AssetPath))
			{
				return;
			}

			try
			{
				Project.ApplyBytes(File.ReadAllBytes(AssetDatabase.ToAbsolute(Project.AssetPath)));
				savedState = Project.ToBytes();
				Dirty = false;
				undoStack.Clear();
				redoStack.Clear();
				liveUpdatePending = true;
				SetStatus("Reverted");
			}
			catch (Exception e)
			{
				SetStatus("Cannot revert: " + e.Message);
			}
		}

		/// <summary>The project file changed on disk and was reloaded by the editor: the window shows the new state.</summary>
		public void OnProjectReloaded()
		{
			if (Project == null)
			{
				return;
			}

			savedState = Project.ToBytes();
			Dirty = false;
			undoStack.Clear();
			redoStack.Clear();
			if (SelectedEvent == null)
			{
				selectedEventId = Project.Events.Count > 0 ? Project.Events[0].Id : null;
			}
		}

		/// <summary>Shows an event of the open project (the inspector's "Open in Audio Studio").</summary>
		public void ShowEvent(string eventId)
		{
			if (Project?.FindEvent(eventId) != null)
			{
				SelectEvent(eventId);
			}
		}

		/// <summary>The window asked to close: true to allow it (no unsaved changes, or confirmed).</summary>
		public bool RequestClose()
		{
			if (!Dirty || closeConfirmed)
			{
				StopPreview();
				return true;
			}

			openCloseConfirm = true;
			return false;
		}

		public void Draw()
		{
			if (injectedInput.Count > 0)
			{
				injectedInput.Dequeue()();
			}

			DrawMenuBar();
			HandleShortcuts();
			uint dockspace = ImGui.DockSpaceOverViewport();
			if (!layoutBuilt || resetLayoutRequested)
			{
				layoutBuilt = true;
				if (!layoutFileExisted || resetLayoutRequested)
				{
					BuildDefaultLayout(dockspace);
				}

				resetLayoutRequested = false;
			}

			if (showEvents) DrawEventsPanel();
			if (showBanks) DrawBanksPanel();
			if (showParameters) DrawParametersPanel();
			if (showSnapshots) DrawSnapshotsPanel();
			if (showAssets) DrawAssetsPanel();
			DrawEventEditor();
			if (showDeck) DrawDeck();
			if (showOverview) DrawOverview();
			if (showMixer) DrawMixer();
			DrawPopups();
			FlushLiveUpdate();
			UpdatePreviewState();
		}

		public void Dispose()
		{
			StopPreview();
			StopPreviewSnapshot();
		}

		/// <summary>
		///     Scripted editing for automated tests (arguments separated by commas): newproject=Path, open=Path,
		///     event=Path (select), play, stop, param=Name:Value, newevent=Path, addsingle=Clip,Start,Length (to the
		///     first track), addtrack, addloop=Start,Length, addtempo=Bpm, adddest=Name,Position,
		///     addtransition=Position,DestinationName, sheet=ParameterName (or "timeline"), addparam=Name,Min,Max,
		///     select=instrument:Id | track:Id | marker:Id, undo, redo, save, buildbanks=Folder, report (logs the state),
		///     delete, zoom=PixelsPerSecond, scroll=Seconds, mouse=x,y, click=x,y, drag=x1,y1,x2,y2.
		/// </summary>
		public void RunTestCommand(string command)
		{
			int eq = command.IndexOf('=');
			string name = eq > 0 ? command.Substring(0, eq) : command;
			string arg = eq > 0 ? command.Substring(eq + 1) : "";
			string[] parts = arg.Split(',');
			AudioEvent current = SelectedEvent;
			switch (name)
			{
				case "event":
				{
					AudioEvent found = Project?.FindEventByPath(arg);
					if (found != null) SelectEvent(found.Id);
					break;
				}
				case "play": PlayPreview(); break;
				case "stop": StopPreview(); break;
				case "param":
				{
					int colon = arg.IndexOf(':');
					if (colon > 0 && float.TryParse(arg.Substring(colon + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value))
					{
						SetPreviewParameter(arg.Substring(0, colon), value);
					}

					break;
				}
				case "newevent": CreateEvent(arg); break;
				case "newproject": CreateProject(arg); break;
				case "open": Open(arg); break;
				case "addtransitiontimeline":
				{
					// On the selected transition marker, or the one named.
					TransitionMarker transition = string.IsNullOrEmpty(arg) ? current?.FindMarker(selection.Id) as TransitionMarker : current?.Markers.Find(m => m is TransitionMarker && m.Name == arg) as TransitionMarker;
					if (transition != null) OpenTransitionTimeline(transition);
					break;
				}
				case "closetransition": CloseTransitionTimeline(); break;
				case "addsingleto":
				case "addtransitionclip":
				{
					// addsingleto=TrackIndex,Clip,Start,Length[,loop] on the current sheet; addtransitionclip=... on the open transition timeline.
					if (current != null && parts.Length >= 4 && int.TryParse(parts[0], out int trackIndex) && trackIndex >= 0 && trackIndex < current.Tracks.Count)
					{
						var single = new SingleInstrument { Clip = AudioClipReference.FromPath(parts[1]) };
						string sheet = name == "addtransitionclip" ? (ExpandedTransition != null ? TransitionTimeline.SheetId(ExpandedTransition.Id) : null) : currentSheet;
						if (name == "addsingleto" || sheet != null)
						{
							AddInstrument(current.Tracks[trackIndex], single, Parse(parts[2]), Parse(parts[3]), sheet);
							if (parts.Length >= 5 && parts[4] == "loop") single.Loop = true;
						}
					}

					break;
				}
				case "addregion":
					// addregion=Start,Length,DestinationName[,Quantization]
					if (current != null && parts.Length >= 3)
					{
						AudioMarker destination = current.Markers.Find(m => m is DestinationMarker && m.Name == parts[2]);
						var region = new TransitionRegion { Position = Parse(parts[0]), RegionLength = Parse(parts[1]), DestinationId = destination?.Id ?? TransitionMarker.EndDestination, Name = "Transition Region" };
						if (parts.Length >= 4 && Enum.TryParse(parts[3], true, out QuantizationInterval quantization)) region.Quantization = quantization;
						AddMarker(region);
					}

					break;
				case "beats": showBeats = arg != "0"; break;
				case "snap": snap = arg != "0"; break;
				case "selecttrack":
				{
					AudioTrack track = arg == "Master" ? current?.Master : current?.Tracks.Find(t => t.Name == arg);
					if (track != null) selection = Selection.Of(track == current.Master ? SelectionKind.Master : SelectionKind.Track, track.Id);
					break;
				}
				case "addeffect":
				{
					// addeffect=lowpass|highpass|reverb|delay on the selected track (or the master track).
					AudioTrack track = current?.FindTrack(selection.Id) ?? current?.Master;
					AudioEffect effect = arg == "highpass" ? new HighPassEffect { CutoffHz = 200f } : arg == "reverb" ? new ReverbEffect() : arg == "delay" ? (AudioEffect)new DelayEffect() : new LowPassEffect { CutoffHz = 4000f };
					effect.Id = AudioProject.NewId();
					if (track != null) Edit("Add effect", () => track.Effects.Add(effect));
					break;
				}
				case "loop": loopPlayback = arg != "0"; break;
				case "solo":
				{
					AudioTrack track = current?.Tracks.Find(t => t.Name == arg);
					if (track != null) Edit("Toggle solo", () => track.Solo = !track.Solo);
					break;
				}
				case "transition":
				{
					// transition=Length,SourceLength,DestinationStart,SourceFade,DestinationFade on the open transition timeline.
					TransitionTimeline timeline = ExpandedTransition?.Timeline;
					if (timeline != null && parts.Length >= 5)
					{
						Edit("Set transition timeline", () =>
						{
							timeline.Length = Parse(parts[0]);
							timeline.SourceLength = Parse(parts[1]);
							timeline.DestinationStart = Parse(parts[2]);
							timeline.SourceFadeOut.Length = Parse(parts[3]);
							timeline.DestinationFadeIn.Length = Parse(parts[4]);
							timeline.Clamp();
						});
					}

					break;
				}
				case "newsnapshot":
				{
					var snapshot = new AudioSnapshot { Id = AudioProject.NewId(), Name = arg };
					Edit("Add snapshot", () => Project.Snapshots.Add(snapshot));
					selection = Selection.Of(SelectionKind.Snapshot, snapshot.Id);
					break;
				}
				case "selectsnapshot":
				{
					AudioSnapshot snapshot = Project?.FindSnapshotByName(arg);
					if (snapshot != null) selection = Selection.Of(SelectionKind.Snapshot, snapshot.Id);
					break;
				}
				case "snapshotbus":
				{
					// snapshotbus=BusName,VolumeDb on the selected snapshot.
					AudioSnapshot snapshot = Project?.FindSnapshot(selection.Id);
					AudioBus bus = Project?.Buses.Find(b => b.Name == parts[0]);
					if (snapshot != null && bus != null && parts.Length >= 2)
					{
						Edit("Set snapshot bus", () =>
						{
							SnapshotBusValue value = snapshot.FindBus(bus.Id);
							if (value == null)
							{
								value = new SnapshotBusValue { BusId = bus.Id };
								snapshot.Buses.Add(value);
							}

							value.SetVolume = true;
							value.VolumeDb = (float)Parse(parts[1]);
						});
					}

					break;
				}
				case "snapshotstart":
				{
					AudioSnapshot snapshot = Project?.FindSnapshot(selection.Id);
					if (snapshot != null) StartPreviewSnapshot(snapshot);
					break;
				}
				case "snapshotstop": StopPreviewSnapshot(); break;
				case "addsnapshotinstrument":
					if (current != null && current.Tracks.Count > 0 && parts.Length >= 3)
					{
						AudioSnapshot snapshot = Project.FindSnapshotByName(parts[0]);
						AddInstrument(current.Tracks[0], new SnapshotInstrument { SnapshotId = snapshot?.Id }, Parse(parts[1]), Parse(parts[2]));
					}

					break;
				case "addtrack":
					if (current != null) Edit("Add track", () => current.Tracks.Add(AudioProject.CreateTrack("Audio " + (current.Tracks.Count + 1))));
					break;
				case "addsingle":
					if (current != null && current.Tracks.Count > 0 && parts.Length >= 3)
					{
						double start = Parse(parts[1]);
						double length = Parse(parts[2]);
						AddInstrument(current.Tracks[0], new SingleInstrument { Clip = AudioClipReference.FromPath(parts[0]) }, start, length);
					}

					break;
				case "addloop":
					if (current != null && parts.Length >= 2) AddMarker(new LoopRegion { Position = Parse(parts[0]), RegionLength = Parse(parts[1]) });
					break;
				case "addtempo":
					if (current != null) AddMarker(new TempoMarker { Position = 0.0, Bpm = (float)Parse(parts[0]) });
					break;
				case "adddest":
					if (current != null && parts.Length >= 2) AddMarker(new DestinationMarker { Name = parts[0], Position = Parse(parts[1]) });
					break;
				case "addtransition":
					if (current != null && parts.Length >= 2)
					{
						// addtransition=Position,DestinationName (a point marker: it fires when the cursor crosses it)
						AudioMarker destination = current.Markers.Find(m => m is DestinationMarker && m.Name == parts[1]);
						AddMarker(new TransitionMarker { Position = Parse(parts[0]), DestinationId = destination?.Id ?? TransitionMarker.EndDestination });
					}

					break;
				case "selectmarker":
				{
					AudioMarker marker = current?.Markers.Find(m => m.Name == arg);
					if (marker != null) selection = Selection.Of(SelectionKind.Marker, marker.Id);
					break;
				}
				case "sheet":
				{
					if (arg == "timeline")
					{
						currentSheet = null;
						tabSwitchRequested = true;
					}
					else
					{
						AudioParameter parameter = current != null ? Project.ParametersOf(current).Find(x => x.Name == arg) : null;
						if (parameter != null)
						{
							openSheets.Add(parameter.Id);
							currentSheet = parameter.Id;
							tabSwitchRequested = true;
						}
					}

					break;
				}
				case "addparam":
					if (current != null && parts.Length >= 3)
					{
						Edit("Add parameter", () => current.Parameters.Add(new AudioParameter { Id = AudioProject.NewId(), Name = parts[0], Min = (float)Parse(parts[1]), Max = (float)Parse(parts[2]) }));
					}

					break;
				case "select":
				{
					int colon = arg.IndexOf(':');
					if (colon > 0 && Enum.TryParse(arg.Substring(0, colon), true, out SelectionKind kind))
					{
						selection = Selection.Of(kind, arg.Substring(colon + 1));
					}

					break;
				}
				case "undo": Undo(); break;
				case "redo": Redo(); break;
				case "save": Save(); break;
				case "delete": DeleteSelection(); break;
				case "zoom": zoom = (float)Parse(arg); break;
				case "scroll": scroll = Parse(arg); break;
				case "buildbanks": BuildBanks(arg); break;
				case "report":
				{
					AudioEvent e = SelectedEvent;
					int instruments = 0;
					if (e != null)
					{
						foreach (AudioTrack track in e.Tracks) instruments += track.Instruments.Count;
					}

					Debug.Log($"[Audio Studio] project={Project?.AssetPath ?? "none"} events={Project?.Events.Count ?? 0} selected={(e != null ? e.Path : "none")} tracks={e?.Tracks.Count ?? 0} instruments={instruments} markers={e?.Markers.Count ?? 0} snapshots={Project?.Snapshots.Count ?? 0} selection={selection.Kind}:{selection.Id} dirty={Dirty} undo={undoStack.Count} redo={redoStack.Count} preview={(Preview != null ? Preview.State.ToString() : "none")} position={(Preview != null ? Preview.TimelinePosition.ToString("0.000") : "-")} transition={(Preview != null ? Preview.Playback.ActiveTransitionIndex + "@" + Preview.Playback.TransitionSeconds.ToString("0.000") : "-")} sheet={currentSheet ?? "timeline"} activesnapshots={AudioSystem.ActiveSnapshots} status=\"{status}\" device=\"{AudioSystem.DeviceStatus}\" voices={AudioSystem.ActiveVoices}");
					TransitionTimeline shown = ExpandedTransition?.Timeline;
					if (shown != null)
					{
						Debug.Log($"[Audio Studio] transition timeline length={shown.Length:0.000} source={shown.SourceLength:0.000} destination={shown.DestinationStart:0.000} sourcefade={shown.SourceFadeOut.Length:0.000} destinationfade={shown.DestinationFadeIn.Length:0.000} pieces={pieces.Count} stacked={pieces.FindAll(p => p.Rows > 1).Count}");
					}

					Debug.Log($"[Audio Studio] live={liveInstances.Count} followed={liveInstanceId} shown={(Shown != null ? "#" + Shown.Id + (IsLiveShown ? " live" : " preview") : "none")} beats={showBeats} zoom={zoom:0.0} scroll={scroll:0.000}");

					break;
				}
				case "mouse":
				{
					string[] xy = arg.Split(',');
					float x = (float)Parse(xy[0]), y = (float)Parse(xy[1]);
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x, y));
					break;
				}
				case "click":
				{
					string[] xy = arg.Split(',');
					float x = (float)Parse(xy[0]), y = (float)Parse(xy[1]);
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x, y));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, true));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, false));
					break;
				}
				case "drag":
				{
					string[] v = arg.Split(',');
					float x1 = (float)Parse(v[0]), y1 = (float)Parse(v[1]), x2 = (float)Parse(v[2]), y2 = (float)Parse(v[3]);
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x1, y1));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, true));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent((x1 + x2) * 0.5f, (y1 + y2) * 0.5f));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x2, y2));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, false));
					break;
				}
				default:
					Debug.LogWarning("[Audio Studio] Unknown test command: " + command);
					break;
			}
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- edits and undo

		/// <summary>An instant change (button, checkbox, combo): one undo step.</summary>
		private void Edit(string name, Action change)
		{
			if (Project == null)
			{
				return;
			}

			PushUndo();
			try
			{
				change();
			}
			finally
			{
				Changed();
			}
		}

		/// <summary>
		///     A change from a continuous widget (text, drag): the undo step covers the whole edit session, from the
		///     widget's activation to its release. Call right after the widget with its changed flag.
		/// </summary>
		private void Track(bool changed)
		{
			if (Project == null)
			{
				return;
			}

			if (ImGui.IsItemActivated())
			{
				editSnapshot = Project.ToBytes();
			}

			if (changed)
			{
				if (editSnapshot == null && !ImGui.IsItemActive())
				{
					// A widget that changes without an activation (keyboard step): its own step.
					PushUndo();
				}

				Changed();
			}

			if (ImGui.IsItemDeactivatedAfterEdit() && editSnapshot != null)
			{
				undoStack.Add(editSnapshot);
				Trim(undoStack);
				redoStack.Clear();
				editSnapshot = null;
			}
			else if (ImGui.IsItemDeactivated())
			{
				editSnapshot = null;
			}
		}

		private void PushUndo()
		{
			undoStack.Add(Project.ToBytes());
			Trim(undoStack);
			redoStack.Clear();
		}

		private static void Trim(List<byte[]> stack)
		{
			if (stack.Count > 100)
			{
				stack.RemoveAt(0);
			}
		}

		private void Changed()
		{
			Dirty = true;
			Project.MarkChanged();
			liveUpdatePending = true;
		}

		private void Undo()
		{
			if (undoStack.Count == 0 || Project == null)
			{
				return;
			}

			redoStack.Add(Project.ToBytes());
			byte[] state = undoStack[undoStack.Count - 1];
			undoStack.RemoveAt(undoStack.Count - 1);
			Restore(state);
		}

		private void Redo()
		{
			if (redoStack.Count == 0 || Project == null)
			{
				return;
			}

			undoStack.Add(Project.ToBytes());
			byte[] state = redoStack[redoStack.Count - 1];
			redoStack.RemoveAt(redoStack.Count - 1);
			Restore(state);
		}

		private void Restore(byte[] state)
		{
			Project.ApplyBytes(state);
			Dirty = savedState == null || !EditorSerialization.SameState(savedState, state);
			liveUpdatePending = true;
			if (SelectedEvent == null)
			{
				selectedEventId = Project.Events.Count > 0 ? Project.Events[0].Id : null;
				selection = default;
			}
		}

		/// <summary>After a frame with edits: the audio system rebuilds the banks from the project (playing instances follow).</summary>
		private void FlushLiveUpdate()
		{
			if (!liveUpdatePending)
			{
				return;
			}

			liveUpdatePending = false;
			if (AudioSystem.SourceProject == Project)
			{
				EnsureBanksLoaded();
				AudioSystem.ReloadFromProject();
			}

			editor.NotifySceneChanged();
		}

		/// <summary>Every bank of the project is loaded in the editor, so any event can be previewed.</summary>
		private void EnsureBanksLoaded()
		{
			if (Project == null || AudioSystem.SourceProject != Project)
			{
				return;
			}

			foreach (AudioBank bank in Project.Banks)
			{
				if (!AudioSystem.IsBankLoaded(bank.Name))
				{
					AudioSystem.LoadBank(bank.Name);
				}
			}
		}

		private void SetStatus(string text)
		{
			status = text;
			statusAt = ImGui.GetTime();
		}

		// ---------------------------------------------------------------- selection and structure

		private void SelectEvent(string id)
		{
			if (selectedEventId != id)
			{
				StopPreview();
				selectedEventId = id;
				currentSheet = null;
				expandedTransitionId = null;
				liveInstanceId = 0;
				previewParameters.Clear();
			}

			selection = Selection.Of(SelectionKind.Event, id);
		}

		private void CreateEvent(string path)
		{
			if (Project == null)
			{
				return;
			}

			AudioEvent created = null;
			Edit("Create event", () => created = Project.CreateEvent(path));
			if (created != null)
			{
				SelectEvent(created.Id);
			}
		}

		private void DuplicateEvent(AudioEvent source)
		{
			AudioEvent copy = EventCompiler.CloneEvent(source);
			copy.Id = AudioProject.NewId();
			copy.Path = Project.UniqueEventPath(source.Path);
			Edit("Duplicate event", () => Project.Events.Add(copy));
			SelectEvent(copy.Id);
		}

		private void DeleteEvent(AudioEvent audioEvent)
		{
			Edit("Delete event", () => Project.Events.Remove(audioEvent));
			if (selectedEventId == audioEvent.Id)
			{
				StopPreview();
				selectedEventId = Project.Events.Count > 0 ? Project.Events[0].Id : null;
				selection = selectedEventId != null ? Selection.Of(SelectionKind.Event, selectedEventId) : default;
			}
		}

		private void AddInstrument(AudioTrack track, AudioInstrument instrument, double start, double length)
		{
			AddInstrument(track, instrument, start, length, currentSheet);
		}

		/// <summary>Adds an instrument to a sheet: null the timeline, a parameter's id, or a transition timeline's sheet.</summary>
		private void AddInstrument(AudioTrack track, AudioInstrument instrument, double start, double length, string sheet)
		{
			instrument.Id = AudioProject.NewId();
			instrument.Sheet = sheet;
			instrument.Start = start;
			instrument.Length = Math.Max(0.01, length);
			if (sheet != null && TransitionTimeline.TransitionOfSheet(sheet) == null)
			{
				// Parameter sheet layers loop by default.
				instrument.Loop = true;
			}

			Edit("Add " + instrument.Kind.ToLower() + " instrument", () => track.Instruments.Add(instrument));
			selection = Selection.Of(SelectionKind.Instrument, instrument.Id);
		}

		private void AddMarker(AudioMarker marker)
		{
			AudioEvent current = SelectedEvent;
			if (current == null)
			{
				return;
			}

			marker.Id = AudioProject.NewId();
			if (marker.Name == null)
			{
				marker.Name = marker is DestinationMarker ? "Marker " + (current.Markers.Count + 1) : marker.Kind;
			}

			Edit("Add " + marker.Kind.ToLower(), () => current.Markers.Add(marker));
			selection = Selection.Of(SelectionKind.Marker, marker.Id);
		}

		/// <summary>Deletes what is selected in the event editor (instrument, marker, track).</summary>
		private void DeleteSelection()
		{
			AudioEvent current = SelectedEvent;
			if (current == null)
			{
				return;
			}

			switch (selection.Kind)
			{
				case SelectionKind.Instrument:
				{
					AudioInstrument instrument = current.FindInstrument(selection.Id, out AudioTrack owner);
					if (instrument != null)
					{
						Edit("Delete instrument", () => owner.Instruments.Remove(instrument));
						selection = Selection.Of(SelectionKind.Track, owner.Id);
					}

					break;
				}
				case SelectionKind.Marker:
				{
					AudioMarker marker = current.FindMarker(selection.Id);
					if (marker != null)
					{
						Edit("Delete marker", () => current.Markers.Remove(marker));
						selection = Selection.Of(SelectionKind.Event, current.Id);
					}

					break;
				}
				case SelectionKind.Track:
				{
					AudioTrack track = current.FindTrack(selection.Id);
					if (track != null && track != current.Master)
					{
						Edit("Delete track", () => current.Tracks.Remove(track));
						selection = Selection.Of(SelectionKind.Event, current.Id);
					}

					break;
				}
			}
		}

		private void DuplicateSelection()
		{
			AudioEvent current = SelectedEvent;
			if (current == null)
			{
				return;
			}

			if (selection.Kind == SelectionKind.Instrument)
			{
				AudioInstrument instrument = current.FindInstrument(selection.Id, out AudioTrack owner);
				if (instrument != null)
				{
					AudioInstrument copy = instrument.Clone();
					copy.Id = AudioProject.NewId();
					copy.Start = instrument.End;
					Edit("Duplicate instrument", () => owner.Instruments.Add(copy));
					selection = Selection.Of(SelectionKind.Instrument, copy.Id);
				}
			}
			else if (selection.Kind == SelectionKind.Marker)
			{
				AudioMarker marker = current.FindMarker(selection.Id);
				if (marker != null)
				{
					AudioMarker copy = marker.Clone();
					copy.Id = AudioProject.NewId();
					copy.Position = marker.End + 0.5;
					Edit("Duplicate marker", () => current.Markers.Add(copy));
					selection = Selection.Of(SelectionKind.Marker, copy.Id);
				}
			}
			else if (selection.Kind == SelectionKind.Event)
			{
				DuplicateEvent(current);
			}
		}

		// ---------------------------------------------------------------- preview

		private void PlayPreview()
		{
			AudioEvent current = SelectedEvent;
			if (current == null || !AudioSystem.IsInitialized)
			{
				return;
			}

			FlushLiveUpdate();
			StopPreview();
			preview = AudioSystem.CreateInstance(AudioEventReference.FromEvent(current));
			if (preview == null)
			{
				SetStatus("The event is not loaded (is its bank in the project?)");
				return;
			}

			foreach (KeyValuePair<string, float> entry in previewParameters)
			{
				AudioParameter parameter = Project.FindParameter(current, entry.Key);
				if (parameter != null)
				{
					preview.SetParameter(parameter.Name, entry.Value, true);
				}
			}

			if (current.Spatializer != null && current.Spatializer.Enabled)
			{
				preview.Set3DAttributes(PreviewPosition());
			}

			preview.Start();
		}

		private void StopPreview(AudioStopMode mode = AudioStopMode.AllowFadeout)
		{
			if (preview != null)
			{
				preview.Stop(mode);
				preview.Release();
				preview = null;
			}
		}

		private Vector3 PreviewPosition()
		{
			Vector3 listener = AudioSystem.ListenerPosition;
			float angle = MathHelper.DegreesToRadians(previewAngle);
			return listener + new Vector3(MathF.Sin(angle) * previewDistance, 0f, -MathF.Cos(angle) * previewDistance);
		}

		private void SetPreviewParameter(string nameOrId, float value)
		{
			AudioEvent current = SelectedEvent;
			if (current == null)
			{
				return;
			}

			AudioParameter parameter = Project.FindParameter(current, nameOrId) ?? Project.ParametersOf(current).Find(p => string.Equals(p.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
			if (parameter == null)
			{
				return;
			}

			previewParameters[parameter.Id] = value;
			if (Project.FindGlobalParameter(parameter.Id) != null)
			{
				AudioSystem.SetGlobalParameter(parameter.Name, value);
			}
			else
			{
				Preview?.SetParameter(parameter.Name, value);
			}
		}

		private void UpdatePreviewState()
		{
			if (preview != null && !preview.IsValid)
			{
				preview = null;
			}

			if (previewSnapshot != null && !previewSnapshot.IsActive)
			{
				previewSnapshot = null;
			}

			// Loop playback: a preview that ended by itself starts again.
			if (preview != null)
			{
				if (preview.IsPlaying)
				{
					previewSeenPlaying = true;
				}
				else if (preview.State == PlaybackState.Stopped && previewSeenPlaying)
				{
					previewSeenPlaying = false;
					if (loopPlayback)
					{
						PlayPreview();
					}
				}
			}
		}

		// ---------------------------------------------------------------- snapshot preview

		private SnapshotInstance previewSnapshot;
		private float previewSnapshotIntensity = 1f;

		private void StartPreviewSnapshot(AudioSnapshot snapshot)
		{
			if (snapshot == null || !AudioSystem.IsInitialized)
			{
				return;
			}

			FlushLiveUpdate();
			if (previewSnapshot != null && previewSnapshot.IsActive && previewSnapshot.Definition.Id == snapshot.Id)
			{
				previewSnapshot.SetIntensity(previewSnapshotIntensity);
				return;
			}

			StopPreviewSnapshot();
			previewSnapshot = AudioSystem.StartSnapshot(snapshot.Name, previewSnapshotIntensity);
			if (previewSnapshot == null)
			{
				SetStatus("The snapshot is not loaded yet (save or edit once)");
			}
		}

		private void StopPreviewSnapshot()
		{
			if (previewSnapshot != null)
			{
				previewSnapshot.Stop();
				previewSnapshot = null;
			}
		}

		// ---------------------------------------------------------------- menu, shortcuts, layout

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMainMenuBar())
			{
				return;
			}

			if (EditorMenu.Begin("File"))
			{
				if (EditorMenu.Item("New Audio Project...")) openNewProjectPopup = true;
				if (EditorMenu.Begin("Open"))
				{
					List<string> projects = AssetDatabase.GetAllAssetPaths(AudioProject.Extension);
					if (projects.Count == 0) ImGui.TextDisabled("No .lelaudio files in the project");
					foreach (string path in projects)
					{
						if (EditorMenu.Item(path)) Open(path);
					}

					EditorMenu.End();
				}

				ImGui.Separator();
				if (EditorMenu.Item("Save", "Ctrl+S", false, Project != null)) Save();
				if (EditorMenu.Item("Revert", "", false, Project != null && Dirty)) Revert();
				ImGui.Separator();
				if (EditorMenu.Item("Build Banks (Library/AudioBanks)", "", false, Project != null)) BuildBanks(null);
				ImGui.Separator();
				if (EditorMenu.Item("Close")) window.Close();
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Edit"))
			{
				if (EditorMenu.Item("Undo", "Ctrl+Z", false, undoStack.Count > 0)) Undo();
				if (EditorMenu.Item("Redo", "Ctrl+Y", false, redoStack.Count > 0)) Redo();
				ImGui.Separator();
				if (EditorMenu.Item("Duplicate", "Ctrl+D", false, selection.Kind != SelectionKind.None)) DuplicateSelection();
				if (EditorMenu.Item("Delete", "Del", false, selection.Kind == SelectionKind.Instrument || selection.Kind == SelectionKind.Marker || selection.Kind == SelectionKind.Track)) DeleteSelection();
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Event", Project != null))
			{
				if (EditorMenu.Item("New Event...")) RequestNewEvent(SelectedEvent?.Folder);
				if (EditorMenu.Item("New Folder...")) openNewFolderPopup = true;
				ImGui.Separator();
				AudioEvent current = SelectedEvent;
				if (EditorMenu.Item("Add Track", "", false, current != null)) Edit("Add track", () => current.Tracks.Add(AudioProject.CreateTrack("Audio " + (current.Tracks.Count + 1))));
				if (EditorMenu.Begin("Add Parameter Sheet", current != null))
				{
					DrawAddSheetMenu(current);
					EditorMenu.End();
				}

				ImGui.Separator();
				if (EditorMenu.Item("Play", "Space", false, current != null)) PlayPreview();
				if (EditorMenu.Item("Stop", "Space", false, Preview != null)) StopPreview();
				EditorMenu.End();
			}

			if (EditorMenu.Begin("View"))
			{
				EditorMenu.Item("Events", "", ref showEvents);
				EditorMenu.Item("Banks", "", ref showBanks);
				EditorMenu.Item("Parameters", "", ref showParameters);
				EditorMenu.Item("Snapshots", "", ref showSnapshots);
				EditorMenu.Item("Assets", "", ref showAssets);
				EditorMenu.Item("Deck", "", ref showDeck);
				EditorMenu.Item("Overview", "", ref showOverview);
				EditorMenu.Item("Mixer", "", ref showMixer);
				ImGui.Separator();
				EditorMenu.Item("Snap to Grid", "", ref snap);
				EditorMenu.Item("Follow Playhead", "", ref followPlayhead);
				ImGui.Separator();
				if (EditorMenu.Item("Reset Layout"))
				{
					showEvents = showBanks = showParameters = showAssets = showDeck = showOverview = showMixer = showSnapshots = true;
					resetLayoutRequested = true;
				}

				EditorMenu.End();
			}

			// Status on the right: the device, then the title.
			string device = AudioSystem.IsDeviceOpen ? "" : "no audio device";
			string shown = ImGui.GetTime() - statusAt < 6.0 ? status : "";
			float width = ImGui.CalcTextSize(Title).X + 16;
			float right = ImGui.GetWindowWidth() - width;
			if (!string.IsNullOrEmpty(device))
			{
				right -= ImGui.CalcTextSize(device).X + 24;
			}

			if (!string.IsNullOrEmpty(shown))
			{
				ImGui.SetCursorPosX(right - ImGui.CalcTextSize(shown).X - 24);
				ImGui.TextDisabled(shown);
			}

			if (!string.IsNullOrEmpty(device))
			{
				ImGui.SetCursorPosX(ImGui.GetWindowWidth() - width - ImGui.CalcTextSize(device).X - 24);
				ImGui.TextColored(EditorStyle.Warning, device);
			}

			ImGui.SetCursorPosX(ImGui.GetWindowWidth() - width);
			ImGui.TextUnformatted(Title);
			ImGui.EndMainMenuBar();
		}

		private void DrawAddSheetMenu(AudioEvent current)
		{
			if (current == null)
			{
				return;
			}

			bool any = false;
			foreach (AudioParameter parameter in Project.ParametersOf(current))
			{
				any = true;
				if (EditorMenu.Item(parameter.Name + (Project.FindGlobalParameter(parameter.Id) != null ? "  (global)" : "")))
				{
					currentSheet = parameter.Id;
				}
			}

			if (!any)
			{
				ImGui.TextDisabled("Add a parameter first (Overview).");
			}
		}

		private void HandleShortcuts()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			bool ctrl = io.KeyCtrl;
			if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
			{
				Save();
			}

			if (io.WantTextInput)
			{
				return;
			}

			if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z, false)) Undo();
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y, false)) Redo();
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, false)) DuplicateSelection();
			else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) DeleteSelection();
			else if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
			{
				if (Preview != null && Preview.IsPlaying) StopPreview();
				else PlayPreview();
			}
		}

		/// <summary>Bumped when the default layout gains a panel: a saved layout of an older version is rebuilt once.</summary>
		public const int LayoutVersion = 2;

		/// <summary>True when the saved layout was written by this layout version (and the version file is updated).</summary>
		public static bool LayoutIsCurrent(string layoutPath)
		{
			string versionPath = layoutPath + ".version";
			try
			{
				bool current = System.IO.File.Exists(layoutPath) && System.IO.File.Exists(versionPath) && System.IO.File.ReadAllText(versionPath).Trim() == LayoutVersion.ToString();
				if (!current)
				{
					System.IO.File.WriteAllText(versionPath, LayoutVersion.ToString());
				}

				return current;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private unsafe void BuildDefaultLayout(uint dockspace)
		{
			ImGuiP.DockBuilderRemoveNode(dockspace);
			ImGuiP.DockBuilderAddNode(dockspace, (ImGuiDockNodeFlags)(1 << 10));
			ImGuiP.DockBuilderSetNodeSize(dockspace, ImGui.GetMainViewport().Size);

			uint center = dockspace;
			uint right = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.24f, null, &center);
			uint left = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.22f, null, &center);
			uint bottom = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.36f, null, &center);
			uint leftBottom = ImGuiP.DockBuilderSplitNode(left, ImGuiDir.Down, 0.4f, null, &left);
			uint rightBottom = ImGuiP.DockBuilderSplitNode(right, ImGuiDir.Down, 0.45f, null, &right);

			ImGuiP.DockBuilderDockWindow("Events", left);
			ImGuiP.DockBuilderDockWindow("Banks", left);
			ImGuiP.DockBuilderDockWindow("Parameters", left);
			ImGuiP.DockBuilderDockWindow("Snapshots", left);
			ImGuiP.DockBuilderDockWindow("Assets", leftBottom);
			ImGuiP.DockBuilderDockWindow("Event Editor", center);
			ImGuiP.DockBuilderDockWindow("Deck", bottom);
			ImGuiP.DockBuilderDockWindow("Overview", right);
			ImGuiP.DockBuilderDockWindow("Mixer", rightBottom);
			ImGuiP.DockBuilderFinish(dockspace);
		}

		// ---------------------------------------------------------------- popups

		private void RequestNewEvent(string folder)
		{
			newEventPath = string.IsNullOrEmpty(folder) ? "New Event" : folder + "/New Event";
			openNewEventPopup = true;
		}

		private void RequestRename(Selection target, string currentName)
		{
			renameTarget = target;
			renameBuffer = currentName ?? "";
			openRenamePopup = true;
		}

		private void DrawPopups()
		{
			if (openCloseConfirm)
			{
				ImGui.OpenPopup("Unsaved changes##audio");
				openCloseConfirm = false;
			}

			if (openNewEventPopup)
			{
				ImGui.OpenPopup("New event");
				openNewEventPopup = false;
			}

			if (openRenamePopup)
			{
				ImGui.OpenPopup("Rename##audio");
				openRenamePopup = false;
			}

			if (openNewProjectPopup)
			{
				ImGui.OpenPopup("New audio project");
				openNewProjectPopup = false;
			}

			if (openNewFolderPopup)
			{
				ImGui.OpenPopup("New folder##audio");
				openNewFolderPopup = false;
			}

			if (pendingDeleteEvent != null)
			{
				ImGui.OpenPopup("Delete event");
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Unsaved changes##audio", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("The audio project has unsaved changes.");
				if (EditorGui.Button("Save", new NVector2(110, 0)))
				{
					if (Save())
					{
						closeConfirmed = true;
						ImGui.CloseCurrentPopup();
						window.Close();
					}
				}

				ImGui.SameLine();
				if (EditorGui.Button("Don't save", new NVector2(110, 0)))
				{
					closeConfirmed = true;
					ImGui.CloseCurrentPopup();
					// The audio system keeps the edited project in memory: the file is the truth again.
					Revert();
					window.Close();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
				}

				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("New event", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("Event path (folders separated by /):");
				if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
				ImGui.SetNextItemWidth(360);
				bool enter = EditorGui.InputText("##eventpath", ref newEventPath, 256, ImGuiInputTextFlags.EnterReturnsTrue);
				if ((EditorGui.Button("Create", new NVector2(110, 0)) || enter) && !string.IsNullOrWhiteSpace(newEventPath))
				{
					CreateEvent(newEventPath.Trim());
					ImGui.CloseCurrentPopup();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0))) ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("New folder##audio", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("Folder path:");
				if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
				ImGui.SetNextItemWidth(360);
				bool enter = EditorGui.InputText("##folderpath", ref newFolderPath, 256, ImGuiInputTextFlags.EnterReturnsTrue);
				if ((EditorGui.Button("Create", new NVector2(110, 0)) || enter) && !string.IsNullOrWhiteSpace(newFolderPath))
				{
					string folder = newFolderPath.Trim().Replace('\\', '/').Trim('/');
					Edit("Create folder", () =>
					{
						if (!Project.Folders.Contains(folder)) Project.Folders.Add(folder);
					});
					ImGui.CloseCurrentPopup();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0))) ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Rename##audio", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text(renameTarget.Kind == SelectionKind.Event ? "Event path:" : "Name:");
				if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
				ImGui.SetNextItemWidth(360);
				bool enter = EditorGui.InputText("##rename", ref renameBuffer, 256, ImGuiInputTextFlags.EnterReturnsTrue);
				if ((EditorGui.Button("Rename", new NVector2(110, 0)) || enter) && !string.IsNullOrWhiteSpace(renameBuffer))
				{
					ApplyRename(renameTarget, renameBuffer.Trim());
					ImGui.CloseCurrentPopup();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0))) ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("New audio project", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("Asset path of the new audio project (inside Assets):");
				if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
				ImGui.SetNextItemWidth(360);
				bool enter = EditorGui.InputText("##projectpath", ref newProjectPath, 256, ImGuiInputTextFlags.EnterReturnsTrue);
				ImGui.TextDisabled("A game has one audio project; a second one needs Project Settings > Audio to choose it.");
				if ((EditorGui.Button("Create", new NVector2(110, 0)) || enter) && !string.IsNullOrWhiteSpace(newProjectPath))
				{
					CreateProject(newProjectPath.Trim());
					ImGui.CloseCurrentPopup();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0))) ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Delete event", ImGuiWindowFlags.AlwaysAutoResize))
			{
				AudioEvent target = Project?.FindEvent(pendingDeleteEvent);
				ImGui.Text("Delete the event '" + (target?.Path ?? "?") + "'?");
				ImGui.TextDisabled("Emitters referencing it keep its GUID and play nothing.");
				if (EditorGui.Button("Delete", new NVector2(110, 0)))
				{
					if (target != null) DeleteEvent(target);
					pendingDeleteEvent = null;
					ImGui.CloseCurrentPopup();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0)))
				{
					pendingDeleteEvent = null;
					ImGui.CloseCurrentPopup();
				}

				ImGui.EndPopup();
			}
		}

		private void ApplyRename(Selection target, string text)
		{
			switch (target.Kind)
			{
				case SelectionKind.Event:
				{
					AudioEvent audioEvent = Project.FindEvent(target.Id);
					if (audioEvent != null)
					{
						string path = text.Replace('\\', '/').Trim('/');
						if (Project.FindEventByPath(path) == null || string.Equals(path, audioEvent.Path, StringComparison.OrdinalIgnoreCase))
						{
							Edit("Rename event", () => audioEvent.Path = path);
						}
						else
						{
							SetStatus("An event with this path exists already");
						}
					}

					break;
				}
				case SelectionKind.Track:
				{
					AudioTrack track = SelectedEvent?.FindTrack(target.Id);
					if (track != null) Edit("Rename track", () => track.Name = text);
					break;
				}
				case SelectionKind.Bus:
				{
					AudioBus bus = Project.FindBus(target.Id);
					if (bus != null) Edit("Rename bus", () => bus.Name = text);
					break;
				}
				case SelectionKind.Bank:
				{
					AudioBank bank = Project.FindBank(target.Id);
					if (bank != null && bank.Id != AudioProject.MasterBankId) Edit("Rename bank", () => bank.Name = text);
					break;
				}
				case SelectionKind.GlobalParameter:
				case SelectionKind.LocalParameter:
				{
					AudioParameter parameter = Project.FindParameter(SelectedEvent, target.Id);
					if (parameter != null) Edit("Rename parameter", () => parameter.Name = text);
					break;
				}
				case SelectionKind.Snapshot:
				{
					AudioSnapshot snapshot = Project.FindSnapshot(target.Id);
					if (snapshot != null) Edit("Rename snapshot", () => snapshot.Name = text);
					break;
				}
			}
		}

		// ---------------------------------------------------------------- banks

		/// <summary>Builds every bank into the folder (null: Library/AudioBanks) and reports the sizes.</summary>
		private void BuildBanks(string folder)
		{
			if (Project == null)
			{
				return;
			}

			folder = string.IsNullOrEmpty(folder) ? Path.Combine(editor.Project.LibraryPath, "AudioBanks") : folder;
			var lines = new List<string>();
			bool ok = AudioBankBuilder.BuildAll(Project, folder, lines.Add);
			foreach (string line in lines)
			{
				Debug.Log("[Audio Studio] " + line);
			}

			SetStatus(ok ? "Banks built into " + folder : "Bank build failed (see the console)");
		}

		private static double Parse(string text)
		{
			return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0.0;
		}

		private static string FormatTime(double seconds)
		{
			if (seconds < 0) seconds = 0;
			int minutes = (int)(seconds / 60);
			double rest = seconds - minutes * 60;
			return minutes > 0 ? $"{minutes}:{rest:00.000}" : $"{rest:0.000}";
		}

		private static uint Color(float r, float g, float b, float a = 1f)
		{
			return ImGui.GetColorU32(new NVector4(r, g, b, a));
		}

		private static uint Color(Color4 color, float alpha = 1f)
		{
			return ImGui.GetColorU32(new NVector4(color.R, color.G, color.B, color.A * alpha));
		}

		#endregion
	}
}
