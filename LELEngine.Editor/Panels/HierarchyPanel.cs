using System;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Scene object tree: selection, drag and drop re-parenting, create / rename / duplicate / delete. Prefab
	///     instances show as in Unity: their objects' names in blue (red when the prefab is missing), a prefab icon on
	///     instance roots and the roots of nested prefabs (variants marked), a "+" on objects added to an instance, and
	///     an arrow at the end of a root's row that opens its prefab. In prefab mode a breadcrumb leads back.
	/// </summary>
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

		// A model or prefab file dropped from the project panel this frame, and the object it goes under.
		private string droppedAsset;
		private Transform droppedAssetParent;

		// The row the left button went down on (it is selected when the button comes up without a drag).
		private GameObject pressed;

		// The object an object field pinged: its parents unfold, the row scrolls into view and flashes.
		private GameObject pinged;
		private double pingTime;
		private bool revealPinged;
		private const double PingSeconds = 1.6;

		// A prefab to open after the tree is drawn (the arrow at the end of a row).
		private string openPrefab;

		// Leading spaces of a row's label: room for its icon.
		private string iconPadding = "";
		private float iconPaddingFont;

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

		/// <summary>Drops the scene objects the panel remembers (before the scene or the scripts unload).</summary>
		public void ForgetObjects()
		{
			pinged = null;
			pressed = null;
			renaming = null;
		}

		/// <summary>Shows the object: its parents unfold, its row scrolls into view and flashes for a moment.</summary>
		public void Ping(GameObject go)
		{
			pinged = go;
			pingTime = ImGui.GetTime();
			revealPinged = true;
		}

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open = true;

		public void Draw()
		{
			if (!Open)
			{
				IsFocused = false;
				return;
			}

			bool visible = ImGui.Begin("Hierarchy", ref Open);
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

			if (editor.Stage != null)
			{
				DrawStageHeader();
			}
			else
			{
				ImGui.TextDisabled(scene.Name + (editor.SceneDirty ? " *" : ""));
			}

			ImGui.Separator();

			float space = ImGui.CalcTextSize(" ").X;
			if (iconPaddingFont != ImGui.GetFontSize())
			{
				iconPaddingFont = ImGui.GetFontSize();
				iconPadding = new string(' ', (int)Math.Ceiling((ImGui.GetTextLineHeight() + 5f) / Math.Max(1f, space)));
			}

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

				AcceptAsset(null);
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

			if (droppedAsset != null)
			{
				if (droppedAsset.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
				{
					editor.InstantiatePrefab(droppedAsset, droppedAssetParent);
				}
				else
				{
					editor.InstantiateModel(droppedAsset, droppedAssetParent);
				}

				droppedAsset = null;
				droppedAssetParent = null;
			}

			if (openPrefab != null)
			{
				string path = openPrefab;
				openPrefab = null;
				editor.OpenPrefab(path);
			}
		}

		/// <summary>Prefab mode: the way back (the scene, then each prefab opened), saving, and automatic saving.</summary>
		private void DrawStageHeader()
		{
			PrefabStage stage = editor.Stage;
			var frames = editor.StageFrames;
			if (ImGui.SmallButton("<##stage_back"))
			{
				editor.ClosePrefabStage();
				return;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Back to " + frames[frames.Count - 1].Name);
			}

			ImGui.SameLine();
			for (int i = 0; i <= frames.Count; i++)
			{
				string name = i == 0 ? frames[0].Name : frames[i - 1].Stage.Name;
				bool current = i == frames.Count;
				if (i > 0)
				{
					ImGui.SameLine(0f, 2f);
					ImGui.TextDisabled(">");
					ImGui.SameLine(0f, 2f);
				}

				if (current)
				{
					NVector2 start = ImGui.GetCursorScreenPos();
					float icon = ImGui.GetTextLineHeight();
					EditorIcons.Draw(ImGui.GetWindowDrawList(), start, icon, Prefab.Load(stage.AssetPath)?.IsVariant == true ? IconKind.PrefabVariant : IconKind.Prefab, ImGui.GetColorU32(EditorStyle.Prefab));
					ImGui.Dummy(new NVector2(icon, icon));
					ImGui.SameLine(0f, 3f);
					ImGui.TextUnformatted(name + (editor.SceneDirty ? " *" : ""));
				}
				else if (ImGui.SmallButton(name + "##crumb" + i))
				{
					editor.GoToStage(i - 1);
					return;
				}
			}

			bool autoSave = stage.AutoSave;
			ImGui.BeginDisabled(autoSave || !editor.SceneDirty);
			if (ImGui.SmallButton("Save##stage_save"))
			{
				editor.SavePrefabStage();
			}

			ImGui.EndDisabled();
			ImGui.SameLine();
			if (ImGui.Checkbox("Auto Save", ref autoSave))
			{
				stage.AutoSave = autoSave;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Write every change to the prefab file as it is made");
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

			if (revealPinged && pinged != null && pinged != go && !pinged.dead && pinged.transform.IsChildOf(go.transform))
			{
				ImGui.SetNextItemOpen(true);
			}

			PrefabInstance instance = go.prefabInstance;
			Prefab openTarget = instance == null ? null : PrefabUtility.IsPrefabInstanceRoot(go) ? instance.Prefab : PrefabUtility.GetNestedPrefab(go);
			bool open;
			ImGui.SetNextItemAllowOverlap();
			if (renaming == go)
			{
				open = ImGui.TreeNodeEx(iconPadding + "###node", flags);
				DrawIcon(go, instance);
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
				NVector4? color = TextColor(go, instance);
				if (color.HasValue)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
				}

				open = ImGui.TreeNodeEx(iconPadding + go.Name + "###node", flags);
				TestAnchors.RecordItem("hierarchy:" + go.Name);
				if (color.HasValue)
				{
					ImGui.PopStyleColor();
				}

				DrawIcon(go, instance);
				if (ImGui.IsItemHovered() && instance != null && !DragAndDrop.IsDragging)
				{
					ImGui.SetTooltip(RowTooltip(go, instance));
				}

				// Selected when the click ends without a drag (as in Unity): dragging an object on to a field of the
				// selected object keeps the selection, so the inspector showing the field stays.
				if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
				{
					pressed = go;
				}

				if (pressed == go && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
				{
					if (ImGui.IsItemHovered() && !ImGuiP.IsMouseDragPastThreshold(ImGuiMouseButton.Left))
					{
						editor.Select(go);
					}

					pressed = null;
				}

				if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && go.transform.childCount == 0)
				{
					editor.SceneView.FrameObject(go);
				}
			}

			if (go == pinged)
			{
				FlashPinged();
			}

			if (ImGui.BeginDragDropSource())
			{
				Dragged = go;
				// Re-parents in the hierarchy; object fields take the object or its component of their type; the project panel makes a prefab of it.
				DragAndDrop.SetPayload(DragType, DragItem.FromGameObject(go));
				ImGui.EndDragDropSource();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (AcceptObject() && Dragged != null && Dragged != go)
				{
					deferredReparent = Dragged;
					deferredParent = go.transform;
				}

				AcceptAsset(go.transform);
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

				if ((instance == null || PrefabUtility.IsPrefabInstanceRoot(go)) && go != editor.StageRoot && ImGui.MenuItem("Save as Prefab", "", false, !editor.IsPlaying))
				{
					string folder = editor.ProjectFolder;
					editor.Defer(() => editor.CreatePrefab(go, folder));
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Saves the object as a prefab in the project panel's folder (or drag it there)");
				}

				PrefabMenus.DrawObjectMenu(editor, go);
				ImGui.EndPopup();
			}

			if (editor.Selected == go && IsFocused && renaming == null && ImGui.IsKeyPressed(ImGuiKey.F2, false))
			{
				StartRename(go);
			}

			if (openTarget != null)
			{
				DrawOpenArrow(openTarget);
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

		/// <summary>Unity's colors: prefab instances blue (dimmed when inactive), a missing prefab's instance red, inactive objects grey.</summary>
		private static NVector4? TextColor(GameObject go, PrefabInstance instance)
		{
			if (instance != null)
			{
				return instance.IsMissing ? EditorStyle.PrefabMissing : go.activeInHierarchy ? EditorStyle.Prefab : EditorStyle.PrefabDim;
			}

			return go.activeInHierarchy ? (NVector4?)null : EditorStyle.Dim;
		}

		/// <summary>The row's icon (the last item is its tree node): a cube; a solid prefab cube on instance roots and nested prefab roots; "+" on added objects.</summary>
		private static void DrawIcon(GameObject go, PrefabInstance instance)
		{
			NVector2 min = ImGui.GetItemRectMin();
			float height = ImGui.GetItemRectSize().Y;
			float size = ImGui.GetTextLineHeight();
			var at = new NVector2(min.X + ImGui.GetTreeNodeToLabelSpacing() + 1f, min.Y + (height - size) * 0.5f);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			IconKind kind = IconKind.GameObject;
			NVector4 color = go.activeInHierarchy ? new NVector4(0.75f, 0.75f, 0.78f, 1f) : EditorStyle.Dim;
			if (instance != null)
			{
				color = instance.IsMissing ? EditorStyle.PrefabMissing : go.activeInHierarchy ? EditorStyle.Prefab : EditorStyle.PrefabDim;
				Prefab prefab = PrefabUtility.IsPrefabInstanceRoot(go) ? instance.Prefab : PrefabUtility.GetNestedPrefab(go);
				if (prefab != null || (instance.IsMissing && instance.Root == go))
				{
					kind = prefab != null && prefab.IsVariant ? IconKind.PrefabVariant : IconKind.Prefab;
				}
			}

			EditorIcons.Draw(draw, at, size, kind, ImGui.GetColorU32(color));
			if (PrefabUtility.IsAddedGameObject(go))
			{
				float badge = size * 0.62f;
				EditorIcons.Draw(draw, at + new NVector2(-badge * 0.35f, size - badge * 0.75f), badge, IconKind.AddedBadge, ImGui.GetColorU32(EditorStyle.Added));
			}
		}

		/// <summary>The arrow at the end of a prefab root's row: opens the prefab for editing.</summary>
		private void DrawOpenArrow(Prefab prefab)
		{
			NVector2 rowMin = ImGui.GetItemRectMin();
			NVector2 rowMax = ImGui.GetItemRectMax();
			float height = rowMax.Y - rowMin.Y;
			float width = height;
			ImGui.SameLine();
			ImGui.SetCursorScreenPos(new NVector2(rowMax.X - width, rowMin.Y));
			ImGui.InvisibleButton("##open_prefab", new NVector2(width, height));
			bool hovered = ImGui.IsItemHovered();
			if (hovered)
			{
				ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.ButtonHovered), 2f);
				ImGui.SetTooltip("Open the prefab '" + prefab.name + "'");
			}

			float icon = height * 0.7f;
			EditorIcons.Draw(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new NVector2((width - icon) * 0.5f, (height - icon) * 0.5f), icon, IconKind.Open, ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled));
			if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !editor.IsPlaying)
			{
				openPrefab = prefab.AssetPath;
			}
		}

		private static string RowTooltip(GameObject go, PrefabInstance instance)
		{
			if (instance.IsMissing)
			{
				return "Missing prefab: " + instance.PrefabPath;
			}

			if (PrefabUtility.IsPrefabInstanceRoot(go))
			{
				Prefab prefab = instance.Prefab;
				return "Instance of the prefab '" + prefab?.name + "'" + (prefab?.BasePrefab != null ? " (variant of '" + prefab.BasePrefab.name + "')" : "");
			}

			Prefab nested = PrefabUtility.GetNestedPrefab(go);
			return nested != null
				? "Nested prefab '" + nested.name + "' in the instance of '" + instance.Prefab?.name + "'"
				: "Part of the instance of '" + instance.Prefab?.name + "'";
		}

		/// <summary>The pinged row (the last item): scrolled into view once, outlined while the ping lasts.</summary>
		private void FlashPinged()
		{
			if (revealPinged)
			{
				ImGui.SetScrollHereY(0.5f);
				revealPinged = false;
			}

			double age = ImGui.GetTime() - pingTime;
			if (age > PingSeconds)
			{
				pinged = null;
				return;
			}

			float alpha = (float)(1.0 - age / PingSeconds);
			NVector4 color = EditorStyle.Selection;
			color.W = alpha;
			ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - new NVector2(2f, 1f), ImGui.GetItemRectMax() + new NVector2(2f, 1f), ImGui.GetColorU32(color), 3f, ImDrawFlags.None, 2f);
		}

		private static unsafe bool AcceptObject()
		{
			ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(DragType);
			return !payload.IsNull;
		}

		/// <summary>A model or prefab file from the project panel: instantiated under <paramref name="parent" /> after the tree is drawn.</summary>
		private void AcceptAsset(Transform parent)
		{
			string path = ProjectPanel.DraggedAsset;
			if (!ImGui.AcceptDragDropPayload(ProjectPanel.DragType).IsNull && path != null
				&& (ModelAssets.IsModelFile(path) || path.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase)))
			{
				droppedAsset = path;
				droppedAssetParent = parent;
			}
		}

		private void Reparent(GameObject child, Transform parent)
		{
			if (parent != null && (parent == child.transform || parent.IsChildOf(child.transform)))
			{
				return;
			}

			if (child.prefabInstance != null && child.prefabInstance.Root != child)
			{
				Debug.LogWarning($"[Editor] '{child.Name}' is part of the prefab instance '{child.prefabInstance.Root?.Name}': its objects stay where the prefab has them (move the instance's root, or unpack it).");
				return;
			}

			if (editor.Stage != null)
			{
				GameObject root = editor.StageRoot;
				if (child == root)
				{
					Debug.LogWarning("[Editor] The root of the edited prefab stays the root.");
					return;
				}

				// A prefab has one root: what is dropped on the empty area goes under it.
				parent ??= root?.transform;
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
