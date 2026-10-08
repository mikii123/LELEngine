using System;
using System.ComponentModel;
using System.IO;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;

namespace LELEngine.Editor
{
	/// <summary>The editor's main window: one GL context shared by the UI and the scene / game view renderers.</summary>
	internal sealed class EditorWindow : Window
	{
		#region PrivateFields

		private readonly string[] args;
		private ImGuiController imgui;
		private EditorApplication editor;
		private int frame;
		private readonly string screenshotFolder;
		private readonly int screenshotFrame = 120;
		private readonly int exitFrame;
		private readonly FrameDumper screenshotDumper = new FrameDumper();

		#endregion

		#region Constructors

		private EditorWindow(Version apiVersion, string[] args)
			: base(1600, 900, "LELEngine Editor", apiVersion)
		{
			this.args = args;
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
				else if (name == "screenshotframe") int.TryParse(value, out screenshotFrame);
				else if (name == "exitframe") int.TryParse(value, out exitFrame);
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Creates the window with the newest OpenGL context the driver grants (4.6 down to 4.3).</summary>
		public static EditorWindow Create(string[] args)
		{
			// focus=0: automated runs open the window without taking the keyboard focus.
			StartFocused = Array.IndexOf(args, "focus=0") < 0;
			Version version = PreferredApiVersion;
			while (true)
			{
				try
				{
					return new EditorWindow(version, args);
				}
				catch (Exception e) when (version > MinimumApiVersion)
				{
					Console.WriteLine($"[Window] OpenGL {version} context unavailable ({e.GetType().Name}), trying an older version");
					version = version.Minor > 0 ? new Version(version.Major, version.Minor - 1) : MinimumApiVersion;
				}
			}
		}

		#endregion

		#region ProtectedMethods

		protected override void OnLoad()
		{
			base.OnLoad();
			string layout = Path.Combine(RecentProjects.EditorDataFolder, "layout.ini");
			bool layoutExisted = File.Exists(layout);
			imgui = new ImGuiController(this, layout);
			EditorStyle.Apply();
			editor = new EditorApplication(this, layoutExisted ? layout : null, args);
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			base.OnUpdateFrame(e);
			editor.Update((float)e.Time);
		}

		protected override void OnRenderFrame(FrameEventArgs e)
		{
			// Minimized: nothing is shown, and without a wait the loop would spin (presenting does not block).
			if (WindowState == WindowState.Minimized || ClientSize.X <= 0 || ClientSize.Y <= 0)
			{
				System.Threading.Thread.Sleep(50);
				return;
			}

			PresentPendingFrame();

			imgui.NewFrame((float)e.Time, ClientSize.X, ClientSize.Y);
			editor.DrawUI();
			imgui.EndFrame();

			// The views shown by this frame's UI render now, so the UI draws their current images.
			editor.RenderViews();

			Framebuffer.BindDefault(ClientSize.X, ClientSize.Y);
			GL.ClearColor(0.11f, 0.11f, 0.12f, 1f);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
			imgui.RenderDrawData(ClientSize.X, ClientSize.Y);

			// Testing aid: screenshot=folder screenshotframe=N writes frame N of the window; exitframe=N closes.
			frame++;
			if (screenshotFolder != null && frame == screenshotFrame)
			{
				screenshotDumper.Start(screenshotFolder, 1);
				screenshotDumper.Capture(ClientSize.X, ClientSize.Y);
			}

			base.OnRenderFrame(e);
			if (exitFrame > 0 && frame >= exitFrame)
			{
				editor.DiscardChangesOnClose();
				Close();
			}
		}

		protected override void OnClosing(CancelEventArgs e)
		{
			if (editor != null && !editor.RequestClose())
			{
				e.Cancel = true;
			}

			base.OnClosing(e);
		}

		protected override void OnUnload()
		{
			editor?.Dispose();
			imgui?.Dispose();
			base.OnUnload();
		}

		#endregion
	}
}
