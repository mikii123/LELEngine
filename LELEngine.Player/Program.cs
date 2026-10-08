using System;
using System.IO;
using System.Reflection;
using LELEngine.Serialization;

namespace LELEngine.Player
{
	/// <summary>
	///     Runs a game build: reads boot.json next to the executable, indexes the data folder, loads the game's
	///     scripts assembly and the start scene, then runs the game loop. A window application (no console): the log
	///     goes to Player.log.
	///     Command line overrides (testing): data=folder game=assembly.dll scene=assetPath fullscreen=0|1
	///     screenshot=folder screenshotframe=N exitframe=N.
	/// </summary>
	internal static class Program
	{
		#region PrivateMethods

		private static int Main(string[] args)
		{
			string baseDirectory = AppContext.BaseDirectory;
			BootConfig boot = BootConfig.Load(Path.Combine(baseDirectory, BootConfig.FileName));
			using (StreamWriter log = OpenLog(baseDirectory, boot.Title))
			{
				if (log != null)
				{
					TextWriter file = TextWriter.Synchronized(log);
					Console.SetOut(new TeeWriter(Console.Out, file));
					Console.SetError(new TeeWriter(Console.Error, file));
				}

				try
				{
					return Run(args, baseDirectory, boot);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
					return 1;
				}
				finally
				{
					Console.Out.Flush();
				}
			}
		}

		/// <summary>
		///     Player.log next to the executable; in the user's local app data when the game folder is read-only
		///     (installed under Program Files).
		/// </summary>
		private static StreamWriter OpenLog(string baseDirectory, string title)
		{
			string[] folders =
			{
				baseDirectory,
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LELEngine", string.IsNullOrWhiteSpace(title) ? "Game" : title)
			};

			foreach (string folder in folders)
			{
				try
				{
					Directory.CreateDirectory(folder);
					return new StreamWriter(Path.Combine(folder, "Player.log"), false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
				}
				catch (Exception)
				{
					// Not writable: try the next folder.
				}
			}

			return null;
		}

		private static int Run(string[] args, string baseDirectory, BootConfig boot)
		{
			string dataFolder = Path.GetFullPath(Path.Combine(baseDirectory, GetValue(args, "data") ?? boot.DataFolder));
			string gameAssembly = GetValue(args, "game") ?? boot.GameAssembly;
			string startScene = GetValue(args, "scene") ?? boot.StartScene;
			string fullscreen = GetValue(args, "fullscreen");

			if (!Directory.Exists(dataFolder))
			{
				Console.WriteLine("[Player] Data folder not found: " + dataFolder);
				return 1;
			}

			AssetDatabase.Initialize(dataFolder);

			Assembly game = null;
			if (!string.IsNullOrEmpty(gameAssembly))
			{
				string assemblyPath = Path.GetFullPath(Path.Combine(baseDirectory, gameAssembly));
				if (File.Exists(assemblyPath))
				{
					game = Assembly.LoadFrom(assemblyPath);
					TypeRegistry.Register(game);
				}
				else
				{
					Console.WriteLine("[Player] Game assembly not found: " + assemblyPath + " (running without scripts)");
				}
			}

			Window.StartFocused = GetValue(args, "focus") != "0";
			Game.CreateWindow(boot.Width, boot.Height, boot.Title);
			Game.Mono.RunInBackground = boot.RunInBackground;
			if (game != null)
			{
				Game.Mono.InitializeECSScope(game);
			}

			if (string.IsNullOrEmpty(startScene))
			{
				Console.WriteLine("[Player] No start scene in " + BootConfig.FileName);
				return 1;
			}

			string scenePath = Path.IsPathRooted(startScene) ? startScene : AssetDatabase.ToAbsolute(AssetDatabase.Resolve(startScene, null) ?? startScene);
			Scene scene = SceneSerializer.Load(scenePath);
			SceneManager.SetActiveScene(scene);

			if (fullscreen == "1" || (fullscreen == null && boot.Fullscreen))
			{
				Game.Mono.WindowState = OpenTK.Windowing.Common.WindowState.Fullscreen;
			}

			InstallTestHooks(args);

			// The scene's lifecycle starts when the window loads (GameHost.OnLoad).
			Game.Mono.Run();
			return 0;
		}

		/// <summary>Testing aid: screenshot=folder screenshotframe=N writes frame N, exitframe=N closes the game.</summary>
		private static void InstallTestHooks(string[] args)
		{
			string screenshotFolder = GetValue(args, "screenshot");
			int screenshotFrame = int.TryParse(GetValue(args, "screenshotframe"), out int s) ? s : 120;
			int exitFrame = int.TryParse(GetValue(args, "exitframe"), out int e) ? e : 0;
			if (screenshotFolder == null && exitFrame <= 0)
			{
				return;
			}

			int frame = 0;
			Game.Mono.UpdateFrame += _ =>
			{
				frame++;
				if (screenshotFolder != null && frame == screenshotFrame)
				{
					Game.Renderer?.FrameDumper.Start(screenshotFolder, 1);
				}

				if (exitFrame > 0 && frame >= exitFrame)
				{
					Game.Mono.Close();
				}
			};
		}

		private static string GetValue(string[] args, string name)
		{
			foreach (string arg in args)
			{
				int eq = arg.IndexOf('=');
				if (eq > 0 && string.Equals(arg.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase))
				{
					return arg.Substring(eq + 1).Trim();
				}
			}

			return null;
		}

		#endregion

		#region NestedTypes

		/// <summary>Writes to the console (when there is one or stdout is redirected) and the log file.</summary>
		private sealed class TeeWriter : TextWriter
		{
			private readonly TextWriter first;
			private readonly TextWriter second;

			public TeeWriter(TextWriter first, TextWriter second)
			{
				this.first = first;
				this.second = second;
			}

			public override System.Text.Encoding Encoding => first.Encoding;

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
}
