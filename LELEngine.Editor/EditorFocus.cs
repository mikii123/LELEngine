using System;
using System.Runtime.InteropServices;
using OpenTK.Windowing.Desktop;

namespace LELEngine.Editor
{
	/// <summary>
	///     Whether the editor is the application in front: the window with the keyboard focus belongs to the editor's process
	///     (its main window, the Audio Studio's or the UI Builder's, a file dialog of its own). Files changed on disk import
	///     only then (<see cref="EditorApplication" />, "Changes on disk import when the editor is in front").
	/// </summary>
	internal static class EditorFocus
	{
		#region PublicMethods

		/// <param name="mainWindow">Asked on systems other than Windows, where only the editor's main window counts.</param>
		public static bool IsInFront(NativeWindow mainWindow)
		{
			if (OperatingSystem.IsWindows())
			{
				// No window in front (switching, a locked session): not the editor.
				IntPtr window = GetForegroundWindow();
				return window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint process) != 0 && process == (uint)Environment.ProcessId;
			}

			return mainWindow == null || mainWindow.IsFocused;
		}

		#endregion

		#region PrivateMethods

		[DllImport("user32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("user32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

		#endregion
	}
}
