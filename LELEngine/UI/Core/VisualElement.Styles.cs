using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>Read access to the values the cascade and layout resolved for an element (pixels, final colors).</summary>
	public interface IResolvedStyle
	{
		Align alignContent { get; }
		Align alignItems { get; }
		Align alignSelf { get; }
		Color4 backgroundColor { get; }
		Background backgroundImage { get; }
		BackgroundPosition backgroundPositionX { get; }
		BackgroundPosition backgroundPositionY { get; }
		BackgroundRepeat backgroundRepeat { get; }
		BackgroundSize backgroundSize { get; }
		Color4 borderBottomColor { get; }
		float borderBottomLeftRadius { get; }
		float borderBottomRightRadius { get; }
		float borderBottomWidth { get; }
		Color4 borderLeftColor { get; }
		float borderLeftWidth { get; }
		Color4 borderRightColor { get; }
		float borderRightWidth { get; }
		Color4 borderTopColor { get; }
		float borderTopLeftRadius { get; }
		float borderTopRightRadius { get; }
		float borderTopWidth { get; }
		float bottom { get; }
		Color4 color { get; }
		DisplayStyle display { get; }
		StyleFloat flexBasis { get; }
		FlexDirection flexDirection { get; }
		float flexGrow { get; }
		float flexShrink { get; }
		Wrap flexWrap { get; }
		float fontSize { get; }
		float height { get; }
		Justify justifyContent { get; }
		float left { get; }
		float letterSpacing { get; }
		float marginBottom { get; }
		float marginLeft { get; }
		float marginRight { get; }
		float marginTop { get; }
		StyleFloat maxHeight { get; }
		StyleFloat maxWidth { get; }
		StyleFloat minHeight { get; }
		StyleFloat minWidth { get; }
		float opacity { get; }
		float paddingBottom { get; }
		float paddingLeft { get; }
		float paddingRight { get; }
		float paddingTop { get; }
		Position position { get; }
		float right { get; }
		Rotate rotate { get; }
		Scale scale { get; }
		TextOverflow textOverflow { get; }
		float top { get; }
		Vector3 transformOrigin { get; }
		IEnumerable<TimeValue> transitionDelay { get; }
		IEnumerable<TimeValue> transitionDuration { get; }
		IEnumerable<StylePropertyName> transitionProperty { get; }
		IEnumerable<EasingFunction> transitionTimingFunction { get; }
		Vector3 translate { get; }
		Color4 unityBackgroundImageTintColor { get; }
		ScaleMode unityBackgroundScaleMode { get; }
		FontAsset unityFont { get; }
		FontDefinition unityFontDefinition { get; }
		FontStyle unityFontStyleAndWeight { get; }
		float unityParagraphSpacing { get; }
		int unitySliceBottom { get; }
		int unitySliceLeft { get; }
		int unitySliceRight { get; }
		float unitySliceScale { get; }
		int unitySliceTop { get; }
		TextAnchor unityTextAlign { get; }
		Color4 unityTextOutlineColor { get; }
		float unityTextOutlineWidth { get; }
		TextOverflowPosition unityTextOverflowPosition { get; }
		Visibility visibility { get; }
		WhiteSpace whiteSpace { get; }
		float width { get; }
		float wordSpacing { get; }
	}

	/// <summary>A typed USS custom property (--name) read through <see cref="ICustomStyle" />.</summary>
	public readonly struct CustomStyleProperty<T>
	{
		public string name { get; }

		public CustomStyleProperty(string name)
		{
			if (string.IsNullOrEmpty(name) || !name.StartsWith("--", StringComparison.Ordinal))
			{
				throw new ArgumentException("Custom property names start with --", nameof(name));
			}

			this.name = name;
		}
	}

	/// <summary>Reads the USS custom properties (--name: value) that apply to an element.</summary>
	public interface ICustomStyle
	{
		bool TryGetValue(CustomStyleProperty<float> property, out float value);
		bool TryGetValue(CustomStyleProperty<int> property, out int value);
		bool TryGetValue(CustomStyleProperty<bool> property, out bool value);
		bool TryGetValue(CustomStyleProperty<Color4> property, out Color4 value);
		bool TryGetValue(CustomStyleProperty<Texture> property, out Texture value);
		bool TryGetValue(CustomStyleProperty<FontAsset> property, out FontAsset value);
		bool TryGetValue(CustomStyleProperty<string> property, out string value);
	}

	/// <summary>The style sheets attached to one element (they apply to it and its descendants).</summary>
	public readonly struct VisualElementStyleSheetSet
	{
		private readonly VisualElement element;

		internal VisualElementStyleSheetSet(VisualElement element)
		{
			this.element = element;
		}

		public int count => element.styleSheetList?.Count ?? 0;

		public StyleSheet this[int index] => element.styleSheetList[index];

		public void Add(StyleSheet styleSheet)
		{
			if (styleSheet == null)
			{
				throw new ArgumentNullException(nameof(styleSheet));
			}

			element.styleSheetList ??= new List<StyleSheet>(2);
			if (element.styleSheetList.Contains(styleSheet))
			{
				return;
			}

			element.styleSheetList.Add(styleSheet);
			element.OnStyleSheetsChanged();
		}

		public bool Remove(StyleSheet styleSheet)
		{
			if (element.styleSheetList != null && element.styleSheetList.Remove(styleSheet))
			{
				element.OnStyleSheetsChanged();
				return true;
			}

			return false;
		}

		public bool Contains(StyleSheet styleSheet)
		{
			return element.styleSheetList != null && element.styleSheetList.Contains(styleSheet);
		}

		public void Clear()
		{
			if (element.styleSheetList != null && element.styleSheetList.Count > 0)
			{
				element.styleSheetList.Clear();
				element.OnStyleSheetsChanged();
			}
		}
	}

	public partial class VisualElement : IResolvedStyle, ICustomStyle
	{
		#region PublicFields

		/// <summary>Inline style: values set here override the style sheets.</summary>
		public IStyle style => inlineStyle ??= new InlineStyle(this);

		/// <summary>The values resolved for this element (after the cascade, inline style, transitions and layout).</summary>
		public IResolvedStyle resolvedStyle => this;

		/// <summary>USS custom properties (--name) of this element.</summary>
		public ICustomStyle customStyle => this;

		public VisualElementStyleSheetSet styleSheets => new VisualElementStyleSheetSet(this);

		#endregion

		#region InternalMethods

		internal void OnStyleSheetsChanged()
		{
			elementPanel?.OnStyleSheetsChanged();
			IncrementVersion(VersionChangeType.StyleSheet | VersionChangeType.Styles);
			// Descendants match against the new sheet set too.
			ForEachDescendant(e => e.IncrementVersion(VersionChangeType.Styles));
		}

		/// <summary>Resolved value of a length against the element's own width / height (percent) in pixels.</summary>
		internal float ResolveLength(Length length, float reference)
		{
			float value = length.Resolve(reference);
			return float.IsNaN(value) ? 0f : value;
		}

		#endregion

		#region IResolvedStyle

		Align IResolvedStyle.alignContent => computedStyle.alignContent;
		Align IResolvedStyle.alignItems => computedStyle.alignItems;
		Align IResolvedStyle.alignSelf => computedStyle.alignSelf;
		Color4 IResolvedStyle.backgroundColor => computedStyle.backgroundColor;
		Background IResolvedStyle.backgroundImage => computedStyle.backgroundImage;
		BackgroundPosition IResolvedStyle.backgroundPositionX => computedStyle.backgroundPositionX;
		BackgroundPosition IResolvedStyle.backgroundPositionY => computedStyle.backgroundPositionY;
		BackgroundRepeat IResolvedStyle.backgroundRepeat => computedStyle.backgroundRepeat;
		BackgroundSize IResolvedStyle.backgroundSize => computedStyle.backgroundSize;
		Color4 IResolvedStyle.borderBottomColor => computedStyle.borderBottomColor;
		float IResolvedStyle.borderBottomLeftRadius => ResolveLength(computedStyle.borderBottomLeftRadius, Math.Min(layoutNode.rect.width, layoutNode.rect.height));
		float IResolvedStyle.borderBottomRightRadius => ResolveLength(computedStyle.borderBottomRightRadius, Math.Min(layoutNode.rect.width, layoutNode.rect.height));
		float IResolvedStyle.borderBottomWidth => computedStyle.borderBottomWidth;
		Color4 IResolvedStyle.borderLeftColor => computedStyle.borderLeftColor;
		float IResolvedStyle.borderLeftWidth => computedStyle.borderLeftWidth;
		Color4 IResolvedStyle.borderRightColor => computedStyle.borderRightColor;
		float IResolvedStyle.borderRightWidth => computedStyle.borderRightWidth;
		Color4 IResolvedStyle.borderTopColor => computedStyle.borderTopColor;
		float IResolvedStyle.borderTopLeftRadius => ResolveLength(computedStyle.borderTopLeftRadius, Math.Min(layoutNode.rect.width, layoutNode.rect.height));
		float IResolvedStyle.borderTopRightRadius => ResolveLength(computedStyle.borderTopRightRadius, Math.Min(layoutNode.rect.width, layoutNode.rect.height));
		float IResolvedStyle.borderTopWidth => computedStyle.borderTopWidth;
		float IResolvedStyle.bottom => m_PhysicalParent != null ? m_PhysicalParent.layoutNode.rect.height - layoutNode.rect.yMax : 0f;
		Color4 IResolvedStyle.color => computedStyle.inherited.color;
		DisplayStyle IResolvedStyle.display => computedStyle.display;
		StyleFloat IResolvedStyle.flexBasis => computedStyle.flexBasis.IsAuto ? new StyleFloat(StyleKeyword.Auto) : new StyleFloat(ResolveLength(computedStyle.flexBasis, m_PhysicalParent?.layoutNode.rect.width ?? 0f));
		FlexDirection IResolvedStyle.flexDirection => computedStyle.flexDirection;
		float IResolvedStyle.flexGrow => computedStyle.flexGrow;
		float IResolvedStyle.flexShrink => computedStyle.flexShrink;
		Wrap IResolvedStyle.flexWrap => computedStyle.flexWrap;
		float IResolvedStyle.fontSize => ResolveLength(computedStyle.inherited.fontSize, 12f);
		float IResolvedStyle.height => layout.height;
		Justify IResolvedStyle.justifyContent => computedStyle.justifyContent;
		float IResolvedStyle.left => layout.x;
		float IResolvedStyle.letterSpacing => ResolveLength(computedStyle.inherited.letterSpacing, 0f);
		float IResolvedStyle.marginBottom => layoutNode.marginBottom;
		float IResolvedStyle.marginLeft => layoutNode.marginLeft;
		float IResolvedStyle.marginRight => layoutNode.marginRight;
		float IResolvedStyle.marginTop => layoutNode.marginTop;
		StyleFloat IResolvedStyle.maxHeight => computedStyle.maxHeight.IsNone ? new StyleFloat(StyleKeyword.None) : new StyleFloat(ResolveLength(computedStyle.maxHeight, m_PhysicalParent?.layoutNode.rect.height ?? 0f));
		StyleFloat IResolvedStyle.maxWidth => computedStyle.maxWidth.IsNone ? new StyleFloat(StyleKeyword.None) : new StyleFloat(ResolveLength(computedStyle.maxWidth, m_PhysicalParent?.layoutNode.rect.width ?? 0f));
		StyleFloat IResolvedStyle.minHeight => computedStyle.minHeight.IsAuto ? new StyleFloat(StyleKeyword.Auto) : new StyleFloat(ResolveLength(computedStyle.minHeight, m_PhysicalParent?.layoutNode.rect.height ?? 0f));
		StyleFloat IResolvedStyle.minWidth => computedStyle.minWidth.IsAuto ? new StyleFloat(StyleKeyword.Auto) : new StyleFloat(ResolveLength(computedStyle.minWidth, m_PhysicalParent?.layoutNode.rect.width ?? 0f));
		float IResolvedStyle.opacity => computedStyle.opacity;
		float IResolvedStyle.paddingBottom => layoutNode.paddingBottom;
		float IResolvedStyle.paddingLeft => layoutNode.paddingLeft;
		float IResolvedStyle.paddingRight => layoutNode.paddingRight;
		float IResolvedStyle.paddingTop => layoutNode.paddingTop;
		Position IResolvedStyle.position => computedStyle.position;
		float IResolvedStyle.right => m_PhysicalParent != null ? m_PhysicalParent.layoutNode.rect.width - layoutNode.rect.xMax : 0f;
		Rotate IResolvedStyle.rotate => computedStyle.rotate;
		Scale IResolvedStyle.scale => computedStyle.scale;
		TextOverflow IResolvedStyle.textOverflow => computedStyle.textOverflow;
		float IResolvedStyle.top => layout.y;
		Vector3 IResolvedStyle.transformOrigin => new Vector3(ResolveLength(computedStyle.transformOrigin.x, layoutNode.rect.width), ResolveLength(computedStyle.transformOrigin.y, layoutNode.rect.height), 0f);
		IEnumerable<TimeValue> IResolvedStyle.transitionDelay => computedStyle.transitionDelay ?? (IEnumerable<TimeValue>)Array.Empty<TimeValue>();
		IEnumerable<TimeValue> IResolvedStyle.transitionDuration => computedStyle.transitionDuration ?? (IEnumerable<TimeValue>)Array.Empty<TimeValue>();
		IEnumerable<StylePropertyName> IResolvedStyle.transitionProperty => computedStyle.transitionProperty ?? (IEnumerable<StylePropertyName>)Array.Empty<StylePropertyName>();
		IEnumerable<EasingFunction> IResolvedStyle.transitionTimingFunction => computedStyle.transitionTimingFunction ?? (IEnumerable<EasingFunction>)Array.Empty<EasingFunction>();
		Vector3 IResolvedStyle.translate => new Vector3(ResolveLength(computedStyle.translate.x, layoutNode.rect.width), ResolveLength(computedStyle.translate.y, layoutNode.rect.height), 0f);
		Color4 IResolvedStyle.unityBackgroundImageTintColor => computedStyle.unityBackgroundImageTintColor;
		ScaleMode IResolvedStyle.unityBackgroundScaleMode => computedStyle.unityBackgroundScaleMode;
		FontAsset IResolvedStyle.unityFont => computedStyle.inherited.unityFont;
		FontDefinition IResolvedStyle.unityFontDefinition => computedStyle.inherited.unityFontDefinition;
		FontStyle IResolvedStyle.unityFontStyleAndWeight => computedStyle.inherited.unityFontStyleAndWeight;
		float IResolvedStyle.unityParagraphSpacing => ResolveLength(computedStyle.inherited.unityParagraphSpacing, 0f);
		int IResolvedStyle.unitySliceBottom => computedStyle.unitySliceBottom;
		int IResolvedStyle.unitySliceLeft => computedStyle.unitySliceLeft;
		int IResolvedStyle.unitySliceRight => computedStyle.unitySliceRight;
		float IResolvedStyle.unitySliceScale => computedStyle.unitySliceScale;
		int IResolvedStyle.unitySliceTop => computedStyle.unitySliceTop;
		TextAnchor IResolvedStyle.unityTextAlign => computedStyle.inherited.unityTextAlign;
		Color4 IResolvedStyle.unityTextOutlineColor => computedStyle.inherited.unityTextOutlineColor;
		float IResolvedStyle.unityTextOutlineWidth => computedStyle.inherited.unityTextOutlineWidth;
		TextOverflowPosition IResolvedStyle.unityTextOverflowPosition => computedStyle.inherited.unityTextOverflowPosition;
		Visibility IResolvedStyle.visibility => computedStyle.inherited.visibility;
		WhiteSpace IResolvedStyle.whiteSpace => computedStyle.inherited.whiteSpace;
		float IResolvedStyle.width => layout.width;
		float IResolvedStyle.wordSpacing => ResolveLength(computedStyle.inherited.wordSpacing, 0f);

		#endregion

		#region ICustomStyle

		private StyleValue[] CustomValues(string name)
		{
			StyleValue[] values = computedStyle.inherited.GetCustomProperty(name);
			return values != null && values.Length > 0 ? values : null;
		}

		bool ICustomStyle.TryGetValue(CustomStyleProperty<float> property, out float value)
		{
			StyleValue[] values = CustomValues(property.name);
			if (values != null && values[0].IsNumber)
			{
				value = values[0].number;
				return true;
			}

			value = 0f;
			return false;
		}

		bool ICustomStyle.TryGetValue(CustomStyleProperty<int> property, out int value)
		{
			StyleValue[] values = CustomValues(property.name);
			if (values != null && values[0].IsNumber)
			{
				value = (int)values[0].number;
				return true;
			}

			value = 0;
			return false;
		}

		bool ICustomStyle.TryGetValue(CustomStyleProperty<bool> property, out bool value)
		{
			StyleValue[] values = CustomValues(property.name);
			if (values != null && values[0].type == StyleValueType.Identifier && bool.TryParse(values[0].text, out value))
			{
				return true;
			}

			value = false;
			return false;
		}

		bool ICustomStyle.TryGetValue(CustomStyleProperty<Color4> property, out Color4 value)
		{
			StyleValue[] values = CustomValues(property.name);
			if (values != null && values[0].type == StyleValueType.Color)
			{
				value = values[0].color;
				return true;
			}

			value = default;
			return false;
		}

		bool ICustomStyle.TryGetValue(CustomStyleProperty<Texture> property, out Texture value)
		{
			StyleValue[] values = CustomValues(property.name);
			value = values?[0].reference as Texture;
			return value != null;
		}

		bool ICustomStyle.TryGetValue(CustomStyleProperty<FontAsset> property, out FontAsset value)
		{
			StyleValue[] values = CustomValues(property.name);
			value = values?[0].reference as FontAsset;
			return value != null;
		}

		bool ICustomStyle.TryGetValue(CustomStyleProperty<string> property, out string value)
		{
			StyleValue[] values = CustomValues(property.name);
			if (values != null)
			{
				value = values.Length == 1 && (values[0].type == StyleValueType.String || values[0].type == StyleValueType.Identifier) ? values[0].text : StyleValue.ToString(values);
				return true;
			}

			value = null;
			return false;
		}

		#endregion
	}
}
