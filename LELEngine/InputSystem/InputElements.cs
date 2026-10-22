using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.InputSystem
{
	public enum ControllerType
	{
		Keyboard,
		Mouse,

		/// <summary>
		///     The standard gamepad (Xbox layout, the template every gamepad is read through): GLFW maps hundreds of
		///     controllers onto it (SDL's controller database); also virtual gamepads driven by code.
		/// </summary>
		Gamepad
	}

	/// <summary>The mouse's elements.</summary>
	public enum MouseElement
	{
		/// <summary>Horizontal movement (pixels per frame; right is positive).</summary>
		AxisX,

		/// <summary>Vertical movement (up is positive).</summary>
		AxisY,

		/// <summary>The wheel (notches; up is positive).</summary>
		Wheel,

		WheelHorizontal,
		LeftButton,
		RightButton,
		MiddleButton,
		Button4,
		Button5,
		Button6,
		Button7,
		Button8
	}

	/// <summary>The standard gamepad's elements (Xbox names; the face buttons by position).</summary>
	public enum GamepadElement
	{
		LeftStickX,
		LeftStickY,
		RightStickX,
		RightStickY,
		LeftTrigger,
		RightTrigger,

		/// <summary>A / Cross.</summary>
		ActionBottom,

		/// <summary>B / Circle.</summary>
		ActionRight,

		/// <summary>X / Square.</summary>
		ActionLeft,

		/// <summary>Y / Triangle.</summary>
		ActionTop,

		LeftShoulder,
		RightShoulder,
		Back,
		Start,
		Guide,
		LeftStickButton,
		RightStickButton,
		DPadUp,
		DPadRight,
		DPadDown,
		DPadLeft
	}

	/// <summary>The elements of each controller type: which are axes, their names, the lists an editor offers.</summary>
	public static class ControllerTypes
	{
		#region PublicFields

		public static readonly ControllerType[] All = { ControllerType.Keyboard, ControllerType.Mouse, ControllerType.Gamepad };

		#endregion

		#region PrivateFields

		private static List<int> keyboardElements;

		#endregion

		#region PublicMethods

		/// <summary>The element is an axis (mouse movement and wheel, sticks and triggers); else a button or key.</summary>
		public static bool IsAxis(ControllerType type, int element)
		{
			switch (type)
			{
				case ControllerType.Mouse:
					return element <= (int)MouseElement.WheelHorizontal;
				case ControllerType.Gamepad:
					return element <= (int)GamepadElement.RightTrigger;
				default:
					return false;
			}
		}

		/// <summary>The name shown for an element ("Space", "Left Button", "Right Stick X"), with its binding's modifiers and half.</summary>
		public static string ElementName(ControllerType type, int element, ModifierKeys modifiers = ModifierKeys.None, AxisRange range = AxisRange.Full)
		{
			string name;
			switch (type)
			{
				case ControllerType.Keyboard:
					name = KeyName((Keys)element);
					break;
				case ControllerType.Mouse:
					name = Enum.IsDefined(typeof(MouseElement), element) ? Nicify(((MouseElement)element).ToString()) : "Mouse " + element;
					break;
				default:
					name = Enum.IsDefined(typeof(GamepadElement), element) ? Nicify(((GamepadElement)element).ToString()) : "Element " + element;
					break;
			}

			if (modifiers != ModifierKeys.None)
			{
				string prefix = "";
				if ((modifiers & ModifierKeys.Control) != 0) prefix += "Ctrl + ";
				if ((modifiers & ModifierKeys.Shift) != 0) prefix += "Shift + ";
				if ((modifiers & ModifierKeys.Alt) != 0) prefix += "Alt + ";
				name = prefix + name;
			}

			if (IsAxis(type, element) && range != AxisRange.Full)
			{
				name += range == AxisRange.Positive ? " +" : " -";
			}

			return name;
		}

		/// <summary>The elements of a controller type in the order an editor lists them.</summary>
		public static IReadOnlyList<int> Elements(ControllerType type)
		{
			switch (type)
			{
				case ControllerType.Keyboard:
					if (keyboardElements == null)
					{
						var keys = new List<int>();
						foreach (Keys key in Enum.GetValues<Keys>())
						{
							if (key != Keys.Unknown && !keys.Contains((int)key))
							{
								keys.Add((int)key);
							}
						}

						keyboardElements = keys;
					}

					return keyboardElements;
				case ControllerType.Mouse:
					return Range(Enum.GetValues<MouseElement>().Length);
				default:
					return Range(Enum.GetValues<GamepadElement>().Length);
			}
		}

		public static string KeyName(Keys key)
		{
			switch (key)
			{
				case Keys.D0: case Keys.D1: case Keys.D2: case Keys.D3: case Keys.D4:
				case Keys.D5: case Keys.D6: case Keys.D7: case Keys.D8: case Keys.D9:
					return ((int)key - (int)Keys.D0).ToString();
				case Keys.KeyPad0: case Keys.KeyPad1: case Keys.KeyPad2: case Keys.KeyPad3: case Keys.KeyPad4:
				case Keys.KeyPad5: case Keys.KeyPad6: case Keys.KeyPad7: case Keys.KeyPad8: case Keys.KeyPad9:
					return "Num " + ((int)key - (int)Keys.KeyPad0);
				case Keys.Up: return "Up Arrow";
				case Keys.Down: return "Down Arrow";
				case Keys.Left: return "Left Arrow";
				case Keys.Right: return "Right Arrow";
				case Keys.GraveAccent: return "`";
				case Keys.Minus: return "-";
				case Keys.Equal: return "=";
				case Keys.LeftBracket: return "[";
				case Keys.RightBracket: return "]";
				case Keys.Backslash: return "\\";
				case Keys.Semicolon: return ";";
				case Keys.Apostrophe: return "'";
				case Keys.Comma: return ",";
				case Keys.Period: return ".";
				case Keys.Slash: return "/";
				default: return Nicify(key.ToString());
			}
		}

		#endregion

		#region PrivateMethods

		private static List<int> Range(int count)
		{
			var list = new List<int>(count);
			for (int i = 0; i < count; i++)
			{
				list.Add(i);
			}

			return list;
		}

		/// <summary>"LeftStickX" → "Left Stick X".</summary>
		private static string Nicify(string name)
		{
			var builder = new System.Text.StringBuilder(name.Length + 4);
			for (int i = 0; i < name.Length; i++)
			{
				char c = name[i];
				if (i > 0 && (char.IsUpper(c) || (char.IsDigit(c) && !char.IsDigit(name[i - 1]))) && name[i - 1] != ' ')
				{
					builder.Append(' ');
				}

				builder.Append(c);
			}

			return builder.ToString();
		}

		#endregion
	}
}
