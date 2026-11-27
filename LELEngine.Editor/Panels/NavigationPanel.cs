using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Navigation;

namespace LELEngine.Editor
{
	/// <summary>
	///     Window > Navigation (Unity's Navigation window): the project's agent types (Project Settings' Navigation: id,
	///     name, size and voxels of each), the open scene's bake (what it reads, where, for which agent types) with Bake and
	///     Clear, what each agent type's navmesh came to, and which agent type's navmesh the Scene view draws.
	/// </summary>
	internal sealed class NavigationPanel : IEditorPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private string error;

		#endregion

		#region Constructors

		public NavigationPanel(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open;

		string IEditorPanel.PanelName => "Navigation";
		string IEditorPanel.ImGuiName => "Navigation";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		System.Numerics.Vector2 IEditorPanel.DefaultCaptureSize => new System.Numerics.Vector2(520, 820);

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			ImGui.SetNextWindowSize(new System.Numerics.Vector2(460, 640), ImGuiCond.FirstUseEver);
			if (ImGui.Begin("Navigation", ref Open))
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					context.Release();
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			Scene scene = editor.Scene;
			if (scene == null || !editor.AssetsReady)
			{
				if (!editor.IsLoadingProject)
				{
					ImGui.TextDisabled("No scene");
				}

				return;
			}

			context.Scene = scene;
			context.LabelWidth = Math.Max(150f, ImGui.GetContentRegionAvail().X * 0.45f);
			NavigationSettings project = ProjectSettings.Current.Navigation ??= new NavigationSettings();
			if (EditorGui.CollapsingHeader("Agent Types (project)", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushID("agents");
				DrawAgentTypes(project);
				ImGui.PopID();
			}

			if (EditorGui.CollapsingHeader("Areas (project)", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushID("areas");
				DrawAreas(project);
				ImGui.PopID();
			}

			if (EditorGui.CollapsingHeader("Bake (" + scene.Name + ")", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushID("bake");
				DrawBake(scene, project);
				ImGui.PopID();
			}
		}

		private void DrawAgentTypes(NavigationSettings project)
		{
			context.UndoTarget = ProjectSettings.Current;
			ImGui.TextDisabled("Every scene bakes for these (agents name them by id).");
			int remove = -1;
			for (int i = 0; i < project.AgentTypes.Count; i++)
			{
				NavMeshAgentType type = project.AgentTypes[i];
				if (type == null)
				{
					continue;
				}

				ImGui.PushID(i);
				if (ImGui.TreeNodeEx($"{type.Name} (id {type.Id}, radius {type.Build?.AgentRadius:0.##})###type", ImGuiTreeNodeFlags.DefaultOpen))
				{
					if (FieldDrawer.DrawFields(type, context, filter: field => field.Name != nameof(NavMeshAgentType.Id)))
					{
						editor.NotifySceneChanged();
					}

					ImGui.BeginDisabled(project.AgentTypes.Count <= 1);
					if (EditorGui.Button("Remove " + type.Name))
					{
						remove = i;
					}

					ImGui.EndDisabled();
					ImGui.TreePop();
				}

				ImGui.PopID();
			}

			if (remove >= 0)
			{
				context.BeginEdit();
				project.AgentTypes.RemoveAt(remove);
				context.EndEdit();
			}

			if (EditorGui.Button("Add Agent Type"))
			{
				context.BeginEdit();
				int id = project.NextId();
				project.AgentTypes.Add(new NavMeshAgentType { Id = id, Name = "Agent " + id });
				context.EndEdit();
			}
		}

		/// <summary>The project's areas: name and path cost each (Walkable, Not Walkable and Jump are the engine's).</summary>
		private void DrawAreas(NavigationSettings project)
		{
			context.UndoTarget = ProjectSettings.Current;
			project.Areas ??= new List<NavMeshAreaDefinition>();
			ImGui.TextDisabled("Walkable costs 1, Jump 2; Not Walkable is no navmesh. Paths go round a costly area.");
			int remove = -1;
			for (int i = 0; i < project.Areas.Count; i++)
			{
				NavMeshAreaDefinition area = project.Areas[i];
				if (area == null)
				{
					continue;
				}

				ImGui.PushID(i);
				if (ImGui.TreeNodeEx($"{area.Name} (area {area.Index}, cost {area.Cost:0.##})###area", ImGuiTreeNodeFlags.DefaultOpen))
				{
					FieldDrawer.DrawFields(area, context, filter: field => field.Name != nameof(NavMeshAreaDefinition.Index));
					if (EditorGui.Button("Remove " + area.Name))
					{
						remove = i;
					}

					ImGui.TreePop();
				}

				ImGui.PopID();
			}

			if (remove >= 0)
			{
				context.BeginEdit();
				project.Areas.RemoveAt(remove);
				context.EndEdit();
			}

			int next = project.NextAreaIndex();
			ImGui.BeginDisabled(next < 0);
			if (EditorGui.Button("Add Area"))
			{
				context.BeginEdit();
				project.Areas.Add(new NavMeshAreaDefinition { Index = next, Name = "Area " + next, Cost = 2f });
				context.EndEdit();
			}

			ImGui.EndDisabled();
			// The names and costs queries and the pickers read: live as they are edited.
			project.Apply();
		}

		private void DrawBake(Scene scene, NavigationSettings project)
		{
			SceneNavigationSettings settings = scene.Settings.Navigation ??= new SceneNavigationSettings();
			context.UndoTarget = scene.Settings;
			bool changed = FieldDrawer.DrawFields(settings, context, filter: field => field.Name switch
			{
				nameof(SceneNavigationSettings.AgentTypes) => false,
				nameof(SceneNavigationSettings.NavMesh) => false,
				nameof(SceneNavigationSettings.Padding) => settings.AutoBounds,
				nameof(SceneNavigationSettings.Center) or nameof(SceneNavigationSettings.Size) => !settings.AutoBounds,
				_ => true
			});

			// The agent types this scene bakes: all of them while none is unticked (the list is empty then).
			FieldDrawer.Label("Agent Types", null, context);
			ImGui.BeginGroup();
			foreach (NavMeshAgentType type in project.AgentTypes)
			{
				if (type == null)
				{
					continue;
				}

				bool baked = settings.AgentTypes.Count == 0 || settings.AgentTypes.Contains(type.Id);
				if (EditorGui.Checkbox(type.Name + "##agent" + type.Id, ref baked))
				{
					context.BeginEdit();
					var ids = new List<int>();
					foreach (NavMeshAgentType other in project.AgentTypes)
					{
						if (other != null && (other == type ? baked : settings.AgentTypes.Count == 0 || settings.AgentTypes.Contains(other.Id)))
						{
							ids.Add(other.Id);
						}
					}

					settings.AgentTypes = ids.Count == project.AgentTypes.Count ? new List<int>() : ids;
					context.EndEdit();
					changed = true;
				}
			}

			ImGui.EndGroup();
			if (changed)
			{
				editor.MarkDirty();
			}

			ImGui.Spacing();
			ImGui.BeginDisabled(editor.IsPlaying);
			if (EditorGui.Button("Bake"))
			{
				error = null;
				try
				{
					editor.BakeSceneNavigation();
				}
				catch (InvalidOperationException e)
				{
					error = e.Message;
				}
			}

			ImGui.SameLine();
			ImGui.BeginDisabled(settings.NavMesh == null || settings.NavMesh.NavMeshes.Count == 0);
			if (EditorGui.Button("Clear"))
			{
				error = null;
				editor.ClearSceneNavigation();
			}

			ImGui.EndDisabled();
			ImGui.EndDisabled();
			if (error != null)
			{
				ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.35f, 1f), error);
			}

			ImGui.Spacing();
			DrawBaked(settings, project);
		}

		/// <summary>The asset, each agent type's navmesh and the Scene view's choice of which to draw.</summary>
		private void DrawBaked(SceneNavigationSettings settings, NavigationSettings project)
		{
			SceneNavMeshAsset asset = settings.NavMesh;
			if (asset == null)
			{
				ImGui.TextDisabled("Not baked.");
				return;
			}

			ImGui.TextDisabled(asset.AssetPath ?? "(not saved)");
			foreach (SceneNavMesh navMesh in asset.NavMeshes)
			{
				if (navMesh?.Data == null)
				{
					continue;
				}

				string time = "";
				foreach (SceneNavMeshBakeResult result in editor.LastNavigationBake)
				{
					if (result.AgentTypeId == navMesh.AgentTypeId && ReferenceEquals(result.Data, navMesh.Data))
					{
						time = $", {result.Obstacles} obstacle pieces, baked in {result.Milliseconds:F0} ms";
					}
				}

				ImGui.BulletText(navMesh.Data.IsEmpty
					? $"{project.NameOf(navMesh.AgentTypeId)}: nothing walkable"
					: $"{project.NameOf(navMesh.AgentTypeId)}: {navMesh.Data.PolygonCount} polygons, {navMesh.Data.Tiles.Length} tiles, {navMesh.Data.DataSize / 1024} KB{time}");
			}

			ImGui.Spacing();
			FieldDrawer.Label("Scene View Shows", null, context);
			int shown = NavMeshDebug.AgentTypeFilter;
			if (EditorGui.BeginCombo("##shown", shown < 0 ? "Every agent type" : project.NameOf(shown)))
			{
				if (EditorGui.Selectable("Every agent type", shown < 0))
				{
					NavMeshDebug.AgentTypeFilter = -1;
					editor.NotifySceneChanged();
				}

				foreach (NavMeshAgentType type in project.AgentTypes)
				{
					if (type != null && EditorGui.Selectable(type.Name + "##show" + type.Id, shown == type.Id))
					{
						NavMeshDebug.AgentTypeFilter = type.Id;
						editor.NotifySceneChanged();
					}
				}

				EditorGui.EndCombo();
			}
		}

		#endregion
	}
}
