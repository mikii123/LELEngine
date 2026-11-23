using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using Brutal.GlfwApi;
using Brutal.Numerics;
using LELEngine.Mathematics;

namespace LELEngine.Windowing
{
	/// <summary>
	///     A window of the desktop with its input and, unless made for Vulkan, its OpenGL context: the BRUTAL
	///     framework's GLFW window (<see cref="Handle" />) with events, input state and window states on top.
	///     Windows are created, read and destroyed on the main thread; a context may be handed to another thread.
	/// </summary>
	public class NativeWindow : IDisposable
	{
		// What the window was before it went fullscreen: restored when it leaves.
		private Vector2i cachedClientSize;
		private Vector2i cachedLocation;

		private Vector2 lastReportedMousePosition;

		// Exceptions thrown inside GLFW's callbacks, thrown again once the events are processed (an exception cannot
		// cross the native frames of the event loop).
		private static readonly ConcurrentQueue<ExceptionDispatchInfo> callbackExceptions = new ConcurrentQueue<ExceptionDispatchInfo>();
		private static readonly List<ExceptionDispatchInfo> pendingExceptions = new List<ExceptionDispatchInfo>();

		// The living windows by their native handle (any thread may ask whose context is current on it).
		private static readonly ConcurrentDictionary<IntPtr, NativeWindow> windows = new ConcurrentDictionary<IntPtr, NativeWindow>();

		/// <summary>The window whose graphics context is current on the calling thread; null when none of these windows' is.</summary>
		public static NativeWindow Current
		{
			get
			{
				IntPtr context = GraphicsContext.CurrentWindowPtr;
				return context != IntPtr.Zero && windows.TryGetValue(context, out NativeWindow window) ? window : null;
			}
		}

		private GlfwCursor glfwCursor;
		private MouseCursor cursor = MouseCursor.Default;
		private VSyncMode vSync;
		private string title;
		private bool isFocused;
		private bool isVisible;
		private WindowState windowState = WindowState.Normal;
		private WindowBorder windowBorder;
		private bool disposed;

		/// <summary>The BRUTAL framework's window.</summary>
		public GlfwWindow Handle { get; private set; }

		/// <summary>The native GLFW window (GLFWwindow*).</summary>
		public IntPtr WindowPtr => Handle != null ? Handle.NativeHandle : IntPtr.Zero;

		/// <summary>The window's Win32 handle (HWND); zero on other systems.</summary>
		public IntPtr Win32Window => Handle != null && OperatingSystem.IsWindows() ? Glfw.PInvoke.GetWin32Window(Handle) : IntPtr.Zero;

		public KeyboardState KeyboardState { get; } = new KeyboardState();
		public MouseState MouseState { get; } = new MouseState();
		public ContextAPI API { get; }
		public ContextProfile Profile { get; }
		public ContextFlags Flags { get; }
		public Version APIVersion { get; }

		/// <summary>The window's OpenGL context; null for a window made without one (<see cref="ContextAPI.NoAPI" />).</summary>
		public GraphicsContext Context { get; }

		/// <summary>The loop waits for events instead of polling them.</summary>
		public bool IsEventDriven { get; set; }

		public bool Exists { get; private set; }

		public event Action<WindowPositionEventArgs> Move;
		public event Action<ResizeEventArgs> Resize;
		public event Action<FramebufferResizeEventArgs> FramebufferResize;
		public event Action Refresh;
		public event Action<CancelEventArgs> Closing;
		public event Action<MinimizedEventArgs> Minimized;
		public event Action<MaximizedEventArgs> Maximized;
		public event Action<FocusedChangedEventArgs> FocusedChanged;
		public event Action<KeyboardKeyEventArgs> KeyDown;
		public event Action<TextInputEventArgs> TextInput;
		public event Action<KeyboardKeyEventArgs> KeyUp;
		public event Action MouseLeave;
		public event Action MouseEnter;
		public event Action<MouseButtonEventArgs> MouseDown;
		public event Action<MouseButtonEventArgs> MouseUp;
		public event Action<MouseMoveEventArgs> MouseMove;
		public event Action<MouseWheelEventArgs> MouseWheel;
		public event Action<FileDropEventArgs> FileDrop;

		public NativeWindow(NativeWindowSettings settings)
		{
			WindowSystem.EnsureInitialized();
			title = settings.Title;

			// Hints stay set for the windows created later: every creation sets the same ones.
			switch (settings.WindowBorder)
			{
				case WindowBorder.Hidden:
					Glfw.WindowHint(GlfwWindowHint.Decorated, 0);
					break;
				case WindowBorder.Resizable:
					Glfw.WindowHint(GlfwWindowHint.Resizable, 1);
					break;
				case WindowBorder.Fixed:
					Glfw.WindowHint(GlfwWindowHint.Resizable, 0);
					break;
			}

			API = settings.API;
			// A window that draws gets the context of the process's graphics device: an OpenGL context of its own,
			// or (Vulkan) no client API at all: the device makes it a surface and a swap chain.
			bool drawn = settings.API != ContextAPI.NoAPI;
			bool vulkan = drawn && Graphics.GraphicsDevice.CurrentApi == Graphics.GraphicsApi.Vulkan;
			if (!drawn || vulkan)
			{
				Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);
			}
			else if (settings.API == ContextAPI.OpenGLES)
			{
				Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_OPENGL_ES_API);
			}
			else
			{
				Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_OPENGL_API);
			}

			Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, settings.APIVersion.Major);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, settings.APIVersion.Minor);
			APIVersion = settings.APIVersion;
			Flags = settings.Flags;
			if ((settings.Flags & ContextFlags.ForwardCompatible) != 0)
			{
				Glfw.WindowHint(GlfwWindowHint.OpenGlForwardCompat, 1);
			}

			if ((settings.Flags & ContextFlags.Debug) != 0)
			{
				Glfw.WindowHint(GlfwWindowHint.OpenGlDebugContext, 1);
			}

			Profile = settings.Profile;
			switch (settings.Profile)
			{
				case ContextProfile.Any:
					Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_ANY_PROFILE);
					break;
				case ContextProfile.Compatability:
					Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_COMPAT_PROFILE);
					break;
				default:
					Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_CORE_PROFILE);
					break;
			}

			Glfw.WindowHint(GlfwWindowHint.Focused, settings.StartFocused ? 1 : 0);
			windowBorder = settings.WindowBorder;
			isVisible = settings.StartVisible;
			Glfw.WindowHint(GlfwWindowHint.Visible, isVisible ? 1 : 0);
			Glfw.WindowHint(GlfwWindowHint.Samples, settings.NumberOfSamples);
			Glfw.WindowHint(GlfwWindowHint.SrgbCapable, settings.SrgbCapable ? 1 : 0);
			Glfw.WindowHint(GlfwWindowHint.AutoIconify, settings.AutoIconify ? 1 : 0);

			GlfwMonitor monitor = Glfw.PrimaryMonitor;
			GlfwVidMode mode = monitor.VideoMode;
			Glfw.WindowHint(GlfwWindowHint.RedBits, mode.RedBits);
			Glfw.WindowHint(GlfwWindowHint.GreenBits, mode.GreenBits);
			Glfw.WindowHint(GlfwWindowHint.BlueBits, mode.BlueBits);
			Glfw.WindowHint(GlfwWindowHint.RefreshRate, mode.RefreshRate);

			cachedLocation = settings.Location ?? new Vector2i(32, 32);
			cachedClientSize = settings.ClientSize;

			// Vulkan shares nothing at the window's level: every window of the process draws with the one device.
			GlfwWindow share = vulkan ? null : settings.SharedContext?.Window;
			if (settings.WindowState == WindowState.Fullscreen && isVisible)
			{
				windowState = WindowState.Fullscreen;
				Handle = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = title, Size = new int2(mode.Width, mode.Height), Monitor = monitor, Share = share });
			}
			else
			{
				Handle = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = title, Size = new int2(settings.ClientSize.X, settings.ClientSize.Y), Share = share });
			}

			Exists = true;
			windows[Handle.NativeHandle] = this;
			if (drawn)
			{
				// The graphics device starts with the process's first window that draws; the window's context is
				// current on this thread from here on.
				try
				{
					Context = Graphics.GraphicsDevice.CreateContext(this, settings);
				}
				catch
				{
					// No window is left behind: the caller may make it again for another graphics API.
					disposed = true;
					windows.TryRemove(Handle.NativeHandle, out _);
					Handle.Dispose();
					Exists = false;
					GC.SuppressFinalize(this);
					throw;
				}

				Context.MakeCurrent();
			}

			if (settings.WindowState != WindowState.Fullscreen && isVisible)
			{
				// A window that starts maximized or minimized.
				WindowState = settings.WindowState;
			}

			// The lock keys' state arrives with the key events' modifiers.
			Handle.SetInputMode(GlfwInputMode.LockKeyMods, 1);
			RegisterCallbacks();

			isFocused = settings.StartFocused;
			if (OperatingSystem.IsMacOS() && !isVisible)
			{
				// Focusing a hidden window would show it there.
				isFocused = false;
			}

			if (isFocused)
			{
				Focus();
			}

			IsEventDriven = settings.IsEventDriven;
			if (settings.Location.HasValue)
			{
				Location = settings.Location.Value;
			}

			Glfw.PInvoke.GetCursorPos(Handle, out double mouseX, out double mouseY);
			lastReportedMousePosition = new Vector2((float)mouseX, (float)mouseY);
			MouseState.Position = lastReportedMousePosition;
			isFocused = Handle.GetAttribute(GlfwWindowAttribute.Focused) != 0;

			if (Context != null)
			{
				VSync = settings.Vsync;
			}
		}

		#region State

		public Vector2 MousePosition
		{
			get => lastReportedMousePosition;
			set => Glfw.PInvoke.SetCursorPos(Handle, value.X, value.Y);
		}

		public bool IsAnyKeyDown => KeyboardState.IsAnyKeyDown;
		public bool IsAnyMouseButtonDown => MouseState.IsAnyButtonDown;

		public VSyncMode VSync
		{
			get
			{
				if (Context == null)
				{
					throw new InvalidOperationException("A window without an OpenGL context has no swap interval: presentation is the graphics backend's.");
				}

				return vSync;
			}
			set
			{
				if (Context == null)
				{
					throw new InvalidOperationException("A window without an OpenGL context has no swap interval: presentation is the graphics backend's.");
				}

				// Adaptive is switched by the game window's loop.
				switch (value)
				{
					case VSyncMode.On:
						Context.SwapInterval = 1;
						break;
					case VSyncMode.Off:
						Context.SwapInterval = 0;
						break;
				}

				vSync = value;
			}
		}

		public string ClipboardString
		{
			get => Glfw.Clipboard;
			set => Glfw.Clipboard = value ?? string.Empty;
		}

		public string Title
		{
			get => title;
			set
			{
				Glfw.PInvoke.SetWindowTitle(Handle, value);
				title = value;
			}
		}

		public MonitorInfo CurrentMonitor => Monitors.GetMonitorFromWindow(this);

		public bool IsFocused => isFocused;

		public bool IsVisible
		{
			get => isVisible;
			set
			{
				if (value != isVisible)
				{
					isVisible = value;
					if (isVisible)
					{
						Glfw.PInvoke.ShowWindow(Handle);
					}
					else
					{
						Glfw.PInvoke.HideWindow(Handle);
					}
				}
			}
		}

		/// <summary>The window was asked to close and its loop will end.</summary>
		public bool IsExiting => Glfw.PInvoke.WindowShouldClose(Handle) != 0;

		public WindowState WindowState
		{
			get => windowState;
			set
			{
				// Set before any callback runs, so that a resize handler already reads the new state.
				WindowState previous = windowState;
				windowState = value;

				if (previous == WindowState.Fullscreen && value != WindowState.Fullscreen)
				{
					Glfw.PInvoke.SetWindowMonitor(Handle, default, cachedLocation.X, cachedLocation.Y, cachedClientSize.X, cachedClientSize.Y, Glfw.Constants.GLFW_DONT_CARE);
				}

				switch (value)
				{
					case WindowState.Normal:
						Handle.Restore();
						break;
					case WindowState.Minimized:
						Handle.Iconify();
						break;
					case WindowState.Maximized:
						Handle.Maximize();
						break;
					case WindowState.Fullscreen:
						if (previous != WindowState.Fullscreen)
						{
							cachedClientSize = ClientSize;
							cachedLocation = ClientLocation;
						}

						GlfwMonitor monitor = CurrentMonitor.Handle;
						GlfwVidMode mode = monitor.VideoMode;
						Glfw.PInvoke.SetWindowMonitor(Handle, monitor, 0, 0, mode.Width, mode.Height, mode.RefreshRate);
						break;
				}
			}
		}

		public bool IsFullscreen => WindowState == WindowState.Fullscreen;

		public WindowBorder WindowBorder
		{
			get => windowBorder;
			set
			{
				switch (value)
				{
					case WindowBorder.Hidden:
						Handle.SetAttribute(GlfwWindowAttribute.Decorated, 0);
						break;
					case WindowBorder.Resizable:
						Handle.SetAttribute(GlfwWindowAttribute.Decorated, 1);
						Handle.SetAttribute(GlfwWindowAttribute.Resizable, 1);
						break;
					case WindowBorder.Fixed:
						Handle.SetAttribute(GlfwWindowAttribute.Decorated, 1);
						Handle.SetAttribute(GlfwWindowAttribute.Resizable, 0);
						break;
				}

				windowBorder = value;
			}
		}

		/// <summary>The window's frame on the desktop (decorations included).</summary>
		public Box2i Bounds => new Box2i(Location, Location + Size);

		/// <summary>Where the window's frame starts on the desktop.</summary>
		public Vector2i Location
		{
			get
			{
				Handle.GetWindowFrameSize(out int left, out int top, out _, out _);
				Glfw.PInvoke.GetWindowPos(Handle, out int x, out int y);
				return new Vector2i(x - left, y - top);
			}
			set
			{
				Handle.GetWindowFrameSize(out int left, out int top, out _, out _);
				Glfw.PInvoke.SetWindowPos(Handle, value.X + left, value.Y + top);
			}
		}

		/// <summary>Where the window's content starts on the desktop.</summary>
		public Vector2i ClientLocation
		{
			get
			{
				Glfw.PInvoke.GetWindowPos(Handle, out int x, out int y);
				return new Vector2i(x, y);
			}
			set => Glfw.PInvoke.SetWindowPos(Handle, value.X, value.Y);
		}

		/// <summary>The window's size with its frame.</summary>
		public Vector2i Size
		{
			get
			{
				Handle.GetWindowFrameSize(out int left, out int top, out int right, out int bottom);
				Glfw.PInvoke.GetWindowSize(Handle, out int width, out int height);
				return new Vector2i(width + left + right, height + top + bottom);
			}
			set
			{
				Handle.GetWindowFrameSize(out int left, out int top, out int right, out int bottom);
				Glfw.PInvoke.SetWindowSize(Handle, Math.Max(value.X - left - right, 0), Math.Max(value.Y - top - bottom, 0));
			}
		}

		/// <summary>The size of the window's content, in screen coordinates.</summary>
		public Vector2i ClientSize
		{
			get
			{
				Glfw.PInvoke.GetWindowSize(Handle, out int width, out int height);
				return new Vector2i(width, height);
			}
			set => Glfw.PInvoke.SetWindowSize(Handle, value.X, value.Y);
		}

		/// <summary>The size of the window's content in pixels (what is drawn into).</summary>
		public Vector2i FramebufferSize
		{
			get
			{
				Glfw.PInvoke.GetFramebufferSize(Handle, out int width, out int height);
				return new Vector2i(width, height);
			}
		}

		public Box2i ClientRectangle
		{
			get => new Box2i(ClientLocation, ClientLocation + ClientSize);
			set
			{
				ClientLocation = value.Min;
				ClientSize = value.Size;
			}
		}

		public MouseCursor Cursor
		{
			get => cursor;
			set
			{
				if (value == null)
				{
					throw new ArgumentNullException(nameof(value), "A window always has a cursor: MouseCursor.Default is the system's arrow.");
				}

				if (ReferenceEquals(value, cursor))
				{
					return;
				}

				cursor = value;
				GlfwCursor old = glfwCursor;
				// The default cursor is no cursor object: GLFW shows the system's arrow.
				glfwCursor = ReferenceEquals(value, MouseCursor.Default) ? default : Glfw.CreateCursor(value.ToGlfw());
				Handle.SetCursor(glfwCursor);
				if (!old.IsNull())
				{
					// After the new one is set, so that the system's cursor does not show in between.
					old.Dispose();
				}
			}
		}

		public CursorState CursorState
		{
			get
			{
				switch ((GlfwCursorMode)Handle.GetInputMode(GlfwInputMode.Cursor))
				{
					case GlfwCursorMode.Hidden: return CursorState.Hidden;
					case GlfwCursorMode.Disabled: return CursorState.Grabbed;
					case GlfwCursorMode.Captured: return CursorState.Confined;
					default: return CursorState.Normal;
				}
			}
			set
			{
				GlfwCursorMode mode;
				switch (value)
				{
					case CursorState.Hidden:
						mode = GlfwCursorMode.Hidden;
						break;
					case CursorState.Grabbed:
						mode = GlfwCursorMode.Disabled;
						break;
					case CursorState.Confined:
						mode = GlfwCursorMode.Captured;
						break;
					default:
						mode = GlfwCursorMode.Normal;
						break;
				}

				Handle.SetInputMode(GlfwInputMode.Cursor, (int)mode);
			}
		}

		#endregion

		#region Methods

		public void Focus()
		{
			Handle.Focus();
		}

		/// <summary>Asks the window to close: <see cref="Closing" /> may refuse.</summary>
		public virtual void Close()
		{
			CancelEventArgs e = new CancelEventArgs();
			OnClosing(e);
			if (!e.Cancel)
			{
				Glfw.PInvoke.SetWindowShouldClose(Handle, 1);
			}
		}

		public void MakeCurrent()
		{
			if (Context == null)
			{
				throw new InvalidOperationException("A window without an OpenGL context has no context to make current.");
			}

			Context.MakeCurrent();
		}

		/// <summary>
		///     Starts an input frame and waits for events for at most <paramref name="timeout" /> seconds, handling
		///     those that came (every window's: GLFW's event queue is the thread's).
		/// </summary>
		public bool ProcessEvents(double timeout)
		{
			NewInputFrame();
			Glfw.WaitEventsTimeout(timeout);
			RethrowCallbackExceptions();
			return true;
		}

		/// <summary>Handles the events that came for every window: waiting for one first, or only those already there.</summary>
		public static void ProcessWindowEvents(bool waitForEvents)
		{
			if (waitForEvents)
			{
				Glfw.WaitEvents();
			}
			else
			{
				Glfw.PollEvents();
			}

			RethrowCallbackExceptions();
		}

		/// <summary>What is down now becomes "was down"; the mouse position is read.</summary>
		public void NewInputFrame()
		{
			MouseState.NewFrame(Handle);
			KeyboardState.NewFrame();
		}

		public Vector2i PointToClient(Vector2i point)
		{
			return point - ClientLocation;
		}

		public Vector2i PointToScreen(Vector2i point)
		{
			return point + ClientLocation;
		}

		public bool IsKeyDown(Keys key)
		{
			return KeyboardState.IsKeyDown(key);
		}

		public bool IsKeyPressed(Keys key)
		{
			return KeyboardState.IsKeyDown(key) && !KeyboardState.WasKeyDown(key);
		}

		public bool IsKeyReleased(Keys key)
		{
			return !KeyboardState.IsKeyDown(key) && KeyboardState.WasKeyDown(key);
		}

		public bool IsMouseButtonDown(MouseButton button)
		{
			return MouseState.IsButtonDown(button);
		}

		public bool IsMouseButtonPressed(MouseButton button)
		{
			return MouseState.IsButtonDown(button) && !MouseState.WasButtonDown(button);
		}

		public bool IsMouseButtonReleased(MouseButton button)
		{
			return !MouseState.IsButtonDown(button) && MouseState.WasButtonDown(button);
		}

		public void CenterWindow()
		{
			CenterWindow(ClientSize);
		}

		/// <summary>Gives the window's content a size and puts it in the middle of the monitor it is on.</summary>
		public void CenterWindow(Vector2i newSize)
		{
			Box2i monitor = Monitors.GetMonitorFromWindow(this).ClientArea;
			int x = Math.Max((monitor.Min.X + monitor.Max.X - newSize.X) / 2, monitor.Min.X);
			int y = Math.Max((monitor.Min.Y + monitor.Max.Y - newSize.Y) / 2, monitor.Min.Y);
			ClientRectangle = new Box2i(x, y, x + newSize.X, y + newSize.Y);
		}

		public virtual void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			if (Handle != null)
			{
				windows.TryRemove(Handle.NativeHandle, out _);
				Context?.Dispose();
				Handle.Dispose();
				Exists = false;
			}

			GC.SuppressFinalize(this);
		}

		#endregion

		#region Events

		protected virtual void OnMove(WindowPositionEventArgs e)
		{
			Move?.Invoke(e);
		}

		protected virtual void OnResize(ResizeEventArgs e)
		{
			Resize?.Invoke(e);
		}

		protected virtual void OnFramebufferResize(FramebufferResizeEventArgs e)
		{
			FramebufferResize?.Invoke(e);
		}

		protected virtual void OnRefresh()
		{
			Refresh?.Invoke();
		}

		protected virtual void OnClosing(CancelEventArgs e)
		{
			Closing?.Invoke(e);
		}

		protected virtual void OnFocusedChanged(FocusedChangedEventArgs e)
		{
			FocusedChanged?.Invoke(e);
			isFocused = e.IsFocused;
		}

		protected virtual void OnKeyDown(KeyboardKeyEventArgs e)
		{
			KeyDown?.Invoke(e);
		}

		protected virtual void OnTextInput(TextInputEventArgs e)
		{
			TextInput?.Invoke(e);
		}

		protected virtual void OnKeyUp(KeyboardKeyEventArgs e)
		{
			KeyUp?.Invoke(e);
		}

		protected virtual void OnMouseLeave()
		{
			MouseLeave?.Invoke();
		}

		protected virtual void OnMouseEnter()
		{
			MouseEnter?.Invoke();
		}

		protected virtual void OnMouseDown(MouseButtonEventArgs e)
		{
			MouseDown?.Invoke(e);
		}

		protected virtual void OnMouseUp(MouseButtonEventArgs e)
		{
			MouseUp?.Invoke(e);
		}

		protected virtual void OnMouseMove(MouseMoveEventArgs e)
		{
			MouseMove?.Invoke(e);
		}

		protected virtual void OnMouseWheel(MouseWheelEventArgs e)
		{
			MouseWheel?.Invoke(e);
		}

		protected virtual void OnMinimized(MinimizedEventArgs e)
		{
			windowState = e.IsMinimized ? WindowState.Minimized : StateFromGlfw();
			Minimized?.Invoke(e);
		}

		protected virtual void OnMaximized(MaximizedEventArgs e)
		{
			windowState = e.IsMaximized ? WindowState.Maximized : StateFromGlfw();
			Maximized?.Invoke(e);
		}

		protected virtual void OnFileDrop(FileDropEventArgs e)
		{
			FileDrop?.Invoke(e);
		}

		#endregion

		#region Callbacks

		private WindowState StateFromGlfw()
		{
			if (Handle.GetAttribute(GlfwWindowAttribute.Iconified) != 0)
			{
				return WindowState.Minimized;
			}

			if (Handle.GetAttribute(GlfwWindowAttribute.Maximized) != 0)
			{
				return WindowState.Maximized;
			}

			return Glfw.PInvoke.GetWindowMonitor(Handle).IsNull() ? WindowState.Normal : WindowState.Fullscreen;
		}

		private void RegisterCallbacks()
		{
			Handle.OnPosition += (_, position) => Guarded(() => OnMove(new WindowPositionEventArgs(position.X, position.Y)));
			Handle.OnSize += (_, size) => Guarded(() => OnResize(new ResizeEventArgs(size.X, size.Y)));
			Handle.OnFramebufferSize += (_, size) => Guarded(() => OnFramebufferResize(new FramebufferResizeEventArgs(size.X, size.Y)));
			Handle.OnRefresh += _ => Guarded(OnRefresh);
			Handle.OnFocus += (_, focus) => Guarded(() => OnFocusedChanged(new FocusedChangedEventArgs((int)focus != 0)));
			Handle.OnIconify += (_, iconify) => Guarded(() => OnMinimized(new MinimizedEventArgs((int)iconify != 0)));
			Handle.OnMaximize += (_, maximize) => Guarded(() => OnMaximized(new MaximizedEventArgs((int)maximize != 0)));
			Handle.OnClose += _ => Guarded(() =>
			{
				CancelEventArgs e = new CancelEventArgs();
				OnClosing(e);
				if (e.Cancel)
				{
					Glfw.PInvoke.SetWindowShouldClose(Handle, 0);
				}
			});
			Handle.OnMouseButton += OnGlfwMouseButton;
			Handle.OnCursorPos += OnGlfwCursorPosition;
			Handle.OnCursorEnter += (_, entered) => Guarded(() =>
			{
				if (entered)
				{
					OnMouseEnter();
				}
				else
				{
					OnMouseLeave();
				}
			});
			Handle.OnScroll += OnGlfwScroll;
			Handle.OnKey += OnGlfwKey;
			Handle.OnDrop += (_, _, paths) => Guarded(() => OnFileDrop(new FileDropEventArgs(paths)));
			RegisterCharCallback();
		}

		// Rooted: GLFW keeps only the native function pointer.
		private GlfwCharUnmanagedCallback charCallback;

		/// <summary>
		///     Typed characters straight from GLFW: the framework's own character event narrows the code point to 16 bits,
		///     which loses the characters past the basic plane (emoji, rare scripts).
		/// </summary>
		private unsafe void RegisterCharCallback()
		{
			charCallback = (_, codepoint) => OnGlfwChar(codepoint);
			Glfw.PInvoke.SetCharCallback(Handle, charCallback);
		}

		private void OnGlfwMouseButton(GlfwWindow window, GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
		{
			try
			{
				MouseButton engineButton = (MouseButton)(int)button;
				MouseButtonEventArgs e = new MouseButtonEventArgs(engineButton, (InputAction)(int)action, (KeyModifiers)(int)mods);
				if (action == GlfwButtonAction.Release)
				{
					MouseState[engineButton] = false;
					OnMouseUp(e);
				}
				else
				{
					MouseState[engineButton] = true;
					OnMouseDown(e);
				}
			}
			catch (Exception exception)
			{
				callbackExceptions.Enqueue(ExceptionDispatchInfo.Capture(exception));
			}
		}

		private void OnGlfwCursorPosition(GlfwWindow window, double2 position)
		{
			try
			{
				Vector2 newPosition = new Vector2((float)position.X, (float)position.Y);
				Vector2 delta = newPosition - lastReportedMousePosition;
				lastReportedMousePosition = newPosition;
				OnMouseMove(new MouseMoveEventArgs(newPosition, delta));
			}
			catch (Exception exception)
			{
				callbackExceptions.Enqueue(ExceptionDispatchInfo.Capture(exception));
			}
		}

		private void OnGlfwScroll(GlfwWindow window, double2 offset)
		{
			try
			{
				Vector2 scroll = new Vector2((float)offset.X, (float)offset.Y);
				MouseState.Scroll += scroll;
				OnMouseWheel(new MouseWheelEventArgs(scroll));
			}
			catch (Exception exception)
			{
				callbackExceptions.Enqueue(ExceptionDispatchInfo.Capture(exception));
			}
		}

		private void OnGlfwKey(GlfwWindow window, GlfwKey key, int scanCode, GlfwKeyAction action, GlfwModifier mods)
		{
			try
			{
				Keys engineKey = (Keys)(int)key;
				KeyboardKeyEventArgs e = new KeyboardKeyEventArgs(engineKey, scanCode, (KeyModifiers)(int)mods, action == GlfwKeyAction.Repeat);
				bool known = engineKey != Keys.Unknown && (int)engineKey <= (int)Keys.LastKey;
				if (action == GlfwKeyAction.Release)
				{
					if (known)
					{
						KeyboardState.SetKeyState(engineKey, false);
					}

					OnKeyUp(e);
				}
				else
				{
					if (known)
					{
						KeyboardState.SetKeyState(engineKey, true);
					}

					OnKeyDown(e);
				}
			}
			catch (Exception exception)
			{
				callbackExceptions.Enqueue(ExceptionDispatchInfo.Capture(exception));
			}
		}

		private void OnGlfwChar(uint codepoint)
		{
			try
			{
				OnTextInput(new TextInputEventArgs((int)codepoint));
			}
			catch (Exception exception)
			{
				callbackExceptions.Enqueue(ExceptionDispatchInfo.Capture(exception));
			}
		}

		private static void Guarded(Action action)
		{
			try
			{
				action();
			}
			catch (Exception exception)
			{
				callbackExceptions.Enqueue(ExceptionDispatchInfo.Capture(exception));
			}
		}

		/// <summary>Throws what the events' handlers threw, on the thread that processed the events.</summary>
		private static void RethrowCallbackExceptions()
		{
			while (callbackExceptions.TryDequeue(out ExceptionDispatchInfo exception))
			{
				pendingExceptions.Add(exception);
			}

			if (pendingExceptions.Count == 1)
			{
				ExceptionDispatchInfo exception = pendingExceptions[0];
				pendingExceptions.Clear();
				exception.Throw();
			}
			else if (pendingExceptions.Count > 1)
			{
				Exception[] exceptions = new Exception[pendingExceptions.Count];
				for (int i = 0; i < exceptions.Length; i++)
				{
					exceptions[i] = pendingExceptions[i].SourceException;
				}

				pendingExceptions.Clear();
				throw new AggregateException("Several exceptions in the window events' handlers.", exceptions);
			}
		}

		#endregion
	}
}
