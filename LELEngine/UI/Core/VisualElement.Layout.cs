using System;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>Layout state of one element: the result rectangle, resolved box edges and the measurement cache.</summary>
	internal sealed class LayoutNodeData
	{
		#region PublicFields

		/// <summary>Position relative to the parent's border box and size, after pixel rounding.</summary>
		public Rect rect;

		/// <summary>Unrounded layout (the rounding of children uses the parent's fractional position).</summary>
		public float rawX, rawY, rawWidth, rawHeight;

		public float marginTop, marginRight, marginBottom, marginLeft;
		public float paddingTop, paddingRight, paddingBottom, paddingLeft;
		public float borderTop, borderRight, borderBottom, borderLeft;

		/// <summary>The node (or a descendant) needs layout.</summary>
		public bool dirty = true;

		/// <summary>The rectangle changed in the last layout pass (GeometryChangedEvent pending).</summary>
		public bool hasNewLayout;

		/// <summary>Constraints the last full layout of this node was computed for (skips unchanged subtrees).</summary>
		public float lastAvailableWidth = float.NaN, lastAvailableHeight = float.NaN;
		public VisualElement.MeasureMode lastWidthMode, lastHeightMode;
		public float lastOwnerWidth = float.NaN, lastOwnerHeight = float.NaN;
		public float lastLayoutWidth, lastLayoutHeight;
		public bool lastLayoutValid;

		/// <summary>Measurement cache: results of measuring the node under given constraints.</summary>
		public int measureCacheCount;
		public readonly MeasureCacheEntry[] measureCache = new MeasureCacheEntry[4];

		/// <summary>Line index when wrapping (used by align-content).</summary>
		public int lineIndex;

		/// <summary>Computed flex basis for the current layout pass.</summary>
		public float computedFlexBasis;

		#endregion

		#region PublicMethods

		public void InvalidateMeasure()
		{
			measureCacheCount = 0;
			lastLayoutValid = false;
		}

		public bool TryGetMeasure(float availableWidth, VisualElement.MeasureMode widthMode, float availableHeight, VisualElement.MeasureMode heightMode, float ownerWidth, float ownerHeight, out float width, out float height)
		{
			for (int i = 0; i < measureCacheCount; i++)
			{
				ref MeasureCacheEntry entry = ref measureCache[i];
				if (entry.widthMode == widthMode && entry.heightMode == heightMode && Same(entry.availableWidth, availableWidth) && Same(entry.availableHeight, availableHeight)
					&& Same(entry.ownerWidth, ownerWidth) && Same(entry.ownerHeight, ownerHeight))
				{
					width = entry.width;
					height = entry.height;
					return true;
				}
			}

			width = height = 0f;
			return false;
		}

		public void StoreMeasure(float availableWidth, VisualElement.MeasureMode widthMode, float availableHeight, VisualElement.MeasureMode heightMode, float ownerWidth, float ownerHeight, float width, float height)
		{
			int index = measureCacheCount < measureCache.Length ? measureCacheCount++ : measureCache.Length - 1;
			measureCache[index] = new MeasureCacheEntry
			{
				availableWidth = availableWidth,
				availableHeight = availableHeight,
				widthMode = widthMode,
				heightMode = heightMode,
				ownerWidth = ownerWidth,
				ownerHeight = ownerHeight,
				width = width,
				height = height
			};
		}

		private static bool Same(float a, float b)
		{
			return (float.IsNaN(a) && float.IsNaN(b)) || Math.Abs(a - b) < 0.0001f;
		}

		#endregion

		#region NestedTypes

		public struct MeasureCacheEntry
		{
			public float availableWidth, availableHeight, ownerWidth, ownerHeight;
			public VisualElement.MeasureMode widthMode, heightMode;
			public float width, height;
		}

		#endregion
	}

	public partial class VisualElement
	{
		#region PublicFields

		/// <summary>How a dimension is constrained when an element measures its content.</summary>
		public enum MeasureMode
		{
			/// <summary>No constraint: the element returns its natural size.</summary>
			Undefined,

			/// <summary>The size is fixed.</summary>
			Exactly,

			/// <summary>The size must not exceed the given value.</summary>
			AtMost
		}

		#endregion

		#region InternalFields

		internal readonly LayoutNodeData layoutNode;

		/// <summary>Leaf elements with content of their own size (text, images) measure themselves during layout.</summary>
		internal virtual bool requiresMeasure => false;

		#endregion

		#region PublicMethods

		/// <summary>Measures text as this element would draw it (its font, size and wrapping) under the constraints.</summary>
		public Vector2 MeasureTextSize(string textToMeasure, float width, MeasureMode widthMode, float height, MeasureMode heightMode)
		{
			return TextMeasurement.Measure(this, textToMeasure, width, widthMode, height, heightMode);
		}

		#endregion

		#region InternalMethods

		/// <summary>Content size of a measuring element (text, image) for the constraints; called by the layout.</summary>
		internal virtual Vector2 Measure(float width, MeasureMode widthMode, float height, MeasureMode heightMode)
		{
			return Vector2.Zero;
		}

		/// <summary>The layout pass computed a new rectangle: send the geometry event and refresh dependent state.</summary>
		internal void OnLayoutChanged(Rect oldRect, Rect newRect)
		{
			bool sizeChanged = oldRect.width != newRect.width || oldRect.height != newRect.height;
			if (sizeChanged)
			{
				IncrementVersion(VersionChangeType.Repaint);
			}

			MarkTransformDirty();
			if (elementPanel != null)
			{
				elementPanel.QueueGeometryChanged(this, oldRect, newRect);
			}
		}

		#endregion
	}
}
