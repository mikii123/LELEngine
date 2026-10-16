using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>
	///     The inline style of an element (<see cref="VisualElement.style" />): values set here win over every style
	///     sheet rule. Unset properties (StyleKeyword.Null) fall back to the cascade.
	/// </summary>
	public interface IStyle
	{
		StyleEnum<Align> alignContent { get; set; }
		StyleEnum<Align> alignItems { get; set; }
		StyleEnum<Align> alignSelf { get; set; }
		StyleColor backgroundColor { get; set; }
		StyleBackground backgroundImage { get; set; }
		StyleBackgroundPosition backgroundPositionX { get; set; }
		StyleBackgroundPosition backgroundPositionY { get; set; }
		StyleBackgroundRepeat backgroundRepeat { get; set; }
		StyleBackgroundSize backgroundSize { get; set; }
		StyleColor borderBottomColor { get; set; }
		StyleLength borderBottomLeftRadius { get; set; }
		StyleLength borderBottomRightRadius { get; set; }
		StyleFloat borderBottomWidth { get; set; }
		StyleColor borderLeftColor { get; set; }
		StyleFloat borderLeftWidth { get; set; }
		StyleColor borderRightColor { get; set; }
		StyleFloat borderRightWidth { get; set; }
		StyleColor borderTopColor { get; set; }
		StyleLength borderTopLeftRadius { get; set; }
		StyleLength borderTopRightRadius { get; set; }
		StyleFloat borderTopWidth { get; set; }
		StyleLength bottom { get; set; }
		StyleColor color { get; set; }
		StyleCursor cursor { get; set; }
		StyleEnum<DisplayStyle> display { get; set; }
		StyleLength flexBasis { get; set; }
		StyleEnum<FlexDirection> flexDirection { get; set; }
		StyleFloat flexGrow { get; set; }
		StyleFloat flexShrink { get; set; }
		StyleEnum<Wrap> flexWrap { get; set; }
		StyleLength fontSize { get; set; }
		StyleLength height { get; set; }
		StyleEnum<Justify> justifyContent { get; set; }
		StyleLength left { get; set; }
		StyleLength letterSpacing { get; set; }
		StyleLength marginBottom { get; set; }
		StyleLength marginLeft { get; set; }
		StyleLength marginRight { get; set; }
		StyleLength marginTop { get; set; }
		StyleLength maxHeight { get; set; }
		StyleLength maxWidth { get; set; }
		StyleLength minHeight { get; set; }
		StyleLength minWidth { get; set; }
		StyleFloat opacity { get; set; }
		StyleEnum<Overflow> overflow { get; set; }
		StyleLength paddingBottom { get; set; }
		StyleLength paddingLeft { get; set; }
		StyleLength paddingRight { get; set; }
		StyleLength paddingTop { get; set; }
		StyleEnum<Position> position { get; set; }
		StyleLength right { get; set; }
		StyleRotate rotate { get; set; }
		StyleScale scale { get; set; }
		StyleEnum<TextOverflow> textOverflow { get; set; }
		StyleTextShadow textShadow { get; set; }
		StyleLength top { get; set; }
		StyleTransformOrigin transformOrigin { get; set; }
		StyleList<TimeValue> transitionDelay { get; set; }
		StyleList<TimeValue> transitionDuration { get; set; }
		StyleList<StylePropertyName> transitionProperty { get; set; }
		StyleList<EasingFunction> transitionTimingFunction { get; set; }
		StyleTranslate translate { get; set; }
		StyleColor unityBackgroundImageTintColor { get; set; }
		StyleEnum<ScaleMode> unityBackgroundScaleMode { get; set; }
		StyleFont unityFont { get; set; }
		StyleFontDefinition unityFontDefinition { get; set; }
		StyleEnum<FontStyle> unityFontStyleAndWeight { get; set; }
		StyleEnum<OverflowClipBox> unityOverflowClipBox { get; set; }
		StyleLength unityParagraphSpacing { get; set; }
		StyleInt unitySliceBottom { get; set; }
		StyleInt unitySliceLeft { get; set; }
		StyleInt unitySliceRight { get; set; }
		StyleFloat unitySliceScale { get; set; }
		StyleInt unitySliceTop { get; set; }
		StyleEnum<SliceType> unitySliceType { get; set; }
		StyleEnum<TextAnchor> unityTextAlign { get; set; }
		StyleColor unityTextOutlineColor { get; set; }
		StyleFloat unityTextOutlineWidth { get; set; }
		StyleEnum<TextOverflowPosition> unityTextOverflowPosition { get; set; }
		StyleEnum<Visibility> visibility { get; set; }
		StyleEnum<WhiteSpace> whiteSpace { get; set; }
		StyleLength width { get; set; }
		StyleLength wordSpacing { get; set; }
	}

	/// <summary>
	///     Inline declarations of one element, stored like sheet declarations (property id + value tokens) so the
	///     cascade applies them with the same code path. Setting a property marks the element's styles dirty.
	/// </summary>
	public sealed class InlineStyle : IStyle
	{
		#region PublicFields

		/// <summary>Bumped on every change (the cascade re-applies inline values when it changed).</summary>
		public int version { get; private set; }

		public int count => entries.Count;

		#endregion

		#region PrivateFields

		private readonly VisualElement element;
		private readonly List<InlineEntry> entries = new List<InlineEntry>(4);

		#endregion

		#region Constructors

		internal InlineStyle(VisualElement element)
		{
			this.element = element;
		}

		#endregion

		#region PublicMethods

		/// <summary>Sets a property from USS text ("10px", "flex-start", "rgba(...)", shorthands included).</summary>
		public void SetProperty(string name, string valueText)
		{
			StylePropertyId id = StylePropertyRegistry.FromName(name);
			if (id == StylePropertyId.Unknown)
			{
				Debug.LogWarning("[UI] Unknown style property '" + name + "'");
				return;
			}

			StyleValue[] values = StyleSheetParser.ParseValue(valueText);
			Set(id, id == StylePropertyId.Custom ? name : null, values, HasVariables(values));
		}

		/// <summary>Applies a USS declaration list ("width: 10px; color: red").</summary>
		public void SetFromDeclarations(string declarations, string assetPath = null)
		{
			var parser = new StyleSheetParser(new StyleSheet { AssetPath = assetPath, name = "inline" }, declarations);
			foreach (StyleDeclaration declaration in parser.ParseDeclarationList())
			{
				Set(declaration.id, declaration.name, declaration.values, declaration.usesVariables);
			}
		}

		/// <summary>Removes an inline value: the cascade decides again.</summary>
		public void Unset(StylePropertyId id, string customName = null)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				if (entries[i].id == id && (id != StylePropertyId.Custom || entries[i].customName == customName))
				{
					entries.RemoveAt(i);
					Changed(id);
					return;
				}
			}
		}

		public void Clear()
		{
			if (entries.Count == 0)
			{
				return;
			}

			entries.Clear();
			version++;
			element.IncrementVersion(VersionChangeType.Styles);
		}

		/// <summary>Writes every inline declaration into the style (after the cascade).</summary>
		internal void ApplyTo(ComputedStyle target, ComputedStyle parent)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				InlineEntry entry = entries[i];
				StyleApplier.Apply(target, parent, entry.id, entry.customName, entry.values, entry.usesVariables);
			}
		}

		internal bool Has(StylePropertyId id)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				if (entries[i].id == id)
				{
					return true;
				}
			}

			return false;
		}

		internal StyleValue[] Get(StylePropertyId id)
		{
			for (int i = 0; i < entries.Count; i++)
			{
				if (entries[i].id == id)
				{
					return entries[i].values;
				}
			}

			return null;
		}

		internal void Set(StylePropertyId id, string customName, StyleValue[] values, bool usesVariables)
		{
			if (values == null || values.Length == 0)
			{
				Unset(id, customName);
				return;
			}

			for (int i = 0; i < entries.Count; i++)
			{
				if (entries[i].id == id && (id != StylePropertyId.Custom || entries[i].customName == customName))
				{
					if (SameValues(entries[i].values, values))
					{
						return;
					}

					entries[i] = new InlineEntry { id = id, customName = customName, values = values, usesVariables = usesVariables };
					Changed(id);
					return;
				}
			}

			entries.Add(new InlineEntry { id = id, customName = customName, values = values, usesVariables = usesVariables });
			Changed(id);
		}

		#endregion

		#region PrivateMethods

		private void Changed(StylePropertyId id)
		{
			version++;
			element.IncrementVersion(VersionChangeType.Styles);
		}

		private static bool HasVariables(StyleValue[] values)
		{
			foreach (StyleValue value in values)
			{
				if (value.type == StyleValueType.Function && value.text == "var")
				{
					return true;
				}
			}

			return false;
		}

		private static bool SameValues(StyleValue[] a, StyleValue[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}

			for (int i = 0; i < a.Length; i++)
			{
				if (a[i].type != b[i].type || a[i].unit != b[i].unit || a[i].keyword != b[i].keyword || a[i].number != b[i].number
					|| a[i].color != b[i].color || !string.Equals(a[i].text, b[i].text, StringComparison.Ordinal) || !ReferenceEquals(a[i].reference, b[i].reference))
				{
					return false;
				}
			}

			return true;
		}

		// ---- writers

		// StyleKeyword.Null unsets the inline value (the cascade decides again); Undefined carries a value.

		private void SetKeywordOrValues(StylePropertyId id, StyleKeyword keyword, params StyleValue[] values)
		{
			if (keyword == StyleKeyword.Null)
			{
				Unset(id);
				return;
			}

			if (keyword == StyleKeyword.Undefined)
			{
				if (values.Length == 0)
				{
					Unset(id);
					return;
				}

				Set(id, null, values, false);
				return;
			}

			Set(id, null, new[] { StyleValue.Keyword(keyword) }, false);
		}

		private void SetLength(StylePropertyId id, StyleLength value)
		{
			if (value.keyword == StyleKeyword.Null)
			{
				Unset(id);
			}
			else if (value.keyword == StyleKeyword.Undefined)
			{
				Set(id, null, new[] { StyleValue.FromLength(value.value) }, false);
			}
			else
			{
				Set(id, null, new[] { StyleValue.Keyword(value.keyword) }, false);
			}
		}

		private void SetFloat(StylePropertyId id, StyleFloat value)
		{
			SetKeywordOrValues(id, value.keyword, StyleValue.Float(value.value));
		}

		private void SetInt(StylePropertyId id, StyleInt value)
		{
			SetKeywordOrValues(id, value.keyword, StyleValue.Float(value.value));
		}

		private void SetColor(StylePropertyId id, StyleColor value)
		{
			SetKeywordOrValues(id, value.keyword, StyleValue.FromColor(value.value));
		}

		private void SetEnum<T>(StylePropertyId id, StyleEnum<T> value)
			where T : struct, Enum
		{
			SetKeywordOrValues(id, value.keyword, StyleValue.Identifier(value.value.ToString()));
		}

		private void SetList(StylePropertyId id, StyleKeyword keyword, List<StyleValue> values)
		{
			if (keyword != StyleKeyword.Null && keyword != StyleKeyword.Undefined)
			{
				Set(id, null, new[] { StyleValue.Keyword(keyword) }, false);
				return;
			}

			if (keyword == StyleKeyword.Null || values == null || values.Count == 0)
			{
				Unset(id);
				return;
			}

			Set(id, null, values.ToArray(), false);
		}

		// ---- readers

		private StyleKeyword KeywordOf(StylePropertyId id, out StyleValue[] values)
		{
			values = Get(id);
			if (values == null)
			{
				return StyleKeyword.Null;
			}

			if (values.Length == 1 && values[0].type == StyleValueType.Keyword)
			{
				return values[0].keyword;
			}

			return StyleKeyword.Undefined;
		}

		private StyleLength GetLength(StylePropertyId id)
		{
			StyleValue[] values;
			StyleKeyword keyword = KeywordOf(id, out values);
			if (keyword == StyleKeyword.Auto || keyword == StyleKeyword.None || keyword == StyleKeyword.Undefined)
			{
				return values[0].IsLength || keyword != StyleKeyword.Undefined ? new StyleLength(values[0].ToLength()) : new StyleLength(StyleKeyword.Null);
			}

			return new StyleLength(keyword);
		}

		private StyleFloat GetFloat(StylePropertyId id)
		{
			StyleValue[] values;
			StyleKeyword keyword = KeywordOf(id, out values);
			return keyword == StyleKeyword.Undefined && values[0].IsNumber ? new StyleFloat(values[0].number) : new StyleFloat(keyword);
		}

		private StyleInt GetInt(StylePropertyId id)
		{
			StyleValue[] values;
			StyleKeyword keyword = KeywordOf(id, out values);
			return keyword == StyleKeyword.Undefined && values[0].IsNumber ? new StyleInt((int)values[0].number) : new StyleInt(keyword);
		}

		private StyleColor GetColor(StylePropertyId id)
		{
			StyleValue[] values;
			StyleKeyword keyword = KeywordOf(id, out values);
			return keyword == StyleKeyword.Undefined && values[0].type == StyleValueType.Color ? new StyleColor(values[0].color) : new StyleColor(keyword);
		}

		private StyleEnum<T> GetEnum<T>(StylePropertyId id)
			where T : struct, Enum
		{
			StyleValue[] values;
			StyleKeyword keyword = KeywordOf(id, out values);
			if (keyword != StyleKeyword.Undefined || values[0].type != StyleValueType.Identifier)
			{
				return new StyleEnum<T>(keyword);
			}

			foreach (T candidate in System.Enum.GetValues(typeof(T)))
			{
				if (string.Equals(candidate.ToString().Replace("-", ""), values[0].text.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
				{
					return new StyleEnum<T>(candidate);
				}
			}

			return new StyleEnum<T>(StyleKeyword.Null);
		}

		private Length LengthAt(StyleValue[] values, int index, Length fallback)
		{
			return index < values.Length && (values[index].IsLength || values[index].type == StyleValueType.Keyword) ? values[index].ToLength() : fallback;
		}

		#endregion

		#region IStyle

		public StyleEnum<Align> alignContent { get => GetEnum<Align>(StylePropertyId.AlignContent); set => SetEnum(StylePropertyId.AlignContent, value); }
		public StyleEnum<Align> alignItems { get => GetEnum<Align>(StylePropertyId.AlignItems); set => SetEnum(StylePropertyId.AlignItems, value); }
		public StyleEnum<Align> alignSelf { get => GetEnum<Align>(StylePropertyId.AlignSelf); set => SetEnum(StylePropertyId.AlignSelf, value); }
		public StyleColor backgroundColor { get => GetColor(StylePropertyId.BackgroundColor); set => SetColor(StylePropertyId.BackgroundColor, value); }

		public StyleBackground backgroundImage
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.BackgroundImage, out values);
				return keyword == StyleKeyword.Undefined ? new StyleBackground(new Background(values[0].reference as Texture)) : new StyleBackground(keyword);
			}
			set
			{
				if (value.keyword == StyleKeyword.Null || value.keyword == StyleKeyword.Undefined)
				{
					Set(StylePropertyId.BackgroundImage, null, new[] { value.value.texture != null ? StyleValue.Reference(value.value.texture) : StyleValue.Keyword(StyleKeyword.None) }, false);
				}
				else
				{
					Set(StylePropertyId.BackgroundImage, null, new[] { StyleValue.Keyword(value.keyword) }, false);
				}
			}
		}

		public StyleBackgroundPosition backgroundPositionX
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.BackgroundPositionX, out values);
				return keyword != StyleKeyword.Undefined ? new StyleBackgroundPosition(keyword) : new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Left, LengthAt(values, 1, 0f)));
			}
			set => SetKeywordOrValues(StylePropertyId.BackgroundPositionX, value.keyword, StyleValue.Identifier(value.value.keyword.ToString()), StyleValue.FromLength(value.value.offset));
		}

		public StyleBackgroundPosition backgroundPositionY
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.BackgroundPositionY, out values);
				return keyword != StyleKeyword.Undefined ? new StyleBackgroundPosition(keyword) : new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Top, LengthAt(values, 1, 0f)));
			}
			set => SetKeywordOrValues(StylePropertyId.BackgroundPositionY, value.keyword, StyleValue.Identifier(value.value.keyword.ToString()), StyleValue.FromLength(value.value.offset));
		}

		public StyleBackgroundRepeat backgroundRepeat
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.BackgroundRepeat, out values);
				return new StyleBackgroundRepeat(keyword);
			}
			set => SetKeywordOrValues(StylePropertyId.BackgroundRepeat, value.keyword, StyleValue.Identifier(value.value.x.ToString()), StyleValue.Identifier(value.value.y.ToString()));
		}

		public StyleBackgroundSize backgroundSize
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.BackgroundSize, out values);
				return new StyleBackgroundSize(keyword);
			}
			set
			{
				if (value.value.sizeType != BackgroundSizeType.Length && (value.keyword == StyleKeyword.Null || value.keyword == StyleKeyword.Undefined))
				{
					Set(StylePropertyId.BackgroundSize, null, new[] { StyleValue.Identifier(value.value.sizeType.ToString()) }, false);
					return;
				}

				SetKeywordOrValues(StylePropertyId.BackgroundSize, value.keyword, StyleValue.FromLength(value.value.x), StyleValue.FromLength(value.value.y));
			}
		}

		public StyleColor borderBottomColor { get => GetColor(StylePropertyId.BorderBottomColor); set => SetColor(StylePropertyId.BorderBottomColor, value); }
		public StyleLength borderBottomLeftRadius { get => GetLength(StylePropertyId.BorderBottomLeftRadius); set => SetLength(StylePropertyId.BorderBottomLeftRadius, value); }
		public StyleLength borderBottomRightRadius { get => GetLength(StylePropertyId.BorderBottomRightRadius); set => SetLength(StylePropertyId.BorderBottomRightRadius, value); }
		public StyleFloat borderBottomWidth { get => GetFloat(StylePropertyId.BorderBottomWidth); set => SetFloat(StylePropertyId.BorderBottomWidth, value); }
		public StyleColor borderLeftColor { get => GetColor(StylePropertyId.BorderLeftColor); set => SetColor(StylePropertyId.BorderLeftColor, value); }
		public StyleFloat borderLeftWidth { get => GetFloat(StylePropertyId.BorderLeftWidth); set => SetFloat(StylePropertyId.BorderLeftWidth, value); }
		public StyleColor borderRightColor { get => GetColor(StylePropertyId.BorderRightColor); set => SetColor(StylePropertyId.BorderRightColor, value); }
		public StyleFloat borderRightWidth { get => GetFloat(StylePropertyId.BorderRightWidth); set => SetFloat(StylePropertyId.BorderRightWidth, value); }
		public StyleColor borderTopColor { get => GetColor(StylePropertyId.BorderTopColor); set => SetColor(StylePropertyId.BorderTopColor, value); }
		public StyleLength borderTopLeftRadius { get => GetLength(StylePropertyId.BorderTopLeftRadius); set => SetLength(StylePropertyId.BorderTopLeftRadius, value); }
		public StyleLength borderTopRightRadius { get => GetLength(StylePropertyId.BorderTopRightRadius); set => SetLength(StylePropertyId.BorderTopRightRadius, value); }
		public StyleFloat borderTopWidth { get => GetFloat(StylePropertyId.BorderTopWidth); set => SetFloat(StylePropertyId.BorderTopWidth, value); }
		public StyleLength bottom { get => GetLength(StylePropertyId.Bottom); set => SetLength(StylePropertyId.Bottom, value); }
		public StyleColor color { get => GetColor(StylePropertyId.Color); set => SetColor(StylePropertyId.Color, value); }

		public StyleCursor cursor
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.Cursor, out values);
				return new StyleCursor(keyword);
			}
			set
			{
				if (value.keyword == StyleKeyword.Null || value.keyword == StyleKeyword.Undefined)
				{
					if (value.value.texture != null)
					{
						Set(StylePropertyId.Cursor, null, new[] { StyleValue.Reference(value.value.texture), StyleValue.Float(value.value.hotspot.X), StyleValue.Float(value.value.hotspot.Y) }, false);
					}
					else
					{
						Set(StylePropertyId.Cursor, null, new[] { StyleValue.Identifier(value.value.type.ToString()) }, false);
					}
				}
				else
				{
					Set(StylePropertyId.Cursor, null, new[] { StyleValue.Keyword(value.keyword) }, false);
				}
			}
		}

		public StyleEnum<DisplayStyle> display { get => GetEnum<DisplayStyle>(StylePropertyId.Display); set => SetEnum(StylePropertyId.Display, value); }
		public StyleLength flexBasis { get => GetLength(StylePropertyId.FlexBasis); set => SetLength(StylePropertyId.FlexBasis, value); }
		public StyleEnum<FlexDirection> flexDirection { get => GetEnum<FlexDirection>(StylePropertyId.FlexDirection); set => SetEnum(StylePropertyId.FlexDirection, value); }
		public StyleFloat flexGrow { get => GetFloat(StylePropertyId.FlexGrow); set => SetFloat(StylePropertyId.FlexGrow, value); }
		public StyleFloat flexShrink { get => GetFloat(StylePropertyId.FlexShrink); set => SetFloat(StylePropertyId.FlexShrink, value); }
		public StyleEnum<Wrap> flexWrap { get => GetEnum<Wrap>(StylePropertyId.FlexWrap); set => SetEnum(StylePropertyId.FlexWrap, value); }
		public StyleLength fontSize { get => GetLength(StylePropertyId.FontSize); set => SetLength(StylePropertyId.FontSize, value); }
		public StyleLength height { get => GetLength(StylePropertyId.Height); set => SetLength(StylePropertyId.Height, value); }
		public StyleEnum<Justify> justifyContent { get => GetEnum<Justify>(StylePropertyId.JustifyContent); set => SetEnum(StylePropertyId.JustifyContent, value); }
		public StyleLength left { get => GetLength(StylePropertyId.Left); set => SetLength(StylePropertyId.Left, value); }
		public StyleLength letterSpacing { get => GetLength(StylePropertyId.LetterSpacing); set => SetLength(StylePropertyId.LetterSpacing, value); }
		public StyleLength marginBottom { get => GetLength(StylePropertyId.MarginBottom); set => SetLength(StylePropertyId.MarginBottom, value); }
		public StyleLength marginLeft { get => GetLength(StylePropertyId.MarginLeft); set => SetLength(StylePropertyId.MarginLeft, value); }
		public StyleLength marginRight { get => GetLength(StylePropertyId.MarginRight); set => SetLength(StylePropertyId.MarginRight, value); }
		public StyleLength marginTop { get => GetLength(StylePropertyId.MarginTop); set => SetLength(StylePropertyId.MarginTop, value); }
		public StyleLength maxHeight { get => GetLength(StylePropertyId.MaxHeight); set => SetLength(StylePropertyId.MaxHeight, value); }
		public StyleLength maxWidth { get => GetLength(StylePropertyId.MaxWidth); set => SetLength(StylePropertyId.MaxWidth, value); }
		public StyleLength minHeight { get => GetLength(StylePropertyId.MinHeight); set => SetLength(StylePropertyId.MinHeight, value); }
		public StyleLength minWidth { get => GetLength(StylePropertyId.MinWidth); set => SetLength(StylePropertyId.MinWidth, value); }
		public StyleFloat opacity { get => GetFloat(StylePropertyId.Opacity); set => SetFloat(StylePropertyId.Opacity, value); }
		public StyleEnum<Overflow> overflow { get => GetEnum<Overflow>(StylePropertyId.Overflow); set => SetEnum(StylePropertyId.Overflow, value); }
		public StyleLength paddingBottom { get => GetLength(StylePropertyId.PaddingBottom); set => SetLength(StylePropertyId.PaddingBottom, value); }
		public StyleLength paddingLeft { get => GetLength(StylePropertyId.PaddingLeft); set => SetLength(StylePropertyId.PaddingLeft, value); }
		public StyleLength paddingRight { get => GetLength(StylePropertyId.PaddingRight); set => SetLength(StylePropertyId.PaddingRight, value); }
		public StyleLength paddingTop { get => GetLength(StylePropertyId.PaddingTop); set => SetLength(StylePropertyId.PaddingTop, value); }
		public StyleEnum<Position> position { get => GetEnum<Position>(StylePropertyId.Position); set => SetEnum(StylePropertyId.Position, value); }
		public StyleLength right { get => GetLength(StylePropertyId.Right); set => SetLength(StylePropertyId.Right, value); }

		public StyleRotate rotate
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.Rotate, out values);
				return keyword == StyleKeyword.Undefined && values[0].IsAngle ? new StyleRotate(new Rotate(values[0].ToAngle())) : new StyleRotate(keyword);
			}
			set => SetKeywordOrValues(StylePropertyId.Rotate, value.keyword, StyleValue.FromAngle(value.value.angle));
		}

		public StyleScale scale
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.Scale, out values);
				return keyword == StyleKeyword.Undefined && values.Length >= 2 ? new StyleScale(new Scale(new Vector2(values[0].number, values[1].number))) : new StyleScale(keyword);
			}
			set => SetKeywordOrValues(StylePropertyId.Scale, value.keyword, StyleValue.Float(value.value.value.X), StyleValue.Float(value.value.value.Y));
		}

		public StyleEnum<TextOverflow> textOverflow { get => GetEnum<TextOverflow>(StylePropertyId.TextOverflow); set => SetEnum(StylePropertyId.TextOverflow, value); }

		public StyleTextShadow textShadow
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.TextShadow, out values);
				if (keyword != StyleKeyword.Undefined || values.Length < 4)
				{
					return new StyleTextShadow(keyword);
				}

				return new StyleTextShadow(new TextShadow { offset = new Vector2(values[0].number, values[1].number), blurRadius = values[2].number, color = values[3].color });
			}
			set => SetKeywordOrValues(StylePropertyId.TextShadow, value.keyword, StyleValue.Pixels(value.value.offset.X), StyleValue.Pixels(value.value.offset.Y), StyleValue.Pixels(value.value.blurRadius), StyleValue.FromColor(value.value.color));
		}

		public StyleLength top { get => GetLength(StylePropertyId.Top); set => SetLength(StylePropertyId.Top, value); }

		public StyleTransformOrigin transformOrigin
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.TransformOrigin, out values);
				return keyword == StyleKeyword.Undefined && values.Length >= 2 ? new StyleTransformOrigin(new TransformOrigin(values[0].ToLength(), values[1].ToLength())) : new StyleTransformOrigin(keyword);
			}
			set => SetKeywordOrValues(StylePropertyId.TransformOrigin, value.keyword, StyleValue.FromLength(value.value.x), StyleValue.FromLength(value.value.y));
		}

		public StyleList<TimeValue> transitionDelay
		{
			get => new StyleList<TimeValue>(KeywordOf(StylePropertyId.TransitionDelay, out _));
			set => SetList(StylePropertyId.TransitionDelay, value.keyword, TimeList(value.value));
		}

		public StyleList<TimeValue> transitionDuration
		{
			get => new StyleList<TimeValue>(KeywordOf(StylePropertyId.TransitionDuration, out _));
			set => SetList(StylePropertyId.TransitionDuration, value.keyword, TimeList(value.value));
		}

		public StyleList<StylePropertyName> transitionProperty
		{
			get => new StyleList<StylePropertyName>(KeywordOf(StylePropertyId.TransitionProperty, out _));
			set
			{
				List<StyleValue> values = null;
				if (value.value != null)
				{
					values = new List<StyleValue>();
					foreach (StylePropertyName property in value.value)
					{
						if (values.Count > 0) values.Add(StyleValue.Comma());
						values.Add(StyleValue.Identifier(property.name ?? "all"));
					}
				}

				SetList(StylePropertyId.TransitionProperty, value.keyword, values);
			}
		}

		public StyleList<EasingFunction> transitionTimingFunction
		{
			get => new StyleList<EasingFunction>(KeywordOf(StylePropertyId.TransitionTimingFunction, out _));
			set
			{
				List<StyleValue> values = null;
				if (value.value != null)
				{
					values = new List<StyleValue>();
					foreach (EasingFunction easing in value.value)
					{
						if (values.Count > 0) values.Add(StyleValue.Comma());
						values.Add(StyleValue.Identifier(easing.mode.ToString()));
					}
				}

				SetList(StylePropertyId.TransitionTimingFunction, value.keyword, values);
			}
		}

		public StyleTranslate translate
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.Translate, out values);
				return keyword == StyleKeyword.Undefined && values.Length >= 2 ? new StyleTranslate(new Translate(values[0].ToLength(), values[1].ToLength())) : new StyleTranslate(keyword);
			}
			set => SetKeywordOrValues(StylePropertyId.Translate, value.keyword, StyleValue.FromLength(value.value.x), StyleValue.FromLength(value.value.y));
		}

		public StyleColor unityBackgroundImageTintColor { get => GetColor(StylePropertyId.UnityBackgroundImageTintColor); set => SetColor(StylePropertyId.UnityBackgroundImageTintColor, value); }
		public StyleEnum<ScaleMode> unityBackgroundScaleMode { get => GetEnum<ScaleMode>(StylePropertyId.UnityBackgroundScaleMode); set => SetEnum(StylePropertyId.UnityBackgroundScaleMode, value); }

		public StyleFont unityFont
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.UnityFont, out values);
				return keyword == StyleKeyword.Undefined ? new StyleFont(values[0].reference as FontAsset) : new StyleFont(keyword);
			}
			set => SetKeywordOrValues(StylePropertyId.UnityFont, value.keyword, value.value != null ? StyleValue.Reference(value.value) : StyleValue.Keyword(StyleKeyword.None));
		}

		public StyleFontDefinition unityFontDefinition
		{
			get
			{
				StyleValue[] values;
				StyleKeyword keyword = KeywordOf(StylePropertyId.UnityFontDefinition, out values);
				return keyword == StyleKeyword.Undefined ? new StyleFontDefinition(new FontDefinition(values[0].reference as FontAsset)) : new StyleFontDefinition(keyword);
			}
			set => SetKeywordOrValues(StylePropertyId.UnityFontDefinition, value.keyword, value.value.fontAsset != null ? StyleValue.Reference(value.value.fontAsset) : StyleValue.Keyword(StyleKeyword.None));
		}

		public StyleEnum<FontStyle> unityFontStyleAndWeight { get => GetEnum<FontStyle>(StylePropertyId.UnityFontStyleAndWeight); set => SetEnum(StylePropertyId.UnityFontStyleAndWeight, value); }
		public StyleEnum<OverflowClipBox> unityOverflowClipBox { get => GetEnum<OverflowClipBox>(StylePropertyId.UnityOverflowClipBox); set => SetEnum(StylePropertyId.UnityOverflowClipBox, value); }
		public StyleLength unityParagraphSpacing { get => GetLength(StylePropertyId.UnityParagraphSpacing); set => SetLength(StylePropertyId.UnityParagraphSpacing, value); }
		public StyleInt unitySliceBottom { get => GetInt(StylePropertyId.UnitySliceBottom); set => SetInt(StylePropertyId.UnitySliceBottom, value); }
		public StyleInt unitySliceLeft { get => GetInt(StylePropertyId.UnitySliceLeft); set => SetInt(StylePropertyId.UnitySliceLeft, value); }
		public StyleInt unitySliceRight { get => GetInt(StylePropertyId.UnitySliceRight); set => SetInt(StylePropertyId.UnitySliceRight, value); }
		public StyleFloat unitySliceScale { get => GetFloat(StylePropertyId.UnitySliceScale); set => SetFloat(StylePropertyId.UnitySliceScale, value); }
		public StyleInt unitySliceTop { get => GetInt(StylePropertyId.UnitySliceTop); set => SetInt(StylePropertyId.UnitySliceTop, value); }
		public StyleEnum<SliceType> unitySliceType { get => GetEnum<SliceType>(StylePropertyId.UnitySliceType); set => SetEnum(StylePropertyId.UnitySliceType, value); }
		public StyleEnum<TextAnchor> unityTextAlign { get => GetEnum<TextAnchor>(StylePropertyId.UnityTextAlign); set => SetEnum(StylePropertyId.UnityTextAlign, value); }
		public StyleColor unityTextOutlineColor { get => GetColor(StylePropertyId.UnityTextOutlineColor); set => SetColor(StylePropertyId.UnityTextOutlineColor, value); }
		public StyleFloat unityTextOutlineWidth { get => GetFloat(StylePropertyId.UnityTextOutlineWidth); set => SetFloat(StylePropertyId.UnityTextOutlineWidth, value); }
		public StyleEnum<TextOverflowPosition> unityTextOverflowPosition { get => GetEnum<TextOverflowPosition>(StylePropertyId.UnityTextOverflowPosition); set => SetEnum(StylePropertyId.UnityTextOverflowPosition, value); }
		public StyleEnum<Visibility> visibility { get => GetEnum<Visibility>(StylePropertyId.Visibility); set => SetEnum(StylePropertyId.Visibility, value); }
		public StyleEnum<WhiteSpace> whiteSpace { get => GetEnum<WhiteSpace>(StylePropertyId.WhiteSpace); set => SetEnum(StylePropertyId.WhiteSpace, value); }
		public StyleLength width { get => GetLength(StylePropertyId.Width); set => SetLength(StylePropertyId.Width, value); }
		public StyleLength wordSpacing { get => GetLength(StylePropertyId.WordSpacing); set => SetLength(StylePropertyId.WordSpacing, value); }

		private static List<StyleValue> TimeList(List<TimeValue> times)
		{
			if (times == null)
			{
				return null;
			}

			var values = new List<StyleValue>();
			foreach (TimeValue time in times)
			{
				if (values.Count > 0) values.Add(StyleValue.Comma());
				values.Add(StyleValue.FromTime(time));
			}

			return values;
		}

		#endregion

		#region NestedTypes

		private struct InlineEntry
		{
			public StylePropertyId id;
			public string customName;
			public StyleValue[] values;
			public bool usesVariables;
		}

		#endregion
	}
}
