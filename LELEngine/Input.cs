using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

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
	}

	public sealed class Input
	{
		#region PublicFields

		/// <summary>Source of keyboard / mouse state; set by the host that owns the window.</summary>
		public static IInputHost Host;

		public static List<KeyController> Pressed = new List<KeyController>();
		public static List<KeyController> UnPressed = new List<KeyController>();

		public static Vector2 mousePosition { get; private set; }
		public static Vector2 relativeMousePosition { get; private set; }

		/// <summary>
		///     Mouse movement since the previous update frame, in pixels. Valid also while the cursor is locked.
		/// </summary>
		public static Vector2 mouseDelta { get; private set; }

		public static bool cursorLocked { get; private set; }

		private static Vector2 lastMousePosition;

		public enum StandardInputAxis
		{
			MouseX,
			MouseY
		}

		#endregion

		#region PublicMethods

		public static void EndFrame()
		{
			foreach (KeyController pr in Pressed)
			{
				pr.frame++;
			}
			foreach (KeyController upr in UnPressed)
			{
				upr.frame++;
			}

			lastMousePosition = mousePosition;
			mouseScrollDelta = Vector2.Zero;
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

		public static bool GetKey(Keys code)
		{
			return Host != null && Host.IsKeyDown(code);
		}

		public static bool GetKeyUp(Keys code)
		{
			if (UnPressed.Exists(item => item.key == code))
			{
				KeyController kc = UnPressed.Find(item => item.key == code);
				return kc.frame == 0;
			}
			return false;
		}

		public static bool GetKeyDown(Keys code)
		{
			if (Pressed.Exists(item => item.key == code))
			{
				KeyController kc = Pressed.Find(item => item.key == code);
				return kc.frame == 0;
			}
			return false;
		}

		public static void Input_KeyUp(KeyboardKeyEventArgs e)
		{
			if (!UnPressed.Exists(item => item.key == e.Key))
			{
				UnPressed.Add(new KeyController(e.Key));
			}
			Pressed.RemoveAll(item => item.key == e.Key);
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.KeyUp, key = e.Key, modifiers = e.Modifiers, position = mousePosition });
		}

		public static void Input_KeyDown(KeyboardKeyEventArgs e)
		{
			if (!Pressed.Exists(item => item.key == e.Key))
			{
				Pressed.Add(new KeyController(e.Key));
			}
			UnPressed.RemoveAll(item => item.key == e.Key);
			// Key repeats reach the UI (text fields), the game sees one press.
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.KeyDown, key = e.Key, modifiers = e.Modifiers, position = mousePosition });
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
			UIEvents.Add(new RawInputEvent { kind = RawInputKind.MouseDown, button = e.Button, modifiers = e.Modifiers, position = mousePosition });
		}

		public static void Input_MouseUp(MouseButtonEventArgs e)
		{
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
		}

		#endregion

		#region NestedTypes

		public class KeyController
		{
			#region PublicFields

			public Keys key;
			public int frame;

			#endregion

			#region Constructors

			public KeyController(Keys k)
			{
				key = k;
				frame = 0;
			}

			#endregion
		}

		#endregion
	}
}
