using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Start screen: recent projects, open a project folder, create a new project.</summary>
	internal sealed class ProjectLauncher
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private List<string> recent;
		private string openPath = "";
		private string newName = "MyGame";
		private string newLocation;
		private string error;

		#endregion

		#region Constructors

		public ProjectLauncher(EditorApplication editor)
		{
			this.editor = editor;
			newLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LELEngine Projects");
		}

		#endregion

		#region PublicMethods

		public void Draw()
		{
			recent ??= RecentProjects.Load();

			ImGuiViewportPtr viewport = ImGui.GetMainViewport();
			NVector2 size = new NVector2(Math.Min(760, viewport.Size.X - 40), Math.Min(560, viewport.Size.Y - 40));
			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Always, new NVector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(size, ImGuiCond.Always);
			ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking;
			if (ImGui.Begin("LELEngine - Projects", flags))
			{
				DrawRecent();
				ImGui.Separator();
				DrawOpen();
				ImGui.Separator();
				DrawCreate();
				if (error != null)
				{
					ImGui.Spacing();
					ImGui.TextColored(EditorStyle.Error, error);
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawRecent()
		{
			ImGui.Text("Recent projects");
			if (ImGui.BeginChild("recent", new NVector2(0, 210), ImGuiChildFlags.Borders))
			{
				if (recent.Count == 0)
				{
					ImGui.TextDisabled("No recent projects.");
				}

				foreach (string path in recent)
				{
					ImGui.PushID(path);
					if (ImGui.Selectable(Path.GetFileName(path), false, ImGuiSelectableFlags.AllowDoubleClick))
					{
						openPath = path;
						if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							Open(path);
						}
					}

					ImGui.SameLine(220);
					ImGui.TextDisabled(path);
					ImGui.PopID();
				}
			}

			ImGui.EndChild();
		}

		private void DrawOpen()
		{
			ImGui.Text("Open project folder");
			ImGui.SetNextItemWidth(-90);
			ImGui.InputText("##open", ref openPath, 512);
			ImGui.SameLine();
			if (ImGui.Button("Open", new NVector2(-1, 0)))
			{
				Open(openPath);
			}
		}

		private void DrawCreate()
		{
			ImGui.Text("Create new project");
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Name");
			ImGui.SameLine(90);
			ImGui.SetNextItemWidth(-1);
			ImGui.InputText("##name", ref newName, 128);
			ImGui.AlignTextToFramePadding();
			ImGui.Text("Location");
			ImGui.SameLine(90);
			ImGui.SetNextItemWidth(-1);
			ImGui.InputText("##location", ref newLocation, 512);
			ImGui.TextDisabled("Folder: " + SafeCombine(newLocation, newName));
			if (ImGui.Button("Create", new NVector2(120, 0)))
			{
				try
				{
					Directory.CreateDirectory(newLocation);
					ProjectInfo project = ProjectInfo.Create(newLocation, newName.Trim(), EditorApplication.TemplateAssetsPath);
					error = null;
					editor.OpenProject(project.RootPath);
					recent = null;
				}
				catch (Exception e)
				{
					error = e.Message;
				}
			}
		}

		private void Open(string path)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(path) || !ProjectInfo.IsProjectFolder(path.Trim().Trim('"')))
				{
					error = "Not a LELEngine project folder (no ProjectSettings/Project.json): " + path;
					return;
				}

				error = null;
				editor.OpenProject(path.Trim().Trim('"'));
				recent = null;
			}
			catch (Exception e)
			{
				error = e.Message;
			}
		}

		private static string SafeCombine(string a, string b)
		{
			try
			{
				return Path.Combine(a ?? "", b ?? "");
			}
			catch (ArgumentException)
			{
				return "(invalid path)";
			}
		}

		#endregion
	}
}
