using System;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>Small icons of object fields, lists and drag previews, drawn with the draw list (the editor font has no symbol glyphs).</summary>
	internal enum IconKind
	{
		None,
		GameObject,
		Component,
		Asset,
		ScriptableObject,
		Picker,
		Grip,
		Cross,
		Plus,
		Dropdown,
		Lock,
		Unlock
	}

	internal static class EditorIcons
	{
		#region PublicFields

		public static readonly NVector4 Accept = new NVector4(0.35f, 0.82f, 0.45f, 1f);
		public static readonly NVector4 Reject = new NVector4(0.95f, 0.38f, 0.33f, 1f);

		#endregion

		#region PublicMethods

		/// <summary>The icon of a referenced object (null: none).</summary>
		public static IconKind KindOf(object value)
		{
			switch (value)
			{
				case GameObject _:
					return IconKind.GameObject;
				case Behaviour _:
					return IconKind.Component;
				case ScriptableObject _:
					return IconKind.ScriptableObject;
				case IAsset _:
					return IconKind.Asset;
				default:
					return IconKind.None;
			}
		}

		/// <summary>Draws an icon into the square at <paramref name="min" /> with side <paramref name="size" />.</summary>
		public static void Draw(ImDrawListPtr draw, NVector2 min, float size, IconKind kind, uint color)
		{
			NVector2 center = min + new NVector2(size * 0.5f);
			float s = size * 0.5f;
			float thickness = Math.Max(1f, size * 0.09f);
			switch (kind)
			{
				case IconKind.GameObject:
				{
					// An isometric cube.
					var top = center + new NVector2(0f, -s * 0.8f);
					var left = center + new NVector2(-s * 0.72f, -s * 0.38f);
					var right = center + new NVector2(s * 0.72f, -s * 0.38f);
					var middle = center + new NVector2(0f, 0.02f * s);
					var bottomLeft = center + new NVector2(-s * 0.72f, s * 0.42f);
					var bottomRight = center + new NVector2(s * 0.72f, s * 0.42f);
					var bottom = center + new NVector2(0f, s * 0.82f);
					draw.AddQuad(top, right, middle, left, color, thickness);
					draw.AddLine(left, bottomLeft, color, thickness);
					draw.AddLine(right, bottomRight, color, thickness);
					draw.AddLine(middle, bottom, color, thickness);
					draw.AddLine(bottomLeft, bottom, color, thickness);
					draw.AddLine(bottomRight, bottom, color, thickness);
					break;
				}
				case IconKind.Component:
					draw.AddCircle(center, s * 0.7f, color, 12, thickness);
					draw.AddCircleFilled(center, s * 0.28f, color, 10);
					for (int i = 0; i < 4; i++)
					{
						float angle = i * MathF.PI * 0.5f + MathF.PI * 0.25f;
						var direction = new NVector2(MathF.Cos(angle), MathF.Sin(angle));
						draw.AddLine(center + direction * s * 0.7f, center + direction * s * 0.95f, color, thickness * 1.6f);
					}

					break;
				case IconKind.Asset:
				case IconKind.ScriptableObject:
				{
					// A page with a folded corner (lines of text for ScriptableObjects).
					float w = s * 0.72f;
					float h = s * 0.9f;
					float fold = s * 0.38f;
					var a = center + new NVector2(-w, -h);
					var b = center + new NVector2(w - fold, -h);
					var c = center + new NVector2(w, -h + fold);
					var d = center + new NVector2(w, h);
					var e = center + new NVector2(-w, h);
					draw.PathLineTo(a);
					draw.PathLineTo(b);
					draw.PathLineTo(c);
					draw.PathLineTo(d);
					draw.PathLineTo(e);
					draw.PathStroke(color, ImDrawFlags.Closed, thickness);
					draw.AddLine(b, b + new NVector2(0f, fold), color, thickness);
					draw.AddLine(b + new NVector2(0f, fold), c, color, thickness);
					if (kind == IconKind.ScriptableObject)
					{
						for (int i = 0; i < 3; i++)
						{
							float y = center.Y - h * 0.1f + i * h * 0.36f;
							draw.AddLine(new NVector2(center.X - w * 0.55f, y), new NVector2(center.X + w * 0.55f, y), color, thickness);
						}
					}

					break;
				}
				case IconKind.Picker:
					draw.AddCircle(center, s * 0.62f, color, 14, thickness);
					draw.AddCircleFilled(center, s * 0.22f, color, 10);
					break;
				case IconKind.Grip:
					for (int i = -1; i <= 1; i++)
					{
						float y = center.Y + i * s * 0.42f;
						draw.AddLine(new NVector2(center.X - s * 0.55f, y), new NVector2(center.X + s * 0.55f, y), color, thickness);
					}

					break;
				case IconKind.Cross:
					draw.AddLine(center + new NVector2(-s * 0.45f, -s * 0.45f), center + new NVector2(s * 0.45f, s * 0.45f), color, thickness * 1.3f);
					draw.AddLine(center + new NVector2(-s * 0.45f, s * 0.45f), center + new NVector2(s * 0.45f, -s * 0.45f), color, thickness * 1.3f);
					break;
				case IconKind.Plus:
					draw.AddLine(center + new NVector2(-s * 0.55f, 0f), center + new NVector2(s * 0.55f, 0f), color, thickness * 1.3f);
					draw.AddLine(center + new NVector2(0f, -s * 0.55f), center + new NVector2(0f, s * 0.55f), color, thickness * 1.3f);
					break;
				case IconKind.Dropdown:
					draw.AddTriangleFilled(center + new NVector2(-s * 0.5f, -s * 0.22f), center + new NVector2(s * 0.5f, -s * 0.22f), center + new NVector2(0f, s * 0.36f), color);
					break;
				case IconKind.Lock:
				case IconKind.Unlock:
				{
					// A padlock: the body, and the shackle closed or swung open.
					draw.AddRectFilled(center + new NVector2(-s * 0.62f, -s * 0.05f), center + new NVector2(s * 0.62f, s * 0.8f), color, s * 0.12f);
					float shackleX = kind == IconKind.Lock ? 0f : s * 0.55f;
					draw.PathArcTo(center + new NVector2(shackleX, -s * 0.2f), s * 0.38f, MathF.PI, MathF.PI * 2f, 10);
					draw.PathStroke(color, ImDrawFlags.None, thickness * 1.4f);
					draw.AddLine(center + new NVector2(shackleX - s * 0.38f, -s * 0.2f), center + new NVector2(shackleX - s * 0.38f, -s * 0.02f), color, thickness * 1.4f);
					if (kind == IconKind.Lock)
					{
						draw.AddLine(center + new NVector2(s * 0.38f, -s * 0.2f), center + new NVector2(s * 0.38f, -s * 0.02f), color, thickness * 1.4f);
					}

					break;
				}
			}
		}

		/// <summary>A small square button showing an icon; true when clicked.</summary>
		public static bool Button(string id, IconKind kind, string tooltip, float size = 0f)
		{
			float height = ImGui.GetFrameHeight();
			size = size > 0f ? size : height;
			bool pressed = ImGui.Button("##" + id, new NVector2(size, height));
			if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(tooltip);
			}

			NVector2 min = ImGui.GetItemRectMin();
			NVector2 max = ImGui.GetItemRectMax();
			float icon = Math.Min(max.X - min.X, max.Y - min.Y) * 0.62f;
			Draw(ImGui.GetWindowDrawList(), (min + max) * 0.5f - new NVector2(icon * 0.5f), icon, kind, ImGui.GetColorU32(ImGuiCol.Text));
			return pressed;
		}

		/// <summary>An outline around a drop target: green when the dragged object fits, red when it does not.</summary>
		public static void DropHighlight(NVector2 min, NVector2 max, bool fits)
		{
			ImGui.GetWindowDrawList().AddRect(min - new NVector2(1f), max + new NVector2(1f), ImGui.GetColorU32(fits ? Accept : Reject), ImGui.GetStyle().FrameRounding, ImDrawFlags.None, 2f);
		}

		#endregion
	}
}
