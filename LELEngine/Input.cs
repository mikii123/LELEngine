using System.Collections.Generic;
using OpenTK;
using OpenTK.Input;

namespace LELEngine
{
	public sealed class Input
	{
		#region PublicFields

		public static List<KeyController> Pressed = new List<KeyController>();
		public static List<KeyController> UnPressed = new List<KeyController>();

		public static Vector2 mousePosition { get; private set; }

		public static Vector2 relativeMousePosition { get; private set; }

		public enum StandardInputAxis
		{
			MouseX,
			MouseY
		}

		#endregion

		#region PrivateFields

		private static MouseState mouseState = Mouse.GetState();
		private static KeyboardState keyboardState = Keyboard.GetState();
		private static MouseState lastMouseState = Mouse.GetState();
		private static KeyboardState lastKeyboardState = Keyboard.GetState();

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

			lastMouseState = Mouse.GetState();
			lastKeyboardState = Keyboard.GetState();
		}

		public static void BeginFrame()
		{
			mouseState = Mouse.GetState();
			keyboardState = Keyboard.GetState();
		}

		public static float GetStandardAxis(StandardInputAxis axis, float sensitivity = 0.1f)
		{
			float ret = 0;

			switch (axis)
			{
				case StandardInputAxis.MouseX:
					ret = (lastMouseState.X - mouseState.X) * sensitivity;
					break;
				case StandardInputAxis.MouseY:
					ret = (lastMouseState.Y - mouseState.Y) * sensitivity;
					break;
			}

			return ret;
		}

		public static bool GetMouseButton(MouseButton button)
		{
			return mouseState.IsButtonDown(button);
		}

		public static bool GetKey(Key code)
		{
			return keyboardState.IsKeyDown(code);
		}

		public static bool GetKeyUp(Key code)
		{
			if (UnPressed.Exists(item => item.key == code))
			{
				KeyController kc = UnPressed.Find(item => item.key == code);
				if (kc.frame == 0)
				{
					return true;
				}

				return false;
			}

			return false;
		}

		public static bool GetKeyDown(Key code)
		{
			if (Pressed.Exists(item => item.key == code))
			{
				KeyController kc = Pressed.Find(item => item.key == code);
				if (kc.frame == 0)
				{
					return true;
				}

				return false;
			}

			return false;
		}

		public static void Input_KeyUp(object sender, KeyboardKeyEventArgs e)
		{
			if (!UnPressed.Exists(item => item.key == e.Key))
			{
				UnPressed.Add(new KeyController(e.Key));
			}
			Pressed.RemoveAll(item => item.key == e.Key);
		}

		public static void Input_KeyDown(object sender, KeyboardKeyEventArgs e)
		{
			if (!Pressed.Exists(item => item.key == e.Key))
			{
				Pressed.Add(new KeyController(e.Key));
			}
			UnPressed.RemoveAll(item => item.key == e.Key);
		}

		public static void Input_MouseMove(object sender, MouseMoveEventArgs e)
		{
			relativeMousePosition = new Vector2(e.X / (float)Game.Mono.Width, e.Y / (float)Game.Mono.Height);
			mousePosition = new Vector2(e.X, e.Y);
		}

		#endregion

		#region NestedTypes

		public class KeyController
		{
			#region PublicFields

			public Key key;
			public int frame;

			#endregion

			#region Constructors

			public KeyController(Key k)
			{
				key = k;
				frame = 0;
			}

			#endregion
		}

		#endregion
	}
}
