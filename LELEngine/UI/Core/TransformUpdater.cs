using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	public partial class VisualElement
	{
		/// <summary>Panel-space bounds of this element and everything below it (picking and culling skip subtrees).</summary>
		internal Rect subtreeWorldBounds = new Rect(float.MinValue * 0.5f, float.MinValue * 0.5f, float.MaxValue, float.MaxValue);

		/// <summary>The renderer must refresh this element's per-element data (transform, clip, opacity).</summary>
		internal bool renderDataDirty = true;
	}

	/// <summary>
	///     Computes world transforms, clip rectangles, accumulated opacity and subtree bounds for the elements whose
	///     transform (or an ancestor's) changed. Clean subtrees are skipped.
	/// </summary>
	internal static class TransformUpdater
	{
		#region PublicMethods

		public static void Update(BaseVisualElementPanel panel)
		{
			var infinite = new Rect(-1e9f, -1e9f, 2e9f, 2e9f);
			Visit(panel.visualTree, Matrix2x3.identity, infinite, 1f, false);
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     A subtree under a display: none element: its elements keep their last layout, so the renderer gets opacity 0
		///     for each of them (once; shown again, the whole subtree recomputes).
		/// </summary>
		private static void HideSubtree(VisualElement element)
		{
			if (element.hiddenByDisplay)
			{
				return;
			}

			element.hiddenByDisplay = true;
			element.worldOpacity = 0f;
			element.renderDataDirty = true;
			List<VisualElement> children = element.m_Children;
			if (children == null)
			{
				return;
			}

			for (int i = 0; i < children.Count; i++)
			{
				HideSubtree(children[i]);
			}
		}

		private static void Visit(VisualElement element, in Matrix2x3 parentWorld, in Rect parentClip, float parentOpacity, bool parentDirty)
		{
			// Shown again after display: none: every transform and opacity of the subtree is stale.
			if (element.hiddenByDisplay)
			{
				parentDirty = true;
			}

			bool dirty = parentDirty || (element.dirtyFlags & VersionChangeType.Transform) != 0;
			if (!dirty && !element.hasDirtyTransformDescendant)
			{
				return;
			}

			ComputedStyle style = element.computedStyle;
			Rect rect = element.layoutNode.rect;
			if (dirty)
			{
				// Shown again after display: none: the subtree's transforms are recomputed below.
				element.hiddenByDisplay = false;
				Matrix2x3 local = element.ComputeLocalTransform();
				Matrix2x3 world = parentWorld.Multiply(local);
				element.worldTransformRef = world;
				element.worldTransformInverseDirty = true;
				element.isWorldTransformIdentityScaleRotation = world.isTranslationOnly;
				element.worldOpacity = parentOpacity * Math.Clamp(style.opacity, 0f, 1f);
				element.worldClipRef = parentClip;
				element.dirtyFlags &= ~VersionChangeType.Transform;
				element.renderDataDirty = true;
			}

			// Children are clipped by this element when it hides its overflow.
			Rect childClip = element.worldClipRef;
			if (style.overflow == Overflow.Hidden)
			{
				Rect clipBox = ClipBox(element, style);
				Rect worldBox = element.worldTransformRef.TransformRect(clipBox);
				childClip = Rect.Intersect(childClip, worldBox);
			}

			Rect bounds = element.worldTransformRef.TransformRect(new Rect(0f, 0f, rect.width, rect.height));
			List<VisualElement> children = element.m_Children;
			if (children != null)
			{
				for (int i = 0; i < children.Count; i++)
				{
					VisualElement child = children[i];
					if (child.resolvedStyleDisplayNone)
					{
						child.dirtyFlags &= ~VersionChangeType.Transform;
						child.hasDirtyTransformDescendant = false;
						// Its subtree keeps its last transforms; the renderer must not draw it any more.
						HideSubtree(child);
						continue;
					}

					Visit(child, element.worldTransformRef, childClip, element.worldOpacity, dirty);
					Rect childBounds = child.subtreeWorldBounds;
					if (style.overflow == Overflow.Hidden)
					{
						childBounds = Rect.Intersect(childBounds, childClip);
					}

					if (!childBounds.IsEmpty)
					{
						bounds = Rect.Union(bounds, childBounds);
					}
				}
			}

			element.subtreeWorldBounds = bounds;
			element.hasDirtyTransformDescendant = false;
		}

		/// <summary>The box overflow clips to: the padding box, or the content box with -unity-overflow-clip-box: content-box.</summary>
		internal static Rect ClipBox(VisualElement element, ComputedStyle style)
		{
			LayoutNodeData node = element.layoutNode;
			Rect rect = node.rect;
			if (style.unityOverflowClipBox == OverflowClipBox.ContentBox)
			{
				float left = node.borderLeft + node.paddingLeft;
				float top = node.borderTop + node.paddingTop;
				return new Rect(left, top, Math.Max(0f, rect.width - left - node.borderRight - node.paddingRight), Math.Max(0f, rect.height - top - node.borderBottom - node.paddingBottom));
			}

			return new Rect(node.borderLeft, node.borderTop, Math.Max(0f, rect.width - node.borderLeft - node.borderRight), Math.Max(0f, rect.height - node.borderTop - node.borderBottom));
		}

		#endregion
	}
}
