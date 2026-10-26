using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LELEngine.Editor
{
	internal static class Program
	{
		#region PrivateMethods

		[STAThread]
		private static int Main(string[] args)
		{
			CommandLine options = CommandLine.Parse(args);
			bool help = options.Has("help") || options.Has("h");

			// -command / -script: a client of a running editor's automation server (nothing of the editor starts here).
			if (options.Has("command") || options.Has("script"))
			{
				bool ownConsole = ConsoleHost.Ensure();
				int code = Automation.AutomationClient.Run(args);
				ConsoleHost.Release(ownConsole);
				return code;
			}

			// Model import: Assimp for the formats beyond .obj, and the imports' materials as project assets.
			ModelAssets.RegisterBackend(new AssimpModelBackend());
			ModelAssets.MaterialExtractor = new ModelMaterialExtractor();

			// Using a destroyed object names the line that destroyed it (ObjectDestroyedException).
			EngineObject.RecordDestroyStackTraces = true;

			// -batchmode: no window, no UI, a console (build machines, scripts).
			if (help || options.Has("batchmode"))
			{
				bool ownConsole = ConsoleHost.Ensure();
				int code = help ? ShowHelp() : BatchMode.Run(args);
				ConsoleHost.Release(ownConsole);
				return code;
			}

			return RunEditor(args);
		}

		/// <summary>
		///     The project chooser in its own small window first (no editor window exists yet), then the editor's window
		///     on the chosen project. A command line naming a project (project=, -projectPath, newproject=: automated
		///     runs) opens the editor directly. File > Close Project shows the chooser again from the editor
		///     (<see cref="EditorApplication" />): its window, GL context and caches stay for the next project.
		/// </summary>
		private static int RunEditor(string[] args)
		{
			// The driver's binaries of the programs linked before (keyed by their final sources: an edited shader compiles
			// again), before the first window builds a shader; shadercache=0 compiles everything, as the player's switch.
			if (Array.IndexOf(args, "shadercache=0") < 0)
			{
				LELEngine.Shaders.ProgramBinaryCache.Folder =Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LELEngine", "Editor", "ShaderCache");
			}

			// The user's preferences first: the theme applies to the first window.
			EditorPreferences.Initialize(args);
			ImGuiController.ReadOnlyLayouts = EditorApplication.IsAutomatedRun(args);
			HierarchyExpansion.Initialize(args);

			// focus=0: automated runs open their windows without taking the keyboard focus.
			Window.StartFocused = Array.IndexOf(args, "focus=0") < 0;
			if (!EditorApplication.OpensProjectDirectly(args))
			{
				string chosen = LauncherWindow.Choose(null);
				if (chosen == null)
				{
					return 0;
				}

				args = WithProject(args, chosen);
			}

			using (MainWindow window = MainWindow.Create(args))
			{
				window.Run();
			}

			return 0;
		}

		/// <summary>The command line with the chosen project instead of any project given before.</summary>
		private static string[] WithProject(string[] args, string project)
		{
			var result = new System.Collections.Generic.List<string>();
			for (int i = 0; i < args.Length; i++)
			{
				string arg = args[i];
				if (arg.StartsWith("project=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("newproject=", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (string.Equals(arg, "-projectPath", StringComparison.OrdinalIgnoreCase))
				{
					i++;
					continue;
				}

				result.Add(arg);
			}

			result.Add("project=" + project);
			return result.ToArray();
		}

		private static int ShowHelp()
		{
			Console.WriteLine(BatchMode.Usage);
			return 0;
		}

		#endregion
	}

	/// <summary>
	///     Console of batch mode. The editor is a window application (no console when it shows its UI); in batch mode it
	///     writes to the terminal it was started from, or to a console window of its own when there is none (started
	///     from Explorer). Redirected output (build machines) and the console twin LELEngine.Editor.Console.exe need
	///     nothing.
	/// </summary>
	internal static class ConsoleHost
	{
		#region PrivateFields

		private const int AttachParentProcess = -1;
		private const int StdOutputHandle = -11;
		private const uint FileTypeUnknown = 0;

		#endregion

		#region PublicMethods

		/// <summary>Makes the console output visible; true when a console window was created for this process.</summary>
		public static bool Ensure()
		{
			if (!OperatingSystem.IsWindows() || GetConsoleWindow() != IntPtr.Zero || IsOutputRedirected())
			{
				return false;
			}

			if (AttachConsole(AttachParentProcess))
			{
				ReopenStreams();
				// The calling shell printed its prompt already: start on a fresh line.
				Console.WriteLine();
				return false;
			}

			if (AllocConsole())
			{
				ReopenStreams();
				return true;
			}

			return false;
		}

		/// <summary>A console window of its own would close with the process: keep it until a key is pressed.</summary>
		public static void Release(bool ownConsole)
		{
			if (!ownConsole)
			{
				return;
			}

			Console.WriteLine("Press any key to close.");
			try
			{
				Console.ReadKey(true);
			}
			catch (InvalidOperationException)
			{
				// No keyboard input available.
			}
		}

		#endregion

		#region PrivateMethods

		private static bool IsOutputRedirected()
		{
			IntPtr handle = GetStdHandle(StdOutputHandle);
			return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != FileTypeUnknown;
		}

		private static void ReopenStreams()
		{
			var output = new StreamWriter(new FileStream("CONOUT$", FileMode.Open, FileAccess.Write)) { AutoFlush = true };
			Console.SetOut(output);
			Console.SetError(output);
			Console.SetIn(new StreamReader(new FileStream("CONIN$", FileMode.Open, FileAccess.Read)));
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool AttachConsole(int processId);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool AllocConsole();

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetConsoleWindow();

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetStdHandle(int handle);

		[DllImport("kernel32.dll")]
		private static extern uint GetFileType(IntPtr handle);

		#endregion
	}
}
