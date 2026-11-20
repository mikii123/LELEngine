using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     An object's static flag (<see cref="GameObject.isStatic" />) and layer changed from the editor (the inspector's
	///     header, automation). An object with children asks first, as Unity does: change the children too, this object
	///     only, or nothing (the user's ask, 2026-11-20). Either way the change is one undo step.
	/// </summary>
	internal sealed partial class EditorApplication
	{
		#region PrivateFields

		// The question waiting for an answer: the object, and what it is to be set to (a layer, or the static flag).
		private GameObject flagObject;
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
			if (go == null || go.isStatic == value && !HasChildren(go))
			{
				return;
			}

			if (!HasChildren(go))
			{
				SetStatic(go, value, false);
				return;
			}

			flagObject = go;
			flagIsLayer = false;
			flagStatic = value;
			openFlagPopup = true;
		}

		/// <summary>Sets the object's layer as the inspector's drop-down does: at once without children, else after the question.</summary>
		public void RequestLayer(GameObject go, int layer)
		{
			if (go == null || go.layer == layer && !HasChildren(go))
			{
				return;
			}

			if (!HasChildren(go))
			{
				SetLayer(go, layer, false);
				return;
			}

			flagObject = go;
			flagIsLayer = true;
			flagLayer = layer;
			openFlagPopup = true;
		}

		/// <summary>
		///     Sets the static flag of an object, with <paramref name="children" /> of everything under it too (objects a
		///     script keeps read-only are left as they are); one undo step. Returns how many objects changed.
		/// </summary>
		public int SetStatic(GameObject go, bool value, bool children)
		{
			List<GameObject> targets = FlagTargets(go, children, target => target.isStatic != value);
			if (targets.Count == 0)
			{
				return 0;
			}

			string what = value ? "Make " : "Make not static: ";
			RecordStructural(what + Counted(go, targets.Count) + (value ? " static" : ""), () =>
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
			layer = Math.Clamp(layer, 0, 31);
			List<GameObject> targets = FlagTargets(go, children, target => target.layer != layer);
			if (targets.Count == 0)
			{
				return 0;
			}

			RecordStructural("Change layer of " + Counted(go, targets.Count) + " to " + LayerMask.DisplayName(layer), () =>
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

		// The object and, with children, everything under it that is editable and would change.
		private static List<GameObject> FlagTargets(GameObject go, bool children, Func<GameObject, bool> changes)
		{
			var targets = new List<GameObject>();
			if (go == null || go.dead)
			{
				return targets;
			}

			void Visit(GameObject current, bool root)
			{
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

			Visit(go, true);
			return targets;
		}

		private static string Counted(GameObject go, int count)
		{
			return count == 1 ? go.Name : go.Name + " and its children (" + count + " objects)";
		}

		/// <summary>The question of a static flag or layer set on an object with children (drawn with the editor's modals).</summary>
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

			GameObject go = flagObject;
			if (go == null || go.dead || flagIsLayer != layer)
			{
				flagObject = null;
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.TextUnformatted(layer
				? $"Do you want to set the layer to {LayerMask.DisplayName(flagLayer)} for all the child objects of '{go.Name}' as well?"
				: flagStatic
					? $"Do you want to make all the child objects of '{go.Name}' static as well?"
					: $"Do you want to make all the child objects of '{go.Name}' not static as well?");
			ImGui.Spacing();
			bool? children = null;
			if (EditorGui.Button("Yes, change children", new NVector2(170, 0)))
			{
				children = true;
			}

			ImGui.SameLine();
			if (EditorGui.Button("No, this object only", new NVector2(170, 0)))
			{
				children = false;
			}

			ImGui.SameLine();
			bool cancel = EditorGui.Button("Cancel", new NVector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape);
			if (children.HasValue || cancel)
			{
				ImGui.CloseCurrentPopup();
				flagObject = null;
				if (children.HasValue)
				{
					// After the frame's panels: the change replaces nothing they hold, but it snapshots the scene.
					bool withChildren = children.Value;
					bool value = flagStatic;
					int newLayer = flagLayer;
					Defer(() =>
					{
						if (go.dead)
						{
							return;
						}

						if (layer)
						{
							SetLayer(go, newLayer, withChildren);
						}
						else
						{
							SetStatic(go, value, withChildren);
						}
					});
				}
			}

			ImGui.EndPopup();
		}

		#endregion
	}
}
