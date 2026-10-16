using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>A reusable behaviour attached to an element through its event callbacks.</summary>
	public interface IManipulator
	{
		VisualElement target { get; set; }
	}

	public abstract class Manipulator : IManipulator
	{
		#region PublicFields

		public VisualElement target
		{
			get => m_Target;
			set
			{
				if (m_Target == value)
				{
					return;
				}

				if (m_Target != null)
				{
					UnregisterCallbacksFromTarget();
				}

				m_Target = value;
				if (m_Target != null)
				{
					RegisterCallbacksOnTarget();
				}
			}
		}

		#endregion

		#region PrivateFields

		private VisualElement m_Target;

		#endregion

		#region ProtectedMethods

		protected abstract void RegisterCallbacksOnTarget();
		protected abstract void UnregisterCallbacksFromTarget();

		#endregion
	}

	/// <summary>Which pointer button and modifiers start a manipulation.</summary>
	public struct ManipulatorActivationFilter
	{
		public int button;
		public EventModifiers modifiers;
		public int clickCount;

		public bool Matches(IPointerEvent evt)
		{
			return button == evt.button && HasModifiers(evt.modifiers) && (clickCount == 0 || evt.clickCount == clickCount);
		}

		public bool Matches(IMouseEvent evt)
		{
			return button == evt.button && HasModifiers(evt.modifiers) && (clickCount == 0 || evt.clickCount == clickCount);
		}

		private bool HasModifiers(EventModifiers current)
		{
			const EventModifiers relevant = EventModifiers.Shift | EventModifiers.Control | EventModifiers.Alt | EventModifiers.Command;
			return (current & relevant) == (modifiers & relevant);
		}
	}

	public enum MouseButton
	{
		LeftMouse = 0,
		RightMouse = 1,
		MiddleMouse = 2
	}

	public abstract class MouseManipulator : Manipulator
	{
		#region PublicFields

		public List<ManipulatorActivationFilter> activators { get; } = new List<ManipulatorActivationFilter>();

		#endregion

		#region PrivateFields

		private ManipulatorActivationFilter currentActivator;

		#endregion

		#region ProtectedMethods

		protected bool CanStartManipulation(IMouseEvent e)
		{
			foreach (ManipulatorActivationFilter activator in activators)
			{
				if (activator.Matches(e))
				{
					currentActivator = activator;
					return true;
				}
			}

			return false;
		}

		protected bool CanStopManipulation(IMouseEvent e)
		{
			return e != null && e.button == currentActivator.button;
		}

		#endregion
	}

	public abstract class PointerManipulator : MouseManipulator
	{
		#region PrivateFields

		private int currentPointerId = -1;
		private int currentButton = -1;

		#endregion

		#region ProtectedMethods

		protected bool CanStartManipulation(IPointerEvent e)
		{
			foreach (ManipulatorActivationFilter activator in activators)
			{
				if (activator.Matches(e))
				{
					currentPointerId = e.pointerId;
					currentButton = e.button;
					return true;
				}
			}

			return false;
		}

		protected bool CanStopManipulation(IPointerEvent e)
		{
			return e != null && e.pointerId == currentPointerId && (e.button == currentButton || e.button < 0);
		}

		#endregion
	}

	/// <summary>
	///     Turns pointer presses on an element into clicks: sets :active while pressed, captures the pointer, fires
	///     <see cref="clicked" /> when released over the element (or repeatedly while held, with a delay and interval).
	/// </summary>
	public class Clickable : PointerManipulator
	{
		#region PublicFields

		public event Action<EventBase> clickedWithEventInfo;
		public event Action clicked;

		/// <summary>Position of the last pointer event, relative to the target.</summary>
		public Vector2 lastMousePosition { get; private set; }

		/// <summary>Clicks also fire while the target is disabled (default false).</summary>
		public bool acceptClicksIfDisabled { get; set; }

		public bool active { get; private set; }

		#endregion

		#region PrivateFields

		private readonly long delay;
		private readonly long interval;
		private IVisualElementScheduledItem repeatTimer;

		#endregion

		#region Constructors

		public Clickable(Action handler, long delay, long interval)
			: this(handler)
		{
			this.delay = delay;
			this.interval = interval;
		}

		public Clickable(Action<EventBase> handler)
		{
			clickedWithEventInfo = handler;
			activators.Add(new ManipulatorActivationFilter { button = (int)MouseButton.LeftMouse });
		}

		public Clickable(Action handler)
		{
			clicked = handler;
			activators.Add(new ManipulatorActivationFilter { button = (int)MouseButton.LeftMouse });
		}

		#endregion

		#region PublicMethods

		/// <summary>Simulates a click (keyboard activation of buttons).</summary>
		public void SimulateSingleClick(EventBase evt, int delayMs = 100)
		{
			target.pseudoStates |= PseudoStates.Active;
			target.schedule.Execute(() => target.pseudoStates &= ~PseudoStates.Active).StartingIn(delayMs);
			Invoke(evt);
		}

		#endregion

		#region ProtectedMethods

		protected override void RegisterCallbacksOnTarget()
		{
			target.RegisterCallback<PointerDownEvent>(OnPointerDown);
			target.RegisterCallback<PointerMoveEvent>(OnPointerMove);
			target.RegisterCallback<PointerUpEvent>(OnPointerUp);
			target.RegisterCallback<PointerCancelEvent>(OnPointerCancel);
			target.RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
		}

		protected override void UnregisterCallbacksFromTarget()
		{
			target.UnregisterCallback<PointerDownEvent>(OnPointerDown);
			target.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
			target.UnregisterCallback<PointerUpEvent>(OnPointerUp);
			target.UnregisterCallback<PointerCancelEvent>(OnPointerCancel);
			target.UnregisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
		}

		protected virtual void ProcessDownEvent(EventBase evt, Vector2 localPosition, int pointerId)
		{
			active = true;
			target.CapturePointer(pointerId);
			target.pseudoStates |= PseudoStates.Active;
			if (delay > 0 || interval > 0)
			{
				repeatTimer?.Pause();
				repeatTimer = target.schedule.Execute(OnRepeat).StartingIn(delay).Every(interval > 0 ? interval : delay);
			}

			evt.StopImmediatePropagation();
		}

		protected virtual void ProcessMoveEvent(EventBase evt, Vector2 localPosition)
		{
			if (target.ContainsPoint(localPosition))
			{
				target.pseudoStates |= PseudoStates.Active;
			}
			else
			{
				target.pseudoStates &= ~PseudoStates.Active;
			}

			evt.StopPropagation();
		}

		protected virtual void ProcessUpEvent(EventBase evt, Vector2 localPosition, int pointerId)
		{
			bool inside = target.ContainsPoint(localPosition);
			ProcessCancelEvent(evt, pointerId);
			if (inside && (delay == 0 && interval == 0))
			{
				Invoke(evt);
			}
		}

		protected virtual void ProcessCancelEvent(EventBase evt, int pointerId)
		{
			active = false;
			repeatTimer?.Pause();
			repeatTimer = null;
			target.pseudoStates &= ~PseudoStates.Active;
			target.ReleasePointer(pointerId);
			evt.StopPropagation();
		}

		protected void Invoke(EventBase evt)
		{
			if (!acceptClicksIfDisabled && !target.enabledInHierarchy)
			{
				return;
			}

			clickedWithEventInfo?.Invoke(evt);
			clicked?.Invoke();
		}

		#endregion

		#region PrivateMethods

		private void OnPointerDown(PointerDownEvent evt)
		{
			if (!CanStartManipulation(evt))
			{
				return;
			}

			if (!acceptClicksIfDisabled && !target.enabledInHierarchy)
			{
				return;
			}

			lastMousePosition = evt.localPosition;
			ProcessDownEvent(evt, evt.localPosition, evt.pointerId);
		}

		private void OnPointerMove(PointerMoveEvent evt)
		{
			if (!active)
			{
				return;
			}

			lastMousePosition = evt.localPosition;
			ProcessMoveEvent(evt, evt.localPosition);
		}

		private void OnPointerUp(PointerUpEvent evt)
		{
			if (!active || !CanStopManipulation(evt))
			{
				return;
			}

			lastMousePosition = evt.localPosition;
			ProcessUpEvent(evt, evt.localPosition, evt.pointerId);
		}

		private void OnPointerCancel(PointerCancelEvent evt)
		{
			if (active && CanStopManipulation(evt))
			{
				ProcessCancelEvent(evt, evt.pointerId);
			}
		}

		private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
		{
			if (active)
			{
				active = false;
				repeatTimer?.Pause();
				repeatTimer = null;
				target.pseudoStates &= ~PseudoStates.Active;
			}
		}

		private void OnRepeat()
		{
			if (active && (target.pseudoStates & PseudoStates.Active) != 0)
			{
				Invoke(null);
			}
		}

		#endregion
	}

	/// <summary>Sends <see cref="ContextClickEvent" /> on right clicks (helper for context menus).</summary>
	public sealed class ContextualMenuManipulator : MouseManipulator
	{
		private readonly Action<ContextClickEvent> onContextClick;

		public ContextualMenuManipulator(Action<ContextClickEvent> onContextClick)
		{
			this.onContextClick = onContextClick;
			activators.Add(new ManipulatorActivationFilter { button = (int)MouseButton.RightMouse });
		}

		protected override void RegisterCallbacksOnTarget()
		{
			target.RegisterCallback<ContextClickEvent>(OnContextClick);
		}

		protected override void UnregisterCallbacksFromTarget()
		{
			target.UnregisterCallback<ContextClickEvent>(OnContextClick);
		}

		private void OnContextClick(ContextClickEvent evt)
		{
			onContextClick?.Invoke(evt);
		}
	}
}
