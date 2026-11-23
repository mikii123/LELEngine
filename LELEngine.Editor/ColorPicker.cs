using System;
using Hexa.NET.ImGui;
using LELEngine.Editor.Automation;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's color field and its picker, which works in HSV (as Unity's): the field is ImGui's (the inputs as
	///     the caller's flags ask, the swatch, the label), a click on the swatch opens the picker in a popup:
	///     <list type="bullet">
	///         <item>a saturation / brightness square beside a hue bar;</item>
	///         <item>the new color beside the one the picker opened with (a click on that one goes back to it);</item>
	///         <item>H (degrees), S and V (percent) and A (percent, unless the field has no alpha) as gradient bars with
	///         their numbers;</item>
	///         <item>for an HDR field the intensity in stops (Unity's): the color is a base color whose brightest channel is
	///         at most 1, times 2^intensity, and the square and the bars edit the base color, so a bright emission keeps
	///         its intensity while its hue or saturation change;</item>
	///         <item>the color's RGB values and the base color in hexadecimal.</item>
	///     </list>
	///     Swatches show an HDR color brighter than 1 as its base color (the field's, the new and the original one).
	///     The picker is drawn inside the field's group, so the field reads as active while one of its parts is dragged
	///     and as edited when it lets go: the inspector makes one undo step per drag or click.
	/// </summary>
	internal static class ColorPicker
	{
		#region PrivateFields

		private const string PopupId = "##hsvpicker";
		private const float MaxIntensity = 10f;

		// One picker is open at a time (a popup): the field it edits and its HSV state, kept between frames so the hue
		// stays where it was while the color is grey or black (RGB has no hue there).
		private static uint openField;
		private static float hue;
		private static float saturation;
		private static float brightness;
		private static float intensity;
		private static NVector4 written;
		private static NVector4 original;

		#endregion

		#region PublicMethods

		/// <summary>The color field with id <paramref name="id" /> (the label's): inputs, swatch, label, the picker. True when the color changed.</summary>
		public static bool Field(string label, ref NVector4 color, ImGuiColorEditFlags flags, uint id)
		{
			bool changed = false;
			float width = ImGui.CalcItemWidth();
			float square = ImGui.GetFrameHeight();
			float spacing = ImGui.GetStyle().ItemInnerSpacing.X;

			ImGui.BeginGroup();
			ImGui.PushID(label);
			if ((flags & ImGuiColorEditFlags.NoInputs) == 0)
			{
				ImGui.SetNextItemWidth(Math.Max(1f, width - square - spacing));
				changed |= ImGui.ColorEdit4("##fields", ref color, flags | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoSmallPreview | ImGuiColorEditFlags.NoPicker);
				ImGui.SameLine(0f, spacing);
			}

			// Automation's click on the field ("color:Label") opens the picker too.
			bool clicked = ImGui.ColorButton("##swatch", Shown(color, flags), flags, new NVector2(square, square));
			if ((clicked || UIAutomation.ItemClicked(id, 0)) && (flags & ImGuiColorEditFlags.NoPicker) == 0)
			{
				ImGui.OpenPopup(PopupId);
				ImGui.SetNextWindowPos(new NVector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y + ImGui.GetStyle().ItemSpacing.Y));
				openField = id;
				original = color;
				written = color;
				Derive(color, (flags & ImGuiColorEditFlags.Hdr) != 0);
			}

			int hidden = label.IndexOf("##", StringComparison.Ordinal);
			string shown = hidden < 0 ? label : label.Substring(0, hidden);
			if (shown.Length > 0 && (flags & ImGuiColorEditFlags.NoLabel) == 0)
			{
				ImGui.SameLine(0f, spacing);
				ImGui.TextUnformatted(shown);
			}

			if (ImGui.BeginPopup(PopupId))
			{
				// The picker's own elements must not take the field's name from its line ("color:Color" stays).
				var lineLabel = TestAnchors.TakeLineLabel();
				changed |= Picker(ref color, flags, id);
				TestAnchors.RestoreLineLabel(lineLabel);
				ImGui.EndPopup();
			}

			ImGui.PopID();
			ImGui.EndGroup();
			return changed;
		}

		#endregion

		#region PrivateMethods

		private static bool Picker(ref NVector4 color, ImGuiColorEditFlags flags, uint id)
		{
			bool hdr = (flags & ImGuiColorEditFlags.Hdr) != 0;
			bool hasAlpha = (flags & ImGuiColorEditFlags.NoAlpha) == 0;
			if (openField != id || color != written)
			{
				// Opened for another field, or the color changed elsewhere (undo, automation, the field's own inputs).
				openField = id;
				written = color;
				Derive(color, hdr);
			}

			ImGuiStylePtr style = ImGui.GetStyle();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			float unit = ImGui.GetFrameHeight();
			float gap = style.ItemInnerSpacing.X;
			float width = unit * 12f;
			bool edited = false;
			NVector2 mouse = ImGui.GetIO().MousePos;

			// Saturation (across) and brightness (up) of the hue, as ImGui draws it: white to the hue, then black over it.
			float side = width - unit - gap;
			NVector2 at = ImGui.GetCursorScreenPos();
			EditorGui.InvisibleButton("##sv", new NVector2(side, side));
			if (ImGui.IsItemActive())
			{
				edited |= Set(ref saturation, Saturate((mouse.X - at.X) / side));
				edited |= Set(ref brightness, 1f - Saturate((mouse.Y - at.Y) / side));
				ImGuiP.MarkItemEdited(ImGui.GetItemID());
			}

			uint white = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 1f, 1f, 1f));
			uint black = ImGui.ColorConvertFloat4ToU32(new NVector4(0f, 0f, 0f, 1f));
			uint clear = ImGui.ColorConvertFloat4ToU32(new NVector4(0f, 0f, 0f, 0f));
			uint pure = ToU32(HsvToRgb(hue, 1f, 1f));
			NVector2 end = at + new NVector2(side, side);
			draw.AddRectFilledMultiColor(at, end, white, pure, pure, white);
			draw.AddRectFilledMultiColor(at, end, clear, clear, black, black);
			draw.AddRect(at, end, ImGui.GetColorU32(ImGuiCol.Border));
			var point = new NVector2(at.X + Saturate(saturation) * side, at.Y + (1f - Saturate(brightness)) * side);
			draw.AddCircleFilled(point, 5f, ToU32(HsvToRgb(hue, saturation, Math.Min(brightness, 1f))));
			draw.AddCircle(point, 5.5f, white, 0, 1.5f);
			draw.AddCircle(point, 7f, black, 0, 1f);

			// Hue, top to bottom.
			ImGui.SameLine(0f, gap);
			at = ImGui.GetCursorScreenPos();
			EditorGui.InvisibleButton("##hue", new NVector2(unit, side));
			if (ImGui.IsItemActive())
			{
				edited |= Set(ref hue, Saturate((mouse.Y - at.Y) / side));
				ImGuiP.MarkItemEdited(ImGui.GetItemID());
			}

			for (int i = 0; i < 6; i++)
			{
				uint top = ToU32(HsvToRgb(i / 6f, 1f, 1f));
				uint bottom = ToU32(HsvToRgb((i + 1) / 6f, 1f, 1f));
				draw.AddRectFilledMultiColor(at + new NVector2(0f, side * i / 6f), at + new NVector2(unit, side * (i + 1) / 6f), top, top, bottom, bottom);
			}

			draw.AddRect(at, at + new NVector2(unit, side), ImGui.GetColorU32(ImGuiCol.Border));
			float y = at.Y + hue * side;
			draw.AddRectFilled(new NVector2(at.X - 2f, y - 2f), new NVector2(at.X + unit + 2f, y + 2f), black);
			draw.AddRectFilled(new NVector2(at.X - 1f, y - 1f), new NVector2(at.X + unit + 1f, y + 1f), white);

			// The new color beside the one the picker opened with.
			float half = (width - gap) / 2f;
			var swatch = new NVector2(half, unit * 1.2f);
			ImGuiColorEditFlags swatchFlags = (flags & (ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.Hdr)) | ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop;
			ImGui.ColorButton("##new", Shown(color, flags), swatchFlags, swatch);
			ImGui.SetItemTooltip("New");
			ImGui.SameLine(0f, gap);
			bool changed = false;
			if (ImGui.ColorButton("##original", Shown(original, flags), swatchFlags, swatch) && color != original)
			{
				color = original;
				written = color;
				Derive(color, hdr);
				ImGuiP.MarkItemEdited(ImGui.GetItemID());
				changed = true;
				edited = false;
			}

			ImGui.SetItemTooltip("Original: click to go back to it");

			float labels = ImGui.CalcTextSize(hdr ? "Intensity" : "RGB").X + gap * 2f;
			float field = unit * 2.6f;
			float degrees = hue * 360f;
			if (Bar("H", ref degrees, 360f, "%.0f", labels, width, field, Gradient.Hue))
			{
				hue = degrees / 360f;
				edited = true;
			}

			float percent = saturation * 100f;
			if (Bar("S", ref percent, 100f, "%.0f", labels, width, field, Gradient.Saturation))
			{
				saturation = percent / 100f;
				edited = true;
			}

			percent = brightness * 100f;
			if (Bar("V", ref percent, 100f, "%.0f", labels, width, field, Gradient.Brightness))
			{
				brightness = percent / 100f;
				edited = true;
			}

			float alpha = color.W;
			if (hasAlpha)
			{
				percent = alpha * 100f;
				if (Bar("A", ref percent, 100f, "%.0f", labels, width, field, Gradient.Alpha))
				{
					alpha = percent / 100f;
					edited = true;
				}
			}

			if (hdr)
			{
				Label("Intensity", labels);
				ImGui.SetNextItemWidth(width - labels);
				edited |= EditorGui.DragFloat("##intensity", ref intensity, 0.02f, -MaxIntensity, MaxIntensity, "%.2f", ImGuiSliderFlags.AlwaysClamp);
				ImGui.SetItemTooltip("Stops: the color is the base color times 2 to this power");
			}

			if (edited)
			{
				color = Compose(alpha);
				written = color;
				changed = true;
			}

			// The values themselves: the color's RGB, the base color in hexadecimal.
			const ImGuiColorEditFlags Plain = ImGuiColorEditFlags.NoPicker | ImGuiColorEditFlags.NoSmallPreview | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoOptions;
			Label("RGB", labels);
			ImGui.SetNextItemWidth(width - labels);
			NVector3 rgb = new NVector3(color.X, color.Y, color.Z);
			if (ImGui.ColorEdit3("##rgb", ref rgb, Plain | ImGuiColorEditFlags.DisplayRgb | (flags & (ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr))))
			{
				color = new NVector4(rgb, color.W);
				written = color;
				Derive(color, hdr);
				changed = true;
			}

			Label("Hex", labels);
			ImGui.SetNextItemWidth(width - labels);
			NVector3 baseRgb = HsvToRgb(hue, saturation, brightness);
			NVector4 baseColor = new NVector4(Math.Min(baseRgb.X, 1f), Math.Min(baseRgb.Y, 1f), Math.Min(baseRgb.Z, 1f), color.W);
			if (ImGui.ColorEdit4("##hex", ref baseColor, Plain | ImGuiColorEditFlags.DisplayHex | (flags & ImGuiColorEditFlags.NoAlpha)))
			{
				RgbToHsv(baseColor.X, baseColor.Y, baseColor.Z, out float h, out float s, out float v);
				Keep(h, s, v);
				color = Compose(hasAlpha ? baseColor.W : color.W);
				written = color;
				changed = true;
			}

			return changed;
		}

		private enum Gradient
		{
			Hue,
			Saturation,
			Brightness,
			Alpha
		}

		/// <summary>A row: its name, a bar showing what the value does to the color (drag on it to set it), its number.</summary>
		private static bool Bar(string name, ref float value, float max, string format, float labels, float width, float field, Gradient gradient)
		{
			float unit = ImGui.GetFrameHeight();
			float gap = ImGui.GetStyle().ItemInnerSpacing.X;
			Label(name, labels);
			NVector2 at = ImGui.GetCursorScreenPos();
			float length = Math.Max(1f, width - labels - field - gap);
			bool changed = false;
			EditorGui.InvisibleButton("##bar" + name, new NVector2(length, unit));
			if (ImGui.IsItemActive())
			{
				changed = Set(ref value, Saturate((ImGui.GetIO().MousePos.X - at.X) / length) * max);
				ImGuiP.MarkItemEdited(ImGui.GetItemID());
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector2 end = at + new NVector2(length, unit);
			if (gradient == Gradient.Alpha)
			{
				DrawCheckerboard(draw, at, end);
			}

			int segments = gradient == Gradient.Hue ? 6 : 1;
			for (int i = 0; i < segments; i++)
			{
				float t0 = (float)i / segments;
				float t1 = (float)(i + 1) / segments;
				uint left = ToU32(ColorAt(gradient, t0));
				uint right = ToU32(ColorAt(gradient, t1));
				draw.AddRectFilledMultiColor(at + new NVector2(length * t0, 0f), at + new NVector2(length * t1, unit), left, right, right, left);
			}

			draw.AddRect(at, end, ImGui.GetColorU32(ImGuiCol.Border));
			float x = at.X + Saturate(value / max) * length;
			draw.AddRectFilled(new NVector2(x - 2f, at.Y - 1f), new NVector2(x + 2f, end.Y + 1f), ImGui.ColorConvertFloat4ToU32(new NVector4(0f, 0f, 0f, 1f)));
			draw.AddRectFilled(new NVector2(x - 1f, at.Y), new NVector2(x + 1f, end.Y), ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 1f, 1f, 1f)));

			ImGui.SameLine(0f, gap);
			ImGui.SetNextItemWidth(field);
			changed |= EditorGui.DragFloat("##" + name, ref value, max / 200f, 0f, max, format, ImGuiSliderFlags.AlwaysClamp);
			return changed;
		}

		private static void Label(string text, float width)
		{
			float x = ImGui.GetCursorPosX();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(text);
			ImGui.SameLine(x + width);
		}

		private static NVector4 ColorAt(Gradient gradient, float t)
		{
			float v = Math.Min(brightness, 1f);
			switch (gradient)
			{
				case Gradient.Hue:
					return new NVector4(HsvToRgb(t, saturation, v), 1f);
				case Gradient.Saturation:
					return new NVector4(HsvToRgb(hue, t, v), 1f);
				case Gradient.Brightness:
					return new NVector4(HsvToRgb(hue, saturation, t), 1f);
				default:
					return new NVector4(HsvToRgb(hue, saturation, v), t);
			}
		}

		private static void DrawCheckerboard(ImDrawListPtr draw, NVector2 min, NVector2 max)
		{
			float cell = (max.Y - min.Y) / 2f;
			uint light = ImGui.ColorConvertFloat4ToU32(new NVector4(0.8f, 0.8f, 0.8f, 1f));
			uint dark = ImGui.ColorConvertFloat4ToU32(new NVector4(0.5f, 0.5f, 0.5f, 1f));
			draw.AddRectFilled(min, max, light);
			int column = 0;
			for (float x = min.X; x < max.X; x += cell, column++)
			{
				for (int row = 0; row < 2; row++)
				{
					if ((column + row) % 2 == 1)
					{
						float y = min.Y + row * cell;
						draw.AddRectFilled(new NVector2(x, y), new NVector2(Math.Min(x + cell, max.X), y + cell), dark);
					}
				}
			}
		}

		/// <summary>The picker's state from a color: intensity (HDR: the brightest channel's stops over 1), then the base color's HSV.</summary>
		private static void Derive(NVector4 color, bool hdr)
		{
			float max = Math.Max(color.X, Math.Max(color.Y, color.Z));
			intensity = hdr && max > 1f ? Math.Min(MathF.Log2(max), MaxIntensity) : 0f;
			float scale = MathF.Pow(2f, -intensity);
			RgbToHsv(color.X * scale, color.Y * scale, color.Z * scale, out float h, out float s, out float v);
			Keep(h, s, v);
		}

		/// <summary>Takes HSV values, keeping the hue of a grey color and the hue and saturation of black.</summary>
		private static void Keep(float h, float s, float v)
		{
			if (v > 0f)
			{
				if (s > 0f)
				{
					hue = h;
				}

				saturation = s;
			}

			brightness = v;
		}

		private static NVector4 Compose(float alpha)
		{
			NVector3 rgb = HsvToRgb(hue, saturation, brightness) * MathF.Pow(2f, intensity);
			return new NVector4(rgb, alpha);
		}

		/// <summary>A swatch's color: an HDR color brighter than 1 as its base color (its brightest channel at 1), as Unity shows it.</summary>
		private static NVector4 Shown(NVector4 color, ImGuiColorEditFlags flags)
		{
			float max = Math.Max(color.X, Math.Max(color.Y, color.Z));
			if ((flags & ImGuiColorEditFlags.Hdr) == 0 || max <= 1f)
			{
				return color;
			}

			return new NVector4(color.X / max, color.Y / max, color.Z / max, color.W);
		}

		private static bool Set(ref float field, float value)
		{
			if (field == value)
			{
				return false;
			}

			field = value;
			return true;
		}

		private static float Saturate(float value)
		{
			return value < 0f ? 0f : value > 1f ? 1f : value;
		}

		private static uint ToU32(NVector3 rgb)
		{
			return ImGui.ColorConvertFloat4ToU32(new NVector4(Saturate(rgb.X), Saturate(rgb.Y), Saturate(rgb.Z), 1f));
		}

		private static uint ToU32(NVector4 color)
		{
			return ImGui.ColorConvertFloat4ToU32(new NVector4(Saturate(color.X), Saturate(color.Y), Saturate(color.Z), Saturate(color.W)));
		}

		/// <summary>RGB to hue (0..1), saturation and value (the brightest channel).</summary>
		private static void RgbToHsv(float r, float g, float b, out float h, out float s, out float v)
		{
			float max = Math.Max(r, Math.Max(g, b));
			float min = Math.Min(r, Math.Min(g, b));
			float delta = max - min;
			v = max;
			s = max > 0f ? delta / max : 0f;
			if (delta <= 0f)
			{
				h = 0f;
				return;
			}

			if (max == r)
			{
				h = (g - b) / delta;
			}
			else if (max == g)
			{
				h = 2f + (b - r) / delta;
			}
			else
			{
				h = 4f + (r - g) / delta;
			}

			h /= 6f;
			if (h < 0f)
			{
				h += 1f;
			}
		}

		private static NVector3 HsvToRgb(float h, float s, float v)
		{
			h = (h - MathF.Floor(h)) * 6f;
			int sector = (int)h % 6;
			float f = h - MathF.Floor(h);
			float p = v * (1f - s);
			float q = v * (1f - s * f);
			float t = v * (1f - s * (1f - f));
			switch (sector)
			{
				case 0:
					return new NVector3(v, t, p);
				case 1:
					return new NVector3(q, v, p);
				case 2:
					return new NVector3(p, v, t);
				case 3:
					return new NVector3(p, q, v);
				case 4:
					return new NVector3(t, p, v);
				default:
					return new NVector3(v, p, q);
			}
		}

		#endregion
	}
}
