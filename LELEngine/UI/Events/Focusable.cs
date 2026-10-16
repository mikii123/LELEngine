using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>An event handler that can take the keyboard focus (elements; the focus ring orders them by tabIndex).</summary>
	public abstract class Focusable : CallbackEventHandler
	{
		#region PublicFields

		public abstract FocusController focusController { get; }

		/// <summary>The element can receive focus (keyboard events) by clicking or tab navigation.</summary>
		public bool focusable { get; set; }

		/// <summary>Order in the focus ring: higher values come later; negative values are excluded from tab navigation.</summary>
		public int tabIndex { get; set; }

		/// <summary>Focusing this element focuses its first focusable descendant instead (composite controls).</summary>
		public bool delegatesFocus { get; set; }

		/// <summary>True while an ancestor hands focus to a descendant through <see cref="delegatesFocus" />.</summary>
		internal bool isFocusDelegated;

		public virtual bool canGrabFocus => focusable;

		#endregion

		#region PublicMethods

		public virtual void Focus()
		{
			FocusController controller = focusController;
			if (controller == null)
			{
				return;
			}

			if (canGrabFocus)
			{
				Focusable target = delegatesFocus ? GetFocusDelegate() ?? this : this;
				controller.SwitchFocus(target, FocusChangeDirection.Unspecified);
			}
		}

		public virtual void Blur()
		{
			FocusController controller = focusController;
			if (controller != null && controller.focusedElement == this)
			{
				controller.SwitchFocus(null, FocusChangeDirection.Unspecified);
			}
		}

		#endregion

		#region ProtectedMethods

		/// <summary>The descendant that takes focus for this element when it delegates focus.</summary>
		protected virtual Focusable GetFocusDelegate()
		{
			return null;
		}

		#endregion
	}

	/// <summary>
	///     Keeps the panel's focused element, moves focus between elements (clicks, Tab / Shift+Tab, arrows) and sends
	///     the focus events. One per panel.
	/// </summary>
	public sealed class FocusController
	{
		#region PublicFields

		public Focusable focusedElement { get; private set; }

		#endregion

		#region PrivateFields

		private readonly BaseVisualElementPanel panel;
		private readonly List<VisualElement> ring = new List<VisualElement>();

		#endregion

		#region Constructors

		internal FocusController(BaseVisualElementPanel panel)
		{
			this.panel = panel;
		}

		#endregion

		#region PublicMethods

		public void SwitchFocus(Focusable newFocus, FocusChangeDirection direction)
		{
			if (newFocus == focusedElement)
			{
				return;
			}

			Focusable oldFocus = focusedElement;
			focusedElement = newFocus;

			if (oldFocus is VisualElement oldElement)
			{
				oldElement.pseudoStates &= ~PseudoStates.Focus;
				using (FocusOutEvent evt = FocusOutEvent.GetPooled(oldElement, newFocus, direction))
				{
					oldElement.SendEvent(evt);
				}

				using (BlurEvent evt = BlurEvent.GetPooled(oldElement, newFocus, direction))
				{
					oldElement.SendEvent(evt);
				}
			}

			if (newFocus is VisualElement newElement)
			{
				newElement.pseudoStates |= PseudoStates.Focus;
				using (FocusInEvent evt = FocusInEvent.GetPooled(newElement, oldFocus, direction))
				{
					newElement.SendEvent(evt);
				}

				using (FocusEvent evt = FocusEvent.GetPooled(newElement, oldFocus, direction))
				{
					newElement.SendEvent(evt);
				}
			}
		}

		/// <summary>Moves focus to the next / previous focusable element in the ring (tab navigation).</summary>
		public void MoveFocus(FocusChangeDirection direction)
		{
			BuildRing();
			if (ring.Count == 0)
			{
				return;
			}

			int index = focusedElement is VisualElement current ? ring.IndexOf(current) : -1;
			VisualElement next;
			if (direction == FocusChangeDirection.Previous)
			{
				next = ring[index <= 0 ? ring.Count - 1 : index - 1];
			}
			else
			{
				next = ring[index < 0 || index >= ring.Count - 1 ? 0 : index + 1];
			}

			SwitchFocus(next, direction);
		}

		/// <summary>The focused element left the tree or became unfocusable: focus nothing.</summary>
		internal void ReleaseFocus(Focusable element)
		{
			if (focusedElement == element)
			{
				SwitchFocus(null, FocusChangeDirection.Unspecified);
			}
		}

		/// <summary>Focus the clicked element (or its nearest focusable ancestor); nothing clears focus.</summary>
		internal void OnPointerDown(VisualElement target)
		{
			VisualElement candidate = target;
			while (candidate != null && !(candidate.canGrabFocus && candidate.focusable))
			{
				candidate = candidate.hierarchy.parent;
			}

			if (candidate != null)
			{
				candidate.Focus();
			}
			else
			{
				SwitchFocus(null, FocusChangeDirection.Unspecified);
			}
		}

		#endregion

		#region PrivateMethods

		private void BuildRing()
		{
			ring.Clear();
			Collect(panel.visualTree);
			// Stable sort by tabIndex keeps the tree order for equal indices.
			var indexed = new List<KeyValuePair<int, VisualElement>>(ring.Count);
			for (int i = 0; i < ring.Count; i++)
			{
				indexed.Add(new KeyValuePair<int, VisualElement>(i, ring[i]));
			}

			indexed.Sort((a, b) =>
			{
				int byTab = a.Value.tabIndex.CompareTo(b.Value.tabIndex);
				return byTab != 0 ? byTab : a.Key.CompareTo(b.Key);
			});

			ring.Clear();
			foreach (KeyValuePair<int, VisualElement> pair in indexed)
			{
				ring.Add(pair.Value);
			}
		}

		private void Collect(VisualElement element)
		{
			if (element.resolvedStyleDisplayNone || !element.enabledInHierarchy)
			{
				return;
			}

			if (element.focusable && element.canGrabFocus && element.tabIndex >= 0 && !element.delegatesFocus)
			{
				ring.Add(element);
			}

			int count = element.hierarchy.childCount;
			for (int i = 0; i < count; i++)
			{
				Collect(element.hierarchy[i]);
			}
		}

		#endregion
	}
}
