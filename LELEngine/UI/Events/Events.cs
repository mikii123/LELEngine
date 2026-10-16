using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.UIElements
{
	// ------------------------------------------------------------------ pointer and mouse

	public static class PointerId
	{
		public const int maxPointers = 32;
		public const int invalidPointerId = -1;
		public const int mousePointerId = 0;
		public const int touchPointerIdBase = 1;
		public const int touchPointerCount = 20;
		public const int penPointerIdBase = touchPointerIdBase + touchPointerCount;
	}

	public static class PointerType
	{
		public const string mouse = "mouse";
		public const string touch = "touch";
		public const string pen = "pen";
		public const string unknown = "";
	}

	/// <summary>Common data of pointer events (mouse, touch, pen).</summary>
	public interface IPointerEvent
	{
		int pointerId { get; }
		string pointerType { get; }
		bool isPrimary { get; }

		/// <summary>Button of the event: 0 left, 1 right, 2 middle, -1 none (moves).</summary>
		int button { get; }

		/// <summary>Buttons held down: bit 0 left, bit 1 right, bit 2 middle.</summary>
		int pressedButtons { get; }

		/// <summary>Position in panel coordinates.</summary>
		Vector2 position { get; }

		/// <summary>Position relative to the element whose callbacks run (<see cref="EventBase.currentTarget" />).</summary>
		Vector2 localPosition { get; }

		/// <summary>Movement since the previous pointer event, in panel coordinates.</summary>
		Vector2 deltaPosition { get; }

		float deltaTime { get; }
		int clickCount { get; }
		float pressure { get; }
		EventModifiers modifiers { get; }
		bool shiftKey { get; }
		bool ctrlKey { get; }
		bool commandKey { get; }
		bool altKey { get; }
		bool actionKey { get; }
	}

	public abstract class PointerEventBase<T> : EventBase<T>, IPointerEvent
		where T : PointerEventBase<T>, new()
	{
		public int pointerId { get; set; }
		public string pointerType { get; set; }
		public bool isPrimary { get; set; }
		public int button { get; set; }
		public int pressedButtons { get; set; }
		public Vector2 position { get; set; }
		public Vector2 localPosition { get; internal set; }
		public Vector2 deltaPosition { get; set; }
		public float deltaTime { get; set; }
		public int clickCount { get; set; }
		public float pressure { get; set; }
		public EventModifiers modifiers { get; set; }
		public bool shiftKey => (modifiers & EventModifiers.Shift) != 0;
		public bool ctrlKey => (modifiers & EventModifiers.Control) != 0;
		public bool commandKey => (modifiers & EventModifiers.Command) != 0;
		public bool altKey => (modifiers & EventModifiers.Alt) != 0;
		public bool actionKey => ctrlKey || commandKey;

		protected override void Init()
		{
			base.Init();
			pointerId = PointerId.mousePointerId;
			pointerType = PointerType.mouse;
			isPrimary = true;
			button = -1;
			pressedButtons = 0;
			position = Vector2.Zero;
			localPosition = Vector2.Zero;
			deltaPosition = Vector2.Zero;
			deltaTime = 0f;
			clickCount = 0;
			pressure = 0f;
			modifiers = EventModifiers.None;
		}

		/// <summary>Copies the pointer data of another pointer event (derived events such as ClickEvent).</summary>
		public void CopyFrom(IPointerEvent other)
		{
			pointerId = other.pointerId;
			pointerType = other.pointerType;
			isPrimary = other.isPrimary;
			button = other.button;
			pressedButtons = other.pressedButtons;
			position = other.position;
			localPosition = other.localPosition;
			deltaPosition = other.deltaPosition;
			deltaTime = other.deltaTime;
			clickCount = other.clickCount;
			pressure = other.pressure;
			modifiers = other.modifiers;
		}

		internal void SetLocalPosition(VisualElement element)
		{
			localPosition = element != null ? element.WorldToLocal(position) : position;
		}
	}

	public sealed class PointerDownEvent : PointerEventBase<PointerDownEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class PointerUpEvent : PointerEventBase<PointerUpEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class PointerMoveEvent : PointerEventBase<PointerMoveEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	/// <summary>The pointer entered an element or one of its descendants (does not bubble).</summary>
	public sealed class PointerEnterEvent : PointerEventBase<PointerEnterEvent>
	{
		protected override void Init()
		{
			base.Init();
			tricklesDown = true;
		}
	}

	/// <summary>The pointer left an element and all of its descendants (does not bubble).</summary>
	public sealed class PointerLeaveEvent : PointerEventBase<PointerLeaveEvent>
	{
		protected override void Init()
		{
			base.Init();
			tricklesDown = true;
		}
	}

	public sealed class PointerOverEvent : PointerEventBase<PointerOverEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class PointerOutEvent : PointerEventBase<PointerOutEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class PointerCancelEvent : PointerEventBase<PointerCancelEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class PointerStationaryEvent : PointerEventBase<PointerStationaryEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	/// <summary>A pointer down followed by a pointer up over the same element.</summary>
	public sealed class ClickEvent : PointerEventBase<ClickEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class ContextClickEvent : PointerEventBase<ContextClickEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class PointerCaptureEvent : EventBase<PointerCaptureEvent>
	{
		public IEventHandler relatedTarget { get; internal set; }
		public int pointerId { get; internal set; }

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			relatedTarget = null;
			pointerId = PointerId.invalidPointerId;
		}
	}

	public sealed class PointerCaptureOutEvent : EventBase<PointerCaptureOutEvent>
	{
		public IEventHandler relatedTarget { get; internal set; }
		public int pointerId { get; internal set; }

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			relatedTarget = null;
			pointerId = PointerId.invalidPointerId;
		}
	}

	/// <summary>Mouse wheel. <see cref="delta" />: y is the vertical scroll (positive = down in Unity convention).</summary>
	public sealed class WheelEvent : PointerEventBase<WheelEvent>
	{
		public Vector3 delta { get; set; }
		public Vector2 mousePosition => position;

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			delta = Vector3.Zero;
		}
	}

	/// <summary>Mouse events are sent alongside pointer events for code written against the mouse API.</summary>
	public interface IMouseEvent
	{
		EventModifiers modifiers { get; }
		Vector2 mousePosition { get; }
		Vector2 localMousePosition { get; }
		Vector2 mouseDelta { get; }
		int clickCount { get; }
		int button { get; }
		int pressedButtons { get; }
		bool shiftKey { get; }
		bool ctrlKey { get; }
		bool commandKey { get; }
		bool altKey { get; }
		bool actionKey { get; }
	}

	public abstract class MouseEventBase<T> : EventBase<T>, IMouseEvent
		where T : MouseEventBase<T>, new()
	{
		public EventModifiers modifiers { get; set; }
		public Vector2 mousePosition { get; set; }
		public Vector2 localMousePosition { get; internal set; }
		public Vector2 mouseDelta { get; set; }
		public int clickCount { get; set; }
		public int button { get; set; }
		public int pressedButtons { get; set; }
		public bool shiftKey => (modifiers & EventModifiers.Shift) != 0;
		public bool ctrlKey => (modifiers & EventModifiers.Control) != 0;
		public bool commandKey => (modifiers & EventModifiers.Command) != 0;
		public bool altKey => (modifiers & EventModifiers.Alt) != 0;
		public bool actionKey => ctrlKey || commandKey;

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			modifiers = EventModifiers.None;
			mousePosition = Vector2.Zero;
			localMousePosition = Vector2.Zero;
			mouseDelta = Vector2.Zero;
			clickCount = 0;
			button = 0;
			pressedButtons = 0;
		}

		public void CopyFrom(IPointerEvent pointer)
		{
			modifiers = pointer.modifiers;
			mousePosition = pointer.position;
			localMousePosition = pointer.localPosition;
			mouseDelta = pointer.deltaPosition;
			clickCount = pointer.clickCount;
			button = pointer.button;
			pressedButtons = pointer.pressedButtons;
		}

		internal void SetLocalPosition(VisualElement element)
		{
			localMousePosition = element != null ? element.WorldToLocal(mousePosition) : mousePosition;
		}
	}

	public sealed class MouseDownEvent : MouseEventBase<MouseDownEvent>
	{ }

	public sealed class MouseUpEvent : MouseEventBase<MouseUpEvent>
	{ }

	public sealed class MouseMoveEvent : MouseEventBase<MouseMoveEvent>
	{ }

	public sealed class MouseEnterEvent : MouseEventBase<MouseEnterEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = false;
		}
	}

	public sealed class MouseLeaveEvent : MouseEventBase<MouseLeaveEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = false;
		}
	}

	public sealed class MouseOverEvent : MouseEventBase<MouseOverEvent>
	{ }

	public sealed class MouseOutEvent : MouseEventBase<MouseOutEvent>
	{ }

	public sealed class MouseEnterWindowEvent : MouseEventBase<MouseEnterWindowEvent>
	{ }

	public sealed class MouseLeaveWindowEvent : MouseEventBase<MouseLeaveWindowEvent>
	{ }

	// ------------------------------------------------------------------ keyboard

	public interface IKeyboardEvent
	{
		EventModifiers modifiers { get; }
		char character { get; }
		Keys keyCode { get; }
		bool shiftKey { get; }
		bool ctrlKey { get; }
		bool commandKey { get; }
		bool altKey { get; }
		bool actionKey { get; }
	}

	public abstract class KeyboardEventBase<T> : EventBase<T>, IKeyboardEvent
		where T : KeyboardEventBase<T>, new()
	{
		public EventModifiers modifiers { get; set; }

		/// <summary>The typed character ('\0' for keys without one).</summary>
		public char character { get; set; }

		public Keys keyCode { get; set; }
		public bool shiftKey => (modifiers & EventModifiers.Shift) != 0;
		public bool ctrlKey => (modifiers & EventModifiers.Control) != 0;
		public bool commandKey => (modifiers & EventModifiers.Command) != 0;
		public bool altKey => (modifiers & EventModifiers.Alt) != 0;
		public bool actionKey => ctrlKey || commandKey;

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			modifiers = EventModifiers.None;
			character = '\0';
			keyCode = Keys.Unknown;
		}

		public static T GetPooled(char character, Keys keyCode, EventModifiers modifiers)
		{
			T evt = GetPooled();
			evt.character = character;
			evt.keyCode = keyCode;
			evt.modifiers = modifiers;
			return evt;
		}
	}

	public sealed class KeyDownEvent : KeyboardEventBase<KeyDownEvent>
	{ }

	public sealed class KeyUpEvent : KeyboardEventBase<KeyUpEvent>
	{ }

	// ------------------------------------------------------------------ navigation

	public enum NavigationDeviceType
	{
		Unknown,
		Keyboard,
		NonKeyboard
	}

	public abstract class NavigationEventBase<T> : EventBase<T>
		where T : NavigationEventBase<T>, new()
	{
		public EventModifiers modifiers { get; set; }
		public NavigationDeviceType deviceType { get; set; }
		public bool shiftKey => (modifiers & EventModifiers.Shift) != 0;
		public bool ctrlKey => (modifiers & EventModifiers.Control) != 0;
		public bool altKey => (modifiers & EventModifiers.Alt) != 0;

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			modifiers = EventModifiers.None;
			deviceType = NavigationDeviceType.Keyboard;
		}
	}

	public sealed class NavigationMoveEvent : NavigationEventBase<NavigationMoveEvent>
	{
		public enum Direction
		{
			None,
			Left,
			Up,
			Right,
			Down,
			Next,
			Previous
		}

		public Direction direction { get; set; }
		public Vector2 move { get; set; }

		protected override void Init()
		{
			base.Init();
			direction = Direction.None;
			move = Vector2.Zero;
		}

		public static NavigationMoveEvent GetPooled(Direction direction, EventModifiers modifiers = EventModifiers.None)
		{
			NavigationMoveEvent evt = GetPooled();
			evt.direction = direction;
			evt.modifiers = modifiers;
			return evt;
		}
	}

	public sealed class NavigationSubmitEvent : NavigationEventBase<NavigationSubmitEvent>
	{ }

	public sealed class NavigationCancelEvent : NavigationEventBase<NavigationCancelEvent>
	{ }

	// ------------------------------------------------------------------ focus

	public enum FocusChangeDirection
	{
		Unspecified = -1,
		None = 0,
		Next = 1,
		Previous = 2
	}

	public abstract class FocusEventBase<T> : EventBase<T>
		where T : FocusEventBase<T>, new()
	{
		/// <summary>The other element of the focus change (losing or gaining it).</summary>
		public Focusable relatedTarget { get; internal set; }

		public FocusChangeDirection direction { get; internal set; }

		protected override void Init()
		{
			base.Init();
			relatedTarget = null;
			direction = FocusChangeDirection.Unspecified;
		}

		public static T GetPooled(IEventHandler target, Focusable relatedTarget, FocusChangeDirection direction)
		{
			T evt = GetPooled();
			evt.target = target;
			evt.relatedTarget = relatedTarget;
			evt.direction = direction;
			return evt;
		}
	}

	/// <summary>Sent to the element gaining focus and its ancestors (bubbles) before <see cref="FocusEvent" />.</summary>
	public sealed class FocusInEvent : FocusEventBase<FocusInEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class FocusOutEvent : FocusEventBase<FocusOutEvent>
	{
		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
		}
	}

	public sealed class FocusEvent : FocusEventBase<FocusEvent>
	{ }

	public sealed class BlurEvent : FocusEventBase<BlurEvent>
	{ }

	// ------------------------------------------------------------------ value changes

	public interface IChangeEvent
	{ }

	/// <summary>A control's value changed; sent by <see cref="INotifyValueChanged{T}" /> implementations.</summary>
	public sealed class ChangeEvent<T> : EventBase<ChangeEvent<T>>, IChangeEvent
	{
		public T previousValue { get; set; }
		public T newValue { get; set; }

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			previousValue = default;
			newValue = default;
		}

		public static ChangeEvent<T> GetPooled(T previousValue, T newValue)
		{
			ChangeEvent<T> evt = GetPooled();
			evt.previousValue = previousValue;
			evt.newValue = newValue;
			return evt;
		}
	}

	/// <summary>Text typed into a text field (before it is applied); the new text is in <see cref="newData" />.</summary>
	public sealed class InputEvent : EventBase<InputEvent>
	{
		public string previousData { get; set; }
		public string newData { get; set; }

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			previousData = null;
			newData = null;
		}

		public static InputEvent GetPooled(string previousData, string newData)
		{
			InputEvent evt = GetPooled();
			evt.previousData = previousData;
			evt.newData = newData;
			return evt;
		}
	}

	// ------------------------------------------------------------------ layout and panel

	/// <summary>The element's layout rectangle changed (sent after the layout pass; does not bubble).</summary>
	public sealed class GeometryChangedEvent : EventBase<GeometryChangedEvent>
	{
		public Rect oldRect { get; set; }
		public Rect newRect { get; set; }

		public static GeometryChangedEvent GetPooled(Rect oldRect, Rect newRect)
		{
			GeometryChangedEvent evt = GetPooled();
			evt.oldRect = oldRect;
			evt.newRect = newRect;
			return evt;
		}
	}

	public sealed class AttachToPanelEvent : EventBase<AttachToPanelEvent>
	{
		public IPanel originPanel { get; internal set; }
		public IPanel destinationPanel { get; internal set; }

		protected override void Init()
		{
			base.Init();
			originPanel = null;
			destinationPanel = null;
		}

		public static AttachToPanelEvent GetPooled(IPanel origin, IPanel destination)
		{
			AttachToPanelEvent evt = GetPooled();
			evt.originPanel = origin;
			evt.destinationPanel = destination;
			return evt;
		}
	}

	public sealed class DetachFromPanelEvent : EventBase<DetachFromPanelEvent>
	{
		public IPanel originPanel { get; internal set; }
		public IPanel destinationPanel { get; internal set; }

		protected override void Init()
		{
			base.Init();
			originPanel = null;
			destinationPanel = null;
		}

		public static DetachFromPanelEvent GetPooled(IPanel origin, IPanel destination)
		{
			DetachFromPanelEvent evt = GetPooled();
			evt.originPanel = origin;
			evt.destinationPanel = destination;
			return evt;
		}
	}

	/// <summary>Sent after the element's styles are resolved; read custom properties with <see cref="customStyle" />.</summary>
	public sealed class CustomStyleResolvedEvent : EventBase<CustomStyleResolvedEvent>
	{
		public ICustomStyle customStyle { get; internal set; }

		protected override void Init()
		{
			base.Init();
			customStyle = null;
		}
	}

	public sealed class TooltipEvent : EventBase<TooltipEvent>
	{
		public string tooltip { get; set; }
		public Rect rect { get; set; }

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			tooltip = null;
			rect = Rect.zero;
		}
	}

	// ------------------------------------------------------------------ transitions

	public abstract class TransitionEventBase<T> : EventBase<T>
		where T : TransitionEventBase<T>, new()
	{
		public List<StylePropertyName> stylePropertyNames { get; } = new List<StylePropertyName>();
		public double elapsedTime { get; internal set; }

		protected override void Init()
		{
			base.Init();
			bubbles = true;
			tricklesDown = true;
			stylePropertyNames.Clear();
			elapsedTime = 0;
		}

		public bool AffectsProperty(StylePropertyName name)
		{
			return stylePropertyNames.Contains(name);
		}

		public static T GetPooled(StylePropertyName property, double elapsedTime)
		{
			T evt = GetPooled();
			evt.stylePropertyNames.Add(property);
			evt.elapsedTime = elapsedTime;
			return evt;
		}
	}

	public sealed class TransitionRunEvent : TransitionEventBase<TransitionRunEvent>
	{ }

	public sealed class TransitionStartEvent : TransitionEventBase<TransitionStartEvent>
	{ }

	public sealed class TransitionEndEvent : TransitionEventBase<TransitionEndEvent>
	{ }

	public sealed class TransitionCancelEvent : TransitionEventBase<TransitionCancelEvent>
	{ }
}
