using System;
using System.Collections.Generic;
using LELEngine.Mathematics;
using LELEngine.Windowing;

namespace LELEngine
{
	/// <summary>Window-side state the input system reads (the game window, or the editor's game view).</summary>
	public interface IInputHost
	{
		MouseState MouseState { get; }
		CursorState CursorState { get; set; }
		Vector2i ClientSize { get; }
		bool IsKeyDown(Keys key);
		bool IsMouseButtonDown(MouseButton button);

		/// <summary>Moves the system's cursor to a pixel of the game's image (<see cref="Input.mousePosition" />'s coordinates).</summary>
		void WarpCursor(Vector2 position);
	}

	public sealed class Input
	{
		#region PublicFields

		/// <summary>Source of keyboard / mouse state; set by the host that owns the window.</summary>
		public static IInputHost Host;

		// Keys pressed / released since the last frame ended, from the window's events (Unity's GetKeyDown / GetKeyUp: the
		// frame of the press, the frame of the release; a tap shorter than a frame is both). The system's repeats of a
		// held key are not presses. Read by the editor's play session recorder (internal: it allocates nothing).
		internal static readonly HashSet<Keys> keysPressed = new HashSet<Keys>();
		internal static readonly HashSet<Keys> keysReleased = new HashSet<Keys>();

		public static Vector2 mousePosition { get; private set; }
		public static Vector2 relativeMousePosition { get; private set; }

		/// <summary>
		///     Mouse movement since the previous update frame, in pixels. Valid also while the cursor is locked.
		/// </summary>
		public static Vector2 mouseDelta { get; private set; }

		public static bool cursorLocked { get; private set; }

		private static Vector2 lastMousePosition;

		// Mouse buttons pressed / released this frame (bits by MouseButton), from the window's events.
		private static int mouseButtonsPressed;
		private static int mouseButtonsReleased;

		public enum StandardInputAxis
		{
			MouseX,
			MouseY
		}

		#endregion

		#region PublicMethods

		public static void EndFrame()
		{
			keysPressed.Clear();
			keysReleased.Clear();
			lastMousePosition = mousePosition;
			mouseScrollDelta = Vector2.Zero;
			mouseButtonsPressed = 0;
			mouseButtonsReleased = 0;
			// The UI drains the raw events on its next update (events raised during a frame reach it then); a host
			// without any scene update would let them pile up, so they are capped.
			if (UIEvents.Count > 4096)
			{
				UIEvents.Clear();
			}
		}

		public static void BeginFrame()
		{
			// Key state is captured via events; the mouse delta is polled so it works with a grabbed cursor.
			mouseDelta = Host != null ? Host.MouseState.Delta : Vector2.Zero;
			// The input actions read this frame's devices (InputSystem.InputManager).
			InputSystem.InputManager.Update();
		}

		/// <summary>
		///     Hides the cursor and confines it to the window (FPS style look).
		/// </summary>
		public static void SetCursorLocked(bool locked)
		{
			cursorLocked = locked;
			if (Host != null)
			{
				Host.CursorState = locked ? CursorState.Grabbed : CursorState.Normal;
			}
		}

		/// <summary>
		///     Puts the cursor on a pixel of the game's image (<see cref="mousePosition" />'s coordinates: from the image's
		///     top left): the system's cursor moves there (in the editor only while the Game view has the input) and
		///     <see cref="mousePosition" /> is that pixel from now on, with no movement counted (Unity's
		///     Mouse.WarpCursorPosition). For a game whose aim follows the cursor: give the cursor back where the aim is.
		/// </summary>
		public static void WarpCursor(Vector2 position)
		{
			Vector2i size = Host != null ? Host.ClientSize : new Vector2i(1, 1);
			mousePosition = position;
			lastMousePosition = position;
			relativeMousePosition = new Vector2(position.X / MathF.Max(1f, size.X), position.Y / MathF.Max(1f, size.Y));
			Host?.WarpCursor(position);
		}

		public static float GetStandardAxis(StandardInputAxis axis, float sensitivity = 0.1f)
		{
			switch (axis)
			{
				case StandardInputAxis.MouseX:
					return (lastMousePosition.X - mousePosition.X) * sensitivity;
				case StandardInputAxis.MouseY:
					return (lastMousePosition.Y - mousePosition.Y) * sensitivity;
				default:
					return 0f;
			}
		}

		public static bool GetMouseButton(MouseButton button)
		{
			return Host != null && Host.IsMouseButtonDown(button);
		}

		/// <summary>The button was pressed this frame (also a click shorter than a frame).</summary>
		public static bool GetMouseButtonDown(MouseButton button)
		{
			return (mouseButtonsPressed & (1 << (int)button)) != 0;
		}

		/// <summary>The button was released this frame.</summary>
		public static bool GetMouseButtonUp(MouseButton button)
		{
			return (mouseButtonsReleased & (1 << (int)button)) != 0;
		}

		/// <summary>The key is held (every frame it is down; also the frame of a tap shorter than a frame).</summary>
		public static bool GetKey(Keys code)
		{
			return (Host != null && Host.IsKeyDown(code)) || keysPressed.Contains(code);
		}

		/// <summary>The key was released this frame (only that frame).</summary>
		public static bool GetKeyUp(Keys code)
		{
			return keysReleased.Contains(code);
		}

		/// <summary>The key was pressed this frame (only that frame; holding it does not repeat it).</summary>
		public static bool GetKeyDown(Keys code)
		{
			return keysPressed.Contains(code);
		}

		public static void Input_KeyUp(KeyboardKeyEventArgs e)
		{
			keysReleased.Add(e.Key);
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.KeyUp, key = e.Key, modifiers = e.Modifiers, position = mousePosition });
		}

		public static void Input_KeyDown(KeyboardKeyEventArgs e)
		{
			if (!e.IsRepeat)
			{
				keysPressed.Add(e.Key);
			}

			// The game sees one press; the UI gets the system's repeats marked as such (only text editing takes them).
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.KeyDown, key = e.Key, modifiers = e.Modifiers, position = mousePosition, repeat = e.IsRepeat });
		}

		public static void Input_MouseMove(MouseMoveEventArgs e)
		{
			Vector2i size = Host != null ? Host.ClientSize : new Vector2i(1, 1);
			relativeMousePosition = new Vector2(e.X / (float)size.X, e.Y / (float)size.Y);
			mousePosition = new Vector2(e.X, e.Y);
		}

		// Raw events for the UI system (consumed once per frame by UIElements.UIRuntime.ProcessInput).

		public static void Input_MouseDown(MouseButtonEventArgs e)
		{
			mouseButtonsPressed |= 1 << (int)e.Button;
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.MouseDown, button = e.Button, modifiers = e.Modifiers, position = mousePosition });
		}

		public static void Input_MouseUp(MouseButtonEventArgs e)
		{
			mouseButtonsReleased |= 1 << (int)e.Button;
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.MouseUp, button = e.Button, modifiers = e.Modifiers, position = mousePosition });
		}

		public static void Input_MouseWheel(MouseWheelEventArgs e)
		{
			mouseScrollDelta = new Vector2(e.OffsetX, e.OffsetY);
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.MouseWheel, delta = new Vector2(e.OffsetX, e.OffsetY), position = mousePosition });
		}

		public static void Input_TextInput(TextInputEventArgs e)
		{
			if (e.Unicode >= 0x20 && e.Unicode != 0x7F)
			{
				UIEvents.Add(new RawInputEvent { kind = RawInputKind.TextInput, character = e.Unicode <= 0xFFFF ? (char)e.Unicode : '?', position = mousePosition });
			}
		}

		/// <summary>Scroll wheel movement of the last frame (positive y = up).</summary>
		public static Vector2 mouseScrollDelta { get; private set; }

		/// <summary>Raw window events of this frame, in order (the UI drains the list).</summary>
		public static readonly List<RawInputEvent> UIEvents = new List<RawInputEvent>();

		public enum RawInputKind
		{
			MouseDown,
			MouseUp,
			MouseWheel,
			KeyDown,
			KeyUp,
			TextInput
		}

		public struct RawInputEvent
		{
			public RawInputKind kind;
			public MouseButton button;
			public Keys key;
			public KeyModifiers modifiers;
			public Vector2 position;
			public Vector2 delta;
			public char character;

			/// <summary>A key down the system repeats while the key is held (not a press of its own).</summary>
			public bool repeat;
		}

		#endregion
	}
}
