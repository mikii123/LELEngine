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
	internal sealed partial class EditorApplication : IDisposable
	{
		#region PublicFields

		public static EditorApplication Instance { get; private set; }

		public MainWindow Window { get; }
		public ProjectInfo Project { get; private set; }
		public Scene Scene => SceneManager.ActiveScene;
		public GameObject Selected { get; private set; }

		/// <summary>Asset path of the project file shown in the inspector (when no GameObject is selected).</summary>
		public string SelectedAsset { get; private set; }

		/// <summary>The GameObject the inspector shows: the selection, or the object the inspector is locked on.</summary>
		public GameObject InspectedObject => inspector.Shown ?? Selected;
		public bool SceneDirty { get; private set; }
		public PlayState PlayState { get; private set; } = PlayState.Edit;
		public bool IsPlaying => PlayState != PlayState.Edit;
		public UndoStack Undo { get; } = new UndoStack();
		public EditTracker Edits { get; }
		public ConsoleLog Log { get; }
		public ScriptDomain Scripts { get; private set; }

		/// <summary>Custom inspectors, windows and menu items of the project's editor scripts.</summary>
		public EditorExtensions Extensions { get; }

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

		/// <summary>Why the last project could not be opened (the project chooser shows it), or null.</summary>
		public string OpenError { get; private set; }

		/// <summary>A run driven by a script (<see cref="IsAutomatedRun" />): windows open as by default, nothing about them is saved.</summary>
		public bool IsAutomated => automated;

		#endregion

		#region PrivateFields

		private readonly HierarchyPanel hierarchy;
		private readonly InspectorPanel inspector;
		private readonly ProjectPanel projectPanel;
		private readonly ConsolePanel consolePanel;
		private readonly LightingPanel lightingPanel;
		private readonly ProjectSettingsWindow projectSettingsWindow;
		private readonly PreferencesWindow preferencesWindow = new PreferencesWindow();
		private readonly VFX.VfxGraphWindow vfxGraphWindow;

		/// <summary>The console's error count Error Pause last saw.</summary>
		private int errorsSeen;
		private readonly InputManagerWindow inputManagerWindow;
		private readonly GameInputHost gameInput;
		private readonly TestHooks testHooks;
		private readonly bool layoutFileExisted;
		private bool layoutBuilt;
		private string lastTitle;

		// Play mode: the edit scene as it was when play started.
		private byte[] playSnapshot;
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

		// Changes made outside the editor (git, Explorer, a text editor): the ProjectSettings folder is watched too, and
		// the stamps of the files the editor read or wrote last tell its own writes from someone else's.
		private FileSystemWatcher settingsWatcher;
		private volatile bool settingsChanged;
		private double settingsChangedAt;
		private FileStamp projectSettingsStamp;
		private FileStamp sceneStamp;

		/// <summary>The open scene's file changed on disk: reloaded when the scene has no unsaved changes, asked otherwise (after play mode).</summary>
		private bool sceneChangedOnDisk;
		private bool openSceneChangedPopup;

		/// <summary>
		///     An automated run (<see cref="IsAutomatedRun" />): nobody is there to choose a project when it has none, and
		///     the windows open as by default (their saved state is neither read nor written).
		/// </summary>
		private readonly bool automated;

		/// <summary>The project just opened: its windows reopen on the next frame (the UI Builder needs a frame loop).</summary>
		private bool restoreWindowsPending;

		/// <summary>Window > Reset Layout: the default docking is built on the next frame.</summary>
		private bool resetLayoutRequested;

		private static string WindowStatesFile => Path.Combine(RecentProjects.EditorDataFolder, "windows.json");

		#endregion

		#region Constructors

		public EditorApplication(MainWindow window, string layoutFile, string[] args)
		{
			Instance = this;
			Window = window;
			automated = IsAutomatedRun(args);
			Edits = new EditTracker(this);
			Extensions = new EditorExtensions(this);
			Log = ConsoleLog.Install();
			// The audio device opens on its own thread; the project's audio project and banks follow when it opens.
			LELEngine.Audio.AudioSystem.Initialize(new LELEngine.Audio.AudioSettings());
			layoutFileExisted = File.Exists(layoutFile);

			hierarchy = new HierarchyPanel(this);
			inspector = new InspectorPanel(this);
			SceneView = new SceneViewPanel(this);
			GameView = new GameViewPanel(this);
			projectPanel = new ProjectPanel(this);
			consolePanel = new ConsolePanel(this);
			lightingPanel = new LightingPanel(this);
			projectSettingsWindow = new ProjectSettingsWindow(this);
			vfxGraphWindow = new VFX.VfxGraphWindow(this);
			inputManagerWindow = new InputManagerWindow(this);

			gameInput = new GameInputHost(window, () => PlayState == PlayState.Playing && GameView.HasInputFocus);
			Input.Host = gameInput;
			Navigation.NavMesh.NavMeshChanged += NotifySceneChanged;
			TextureAssets.TextureReplaced += OnTextureReplaced;

			// The editor keeps its assets loaded across scenes (views, inspectors and thumbnails use them); scripts
			// loading scenes in play mode change the active scene.
			SceneManager.Lifecycle = SceneLifecycle.Edit;
			SceneManager.ReleaseUnusedAssets = false;
			SceneManager.ActiveSceneChanged += OnActiveSceneChanged;
			window.KeyDown += OnWindowKeyDown;
			window.KeyUp += OnWindowKeyUp;
			window.MouseMove += OnWindowMouseMove;
			window.MouseDown += OnWindowMouseDown;
			window.MouseUp += OnWindowMouseUp;
			window.MouseWheel += OnWindowMouseWheel;
			window.TextInput += OnWindowTextInput;
			UIElements.UIRuntime.GetClipboard = () => window.ClipboardString;
			UIElements.UIRuntime.SetClipboard = text => window.ClipboardString = text;

			// The window always edits a project: project=folder (the project chooser passes it) opens it;
			// newproject=parent;name creates it first when missing (automated runs). Without one the chooser comes back.
			try
			{
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

				string projectArgument = ProjectArgument(args);
				if (Project == null && projectArgument != null)
				{
					if (ProjectInfo.IsProjectFolder(projectArgument))
					{
						OpenProject(projectArgument);
					}
					else
					{
						OpenError = "Not a LELEngine project folder (no ProjectSettings/Project.json): " + projectArgument;
					}
				}
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				OpenError = "Cannot open the project: " + e.Message;
				if (Project != null)
				{
					CloseProject();
				}
			}

			testHooks = new TestHooks(this, args);
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- frame

		public void Update(float deltaTime)
		{
			EditorPreferences.Update();
			if (Project == null)
			{
				ChooseProject();
				return;
			}

			// Error Pause: an error or exception since the last frame pauses the game before its next frame.
			if (Log.ErrorsLogged != errorsSeen)
			{
				errorsSeen = Log.ErrorsLogged;
				if (PlayState == PlayState.Playing && EditorPreferences.Current.Console.ErrorPause)
				{
					TogglePause();
					Debug.Log("[Editor] Paused on an error (Console > Error Pause)");
				}
			}

			if (restoreWindowsPending)
			{
				restoreWindowsPending = false;
				RestoreWindowStates();
			}

			// The GI method changes in edit mode only (Project Settings, undo, a reload from disk); play mode keeps the
			// method it started with.
			Lighting.AllowGIMethodChange = PlayState == PlayState.Edit;

			RefreshAssetsIfChanged();
			RefreshSettingsIfChanged();
			ReloadSceneIfChangedOnDisk();
			Scripts?.Update();
			testHooks.Update();
			UpdatePrefabStage();
			if (PlayState == PlayState.Edit)
			{
				// Resources.LoadAsync requests and code awaiting on the main thread advance in edit mode too (editor
				// scripts, [ExecuteAlways] components); play mode advances them with the scenes.
				SceneManager.UpdateResourceLoads();
			}

			Scene scene = Scene;
			if (scene == null || PlayState == PlayState.Paused)
			{
				return;
			}

			if (PlayState == PlayState.Playing)
			{
				// Background scene loads advance, then every loaded scene runs (scripts may load more). What the frame
				// logs is the game's (the console tells it from the editor's).
				ConsoleLog.BeginGame();
				try
				{
					Input.BeginFrame();
					SceneManager.Update();
					SceneManager.UpdateScenes(deltaTime);
					Input.EndFrame();
				}
				finally
				{
					ConsoleLog.EndGame();
				}
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
			UpdateAudio();
			if (Project == null)
			{
				// Hidden while the project chooser is up (see ChooseProject).
				return;
			}

			HandleShortcuts();
			DrawMainMenu();

			uint dockspace = ImGui.DockSpaceOverViewport();
			if (!layoutBuilt || resetLayoutRequested)
			{
				layoutBuilt = true;
				if (!layoutFileExisted || resetLayoutRequested)
				{
					BuildDefaultLayout(dockspace);
				}

				resetLayoutRequested = false;
			}

			if (testHooks.PendingDock is (string window, string target))
			{
				testHooks.PendingDock = null;
				DockInto(window, target);
			}

			hierarchy.Draw();
			SceneView.Draw();
			GameView.Draw();
			inspector.Draw();
			lightingPanel.Draw();
			projectPanel.Draw();
			consolePanel.Draw();
			projectSettingsWindow.Draw();
			preferencesWindow.Draw();
			vfxGraphWindow.Draw();
			inputManagerWindow.Draw();
			Extensions.DrawWindows();
			ObjectPicker.Draw(this);
			Extensions.EndFrame();
			Edits.Flush();
			RunDeferred();

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

			bool continuous = PlayState == PlayState.Playing || SceneView.AlwaysRefresh || EditModeScriptsRunning() || UIElements.UIRuntime.AnyPanelAnimating || LELEngine.VFX.VfxRuntime.AnyActive;
			SceneView.Render(continuous);
			GameView.Render(continuous);
			DebugDraw.Clear();
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
			if (!closeConfirmed && Stage != null && !IsPlaying)
			{
				if (SceneDirty && !Stage.AutoSave)
				{
					ConfirmLeaveStage(() =>
					{
						LeaveAllStages(null);
						Window.Close();
					});
					return false;
				}

				// Saved (automatically) and back to the scene, whose own changes are asked about below.
				LeaveAllStages(null);
			}

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

		/// <summary>
		///     No project (File > Close Project, or it could not be opened): the editor's window hides and the project
		///     chooser shows in its own window until a project opens; then the editor's window comes back with it. Its
		///     GL context stays, so the engine's caches (shaders, geometry) stay valid. Closing the chooser quits.
		/// </summary>
		private void ChooseProject()
		{
			if (automated)
			{
				Debug.LogError("[Editor] No project to open: " + (OpenError ?? "none given"));
				closeConfirmed = true;
				Window.Close();
				return;
			}

			Window.IsVisible = false;
			while (Project == null)
			{
				string chosen = LauncherWindow.Choose(OpenError);
				// The chooser had its own GL and ImGui contexts current.
				Window.MakeEditorCurrent();
				if (chosen == null)
				{
					closeConfirmed = true;
					Window.Close();
					return;
				}

				OpenError = null;
				try
				{
					OpenProject(chosen);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
					OpenError = "Cannot open the project: " + e.Message;
					if (Project != null)
					{
						CloseProject();
					}
				}
			}

			Window.IsVisible = true;
			Window.Focus();
		}

		public void OpenProject(string root)
		{
			ProjectInfo project = ProjectInfo.Load(root);
			CloseProject();

			Project = project;
			// Physics and lighting quality of the project; projects from before the file get it with the defaults.
			project.LoadProjectSettings();
			projectSettingsStamp = FileStamp.Of(project.ProjectSettingsFile);
			AssetDatabase.Initialize(project.AssetsPath, true);
			ModelAssets.CacheFolder = project.ModelCachePath;
			ImportChangedModels(true);
			// Remapped controls of play mode are the project's, not the user's (a built game keeps them in AppData).
			LELEngine.InputSystem.InputManager.UserDataPath = Path.Combine(project.LibraryPath, "InputUserData.json");
			OpenAudioProjectForEditor();
			if (!automated)
			{
				// Test projects stay out of the user's list.
				RecentProjects.Add(project.RootPath);
			}

			Debug.Log($"[Editor] Opened project {project.Name} ({project.RootPath})");

			GameView.EnsureRenderer();
			SceneView.EnsureRenderer();
			Game.Renderer = GameView.ViewRenderer.Renderer;
			SceneView.LoadState(project);

			assetWatcher = new FileSystemWatcher(project.AssetsPath) { IncludeSubdirectories = true };
			assetWatcher.Created += (s, e) => OnAssetsChanged();
			assetWatcher.Deleted += (s, e) => OnAssetsChanged();
			assetWatcher.Renamed += (s, e) => OnAssetsChanged();
			// Edited files: UI style sheets and documents reload in place.
			assetWatcher.Changed += (s, e) => OnAssetsChanged();
			assetWatcher.EnableRaisingEvents = true;

			// Project.json and ProjectSettings.asset (git checkouts, edits in a text editor).
			Directory.CreateDirectory(project.SettingsPath);
			settingsWatcher = new FileSystemWatcher(project.SettingsPath);
			settingsWatcher.Created += (s, e) => OnSettingsChanged();
			settingsWatcher.Renamed += (s, e) => OnSettingsChanged();
			settingsWatcher.Changed += (s, e) => OnSettingsChanged();
			settingsWatcher.EnableRaisingEvents = true;

			Extensions.LoadOpenWindows(project);
			Scripts = new ScriptDomain(this, project);
			Scripts.Initialize();

			// The windows as they were (not in automated runs: they get the default ones).
			restoreWindowsPending = !automated;

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

			stageFrames.Clear();
			SceneManager.UnloadAll(true);
			PrefabCache.Clear();
			Scripts?.Dispose();
			Scripts = null;
			Extensions.Reset();
			assetWatcher?.Dispose();
			assetWatcher = null;
			settingsWatcher?.Dispose();
			settingsWatcher = null;
			settingsChanged = false;
			sceneChangedOnDisk = false;
			Selected = null;
			SelectedAsset = null;
			AssetInspectors.Deselected();
			ObjectPicker.Close();
			DragAndDrop.Clear();
			CollectionDrawers.Reset();
			AssetThumbnails.Clear();
			vfxGraphWindow.Reset();
			Undo.Clear();
			Edits.Cancel();
			ScriptableObjectAssets.UnloadAll();
			InternalStorage.Clear();
			ModelAssets.CacheFolder = null;
			// Audio: the studio closes with the project, every bank and clip goes.
			audioStudio?.Dispose();
			audioStudio = null;
			LELEngine.Audio.AudioSystem.StopAll();
			foreach (LELEngine.Audio.LoadedBank bank in new List<LELEngine.Audio.LoadedBank>(LELEngine.Audio.AudioSystem.LoadedBanks))
			{
				LELEngine.Audio.AudioSystem.UnloadBank(bank.Name);
			}

			LELEngine.Audio.AudioSystem.UseProject(null);
			LELEngine.Audio.AudioProject.ClearCache();
			LELEngine.Audio.AudioClip.ClearCache();
			ProjectSettings.Use(null);
			Project = null;
		}

		/// <summary>
		///     Makes the game's audio project (Project Settings > Audio, or the only .lelaudio file) the audio system's
		///     source and loads every bank of it, so events play in the editor (play mode, inspector previews, the studio).
		/// </summary>
		private void OpenAudioProjectForEditor()
		{
			string path = LELEngine.Audio.AudioProject.FindProjectPath();
			LELEngine.Audio.AudioProject project = path != null ? LELEngine.Audio.AudioProject.Load(path) : null;
			if (LELEngine.Audio.AudioSystem.SourceProject != project)
			{
				foreach (LELEngine.Audio.LoadedBank bank in new List<LELEngine.Audio.LoadedBank>(LELEngine.Audio.AudioSystem.LoadedBanks))
				{
					LELEngine.Audio.AudioSystem.UnloadBank(bank.Name);
				}

				LELEngine.Audio.AudioSystem.UseProject(project);
				if (project != null)
				{
					foreach (LELEngine.Audio.AudioBank bank in project.Banks)
					{
						LELEngine.Audio.AudioSystem.LoadBank(bank.Name);
					}
				}
			}

			LELEngine.Audio.AudioSystem.ApplySettings(ProjectSettings.Current.Audio);
		}

		/// <summary>Per frame: the listener (the scene camera outside play mode) and the audio system's callbacks.</summary>
		private void UpdateAudio()
		{
			if (!LELEngine.Audio.AudioSystem.IsInitialized)
			{
				return;
			}

			if (Project != null && PlayState != PlayState.Playing)
			{
				LELEngine.Audio.AudioSystem.SetListener(SceneView.CameraPosition, SceneView.CameraForward, Vector3.UnitY, Vector3.Zero);
			}

			LELEngine.Audio.AudioSystem.Update();
		}

		/// <summary>
		///     A script loaded or unloaded scenes in play mode (the editor's own scene changes come through
		///     <see cref="ReplaceScene" />): the views drop their GI caches and a selected object that is gone is deselected.
		/// </summary>
		private void OnActiveSceneChanged(Scene previous, Scene next)
		{
			if (!IsPlaying)
			{
				return;
			}

			SceneView.InvalidateCaches();
			GameView.InvalidateCaches();
			if (Selected != null && (Selected.dead || Selected.scene == null))
			{
				Selected = null;
			}

			NotifySceneChanged();
		}

		/// <summary>A reimport replaced a texture object (its shape changed): materials holding the old one take the new one.</summary>
		private static void OnTextureReplaced(string path, Texture old, Texture replacement)
		{
			bool skyCached;
			Shaders.Material sky = Lighting.Environment?.Skybox;
			lock (InternalStorage.Sync)
			{
				foreach (Shaders.Material material in InternalStorage.Materials.Values)
				{
					foreach (Shaders.MaterialProperty property in material.Properties)
					{
						if (property is Shaders.TextureProperty texture && ReferenceEquals(texture.value, old))
						{
							texture.value = replacement;
						}
					}
				}

				skyCached = sky != null && InternalStorage.Materials.ContainsValue(sky);
			}

			if (sky != null && !skyCached)
			{
				foreach (Shaders.MaterialProperty property in sky.Properties)
				{
					if (property is Shaders.TextureProperty texture && ReferenceEquals(texture.value, old))
					{
						texture.value = replacement;
					}
				}
			}
		}

		// ---------------------------------------------------------------- scenes

		public void NewScene()
		{
			LeaveAllStages(() => ConfirmDiscard(NewSceneNow));
		}

		public void OpenScene(string assetPath)
		{
			LeaveAllStages(() => ConfirmDiscard(() => OpenSceneNow(assetPath)));
		}

		public void SaveScene()
		{
			if (IsPlaying)
			{
				Debug.LogWarning("[Editor] Exit play mode to save the scene.");
				return;
			}

			if (Stage != null)
			{
				// Prefab mode: Ctrl+S saves the prefab.
				SavePrefabStage();
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
			if (Scene != null && Stage == null)
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
			// Drags and open collection rows may point into the old scene.
			DragAndDrop.Clear();
			CollectionDrawers.Reset();
			// Every scene goes: play mode may have loaded more (additively, DontDestroyOnLoad, in the background).
			SceneManager.UnloadAll(true);
			SceneManager.SetActiveScene(scene);
			scene?.StartLifecycle(playing);
			SceneView.InvalidateCaches();
			GameView.InvalidateCaches();
			NotifySceneChanged();
			Selected = selected != 0 ? scene?.FindObjectById(selected) : null;
		}

		/// <summary>Reloads the scene from a snapshot (undo of structural changes), keeping its path.</summary>
		public void RestoreSnapshot(byte[] snapshot, ulong selection)
		{
			string path = Scene?.Path;
			Scene scene = SceneSerializer.LoadFromBytes(snapshot);
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
			if (go != null && SelectedAsset != null)
			{
				SelectedAsset = null;
				AssetInspectors.Deselected();
			}
		}

		/// <summary>Nothing selected: the inspector clears (a click on an empty area of the hierarchy or the project panel).</summary>
		public void ClearSelection()
		{
			SelectAsset(null);
		}

		/// <summary>
		///     Renames or moves an asset file or folder (<see cref="AssetDatabase.MoveAsset" />): loaded assets keep their
		///     identity, and the open scene, prefab stages, the selection and the project's scene lists follow. Not in play
		///     mode. Returns an error message, or null when it moved.
		/// </summary>
		public string MoveAsset(string fromPath, string toPath)
		{
			if (IsPlaying)
			{
				return "Stop play mode to rename or move assets.";
			}

			Edits.Commit();
			string error = AssetDatabase.MoveAsset(fromPath, toPath);
			if (error != null)
			{
				return error;
			}

			string Moved(string path) => AssetDatabase.MovedPath(path, fromPath, toPath) ?? path;
			if (Scene != null && Scene.Path != null)
			{
				Scene.Path = Moved(Scene.Path);
			}

			foreach (StageFrame frame in stageFrames)
			{
				frame.Path = frame.Path != null ? Moved(frame.Path) : null;
				if (frame.Stage != null)
				{
					frame.Stage.AssetPath = Moved(frame.Stage.AssetPath);
				}
			}

			if (SelectedAsset != null)
			{
				SelectedAsset = Moved(SelectedAsset);
			}

			inspector.AssetMoved(fromPath, toPath);
			inputManagerWindow.AssetMoved(fromPath, toPath);
			AssetThumbnails.Invalidate(fromPath);

			// The project's scene lists name scenes by path.
			string startScene = Moved(Project.StartScene);
			string lastScene = Project.LastScene != null ? Moved(Project.LastScene) : null;
			List<string> buildScenes = Project.BuildScenes.ConvertAll(Moved);
			bool buildScenesMoved = false;
			for (int i = 0; i < buildScenes.Count; i++)
			{
				buildScenesMoved |= buildScenes[i] != Project.BuildScenes[i];
			}

			if (startScene != Project.StartScene || lastScene != Project.LastScene || buildScenesMoved)
			{
				Project.StartScene = startScene;
				Project.LastScene = lastScene;
				Project.BuildScenes = buildScenes;
				Project.Save();
			}

			// Project settings that name assets by path: the audio project, the input configuration.
			ProjectSettings settings = ProjectSettings.Current;
			string audioProject = settings.Audio.Project != "" ? Moved(settings.Audio.Project) : "";
			string inputConfiguration = settings.Input.Configuration != "" ? Moved(settings.Input.Configuration) : "";
			if (audioProject != settings.Audio.Project || inputConfiguration != settings.Input.Configuration)
			{
				settings.Audio.Project = audioProject;
				settings.Input.Configuration = inputConfiguration;
				settings.Save(Project.ProjectSettingsFile);
				projectSettingsStamp = FileStamp.Of(Project.ProjectSettingsFile);
			}

			NotifyAssetsChanged();
			Debug.Log($"[Editor] Moved {fromPath} to {toPath}");
			return null;
		}

		/// <summary>Shows a project file in the inspector (clears the GameObject selection, as in Unity).</summary>
		public void SelectAsset(string assetPath)
		{
			if (!string.Equals(SelectedAsset, assetPath, StringComparison.OrdinalIgnoreCase))
			{
				Edits.Commit();
				AssetInspectors.Deselected();
			}

			Selected = null;
			SelectedAsset = assetPath;
		}

		/// <summary>
		///     Selects what an object field references: a GameObject (a component's GameObject) in the hierarchy, an
		///     asset file in the project panel and the inspector.
		/// </summary>
		public void SelectObject(object target)
		{
			switch (target)
			{
				case GameObject go when go.scene == Scene:
					Select(go);
					hierarchy.Ping(go);
					break;
				case Behaviour component when component.gameObject?.scene == Scene:
					Select(component.gameObject);
					hierarchy.Ping(component.gameObject);
					break;
				case IAsset asset when asset.AssetPath != null && !AssetDatabase.IsEngineResource(asset.AssetPath):
					SelectAsset(asset.AssetPath);
					projectPanel.Ping(asset.AssetPath);
					break;
			}
		}

		/// <summary>Shows where an object field's reference is without selecting it: the hierarchy row or the project file flashes.</summary>
		public void Ping(object target)
		{
			switch (target)
			{
				case GameObject go when go.scene == Scene:
					hierarchy.Ping(go);
					break;
				case Behaviour component when component.gameObject?.scene == Scene:
					hierarchy.Ping(component.gameObject);
					break;
				case IAsset asset when asset.AssetPath != null && !AssetDatabase.IsEngineResource(asset.AssetPath):
					projectPanel.Ping(asset.AssetPath);
					break;
				case string assetPath:
					projectPanel.Ping(assetPath);
					break;
			}
		}

		public void MarkDirty()
		{
			NotifySceneChanged();
			lastEditTime = Time.timeD;
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
			byte[] before = SceneSerializer.SaveToBytes(Scene);
			ulong selectionBefore = Selected?.Id ?? 0;
			change();
			byte[] after = SceneSerializer.SaveToBytes(Scene);
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

			if (Stage != null && target == StageRoot)
			{
				Debug.LogWarning("[Editor] The root of the edited prefab cannot be deleted.");
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
				byte[] data = SceneSerializer.SerializeObjects(Scene, new[] { target });
				List<GameObject> copies = SceneSerializer.InstantiateObjects(data, Scene, target.transform.parent);
				if (copies.Count > 0)
				{
					Selected = copies[0];
				}
			});
		}

		/// <summary>
		///     Rebuilds the scene from its saved state around <paramref name="change" /> (assets were reimported:
		///     components load their references again). The dirty state and the selection are kept.
		/// </summary>
		public void ReloadScene(Action change)
		{
			Scene scene = Scene;
			if (scene == null)
			{
				change();
				return;
			}

			Edits.Commit();
			byte[] snapshot = SceneSerializer.SaveToBytes(scene);
			string path = scene.Path;
			ulong selected = Selected?.Id ?? 0;
			change();
			Scene reloaded = SceneSerializer.LoadFromBytes(snapshot);
			reloaded.Path = path;
			ReplaceScene(reloaded, IsPlaying);
			Selected = selected != 0 ? reloaded.FindObjectById(selected) : null;
		}

		/// <summary>Builds a model file's hierarchy in the scene as one undo step and selects its root.</summary>
		public GameObject InstantiateModel(string assetPath, Transform parent = null)
		{
			Model model;
			try
			{
				model = ModelAssets.Load(assetPath);
			}
			catch (Exception e)
			{
				Debug.LogError($"[Editor] Cannot import {assetPath}: {e.Message}");
				return null;
			}

			GameObject root = null;
			parent ??= StageRoot?.transform;
			RecordStructural("Instantiate " + model.name, () =>
			{
				root = model.Instantiate(Scene, parent);
				if (root != null && parent == null)
				{
					root.transform.position = SceneView.DefaultSpawnPoint();
				}

				Selected = root;
			});
			return root;
		}

		public GameObject CreateObject(string name, Action<GameObject> setup = null, Transform parent = null)
		{
			return CreateModelObject(name, null, setup, parent);
		}

		/// <summary>
		///     Creates an instance of a project model (<paramref name="modelPath" />, null: an empty GameObject) as one undo
		///     step, at the scene view's spawn point or under <paramref name="parent" />, and selects it.
		/// </summary>
		public GameObject CreateModelObject(string name, string modelPath, Action<GameObject> setup = null, Transform parent = null)
		{
			GameObject created = null;
			// Prefab mode: everything goes under the prefab's root (a prefab has one).
			parent ??= StageRoot?.transform;
			RecordStructural("Create " + name, () =>
			{
				created = modelPath != null ? DefaultScene.Instantiate(Scene, modelPath, name, parent) : Scene.CreateGameObject(name);
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

			LELEngine.Audio.AudioSystem.PauseAll(PlayState == PlayState.Paused);
		}

		/// <summary>One frame of game logic while paused.</summary>
		public void Step()
		{
			if (PlayState == PlayState.Paused && Scene != null)
			{
				ConsoleLog.BeginGame();
				try
				{
					Input.BeginFrame();
					Scene.Update(1f / 60f);
					Input.EndFrame();
				}
				finally
				{
					ConsoleLog.EndGame();
				}

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

			if (Stage != null)
			{
				// Play mode plays the scene: prefab mode closes first.
				LeaveAllStages(EnterPlayMode);
				return;
			}

			Edits.Commit();
			if (EditorPreferences.Current.Console.ClearOnPlay)
			{
				Log.Clear();
			}

			// Previews of the studio and the inspectors stop; the game starts with silence.
			LELEngine.Audio.AudioSystem.StopAll();
			playSnapshot = SceneSerializer.SaveToBytes(Scene);
			playScenePath = Scene.Path;
			playSceneDirty = SceneDirty;

			// Full reload of the game scripts: the play session starts from clean statics.
			ulong selected = Selected?.Id ?? 0;
			Scripts?.TrackComponents(Scene);
			if (Project.ReloadScriptsOnPlay && Scripts != null)
			{
				// Editor windows keep their references to scene objects across the reload.
				Extensions.CaptureWindowStates();
			}

			Selected = null;
			SceneManager.UnloadAll(true);
			if (Project.ReloadScriptsOnPlay && Scripts != null)
			{
				Scripts.ReloadNow();
			}

			Scene play = SceneSerializer.LoadFromBytes(playSnapshot);
			play.Path = playScenePath;
			Time.ResetTime();
			PlayState = PlayState.Playing;
			SceneManager.Lifecycle = SceneLifecycle.Play;
			ConsoleLog.BeginGame();
			try
			{
				// The game's start (Awake, OnEnable, Start).
				ReplaceScene(play, true);
			}
			finally
			{
				ConsoleLog.EndGame();
			}
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
			// The next play starts the input system again (with the configuration as edited meanwhile).
			LELEngine.InputSystem.InputManager.Shutdown();
			PlayState = PlayState.Edit;
			SceneManager.Lifecycle = SceneLifecycle.Edit;
			LELEngine.Audio.AudioSystem.StopAll();
			LELEngine.Audio.AudioSystem.PauseAll(false);
			Scene edit = SceneSerializer.LoadFromBytes(playSnapshot);
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
			byte[] snapshot = SceneSerializer.SaveToBytes(scene);
			string path = scene.Path;
			ulong selected = Selected?.Id ?? 0;
			bool playing = IsPlaying;
			Scripts?.TrackComponents(scene);
			Extensions.CaptureWindowStates();
			SceneManager.UnloadAll(true);
			Selected = null;

			reload();

			Scene reloaded = SceneSerializer.LoadFromBytes(snapshot);
			reloaded.Path = path;
			ReplaceScene(reloaded, playing);
			Selected = selected != 0 ? reloaded.FindObjectById(selected) : null;
		}

		public void ShowBuildWindow()
		{
			projectSettingsWindow.Show(ProjectSettingsWindow.Tab.Build);
		}

		/// <summary>Opens the Project Settings window (on the given tab, or the last one shown).</summary>
		public void ShowProjectSettings(ProjectSettingsWindow.Tab? tab = null)
		{
			projectSettingsWindow.Show(tab);
		}

		/// <summary>Edit > Preferences... on a section (null: the one it was on).</summary>
		public void ShowPreferences(PreferencesWindow.Section? section = null)
		{
			preferencesWindow.Show(section);
		}

		/// <summary>Selects the first console message containing the text (test runs).</summary>
		public bool SelectConsoleMessage(string text)
		{
			consolePanel.Open = true;
			return consolePanel.SelectFirst(text);
		}

		/// <summary>Opens Window > Input Manager on an input configuration (null: the project's).</summary>
		public void OpenInputManager(string assetPath, string page = null)
		{
			inputManagerWindow.Show(assetPath);
			if (page != null)
			{
				inputManagerWindow.ShowPage(page);
			}
		}

		/// <summary>Writes the live project settings to ProjectSettings/ProjectSettings.asset.</summary>
		public void SaveProjectSettings()
		{
			if (Project == null)
			{
				return;
			}

			try
			{
				ProjectSettings.Current.Save(Project.ProjectSettingsFile);
				// The editor's own write: the settings watcher must not reload it.
				projectSettingsStamp = FileStamp.Of(Project.ProjectSettingsFile);
			}
			catch (Exception e)
			{
				Debug.LogError("[Editor] Cannot save the project settings: " + e.Message);
			}
		}

		// ---------------------------------------------------------------- UI Builder and Audio Studio (own windows)

		private UIBuilder.BuilderWindow uiBuilder;
		private Audio.AudioStudioWindow audioStudio;

		/// <summary>The UI Builder window when open (test hooks).</summary>
		public UIBuilder.BuilderWindow UIBuilderWindow => uiBuilder != null && uiBuilder.IsOpen ? uiBuilder : null;

		/// <summary>The Audio Studio window when open (test hooks).</summary>
		public Audio.AudioStudioWindow AudioStudioWindow => audioStudio != null && audioStudio.IsOpen ? audioStudio : null;

		/// <summary>
		///     Opens the Audio Studio window (creating it on first use) on an audio project (null: the game's audio
		///     project) and shows an event of it when given.
		/// </summary>
		public void OpenAudioStudio(string assetPath, string eventId)
		{
			if (Project == null)
			{
				return;
			}

			if (audioStudio == null || !audioStudio.IsOpen)
			{
				audioStudio?.Dispose();
				audioStudio = new Audio.AudioStudioWindow(this, Window);
			}

			string path = assetPath ?? LELEngine.Audio.AudioProject.FindProjectPath();
			if (path != null && (audioStudio.Studio.Project == null || !string.Equals(audioStudio.Studio.AssetPath, path, StringComparison.OrdinalIgnoreCase)))
			{
				audioStudio.Studio.Open(path);
			}

			if (eventId != null)
			{
				audioStudio.Studio.ShowEvent(eventId);
			}

			audioStudio.Focus();
		}

		/// <summary>The VFX Graph window (dockable; open or not).</summary>
		public VFX.VfxGraphWindow VfxGraphWindow => vfxGraphWindow;

		/// <summary>Opens the VFX Graph window on a .vfx asset (null: the graph it last showed).</summary>
		public void OpenVfxGraph(string assetPath)
		{
			if (Project == null)
			{
				return;
			}

			vfxGraphWindow.Show(assetPath);
		}

		/// <summary>Opens the UI Builder window (creating it on first use) and loads the asset when given.</summary>
		public void OpenUIBuilder(string assetPath)
		{
			if (Project == null)
			{
				return;
			}

			if (uiBuilder == null || !uiBuilder.IsOpen)
			{
				uiBuilder?.Dispose();
				uiBuilder = new UIBuilder.BuilderWindow(this, Window);
			}

			if (assetPath != null)
			{
				if (assetPath.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)) uiBuilder.Builder.OpenStyleSheet(assetPath);
				else uiBuilder.Builder.Open(assetPath);
			}

			uiBuilder.Focus();
		}

		/// <summary>Runs the frames of the editor's other windows (after the main window's UI was drawn).</summary>
		public void RenderExtraWindows(float deltaTime)
		{
			if (uiBuilder != null)
			{
				if (!uiBuilder.IsOpen)
				{
					uiBuilder.Dispose();
					uiBuilder = null;
				}
				else
				{
					uiBuilder.Frame(deltaTime);
				}
			}

			if (audioStudio != null)
			{
				if (!audioStudio.IsOpen)
				{
					audioStudio.Dispose();
					audioStudio = null;
				}
				else
				{
					audioStudio.Frame(deltaTime);
				}
			}
		}

		public void NotifyAssetsChanged()
		{
			AssetsVersion++;
			NotifySceneChanged();
		}

		/// <summary>Drops every editor-side cache that holds script types or scene objects (before a domain unload).</summary>
		public void ClearTypeCaches()
		{
			Extensions.OnDomainUnloading();
			inspector.ClearTypeCaches();
			HierarchyPanel.ResetDrag();
			hierarchy.ForgetObjects();
			DragAndDrop.Clear();
			CollectionDrawers.Reset();
			TypeSelector.ClearCache();
			ObjectPicker.Close();
			Edits.Cancel();
			InlineEditors.Clear();
		}

		public void Dispose()
		{
			EditorPreferences.Flush();
			TextureAssets.TextureReplaced -= OnTextureReplaced;
			SceneManager.ActiveSceneChanged -= OnActiveSceneChanged;
			uiBuilder?.Dispose();
			uiBuilder = null;
			audioStudio?.Dispose();
			audioStudio = null;
			CloseProject();
			LELEngine.Audio.AudioSystem.Shutdown();
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
			Window.MouseDown -= OnWindowMouseDown;
			Window.MouseUp -= OnWindowMouseUp;
			Window.MouseWheel -= OnWindowMouseWheel;
			Window.TextInput -= OnWindowTextInput;
		}

		private void NewSceneNow()
		{
			if (IsPlaying)
			{
				ExitPlayMode();
			}

			// Sounds the old scene's objects started in edit mode (inspector previews) end with it.
			LELEngine.Audio.AudioSystem.StopAll();
			Scene scene = DefaultScene.Create(Project);
			ReplaceScene(scene, false);
			Selected = null;
			SceneDirty = false;
			sceneStamp = default;
			sceneChangedOnDisk = false;
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
				string file = AssetDatabase.ToAbsolute(assetPath);
				FileStamp stamp = FileStamp.Of(file);
				Scene scene = SceneSerializer.Load(file);
				scene.Path = assetPath;
				LELEngine.Audio.AudioSystem.StopAll();
				ReplaceScene(scene, false);
				Selected = null;
				SceneDirty = false;
				sceneStamp = stamp;
				sceneChangedOnDisk = false;
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
			// The editor's own write: the asset watcher must not reload it.
			sceneStamp = FileStamp.Of(AssetDatabase.ToAbsolute(assetPath));
			sceneChangedOnDisk = false;
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
			DrawPrefabModals();
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

			if (openSceneChangedPopup)
			{
				ImGui.OpenPopup("Scene changed on disk");
				openSceneChangedPopup = false;
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (ImGui.BeginPopupModal("Scene changed on disk", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.Text($"The scene '{Scene?.Name}' was changed outside the editor (git, Explorer),");
				ImGui.Text("and it has unsaved changes here.");
				ImGui.Spacing();
				if (ImGui.Button("Reload", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
					ReloadSceneFromDisk();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Load the file from disk; the changes made here are lost.");
				}

				ImGui.SameLine();
				if (ImGui.Button("Keep mine", new NVector2(110, 0)))
				{
					ImGui.CloseCurrentPopup();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Keep the scene as it is here; saving it overwrites the file on disk.");
				}

				ImGui.EndPopup();
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
				if (ImGui.MenuItem(Stage != null ? "Save Prefab" : "Save Scene", "Ctrl+S", false, !IsPlaying)) SaveScene();
				if (ImGui.MenuItem("Save Scene As...", "Ctrl+Shift+S", false, !IsPlaying && Stage == null)) SaveSceneAs();
				ImGui.Separator();
				if (ImGui.MenuItem("Open Script Project", "", false, Scripts != null)) Scripts.OpenInIde();
				if (ImGui.MenuItem("Rebuild Scripts", "", false, Scripts != null && !IsPlaying)) Scripts.RequestBuild();
				ImGui.Separator();
				if (ImGui.MenuItem("Build Settings...", "Ctrl+Shift+B")) ShowBuildWindow();
				ImGui.Separator();
				if (ImGui.MenuItem("Close Project")) LeaveAllStages(() => ConfirmDiscard(CloseProject));
				if (ImGui.MenuItem("Exit")) Window.Close();
				Extensions.DrawMenuItems("File");
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

				ImGui.Separator();
				if (ImGui.MenuItem("Project Settings...")) ShowProjectSettings();
				if (ImGui.MenuItem("Preferences...")) ShowPreferences();
				Extensions.DrawMenuItems("Edit");
				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("GameObject"))
			{
				DrawCreateMenu(null);
				Extensions.DrawMenuItems("GameObject");
				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Window"))
			{
				// Ticked windows are open; a closed one comes back where it was docked (layout.ini keeps its place).
				if (ImGui.MenuItem("Scene", "", SceneView.Open))
				{
					if (SceneView.Open) SceneView.Open = false;
					else SceneView.Focus();
				}

				if (ImGui.MenuItem("Game", "", GameView.Open))
				{
					if (GameView.Open) GameView.Open = false;
					else GameView.Focus();
				}

				ToggleMenuItem("Hierarchy", ref hierarchy.Open);
				ToggleMenuItem("Inspector", ref inspector.Open);
				ToggleMenuItem("Project", ref projectPanel.Open);
				ToggleMenuItem("Console", ref consolePanel.Open);
				ToggleMenuItem("Lighting", ref lightingPanel.Open);
				ImGui.Separator();
				if (ImGui.MenuItem("Project Settings", "", projectSettingsWindow.Open))
				{
					if (projectSettingsWindow.Open) projectSettingsWindow.Open = false;
					else ShowProjectSettings();
				}

				if (ImGui.MenuItem("VFX Graph", "", vfxGraphWindow.Open))
				{
					if (vfxGraphWindow.Open) vfxGraphWindow.Close();
					else OpenVfxGraph(null);
				}

				if (ImGui.MenuItem("Input Manager", "", inputManagerWindow.Open))
				{
					if (inputManagerWindow.Open) inputManagerWindow.Open = false;
					else OpenInputManager(null);
				}

				bool builderOpen = uiBuilder != null && uiBuilder.IsOpen;
				if (ImGui.MenuItem("UI Builder", "", builderOpen))
				{
					if (builderOpen) uiBuilder.Close();
					else OpenUIBuilder(null);
				}

				bool studioOpen = audioStudio != null && audioStudio.IsOpen;
				if (ImGui.MenuItem("Audio Studio", "", studioOpen))
				{
					if (studioOpen) audioStudio.Close();
					else OpenAudioStudio(null, null);
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Reset Layout"))
				{
					// Every panel open, docked as by default.
					SceneView.Open = GameView.Open = hierarchy.Open = inspector.Open = projectPanel.Open = consolePanel.Open = lightingPanel.Open = true;
					resetLayoutRequested = true;
				}

				Extensions.DrawMenuItems("Window");
				ImGui.EndMenu();
			}

			// Menus of the project's editor scripts ([MenuItem("Tools/...")]).
			Extensions.DrawExtraMenus();
			DrawPlayControls();
			DrawStatus();
			ImGui.EndMainMenuBar();
		}

		/// <summary>A Window menu entry: ticked while the window is open, a click opens or closes it.</summary>
		private static void ToggleMenuItem(string label, ref bool open)
		{
			if (ImGui.MenuItem(label, "", open))
			{
				open = !open;
			}
		}

		/// <summary>Create entries (also used by the hierarchy's context menu).</summary>
		public void DrawCreateMenu(Transform parent)
		{
			if (ImGui.MenuItem("Create Empty")) CreateObject("GameObject", null, parent);
			ImGui.Separator();
			if (ImGui.MenuItem("Cube")) CreateModelObject("Cube", DefaultScene.CubeModel, null, parent);
			if (ImGui.MenuItem("Sphere")) CreateModelObject("Sphere", DefaultScene.SphereModel, null, parent);
			if (ImGui.MenuItem("Quad")) CreateModelObject("Quad", DefaultScene.QuadModel, null, parent);
			ImGui.Separator();
			if (ImGui.BeginMenu("Navigation"))
			{
				if (ImGui.MenuItem("NavMesh Surface")) CreateObject("NavMesh Surface", go => go.AddComponent<Navigation.NavMeshSurface>(), parent);
				if (ImGui.MenuItem("NavMesh Agent"))
				{
					CreateModelObject("NavMesh Agent", DefaultScene.CubeModel, go =>
					{
						go.transform.localScale = new Vector3(0.35f, 0.9f, 0.35f);
						Navigation.NavMeshAgent agent = go.AddComponent<Navigation.NavMeshAgent>();
						agent.radius = 0.35f;
						agent.height = 1.8f;
						agent.baseOffset = 0.9f;
					}, parent);
				}

				if (ImGui.MenuItem("NavMesh Obstacle"))
				{
					CreateModelObject("NavMesh Obstacle", DefaultScene.CubeModel, go =>
					{
						Navigation.NavMeshObstacle obstacle = go.AddComponent<Navigation.NavMeshObstacle>();
						obstacle.size = new Vector3(2f, 2f, 2f);
					}, parent);
				}

				ImGui.EndMenu();
			}

			if (ImGui.BeginMenu("Physics 2D"))
			{
				if (ImGui.MenuItem("Dynamic Box (Rigidbody2D + BoxCollider2D)"))
				{
					CreateModelObject("Box 2D", DefaultScene.CubeModel, go =>
					{
						// Cube.obj spans -1..1, so the scale is the half size; the colliders fit the mesh like Unity's Reset.
						go.transform.localScale = Planar(0.5f, 0.5f, 0.25f);
						go.AddComponent<Rigidbody2D>();
						go.AddComponent<BoxCollider2D>().FitToRenderer();
					}, parent);
				}

				if (ImGui.MenuItem("Dynamic Circle (Rigidbody2D + CircleCollider2D)"))
				{
					CreateModelObject("Circle 2D", DefaultScene.SphereModel, go =>
					{
						go.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
						go.AddComponent<Rigidbody2D>();
						go.AddComponent<CircleCollider2D>().FitToRenderer();
					}, parent);
				}

				if (ImGui.MenuItem("Static Ground (BoxCollider2D)"))
				{
					CreateModelObject("Ground 2D", DefaultScene.CubeModel, go =>
					{
						go.transform.localScale = Planar(5f, 0.25f, 0.25f);
						if (go.GetComponent<MeshRenderer>() is MeshRenderer renderer)
						{
							renderer.IsStatic = true;
						}

						go.AddComponent<BoxCollider2D>().FitToRenderer();
					}, parent);
				}

				if (ImGui.MenuItem("Dynamic Capsule (Rigidbody2D + CapsuleCollider2D)"))
				{
					CreateModelObject("Capsule 2D", DefaultScene.CubeModel, go =>
					{
						go.transform.localScale = Planar(0.25f, 0.5f, 0.25f);
						go.AddComponent<Rigidbody2D>();
						go.AddComponent<CapsuleCollider2D>().FitToRenderer();
					}, parent);
				}

				if (ImGui.MenuItem("Edge Ground (EdgeCollider2D)"))
				{
					CreateObject("Edge 2D", go => go.AddComponent<EdgeCollider2D>(), parent);
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Hinge Chain (3 links)"))
				{
					CreateObject("Chain Anchor", go =>
					{
						Rigidbody2D anchor = go.AddComponent<Rigidbody2D>();
						anchor.bodyType = RigidbodyType2D.Static;
						Rigidbody2D previous = anchor;
						for (int i = 0; i < 3; i++)
						{
							GameObject link = DefaultScene.Instantiate(Scene, DefaultScene.CubeModel, "Link " + (i + 1), go.transform);
							// Links of 0.25 x 0.6 m hanging end to end; the anchor is in unscaled local space (the mesh top).
							link.transform.localPosition = Planar(0f, -0.3f - 0.6f * i, 0f);
							link.transform.localScale = Planar(0.125f, 0.3f, 0.125f);
							Rigidbody2D body = link.AddComponent<Rigidbody2D>();
							link.AddComponent<BoxCollider2D>().FitToRenderer();
							HingeJoint2D hinge = link.AddComponent<HingeJoint2D>();
							hinge.connectedBody = previous;
							hinge.anchor = new Vector2(0f, 1f);
							previous = body;
						}
					}, parent);
				}

				ImGui.EndMenu();
			}

			ImGui.Separator();
			if (ImGui.MenuItem("Visual Effect"))
			{
				CreateObject("Visual Effect", go =>
				{
					var effect = go.AddComponent<LELEngine.VFX.VisualEffect>();
					string first = AssetDatabase.FindAssets(typeof(LELEngine.VFX.VfxGraph)).Count > 0 ? AssetDatabase.FindAssets(typeof(LELEngine.VFX.VfxGraph))[0] : null;
					if (first != null)
					{
						effect.visualEffectAsset = LELEngine.VFX.VfxGraph.Load(first);
					}
				}, parent);
			}

			if (ImGui.BeginMenu("Audio"))
			{
				if (ImGui.MenuItem("Audio Listener")) CreateObject("Audio Listener", go => go.AddComponent<LELEngine.Audio.AudioListener>(), parent);
				if (ImGui.MenuItem("Audio Event Emitter")) CreateObject("Audio Emitter", go => go.AddComponent<LELEngine.Audio.AudioEventEmitter>(), parent);
				if (ImGui.MenuItem("Audio Bank Loader")) CreateObject("Audio Bank Loader", go => go.AddComponent<LELEngine.Audio.AudioBankLoader>(), parent);
				if (ImGui.MenuItem("Audio Snapshot Trigger")) CreateObject("Audio Snapshot Trigger", go => go.AddComponent<LELEngine.Audio.AudioSnapshotTrigger>(), parent);
				ImGui.EndMenu();
			}

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

		/// <summary>A vector of the 2D physics plane (x and y in the plane, depth along its normal): XY → (x, y, depth), XZ → (x, depth, y).</summary>
		private static Vector3 Planar(float x, float y, float depth)
		{
			return ProjectSettings.Current.Physics2D.SimulationPlane == SimulationPlane2D.XZ ? new Vector3(x, depth, y) : new Vector3(x, y, depth);
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
			if (vfxGraphWindow.IsFocused)
			{
				// The graph window handles save, duplicate and delete itself; undo / redo stay global.
				if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z, false))
				{
					if (shift) RedoLast();
					else UndoLast();
				}
				else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y, false)) RedoLast();
				else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.P, false)) TogglePlay();

				return;
			}

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

		/// <summary>Docks <paramref name="window" /> where <paramref name="target" /> is docked (test runs: dockwindow=).</summary>
		private static unsafe void DockInto(string window, string target)
		{
			ImGuiWindowPtr targetWindow = ImGuiP.FindWindowByName(target);
			if (targetWindow.Handle == null || targetWindow.DockId == 0)
			{
				Debug.LogWarning("[Test] Cannot dock " + window + ": " + target + " is no docked window");
				return;
			}

			ImGuiP.DockBuilderDockWindow(window, targetWindow.DockId);
			Debug.Log("[Test] Docked " + window + " with " + target);
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
				: Stage != null
					? $"LELEngine Editor - {Project.Name} - Prefab: {Stage.Name}{(SceneDirty ? " *" : "")}"
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

		/// <summary>
		///     Models new or changed on disk import completely now (materials extracted when their settings ask), not when
		///     something first uses them; a loaded one is replaced by its new import (the scene reloads, not in play mode).
		/// </summary>
		private void ImportChangedModels(bool verifyMaterials = false)
		{
			List<string> imported = ModelAssets.ImportOutdated(verifyMaterials);
			if (imported.Count == 0)
			{
				return;
			}

			List<string> loaded = imported.FindAll(ModelAssets.IsLoaded);
			if (loaded.Count > 0 && !IsPlaying)
			{
				ReloadScene(() => loaded.ForEach(ModelAssets.Unload));
				Lighting.GI.InvalidateStatic();
			}
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
			ImportChangedModels();
			AssetsVersion++;
			// Hot reload of .uss / .uxml files: the documents using them rebuild.
			UIElements.UIAssets.ReloadChanged();
			// ScriptableObject assets changed outside the editor: the loaded instances read their files again.
			ScriptableObjectAssets.ReloadChanged();
			// Prefab files changed outside the editor: the scene's instances of them are rebuilt.
			RefreshPrefabsIfChanged();
			// Audio files and the audio project changed outside: clips re-read on use, the project reloaded (not while the
			// Audio Studio holds unsaved edits of it), the banks rebuilt.
			List<string> changedClips = LELEngine.Audio.AudioClip.ReloadChanged();
			List<LELEngine.Audio.AudioProject> reloadedAudio = LELEngine.Audio.AudioProject.ReloadChanged(p => audioStudio != null && audioStudio.IsOpen && audioStudio.Studio.Project == p && audioStudio.Studio.Dirty);
			foreach (LELEngine.Audio.AudioProject reloaded in reloadedAudio)
			{
				if (audioStudio != null && audioStudio.IsOpen && audioStudio.Studio.Project == reloaded)
				{
					audioStudio.Studio.OnProjectReloaded();
				}
			}

			if (LELEngine.Audio.AudioSystem.SourceProject == null || !string.Equals(LELEngine.Audio.AudioSystem.SourceProject.AssetPath, LELEngine.Audio.AudioProject.FindProjectPath(), StringComparison.OrdinalIgnoreCase))
			{
				OpenAudioProjectForEditor();
			}
			else if (changedClips.Count > 0 || reloadedAudio.Count > 0)
			{
				LELEngine.Audio.AudioSystem.ReloadFromProject();
			}

			NotifySceneChanged();
			CheckSceneOnDisk();
		}

		private void OnSettingsChanged()
		{
			settingsChangedAt = Time.timeD;
			settingsChanged = true;
		}

		/// <summary>The open scene's file is not the one the editor read or wrote last (git, Explorer): note it for reloading.</summary>
		private void CheckSceneOnDisk()
		{
			// In play mode the scene being played is a copy; the edit scene's file is what matters.
			string path = IsPlaying ? playScenePath : Scene?.Path;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			string file = AssetDatabase.ToAbsolute(path);
			if (!File.Exists(file))
			{
				return;
			}

			FileStamp stamp = FileStamp.Of(file);
			if (stamp != sceneStamp)
			{
				sceneStamp = stamp;
				sceneChangedOnDisk = true;
			}
		}

		/// <summary>
		///     Reloads the open scene changed on disk when it has no unsaved changes (keeping the selection); with
		///     unsaved changes the user chooses. Waits for play mode to end.
		/// </summary>
		private void ReloadSceneIfChangedOnDisk()
		{
			if (!sceneChangedOnDisk || IsPlaying)
			{
				return;
			}

			sceneChangedOnDisk = false;
			if (SceneDirty)
			{
				openSceneChangedPopup = true;
				return;
			}

			ReloadSceneFromDisk();
		}

		private void ReloadSceneFromDisk()
		{
			string path = Scene?.Path;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			ulong selected = Selected?.Id ?? 0;
			OpenSceneNow(path);
			GameObject reselected = selected != 0 ? Scene?.FindObjectById(selected) : null;
			if (reselected != null)
			{
				Select(reselected);
			}

			Debug.Log("[Editor] Reloaded " + path + " (changed on disk)");
		}

		/// <summary>Project.json and ProjectSettings.asset changed outside the editor: read them again (debounced).</summary>
		private void RefreshSettingsIfChanged()
		{
			if (!settingsChanged || Time.timeD - settingsChangedAt < 0.5)
			{
				return;
			}

			settingsChanged = false;
			// Reading it again is harmless when the editor wrote it itself.
			if (File.Exists(Project.SettingsFile))
			{
				Project.ReloadSettingsFile();
			}

			string file = Project.ProjectSettingsFile;
			FileStamp stamp = FileStamp.Of(file);
			if (stamp == projectSettingsStamp || !File.Exists(file))
			{
				return;
			}

			ProjectSettings settings;
			try
			{
				settings = Documents.ReadObject(File.ReadAllBytes(file), typeof(ProjectSettings), new ProjectSettings()) as ProjectSettings;
			}
			catch (IOException)
			{
				// Still being written (a checkout): try again shortly.
				OnSettingsChanged();
				return;
			}
			catch (Exception e)
			{
				projectSettingsStamp = stamp;
				Debug.LogError("[Editor] ProjectSettings.asset changed on disk but cannot be read (" + e.Message + "); keeping the current settings");
				return;
			}

			projectSettingsStamp = stamp;
			if (settings == null)
			{
				return;
			}

			// An edit in progress belongs to the settings being replaced.
			Edits.Cancel();
			ProjectSettings.Use(settings);
			OpenAudioProjectForEditor();
			NotifySceneChanged();
			Debug.Log("[Editor] Reloaded the project settings (changed on disk)");
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
				Extensions.SaveOpenWindows(Project);
				SaveWindowStates();
				hierarchy.SaveState();
			}
		}

		// ---------------------------------------------------------------- window states

		/// <summary>
		///     Writes which built-in windows are open (closed ones too) next to the docking layout (layout.ini keeps where
		///     each one is, also while closed): the panels, Project Settings and its tab, VFX Graph and the graph it shows
		///     (per project), the UI Builder. The project's scripted windows are saved by <see cref="EditorExtensions" />.
		/// </summary>
		private void SaveWindowStates()
		{
			if (automated || Project == null)
			{
				return;
			}

			// Read first: the graphs other projects showed stay.
			WindowStates states = WindowStates.Load(WindowStatesFile) ?? new WindowStates();
			states.Open["Scene"] = SceneView.Open;
			states.Open["Game"] = GameView.Open;
			states.Open["Hierarchy"] = hierarchy.Open;
			states.Open["Inspector"] = inspector.Open;
			states.Open["Project"] = projectPanel.Open;
			states.Open["Console"] = consolePanel.Open;
			states.Open["Lighting"] = lightingPanel.Open;
			states.Open["Project Settings"] = projectSettingsWindow.Open;
			states.ProjectSettingsTab = projectSettingsWindow.CurrentTab.ToString();
			states.Open["VFX Graph"] = vfxGraphWindow.Open;
			states.Open["Input Manager"] = inputManagerWindow.Open;
			string graph = vfxGraphWindow.Document?.AssetPath;
			if (graph != null)
			{
				states.VfxGraphs[Project.RootPath] = graph;
			}

			states.Open["UI Builder"] = uiBuilder != null && uiBuilder.IsOpen;
			states.Open["Audio Studio"] = audioStudio != null && audioStudio.IsOpen;
			states.Save(WindowStatesFile);
		}

		/// <summary>Opens and closes the built-in windows as they were saved (the first frame of a project).</summary>
		private void RestoreWindowStates()
		{
			WindowStates states = WindowStates.Load(WindowStatesFile);
			if (states == null)
			{
				return;
			}

			bool Saved(string name, bool fallback) => states.Open.TryGetValue(name, out bool open) ? open : fallback;
			SceneView.Open = Saved("Scene", true);
			GameView.Open = Saved("Game", true);
			hierarchy.Open = Saved("Hierarchy", true);
			inspector.Open = Saved("Inspector", true);
			projectPanel.Open = Saved("Project", true);
			consolePanel.Open = Saved("Console", true);
			lightingPanel.Open = Saved("Lighting", true);
			inputManagerWindow.Open = Saved("Input Manager", false);
			if (Saved("Project Settings", false))
			{
				projectSettingsWindow.Restore(Enum.TryParse(states.ProjectSettingsTab, out ProjectSettingsWindow.Tab tab) ? tab : ProjectSettingsWindow.Tab.Lighting);
			}

			try
			{
				if (Saved("VFX Graph", false))
				{
					string graph = states.VfxGraphs.TryGetValue(Project.RootPath, out string path) && File.Exists(AssetDatabase.ToAbsolute(path)) ? path : null;
					OpenVfxGraph(graph);
				}

				if (Saved("UI Builder", false))
				{
					OpenUIBuilder(null);
				}

				if (Saved("Audio Studio", false))
				{
					OpenAudioStudio(null, null);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot reopen a window: " + e.Message);
			}
		}

		/// <summary>The built-in windows' open state (windows.json in the editor's data folder).</summary>
		private sealed class WindowStates
		{
			public Dictionary<string, bool> Open { get; set; } = new Dictionary<string, bool>();
			public string ProjectSettingsTab { get; set; }

			/// <summary>Per project folder: the graph the VFX Graph window showed.</summary>
			public Dictionary<string, string> VfxGraphs { get; set; } = new Dictionary<string, string>();

			public static WindowStates Load(string file)
			{
				try
				{
					WindowStates states = File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<WindowStates>(File.ReadAllText(file)) : null;
					if (states != null)
					{
						states.Open ??= new Dictionary<string, bool>();
						states.VfxGraphs ??= new Dictionary<string, string>();
					}

					return states;
				}
				catch (Exception e) when (e is IOException || e is System.Text.Json.JsonException || e is UnauthorizedAccessException)
				{
					return null;
				}
			}

			public void Save(string file)
			{
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(file));
					File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					Debug.LogWarning("[Editor] Cannot save the open windows: " + e.Message);
				}
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

		/// <summary>Game mouse coordinates are relative to the game view image, as in a standalone window.</summary>
		private void OnWindowMouseMove(MouseMoveEventArgs e)
		{
			System.Numerics.Vector2 origin = GameView.ImageOrigin;
			Input.Input_MouseMove(new MouseMoveEventArgs(e.X - origin.X, e.Y - origin.Y, e.DeltaX, e.DeltaY));
		}

		private bool GameReceivesInput => PlayState == PlayState.Playing && GameView.HasInputFocus;

		private void OnWindowMouseDown(MouseButtonEventArgs e)
		{
			if (GameReceivesInput && GameView.IsMouseOverImage)
			{
				Input.Input_MouseDown(e);
			}
		}

		private void OnWindowMouseUp(MouseButtonEventArgs e)
		{
			if (GameReceivesInput)
			{
				Input.Input_MouseUp(e);
			}
		}

		private void OnWindowMouseWheel(MouseWheelEventArgs e)
		{
			if (GameReceivesInput && GameView.IsMouseOverImage)
			{
				Input.Input_MouseWheel(e);
			}
		}

		private void OnWindowTextInput(TextInputEventArgs e)
		{
			if (GameReceivesInput)
			{
				Input.Input_TextInput(e);
			}
		}

		/// <summary>A run driven by a script (focus=0, exitframe=, screenshot=): no saved window state is used or written.</summary>
		public static bool IsAutomatedRun(string[] args)
		{
			return Array.IndexOf(args, "focus=0") >= 0
				|| Array.Exists(args, a => a.StartsWith("exitframe=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("screenshot=", StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>The project folder the command line names (project=folder or -projectPath folder), or null.</summary>
		public static string ProjectArgument(string[] args)
		{
			return GetValue(args, "project") ?? CommandLine.Parse(args).Get("projectpath");
		}

		/// <summary>The command line opens (or creates) a project itself: the project chooser is skipped (automated runs).</summary>
		public static bool OpensProjectDirectly(string[] args)
		{
			return ProjectArgument(args) != null || GetValue(args, "newproject") != null;
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

		#region NestedTypes

		/// <summary>Size and last write time of a file: a different stamp means someone else wrote it since (default: no file).</summary>
		private readonly record struct FileStamp(long Length, DateTime WrittenUtc)
		{
			public static FileStamp Of(string path)
			{
				try
				{
					var info = new FileInfo(path);
					return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : default;
				}
				catch (Exception)
				{
					return default;
				}
			}
		}

		#endregion
	}
}
