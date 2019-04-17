using System;
using System.Collections.Generic;

namespace LELEngine
{
	public sealed class GameObject
	{
		#region PublicFields

		public string Name { get; set; }

		public Transform transform { get; internal set; }

		public Scene scene { get; set; }

		#endregion

		#region PrivateFields

		private List<Behaviour> components = new List<Behaviour>();

		#endregion

		#region Constructors

		public GameObject(string Name, Scene scene)
		{
			this.Name = Name;
			this.scene = scene;
			AddTransform();
		}

		#endregion

		#region PublicMethods

		public T GetComponent<T>()
			where T : Behaviour
		{
			foreach (Behaviour behaviour in components)
			{
				if (behaviour is T)
				{
					return behaviour as T;
				}
			}

			return null;
		}

		public T AddComponent<T>()
			where T : Behaviour
		{
			T component = Activator.CreateInstance<T>();

			LinkComponent(component);
			return component;
		}

		public Behaviour AddComponent(string component)
		{
			Type type = Type.GetType(component);
			if (type == null)
			{
				return null;
			}

			Behaviour behaviour = Activator.CreateInstance(type) as Behaviour;
			LinkComponent(behaviour);
			return behaviour;
		}

		public Transform AddTransform()
		{
			Transform component = new Transform();

			LinkComponent(component);
			transform = component;
			return component;
		}

		public Behaviour LinkComponent(Behaviour component)
		{
			components.Add(component);
			component.gameObject = this;
			Game.Mono.InitBehaviour(component);
			return component;
		}

		#endregion
	}
}
