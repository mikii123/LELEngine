using System;
using System.Diagnostics;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Input;

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
				width,
				height,
				new GraphicsMode(32, 24, 0, 4),
				title,
				GameWindowFlags.FixedWindow,
				DisplayDevice.Default,
				4,
				0,
				GraphicsContextFlags.ForwardCompatible)
		{
			Console.WriteLine("GL version: " + GL.GetString(StringName.Version) + "\nRenderer: " + GL.GetString(StringName.Renderer));
		}

		#endregion

		#region ProtectedMethods

		protected override void OnResize(EventArgs e)
		{
			GL.Viewport(0, 0, Width, Height);
		}

		protected override void OnLoad(EventArgs e)
		{
			Console.WriteLine("Success!\nExecuting logic...");

			renderStopwatch = new Stopwatch();
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			Time.deltaTimeD = e.Time;
			Time.timeD += e.Time;
			if (Input.GetKeyDown(Key.F12))
			{
				if (WindowState == WindowState.Normal)
				{
					WindowState = WindowState.Fullscreen;
				}
				else
				{
					WindowState = WindowState.Normal;
				}
			}
			if (Input.GetKey(Key.AltLeft))
			{
				if (Input.GetKeyDown(Key.F4))
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
