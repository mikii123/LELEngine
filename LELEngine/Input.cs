using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine
{
	public sealed class Input
	{
		#region PublicFields

		public static List<KeyController> Pressed = new List<KeyController>();
		public static List<KeyController> UnPressed = new List<KeyController>();

		public static Vector2 mousePosition { get; private set; }
		public static Vector2 relativeMousePosition { get; private set; }

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
			// State captured via events; nothing to poll here
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
			return Game.Mono.IsMouseButtonDown(button);
		}

		public static bool GetKey(Keys code)
		{
			return Game.Mono.IsKeyDown(code);
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
			relativeMousePosition = new Vector2(e.X / (float)Game.Mono.ClientSize.X, e.Y / (float)Game.Mono.ClientSize.Y);
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
