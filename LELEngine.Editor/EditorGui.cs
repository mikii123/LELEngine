using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Small ImGui helpers.</summary>
	internal static class EditorGui
	{
		#region PublicMethods

		/// <summary>Reference to a GL texture for ImGui.Image.</summary>
		public static unsafe ImTextureRef Texture(int handle)
		{
			return new ImTextureRef(null, new ImTextureID((ulong)handle));
		}

		public static NVector2 ViewportCenter()
		{
			ImGuiViewportPtr viewport = ImGui.GetMainViewport();
			return viewport.Pos + viewport.Size * 0.5f;
		}

		/// <summary>
		///     Button showing a vector icon drawn with the draw list (the editor font has no symbol glyphs).
		///     <paramref name="active" /> highlights it (play mode running, paused).
		/// </summary>
		public static bool IconButton(string id, Icon icon, bool active, string tooltip)
		{
			float height = ImGui.GetFrameHeight();
			if (active)
			{
				ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
			}

			bool pressed = ImGui.Button("##" + id, new NVector2(height * 1.5f, height));
			if (active)
			{
				ImGui.PopStyleColor();
			}

			if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(tooltip);
			}

			NVector2 min = ImGui.GetItemRectMin();
			NVector2 max = ImGui.GetItemRectMax();
			NVector2 center = (min + max) * 0.5f;
			float s = height * 0.28f;
			// Inside BeginDisabled the style alpha is reduced, and GetColorU32 applies it: disabled icons dim by themselves.
			uint color = ImGui.GetColorU32(ImGuiCol.Text);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			switch (icon)
			{
				case Icon.Play:
					draw.AddTriangleFilled(center + new NVector2(-s * 0.8f, -s), center + new NVector2(-s * 0.8f, s), center + new NVector2(s, 0f), color);
					break;
				case Icon.Stop:
					draw.AddRectFilled(center - new NVector2(s * 0.85f), center + new NVector2(s * 0.85f), color);
					break;
				case Icon.Pause:
					draw.AddRectFilled(center + new NVector2(-s * 0.85f, -s), center + new NVector2(-s * 0.25f, s), color);
					draw.AddRectFilled(center + new NVector2(s * 0.25f, -s), center + new NVector2(s * 0.85f, s), color);
					break;
				case Icon.Step:
					draw.AddTriangleFilled(center + new NVector2(-s, -s), center + new NVector2(-s, s), center + new NVector2(s * 0.5f, 0f), color);
					draw.AddRectFilled(center + new NVector2(s * 0.55f, -s), center + new NVector2(s * 0.95f, s), color);
					break;
			}

			return pressed;
		}

		#endregion
	}

	internal enum Icon
	{
		Play,
		Stop,
		Pause,
		Step
	}
}
