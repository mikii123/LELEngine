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

		#endregion

		#region PrivateFields

		/// <summary>Fixed update step in seconds.</summary>
		private const float FixedStep = 0.02f;

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
		private float fixedTime;
		private int activationSuspended;
		private List<Behaviour> deferredActivation = new List<Behaviour>();

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
		///     objects destroyed during the frame are removed.
		/// </summary>
		public void Update(float deltaTime)
		{
			if (!LifecycleStarted)
			{
				return;
			}

			RunPendingStarts();

			List<Behaviour> active = ActiveBehaviours as List<Behaviour>;
			fixedTime += deltaTime;
			if (fixedTime >= FixedStep)
			{
				foreach (Behaviour component in active)
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

				fixedTime = 0f;
			}

			foreach (Behaviour component in active)
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

			foreach (Behaviour component in active)
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

			ProcessDestroyQueue();
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
