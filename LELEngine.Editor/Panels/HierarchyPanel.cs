using System;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Scene object tree: selection, drag and drop re-parenting, create / rename / duplicate / delete.</summary>
	internal sealed class HierarchyPanel
	{
		#region PublicFields

		public bool IsFocused { get; private set; }

		/// <summary>Object being dragged from the hierarchy (drop targets elsewhere read it).</summary>
		public static GameObject Dragged { get; private set; }

		public const string DragType = "LEL_GAMEOBJECT";

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private GameObject renaming;
		private string renameText = "";
		private bool focusRename;
		private int renameAge;

		#endregion

		#region Constructors

		public HierarchyPanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public static void ResetDrag()
		{
			Dragged = null;
		}

		public void Draw()
		{
			bool visible = ImGui.Begin("Hierarchy");
			IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			if (visible)
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			Scene scene = editor.Scene;
			if (scene == null)
			{
				ImGui.TextDisabled("No scene");
				return;
			}

			ImGui.TextDisabled(scene.Name + (editor.SceneDirty ? " *" : ""));
			ImGui.Separator();

			GameObject deferredReparent = null;
			Transform deferredParent = null;
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				DrawNode(root, ref deferredReparent, ref deferredParent);
			}

			// Empty area: drop to un-parent, right click to create, click to deselect.
			NVector2 rest = ImGui.GetContentRegionAvail();
			ImGui.InvisibleButton("##hierarchy_empty", new NVector2(Math.Max(1, rest.X), Math.Max(30, rest.Y)));
			if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
			{
				editor.Select(null);
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (AcceptObject() && Dragged != null)
				{
					deferredReparent = Dragged;
					deferredParent = null;
				}

				ImGui.EndDragDropTarget();
			}

			if (ImGui.BeginPopupContextItem("##hierarchy_context"))
			{
				editor.DrawCreateMenu(null);
				ImGui.EndPopup();
			}

			if (deferredReparent != null)
			{
				Reparent(deferredReparent, deferredParent);
			}
		}

		private void DrawNode(GameObject go, ref GameObject deferredReparent, ref Transform deferredParent)
		{
			if ((go.hideFlags & HideFlags.HideInHierarchy) != 0)
			{
				return;
			}

			ImGui.PushID(go.Id.ToString());
			ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
			if (go.transform.childCount == 0)
			{
				flags |= ImGuiTreeNodeFlags.Leaf;
			}

			if (editor.Selected == go)
			{
				flags |= ImGuiTreeNodeFlags.Selected;
			}

			bool open;
			if (renaming == go)
			{
				open = ImGui.TreeNodeEx("##node", flags);
				ImGui.SameLine();
				if (focusRename)
				{
					ImGui.SetKeyboardFocusHere();
					focusRename = false;
				}

				ImGui.SetNextItemWidth(-1);
				renameAge++;
				if (ImGui.InputText("##rename", ref renameText, 256, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
				{
					CommitRename();
				}
				else if (renameAge > 2 && !ImGui.IsItemActive())
				{
					// Clicked elsewhere: keep what was typed.
					CommitRename();
				}
			}
			else
			{
				if (!go.activeInHierarchy)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Dim);
				}

				open = ImGui.TreeNodeEx(go.Name, flags);
				if (!go.activeInHierarchy)
				{
					ImGui.PopStyleColor();
				}

				if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
				{
					editor.Select(go);
				}

				if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && go.transform.childCount == 0)
				{
					editor.SceneView.FrameObject(go);
				}
			}

			if (ImGui.BeginDragDropSource())
			{
				Dragged = go;
				unsafe
				{
					ImGui.SetDragDropPayload(DragType, null, 0);
				}

				ImGui.Text(go.Name);
				ImGui.EndDragDropSource();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (AcceptObject() && Dragged != null && Dragged != go)
				{
					deferredReparent = Dragged;
					deferredParent = go.transform;
				}

				ImGui.EndDragDropTarget();
			}

			if (ImGui.BeginPopupContextItem("##object_context"))
			{
				editor.Select(go);
				if (ImGui.MenuItem("Rename", "F2"))
				{
					StartRename(go);
				}

				if (ImGui.MenuItem("Duplicate", "Ctrl+D")) editor.DuplicateSelected();
				if (ImGui.MenuItem("Delete", "Del")) editor.DeleteSelected();
				ImGui.Separator();
				if (ImGui.BeginMenu("Create Child"))
				{
					editor.DrawCreateMenu(go.transform);
					ImGui.EndMenu();
				}

				ImGui.EndPopup();
			}

			if (editor.Selected == go && IsFocused && renaming == null && ImGui.IsKeyPressed(ImGuiKey.F2, false))
			{
				StartRename(go);
			}

			if (open)
			{
				foreach (Transform child in new System.Collections.Generic.List<Transform>(go.transform.Children))
				{
					DrawNode(child.gameObject, ref deferredReparent, ref deferredParent);
				}

				ImGui.TreePop();
			}

			ImGui.PopID();
		}

		private static unsafe bool AcceptObject()
		{
			ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(DragType);
			return !payload.IsNull;
		}

		private void Reparent(GameObject child, Transform parent)
		{
			if (parent != null && (parent == child.transform || parent.IsChildOf(child.transform)))
			{
				return;
			}

			if (child.transform.parent == parent)
			{
				return;
			}

			editor.RecordStructural("Re-parent " + child.Name, () => child.transform.SetParent(parent, true));
		}

		private void StartRename(GameObject go)
		{
			renaming = go;
			renameText = go.Name;
			focusRename = true;
			renameAge = 0;
		}

		private void CommitRename()
		{
			GameObject target = renaming;
			renaming = null;
			if (target == null || string.IsNullOrWhiteSpace(renameText) || renameText == target.Name)
			{
				return;
			}

			editor.Undo.Push(new GameObjectStateRecord
			{
				Name = "Rename " + target.Name,
				ObjectId = target.Id,
				NameBefore = target.Name,
				NameAfter = renameText,
				ActiveBefore = target.activeSelf,
				ActiveAfter = target.activeSelf
			});
			target.Name = renameText;
			editor.MarkDirty();
		}

		#endregion
	}
}
