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

		#endregion
	}
}
