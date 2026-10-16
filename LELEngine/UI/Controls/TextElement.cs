using System;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>
	///     An element that displays text (base of <see cref="Label" /> and <see cref="Button" />). The text is laid out
	///     with the element's font styles; the element measures itself for the layout and caches its glyph geometry.
	/// </summary>
	[UxmlElement]
	public class TextElement : VisualElement, INotifyValueChanged<string>
	{
		#region PublicFields

		public static readonly string ussClassName = "unity-text-element";

		[UxmlAttribute]
		public virtual string text
		{
			get => m_Text;
			set
			{
				value ??= "";
				if (string.Equals(m_Text, value, StringComparison.Ordinal))
				{
					return;
				}

				m_Text = value;
				textVersion++;
				IncrementVersion(VersionChangeType.Text);
			}
		}

		/// <summary>Parses rich text tags (&lt;b&gt;, &lt;color=...&gt;, &lt;size=...&gt;, ...).</summary>
		[UxmlAttribute("enable-rich-text")]
		public bool enableRichText
		{
			get => m_EnableRichText;
			set
			{
				if (m_EnableRichText != value)
				{
					m_EnableRichText = value;
					textVersion++;
					IncrementVersion(VersionChangeType.Text);
				}
			}
		}

		/// <summary>Shows the full text as a tooltip when it is cut with an ellipsis.</summary>
		[UxmlAttribute("display-tooltip-when-elided")]
		public bool displayTooltipWhenElided { get; set; } = true;

		/// <summary>The text was cut with an ellipsis in the last layout.</summary>
		public bool isElided => lastRenderLayout != null && lastRenderLayout.elided;

		/// <summary>Accepts \n, \t and \\ escape sequences in UXML text (kept for compatibility; UXML text is used as is).</summary>
		[UxmlAttribute("parse-escape-sequences")]
		public bool parseEscapeSequences { get; set; }

		string INotifyValueChanged<string>.value
		{
			get => text;
			set
			{
				string previous = text;
				if (string.Equals(previous, value, StringComparison.Ordinal))
				{
					return;
				}

				if (panel != null)
				{
					using (ChangeEvent<string> evt = ChangeEvent<string>.GetPooled(previous, value))
					{
						evt.target = this;
						text = value;
						SendEvent(evt);
					}
				}
				else
				{
					text = value;
				}
			}
		}

		#endregion

		#region InternalFields

		internal override bool requiresMeasure => true;
		internal int textVersion;

		#endregion

		#region PrivateFields

		private string m_Text = "";
		private bool m_EnableRichText = true;
		private TextLayoutResult lastMeasureLayout;
		private TextLayoutResult lastRenderLayout;
		private int renderLayoutHash;
		private const int MaxCachedMeasures = 3;
		private readonly TextLayoutResult[] measureCache = new TextLayoutResult[MaxCachedMeasures];
		private int measureCacheNext;

		#endregion

		#region Constructors

		public TextElement()
		{
			AddToClassList(ussClassName);
			pickingMode = PickingMode.Ignore;
		}

		#endregion

		#region PublicMethods

		public void SetValueWithoutNotify(string newValue)
		{
			text = newValue;
		}

		/// <summary>Measures the element's text under constraints (the layout engine calls this).</summary>
		public Vector2 MeasureTextSize(float width, MeasureMode widthMode, float height, MeasureMode heightMode)
		{
			return Measure(width, widthMode, height, heightMode);
		}

		#endregion

		#region InternalMethods

		internal override Vector2 Measure(float width, MeasureMode widthMode, float height, MeasureMode heightMode)
		{
			TextSettings settings = BuildSettings(false);
			int hash = SettingsHash(settings);
			for (int i = 0; i < MaxCachedMeasures; i++)
			{
				TextLayoutResult cached = measureCache[i];
				if (cached != null && cached.settingsHash == hash && cached.widthMode == widthMode && cached.heightMode == heightMode && Same(cached.constraintWidth, width) && Same(cached.constraintHeight, height) && ReferenceEquals(cached.text, settings.text))
				{
					return cached.size;
				}
			}

			TextLayoutResult result = TextGenerator.Generate(settings, width, widthMode, height, heightMode);
			result.settingsHash = hash;
			measureCache[measureCacheNext] = result;
			measureCacheNext = (measureCacheNext + 1) % MaxCachedMeasures;
			lastMeasureLayout = result;
			return result.size;
		}

		/// <summary>The glyph geometry for the element's current content rectangle (regenerated only when something changed).</summary>
		internal TextLayoutResult GetRenderLayout(bool wantCharPositions = false)
		{
			Rect content = contentRect;
			TextSettings settings = BuildSettings(wantCharPositions);
			int hash = SettingsHash(settings);
			if (lastRenderLayout != null && renderLayoutHash == hash && Same(lastRenderLayout.constraintWidth, content.width) && Same(lastRenderLayout.constraintHeight, content.height)
				&& ReferenceEquals(lastRenderLayout.text, settings.text) && (!wantCharPositions || lastRenderLayout.charPositions != null))
			{
				return lastRenderLayout;
			}

			// Unwrapped text with no width constraint still aligns inside the element's box.
			lastRenderLayout = TextGenerator.Generate(settings, content.width, MeasureMode.Exactly, content.height, MeasureMode.Exactly);
			lastRenderLayout.settingsHash = hash;
			renderLayoutHash = hash;
			return lastRenderLayout;
		}

		internal void InvalidateTextLayout()
		{
			lastRenderLayout = null;
			for (int i = 0; i < MaxCachedMeasures; i++)
			{
				measureCache[i] = null;
			}

			layoutNode.InvalidateMeasure();
		}

		#endregion

		#region ProtectedMethods

		protected override void ExecuteDefaultAction(EventBase evt)
		{
			base.ExecuteDefaultAction(evt);
			if (evt is TooltipEvent tooltipEvent && displayTooltipWhenElided && isElided && string.IsNullOrEmpty(tooltip))
			{
				tooltipEvent.tooltip = text;
				tooltipEvent.rect = worldBound;
				tooltipEvent.StopPropagation();
			}
		}

		#endregion

		#region PrivateMethods

		private TextSettings BuildSettings(bool wantCharPositions)
		{
			TextSettings settings = TextMeasurement.BuildSettings(this, m_Text);
			settings.richText = m_EnableRichText;
			settings.wantCharPositions = wantCharPositions;
			return settings;
		}

		private int SettingsHash(in TextSettings s)
		{
			return HashCode.Combine(
				HashCode.Combine(textVersion, RuntimeHelpers.GetHashCode(s.font), s.fontSize, (int)s.fontStyle, (int)s.whiteSpace, (int)s.anchor, s.letterSpacing, s.wordSpacing),
				HashCode.Combine(s.paragraphSpacing, s.richText, (int)s.overflow, (int)s.overflowPosition, s.clipOverflow, s.wantCharPositions, s.color.GetHashCode(), s.font?.atlasVersion ?? 0));
		}

		private static bool Same(float a, float b)
		{
			return (float.IsNaN(a) && float.IsNaN(b)) || Math.Abs(a - b) < 0.01f;
		}

		#endregion
	}
}
