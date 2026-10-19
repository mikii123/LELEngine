using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LELEngine.Rendering;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;

namespace LELEngine.Player
{
	/// <summary>
	///     Runs a game build: reads boot.json next to the executable, opens the window with the splash screen as its
	///     first image, and meanwhile loads the game in the background (<see cref="GameLoader" />: the data folder's
	///     index, the project settings, the scripts assembly, the renderer, the start scene). The start scene replaces
	///     the splash screen once it has loaded and the splash screen's time is up; then the game loop runs. A window
	///     application (no console): the log goes to Player.log, with the startup times.
	///     Command line overrides (testing): data=folder game=assembly.dll scene=assetPath fullscreen=0|1
	///     screenshot=folder screenshotframe=N exitframe=N focus=0.
	/// </summary>
	internal static class Program
	{
		#region PrivateFields

		// Startup times in the log count from the process start: the .NET runtime's part (until Main) plus this clock.
		private static Stopwatch clock;
		private static DateTime mainStartUtc;
		private static double runtimeStartupMs = -1;

		#endregion

		#region PrivateMethods

		private static int Main(string[] args)
		{
			clock = Stopwatch.StartNew();
			mainStartUtc = DateTime.UtcNow;
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

			if (string.IsNullOrEmpty(startScene))
			{
				Console.WriteLine("[Player] No start scene in " + BootConfig.FileName);
				return 1;
			}

			// While the window opens: the splash screen's logo is read, and the loading that needs no GL context starts.
			Task<SplashScreen> splashFile = Task.Run(() => SplashScreen.Load(boot.SplashScreen, baseDirectory));
			var loader = new GameLoader(
				dataFolder,
				string.IsNullOrEmpty(boot.ProjectSettings) ? null : Path.GetFullPath(Path.Combine(baseDirectory, boot.ProjectSettings)),
				string.IsNullOrEmpty(gameAssembly) ? null : Path.GetFullPath(Path.Combine(baseDirectory, gameAssembly)),
				startScene);
			loader.Start();

			// The window opens hidden and at its final size and state; it appears with the splash screen drawn, so its
			// first image is never blank. The engine's shaders compile later, on the loader thread.
			Window.StartFocused = GetValue(args, "focus") != "0";
			Game.CreateWindow(new GameWindowOptions
			{
				Width = boot.Width,
				Height = boot.Height,
				Title = boot.Title,
				Fullscreen = fullscreen == "1" || (fullscreen == null && boot.Fullscreen),
				Visible = false,
				CreateRenderer = false
			});
			GameHost host = Game.Mono;
			host.RunInBackground = boot.RunInBackground;
			Action<FramebufferResizeEventArgs> resized = e => loader.RequestSplashRedraw(e.Width, e.Height);
			host.FramebufferResize += resized;

			SplashScreen splash = splashFile.Result;
			try
			{
				Vector2i size = host.FramebufferSize;
				host.Show(() => splash.Draw(size.X, size.Y));
				var shown = Stopwatch.StartNew();
				double shownAt = clock.Elapsed.TotalMilliseconds;

				bool loaded = WaitForLoader(host, loader, splash);
				Log(boot.SplashScreen != null
					? $"Splash screen shown {SinceStart(shownAt)} ms after the start"
					: $"Window shown {SinceStart(shownAt)} ms after the start (no splash screen)");
				if (!loaded)
				{
					return loader.Error != null ? Fail(loader.Error) : 0;
				}

				Log($"Loaded in the background ({loader.Summary}), {SinceStart(clock.Elapsed.TotalMilliseconds)} ms after the start");

				// The rest of the splash screen's time, when the loading took less.
				while (!host.IsExiting && shown.Elapsed.TotalSeconds < splash.Duration)
				{
					RedrawSplashIfResized(host, loader, splash);
					host.ProcessEvents(Math.Max(0.001, splash.Duration - shown.Elapsed.TotalSeconds));
				}
			}
			finally
			{
				host.FramebufferResize -= resized;
				splash.Dispose();
			}

			if (host.IsExiting)
			{
				return 0;
			}

			InstallTestHooks(args);
			bool first = true;
			host.RenderFrame += _ =>
			{
				if (first)
				{
					first = false;
					Log($"Start scene's first frame {SinceStart(clock.Elapsed.TotalMilliseconds)} ms after the start");
				}
			};

			// The scene's lifecycle starts when the window loads (GameHost.OnLoad), on this thread.
			host.Run();
			return 0;
		}

		/// <summary>
		///     Keeps the window responsive while the loader works: hands it the GL context when it asks (the splash
		///     screen is up by then), redraws the splash screen after a resize while this thread holds the context, and
		///     takes the context back when the loader is done. False when the game does not start: the window was
		///     closed (the loader stops after its current step) or the loading failed.
		/// </summary>
		private static bool WaitForLoader(GameHost host, GameLoader loader, SplashScreen splash)
		{
			bool holdsContext = true;
			bool closed = false;
			while (!loader.IsDone)
			{
				if (host.IsExiting && !closed)
				{
					closed = true;
					host.IsVisible = false;
					loader.Cancel();
				}

				if (holdsContext && loader.WantsContext && !closed)
				{
					Vector2i size = host.ClientSize;
					host.Context.MakeNoneCurrent();
					holdsContext = false;
					loader.GiveContext(host, splash, size.X, size.Y);
				}

				if (holdsContext)
				{
					RedrawSplashIfResized(host, loader, splash);
				}

				// The loader wakes this wait when it is done; its context request waits for the timeout at most.
				host.ProcessEvents(0.01);
			}

			if (!holdsContext)
			{
				host.Context.MakeCurrent();
			}

			return !closed && loader.Error == null && !host.IsExiting;
		}

		private static void RedrawSplashIfResized(GameHost host, GameLoader loader, SplashScreen splash)
		{
			if (loader.TakeSplashRedraw(out int width, out int height))
			{
				splash.Draw(width, height);
				host.Present();
			}
		}

		private static int Fail(Exception error)
		{
			Debug.LogException(error);
			return 1;
		}

		private static void Log(string message)
		{
			Console.WriteLine("[Player] " + message);
		}

		/// <summary>Milliseconds from the process start to a time of Main's clock (the runtime's startup is read once, off the critical path).</summary>
		private static long SinceStart(double clockMs)
		{
			if (runtimeStartupMs < 0)
			{
				try
				{
					runtimeStartupMs = Math.Max(0, (mainStartUtc - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds);
				}
				catch (Exception)
				{
					runtimeStartupMs = 0;
				}
			}

			return (long)(runtimeStartupMs + clockMs);
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
