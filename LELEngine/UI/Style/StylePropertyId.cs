using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>Every USS property the style system knows (shorthands expand into these at apply time).</summary>
	public enum StylePropertyId
	{
		Unknown = 0,

		// Layout
		AlignContent,
		AlignItems,
		AlignSelf,
		Bottom,
		Display,
		FlexBasis,
		FlexDirection,
		FlexGrow,
		FlexShrink,
		FlexWrap,
		Height,
		JustifyContent,
		Left,
		MarginBottom,
		MarginLeft,
		MarginRight,
		MarginTop,
		MaxHeight,
		MaxWidth,
		MinHeight,
		MinWidth,
		PaddingBottom,
		PaddingLeft,
		PaddingRight,
		PaddingTop,
		Position,
		Right,
		Top,
		Width,
		BorderBottomWidth,
		BorderLeftWidth,
		BorderRightWidth,
		BorderTopWidth,

		// Visual
		BackgroundColor,
		BackgroundImage,
		BackgroundPositionX,
		BackgroundPositionY,
		BackgroundRepeat,
		BackgroundSize,
		UnityBackgroundImageTintColor,
		UnityBackgroundScaleMode,
		BorderBottomColor,
		BorderLeftColor,
		BorderRightColor,
		BorderTopColor,
		BorderBottomLeftRadius,
		BorderBottomRightRadius,
		BorderTopLeftRadius,
		BorderTopRightRadius,
		Opacity,
		Overflow,
		UnityOverflowClipBox,
		UnitySliceBottom,
		UnitySliceLeft,
		UnitySliceRight,
		UnitySliceTop,
		UnitySliceScale,
		UnitySliceType,
		TextOverflow,

		// Inherited (text and misc)
		Color,
		Cursor,
		FontSize,
		UnityFont,
		UnityFontDefinition,
		UnityFontStyleAndWeight,
		LetterSpacing,
		WordSpacing,
		WhiteSpace,
		UnityTextAlign,
		UnityParagraphSpacing,
		TextShadow,
		UnityTextOutlineColor,
		UnityTextOutlineWidth,
		UnityTextOverflowPosition,
		Visibility,

		// Transform
		Translate,
		Rotate,
		Scale,
		TransformOrigin,

		// Transitions
		TransitionProperty,
		TransitionDuration,
		TransitionDelay,
		TransitionTimingFunction,

		// Shorthands (never stored; expanded when applied)
		Margin,
		Padding,
		BorderWidth,
		BorderColor,
		BorderRadius,
		Flex,
		Transition,
		BackgroundPosition,
		UnityTextOutline,
		All,

		/// <summary>A --custom property (USS variable); its name is kept with the declaration.</summary>
		Custom,

		Count
	}

	/// <summary>Which parts of an element the change of a property invalidates.</summary>
	[Flags]
	public enum StyleEffect
	{
		None = 0,

		/// <summary>Sizes and positions change (layout pass).</summary>
		Layout = 1,

		/// <summary>Geometry must be regenerated (repaint).</summary>
		Repaint = 2,

		/// <summary>Only the element transform / opacity / clip data changes (no geometry regeneration).</summary>
		Transform = 4,

		/// <summary>Descendants inherit the value: their styles must be recomputed too.</summary>
		Inherited = 8,

		/// <summary>Text measurement changes (text elements re-measure before layout).</summary>
		Text = 16
	}

	/// <summary>How a property's values interpolate in transitions.</summary>
	public enum StyleAnimationKind
	{
		None,
		Float,
		Length,
		Color,
		Translate,
		Rotate,
		Scale,
		TransformOrigin,
		TextShadow
	}

	/// <summary>Static facts about every property: USS name, inheritance, invalidation, interpolation.</summary>
	public static class StylePropertyRegistry
	{
		#region PublicFields

		public static readonly StylePropertyInfo[] Properties = new StylePropertyInfo[(int)StylePropertyId.Count];

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, StylePropertyId> byName = new Dictionary<string, StylePropertyId>(StringComparer.Ordinal);

		#endregion

		#region Constructors

		static StylePropertyRegistry()
		{
			const StyleEffect L = StyleEffect.Layout | StyleEffect.Repaint;
			const StyleEffect R = StyleEffect.Repaint;
			const StyleEffect T = StyleEffect.Transform;
			const StyleEffect I = StyleEffect.Inherited;
			const StyleEffect TX = StyleEffect.Text | StyleEffect.Layout | StyleEffect.Repaint | StyleEffect.Inherited;

			Add(StylePropertyId.AlignContent, "align-content", L);
			Add(StylePropertyId.AlignItems, "align-items", L);
			Add(StylePropertyId.AlignSelf, "align-self", L);
			Add(StylePropertyId.Bottom, "bottom", L, StyleAnimationKind.Length);
			Add(StylePropertyId.Display, "display", L);
			Add(StylePropertyId.FlexBasis, "flex-basis", L, StyleAnimationKind.Length);
			Add(StylePropertyId.FlexDirection, "flex-direction", L);
			Add(StylePropertyId.FlexGrow, "flex-grow", L, StyleAnimationKind.Float);
			Add(StylePropertyId.FlexShrink, "flex-shrink", L, StyleAnimationKind.Float);
			Add(StylePropertyId.FlexWrap, "flex-wrap", L);
			Add(StylePropertyId.Height, "height", L, StyleAnimationKind.Length);
			Add(StylePropertyId.JustifyContent, "justify-content", L);
			Add(StylePropertyId.Left, "left", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MarginBottom, "margin-bottom", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MarginLeft, "margin-left", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MarginRight, "margin-right", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MarginTop, "margin-top", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MaxHeight, "max-height", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MaxWidth, "max-width", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MinHeight, "min-height", L, StyleAnimationKind.Length);
			Add(StylePropertyId.MinWidth, "min-width", L, StyleAnimationKind.Length);
			Add(StylePropertyId.PaddingBottom, "padding-bottom", L, StyleAnimationKind.Length);
			Add(StylePropertyId.PaddingLeft, "padding-left", L, StyleAnimationKind.Length);
			Add(StylePropertyId.PaddingRight, "padding-right", L, StyleAnimationKind.Length);
			Add(StylePropertyId.PaddingTop, "padding-top", L, StyleAnimationKind.Length);
			Add(StylePropertyId.Position, "position", L);
			Add(StylePropertyId.Right, "right", L, StyleAnimationKind.Length);
			Add(StylePropertyId.Top, "top", L, StyleAnimationKind.Length);
			Add(StylePropertyId.Width, "width", L, StyleAnimationKind.Length);
			Add(StylePropertyId.BorderBottomWidth, "border-bottom-width", L, StyleAnimationKind.Float);
			Add(StylePropertyId.BorderLeftWidth, "border-left-width", L, StyleAnimationKind.Float);
			Add(StylePropertyId.BorderRightWidth, "border-right-width", L, StyleAnimationKind.Float);
			Add(StylePropertyId.BorderTopWidth, "border-top-width", L, StyleAnimationKind.Float);

			Add(StylePropertyId.BackgroundColor, "background-color", R, StyleAnimationKind.Color);
			Add(StylePropertyId.BackgroundImage, "background-image", R);
			Add(StylePropertyId.BackgroundPositionX, "background-position-x", R);
			Add(StylePropertyId.BackgroundPositionY, "background-position-y", R);
			Add(StylePropertyId.BackgroundRepeat, "background-repeat", R);
			Add(StylePropertyId.BackgroundSize, "background-size", R);
			Add(StylePropertyId.UnityBackgroundImageTintColor, "-unity-background-image-tint-color", R, StyleAnimationKind.Color);
			Add(StylePropertyId.UnityBackgroundScaleMode, "-unity-background-scale-mode", R);
			Add(StylePropertyId.BorderBottomColor, "border-bottom-color", R, StyleAnimationKind.Color);
			Add(StylePropertyId.BorderLeftColor, "border-left-color", R, StyleAnimationKind.Color);
			Add(StylePropertyId.BorderRightColor, "border-right-color", R, StyleAnimationKind.Color);
			Add(StylePropertyId.BorderTopColor, "border-top-color", R, StyleAnimationKind.Color);
			Add(StylePropertyId.BorderBottomLeftRadius, "border-bottom-left-radius", R, StyleAnimationKind.Length);
			Add(StylePropertyId.BorderBottomRightRadius, "border-bottom-right-radius", R, StyleAnimationKind.Length);
			Add(StylePropertyId.BorderTopLeftRadius, "border-top-left-radius", R, StyleAnimationKind.Length);
			Add(StylePropertyId.BorderTopRightRadius, "border-top-right-radius", R, StyleAnimationKind.Length);
			Add(StylePropertyId.Opacity, "opacity", T, StyleAnimationKind.Float);
			Add(StylePropertyId.Overflow, "overflow", T | StyleEffect.Layout);
			Add(StylePropertyId.UnityOverflowClipBox, "-unity-overflow-clip-box", T);
			Add(StylePropertyId.UnitySliceBottom, "-unity-slice-bottom", R);
			Add(StylePropertyId.UnitySliceLeft, "-unity-slice-left", R);
			Add(StylePropertyId.UnitySliceRight, "-unity-slice-right", R);
			Add(StylePropertyId.UnitySliceTop, "-unity-slice-top", R);
			Add(StylePropertyId.UnitySliceScale, "-unity-slice-scale", R, StyleAnimationKind.Float);
			Add(StylePropertyId.UnitySliceType, "-unity-slice-type", R);
			Add(StylePropertyId.TextOverflow, "text-overflow", R | StyleEffect.Text);

			Add(StylePropertyId.Color, "color", R | I, StyleAnimationKind.Color, true);
			Add(StylePropertyId.Cursor, "cursor", StyleEffect.None | I, StyleAnimationKind.None, true);
			Add(StylePropertyId.FontSize, "font-size", TX, StyleAnimationKind.Length, true);
			Add(StylePropertyId.UnityFont, "-unity-font", TX, StyleAnimationKind.None, true);
			Add(StylePropertyId.UnityFontDefinition, "-unity-font-definition", TX, StyleAnimationKind.None, true);
			Add(StylePropertyId.UnityFontStyleAndWeight, "-unity-font-style", TX, StyleAnimationKind.None, true);
			Add(StylePropertyId.LetterSpacing, "letter-spacing", TX, StyleAnimationKind.Length, true);
			Add(StylePropertyId.WordSpacing, "word-spacing", TX, StyleAnimationKind.Length, true);
			Add(StylePropertyId.WhiteSpace, "white-space", TX, StyleAnimationKind.None, true);
			Add(StylePropertyId.UnityTextAlign, "-unity-text-align", R | I | StyleEffect.Text, StyleAnimationKind.None, true);
			Add(StylePropertyId.UnityParagraphSpacing, "-unity-paragraph-spacing", TX, StyleAnimationKind.Length, true);
			Add(StylePropertyId.TextShadow, "text-shadow", R | I, StyleAnimationKind.TextShadow, true);
			Add(StylePropertyId.UnityTextOutlineColor, "-unity-text-outline-color", R | I, StyleAnimationKind.Color, true);
			Add(StylePropertyId.UnityTextOutlineWidth, "-unity-text-outline-width", R | I, StyleAnimationKind.Float, true);
			Add(StylePropertyId.UnityTextOverflowPosition, "-unity-text-overflow-position", R | I | StyleEffect.Text, StyleAnimationKind.None, true);
			Add(StylePropertyId.Visibility, "visibility", T | I, StyleAnimationKind.None, true);

			Add(StylePropertyId.Translate, "translate", T, StyleAnimationKind.Translate);
			Add(StylePropertyId.Rotate, "rotate", T, StyleAnimationKind.Rotate);
			Add(StylePropertyId.Scale, "scale", T, StyleAnimationKind.Scale);
			Add(StylePropertyId.TransformOrigin, "transform-origin", T, StyleAnimationKind.TransformOrigin);

			Add(StylePropertyId.TransitionProperty, "transition-property", StyleEffect.None);
			Add(StylePropertyId.TransitionDuration, "transition-duration", StyleEffect.None);
			Add(StylePropertyId.TransitionDelay, "transition-delay", StyleEffect.None);
			Add(StylePropertyId.TransitionTimingFunction, "transition-timing-function", StyleEffect.None);

			Add(StylePropertyId.Margin, "margin", L, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.Padding, "padding", L, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.BorderWidth, "border-width", L, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.BorderColor, "border-color", R, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.BorderRadius, "border-radius", R, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.Flex, "flex", L, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.Transition, "transition", StyleEffect.None, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.BackgroundPosition, "background-position", R, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.UnityTextOutline, "-unity-text-outline", R | I, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.All, "all", L | T | I, StyleAnimationKind.None, false, true);
			Add(StylePropertyId.Custom, "--", StyleEffect.Inherited);
		}

		#endregion

		#region PublicMethods

		/// <summary>The property with this USS name; Custom for "--names", Unknown for anything else.</summary>
		public static StylePropertyId FromName(string name)
		{
			if (name == null)
			{
				return StylePropertyId.Unknown;
			}

			if (name.StartsWith("--", StringComparison.Ordinal))
			{
				return StylePropertyId.Custom;
			}

			StylePropertyId id;
			return byName.TryGetValue(name, out id) ? id : StylePropertyId.Unknown;
		}

		public static string GetName(StylePropertyId id)
		{
			return Properties[(int)id]?.Name;
		}

		public static bool IsInherited(StylePropertyId id)
		{
			StylePropertyInfo info = Properties[(int)id];
			return info != null && info.Inherited;
		}

		public static StyleEffect GetEffect(StylePropertyId id)
		{
			StylePropertyInfo info = Properties[(int)id];
			return info != null ? info.Effect : StyleEffect.None;
		}

		public static StyleAnimationKind GetAnimationKind(StylePropertyId id)
		{
			StylePropertyInfo info = Properties[(int)id];
			return info != null ? info.AnimationKind : StyleAnimationKind.None;
		}

		/// <summary>Expands a shorthand into its longhand ids (empty for longhands).</summary>
		public static StylePropertyId[] GetLonghands(StylePropertyId id)
		{
			switch (id)
			{
				case StylePropertyId.Margin: return new[] { StylePropertyId.MarginTop, StylePropertyId.MarginRight, StylePropertyId.MarginBottom, StylePropertyId.MarginLeft };
				case StylePropertyId.Padding: return new[] { StylePropertyId.PaddingTop, StylePropertyId.PaddingRight, StylePropertyId.PaddingBottom, StylePropertyId.PaddingLeft };
				case StylePropertyId.BorderWidth: return new[] { StylePropertyId.BorderTopWidth, StylePropertyId.BorderRightWidth, StylePropertyId.BorderBottomWidth, StylePropertyId.BorderLeftWidth };
				case StylePropertyId.BorderColor: return new[] { StylePropertyId.BorderTopColor, StylePropertyId.BorderRightColor, StylePropertyId.BorderBottomColor, StylePropertyId.BorderLeftColor };
				case StylePropertyId.BorderRadius: return new[] { StylePropertyId.BorderTopLeftRadius, StylePropertyId.BorderTopRightRadius, StylePropertyId.BorderBottomRightRadius, StylePropertyId.BorderBottomLeftRadius };
				case StylePropertyId.Flex: return new[] { StylePropertyId.FlexGrow, StylePropertyId.FlexShrink, StylePropertyId.FlexBasis };
				case StylePropertyId.Transition: return new[] { StylePropertyId.TransitionProperty, StylePropertyId.TransitionDuration, StylePropertyId.TransitionTimingFunction, StylePropertyId.TransitionDelay };
				case StylePropertyId.BackgroundPosition: return new[] { StylePropertyId.BackgroundPositionX, StylePropertyId.BackgroundPositionY };
				case StylePropertyId.UnityTextOutline: return new[] { StylePropertyId.UnityTextOutlineWidth, StylePropertyId.UnityTextOutlineColor };
				default: return Array.Empty<StylePropertyId>();
			}
		}

		#endregion

		#region PrivateMethods

		private static void Add(StylePropertyId id, string name, StyleEffect effect, StyleAnimationKind animation = StyleAnimationKind.None, bool inherited = false, bool shorthand = false)
		{
			var info = new StylePropertyInfo(id, name, effect, animation, inherited, shorthand);
			Properties[(int)id] = info;
			byName[name] = id;
		}

		#endregion
	}

	public sealed class StylePropertyInfo
	{
		public readonly StylePropertyId Id;
		public readonly string Name;
		public readonly StyleEffect Effect;
		public readonly StyleAnimationKind AnimationKind;
		public readonly bool Inherited;
		public readonly bool Shorthand;

		public StylePropertyInfo(StylePropertyId id, string name, StyleEffect effect, StyleAnimationKind animationKind, bool inherited, bool shorthand)
		{
			Id = id;
			Name = name;
			Effect = effect;
			AnimationKind = animationKind;
			Inherited = inherited;
			Shorthand = shorthand;
		}
	}
}
