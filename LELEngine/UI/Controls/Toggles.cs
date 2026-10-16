using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>A check box with an optional text next to it.</summary>
	[UxmlElement]
	public class Toggle : BaseField<bool>
	{
		public new static readonly string ussClassName = "unity-toggle";
		public new static readonly string labelUssClassName = ussClassName + "__label";
		public new static readonly string inputUssClassName = ussClassName + "__input";
		public static readonly string checkmarkUssClassName = ussClassName + "__checkmark";
		public static readonly string textUssClassName = ussClassName + "__text";

		protected readonly VisualElement checkmark;
		protected readonly VisualElement tick;
		private Label textLabel;

		[UxmlAttribute]
		public string text
		{
			get => textLabel?.text ?? "";
			set
			{
				if (!string.IsNullOrEmpty(value))
				{
					if (textLabel == null)
					{
						textLabel = new Label { pickingMode = PickingMode.Ignore };
						textLabel.AddToClassList(textUssClassName);
						visualInput.Add(textLabel);
					}

					textLabel.text = value;
				}
				else if (textLabel != null)
				{
					textLabel.RemoveFromHierarchy();
					textLabel = null;
				}
			}
		}

		public Toggle()
			: this(null)
		{ }

		public Toggle(string label)
			: base(label, new VisualElement { pickingMode = PickingMode.Ignore })
		{
			AddToClassList(ussClassName);
			labelElement.AddToClassList(labelUssClassName);
			visualInput.AddToClassList(inputUssClassName);
			checkmark = new VisualElement { name = "unity-checkmark", pickingMode = PickingMode.Ignore };
			checkmark.AddToClassList(checkmarkUssClassName);
			tick = new VisualElement { pickingMode = PickingMode.Ignore };
			tick.AddToClassList(checkmarkUssClassName + "-tick");
			checkmark.Add(tick);
			visualInput.Add(checkmark);
			this.AddManipulator(new Clickable(OnClick));
		}

		protected override void UpdateDisplay()
		{
			if (rawValue)
			{
				pseudoStates |= PseudoStates.Checked;
			}
			else
			{
				pseudoStates &= ~PseudoStates.Checked;
			}
		}

		protected override void ExecuteDefaultActionAtTarget(EventBase evt)
		{
			base.ExecuteDefaultActionAtTarget(evt);
			if (evt is NavigationSubmitEvent && enabledInHierarchy)
			{
				ToggleValue();
				evt.StopPropagation();
			}
		}

		protected virtual void ToggleValue()
		{
			value = !value;
		}

		private void OnClick()
		{
			ToggleValue();
		}
	}

	/// <summary>One choice of a group: selecting it deselects the other radio buttons of the same group (or parent).</summary>
	[UxmlElement]
	public class RadioButton : BaseField<bool>
	{
		public new static readonly string ussClassName = "unity-radio-button";
		public new static readonly string labelUssClassName = ussClassName + "__label";
		public new static readonly string inputUssClassName = ussClassName + "__input";
		public static readonly string checkmarkBackgroundUssClassName = ussClassName + "__checkmark-background";
		public static readonly string checkmarkUssClassName = ussClassName + "__checkmark";
		public static readonly string textUssClassName = ussClassName + "__text";

		private readonly Label textLabel;

		[UxmlAttribute]
		public string text
		{
			get => textLabel.text;
			set
			{
				textLabel.text = value ?? "";
				textLabel.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.None : DisplayStyle.Flex;
			}
		}

		public RadioButton()
			: this(null)
		{ }

		public RadioButton(string label)
			: base(label, new VisualElement { pickingMode = PickingMode.Ignore })
		{
			AddToClassList(ussClassName);
			labelElement.AddToClassList(labelUssClassName);
			visualInput.AddToClassList(inputUssClassName);
			var background = new VisualElement { pickingMode = PickingMode.Ignore };
			background.AddToClassList(checkmarkBackgroundUssClassName);
			var checkmark = new VisualElement { pickingMode = PickingMode.Ignore };
			checkmark.AddToClassList(checkmarkUssClassName);
			background.Add(checkmark);
			visualInput.Add(background);
			textLabel = new Label { pickingMode = PickingMode.Ignore };
			textLabel.AddToClassList(textUssClassName);
			visualInput.Add(textLabel);
			text = null;
			this.AddManipulator(new Clickable(() => value = true));
		}

		public override void SetValueWithoutNotify(bool newValue)
		{
			base.SetValueWithoutNotify(newValue);
			if (newValue)
			{
				DeselectSiblings();
			}
		}

		protected override void UpdateDisplay()
		{
			if (rawValue)
			{
				pseudoStates |= PseudoStates.Checked;
			}
			else
			{
				pseudoStates &= ~PseudoStates.Checked;
			}
		}

		protected override void ExecuteDefaultActionAtTarget(EventBase evt)
		{
			base.ExecuteDefaultActionAtTarget(evt);
			if (evt is NavigationSubmitEvent && enabledInHierarchy)
			{
				value = true;
				evt.StopPropagation();
			}
		}

		private void DeselectSiblings()
		{
			VisualElement container = GetFirstAncestorOfType<RadioButtonGroup>()?.container ?? hierarchy.parent;
			if (container == null)
			{
				return;
			}

			foreach (VisualElement child in container.Children())
			{
				if (child is RadioButton other && other != this && other.rawValue)
				{
					other.SetValueWithoutNotify(false);
				}
			}
		}
	}

	/// <summary>A set of radio buttons built from <see cref="choices" />; the value is the selected index (-1: none).</summary>
	[UxmlElement]
	public class RadioButtonGroup : BaseField<int>
	{
		public new static readonly string ussClassName = "unity-radio-button-group";
		public static readonly string containerUssClassName = ussClassName + "__container";

		internal readonly VisualElement container;
		private List<string> m_Choices = new List<string>();
		private readonly List<RadioButton> buttons = new List<RadioButton>();

		[UxmlAttribute]
		public List<string> choices
		{
			get => m_Choices;
			set
			{
				m_Choices = value ?? new List<string>();
				RebuildButtons();
			}
		}

		public RadioButtonGroup()
			: this(null, null)
		{ }

		public RadioButtonGroup(string label, List<string> choices)
			: base(label, new VisualElement { pickingMode = PickingMode.Ignore })
		{
			AddToClassList(ussClassName);
			container = visualInput;
			container.AddToClassList(containerUssClassName);
			rawValue = -1;
			this.choices = choices ?? new List<string>();
		}

		protected override void UpdateDisplay()
		{
			for (int i = 0; i < buttons.Count; i++)
			{
				buttons[i].SetValueWithoutNotify(i == rawValue);
			}
		}

		private void RebuildButtons()
		{
			container.Clear();
			buttons.Clear();
			for (int i = 0; i < m_Choices.Count; i++)
			{
				int index = i;
				var button = new RadioButton { text = m_Choices[i] };
				button.RegisterValueChangedCallback(evt =>
				{
					if (evt.newValue)
					{
						value = index;
					}
				});
				buttons.Add(button);
				container.Add(button);
			}

			UpdateDisplay();
		}
	}

	/// <summary>A bar filling from <see cref="lowValue" /> to <see cref="highValue" /> with an optional title.</summary>
	[UxmlElement]
	public class ProgressBar : BaseField<float>
	{
		public new static readonly string ussClassName = "unity-progress-bar";
		public static readonly string containerUssClassName = ussClassName + "__container";
		public static readonly string backgroundUssClassName = ussClassName + "__background";
		public static readonly string progressUssClassName = ussClassName + "__progress";
		public static readonly string titleContainerUssClassName = ussClassName + "__title-container";
		public static readonly string titleUssClassName = ussClassName + "__title";

		private readonly VisualElement progress;
		private readonly Label titleLabel;
		private float m_LowValue;
		private float m_HighValue = 100f;

		[UxmlAttribute]
		public string title
		{
			get => titleLabel.text;
			set => titleLabel.text = value ?? "";
		}

		[UxmlAttribute("low-value")]
		public float lowValue
		{
			get => m_LowValue;
			set
			{
				m_LowValue = value;
				UpdateDisplay();
			}
		}

		[UxmlAttribute("high-value")]
		public float highValue
		{
			get => m_HighValue;
			set
			{
				m_HighValue = value;
				UpdateDisplay();
			}
		}

		public ProgressBar()
			: base(null, new VisualElement { pickingMode = PickingMode.Ignore })
		{
			AddToClassList(ussClassName);
			VisualElement container = visualInput;
			container.AddToClassList(containerUssClassName);
			var background = new VisualElement { pickingMode = PickingMode.Ignore };
			background.AddToClassList(backgroundUssClassName);
			progress = new VisualElement { pickingMode = PickingMode.Ignore };
			progress.AddToClassList(progressUssClassName);
			background.Add(progress);
			var titleContainer = new VisualElement { pickingMode = PickingMode.Ignore };
			titleContainer.AddToClassList(titleContainerUssClassName);
			titleLabel = new Label { pickingMode = PickingMode.Ignore };
			titleLabel.AddToClassList(titleUssClassName);
			titleContainer.Add(titleLabel);
			container.Add(background);
			container.Add(titleContainer);
			focusable = false;
			UpdateDisplay();
		}

		protected override void UpdateDisplay()
		{
			if (progress == null)
			{
				return;
			}

			float range = m_HighValue - m_LowValue;
			float t = range > 0f ? Math.Clamp((rawValue - m_LowValue) / range, 0f, 1f) : 0f;
			progress.style.width = Length.Percent(t * 100f);
		}
	}
}
