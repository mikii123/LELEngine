using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Start screen: recent projects (one click opens), open a project folder, create a new project.</summary>
	internal sealed class ProjectLauncher
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private List<string> recent;
		private string newName = "MyGame";
		private string newLocation;
		private string error;

		/// <summary>
		///     System dialog requested by this frame's UI. It runs from <see cref="Update" />, outside the ImGui frame:
		///     the dialog is modal and pumps the window's messages while open.
		/// </summary>
		private Action pendingDialog;

		#endregion

		#region Constructors

		public ProjectLauncher(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public void Update()
		{
			Action dialog = pendingDialog;
			pendingDialog = null;
			try
			{
				dialog?.Invoke();
			}
			catch (Exception e)
			{
				error = e.Message;
			}
		}

		public void Draw()
		{
			if (recent == null)
			{
				recent = RecentProjects.Load();
				// New projects go next to the last one by default.
				newLocation ??= recent.Count > 0
					? Path.GetDirectoryName(recent[0])
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LELEngine Projects");
			}

			ImGuiViewportPtr viewport = ImGui.GetMainViewport();
			NVector2 size = new NVector2(Math.Min(760, viewport.Size.X - 40), Math.Min(560, viewport.Size.Y - 40));
			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Always, new NVector2(0.5f, 0.5f));
			ImGui.SetNextWindowSize(size, ImGuiCond.Always);
			ImGuiWindowFlags flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking;
			if (ImGui.Begin("LELEngine - Projects", flags))
			{
				DrawRecent();
				ImGui.Spacing();
				ImGui.Separator();
				ImGui.Spacing();
				DrawCreate();
				if (error != null)
				{
					ImGui.Spacing();
					ImGui.PushTextWrapPos(0f);
					ImGui.TextColored(EditorStyle.Error, error);
					ImGui.PopTextWrapPos();
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawRecent()
		{
			ImGuiStylePtr style = ImGui.GetStyle();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted("Recent projects");
			const float openWidth = 150f;
			ImGui.SameLine(ImGui.GetContentRegionAvail().X - openWidth + style.WindowPadding.X);
			if (ImGui.Button("Open project...", new NVector2(openWidth, 0)))
			{
				pendingDialog = BrowseOpen;
			}

			// The create section below keeps its height; the list takes the rest.
			float createHeight = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing() * 3f + style.ItemSpacing.Y * 6f + style.WindowPadding.Y;
			if (error != null)
			{
				createHeight += ImGui.GetTextLineHeightWithSpacing() * 2f + style.ItemSpacing.Y;
			}

			float listHeight = Math.Max(120f, ImGui.GetContentRegionAvail().Y - createHeight);
			if (ImGui.BeginChild("recent", new NVector2(0, listHeight), ImGuiChildFlags.Borders))
			{
				if (recent.Count == 0)
				{
					ImGui.TextDisabled("No recent projects. Open an existing project or create a new one below.");
				}

				string remove = null;
				float line = ImGui.GetTextLineHeight();
				float rowHeight = line * 2f + 10f;
				ImDrawListPtr draw = ImGui.GetWindowDrawList();
				uint nameColor = ImGui.GetColorU32(ImGuiCol.Text);
				uint pathColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
				foreach (string path in recent)
				{
					ImGui.PushID(path);
					if (ImGui.Selectable("##row", false, ImGuiSelectableFlags.None, new NVector2(0, rowHeight)))
					{
						Open(path);
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Click to open. Right-click for more.");
					}

					if (ImGui.BeginPopupContextItem("row"))
					{
						if (ImGui.MenuItem("Open"))
						{
							Open(path);
						}

						if (ImGui.MenuItem("Show in Explorer"))
						{
							ShowInExplorer(path);
						}

						if (ImGui.MenuItem("Remove from list"))
						{
							remove = path;
						}

						ImGui.EndPopup();
					}

					NVector2 min = ImGui.GetItemRectMin();
					draw.AddText(min + new NVector2(8f, 4f), nameColor, Path.GetFileName(path));
					draw.AddText(min + new NVector2(8f, 6f + line), pathColor, path);
					ImGui.PopID();
				}

				if (remove != null)
				{
					RecentProjects.Remove(remove);
					recent = null;
				}
			}

			ImGui.EndChild();
		}

		private void DrawCreate()
		{
			ImGui.TextUnformatted("New project");
			const float labelWidth = 80f;
			const float buttonWidth = 110f;

			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted("Name");
			ImGui.SameLine(labelWidth);
			ImGui.SetNextItemWidth(-1);
			bool enter = ImGui.InputText("##name", ref newName, 128, ImGuiInputTextFlags.EnterReturnsTrue);

			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted("Location");
			ImGui.SameLine(labelWidth);
			ImGuiStylePtr style = ImGui.GetStyle();
			if (NativeDialogs.CanPickFolder)
			{
				// Read-only path: it is chosen with the system folder picker (or a click on the field).
				string shown = newLocation;
				ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - buttonWidth - style.ItemSpacing.X);
				ImGui.InputText("##location", ref shown, 512, ImGuiInputTextFlags.ReadOnly);
				if (ImGui.IsItemClicked())
				{
					pendingDialog = BrowseLocation;
				}

				ImGui.SameLine();
				if (ImGui.Button("Browse...", new NVector2(buttonWidth, 0)))
				{
					pendingDialog = BrowseLocation;
				}
			}
			else
			{
				ImGui.SetNextItemWidth(-1);
				ImGui.InputText("##location", ref newLocation, 512);
			}

			string problem = ValidateNew(out string folder);
			ImGui.Spacing();
			ImGui.AlignTextToFramePadding();
			// The status line is clipped before the Create button.
			NVector2 status = ImGui.GetCursorScreenPos();
			float statusWidth = ImGui.GetContentRegionAvail().X - buttonWidth - style.ItemSpacing.X;
			ImGui.PushClipRect(status, status + new NVector2(statusWidth, ImGui.GetFrameHeight()), true);
			if (problem != null)
			{
				ImGui.TextColored(EditorStyle.Warning, problem);
			}
			else
			{
				ImGui.TextDisabled("Creates " + folder);
			}

			ImGui.PopClipRect();
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(problem ?? folder);
			}

			ImGui.SameLine(ImGui.GetContentRegionAvail().X - buttonWidth + style.WindowPadding.X);
			ImGui.BeginDisabled(problem != null);
			bool create = ImGui.Button("Create", new NVector2(buttonWidth, 0));
			ImGui.EndDisabled();
			if ((create || enter) && problem == null)
			{
				Create();
			}
		}

		/// <summary>Why the new project cannot be created yet, or null (with the folder it will be created in).</summary>
		private string ValidateNew(out string folder)
		{
			folder = null;
			string name = newName.Trim();
			if (name.Length == 0)
			{
				return "Enter a project name.";
			}

			if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				return "The name contains characters not allowed in folder names.";
			}

			if (string.IsNullOrWhiteSpace(newLocation))
			{
				return "Choose a location.";
			}

			try
			{
				folder = Path.GetFullPath(Path.Combine(newLocation, name));
			}
			catch (Exception)
			{
				return "Invalid location.";
			}

			if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length > 0)
			{
				return "The folder " + folder + " already exists and is not empty.";
			}

			return null;
		}

		private void Create()
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

		private void BrowseOpen()
		{
			string start = recent != null && recent.Count > 0 ? Path.GetDirectoryName(recent[0]) : newLocation;
			string folder = NativeDialogs.PickFolder(editor.Window, "Open LELEngine project", start);
			if (folder != null)
			{
				Open(folder);
			}
		}

		private void BrowseLocation()
		{
			string folder = NativeDialogs.PickFolder(editor.Window, "Location of the new project", newLocation);
			if (folder != null)
			{
				newLocation = folder;
				error = null;
			}
		}

		private void Open(string path)
		{
			try
			{
				if (!ProjectInfo.IsProjectFolder(path))
				{
					error = "Not a LELEngine project folder (no ProjectSettings/Project.json): " + path;
					return;
				}

				error = null;
				editor.OpenProject(path);
				recent = null;
			}
			catch (Exception e)
			{
				error = e.Message;
			}
		}

		private static void ShowInExplorer(string path)
		{
			try
			{
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot show " + path + ": " + e.Message);
			}
		}

		#endregion
	}
}
