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
	/// <summary>Build Settings: scenes in the build, start scene, output folder, configuration; builds in the background.</summary>
	internal sealed class BuildPanel
	{
		#region PublicFields

		public bool Open;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private string outputFolder;
		private bool release = true;
		private Task<bool> buildTask;
		private readonly List<string> lines = new List<string>();
		private string lastOutput;

		#endregion

		#region Constructors

		public BuildPanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public void Draw()
		{
			if (!Open || editor.Project == null)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(560, 520), ImGuiCond.FirstUseEver);
			if (ImGui.Begin("Build Settings", ref Open))
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
			ProjectInfo project = editor.Project;
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
