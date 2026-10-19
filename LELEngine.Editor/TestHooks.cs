using System;
using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>
	///     Command line actions for automated editor tests (screenshots of a scripted session):
	///     select=ObjectName, addcomponent=ObjectName;Type.Name, playframe=N, stopframe=N, saveframe=N,
	///     touchscript=N;path (rewrites a script file at frame N to trigger a rebuild), buildgame=N;outputFolder,
	///     focusscene=N, nudgecamera=N, dumpview=f1,f2,...;folder, reportframes=f1,f2,..., focuswindow=N;Name,
	///     maximize=N, restore=N, grow=N, slowframes=ms, bakenav=N, projectsettings=N;Tab,
	///     createvfx=N;asset (writes the default VFX graph and opens it), vfxgraph=N;asset (opens the VFX Graph window),
	///     menuitem=N;Menu/Item|M;Menu/Other (runs [MenuItem]s of the editor scripts), addmodel=N;Models/Car.fbx|M;...
	///     (instantiates model files).
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

			// selectasset=path shows a project file in the inspector at frame 6 (materials, textures, ...).
			if (frame == 6 && values.TryGetValue("selectasset", out string assetPath))
			{
				editor.SelectAsset(assetPath);
			}

			// setskybox=N;Materials/Sky.material assigns the scene's skybox material at frame N.
			if (values.TryGetValue("setskybox", out string skybox))
			{
				string[] parts = skybox.Split(';');
				if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame)
				{
					scene.Settings.Environment.Skybox = InternalStorage.GetOrCreateMaterial(parts[1]);
					editor.MarkDirty();
					Debug.Log("[Test] Skybox set to " + parts[1]);
				}
			}

			if (!componentAdded && values.TryGetValue("addcomponent", out string add) && frame > 60 && editor.Scripts != null && !editor.Scripts.IsBuilding)
			{
				string[] parts = add.Split(';');
				GameObject go = Find(scene, parts[0]);
				Type type = parts.Length > 1 ? TypeRegistry.Resolve(parts[1]) : null;
				if (go != null && type != null)
				{
					editor.RecordStructural("Add " + type.Name, () => go.AddComponent(type)?.Reset());
					componentAdded = true;
					Debug.Log("[Test] Added " + type.Name + " to " + go.Name);
				}
			}

			// bakenav=N: bakes every NavMeshSurface of the scene at frame N.
			if (Is("bakenav"))
			{
				foreach (Navigation.NavMeshSurface surface in scene.FindObjectsOfType<Navigation.NavMeshSurface>())
				{
					surface.BuildNavMesh();
					Debug.Log($"[Test] Baked {surface.gameObject.Name}: {surface.NavMeshData?.Tiles.Length ?? 0} tiles, {surface.NavMeshData?.PolygonCount ?? 0} polygons");
				}

				editor.MarkDirty();
			}

			if (Is("playframe")) editor.EnterPlayMode();
			if (Is("stopframe")) editor.ExitPlayMode();
			if (Is("saveframe")) editor.SaveScene();
			// uibuilder=N;asset opens the UI Builder window at frame N with the asset (uibuilderscreenshot=N;folder dumps its frame N).
			if (values.TryGetValue("uibuilder", out string builder))
			{
				string[] parts = builder.Split(';');
				if (parts.Length >= 1 && int.TryParse(parts[0], out int at) && at == frame)
				{
					editor.OpenUIBuilder(parts.Length > 1 ? parts[1] : null);
				}
			}

			// uibuilderscreenshot=f1,f2,...;folder dumps the builder window at each listed frame into folder/fNNNN.
			if (values.TryGetValue("uibuilderscreenshot", out string builderShot))
			{
				string[] parts = builderShot.Split(';');
				if (parts.Length == 2)
				{
					foreach (string item in parts[0].Split(','))
					{
						if (int.TryParse(item, out int at) && at == frame)
						{
							UIBuilder.BuilderWindow.ScreenshotFolder = System.IO.Path.Combine(parts[1], "f" + at.ToString("0000"));
						}
					}
				}
			}

			// uibuildercmd=N:cmd|cmd;M:cmd runs builder commands (see UIBuilder.RunTestCommand) at the given frames.
			if (values.TryGetValue("uibuildercmd", out string builderCommands))
			{
				foreach (string entry in builderCommands.Split(';'))
				{
					int colon = entry.IndexOf(':');
					if (colon <= 0 || !int.TryParse(entry.Substring(0, colon), out int at) || at != frame)
					{
						continue;
					}

					UIBuilder.BuilderWindow window = editor.UIBuilderWindow;
					if (window == null)
					{
						Debug.LogWarning("[Test] UI Builder is not open at frame " + frame);
						continue;
					}

					foreach (string command in entry.Substring(colon + 1).Split('|'))
					{
						window.Builder.RunTestCommand(command);
					}
				}
			}
			// reportframes=f1,f2,...: how many frames each view rendered so far (on-demand rendering).
			if (values.TryGetValue("reportframes", out string report) && Array.IndexOf(report.Split(','), frame.ToString()) >= 0)
			{
				Debug.Log($"[Test] Frame {frame}: scene view rendered {editor.SceneView.ViewRenderer?.Renderer.FrameIndex ?? 0}, game view {editor.GameView.ViewRenderer?.Renderer.FrameIndex ?? 0}");
			}

			// createvfx=N;asset writes the default graph to the asset path at frame N and opens it in the VFX Graph window.
			if (values.TryGetValue("createvfx", out string createVfx))
			{
				string[] parts = createVfx.Split(';');
				if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame)
				{
					LELEngine.VFX.VfxGraph graph = LELEngine.VFX.VfxGraphBuilder.CreateDefault(System.IO.Path.GetFileNameWithoutExtension(parts[1]));
					graph.Save(parts[1]);
					AssetDatabase.Refresh(true);
					editor.NotifyAssetsChanged();
					editor.OpenVfxGraph(parts[1]);
					editor.CreateObject("Visual Effect", go => go.AddComponent<LELEngine.VFX.VisualEffect>().visualEffectAsset = LELEngine.VFX.VfxGraph.Load(parts[1]));
					Debug.Log("[Test] Created " + parts[1]);
				}
			}

			// vfxgraph=N;asset opens the VFX Graph window on the asset at frame N (vfxgraph=N alone opens it empty).
			if (values.TryGetValue("vfxgraph", out string vfxGraph))
			{
				string[] parts = vfxGraph.Split(';');
				if (parts.Length >= 1 && int.TryParse(parts[0], out int at) && at == frame)
				{
					editor.OpenVfxGraph(parts.Length > 1 ? parts[1] : null);
				}
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

			// projectsettings=N;Tab: the Project Settings window on that tab (Lighting, Physics2D, Build) at frame N.
			if (values.TryGetValue("projectsettings", out string settingsTab))
			{
				string[] parts = settingsTab.Split(';');
				if (int.TryParse(parts[0], out int at) && at == frame)
				{
					editor.ShowProjectSettings(parts.Length > 1 && Enum.TryParse(parts[1], true, out ProjectSettingsWindow.Tab tab) ? tab : null);
				}
			}

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
					var settings = new BuildSettings { OutputFolder = parts[1], Release = true, SelfContained = editor.Project.BuildSelfContained, StartScene = editor.Project.StartScene, Scenes = new List<string>(editor.Project.BuildScenes) };
					bool ok = GameBuilder.Build(editor.Project, settings, line => Console.WriteLine("[Build] " + line));
					Debug.Log("[Test] Game build " + (ok ? "succeeded" : "failed"));
				}
			}

			// menuitem=N;Menu/Item|M;Other/Item runs [MenuItem]s of the project's editor scripts at frames N, M.
			if (values.TryGetValue("menuitem", out string menuItems))
			{
				foreach (string menuItem in menuItems.Split('|'))
				{
					string[] parts = menuItem.Split(';');
					if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame)
					{
						bool found = editor.Extensions.ExecuteMenuItem(parts[1]);
						Debug.Log("[Test] Menu item " + parts[1] + (found ? " run" : " not found"));
					}
				}
			}

			// addmodel=N;Models/Car.fbx|M;Other.obj instantiates model files at frames N, M (importing them first).
			if (values.TryGetValue("addmodel", out string addModels))
			{
				foreach (string addModel in addModels.Split('|'))
				{
					string[] parts = addModel.Split(';');
					if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame)
					{
						GameObject root = editor.InstantiateModel(parts[1]);
						Debug.Log("[Test] Model " + parts[1] + (root != null ? " added as " + root.Name : " not added"));
					}
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
