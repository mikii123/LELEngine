using System;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Shaders;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     A material's editor as its own section: a header tinted in the theme's material color
	///     (<see cref="EditorColor.Material" />) with its icon, and the editor on a tinted panel with a colored bar on the
	///     left, framed together so it never reads as a component. Used below an object's components and for a selected
	///     material asset.
	/// </summary>
	internal static class MaterialSection
	{
		#region PublicMethods

		/// <param name="anchor">Test anchor of the header (<c>material:...</c>).</param>
		/// <param name="showPath">The asset path above the editor (the asset inspector already shows it).</param>
		public static void Draw(Material material, InspectorContext context, string anchor, bool showPath)
		{
			bool editable = material.AssetPath != null && !AssetDatabase.IsEngineResource(material.AssetPath) && File.Exists(AssetDatabase.ToAbsolute(material.AssetPath));
			NVector4 tint = EditorStyle.Material;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector2 start = ImGui.GetCursorScreenPos();
			float right = start.X + ImGui.GetContentRegionAvail().X;
			float icon = ImGui.GetTextLineHeight();
			string padding = new string(' ', (int)Math.Ceiling((icon + 4f) / Math.Max(1f, ImGui.CalcTextSize(" ").X)));

			string title = (material.name ?? "Material") + (editable ? "" : material.AssetPath == null ? "  (runtime material)" : "  (built-in, read-only)");
			ImGui.PushStyleColor(ImGuiCol.Header, EditorStyle.Tinted(ImGuiCol.Header, tint, 0.3f));
			ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorStyle.Tinted(ImGuiCol.HeaderHovered, tint, 0.35f));
			ImGui.PushStyleColor(ImGuiCol.HeaderActive, EditorStyle.Tinted(ImGuiCol.HeaderActive, tint, 0.4f));
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new NVector2(ImGui.GetStyle().FramePadding.X, 5f));
			bool bold = EditorFonts.PushBold();
			bool open = EditorGui.CollapsingHeader(padding + title + "###material", ImGuiTreeNodeFlags.DefaultOpen);
			EditorFonts.Pop(bold);
			ImGui.PopStyleVar();
			ImGui.PopStyleColor(3);
			TestAnchors.RecordItem(anchor);
			NVector2 headerMin = ImGui.GetItemRectMin();
			NVector2 headerMax = ImGui.GetItemRectMax();
			EditorIcons.Draw(draw, new NVector2(headerMin.X + ImGui.GetTreeNodeToLabelSpacing(), headerMin.Y + (headerMax.Y - headerMin.Y - icon) * 0.5f), icon, IconKind.Material, ImGui.GetColorU32(tint));

			float bottom = headerMax.Y;
			if (open)
			{
				// Drawn first, shown behind: the panel's size is known once the editor is drawn.
				draw.ChannelsSplit(2);
				draw.ChannelsSetCurrent(1);
				const float bar = 3f;
				try
				{
					ImGui.Dummy(new NVector2(0f, 2f));
					ImGui.Indent(bar + 4f);
					ImGui.BeginGroup();
					if (showPath && material.AssetPath != null)
					{
						ImGui.TextDisabled(material.AssetPath);
					}

					ImGui.BeginDisabled(!editable);
					MaterialEditor.Draw(material, context);
					ImGui.EndDisabled();
					ImGui.EndGroup();
					ImGui.Unindent(bar + 4f);
					ImGui.Dummy(new NVector2(0f, 2f));
					bottom = ImGui.GetItemRectMax().Y;
				}
				finally
				{
					draw.ChannelsSetCurrent(0);
					var panelMin = new NVector2(start.X, headerMax.Y);
					draw.AddRectFilled(panelMin, new NVector2(right, bottom), ImGui.GetColorU32(new NVector4(tint.X, tint.Y, tint.Z, 0.08f)));
					draw.AddRectFilled(panelMin, new NVector2(start.X + bar, bottom), ImGui.GetColorU32(new NVector4(tint.X, tint.Y, tint.Z, 0.8f)));
					draw.ChannelsMerge();
				}
			}

			draw.AddRect(start, new NVector2(right, bottom), ImGui.GetColorU32(new NVector4(tint.X, tint.Y, tint.Z, 0.45f)), ImGui.GetStyle().FrameRounding);
		}

		#endregion
	}
}
