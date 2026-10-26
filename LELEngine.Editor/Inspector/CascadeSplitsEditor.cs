using System;
using Hexa.NET.ImGui;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The shadow cascade splits as a bar, like Unity's: one colored segment per cascade showing its share of the
	///     shadow distance (percent, and meters of the active scene's distance), handles between the segments to drag
	///     the splits, the exact values below. Right click on the bar resets the splits. Edits are undoable (the
	///     context's undo target, the project settings).
	/// </summary>
	internal static class CascadeSplitsEditor
	{
		#region PrivateFields

		private static readonly NVector4[] SegmentColors =
		{
			new NVector4(0.33f, 0.45f, 0.66f, 1f),
			new NVector4(0.36f, 0.58f, 0.40f, 1f),
			new NVector4(0.64f, 0.57f, 0.33f, 1f),
			new NVector4(0.64f, 0.37f, 0.37f, 1f)
		};

		/// <summary>Smallest share of the distance a cascade keeps while its neighbours are dragged.</summary>
		private const float MinShare = 0.01f;

		private const float HandleWidth = 9f;

		/// <summary>Where on the handle the drag started (mouse x minus the split's x), so the split does not jump.</summary>
		private static float grabOffset;

		#endregion

		#region PublicMethods

		/// <summary>Draws the bar and the split values of <paramref name="settings" />; true when a split changed.</summary>
		public static bool Draw(ShadowSettings settings, InspectorContext context)
		{
			int count = settings.Cascades == ShadowCascadeCount.Four ? 4 : settings.Cascades == ShadowCascadeCount.Two ? 2 : 1;
			Span<float> splits = stackalloc float[3];
			Read(settings, count, splits);
			float distance = settings.Distance;

			ImGui.PushID("cascadesplits");
			ImGui.Spacing();
			ImGui.TextUnformatted("Cascade Splits");
			ImGui.SameLine();
			ImGui.TextDisabled("(shadow distance " + distance.ToString("0.#") + " m, set per scene in the Lighting panel)");

			bool changed = DrawBar(settings, context, count, splits, distance);
			changed |= DrawValues(context, count, splits, distance);
			if (changed)
			{
				Write(settings, count, splits);
			}

			ImGui.PopID();
			return changed;
		}

		#endregion

		#region PrivateMethods

		private static bool DrawBar(ShadowSettings settings, InspectorContext context, int count, Span<float> splits, float distance)
		{
			float width = Math.Max(60f, ImGui.GetContentRegionAvail().X);
			float lineHeight = ImGui.GetTextLineHeight();
			float height = lineHeight * 2f + 10f;
			NVector2 min = ImGui.GetCursorScreenPos();
			NVector2 max = min + new NVector2(width, height);

			// Reserve the bar's space; the handles are placed on it afterwards.
			ImGui.Dummy(new NVector2(width, height));
			NVector2 after = ImGui.GetCursorScreenPos();
			bool barHovered = ImGui.IsItemHovered();

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			uint text = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.95f));
			uint textDim = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.7f));
			for (int i = 0; i < count; i++)
			{
				float start = i == 0 ? 0f : splits[i - 1];
				float end = i == count - 1 ? 1f : splits[i];
				var segmentMin = new NVector2(min.X + width * start, min.Y);
				var segmentMax = new NVector2(min.X + width * end, max.Y);
				draw.AddRectFilled(segmentMin, segmentMax, ImGui.GetColorU32(SegmentColors[i]));

				// Index and share on the first line, the range in meters on the second, clipped to the segment.
				draw.PushClipRect(segmentMin, segmentMax, true);
				draw.AddText(new NVector2(segmentMin.X + 5f, min.Y + 4f), text, i + "   " + ((end - start) * 100f).ToString("0.#") + "%");
				draw.AddText(new NVector2(segmentMin.X + 5f, min.Y + 6f + lineHeight), textDim, (start * distance).ToString("0.#") + " - " + (end * distance).ToString("0.#") + " m");
				draw.PopClipRect();
			}

			draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));

			bool changed = false;
			NVector2 mouse = ImGui.GetMousePos();
			for (int k = 0; k < count - 1; k++)
			{
				float x = min.X + width * splits[k];
				ImGui.SetCursorScreenPos(new NVector2(x - HandleWidth * 0.5f, min.Y));
				ImGui.PushID(k);
				EditorGui.InvisibleButton("##handle", new NVector2(HandleWidth, height));
				bool hovered = ImGui.IsItemHovered();
				bool active = ImGui.IsItemActive();

				if (ImGui.IsItemActivated())
				{
					grabOffset = mouse.X - x;
					context.Editor.Edits.Begin(context.UndoTarget);
				}

				if (active)
				{
					// Between the neighbouring splits, every cascade keeping a minimal share.
					float low = (k == 0 ? 0f : splits[k - 1]) + MinShare;
					float high = (k == count - 2 ? 1f : splits[k + 1]) - MinShare;
					float t = Math.Clamp((mouse.X - grabOffset - min.X) / width, low, high);
					if (Math.Abs(t - splits[k]) > 1e-5f)
					{
						splits[k] = t;
						x = min.X + width * t;
						changed = true;
					}
				}

				if (ImGui.IsItemDeactivated())
				{
					context.Editor.Edits.RequestCommit();
				}

				if (hovered || active)
				{
					ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
					ImGui.SetTooltip("Cascade " + (k + 1) + " starts at " + (splits[k] * 100f).ToString("0.#") + "% (" + (splits[k] * distance).ToString("0.##") + " m)");
				}

				float alpha = active ? 1f : hovered ? 0.9f : 0.6f;
				draw.AddRectFilled(new NVector2(x - 1.5f, min.Y), new NVector2(x + 1.5f, max.Y), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, alpha)));
				ImGui.PopID();
			}

			ImGui.SetCursorScreenPos(after);

			// Right click on the bar: back to the defaults.
			if (barHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				ImGui.OpenPopup("##splitsmenu");
			}

			if (ImGui.BeginPopup("##splitsmenu"))
			{
				if (EditorMenu.Item("Reset Splits"))
				{
					var defaults = new ShadowSettings();
					context.Editor.Edits.Begin(context.UndoTarget);
					settings.TwoCascadeSplit = defaults.TwoCascadeSplit;
					settings.FourCascadeSplits = defaults.FourCascadeSplits;
					context.Editor.Edits.RequestCommit();
					Read(settings, count, splits);
					changed = true;
				}

				ImGui.EndPopup();
			}

			return changed;
		}

		/// <summary>Each split as an exact value: percent of the shadow distance, its meters in the scene alongside.</summary>
		private static bool DrawValues(InspectorContext context, int count, Span<float> splits, float distance)
		{
			if (count == 1)
			{
				ImGui.TextDisabled("One cascade covers the whole shadow distance.");
				return false;
			}

			bool changed = false;
			for (int k = 0; k < count - 1; k++)
			{
				float low = (k == 0 ? 0f : splits[k - 1]) + MinShare;
				float high = (k == count - 2 ? 1f : splits[k + 1]) - MinShare;
				FieldDrawer.Label("Cascade " + (k + 1) + " Starts At", null, context);
				float percent = splits[k] * 100f;
				string format = "%.1f%%   (" + (splits[k] * distance).ToString("0.0#") + " m)";
				bool edited = EditorGui.DragFloat("##split" + k, ref percent, 0.1f, low * 100f, high * 100f, format);
				context.Track(edited);
				if (edited)
				{
					splits[k] = Math.Clamp(percent / 100f, low, high);
					changed = true;
				}
			}

			return changed;
		}

		private static void Read(ShadowSettings settings, int count, Span<float> splits)
		{
			if (count == 2)
			{
				splits[0] = settings.TwoCascadeSplit;
			}
			else if (count == 4)
			{
				splits[0] = settings.FourCascadeSplits.X;
				splits[1] = settings.FourCascadeSplits.Y;
				splits[2] = settings.FourCascadeSplits.Z;
			}

			// Stored values may be out of order (hand edits): show them as the shadow pass uses them, increasing.
			for (int k = 0; k < count - 1; k++)
			{
				float low = (k == 0 ? 0f : splits[k - 1]) + MinShare;
				float high = 1f - MinShare * (count - 1 - k);
				splits[k] = Math.Clamp(splits[k], low, Math.Max(low, high));
			}
		}

		private static void Write(ShadowSettings settings, int count, Span<float> splits)
		{
			if (count == 2)
			{
				settings.TwoCascadeSplit = splits[0];
			}
			else if (count == 4)
			{
				settings.FourCascadeSplits = new Vector3(splits[0], splits[1], splits[2]);
			}
		}

		#endregion
	}
}
