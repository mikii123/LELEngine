using System;
using System.Globalization;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>A value typed as text: the field parses on commit (Enter / focus loss) and formats on display.</summary>
	public abstract class TextValueField<TValueType> : BaseField<TValueType>
	{
		#region PublicFields

		public static readonly string textValueFieldUssClassName = "unity-text-value-field";

		/// <summary>The embedded text field.</summary>
		public TextField textField { get; }

		[UxmlAttribute("is-delayed")]
		public bool isDelayed
		{
			get => textField.isDelayed;
			set => textField.isDelayed = value;
		}

		[UxmlAttribute("readonly")]
		public bool isReadOnly
		{
			get => textField.isReadOnly;
			set => textField.isReadOnly = value;
		}

		public string text => textField.text;

		#endregion

		#region Constructors

		protected TextValueField(string label)
			: base(label, new TextField { isDelayed = true })
		{
			AddToClassList(textValueFieldUssClassName);
			AddToClassList(TextField.baseTextFieldUssClassName);
			textField = (TextField)visualInput;
			textField.RemoveFromClassList(BaseField<string>.ussClassName);
			textField.labelElement.style.display = DisplayStyle.None;
			textField.RegisterValueChangedCallback(OnTextChanged);
			delegatesFocus = true;
		}

		#endregion

		#region ProtectedMethods

		protected abstract TValueType StringToValue(string text);
		protected abstract string ValueToString(TValueType value);

		protected override void UpdateDisplay()
		{
			textField.SetValueWithoutNotify(ValueToString(rawValue));
		}

		protected override Focusable GetFocusDelegate()
		{
			return textField;
		}

		#endregion

		#region PrivateMethods

		private void OnTextChanged(ChangeEvent<string> evt)
		{
			evt.StopPropagation();
			try
			{
				value = StringToValue(evt.newValue);
			}
			catch (Exception)
			{
				// Unparsable text: show the previous value again.
				UpdateDisplay();
			}

			UpdateDisplay();
		}

		#endregion
	}

	[UxmlElement]
	public class IntegerField : TextValueField<int>
	{
		public new static readonly string ussClassName = "unity-integer-field";

		public IntegerField()
			: this(null)
		{ }

		public IntegerField(string label)
			: base(label)
		{
			AddToClassList(ussClassName);
			UpdateDisplay();
		}

		protected override int StringToValue(string text)
		{
			double d = double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
			return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
		}

		protected override string ValueToString(int value)
		{
			return value.ToString(CultureInfo.InvariantCulture);
		}
	}

	[UxmlElement]
	public class LongField : TextValueField<long>
	{
		public new static readonly string ussClassName = "unity-long-field";

		public LongField()
			: this(null)
		{ }

		public LongField(string label)
			: base(label)
		{
			AddToClassList(ussClassName);
			UpdateDisplay();
		}

		protected override long StringToValue(string text)
		{
			return (long)Math.Round(double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture));
		}

		protected override string ValueToString(long value)
		{
			return value.ToString(CultureInfo.InvariantCulture);
		}
	}

	[UxmlElement]
	public class FloatField : TextValueField<float>
	{
		public new static readonly string ussClassName = "unity-float-field";

		/// <summary>Digits after the decimal point when formatting (-1: shortest round-trip).</summary>
		public int decimals { get; set; } = -1;

		public FloatField()
			: this(null)
		{ }

		public FloatField(string label)
			: base(label)
		{
			AddToClassList(ussClassName);
			UpdateDisplay();
		}

		protected override float StringToValue(string text)
		{
			return float.Parse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		protected override string ValueToString(float value)
		{
			return decimals >= 0 ? value.ToString("F" + decimals, CultureInfo.InvariantCulture) : value.ToString("0.#####", CultureInfo.InvariantCulture);
		}
	}

	[UxmlElement]
	public class DoubleField : TextValueField<double>
	{
		public new static readonly string ussClassName = "unity-double-field";

		public DoubleField()
			: this(null)
		{ }

		public DoubleField(string label)
			: base(label)
		{
			AddToClassList(ussClassName);
			UpdateDisplay();
		}

		protected override double StringToValue(string text)
		{
			return double.Parse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		protected override string ValueToString(double value)
		{
			return value.ToString("0.########", CultureInfo.InvariantCulture);
		}
	}

	/// <summary>A row of float fields (x, y, ...).</summary>
	public abstract class BaseCompositeField<TValueType> : BaseField<TValueType>
	{
		public static readonly string compositeUssClassName = "unity-composite-field";
		public static readonly string fieldUssClassName = compositeUssClassName + "__field";

		protected readonly FloatField[] fields;
		private bool updating;

		protected BaseCompositeField(string label, string[] componentLabels)
			: base(label, new VisualElement { pickingMode = PickingMode.Ignore })
		{
			AddToClassList(compositeUssClassName);
			visualInput.AddToClassList(compositeUssClassName + "__input");
			fields = new FloatField[componentLabels.Length];
			for (int i = 0; i < componentLabels.Length; i++)
			{
				int index = i;
				var field = new FloatField(componentLabels[i]);
				field.AddToClassList(fieldUssClassName);
				field.RegisterValueChangedCallback(evt =>
				{
					evt.StopPropagation();
					if (!updating)
					{
						value = Compose(index, evt.newValue);
					}
				});
				fields[i] = field;
				visualInput.Add(field);
			}

			delegatesFocus = true;
		}

		protected abstract float Component(TValueType value, int index);
		protected abstract TValueType Compose(int changedIndex, float componentValue);

		protected override void UpdateDisplay()
		{
			if (fields == null)
			{
				return;
			}

			updating = true;
			for (int i = 0; i < fields.Length; i++)
			{
				fields[i].SetValueWithoutNotify(Component(rawValue, i));
			}

			updating = false;
		}

		protected override Focusable GetFocusDelegate()
		{
			return fields.Length > 0 ? fields[0] : null;
		}
	}

	[UxmlElement]
	public class Vector2Field : BaseCompositeField<Vector2>
	{
		public new static readonly string ussClassName = "unity-vector2-field";

		public Vector2Field()
			: this(null)
		{ }

		public Vector2Field(string label)
			: base(label, new[] { "X", "Y" })
		{
			AddToClassList(ussClassName);
			UpdateDisplay();
		}

		protected override float Component(Vector2 value, int index)
		{
			return index == 0 ? value.X : value.Y;
		}

		protected override Vector2 Compose(int changedIndex, float componentValue)
		{
			Vector2 v = rawValue;
			if (changedIndex == 0) v.X = componentValue;
			else v.Y = componentValue;
			return v;
		}
	}

	[UxmlElement]
	public class Vector3Field : BaseCompositeField<Vector3>
	{
		public new static readonly string ussClassName = "unity-vector3-field";

		public Vector3Field()
			: this(null)
		{ }

		public Vector3Field(string label)
			: base(label, new[] { "X", "Y", "Z" })
		{
			AddToClassList(ussClassName);
			UpdateDisplay();
		}

		protected override float Component(Vector3 value, int index)
		{
			return index == 0 ? value.X : index == 1 ? value.Y : value.Z;
		}

		protected override Vector3 Compose(int changedIndex, float componentValue)
		{
			Vector3 v = rawValue;
			if (changedIndex == 0) v.X = componentValue;
			else if (changedIndex == 1) v.Y = componentValue;
			else v.Z = componentValue;
			return v;
		}
	}
}
