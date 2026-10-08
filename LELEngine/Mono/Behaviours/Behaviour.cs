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
	public class Behaviour
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

		public bool isActiveAndEnabled => enabledSelf && gameObject != null && gameObject.activeInHierarchy;

		#endregion

		#region InternalFields

		internal bool enabledSelf = true;
		internal bool awakeCalled;
		internal bool enableCalled;
		internal bool startCalled;
		internal bool destroyed;
		internal bool executeAlways;

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

		#endregion

		#region PublicMethods

		public T GetComponent<T>()
			where T : Behaviour
		{
			return gameObject.GetComponent<T>();
		}

		public T AddComponent<T>()
			where T : Behaviour
		{
			return gameObject.AddComponent<T>();
		}

		public Behaviour AddComponent(string component)
		{
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

		/// <summary>Destroys the GameObject (with its children and components) at the end of the frame.</summary>
		protected static void Destroy(GameObject target)
		{
			GameObject.Destroy(target);
		}

		/// <summary>Removes the component at the end of the frame (the Transform cannot be removed).</summary>
		protected static void Destroy(Behaviour target)
		{
			GameObject.Destroy(target);
		}

		/// <summary>Engine resources of the component (GPU buffers, ...); released on destroy in every mode.</summary>
		protected internal virtual void ReleaseResources()
		{ }

		#endregion
	}
}
