using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using LELEngine.Audio;
using LELEngine.Editor.Audio;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The timeline itself: the ruler, the logic track (transitions, markers, loop regions, drawn as the Audio Studio
	///     draws them), a row per channel with its object and its clips, and rows for the properties an expanded channel
	///     has keys for. Clips move, trim and fade with the mouse; overlapping clips of a channel share its lane and crossfade.
	/// </summary>
	internal sealed partial class TimelineWindow
	{
		#region PrivateFields

		private const float HeaderWidth = 290f;
		private const float RulerHeight = 24f;
		private const float LogicRowHeight = 20f;
		private const float LogicHeight = LogicRowHeight * 3f + 8f;
		private const float ChannelHeight = 52f;
		private const float PropertyHeight = 20f;
		private const float EdgeGrab = 6f;
		private const float FadeHandleSize = 8f;
		private const float KeyRadius = 4.5f;
		private const float SnapPixels = 7f;

		private enum SelectionKind
		{
			None,
			Channel,
			Clip,
			Marker
		}

		private enum RowKind
		{
			Logic,
			Channel,
			Property
		}

		private struct Row
		{
			public RowKind Kind;
			public TimelineChannel Channel;
			public int ChannelIndex;
			public PropertyRow Property;
			public float Y;
			public float Height;
		}

		/// <summary>A property a channel's clips have keys for.</summary>
		private sealed class PropertyRow
		{
			public string Key;
			public string Label;
			public string Path;
			public string TypeName;
			public string PropertyName;
			public AnimationValueKind Kind;
		}

		/// <summary>One drawn clip.</summary>
		private sealed class Piece
		{
			public TimelineClip Clip;
			public TimelineChannel Channel;
			public int Row;
			public int Rows = 1;
			public double FadeIn;
			public double FadeOut;
			public NVector2 Min;
			public NVector2 Max;
		}

		private struct LogicItem
		{
			public HitKind Kind;
			public string Id;
			public NVector2 Min;
			public NVector2 Max;
		}

		private struct KeyItem
		{
			public TimelineClip Clip;
			public PropertyRow Property;
			public float ClipTime;
			public NVector2 Center;
		}

		private enum HitKind
		{
			None,
			Clip,
			ClipStart,
			ClipEnd,
			FadeIn,
			FadeOut,
			Marker,
			MarkerEnd,
			Lane,
			Logic,
			PropertyLane,
			Key
		}

		private enum DragKind
		{
			None,
			MoveClip,
			TrimStart,
			TrimEnd,
			FadeIn,
			FadeOut,
			MoveMarker,
			ResizeMarker,
			MoveKey
		}

		private struct Hit
		{
			public HitKind Kind;
			public string Id;
			public TimelineChannel Channel;
			public PropertyRow Property;
			public Piece Piece;
			public KeyItem Key;
			public double Time;
		}

		private readonly List<Row> rows = new List<Row>();
		private readonly List<Piece> pieces = new List<Piece>();
		private readonly List<double> rowEnds = new List<double>();
		private readonly List<LogicItem> logicItems = new List<LogicItem>();
		private readonly List<KeyItem> keyItems = new List<KeyItem>();
		private readonly HashSet<string> expandedChannels = new HashSet<string>(StringComparer.Ordinal);
		private readonly List<double> keyTimes = new List<double>();
		private readonly Dictionary<AnimationClip, (int Version, List<double> Times)> keyTimeCache = new Dictionary<AnimationClip, (int, List<double>)>();
		private readonly Dictionary<string, (GameObject Target, BoundProperty Bound)> propertyBindings = new Dictionary<string, (GameObject, BoundProperty)>(StringComparer.Ordinal);
		private float pixelsPerSecond = 120f;
		private double scroll;
		private float bodyScroll;
		private bool snap = true;
		private bool followPlayhead = true;
		private float canvasLeft;
		private float canvasRight;
		private SelectionKind selectionKind;
		private string selectedId;
		private TimelineClip selectedKeyClip;
		private PropertyRow selectedKeyProperty;
		private float selectedKeyTime;
		private DragKind dragKind;
		private string dragId;
		private double dragStart0;
		private double dragLength0;
		private double dragOffset0;
		private float dragMouseX0;
		private float dragMouseY0;
		private bool dragChanged;
		private bool panning;
		private float panMouseX0;
		private double panScroll0;
		private Hit hoverHit;
		private Hit contextHit;
		private bool openContext;
		private string openMarkerPopup;
		private string openClipPopup;
		private TimelineClip clipboardClip;

		#endregion

		#region PrivateMethods

		private void ClearSelection()
		{
			selectionKind = SelectionKind.None;
			selectedId = null;
			selectedKeyClip = null;
			selectedKeyProperty = null;
		}

		private void Select(SelectionKind kind, string id)
		{
			selectionKind = kind;
			selectedId = id;
			selectedKeyClip = null;
			selectedKeyProperty = null;
		}

		private bool IsSelected(SelectionKind kind, string id)
		{
			return selectionKind == kind && selectedId == id;
		}

		private float ToX(double time)
		{
			return canvasLeft + (float)((time - scroll) * pixelsPerSecond);
		}

		private double FromX(float x)
		{
			return scroll + (x - canvasLeft) / pixelsPerSecond;
		}

		private void FrameAll()
		{
			if (document == null)
			{
				return;
			}

			double length = Math.Max(1.0, Asset.Duration);
			pixelsPerSecond = (float)Math.Clamp((canvasRight - canvasLeft - 40f) / length, 4.0, 4000.0);
			scroll = 0.0;
		}

		/// <summary>A time moved by the mouse: to the frame grid, and to the edges of clips and markers and the playhead when one is within a few pixels.</summary>
		private double SnapTime(double time, string except)
		{
			if (!snap)
			{
				return Math.Max(0.0, time);
			}

			double best = SnapToFrame(time);
			double range = SnapPixels / pixelsPerSecond;
			double bestDistance = range;
			void Consider(double candidate)
			{
				double distance = Math.Abs(candidate - time);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = candidate;
				}
			}

			Consider(playhead);
			foreach (TimelineChannel channel in Asset.channels)
			{
				foreach (TimelineClip clip in channel.clips)
				{
					if (clip.id != except)
					{
						Consider(clip.start);
						Consider(clip.End);
					}
				}
			}

			foreach (AudioMarker marker in Asset.markers)
			{
				if (marker.Id != except)
				{
					Consider(marker.Position);
					Consider(marker.End);
				}
			}

			return Math.Max(0.0, best);
		}

		/// <summary>A step in seconds at least <paramref name="minPixels" /> wide at the zoom.</summary>
		private static double NiceStep(float pixelsPerSecond, float minPixels)
		{
			double[] steps = { 0.001, 0.005, 0.01, 0.02, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300 };
			foreach (double step in steps)
			{
				if (step * pixelsPerSecond >= minPixels)
				{
					return step;
				}
			}

			return 600;
		}

		private static string FormatRulerTime(double seconds, double step)
		{
			int minutes = (int)Math.Floor(seconds / 60.0);
			double rest = seconds - minutes * 60.0;
			if (step >= 1.0)
			{
				return minutes + ":" + Math.Round(rest).ToString("00");
			}

			return minutes + ":" + rest.ToString(step >= 0.1 ? "00.0" : step >= 0.01 ? "00.00" : "00.000");
		}

		// ---------------------------------------------------------------- body

		private void DrawBody()
		{
			NVector2 avail = ImGui.GetContentRegionAvail();
			if (avail.X < 120f || avail.Y < 60f)
			{
				return;
			}

			NVector2 origin = ImGui.GetCursorScreenPos();
			canvasLeft = origin.X + HeaderWidth;
			canvasRight = origin.X + avail.X - 2f;
			BuildRows();
			float totalHeight = rows.Count > 0 ? rows[rows.Count - 1].Y + rows[rows.Count - 1].Height + 34f : 34f;
			float lanesHeight = Math.Max(20f, avail.Y - RulerHeight);
			DrawRuler(origin, avail.X);
			ImGui.SetCursorScreenPos(new NVector2(origin.X, origin.Y + RulerHeight));
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, NVector2.Zero);
			bool open = ImGui.BeginChild("##timeline_lanes", new NVector2(avail.X, lanesHeight), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
			ImGui.PopStyleVar();
			if (open)
			{
				bodyScroll = Math.Clamp(bodyScroll, 0f, Math.Max(0f, totalHeight - lanesHeight));
				NVector2 lanesOrigin = ImGui.GetCursorScreenPos() - new NVector2(0f, bodyScroll);
				ImDrawListPtr draw = ImGui.GetWindowDrawList();
				LayoutPieces(lanesOrigin);

				// Headers first: their widgets take the mouse before the canvas does.
				DrawHeaders(lanesOrigin, draw);
				ImGui.SetCursorScreenPos(new NVector2(canvasLeft, lanesOrigin.Y));
				EditorGui.InvisibleButton("##timeline_canvas", new NVector2(Math.Max(1f, canvasRight - canvasLeft), Math.Max(lanesHeight, totalHeight)), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
				bool canvasHovered = ImGui.IsItemHovered();
				HandleDrop(lanesOrigin);
				var clipMin = new NVector2(canvasLeft, origin.Y + RulerHeight);
				var clipMax = new NVector2(canvasRight, origin.Y + RulerHeight + lanesHeight);
				draw.PushClipRect(clipMin, clipMax, true);
				DrawLaneBackgrounds(lanesOrigin, draw);
				DrawGrid(lanesOrigin, draw, clipMax.Y);
				foreach (Piece piece in pieces)
				{
					DrawPiece(piece, draw);
				}

				DrawPropertyRows(lanesOrigin, draw);
				logicItems.Clear();
				DrawMarkerLines(lanesOrigin, draw, clipMax.Y);
				DrawLogic(lanesOrigin.Y + rows[0].Y, draw);
				DrawPlayhead(draw, clipMin.Y, clipMax.Y);
				draw.PopClipRect();
				HandleWheel(lanesHeight, totalHeight);
				HandleMouse(lanesOrigin, canvasHovered);
				DrawContextMenu();
				DrawEditPopups();
			}

			ImGui.EndChild();
			FinishPendingEdit();
		}

		private void BuildRows()
		{
			rows.Clear();
			float y = 0f;
			rows.Add(new Row { Kind = RowKind.Logic, Y = y, Height = LogicHeight, ChannelIndex = -1 });
			y += LogicHeight;
			for (int i = 0; i < Asset.channels.Count; i++)
			{
				TimelineChannel channel = Asset.channels[i];
				rows.Add(new Row { Kind = RowKind.Channel, Channel = channel, ChannelIndex = i, Y = y, Height = ChannelHeight });
				y += ChannelHeight;
				if (expandedChannels.Contains(channel.id))
				{
					foreach (PropertyRow property in CollectProperties(channel))
					{
						rows.Add(new Row { Kind = RowKind.Property, Channel = channel, ChannelIndex = i, Property = property, Y = y, Height = PropertyHeight });
						y += PropertyHeight;
					}
				}
			}
		}

		/// <summary>The properties the channel's clips have keys for: exactly what it overrides during playback.</summary>
		private static List<PropertyRow> CollectProperties(TimelineChannel channel)
		{
			var result = new List<PropertyRow>();
			foreach (TimelineClip clip in channel.clips)
			{
				if (clip.clip == null)
				{
					continue;
				}

				foreach (AnimationBinding binding in clip.clip.bindings)
				{
					if (binding.IsEmpty || result.Exists(r => r.Key == binding.TargetKey))
					{
						continue;
					}

					string component = string.IsNullOrEmpty(binding.type) ? "GameObject" : AnimationBinder.ShortTypeName(binding.type);
					result.Add(new PropertyRow
					{
						Key = binding.TargetKey,
						Label = (string.IsNullOrEmpty(binding.path) ? "" : binding.path + " : ") + component + "." + binding.property,
						Path = binding.path,
						TypeName = binding.type,
						PropertyName = binding.property,
						Kind = binding.kind
					});
				}
			}

			result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
			return result;
		}

		// ---------------------------------------------------------------- ruler

		private void DrawRuler(NVector2 origin, float width)
		{
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			var max = new NVector2(origin.X + width, origin.Y + RulerHeight);
			draw.AddRectFilled(origin, max, recorder.Recording ? StudioUi.Rgb(0.42f, 0.2f, 0.2f) : StudioUi.Ruler);
			draw.AddRectFilled(origin, new NVector2(canvasLeft - 1f, max.Y), StudioUi.Shade(StudioUi.TrackHeader, 0.8f));
			draw.AddText(origin + new NVector2(10f, 4f), StudioUi.TextDim, recorder.Recording ? "Recording" : "Time");
			draw.PushClipRect(new NVector2(canvasLeft, origin.Y), max, true);
			double step = NiceStep(pixelsPerSecond, 64f);
			double from = Math.Max(0.0, FromX(canvasLeft));
			double to = FromX(canvasRight);
			double first = Math.Floor(from / step) * step;
			int guard = 0;
			for (double v = first; v <= to + 1e-9 && guard++ < 2000; v += step)
			{
				float x = ToX(v);
				draw.AddLine(new NVector2(x, max.Y - 10f), new NVector2(x, max.Y), StudioUi.RulerTick);
				draw.AddText(new NVector2(x + 3f, origin.Y + 2f), StudioUi.RulerText, FormatRulerTime(v, step));
				for (int k = 1; k < 4; k++)
				{
					float minor = ToX(v + step * k / 4.0);
					draw.AddLine(new NVector2(minor, max.Y - 5f), new NVector2(minor, max.Y), StudioUi.RulerTick);
				}
			}

			float head = ToX(playhead);
			draw.AddTriangleFilled(new NVector2(head - 6f, max.Y - 10f), new NVector2(head + 6f, max.Y - 10f), new NVector2(head, max.Y), PlayheadColor);
			draw.PopClipRect();
			ImGui.SetCursorScreenPos(new NVector2(canvasLeft, origin.Y));
			EditorGui.InvisibleButton("##timeline_ruler", new NVector2(Math.Max(1f, canvasRight - canvasLeft), RulerHeight));
			if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				double time = Math.Max(0.0, FromX(ImGui.GetMousePos().X));
				SeekTo(snap ? SnapToFrame(time) : time);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Click or drag: the playhead. In edit mode this previews on the objects (Stop puts them back).");
			}
		}

		private uint PlayheadColor => editor.IsPlaying ? StudioUi.LivePlayhead : recorder.Recording ? StudioUi.Red : StudioUi.Playhead;

		private void DrawPlayhead(ImDrawListPtr draw, float top, float bottom)
		{
			float x = ToX(playhead);
			TimelinePlayer shown = Shown;
			if (followPlayhead && shown != null && shown.IsPlaying && !shown.Paused && (x > canvasRight - 20f || x < canvasLeft))
			{
				scroll = Math.Max(0.0, playhead - 0.1 * (canvasRight - canvasLeft) / pixelsPerSecond);
				x = ToX(playhead);
			}

			draw.AddLine(new NVector2(x, top), new NVector2(x, bottom), PlayheadColor, 1.5f);
		}

		// ---------------------------------------------------------------- headers

		private void DrawHeaders(NVector2 lanesOrigin, ImDrawListPtr draw)
		{
			foreach (Row row in rows)
			{
				var min = new NVector2(lanesOrigin.X, lanesOrigin.Y + row.Y);
				var max = new NVector2(canvasLeft - 1f, min.Y + row.Height);
				ImGui.PushID(row.Kind + (row.Channel?.id ?? "") + (row.Property?.Key ?? ""));
				switch (row.Kind)
				{
					case RowKind.Logic:
						draw.AddRectFilled(min, max, StudioUi.Shade(StudioUi.TrackHeader, 0.8f));
						draw.AddText(min + new NVector2(10f, 5f), StudioUi.Text, "Logic");
						draw.AddText(min + new NVector2(10f, 24f), StudioUi.TextDim, "regions, transitions, markers");
						draw.AddText(min + new NVector2(10f, 43f), StudioUi.TextDim, "right click the lane: add");
						break;
					case RowKind.Channel:
						DrawChannelHeader(row.Channel, row.ChannelIndex, min, max, draw);
						break;
					case RowKind.Property:
						DrawPropertyHeader(row.Channel, row.Property, min, max, draw);
						break;
				}

				draw.AddLine(new NVector2(min.X, max.Y - 0.5f), new NVector2(max.X, max.Y - 0.5f), StudioUi.LaneLine);
				ImGui.PopID();
			}

			float addY = lanesOrigin.Y + (rows.Count > 0 ? rows[rows.Count - 1].Y + rows[rows.Count - 1].Height : 0f) + 6f;
			ImGui.SetCursorScreenPos(new NVector2(lanesOrigin.X + 8f, addY));
			ImGui.BeginDisabled(host == null || editor.Selected == null);
			if (EditorGui.SmallButton("+ Channel From Selection"))
			{
				AddChannelFromSelection();
			}

			ImGui.EndDisabled();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip("A channel per selected object. Or drag objects from the Hierarchy onto the timeline.");
			}
		}

		private void DrawChannelHeader(TimelineChannel channel, int index, NVector2 min, NVector2 max, ImDrawListPtr draw)
		{
			bool selected = IsSelected(SelectionKind.Channel, channel.id);
			draw.AddRectFilled(min, max, selected ? StudioUi.TrackHeaderSelected : StudioUi.TrackHeader);
			draw.AddRectFilled(min, new NVector2(min.X + 4f, max.Y), StudioUi.Rgb(channel.color.R, channel.color.G, channel.color.B));
			GameObject target = TargetOf(channel);

			// The arrow: the properties it overrides.
			bool expanded = expandedChannels.Contains(channel.id);
			ImGui.SetCursorScreenPos(min + new NVector2(8f, 4f));
			if (EditorGui.ArrowButton("##expand", expanded ? ImGuiDir.Down : ImGuiDir.Right))
			{
				if (expanded) expandedChannels.Remove(channel.id);
				else expandedChannels.Add(channel.id);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Show the properties this channel has keys for: the only ones it overrides");
			}

			// The name: the object's.
			const float togglesWidth = 3f * 22f + 4f;
			float nameX = min.X + 32f;
			float togglesX = max.X - togglesWidth;
			ImGui.SetCursorScreenPos(new NVector2(nameX, min.Y + 4f));
			TimelineBinding binding = host?.FindBinding(channel.id);
			bool missing = target == null && binding != null && binding.targetId != 0;
			string shownName = target != null ? target.Name : missing ? (binding.targetName ?? channel.name) + " (missing)" : channel.name;
			if (missing)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Error);
			}

			if (EditorGui.Selectable(shownName + "##channel", selected, ImGuiSelectableFlags.None, new NVector2(Math.Max(20f, togglesX - nameX - 6f), 0f)))
			{
				Select(SelectionKind.Channel, channel.id);
			}

			if (missing)
			{
				ImGui.PopStyleColor();
			}

			if (ImGui.BeginDragDropSource())
			{
				draggedChannelId = channel.id;
				unsafe
				{
					ImGui.SetDragDropPayload(ChannelPayload, null, 0);
				}

				ImGui.TextUnformatted(shownName);
				ImGui.EndDragDropSource();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ChannelPayload).IsNull && draggedChannelId != null && draggedChannelId != channel.id)
				{
					TimelineChannel moved = Asset.FindChannel(draggedChannelId);
					draggedChannelId = null;
					if (moved != null)
					{
						document.Edit("Reorder channels", () =>
						{
							Asset.channels.Remove(moved);
							Asset.channels.Insert(Asset.channels.IndexOf(channel), moved);
						});
					}
				}

				ImGui.EndDragDropTarget();
			}

			if (EditorGui.BeginPopupContextItem("##channel_menu"))
			{
				DrawChannelMenu(channel, target);
				ImGui.EndPopup();
			}

			// Solo, mute, arm.
			const float toggle = 20f;
			ImGui.SetCursorScreenPos(new NVector2(togglesX, min.Y + 5f));
			if (StudioUi.TextToggle("##solo", "S", channel.solo, StudioUi.Yellow, new NVector2(toggle, 18f), "Solo: only soloed channels write (auditioning: game builds play every channel)"))
			{
				bool solo = channel.solo;
				document.Edit("Toggle solo", () => channel.solo = !solo);
			}

			ImGui.SetCursorScreenPos(new NVector2(togglesX + 22f, min.Y + 5f));
			if (StudioUi.TextToggle("##mute", "M", channel.mute, StudioUi.LoopBlue, new NVector2(toggle, 18f), "Mute: the channel writes nothing"))
			{
				bool mute = channel.mute;
				document.Edit("Toggle mute", () => channel.mute = !mute);
			}

			ImGui.SetCursorScreenPos(new NVector2(togglesX + 44f, min.Y + 5f));
			bool armed = recorder.IsArmed(channel.id);
			if (StudioUi.TextToggle("##arm", "R", armed, StudioUi.Red, new NVector2(toggle, 18f), "Arm for recording: while Record is on, what changes on this object becomes keys"))
			{
				recorder.SetArmed(channel.id, !armed);
			}

			// The object: bound by its id, any object of the scene.
			ImGui.SetCursorScreenPos(new NVector2(nameX, min.Y + 28f));
			ImGui.BeginDisabled(host == null);
			ImGui.SetNextItemWidth(Math.Max(40f, max.X - nameX - 18f));
			object value = target;
			TestAnchors.NameNextElement("Channel " + index);
			if (ObjectField.Draw(typeof(GameObject), ref value, context))
			{
				BindChannel(channel, value as GameObject);
			}

			ImGui.EndDisabled();

			// How much it plays now.
			float level = Shown?.ChannelLevel(channel.id) ?? 0f;
			var meterMin = new NVector2(max.X - 11f, min.Y + 28f);
			var meterMax = new NVector2(max.X - 4f, max.Y - 4f);
			draw.AddRectFilled(meterMin, meterMax, StudioUi.Rgb(0.08f, 0.08f, 0.085f));
			if (level > 0f)
			{
				float height = (meterMax.Y - meterMin.Y) * Math.Min(level, 1f);
				draw.AddRectFilled(new NVector2(meterMin.X + 1f, meterMax.Y - height), new NVector2(meterMax.X - 1f, meterMax.Y), StudioUi.Rgb(channel.color.R, channel.color.G, channel.color.B));
			}
		}

		private void DrawPropertyHeader(TimelineChannel channel, PropertyRow property, NVector2 min, NVector2 max, ImDrawListPtr draw)
		{
			draw.AddRectFilled(min, max, StudioUi.Shade(StudioUi.TrackHeader, 0.85f));
			draw.AddRectFilled(min, new NVector2(min.X + 4f, max.Y), StudioUi.Shade(StudioUi.Rgb(channel.color.R, channel.color.G, channel.color.B), 0.6f));
			string value = PropertyValueText(channel, property);
			float valueWidth = ImGui.CalcTextSize(value).X;
			draw.PushClipRect(min, new NVector2(max.X - valueWidth - 10f, max.Y), true);
			draw.AddText(min + new NVector2(32f, 2f), StudioUi.Text, property.Label);
			draw.PopClipRect();
			draw.AddText(new NVector2(max.X - valueWidth - 6f, min.Y + 2f), StudioUi.TextDim, value);
			ImGui.SetCursorScreenPos(min);
			EditorGui.InvisibleButton("##property", max - min, ImGuiButtonFlags.MouseButtonRight);
			if (EditorGui.BeginPopupContextItem("##property_menu"))
			{
				ImGui.TextDisabled(property.Label);
				ImGui.Separator();
				if (EditorMenu.Item("Key At Playhead", "K")) KeyProperty(channel, property);
				if (EditorMenu.Item("Remove Property")) RemoveProperty(channel, property);
				ImGui.EndPopup();
			}
		}

		/// <summary>The value the object's property has now.</summary>
		private string PropertyValueText(TimelineChannel channel, PropertyRow property)
		{
			GameObject target = TargetOf(channel);
			string bindingKey = channel.id + "|" + property.Key;
			if (!propertyBindings.TryGetValue(bindingKey, out (GameObject Target, BoundProperty Bound) cached) || cached.Target != target || (cached.Bound != null && !cached.Bound.IsAlive))
			{
				cached = (target, target != null ? AnimationBinder.Bind(target, new AnimationBinding { path = property.Path, type = property.TypeName, property = property.PropertyName, kind = property.Kind }) : null);
				propertyBindings[bindingKey] = cached;
			}

			BoundProperty bound = cached.Bound;
			if (bound == null)
			{
				return target != null ? "not found" : "";
			}

			if (property.Kind == AnimationValueKind.String || property.Kind == AnimationValueKind.Reference)
			{
				return bound.ReadDiscrete()?.ToString() ?? "null";
			}

			var values = new float[4];
			bound.ReadChannels(values, 0);
			switch (bound.Kind)
			{
				case AnimationValueKind.Quaternion:
				{
					Vector3 euler = QuaternionHelper.ToEulerDegrees(new Quaternion(values[0], values[1], values[2], values[3]));
					return euler.X.ToString("0.#") + ", " + euler.Y.ToString("0.#") + ", " + euler.Z.ToString("0.#");
				}
				case AnimationValueKind.Bool:
					return values[0] > 0.5f ? "true" : "false";
				case AnimationValueKind.Int:
				case AnimationValueKind.Enum:
					return values[0].ToString("0");
			}

			int channels = AnimationBinding.ChannelCountOf(bound.Kind);
			var text = new System.Text.StringBuilder();
			for (int c = 0; c < channels; c++)
			{
				text.Append(c > 0 ? ", " : "").Append(values[c].ToString("0.##"));
			}

			return text.ToString();
		}

		// ---------------------------------------------------------------- lanes

		private void DrawLaneBackgrounds(NVector2 lanesOrigin, ImDrawListPtr draw)
		{
			foreach (Row row in rows)
			{
				var min = new NVector2(canvasLeft, lanesOrigin.Y + row.Y);
				var max = new NVector2(canvasRight, min.Y + row.Height);
				uint background;
				switch (row.Kind)
				{
					case RowKind.Logic:
						background = StudioUi.LogicLane;
						break;
					case RowKind.Property:
						background = StudioUi.Shade(StudioUi.LaneAlternate, 0.85f);
						break;
					default:
						background = IsSelected(SelectionKind.Channel, row.Channel.id) ? StudioUi.LaneSelected : row.ChannelIndex % 2 == 0 ? StudioUi.Lane : StudioUi.LaneAlternate;
						break;
				}

				draw.AddRectFilled(min, max, background);
				draw.AddLine(new NVector2(min.X, max.Y - 0.5f), new NVector2(max.X, max.Y - 0.5f), StudioUi.LaneLine);
			}
		}

		private void DrawGrid(NVector2 lanesOrigin, ImDrawListPtr draw, float bottom)
		{
			float top = lanesOrigin.Y;
			double step = NiceStep(pixelsPerSecond, 64f);
			double from = Math.Max(0.0, FromX(canvasLeft));
			double to = FromX(canvasRight);
			double first = Math.Floor(from / step) * step;
			int guard = 0;
			for (double v = first; v <= to + 1e-9 && guard++ < 2000; v += step)
			{
				float x = ToX(v);
				draw.AddLine(new NVector2(x, top), new NVector2(x, bottom), StudioUi.GridBar);
				if (step * pixelsPerSecond >= 32f)
				{
					for (int k = 1; k < 4; k++)
					{
						float minor = ToX(v + step * k / 4.0);
						draw.AddLine(new NVector2(minor, top), new NVector2(minor, bottom), StudioUi.GridBeat);
					}
				}
			}

			float zero = ToX(0.0);
			draw.AddLine(new NVector2(zero, top), new NVector2(zero, bottom), StudioUi.Rgba(1f, 1f, 1f, 0.22f));
		}

		/// <summary>The clips of every channel, sharing the lane in rows where they overlap.</summary>
		private void LayoutPieces(NVector2 lanesOrigin)
		{
			pieces.Clear();
			foreach (Row row in rows)
			{
				if (row.Kind != RowKind.Channel)
				{
					continue;
				}

				int first = pieces.Count;
				foreach (TimelineClip clip in row.Channel.clips)
				{
					row.Channel.GetFades(clip, out double fadeIn, out double fadeOut);
					pieces.Add(new Piece { Clip = clip, Channel = row.Channel, FadeIn = fadeIn, FadeOut = fadeOut });
				}

				int count = pieces.Count - first;
				StackPieces(first, count);
				float top = lanesOrigin.Y + row.Y + 3f;
				float height = row.Height - 6f;
				for (int i = first; i < first + count; i++)
				{
					Piece piece = pieces[i];
					float rowHeight = height / piece.Rows;
					float y = top + piece.Row * rowHeight;
					float x0 = ToX(piece.Clip.start);
					float x1 = Math.Max(ToX(piece.Clip.End), x0 + 3f);
					piece.Min = new NVector2(x0, y + (piece.Row > 0 ? 1f : 0f));
					piece.Max = new NVector2(x1, y + rowHeight - (piece.Row < piece.Rows - 1 ? 1f : 0f));
				}
			}
		}

		private void StackPieces(int first, int count)
		{
			if (count <= 0)
			{
				return;
			}

			pieces.Sort(first, count, Comparer<Piece>.Create((a, b) =>
			{
				int order = a.Clip.start.CompareTo(b.Clip.start);
				return order != 0 ? order : b.Clip.End.CompareTo(a.Clip.End);
			}));
			rowEnds.Clear();
			int clusterStart = first;
			double clusterEnd = double.NegativeInfinity;
			for (int i = first; i < first + count; i++)
			{
				Piece piece = pieces[i];
				if (i > clusterStart && piece.Clip.start >= clusterEnd - 1e-9)
				{
					for (int j = clusterStart; j < i; j++)
					{
						pieces[j].Rows = rowEnds.Count;
					}

					rowEnds.Clear();
					clusterStart = i;
				}

				int row = -1;
				for (int r = 0; r < rowEnds.Count; r++)
				{
					if (rowEnds[r] <= piece.Clip.start + 1e-9)
					{
						row = r;
						break;
					}
				}

				if (row < 0)
				{
					row = rowEnds.Count;
					rowEnds.Add(piece.Clip.End);
				}
				else
				{
					rowEnds[row] = piece.Clip.End;
				}

				piece.Row = row;
				clusterEnd = Math.Max(clusterEnd, piece.Clip.End);
			}

			for (int j = clusterStart; j < first + count; j++)
			{
				pieces[j].Rows = Math.Max(1, rowEnds.Count);
			}
		}

		private void DrawPiece(Piece piece, ImDrawListPtr draw)
		{
			NVector2 min = piece.Min;
			NVector2 max = piece.Max;
			if (max.X < canvasLeft || min.X > canvasRight)
			{
				return;
			}

			TimelineClip clip = piece.Clip;
			bool selected = IsSelected(SelectionKind.Clip, clip.id);
			uint body = StudioUi.Rgb(piece.Channel.color.R, piece.Channel.color.G, piece.Channel.color.B);
			if (clip.clip == null)
			{
				body = StudioUi.Rgb(0.55f, 0.28f, 0.28f);
			}
			else if (piece.Channel.mute)
			{
				body = StudioUi.Shade(body, 0.5f);
			}

			if (selected)
			{
				body = StudioUi.Shade(body, 1.14f);
			}

			float height = max.Y - min.Y;
			float headerHeight = Math.Min(height, 15f);
			draw.AddRectFilled(min, max, StudioUi.Shade(body, 0.8f), 3f);
			draw.AddRectFilled(min, new NVector2(max.X, min.Y + headerHeight), StudioUi.Shade(body, 0.6f), 3f, height > headerHeight ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll);
			draw.PushClipRect(min, max, true);
			if (clip.clip != null && height > headerHeight + 6f)
			{
				DrawClipContent(clip, new NVector2(min.X, min.Y + headerHeight + 1f), new NVector2(max.X, max.Y - 1f), StudioUi.Shade(body, 0.35f), draw);
			}

			float bottom = max.Y;
			if (piece.FadeIn > 0.0)
			{
				StudioUi.Fade(draw, min.X, ToX(clip.start + piece.FadeIn), min.Y, bottom, clip.fadeIn.Shape, true, StudioUi.Rgba(1f, 1f, 1f, 0.9f), StudioUi.Rgba(0f, 0f, 0f, 0.32f));
			}

			if (piece.FadeOut > 0.0)
			{
				StudioUi.Fade(draw, ToX(clip.End - piece.FadeOut), max.X, min.Y, bottom, clip.fadeOut.Shape, false, StudioUi.Rgba(1f, 1f, 1f, 0.9f), StudioUi.Rgba(0f, 0f, 0f, 0.32f));
			}

			string flags = (clip.loop ? " L" : "") + (clip.conditions.Count > 0 ? " ?" : "") + (Math.Abs(clip.speed - 1f) > 1e-3f ? " x" + clip.speed.ToString("0.##") : "");
			float textY = min.Y + (headerHeight - ImGui.GetTextLineHeight()) * 0.5f;
			draw.AddText(new NVector2(Math.Max(min.X, canvasLeft) + 5f, textY), StudioUi.Rgba(1f, 1f, 1f, 0.95f), clip.DisplayName);
			if (flags.Length > 0)
			{
				float flagsWidth = ImGui.CalcTextSize(flags).X;
				draw.AddText(new NVector2(Math.Max(min.X + 5f, max.X - flagsWidth - 4f), textY), StudioUi.Rgba(1f, 1f, 1f, 0.7f), flags);
			}

			draw.PopClipRect();
			float level = Shown?.ClipLevel(clip.id) ?? 0f;
			draw.AddRect(min, max, selected ? StudioUi.Selected : level > 0f ? StudioUi.WithAlpha(StudioUi.Selected, 0.55f) : StudioUi.Outline, 3f, ImDrawFlags.None, selected ? 2f : 1f);

			// Fade handles at the top corners, where the fades end.
			if (selected || hoverHit.Piece == piece)
			{
				float inX = Math.Clamp(ToX(clip.start + clip.fadeIn.Length), min.X + 4f, max.X - 4f);
				float outX = Math.Clamp(ToX(clip.End - clip.fadeOut.Length), min.X + 4f, max.X - 4f);
				StudioUi.Handle(draw, new NVector2(inX, min.Y + 4f), FadeHandleSize, StudioUi.Selected, hoverHit.Kind == HitKind.FadeIn && hoverHit.Piece == piece);
				StudioUi.Handle(draw, new NVector2(outX, min.Y + 4f), FadeHandleSize, StudioUi.Selected, hoverHit.Kind == HitKind.FadeOut && hoverHit.Piece == piece);
			}
		}

		/// <summary>What an audio clip's waveform is to a sound: the clip's curves over the region, and a tick at every key.</summary>
		private void DrawClipContent(TimelineClip clip, NVector2 min, NVector2 max, uint color, ImDrawListPtr draw)
		{
			AnimationClip animation = clip.clip;
			double clipLength = animation.length;
			if (clipLength <= 1e-6)
			{
				clipLength = 1.0;
			}

			float left = Math.Max(min.X, canvasLeft);
			float right = Math.Min(max.X, canvasRight);
			float height = max.Y - min.Y;

			// The first property's channels, each normalized on its own.
			int drawn = 0;
			foreach (AnimationBinding binding in animation.bindings)
			{
				if (drawn >= 4)
				{
					break;
				}

				for (int c = 0; c < binding.channels.Count && drawn < 4; c++)
				{
					FloatCurve curve = binding.channels[c];
					if (curve.Length < 2)
					{
						continue;
					}

					float low = float.MaxValue;
					float high = float.MinValue;
					foreach (float value in curve.Values)
					{
						low = Math.Min(low, value);
						high = Math.Max(high, value);
					}

					if (high - low < 1e-5f)
					{
						continue;
					}

					drawn++;
					NVector2 previous = default;
					bool started = false;
					for (float x = left; x <= right + 3f; x += 3f)
					{
						double time = clip.ClipTimeAt(FromX(x));
						float v = (curve.Evaluate((float)time) - low) / (high - low);
						var point = new NVector2(x, max.Y - 2f - v * (height - 4f));
						if (started)
						{
							draw.AddLine(previous, point, color, 1f);
						}

						previous = point;
						started = true;
					}
				}
			}

			// Key ticks along the bottom edge.
			uint tick = StudioUi.Rgba(1f, 1f, 1f, 0.55f);
			foreach (double keyTime in KeyTimesOf(animation))
			{
				foreach (double at in PlacesOf(clip, keyTime, clipLength))
				{
					float x = ToX(at);
					draw.AddLine(new NVector2(x, max.Y - 4f), new NVector2(x, max.Y), tick);
				}
			}

			// Where the loop starts over.
			if (clip.loop && animation.length > 1e-3f)
			{
				double cycle = animation.length / Math.Max(clip.speed, 1e-3f);
				double firstEnd = clip.TimelineTimeOf(animation.length);
				for (double at = firstEnd; at < clip.End && cycle * pixelsPerSecond > 4.0; at += cycle)
				{
					float x = ToX(at);
					draw.AddLine(new NVector2(x, min.Y), new NVector2(x, max.Y), StudioUi.Rgba(1f, 1f, 1f, 0.3f));
				}
			}
		}

		/// <summary>Every time a clip has a key at (each once, sorted); kept until the clip changes.</summary>
		private List<double> KeyTimesOf(AnimationClip clip)
		{
			if (keyTimeCache.TryGetValue(clip, out (int Version, List<double> Times) cached) && cached.Version == clip.Version)
			{
				return cached.Times;
			}

			var times = new List<double>();
			foreach (AnimationBinding binding in clip.bindings)
			{
				CollectKeyTimes(binding, times);
			}

			SortUnique(times);
			keyTimeCache[clip] = (clip.Version, times);
			return times;
		}

		private static void CollectKeyTimes(AnimationBinding binding, List<double> times)
		{
			foreach (FloatCurve curve in binding.channels)
			{
				foreach (float time in curve.Times)
				{
					times.Add(time);
				}
			}

			foreach (StringKey key in binding.stringKeys)
			{
				times.Add(key.time);
			}

			foreach (ReferenceKey key in binding.referenceKeys)
			{
				times.Add(key.time);
			}
		}

		private static void SortUnique(List<double> times)
		{
			times.Sort();
			int count = 0;
			for (int i = 0; i < times.Count; i++)
			{
				if (count == 0 || times[i] - times[count - 1] >= 1e-4)
				{
					times[count++] = times[i];
				}
			}

			times.RemoveRange(count, times.Count - count);
		}

		/// <summary>The times of the timeline a clip time plays at inside the region (every pass of a loop).</summary>
		private static IEnumerable<double> PlacesOf(TimelineClip clip, double clipTime, double clipLength)
		{
			double at = clip.TimelineTimeOf(clipTime);
			double cycle = clipLength / Math.Max(clip.speed, 1e-3f);
			if (clip.loop && cycle > 1e-3)
			{
				at -= Math.Floor((at - clip.start) / cycle) * cycle;
				for (int guard = 0; at < clip.End && guard < 256; at += cycle, guard++)
				{
					yield return at;
				}
			}
			else if (at >= clip.start - 1e-6 && at <= clip.End + 1e-6)
			{
				yield return at;
			}
		}

		/// <summary>The keys of an expanded channel's properties as diamonds on their rows.</summary>
		private void DrawPropertyRows(NVector2 lanesOrigin, ImDrawListPtr draw)
		{
			keyItems.Clear();
			foreach (Row row in rows)
			{
				if (row.Kind != RowKind.Property)
				{
					continue;
				}

				float y = lanesOrigin.Y + row.Y + row.Height * 0.5f;
				uint color = StudioUi.Rgb(row.Channel.color.R, row.Channel.color.G, row.Channel.color.B);
				foreach (TimelineClip clip in row.Channel.clips)
				{
					AnimationBinding binding = clip.clip?.FindBinding(row.Property.Path, row.Property.TypeName, row.Property.PropertyName);
					if (binding == null)
					{
						continue;
					}

					// The region's extent on the row.
					draw.AddRectFilled(new NVector2(ToX(clip.start), y - row.Height * 0.5f + 2f), new NVector2(ToX(clip.End), y + row.Height * 0.5f - 2f), StudioUi.WithAlpha(color, 0.18f));
					keyTimes.Clear();
					CollectKeyTimes(binding, keyTimes);
					SortUnique(keyTimes);
					double clipLength = clip.clip.length > 1e-6 ? clip.clip.length : 1.0;
					foreach (double keyTime in keyTimes)
					{
						bool firstPass = true;
						foreach (double at in PlacesOf(clip, keyTime, clipLength))
						{
							float x = ToX(at);
							if (x < canvasLeft - 8f || x > canvasRight + 8f)
							{
								firstPass = false;
								continue;
							}

							bool selected = selectedKeyClip == clip && selectedKeyProperty?.Key == row.Property.Key && Math.Abs(selectedKeyTime - keyTime) < 1e-4;
							uint fill = selected ? StudioUi.Yellow : firstPass ? StudioUi.Selected : StudioUi.Rgba(1f, 1f, 1f, 0.4f);
							var center = new NVector2(x, y);
							draw.AddQuadFilled(center + new NVector2(0f, -KeyRadius), center + new NVector2(KeyRadius, 0f), center + new NVector2(0f, KeyRadius), center + new NVector2(-KeyRadius, 0f), fill);
							if (firstPass)
							{
								keyItems.Add(new KeyItem { Clip = clip, Property = row.Property, ClipTime = (float)keyTime, Center = center });
							}

							firstPass = false;
						}
					}
				}
			}
		}

		// ---------------------------------------------------------------- logic track

		private static float LogicRowTop(float laneY, int row)
		{
			return laneY + 3f + row * (LogicRowHeight + 1f);
		}

		private void DrawLogic(float laneY, ImDrawListPtr draw)
		{
			foreach (AudioMarker marker in Asset.markers)
			{
				bool selected = IsSelected(SelectionKind.Marker, marker.Id);
				float x0 = ToX(marker.Position);
				float x1 = ToX(marker.End);
				switch (marker)
				{
					case TransitionRegion region:
					{
						float top = LogicRowTop(laneY, 0);
						var min = new NVector2(x0, top);
						var max = new NVector2(Math.Max(x1, x0 + 4f), top + LogicRowHeight);
						StudioUi.Chevrons(draw, min, max, StudioUi.Green, StudioUi.GreenStripe);
						draw.AddRect(min, max, selected ? StudioUi.Selected : StudioUi.Outline, 2f, ImDrawFlags.None, selected ? 2f : 1f);
						draw.PushClipRect(min, max, true);
						draw.AddText(new NVector2(min.X + 6f, top + 2f), StudioUi.GreenText, TransitionText(region));
						draw.PopClipRect();
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = min, Max = max });
						logicItems.Add(new LogicItem { Kind = HitKind.MarkerEnd, Id = marker.Id, Min = new NVector2(max.X - EdgeGrab, top), Max = new NVector2(max.X + 2f, max.Y) });
						break;
					}
					case TransitionMarker transition:
					{
						float top = LogicRowTop(laneY, 0);
						string text = TransitionText(transition);
						var min = new NVector2(x0, top);
						var max = new NVector2(x0 + ImGui.CalcTextSize(text).X + 22f, top + LogicRowHeight);
						draw.AddRectFilled(min, max, StudioUi.Green, 2f);
						draw.AddTriangleFilled(new NVector2(max.X - 11f, top + 5f), new NVector2(max.X - 11f, top + LogicRowHeight - 5f), new NVector2(max.X - 4f, top + LogicRowHeight * 0.5f), StudioUi.GreenText);
						draw.AddRect(min, max, selected ? StudioUi.Selected : StudioUi.Outline, 2f, ImDrawFlags.None, selected ? 2f : 1f);
						draw.AddText(new NVector2(x0 + 5f, top + 2f), StudioUi.GreenText, text);
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(x0 - 3f, top), Max = max });
						break;
					}
					case DestinationMarker destination:
						AddFlag(marker, x0, laneY, destination.Name ?? "", StudioUi.Flag, selected, draw);
						break;
					case TempoMarker tempo:
						AddFlag(marker, x0, laneY, $"{tempo.Bpm:0.#}  {tempo.BeatsPerBar}/{tempo.BeatUnit}", StudioUi.Tempo, selected, draw);
						break;
					case SustainPoint sustain:
						AddFlag(marker, x0, laneY, "S  " + (sustain.Name ?? ""), StudioUi.Sustain, selected, draw);
						break;
					case LoopRegion loop:
					{
						float top = LogicRowTop(laneY, 2);
						var min = new NVector2(x0, top + 2f);
						var max = new NVector2(Math.Max(x1, x0 + 4f), top + LogicRowHeight - 2f);
						draw.AddRectFilled(min, max, StudioUi.LoopBlue, 2f);
						draw.AddRectFilled(new NVector2(min.X, max.Y - 3f), max, StudioUi.LoopBlueStripe, 2f);
						draw.AddRect(min, max, selected ? StudioUi.Selected : StudioUi.Outline, 2f, ImDrawFlags.None, selected ? 2f : 1f);
						draw.AddTriangleFilled(new NVector2(min.X + 3f, min.Y + 2f), new NVector2(min.X + 3f, max.Y - 2f), new NVector2(min.X + 9f, (min.Y + max.Y) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.85f));
						draw.AddTriangleFilled(new NVector2(max.X - 3f, min.Y + 2f), new NVector2(max.X - 3f, max.Y - 2f), new NVector2(max.X - 9f, (min.Y + max.Y) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.85f));
						draw.PushClipRect(min + new NVector2(12f, 0f), max - new NVector2(12f, 0f), true);
						string text = (loop.Name ?? "Loop") + (loop.Conditions.Count > 0 ? "  ?" : "") + (loop.MaxLoops > 0 ? "  x" + loop.MaxLoops : "");
						draw.AddText(new NVector2(min.X + 13f, min.Y + (max.Y - min.Y - ImGui.GetTextLineHeight()) * 0.5f), StudioUi.Rgba(1f, 1f, 1f, 0.95f), text);
						draw.PopClipRect();
						logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(min.X, top), Max = new NVector2(max.X, top + LogicRowHeight) });
						logicItems.Add(new LogicItem { Kind = HitKind.MarkerEnd, Id = marker.Id, Min = new NVector2(max.X - EdgeGrab, top), Max = new NVector2(max.X + 2f, top + LogicRowHeight) });
						break;
					}
				}
			}
		}

		private void AddFlag(AudioMarker marker, float x, float laneY, string text, uint color, bool selected, ImDrawListPtr draw)
		{
			float top = LogicRowTop(laneY, 1);
			float right = StudioUi.FlagLabel(draw, x, top, LogicRowHeight, text, color, StudioUi.FlagText, selected);
			logicItems.Add(new LogicItem { Kind = HitKind.Marker, Id = marker.Id, Min = new NVector2(x - 3f, top), Max = new NVector2(right, top + LogicRowHeight) });
		}

		private string TransitionText(TransitionMarker transition)
		{
			string quantization = transition is TransitionRegion region ? QuantizationShort(region.Quantization) : "";
			return "To " + DestinationName(transition.DestinationId) + (transition.Conditions.Count > 0 ? "  ?" : "") + (transition.Crossfade > 0.0 ? "  " + transition.Crossfade.ToString("0.##") + " s" : "") + (quantization.Length > 0 ? "  [" + quantization + "]" : "");
		}

		private string DestinationName(string destinationId)
		{
			if (destinationId == TransitionMarker.EndDestination)
			{
				return "End";
			}

			AudioMarker destination = Asset.FindMarker(destinationId);
			return destination != null ? destination.Name ?? "marker" : "(none)";
		}

		private static string QuantizationShort(QuantizationInterval interval)
		{
			switch (interval)
			{
				case QuantizationInterval.QuarterBeat: return "1/4";
				case QuantizationInterval.HalfBeat: return "1/2";
				case QuantizationInterval.Beat: return "1 beat";
				case QuantizationInterval.TwoBeats: return "2 beats";
				case QuantizationInterval.Bar: return "1 bar";
				case QuantizationInterval.TwoBars: return "2 bars";
				case QuantizationInterval.FourBars: return "4 bars";
				case QuantizationInterval.EightBars: return "8 bars";
				default: return "";
			}
		}

		/// <summary>Thin lines through the lanes at the markers and region edges.</summary>
		private void DrawMarkerLines(NVector2 lanesOrigin, ImDrawListPtr draw, float bottom)
		{
			float top = lanesOrigin.Y + LogicHeight;
			foreach (AudioMarker marker in Asset.markers)
			{
				uint color;
				switch (marker)
				{
					case TransitionMarker _: color = StudioUi.WithAlpha(StudioUi.Green, 0.5f); break;
					case LoopRegion _: color = StudioUi.WithAlpha(StudioUi.LoopBlue, 0.45f); break;
					case TempoMarker _: color = StudioUi.WithAlpha(StudioUi.Tempo, 0.3f); break;
					case SustainPoint _: color = StudioUi.WithAlpha(StudioUi.Sustain, 0.4f); break;
					default: color = StudioUi.Rgba(1f, 1f, 1f, 0.3f); break;
				}

				float x0 = ToX(marker.Position);
				draw.AddLine(new NVector2(x0, top), new NVector2(x0, bottom), color);
				if (marker.Length > 0.0)
				{
					float x1 = ToX(marker.End);
					draw.AddLine(new NVector2(x1, top), new NVector2(x1, bottom), color);
				}
			}
		}

		// ---------------------------------------------------------------- input

		private void ZoomAround(float x, float factor)
		{
			double at = FromX(x);
			pixelsPerSecond = (float)Math.Clamp(pixelsPerSecond * factor, 4.0, 4000.0);
			scroll = Math.Max(0.0, at - (x - canvasLeft) / pixelsPerSecond);
		}

		private void HandleWheel(float viewHeight, float totalHeight)
		{
			if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
			{
				return;
			}

			ImGuiIOPtr io = ImGui.GetIO();
			if (Math.Abs(io.MouseWheel) < 0.01f)
			{
				return;
			}

			if (io.KeyCtrl)
			{
				ZoomAround(ImGui.GetMousePos().X, io.MouseWheel > 0f ? 1.2f : 1f / 1.2f);
			}
			else if (io.KeyShift)
			{
				scroll = Math.Max(0.0, scroll - io.MouseWheel * 60.0 / pixelsPerSecond);
			}
			else
			{
				bodyScroll = Math.Clamp(bodyScroll - io.MouseWheel * 40f, 0f, Math.Max(0f, totalHeight - viewHeight));
			}
		}

		private static bool Inside(NVector2 point, NVector2 min, NVector2 max)
		{
			return point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;
		}

		private Hit HitTest(NVector2 lanesOrigin, NVector2 mouse)
		{
			var hit = new Hit { Kind = HitKind.None };
			if (mouse.X < canvasLeft || mouse.X > canvasRight)
			{
				return hit;
			}

			hit.Time = FromX(mouse.X);
			for (int i = logicItems.Count - 1; i >= 0; i--)
			{
				LogicItem item = logicItems[i];
				if (item.Kind == HitKind.MarkerEnd && Inside(mouse, item.Min, item.Max))
				{
					hit.Kind = item.Kind;
					hit.Id = item.Id;
					return hit;
				}
			}

			for (int i = logicItems.Count - 1; i >= 0; i--)
			{
				LogicItem item = logicItems[i];
				if (item.Kind == HitKind.Marker && Inside(mouse, item.Min, item.Max))
				{
					hit.Kind = item.Kind;
					hit.Id = item.Id;
					return hit;
				}
			}

			foreach (Row row in rows)
			{
				float y = lanesOrigin.Y + row.Y;
				if (mouse.Y < y || mouse.Y >= y + row.Height)
				{
					continue;
				}

				switch (row.Kind)
				{
					case RowKind.Logic:
						hit.Kind = HitKind.Logic;
						return hit;
					case RowKind.Property:
						hit.Kind = HitKind.PropertyLane;
						hit.Channel = row.Channel;
						hit.Property = row.Property;
						foreach (KeyItem key in keyItems)
						{
							if (key.Property.Key == row.Property.Key && Math.Abs(key.Center.Y - mouse.Y) <= KeyRadius + 2f && Math.Abs(key.Center.X - mouse.X) <= KeyRadius + 2f)
							{
								hit.Kind = HitKind.Key;
								hit.Key = key;
								return hit;
							}
						}

						return hit;
				}

				hit.Kind = HitKind.Lane;
				hit.Channel = row.Channel;
				for (int i = pieces.Count - 1; i >= 0; i--)
				{
					Piece piece = pieces[i];
					if (piece.Channel != row.Channel || !Inside(mouse, piece.Min, piece.Max))
					{
						continue;
					}

					hit.Piece = piece;
					hit.Id = piece.Clip.id;
					hit.Kind = HitKind.Clip;
					TimelineClip clip = piece.Clip;
					if (mouse.Y <= piece.Min.Y + FadeHandleSize + 2f)
					{
						float inX = Math.Clamp(ToX(clip.start + clip.fadeIn.Length), piece.Min.X + 4f, piece.Max.X - 4f);
						float outX = Math.Clamp(ToX(clip.End - clip.fadeOut.Length), piece.Min.X + 4f, piece.Max.X - 4f);
						if (Math.Abs(mouse.X - outX) <= FadeHandleSize)
						{
							hit.Kind = HitKind.FadeOut;
							return hit;
						}

						if (Math.Abs(mouse.X - inX) <= FadeHandleSize)
						{
							hit.Kind = HitKind.FadeIn;
							return hit;
						}
					}

					if (piece.Max.X - piece.Min.X > EdgeGrab * 3f)
					{
						if (mouse.X <= piece.Min.X + EdgeGrab)
						{
							hit.Kind = HitKind.ClipStart;
						}
						else if (mouse.X >= piece.Max.X - EdgeGrab)
						{
							hit.Kind = HitKind.ClipEnd;
						}
					}

					return hit;
				}

				return hit;
			}

			return hit;
		}

		private void HandleMouse(NVector2 lanesOrigin, bool canvasHovered)
		{
			NVector2 mouse = ImGui.GetMousePos();
			Hit hit = canvasHovered && dragKind == DragKind.None ? HitTest(lanesOrigin, mouse) : new Hit();
			hoverHit = dragKind == DragKind.None ? hit : hoverHit;
			bool resizing = hit.Kind == HitKind.ClipStart || hit.Kind == HitKind.ClipEnd || hit.Kind == HitKind.FadeIn || hit.Kind == HitKind.FadeOut || hit.Kind == HitKind.MarkerEnd
				|| (dragKind != DragKind.None && dragKind != DragKind.MoveClip && dragKind != DragKind.MoveMarker);
			if (resizing)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
			}

			if (canvasHovered && dragKind == DragKind.None)
			{
				ShowHitTooltip(hit);
			}

			// Middle drag pans.
			if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
			{
				panning = true;
				panMouseX0 = mouse.X;
				panScroll0 = scroll;
			}

			if (panning)
			{
				scroll = Math.Max(0.0, panScroll0 - (mouse.X - panMouseX0) / pixelsPerSecond);
				if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
				{
					panning = false;
				}
			}

			if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				BeginDrag(hit, mouse);
			}

			if (dragKind != DragKind.None && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				double delta = (mouse.X - dragMouseX0) / pixelsPerSecond;
				if (Math.Abs(mouse.X - dragMouseX0) >= 2f || Math.Abs(mouse.Y - dragMouseY0) >= 2f || dragChanged)
				{
					ApplyDrag(lanesOrigin, mouse, delta);
				}
			}

			if (dragKind != DragKind.None && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				dragKind = DragKind.None;
				dragId = null;
				document.EndEdit();
			}

			if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				contextHit = hit;
				switch (hit.Kind)
				{
					case HitKind.Clip:
					case HitKind.ClipStart:
					case HitKind.ClipEnd:
					case HitKind.FadeIn:
					case HitKind.FadeOut:
						Select(SelectionKind.Clip, hit.Id);
						openClipPopup = hit.Id;
						break;
					case HitKind.Marker:
					case HitKind.MarkerEnd:
						Select(SelectionKind.Marker, hit.Id);
						openMarkerPopup = hit.Id;
						break;
					default:
						openContext = true;
						break;
				}
			}
		}

		private void BeginDrag(Hit hit, NVector2 mouse)
		{
			dragMouseX0 = mouse.X;
			dragMouseY0 = mouse.Y;
			dragChanged = false;
			switch (hit.Kind)
			{
				case HitKind.Clip:
				case HitKind.ClipStart:
				case HitKind.ClipEnd:
				case HitKind.FadeIn:
				case HitKind.FadeOut:
				{
					TimelineClip clip = hit.Piece.Clip;
					Select(SelectionKind.Clip, clip.id);
					if (hit.Kind == HitKind.Clip && EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						OpenInAnimationWindow(clip, hit.Piece.Channel);
						return;
					}

					dragKind = hit.Kind == HitKind.Clip ? DragKind.MoveClip : hit.Kind == HitKind.ClipStart ? DragKind.TrimStart : hit.Kind == HitKind.ClipEnd ? DragKind.TrimEnd : hit.Kind == HitKind.FadeIn ? DragKind.FadeIn : DragKind.FadeOut;
					dragId = clip.id;
					dragStart0 = clip.start;
					dragLength0 = clip.length;
					dragOffset0 = clip.startOffset;
					if (hit.Kind == HitKind.FadeIn) dragStart0 = clip.fadeIn.Length;
					if (hit.Kind == HitKind.FadeOut) dragStart0 = clip.fadeOut.Length;
					document.BeginEdit("Edit clip");
					break;
				}
				case HitKind.Marker:
				case HitKind.MarkerEnd:
				{
					AudioMarker marker = Asset.FindMarker(hit.Id);
					Select(SelectionKind.Marker, hit.Id);
					if (marker == null)
					{
						return;
					}

					if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						openMarkerPopup = hit.Id;
						return;
					}

					dragKind = hit.Kind == HitKind.MarkerEnd ? DragKind.ResizeMarker : DragKind.MoveMarker;
					dragId = hit.Id;
					dragStart0 = marker.Position;
					dragLength0 = marker.Length;
					document.BeginEdit("Edit marker");
					break;
				}
				case HitKind.Key:
					selectionKind = SelectionKind.None;
					selectedId = null;
					selectedKeyClip = hit.Key.Clip;
					selectedKeyProperty = hit.Key.Property;
					selectedKeyTime = hit.Key.ClipTime;
					dragKind = DragKind.MoveKey;
					dragId = hit.Key.Clip.id;
					dragStart0 = hit.Key.ClipTime;
					document.BeginEdit("Move key");
					document.TouchClip(hit.Key.Clip.clip);
					break;
				case HitKind.PropertyLane:
					if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						double time = snap ? SnapToFrame(hit.Time) : hit.Time;
						KeyProperty(hit.Channel, hit.Property, time);
					}

					break;
				case HitKind.Lane:
					Select(SelectionKind.Channel, hit.Channel.id);
					break;
				case HitKind.Logic:
					ClearSelection();
					break;
			}
		}

		private void ShowHitTooltip(Hit hit)
		{
			switch (hit.Kind)
			{
				case HitKind.Clip:
				case HitKind.ClipStart:
				case HitKind.ClipEnd:
				{
					TimelineClip clip = hit.Piece.Clip;
					ImGui.SetTooltip(clip.DisplayName + "\n" + FormatTime(clip.start) + " - " + FormatTime(clip.End) + (clip.loop ? ", looping" : "") + "\nDrag: move, edges: trim, top corners: fades, overlap another clip: crossfade.\nDouble click: its keys in the Animation window. Right click: settings.");
					break;
				}
				case HitKind.FadeIn:
					ImGui.SetTooltip("Fade in: from what the property holds without the clip");
					break;
				case HitKind.FadeOut:
					ImGui.SetTooltip("Fade out: back to what the property holds without the clip (without one, the last key holds).\nOverlapping the next clip crossfades into it: only what that clip has keys for changes.");
					break;
				case HitKind.Key:
					ImGui.SetTooltip(hit.Key.Property.Label + "\nDrag: move the key. Del: remove it.");
					break;
				case HitKind.PropertyLane:
					ImGui.SetTooltip(hit.Property.Label + "\nDouble click: a key here with the object's value");
					break;
				case HitKind.Marker:
				case HitKind.MarkerEnd:
				{
					AudioMarker marker = Asset.FindMarker(hit.Id);
					if (marker != null)
					{
						ImGui.SetTooltip(marker.Kind + (string.IsNullOrEmpty(marker.Name) ? "" : ": " + marker.Name) + " at " + FormatTime(marker.Position) + (marker.Length > 0.0 ? " - " + FormatTime(marker.End) : "") + "\nDrag: move" + (marker.Length > 0.0 ? ", right edge: resize" : "") + ". Double or right click: settings.");
					}

					break;
				}
			}
		}

		private void ApplyDrag(NVector2 lanesOrigin, NVector2 mouse, double delta)
		{
			switch (dragKind)
			{
				case DragKind.MoveClip:
				{
					TimelineClip clip = Asset.FindClip(dragId, out TimelineChannel owner);
					if (clip == null)
					{
						dragKind = DragKind.None;
						return;
					}

					double start = SnapTime(dragStart0 + delta, clip.id);
					double endSnapped = SnapTime(dragStart0 + delta + clip.length, clip.id);
					if (snap && Math.Abs(endSnapped - (dragStart0 + delta + clip.length)) < Math.Abs(start - (dragStart0 + delta)))
					{
						start = Math.Max(0.0, endSnapped - clip.length);
					}

					bool changed = Math.Abs(start - clip.start) > 1e-9;
					clip.start = start;
					foreach (Row row in rows)
					{
						float y = lanesOrigin.Y + row.Y;
						if (row.Kind == RowKind.Channel && mouse.Y >= y && mouse.Y < y + row.Height && row.Channel != owner)
						{
							owner.clips.Remove(clip);
							row.Channel.clips.Add(clip);
							changed = true;
							break;
						}
					}

					if (changed)
					{
						dragChanged = true;
						Asset.MarkChanged();
					}

					break;
				}
				case DragKind.TrimEnd:
				{
					TimelineClip clip = Asset.FindClip(dragId, out _);
					if (clip == null) return;
					double end = SnapTime(dragStart0 + dragLength0 + delta, clip.id);
					double length = Math.Max(0.01, end - clip.start);
					if (Math.Abs(length - clip.length) > 1e-9)
					{
						clip.length = length;
						dragChanged = true;
						Asset.MarkChanged();
					}

					break;
				}
				case DragKind.TrimStart:
				{
					TimelineClip clip = Asset.FindClip(dragId, out _);
					if (clip == null) return;
					double end = dragStart0 + dragLength0;
					double start = Math.Min(SnapTime(dragStart0 + delta, clip.id), end - 0.01);

					// The content stays where it is: the clip starts later in its keys. It cannot start before them.
					double offset = dragOffset0 + (start - dragStart0) * clip.speed;
					if (offset < 0.0)
					{
						start -= offset / clip.speed;
						offset = 0.0;
					}

					if (Math.Abs(start - clip.start) > 1e-9)
					{
						clip.start = start;
						clip.length = end - start;
						clip.startOffset = offset;
						dragChanged = true;
						Asset.MarkChanged();
					}

					break;
				}
				case DragKind.FadeIn:
				case DragKind.FadeOut:
				{
					TimelineClip clip = Asset.FindClip(dragId, out _);
					if (clip == null) return;
					bool fadeIn = dragKind == DragKind.FadeIn;
					double fade = Math.Clamp(dragStart0 + (fadeIn ? delta : -delta), 0.0, clip.length);
					if (snap) fade = SnapToFrame(fade);
					AudioFade target = fadeIn ? clip.fadeIn : clip.fadeOut;
					if (Math.Abs(fade - target.Length) > 1e-9)
					{
						target.Length = fade;
						dragChanged = true;
						Asset.MarkChanged();
					}

					break;
				}
				case DragKind.MoveMarker:
				{
					AudioMarker marker = Asset.FindMarker(dragId);
					if (marker == null) return;
					double position = SnapTime(dragStart0 + delta, marker.Id);
					if (Math.Abs(position - marker.Position) > 1e-9)
					{
						marker.Position = position;
						dragChanged = true;
						Asset.MarkChanged();
					}

					break;
				}
				case DragKind.ResizeMarker:
				{
					AudioMarker marker = Asset.FindMarker(dragId);
					double end = SnapTime(dragStart0 + dragLength0 + delta, dragId);
					double length = Math.Max(0.05, end - dragStart0);
					switch (marker)
					{
						case LoopRegion loop when Math.Abs(loop.RegionLength - length) > 1e-9:
							loop.RegionLength = length;
							dragChanged = true;
							Asset.MarkChanged();
							break;
						case TransitionRegion region when Math.Abs(region.RegionLength - length) > 1e-9:
							region.RegionLength = length;
							dragChanged = true;
							Asset.MarkChanged();
							break;
					}

					break;
				}
				case DragKind.MoveKey:
				{
					if (selectedKeyClip?.clip == null || selectedKeyProperty == null)
					{
						return;
					}

					double frame = 1.0 / Math.Max(Asset.frameRate, 1f);
					double time = dragStart0 + delta * selectedKeyClip.speed;
					time = Math.Max(0.0, snap ? Math.Round(time / frame) * frame : time);
					if (Math.Abs(time - selectedKeyTime) > 1e-6 && MoveKeyTime(selectedKeyClip.clip, selectedKeyProperty, selectedKeyTime, (float)time))
					{
						selectedKeyTime = (float)time;
						dragChanged = true;
					}

					break;
				}
			}
		}

		/// <summary>Animation clips dragged from the project onto a lane become clips there; objects from the hierarchy become channels.</summary>
		private void HandleDrop(NVector2 lanesOrigin)
		{
			if (!ImGui.BeginDragDropTarget())
			{
				return;
			}

			ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(DragAndDrop.AssetPayload, ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
			if (!payload.IsNull)
			{
				var drop = new List<AnimationClip>();
				if (payload.IsDelivery())
				{
					foreach (DragItem item in DragAndDrop.Items)
					{
						if (DragAndDrop.TryResolve(item, typeof(AnimationClip), true, out object value) && value is AnimationClip clip)
						{
							drop.Add(clip);
						}
					}
				}

				if (drop.Count > 0)
				{
					DropClips(drop, lanesOrigin);
				}
			}

			payload = ImGui.AcceptDragDropPayload(DragAndDrop.GameObjectPayload, ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
			if (!payload.IsNull && payload.IsDelivery() && host != null)
			{
				var objects = new List<GameObject>();
				foreach (DragItem item in DragAndDrop.Items)
				{
					if (DragAndDrop.TryResolve(item, typeof(GameObject), true, out object value) && value is GameObject go)
					{
						objects.Add(go);
					}
				}

				foreach (GameObject go in objects)
				{
					AddChannel(go);
				}
			}

			ImGui.EndDragDropTarget();
		}

		private void DropClips(List<AnimationClip> clips, NVector2 lanesOrigin)
		{
			Hit hit = HitTest(lanesOrigin, ImGui.GetMousePos());
			TimelineChannel channel = hit.Channel ?? (Asset.channels.Count > 0 ? Asset.channels[Asset.channels.Count - 1] : null);
			double at = SnapTime(hit.Time, null);
			document.Edit("Add clip", () =>
			{
				channel = channel ?? NewChannel(null);
				foreach (AnimationClip clip in clips)
				{
					var placed = new TimelineClip { id = TimelineAsset.NewId(), clip = clip, start = at, length = Math.Max(clip.length, 0.1f), loop = clip.loop };
					channel.clips.Add(placed);
					Select(SelectionKind.Clip, placed.id);
					at += placed.length;
				}
			});
		}

		#endregion
	}
}
