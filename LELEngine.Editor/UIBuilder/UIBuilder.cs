using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.UIElements;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.UIBuilder
{
	/// <summary>
	///     The UI Builder: edits a UXML document (hierarchy, library of elements, inspector with attributes, inline
	///     styles and bindings, style sheets with a USS editor) with a live preview rendered by the runtime. Lives in
	///     its own window (<see cref="BuilderWindow" />).
	/// </summary>
	internal sealed partial class UIBuilder : IDisposable
	{
		#region PublicFields

		public UxmlDocument document { get; private set; } = new UxmlDocument();
		public bool dirty { get; private set; }
		public string title => (document.assetPath ?? "Untitled.uxml") + (dirty ? " *" : "");

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly BuilderWindow window;
		private readonly BuilderPreview preview = new BuilderPreview();
		private readonly List<Type> libraryTypes;
		private bool previewDirty = true;
		private string selectedPath;
		private readonly List<Snapshot> undoStack = new List<Snapshot>();
		private readonly List<Snapshot> redoStack = new List<Snapshot>();
		private Snapshot? editSnapshot;
		private string savedXml;
		private bool layoutBuilt;

		// Canvas
		private int canvasWidth = 1280;
		private int canvasHeight = 720;
		private float zoom;
		private bool interact;
		private NVector2 imageMin;
		private float displayScale = 1f;
		private UxmlNode hoveredNode;
		private bool draggingSelection;
		private NVector2 dragStartMouse;
		private float dragStartLeft, dragStartTop;
		private int dragMode; // 1 move, 2 resize
		private float dragStartWidth, dragStartHeight;
		private bool dragAnchorRight, dragAnchorBottom;
		private bool interactPressed;

		// USS editor
		private string ussAssetPath;
		private string ussText = "";
		private bool ussDirty;
		private StyleSheet ussSheet;
		private double ussEditedAt = -1;
		private bool ussApplied = true;

		// Popups
		private bool openSaveAsPopup;
		private string saveAsPath = "UI/NewDocument.uxml";
		private bool openCloseConfirm;
		private bool closeConfirmed;
		private bool openNewSheetPopup;
		private string newSheetName = "NewStyle";
		private string librarySearch = "";
		private string status = "";

		// Test input: one injected ImGui input event per frame (applied at the start of Draw).
		private readonly Queue<Action> injectedInput = new Queue<Action>();

		private struct Snapshot
		{
			public string xml;
			public string selection;
		}

		private const string NodePayload = "UXML_NODE";
		private const string TypePayload = "UXML_TYPE";

		#endregion

		#region Constructors

		public UIBuilder(EditorApplication editor, BuilderWindow window)
		{
			this.editor = editor;
			this.window = window;
			libraryTypes = UxmlRegistry.ElementTypes();
			libraryTypes.RemoveAll(t => !t.IsPublic || t == typeof(TemplateContainer) || t == typeof(TextElement));
		}

		#endregion

		#region PublicMethods

		public void Open(string assetPath)
		{
			try
			{
				string xml = File.ReadAllText(AssetDatabase.ToAbsolute(assetPath));
				document = UxmlDocument.Parse(xml, assetPath);
				savedXml = document.ToXml();
				selectedPath = null;
				dirty = false;
				undoStack.Clear();
				redoStack.Clear();
				previewDirty = true;
				status = "Opened " + assetPath;
				if (document.styleSheets.Count > 0 && ussAssetPath == null)
				{
					string sheet = document.ResolveSource(document.styleSheets[0]);
					if (sheet != null && File.Exists(AssetDatabase.ToAbsolute(sheet)))
					{
						OpenStyleSheet(sheet);
					}
				}
			}
			catch (Exception e)
			{
				status = "Cannot open " + assetPath + ": " + e.Message;
				Debug.LogError("[UI Builder] " + status);
			}
		}

		public void OpenStyleSheet(string assetPath)
		{
			try
			{
				ussText = File.ReadAllText(AssetDatabase.ToAbsolute(assetPath));
				ussAssetPath = assetPath;
				ussSheet = UIAssets.LoadStyleSheet(assetPath);
				ussDirty = false;
				ussApplied = true;
			}
			catch (Exception e)
			{
				status = "Cannot open " + assetPath + ": " + e.Message;
			}
		}

		public void NewDocument()
		{
			document = new UxmlDocument();
			var root = new UxmlNode { tag = "VisualElement" };
			root.SetAttribute("name", "root");
			root.SetAttribute("style", "flex-grow: 1;");
			document.root.children.Add(root);
			savedXml = document.ToXml();
			selectedPath = "0";
			dirty = false;
			undoStack.Clear();
			redoStack.Clear();
			previewDirty = true;
		}

		public bool Save()
		{
			if (string.IsNullOrEmpty(document.assetPath))
			{
				openSaveAsPopup = true;
				return false;
			}

			return SaveTo(document.assetPath);
		}

		/// <summary>The window asked to close: true to allow it (no unsaved changes, or confirmed).</summary>
		public bool RequestClose()
		{
			if (!dirty && !ussDirty || closeConfirmed)
			{
				return true;
			}

			openCloseConfirm = true;
			return false;
		}

		public void Draw()
		{
			if (injectedInput.Count > 0)
			{
				injectedInput.Dequeue()();
			}

			DrawMenuBar();
			HandleShortcuts();
			uint dockspace = ImGui.DockSpaceOverViewport();
			if (!layoutBuilt)
			{
				layoutBuilt = true;
				if (!File.Exists(Path.Combine(RecentProjects.EditorDataFolder, "builder-layout.ini")))
				{
					BuildDefaultLayout(dockspace);
				}
			}

			DrawHierarchy();
			DrawLibrary();
			DrawInspector();
			DrawStyleSheets();
			DrawUssEditor();
			DrawCanvas();
			DrawPopups();
			ApplyUssIfDue();
		}

		/// <summary>Rebuilds the preview when the document changed and renders it (builder GL context current).</summary>
		public void RenderPreview()
		{
			if (previewDirty)
			{
				previewDirty = false;
				preview.Rebuild(document);
			}

			preview.Render(canvasWidth, canvasHeight, 1f);
		}

		public void Dispose()
		{
			preview.Dispose();
		}

		/// <summary>
		///     Scripted editing for automated tests: select=0/2/1 (index path), pick=x,y (canvas point), add=Tag,
		///     style=property:value, attr=name=value, zoom=1, interact=1, undo, redo, delete, duplicate, save,
		///     saveas=path, new, sheet=path, uss=text (replaces the sheet text), report (logs the state).
		/// </summary>
		public void RunTestCommand(string command)
		{
			int eq = command.IndexOf('=');
			string name = eq > 0 ? command.Substring(0, eq) : command;
			string arg = eq > 0 ? command.Substring(eq + 1) : "";
			UxmlNode selected = Selected;
			switch (name)
			{
				case "select": selectedPath = arg; break;
				case "pick":
				{
					string[] xy = arg.Split(',');
					Select(preview.NodeAt(new Vector2(ParsePx(xy[0], 0f), ParsePx(xy[1], 0f))));
					break;
				}
				case "add": AddElement(arg, selected != null && !selected.isInstance ? selected : document.root); break;
				case "style":
				{
					int colon = arg.IndexOf(':');
					if (selected != null && colon > 0) Apply(() => selected.SetStyleProperty(arg.Substring(0, colon).Trim(), arg.Substring(colon + 1).Trim()));
					break;
				}
				case "attr":
				{
					int split = arg.IndexOf('=');
					if (selected != null && split > 0) Apply(() => selected.SetAttribute(arg.Substring(0, split), arg.Substring(split + 1)));
					break;
				}
				case "zoom": zoom = ParsePx(arg, 0f); break;
				case "interact": interact = arg == "1"; break;
				case "undo": Undo(); break;
				case "redo": Redo(); break;
				case "delete": if (selected != null) DeleteNode(selected); break;
				case "duplicate": if (selected != null) DuplicateNode(selected); break;
				case "save": Save(); break;
				case "saveas": SaveTo(arg); break;
				case "new": NewDocument(); break;
				case "sheet": OpenStyleSheet(arg); break;
				case "uss":
					ussText = arg.Replace("\\n", "\n");
					ussDirty = true;
					ussApplied = false;
					ussEditedAt = 0;
					break;
				case "ussreplace":
				{
					int arrow = arg.IndexOf("=>", StringComparison.Ordinal);
					if (arrow > 0)
					{
						ussText = ussText.Replace(arg.Substring(0, arrow), arg.Substring(arrow + 2));
						ussDirty = true;
						ussApplied = false;
						ussEditedAt = 0;
					}

					break;
				}
				case "savesheet": SaveStyleSheet(); break;
				case "mouse":
				{
					// mouse=x,y moves the (ImGui) mouse; click=x,y presses and releases there; drag=x1,y1,x2,y2.
					string[] xy = arg.Split(',');
					float x = ParsePx(xy[0], 0f), y = ParsePx(xy[1], 0f);
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x, y));
					break;
				}
				case "click":
				{
					string[] xy = arg.Split(',');
					float x = ParsePx(xy[0], 0f), y = ParsePx(xy[1], 0f);
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x, y));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, true));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, false));
					break;
				}
				case "drag":
				{
					string[] v = arg.Split(',');
					float x1 = ParsePx(v[0], 0f), y1 = ParsePx(v[1], 0f), x2 = ParsePx(v[2], 0f), y2 = ParsePx(v[3], 0f);
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x1, y1));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, true));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent((x1 + x2) * 0.5f, (y1 + y2) * 0.5f));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMousePosEvent(x2, y2));
					injectedInput.Enqueue(() => ImGui.GetIO().AddMouseButtonEvent(0, false));
					break;
				}
				case "report":
				{
					UxmlNode node = Selected;
					Rect bounds = node != null ? preview.BoundsOf(node) : Rect.zero;
					Debug.Log($"[UI Builder] selection={selectedPath ?? "none"} node={(node != null ? NodeLabel(node) : "none")} bounds={bounds.x},{bounds.y} {bounds.width}x{bounds.height} dirty={dirty} undo={undoStack.Count} redo={redoStack.Count} errors={preview.errors.Count} status=\"{status}\"");
					break;
				}
				case "gamereport":
				{
					// Compares the scene's documents with the preview: element arg's border color, transitions, sheet identity.
					foreach (UIDocument doc in UIRuntime.Documents)
					{
						VisualElement element = doc.rootVisualElement?.Q(arg);
						VisualElement previewElement = preview.panel.visualTree.Q(arg);
						bool sameSheet = false;
						for (VisualElement e = element; e != null && !sameSheet; e = e.parent)
						{
							for (int i = 0; i < e.styleSheets.count; i++)
							{
								if (ReferenceEquals(e.styleSheets[i], ussSheet)) sameSheet = true;
							}
						}

						Debug.Log($"[UI Builder] game '{arg}': border={(element != null ? element.resolvedStyle.borderTopColor.ToString() : "n/a")} running={(element?.transitionState?.running.Count ?? 0)} panelActive={doc.runtimePanel?.transitions.activeCount} sameSheet={sameSheet} panelTime={doc.runtimePanel?.timeMs} | preview border={(previewElement != null ? previewElement.resolvedStyle.borderTopColor.ToString() : "n/a")} previewActive={preview.panel.transitions.activeCount}");
					}

					break;
				}
				default: Debug.LogWarning("[UI Builder] Unknown test command: " + command); break;
			}
		}

		#endregion

		#region PrivateMethods

		// ------------------------------------------------------------------ document edits

		private UxmlNode Selected => selectedPath != null ? document.NodeAt(selectedPath) : null;

		private void Select(UxmlNode node)
		{
			selectedPath = node != null ? document.PathOf(node) : null;
		}

		private Snapshot TakeSnapshot()
		{
			return new Snapshot { xml = document.ToXml(), selection = selectedPath };
		}

		private void PushUndo()
		{
			undoStack.Add(TakeSnapshot());
			if (undoStack.Count > 100)
			{
				undoStack.RemoveAt(0);
			}

			redoStack.Clear();
		}

		private void Restore(Snapshot snapshot)
		{
			document = UxmlDocument.Parse(snapshot.xml, document.assetPath);
			selectedPath = snapshot.selection;
			Changed();
			dirty = snapshot.xml != savedXml;
		}

		private void Undo()
		{
			if (undoStack.Count == 0)
			{
				return;
			}

			redoStack.Add(TakeSnapshot());
			Snapshot snapshot = undoStack[undoStack.Count - 1];
			undoStack.RemoveAt(undoStack.Count - 1);
			Restore(snapshot);
		}

		private void Redo()
		{
			if (redoStack.Count == 0)
			{
				return;
			}

			undoStack.Add(TakeSnapshot());
			Snapshot snapshot = redoStack[redoStack.Count - 1];
			redoStack.RemoveAt(redoStack.Count - 1);
			Restore(snapshot);
		}

		/// <summary>An instant change (button, checkbox, combo): one undo step.</summary>
		private void Apply(Action change)
		{
			PushUndo();
			change();
			Changed();
		}

		/// <summary>
		///     A change from a continuous widget (text, drag): the undo step covers the whole edit session, from the
		///     widget's activation to its release.
		/// </summary>
		private void Track(bool changed, Action apply)
		{
			if (ImGui.IsItemActivated())
			{
				editSnapshot = TakeSnapshot();
			}

			if (changed)
			{
				apply();
				Changed();
			}

			if (ImGui.IsItemDeactivatedAfterEdit() && editSnapshot.HasValue)
			{
				undoStack.Add(editSnapshot.Value);
				redoStack.Clear();
				editSnapshot = null;
			}
		}

		private void Changed()
		{
			dirty = true;
			previewDirty = true;
		}

		private bool SaveTo(string assetPath)
		{
			try
			{
				assetPath = assetPath.Replace('\\', '/').TrimStart('/');
				if (!assetPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
				{
					assetPath += ".uxml";
				}

				string absolute = AssetDatabase.ToAbsolute(assetPath);
				Directory.CreateDirectory(Path.GetDirectoryName(absolute));
				document.assetPath = assetPath;
				savedXml = document.ToXml();
				File.WriteAllText(absolute, savedXml);
				AssetDatabase.EnsureMeta(assetPath);
				dirty = false;
				status = "Saved " + assetPath;
				editor.NotifyAssetsChanged();
				return true;
			}
			catch (Exception e)
			{
				status = "Save failed: " + e.Message;
				Debug.LogError("[UI Builder] " + status);
				return false;
			}
		}

		private void SaveStyleSheet()
		{
			if (ussAssetPath == null)
			{
				return;
			}

			try
			{
				File.WriteAllText(AssetDatabase.ToAbsolute(ussAssetPath), ussText);
				AssetDatabase.EnsureMeta(ussAssetPath);
				ussDirty = false;
				ApplyUss();
				status = "Saved " + ussAssetPath;
				editor.NotifyAssetsChanged();
			}
			catch (Exception e)
			{
				status = "Save failed: " + e.Message;
			}
		}

		private void ApplyUssIfDue()
		{
			if (ussApplied || ussEditedAt < 0 || ImGui.GetTime() - ussEditedAt < 0.25)
			{
				return;
			}

			ApplyUss();
		}

		/// <summary>Reparses the edited sheet in memory so the preview (and game views using it) restyle without saving.</summary>
		private void ApplyUss()
		{
			ussApplied = true;
			if (ussSheet == null)
			{
				return;
			}

			ussSheet.Reparse(ussText);
			preview.panel.InvalidateStyles();
			// The scene's documents share the cached sheet: their panels restyle too (live edit in the Game view).
			UIAssets.NotifyReloaded(ussSheet);
			editor.NotifySceneChanged();
		}

		private UxmlNode AddElement(string tag, UxmlNode parent)
		{
			var node = new UxmlNode { tag = tag };
			if (tag == "Label" || tag == "Button" || tag == "Toggle" || tag == "Foldout")
			{
				node.SetAttribute("text", tag);
			}

			if (tag == "TextField" || tag == "Slider" || tag == "SliderInt" || tag == "DropdownField" || tag == "IntegerField" || tag == "FloatField")
			{
				node.SetAttribute("label", tag);
			}

			parent ??= document.root;
			if (parent != document.root && parent.isInstance)
			{
				parent = document.ParentOf(parent) ?? document.root;
			}

			Apply(() => parent.children.Add(node));
			Select(node);
			return node;
		}

		private void DeleteNode(UxmlNode node)
		{
			UxmlNode parent = document.ParentOf(node);
			if (parent == null)
			{
				return;
			}

			Apply(() => parent.children.Remove(node));
			if (Selected == null)
			{
				selectedPath = null;
			}
		}

		private void DuplicateNode(UxmlNode node)
		{
			UxmlNode parent = document.ParentOf(node);
			if (parent == null)
			{
				return;
			}

			UxmlNode copy = node.Clone();
			Apply(() => parent.children.Insert(parent.children.IndexOf(node) + 1, copy));
			Select(copy);
		}

		private void MoveNode(UxmlNode node, int delta)
		{
			UxmlNode parent = document.ParentOf(node);
			if (parent == null)
			{
				return;
			}

			int index = parent.children.IndexOf(node);
			int target = index + delta;
			if (target < 0 || target >= parent.children.Count)
			{
				return;
			}

			Apply(() =>
			{
				parent.children.RemoveAt(index);
				parent.children.Insert(target, node);
			});
			Select(node);
		}

		private void Reparent(UxmlNode node, UxmlNode newParent, int index = -1)
		{
			if (node == null || newParent == null || node == newParent || IsAncestor(node, newParent))
			{
				return;
			}

			UxmlNode oldParent = document.ParentOf(node);
			if (oldParent == null)
			{
				return;
			}

			Apply(() =>
			{
				oldParent.children.Remove(node);
				if (index < 0 || index > newParent.children.Count) newParent.children.Add(node);
				else newParent.children.Insert(index, node);
			});
			Select(node);
		}

		private static bool IsAncestor(UxmlNode ancestor, UxmlNode node)
		{
			foreach (UxmlNode child in ancestor.children)
			{
				if (child == node || IsAncestor(child, node))
				{
					return true;
				}
			}

			return false;
		}

		// ------------------------------------------------------------------ menu, shortcuts, layout

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMainMenuBar())
			{
				return;
			}

			if (EditorMenu.Begin("File"))
			{
				if (EditorMenu.Item("New Document", "Ctrl+N")) NewDocument();
				if (EditorMenu.Begin("Open"))
				{
					foreach (string path in AssetDatabase.GetAllAssetPaths(".uxml"))
					{
						if (EditorMenu.Item(path)) Open(path);
					}

					EditorMenu.End();
				}

				ImGui.Separator();
				if (EditorMenu.Item("Save", "Ctrl+S")) Save();
				if (EditorMenu.Item("Save As...")) openSaveAsPopup = true;
				ImGui.Separator();
				if (EditorMenu.Item("Close")) window.Close();
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Edit"))
			{
				if (EditorMenu.Item("Undo", "Ctrl+Z", false, undoStack.Count > 0)) Undo();
				if (EditorMenu.Item("Redo", "Ctrl+Y", false, redoStack.Count > 0)) Redo();
				ImGui.Separator();
				UxmlNode selected = Selected;
				if (EditorMenu.Item("Duplicate", "Ctrl+D", false, selected != null)) DuplicateNode(selected);
				if (EditorMenu.Item("Delete", "Del", false, selected != null)) DeleteNode(selected);
				EditorMenu.End();
			}

			if (EditorMenu.Begin("View"))
			{
				if (EditorMenu.Item("1280 x 720")) SetCanvasSize(1280, 720);
				if (EditorMenu.Item("1920 x 1080")) SetCanvasSize(1920, 1080);
				if (EditorMenu.Item("800 x 600")) SetCanvasSize(800, 600);
				if (EditorMenu.Item("375 x 812 (phone)")) SetCanvasSize(375, 812);
				ImGui.Separator();
				if (EditorMenu.Item("Fit", "", zoom == 0f)) zoom = 0f;
				if (EditorMenu.Item("50%", "", zoom == 0.5f)) zoom = 0.5f;
				if (EditorMenu.Item("100%", "", zoom == 1f)) zoom = 1f;
				if (EditorMenu.Item("200%", "", zoom == 2f)) zoom = 2f;
				ImGui.Separator();
				bool theme = preview.useDefaultTheme;
				if (EditorMenu.Item("Default Theme", "", theme)) preview.useDefaultTheme = !theme;
				EditorMenu.End();
			}

			float width = ImGui.CalcTextSize(title).X + 16;
			ImGui.SetCursorPosX(ImGui.GetWindowWidth() - width - ImGui.CalcTextSize(status).X - 24);
			ImGui.TextDisabled(status);
			ImGui.SetCursorPosX(ImGui.GetWindowWidth() - width);
			ImGui.TextUnformatted(title);
			ImGui.EndMainMenuBar();
		}

		private void SetCanvasSize(int w, int h)
		{
			canvasWidth = w;
			canvasHeight = h;
		}

		private void HandleShortcuts()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			bool ctrl = io.KeyCtrl;
			if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
			{
				if (ussAssetPath != null && ussDirty && ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow) && ussEditorFocused) SaveStyleSheet();
				else Save();
			}

			if (io.WantTextInput)
			{
				return;
			}

			if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z, false)) Undo();
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y, false)) Redo();
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, false) && Selected != null) DuplicateNode(Selected);
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.N, false)) NewDocument();
			else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) && Selected != null && !interact) DeleteNode(Selected);
		}

		private unsafe void BuildDefaultLayout(uint dockspace)
		{
			ImGuiP.DockBuilderRemoveNode(dockspace);
			ImGuiP.DockBuilderAddNode(dockspace, (ImGuiDockNodeFlags)(1 << 10));
			ImGuiP.DockBuilderSetNodeSize(dockspace, ImGui.GetMainViewport().Size);

			uint center = dockspace;
			uint right = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.26f, null, &center);
			uint left = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.22f, null, &center);
			uint bottom = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.3f, null, &center);
			uint leftBottom = ImGuiP.DockBuilderSplitNode(left, ImGuiDir.Down, 0.45f, null, &left);
			uint rightBottom = ImGuiP.DockBuilderSplitNode(right, ImGuiDir.Down, 0.25f, null, &right);

			ImGuiP.DockBuilderDockWindow("Hierarchy", left);
			ImGuiP.DockBuilderDockWindow("Library", leftBottom);
			ImGuiP.DockBuilderDockWindow("Inspector", right);
			ImGuiP.DockBuilderDockWindow("StyleSheets", rightBottom);
			ImGuiP.DockBuilderDockWindow("USS Editor", bottom);
			ImGuiP.DockBuilderDockWindow("Canvas", center);
			ImGuiP.DockBuilderFinish(dockspace);
		}

		// ------------------------------------------------------------------ hierarchy

		private void DrawHierarchy()
		{
			if (ImGui.Begin("Hierarchy"))
			{
				ImGuiTreeNodeFlags rootFlags = ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
				bool open = EditorGui.TreeNodeEx(Path.GetFileName(document.assetPath ?? "Untitled.uxml") + "##docroot", rootFlags);
				if (EditorGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
				{
					selectedPath = null;
				}

				DropTarget(document.root);
				if (open)
				{
					foreach (UxmlNode node in document.root.children.ToArray())
					{
						DrawHierarchyNode(node);
					}

					ImGui.TreePop();
				}

				// Empty area: deselect, context menu to add at the root.
				NVector2 rest = ImGui.GetContentRegionAvail();
				EditorGui.InvisibleButton("##hierarchy_rest", new NVector2(Math.Max(1f, rest.X), Math.Max(24f, rest.Y)));
				if (EditorGui.IsItemClicked()) selectedPath = null;
				if (EditorGui.BeginPopupContextItem("##hierarchy_context"))
				{
					DrawAddMenu(document.root);
					ImGui.EndPopup();
				}

				DropTarget(document.root);
			}

			ImGui.End();
		}

		private void DrawHierarchyNode(UxmlNode node)
		{
			bool selected = Selected == node;
			ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
			if (selected) flags |= ImGuiTreeNodeFlags.Selected;
			if (node.children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf;

			string label = NodeLabel(node);
			bool open = EditorGui.TreeNodeEx(label + "##" + node.id, flags);
			if (EditorGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
			{
				Select(node);
			}

			if (ImGui.BeginDragDropSource())
			{
				int id = node.id;
				unsafe
				{
					ImGui.SetDragDropPayload(NodePayload, &id, sizeof(int));
				}

				ImGui.TextUnformatted(label);
				ImGui.EndDragDropSource();
			}

			DropTarget(node);

			if (EditorGui.BeginPopupContextItem("##node_context_" + node.id))
			{
				Select(node);
				if (!node.isInstance)
				{
					DrawAddMenu(node);
					ImGui.Separator();
				}

				if (EditorMenu.Item("Duplicate")) DuplicateNode(node);
				if (EditorMenu.Item("Delete")) DeleteNode(node);
				ImGui.Separator();
				if (EditorMenu.Item("Move Up")) MoveNode(node, -1);
				if (EditorMenu.Item("Move Down")) MoveNode(node, 1);
				UxmlNode parent = document.ParentOf(node);
				if (parent != null && parent != document.root && EditorMenu.Item("Move To Parent Level"))
				{
					UxmlNode grand = document.ParentOf(parent);
					Reparent(node, grand, grand.children.IndexOf(parent) + 1);
				}

				ImGui.EndPopup();
			}

			if (open)
			{
				foreach (UxmlNode child in node.children.ToArray())
				{
					DrawHierarchyNode(child);
				}

				ImGui.TreePop();
			}
		}

		private static string NodeLabel(UxmlNode node)
		{
			string label = node.tag;
			if (node.isInstance) label = "Instance " + node.GetAttribute("template");
			string name = node.name;
			if (!string.IsNullOrEmpty(name)) label += "  #" + name;
			string classes = node.GetAttribute("class");
			if (!string.IsNullOrEmpty(classes)) label += "  ." + classes.Trim().Replace(" ", " .");
			string text = node.GetAttribute("text");
			if (!string.IsNullOrEmpty(text)) label += "  \"" + (text.Length > 16 ? text.Substring(0, 16) + "..." : text) + "\"";
			return label;
		}

		private unsafe void DropTarget(UxmlNode target)
		{
			if (!ImGui.BeginDragDropTarget())
			{
				return;
			}

			ImGuiPayloadPtr nodePayload = ImGui.AcceptDragDropPayload(NodePayload);
			if (!nodePayload.IsNull && nodePayload.Data != null)
			{
				int id = *(int*)nodePayload.Data;
				UxmlNode dragged = document.FindById(id);
				if (dragged != null && !target.isInstance)
				{
					Reparent(dragged, target);
				}
			}

			ImGuiPayloadPtr typePayload = ImGui.AcceptDragDropPayload(TypePayload);
			if (!typePayload.IsNull && typePayload.Data != null)
			{
				string tag = System.Text.Encoding.UTF8.GetString((byte*)typePayload.Data, typePayload.DataSize).TrimEnd('\0');
				if (!target.isInstance)
				{
					AddElement(tag, target);
				}
			}

			ImGui.EndDragDropTarget();
		}

		private void DrawAddMenu(UxmlNode parent)
		{
			if (!EditorMenu.Begin("Add"))
			{
				return;
			}

			foreach (string group in new[] { "Containers", "Controls", "Custom" })
			{
				if (!EditorMenu.Begin(group))
				{
					continue;
				}

				foreach (Type type in libraryTypes)
				{
					if (GroupOf(type) != group) continue;
					if (EditorMenu.Item(type.Name)) AddElement(UxmlRegistry.TagOf(type), parent);
				}

				EditorMenu.End();
			}

			EditorMenu.End();
		}

		// ------------------------------------------------------------------ library

		private static string GroupOf(Type type)
		{
			if (type.Assembly != typeof(VisualElement).Assembly) return "Custom";
			if (type == typeof(VisualElement) || type == typeof(ScrollView) || type == typeof(Box) || type == typeof(GroupBox) || type == typeof(Foldout) || type == typeof(ListView))
			{
				return "Containers";
			}

			return "Controls";
		}

		private void DrawLibrary()
		{
			if (ImGui.Begin("Library"))
			{
				ImGui.SetNextItemWidth(-1);
				EditorGui.InputTextWithHint("##search", "Search", ref librarySearch, 64u);
				ImGui.TextDisabled("Double-click or drag into the hierarchy / canvas");
				foreach (string group in new[] { "Containers", "Controls", "Custom" })
				{
					bool any = false;
					foreach (Type type in libraryTypes)
					{
						if (GroupOf(type) == group && Matches(type.Name)) { any = true; break; }
					}

					if (!any)
					{
						continue;
					}

					if (!EditorGui.CollapsingHeader(group, ImGuiTreeNodeFlags.DefaultOpen))
					{
						continue;
					}

					foreach (Type type in libraryTypes)
					{
						if (GroupOf(type) != group || !Matches(type.Name))
						{
							continue;
						}

						EditorGui.Selectable("  " + type.Name);
						if (EditorGui.IsItemDoubleClicked())
						{
							UxmlNode parent = Selected;
							AddElement(UxmlRegistry.TagOf(type), parent != null && !parent.isInstance ? parent : document.root);
						}

						if (ImGui.BeginDragDropSource())
						{
							byte[] bytes = System.Text.Encoding.UTF8.GetBytes(UxmlRegistry.TagOf(type) + "\0");
							unsafe
							{
								fixed (byte* p = bytes)
								{
									ImGui.SetDragDropPayload(TypePayload, p, (nuint)bytes.Length);
								}
							}

							ImGui.TextUnformatted(type.Name);
							ImGui.EndDragDropSource();
						}
					}
				}
			}

			ImGui.End();
		}

		private bool Matches(string name)
		{
			return string.IsNullOrEmpty(librarySearch) || name.IndexOf(librarySearch, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		// ------------------------------------------------------------------ style sheets

		private void DrawStyleSheets()
		{
			if (ImGui.Begin("StyleSheets"))
			{
				ImGui.TextDisabled("Sheets of the document (<Style src>)");
				for (int i = 0; i < document.styleSheets.Count; i++)
				{
					string src = document.styleSheets[i];
					ImGui.PushID(i);
					string resolved = document.ResolveSource(src);
					bool exists = resolved != null && File.Exists(AssetDatabase.ToAbsolute(resolved));
					if (!exists) ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Error);
					ImGui.TextUnformatted(src);
					if (!exists) ImGui.PopStyleColor();
					ImGui.SameLine(ImGui.GetWindowWidth() - 110);
					if (EditorGui.SmallButton("Edit") && exists) OpenStyleSheet(resolved);
					ImGui.SameLine();
					if (EditorGui.SmallButton("Remove"))
					{
						int index = i;
						Apply(() => document.styleSheets.RemoveAt(index));
					}

					ImGui.PopID();
				}

				ImGui.Separator();
				if (EditorGui.BeginCombo("##addsheet", "Add existing..."))
				{
					foreach (string path in AssetDatabase.GetAllAssetPaths(".uss"))
					{
						if (EditorGui.Selectable(path))
						{
							string src = document.RelativeSource(path);
							Apply(() => document.styleSheets.Add(src));
							OpenStyleSheet(path);
						}
					}

					EditorGui.EndCombo();
				}

				ImGui.SameLine();
				if (EditorGui.Button("New..."))
				{
					newSheetName = Path.GetFileNameWithoutExtension(document.assetPath ?? "NewStyle");
					openNewSheetPopup = true;
				}

				UxmlNode selected = Selected;
				if (selected != null)
				{
					ImGui.Separator();
					ImGui.TextDisabled("Classes of the selected element");
					string classes = selected.GetAttribute("class") ?? "";
					ImGui.SetNextItemWidth(-1);
					bool changed = EditorGui.InputText("##classes", ref classes, 512u);
					Track(changed, () => selected.SetAttribute("class", classes.Trim()));
				}
			}

			ImGui.End();
		}

		private bool ussEditorFocused;

		private void DrawUssEditor()
		{
			if (ImGui.Begin("USS Editor"))
			{
				ussEditorFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
				if (ussAssetPath == null)
				{
					ImGui.TextDisabled("Open a style sheet from the StyleSheets panel.");
				}
				else
				{
					ImGui.TextUnformatted(ussAssetPath + (ussDirty ? " *" : ""));
					ImGui.SameLine();
					if (EditorGui.SmallButton("Save")) SaveStyleSheet();
					ImGui.SameLine();
					if (EditorGui.SmallButton("Revert")) OpenStyleSheet(ussAssetPath);
					ImGui.SameLine();
					if (EditorGui.SmallButton("Close")) ussAssetPath = null;
					if (ussSheet != null && ussSheet.errors.Count > 0)
					{
						ImGui.SameLine();
						ImGui.TextColored(EditorStyle.Error, ussSheet.errors.Count + " error(s): " + ussSheet.errors[0]);
					}

					if (ussAssetPath != null)
					{
						NVector2 size = ImGui.GetContentRegionAvail();
						if (EditorGui.InputTextMultiline("##uss", ref ussText, 1u << 20, size, ImGuiInputTextFlags.AllowTabInput))
						{
							ussDirty = true;
							ussApplied = false;
							ussEditedAt = ImGui.GetTime();
						}
					}
				}
			}

			ImGui.End();
		}

		// ------------------------------------------------------------------ canvas

		private void DrawCanvas()
		{
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(4, 4));
			bool visible = ImGui.Begin("Canvas");
			ImGui.PopStyleVar();
			if (visible)
			{
				ImGui.SetNextItemWidth(110);
				int w = canvasWidth, h = canvasHeight;
				if (EditorGui.InputInt("##cw", ref w, 0)) canvasWidth = Math.Clamp(w, 16, 8192);
				ImGui.SameLine();
				ImGui.TextUnformatted("x");
				ImGui.SameLine();
				ImGui.SetNextItemWidth(110);
				if (EditorGui.InputInt("##ch", ref h, 0)) canvasHeight = Math.Clamp(h, 16, 8192);
				ImGui.SameLine();
				ImGui.SetNextItemWidth(90);
				string zoomLabel = zoom == 0f ? "Fit" : (int)(zoom * 100) + "%";
				if (EditorGui.BeginCombo("##zoom", zoomLabel))
				{
					if (EditorGui.Selectable("Fit", zoom == 0f)) zoom = 0f;
					foreach (float z in new[] { 0.25f, 0.5f, 0.75f, 1f, 1.5f, 2f })
					{
						if (EditorGui.Selectable((int)(z * 100) + "%", zoom == z)) zoom = z;
					}

					EditorGui.EndCombo();
				}

				ImGui.SameLine();
				EditorGui.Checkbox("Interact", ref interact);
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Send the mouse to the preview (hover, clicks, scrolling) instead of selecting elements");
				if (preview.errors.Count > 0)
				{
					ImGui.SameLine();
					ImGui.TextColored(EditorStyle.Error, preview.errors[0]);
				}

				DrawCanvasImage();
			}

			ImGui.End();
		}

		private void DrawCanvasImage()
		{
			NVector2 avail = ImGui.GetContentRegionAvail();
			if (avail.X < 8 || avail.Y < 8 || preview.Texture == 0)
			{
				return;
			}

			float scale = zoom > 0f ? zoom : Math.Min(avail.X / canvasWidth, avail.Y / canvasHeight);
			scale = Math.Max(0.05f, scale);
			var size = new NVector2(canvasWidth * scale, canvasHeight * scale);
			ImGui.BeginChild("##canvas_scroll", avail, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoMove);
			NVector2 origin = ImGui.GetCursorScreenPos();
			var offset = new NVector2(Math.Max(0f, (avail.X - size.X) * 0.5f), Math.Max(0f, (avail.Y - size.Y) * 0.5f));
			ImGui.SetCursorScreenPos(origin + offset);
			imageMin = ImGui.GetCursorScreenPos();
			displayScale = scale;
			ImGui.Image(EditorGui.Texture(preview.Texture), size, new NVector2(0, 1), new NVector2(1, 0));
			ImGui.SetCursorScreenPos(imageMin);
			EditorGui.InvisibleButton("##canvas_input", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
			bool hovered = ImGui.IsItemHovered();
			NVector2 mouse = ImGui.GetMousePos();
			var panelPoint = new Vector2((mouse.X - imageMin.X) / scale, (mouse.Y - imageMin.Y) / scale);

			if (interact)
			{
				ForwardInput(hovered, panelPoint);
			}
			else
			{
				HandleSelection(hovered, panelPoint);
			}

			DropTargetCanvas(panelPoint);
			DrawOverlays();
			ImGui.EndChild();
		}

		private void ForwardInput(bool hovered, Vector2 panelPoint)
		{
			RuntimePanel panel = preview.panel;
			ImGuiIOPtr io = ImGui.GetIO();
			int pressed = (ImGui.IsMouseDown(ImGuiMouseButton.Left) ? 1 : 0) | (ImGui.IsMouseDown(ImGuiMouseButton.Right) ? 2 : 0);
			var modifiers = EventModifiers.None;
			if (io.KeyShift) modifiers |= EventModifiers.Shift;
			if (io.KeyCtrl) modifiers |= EventModifiers.Control;
			if (io.KeyAlt) modifiers |= EventModifiers.Alt;
			if (hovered || interactPressed)
			{
				panel.ProcessPointerMove(panelPoint, pressed, modifiers);
				if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { panel.ProcessPointerDown(panelPoint, 0, pressed, modifiers); interactPressed = true; }
				if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) { panel.ProcessPointerUp(panelPoint, 0, pressed, modifiers); interactPressed = false; }
				if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) panel.ProcessPointerDown(panelPoint, 1, pressed, modifiers);
				if (ImGui.IsMouseReleased(ImGuiMouseButton.Right)) panel.ProcessPointerUp(panelPoint, 1, pressed, modifiers);
				if (io.MouseWheel != 0f) panel.ProcessWheel(panelPoint, new Vector2(0f, io.MouseWheel), modifiers);
			}
			else
			{
				panel.ProcessPointerLeave(modifiers);
			}

			// Keyboard goes to the preview while the canvas is focused (typing into its text fields).
			if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
			{
				ForwardKeyboard(panel, io, modifiers);
			}

			hoveredNode = null;
		}

		private static readonly (ImGuiKey imgui, OpenTK.Windowing.GraphicsLibraryFramework.Keys key)[] ForwardedKeys =
		{
			(ImGuiKey.Backspace, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Backspace), (ImGuiKey.Delete, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Delete),
			(ImGuiKey.Enter, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Enter), (ImGuiKey.Tab, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Tab),
			(ImGuiKey.Escape, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Escape), (ImGuiKey.Space, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Space),
			(ImGuiKey.LeftArrow, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Left), (ImGuiKey.RightArrow, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Right),
			(ImGuiKey.UpArrow, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Up), (ImGuiKey.DownArrow, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Down),
			(ImGuiKey.Home, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Home), (ImGuiKey.End, OpenTK.Windowing.GraphicsLibraryFramework.Keys.End),
			(ImGuiKey.A, OpenTK.Windowing.GraphicsLibraryFramework.Keys.A), (ImGuiKey.C, OpenTK.Windowing.GraphicsLibraryFramework.Keys.C),
			(ImGuiKey.V, OpenTK.Windowing.GraphicsLibraryFramework.Keys.V), (ImGuiKey.X, OpenTK.Windowing.GraphicsLibraryFramework.Keys.X),
			(ImGuiKey.Z, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Z), (ImGuiKey.Y, OpenTK.Windowing.GraphicsLibraryFramework.Keys.Y)
		};

		private unsafe void ForwardKeyboard(RuntimePanel panel, ImGuiIOPtr io, EventModifiers modifiers)
		{
			foreach ((ImGuiKey imgui, OpenTK.Windowing.GraphicsLibraryFramework.Keys key) pair in ForwardedKeys)
			{
				// Letters only as shortcuts (Ctrl+A/C/V/X/Z/Y); plain letters arrive as text.
				bool letter = pair.imgui >= ImGuiKey.A && pair.imgui <= ImGuiKey.Z;
				if (letter && !io.KeyCtrl) continue;
				if (ImGui.IsKeyPressed(pair.imgui)) panel.ProcessKeyDown(pair.key, modifiers);
				if (ImGui.IsKeyReleased(pair.imgui)) panel.ProcessKeyUp(pair.key, modifiers);
			}

			ImVector<uint> characters = io.InputQueueCharacters;
			for (int i = 0; i < characters.Size; i++)
			{
				panel.ProcessTextInput((char)characters.Data[i], modifiers);
			}
		}

		private void HandleSelection(bool hovered, Vector2 panelPoint)
		{
			UxmlNode selected = Selected;
			hoveredNode = hovered ? preview.NodeAt(panelPoint) : null;
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				// Resize handle of the selection first, then the element under the mouse.
				Rect bounds = selected != null ? preview.BoundsOf(selected) : Rect.zero;
				bool onHandle = selected != null && !bounds.IsEmpty && Vector2.Distance(panelPoint, bounds.max) * displayScale < 8f;
				if (!onHandle)
				{
					Select(hoveredNode);
					selected = Selected;
					bounds = selected != null ? preview.BoundsOf(selected) : Rect.zero;
				}

				if (selected != null && !bounds.IsEmpty && IsAbsolute(selected))
				{
					// Moves keep the element's anchoring: one anchored by right / bottom edits those instead of left / top.
					VisualElement element = preview.ElementOf(selected);
					ComputedStyle cs = element.computedStyle;
					Rect layout = element.layout;
					Rect parentLayout = element.hierarchy.parent?.layout ?? layout;
					dragAnchorRight = cs.left.IsAuto && !cs.right.IsAuto;
					dragAnchorBottom = cs.top.IsAuto && !cs.bottom.IsAuto;
					dragMode = onHandle ? 2 : 1;
					draggingSelection = true;
					dragStartMouse = ImGui.GetMousePos();
					dragStartLeft = dragAnchorRight ? parentLayout.width - layout.xMax : layout.x;
					dragStartTop = dragAnchorBottom ? parentLayout.height - layout.yMax : layout.y;
					dragStartWidth = layout.width;
					dragStartHeight = layout.height;
					editSnapshot = TakeSnapshot();
				}
			}

			if (draggingSelection && selected != null)
			{
				NVector2 delta = (ImGui.GetMousePos() - dragStartMouse) / displayScale;
				if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
				{
					if (delta.LengthSquared() > 0.25f)
					{
						if (dragMode == 1)
						{
							if (dragAnchorRight) selected.SetStyleProperty("right", Px(dragStartLeft - delta.X));
							else selected.SetStyleProperty("left", Px(dragStartLeft + delta.X));
							if (dragAnchorBottom) selected.SetStyleProperty("bottom", Px(dragStartTop - delta.Y));
							else selected.SetStyleProperty("top", Px(dragStartTop + delta.Y));
						}
						else
						{
							selected.SetStyleProperty("width", Px(Math.Max(1f, dragStartWidth + delta.X)));
							selected.SetStyleProperty("height", Px(Math.Max(1f, dragStartHeight + delta.Y)));
						}

						Changed();
					}
				}
				else
				{
					draggingSelection = false;
					if (delta.LengthSquared() > 0.25f && editSnapshot.HasValue)
					{
						undoStack.Add(editSnapshot.Value);
						redoStack.Clear();
					}

					editSnapshot = null;
				}
			}

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && hoveredNode != null)
			{
				Select(hoveredNode);
				ImGui.OpenPopup("##canvas_context");
			}

			if (ImGui.BeginPopup("##canvas_context"))
			{
				UxmlNode node = Selected;
				if (node != null)
				{
					ImGui.TextDisabled(NodeLabel(node));
					ImGui.Separator();
					if (!node.isInstance) DrawAddMenu(node);
					if (EditorMenu.Item("Duplicate")) DuplicateNode(node);
					if (EditorMenu.Item("Delete")) DeleteNode(node);
				}

				ImGui.EndPopup();
			}
		}

		private unsafe void DropTargetCanvas(Vector2 panelPoint)
		{
			if (!ImGui.BeginDragDropTarget())
			{
				return;
			}

			ImGuiPayloadPtr typePayload = ImGui.AcceptDragDropPayload(TypePayload);
			if (!typePayload.IsNull && typePayload.Data != null)
			{
				string tag = System.Text.Encoding.UTF8.GetString((byte*)typePayload.Data, typePayload.DataSize).TrimEnd('\0');
				UxmlNode target = preview.NodeAt(panelPoint);
				while (target != null && target.isInstance)
				{
					target = document.ParentOf(target);
				}

				AddElement(tag, target ?? document.root);
			}

			ImGui.EndDragDropTarget();
		}

		private void DrawOverlays()
		{
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			UxmlNode selected = Selected;
			if (hoveredNode != null && hoveredNode != selected)
			{
				DrawBounds(draw, preview.BoundsOf(hoveredNode), ImGui.GetColorU32(new NVector4(0.5f, 0.8f, 1f, 0.9f)), 1f, null);
			}

			if (selected != null)
			{
				Rect bounds = preview.BoundsOf(selected);
				DrawBounds(draw, bounds, ImGui.GetColorU32(EditorStyle.Selection), 2f, NodeLabel(selected) + "  " + (int)bounds.width + " x " + (int)bounds.height);
				if (!bounds.IsEmpty && IsAbsolute(selected))
				{
					NVector2 corner = imageMin + new NVector2(bounds.xMax, bounds.yMax) * displayScale;
					draw.AddRectFilled(corner - new NVector2(4, 4), corner + new NVector2(4, 4), ImGui.GetColorU32(EditorStyle.Selection));
				}
			}
		}

		private void DrawBounds(ImDrawListPtr draw, Rect bounds, uint color, float thickness, string label)
		{
			if (bounds.IsEmpty)
			{
				return;
			}

			NVector2 min = imageMin + new NVector2(bounds.x, bounds.y) * displayScale;
			NVector2 max = imageMin + new NVector2(bounds.xMax, bounds.yMax) * displayScale;
			draw.AddRect(min, max, color, 0f, ImDrawFlags.None, thickness);
			if (label != null)
			{
				NVector2 textSize = ImGui.CalcTextSize(label);
				NVector2 pos = new NVector2(min.X, min.Y - textSize.Y - 4);
				if (pos.Y < imageMin.Y) pos.Y = max.Y + 2;
				draw.AddRectFilled(pos - new NVector2(2, 1), pos + textSize + new NVector2(2, 1), ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.7f)));
				draw.AddText(pos, color, label);
			}
		}

		private bool IsAbsolute(UxmlNode node)
		{
			VisualElement element = preview.ElementOf(node);
			return element != null && element.resolvedStyle.position == Position.Absolute;
		}

		private static float ParsePx(string value, float fallback)
		{
			if (string.IsNullOrEmpty(value))
			{
				return fallback;
			}

			float number;
			return float.TryParse(value.Replace("px", "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) ? number : fallback;
		}

		private static string Px(float value)
		{
			return ((int)Math.Round(value)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
		}

		// ------------------------------------------------------------------ popups

		private void DrawPopups()
		{
			if (openSaveAsPopup)
			{
				ImGui.OpenPopup("Save document as");
				saveAsPath = document.assetPath ?? saveAsPath;
				openSaveAsPopup = false;
			}

			if (openCloseConfirm)
			{
				ImGui.OpenPopup("Unsaved changes##builder");
				openCloseConfirm = false;
			}

			if (openNewSheetPopup)
			{
				ImGui.OpenPopup("New style sheet");
				openNewSheetPopup = false;
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Save document as", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("Asset path (inside the project's Assets folder):");
				ImGui.SetNextItemWidth(420);
				EditorGui.InputText("##path", ref saveAsPath, 260u);
				if (EditorGui.Button("Save", new NVector2(110, 0)) && !string.IsNullOrWhiteSpace(saveAsPath))
				{
					ImGui.CloseCurrentPopup();
					SaveTo(saveAsPath);
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0))) ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Unsaved changes##builder", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("The document or style sheet has unsaved changes.");
				if (EditorGui.Button("Save", new NVector2(110, 0)))
				{
					if (ussDirty) SaveStyleSheet();
					if (!dirty || Save())
					{
						closeConfirmed = true;
						ImGui.CloseCurrentPopup();
						window.Close();
					}
				}

				ImGui.SameLine();
				if (EditorGui.Button("Don't save", new NVector2(110, 0)))
				{
					closeConfirmed = true;
					ImGui.CloseCurrentPopup();
					window.Close();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0))) ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("New style sheet", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("Name (created next to the document):");
				ImGui.SetNextItemWidth(300);
				EditorGui.InputText("##sheetname", ref newSheetName, 120u);
				if (EditorGui.Button("Create", new NVector2(110, 0)) && !string.IsNullOrWhiteSpace(newSheetName))
				{
					ImGui.CloseCurrentPopup();
					CreateStyleSheet(newSheetName.Trim());
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel", new NVector2(110, 0))) ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
			}
		}

		private void CreateStyleSheet(string name)
		{
			string folder = document.assetPath != null ? Path.GetDirectoryName(document.assetPath)?.Replace('\\', '/') ?? "" : "UI";
			string assetPath = (folder.Length > 0 ? folder + "/" : "") + name + ".uss";
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(absolute));
				if (!File.Exists(absolute))
				{
					File.WriteAllText(absolute, ".root {\n    flex-grow: 1;\n}\n");
				}

				AssetDatabase.Refresh(true);
				string src = document.RelativeSource(assetPath);
				Apply(() => document.styleSheets.Add(src));
				OpenStyleSheet(assetPath);
				editor.NotifyAssetsChanged();
			}
			catch (Exception e)
			{
				status = "Cannot create " + assetPath + ": " + e.Message;
			}
		}

		#endregion
	}
}
