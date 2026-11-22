using System;
using System.Collections;
using System.Text;
using Brutal.GlfwApi;
using LELEngine.Mathematics;

namespace LELEngine.Windowing
{
	/// <summary>A window's mouse: where it is and which buttons are down, this frame and the frame before.</summary>
	public class MouseState
	{
		internal const int MaxButtons = 16;

		private readonly BitArray buttons = new BitArray(MaxButtons);
		private readonly BitArray buttonsPrevious = new BitArray(MaxButtons);

		internal MouseState()
		{ }

		public Vector2 Position { get; internal set; }
		public Vector2 PreviousPosition { get; internal set; }

		/// <summary>Movement since the previous frame, in pixels; unlimited while the cursor is grabbed.</summary>
		public Vector2 Delta => Position - PreviousPosition;

		public Vector2 Scroll { get; internal set; }
		public Vector2 PreviousScroll { get; internal set; }
		public Vector2 ScrollDelta => Scroll - PreviousScroll;
		public float X => Position.X;
		public float Y => Position.Y;
		public float PreviousX => PreviousPosition.X;
		public float PreviousY => PreviousPosition.Y;

		public bool this[MouseButton button]
		{
			get => buttons[(int)button];
			internal set => buttons[(int)button] = value;
		}

		public bool IsAnyButtonDown
		{
			get
			{
				for (int i = 0; i < MaxButtons; i++)
				{
					if (buttons[i])
					{
						return true;
					}
				}

				return false;
			}
		}

		public bool IsButtonDown(MouseButton button)
		{
			return buttons[(int)button];
		}

		public bool WasButtonDown(MouseButton button)
		{
			return buttonsPrevious[(int)button];
		}

		public bool IsButtonPressed(MouseButton button)
		{
			return buttons[(int)button] && !buttonsPrevious[(int)button];
		}

		public bool IsButtonReleased(MouseButton button)
		{
			return !buttons[(int)button] && buttonsPrevious[(int)button];
		}

		/// <summary>Starts a frame: what was current becomes previous, and the position is read from the window.</summary>
		internal void NewFrame(GlfwWindow window)
		{
			buttonsPrevious.SetAll(false);
			buttonsPrevious.Or(buttons);
			PreviousPosition = Position;
			PreviousScroll = Scroll;
			Glfw.PInvoke.GetCursorPos(window, out double x, out double y);
			Position = new Vector2((float)x, (float)y);
		}

		public override string ToString()
		{
			StringBuilder text = new StringBuilder();
			for (int i = 0; i < MaxButtons; i++)
			{
				text.Append(buttons[i] ? "1" : "0");
			}

			return $"[X={X}, Y={Y}, Buttons={text}]";
		}
	}

	/// <summary>A window's keyboard: which keys are down, this frame and the frame before.</summary>
	public class KeyboardState
	{
		private readonly BitArray keys = new BitArray((int)Keys.LastKey + 1);
		private readonly BitArray keysPrevious = new BitArray((int)Keys.LastKey + 1);

		internal KeyboardState()
		{ }

		public bool this[Keys key] => IsKeyDown(key);

		public bool IsAnyKeyDown
		{
			get
			{
				for (int i = 0; i < keys.Length; i++)
				{
					if (keys[i])
					{
						return true;
					}
				}

				return false;
			}
		}

		public bool IsKeyDown(Keys key)
		{
			return keys[(int)key];
		}

		public bool WasKeyDown(Keys key)
		{
			return keysPrevious[(int)key];
		}

		public bool IsKeyPressed(Keys key)
		{
			return keys[(int)key] && !keysPrevious[(int)key];
		}

		public bool IsKeyReleased(Keys key)
		{
			return !keys[(int)key] && keysPrevious[(int)key];
		}

		internal void SetKeyState(Keys key, bool down)
		{
			keys[(int)key] = down;
		}

		internal void NewFrame()
		{
			keysPrevious.SetAll(false);
			keysPrevious.Or(keys);
		}
	}

	/// <summary>A mouse cursor image: one of the system's shapes.</summary>
	public sealed class MouseCursor
	{
		public enum StandardShape
		{
			Arrow,
			IBeam,
			Crosshair,
			PointingHand,
			ResizeEW,
			ResizeNS,
			ResizeNWSE,
			ResizeNESW,
			ResizeAll,
			NotAllowed
		}

		public StandardShape Shape { get; }

		private MouseCursor(StandardShape shape)
		{
			Shape = shape;
		}

		/// <summary>The system's arrow: the cursor a window starts with.</summary>
		public static MouseCursor Default { get; } = new MouseCursor(StandardShape.Arrow);

		public static MouseCursor IBeam { get; } = new MouseCursor(StandardShape.IBeam);
		public static MouseCursor Crosshair { get; } = new MouseCursor(StandardShape.Crosshair);
		public static MouseCursor PointingHand { get; } = new MouseCursor(StandardShape.PointingHand);
		public static MouseCursor ResizeEW { get; } = new MouseCursor(StandardShape.ResizeEW);
		public static MouseCursor ResizeNS { get; } = new MouseCursor(StandardShape.ResizeNS);
		public static MouseCursor ResizeNWSE { get; } = new MouseCursor(StandardShape.ResizeNWSE);
		public static MouseCursor ResizeNESW { get; } = new MouseCursor(StandardShape.ResizeNESW);
		public static MouseCursor ResizeAll { get; } = new MouseCursor(StandardShape.ResizeAll);
		public static MouseCursor NotAllowed { get; } = new MouseCursor(StandardShape.NotAllowed);

		internal GlfwStandardCursor ToGlfw()
		{
			switch (Shape)
			{
				case StandardShape.IBeam: return GlfwStandardCursor.IBeam;
				case StandardShape.Crosshair: return GlfwStandardCursor.Crosshair;
				case StandardShape.PointingHand: return GlfwStandardCursor.PointingHand;
				case StandardShape.ResizeEW: return GlfwStandardCursor.ResizeEw;
				case StandardShape.ResizeNS: return GlfwStandardCursor.ResizeNs;
				case StandardShape.ResizeNWSE: return GlfwStandardCursor.ResizeNwse;
				case StandardShape.ResizeNESW: return GlfwStandardCursor.ResizeNesw;
				case StandardShape.ResizeAll: return GlfwStandardCursor.ResizeAll;
				case StandardShape.NotAllowed: return GlfwStandardCursor.NotAllowed;
				default: return GlfwStandardCursor.Arrow;
			}
		}
	}

	/// <summary>A gamepad's buttons and axes in the standard layout (GLFW's gamepad mapping).</summary>
	public unsafe struct GamepadState
	{
		public fixed byte Buttons[15];
		public fixed float Axes[6];
	}

	/// <summary>
	///     Joysticks and gamepads connected to the machine (GLFW's joystick slots 0..15). A host that never made a
	///     window (GLFW not started) has none.
	/// </summary>
	public static class Joysticks
	{
		public const int Count = 16;

		public static bool IsPresent(int index)
		{
			return WindowSystem.IsInitialized && Glfw.JoystickPresent(index);
		}

		/// <summary>The joystick has a gamepad mapping: <see cref="GetGamepadState" /> works for it.</summary>
		public static bool IsGamepad(int index)
		{
			return WindowSystem.IsInitialized && Glfw.JoystickIsGamepad(index);
		}

		public static string GetName(int index)
		{
			return WindowSystem.IsInitialized ? Glfw.GetJoystickName(index) : null;
		}

		public static string GetGamepadName(int index)
		{
			return WindowSystem.IsInitialized ? Glfw.GetGamepadName(index) : null;
		}

		/// <summary>The device's SDL-compatible GUID (the key of gamepad mappings).</summary>
		public static string GetGuid(int index)
		{
			return WindowSystem.IsInitialized ? Glfw.GetJoystickGuid(index) : null;
		}

		public static unsafe bool GetGamepadState(int index, out GamepadState state)
		{
			state = default;
			if (!WindowSystem.IsInitialized || !Glfw.GetGamepadState(index, out GlfwGamepadState native))
			{
				return false;
			}

			for (int i = 0; i < 15; i++)
			{
				state.Buttons[i] = native.Buttons[i];
			}

			for (int i = 0; i < 6; i++)
			{
				state.Axes[i] = native.Axes[i];
			}

			return true;
		}
	}
}
