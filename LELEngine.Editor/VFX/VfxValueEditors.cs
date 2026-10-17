using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.VFX;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.VFX
{
	/// <summary>
	///     Widgets for <see cref="VfxValue" />s of every type: numbers, vectors, colors, asset fields and the gradient
	///     and curve editors. <see cref="Draw" /> returns true while the value changes; <see cref="EditState" /> tells
	///     when an edit begins and ends so the caller can capture undo snapshots once per drag.
	/// </summary>
	internal static class VfxValueEditors
	{
		#region PublicFields

		public enum EditState
		{
			None,
			Began,
			Ended
		}

		/// <summary>Set by <see cref="Draw" />: whether the last widget began or ended an edit (undo capture).</summary>
		public static EditState LastEditState;

		#endregion

		#region PrivateFields

		private static int selectedGradientKey = -1;
		private static bool selectedGradientAlpha;
		private static int selectedCurveKey = -1;
		private static object dragTarget;
		private static bool dragging;

		#endregion

		#region PublicMethods

		/// <summary>Draws an editor for the value in the current line (the caller placed the label); true while it changes.</summary>
		public static bool Draw(string id, VfxValue value, VfxSlotDefinition slot = null)
		{
			LastEditState = EditState.None;
			if (value == null)
			{
				ImGui.TextDisabled("null");
				return false;
			}

			ImGui.PushID(id);
			bool changed;
			try
			{
				changed = DrawInner(value, slot);
			}
			finally
			{
				ImGui.PopID();
			}

			return changed;
		}

		/// <summary>A one-line preview of a value for the canvas.</summary>
		public static string Preview(VfxValue value)
		{
			if (value == null)
			{
				return "";
			}

			switch (value.Type)
			{
				case VfxValueType.Gradient: return "gradient";
				case VfxValueType.Curve: return "curve";
				default: return value.ToString();
			}
		}

		#endregion

		#region PrivateMethods

		private static void Track(bool changed)
		{
			if (ImGui.IsItemActivated() || (changed && !ImGui.IsItemActive()))
			{
				LastEditState = EditState.Began;
			}

			if (ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive()))
			{
				LastEditState = LastEditState == EditState.Began ? EditState.Ended : EditState.Ended;
			}
		}

		private static bool DrawInner(VfxValue value, VfxSlotDefinition slot)
		{
			bool changed = false;
			switch (value.Type)
			{
				case VfxValueType.Float:
				{
					float f = value.Float;
					if (slot != null && slot.HasRange)
					{
						changed = ImGui.SliderFloat("##v", ref f, slot.Min, slot.Max);
					}
					else
					{
						changed = ImGui.DragFloat("##v", ref f, Math.Max(0.005f, Math.Abs(f) * 0.01f));
					}

					Track(changed);
					if (changed) value.Float = f;
					break;
				}
				case VfxValueType.Int:
				{
					int i = value.Int;
					changed = slot != null && slot.HasRange ? ImGui.SliderInt("##v", ref i, (int)slot.Min, (int)slot.Max) : ImGui.DragInt("##v", ref i, 0.2f);
					Track(changed);
					if (changed) value.Int = i;
					break;
				}
				case VfxValueType.Uint:
				{
					int i = (int)Math.Min(int.MaxValue, value.Uint);
					changed = ImGui.DragInt("##v", ref i, 0.2f);
					Track(changed);
					if (changed) value.Uint = (uint)Math.Max(0, i);
					break;
				}
				case VfxValueType.Bool:
				{
					bool b = value.Bool;
					changed = ImGui.Checkbox("##v", ref b);
					Track(changed);
					if (changed) value.Bool = b;
					break;
				}
				case VfxValueType.Vector2:
				{
					var n = new NVector2(value.Vector.X, value.Vector.Y);
					changed = ImGui.DragFloat2("##v", ref n, 0.05f);
					Track(changed);
					if (changed) value.Vector2 = new Vector2(n.X, n.Y);
					break;
				}
				case VfxValueType.Vector3:
				{
					var n = new NVector3(value.Vector.X, value.Vector.Y, value.Vector.Z);
					changed = ImGui.DragFloat3("##v", ref n, 0.05f);
					Track(changed);
					if (changed) value.Vector3 = new Vector3(n.X, n.Y, n.Z);
					break;
				}
				case VfxValueType.Vector4:
				{
					var n = new NVector4(value.Vector.X, value.Vector.Y, value.Vector.Z, value.Vector.W);
					changed = ImGui.DragFloat4("##v", ref n, 0.05f);
					Track(changed);
					if (changed) value.Vector = new Vector4(n.X, n.Y, n.Z, n.W);
					break;
				}
				case VfxValueType.Color:
				{
					var n = new NVector4(value.Vector.X, value.Vector.Y, value.Vector.Z, value.Vector.W);
					ImGuiColorEditFlags flags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.Hdr;
					changed = ImGui.ColorEdit4("##v", ref n, flags);
					Track(changed);
					if (changed) value.Vector = new Vector4(n.X, n.Y, n.Z, n.W);
					break;
				}
				case VfxValueType.Gradient:
					value.Gradient = value.Gradient ?? VfxGradient.WhiteToTransparent();
					changed = DrawGradient(value.Gradient);
					break;
				case VfxValueType.Curve:
					value.Curve = value.Curve ?? VfxCurve.Linear(1f, 0f);
					changed = DrawCurve(value.Curve);
					break;
				case VfxValueType.Texture2D:
				{
					object asset = value.Texture;
					changed = AssetField.Draw(typeof(Texture2D), ref asset, path => MaterialEditor.ShapeMatches(path, typeof(Texture2D)));
					if (changed)
					{
						LastEditState = EditState.Began;
						value.Texture = asset as Texture2D;
						LastEditState = EditState.Ended;
					}

					break;
				}
				case VfxValueType.Texture3D:
				{
					object asset = value.Texture3D;
					changed = AssetField.Draw(typeof(Texture3D), ref asset, path => MaterialEditor.ShapeMatches(path, typeof(Texture3D)));
					if (changed)
					{
						value.Texture3D = asset as Texture3D;
						LastEditState = EditState.Ended;
					}

					break;
				}
				case VfxValueType.Mesh:
				{
					object asset = value.Mesh;
					changed = AssetField.Draw(typeof(Mesh), ref asset);
					if (changed)
					{
						value.Mesh = asset as Mesh;
						LastEditState = EditState.Ended;
					}

					break;
				}
				default:
					ImGui.TextDisabled(value.Type.ToString());
					break;
			}

			return changed;
		}

		// ---------------------------------------------------------------- gradient

		private static bool DrawGradient(VfxGradient gradient)
		{
			bool changed = false;
			float width = Math.Max(120f, ImGui.CalcItemWidth());
			const float barHeight = 22f;
			const float keyHeight = 12f;
			NVector2 origin = ImGui.GetCursorScreenPos();
			var barMin = new NVector2(origin.X, origin.Y + keyHeight);
			var barMax = new NVector2(origin.X + width, origin.Y + keyHeight + barHeight);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();

			// Checkerboard, then the gradient in 64 steps.
			draw.AddRectFilled(barMin, barMax, ImGui.GetColorU32(new NVector4(0.35f, 0.35f, 0.35f, 1f)));
			const int steps = 64;
			for (int i = 0; i < steps; i++)
			{
				float t0 = i / (float)steps;
				float t1 = (i + 1) / (float)steps;
				Color4 c = gradient.Evaluate((t0 + t1) * 0.5f);
				var min = new NVector2(barMin.X + width * t0, barMin.Y);
				var max = new NVector2(barMin.X + width * t1, barMax.Y);
				draw.AddRectFilled(min, max, ImGui.GetColorU32(new NVector4(Math.Clamp(c.R, 0f, 1f), Math.Clamp(c.G, 0f, 1f), Math.Clamp(c.B, 0f, 1f), Math.Clamp(c.A, 0f, 1f))));
			}

			draw.AddRect(barMin, barMax, ImGui.GetColorU32(ImGuiCol.Border));

			// Alpha keys above the bar, color keys below.
			ImGui.InvisibleButton("##gradient", new NVector2(width, keyHeight * 2 + barHeight));
			bool hovered = ImGui.IsItemHovered();
			NVector2 mouse = ImGui.GetMousePos();
			int hitKey = -1;
			bool hitAlpha = false;
			for (int pass = 0; pass < 2; pass++)
			{
				bool alpha = pass == 1;
				List<VfxGradientKey> keys = alpha ? gradient.AlphaKeys : gradient.ColorKeys;
				for (int i = 0; i < keys.Count; i++)
				{
					float x = barMin.X + width * Math.Clamp(keys[i].Time, 0f, 1f);
					float y = alpha ? origin.Y + keyHeight * 0.5f : barMax.Y + keyHeight * 0.5f;
					bool selected = selectedGradientKey == i && selectedGradientAlpha == alpha && dragTarget == gradient;
					Color4 c = keys[i].Color;
					uint fill = alpha ? ImGui.GetColorU32(new NVector4(c.A, c.A, c.A, 1f)) : ImGui.GetColorU32(new NVector4(Math.Clamp(c.R, 0f, 1f), Math.Clamp(c.G, 0f, 1f), Math.Clamp(c.B, 0f, 1f), 1f));
					uint outline = selected ? ImGui.GetColorU32(EditorStyle.Selection) : ImGui.GetColorU32(ImGuiCol.Text);
					if (alpha)
					{
						draw.AddTriangleFilled(new NVector2(x, y + 5), new NVector2(x - 5, y - 5), new NVector2(x + 5, y - 5), fill);
						draw.AddTriangle(new NVector2(x, y + 5), new NVector2(x - 5, y - 5), new NVector2(x + 5, y - 5), outline);
					}
					else
					{
						draw.AddTriangleFilled(new NVector2(x, y - 5), new NVector2(x - 5, y + 5), new NVector2(x + 5, y + 5), fill);
						draw.AddTriangle(new NVector2(x, y - 5), new NVector2(x - 5, y + 5), new NVector2(x + 5, y + 5), outline);
					}

					if (hovered && Math.Abs(mouse.X - x) <= 6f && Math.Abs(mouse.Y - y) <= 7f)
					{
						hitKey = i;
						hitAlpha = alpha;
					}
				}
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				if (hitKey >= 0)
				{
					selectedGradientKey = hitKey;
					selectedGradientAlpha = hitAlpha;
					dragTarget = gradient;
					dragging = true;
					LastEditState = EditState.Began;
				}
				else if (mouse.Y < barMin.Y || mouse.Y > barMax.Y)
				{
					// Click next to the keys: add one at that time.
					bool alpha = mouse.Y < barMin.Y;
					float t = Math.Clamp((mouse.X - barMin.X) / width, 0f, 1f);
					Color4 c = gradient.Evaluate(t);
					List<VfxGradientKey> keys = alpha ? gradient.AlphaKeys : gradient.ColorKeys;
					LastEditState = EditState.Began;
					keys.Add(new VfxGradientKey(t, alpha ? new Color4(1f, 1f, 1f, c.A) : new Color4(c.R, c.G, c.B, 1f)));
					gradient.Sort();
					selectedGradientKey = keys.FindIndex(k => k.Time == t);
					selectedGradientAlpha = alpha;
					dragTarget = gradient;
					dragging = true;
					changed = true;
				}
			}

			if (dragging && dragTarget == gradient)
			{
				if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
				{
					List<VfxGradientKey> keys = selectedGradientAlpha ? gradient.AlphaKeys : gradient.ColorKeys;
					if (selectedGradientKey >= 0 && selectedGradientKey < keys.Count && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f))
					{
						VfxGradientKey key = keys[selectedGradientKey];
						float t = Math.Clamp((mouse.X - barMin.X) / width, 0f, 1f);
						if (Math.Abs(t - key.Time) > 1e-4f)
						{
							key.Time = t;
							keys[selectedGradientKey] = key;
							changed = true;
						}
					}
				}
				else
				{
					dragging = false;
					LastEditState = EditState.Ended;
					List<VfxGradientKey> keys = selectedGradientAlpha ? gradient.AlphaKeys : gradient.ColorKeys;
					if (selectedGradientKey >= 0 && selectedGradientKey < keys.Count)
					{
						VfxGradientKey key = keys[selectedGradientKey];
						gradient.Sort();
						selectedGradientKey = keys.FindIndex(k => k.Time == key.Time && k.Color == key.Color);
					}
				}
			}

			// The selected key's value.
			if (dragTarget == gradient && selectedGradientKey >= 0)
			{
				List<VfxGradientKey> keys = selectedGradientAlpha ? gradient.AlphaKeys : gradient.ColorKeys;
				if (selectedGradientKey < keys.Count)
				{
					VfxGradientKey key = keys[selectedGradientKey];
					ImGui.SetNextItemWidth(width - 70f);
					bool keyChanged;
					if (selectedGradientAlpha)
					{
						float a = key.Color.A;
						keyChanged = ImGui.SliderFloat("##alpha", ref a, 0f, 1f, "alpha %.2f");
						Track(keyChanged);
						if (keyChanged) key.Color = new Color4(1f, 1f, 1f, a);
					}
					else
					{
						var n = new NVector4(key.Color.R, key.Color.G, key.Color.B, 1f);
						keyChanged = ImGui.ColorEdit4("##color", ref n, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.Hdr);
						Track(keyChanged);
						if (keyChanged) key.Color = new Color4(n.X, n.Y, n.Z, 1f);
					}

					if (keyChanged)
					{
						keys[selectedGradientKey] = key;
						changed = true;
					}

					ImGui.SameLine();
					ImGui.BeginDisabled(keys.Count <= 1);
					if (ImGui.SmallButton("Remove"))
					{
						LastEditState = EditState.Began;
						keys.RemoveAt(selectedGradientKey);
						selectedGradientKey = -1;
						changed = true;
						LastEditState = EditState.Ended;
					}

					ImGui.EndDisabled();
				}
			}
			else
			{
				ImGui.TextDisabled("Click a key to edit it, click above or below the bar to add one.");
			}

			return changed;
		}

		// ---------------------------------------------------------------- curve

		private static bool DrawCurve(VfxCurve curve)
		{
			bool changed = false;
			float width = Math.Max(120f, ImGui.CalcItemWidth());
			const float height = 90f;
			NVector2 min = ImGui.GetCursorScreenPos();
			var max = new NVector2(min.X + width, min.Y + height);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg));
			draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));

			// Vertical range: the keys' extent (at least 0..1).
			float low = 0f;
			float high = 1f;
			foreach (VfxCurveKey key in curve.Keys)
			{
				low = Math.Min(low, key.Value);
				high = Math.Max(high, key.Value);
			}

			float span = Math.Max(1e-3f, high - low);
			low -= span * 0.1f;
			high += span * 0.1f;
			span = high - low;
			NVector2 Point(float t, float v) => new NVector2(min.X + width * Math.Clamp(t, 0f, 1f), max.Y - height * (v - low) / span);

			// Grid lines at 0 and 1.
			draw.AddLine(Point(0f, 0f), Point(1f, 0f), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.15f)));
			draw.AddLine(Point(0f, 1f), Point(1f, 1f), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.15f)));

			const int samples = 64;
			NVector2 previous = Point(0f, curve.Evaluate(0f));
			for (int i = 1; i <= samples; i++)
			{
				float t = i / (float)samples;
				NVector2 current = Point(t, curve.Evaluate(t));
				draw.AddLine(previous, current, ImGui.GetColorU32(new NVector4(0.4f, 0.8f, 1f, 1f)), 1.5f);
				previous = current;
			}

			ImGui.InvisibleButton("##curve", new NVector2(width, height));
			bool hovered = ImGui.IsItemHovered();
			NVector2 mouse = ImGui.GetMousePos();
			int hitKey = -1;
			for (int i = 0; i < curve.Keys.Count; i++)
			{
				NVector2 p = Point(curve.Keys[i].Time, curve.Keys[i].Value);
				bool selected = selectedCurveKey == i && dragTarget == curve;
				draw.AddCircleFilled(p, 4f, selected ? ImGui.GetColorU32(EditorStyle.Selection) : ImGui.GetColorU32(ImGuiCol.Text));
				if (hovered && NVector2.DistanceSquared(mouse, p) <= 49f)
				{
					hitKey = i;
				}
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				if (hitKey >= 0)
				{
					selectedCurveKey = hitKey;
				}
				else
				{
					float t = Math.Clamp((mouse.X - min.X) / width, 0f, 1f);
					float v = low + (max.Y - mouse.Y) / height * span;
					LastEditState = EditState.Began;
					curve.Keys.Add(new VfxCurveKey(t, v));
					curve.SmoothTangents();
					selectedCurveKey = curve.Keys.FindIndex(k => k.Time == t && k.Value == v);
					changed = true;
				}

				dragTarget = curve;
				dragging = true;
				if (hitKey >= 0)
				{
					LastEditState = EditState.Began;
				}
			}

			if (dragging && dragTarget == curve)
			{
				if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
				{
					if (selectedCurveKey >= 0 && selectedCurveKey < curve.Keys.Count && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f))
					{
						VfxCurveKey key = curve.Keys[selectedCurveKey];
						float t = Math.Clamp((mouse.X - min.X) / width, 0f, 1f);
						float v = low + (max.Y - mouse.Y) / height * span;
						if (Math.Abs(t - key.Time) > 1e-4f || Math.Abs(v - key.Value) > 1e-4f)
						{
							key.Time = t;
							key.Value = v;
							curve.Keys[selectedCurveKey] = key;
							curve.SmoothTangents();
							selectedCurveKey = curve.Keys.FindIndex(k => k.Time == t);
							changed = true;
						}
					}
				}
				else
				{
					dragging = false;
					LastEditState = EditState.Ended;
				}
			}

			if (dragTarget == curve && selectedCurveKey >= 0 && selectedCurveKey < curve.Keys.Count)
			{
				VfxCurveKey key = curve.Keys[selectedCurveKey];
				float v = key.Value;
				ImGui.SetNextItemWidth(width - 70f);
				bool valueChanged = ImGui.DragFloat("##keyvalue", ref v, 0.01f);
				Track(valueChanged);
				if (valueChanged)
				{
					key.Value = v;
					curve.Keys[selectedCurveKey] = key;
					curve.SmoothTangents();
					changed = true;
				}

				ImGui.SameLine();
				ImGui.BeginDisabled(curve.Keys.Count <= 1);
				if (ImGui.SmallButton("Remove"))
				{
					LastEditState = EditState.Began;
					curve.Keys.RemoveAt(selectedCurveKey);
					selectedCurveKey = -1;
					changed = true;
					LastEditState = EditState.Ended;
				}

				ImGui.EndDisabled();
			}
			else
			{
				ImGui.TextDisabled("Click to add a key, drag keys to move them.");
			}

			return changed;
		}

		#endregion
	}
}
