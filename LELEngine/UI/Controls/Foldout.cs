namespace LELEngine.UIElements
{
	/// <summary>A collapsible section: a header toggle and a content container that shows when open.</summary>
	[UxmlElement]
	public class Foldout : VisualElement, INotifyValueChanged<bool>
	{
		public static readonly string ussClassName = "unity-foldout";
		public static readonly string toggleUssClassName = ussClassName + "__toggle";
		public static readonly string contentUssClassName = ussClassName + "__content";
		public static readonly string arrowUssClassName = ussClassName + "__arrow";
		public static readonly string openUssClassName = ussClassName + "--open";

		public Toggle toggle { get; }
		public override VisualElement contentContainer => content;

		[UxmlAttribute]
		public string text
		{
			get => toggle.text;
			set => toggle.text = value;
		}

		[UxmlAttribute]
		public bool value
		{
			get => m_Value;
			set
			{
				if (m_Value == value)
				{
					return;
				}

				bool previous = m_Value;
				if (panel != null)
				{
					using (ChangeEvent<bool> evt = ChangeEvent<bool>.GetPooled(previous, value))
					{
						evt.target = this;
						SetValueWithoutNotify(value);
						SendEvent(evt);
					}
				}
				else
				{
					SetValueWithoutNotify(value);
				}
			}
		}

		private readonly VisualElement content;
		private bool m_Value = true;

		public Foldout()
		{
			AddToClassList(ussClassName);
			toggle = new Toggle { name = "unity-foldout-toggle" };
			toggle.AddToClassList(toggleUssClassName);
			var arrow = new VisualElement { pickingMode = PickingMode.Ignore };
			arrow.AddToClassList(arrowUssClassName);
			toggle.hierarchy.Insert(0, arrow);
			toggle.RegisterValueChangedCallback(evt =>
			{
				evt.StopPropagation();
				value = evt.newValue;
			});
			hierarchy.Add(toggle);
			content = new VisualElement { name = "unity-content" };
			content.AddToClassList(contentUssClassName);
			hierarchy.Add(content);
			SetValueWithoutNotify(true);
		}

		public void SetValueWithoutNotify(bool newValue)
		{
			m_Value = newValue;
			toggle.SetValueWithoutNotify(newValue);
			content.style.display = newValue ? DisplayStyle.Flex : DisplayStyle.None;
			EnableInClassList(openUssClassName, newValue);
		}
	}
}
