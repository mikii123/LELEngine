using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     An object's static flag (<see cref="GameObject.isStatic" />) and layer changed from the editor (the inspector's
	///     header, automation), of one object or of several selected together. Objects with children ask first, as Unity
	///     does: change the children too, these objects only, or nothing (the user's ask, 2026-11-20). Either way the change
	///     is one undo step.
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PrivateFields

		// The question waiting for an answer: the objects, and what they are to be set to (a layer, or the static flag).
		private readonly List<GameObject> flagObjects = new List<GameObject>();
		private bool flagIsLayer;
		private bool flagStatic;
		private int flagLayer;
		private bool openFlagPopup;

		private const string StaticPopup = "Change Static Flag";
		private const string LayerPopup = "Change Layer";

		#endregion

		#region PublicMethods

		/// <summary>
		///     Sets the object's static flag as the inspector's checkbox does: at once when it has no children, else after
		///     the question (children too / this object only / cancel).
		/// </summary>
		public void RequestStatic(GameObject go, bool value)
		{
			RequestStatic(new[] { go }, value);
		}

		/// <summary>The static flag of several objects (one undo step); the question comes when one of them has children.</summary>
		public void RequestStatic(IReadOnlyList<GameObject> objects, bool value)
		{
			List<GameObject> live = Live(objects);
			if (live.Count == 0 || !live.Exists(HasChildren) && !live.Exists(go => go.isStatic != value))
			{
				return;
			}

			if (!live.Exists(HasChildren))
			{
				SetStatic(live, value, false);
				return;
			}

			Ask(live, false);
			flagStatic = value;
		}

		/// <summary>Sets the object's layer as the inspector's drop-down does: at once without children, else after the question.</summary>
		public void RequestLayer(GameObject go, int layer)
		{
			RequestLayer(new[] { go }, layer);
		}

		/// <summary>The layer of several objects (one undo step); the question comes when one of them has children.</summary>
		public void RequestLayer(IReadOnlyList<GameObject> objects, int layer)
		{
			List<GameObject> live = Live(objects);
			if (live.Count == 0 || !live.Exists(HasChildren) && !live.Exists(go => go.layer != layer))
			{
				return;
			}

			if (!live.Exists(HasChildren))
			{
				SetLayer(live, layer, false);
				return;
			}

			Ask(live, true);
			flagLayer = layer;
		}

		/// <summary>
		///     Sets the static flag of an object, with <paramref name="children" /> of everything under it too (objects a
		///     script keeps read-only are left as they are); one undo step. Returns how many objects changed.
		/// </summary>
		public int SetStatic(GameObject go, bool value, bool children)
		{
			return SetStatic(new[] { go }, value, children);
		}

		/// <summary>The static flag of several objects (and with <paramref name="children" /> everything under them); one undo step.</summary>
		public int SetStatic(IReadOnlyList<GameObject> objects, bool value, bool children)
		{
			List<GameObject> targets = FlagTargets(objects, children, target => target.isStatic != value);
			if (targets.Count == 0)
			{
				return 0;
			}

			string what = value ? "Make " : "Make not static: ";
			RecordStructural(what + Counted(objects, targets.Count) + (value ? " static" : ""), () =>
			{
				foreach (GameObject target in targets)
				{
					target.isStatic = value;
				}
			});
			return targets.Count;
		}

		/// <summary>Sets the layer of an object, with <paramref name="children" /> of everything under it too; one undo step. Returns how many objects changed.</summary>
		public int SetLayer(GameObject go, int layer, bool children)
		{
			return SetLayer(new[] { go }, layer, children);
		}

		/// <summary>The layer of several objects (and with <paramref name="children" /> everything under them); one undo step.</summary>
		public int SetLayer(IReadOnlyList<GameObject> objects, int layer, bool children)
		{
			layer = Math.Clamp(layer, 0, 31);
			List<GameObject> targets = FlagTargets(objects, children, target => target.layer != layer);
			if (targets.Count == 0)
			{
				return 0;
			}

			RecordStructural("Change layer of " + Counted(objects, targets.Count) + " to " + LayerMask.DisplayName(layer), () =>
			{
				foreach (GameObject target in targets)
				{
					target.layer = layer;
				}
			});
			return targets.Count;
		}

		#endregion

		#region PrivateMethods

		private static bool HasChildren(GameObject go)
		{
			return go.transform != null && go.transform.childCount > 0;
		}

		private static List<GameObject> Live(IReadOnlyList<GameObject> objects)
		{
			var live = new List<GameObject>();
			foreach (GameObject go in objects ?? Array.Empty<GameObject>())
			{
				if (go != null && !go.dead && !live.Contains(go))
				{
					live.Add(go);
				}
			}

			return live;
		}

		private void Ask(List<GameObject> objects, bool layer)
		{
			flagObjects.Clear();
			flagObjects.AddRange(objects);
			flagIsLayer = layer;
			openFlagPopup = true;
		}

		// The objects and, with children, everything under them that is editable and would change (each once).
		private static List<GameObject> FlagTargets(IReadOnlyList<GameObject> objects, bool children, Func<GameObject, bool> changes)
		{
			var targets = new List<GameObject>();
			var seen = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);

			void Visit(GameObject current, bool root)
			{
				if (!seen.Add(current))
				{
					return;
				}

				if ((root || !TempObjects.IsReadOnly(current)) && changes(current))
				{
					targets.Add(current);
				}

				if (!children || current.transform == null)
				{
					return;
				}

				foreach (Transform child in current.transform.Children)
				{
					if (!child.gameObject.dead)
					{
						Visit(child.gameObject, false);
					}
				}
			}

			foreach (GameObject go in Live(objects))
			{
				Visit(go, true);
			}

			return targets;
		}

		private static string Counted(IReadOnlyList<GameObject> objects, int count)
		{
			GameObject first = objects.Count > 0 ? objects[0] : null;
			if (objects.Count == 1)
			{
				return count == 1 ? first?.Name : first?.Name + " and its children (" + count + " objects)";
			}

			return count + " objects";
		}

		/// <summary>The question of a static flag or layer set on objects with children (drawn with the editor's modals).</summary>
		private void DrawObjectFlagModal()
		{
			if (openFlagPopup)
			{
				openFlagPopup = false;
				ImGui.OpenPopup(flagIsLayer ? LayerPopup : StaticPopup);
			}

			DrawObjectFlagModal(StaticPopup, false);
			DrawObjectFlagModal(LayerPopup, true);
		}

		private void DrawObjectFlagModal(string title, bool layer)
		{
			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (!ImGui.BeginPopupModal(title, ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			List<GameObject> objects = Live(flagObjects);
			if (objects.Count == 0 || flagIsLayer != layer)
			{
				flagObjects.Clear();
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			string whose = objects.Count == 1 ? $"'{objects[0].Name}'" : $"the {objects.Count} selected objects";
			ImGui.TextUnformatted(layer
				? $"Do you want to set the layer to {LayerMask.DisplayName(flagLayer)} for all the child objects of {whose} as well?"
				: flagStatic
					? $"Do you want to make all the child objects of {whose} static as well?"
					: $"Do you want to make all the child objects of {whose} not static as well?");
			ImGui.Spacing();
			bool? children = null;
			if (EditorGui.Button("Yes, change children", new NVector2(170, 0)))
			{
				children = true;
			}

			ImGui.SameLine();
			if (EditorGui.Button(objects.Count == 1 ? "No, this object only" : "No, these objects only", new NVector2(170, 0)))
			{
				children = false;
			}

			ImGui.SameLine();
			bool cancel = EditorGui.Button("Cancel", new NVector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape);
			if (children.HasValue || cancel)
			{
				ImGui.CloseCurrentPopup();
				flagObjects.Clear();
				if (children.HasValue)
				{
					// After the frame's panels: the change replaces nothing they hold, but it snapshots the scene.
					bool withChildren = children.Value;
					bool value = flagStatic;
					int newLayer = flagLayer;
					Defer(() =>
					{
						if (layer)
						{
							SetLayer(objects, newLayer, withChildren);
						}
						else
						{
							SetStatic(objects, value, withChildren);
						}
					});
				}
			}

			ImGui.EndPopup();
		}

		#endregion
	}
}
