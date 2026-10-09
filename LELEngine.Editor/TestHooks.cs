using System;
using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>
	///     Command line actions for automated editor tests (screenshots of a scripted session):
	///     select=ObjectName, addcomponent=ObjectName;Type.Name, playframe=N, stopframe=N, saveframe=N,
	///     touchscript=N;path (rewrites a script file at frame N to trigger a rebuild), buildgame=N;outputFolder,
	///     focusscene=N, nudgecamera=N, dumpview=f1,f2,...;folder, reportframes=f1,f2,..., focuswindow=N;Name,
	///     maximize=N, restore=N, grow=N, slowframes=ms.
	/// </summary>
	internal sealed class TestHooks
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private int frame;
		private bool selected;
		private bool componentAdded;
		private readonly System.Diagnostics.Stopwatch frameClock = System.Diagnostics.Stopwatch.StartNew();
		private double lastFrameMs;

		#endregion

		#region Constructors

		public TestHooks(EditorApplication editor, string[] args)
		{
			this.editor = editor;
			foreach (string arg in args)
			{
				int eq = arg.IndexOf('=');
				if (eq > 0)
				{
					values[arg.Substring(0, eq)] = arg.Substring(eq + 1).Trim('"');
				}
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Window the next UI frame brings to the front (focuswindow=).</summary>
		public string PendingFocus { get; set; }

		public void Update()
		{
			frame++;
			// maximize=N / restore=N / grow=N (10 px larger): window size changes; slowframes=ms: logs every frame
			// slower than that (also on the project launcher).
			if (Is("maximize")) editor.Window.WindowState = OpenTK.Windowing.Common.WindowState.Maximized;
			if (Is("restore")) editor.Window.WindowState = OpenTK.Windowing.Common.WindowState.Normal;
			if (Is("grow")) editor.Window.ClientSize += new OpenTK.Mathematics.Vector2i(10, 10);
			if (values.TryGetValue("slowframes", out string slow) && double.TryParse(slow, out double slowMs))
			{
				double now = frameClock.Elapsed.TotalMilliseconds;
				if (frame > 2 && now - lastFrameMs > slowMs)
				{
					Debug.Log($"[Test] Frame {frame}: {now - lastFrameMs:0.0} ms ({editor.Window.ClientSize.X}x{editor.Window.ClientSize.Y})");
				}

				lastFrameMs = now;
			}

			Scene scene = editor.Scene;
			if (scene == null)
			{
				return;
			}

			if (!selected && values.TryGetValue("select", out string selectName) && frame > 5)
			{
				GameObject go = Find(scene, selectName);
				if (go != null)
				{
					editor.Select(go);
					selected = true;
				}
			}

			if (!componentAdded && values.TryGetValue("addcomponent", out string add) && frame > 60 && editor.Scripts != null && !editor.Scripts.IsBuilding)
			{
				string[] parts = add.Split(';');
				GameObject go = Find(scene, parts[0]);
				Type type = parts.Length > 1 ? TypeRegistry.Resolve(parts[1]) : null;
				if (go != null && type != null)
				{
					editor.RecordStructural("Add " + type.Name, () => go.AddComponent(type));
					componentAdded = true;
					Debug.Log("[Test] Added " + type.Name + " to " + go.Name);
				}
			}

			if (Is("playframe")) editor.EnterPlayMode();
			if (Is("stopframe")) editor.ExitPlayMode();
			if (Is("saveframe")) editor.SaveScene();
			// reportframes=f1,f2,...: how many frames each view rendered so far (on-demand rendering).
			if (values.TryGetValue("reportframes", out string report) && Array.IndexOf(report.Split(','), frame.ToString()) >= 0)
			{
				Debug.Log($"[Test] Frame {frame}: scene view rendered {editor.SceneView.ViewRenderer?.Renderer.FrameIndex ?? 0}, game view {editor.GameView.ViewRenderer?.Renderer.FrameIndex ?? 0}");
			}

			// focusscene=N: Scene tab to the front at frame N; nudgecamera=N: turn the scene camera by 20 degrees.
			if (Is("focusscene")) editor.SceneView.Focus();

			// focuswindow=N;Name: that window to the front at frame N (consumed by the next UI frame).
			if (values.TryGetValue("focuswindow", out string focus))
			{
				string[] parts = focus.Split(';');
				if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame)
				{
					PendingFocus = parts[1];
				}
			}
			if (Is("nudgecamera")) editor.SceneView.RotateView(20f);

			// dumpview=f1,f2,...;folder: the scene view image of each listed frame into folder/fNNNN (lossless).
			if (values.TryGetValue("dumpview", out string dump))
			{
				string[] parts = dump.Split(';');
				if (parts.Length == 2 && editor.SceneView.ViewRenderer != null)
				{
					foreach (string item in parts[0].Split(','))
					{
						if (int.TryParse(item, out int at) && at == frame)
						{
							editor.SceneView.ViewRenderer.Renderer.FrameDumper.Start(System.IO.Path.Combine(parts[1], "f" + at.ToString("0000")), 1);
							editor.SceneView.RequestRender();
						}
					}
				}
			}

			if (values.TryGetValue("buildgame", out string build))
			{
				// buildgame=N;outputFolder: builds the game (Release) at frame N, synchronously.
				string[] parts = build.Split(';');
				if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame && editor.Project != null)
				{
					var settings = new BuildSettings { OutputFolder = parts[1], Release = true, StartScene = editor.Project.StartScene, Scenes = new List<string>(editor.Project.BuildScenes) };
					bool ok = GameBuilder.Build(editor.Project, settings, line => Console.WriteLine("[Build] " + line));
					Debug.Log("[Test] Game build " + (ok ? "succeeded" : "failed"));
				}
			}

			if (values.TryGetValue("touchscript", out string touch))
			{
				string[] parts = touch.Split(';');
				if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame)
				{
					string text = System.IO.File.ReadAllText(parts[1]);
					System.IO.File.WriteAllText(parts[1], text.Replace("/*v1*/", "/*v2*/"));
					Debug.Log("[Test] Touched " + parts[1]);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private bool Is(string name)
		{
			return values.TryGetValue(name, out string value) && int.TryParse(value, out int at) && at == frame;
		}

		private static GameObject Find(Scene scene, string name)
		{
			foreach (GameObject go in scene.GameObjects)
			{
				if (go.Name == name)
				{
					return go;
				}
			}

			return null;
		}

		#endregion
	}
}
