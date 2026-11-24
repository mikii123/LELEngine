using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Small ImGui helpers, and the editor's interactive controls (EditorGui.Controls.cs).</summary>
	internal static partial class EditorGui
	{
		#region PublicMethods

		/// <summary>Reference to a texture for ImGui.Image (ImGui names it by its id; null: none, nothing is drawn).</summary>
		public static unsafe ImTextureRef Texture(Graphics.GpuTexture texture)
		{
			return new ImTextureRef(null, new ImTextureID((ulong)(texture != null ? texture.Id : 0)));
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

			bool pressed = Button("##" + id, new NVector2(height * 1.5f, height));
			if (active)
			{
				ImGui.PopStyleColor();
			}

			if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				SetTooltip(tooltip);
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
				case Icon.Maximize:
				case Icon.Minimize:
					DrawFullscreenCorners(draw, center, s, icon == Icon.Maximize, color);
					break;
				default:
					DrawToolIcon(draw, center, s * 1.15f, icon, color);
					break;
			}

			return pressed;
		}

		/// <summary>The scene view's tools, as Unity draws them: move arrows, rotate arc, scale box, World globe, Local cube.</summary>
		private static void DrawToolIcon(ImDrawListPtr draw, NVector2 c, float r, Icon icon, uint color)
		{
			float thickness = System.MathF.Max(1.4f, r * 0.2f);
			float head = r * 0.42f;
			switch (icon)
			{
				case Icon.Move:
				{
					// A cross with an arrowhead at each of its four ends.
					float shaft = r - head * 0.6f;
					draw.AddLine(c + new NVector2(-shaft, 0f), c + new NVector2(shaft, 0f), color, thickness);
					draw.AddLine(c + new NVector2(0f, -shaft), c + new NVector2(0f, shaft), color, thickness);
					for (int i = 0; i < 4; i++)
					{
						var d = new NVector2(i == 0 ? 1f : i == 1 ? -1f : 0f, i == 2 ? 1f : i == 3 ? -1f : 0f);
						var side = new NVector2(-d.Y, d.X);
						ArrowHead(draw, c + d * r, d, side, head, color);
					}

					break;
				}
				case Icon.Rotate:
				{
					// Most of a circle, the arrowhead where it ends.
					const float start = -0.35f * System.MathF.PI;
					const float end = 1.25f * System.MathF.PI;
					float radius = r * 0.82f;
					draw.PathArcTo(c, radius, start, end, 24);
					draw.PathStroke(color, ImDrawFlags.None, thickness);
					var at = new NVector2(System.MathF.Cos(end), System.MathF.Sin(end));
					var tangent = new NVector2(-at.Y, at.X);
					ArrowHead(draw, c + at * radius + tangent * head * 0.55f, tangent, at, head, color);
					break;
				}
				case Icon.Scale:
				{
					// The outline of a box, a small solid box in its corner and an arrow from it to the opposite corner.
					float small = r * 0.8f;
					draw.AddRect(c - new NVector2(r), c + new NVector2(r), color, 0f, ImDrawFlags.None, thickness * 0.75f);
					draw.AddRectFilled(c + new NVector2(-r, r - small), c + new NVector2(-r + small, r), color);
					var tip = c + new NVector2(r * 0.8f, -r * 0.8f);
					var d = NVector2.Normalize(new NVector2(1f, -1f));
					draw.AddLine(c + new NVector2(-r + small, r - small), tip - d * head * 0.6f, color, thickness);
					ArrowHead(draw, tip, d, new NVector2(-d.Y, d.X), head * 0.9f, color);
					break;
				}
				case Icon.World:
				{
					// A globe: its outline, a meridian and the equator.
					draw.AddCircle(c, r, color, 24, thickness * 0.85f);
					draw.AddEllipse(c, new NVector2(r * 0.42f, r), color, 0f, 24, thickness * 0.85f);
					draw.AddLine(c + new NVector2(-r, 0f), c + new NVector2(r, 0f), color, thickness * 0.85f);
					break;
				}
				case Icon.Local:
				{
					// A cube seen from above a corner: the outline and the three edges meeting at the near corner.
					float x = r * 0.87f;
					float y = r * 0.5f;
					NVector2 top = c + new NVector2(0f, -r), upperRight = c + new NVector2(x, -y), lowerRight = c + new NVector2(x, y);
					NVector2 bottom = c + new NVector2(0f, r), lowerLeft = c + new NVector2(-x, y), upperLeft = c + new NVector2(-x, -y);
					NVector2[] outline = { top, upperRight, lowerRight, bottom, lowerLeft, upperLeft };
					draw.AddPolyline(ref outline[0], outline.Length, color, ImDrawFlags.Closed, thickness * 0.85f);
					draw.AddLine(c, upperLeft, color, thickness * 0.85f);
					draw.AddLine(c, upperRight, color, thickness * 0.85f);
					draw.AddLine(c, bottom, color, thickness * 0.85f);
					break;
				}
			}
		}

		/// <summary>A solid arrowhead whose tip is at <paramref name="tip" />, pointing along <paramref name="direction" />.</summary>
		private static void ArrowHead(ImDrawListPtr draw, NVector2 tip, NVector2 direction, NVector2 side, float size, uint color)
		{
			NVector2 back = tip - direction * size;
			draw.AddTriangleFilled(tip, back + side * size * 0.6f, back - side * size * 0.6f, color);
		}

		/// <summary>
		///     A video player's full screen icons: four corner brackets, their corners outwards (maximize) or towards the middle
		///     (minimize, back from full screen).
		/// </summary>
		private static void DrawFullscreenCorners(ImDrawListPtr draw, NVector2 center, float s, bool outwards, uint color)
		{
			float r = s * 1.1f;
			float thickness = System.MathF.Max(1.5f, s * 0.28f);
			float inner = r * 0.3f;
			for (int i = 0; i < 4; i++)
			{
				var sign = new NVector2(i % 2 == 0 ? -1f : 1f, i < 2 ? -1f : 1f);
				// Maximize: the corner at the square's corner, its arms along the edges towards the middle of each side.
				// Minimize: the corner near the middle, its arms out towards the square's edges.
				NVector2 corner = center + sign * (outwards ? r : inner);
				float arm = (r - inner) * (outwards ? -1f : 1f);
				draw.AddLine(corner, corner + new NVector2(sign.X * arm, 0f), color, thickness);
				draw.AddLine(corner, corner + new NVector2(0f, sign.Y * arm), color, thickness);
			}
		}

		#endregion
	}

	internal enum Icon
	{
		Play,
		Stop,
		Pause,
		Step,
		Maximize,
		Minimize,
		Move,
		Rotate,
		Scale,
		World,
		Local
	}
}
