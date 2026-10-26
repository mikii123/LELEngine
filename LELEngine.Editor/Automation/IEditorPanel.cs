using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     A window of the editor as automation sees it: the docked panels (Scene, Inspector, ...), the editor scripts'
	///     <see cref="EditorWindow" />s, the windows with an operating-system window of their own (UI Builder, Audio
	///     Studio) and the editor's main window as a whole ("Editor"). Every one can be opened, closed, focused and
	///     captured (<see cref="GetScreenshot" />).
	/// </summary>
	internal interface IEditorPanel
	{
		/// <summary>The name automation finds it by ("Inspector", "Project Settings", a script window's title).</summary>
		string PanelName { get; }

		/// <summary>The name its ImGui window is begun with (null for a whole ImGui context: an OS window, the main window).</summary>
		string ImGuiName { get; }

		/// <summary>The window is shown (a closed panel draws nothing; a capture opens it for its frames).</summary>
		bool PanelOpen { get; set; }

		/// <summary>Draws the window into the current ImGui context: Begin ... End, or a whole context's frame.</summary>
		void Draw();

		/// <summary>Everything its ImGui context draws is the window (an OS window of its own, the editor's main window).</summary>
		bool IsWholeContext => false;

		/// <summary>The ImGui context (and GL context, through its window) the window is drawn with.</summary>
		ImGuiController Owner => EditorApplication.Instance?.Window?.Controller;

		/// <summary>Size of a capture that names none while the window is not on screen.</summary>
		NVector2 DefaultCaptureSize => new NVector2(800, 600);

		/// <summary>Between the frames of a capture, after the UI was built (renders what the UI shows: the UI Builder's preview).</summary>
		void AfterCaptureFrame()
		{
		}

		/// <summary>Brings the window to the front (on the next frame).</summary>
		void Focus()
		{
			PanelOpen = true;
			EditorApplication.Instance?.RequestWindowFocus(ImGuiName);
		}

		/// <summary>
		///     An image of the window: <paramref name="width" /> x <paramref name="height" /> pixels (0: its size on screen).
		///     At its size on screen, the editor's last frame (exactly what shows); otherwise the window laid out again, off
		///     screen, at that size. Call between frames (not from OnGUI).
		/// </summary>
		EditorScreenshot GetScreenshot(int width = 0, int height = 0)
		{
			return Automation.WindowCapture.Capture(this, width, height, null);
		}
	}
}
