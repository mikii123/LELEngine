using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine
{
	public class Window : GameWindow, IInputHost
	{
		#region OtherFields

		internal Stopwatch renderStopwatch;
		private bool framePending;
		private Vector2i pendingFrameSize;
		private bool resizedSinceSwap;

		#endregion

		#region Constructors

		/// <summary>Lowest OpenGL version the engine runs on (compute shaders, SSBOs, image load/store).</summary>
		public static readonly Version MinimumApiVersion = new Version(4, 3);

		/// <summary>Newest OpenGL version the engine asks for; extra features are used when the driver grants it.</summary>
		public static readonly Version PreferredApiVersion = new Version(4, 6);

		/// <summary>New windows take the keyboard focus. Automated runs turn it off so they do not interrupt the user.</summary>
		public static bool StartFocused { get; set; } = true;

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
					ClientSize = new Vector2i(width, height),
					Title = title,
					// The scene is rendered off-screen, so the default framebuffer does not need multisampling.
					APIVersion = apiVersion,
					Profile = ContextProfile.Core,
					Flags = ContextFlags.ForwardCompatible,
					StartFocused = StartFocused
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
			// The pending frame has the old size; the next swap waits for the earlier ones (see PresentPendingFrame).
			framePending = false;
			resizedSinceSwap = true;
		}

		protected override void OnLoad()
		{
			Console.WriteLine("Success!\nExecuting logic...");
			renderStopwatch = new Stopwatch();
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			// Raises the UpdateFrame event (hosts and test hooks subscribe to it).
			base.OnUpdateFrame(e);
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
			pendingFrameSize = FramebufferSize;

			renderStopwatch.Stop();
			Time.renderDeltaTimeD = renderStopwatch.Elapsed.TotalMilliseconds;
			base.OnRenderFrame(e);
		}

		/// <summary>
		///     Presents the previously rendered frame. The time spent here is the wait for the GPU to finish
		///     it (the frame is GPU bound) or for the display (vsync); recorded in <see cref="Time.swapMs" />.
		///     A frame rendered before the window was resized is dropped, and the first swap after a resize
		///     waits until the earlier frames are presented (see <see cref="WaitForQueuedPresents" />).
		/// </summary>
		protected void PresentPendingFrame()
		{
			if (!framePending)
			{
				return;
			}

			framePending = false;
			if (pendingFrameSize != FramebufferSize)
			{
				return;
			}

			if (resizedSinceSwap)
			{
				resizedSinceSwap = false;
				WaitForQueuedPresents();
			}

			long before = Stopwatch.GetTimestamp();
			SwapBuffers();
			Time.swapMs = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     Drains the presentation queue before the first swap at a new window size. Drivers that present OpenGL
		///     through a DXGI swap chain (Intel) resize it on that swap; resizing while earlier presents are still
		///     queued stalls for ~2 s in the D3D runtime (the same issue as microsoft/angle#111), and the swaps after
		///     it block: maximizing or restoring froze the editor. Costs one composition interval per resize.
		/// </summary>
		private static void WaitForQueuedPresents()
		{
			GL.Finish();
			if (OperatingSystem.IsWindows())
			{
				DwmFlush();
			}
		}

		[DllImport("dwmapi.dll")]
		private static extern int DwmFlush();

		#endregion
	}
}
