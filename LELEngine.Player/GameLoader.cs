using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using GLFW = OpenTK.Windowing.GraphicsLibraryFramework.GLFW;

namespace LELEngine.Player
{
	/// <summary>
	///     Loads a build's game on a background thread while the main thread keeps the window responsive behind the
	///     splash screen. The part that needs no GL context (the data folder's index, the project settings, the scripts
	///     assembly) starts at once and runs while the window opens; the rest (the renderer with the engine's shaders,
	///     the start scene, two warm-up frames) needs the window's context, which the main thread hands over once the
	///     splash screen is up (<see cref="WantsContext" />, <see cref="GiveContext" />) and takes back when
	///     <see cref="IsDone" />. Between its steps the loader redraws the splash screen when the window was resized
	///     (only the context's holder can draw) and stops when the window was closed (<see cref="Cancel" />).
	/// </summary>
	internal sealed class GameLoader
	{
		#region PublicFields

		/// <summary>The loader waits for the window's GL context.</summary>
		public bool WantsContext => wantsContext;

		/// <summary>Finished, failed or cancelled; it no longer holds the context.</summary>
		public bool IsDone => done;

		/// <summary>Why the loading failed (null: it did not).</summary>
		public Exception Error { get; private set; }

		/// <summary>Milliseconds per step, for the log.</summary>
		public string Summary
		{
			get
			{
				lock (timings)
				{
					return string.Join(", ", timings);
				}
			}
		}

		#endregion

		#region PrivateFields

		private readonly string dataFolder;
		private readonly string projectSettings;
		private readonly string gameAssembly;
		private readonly string startScene;
		private readonly Thread thread;
		private readonly ManualResetEventSlim contextGiven = new ManualResetEventSlim();
		private readonly List<string> timings = new List<string>();
		private volatile bool wantsContext;
		private volatile bool done;
		private volatile bool cancelled;
		private GameHost host;
		private SplashScreen splash;
		private int width;
		private int height;

		// Splash screen redraw after a resize: requested on the main thread, done by whoever holds the context.
		private long redrawSize;
		private int redrawRequested;

		#endregion

		#region Constructors

		/// <param name="dataFolder">The build's asset root.</param>
		/// <param name="projectSettings">The project settings document (null: the defaults).</param>
		/// <param name="gameAssembly">The scripts assembly (null: no scripts).</param>
		/// <param name="startScene">Asset path (or absolute path) of the start scene.</param>
		public GameLoader(string dataFolder, string projectSettings, string gameAssembly, string startScene)
		{
			this.dataFolder = dataFolder;
			this.projectSettings = projectSettings;
			this.gameAssembly = gameAssembly;
			this.startScene = startScene;
			thread = new Thread(Load) { Name = "Game loader", IsBackground = true };
		}

		#endregion

		#region PublicMethods

		public void Start()
		{
			thread.Start();
		}

		/// <summary>
		///     Main thread, after releasing the window's context there: the loader makes it current and creates the
		///     renderer at this size (the window's, read on the main thread).
		/// </summary>
		public void GiveContext(GameHost window, SplashScreen splashScreen, int windowWidth, int windowHeight)
		{
			host = window;
			splash = splashScreen;
			width = windowWidth;
			height = windowHeight;
			wantsContext = false;
			contextGiven.Set();
		}

		/// <summary>The window was closed: the loader stops before its next step.</summary>
		public void Cancel()
		{
			cancelled = true;
			contextGiven.Set();
		}

		/// <summary>The window's framebuffer has this size now (any thread): the splash screen is drawn again.</summary>
		public void RequestSplashRedraw(int framebufferWidth, int framebufferHeight)
		{
			Interlocked.Exchange(ref redrawSize, ((long)framebufferWidth << 32) | (uint)framebufferHeight);
			Interlocked.Exchange(ref redrawRequested, 1);
		}

		/// <summary>The context's holder: true once per requested redraw, with the framebuffer size to draw at.</summary>
		public bool TakeSplashRedraw(out int framebufferWidth, out int framebufferHeight)
		{
			bool requested = Interlocked.Exchange(ref redrawRequested, 0) != 0;
			long size = Interlocked.Read(ref redrawSize);
			framebufferWidth = (int)(size >> 32);
			framebufferHeight = (int)size;
			return requested;
		}

		#endregion

		#region PrivateMethods

		private void Load()
		{
			try
			{
				// No GL context needed: runs while the main thread opens the window.
				Step("assets", () => AssetDatabase.Initialize(dataFolder));
				if (projectSettings != null)
				{
					Step("settings", () => ProjectSettings.Load(projectSettings));
				}

				if (gameAssembly != null)
				{
					Step("scripts", LoadScripts);
				}

				// The rest needs the window's GL context: the main thread hands it over once the splash screen is up.
				wantsContext = true;
				contextGiven.Wait();
				if (cancelled)
				{
					return;
				}

				host.Context.MakeCurrent();
				Step("renderer", () => host.CreateRenderer(width, height));
				Step("scene", () =>
				{
					// Read and prepared for the GPU here; its lifecycle starts with the game loop (SceneManager.Lifecycle).
					if (SceneManager.LoadScene(startScene) == null)
					{
						throw new InvalidOperationException("Cannot load the start scene " + startScene);
					}
				});
				Step("first frames", WarmUp);
			}
			catch (OperationCanceledException)
			{
				// The window was closed.
			}
			catch (Exception e)
			{
				Error = e;
			}
			finally
			{
				if (host != null && host.Context.IsCurrent)
				{
					host.Context.MakeNoneCurrent();
				}

				done = true;
				if (host != null)
				{
					// Wakes the main thread's event wait (GLFW is initialized: the window handed its context over).
					GLFW.PostEmptyEvent();
				}
			}
		}

		private void Step(string name, Action action)
		{
			if (cancelled)
			{
				throw new OperationCanceledException();
			}

			long start = Stopwatch.GetTimestamp();
			action();
			long ms = (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
			lock (timings)
			{
				timings.Add(name + " " + ms + " ms");
			}

			if (host != null && host.Context.IsCurrent && TakeSplashRedraw(out int w, out int h))
			{
				splash.Draw(w, h);
				host.Present();
			}
		}

		private void LoadScripts()
		{
			if (File.Exists(gameAssembly))
			{
				TypeRegistry.Register(Assembly.LoadFrom(gameAssembly));
			}
			else
			{
				Console.WriteLine("[Player] Game assembly not found: " + gameAssembly + " (running without scripts)");
			}
		}

		/// <summary>
		///     The start scene rendered twice offscreen: the GI caches, distance fields and lazily built shaders are
		///     ready before the first frame is shown, and the GPU has finished that work when the main thread takes over.
		/// </summary>
		private void WarmUp()
		{
			try
			{
				host.WarmUp(2);
			}
			catch (Exception e)
			{
				// Only a head start: the game loop renders (and reports) the scene anyway.
				Debug.LogWarning("[Player] Rendering the start scene ahead failed: " + e.Message);
			}

			GL.Finish();
		}

		#endregion
	}
}
