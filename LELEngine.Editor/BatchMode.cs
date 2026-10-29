using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using LELEngine.Rendering;
using LELEngine.Serialization;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor without UI, for build machines and scripts (Unity-style command line, see <see cref="Usage" />).
	///     Opens the project (writing missing .meta files and the IDE project), compiles the scripts when they changed,
	///     then runs the requested steps in this order: open the scene, execute the method, build the game. Batch mode
	///     always quits when done. Exit code: 0 success, 1 a step failed (bad arguments, compile errors, an exception
	///     or a false / non-zero result of the method, a failed build); a non-zero int returned by the method becomes
	///     the exit code.
	///     The OpenGL context (materials compile their shaders when loaded) is created only when a step needs it, in a
	///     hidden window.
	/// </summary>
	internal static class BatchMode
	{
		#region PublicFields

		public const string Usage =
			"LELEngine editor\n" +
			"  LELEngine.Editor [-projectPath <folder>]            open the editor (with this project)\n" +
			"  LELEngine.Editor.Console -batchmode -projectPath <folder> [options]\n" +
			"                             no UI; the console twin of the editor, for terminals and build machines\n" +
			"                             (the shell waits for it and gets the exit code; LELEngine.Editor -batchmode\n" +
			"                             works too but a shell does not wait for a window application)\n" +
			"Batch mode options:\n" +
			"  -createProject             create the project when the folder holds none (starter assets, default scene)\n" +
			"  -scene <asset path>        open this scene (default: the last or start scene; with -executeMethod / -listen)\n" +
			"  -executeMethod <Type.Method>\n" +
			"                             call a static method of the scripts (Editor folders included), the engine or\n" +
			"                             the editor: no parameters\n" +
			"                             or (string[] args) with the whole command line; void, bool, int, Task, Task<bool>\n" +
			"                             or Task<int> result. It runs inside the editor, which draws no UI: the editor\n" +
			"                             scripts' windows, menus and inspectors exist, EditorAutomation works (window\n" +
			"                             screenshots are drawn off screen on request)\n" +
			"  -listen                    serve editor automation commands until 'quit' (LELEngine.Editor.Console -command help)\n" +
			"  -runTests                  run the project's unit tests (scripts in Tests folders) inside the editor; a\n" +
			"                             failed test, a test that does not compile or a filter matching none fails the run\n" +
			"  -testFilter <text>         only tests whose full name (Namespace.Class.Method(args)) contains the text\n" +
			"  -testCategory <name>       only tests of this [Category]\n" +
			"  -testResults <file.xml>    write the results as JUnit XML\n" +
			"  -build <folder>            build the game into the folder\n" +
			"  -buildConfig Release|Debug configuration of the build (default Release)\n" +
			"  -selfContained [true|false]\n" +
			"                             ship the .NET runtime with the game (default: the project's Build setting)\n" +
			"  -logFile <file>            also write the log to the file\n" +
			"  -quit                      accepted for Unity compatibility (batch mode always quits)\n" +
			"Exit code: 0 success, 1 failure.\n" +
			Automation.AutomationClient.Usage;

		#endregion

		#region PrivateFields

		private static NativeWindow graphics;

		#endregion

		#region PublicMethods

		public static int Run(string[] args)
		{
			CommandLine options = CommandLine.Parse(args);
			StreamWriter logFile = null;
			try
			{
				string logPath = options.Get("logfile");
				if (logPath != null)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
					logFile = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
					TextWriter file = TextWriter.Synchronized(logFile);
					Console.SetOut(new TeeWriter(Console.Out, file));
					Console.SetError(new TeeWriter(Console.Error, file));
				}

				int code = Execute(options, args);
				Console.WriteLine(code == 0 ? "[Batch] Done" : $"[Batch] Failed (exit code {code})");
				return code;
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				Console.WriteLine("[Batch] Failed (exit code 1)");
				return 1;
			}
			finally
			{
				SceneManager.UnloadAll(true);
				graphics?.Dispose();
				graphics = null;
				Console.Out.Flush();
				logFile?.Dispose();
			}
		}

		#endregion

		#region PrivateMethods

		private static int Execute(CommandLine options, string[] args)
		{
			// 1. Project.
			string projectPath = options.Get("projectpath");
			if (string.IsNullOrEmpty(projectPath))
			{
				Debug.LogError("[Batch] -projectPath <folder> is required.\n" + Usage);
				return 1;
			}

			projectPath = Path.GetFullPath(projectPath);
			bool created = false;
			if (!ProjectInfo.IsProjectFolder(projectPath))
			{
				if (!options.Has("createproject"))
				{
					Debug.LogError("[Batch] Not a LELEngine project: " + projectPath + " (add -createProject to create it)");
					return 1;
				}

				ProjectInfo.Create(Path.GetDirectoryName(projectPath), Path.GetFileName(projectPath), EditorApplication.TemplateAssetsPath);
				created = true;
				Debug.Log("[Batch] Created project " + projectPath);
			}

			// A method, the tests or automation commands run inside the editor itself, without UI (BatchEditor); a build
			// alone needs only the project (and never compiles the tests).
			if (options.Has("executemethod") || options.Has("listen") || options.Has("runtests"))
			{
				if (options.Has("executemethod") && string.IsNullOrEmpty(options.Get("executemethod")))
				{
					Debug.LogError("[Batch] -executeMethod needs a method name (Namespace.Type.Method).");
					return 1;
				}

				return BatchEditor.Run(options, args, projectPath, options.Has("build") ? opened => BuildGame(opened, options, false) : null);
			}

			ProjectInfo project = ProjectInfo.Load(projectPath);
			project.LoadProjectSettings();
			AssetDatabase.Initialize(project.AssetsPath, true);
			ModelAssets.CacheFolder = project.ModelCachePath;
			// Models new or changed since the last import import completely (materials too), as in the editor.
			foreach (string model in ModelAssets.ImportOutdated(true))
			{
				Debug.Log("[Batch] Imported " + model);
			}

			ProjectGenerator.Generate(project);
			Debug.Log($"[Batch] Opened project {project.Name} ({project.RootPath})");

			if (created && !string.IsNullOrEmpty(project.StartScene) && !File.Exists(AssetDatabase.ToAbsolute(project.StartScene)))
			{
				EnsureGraphics();
				DefaultScene.CreateAsset(project, project.StartScene);
				Debug.Log("[Batch] Created the scene " + project.StartScene);
			}

			// 2. Scripts.
			if (!CompileScripts(project))
			{
				return 1;
			}

			// 3. Game build.
			if (options.Has("build") && !BuildGame(project, options, true))
			{
				return 1;
			}

			return 0;
		}

		/// <summary>-build: the game into the folder (<paramref name="ownGraphics" />: make batch mode's own GL context; the editor has one).</summary>
		private static bool BuildGame(ProjectInfo project, CommandLine options, bool ownGraphics)
		{
			string output = options.Get("build");
			string configuration = options.Get("buildconfig") ?? "Release";
			bool release = string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase);
			// -selfContained alone means true.
			string selfContainedValue = options.Get("selfcontained");
			bool selfContained = options.Has("selfcontained") ? selfContainedValue == null || string.Equals(selfContainedValue, "true", StringComparison.OrdinalIgnoreCase) : project.BuildSelfContained;
			bool validSelfContained = selfContainedValue == null || string.Equals(selfContainedValue, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(selfContainedValue, "false", StringComparison.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(output) || (!release && !string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase)) || !validSelfContained)
			{
				Debug.LogError("[Batch] Usage: -build <folder> [-buildConfig Release|Debug] [-selfContained [true|false]]");
				return false;
			}

			// The GL context bakes the meshes' distance fields (and lets model import extract materials).
			if (ownGraphics)
			{
				EnsureGraphics();
			}

			var settings = new BuildSettings { OutputFolder = Path.GetFullPath(output), Release = release, SelfContained = selfContained, StartScene = project.StartScene, Scenes = new List<string>(project.BuildScenes) };
			return GameBuilder.Build(project, settings, line => Console.WriteLine((line.StartsWith("Error", StringComparison.Ordinal) ? "[Error] " : "") + "[Build] " + line));
		}

		/// <summary>
		///     Compiles the scripts when the editor build is out of date (the editor scripts too, so their errors fail the run)
		///     and loads Game.dll (types for the scenes the build reads).
		/// </summary>
		private static bool CompileScripts(ProjectInfo project)
		{
			if (!ScriptDomain.HasScripts(project))
			{
				return true;
			}

			bool withEditor = ScriptDomain.HasEditorScripts(project);
			string dll = ScriptDomain.AssemblyFile(project);
			string editorDll = ScriptDomain.EditorAssemblyFile(project);
			byte[] assembly;
			byte[] symbols;
			if (ScriptDomain.IsAssemblyCurrent(project, withEditor))
			{
				Debug.Log("[Batch] Scripts are up to date");
				// Built before the weaver existed (or by an IDE): rewritten before they load.
				var woven = new BuildResult();
				if (!ScriptBuilder.Weave(dll, Path.GetDirectoryName(dll), woven) || (withEditor && !ScriptBuilder.Weave(editorDll, Path.GetDirectoryName(editorDll), woven)))
				{
					Debug.LogError("[Scripts] " + woven.Diagnostics[0]);
					return false;
				}

				assembly = File.ReadAllBytes(dll);
				symbols = ReadSymbols(dll);
			}
			else
			{
				Debug.Log(withEditor ? "[Batch] Compiling scripts (with the editor scripts)..." : "[Batch] Compiling scripts...");
				string sources = ScriptDomain.SourceStamp(project);
				BuildResult result = ScriptBuilder.Build(ProjectGenerator.ProjectFile(project), "Debug", null, withEditor);
				foreach (BuildDiagnostic diagnostic in result.Diagnostics)
				{
					if (diagnostic.IsError)
					{
						Debug.LogError("[Scripts] " + diagnostic);
					}
					else
					{
						Debug.LogWarning("[Scripts] " + diagnostic);
					}
				}

				if (!result.Success)
				{
					Debug.LogError($"[Batch] Script compilation failed ({Math.Max(1, result.ErrorCount)} errors)");
					return false;
				}

				ScriptDomain.RecordSources(project, sources, withEditor);
				Debug.Log($"[Batch] Scripts compiled ({result.Seconds:0.0} s), {result.WovenChecks} null checks woven");
				assembly = result.Assembly;
				symbols = result.Symbols;
			}

			// The game's types (scenes read for the build). The editor scripts are compiled (a build with errors there fails
			// too) but not loaded: methods and commands run in the editor itself (BatchEditor).
			var context = new ScriptDomain.GameLoadContext(Path.GetDirectoryName(dll));
			TypeRegistry.Register(context.Load(assembly, symbols));
			return true;
		}

		private static byte[] ReadSymbols(string dll)
		{
			string pdb = Path.ChangeExtension(dll, ".pdb");
			return File.Exists(pdb) ? File.ReadAllBytes(pdb) : null;
		}

		/// <summary>Hidden window owning the OpenGL context (newest version the driver grants, 4.6 down to 4.3).</summary>
		private static void EnsureGraphics()
		{
			if (graphics != null)
			{
				return;
			}

			// A refused version has to come back as an exception this loop catches: OpenTK's own GLFW error callback
			// throws inside GLFW and aborts the process (Mesa grants 4.5, so 4.6 is refused there).
			Window.InitializeGlfw();
			Version version = Window.PreferredApiVersion;
			while (true)
			{
				try
				{
					graphics = new NativeWindow(new NativeWindowSettings
					{
						ClientSize = new OpenTK.Mathematics.Vector2i(64, 64),
						Title = "LELEngine batch",
						StartVisible = false,
						APIVersion = version,
						Profile = ContextProfile.Core,
						Flags = ContextFlags.ForwardCompatible
					});
					break;
				}
				catch (Exception e) when (version > Window.MinimumApiVersion)
				{
					Console.WriteLine($"[Batch] OpenGL {version} context unavailable ({e.GetType().Name}), trying an older version");
					version = version.Minor > 0 ? new Version(version.Major, version.Minor - 1) : Window.MinimumApiVersion;
				}
			}

			graphics.MakeCurrent();
			Console.WriteLine("[Batch] OpenGL " + GL.GetString(StringName.Version) + " | " + GL.GetString(StringName.Renderer));
			GLCapabilities.Initialize();
		}

		#endregion

		#region NestedTypes

		/// <summary>Writes to the console and the log file.</summary>
		private sealed class TeeWriter : TextWriter
		{
			private readonly TextWriter first;
			private readonly TextWriter second;

			public TeeWriter(TextWriter first, TextWriter second)
			{
				this.first = first;
				this.second = second;
			}

			public override Encoding Encoding => first.Encoding;

			public override void Write(char value)
			{
				first.Write(value);
				second.Write(value);
			}

			public override void Write(string value)
			{
				first.Write(value);
				second.Write(value);
			}

			public override void WriteLine(string value)
			{
				first.WriteLine(value);
				second.WriteLine(value);
			}

			public override void Flush()
			{
				first.Flush();
				second.Flush();
			}
		}

		#endregion
	}

	/// <summary>
	///     Unity-style command line: "-name value" (or "-name=value"; names are case-insensitive, leading dashes
	///     optional for "name=value"). A flag without a value has a null value.
	/// </summary>
	internal sealed class CommandLine
	{
		#region PrivateFields

		private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		public static CommandLine Parse(string[] args)
		{
			var line = new CommandLine();
			for (int i = 0; i < args.Length; i++)
			{
				string arg = args[i];
				bool dashed = arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1;
				string name = arg.TrimStart('-');
				int eq = name.IndexOf('=');
				string value = null;
				if (eq > 0)
				{
					value = name.Substring(eq + 1).Trim('"');
					name = name.Substring(0, eq);
				}
				else if (!dashed)
				{
					continue;
				}
				else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
				{
					value = args[++i];
				}

				// The first occurrence wins.
				line.values.TryAdd(name, value);
			}

			return line;
		}

		public static bool HasFlag(string[] args, string name)
		{
			return Parse(args).Has(name);
		}

		public bool Has(string name)
		{
			return values.ContainsKey(name);
		}

		public string Get(string name)
		{
			return values.TryGetValue(name, out string value) ? value : null;
		}

		#endregion
	}
}
