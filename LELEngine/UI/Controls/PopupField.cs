using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>A list of actions shown as a floating menu (dropdown fields, context menus).</summary>
	public sealed class GenericDropdownMenu
	{
		public static readonly string ussClassName = "unity-base-dropdown";
		public static readonly string containerOuterUssClassName = ussClassName + "__container-outer";
		public static readonly string itemUssClassName = ussClassName + "__item";
		public static readonly string checkedItemUssClassName = itemUssClassName + "--checked";

		private readonly VisualElement outer;
		private VisualElement root;

		public GenericDropdownMenu()
		{
			outer = new VisualElement { name = "unity-dropdown-menu" };
			outer.AddToClassList(containerOuterUssClassName);
			outer.AddToClassList(ussClassName);
		}

		public void AddItem(string itemName, bool isChecked, Action action)
		{
			var item = new Label(itemName) { pickingMode = PickingMode.Position, focusable = false };
			item.AddToClassList(itemUssClassName);
			item.EnableInClassList(checkedItemUssClassName, isChecked);
			item.RegisterCallback<PointerUpEvent>(evt =>
			{
				if (evt.button == 0)
				{
					Hide();
					action?.Invoke();
					evt.StopPropagation();
				}
			});
			outer.Add(item);
		}

		public void AddDisabledItem(string itemName, bool isChecked)
		{
			var item = new Label(itemName);
			item.AddToClassList(itemUssClassName);
			item.EnableInClassList(checkedItemUssClassName, isChecked);
			item.SetEnabled(false);
			outer.Add(item);
		}

		public void AddSeparator(string path = null)
		{
			var separator = new VisualElement { pickingMode = PickingMode.Ignore };
			separator.AddToClassList(ussClassName + "__separator");
			separator.style.height = 1f;
			separator.style.marginTop = 2f;
			separator.style.marginBottom = 2f;
			separator.style.backgroundColor = new Color4(1f, 1f, 1f, 0.15f);
			outer.Add(separator);
		}

		/// <summary>Shows the menu below (or above) the rectangle, in the element's panel.</summary>
		public void DropDown(Rect position, VisualElement targetElement, bool anchored = true)
		{
			if (targetElement?.panel == null)
			{
				return;
			}

			root = targetElement.panel.visualTree;
			Rect world = targetElement.LocalToWorld(position);
			outer.style.left = world.x;
			outer.style.top = world.yMax;
			outer.style.minWidth = anchored ? Math.Max(world.width, 80f) : 80f;
			root.Add(outer);
			root.RegisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
			outer.RegisterCallback<GeometryChangedEvent>(OnMenuGeometry);
			outer.schedule.Execute(() => KeepInPanel(world)).StartingIn(0);
		}

		public void Hide()
		{
			if (root != null)
			{
				root.UnregisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
				outer.RemoveFromHierarchy();
				root = null;
			}
		}

		private void OnRootPointerDown(PointerDownEvent evt)
		{
			if (!(evt.target is VisualElement target) || !(target == outer || outer.Contains(target)))
			{
				Hide();
			}
		}

		private void OnMenuGeometry(GeometryChangedEvent evt)
		{
			// Flip above the field when the menu runs past the bottom of the panel.
			if (root == null)
			{
				return;
			}

			float panelHeight = root.layout.height;
			if (outer.layout.yMax > panelHeight)
			{
				float top = outer.layout.y;
				outer.style.top = Math.Max(0f, top - outer.layout.height - 24f);
			}
		}

		private void KeepInPanel(Rect world)
		{
			if (root == null)
			{
				return;
			}

			float panelWidth = root.layout.width;
			if (outer.layout.xMax > panelWidth)
			{
				outer.style.left = Math.Max(0f, panelWidth - outer.layout.width);
			}
		}
	}

	/// <summary>A field showing one of several choices in a dropdown menu.</summary>
	public abstract class BasePopupField<TValueType, TValueChoice> : BaseField<TValueType>
	{
		#region PublicFields

		public new static readonly string ussClassName = "unity-base-popup-field";
		public new static readonly string labelUssClassName = ussClassName + "__label";
		public new static readonly string inputUssClassName = ussClassName + "__input";
		public static readonly string textUssClassName = ussClassName + "__text";
		public static readonly string arrowUssClassName = ussClassName + "__arrow";

		public virtual List<TValueChoice> choices
		{
			get => m_Choices;
			set
			{
				m_Choices = value ?? new List<TValueChoice>();
				UpdateDisplay();
			}
		}

		public string text => textElement.text;

		public Func<TValueChoice, string> formatSelectedValueCallback { get; set; }
		public Func<TValueChoice, string> formatListItemCallback { get; set; }

		#endregion

		#region PrivateFields

		protected readonly TextElement textElement;
		private List<TValueChoice> m_Choices = new List<TValueChoice>();

		#endregion

		#region Constructors

		protected BasePopupField(string label)
			: base(label, new VisualElement { pickingMode = PickingMode.Position })
		{
			AddToClassList(ussClassName);
			labelElement.AddToClassList(labelUssClassName);
			visualInput.AddToClassList(inputUssClassName);
			textElement = new TextElement { pickingMode = PickingMode.Ignore };
			textElement.AddToClassList(textUssClassName);
			var arrow = new VisualElement { pickingMode = PickingMode.Ignore };
			arrow.AddToClassList(arrowUssClassName);
			visualInput.Add(textElement);
			visualInput.Add(arrow);
			visualInput.RegisterCallback<PointerDownEvent>(evt =>
			{
				if (evt.button == 0 && enabledInHierarchy)
				{
					Focus();
					ShowMenu();
					evt.StopPropagation();
				}
			});
		}

		#endregion

		#region PublicMethods

		public void ShowMenu()
		{
			var menu = new GenericDropdownMenu();
			AddMenuItems(menu);
			menu.DropDown(new Rect(0f, 0f, visualInput.layout.width, visualInput.layout.height), visualInput);
		}

		#endregion

		#region ProtectedMethods

		protected abstract string GetValueToDisplay();
		protected abstract void AddMenuItems(GenericDropdownMenu menu);

		protected override void UpdateDisplay()
		{
			if (textElement != null)
			{
				textElement.text = GetValueToDisplay() ?? "";
			}
		}

		protected override void ExecuteDefaultActionAtTarget(EventBase evt)
		{
			base.ExecuteDefaultActionAtTarget(evt);
			if (evt is NavigationSubmitEvent && enabledInHierarchy)
			{
				ShowMenu();
				evt.StopPropagation();
			}
		}

		protected string FormatChoice(TValueChoice choice, bool forList)
		{
			Func<TValueChoice, string> formatter = forList ? formatListItemCallback : formatSelectedValueCallback;
			return formatter != null ? formatter(choice) : choice?.ToString() ?? "";
		}

		#endregion
	}

	/// <summary>A dropdown choosing one value out of a list of values.</summary>
	public class PopupField<T> : BasePopupField<T, T>
	{
		public new static readonly string ussClassName = "unity-popup-field";

		public int index
		{
			get => choices.IndexOf(value);
			set
			{
				if (value >= 0 && value < choices.Count)
				{
					this.value = choices[value];
				}
			}
		}

		public PopupField()
			: this(null)
		{ }

		public PopupField(string label)
			: base(label)
		{
			AddToClassList(ussClassName);
		}

		public PopupField(List<T> choices, T defaultValue, Func<T, string> formatSelectedValueCallback = null, Func<T, string> formatListItemCallback = null)
			: this(null, choices, defaultValue, formatSelectedValueCallback, formatListItemCallback)
		{ }

		public PopupField(string label, List<T> choices, T defaultValue, Func<T, string> formatSelectedValueCallback = null, Func<T, string> formatListItemCallback = null)
			: this(label)
		{
			this.choices = choices;
			this.formatSelectedValueCallback = formatSelectedValueCallback;
			this.formatListItemCallback = formatListItemCallback;
			SetValueWithoutNotify(defaultValue);
		}

		public PopupField(List<T> choices, int defaultIndex, Func<T, string> formatSelectedValueCallback = null, Func<T, string> formatListItemCallback = null)
			: this(null)
		{
			this.choices = choices;
			this.formatSelectedValueCallback = formatSelectedValueCallback;
			this.formatListItemCallback = formatListItemCallback;
			if (defaultIndex >= 0 && defaultIndex < choices.Count)
			{
				SetValueWithoutNotify(choices[defaultIndex]);
			}
		}

		protected override string GetValueToDisplay()
		{
			return rawValue == null ? "" : FormatChoice(rawValue, false);
		}

		protected override void AddMenuItems(GenericDropdownMenu menu)
		{
			foreach (T choice in choices)
			{
				T captured = choice;
				menu.AddItem(FormatChoice(choice, true), EqualityComparer<T>.Default.Equals(choice, rawValue), () => value = captured);
			}
		}
	}

	/// <summary>A dropdown of strings (UXML: choices="A,B,C").</summary>
	[UxmlElement]
	public class DropdownField : PopupField<string>
	{
		public new static readonly string ussClassName = "unity-dropdown-field";

		[UxmlAttribute]
		public override List<string> choices
		{
			get => base.choices;
			set => base.choices = value;
		}

		[UxmlAttribute]
		public new int index
		{
			get => base.index;
			set => base.index = value;
		}

		public DropdownField()
			: this(null)
		{ }

		public DropdownField(string label)
			: base(label)
		{
			AddToClassList(ussClassName);
		}

		public DropdownField(List<string> choices, int defaultIndex, Func<string, string> formatSelectedValueCallback = null, Func<string, string> formatListItemCallback = null)
			: base(choices, defaultIndex, formatSelectedValueCallback, formatListItemCallback)
		{
			AddToClassList(ussClassName);
		}

		public DropdownField(string label, List<string> choices, int defaultIndex, Func<string, string> formatSelectedValueCallback = null, Func<string, string> formatListItemCallback = null)
			: base(label, choices, defaultIndex >= 0 && defaultIndex < choices.Count ? choices[defaultIndex] : null, formatSelectedValueCallback, formatListItemCallback)
		{
			AddToClassList(ussClassName);
		}
	}

	/// <summary>A dropdown over the values of an enum (UXML: type="MyGame.Mode, MyGame" value="Fast").</summary>
	[UxmlElement]
	public class EnumField : BasePopupField<Enum, Enum>
	{
		public new static readonly string ussClassName = "unity-enum-field";

		private Type enumType;

		/// <summary>The enum type as an assembly-qualified name (UXML).</summary>
		[UxmlAttribute("type")]
		public string enumTypeName
		{
			get => enumType != null ? TypeRegistry.GetName(enumType) : null;
			set
			{
				Type type = TypeRegistry.Resolve(value);
				if (type != null && type.IsEnum)
				{
					Init((Enum)Enum.GetValues(type).GetValue(0));
				}
			}
		}

		[UxmlAttribute("value")]
		public string valueName
		{
			get => rawValue?.ToString();
			set
			{
				if (enumType != null && value != null)
				{
					try
					{
						this.value = (Enum)Enum.Parse(enumType, value, true);
					}
					catch (Exception)
					{
						// unknown name: keep the current value
					}
				}
			}
		}

		public EnumField()
			: this((string)null)
		{ }

		public EnumField(string label)
			: base(label)
		{
			AddToClassList(ussClassName);
		}

		public EnumField(Enum defaultValue)
			: this(null, defaultValue)
		{ }

		public EnumField(string label, Enum defaultValue)
			: this(label)
		{
			Init(defaultValue);
		}

		public void Init(Enum defaultValue)
		{
			if (defaultValue == null)
			{
				return;
			}

			enumType = defaultValue.GetType();
			var list = new List<Enum>();
			foreach (object v in Enum.GetValues(enumType))
			{
				list.Add((Enum)v);
			}

			choices = list;
			SetValueWithoutNotify(defaultValue);
		}

		protected override string GetValueToDisplay()
		{
			return rawValue == null ? "" : FormatChoice(rawValue, false);
		}

		protected override void AddMenuItems(GenericDropdownMenu menu)
		{
			foreach (Enum choice in choices)
			{
				Enum captured = choice;
				menu.AddItem(FormatChoice(choice, true), Equals(choice, rawValue), () => value = captured);
			}
		}
	}
}
