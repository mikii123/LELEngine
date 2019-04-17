using System.Collections.Generic;

namespace LELEngine
{
	public class Behaviour
	{
		#region PublicFields

		public Transform transform => gameObject.transform;
		public GameObject gameObject { get; internal set; }

		#endregion

		#region UnityMethods

		// Executable methods
		public virtual void Awake()
		{ }

		public virtual void Start()
		{ }

		public virtual void Update()
		{ }

		public virtual void FixedUpdate()
		{ }

		/// <summary>
		///     Internal Method.
		///     Override at your own risk.
		///     Called in sync with Transform component.
		/// </summary>
		public virtual void LateUpdate()
		{ }

		#endregion

		#region PublicMethods

		public T GetComponent<T>()
			where T : Behaviour, new()
		{
			return gameObject.GetComponent<T>();
		}

		public T AddComponent<T>()
			where T : Behaviour, new()
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
	}
}
