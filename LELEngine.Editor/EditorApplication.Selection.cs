using System;
using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>
	///     The selection of several GameObjects (Ctrl / Shift clicks and the keys in the hierarchy, clicks in the scene view)
	///     or of several project files (the project panel). One of them is the active one, <see cref="Selected" /> /
	///     <see cref="SelectedAsset" />: the transform gizmo sits on it and the inspector draws from it; the inspector edits
	///     all of them together (multi-object editing, <see cref="MultiEdit" />). What acts on the whole selection (delete,
	///     duplicate, re-parent, the gizmo, tools like the Mesh Baker) reads <see cref="SelectedObjects" />,
	///     <see cref="SelectedRoots" /> or <see cref="SelectedAssets" />.
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PublicFields

		/// <summary>
		///     Every selected GameObject in the order they were selected, the active one (<see cref="Selected" />) among
		///     them; destroyed objects are left out. Empty when nothing (or a project file) is selected.
		/// </summary>
		public IReadOnlyList<GameObject> SelectedObjects
		{
			get
			{
				PruneSelection();
				return selection;
			}
		}

		/// <summary>
		///     Every selected project file, the active one (<see cref="SelectedAsset" />) among them (the project panel gives
		///     them in its rows' order). Empty when GameObjects (or nothing) are selected.
		/// </summary>
		public IReadOnlyList<string> SelectedAssets => selectedAssets;

		#endregion

		#region PrivateFields

		private readonly List<string> selectedAssets = new List<string>();
		private readonly List<GameObject> selection = new List<GameObject>();
		private readonly HashSet<GameObject> selectionSet = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);

		#endregion

		#region PublicMethods

		/// <summary>The object is selected (the active one or one of the others).</summary>
		public bool IsSelected(GameObject go)
		{
			return go != null && !go.dead && selectionSet.Contains(go);
		}

		/// <summary>
		///     Adds the object to the selection, or takes it out when it is in it (a Ctrl click). The object added becomes
		///     the active one; taking the active one out makes the one selected before it active.
		/// </summary>
		public void ToggleSelected(GameObject go)
		{
			if (go == null || go.dead)
			{
				return;
			}

			PruneSelection();
			Edits.Commit();
			if (selectionSet.Remove(go))
			{
				selection.Remove(go);
				if (selected == go)
				{
					selected = selection.Count > 0 ? selection[selection.Count - 1] : null;
				}

				return;
			}

			selection.Add(go);
			selectionSet.Add(go);
			selected = go;
			DeselectAsset();
		}

		/// <summary>
		///     Selects these objects and no others. The active one is <paramref name="active" /> when it is among them, else
		///     the last.
		/// </summary>
		public void SelectObjects(IEnumerable<GameObject> objects, GameObject active = null)
		{
			var chosen = new List<GameObject>();
			var seen = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);
			foreach (GameObject go in objects ?? Array.Empty<GameObject>())
			{
				if (go != null && !go.dead && seen.Add(go))
				{
					chosen.Add(go);
				}
			}

			GameObject newActive = chosen.Count == 0 ? null : active != null && seen.Contains(active) ? active : chosen[chosen.Count - 1];
			if (newActive != Selected)
			{
				Edits.Commit();
			}

			selection.Clear();
			selectionSet.Clear();
			selection.AddRange(chosen);
			selectionSet.UnionWith(chosen);
			selected = newActive;
			if (newActive != null)
			{
				DeselectAsset();
			}
		}

		/// <summary>
		///     The selected objects none of whose parents is selected, in the hierarchy's order: what an action on the
		///     selection takes (a child selected with its parent goes where the parent goes).
		/// </summary>
		public List<GameObject> SelectedRoots()
		{
			PruneSelection();
			var roots = new List<GameObject>();
			foreach (GameObject go in selection)
			{
				bool under = false;
				for (Transform parent = go.transform.parent; parent != null && !under; parent = parent.parent)
				{
					under = selectionSet.Contains(parent.gameObject);
				}

				if (!under)
				{
					roots.Add(go);
				}
			}

			if (roots.Count > 1)
			{
				// As the hierarchy lists them, whatever the order of the clicks.
				var order = new Dictionary<GameObject, int>(ReferenceEqualityComparer.Instance);
				foreach (GameObject root in roots)
				{
					order[root] = 0;
				}

				int next = 0;
				foreach (GameObject top in roots[0].scene?.GetRootGameObjects() ?? new List<GameObject>())
				{
					NumberInOrder(top.transform, order, ref next);
				}

				roots.Sort((a, b) => order[a].CompareTo(order[b]));
			}

			return roots;
		}

		#endregion

		#region InternalMethods

		/// <summary>The selection as ids, the active object's first: to find it again in a scene built from a snapshot.</summary>
		internal ulong[] CaptureSelection()
		{
			PruneSelection();
			if (selected == null)
			{
				return Array.Empty<ulong>();
			}

			var ids = new ulong[selection.Count];
			int next = 0;
			ids[next++] = selected.Id;
			foreach (GameObject go in selection)
			{
				if (go != selected)
				{
					ids[next++] = go.Id;
				}
			}

			return ids;
		}

		/// <summary>Selects the objects of <paramref name="scene" /> with these ids (<see cref="CaptureSelection" />: the first is the active one).</summary>
		internal void RestoreSelection(Scene scene, ulong[] ids)
		{
			selection.Clear();
			selectionSet.Clear();
			selected = null;
			if (scene == null || ids == null)
			{
				return;
			}

			foreach (ulong id in ids)
			{
				GameObject go = id != 0 ? scene.FindObjectById(id) : null;
				if (go != null && !go.dead && selectionSet.Add(go))
				{
					selection.Add(go);
					selected ??= go;
				}
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>One object selected, or none.</summary>
		private void SetSelection(GameObject single)
		{
			selection.Clear();
			selectionSet.Clear();
			selected = single;
			if (single != null)
			{
				selection.Add(single);
				selectionSet.Add(single);
			}
		}

		/// <summary>Destroyed objects leave the selection (and, with <paramref name="unloaded" />, those whose scene is gone); the active one passes to the one selected before it.</summary>
		private void PruneSelection(bool unloaded = false)
		{
			bool gone = false;
			foreach (GameObject go in selection)
			{
				if (go.dead || unloaded && go.scene == null)
				{
					gone = true;
					break;
				}
			}

			if (!gone)
			{
				return;
			}

			selection.RemoveAll(go => go.dead || unloaded && go.scene == null);
			selectionSet.Clear();
			selectionSet.UnionWith(selection);
			if (selected != null && !selectionSet.Contains(selected))
			{
				selected = selection.Count > 0 ? selection[selection.Count - 1] : null;
			}
		}

		/// <summary>Files or folders moved: the selected files follow (<paramref name="moved" /> maps a path).</summary>
		private void MoveSelectedAssets(Func<string, string> moved)
		{
			string active = selectedAsset != null ? moved(selectedAsset) : null;
			for (int i = 0; i < selectedAssets.Count; i++)
			{
				selectedAssets[i] = moved(selectedAssets[i]);
			}

			selectedAsset = active;
		}

		/// <summary>A GameObject was selected: the project file the inspector showed is not any more (as in Unity).</summary>
		private void DeselectAsset()
		{
			if (SelectedAsset != null)
			{
				SelectedAsset = null;
				AssetInspectors.Deselected();
			}
		}

		private static void NumberInOrder(Transform transform, Dictionary<GameObject, int> order, ref int next)
		{
			if (order.ContainsKey(transform.gameObject))
			{
				// Nothing under a root is a root.
				order[transform.gameObject] = next++;
				return;
			}

			foreach (Transform child in transform.Children)
			{
				NumberInOrder(child, order, ref next);
			}
		}

		#endregion
	}
}
