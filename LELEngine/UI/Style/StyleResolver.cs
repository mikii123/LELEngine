using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Runs the USS cascade for dirty elements: collects the rules whose selectors match (through the indexes of
	///     every sheet in scope), sorts them by specificity and order, applies them over the inherited values, then the
	///     inline style, and hands the result to the transition runner. Elements that match the same rules under the
	///     same parent share one <see cref="ComputedStyle" /> instance (the cache), so a list of a thousand identical
	///     rows resolves its styles once.
	/// </summary>
	internal sealed class StyleResolver
	{
		#region PrivateFields

		private readonly BaseVisualElementPanel panel;
		private readonly Dictionary<long, CacheEntry> cache = new Dictionary<long, CacheEntry>();
		private readonly List<MatchedRule> matches = new List<MatchedRule>(32);
		private readonly List<StyleSheet> sheetChain = new List<StyleSheet>(16);
		private readonly List<StyleSheet> expandedSheets = new List<StyleSheet>(16);
		private readonly Comparison<MatchedRule> matchOrder = (a, b) =>
		{
			int bySpecificity = a.specificity.CompareTo(b.specificity);
			if (bySpecificity != 0) return bySpecificity;
			int bySheet = a.sheetOrder.CompareTo(b.sheetOrder);
			return bySheet != 0 ? bySheet : a.rule.order.CompareTo(b.rule.order);
		};

		private const int MaxCacheEntries = 8192;

		#endregion

		#region Constructors

		public StyleResolver(BaseVisualElementPanel panel)
		{
			this.panel = panel;
		}

		#endregion

		#region PublicMethods

		public void ClearCache()
		{
			cache.Clear();
		}

		/// <summary>Resolves every dirty element (shallowest first) with its whole subtree.</summary>
		public void UpdateDirty(List<VisualElement> dirty)
		{
			if (dirty.Count > 1)
			{
				dirty.Sort((a, b) => a.depth.CompareTo(b.depth));
			}

			ComputeUsedPseudoStates();
			for (int i = 0; i < dirty.Count; i++)
			{
				VisualElement element = dirty[i];
				if (element.elementPanel != panel || (element.dirtyFlags & VersionChangeType.Styles) == 0)
				{
					continue;
				}

				BuildSheetChain(element.m_PhysicalParent);
				ResolveSubtree(element, element.m_PhysicalParent?.computedStyle);
			}
		}

		#endregion

		#region PrivateMethods

		private void BuildSheetChain(VisualElement ancestor)
		{
			sheetChain.Clear();
			for (VisualElement e = ancestor; e != null; e = e.m_PhysicalParent)
			{
				if (e.styleSheetList != null)
				{
					for (int i = e.styleSheetList.Count - 1; i >= 0; i--)
					{
						sheetChain.Add(e.styleSheetList[i]);
					}
				}
			}

			sheetChain.Reverse();
		}

		private void ResolveSubtree(VisualElement element, ComputedStyle parentStyle)
		{
			int pushed = 0;
			if (element.styleSheetList != null)
			{
				for (int i = 0; i < element.styleSheetList.Count; i++)
				{
					sheetChain.Add(element.styleSheetList[i]);
					pushed++;
				}
			}

			ResolveElement(element, parentStyle);
			element.dirtyFlags &= ~VersionChangeType.Styles;

			List<VisualElement> children = element.m_Children;
			if (children != null)
			{
				ComputedStyle style = element.transitionState?.target ?? element.computedStyle;
				for (int i = 0; i < children.Count; i++)
				{
					ResolveSubtree(children[i], style);
				}
			}

			if (pushed > 0)
			{
				sheetChain.RemoveRange(sheetChain.Count - pushed, pushed);
			}
		}

		private void ResolveElement(VisualElement element, ComputedStyle parentStyle)
		{
			ComputedStyle displayedOld = element.computedStyle;
			ComputedStyle oldTarget = element.transitionState?.target ?? displayedOld;
			InheritedStyle parentInherited = parentStyle?.inherited ?? InheritedStyle.Default;

			CollectMatches(element);
			long hash = HashMatches(parentInherited);

			ComputedStyle cascaded;
			CacheEntry entry;
			if (cache.TryGetValue(hash, out entry) && entry.Matches(matches, parentInherited))
			{
				cascaded = entry.style;
			}
			else
			{
				cascaded = Build(parentStyle, parentInherited);
				if (cache.Count >= MaxCacheEntries)
				{
					cache.Clear();
				}

				cache[hash] = new CacheEntry(matches, parentInherited, cascaded);
			}

			element.cascadedStyle = cascaded;
			element.matchedRulesHash = hash;

			ComputedStyle target = cascaded;
			InlineStyle inline = element.inlineStyle;
			if (inline != null && inline.count > 0)
			{
				target = cascaded.Clone();
				if (InlineTouchesInherited(inline))
				{
					target.inherited = cascaded.inherited.Clone();
				}

				inline.ApplyTo(target, parentStyle);
			}

			ComputedStyle displayed = panel.transitions.OnStyleChanged(element, displayedOld, oldTarget, target);
			element.computedStyle = displayed;

			StyleEffect effect = StyleDiff.Compute(displayedOld, displayed);
			ApplyEffect(element, effect);

			if (!ReferenceEquals(displayed.inherited.customProperties, displayedOld.inherited.customProperties) && displayed.inherited.customProperties != null)
			{
				using (CustomStyleResolvedEvent evt = CustomStyleResolvedEvent.GetPooled())
				{
					evt.target = element;
					evt.customStyle = element.customStyle;
					element.SendEvent(evt);
				}
			}
		}

		internal static void ApplyEffect(VisualElement element, StyleEffect effect)
		{
			if (effect == StyleEffect.None)
			{
				return;
			}

			VersionChangeType change = VersionChangeType.None;
			if ((effect & StyleEffect.Text) != 0) change |= VersionChangeType.Text;
			if ((effect & StyleEffect.Layout) != 0) change |= VersionChangeType.Layout;
			if ((effect & StyleEffect.Repaint) != 0) change |= VersionChangeType.Repaint;
			if ((effect & StyleEffect.Transform) != 0) change |= VersionChangeType.Transform;
			element.IncrementVersion(change);
		}

		// ------------------------------------------------------------------ matching

		private void CollectMatches(VisualElement element)
		{
			matches.Clear();
			expandedSheets.Clear();
			for (int i = 0; i < sheetChain.Count; i++)
			{
				AddSheetWithImports(sheetChain[i], 0);
			}

			for (int s = 0; s < expandedSheets.Count; s++)
			{
				SelectorIndex index = expandedSheets[s].GetIndex();
				TestCandidates(index.universal, element, s);
				List<SelectorEntry> list;
				if (element.typeName != null && index.byType.TryGetValue(element.typeName, out list))
				{
					TestCandidates(list, element, s);
				}

				if (element.name != null && index.byName.TryGetValue(element.name, out list))
				{
					TestCandidates(list, element, s);
				}

				List<string> classes = element.classListInternal;
				if (classes != null)
				{
					for (int c = 0; c < classes.Count; c++)
					{
						if (index.byClass.TryGetValue(classes[c], out list))
						{
							TestCandidates(list, element, s);
						}
					}
				}
			}

			if (matches.Count > 1)
			{
				matches.Sort(matchOrder);
			}
		}

		private void AddSheetWithImports(StyleSheet sheet, int depth)
		{
			if (sheet == null || depth > 8 || expandedSheets.Contains(sheet))
			{
				return;
			}

			for (int i = 0; i < sheet.imports.Count; i++)
			{
				AddSheetWithImports(sheet.imports[i], depth + 1);
			}

			expandedSheets.Add(sheet);
		}

		private void TestCandidates(List<SelectorEntry> candidates, VisualElement element, int sheetOrder)
		{
			for (int i = 0; i < candidates.Count; i++)
			{
				SelectorEntry candidate = candidates[i];
				if (MatchParts(candidate.selector, candidate.selector.parts.Length - 1, element))
				{
					matches.Add(new MatchedRule { rule = candidate.rule, specificity = candidate.selector.specificity, sheetOrder = sheetOrder });
				}
			}
		}

		private static bool MatchParts(StyleSelector selector, int partIndex, VisualElement element)
		{
			if (!MatchSimple(selector.parts[partIndex], element))
			{
				return false;
			}

			if (partIndex == 0)
			{
				return true;
			}

			SelectorCombinator combinator = selector.combinators[partIndex - 1];
			if (combinator == SelectorCombinator.Child)
			{
				VisualElement parent = element.m_PhysicalParent;
				return parent != null && MatchParts(selector, partIndex - 1, parent);
			}

			for (VisualElement ancestor = element.m_PhysicalParent; ancestor != null; ancestor = ancestor.m_PhysicalParent)
			{
				if (MatchParts(selector, partIndex - 1, ancestor))
				{
					return true;
				}
			}

			return false;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool MatchSimple(SimpleSelector part, VisualElement element)
		{
			if (part.neverMatches)
			{
				return false;
			}

			if (part.type != null && !string.Equals(part.type, element.typeName, StringComparison.Ordinal))
			{
				return false;
			}

			if (part.name != null && !string.Equals(part.name, element.name, StringComparison.Ordinal))
			{
				return false;
			}

			PseudoStates states = element.pseudoStates;
			if ((states & part.pseudoMask) != part.pseudoMask || (states & part.negatedPseudoMask) != 0)
			{
				return false;
			}

			string[] classes = part.classes;
			if (classes.Length > 0)
			{
				List<string> elementClasses = element.classListInternal;
				if (elementClasses == null)
				{
					return false;
				}

				for (int i = 0; i < classes.Length; i++)
				{
					if (!elementClasses.Contains(classes[i]))
					{
						return false;
					}
				}
			}

			return true;
		}

		private long HashMatches(InheritedStyle parentInherited)
		{
			unchecked
			{
				long hash = 1469598103934665603L;
				hash = (hash ^ RuntimeHelpers.GetHashCode(parentInherited)) * 1099511628211L;
				hash = (hash ^ panel.styleSheetVersion) * 1099511628211L;
				for (int i = 0; i < matches.Count; i++)
				{
					hash = (hash ^ RuntimeHelpers.GetHashCode(matches[i].rule)) * 1099511628211L;
					hash = (hash ^ matches[i].rule.sheet.version) * 1099511628211L;
				}

				return hash;
			}
		}

		// ------------------------------------------------------------------ building

		private ComputedStyle Build(ComputedStyle parentStyle, InheritedStyle parentInherited)
		{
			ComputedStyle style = ComputedStyle.Default.Clone();
			style.inherited = parentInherited;

			bool needsOwnInherited = false;
			bool hasCustom = false;
			for (int i = 0; i < matches.Count && !(needsOwnInherited && hasCustom); i++)
			{
				StyleDeclaration[] declarations = matches[i].rule.declarations;
				for (int d = 0; d < declarations.Length; d++)
				{
					StylePropertyId id = declarations[d].id;
					if (id == StylePropertyId.Custom)
					{
						hasCustom = true;
						needsOwnInherited = true;
					}
					else if (StylePropertyRegistry.IsInherited(id) || id == StylePropertyId.All)
					{
						needsOwnInherited = true;
					}
				}
			}

			if (needsOwnInherited)
			{
				style.inherited = parentInherited.Clone();
			}

			// Custom properties first: var() references in the other declarations read them.
			if (hasCustom)
			{
				for (int i = 0; i < matches.Count; i++)
				{
					StyleDeclaration[] declarations = matches[i].rule.declarations;
					for (int d = 0; d < declarations.Length; d++)
					{
						if (declarations[d].id == StylePropertyId.Custom)
						{
							StyleApplier.Apply(style, parentStyle, StylePropertyId.Custom, declarations[d].name, declarations[d].values, declarations[d].usesVariables);
						}
					}
				}
			}

			for (int i = 0; i < matches.Count; i++)
			{
				StyleDeclaration[] declarations = matches[i].rule.declarations;
				for (int d = 0; d < declarations.Length; d++)
				{
					ref StyleDeclaration declaration = ref declarations[d];
					if (declaration.id == StylePropertyId.Custom)
					{
						continue;
					}

					StyleApplier.Apply(style, parentStyle, declaration.id, declaration.name, declaration.values, declaration.usesVariables);
				}
			}

			return style;
		}

		private static bool InlineTouchesInherited(InlineStyle inline)
		{
			for (StylePropertyId id = StylePropertyId.Color; id <= StylePropertyId.Visibility; id++)
			{
				if (inline.Has(id))
				{
					return true;
				}
			}

			return inline.Has(StylePropertyId.UnityTextOutline) || inline.Has(StylePropertyId.All) || inline.Has(StylePropertyId.Custom);
		}

		/// <summary>Which pseudo states any selector in the panel uses (hover changes on elements no selector cares about skip restyling).</summary>
		private void ComputeUsedPseudoStates()
		{
			PseudoStates used = PseudoStates.None;
			var visited = new HashSet<StyleSheet>();
			var stack = new Stack<VisualElement>();
			stack.Push(panel.visualTree);
			while (stack.Count > 0)
			{
				VisualElement e = stack.Pop();
				if (e.styleSheetList != null)
				{
					foreach (StyleSheet sheet in e.styleSheetList)
					{
						CollectPseudo(sheet, visited, ref used);
					}
				}

				if (e.m_Children != null)
				{
					foreach (VisualElement child in e.m_Children)
					{
						// Sheets are almost always attached near the root; stop descending past depth 3 for speed.
						if (child.depth <= 3)
						{
							stack.Push(child);
						}
					}
				}
			}

			panel.usedPseudoStates = used | PseudoStates.Disabled | PseudoStates.Focus;
		}

		private static void CollectPseudo(StyleSheet sheet, HashSet<StyleSheet> visited, ref PseudoStates used)
		{
			if (sheet == null || !visited.Add(sheet))
			{
				return;
			}

			foreach (StyleSheet import in sheet.imports)
			{
				CollectPseudo(import, visited, ref used);
			}

			foreach (StyleRule rule in sheet.rules)
			{
				foreach (StyleSelector selector in rule.selectors)
				{
					foreach (SimpleSelector part in selector.parts)
					{
						used |= part.pseudoMask | part.negatedPseudoMask;
					}
				}
			}
		}

		#endregion

		#region NestedTypes

		private struct MatchedRule
		{
			public StyleRule rule;
			public int specificity;
			public int sheetOrder;
		}

		private sealed class CacheEntry
		{
			public readonly StyleRule[] rules;
			public readonly InheritedStyle parentInherited;
			public readonly ComputedStyle style;

			public CacheEntry(List<MatchedRule> matches, InheritedStyle parentInherited, ComputedStyle style)
			{
				rules = new StyleRule[matches.Count];
				for (int i = 0; i < matches.Count; i++)
				{
					rules[i] = matches[i].rule;
				}

				this.parentInherited = parentInherited;
				this.style = style;
			}

			public bool Matches(List<MatchedRule> matches, InheritedStyle parentInherited)
			{
				if (!ReferenceEquals(this.parentInherited, parentInherited) || rules.Length != matches.Count)
				{
					return false;
				}

				for (int i = 0; i < rules.Length; i++)
				{
					if (!ReferenceEquals(rules[i], matches[i].rule))
					{
						return false;
					}
				}

				return true;
			}
		}

		#endregion
	}

	/// <summary>Compares two computed styles and reports which passes the difference invalidates.</summary>
	internal static class StyleDiff
	{
		public static StyleEffect Compute(ComputedStyle a, ComputedStyle b)
		{
			if (ReferenceEquals(a, b))
			{
				return StyleEffect.None;
			}

			StyleEffect effect = StyleEffect.None;
			if (a.alignContent != b.alignContent || a.alignItems != b.alignItems || a.alignSelf != b.alignSelf || a.bottom != b.bottom || a.display != b.display
				|| a.flexBasis != b.flexBasis || a.flexDirection != b.flexDirection || a.flexGrow != b.flexGrow || a.flexShrink != b.flexShrink || a.flexWrap != b.flexWrap
				|| a.height != b.height || a.justifyContent != b.justifyContent || a.left != b.left || a.marginBottom != b.marginBottom || a.marginLeft != b.marginLeft
				|| a.marginRight != b.marginRight || a.marginTop != b.marginTop || a.maxHeight != b.maxHeight || a.maxWidth != b.maxWidth || a.minHeight != b.minHeight
				|| a.minWidth != b.minWidth || a.paddingBottom != b.paddingBottom || a.paddingLeft != b.paddingLeft || a.paddingRight != b.paddingRight
				|| a.paddingTop != b.paddingTop || a.position != b.position || a.right != b.right || a.top != b.top || a.width != b.width
				|| a.borderBottomWidth != b.borderBottomWidth || a.borderLeftWidth != b.borderLeftWidth || a.borderRightWidth != b.borderRightWidth || a.borderTopWidth != b.borderTopWidth)
			{
				effect |= StyleEffect.Layout | StyleEffect.Repaint;
			}

			if (a.backgroundColor != b.backgroundColor || !a.backgroundImage.Equals(b.backgroundImage) || !a.backgroundPositionX.Equals(b.backgroundPositionX)
				|| !a.backgroundPositionY.Equals(b.backgroundPositionY) || !a.backgroundRepeat.Equals(b.backgroundRepeat) || !a.backgroundSize.Equals(b.backgroundSize)
				|| a.unityBackgroundImageTintColor != b.unityBackgroundImageTintColor || a.unityBackgroundScaleMode != b.unityBackgroundScaleMode
				|| a.borderBottomColor != b.borderBottomColor || a.borderLeftColor != b.borderLeftColor || a.borderRightColor != b.borderRightColor || a.borderTopColor != b.borderTopColor
				|| a.borderBottomLeftRadius != b.borderBottomLeftRadius || a.borderBottomRightRadius != b.borderBottomRightRadius || a.borderTopLeftRadius != b.borderTopLeftRadius
				|| a.borderTopRightRadius != b.borderTopRightRadius || a.unitySliceBottom != b.unitySliceBottom || a.unitySliceLeft != b.unitySliceLeft
				|| a.unitySliceRight != b.unitySliceRight || a.unitySliceTop != b.unitySliceTop || a.unitySliceScale != b.unitySliceScale || a.unitySliceType != b.unitySliceType
				|| a.textOverflow != b.textOverflow)
			{
				effect |= StyleEffect.Repaint;
			}

			if (a.opacity != b.opacity || a.overflow != b.overflow || a.unityOverflowClipBox != b.unityOverflowClipBox || !a.translate.Equals(b.translate)
				|| !a.rotate.Equals(b.rotate) || !a.scale.Equals(b.scale) || !a.transformOrigin.Equals(b.transformOrigin))
			{
				effect |= StyleEffect.Transform;
			}

			InheritedStyle ia = a.inherited;
			InheritedStyle ib = b.inherited;
			if (!ReferenceEquals(ia, ib))
			{
				if (ia.fontSize != ib.fontSize || !ReferenceEquals(ia.unityFont, ib.unityFont) || !ia.unityFontDefinition.Equals(ib.unityFontDefinition)
					|| ia.unityFontStyleAndWeight != ib.unityFontStyleAndWeight || ia.letterSpacing != ib.letterSpacing || ia.wordSpacing != ib.wordSpacing
					|| ia.whiteSpace != ib.whiteSpace || ia.unityParagraphSpacing != ib.unityParagraphSpacing || ia.unityTextAlign != ib.unityTextAlign
					|| ia.unityTextOverflowPosition != ib.unityTextOverflowPosition)
				{
					effect |= StyleEffect.Text | StyleEffect.Layout | StyleEffect.Repaint;
				}

				if (ia.color != ib.color || !ia.textShadow.Equals(ib.textShadow) || ia.unityTextOutlineColor != ib.unityTextOutlineColor || ia.unityTextOutlineWidth != ib.unityTextOutlineWidth)
				{
					effect |= StyleEffect.Repaint;
				}

				if (ia.visibility != ib.visibility)
				{
					effect |= StyleEffect.Transform;
				}
			}

			return effect;
		}
	}
}
