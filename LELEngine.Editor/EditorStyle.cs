using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>Dark editor theme.</summary>
	internal static class EditorStyle
	{
		#region PublicFields

		public static readonly NVector4 Warning = new NVector4(1f, 0.8f, 0.35f, 1f);
		public static readonly NVector4 Error = new NVector4(1f, 0.42f, 0.38f, 1f);
		public static readonly NVector4 Dim = new NVector4(0.55f, 0.55f, 0.58f, 1f);
		public static readonly NVector4 Selection = new NVector4(1f, 0.6f, 0.15f, 1f);

		#endregion

		#region PublicMethods

		public static void Apply()
		{
			ImGui.StyleColorsDark();
			ImGuiStylePtr style = ImGui.GetStyle();
			style.WindowRounding = 2f;
			style.FrameRounding = 2f;
			style.GrabRounding = 2f;
			style.TabRounding = 2f;
			style.ScrollbarRounding = 2f;
			style.WindowPadding = new NVector2(8, 6);
			style.FramePadding = new NVector2(6, 3);
			style.ItemSpacing = new NVector2(6, 4);
			style.IndentSpacing = 14f;

			Set(ImGuiCol.WindowBg, 0.15f, 0.15f, 0.16f);
			Set(ImGuiCol.ChildBg, 0.15f, 0.15f, 0.16f);
			Set(ImGuiCol.PopupBg, 0.13f, 0.13f, 0.14f);
			Set(ImGuiCol.TitleBg, 0.11f, 0.11f, 0.12f);
			Set(ImGuiCol.TitleBgActive, 0.13f, 0.13f, 0.14f);
			Set(ImGuiCol.MenuBarBg, 0.12f, 0.12f, 0.13f);
			Set(ImGuiCol.FrameBg, 0.22f, 0.22f, 0.23f);
			Set(ImGuiCol.FrameBgHovered, 0.28f, 0.28f, 0.3f);
			Set(ImGuiCol.FrameBgActive, 0.3f, 0.3f, 0.33f);
			Set(ImGuiCol.Header, 0.24f, 0.24f, 0.26f);
			Set(ImGuiCol.HeaderHovered, 0.3f, 0.3f, 0.33f);
			Set(ImGuiCol.HeaderActive, 0.17f, 0.36f, 0.6f);
			Set(ImGuiCol.Button, 0.25f, 0.25f, 0.27f);
			Set(ImGuiCol.ButtonHovered, 0.32f, 0.32f, 0.35f);
			Set(ImGuiCol.ButtonActive, 0.17f, 0.36f, 0.6f);
			Set(ImGuiCol.Tab, 0.13f, 0.13f, 0.14f);
			Set(ImGuiCol.TabHovered, 0.24f, 0.24f, 0.26f);
			Set(ImGuiCol.TabSelected, 0.18f, 0.18f, 0.19f);
			Set(ImGuiCol.TabDimmed, 0.12f, 0.12f, 0.13f);
			Set(ImGuiCol.TabDimmedSelected, 0.16f, 0.16f, 0.17f);
			Set(ImGuiCol.CheckMark, 0.4f, 0.65f, 1f);
			Set(ImGuiCol.SliderGrab, 0.4f, 0.6f, 0.9f);
			Set(ImGuiCol.DockingPreview, 0.25f, 0.5f, 0.85f);
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
