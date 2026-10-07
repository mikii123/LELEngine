using System;
using System.Diagnostics;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine
{
	public class Window : GameWindow
	{
		#region OtherFields

		internal Stopwatch renderStopwatch;
		private bool framePending;

		#endregion

		#region Constructors

		/// <summary>Lowest OpenGL version the engine runs on (compute shaders, SSBOs, image load/store).</summary>
		public static readonly Version MinimumApiVersion = new Version(4, 3);

		/// <summary>Newest OpenGL version the engine asks for; extra features are used when the driver grants it.</summary>
		public static readonly Version PreferredApiVersion = new Version(4, 6);

		public Window(int width, int height, string title)
			: this(width, height, title, PreferredApiVersion)
		{ }

		public Window(int width, int height, string title, Version apiVersion)
			: base(
				new GameWindowSettings
				{
					// 0 = run as fast as the GPU allows; a fixed frequency makes the loop sleep with the coarse
					// Windows timer and costs frames when a frame takes longer than one period.
					UpdateFrequency = 0.0
				},
				new NativeWindowSettings
				{
					ClientSize = new OpenTK.Mathematics.Vector2i(width, height),
					Title = title,
					// The scene is rendered off-screen, so the default framebuffer does not need multisampling.
					APIVersion = apiVersion,
					Profile = ContextProfile.Core,
					Flags = ContextFlags.ForwardCompatible
				})
		{
			Console.WriteLine("GL version: " + GL.GetString(StringName.Version) + "\nRenderer: " + GL.GetString(StringName.Renderer) + "\nVSync: " + VSync);
			GLCapabilities.Initialize();
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
			// The frame's commands are submitted; hand them to the GPU now and present later, right before
			// the next frame is submitted (PresentPendingFrame). On this driver SwapBuffers blocks until the
			// GPU has finished the frame, so swapping here would leave the GPU idle during the next update
			// and command submission; deferring the swap overlaps that CPU work with GPU rendering.
			GL.Flush();
			framePending = true;

			renderStopwatch.Stop();
			Time.renderDeltaTimeD = renderStopwatch.Elapsed.TotalMilliseconds;
		}

		/// <summary>
		///     Presents the previously rendered frame. The time spent here is the wait for the GPU to finish
		///     it (the frame is GPU bound) or for the display (vsync); recorded in <see cref="Time.swapMs" />.
		/// </summary>
		protected void PresentPendingFrame()
		{
			if (!framePending)
			{
				return;
			}

			long before = Stopwatch.GetTimestamp();
			SwapBuffers();
			Time.swapMs = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;
			framePending = false;
		}

		#endregion
	}
}
