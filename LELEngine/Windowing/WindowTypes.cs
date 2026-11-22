using System;
using LELEngine.Mathematics;

namespace LELEngine.Windowing
{
	/// <summary>How the mouse cursor behaves over a window.</summary>
	public enum CursorState
	{
		/// <summary>Visible and free.</summary>
		Normal = 0,

		/// <summary>Hidden while over the window, free to leave it.</summary>
		Hidden = 1,

		/// <summary>Hidden and locked to the window: unlimited movement for camera controls.</summary>
		Grabbed = 2,

		/// <summary>Visible and kept inside the window.</summary>
		Confined = 3
	}

	public enum WindowState
	{
		Normal = 0,
		Minimized = 1,
		Maximized = 2,
		Fullscreen = 3
	}

	public enum WindowBorder
	{
		Resizable = 0,
		Fixed = 1,
		Hidden = 2
	}

	public enum VSyncMode
	{
		Off = 0,
		On = 1,

		/// <summary>On while the frame rate keeps up with the display, off below it (<see cref="GameWindow" /> switches).</summary>
		Adaptive = 2
	}

	/// <summary>Which graphics API a window's context is made for.</summary>
	public enum ContextAPI
	{
		/// <summary>No context: the window is presented to by Vulkan.</summary>
		NoAPI = 0,
		OpenGLES = 1,
		OpenGL = 2
	}

	public enum ContextProfile
	{
		Any = 0,
		Compatability = 1,
		Core = 2
	}

	[Flags]
	public enum ContextFlags
	{
		Default = 0,
		Debug = 1,
		ForwardCompatible = 2,
		Offscreen = 8
	}

	public readonly struct FrameEventArgs
	{
		/// <summary>Seconds since the previous frame's event.</summary>
		public double Time { get; }

		public FrameEventArgs(double elapsed)
		{
			Time = elapsed;
		}
	}

	public readonly struct KeyboardKeyEventArgs
	{
		public Keys Key { get; }
		public int ScanCode { get; }
		public KeyModifiers Modifiers { get; }
		public bool IsRepeat { get; }
		public bool Alt => (Modifiers & KeyModifiers.Alt) != 0;
		public bool Control => (Modifiers & KeyModifiers.Control) != 0;
		public bool Shift => (Modifiers & KeyModifiers.Shift) != 0;
		public bool Command => (Modifiers & KeyModifiers.Super) != 0;

		public KeyboardKeyEventArgs(Keys key, int scanCode, KeyModifiers modifiers, bool isRepeat)
		{
			Key = key;
			ScanCode = scanCode;
			Modifiers = modifiers;
			IsRepeat = isRepeat;
		}
	}

	public readonly struct TextInputEventArgs
	{
		/// <summary>The Unicode code point typed.</summary>
		public int Unicode { get; }

		public string AsString => char.ConvertFromUtf32(Unicode);

		public TextInputEventArgs(int unicode)
		{
			Unicode = unicode;
		}
	}

	public readonly struct MouseButtonEventArgs
	{
		public MouseButton Button { get; }
		public InputAction Action { get; }
		public KeyModifiers Modifiers { get; }
		public bool IsPressed => Action != InputAction.Release;

		public MouseButtonEventArgs(MouseButton button, InputAction action, KeyModifiers modifiers)
		{
			Button = button;
			Action = action;
			Modifiers = modifiers;
		}
	}

	public readonly struct MouseMoveEventArgs
	{
		public Vector2 Position { get; }
		public Vector2 Delta { get; }
		public float X => Position.X;
		public float Y => Position.Y;
		public float DeltaX => Delta.X;
		public float DeltaY => Delta.Y;

		public MouseMoveEventArgs(Vector2 position, Vector2 delta)
		{
			Position = position;
			Delta = delta;
		}

		public MouseMoveEventArgs(float x, float y, float deltaX, float deltaY)
			: this(new Vector2(x, y), new Vector2(deltaX, deltaY))
		{ }
	}

	public readonly struct MouseWheelEventArgs
	{
		public Vector2 Offset { get; }
		public float OffsetX => Offset.X;
		public float OffsetY => Offset.Y;

		public MouseWheelEventArgs(Vector2 offset)
		{
			Offset = offset;
		}

		public MouseWheelEventArgs(float offsetX, float offsetY)
			: this(new Vector2(offsetX, offsetY))
		{ }
	}

	public readonly struct ResizeEventArgs
	{
		public Vector2i Size { get; }
		public int Width => Size.X;
		public int Height => Size.Y;

		public ResizeEventArgs(Vector2i size)
		{
			Size = size;
		}

		public ResizeEventArgs(int width, int height)
			: this(new Vector2i(width, height))
		{ }
	}

	public readonly struct FramebufferResizeEventArgs
	{
		public Vector2i Size { get; }
		public int Width => Size.X;
		public int Height => Size.Y;

		public FramebufferResizeEventArgs(Vector2i size)
		{
			Size = size;
		}

		public FramebufferResizeEventArgs(int width, int height)
			: this(new Vector2i(width, height))
		{ }
	}

	public readonly struct WindowPositionEventArgs
	{
		public Vector2i Position { get; }
		public int X => Position.X;
		public int Y => Position.Y;

		public WindowPositionEventArgs(Vector2i position)
		{
			Position = position;
		}

		public WindowPositionEventArgs(int x, int y)
			: this(new Vector2i(x, y))
		{ }
	}

	public readonly struct FocusedChangedEventArgs
	{
		public bool IsFocused { get; }

		public FocusedChangedEventArgs(bool isFocused)
		{
			IsFocused = isFocused;
		}
	}

	public readonly struct MinimizedEventArgs
	{
		public bool IsMinimized { get; }

		public MinimizedEventArgs(bool isMinimized)
		{
			IsMinimized = isMinimized;
		}
	}

	public readonly struct MaximizedEventArgs
	{
		public bool IsMaximized { get; }

		public MaximizedEventArgs(bool isMaximized)
		{
			IsMaximized = isMaximized;
		}
	}

	public readonly struct FileDropEventArgs
	{
		public string[] FileNames { get; }

		public FileDropEventArgs(string[] paths)
		{
			FileNames = paths;
		}
	}

	/// <summary>How a <see cref="GameWindow" /> runs its loop.</summary>
	public class GameWindowSettings
	{
		public static readonly GameWindowSettings Default = new GameWindowSettings();

		/// <summary>Frames a second the loop aims for; 0 runs as fast as it can.</summary>
		public double UpdateFrequency { get; set; }
	}

	/// <summary>What a <see cref="NativeWindow" /> is created with.</summary>
	public class NativeWindowSettings
	{
		public static readonly NativeWindowSettings Default = new NativeWindowSettings();

		/// <summary>A window whose graphics context this one shares its objects with (textures, buffers, programs).</summary>
		public GraphicsContext SharedContext { get; set; }

		/// <summary>The loop waits for events instead of polling them.</summary>
		public bool IsEventDriven { get; set; }

		public ContextAPI API { get; set; } = ContextAPI.OpenGL;
		public ContextProfile Profile { get; set; } = ContextProfile.Core;
		public ContextFlags Flags { get; set; } = ContextFlags.ForwardCompatible;
		public Version APIVersion { get; set; } = new Version(3, 3);
		public string Title { get; set; } = "LELEngine";
		public bool StartFocused { get; set; } = true;
		public bool StartVisible { get; set; } = true;
		public WindowState WindowState { get; set; } = WindowState.Normal;
		public WindowBorder WindowBorder { get; set; } = WindowBorder.Resizable;

		/// <summary>Where the window's frame starts on the desktop; null leaves it to the system.</summary>
		public Vector2i? Location { get; set; }

		public Vector2i ClientSize { get; set; } = new Vector2i(640, 360);
		public int NumberOfSamples { get; set; }
		public bool SrgbCapable { get; set; }
		public VSyncMode Vsync { get; set; } = VSyncMode.Off;
		public bool AutoIconify { get; set; } = true;
	}
}
