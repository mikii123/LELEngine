using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Audio
{
	/// <summary>The Mixer panel: one strip per bus (fader, meter, mute), the tree's children indented, the master first.</summary>
	internal sealed partial class AudioStudio
	{
		#region PrivateFields

		private readonly Dictionary<string, float> meterLeft = new Dictionary<string, float>(StringComparer.Ordinal);
		private readonly Dictionary<string, float> meterRight = new Dictionary<string, float>(StringComparer.Ordinal);

		#endregion

		#region PrivateMethods

		private void DrawMixer()
		{
			if (ImGui.Begin("Mixer", ref showMixer))
			{
				if (Project == null)
				{
					ImGui.TextDisabled("No audio project open.");
				}
				else
				{
					if (ImGui.Button("+ Bus"))
					{
						AudioBus parent = selection.Kind == SelectionKind.Bus ? Project.FindBus(selection.Id) : null;
						var bus = new AudioBus { Id = AudioProject.NewId(), Name = "Bus " + Project.Buses.Count, ParentId = parent?.Id ?? AudioProject.MasterBusId };
						Edit("Add bus", () => Project.Buses.Add(bus));
						selection = Selection.Of(SelectionKind.Bus, bus.Id);
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Adds a bus under the selected bus (or the master). Events route to a bus in the Deck.");
					}

					ImGui.SameLine();
					ImGui.TextDisabled($"{AudioSystem.ActiveVoices} voices, {AudioSystem.ActiveInstances} instances, {AudioSystem.CpuLoad * 100f:0.0}%% audio CPU" + (AudioSystem.ActiveSnapshots > 0 ? $", {AudioSystem.ActiveSnapshots} snapshots active" : ""));
					AudioSnapshot scoped = selection.Kind == SelectionKind.Snapshot ? Project.FindSnapshot(selection.Id) : null;
					if (scoped != null)
					{
						ImGui.TextColored(EditorStyle.Warning, "Snapshot '" + scoped.Name + "': the faders set its values; tick a bus to scope its volume.");
					}

					ImGui.Separator();
					if (ImGui.BeginChild("##mixer_strips", NVector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
					{
						List<AudioBus> buses = Project.BusesInTreeOrder();
						float height = Math.Max(160f, ImGui.GetContentRegionAvail().Y - 4f);
						bool first = true;
						foreach (AudioBus bus in buses)
						{
							if (!first)
							{
								ImGui.SameLine();
							}

							first = false;
							DrawStrip(bus, height, Depth(bus));
						}
					}

					ImGui.EndChild();
				}
			}

			ImGui.End();
		}

		private int Depth(AudioBus bus)
		{
			int depth = 0;
			for (AudioBus parent = Project.FindBus(bus.ParentId); parent != null && depth < 32; parent = Project.FindBus(parent.ParentId))
			{
				depth++;
			}

			return depth;
		}

		private void DrawStrip(AudioBus bus, float height, int depth)
		{
			bool isMaster = bus.Id == AudioProject.MasterBusId;
			bool selected = selection.Is(SelectionKind.Bus, bus.Id);
			ImGui.PushID(bus.Id);
			if (selected)
			{
				ImGui.PushStyleColor(ImGuiCol.ChildBg, new System.Numerics.Vector4(0.2f, 0.22f, 0.26f, 1f));
			}

			if (ImGui.BeginChild("##strip", new NVector2(96f, height), ImGuiChildFlags.Borders))
			{
				string name = new string(' ', Math.Min(depth, 6)) + bus.Name;
				if (ImGui.Selectable(name + "##name", selected))
				{
					selection = Selection.Of(SelectionKind.Bus, bus.Id);
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(Project.BusPath(bus) + "\n" + bus.Effects.Count + (bus.Effects.Count == 1 ? " effect" : " effects") + "\nDouble click: rename");
				}

				if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !isMaster)
				{
					RequestRename(Selection.Of(SelectionKind.Bus, bus.Id), bus.Name);
				}

				if (ImGui.BeginPopupContextItem("##bus_menu"))
				{
					if (ImGui.MenuItem("Rename...", "", false, !isMaster)) RequestRename(Selection.Of(SelectionKind.Bus, bus.Id), bus.Name);
					if (ImGui.MenuItem("Add Child Bus"))
					{
						var child = new AudioBus { Id = AudioProject.NewId(), Name = "Bus " + Project.Buses.Count, ParentId = bus.Id };
						Edit("Add bus", () => Project.Buses.Add(child));
						selection = Selection.Of(SelectionKind.Bus, child.Id);
					}

					if (ImGui.MenuItem("Delete", "", false, !isMaster))
					{
						Edit("Delete bus", () =>
						{
							foreach (AudioBus other in Project.Buses)
							{
								if (other.ParentId == bus.Id) other.ParentId = bus.ParentId;
							}

							foreach (AudioEvent audioEvent in Project.Events)
							{
								if (audioEvent.BusId == bus.Id) audioEvent.BusId = bus.ParentId ?? AudioProject.MasterBusId;
							}

							Project.Buses.Remove(bus);
						});
					}

					ImGui.EndPopup();
				}

				// Fader and meter side by side. With a snapshot selected the fader edits the snapshot's value for the bus.
				AudioSnapshot scoped = selection.Kind == SelectionKind.Snapshot ? Project.FindSnapshot(selection.Id) : null;
				SnapshotBusValue snapshotValue = scoped?.FindBus(bus.Id);
				bool snapshotMode = scoped != null;
				bool inScope = snapshotValue != null && snapshotValue.SetVolume;
				float faderHeight = Math.Max(60f, height - (snapshotMode ? 112f : 92f));
				float volume = snapshotMode ? (inScope ? snapshotValue.VolumeDb : bus.VolumeDb) : bus.VolumeDb;
				if (snapshotMode)
				{
					ImGui.PushStyleColor(ImGuiCol.SliderGrab, inScope ? EditorStyle.Selection : EditorStyle.Dim);
				}

				ImGui.BeginDisabled(snapshotMode && !inScope);
				bool changed = ImGui.VSliderFloat("##fader", new NVector2(30f, faderHeight), ref volume, AudioMath.SilenceDb, AudioMath.MaxDb, "");
				Track(changed);
				if (changed)
				{
					if (snapshotMode)
					{
						snapshotValue.VolumeDb = volume;
					}
					else
					{
						bus.VolumeDb = volume;
					}
				}

				ImGui.EndDisabled();
				if (snapshotMode)
				{
					ImGui.PopStyleColor();
				}

				if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
				{
					ImGui.SetTooltip((volume <= AudioMath.SilenceDb ? "-inf dB" : volume.ToString("0.0") + " dB") + (snapshotMode ? inScope ? " (snapshot value)" : " (not in the snapshot: tick the box below)" : " (double click to reset)"));
				}

				if (!snapshotMode && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				{
					Edit("Reset bus volume", () => bus.VolumeDb = 0f);
				}

				ImGui.SameLine();
				DrawMeter(bus.Id, faderHeight);
				string shownVolume = volume <= AudioMath.SilenceDb ? "-inf" : volume.ToString("0.0");
				if (!snapshotMode && AudioSystem.ActiveSnapshots > 0)
				{
					float effective = EffectiveVolume(bus.Id, bus.VolumeDb);
					if (Math.Abs(effective - bus.VolumeDb) > 0.05f)
					{
						shownVolume += " > " + (effective <= AudioMath.SilenceDb ? "-inf" : effective.ToString("0.0"));
					}
				}

				ImGui.TextUnformatted(shownVolume);
				if (snapshotMode)
				{
					bool tick = inScope;
					if (ImGui.Checkbox("##scope", ref tick) && tick != inScope)
					{
						SetSnapshotVolume(scoped, bus, tick, bus.VolumeDb);
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("The snapshot sets this bus's volume.");
					}

					ImGui.SameLine();
					ImGui.TextDisabled("scope");
				}
				bool mute = bus.Mute;
				if (mute)
				{
					ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Selection);
				}

				if (ImGui.Button("M", new NVector2(26f, 0f)))
				{
					Edit("Toggle bus mute", () => bus.Mute = !mute);
				}

				if (mute)
				{
					ImGui.PopStyleColor();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Mute");
				}

				ImGui.SameLine();
				ImGui.TextDisabled(bus.Effects.Count > 0 ? bus.Effects.Count + " fx" : "");
			}

			ImGui.EndChild();
			if (selected)
			{
				ImGui.PopStyleColor();
			}

			ImGui.PopID();
		}

		/// <summary>The bus volume after the active snapshots, as the mixer computes it (the definition's when unknown).</summary>
		private static float EffectiveVolume(string busId, float fallback)
		{
			Mixer mixer = AudioSystem.Mixer;
			if (mixer != null)
			{
				foreach (BusState state in mixer.Buses)
				{
					if (state.Id == busId)
					{
						return state.EffectiveVolumeDb;
					}
				}
			}

			return fallback;
		}

		/// <summary>Two peak bars of a bus from the mixer's meters, falling off smoothly.</summary>
		private void DrawMeter(string busId, float height)
		{
			float peakLeft = 0f;
			float peakRight = 0f;
			Mixer mixer = AudioSystem.Mixer;
			if (mixer != null)
			{
				foreach (BusState state in mixer.Buses)
				{
					if (state.Id == busId)
					{
						peakLeft = state.PeakLeft;
						peakRight = state.PeakRight;
						break;
					}
				}
			}

			meterLeft.TryGetValue(busId, out float shownLeft);
			meterRight.TryGetValue(busId, out float shownRight);
			float decay = 0.92f;
			shownLeft = Math.Max(peakLeft, shownLeft * decay);
			shownRight = Math.Max(peakRight, shownRight * decay);
			meterLeft[busId] = shownLeft;
			meterRight[busId] = shownRight;

			NVector2 min = ImGui.GetCursorScreenPos();
			var size = new NVector2(22f, height);
			ImGui.InvisibleButton("##meter", size);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, min + size, Color(0.1f, 0.1f, 0.11f));
			DrawMeterBar(draw, min, new NVector2(10f, height), shownLeft);
			DrawMeterBar(draw, min + new NVector2(12f, 0f), new NVector2(10f, height), shownRight);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip($"Peak {AudioMath.GainToDb(Math.Max(shownLeft, shownRight)):0.0} dB");
			}
		}

		private static void DrawMeterBar(ImDrawListPtr draw, NVector2 min, NVector2 size, float peak)
		{
			// Decibel scale from -60 to 0 (above 0 the bar turns red).
			float db = AudioMath.GainToDb(Math.Max(peak, 1e-6f));
			float fraction = Math.Clamp((db + 60f) / 60f, 0f, 1f);
			float top = min.Y + size.Y * (1f - fraction);
			uint green = Color(0.3f, 0.78f, 0.35f);
			uint yellow = Color(0.9f, 0.8f, 0.3f);
			uint red = Color(0.95f, 0.35f, 0.3f);
			float yellowAt = min.Y + size.Y * (1f - 48f / 60f);
			float redAt = min.Y + size.Y * (1f - 57f / 60f);
			if (fraction > 0f)
			{
				draw.AddRectFilled(new NVector2(min.X, Math.Max(top, yellowAt)), new NVector2(min.X + size.X, min.Y + size.Y), green);
				if (top < yellowAt)
				{
					draw.AddRectFilled(new NVector2(min.X, Math.Max(top, redAt)), new NVector2(min.X + size.X, yellowAt), yellow);
				}

				if (top < redAt)
				{
					draw.AddRectFilled(new NVector2(min.X, top), new NVector2(min.X + size.X, redAt), red);
				}
			}

			if (peak > 1f)
			{
				draw.AddRectFilled(min, new NVector2(min.X + size.X, min.Y + 4f), red);
			}
		}

		#endregion
	}
}
