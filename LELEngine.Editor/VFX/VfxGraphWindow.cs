using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.VFX;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.VFX
{
	/// <summary>
	///     The VFX Graph window (Window > VFX Graph, or open a .vfx asset): a toolbar (save, compile state, playback
	///     of the scene's effects using the graph), the blackboard on the left, the node canvas in the middle, the
	///     inspector of the selected node on the right and the compile messages at the bottom. Edits are undoable
	///     (Ctrl+Z / Ctrl+Y of the editor) and apply live to every component playing the graph; Ctrl+S saves the asset.
	/// </summary>
	internal sealed class VfxGraphWindow
	{
		#region PublicFields

		public const string Title = "VFX Graph";
		public bool Open;
		public bool IsFocused { get; private set; }
		public VfxGraphDocument Document => document;
		public VfxGraphCanvas Canvas => canvas;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly VfxGraphCanvas canvas = new VfxGraphCanvas();
		private readonly VfxNodeInspector inspector = new VfxNodeInspector();
		private VfxGraphDocument document;
		private bool focusRequested;
		private bool showBlackboard = true;
		private bool showInspector = true;
		private bool showMessages = true;
		private bool showCode;
		private bool frameOnOpen;
		private bool closeRequested;
		private bool openUnsavedPopup;
		private int compiledVersion = -1;
		private readonly List<VfxCompileMessage> messages = new List<VfxCompileMessage>();
		private readonly List<string> shaderErrors = new List<string>();
		private string codeText = "";
		private int codeSystem;
		private int codeKernel;
		private int codeVersion = -1;

		#endregion

		#region Constructors

		public VfxGraphWindow(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens the window on a graph asset (null keeps the current one).</summary>
		public void Show(string assetPath)
		{
			Open = true;
			focusRequested = true;
			if (assetPath != null && (document == null || !string.Equals(document.AssetPath, assetPath, StringComparison.OrdinalIgnoreCase)))
			{
				VfxGraph graph = VfxGraph.Load(assetPath);
				document = new VfxGraphDocument(editor, graph);
				canvas.Document = document;
				canvas.ClearSelection();
				canvas.Zoom = graph.ViewZoom > 0.01f ? graph.ViewZoom : 1f;
				canvas.Offset = new NVector2(graph.ViewOffset.X, graph.ViewOffset.Y);
				frameOnOpen = graph.ViewOffset == OpenTK.Mathematics.Vector2.Zero;
				compiledVersion = -1;
			}
		}

		/// <summary>Closes the window; unsaved changes ask first.</summary>
		public void Close()
		{
			closeRequested = true;
		}

		/// <summary>Called by the undo records: the graph object changed under the window.</summary>
		public void OnGraphRestored(VfxGraph graph)
		{
			if (document != null && document.Graph == graph)
			{
				document.Restored();
				compiledVersion = -1;
			}
		}

		/// <summary>Drops the open document (project closed).</summary>
		public void Reset()
		{
			document = null;
			canvas.Document = null;
			canvas.ClearSelection();
			Open = false;
		}

		public void Draw()
		{
			if (!Open || editor.Project == null)
			{
				IsFocused = false;
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(1200, 760), ImGuiCond.FirstUseEver);
			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			bool open = Open;
			string title = Title + (document != null ? " - " + Path.GetFileNameWithoutExtension(document.AssetPath ?? "untitled") + (document.Dirty ? " *" : "") : "") + "###vfx_graph";
			bool visible = ImGui.Begin(title, ref open, ImGuiWindowFlags.MenuBar);
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

		private void DrawContent()
		{
			DrawMenuBar();
			if (document == null)
			{
				ImGui.TextDisabled("No graph open: double click a .vfx asset in the Project panel, or create one (right click > Create Visual Effect Graph).");
				return;
			}

			RefreshCompileState();
			HandleShortcuts();
			DrawToolbar();

			float bottom = showMessages && (messages.Count > 0 || shaderErrors.Count > 0) ? 110f : 0f;
			NVector2 avail = ImGui.GetContentRegionAvail();
			float height = Math.Max(100f, avail.Y - bottom);
			float blackboardWidth = showBlackboard ? 230f : 0f;
			float inspectorWidth = showInspector ? Math.Max(260f, avail.X * 0.24f) : 0f;
			float canvasWidth = Math.Max(100f, avail.X - blackboardWidth - inspectorWidth - (showBlackboard ? 4f : 0f) - (showInspector ? 4f : 0f));

			if (showBlackboard)
			{
				if (ImGui.BeginChild("##vfx_blackboard", new NVector2(blackboardWidth, height), ImGuiChildFlags.Borders))
				{
					ImGui.TextDisabled("Blackboard");
					ImGui.Separator();
					inspector.DrawBlackboard(document, canvas);
				}

				ImGui.EndChild();
				ImGui.SameLine();
			}

			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			bool canvasOpen = ImGui.BeginChild("##vfx_canvas_area", new NVector2(canvasWidth, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
			ImGui.PopStyleVar();
			if (canvasOpen)
			{
				if (showCode)
				{
					DrawCode();
				}
				else
				{
					if (frameOnOpen)
					{
						frameOnOpen = false;
						canvas.PendingFrame = true;
					}

					canvas.Draw();
				}
			}

			ImGui.EndChild();

			if (showInspector)
			{
				ImGui.SameLine();
				if (ImGui.BeginChild("##vfx_inspector", new NVector2(inspectorWidth, height), ImGuiChildFlags.Borders))
				{
					ImGui.TextDisabled("Inspector");
					ImGui.Separator();
					inspector.DrawInspector(document, canvas);
				}

				ImGui.EndChild();
			}

			if (bottom > 0f)
			{
				DrawMessages();
			}

			// The view state travels with the asset.
			document.Graph.ViewZoom = canvas.Zoom;
			document.Graph.ViewOffset = new OpenTK.Mathematics.Vector2(canvas.Offset.X, canvas.Offset.Y);
		}

		private void DrawMenuBar()
		{
			if (!ImGui.BeginMenuBar())
			{
				return;
			}

			if (ImGui.BeginMenu("File"))
			{
				if (ImGui.MenuItem("Save", "Ctrl+S", false, document != null && document.Dirty)) document.Save();
				if (ImGui.MenuItem("Revert", "", false, document != null && document.Dirty)) document.Revert();
				ImGui.Separator();
				if (ImGui.MenuItem("Close")) closeRequested = true;
				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Edit"))
			{
				if (ImGui.MenuItem("Undo", "Ctrl+Z", false, editor.Undo.CanUndo)) editor.UndoLast();
				if (ImGui.MenuItem("Redo", "Ctrl+Y", false, editor.Undo.CanRedo)) editor.RedoLast();
				ImGui.Separator();
				if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, canvas.Selected.Count > 0)) canvas.DuplicateSelected();
				if (ImGui.MenuItem("Delete", "Del", false, canvas.Selected.Count > 0)) canvas.DeleteSelected();
				ImGui.Separator();
				if (ImGui.MenuItem("Frame All", "F")) canvas.PendingFrame = true;
				if (ImGui.MenuItem("Auto Layout")) canvas.AutoLayout();
				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("View"))
			{
				ImGui.MenuItem("Blackboard", "", ref showBlackboard);
				ImGui.MenuItem("Inspector", "", ref showInspector);
				ImGui.MenuItem("Messages", "", ref showMessages);
				ImGui.MenuItem("Generated Code", "", ref showCode);
				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Templates", document != null))
			{
				if (ImGui.MenuItem("Replace with default effect"))
				{
					ReplaceGraph(VfxGraphBuilder.CreateDefault(document.Graph.name));
				}

				if (ImGui.MenuItem("Replace with empty systems"))
				{
					ReplaceGraph(VfxGraphBuilder.CreateEmpty(document.Graph.name));
				}

				ImGui.EndMenu();
			}

			ImGui.EndMenuBar();
		}

		private void ReplaceGraph(VfxGraph template)
		{
			document.Edit("Replace graph", () =>
			{
				VfxGraph graph = document.Graph;
				graph.Properties.Clear();
				graph.Nodes.Clear();
				graph.Edges.Clear();
				graph.Flow.Clear();
				graph.Properties.AddRange(template.Properties);
				graph.Nodes.AddRange(template.Nodes);
				graph.Edges.AddRange(template.Edges);
				graph.Flow.AddRange(template.Flow);
			});
			canvas.ClearSelection();
			canvas.PendingFrame = true;
		}

		private void DrawToolbar()
		{
			ImGui.BeginDisabled(!document.Dirty);
			if (ImGui.Button("Save"))
			{
				document.Save();
			}

			ImGui.EndDisabled();
			ImGui.SameLine();
			ImGui.TextDisabled(document.AssetPath ?? "");
			ImGui.SameLine();
			ImGui.Dummy(new NVector2(16, 0));
			ImGui.SameLine();

			// Playback of the scene's components playing this graph.
			List<VisualEffect> players = Players();
			ImGui.BeginDisabled(players.Count == 0);
			if (ImGui.Button("Play"))
			{
				foreach (VisualEffect player in players) player.Play();
			}

			ImGui.SameLine();
			if (ImGui.Button("Stop"))
			{
				foreach (VisualEffect player in players) player.Stop();
			}

			ImGui.SameLine();
			if (ImGui.Button("Restart"))
			{
				foreach (VisualEffect player in players) player.Reinit();
			}

			ImGui.SameLine();
			bool paused = players.Count > 0 && players[0].pause;
			if (ImGui.Button(paused ? "Resume" : "Pause"))
			{
				foreach (VisualEffect player in players) player.pause = !paused;
			}

			ImGui.EndDisabled();
			ImGui.SameLine();
			if (players.Count == 0)
			{
				if (ImGui.Button("Add to scene"))
				{
					string path = document.AssetPath;
					editor.CreateObject(Path.GetFileNameWithoutExtension(path ?? "Visual Effect"), go => go.AddComponent<VisualEffect>().visualEffectAsset = VfxGraph.Load(path));
					editor.NotifySceneChanged();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Creates a GameObject with a VisualEffect component playing this graph.");
				}
			}
			else
			{
				int alive = 0;
				foreach (VisualEffect player in players)
				{
					alive += player.aliveParticleCount;
				}

				ImGui.TextDisabled(players.Count + (players.Count == 1 ? " player, " : " players, ") + alive + " particles");
			}

			ImGui.SameLine();
			ImGui.Dummy(new NVector2(16, 0));
			ImGui.SameLine();
			int errors = 0;
			foreach (VfxCompileMessage message in messages)
			{
				if (message.IsError) errors++;
			}

			errors += shaderErrors.Count;
			if (errors > 0)
			{
				ImGui.TextColored(EditorStyle.Error, errors + (errors == 1 ? " error" : " errors"));
			}
			else
			{
				ImGui.TextColored(new System.Numerics.Vector4(0.5f, 0.85f, 0.5f, 1f), "Compiled");
			}

			ImGui.SameLine();
			ImGui.TextDisabled($"zoom {canvas.Zoom:0.00}");
			ImGui.Separator();
		}

		private List<VisualEffect> Players()
		{
			var result = new List<VisualEffect>();
			Scene scene = editor.Scene;
			if (scene == null || document == null)
			{
				return result;
			}

			foreach (VisualEffect effect in scene.FindObjectsOfType<VisualEffect>())
			{
				if (effect.visualEffectAsset == document.Graph)
				{
					result.Add(effect);
				}
			}

			return result;
		}

		private void RefreshCompileState()
		{
			VfxGraph graph = document.Graph;
			VfxProgramSet programs = VfxRuntime.PeekPrograms(graph);
			if (programs == null || programs.GraphVersion != graph.Version)
			{
				// No player compiled this version yet: compile for the messages (the GL programs too, so players reuse them).
				programs = VfxRuntime.GetPrograms(graph);
			}

			if (programs == null || compiledVersion == programs.GraphVersion)
			{
				return;
			}

			compiledVersion = programs.GraphVersion;
			messages.Clear();
			shaderErrors.Clear();
			canvas.ErrorNodes.Clear();
			if (programs.Compiled != null)
			{
				messages.AddRange(programs.Compiled.Messages);
				foreach (VfxCompileMessage message in programs.Compiled.Messages)
				{
					if (message.IsError && message.NodeId != 0)
					{
						canvas.ErrorNodes.Add(message.NodeId);
					}
				}
			}

			shaderErrors.AddRange(programs.ShaderErrors);
			codeVersion = -1;
		}

		private void DrawMessages()
		{
			if (!ImGui.BeginChild("##vfx_messages", new NVector2(0, 0), ImGuiChildFlags.Borders))
			{
				ImGui.EndChild();
				return;
			}

			foreach (VfxCompileMessage message in messages)
			{
				ImGui.TextColored(message.IsError ? EditorStyle.Error : EditorStyle.Warning, message.ToString());
				if (message.NodeId != 0 && ImGui.IsItemClicked())
				{
					canvas.Select(message.NodeId);
				}
			}

			foreach (string error in shaderErrors)
			{
				ImGui.TextColored(EditorStyle.Error, error);
			}

			ImGui.EndChild();
		}

		private void DrawCode()
		{
			VfxProgramSet programs = VfxRuntime.PeekPrograms(document.Graph);
			VfxCompiledGraph compiled = programs?.Compiled;
			if (compiled == null || compiled.Systems.Count == 0)
			{
				ImGui.TextDisabled("Nothing compiled.");
				return;
			}

			ImGui.Dummy(new NVector2(4, 2));
			ImGui.SameLine();
			var systems = new string[compiled.Systems.Count];
			for (int i = 0; i < systems.Length; i++)
			{
				systems[i] = compiled.Systems[i].Name;
			}

			codeSystem = Math.Clamp(codeSystem, 0, systems.Length - 1);
			ImGui.SetNextItemWidth(200);
			bool changed = ImGui.Combo("##system", ref codeSystem, systems, systems.Length);
			VfxSystemDescription system = compiled.Systems[codeSystem];
			var kernels = new List<string> { "Initialize", "Update" };
			foreach (VfxOutputDescription output in system.Outputs)
			{
				kernels.Add(output.Name + " (vertex)");
				kernels.Add(output.Name + " (fragment)");
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(260);
			codeKernel = Math.Clamp(codeKernel, 0, kernels.Count - 1);
			changed |= ImGui.Combo("##kernel", ref codeKernel, kernels.ToArray(), kernels.Count);
			if (changed || codeVersion != compiledVersion)
			{
				codeVersion = compiledVersion;
				if (codeKernel == 0) codeText = system.InitSource ?? "";
				else if (codeKernel == 1) codeText = system.UpdateSource ?? "";
				else
				{
					int index = (codeKernel - 2) / 2;
					bool fragment = (codeKernel - 2) % 2 == 1;
					VfxOutputDescription output = index < system.Outputs.Count ? system.Outputs[index] : null;
					codeText = output == null ? "" : fragment ? output.FragmentSource ?? "" : output.VertexSource ?? "";
				}
			}

			ImGui.InputTextMultiline("##code", ref codeText, (nuint)Math.Max(codeText.Length + 1, 1024), new NVector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
		}

		private void HandleShortcuts()
		{
			if (!IsFocused)
			{
				return;
			}

			ImGuiIOPtr io = ImGui.GetIO();
			if (io.WantTextInput)
			{
				return;
			}

			if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
			{
				document.Save();
			}
		}

		private void DrawUnsavedPopup()
		{
			if (openUnsavedPopup)
			{
				ImGui.OpenPopup("Unsaved VFX graph");
				openUnsavedPopup = false;
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (!ImGui.BeginPopupModal("Unsaved VFX graph", ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			ImGui.Text("The graph '" + Path.GetFileName(document?.AssetPath ?? "") + "' has unsaved changes.");
			ImGui.Spacing();
			if (ImGui.Button("Save", new NVector2(110, 0)))
			{
				document.Save();
				Open = false;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Discard", new NVector2(110, 0)))
			{
				document.Revert();
				Open = false;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel", new NVector2(110, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		#endregion
	}
}
