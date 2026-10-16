using System;
using System.Collections;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	public partial class VisualElement
	{
		#region PublicFields

		/// <summary>The physical children (bypasses <see cref="contentContainer" />).</summary>
		public Hierarchy hierarchy => new Hierarchy(this);

		/// <summary>Number of children in the content container.</summary>
		public int childCount => contentContainer.hierarchy.childCount;

		public VisualElement this[int index] => contentContainer.hierarchy[index];

		#endregion

		#region PublicMethods

		public void Add(VisualElement child)
		{
			if (child == null)
			{
				throw new ArgumentNullException(nameof(child));
			}

			VisualElement container = contentContainer;
			if (container == null || container == this)
			{
				hierarchy.Add(child);
			}
			else
			{
				container.hierarchy.Add(child);
			}

			child.m_LogicalParent = this;
		}

		public void Insert(int index, VisualElement child)
		{
			if (child == null)
			{
				throw new ArgumentNullException(nameof(child));
			}

			VisualElement container = contentContainer;
			if (container == null || container == this)
			{
				hierarchy.Insert(index, child);
			}
			else
			{
				container.hierarchy.Insert(index, child);
			}

			child.m_LogicalParent = this;
		}

		public void Remove(VisualElement child)
		{
			if (child == null)
			{
				throw new ArgumentNullException(nameof(child));
			}

			VisualElement container = contentContainer ?? this;
			if (child.m_PhysicalParent != container)
			{
				throw new ArgumentException("This VisualElement is not my child");
			}

			container.hierarchy.Remove(child);
		}

		public void RemoveAt(int index)
		{
			(contentContainer ?? this).hierarchy.RemoveAt(index);
		}

		public void Clear()
		{
			(contentContainer ?? this).hierarchy.Clear();
		}

		public VisualElement ElementAt(int index)
		{
			return (contentContainer ?? this).hierarchy[index];
		}

		public int IndexOf(VisualElement element)
		{
			return (contentContainer ?? this).hierarchy.IndexOf(element);
		}

		public bool Contains(VisualElement child)
		{
			for (VisualElement e = child; e != null; e = e.m_PhysicalParent)
			{
				if (e.m_PhysicalParent == this)
				{
					return true;
				}
			}

			return false;
		}

		public IEnumerable<VisualElement> Children()
		{
			return (contentContainer ?? this).hierarchy.Children();
		}

		public void RemoveFromHierarchy()
		{
			m_PhysicalParent?.hierarchy.Remove(this);
		}

		public void BringToFront()
		{
			VisualElement p = m_PhysicalParent;
			if (p != null && p.m_Children.Count > 1 && p.m_Children[p.m_Children.Count - 1] != this)
			{
				p.m_Children.Remove(this);
				p.m_Children.Add(this);
				p.IncrementVersion(VersionChangeType.Hierarchy | VersionChangeType.Layout);
			}
		}

		public void SendToBack()
		{
			VisualElement p = m_PhysicalParent;
			if (p != null && p.m_Children.Count > 1 && p.m_Children[0] != this)
			{
				p.m_Children.Remove(this);
				p.m_Children.Insert(0, this);
				p.IncrementVersion(VersionChangeType.Hierarchy | VersionChangeType.Layout);
			}
		}

		public void PlaceBehind(VisualElement sibling)
		{
			MoveNextTo(sibling, false);
		}

		public void PlaceInFront(VisualElement sibling)
		{
			MoveNextTo(sibling, true);
		}

		/// <summary>Sorts the physical children.</summary>
		public void Sort(Comparison<VisualElement> comp)
		{
			(contentContainer ?? this).hierarchy.Sort(comp);
		}

		public IEnumerator<VisualElement> GetEnumerator()
		{
			return Children().GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		/// <summary>Walks every element below this one (depth first).</summary>
		public void ForEachDescendant(Action<VisualElement> action)
		{
			if (m_Children == null)
			{
				return;
			}

			for (int i = 0; i < m_Children.Count; i++)
			{
				action(m_Children[i]);
				m_Children[i].ForEachDescendant(action);
			}
		}

		#endregion

		#region PrivateMethods

		private void MoveNextTo(VisualElement sibling, bool inFront)
		{
			if (sibling == null || sibling == this)
			{
				return;
			}

			VisualElement p = m_PhysicalParent;
			if (p == null || sibling.m_PhysicalParent != p)
			{
				throw new ArgumentException("VisualElements are not siblings");
			}

			p.m_Children.Remove(this);
			int index = p.m_Children.IndexOf(sibling);
			p.m_Children.Insert(inFront ? index + 1 : index, this);
			p.IncrementVersion(VersionChangeType.Hierarchy | VersionChangeType.Layout);
		}

		/// <summary>Attaches or detaches this subtree to a panel; sends the panel events.</summary>
		internal void SetPanel(BaseVisualElementPanel newPanel)
		{
			if (elementPanel == newPanel)
			{
				return;
			}

			BaseVisualElementPanel oldPanel = elementPanel;
			if (oldPanel != null)
			{
				using (DetachFromPanelEvent evt = DetachFromPanelEvent.GetPooled(oldPanel, newPanel))
				{
					evt.target = this;
					SendEvent(evt);
				}

				oldPanel.OnElementDetached(this);
			}

			elementPanel = newPanel;
			if (newPanel != null)
			{
				depth = m_PhysicalParent != null ? m_PhysicalParent.depth + 1 : 0;
				newPanel.OnElementAttached(this);
				dirtyFlags = VersionChangeType.None;
				IncrementVersion(VersionChangeType.Styles | VersionChangeType.Layout | VersionChangeType.Transform | VersionChangeType.Repaint | VersionChangeType.Hierarchy);
				layoutNode.dirty = true;
				layoutNode.InvalidateMeasure();
			}
			else
			{
				dirtyFlags = VersionChangeType.None;
				hasDirtyLayoutDescendant = false;
				hasDirtyTransformDescendant = false;
			}

			if (m_Children != null)
			{
				for (int i = 0; i < m_Children.Count; i++)
				{
					m_Children[i].SetPanel(newPanel);
				}
			}

			if (newPanel != null)
			{
				using (AttachToPanelEvent evt = AttachToPanelEvent.GetPooled(oldPanel, newPanel))
				{
					evt.target = this;
					SendEvent(evt);
				}
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>Direct access to an element's physical children.</summary>
		public readonly struct Hierarchy
		{
			private readonly VisualElement owner;

			internal Hierarchy(VisualElement owner)
			{
				this.owner = owner;
			}

			public VisualElement parent => owner.m_PhysicalParent;
			public int childCount => owner.m_Children?.Count ?? 0;

			public VisualElement this[int index]
			{
				get
				{
					if (owner.m_Children == null || index < 0 || index >= owner.m_Children.Count)
					{
						throw new ArgumentOutOfRangeException(nameof(index));
					}

					return owner.m_Children[index];
				}
			}

			public void Add(VisualElement child)
			{
				Insert(childCount, child);
			}

			public void Insert(int index, VisualElement child)
			{
				if (child == null)
				{
					throw new ArgumentNullException(nameof(child));
				}

				if (child == owner || child.IsAncestorOf(owner))
				{
					throw new ArgumentException("Cannot insert element in its own hierarchy");
				}

				owner.m_Children ??= new List<VisualElement>(4);
				if (child.m_PhysicalParent == owner)
				{
					int current = owner.m_Children.IndexOf(child);
					if (current == index)
					{
						return;
					}

					owner.m_Children.RemoveAt(current);
					if (current < index)
					{
						index--;
					}

					owner.m_Children.Insert(Math.Clamp(index, 0, owner.m_Children.Count), child);
					owner.IncrementVersion(VersionChangeType.Hierarchy | VersionChangeType.Layout);
					return;
				}

				child.RemoveFromHierarchy();
				index = Math.Clamp(index, 0, owner.m_Children.Count);
				owner.m_Children.Insert(index, child);
				child.m_PhysicalParent = owner;
				child.m_LogicalParent = owner;
				child.PropagateEnabled(owner.enabledInHierarchy);
				child.SetDepth(owner.depth + 1);
				child.SetPanel(owner.elementPanel);
				child.IncrementVersion(VersionChangeType.Styles | VersionChangeType.Layout | VersionChangeType.Transform | VersionChangeType.Repaint);
				owner.IncrementVersion(VersionChangeType.Hierarchy | VersionChangeType.Layout);
			}

			public void Remove(VisualElement child)
			{
				if (child == null)
				{
					throw new ArgumentNullException(nameof(child));
				}

				int index = owner.m_Children?.IndexOf(child) ?? -1;
				if (index < 0)
				{
					throw new ArgumentException("This VisualElement is not my child");
				}

				RemoveAt(index);
			}

			public void RemoveAt(int index)
			{
				VisualElement child = this[index];
				owner.m_Children.RemoveAt(index);
				child.SetPanel(null);
				child.m_PhysicalParent = null;
				child.m_LogicalParent = null;
				owner.IncrementVersion(VersionChangeType.Hierarchy | VersionChangeType.Layout);
			}

			public void Clear()
			{
				if (owner.m_Children == null)
				{
					return;
				}

				while (owner.m_Children.Count > 0)
				{
					RemoveAt(owner.m_Children.Count - 1);
				}
			}

			public int IndexOf(VisualElement element)
			{
				return owner.m_Children?.IndexOf(element) ?? -1;
			}

			public VisualElement ElementAt(int index)
			{
				return this[index];
			}

			public IEnumerable<VisualElement> Children()
			{
				if (owner.m_Children == null)
				{
					yield break;
				}

				for (int i = 0; i < owner.m_Children.Count; i++)
				{
					yield return owner.m_Children[i];
				}
			}

			public void Sort(Comparison<VisualElement> comp)
			{
				if (owner.m_Children == null || owner.m_Children.Count < 2)
				{
					return;
				}

				owner.m_Children.Sort(comp);
				owner.IncrementVersion(VersionChangeType.Hierarchy | VersionChangeType.Layout);
			}
		}

		#endregion

		#region InternalMethods

		internal void SetDepth(int value)
		{
			depth = value;
			if (m_Children != null)
			{
				for (int i = 0; i < m_Children.Count; i++)
				{
					m_Children[i].SetDepth(value + 1);
				}
			}
		}

		#endregion
	}
}
