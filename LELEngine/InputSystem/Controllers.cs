using System;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.InputSystem
{
	/// <summary>
	///     An input device as the input system reads it: one value per element each frame (axes -1..1, the mouse in pixels
	///     and wheel notches; buttons 0 or 1), and the frame before. Polled by <see cref="InputManager" /> once per frame.
	/// </summary>
	public abstract class Controller
	{
		#region PublicFields

		public ControllerType Type { get; }

		/// <summary>Unique per type (the keyboard and the mouse are 0; gamepads count up as they connect).</summary>
		public int Id { get; }

		public string Name { get; protected set; }

		public bool IsConnected { get; internal set; } = true;

		/// <summary>The player it is assigned to (gamepads belong to one player; the keyboard and mouse may be shared), or null.</summary>
		public InputPlayer Owner { get; internal set; }

		/// <summary>Game time of the last frame any element was active (a pressed button, a moved axis).</summary>
		public double LastActiveTime { get; private set; } = double.NegativeInfinity;

		public int ElementCount => current.Length;

		#endregion

		#region PrivateFields

		private float[] current;
		private float[] previous;

		#endregion

		#region Constructors

		protected Controller(ControllerType type, int id, string name, int elementCount)
		{
			Type = type;
			Id = id;
			Name = name;
			current = new float[elementCount];
			previous = new float[elementCount];
		}

		#endregion

		#region PublicMethods

		/// <summary>The element's value this frame (0 for elements the controller does not have).</summary>
		public float GetValue(int element)
		{
			return element >= 0 && element < current.Length ? current[element] : 0f;
		}

		public float GetValuePrevious(int element)
		{
			return element >= 0 && element < previous.Length ? previous[element] : 0f;
		}

		public bool GetButton(int element)
		{
			return GetValue(element) > 0.5f;
		}

		public bool GetButtonDown(int element)
		{
			return GetValue(element) > 0.5f && GetValuePrevious(element) <= 0.5f;
		}

		public bool GetButtonUp(int element)
		{
			return GetValue(element) <= 0.5f && GetValuePrevious(element) > 0.5f;
		}

		public string ElementName(int element)
		{
			return ControllerTypes.ElementName(Type, element);
		}

		public override string ToString()
		{
			return Name;
		}

		#endregion

		#region InternalMethods

		/// <summary>Moves this frame's values to the previous frame and reads the device.</summary>
		internal void Poll(double time)
		{
			(previous, current) = (current, previous);
			Array.Clear(current);
			if (IsConnected)
			{
				Read(current);
			}

			for (int i = 0; i < current.Length; i++)
			{
				// Mouse movement counts only when it is more than jitter.
				float threshold = Type == ControllerType.Mouse && i <= (int)MouseElement.AxisY ? 2f : 0.25f;
				if (Math.Abs(current[i]) > threshold)
				{
					LastActiveTime = time;
					break;
				}
			}
		}

		/// <summary>Writes this frame's element values.</summary>
		protected abstract void Read(float[] values);

		#endregion
	}

	/// <summary>The keyboard (elements are <see cref="Keys" /> codes).</summary>
	public sealed class KeyboardController : Controller
	{
		internal KeyboardController() : base(ControllerType.Keyboard, 0, "Keyboard", (int)Keys.LastKey + 1)
		{
		}

		/// <summary>The modifiers held now (bindings that need modifiers check them).</summary>
		public ModifierKeys Modifiers
		{
			get
			{
				ModifierKeys modifiers = ModifierKeys.None;
				if (GetButton((int)Keys.LeftShift) || GetButton((int)Keys.RightShift)) modifiers |= ModifierKeys.Shift;
				if (GetButton((int)Keys.LeftControl) || GetButton((int)Keys.RightControl)) modifiers |= ModifierKeys.Control;
				if (GetButton((int)Keys.LeftAlt) || GetButton((int)Keys.RightAlt)) modifiers |= ModifierKeys.Alt;
				return modifiers;
			}
		}

		protected override void Read(float[] values)
		{
			if (Input.Host == null)
			{
				return;
			}

			for (int key = (int)Keys.Space; key < values.Length; key++)
			{
				// A tap shorter than a frame still counts (the key event of this frame).
				if (Input.Host.IsKeyDown((Keys)key) || Input.GetKeyDown((Keys)key))
				{
					values[key] = 1f;
				}
			}
		}
	}

	/// <summary>The mouse: movement and wheel as axes, buttons.</summary>
	public sealed class MouseController : Controller
	{
		internal MouseController() : base(ControllerType.Mouse, 0, "Mouse", Enum.GetValues<MouseElement>().Length)
		{
		}

		protected override void Read(float[] values)
		{
			values[(int)MouseElement.AxisX] = Input.mouseDelta.X;
			values[(int)MouseElement.AxisY] = -Input.mouseDelta.Y;
			values[(int)MouseElement.Wheel] = Input.mouseScrollDelta.Y;
			values[(int)MouseElement.WheelHorizontal] = Input.mouseScrollDelta.X;
			for (int button = 0; button <= (int)MouseElement.Button8 - (int)MouseElement.LeftButton; button++)
			{
				var mouseButton = (OpenTK.Windowing.GraphicsLibraryFramework.MouseButton)button;
				if (Input.GetMouseButton(mouseButton) || Input.GetMouseButtonDown(mouseButton))
				{
					values[(int)MouseElement.LeftButton + button] = 1f;
				}
			}
		}
	}

	/// <summary>
	///     A gamepad read through the standard layout (<see cref="GamepadElement" />): a connected device (GLFW, which maps
	///     most controllers with SDL's database), or a virtual one whose elements code sets (on-screen controls, tests,
	///     network players) with <see cref="SetButton" /> / <see cref="SetAxis" />. Stick and trigger dead zones come
	///     from <see cref="InputManagerSettings" />.
	/// </summary>
	public sealed class GamepadController : Controller
	{
		#region PublicFields

		/// <summary>GLFW's joystick slot, or -1 for a virtual gamepad.</summary>
		public int JoystickSlot { get; }

		public bool IsVirtual => JoystickSlot < 0;

		/// <summary>The device's GUID (a reconnected gamepad goes back to its player); the name for virtual ones.</summary>
		public string HardwareId { get; }

		#endregion

		#region PrivateFields

		private readonly float[] virtualValues = new float[Enum.GetValues<GamepadElement>().Length];

		#endregion

		#region Constructors

		internal GamepadController(int id, int joystickSlot, string name, string hardwareId) : base(ControllerType.Gamepad, id, name, Enum.GetValues<GamepadElement>().Length)
		{
			JoystickSlot = joystickSlot;
			HardwareId = hardwareId;
		}

		#endregion

		#region PublicMethods

		/// <summary>Virtual gamepads: presses or releases a button (or pushes an axis fully) from the next frame on.</summary>
		public void SetButton(GamepadElement element, bool pressed)
		{
			virtualValues[(int)element] = pressed ? 1f : 0f;
		}

		/// <summary>Virtual gamepads: an axis value (-1..1; triggers 0..1) from the next frame on.</summary>
		public void SetAxis(GamepadElement element, float value)
		{
			virtualValues[(int)element] = Math.Clamp(value, -1f, 1f);
		}

		#endregion

		#region ProtectedMethods

		protected override unsafe void Read(float[] values)
		{
			InputManagerSettings settings = InputManager.Configuration?.Settings ?? new InputManagerSettings();
			if (IsVirtual)
			{
				Array.Copy(virtualValues, values, values.Length);
				return;
			}

			if (!GLFW.GetGamepadState(JoystickSlot, out GamepadState state))
			{
				return;
			}

			// GLFW: stick y down is positive, triggers -1 released to 1 pressed; here up is positive, triggers 0..1.
			Stick(state.Axes[0], -state.Axes[1], settings.StickDeadZone, out values[(int)GamepadElement.LeftStickX], out values[(int)GamepadElement.LeftStickY]);
			Stick(state.Axes[2], -state.Axes[3], settings.StickDeadZone, out values[(int)GamepadElement.RightStickX], out values[(int)GamepadElement.RightStickY]);
			values[(int)GamepadElement.LeftTrigger] = Trigger(state.Axes[4], settings.TriggerDeadZone);
			values[(int)GamepadElement.RightTrigger] = Trigger(state.Axes[5], settings.TriggerDeadZone);
			for (int button = 0; button < 15; button++)
			{
				values[(int)GamepadElement.ActionBottom + button] = state.Buttons[button] != 0 ? 1f : 0f;
			}
		}

		#endregion

		#region PrivateMethods

		// A radial dead zone: the stick's length under it reads 0, above it is rescaled to 0..1 (the direction kept).
		private static void Stick(float x, float y, float deadZone, out float outX, out float outY)
		{
			float length = MathF.Sqrt(x * x + y * y);
			if (length <= deadZone || length < 1e-6f)
			{
				outX = 0f;
				outY = 0f;
				return;
			}

			float scaled = Math.Min(1f, (length - deadZone) / (1f - deadZone));
			outX = x / length * scaled;
			outY = y / length * scaled;
		}

		private static float Trigger(float value, float deadZone)
		{
			float pressed = (value + 1f) * 0.5f;
			return pressed <= deadZone ? 0f : Math.Min(1f, (pressed - deadZone) / (1f - deadZone));
		}

		#endregion
	}
}
