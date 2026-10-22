using System;
using System.Collections.Generic;

namespace LELEngine.InputSystem
{
	/// <summary>
	///     The project's input setup, edited in Window > Input Manager (the project uses the one named in Project Settings >
	///     Input). Modeled on Rewired: the game reads <see cref="InputAction" />s of a <see cref="InputPlayer" />, never
	///     keys; controller maps bind the elements of a controller type (keyboard keys, mouse buttons and axes, the
	///     standard gamepad's buttons and sticks) to actions, grouped by map category (what the game is doing: flying, a
	///     menu) and layout (alternative bindings: left-handed); input behaviors shape the values (dead zones, digital axis
	///     gravity, double / short / long press times); players start with maps and controllers assigned.
	/// </summary>
	public sealed class InputConfiguration : ScriptableObject
	{
		#region PublicFields

		public InputManagerSettings Settings = new InputManagerSettings();

		/// <summary>Groups of actions (organization only, and what a remapping screen lists together).</summary>
		public List<InputCategory> ActionCategories = new List<InputCategory>();

		public List<InputAction> Actions = new List<InputAction>();

		/// <summary>What the game is doing: a map belongs to one (Default, Gameplay, UI); players enable maps by category.</summary>
		public List<InputCategory> MapCategories = new List<InputCategory>();

		/// <summary>Alternative bindings of one category (Default, Left Handed): a player has one layout of a category loaded.</summary>
		public List<InputCategory> Layouts = new List<InputCategory>();

		public List<InputBehavior> Behaviors = new List<InputBehavior>();

		public List<InputPlayerDefinition> Players = new List<InputPlayerDefinition>();

		/// <summary>Every controller map: per controller type, map category and layout.</summary>
		public List<ControllerMap> Maps = new List<ControllerMap>();

		#endregion

		#region PublicMethods

		/// <summary>A fresh setup: the Default categories, layout and behavior, one player with the default maps.</summary>
		public override void Reset()
		{
			Settings = new InputManagerSettings();
			ActionCategories = new List<InputCategory> { new InputCategory { Id = 0, Name = "Default" } };
			Actions = new List<InputAction>();
			MapCategories = new List<InputCategory> { new InputCategory { Id = 0, Name = "Default" } };
			Layouts = new List<InputCategory> { new InputCategory { Id = 0, Name = "Default" } };
			Behaviors = new List<InputBehavior> { new InputBehavior { Id = 0, Name = "Default" } };
			Players = new List<InputPlayerDefinition> { InputPlayerDefinition.CreateDefault(0, "Player0") };
			Maps = new List<ControllerMap>();
			foreach (ControllerType type in ControllerTypes.All)
			{
				Maps.Add(new ControllerMap { Id = NextId(Maps, m => m.Id), Type = type, CategoryId = 0, LayoutId = 0 });
			}
		}

		public InputAction FindAction(int id)
		{
			return Actions.Find(a => a.Id == id);
		}

		public InputAction FindAction(string name)
		{
			return Actions.Find(a => string.Equals(a.Name, name, StringComparison.Ordinal)) ?? Actions.Find(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
		}

		public InputBehavior FindBehavior(int id)
		{
			return Behaviors.Find(b => b.Id == id) ?? (Behaviors.Count > 0 ? Behaviors[0] : null);
		}

		public InputCategory FindMapCategory(string name)
		{
			return MapCategories.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
		}

		public InputCategory FindLayout(string name)
		{
			return Layouts.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
		}

		public ControllerMap FindMap(ControllerType type, int categoryId, int layoutId)
		{
			return Maps.Find(m => m.Type == type && m.CategoryId == categoryId && m.LayoutId == layoutId);
		}

		/// <summary>One more than the largest id of the list (ids are never reused while the list holds them).</summary>
		public static int NextId<T>(List<T> list, Func<T, int> id)
		{
			int next = 0;
			foreach (T item in list)
			{
				next = Math.Max(next, id(item) + 1);
			}

			return next;
		}

		#endregion
	}

	/// <summary>How the input manager assigns controllers and stores players' remapped bindings.</summary>
	[Serializable]
	public sealed class InputManagerSettings
	{
		/// <summary>A gamepad that connects goes to the first player that wants gamepads and has room for one.</summary>
		[Tooltip("A gamepad that connects goes to the first player that wants gamepads and has room for one")]
		public bool AutoAssignGamepads = true;

		[Range(0, 8)] public int MaxGamepadsPerPlayer = 1;

		/// <summary>Spread gamepads over the players (one each first) instead of filling the first player.</summary>
		public bool DistributeGamepadsEvenly = true;

		/// <summary>A gamepad that reconnects goes back to the player it was with.</summary>
		public bool ReassignToPreviousOwner = true;

		/// <summary>Radial dead zone of the sticks (a length under it reads 0, the rest is rescaled to 0..1).</summary>
		[Range(0f, 0.9f)] public float StickDeadZone = 0.2f;

		/// <summary>Dead zone of the triggers.</summary>
		[Range(0f, 0.9f)] public float TriggerDeadZone = 0.1f;

		/// <summary>Players' remapped controller maps are saved to and loaded from the user's data folder.</summary>
		[Tooltip("Players' remapped maps are saved to (InputManager.SaveUserData) and loaded from the user's data folder")]
		public bool LoadUserData = true;
	}

	/// <summary>A named group (action category, map category, layout).</summary>
	[Serializable]
	public sealed class InputCategory
	{
		public int Id;
		public string Name = "";
		public string DescriptiveName = "";

		/// <summary>Players may remap the bindings of this group (a remapping screen lists it).</summary>
		public bool UserAssignable = true;

		public override string ToString() => Name;
	}

	public enum InputActionType
	{
		/// <summary>A value from -1 to 1 (sticks, two keys, the mouse); also readable as a button (pressed past the dead zone).</summary>
		Axis,

		/// <summary>Pressed or not (keys, buttons; an axis pushed past the button dead zone).</summary>
		Button
	}

	/// <summary>Something the player does (Move Horizontal, Fire, Jump): what the game reads instead of keys.</summary>
	[Serializable]
	public sealed class InputAction
	{
		public int Id;
		public string Name = "";

		/// <summary>The name shown to players (a remapping screen); Name when empty.</summary>
		public string DescriptiveName = "";

		/// <summary>Names of the two directions of an axis (Right / Left) for remapping screens.</summary>
		public string PositiveName = "";

		public string NegativeName = "";
		public InputActionType Type = InputActionType.Button;
		public int CategoryId;
		public int BehaviorId;

		/// <summary>Players may remap it.</summary>
		public bool UserAssignable = true;

		public string DisplayName => string.IsNullOrEmpty(DescriptiveName) ? Name : DescriptiveName;

		public override string ToString() => Name;
	}

	public enum MouseAxisMode
	{
		/// <summary>The mouse movement in pixels times the sensitivity (can exceed 1: a fast flick turns more).</summary>
		Analog,

		/// <summary>The movement clamped to -1..1 (a stick-like value).</summary>
		Clamped
	}

	/// <summary>How the values of the actions that use it are shaped (Rewired's Input Behaviors).</summary>
	[Serializable]
	public sealed class InputBehavior
	{
		public int Id;
		public string Name = "";

		/// <summary>Gamepad axes are multiplied by this.</summary>
		[Header("Axes")]
		public float GamepadAxisSensitivity = 1f;

		/// <summary>Keys and buttons bound to an axis move it gradually (Gravity / Sensitivity) instead of jumping to ±1.</summary>
		[Tooltip("Keys and buttons bound to an axis move it gradually (gravity / sensitivity) instead of jumping to ±1")]
		public bool DigitalAxisSimulation = true;

		/// <summary>Units per second the simulated axis moves towards the pressed direction.</summary>
		public float DigitalAxisSensitivity = 3f;

		/// <summary>Units per second the simulated axis returns to 0 when released.</summary>
		public float DigitalAxisGravity = 3f;

		/// <summary>Pressing the opposite direction starts from 0 instead of travelling through it.</summary>
		public bool DigitalAxisSnap = true;

		public MouseAxisMode MouseAxisMode = MouseAxisMode.Analog;

		/// <summary>Pixels of mouse movement times this make the axis value (wheel notches too).</summary>
		public float MouseAxisSensitivity = 0.1f;

		/// <summary>An axis reads as a pressed button above this.</summary>
		[Header("Buttons")]
		[Range(0f, 1f)] public float ButtonDeadZone = 0.5f;

		/// <summary>Two presses within this many seconds are a double press.</summary>
		public float ButtonDoublePressSpeed = 0.3f;

		/// <summary>A press held at least this long is a short press (GetButtonShortPress).</summary>
		public float ButtonShortPressTime = 0.25f;

		/// <summary>The short press stops counting after this many more seconds (0: while held).</summary>
		public float ButtonShortPressExpiresIn;

		/// <summary>A press held at least this long is a long press (GetButtonLongPress).</summary>
		public float ButtonLongPressTime = 1f;

		/// <summary>The long press stops counting after this many more seconds (0: while held).</summary>
		public float ButtonLongPressExpiresIn;

		public override string ToString() => Name;
	}

	/// <summary>A controller map a player starts with.</summary>
	[Serializable]
	public sealed class StartingMap
	{
		public ControllerType Type;
		public int CategoryId;
		public int LayoutId;
		public bool Enabled = true;
	}

	/// <summary>A player of the game (one per person at the keyboard or holding a gamepad).</summary>
	[Serializable]
	public sealed class InputPlayerDefinition
	{
		public int Id;
		public string Name = "";
		public string DescriptiveName = "";

		/// <summary>The player starts with the keyboard (several players may share it).</summary>
		public bool AssignKeyboardOnStart = true;

		public bool AssignMouseOnStart = true;

		/// <summary>Gamepads may be assigned to the player automatically (<see cref="InputManagerSettings.AutoAssignGamepads" />).</summary>
		public bool AssignGamepads = true;

		public List<StartingMap> StartingMaps = new List<StartingMap>();

		public static InputPlayerDefinition CreateDefault(int id, string name)
		{
			var player = new InputPlayerDefinition { Id = id, Name = name };
			foreach (ControllerType type in ControllerTypes.All)
			{
				player.StartingMaps.Add(new StartingMap { Type = type, CategoryId = 0, LayoutId = 0, Enabled = true });
			}

			return player;
		}

		public override string ToString() => Name;
	}

	/// <summary>The bindings of one controller type in one map category and layout.</summary>
	[Serializable]
	public sealed class ControllerMap
	{
		public int Id;
		public ControllerType Type;
		public int CategoryId;
		public int LayoutId;
		public List<ActionElementMap> Bindings = new List<ActionElementMap>();

		/// <summary>A copy (players get their own: remapping changes a player's maps, not the configuration).</summary>
		public ControllerMap Clone()
		{
			var copy = new ControllerMap { Id = Id, Type = Type, CategoryId = CategoryId, LayoutId = LayoutId };
			foreach (ActionElementMap binding in Bindings)
			{
				copy.Bindings.Add(binding.Clone());
			}

			return copy;
		}
	}

	/// <summary>Which half of an action's axis a key or button drives (a button bound to an axis gives +1 or -1).</summary>
	public enum AxisContribution
	{
		Positive,
		Negative
	}

	/// <summary>The part of a controller axis a binding reads.</summary>
	public enum AxisRange
	{
		/// <summary>The whole axis, -1..1.</summary>
		Full,

		/// <summary>Only its positive half, as 0..1.</summary>
		Positive,

		/// <summary>Only its negative half, as 0..1.</summary>
		Negative
	}

	/// <summary>Keyboard modifiers a key binding needs held (Ctrl + S).</summary>
	[Flags]
	public enum ModifierKeys
	{
		None = 0,
		Shift = 1,
		Control = 2,
		Alt = 4
	}

	/// <summary>One element of a controller bound to an action.</summary>
	[Serializable]
	public sealed class ActionElementMap
	{
		public int ActionId;

		/// <summary>The element: a <see cref="OpenTK.Windowing.GraphicsLibraryFramework.Keys" /> code, a <see cref="MouseElement" /> or a <see cref="GamepadElement" />.</summary>
		public int Element;

		/// <summary>For keys and buttons bound to an axis: the direction they push it.</summary>
		public AxisContribution Contribution;

		/// <summary>For controller axes: the part read.</summary>
		public AxisRange Range;

		public bool Invert;

		/// <summary>Keyboard: modifiers that must be held.</summary>
		public ModifierKeys Modifiers;

		public ActionElementMap Clone()
		{
			return (ActionElementMap)MemberwiseClone();
		}
	}
}
