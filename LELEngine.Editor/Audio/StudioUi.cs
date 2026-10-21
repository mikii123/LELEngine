using System;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     The Audio Studio's look, modelled on FMOD Studio: its palette and ImGui style (applied to the studio's own
	///     ImGui context) and its custom widgets: rotary knobs, transport buttons, the time display, SOLO / MUTE
	///     toggles, level meters, and the shapes the timeline is drawn with (chevron regions, marker flags, fades).
	/// </summary>
	internal static class StudioUi
	{
		#region PublicFields

		public enum KnobScale
		{
			Linear,

			/// <summary>Logarithmic between min and max (frequencies; min must be above 0).</summary>
			Logarithmic,

			/// <summary>Decibels with a fader-like throw: most of the turn covers -40..+max dB.</summary>
			Volume,

			/// <summary>Square-root throw: fine control near min (times, distances).</summary>
			Quadratic
		}

		public enum TransportIcon
		{
			Stop,
			Play,
			Pause,
			Loop,
			Follow
		}

		// Palette.
		public static readonly uint Background = Rgb(0.185f, 0.185f, 0.19f);
		public static readonly uint Lane = Rgb(0.225f, 0.225f, 0.23f);
		public static readonly uint LaneAlternate = Rgb(0.21f, 0.21f, 0.215f);
		public static readonly uint LaneSelected = Rgb(0.27f, 0.27f, 0.29f);
		public static readonly uint LaneLine = Rgb(0.13f, 0.13f, 0.135f);
		public static readonly uint LogicLane = Rgb(0.125f, 0.125f, 0.13f);
		public static readonly uint TrackHeader = Rgb(0.265f, 0.265f, 0.275f);
		public static readonly uint TrackHeaderSelected = Rgb(0.33f, 0.33f, 0.35f);
		public static readonly uint Ruler = Rgb(0.29f, 0.29f, 0.3f);
		public static readonly uint RulerText = Rgb(0.86f, 0.86f, 0.88f);
		public static readonly uint RulerTick = Rgb(0.6f, 0.6f, 0.63f);
		public static readonly uint GridBar = Rgba(1f, 1f, 1f, 0.12f);
		public static readonly uint GridBeat = Rgba(1f, 1f, 1f, 0.045f);
		public static readonly uint Yellow = Rgb(0.97f, 0.77f, 0.17f);
		public static readonly uint Red = Rgb(0.88f, 0.29f, 0.24f);
		public static readonly uint Green = Rgb(0.55f, 0.78f, 0.24f);
		public static readonly uint GreenStripe = Rgb(0.47f, 0.69f, 0.17f);
		public static readonly uint GreenText = Rgb(0.08f, 0.15f, 0.03f);
		public static readonly uint LoopBlue = Rgb(0.2f, 0.6f, 0.92f);
		public static readonly uint LoopBlueStripe = Rgb(0.16f, 0.5f, 0.8f);
		public static readonly uint Flag = Rgb(0.84f, 0.84f, 0.86f);
		public static readonly uint FlagText = Rgb(0.11f, 0.11f, 0.12f);
		public static readonly uint Tempo = Rgb(0.96f, 0.66f, 0.24f);
		public static readonly uint Sustain = Rgb(0.82f, 0.45f, 0.86f);
		public static readonly uint Destination = Rgb(0.96f, 0.58f, 0.2f);
		public static readonly uint Playhead = Rgb(0.98f, 0.98f, 0.98f);
		public static readonly uint LivePlayhead = Rgb(0.3f, 0.88f, 0.95f);
		public static readonly uint TransitionTint = Rgba(0.4f, 0.6f, 1f, 0.09f);
		public static readonly uint Text = Rgb(0.9f, 0.9f, 0.92f);
		public static readonly uint TextDim = Rgb(0.58f, 0.58f, 0.62f);
		public static readonly uint KnobFace = Rgb(0.115f, 0.115f, 0.12f);
		public static readonly uint KnobTrack = Rgb(0.31f, 0.31f, 0.33f);
		public static readonly uint Lcd = Rgb(0.06f, 0.06f, 0.065f);
		public static readonly uint LcdText = Rgb(0.9f, 0.92f, 0.94f);
		public static readonly uint Outline = Rgba(0f, 0f, 0f, 0.55f);
		public static readonly uint Selected = Rgb(1f, 1f, 1f);

		#endregion

		#region PrivateFields

		private static uint activeKnob;
		private static float activeKnobPosition;

		#endregion

		#region PublicMethods

		public static uint Rgb(float r, float g, float b)
		{
			return ImGui.ColorConvertFloat4ToU32(new NVector4(r, g, b, 1f));
		}

		public static uint Rgba(float r, float g, float b, float a)
		{
			return ImGui.ColorConvertFloat4ToU32(new NVector4(r, g, b, a));
		}

		/// <summary>The color scaled towards black (factor below 1) or white (above 1), alpha kept.</summary>
		public static uint Shade(uint color, float factor)
		{
			NVector4 c = ImGui.ColorConvertU32ToFloat4(color);
			if (factor <= 1f)
			{
				return ImGui.ColorConvertFloat4ToU32(new NVector4(c.X * factor, c.Y * factor, c.Z * factor, c.W));
			}

			float t = Math.Min(1f, factor - 1f);
			return ImGui.ColorConvertFloat4ToU32(new NVector4(c.X + (1f - c.X) * t, c.Y + (1f - c.Y) * t, c.Z + (1f - c.Z) * t, c.W));
		}

		public static uint WithAlpha(uint color, float alpha)
		{
			NVector4 c = ImGui.ColorConvertU32ToFloat4(color);
			return ImGui.ColorConvertFloat4ToU32(new NVector4(c.X, c.Y, c.Z, alpha));
		}

		/// <summary>The studio's ImGui style: flat greys, dark inset fields, yellow accents (after the editor's own style).</summary>
		public static void ApplyStyle()
		{
			ImGuiStylePtr style = ImGui.GetStyle();
			style.WindowRounding = 0f;
			style.ChildRounding = 0f;
			style.FrameRounding = 2f;
			style.GrabRounding = 2f;
			style.TabRounding = 3f;
			style.WindowBorderSize = 1f;
			style.FrameBorderSize = 0f;
			style.WindowPadding = new NVector2(8, 6);
			style.FramePadding = new NVector2(6, 3);
			style.ItemSpacing = new NVector2(6, 4);

			Set(ImGuiCol.Text, 0.9f, 0.9f, 0.92f);
			Set(ImGuiCol.TextDisabled, 0.56f, 0.56f, 0.6f);
			Set(ImGuiCol.WindowBg, 0.185f, 0.185f, 0.19f);
			Set(ImGuiCol.ChildBg, 0.185f, 0.185f, 0.19f);
			Set(ImGuiCol.PopupBg, 0.15f, 0.15f, 0.155f);
			Set(ImGuiCol.Border, 0.09f, 0.09f, 0.095f);
			Set(ImGuiCol.TitleBg, 0.15f, 0.15f, 0.155f);
			Set(ImGuiCol.TitleBgActive, 0.2f, 0.2f, 0.21f);
			Set(ImGuiCol.MenuBarBg, 0.24f, 0.24f, 0.25f);
			Set(ImGuiCol.FrameBg, 0.11f, 0.11f, 0.115f);
			Set(ImGuiCol.FrameBgHovered, 0.15f, 0.15f, 0.16f);
			Set(ImGuiCol.FrameBgActive, 0.18f, 0.18f, 0.19f);
			Set(ImGuiCol.Button, 0.31f, 0.31f, 0.33f);
			Set(ImGuiCol.ButtonHovered, 0.39f, 0.39f, 0.41f);
			Set(ImGuiCol.ButtonActive, 0.46f, 0.46f, 0.48f);
			Set(ImGuiCol.Header, 0.3f, 0.3f, 0.32f);
			Set(ImGuiCol.HeaderHovered, 0.36f, 0.36f, 0.38f);
			Set(ImGuiCol.HeaderActive, 0.42f, 0.42f, 0.44f);
			Set(ImGuiCol.Separator, 0.3f, 0.3f, 0.32f);
			Set(ImGuiCol.Tab, 0.2f, 0.2f, 0.21f);
			Set(ImGuiCol.TabHovered, 0.38f, 0.38f, 0.4f);
			Set(ImGuiCol.TabSelected, 0.33f, 0.33f, 0.35f);
			Set(ImGuiCol.TabSelectedOverline, 0.97f, 0.77f, 0.17f);
			Set(ImGuiCol.TabDimmed, 0.17f, 0.17f, 0.18f);
			Set(ImGuiCol.TabDimmedSelected, 0.27f, 0.27f, 0.29f);
			Set(ImGuiCol.CheckMark, 0.97f, 0.77f, 0.17f);
			Set(ImGuiCol.SliderGrab, 0.85f, 0.67f, 0.17f);
			Set(ImGuiCol.SliderGrabActive, 0.97f, 0.77f, 0.17f);
			Set(ImGuiCol.ScrollbarBg, 0.15f, 0.15f, 0.155f);
			Set(ImGuiCol.ScrollbarGrab, 0.34f, 0.34f, 0.36f);
			Set(ImGuiCol.ResizeGrip, 0.3f, 0.3f, 0.32f);
			Set(ImGuiCol.DockingPreview, 0.97f, 0.77f, 0.17f, 0.5f);
			Set(ImGuiCol.TextSelectedBg, 0.97f, 0.77f, 0.17f, 0.35f);
		}

		// ---------------------------------------------------------------- knobs

		/// <summary>
		///     A rotary knob: label above, the dial (drag up / down or right / left, Shift for fine steps, double click
		///     resets) and the value below. The arc runs from the minimum (or the middle when bipolar, or the reset value
		///     for volumes) to the value; volumes below the reset value draw red. Returns true when the value changed
		///     (the item is marked edited, so the undo tracking sees the whole drag as one edit).
		/// </summary>
		public static bool Knob(string id, ref float value, float min, float max, float reset, string label, string valueText, KnobScale scale = KnobScale.Linear, bool bipolar = false, float diameter = 32f, float width = 0f, bool readOnly = false, string tooltip = null, float step = 0f)
		{
			float lineHeight = ImGui.GetTextLineHeight();
			if (width <= 0f)
			{
				width = Math.Max(diameter + 22f, 58f);
			}

			float labelHeight = label != null ? lineHeight + 1f : 0f;
			float height = labelHeight + diameter + lineHeight + 3f;
			NVector2 origin = ImGui.GetCursorScreenPos();
			ImGui.InvisibleButton(id, new NVector2(width, height));
			bool hovered = ImGui.IsItemHovered();
			bool active = ImGui.IsItemActive();
			uint itemId = ImGui.GetItemID();
			bool changed = false;
			float t = ToNormalized(value, min, max, scale);
			if (!readOnly)
			{
				ImGuiIOPtr io = ImGui.GetIO();
				if (ImGui.IsItemActivated())
				{
					// The drag accumulates in normalized units, so small moves add up on stepped values.
					activeKnob = itemId;
					activeKnobPosition = t;
				}

				if (active && activeKnob == itemId && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
				{
					float delta = (-io.MouseDelta.Y + io.MouseDelta.X * 0.5f) / (io.KeyShift ? 900f : 180f);
					if (Math.Abs(delta) > 0f)
					{
						activeKnobPosition = Math.Clamp(activeKnobPosition + delta, 0f, 1f);
						float nextValue = FromNormalized(activeKnobPosition, min, max, scale);
						if (step > 0f)
						{
							nextValue = Math.Clamp(min + MathF.Round((nextValue - min) / step) * step, min, max);
						}

						if (Math.Abs(nextValue - value) > 0f)
						{
							value = nextValue;
							t = ToNormalized(value, min, max, scale);
							changed = true;
						}
					}
				}

				if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && Math.Abs(value - reset) > 0f)
				{
					value = reset;
					t = ToNormalized(value, min, max, scale);
					changed = true;
				}

				if (changed)
				{
					ImGuiP.MarkItemEdited(ImGui.GetItemID());
				}
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			if (label != null)
			{
				DrawCenteredText(draw, label, origin.X, width, origin.Y, TextDim);
			}

			float radius = diameter * 0.5f;
			var center = new NVector2(origin.X + width * 0.5f, origin.Y + labelHeight + radius);
			draw.AddCircleFilled(center, radius, KnobFace, 32);
			draw.AddCircle(center, radius, Outline, 32, 1f);
			const float start = MathF.PI * 0.75f;
			const float sweep = MathF.PI * 1.5f;
			float arcRadius = radius - 3.5f;
			draw.PathArcTo(center, arcRadius, start, start + sweep, 32);
			draw.PathStroke(KnobTrack, ImDrawFlags.None, 3f);
			float from = bipolar ? 0.5f : scale == KnobScale.Volume ? ToNormalized(reset, min, max, scale) : 0f;
			float a1 = start + sweep * Math.Min(from, t);
			float a2 = start + sweep * Math.Max(from, t);
			uint arc = readOnly ? LivePlayhead : scale == KnobScale.Volume && value < reset - 0.01f ? Red : Yellow;
			if (a2 - a1 > 0.02f)
			{
				draw.PathArcTo(center, arcRadius, a1, a2, 24);
				draw.PathStroke(arc, ImDrawFlags.None, 3f);
			}

			float angle = start + sweep * t;
			var direction = new NVector2(MathF.Cos(angle), MathF.Sin(angle));
			draw.AddLine(center + direction * (radius * 0.2f), center + direction * (radius - 6.5f), hovered || active ? Selected : Rgb(0.8f, 0.8f, 0.82f), 2f);
			DrawCenteredText(draw, valueText ?? "", origin.X, width, origin.Y + labelHeight + diameter + 2f, active ? Yellow : Text);
			if (hovered && !active)
			{
				// SetTooltip formats like printf: percent signs are escaped.
				string text = (label != null ? label + ": " : "") + valueText + (tooltip != null ? "\n" + tooltip : "") + (readOnly ? "\n(live value from the game)" : "\nDrag up / down (Shift: fine). Double click: reset.");
				ImGui.SetTooltip(text.Replace("%", "%%"));
			}

			return changed;
		}

		public static float ToNormalized(float value, float min, float max, KnobScale scale)
		{
			if (max <= min)
			{
				return 0f;
			}

			switch (scale)
			{
				case KnobScale.Logarithmic when min > 0f:
					return Math.Clamp(MathF.Log(Math.Max(value, min) / min) / MathF.Log(max / min), 0f, 1f);
				case KnobScale.Volume:
				{
					if (value <= min + 0.001f)
					{
						return 0f;
					}

					float ratio = MathF.Pow(10f, (Math.Min(value, max) - max) / 20f);
					return Math.Clamp(MathF.Pow(ratio, 0.25f), 0f, 1f);
				}
				case KnobScale.Quadratic:
					return Math.Clamp(MathF.Sqrt(Math.Max(0f, (value - min) / (max - min))), 0f, 1f);
				default:
					return Math.Clamp((value - min) / (max - min), 0f, 1f);
			}
		}

		public static float FromNormalized(float t, float min, float max, KnobScale scale)
		{
			t = Math.Clamp(t, 0f, 1f);
			switch (scale)
			{
				case KnobScale.Logarithmic when min > 0f:
					return min * MathF.Pow(max / min, t);
				case KnobScale.Volume:
				{
					if (t <= 0.0005f)
					{
						return min;
					}

					float db = max + 20f * MathF.Log10(t * t * t * t);
					return Math.Clamp(db, min, max);
				}
				case KnobScale.Quadratic:
					return min + (max - min) * t * t;
				default:
					return min + (max - min) * t;
			}
		}

		public static string FormatDb(float db)
		{
			return db <= AudioMath.SilenceDb + 0.01f ? "-inf dB" : db.ToString("0.0") + " dB";
		}

		public static string FormatPan(float pan)
		{
			if (Math.Abs(pan) < 0.005f)
			{
				return "C";
			}

			return (pan < 0f ? "L " : "R ") + Math.Round(Math.Abs(pan) * 100f).ToString("0");
		}

		public static string FormatSemitones(float semitones)
		{
			return (semitones > 0f ? "+" : "") + semitones.ToString("0.00") + " st";
		}

		// ---------------------------------------------------------------- transport

		/// <summary>A transport button: light grey with a dark icon, yellow while active (FMOD's play button).</summary>
		public static bool TransportButton(string id, TransportIcon icon, bool active, NVector2 size, string tooltip)
		{
			NVector2 min = ImGui.GetCursorScreenPos();
			bool pressed = ImGui.InvisibleButton(id, size);
			bool hovered = ImGui.IsItemHovered();
			bool held = ImGui.IsItemActive();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector2 max = min + size;
			uint background = active ? Yellow : held ? Rgb(0.58f, 0.58f, 0.6f) : hovered ? Rgb(0.8f, 0.8f, 0.82f) : Rgb(0.7f, 0.7f, 0.72f);
			draw.AddRectFilledMultiColor(min, max, Shade(background, 1.12f), Shade(background, 1.12f), Shade(background, 0.86f), Shade(background, 0.86f));
			draw.AddRect(min, max, Rgba(0f, 0f, 0f, 0.7f), 3f, ImDrawFlags.None, 1f);
			DrawIcon(draw, icon, (min + max) * 0.5f, Math.Min(size.X, size.Y) * 0.3f, Rgb(0.08f, 0.08f, 0.09f));
			if (hovered && tooltip != null)
			{
				ImGui.SetTooltip(tooltip);
			}

			return pressed;
		}

		/// <summary>A dark square toggle with a light icon (loop playback, follow the playhead); the icon turns yellow while on.</summary>
		public static bool IconToggle(string id, TransportIcon icon, bool on, NVector2 size, string tooltip)
		{
			NVector2 min = ImGui.GetCursorScreenPos();
			bool pressed = ImGui.InvisibleButton(id, size);
			bool hovered = ImGui.IsItemHovered();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, min + size, hovered ? Rgb(0.2f, 0.2f, 0.21f) : Rgb(0.14f, 0.14f, 0.15f), 3f);
			draw.AddRect(min, min + size, Outline, 3f);
			DrawIcon(draw, icon, min + size * 0.5f, Math.Min(size.X, size.Y) * 0.3f, on ? Yellow : TextDim);
			if (hovered && tooltip != null)
			{
				ImGui.SetTooltip(tooltip + (on ? " (on)" : " (off)"));
			}

			return pressed;
		}

		public static void DrawIcon(ImDrawListPtr draw, TransportIcon icon, NVector2 c, float s, uint color)
		{
			switch (icon)
			{
				case TransportIcon.Stop:
					draw.AddRectFilled(c - new NVector2(s * 0.8f), c + new NVector2(s * 0.8f), color, 1f);
					break;
				case TransportIcon.Play:
					draw.AddTriangleFilled(new NVector2(c.X - s * 0.7f, c.Y - s), new NVector2(c.X - s * 0.7f, c.Y + s), new NVector2(c.X + s * 0.95f, c.Y), color);
					break;
				case TransportIcon.Pause:
					draw.AddRectFilled(new NVector2(c.X - s * 0.8f, c.Y - s * 0.85f), new NVector2(c.X - s * 0.2f, c.Y + s * 0.85f), color);
					draw.AddRectFilled(new NVector2(c.X + s * 0.2f, c.Y - s * 0.85f), new NVector2(c.X + s * 0.8f, c.Y + s * 0.85f), color);
					break;
				case TransportIcon.Loop:
				{
					// Two arrows chasing each other round a rounded rectangle.
					float w = s * 1.1f;
					float h = s * 0.6f;
					draw.AddRect(new NVector2(c.X - w, c.Y - h), new NVector2(c.X + w, c.Y + h), color, h, ImDrawFlags.None, 1.6f);
					draw.AddTriangleFilled(new NVector2(c.X + 1f, c.Y - h - 3.5f), new NVector2(c.X + 1f, c.Y - h + 3.5f), new NVector2(c.X + 5f, c.Y - h), color);
					draw.AddTriangleFilled(new NVector2(c.X - 1f, c.Y + h - 3.5f), new NVector2(c.X - 1f, c.Y + h + 3.5f), new NVector2(c.X - 5f, c.Y + h), color);
					break;
				}
				case TransportIcon.Follow:
				{
					// A playhead with an arrow pushing it along.
					draw.AddLine(new NVector2(c.X + s * 0.6f, c.Y - s), new NVector2(c.X + s * 0.6f, c.Y + s), color, 1.6f);
					draw.AddLine(new NVector2(c.X - s, c.Y), new NVector2(c.X + s * 0.1f, c.Y), color, 1.6f);
					draw.AddTriangleFilled(new NVector2(c.X - s * 0.1f, c.Y - s * 0.45f), new NVector2(c.X - s * 0.1f, c.Y + s * 0.45f), new NVector2(c.X + s * 0.45f, c.Y), color);
					break;
				}
			}
		}

		/// <summary>
		///     The transport's time display: a dark panel with TIME / BEATS selectors and the state on the top row,
		///     the position in a large font below. Returns true when a selector was clicked (<paramref name="beats" />
		///     changed).
		/// </summary>
		public static bool TimeDisplay(string id, NVector2 size, string time, string state, uint stateColor, ref bool beats, bool beatsAvailable)
		{
			NVector2 min = ImGui.GetCursorScreenPos();
			NVector2 max = min + size;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, max, Lcd, 4f);
			draw.AddRect(min, max, Rgb(0.32f, 0.32f, 0.34f), 4f, ImDrawFlags.None, 1f);
			bool clicked = false;
			float x = min.X + 6f;
			float y = min.Y + 4f;
			string[] names = { "TIME", "BEATS" };
			for (int i = 0; i < 2; i++)
			{
				NVector2 textSize = ImGui.CalcTextSize(names[i]);
				var boxMin = new NVector2(x, y);
				var boxMax = new NVector2(x + textSize.X + 8f, y + textSize.Y + 2f);
				bool selected = beats == (i == 1);
				ImGui.SetCursorScreenPos(boxMin);
				ImGui.PushID(id + i);
				bool enabled = i == 0 || beatsAvailable;
				if (ImGui.InvisibleButton("##sel", boxMax - boxMin) && enabled && !selected)
				{
					beats = i == 1;
					clicked = true;
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(i == 0 ? "Show minutes:seconds.milliseconds" : beatsAvailable ? "Show bar.beat.sixteenth (from the event's tempo markers)" : "Needs a tempo marker on the event");
				}

				ImGui.PopID();
				draw.AddRectFilled(boxMin, boxMax, selected ? Rgb(0.78f, 0.78f, 0.8f) : Rgb(0.16f, 0.16f, 0.17f), 2f);
				draw.AddText(boxMin + new NVector2(4f, 1f), selected ? Rgb(0.05f, 0.05f, 0.06f) : enabled ? TextDim : Rgb(0.3f, 0.3f, 0.32f), names[i]);
				x = boxMax.X + 4f;
			}

			NVector2 stateSize = ImGui.CalcTextSize(state);
			draw.AddText(new NVector2(max.X - stateSize.X - 8f, y + 1f), stateColor, state);

			// The position in a large font (ImGui's font size is the line height: the digits come out about half of it).
			ImGui.PushFont(ImGui.GetFont(), 36f);
			NVector2 timeSize = ImGui.CalcTextSize(time);
			draw.AddText(new NVector2(min.X + 8f, max.Y - timeSize.Y + 3f), LcdText, time);
			ImGui.PopFont();
			ImGui.SetCursorScreenPos(min);
			ImGui.Dummy(size);
			return clicked;
		}

		/// <summary>A small labelled toggle (SOLO / MUTE): grey when off, the given color with dark text when on.</summary>
		public static bool TextToggle(string id, string text, bool on, uint onColor, NVector2 size, string tooltip)
		{
			NVector2 min = ImGui.GetCursorScreenPos();
			bool pressed = ImGui.InvisibleButton(id, size);
			bool hovered = ImGui.IsItemHovered();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			uint background = on ? onColor : hovered ? Rgb(0.42f, 0.42f, 0.44f) : Rgb(0.34f, 0.34f, 0.36f);
			draw.AddRectFilled(min, min + size, background, 2f);
			draw.AddRect(min, min + size, Outline, 2f);
			ImGui.PushFont(ImGui.GetFont(), 14f);
			NVector2 textSize = ImGui.CalcTextSize(text);
			draw.AddText(min + (size - textSize) * 0.5f, on ? Rgb(0.06f, 0.06f, 0.07f) : Rgb(0.88f, 0.88f, 0.9f), text);
			ImGui.PopFont();
			if (hovered && tooltip != null)
			{
				ImGui.SetTooltip(tooltip);
			}

			return pressed;
		}

		/// <summary>A vertical level meter from a linear peak: green to -12 dB, yellow to -3 dB, red above.</summary>
		public static void Meter(ImDrawListPtr draw, NVector2 min, NVector2 max, float peak)
		{
			draw.AddRectFilled(min, max, Rgb(0.08f, 0.08f, 0.085f));
			float db = peak > 1e-6f ? 20f * MathF.Log10(peak) : -100f;
			float t = Math.Clamp((db + 60f) / 66f, 0f, 1f);
			if (t <= 0f)
			{
				return;
			}

			float height = max.Y - min.Y;
			float top = max.Y - height * t;
			float yellowAt = max.Y - height * ((-12f + 60f) / 66f);
			float redAt = max.Y - height * ((-3f + 60f) / 66f);
			draw.AddRectFilled(new NVector2(min.X + 1f, Math.Max(top, yellowAt)), new NVector2(max.X - 1f, max.Y), Rgb(0.3f, 0.78f, 0.3f));
			if (top < yellowAt)
			{
				draw.AddRectFilled(new NVector2(min.X + 1f, Math.Max(top, redAt)), new NVector2(max.X - 1f, yellowAt), Rgb(0.92f, 0.8f, 0.2f));
			}

			if (top < redAt)
			{
				draw.AddRectFilled(new NVector2(min.X + 1f, top), new NVector2(max.X - 1f, redAt), Red);
			}
		}

		// ---------------------------------------------------------------- timeline shapes

		/// <summary>A region filled with right-pointing chevrons (FMOD's transition regions).</summary>
		public static void Chevrons(ImDrawListPtr draw, NVector2 min, NVector2 max, uint fill, uint stripe, float spacing = 13f)
		{
			draw.AddRectFilled(min, max, fill, 2f);
			draw.PushClipRect(min, max, true);
			float height = max.Y - min.Y;
			float middle = (min.Y + max.Y) * 0.5f;
			float depth = height * 0.38f;
			const float thickness = 4.5f;
			float startX = min.X - depth - spacing - (min.X % spacing);
			for (float x = startX; x < max.X + spacing; x += spacing)
			{
				draw.AddQuadFilled(new NVector2(x, min.Y), new NVector2(x + thickness, min.Y), new NVector2(x + thickness + depth, middle), new NVector2(x + depth, middle), stripe);
				draw.AddQuadFilled(new NVector2(x + depth, middle), new NVector2(x + thickness + depth, middle), new NVector2(x + thickness, max.Y), new NVector2(x, max.Y), stripe);
			}

			draw.PopClipRect();
		}

		/// <summary>A marker's flag: a box with its name at (x, y), its left edge on the marker's line. Returns the box's right edge.</summary>
		public static float FlagLabel(ImDrawListPtr draw, float x, float y, float height, string text, uint background, uint foreground, bool selected)
		{
			NVector2 textSize = ImGui.CalcTextSize(text);
			var min = new NVector2(x, y);
			var max = new NVector2(x + textSize.X + 10f, y + height);
			draw.AddRectFilled(min, max, background, 2f);
			draw.AddRect(min, max, selected ? Selected : Outline, 2f, ImDrawFlags.None, selected ? 2f : 1f);
			draw.AddText(new NVector2(x + 5f, y + (height - textSize.Y) * 0.5f), foreground, text);
			return max.X;
		}

		/// <summary>
		///     A fade drawn over a region: the area above the gain curve shaded, the curve as a line. A fade in rises
		///     from x0 to x1, a fade out falls; <paramref name="shape" /> bends it like <see cref="AudioAutomationCurve.Shape" />.
		/// </summary>
		public static void Fade(ImDrawListPtr draw, float x0, float x1, float top, float bottom, float shape, bool fadeIn, uint line, uint shade)
		{
			if (x1 - x0 < 1f)
			{
				return;
			}

			const int segments = 16;
			NVector2 previous = default;
			for (int i = 0; i <= segments; i++)
			{
				float u = i / (float)segments;
				float progress = fadeIn ? u : 1f - u;
				float gain = AudioAutomationCurve.Shape(progress, shape);
				var point = new NVector2(x0 + (x1 - x0) * u, bottom - (bottom - top) * gain);
				if (i > 0)
				{
					draw.AddQuadFilled(new NVector2(previous.X, top), new NVector2(point.X, top), point, previous, shade);
					draw.AddLine(previous, point, line, 1.5f);
				}

				previous = point;
			}
		}

		/// <summary>A small square handle (fade and region handles).</summary>
		public static void Handle(ImDrawListPtr draw, NVector2 center, float size, uint color, bool hot)
		{
			var half = new NVector2(size * 0.5f);
			draw.AddRectFilled(center - half, center + half, hot ? Yellow : color, 1.5f);
			draw.AddRect(center - half, center + half, Rgba(0f, 0f, 0f, 0.85f), 1.5f);
		}

		public static void DrawCenteredText(ImDrawListPtr draw, string text, float x, float width, float y, uint color)
		{
			NVector2 size = ImGui.CalcTextSize(text);
			if (size.X > width)
			{
				draw.PushClipRect(new NVector2(x, y), new NVector2(x + width, y + size.Y), true);
				draw.AddText(new NVector2(x, y), color, text);
				draw.PopClipRect();
				return;
			}

			draw.AddText(new NVector2(x + (width - size.X) * 0.5f, y), color, text);
		}

		#endregion

		#region PrivateMethods

		private static void Set(ImGuiCol color, float r, float g, float b, float a = 1f)
		{
			ImGui.GetStyle().Colors[(int)color] = new NVector4(r, g, b, a);
		}

		#endregion
	}
}
