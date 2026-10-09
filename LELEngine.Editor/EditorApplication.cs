using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using LELEngine.Serialization;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	internal enum PlayState
	{
		Edit,
		Playing,
		Paused
	}

	/// <summary>
	///     The editor: the open project and scene, selection, undo, play mode, the main menu and the docked panels.
	///     Everything runs on the main (GL) thread; script builds run in the background (see
	///     <see cref="ScriptDomain" />).
	/// </summary>
	internal sealed class EditorApplication : IDisposable
	{
		#region PublicFields

		public static EditorApplication Instance { get; private set; }

		public EditorWindow Window { get; }
		public ProjectInfo Project { get; private set; }
		public Scene Scene => SceneManager.ActiveScene;
		public GameObject Selected { get; private set; }
		public bool SceneDirty { get; private set; }
		public PlayState PlayState { get; private set; } = PlayState.Edit;
		public bool IsPlaying => PlayState != PlayState.Edit;
		public UndoStack Undo { get; } = new UndoStack();
		public EditTracker Edits { get; }
		public ConsoleLog Log { get; }
		public ScriptDomain Scripts { get; private set; }

		public SceneViewPanel SceneView { get; }
		public GameViewPanel GameView { get; }

		/// <summary>Bumped when asset files were added, removed or renamed (panels re-list).</summary>
		public int AssetsVersion { get; private set; }

		/// <summary>
		///     Bumped on every change of what the views show (edits, undo, scene loads, reloads, assets): views render
		///     on demand (<see cref="ViewRefresh" />).
		/// </summary>
		public int SceneVersion { get; private set; }

		public static string TemplateAssetsPath => Path.Combine(AppContext.BaseDirectory, "Templates", "NewProject", "Assets");

		#endregion

		#region PrivateFields

		private readonly ProjectLauncher launcher;
		private readonly HierarchyPanel hierarchy;
		private readonly InspectorPanel inspector;
		private readonly ProjectPanel projectPanel;
		private readonly ConsolePanel consolePanel;
		private readonly LightingPanel lightingPanel;
		private readonly BuildPanel buildPanel;
		private readonly GameInputHost gameInput;
		private readonly TestHooks testHooks;
		private readonly bool layoutFileExisted;
		private bool layoutBuilt;
		private string lastTitle;

		// Play mode: the edit scene as it was when play started.
		private string playSnapshot;
		private string playScenePath;
		private bool playSceneDirty;

		// Modal dialogs.
		private Action pendingAfterSave;
		private bool openUnsavedPopup;
		private bool openSaveAsPopup;
		private string saveAsPath = "Scenes/NewScene.scene";
		private bool closeConfirmed;

		private FileSystemWatcher assetWatcher;
		private volatile bool assetsChanged;
		private double assetsChangedAt;

		#endregion

		#region Constructors

		public EditorApplication(EditorWindow window, string layoutFile, string[] args)
		{
			Instance = this;
			Window = window;
			Edits = new EditTracker(this);
			Log = ConsoleLog.Install();
			layoutFileExisted = File.Exists(layoutFile);

			launcher = new ProjectLauncher(this);
			hierarchy = new HierarchyPanel(this);
			inspector = new InspectorPanel(this);
			SceneView = new SceneViewPanel(this);
			GameView = new GameViewPanel(this);
			projectPanel = new ProjectPanel(this);
			consolePanel = new ConsolePanel(this);
			lightingPanel = new LightingPanel(this);
			buildPanel = new BuildPanel(this);

			gameInput = new GameInputHost(window, () => PlayState == PlayState.Playing && GameView.HasInputFocus);
			Input.Host = gameInput;
			window.KeyDown += OnWindowKeyDown;
			window.KeyUp += OnWindowKeyUp;
			window.MouseMove += OnWindowMouseMove;

			// project=folder opens a project directly; newproject=parent;name creates it first when missing.
			string newProject = GetValue(args, "newproject");
			if (newProject != null)
			{
				string[] parts = newProject.Split(';');
				string root = Path.Combine(parts[0], parts.Length > 1 ? parts[1] : "NewProject");
				if (!ProjectInfo.IsProjectFolder(root))
				{
					Directory.CreateDirectory(parts[0]);
					ProjectInfo.Create(parts[0], Path.GetFileName(root), TemplateAssetsPath);
				}

				OpenProject(root);
			}

			string projectArgument = GetValue(args, "project") ?? CommandLine.Parse(args).Get("projectpath");
			if (Project == null && projectArgument != null && ProjectInfo.IsProjectFolder(projectArgument))
			{
				OpenProject(projectArgument);
			}

			testHooks = new TestHooks(this, args);
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- frame

		public void Update(float deltaTime)
		{
			if (Project == null)
			{
				return;
			}

			RefreshAssetsIfChanged();
			Scripts?.Update();
			testHooks.Update();

			Scene scene = Scene;
			if (scene == null || PlayState == PlayState.Paused)
			{
				return;
			}

			if (PlayState == PlayState.Playing)
			{
				Input.BeginFrame();
				scene.Update(deltaTime);
				Input.EndFrame();
			}
			else
			{
				// Edit mode: only [ExecuteAlways] components run.
				scene.Update(deltaTime);
			}
		}

		public void DrawUI()
		{
			UpdateTitle();
			if (Project == null)
			{
				launcher.Draw();
				return;
			}

			HandleShortcuts();
			DrawMainMenu();

			uint dockspace = ImGui.DockSpaceOverViewport();
			if (!layoutBuilt)
			{
				layoutBuilt = true;
				if (!layoutFileExisted)
				{
					BuildDefaultLayout(dockspace);
				}
			}

			hierarchy.Draw();
			SceneView.Draw();
			GameView.Draw();
			inspector.Draw();
			lightingPanel.Draw();
			projectPanel.Draw();
			consolePanel.Draw();
			buildPanel.Draw();
			Edits.Flush();

			if (testHooks.PendingFocus != null)
			{
				ImGui.SetWindowFocus(testHooks.PendingFocus);
				testHooks.PendingFocus = null;
			}

			DrawModals();
		}

		/// <summary>
		///     Renders the visible views that need it (after the UI was built, before it is drawn): every frame while
		///     the game plays, with "Always Refresh" or while game scripts run in edit mode; otherwise on changes.
		/// </summary>
		public void RenderViews()
		{
			if (Project == null)
			{
				return;
			}

			bool continuous = PlayState == PlayState.Playing || SceneView.AlwaysRefresh || EditModeScriptsRunning();
			SceneView.Render(continuous);
			GameView.Render(continuous);
		}

		/// <summary>A view must render on its next frame(s) whatever changed.</summary>
		public void NotifySceneChanged()
		{
			SceneVersion++;
		}

		/// <summary>The next close does not ask about unsaved changes (automated runs).</summary>
		public void DiscardChangesOnClose()
		{
			closeConfirmed = true;
		}

		/// <summary>Window close request: false while unsaved changes wait for an answer.</summary>
		public bool RequestClose()
		{
			if (closeConfirmed || Project == null || !SceneDirty || IsPlaying)
			{
				SaveEditorState();
				return true;
			}

			ConfirmDiscard(() =>
			{
				closeConfirmed = true;
				Window.Close();
			});
			return false;
		}

		// ---------------------------------------------------------------- projects

		public void OpenProject(string root)
		{
			ProjectInfo project = ProjectInfo.Load(root);
			CloseProject();

			Project = project;
			AssetDatabase.Initialize(project.AssetsPath, true);
			RecentProjects.Add(project.RootPath);
			Debug.Log($"[Editor] Opened project {project.Name} ({project.RootPath})");

			GameView.EnsureRenderer();
			SceneView.EnsureRenderer();
			Game.Renderer = GameView.ViewRenderer.Renderer;
			SceneView.LoadState(project);

			assetWatcher = new FileSystemWatcher(project.AssetsPath) { IncludeSubdirectories = true };
			assetWatcher.Created += (s, e) => OnAssetsChanged();
			assetWatcher.Deleted += (s, e) => OnAssetsChanged();
			assetWatcher.Renamed += (s, e) => OnAssetsChanged();
			assetWatcher.EnableRaisingEvents = true;

			Scripts = new ScriptDomain(this, project);
			Scripts.Initialize();

			string scenePath = project.LastScene ?? project.StartScene;
			if (scenePath != null && File.Exists(AssetDatabase.ToAbsolute(scenePath)))
			{
				OpenSceneNow(scenePath);
			}
			else
			{
				NewSceneNow();
				if (AssetDatabase.GetAllAssetPaths(SceneSerializer.Extension).Count == 0)
				{
					SaveSceneTo(project.StartScene);
				}
			}
		}

		public void CloseProject()
		{
			if (Project == null)
			{
				return;
			}

			SaveEditorState();
			if (IsPlaying)
			{
				ExitPlayMode();
			}

			Scene?.Unload();
			SceneManager.SetActiveScene(null);
			Scripts?.Dispose();
			Scripts = null;
			assetWatcher?.Dispose();
			assetWatcher = null;
			Selected = null;
			Undo.Clear();
			Edits.Cancel();
			InternalStorage.Clear();
			Project = null;
		}

		// ---------------------------------------------------------------- scenes

		public void NewScene()
		{
			ConfirmDiscard(NewSceneNow);
		}

		public void OpenScene(string assetPath)
		{
			ConfirmDiscard(() => OpenSceneNow(assetPath));
		}

		public void SaveScene()
		{
			if (IsPlaying)
			{
				Debug.LogWarning("[Editor] Exit play mode to save the scene.");
				return;
			}

			if (string.IsNullOrEmpty(Scene?.Path))
			{
				openSaveAsPopup = true;
				return;
			}

			SaveSceneTo(Scene.Path);
		}

		public void SaveSceneAs()
		{
			if (Scene != null)
			{
				saveAsPath = Scene.Path ?? "Scenes/" + Scene.Name + SceneSerializer.Extension;
				openSaveAsPopup = true;
			}
		}

		/// <summary>Replaces the active scene (unloading the old one) and starts its lifecycle.</summary>
		public void ReplaceScene(Scene scene, bool playing)
		{
			ulong selected = Selected?.Id ?? 0;
			Edits.Cancel();
			Scene?.Unload();
			SceneManager.SetActiveScene(scene);
			scene?.StartLifecycle(playing);
			SceneView.InvalidateCaches();
			GameView.InvalidateCaches();
			NotifySceneChanged();
			Selected = selected != 0 ? scene?.FindObjectById(selected) : null;
		}

		/// <summary>Reloads the scene from a snapshot (undo of structural changes), keeping its path.</summary>
		public void RestoreSnapshot(string json, ulong selection)
		{
			string path = Scene?.Path;
			Scene scene = SceneSerializer.LoadFromString(json);
			scene.Path = path;
			ReplaceScene(scene, IsPlaying);
			Selected = selection != 0 ? scene.FindObjectById(selection) : null;
			MarkDirty();
		}

		// ---------------------------------------------------------------- editing

		public void Select(GameObject go)
		{
			if (Selected != go)
			{
				Edits.Commit();
			}

			Selected = go;
		}

		public void MarkDirty()
		{
			NotifySceneChanged();
			if (!IsPlaying)
			{
				SceneDirty = true;
			}
		}

		/// <summary>An object was modified: the scene is dirty, and static GI caches rebuild if it is static.</summary>
		public void NotifyChanged(GameObject go)
		{
			MarkDirty();
			if (go != null && HasStaticRenderer(go))
			{
				Lighting.GI.InvalidateStatic();
			}
		}

		/// <summary>Runs a structural change (create, delete, re-parent, add / remove component) as one undo step.</summary>
		public void RecordStructural(string name, Action change)
		{
			if (Scene == null)
			{
				return;
			}

			Edits.Commit();
			string before = SceneSerializer.SaveToString(Scene);
			ulong selectionBefore = Selected?.Id ?? 0;
			change();
			string after = SceneSerializer.SaveToString(Scene);
			Undo.Push(new SceneSnapshotRecord { Name = name, Before = before, After = after, SelectionBefore = selectionBefore, SelectionAfter = Selected?.Id ?? 0 });
			MarkDirty();
			Lighting.GI.InvalidateStatic();
		}

		public void DeleteSelected()
		{
			GameObject target = Selected;
			if (target == null)
			{
				return;
			}

			RecordStructural("Delete " + target.Name, () =>
			{
				Selected = null;
				GameObject.DestroyImmediate(target);
			});
		}

		public void DuplicateSelected()
		{
			GameObject target = Selected;
			if (target == null || Scene == null)
			{
				return;
			}

			RecordStructural("Duplicate " + target.Name, () =>
			{
				string json = SceneSerializer.SerializeObjects(Scene, new[] { target });
				List<GameObject> copies = SceneSerializer.InstantiateObjects(json, Scene, target.transform.parent);
				if (copies.Count > 0)
				{
					Selected = copies[0];
				}
			});
		}

		public GameObject CreateObject(string name, Action<GameObject> setup = null, Transform parent = null)
		{
			GameObject created = null;
			RecordStructural("Create " + name, () =>
			{
				created = Scene.CreateGameObject(name);
				if (parent != null)
				{
					created.transform.SetParent(parent, false);
				}
				else
				{
					created.transform.position = SceneView.DefaultSpawnPoint();
				}

				setup?.Invoke(created);
				Selected = created;
			});
			return created;
		}

		public void UndoLast()
		{
			Edits.Commit();
			Undo.Undo(this);
		}

		public void RedoLast()
		{
			Edits.Commit();
			Undo.Redo(this);
		}

		// ---------------------------------------------------------------- play mode

		public void TogglePlay()
		{
			if (IsPlaying)
			{
				ExitPlayMode();
			}
			else
			{
				EnterPlayMode();
			}
		}

		public void TogglePause()
		{
			if (PlayState == PlayState.Playing)
			{
				PlayState = PlayState.Paused;
			}
			else if (PlayState == PlayState.Paused)
			{
				PlayState = PlayState.Playing;
			}
		}

		/// <summary>One frame of game logic while paused.</summary>
		public void Step()
		{
			if (PlayState == PlayState.Paused && Scene != null)
			{
				Input.BeginFrame();
				Scene.Update(1f / 60f);
				Input.EndFrame();
				NotifySceneChanged();
			}
		}

		public void EnterPlayMode()
		{
			if (IsPlaying || Scene == null)
			{
				return;
			}

			if (Scripts != null && Scripts.HasErrors)
			{
				Debug.LogError("[Editor] Fix the script compile errors before entering play mode.");
				return;
			}

			Edits.Commit();
			playSnapshot = SceneSerializer.SaveToString(Scene);
			playScenePath = Scene.Path;
			playSceneDirty = SceneDirty;

			// Full reload of the game scripts: the play session starts from clean statics.
			ulong selected = Selected?.Id ?? 0;
			Scripts?.TrackComponents(Scene);
			Selected = null;
			Scene.Unload();
			SceneManager.SetActiveScene(null);
			if (Project.ReloadScriptsOnPlay && Scripts != null)
			{
				Scripts.ReloadNow();
			}

			Scene play = SceneSerializer.LoadFromString(playSnapshot);
			play.Path = playScenePath;
			Time.ResetTime();
			PlayState = PlayState.Playing;
			ReplaceScene(play, true);
			Selected = selected != 0 ? play.FindObjectById(selected) : null;
			GameView.Focus();
			Undo.Clear();
		}

		public void ExitPlayMode()
		{
			if (!IsPlaying)
			{
				return;
			}

			Input.SetCursorLocked(false);
			PlayState = PlayState.Edit;
			Scene edit = SceneSerializer.LoadFromString(playSnapshot);
			edit.Path = playScenePath;
			ReplaceScene(edit, false);
			SceneDirty = playSceneDirty;
			playSnapshot = null;
			Undo.Clear();
		}

		/// <summary>
		///     Saves the current scene to memory, unloads it, runs <paramref name="reload" /> (the script domain swap)
		///     and loads the scene back: every component is re-created from its serialized state.
		/// </summary>
		public void ReloadSceneAround(Action reload)
		{
			Scene scene = Scene;
			if (scene == null)
			{
				reload();
				return;
			}

			Edits.Commit();
			string snapshot = SceneSerializer.SaveToString(scene);
			string path = scene.Path;
			ulong selected = Selected?.Id ?? 0;
			bool playing = IsPlaying;
			Scripts?.TrackComponents(scene);
			scene.Unload();
			SceneManager.SetActiveScene(null);
			Selected = null;

			reload();

			Scene reloaded = SceneSerializer.LoadFromString(snapshot);
			reloaded.Path = path;
			ReplaceScene(reloaded, playing);
			Selected = selected != 0 ? reloaded.FindObjectById(selected) : null;
		}

		public void ShowBuildWindow()
		{
			buildPanel.Open = true;
		}

		public void NotifyAssetsChanged()
		{
			AssetsVersion++;
			NotifySceneChanged();
		}

		/// <summary>Drops every editor-side cache that holds script types or scene objects (before a domain unload).</summary>
		public void ClearTypeCaches()
		{
			inspector.ClearTypeCaches();
			HierarchyPanel.ResetDrag();
			Edits.Cancel();
		}

		public void Dispose()
		{
			CloseProject();
			SceneView.Dispose();
			GameView.Dispose();
			UnsubscribeWindow();
		}

		#endregion

		#region PrivateMethods

		private void UnsubscribeWindow()
		{
			Window.KeyDown -= OnWindowKeyDown;
			Window.KeyUp -= OnWindowKeyUp;
			Window.MouseMove -= OnWindowMouseMove;
		}

		private void NewSceneNow()
		{
			if (IsPlaying)
			{
				ExitPlayMode();
			}

			Scene scene = DefaultScene.Create(Project);
			ReplaceScene(scene, false);
			Selected = null;
			SceneDirty = false;
			Undo.Clear();
		}

		private void OpenSceneNow(string assetPath)
		{
			if (IsPlaying)
			{
				ExitPlayMode();
			}

			try
			{
				Scene scene = SceneSerializer.Load(AssetDatabase.ToAbsolute(assetPath));
				scene.Path = assetPath;
				ReplaceScene(scene, false);
				Selected = null;
				SceneDirty = false;
				Undo.Clear();
				Project.LastScene = assetPath;
				Project.Save();
			}
			catch (Exception e)
			{
				Debug.LogError($"[Editor] Cannot open scene {assetPath}: {e.Message}");
			}
		}

		private void SaveSceneTo(string assetPath)
		{
			assetPath = assetPath.Replace('\\', '/').TrimStart('/');
			if (!assetPath.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase))
			{
				assetPath += SceneSerializer.Extension;
			}

			Scene.Path = assetPath;
			Scene.Name = Path.GetFileNameWithoutExtension(assetPath);
			SceneSerializer.Save(Scene, AssetDatabase.ToAbsolute(assetPath));
			AssetDatabase.EnsureMeta(assetPath);
			SceneDirty = false;
			Project.LastScene = assetPath;
			if (!Project.BuildScenes.Contains(assetPath))
			{
				Project.BuildScenes.Add(assetPath);
			}

			Project.Save();
			Debug.Log("[Editor] Saved " + assetPath);

			Action after = pendingAfterSave;
			pendingAfterSave = null;
			after?.Invoke();
		}

		/// <summary>Runs <paramref name="continuation" /> now, or after the user saved / discarded the unsaved scene.</summary>
		private void ConfirmDiscard(Action continuation)
		{
			if (!SceneDirty || IsPlaying)
			{
				continuation();
				return;
			}

			pendingAfterSave = continuation;
			openUnsavedPopup = true;
		}

		private void DrawModals()
		{
			if (openUnsavedPopup)
			{
				ImGui.OpenPopup("Unsaved changes");
				openUnsavedPopup = false;
			}

			if (openSaveAsPopup)
			{
				ImGui.OpenPopup("Save scene as");
				openSaveAsPopup = false;
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Unsaved changes", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text($"The scene '{Scene?.Name}' has unsaved changes.");
				ImGui.Spacing();
				if (ImGui.Button("Save", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					Action continuation = pendingAfterSave;
					if (string.IsNullOrEmpty(Scene?.Path))
					{
						openSaveAsPopup = true;
					}
					else
					{
						pendingAfterSave = null;
						SaveSceneTo(Scene.Path);
						continuation?.Invoke();
					}
				}

				ImGui.SameLine();
				if (ImGui.Button("Don't save", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					Action continuation = pendingAfterSave;
					pendingAfterSave = null;
					SceneDirty = false;
					continuation?.Invoke();
				}

				ImGui.SameLine();
				if (ImGui.Button("Cancel", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					pendingAfterSave = null;
				}

				ImGui.EndPopup();
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Save scene as", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text("Asset path (inside the project's Assets folder):");
				ImGui.SetNextItemWidth(420);
				ImGui.InputText("##path", ref saveAsPath, 260);
				if (ImGui.Button("Save", new NVector2(110, 0)) && !string.IsNullOrWhiteSpace(saveAsPath))
				{
					ImGui.CloseCurrentPopup();
					SaveSceneTo(saveAsPath);
				}

				ImGui.SameLine();
				if (ImGui.Button("Cancel", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					pendingAfterSave = null;
				}

				ImGui.EndPopup();
			}
		}

		private void DrawMainMenu()
		{
			if (!ImGui.BeginMainMenuBar())
			{
				return;
			}

			if (ImGui.BeginMenu("File"))
			{
				if (ImGui.MenuItem("New Scene", "Ctrl+N")) NewScene();
				if (ImGui.MenuItem("Save Scene", "Ctrl+S", false, !IsPlaying)) SaveScene();
				if (ImGui.MenuItem("Save Scene As...", "Ctrl+Shift+S", false, !IsPlaying)) SaveSceneAs();
				ImGui.Separator();
				if (ImGui.MenuItem("Open Script Project", "", false, Scripts != null)) Scripts.OpenInIde();
				if (ImGui.MenuItem("Rebuild Scripts", "", false, Scripts != null && !IsPlaying)) Scripts.RequestBuild();
				ImGui.Separator();
				if (ImGui.MenuItem("Build Settings...", "Ctrl+Shift+B")) ShowBuildWindow();
				ImGui.Separator();
				if (ImGui.MenuItem("Close Project")) ConfirmDiscard(CloseProject);
				if (ImGui.MenuItem("Exit")) Window.Close();
				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Edit"))
			{
				if (ImGui.MenuItem(Undo.CanUndo ? "Undo " + Undo.UndoName : "Undo", "Ctrl+Z", false, Undo.CanUndo)) UndoLast();
				if (ImGui.MenuItem(Undo.CanRedo ? "Redo " + Undo.RedoName : "Redo", "Ctrl+Y", false, Undo.CanRedo)) RedoLast();
				ImGui.Separator();
				if (ImGui.MenuItem("Duplicate", "Ctrl+D", false, Selected != null)) DuplicateSelected();
				if (ImGui.MenuItem("Delete", "Del", false, Selected != null)) DeleteSelected();
				ImGui.Separator();
				bool reload = Project.ReloadScriptsOnPlay;
				if (ImGui.MenuItem("Reload Scripts On Play", "", reload))
				{
					Project.ReloadScriptsOnPlay = !reload;
					Project.Save();
				}

				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("GameObject"))
			{
				DrawCreateMenu(null);
				ImGui.EndMenu();
			}

			DrawPlayControls();
			DrawStatus();
			ImGui.EndMainMenuBar();
		}

		/// <summary>Create entries (also used by the hierarchy's context menu).</summary>
		public void DrawCreateMenu(Transform parent)
		{
			if (ImGui.MenuItem("Create Empty")) CreateObject("GameObject", null, parent);
			ImGui.Separator();
			if (ImGui.MenuItem("Cube")) CreateObject("Cube", go => DefaultScene.AddMesh(go, "Cube.obj"), parent);
			if (ImGui.MenuItem("Sphere")) CreateObject("Sphere", go => DefaultScene.AddMesh(go, "Sphere.obj"), parent);
			if (ImGui.MenuItem("Quad")) CreateObject("Quad", go => DefaultScene.AddMesh(go, "Quad.obj"), parent);
			ImGui.Separator();
			if (ImGui.MenuItem("Camera")) CreateObject("Camera", go => go.AddComponent<Camera>(), parent);
			if (ImGui.MenuItem("Directional Light"))
			{
				CreateObject("Directional Light", go =>
				{
					go.transform.rotation = QuaternionHelper.LookRotation(new Vector3(-0.45f, -0.75f, 0.35f).Normalized(), Vector3.UnitY);
					go.AddComponent<DirectionalLight>();
				}, parent);
			}
		}

		private void DrawPlayControls()
		{
			float button = ImGui.GetFrameHeight() * 1.5f;
			float width = button * 3f + ImGui.GetStyle().ItemSpacing.X * 2f;
			ImGui.SetCursorPosX((ImGui.GetWindowWidth() - width) * 0.5f);
			bool playing = IsPlaying;
			if (EditorGui.IconButton("play", playing ? Icon.Stop : Icon.Play, playing, playing ? "Stop (Ctrl+P)" : "Play (Ctrl+P)")) TogglePlay();
			ImGui.BeginDisabled(!playing);
			bool paused = PlayState == PlayState.Paused;
			if (EditorGui.IconButton("pause", Icon.Pause, paused, paused ? "Resume (Ctrl+Shift+P)" : "Pause (Ctrl+Shift+P)")) TogglePause();
			if (EditorGui.IconButton("step", Icon.Step, false, "Step one frame")) Step();
			ImGui.EndDisabled();
		}

		private void DrawStatus()
		{
			string status = Scripts?.StatusText;
			if (string.IsNullOrEmpty(status))
			{
				return;
			}

			float width = ImGui.CalcTextSize(status).X + 16;
			ImGui.SetCursorPosX(ImGui.GetWindowWidth() - width);
			if (Scripts.HasErrors)
			{
				ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.35f, 1f), status);
			}
			else
			{
				ImGui.TextDisabled(status);
			}
		}

		private void HandleShortcuts()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			if (io.WantTextInput)
			{
				return;
			}

			bool ctrl = io.KeyCtrl;
			bool shift = io.KeyShift;
			if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
			{
				if (shift) SaveSceneAs();
				else SaveScene();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.N, false)) NewScene();
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
			{
				if (shift) RedoLast();
				else UndoLast();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y, false)) RedoLast();
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.P, false))
			{
				if (shift) TogglePause();
				else TogglePlay();
			}
			else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, false)) DuplicateSelected();
			else if (ctrl && shift && ImGui.IsKeyPressed(ImGuiKey.B, false)) ShowBuildWindow();
			else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.Delete, false) && Selected != null && (hierarchy.IsFocused || SceneView.IsFocused)) DeleteSelected();
		}

		private unsafe void BuildDefaultLayout(uint dockspace)
		{
			ImGuiP.DockBuilderRemoveNode(dockspace);
			ImGuiP.DockBuilderAddNode(dockspace, (ImGuiDockNodeFlags)(1 << 10));
			ImGuiP.DockBuilderSetNodeSize(dockspace, ImGui.GetMainViewport().Size);

			uint center = dockspace;
			uint right = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.25f, null, &center);
			uint left = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.22f, null, &center);
			uint bottom = ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.28f, null, &center);

			ImGuiP.DockBuilderDockWindow("Hierarchy", left);
			ImGuiP.DockBuilderDockWindow("Inspector", right);
			ImGuiP.DockBuilderDockWindow("Lighting", right);
			ImGuiP.DockBuilderDockWindow("Project", bottom);
			ImGuiP.DockBuilderDockWindow("Console", bottom);
			ImGuiP.DockBuilderDockWindow("Scene", center);
			ImGuiP.DockBuilderDockWindow("Game", center);
			ImGuiP.DockBuilderFinish(dockspace);
		}

		private void UpdateTitle()
		{
			string title = Project == null
				? "LELEngine Editor"
				: $"LELEngine Editor - {Project.Name} - {Scene?.Name ?? "no scene"}{(SceneDirty ? " *" : "")}{(IsPlaying ? " [Playing]" : "")}";
			if (title != lastTitle)
			{
				lastTitle = title;
				Window.Title = title;
			}
		}

		private void OnAssetsChanged()
		{
			assetsChangedAt = Time.timeD;
			assetsChanged = true;
		}

		private void RefreshAssetsIfChanged()
		{
			// Debounced: copying a folder raises many events.
			if (!assetsChanged || Time.timeD - assetsChangedAt < 0.5)
			{
				return;
			}

			assetsChanged = false;
			AssetDatabase.Refresh(true);
			AssetsVersion++;
			NotifySceneChanged();
		}

		/// <summary>Edit mode with an enabled [ExecuteAlways] game component: it may change the scene every frame.</summary>
		private bool EditModeScriptsRunning()
		{
			Scene scene = Scene;
			if (scene == null || IsPlaying)
			{
				return false;
			}

			System.Reflection.Assembly engine = typeof(Behaviour).Assembly;
			foreach (Behaviour behaviour in scene.ActiveBehaviours)
			{
				Type type = behaviour.GetType();
				if (type.Assembly != engine && type.IsDefined(typeof(ExecuteAlways), true))
				{
					return true;
				}
			}

			return false;
		}

		private void SaveEditorState()
		{
			if (Project != null)
			{
				SceneView.SaveState(Project);
			}
		}

		private static bool HasStaticRenderer(GameObject go)
		{
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			if (renderer != null && renderer.IsStatic)
			{
				return true;
			}

			foreach (Transform child in go.transform.Children)
			{
				if (HasStaticRenderer(child.gameObject))
				{
					return true;
				}
			}

			return false;
		}

		// Game input: keys reach the game only while it plays and the game view has focus.
		private void OnWindowKeyDown(KeyboardKeyEventArgs e)
		{
			if (PlayState == PlayState.Playing && GameView.HasInputFocus)
			{
				Input.Input_KeyDown(e);
			}
		}

		private static void OnWindowKeyUp(KeyboardKeyEventArgs e)
		{
			Input.Input_KeyUp(e);
		}

		private static void OnWindowMouseMove(MouseMoveEventArgs e)
		{
			Input.Input_MouseMove(e);
		}

		private static string GetValue(string[] args, string name)
		{
			foreach (string arg in args)
			{
				int eq = arg.IndexOf('=');
				if (eq > 0 && string.Equals(arg.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase))
				{
					return arg.Substring(eq + 1).Trim().Trim('"');
				}
			}

			return null;
		}

		#endregion
	}
}
