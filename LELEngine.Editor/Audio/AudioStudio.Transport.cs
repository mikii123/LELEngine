using System;
using System.Collections.Generic;
using System.Globalization;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     The Event Editor's transport bar (FMOD Studio's): stop / play / pause, the time display (TIME or BEATS, the
	///     playback state), loop playback and follow toggles, the game's instances of the event while the editor plays
	///     (live preview: the editor shows the followed instance's playhead, parameters and meters), a knob per
	///     parameter, and snapping and zoom. Also the level meters' state.
	/// </summary>
	internal sealed partial class AudioStudio
	{
		#region PrivateFields

		private bool showBeats;
		private bool loopPlayback;

		/// <summary>Follow the newest game instance of the event automatically (else the one picked, or none).</summary>
		private bool followGame = true;

		private int liveInstanceId;
		private readonly List<EventInstance> liveInstances = new List<EventInstance>();
		private readonly Dictionary<string, float> trackLevels = new Dictionary<string, float>(StringComparer.Ordinal);
		private readonly List<string> levelKeys = new List<string>();
		private float masterLevel;
		private double levelTime;
		private bool previewSeenPlaying;

		#endregion

		#region PrivateMethods

		/// <summary>The instance the editor shows: the studio's preview while there is one, else the followed game instance.</summary>
		private EventInstance Shown => Preview ?? Live;

		/// <summary>The game instance followed (play mode), or null.</summary>
		private EventInstance Live
		{
			get
			{
				foreach (EventInstance instance in liveInstances)
				{
					if (instance.Id == liveInstanceId)
					{
						return instance;
					}
				}

				return null;
			}
		}

		/// <summary>The editor shows a game instance (live preview), not its own.</summary>
		private bool IsLiveShown => Preview == null && Live != null;

		/// <summary>Every frame: the game's instances of the selected event, the one to follow, and the meters.</summary>
		private void UpdateLive(AudioEvent current)
		{
			liveInstances.Clear();
			if (current != null && AudioSystem.IsInitialized)
			{
				foreach (EventInstance instance in AudioSystem.Instances)
				{
					if (instance == preview || !instance.IsValid || instance.Description == null || instance.Description.Id != current.Id)
					{
						continue;
					}

					if (instance.State == PlaybackState.Stopped)
					{
						continue;
					}

					liveInstances.Add(instance);
				}

				liveInstances.Sort((a, b) => a.Id.CompareTo(b.Id));
			}

			bool followedAlive = false;
			foreach (EventInstance instance in liveInstances)
			{
				followedAlive |= instance.Id == liveInstanceId;
			}

			if (!followedAlive)
			{
				// The followed instance ended (or none was followed): the newest one, when following automatically.
				liveInstanceId = followGame && liveInstances.Count > 0 ? liveInstances[liveInstances.Count - 1].Id : 0;
			}

			UpdateLevels();
		}

		/// <summary>Game instances of an event playing now (the studio's own preview excluded).</summary>
		private int LiveCount(string eventId)
		{
			if (!AudioSystem.IsInitialized)
			{
				return 0;
			}

			int count = 0;
			foreach (EventInstance instance in AudioSystem.Instances)
			{
				if (instance != preview && instance.IsValid && instance.State != PlaybackState.Stopped && instance.Description?.Id == eventId)
				{
					count++;
				}
			}

			return count;
		}

		private void UpdateLevels()
		{
			double now = ImGui.GetTime();
			float elapsed = (float)Math.Clamp(now - levelTime, 0.0, 0.5);
			levelTime = now;
			float decay = MathF.Exp(-elapsed * 6f);
			levelKeys.Clear();
			levelKeys.AddRange(trackLevels.Keys);
			foreach (string key in levelKeys)
			{
				trackLevels[key] *= decay;
			}

			masterLevel *= decay;
			EventInstance shown = Shown;
			if (shown == null || !shown.IsPlaying || (shown == preview && previewPaused))
			{
				return;
			}

			float[] peaks = shown.Playback.TrackPeaks;
			CompiledTrack[] tracks = shown.Description.Tracks;
			for (int i = 0; i < tracks.Length && i < peaks.Length; i++)
			{
				trackLevels.TryGetValue(tracks[i].Id, out float level);
				trackLevels[tracks[i].Id] = Math.Max(level, peaks[i]);
			}

			masterLevel = Math.Max(masterLevel, Math.Max(shown.PeakLeft, shown.PeakRight));
		}

		private float TrackLevel(string trackId)
		{
			return trackLevels.TryGetValue(trackId, out float level) ? level : 0f;
		}

		/// <summary>The value the editor shows for a parameter: the live instance's, else the preview's setting, the global value or the default.</summary>
		private float ShownParameterValue(AudioParameter parameter)
		{
			if (IsLiveShown)
			{
				float live = Live.GetParameter(parameter.Name);
				if (!float.IsNaN(live))
				{
					return live;
				}
			}

			if (previewParameters.TryGetValue(parameter.Id, out float stored))
			{
				return stored;
			}

			if (Project.FindGlobalParameter(parameter.Id) != null)
			{
				float global = AudioSystem.GetGlobalParameter(parameter.Name);
				if (!float.IsNaN(global))
				{
					return global;
				}
			}

			if (Preview != null)
			{
				float value = Preview.GetParameter(parameter.Name);
				if (!float.IsNaN(value))
				{
					return value;
				}
			}

			return parameter.Default;
		}

		private static string ParameterValueText(AudioParameter parameter, float value)
		{
			switch (parameter.Type)
			{
				case AudioParameterType.Labeled:
				{
					int index = (int)Math.Round(value - Math.Min(parameter.Min, parameter.Max));
					if (index >= 0 && index < parameter.Labels.Count && !string.IsNullOrEmpty(parameter.Labels[index]))
					{
						return parameter.Labels[index];
					}

					return value.ToString("0", CultureInfo.InvariantCulture);
				}
				case AudioParameterType.Discrete:
					return value.ToString("0", CultureInfo.InvariantCulture);
				default:
					return value.ToString("0.00", CultureInfo.InvariantCulture);
			}
		}

		private static string FormatClock(double seconds)
		{
			seconds = Math.Max(0.0, seconds);
			int minutes = (int)(seconds / 60.0);
			double rest = seconds - minutes * 60.0;
			return minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + rest.ToString("00.000", CultureInfo.InvariantCulture);
		}

		private static string FormatBeats(AudioEvent current, double seconds)
		{
			List<TempoMarker> tempos = current.TempoMarkers;
			if (tempos.Count == 0)
			{
				return FormatClock(seconds);
			}

			new BeatGrid(tempos).BarBeat(Math.Max(0.0, seconds) + 1e-6, out int bar, out int beat, out double fraction);
			int sixteenth = 1 + Math.Clamp((int)(fraction * 4.0), 0, 3);
			return bar.ToString("000", CultureInfo.InvariantCulture) + "." + beat + "." + sixteenth;
		}

		private void PlaybackStateText(out string text, out uint color)
		{
			EventInstance own = Preview;
			if (own != null)
			{
				color = StudioUi.Yellow;
				switch (own.State)
				{
					case PlaybackState.Playing:
						text = previewPaused ? "PAUSED" : "PLAYING";
						break;
					case PlaybackState.Sustaining:
						text = "SUSTAINING";
						break;
					case PlaybackState.Stopping:
						text = "STOPPING";
						break;
					case PlaybackState.Created:
						text = "STARTING";
						break;
					default:
						text = "STOPPED";
						color = StudioUi.TextDim;
						break;
				}

				if (own.Playback.ActiveTransitionIndex >= 0)
				{
					text = "TRANSITION";
				}

				return;
			}

			EventInstance live = Live;
			if (live != null)
			{
				text = "LIVE  " + (live.Playback.ActiveTransitionIndex >= 0 ? "TRANSITION" : live.State.ToString().ToUpperInvariant());
				color = StudioUi.LivePlayhead;
				return;
			}

			text = "STOPPED";
			color = StudioUi.TextDim;
		}

		private void DrawTransport(AudioEvent current)
		{
			const float rowHeight = 70f;
			NVector2 row = ImGui.GetCursorScreenPos();
			float width = ImGui.GetContentRegionAvail().X;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(row, row + new NVector2(width, rowHeight), StudioUi.Rgb(0.235f, 0.235f, 0.245f), 3f);
			EventInstance own = Preview;
			bool playing = own != null && own.IsPlaying;

			// Stop, play, pause.
			var buttonSize = new NVector2(38f, 34f);
			float x = row.X + 8f;
			float buttonY = row.Y + (rowHeight - buttonSize.Y) * 0.5f;
			ImGui.SetCursorScreenPos(new NVector2(x, buttonY));
			if (StudioUi.TransportButton("##stop", StudioUi.TransportIcon.Stop, false, buttonSize, "Stop (Space)"))
			{
				StopPreview();
				previewPaused = false;
			}

			x += buttonSize.X + 3f;
			ImGui.SetCursorScreenPos(new NVector2(x, buttonY));
			if (StudioUi.TransportButton("##play", StudioUi.TransportIcon.Play, playing && !previewPaused, buttonSize, "Play the event (Space)"))
			{
				if (playing && previewPaused)
				{
					previewPaused = false;
					own.SetPaused(false);
				}
				else
				{
					previewPaused = false;
					PlayPreview();
				}
			}

			x += buttonSize.X + 3f;
			ImGui.SetCursorScreenPos(new NVector2(x, buttonY));
			if (StudioUi.TransportButton("##pause", StudioUi.TransportIcon.Pause, playing && previewPaused, buttonSize, "Pause / resume") && own != null)
			{
				previewPaused = !previewPaused;
				own.SetPaused(previewPaused);
			}

			x += buttonSize.X + 10f;

			// The time display.
			EventInstance shown = Shown;
			List<TempoMarker> tempos = current.TempoMarkers;
			if (tempos.Count == 0)
			{
				showBeats = false;
			}

			double position = shown != null ? shown.TimelinePosition : previewStartPosition;
			string time = showBeats ? FormatBeats(current, position) : FormatClock(position);
			PlaybackStateText(out string state, out uint stateColor);
			var displaySize = new NVector2(222f, 54f);
			ImGui.SetCursorScreenPos(new NVector2(x, row.Y + (rowHeight - displaySize.Y) * 0.5f));
			StudioUi.TimeDisplay("##lcd", displaySize, time, state, stateColor, ref showBeats, tempos.Count > 0);
			x += displaySize.X + 6f;

			// Loop playback, follow the playhead.
			var toggleSize = new NVector2(28f, 25f);
			ImGui.SetCursorScreenPos(new NVector2(x, row.Y + rowHeight * 0.5f - toggleSize.Y - 1f));
			if (StudioUi.IconToggle("##loop", StudioUi.TransportIcon.Loop, loopPlayback, toggleSize, "Loop playback: the preview starts again when it ends"))
			{
				loopPlayback = !loopPlayback;
			}

			ImGui.SetCursorScreenPos(new NVector2(x, row.Y + rowHeight * 0.5f + 1f));
			if (StudioUi.IconToggle("##follow", StudioUi.TransportIcon.Follow, followPlayhead, toggleSize, "Follow the playhead"))
			{
				followPlayhead = !followPlayhead;
			}

			x += toggleSize.X + 10f;

			// The game's instances while the editor plays.
			if (editor.IsPlaying || liveInstances.Count > 0)
			{
				x = DrawLiveSelector(x, row.Y, rowHeight);
			}

			// The parameter knobs fill the rest (snapping and zoom sit on the sheet tabs' row).
			float knobsWidth = row.X + width - x - 4f;
			if (knobsWidth > 70f)
			{
				ImGui.SetCursorScreenPos(new NVector2(x, row.Y));
				DrawParameterKnobs(current, knobsWidth, rowHeight);
			}

			ImGui.SetCursorScreenPos(new NVector2(row.X, row.Y + rowHeight + 4f));
		}

		/// <summary>The game's instances of the event (play mode): which one the editor follows.</summary>
		private float DrawLiveSelector(float x, float y, float height)
		{
			const float width = 156f;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddText(new NVector2(x, y + 9f), StudioUi.LivePlayhead, "GAME");
			draw.AddText(new NVector2(x + 44f, y + 9f), StudioUi.TextDim, liveInstances.Count + (liveInstances.Count == 1 ? " instance" : " instances"));
			ImGui.SetCursorScreenPos(new NVector2(x, y + 32f));
			ImGui.SetNextItemWidth(width);
			EventInstance live = Live;
			string chosen = liveInstances.Count == 0 ? "none playing" : live != null ? DescribeInstance(live) : "not followed";
			if (EditorGui.BeginCombo("##live", chosen))
			{
				if (EditorGui.Selectable("Follow the newest", followGame))
				{
					followGame = true;
					liveInstanceId = liveInstances.Count > 0 ? liveInstances[liveInstances.Count - 1].Id : 0;
				}

				if (EditorGui.Selectable("Don't follow", !followGame && liveInstanceId == 0))
				{
					followGame = false;
					liveInstanceId = 0;
				}

				if (liveInstances.Count > 0)
				{
					ImGui.Separator();
				}

				foreach (EventInstance instance in liveInstances)
				{
					if (EditorGui.Selectable(DescribeInstance(instance) + "##i" + instance.Id, !followGame && instance.Id == liveInstanceId))
					{
						followGame = false;
						liveInstanceId = instance.Id;
					}
				}

				EditorGui.EndCombo();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Live preview: the editor shows the followed game instance (playhead, parameters, meters) while the studio's own preview is stopped.\nEdits apply to the game's instances at once.");
			}

			return x + width + 10f;
		}

		private static string DescribeInstance(EventInstance instance)
		{
			return "#" + instance.Id + "  " + FormatClock(instance.TimelinePosition) + (instance.State == PlaybackState.Playing ? "" : "  " + instance.State.ToString().ToLowerInvariant());
		}

		/// <summary>Width of <see cref="DrawSnapControls" />.</summary>
		private const float SnapControlsWidth = 300f;

		/// <summary>Snap, its interval and the zoom buttons in one row at (x, y).</summary>
		private void DrawSnapControls(AudioEvent current, float x, float y)
		{
			ImGui.SetCursorScreenPos(new NVector2(x, y));
			EditorGui.Checkbox("Snap", ref snap);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(96f);
			string[] intervals = { "Grid", "1/4 Beat", "1/2 Beat", "Beat", "2 Beats", "Bar", "2 Bars", "4 Bars", "8 Bars" };
			int interval = (int)snapInterval;
			if (ImGui.Combo("##snapinterval", ref interval, intervals, intervals.Length))
			{
				snapInterval = (QuantizationInterval)interval;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Snapping step; the beat steps follow the event's tempo markers.");
			}

			ImGui.SameLine(0f, 14f);
			ImGui.TextDisabled("Zoom");
			ImGui.SameLine();
			if (EditorGui.Button("-##zoomout")) ZoomAround((canvasLeft + canvasRight) * 0.5f, 1f / 1.4f);
			ImGui.SameLine();
			if (EditorGui.Button("+##zoomin")) ZoomAround((canvasLeft + canvasRight) * 0.5f, 1.4f);
			ImGui.SameLine();
			if (EditorGui.Button("Fit"))
			{
				double length = Math.Max(1.0, current.TimelineLength);
				if (view.Active)
				{
					length = Math.Max(length, view.Source + view.Timeline.Length + (view.Destination >= 0.0 ? Math.Max(0.0, current.TimelineLength - view.Destination) : 0.0));
				}

				zoom = (float)Math.Clamp((canvasRight - canvasLeft - 40f) / length, 4.0, 4000.0);
				scroll = 0.0;
			}
		}

		/// <summary>A knob per parameter of the event (FMOD's transport parameters): drives the preview, shows a followed game instance's values.</summary>
		private void DrawParameterKnobs(AudioEvent current, float width, float height)
		{
			List<AudioParameter> parameters = Project.ParametersOf(current);
			// No scrollbars (they would eat the knobs' height): the wheel scrolls sideways when the knobs overflow.
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(2f, 0f));
			bool open = ImGui.BeginChild("##paramknobs", new NVector2(width, height), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground);
			ImGui.PopStyleVar();
			if (open && ImGui.IsWindowHovered() && Math.Abs(ImGui.GetIO().MouseWheel) > 0.01f)
			{
				ImGui.SetScrollX(ImGui.GetScrollX() - ImGui.GetIO().MouseWheel * 40f);
			}
			if (open)
			{
				if (parameters.Count == 0)
				{
					ImGui.SetCursorPos(new NVector2(8f, height * 0.5f - 11f));
					ImGui.TextDisabled("No parameters");
					ImGui.SameLine();
					if (EditorGui.SmallButton("+ Parameter"))
					{
						var parameter = new AudioParameter { Id = AudioProject.NewId(), Name = UniqueParameterName(current, "Parameter") };
						Edit("Add parameter", () => current.Parameters.Add(parameter));
						selection = Selection.Of(SelectionKind.LocalParameter, parameter.Id);
					}
				}
				else
				{
					ImGui.SetCursorPos(new NVector2(2f, 1f));
				}

				bool live = IsLiveShown;
				for (int i = 0; i < parameters.Count; i++)
				{
					AudioParameter parameter = parameters[i];
					ImGui.PushID(parameter.Id);
					bool global = Project.FindGlobalParameter(parameter.Id) != null;
					float value = ShownParameterValue(parameter);
					float min = Math.Min(parameter.Min, parameter.Max);
					float max = Math.Max(parameter.Min, parameter.Max);
					bool readOnly = live || (parameter.BuiltIn != BuiltInParameter.None && !global);
					string tooltip = (global ? "Global parameter" : "Local parameter") + (parameter.BuiltIn != BuiltInParameter.None ? " (built-in: " + parameter.BuiltIn + ")" : "") + "\nRight click: open its sheet, edit it.";
					if (StudioUi.Knob("##knob", ref value, min, max, parameter.Default, parameter.Name, ParameterValueText(parameter, value), StudioUi.KnobScale.Linear, false, 27f, 76f, readOnly, tooltip, parameter.Type == AudioParameterType.Continuous ? 0f : 1f))
					{
						SetPreviewParameter(parameter.Id, parameter.Clamp(value));
					}

					if (EditorGui.BeginPopupContextItem("##param_menu"))
					{
						if (EditorMenu.Item("Open Sheet"))
						{
							openSheets.Add(parameter.Id);
							currentSheet = parameter.Id;
							tabSwitchRequested = true;
						}

						if (EditorMenu.Item("Edit Parameter"))
						{
							selection = Selection.Of(global ? SelectionKind.GlobalParameter : SelectionKind.LocalParameter, parameter.Id);
						}

						if (!global && EditorMenu.Item("Remove Parameter"))
						{
							AudioParameter removed = parameter;
							Edit("Remove parameter", () => current.Parameters.Remove(removed));
						}

						ImGui.EndPopup();
					}

					ImGui.SameLine(0f, 2f);
					ImGui.PopID();
				}
			}

			ImGui.EndChild();
		}

		#endregion
	}
}
