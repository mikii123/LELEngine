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
			"  -scene <asset path>        open this scene (default: the last or start scene, only with -executeMethod)\n" +
			"  -executeMethod <Type.Method>\n" +
			"                             call a static method of the scripts, the engine or the editor: no parameters\n" +
			"                             or (string[] args) with the whole command line; void, bool or int result\n" +
			"  -build <folder>            build the game into the folder\n" +
			"  -buildConfig Release|Debug configuration of the build (default Release)\n" +
			"  -logFile <file>            also write the log to the file\n" +
			"  -quit                      accepted for Unity compatibility (batch mode always quits)\n" +
			"Exit code: 0 success, 1 failure.";

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
				SceneManager.ActiveScene?.Unload();
				SceneManager.SetActiveScene(null);
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

			ProjectInfo project = ProjectInfo.Load(projectPath);
			AssetDatabase.Initialize(project.AssetsPath, true);
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

			// 3. Scene and method.
			string methodName = options.Get("executemethod");
			if (options.Has("executemethod") && string.IsNullOrEmpty(methodName))
			{
				Debug.LogError("[Batch] -executeMethod needs a method name (Namespace.Type.Method).");
				return 1;
			}

			string scenePath = options.Get("scene");
			if (scenePath != null || methodName != null)
			{
				if (!OpenScene(project, scenePath))
				{
					return 1;
				}
			}

			if (methodName != null)
			{
				int code = ExecuteMethod(methodName, args);
				if (code != 0)
				{
					return code;
				}
			}

			// 4. Game build.
			if (options.Has("build"))
			{
				string output = options.Get("build");
				string configuration = options.Get("buildconfig") ?? "Release";
				bool release = string.Equals(configuration, "Release", StringComparison.OrdinalIgnoreCase);
				if (string.IsNullOrEmpty(output) || (!release && !string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase)))
				{
					Debug.LogError("[Batch] Usage: -build <folder> [-buildConfig Release|Debug]");
					return 1;
				}

				var settings = new BuildSettings { OutputFolder = Path.GetFullPath(output), Release = release, StartScene = project.StartScene, Scenes = new List<string>(project.BuildScenes) };
				if (!GameBuilder.Build(project, settings, line => Console.WriteLine((line.StartsWith("Error", StringComparison.Ordinal) ? "[Error] " : "") + "[Build] " + line)))
				{
					return 1;
				}
			}

			return 0;
		}

		/// <summary>Compiles the scripts when the editor build is out of date and loads them (types for scenes and methods).</summary>
		private static bool CompileScripts(ProjectInfo project)
		{
			if (!ScriptDomain.HasScripts(project))
			{
				return true;
			}

			string dll = ScriptDomain.AssemblyFile(project);
			byte[] assembly;
			byte[] symbols;
			if (ScriptDomain.IsAssemblyCurrent(project))
			{
				Debug.Log("[Batch] Scripts are up to date");
				assembly = File.ReadAllBytes(dll);
				string pdb = Path.ChangeExtension(dll, ".pdb");
				symbols = File.Exists(pdb) ? File.ReadAllBytes(pdb) : null;
			}
			else
			{
				Debug.Log("[Batch] Compiling scripts...");
				BuildResult result = ScriptBuilder.Build(ProjectGenerator.ProjectFile(project), "Debug", null);
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

				Debug.Log($"[Batch] Scripts compiled ({result.Seconds:0.0} s)");
				assembly = result.Assembly;
				symbols = result.Symbols;
			}

			// Batch runs never reload: the default context is enough.
			TypeRegistry.Register(symbols != null ? Assembly.Load(assembly, symbols) : Assembly.Load(assembly));
			return true;
		}

		private static bool OpenScene(ProjectInfo project, string requested)
		{
			string assetPath = requested ?? project.LastScene ?? project.StartScene;
			if (assetPath == null || !File.Exists(AssetDatabase.ToAbsolute(assetPath)))
			{
				if (requested != null)
				{
					Debug.LogError("[Batch] Scene not found: " + requested);
					return false;
				}

				Debug.Log("[Batch] The project has no scene to open");
				return true;
			}

			EnsureGraphics();
			Scene scene = SceneSerializer.Load(AssetDatabase.ToAbsolute(assetPath));
			scene.Path = assetPath;
			SceneManager.SetActiveScene(scene);
			scene.StartLifecycle(false);
			Debug.Log("[Batch] Opened scene " + assetPath);
			return true;
		}

		private static int ExecuteMethod(string name, string[] args)
		{
			int dot = name.LastIndexOf('.');
			if (dot <= 0 || dot == name.Length - 1)
			{
				Debug.LogError("[Batch] -executeMethod expects Namespace.Type.Method: " + name);
				return 1;
			}

			string typeName = name.Substring(0, dot);
			string memberName = name.Substring(dot + 1);
			Type type = FindType(typeName);
			if (type == null)
			{
				Debug.LogError("[Batch] Type not found: " + typeName);
				return 1;
			}

			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
			object[] parameters = null;
			MethodInfo method = type.GetMethod(memberName, flags, null, Type.EmptyTypes, null);
			if (method == null)
			{
				method = type.GetMethod(memberName, flags, null, new[] { typeof(string[]) }, null);
				parameters = new object[] { args };
			}

			if (method == null)
			{
				Debug.LogError($"[Batch] {type.FullName} has no static method {memberName}() or {memberName}(string[])");
				return 1;
			}

			EnsureGraphics();
			Debug.Log("[Batch] Executing " + type.FullName + "." + method.Name);
			object result;
			try
			{
				result = method.Invoke(null, parameters);
			}
			catch (TargetInvocationException e)
			{
				Debug.LogException(e.InnerException ?? e);
				return 1;
			}

			if (result is bool success && !success)
			{
				Debug.LogError($"[Batch] {method.Name} returned false");
				return 1;
			}

			if (result is int code && code != 0)
			{
				Debug.LogError($"[Batch] {method.Name} returned {code}");
				return code;
			}

			return 0;
		}

		/// <summary>Full name in the scripts, the engine or the editor; a simple name when it is unique.</summary>
		private static Type FindType(string name)
		{
			var assemblies = new List<Assembly>(TypeRegistry.Assemblies);
			if (!assemblies.Contains(typeof(BatchMode).Assembly))
			{
				assemblies.Add(typeof(BatchMode).Assembly);
			}

			Type bySimpleName = null;
			int simpleMatches = 0;
			foreach (Assembly assembly in assemblies)
			{
				Type type = assembly.GetType(name, false);
				if (type != null)
				{
					return type;
				}

				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = Array.FindAll(e.Types, t => t != null);
				}

				foreach (Type candidate in types)
				{
					if (candidate.Name == name)
					{
						bySimpleName = candidate;
						simpleMatches++;
					}
				}
			}

			return simpleMatches == 1 ? bySimpleName : null;
		}

		/// <summary>Hidden window owning the OpenGL context (newest version the driver grants, 4.6 down to 4.3).</summary>
		private static void EnsureGraphics()
		{
			if (graphics != null)
			{
				return;
			}

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
