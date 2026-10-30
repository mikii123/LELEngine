using System;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Below a LOD group's fields, as Unity draws it: the levels as a bar over the screen height they cover (100 % on
	///     the left, the last level's height to the culled part on the right), the level the last frame drew, and Recalculate
	///     Bounds (reference point and size from the levels' renderers).
	/// </summary>
	internal static class LODGroupInspector
	{
		#region PrivateFields

		private static readonly NVector4[] LevelColors =
		{
			new NVector4(0.24f, 0.43f, 0.22f, 1f), new NVector4(0.20f, 0.36f, 0.50f, 1f), new NVector4(0.19f, 0.47f, 0.53f, 1f),
			new NVector4(0.38f, 0.31f, 0.51f, 1f), new NVector4(0.51f, 0.38f, 0.22f, 1f), new NVector4(0.43f, 0.26f, 0.26f, 1f)
		};

		private static readonly NVector4 CulledColor = new NVector4(0.31f, 0.11f, 0.11f, 1f);

		#endregion

		#region PublicMethods

		public static bool Draw(LODGroup group, InspectorContext context)
		{
			bool changed = false;
			ImGui.SeparatorText("Levels");
			LOD[] lods = group.GetLODs();
			if (lods.Length == 0)
			{
				ImGui.TextDisabled("No levels: every renderer draws.");
			}
			else
			{
				DrawBar(group, lods);
			}

			if (group.CurrentLOD >= 0)
			{
				ImGui.TextDisabled($"Last frame: LOD {group.CurrentLOD} (height on screen {group.CurrentRelativeHeight * 100f:0.#} % x bias {QualitySettings.lodBias:0.##})");
			}
			else if (group.isActiveAndEnabled && lods.Length > 0)
			{
				ImGui.TextDisabled("Last frame: culled (smaller than the last level) or not rendered.");
			}

			if (EditorGui.Button("Recalculate Bounds", new NVector2(ImGui.GetContentRegionAvail().X, 0)))
			{
				context.Editor.Edits.Begin(context.UndoTarget);
				group.RecalculateBounds();
				context.Editor.Edits.RequestCommit();
				changed = true;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Reference point and size from the bounds of every level's renderers.");
			}

			return changed;
		}

		#endregion

		#region PrivateMethods

		private static void DrawBar(LODGroup group, LOD[] lods)
		{
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector2 origin = ImGui.GetCursorScreenPos();
			float width = Math.Max(60f, ImGui.GetContentRegionAvail().X);
			float height = ImGui.GetTextLineHeight() * 2.6f;
			float left = 1f;
			for (int i = 0; i <= lods.Length; i++)
			{
				bool culled = i == lods.Length;
				float right = culled ? 0f : Math.Clamp(lods[i].screenRelativeTransitionHeight, 0f, left);
				float x0 = origin.X + (1f - left) * width;
				float x1 = origin.X + (1f - right) * width;
				if (x1 - x0 >= 1f)
				{
					NVector4 color = culled ? CulledColor : LevelColors[i % LevelColors.Length];
					if (i == group.CurrentLOD || culled && group.CurrentLOD < 0 && group.isActiveAndEnabled)
					{
						color = new NVector4(Math.Min(1f, color.X * 1.5f), Math.Min(1f, color.Y * 1.5f), Math.Min(1f, color.Z * 1.5f), 1f);
					}

					draw.AddRectFilled(new NVector2(x0, origin.Y), new NVector2(x1 - 1f, origin.Y + height), ImGui.GetColorU32(color));
					string label = culled ? "Culled" : $"LOD {i}\n{lods[i].screenRelativeTransitionHeight * 100f:0.#} %";
					NVector2 size = ImGui.CalcTextSize(label);
					if (size.X <= x1 - x0 - 4f)
					{
						draw.AddText(new NVector2(x0 + 4f, origin.Y + (height - size.Y) * 0.5f), ImGui.GetColorU32(ImGuiCol.Text), label);
					}
				}

				left = right;
			}

			ImGui.Dummy(new NVector2(width, height));
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Levels by height on screen (a share of the view's height, times the project's LOD bias):\n" +
				                 "each draws down to its own height; below the last one the group is culled.");
			}
		}

		#endregion
	}
}
