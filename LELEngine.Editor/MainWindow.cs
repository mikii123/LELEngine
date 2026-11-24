using System;
using System.ComponentModel;
using System.IO;
using LELEngine.Rendering;
using LELEngine.Graphics;
using LELEngine.Mathematics;
using LELEngine.Windowing;

namespace LELEngine.Editor
{
	/// <summary>The editor's main window: one GL context shared by the UI and the scene / game view renderers.</summary>
	internal sealed class MainWindow : Window
	{
		#region PrivateFields

		private readonly string[] args;
		private ImGuiController imgui;
		private EditorApplication editor;
		private int frame;
		private readonly string screenshotFolder;
		private readonly int[] screenshotFrames = { 120 };
		private readonly int exitFrame;
		private readonly FrameDumper screenshotDumper = new FrameDumper();

		// loadingshots=folder: per step of the project's load the frame shown while it runs, folder/load01, load02, ... (tests).
		private readonly string loadingShotsFolder;
		private string lastLoadingShot;
		private int loadingShots;
		private bool firstFrameLogged;

		// Milliseconds after the process started: the window's GL context made (OnLoad), the editor made (its project's
		// first step included).
		private double contextMs;
		private double editorMs;

		/// <summary>The window's place is remembered between runs (not for automated runs: they must not move it or depend on it).</summary>
		private readonly bool rememberPlacement;

		private readonly WindowPlacement placement;

		#endregion

		#region Constructors

		private MainWindow(Version apiVersion, string[] args)
			// Hidden until its place is restored (see Create).
			: base(1600, 900, "LELEngine Editor", apiVersion, false, false)
		{
			this.args = args;
			rememberPlacement = !EditorApplication.IsAutomatedRun(args);
			placement = new WindowPlacement(this, "window.json");
			VSync = VSyncMode.On;
			foreach (string arg in args)
			{
				int eq = arg.IndexOf('=');
				if (eq <= 0)
				{
					continue;
				}

				string name = arg.Substring(0, eq);
				string value = arg.Substring(eq + 1).Trim('"');
				if (name == "screenshot") screenshotFolder = value;
				else if (name == "screenshotframe") screenshotFrames = Array.ConvertAll(value.Split(',', StringSplitOptions.RemoveEmptyEntries), item => int.TryParse(item, out int at) ? at : -1);
				else if (name == "exitframe") int.TryParse(value, out exitFrame);
				else if (name == "loadingshots") loadingShotsFolder = value;
				else if (name == "progressshots") ProgressOverlay.ShotsFolder = value;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Creates the window on the graphics API asked for (<see cref="Graphics.GraphicsDevice.RequestedApi" />), on
		///     OpenGL with the newest context the driver grants (4.6 down to 4.3) where this machine has no other.
		/// </summary>
		public static MainWindow Create(string[] args)
		{
			// focus=0: automated runs open the window without taking the keyboard focus (hidden=1: there is none to focus).
			StartFocused = Array.IndexOf(args, "focus=0") < 0 && !StartHidden;
			// On the graphics API asked for (GraphicsDevice.RequestedApi: the project's), OpenGL where this machine has no other.
			MainWindow window = Open(version => new MainWindow(version, args));
			window.placement.Restore(window.rememberPlacement);
			return window;
		}

		#endregion

		#region ProtectedMethods

		protected override void OnLoad()
		{
			base.OnLoad();
			contextMs = SinceProcessStart();
			string layout = ImGuiController.LayoutFileOverride ?? Path.Combine(RecentProjects.EditorDataFolder, "layout.ini");
			bool layoutExisted = File.Exists(layout);
			imgui = new ImGuiController(this, layout);
			EditorStyle.Apply();
			// The progress bar's own thread and window, before the project's load begins (it shows that too).
			StartProgressBar();
			editor = new EditorApplication(this, layoutExisted ? layout : null, args);
			EditorAutomation.Start(editor, args);
			editorMs = SinceProcessStart();
		}

		private unsafe void StartProgressBar()
		{
			if (OperatingSystem.IsWindows())
			{
				// A main thread busy for 5 s would make Windows wash the window out white as "Not Responding" (its ghost
				// window); the progress bar's black dim and panel show what it is busy with instead.
				DisableProcessWindowsGhosting();
				ProgressOverlay.Start(Win32Window);
			}
		}

		[System.Runtime.InteropServices.DllImport("user32.dll")]
		private static extern void DisableProcessWindowsGhosting();

		private static double SinceProcessStart()
		{
			return (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
		}

		private void StartWithoutUI()
		{
			MakeCurrent();
			base.OnLoad();
			string layout = Path.Combine(RecentProjects.EditorDataFolder, "layout.ini");
			imgui = new ImGuiController(this, layout);
			EditorStyle.Apply();
			editor = new EditorApplication(this, File.Exists(layout) ? layout : null, args, false);
		}

		/// <summary>
		///     The editor without UI (batch mode): the window stays hidden and owns the GL context, its ImGui context only
		///     serves window captures (fonts, style, the user's layout read but never saved), and nothing runs frames but
		///     <see cref="TickWithoutUI" />.
		/// </summary>
		public static MainWindow CreateWithoutUI(string[] args)
		{
			MainWindow window = Open(version => new MainWindow(version, args));
			window.StartWithoutUI();
			return window;
		}

		/// <summary>The editor (null until the window loaded).</summary>
		public EditorApplication Editor => editor;

		/// <summary>The main window's ImGui context.</summary>
		public ImGuiController Controller => imgui;

		/// <summary>Makes this window's GL and ImGui contexts current again (after another editor window used its own).</summary>
		public void MakeEditorCurrent()
		{
			MakeCurrent();
			imgui?.Activate();
		}

		/// <summary>One tick of the editor without UI: time, the editor's update, what a UI frame does besides drawing, automation.</summary>
		public void TickWithoutUI(double deltaTime)
		{
			ProcessEvents(0);
			OnUpdateFrame(new FrameEventArgs(deltaTime));
			editor.FrameWithoutUI();
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			// A frame of the editor for the hitch profiler (LEL_HITCH): this update (in play mode: the game's) and the
			// render that follows.
			FrameProfiler.BeginFrame();
			base.OnUpdateFrame(e);
			if (rememberPlacement)
			{
				placement.Update();
			}

			using (FrameProfiler.Begin(editor.IsPlaying ? "Editor update (the game's frame in play mode)" : "Editor update"))
			{
				editor.Update((float)e.Time);
			}

			// Between two frames: the last one is drawn but not presented yet (window captures read it), and no UI frame
			// is being built (automation drives the editor from here).
			using (FrameProfiler.Begin("Automation commands"))
			{
				EditorAutomation.Pump();
			}
		}

		protected override void OnRenderFrame(FrameEventArgs e)
		{
			// Minimized: nothing is shown, and without a wait the loop would spin (presenting does not block).
			if (WindowState == WindowState.Minimized || ClientSize.X <= 0 || ClientSize.Y <= 0)
			{
				System.Threading.Thread.Sleep(50);
				FrameProfiler.EndFrame();
				return;
			}

			PresentPendingFrame();

			using (FrameProfiler.Begin("Editor UI"))
			{
				imgui.NewFrame((float)e.Time, ClientSize.X, ClientSize.Y);
				TestAnchors.NextFrame();
				EditorMenu.NewFrame();
				Automation.WindowCapture.UIFrameDepth++;
				try
				{
					editor.DrawUI();
				}
				finally
				{
					Automation.WindowCapture.UIFrameDepth--;
				}

				imgui.EndFrame();
			}

			// The views shown by this frame's UI render now, so the UI draws their current images.
			using (FrameProfiler.Begin("Views rendered (Scene, Game)"))
			{
				editor.RenderViews();
			}

			using (FrameProfiler.Begin("Editor UI drawn"))
			{
				CommandList commands = Gfx.Commands;
				commands.BeginRenderPass(Gfx.Device.Backbuffer, null, LoadOperation.Clear, new ClearValue(0.11f, 0.11f, 0.12f, 1f));
				commands.SetViewport(0, 0, ClientSize.X, ClientSize.Y);
				imgui.RenderDrawData(ClientSize.X, ClientSize.Y);
			}

			// Other windows of the editor (UI Builder) draw and present themselves now, in their own GL context.
			editor.RenderExtraWindows((float)e.Time);

			// Testing aid: screenshot=folder screenshotframe=N writes frame N of the window (screenshotframe=a,b,c: each
			// into folder/fNNNN); exitframe=N closes. Frames count once the project is loaded (it loads over frames, see
			// EditorApplication.OpenProject), so frame N is the scene's Nth frame as before.
			if (editor.IsLoadingProject)
			{
				CaptureLoadingStep();
			}
			else
			{
				frame++;
				if (screenshotFolder != null && Array.IndexOf(screenshotFrames, frame) >= 0)
				{
					screenshotDumper.Start(screenshotFrames.Length > 1 ? Path.Combine(screenshotFolder, "f" + frame.ToString("0000")) : screenshotFolder, 1);
					screenshotDumper.Capture(Gfx.Device.Backbuffer, ClientSize.X, ClientSize.Y);
				}
			}

			base.OnRenderFrame(e);
			FrameProfiler.EndFrame();
			if (!firstFrameLogged)
			{
				firstFrameLogged = true;
				Console.WriteLine($"[Editor] First frame drawn {SinceProcessStart():F0} ms after the process started (window and GL context at {contextMs:F0} ms, editor made at {editorMs:F0} ms)");
			}

			if (exitFrame > 0 && frame >= exitFrame)
			{
				editor.DiscardChangesOnClose();
				Close();
			}
		}

		// loadingshots=folder: per step of the project's load the frame that shows while it runs (the last one before it).
		private void CaptureLoadingStep()
		{
			string step = editor.LoadingStatus;
			if (loadingShotsFolder == null || step == null || step == lastLoadingShot || !editor.LoadingStepNext)
			{
				return;
			}

			lastLoadingShot = step;
			loadingShots++;
			string folder = Path.Combine(loadingShotsFolder, "load" + loadingShots.ToString("00"));
			Console.WriteLine($"[Test] Loading frame {loadingShots:00}: {step}");
			screenshotDumper.Start(folder, 1);
			screenshotDumper.Capture(Gfx.Device.Backbuffer, ClientSize.X, ClientSize.Y);
		}

		protected override void OnClosing(CancelEventArgs e)
		{
			if (editor != null && !editor.RequestClose())
			{
				e.Cancel = true;
			}

			base.OnClosing(e);
			if (!e.Cancel && rememberPlacement)
			{
				placement.Save();
			}
		}

		// The engine's Window does not raise the Resize event: the placement is told here.
		protected override void OnMove(WindowPositionEventArgs e)
		{
			base.OnMove(e);
			placement?.Track();
		}

		protected override void OnResize(ResizeEventArgs e)
		{
			base.OnResize(e);
			placement?.Track();
		}

		protected override void OnUnload()
		{
			ProgressOverlay.Stop();
			EditorAutomation.Shutdown();
			editor?.Dispose();
			imgui?.Dispose();
			base.OnUnload();
		}

		#endregion
	}
}
