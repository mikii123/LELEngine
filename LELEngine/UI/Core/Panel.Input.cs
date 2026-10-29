using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.UIElements
{
	public abstract partial class BaseVisualElementPanel
	{
		#region PrivateFields

		private Vector2 lastPointerPosition = new Vector2(-1f, -1f);
		private int lastPressedButtons;
		private readonly VisualElement[] pointerDownTargets = new VisualElement[3];
		private long lastClickTime;
		private Vector2 lastClickPosition;
		private int clickCount;
		private int lastClickButton = -1;
		private static readonly List<VisualElement> chainBuffer = new List<VisualElement>(32);
		private static readonly List<VisualElement> chainBuffer2 = new List<VisualElement>(32);

		#endregion

		#region PublicMethods

		/// <summary>True when the point is over an element that accepts picking (excluding the root itself).</summary>
		public bool HitTest(Vector2 panelPosition)
		{
			VisualElement hit = Pick(panelPosition);
			return hit != null && hit != visualTree;
		}

		/// <summary>The pointer moved (or buttons changed without moving). Updates hover and sends move events.</summary>
		public void ProcessPointerMove(Vector2 position, int pressedButtons, EventModifiers modifiers)
		{
			Vector2 delta = lastPointerPosition.X < 0f ? Vector2.Zero : position - lastPointerPosition;
			lastPointerPosition = position;
			lastPressedButtons = pressedButtons;

			VisualElement captured = pointerCapture.GetCapturing(PointerId.mousePointerId);
			VisualElement under = Pick(position);
			UpdateHover(under, position, pressedButtons, modifiers);

			VisualElement target = captured ?? under;
			if (target == null)
			{
				return;
			}

			using (PointerMoveEvent evt = PointerMoveEvent.GetPooled())
			{
				Fill(evt, position, -1, pressedButtons, modifiers, 0, delta);
				evt.target = target;
				target.SendEvent(evt);
			}

			using (MouseMoveEvent evt = MouseMoveEvent.GetPooled())
			{
				FillMouse(evt, position, -1, pressedButtons, modifiers, 0, delta);
				evt.target = target;
				target.SendEvent(evt);
			}
		}

		public void ProcessPointerDown(Vector2 position, int button, int pressedButtons, EventModifiers modifiers)
		{
			lastPointerPosition = position;
			lastPressedButtons = pressedButtons;
			VisualElement captured = pointerCapture.GetCapturing(PointerId.mousePointerId);
			VisualElement under = Pick(position);
			UpdateHover(under, position, pressedButtons, modifiers);
			VisualElement target = captured ?? under;
			if (target == null)
			{
				return;
			}

			long now = timeMs;
			if (button == lastClickButton && now - lastClickTime < 500 && (position - lastClickPosition).LengthSquared < 25f)
			{
				clickCount++;
			}
			else
			{
				clickCount = 1;
			}

			lastClickTime = now;
			lastClickPosition = position;
			lastClickButton = button;
			if (button >= 0 && button < pointerDownTargets.Length)
			{
				pointerDownTargets[button] = target;
			}

			bool prevented;
			using (PointerDownEvent evt = PointerDownEvent.GetPooled())
			{
				Fill(evt, position, button, pressedButtons, modifiers, clickCount, Vector2.Zero);
				evt.target = target;
				target.SendEvent(evt);
				prevented = evt.isDefaultPrevented;
			}

			using (MouseDownEvent evt = MouseDownEvent.GetPooled())
			{
				FillMouse(evt, position, button, pressedButtons, modifiers, clickCount, Vector2.Zero);
				evt.target = target;
				target.SendEvent(evt);
			}

			if (!prevented)
			{
				focusController.OnPointerDown(target);
			}
		}

		public void ProcessPointerUp(Vector2 position, int button, int pressedButtons, EventModifiers modifiers)
		{
			lastPointerPosition = position;
			lastPressedButtons = pressedButtons;
			VisualElement captured = pointerCapture.GetCapturing(PointerId.mousePointerId);
			VisualElement under = Pick(position);
			VisualElement target = captured ?? under;
			VisualElement downTarget = button >= 0 && button < pointerDownTargets.Length ? pointerDownTargets[button] : null;
			if (button >= 0 && button < pointerDownTargets.Length)
			{
				pointerDownTargets[button] = null;
			}

			if (target != null)
			{
				using (PointerUpEvent evt = PointerUpEvent.GetPooled())
				{
					Fill(evt, position, button, pressedButtons, modifiers, clickCount, Vector2.Zero);
					evt.target = target;
					target.SendEvent(evt);
				}

				using (MouseUpEvent evt = MouseUpEvent.GetPooled())
				{
					FillMouse(evt, position, button, pressedButtons, modifiers, clickCount, Vector2.Zero);
					evt.target = target;
					target.SendEvent(evt);
				}
			}

			// Click: press and release on the same element (or a common ancestor of both).
			VisualElement clickTarget = CommonAncestor(downTarget, under ?? downTarget);
			if (clickTarget != null && clickTarget.elementPanel == this && (under == downTarget || under != null && under.worldBound.Contains(position)))
			{
				if (button == 0)
				{
					using (ClickEvent evt = ClickEvent.GetPooled())
					{
						Fill(evt, position, button, pressedButtons, modifiers, clickCount, Vector2.Zero);
						evt.target = clickTarget;
						clickTarget.SendEvent(evt);
					}
				}
				else if (button == 1)
				{
					using (ContextClickEvent evt = ContextClickEvent.GetPooled())
					{
						Fill(evt, position, button, pressedButtons, modifiers, clickCount, Vector2.Zero);
						evt.target = clickTarget;
						clickTarget.SendEvent(evt);
					}
				}
			}

			UpdateHover(under, position, pressedButtons, modifiers);
		}

		public void ProcessWheel(Vector2 position, Vector2 delta, EventModifiers modifiers)
		{
			VisualElement captured = pointerCapture.GetCapturing(PointerId.mousePointerId);
			VisualElement target = captured ?? Pick(position);
			if (target == null)
			{
				return;
			}

			using (WheelEvent evt = WheelEvent.GetPooled())
			{
				Fill(evt, position, -1, lastPressedButtons, modifiers, 0, Vector2.Zero);
				// Unity: positive y scrolls down; OpenTK gives positive y for wheel up.
				evt.delta = new Vector3(-delta.X, -delta.Y, 0f);
				evt.target = target;
				target.SendEvent(evt);
			}
		}

		/// <summary>The pointer left the panel's window or view: no element is hovered any more.</summary>
		public void ProcessPointerLeave(EventModifiers modifiers)
		{
			UpdateHover(null, lastPointerPosition, lastPressedButtons, modifiers);
			lastPointerPosition = new Vector2(-1f, -1f);
		}

		public void ProcessKeyDown(Keys key, EventModifiers modifiers)
		{
			VisualElement target = focusController.focusedElement as VisualElement ?? visualTree;
			bool handled;
			using (KeyDownEvent evt = KeyDownEvent.GetPooled('\0', key, modifiers))
			{
				evt.target = target;
				target.SendEvent(evt);
				handled = evt.isDefaultPrevented || evt.isPropagationStopped;
			}

			if (handled)
			{
				return;
			}

			NavigationMoveEvent.Direction direction = NavigationMoveEvent.Direction.None;
			switch (key)
			{
				case Keys.Tab: direction = (modifiers & EventModifiers.Shift) != 0 ? NavigationMoveEvent.Direction.Previous : NavigationMoveEvent.Direction.Next; break;
				case Keys.Left: direction = NavigationMoveEvent.Direction.Left; break;
				case Keys.Right: direction = NavigationMoveEvent.Direction.Right; break;
				case Keys.Up: direction = NavigationMoveEvent.Direction.Up; break;
				case Keys.Down: direction = NavigationMoveEvent.Direction.Down; break;
				case Keys.Enter:
				case Keys.KeyPadEnter:
				case Keys.Space:
					SendNavigationSubmit(modifiers);
					return;
				case Keys.Escape:
					SendNavigationCancel(modifiers);
					return;
			}

			if (direction != NavigationMoveEvent.Direction.None)
			{
				SendNavigationMove(direction, modifiers);
			}
		}

		public void SendNavigationMove(NavigationMoveEvent.Direction direction)
		{
			SendNavigationMove(direction, EventModifiers.None);
		}

		public void SendNavigationSubmit()
		{
			SendNavigationSubmit(EventModifiers.None);
		}

		public void SendNavigationCancel()
		{
			SendNavigationCancel(EventModifiers.None);
		}

		/// <summary>A navigation move from any device (keyboard arrows, a gamepad): the focused element gets it first.</summary>
		public void SendNavigationMove(NavigationMoveEvent.Direction direction, EventModifiers modifiers)
		{
			if (direction == NavigationMoveEvent.Direction.None)
			{
				return;
			}

			VisualElement target = focusController.focusedElement as VisualElement ?? visualTree;
			using (NavigationMoveEvent evt = NavigationMoveEvent.GetPooled(direction, modifiers))
			{
				evt.target = target;
				target.SendEvent(evt);
			}
		}

		public void SendNavigationSubmit(EventModifiers modifiers)
		{
			VisualElement target = focusController.focusedElement as VisualElement ?? visualTree;
			using (NavigationSubmitEvent evt = NavigationSubmitEvent.GetPooled())
			{
				evt.modifiers = modifiers;
				evt.target = target;
				target.SendEvent(evt);
			}
		}

		public void SendNavigationCancel(EventModifiers modifiers)
		{
			VisualElement target = focusController.focusedElement as VisualElement ?? visualTree;
			using (NavigationCancelEvent evt = NavigationCancelEvent.GetPooled())
			{
				evt.modifiers = modifiers;
				evt.target = target;
				target.SendEvent(evt);
			}
		}

		public void ProcessKeyUp(Keys key, EventModifiers modifiers)
		{
			VisualElement target = focusController.focusedElement as VisualElement ?? visualTree;
			using (KeyUpEvent evt = KeyUpEvent.GetPooled('\0', key, modifiers))
			{
				evt.target = target;
				target.SendEvent(evt);
			}
		}

		/// <summary>A typed character (text input): sent as a KeyDownEvent with the character and no key code.</summary>
		public void ProcessTextInput(char character, EventModifiers modifiers)
		{
			VisualElement target = focusController.focusedElement as VisualElement;
			if (target == null)
			{
				return;
			}

			using (KeyDownEvent evt = KeyDownEvent.GetPooled(character, Keys.Unknown, modifiers))
			{
				evt.target = target;
				target.SendEvent(evt);
			}
		}

		#endregion

		#region PrivateMethods

		private void UpdateHover(VisualElement newTop, Vector2 position, int pressedButtons, EventModifiers modifiers)
		{
			VisualElement oldTop = hoveredElement;
			if (newTop == oldTop)
			{
				return;
			}

			hoveredElement = newTop;
			topElementUnderPointer = newTop;

			// Chains root -> element; the common part keeps its hover state.
			chainBuffer.Clear();
			chainBuffer2.Clear();
			for (VisualElement e = oldTop; e != null; e = e.m_PhysicalParent) chainBuffer.Add(e);
			for (VisualElement e = newTop; e != null; e = e.m_PhysicalParent) chainBuffer2.Add(e);
			chainBuffer.Reverse();
			chainBuffer2.Reverse();
			int common = 0;
			while (common < chainBuffer.Count && common < chainBuffer2.Count && chainBuffer[common] == chainBuffer2[common])
			{
				common++;
			}

			if (oldTop != null && oldTop.elementPanel == this)
			{
				using (PointerOutEvent evt = PointerOutEvent.GetPooled())
				{
					Fill(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = oldTop;
					oldTop.SendEvent(evt);
				}

				using (MouseOutEvent evt = MouseOutEvent.GetPooled())
				{
					FillMouse(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = oldTop;
					oldTop.SendEvent(evt);
				}
			}

			for (int i = chainBuffer.Count - 1; i >= common; i--)
			{
				VisualElement e = chainBuffer[i];
				if (e.elementPanel != this)
				{
					continue;
				}

				e.pseudoStates &= ~PseudoStates.Hover;
				using (PointerLeaveEvent evt = PointerLeaveEvent.GetPooled())
				{
					Fill(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = e;
					e.SendEvent(evt);
				}

				using (MouseLeaveEvent evt = MouseLeaveEvent.GetPooled())
				{
					FillMouse(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = e;
					e.SendEvent(evt);
				}
			}

			if (newTop != null)
			{
				using (PointerOverEvent evt = PointerOverEvent.GetPooled())
				{
					Fill(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = newTop;
					newTop.SendEvent(evt);
				}

				using (MouseOverEvent evt = MouseOverEvent.GetPooled())
				{
					FillMouse(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = newTop;
					newTop.SendEvent(evt);
				}
			}

			for (int i = common; i < chainBuffer2.Count; i++)
			{
				VisualElement e = chainBuffer2[i];
				if (e.enabledInHierarchy)
				{
					e.pseudoStates |= PseudoStates.Hover;
				}

				using (PointerEnterEvent evt = PointerEnterEvent.GetPooled())
				{
					Fill(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = e;
					e.SendEvent(evt);
				}

				using (MouseEnterEvent evt = MouseEnterEvent.GetPooled())
				{
					FillMouse(evt, position, -1, pressedButtons, modifiers, 0, Vector2.Zero);
					evt.target = e;
					e.SendEvent(evt);
				}
			}

			// Shared by every panel: emptied, so they do not keep this panel's elements alive.
			chainBuffer.Clear();
			chainBuffer2.Clear();
			cursor = newTop != null ? newTop.computedStyle.inherited.cursor.type : CursorType.Arrow;
		}

		private static void Fill<T>(PointerEventBase<T> evt, Vector2 position, int button, int pressedButtons, EventModifiers modifiers, int clicks, Vector2 delta)
			where T : PointerEventBase<T>, new()
		{
			evt.pointerId = PointerId.mousePointerId;
			evt.pointerType = PointerType.mouse;
			evt.isPrimary = true;
			evt.position = position;
			evt.button = button;
			evt.pressedButtons = pressedButtons;
			evt.modifiers = modifiers;
			evt.clickCount = clicks;
			evt.deltaPosition = delta;
			evt.pressure = pressedButtons != 0 ? 1f : 0f;
		}

		private static void FillMouse<T>(MouseEventBase<T> evt, Vector2 position, int button, int pressedButtons, EventModifiers modifiers, int clicks, Vector2 delta)
			where T : MouseEventBase<T>, new()
		{
			evt.mousePosition = position;
			evt.button = Math.Max(0, button);
			evt.pressedButtons = pressedButtons;
			evt.modifiers = modifiers;
			evt.clickCount = clicks;
			evt.mouseDelta = delta;
		}

		private static VisualElement CommonAncestor(VisualElement a, VisualElement b)
		{
			if (a == null || b == null)
			{
				return null;
			}

			if (a == b)
			{
				return a;
			}

			for (VisualElement x = a; x != null; x = x.m_PhysicalParent)
			{
				for (VisualElement y = b; y != null; y = y.m_PhysicalParent)
				{
					if (x == y)
					{
						return x;
					}
				}
			}

			return null;
		}

		#endregion
	}
}
