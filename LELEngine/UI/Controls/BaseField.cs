using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>
	///     A labeled control with a value: a label on the left and the control's input on the right. Setting
	///     <see cref="value" /> sends a <see cref="ChangeEvent{T}" />; <see cref="SetValueWithoutNotify" /> does not.
	/// </summary>
	public abstract class BaseField<TValueType> : VisualElement, INotifyValueChanged<TValueType>
	{
		#region PublicFields

		public static readonly string ussClassName = "unity-base-field";
		public static readonly string labelUssClassName = ussClassName + "__label";
		public static readonly string inputUssClassName = ussClassName + "__input";
		public static readonly string noLabelVariantUssClassName = ussClassName + "--no-label";
		public static readonly string labelDraggerVariantUssClassName = ussClassName + "__label--with-dragger";
		public static readonly string alignedFieldUssClassName = ussClassName + "__aligned";

		public Label labelElement { get; }

		[UxmlAttribute]
		public string label
		{
			get => labelElement.text;
			set
			{
				labelElement.text = value ?? "";
				EnableInClassList(noLabelVariantUssClassName, string.IsNullOrEmpty(value));
			}
		}

		[UxmlAttribute]
		public virtual TValueType value
		{
			get => rawValue;
			set
			{
				if (EqualityComparer<TValueType>.Default.Equals(rawValue, value))
				{
					return;
				}

				TValueType previous = rawValue;
				if (panel != null)
				{
					using (ChangeEvent<TValueType> evt = ChangeEvent<TValueType>.GetPooled(previous, value))
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

		/// <summary>The value as stored, without any clamping or change events.</summary>
		public TValueType rawValue
		{
			get => m_Value;
			protected set => m_Value = value;
		}

		/// <summary>The element holding the control's visuals (right of the label).</summary>
		protected VisualElement visualInput { get; private set; }

		#endregion

		#region PrivateFields

		private TValueType m_Value;

		#endregion

		#region Constructors

		protected BaseField(string label, VisualElement visualInput)
		{
			AddToClassList(ussClassName);
			focusable = true;
			tabIndex = 0;
			labelElement = new Label { name = "unity-label", pickingMode = PickingMode.Ignore };
			labelElement.AddToClassList(labelUssClassName);
			hierarchy.Add(labelElement);
			this.label = label;
			if (visualInput != null)
			{
				SetVisualInput(visualInput);
			}
		}

		#endregion

		#region PublicMethods

		public virtual void SetValueWithoutNotify(TValueType newValue)
		{
			m_Value = newValue;
			UpdateDisplay();
		}

		#endregion

		#region ProtectedMethods

		/// <summary>Refreshes the visuals from <see cref="rawValue" />.</summary>
		protected virtual void UpdateDisplay()
		{ }

		protected void SetVisualInput(VisualElement input)
		{
			if (visualInput != null)
			{
				visualInput.RemoveFromClassList(inputUssClassName);
				visualInput.RemoveFromHierarchy();
			}

			visualInput = input;
			if (input != null)
			{
				input.AddToClassList(inputUssClassName);
				hierarchy.Add(input);
			}
		}

		#endregion
	}
}
