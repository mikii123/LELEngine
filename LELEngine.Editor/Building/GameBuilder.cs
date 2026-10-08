using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine.Editor
{
	internal sealed class BuildSettings
	{
		public string OutputFolder;
		public bool Release = true;
		public string StartScene;
		public List<string> Scenes = new List<string>();
	}

	/// <summary>
	///     Produces a runnable game folder:
	///     <code>
	///     Output/
	///       &lt;Name&gt;.exe + LELEngine.Player.dll, engine and OpenTK assemblies, native GLFW (runtimes/)
	///       Shaders/Engine/   engine shader library
	///       Game.dll          the scripts, compiled in the chosen configuration
	///       Data/             the project's assets with their .meta files (scripts excluded)
	///       boot.json         start scene, window title
	///     </code>
	///     Runs on a background thread; <paramref name="log" /> is called from it.
	/// </summary>
	internal static class GameBuilder
	{
		#region PublicMethods

		public static bool Build(ProjectInfo project, BuildSettings settings, Action<string> log)
		{
			string output = Path.GetFullPath(settings.OutputFolder);
			string playerSource = Path.Combine(AppContext.BaseDirectory, "Player");
			if (!File.Exists(Path.Combine(playerSource, "LELEngine.Player.dll")))
			{
				log("Error: the player is missing next to the editor (" + playerSource + ").");
				return false;
			}

			if (IsInside(output, project.AssetsPath))
			{
				log("Error: the build folder must not be inside Assets.");
				return false;
			}

			if (string.IsNullOrEmpty(settings.StartScene))
			{
				log("Error: choose a start scene.");
				return false;
			}

			if (!File.Exists(Path.Combine(project.AssetsPath, settings.StartScene)))
			{
				log("Error: the start scene does not exist: " + settings.StartScene);
				return false;
			}

			// 1. Scripts.
			bool hasScripts = Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories).GetEnumerator().MoveNext();
			string scriptsOutput = Path.Combine(project.LibraryPath, "PlayerScripts", settings.Release ? "Release" : "Debug");
			if (hasScripts)
			{
				log($"Compiling scripts ({(settings.Release ? "Release" : "Debug")})...");
				ProjectGenerator.Generate(project);
				BuildResult scripts = ScriptBuilder.Build(ProjectGenerator.ProjectFile(project), settings.Release ? "Release" : "Debug", scriptsOutput);
				foreach (BuildDiagnostic diagnostic in scripts.Diagnostics)
				{
					log(diagnostic.ToString());
				}

				if (!scripts.Success)
				{
					log("Error: the scripts do not compile; nothing was written.");
					return false;
				}
			}

			Directory.CreateDirectory(output);

			// 2. Player and engine.
			log("Copying the player...");
			string executable = ProjectGenerator.ProjectName(project) + ".exe";
			foreach (string file in Directory.GetFiles(playerSource, "*", SearchOption.AllDirectories))
			{
				string relative = Path.GetRelativePath(playerSource, file);
				if (relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && settings.Release)
				{
					continue;
				}

				// The executable takes the game's name; it still starts LELEngine.Player.dll.
				string target = string.Equals(relative, "LELEngine.Player.exe", StringComparison.OrdinalIgnoreCase) ? executable : relative;
				Copy(file, Path.Combine(output, target));
			}

			// 3. Scripts (and the packages they use).
			if (hasScripts)
			{
				foreach (string file in Directory.GetFiles(scriptsOutput))
				{
					string name = Path.GetFileName(file);
					bool symbols = name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
					if ((symbols && settings.Release) || File.Exists(Path.Combine(output, name)) && !name.StartsWith(ProjectGenerator.AssemblyName + ".", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					Copy(file, Path.Combine(output, name));
				}
			}

			// 4. Assets (everything but the scripts; the meta files keep the GUIDs the scenes reference).
			log("Copying assets...");
			string data = Path.Combine(output, "Data");
			int copied = 0;
			foreach (string file in Directory.GetFiles(project.AssetsPath, "*", SearchOption.AllDirectories))
			{
				string relative = Path.GetRelativePath(project.AssetsPath, file);
				if (relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".cs.meta", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				Copy(file, Path.Combine(data, relative));
				copied++;
			}

			// 5. Boot configuration.
			new BootConfig
			{
				DataFolder = "Data",
				GameAssembly = hasScripts ? ProjectGenerator.AssemblyName + ".dll" : null,
				StartScene = settings.StartScene,
				Title = project.Name,
				RunInBackground = project.RunInBackground
			}.Save(Path.Combine(output, BootConfig.FileName));

			log($"Done: {Path.Combine(output, executable)} ({copied} asset files).");
			return true;
		}

		#endregion

		#region PrivateMethods

		private static void Copy(string source, string target)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.Copy(source, target, true);
		}

		private static bool IsInside(string path, string folder)
		{
			string a = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string b = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
		}

		#endregion
	}
}
