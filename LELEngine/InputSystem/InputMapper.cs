using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.InputSystem
{
	/// <summary>What the mapper does when the element pressed is already bound in the player's map.</summary>
	public enum InputConflictResolution
	{
		/// <summary>The old binding is removed.</summary>
		Replace,

		/// <summary>The old binding gets the element the new one replaces (or is removed when there is none).</summary>
		Swap,

		/// <summary>Both bindings stay.</summary>
		Allow,

		/// <summary>Nothing changes; the result reports the conflict.</summary>
		Cancel
	}

	public enum InputMapperOutcome
	{
		Assigned,
		Canceled,
		TimedOut,
		Conflict
	}

	/// <summary>The end of a remapping (<see cref="InputMapper.Finished" />).</summary>
	public sealed class InputMapperResult
	{
		public InputMapperOutcome Outcome;
		public InputPlayer Player;
		public PlayerMap Map;
		public InputAction Action;

		/// <summary>The binding made (Assigned), or the one that would have been (Conflict).</summary>
		public ActionElementMap Binding;

		/// <summary>Bindings of the same element that were in the map (replaced, swapped, kept or blocking).</summary>
		public readonly List<ActionElementMap> Conflicts = new List<ActionElementMap>();
	}

	/// <summary>
	///     Remapping (Rewired's Input Mapper): <see cref="Start" /> listens for the next element a player presses on a
	///     controller type (after everything was released) and binds it to an action in the player's map: the key with
	///     the modifiers held, a mouse button or wheel direction, a gamepad button, or a gamepad axis pushed past
	///     <see cref="AxisThreshold" /> (its half for a button or one direction of an axis action). Escape cancels; so does
	///     <see cref="Timeout" />. Conflicts follow <see cref="ConflictResolution" />. Save with
	///     <see cref="InputManager.SaveUserData" />.
	/// </summary>
	public sealed class InputMapper
	{
		#region PublicFields

		public bool IsListening { get; private set; }

		/// <summary>Seconds of listening before giving up (0: no limit).</summary>
		public float Timeout = 5f;

		public float AxisThreshold = 0.7f;

		/// <summary>Mouse movement counts as an element (off by default: a hand on the mouse moves it).</summary>
		public bool AllowMouseMovement;

		public InputConflictResolution ConflictResolution = InputConflictResolution.Replace;

		/// <summary>Raised when listening ends (assigned, canceled, timed out or blocked by a conflict).</summary>
		public event Action<InputMapperResult> Finished;

		#endregion

		#region PrivateFields

		private InputPlayer player;
		private PlayerMap map;
		private InputAction action;
		private AxisContribution contribution;
		private ActionElementMap replacing;
		private bool waitingForRelease;
		private double started;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Listens for an element of <paramref name="type" /> to bind to <paramref name="action" /> in the player's map
		///     of that type and <paramref name="mapCategoryId" />. <paramref name="replacing" /> is a binding of that map the
		///     new one takes the place of (null: added).
		/// </summary>
		public bool Start(InputPlayer player, ControllerType type, int mapCategoryId, InputAction action, AxisContribution contribution = AxisContribution.Positive, ActionElementMap replacing = null)
		{
			PlayerMap target = player?.GetMap(type, mapCategoryId);
			if (target == null || action == null)
			{
				Debug.LogWarning("[Input] The mapper needs a player with a " + type + " map of that category and an action");
				return false;
			}

			this.player = player;
			map = target;
			this.action = action;
			this.contribution = contribution;
			this.replacing = replacing;
			waitingForRelease = true;
			started = Clock();
			IsListening = true;
			return true;
		}

		public void Stop()
		{
			if (IsListening)
			{
				Finish(InputMapperOutcome.Canceled, null, null);
			}
		}

		#endregion

		#region InternalMethods

		internal void Update()
		{
			if (!IsListening)
			{
				return;
			}

			if (Timeout > 0f && Clock() - started > Timeout)
			{
				Finish(InputMapperOutcome.TimedOut, null, null);
				return;
			}

			if (InputManager.Keyboard.GetButtonDown((int)Keys.Escape))
			{
				Finish(InputMapperOutcome.Canceled, null, null);
				return;
			}

			ActionElementMap pressed = Detect();
			if (waitingForRelease)
			{
				// What was held when listening began (the click on "Remap") must be let go first.
				waitingForRelease = pressed != null;
				return;
			}

			if (pressed != null)
			{
				Assign(pressed);
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>The element of the map's controller type the player is pressing now, as a binding of the action.</summary>
		private ActionElementMap Detect()
		{
			foreach (Controller controller in player.Controllers)
			{
				if (controller.Type != map.Type)
				{
					continue;
				}

				for (int element = 0; element < controller.ElementCount; element++)
				{
					if (controller.Type == ControllerType.Keyboard && IsModifier((Keys)element))
					{
						continue;
					}

					if (ControllerTypes.IsAxis(controller.Type, element))
					{
						bool movement = controller.Type == ControllerType.Mouse && element <= (int)MouseElement.AxisY;
						if (movement && !AllowMouseMovement)
						{
							continue;
						}

						float value = controller.GetValue(element);
						float threshold = movement ? 30f : controller.Type == ControllerType.Mouse ? 0.5f : AxisThreshold;
						if (Math.Abs(value) >= threshold)
						{
							return AxisBinding(element, value);
						}
					}
					else if (controller.GetButton(element))
					{
						var binding = new ActionElementMap { ActionId = action.Id, Element = element, Contribution = contribution };
						if (controller is KeyboardController keyboard)
						{
							binding.Modifiers = keyboard.Modifiers;
						}

						return binding;
					}
				}
			}

			return null;
		}

		// A pushed axis: the whole axis for an axis action read in its positive direction, else its pushed half.
		private ActionElementMap AxisBinding(int element, float value)
		{
			var binding = new ActionElementMap { ActionId = action.Id, Element = element, Contribution = contribution };
			bool wholeAxis = action.Type == InputActionType.Axis && contribution == AxisContribution.Positive && !(map.Type == ControllerType.Gamepad && (element == (int)GamepadElement.LeftTrigger || element == (int)GamepadElement.RightTrigger));
			if (wholeAxis)
			{
				binding.Range = AxisRange.Full;
				binding.Invert = value < 0f;
			}
			else
			{
				binding.Range = value >= 0f ? AxisRange.Positive : AxisRange.Negative;
			}

			return binding;
		}

		private void Assign(ActionElementMap binding)
		{
			var result = new InputMapperResult { Player = player, Map = map, Action = action, Binding = binding };
			foreach (ActionElementMap existing in map.Map.Bindings)
			{
				if (existing != replacing && existing.Element == binding.Element && existing.Modifiers == binding.Modifiers && SameHalf(existing, binding))
				{
					result.Conflicts.Add(existing);
				}
			}

			if (result.Conflicts.Count > 0)
			{
				switch (ConflictResolution)
				{
					case InputConflictResolution.Cancel:
						Finish(InputMapperOutcome.Conflict, binding, result);
						return;
					case InputConflictResolution.Replace:
						foreach (ActionElementMap conflict in result.Conflicts)
						{
							map.Map.Bindings.Remove(conflict);
						}

						break;
					case InputConflictResolution.Swap:
						foreach (ActionElementMap conflict in result.Conflicts)
						{
							if (replacing != null)
							{
								conflict.Element = replacing.Element;
								conflict.Modifiers = replacing.Modifiers;
								conflict.Range = replacing.Range;
								conflict.Invert = replacing.Invert;
							}
							else
							{
								map.Map.Bindings.Remove(conflict);
							}
						}

						break;
				}
			}

			int index = replacing != null ? map.Map.Bindings.IndexOf(replacing) : -1;
			if (index >= 0)
			{
				map.Map.Bindings[index] = binding;
			}
			else
			{
				map.Map.Bindings.Add(binding);
			}

			Finish(InputMapperOutcome.Assigned, binding, result);
		}

		// Two bindings of one axis conflict only on the same half (a stick's left and right may be different actions).
		private bool SameHalf(ActionElementMap a, ActionElementMap b)
		{
			if (!ControllerTypes.IsAxis(map.Type, a.Element))
			{
				return true;
			}

			return a.Range == AxisRange.Full || b.Range == AxisRange.Full || a.Range == b.Range;
		}

		private void Finish(InputMapperOutcome outcome, ActionElementMap binding, InputMapperResult result)
		{
			IsListening = false;
			result ??= new InputMapperResult { Player = player, Map = map, Action = action, Binding = binding };
			result.Outcome = outcome;
			player = null;
			map = null;
			action = null;
			replacing = null;
			Finished?.Invoke(result);
		}

		private static bool IsModifier(Keys key)
		{
			return key == Keys.LeftShift || key == Keys.RightShift || key == Keys.LeftControl || key == Keys.RightControl || key == Keys.LeftAlt || key == Keys.RightAlt || key == Keys.LeftSuper || key == Keys.RightSuper;
		}

		private static double Clock()
		{
			return System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
		}

		#endregion
	}
}
