using System;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.UIElements
{
	public enum SliderDirection
	{
		Horizontal,
		Vertical
	}

	/// <summary>A draggable handle on a track between <see cref="lowValue" /> and <see cref="highValue" />.</summary>
	public abstract class BaseSlider<TValueType> : BaseField<TValueType>
		where TValueType : IComparable<TValueType>
	{
		#region PublicFields

		public new static readonly string ussClassName = "unity-base-slider";
		public new static readonly string labelUssClassName = ussClassName + "__label";
		public new static readonly string inputUssClassName = ussClassName + "__input";
		public static readonly string horizontalVariantUssClassName = ussClassName + "--horizontal";
		public static readonly string verticalVariantUssClassName = ussClassName + "--vertical";
		public static readonly string dragContainerUssClassName = ussClassName + "__drag-container";
		public static readonly string trackerUssClassName = ussClassName + "__tracker";
		public static readonly string draggerUssClassName = ussClassName + "__dragger";
		public static readonly string draggerBorderUssClassName = ussClassName + "__dragger-border";
		public static readonly string fillUssClassName = ussClassName + "__fill";
		public static readonly string textFieldClassName = ussClassName + "__text-field";

		[UxmlAttribute("low-value")]
		public TValueType lowValue
		{
			get => m_LowValue;
			set
			{
				m_LowValue = value;
				ClampValue();
				UpdateDragger();
			}
		}

		[UxmlAttribute("high-value")]
		public TValueType highValue
		{
			get => m_HighValue;
			set
			{
				m_HighValue = value;
				ClampValue();
				UpdateDragger();
			}
		}

		/// <summary>Range covered by a click on the track (0: jump to the click).</summary>
		[UxmlAttribute("page-size")]
		public float pageSize { get; set; }

		[UxmlAttribute]
		public SliderDirection direction
		{
			get => m_Direction;
			set
			{
				m_Direction = value;
				EnableInClassList(horizontalVariantUssClassName, value == SliderDirection.Horizontal);
				EnableInClassList(verticalVariantUssClassName, value == SliderDirection.Vertical);
				UpdateDragger();
			}
		}

		/// <summary>High value at the left / top.</summary>
		[UxmlAttribute]
		public bool inverted
		{
			get => m_Inverted;
			set
			{
				m_Inverted = value;
				UpdateDragger();
			}
		}

		/// <summary>Shows a text field next to the slider to type the value.</summary>
		[UxmlAttribute("show-input-field")]
		public bool showInputField
		{
			get => inputTextField != null;
			set
			{
				if (value == (inputTextField != null))
				{
					return;
				}

				if (value)
				{
					inputTextField = new TextField { isDelayed = true };
					inputTextField.AddToClassList(textFieldClassName);
					inputTextField.RegisterValueChangedCallback(OnTextFieldChanged);
					visualInput.Add(inputTextField);
					UpdateTextField();
				}
				else
				{
					inputTextField.RemoveFromHierarchy();
					inputTextField = null;
				}
			}
		}

		/// <summary>Colors the track up to the value.</summary>
		[UxmlAttribute]
		public bool fill
		{
			get => fillElement.style.display.value != DisplayStyle.None && fillElement.style.display.keyword == StyleKeyword.Undefined;
			set => fillElement.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
		}

		public VisualElement dragContainer { get; }
		public VisualElement dragElement { get; }

		/// <summary>Length of the dragger as a fraction of the track (scrollers), 0 for the styled size.</summary>
		public float draggerFactor
		{
			get => m_DraggerFactor;
			set
			{
				m_DraggerFactor = Math.Clamp(value, 0f, 1f);
				UpdateDragger();
			}
		}

		#endregion

		#region PrivateFields

		private TValueType m_LowValue;
		private TValueType m_HighValue;
		private SliderDirection m_Direction;
		private bool m_Inverted;
		private readonly VisualElement tracker;
		private readonly VisualElement fillElement;
		private TextField inputTextField;
		private float m_DraggerFactor;
		private bool dragging;
		private float dragOffset;
		private float draggerMinSize = 10f;

		#endregion

		#region Constructors

		protected BaseSlider(string label, TValueType start, TValueType end, SliderDirection direction, float pageSize)
			: base(label, new VisualElement { pickingMode = PickingMode.Ignore })
		{
			AddToClassList(ussClassName);
			labelElement.AddToClassList(labelUssClassName);
			visualInput.AddToClassList(inputUssClassName);
			this.pageSize = pageSize;
			m_LowValue = start;
			m_HighValue = end;

			dragContainer = new VisualElement { name = "unity-drag-container" };
			dragContainer.AddToClassList(dragContainerUssClassName);
			tracker = new VisualElement { name = "unity-tracker", pickingMode = PickingMode.Ignore };
			tracker.AddToClassList(trackerUssClassName);
			fillElement = new VisualElement { name = "unity-fill", pickingMode = PickingMode.Ignore };
			fillElement.AddToClassList(fillUssClassName);
			fillElement.style.display = DisplayStyle.None;
			dragElement = new VisualElement { name = "unity-dragger" };
			dragElement.AddToClassList(draggerUssClassName);
			var border = new VisualElement { name = "unity-dragger-border", pickingMode = PickingMode.Ignore };
			border.AddToClassList(draggerBorderUssClassName);
			dragElement.Add(border);
			dragContainer.Add(tracker);
			dragContainer.Add(fillElement);
			dragContainer.Add(dragElement);
			visualInput.Add(dragContainer);

			dragContainer.RegisterCallback<PointerDownEvent>(OnPointerDown);
			dragContainer.RegisterCallback<PointerMoveEvent>(OnPointerMove);
			dragContainer.RegisterCallback<PointerUpEvent>(OnPointerUp);
			dragContainer.RegisterCallback<PointerCaptureOutEvent>(evt => dragging = false);
			dragContainer.RegisterCallback<GeometryChangedEvent>(evt => UpdateDragger());
			dragElement.RegisterCallback<GeometryChangedEvent>(evt => UpdateDragger());
			RegisterCallback<KeyDownEvent>(OnKeyDown);
			this.direction = direction;
		}

		#endregion

		#region PublicMethods

		public override void SetValueWithoutNotify(TValueType newValue)
		{
			base.SetValueWithoutNotify(Clamp(newValue));
		}

		/// <summary>Scrollers: sets the dragger length to a fraction of the track.</summary>
		public void AdjustDragElement(float factor)
		{
			draggerFactor = factor;
			dragElement.style.display = factor >= 1f ? DisplayStyle.None : DisplayStyle.Flex;
		}

		#endregion

		#region ProtectedMethods

		protected abstract float SliderNormalizeValue(TValueType currentValue, TValueType lowerValue, TValueType higherValue);
		protected abstract TValueType SliderLerpUnclamped(TValueType a, TValueType b, float interpolant);
		protected abstract TValueType SliderRange();
		protected abstract TValueType ParseStringToValue(string text);
		protected abstract string ValueToString(TValueType value);

		protected override void UpdateDisplay()
		{
			UpdateDragger();
			UpdateTextField();
		}

		protected TValueType Clamp(TValueType v)
		{
			if (m_LowValue.CompareTo(m_HighValue) <= 0)
			{
				if (v.CompareTo(m_LowValue) < 0) return m_LowValue;
				if (v.CompareTo(m_HighValue) > 0) return m_HighValue;
				return v;
			}

			if (v.CompareTo(m_HighValue) < 0) return m_HighValue;
			if (v.CompareTo(m_LowValue) > 0) return m_LowValue;
			return v;
		}

		#endregion

		#region PrivateMethods

		private void ClampValue()
		{
			TValueType clamped = Clamp(rawValue);
			if (clamped.CompareTo(rawValue) != 0)
			{
				value = clamped;
			}
		}

		private bool horizontal => m_Direction == SliderDirection.Horizontal;

		private float TrackLength()
		{
			Rect r = dragContainer.layout;
			return horizontal ? r.width : r.height;
		}

		private float DraggerLength()
		{
			Rect r = dragElement.layout;
			return horizontal ? r.width : r.height;
		}

		private void UpdateDragger()
		{
			if (dragElement == null || dragContainer == null)
			{
				return;
			}

			float track = TrackLength();
			if (m_DraggerFactor > 0f && track > 0f)
			{
				float length = Math.Max(draggerMinSize, track * m_DraggerFactor);
				if (horizontal) dragElement.style.width = length;
				else dragElement.style.height = length;
			}

			float normalized = SliderNormalizeValue(rawValue, m_LowValue, m_HighValue);
			if (float.IsNaN(normalized)) normalized = 0f;
			normalized = Math.Clamp(normalized, 0f, 1f);
			if (m_Inverted)
			{
				normalized = 1f - normalized;
			}

			float dragger = DraggerLength();
			float position = (track - dragger) * normalized;
			if (horizontal)
			{
				dragElement.style.left = position;
				dragElement.style.top = StyleKeyword.Null;
				fillElement.style.left = 0f;
				fillElement.style.width = m_Inverted ? track - position : position + dragger * 0.5f;
				fillElement.style.top = StyleKeyword.Null;
				fillElement.style.height = StyleKeyword.Null;
				if (m_Inverted)
				{
					fillElement.style.left = position + dragger * 0.5f;
					fillElement.style.width = track - position - dragger * 0.5f;
				}
			}
			else
			{
				dragElement.style.top = position;
				dragElement.style.left = StyleKeyword.Null;
				fillElement.style.top = 0f;
				fillElement.style.height = position + dragger * 0.5f;
				fillElement.style.left = StyleKeyword.Null;
				fillElement.style.width = StyleKeyword.Null;
			}
		}

		private void UpdateTextField()
		{
			inputTextField?.SetValueWithoutNotify(ValueToString(rawValue));
		}

		private void OnTextFieldChanged(ChangeEvent<string> evt)
		{
			evt.StopPropagation();
			try
			{
				value = ParseStringToValue(evt.newValue);
			}
			catch (Exception)
			{
				UpdateTextField();
			}
		}

		private TValueType ValueAtPosition(float position)
		{
			float track = TrackLength();
			float dragger = DraggerLength();
			float range = Math.Max(1f, track - dragger);
			float t = Math.Clamp((position - dragger * 0.5f) / range, 0f, 1f);
			if (m_Inverted)
			{
				t = 1f - t;
			}

			return SliderLerpUnclamped(m_LowValue, m_HighValue, t);
		}

		private void OnPointerDown(PointerDownEvent evt)
		{
			if (evt.button != 0 || !enabledInHierarchy)
			{
				return;
			}

			Focus();
			float position = horizontal ? evt.localPosition.X : evt.localPosition.Y;
			Rect draggerRect = dragElement.layout;
			bool onDragger = horizontal ? position >= draggerRect.xMin && position <= draggerRect.xMax : position >= draggerRect.yMin && position <= draggerRect.yMax;
			if (onDragger)
			{
				dragOffset = position - (horizontal ? draggerRect.x : draggerRect.y);
				dragging = true;
				dragContainer.CapturePointer(evt.pointerId);
				evt.StopPropagation();
				return;
			}

			if (pageSize > 0f)
			{
				// Page towards the click.
				float draggerCenter = (horizontal ? draggerRect.x : draggerRect.y) + DraggerLength() * 0.5f;
				float sign = position > draggerCenter ? 1f : -1f;
				if (m_Inverted) sign = -sign;
				float normalized = SliderNormalizeValue(rawValue, m_LowValue, m_HighValue);
				float step = pageSize / SliderNormalizeDenominator();
				value = SliderLerpUnclamped(m_LowValue, m_HighValue, Math.Clamp(normalized + sign * step, 0f, 1f));
			}
			else
			{
				value = ValueAtPosition(position);
				dragOffset = DraggerLength() * 0.5f;
				dragging = true;
				dragContainer.CapturePointer(evt.pointerId);
			}

			evt.StopPropagation();
		}

		/// <summary>The range size as a float (pageSize is expressed in value units).</summary>
		private float SliderNormalizeDenominator()
		{
			float low = Convert.ToSingle(m_LowValue);
			float high = Convert.ToSingle(m_HighValue);
			return Math.Abs(high - low) > 0f ? Math.Abs(high - low) : 1f;
		}

		private void OnPointerMove(PointerMoveEvent evt)
		{
			if (!dragging)
			{
				return;
			}

			float position = horizontal ? evt.localPosition.X : evt.localPosition.Y;
			value = ValueAtPosition(position - dragOffset + DraggerLength() * 0.5f);
			evt.StopPropagation();
		}

		private void OnPointerUp(PointerUpEvent evt)
		{
			if (dragging)
			{
				dragging = false;
				dragContainer.ReleasePointer(evt.pointerId);
				evt.StopPropagation();
			}
		}

		private void OnKeyDown(KeyDownEvent evt)
		{
			if (evt.keyCode == Keys.Unknown)
			{
				return;
			}

			float step = 0f;
			bool decrease = horizontal ? evt.keyCode == Keys.Left : evt.keyCode == Keys.Up;
			bool increase = horizontal ? evt.keyCode == Keys.Right : evt.keyCode == Keys.Down;
			if (!horizontal)
			{
				// Up increases for vertical sliders unless inverted.
				bool swap = !m_Inverted;
				if (swap)
				{
					bool t = decrease;
					decrease = increase;
					increase = t;
				}
			}

			if (increase) step = 1f;
			else if (decrease) step = -1f;
			else if (evt.keyCode == Keys.Home) { value = m_LowValue; evt.StopPropagation(); return; }
			else if (evt.keyCode == Keys.End) { value = m_HighValue; evt.StopPropagation(); return; }
			else return;

			if (m_Inverted) step = -step;
			float normalized = SliderNormalizeValue(rawValue, m_LowValue, m_HighValue);
			float delta = pageSize > 0f ? pageSize / SliderNormalizeDenominator() : 0.01f;
			if (typeof(TValueType) == typeof(int))
			{
				delta = Math.Max(delta, 1f / SliderNormalizeDenominator());
			}

			value = SliderLerpUnclamped(m_LowValue, m_HighValue, Math.Clamp(normalized + step * delta, 0f, 1f));
			evt.StopPropagation();
		}

		#endregion
	}

	[UxmlElement]
	public class Slider : BaseSlider<float>
	{
		public new static readonly string ussClassName = "unity-slider";

		public Slider()
			: this(null, 0f, 10f)
		{ }

		public Slider(float start, float end, SliderDirection direction = SliderDirection.Horizontal, float pageSize = 0f)
			: this(null, start, end, direction, pageSize)
		{ }

		public Slider(string label, float start = 0f, float end = 10f, SliderDirection direction = SliderDirection.Horizontal, float pageSize = 0f)
			: base(label, start, end, direction, pageSize)
		{
			AddToClassList(ussClassName);
		}

		protected override float SliderNormalizeValue(float currentValue, float lowerValue, float higherValue)
		{
			float range = higherValue - lowerValue;
			return range == 0f ? 0f : (currentValue - lowerValue) / range;
		}

		protected override float SliderLerpUnclamped(float a, float b, float interpolant)
		{
			return a + (b - a) * interpolant;
		}

		protected override float SliderRange()
		{
			return highValue - lowValue;
		}

		protected override float ParseStringToValue(string text)
		{
			return float.Parse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
		}

		protected override string ValueToString(float value)
		{
			return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	[UxmlElement]
	public class SliderInt : BaseSlider<int>
	{
		public new static readonly string ussClassName = "unity-slider-int";

		public SliderInt()
			: this(null, 0, 10)
		{ }

		public SliderInt(int start, int end, SliderDirection direction = SliderDirection.Horizontal, float pageSize = 0f)
			: this(null, start, end, direction, pageSize)
		{ }

		public SliderInt(string label, int start = 0, int end = 10, SliderDirection direction = SliderDirection.Horizontal, float pageSize = 0f)
			: base(label, start, end, direction, pageSize)
		{
			AddToClassList(ussClassName);
		}

		protected override float SliderNormalizeValue(int currentValue, int lowerValue, int higherValue)
		{
			float range = higherValue - lowerValue;
			return range == 0f ? 0f : (currentValue - lowerValue) / range;
		}

		protected override int SliderLerpUnclamped(int a, int b, float interpolant)
		{
			return (int)Math.Round(a + (b - a) * interpolant);
		}

		protected override int SliderRange()
		{
			return highValue - lowValue;
		}

		protected override int ParseStringToValue(string text)
		{
			return int.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
		}

		protected override string ValueToString(int value)
		{
			return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	/// <summary>Two handles selecting a range (x = min, y = max) inside [minLimit, maxLimit].</summary>
	[UxmlElement]
	public class MinMaxSlider : BaseField<Vector2>
	{
		public new static readonly string ussClassName = "unity-min-max-slider";
		public static readonly string trackerUssClassName = ussClassName + "__tracker";
		public static readonly string draggerUssClassName = ussClassName + "__dragger";
		public static readonly string minThumbUssClassName = ussClassName + "__min-thumb";
		public static readonly string maxThumbUssClassName = ussClassName + "__max-thumb";

		private readonly VisualElement dragContainer;
		private readonly VisualElement dragger;
		private readonly VisualElement minThumb;
		private readonly VisualElement maxThumb;
		private float m_MinLimit;
		private float m_MaxLimit = 1f;
		private int dragMode; // 0 none, 1 min, 2 max, 3 both
		private float dragStart;
		private Vector2 dragStartValue;
		private const float ThumbWidth = 8f;

		[UxmlAttribute("min-limit")]
		public float minLimit
		{
			get => m_MinLimit;
			set
			{
				m_MinLimit = value;
				SetValueWithoutNotify(rawValue);
			}
		}

		[UxmlAttribute("max-limit")]
		public float maxLimit
		{
			get => m_MaxLimit;
			set
			{
				m_MaxLimit = value;
				SetValueWithoutNotify(rawValue);
			}
		}

		public float minValue
		{
			get => rawValue.X;
			set => this.value = new Vector2(value, rawValue.Y);
		}

		public float maxValue
		{
			get => rawValue.Y;
			set => this.value = new Vector2(rawValue.X, value);
		}

		public float range => m_MaxLimit - m_MinLimit;

		public MinMaxSlider()
			: this(null, 0f, 1f, 0f, 1f)
		{ }

		public MinMaxSlider(string label, float minValue, float maxValue, float minLimit, float maxLimit)
			: base(label, new VisualElement { pickingMode = PickingMode.Ignore })
		{
			AddToClassList(ussClassName);
			AddToClassList(BaseSlider<float>.ussClassName);
			AddToClassList(BaseSlider<float>.horizontalVariantUssClassName);
			visualInput.AddToClassList(BaseSlider<float>.inputUssClassName);
			m_MinLimit = minLimit;
			m_MaxLimit = maxLimit;
			dragContainer = new VisualElement();
			dragContainer.AddToClassList(BaseSlider<float>.dragContainerUssClassName);
			var tracker = new VisualElement { pickingMode = PickingMode.Ignore };
			tracker.AddToClassList(BaseSlider<float>.trackerUssClassName);
			tracker.AddToClassList(trackerUssClassName);
			dragger = new VisualElement { pickingMode = PickingMode.Ignore };
			dragger.AddToClassList(draggerUssClassName);
			minThumb = new VisualElement { pickingMode = PickingMode.Ignore };
			minThumb.AddToClassList(minThumbUssClassName);
			maxThumb = new VisualElement { pickingMode = PickingMode.Ignore };
			maxThumb.AddToClassList(maxThumbUssClassName);
			dragContainer.Add(tracker);
			dragContainer.Add(dragger);
			dragContainer.Add(minThumb);
			dragContainer.Add(maxThumb);
			visualInput.Add(dragContainer);
			dragContainer.RegisterCallback<PointerDownEvent>(OnPointerDown);
			dragContainer.RegisterCallback<PointerMoveEvent>(OnPointerMove);
			dragContainer.RegisterCallback<PointerUpEvent>(OnPointerUp);
			dragContainer.RegisterCallback<GeometryChangedEvent>(evt => UpdateDisplay());
			SetValueWithoutNotify(new Vector2(minValue, maxValue));
		}

		public override void SetValueWithoutNotify(Vector2 newValue)
		{
			float min = Math.Clamp(Math.Min(newValue.X, newValue.Y), m_MinLimit, m_MaxLimit);
			float max = Math.Clamp(Math.Max(newValue.X, newValue.Y), m_MinLimit, m_MaxLimit);
			base.SetValueWithoutNotify(new Vector2(min, max));
		}

		protected override void UpdateDisplay()
		{
			if (dragContainer == null)
			{
				return;
			}

			float width = dragContainer.layout.width;
			float usable = Math.Max(0f, width - ThumbWidth * 2f);
			float r = range > 0f ? range : 1f;
			float minX = (rawValue.X - m_MinLimit) / r * usable;
			float maxX = (rawValue.Y - m_MinLimit) / r * usable + ThumbWidth;
			minThumb.style.left = minX;
			maxThumb.style.left = maxX;
			dragger.style.left = minX + ThumbWidth;
			dragger.style.width = Math.Max(0f, maxX - minX - ThumbWidth);
		}

		private float PositionToValue(float x)
		{
			float usable = Math.Max(1f, dragContainer.layout.width - ThumbWidth * 2f);
			return m_MinLimit + Math.Clamp((x - ThumbWidth) / usable, 0f, 1f) * range;
		}

		private void OnPointerDown(PointerDownEvent evt)
		{
			if (evt.button != 0)
			{
				return;
			}

			float x = evt.localPosition.X;
			Rect minRect = minThumb.layout;
			Rect maxRect = maxThumb.layout;
			if (x >= minRect.xMin - 2f && x <= minRect.xMax + 2f) dragMode = 1;
			else if (x >= maxRect.xMin - 2f && x <= maxRect.xMax + 2f) dragMode = 2;
			else if (x > minRect.xMax && x < maxRect.xMin) dragMode = 3;
			else
			{
				dragMode = x < minRect.xMin ? 1 : 2;
				ApplyDrag(x);
			}

			dragStart = x;
			dragStartValue = rawValue;
			dragContainer.CapturePointer(evt.pointerId);
			Focus();
			evt.StopPropagation();
		}

		private void OnPointerMove(PointerMoveEvent evt)
		{
			if (dragMode == 0)
			{
				return;
			}

			ApplyDrag(evt.localPosition.X);
			evt.StopPropagation();
		}

		private void ApplyDrag(float x)
		{
			switch (dragMode)
			{
				case 1: value = new Vector2(Math.Min(PositionToValue(x + ThumbWidth * 0.5f), rawValue.Y), rawValue.Y); break;
				case 2: value = new Vector2(rawValue.X, Math.Max(PositionToValue(x - ThumbWidth * 0.5f), rawValue.X)); break;
				case 3:
				{
					float usable = Math.Max(1f, dragContainer.layout.width - ThumbWidth * 2f);
					float delta = (x - dragStart) / usable * range;
					float size = dragStartValue.Y - dragStartValue.X;
					float min = Math.Clamp(dragStartValue.X + delta, m_MinLimit, m_MaxLimit - size);
					value = new Vector2(min, min + size);
					break;
				}
			}
		}

		private void OnPointerUp(PointerUpEvent evt)
		{
			if (dragMode != 0)
			{
				dragMode = 0;
				dragContainer.ReleasePointer(evt.pointerId);
				evt.StopPropagation();
			}
		}
	}
}
