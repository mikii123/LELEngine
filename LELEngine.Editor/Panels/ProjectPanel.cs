using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     The project's Assets folder: folders on the left, the current folder's files on the right. Double click
	///     opens scenes and scripts; files can be dragged onto inspector fields and into the scene view.
	/// </summary>
	internal sealed class ProjectPanel
	{
		#region PublicFields

		public const string DragType = "LEL_ASSET";

		/// <summary>Asset path being dragged from the project panel.</summary>
		public static string DraggedAsset { get; private set; }

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private string folder = "";
		private int listedVersion = -1;
		private string listedFolder;
		private readonly List<string> listedDirectories = new List<string>();
		private readonly List<string> listedFiles = new List<string>();

		// The class stored by each listed ScriptableObject asset, by name (no script types: they would outlive a reload),
		// and whether the scripts have it; read when the folder is listed, not every frame.
		private readonly Dictionary<string, (string name, bool found)> listedAssetTypes = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);

		// Listed prefab variants with the name of their base.
		private readonly Dictionary<string, string> listedVariants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		// A hierarchy object dropped on a folder this frame: saved as a prefab after the panel is drawn.
		private GameObject droppedObject;
		private string droppedObjectFolder;
		private int listedScriptsVersion = -1;
		private string selectedFile;

		// Ctrl / Shift click selection (dragged together, e.g. on to a list); the last clicked file is the inspector's.
		private readonly HashSet<string> selection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private string selectionAnchor;

		// The file an object field pinged: its folder opens, the row scrolls into view and flashes.
		private string pinged;
		private double pingTime;
		private bool revealPinged;
		private const double PingSeconds = 1.6;

		private string createKind;
		private string createName = "";
		private Type createType;
		private bool openCreatePopup;
		private string error;

		// A folder row picked in the file list (highlighted; F2 renames it). Files and folders are not selected together.
		private string selectedFolder;

		// Inline rename: the file or folder being renamed (asset path), the name typed so far, and the first frame of the
		// field (it takes the keyboard focus). Files are renamed without their extension.
		private string renaming;
		private string renameBuffer = "";
		private bool renameStarted;
		private bool renameCommitted;

		// The field is where the rename started: the tree or the file list.
		private bool renameInTree;

		// Tree nodes open in the last drawn tree, and those to open on the next frame (a renamed folder and the folders
		// inside it keep their open state under their new ids).
		private readonly HashSet<string> openFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private readonly HashSet<string> openNext = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// The tree opens the shown folder's parents once after the folder changed (a ping, the file list, the breadcrumb).
		private bool revealFolder;

		#endregion

		#region Constructors

		public ProjectPanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open = true;

		/// <summary>The folder shown (asset path, "" for Assets).</summary>
		public string Folder => folder;

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			if (ImGui.Begin("Project", ref Open))
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
			if (renameCommitted)
			{
				renameCommitted = false;
				CommitRename();
			}

			if (droppedObject != null)
			{
				GameObject dropped = droppedObject;
				droppedObject = null;
				editor.CreatePrefab(dropped, droppedObjectFolder);
			}
		}

		/// <summary>Shows a file: its folder opens, its row scrolls into view and flashes for a moment.</summary>
		public void Ping(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath) || editor.Project == null)
			{
				return;
			}

			assetPath = assetPath.Replace('\\', '/');
			int slash = assetPath.LastIndexOf('/');
			folder = slash >= 0 ? assetPath.Substring(0, slash) : "";
			pinged = assetPath;
			pingTime = ImGui.GetTime();
			revealPinged = true;
			revealFolder = true;
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			ProjectInfo project = editor.Project;
			if (project == null)
			{
				return;
			}

			if (!Directory.Exists(Absolute(folder)))
			{
				folder = "";
			}

			if (ImGui.BeginTable("##project", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableSetupColumn("folders", ImGuiTableColumnFlags.WidthFixed, 200f);
				ImGui.TableSetupColumn("files", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableNextRow();

				ImGui.TableSetColumnIndex(0);
				if (ImGui.BeginChild("##folders"))
				{
					openFolders.Clear();
					DrawFolderTree("", "Assets");
					revealFolder = false;
				}

				ImGui.EndChild();

				ImGui.TableSetColumnIndex(1);
				if (ImGui.BeginChild("##files"))
				{
					DrawFiles();
				}

				ImGui.EndChild();
				ImGui.EndTable();
			}

			DrawCreatePopup();
		}

		private void DrawFolderTree(string relative, string label)
		{
			if (!Directory.Exists(Absolute(relative)))
			{
				// Gone since the parent listed it (deleted or moved outside the editor).
				return;
			}

			string[] children = Directory.GetDirectories(Absolute(relative));
			ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick | ImGuiTreeNodeFlags.SpanAvailWidth;
			if (relative.Length == 0)
			{
				flags |= ImGuiTreeNodeFlags.DefaultOpen;
			}

			if (children.Length == 0)
			{
				flags |= ImGuiTreeNodeFlags.Leaf;
			}

			bool current = string.Equals(relative, folder, StringComparison.OrdinalIgnoreCase);
			if (current)
			{
				flags |= ImGuiTreeNodeFlags.Selected;
			}

			// The shown folder's parents open, so it is visible in the tree.
			if (openNext.Remove(relative) || (folder.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase) && relative.Length > 0 && revealFolder))
			{
				ImGui.SetNextItemOpen(true);
			}

			// The label is drawn after the node: the folder icon, then the name (or the rename field).
			bool open = ImGui.TreeNodeEx("##" + relative, flags);
			bool clicked = ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen();
			NVector2 min = ImGui.GetItemRectMin();
			float labelX = min.X + ImGui.GetTreeNodeToLabelSpacing();
			float iconSize = ImGui.GetTextLineHeight();
			float y = min.Y + (ImGui.GetItemRectSize().Y - iconSize) * 0.5f;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			IconKind icon = open && children.Length > 0 ? IconKind.FolderOpen : IconKind.Folder;
			EditorIcons.Draw(draw, new NVector2(labelX, y), iconSize, icon, EditorIcons.ColorOf(icon));
			TestAnchors.RecordItem("tree:" + (relative.Length == 0 ? "Assets" : relative));
			if (open)
			{
				openFolders.Add(relative);
			}

			if (relative.Length > 0 && renameInTree && string.Equals(renaming, relative, StringComparison.OrdinalIgnoreCase))
			{
				NVector2 after = ImGui.GetCursorScreenPos();
				ImGui.SetCursorScreenPos(new NVector2(labelX + iconSize + 4f, min.Y));
				DrawRenameField();
				ImGui.SetCursorScreenPos(new NVector2(after.X, Math.Max(after.Y, ImGui.GetCursorScreenPos().Y)));
			}
			else
			{
				draw.AddText(new NVector2(labelX + iconSize + 4f, y), ImGui.GetColorU32(ImGuiCol.Text), label);
			}

			if (clicked)
			{
				ShowFolder(relative);
			}

			if (current && relative.Length > 0 && ImGui.IsWindowFocused() && ImGui.IsKeyPressed(ImGuiKey.F2) && renaming == null)
			{
				StartRename(relative, true);
			}

			AcceptSceneObject(relative);
			if (ImGui.BeginPopupContextItem("##tree_context"))
			{
				if (ImGui.MenuItem("Create Folder"))
				{
					ShowFolder(relative);
					StartCreate("Folder", "New Folder");
				}

				if (relative.Length > 0 && ImGui.MenuItem("Rename", "F2"))
				{
					StartRename(relative, true);
				}

				if (ImGui.MenuItem("Show in Explorer")) ShowInExplorer(Absolute(relative));
				ImGui.EndPopup();
			}

			if (open)
			{
				Array.Sort(children, StringComparer.OrdinalIgnoreCase);
				foreach (string child in children)
				{
					string name = Path.GetFileName(child);
					DrawFolderTree(Combine(relative, name), name);
				}

				ImGui.TreePop();
			}
		}

		/// <summary>Shows a folder in the file list (its files, nothing selected in it yet).</summary>
		private void ShowFolder(string relative)
		{
			if (!string.Equals(folder, relative, StringComparison.OrdinalIgnoreCase))
			{
				folder = relative;
				selectedFolder = null;
				revealFolder = true;
			}
		}

		/// <summary>The current folder as links: Assets > A > B (a click shows that folder).</summary>
		private void DrawBreadcrumb()
		{
			string[] parts = folder.Length == 0 ? Array.Empty<string>() : folder.Split('/');
			ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0f, 0f, 0f, 0f));
			ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new NVector2(3f, 1f));
			for (int i = -1; i < parts.Length; i++)
			{
				if (i >= 0)
				{
					ImGui.SameLine(0f, 2f);
					ImGui.TextDisabled(">");
					ImGui.SameLine(0f, 2f);
				}

				string target = i < 0 ? "" : string.Join("/", parts, 0, i + 1);
				if (ImGui.SmallButton((i < 0 ? "Assets" : parts[i]) + "##crumb" + i) && i < parts.Length - 1)
				{
					ShowFolder(target);
				}

				AcceptSceneObject(target);
			}

			ImGui.PopStyleVar();
			ImGui.PopStyleColor();
		}

		private void DrawFiles()
		{
			RefreshListing();
			FollowEditorSelection();
			DrawBreadcrumb();
			ImGui.Separator();

			float iconSize = ImGui.GetTextLineHeight();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();

			// Folders first: a click selects (F2 renames), a double click opens.
			foreach (string directory in listedDirectories)
			{
				string folderPath = Combine(folder, directory);
				NVector2 rowStart = ImGui.GetCursorScreenPos();
				if (!renameInTree && string.Equals(renaming, folderPath, StringComparison.OrdinalIgnoreCase))
				{
					EditorIcons.Draw(draw, rowStart + new NVector2(1f, 0f), iconSize, IconKind.Folder, EditorIcons.ColorOf(IconKind.Folder));
					ImGui.SetCursorScreenPos(rowStart + new NVector2(iconSize + 4f, -ImGui.GetStyle().FramePadding.Y));
					DrawRenameField();
					continue;
				}

				bool selected = string.Equals(selectedFolder, folderPath, StringComparison.OrdinalIgnoreCase);
				if (ImGui.Selectable("##dir" + directory, selected, ImGuiSelectableFlags.AllowDoubleClick))
				{
					if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						ShowFolder(folderPath);
						return;
					}

					SelectFolderRow(folderPath);
				}

				TestAnchors.RecordItem("project:" + folderPath + "/");
				EditorIcons.Draw(draw, rowStart + new NVector2(1f, 0f), iconSize, IconKind.Folder, EditorIcons.ColorOf(IconKind.Folder));
				draw.AddText(rowStart + new NVector2(iconSize + 6f, 0f), ImGui.GetColorU32(ImGuiCol.Text), directory);
				if (string.Equals(folderPath, pinged, StringComparison.OrdinalIgnoreCase))
				{
					FlashPinged();
				}

				AcceptSceneObject(folderPath);
				if (ImGui.BeginPopupContextItem("##dir_context"))
				{
					SelectFolderRow(folderPath);
					if (ImGui.MenuItem("Open")) ShowFolder(folderPath);
					if (ImGui.MenuItem("Rename", "F2")) StartRename(folderPath);
					if (ImGui.MenuItem("Show in Explorer")) ShowInExplorer(Absolute(folderPath));
					ImGui.EndPopup();
				}
			}

			for (int index = 0; index < listedFiles.Count; index++)
			{
				string file = listedFiles[index];
				string assetPath = Combine(folder, file);
				bool isPrefab = assetPath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase);
				listedVariants.TryGetValue(assetPath, out string variantOf);
				IconKind iconKind = EditorIcons.KindOfFile(assetPath);
				if (iconKind == IconKind.Prefab && variantOf != null)
				{
					iconKind = IconKind.PrefabVariant;
				}

				NVector2 rowStart = ImGui.GetCursorScreenPos();
				if (!renameInTree && string.Equals(renaming, assetPath, StringComparison.OrdinalIgnoreCase))
				{
					DrawFileIcon(draw, rowStart, iconSize, assetPath, iconKind);
					ImGui.SetCursorScreenPos(rowStart + new NVector2(iconSize + 4f, -ImGui.GetStyle().FramePadding.Y));
					DrawRenameField();
					continue;
				}

				bool selected = selection.Contains(assetPath) || string.Equals(selectedFile, assetPath, StringComparison.OrdinalIgnoreCase);
				if (ImGui.Selectable("##file" + file, selected, ImGuiSelectableFlags.AllowDoubleClick))
				{
					Click(assetPath, index);
					if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						OpenAsset(assetPath);
					}
				}

				TestAnchors.RecordItem("project:" + assetPath);

				// Icon and name over the row (ScriptableObject assets with their class, variants with their base).
				DrawFileIcon(draw, rowStart, iconSize, assetPath, iconKind);
				draw.AddText(rowStart + new NVector2(iconSize + 6f, 0f), ImGui.GetColorU32(ImGuiCol.Text), file);
				if (variantOf != null)
				{
					draw.AddText(rowStart + new NVector2(iconSize + 6f + ImGui.CalcTextSize(file).X, 0f), ImGui.GetColorU32(ImGuiCol.TextDisabled), "  variant of " + variantOf);
				}
				if (listedAssetTypes.TryGetValue(assetPath, out (string name, bool found) stored))
				{
					string kind = stored.found ? "  " + stored.name : "  " + (stored.name ?? "?") + " (missing script)";
					draw.AddText(rowStart + new NVector2(iconSize + 6f + ImGui.CalcTextSize(file).X, 0f), ImGui.GetColorU32(stored.found ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled] : EditorStyle.Warning), kind);
				}

				if (string.Equals(assetPath, pinged, StringComparison.OrdinalIgnoreCase))
				{
					FlashPinged();
				}

				if (ImGui.BeginDragDropSource())
				{
					DraggedAsset = assetPath;
					// Dragging one of several selected files drags them all (on to a list: every one that fits is added).
					var dragged = new List<DragItem>();
					if (selection.Contains(assetPath) && selection.Count > 1)
					{
						foreach (string listed in listedFiles)
						{
							string path = Combine(folder, listed);
							if (selection.Contains(path))
							{
								dragged.Add(DragItem.FromFile(path));
							}
						}
					}
					else
					{
						dragged.Add(DragItem.FromFile(assetPath));
					}

					DragAndDrop.SetPayload(DragType, dragged);
					ImGui.EndDragDropSource();
				}

				if (ImGui.BeginPopupContextItem("##file_context"))
				{
					selectedFile = assetPath;
					editor.SelectAsset(assetPath);
					if (ImGui.MenuItem(isPrefab ? "Open Prefab" : "Open")) OpenAsset(assetPath);
					if (isPrefab)
					{
						if (ImGui.MenuItem("Create Prefab Variant")) editor.CreatePrefabVariant(assetPath);
						if (ImGui.MenuItem("Instantiate in Scene", "", false, editor.Scene != null)) editor.InstantiatePrefab(assetPath);
					}

					if (TextureAssets.IsImageFile(assetPath) && ImGui.MenuItem("Reimport"))
					{
						TextureAssets.Reimport(assetPath);
						AssetThumbnails.Invalidate(assetPath);
						editor.NotifySceneChanged();
					}

					if (ImGui.MenuItem("Rename", "F2")) StartRename(assetPath);
					if (ImGui.MenuItem("Show in Explorer")) ShowInExplorer(Absolute(assetPath));
					ImGui.EndPopup();
				}
			}

			// F2 renames the picked folder or the inspected file.
			if (renaming == null && ImGui.IsWindowFocused() && ImGui.IsKeyPressed(ImGuiKey.F2))
			{
				string target = selectedFolder ?? selectedFile;
				if (target != null)
				{
					StartRename(target);
				}
			}

			NVector2 rest = ImGui.GetContentRegionAvail();
			ImGui.InvisibleButton("##files_empty", new NVector2(Math.Max(1, rest.X), Math.Max(30, rest.Y)));
			TestAnchors.RecordItem("project:(empty)");
			if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
			{
				// Nothing selected: the inspector clears too.
				selection.Clear();
				selectedFile = null;
				selectedFolder = null;
				if (editor.SelectedAsset != null)
				{
					editor.ClearSelection();
				}
			}

			// A scene object dropped here becomes a prefab in this folder.
			AcceptSceneObject(folder);

			if (ImGui.BeginPopupContextItem("##files_context"))
			{
				if (ImGui.MenuItem("Create Folder")) StartCreate("Folder", "New Folder");
				if (ImGui.MenuItem("Create C# Script")) StartCreate("Script", "NewBehaviour");
				if (ImGui.MenuItem("Create C# ScriptableObject Script")) StartCreate("ScriptableObjectScript", "NewData");
				if (ImGui.BeginMenu("Create ScriptableObject"))
				{
					DrawScriptableObjectMenu();
					ImGui.EndMenu();
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Create Prefab")) StartCreate("Prefab", "NewPrefab");
				if (ImGui.MenuItem("Create Material")) StartCreate("Material", "NewMaterial");
				if (ImGui.MenuItem("Create Physics Material 2D")) StartCreate("PhysicsMaterial2D", "NewPhysicsMaterial");
				if (ImGui.MenuItem("Create Visual Effect Graph")) StartCreate("VFX", "NewVisualEffect");
				if (ImGui.MenuItem("Create Audio Project")) StartCreate("AudioProject", "Audio");
				if (ImGui.MenuItem("Create Input Configuration")) StartCreate("InputConfiguration", "Input");
				if (ImGui.MenuItem("Create Scene")) StartCreate("Scene", "NewScene");
				if (ImGui.MenuItem("Create UI Document (UXML)")) StartCreate("UXML", "NewDocument");
				if (ImGui.MenuItem("Create Style Sheet (USS)")) StartCreate("USS", "NewStyle");
				ImGui.Separator();
				if (ImGui.MenuItem("Show in Explorer")) ShowInExplorer(Absolute(folder));
				ImGui.EndPopup();
			}
		}

		/// <summary>A click on a file: Ctrl toggles it in the selection, Shift selects the range from the last click, else it alone.</summary>
		private void Click(string assetPath, int index)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			if (io.KeyCtrl)
			{
				if (!selection.Remove(assetPath))
				{
					selection.Add(assetPath);
				}

				if (selectedFile != null && selection.Count == 1 && !selection.Contains(selectedFile) && selectedFile != assetPath)
				{
					selection.Add(selectedFile);
				}
			}
			else if (io.KeyShift && selectionAnchor != null)
			{
				int anchor = listedFiles.FindIndex(file => string.Equals(Combine(folder, file), selectionAnchor, StringComparison.OrdinalIgnoreCase));
				selection.Clear();
				if (anchor < 0)
				{
					anchor = index;
				}

				for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
				{
					selection.Add(Combine(folder, listedFiles[i]));
				}
			}
			else
			{
				selection.Clear();
				selection.Add(assetPath);
				selectionAnchor = assetPath;
			}

			if (!io.KeyShift)
			{
				selectionAnchor = assetPath;
			}

			selectedFolder = null;
			if (selection.Contains(assetPath))
			{
				selectedFile = assetPath;
			}
			else
			{
				// Ctrl+click took the file out: the inspector shows another selected file, or nothing.
				selectedFile = null;
				foreach (string listed in listedFiles)
				{
					string path = Combine(folder, listed);
					if (selection.Contains(path))
					{
						selectedFile = path;
					}
				}
			}

			if (selectedFile != null)
			{
				editor.SelectAsset(selectedFile);
			}
			else
			{
				editor.ClearSelection();
			}
		}

		/// <summary>A folder row picked: the files' selection and the inspector clear.</summary>
		private void SelectFolderRow(string folderPath)
		{
			selectedFolder = folderPath;
			selection.Clear();
			selectedFile = null;
			if (editor.SelectedAsset != null)
			{
				editor.ClearSelection();
			}
		}

		/// <summary>
		///     The panel shows what the inspector shows: a GameObject selected elsewhere clears the files' highlight, an
		///     asset selected elsewhere (an object field ping) becomes the selection.
		/// </summary>
		private void FollowEditorSelection()
		{
			string inspected = editor.SelectedAsset;
			if (string.Equals(inspected, selectedFile, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			selectedFile = inspected;
			selection.Clear();
			if (inspected != null)
			{
				selection.Add(inspected);
				selectedFolder = null;
			}
		}

		/// <summary>A file's icon: the image itself for pictures (when it has a preview), else the icon of its kind.</summary>
		private static void DrawFileIcon(ImDrawListPtr draw, NVector2 rowStart, float iconSize, string assetPath, IconKind kind)
		{
			NVector2 min = rowStart + new NVector2(1f, 0f);
			if (kind == IconKind.Texture && AssetThumbnails.TryGet(assetPath, out int handle, out NVector2 uv0, out NVector2 uv1))
			{
				draw.AddImage(EditorGui.Texture(handle), min, min + new NVector2(iconSize), uv0, uv1);
				return;
			}

			EditorIcons.Draw(draw, min, iconSize, kind, EditorIcons.ColorOf(kind));
		}

		// ---------------------------------------------------------------- rename

		private void StartRename(string assetPath, bool inTree = false)
		{
			if (editor.IsPlaying)
			{
				Debug.LogWarning("[Editor] Stop play mode to rename assets.");
				return;
			}

			renaming = assetPath;
			renameInTree = inTree;
			bool isFolder = Directory.Exists(Absolute(assetPath));
			string name = Path.GetFileName(assetPath);
			renameBuffer = isFolder ? name : Path.GetFileNameWithoutExtension(name);
			renameStarted = true;
		}

		/// <summary>The name field of the row being renamed: Enter or a click elsewhere renames, Escape cancels.</summary>
		private void DrawRenameField()
		{
			if (renameStarted)
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X));
			bool enter = ImGui.InputText("##rename", ref renameBuffer, 256, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
			TestAnchors.RecordItem("rename");
			if (renameStarted)
			{
				renameStarted = false;
				return;
			}

			if (ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Escape))
			{
				renaming = null;
				return;
			}

			if (enter || ImGui.IsItemDeactivated())
			{
				// Renamed after the panel is drawn: the tree and the listing are being walked now.
				renameCommitted = true;
			}
		}

		private void CommitRename()
		{
			string path = renaming;
			renaming = null;
			if (path == null)
			{
				return;
			}

			string name = renameBuffer.Trim();
			bool isFolder = Directory.Exists(Absolute(path));
			if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..")
			{
				Debug.LogWarning("[Editor] Cannot rename " + path + ": '" + name + "' is not a valid name.");
				return;
			}

			int slash = path.LastIndexOf('/');
			string parent = slash >= 0 ? path.Substring(0, slash) : "";
			string target = Combine(parent, isFolder ? name : name + Path.GetExtension(path));
			if (string.Equals(target, path, StringComparison.Ordinal))
			{
				return;
			}

			string failure = editor.MoveAsset(path, target);
			if (failure != null)
			{
				Debug.LogWarning("[Editor] Cannot rename " + path + ": " + failure);
				return;
			}

			// The panel follows: the shown folder, the picked folder, the selection, the open tree nodes.
			foreach (string open in openFolders)
			{
				if (AssetDatabase.MovedPath(open, path, target) is string reopened)
				{
					openNext.Add(reopened);
				}
			}

			folder = AssetDatabase.MovedPath(folder, path, target) ?? folder;
			selectedFolder = AssetDatabase.MovedPath(selectedFolder, path, target) ?? selectedFolder;
			selectionAnchor = AssetDatabase.MovedPath(selectionAnchor, path, target) ?? selectionAnchor;
			var moved = new List<string>(selection);
			selection.Clear();
			foreach (string selected in moved)
			{
				selection.Add(AssetDatabase.MovedPath(selected, path, target) ?? selected);
			}

			selectedFile = editor.SelectedAsset;
			listedVersion = -1;
		}

		/// <summary>The pinged file's row (the last item): scrolled into view once, outlined while the ping lasts.</summary>
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

			System.Numerics.Vector4 color = EditorStyle.Selection;
			color.W = (float)(1.0 - age / PingSeconds);
			ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - new NVector2(2f, 1f), ImGui.GetItemRectMax() + new NVector2(2f, 1f), ImGui.GetColorU32(color), 3f, ImDrawFlags.None, 2f);
		}

		/// <summary>
		///     The Create ScriptableObject menu: the classes marked [CreateAssetMenu] at their menu paths, then every
		///     ScriptableObject class of the scripts.
		/// </summary>
		private void DrawScriptableObjectMenu()
		{
			List<Type> types = TypeSelector.ScriptableObjectTypes();
			if (types.Count == 0)
			{
				ImGui.TextDisabled("No ScriptableObject class yet: create one with");
				ImGui.TextDisabled("Create C# ScriptableObject Script.");
				return;
			}

			var marked = new List<(string[] path, int order, Type type, string fileName)>();
			foreach (Type type in types)
			{
				CreateAssetMenuAttribute menu = type.GetCustomAttribute<CreateAssetMenuAttribute>(false);
				if (menu != null)
				{
					string path = string.IsNullOrWhiteSpace(menu.menuName) ? type.Name : menu.menuName;
					marked.Add((path.Split('/', StringSplitOptions.RemoveEmptyEntries), menu.order, type, menu.fileName));
				}
			}

			marked.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.Compare(string.Join("/", a.path), string.Join("/", b.path), StringComparison.OrdinalIgnoreCase));
			DrawMenuLevel(marked, 0, 0, marked.Count);
			if (marked.Count > 0)
			{
				ImGui.Separator();
			}

			if (ImGui.BeginMenu("All ScriptableObject Classes"))
			{
				foreach (Type type in types)
				{
					if (ImGui.MenuItem(type.FullName))
					{
						StartCreateAsset(type, null);
					}
				}

				ImGui.EndMenu();
			}
		}

		/// <summary>Menu items of a sorted range of entries at one depth of their paths (shared prefixes become sub-menus).</summary>
		private void DrawMenuLevel(List<(string[] path, int order, Type type, string fileName)> entries, int depth, int from, int to)
		{
			int i = from;
			while (i < to)
			{
				(string[] path, int order, Type type, string fileName) entry = entries[i];
				if (entry.path.Length <= depth)
				{
					i++;
					continue;
				}

				if (entry.path.Length == depth + 1)
				{
					if (ImGui.MenuItem(entry.path[depth] + "##" + entry.type.FullName))
					{
						StartCreateAsset(entry.type, entry.fileName);
					}

					i++;
					continue;
				}

				// Every following entry with the same folder at this depth goes into one sub-menu.
				string name = entry.path[depth];
				int end = i + 1;
				while (end < to && entries[end].path.Length > depth + 1 && string.Equals(entries[end].path[depth], name, StringComparison.Ordinal))
				{
					end++;
				}

				if (ImGui.BeginMenu(name + "##" + depth))
				{
					DrawMenuLevel(entries, depth + 1, i, end);
					ImGui.EndMenu();
				}

				i = end;
			}
		}

		private void StartCreateAsset(Type type, string fileName)
		{
			createType = type;
			StartCreate("ScriptableObject", string.IsNullOrWhiteSpace(fileName) ? "New " + type.Name : fileName);
		}

		private void RefreshListing()
		{
			int scriptsVersion = editor.Scripts?.Version ?? 0;
			if (listedVersion == editor.AssetsVersion && listedFolder == folder && listedScriptsVersion == scriptsVersion)
			{
				return;
			}

			listedScriptsVersion = scriptsVersion;

			if (!string.Equals(listedFolder, folder, StringComparison.OrdinalIgnoreCase))
			{
				selection.Clear();
			}

			listedVersion = editor.AssetsVersion;
			listedFolder = folder;
			listedDirectories.Clear();
			listedFiles.Clear();
			string path = Absolute(folder);
			foreach (string directory in Directory.GetDirectories(path))
			{
				listedDirectories.Add(Path.GetFileName(directory));
			}

			foreach (string file in Directory.GetFiles(path))
			{
				if (!file.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase))
				{
					listedFiles.Add(Path.GetFileName(file));
				}
			}

			listedDirectories.Sort(StringComparer.OrdinalIgnoreCase);
			listedFiles.Sort(StringComparer.OrdinalIgnoreCase);
			listedAssetTypes.Clear();
			listedVariants.Clear();
			foreach (string file in listedFiles)
			{
				string assetPath = Combine(folder, file);
				if (assetPath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
				{
					Prefab basePrefab = Prefab.Load(assetPath)?.BasePrefab;
					if (basePrefab != null)
					{
						listedVariants[assetPath] = basePrefab.name;
					}

					continue;
				}

				if (ScriptableObjectAssets.IsAssetFile(assetPath))
				{
					string typeName = ScriptableObjectAssets.GetTypeName(assetPath);
					int comma = typeName?.IndexOf(',') ?? -1;
					string fullName = comma > 0 ? typeName.Substring(0, comma) : typeName;
					string shortName = fullName?.Substring(Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('+')) + 1);
					listedAssetTypes[assetPath] = (shortName, ScriptableObjectAssets.GetStoredType(assetPath) != null);
				}
			}
		}

		private void OpenAsset(string assetPath)
		{
			if (assetPath.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase))
			{
				editor.OpenScene(assetPath);
			}
			else if (assetPath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
			{
				editor.OpenPrefab(assetPath);
			}
			else if (assetPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) || assetPath.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
			{
				editor.OpenUIBuilder(assetPath);
			}
			else if (assetPath.EndsWith(LELEngine.VFX.VfxGraph.Extension, StringComparison.OrdinalIgnoreCase))
			{
				editor.OpenVfxGraph(assetPath);
			}
			else if (assetPath.EndsWith(LELEngine.Audio.AudioProject.Extension, StringComparison.OrdinalIgnoreCase))
			{
				editor.OpenAudioStudio(assetPath, null);
			}
			else if (EditorIcons.KindOfFile(assetPath) == IconKind.InputConfiguration)
			{
				editor.OpenInputManager(assetPath);
			}
			else if (AssetDatabase.GetAssetType(assetPath) != null && !assetPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
			{
				// Materials, textures, meshes, physics materials: edited in the inspector.
				editor.SelectAsset(assetPath);
			}
			else
			{
				// Scripts, shaders, materials: the user's default application (IDE, text editor).
				try
				{
					Process.Start(new ProcessStartInfo(Absolute(assetPath)) { UseShellExecute = true });
				}
				catch (Exception e)
				{
					Debug.LogWarning("[Editor] Cannot open " + assetPath + ": " + e.Message);
				}
			}
		}

		private void StartCreate(string kind, string defaultName)
		{
			createKind = kind;
			createName = defaultName;
			error = null;
			openCreatePopup = true;
		}

		private void DrawCreatePopup()
		{
			if (openCreatePopup)
			{
				ImGui.OpenPopup("Create asset");
				openCreatePopup = false;
			}

			if (!ImGui.BeginPopupModal("Create asset", ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			ImGui.Text("New " + (createKind == "ScriptableObject" && createType != null ? createType.Name + " asset" : createKind) + " in Assets/" + folder);
			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(320);
			bool enter = ImGui.InputText("##name", ref createName, 128, ImGuiInputTextFlags.EnterReturnsTrue);
			if (error != null)
			{
				ImGui.TextColored(EditorStyle.Error, error);
			}

			if (ImGui.Button("Create", new NVector2(100, 0)) || enter)
			{
				try
				{
					Create(createKind, createName.Trim());
					ImGui.CloseCurrentPopup();
				}
				catch (Exception e)
				{
					error = e.Message;
				}
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel", new NVector2(100, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		private void Create(string kind, string name)
		{
			if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				throw new ArgumentException("Invalid name.");
			}

			string target;
			switch (kind)
			{
				case "Folder":
					target = Absolute(Combine(folder, name));
					Directory.CreateDirectory(target);
					break;
				case "Script":
					string className = ScriptClassName(name);
					target = Absolute(Combine(folder, className + ".cs"));
					EnsureNew(target);
					File.WriteAllText(target, ScriptTemplate(className));
					break;
				case "ScriptableObjectScript":
				{
					string dataClass = ScriptClassName(name);
					target = Absolute(Combine(folder, dataClass + ".cs"));
					EnsureNew(target);
					File.WriteAllText(target, ScriptableObjectTemplate(dataClass));
					break;
				}
				case "ScriptableObject":
				{
					if (createType == null)
					{
						return;
					}

					target = Absolute(Combine(folder, name + ScriptableObjectAssets.Extension));
					EnsureNew(target);
					ScriptableObject asset = ScriptableObject.CreateInstance(createType);
					try
					{
						asset.Reset();
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}

					ScriptableObjectAssets.Save(asset, AssetDatabase.ToAssetPath(target));
					break;
				}
				case "Material":
				{
					target = Absolute(Combine(folder, name + Shaders.Material.Extension));
					EnsureNew(target);
					var material = new Shaders.Material(InternalStorage.GetOrCreateShader(Shaders.Material.DefaultShaderPath));
					if (!material.HasProperty("Color"))
					{
						material.SetColor("Color", new OpenTK.Mathematics.Color4(0.8f, 0.8f, 0.8f, 1f));
					}

					material.Save(AssetDatabase.ToAssetPath(target));
					InternalStorage.RegisterMaterial(material);
					break;
				}
				case "PhysicsMaterial2D":
				{
					target = Absolute(Combine(folder, name + PhysicsMaterial2D.Extension));
					EnsureNew(target);
					new PhysicsMaterial2D(name).Save(AssetDatabase.ToAssetPath(target));
					break;
				}
				case "VFX":
				{
					target = Absolute(Combine(folder, name + LELEngine.VFX.VfxGraph.Extension));
					EnsureNew(target);
					LELEngine.VFX.VfxGraph graph = LELEngine.VFX.VfxGraphBuilder.CreateDefault(name);
					graph.Save(AssetDatabase.ToAssetPath(target));
					break;
				}
				case "AudioProject":
				{
					target = Absolute(Combine(folder, name + LELEngine.Audio.AudioProject.Extension));
					EnsureNew(target);
					LELEngine.Audio.AudioProject audioProject = LELEngine.Audio.AudioProject.CreateDefault(name);
					audioProject.CreateEvent("New Event");
					audioProject.Save(AssetDatabase.ToAssetPath(target));
					break;
				}
				case "InputConfiguration":
				{
					target = Absolute(Combine(folder, name + ScriptableObjectAssets.Extension));
					EnsureNew(target);
					var configuration = ScriptableObject.CreateInstance<LELEngine.InputSystem.InputConfiguration>();
					configuration.Reset();
					ScriptableObjectAssets.Save(configuration, AssetDatabase.ToAssetPath(target));
					break;
				}
				case "Scene":
					target = Absolute(Combine(folder, name + SceneSerializer.Extension));
					EnsureNew(target);
					SceneSerializer.Save(DefaultScene.Create(editor.Project), target);
					break;
				case "Prefab":
					target = Absolute(Combine(folder, name + Prefab.Extension));
					EnsureNew(target);
					target = Absolute(editor.CreateEmptyPrefab(folder, name));
					break;
				case "UXML":
					target = Absolute(Combine(folder, name + ".uxml"));
					EnsureNew(target);
					File.WriteAllText(target,
						"<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n" +
						"    <Style src=\"" + name + ".uss\" />\n" +
						"    <ui:VisualElement name=\"root\" class=\"root\">\n" +
						"        <ui:Label text=\"Hello\" />\n" +
						"    </ui:VisualElement>\n" +
						"</ui:UXML>\n");
					string sheet = Absolute(Combine(folder, name + ".uss"));
					if (!File.Exists(sheet))
					{
						File.WriteAllText(sheet, ".root {\n    flex-grow: 1;\n}\n");
					}

					break;
				case "USS":
					target = Absolute(Combine(folder, name + ".uss"));
					EnsureNew(target);
					File.WriteAllText(target, ".root {\n    flex-grow: 1;\n}\n");
					break;
				default:
					return;
			}

			AssetDatabase.Refresh(true);
			editor.NotifyAssetsChanged();
			selectedFile = AssetDatabase.ToAssetPath(target);
			if (selectedFile != null && File.Exists(target))
			{
				editor.SelectAsset(selectedFile);
				if (kind == "VFX")
				{
					editor.OpenVfxGraph(selectedFile);
				}
				else if (kind == "AudioProject")
				{
					editor.OpenAudioStudio(selectedFile, null);
				}
				else if (kind == "InputConfiguration")
				{
					editor.OpenInputManager(selectedFile);
				}
			}
		}

		/// <summary>Drop target on the last item for hierarchy objects: the dropped object is saved as a prefab in <paramref name="targetFolder" />.</summary>
		private void AcceptSceneObject(string targetFolder)
		{
			if (!ImGui.BeginDragDropTarget())
			{
				return;
			}

			ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(HierarchyPanel.DragType, ImGuiDragDropFlags.AcceptBeforeDelivery);
			if (!payload.IsNull)
			{
				GameObject dragged = HierarchyPanel.Dragged;
				bool fits = dragged != null && !dragged.IsDestroyed && (dragged.prefabInstance == null || dragged.prefabInstance.Root == dragged) && !editor.IsPlaying;
				if (!fits)
				{
					ImGui.SetMouseCursor(ImGuiMouseCursor.NotAllowed);
				}
				else if (dragged != null)
				{
					ImGui.SetTooltip("Create a prefab of '" + dragged.Name + "' in Assets/" + targetFolder);
				}

				if (payload.IsDelivery() && fits)
				{
					// Made after the panel is drawn (the scene reloads).
					droppedObject = dragged;
					droppedObjectFolder = targetFolder;
				}
				else if (payload.IsDelivery() && dragged != null && dragged.prefabInstance != null)
				{
					Debug.LogWarning($"[Editor] '{dragged.Name}' is part of a prefab instance: drag its root, or unpack it first.");
				}
			}

			ImGui.EndDragDropTarget();
		}

		private static void EnsureNew(string path)
		{
			if (File.Exists(path))
			{
				throw new InvalidOperationException("A file with this name already exists.");
			}
		}

		private static string ScriptClassName(string name)
		{
			var builder = new System.Text.StringBuilder();
			foreach (char c in name)
			{
				if (char.IsLetterOrDigit(c) || c == '_')
				{
					builder.Append(c);
				}
			}

			string result = builder.Length > 0 ? builder.ToString() : "NewBehaviour";
			return char.IsDigit(result[0]) ? "_" + result : result;
		}

		private static string ScriptTemplate(string className)
		{
			return "using LELEngine;\nusing OpenTK.Mathematics;\n\npublic class " + className + " : Behaviour\n{\n" +
				"\tpublic float Speed = 1f;\n\n" +
				"\t[SerializeField] private Vector3 offset;\n\n" +
				"\tpublic override void Start()\n\t{\n\t}\n\n" +
				"\tpublic override void Update()\n\t{\n\t}\n}\n";
		}

		private static string ScriptableObjectTemplate(string className)
		{
			return "using System.Collections.Generic;\nusing LELEngine;\nusing OpenTK.Mathematics;\n\n" +
				"[CreateAssetMenu(fileName = \"New" + className + "\", menuName = \"Data/" + className + "\")]\n" +
				"public class " + className + " : ScriptableObject\n{\n" +
				"\tpublic string displayName = \"\";\n\n" +
				"\tpublic float value = 1f;\n\n" +
				"\tpublic List<string> tags = new List<string>();\n}\n";
		}

		private static void ShowInExplorer(string path)
		{
			try
			{
				string arguments = File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"";
				Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot open Explorer: " + e.Message);
			}
		}

		private string Absolute(string relative)
		{
			return relative.Length == 0 ? editor.Project.AssetsPath : Path.Combine(editor.Project.AssetsPath, relative.Replace('/', Path.DirectorySeparatorChar));
		}

		private static string Combine(string a, string b)
		{
			return a.Length == 0 ? b : a + "/" + b;
		}

		#endregion
	}
}
