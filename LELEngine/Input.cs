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
		}

		public static void Input_KeyDown(KeyboardKeyEventArgs e)
		{
			if (!Pressed.Exists(item => item.key == e.Key))
			{
				Pressed.Add(new KeyController(e.Key));
			}
			UnPressed.RemoveAll(item => item.key == e.Key);
		}

		public static void Input_MouseMove(MouseMoveEventArgs e)
		{
			Vector2i size = Host != null ? Host.ClientSize : new Vector2i(1, 1);
			relativeMousePosition = new Vector2(e.X / (float)size.X, e.Y / (float)size.Y);
			mousePosition = new Vector2(e.X, e.Y);
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
