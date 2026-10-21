using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>Extra faces of the editor font, per ImGui context (every editor window has its own).</summary>
	internal static class EditorFonts
	{
		#region PrivateFields

		private static readonly Dictionary<nint, ImFontPtr> bold = new Dictionary<nint, ImFontPtr>();

		#endregion

		#region PublicMethods

		public static void Register(nint context, ImFontPtr boldFont)
		{
			bold[context] = boldFont;
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
