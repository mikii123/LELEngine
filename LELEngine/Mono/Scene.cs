using System;
using System.Collections.Generic;

namespace LELEngine
{
	/// <summary>
	///     A set of GameObjects and the owner of their lifecycle. The scene keeps every attached component, the
	///     lists the renderer needs (active renderers, main camera and light) and runs the component callbacks.
	///
	///     Lifecycle starts with <see cref="StartLifecycle" />: before that (while a scene is built in code or
	///     loaded from a file) components are only registered, so serialized values are in place when Awake runs.
	///     A playing scene calls every component; a scene in edit mode (editor) only calls components marked
	///     <see cref="ExecuteAlways" />.
	/// </summary>
	public sealed class Scene
	{
		#region PublicFields

		public string Name { get; set; }

		/// <summary>Asset path of the scene file this scene was loaded from or saved to (null when never saved).</summary>
		public string Path { get; set; }

		/// <summary>Environment, shadow and GI settings saved with the scene (live while the scene is active).</summary>
		public SceneSettings Settings { get; set; } = new SceneSettings();

		/// <summary>True when every component receives its callbacks (game, play mode); false in edit mode.</summary>
		public bool IsPlaying { get; private set; }

		/// <summary>True once <see cref="StartLifecycle" /> ran; component callbacks are deferred until then.</summary>
		public bool LifecycleStarted { get; private set; }

		/// <summary>Every GameObject of the scene, roots and children, in creation order.</summary>
		public IReadOnlyList<GameObject> GameObjects => objects;

		/// <summary>Renderers that are enabled on active objects, in the order they were added.</summary>
		public IReadOnlyList<MeshRenderer> Renderers
		{
			get
			{
				RebuildIfDirty();
				return renderers;
			}
		}

		/// <summary>Components currently receiving Update calls (awake, enabled, on active objects).</summary>
		public IReadOnlyList<Behaviour> ActiveBehaviours
		{
			get
			{
				RebuildIfDirty();
				return running;
			}
		}

		/// <summary>First enabled camera on an active object.</summary>
		public Camera MainCamera
		{
			get
			{
				RebuildIfDirty();
				return mainCamera;
			}
		}

		/// <summary>First enabled directional light on an active object.</summary>
		public DirectionalLight MainLight
		{
			get
			{
				RebuildIfDirty();
				return mainLight;
			}
		}

		/// <summary>The scene's 2D physics world (created on first use: a rigidbody or collider becoming active, a query).</summary>
		public PhysicsScene2D Physics2D => physics2D ??= new PhysicsScene2D(this);

		/// <summary>The physics world when one was created (null until then).</summary>
		public PhysicsScene2D Physics2DIfCreated => physics2D;

		#endregion

		#region PrivateFields

		/// <summary>Fixed steps per frame at most (a stalled frame does not catch up forever).</summary>
		private const int MaxFixedStepsPerFrame = 8;

		private readonly List<GameObject> objects = new List<GameObject>();
		private readonly List<Behaviour> components = new List<Behaviour>();
		private List<Behaviour> pendingStart = new List<Behaviour>();
		private readonly List<GameObject> destroyQueue = new List<GameObject>();
		private readonly List<Behaviour> componentDestroyQueue = new List<Behaviour>();

		private List<Behaviour> running = new List<Behaviour>();
		private List<MeshRenderer> renderers = new List<MeshRenderer>();
		private Camera mainCamera;
		private DirectionalLight mainLight;
		private bool dirty = true;
		private double fixedAccumulator;
		private int activationSuspended;
		private List<Behaviour> deferredActivation = new List<Behaviour>();
		private PhysicsScene2D physics2D;
		private List<Behaviour> frameActive;
		private readonly Scene[] single = new Scene[1];

		private enum Callback
		{
			Awake,
			OnEnable,
			Start,
			OnDisable,
			OnDestroy,
			OnValidate
		}

		#endregion

		#region Constructors

		public Scene()
			: this("Untitled")
		{ }

		public Scene(string name)
		{
			Name = name;
		}

		#endregion

		#region PublicMethods

		public GameObject CreateGameObject(string Name)
		{
			return new GameObject(Name, this);
		}

		public GameObject CreateGameObject(string Name, ICollection<string> components)
		{
			GameObject result = new GameObject(Name, this);
			foreach (string ob in components)
			{
				result.AddComponent(ob);
			}

			return result;
		}

		/// <summary>GameObjects without a parent, in creation order.</summary>
		public List<GameObject> GetRootGameObjects()
		{
			var roots = new List<GameObject>();
			foreach (GameObject go in objects)
			{
				if (go.transform == null || go.transform.parent == null)
				{
					roots.Add(go);
				}
			}

			return roots;
		}

		public T FindObjectOfType<T>()
			where T : Behaviour
		{
			foreach (Behaviour component in components)
			{
				if (component is T typed && !component.destroyed)
				{
					return typed;
				}
			}

			return null;
		}

		public List<T> FindObjectsOfType<T>()
			where T : Behaviour
		{
			var result = new List<T>();
			foreach (Behaviour component in components)
			{
				if (component is T typed && !component.destroyed)
				{
					result.Add(typed);
				}
			}

			return result;
		}

		public GameObject FindObjectById(ulong id)
		{
			foreach (GameObject go in objects)
			{
				if (go.Id == id)
				{
					return go;
				}
			}

			return null;
		}

		public Behaviour FindComponentById(ulong id)
		{
			foreach (Behaviour component in components)
			{
				if (component.Id == id)
				{
					return component;
				}
			}

			return null;
		}

		/// <summary>
		///     Starts the component lifecycle: Awake and OnEnable for every component that may run (all of them when
		///     <paramref name="playing" />, otherwise only <see cref="ExecuteAlways" /> ones); Start follows before
		///     the next Update.
		/// </summary>
		public void StartLifecycle(bool playing)
		{
			if (LifecycleStarted)
			{
				return;
			}

			IsPlaying = playing;
			LifecycleStarted = true;
			foreach (Behaviour component in new List<Behaviour>(components))
			{
				TryActivate(component);
			}
		}

		/// <summary>
		///     One frame of game logic: pending Start calls, FixedUpdate (fixed step), Update, LateUpdate, then the
		///     objects destroyed during the frame are removed. Scenes loaded together run their frame through
		///     <see cref="UpdateScenes" /> instead (each phase for every scene before the next phase).
		/// </summary>
		public void Update(float deltaTime)
		{
			if (!LifecycleStarted)
			{
				return;
			}

			single[0] = this;
			UpdateScenes(single, deltaTime, ref fixedAccumulator);
			single[0] = null;
		}

		/// <summary>
		///     One frame of game logic for scenes loaded together, phase by phase as in Unity: every scene's pending
		///     Start calls and UI events, then the fixed steps (each step's FixedUpdate and physics step for every scene,
		///     the fixed time advancing once), every Update, the navigation step, every LateUpdate, and last the objects
		///     destroyed during the frame. <paramref name="fixedAccumulator" /> carries the fixed time not stepped yet.
		/// </summary>
		public static void UpdateScenes(IReadOnlyList<Scene> scenes, float deltaTime, ref double fixedAccumulator)
		{
			foreach (Scene scene in scenes)
			{
				scene.BeginFrame();
			}

			// Fixed steps (FixedUpdate, then the physics step and its collision callbacks) catch up with the frame time,
			// as in Unity; a frame longer than Time.maximumDeltaTime runs a bounded number of steps.
			double step = Math.Max(0.001, Time.fixedDeltaTimeD);
			fixedAccumulator += Math.Min(deltaTime, Math.Max(step, Time.maximumDeltaTimeD));
			int steps = 0;
			while (fixedAccumulator >= step && steps < MaxFixedStepsPerFrame)
			{
				fixedAccumulator -= step;
				steps++;
				Time.inFixedTimeStep = true;
				foreach (Scene scene in scenes)
				{
					scene.FixedStep((float)step);
				}

				Time.fixedTimeD += step;
				Time.inFixedTimeStep = false;
			}

			if (steps == MaxFixedStepsPerFrame)
			{
				fixedAccumulator = 0;
			}

			float interpolation = (float)(fixedAccumulator / step);
			foreach (Scene scene in scenes)
			{
				scene.AfterFixedSteps(deltaTime, interpolation);
			}

			foreach (Scene scene in scenes)
			{
				scene.UpdatePhase();
			}

			// Navigation step (agents move, obstacles carve) between Update and LateUpdate, as in Unity.
			foreach (Scene scene in scenes)
			{
				scene.NavigationPhase(deltaTime);
			}

			foreach (Scene scene in scenes)
			{
				scene.LateUpdatePhase();
			}

			foreach (Scene scene in scenes)
			{
				scene.EndFrame();
			}
		}

		/// <summary>Destroys every object of the scene now (unloading, reloading).</summary>
		public void Unload()
		{
			foreach (GameObject root in GetRootGameObjects())
			{
				DestroyNow(root);
			}

			destroyQueue.Clear();
			componentDestroyQueue.Clear();
			pendingStart.Clear();
			physics2D?.Dispose();
			physics2D = null;
			dirty = true;
		}

		/// <summary>Calls OnValidate on a component (editor, after an inspector edit or a load; also in edit mode).</summary>
		public void Validate(Behaviour component)
		{
			Call(component, Callback.OnValidate);
		}

		#endregion

		#region InternalMethods

		internal void AddGameObject(GameObject go)
		{
			objects.Add(go);
			dirty = true;
		}

		internal void AddComponent(Behaviour component)
		{
			components.Add(component);
			dirty = true;
			if (activationSuspended > 0)
			{
				deferredActivation.Add(component);
			}
			else
			{
				TryActivate(component);
			}
		}

		/// <summary>
		///     Defers the lifecycle of components added until <see cref="ResumeActivation" /> (objects instantiated from
		///     serialized data into a running scene: Awake must see the deserialized field values).
		/// </summary>
		public void SuspendActivation()
		{
			activationSuspended++;
		}

		/// <summary>
		///     Moves a root object with its descendants into <paramref name="target" /> (SceneManager.MoveGameObjectToScene):
		///     its components get OnDisable here and OnEnable there, so physics bodies and other per-scene registrations
		///     move with them; Awake does not run again, and a pending Start runs in the target scene.
		/// </summary>
		internal void MoveRootTo(GameObject root, Scene target)
		{
			var subtree = new List<GameObject>();
			CollectSubtree(root, subtree);
			foreach (GameObject node in subtree)
			{
				foreach (Behaviour component in node.Components)
				{
					TryDeactivate(component);
				}
			}

			foreach (GameObject node in subtree)
			{
				objects.Remove(node);
				destroyQueue.Remove(node);
				foreach (Behaviour component in node.Components)
				{
					components.Remove(component);
					pendingStart.Remove(component);
					componentDestroyQueue.Remove(component);
					deferredActivation.Remove(component);
				}

				node.scene = target;
				target.objects.Add(node);
				foreach (Behaviour component in node.Components)
				{
					target.components.Add(component);
				}
			}

			dirty = true;
			target.dirty = true;
			foreach (GameObject node in subtree)
			{
				foreach (Behaviour component in node.Components)
				{
					target.TryActivate(component);
				}
			}
		}

		public void ResumeActivation()
		{
			if (activationSuspended == 0 || --activationSuspended > 0)
			{
				return;
			}

			List<Behaviour> batch = deferredActivation;
			deferredActivation = new List<Behaviour>();
			foreach (Behaviour component in batch)
			{
				TryActivate(component);
			}
		}

		internal void OnComponentStateChanged(Behaviour component)
		{
			if (component.enabledSelf)
			{
				TryActivate(component);
			}
			else
			{
				TryDeactivate(component);
			}

			dirty = true;
		}

		/// <summary>The object's active flag or its parent changed: (de)activate it and its descendants.</summary>
		internal void OnHierarchyActiveChanged(GameObject go)
		{
			var subtree = new List<GameObject>();
			CollectSubtree(go, subtree);
			foreach (GameObject node in subtree)
			{
				bool active = node.activeInHierarchy;
				foreach (Behaviour component in node.Components)
				{
					if (active)
					{
						TryActivate(component);
					}
					else
					{
						TryDeactivate(component);
					}
				}
			}

			dirty = true;
		}

		internal void QueueDestroy(GameObject go)
		{
			if (!destroyQueue.Contains(go))
			{
				destroyQueue.Add(go);
			}
		}

		internal void QueueDestroy(Behaviour component)
		{
			if (!componentDestroyQueue.Contains(component))
			{
				componentDestroyQueue.Add(component);
			}
		}

		internal void DestroyNow(GameObject go)
		{
			if (go.destroyed)
			{
				return;
			}

			var subtree = new List<GameObject>();
			CollectSubtree(go, subtree);

			foreach (GameObject node in subtree)
			{
				foreach (Behaviour component in node.Components)
				{
					TryDeactivate(component);
				}
			}

			foreach (GameObject node in subtree)
			{
				foreach (Behaviour component in node.Components)
				{
					if (component.awakeCalled && !component.destroyed)
					{
						Call(component, Callback.OnDestroy);
					}

					component.destroyed = true;
					component.ReleaseResources();
					components.Remove(component);
				}

				node.destroyed = true;
				objects.Remove(node);
			}

			go.transform?.SetParent(null, false);
			dirty = true;
		}

		internal void DestroyComponentNow(Behaviour component)
		{
			if (component.destroyed)
			{
				return;
			}

			TryDeactivate(component);
			if (component.awakeCalled)
			{
				Call(component, Callback.OnDestroy);
			}

			component.destroyed = true;
			component.ReleaseResources();
			component.gameObject?.RemoveComponentEntry(component);
			components.Remove(component);
			dirty = true;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- frame phases (UpdateScenes)

		private void BeginFrame()
		{
			if (!LifecycleStarted)
			{
				return;
			}

			RunPendingStarts();

			// UI events first: callbacks run before the components read the same input.
			if (IsPlaying)
			{
				try
				{
					UIElements.UIRuntime.ProcessInput(this);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}

		private void FixedStep(float step)
		{
			if (!LifecycleStarted)
			{
				return;
			}

			List<Behaviour> fixedBatch = ActiveBehaviours as List<Behaviour>;
			foreach (Behaviour component in fixedBatch)
			{
				if (!component.enableCalled || component.destroyed) continue;
				try
				{
					component.FixedUpdate();
				}
				catch (Exception e)
				{
					Debug.LogException(e, component);
				}
			}

			if (IsPlaying && physics2D != null && global::LELEngine.Physics2D.simulationMode == SimulationMode2D.FixedUpdate)
			{
				physics2D.Step(step);
			}
		}

		private void AfterFixedSteps(float deltaTime, float interpolation)
		{
			if (!LifecycleStarted || !IsPlaying || physics2D == null)
			{
				return;
			}

			if (global::LELEngine.Physics2D.simulationMode == SimulationMode2D.Update)
			{
				physics2D.Step(deltaTime);
			}

			physics2D.ApplyInterpolation(interpolation);
		}

		private void UpdatePhase()
		{
			// The same components get LateUpdate: one added during Update waits for its Start.
			frameActive = LifecycleStarted ? ActiveBehaviours as List<Behaviour> : null;
			if (frameActive == null)
			{
				return;
			}

			foreach (Behaviour component in frameActive)
			{
				if (!component.enableCalled || component.destroyed) continue;
				try
				{
					component.Update();
				}
				catch (Exception e)
				{
					Debug.LogException(e, component);
				}
			}
		}

		private void NavigationPhase(float deltaTime)
		{
			if (!LifecycleStarted)
			{
				return;
			}

			try
			{
				Navigation.NavMesh.Update(this, deltaTime);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
		}

		private void LateUpdatePhase()
		{
			if (frameActive == null)
			{
				return;
			}

			foreach (Behaviour component in frameActive)
			{
				if (!component.enableCalled || component.destroyed) continue;
				try
				{
					component.LateUpdate();
				}
				catch (Exception e)
				{
					Debug.LogException(e, component);
				}
			}
		}

		private void EndFrame()
		{
			frameActive = null;
			if (LifecycleStarted)
			{
				ProcessDestroyQueue();
			}
		}

		private bool CanRun(Behaviour component)
		{
			return LifecycleStarted && (IsPlaying || component.executeAlways);
		}

		private void TryActivate(Behaviour component)
		{
			dirty = true;
			if (component.destroyed || !CanRun(component) || component.gameObject == null || !component.gameObject.activeInHierarchy)
			{
				return;
			}

			if (!component.awakeCalled)
			{
				component.awakeCalled = true;
				Call(component, Callback.Awake);
			}

			if (component.enabledSelf && !component.enableCalled && !component.destroyed && component.gameObject.activeInHierarchy)
			{
				component.enableCalled = true;
				Call(component, Callback.OnEnable);
				if (!component.startCalled)
				{
					pendingStart.Add(component);
				}
			}
		}

		private void TryDeactivate(Behaviour component)
		{
			dirty = true;
			if (!component.enableCalled)
			{
				return;
			}

			component.enableCalled = false;
			Call(component, Callback.OnDisable);
		}

		private void RunPendingStarts()
		{
			while (pendingStart.Count > 0)
			{
				List<Behaviour> batch = pendingStart;
				pendingStart = new List<Behaviour>();
				foreach (Behaviour component in batch)
				{
					// Disabled again before its Start: Start runs when it is next enabled.
					if (component.destroyed || component.startCalled || !component.enableCalled)
					{
						continue;
					}

					component.startCalled = true;
					Call(component, Callback.Start);
				}
			}
		}

		private void ProcessDestroyQueue()
		{
			if (componentDestroyQueue.Count > 0)
			{
				var batch = new List<Behaviour>(componentDestroyQueue);
				componentDestroyQueue.Clear();
				foreach (Behaviour component in batch)
				{
					DestroyComponentNow(component);
				}
			}

			if (destroyQueue.Count > 0)
			{
				var batch = new List<GameObject>(destroyQueue);
				destroyQueue.Clear();
				foreach (GameObject go in batch)
				{
					DestroyNow(go);
				}
			}
		}

		private static void CollectSubtree(GameObject go, List<GameObject> result)
		{
			result.Add(go);
			if (go.transform == null)
			{
				return;
			}

			foreach (Transform child in go.transform.Children)
			{
				CollectSubtree(child.gameObject, result);
			}
		}

		private void RebuildIfDirty()
		{
			if (!dirty)
			{
				return;
			}

			dirty = false;
			var newRunning = new List<Behaviour>(components.Count);
			var newRenderers = new List<MeshRenderer>();
			Camera camera = null;
			DirectionalLight light = null;
			foreach (Behaviour component in components)
			{
				if (component.destroyed)
				{
					continue;
				}

				if (component.enableCalled)
				{
					newRunning.Add(component);
				}

				if (!component.isActiveAndEnabled)
				{
					continue;
				}

				switch (component)
				{
					case MeshRenderer renderer:
						newRenderers.Add(renderer);
						break;
					case Camera cam when camera == null:
						camera = cam;
						break;
					case DirectionalLight dir when light == null:
						light = dir;
						break;
				}
			}

			// New list instances: callers iterating the previous lists (Update loops) stay valid.
			running = newRunning;
			renderers = newRenderers;
			mainCamera = camera;
			mainLight = light;
		}

		private static void Call(Behaviour component, Callback callback)
		{
			try
			{
				switch (callback)
				{
					case Callback.Awake:
						component.Awake();
						break;
					case Callback.OnEnable:
						component.OnEnable();
						break;
					case Callback.Start:
						component.Start();
						break;
					case Callback.OnDisable:
						component.OnDisable();
						break;
					case Callback.OnDestroy:
						component.OnDestroy();
						break;
					case Callback.OnValidate:
						component.OnValidate();
						break;
				}
			}
			catch (Exception e)
			{
				Debug.LogException(e, component);
			}
		}

		#endregion
	}
}
