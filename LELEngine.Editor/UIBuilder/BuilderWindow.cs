using System;
using System.IO;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine.Editor.UIBuilder
{
	/// <summary>
	///     The UI Builder's own operating-system window: a second GLFW window of the editor process with a GL context
	///     shared with the editor's (textures and buffers in common) and its own Dear ImGui context. The editor pumps
	///     it once per frame after its own window; events come from the shared GLFW poll.
	/// </summary>
	internal sealed class BuilderWindow : IDisposable
	{
		#region PublicFields

		public UIBuilder Builder { get; }
		public bool IsOpen => native != null && native.Exists && !closeRequested;

		/// <summary>Set by the test hooks: the next builder frame is written to this folder.</summary>
		public static string ScreenshotFolder;

		#endregion

		#region PrivateFields

		private readonly EditorWindow owner;
		private NativeWindow native;
		private ImGuiController imgui;
		private bool closeRequested;
		private bool disposed;

		#endregion

		#region Constructors

		public BuilderWindow(EditorApplication editor, EditorWindow owner)
		{
			this.owner = owner;
			native = new NativeWindow(new NativeWindowSettings
			{
				ClientSize = new Vector2i(1440, 860),
				Title = "LELEngine UI Builder",
				APIVersion = owner.APIVersion,
				Profile = ContextProfile.Core,
				Flags = ContextFlags.ForwardCompatible,
				SharedContext = owner.Context,
				StartFocused = Window.StartFocused,
				StartVisible = true
			});
			native.Closing += OnClosing;

			native.MakeCurrent();
			// The editor's window swaps with vsync; a second vsync wait per frame would halve the frame rate.
			native.Context.SwapInterval = 0;
			string layout = Path.Combine(RecentProjects.EditorDataFolder, "builder-layout.ini");
			imgui = new ImGuiController(native, layout);
			EditorStyle.Apply();
			Builder = new UIBuilder(editor, this);
			owner.MakeEditorCurrent();
		}

		#endregion

		#region PublicMethods

		/// <summary>Runs one frame of the builder window (UI, preview render, present) and returns to the editor context.</summary>
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
					Builder.Draw();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}

				imgui.EndFrame();
				Builder.RenderPreview();

				Framebuffer.BindDefault(size.X, size.Y);
				GL.ClearColor(0.11f, 0.11f, 0.12f, 1f);
				GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
				imgui.RenderDrawData(size.X, size.Y);
				if (ScreenshotFolder != null)
				{
					// Testing aid: one frame of this window as a BMP.
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

		/// <summary>
		///     Closes the window like its close button (unsaved changes ask first). Safe mid-frame: the window is disposed
		///     by the editor after the frame, never while its ImGui context is drawing.
		/// </summary>
		public void Close()
		{
			if (Builder.RequestClose())
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
				native.MakeCurrent();
				Builder.Dispose();
				imgui.Dispose();
			}

			native?.Closing -= OnClosing;
			native?.Dispose();
			native = null;
			owner.MakeEditorCurrent();
		}

		#endregion

		#region PrivateMethods

		private void OnClosing(System.ComponentModel.CancelEventArgs e)
		{
			if (!Builder.RequestClose())
			{
				e.Cancel = true;
				return;
			}

			closeRequested = true;
		}

		#endregion
	}
}
