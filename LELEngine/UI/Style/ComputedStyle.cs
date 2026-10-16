using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>
	///     The values text elements inherit from their ancestors (color, font, text layout, visibility, cursor).
	///     Shared between an element and its descendants until one of them overrides a value (copy on write).
	/// </summary>
	public sealed class InheritedStyle
	{
		#region PublicFields

		public static readonly InheritedStyle Default = new InheritedStyle();

		public Color4 color = new Color4(0f, 0f, 0f, 1f);
		public Cursor cursor = new Cursor(CursorType.Arrow);
		public Length fontSize = Length.Pixels(12f);
		public FontAsset unityFont;
		public FontDefinition unityFontDefinition;
		public FontStyle unityFontStyleAndWeight = FontStyle.Normal;
		public Length letterSpacing = Length.Pixels(0f);
		public Length wordSpacing = Length.Pixels(0f);
		public WhiteSpace whiteSpace = WhiteSpace.Normal;
		public TextAnchor unityTextAlign = TextAnchor.UpperLeft;
		public Length unityParagraphSpacing = Length.Pixels(0f);
		public TextShadow textShadow;
		public Color4 unityTextOutlineColor = new Color4(0f, 0f, 0f, 1f);
		public float unityTextOutlineWidth;
		public TextOverflowPosition unityTextOverflowPosition = TextOverflowPosition.End;
		public Visibility visibility = Visibility.Visible;

		/// <summary>USS variables (--name) visible to this element and its descendants.</summary>
		public Dictionary<string, StyleValue[]> customProperties;

		#endregion

		#region PublicMethods

		public InheritedStyle Clone()
		{
			return (InheritedStyle)MemberwiseClone();
		}

		/// <summary>The font to draw text with: the font definition, the -unity-font, or the default font.</summary>
		public FontAsset ResolveFont()
		{
			return unityFontDefinition.fontAsset ?? unityFont ?? FontAsset.Default;
		}

		public StyleValue[] GetCustomProperty(string name)
		{
			StyleValue[] values;
			return customProperties != null && customProperties.TryGetValue(name, out values) ? values : null;
		}

		#endregion
	}

	/// <summary>
	///     The resolved style of an element after the USS cascade, inline styles and transitions. Elements that match
	///     the same rules share one instance; a transition or an inline value gives the element its own copy.
	///     Lengths stay in their declared unit; the layout pass resolves percentages.
	/// </summary>
	public sealed class ComputedStyle
	{
		#region PublicFields

		public static readonly ComputedStyle Default = new ComputedStyle();

		// Layout
		public Align alignContent = Align.FlexStart;
		public Align alignItems = Align.Stretch;
		public Align alignSelf = Align.Auto;
		public Length bottom = Length.Auto();
		public DisplayStyle display = DisplayStyle.Flex;
		public Length flexBasis = Length.Auto();
		public FlexDirection flexDirection = FlexDirection.Column;
		public float flexGrow;
		public float flexShrink = 1f;
		public Wrap flexWrap = Wrap.NoWrap;
		public Length height = Length.Auto();
		public Justify justifyContent = Justify.FlexStart;
		public Length left = Length.Auto();
		public Length marginBottom = Length.Pixels(0f);
		public Length marginLeft = Length.Pixels(0f);
		public Length marginRight = Length.Pixels(0f);
		public Length marginTop = Length.Pixels(0f);
		public Length maxHeight = Length.None();
		public Length maxWidth = Length.None();
		public Length minHeight = Length.Auto();
		public Length minWidth = Length.Auto();
		public Length paddingBottom = Length.Pixels(0f);
		public Length paddingLeft = Length.Pixels(0f);
		public Length paddingRight = Length.Pixels(0f);
		public Length paddingTop = Length.Pixels(0f);
		public Position position = Position.Relative;
		public Length right = Length.Auto();
		public Length top = Length.Auto();
		public Length width = Length.Auto();
		public float borderBottomWidth;
		public float borderLeftWidth;
		public float borderRightWidth;
		public float borderTopWidth;

		// Visual
		public Color4 backgroundColor = new Color4(0f, 0f, 0f, 0f);
		public Background backgroundImage;
		public BackgroundPosition backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Left);
		public BackgroundPosition backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Top);
		public BackgroundRepeat backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
		public BackgroundSize backgroundSize = new BackgroundSize(Length.Auto(), Length.Auto());
		public Color4 unityBackgroundImageTintColor = new Color4(1f, 1f, 1f, 1f);
		public ScaleMode unityBackgroundScaleMode = ScaleMode.StretchToFill;
		public Color4 borderBottomColor = new Color4(0f, 0f, 0f, 1f);
		public Color4 borderLeftColor = new Color4(0f, 0f, 0f, 1f);
		public Color4 borderRightColor = new Color4(0f, 0f, 0f, 1f);
		public Color4 borderTopColor = new Color4(0f, 0f, 0f, 1f);
		public Length borderBottomLeftRadius = Length.Pixels(0f);
		public Length borderBottomRightRadius = Length.Pixels(0f);
		public Length borderTopLeftRadius = Length.Pixels(0f);
		public Length borderTopRightRadius = Length.Pixels(0f);
		public float opacity = 1f;
		public Overflow overflow = Overflow.Visible;
		public OverflowClipBox unityOverflowClipBox = OverflowClipBox.PaddingBox;
		public int unitySliceBottom;
		public int unitySliceLeft;
		public int unitySliceRight;
		public int unitySliceTop;
		public float unitySliceScale = 1f;
		public SliceType unitySliceType = SliceType.Stretch;
		public TextOverflow textOverflow = TextOverflow.Clip;

		// Transform
		public Translate translate = Translate.None();
		public Rotate rotate = Rotate.None();
		public Scale scale = Scale.None();
		public TransformOrigin transformOrigin = TransformOrigin.Initial();

		// Transitions (null lists mean the initial value)
		public List<StylePropertyName> transitionProperty;
		public List<TimeValue> transitionDuration;
		public List<TimeValue> transitionDelay;
		public List<EasingFunction> transitionTimingFunction;

		/// <summary>Inherited values (shared with the ancestors that did not override them).</summary>
		public InheritedStyle inherited = InheritedStyle.Default;

		#endregion

		#region PublicMethods

		public ComputedStyle Clone()
		{
			return (ComputedStyle)MemberwiseClone();
		}

		/// <summary>Writes the inherited values: gives this style its own copy of them if they are shared.</summary>
		public InheritedStyle OwnInherited(InheritedStyle parentInherited)
		{
			if (ReferenceEquals(inherited, parentInherited) || ReferenceEquals(inherited, InheritedStyle.Default))
			{
				inherited = inherited.Clone();
			}

			return inherited;
		}

		/// <summary>True when transitions can start on this element (a duration is set).</summary>
		public bool HasTransitions
		{
			get
			{
				if (transitionDuration == null)
				{
					return false;
				}

				for (int i = 0; i < transitionDuration.Count; i++)
				{
					if (transitionDuration[i].value > 0f)
					{
						return true;
					}
				}

				return false;
			}
		}

		#endregion
	}
}
