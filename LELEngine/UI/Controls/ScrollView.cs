using System;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>A scroll bar: a slider with a thumb sized to the visible fraction, plus optional arrow buttons.</summary>
	[UxmlElement]
	public class Scroller : VisualElement
	{
		public static readonly string ussClassName = "unity-scroller";
		public static readonly string horizontalVariantUssClassName = ussClassName + "--horizontal";
		public static readonly string verticalVariantUssClassName = ussClassName + "--vertical";
		public static readonly string sliderUssClassName = ussClassName + "__slider";
		public static readonly string lowButtonUssClassName = ussClassName + "__low-button";
		public static readonly string highButtonUssClassName = ussClassName + "__high-button";

		public event Action<float> valueChanged;

		public Slider slider { get; }
		public RepeatButton lowButton { get; }
		public RepeatButton highButton { get; }

		[UxmlAttribute]
		public float value
		{
			get => slider.value;
			set => slider.value = value;
		}

		[UxmlAttribute("low-value")]
		public float lowValue
		{
			get => slider.lowValue;
			set => slider.lowValue = value;
		}

		[UxmlAttribute("high-value")]
		public float highValue
		{
			get => slider.highValue;
			set => slider.highValue = value;
		}

		[UxmlAttribute]
		public SliderDirection direction
		{
			get => slider.direction;
			set
			{
				slider.direction = value;
				EnableInClassList(horizontalVariantUssClassName, value == SliderDirection.Horizontal);
				EnableInClassList(verticalVariantUssClassName, value == SliderDirection.Vertical);
			}
		}

		public Scroller()
			: this(0f, 0f, null)
		{ }

		public Scroller(float lowValue, float highValue, Action<float> valueChanged, SliderDirection direction = SliderDirection.Vertical)
		{
			AddToClassList(ussClassName);
			slider = new Slider(lowValue, highValue, direction, 20f) { name = "unity-slider" };
			slider.AddToClassList(sliderUssClassName);
			slider.focusable = false;
			slider.RegisterValueChangedCallback(evt =>
			{
				evt.StopPropagation();
				this.valueChanged?.Invoke(evt.newValue);
			});
			lowButton = new RepeatButton(ScrollPageUp, 250, 30) { name = "unity-low-button" };
			lowButton.AddToClassList(lowButtonUssClassName);
			highButton = new RepeatButton(ScrollPageDown, 250, 30) { name = "unity-high-button" };
			highButton.AddToClassList(highButtonUssClassName);
			hierarchy.Add(lowButton);
			hierarchy.Add(slider);
			hierarchy.Add(highButton);
			this.direction = direction;
			if (valueChanged != null)
			{
				this.valueChanged += valueChanged;
			}
		}

		/// <summary>Sizes the thumb to the fraction of the content that is visible.</summary>
		public void Adjust(float factor)
		{
			slider.AdjustDragElement(factor);
		}

		public void ScrollPageUp()
		{
			value -= slider.pageSize * (slider.lowValue < slider.highValue ? 1f : -1f);
		}

		public void ScrollPageDown()
		{
			value += slider.pageSize * (slider.lowValue < slider.highValue ? 1f : -1f);
		}
	}

	public enum ScrollViewMode
	{
		Vertical,
		Horizontal,
		VerticalAndHorizontal
	}

	public enum ScrollerVisibility
	{
		Auto,
		AlwaysVisible,
		Hidden
	}

	/// <summary>
	///     Shows content larger than its box: children go into the <see cref="contentContainer" />, scrolled with the
	///     mouse wheel, the scroll bars or by dragging the bars.
	/// </summary>
	[UxmlElement]
	public class ScrollView : VisualElement
	{
		#region PublicFields

		public static readonly string ussClassName = "unity-scroll-view";
		public static readonly string viewportUssClassName = ussClassName + "__content-viewport";
		public static readonly string contentAndVerticalScrollUssClassName = ussClassName + "__content-and-vertical-scroll-container";
		public static readonly string contentUssClassName = ussClassName + "__content-container";
		public static readonly string hScrollerUssClassName = ussClassName + "__horizontal-scroller";
		public static readonly string vScrollerUssClassName = ussClassName + "__vertical-scroller";
		public static readonly string horizontalVariantUssClassName = ussClassName + "--horizontal";
		public static readonly string verticalVariantUssClassName = ussClassName + "--vertical";
		public static readonly string verticalHorizontalVariantUssClassName = ussClassName + "--vertical-horizontal";

		public VisualElement contentViewport { get; }
		public Scroller horizontalScroller { get; }
		public Scroller verticalScroller { get; }

		public override VisualElement contentContainer => m_ContentContainer;

		[UxmlAttribute]
		public ScrollViewMode mode
		{
			get => m_Mode;
			set
			{
				m_Mode = value;
				RemoveFromClassList(horizontalVariantUssClassName);
				RemoveFromClassList(verticalVariantUssClassName);
				RemoveFromClassList(verticalHorizontalVariantUssClassName);
				switch (value)
				{
					case ScrollViewMode.Vertical: AddToClassList(verticalVariantUssClassName); break;
					case ScrollViewMode.Horizontal: AddToClassList(horizontalVariantUssClassName); break;
					default: AddToClassList(verticalHorizontalVariantUssClassName); break;
				}

				UpdateScrollers();
			}
		}

		[UxmlAttribute("horizontal-scroller-visibility")]
		public ScrollerVisibility horizontalScrollerVisibility
		{
			get => m_HorizontalVisibility;
			set
			{
				m_HorizontalVisibility = value;
				UpdateScrollers();
			}
		}

		[UxmlAttribute("vertical-scroller-visibility")]
		public ScrollerVisibility verticalScrollerVisibility
		{
			get => m_VerticalVisibility;
			set
			{
				m_VerticalVisibility = value;
				UpdateScrollers();
			}
		}

		/// <summary>Panel units scrolled per wheel notch.</summary>
		[UxmlAttribute("mouse-wheel-scroll-size")]
		public float mouseWheelScrollSize { get; set; } = 18f;

		[UxmlAttribute("horizontal-page-size")]
		public float horizontalPageSize
		{
			get => horizontalScroller.slider.pageSize;
			set => horizontalScroller.slider.pageSize = value;
		}

		[UxmlAttribute("vertical-page-size")]
		public float verticalPageSize
		{
			get => verticalScroller.slider.pageSize;
			set => verticalScroller.slider.pageSize = value;
		}

		/// <summary>Scroll position in panel units (x, y).</summary>
		public Vector2 scrollOffset
		{
			get => new Vector2(horizontalScroller.value, verticalScroller.value);
			set
			{
				if (value != scrollOffset)
				{
					horizontalScroller.value = value.X;
					verticalScroller.value = value.Y;
					UpdateContentViewTransform();
				}
			}
		}

		#endregion

		#region PrivateFields

		private readonly VisualElement m_ContentContainer;
		private readonly VisualElement contentAndVerticalScrollContainer;
		private ScrollViewMode m_Mode = ScrollViewMode.Vertical;
		private ScrollerVisibility m_HorizontalVisibility = ScrollerVisibility.Auto;
		private ScrollerVisibility m_VerticalVisibility = ScrollerVisibility.Auto;

		#endregion

		#region Constructors

		public ScrollView()
			: this(ScrollViewMode.Vertical)
		{ }

		public ScrollView(ScrollViewMode scrollViewMode)
		{
			AddToClassList(ussClassName);
			contentAndVerticalScrollContainer = new VisualElement { name = "unity-content-and-vertical-scroll-container", pickingMode = PickingMode.Ignore };
			contentAndVerticalScrollContainer.AddToClassList(contentAndVerticalScrollUssClassName);
			hierarchy.Add(contentAndVerticalScrollContainer);

			contentViewport = new VisualElement { name = "unity-content-viewport" };
			contentViewport.AddToClassList(viewportUssClassName);
			contentViewport.pickingMode = PickingMode.Ignore;
			contentAndVerticalScrollContainer.Add(contentViewport);

			m_ContentContainer = new VisualElement { name = "unity-content-container", pickingMode = PickingMode.Ignore };
			m_ContentContainer.AddToClassList(contentUssClassName);
			contentViewport.Add(m_ContentContainer);

			verticalScroller = new Scroller(0f, 1f, value => UpdateContentViewTransform(), SliderDirection.Vertical) { name = "unity-vertical-scroller" };
			verticalScroller.AddToClassList(vScrollerUssClassName);
			contentAndVerticalScrollContainer.Add(verticalScroller);

			horizontalScroller = new Scroller(0f, 1f, value => UpdateContentViewTransform(), SliderDirection.Horizontal) { name = "unity-horizontal-scroller" };
			horizontalScroller.AddToClassList(hScrollerUssClassName);
			hierarchy.Add(horizontalScroller);

			contentViewport.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
			m_ContentContainer.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
			RegisterCallback<WheelEvent>(OnWheel);
			mode = scrollViewMode;
		}

		#endregion

		#region PublicMethods

		/// <summary>Scrolls so the child (anywhere inside the content) is visible.</summary>
		public void ScrollTo(VisualElement child)
		{
			if (child == null || !m_ContentContainer.Contains(child))
			{
				return;
			}

			Rect childRect = child.ChangeCoordinatesTo(m_ContentContainer, new Rect(0f, 0f, child.layout.width, child.layout.height));
			Rect viewport = contentViewport.contentRect;
			Vector2 offset = scrollOffset;
			if (childRect.yMin < offset.Y) offset.Y = childRect.yMin;
			else if (childRect.yMax > offset.Y + viewport.height) offset.Y = childRect.yMax - viewport.height;
			if (childRect.xMin < offset.X) offset.X = childRect.xMin;
			else if (childRect.xMax > offset.X + viewport.width) offset.X = childRect.xMax - viewport.width;
			scrollOffset = offset;
		}

		#endregion

		#region PrivateMethods

		private void OnGeometryChanged(GeometryChangedEvent evt)
		{
			UpdateScrollers();
			UpdateContentViewTransform();
		}

		private bool NeedsHorizontal()
		{
			return m_Mode != ScrollViewMode.Vertical && m_ContentContainer.layout.width > contentViewport.contentRect.width + 0.5f;
		}

		private bool NeedsVertical()
		{
			return m_Mode != ScrollViewMode.Horizontal && m_ContentContainer.layout.height > contentViewport.contentRect.height + 0.5f;
		}

		private void UpdateScrollers()
		{
			if (horizontalScroller == null || verticalScroller == null)
			{
				return;
			}

			bool showH = m_HorizontalVisibility == ScrollerVisibility.AlwaysVisible || (m_HorizontalVisibility == ScrollerVisibility.Auto && NeedsHorizontal());
			bool showV = m_VerticalVisibility == ScrollerVisibility.AlwaysVisible || (m_VerticalVisibility == ScrollerVisibility.Auto && NeedsVertical());
			if (m_Mode == ScrollViewMode.Vertical) showH = false;
			if (m_Mode == ScrollViewMode.Horizontal) showV = false;
			horizontalScroller.style.display = showH ? DisplayStyle.Flex : DisplayStyle.None;
			verticalScroller.style.display = showV ? DisplayStyle.Flex : DisplayStyle.None;

			Rect viewport = contentViewport.contentRect;
			Rect content = m_ContentContainer.layout;
			float maxX = Math.Max(0f, content.width - viewport.width);
			float maxY = Math.Max(0f, content.height - viewport.height);
			horizontalScroller.lowValue = 0f;
			horizontalScroller.highValue = maxX;
			verticalScroller.lowValue = 0f;
			verticalScroller.highValue = maxY;
			horizontalScroller.Adjust(content.width > 0f ? Math.Clamp(viewport.width / content.width, 0f, 1f) : 1f);
			verticalScroller.Adjust(content.height > 0f ? Math.Clamp(viewport.height / content.height, 0f, 1f) : 1f);
			if (horizontalScroller.slider.pageSize <= 0f) horizontalScroller.slider.pageSize = Math.Max(1f, viewport.width * 0.9f);
			if (verticalScroller.slider.pageSize <= 0f) verticalScroller.slider.pageSize = Math.Max(1f, viewport.height * 0.9f);
			if (horizontalScroller.value > maxX) horizontalScroller.value = maxX;
			if (verticalScroller.value > maxY) verticalScroller.value = maxY;
		}

		private void UpdateContentViewTransform()
		{
			Vector2 offset = scrollOffset;
			m_ContentContainer.style.translate = new Translate(-(float)Math.Round(offset.X), -(float)Math.Round(offset.Y));
		}

		private void OnWheel(WheelEvent evt)
		{
			bool vertical = m_Mode != ScrollViewMode.Horizontal && verticalScroller.highValue > 0f;
			bool horizontal = m_Mode != ScrollViewMode.Vertical && horizontalScroller.highValue > 0f;
			if (!vertical && !horizontal)
			{
				return;
			}

			Vector2 before = scrollOffset;
			if (vertical && (!horizontal || !evt.shiftKey))
			{
				verticalScroller.value += evt.delta.Y * mouseWheelScrollSize;
			}
			else
			{
				horizontalScroller.value += (evt.shiftKey ? evt.delta.Y : evt.delta.X) * mouseWheelScrollSize;
			}

			if (scrollOffset != before)
			{
				UpdateContentViewTransform();
				evt.StopPropagation();
			}
		}

		#endregion
	}
}
