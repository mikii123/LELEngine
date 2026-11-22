using System;
using System.Collections.Generic;
using Brutal.GlfwApi;
using LELEngine.Mathematics;

namespace LELEngine.Windowing
{
	/// <summary>
	///     The windowing library (GLFW, through the BRUTAL framework's bindings): started once, on the thread that will
	///     own the windows.
	/// </summary>
	public static class WindowSystem
	{
		private static bool initialized;
		private static readonly object sync = new object();

		/// <summary>The last error GLFW reported (window creation's refusals arrive here before the exception).</summary>
		public static string LastError { get; private set; }

		/// <summary>GLFW was started (false in a host that never made a window).</summary>
		public static bool IsInitialized => initialized;

		/// <summary>
		///     Starts GLFW and installs an error callback that logs: a refused context version is a managed exception
		///     from the window's constructor, which callers catch to fall back to an older one.
		/// </summary>
		public static void EnsureInitialized()
		{
			if (initialized)
			{
				return;
			}

			lock (sync)
			{
				if (initialized)
				{
					return;
				}

				Glfw.ErrorCallback += OnError;
				if (!Glfw.Init())
				{
					GlfwErrorCode code = Glfw.GetError(out string description);
					throw new InvalidOperationException($"GLFW could not be initialized: {code} ({description}).");
				}

				initialized = true;
			}
		}

		/// <summary>Seconds since GLFW started.</summary>
		public static double Time => Glfw.Time;

		/// <summary>A context is current on the calling thread (any thread may ask).</summary>
		public static bool HasCurrentContext => Glfw.GetCurrentContext() != IntPtr.Zero;

		/// <summary>Wakes the thread waiting in <see cref="NativeWindow.ProcessEvents(double)" />; any thread may call.</summary>
		public static void PostEmptyEvent()
		{
			Glfw.PostEmptyEvent();
		}

		private static void OnError(GlfwErrorCode error, string description)
		{
			LastError = $"{error}: {description}";
			Console.WriteLine($"[GLFW] {error}: {description}");
		}
	}

	/// <summary>A window's OpenGL context.</summary>
	public sealed class GraphicsContext
	{
		private int swapInterval;

		internal GraphicsContext(GlfwWindow window)
		{
			Window = window;
		}

		/// <summary>The BRUTAL framework's window the context belongs to.</summary>
		public GlfwWindow Window { get; }

		public IntPtr WindowPtr => Window.NativeHandle;

		/// <summary>The context is current on the calling thread.</summary>
		public bool IsCurrent => Glfw.GetCurrentContext() == Window.NativeHandle;

		/// <summary>Frames the swap waits for: 0 presents at once, 1 waits for the display's refresh.</summary>
		public int SwapInterval
		{
			get => swapInterval;
			set
			{
				Glfw.SwapInterval(value);
				swapInterval = value;
			}
		}

		public void MakeCurrent()
		{
			Window.MakeContextCurrent();
		}

		/// <summary>Releases the calling thread's context, so that another thread can take it.</summary>
		public void MakeNoneCurrent()
		{
			Glfw.PInvoke.MakeContextCurrent(null);
		}

		public void SwapBuffers()
		{
			Window.SwapBuffers();
		}
	}

	public readonly struct VideoMode
	{
		public int Width { get; }
		public int Height { get; }
		public int RedBits { get; }
		public int GreenBits { get; }
		public int BlueBits { get; }
		public int RefreshRate { get; }

		internal VideoMode(GlfwVidMode mode)
		{
			Width = mode.Width;
			Height = mode.Height;
			RedBits = mode.RedBits;
			GreenBits = mode.GreenBits;
			BlueBits = mode.BlueBits;
			RefreshRate = mode.RefreshRate;
		}
	}

	/// <summary>A connected display, as it was when asked for.</summary>
	public sealed class MonitorInfo
	{
		private readonly VideoMode[] supportedVideoModes;

		internal MonitorInfo(GlfwMonitor monitor)
		{
			Handle = monitor;
			Name = monitor.Name;
			Brutal.Numerics.int2 position = monitor.Position;
			GlfwVidMode mode = monitor.VideoMode;
			ClientArea = new Box2i(position.X, position.Y, position.X + mode.Width, position.Y + mode.Height);
			monitor.GetMonitorWorkArea(out Brutal.Numerics.int2 workPosition, out Brutal.Numerics.int2 workSize);
			WorkArea = new Box2i(workPosition.X, workPosition.Y, workPosition.X + workSize.X, workPosition.Y + workSize.Y);
			Brutal.Numerics.float2 scale = monitor.ContentScale;
			HorizontalScale = scale.X;
			VerticalScale = scale.Y;
			CurrentVideoMode = new VideoMode(mode);
			ReadOnlySpan<GlfwVidMode> modes = monitor.VideoModes;
			supportedVideoModes = new VideoMode[modes.Length];
			for (int i = 0; i < modes.Length; i++)
			{
				supportedVideoModes[i] = new VideoMode(modes[i]);
			}
		}

		/// <summary>The BRUTAL framework's monitor handle.</summary>
		public GlfwMonitor Handle { get; }

		public string Name { get; }

		/// <summary>The display's rectangle on the desktop, in screen coordinates.</summary>
		public Box2i ClientArea { get; }

		/// <summary>The rectangle not taken by the system's bars.</summary>
		public Box2i WorkArea { get; }

		public int HorizontalResolution => ClientArea.Size.X;
		public int VerticalResolution => ClientArea.Size.Y;
		public float HorizontalScale { get; }
		public float VerticalScale { get; }
		public VideoMode CurrentVideoMode { get; }
		public IReadOnlyList<VideoMode> SupportedVideoModes => supportedVideoModes;
	}

	public static class Monitors
	{
		public static MonitorInfo GetPrimaryMonitor()
		{
			WindowSystem.EnsureInitialized();
			return new MonitorInfo(Glfw.PrimaryMonitor);
		}

		public static List<MonitorInfo> GetMonitors()
		{
			WindowSystem.EnsureInitialized();
			ReadOnlySpan<GlfwMonitor> monitors = Glfw.GetMonitors();
			List<MonitorInfo> result = new List<MonitorInfo>(monitors.Length);
			for (int i = 0; i < monitors.Length; i++)
			{
				result.Add(new MonitorInfo(monitors[i]));
			}

			return result;
		}

		/// <summary>
		///     The monitor a window is on: its fullscreen monitor, else the one its client rectangle overlaps most.
		/// </summary>
		public static MonitorInfo GetMonitorFromWindow(NativeWindow window)
		{
			GlfwMonitor fullscreen = Glfw.PInvoke.GetWindowMonitor(window.Handle);
			if (!fullscreen.IsNull())
			{
				return new MonitorInfo(fullscreen);
			}

			Glfw.PInvoke.GetWindowPos(window.Handle, out int x, out int y);
			Glfw.PInvoke.GetWindowSize(window.Handle, out int width, out int height);
			Box2i area = new Box2i(x, y, x + width, y + height);
			List<MonitorInfo> monitors = GetMonitors();
			int selected = 0;
			for (int i = 0; i < monitors.Count; i++)
			{
				if (IntersectionArea(monitors[i].ClientArea, area) > IntersectionArea(monitors[selected].ClientArea, area))
				{
					selected = i;
				}
			}

			return monitors[selected];
		}

		private static int IntersectionArea(Box2i a, Box2i b)
		{
			Box2i area = Box2i.Intersect(a, b);
			return area.Size.X * area.Size.Y;
		}
	}
}
