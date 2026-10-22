using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;

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
	///     Scripted mouse input (real ImGui drag and drop, aimed at <see cref="TestAnchors" />):
	///     drag=N;fromAnchor;toAnchor|M;... (press at N, move, hover at N+10, release at N+12),
	///     click=N;anchor|..., doubleclick=N;anchor|...; expandinspector=1 opens every inspector foldout;
	///     wheel=N;anchor;notches|... (mouse wheel over an anchor), mdrag=N;anchor;dx,dy|... (middle button drag by
	///     pixels; anchor "sceneview" is the scene view's image), logsceneview=N|M logs the scene view camera;
	///     logfield=N;Object;Component.field|... logs a field's value; pingasset=N;path shows a file in the project panel.
	///     key=N;F2|M;Enter (ImGuiKey presses), type=N;text (characters), logselection=N|M (what the inspector shows).
	///     Project panel anchors: project:Folder/File.ext, project:Folder/ (a folder row), tree:Folder, rename (the field).
	///     Prefabs: selectframe=, instantiateprefab=, openprefab=, closeprefab=, saveprefab=, createprefab=, createvariant=,
	///     unpackprefab=, applyprefab=, revertprefab=, setfield=, addcomponentat=, removecomponent=, destroyobject=,
	///     reparent=, undoat=, replacetext=, logprefab= (see <see cref="UpdatePrefabHooks" />).
	///     Console and preferences: log=N;Type;text, consoleselect=N;text, preferences=N;Section (preferencesfile=path
	///     gives an automated run preferences of its own); anchors console:row, consoleframe:index, preferences:Section.
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
		private readonly List<(int start, string from, string to)> drags = new List<(int, string, string)>();
		private readonly List<(int at, string anchor, bool twice, int button)> clicks = new List<(int, string, bool, int)>();
		private readonly List<(int at, string anchor, float amount)> wheels = new List<(int, string, float)>();
		private readonly List<(int start, string anchor, System.Numerics.Vector2 delta)> middleDrags = new List<(int, string, System.Numerics.Vector2)>();

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

			if (values.TryGetValue("drag", out string dragList))
			{
				foreach (string entry in dragList.Split('|'))
				{
					string[] parts = entry.Split(';');
					if (parts.Length == 3 && int.TryParse(parts[0], out int at))
					{
						drags.Add((at, parts[1], parts[2]));
					}
				}
			}

			foreach (string name in new[] { "click", "doubleclick", "rclick", "ctrlclick" })
			{
				if (values.TryGetValue(name, out string clickList))
				{
					foreach (string entry in clickList.Split('|'))
					{
						string[] parts = entry.Split(';');
						if (parts.Length == 2 && int.TryParse(parts[0], out int at))
						{
							// Button 0 left, 1 right; -1: left with Ctrl held.
							clicks.Add((at, parts[1], name == "doubleclick", name == "rclick" ? 1 : name == "ctrlclick" ? -1 : 0));
						}
					}
				}
			}

			// wheel=N;anchor;notches|...: the mouse over the anchor at N, the wheel turned at N + 1 (positive: forward).
			if (values.TryGetValue("wheel", out string wheelList))
			{
				foreach (string entry in wheelList.Split('|'))
				{
					string[] parts = entry.Split(';');
					if (parts.Length == 3 && int.TryParse(parts[0], out int at) && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float amount))
					{
						wheels.Add((at, parts[1], amount));
					}
				}
			}

			// mdrag=N;anchor;dx,dy|...: a middle button drag from the anchor by (dx, dy) pixels (press at N + 1, release at N + 11).
			if (values.TryGetValue("mdrag", out string middleList))
			{
				foreach (string entry in middleList.Split('|'))
				{
					string[] parts = entry.Split(';');
					string[] delta = parts.Length == 3 ? parts[2].Split(',') : Array.Empty<string>();
					if (parts.Length == 3 && delta.Length == 2 && int.TryParse(parts[0], out int at)
						&& float.TryParse(delta[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dx)
						&& float.TryParse(delta[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float dy))
					{
						middleDrags.Add((at, parts[1], new System.Numerics.Vector2(dx, dy)));
					}
				}
			}

			TestAnchors.Enabled = drags.Count > 0 || clicks.Count > 0 || wheels.Count > 0 || middleDrags.Count > 0;
			FieldDrawer.ForceOpen = values.TryGetValue("expandinspector", out string expand) && expand == "1";
		}

		#endregion

		#region PublicMethods

		/// <summary>Window the next UI frame brings to the front (focuswindow=).</summary>
		public string PendingFocus { get; set; }

		/// <summary>A window the next UI frame docks where another one is (dockwindow=).</summary>
		public (string window, string target)? PendingDock { get; set; }

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

			// audiostudio=N;asset opens the Audio Studio window at frame N (on the .lelaudio asset when given).
			if (values.TryGetValue("audiostudio", out string audioStudio))
			{
				string[] parts = audioStudio.Split(';');
				if (parts.Length >= 1 && int.TryParse(parts[0], out int at) && at == frame)
				{
					editor.OpenAudioStudio(parts.Length > 1 ? parts[1] : null, null);
				}
			}

			// audiostudioscreenshot=f1,f2,...;folder dumps the Audio Studio window at each listed frame into folder/fNNNN.
			if (values.TryGetValue("audiostudioscreenshot", out string audioShot))
			{
				string[] parts = audioShot.Split(';');
				if (parts.Length == 2)
				{
					foreach (string item in parts[0].Split(','))
					{
						if (int.TryParse(item, out int at) && at == frame)
						{
							Audio.AudioStudioWindow.ScreenshotFolder = System.IO.Path.Combine(parts[1], "f" + at.ToString("0000"));
						}
					}
				}
			}

			// audiocmd=N:cmd|cmd;M:cmd runs Audio Studio commands (see AudioStudio.RunTestCommand) at the given frames.
			if (values.TryGetValue("audiocmd", out string audioCommands))
			{
				foreach (string entry in audioCommands.Split(';'))
				{
					int colon = entry.IndexOf(':');
					if (colon <= 0 || !int.TryParse(entry.Substring(0, colon), out int at) || at != frame)
					{
						continue;
					}

					Audio.AudioStudioWindow window = editor.AudioStudioWindow;
					if (window == null)
					{
						Debug.LogWarning("[Test] The Audio Studio is not open at frame " + frame);
						continue;
					}

					foreach (string command in entry.Substring(colon + 1).Split('|'))
					{
						window.Studio.RunTestCommand(command);
					}
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

			UpdatePrefabHooks(scene);

			// pingasset=N;path|M;path: the project panel shows the file (its folder opens: its row can be dragged then).
			if (values.TryGetValue("pingasset", out string pingAssets))
			{
				foreach (string entry in pingAssets.Split('|'))
				{
					string[] parts = entry.Split(';');
					if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame)
					{
						editor.Ping(parts[1]);
						Debug.Log("[Test] Pinged " + parts[1]);
					}
				}
			}

			// inspectorscroll=N;pixels|M;pixels: scrolls the inspector (screenshots of long inspectors).
			if (values.TryGetValue("inspectorscroll", out string scrolls))
			{
				foreach (string entry in scrolls.Split('|'))
				{
					string[] parts = entry.Split(';');
					if (parts.Length == 2 && int.TryParse(parts[0], out int at) && at == frame && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pixels))
					{
						InspectorPanel.TestScroll = pixels;
					}
				}
			}

			// lockinspector=N: the inspector locks on what it shows.
			if (Is("lockinspector"))
			{
				InspectorPanel.TestLock = true;
			}

			// undo=N|M / redo=N: the editor's Undo / Redo at those frames.
			if (IsListed("undo"))
			{
				editor.UndoLast();
				Debug.Log("[Test] Undo");
			}

			if (IsListed("redo"))
			{
				editor.RedoLast();
				Debug.Log("[Test] Redo");
			}

			// logsceneview=N|M: the scene view camera's position, view direction and focus distance.
			if (IsListed("logsceneview"))
			{
				Debug.Log($"[Test] Scene view at {editor.SceneView.CameraPosition}, forward {editor.SceneView.CameraForward}, focus {editor.SceneView.FocusDistance:0.###}");
			}

			// logselection=N|M: what the inspector shows (the selected GameObject, the selected asset).
			if (IsListed("logselection"))
			{
				Debug.Log($"[Test] Selection: object {editor.Selected?.Name ?? "none"}, asset {editor.SelectedAsset ?? "none"}");
			}

			RunScriptedMouse();
			RunScriptedKeys();

			// logfield=N;Object;Component.field|...: the field's value as the inspector names it.
			if (values.TryGetValue("logfield", out string logFields))
			{
				foreach (string entry in logFields.Split('|'))
				{
					string[] parts = entry.Split(';');
					if (parts.Length == 3 && int.TryParse(parts[0], out int at) && at == frame)
					{
						Debug.Log("[Test] " + parts[1] + "/" + parts[2] + " = " + DescribeField(scene, parts[1], parts[2]));
					}
				}
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     key=N;F2|M;Enter: a key press for the focused window (ImGuiKey names; down at N, up at N + 1);
		///     type=N;text|M;...: characters typed at N (into the active text field).
		/// </summary>
		private void RunScriptedKeys()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			if (values.TryGetValue("key", out string keys))
			{
				foreach (string entry in keys.Split('|'))
				{
					string[] parts = entry.Split(';');
					if (parts.Length == 2 && int.TryParse(parts[0], out int at) && Enum.TryParse(parts[1], true, out ImGuiKey key))
					{
						if (frame == at)
						{
							io.AddKeyEvent(key, true);
						}
						else if (frame == at + 1)
						{
							io.AddKeyEvent(key, false);
						}
					}
				}
			}

			if (values.TryGetValue("type", out string typed))
			{
				foreach (string entry in typed.Split('|'))
				{
					string[] parts = entry.Split(';', 2);
					if (parts.Length == 2 && int.TryParse(parts[0], out int at) && frame == at)
					{
						foreach (char c in parts[1])
						{
							io.AddInputCharacter(c);
						}
					}
				}
			}
		}

		/// <summary>Feeds the scripted drags and clicks to ImGui as mouse events (aimed at the anchors recorded last frame).</summary>
		private void RunScriptedMouse()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			foreach ((int start, string from, string to) in drags)
			{
				int step = frame - start;
				if (step < 0 || step > 13)
				{
					continue;
				}

				if (!TestAnchors.TryGetPoint(from, out System.Numerics.Vector2 a) || !TestAnchors.TryGetPoint(to, out System.Numerics.Vector2 b))
				{
					if (step == 0)
					{
						Debug.LogWarning("[Test] Drag " + from + " -> " + to + ": an anchor was not drawn");
					}

					continue;
				}

				if (step == 0)
				{
					io.AddMousePosEvent(a.X, a.Y);
				}
				else if (step == 1)
				{
					io.AddMouseButtonEvent(0, true);
				}
				else if (step <= 9)
				{
					// Past the drag threshold first, then on to the target.
					float t = (step - 1) / 8f;
					System.Numerics.Vector2 p = step == 2 ? a + new System.Numerics.Vector2(10f, 10f) : System.Numerics.Vector2.Lerp(a, b, t);
					io.AddMousePosEvent(p.X, p.Y);
				}
				else if (step <= 11)
				{
					io.AddMousePosEvent(b.X, b.Y);
				}
				else if (step == 12)
				{
					io.AddMouseButtonEvent(0, false);
					Debug.Log("[Test] Dropped " + from + " on " + to);
				}
			}

			foreach ((int at, string anchor, bool twice, int button) in clicks)
			{
				int step = frame - at;
				if (step < 0 || step > 5 || !TestAnchors.TryGetPoint(anchor, out System.Numerics.Vector2 point))
				{
					continue;
				}

				int mouseButton = Math.Max(0, button);
				if (step == 0) io.AddMousePosEvent(point.X, point.Y);
				else if (step == 1 || (twice && step == 3))
				{
					if (button < 0) io.AddKeyEvent(ImGuiKey.ModCtrl, true);
					io.AddMouseButtonEvent(mouseButton, true);
				}
				else if (step == 2 || (twice && step == 4)) io.AddMouseButtonEvent(mouseButton, false);
				else if (step == 3 && button < 0) io.AddKeyEvent(ImGuiKey.ModCtrl, false);
			}

			foreach ((int at, string anchor, float amount) in wheels)
			{
				int step = frame - at;
				if (step < 0 || step > 1 || !TestAnchors.TryGetPoint(anchor, out System.Numerics.Vector2 point))
				{
					continue;
				}

				if (step == 0) io.AddMousePosEvent(point.X, point.Y);
				else io.AddMouseWheelEvent(0f, amount);
			}

			foreach ((int start, string anchor, System.Numerics.Vector2 delta) in middleDrags)
			{
				int step = frame - start;
				if (step < 0 || step > 12 || !TestAnchors.TryGetPoint(anchor, out System.Numerics.Vector2 a))
				{
					continue;
				}

				// Middle button: 2. Moves in eight even steps, so every frame carries part of the delta.
				if (step == 0) io.AddMousePosEvent(a.X, a.Y);
				else if (step == 1) io.AddMouseButtonEvent(2, true);
				else if (step <= 10)
				{
					System.Numerics.Vector2 p = a + delta * ((step - 2) / 8f);
					io.AddMousePosEvent(p.X, p.Y);
				}
				else if (step == 11)
				{
					io.AddMouseButtonEvent(2, false);
					Debug.Log($"[Test] Middle-dragged {anchor} by {delta}");
				}
			}
		}

		private static string DescribeField(Scene scene, string objectName, string fieldPath)
		{
			GameObject go = Find(scene, objectName);
			int dot = fieldPath.IndexOf('.');
			if (go == null || dot <= 0)
			{
				return "(object not found)";
			}

			foreach (Behaviour component in go.Components)
			{
				if (component.GetType().Name != fieldPath.Substring(0, dot))
				{
					continue;
				}

				foreach (Serialization.SerializedField field in Serialization.SerializationUtility.GetFields(component.GetType()))
				{
					if (field.Name == fieldPath.Substring(dot + 1))
					{
						return Describe(field.GetValue(component));
					}
				}
			}

			return "(field not found)";
		}

		private static string Describe(object value)
		{
			switch (value)
			{
				case null:
					return "null";
				case System.Collections.IDictionary dictionary:
				{
					var parts = new List<string>();
					foreach (System.Collections.DictionaryEntry entry in dictionary)
					{
						parts.Add(Describe(entry.Key) + ": " + Describe(entry.Value));
					}

					return "{" + string.Join(", ", parts) + "}";
				}
				case System.Collections.IList list when !(value is string):
				{
					var parts = new List<string>();
					foreach (object item in list)
					{
						parts.Add(Describe(item));
					}

					return "[" + string.Join(", ", parts) + "]";
				}
				case EngineObject _:
				case IAsset _:
					return ObjectField.NameOf(value);
				default:
					return value.GetType().IsPrimitive || value is string ? value.ToString() : value.GetType().Name + "(" + value + ")";
			}
		}

		private bool Is(string name)
		{
			return values.TryGetValue(name, out string value) && int.TryParse(value, out int at) && at == frame;
		}

		/// <summary>name=N|M|...: true at each listed frame.</summary>
		private bool IsListed(string name)
		{
			if (!values.TryGetValue(name, out string value))
			{
				return false;
			}

			foreach (string item in value.Split('|'))
			{
				if (int.TryParse(item, out int at) && at == frame)
				{
					return true;
				}
			}

			return false;
		}

		private static GameObject Find(Scene scene, string name)
		{
			foreach (GameObject go in scene.GameObjects)
			{
				if (go.Name == name && !go.IsDestroyed)
				{
					return go;
				}
			}

			return null;
		}

		/// <summary>Runs <paramref name="action" /> with the ';'-separated arguments of each '|'-separated entry of name=N;args|M;args listed for this frame.</summary>
		private void At(string name, Action<string[]> action)
		{
			if (!values.TryGetValue(name, out string list))
			{
				return;
			}

			foreach (string entry in list.Split('|'))
			{
				string[] parts = entry.Split(';');
				if (parts.Length >= 1 && int.TryParse(parts[0], out int at) && at == frame)
				{
					try
					{
						action(parts);
					}
					catch (Exception e)
					{
						Debug.LogError("[Test] " + name + " at frame " + frame + " failed: " + e.Message);
					}
				}
			}
		}

		/// <summary>
		///     Prefab hooks (entries N;args, several with '|'): selectframe=N;Object, instantiateprefab=N;path,
		///     openprefab=N;path, closeprefab=N, saveprefab=N, createprefab=N;Object;folder[;original|variant],
		///     createvariant=N;path, unpackprefab=N;Object[;completely], applyprefab=N;Object, revertprefab=N;Object,
		///     setfield=N;Object;Component.field[.sub];value (value: number, true/false, enum name, x,y,z, new:Class, null),
		///     addcomponentat=N;Object;Type, removecomponent=N;Object;Type, destroyobject=N;Object, reparent=N;Object;Parent,
		///     undoat=N, logprefab=N;Object (logs the instance's overrides).
		/// </summary>
		private void UpdatePrefabHooks(Scene scene)
		{
			At("selectframe", parts => editor.Select(Find(scene, parts[1])));
			At("instantiateprefab", parts => Debug.Log("[Test] Instantiated " + editor.InstantiatePrefab(parts[1])?.Name));
			At("openprefab", parts => editor.OpenPrefab(parts[1]));
			At("closeprefab", parts => editor.ClosePrefabStage());
			At("saveprefab", parts => editor.SavePrefabStage());
			At("createprefab", parts => editor.CreatePrefab(Find(scene, parts[1]), parts.Length > 2 ? parts[2] : "", parts.Length > 3 ? parts[3] == "variant" : (bool?)null));
			At("createvariant", parts => Debug.Log("[Test] Variant " + editor.CreatePrefabVariant(parts[1])));
			At("unpackprefab", parts => editor.UnpackPrefab(Find(scene, parts[1]), parts.Length > 2 && parts[2] == "completely" ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot));
			At("applyprefab", parts => PrefabMenus.ApplyAll(editor, Find(scene, parts[1]).prefabInstance));
			At("revertprefab", parts => PrefabMenus.RevertAll(editor, Find(scene, parts[1]).prefabInstance));
			At("undoat", parts => editor.UndoLast());
			// replacetext=N;assetPath;old;new: an edit of a file outside the editor (the file watcher notices it).
			// inputmanager=N;Page: Window > Input Manager on the project's configuration, showing a page (Actions, KeyboardMaps, ...).
			At("inputmanager", parts => editor.OpenInputManager(null, parts.Length > 1 ? parts[1] : null));
			// preferences=N;Section: Edit > Preferences on a section (Theme, Console, ExternalTools).
			At("preferences", parts => editor.ShowPreferences(parts.Length > 1 ? Enum.Parse<PreferencesWindow.Section>(parts[1], true) : null));
			// log=N;Type;text: a message logged from a few nested calls (console call stacks), Type Log / Warning / Error / Exception.
			At("log", parts => TestLog.Write(Enum.Parse<LogType>(parts[1], true), parts.Length > 2 ? parts[2] : "Test message"));
			// dockwindow=N;Window;Target: docks the window into the dock node of the target window (the next UI frame).
			At("dockwindow", parts => PendingDock = (parts[1], parts[2]));
			// consoleselect=N;text: selects the first console message containing the text.
			At("consoleselect", parts => Debug.Log("[Test] Console selection " + (editor.SelectConsoleMessage(parts[1]) ? "found" : "not found") + ": " + parts[1]));

			At("replacetext", parts =>
			{
				string file = AssetDatabase.ToAbsolute(parts[1]);
				string text = System.IO.File.ReadAllText(file);
				System.IO.File.WriteAllText(file, text.Replace(parts[2], parts[3]));
				Debug.Log($"[Test] Replaced '{parts[2]}' with '{parts[3]}' in {parts[1]} ({(text.Contains(parts[2]) ? "found" : "not found")})");
			});
			At("destroyobject", parts =>
			{
				GameObject go = Find(scene, parts[1]);
				editor.RecordStructural("Delete " + go.Name, () => GameObject.DestroyImmediate(go));
			});
			At("addcomponentat", parts =>
			{
				GameObject go = Find(scene, parts[1]);
				Type type = TypeRegistry.Resolve(parts[2]);
				editor.RecordStructural("Add " + type.Name, () => go.AddComponent(type));
			});
			At("removecomponent", parts =>
			{
				GameObject go = Find(scene, parts[1]);
				Behaviour component = go.GetComponent(TypeRegistry.Resolve(parts[2]));
				editor.RecordStructural("Remove " + parts[2], () => GameObject.DestroyImmediate(component));
			});
			At("reparent", parts =>
			{
				GameObject go = Find(scene, parts[1]);
				GameObject parent = Find(scene, parts[2]);
				editor.RecordStructural("Re-parent " + go.Name, () => go.transform.SetParent(parent?.transform, true));
			});
			At("setfield", parts =>
			{
				GameObject go = Find(scene, parts[1]);
				string[] path = parts[2].Split('.');
				Behaviour component = go.GetComponent(TypeRegistry.Resolve(path[0])) ?? FindComponentByName(go, path[0]);
				editor.Edits.Begin(component);
				SetPath(component, path, 1, parts[3]);
				editor.Edits.RequestCommit();
				editor.NotifyChanged(go);
				Debug.Log($"[Test] {go.Name}.{parts[2]} = {parts[3]}");
			});
			At("logprefab", parts =>
			{
				PrefabInstance instance = Find(scene, parts[1])?.prefabInstance;
				PrefabInstanceOverrides overrides = PrefabUtility.GetOverrides(instance);
				Debug.Log($"[Test] {parts[1]}: {overrides.Modifications.Count} overrides ({string.Join("; ", overrides.Modifications.ConvertAll(m => m.ToString()))}), added {overrides.AddedComponents.Count} components / {overrides.AddedObjects.Count} objects, removed {overrides.RemovedComponents.Count} components / {overrides.RemovedObjects.Count} objects");
			});
		}

		private static Behaviour FindComponentByName(GameObject go, string name)
		{
			foreach (Behaviour component in go.Components)
			{
				if (component.GetType().Name == name)
				{
					return component;
				}
			}

			return null;
		}

		/// <summary>Sets a field (through nested fields) from text.</summary>
		private static void SetPath(object target, string[] path, int index, string text)
		{
			System.Reflection.FieldInfo field = target.GetType().GetField(path[index], System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
			if (field == null)
			{
				throw new InvalidOperationException("no field " + path[index] + " in " + target.GetType().Name);
			}

			if (index < path.Length - 1)
			{
				object inner = field.GetValue(target);
				SetPath(inner, path, index + 1, text);
				if (field.FieldType.IsValueType)
				{
					field.SetValue(target, inner);
				}

				return;
			}

			field.SetValue(target, ParseValue(field.FieldType, text));
		}

		private static object ParseValue(Type type, string text)
		{
			var culture = System.Globalization.CultureInfo.InvariantCulture;
			if (text == "null")
			{
				return null;
			}

			if (text.StartsWith("new:", StringComparison.Ordinal))
			{
				string name = text.Substring(4);
				foreach (Type candidate in Serialization.SerializationUtility.GetConcreteTypes(type))
				{
					if (candidate.Name == name || candidate.FullName == name)
					{
						return Activator.CreateInstance(candidate);
					}
				}

				throw new InvalidOperationException("no class " + name + " for " + type.Name);
			}

			if (type.IsEnum)
			{
				return Enum.Parse(type, text, true);
			}

			if (type == typeof(Prefab))
			{
				return Prefab.Load(text);
			}

			if (type == typeof(OpenTK.Mathematics.Vector3))
			{
				string[] c = text.Split(',');
				return new OpenTK.Mathematics.Vector3(float.Parse(c[0], culture), float.Parse(c[1], culture), float.Parse(c[2], culture));
			}

			return Convert.ChangeType(text, type, culture);
		}

		#endregion
	}

	/// <summary>Messages logged a few calls deep (log=): the console shows a call stack with source.</summary>
	internal static class TestLog
	{
		public static void Write(LogType type, string text)
		{
			Outer(type, text);
		}

		private static void Outer(LogType type, string text)
		{
			Inner(type, text);
		}

		private static void Inner(LogType type, string text)
		{
			switch (type)
			{
				case LogType.Warning:
					Debug.LogWarning(text);
					break;
				case LogType.Error:
					Debug.LogError(text);
					break;
				case LogType.Exception:
					try
					{
						throw new InvalidOperationException(text, new ArgumentException("The inner cause"));
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}

					break;
				default:
					Debug.Log(text);
					break;
			}
		}
	}
}
