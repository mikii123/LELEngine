using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Propagates an event along the path from the panel root to its target: trickle-down callbacks on the
	///     ancestors, the target's callbacks and default action, bubble-up callbacks on the ancestors, then the
	///     target's final default action. Pointer events skip disabled elements' callbacks.
	/// </summary>
	public static class EventDispatcher
	{
		#region PrivateFields

		[ThreadStatic] private static List<VisualElement> pathBuffer;
		[ThreadStatic] private static int dispatchDepth;

		#endregion

		#region PublicMethods

		public static void Dispatch(EventBase evt, BaseVisualElementPanel panel)
		{
			if (evt == null)
			{
				return;
			}

			IEventHandler handler = evt.target;
			if (handler == null)
			{
				return;
			}

			var target = handler as VisualElement;
			if (target == null)
			{
				evt.dispatched = true;
				try
				{
					evt.currentTarget = handler;
					evt.propagationPhase = PropagationPhase.AtTarget;
					handler.HandleEvent(evt);
					evt.propagationPhase = PropagationPhase.DefaultAction;
					handler.HandleEvent(evt);
				}
				finally
				{
					evt.dispatched = false;
					evt.propagationPhase = PropagationPhase.None;
					evt.currentTarget = null;
				}

				return;
			}

			// Nested dispatches (an event callback sending another event) need their own path list.
			List<VisualElement> path = dispatchDepth == 0 ? (pathBuffer ??= new List<VisualElement>(32)) : new List<VisualElement>(16);
			dispatchDepth++;
			evt.dispatched = true;
			try
			{
				path.Clear();
				for (VisualElement e = target.m_PhysicalParent; e != null; e = e.m_PhysicalParent)
				{
					path.Add(e);
				}

				bool skipDisabled = evt.skipDisabledElements && IsPointerLike(evt);

				// Trickle down: root first.
				if (evt.tricklesDown)
				{
					for (int i = path.Count - 1; i >= 0 && !evt.isPropagationStopped; i--)
					{
						VisualElement element = path[i];
						if (skipDisabled && !element.enabledInHierarchy)
						{
							continue;
						}

						Invoke(evt, element, PropagationPhase.TrickleDown);
					}
				}

				bool targetDisabled = skipDisabled && !target.enabledInHierarchy;
				if (!evt.isPropagationStopped && !targetDisabled)
				{
					Invoke(evt, target, PropagationPhase.AtTarget);
				}

				if (!targetDisabled)
				{
					Invoke(evt, target, PropagationPhase.DefaultActionAtTarget);
				}

				if (evt.bubbles)
				{
					for (int i = 0; i < path.Count && !evt.isPropagationStopped; i++)
					{
						VisualElement element = path[i];
						if (skipDisabled && !element.enabledInHierarchy)
						{
							continue;
						}

						Invoke(evt, element, PropagationPhase.BubbleUp);
					}
				}

				if (!targetDisabled)
				{
					Invoke(evt, target, PropagationPhase.DefaultAction);
				}
			}
			finally
			{
				evt.dispatched = false;
				evt.propagationPhase = PropagationPhase.None;
				evt.currentTarget = null;
				dispatchDepth--;
				path.Clear();
			}
		}

		#endregion

		#region PrivateMethods

		private static void Invoke(EventBase evt, VisualElement element, PropagationPhase phase)
		{
			evt.currentTarget = element;
			evt.propagationPhase = phase;
			switch (evt)
			{
				case IPointerEvent _:
					SetPointerLocalPosition(evt, element);
					break;
				case IMouseEvent _:
					SetMouseLocalPosition(evt, element);
					break;
			}

			try
			{
				element.HandleEvent(evt);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
		}

		private static bool IsPointerLike(EventBase evt)
		{
			return evt is IPointerEvent || evt is IMouseEvent;
		}

		private static void SetPointerLocalPosition(EventBase evt, VisualElement element)
		{
			switch (evt)
			{
				case PointerDownEvent e: e.SetLocalPosition(element); break;
				case PointerUpEvent e: e.SetLocalPosition(element); break;
				case PointerMoveEvent e: e.SetLocalPosition(element); break;
				case PointerEnterEvent e: e.SetLocalPosition(element); break;
				case PointerLeaveEvent e: e.SetLocalPosition(element); break;
				case PointerOverEvent e: e.SetLocalPosition(element); break;
				case PointerOutEvent e: e.SetLocalPosition(element); break;
				case PointerCancelEvent e: e.SetLocalPosition(element); break;
				case ClickEvent e: e.SetLocalPosition(element); break;
				case ContextClickEvent e: e.SetLocalPosition(element); break;
				case WheelEvent e: e.SetLocalPosition(element); break;
				case PointerStationaryEvent e: e.SetLocalPosition(element); break;
			}
		}

		private static void SetMouseLocalPosition(EventBase evt, VisualElement element)
		{
			switch (evt)
			{
				case MouseDownEvent e: e.SetLocalPosition(element); break;
				case MouseUpEvent e: e.SetLocalPosition(element); break;
				case MouseMoveEvent e: e.SetLocalPosition(element); break;
				case MouseEnterEvent e: e.SetLocalPosition(element); break;
				case MouseLeaveEvent e: e.SetLocalPosition(element); break;
				case MouseOverEvent e: e.SetLocalPosition(element); break;
				case MouseOutEvent e: e.SetLocalPosition(element); break;
			}
		}

		#endregion
	}
}
