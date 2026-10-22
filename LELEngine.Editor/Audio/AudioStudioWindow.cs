using System;
using System.IO;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     The Audio Studio's own operating-system window (like the UI Builder's): a second GLFW window of the editor
	///     process with a GL context shared with the editor's and its own Dear ImGui context. The editor pumps it once
	///     per frame after its own window; events come from the shared GLFW poll.
	/// </summary>
	internal sealed class AudioStudioWindow : IDisposable
	{
		#region PublicFields

		public AudioStudio Studio { get; }
		public bool IsOpen => native != null && native.Exists && !closeRequested;

		/// <summary>Set by the test hooks: the next frame is written to this folder.</summary>
		public static string ScreenshotFolder;

		#endregion

		#region PrivateFields

		private readonly MainWindow owner;
		private NativeWindow native;
		private ImGuiController imgui;
		private bool closeRequested;
		private bool disposed;
		private readonly WindowPlacement placement;
		private readonly bool rememberPlacement;

		#endregion

		#region Constructors

		public AudioStudioWindow(EditorApplication editor, MainWindow owner)
		{
			this.owner = owner;
			native = new NativeWindow(new NativeWindowSettings
			{
				ClientSize = new Vector2i(1500, 900),
				Title = "LELEngine Audio Studio",
				APIVersion = owner.APIVersion,
				Profile = ContextProfile.Core,
				Flags = ContextFlags.ForwardCompatible,
				SharedContext = owner.Context,
				StartFocused = Window.StartFocused,
				StartVisible = false
			});
			native.Closing += OnClosing;
			rememberPlacement = !editor.IsAutomated;
			placement = new WindowPlacement(native, "audio-studio-window.json");
			native.Move += OnMoved;
			native.Resize += OnResized;
			placement.Restore(rememberPlacement);

			native.MakeCurrent();
			native.Context.SwapInterval = 0;
			string layout = Path.Combine(RecentProjects.EditorDataFolder, "audio-studio-layout.ini");
			imgui = new ImGuiController(native, layout);
			imgui.ApplyStyle = () =>
			{
				EditorStyle.Apply();
				StudioUi.ApplyStyle();
			};
			imgui.ApplyStyle();
			// A saved layout from before a panel existed would leave that panel floating: rebuild the default then.
			Studio = new AudioStudio(editor, this, AudioStudio.LayoutIsCurrent(layout));
			owner.MakeEditorCurrent();
		}

		#endregion

		#region PublicMethods

		/// <summary>Runs one frame of the window (UI and present) and returns to the editor context.</summary>
		public void Frame(float deltaTime)
		{
			if (!IsOpen)
			{
				return;
			}

			native.MakeCurrent();
			try
			{
				Vector2i size = native.ClientSize;
				if (size.X <= 0 || size.Y <= 0 || native.WindowState == WindowState.Minimized)
				{
					return;
				}

				imgui.NewFrame(deltaTime, size.X, size.Y);
				try
				{
					Studio.Draw();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}

				imgui.EndFrame();
				Framebuffer.BindDefault(size.X, size.Y);
				GL.ClearColor(0.11f, 0.11f, 0.12f, 1f);
				GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
				imgui.RenderDrawData(size.X, size.Y);
				if (ScreenshotFolder != null)
				{
					var dumper = new FrameDumper();
					dumper.Start(ScreenshotFolder, 1);
					dumper.Capture(size.X, size.Y);
					ScreenshotFolder = null;
				}

				native.Context.SwapBuffers();
			}
			finally
			{
				owner.MakeEditorCurrent();
			}
		}

		public void Focus()
		{
			native?.Focus();
		}

		/// <summary>Closes the window like its close button (unsaved changes ask first). Safe mid-frame.</summary>
		public void Close()
		{
			if (Studio.RequestClose())
			{
				closeRequested = true;
			}
		}

		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			if (native != null && native.Exists)
			{
				if (rememberPlacement)
				{
					placement.Save();
				}

				native.MakeCurrent();
				Studio.Dispose();
				imgui.Dispose();
			}

			if (native != null)
			{
				native.Closing -= OnClosing;
				native.Move -= OnMoved;
				native.Resize -= OnResized;
			}

			native?.Dispose();
			native = null;
			owner.MakeEditorCurrent();
		}

		#endregion

		#region PrivateMethods

		private void OnMoved(WindowPositionEventArgs e)
		{
			placement.Track();
		}

		private void OnResized(ResizeEventArgs e)
		{
			placement.Track();
		}

		private void OnClosing(System.ComponentModel.CancelEventArgs e)
		{
			if (!Studio.RequestClose())
			{
				e.Cancel = true;
				return;
			}

			closeRequested = true;
			if (rememberPlacement)
			{
				placement.Save();
			}
		}

		#endregion
	}
}
