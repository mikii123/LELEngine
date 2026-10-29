using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>What changed on an element: the panel's updaters run only the passes a change needs.</summary>
	[Flags]
	public enum VersionChangeType
	{
		None = 0,

		/// <summary>The USS cascade must run again for the element (and its descendants, which inherit).</summary>
		Styles = 1,

		/// <summary>Sizes or positions changed: the layout pass runs on the element and its ancestors.</summary>
		Layout = 2,

		/// <summary>The element's world transform, clip rectangle or opacity changed (its subtree follows).</summary>
		Transform = 4,

		/// <summary>The element's geometry must be regenerated.</summary>
		Repaint = 8,

		/// <summary>Children were added, removed or reordered: draw order and picking change.</summary>
		Hierarchy = 16,

		/// <summary>Style sheets attached to the element changed: its subtree matches rules again.</summary>
		StyleSheet = 32,

		/// <summary>Text content or text style changed: the element re-measures before layout.</summary>
		Text = 64,

		/// <summary>Only the element's pointer picking changed.</summary>
		Picking = 128
	}

	/// <summary>
	///     Base class of everything in a UI tree: a box with a style, children, events and a layout. Add elements to
	///     a <see cref="UIDocument" />'s root (or any element already in a panel); the panel resolves their USS
	///     styles, lays them out with flexbox and draws them. Mirrors Unity's UI Toolkit API so documents, style
	///     sheets and code port over.
	/// </summary>
	public partial class VisualElement : Focusable, IEnumerable<VisualElement>
	{
		#region PublicFields

		/// <summary>USS class every element has (the type's default class is added by controls).</summary>
		public static readonly string disabledUssClassName = "unity-disabled";

		/// <summary>Name used by #name selectors and <see cref="Q" />.</summary>
		public string name
		{
			get => m_Name;
			set
			{
				if (string.Equals(m_Name, value, StringComparison.Ordinal))
				{
					return;
				}

				m_Name = value;
				IncrementVersion(VersionChangeType.Styles);
			}
		}

		/// <summary>The C# type name selectors match (Button, Label, ...).</summary>
		public string typeName { get; }

		public string tooltip { get; set; }
		public object userData { get; set; }

		/// <summary>Key used to persist view state (kept for API compatibility; nothing is persisted).</summary>
		public string viewDataKey { get; set; }

		public PickingMode pickingMode
		{
			get => m_PickingMode;
			set
			{
				if (m_PickingMode != value)
				{
					m_PickingMode = value;
					IncrementVersion(VersionChangeType.Picking);
				}
			}
		}

		public UsageHints usageHints { get; set; }

		/// <summary>The panel the element belongs to (null while detached).</summary>
		public IPanel panel => elementPanel;

		/// <summary>The logical parent: the element this one was added to (see <see cref="contentContainer" />).</summary>
		public VisualElement parent => m_LogicalParent;

		/// <summary>Element that receives the children added with <see cref="Add" /> (itself unless a control overrides it).</summary>
		public virtual VisualElement contentContainer => this;

		/// <summary>Own enabled flag; <see cref="enabledInHierarchy" /> also considers the ancestors.</summary>
		public bool enabledSelf => m_EnabledSelf;

		/// <summary>Enabled, and every ancestor is enabled. Disabled elements get the :disabled pseudo-class and no pointer events.</summary>
		public bool enabledInHierarchy => (m_PseudoStates & PseudoStates.Disabled) == 0;

		/// <summary>Shortcut for style.visibility: a hidden element keeps its layout space but is not drawn or picked.</summary>
		public bool visible
		{
			get => computedStyle.inherited.visibility == Visibility.Visible;
			set => style.visibility = value ? Visibility.Visible : Visibility.Hidden;
		}

		/// <summary>Custom geometry callback, invoked when the element repaints (<see cref="MeshGenerationContext" />).</summary>
		public Action<MeshGenerationContext> generateVisualContent
		{
			get => m_GenerateVisualContent;
			set
			{
				m_GenerateVisualContent = value;
				MarkDirtyRepaint();
			}
		}

		public override FocusController focusController => elementPanel?.focusController;

		public override bool canGrabFocus => focusable && enabledInHierarchy && resolvedStyleVisible && !resolvedStyleDisplayNone;

		/// <summary>Pseudo-classes the element currently has (:hover, :active, :focus, :disabled, :checked, :root).</summary>
		public PseudoStates pseudoStates
		{
			get => m_PseudoStates;
			internal set
			{
				if (m_PseudoStates == value)
				{
					return;
				}

				PseudoStates changed = m_PseudoStates ^ value;
				m_PseudoStates = value;
				// Only restyle when a selector of the panel cares about the changed state.
				if (elementPanel == null || (elementPanel.usedPseudoStates & changed) != 0)
				{
					IncrementVersion(VersionChangeType.Styles);
				}
			}
		}

		#endregion

		#region InternalFields

		internal VisualElement m_PhysicalParent;
		internal VisualElement m_LogicalParent;
		internal List<VisualElement> m_Children;
		internal BaseVisualElementPanel elementPanel;

		/// <summary>Depth in the tree (root = 0); style updates process dirty elements shallow to deep.</summary>
		internal int depth;

		internal VersionChangeType dirtyFlags;
		internal bool hasDirtyLayoutDescendant;
		internal bool hasDirtyTransformDescendant;

		/// <summary>Position in the draw / picking order (assigned by the hierarchy pass).</summary>
		internal int drawOrder;

		/// <summary>Resolved style; shared with other elements until one of them diverges.</summary>
		internal ComputedStyle computedStyle = ComputedStyle.Default;

		/// <summary>Result of the cascade without the inline style (reused when only the inline style changes).</summary>
		internal ComputedStyle cascadedStyle;

		internal long matchedRulesHash;
		internal List<StyleSheet> styleSheetList;
		internal InlineStyle inlineStyle;
		internal StyleTransitionState transitionState;
		internal VisualElementScheduler scheduler;

		// World-space data computed by the transform pass.
		internal Matrix2x3 worldTransformRef = Matrix2x3.identity;
		internal Matrix2x3 worldTransformInverse = Matrix2x3.identity;
		internal bool worldTransformInverseDirty = true;
		internal Rect worldClipRef = new Rect(float.MinValue * 0.5f, float.MinValue * 0.5f, float.MaxValue, float.MaxValue);
		internal float worldOpacity = 1f;

		/// <summary>Under a display: none ancestor: drawn with opacity 0 until the subtree's transforms are recomputed.</summary>
		internal bool hiddenByDisplay;
		internal bool isWorldTransformIdentityScaleRotation = true;

		/// <summary>The element's slot in the renderer (element data, mesh); -1 while not attached.</summary>
		internal int renderSlot = -1;

		internal bool resolvedStyleDisplayNone => computedStyle.display == DisplayStyle.None;
		internal bool resolvedStyleVisible => computedStyle.inherited.visibility == Visibility.Visible;

		#endregion

		#region PrivateFields

		private string m_Name;
		private PickingMode m_PickingMode = PickingMode.Position;
		private bool m_EnabledSelf = true;
		private PseudoStates m_PseudoStates;
		private List<string> m_ClassList;
		private Action<MeshGenerationContext> m_GenerateVisualContent;
		// Weak keys: element types of the game scripts (a collectible assembly) do not keep it loaded after a reload.
		private static readonly ConditionalWeakTable<Type, string> typeNames = new ConditionalWeakTable<Type, string>();
		private static readonly ConditionalWeakTable<Type, string>.CreateValueCallback typeNameOf = TypeNameOf;

		#endregion

		#region Constructors

		public VisualElement()
		{
			typeName = typeNames.GetValue(GetType(), typeNameOf);
			layoutNode = new LayoutNodeData();
		}

		/// <summary>The type's name without a generic arity suffix ("ListView`1" → "ListView").</summary>
		private static string TypeNameOf(Type type)
		{
			string name = type.Name;
			int generic = name.IndexOf('`');
			return generic >= 0 ? name.Substring(0, generic) : name;
		}

		#endregion

		#region PublicMethods

		// ------------------------------------------------------------------ classes

		public IEnumerable<string> GetClasses()
		{
			return m_ClassList ?? (IEnumerable<string>)Array.Empty<string>();
		}

		public void AddToClassList(string className)
		{
			if (string.IsNullOrEmpty(className))
			{
				return;
			}

			m_ClassList ??= new List<string>(4);
			if (m_ClassList.Contains(className))
			{
				return;
			}

			m_ClassList.Add(className);
			IncrementVersion(VersionChangeType.Styles);
		}

		public void RemoveFromClassList(string className)
		{
			if (m_ClassList != null && m_ClassList.Remove(className))
			{
				IncrementVersion(VersionChangeType.Styles);
			}
		}

		public void ToggleInClassList(string className)
		{
			if (ClassListContains(className))
			{
				RemoveFromClassList(className);
			}
			else
			{
				AddToClassList(className);
			}
		}

		public void EnableInClassList(string className, bool enable)
		{
			if (enable)
			{
				AddToClassList(className);
			}
			else
			{
				RemoveFromClassList(className);
			}
		}

		public bool ClassListContains(string className)
		{
			return m_ClassList != null && m_ClassList.Contains(className);
		}

		public void ClearClassList()
		{
			if (m_ClassList != null && m_ClassList.Count > 0)
			{
				m_ClassList.Clear();
				IncrementVersion(VersionChangeType.Styles);
			}
		}

		// ------------------------------------------------------------------ enabled

		/// <summary>Enables or disables the element and its subtree (disabled: :disabled pseudo-class, no pointer events, no focus).</summary>
		public void SetEnabled(bool value)
		{
			if (m_EnabledSelf == value)
			{
				return;
			}

			m_EnabledSelf = value;
			bool parentEnabled = m_PhysicalParent == null || m_PhysicalParent.enabledInHierarchy;
			PropagateEnabled(parentEnabled);
		}

		// ------------------------------------------------------------------ events

		/// <summary>Sends the event through the panel's dispatcher (trickle down, target, bubble up).</summary>
		public override void SendEvent(EventBase e)
		{
			if (e == null)
			{
				return;
			}

			if (e.target == null)
			{
				e.target = this;
			}

			EventDispatcher.Dispatch(e, elementPanel);
		}

		/// <summary>Attaches a manipulator (registers its callbacks on this element).</summary>
		public void AddManipulator(IManipulator manipulator)
		{
			if (manipulator != null)
			{
				manipulator.target = this;
			}
		}

		public void RemoveManipulator(IManipulator manipulator)
		{
			if (manipulator != null && manipulator.target == this)
			{
				manipulator.target = null;
			}
		}

		// ------------------------------------------------------------------ pointer capture

		public void CapturePointer(int pointerId)
		{
			elementPanel?.pointerCapture.Capture(this, pointerId);
		}

		public void ReleasePointer(int pointerId)
		{
			elementPanel?.pointerCapture.Release(this, pointerId);
		}

		public bool HasPointerCapture(int pointerId)
		{
			return elementPanel != null && elementPanel.pointerCapture.GetCapturing(pointerId) == this;
		}

		/// <summary>Legacy mouse capture (pointer id 0).</summary>
		public void CaptureMouse()
		{
			CapturePointer(PointerId.mousePointerId);
		}

		public void ReleaseMouse()
		{
			ReleasePointer(PointerId.mousePointerId);
		}

		public bool HasMouseCapture()
		{
			return HasPointerCapture(PointerId.mousePointerId);
		}

		// ------------------------------------------------------------------ repaint and scheduling

		/// <summary>Regenerates the element's geometry on the next frame (after changing what generateVisualContent draws).</summary>
		public void MarkDirtyRepaint()
		{
			IncrementVersion(VersionChangeType.Repaint);
		}

		/// <summary>Timed callbacks that run while the element is in a panel.</summary>
		public IVisualElementScheduler schedule => scheduler ??= new VisualElementScheduler(this);

		// ------------------------------------------------------------------ coordinates

		/// <summary>Position and size relative to the parent's content box (after layout).</summary>
		public Rect layout
		{
			get
			{
				elementPanel?.ValidateLayout();
				return layoutNode.rect;
			}
		}

		/// <summary>The padding box (inside the borders) in local coordinates.</summary>
		public Rect paddingRect
		{
			get
			{
				Rect r = layout;
				return new Rect(layoutNode.borderLeft, layoutNode.borderTop, Math.Max(0f, r.width - layoutNode.borderLeft - layoutNode.borderRight), Math.Max(0f, r.height - layoutNode.borderTop - layoutNode.borderBottom));
			}
		}

		/// <summary>The content box (inside borders and padding) in local coordinates.</summary>
		public Rect contentRect
		{
			get
			{
				Rect r = layout;
				float left = layoutNode.borderLeft + layoutNode.paddingLeft;
				float top = layoutNode.borderTop + layoutNode.paddingTop;
				return new Rect(left, top, Math.Max(0f, r.width - left - layoutNode.borderRight - layoutNode.paddingRight), Math.Max(0f, r.height - top - layoutNode.borderBottom - layoutNode.paddingBottom));
			}
		}

		/// <summary>Local-to-panel transform (layout position, translate, rotate, scale of this element and its ancestors).</summary>
		public Matrix2x3 worldTransform
		{
			get
			{
				elementPanel?.ValidateTransforms();
				return worldTransformRef;
			}
		}

		/// <summary>Axis-aligned bounds of the element in panel coordinates.</summary>
		public Rect worldBound
		{
			get
			{
				Rect r = layout;
				return worldTransform.TransformRect(new Rect(0f, 0f, r.width, r.height));
			}
		}

		/// <summary>Axis-aligned bounds in the parent's coordinates (layout plus this element's own transform).</summary>
		public Rect localBound
		{
			get
			{
				Rect r = layout;
				Matrix2x3 local = ComputeLocalTransform();
				return local.TransformRect(new Rect(0f, 0f, r.width, r.height));
			}
		}

		/// <summary>Legacy transform API (position, rotation, scale) backed by the translate / rotate / scale styles.</summary>
		public ITransform transform => new TransformAccess(this);

		public Vector2 WorldToLocal(Vector2 point)
		{
			elementPanel?.ValidateTransforms();
			return GetWorldInverse().MultiplyPoint(point);
		}

		public Vector2 LocalToWorld(Vector2 point)
		{
			return worldTransform.MultiplyPoint(point);
		}

		public Rect WorldToLocal(Rect rect)
		{
			elementPanel?.ValidateTransforms();
			return GetWorldInverse().TransformRect(rect);
		}

		public Rect LocalToWorld(Rect rect)
		{
			return worldTransform.TransformRect(rect);
		}

		public Vector2 ChangeCoordinatesTo(VisualElement dest, Vector2 point)
		{
			return dest.WorldToLocal(LocalToWorld(point));
		}

		public Rect ChangeCoordinatesTo(VisualElement dest, Rect rect)
		{
			return dest.WorldToLocal(LocalToWorld(rect));
		}

		/// <summary>True when the local point is inside the element's layout rectangle.</summary>
		public virtual bool ContainsPoint(Vector2 localPoint)
		{
			Rect r = layout;
			return localPoint.X >= 0f && localPoint.X < r.width && localPoint.Y >= 0f && localPoint.Y < r.height;
		}

		public virtual bool Overlaps(Rect rectangle)
		{
			Rect r = layout;
			return new Rect(0f, 0f, r.width, r.height).Overlaps(rectangle);
		}

		public override string ToString()
		{
			return typeName + (string.IsNullOrEmpty(m_Name) ? "" : " #" + m_Name) + (m_ClassList != null && m_ClassList.Count > 0 ? " ." + string.Join(" .", m_ClassList) : "");
		}

		#endregion

		#region InternalMethods

		/// <summary>Records a change: the panel's next update runs the passes the change needs.</summary>
		internal void IncrementVersion(VersionChangeType change)
		{
			if ((change & (VersionChangeType.Styles | VersionChangeType.StyleSheet)) != 0)
			{
				if ((dirtyFlags & VersionChangeType.Styles) == 0)
				{
					dirtyFlags |= VersionChangeType.Styles;
					elementPanel?.MarkStylesDirty(this);
				}
			}

			if ((change & VersionChangeType.Text) != 0)
			{
				dirtyFlags |= VersionChangeType.Text;
				layoutNode.InvalidateMeasure();
				change |= VersionChangeType.Layout | VersionChangeType.Repaint;
			}

			if ((change & VersionChangeType.Layout) != 0)
			{
				MarkLayoutDirty();
			}

			if ((change & VersionChangeType.Transform) != 0)
			{
				MarkTransformDirty();
			}

			if ((change & VersionChangeType.Repaint) != 0)
			{
				// Border colors, radii, opacity and text outline live in the per-element GPU data, not the mesh.
				renderDataDirty = true;
				if ((dirtyFlags & VersionChangeType.Repaint) == 0)
				{
					dirtyFlags |= VersionChangeType.Repaint;
					elementPanel?.MarkRepaintDirty(this);
				}
			}

			if ((change & VersionChangeType.Hierarchy) != 0)
			{
				dirtyFlags |= VersionChangeType.Hierarchy;
				if (elementPanel != null)
				{
					elementPanel.hierarchyDirty = true;
				}
			}

			if ((change & VersionChangeType.Picking) != 0 && elementPanel != null)
			{
				elementPanel.pickingDirty = true;
			}
		}

		internal void MarkLayoutDirty()
		{
			if ((dirtyFlags & VersionChangeType.Layout) != 0 && layoutNode.dirty)
			{
				return;
			}

			dirtyFlags |= VersionChangeType.Layout;
			layoutNode.dirty = true;
			layoutNode.InvalidateMeasure();
			if (elementPanel != null)
			{
				elementPanel.layoutDirty = true;
			}

			for (VisualElement ancestor = m_PhysicalParent; ancestor != null && !ancestor.hasDirtyLayoutDescendant; ancestor = ancestor.m_PhysicalParent)
			{
				ancestor.hasDirtyLayoutDescendant = true;
				// A dirty descendant can change the ancestor's size when it is auto-sized: mark the chain dirty too.
				ancestor.layoutNode.dirty = true;
				ancestor.layoutNode.InvalidateMeasure();
				ancestor.dirtyFlags |= VersionChangeType.Layout;
			}
		}

		internal void MarkTransformDirty()
		{
			if ((dirtyFlags & VersionChangeType.Transform) != 0)
			{
				return;
			}

			dirtyFlags |= VersionChangeType.Transform;
			worldTransformInverseDirty = true;
			if (elementPanel != null)
			{
				elementPanel.transformDirty = true;
			}

			for (VisualElement ancestor = m_PhysicalParent; ancestor != null && !ancestor.hasDirtyTransformDescendant; ancestor = ancestor.m_PhysicalParent)
			{
				ancestor.hasDirtyTransformDescendant = true;
			}
		}

		/// <summary>The element's own transform relative to its parent: layout position, translate, rotate and scale about the origin.</summary>
		internal Matrix2x3 ComputeLocalTransform()
		{
			ComputedStyle s = computedStyle;
			Rect r = layoutNode.rect;
			float tx = r.x + s.translate.x.Resolve(r.width);
			float ty = r.y + s.translate.y.Resolve(r.height);
			if (float.IsNaN(tx)) tx = r.x;
			if (float.IsNaN(ty)) ty = r.y;
			float angle = s.rotate.angle.ToDegrees();
			Vector2 scale = s.scale.value;
			if (angle == 0f && scale.X == 1f && scale.Y == 1f)
			{
				return Matrix2x3.Translation(tx, ty);
			}

			float ox = s.transformOrigin.x.Resolve(r.width);
			float oy = s.transformOrigin.y.Resolve(r.height);
			if (float.IsNaN(ox)) ox = r.width * 0.5f;
			if (float.IsNaN(oy)) oy = r.height * 0.5f;
			return Matrix2x3.TRS(new Vector2(tx, ty), angle, scale, new Vector2(ox, oy));
		}

		internal Matrix2x3 GetWorldInverse()
		{
			if (worldTransformInverseDirty)
			{
				worldTransformInverse = worldTransformRef.Inverse();
				worldTransformInverseDirty = false;
			}

			return worldTransformInverse;
		}

		internal bool HasClass(string className)
		{
			return m_ClassList != null && m_ClassList.Contains(className);
		}

		internal List<string> classListInternal => m_ClassList;

		internal bool IsAncestorOf(VisualElement element)
		{
			for (VisualElement e = element?.m_PhysicalParent; e != null; e = e.m_PhysicalParent)
			{
				if (e == this)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Resolves the inherited enabled state of this subtree after an ancestor or this element changed.</summary>
		internal void PropagateEnabled(bool parentEnabled)
		{
			bool enabled = parentEnabled && m_EnabledSelf;
			PseudoStates states = m_PseudoStates;
			if (enabled)
			{
				states &= ~PseudoStates.Disabled;
			}
			else
			{
				states |= PseudoStates.Disabled;
			}

			bool changed = states != m_PseudoStates;
			pseudoStates = states;
			if (changed)
			{
				EnableInClassList(disabledUssClassName, !enabled);
				if (!enabled)
				{
					focusController?.ReleaseFocus(this);
					pseudoStates &= ~(PseudoStates.Hover | PseudoStates.Active);
				}
			}

			if (m_Children != null)
			{
				for (int i = 0; i < m_Children.Count; i++)
				{
					m_Children[i].PropagateEnabled(enabled);
				}
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>Legacy transform access: position / rotation / scale map to the translate, rotate and scale styles.</summary>
		public interface ITransform
		{
			Vector3 position { get; set; }
			Quaternion rotation { get; set; }
			Vector3 scale { get; set; }
			Matrix2x3 matrix { get; }
		}

		private readonly struct TransformAccess : ITransform
		{
			private readonly VisualElement element;

			public TransformAccess(VisualElement element)
			{
				this.element = element;
			}

			public Vector3 position
			{
				get
				{
					Translate t = element.computedStyle.translate;
					return new Vector3(t.x.value, t.y.value, 0f);
				}
				set => element.style.translate = new Translate(value.X, value.Y);
			}

			public Quaternion rotation
			{
				get => Quaternion.FromAxisAngle(Vector3.UnitZ, element.computedStyle.rotate.angle.ToDegrees() * (float)(Math.PI / 180.0));
				set
				{
					Vector3 euler = value.ToEulerAngles();
					element.style.rotate = new Rotate(Angle.Degrees(euler.Z * (float)(180.0 / Math.PI)));
				}
			}

			public Vector3 scale
			{
				get
				{
					Vector2 s = element.computedStyle.scale.value;
					return new Vector3(s.X, s.Y, 1f);
				}
				set => element.style.scale = new Scale(new Vector2(value.X, value.Y));
			}

			public Matrix2x3 matrix => element.ComputeLocalTransform();
		}

		#endregion
	}
}
