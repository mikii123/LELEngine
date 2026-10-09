using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     A LELEngine project folder:
	///     <code>
	///     Project/
	///       Assets/            everything the game uses (scenes, scripts, meshes, materials, shaders, textures) + .meta
	///       ProjectSettings/   Project.json (name, scenes in the build, start scene)
	///       Library/           editor caches (build output of the scripts, editor state); safe to delete
	///       Project.csproj     generated: open it (or Project.sln) in any IDE to edit the scripts
	///     </code>
	/// </summary>
	public sealed class ProjectInfo
	{
		#region PublicFields

		public string Name { get; set; }

		/// <summary>Asset path of the scene the game starts with.</summary>
		public string StartScene { get; set; } = "Scenes/Main.scene";

		/// <summary>Scenes included in builds (asset paths); the start scene is always included.</summary>
		public List<string> BuildScenes { get; set; } = new List<string>();

		/// <summary>Scene open when the editor last closed.</summary>
		public string LastScene { get; set; }

		/// <summary>Play mode starts with a full reload of the game scripts (clean statics).</summary>
		public bool ReloadScriptsOnPlay { get; set; } = true;

		/// <summary>Builds keep running while their window is not focused.</summary>
		public bool RunInBackground { get; set; } = true;

		[JsonIgnore] public string RootPath { get; private set; }
		[JsonIgnore] public string AssetsPath => Path.Combine(RootPath, "Assets");
		[JsonIgnore] public string LibraryPath => Path.Combine(RootPath, "Library");
		[JsonIgnore] public string SettingsPath => Path.Combine(RootPath, "ProjectSettings");
		[JsonIgnore] public string SettingsFile => Path.Combine(SettingsPath, "Project.json");

		#endregion

		#region PrivateFields

		private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

		#endregion

		#region PublicMethods

		public static bool IsProjectFolder(string folder)
		{
			return File.Exists(Path.Combine(folder, "ProjectSettings", "Project.json"));
		}

		public static ProjectInfo Load(string folder)
		{
			string root = Path.GetFullPath(folder);
			string file = Path.Combine(root, "ProjectSettings", "Project.json");
			if (!File.Exists(file))
			{
				throw new InvalidOperationException("Not a LELEngine project (no ProjectSettings/Project.json): " + root);
			}

			ProjectInfo project = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(file), JsonOptions) ?? new ProjectInfo();
			project.RootPath = root;
			if (string.IsNullOrEmpty(project.Name))
			{
				project.Name = Path.GetFileName(root);
			}

			Directory.CreateDirectory(project.AssetsPath);
			Directory.CreateDirectory(project.LibraryPath);
			return project;
		}

		/// <summary>Creates the folder structure and copies the starter assets of the template.</summary>
		public static ProjectInfo Create(string parentFolder, string name, string templateAssets)
		{
			if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				throw new ArgumentException("Invalid project name: '" + name + "'");
			}

			string root = Path.GetFullPath(Path.Combine(parentFolder, name));
			if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length > 0)
			{
				throw new InvalidOperationException("The folder already exists and is not empty: " + root);
			}

			Directory.CreateDirectory(root);
			var project = new ProjectInfo { Name = name, RootPath = root };
			Directory.CreateDirectory(project.AssetsPath);
			Directory.CreateDirectory(Path.Combine(project.AssetsPath, "Scenes"));
			Directory.CreateDirectory(Path.Combine(project.AssetsPath, "Scripts"));
			Directory.CreateDirectory(project.SettingsPath);
			Directory.CreateDirectory(project.LibraryPath);

			if (Directory.Exists(templateAssets))
			{
				CopyDirectory(templateAssets, project.AssetsPath);
			}

			File.WriteAllText(Path.Combine(root, ".gitignore"), "Library/\nBuild/\nbin/\nobj/\n.vs/\n.idea/\n*.user\n");
			project.Save();
			return project;
		}

		public void Save()
		{
			Directory.CreateDirectory(SettingsPath);
			File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
		}

		#endregion

		#region PrivateMethods

		private static void CopyDirectory(string source, string target)
		{
			foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
			{
				Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
			}

			foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
			{
				if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), false);
			}
		}

		#endregion
	}

	/// <summary>Recently opened projects (per user).</summary>
	public static class RecentProjects
	{
		#region PublicFields

		public static string EditorDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LELEngine", "Editor");

		#endregion

		#region PublicMethods

		public static List<string> Load()
		{
			try
			{
				string file = Path.Combine(EditorDataFolder, "recent.json");
				if (File.Exists(file))
				{
					List<string> list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? new List<string>();
					list.RemoveAll(p => !ProjectInfo.IsProjectFolder(p));
					return list;
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot read the recent projects list: " + e.Message);
			}

			return new List<string>();
		}

		public static void Add(string root)
		{
			List<string> list = Load();
			list.RemoveAll(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
			list.Insert(0, Path.GetFullPath(root));
			if (list.Count > 12)
			{
				list.RemoveRange(12, list.Count - 12);
			}

			Save(list);
		}

		/// <summary>Drops a project from the list (the folder stays on disk).</summary>
		public static void Remove(string root)
		{
			List<string> list = Load();
			list.RemoveAll(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
			Save(list);
		}

		#endregion

		#region PrivateMethods

		private static void Save(List<string> list)
		{
			Directory.CreateDirectory(EditorDataFolder);
			File.WriteAllText(Path.Combine(EditorDataFolder, "recent.json"), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
		}

		#endregion
	}
}
