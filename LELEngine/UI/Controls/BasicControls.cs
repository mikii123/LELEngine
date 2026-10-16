using System;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>A piece of text.</summary>
	[UxmlElement]
	public class Label : TextElement
	{
		public new static readonly string ussClassName = "unity-label";

		public Label()
			: this("")
		{ }

		public Label(string text)
		{
			AddToClassList(ussClassName);
			this.text = text;
		}
	}

	/// <summary>A clickable button with a text; <see cref="clicked" /> fires on click, Enter or Space.</summary>
	[UxmlElement]
	public class Button : TextElement
	{
		public new static readonly string ussClassName = "unity-button";

		public event Action clicked
		{
			add => clickable.clicked += value;
			remove => clickable.clicked -= value;
		}

		public Clickable clickable { get; }

		public Button()
			: this(null)
		{ }

		public Button(Action clickEvent)
		{
			AddToClassList(ussClassName);
			pickingMode = PickingMode.Position;
			focusable = true;
			tabIndex = 0;
			clickable = new Clickable(clickEvent);
			this.AddManipulator(clickable);
		}

		protected override void ExecuteDefaultActionAtTarget(EventBase evt)
		{
			base.ExecuteDefaultActionAtTarget(evt);
			if (evt is NavigationSubmitEvent && enabledInHierarchy)
			{
				clickable.SimulateSingleClick(evt);
				evt.StopPropagation();
			}
		}
	}

	/// <summary>A button that keeps firing while held (scroll bar arrows, steppers).</summary>
	[UxmlElement]
	public class RepeatButton : TextElement
	{
		public new static readonly string ussClassName = "unity-repeat-button";

		private Clickable clickable;

		public RepeatButton()
			: this(null, 250, 50)
		{ }

		public RepeatButton(Action clickEvent, long delay, long interval)
		{
			AddToClassList(ussClassName);
			pickingMode = PickingMode.Position;
			SetAction(clickEvent, delay, interval);
		}

		public void SetAction(Action clickEvent, long delay, long interval)
		{
			if (clickable != null)
			{
				this.RemoveManipulator(clickable);
			}

			clickable = new Clickable(clickEvent, delay, interval);
			this.AddManipulator(clickable);
		}
	}

	/// <summary>Displays a texture with a scale mode (independent of the background image styles).</summary>
	[UxmlElement]
	public class Image : VisualElement
	{
		public static readonly string ussClassName = "unity-image";

		[UxmlAttribute]
		public Texture image
		{
			get => m_Image;
			set
			{
				if (!ReferenceEquals(m_Image, value))
				{
					m_Image = value;
					IncrementVersion(VersionChangeType.Layout | VersionChangeType.Repaint);
				}
			}
		}

		[UxmlAttribute("scale-mode")]
		public ScaleMode scaleMode
		{
			get => m_ScaleMode;
			set
			{
				if (m_ScaleMode != value)
				{
					m_ScaleMode = value;
					MarkDirtyRepaint();
				}
			}
		}

		[UxmlAttribute("tint-color")]
		public Color4 tintColor
		{
			get => m_TintColor;
			set
			{
				if (m_TintColor != value)
				{
					m_TintColor = value;
					MarkDirtyRepaint();
				}
			}
		}

		/// <summary>Texture coordinates drawn (0..1).</summary>
		public Rect uv
		{
			get => m_Uv;
			set
			{
				m_Uv = value;
				MarkDirtyRepaint();
			}
		}

		/// <summary>Pixel rectangle of the texture drawn (overrides uv when set).</summary>
		public Rect sourceRect
		{
			get => m_SourceRect;
			set
			{
				m_SourceRect = value;
				m_Uv = m_Image != null && m_Image.width > 0 && m_Image.height > 0
					? new Rect(value.x / m_Image.width, value.y / m_Image.height, value.width / m_Image.width, value.height / m_Image.height)
					: new Rect(0f, 0f, 1f, 1f);
				MarkDirtyRepaint();
			}
		}

		private Texture m_Image;
		private ScaleMode m_ScaleMode = ScaleMode.ScaleToFit;
		private Color4 m_TintColor = new Color4(1f, 1f, 1f, 1f);
		private Rect m_Uv = new Rect(0f, 0f, 1f, 1f);
		private Rect m_SourceRect;

		internal override bool requiresMeasure => true;

		public Image()
		{
			AddToClassList(ussClassName);
			pickingMode = PickingMode.Ignore;
			generateVisualContent = OnGenerateVisualContent;
		}

		internal override Vector2 Measure(float width, MeasureMode widthMode, float height, MeasureMode heightMode)
		{
			if (m_Image == null || m_Image.width <= 0 || m_Image.height <= 0)
			{
				return Vector2.Zero;
			}

			float tw = m_Image.width * m_Uv.width;
			float th = m_Image.height * m_Uv.height;
			float w = tw, h = th;
			if (widthMode == MeasureMode.Exactly)
			{
				w = width;
				h = heightMode == MeasureMode.Exactly ? height : th * (width / tw);
			}
			else if (heightMode == MeasureMode.Exactly)
			{
				h = height;
				w = tw * (height / th);
			}
			else
			{
				if (widthMode == MeasureMode.AtMost && w > width)
				{
					w = width;
					h = th * (w / tw);
				}

				if (heightMode == MeasureMode.AtMost && h > height)
				{
					h = height;
					w = tw * (h / th);
				}
			}

			return new Vector2(w, h);
		}

		private void OnGenerateVisualContent(MeshGenerationContext context)
		{
			if (m_Image == null || !m_Image.IsValid)
			{
				return;
			}

			Rect content = contentRect;
			float tw = m_Image.width * m_Uv.width;
			float th = m_Image.height * m_Uv.height;
			if (content.width <= 0f || content.height <= 0f || tw <= 0f || th <= 0f)
			{
				return;
			}

			Rect dest = content;
			Rect uvRect = m_Uv;
			switch (m_ScaleMode)
			{
				case ScaleMode.ScaleToFit:
				{
					float scale = Math.Min(content.width / tw, content.height / th);
					float w = tw * scale, h = th * scale;
					dest = new Rect(content.x + (content.width - w) * 0.5f, content.y + (content.height - h) * 0.5f, w, h);
					break;
				}

				case ScaleMode.ScaleAndCrop:
				{
					float scale = Math.Max(content.width / tw, content.height / th);
					float visibleW = content.width / scale / m_Image.width;
					float visibleH = content.height / scale / m_Image.height;
					uvRect = new Rect(m_Uv.x + (m_Uv.width - visibleW) * 0.5f, m_Uv.y + (m_Uv.height - visibleH) * 0.5f, visibleW, visibleH);
					break;
				}
			}

			context.DrawRectangle(dest, m_TintColor, m_Image, uvRect);
		}
	}

	/// <summary>A plain container with a background and border (styled by the theme).</summary>
	[UxmlElement]
	public class Box : VisualElement
	{
		public static readonly string ussClassName = "unity-box";

		public Box()
		{
			AddToClassList(ussClassName);
		}
	}

	/// <summary>A container with a title.</summary>
	[UxmlElement]
	public class GroupBox : VisualElement
	{
		public static readonly string ussClassName = "unity-group-box";
		public static readonly string labelUssClassName = ussClassName + "__label";

		private readonly Label titleLabel;

		[UxmlAttribute]
		public string text
		{
			get => titleLabel.text;
			set
			{
				titleLabel.text = value ?? "";
				titleLabel.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.None : DisplayStyle.Flex;
			}
		}

		public GroupBox()
			: this(null)
		{ }

		public GroupBox(string text)
		{
			AddToClassList(ussClassName);
			titleLabel = new Label();
			titleLabel.AddToClassList(labelUssClassName);
			hierarchy.Add(titleLabel);
			this.text = text;
		}
	}

	public enum HelpBoxMessageType
	{
		None,
		Info,
		Warning,
		Error
	}

	/// <summary>A message with an icon (info, warning, error).</summary>
	[UxmlElement]
	public class HelpBox : VisualElement
	{
		public static readonly string ussClassName = "unity-help-box";
		public static readonly string labelUssClassName = ussClassName + "__label";
		public static readonly string iconUssClassName = ussClassName + "__icon";

		private readonly Label label;
		private readonly VisualElement icon;
		private HelpBoxMessageType m_MessageType;

		[UxmlAttribute]
		public string text
		{
			get => label.text;
			set => label.text = value ?? "";
		}

		[UxmlAttribute("message-type")]
		public HelpBoxMessageType messageType
		{
			get => m_MessageType;
			set
			{
				m_MessageType = value;
				icon.RemoveFromClassList(iconUssClassName + "--info");
				icon.RemoveFromClassList(iconUssClassName + "--warning");
				icon.RemoveFromClassList(iconUssClassName + "--error");
				icon.style.display = value == HelpBoxMessageType.None ? DisplayStyle.None : DisplayStyle.Flex;
				if (value != HelpBoxMessageType.None)
				{
					icon.AddToClassList(iconUssClassName + "--" + value.ToString().ToLowerInvariant());
				}
			}
		}

		public HelpBox()
			: this("", HelpBoxMessageType.None)
		{ }

		public HelpBox(string text, HelpBoxMessageType messageType)
		{
			AddToClassList(ussClassName);
			icon = new VisualElement { pickingMode = PickingMode.Ignore };
			icon.AddToClassList(iconUssClassName);
			label = new Label();
			label.AddToClassList(labelUssClassName);
			hierarchy.Add(icon);
			hierarchy.Add(label);
			this.text = text;
			this.messageType = messageType;
		}
	}
}
