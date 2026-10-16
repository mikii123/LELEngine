using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	public partial class VisualElement
	{
		#region PublicMethods

		/// <summary>First descendant (depth first) with this name and / or class, or null.</summary>
		public VisualElement Q(string name = null, string className = null)
		{
			return Q<VisualElement>(name, className);
		}

		public T Q<T>(string name = null, string className = null)
			where T : VisualElement
		{
			if (name == null && className == null && this is T self)
			{
				// Unity returns the element itself when it matches and no filter is given.
				return self;
			}

			return FindFirst<T>(this, name, className);
		}

		public VisualElement Q(string name, params string[] classes)
		{
			return Query<VisualElement>(name, classes).First();
		}

		public UQueryBuilder<VisualElement> Query(string name = null, string className = null)
		{
			return Query<VisualElement>(name, className);
		}

		public UQueryBuilder<T> Query<T>(string name = null, string className = null)
			where T : VisualElement
		{
			var builder = new UQueryBuilder<T>(this);
			if (name != null) builder = builder.Name(name);
			if (className != null) builder = builder.Class(className);
			return builder;
		}

		public UQueryBuilder<T> Query<T>(string name, params string[] classes)
			where T : VisualElement
		{
			var builder = new UQueryBuilder<T>(this);
			if (name != null) builder = builder.Name(name);
			foreach (string c in classes)
			{
				builder = builder.Class(c);
			}

			return builder;
		}

		/// <summary>The nearest ancestor of the type (or this element when it matches).</summary>
		public T GetFirstAncestorOfType<T>()
			where T : class
		{
			for (VisualElement e = m_PhysicalParent; e != null; e = e.m_PhysicalParent)
			{
				if (e is T typed)
				{
					return typed;
				}
			}

			return null;
		}

		public T GetFirstOfType<T>()
			where T : class
		{
			for (VisualElement e = this; e != null; e = e.m_PhysicalParent)
			{
				if (e is T typed)
				{
					return typed;
				}
			}

			return null;
		}

		#endregion

		#region PrivateMethods

		private static T FindFirst<T>(VisualElement root, string name, string className)
			where T : VisualElement
		{
			if (root.m_Children == null)
			{
				return null;
			}

			for (int i = 0; i < root.m_Children.Count; i++)
			{
				VisualElement child = root.m_Children[i];
				if (child is T typed && (name == null || string.Equals(child.name, name, StringComparison.Ordinal)) && (className == null || child.ClassListContains(className)))
				{
					return typed;
				}

				T inner = FindFirst<T>(child, name, className);
				if (inner != null)
				{
					return inner;
				}
			}

			return null;
		}

		#endregion
	}

	/// <summary>A query over the descendants of an element (UQuery): filters by type, name, classes and predicates.</summary>
	public struct UQueryBuilder<T>
		where T : VisualElement
	{
		#region PrivateFields

		private readonly VisualElement root;
		private List<Func<VisualElement, bool>> filters;
		private bool directChildrenOnly;

		#endregion

		#region Constructors

		public UQueryBuilder(VisualElement root)
		{
			this.root = root;
			filters = null;
			directChildrenOnly = false;
		}

		#endregion

		#region PublicMethods

		public UQueryBuilder<T> Name(string name)
		{
			return Where(e => string.Equals(e.name, name, StringComparison.Ordinal));
		}

		public UQueryBuilder<T> Class(string className)
		{
			return Where(e => e.ClassListContains(className));
		}

		public UQueryBuilder<T> Where(Func<T, bool> predicate)
		{
			Func<VisualElement, bool> filter = e => e is T typed && predicate(typed);
			return Where(filter);
		}

		public UQueryBuilder<T> Children()
		{
			directChildrenOnly = true;
			return this;
		}

		public UQueryBuilder<T> Descendents()
		{
			directChildrenOnly = false;
			return this;
		}

		public UQueryBuilder<T> Visible()
		{
			return Where(e => e.visible);
		}

		public UQueryBuilder<T> Focused()
		{
			return Where(e => (e.pseudoStates & PseudoStates.Focus) != 0);
		}

		public UQueryBuilder<T> Hovered()
		{
			return Where(e => (e.pseudoStates & PseudoStates.Hover) != 0);
		}

		public UQueryBuilder<T> Enabled()
		{
			return Where(e => e.enabledInHierarchy);
		}

		public UQueryBuilder<T> OfType<TOther>()
			where TOther : VisualElement
		{
			return Where(e => e is TOther);
		}

		public UQueryState<T> Build()
		{
			return new UQueryState<T>(ToList());
		}

		public T First()
		{
			var results = new List<T>(1);
			Collect(root, results, true);
			return results.Count > 0 ? results[0] : null;
		}

		public T Last()
		{
			List<T> results = ToList();
			return results.Count > 0 ? results[results.Count - 1] : null;
		}

		public T AtIndex(int index)
		{
			List<T> results = ToList();
			return index >= 0 && index < results.Count ? results[index] : null;
		}

		public List<T> ToList()
		{
			var results = new List<T>();
			Collect(root, results, false);
			return results;
		}

		public void ToList(List<T> results)
		{
			Collect(root, results, false);
		}

		public void ForEach(Action<T> action)
		{
			foreach (T element in ToList())
			{
				action(element);
			}
		}

		public List<TResult> ForEach<TResult>(Func<T, TResult> function)
		{
			var results = new List<TResult>();
			foreach (T element in ToList())
			{
				results.Add(function(element));
			}

			return results;
		}

		#endregion

		#region PrivateMethods

		private UQueryBuilder<T> Where(Func<VisualElement, bool> filter)
		{
			filters ??= new List<Func<VisualElement, bool>>();
			filters.Add(filter);
			return this;
		}

		private void Collect(VisualElement element, List<T> results, bool firstOnly)
		{
			if (element.m_Children == null)
			{
				return;
			}

			for (int i = 0; i < element.m_Children.Count; i++)
			{
				VisualElement child = element.m_Children[i];
				if (child is T typed && Matches(child))
				{
					results.Add(typed);
					if (firstOnly)
					{
						return;
					}
				}

				if (!directChildrenOnly)
				{
					Collect(child, results, firstOnly);
					if (firstOnly && results.Count > 0)
					{
						return;
					}
				}
			}
		}

		private bool Matches(VisualElement element)
		{
			if (filters == null)
			{
				return true;
			}

			foreach (Func<VisualElement, bool> filter in filters)
			{
				if (!filter(element))
				{
					return false;
				}
			}

			return true;
		}

		#endregion
	}

	/// <summary>A built query result (a snapshot list).</summary>
	public readonly struct UQueryState<T>
		where T : VisualElement
	{
		private readonly List<T> results;

		internal UQueryState(List<T> results)
		{
			this.results = results;
		}

		public T First() => results.Count > 0 ? results[0] : null;
		public T Last() => results.Count > 0 ? results[results.Count - 1] : null;
		public List<T> ToList() => new List<T>(results);

		public void ForEach(Action<T> action)
		{
			foreach (T element in results)
			{
				action(element);
			}
		}
	}
}
