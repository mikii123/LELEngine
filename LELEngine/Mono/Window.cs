using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using ErrorCode = OpenTK.Windowing.GraphicsLibraryFramework.ErrorCode;
using GLFW = OpenTK.Windowing.GraphicsLibraryFramework.GLFW;
using GLFWBindingsContext = OpenTK.Windowing.GraphicsLibraryFramework.GLFWBindingsContext;
using GLFWCallbacks = OpenTK.Windowing.GraphicsLibraryFramework.GLFWCallbacks;
using WindowHintBool = OpenTK.Windowing.GraphicsLibraryFramework.WindowHintBool;

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

		/// <summary>Rooted: GLFW keeps only the native function pointer.</summary>
		private static readonly GLFWCallbacks.ErrorCallback glfwErrorLog = OnGlfwError;

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
			// Only the bindings the engine uses: OpenTK would also load the compatibility and ES ones (~35 ms of the
			// time before a window can show anything).
			GL.LoadBindings(new GLFWBindingsContext());
			Console.WriteLine("GL version: " + GL.GetString(StringName.Version) + "\nRenderer: " + GL.GetString(StringName.Renderer) + "\nVSync: " + VSync);
			GLCapabilities.Initialize();
		}

		#endregion

		#region ProtectedMethods

		protected override void OnResize(ResizeEventArgs e)
		{
			// Without the context (another thread holds it: the player's background loading) there is nothing to set;
			// every frame binds its own viewport.
			if (Context.IsCurrent)
			{
				GL.Viewport(0, 0, e.Width, e.Height);
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
			double deltaTime = Time.captureDeltaTimeD > 0 ? Time.captureDeltaTimeD : e.Time;
			Time.deltaTimeD = deltaTime;
			Time.timeD += deltaTime;

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

			SwapBuffers();
		}

		/// <summary>
		///     Shows a window created hidden with <paramref name="drawFirstImage" />'s image as the first thing on screen
		///     (the player's splash screen). A swap while hidden is dropped, and a window shown before its first swap is
		///     blank (white) for a moment, so on Windows the window is cloaked meanwhile (DWM composes it without
		///     displaying it) until the image is presented and composed. A window already visible (fullscreen) is drawn.
		/// </summary>
		public unsafe void Show(Action drawFirstImage)
		{
			IntPtr handle = !IsVisible && OperatingSystem.IsWindows() ? GLFW.GetWin32Window(WindowPtr) : IntPtr.Zero;
			int on = 1;
			bool cloaked = handle != IntPtr.Zero && DwmSetWindowAttribute(handle, DwmCloak, ref on, sizeof(int)) == 0;
			IsVisible = true;
			drawFirstImage();
			Present();
			if (cloaked)
			{
				GL.Finish();
				DwmFlush();
				int off = 0;
				DwmSetWindowAttribute(handle, DwmCloak, ref off, sizeof(int));
			}
		}

		/// <summary>
		///     Initializes GLFW with an error callback that logs. OpenTK's default one throws from inside the native
		///     callback, which aborts the process (exit 134 on Linux) before a refused context version reaches the
		///     callers' fallback to an older one; with this one glfwCreateWindow returns NULL and NativeWindow throws a
		///     managed exception they catch. Call before creating a window (after GLFWProvider.EnsureInitialized, which
		///     installs OpenTK's callback right before glfwInit).
		/// </summary>
		public static void InitializeGlfw()
		{
			GLFWProvider.EnsureInitialized();
			GLFWProvider.SetErrorCallback(glfwErrorLog);
		}

		#endregion

		#region PrivateMethods

		private static void OnGlfwError(ErrorCode error, string description)
		{
			Console.WriteLine($"[GLFW] {error}: {description}");
		}

		private static NativeWindowSettings CreateSettings(int width, int height, string title, Version apiVersion, bool visible, bool fullscreen)
		{
			// A hidden window takes the focus when it is shown (GLFW's focus-on-show), unless automated runs turned
			// that off. Window hints stay set for the windows created later, and OpenTK leaves this one alone.
			InitializeGlfw();
			GLFW.WindowHint(WindowHintBool.FocusOnShow, StartFocused);
			return new NativeWindowSettings
			{
				ClientSize = new Vector2i(width, height),
				Title = title,
				// The scene is rendered off-screen, so the default framebuffer does not need multisampling.
				APIVersion = apiVersion,
				Profile = ContextProfile.Core,
				Flags = ContextFlags.ForwardCompatible,
				StartVisible = visible,
				StartFocused = StartFocused && visible,
				WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal,
				AutoLoadBindings = false
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
			GL.Finish();
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
