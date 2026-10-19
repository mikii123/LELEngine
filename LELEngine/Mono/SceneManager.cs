using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LELEngine.Rendering;
using LELEngine.Shaders;

namespace LELEngine
{
	/// <summary>When an activated scene starts its component lifecycle; set by the host.</summary>
	public enum SceneLifecycle
	{
		/// <summary>The host does not run yet: activated scenes start when it does (<see cref="SceneManager.StartLifecycle" />).</summary>
		Deferred,

		/// <summary>The editor's edit mode: only [ExecuteAlways] components run.</summary>
		Edit,

		/// <summary>The game runs: every component runs.</summary>
		Play
	}

	/// <summary>
	///     The loaded scenes (updated and drawn together) and the active one (its lighting is live; new objects of the
	///     game usually go there), and how scenes load and unload:
	///     <list type="bullet">
	///         <item><see cref="LoadScene" /> loads and activates a scene now.</item>
	///         <item>
	///             <see cref="LoadSceneAsync" /> loads it in the background (<see cref="SceneLoadOperation" />): read on a
	///             loading thread, prepared for the GPU a few milliseconds per frame, then activated, at once or when the
	///             game allows it (loaded but not activated meanwhile: nothing of it runs or is drawn).
	///         </item>
	///         <item>
	///             <see cref="UnloadScene" /> destroys a scene's objects (OnDisable, OnDestroy) and frees the GPU memory of
	///             the textures and meshes no other scene uses (<see cref="ReleaseUnusedAssets" />).
	///         </item>
	///         <item>
	///             <see cref="LoadSceneMode.Single" /> replaces the loaded scenes, but for the objects moved to the
	///             DontDestroyOnLoad scene (<see cref="DontDestroyOnLoad" />); <see cref="LoadSceneMode.Additive" /> adds.
	///         </item>
	///     </list>
	///     Hosts call <see cref="Update" /> once per frame (background loads, activation) and <see cref="UpdateScenes" />
	///     for the game logic of the loaded scenes. Call it all from the main thread. Each scene keeps its own 2D physics
	///     world; queries go to the active scene's.
	/// </summary>
	public static class SceneManager
	{
		#region PublicFields

		/// <summary>The active scene: its lighting and environment are live, <see cref="Camera.main" /> looks there first.</summary>
		public static Scene ActiveScene { get; private set; }

		/// <summary>The loaded (activated) scenes, updated and drawn together, in activation order.</summary>
		public static IReadOnlyList<Scene> LoadedScenes => loaded;

		/// <summary>Background loads not done yet (reading, preparing, or loaded and waiting for their activation), in request order.</summary>
		public static IReadOnlyList<SceneLoadOperation> PendingLoads => operations;

		/// <summary>When activated scenes start their lifecycle (set by the host: the player when its game loop starts, the editor's play mode).</summary>
		public static SceneLifecycle Lifecycle { get; set; }

		/// <summary>Milliseconds of main-thread work per frame for background loads (GPU preparation: shader compiles, uploads); at least one asset per frame.</summary>
		public static float BackgroundLoadingBudget { get; set; } = 4f;

		/// <summary>
		///     Unloading a scene frees the GPU memory of its textures and meshes when no other loaded or loading scene
		///     uses them, and drops them and its materials from the asset caches (the editor turns it off: its views and
		///     inspectors keep using the assets).
		/// </summary>
		public static bool ReleaseUnusedAssets { get; set; } = true;

		/// <summary>A scene was activated: it is now one of <see cref="LoadedScenes" /> (its Awake and OnEnable ran when the lifecycle runs).</summary>
		public static event Action<Scene, LoadSceneMode> SceneLoaded;

		/// <summary>A scene was unloaded (its objects are destroyed).</summary>
		public static event Action<Scene> SceneUnloaded;

		/// <summary>The active scene changed (previous, new; either may be null).</summary>
		public static event Action<Scene, Scene> ActiveSceneChanged;

		/// <summary>The first enabled camera of the active scene, or of the other loaded scenes.</summary>
		public static Camera MainCamera
		{
			get
			{
				Camera camera = ActiveScene?.MainCamera;
				if (camera != null)
				{
					return camera;
				}

				foreach (Scene scene in loaded)
				{
					if (scene.MainCamera != null)
					{
						return scene.MainCamera;
					}
				}

				return null;
			}
		}

		/// <summary>The first enabled directional light of the active scene, or of the other loaded scenes.</summary>
		public static DirectionalLight MainLight
		{
			get
			{
				DirectionalLight light = ActiveScene?.MainLight;
				if (light != null)
				{
					return light;
				}

				foreach (Scene scene in loaded)
				{
					if (scene.MainLight != null)
					{
						return scene.MainLight;
					}
				}

				return null;
			}
		}

		/// <summary>The scene of the objects kept across Single loads (<see cref="DontDestroyOnLoad" />); created on first use.</summary>
		public static Scene DontDestroyOnLoadScene
		{
			get
			{
				if (dontDestroyScene == null)
				{
					dontDestroyScene = new Scene("DontDestroyOnLoad");
					AddLoaded(dontDestroyScene);
					StartIfRunning(dontDestroyScene);
				}

				return dontDestroyScene;
			}
		}

		#endregion

		#region PrivateFields

		private static readonly List<Scene> loaded = new List<Scene>();
		private static Scene[] loadedSnapshot = Array.Empty<Scene>();
		private static readonly List<SceneLoadOperation> operations = new List<SceneLoadOperation>();
		private static readonly Dictionary<Scene, IReadOnlyList<object>> sceneAssets = new Dictionary<Scene, IReadOnlyList<object>>();
		private static readonly List<object> releaseCandidates = new List<object>();
		private static readonly List<MeshRenderer> combinedRenderers = new List<MeshRenderer>();
		private static readonly List<Behaviour> combinedBehaviours = new List<Behaviour>();
		private static Scene dontDestroyScene;
		private static double fixedAccumulator;

		// The loading thread reads the requested scenes in order.
		private static readonly BlockingCollection<SceneLoadOperation> readQueue = new BlockingCollection<SceneLoadOperation>();
		private static Thread loadingThread;
		private static volatile SceneLoadOperation reading;

		#endregion

		#region PublicMethods

		public static Scene CreateScene(string name)
		{
			return new Scene(name);
		}

		/// <summary>
		///     Makes the scene active; its settings become the live lighting / GI settings. A scene that is not loaded
		///     yet joins the loaded scenes (a scene built in code); its lifecycle is the caller's to start. Null: no
		///     active scene.
		/// </summary>
		public static void SetActiveScene(Scene scene)
		{
			if (scene != null && !loaded.Contains(scene))
			{
				AddLoaded(scene);
			}

			SetActive(scene);
		}

		/// <summary>
		///     Loads a scene file (asset path or absolute path) and activates it now, on this thread: Single unloads the
		///     other loaded scenes (but DontDestroyOnLoad) and makes it active, Additive adds it. Its lifecycle starts when
		///     the host runs (<see cref="Lifecycle" />). Null when the file cannot be read (the error is logged). Called
		///     from a script, the frame's remaining callbacks of an unloaded scene's objects are skipped.
		/// </summary>
		public static Scene LoadScene(string path, LoadSceneMode mode = LoadSceneMode.Single)
		{
			SceneLoadOperation operation = CreateOperation(path, mode, true);
			operation.Read();
			if (operation.State == SceneLoadOperation.LoadState.Failed)
			{
				Debug.LogError($"[SceneManager] Cannot load {path}: {operation.Error?.Message}");
				operation.MarkFailed();
				return null;
			}

			// Without the GL context here (a loading thread of the host) the GPU work happens on first use.
			if (GraphicsThread.HasContext)
			{
				operation.Prepare(long.MaxValue, IsGpuDriven());
			}

			Activate(operation);
			return operation.Scene;
		}

		/// <summary>
		///     Starts loading a scene in the background (see <see cref="SceneLoadOperation" />). With
		///     <paramref name="allowActivation" /> false the scene stays loaded but not activated until the operation is
		///     allowed to activate; scenes activate in the order they were requested.
		/// </summary>
		public static SceneLoadOperation LoadSceneAsync(string path, LoadSceneMode mode = LoadSceneMode.Single, bool allowActivation = true)
		{
			SceneLoadOperation operation = CreateOperation(path, mode, allowActivation);
			operations.Add(operation);
			EnsureLoadingThread();
			readQueue.Add(operation);
			return operation;
		}

		/// <summary>
		///     Unloads a scene now: its objects are destroyed (OnDisable, OnDestroy; nothing runs for a scene loaded but
		///     not activated), and the GPU memory of the assets no other scene uses is freed. The next loaded scene
		///     becomes active when it was the active one.
		/// </summary>
		public static void UnloadScene(Scene scene)
		{
			if (scene == null)
			{
				return;
			}

			foreach (SceneLoadOperation operation in operations)
			{
				if (operation.Scene == scene && !operation.IsDone)
				{
					operation.Cancel();
					DiscardPending(operation);
					operations.Remove(operation);
					ReleaseAssets();
					return;
				}
			}

			if (!loaded.Contains(scene))
			{
				return;
			}

			UnloadLoaded(scene);
			ReleaseAssets();
		}

		/// <summary>Unloads the loaded scene of this asset path (the first one, when it is loaded twice).</summary>
		public static void UnloadScene(string path)
		{
			UnloadScene(GetSceneByPath(path));
		}

		/// <summary>The loaded scene of an asset path, or null.</summary>
		public static Scene GetSceneByPath(string path)
		{
			string resolved = ResolvePath(path);
			foreach (Scene scene in loaded)
			{
				if (scene.Path != null && (string.Equals(scene.Path, path, StringComparison.OrdinalIgnoreCase) || string.Equals(scene.Path, resolved, StringComparison.OrdinalIgnoreCase)))
				{
					return scene;
				}
			}

			return null;
		}

		/// <summary>
		///     Moves a root object (a child moves with its root detached) with its descendants into another loaded scene.
		///     Its components get OnDisable and OnEnable around the move (physics bodies and other per-scene
		///     registrations move along); Awake does not run again.
		/// </summary>
		public static void MoveGameObjectToScene(GameObject go, Scene scene)
		{
			if (go == null || scene == null || go.scene == scene || go.destroyed)
			{
				return;
			}

			if (go.transform?.parent != null)
			{
				go.transform.SetParent(null, true);
			}

			if (go.scene != null)
			{
				go.scene.MoveRootTo(go, scene);
			}
		}

		/// <summary>Keeps an object (its root, with every descendant) across Single loads: it moves to the DontDestroyOnLoad scene.</summary>
		public static void DontDestroyOnLoad(GameObject go)
		{
			if (go?.transform == null)
			{
				return;
			}

			MoveGameObjectToScene(go.transform.root.gameObject, DontDestroyOnLoadScene);
		}

		// ---------------------------------------------------------------- hosts

		/// <summary>
		///     Once per frame on the main thread, before the game logic: prepares the scenes read in the background for
		///     the GPU (within <see cref="BackgroundLoadingBudget" />) and activates the loaded ones that may activate, in
		///     request order.
		/// </summary>
		public static void Update()
		{
			if (operations.Count == 0)
			{
				return;
			}

			long deadline = Stopwatch.GetTimestamp() + (long)(Math.Max(0f, BackgroundLoadingBudget) * Stopwatch.Frequency / 1000.0);
			bool gpuDriven = IsGpuDriven();
			bool blocked = false;
			for (int i = 0; i < operations.Count; i++)
			{
				SceneLoadOperation operation = operations[i];
				switch (operation.State)
				{
					case SceneLoadOperation.LoadState.Failed:
						Debug.LogError($"[SceneManager] Cannot load {operation.Path}: {operation.Error?.Message}");
						operations.RemoveAt(i--);
						operation.MarkFailed();
						continue;
					case SceneLoadOperation.LoadState.Preparing:
						if (operation.CancelRequested)
						{
							DiscardPending(operation);
							operations.RemoveAt(i--);
							ReleaseAssets();
							continue;
						}

						if (Stopwatch.GetTimestamp() < deadline || i == 0)
						{
							operation.Prepare(deadline, gpuDriven);
						}

						break;
				}

				if (operation.State != SceneLoadOperation.LoadState.Loaded)
				{
					// Later scenes may prepare meanwhile but not activate before this one.
					blocked = true;
					continue;
				}

				if (operation.CancelRequested)
				{
					DiscardPending(operation);
					operations.RemoveAt(i--);
					ReleaseAssets();
					continue;
				}

				if (!blocked && operation.AllowActivation)
				{
					operations.RemoveAt(i--);
					Activate(operation);
					continue;
				}

				blocked = true;
			}
		}

		/// <summary>One frame of game logic for every loaded scene, phase by phase (<see cref="Scene.UpdateScenes" />).</summary>
		public static void UpdateScenes(float deltaTime)
		{
			Scene.UpdateScenes(loadedSnapshot, deltaTime, ref fixedAccumulator);
		}

		/// <summary>The host starts running: the loaded scenes start their lifecycle (play mode, or the editor's edit mode).</summary>
		public static void StartLifecycle(bool playing)
		{
			Lifecycle = playing ? SceneLifecycle.Play : SceneLifecycle.Edit;
			foreach (Scene scene in loadedSnapshot)
			{
				scene.StartLifecycle(playing);
			}
		}

		/// <summary>
		///     What the loaded scenes draw this frame: the renderers and behaviours of every loaded scene (one scene's own
		///     lists when only one is loaded). The lists are valid until the next call.
		/// </summary>
		public static void GetRenderLists(out IReadOnlyList<MeshRenderer> renderers, out IReadOnlyList<Behaviour> behaviours)
		{
			if (loadedSnapshot.Length == 1)
			{
				renderers = loadedSnapshot[0].Renderers;
				behaviours = loadedSnapshot[0].ActiveBehaviours;
				return;
			}

			combinedRenderers.Clear();
			combinedBehaviours.Clear();
			foreach (Scene scene in loadedSnapshot)
			{
				combinedRenderers.AddRange(scene.Renderers);
				combinedBehaviours.AddRange(scene.ActiveBehaviours);
			}

			renderers = combinedRenderers;
			behaviours = combinedBehaviours;
		}

		/// <summary>
		///     Unloads every scene, loaded or loading (the editor replacing its scene, the end of play mode, the host
		///     quitting); no asset is released. <paramref name="waitForReading" />: a scene being read on the loading
		///     thread is waited for (the editor before it reloads the game scripts the scene's components belong to).
		/// </summary>
		public static void UnloadAll(bool waitForReading = false)
		{
			foreach (SceneLoadOperation operation in operations)
			{
				operation.Cancel();
				if (operation.State == SceneLoadOperation.LoadState.Preparing || operation.State == SceneLoadOperation.LoadState.Loaded)
				{
					DiscardPending(operation);
				}
			}

			if (waitForReading)
			{
				while (reading != null)
				{
					Thread.Sleep(1);
				}
			}

			foreach (SceneLoadOperation operation in operations)
			{
				if (operation.State == SceneLoadOperation.LoadState.Preparing || operation.State == SceneLoadOperation.LoadState.Loaded)
				{
					DiscardPending(operation);
				}
			}

			operations.Clear();
			foreach (Scene scene in loadedSnapshot)
			{
				scene.Unload();
				sceneAssets.Remove(scene);
				SceneUnloaded?.Invoke(scene);
			}

			loaded.Clear();
			loadedSnapshot = Array.Empty<Scene>();
			dontDestroyScene = null;
			sceneAssets.Clear();
			releaseCandidates.Clear();
			fixedAccumulator = 0;
			SetActive(null);
		}

		#endregion

		#region InternalMethods

		/// <summary>The GPU work of one asset of a scene being prepared (main thread).</summary>
		internal static void PrepareAsset(object asset, bool gpuDriven)
		{
			try
			{
				switch (asset)
				{
					case ShaderProgram program:
						program.EnsureBuilt();
						if (gpuDriven && program.SupportsInstancing && program.IsLinked)
						{
							program.GetInstancedVariant();
						}

						break;
					case Texture texture:
						_ = texture.Handle;
						break;
					case Sprite sprite:
						_ = sprite.texture?.Handle;
						break;
					case Mesh mesh:
						GeometryPool.Prepare(mesh);
						break;
					case Model model:
						foreach (Mesh mesh in model.Meshes)
						{
							GeometryPool.Prepare(mesh);
						}

						break;
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[SceneManager] Cannot prepare {asset}: {e.Message}");
			}
		}

		#endregion

		#region PrivateMethods

		private static SceneLoadOperation CreateOperation(string path, LoadSceneMode mode, bool allowActivation)
		{
			string absolute = Path.IsPathRooted(path) ? path : AssetDatabase.ToAbsolute(ResolvePath(path));
			return new SceneLoadOperation(path, absolute, mode, allowActivation);
		}

		private static string ResolvePath(string path)
		{
			return path == null || Path.IsPathRooted(path) ? path : AssetDatabase.Resolve(path, null) ?? path;
		}

		private static bool IsGpuDriven()
		{
			Renderer renderer = Game.Renderer;
			return renderer != null && renderer.Settings.GpuDriven && GpuScene.IsSupported;
		}

		private static void EnsureLoadingThread()
		{
			if (loadingThread != null)
			{
				return;
			}

			loadingThread = new Thread(ReadLoop) { Name = "Scene loader", IsBackground = true };
			loadingThread.Start();
		}

		private static void ReadLoop()
		{
			foreach (SceneLoadOperation operation in readQueue.GetConsumingEnumerable())
			{
				if (operation.CancelRequested)
				{
					continue;
				}

				reading = operation;
				try
				{
					operation.Read();
				}
				finally
				{
					reading = null;
				}
			}
		}

		private static void Activate(SceneLoadOperation operation)
		{
			long start = Stopwatch.GetTimestamp();
			Scene scene = operation.Scene;
			if (operation.Mode == LoadSceneMode.Single)
			{
				// The assets the new scene shares with the old ones stay loaded: they are released after it joined.
				bool replaced = false;
				foreach (Scene old in loadedSnapshot)
				{
					if (old != dontDestroyScene)
					{
						UnloadLoaded(old);
						replaced = true;
					}
				}

				// The GI caches (distance fields, surface cards) belong to the old scenes' renderers.
				if (replaced)
				{
					Game.Renderer?.InvalidateSceneCaches();
				}
			}

			sceneAssets[scene] = operation.Assets;
			AddLoaded(scene);
			if (operation.Mode == LoadSceneMode.Single || ActiveScene == null || ActiveScene == dontDestroyScene)
			{
				SetActive(scene);
			}

			StartIfRunning(scene);
			ReleaseAssets();
			operation.MarkActivated(Stopwatch.GetTimestamp() - start);
			Console.WriteLine($"[SceneManager] {scene.Path ?? scene.Name}: {operation.Summary}");
			SceneLoaded?.Invoke(scene, operation.Mode);
		}

		private static void StartIfRunning(Scene scene)
		{
			if (Lifecycle != SceneLifecycle.Deferred)
			{
				scene.StartLifecycle(Lifecycle == SceneLifecycle.Play);
			}
		}

		/// <summary>A scene read (and maybe prepared) but not activated is dropped: nothing of it ran, its objects go.</summary>
		private static void DiscardPending(SceneLoadOperation operation)
		{
			if (operation.Scene != null)
			{
				operation.Scene.Unload();
				releaseCandidates.AddRange(operation.Assets);
			}

			operation.MarkCancelled();
		}

		private static void UnloadLoaded(Scene scene)
		{
			scene.Unload();
			loaded.Remove(scene);
			loadedSnapshot = loaded.ToArray();
			if (sceneAssets.TryGetValue(scene, out IReadOnlyList<object> assets))
			{
				releaseCandidates.AddRange(assets);
				sceneAssets.Remove(scene);
			}

			if (scene == dontDestroyScene)
			{
				dontDestroyScene = null;
			}

			if (ActiveScene == scene)
			{
				Scene next = null;
				foreach (Scene candidate in loaded)
				{
					if (candidate != dontDestroyScene)
					{
						next = candidate;
						break;
					}
				}

				SetActive(next ?? (loaded.Count > 0 ? loaded[0] : null));
			}

			SceneUnloaded?.Invoke(scene);
		}

		private static void AddLoaded(Scene scene)
		{
			loaded.Add(scene);
			loadedSnapshot = loaded.ToArray();
		}

		private static void SetActive(Scene scene)
		{
			Scene previous = ActiveScene;
			ActiveScene = scene;
			scene?.Settings.Activate();
			if (previous != scene)
			{
				ActiveSceneChanged?.Invoke(previous, scene);
			}
		}

		/// <summary>
		///     Frees what the unloaded scenes used and no loaded or loading scene uses: the GPU memory of textures and
		///     meshes, and their cache entries with the materials' (the objects stay usable: used again, they upload again).
		/// </summary>
		private static void ReleaseAssets()
		{
			if (releaseCandidates.Count == 0)
			{
				return;
			}

			if (!ReleaseUnusedAssets)
			{
				releaseCandidates.Clear();
				return;
			}

			var used = new HashSet<object>(ReferenceEqualityComparer.Instance);
			foreach (IReadOnlyList<object> assets in sceneAssets.Values)
			{
				used.UnionWith(assets);
			}

			foreach (SceneLoadOperation operation in operations)
			{
				if (operation.State >= SceneLoadOperation.LoadState.Preparing && operation.State <= SceneLoadOperation.LoadState.Loaded)
				{
					used.UnionWith(operation.Assets);
				}
			}

			int textures = 0;
			int meshes = 0;
			var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (object asset in releaseCandidates)
			{
				if (used.Contains(asset))
				{
					continue;
				}

				switch (asset)
				{
					case Texture texture:
						TextureAssets.Release(texture);
						textures++;
						break;
					case Mesh mesh:
						GeometryPool.Release(mesh);
						if (mesh.AssetPath != null)
						{
							models.Add(mesh.AssetPath);
						}

						meshes++;
						break;
					case Material material when material.AssetPath != null:
						InternalStorage.ForgetMaterial(material.AssetPath);
						break;
				}
			}

			// A model's cache entry goes when none of its meshes is used any more.
			foreach (string model in models)
			{
				bool inUse = false;
				foreach (object asset in used)
				{
					if (asset is Mesh mesh && string.Equals(mesh.AssetPath, model, StringComparison.OrdinalIgnoreCase))
					{
						inUse = true;
						break;
					}
				}

				if (!inUse)
				{
					ModelAssets.Unload(model);
				}
			}

			releaseCandidates.Clear();
			if (textures + meshes > 0)
			{
				Console.WriteLine($"[SceneManager] Released {textures} textures and {meshes} meshes no loaded scene uses");
			}
		}

		#endregion
	}
}
