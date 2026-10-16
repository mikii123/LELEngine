using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	public enum ContextType
	{
		Player,
		Editor
	}

	/// <summary>A UI tree with its own styles, layout, events and rendering (one per <see cref="UIDocument" />).</summary>
	public interface IPanel
	{
		VisualElement visualTree { get; }
		FocusController focusController { get; }
		ContextType contextType { get; }

		/// <summary>Topmost element under the point (panel coordinates) that accepts picking, or null.</summary>
		VisualElement Pick(Vector2 point);

		/// <summary>Every element under the point, topmost first; returns the topmost.</summary>
		VisualElement PickAll(Vector2 point, List<VisualElement> picked);
	}

	/// <summary>
	///     Owns a visual tree and runs its update passes in order: scheduled callbacks, style resolution, transitions,
	///     layout, transforms and clipping, geometry regeneration. Each pass only touches what changed since the last
	///     frame (dirty elements), so a static UI costs nothing per frame beyond the draw.
	/// </summary>
	public abstract partial class BaseVisualElementPanel : IPanel, IDisposable
	{
		#region PublicFields

		public VisualElement visualTree { get; }
		public FocusController focusController { get; }
		public abstract ContextType contextType { get; }
		public string name { get; set; } = "panel";

		/// <summary>Panel units to target pixels factor (PanelSettings scale).</summary>
		public float scale { get; private set; } = 1f;

		/// <summary>Size of the render target, in pixels.</summary>
		public int targetWidth { get; private set; } = 1;

		public int targetHeight { get; private set; } = 1;

		/// <summary>Size of the panel in panel units (target size / scale).</summary>
		public Vector2 size => new Vector2(targetWidth / scale, targetHeight / scale);

		/// <summary>Cursor the hovered element asks for (hosts apply it to the window).</summary>
		public CursorType cursor { get; internal set; } = CursorType.Arrow;

		/// <summary>The element the pointer is over (topmost), null when none.</summary>
		public VisualElement topElementUnderPointer { get; internal set; }

		/// <summary>Time of the last update, in milliseconds (transitions and schedulers run on it).</summary>
		public long timeMs { get; private set; }

		/// <summary>Transitions or scheduled callbacks are running: the panel changes without input (on-demand hosts keep rendering).</summary>
		public bool needsContinuousUpdate => transitions.activeCount > 0 || activeSchedulers.Count > 0;

		/// <summary>Pointer capture state of this panel.</summary>
		public PointerCaptureHelper pointerCapture { get; }

		/// <summary>Draw order and pick data are rebuilt when children change.</summary>
		internal bool hierarchyDirty = true;

		internal bool layoutDirty = true;
		internal bool transformDirty = true;
		internal bool pickingDirty = true;

		/// <summary>Pseudo states any selector of the panel's sheets uses (changes of other states skip restyling).</summary>
		internal PseudoStates usedPseudoStates = (PseudoStates)~0;

		/// <summary>Version of the sheet set: bumped when a sheet is attached anywhere in the tree (style caches reset).</summary>
		internal int styleSheetVersion;

		/// <summary>Bumped after every layout pass (elements that cache measurements against the layout can check it).</summary>
		internal int layoutVersion;

		/// <summary>Bumped when the draw order was rebuilt (the renderer rebuilds its index list).</summary>
		internal int drawOrderVersion;

		#endregion

		#region InternalFields

		internal readonly StyleResolver styleResolver;
		internal readonly TransitionRunner transitions;
		internal readonly List<VisualElement> stylesDirty = new List<VisualElement>();
		internal readonly HashSet<VisualElement> repaintDirty = new HashSet<VisualElement>();
		internal readonly List<VisualElement> drawOrder = new List<VisualElement>();
		internal int elementCount;

		/// <summary>The renderer's per-panel state (created by the render pass on first draw).</summary>
		internal Rendering.UIRenderer renderer;

		#endregion

		#region PrivateFields

		private readonly List<VisualElement> activeSchedulers = new List<VisualElement>();
		private readonly List<GeometryChange> geometryChanges = new List<GeometryChange>();
		private readonly Stack<int> freeRenderSlots = new Stack<int>();
		private int nextRenderSlot;
		private bool updating;
		private bool validating;
		private bool disposed;

		#endregion

		#region Constructors

		protected BaseVisualElementPanel()
		{
			focusController = new FocusController(this);
			pointerCapture = new PointerCaptureHelper(this);
			styleResolver = new StyleResolver(this);
			transitions = new TransitionRunner(this);
			visualTree = new PanelRootElement(this) { name = "root" };
			visualTree.SetPanel(this);
			visualTree.pseudoStates |= PseudoStates.Root;
			visualTree.style.position = Position.Absolute;
			visualTree.style.left = 0f;
			visualTree.style.top = 0f;
			visualTree.style.width = 1f;
			visualTree.style.height = 1f;
			visualTree.style.overflow = Overflow.Hidden;
		}

		#endregion

		#region PublicMethods

		/// <summary>Sets the render target size and scale; the root element fills the panel.</summary>
		public void SetTargetSize(int width, int height, float panelScale)
		{
			width = Math.Max(1, width);
			height = Math.Max(1, height);
			panelScale = panelScale > 0.0001f ? panelScale : 1f;
			if (width == targetWidth && height == targetHeight && panelScale == scale)
			{
				return;
			}

			targetWidth = width;
			targetHeight = height;
			scale = panelScale;
			visualTree.style.width = width / panelScale;
			visualTree.style.height = height / panelScale;
			visualTree.MarkTransformDirty();
		}

		/// <summary>Runs every pending pass. Called once per frame before drawing; safe to call more often.</summary>
		public void Update(long nowMs)
		{
			if (disposed || updating)
			{
				return;
			}

			updating = true;
			try
			{
				timeMs = nowMs;
				RunSchedulers(nowMs);
				UpdateBindings();
				UpdateStyles();
				transitions.Update(nowMs);
				UpdateLayout();
				UpdateTransforms();
				UpdateHierarchy();
			}
			finally
			{
				updating = false;
			}
		}

		/// <summary>Brings styles and layout up to date now (called by layout getters when they are dirty).</summary>
		public void ValidateLayout()
		{
			if (disposed || updating || validating || (!layoutDirty && stylesDirty.Count == 0))
			{
				return;
			}

			validating = true;
			try
			{
				UpdateStyles();
				UpdateLayout();
			}
			finally
			{
				validating = false;
			}
		}

		public void ValidateTransforms()
		{
			if (disposed || updating || validating)
			{
				return;
			}

			ValidateLayout();
			if (!transformDirty)
			{
				return;
			}

			validating = true;
			try
			{
				UpdateTransforms();
			}
			finally
			{
				validating = false;
			}
		}

		public VisualElement Pick(Vector2 point)
		{
			ValidateTransforms();
			return PickRecursive(visualTree, point, null);
		}

		public VisualElement PickAll(Vector2 point, List<VisualElement> picked)
		{
			ValidateTransforms();
			picked?.Clear();
			return PickRecursive(visualTree, point, picked);
		}

		/// <summary>Resets every style cache (sheets changed on disk) and restyles the tree.</summary>
		public void InvalidateStyles()
		{
			styleSheetVersion++;
			styleResolver.ClearCache();
			visualTree.IncrementVersion(VersionChangeType.Styles);
			visualTree.ForEachDescendant(e => e.IncrementVersion(VersionChangeType.Styles));
		}

		public virtual void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			renderer?.Dispose();
			renderer = null;
			visualTree.Clear();
		}

		#endregion

		#region InternalMethods

		internal void MarkStylesDirty(VisualElement element)
		{
			stylesDirty.Add(element);
		}

		internal void MarkRepaintDirty(VisualElement element)
		{
			repaintDirty.Add(element);
		}

		internal void OnStyleSheetsChanged()
		{
			styleSheetVersion++;
			styleResolver.ClearCache();
			usedPseudoStates = (PseudoStates)~0;
		}

		internal void OnElementAttached(VisualElement element)
		{
			element.renderSlot = freeRenderSlots.Count > 0 ? freeRenderSlots.Pop() : nextRenderSlot++;
			elementCount++;
			hierarchyDirty = true;
			pickingDirty = true;
			if (element.scheduler != null && element.scheduler.items.Count > 0)
			{
				RegisterScheduler(element);
			}

			if (element.bindings != null && element.bindings.Count > 0)
			{
				RegisterBindingTarget(element);
				element.ActivateBindings();
			}
		}

		internal void OnElementDetached(VisualElement element)
		{
			if (element.renderSlot >= 0)
			{
				renderer?.ReleaseElement(element);
				freeRenderSlots.Push(element.renderSlot);
				element.renderSlot = -1;
			}

			elementCount--;
			hierarchyDirty = true;
			pickingDirty = true;
			repaintDirty.Remove(element);
			activeSchedulers.Remove(element);
			bindingTargets.Remove(element);
			element.DeactivateBindings();
			transitions.Cancel(element);
			focusController.ReleaseFocus(element);
			pointerCapture.ReleaseAll(element);
			if (topElementUnderPointer == element)
			{
				topElementUnderPointer = null;
			}

			if (hoveredElement == element)
			{
				hoveredElement = null;
			}
		}

		internal void RegisterScheduler(VisualElement element)
		{
			if (element.elementPanel == this && !activeSchedulers.Contains(element))
			{
				activeSchedulers.Add(element);
			}
		}

		internal void RegisterBindingTarget(VisualElement element)
		{
			if (element.elementPanel == this && !bindingTargets.Contains(element))
			{
				bindingTargets.Add(element);
			}
		}

		internal void UnregisterBindingTarget(VisualElement element)
		{
			bindingTargets.Remove(element);
		}

		private readonly List<VisualElement> bindingTargets = new List<VisualElement>();

		private void UpdateBindings()
		{
			if (bindingTargets.Count == 0)
			{
				return;
			}

			VisualElement[] targets = bindingTargets.ToArray();
			foreach (VisualElement element in targets)
			{
				if (element.elementPanel != this || !element.UpdateBindings())
				{
					bindingTargets.Remove(element);
				}
			}
		}

		internal void QueueGeometryChanged(VisualElement element, Rect oldRect, Rect newRect)
		{
			geometryChanges.Add(new GeometryChange { element = element, oldRect = oldRect, newRect = newRect });
		}

		internal int renderSlotCount => nextRenderSlot;

		#endregion

		#region PrivateMethods

		private void RunSchedulers(long nowMs)
		{
			if (activeSchedulers.Count == 0)
			{
				return;
			}

			VisualElement[] elements = activeSchedulers.ToArray();
			foreach (VisualElement element in elements)
			{
				if (element.elementPanel != this || element.scheduler == null)
				{
					activeSchedulers.Remove(element);
					continue;
				}

				if (!element.scheduler.Update(nowMs))
				{
					activeSchedulers.Remove(element);
				}
			}
		}

		private void UpdateStyles()
		{
			if (stylesDirty.Count == 0)
			{
				return;
			}

			styleResolver.UpdateDirty(stylesDirty);
			stylesDirty.Clear();
		}

		private void UpdateLayout()
		{
			if (!layoutDirty)
			{
				return;
			}

			layoutDirty = false;
			FlexLayout.Layout(visualTree, targetWidth / scale, targetHeight / scale);
			layoutVersion++;
			// Layout may have found new text measurements that changed styles (none), but geometry events may change
			// the tree: run them after the pass, then catch the follow-up dirt on the next Update / Validate.
			if (geometryChanges.Count > 0)
			{
				GeometryChange[] changes = geometryChanges.ToArray();
				geometryChanges.Clear();
				foreach (GeometryChange change in changes)
				{
					if (change.element.elementPanel != this)
					{
						continue;
					}

					using (GeometryChangedEvent evt = GeometryChangedEvent.GetPooled(change.oldRect, change.newRect))
					{
						evt.target = change.element;
						change.element.SendEvent(evt);
					}
				}
			}
		}

		private void UpdateTransforms()
		{
			if (!transformDirty)
			{
				return;
			}

			transformDirty = false;
			TransformUpdater.Update(this);
		}

		private void UpdateHierarchy()
		{
			if (!hierarchyDirty)
			{
				return;
			}

			hierarchyDirty = false;
			pickingDirty = false;
			drawOrder.Clear();
			CollectDrawOrder(visualTree);
			drawOrderVersion++;
		}

		private void CollectDrawOrder(VisualElement element)
		{
			element.drawOrder = drawOrder.Count;
			drawOrder.Add(element);
			List<VisualElement> children = element.m_Children;
			if (children == null)
			{
				return;
			}

			for (int i = 0; i < children.Count; i++)
			{
				CollectDrawOrder(children[i]);
			}
		}

		private static VisualElement PickRecursive(VisualElement element, Vector2 point, List<VisualElement> picked)
		{
			if (element.resolvedStyleDisplayNone || !element.resolvedStyleVisible)
			{
				return null;
			}

			if (!element.subtreeWorldBounds.Contains(point))
			{
				return null;
			}

			Vector2 local = element.GetWorldInverse().MultiplyPoint(point);
			Rect rect = element.layoutNode.rect;
			bool inside = local.X >= 0f && local.X < rect.width && local.Y >= 0f && local.Y < rect.height;
			if (!inside && element.computedStyle.overflow == Overflow.Hidden)
			{
				return null;
			}

			List<VisualElement> children = element.m_Children;
			VisualElement result = null;
			if (children != null)
			{
				for (int i = children.Count - 1; i >= 0; i--)
				{
					VisualElement hit = PickRecursive(children[i], point, picked);
					if (hit != null)
					{
						if (picked == null)
						{
							return hit;
						}

						result ??= hit;
					}
				}
			}

			if (inside && element.pickingMode == PickingMode.Position)
			{
				picked?.Add(element);
				return result ?? element;
			}

			return result;
		}

		#endregion

		#region NestedTypes

		private struct GeometryChange
		{
			public VisualElement element;
			public Rect oldRect;
			public Rect newRect;
		}

		#endregion

		// Input and hover state (see Panel.Input.cs)
		internal VisualElement hoveredElement;
	}

	/// <summary>The panel of a game UI (<see cref="UIDocument" />).</summary>
	public sealed class RuntimePanel : BaseVisualElementPanel
	{
		public override ContextType contextType => ContextType.Player;
	}

	/// <summary>The root element of a panel: default handling of navigation events (focus ring).</summary>
	internal sealed class PanelRootElement : VisualElement
	{
		private readonly BaseVisualElementPanel owner;

		public PanelRootElement(BaseVisualElementPanel owner)
		{
			this.owner = owner;
			pickingMode = PickingMode.Position;
		}

		protected override void ExecuteDefaultAction(EventBase evt)
		{
			base.ExecuteDefaultAction(evt);
			if (evt is NavigationMoveEvent move && !move.isHandled)
			{
				switch (move.direction)
				{
					case NavigationMoveEvent.Direction.Next:
					case NavigationMoveEvent.Direction.Right:
					case NavigationMoveEvent.Direction.Down:
						owner.focusController.MoveFocus(FocusChangeDirection.Next);
						break;
					case NavigationMoveEvent.Direction.Previous:
					case NavigationMoveEvent.Direction.Left:
					case NavigationMoveEvent.Direction.Up:
						owner.focusController.MoveFocus(FocusChangeDirection.Previous);
						break;
				}
			}
		}
	}

	/// <summary>Which element receives the events of each pointer while a button is held (drags, sliders).</summary>
	public sealed class PointerCaptureHelper
	{
		#region PrivateFields

		private readonly BaseVisualElementPanel panel;
		private readonly VisualElement[] capturing = new VisualElement[PointerId.maxPointers];

		#endregion

		#region Constructors

		internal PointerCaptureHelper(BaseVisualElementPanel panel)
		{
			this.panel = panel;
		}

		#endregion

		#region PublicMethods

		public VisualElement GetCapturing(int pointerId)
		{
			return pointerId >= 0 && pointerId < capturing.Length ? capturing[pointerId] : null;
		}

		public bool IsCaptured(int pointerId)
		{
			return GetCapturing(pointerId) != null;
		}

		public void Capture(VisualElement element, int pointerId)
		{
			if (pointerId < 0 || pointerId >= capturing.Length || element == null || element.elementPanel != panel)
			{
				return;
			}

			VisualElement previous = capturing[pointerId];
			if (previous == element)
			{
				return;
			}

			capturing[pointerId] = element;
			if (previous != null)
			{
				using (PointerCaptureOutEvent evt = PointerCaptureOutEvent.GetPooled())
				{
					evt.target = previous;
					evt.relatedTarget = element;
					evt.pointerId = pointerId;
					previous.SendEvent(evt);
				}
			}

			using (PointerCaptureEvent evt = PointerCaptureEvent.GetPooled())
			{
				evt.target = element;
				evt.relatedTarget = previous;
				evt.pointerId = pointerId;
				element.SendEvent(evt);
			}
		}

		public void Release(VisualElement element, int pointerId)
		{
			if (pointerId < 0 || pointerId >= capturing.Length || capturing[pointerId] != element)
			{
				return;
			}

			capturing[pointerId] = null;
			using (PointerCaptureOutEvent evt = PointerCaptureOutEvent.GetPooled())
			{
				evt.target = element;
				evt.pointerId = pointerId;
				element.SendEvent(evt);
			}
		}

		public void ReleaseAll(VisualElement element)
		{
			for (int i = 0; i < capturing.Length; i++)
			{
				if (capturing[i] == element)
				{
					capturing[i] = null;
				}
			}
		}

		#endregion
	}
}
