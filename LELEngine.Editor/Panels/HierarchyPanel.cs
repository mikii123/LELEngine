using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Editor.Automation;
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
	internal sealed class HierarchyPanel : IEditorPanel
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

		// The selection the panel saw last, and a new one still to be revealed (parents unfolded, row scrolled into view).
		private GameObject lastSelected;
		private GameObject revealSelection;

		// The rows as drawn (this frame, and the frame before: what a Shift click's range runs over) and the row a range
		// starts from: the one clicked last without Shift.
		private List<GameObject> rows = new List<GameObject>();
		private List<GameObject> shownRows = new List<GameObject>();
		private GameObject rangeAnchor;

		// Vertical padding above and below a row's text (rows have no spacing between them).
		private const float RowPadding = 2f;

		// A prefab to open after the tree is drawn (the arrow at the end of a row).
		private string openPrefab;

		// Unfolded rows of the shown scene (rows start folded), kept per user (HierarchyExpansion): applied to each row the
		// first time it is drawn for the scene, saved a moment after a change.
		private string expansionKey;
		private HashSet<ulong> expanded = new HashSet<ulong>();
		private readonly HashSet<ulong> expansionApplied = new HashSet<ulong>();
		private bool expansionDirty;
		private double expansionChangedAt;
		private const double ExpansionSaveDelay = 0.5;

		// Leading spaces of a row's label: room for its icon.
		private string iconPadding = "";
		private float iconPaddingFont;

		// The visibility column at the window's left (the eye: hidden in the scene view), the rows indented past it.
		private float gutterX;
		private float gutterWidth;

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
			lastSelected = null;
			revealSelection = null;
			rangeAnchor = null;
			rows.Clear();
			shownRows.Clear();
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

		string IEditorPanel.PanelName => "Hierarchy";
		string IEditorPanel.ImGuiName => "Hierarchy";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(420, 800);

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
			if (expansionDirty && ImGui.GetTime() - expansionChangedAt > ExpansionSaveDelay)
			{
				SaveState();
			}
		}

		/// <summary>Writes the folded rows of the shown scene now (when they changed).</summary>
		public void SaveState()
		{
			if (expansionDirty)
			{
				expansionDirty = false;
				HierarchyExpansion.Save(expansionKey, expanded);
			}
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			Scene scene = editor.Scene;
			if (scene == null)
			{
				// Empty while the project loads (its scene opens last).
				if (!editor.IsLoadingProject)
				{
					ImGui.TextDisabled("No scene");
				}

				return;
			}

			string key = HierarchyExpansion.Key(editor.Project?.RootPath, editor.Stage?.AssetPath ?? scene.Path);
			if (key != expansionKey)
			{
				SaveState();
				expansionKey = key;
				expanded = HierarchyExpansion.Load(key);
				expansionApplied.Clear();
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

			// A new selection (picked in the scene view, framed, chosen in a field): its parents unfold and its row scrolls
			// into view when it is out of it (the user's ask, 2026-11-08; a click on a row is in view already).
			GameObject selected = editor.Selected;
			if (selected != lastSelected)
			{
				lastSelected = selected;
				revealSelection = selected != null && !selected.dead && selected.scene == scene ? selected : null;
				// Selected elsewhere (the scene view, a field, a command): the next Shift click's range starts there.
				if (rangeAnchor == null || !editor.IsSelected(rangeAnchor))
				{
					rangeAnchor = selected;
				}
			}

			GameObject deferredReparent = null;
			Transform deferredParent = null;
			rows.Clear();
			gutterX = ImGui.GetCursorScreenPos().X;
			gutterWidth = ImGui.GetTextLineHeight() + 2f * RowPadding + 2f;
			ImGui.Indent(gutterWidth);
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				DrawNode(root, ref deferredReparent, ref deferredParent);
			}

			(rows, shownRows) = (shownRows, rows);
			ImGui.Unindent(gutterWidth);

			// Not drawn at all (hidden from the hierarchy): nothing to reveal.
			revealSelection = null;

			// Empty area: drop to un-parent, right click to create, click to deselect.
			NVector2 rest = ImGui.GetContentRegionAvail();
			EditorGui.InvisibleButton("##hierarchy_empty", new NVector2(Math.Max(1, rest.X), Math.Max(30, rest.Y)));
			if (EditorGui.IsItemClicked(ImGuiMouseButton.Left))
			{
				editor.ClearSelection();
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

			if (EditorGui.BeginPopupContextItem("##hierarchy_context"))
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
			if (EditorGui.SmallButton("<##stage_back"))
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
				else if (EditorGui.SmallButton(name + "##crumb" + i))
				{
					editor.GoToStage(i - 1);
					return;
				}
			}

			bool autoSave = stage.AutoSave;
			ImGui.BeginDisabled(autoSave || !editor.SceneDirty);
			if (EditorGui.SmallButton("Save##stage_save"))
			{
				editor.SavePrefabStage();
			}

			ImGui.EndDisabled();
			ImGui.SameLine();
			if (EditorGui.Checkbox("Auto Save", ref autoSave))
			{
				stage.AutoSave = autoSave;
				EditorPreferences.SetToggle("Prefab.AutoSave", autoSave);
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
			ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanFullWidth;
			// Hidden children do not count: an arrow over nothing would look like a broken object.
			bool leaf = !TempObjects.HasVisibleChildren(go);
			bool readOnly = TempObjects.IsReadOnly(go);
			if (leaf)
			{
				flags |= ImGuiTreeNodeFlags.Leaf;
			}
			else if (expansionApplied.Add(go.Id))
			{
				ImGui.SetNextItemOpen(expanded.Contains(go.Id), ImGuiCond.Always);
			}

			rows.Add(go);
			if (editor.IsSelected(go))
			{
				flags |= ImGuiTreeNodeFlags.Selected;
			}

			if (revealPinged && pinged != null && pinged != go && !pinged.dead && pinged.transform.IsChildOf(go.transform)
				|| revealSelection != null && revealSelection != go && revealSelection.transform.IsChildOf(go.transform))
			{
				ImGui.SetNextItemOpen(true);
			}

			PrefabInstance instance = go.prefabInstance;
			Prefab openTarget = instance == null ? null : PrefabUtility.IsPrefabInstanceRoot(go) ? instance.Prefab : PrefabUtility.GetNestedPrefab(go);
			bool open;
			NVector2 rowMin;
			NVector2 rowMax;
			flags |= ImGuiTreeNodeFlags.FramePadding;
			PushRowStyle();
			ImGui.SetNextItemAllowOverlap();
			// The row spans the whole width (indent included); its arrow and label start at the indented cursor.
			float rowX = ImGui.GetCursorScreenPos().X;
			if (renaming == go)
			{
				open = EditorGui.TreeNodeEx(iconPadding + "###node", flags);
				rowMin = ImGui.GetItemRectMin();
				rowMax = ImGui.GetItemRectMax();
				DrawIcon(go, instance, rowX);
				ImGui.SameLine();
				if (focusRename)
				{
					ImGui.SetKeyboardFocusHere();
					focusRename = false;
				}

				ImGui.SetNextItemWidth(-1);
				renameAge++;
				if (EditorGui.InputText("##rename", ref renameText, 256, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
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

				open = EditorGui.TreeNodeEx(iconPadding + go.Name + "###node", flags);
				rowMin = ImGui.GetItemRectMin();
				rowMax = ImGui.GetItemRectMax();
				// From the label on: the row spans the arrow too, and a click there would fold instead of select.
				TestAnchors.Record("hierarchy:" + go.Name, new NVector2(rowX + ImGui.GetTreeNodeToLabelSpacing(), ImGui.GetItemRectMin().Y), ImGui.GetItemRectMax());
				if (color.HasValue)
				{
					ImGui.PopStyleColor();
				}

				DrawIcon(go, instance, rowX);
				string temporary = TempObjects.Describe(go);
				if (temporary != null)
				{
					DrawTempTag();
				}

				if (ImGui.IsItemHovered() && temporary != null && !DragAndDrop.IsDragging)
				{
					ImGui.SetTooltip(temporary);
				}

				// Selected when the click ends without a drag (as in Unity): dragging an object on to a field of the
				// selected object keeps the selection, so the inspector showing the field stays; and a press on one of
				// several selected rows can drag them all.
				if (EditorGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
				{
					pressed = go;
				}

				// A click from automation (EditorGui.IsItemInvoked) ends at once: there is no mouse button to release.
				bool invoked = EditorGui.IsItemInvoked();
				if (pressed == go && (ImGui.IsMouseReleased(ImGuiMouseButton.Left) || invoked))
				{
					if (invoked || (ImGui.IsItemHovered() && !ImGuiP.IsMouseDragPastThreshold(ImGuiMouseButton.Left)))
					{
						// Automation's click holds the keys it was asked with, whatever the keyboard holds.
						ClickRow(go, invoked ? UIAutomation.InvokedCtrl : ImGui.GetIO().KeyCtrl, invoked ? UIAutomation.InvokedShift : ImGui.GetIO().KeyShift);
					}

					pressed = null;
				}

				// A double click frames the object, as in Unity; only the arrow folds (a double click there folds twice).
				if (EditorGui.IsItemDoubleClicked() && !ImGui.IsItemToggledOpen())
				{
					editor.SceneView.FrameObject(go);
				}
			}

			ImGui.PopStyleVar(2);
			if (!leaf && open != expanded.Contains(go.Id))
			{
				if (open)
				{
					expanded.Add(go.Id);
				}
				else
				{
					expanded.Remove(go.Id);
				}

				expansionDirty = true;
				expansionChangedAt = ImGui.GetTime();
			}

			if (go == pinged)
			{
				FlashPinged();
			}

			// The new selection's row (the last item): scrolled to the middle when it is not in view.
			if (go == revealSelection)
			{
				if (!ImGui.IsItemVisible())
				{
					ImGui.SetScrollHereY(0.5f);
				}

				revealSelection = null;
			}

			// A read-only object is not moved, and nothing goes under it (it would not be saved).
			if (!readOnly && ImGui.BeginDragDropSource())
			{
				Dragged = go;
				// Re-parents in the hierarchy; object fields take the object or its component of their type; the project panel makes a prefab of it.
				DragAndDrop.SetPayload(DragType, DragItem.FromGameObject(go));
				ImGui.EndDragDropSource();
			}

			if (!readOnly && ImGui.BeginDragDropTarget())
			{
				if (AcceptObject() && Dragged != null && Dragged != go)
				{
					deferredReparent = Dragged;
					deferredParent = go.transform;
				}

				AcceptAsset(go.transform);
				ImGui.EndDragDropTarget();
			}

			if (EditorGui.BeginPopupContextItem("##object_context"))
			{
				// On one of several selected rows the menu is the selection's (Duplicate and Delete take them all);
				// on any other row it selects that row.
				if (!editor.IsSelected(go))
				{
					editor.Select(go);
					rangeAnchor = go;
				}

				if (EditorMenu.Item("Rename", "F2", false, !readOnly))
				{
					StartRename(go);
				}

				if (EditorMenu.Item("Duplicate", "Ctrl+D", false, !readOnly)) editor.DuplicateSelected();
				if (EditorMenu.Item("Delete", "Del", false, !readOnly)) editor.DeleteSelected();
				ImGui.Separator();
				if (EditorMenu.Begin("Create Child", !readOnly))
				{
					editor.DrawCreateMenu(go.transform);
					EditorMenu.End();
				}

				if ((instance == null || PrefabUtility.IsPrefabInstanceRoot(go)) && go != editor.StageRoot && EditorMenu.Item("Save as Prefab", "", false, !editor.IsPlaying && !TempObjects.IsTemporary(go)))
				{
					string folder = editor.ProjectFolder;
					editor.Defer(() => editor.CreatePrefab(go, folder));
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Saves the object as a prefab in the project panel's folder (or drag it there)");
				}

				PrefabMenus.DrawObjectMenu(editor, go);
				ImGui.Separator();
				if (EditorMenu.Item("Show All in Scene View", "", false, editor.Visibility.AnyHidden))
				{
					editor.Visibility.ShowAll();
				}

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

			DrawVisibility(go, rowMin, rowMax);
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

		/// <summary>
		///     Style of a row's items (pops two vars): no vertical spacing, so rows touch and their highlights form one
		///     column as in Unity; the row's own padding keeps the old pitch (text line + 4 px).
		/// </summary>
		private static void PushRowStyle()
		{
			ImGuiStylePtr style = ImGui.GetStyle();
			var spacing = new NVector2(style.ItemSpacing.X, 0f);
			var padding = new NVector2(style.FramePadding.X, RowPadding);
			ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, spacing);
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, padding);
		}

		/// <summary>Unity's colors: prefab instances blue (dimmed when inactive), a missing prefab's instance red, inactive objects grey.</summary>
		private static NVector4? TextColor(GameObject go, PrefabInstance instance)
		{
			if (instance != null)
			{
				return instance.IsMissing ? EditorStyle.PrefabMissing : go.activeInHierarchy ? EditorStyle.Prefab : EditorStyle.PrefabDim;
			}

			// A temporary object is dimmed like an inactive one, so the scene's own objects stand out.
			return go.activeInHierarchy && !TempObjects.IsTemporary(go) ? (NVector4?)null : EditorStyle.Dim;
		}

		/// <summary>The "temp" tag at the right end of a temporary object's row (the last item is its tree node).</summary>
		private static void DrawTempTag()
		{
			NVector2 min = ImGui.GetItemRectMin();
			NVector2 max = ImGui.GetItemRectMax();
			NVector2 size = ImGui.CalcTextSize(TempObjects.Tag);
			// The row spans the window's width (SpanFullWidth): the tag sits at its right end.
			float x = MathF.Max(min.X, max.X - size.X - 6f);
			ImGui.GetWindowDrawList().AddText(new NVector2(x, min.Y + (max.Y - min.Y - size.Y) * 0.5f), ImGui.GetColorU32(EditorStyle.Dim), TempObjects.Tag);
		}

		/// <summary>The row's icon (the last item is its tree node): a cube; a solid prefab cube on instance roots and nested prefab roots; "+" on added objects.</summary>
		private static void DrawIcon(GameObject go, PrefabInstance instance, float rowX)
		{
			NVector2 min = ImGui.GetItemRectMin();
			float height = ImGui.GetItemRectSize().Y;
			float size = ImGui.GetTextLineHeight();
			var at = new NVector2(rowX + ImGui.GetTreeNodeToLabelSpacing() + 1f, min.Y + (height - size) * 0.5f);
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
		/// <summary>
		///     The eye in the column at the row's left (Unity's scene visibility): a click hides the object and everything under
		///     it in the scene view, or shows them again; Alt + click only the object. Shown while the row is hovered, and
		///     always for a hidden object (a closed eye) or one with hidden objects under it (a dim eye).
		/// </summary>
		private void DrawVisibility(GameObject go, NVector2 rowMin, NVector2 rowMax)
		{
			float height = rowMax.Y - rowMin.Y;
			var min = new NVector2(gutterX, rowMin.Y);
			ImGui.SameLine();
			ImGui.SetCursorScreenPos(min);
			// The last item of the row: it decides where the next row starts.
			PushRowStyle();
			bool clicked = EditorGui.InvisibleButton("Visibility " + go.Name + "###visibility", new NVector2(gutterWidth, height));
			ImGui.PopStyleVar(2);
			SceneVisibility visibility = editor.Visibility;
			bool hidden = visibility.IsHidden(go);
			bool hovered = ImGui.IsItemHovered();
			if (clicked)
			{
				visibility.SetHidden(go, !hidden, ImGui.GetIO().KeyAlt);
				hidden = !hidden;
			}

			bool hiddenBelow = !hidden && visibility.HasHiddenChildren(go);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			if (hovered)
			{
				draw.AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.ButtonHovered), 2f);
				ImGui.SetTooltip((hidden ? "Show in the Scene view" : "Hide in the Scene view") + " with the objects under it (Alt: the object alone)");
			}

			bool rowHovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, rowMax);
			if (!hidden && !hiddenBelow && !rowHovered)
			{
				return;
			}

			float icon = ImGui.GetTextLineHeight() * 0.95f;
			NVector2 at = min + new NVector2((gutterWidth - icon) * 0.5f, (height - icon) * 0.5f);
			uint color = hidden || hovered ? ImGui.GetColorU32(ImGuiCol.Text) : ImGui.GetColorU32(ImGuiCol.TextDisabled);
			EditorIcons.Draw(draw, at, icon, hidden ? IconKind.EyeClosed : IconKind.Eye, color);
		}

		private void DrawOpenArrow(Prefab prefab)
		{
			NVector2 rowMin = ImGui.GetItemRectMin();
			NVector2 rowMax = ImGui.GetItemRectMax();
			float height = rowMax.Y - rowMin.Y;
			float width = height;
			ImGui.SameLine();
			ImGui.SetCursorScreenPos(new NVector2(rowMax.X - width, rowMin.Y));
			// The last item of the row: it decides where the next row starts.
			PushRowStyle();
			EditorGui.InvisibleButton("##open_prefab", new NVector2(width, height));
			ImGui.PopStyleVar(2);
			bool hovered = ImGui.IsItemHovered();
			if (hovered)
			{
				ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.ButtonHovered), 2f);
				ImGui.SetTooltip("Open the prefab '" + prefab.name + "'");
			}

			float icon = height * 0.7f;
			EditorIcons.Draw(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new NVector2((width - icon) * 0.5f, (height - icon) * 0.5f), icon, IconKind.Open, ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled));
			if (EditorGui.IsItemClicked(ImGuiMouseButton.Left) && !editor.IsPlaying)
			{
				openPrefab = prefab.AssetPath;
			}
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

		/// <summary>
		///     A click on a row: alone it selects the row; with Ctrl the row joins the selection or leaves it; with Shift the
		///     rows from the one clicked before to this one are selected (added to the selection with Ctrl too).
		/// </summary>
		private void ClickRow(GameObject go, bool ctrl, bool shift)
		{
			int from = shift && rangeAnchor != null && !rangeAnchor.dead ? shownRows.IndexOf(rangeAnchor) : -1;
			int to = from >= 0 ? shownRows.IndexOf(go) : -1;
			if (from >= 0 && to >= 0)
			{
				var range = new List<GameObject>();
				if (ctrl)
				{
					range.AddRange(editor.SelectedObjects);
				}

				for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
				{
					range.Add(shownRows[i]);
				}

				// The range's start stays: the next Shift click runs from the same row.
				editor.SelectObjects(range, go);
				return;
			}

			if (ctrl)
			{
				editor.ToggleSelected(go);
			}
			else
			{
				editor.Select(go);
			}

			rangeAnchor = go;
		}

		/// <summary>The dragged row goes under <paramref name="parent" />; when it is one of several selected rows, they all do (one undo step).</summary>
		private void Reparent(GameObject dragged, Transform parent)
		{
			List<GameObject> moving = editor.IsSelected(dragged) && editor.SelectedObjects.Count > 1 ? editor.SelectedRoots() : new List<GameObject> { dragged };
			if (editor.Stage != null)
			{
				// A prefab has one root: what is dropped on the empty area goes under it.
				parent ??= editor.StageRoot?.transform;
			}

			moving.RemoveAll(child =>
			{
				if (editor.Stage != null && child == editor.StageRoot)
				{
					Debug.LogWarning("[Editor] The root of the edited prefab stays the root.");
					return true;
				}

				if (parent != null && (parent == child.transform || parent.IsChildOf(child.transform)))
				{
					return true;
				}

				if (child.prefabInstance != null && child.prefabInstance.Root != child)
				{
					Debug.LogWarning($"[Editor] '{child.Name}' is part of the prefab instance '{child.prefabInstance.Root?.Name}': its objects stay where the prefab has them (move the instance's root, or unpack it).");
					return true;
				}

				return child.transform.parent == parent || TempObjects.IsReadOnly(child);
			});
			if (moving.Count == 0)
			{
				return;
			}

			editor.RecordStructural(moving.Count == 1 ? "Re-parent " + moving[0].Name : "Re-parent " + moving.Count + " objects", () =>
			{
				foreach (GameObject child in moving)
				{
					child.transform.SetParent(parent, true);
				}
			});
		}

		private void StartRename(GameObject go)
		{
			if (TempObjects.IsReadOnly(go))
			{
				return;
			}

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
