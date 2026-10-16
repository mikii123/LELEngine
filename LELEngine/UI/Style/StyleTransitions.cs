using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>Per-element transition state: the style being animated towards and the running interpolations.</summary>
	internal sealed class StyleTransitionState
	{
		public ComputedStyle target;
		public ComputedStyle animated;
		public readonly List<RunningTransition> running = new List<RunningTransition>(4);
	}

	internal struct RunningTransition
	{
		public StylePropertyId id;
		public AnimValue from;
		public AnimValue to;
		public long startMs;
		public float durationMs;
		public float delayMs;
		public EasingMode easing;
		public bool started;
	}

	/// <summary>Union of the value types transitions interpolate.</summary>
	internal struct AnimValue
	{
		public float f;
		public Length length;
		public Color4 color;
		public Translate translate;
		public Rotate rotate;
		public Scale scale;
		public TransformOrigin origin;
		public TextShadow shadow;
	}

	/// <summary>
	///     Starts, advances and finishes USS transitions. When an element's resolved style changes a property listed in
	///     transition-property with a non-zero duration, the element gets its own animated style copy that moves from
	///     the value it showed to the new value over the duration; the shared target style is restored at the end.
	/// </summary>
	internal sealed class TransitionRunner
	{
		#region PrivateFields

		private readonly BaseVisualElementPanel panel;
		private readonly List<VisualElement> animating = new List<VisualElement>();
		private static readonly StylePropertyId[] animatableProperties = BuildAnimatableList();

		#endregion

		#region Constructors

		public TransitionRunner(BaseVisualElementPanel panel)
		{
			this.panel = panel;
		}

		#endregion

		#region PublicMethods

		public int activeCount => animating.Count;

		/// <summary>
		///     The element's target style changed: starts transitions for the animatable properties that differ and
		///     returns the style the element shows now (the animated copy, or the new target when nothing animates).
		/// </summary>
		public ComputedStyle OnStyleChanged(VisualElement element, ComputedStyle displayedOld, ComputedStyle oldTarget, ComputedStyle newTarget)
		{
			StyleTransitionState state = element.transitionState;
			bool hasDefinitions = newTarget.HasTransitions;
			if (!hasDefinitions && state == null)
			{
				return newTarget;
			}

			if (!hasDefinitions)
			{
				Cancel(element);
				return newTarget;
			}

			long now = panel.timeMs;
			bool anyStarted = false;
			if (!ReferenceEquals(oldTarget, newTarget))
			{
				for (int i = 0; i < animatableProperties.Length; i++)
				{
					StylePropertyId id = animatableProperties[i];
					if (StylePropertyRegistry.IsInherited(id) && ReferenceEquals(oldTarget.inherited, newTarget.inherited))
					{
						continue;
					}

					float duration, delay;
					EasingMode easing;
					if (!FindDefinition(newTarget, id, out duration, out delay, out easing))
					{
						continue;
					}

					AnimValue to = StyleAnimation.Get(newTarget, id);
					AnimValue current = StyleAnimation.Get(displayedOld, id);
					if (StyleAnimation.Equal(StyleAnimation.Get(oldTarget, id), to, id) && (state == null || !HasRunning(state, id)))
					{
						continue;
					}

					if (StyleAnimation.Equal(current, to, id))
					{
						// Already showing the new value (a transition reversed to its start): drop the running one.
						if (state != null)
						{
							RemoveRunning(element, state, id, true);
						}

						continue;
					}

					state ??= element.transitionState = new StyleTransitionState();
					RemoveRunning(element, state, id, true);
					state.running.Add(new RunningTransition
					{
						id = id,
						from = current,
						to = to,
						startMs = now,
						durationMs = duration * 1000f,
						delayMs = delay * 1000f,
						easing = easing
					});
					anyStarted = true;
					using (TransitionRunEvent evt = TransitionRunEvent.GetPooled(new StylePropertyName(StylePropertyRegistry.GetName(id)), 0))
					{
						evt.target = element;
						element.SendEvent(evt);
					}
				}
			}

			if (state == null || state.running.Count == 0)
			{
				if (state != null)
				{
					element.transitionState = null;
					animating.Remove(element);
				}

				return newTarget;
			}

			state.target = newTarget;
			state.animated = newTarget.Clone();
			state.animated.inherited = newTarget.inherited;
			bool inheritedAnimated = false;
			for (int i = 0; i < state.running.Count; i++)
			{
				if (StylePropertyRegistry.IsInherited(state.running[i].id))
				{
					inheritedAnimated = true;
					break;
				}
			}

			if (inheritedAnimated)
			{
				state.animated.inherited = newTarget.inherited.Clone();
			}

			// Write the current values so the element does not jump before the first Update.
			for (int i = 0; i < state.running.Count; i++)
			{
				RunningTransition run = state.running[i];
				float t = Progress(run, now);
				StyleAnimation.Set(state.animated, run.id, StyleAnimation.Lerp(run.from, run.to, Ease(run.easing, t), run.id));
			}

			if (anyStarted && !animating.Contains(element))
			{
				animating.Add(element);
			}

			return state.animated;
		}

		/// <summary>Advances every running transition to the current time.</summary>
		public void Update(long nowMs)
		{
			if (animating.Count == 0)
			{
				return;
			}

			for (int e = animating.Count - 1; e >= 0; e--)
			{
				VisualElement element = animating[e];
				StyleTransitionState state = element.transitionState;
				if (state == null || element.elementPanel != panel)
				{
					animating.RemoveAt(e);
					continue;
				}

				StyleEffect effect = StyleEffect.None;
				for (int i = state.running.Count - 1; i >= 0; i--)
				{
					RunningTransition run = state.running[i];
					float t = Progress(run, nowMs);
					if (!run.started && nowMs - run.startMs >= run.delayMs)
					{
						run.started = true;
						state.running[i] = run;
						using (TransitionStartEvent evt = TransitionStartEvent.GetPooled(new StylePropertyName(StylePropertyRegistry.GetName(run.id)), 0))
						{
							evt.target = element;
							element.SendEvent(evt);
						}
					}

					AnimValue value = t >= 1f ? run.to : StyleAnimation.Lerp(run.from, run.to, Ease(run.easing, t), run.id);
					StyleAnimation.Set(state.animated, run.id, value);
					effect |= StylePropertyRegistry.GetEffect(run.id);
					if (t >= 1f)
					{
						state.running.RemoveAt(i);
						using (TransitionEndEvent evt = TransitionEndEvent.GetPooled(new StylePropertyName(StylePropertyRegistry.GetName(run.id)), run.durationMs / 1000.0))
						{
							evt.target = element;
							element.SendEvent(evt);
						}
					}
				}

				if (state.running.Count == 0)
				{
					element.computedStyle = state.target;
					element.transitionState = null;
					animating.RemoveAt(e);
				}

				StyleResolver.ApplyEffect(element, effect);
				if ((effect & StyleEffect.Inherited) != 0)
				{
					element.ForEachDescendant(d => d.IncrementVersion(VersionChangeType.Repaint));
				}
			}
		}

		/// <summary>Stops every transition of the element (it shows its target style).</summary>
		public void Cancel(VisualElement element)
		{
			StyleTransitionState state = element.transitionState;
			if (state == null)
			{
				return;
			}

			for (int i = 0; i < state.running.Count; i++)
			{
				using (TransitionCancelEvent evt = TransitionCancelEvent.GetPooled(new StylePropertyName(StylePropertyRegistry.GetName(state.running[i].id)), 0))
				{
					evt.target = element;
					element.SendEvent(evt);
				}
			}

			state.running.Clear();
			if (state.target != null)
			{
				element.computedStyle = state.target;
			}

			element.transitionState = null;
			animating.Remove(element);
		}

		#endregion

		#region PrivateMethods

		private static float Progress(in RunningTransition run, long now)
		{
			float elapsed = now - run.startMs - run.delayMs;
			if (elapsed <= 0f)
			{
				return 0f;
			}

			return run.durationMs <= 0f ? 1f : Math.Min(1f, elapsed / run.durationMs);
		}

		private static bool HasRunning(StyleTransitionState state, StylePropertyId id)
		{
			for (int i = 0; i < state.running.Count; i++)
			{
				if (state.running[i].id == id)
				{
					return true;
				}
			}

			return false;
		}

		private static void RemoveRunning(VisualElement element, StyleTransitionState state, StylePropertyId id, bool sendCancel)
		{
			for (int i = state.running.Count - 1; i >= 0; i--)
			{
				if (state.running[i].id != id)
				{
					continue;
				}

				state.running.RemoveAt(i);
				if (sendCancel)
				{
					using (TransitionCancelEvent evt = TransitionCancelEvent.GetPooled(new StylePropertyName(StylePropertyRegistry.GetName(id)), 0))
					{
						evt.target = element;
						element.SendEvent(evt);
					}
				}
			}
		}

		/// <summary>Finds the transition definition that applies to the property (its name, a shorthand of it, or "all").</summary>
		private static bool FindDefinition(ComputedStyle style, StylePropertyId id, out float duration, out float delay, out EasingMode easing)
		{
			duration = 0f;
			delay = 0f;
			easing = EasingMode.Ease;
			List<StylePropertyName> properties = style.transitionProperty;
			List<TimeValue> durations = style.transitionDuration;
			if (durations == null || durations.Count == 0)
			{
				return false;
			}

			string name = StylePropertyRegistry.GetName(id);
			int count = properties != null && properties.Count > 0 ? properties.Count : 1;
			int found = -1;
			for (int i = count - 1; i >= 0; i--)
			{
				string property = properties != null && properties.Count > 0 ? properties[i].name : "all";
				if (property == null)
				{
					continue;
				}

				if (property == "none")
				{
					continue;
				}

				if (property == "all" || string.Equals(property, name, StringComparison.Ordinal) || IsShorthandOf(property, id))
				{
					found = i;
					break;
				}
			}

			if (found < 0)
			{
				return false;
			}

			duration = durations[found % durations.Count].ToSeconds();
			if (duration <= 0f)
			{
				return false;
			}

			if (style.transitionDelay != null && style.transitionDelay.Count > 0)
			{
				delay = style.transitionDelay[found % style.transitionDelay.Count].ToSeconds();
			}

			if (style.transitionTimingFunction != null && style.transitionTimingFunction.Count > 0)
			{
				easing = style.transitionTimingFunction[found % style.transitionTimingFunction.Count].mode;
			}

			return true;
		}

		private static bool IsShorthandOf(string property, StylePropertyId longhand)
		{
			StylePropertyId shorthand = StylePropertyRegistry.FromName(property);
			if (shorthand == StylePropertyId.Unknown)
			{
				return false;
			}

			foreach (StylePropertyId id in StylePropertyRegistry.GetLonghands(shorthand))
			{
				if (id == longhand)
				{
					return true;
				}
			}

			return false;
		}

		private static StylePropertyId[] BuildAnimatableList()
		{
			var list = new List<StylePropertyId>();
			for (StylePropertyId id = StylePropertyId.AlignContent; id < StylePropertyId.Margin; id++)
			{
				if (StylePropertyRegistry.GetAnimationKind(id) != StyleAnimationKind.None)
				{
					list.Add(id);
				}
			}

			return list.ToArray();
		}

		// ------------------------------------------------------------------ easing

		public static float Ease(EasingMode mode, float t)
		{
			t = Math.Clamp(t, 0f, 1f);
			switch (mode)
			{
				case EasingMode.Linear: return t;
				case EasingMode.Ease: return CubicBezier(0.25f, 0.1f, 0.25f, 1f, t);
				case EasingMode.EaseIn: return CubicBezier(0.42f, 0f, 1f, 1f, t);
				case EasingMode.EaseOut: return CubicBezier(0f, 0f, 0.58f, 1f, t);
				case EasingMode.EaseInOut: return CubicBezier(0.42f, 0f, 0.58f, 1f, t);
				case EasingMode.EaseInSine: return 1f - (float)Math.Cos(t * Math.PI / 2);
				case EasingMode.EaseOutSine: return (float)Math.Sin(t * Math.PI / 2);
				case EasingMode.EaseInOutSine: return -(float)(Math.Cos(Math.PI * t) - 1) / 2f;
				case EasingMode.EaseInCubic: return t * t * t;
				case EasingMode.EaseOutCubic: return 1f - (float)Math.Pow(1 - t, 3);
				case EasingMode.EaseInOutCubic: return t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2 * t + 2, 3) / 2f;
				case EasingMode.EaseInCirc: return 1f - (float)Math.Sqrt(1 - t * t);
				case EasingMode.EaseOutCirc: return (float)Math.Sqrt(1 - Math.Pow(t - 1, 2));
				case EasingMode.EaseInOutCirc: return t < 0.5f ? (1f - (float)Math.Sqrt(1 - Math.Pow(2 * t, 2))) / 2f : ((float)Math.Sqrt(1 - Math.Pow(-2 * t + 2, 2)) + 1f) / 2f;
				case EasingMode.EaseInElastic: return t == 0f ? 0f : t == 1f ? 1f : -(float)(Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3)));
				case EasingMode.EaseOutElastic: return t == 0f ? 0f : t == 1f ? 1f : (float)(Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * (2 * Math.PI / 3)) + 1);
				case EasingMode.EaseInOutElastic:
					if (t == 0f) return 0f;
					if (t == 1f) return 1f;
					return t < 0.5f
						? -(float)(Math.Pow(2, 20 * t - 10) * Math.Sin((20 * t - 11.125) * (2 * Math.PI / 4.5))) / 2f
						: (float)(Math.Pow(2, -20 * t + 10) * Math.Sin((20 * t - 11.125) * (2 * Math.PI / 4.5))) / 2f + 1f;
				case EasingMode.EaseInBack: return 2.70158f * t * t * t - 1.70158f * t * t;
				case EasingMode.EaseOutBack: return 1f + 2.70158f * (float)Math.Pow(t - 1, 3) + 1.70158f * (float)Math.Pow(t - 1, 2);
				case EasingMode.EaseInOutBack:
					return t < 0.5f
						? (float)(Math.Pow(2 * t, 2) * ((2.5949095 + 1) * 2 * t - 2.5949095)) / 2f
						: (float)(Math.Pow(2 * t - 2, 2) * ((2.5949095 + 1) * (t * 2 - 2) + 2.5949095) + 2) / 2f;
				case EasingMode.EaseInBounce: return 1f - Bounce(1f - t);
				case EasingMode.EaseOutBounce: return Bounce(t);
				case EasingMode.EaseInOutBounce: return t < 0.5f ? (1f - Bounce(1f - 2f * t)) / 2f : (1f + Bounce(2f * t - 1f)) / 2f;
				default: return t;
			}
		}

		private static float Bounce(float t)
		{
			const float n1 = 7.5625f;
			const float d1 = 2.75f;
			if (t < 1f / d1) return n1 * t * t;
			if (t < 2f / d1) return n1 * (t -= 1.5f / d1) * t + 0.75f;
			if (t < 2.5f / d1) return n1 * (t -= 2.25f / d1) * t + 0.9375f;
			return n1 * (t -= 2.625f / d1) * t + 0.984375f;
		}

		/// <summary>CSS cubic-bezier(x1, y1, x2, y2) evaluated at time t (Newton iterations on the x curve).</summary>
		private static float CubicBezier(float x1, float y1, float x2, float y2, float t)
		{
			if (t <= 0f) return 0f;
			if (t >= 1f) return 1f;
			float u = t;
			for (int i = 0; i < 8; i++)
			{
				float x = Bezier(x1, x2, u) - t;
				float dx = BezierDerivative(x1, x2, u);
				if (Math.Abs(x) < 1e-5f || Math.Abs(dx) < 1e-6f)
				{
					break;
				}

				u -= x / dx;
				u = Math.Clamp(u, 0f, 1f);
			}

			return Bezier(y1, y2, u);
		}

		private static float Bezier(float p1, float p2, float u)
		{
			float v = 1f - u;
			return 3f * v * v * u * p1 + 3f * v * u * u * p2 + u * u * u;
		}

		private static float BezierDerivative(float p1, float p2, float u)
		{
			float v = 1f - u;
			return 3f * v * v * p1 + 6f * v * u * (p2 - p1) + 3f * u * u * (1f - p2);
		}

		#endregion
	}

	/// <summary>Reads, writes and interpolates animatable style values by property id.</summary>
	internal static class StyleAnimation
	{
		public static AnimValue Get(ComputedStyle s, StylePropertyId id)
		{
			var v = new AnimValue();
			InheritedStyle i = s.inherited;
			switch (id)
			{
				case StylePropertyId.Bottom: v.length = s.bottom; break;
				case StylePropertyId.FlexBasis: v.length = s.flexBasis; break;
				case StylePropertyId.FlexGrow: v.f = s.flexGrow; break;
				case StylePropertyId.FlexShrink: v.f = s.flexShrink; break;
				case StylePropertyId.Height: v.length = s.height; break;
				case StylePropertyId.Left: v.length = s.left; break;
				case StylePropertyId.MarginBottom: v.length = s.marginBottom; break;
				case StylePropertyId.MarginLeft: v.length = s.marginLeft; break;
				case StylePropertyId.MarginRight: v.length = s.marginRight; break;
				case StylePropertyId.MarginTop: v.length = s.marginTop; break;
				case StylePropertyId.MaxHeight: v.length = s.maxHeight; break;
				case StylePropertyId.MaxWidth: v.length = s.maxWidth; break;
				case StylePropertyId.MinHeight: v.length = s.minHeight; break;
				case StylePropertyId.MinWidth: v.length = s.minWidth; break;
				case StylePropertyId.PaddingBottom: v.length = s.paddingBottom; break;
				case StylePropertyId.PaddingLeft: v.length = s.paddingLeft; break;
				case StylePropertyId.PaddingRight: v.length = s.paddingRight; break;
				case StylePropertyId.PaddingTop: v.length = s.paddingTop; break;
				case StylePropertyId.Right: v.length = s.right; break;
				case StylePropertyId.Top: v.length = s.top; break;
				case StylePropertyId.Width: v.length = s.width; break;
				case StylePropertyId.BorderBottomWidth: v.f = s.borderBottomWidth; break;
				case StylePropertyId.BorderLeftWidth: v.f = s.borderLeftWidth; break;
				case StylePropertyId.BorderRightWidth: v.f = s.borderRightWidth; break;
				case StylePropertyId.BorderTopWidth: v.f = s.borderTopWidth; break;
				case StylePropertyId.BackgroundColor: v.color = s.backgroundColor; break;
				case StylePropertyId.UnityBackgroundImageTintColor: v.color = s.unityBackgroundImageTintColor; break;
				case StylePropertyId.BorderBottomColor: v.color = s.borderBottomColor; break;
				case StylePropertyId.BorderLeftColor: v.color = s.borderLeftColor; break;
				case StylePropertyId.BorderRightColor: v.color = s.borderRightColor; break;
				case StylePropertyId.BorderTopColor: v.color = s.borderTopColor; break;
				case StylePropertyId.BorderBottomLeftRadius: v.length = s.borderBottomLeftRadius; break;
				case StylePropertyId.BorderBottomRightRadius: v.length = s.borderBottomRightRadius; break;
				case StylePropertyId.BorderTopLeftRadius: v.length = s.borderTopLeftRadius; break;
				case StylePropertyId.BorderTopRightRadius: v.length = s.borderTopRightRadius; break;
				case StylePropertyId.Opacity: v.f = s.opacity; break;
				case StylePropertyId.UnitySliceScale: v.f = s.unitySliceScale; break;
				case StylePropertyId.Color: v.color = i.color; break;
				case StylePropertyId.FontSize: v.length = i.fontSize; break;
				case StylePropertyId.LetterSpacing: v.length = i.letterSpacing; break;
				case StylePropertyId.WordSpacing: v.length = i.wordSpacing; break;
				case StylePropertyId.UnityParagraphSpacing: v.length = i.unityParagraphSpacing; break;
				case StylePropertyId.TextShadow: v.shadow = i.textShadow; break;
				case StylePropertyId.UnityTextOutlineColor: v.color = i.unityTextOutlineColor; break;
				case StylePropertyId.UnityTextOutlineWidth: v.f = i.unityTextOutlineWidth; break;
				case StylePropertyId.Translate: v.translate = s.translate; break;
				case StylePropertyId.Rotate: v.rotate = s.rotate; break;
				case StylePropertyId.Scale: v.scale = s.scale; break;
				case StylePropertyId.TransformOrigin: v.origin = s.transformOrigin; break;
			}

			return v;
		}

		public static void Set(ComputedStyle s, StylePropertyId id, AnimValue v)
		{
			InheritedStyle i = s.inherited;
			switch (id)
			{
				case StylePropertyId.Bottom: s.bottom = v.length; break;
				case StylePropertyId.FlexBasis: s.flexBasis = v.length; break;
				case StylePropertyId.FlexGrow: s.flexGrow = v.f; break;
				case StylePropertyId.FlexShrink: s.flexShrink = v.f; break;
				case StylePropertyId.Height: s.height = v.length; break;
				case StylePropertyId.Left: s.left = v.length; break;
				case StylePropertyId.MarginBottom: s.marginBottom = v.length; break;
				case StylePropertyId.MarginLeft: s.marginLeft = v.length; break;
				case StylePropertyId.MarginRight: s.marginRight = v.length; break;
				case StylePropertyId.MarginTop: s.marginTop = v.length; break;
				case StylePropertyId.MaxHeight: s.maxHeight = v.length; break;
				case StylePropertyId.MaxWidth: s.maxWidth = v.length; break;
				case StylePropertyId.MinHeight: s.minHeight = v.length; break;
				case StylePropertyId.MinWidth: s.minWidth = v.length; break;
				case StylePropertyId.PaddingBottom: s.paddingBottom = v.length; break;
				case StylePropertyId.PaddingLeft: s.paddingLeft = v.length; break;
				case StylePropertyId.PaddingRight: s.paddingRight = v.length; break;
				case StylePropertyId.PaddingTop: s.paddingTop = v.length; break;
				case StylePropertyId.Right: s.right = v.length; break;
				case StylePropertyId.Top: s.top = v.length; break;
				case StylePropertyId.Width: s.width = v.length; break;
				case StylePropertyId.BorderBottomWidth: s.borderBottomWidth = v.f; break;
				case StylePropertyId.BorderLeftWidth: s.borderLeftWidth = v.f; break;
				case StylePropertyId.BorderRightWidth: s.borderRightWidth = v.f; break;
				case StylePropertyId.BorderTopWidth: s.borderTopWidth = v.f; break;
				case StylePropertyId.BackgroundColor: s.backgroundColor = v.color; break;
				case StylePropertyId.UnityBackgroundImageTintColor: s.unityBackgroundImageTintColor = v.color; break;
				case StylePropertyId.BorderBottomColor: s.borderBottomColor = v.color; break;
				case StylePropertyId.BorderLeftColor: s.borderLeftColor = v.color; break;
				case StylePropertyId.BorderRightColor: s.borderRightColor = v.color; break;
				case StylePropertyId.BorderTopColor: s.borderTopColor = v.color; break;
				case StylePropertyId.BorderBottomLeftRadius: s.borderBottomLeftRadius = v.length; break;
				case StylePropertyId.BorderBottomRightRadius: s.borderBottomRightRadius = v.length; break;
				case StylePropertyId.BorderTopLeftRadius: s.borderTopLeftRadius = v.length; break;
				case StylePropertyId.BorderTopRightRadius: s.borderTopRightRadius = v.length; break;
				case StylePropertyId.Opacity: s.opacity = v.f; break;
				case StylePropertyId.UnitySliceScale: s.unitySliceScale = v.f; break;
				case StylePropertyId.Color: i.color = v.color; break;
				case StylePropertyId.FontSize: i.fontSize = v.length; break;
				case StylePropertyId.LetterSpacing: i.letterSpacing = v.length; break;
				case StylePropertyId.WordSpacing: i.wordSpacing = v.length; break;
				case StylePropertyId.UnityParagraphSpacing: i.unityParagraphSpacing = v.length; break;
				case StylePropertyId.TextShadow: i.textShadow = v.shadow; break;
				case StylePropertyId.UnityTextOutlineColor: i.unityTextOutlineColor = v.color; break;
				case StylePropertyId.UnityTextOutlineWidth: i.unityTextOutlineWidth = v.f; break;
				case StylePropertyId.Translate: s.translate = v.translate; break;
				case StylePropertyId.Rotate: s.rotate = v.rotate; break;
				case StylePropertyId.Scale: s.scale = v.scale; break;
				case StylePropertyId.TransformOrigin: s.transformOrigin = v.origin; break;
			}
		}

		public static bool Equal(AnimValue a, AnimValue b, StylePropertyId id)
		{
			switch (StylePropertyRegistry.GetAnimationKind(id))
			{
				case StyleAnimationKind.Float: return a.f == b.f;
				case StyleAnimationKind.Length: return a.length == b.length;
				case StyleAnimationKind.Color: return a.color == b.color;
				case StyleAnimationKind.Translate: return a.translate.Equals(b.translate);
				case StyleAnimationKind.Rotate: return a.rotate.Equals(b.rotate);
				case StyleAnimationKind.Scale: return a.scale.Equals(b.scale);
				case StyleAnimationKind.TransformOrigin: return a.origin.Equals(b.origin);
				case StyleAnimationKind.TextShadow: return a.shadow.Equals(b.shadow);
				default: return true;
			}
		}

		public static AnimValue Lerp(AnimValue a, AnimValue b, float t, StylePropertyId id)
		{
			var r = new AnimValue();
			switch (StylePropertyRegistry.GetAnimationKind(id))
			{
				case StyleAnimationKind.Float: r.f = a.f + (b.f - a.f) * t; break;
				case StyleAnimationKind.Length: r.length = LerpLength(a.length, b.length, t); break;
				case StyleAnimationKind.Color: r.color = LerpColor(a.color, b.color, t); break;
				case StyleAnimationKind.Translate: r.translate = new Translate(LerpLength(a.translate.x, b.translate.x, t), LerpLength(a.translate.y, b.translate.y, t)); break;
				case StyleAnimationKind.Rotate: r.rotate = new Rotate(Angle.Degrees(a.rotate.angle.ToDegrees() + (b.rotate.angle.ToDegrees() - a.rotate.angle.ToDegrees()) * t)); break;
				case StyleAnimationKind.Scale: r.scale = new Scale(Vector2.Lerp(a.scale.value, b.scale.value, t)); break;
				case StyleAnimationKind.TransformOrigin: r.origin = new TransformOrigin(LerpLength(a.origin.x, b.origin.x, t), LerpLength(a.origin.y, b.origin.y, t)); break;
				case StyleAnimationKind.TextShadow:
					r.shadow = new TextShadow
					{
						offset = Vector2.Lerp(a.shadow.offset, b.shadow.offset, t),
						blurRadius = a.shadow.blurRadius + (b.shadow.blurRadius - a.shadow.blurRadius) * t,
						color = LerpColor(a.shadow.color, b.shadow.color, t)
					};
					break;
			}

			return r;
		}

		private static Length LerpLength(Length a, Length b, float t)
		{
			if (a.IsAuto || a.IsNone || b.IsAuto || b.IsNone || a.unit != b.unit)
			{
				// Keywords and mixed units cannot interpolate: switch halfway.
				return t < 0.5f ? a : b;
			}

			return new Length(a.value + (b.value - a.value) * t, a.unit);
		}

		private static Color4 LerpColor(Color4 a, Color4 b, float t)
		{
			return new Color4(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
		}
	}
}
