using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
		private string selectedFile;

		private string createKind;
		private string createName = "";
		private bool openCreatePopup;
		private string error;

		#endregion

		#region Constructors

		public ProjectPanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public void Draw()
		{
			if (ImGui.Begin("Project"))
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
					DrawFolderTree("", "Assets");
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
			string[] children = Directory.GetDirectories(Absolute(relative));
			ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
			if (relative.Length == 0)
			{
				flags |= ImGuiTreeNodeFlags.DefaultOpen;
			}

			if (children.Length == 0)
			{
				flags |= ImGuiTreeNodeFlags.Leaf;
			}

			if (string.Equals(relative, folder, StringComparison.OrdinalIgnoreCase))
			{
				flags |= ImGuiTreeNodeFlags.Selected;
			}

			bool open = ImGui.TreeNodeEx(label + "##" + relative, flags);
			if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
			{
				folder = relative;
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

		private void DrawFiles()
		{
			RefreshListing();
			ImGui.TextDisabled("Assets/" + folder);
			ImGui.Separator();

			if (folder.Length > 0 && ImGui.Selectable("..", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
			{
				int slash = folder.LastIndexOf('/');
				folder = slash >= 0 ? folder.Substring(0, slash) : "";
				return;
			}

			foreach (string directory in listedDirectories)
			{
				if (ImGui.Selectable("[" + directory + "]##dir", false, ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				{
					folder = Combine(folder, directory);
					return;
				}
			}

			foreach (string file in listedFiles)
			{
				string assetPath = Combine(folder, file);
				bool selected = string.Equals(selectedFile, assetPath, StringComparison.OrdinalIgnoreCase);
				if (ImGui.Selectable(file, selected, ImGuiSelectableFlags.AllowDoubleClick))
				{
					selectedFile = assetPath;
					if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						OpenAsset(assetPath);
					}
				}

				if (ImGui.BeginDragDropSource())
				{
					DraggedAsset = assetPath;
					unsafe
					{
						ImGui.SetDragDropPayload(DragType, null, 0);
					}

					ImGui.Text(file);
					ImGui.EndDragDropSource();
				}

				if (ImGui.BeginPopupContextItem("##file_context"))
				{
					selectedFile = assetPath;
					if (ImGui.MenuItem("Open")) OpenAsset(assetPath);
					if (ImGui.MenuItem("Show in Explorer")) ShowInExplorer(Absolute(assetPath));
					ImGui.EndPopup();
				}
			}

			NVector2 rest = ImGui.GetContentRegionAvail();
			ImGui.InvisibleButton("##files_empty", new NVector2(Math.Max(1, rest.X), Math.Max(30, rest.Y)));
			if (ImGui.BeginPopupContextItem("##files_context"))
			{
				if (ImGui.MenuItem("Create Folder")) StartCreate("Folder", "New Folder");
				if (ImGui.MenuItem("Create C# Script")) StartCreate("Script", "NewBehaviour");
				if (ImGui.MenuItem("Create Material")) StartCreate("Material", "NewMaterial");
				if (ImGui.MenuItem("Create Scene")) StartCreate("Scene", "NewScene");
				ImGui.Separator();
				if (ImGui.MenuItem("Show in Explorer")) ShowInExplorer(Absolute(folder));
				ImGui.EndPopup();
			}
		}

		private void RefreshListing()
		{
			if (listedVersion == editor.AssetsVersion && listedFolder == folder)
			{
				return;
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
		}

		private void OpenAsset(string assetPath)
		{
			if (assetPath.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase))
			{
				editor.OpenScene(assetPath);
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

			ImGui.Text("New " + createKind + " in Assets/" + folder);
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
				case "Material":
					target = Absolute(Combine(folder, name + ".material"));
					EnsureNew(target);
					File.WriteAllText(target, "Lit.shader\nuniform\n\tvec4\n\t\tColor\n\t\t\t0.8 0.8 0.8 1\n");
					break;
				case "Scene":
					target = Absolute(Combine(folder, name + SceneSerializer.Extension));
					EnsureNew(target);
					SceneSerializer.Save(DefaultScene.Create(editor.Project), target);
					break;
				default:
					return;
			}

			AssetDatabase.Refresh(true);
			editor.NotifyAssetsChanged();
			selectedFile = AssetDatabase.ToAssetPath(target);
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
