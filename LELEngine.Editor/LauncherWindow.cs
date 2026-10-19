using System;
using System.IO;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine.Editor
{
	/// <summary>
	///     The project chooser in a small operating-system window of its own, shown before the editor's window (as Unity
	///     Hub): recent projects, open a folder, create a project (<see cref="ProjectLauncher" />). It has its own GL and
	///     Dear ImGui contexts and is gone before the editor's window opens. <see cref="Choose" /> returns the folder of
	///     the chosen project, or null when the window was closed.
	/// </summary>
	internal sealed class LauncherWindow : GameWindow
	{
		#region PrivateFields

		private readonly string initialError;
		private ImGuiController imgui;
		private ProjectLauncher launcher;
		private string chosen;

		#endregion

		#region Constructors

		private LauncherWindow(Version apiVersion, string error)
			: base(
				new GameWindowSettings { UpdateFrequency = 0.0 },
				new NativeWindowSettings
				{
					ClientSize = new Vector2i(780, 580),
					Title = "LELEngine - Projects",
					APIVersion = apiVersion,
					Profile = ContextProfile.Core,
					Flags = ContextFlags.ForwardCompatible,
					WindowBorder = WindowBorder.Fixed,
					StartVisible = true,
					StartFocused = Window.StartFocused
				})
		{
			initialError = error;
			VSync = VSyncMode.On;
			CenterWindow();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Shows the chooser until a project is chosen (returns its folder) or the window is closed (null).
		///     <paramref name="error" /> is shown from the start (why the last project could not be opened).
		/// </summary>
		public static string Choose(string error)
		{
			using (LauncherWindow window = Create(error))
			{
				window.Run();
				return window.chosen;
			}
		}

		#endregion

		#region ProtectedMethods

		protected override void OnLoad()
		{
			base.OnLoad();
			imgui = new ImGuiController(this, Path.Combine(RecentProjects.EditorDataFolder, "launcher.ini"));
			EditorStyle.Apply();
			// Chosen inside the UI (or a dialog): the window closes on the next frame, not in the middle of this one.
			launcher = new ProjectLauncher(this, path => chosen = path, initialError);
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			base.OnUpdateFrame(e);
			if (chosen != null)
			{
				Close();
				return;
			}

			// System dialogs the UI asked for run here, outside the ImGui frame.
			launcher.Update();
		}

		protected override void OnRenderFrame(FrameEventArgs e)
		{
			base.OnRenderFrame(e);
			if (chosen != null || WindowState == WindowState.Minimized || ClientSize.X <= 0 || ClientSize.Y <= 0)
			{
				System.Threading.Thread.Sleep(20);
				return;
			}

			imgui.NewFrame((float)e.Time, ClientSize.X, ClientSize.Y);
			try
			{
				launcher.Draw();
			}
			catch (Exception exception)
			{
				Console.WriteLine("[Launcher] " + exception);
			}

			imgui.EndFrame();

			Framebuffer.BindDefault(ClientSize.X, ClientSize.Y);
			GL.ClearColor(0.11f, 0.11f, 0.12f, 1f);
			GL.Clear(ClearBufferMask.ColorBufferBit);
			imgui.RenderDrawData(ClientSize.X, ClientSize.Y);
			SwapBuffers();
		}

		protected override void OnUnload()
		{
			imgui?.Dispose();
			imgui = null;
			base.OnUnload();
		}

		#endregion

		#region PrivateMethods

		/// <summary>With the newest OpenGL context the driver grants, as the editor's window.</summary>
		private static LauncherWindow Create(string error)
		{
			Version version = Window.PreferredApiVersion;
			while (true)
			{
				try
				{
					return new LauncherWindow(version, error);
				}
				catch (Exception e) when (version > Window.MinimumApiVersion)
				{
					Console.WriteLine($"[Launcher] OpenGL {version} context unavailable ({e.GetType().Name}), trying an older version");
					version = version.Minor > 0 ? new Version(version.Major, version.Minor - 1) : Window.MinimumApiVersion;
				}
			}
		}

		#endregion
	}
}
