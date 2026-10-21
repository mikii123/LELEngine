using System.Collections.Generic;

namespace LELEngine
{
	/// <summary>
	///     Base class of every component (engine components and game scripts).
	///     Lifecycle, as in Unity: Awake once when the component first becomes active, OnEnable / OnDisable whenever
	///     it becomes active and enabled / stops being so, Start once before its first Update, OnDestroy when it is
	///     destroyed (only after Awake). While the scene is not playing (editor edit mode) only components marked
	///     <see cref="ExecuteAlways" /> receive these callbacks; the scene still registers every component, so
	///     cameras, lights and renderers work in edit mode. Exceptions thrown by callbacks are logged, not rethrown.
	/// </summary>
	public class Behaviour : EngineObject
	{
		#region PublicFields

		public Transform transform => gameObject?.transform;
		public GameObject gameObject { get; internal set; }

		/// <summary>Identifier of this component in its scene file (references between components use it).</summary>
		public ulong Id { get; internal set; }

		/// <summary>Enabled components receive Update calls and OnEnable / OnDisable.</summary>
		public bool enabled
		{
			get => enabledSelf;
			set
			{
				if (enabledSelf == value)
				{
					return;
				}

				enabledSelf = value;
				gameObject?.scene?.OnComponentStateChanged(this);
			}
		}

		public bool isActiveAndEnabled => enabledSelf && !dead && gameObject != null && gameObject.activeInHierarchy;

		#endregion

		#region InternalFields

		internal bool enabledSelf = true;
		internal bool awakeCalled;
		internal bool enableCalled;
		internal bool startCalled;
		internal bool executeAlways;

		internal override string DisplayName => "'" + (gameObject?.Name ?? "?") + "' (" + GetType().Name + ")";

		#endregion

		#region UnityMethods

		public virtual void Awake()
		{ }

		public virtual void OnEnable()
		{ }

		public virtual void Start()
		{ }

		public virtual void Update()
		{ }

		public virtual void FixedUpdate()
		{ }

		/// <summary>Called after every Update of the frame.</summary>
		public virtual void LateUpdate()
		{ }

		public virtual void OnDisable()
		{ }

		/// <summary>Called when the component (or its GameObject) is destroyed, if Awake was called.</summary>
		public virtual void OnDestroy()
		{ }

		/// <summary>Editor: called after a serialized field was changed in the inspector or loaded.</summary>
		public virtual void OnValidate()
		{ }

		/// <summary>Editor: called when the component is added in the editor, to fit its defaults to the object.</summary>
		public virtual void Reset()
		{ }

		// 2D physics (see Physics2D): called on every component of both objects after a physics step.

		/// <summary>A collider of this object started touching another (non-trigger) collider.</summary>
		public virtual void OnCollisionEnter2D(Collision2D collision)
		{ }

		/// <summary>Called every physics step while the colliders touch.</summary>
		public virtual void OnCollisionStay2D(Collision2D collision)
		{ }

		public virtual void OnCollisionExit2D(Collision2D collision)
		{ }

		/// <summary>A trigger collider of this object (or the other one) started overlapping the other collider.</summary>
		public virtual void OnTriggerEnter2D(Collider2D other)
		{ }

		public virtual void OnTriggerStay2D(Collider2D other)
		{ }

		public virtual void OnTriggerExit2D(Collider2D other)
		{ }

		/// <summary>A joint of this object broke (its reaction force or torque exceeded the break limit); the joint component is destroyed.</summary>
		public virtual void OnJointBreak2D(Joint2D joint)
		{ }

		#endregion

		#region PublicMethods

		public T GetComponent<T>()
			where T : Behaviour
		{
			ThrowIfDestroyed();
			return gameObject.GetComponent<T>();
		}

		public T AddComponent<T>()
			where T : Behaviour
		{
			ThrowIfDestroyed();
			return gameObject.AddComponent<T>();
		}

		public Behaviour AddComponent(string component)
		{
			ThrowIfDestroyed();
			return gameObject.AddComponent(component);
		}

		public GameObject Instantiate(string Name)
		{
			return gameObject.scene.CreateGameObject(Name);
		}

		public GameObject Instantiate(string Name, ICollection<string> Components)
		{
			return gameObject.scene.CreateGameObject(Name, Components);
		}

		/// <summary>
		///     Internal Method.
		///     Override at your own risk.
		///     Called in sync with MeshRenderer component.
		/// </summary>
		public virtual void Render()
		{ }

		public virtual void PostRender()
		{ }

		#endregion

		#region ProtectedMethods

		/// <summary>
		///     Destroys the GameObject with its children and components: gone for the game at once, OnDestroy at the end
		///     of the frame (see <see cref="EngineObject" />).
		/// </summary>
		protected static void Destroy(GameObject target)
		{
			GameObject.Destroy(target);
		}

		/// <summary>Destroys the GameObject after <paramref name="delay" /> seconds of game time.</summary>
		protected static void Destroy(GameObject target, float delay)
		{
			GameObject.Destroy(target, delay);
		}

		/// <summary>Removes the component: gone for the game at once, OnDestroy at the end of the frame (not the Transform).</summary>
		protected static void Destroy(Behaviour target)
		{
			GameObject.Destroy(target);
		}

		/// <summary>Removes the component after <paramref name="delay" /> seconds of game time.</summary>
		protected static void Destroy(Behaviour target, float delay)
		{
			GameObject.Destroy(target, delay);
		}

		/// <summary>An instance of a prefab (under <paramref name="parent" />, else in the active scene).</summary>
		protected static GameObject Instantiate(Prefab prefab, Transform parent = null)
		{
			return GameObject.Instantiate(prefab, parent);
		}

		protected static GameObject Instantiate(Prefab prefab, OpenTK.Mathematics.Vector3 position, OpenTK.Mathematics.Quaternion rotation, Transform parent = null)
		{
			return GameObject.Instantiate(prefab, position, rotation, parent);
		}

		/// <summary>A copy of a GameObject with its children and components (see <see cref="GameObject.Instantiate(GameObject, Transform)" />).</summary>
		protected static GameObject Instantiate(GameObject original, Transform parent = null)
		{
			return GameObject.Instantiate(original, parent);
		}

		protected static GameObject Instantiate(GameObject original, OpenTK.Mathematics.Vector3 position, OpenTK.Mathematics.Quaternion rotation, Transform parent = null)
		{
			return GameObject.Instantiate(original, position, rotation, parent);
		}

		/// <summary>A copy of a component's GameObject; returns the copy's component that corresponds to <paramref name="original" />.</summary>
		protected static T Instantiate<T>(T original, Transform parent = null)
			where T : Behaviour
		{
			GameObject copy = GameObject.Instantiate(original.gameObject, parent);
			int index = 0;
			foreach (Behaviour component in original.gameObject.Components)
			{
				if (component == original)
				{
					break;
				}

				index++;
			}

			return copy != null && index < copy.Components.Count ? copy.Components[index] as T : null;
		}

		/// <summary>Engine resources of the component (GPU buffers, ...); released on destroy in every mode.</summary>
		protected internal virtual void ReleaseResources()
		{ }

		/// <summary>The GameObject's layer changed (physics shapes refilter).</summary>
		protected internal virtual void OnLayerChanged()
		{ }

		#endregion
	}
}
