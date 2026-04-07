using System;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine
{
	public class Window : GameWindow
	{
		#region OtherFields

		internal Stopwatch renderStopwatch;

		#endregion

		#region Constructors

		public Window(int width, int height, string title)
			: base(
				new GameWindowSettings
				{
					UpdateFrequency = 60.0
				},
				new NativeWindowSettings
				{
					ClientSize = new OpenTK.Mathematics.Vector2i(width, height),
					Title = title,
					APIVersion = new Version(4, 0),
					Flags = ContextFlags.ForwardCompatible,
					NumberOfSamples = 4
				})
		{
			Console.WriteLine("GL version: " + GL.GetString(StringName.Version) + "\nRenderer: " + GL.GetString(StringName.Renderer));
		}

		#endregion

		#region ProtectedMethods

		protected override void OnResize(ResizeEventArgs e)
		{
			GL.Viewport(0, 0, e.Width, e.Height);
		}

		protected override void OnLoad()
		{
			Console.WriteLine("Success!\nExecuting logic...");
			renderStopwatch = new Stopwatch();
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			Time.deltaTimeD = e.Time;
			Time.timeD += e.Time;

			if (Input.GetKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.F12))
			{
				WindowState = WindowState == WindowState.Normal ? WindowState.Fullscreen : WindowState.Normal;
			}
			if (Input.GetKey(OpenTK.Windowing.GraphicsLibraryFramework.Keys.LeftAlt))
			{
				if (Input.GetKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.F4))
				{
					Close();
				}
			}
		}

		protected override void OnRenderFrame(FrameEventArgs e)
		{
			// Child loop

			// swap backbuffer
			SwapBuffers();

			// Stop the stopwatch
			renderStopwatch.Stop();
			Time.renderDeltaTimeD = renderStopwatch.ElapsedMilliseconds;
		}

		#endregion
	}
}
