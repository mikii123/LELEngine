using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using LELEngine.Audio;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace LELEngine.Editor.Animation
{
	/// <summary>The Timeline window's edits: channels, clips, markers, parameters, keys, and the small popups that show the settings of what was clicked.</summary>
	internal sealed partial class TimelineWindow
	{
		#region PrivateFields

		private const string ChannelPayload = "LEL_TIMELINE_CHANNEL";
		private const float FieldLabelWidth = 112f;
		private const float FieldWidth = 180f;

		private static readonly Color4[] ChannelColors =
		{
			new Color4(0.37f, 0.6f, 0.9f, 1f), new Color4(0.36f, 0.72f, 0.55f, 1f), new Color4(0.85f, 0.56f, 0.28f, 1f), new Color4(0.65f, 0.5f, 0.9f, 1f),
			new Color4(0.82f, 0.42f, 0.55f, 1f), new Color4(0.45f, 0.72f, 0.82f, 1f), new Color4(0.72f, 0.72f, 0.36f, 1f)
		};

		private static readonly string[] QuantizationNames = { "None", "1/4 Beat", "1/2 Beat", "Beat", "2 Beats", "Bar", "2 Bars", "4 Bars", "8 Bars" };

		private string draggedChannelId;
		private AudioMarker popupMarker;
		private TimelineClip popupClip;
		private TimelineChannel popupClipChannel;

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- fields

		private static void FieldLabel(string label)
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			ImGui.SameLine(FieldLabelWidth);
			ImGui.SetNextItemWidth(FieldWidth);
			TestAnchors.LineLabel(label);
		}

		/// <summary>A value edited in place: the edit opens before the value changes (one undo step for a whole drag), the players follow at once.</summary>
		private void AfterField(bool changed, string editName, Action apply)
		{
			if (ImGui.IsItemActivated() || changed)
			{
				document.BeginEdit(editName);
			}

			if (changed)
			{
				apply();
				Asset.MarkChanged();
			}

			if (ImGui.IsItemDeactivated())
			{
				document.EndEdit();
			}
		}

		private void DoubleField(string label, double value, float speed, double min, double max, string format, Action<double> apply)
		{
			FieldLabel(label);
			float shown = (float)value;
			bool changed = EditorGui.DragFloat("##" + label, ref shown, speed, (float)min, (float)max, format);
			AfterField(changed, "Edit " + label, () => apply(Math.Clamp(shown, min, max)));
		}

		private void FloatField(string label, float value, float speed, float min, float max, string format, Action<float> apply)
		{
			FieldLabel(label);
			float shown = value;
			bool changed = EditorGui.DragFloat("##" + label, ref shown, speed, min, max, format);
			AfterField(changed, "Edit " + label, () => apply(Math.Clamp(shown, min, max)));
		}

		private void IntField(string label, int value, int min, int max, Action<int> apply)
		{
			FieldLabel(label);
			int shown = value;
			bool changed = EditorGui.DragInt("##" + label, ref shown, 0.2f, min, max);
			AfterField(changed, "Edit " + label, () => apply(Math.Clamp(shown, min, max)));
		}

		private void TextField(string label, string value, Action<string> apply)
		{
			FieldLabel(label);
			string shown = value ?? "";
			bool changed = EditorGui.InputText("##" + label, ref shown, 128);
			AfterField(changed, "Edit " + label, () => apply(shown));
		}

		private void BoolField(string label, bool value, Action<bool> apply)
		{
			bool shown = value;
			if (EditorGui.Checkbox(label, ref shown))
			{
				document.Edit("Edit " + label, () => apply(shown));
			}
		}

		/// <summary>Conditions on parameters: each must hold.</summary>
		private void DrawConditions(List<AudioCondition> conditions)
		{
			ImGui.SeparatorText("Conditions");
			for (int i = 0; i < conditions.Count; i++)
			{
				AudioCondition condition = conditions[i];
				ImGui.PushID(i);
				AudioParameter parameter = Asset.FindParameter(condition.ParameterId);
				ImGui.SetNextItemWidth(110f);
				if (EditorGui.BeginCombo("##parameter", parameter?.Name ?? "(missing)"))
				{
					foreach (AudioParameter candidate in Asset.parameters)
					{
						if (EditorGui.Selectable(candidate.Name + "##" + candidate.Id, candidate == parameter))
						{
							document.Edit("Set condition", () =>
							{
								condition.ParameterId = candidate.Id;
								condition.Min = candidate.Min;
								condition.Max = candidate.Max;
							});
						}
					}

					EditorGui.EndCombo();
				}

				float lower = parameter != null ? Math.Min(parameter.Min, parameter.Max) : -1e6f;
				float upper = parameter != null ? Math.Max(parameter.Min, parameter.Max) : 1e6f;
				float speed = Math.Max((upper - lower) * 0.005f, 0.001f);
				ImGui.SameLine();
				ImGui.SetNextItemWidth(58f);
				float min = condition.Min;
				bool minChanged = EditorGui.DragFloat("##min", ref min, speed, lower, upper, "%.2f");
				AfterField(minChanged, "Edit condition", () => condition.Min = min);
				ImGui.SameLine();
				ImGui.TextDisabled("to");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(58f);
				float max = condition.Max;
				bool maxChanged = EditorGui.DragFloat("##max", ref max, speed, lower, upper, "%.2f");
				AfterField(maxChanged, "Edit condition", () => condition.Max = max);
				ImGui.SameLine();
				if (EditorGui.SmallButton("x"))
				{
					document.Edit("Remove condition", () => conditions.Remove(condition));
					ImGui.PopID();
					break;
				}

				ImGui.PopID();
			}

			ImGui.BeginDisabled(Asset.parameters.Count == 0);
			if (EditorGui.SmallButton("+ Condition"))
			{
				AudioParameter first = Asset.parameters[0];
				document.Edit("Add condition", () => conditions.Add(new AudioCondition { ParameterId = first.Id, Min = first.Min, Max = first.Max }));
			}

			ImGui.EndDisabled();
			if (Asset.parameters.Count == 0)
			{
				ImGui.SameLine();
				ImGui.TextDisabled("add a parameter first");
			}
		}

		/// <summary>An edit opened by a field whose popup closed mid-way ends here.</summary>
		private void FinishPendingEdit()
		{
			if (document != null && document.Editing && dragKind == DragKind.None && !recorder.HoldsEdit && !ImGui.IsAnyItemActive())
			{
				document.EndEdit();
			}
		}

		// ---------------------------------------------------------------- menus

		private void DrawContextMenu()
		{
			if (openContext)
			{
				ImGui.OpenPopup("##timeline_context");
				openContext = false;
			}

			if (!ImGui.BeginPopup("##timeline_context"))
			{
				return;
			}

			Hit hit = contextHit;
			double at = SnapTime(hit.Time, null);
			switch (hit.Kind)
			{
				case HitKind.Logic:
					ImGui.TextDisabled("Logic at " + FormatTime(at));
					ImGui.Separator();
					DrawAddMarkerItems(at);
					break;
				case HitKind.Lane:
					ImGui.TextDisabled((TargetOf(hit.Channel)?.Name ?? hit.Channel.name) + " at " + FormatTime(at));
					ImGui.Separator();
					DrawLaneItems(hit.Channel, at);
					ImGui.Separator();
					DrawChannelMenu(hit.Channel, TargetOf(hit.Channel));
					break;
				case HitKind.Key:
				case HitKind.PropertyLane:
					ImGui.TextDisabled(hit.Property?.Label ?? hit.Key.Property?.Label ?? "");
					ImGui.Separator();
					if (hit.Kind == HitKind.PropertyLane && EditorMenu.Item("Add Key Here"))
					{
						KeyProperty(hit.Channel, hit.Property, at);
					}

					if (hit.Kind == HitKind.Key && EditorMenu.Item("Delete Key"))
					{
						selectedKeyClip = hit.Key.Clip;
						selectedKeyProperty = hit.Key.Property;
						selectedKeyTime = hit.Key.ClipTime;
						DeleteSelection();
					}

					break;
				default:
					ImGui.TextDisabled("Nothing here");
					break;
			}

			ImGui.EndPopup();
		}

		private void DrawLaneItems(TimelineChannel channel, double at)
		{
			if (EditorMenu.Item("New Clip Here"))
			{
				TimelineClip created = null;
				document.Edit("New clip", () => created = recorder.CreateClip(channel, at));
				if (created != null)
				{
					Select(SelectionKind.Clip, created.id);
				}
			}

			if (EditorMenu.Begin("Add Clip"))
			{
				bool any = false;
				foreach (string path in AssetDatabase.GetAllAssetPaths(AnimationClip.Extension))
				{
					any = true;
					if (EditorMenu.Item(path))
					{
						AddClipAsset(channel, path, at);
					}
				}

				if (!any)
				{
					ImGui.TextDisabled("No animation clips in the project.");
				}

				EditorMenu.End();
			}

			if (EditorMenu.Item("Paste Clip", "", false, clipboardClip != null))
			{
				TimelineClip copy = CopyOf(clipboardClip);
				copy.start = at;
				document.Edit("Paste clip", () => channel.clips.Add(copy));
				Select(SelectionKind.Clip, copy.id);
			}
		}

		private void AddClipAsset(TimelineChannel channel, string path, double at)
		{
			AnimationClip clip = AnimationClip.Load(path);
			if (clip == null)
			{
				return;
			}

			var placed = new TimelineClip { id = TimelineAsset.NewId(), clip = clip, start = at, length = Math.Max(clip.length, 0.1f), loop = clip.loop };
			document.Edit("Add clip", () => channel.clips.Add(placed));
			Select(SelectionKind.Clip, placed.id);
		}

		private void DrawChannelMenu(TimelineChannel channel, GameObject target)
		{
			if (EditorMenu.Begin("Key Property", target != null))
			{
				DrawKeyPropertyItems(channel, target);
				EditorMenu.End();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("A key at the playhead with the object's value: every exposed value of its components, your scripts' too");
			}

			if (EditorMenu.Item("Select Object", "", false, target != null))
			{
				editor.SelectObject(target);
			}

			NVector3 color = new NVector3(channel.color.R, channel.color.G, channel.color.B);
			ImGui.SetNextItemWidth(FieldWidth);
			bool colorChanged = EditorGui.ColorEdit3("Color##channel", ref color, ImGuiColorEditFlags.NoInputs);
			AfterField(colorChanged, "Change channel color", () => channel.color = new Color4(color.X, color.Y, color.Z, 1f));
			ImGui.Separator();
			if (EditorMenu.Item("Remove Channel"))
			{
				RemoveChannel(channel);
			}
		}

		private void DrawKeyPropertyItems(TimelineChannel channel, GameObject target)
		{
			foreach (AnimatableProperty property in AnimationBinder.GetAnimatableProperties(target))
			{
				if (EditorMenu.Item("GameObject." + property.DisplayName))
				{
					KeyProperty(channel, new PropertyRow { Path = "", TypeName = "", PropertyName = property.Property, Kind = property.Kind, Key = "||" + property.Property });
				}
			}

			foreach (Behaviour component in target.Components)
			{
				if (component is Timeline || component is LELEngine.Animation.Animator)
				{
					continue;
				}

				string typeName = AnimationBinder.TypeName(component.GetType());
				if (!EditorMenu.Begin(component.GetType().Name))
				{
					continue;
				}

				foreach (AnimatableProperty property in AnimationBinder.GetAnimatableProperties(component))
				{
					if (EditorMenu.Item(property.DisplayName + "##" + property.Property + property.Kind))
					{
						KeyProperty(channel, new PropertyRow { Path = "", TypeName = typeName, PropertyName = property.Property, Kind = property.Kind, Key = "|" + component.GetType().Name + "|" + property.Property });
					}
				}

				EditorMenu.End();
			}
		}

		internal void DrawAddMarkerItems(double at)
		{
			if (EditorMenu.Item("Add Loop Region")) AddMarker(new LoopRegion { Position = at, RegionLength = 2.0, Name = "Loop" });
			if (EditorMenu.Item("Add Transition Region")) AddMarker(new TransitionRegion { Position = at, RegionLength = 2.0, DestinationId = FirstDestination(), Name = "Transition Region", Quantization = QuantizationInterval.None });
			if (EditorMenu.Item("Add Transition Marker")) AddMarker(new TransitionMarker { Position = at, DestinationId = FirstDestination(), Name = "Transition" });
			if (EditorMenu.Item("Add Destination Marker")) AddMarker(new DestinationMarker { Position = at, Name = "Marker " + (Asset.markers.Count + 1) });
			if (EditorMenu.Item("Add Sustain Point")) AddMarker(new SustainPoint { Position = at, Name = "Sustain" });
			if (EditorMenu.Item("Add Tempo Marker")) AddMarker(new TempoMarker { Position = at, Name = "Tempo" });
		}

		private string FirstDestination()
		{
			foreach (AudioMarker marker in Asset.markers)
			{
				if (marker is DestinationMarker)
				{
					return marker.Id;
				}
			}

			return TransitionMarker.EndDestination;
		}

		private void AddMarker(AudioMarker marker)
		{
			marker.Id = TimelineAsset.NewId();
			document.Edit("Add " + marker.Kind, () => Asset.markers.Add(marker));
			Select(SelectionKind.Marker, marker.Id);
			openMarkerPopup = marker.Id;
		}

		// ---------------------------------------------------------------- popups

		private void DrawEditPopups()
		{
			if (openMarkerPopup != null)
			{
				popupMarker = Asset.FindMarker(openMarkerPopup);
				openMarkerPopup = null;
				ImGui.OpenPopup("##timeline_marker");
			}

			if (ImGui.BeginPopup("##timeline_marker"))
			{
				if (popupMarker != null && Asset.markers.Contains(popupMarker))
				{
					DrawMarkerPopup(popupMarker);
				}
				else
				{
					ImGui.CloseCurrentPopup();
				}

				ImGui.EndPopup();
			}

			if (openClipPopup != null)
			{
				popupClip = Asset.FindClip(openClipPopup, out popupClipChannel);
				openClipPopup = null;
				ImGui.OpenPopup("##timeline_clip");
			}

			if (ImGui.BeginPopup("##timeline_clip"))
			{
				if (popupClip != null && Asset.FindClip(popupClip.id, out popupClipChannel) == popupClip)
				{
					DrawClipPopup(popupClip, popupClipChannel);
				}
				else
				{
					ImGui.CloseCurrentPopup();
				}

				ImGui.EndPopup();
			}
		}

		private void DrawMarkerPopup(AudioMarker marker)
		{
			ImGui.TextDisabled(marker.Kind);
			ImGui.Separator();
			if (!(marker is TempoMarker))
			{
				TextField("Name", marker.Name, value => marker.Name = value);
			}

			DoubleField("Position", marker.Position, 0.01f, 0.0, 1e6, "%.3f s", value => marker.Position = value);
			switch (marker)
			{
				case TransitionMarker transition:
					if (transition is TransitionRegion region)
					{
						DoubleField("Length", region.RegionLength, 0.01f, 0.01, 1e6, "%.3f s", value => region.RegionLength = value);
						FieldLabel("Quantization");
						if (EditorGui.BeginCombo("##Quantization", QuantizationNames[(int)region.Quantization]))
						{
							for (int i = 0; i < QuantizationNames.Length; i++)
							{
								QuantizationInterval interval = (QuantizationInterval)i;
								if (EditorGui.Selectable(QuantizationNames[i], region.Quantization == interval))
								{
									document.Edit("Set quantization", () => region.Quantization = interval);
								}
							}

							EditorGui.EndCombo();
						}

						if (ImGui.IsItemHovered())
						{
							ImGui.SetTooltip("The jump waits for the next grid point of the tempo markers (none: at once)");
						}
					}

					FieldLabel("Destination");
					if (EditorGui.BeginCombo("##Destination", DestinationName(transition.DestinationId)))
					{
						if (EditorGui.Selectable("End (stops the timeline)", transition.DestinationId == TransitionMarker.EndDestination))
						{
							document.Edit("Set destination", () => transition.DestinationId = TransitionMarker.EndDestination);
						}

						foreach (AudioMarker candidate in Asset.markers)
						{
							if (candidate is DestinationMarker && EditorGui.Selectable((candidate.Name ?? "marker") + " (" + FormatTime(candidate.Position) + ")##" + candidate.Id, candidate.Id == transition.DestinationId))
							{
								string id = candidate.Id;
								document.Edit("Set destination", () => transition.DestinationId = id);
							}
						}

						EditorGui.EndCombo();
					}

					DoubleField("Crossfade", transition.Crossfade, 0.01f, 0.0, 60.0, "%.2f s", value => transition.Crossfade = value);
					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Seconds the pose before the jump blends into the pose at the destination (0: a cut)");
					}

					FloatField("Probability", transition.Probability, 0.5f, 0f, 100f, "%.0f %%", value => transition.Probability = value);
					DrawConditions(transition.Conditions);
					break;
				case LoopRegion loop:
					DoubleField("Length", loop.RegionLength, 0.01f, 0.01, 1e6, "%.3f s", value => loop.RegionLength = value);
					IntField("Max Loops", loop.MaxLoops, 0, 100000, value => loop.MaxLoops = value);
					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("How many times it jumps back per pass; 0: while the conditions hold");
					}

					DrawConditions(loop.Conditions);
					break;
				case TempoMarker tempo:
					FloatField("Tempo", tempo.Bpm, 0.5f, 20f, 400f, "%.1f bpm", value => tempo.Bpm = value);
					IntField("Beats Per Bar", tempo.BeatsPerBar, 1, 32, value => tempo.BeatsPerBar = value);
					ImGui.TextDisabled($"The grid transition regions wait for: beat {tempo.SecondsPerBeat:0.000} s, bar {tempo.SecondsPerBeat * tempo.BeatsPerBar:0.000} s");
					break;
				case SustainPoint _:
					ImGui.TextDisabled("The cursor waits here until the game calls timeline.KeyOff().");
					break;
				case DestinationMarker _:
					ImGui.TextDisabled("Transitions jump here; the game hears its name (MarkerReached).");
					break;
			}

			ImGui.Separator();
			if (EditorGui.SmallButton("Delete"))
			{
				document.Edit("Delete " + marker.Kind, () => Asset.markers.Remove(marker));
				ClearSelection();
				ImGui.CloseCurrentPopup();
			}
		}

		private void DrawClipPopup(TimelineClip clip, TimelineChannel channel)
		{
			ImGui.TextDisabled("Clip on " + (TargetOf(channel)?.Name ?? channel.name));
			ImGui.Separator();
			FieldLabel("Clip");
			object asset = clip.clip;
			if (ObjectField.Draw(typeof(AnimationClip), ref asset, context))
			{
				document.Edit("Set clip", () => clip.clip = asset as AnimationClip);
			}

			DoubleField("Start", clip.start, 0.01f, 0.0, 1e6, "%.3f s", value => clip.start = value);
			DoubleField("Length", clip.length, 0.01f, 0.001, 1e6, "%.3f s", value => clip.length = value);
			DoubleField("Clip Start", clip.startOffset, 0.01f, 0.0, 1e6, "%.3f s", value => clip.startOffset = value);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Seconds into the clip where the region starts (dragging the left edge trims it too)");
			}

			FloatField("Speed", clip.speed, 0.01f, 0.01f, 100f, "x %.2f", value => clip.speed = value);
			DoubleField("Fade In", clip.fadeIn.Length, 0.01f, 0.0, clip.length, "%.3f s", value => clip.fadeIn.Length = value);
			DoubleField("Fade Out", clip.fadeOut.Length, 0.01f, 0.0, clip.length, "%.3f s", value => clip.fadeOut.Length = value);
			BoolField("Loop", clip.loop, value => clip.loop = value);
			DrawConditions(clip.conditions);
			ImGui.Separator();
			ImGui.BeginDisabled(clip.clip == null);
			if (EditorGui.SmallButton("Open Keys"))
			{
				OpenInAnimationWindow(clip, channel);
				ImGui.CloseCurrentPopup();
			}

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip("The clip's keys in the Animation window, on this channel's object");
			}

			ImGui.SameLine();
			if (EditorGui.SmallButton("Fit To Clip") && clip.clip != null)
			{
				document.Edit("Fit clip", () => clip.length = Math.Max((clip.clip.length - clip.startOffset) / Math.Max(clip.speed, 1e-3f), 0.01));
			}

			ImGui.SameLine();
			if (EditorGui.SmallButton("Make Unique") && clip.clip != null)
			{
				MakeUnique(clip, channel);
			}

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip("Its own copy of the .anim file: keying it no longer changes other uses of the clip");
			}

			ImGui.EndDisabled();
			ImGui.BeginDisabled(playhead <= clip.start || playhead >= clip.End);
			if (EditorGui.SmallButton("Split At Playhead"))
			{
				SplitClip(clip, channel);
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndDisabled();
			ImGui.SameLine();
			if (EditorGui.SmallButton("Copy"))
			{
				clipboardClip = CopyOf(clip);
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (EditorGui.SmallButton("Duplicate"))
			{
				DuplicateSelection();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (EditorGui.SmallButton("Delete"))
			{
				document.Edit("Delete clip", () => channel.clips.Remove(clip));
				ClearSelection();
				ImGui.CloseCurrentPopup();
			}
		}

		private void DrawParameterPopup(AudioParameter parameter)
		{
			ImGui.TextDisabled("Parameter");
			ImGui.Separator();
			TextField("Name", parameter.Name, value => parameter.Name = value);
			FloatField("Min", parameter.Min, 0.01f, -1e6f, 1e6f, "%.2f", value => parameter.Min = value);
			FloatField("Max", parameter.Max, 0.01f, -1e6f, 1e6f, "%.2f", value => parameter.Max = value);
			FloatField("Default", parameter.Default, 0.01f, Math.Min(parameter.Min, parameter.Max), Math.Max(parameter.Min, parameter.Max), "%.2f", value => parameter.Default = value);
			FloatField("Seek Speed", parameter.SeekSpeed, 0.01f, 0f, 1e6f, "%.2f /s", value => parameter.SeekSpeed = value);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Units per second the value moves towards what the game set; 0: it jumps");
			}

			BoolField("Whole numbers", parameter.Type != AudioParameterType.Continuous, value => parameter.Type = value ? AudioParameterType.Discrete : AudioParameterType.Continuous);
			ImGui.Separator();
			if (EditorGui.SmallButton("Delete"))
			{
				document.Edit("Delete parameter", () => Asset.parameters.Remove(parameter));
				ImGui.CloseCurrentPopup();
			}
		}

		// ---------------------------------------------------------------- channels

		private TimelineChannel NewChannel(GameObject target)
		{
			var channel = new TimelineChannel
			{
				id = TimelineAsset.NewId(),
				name = target != null ? target.Name : "Channel " + (Asset.channels.Count + 1),
				color = ChannelColors[Asset.channels.Count % ChannelColors.Length]
			};
			Asset.channels.Add(channel);
			if (target != null && host != null)
			{
				document.TouchHost(host);
				host.SetBinding(channel, target);
			}

			return channel;
		}

		private void AddChannel(GameObject target)
		{
			// An object has one channel: it writes its properties once.
			if (target != null)
			{
				foreach (TimelineChannel existing in Asset.channels)
				{
					if (TargetOf(existing) == target)
					{
						Select(SelectionKind.Channel, existing.id);
						return;
					}
				}
			}

			TimelineChannel created = null;
			document.Edit("Add channel", () => created = NewChannel(target));
			if (created != null)
			{
				Select(SelectionKind.Channel, created.id);
			}
		}

		private void AddChannelFromSelection()
		{
			if (editor.Selected != null && !editor.Selected.IsDestroyed)
			{
				AddChannel(editor.Selected);
			}
		}

		private void BindChannel(TimelineChannel channel, GameObject target)
		{
			if (host == null)
			{
				return;
			}

			document.Edit("Bind channel", () =>
			{
				document.TouchHost(host);
				host.SetBinding(channel, target);
				if (target != null)
				{
					channel.name = target.Name;
				}
			});
		}

		private void RemoveChannel(TimelineChannel channel)
		{
			document.Edit("Remove channel", () =>
			{
				Asset.channels.Remove(channel);
				TimelineBinding binding = host?.FindBinding(channel.id);
				if (binding != null)
				{
					document.TouchHost(host);
					host.bindings.Remove(binding);
				}
			});
			recorder.SetArmed(channel.id, false);
			ClearSelection();
		}

		// ---------------------------------------------------------------- selection commands

		private static TimelineClip CopyOf(TimelineClip clip)
		{
			var copy = new TimelineClip
			{
				id = TimelineAsset.NewId(), clip = clip.clip, start = clip.start, length = clip.length, startOffset = clip.startOffset, speed = clip.speed, loop = clip.loop,
				fadeIn = clip.fadeIn.Clone(), fadeOut = clip.fadeOut.Clone()
			};
			foreach (AudioCondition condition in clip.conditions)
			{
				copy.conditions.Add(new AudioCondition { ParameterId = condition.ParameterId, Min = condition.Min, Max = condition.Max });
			}

			return copy;
		}

		private void DuplicateSelection()
		{
			if (selectionKind == SelectionKind.Clip)
			{
				TimelineClip clip = Asset.FindClip(selectedId, out TimelineChannel channel);
				if (clip != null)
				{
					TimelineClip copy = CopyOf(clip);
					copy.start = clip.End;
					document.Edit("Duplicate clip", () => channel.clips.Add(copy));
					Select(SelectionKind.Clip, copy.id);
				}
			}
			else if (selectionKind == SelectionKind.Marker)
			{
				AudioMarker marker = Asset.FindMarker(selectedId);
				if (marker != null)
				{
					AudioMarker copy = marker.Clone();
					copy.Id = TimelineAsset.NewId();
					copy.Position = marker.End + (marker.Length > 0.0 ? 0.0 : 1.0);
					document.Edit("Duplicate marker", () => Asset.markers.Add(copy));
					Select(SelectionKind.Marker, copy.Id);
				}
			}
		}

		private void DeleteSelection()
		{
			if (selectedKeyClip != null && selectedKeyProperty != null)
			{
				DeleteKey(selectedKeyClip, selectedKeyProperty, selectedKeyTime);
				selectedKeyClip = null;
				selectedKeyProperty = null;
				return;
			}

			switch (selectionKind)
			{
				case SelectionKind.Clip:
				{
					TimelineClip clip = Asset.FindClip(selectedId, out TimelineChannel channel);
					if (clip != null)
					{
						document.Edit("Delete clip", () => channel.clips.Remove(clip));
					}

					ClearSelection();
					break;
				}
				case SelectionKind.Marker:
				{
					AudioMarker marker = Asset.FindMarker(selectedId);
					if (marker != null)
					{
						document.Edit("Delete " + marker.Kind, () => Asset.markers.Remove(marker));
					}

					ClearSelection();
					break;
				}
				case SelectionKind.Channel:
				{
					TimelineChannel channel = Asset.FindChannel(selectedId);
					if (channel != null)
					{
						RemoveChannel(channel);
					}

					break;
				}
			}
		}

		private void SplitClip(TimelineClip clip, TimelineChannel channel)
		{
			TimelineClip second = CopyOf(clip);
			double offset = playhead - clip.start;
			second.start = playhead;
			second.length = clip.length - offset;
			second.startOffset = clip.startOffset + offset * clip.speed;
			second.fadeIn.Length = 0.0;
			document.Edit("Split clip", () =>
			{
				clip.length = offset;
				clip.fadeOut.Length = 0.0;
				channel.clips.Add(second);
			});
		}

		private void MakeUnique(TimelineClip clip, TimelineChannel channel)
		{
			string folder = recorder.ClipFolder();
			string path = TimelineRecorder.UniquePath(folder, clip.clip.name, AnimationClip.Extension);
			AnimationClip copy = clip.clip.Clone();
			copy.Save(path);
			document.Edit("Make clip unique", () => clip.clip = AnimationClip.Load(path));
		}

		private void OpenInAnimationWindow(TimelineClip clip, TimelineChannel channel)
		{
			if (clip.clip != null && !string.IsNullOrEmpty(clip.clip.AssetPath))
			{
				editor.AnimationWindow.Show(clip.clip.AssetPath, TargetOf(channel));
			}
		}

		private void AddParameter()
		{
			string name = "Parameter";
			for (int i = 2; Asset.FindParameterByName(name) != null; i++)
			{
				name = "Parameter " + i;
			}

			var parameter = new AudioParameter { Id = TimelineAsset.NewId(), Name = name };
			document.Edit("Add parameter", () => Asset.parameters.Add(parameter));
		}

		// ---------------------------------------------------------------- keys

		/// <summary>A key with the object's value at the playhead (or another time), in the clip there; a new clip when there is none.</summary>
		private void KeyProperty(TimelineChannel channel, PropertyRow property, double? time = null)
		{
			GameObject target = TargetOf(channel);
			if (target == null)
			{
				return;
			}

			double at = time ?? (snap ? SnapToFrame(playhead) : playhead);
			document.Edit("Add key", () => recorder.KeyCurrentValue(channel, target, property.Path, property.TypeName, property.PropertyName, property.Kind, at));
		}

		/// <summary>A key at the playhead for every property the channel has keys for: the selected channel, else all of them.</summary>
		private void KeyAllAtPlayhead()
		{
			TimelineChannel only = selectionKind == SelectionKind.Channel ? Asset.FindChannel(selectedId) : null;
			double at = snap ? SnapToFrame(playhead) : playhead;
			document.Edit("Add keys", () =>
			{
				foreach (TimelineChannel channel in Asset.channels)
				{
					GameObject target = TargetOf(channel);
					if (target == null || (only != null && only != channel))
					{
						continue;
					}

					foreach (PropertyRow property in CollectProperties(channel))
					{
						recorder.KeyCurrentValue(channel, target, property.Path, property.TypeName, property.PropertyName, property.Kind, at);
					}
				}
			});
		}

		private void RemoveProperty(TimelineChannel channel, PropertyRow property)
		{
			document.Edit("Remove property", () =>
			{
				foreach (TimelineClip clip in channel.clips)
				{
					AnimationBinding binding = clip.clip?.FindBinding(property.Path, property.TypeName, property.PropertyName);
					if (binding != null)
					{
						document.TouchClip(clip.clip);
						clip.clip.bindings.Remove(binding);
					}
				}
			});
		}

		private static AnimationBinding BindingOf(AnimationClip clip, PropertyRow property)
		{
			return clip.FindBinding(property.Path, property.TypeName, property.PropertyName);
		}

		/// <summary>Moves a property's keys at a clip time (every channel of it) to another time.</summary>
		private bool MoveKeyTime(AnimationClip clip, PropertyRow property, float from, float to)
		{
			AnimationBinding binding = BindingOf(clip, property);
			if (binding == null)
			{
				return false;
			}

			foreach (FloatCurve curve in binding.channels)
			{
				int index = curve.IndexOf(from);
				if (index >= 0)
				{
					Keyframe key = curve.GetKey(index);
					key.time = to;
					curve.MoveKey(index, key);
					curve.RecalculateTangents();
				}
			}

			foreach (StringKey key in binding.stringKeys)
			{
				if (Math.Abs(key.time - from) < 1e-4f) key.time = to;
			}

			foreach (ReferenceKey key in binding.referenceKeys)
			{
				if (Math.Abs(key.time - from) < 1e-4f) key.time = to;
			}

			clip.MarkChanged();
			return true;
		}

		private void DeleteKey(TimelineClip clip, PropertyRow property, float time)
		{
			if (clip.clip == null)
			{
				return;
			}

			document.Edit("Delete key", () =>
			{
				document.TouchClip(clip.clip);
				AnimationBinding binding = BindingOf(clip.clip, property);
				if (binding == null)
				{
					return;
				}

				foreach (FloatCurve curve in binding.channels)
				{
					int index = curve.IndexOf(time);
					if (index >= 0)
					{
						curve.RemoveKey(index);
						curve.RecalculateTangents();
					}
				}

				binding.stringKeys.RemoveAll(k => Math.Abs(k.time - time) < 1e-4f);
				binding.referenceKeys.RemoveAll(k => Math.Abs(k.time - time) < 1e-4f);
				if (binding.IsEmpty)
				{
					clip.clip.bindings.Remove(binding);
				}
			});
		}

		#endregion
	}
}
