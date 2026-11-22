using System;
using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>
	///     Moving objects in the hierarchy: under another object, and among their siblings (the hierarchy's drag and drop,
	///     automation's reparent and reorder). The order is the scene's: saved with it and kept by undo.
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PublicMethods

		/// <summary>
		///     Moves <paramref name="objects" /> under <paramref name="parent" /> (null: the scene's roots; in prefab mode the
		///     prefab's root), keeping their places in the world, and stands them together, in the order given, before
		///     <paramref name="before" /> (null, or not a child of the parent: after its last child); one undo step. With
		///     <paramref name="reorder" /> false the objects already under the parent stay where they are (a drop on a row
		///     re-parents only). What cannot move is left out and <paramref name="refuse" /> is told why (default: a
		///     warning in the console). False when nothing changed.
		/// </summary>
		public bool MoveObjects(IReadOnlyList<GameObject> objects, Transform parent, Transform before, bool reorder, Action<string> refuse = null)
		{
			refuse ??= reason => Debug.LogWarning("[Editor] " + reason);
			if (Scene == null)
			{
				return false;
			}

			if (Stage != null && parent == null)
			{
				// A prefab has one root: what would stand beside it goes under it.
				parent = StageRoot?.transform;
			}

			if (parent != null && TempObjects.IsReadOnly(parent.gameObject))
			{
				refuse($"'{parent.gameObject.Name}' is not editable: nothing goes under it (a script keeps it).");
				return false;
			}

			var moving = new List<GameObject>();
			foreach (GameObject go in objects)
			{
				if (go == null || go.dead || moving.Contains(go))
				{
					continue;
				}

				if (Stage != null && go == StageRoot)
				{
					refuse("The root of the edited prefab stays the root.");
					continue;
				}

				// Into itself: the drop lands on its own subtree.
				if (parent != null && (parent == go.transform || parent.IsChildOf(go.transform)))
				{
					continue;
				}

				if (go.prefabInstance != null && go.prefabInstance.Root != go)
				{
					refuse($"'{go.Name}' is part of the prefab instance '{go.prefabInstance.Root?.Name}': its objects stay where the prefab has them (move the instance's root, or unpack it).");
					continue;
				}

				if (TempObjects.IsReadOnly(go))
				{
					refuse($"'{go.Name}' is not editable: a script keeps it.");
					continue;
				}

				if (!reorder && go.transform.parent == parent)
				{
					continue;
				}

				moving.Add(go);
			}

			if (moving.Count == 0)
			{
				return false;
			}

			List<Transform> siblings = Siblings(parent);
			before = ResolvePlace(parent, before, moving, refuse);

			// Where they would stand: nothing to do when they stand there already.
			var order = new List<Transform>(siblings);
			order.RemoveAll(child => moving.Contains(child.gameObject));
			order.InsertRange(before != null ? order.IndexOf(before) : order.Count, moving.ConvertAll(go => go.transform));
			bool reparenting = moving.Exists(go => go.transform.parent != parent);
			if (!reparenting && SameOrder(order, siblings))
			{
				return false;
			}

			string what = moving.Count == 1 ? moving[0].Name : moving.Count + " objects";
			RecordStructural((reparenting ? "Re-parent " : "Move ") + what, () =>
			{
				foreach (GameObject go in moving)
				{
					go.transform.SetParent(parent, true);
				}

				foreach (GameObject go in moving)
				{
					PlaceBefore(go.transform, before);
				}
			});
			return true;
		}

		/// <summary>The sibling after <paramref name="transform" /> (a root's: the next root), null for the last.</summary>
		public Transform NextSibling(Transform transform)
		{
			List<Transform> siblings = Siblings(transform.parent);
			int at = siblings.IndexOf(transform);
			return at >= 0 && at + 1 < siblings.Count ? siblings[at + 1] : null;
		}

		/// <summary>The children of <paramref name="parent" />, or the scene's roots for null.</summary>
		public List<Transform> Siblings(Transform parent)
		{
			if (parent != null)
			{
				return new List<Transform>(parent.Children);
			}

			var roots = new List<Transform>();
			if (Scene != null)
			{
				foreach (GameObject root in Scene.GetRootGameObjects())
				{
					roots.Add(root.transform);
				}
			}

			return roots;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     Where an object going under <paramref name="parent" /> in front of <paramref name="before" /> (null: after the
		///     last child) can stand: in front of <paramref name="before" />, or of the first sibling after it that is not
		///     <paramref name="moving" />; under an object of a prefab instance not among the prefab's own children (then
		///     <paramref name="refuse" /> is told). Null: after the last child.
		/// </summary>
		private Transform ResolvePlace(Transform parent, Transform before, List<GameObject> moving, Action<string> refuse)
		{
			List<Transform> siblings = Siblings(parent);
			before = FirstStaying(siblings, before != null ? siblings.IndexOf(before) : -1, moving);

			// Under a prefab instance's object the prefab's own children come first: a saved scene makes the instance's
			// objects, then puts the added ones under them, so an added object between the prefab's would not stay there.
			PrefabInstance instance = parent?.gameObject.prefabInstance;
			if (instance != null && before != null)
			{
				int lastOwn = siblings.FindLastIndex(child => child.gameObject.prefabInstance == instance);
				if (lastOwn >= siblings.IndexOf(before))
				{
					before = FirstStaying(siblings, lastOwn + 1, moving);
					refuse?.Invoke($"Objects added to the prefab instance '{instance.Root?.Name}' stand after the prefab's own objects (the prefab keeps their order).");
				}
			}

			return before;
		}

		/// <summary>Moves <paramref name="transform" /> in front of its sibling <paramref name="before" /> (null: after the last).</summary>
		private static void PlaceBefore(Transform transform, Transform before)
		{
			if (before == null)
			{
				transform.SetAsLastSibling();
				return;
			}

			// The place of the object it goes in front of, once it is out of the list.
			int index = before.GetSiblingIndex();
			if (transform.GetSiblingIndex() < index)
			{
				index--;
			}

			transform.SetSiblingIndex(index);
		}

		/// <summary>The first of <paramref name="siblings" /> from <paramref name="start" /> on that is not moving (null: none, or no start).</summary>
		private static Transform FirstStaying(List<Transform> siblings, int start, List<GameObject> moving)
		{
			if (start < 0)
			{
				return null;
			}

			for (int i = start; i < siblings.Count; i++)
			{
				if (!moving.Contains(siblings[i].gameObject))
				{
					return siblings[i];
				}
			}

			return null;
		}

		private static bool SameOrder(List<Transform> a, List<Transform> b)
		{
			if (a.Count != b.Count)
			{
				return false;
			}

			for (int i = 0; i < a.Count; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}

			return true;
		}

		#endregion
	}
}
