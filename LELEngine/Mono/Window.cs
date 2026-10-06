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
					UpdateFrequency = 60.0
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
			Console.WriteLine("GL version: " + GL.GetString(StringName.Version) + "\nRenderer: " + GL.GetString(StringName.Renderer));
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
