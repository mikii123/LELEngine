using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Flexbox layout engine (the algorithm Yoga implements, which Unity's UI Toolkit uses): flex direction and
	///     wrapping, flex basis / grow / shrink with min and max constraints, justify-content, align-items / self /
	///     content, auto margins, relative offsets, absolute positioning, percent units against the parent, and
	///     measured leaves (text, images). Incremental: clean subtrees whose constraints did not change are not
	///     visited, and measurements are cached per node. Results are rounded to the pixel grid.
	/// </summary>
	internal static class FlexLayout
	{
		#region PrivateFields

		private const float Undefined = float.NaN;

		// Per-node scratch lists come from pools: the layout recurses into children while a node's lists are in use.
		private static readonly Stack<List<VisualElement>> linePool = new Stack<List<VisualElement>>();
		private static readonly Stack<List<float>> floatPool = new Stack<List<float>>();

		private static List<VisualElement> RentLine()
		{
			return linePool.Count > 0 ? linePool.Pop() : new List<VisualElement>(32);
		}

		private static List<float> RentFloats()
		{
			return floatPool.Count > 0 ? floatPool.Pop() : new List<float>(8);
		}

		private static void Return(List<VisualElement> list)
		{
			list.Clear();
			linePool.Push(list);
		}

		private static void Return(List<float> list)
		{
			list.Clear();
			floatPool.Push(list);
		}

		private enum Axis
		{
			Row,
			Column
		}

		#endregion

		#region PublicMethods

		/// <summary>Lays out the whole tree for a panel of the given size and rounds the results to pixels.</summary>
		public static void Layout(VisualElement root, float width, float height)
		{
			LayoutNodeInternal(root, width, height, VisualElement.MeasureMode.Exactly, VisualElement.MeasureMode.Exactly, width, height, true);
			root.layoutNode.rawX = 0f;
			root.layoutNode.rawY = 0f;
			RoundToPixelGrid(root, 0f, 0f);
		}

		#endregion

		#region PrivateMethods

		// ------------------------------------------------------------------ style access

		private static bool IsDefined(float value)
		{
			return !float.IsNaN(value);
		}

		private static float Resolve(Length length, float reference)
		{
			if (length.IsAuto || length.IsNone)
			{
				return Undefined;
			}

			if (length.unit == LengthUnit.Percent)
			{
				return IsDefined(reference) ? reference * length.value * 0.01f : Undefined;
			}

			return length.value;
		}

		private static bool IsRow(FlexDirection direction)
		{
			return direction == FlexDirection.Row || direction == FlexDirection.RowReverse;
		}

		private static bool IsReverse(FlexDirection direction)
		{
			return direction == FlexDirection.RowReverse || direction == FlexDirection.ColumnReverse;
		}

		private static Length StyleDim(ComputedStyle s, Axis axis)
		{
			return axis == Axis.Row ? s.width : s.height;
		}

		private static Length StyleMin(ComputedStyle s, Axis axis)
		{
			return axis == Axis.Row ? s.minWidth : s.minHeight;
		}

		private static Length StyleMax(ComputedStyle s, Axis axis)
		{
			return axis == Axis.Row ? s.maxWidth : s.maxHeight;
		}

		private static Length LeadingPosition(ComputedStyle s, Axis axis)
		{
			return axis == Axis.Row ? s.left : s.top;
		}

		private static Length TrailingPosition(ComputedStyle s, Axis axis)
		{
			return axis == Axis.Row ? s.right : s.bottom;
		}

		private static Length LeadingMargin(ComputedStyle s, Axis axis)
		{
			return axis == Axis.Row ? s.marginLeft : s.marginTop;
		}

		private static Length TrailingMargin(ComputedStyle s, Axis axis)
		{
			return axis == Axis.Row ? s.marginRight : s.marginBottom;
		}

		/// <summary>Margins resolve against the owner's width (CSS), auto counts as 0 here.</summary>
		private static float LeadingMarginValue(ComputedStyle s, Axis axis, float ownerWidth)
		{
			float v = Resolve(LeadingMargin(s, axis), ownerWidth);
			return IsDefined(v) ? v : 0f;
		}

		private static float TrailingMarginValue(ComputedStyle s, Axis axis, float ownerWidth)
		{
			float v = Resolve(TrailingMargin(s, axis), ownerWidth);
			return IsDefined(v) ? v : 0f;
		}

		private static float MarginForAxis(ComputedStyle s, Axis axis, float ownerWidth)
		{
			return LeadingMarginValue(s, axis, ownerWidth) + TrailingMarginValue(s, axis, ownerWidth);
		}

		private static float LeadingPadding(ComputedStyle s, Axis axis, float ownerWidth)
		{
			float v = Resolve(axis == Axis.Row ? s.paddingLeft : s.paddingTop, ownerWidth);
			return IsDefined(v) ? Math.Max(0f, v) : 0f;
		}

		private static float TrailingPadding(ComputedStyle s, Axis axis, float ownerWidth)
		{
			float v = Resolve(axis == Axis.Row ? s.paddingRight : s.paddingBottom, ownerWidth);
			return IsDefined(v) ? Math.Max(0f, v) : 0f;
		}

		private static float LeadingBorder(ComputedStyle s, Axis axis)
		{
			return Math.Max(0f, axis == Axis.Row ? s.borderLeftWidth : s.borderTopWidth);
		}

		private static float TrailingBorder(ComputedStyle s, Axis axis)
		{
			return Math.Max(0f, axis == Axis.Row ? s.borderRightWidth : s.borderBottomWidth);
		}

		private static float LeadingPaddingAndBorder(ComputedStyle s, Axis axis, float ownerWidth)
		{
			return LeadingPadding(s, axis, ownerWidth) + LeadingBorder(s, axis);
		}

		private static float TrailingPaddingAndBorder(ComputedStyle s, Axis axis, float ownerWidth)
		{
			return TrailingPadding(s, axis, ownerWidth) + TrailingBorder(s, axis);
		}

		private static float PaddingAndBorderForAxis(ComputedStyle s, Axis axis, float ownerWidth)
		{
			return LeadingPaddingAndBorder(s, axis, ownerWidth) + TrailingPaddingAndBorder(s, axis, ownerWidth);
		}

		private static bool IsDimDefined(ComputedStyle s, Axis axis, float ownerSize)
		{
			Length dim = StyleDim(s, axis);
			if (dim.IsAuto || dim.IsNone)
			{
				return false;
			}

			if (dim.unit == LengthUnit.Percent)
			{
				return IsDefined(ownerSize) && dim.value >= 0f;
			}

			return dim.value >= 0f;
		}

		private static bool IsLeadingPositionDefined(ComputedStyle s, Axis axis)
		{
			Length p = LeadingPosition(s, axis);
			return !p.IsAuto && !p.IsNone;
		}

		private static bool IsTrailingPositionDefined(ComputedStyle s, Axis axis)
		{
			Length p = TrailingPosition(s, axis);
			return !p.IsAuto && !p.IsNone;
		}

		private static float LeadingPositionValue(ComputedStyle s, Axis axis, float axisSize)
		{
			float v = Resolve(LeadingPosition(s, axis), axisSize);
			return IsDefined(v) ? v : 0f;
		}

		private static float TrailingPositionValue(ComputedStyle s, Axis axis, float axisSize)
		{
			float v = Resolve(TrailingPosition(s, axis), axisSize);
			return IsDefined(v) ? v : 0f;
		}

		/// <summary>Offset of a relatively positioned node from its layout position (left / top, else -right / -bottom).</summary>
		private static float RelativePosition(ComputedStyle s, Axis axis, float axisSize)
		{
			if (IsLeadingPositionDefined(s, axis))
			{
				float v = Resolve(LeadingPosition(s, axis), axisSize);
				if (IsDefined(v)) return v;
			}

			float trailing = Resolve(TrailingPosition(s, axis), axisSize);
			return IsDefined(trailing) ? -trailing : 0f;
		}

		/// <summary>Clamps a border-box size to min / max (resolved against the owner size) and to the padding + border.</summary>
		private static float BoundAxisWithinMinAndMax(ComputedStyle s, Axis axis, float value, float axisSize)
		{
			float min = Resolve(StyleMin(s, axis), axisSize);
			float max = Resolve(StyleMax(s, axis), axisSize);
			float bound = value;
			if (IsDefined(max) && max >= 0f && bound > max)
			{
				bound = max;
			}

			if (IsDefined(min) && min >= 0f && bound < min)
			{
				bound = min;
			}

			return bound;
		}

		private static float BoundAxis(ComputedStyle s, Axis axis, float value, float axisSize, float widthSize)
		{
			return Math.Max(BoundAxisWithinMinAndMax(s, axis, value, axisSize), PaddingAndBorderForAxis(s, axis, widthSize));
		}

		private static Align AlignItem(ComputedStyle node, ComputedStyle child)
		{
			Align align = child.alignSelf == Align.Auto ? node.alignItems : child.alignSelf;
			if (align == Align.Auto)
			{
				align = Align.Stretch;
			}

			return align;
		}

		private static float GetMeasured(VisualElement e, Axis axis)
		{
			return axis == Axis.Row ? e.layoutNode.rawWidth : e.layoutNode.rawHeight;
		}

		private static void SetMeasured(VisualElement e, Axis axis, float value)
		{
			if (axis == Axis.Row) e.layoutNode.rawWidth = value;
			else e.layoutNode.rawHeight = value;
		}

		private static float GetPosition(VisualElement e, Axis axis)
		{
			return axis == Axis.Row ? e.layoutNode.rawX : e.layoutNode.rawY;
		}

		private static void SetPosition(VisualElement e, Axis axis, float value)
		{
			if (axis == Axis.Row) e.layoutNode.rawX = value;
			else e.layoutNode.rawY = value;
		}

		private static float DimWithMargin(VisualElement e, Axis axis, float ownerWidth)
		{
			return GetMeasured(e, axis) + MarginForAxis(e.computedStyle, axis, ownerWidth);
		}

		private static bool IsMarginAuto(ComputedStyle s, Axis axis, bool leading)
		{
			return (leading ? LeadingMargin(s, axis) : TrailingMargin(s, axis)).IsAuto;
		}

		private static void ConstrainMaxSizeForMode(ComputedStyle s, Axis axis, float ownerAxisSize, float ownerWidth, ref VisualElement.MeasureMode mode, ref float size)
		{
			float maxSize = Resolve(StyleMax(s, axis), ownerAxisSize);
			if (!IsDefined(maxSize))
			{
				return;
			}

			maxSize += MarginForAxis(s, axis, ownerWidth);
			switch (mode)
			{
				case VisualElement.MeasureMode.Exactly:
				case VisualElement.MeasureMode.AtMost:
					size = IsDefined(size) ? Math.Min(size, maxSize) : maxSize;
					if (mode == VisualElement.MeasureMode.Exactly && IsDefined(size))
					{
						// keep exactly
					}
					break;
				case VisualElement.MeasureMode.Undefined:
					mode = VisualElement.MeasureMode.AtMost;
					size = maxSize;
					break;
			}
		}

		// ------------------------------------------------------------------ core

		/// <summary>
		///     Computes the node's size (and, with <paramref name="performLayout" />, its children's positions). The
		///     available sizes include the node's margins; the measured size excludes them.
		/// </summary>
		private static void LayoutNodeInternal(VisualElement node, float availableWidth, float availableHeight, VisualElement.MeasureMode widthMode, VisualElement.MeasureMode heightMode, float ownerWidth, float ownerHeight, bool performLayout)
		{
			LayoutNodeData data = node.layoutNode;
			ComputedStyle style = node.computedStyle;

			if (widthMode == VisualElement.MeasureMode.Undefined) availableWidth = Undefined;
			if (heightMode == VisualElement.MeasureMode.Undefined) availableHeight = Undefined;

			// Cached results: a full layout with identical constraints, or a measurement with identical constraints.
			if (!data.dirty)
			{
				bool sameAsLastLayout = data.lastLayoutValid && Same(data.lastAvailableWidth, availableWidth) && Same(data.lastAvailableHeight, availableHeight)
					&& data.lastWidthMode == widthMode && data.lastHeightMode == heightMode && Same(data.lastOwnerWidth, ownerWidth) && Same(data.lastOwnerHeight, ownerHeight);
				if (sameAsLastLayout)
				{
					// The children keep their positions; only the measured size must be restored (a measurement in
					// between may have overwritten it).
					data.rawWidth = data.lastLayoutWidth;
					data.rawHeight = data.lastLayoutHeight;
					return;
				}

				if (!performLayout)
				{
					float cachedWidth, cachedHeight;
					if (data.TryGetMeasure(availableWidth, widthMode, availableHeight, heightMode, ownerWidth, ownerHeight, out cachedWidth, out cachedHeight))
					{
						data.rawWidth = cachedWidth;
						data.rawHeight = cachedHeight;
						return;
					}
				}
			}

			ComputeNode(node, style, availableWidth, availableHeight, widthMode, heightMode, ownerWidth, ownerHeight, performLayout);

			if (performLayout)
			{
				data.lastAvailableWidth = availableWidth;
				data.lastAvailableHeight = availableHeight;
				data.lastWidthMode = widthMode;
				data.lastHeightMode = heightMode;
				data.lastOwnerWidth = ownerWidth;
				data.lastOwnerHeight = ownerHeight;
				data.lastLayoutWidth = data.rawWidth;
				data.lastLayoutHeight = data.rawHeight;
				data.lastLayoutValid = true;
				data.dirty = false;
				node.hasDirtyLayoutDescendant = false;
				node.dirtyFlags &= ~VersionChangeType.Layout;
			}
			else
			{
				data.StoreMeasure(availableWidth, widthMode, availableHeight, heightMode, ownerWidth, ownerHeight, data.rawWidth, data.rawHeight);
			}
		}

		private static bool Same(float a, float b)
		{
			return (float.IsNaN(a) && float.IsNaN(b)) || Math.Abs(a - b) < 0.001f;
		}

		private static void ComputeNode(VisualElement node, ComputedStyle style, float availableWidth, float availableHeight, VisualElement.MeasureMode widthMode, VisualElement.MeasureMode heightMode, float ownerWidth, float ownerHeight, bool performLayout)
		{
			LayoutNodeData data = node.layoutNode;

			float marginAxisRow = MarginForAxis(style, Axis.Row, ownerWidth);
			float marginAxisColumn = MarginForAxis(style, Axis.Column, ownerWidth);
			float paddingAndBorderAxisRow = PaddingAndBorderForAxis(style, Axis.Row, ownerWidth);
			float paddingAndBorderAxisColumn = PaddingAndBorderForAxis(style, Axis.Column, ownerWidth);

			// Store the resolved box edges for resolvedStyle / rendering.
			data.marginLeft = LeadingMarginValue(style, Axis.Row, ownerWidth);
			data.marginRight = TrailingMarginValue(style, Axis.Row, ownerWidth);
			data.marginTop = LeadingMarginValue(style, Axis.Column, ownerWidth);
			data.marginBottom = TrailingMarginValue(style, Axis.Column, ownerWidth);
			data.paddingLeft = LeadingPadding(style, Axis.Row, ownerWidth);
			data.paddingRight = TrailingPadding(style, Axis.Row, ownerWidth);
			data.paddingTop = LeadingPadding(style, Axis.Column, ownerWidth);
			data.paddingBottom = TrailingPadding(style, Axis.Column, ownerWidth);
			data.borderLeft = LeadingBorder(style, Axis.Row);
			data.borderRight = TrailingBorder(style, Axis.Row);
			data.borderTop = LeadingBorder(style, Axis.Column);
			data.borderBottom = TrailingBorder(style, Axis.Column);

			if (node.requiresMeasure)
			{
				MeasureLeaf(node, style, availableWidth, availableHeight, widthMode, heightMode, ownerWidth, ownerHeight, marginAxisRow, marginAxisColumn, paddingAndBorderAxisRow, paddingAndBorderAxisColumn);
				return;
			}

			List<VisualElement> children = node.m_Children;
			int childCount = 0;
			if (children != null)
			{
				for (int i = 0; i < children.Count; i++)
				{
					if (!children[i].resolvedStyleDisplayNone)
					{
						childCount++;
					}
					else
					{
						ZeroLayout(children[i]);
					}
				}
			}

			if (childCount == 0)
			{
				data.rawWidth = BoundAxis(style, Axis.Row, widthMode == VisualElement.MeasureMode.Undefined || widthMode == VisualElement.MeasureMode.AtMost ? paddingAndBorderAxisRow : availableWidth - marginAxisRow, ownerWidth, ownerWidth);
				data.rawHeight = BoundAxis(style, Axis.Column, heightMode == VisualElement.MeasureMode.Undefined || heightMode == VisualElement.MeasureMode.AtMost ? paddingAndBorderAxisColumn : availableHeight - marginAxisColumn, ownerHeight, ownerWidth);
				return;
			}

			// Fixed size shortcut for measurements.
			if (!performLayout && FixedSizeSetMeasuredDimensions(node, style, availableWidth, availableHeight, widthMode, heightMode, ownerWidth, ownerHeight, marginAxisRow, marginAxisColumn))
			{
				return;
			}

			List<VisualElement> lineBuffer = RentLine();
			List<float> lineCrossSizes = RentFloats();
			List<float> lineCrossOffsets = RentFloats();
			try
			{
				ComputeFlexNode(node, style, data, children, availableWidth, availableHeight, widthMode, heightMode, ownerWidth, ownerHeight, performLayout,
					marginAxisRow, marginAxisColumn, paddingAndBorderAxisRow, paddingAndBorderAxisColumn, lineBuffer, lineCrossSizes, lineCrossOffsets);
			}
			finally
			{
				Return(lineBuffer);
				Return(lineCrossSizes);
				Return(lineCrossOffsets);
			}
		}

		private static void ComputeFlexNode(VisualElement node, ComputedStyle style, LayoutNodeData data, List<VisualElement> children, float availableWidth, float availableHeight, VisualElement.MeasureMode widthMode, VisualElement.MeasureMode heightMode, float ownerWidth, float ownerHeight, bool performLayout,
			float marginAxisRow, float marginAxisColumn, float paddingAndBorderAxisRow, float paddingAndBorderAxisColumn, List<VisualElement> lineBuffer, List<float> lineCrossSizes, List<float> lineCrossOffsets)
		{
			// ---- Step 1: axes
			FlexDirection direction = style.flexDirection;
			bool isMainAxisRow = IsRow(direction);
			Axis mainAxis = isMainAxisRow ? Axis.Row : Axis.Column;
			Axis crossAxis = isMainAxisRow ? Axis.Column : Axis.Row;
			bool isNodeFlexWrap = style.flexWrap != Wrap.NoWrap;
			float mainAxisOwnerSize = isMainAxisRow ? ownerWidth : ownerHeight;
			float crossAxisOwnerSize = isMainAxisRow ? ownerHeight : ownerWidth;

			float leadingPaddingAndBorderMain = LeadingPaddingAndBorder(style, mainAxis, ownerWidth);
			float trailingPaddingAndBorderMain = TrailingPaddingAndBorder(style, mainAxis, ownerWidth);
			float leadingPaddingAndBorderCross = LeadingPaddingAndBorder(style, crossAxis, ownerWidth);
			float paddingAndBorderAxisMain = PaddingAndBorderForAxis(style, mainAxis, ownerWidth);
			float paddingAndBorderAxisCross = PaddingAndBorderForAxis(style, crossAxis, ownerWidth);

			VisualElement.MeasureMode measureModeMainDim = isMainAxisRow ? widthMode : heightMode;
			VisualElement.MeasureMode measureModeCrossDim = isMainAxisRow ? heightMode : widthMode;

			// ---- Step 2: available inner size
			float minInnerWidth = Resolve(style.minWidth, ownerWidth) - paddingAndBorderAxisRow;
			float maxInnerWidth = Resolve(style.maxWidth, ownerWidth) - paddingAndBorderAxisRow;
			float minInnerHeight = Resolve(style.minHeight, ownerHeight) - paddingAndBorderAxisColumn;
			float maxInnerHeight = Resolve(style.maxHeight, ownerHeight) - paddingAndBorderAxisColumn;

			float availableInnerWidth = CalculateAvailableInnerDim(availableWidth, marginAxisRow, paddingAndBorderAxisRow, minInnerWidth, maxInnerWidth);
			float availableInnerHeight = CalculateAvailableInnerDim(availableHeight, marginAxisColumn, paddingAndBorderAxisColumn, minInnerHeight, maxInnerHeight);
			float availableInnerMainDim = isMainAxisRow ? availableInnerWidth : availableInnerHeight;
			float availableInnerCrossDim = isMainAxisRow ? availableInnerHeight : availableInnerWidth;

			// ---- Step 3: flex basis of every child
			float totalOuterFlexBasis = 0f;
			for (int i = 0; i < children.Count; i++)
			{
				VisualElement child = children[i];
				if (child.resolvedStyleDisplayNone)
				{
					continue;
				}

				ComputedStyle childStyle = child.computedStyle;
				if (childStyle.position == Position.Absolute)
				{
					continue;
				}

				ComputeFlexBasisForChild(node, style, child, childStyle, availableInnerWidth, widthMode, availableInnerHeight, availableInnerWidth, availableInnerHeight, heightMode, isMainAxisRow, mainAxis, ownerWidth);
				totalOuterFlexBasis += child.layoutNode.computedFlexBasis + MarginForAxis(childStyle, mainAxis, availableInnerWidth);
			}

			bool flexBasisOverflows = measureModeMainDim == VisualElement.MeasureMode.Undefined ? false : totalOuterFlexBasis > availableInnerMainDim;
			if (isNodeFlexWrap && flexBasisOverflows && measureModeMainDim == VisualElement.MeasureMode.AtMost)
			{
				measureModeMainDim = VisualElement.MeasureMode.Exactly;
			}

			// ---- Step 4..8: lines
			int startOfLineIndex = 0;
			int endOfLineIndex = 0;
			int lineCount = 0;
			float totalLineCrossDim = 0f;
			float maxLineMainDim = 0f;
			lineCrossSizes.Clear();

			for (; endOfLineIndex < children.Count; lineCount++, startOfLineIndex = endOfLineIndex)
			{
				// ---- Step 4: collect one line
				lineBuffer.Clear();
				float sizeConsumedOnCurrentLine = 0f;
				float sizeConsumedOnCurrentLineIncludingMinConstraint = 0f;
				float totalFlexGrowFactors = 0f;
				float totalFlexShrinkScaledFactors = 0f;
				int itemsOnLine = 0;
				endOfLineIndex = startOfLineIndex;
				for (; endOfLineIndex < children.Count; endOfLineIndex++)
				{
					VisualElement child = children[endOfLineIndex];
					if (child.resolvedStyleDisplayNone)
					{
						continue;
					}

					ComputedStyle childStyle = child.computedStyle;
					if (childStyle.position == Position.Absolute)
					{
						continue;
					}

					child.layoutNode.lineIndex = lineCount;
					float childMarginMainAxis = MarginForAxis(childStyle, mainAxis, availableInnerWidth);
					float flexBasisWithMinAndMaxConstraints = BoundAxisWithinMinAndMax(childStyle, mainAxis, child.layoutNode.computedFlexBasis, mainAxisOwnerSize);

					if (sizeConsumedOnCurrentLineIncludingMinConstraint + flexBasisWithMinAndMaxConstraints + childMarginMainAxis > availableInnerMainDim && isNodeFlexWrap && itemsOnLine > 0)
					{
						break;
					}

					sizeConsumedOnCurrentLineIncludingMinConstraint += flexBasisWithMinAndMaxConstraints + childMarginMainAxis;
					sizeConsumedOnCurrentLine += flexBasisWithMinAndMaxConstraints + childMarginMainAxis;
					itemsOnLine++;

					if (IsFlexible(childStyle, node))
					{
						totalFlexGrowFactors += childStyle.flexGrow;
						totalFlexShrinkScaledFactors += -childStyle.flexShrink * child.layoutNode.computedFlexBasis;
					}

					lineBuffer.Add(child);
				}

				if (totalFlexGrowFactors > 0f && totalFlexGrowFactors < 1f) totalFlexGrowFactors = 1f;
				if (totalFlexShrinkScaledFactors > 0f && totalFlexShrinkScaledFactors < 1f) totalFlexShrinkScaledFactors = 1f;

				bool canSkipFlex = !performLayout && measureModeCrossDim == VisualElement.MeasureMode.Exactly;

				// ---- Step 5: resolve flexible lengths
				float leadingMainDim = 0f;
				float betweenMainDim = 0f;
				bool sizeBasedOnContent = false;
				float remainingFreeSpace = 0f;
				if (IsDefined(availableInnerMainDim))
				{
					remainingFreeSpace = availableInnerMainDim - sizeConsumedOnCurrentLine;
				}
				else if (sizeConsumedOnCurrentLine < 0f)
				{
					remainingFreeSpace = -sizeConsumedOnCurrentLine;
				}

				if (measureModeMainDim == VisualElement.MeasureMode.AtMost && remainingFreeSpace > 0f)
				{
					float minMain = Resolve(StyleMin(style, mainAxis), mainAxisOwnerSize);
					if (IsDefined(minMain) && minMain >= 0f)
					{
						remainingFreeSpace = Math.Max(0f, minMain - (availableInnerMainDim - remainingFreeSpace));
					}
					else
					{
						remainingFreeSpace = 0f;
						sizeBasedOnContent = true;
					}
				}

				if (!canSkipFlex)
				{
					remainingFreeSpace = ResolveFlexibleLength(node, style, lineBuffer, mainAxis, crossAxis, mainAxisOwnerSize, availableInnerMainDim, availableInnerCrossDim, availableInnerWidth, availableInnerHeight, flexBasisOverflows, measureModeCrossDim, performLayout, remainingFreeSpace, totalFlexGrowFactors, totalFlexShrinkScaledFactors, isNodeFlexWrap);
				}

				// ---- Step 6: main axis justification
				int numberOfAutoMarginsOnCurrentLine = 0;
				for (int i = 0; i < lineBuffer.Count; i++)
				{
					ComputedStyle cs = lineBuffer[i].computedStyle;
					if (IsMarginAuto(cs, mainAxis, true)) numberOfAutoMarginsOnCurrentLine++;
					if (IsMarginAuto(cs, mainAxis, false)) numberOfAutoMarginsOnCurrentLine++;
				}

				if (numberOfAutoMarginsOnCurrentLine == 0 && !sizeBasedOnContent)
				{
					switch (style.justifyContent)
					{
						case Justify.Center:
							leadingMainDim = remainingFreeSpace / 2f;
							break;
						case Justify.FlexEnd:
							leadingMainDim = remainingFreeSpace;
							break;
						case Justify.SpaceBetween:
							if (itemsOnLine > 1)
							{
								betweenMainDim = Math.Max(remainingFreeSpace, 0f) / (itemsOnLine - 1);
							}
							break;
						case Justify.SpaceEvenly:
							betweenMainDim = remainingFreeSpace / (itemsOnLine + 1);
							leadingMainDim = betweenMainDim;
							break;
						case Justify.SpaceAround:
							betweenMainDim = remainingFreeSpace / itemsOnLine;
							leadingMainDim = betweenMainDim / 2f;
							break;
					}
				}

				float mainDim = leadingPaddingAndBorderMain + leadingMainDim;
				float crossDim = 0f;
				for (int i = 0; i < lineBuffer.Count; i++)
				{
					VisualElement child = lineBuffer[i];
					ComputedStyle cs = child.computedStyle;
					if (IsMarginAuto(cs, mainAxis, true) && numberOfAutoMarginsOnCurrentLine > 0)
					{
						mainDim += remainingFreeSpace / numberOfAutoMarginsOnCurrentLine;
					}

					if (performLayout)
					{
						SetPosition(child, mainAxis, mainDim + LeadingMarginValue(cs, mainAxis, availableInnerWidth));
					}

					if (IsMarginAuto(cs, mainAxis, false) && numberOfAutoMarginsOnCurrentLine > 0)
					{
						mainDim += remainingFreeSpace / numberOfAutoMarginsOnCurrentLine;
					}

					if (canSkipFlex)
					{
						mainDim += betweenMainDim + MarginForAxis(cs, mainAxis, availableInnerWidth) + child.layoutNode.computedFlexBasis;
						crossDim = availableInnerCrossDim;
					}
					else
					{
						mainDim += betweenMainDim + DimWithMargin(child, mainAxis, availableInnerWidth);
						crossDim = Math.Max(crossDim, DimWithMargin(child, crossAxis, availableInnerWidth));
					}
				}

				mainDim += trailingPaddingAndBorderMain;

				float containerCrossAxis = availableInnerCrossDim;
				if (measureModeCrossDim == VisualElement.MeasureMode.Undefined || measureModeCrossDim == VisualElement.MeasureMode.AtMost)
				{
					containerCrossAxis = BoundAxis(style, crossAxis, crossDim + paddingAndBorderAxisCross, crossAxisOwnerSize, ownerWidth) - paddingAndBorderAxisCross;
				}

				if (!isNodeFlexWrap && measureModeCrossDim == VisualElement.MeasureMode.Exactly)
				{
					crossDim = availableInnerCrossDim;
				}

				crossDim = BoundAxis(style, crossAxis, crossDim + paddingAndBorderAxisCross, crossAxisOwnerSize, ownerWidth) - paddingAndBorderAxisCross;

				// ---- Step 7: cross axis alignment
				if (performLayout)
				{
					for (int i = 0; i < lineBuffer.Count; i++)
					{
						VisualElement child = lineBuffer[i];
						ComputedStyle cs = child.computedStyle;
						float leadingCrossDim = leadingPaddingAndBorderCross;
						Align alignItem = AlignItem(style, cs);
						bool autoLeading = IsMarginAuto(cs, crossAxis, true);
						bool autoTrailing = IsMarginAuto(cs, crossAxis, false);
						if (alignItem == Align.Stretch && !autoLeading && !autoTrailing)
						{
							if (!IsDimDefined(cs, crossAxis, availableInnerCrossDim))
							{
								float childMainSize = GetMeasured(child, mainAxis);
								float childCrossSize = crossDim - MarginForAxis(cs, crossAxis, availableInnerWidth);
								childCrossSize = BoundAxisWithinMinAndMax(cs, crossAxis, childCrossSize, crossAxisOwnerSize);
								float childWidth = isMainAxisRow ? childMainSize : childCrossSize;
								float childHeight = isMainAxisRow ? childCrossSize : childMainSize;
								childWidth += MarginForAxis(cs, Axis.Row, availableInnerWidth);
								childHeight += MarginForAxis(cs, Axis.Column, availableInnerWidth);
								LayoutNodeInternal(child, childWidth, childHeight, VisualElement.MeasureMode.Exactly, VisualElement.MeasureMode.Exactly, availableInnerWidth, availableInnerHeight, true);
							}
						}
						else
						{
							float remainingCrossDim = containerCrossAxis - DimWithMargin(child, crossAxis, availableInnerWidth);
							if (autoLeading && autoTrailing)
							{
								leadingCrossDim += Math.Max(0f, remainingCrossDim / 2f);
							}
							else if (autoTrailing)
							{
								// leading stays
							}
							else if (autoLeading)
							{
								leadingCrossDim += Math.Max(0f, remainingCrossDim);
							}
							else if (alignItem == Align.FlexStart)
							{
								// nothing
							}
							else if (alignItem == Align.Center)
							{
								leadingCrossDim += remainingCrossDim / 2f;
							}
							else
							{
								leadingCrossDim += remainingCrossDim;
							}
						}

						SetPosition(child, crossAxis, totalLineCrossDim + leadingCrossDim + LeadingMarginValue(cs, crossAxis, availableInnerWidth));
					}
				}

				lineCrossSizes.Add(crossDim);
				totalLineCrossDim += crossDim;
				maxLineMainDim = Math.Max(maxLineMainDim, mainDim);
			}

			// ---- Step 8: align-content for multi-line containers
			if (performLayout && (lineCount > 1 || style.flexWrap == Wrap.WrapReverse))
			{
				float remainingAlignContentDim = 0f;
				if (IsDefined(availableInnerCrossDim))
				{
					remainingAlignContentDim = availableInnerCrossDim - totalLineCrossDim;
				}

				float crossDimLead = 0f;
				float currentLead = leadingPaddingAndBorderCross;
				float extraPerLine = 0f;
				switch (style.alignContent)
				{
					case Align.FlexEnd:
						currentLead += remainingAlignContentDim;
						break;
					case Align.Center:
						currentLead += remainingAlignContentDim / 2f;
						break;
					case Align.Stretch:
						if (availableInnerCrossDim > totalLineCrossDim)
						{
							extraPerLine = remainingAlignContentDim / lineCount;
						}
						break;
				}

				lineCrossOffsets.Clear();
				for (int line = 0; line < lineCount; line++)
				{
					lineCrossOffsets.Add(currentLead);
					float lineHeight = lineCrossSizes[line] + extraPerLine;
					lineCrossSizes[line] = lineHeight;
					currentLead += lineHeight;
				}

				bool reverseWrap = style.flexWrap == Wrap.WrapReverse;
				for (int i = 0; i < children.Count; i++)
				{
					VisualElement child = children[i];
					ComputedStyle cs = child.computedStyle;
					if (child.resolvedStyleDisplayNone || cs.position == Position.Absolute)
					{
						continue;
					}

					int line = child.layoutNode.lineIndex;
					float lineOffset = lineCrossOffsets[line];
					float lineHeight = lineCrossSizes[line];
					// The child position computed in step 7 is relative to the line start (totalLineCrossDim so far) plus the leading padding.
					float positionInLine = GetPosition(child, crossAxis) - LineStart(lineCrossSizes, line) - leadingPaddingAndBorderCross;
					if (reverseWrap)
					{
						lineOffset = leadingPaddingAndBorderCross + (currentLead - leadingPaddingAndBorderCross) - (lineOffset - leadingPaddingAndBorderCross) - lineHeight;
					}

					Align alignItem = AlignItem(style, cs);
					if (alignItem == Align.Stretch && !IsMarginAuto(cs, crossAxis, true) && !IsMarginAuto(cs, crossAxis, false) && !IsDimDefined(cs, crossAxis, availableInnerCrossDim) && extraPerLine != 0f)
					{
						float childMainSize = GetMeasured(child, mainAxis);
						float childCrossSize = BoundAxisWithinMinAndMax(cs, crossAxis, lineHeight - MarginForAxis(cs, crossAxis, availableInnerWidth), crossAxisOwnerSize);
						float childWidth = (isMainAxisRow ? childMainSize : childCrossSize) + MarginForAxis(cs, Axis.Row, availableInnerWidth);
						float childHeight = (isMainAxisRow ? childCrossSize : childMainSize) + MarginForAxis(cs, Axis.Column, availableInnerWidth);
						LayoutNodeInternal(child, childWidth, childHeight, VisualElement.MeasureMode.Exactly, VisualElement.MeasureMode.Exactly, availableInnerWidth, availableInnerHeight, true);
					}

					SetPosition(child, crossAxis, lineOffset + positionInLine);
				}

				if (style.alignContent == Align.Stretch && extraPerLine != 0f)
				{
					totalLineCrossDim = availableInnerCrossDim;
				}
			}

			// ---- Step 9: final dimensions
			data.rawWidth = BoundAxis(style, Axis.Row, availableWidth - marginAxisRow, ownerWidth, ownerWidth);
			data.rawHeight = BoundAxis(style, Axis.Column, availableHeight - marginAxisColumn, ownerHeight, ownerWidth);

			if (measureModeMainDim == VisualElement.MeasureMode.Undefined || measureModeMainDim == VisualElement.MeasureMode.AtMost)
			{
				float contentMain = BoundAxis(style, mainAxis, maxLineMainDim, mainAxisOwnerSize, ownerWidth);
				if (measureModeMainDim == VisualElement.MeasureMode.Undefined)
				{
					SetMeasured(node, mainAxis, contentMain);
				}
				else
				{
					float availableMain = isMainAxisRow ? availableWidth - marginAxisRow : availableHeight - marginAxisColumn;
					SetMeasured(node, mainAxis, Math.Max(Math.Min(availableMain, BoundAxisWithinMinAndMax(style, mainAxis, maxLineMainDim, mainAxisOwnerSize)), paddingAndBorderAxisMain));
				}
			}

			if (measureModeCrossDim == VisualElement.MeasureMode.Undefined || measureModeCrossDim == VisualElement.MeasureMode.AtMost)
			{
				float contentCross = BoundAxis(style, crossAxis, totalLineCrossDim + paddingAndBorderAxisCross, crossAxisOwnerSize, ownerWidth);
				if (measureModeCrossDim == VisualElement.MeasureMode.Undefined)
				{
					SetMeasured(node, crossAxis, contentCross);
				}
				else
				{
					float availableCross = isMainAxisRow ? availableHeight - marginAxisColumn : availableWidth - marginAxisRow;
					SetMeasured(node, crossAxis, Math.Max(Math.Min(availableCross, BoundAxisWithinMinAndMax(style, crossAxis, totalLineCrossDim + paddingAndBorderAxisCross, crossAxisOwnerSize)), paddingAndBorderAxisCross));
				}
			}

			if (!performLayout)
			{
				return;
			}

			// ---- Step 10: reverse directions and relative offsets
			float measuredMain = GetMeasured(node, mainAxis);
			bool reverse = IsReverse(direction);
			for (int i = 0; i < children.Count; i++)
			{
				VisualElement child = children[i];
				ComputedStyle cs = child.computedStyle;
				if (child.resolvedStyleDisplayNone || cs.position == Position.Absolute)
				{
					continue;
				}

				if (reverse)
				{
					SetPosition(child, mainAxis, measuredMain - GetPosition(child, mainAxis) - GetMeasured(child, mainAxis));
				}

				// Relative offsets (left / top / right / bottom) shift the laid-out position.
				child.layoutNode.rawX += RelativePosition(cs, Axis.Row, availableInnerWidth);
				child.layoutNode.rawY += RelativePosition(cs, Axis.Column, availableInnerHeight);
			}

			// ---- Step 11: absolutely positioned children
			for (int i = 0; i < children.Count; i++)
			{
				VisualElement child = children[i];
				if (child.resolvedStyleDisplayNone || child.computedStyle.position != Position.Absolute)
				{
					continue;
				}

				AbsoluteLayoutChild(node, style, child, child.computedStyle, availableInnerWidth, availableInnerHeight, isMainAxisRow, mainAxis, crossAxis);
			}
		}

		private static float LineStart(List<float> lineCrossSizes, int line)
		{
			float start = 0f;
			for (int i = 0; i < line; i++)
			{
				start += lineCrossSizes[i];
			}

			return start;
		}

		private static bool IsFlexible(ComputedStyle child, VisualElement node)
		{
			return child.position != Position.Absolute && (child.flexGrow != 0f || child.flexShrink != 0f);
		}

		private static float CalculateAvailableInnerDim(float available, float margin, float paddingAndBorder, float minInner, float maxInner)
		{
			float inner = available - margin - paddingAndBorder;
			if (IsDefined(inner))
			{
				float min = IsDefined(minInner) ? Math.Max(0f, minInner) : 0f;
				float max = IsDefined(maxInner) ? Math.Max(0f, maxInner) : float.MaxValue;
				inner = Math.Max(Math.Min(inner, max), min);
			}

			return inner;
		}

		private static bool FixedSizeSetMeasuredDimensions(VisualElement node, ComputedStyle style, float availableWidth, float availableHeight, VisualElement.MeasureMode widthMode, VisualElement.MeasureMode heightMode, float ownerWidth, float ownerHeight, float marginAxisRow, float marginAxisColumn)
		{
			bool widthFixed = widthMode == VisualElement.MeasureMode.Exactly || (widthMode == VisualElement.MeasureMode.AtMost && availableWidth <= 0f);
			bool heightFixed = heightMode == VisualElement.MeasureMode.Exactly || (heightMode == VisualElement.MeasureMode.AtMost && availableHeight <= 0f);
			if (!widthFixed || !heightFixed)
			{
				return false;
			}

			node.layoutNode.rawWidth = BoundAxis(style, Axis.Row, (widthMode == VisualElement.MeasureMode.AtMost && availableWidth < 0f) ? 0f : availableWidth - marginAxisRow, ownerWidth, ownerWidth);
			node.layoutNode.rawHeight = BoundAxis(style, Axis.Column, (heightMode == VisualElement.MeasureMode.AtMost && availableHeight < 0f) ? 0f : availableHeight - marginAxisColumn, ownerHeight, ownerWidth);
			return true;
		}

		private static void ZeroLayout(VisualElement element)
		{
			LayoutNodeData data = element.layoutNode;
			data.rawX = 0f;
			data.rawY = 0f;
			data.rawWidth = 0f;
			data.rawHeight = 0f;
			data.lastLayoutWidth = 0f;
			data.lastLayoutHeight = 0f;
			data.dirty = false;
			data.lastLayoutValid = false;
			element.hasDirtyLayoutDescendant = false;
			element.dirtyFlags &= ~VersionChangeType.Layout;
		}

		// ------------------------------------------------------------------ leaves

		private static void MeasureLeaf(VisualElement node, ComputedStyle style, float availableWidth, float availableHeight, VisualElement.MeasureMode widthMode, VisualElement.MeasureMode heightMode, float ownerWidth, float ownerHeight, float marginAxisRow, float marginAxisColumn, float paddingAndBorderAxisRow, float paddingAndBorderAxisColumn)
		{
			float innerWidth = IsDefined(availableWidth) ? Math.Max(0f, availableWidth - marginAxisRow - paddingAndBorderAxisRow) : Undefined;
			float innerHeight = IsDefined(availableHeight) ? Math.Max(0f, availableHeight - marginAxisColumn - paddingAndBorderAxisColumn) : Undefined;

			if (widthMode == VisualElement.MeasureMode.Exactly && heightMode == VisualElement.MeasureMode.Exactly)
			{
				node.layoutNode.rawWidth = BoundAxis(style, Axis.Row, availableWidth - marginAxisRow, ownerWidth, ownerWidth);
				node.layoutNode.rawHeight = BoundAxis(style, Axis.Column, availableHeight - marginAxisColumn, ownerHeight, ownerWidth);
				return;
			}

			// Min / max constraints narrow the measurement so text wraps against the final width.
			float maxWidth = Resolve(style.maxWidth, ownerWidth);
			if (IsDefined(maxWidth) && widthMode != VisualElement.MeasureMode.Exactly)
			{
				float maxInner = Math.Max(0f, maxWidth - paddingAndBorderAxisRow);
				innerWidth = IsDefined(innerWidth) ? Math.Min(innerWidth, maxInner) : maxInner;
				if (widthMode == VisualElement.MeasureMode.Undefined) widthMode = VisualElement.MeasureMode.AtMost;
			}

			Vector2 measured;
			try
			{
				measured = node.Measure(innerWidth, widthMode, innerHeight, heightMode);
				// Whole pixels: a box rounded down below its text would wrap the text again.
				measured.X = (float)Math.Ceiling(measured.X - 0.001f);
				measured.Y = (float)Math.Ceiling(measured.Y - 0.001f);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				measured = Vector2.Zero;
			}

			float width = widthMode == VisualElement.MeasureMode.Exactly
				? availableWidth - marginAxisRow
				: measured.X + paddingAndBorderAxisRow;
			if (widthMode == VisualElement.MeasureMode.AtMost && IsDefined(availableWidth))
			{
				width = Math.Min(width, availableWidth - marginAxisRow);
			}

			float height = heightMode == VisualElement.MeasureMode.Exactly
				? availableHeight - marginAxisColumn
				: measured.Y + paddingAndBorderAxisColumn;
			if (heightMode == VisualElement.MeasureMode.AtMost && IsDefined(availableHeight))
			{
				height = Math.Min(height, availableHeight - marginAxisColumn);
			}

			node.layoutNode.rawWidth = BoundAxis(style, Axis.Row, width, ownerWidth, ownerWidth);
			node.layoutNode.rawHeight = BoundAxis(style, Axis.Column, height, ownerHeight, ownerWidth);
		}

		// ------------------------------------------------------------------ flex basis

		private static void ComputeFlexBasisForChild(VisualElement node, ComputedStyle nodeStyle, VisualElement child, ComputedStyle cs, float width, VisualElement.MeasureMode widthMode, float height, float ownerWidth, float ownerHeight, VisualElement.MeasureMode heightMode, bool isMainAxisRow, Axis mainAxis, float nodeOwnerWidth)
		{
			float mainAxisSize = isMainAxisRow ? width : height;
			float mainAxisOwnerSize = isMainAxisRow ? ownerWidth : ownerHeight;
			LayoutNodeData childData = child.layoutNode;

			float resolvedFlexBasis = Resolve(cs.flexBasis, mainAxisOwnerSize);
			bool isRowStyleDimDefined = IsDimDefined(cs, Axis.Row, ownerWidth);
			bool isColumnStyleDimDefined = IsDimDefined(cs, Axis.Column, ownerHeight);

			if (IsDefined(resolvedFlexBasis) && IsDefined(mainAxisSize))
			{
				childData.computedFlexBasis = Math.Max(resolvedFlexBasis, PaddingAndBorderForAxis(cs, mainAxis, ownerWidth));
				return;
			}

			if (IsDefined(resolvedFlexBasis) && cs.flexBasis.unit == LengthUnit.Pixel)
			{
				childData.computedFlexBasis = Math.Max(resolvedFlexBasis, PaddingAndBorderForAxis(cs, mainAxis, ownerWidth));
				return;
			}

			if (isMainAxisRow && isRowStyleDimDefined)
			{
				childData.computedFlexBasis = Math.Max(Resolve(cs.width, ownerWidth), PaddingAndBorderForAxis(cs, Axis.Row, ownerWidth));
				return;
			}

			if (!isMainAxisRow && isColumnStyleDimDefined)
			{
				childData.computedFlexBasis = Math.Max(Resolve(cs.height, ownerHeight), PaddingAndBorderForAxis(cs, Axis.Column, ownerWidth));
				return;
			}

			// Measure the child to find its natural main size.
			float childWidth = Undefined, childHeight = Undefined;
			VisualElement.MeasureMode childWidthMode = VisualElement.MeasureMode.Undefined, childHeightMode = VisualElement.MeasureMode.Undefined;
			float marginRow = MarginForAxis(cs, Axis.Row, ownerWidth);
			float marginColumn = MarginForAxis(cs, Axis.Column, ownerWidth);

			if (isRowStyleDimDefined)
			{
				childWidth = Resolve(cs.width, ownerWidth) + marginRow;
				childWidthMode = VisualElement.MeasureMode.Exactly;
			}

			if (isColumnStyleDimDefined)
			{
				childHeight = Resolve(cs.height, ownerHeight) + marginColumn;
				childHeightMode = VisualElement.MeasureMode.Exactly;
			}

			// Browsers constrain the cross axis of a child to the container even without stretch (text wraps).
			if (!isMainAxisRow && !IsDefined(childWidth) && widthMode != VisualElement.MeasureMode.Undefined && IsDefined(width))
			{
				childWidth = width;
				childWidthMode = VisualElement.MeasureMode.AtMost;
			}

			if (isMainAxisRow && !IsDefined(childHeight) && heightMode != VisualElement.MeasureMode.Undefined && IsDefined(height))
			{
				childHeight = height;
				childHeightMode = VisualElement.MeasureMode.AtMost;
			}

			// Stretch: the cross size is the container's when the child has none of its own.
			bool noAutoCrossMargins = isMainAxisRow
				? !cs.marginTop.IsAuto && !cs.marginBottom.IsAuto
				: !cs.marginLeft.IsAuto && !cs.marginRight.IsAuto;
			if (!isMainAxisRow && !isRowStyleDimDefined && widthMode == VisualElement.MeasureMode.Exactly && IsDefined(width) && AlignItem(nodeStyle, cs) == Align.Stretch && noAutoCrossMargins && nodeStyle.flexWrap == Wrap.NoWrap)
			{
				childWidth = width;
				childWidthMode = VisualElement.MeasureMode.Exactly;
			}

			if (isMainAxisRow && !isColumnStyleDimDefined && heightMode == VisualElement.MeasureMode.Exactly && IsDefined(height) && AlignItem(nodeStyle, cs) == Align.Stretch && noAutoCrossMargins && nodeStyle.flexWrap == Wrap.NoWrap)
			{
				childHeight = height;
				childHeightMode = VisualElement.MeasureMode.Exactly;
			}

			ConstrainMaxSizeForMode(cs, Axis.Row, ownerWidth, ownerWidth, ref childWidthMode, ref childWidth);
			ConstrainMaxSizeForMode(cs, Axis.Column, ownerHeight, ownerWidth, ref childHeightMode, ref childHeight);

			LayoutNodeInternal(child, childWidth, childHeight, childWidthMode, childHeightMode, ownerWidth, ownerHeight, false);
			childData.computedFlexBasis = Math.Max(GetMeasured(child, mainAxis), PaddingAndBorderForAxis(cs, mainAxis, ownerWidth));
		}

		// ------------------------------------------------------------------ flexible lengths

		private static float ResolveFlexibleLength(VisualElement node, ComputedStyle style, List<VisualElement> line, Axis mainAxis, Axis crossAxis, float mainAxisOwnerSize, float availableInnerMainDim, float availableInnerCrossDim, float availableInnerWidth, float availableInnerHeight, bool flexBasisOverflows, VisualElement.MeasureMode measureModeCrossDim, bool performLayout, float remainingFreeSpace, float totalFlexGrowFactors, float totalFlexShrinkScaledFactors, bool isNodeFlexWrap)
		{
			float originalFreeSpace = remainingFreeSpace;

			// First pass: find the children whose flexed size violates min / max; they freeze at the bound.
			float deltaFreeSpace = 0f;
			for (int i = 0; i < line.Count; i++)
			{
				VisualElement child = line[i];
				ComputedStyle cs = child.computedStyle;
				float childFlexBasis = BoundAxisWithinMinAndMax(cs, mainAxis, child.layoutNode.computedFlexBasis, mainAxisOwnerSize);
				if (remainingFreeSpace < 0f)
				{
					float flexShrinkScaledFactor = -cs.flexShrink * childFlexBasis;
					if (flexShrinkScaledFactor != 0f)
					{
						float baseMainSize = childFlexBasis + remainingFreeSpace / totalFlexShrinkScaledFactors * flexShrinkScaledFactor;
						float boundMainSize = BoundAxis(cs, mainAxis, baseMainSize, mainAxisOwnerSize, availableInnerWidth);
						if (baseMainSize != boundMainSize)
						{
							deltaFreeSpace += boundMainSize - childFlexBasis;
							totalFlexShrinkScaledFactors -= flexShrinkScaledFactor;
						}
					}
				}
				else if (remainingFreeSpace > 0f)
				{
					float flexGrowFactor = cs.flexGrow;
					if (flexGrowFactor != 0f)
					{
						float baseMainSize = childFlexBasis + remainingFreeSpace / totalFlexGrowFactors * flexGrowFactor;
						float boundMainSize = BoundAxis(cs, mainAxis, baseMainSize, mainAxisOwnerSize, availableInnerWidth);
						if (baseMainSize != boundMainSize)
						{
							deltaFreeSpace += boundMainSize - childFlexBasis;
							totalFlexGrowFactors -= flexGrowFactor;
						}
					}
				}
			}

			remainingFreeSpace -= deltaFreeSpace;

			// Second pass: distribute the remaining space and lay out the children.
			float usedFreeSpace = 0f;
			bool isMainAxisRow = mainAxis == Axis.Row;
			for (int i = 0; i < line.Count; i++)
			{
				VisualElement child = line[i];
				ComputedStyle cs = child.computedStyle;
				float childFlexBasis = BoundAxisWithinMinAndMax(cs, mainAxis, child.layoutNode.computedFlexBasis, mainAxisOwnerSize);
				float updatedMainSize = childFlexBasis;

				if (remainingFreeSpace < 0f)
				{
					float flexShrinkScaledFactor = -cs.flexShrink * childFlexBasis;
					if (flexShrinkScaledFactor != 0f)
					{
						float childSize = totalFlexShrinkScaledFactors == 0f
							? childFlexBasis + flexShrinkScaledFactor
							: childFlexBasis + remainingFreeSpace / totalFlexShrinkScaledFactors * flexShrinkScaledFactor;
						updatedMainSize = BoundAxis(cs, mainAxis, childSize, mainAxisOwnerSize, availableInnerWidth);
					}
				}
				else if (remainingFreeSpace > 0f)
				{
					float flexGrowFactor = cs.flexGrow;
					if (flexGrowFactor != 0f)
					{
						updatedMainSize = BoundAxis(cs, mainAxis, childFlexBasis + remainingFreeSpace / totalFlexGrowFactors * flexGrowFactor, mainAxisOwnerSize, availableInnerWidth);
					}
				}

				usedFreeSpace += updatedMainSize - childFlexBasis;

				float marginMain = MarginForAxis(cs, mainAxis, availableInnerWidth);
				float marginCross = MarginForAxis(cs, crossAxis, availableInnerWidth);

				float childCrossSize;
				float childMainSize = updatedMainSize + marginMain;
				VisualElement.MeasureMode childCrossMeasureMode;
				VisualElement.MeasureMode childMainMeasureMode = VisualElement.MeasureMode.Exactly;

				bool noAutoCrossMargins = !IsMarginAuto(cs, crossAxis, true) && !IsMarginAuto(cs, crossAxis, false);
				if (IsDimDefined(cs, crossAxis, availableInnerCrossDim))
				{
					childCrossSize = Resolve(StyleDim(cs, crossAxis), availableInnerCrossDim) + marginCross;
					childCrossMeasureMode = VisualElement.MeasureMode.Exactly;
				}
				else if (IsDefined(availableInnerCrossDim) && !isNodeFlexWrap && measureModeCrossDim == VisualElement.MeasureMode.Exactly && AlignItem(style, cs) == Align.Stretch && noAutoCrossMargins)
				{
					childCrossSize = availableInnerCrossDim;
					childCrossMeasureMode = VisualElement.MeasureMode.Exactly;
				}
				else
				{
					childCrossSize = availableInnerCrossDim;
					childCrossMeasureMode = IsDefined(childCrossSize) ? VisualElement.MeasureMode.AtMost : VisualElement.MeasureMode.Undefined;
				}

				ConstrainMaxSizeForMode(cs, mainAxis, mainAxisOwnerSize, availableInnerWidth, ref childMainMeasureMode, ref childMainSize);
				ConstrainMaxSizeForMode(cs, crossAxis, isMainAxisRow ? availableInnerHeight : availableInnerWidth, availableInnerWidth, ref childCrossMeasureMode, ref childCrossSize);

				bool requiresStretchLayout = !IsDimDefined(cs, crossAxis, availableInnerCrossDim) && AlignItem(style, cs) == Align.Stretch && noAutoCrossMargins;

				float childWidth = isMainAxisRow ? childMainSize : childCrossSize;
				float childHeight = isMainAxisRow ? childCrossSize : childMainSize;
				VisualElement.MeasureMode childWidthMode = isMainAxisRow ? childMainMeasureMode : childCrossMeasureMode;
				VisualElement.MeasureMode childHeightMode = isMainAxisRow ? childCrossMeasureMode : childMainMeasureMode;

				LayoutNodeInternal(child, childWidth, childHeight, childWidthMode, childHeightMode, availableInnerWidth, availableInnerHeight, performLayout && !requiresStretchLayout);
			}

			return originalFreeSpace - deltaFreeSpace - usedFreeSpace;
		}

		// ------------------------------------------------------------------ absolute children

		private static void AbsoluteLayoutChild(VisualElement node, ComputedStyle style, VisualElement child, ComputedStyle cs, float width, float height, bool isMainAxisRow, Axis mainAxis, Axis crossAxis)
		{
			LayoutNodeData nodeData = node.layoutNode;
			float childWidth = Undefined, childHeight = Undefined;
			VisualElement.MeasureMode childWidthMode = VisualElement.MeasureMode.Undefined, childHeightMode = VisualElement.MeasureMode.Undefined;
			float marginRow = MarginForAxis(cs, Axis.Row, width);
			float marginColumn = MarginForAxis(cs, Axis.Column, width);

			if (IsDimDefined(cs, Axis.Row, width))
			{
				childWidth = Resolve(cs.width, width) + marginRow;
			}
			else if (IsLeadingPositionDefined(cs, Axis.Row) && IsTrailingPositionDefined(cs, Axis.Row))
			{
				childWidth = nodeData.rawWidth - (LeadingBorder(style, Axis.Row) + TrailingBorder(style, Axis.Row)) - (LeadingPositionValue(cs, Axis.Row, width) + TrailingPositionValue(cs, Axis.Row, width));
				childWidth = BoundAxis(cs, Axis.Row, childWidth, width, width);
			}

			if (IsDimDefined(cs, Axis.Column, height))
			{
				childHeight = Resolve(cs.height, height) + marginColumn;
			}
			else if (IsLeadingPositionDefined(cs, Axis.Column) && IsTrailingPositionDefined(cs, Axis.Column))
			{
				childHeight = nodeData.rawHeight - (LeadingBorder(style, Axis.Column) + TrailingBorder(style, Axis.Column)) - (LeadingPositionValue(cs, Axis.Column, height) + TrailingPositionValue(cs, Axis.Column, height));
				childHeight = BoundAxis(cs, Axis.Column, childHeight, height, width);
			}

			if (!IsDefined(childWidth) || !IsDefined(childHeight))
			{
				childWidthMode = IsDefined(childWidth) ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined;
				childHeightMode = IsDefined(childHeight) ? VisualElement.MeasureMode.Exactly : VisualElement.MeasureMode.Undefined;
				// Constrain the absolute child to its owner so text wraps.
				if (!IsDefined(childWidth) && IsDefined(width))
				{
					childWidth = width;
					childWidthMode = VisualElement.MeasureMode.AtMost;
				}

				LayoutNodeInternal(child, childWidth, childHeight, childWidthMode, childHeightMode, width, height, false);
				childWidth = child.layoutNode.rawWidth + marginRow;
				childHeight = child.layoutNode.rawHeight + marginColumn;
			}

			LayoutNodeInternal(child, childWidth, childHeight, VisualElement.MeasureMode.Exactly, VisualElement.MeasureMode.Exactly, width, height, true);

			PositionAbsoluteAxis(node, style, child, cs, Axis.Row, width, isMainAxisRow ? style.justifyContent : (Justify?)null, isMainAxisRow ? (Align?)null : AlignItem(style, cs));
			PositionAbsoluteAxis(node, style, child, cs, Axis.Column, width, isMainAxisRow ? (Justify?)null : style.justifyContent, isMainAxisRow ? AlignItem(style, cs) : (Align?)null);
			child.layoutNode.dirty = false;
		}

		private static void PositionAbsoluteAxis(VisualElement node, ComputedStyle style, VisualElement child, ComputedStyle cs, Axis axis, float ownerWidth, Justify? justify, Align? align)
		{
			float nodeSize = GetMeasured(node, axis);
			float childSize = GetMeasured(child, axis);
			float axisSize = nodeSize - LeadingBorder(style, axis) - TrailingBorder(style, axis) - LeadingPadding(style, axis, ownerWidth) - TrailingPadding(style, axis, ownerWidth);
			if (IsLeadingPositionDefined(cs, axis))
			{
				SetPosition(child, axis, LeadingBorder(style, axis) + LeadingPositionValue(cs, axis, axisSize) + LeadingMarginValue(cs, axis, ownerWidth));
			}
			else if (IsTrailingPositionDefined(cs, axis))
			{
				SetPosition(child, axis, nodeSize - childSize - TrailingBorder(style, axis) - TrailingMarginValue(cs, axis, ownerWidth) - TrailingPositionValue(cs, axis, axisSize));
			}
			else if (justify.HasValue && justify.Value == Justify.Center || align.HasValue && align.Value == Align.Center)
			{
				SetPosition(child, axis, (nodeSize - childSize) / 2f);
			}
			else if (justify.HasValue && justify.Value == Justify.FlexEnd || align.HasValue && align.Value == Align.FlexEnd)
			{
				SetPosition(child, axis, nodeSize - childSize - TrailingBorder(style, axis) - TrailingMarginValue(cs, axis, ownerWidth));
			}
			else
			{
				SetPosition(child, axis, LeadingBorder(style, axis) + LeadingMarginValue(cs, axis, ownerWidth));
			}
		}

		// ------------------------------------------------------------------ rounding

		private static void RoundToPixelGrid(VisualElement node, float absoluteLeft, float absoluteTop)
		{
			LayoutNodeData data = node.layoutNode;
			float nodeLeft = data.rawX;
			float nodeTop = data.rawY;
			float absoluteNodeLeft = absoluteLeft + nodeLeft;
			float absoluteNodeTop = absoluteTop + nodeTop;
			// The committed size of the last full layout: measurements in between (a parent probing its content
			// size) overwrite the raw size without laying the node out again.
			float absoluteNodeRight = absoluteNodeLeft + data.lastLayoutWidth;
			float absoluteNodeBottom = absoluteNodeTop + data.lastLayoutHeight;

			Rect old = data.rect;
			Rect rounded = new Rect(
				RoundValue(absoluteNodeLeft) - RoundValue(absoluteLeft),
				RoundValue(absoluteNodeTop) - RoundValue(absoluteTop),
				RoundValue(absoluteNodeRight) - RoundValue(absoluteNodeLeft),
				RoundValue(absoluteNodeBottom) - RoundValue(absoluteNodeTop));

			if (rounded != old)
			{
				data.rect = rounded;
				node.OnLayoutChanged(old, rounded);
			}

			List<VisualElement> children = node.m_Children;
			if (children == null)
			{
				return;
			}

			for (int i = 0; i < children.Count; i++)
			{
				VisualElement child = children[i];
				if (child.resolvedStyleDisplayNone)
				{
					if (child.layoutNode.rect != Rect.zero)
					{
						Rect previous = child.layoutNode.rect;
						child.layoutNode.rect = Rect.zero;
						child.OnLayoutChanged(previous, Rect.zero);
					}

					continue;
				}

				RoundToPixelGrid(child, absoluteNodeLeft, absoluteNodeTop);
			}
		}

		private static float RoundValue(float value)
		{
			return (float)Math.Round(value, MidpointRounding.AwayFromZero);
		}

		#endregion
	}
}
