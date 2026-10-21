using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using LELEngine.Serialization;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     The Deck (properties of whatever is selected: event, track, instrument, marker, bus, parameter, bank, audio
	///     file) and the Overview (the event's settings and its parameters with live sliders for the preview).
	/// </summary>
	internal sealed partial class AudioStudio
	{
		#region PrivateFields

		private const float LabelWidth = 128f;
		private static readonly string[] ParameterTypeNames = { "Continuous", "Discrete", "Labeled" };
		private static readonly string[] BuiltInNames = { "None", "Distance", "Direction", "Elevation", "Speed" };
		private static readonly string[] PlaylistModeNames = { "Random", "Shuffle", "Sequential", "Sequential (global)" };
		private static readonly string[] QuantizationNames = { "None", "1/4 Beat", "1/2 Beat", "Beat", "2 Beats", "Bar", "2 Bars", "4 Bars", "8 Bars" };
		private static readonly string[] StealingNames = { "Oldest", "Quietest", "Furthest", "None (new instance refused)" };
		private static readonly string[] RolloffNames = { "Linear", "Linear Squared", "Inverse", "Inverse Tapered", "Custom Curve" };
		private static readonly string[] LoadTypeNames = { "Decompress On Load (PCM in memory)", "Compressed In Memory (decoded at load)", "Streaming (from disk)" };
		private string clipSearch = "";
		private string selectedAutomationTarget;
		private int selectedCurvePoint = -1;

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- field helpers

		private void Label(string label, string tooltip = null)
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			if (tooltip != null && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(tooltip);
			}

			ImGui.SameLine(LabelWidth);
			ImGui.SetNextItemWidth(-1);
		}

		private bool FloatField(string label, ref float value, float speed, float min, float max, string format = "%.2f", string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			float v = value;
			bool changed = ImGui.DragFloat("##v", ref v, speed, min, max, format);
			Track(changed);
			ImGui.PopID();
			if (changed)
			{
				value = v;
			}

			return changed;
		}

		private bool DoubleField(string label, ref double value, float speed, double min, double max, string format = "%.3f", string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			float v = (float)value;
			bool changed = ImGui.DragFloat("##v", ref v, speed, (float)min, (float)max, format);
			Track(changed);
			ImGui.PopID();
			if (changed)
			{
				value = v;
			}

			return changed;
		}

		private bool DbField(string label, ref float value, string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			float v = value;
			bool changed = ImGui.SliderFloat("##v", ref v, AudioMath.SilenceDb, AudioMath.MaxDb, v <= AudioMath.SilenceDb ? "-inf dB" : "%.1f dB");
			Track(changed);
			if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
			{
				v = 0f;
				changed = true;
				PushUndo();
				Changed();
			}

			ImGui.PopID();
			if (changed)
			{
				value = v;
			}

			return changed;
		}

		private bool IntField(string label, ref int value, int min, int max, string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			int v = value;
			bool changed = ImGui.DragInt("##v", ref v, 0.2f, min, max);
			Track(changed);
			ImGui.PopID();
			if (changed)
			{
				value = Math.Clamp(v, min, max);
			}

			return changed;
		}

		private bool knobRowStarted;

		/// <summary>Starts a row of knobs (FMOD's deck modules): the next <see cref="DeckKnob" /> begins a new line.</summary>
		private void KnobRow()
		{
			knobRowStarted = false;
		}

		/// <summary>One knob of a deck row; the row wraps when the column is full. Undoable like the fields.</summary>
		private bool DeckKnob(string label, ref float value, float min, float max, float reset, string text, StudioUi.KnobScale scale = StudioUi.KnobScale.Linear, bool bipolar = false, string tooltip = null, float step = 0f)
		{
			const float width = 70f;
			if (knobRowStarted)
			{
				ImGui.SameLine(0f, 2f);
				if (ImGui.GetContentRegionAvail().X < width)
				{
					ImGui.NewLine();
				}
			}

			knobRowStarted = true;
			ImGui.PushID(label);
			float v = value;
			bool changed = StudioUi.Knob("##knob", ref v, min, max, reset, label, text, scale, bipolar, 32f, width, false, tooltip, step);
			Track(changed);
			ImGui.PopID();
			if (changed)
			{
				value = v;
			}

			return changed;
		}

		private static readonly Dictionary<Type, AudioEffect> effectDefaults = new Dictionary<Type, AudioEffect>();

		/// <summary>A parameter's value on a new effect of the same kind (what a knob's double click resets to).</summary>
		private static float EffectDefault(AudioEffect effect, string parameter)
		{
			if (!effectDefaults.TryGetValue(effect.GetType(), out AudioEffect fresh))
			{
				fresh = Activator.CreateInstance(effect.GetType()) as AudioEffect;
				effectDefaults[effect.GetType()] = fresh;
			}

			return fresh != null ? fresh.GetParameter(parameter) : effect.GetParameter(parameter);
		}

		private static string FormatSeconds(float seconds)
		{
			return seconds < 1f ? (seconds * 1000f).ToString("0") + " ms" : seconds.ToString("0.00") + " s";
		}

		private bool BoolField(string label, ref bool value, string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			bool v = value;
			bool changed = ImGui.Checkbox("##v", ref v);
			ImGui.PopID();
			if (changed)
			{
				PushUndo();
				value = v;
				Changed();
			}

			return changed;
		}

		private bool ComboField(string label, ref int value, string[] names, string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			int v = Math.Clamp(value, 0, names.Length - 1);
			bool changed = ImGui.Combo("##v", ref v, names, names.Length);
			ImGui.PopID();
			if (changed && v != value)
			{
				PushUndo();
				value = v;
				Changed();
				return true;
			}

			return false;
		}

		private bool TextField(string label, ref string value, string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			string v = value ?? "";
			bool changed = ImGui.InputText("##v", ref v, 256);
			Track(changed);
			ImGui.PopID();
			if (changed)
			{
				value = v;
			}

			return changed;
		}

		private bool ColorField(string label, ref Color4 value)
		{
			Label(label);
			ImGui.PushID(label);
			var v = new NVector4(value.R, value.G, value.B, value.A);
			bool changed = ImGui.ColorEdit4("##v", ref v, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs);
			Track(changed);
			ImGui.PopID();
			if (changed)
			{
				value = new Color4(v.X, v.Y, v.Z, 1f);
			}

			return changed;
		}

		/// <summary>A combo of the parameters the event sees (local then global); null selects none.</summary>
		private bool ParameterField(string label, ref string parameterId, AudioEvent context, bool allowNone, string tooltip = null)
		{
			Label(label, tooltip);
			ImGui.PushID(label);
			AudioParameter current = Project.FindParameter(context, parameterId);
			bool changed = false;
			if (ImGui.BeginCombo("##v", current != null ? current.Name : allowNone ? "(none)" : "(choose)"))
			{
				if (allowNone && ImGui.Selectable("(none)", current == null))
				{
					string previous = parameterId;
					PushUndo();
					parameterId = null;
					Changed();
					changed = previous != null;
				}

				foreach (AudioParameter parameter in Project.ParametersOf(context))
				{
					bool global = Project.FindGlobalParameter(parameter.Id) != null;
					if (ImGui.Selectable(parameter.Name + (global ? "   (global)" : "") + "##p" + parameter.Id, parameter == current) && parameter != current)
					{
						PushUndo();
						parameterId = parameter.Id;
						Changed();
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			ImGui.PopID();
			return changed;
		}

		/// <summary>A searchable combo of the project's audio files, also a drop target for the Assets panel.</summary>
		private bool ClipField(string label, AudioClipReference clip)
		{
			Label(label, "The sound file. Drag one from the Assets panel, or pick it.");
			ImGui.PushID(label);
			bool changed = false;
			string shown = clip == null || clip.IsEmpty ? "(none)" : clip.DisplayName;
			AudioClip loaded = clip != null && !clip.IsEmpty ? AudioClip.Load(clip) : null;
			if (clip != null && !clip.IsEmpty && (loaded == null || loaded.Error != null))
			{
				shown += "  (missing)";
			}

			if (ImGui.BeginCombo("##clip", shown))
			{
				ImGui.SetNextItemWidth(-1);
				if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
				ImGui.InputTextWithHint("##clipsearch", "Search", ref clipSearch, 128);
				if (ImGui.Selectable("(none)", clip == null || clip.IsEmpty))
				{
					PushUndo();
					clip.Guid = null;
					clip.Path = null;
					Changed();
					changed = true;
				}

				foreach (string path in AudioAssets())
				{
					if (!string.IsNullOrWhiteSpace(clipSearch) && path.IndexOf(clipSearch, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					bool selected = clip != null && string.Equals(clip.Path, path, StringComparison.OrdinalIgnoreCase);
					if (ImGui.Selectable(path + "##c" + path, selected) && !selected)
					{
						PushUndo();
						AudioClipReference picked = AudioClipReference.FromPath(path);
						clip.Guid = picked.Guid;
						clip.Path = picked.Path;
						Changed();
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ClipPayload).IsNull && draggedClipPath != null)
				{
					AudioClipReference picked = AudioClipReference.FromPath(draggedClipPath);
					draggedClipPath = null;
					PushUndo();
					clip.Guid = picked.Guid;
					clip.Path = picked.Path;
					Changed();
					changed = true;
				}

				ImGui.EndDragDropTarget();
			}

			if (loaded != null && loaded.Error == null)
			{
				Label("");
				ImGui.TextDisabled(FormatTime(loaded.Duration) + ", " + (loaded.Channels == 1 ? "mono" : "stereo") + ", " + loaded.SampleRate + " Hz, " + Nicify(loaded.Importer.loadType.ToString()));
			}

			ImGui.PopID();
			return changed;
		}

		private bool EventField(string label, ref AudioEventReference reference)
		{
			Label(label, "The event this instrument starts an instance of.");
			ImGui.PushID(label);
			bool changed = false;
			AudioEvent current = Project.FindEvent(reference);
			if (ImGui.BeginCombo("##ev", current != null ? current.Path : reference.IsNull ? "(none)" : reference.Path + " (missing)"))
			{
				foreach (AudioEvent audioEvent in Project.Events)
				{
					if (audioEvent == SelectedEvent)
					{
						continue;
					}

					if (ImGui.Selectable(audioEvent.Path + "##e" + audioEvent.Id, audioEvent == current) && audioEvent != current)
					{
						PushUndo();
						reference = AudioEventReference.FromEvent(audioEvent);
						Changed();
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			ImGui.PopID();
			return changed;
		}

		private static string Nicify(string name)
		{
			return SerializationUtility.NicifyName(name);
		}

		// ---------------------------------------------------------------- deck

		private void DrawDeck()
		{
			if (ImGui.Begin("Deck", ref showDeck))
			{
				AudioEvent current = SelectedEvent;
				if (Project == null)
				{
					ImGui.TextDisabled("No audio project open.");
				}
				else
				{
					switch (selection.Kind)
					{
						case SelectionKind.Instrument:
						{
							AudioInstrument instrument = current?.FindInstrument(selection.Id, out _);
							if (instrument != null) DrawInstrumentDeck(current, instrument);
							else ImGui.TextDisabled("Nothing selected.");
							break;
						}
						case SelectionKind.Track:
						{
							AudioTrack track = current?.FindTrack(selection.Id);
							if (track != null) DrawTrackDeck(current, track, false);
							else ImGui.TextDisabled("Nothing selected.");
							break;
						}
						case SelectionKind.Master:
							if (current != null) DrawTrackDeck(current, current.Master, true);
							break;
						case SelectionKind.Marker:
						{
							AudioMarker marker = current?.FindMarker(selection.Id);
							if (marker != null) DrawMarkerDeck(current, marker);
							else ImGui.TextDisabled("Nothing selected.");
							break;
						}
						case SelectionKind.Bus:
						{
							AudioBus bus = Project.FindBus(selection.Id);
							if (bus != null) DrawBusDeck(bus);
							break;
						}
						case SelectionKind.GlobalParameter:
						case SelectionKind.LocalParameter:
						{
							AudioParameter parameter = Project.FindParameter(current, selection.Id);
							if (parameter != null) DrawParameterDeck(parameter, Project.FindGlobalParameter(selection.Id) != null);
							break;
						}
						case SelectionKind.Bank:
						{
							AudioBank bank = Project.FindBank(selection.Id);
							if (bank != null) DrawBankDeck(bank);
							break;
						}
						case SelectionKind.Asset:
							DrawAssetDeck(selection.Id);
							break;
						case SelectionKind.Snapshot:
						{
							AudioSnapshot snapshot = Project.FindSnapshot(selection.Id);
							if (snapshot != null) DrawSnapshotDeck(snapshot);
							else ImGui.TextDisabled("Nothing selected.");
							break;
						}
						case SelectionKind.Event:
							if (current != null) DrawEventDeck(current);
							else ImGui.TextDisabled("Nothing selected.");
							break;
						default:
							if (current != null) DrawEventDeck(current);
							else ImGui.TextDisabled("Select something: an instrument, a track, a marker, a bus...");
							break;
					}
				}
			}

			ImGui.End();
		}

		private void DeckHeader(string kind, string name)
		{
			ImGui.TextDisabled(kind);
			ImGui.SameLine();
			ImGui.TextUnformatted(name ?? "");
			ImGui.Separator();
		}

		private void DrawEventDeck(AudioEvent current)
		{
			DeckHeader("Event", current.Path);
			if (ImGui.BeginTable("##eventdeck", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				ImGui.PushID("left");
				ImGui.SeparatorText("Playback");
				bool persistent = current.Persistent;
				if (BoolField("Persistent", ref persistent, "Keeps playing after the timeline passed its content (the game stops it).")) current.Persistent = persistent;
				int maxInstances = current.MaxInstances;
				if (IntField("Max Instances", ref maxInstances, 0, 512, "Instances playing at once at most; 0 = unlimited.")) current.MaxInstances = maxInstances;
				int stealing = (int)current.Stealing;
				if (ComboField("Stealing", ref stealing, StealingNames, "What happens to the extra instance.")) current.Stealing = (StealingMode)stealing;
				int priority = current.Priority;
				if (IntField("Priority", ref priority, 0, 256, "0 (most important) .. 256: which voices stay real when there are too many.")) current.Priority = priority;
				ImGui.SeparatorText("Fades");
				DrawEnvelopeFields(current.Envelope, "Fade in when the instance starts, fade out when it stops with fadeout.");
				ImGui.PopID();

				ImGui.TableSetColumnIndex(1);
				ImGui.PushID("right");
				ImGui.SeparatorText("Spatializer");
				AudioSpatializer spatial = current.Spatializer;
				bool enabled = spatial.Enabled;
				if (BoolField("3D", ref enabled, "The instance is placed in the world: distance attenuation and panning from the listener.")) spatial.Enabled = enabled;
				ImGui.BeginDisabled(!spatial.Enabled);
				float minDistance = spatial.MinDistance;
				if (FloatField("Min Distance", ref minDistance, 0.05f, 0.01f, 10000f, "%.2f", "Full volume up to here.")) spatial.MinDistance = Math.Max(0.01f, minDistance);
				float maxDistance = spatial.MaxDistance;
				if (FloatField("Max Distance", ref maxDistance, 0.1f, 0.02f, 100000f, "%.1f", "Silent from here on.")) spatial.MaxDistance = Math.Max(spatial.MinDistance + 0.01f, maxDistance);
				int rolloff = (int)spatial.Rolloff;
				if (ComboField("Rolloff", ref rolloff, RolloffNames)) spatial.Rolloff = (RolloffMode)rolloff;
				KnobRow();
				float spread = spatial.Spread;
				if (DeckKnob("Spread", ref spread, 0f, 360f, 0f, spread.ToString("0") + " deg", StudioUi.KnobScale.Linear, false, "Angular size: 0 a point, 360 everywhere.")) spatial.Spread = spread;
				float doppler = spatial.DopplerScale;
				if (DeckKnob("Doppler", ref doppler, 0f, 5f, 0f, doppler <= 0f ? "off" : (doppler * 100f).ToString("0") + " %", StudioUi.KnobScale.Quadratic, false, "Pitch shift from the instance's and the listener's velocities: 0 off, 100 % physical (Project Settings > Audio sets the speed of sound).")) spatial.DopplerScale = doppler;
				if (spatial.Rolloff == RolloffMode.Custom)
				{
					DrawCurvePoints(spatial.CustomCurve, "normalized distance", "dB", 0f, 1f, AudioMath.SilenceDb, 0f);
				}

				ImGui.EndDisabled();
				ImGui.SeparatorText("Notes");
				string notes = current.Notes ?? "";
				bool notesChanged = ImGui.InputTextMultiline("##notes", ref notes, 4096, new NVector2(-1, 60));
				Track(notesChanged);
				if (notesChanged) current.Notes = notes;
				ImGui.PopID();
				ImGui.EndTable();
			}
		}

		private void DrawTrackDeck(AudioEvent current, AudioTrack track, bool isMaster)
		{
			DeckHeader(isMaster ? "Master track" : "Track", isMaster ? current.Path : track.Name);
			if (ImGui.BeginTable("##trackdeck", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				ImGui.PushID("left");
				if (!isMaster)
				{
					string name = track.Name;
					if (TextField("Name", ref name)) track.Name = name;
					Color4 color = track.Color;
					if (ColorField("Color", ref color)) track.Color = color;
				}

				KnobRow();
				float volume = track.VolumeDb;
				if (DeckKnob("Volume", ref volume, AudioMath.SilenceDb, AudioMath.MaxDb, 0f, StudioUi.FormatDb(volume), StudioUi.KnobScale.Volume)) track.VolumeDb = volume;
				float pan = track.Pan;
				if (DeckKnob("Pan", ref pan, -1f, 1f, 0f, StudioUi.FormatPan(pan), StudioUi.KnobScale.Linear, true, "Stereo balance, before spatialization.")) track.Pan = pan;
				float pitch = track.PitchSemitones;
				if (DeckKnob("Pitch", ref pitch, -24f, 24f, 0f, StudioUi.FormatSemitones(pitch), StudioUi.KnobScale.Linear, true, "Semitones, on top of the instruments' pitch.")) track.PitchSemitones = pitch;
				bool mute = track.Mute;
				if (BoolField("Mute", ref mute)) track.Mute = mute;
				if (!isMaster)
				{
					bool solo = track.Solo;
					if (BoolField("Solo", ref solo, "Hear only the soloed tracks while auditioning (game builds play every track).")) track.Solo = solo;
				}
				if (isMaster)
				{
					ImGui.SeparatorText("Routing");
					Label("Output Bus", "The mixer bus the event feeds.");
					AudioBus bus = Project.FindBus(current.BusId);
					if (ImGui.BeginCombo("##bus", bus != null ? Project.BusPath(bus) : "Master"))
					{
						foreach (AudioBus candidate in Project.BusesInTreeOrder())
						{
							if (ImGui.Selectable(Project.BusPath(candidate) + "##b" + candidate.Id, candidate == bus) && candidate != bus)
							{
								string id = candidate.Id;
								Edit("Route event", () => current.BusId = id);
							}
						}

						ImGui.EndCombo();
					}

					Label("Bank", "The bank the event ships in.");
					AudioBank bank = Project.FindBank(current.BankId);
					if (ImGui.BeginCombo("##bank", bank != null ? bank.Name : "Master"))
					{
						foreach (AudioBank candidate in Project.Banks)
						{
							if (ImGui.Selectable(candidate.Name + "##k" + candidate.Id, candidate == bank) && candidate != bank)
							{
								string id = candidate.Id;
								Edit("Move event to bank", () => current.BankId = id);
							}
						}

						ImGui.EndCombo();
					}
				}

				ImGui.SeparatorText("Effects");
				DrawEffectsList(track.Effects, "track");
				ImGui.PopID();

				ImGui.TableSetColumnIndex(1);
				ImGui.PushID("right");
				ImGui.SeparatorText("Automation");
				DrawAutomation(current, track);
				ImGui.PopID();
				ImGui.EndTable();
			}
		}

		private void DrawInstrumentDeck(AudioEvent current, AudioInstrument instrument)
		{
			DeckHeader(instrument.Kind + " instrument", instrument.DisplayName);
			if (ImGui.BeginTable("##instrdeck", 3, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				ImGui.PushID("sound");
				switch (instrument)
				{
					case SingleInstrument single:
						single.Clip ??= new AudioClipReference();
						ClipField("Sound", single.Clip);
						break;
					case MultiInstrument multi:
					{
						int mode = (int)multi.Mode;
						if (ComboField("Playlist Mode", ref mode, PlaylistModeNames, "How the next sound is picked per trigger.")) multi.Mode = (PlaylistMode)mode;
						DrawPlaylist(multi.Playlist);
						break;
					}
					case ScattererInstrument scatterer:
					{
						int mode = (int)scatterer.Mode;
						if (ComboField("Playlist Mode", ref mode, PlaylistModeNames)) scatterer.Mode = (PlaylistMode)mode;
						double spawnMin = scatterer.SpawnIntervalMin;
						if (DoubleField("Interval Min", ref spawnMin, 0.01f, 0.01, 600.0, "%.2f s", "Seconds between spawns, at least.")) scatterer.SpawnIntervalMin = spawnMin;
						double spawnMax = scatterer.SpawnIntervalMax;
						if (DoubleField("Interval Max", ref spawnMax, 0.01f, 0.01, 600.0, "%.2f s")) scatterer.SpawnIntervalMax = spawnMax;
						int total = scatterer.SpawnTotal;
						if (IntField("Spawn Total", ref total, 0, 100000, "Spawns per trigger; 0 = until untriggered.")) scatterer.SpawnTotal = total;
						int polyphony = scatterer.Polyphony;
						if (IntField("Polyphony", ref polyphony, 1, 64, "Spawns playing at once at most.")) scatterer.Polyphony = polyphony;
						float scatterMin = scatterer.MinScatterDistance;
						if (FloatField("Scatter Min", ref scatterMin, 0.1f, 0f, 1000f, "%.1f", "World units from the event (3D events).")) scatterer.MinScatterDistance = scatterMin;
						float scatterMax = scatterer.MaxScatterDistance;
						if (FloatField("Scatter Max", ref scatterMax, 0.1f, 0f, 1000f, "%.1f")) scatterer.MaxScatterDistance = scatterMax;
						DrawPlaylist(scatterer.Playlist);
						break;
					}
					case EventInstrument nested:
					{
						AudioEventReference reference = nested.Event;
						if (EventField("Event", ref reference)) nested.Event = reference;
						break;
					}
					case SnapshotInstrument snapshotInstrument:
					{
						Label("Snapshot", "The mixer snapshot switched on while the region is triggered.");
						AudioSnapshot chosen = Project.FindSnapshot(snapshotInstrument.SnapshotId);
						if (ImGui.BeginCombo("##snap", chosen != null ? chosen.Name : "(choose)"))
						{
							foreach (AudioSnapshot candidate in Project.Snapshots)
							{
								if (ImGui.Selectable(candidate.Name + "##s" + candidate.Id, candidate == chosen) && candidate != chosen)
								{
									string id = candidate.Id;
									Edit("Set snapshot", () => snapshotInstrument.SnapshotId = id);
								}
							}

							ImGui.EndCombo();
						}

						if (Project.Snapshots.Count == 0)
						{
							ImGui.TextDisabled("No snapshots: add one in the Snapshots panel.");
						}

						break;
					}
				}

				ImGui.PopID();

				ImGui.TableSetColumnIndex(1);
				ImGui.PushID("trigger");
				ImGui.SeparatorText("Trigger");
				string name = instrument.Name ?? "";
				if (TextField("Name", ref name, "Shown in the editor; empty: the sound's name.")) instrument.Name = name;
				bool onTimeline = instrument.OnTimeline;
				double start = instrument.Start;
				if (DoubleField(onTimeline ? "Start" : "From", ref start, onTimeline ? 0.01f : 0.005f, onTimeline ? 0.0 : -1e9, 1e9, onTimeline ? "%.3f s" : "%.3f")) instrument.Start = start;
				double length = instrument.Length;
				if (DoubleField(onTimeline ? "Length" : "Range", ref length, onTimeline ? 0.01f : 0.005f, 0.001, 1e9, onTimeline ? "%.3f s" : "%.3f")) instrument.Length = Math.Max(0.001, length);
				bool isAsync = instrument.Async;
				if (BoolField("Async", ref isAsync, "Plays to its end once triggered. Off: timelocked, follows the cursor and stops when it leaves.")) instrument.Async = isAsync;
				bool loop = instrument.Loop;
				if (BoolField("Loop", ref loop, "Loops the sound while the region is triggered.")) instrument.Loop = loop;
				float probability = instrument.Probability;
				if (FloatField("Probability", ref probability, 0.5f, 0f, 100f, "%.0f %%", "Chance of the trigger playing.")) instrument.Probability = probability;
				int quantization = (int)instrument.Quantization;
				if (ComboField("Quantization", ref quantization, QuantizationNames, "The trigger waits for the next beat grid point (needs a tempo marker).")) instrument.Quantization = (QuantizationInterval)quantization;
				double delayMin = instrument.TriggerDelayMin;
				if (DoubleField("Delay Min", ref delayMin, 0.01f, 0.0, 60.0, "%.2f s", "A random wait before the sound starts.")) instrument.TriggerDelayMin = delayMin;
				double delayMax = instrument.TriggerDelayMax;
				if (DoubleField("Delay Max", ref delayMax, 0.01f, 0.0, 60.0, "%.2f s")) instrument.TriggerDelayMax = delayMax;
				ImGui.SeparatorText("Conditions");
				DrawConditions(current, instrument.Conditions);
				ImGui.PopID();

				ImGui.TableSetColumnIndex(2);
				ImGui.PushID("sound2");
				ImGui.SeparatorText("Sound");
				KnobRow();
				float volume = instrument.VolumeDb;
				if (DeckKnob("Volume", ref volume, AudioMath.SilenceDb, AudioMath.MaxDb, 0f, StudioUi.FormatDb(volume), StudioUi.KnobScale.Volume)) instrument.VolumeDb = volume;
				float pitch = instrument.PitchSemitones;
				if (DeckKnob("Pitch", ref pitch, -24f, 24f, 0f, StudioUi.FormatSemitones(pitch), StudioUi.KnobScale.Linear, true)) instrument.PitchSemitones = pitch;
				float volumeRandom = instrument.VolumeRandomDb;
				if (DeckKnob("Vol Rand", ref volumeRandom, 0f, 24f, 0f, "+-" + volumeRandom.ToString("0.0") + " dB", StudioUi.KnobScale.Quadratic, false, "Volume randomization: each trigger picks an offset in this range.")) instrument.VolumeRandomDb = volumeRandom;
				float pitchRandom = instrument.PitchRandomSemitones;
				if (DeckKnob("Pitch Rand", ref pitchRandom, 0f, 12f, 0f, "+-" + pitchRandom.ToString("0.00") + " st", StudioUi.KnobScale.Quadratic, false, "Pitch randomization: each trigger picks an offset in this range.")) instrument.PitchRandomSemitones = pitchRandom;
				double offset = instrument.StartOffset;
				if (DoubleField("Start Offset", ref offset, 0.01f, 0.0, 3600.0, "%.3f s", "Seconds into the sound the playback starts at.")) instrument.StartOffset = offset;
				double offsetRandom = instrument.StartOffsetRandom;
				if (DoubleField("Offset Random", ref offsetRandom, 0.01f, 0.0, 3600.0, "%.3f s")) instrument.StartOffsetRandom = offsetRandom;
				ImGui.SeparatorText("Envelope (AHDSR)");
				DrawEnvelopeFields(instrument.Envelope, null);
				ImGui.PopID();
				ImGui.EndTable();
			}
		}

		private void DrawMarkerDeck(AudioEvent current, AudioMarker marker)
		{
			DeckHeader(marker.Kind, marker.Name);
			string name = marker.Name ?? "";
			if (TextField("Name", ref name)) marker.Name = name;
			double position = marker.Position;
			if (DoubleField("Position", ref position, 0.01f, 0.0, 1e9, "%.3f s")) marker.Position = position;
			switch (marker)
			{
				case TransitionMarker transition:
				{
					if (transition is TransitionRegion region)
					{
						double length = region.RegionLength;
						if (DoubleField("Length", ref length, 0.01f, 0.01, 1e9, "%.3f s")) region.RegionLength = length;
						int quantization = (int)region.Quantization;
						if (ComboField("Quantization", ref quantization, QuantizationNames, "The jump happens at the next grid point while the conditions hold.")) region.Quantization = (QuantizationInterval)quantization;
					}

					Label("Destination", "Where the cursor jumps to.");
					string shown = DestinationName(current, transition.DestinationId);
					if (ImGui.BeginCombo("##dest", shown))
					{
						if (ImGui.Selectable("End (stop the event)", transition.DestinationId == TransitionMarker.EndDestination))
						{
							Edit("Set destination", () => transition.DestinationId = TransitionMarker.EndDestination);
						}

						foreach (AudioMarker candidate in current.Markers)
						{
							if (candidate is DestinationMarker && ImGui.Selectable((candidate.Name ?? "marker") + " (" + FormatTime(candidate.Position) + ")##d" + candidate.Id, candidate.Id == transition.DestinationId))
							{
								string id = candidate.Id;
								Edit("Set destination", () => transition.DestinationId = id);
							}
						}

						ImGui.EndCombo();
					}

					float probability = transition.Probability;
					if (FloatField("Probability", ref probability, 0.5f, 0f, 100f, "%.0f %%")) transition.Probability = probability;
					ImGui.SeparatorText("Transition Timeline");
					if (transition.Timeline == null)
					{
						double crossfade = transition.Crossfade;
						if (DoubleField("Crossfade", ref crossfade, 0.01f, 0.0, 30.0, "%.2f s", "Without a transition timeline: the sounds before the jump fade out over this time while the new ones fade in (0 = cut).")) transition.Crossfade = crossfade;
						if (ImGui.Button("Add Transition Timeline", new NVector2(-1f, 0f)))
						{
							OpenTransitionTimeline(transition);
						}

						if (ImGui.IsItemHovered())
						{
							ImGui.SetTooltip("A timeline of its own between the source and the destination, edited in the timeline: extend the source's sounds, start the destination's early, drag the fades and add fills.");
						}
					}
					else
					{
						TransitionTimeline timeline = transition.Timeline;
						double length = timeline.Length;
						if (DoubleField("Length", ref length, 0.01f, 0.05, 600.0, "%.3f s", "How long the transition timeline runs before playback continues on the destination.")) { timeline.Length = length; timeline.Clamp(); }
						double sourceLength = timeline.SourceLength;
						if (DoubleField("Source Length", ref sourceLength, 0.01f, 0.0, timeline.Length, "%.3f s", "How long the sounds playing at the transition keep playing.")) { timeline.SourceLength = sourceLength; timeline.Clamp(); }
						double destinationStart = timeline.DestinationStart;
						if (DoubleField("Destination Start", ref destinationStart, 0.01f, 0.0, timeline.Length, "%.3f s", "When the destination's sounds begin.")) { timeline.DestinationStart = destinationStart; timeline.Clamp(); }
						double sourceFade = timeline.SourceFadeOut.Length;
						if (DoubleField("Source Fade Out", ref sourceFade, 0.01f, 0.0, timeline.SourceLength, "%.3f s")) { timeline.SourceFadeOut.Length = sourceFade; timeline.Clamp(); }
						float sourceShape = timeline.SourceFadeOut.Shape;
						if (FloatField("Source Fade Shape", ref sourceShape, 0.01f, -1f, 1f, "%.2f", "0 linear, negative stays loud longer, positive drops early.")) timeline.SourceFadeOut.Shape = sourceShape;
						double destinationFade = timeline.DestinationFadeIn.Length;
						if (DoubleField("Destination Fade In", ref destinationFade, 0.01f, 0.0, timeline.Length - timeline.DestinationStart, "%.3f s")) { timeline.DestinationFadeIn.Length = destinationFade; timeline.Clamp(); }
						float destinationShape = timeline.DestinationFadeIn.Shape;
						if (FloatField("Destination Fade Shape", ref destinationShape, 0.01f, -1f, 1f, "%.2f", "0 linear, negative comes up late, positive comes up early.")) timeline.DestinationFadeIn.Shape = destinationShape;
						bool expanded = expandedTransitionId == transition.Id;
						if (ImGui.Button(expanded ? "Close Transition Timeline" : "Open Transition Timeline", new NVector2(-1f, 0f)))
						{
							if (expanded) CloseTransitionTimeline();
							else OpenTransitionTimeline(transition);
						}

						if (ImGui.Button("Remove Transition Timeline", new NVector2(-1f, 0f)))
						{
							RemoveTransitionTimeline(transition);
						}
					}

					ImGui.SeparatorText("Conditions");
					DrawConditions(current, transition.Conditions);
					break;
				}
				case LoopRegion loop:
				{
					double length = loop.RegionLength;
					if (DoubleField("Length", ref length, 0.01f, 0.01, 1e9, "%.3f s")) loop.RegionLength = length;
					int maxLoops = loop.MaxLoops;
					if (IntField("Max Loops", ref maxLoops, 0, 10000, "Passes per entry; 0 = while the conditions hold.")) loop.MaxLoops = maxLoops;
					ImGui.SeparatorText("Conditions (loop while)");
					DrawConditions(current, loop.Conditions);
					break;
				}
				case TempoMarker tempo:
				{
					float bpm = tempo.Bpm;
					if (FloatField("Tempo", ref bpm, 0.5f, 20f, 400f, "%.1f bpm")) tempo.Bpm = bpm;
					int beats = tempo.BeatsPerBar;
					if (IntField("Beats Per Bar", ref beats, 1, 32)) tempo.BeatsPerBar = beats;
					int unit = tempo.BeatUnit;
					if (IntField("Beat Unit", ref unit, 1, 32, "Note value of a beat (4 = quarter notes).")) tempo.BeatUnit = unit;
					ImGui.TextDisabled($"Beat {tempo.SecondsPerBeat:0.000} s, bar {tempo.SecondsPerBeat * tempo.BeatsPerBar:0.000} s");
					break;
				}
				case SustainPoint _:
					ImGui.TextDisabled("The cursor stops here (sounds keep playing) until the game calls KeyOff on the instance.");
					break;
				case DestinationMarker _:
					ImGui.TextDisabled("Transitions jump here; the game gets a MarkerReached callback with the name.");
					break;
			}
		}

		private void DrawBusDeck(AudioBus bus)
		{
			DeckHeader("Bus", Project.BusPath(bus));
			bool isMaster = bus.Id == AudioProject.MasterBusId;
			if (!isMaster)
			{
				string name = bus.Name;
				if (TextField("Name", ref name)) bus.Name = name;
				Label("Parent");
				AudioBus parent = Project.FindBus(bus.ParentId);
				if (ImGui.BeginCombo("##parent", parent != null ? Project.BusPath(parent) : "Master"))
				{
					foreach (AudioBus candidate in Project.BusesInTreeOrder())
					{
						if (candidate == bus || IsDescendant(candidate, bus))
						{
							continue;
						}

						if (ImGui.Selectable(Project.BusPath(candidate) + "##p" + candidate.Id, candidate == parent) && candidate != parent)
						{
							string id = candidate.Id;
							Edit("Reparent bus", () => bus.ParentId = id);
						}
					}

					ImGui.EndCombo();
				}
			}

			float volume = bus.VolumeDb;
			if (DbField("Volume", ref volume)) bus.VolumeDb = volume;
			bool mute = bus.Mute;
			if (BoolField("Mute", ref mute)) bus.Mute = mute;
			ImGui.SeparatorText("Effects");
			DrawEffectsList(bus.Effects, "bus");
		}

		private bool IsDescendant(AudioBus candidate, AudioBus ancestor)
		{
			int guard = 0;
			for (AudioBus parent = Project.FindBus(candidate.ParentId); parent != null && guard++ < 64; parent = Project.FindBus(parent.ParentId))
			{
				if (parent == ancestor)
				{
					return true;
				}
			}

			return false;
		}

		private void DrawParameterDeck(AudioParameter parameter, bool global)
		{
			DeckHeader(global ? "Global parameter" : "Parameter", parameter.Name);
			string name = parameter.Name;
			if (TextField("Name", ref name, "Scripts set it by this name.")) parameter.Name = name;
			int type = (int)parameter.Type;
			if (ComboField("Type", ref type, ParameterTypeNames)) parameter.Type = (AudioParameterType)type;
			float min = parameter.Min;
			if (FloatField("Minimum", ref min, 0.05f, -1e9f, 1e9f, "%.3f")) parameter.Min = min;
			float max = parameter.Max;
			if (FloatField("Maximum", ref max, 0.05f, -1e9f, 1e9f, "%.3f")) parameter.Max = max;
			float value = parameter.Default;
			if (FloatField("Default", ref value, 0.05f, Math.Min(parameter.Min, parameter.Max), Math.Max(parameter.Min, parameter.Max), "%.3f")) parameter.Default = value;
			float seek = parameter.SeekSpeed;
			if (FloatField("Seek Speed", ref seek, 0.05f, 0f, 1e6f, "%.2f /s", "Units per second the value moves towards what the game set; 0 = jumps.")) parameter.SeekSpeed = seek;
			if (!global)
			{
				int builtIn = (int)parameter.BuiltIn;
				if (ComboField("Built-in", ref builtIn, BuiltInNames, "Driven by the engine from the instance's 3D attributes.")) parameter.BuiltIn = (BuiltInParameter)builtIn;
			}

			if (parameter.Type == AudioParameterType.Labeled)
			{
				ImGui.SeparatorText("Labels");
				int count = (int)Math.Round(Math.Abs(parameter.Max - parameter.Min)) + 1;
				while (parameter.Labels.Count < count) parameter.Labels.Add("");
				for (int i = 0; i < count && i < 64; i++)
				{
					string label = parameter.Labels[i];
					ImGui.PushID(i);
					if (TextField((parameter.Min + i).ToString("0"), ref label)) parameter.Labels[i] = label;
					ImGui.PopID();
				}
			}
		}

		private void DrawBankDeck(AudioBank bank)
		{
			DeckHeader("Bank", bank.Name);
			bool isMaster = bank.Id == AudioProject.MasterBankId;
			if (!isMaster)
			{
				string name = bank.Name;
				if (TextField("Name", ref name, "The file name of the bank (Name.bank) and what the game loads it by.")) bank.Name = name;
				bool autoLoad = bank.AutoLoad;
				if (BoolField("Load At Start", ref autoLoad, "The player loads it when the game starts (the master bank always does).")) bank.AutoLoad = autoLoad;
			}
			else
			{
				ImGui.TextDisabled("The master bank always loads first; it also carries the buses and global parameters.");
			}

			ImGui.SeparatorText("Events");
			foreach (AudioEvent audioEvent in Project.EventsOfBank(bank.Id))
			{
				if (ImGui.Selectable(audioEvent.Path + "##be" + audioEvent.Id, selectedEventId == audioEvent.Id))
				{
					SelectEvent(audioEvent.Id);
					selection = Selection.Of(SelectionKind.Bank, bank.Id);
				}
			}

			LoadedBank loaded = AudioSystem.FindBank(bank.Name);
			if (loaded != null)
			{
				ImGui.Spacing();
				ImGui.TextDisabled($"Loaded in the editor: {loaded.Events.Count} events, {loaded.SamplesByPath.Count} sounds, {loaded.MemoryBytes / 1024.0 / 1024.0:0.0} MB in memory, {loaded.StreamingCount} streamed.");
			}
		}

		/// <summary>A snapshot: its mode and fades, the buses it scopes with their values, and a live preview.</summary>
		private void DrawSnapshotDeck(AudioSnapshot snapshot)
		{
			DeckHeader("Snapshot", snapshot.Name);
			if (ImGui.BeginTable("##snapdeck", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				ImGui.PushID("left");
				string name = snapshot.Name;
				if (TextField("Name", ref name, "Scripts start it by this name.")) snapshot.Name = name;
				int mode = (int)snapshot.Mode;
				string[] modes = { "Overriding (replaces the values)", "Blending (volumes add, in dB)" };
				if (ComboField("Mode", ref mode, modes)) snapshot.Mode = (SnapshotMode)mode;
				int index = Project.Snapshots.IndexOf(snapshot);
				Label("Priority", "Position in the Snapshots list: the first applies on top of the others.");
				ImGui.TextUnformatted((index + 1) + " of " + Project.Snapshots.Count);
				ImGui.SeparatorText("Fades (intensity envelope)");
				float attack = snapshot.Intensity.Attack;
				if (FloatField("Fade In", ref attack, 0.01f, 0f, 60f, "%.2f s", "Seconds the snapshot takes to come up when started.")) snapshot.Intensity.Attack = attack;
				float release = snapshot.Intensity.Release;
				if (FloatField("Fade Out", ref release, 0.01f, 0f, 60f, "%.2f s", "Seconds it takes to go away when stopped.")) snapshot.Intensity.Release = release;
				ImGui.SeparatorText("Preview");
				bool active = previewSnapshot != null && previewSnapshot.IsActive && previewSnapshot.Definition.Id == snapshot.Id;
				float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
				if (active) ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
				if (ImGui.Button("Start", new NVector2(width, 0f))) StartPreviewSnapshot(snapshot);
				if (active) ImGui.PopStyleColor();
				ImGui.SameLine();
				if (ImGui.Button("Stop", new NVector2(width, 0f))) StopPreviewSnapshot();
				Label("Intensity");
				float intensity = previewSnapshotIntensity;
				if (ImGui.SliderFloat("##intensity", ref intensity, 0f, 1f, "%.2f"))
				{
					previewSnapshotIntensity = intensity;
					if (active) previewSnapshot.SetIntensity(intensity);
				}

				ImGui.TextDisabled(active ? "Active: hear it on the events playing now." : "Play an event, then start the snapshot to hear it.");
				ImGui.SeparatorText("Notes");
				string notes = snapshot.Notes ?? "";
				bool notesChanged = ImGui.InputTextMultiline("##snotes", ref notes, 4096, new NVector2(-1, 50));
				Track(notesChanged);
				if (notesChanged) snapshot.Notes = notes;
				ImGui.PopID();

				ImGui.TableSetColumnIndex(1);
				ImGui.PushID("right");
				ImGui.SeparatorText("Scoped buses");
				ImGui.TextDisabled("Tick what the snapshot sets (also from the Mixer while the snapshot is selected).");
				foreach (AudioBus bus in Project.BusesInTreeOrder())
				{
					ImGui.PushID(bus.Id);
					SnapshotBusValue value = snapshot.FindBus(bus.Id);
					bool scopedVolume = value != null && value.SetVolume;
					bool open = ImGui.CollapsingHeader(Project.BusPath(bus) + (value != null ? "  *" : "") + "###bus", ImGuiTreeNodeFlags.DefaultOpen);
					if (open)
					{
						Label("Volume");
						bool tick = scopedVolume;
						if (ImGui.Checkbox("##scopevol", ref tick) && tick != scopedVolume)
						{
							SetSnapshotVolume(snapshot, bus, tick, value?.VolumeDb ?? bus.VolumeDb);
							value = snapshot.FindBus(bus.Id);
						}

						ImGui.SameLine();
						ImGui.SetNextItemWidth(-1);
						ImGui.BeginDisabled(value == null || !value.SetVolume);
						float volume = value != null && value.SetVolume ? value.VolumeDb : bus.VolumeDb;
						bool changed = ImGui.SliderFloat("##snapvol", ref volume, AudioMath.SilenceDb, AudioMath.MaxDb, volume <= AudioMath.SilenceDb ? "-inf dB" : snapshot.Mode == SnapshotMode.Blending ? "%+.1f dB" : "%.1f dB");
						Track(changed);
						if (changed && value != null) value.VolumeDb = volume;
						ImGui.EndDisabled();

						foreach (AudioEffect effect in bus.Effects)
						{
							ImGui.PushID(effect.Id);
							foreach (string parameter in effect.ParameterNames)
							{
								ImGui.PushID(parameter);
								SnapshotEffectValue effectValue = value?.FindEffect(effect.Id, parameter);
								Label(effect.DisplayName + " / " + parameter);
								bool scoped = effectValue != null;
								if (ImGui.Checkbox("##scopefx", ref scoped) && scoped != (effectValue != null))
								{
									SetSnapshotEffect(snapshot, bus, effect, parameter, scoped, effect.GetParameter(parameter));
									value = snapshot.FindBus(bus.Id);
									effectValue = value?.FindEffect(effect.Id, parameter);
								}

								ImGui.SameLine();
								ImGui.SetNextItemWidth(-1);
								effect.GetParameterRange(parameter, out float min, out float max, out string unit);
								ImGui.BeginDisabled(effectValue == null);
								float current = effectValue?.Value ?? effect.GetParameter(parameter);
								bool fxChanged = ImGui.SliderFloat("##fxv", ref current, min, max, unit == "dB" ? "%.1f dB" : unit == "Hz" ? "%.0f Hz" : unit == "s" ? "%.3f s" : "%.2f", unit == "Hz" ? ImGuiSliderFlags.Logarithmic : ImGuiSliderFlags.None);
								Track(fxChanged);
								if (fxChanged && effectValue != null) effectValue.Value = current;
								ImGui.EndDisabled();
								ImGui.PopID();
							}

							ImGui.PopID();
						}
					}

					ImGui.PopID();
				}

				ImGui.PopID();
				ImGui.EndTable();
			}
		}

		/// <summary>Scopes (or unscopes) a bus volume in a snapshot; empty bus entries are dropped.</summary>
		private void SetSnapshotVolume(AudioSnapshot snapshot, AudioBus bus, bool scoped, float volumeDb)
		{
			Edit(scoped ? "Scope bus volume" : "Unscope bus volume", () =>
			{
				SnapshotBusValue value = snapshot.FindBus(bus.Id);
				if (scoped)
				{
					if (value == null)
					{
						value = new SnapshotBusValue { BusId = bus.Id };
						snapshot.Buses.Add(value);
					}

					value.SetVolume = true;
					value.VolumeDb = snapshot.Mode == SnapshotMode.Blending ? 0f : volumeDb;
				}
				else if (value != null)
				{
					value.SetVolume = false;
					if (value.Effects.Count == 0)
					{
						snapshot.Buses.Remove(value);
					}
				}
			});
		}

		private void SetSnapshotEffect(AudioSnapshot snapshot, AudioBus bus, AudioEffect effect, string parameter, bool scoped, float current)
		{
			Edit(scoped ? "Scope effect value" : "Unscope effect value", () =>
			{
				SnapshotBusValue value = snapshot.FindBus(bus.Id);
				if (scoped)
				{
					if (value == null)
					{
						value = new SnapshotBusValue { BusId = bus.Id, SetVolume = false };
						snapshot.Buses.Add(value);
					}

					if (value.FindEffect(effect.Id, parameter) == null)
					{
						value.Effects.Add(new SnapshotEffectValue { EffectId = effect.Id, Parameter = parameter, Value = current });
					}
				}
				else if (value != null)
				{
					value.Effects.RemoveAll(e => e.EffectId == effect.Id && e.Parameter == parameter);
					if (!value.SetVolume && value.Effects.Count == 0)
					{
						snapshot.Buses.Remove(value);
					}
				}
			});
		}

		private void DrawAssetDeck(string path)
		{
			AudioClip clip = AudioClip.Load(path);
			DeckHeader("Audio file", Path.GetFileName(path));
			ImGui.TextDisabled(path);
			if (clip == null || clip.Error != null)
			{
				ImGui.TextColored(EditorStyle.Error, "Cannot read: " + (clip?.Error ?? "unknown"));
				return;
			}

			ImGui.TextUnformatted($"{Nicify(clip.Format.ToString())}, {FormatTime(clip.Duration)}, {(clip.Channels == 1 ? "mono" : "stereo")}, {clip.SampleRate} Hz");
			if (ImGui.Button("Play")) PreviewAsset(clip);
			ImGui.SameLine();
			if (ImGui.Button("Stop")) StopAssetPreview();
			ImGui.SeparatorText("Import settings");
			AudioImporter importer = clip.Importer;
			int loadType = (int)importer.loadType;
			Label("Load Type", "How the bank holds the sound: decoded PCM (fast load), the encoded file decoded at load (smaller), or streamed from disk while playing.");
			if (ImGui.Combo("##loadtype", ref loadType, LoadTypeNames, LoadTypeNames.Length) && loadType != (int)importer.loadType)
			{
				SetImporter(clip, i => i.loadType = (AudioLoadType)loadType);
			}

			Label("Force Mono", "Mix stereo files down to one channel (spatialized sounds need no stereo).");
			bool mono = importer.forceMono;
			if (ImGui.Checkbox("##mono", ref mono) && mono != importer.forceMono)
			{
				SetImporter(clip, i => i.forceMono = mono);
			}

			// Waveform.
			ImGui.SeparatorText("Waveform");
			NVector2 size = new NVector2(ImGui.GetContentRegionAvail().X, 70f);
			NVector2 min = ImGui.GetCursorScreenPos();
			ImGui.InvisibleButton("##wave", size);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, min + size, Color(0.1f, 0.1f, 0.11f));
			float[] peaks = clip.GetPeaks(Math.Max(8, (int)size.X / 2));
			if (peaks != null)
			{
				int buckets = peaks.Length / 2;
				float midY = min.Y + size.Y * 0.5f;
				for (int i = 0; i < buckets; i++)
				{
					float x = min.X + i * 2f;
					draw.AddLine(new NVector2(x, midY - peaks[i * 2 + 1] * size.Y * 0.48f), new NVector2(x, midY - peaks[i * 2] * size.Y * 0.48f + 1f), Color(0.4f, 0.7f, 1f));
				}
			}

			ImGui.Spacing();
			ImGui.TextDisabled("Used by " + CountClipUses(path) + " instrument(s).");
		}

		private int CountClipUses(string path)
		{
			int count = 0;
			foreach (AudioEvent audioEvent in Project.Events)
			{
				foreach (AudioClipReference clip in audioEvent.Clips)
				{
					if (string.Equals(clip.Resolve() ?? clip.Path, path, StringComparison.OrdinalIgnoreCase))
					{
						count++;
					}
				}
			}

			return count;
		}

		// ---------------------------------------------------------------- shared editors

		private void DrawEnvelopeFields(AudioEnvelope envelope, string tooltip)
		{
			KnobRow();
			float attack = envelope.Attack;
			if (DeckKnob("Attack", ref attack, 0f, 30f, 0f, FormatSeconds(attack), StudioUi.KnobScale.Quadratic, false, tooltip)) envelope.Attack = attack;
			float hold = envelope.Hold;
			if (DeckKnob("Hold", ref hold, 0f, 30f, 0f, FormatSeconds(hold), StudioUi.KnobScale.Quadratic)) envelope.Hold = hold;
			float decay = envelope.Decay;
			if (DeckKnob("Decay", ref decay, 0f, 30f, 0f, FormatSeconds(decay), StudioUi.KnobScale.Quadratic)) envelope.Decay = decay;
			float sustain = envelope.SustainDb;
			if (DeckKnob("Sustain", ref sustain, AudioMath.SilenceDb, 0f, 0f, StudioUi.FormatDb(sustain), StudioUi.KnobScale.Volume)) envelope.SustainDb = sustain;
			float release = envelope.Release;
			if (DeckKnob("Release", ref release, 0f, 30f, 0f, FormatSeconds(release), StudioUi.KnobScale.Quadratic)) envelope.Release = release;
		}

		private void DrawConditions(AudioEvent current, List<AudioCondition> conditions)
		{
			for (int i = 0; i < conditions.Count; i++)
			{
				AudioCondition condition = conditions[i];
				ImGui.PushID(i);
				string parameterId = condition.ParameterId;
				if (ParameterField("Parameter", ref parameterId, current, false)) condition.ParameterId = parameterId;
				AudioParameter parameter = Project.FindParameter(current, condition.ParameterId);
				float lower = parameter != null ? Math.Min(parameter.Min, parameter.Max) : -1e9f;
				float upper = parameter != null ? Math.Max(parameter.Min, parameter.Max) : 1e9f;
				float min = condition.Min;
				if (FloatField("Min", ref min, 0.01f, lower, upper, "%.3f")) condition.Min = min;
				float max = condition.Max;
				if (FloatField("Max", ref max, 0.01f, lower, upper, "%.3f")) condition.Max = max;
				if (ImGui.SmallButton("Remove condition"))
				{
					AudioCondition removed = condition;
					Edit("Remove condition", () => conditions.Remove(removed));
					ImGui.PopID();
					break;
				}

				ImGui.Separator();
				ImGui.PopID();
			}

			if (ImGui.SmallButton("+ Condition"))
			{
				List<AudioParameter> parameters = Project.ParametersOf(current);
				if (parameters.Count == 0)
				{
					SetStatus("Add a parameter to the event first (Overview)");
				}
				else
				{
					AudioParameter first = parameters[0];
					Edit("Add condition", () => conditions.Add(new AudioCondition { ParameterId = first.Id, Min = first.Min, Max = first.Max }));
				}
			}
		}

		private void DrawPlaylist(List<AudioPlaylistEntry> playlist)
		{
			ImGui.SeparatorText("Playlist");
			for (int i = 0; i < playlist.Count; i++)
			{
				AudioPlaylistEntry entry = playlist[i];
				ImGui.PushID(i);
				entry.Clip ??= new AudioClipReference();
				ClipField("Sound " + (i + 1), entry.Clip);
				float weight = entry.Weight;
				if (FloatField("Weight", ref weight, 0.05f, 0f, 100f, "%.2f", "Relative chance in random modes.")) entry.Weight = weight;
				float volume = entry.VolumeDb;
				if (FloatField("Volume", ref volume, 0.1f, AudioMath.SilenceDb, AudioMath.MaxDb, "%.1f dB")) entry.VolumeDb = volume;
				float pitch = entry.PitchSemitones;
				if (FloatField("Pitch", ref pitch, 0.05f, -48f, 48f, "%.2f st")) entry.PitchSemitones = pitch;
				if (ImGui.SmallButton("Remove"))
				{
					AudioPlaylistEntry removed = entry;
					Edit("Remove playlist entry", () => playlist.Remove(removed));
					ImGui.PopID();
					break;
				}

				if (i > 0)
				{
					ImGui.SameLine();
					if (ImGui.SmallButton("Up"))
					{
						int index = i;
						Edit("Reorder playlist", () =>
						{
							AudioPlaylistEntry moved = playlist[index];
							playlist.RemoveAt(index);
							playlist.Insert(index - 1, moved);
						});
					}
				}

				ImGui.Separator();
				ImGui.PopID();
			}

			if (ImGui.SmallButton("+ Sound"))
			{
				Edit("Add playlist entry", () => playlist.Add(new AudioPlaylistEntry()));
			}

			ImGui.SameLine();
			ImGui.TextDisabled("or drop files from Assets");
			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ClipPayload).IsNull && draggedClipPath != null)
				{
					string path = draggedClipPath;
					draggedClipPath = null;
					Edit("Add playlist entry", () => playlist.Add(new AudioPlaylistEntry { Clip = AudioClipReference.FromPath(path) }));
				}

				ImGui.EndDragDropTarget();
			}
		}

		private void DrawEffectsList(List<AudioEffect> effects, string owner)
		{
			for (int i = 0; i < effects.Count; i++)
			{
				AudioEffect effect = effects[i];
				ImGui.PushID(effect.Id ?? i.ToString());
				bool open = ImGui.CollapsingHeader(effect.DisplayName + (effect.Bypass ? " (bypassed)" : "") + "###fx", ImGuiTreeNodeFlags.DefaultOpen);
				if (ImGui.BeginPopupContextItem("##fx_menu"))
				{
					bool bypass = effect.Bypass;
					if (ImGui.MenuItem("Bypass", "", bypass)) Edit("Toggle bypass", () => effect.Bypass = !bypass);
					if (i > 0 && ImGui.MenuItem("Move Up"))
					{
						int index = i;
						Edit("Reorder effects", () =>
						{
							AudioEffect moved = effects[index];
							effects.RemoveAt(index);
							effects.Insert(index - 1, moved);
						});
					}

					if (ImGui.MenuItem("Remove"))
					{
						AudioEffect removed = effect;
						Edit("Remove effect", () => effects.Remove(removed));
					}

					ImGui.EndPopup();
				}

				if (open)
				{
					// The effect's parameters as a row of knobs.
					KnobRow();
					ImGui.BeginDisabled(effect.Bypass);
					foreach (string parameter in effect.ParameterNames)
					{
						effect.GetParameterRange(parameter, out float min, out float max, out string unit);
						float value = effect.GetParameter(parameter);
						float reset = EffectDefault(effect, parameter);
						StudioUi.KnobScale scale = unit == "Hz" && min > 0f ? StudioUi.KnobScale.Logarithmic : unit == "s" ? StudioUi.KnobScale.Quadratic : StudioUi.KnobScale.Linear;
						string text = unit == "dB" ? StudioUi.FormatDb(value) : unit == "Hz" ? (value >= 1000f ? (value / 1000f).ToString("0.0") + " kHz" : value.ToString("0") + " Hz") : unit == "s" ? FormatSeconds(value) : unit == "%" ? value.ToString("0") + " %" : value.ToString("0.00");
						if (DeckKnob(parameter, ref value, min, max, reset, text, scale))
						{
							effect.SetParameter(parameter, value);
						}
					}

					ImGui.EndDisabled();
				}

				ImGui.PopID();
			}

			if (ImGui.SmallButton("+ Effect"))
			{
				ImGui.OpenPopup("##add_effect");
			}

			if (ImGui.BeginPopup("##add_effect"))
			{
				if (ImGui.MenuItem("Low Pass")) Edit("Add effect", () => effects.Add(new LowPassEffect { Id = AudioProject.NewId(), CutoffHz = 4000f }));
				if (ImGui.MenuItem("High Pass")) Edit("Add effect", () => effects.Add(new HighPassEffect { Id = AudioProject.NewId(), CutoffHz = 200f }));
				if (ImGui.MenuItem("Reverb")) Edit("Add effect", () => effects.Add(new ReverbEffect { Id = AudioProject.NewId() }));
				if (ImGui.MenuItem("Delay")) Edit("Add effect", () => effects.Add(new DelayEffect { Id = AudioProject.NewId() }));
				ImGui.EndPopup();
			}
		}

		/// <summary>Automation curves of a track: pick a target and a parameter, then edit the curve's points.</summary>
		private void DrawAutomation(AudioEvent current, AudioTrack track)
		{
			ImGui.TextDisabled("A property follows a curve over a parameter's value.");
			for (int i = 0; i < track.Automation.Count; i++)
			{
				AudioAutomationCurve curve = track.Automation[i];
				ImGui.PushID(i);
				AudioParameter parameter = Project.FindParameter(current, curve.ParameterId);
				string title = TargetLabel(track, curve.Target) + " by " + (parameter?.Name ?? "(missing parameter)");
				bool open = ImGui.CollapsingHeader(title + "###auto", ImGuiTreeNodeFlags.DefaultOpen);
				if (ImGui.BeginPopupContextItem("##auto_menu"))
				{
					if (ImGui.MenuItem("Remove"))
					{
						AudioAutomationCurve removed = curve;
						Edit("Remove automation", () => track.Automation.Remove(removed));
					}

					ImGui.EndPopup();
				}

				if (open)
				{
					string parameterId = curve.ParameterId;
					if (ParameterField("Parameter", ref parameterId, current, false)) curve.ParameterId = parameterId;
					TargetRange(track, curve.Target, out float yMin, out float yMax, out string unit);
					float xMin = parameter != null ? Math.Min(parameter.Min, parameter.Max) : 0f;
					float xMax = parameter != null ? Math.Max(parameter.Min, parameter.Max) : 1f;
					DrawCurvePoints(curve.Points, parameter?.Name ?? "x", unit, xMin, xMax, yMin, yMax);
				}

				ImGui.PopID();
			}

			if (ImGui.SmallButton("+ Automation"))
			{
				ImGui.OpenPopup("##add_automation");
			}

			if (ImGui.BeginPopup("##add_automation"))
			{
				List<AudioParameter> parameters = Project.ParametersOf(current);
				if (parameters.Count == 0)
				{
					ImGui.TextDisabled("Add a parameter to the event first (Overview).");
				}
				else
				{
					ImGui.TextDisabled("Property");
					foreach (string target in Targets(track))
					{
						if (ImGui.MenuItem(TargetLabel(track, target)))
						{
							AudioParameter parameter = parameters[0];
							TargetRange(track, target, out float yMin, out float yMax, out _);
							float value = TargetValue(track, target);
							var curve = new AudioAutomationCurve { ParameterId = parameter.Id, Target = target };
							curve.Points.Add(new AudioCurvePoint { X = Math.Min(parameter.Min, parameter.Max), Y = Math.Clamp(value, yMin, yMax) });
							curve.Points.Add(new AudioCurvePoint { X = Math.Max(parameter.Min, parameter.Max), Y = Math.Clamp(value, yMin, yMax) });
							Edit("Add automation", () => track.Automation.Add(curve));
						}
					}
				}

				ImGui.EndPopup();
			}
		}

		private static IEnumerable<string> Targets(AudioTrack track)
		{
			yield return AudioAutomationCurve.VolumeTarget;
			yield return AudioAutomationCurve.PitchTarget;
			yield return AudioAutomationCurve.PanTarget;
			foreach (AudioEffect effect in track.Effects)
			{
				foreach (string parameter in effect.ParameterNames)
				{
					yield return AudioAutomationCurve.EffectTarget(effect.Id, parameter);
				}
			}
		}

		private static string TargetLabel(AudioTrack track, string target)
		{
			if (AudioAutomationCurve.ParseEffectTarget(target, out string effectId, out string parameter))
			{
				AudioEffect effect = track.FindEffect(effectId);
				return (effect?.DisplayName ?? "Effect") + " / " + parameter;
			}

			return target;
		}

		private static void TargetRange(AudioTrack track, string target, out float min, out float max, out string unit)
		{
			switch (target)
			{
				case AudioAutomationCurve.VolumeTarget:
					min = AudioMath.SilenceDb;
					max = AudioMath.MaxDb;
					unit = "dB";
					return;
				case AudioAutomationCurve.PitchTarget:
					min = -48f;
					max = 48f;
					unit = "st";
					return;
				case AudioAutomationCurve.PanTarget:
					min = -1f;
					max = 1f;
					unit = "";
					return;
			}

			if (AudioAutomationCurve.ParseEffectTarget(target, out string effectId, out string parameter))
			{
				AudioEffect effect = track.FindEffect(effectId);
				if (effect != null)
				{
					effect.GetParameterRange(parameter, out min, out max, out unit);
					return;
				}
			}

			min = 0f;
			max = 1f;
			unit = "";
		}

		private static float TargetValue(AudioTrack track, string target)
		{
			switch (target)
			{
				case AudioAutomationCurve.VolumeTarget: return track.VolumeDb;
				case AudioAutomationCurve.PitchTarget: return track.PitchSemitones;
				case AudioAutomationCurve.PanTarget: return track.Pan;
			}

			if (AudioAutomationCurve.ParseEffectTarget(target, out string effectId, out string parameter))
			{
				return track.FindEffect(effectId)?.GetParameter(parameter) ?? 0f;
			}

			return 0f;
		}

		/// <summary>A small curve editor: the graph (click adds a point, drag moves one, right click removes) and the points' fields.</summary>
		private void DrawCurvePoints(List<AudioCurvePoint> points, string xLabel, string unit, float xMin, float xMax, float yMin, float yMax)
		{
			var size = new NVector2(Math.Max(120f, ImGui.GetContentRegionAvail().X), 110f);
			NVector2 min = ImGui.GetCursorScreenPos();
			ImGui.InvisibleButton("##curve", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
			bool hovered = ImGui.IsItemHovered();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, min + size, Color(0.1f, 0.1f, 0.11f), 3f);
			draw.AddRect(min, min + size, Color(0.3f, 0.3f, 0.33f), 3f);
			float xRange = Math.Max(1e-6f, xMax - xMin);
			float yRange = Math.Max(1e-6f, yMax - yMin);
			NVector2 Map(float x, float y) => new NVector2(min.X + 6f + (x - xMin) / xRange * (size.X - 12f), min.Y + size.Y - 6f - (y - yMin) / yRange * (size.Y - 12f));

			// The curve.
			if (points.Count > 0)
			{
				NVector2 previous = Map(xMin, AudioAutomationCurve.Evaluate(points, xMin));
				int steps = Math.Max(16, (int)size.X / 3);
				for (int i = 1; i <= steps; i++)
				{
					float x = xMin + xRange * i / steps;
					NVector2 next = Map(x, AudioAutomationCurve.Evaluate(points, x));
					draw.AddLine(previous, next, Color(0.4f, 0.75f, 1f), 2f);
					previous = next;
				}
			}

			for (int i = 0; i < points.Count; i++)
			{
				NVector2 p = Map(points[i].X, points[i].Y);
				bool selected = selectedCurvePoint == i;
				draw.AddCircleFilled(p, selected ? 6f : 4.5f, selected ? Color(1f, 0.85f, 0.3f) : Color(1f, 1f, 1f));
			}

			draw.AddText(min + new NVector2(6f, 4f), ImGui.GetColorU32(ImGuiCol.TextDisabled), yMax.ToString("0.#") + " " + unit);
			draw.AddText(new NVector2(min.X + 6f, min.Y + size.Y - 18f), ImGui.GetColorU32(ImGuiCol.TextDisabled), yMin.ToString("0.#") + " " + unit);
			string xText = xLabel + " " + xMin.ToString("0.##") + " .. " + xMax.ToString("0.##");
			draw.AddText(new NVector2(min.X + size.X - ImGui.CalcTextSize(xText).X - 6f, min.Y + size.Y - 18f), ImGui.GetColorU32(ImGuiCol.TextDisabled), xText);

			// Interaction.
			NVector2 mouse = ImGui.GetMousePos();
			float mx = xMin + Math.Clamp((mouse.X - min.X - 6f) / (size.X - 12f), 0f, 1f) * xRange;
			float my = yMin + Math.Clamp((min.Y + size.Y - 6f - mouse.Y) / (size.Y - 12f), 0f, 1f) * yRange;
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				int nearest = NearestPoint(points, Map, mouse);
				if (nearest >= 0)
				{
					selectedCurvePoint = nearest;
					curveDragSnapshot = Project.ToBytes();
					curveDragging = true;
				}
				else
				{
					var point = new AudioCurvePoint { X = mx, Y = my };
					Edit("Add curve point", () =>
					{
						points.Add(point);
						points.Sort((a, b) => a.X.CompareTo(b.X));
					});
					selectedCurvePoint = points.IndexOf(point);
				}
			}

			if (curveDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left) && selectedCurvePoint >= 0 && selectedCurvePoint < points.Count)
			{
				AudioCurvePoint point = points[selectedCurvePoint];
				if (Math.Abs(point.X - mx) > 1e-6f || Math.Abs(point.Y - my) > 1e-6f)
				{
					point.X = mx;
					point.Y = my;
					curveDragChanged = true;
					Changed();
				}
			}

			if (curveDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				if (curveDragChanged && curveDragSnapshot != null)
				{
					undoStack.Add(curveDragSnapshot);
					Trim(undoStack);
					redoStack.Clear();
					points.Sort((a, b) => a.X.CompareTo(b.X));
				}

				curveDragging = false;
				curveDragChanged = false;
				curveDragSnapshot = null;
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				int nearest = NearestPoint(points, Map, mouse);
				if (nearest >= 0 && points.Count > 1)
				{
					int index = nearest;
					Edit("Remove curve point", () => points.RemoveAt(index));
					selectedCurvePoint = -1;
				}
			}

			if (hovered)
			{
				ImGui.SetTooltip("Click: add a point, drag: move it, right click: remove it.\n" + xLabel + " " + mx.ToString("0.###") + " -> " + my.ToString("0.##") + " " + unit);
			}

			if (selectedCurvePoint >= 0 && selectedCurvePoint < points.Count)
			{
				AudioCurvePoint point = points[selectedCurvePoint];
				ImGui.PushID("point");
				float x = point.X;
				if (FloatField(xLabel, ref x, xRange * 0.002f, xMin, xMax, "%.3f")) point.X = x;
				float y = point.Y;
				if (FloatField("Value", ref y, yRange * 0.002f, yMin, yMax, "%.2f " + unit)) point.Y = y;
				float tension = point.Tension;
				if (FloatField("Shape", ref tension, 0.01f, -1f, 1f, "%.2f", "Bends the segment to the next point: negative eases in, positive eases out.")) point.Tension = tension;
				ImGui.PopID();
			}
		}

		private bool curveDragging;
		private bool curveDragChanged;
		private byte[] curveDragSnapshot;

		private static int NearestPoint(List<AudioCurvePoint> points, Func<float, float, NVector2> map, NVector2 mouse)
		{
			int best = -1;
			float bestDistance = 10f * 10f;
			for (int i = 0; i < points.Count; i++)
			{
				float distance = NVector2.DistanceSquared(map(points[i].X, points[i].Y), mouse);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = i;
				}
			}

			return best;
		}

		// ---------------------------------------------------------------- overview

		private void DrawOverview()
		{
			if (ImGui.Begin("Overview", ref showOverview))
			{
				AudioEvent current = SelectedEvent;
				if (current == null)
				{
					ImGui.TextDisabled(Project == null ? "No audio project open." : "No event selected.");
				}
				else
				{
					ImGui.TextUnformatted(current.Name);
					ImGui.SameLine();
					ImGui.TextDisabled(current.Folder.Length > 0 ? "in " + current.Folder : "");
					if (ImGui.SmallButton("Rename")) RequestRename(Selection.Of(SelectionKind.Event, current.Id), current.Path);
					ImGui.SameLine();
					if (ImGui.SmallButton("Duplicate")) DuplicateEvent(current);
					ImGui.SameLine();
					if (ImGui.SmallButton("Copy GUID")) ImGui.SetClipboardText(current.Id);
					if (ImGui.IsItemHovered()) ImGui.SetTooltip("Components reference the event by this GUID (the inspector's event picker does it for you).");
					ImGui.TextDisabled("Bank " + (Project.FindBank(current.BankId)?.Name ?? "?") + ", bus " + (Project.FindBus(current.BusId) != null ? Project.BusPath(Project.FindBus(current.BusId)) : "Master") + (current.Spatializer.Enabled ? ", 3D" : ", 2D") + (current.Persistent ? ", persistent" : ""));
					ImGui.Separator();

					ImGui.SeparatorText("Parameters");
					DrawParameterSliders(current);
					if (ImGui.SmallButton("+ Local Parameter"))
					{
						var parameter = new AudioParameter { Id = AudioProject.NewId(), Name = UniqueParameterName(current, "Parameter") };
						Edit("Add parameter", () => current.Parameters.Add(parameter));
						selection = Selection.Of(SelectionKind.LocalParameter, parameter.Id);
					}

					ImGui.SameLine();
					ImGui.TextDisabled("global ones: Parameters panel");

					ImGui.SeparatorText("Preview");
					EventInstance playing = Preview;
					ImGui.TextDisabled(playing != null ? playing.State + " at " + FormatTime(playing.TimelinePosition) + ", " + playing.VoiceCount + " voices" : "stopped");
					if (current.Spatializer.Enabled)
					{
						Label("Distance", "Where the preview instance is placed, relative to the listener (the scene camera in edit mode).");
						float distance = previewDistance;
						if (ImGui.SliderFloat("##pdist", ref distance, 0f, Math.Max(1f, current.Spatializer.MaxDistance * 1.2f), "%.1f"))
						{
							previewDistance = distance;
							playing?.Set3DAttributes(PreviewPosition());
						}

						Label("Angle");
						float angle = previewAngle;
						if (ImGui.SliderFloat("##pangle", ref angle, -180f, 180f, "%.0f deg"))
						{
							previewAngle = angle;
							playing?.Set3DAttributes(PreviewPosition());
						}

						if (playing != null)
						{
							// The listener may move (the scene camera): keep the preview in front of it.
							playing.Set3DAttributes(PreviewPosition());
						}
					}

					if (playing != null && playing.Description.Points.Length > 0)
					{
						foreach (CompiledMarkerPoint point in playing.Description.Points)
						{
							if (point.Kind == MarkerPointKind.Sustain && playing.State == PlaybackState.Sustaining)
							{
								if (ImGui.Button("Key Off (resume from the sustain point)")) playing.KeyOff();
								break;
							}
						}
					}

					ImGui.SeparatorText("Diagnostics");
					ImGui.TextDisabled(AudioSystem.DeviceStatus);
					// ImGui's text functions read '%' as a format: the percent sign is escaped.
					ImGui.TextDisabled($"{AudioSystem.ActiveVoices} voices ({AudioSystem.VirtualVoices} virtual), {AudioSystem.ActiveInstances} instances, {AudioSystem.CpuLoad * 100f:0.0}%% audio CPU, {AudioSystem.Underruns} underruns");
					LoadedBank loaded = AudioSystem.FindBank(Project.FindBank(current.BankId)?.Name ?? "");
					if (loaded != null && loaded.Warnings.Count > 0)
					{
						foreach (string warning in loaded.Warnings)
						{
							ImGui.TextColored(EditorStyle.Warning, warning);
						}
					}
				}
			}

			ImGui.End();
		}

		/// <summary>
		///     A knob per parameter the event sees (FMOD's parameter dials): drives the preview instance and the global
		///     values, shows a followed game instance's values. The name under each knob selects the parameter.
		/// </summary>
		private void DrawParameterSliders(AudioEvent current)
		{
			const float width = 84f;
			bool live = IsLiveShown;
			bool first = true;
			foreach (AudioParameter parameter in Project.ParametersOf(current))
			{
				bool global = Project.FindGlobalParameter(parameter.Id) != null;
				if (!first)
				{
					ImGui.SameLine(0f, 4f);
					if (ImGui.GetContentRegionAvail().X < width)
					{
						ImGui.NewLine();
					}
				}

				first = false;
				ImGui.PushID(parameter.Id);
				ImGui.BeginGroup();
				float value = ShownParameterValue(parameter);
				float min = Math.Min(parameter.Min, parameter.Max);
				float max = Math.Max(parameter.Min, parameter.Max);
				bool readOnly = live || (parameter.BuiltIn != BuiltInParameter.None && !global);
				string tooltip = (global ? "Global parameter" : "Local parameter") + (parameter.BuiltIn != BuiltInParameter.None ? " (built-in: " + parameter.BuiltIn + ")" : "") + ", " + ParameterValueText(parameter, min) + " .. " + ParameterValueText(parameter, max);
				if (StudioUi.Knob("##knob", ref value, min, max, parameter.Default, parameter.Name, ParameterValueText(parameter, value), StudioUi.KnobScale.Linear, false, 38f, width, readOnly, tooltip, parameter.Type == AudioParameterType.Continuous ? 0f : 1f))
				{
					SetPreviewParameter(parameter.Id, parameter.Clamp(value));
				}

				if (ImGui.BeginPopupContextItem("##param_menu"))
				{
					if (ImGui.MenuItem("Edit Parameter"))
					{
						selection = Selection.Of(global ? SelectionKind.GlobalParameter : SelectionKind.LocalParameter, parameter.Id);
					}

					if (ImGui.MenuItem("Open Sheet"))
					{
						openSheets.Add(parameter.Id);
						currentSheet = parameter.Id;
						tabSwitchRequested = true;
					}

					if (!global && ImGui.MenuItem("Remove Parameter"))
					{
						AudioParameter removed = parameter;
						Edit("Remove parameter", () => current.Parameters.Remove(removed));
					}

					ImGui.EndPopup();
				}

				bool selected = selection.Is(global ? SelectionKind.GlobalParameter : SelectionKind.LocalParameter, parameter.Id);
				if (ImGui.Selectable((global ? "global" : "local") + "##psel", selected, ImGuiSelectableFlags.None, new NVector2(width, 0f)))
				{
					selection = Selection.Of(global ? SelectionKind.GlobalParameter : SelectionKind.LocalParameter, parameter.Id);
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Click: edit the parameter in the Deck. Right click the knob: more.");
				}

				ImGui.EndGroup();
				ImGui.PopID();
			}

			if (Project.ParametersOf(current).Count == 0)
			{
				ImGui.TextDisabled("No parameters. Add one to drive instruments, conditions and automation.");
			}
		}

		#endregion
	}
}
