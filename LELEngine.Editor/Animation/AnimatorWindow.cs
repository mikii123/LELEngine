using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using LELEngine.Editor.Graphs;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The Animator window (Window > Animator, or open a .animator asset): a toolbar with the breadcrumbs of the
	///     machine shown and the layer, the layers and parameters on the left, the state machine graph in the middle
	///     (<see cref="AnimatorGraph" /> on <see cref="GraphView" />), the inspector of the selection on the right and
	///     the controller's problems at the bottom. In play mode the window follows the selected object's Animator
	///     (or the first using the controller): the current state, transition and blend weights are highlighted and
	///     the parameters show and edit the live values. Edits are undoable and apply live; Ctrl+S saves the asset.
	/// </summary>
	internal sealed class AnimatorWindow : IEditorPanel
	{
		#region PublicFields

		public const string Title = "Animator";
		public bool Open;

		string IEditorPanel.PanelName => Title;
		string IEditorPanel.ImGuiName => "###animator";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(1280, 800);
		void IEditorPanel.Focus() => editor.OpenAnimator(document?.AssetPath);
		public bool IsFocused { get; private set; }
		public AnimatorDocument Document => document;
		public AnimatorGraph Graph => graph;
		public GraphView View => graph.View;

		/// <summary>The animator the window follows in play mode (null outside play mode or without one); looked up on demand.</summary>
		public Animator LiveAnimator
		{
			get
			{
				UpdateLive();
				return liveAnimator;
			}
		}

		private Animator liveAnimator;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly AnimatorGraph graph;
		private readonly AnimatorInspector inspector;
		private AnimatorDocument document;
		private bool focusRequested;
		private bool showSidebar = true;
		private bool showInspector = true;
		private bool showProblems = true;
		private bool closeRequested;
		private bool openUnsavedPopup;
		private List<string> problems = new List<string>();
		private int problemsVersion = -1;
		private byte[] reloadSnapshot;
		private bool reloadDirty;
		private string reloadPath;

		#endregion

		#region Constructors

		public AnimatorWindow(EditorApplication editor)
		{
			this.editor = editor;
			graph = new AnimatorGraph(this);
			inspector = new AnimatorInspector(editor, this);
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens the window on a controller asset (null keeps the current one).</summary>
		public void Show(string assetPath)
		{
			Open = true;
			focusRequested = true;
			if (assetPath != null && (document == null || !string.Equals(document.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase)))
			{
				AnimatorController controller = AnimatorController.Load(assetPath);
				SetDocument(controller);
			}
		}

		/// <summary>Shows a controller that is loaded already (the Animator component's "Open" button).</summary>
		public void Show(AnimatorController controller)
		{
			Open = true;
			focusRequested = true;
			if (controller != null && (document == null || document.Controller != controller))
			{
				SetDocument(controller);
			}
		}

		/// <summary>Shows a state's layer and machine, selecting the state.</summary>
		public void ShowState(AnimatorController controller, AnimatorState state)
		{
			Show(controller);
			AnimatorState found = controller.FindState(state.id, out AnimatorStateMachine machine, out AnimatorLayer layer);
			if (found != null)
			{
				graph.ShowMachine(layer, machine);
				graph.View.SelectOnly(found.id);
				graph.View.FrameNode(found.id);
			}
		}

		public void Close()
		{
			closeRequested = true;
		}

		/// <summary>Called by the undo records: the controller object changed under the window.</summary>
		public void OnControllerRestored(AnimatorController controller)
		{
			if (document != null && document.Controller == controller)
			{
				document.OnRestored();
				problemsVersion = -1;
			}
		}

		/// <summary>An inspector widget (through the edit tracker) changed the controller.</summary>
		public void OnControllerEdited(AnimatorController controller)
		{
			if (document != null && document.Controller == controller)
			{
				document.Changed(false);
				problemsVersion = -1;
			}
		}

		/// <summary>Drops the open document (project closed).</summary>
		public void Reset()
		{
			document = null;
			graph.Page = new AnimatorPage();
			graph.View.ClearSelection();
			Open = false;
			liveAnimator = null;
		}

		/// <summary>Before the scripts reload: the document (which may hold script types) is kept as bytes.</summary>
		public void OnDomainUnloading()
		{
			reloadSnapshot = document != null && document.Dirty ? document.Controller.ToBytes() : null;
			reloadDirty = document != null && document.Dirty;
			reloadPath = document?.AssetPath;
			document = null;
			graph.Page = new AnimatorPage();
			graph.View.ClearSelection();
			liveAnimator = null;
			AnimatorController.ClearCache();
			AnimationClip.ClearCache();
		}

		/// <summary>After the scripts reloaded: the document is loaded again (unsaved edits restored).</summary>
		public void OnDomainLoaded()
		{
			if (reloadPath == null)
			{
				return;
			}

			AnimatorController controller = AnimatorController.Load(reloadPath);
			if (controller != null && reloadSnapshot != null)
			{
				controller.ApplyBytes(reloadSnapshot);
			}

			SetDocument(controller);
			if (reloadDirty)
			{
				document?.Changed(false);
			}

			reloadSnapshot = null;
			reloadPath = null;
		}

		/// <summary>Navigates the canvas to a machine of the document's controller.</summary>
		public void NavigateTo(AnimatorStateMachine machine)
		{
			if (document == null || machine == null)
			{
				return;
			}

			document.Controller.FindMachine(machine.id, out AnimatorLayer layer);
			if (layer != null)
			{
				graph.ShowMachine(layer, machine);
			}
		}

		public void NavigateToTree(AnimatorState state, BlendTree tree, List<int> path)
		{
			if (document == null)
			{
				return;
			}

			AnimatorPage page = graph.Page;
			if (page.External != null)
			{
				page.External.FindState(state.id, out AnimatorStateMachine externalMachine, out _);
				if (externalMachine != null)
				{
					graph.ShowTree(page.Layer, externalMachine, state, tree, path);
				}

				return;
			}

			document.Controller.FindState(state.id, out AnimatorStateMachine machine, out AnimatorLayer layer);
			if (layer != null)
			{
				graph.ShowTree(layer, machine, state, tree, path);
			}
		}

		/// <summary>Looks into a sub-machine node that plays another asset (read-only): its root machine, or <paramref name="machine" /> of that asset.</summary>
		public void NavigateToExternal(AnimatorStateMachine embedding, AnimatorStateMachine machine = null)
		{
			if (document == null || embedding == null || embedding.controller == null)
			{
				return;
			}

			document.Controller.FindMachine(embedding.id, out AnimatorLayer layer);
			if (layer != null)
			{
				graph.ShowExternal(layer, embedding, machine);
			}
		}

		public void Draw()
		{
			if (!Open || editor.Project == null)
			{
				IsFocused = false;
				liveAnimator = null;
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(1280, 800), ImGuiCond.FirstUseEver);
			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			bool open = Open;
			string title = Title + (document != null ? " - " + Path.GetFileNameWithoutExtension(document.AssetPath ?? "untitled") + (document.Dirty ? " *" : "") : "") + "###animator";
			bool visible = ImGui.Begin(title, ref open, ImGuiWindowFlags.MenuBar);
			if (!Automation.WindowCapture.Active)
			{
				IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			}

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
			if (!open)
			{
				closeRequested = true;
			}

			if (closeRequested)
			{
				closeRequested = false;
				if (document != null && document.Dirty)
				{
					openUnsavedPopup = true;
					Open = true;
				}
				else
				{
					Open = false;
				}
			}

			DrawUnsavedPopup();
		}

		#endregion

		#region PrivateMethods

		private void SetDocument(AnimatorController controller)
		{
			if (controller == null)
			{
				document = null;
				graph.Page = new AnimatorPage();
				return;
			}

			document = new AnimatorDocument(editor, controller);
			document.Restored += () =>
			{
				graph.ResolvePage();
				graph.View.ClearSelection();
				inspector.Reset();
			};
			graph.Page = new AnimatorPage();
			graph.View.ClearSelection();
			if (controller.layers.Count > 0)
			{
				graph.ShowLayer(controller.layers[0]);
			}

			inspector.Reset();
			problemsVersion = -1;
		}

		private void DrawContent()
		{
			DrawMenuBar();
			if (document == null)
			{
				ImGui.TextDisabled("No controller open: double click a .animator asset in the Project panel, or create one (right click > Create Animator Controller).");
				return;
			}

			UpdateLive();
			HandleShortcuts();
			DrawToolbar();
			RefreshProblems();

			float bottom = showProblems && problems.Count > 0 ? Math.Min(120f, 28f + problems.Count * ImGui.GetTextLineHeightWithSpacing()) : 0f;
			NVector2 avail = ImGui.GetContentRegionAvail();
			float height = Math.Max(100f, avail.Y - bottom);
			float sidebarWidth = showSidebar ? 270f : 0f;
			float inspectorWidth = showInspector ? Math.Max(300f, avail.X * 0.26f) : 0f;
			float canvasWidth = Math.Max(100f, avail.X - sidebarWidth - inspectorWidth - (showSidebar ? 4f : 0f) - (showInspector ? 4f : 0f));

			if (showSidebar)
			{
				if (ImGui.BeginChild("##animator_sidebar", new NVector2(sidebarWidth, height), ImGuiChildFlags.Borders))
				{
					inspector.DrawSidebar();
				}

				ImGui.EndChild();
				ImGui.SameLine();
			}

			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			bool canvasOpen = ImGui.BeginChild("##animator_canvas", new NVector2(canvasWidth, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
			ImGui.PopStyleVar();
			if (canvasOpen)
			{
				graph.Draw();
			}

			ImGui.EndChild();

			if (showInspector)
			{
				ImGui.SameLine();
				if (ImGui.BeginChild("##animator_inspector", new NVector2(inspectorWidth, height), ImGuiChildFlags.Borders))
				{
					inspector.DrawInspector();
				}

				ImGui.EndChild();
			}

			if (bottom > 0f)
			{
				DrawProblems();
			}
		}

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMenuBar())
			{
				return;
			}

			if (EditorMenu.Begin("File"))
			{
				if (EditorMenu.Item("Save", "Ctrl+S", false, document != null && document.Dirty)) document.Save();
				if (EditorMenu.Item("Revert", "", false, document != null && document.Dirty)) document.Revert();
				ImGui.Separator();
				if (EditorMenu.Item("Close")) closeRequested = true;
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Edit"))
			{
				if (EditorMenu.Item("Undo", "Ctrl+Z", false, editor.Undo.CanUndo)) editor.UndoLast();
				if (EditorMenu.Item("Redo", "Ctrl+Y", false, editor.Undo.CanRedo)) editor.RedoLast();
				ImGui.Separator();
				if (EditorMenu.Item("Copy", "Ctrl+C", false, graph.View.Selection.Count > 0)) graph.Copy(new List<ulong>(graph.View.Selection));
				if (EditorMenu.Item("Paste", "Ctrl+V")) graph.Paste(graph.View.ToGraph(graph.View.CanvasMin + graph.View.CanvasSize * 0.5f));
				if (EditorMenu.Item("Duplicate", "Ctrl+D", false, graph.View.Selection.Count > 0)) graph.Duplicate(new List<ulong>(graph.View.Selection), new NVector2(40f, 40f));
				if (EditorMenu.Item("Delete", "Del", false, graph.View.Selection.Count > 0)) graph.Delete(new List<ulong>(graph.View.Selection));
				ImGui.Separator();
				if (EditorMenu.Item("Frame All", "F")) graph.View.PendingFrame = true;
				if (EditorMenu.Item("Auto Layout", "", false, document != null && !graph.Page.IsTree)) graph.AutoLayout();
				ImGui.Separator();
				EditorMenu.Item("Snap to Grid", "", ref graph.View.SnapToGrid);
				EditorMenu.Item("Alignment Guides", "", ref graph.View.ShowAlignmentGuides);
				EditorMenu.End();
			}

			if (EditorMenu.Begin("Create", document != null && !graph.Page.IsTree))
			{
				NVector2 center = graph.View.ToGraph(graph.View.CanvasMin + graph.View.CanvasSize * 0.5f);
				if (EditorMenu.Item("State")) graph.CreateState(center);
				if (EditorMenu.Item("Sub-State Machine")) graph.CreateSubMachine(center);
				if (EditorMenu.Item("Note")) graph.CreateNote(center);
				if (EditorMenu.Item("Group Selection", "", false, graph.View.Selection.Count > 0)) graph.GroupSelection();
				ImGui.Separator();
				if (EditorMenu.Item("Layer")) inspector.AddLayer();
				if (EditorMenu.Begin("Parameter"))
				{
					inspector.DrawParameterTypeMenu();
					EditorMenu.End();
				}

				EditorMenu.End();
			}

			if (EditorMenu.Begin("View"))
			{
				EditorMenu.Item("Layers and Parameters", "", ref showSidebar);
				EditorMenu.Item("Inspector", "", ref showInspector);
				EditorMenu.Item("Problems", "", ref showProblems);
				EditorMenu.Item("Minimap", "", ref graph.View.ShowMinimap);
				EditorMenu.Item("Grid", "", ref graph.View.ShowGrid);
				EditorMenu.End();
			}

			ImGui.EndMenuBar();
		}

		private void DrawToolbar()
		{
			ImGui.BeginDisabled(!document.Dirty);
			if (EditorGui.Button("Save"))
			{
				document.Save();
			}

			ImGui.EndDisabled();
			ImGui.SameLine();
			ImGui.TextDisabled(document.AssetPath ?? "");
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(12, 0));
			ImGui.SameLine();

			// Breadcrumbs: layer > machines (> the asset a node plays > its machines) (> blend tree).
			AnimatorPage page = graph.Page;
			AnimatorController controller = document.Controller;
			if (page.Layer != null)
			{
				var chain = new List<AnimatorStateMachine>();
				AnimatorStateMachine ownerLeaf = page.External != null ? page.Embedding : page.Machine;
				for (AnimatorStateMachine m = ownerLeaf; m != null; m = controller.ParentOf(m))
				{
					chain.Insert(0, m);
				}

				bool chainEndsPage = page.External == null && !page.IsTree;
				for (int i = 0; i < chain.Count; i++)
				{
					AnimatorStateMachine machine = chain[i];
					bool last = i == chain.Count - 1 && chainEndsPage;
					if (last)
					{
						ImGui.TextUnformatted(i == 0 ? page.Layer.name : machine.name);
					}
					else if (EditorGui.SmallButton((i == 0 ? page.Layer.name : machine.name) + "##crumb" + i))
					{
						graph.ShowMachine(page.Layer, machine);
					}

					ImGui.SameLine();
					if (!last)
					{
						ImGui.TextDisabled(">");
						ImGui.SameLine();
					}
				}

				if (page.External != null)
				{
					// Inside the asset: its root machine down to the shown one.
					var inner = new List<AnimatorStateMachine>();
					for (AnimatorStateMachine m = page.Machine; m != null; m = page.External.ParentOf(m))
					{
						inner.Insert(0, m);
					}

					ImGui.PushStyleColor(ImGuiCol.Text, AnimatorGraph.ExternalColor + new NVector4(0.3f, 0.3f, 0.3f, 0f));
					for (int i = 0; i < inner.Count; i++)
					{
						string label = i == 0 ? page.External.name + ".animator" : inner[i].name;
						bool last = i == inner.Count - 1 && !page.IsTree;
						if (last)
						{
							ImGui.TextUnformatted(label);
						}
						else if (EditorGui.SmallButton(label + "##ext" + i))
						{
							graph.ShowExternal(page.Layer, page.Embedding, inner[i]);
						}

						ImGui.SameLine();
						if (!last)
						{
							ImGui.TextDisabled(">");
							ImGui.SameLine();
						}
					}

					ImGui.PopStyleColor();
				}

				if (page.IsTree)
				{
					ImGui.TextUnformatted(page.TreeState.name + " / " + page.Tree.DisplayName);
					ImGui.SameLine();
				}

				if (page.ReadOnly)
				{
					ImGui.TextColored(EditorStyle.Warning, "read-only");
					if (ImGui.IsItemHovered()) ImGui.SetTooltip("Another asset's content: open it to edit.");
					ImGui.SameLine();
					if (EditorGui.SmallButton("Open " + page.External.name))
					{
						Show(page.External);
					}

					ImGui.SameLine();
				}
			}

			// Live state.
			if (LiveAnimator != null)
			{
				ImGui.SameLine();
				ImGui.Dummy(new NVector2(12, 0));
				ImGui.SameLine();
				ImGui.TextColored(EditorStyle.Accent, "LIVE");
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Following '" + LiveAnimator.gameObject.Name + "' (select another object with an Animator of this controller to follow it)");
				}

				ImGui.SameLine();
				ImGui.TextDisabled(LiveAnimator.gameObject.Name);
				int layerIndex = page.Layer != null ? controller.layers.IndexOf(page.Layer) : -1;
				if (layerIndex >= 0 && LiveAnimator.Player != null && layerIndex < LiveAnimator.Player.Layers.Length)
				{
					LayerPlayer layer = LiveAnimator.Player.Layers[layerIndex];
					ImGui.SameLine();
					string state = layer.Current != null ? layer.Current.State.FullName + " " + layer.Current.NormalizedTime.ToString("0.00") : "(no state)";
					if (layer.InTransition)
					{
						state += " -> " + layer.Next.State.FullName + " " + (layer.TransitionProgress * 100f).ToString("0") + "%";
					}

					ImGui.TextUnformatted(state);
					if (layer.History.Count > 1 && ImGui.IsItemHovered())
					{
						var history = new System.Text.StringBuilder("History (newest last):");
						foreach ((string name, long frame) in layer.History)
						{
							history.Append('\n').Append(name).Append("  (frame ").Append(frame).Append(')');
						}

						ImGui.SetTooltip(history.ToString());
					}
				}
			}
			else if (editor.IsPlaying)
			{
				ImGui.SameLine();
				ImGui.TextDisabled("(no Animator playing this controller in the scene)");
			}

			ImGui.SameLine();
			float zoomWidth = 70f;
			ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - zoomWidth));
			ImGui.TextDisabled($"zoom {graph.View.Zoom:0.00}");
			ImGui.Separator();
		}

		private void DrawProblems()
		{
			if (ImGui.BeginChild("##animator_problems", new NVector2(0, 0), ImGuiChildFlags.Borders))
			{
				ImGui.TextColored(EditorStyle.Warning, problems.Count + (problems.Count == 1 ? " problem" : " problems"));
				foreach (string problem in problems)
				{
					ImGui.BulletText(problem);
				}
			}

			ImGui.EndChild();
		}

		private void RefreshProblems()
		{
			if (document == null)
			{
				problems.Clear();
				return;
			}

			int combined = document.Controller.CombinedVersion;
			if (problemsVersion != combined)
			{
				problemsVersion = combined;
				try
				{
					problems = document.Controller.Validate();
				}
				catch (Exception e)
				{
					problems = new List<string> { "Validation failed: " + e.Message };
				}
			}
		}

		private void UpdateLive()
		{
			liveAnimator = null;
			if (!editor.IsPlaying || document == null || editor.Scene == null)
			{
				return;
			}

			AnimatorController controller = document.Controller;
			GameObject selected = editor.Selected;
			if (selected != null && !selected.IsDestroyed)
			{
				Animator own = selected.GetComponent<Animator>() ?? selected.GetComponentInParent<Animator>(true);
				if (own != null && own.controller == controller)
				{
					liveAnimator = own;
					return;
				}
			}

			foreach (Animator animator in editor.Scene.FindObjectsOfType<Animator>())
			{
				if (animator != null && !animator.IsDestroyed && animator.controller == controller && animator.isActiveAndEnabled)
				{
					liveAnimator = animator;
					return;
				}
			}
		}

		private void HandleShortcuts()
		{
			if (!IsFocused || ImGui.GetIO().WantTextInput)
			{
				return;
			}

			bool ctrl = ImGui.GetIO().KeyCtrl;
			if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S, false) && document.Dirty)
			{
				document.Save();
			}
		}

		private void DrawUnsavedPopup()
		{
			if (openUnsavedPopup)
			{
				ImGui.OpenPopup("Unsaved animator controller");
				openUnsavedPopup = false;
			}

			bool open = true;
			if (!ImGui.BeginPopupModal("Unsaved animator controller", ref open, ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			ImGui.TextUnformatted("The controller " + Path.GetFileName(document?.AssetPath ?? "") + " has unsaved changes.");
			ImGui.Spacing();
			if (EditorGui.Button("Save", new NVector2(100, 0)))
			{
				document.Save();
				Open = false;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Discard", new NVector2(100, 0)))
			{
				document.Revert();
				Open = false;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Cancel", new NVector2(100, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		#endregion
	}
}
