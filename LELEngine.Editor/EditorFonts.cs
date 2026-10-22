using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>Extra faces of the editor font, per ImGui context (every editor window has its own).</summary>
	internal static class EditorFonts
	{
		#region PrivateFields

		private static readonly Dictionary<nint, ImFontPtr> bold = new Dictionary<nint, ImFontPtr>();
		private static readonly Dictionary<nint, ImFontPtr> mono = new Dictionary<nint, ImFontPtr>();

		#endregion

		#region PublicMethods

		public static void Register(nint context, ImFontPtr boldFont)
		{
			bold[context] = boldFont;
		}

		public static void RegisterMono(nint context, ImFontPtr monoFont)
		{
			mono[context] = monoFont;
		}

		/// <summary>Switches to the bold face at the current size; false (nothing pushed) when the window has none.</summary>
		public static unsafe bool PushBold()
		{
			if (!bold.TryGetValue((nint)ImGui.GetCurrentContext().Handle, out ImFontPtr font))
			{
				return false;
			}

			ImGui.PushFont(font, ImGui.GetFontSize());
			return true;
		}

		/// <summary>Switches to the monospaced face (source code) at <paramref name="size" /> pixels; false when the window has none.</summary>
		public static unsafe bool PushMono(float size)
		{
			if (!mono.TryGetValue((nint)ImGui.GetCurrentContext().Handle, out ImFontPtr font))
			{
				return false;
			}

			ImGui.PushFont(font, size);
			return true;
		}

		public static void Pop(bool pushed)
		{
			if (pushed)
			{
				ImGui.PopFont();
			}
		}

		#endregion
	}
}
