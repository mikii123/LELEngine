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
	///       ProjectSettings/   Project.json (name, scenes in the build, start scene, build options; editor only),
	///                          ProjectSettings.asset (physics, lighting quality; goes into builds)
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

		/// <summary>Builds ship the .NET runtime (the self-contained player): they run on machines without .NET installed.</summary>
		public bool BuildSelfContained { get; set; }

		[JsonIgnore] public string RootPath { get; private set; }
		[JsonIgnore] public string AssetsPath => Path.Combine(RootPath, "Assets");
		[JsonIgnore] public string LibraryPath => Path.Combine(RootPath, "Library");

		/// <summary>Imported models (see <see cref="ModelAssets" />), per asset GUID.</summary>
		[JsonIgnore] public string ModelCachePath => Path.Combine(LibraryPath, "ModelCache");
		[JsonIgnore] public string SettingsPath => Path.Combine(RootPath, "ProjectSettings");
		[JsonIgnore] public string SettingsFile => Path.Combine(SettingsPath, "Project.json");

		/// <summary>The runtime project settings document (<see cref="LELEngine.ProjectSettings" />).</summary>
		[JsonIgnore] public string ProjectSettingsFile => Path.Combine(SettingsPath, LELEngine.ProjectSettings.FileName);

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

		/// <summary>Creates a project in the folder <paramref name="parentFolder" />/<paramref name="name" /> (see <see cref="CreateIn" />).</summary>
		public static ProjectInfo Create(string parentFolder, string name, string templateAssets)
		{
			return CreateIn(Path.Combine(parentFolder, name), name, templateAssets);
		}

		/// <summary>
		///     Creates the folder structure in <paramref name="folder" /> (created when missing) and copies the starter
		///     assets of the template. An existing folder keeps its files: starter assets and .gitignore are only added
		///     where nothing has their name. A folder that is a project already is refused.
		/// </summary>
		public static ProjectInfo CreateIn(string folder, string name, string templateAssets)
		{
			if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			{
				throw new ArgumentException("Invalid project name: '" + name + "'");
			}

			string root = Path.GetFullPath(folder);
			if (IsProjectFolder(root))
			{
				throw new InvalidOperationException("The folder is a LELEngine project already: " + root);
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

			string gitignore = Path.Combine(root, ".gitignore");
			if (!File.Exists(gitignore))
			{
				File.WriteAllText(gitignore, "Library/\nBuild/\nbin/\nobj/\n.vs/\n.idea/\n*.user\n");
			}

			project.Save();
			return project;
		}

		/// <summary>
		///     Reads Project.json again into this object (changed outside the editor: git, a text editor), so everyone
		///     holding the project sees the new scenes and options. False, keeping the values, when it cannot be read.
		/// </summary>
		public bool ReloadSettingsFile()
		{
			ProjectInfo read;
			try
			{
				read = JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(SettingsFile), JsonOptions);
			}
			catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
			{
				Debug.LogWarning("[Editor] Project.json changed on disk but cannot be read (" + e.Message + "); keeping the current values");
				return false;
			}

			if (read == null)
			{
				return false;
			}

			Name = string.IsNullOrEmpty(read.Name) ? Name : read.Name;
			StartScene = read.StartScene;
			BuildScenes = read.BuildScenes ?? new List<string>();
			LastScene = read.LastScene;
			ReloadScriptsOnPlay = read.ReloadScriptsOnPlay;
			RunInBackground = read.RunInBackground;
			BuildSelfContained = read.BuildSelfContained;
			return true;
		}

		public void Save()
		{
			Directory.CreateDirectory(SettingsPath);
			File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
		}

		/// <summary>Makes the project's settings document the live <see cref="LELEngine.ProjectSettings" />, writing the defaults when it has none.</summary>
		public void LoadProjectSettings()
		{
			bool existed = File.Exists(ProjectSettingsFile);
			LELEngine.ProjectSettings settings = LELEngine.ProjectSettings.Load(ProjectSettingsFile);
			if (!existed)
			{
				settings.Save(ProjectSettingsFile);
			}
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

				// A project created in an existing folder keeps its own files.
				string destination = Path.Combine(target, Path.GetRelativePath(source, file));
				if (!File.Exists(destination))
				{
					File.Copy(file, destination, false);
				}
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
