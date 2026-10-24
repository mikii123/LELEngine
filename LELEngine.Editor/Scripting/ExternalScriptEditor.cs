using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LELEngine.Editor
{
	/// <summary>
	///     Opens source files at a line in the script editor the preferences choose
	///     (<see cref="ExternalToolsPreferences" />): Rider, Visual Studio, Visual Studio Code (found where they install),
	///     a custom command with $(File), $(Line), $(Column), $(ProjectPath), $(SolutionPath), or the system's application
	///     for the file (no line). Auto takes the first installed of Rider, Visual Studio, Visual Studio Code.
	/// </summary>
	internal static class ExternalScriptEditor
	{
		#region PrivateFields

		private static string vsCode;
		private static string rider;
		private static string visualStudio;
		private static bool searched;

		#endregion

		#region PublicMethods

		/// <summary>Opens <paramref name="file" /> (absolute) at <paramref name="line" />; false (and a warning) when it cannot.</summary>
		public static bool Open(string file, int line, int column = 1)
		{
			if (string.IsNullOrEmpty(file) || !File.Exists(file))
			{
				Debug.LogWarning("[Editor] Cannot open the script: no file " + file);
				return false;
			}

			ExternalToolsPreferences tools = EditorPreferences.Current.ExternalTools;
			ScriptEditorKind kind = Resolve(tools.ScriptEditor);
			string exe = Executable(kind, tools);
			string arguments = exe == null ? null : Arguments(kind, tools).Replace("$(File)", file).Replace("$(Line)", Math.Max(1, line).ToString())
				.Replace("$(Column)", Math.Max(1, column).ToString()).Replace("$(ProjectPath)", ProjectPath()).Replace("$(SolutionPath)", SolutionPath());
			try
			{
				Process.Start(exe == null ? new ProcessStartInfo(file) { UseShellExecute = true } : new ProcessStartInfo(exe, arguments) { UseShellExecute = true });
				return true;
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Editor] Cannot open {file} with {(exe ?? "the system's application")}: {e.Message}");
				return false;
			}
		}

		/// <summary>The editor <paramref name="kind" /> stands for here (Auto: the one it picks), and what runs (Preferences).</summary>
		public static string Describe(ScriptEditorKind kind)
		{
			ExternalToolsPreferences tools = EditorPreferences.Current.ExternalTools;
			ScriptEditorKind resolved = Resolve(kind);
			string exe = Executable(resolved, tools);
			string name = DisplayName(resolved);
			if (resolved == ScriptEditorKind.SystemDefault)
			{
				return kind == ScriptEditorKind.Auto ? "None of Rider, Visual Studio, Visual Studio Code found: the system's application for .cs files (no line)" : "The system's application for .cs files (opens no line)";
			}

			if (exe == null)
			{
				return name + " not found: the system's application opens files";
			}

			return (kind == ScriptEditorKind.Auto ? "Auto: " + name + " - " : "") + exe + " " + Arguments(resolved, tools);
		}

		/// <summary>An installed editor of the kind was found (Preferences mark the missing ones).</summary>
		public static bool IsInstalled(ScriptEditorKind kind)
		{
			Search();
			return kind switch
			{
				ScriptEditorKind.VisualStudioCode => vsCode != null,
				ScriptEditorKind.Rider => rider != null,
				ScriptEditorKind.VisualStudio => visualStudio != null,
				_ => true
			};
		}

		#endregion

		#region PrivateMethods

		private static ScriptEditorKind Resolve(ScriptEditorKind kind)
		{
			if (kind != ScriptEditorKind.Auto)
			{
				return kind;
			}

			Search();
			return rider != null ? ScriptEditorKind.Rider : visualStudio != null ? ScriptEditorKind.VisualStudio : vsCode != null ? ScriptEditorKind.VisualStudioCode : ScriptEditorKind.SystemDefault;
		}

		private static string Executable(ScriptEditorKind kind, ExternalToolsPreferences tools)
		{
			Search();
			return kind switch
			{
				ScriptEditorKind.VisualStudioCode => vsCode,
				ScriptEditorKind.Rider => rider,
				ScriptEditorKind.VisualStudio => visualStudio,
				ScriptEditorKind.Custom => string.IsNullOrWhiteSpace(tools.CustomPath) ? null : tools.CustomPath.Trim().Trim('"'),
				_ => null
			};
		}

		private static string Arguments(ScriptEditorKind kind, ExternalToolsPreferences tools)
		{
			return kind switch
			{
				// The project folder opens (or the window that has it comes forward), then the file at the line.
				ScriptEditorKind.VisualStudioCode => "\"$(ProjectPath)\" -g \"$(File)\":$(Line):$(Column)",
				// The solution first (as Unity's Rider integration): Rider opens it, or brings the window that has it
				// forward, then the file. A file alone opens in LightEdit mode, outside the solution.
				ScriptEditorKind.Rider => (File.Exists(SolutionPath()) ? "\"$(SolutionPath)\" " : "") + "--line $(Line) --column $(Column) \"$(File)\"",
				ScriptEditorKind.VisualStudio => "/edit \"$(File)\" /command \"Edit.GoTo $(Line)\"",
				ScriptEditorKind.Custom => tools.CustomArguments,
				_ => ""
			};
		}

		private static string DisplayName(ScriptEditorKind kind)
		{
			return kind switch
			{
				ScriptEditorKind.VisualStudioCode => "Visual Studio Code",
				ScriptEditorKind.VisualStudio => "Visual Studio",
				ScriptEditorKind.SystemDefault => "System default",
				_ => kind.ToString()
			};
		}

		private static string ProjectPath()
		{
			return EditorApplication.Instance?.Project?.RootPath ?? "";
		}

		private static string SolutionPath()
		{
			ProjectInfo project = EditorApplication.Instance?.Project;
			return project != null ? ProjectGenerator.SolutionFile(project) : "";
		}

		/// <summary>Where the editors install (once per run).</summary>
		private static void Search()
		{
			if (searched)
			{
				return;
			}

			searched = true;
			string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
			string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			string programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
			vsCode = FirstFile(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(programs, "Microsoft VS Code", "Code.exe"));
			rider = FirstFile(Path.Combine(local, "Programs", "Rider", "bin", "rider64.exe"), Path.Combine(local, "JetBrains", "Toolbox", "scripts", "rider.cmd"))
				?? Newest(Path.Combine(programs, "JetBrains"), "JetBrains Rider*", Path.Combine("bin", "rider64.exe"))
				?? Newest(Path.Combine(local, "Programs"), "Rider*", Path.Combine("bin", "rider64.exe"));
			visualStudio = VisualStudioFromVsWhere(Path.Combine(programsX86, "Microsoft Visual Studio", "Installer", "vswhere.exe"));
		}

		private static string FirstFile(params string[] candidates)
		{
			return candidates.FirstOrDefault(File.Exists);
		}

		/// <summary><paramref name="relative" /> in the newest folder of <paramref name="parent" /> matching <paramref name="pattern" />.</summary>
		private static string Newest(string parent, string pattern, string relative)
		{
			try
			{
				return Directory.Exists(parent)
					? Directory.GetDirectories(parent, pattern).OrderByDescending(folder => folder, StringComparer.OrdinalIgnoreCase).Select(folder => Path.Combine(folder, relative)).FirstOrDefault(File.Exists)
					: null;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return null;
			}
		}

		private static string VisualStudioFromVsWhere(string vswhere)
		{
			if (!File.Exists(vswhere))
			{
				return null;
			}

			try
			{
				using Process process = Process.Start(new ProcessStartInfo(vswhere, "-latest -property productPath") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
				string path = process?.StandardOutput.ReadToEnd().Trim();
				process?.WaitForExit(3000);
				return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
			}
			catch (Exception e) when (e is IOException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
			{
				return null;
			}
		}

		#endregion
	}
}
