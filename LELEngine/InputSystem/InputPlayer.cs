using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.InputSystem
{
	/// <summary>A controller map loaded into a player (its own copy, so remapping changes only this player) and whether it is read.</summary>
	public sealed class PlayerMap
	{
		public ControllerMap Map;
		public bool Enabled;

		public ControllerType Type => Map.Type;
		public int CategoryId => Map.CategoryId;
		public int LayoutId => Map.LayoutId;
	}

	/// <summary>
	///     A player of the game: the controllers assigned to it, the controller maps loaded into it, and its actions'
	///     values this frame (Rewired's Player). Read actions by name or id: <see cref="GetButtonDown(string)" />,
	///     <see cref="GetAxis(string)" />, ... The values change once per frame (<see cref="InputManager" /> updates them
	///     before the scenes run); FixedUpdate reads the frame's values.
	/// </summary>
	public sealed class InputPlayer
	{
		#region PublicFields

		public int Id { get; }
		public string Name { get; }
		public string DescriptiveName { get; }

		/// <summary>Off: every action reads 0 / released (a player not in the game, a paused one).</summary>
		public bool IsPlaying = true;

		/// <summary>The controllers assigned to the player.</summary>
		public IReadOnlyList<Controller> Controllers => controllers;

		public bool HasKeyboard => controllers.Exists(c => c.Type == ControllerType.Keyboard);
		public bool HasMouse => controllers.Exists(c => c.Type == ControllerType.Mouse);

		public IEnumerable<GamepadController> Gamepads
		{
			get
			{
				foreach (Controller controller in controllers)
				{
					if (controller is GamepadController gamepad)
					{
						yield return gamepad;
					}
				}
			}
		}

		public int GamepadCount => controllers.FindAll(c => c.Type == ControllerType.Gamepad).Count;

		/// <summary>The player's controller maps (copies of the configuration's, or of the user's saved remapping).</summary>
		public IReadOnlyList<PlayerMap> Maps => maps;

		/// <summary>The assigned controller used last (keyboard and mouse, or a gamepad): the game can show matching prompts or aim with it.</summary>
		public Controller LastActiveController { get; private set; }

		#endregion

		#region PrivateFields

		private sealed class ActionState
		{
			public float Value;
			public float ValuePrevious;
			public float Raw;
			public float RawPrevious;
			public float Simulated;
			public bool Pressed;
			public bool PressedPrevious;
			public bool NegativePressed;
			public bool NegativePressedPrevious;
			public double PressStart = double.NegativeInfinity;
			public double ReleaseTime = double.NegativeInfinity;
			public double LastPressDuration;
			public bool DoublePress;
			public double ActiveSince = double.NegativeInfinity;
		}

		private readonly List<Controller> controllers = new List<Controller>();
		private readonly List<PlayerMap> maps = new List<PlayerMap>();
		private readonly Dictionary<int, ActionState> states = new Dictionary<int, ActionState>();
		private InputConfiguration configuration;
		private double now;
		private double frameTime;

		#endregion

		#region Constructors

		internal InputPlayer(InputConfiguration configuration, InputPlayerDefinition definition)
		{
			this.configuration = configuration;
			Id = definition.Id;
			Name = definition.Name;
			DescriptiveName = string.IsNullOrEmpty(definition.DescriptiveName) ? definition.Name : definition.DescriptiveName;
			foreach (StartingMap start in definition.StartingMaps)
			{
				LoadMap(start.Type, start.CategoryId, start.LayoutId, start.Enabled);
			}
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- buttons

		/// <summary>Held this frame (a button action pressed; an axis action pushed past the button dead zone either way).</summary>
		public bool GetButton(string action) => State(action) is ActionState s && s.Pressed;

		public bool GetButton(int actionId) => State(actionId) is ActionState s && s.Pressed;

		/// <summary>Pressed this frame.</summary>
		public bool GetButtonDown(string action) => State(action) is ActionState s && s.Pressed && !s.PressedPrevious;

		public bool GetButtonDown(int actionId) => State(actionId) is ActionState s && s.Pressed && !s.PressedPrevious;

		/// <summary>Released this frame.</summary>
		public bool GetButtonUp(string action) => State(action) is ActionState s && !s.Pressed && s.PressedPrevious;

		public bool GetButtonUp(int actionId) => State(actionId) is ActionState s && !s.Pressed && s.PressedPrevious;

		/// <summary>Held the frame before.</summary>
		public bool GetButtonPrevious(string action) => State(action) is ActionState s && s.PressedPrevious;

		/// <summary>An axis action pushed to its negative side past the button dead zone (Move Horizontal to the left).</summary>
		public bool GetNegativeButton(string action) => State(action) is ActionState s && s.NegativePressed;

		public bool GetNegativeButtonDown(string action) => State(action) is ActionState s && s.NegativePressed && !s.NegativePressedPrevious;

		public bool GetNegativeButtonUp(string action) => State(action) is ActionState s && !s.NegativePressed && s.NegativePressedPrevious;

		/// <summary>Pressed this frame within the behavior's double press time of the press before.</summary>
		public bool GetButtonDoublePressDown(string action) => GetButtonDown(action) && State(action).DoublePress;

		/// <summary>Held, and the press began as a double press.</summary>
		public bool GetButtonDoublePressHold(string action) => State(action) is ActionState s && s.Pressed && s.DoublePress;

		/// <summary>Held for at least <paramref name="seconds" /> (and, with <paramref name="expiresIn" /> above 0, not longer than that much more).</summary>
		public bool GetButtonTimedPress(string action, float seconds, float expiresIn = 0f)
		{
			ActionState s = State(action);
			if (s == null || !s.Pressed)
			{
				return false;
			}

			double held = now - s.PressStart;
			return held >= seconds && (expiresIn <= 0f || held <= seconds + expiresIn);
		}

		/// <summary>The frame the press reaches <paramref name="seconds" />.</summary>
		public bool GetButtonTimedPressDown(string action, float seconds)
		{
			ActionState s = State(action);
			if (s == null || !s.Pressed)
			{
				return false;
			}

			double held = now - s.PressStart;
			return held >= seconds && held - frameTime < seconds;
		}

		/// <summary>Released this frame after a press of at least <paramref name="seconds" /> (not longer than that plus <paramref name="expiresIn" /> when above 0).</summary>
		public bool GetButtonTimedPressUp(string action, float seconds, float expiresIn = 0f)
		{
			ActionState s = State(action);
			return s != null && !s.Pressed && s.PressedPrevious && s.LastPressDuration >= seconds && (expiresIn <= 0f || s.LastPressDuration <= seconds + expiresIn);
		}

		/// <summary>Held for the action's behavior short press time (within its expiry).</summary>
		public bool GetButtonShortPress(string action) => Behavior(action) is InputBehavior b && GetButtonTimedPress(action, b.ButtonShortPressTime, b.ButtonShortPressExpiresIn);

		public bool GetButtonShortPressDown(string action) => Behavior(action) is InputBehavior b && GetButtonTimedPressDown(action, b.ButtonShortPressTime);

		public bool GetButtonShortPressUp(string action) => Behavior(action) is InputBehavior b && GetButtonTimedPressUp(action, b.ButtonShortPressTime, b.ButtonShortPressExpiresIn);

		/// <summary>Held for the action's behavior long press time (within its expiry).</summary>
		public bool GetButtonLongPress(string action) => Behavior(action) is InputBehavior b && GetButtonTimedPress(action, b.ButtonLongPressTime, b.ButtonLongPressExpiresIn);

		public bool GetButtonLongPressDown(string action) => Behavior(action) is InputBehavior b && GetButtonTimedPressDown(action, b.ButtonLongPressTime);

		public bool GetButtonLongPressUp(string action) => Behavior(action) is InputBehavior b && GetButtonTimedPressUp(action, b.ButtonLongPressTime, b.ButtonLongPressExpiresIn);

		/// <summary>Seconds the button has been held (0 when released).</summary>
		public float GetButtonTimePressed(string action) => State(action) is ActionState s && s.Pressed ? (float)(now - s.PressStart) : 0f;

		/// <summary>Seconds since the button was released (0 while held).</summary>
		public float GetButtonTimeUnpressed(string action) => State(action) is ActionState s && !s.Pressed && !double.IsNegativeInfinity(s.ReleaseTime) ? (float)(now - s.ReleaseTime) : 0f;

		/// <summary>Any action held.</summary>
		public bool GetAnyButton()
		{
			foreach (ActionState s in states.Values)
			{
				if (s.Pressed)
				{
					return true;
				}
			}

			return false;
		}

		public bool GetAnyButtonDown()
		{
			foreach (ActionState s in states.Values)
			{
				if (s.Pressed && !s.PressedPrevious)
				{
					return true;
				}
			}

			return false;
		}

		public bool GetAnyButtonUp()
		{
			foreach (ActionState s in states.Values)
			{
				if (!s.Pressed && s.PressedPrevious)
				{
					return true;
				}
			}

			return false;
		}

		// ---------------------------------------------------------------- axes

		/// <summary>The action's value: -1..1 (mouse axes in their sensitivity's units), keys and buttons through digital axis simulation.</summary>
		public float GetAxis(string action) => State(action)?.Value ?? 0f;

		public float GetAxis(int actionId) => State(actionId)?.Value ?? 0f;

		/// <summary>The value without the digital axis simulation (keys jump to ±1).</summary>
		public float GetAxisRaw(string action) => State(action)?.Raw ?? 0f;

		public float GetAxisRaw(int actionId) => State(actionId)?.Raw ?? 0f;

		public float GetAxisPrevious(string action) => State(action)?.ValuePrevious ?? 0f;

		/// <summary>The change since the last frame.</summary>
		public float GetAxisDelta(string action) => State(action) is ActionState s ? s.Value - s.ValuePrevious : 0f;

		/// <summary>Two axis actions as a vector (Move Horizontal, Move Vertical), its length clamped to 1 for sticks and keys.</summary>
		public Vector2 GetAxis2D(string xAction, string yAction)
		{
			return new Vector2(GetAxis(xAction), GetAxis(yAction));
		}

		public Vector2 GetAxis2DRaw(string xAction, string yAction)
		{
			return new Vector2(GetAxisRaw(xAction), GetAxisRaw(yAction));
		}

		/// <summary>Seconds the axis has been away from 0.</summary>
		public float GetAxisTimeActive(string action) => State(action) is ActionState s && s.Value != 0f ? (float)(now - s.ActiveSince) : 0f;

		// ---------------------------------------------------------------- bindings

		/// <summary>
		///     The name of the first element bound to the action ("Space", "Left Button", "Action Bottom"), preferring a
		///     controller type (the last active controller's when null); the contribution picks a direction of an axis
		///     action. Null when nothing is bound. For prompts ("Press [Space] to jump").
		/// </summary>
		public string GetBindingName(string action, AxisContribution? contribution = null, ControllerType? preferred = null)
		{
			InputAction definition = configuration.FindAction(action);
			if (definition == null)
			{
				return null;
			}

			ControllerType wanted = preferred ?? (LastActiveController?.Type == ControllerType.Gamepad ? ControllerType.Gamepad : ControllerType.Keyboard);
			string fallback = null;
			foreach (PlayerMap map in maps)
			{
				if (!map.Enabled)
				{
					continue;
				}

				foreach (ActionElementMap binding in map.Map.Bindings)
				{
					if (binding.ActionId != definition.Id || (contribution != null && !ControllerTypes.IsAxis(map.Type, binding.Element) && binding.Contribution != contribution))
					{
						continue;
					}

					string name = ControllerTypes.ElementName(map.Type, binding.Element, binding.Modifiers, binding.Range);
					if (map.Type == wanted || (wanted == ControllerType.Keyboard && map.Type == ControllerType.Mouse))
					{
						return name;
					}

					fallback ??= name;
				}
			}

			return fallback;
		}

		// ---------------------------------------------------------------- controllers

		/// <summary>Gives the player a controller; a gamepad leaves its other player (the keyboard and the mouse may be shared).</summary>
		public void AddController(Controller controller)
		{
			if (controller == null || controllers.Contains(controller))
			{
				return;
			}

			if (controller is GamepadController && controller.Owner != null && controller.Owner != this)
			{
				controller.Owner.RemoveController(controller);
			}

			controllers.Add(controller);
			controller.Owner ??= this;
			if (controller is GamepadController)
			{
				controller.Owner = this;
			}
		}

		public void RemoveController(Controller controller)
		{
			if (controllers.Remove(controller) && controller.Owner == this)
			{
				controller.Owner = null;
			}
		}

		// ---------------------------------------------------------------- maps

		/// <summary>
		///     Loads the configuration's map of a controller type, map category and layout into the player (replacing the
		///     player's map of that type and category). Returns false when the configuration has no such map.
		/// </summary>
		public bool LoadMap(ControllerType type, int categoryId, int layoutId, bool enabled = true)
		{
			ControllerMap source = configuration.FindMap(type, categoryId, layoutId);
			if (source == null)
			{
				return false;
			}

			maps.RemoveAll(m => m.Type == type && m.CategoryId == categoryId);
			maps.Add(new PlayerMap { Map = source.Clone(), Enabled = enabled });
			return true;
		}

		/// <summary>Loads a map by its category's and layout's names (the Default layout when null).</summary>
		public bool LoadMap(ControllerType type, string category, string layout = null, bool enabled = true)
		{
			InputCategory mapCategory = configuration.FindMapCategory(category);
			InputCategory mapLayout = layout != null ? configuration.FindLayout(layout) : configuration.Layouts.Count > 0 ? configuration.Layouts[0] : null;
			return mapCategory != null && mapLayout != null && LoadMap(type, mapCategory.Id, mapLayout.Id, enabled);
		}

		/// <summary>Turns the player's maps of a map category on or off (every controller type; one type with <paramref name="type" />).</summary>
		public void SetMapsEnabled(bool enabled, string category, ControllerType? type = null)
		{
			InputCategory mapCategory = configuration.FindMapCategory(category);
			if (mapCategory == null)
			{
				Debug.LogWarning("[Input] No map category " + category);
				return;
			}

			foreach (PlayerMap map in maps)
			{
				if (map.CategoryId == mapCategory.Id && (type == null || map.Type == type))
				{
					map.Enabled = enabled;
				}
			}
		}

		/// <summary>The player's map of a controller type and map category, or null.</summary>
		public PlayerMap GetMap(ControllerType type, int categoryId)
		{
			return maps.Find(m => m.Type == type && m.CategoryId == categoryId);
		}

		/// <summary>Replaces the player's maps (the user's saved remapping).</summary>
		internal void ReplaceMaps(IEnumerable<PlayerMap> loaded)
		{
			foreach (PlayerMap map in loaded)
			{
				maps.RemoveAll(m => m.Type == map.Type && m.CategoryId == map.CategoryId);
				maps.Add(map);
			}
		}

		public override string ToString()
		{
			return Name;
		}

		#endregion

		#region InternalMethods

		/// <summary>Reads every action from the assigned controllers through the enabled maps.</summary>
		internal void Update(double time, float deltaTime)
		{
			now = time;
			frameTime = deltaTime;
			foreach (Controller controller in controllers)
			{
				if (controller.IsConnected && (LastActiveController == null || controller.LastActiveTime > LastActiveController.LastActiveTime))
				{
					LastActiveController = controller;
				}
			}

			foreach (InputAction action in configuration.Actions)
			{
				if (!states.TryGetValue(action.Id, out ActionState state))
				{
					state = new ActionState();
					states[action.Id] = state;
				}

				Evaluate(action, state, deltaTime);
			}
		}

		#endregion

		#region PrivateMethods

		private ActionState State(string action)
		{
			InputAction definition = configuration.FindAction(action);
			if (definition == null)
			{
				InputManager.WarnUnknownAction(action);
				return null;
			}

			return State(definition.Id);
		}

		private ActionState State(int actionId)
		{
			return states.TryGetValue(actionId, out ActionState state) ? state : null;
		}

		private InputBehavior Behavior(string action)
		{
			InputAction definition = configuration.FindAction(action);
			return definition != null ? configuration.FindBehavior(definition.BehaviorId) : null;
		}

		private void Evaluate(InputAction action, ActionState state, float deltaTime)
		{
			InputBehavior behavior = configuration.FindBehavior(action.BehaviorId) ?? new InputBehavior();
			state.ValuePrevious = state.Value;
			state.RawPrevious = state.Raw;
			state.PressedPrevious = state.Pressed;
			state.NegativePressedPrevious = state.NegativePressed;

			// The analog value with the largest magnitude wins; keys and buttons push the axis to ±1 (or press the button).
			float analog = 0f;
			bool digitalPositive = false;
			bool digitalNegative = false;
			if (IsPlaying)
			{
				foreach (PlayerMap map in maps)
				{
					if (!map.Enabled)
					{
						continue;
					}

					foreach (Controller controller in controllers)
					{
						if (controller.Type != map.Type || !controller.IsConnected)
						{
							continue;
						}

						foreach (ActionElementMap binding in map.Map.Bindings)
						{
							if (binding.ActionId != action.Id)
							{
								continue;
							}

							if (ControllerTypes.IsAxis(controller.Type, binding.Element))
							{
								float value = AxisValue(controller, binding, behavior);
								if (Math.Abs(value) > Math.Abs(analog))
								{
									analog = value;
								}
							}
							else if (controller.GetButton(binding.Element) && ModifiersHeld(controller, binding))
							{
								if (action.Type == InputActionType.Axis && binding.Contribution == AxisContribution.Negative)
								{
									digitalNegative = true;
								}
								else
								{
									digitalPositive = true;
								}
							}
						}
					}
				}
			}

			float target = (digitalPositive ? 1f : 0f) - (digitalNegative ? 1f : 0f);
			if (action.Type == InputActionType.Axis)
			{
				if (behavior.DigitalAxisSimulation)
				{
					if (target != 0f)
					{
						if (behavior.DigitalAxisSnap && state.Simulated != 0f && Math.Sign(state.Simulated) != Math.Sign(target))
						{
							state.Simulated = 0f;
						}

						state.Simulated = MoveTowards(state.Simulated, target, behavior.DigitalAxisSensitivity * deltaTime);
					}
					else
					{
						state.Simulated = MoveTowards(state.Simulated, 0f, behavior.DigitalAxisGravity * deltaTime);
					}
				}
				else
				{
					state.Simulated = target;
				}

				state.Value = Math.Abs(analog) >= Math.Abs(state.Simulated) ? analog : state.Simulated;
				state.Raw = Math.Abs(analog) >= Math.Abs(target) ? analog : target;
				state.Pressed = state.Raw > behavior.ButtonDeadZone || digitalPositive;
				state.NegativePressed = state.Raw < -behavior.ButtonDeadZone || digitalNegative;
			}
			else
			{
				state.Pressed = digitalPositive || analog > behavior.ButtonDeadZone;
				state.Value = state.Pressed ? Math.Max(1f, analog) : Math.Max(0f, analog);
				state.Raw = state.Value;
				state.NegativePressed = false;
			}

			// Press timing (double, short and long presses).
			if (state.Pressed && !state.PressedPrevious)
			{
				state.DoublePress = now - state.PressStart <= behavior.ButtonDoublePressSpeed;
				state.PressStart = now;
			}
			else if (!state.Pressed && state.PressedPrevious)
			{
				state.ReleaseTime = now;
				state.LastPressDuration = now - state.PressStart;
			}

			if (state.Value != 0f && state.ValuePrevious == 0f)
			{
				state.ActiveSince = now;
			}
		}

		private static float AxisValue(Controller controller, ActionElementMap binding, InputBehavior behavior)
		{
			float value = controller.GetValue(binding.Element);
			switch (binding.Range)
			{
				case AxisRange.Positive:
					value = Math.Max(0f, value);
					break;
				case AxisRange.Negative:
					value = Math.Max(0f, -value);
					break;
			}

			if (binding.Range != AxisRange.Full && binding.Contribution == AxisContribution.Negative)
			{
				value = -value;
			}

			if (binding.Invert)
			{
				value = -value;
			}

			if (controller.Type == ControllerType.Mouse)
			{
				value *= behavior.MouseAxisSensitivity;
				if (behavior.MouseAxisMode == MouseAxisMode.Clamped)
				{
					value = Math.Clamp(value, -1f, 1f);
				}
			}
			else if (controller.Type == ControllerType.Gamepad)
			{
				value = Math.Clamp(value * behavior.GamepadAxisSensitivity, -1f, 1f);
			}

			return value;
		}

		// A binding with modifiers needs them held (Ctrl + S); one without works whatever is held.
		private static bool ModifiersHeld(Controller controller, ActionElementMap binding)
		{
			if (binding.Modifiers == ModifierKeys.None || controller is not KeyboardController keyboard)
			{
				return true;
			}

			return (keyboard.Modifiers & binding.Modifiers) == binding.Modifiers;
		}

		private static float MoveTowards(float current, float target, float maxDelta)
		{
			return Math.Abs(target - current) <= maxDelta ? target : current + Math.Sign(target - current) * maxDelta;
		}

		#endregion
	}
}
