using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Project Settings (Edit > Project Settings...), one tab each: the project-wide lighting quality (shadow map,
	///     GI method and its tunables), 2D physics (simulation plane, gravity, solver, queries, layer collision matrix)
	///     and the build (scenes, start scene, output folder, configuration; builds in the background). Lighting and
	///     physics edits are undoable and saved to ProjectSettings/ProjectSettings.asset when they end; the lighting of
	///     a scene (environment, shadow reach, GI volume) stays in the Lighting panel.
	/// </summary>
	internal sealed class ProjectSettingsWindow
	{
		#region PublicFields

		public enum Tab
		{
			Lighting,
			Physics2D,
			Build
		}

		public bool Open;

		#endregion

		#region PrivateFields

		private static readonly string[] PlaneNames = { "XY (side view)", "XZ (top-down)" };

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private Tab? requestedTab;
		private bool focus;

		// ---- build
		private string outputFolder;
		private bool release = true;
		private Task<bool> buildTask;
		private readonly List<string> lines = new List<string>();
		private string lastOutput;

		#endregion

		#region Constructors

		public ProjectSettingsWindow(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens the window in front, on <paramref name="tab" /> (null: the tab it was on).</summary>
		public void Show(Tab? tab)
		{
			Open = true;
			focus = true;
			requestedTab = tab;
		}

		public void Draw()
		{
			if (!Open || editor.Project == null)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(620, 600), ImGuiCond.FirstUseEver);
			if (focus)
			{
				ImGui.SetNextWindowFocus();
				focus = false;
			}

			if (ImGui.Begin("Project Settings", ref Open))
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
			ProjectSettings settings = ProjectSettings.Current;
			context.Scene = editor.Scene;
			context.UndoTarget = settings;
			context.LabelWidth = Math.Max(170f, ImGui.GetContentRegionAvail().X * 0.45f);

			if (!ImGui.BeginTabBar("##tabs"))
			{
				return;
			}

			bool changed = false;
			if (BeginTab(Tab.Lighting, "Lighting"))
			{
				changed |= DrawLighting(settings);
				EndTab();
			}

			if (BeginTab(Tab.Physics2D, "Physics 2D"))
			{
				changed |= DrawPhysics(settings.Physics2D);
				EndTab();
			}

			if (BeginTab(Tab.Build, "Build"))
			{
				DrawBuild(editor.Project);
				EndTab();
			}

			ImGui.EndTabBar();
			requestedTab = null;
			if (changed)
			{
				// Saved when the edit ends (EditTracker); the views show the change right away.
				editor.NotifySceneChanged();
			}
		}

		private bool BeginTab(Tab tab, string label)
		{
			ImGuiTabItemFlags flags = requestedTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
			if (!ImGui.BeginTabItem(label, flags))
			{
				return false;
			}

			ImGui.BeginChild("##content", NVector2.Zero);
			return true;
		}

		private static void EndTab()
		{
			ImGui.EndChild();
			ImGui.EndTabItem();
		}

		// ---------------------------------------------------------------- lighting

		private bool DrawLighting(ProjectSettings settings)
		{
			ImGui.TextDisabled("How shadows and global illumination are computed in every scene.");
			ImGui.TextDisabled("A scene's environment, shadow reach and GI volume are in the Lighting panel.");
			ImGui.Spacing();

			bool changed = false;
			if (ImGui.CollapsingHeader("Shadows", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushID("shadows");
				changed |= FieldDrawer.DrawFields(settings.Shadows, context);
				ImGui.PopID();
			}

			if (ImGui.CollapsingHeader("Global Illumination", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushID("gi");
				if (FieldDrawer.DrawFields(settings.GI, context))
				{
					settings.GI.InvalidateStatic();
					changed = true;
				}

				ImGui.PopID();
			}

			return changed;
		}

		// ---------------------------------------------------------------- physics

		private bool DrawPhysics(Physics2DSettings physics)
		{
			bool changed = DrawSimulationPlane(physics);
			ImGui.PushID("physics2d");
			changed |= FieldDrawer.DrawFields(physics, context);
			changed |= DrawLayerCollisionMatrix(physics);
			ImGui.PopID();
			return changed;
		}

		/// <summary>The plane is read when a scene's physics world is created, so it does not change during play.</summary>
		private bool DrawSimulationPlane(Physics2DSettings physics)
		{
			int current = physics.SimulationPlane == SimulationPlane2D.XZ ? 1 : 0;
			int selected = current;
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted("Simulation Plane");
			ImGui.SameLine(context.LabelWidth);
			ImGui.SetNextItemWidth(-1);
			ImGui.BeginDisabled(editor.IsPlaying);
			bool changed = ImGui.Combo("##plane", ref selected, PlaneNames, PlaneNames.Length) && selected != current;
			ImGui.EndDisabled();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(editor.IsPlaying
					? "Applies when play starts (the running world keeps its plane)."
					: "XY: 2D y is world Y, bodies turn about Z.\nXZ: 2D y is world Z, bodies turn about -Y (the XY plane turned 90 degrees about X).");
			}

			context.Track(changed);
			if (changed)
			{
				physics.SimulationPlane = selected == 1 ? SimulationPlane2D.XZ : SimulationPlane2D.XY;
			}

			return changed;
		}

		/// <summary>Which layers collide with which (the used layers: named ones and those of the scene's objects).</summary>
		private bool DrawLayerCollisionMatrix(Physics2DSettings physics)
		{
			if (!ImGui.TreeNodeEx("Layer Collision Matrix", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen))
			{
				return false;
			}

			var layers = new List<int>();
			for (int i = 0; i < 32; i++)
			{
				if (LayerMask.LayerToName(i).Length > 0)
				{
					layers.Add(i);
				}
			}

			Scene scene = editor.Scene;
			if (scene != null)
			{
				foreach (GameObject go in scene.GameObjects)
				{
					if (!layers.Contains(go.layer))
					{
						layers.Add(go.layer);
					}
				}
			}

			layers.Sort();
			bool changed = false;
			if (ImGui.BeginTable("##matrix", layers.Count + 1, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInner))
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				foreach (int column in layers)
				{
					ImGui.TableNextColumn();
					ImGui.TextDisabled(LayerMask.DisplayName(column));
				}

				foreach (int row in layers)
				{
					ImGui.TableNextRow();
					ImGui.TableSetColumnIndex(0);
					ImGui.TextUnformatted(LayerMask.DisplayName(row));
					foreach (int column in layers)
					{
						ImGui.TableNextColumn();
						if (column < row)
						{
							continue;
						}

						bool collide = !physics.GetIgnoreLayerCollision(row, column);
						ImGui.PushID(row * 32 + column);
						if (ImGui.Checkbox("##c", ref collide))
						{
							editor.Edits.Begin(context.UndoTarget);
							physics.IgnoreLayerCollision(row, column, !collide);
							editor.Edits.RequestCommit();
							changed = true;
						}

						ImGui.PopID();
					}
				}

				ImGui.EndTable();
			}

			ImGui.TreePop();
			return changed;
		}

		// ---------------------------------------------------------------- build

		/// <summary>Scenes in the build, start scene and Run In Background (saved in Project.json), output folder, configuration, Build / Open Folder / Run and the log.</summary>
		private void DrawBuild(ProjectInfo project)
		{
			outputFolder ??= Path.Combine(project.RootPath, "Build", ProjectGenerator.ProjectName(project));

			ImGui.Text("Scenes in the build");
			bool changed = false;
			List<string> scenes = AssetDatabase.GetAllAssetPaths(SceneSerializer.Extension);
			if (ImGui.BeginChild("##scenes", new NVector2(0, 150), ImGuiChildFlags.Borders))
			{
				foreach (string scene in scenes)
				{
					bool included = project.BuildScenes.Contains(scene) || scene == project.StartScene;
					if (ImGui.Checkbox(scene, ref included))
					{
						if (included) project.BuildScenes.Add(scene);
						else project.BuildScenes.Remove(scene);
						changed = true;
					}
				}
			}

			ImGui.EndChild();

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Start scene");
			ImGui.SameLine(120);
			ImGui.SetNextItemWidth(-1);
			if (ImGui.BeginCombo("##start", project.StartScene ?? "(none)"))
			{
				foreach (string scene in scenes)
				{
					if (ImGui.Selectable(scene, scene == project.StartScene))
					{
						project.StartScene = scene;
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Output folder");
			ImGui.SameLine(120);
			ImGui.SetNextItemWidth(-1);
			ImGui.InputText("##output", ref outputFolder, 512);

			ImGui.AlignTextToFramePadding();
			ImGui.Text("Configuration");
			ImGui.SameLine(120);
			if (ImGui.RadioButton("Release", release)) release = true;
			ImGui.SameLine();
			if (ImGui.RadioButton("Debug", !release)) release = false;
			ImGui.TextDisabled("Framework-dependent build: the target machine needs the .NET 10 runtime.");

			bool runInBackground = project.RunInBackground;
			if (ImGui.Checkbox("Run In Background", ref runInBackground))
			{
				project.RunInBackground = runInBackground;
				changed = true;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("The game keeps updating and rendering while its window is not focused.");
			}

			if (changed)
			{
				project.Save();
			}

			ImGui.Spacing();
			bool building = buildTask != null && !buildTask.IsCompleted;
			ImGui.BeginDisabled(building);
			if (ImGui.Button(building ? "Building..." : "Build", new NVector2(140, 0)))
			{
				StartBuild(project);
			}

			ImGui.EndDisabled();
			if (lastOutput != null && !building)
			{
				ImGui.SameLine();
				if (ImGui.Button("Open Folder", new NVector2(140, 0)))
				{
					Process.Start(new ProcessStartInfo("explorer.exe", "\"" + lastOutput + "\"") { UseShellExecute = true });
				}

				ImGui.SameLine();
				if (ImGui.Button("Run", new NVector2(100, 0)))
				{
					string exe = Path.Combine(lastOutput, ProjectGenerator.ProjectName(project) + ".exe");
					Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = lastOutput, UseShellExecute = false });
				}
			}

			ImGui.Separator();
			if (ImGui.BeginChild("##log", new NVector2(0, 0), ImGuiChildFlags.Borders))
			{
				lock (lines)
				{
					foreach (string line in lines)
					{
						if (line.StartsWith("Error", StringComparison.Ordinal) || line.Contains(": error "))
						{
							ImGui.TextColored(EditorStyle.Error, line);
						}
						else
						{
							ImGui.TextUnformatted(line);
						}
					}
				}
			}

			ImGui.EndChild();
		}

		private void StartBuild(ProjectInfo project)
		{
			if (editor.SceneDirty)
			{
				editor.SaveScene();
			}

			// The build copies the settings file; an edit still in progress is committed (and saved) first.
			editor.Edits.Commit();
			editor.SaveProjectSettings();

			var settings = new BuildSettings { OutputFolder = outputFolder, Release = release, StartScene = project.StartScene, Scenes = new List<string>(project.BuildScenes) };
			lock (lines)
			{
				lines.Clear();
			}

			string output = Path.GetFullPath(outputFolder);
			buildTask = Task.Run(() =>
			{
				bool ok;
				try
				{
					ok = GameBuilder.Build(project, settings, Append);
				}
				catch (Exception e)
				{
					Append("Error: " + e.Message);
					ok = false;
				}

				if (ok)
				{
					lastOutput = output;
				}

				return ok;
			});
		}

		private void Append(string line)
		{
			lock (lines)
			{
				lines.Add(line);
			}

			Console.WriteLine("[Build] " + line);
		}

		#endregion
	}
}
