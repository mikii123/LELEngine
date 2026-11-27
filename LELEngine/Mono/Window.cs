using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using LELEngine.Graphics;
using LELEngine.Mathematics;
using LELEngine.Rendering;
using LELEngine.Windowing;

namespace LELEngine
{
	public class Window : GameWindow, IInputHost
	{
		#region OtherFields

		internal Stopwatch renderStopwatch;
		private bool framePending;
		private Vector2i pendingFrameSize;
		private bool resizedSinceSwap;

		// When the last two frames were presented (Stopwatch ticks): the game's time steps come from these.
		private long lastPresent;
		private long previousPresent;
		private bool presentedSinceUpdate;
		private int refreshCheckFrames;

		/// <summary>
		///     Makes the game's time steps even (<see cref="FramePacing" />): from the times frames are presented, snapped to
		///     the display's refresh periods under vsync. Off: each frame advances by the measured time since the last update.
		/// </summary>
		public static bool PaceFrames { get; set; } = true;

		/// <summary>The window's frame pacing (its refresh period, what it still owes the real clock).</summary>
		public FramePacing Pacing { get; } = new FramePacing();

		/// <summary>The game's image is the window: its pixels are the window's.</summary>
		public void WarpCursor(Vector2 position) => MousePosition = position;

		#endregion

		#region Constructors

		/// <summary>Lowest OpenGL version the engine runs on (compute shaders, SSBOs, image load/store).</summary>
		public static readonly Version MinimumApiVersion = new Version(4, 3);

		/// <summary>Newest OpenGL version the engine asks for; extra features are used when the driver grants it.</summary>
		public static readonly Version PreferredApiVersion = new Version(4, 6);

		/// <summary>New windows take the keyboard focus. Automated runs turn it off so they do not interrupt the user.</summary>
		public static bool StartFocused { get; set; } = true;

		/// <summary>
		///     Automated runs that only read frames back (hidden=1): the window is never shown. What it would show is
		///     rendered all the same; reads of the window's own pixels are undefined, reads of render targets are not.
		/// </summary>
		public static bool StartHidden { get; set; }

		/// <summary>
		///     Automated runs (fixeddt=seconds): every frame advances the game by this much, however long it took, so a
		///     frame number means the same moment in every run. 0: time as it passes.
		/// </summary>
		public static double FixedDeltaTime { get; set; }

		/// <summary>
		///     Opens a window on the graphics API that was asked for (<see cref="GraphicsDevice.RequestedApi" />), as far
		///     as this machine has it: when the Vulkan device cannot be made the window is made again for OpenGL
		///     (<see cref="GraphicsDevice.RequestedApi" /> says so after), and OpenGL is asked for at its newest version
		///     first, stepping down to the oldest the engine runs on while the driver refuses.
		/// </summary>
		/// <param name="create">Makes the window; the version is the OpenGL context's (Vulkan windows ignore it).</param>
		public static T Open<T>(Func<Version, T> create)
		{
			if (GraphicsDevice.Current == null && GraphicsDevice.RequestedApi == GraphicsApi.Vulkan)
			{
				try
				{
					return create(PreferredApiVersion);
				}
				catch (GraphicsApiUnavailableException e)
				{
					Console.WriteLine($"[Window] {e.Message}; using OpenGL");
					GraphicsDevice.RequestedApi = GraphicsApi.OpenGL;
					GraphicsDevice.FellBackFrom = GraphicsApi.Vulkan;
				}
			}

			Version version = PreferredApiVersion;
			while (true)
			{
				try
				{
					return create(version);
				}
				catch (Exception e) when (version > MinimumApiVersion && GraphicsDevice.CurrentApi == GraphicsApi.OpenGL && !(e is GraphicsApiUnavailableException))
				{
					Console.WriteLine($"[Window] OpenGL {version} context unavailable ({e.GetType().Name}), trying an older version");
					version = version.Minor > 0 ? new Version(version.Major, version.Minor - 1) : MinimumApiVersion;
					if (version < MinimumApiVersion)
					{
						version = MinimumApiVersion;
					}
				}
			}
		}

		public Window(int width, int height, string title)
			: this(width, height, title, PreferredApiVersion)
		{ }

		public Window(int width, int height, string title, Version apiVersion)
			: this(width, height, title, apiVersion, true, false)
		{ }

		/// <param name="visible">
		///     False: the window opens hidden and appears when <see cref="NativeWindow.IsVisible" /> is set, so its first
		///     image can be drawn before anyone sees it (the player's splash screen). Fullscreen windows open visible.
		/// </param>
		/// <param name="fullscreen">Opens fullscreen on the primary monitor, at its current resolution.</param>
		public Window(int width, int height, string title, Version apiVersion, bool visible, bool fullscreen)
			: base(
				new GameWindowSettings
				{
					// 0 = run as fast as the GPU allows; a fixed frequency makes the loop sleep with the coarse
					// Windows timer and costs frames when a frame takes longer than one period.
					UpdateFrequency = 0.0
				},
				CreateSettings(width, height, title, apiVersion, visible || fullscreen, fullscreen))
		{
			// The graphics device was made with this window's context (current since the base constructor made it).
			DeviceCapabilities capabilities = GraphicsDevice.Current.Capabilities;
			Console.WriteLine((capabilities.Api == GraphicsApi.OpenGL ? "GL version: " + capabilities.DriverVersion : "Graphics: " + capabilities.ApiVersion + ", driver " + capabilities.DriverVersion)
				+ "\nRenderer: " + capabilities.Renderer + "\nVSync: " + VSync);
			GraphicsCapabilities.Initialize();
		}

		#endregion

		#region ProtectedMethods

		protected override void OnResize(ResizeEventArgs e)
		{
			// Without the context (another thread holds it: the player's background loading) there is nothing to set;
			// every frame binds its own viewport.
			if (Context.IsCurrent)
			{
				GraphicsDevice.Current.Commands.SetViewport(0, 0, e.Width, e.Height);
			}

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
			double deltaTime = NextDeltaTime(e.Time);
			Time.unscaledDeltaTimeD = deltaTime;
			Time.unscaledTimeD += deltaTime;
			// The game's step is at most Time.maximumDeltaTime (Unity's): a frame that stalled (a load, entering play mode, a
			// hitch) moves the scripts and Time.time by that much, as far as the fixed steps catch up (Scene.UpdateScenes);
			// the real interval stays in unscaledDeltaTime. Before, Update got the whole stall (3.7 s after entering play
			// mode) while physics got 0.1 s of it.
			deltaTime = Math.Min(deltaTime * Time.timeScaleD, Time.maximumDeltaTimeD);
			Time.deltaTimeD = deltaTime;
			Time.timeD += deltaTime;

			if (Input.GetKeyDown(Keys.F12))
			{
				WindowState = WindowState == WindowState.Normal ? WindowState.Fullscreen : WindowState.Normal;
			}
			if (Input.GetKey(Keys.LeftAlt))
			{
				if (Input.GetKeyDown(Keys.F4))
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
			GraphicsDevice.Current.Flush();
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
				// A wait for the GPU like the swap's (the hitch profiler counts both as waiting, not as the frame's work).
				long queued = Stopwatch.GetTimestamp();
				using (FrameProfiler.Begin("Wait for the frames queued before a resize (the GPU finishing them)"))
				{
					WaitForQueuedPresents();
				}

				FrameProfiler.AddWait(Stopwatch.GetTimestamp() - queued);
			}

			long before = Stopwatch.GetTimestamp();
			using (FrameProfiler.Begin("Swap (waits for the GPU and the display)"))
			{
				SwapBuffers();
			}

			long after = Stopwatch.GetTimestamp();
			Time.swapMs = (after - before) * 1000.0 / Stopwatch.Frequency;
			FrameProfiler.AddWait(after - before);
			MarkPresented(after);
		}

		/// <summary>
		///     How far this frame advances the game: the interval between the last two presents (under vsync the swap
		///     returns at the display's refresh, so it measures what the screen shows, without the CPU's noise of when the
		///     update starts), made even by <see cref="Pacing" />; <paramref name="sinceLastUpdate" /> when no frame was
		///     presented since the last update (minimized, the splash screen) or pacing is off.
		/// </summary>
		private double NextDeltaTime(double sinceLastUpdate)
		{
			bool measured = presentedSinceUpdate && previousPresent != 0;
			presentedSinceUpdate = false;
			if (FixedDeltaTime > 0)
			{
				return FixedDeltaTime;
			}

			if (!PaceFrames)
			{
				return sinceLastUpdate;
			}

			if (refreshCheckFrames-- <= 0)
			{
				// The display can change (the window moved to another monitor, its mode changed): looked at now and then.
				refreshCheckFrames = 120;
				Pacing.RefreshPeriod = RefreshPeriod();
			}

			Pacing.VSync = VSync != VSyncMode.Off;
			return Pacing.Next(measured ? (lastPresent - previousPresent) / (double)Stopwatch.Frequency : sinceLastUpdate);
		}

		private void MarkPresented(long timestamp)
		{
			previousPresent = lastPresent;
			lastPresent = timestamp;
			presentedSinceUpdate = true;
		}

		/// <summary>Seconds of one refresh of the monitor the window's middle is on (the primary monitor's when none is found); 0 when unknown.</summary>
		private double RefreshPeriod()
		{
			try
			{
				Vector2i location = ClientLocation;
				Vector2i size = ClientSize;
				int centerX = location.X + size.X / 2;
				int centerY = location.Y + size.Y / 2;
				GlfwMonitor chosen = Glfw.PrimaryMonitor;
				foreach (GlfwMonitor monitor in Glfw.GetMonitors())
				{
					GlfwVidMode candidate = monitor.VideoMode;
					Brutal.Numerics.int2 origin = monitor.Position;
					if (centerX >= origin.X && centerX < origin.X + candidate.Width && centerY >= origin.Y && centerY < origin.Y + candidate.Height)
					{
						chosen = monitor;
						break;
					}
				}

				if (chosen.IsNull())
				{
					return 0.0;
				}

				int refreshRate = chosen.VideoMode.RefreshRate;
				return refreshRate > 0 ? 1.0 / refreshRate : 0.0;
			}
			catch (Exception)
			{
				return 0.0;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Presents what was drawn into the default framebuffer, now: images outside the game loop (the player's
		///     splash screen), on the thread that holds the context. After a resize it first waits for the earlier
		///     presents, as <see cref="PresentPendingFrame" /> does.
		/// </summary>
		public void Present()
		{
			if (resizedSinceSwap)
			{
				resizedSinceSwap = false;
				WaitForQueuedPresents();
			}

			// Not a frame of the game loop (the splash screen): the time to the game's first present is no frame interval.
			SwapBuffers();
		}

		/// <summary>
		///     Shows a window created hidden with <paramref name="drawFirstImage" />'s image as the first thing on screen
		///     (the player's splash screen). A swap while hidden is dropped, and a window shown before its first swap is
		///     blank (white) for a moment, so on Windows the window is cloaked meanwhile (DWM composes it without
		///     displaying it) until the image is presented and composed. A window already visible (fullscreen) is drawn.
		/// </summary>
		public void Show(Action drawFirstImage)
		{
			if (StartHidden)
			{
				drawFirstImage();
				Present();
				return;
			}

			IntPtr handle = !IsVisible ? Win32Window : IntPtr.Zero;
			int on = 1;
			bool cloaked = handle != IntPtr.Zero && DwmSetWindowAttribute(handle, DwmCloak, ref on, sizeof(int)) == 0;
			IsVisible = true;
			drawFirstImage();
			Present();
			if (cloaked)
			{
				GraphicsDevice.Current.Finish();
				DwmFlush();
				int off = 0;
				DwmSetWindowAttribute(handle, DwmCloak, ref off, sizeof(int));
			}
		}

		/// <summary>
		///     Initializes GLFW with an error callback that logs: a refused context version is then a managed exception
		///     from the window's constructor, which the callers' fallback to an older version catches. Call before
		///     creating a window.
		/// </summary>
		public static void InitializeGlfw()
		{
			WindowSystem.EnsureInitialized();
		}

		#endregion

		#region PrivateMethods

		private static NativeWindowSettings CreateSettings(int width, int height, string title, Version apiVersion, bool visible, bool fullscreen)
		{
			// A hidden window takes the focus when it is shown (GLFW's focus-on-show), unless automated runs turned
			// that off. Window hints stay set for the windows created later, and no window's creation sets this one.
			InitializeGlfw();
			Glfw.WindowHint(GlfwWindowHint.FocusOnShow, StartFocused ? 1 : 0);
			return new NativeWindowSettings
			{
				ClientSize = new Vector2i(width, height),
				Title = title,
				// The scene is rendered off-screen, so the default framebuffer does not need multisampling.
				APIVersion = apiVersion,
				Profile = ContextProfile.Core,
				Flags = ContextFlags.ForwardCompatible,
				StartVisible = visible && !StartHidden,
				StartFocused = StartFocused && visible && !StartHidden,
				WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal
			};
		}

		/// <summary>
		///     Drains the presentation queue before the first swap at a new window size. Drivers that present OpenGL
		///     through a DXGI swap chain (Intel) resize it on that swap; resizing while earlier presents are still
		///     queued stalls for ~2 s in the D3D runtime (the same issue as microsoft/angle#111), and the swaps after
		///     it block: maximizing or restoring froze the editor. Costs one composition interval per resize.
		/// </summary>
		private static void WaitForQueuedPresents()
		{
			GraphicsDevice.Current.Finish();
			if (OperatingSystem.IsWindows())
			{
				DwmFlush();
			}
		}

		[DllImport("dwmapi.dll")]
		private static extern int DwmFlush();

		/// <summary>DWMWA_CLOAK: the window is composed but not displayed.</summary>
		private const int DwmCloak = 13;

		[DllImport("dwmapi.dll")]
		private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

		#endregion
	}
}
