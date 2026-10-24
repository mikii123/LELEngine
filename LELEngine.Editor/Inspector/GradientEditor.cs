using System;
using System.Reflection;
using Hexa.NET.ImGui;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The inspector's <see cref="Gradient" /> field (Unity's gradient field): the gradient over a checkerboard on the
	///     field's line; a click opens the gradient editor in a popup:
	///     <list type="bullet">
	///         <item>the mode (Blend, Fixed, Perceptual Blend);</item>
	///         <item>the bar, with the alpha keys above it and the color keys below it: a click selects a key, a drag moves it
	///         (past its neighbours too), a click beside the bar where there is no key adds one there (up to 8 of each kind),
	///         Delete removes the selected key (one of each kind stays);</item>
	///         <item>the selected key's color (HDR) or alpha, and its location.</item>
	///     </list>
	///     Edits go through the inspector's undo (one step per drag or click). Test anchors: <c>gradientbar</c>,
	///     <c>gradientkey:c0</c> (color keys) and <c>gradientkey:a0</c> (alpha keys).
	/// </summary>
	internal static class GradientEditor
	{
		#region PrivateFields

		private const float BarHeight = 28f;
		private const float KeyRow = 16f;
		private const float KeySize = 6f;

		private static readonly string[] ModeNames = { "Blend", "Fixed", "Perceptual Blend" };

		// One gradient editor is open at a time (a popup): its selection and drag.
		private static Gradient editing;
		private static int selected = -1;
		private static bool selectedAlpha;
		private static bool dragging;
		private static bool dragChanged;

		#endregion

		#region PublicMethods

		/// <summary>The field line: label, preview, popup. True when the gradient changed (or was created).</summary>
		public static bool DrawField(string label, ref object value, FieldInfo info, InspectorContext context)
		{
			RowDecoration row = FieldDrawer.BeginLine(label, info, context);
			var gradient = value as Gradient;
			bool changed = false;

			var size = new NVector2(Math.Max(40f, ImGui.CalcItemWidth()), ImGui.GetFrameHeight());
			NVector2 min = ImGui.GetCursorScreenPos();
			bool clicked = ImGui.InvisibleButton("##gradientfield", size);
			bool hovered = ImGui.IsItemHovered();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			if (gradient != null)
			{
				DrawPreview(draw, gradient, min, min + size);
			}
			else
			{
				draw.AddRectFilled(min, min + size, ImGui.GetColorU32(ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
				draw.AddText(min + new NVector2(6f, ImGui.GetStyle().FramePadding.Y), ImGui.GetColorU32(ImGuiCol.TextDisabled), "(none: click to edit)");
			}

			draw.AddRect(min, min + size, ImGui.GetColorU32(hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Border));
			if (hovered)
			{
				ImGui.SetTooltip("Click to edit the gradient");
			}

			if (clicked)
			{
				ImGui.OpenPopup("##gradienteditor");
				if (gradient == null)
				{
					context.BeginEdit();
					gradient = new Gradient();
					value = gradient;
					changed = true;
					context.EndEdit();
				}

				Open(gradient);
			}

			if (gradient != null && ImGui.BeginPopup("##gradienteditor"))
			{
				if (!ReferenceEquals(editing, gradient))
				{
					Open(gradient);
				}

				changed |= DrawEditor(gradient, context);
				ImGui.EndPopup();
			}

			FieldDrawer.EndLine(row);
			return changed;
		}

		/// <summary>The gradient over a checkerboard (so alpha shows), fitted into a rectangle.</summary>
		public static void DrawPreview(ImDrawListPtr draw, Gradient gradient, NVector2 min, NVector2 max)
		{
			DrawCheckerboard(draw, min, max);
			float width = max.X - min.X;
			int steps = Math.Clamp((int)(width / 3f), 8, 256);
			uint left = ToU32(gradient.Evaluate(0f));
			for (int i = 0; i < steps; i++)
			{
				float x0 = min.X + width * i / steps;
				float x1 = min.X + width * (i + 1) / steps;
				if (gradient.mode == GradientMode.Fixed)
				{
					// Steps stay steps: one color per column.
					draw.AddRectFilled(new NVector2(x0, min.Y), new NVector2(x1, max.Y), ToU32(gradient.Evaluate((i + 0.5f) / steps)));
					continue;
				}

				uint right = ToU32(gradient.Evaluate((i + 1f) / steps));
				draw.AddRectFilledMultiColor(new NVector2(x0, min.Y), new NVector2(x1, max.Y), left, right, right, left);
				left = right;
			}
		}

		#endregion

		#region PrivateMethods

		private static void Open(Gradient gradient)
		{
			editing = gradient;
			selected = -1;
			dragging = false;
			dragChanged = false;
		}

		private static bool DrawEditor(Gradient gradient, InspectorContext context)
		{
			bool changed = false;
			int mode = (int)gradient.mode;
			ImGui.SetNextItemWidth(160f);
			if (ImGui.Combo("Mode", ref mode, ModeNames, ModeNames.Length))
			{
				context.BeginEdit();
				gradient.mode = (GradientMode)mode;
				context.EndEdit();
				changed = true;
			}

			changed |= DrawBar(gradient, context);
			changed |= DrawSelectedKey(gradient, context);
			return changed;
		}

		// ---------------------------------------------------------------- bar and keys

		private static bool DrawBar(Gradient gradient, InspectorContext context)
		{
			bool changed = false;
			float width = Math.Max(380f, ImGui.GetContentRegionAvail().X);
			NVector2 origin = ImGui.GetCursorScreenPos();
			var barMin = new NVector2(origin.X + KeySize, origin.Y + KeyRow);
			var barMax = new NVector2(origin.X + width - KeySize, barMin.Y + BarHeight);
			ImGui.InvisibleButton("##gradientbar", new NVector2(width, KeyRow * 2f + BarHeight));
			bool hovered = ImGui.IsItemHovered();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			DrawPreview(draw, gradient, barMin, barMax);
			draw.AddRect(barMin, barMax, ImGui.GetColorU32(ImGuiCol.Border));
			if (TestAnchors.Enabled)
			{
				TestAnchors.Record("gradientbar", barMin, barMax);
			}

			NVector2 mouse = ImGui.GetMousePos();
			float barWidth = barMax.X - barMin.X;
			float mouseTime = Math.Clamp((mouse.X - barMin.X) / barWidth, 0f, 1f);

			// Alpha keys above the bar, color keys below it; the key under the mouse.
			int hitKey = -1;
			bool hitAlpha = false;
			GradientAlphaKey[] alphaKeys = gradient.alphaKeys;
			GradientColorKey[] colorKeys = gradient.colorKeys;
			for (int i = 0; i < alphaKeys.Length; i++)
			{
				float x = barMin.X + barWidth * alphaKeys[i].time;
				float a = Math.Clamp(alphaKeys[i].alpha, 0f, 1f);
				DrawKey(draw, x, barMin.Y, true, ImGui.GetColorU32(new NVector4(a, a, a, 1f)), selectedAlpha && selected == i, "gradientkey:a" + i);
				if (hovered && Math.Abs(mouse.X - x) <= KeySize + 1f && mouse.Y < barMin.Y)
				{
					hitKey = i;
					hitAlpha = true;
				}
			}

			for (int i = 0; i < colorKeys.Length; i++)
			{
				float x = barMin.X + barWidth * colorKeys[i].time;
				Color4 c = colorKeys[i].color;
				DrawKey(draw, x, barMax.Y, false, ToU32(new Color4(c.R, c.G, c.B, 1f)), !selectedAlpha && selected == i, "gradientkey:c" + i);
				if (hovered && Math.Abs(mouse.X - x) <= KeySize + 1f && mouse.Y > barMax.Y)
				{
					hitKey = i;
					hitAlpha = false;
				}
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				if (hitKey >= 0)
				{
					selected = hitKey;
					selectedAlpha = hitAlpha;
					dragging = true;
					dragChanged = false;
				}
				else if (mouse.Y < barMin.Y || mouse.Y > barMax.Y)
				{
					// Beside the bar: a new key there with the gradient's value (ignored at 8 keys).
					bool alpha = mouse.Y < barMin.Y;
					Color4 here = gradient.Evaluate(mouseTime);
					if ((alpha ? alphaKeys.Length : colorKeys.Length) < Gradient.MaxKeys)
					{
						context.BeginEdit();
						if (alpha)
						{
							var key = new GradientAlphaKey(here.A, mouseTime);
							gradient.alphaKeys = Append(alphaKeys, key);
							selected = Array.IndexOf(gradient.alphaKeys, key);
						}
						else
						{
							var key = new GradientColorKey(new Color4(here.R, here.G, here.B, 1f), mouseTime);
							gradient.colorKeys = Append(colorKeys, key);
							selected = Array.IndexOf(gradient.colorKeys, key);
						}

						context.EndEdit();
						selectedAlpha = alpha;
						dragging = true;
						dragChanged = false;
						changed = true;
					}
				}
			}

			if (dragging)
			{
				if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
				{
					if (dragChanged)
					{
						context.EndEdit();
					}

					dragging = false;
					dragChanged = false;
				}
				else if (ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f) && selected >= 0)
				{
					if (!dragChanged)
					{
						context.BeginEdit();
						dragChanged = true;
					}

					changed |= MoveSelected(gradient, mouseTime);
				}
			}

			if (selected >= 0 && (ImGui.IsKeyPressed(ImGuiKey.Delete) || ImGui.IsKeyPressed(ImGuiKey.Backspace)) && ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive())
			{
				changed |= RemoveSelected(gradient, context);
			}

			return changed;
		}

		// A key: a triangle pointing at the bar and a swatch of its value.
		private static void DrawKey(ImDrawListPtr draw, float x, float barEdge, bool above, uint fill, bool isSelected, string anchor)
		{
			float direction = above ? -1f : 1f;
			var tip = new NVector2(x, barEdge);
			var leftCorner = new NVector2(x - KeySize, barEdge + direction * KeySize);
			var rightCorner = new NVector2(x + KeySize, barEdge + direction * KeySize);
			var swatchMin = new NVector2(x - KeySize, Math.Min(barEdge + direction * KeySize, barEdge + direction * (KeyRow - 1f)));
			var swatchMax = new NVector2(x + KeySize, Math.Max(barEdge + direction * KeySize, barEdge + direction * (KeyRow - 1f)));
			uint outline = isSelected ? ImGui.GetColorU32(EditorStyle.Selection) : ImGui.GetColorU32(ImGuiCol.Text);
			draw.AddTriangleFilled(tip, leftCorner, rightCorner, outline);
			draw.AddRectFilled(swatchMin, swatchMax, fill);
			draw.AddRect(swatchMin, swatchMax, outline, 0f, ImDrawFlags.None, isSelected ? 2f : 1f);
			if (TestAnchors.Enabled)
			{
				TestAnchors.Record(anchor, swatchMin, swatchMax);
			}
		}

		// Moves the selected key to a time; it may pass its neighbours (the keys stay sorted and the selection follows it).
		private static bool MoveSelected(Gradient gradient, float time)
		{
			if (selectedAlpha)
			{
				GradientAlphaKey[] keys = gradient.alphaKeys;
				if (selected >= keys.Length || Math.Abs(keys[selected].time - time) < 1e-5f)
				{
					return false;
				}

				keys[selected].time = time;
				GradientAlphaKey moved = keys[selected];
				gradient.alphaKeys = keys;
				selected = Array.IndexOf(gradient.alphaKeys, moved);
			}
			else
			{
				GradientColorKey[] keys = gradient.colorKeys;
				if (selected >= keys.Length || Math.Abs(keys[selected].time - time) < 1e-5f)
				{
					return false;
				}

				keys[selected].time = time;
				GradientColorKey moved = keys[selected];
				gradient.colorKeys = keys;
				selected = Array.IndexOf(gradient.colorKeys, moved);
			}

			return true;
		}

		private static bool RemoveSelected(Gradient gradient, InspectorContext context)
		{
			int count = selectedAlpha ? gradient.alphaKeyCount : gradient.colorKeyCount;
			if (selected < 0 || selected >= count || count <= 1)
			{
				return false;
			}

			context.BeginEdit();
			if (selectedAlpha)
			{
				gradient.alphaKeys = Remove(gradient.alphaKeys, selected);
			}
			else
			{
				gradient.colorKeys = Remove(gradient.colorKeys, selected);
			}

			context.EndEdit();
			selected = -1;
			return true;
		}

		// ---------------------------------------------------------------- selected key

		private static bool DrawSelectedKey(Gradient gradient, InspectorContext context)
		{
			int count = selectedAlpha ? gradient.alphaKeyCount : gradient.colorKeyCount;
			if (selected < 0 || selected >= count)
			{
				ImGui.TextDisabled("Click a key to edit it; click above or below the bar to add one.");
				return false;
			}

			bool changed = false;
			float fieldWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - 190f);
			if (selectedAlpha)
			{
				GradientAlphaKey[] keys = gradient.alphaKeys;
				float alpha = keys[selected].alpha;
				ImGui.SetNextItemWidth(fieldWidth);
				bool edited = ImGui.SliderFloat("Alpha", ref alpha, 0f, 1f, "%.3f");
				context.Track(edited);
				if (edited)
				{
					keys[selected].alpha = alpha;
					gradient.alphaKeys = keys;
					changed = true;
				}
			}
			else
			{
				GradientColorKey[] keys = gradient.colorKeys;
				Color4 color = keys[selected].color;
				var n = new NVector4(color.R, color.G, color.B, 1f);
				ImGui.SetNextItemWidth(fieldWidth);
				bool edited = ImGui.ColorEdit4("Color", ref n, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.Hdr);
				context.Track(edited);
				if (edited)
				{
					keys[selected].color = new Color4(n.X, n.Y, n.Z, 1f);
					gradient.colorKeys = keys;
					changed = true;
				}
			}

			float location = (selectedAlpha ? gradient.alphaKeys[selected].time : gradient.colorKeys[selected].time) * 100f;
			ImGui.SetNextItemWidth(fieldWidth);
			bool moved = ImGui.DragFloat("Location", ref location, 0.2f, 0f, 100f, "%.1f %%");
			context.Track(moved);
			if (moved)
			{
				changed |= MoveSelected(gradient, Math.Clamp(location / 100f, 0f, 1f));
			}

			ImGui.BeginDisabled(count <= 1);
			if (ImGui.Button("Delete key"))
			{
				changed |= RemoveSelected(gradient, context);
			}

			ImGui.EndDisabled();
			return changed;
		}

		// ---------------------------------------------------------------- helpers

		private static void DrawCheckerboard(ImDrawListPtr draw, NVector2 min, NVector2 max)
		{
			float cell = Math.Max(4f, (max.Y - min.Y) * 0.5f);
			uint light = ImGui.GetColorU32(new NVector4(0.8f, 0.8f, 0.8f, 1f));
			uint dark = ImGui.GetColorU32(new NVector4(0.55f, 0.55f, 0.55f, 1f));
			draw.AddRectFilled(min, max, light);
			int row = 0;
			for (float y = min.Y; y < max.Y; y += cell, row++)
			{
				int column = 0;
				for (float x = min.X; x < max.X; x += cell, column++)
				{
					if (((row + column) & 1) == 1)
					{
						draw.AddRectFilled(new NVector2(x, y), new NVector2(Math.Min(x + cell, max.X), Math.Min(y + cell, max.Y)), dark);
					}
				}
			}
		}

		private static uint ToU32(Color4 color)
		{
			return ImGui.GetColorU32(new NVector4(Math.Clamp(color.R, 0f, 1f), Math.Clamp(color.G, 0f, 1f), Math.Clamp(color.B, 0f, 1f), Math.Clamp(color.A, 0f, 1f)));
		}

		private static T[] Append<T>(T[] keys, T key)
		{
			var result = new T[keys.Length + 1];
			Array.Copy(keys, result, keys.Length);
			result[keys.Length] = key;
			return result;
		}

		private static T[] Remove<T>(T[] keys, int index)
		{
			var result = new T[keys.Length - 1];
			Array.Copy(keys, 0, result, 0, index);
			Array.Copy(keys, index + 1, result, index, keys.Length - index - 1);
			return result;
		}

		#endregion
	}
}
