using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Writes USS declaration values into a <see cref="ComputedStyle" />: value parsing per property, shorthand
	///     expansion, the initial / inherit keywords and var() substitution. Used by the cascade (sheets and inline
	///     styles alike) and by the parser to validate declarations once at load time.
	/// </summary>
	public static class StyleApplier
	{
		#region PrivateFields

		private static readonly Dictionary<Type, Dictionary<string, object>> enumCache = new Dictionary<Type, Dictionary<string, object>>();
		private static readonly ComputedStyle scratch = new ComputedStyle();
		private static readonly List<StyleValue> expansion = new List<StyleValue>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     Applies a declaration. <paramref name="parent" /> supplies values for "inherit"; <paramref name="target" />
		///     must already own its inherited data when inherited properties are written (see
		///     <see cref="ComputedStyle.OwnInherited" />). Returns false when the value is not valid for the property.
		/// </summary>
		public static bool Apply(ComputedStyle target, ComputedStyle parent, StylePropertyId id, string customName, StyleValue[] values, bool usesVariables)
		{
			if (values == null || values.Length == 0)
			{
				return false;
			}

			if (usesVariables)
			{
				values = ExpandVariables(target, values);
				if (values == null)
				{
					return false;
				}
			}

			if (id == StylePropertyId.Custom)
			{
				SetCustomProperty(target, customName, values);
				return true;
			}

			if (values.Length == 1 && values[0].type == StyleValueType.Keyword)
			{
				if (values[0].keyword == StyleKeyword.Initial)
				{
					return ResetToInitial(target, parent, id);
				}

				if (values[0].keyword == StyleKeyword.Inherit)
				{
					return Inherit(target, parent, id);
				}
			}

			return ApplyValue(target, id, values, 0, values.Length);
		}

		/// <summary>Checks a declaration parses for its property (load-time validation); var() values are not checked.</summary>
		public static bool Validate(StyleDeclaration declaration)
		{
			if (declaration.usesVariables || declaration.id == StylePropertyId.Custom)
			{
				return true;
			}

			scratch.inherited = scratch.inherited.Clone();
			return Apply(scratch, ComputedStyle.Default, declaration.id, declaration.name, declaration.values, false);
		}

		#endregion

		#region PrivateMethods

		// ------------------------------------------------------------------ variables

		private static void SetCustomProperty(ComputedStyle target, string name, StyleValue[] values)
		{
			if (string.IsNullOrEmpty(name))
			{
				return;
			}

			InheritedStyle inherited = target.inherited;
			Dictionary<string, StyleValue[]> map = inherited.customProperties;
			if (map == null)
			{
				map = new Dictionary<string, StyleValue[]>(StringComparer.Ordinal);
			}
			else
			{
				StyleValue[] existing;
				if (map.TryGetValue(name, out existing) && ReferenceEquals(existing, values))
				{
					return;
				}

				// The dictionary may be shared with the parent (inheritance): copy before writing.
				map = new Dictionary<string, StyleValue[]>(map, StringComparer.Ordinal);
			}

			map[name] = values;
			inherited.customProperties = map;
		}

		/// <summary>Replaces var(--name, fallback) functions with the variable's values; null when one is undefined.</summary>
		private static StyleValue[] ExpandVariables(ComputedStyle target, StyleValue[] values)
		{
			var output = new List<StyleValue>(values.Length + 4);
			if (!ExpandVariables(target, values, 0, values.Length, output, 0))
			{
				return null;
			}

			return output.ToArray();
		}

		private static bool ExpandVariables(ComputedStyle target, StyleValue[] values, int start, int end, List<StyleValue> output, int depth)
		{
			if (depth > 16)
			{
				return false;
			}

			for (int i = start; i < end; i++)
			{
				StyleValue value = values[i];
				if (value.type != StyleValueType.Function || value.text != "var")
				{
					output.Add(value);
					continue;
				}

				int close = FindFunctionEnd(values, i);
				int argumentStart = i + 1;
				string variableName = argumentStart < close && values[argumentStart].type == StyleValueType.Identifier ? values[argumentStart].text : null;
				int fallbackStart = -1;
				for (int j = argumentStart; j < close; j++)
				{
					if (values[j].type == StyleValueType.Comma)
					{
						fallbackStart = j + 1;
						break;
					}
				}

				StyleValue[] resolved = variableName != null ? target.inherited.GetCustomProperty(variableName) : null;
				if (resolved != null)
				{
					if (!ExpandVariables(target, resolved, 0, resolved.Length, output, depth + 1))
					{
						return false;
					}
				}
				else if (fallbackStart >= 0)
				{
					if (!ExpandVariables(target, values, fallbackStart, close, output, depth + 1))
					{
						return false;
					}
				}
				else
				{
					return false;
				}

				i = close;
			}

			return true;
		}

		private static int FindFunctionEnd(StyleValue[] values, int functionIndex)
		{
			int depth = 0;
			for (int i = functionIndex; i < values.Length; i++)
			{
				if (values[i].type == StyleValueType.Function) depth++;
				else if (values[i].type == StyleValueType.FunctionEnd && --depth == 0) return i;
			}

			return values.Length - 1;
		}

		// ------------------------------------------------------------------ initial / inherit

		private static bool ResetToInitial(ComputedStyle target, ComputedStyle parent, StylePropertyId id)
		{
			if (id == StylePropertyId.All)
			{
				CopyAll(ComputedStyle.Default, target, true);
				return true;
			}

			StylePropertyId[] longhands = StylePropertyRegistry.GetLonghands(id);
			if (longhands.Length > 0)
			{
				foreach (StylePropertyId longhand in longhands)
				{
					CopyProperty(ComputedStyle.Default, target, longhand);
				}

				return true;
			}

			CopyProperty(ComputedStyle.Default, target, id);
			return true;
		}

		private static bool Inherit(ComputedStyle target, ComputedStyle parent, StylePropertyId id)
		{
			if (parent == null)
			{
				return ResetToInitial(target, null, id);
			}

			StylePropertyId[] longhands = StylePropertyRegistry.GetLonghands(id);
			if (longhands.Length > 0)
			{
				foreach (StylePropertyId longhand in longhands)
				{
					CopyProperty(parent, target, longhand);
				}

				return true;
			}

			CopyProperty(parent, target, id);
			return true;
		}

		private static void CopyAll(ComputedStyle from, ComputedStyle to, bool keepCustomProperties)
		{
			Dictionary<string, StyleValue[]> custom = to.inherited.customProperties;
			for (StylePropertyId id = StylePropertyId.AlignContent; id < StylePropertyId.Margin; id++)
			{
				CopyProperty(from, to, id);
			}

			if (keepCustomProperties)
			{
				to.inherited.customProperties = custom;
			}
		}

		/// <summary>Copies one property value between styles (transitions, initial, inherit).</summary>
		internal static void CopyProperty(ComputedStyle from, ComputedStyle to, StylePropertyId id)
		{
			InheritedStyle fi = from.inherited;
			InheritedStyle ti = to.inherited;
			switch (id)
			{
				case StylePropertyId.AlignContent: to.alignContent = from.alignContent; break;
				case StylePropertyId.AlignItems: to.alignItems = from.alignItems; break;
				case StylePropertyId.AlignSelf: to.alignSelf = from.alignSelf; break;
				case StylePropertyId.Bottom: to.bottom = from.bottom; break;
				case StylePropertyId.Display: to.display = from.display; break;
				case StylePropertyId.FlexBasis: to.flexBasis = from.flexBasis; break;
				case StylePropertyId.FlexDirection: to.flexDirection = from.flexDirection; break;
				case StylePropertyId.FlexGrow: to.flexGrow = from.flexGrow; break;
				case StylePropertyId.FlexShrink: to.flexShrink = from.flexShrink; break;
				case StylePropertyId.FlexWrap: to.flexWrap = from.flexWrap; break;
				case StylePropertyId.Height: to.height = from.height; break;
				case StylePropertyId.JustifyContent: to.justifyContent = from.justifyContent; break;
				case StylePropertyId.Left: to.left = from.left; break;
				case StylePropertyId.MarginBottom: to.marginBottom = from.marginBottom; break;
				case StylePropertyId.MarginLeft: to.marginLeft = from.marginLeft; break;
				case StylePropertyId.MarginRight: to.marginRight = from.marginRight; break;
				case StylePropertyId.MarginTop: to.marginTop = from.marginTop; break;
				case StylePropertyId.MaxHeight: to.maxHeight = from.maxHeight; break;
				case StylePropertyId.MaxWidth: to.maxWidth = from.maxWidth; break;
				case StylePropertyId.MinHeight: to.minHeight = from.minHeight; break;
				case StylePropertyId.MinWidth: to.minWidth = from.minWidth; break;
				case StylePropertyId.PaddingBottom: to.paddingBottom = from.paddingBottom; break;
				case StylePropertyId.PaddingLeft: to.paddingLeft = from.paddingLeft; break;
				case StylePropertyId.PaddingRight: to.paddingRight = from.paddingRight; break;
				case StylePropertyId.PaddingTop: to.paddingTop = from.paddingTop; break;
				case StylePropertyId.Position: to.position = from.position; break;
				case StylePropertyId.Right: to.right = from.right; break;
				case StylePropertyId.Top: to.top = from.top; break;
				case StylePropertyId.Width: to.width = from.width; break;
				case StylePropertyId.BorderBottomWidth: to.borderBottomWidth = from.borderBottomWidth; break;
				case StylePropertyId.BorderLeftWidth: to.borderLeftWidth = from.borderLeftWidth; break;
				case StylePropertyId.BorderRightWidth: to.borderRightWidth = from.borderRightWidth; break;
				case StylePropertyId.BorderTopWidth: to.borderTopWidth = from.borderTopWidth; break;
				case StylePropertyId.BackgroundColor: to.backgroundColor = from.backgroundColor; break;
				case StylePropertyId.BackgroundImage: to.backgroundImage = from.backgroundImage; break;
				case StylePropertyId.BackgroundPositionX: to.backgroundPositionX = from.backgroundPositionX; break;
				case StylePropertyId.BackgroundPositionY: to.backgroundPositionY = from.backgroundPositionY; break;
				case StylePropertyId.BackgroundRepeat: to.backgroundRepeat = from.backgroundRepeat; break;
				case StylePropertyId.BackgroundSize: to.backgroundSize = from.backgroundSize; break;
				case StylePropertyId.UnityBackgroundImageTintColor: to.unityBackgroundImageTintColor = from.unityBackgroundImageTintColor; break;
				case StylePropertyId.UnityBackgroundScaleMode: to.unityBackgroundScaleMode = from.unityBackgroundScaleMode; break;
				case StylePropertyId.BorderBottomColor: to.borderBottomColor = from.borderBottomColor; break;
				case StylePropertyId.BorderLeftColor: to.borderLeftColor = from.borderLeftColor; break;
				case StylePropertyId.BorderRightColor: to.borderRightColor = from.borderRightColor; break;
				case StylePropertyId.BorderTopColor: to.borderTopColor = from.borderTopColor; break;
				case StylePropertyId.BorderBottomLeftRadius: to.borderBottomLeftRadius = from.borderBottomLeftRadius; break;
				case StylePropertyId.BorderBottomRightRadius: to.borderBottomRightRadius = from.borderBottomRightRadius; break;
				case StylePropertyId.BorderTopLeftRadius: to.borderTopLeftRadius = from.borderTopLeftRadius; break;
				case StylePropertyId.BorderTopRightRadius: to.borderTopRightRadius = from.borderTopRightRadius; break;
				case StylePropertyId.Opacity: to.opacity = from.opacity; break;
				case StylePropertyId.Overflow: to.overflow = from.overflow; break;
				case StylePropertyId.UnityOverflowClipBox: to.unityOverflowClipBox = from.unityOverflowClipBox; break;
				case StylePropertyId.UnitySliceBottom: to.unitySliceBottom = from.unitySliceBottom; break;
				case StylePropertyId.UnitySliceLeft: to.unitySliceLeft = from.unitySliceLeft; break;
				case StylePropertyId.UnitySliceRight: to.unitySliceRight = from.unitySliceRight; break;
				case StylePropertyId.UnitySliceTop: to.unitySliceTop = from.unitySliceTop; break;
				case StylePropertyId.UnitySliceScale: to.unitySliceScale = from.unitySliceScale; break;
				case StylePropertyId.UnitySliceType: to.unitySliceType = from.unitySliceType; break;
				case StylePropertyId.TextOverflow: to.textOverflow = from.textOverflow; break;
				case StylePropertyId.Color: ti.color = fi.color; break;
				case StylePropertyId.Cursor: ti.cursor = fi.cursor; break;
				case StylePropertyId.FontSize: ti.fontSize = fi.fontSize; break;
				case StylePropertyId.UnityFont: ti.unityFont = fi.unityFont; break;
				case StylePropertyId.UnityFontDefinition: ti.unityFontDefinition = fi.unityFontDefinition; break;
				case StylePropertyId.UnityFontStyleAndWeight: ti.unityFontStyleAndWeight = fi.unityFontStyleAndWeight; break;
				case StylePropertyId.LetterSpacing: ti.letterSpacing = fi.letterSpacing; break;
				case StylePropertyId.WordSpacing: ti.wordSpacing = fi.wordSpacing; break;
				case StylePropertyId.WhiteSpace: ti.whiteSpace = fi.whiteSpace; break;
				case StylePropertyId.UnityTextAlign: ti.unityTextAlign = fi.unityTextAlign; break;
				case StylePropertyId.UnityParagraphSpacing: ti.unityParagraphSpacing = fi.unityParagraphSpacing; break;
				case StylePropertyId.TextShadow: ti.textShadow = fi.textShadow; break;
				case StylePropertyId.UnityTextOutlineColor: ti.unityTextOutlineColor = fi.unityTextOutlineColor; break;
				case StylePropertyId.UnityTextOutlineWidth: ti.unityTextOutlineWidth = fi.unityTextOutlineWidth; break;
				case StylePropertyId.UnityTextOverflowPosition: ti.unityTextOverflowPosition = fi.unityTextOverflowPosition; break;
				case StylePropertyId.Visibility: ti.visibility = fi.visibility; break;
				case StylePropertyId.Translate: to.translate = from.translate; break;
				case StylePropertyId.Rotate: to.rotate = from.rotate; break;
				case StylePropertyId.Scale: to.scale = from.scale; break;
				case StylePropertyId.TransformOrigin: to.transformOrigin = from.transformOrigin; break;
				case StylePropertyId.TransitionProperty: to.transitionProperty = from.transitionProperty; break;
				case StylePropertyId.TransitionDuration: to.transitionDuration = from.transitionDuration; break;
				case StylePropertyId.TransitionDelay: to.transitionDelay = from.transitionDelay; break;
				case StylePropertyId.TransitionTimingFunction: to.transitionTimingFunction = from.transitionTimingFunction; break;
			}
		}

		// ------------------------------------------------------------------ values

		private static bool ApplyValue(ComputedStyle s, StylePropertyId id, StyleValue[] v, int start, int count)
		{
			InheritedStyle inherited = s.inherited;
			switch (id)
			{
				// Layout enums
				case StylePropertyId.AlignContent: return Enum(v, start, ref s.alignContent);
				case StylePropertyId.AlignItems: return Enum(v, start, ref s.alignItems);
				case StylePropertyId.AlignSelf: return Enum(v, start, ref s.alignSelf);
				case StylePropertyId.Display: return Enum(v, start, ref s.display);
				case StylePropertyId.FlexDirection: return Enum(v, start, ref s.flexDirection);
				case StylePropertyId.FlexWrap: return Enum(v, start, ref s.flexWrap);
				case StylePropertyId.JustifyContent: return Enum(v, start, ref s.justifyContent);
				case StylePropertyId.Position: return Enum(v, start, ref s.position);
				case StylePropertyId.Overflow: return Enum(v, start, ref s.overflow);
				case StylePropertyId.UnityOverflowClipBox: return Enum(v, start, ref s.unityOverflowClipBox);
				case StylePropertyId.UnityBackgroundScaleMode: return Enum(v, start, ref s.unityBackgroundScaleMode);
				case StylePropertyId.UnitySliceType: return Enum(v, start, ref s.unitySliceType);
				case StylePropertyId.TextOverflow: return Enum(v, start, ref s.textOverflow);
				case StylePropertyId.UnityFontStyleAndWeight: return Enum(v, start, ref inherited.unityFontStyleAndWeight);
				case StylePropertyId.WhiteSpace: return Enum(v, start, ref inherited.whiteSpace);
				case StylePropertyId.UnityTextAlign: return Enum(v, start, ref inherited.unityTextAlign);
				case StylePropertyId.UnityTextOverflowPosition: return Enum(v, start, ref inherited.unityTextOverflowPosition);
				case StylePropertyId.Visibility: return Enum(v, start, ref inherited.visibility);

				// Lengths
				case StylePropertyId.Bottom: return LengthValue(v, start, ref s.bottom, true, false);
				case StylePropertyId.FlexBasis: return LengthValue(v, start, ref s.flexBasis, true, false);
				case StylePropertyId.Height: return LengthValue(v, start, ref s.height, true, false);
				case StylePropertyId.Left: return LengthValue(v, start, ref s.left, true, false);
				case StylePropertyId.MarginBottom: return LengthValue(v, start, ref s.marginBottom, true, false);
				case StylePropertyId.MarginLeft: return LengthValue(v, start, ref s.marginLeft, true, false);
				case StylePropertyId.MarginRight: return LengthValue(v, start, ref s.marginRight, true, false);
				case StylePropertyId.MarginTop: return LengthValue(v, start, ref s.marginTop, true, false);
				case StylePropertyId.MaxHeight: return LengthValue(v, start, ref s.maxHeight, false, true);
				case StylePropertyId.MaxWidth: return LengthValue(v, start, ref s.maxWidth, false, true);
				case StylePropertyId.MinHeight: return LengthValue(v, start, ref s.minHeight, true, false);
				case StylePropertyId.MinWidth: return LengthValue(v, start, ref s.minWidth, true, false);
				case StylePropertyId.PaddingBottom: return LengthValue(v, start, ref s.paddingBottom, false, false);
				case StylePropertyId.PaddingLeft: return LengthValue(v, start, ref s.paddingLeft, false, false);
				case StylePropertyId.PaddingRight: return LengthValue(v, start, ref s.paddingRight, false, false);
				case StylePropertyId.PaddingTop: return LengthValue(v, start, ref s.paddingTop, false, false);
				case StylePropertyId.Right: return LengthValue(v, start, ref s.right, true, false);
				case StylePropertyId.Top: return LengthValue(v, start, ref s.top, true, false);
				case StylePropertyId.Width: return LengthValue(v, start, ref s.width, true, false);
				case StylePropertyId.BorderBottomLeftRadius: return LengthValue(v, start, ref s.borderBottomLeftRadius, false, false);
				case StylePropertyId.BorderBottomRightRadius: return LengthValue(v, start, ref s.borderBottomRightRadius, false, false);
				case StylePropertyId.BorderTopLeftRadius: return LengthValue(v, start, ref s.borderTopLeftRadius, false, false);
				case StylePropertyId.BorderTopRightRadius: return LengthValue(v, start, ref s.borderTopRightRadius, false, false);
				case StylePropertyId.FontSize: return LengthValue(v, start, ref inherited.fontSize, false, false);
				case StylePropertyId.LetterSpacing: return LengthValue(v, start, ref inherited.letterSpacing, false, false);
				case StylePropertyId.WordSpacing: return LengthValue(v, start, ref inherited.wordSpacing, false, false);
				case StylePropertyId.UnityParagraphSpacing: return LengthValue(v, start, ref inherited.unityParagraphSpacing, false, false);

				// Floats
				case StylePropertyId.FlexGrow: return FloatValue(v, start, ref s.flexGrow);
				case StylePropertyId.FlexShrink: return FloatValue(v, start, ref s.flexShrink);
				case StylePropertyId.BorderBottomWidth: return FloatValue(v, start, ref s.borderBottomWidth);
				case StylePropertyId.BorderLeftWidth: return FloatValue(v, start, ref s.borderLeftWidth);
				case StylePropertyId.BorderRightWidth: return FloatValue(v, start, ref s.borderRightWidth);
				case StylePropertyId.BorderTopWidth: return FloatValue(v, start, ref s.borderTopWidth);
				case StylePropertyId.Opacity: return FloatValue(v, start, ref s.opacity);
				case StylePropertyId.UnitySliceScale: return FloatValue(v, start, ref s.unitySliceScale);
				case StylePropertyId.UnityTextOutlineWidth: return FloatValue(v, start, ref inherited.unityTextOutlineWidth);
				case StylePropertyId.UnitySliceBottom: return IntValue(v, start, ref s.unitySliceBottom);
				case StylePropertyId.UnitySliceLeft: return IntValue(v, start, ref s.unitySliceLeft);
				case StylePropertyId.UnitySliceRight: return IntValue(v, start, ref s.unitySliceRight);
				case StylePropertyId.UnitySliceTop: return IntValue(v, start, ref s.unitySliceTop);

				// Colors
				case StylePropertyId.BackgroundColor: return ColorValue(v, start, ref s.backgroundColor);
				case StylePropertyId.UnityBackgroundImageTintColor: return ColorValue(v, start, ref s.unityBackgroundImageTintColor);
				case StylePropertyId.BorderBottomColor: return ColorValue(v, start, ref s.borderBottomColor);
				case StylePropertyId.BorderLeftColor: return ColorValue(v, start, ref s.borderLeftColor);
				case StylePropertyId.BorderRightColor: return ColorValue(v, start, ref s.borderRightColor);
				case StylePropertyId.BorderTopColor: return ColorValue(v, start, ref s.borderTopColor);
				case StylePropertyId.Color: return ColorValue(v, start, ref inherited.color);
				case StylePropertyId.UnityTextOutlineColor: return ColorValue(v, start, ref inherited.unityTextOutlineColor);

				// Assets
				case StylePropertyId.BackgroundImage: return BackgroundImage(v, start, s);
				case StylePropertyId.UnityFont: return Font(v, start, ref inherited.unityFont);
				case StylePropertyId.UnityFontDefinition:
				{
					FontAsset font = inherited.unityFontDefinition.fontAsset;
					if (!Font(v, start, ref font)) return false;
					inherited.unityFontDefinition = new FontDefinition(font);
					return true;
				}

				case StylePropertyId.Cursor: return CursorValue(v, start, count, ref inherited.cursor);
				case StylePropertyId.TextShadow: return TextShadowValue(v, start, count, ref inherited.textShadow);

				// Background placement
				case StylePropertyId.BackgroundPositionX: return BackgroundPositionValue(v, start, count, ref s.backgroundPositionX, true);
				case StylePropertyId.BackgroundPositionY: return BackgroundPositionValue(v, start, count, ref s.backgroundPositionY, false);
				case StylePropertyId.BackgroundRepeat: return BackgroundRepeatValue(v, start, count, ref s.backgroundRepeat);
				case StylePropertyId.BackgroundSize: return BackgroundSizeValue(v, start, count, ref s.backgroundSize);

				// Transform
				case StylePropertyId.Translate: return TranslateValue(v, start, count, ref s.translate);
				case StylePropertyId.Rotate: return RotateValue(v, start, count, ref s.rotate);
				case StylePropertyId.Scale: return ScaleValue(v, start, count, ref s.scale);
				case StylePropertyId.TransformOrigin: return TransformOriginValue(v, start, count, ref s.transformOrigin);

				// Transitions
				case StylePropertyId.TransitionProperty: return TransitionPropertyValue(v, start, count, s);
				case StylePropertyId.TransitionDuration: return TimeListValue(v, start, count, ref s.transitionDuration);
				case StylePropertyId.TransitionDelay: return TimeListValue(v, start, count, ref s.transitionDelay);
				case StylePropertyId.TransitionTimingFunction: return EasingListValue(v, start, count, ref s.transitionTimingFunction);

				// Shorthands
				case StylePropertyId.Margin: return FourLengths(v, start, count, ref s.marginTop, ref s.marginRight, ref s.marginBottom, ref s.marginLeft, true);
				case StylePropertyId.Padding: return FourLengths(v, start, count, ref s.paddingTop, ref s.paddingRight, ref s.paddingBottom, ref s.paddingLeft, false);
				case StylePropertyId.BorderRadius: return FourLengths(v, start, count, ref s.borderTopLeftRadius, ref s.borderTopRightRadius, ref s.borderBottomRightRadius, ref s.borderBottomLeftRadius, false);
				case StylePropertyId.BorderWidth: return FourFloats(v, start, count, ref s.borderTopWidth, ref s.borderRightWidth, ref s.borderBottomWidth, ref s.borderLeftWidth);
				case StylePropertyId.BorderColor: return FourColors(v, start, count, ref s.borderTopColor, ref s.borderRightColor, ref s.borderBottomColor, ref s.borderLeftColor);
				case StylePropertyId.Flex: return FlexShorthand(v, start, count, s);
				case StylePropertyId.Transition: return TransitionShorthand(v, start, count, s);
				case StylePropertyId.BackgroundPosition: return BackgroundPositionShorthand(v, start, count, s);
				case StylePropertyId.UnityTextOutline: return TextOutlineShorthand(v, start, count, inherited);
				case StylePropertyId.All: return false;
				default: return false;
			}
		}

		// ------------------------------------------------------------------ scalar readers

		private static bool Enum<T>(StyleValue[] v, int i, ref T target)
			where T : struct, System.Enum
		{
			if (i >= v.Length)
			{
				return false;
			}

			string name = v[i].type == StyleValueType.Identifier ? v[i].text : v[i].type == StyleValueType.Keyword ? v[i].keyword.ToString() : null;
			if (name == null)
			{
				return false;
			}

			object value;
			if (!GetEnumMap(typeof(T)).TryGetValue(Normalize(name), out value))
			{
				return false;
			}

			target = (T)value;
			return true;
		}

		private static Dictionary<string, object> GetEnumMap(Type type)
		{
			Dictionary<string, object> map;
			lock (enumCache)
			{
				if (!enumCache.TryGetValue(type, out map))
				{
					map = new Dictionary<string, object>(StringComparer.Ordinal);
					foreach (object value in System.Enum.GetValues(type))
					{
						map[Normalize(value.ToString())] = value;
					}

					enumCache[type] = map;
				}
			}

			return map;
		}

		/// <summary>"flex-start" and "FlexStart" both become "flexstart".</summary>
		private static string Normalize(string name)
		{
			var builder = new System.Text.StringBuilder(name.Length);
			foreach (char c in name)
			{
				if (c != '-' && c != '_')
				{
					builder.Append(char.ToLowerInvariant(c));
				}
			}

			return builder.ToString();
		}

		private static bool LengthValue(StyleValue[] v, int i, ref Length target, bool allowAuto, bool allowNone)
		{
			if (i >= v.Length)
			{
				return false;
			}

			StyleValue value = v[i];
			if (value.type == StyleValueType.Keyword)
			{
				if (value.keyword == StyleKeyword.Auto && allowAuto)
				{
					target = Length.Auto();
					return true;
				}

				if (value.keyword == StyleKeyword.None && allowNone)
				{
					target = Length.None();
					return true;
				}

				return false;
			}

			if (!value.IsLength)
			{
				return false;
			}

			target = value.ToLength();
			return true;
		}

		private static bool FloatValue(StyleValue[] v, int i, ref float target)
		{
			if (i >= v.Length || !v[i].IsNumber || (v[i].type == StyleValueType.Dimension && v[i].unit != StyleUnit.Pixel))
			{
				return false;
			}

			target = v[i].number;
			return true;
		}

		private static bool IntValue(StyleValue[] v, int i, ref int target)
		{
			float f = target;
			if (!FloatValue(v, i, ref f))
			{
				return false;
			}

			target = (int)Math.Round(f);
			return true;
		}

		private static bool ColorValue(StyleValue[] v, int i, ref Color4 target)
		{
			if (i >= v.Length)
			{
				return false;
			}

			if (v[i].type == StyleValueType.Color)
			{
				target = v[i].color;
				return true;
			}

			Color4 parsed;
			if (v[i].type == StyleValueType.Identifier && StyleColorUtility.TryParse(v[i].text, out parsed))
			{
				target = parsed;
				return true;
			}

			return false;
		}

		private static object ReadAsset(StyleValue value)
		{
			if (value.type == StyleValueType.Reference)
			{
				return value.reference;
			}

			if (value.type == StyleValueType.ResourcePath)
			{
				return value.reference ?? UIAssets.LoadResource(value.text);
			}

			return null;
		}

		private static bool BackgroundImage(StyleValue[] v, int i, ComputedStyle s)
		{
			if (i >= v.Length)
			{
				return false;
			}

			if (v[i].IsKeyword(StyleKeyword.None))
			{
				s.backgroundImage = default;
				return true;
			}

			object asset = ReadAsset(v[i]);
			if (asset is LELEngine.Rendering.RenderTexture renderTexture)
			{
				// An inline style showing a camera's image (style.backgroundImage = Background.FromRenderTexture(...)).
				s.backgroundImage = new Background(renderTexture);
				return true;
			}

			Texture texture = asset as Texture;
			if (texture == null && (v[i].type == StyleValueType.ResourcePath || v[i].type == StyleValueType.Reference))
			{
				// Missing file: keep rendering without an image rather than rejecting the declaration.
				s.backgroundImage = default;
				return true;
			}

			if (texture == null)
			{
				return false;
			}

			s.backgroundImage = new Background(texture);
			return true;
		}

		private static bool Font(StyleValue[] v, int i, ref FontAsset target)
		{
			if (i >= v.Length)
			{
				return false;
			}

			if (v[i].IsKeyword(StyleKeyword.None) || v[i].IsKeyword(StyleKeyword.Initial))
			{
				target = null;
				return true;
			}

			object asset = ReadAsset(v[i]);
			if (asset is FontAsset font)
			{
				target = font;
				return true;
			}

			if (v[i].type == StyleValueType.String || v[i].type == StyleValueType.Identifier)
			{
				// A font family name: system font lookup.
				target = FontAsset.FindSystemFont(v[i].text);
				return target != null;
			}

			return v[i].type == StyleValueType.ResourcePath;
		}

		private static bool CursorValue(StyleValue[] v, int start, int count, ref Cursor target)
		{
			if (count == 0)
			{
				return false;
			}

			if (v[start].type == StyleValueType.ResourcePath || v[start].type == StyleValueType.Reference)
			{
				var cursor = new Cursor(CursorType.Arrow) { texture = ReadAsset(v[start]) as Texture };
				if (count >= 3 && v[start + 1].IsNumber && v[start + 2].IsNumber)
				{
					cursor.hotspot = new Vector2(v[start + 1].number, v[start + 2].number);
				}

				target = cursor;
				return true;
			}

			CursorType type = target.type;
			if (!Enum(v, start, ref type))
			{
				return false;
			}

			target = new Cursor(type);
			return true;
		}

		private static bool TextShadowValue(StyleValue[] v, int start, int count, ref TextShadow target)
		{
			if (count == 1 && v[start].IsKeyword(StyleKeyword.None))
			{
				target = default;
				return true;
			}

			var shadow = new TextShadow { color = new Color4(0f, 0f, 0f, 1f) };
			int lengths = 0;
			bool hasColor = false;
			for (int i = start; i < start + count; i++)
			{
				if (v[i].IsNumber)
				{
					switch (lengths++)
					{
						case 0: shadow.offset.X = v[i].number; break;
						case 1: shadow.offset.Y = v[i].number; break;
						case 2: shadow.blurRadius = v[i].number; break;
						default: return false;
					}
				}
				else if (!hasColor && ColorValue(v, i, ref shadow.color))
				{
					hasColor = true;
				}
				else
				{
					return false;
				}
			}

			if (lengths < 2)
			{
				return false;
			}

			target = shadow;
			return true;
		}

		// ------------------------------------------------------------------ background placement

		private static bool BackgroundPositionValue(StyleValue[] v, int start, int count, ref BackgroundPosition target, bool horizontal)
		{
			if (count == 0)
			{
				return false;
			}

			var result = new BackgroundPosition(horizontal ? BackgroundPositionKeyword.Left : BackgroundPositionKeyword.Top);
			int i = start;
			if (v[i].type == StyleValueType.Identifier)
			{
				BackgroundPositionKeyword keyword = result.keyword;
				if (!Enum(v, i, ref keyword))
				{
					return false;
				}

				result.keyword = keyword;
				i++;
				if (i < start + count && v[i].IsLength)
				{
					result.offset = v[i].ToLength();
					i++;
				}
			}
			else if (v[i].IsLength)
			{
				result.offset = v[i].ToLength();
				i++;
			}
			else
			{
				return false;
			}

			if (i != start + count)
			{
				return false;
			}

			target = result;
			return true;
		}

		private static bool BackgroundPositionShorthand(StyleValue[] v, int start, int count, ComputedStyle s)
		{
			// Forms: "x y", "keyword", "keyword keyword", "keyword offset keyword offset", "length length".
			var x = new BackgroundPosition(BackgroundPositionKeyword.Left);
			var y = new BackgroundPosition(BackgroundPositionKeyword.Top);
			var parts = new List<BackgroundPosition>();
			int i = start;
			while (i < start + count)
			{
				var part = new BackgroundPosition(BackgroundPositionKeyword.Center);
				if (v[i].type == StyleValueType.Identifier)
				{
					BackgroundPositionKeyword keyword = part.keyword;
					if (!Enum(v, i, ref keyword)) return false;
					part.keyword = keyword;
					i++;
					if (i < start + count && v[i].IsLength && part.keyword != BackgroundPositionKeyword.Center)
					{
						part.offset = v[i].ToLength();
						i++;
					}
				}
				else if (v[i].IsLength)
				{
					part.keyword = parts.Count == 0 ? BackgroundPositionKeyword.Left : BackgroundPositionKeyword.Top;
					part.offset = v[i].ToLength();
					i++;
				}
				else
				{
					return false;
				}

				parts.Add(part);
			}

			if (parts.Count == 0 || parts.Count > 2)
			{
				return false;
			}

			if (parts.Count == 1)
			{
				BackgroundPosition p = parts[0];
				if (p.keyword == BackgroundPositionKeyword.Top || p.keyword == BackgroundPositionKeyword.Bottom)
				{
					y = p;
					x = new BackgroundPosition(BackgroundPositionKeyword.Center);
				}
				else
				{
					x = p;
					y = new BackgroundPosition(BackgroundPositionKeyword.Center);
				}
			}
			else
			{
				BackgroundPosition a = parts[0], b = parts[1];
				bool aVertical = a.keyword == BackgroundPositionKeyword.Top || a.keyword == BackgroundPositionKeyword.Bottom;
				bool bHorizontal = b.keyword == BackgroundPositionKeyword.Left || b.keyword == BackgroundPositionKeyword.Right;
				if (aVertical || bHorizontal)
				{
					x = b;
					y = a;
				}
				else
				{
					x = a;
					y = b;
				}

				if (x.keyword == BackgroundPositionKeyword.Top) x.keyword = BackgroundPositionKeyword.Left;
				if (y.keyword == BackgroundPositionKeyword.Left) y.keyword = BackgroundPositionKeyword.Top;
			}

			s.backgroundPositionX = x;
			s.backgroundPositionY = y;
			return true;
		}

		private static bool BackgroundRepeatValue(StyleValue[] v, int start, int count, ref BackgroundRepeat target)
		{
			if (count == 1 && v[start].type == StyleValueType.Identifier)
			{
				string name = Normalize(v[start].text);
				if (name == "repeatx")
				{
					target = new BackgroundRepeat(Repeat.Repeat, Repeat.NoRepeat);
					return true;
				}

				if (name == "repeaty")
				{
					target = new BackgroundRepeat(Repeat.NoRepeat, Repeat.Repeat);
					return true;
				}
			}

			Repeat x = Repeat.NoRepeat, y = Repeat.NoRepeat;
			if (count < 1 || !Enum(v, start, ref x))
			{
				return false;
			}

			y = x;
			if (count >= 2 && !Enum(v, start + 1, ref y))
			{
				return false;
			}

			target = new BackgroundRepeat(x, y);
			return true;
		}

		private static bool BackgroundSizeValue(StyleValue[] v, int start, int count, ref BackgroundSize target)
		{
			if (count == 0)
			{
				return false;
			}

			if (v[start].type == StyleValueType.Identifier)
			{
				string name = Normalize(v[start].text);
				if (name == "cover")
				{
					target = new BackgroundSize(BackgroundSizeType.Cover);
					return true;
				}

				if (name == "contain")
				{
					target = new BackgroundSize(BackgroundSizeType.Contain);
					return true;
				}

				return false;
			}

			Length x = Length.Auto(), y = Length.Auto();
			if (!LengthValue(v, start, ref x, true, false))
			{
				return false;
			}

			if (count >= 2 && !LengthValue(v, start + 1, ref y, true, false))
			{
				return false;
			}

			target = new BackgroundSize(x, y);
			return true;
		}

		// ------------------------------------------------------------------ transform

		private static bool TranslateValue(StyleValue[] v, int start, int count, ref Translate target)
		{
			if (count == 1 && v[start].IsKeyword(StyleKeyword.None))
			{
				target = Translate.None();
				return true;
			}

			Length x = 0f, y = 0f;
			if (count < 1 || !LengthValue(v, start, ref x, false, false))
			{
				return false;
			}

			if (count >= 2 && !LengthValue(v, start + 1, ref y, false, false))
			{
				return false;
			}

			target = new Translate(x, y);
			return true;
		}

		private static bool RotateValue(StyleValue[] v, int start, int count, ref Rotate target)
		{
			if (count == 1 && v[start].IsKeyword(StyleKeyword.None))
			{
				target = Rotate.None();
				return true;
			}

			if (count < 1 || !v[start].IsAngle)
			{
				return false;
			}

			target = new Rotate(v[start].ToAngle());
			return true;
		}

		private static bool ScaleValue(StyleValue[] v, int start, int count, ref Scale target)
		{
			if (count == 1 && v[start].IsKeyword(StyleKeyword.None))
			{
				target = Scale.None();
				return true;
			}

			if (count < 1 || !v[start].IsNumber)
			{
				return false;
			}

			float x = v[start].number;
			float y = x;
			if (count >= 2)
			{
				if (!v[start + 1].IsNumber) return false;
				y = v[start + 1].number;
			}

			target = new Scale(new Vector2(x, y));
			return true;
		}

		private static bool TransformOriginValue(StyleValue[] v, int start, int count, ref TransformOrigin target)
		{
			Length x = Length.Percent(50f), y = Length.Percent(50f);
			bool xSet = false, ySet = false;
			for (int i = start; i < start + count; i++)
			{
				if (v[i].type == StyleValueType.Identifier)
				{
					switch (Normalize(v[i].text))
					{
						case "left": x = Length.Percent(0f); xSet = true; break;
						case "right": x = Length.Percent(100f); xSet = true; break;
						case "top": y = Length.Percent(0f); ySet = true; break;
						case "bottom": y = Length.Percent(100f); ySet = true; break;
						case "center":
							if (!xSet) { x = Length.Percent(50f); xSet = true; }
							else { y = Length.Percent(50f); ySet = true; }
							break;
						default: return false;
					}
				}
				else if (v[i].IsLength)
				{
					if (!xSet) { x = v[i].ToLength(); xSet = true; }
					else if (!ySet) { y = v[i].ToLength(); ySet = true; }
					else return false;
				}
				else
				{
					return false;
				}
			}

			target = new TransformOrigin(x, y);
			return true;
		}

		// ------------------------------------------------------------------ transitions

		private static bool TransitionPropertyValue(StyleValue[] v, int start, int count, ComputedStyle s)
		{
			var list = new List<StylePropertyName>();
			for (int i = start; i < start + count; i++)
			{
				if (v[i].type == StyleValueType.Comma) continue;
				if (v[i].type == StyleValueType.Identifier) list.Add(new StylePropertyName(v[i].text));
				else if (v[i].IsKeyword(StyleKeyword.None)) list.Add(new StylePropertyName("none"));
				else if (v[i].IsKeyword(StyleKeyword.Initial)) list.Add(new StylePropertyName("all"));
				else return false;
			}

			s.transitionProperty = list;
			return true;
		}

		private static bool TimeListValue(StyleValue[] v, int start, int count, ref List<TimeValue> target)
		{
			var list = new List<TimeValue>();
			for (int i = start; i < start + count; i++)
			{
				if (v[i].type == StyleValueType.Comma) continue;
				if (v[i].IsTime) list.Add(v[i].ToTime());
				else if (v[i].type == StyleValueType.Float && v[i].number == 0f) list.Add(new TimeValue(0f));
				else return false;
			}

			target = list;
			return true;
		}

		private static bool EasingListValue(StyleValue[] v, int start, int count, ref List<EasingFunction> target)
		{
			var list = new List<EasingFunction>();
			for (int i = start; i < start + count; i++)
			{
				if (v[i].type == StyleValueType.Comma) continue;
				EasingMode mode = EasingMode.Ease;
				if (!Enum(v, i, ref mode)) return false;
				list.Add(new EasingFunction(mode));
			}

			target = list;
			return true;
		}

		private static bool TransitionShorthand(StyleValue[] v, int start, int count, ComputedStyle s)
		{
			var properties = new List<StylePropertyName>();
			var durations = new List<TimeValue>();
			var delays = new List<TimeValue>();
			var easings = new List<EasingFunction>();

			int i = start;
			int end = start + count;
			while (i < end)
			{
				var property = new StylePropertyName("all");
				var duration = new TimeValue(0f);
				var delay = new TimeValue(0f);
				var easing = new EasingFunction(EasingMode.Ease);
				int times = 0;
				bool any = false;
				while (i < end && v[i].type != StyleValueType.Comma)
				{
					any = true;
					if (v[i].IsTime || (v[i].type == StyleValueType.Float && v[i].number == 0f))
					{
						TimeValue time = v[i].IsTime ? v[i].ToTime() : new TimeValue(0f);
						if (times == 0) duration = time;
						else if (times == 1) delay = time;
						else return false;
						times++;
					}
					else if (v[i].type == StyleValueType.Identifier || v[i].type == StyleValueType.Keyword)
					{
						EasingMode mode = EasingMode.Ease;
						string name = v[i].type == StyleValueType.Identifier ? v[i].text : v[i].keyword.ToString().ToLowerInvariant();
						if (Enum(v, i, ref mode) && StylePropertyRegistry.FromName(name) == StylePropertyId.Unknown)
						{
							easing = new EasingFunction(mode);
						}
						else
						{
							property = new StylePropertyName(name);
						}
					}
					else
					{
						return false;
					}

					i++;
				}

				if (any)
				{
					properties.Add(property);
					durations.Add(duration);
					delays.Add(delay);
					easings.Add(easing);
				}

				i++;
			}

			s.transitionProperty = properties;
			s.transitionDuration = durations;
			s.transitionDelay = delays;
			s.transitionTimingFunction = easings;
			return true;
		}

		// ------------------------------------------------------------------ shorthands

		private static bool FourLengths(StyleValue[] v, int start, int count, ref Length top, ref Length right, ref Length bottom, ref Length left, bool allowAuto)
		{
			Length a = default, b = default, c = default, d = default;
			switch (count)
			{
				case 1:
					if (!LengthValue(v, start, ref a, allowAuto, false)) return false;
					top = right = bottom = left = a;
					return true;
				case 2:
					if (!LengthValue(v, start, ref a, allowAuto, false) || !LengthValue(v, start + 1, ref b, allowAuto, false)) return false;
					top = bottom = a;
					right = left = b;
					return true;
				case 3:
					if (!LengthValue(v, start, ref a, allowAuto, false) || !LengthValue(v, start + 1, ref b, allowAuto, false) || !LengthValue(v, start + 2, ref c, allowAuto, false)) return false;
					top = a;
					right = left = b;
					bottom = c;
					return true;
				case 4:
					if (!LengthValue(v, start, ref a, allowAuto, false) || !LengthValue(v, start + 1, ref b, allowAuto, false) || !LengthValue(v, start + 2, ref c, allowAuto, false) || !LengthValue(v, start + 3, ref d, allowAuto, false)) return false;
					top = a;
					right = b;
					bottom = c;
					left = d;
					return true;
				default:
					return false;
			}
		}

		private static bool FourFloats(StyleValue[] v, int start, int count, ref float top, ref float right, ref float bottom, ref float left)
		{
			float a = 0, b = 0, c = 0, d = 0;
			switch (count)
			{
				case 1:
					if (!FloatValue(v, start, ref a)) return false;
					top = right = bottom = left = a;
					return true;
				case 2:
					if (!FloatValue(v, start, ref a) || !FloatValue(v, start + 1, ref b)) return false;
					top = bottom = a;
					right = left = b;
					return true;
				case 3:
					if (!FloatValue(v, start, ref a) || !FloatValue(v, start + 1, ref b) || !FloatValue(v, start + 2, ref c)) return false;
					top = a;
					right = left = b;
					bottom = c;
					return true;
				case 4:
					if (!FloatValue(v, start, ref a) || !FloatValue(v, start + 1, ref b) || !FloatValue(v, start + 2, ref c) || !FloatValue(v, start + 3, ref d)) return false;
					top = a;
					right = b;
					bottom = c;
					left = d;
					return true;
				default:
					return false;
			}
		}

		private static bool FourColors(StyleValue[] v, int start, int count, ref Color4 top, ref Color4 right, ref Color4 bottom, ref Color4 left)
		{
			Color4 a = default, b = default, c = default, d = default;
			switch (count)
			{
				case 1:
					if (!ColorValue(v, start, ref a)) return false;
					top = right = bottom = left = a;
					return true;
				case 2:
					if (!ColorValue(v, start, ref a) || !ColorValue(v, start + 1, ref b)) return false;
					top = bottom = a;
					right = left = b;
					return true;
				case 3:
					if (!ColorValue(v, start, ref a) || !ColorValue(v, start + 1, ref b) || !ColorValue(v, start + 2, ref c)) return false;
					top = a;
					right = left = b;
					bottom = c;
					return true;
				case 4:
					if (!ColorValue(v, start, ref a) || !ColorValue(v, start + 1, ref b) || !ColorValue(v, start + 2, ref c) || !ColorValue(v, start + 3, ref d)) return false;
					top = a;
					right = b;
					bottom = c;
					left = d;
					return true;
				default:
					return false;
			}
		}

		private static bool FlexShorthand(StyleValue[] v, int start, int count, ComputedStyle s)
		{
			if (count == 1 && v[start].type == StyleValueType.Keyword)
			{
				if (v[start].keyword == StyleKeyword.None)
				{
					s.flexGrow = 0f;
					s.flexShrink = 0f;
					s.flexBasis = Length.Auto();
					return true;
				}

				if (v[start].keyword == StyleKeyword.Auto)
				{
					s.flexGrow = 1f;
					s.flexShrink = 1f;
					s.flexBasis = Length.Auto();
					return true;
				}

				return false;
			}

			float grow = 0f, shrink = 1f;
			Length basis = Length.Pixels(0f);
			int numbers = 0;
			bool basisSet = false;
			for (int i = start; i < start + count; i++)
			{
				if (v[i].type == StyleValueType.Float && numbers < 2)
				{
					if (numbers == 0) grow = v[i].number;
					else shrink = v[i].number;
					numbers++;
				}
				else if (!basisSet && (v[i].IsLength || v[i].IsKeyword(StyleKeyword.Auto)))
				{
					basis = v[i].ToLength();
					basisSet = true;
				}
				else
				{
					return false;
				}
			}

			if (numbers == 0 && !basisSet)
			{
				return false;
			}

			if (numbers > 0 && !basisSet)
			{
				// "flex: 1" means 1 1 0 (the basis collapses so the free space is shared), as in CSS.
				basis = Length.Pixels(0f);
			}

			if (numbers == 0)
			{
				grow = 1f;
				shrink = 1f;
			}

			s.flexGrow = grow;
			s.flexShrink = shrink;
			s.flexBasis = basis;
			return true;
		}

		private static bool TextOutlineShorthand(StyleValue[] v, int start, int count, InheritedStyle inherited)
		{
			float width = inherited.unityTextOutlineWidth;
			Color4 color = inherited.unityTextOutlineColor;
			bool any = false;
			for (int i = start; i < start + count; i++)
			{
				if (v[i].IsNumber)
				{
					width = v[i].number;
					any = true;
				}
				else if (ColorValue(v, i, ref color))
				{
					any = true;
				}
				else
				{
					return false;
				}
			}

			if (!any)
			{
				return false;
			}

			inherited.unityTextOutlineWidth = width;
			inherited.unityTextOutlineColor = color;
			return true;
		}

		#endregion
	}
}
