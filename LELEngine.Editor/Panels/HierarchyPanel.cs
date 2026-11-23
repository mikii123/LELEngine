using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Editor.Automation;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Scene object tree: selection, drag and drop re-parenting and reordering (as in Unity: dropped on the upper or
	///     lower edge of a row the object goes in front of it or after it, on its middle under it), create / rename /
	///     duplicate / delete. Prefab
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

		// A model or prefab file dropped from the project panel this frame, the object it goes under and the child it goes
		// in front of (null: after the last).
		private string droppedAsset;
		private Transform droppedAssetParent;
		private Transform droppedAssetBefore;

		// A hierarchy object dropped this frame, moved after the tree is drawn.
		private ObjectDrop? objectDrop;

		// Where a dragged object would go, drawn after the rows (a row drawn later would cover it): a line between rows,
		// or a frame around the row it would go under.
		private bool showDropMark;
		private bool dropMarkFrame;
		private NVector2 dropMarkMin;
		private NVector2 dropMarkMax;

		// The part of a row's height at its upper and lower edge where a dropped object goes in front of it or after it.
		private const float ReorderEdge = 0.3f;

		/// <summary>
		///     A dropped hierarchy object: it goes under <see cref="Parent" /> (null: the scene's roots), in front of
		///     <see cref="Before" /> (null: after the last child), or only under the parent when not <see cref="Reorder" />.
		/// </summary>
		private struct ObjectDrop
		{
			public GameObject Dragged;
			public Transform Parent;
			public Transform Before;
			public bool Reorder;
		}

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

			// The arrows move the selection (HandleSelectionKeys), not ImGui's keyboard navigation.
			bool visible = ImGui.Begin("Hierarchy", ref Open, ImGuiWindowFlags.NoNavInputs);
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

			objectDrop = null;
			showDropMark = false;
			rows.Clear();
			gutterX = ImGui.GetCursorScreenPos().X;
			gutterWidth = ImGui.GetTextLineHeight() + 2f * RowPadding + 2f;
			ImGui.Indent(gutterWidth);
			float rootX = ImGui.GetCursorScreenPos().X;
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				DrawNode(root);
			}

			(rows, shownRows) = (shownRows, rows);
			ImGui.Unindent(gutterWidth);
			if (IsFocused)
			{
				HandleSelectionKeys();
			}

			// Not drawn at all (hidden from the hierarchy): nothing to reveal.
			revealSelection = null;

			// Empty area: drop to make a root (the last; in prefab mode the prefab root's last child), right click to
			// create, click to deselect.
			NVector2 rest = ImGui.GetContentRegionAvail();
			EditorGui.InvisibleButton("##hierarchy_empty", new NVector2(Math.Max(1, rest.X), Math.Max(30, rest.Y)));
			if (EditorGui.IsItemClicked(ImGuiMouseButton.Left))
			{
				editor.ClearSelection();
			}

			if (ImGui.BeginDragDropTarget())
			{
				const ImGuiDragDropFlags flags = ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect;
				float x = rootX + (editor.Stage != null ? ImGui.GetStyle().IndentSpacing : 0f);
				ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(DragType, flags);
				if (!payload.IsNull && Dragged != null)
				{
					SetDropLine(x, ImGui.GetItemRectMin().Y, ImGui.GetItemRectMax().X);
					if (payload.IsDelivery())
					{
						objectDrop = new ObjectDrop { Dragged = Dragged, Reorder = true };
					}
				}

				payload = ImGui.AcceptDragDropPayload(ProjectPanel.DragType, flags);
				if (!payload.IsNull && IsInstantiable(ProjectPanel.DraggedAsset))
				{
					SetDropLine(x, ImGui.GetItemRectMin().Y, ImGui.GetItemRectMax().X);
					if (payload.IsDelivery())
					{
						droppedAsset = ProjectPanel.DraggedAsset;
						droppedAssetParent = null;
						droppedAssetBefore = null;
					}
				}

				ImGui.EndDragDropTarget();
			}

			DrawDropMark();
			if (EditorGui.BeginPopupContextItem("##hierarchy_context"))
			{
				editor.DrawCreateMenu(null);
				ImGui.EndPopup();
			}

			if (objectDrop.HasValue)
			{
				Move(objectDrop.Value);
				objectDrop = null;
			}

			if (droppedAsset != null)
			{
				if (droppedAsset.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
				{
					editor.InstantiatePrefab(droppedAsset, droppedAssetParent, null, droppedAssetBefore);
				}
				else
				{
					editor.InstantiateModel(droppedAsset, droppedAssetParent, droppedAssetBefore);
				}

				droppedAsset = null;
				droppedAssetParent = null;
				droppedAssetBefore = null;
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

		private void DrawNode(GameObject go)
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
				AcceptOnRow(go, rowX, rowMin, rowMax, open && !leaf);
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
					DrawNode(child.gameObject);
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
				// The eye of one of several selected rows hides or shows them all (as in Unity).
				if (editor.IsSelected(go) && editor.SelectedObjects.Count > 1)
				{
					foreach (GameObject selected in editor.SelectedRoots())
					{
						visibility.SetHidden(selected, !hidden, ImGui.GetIO().KeyAlt);
					}
				}
				else
				{
					visibility.SetHidden(go, !hidden, ImGui.GetIO().KeyAlt);
				}

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

		/// <summary>
		///     What is dragged over a row (the last item is the row's drop target): a hierarchy object (moved), a prefab or a
		///     model file from the project panel (instantiated there). Both go where <see cref="PlaceOnRow" /> says; the drop
		///     is done after the tree is drawn.
		/// </summary>
		private void AcceptOnRow(GameObject row, float rowX, NVector2 rowMin, NVector2 rowMax, bool unfolded)
		{
			const ImGuiDragDropFlags flags = ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect;
			ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(DragType, flags);
			GameObject dragged = Dragged;
			// Over itself or its own subtree: it cannot go there.
			if (!payload.IsNull && dragged != null && dragged != row && !row.transform.IsChildOf(dragged.transform))
			{
				ObjectDrop drop = PlaceOnRow(row, rowX, rowMin, rowMax, unfolded);
				drop.Dragged = dragged;
				if (payload.IsDelivery())
				{
					objectDrop = drop;
				}
			}

			payload = ImGui.AcceptDragDropPayload(ProjectPanel.DragType, flags);
			string path = ProjectPanel.DraggedAsset;
			if (!payload.IsNull && IsInstantiable(path))
			{
				ObjectDrop drop = PlaceOnRow(row, rowX, rowMin, rowMax, unfolded);
				if (payload.IsDelivery())
				{
					droppedAsset = path;
					droppedAssetParent = drop.Parent;
					droppedAssetBefore = drop.Reorder ? drop.Before : null;
				}
			}
		}

		/// <summary>
		///     Where something dropped on a row goes, as in Unity: on the row's upper edge in front of the row, on its lower
		///     edge after it (an unfolded row's: in front of its first child), on its middle under it (after its last child).
		///     Sets the mark that shows it.
		/// </summary>
		private ObjectDrop PlaceOnRow(GameObject row, float rowX, NVector2 rowMin, NVector2 rowMax, bool unfolded)
		{
			float mouse = ImGui.GetMousePos().Y;
			float edge = (rowMax.Y - rowMin.Y) * ReorderEdge;
			float indent = ImGui.GetStyle().IndentSpacing;
			var drop = new ObjectDrop { Reorder = true };
			float lineX = rowX;
			float lineY;
			if (mouse < rowMin.Y + edge)
			{
				drop.Parent = row.transform.parent;
				drop.Before = row.transform;
				lineY = rowMin.Y;
			}
			else if (mouse > rowMax.Y - edge && unfolded)
			{
				drop.Parent = row.transform;
				drop.Before = row.transform.childCount > 0 ? row.transform.GetChild(0) : null;
				lineX += indent;
				lineY = rowMax.Y;
			}
			else if (mouse > rowMax.Y - edge)
			{
				drop.Parent = row.transform.parent;
				drop.Before = editor.NextSibling(row.transform);
				lineY = rowMax.Y;
			}
			else
			{
				drop.Parent = row.transform;
				drop.Reorder = false;
				lineY = 0f;
			}

			// In prefab mode nothing stands beside the prefab's root: in front of it is its first child, after it its last.
			if (drop.Reorder && editor.Stage != null && row == editor.StageRoot && drop.Parent != row.transform)
			{
				drop.Parent = row.transform;
				drop.Before = drop.Before == row.transform && row.transform.childCount > 0 ? row.transform.GetChild(0) : null;
				lineX = rowX + indent;
			}

			if (drop.Reorder)
			{
				SetDropLine(lineX, lineY, rowMax.X);
			}
			else
			{
				showDropMark = true;
				dropMarkFrame = true;
				dropMarkMin = rowMin;
				dropMarkMax = rowMax;
			}

			return drop;
		}

		private void SetDropLine(float x, float y, float right)
		{
			showDropMark = true;
			dropMarkFrame = false;
			dropMarkMin = new NVector2(x, y);
			dropMarkMax = new NVector2(right, y);
		}

		/// <summary>The line where the dragged object would go (a small circle at its start, as in Unity), or the frame of its new parent's row.</summary>
		private void DrawDropMark()
		{
			if (!showDropMark)
			{
				return;
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			uint color = ImGui.GetColorU32(EditorIcons.Accept);
			if (dropMarkFrame)
			{
				draw.AddRect(dropMarkMin, dropMarkMax, color, 2f, ImDrawFlags.None, 2f);
				return;
			}

			const float radius = 3f;
			var centre = new NVector2(dropMarkMin.X + radius + 1f, dropMarkMin.Y);
			draw.AddCircle(centre, radius, color, 0, 1.5f);
			draw.AddLine(new NVector2(centre.X + radius, dropMarkMin.Y), dropMarkMax, color, 2f);
		}

		/// <summary>A file the hierarchy instantiates when it is dropped there: a model or a prefab.</summary>
		private static bool IsInstantiable(string path)
		{
			return path != null && (ModelAssets.IsModelFile(path) || path.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase));
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

		/// <summary>
		///     The keys of the focused hierarchy (as in Unity): Ctrl+A selects every row shown; Up / Down select the row above
		///     / below (Shift: the range from the row clicked first grows or shrinks); Home / End the first / last row; Right
		///     unfolds the selected row (unfolded: its first child), Left folds it (folded: its parent).
		/// </summary>
		private void HandleSelectionKeys()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			if (renaming != null || io.WantTextInput || shownRows.Count == 0)
			{
				return;
			}

			if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, false))
			{
				GameObject active = editor.Selected != null && shownRows.Contains(editor.Selected) ? editor.Selected : shownRows[0];
				editor.SelectObjects(shownRows, active);
				rangeAnchor ??= active;
				return;
			}

			GameObject current = editor.Selected;
			int index = current != null ? shownRows.IndexOf(current) : -1;
			if (index >= 0 && !io.KeyShift && (ImGui.IsKeyPressed(ImGuiKey.RightArrow) || ImGui.IsKeyPressed(ImGuiKey.LeftArrow)))
			{
				bool right = ImGui.IsKeyPressed(ImGuiKey.RightArrow);
				bool hasChildren = TempObjects.HasVisibleChildren(current);
				bool unfolded = hasChildren && expanded.Contains(current.Id);
				if (right && hasChildren && !unfolded)
				{
					SetExpanded(current, true);
				}
				else if (right && unfolded && index + 1 < shownRows.Count && shownRows[index + 1].transform.parent == current.transform)
				{
					SelectRow(shownRows[index + 1]);
				}
				else if (!right && unfolded)
				{
					SetExpanded(current, false);
				}
				else if (!right && current.transform.parent != null && shownRows.Contains(current.transform.parent.gameObject))
				{
					SelectRow(current.transform.parent.gameObject);
				}

				return;
			}

			int step = ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? -1 : 0;
			bool home = ImGui.IsKeyPressed(ImGuiKey.Home, false);
			bool end = ImGui.IsKeyPressed(ImGuiKey.End, false);
			if (step == 0 && !home && !end)
			{
				return;
			}

			int next = home ? 0 : end ? shownRows.Count - 1 : index < 0 ? (step > 0 ? 0 : shownRows.Count - 1) : Math.Clamp(index + step, 0, shownRows.Count - 1);
			if (next == index)
			{
				return;
			}

			GameObject target = shownRows[next];
			int from = io.KeyShift && rangeAnchor != null && !rangeAnchor.dead ? shownRows.IndexOf(rangeAnchor) : -1;
			if (from >= 0)
			{
				// The range from the anchor to the new row; the anchor stays.
				var range = new List<GameObject>();
				for (int i = Math.Min(from, next); i <= Math.Max(from, next); i++)
				{
					range.Add(shownRows[i]);
				}

				editor.SelectObjects(range, target);
				return;
			}

			SelectRow(target);
		}

		private void SelectRow(GameObject go)
		{
			editor.Select(go);
			rangeAnchor = go;
		}

		/// <summary>Folds or unfolds a row from the keyboard (applied when it is drawn next, kept with the scene's folded rows).</summary>
		private void SetExpanded(GameObject go, bool open)
		{
			if (open)
			{
				expanded.Add(go.Id);
			}
			else
			{
				expanded.Remove(go.Id);
			}

			expansionApplied.Remove(go.Id);
			expansionDirty = true;
			expansionChangedAt = ImGui.GetTime();
		}

		/// <summary>The dropped row goes where it was dropped; when it is one of several selected rows, they all do, in the hierarchy's order (one undo step).</summary>
		private void Move(ObjectDrop drop)
		{
			List<GameObject> moving = editor.IsSelected(drop.Dragged) && editor.SelectedObjects.Count > 1 ? editor.SelectedRoots() : new List<GameObject> { drop.Dragged };
			editor.MoveObjects(moving, drop.Parent, drop.Before, drop.Reorder);
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
