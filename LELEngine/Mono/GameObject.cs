using System;
using System.Collections.Generic;

namespace LELEngine
{
	/// <summary>
	///     Named entity of a scene: a Transform plus any number of components. A GameObject without a scene
	///     (scene null) is detached: it is not updated, rendered or saved (editor-only helpers such as the scene
	///     view camera).
	/// </summary>
	public sealed class GameObject
	{
		#region PublicFields

		public string Name { get; set; }

		/// <summary>Identifier of this object in its scene file.</summary>
		public ulong Id { get; internal set; }

		public Transform transform { get; internal set; }

		public Scene scene { get; internal set; }

		public HideFlags hideFlags;

		/// <summary>The object's own active flag.</summary>
		public bool activeSelf { get; private set; } = true;

		/// <summary>Active itself and all its parents.</summary>
		public bool activeInHierarchy
		{
			get
			{
				if (!activeSelf || destroyed)
				{
					return false;
				}

				Transform parent = transform?.parent;
				return parent == null || parent.gameObject.activeInHierarchy;
			}
		}

		public IReadOnlyList<Behaviour> Components => components;

		#endregion

		#region InternalFields

		internal bool destroyed;

		#endregion

		#region PrivateFields

		private readonly List<Behaviour> components = new List<Behaviour>();

		#endregion

		#region Constructors

		public GameObject(string Name, Scene scene)
			: this(Name, scene, 0)
		{ }

		internal GameObject(string name, Scene scene, ulong id)
		{
			this.Name = name;
			Id = id != 0 ? id : SceneIds.Next();
			this.scene = scene;
			scene?.AddGameObject(this);
			AddTransform();
		}

		#endregion

		#region PublicMethods

		public T GetComponent<T>()
			where T : Behaviour
		{
			foreach (Behaviour behaviour in components)
			{
				if (behaviour is T typed && !behaviour.destroyed)
				{
					return typed;
				}
			}

			return null;
		}

		public Behaviour GetComponent(Type type)
		{
			foreach (Behaviour behaviour in components)
			{
				if (type.IsInstanceOfType(behaviour) && !behaviour.destroyed)
				{
					return behaviour;
				}
			}

			return null;
		}

		public List<T> GetComponents<T>()
			where T : Behaviour
		{
			var result = new List<T>();
			foreach (Behaviour behaviour in components)
			{
				if (behaviour is T typed && !behaviour.destroyed)
				{
					result.Add(typed);
				}
			}

			return result;
		}

		public T AddComponent<T>()
			where T : Behaviour
		{
			T component = Activator.CreateInstance<T>();
			LinkComponent(component);
			return component;
		}

		public Behaviour AddComponent(Type type)
		{
			if (type == null || !typeof(Behaviour).IsAssignableFrom(type) || type.IsAbstract)
			{
				return null;
			}

			Behaviour behaviour = (Behaviour)Activator.CreateInstance(type);
			LinkComponent(behaviour);
			return behaviour;
		}

		/// <summary>Adds a component by type name (namespace-qualified, optionally with ", Assembly").</summary>
		public Behaviour AddComponent(string component)
		{
			return AddComponent(TypeRegistry.Resolve(component));
		}

		public Transform AddTransform()
		{
			Transform component = new Transform();

			LinkComponent(component);
			transform = component;
			return component;
		}

		/// <summary>
		///     Attaches an existing component instance (used by AddComponent and by scene loading, which sets the
		///     serialized fields first and runs the lifecycle afterwards).
		/// </summary>
		public Behaviour LinkComponent(Behaviour component)
		{
			return LinkComponent(component, 0);
		}

		internal Behaviour LinkComponent(Behaviour component, ulong id)
		{
			if (component is Transform && transform != null)
			{
				throw new InvalidOperationException("A GameObject has exactly one Transform.");
			}

			components.Add(component);
			component.gameObject = this;
			component.Id = id != 0 ? id : SceneIds.Next();
			component.executeAlways = component.GetType().IsDefined(typeof(ExecuteAlways), true);
			scene?.AddComponent(component);
			return component;
		}

		public void SetActive(bool value)
		{
			if (activeSelf == value)
			{
				return;
			}

			activeSelf = value;
			scene?.OnHierarchyActiveChanged(this);
		}

		/// <summary>Destroys the object, its children and components at the end of the frame.</summary>
		public static void Destroy(GameObject target)
		{
			if (target == null || target.destroyed)
			{
				return;
			}

			if (target.scene != null)
			{
				target.scene.QueueDestroy(target);
			}
			else
			{
				DestroyImmediate(target);
			}
		}

		/// <summary>Removes a component at the end of the frame. The Transform cannot be removed.</summary>
		public static void Destroy(Behaviour target)
		{
			if (target == null || target.destroyed || target is Transform)
			{
				return;
			}

			Scene scene = target.gameObject?.scene;
			if (scene != null)
			{
				scene.QueueDestroy(target);
			}
			else
			{
				DestroyImmediate(target);
			}
		}

		/// <summary>Destroys the object now (editor tools, scene unloading).</summary>
		public static void DestroyImmediate(GameObject target)
		{
			if (target == null || target.destroyed)
			{
				return;
			}

			if (target.scene != null)
			{
				target.scene.DestroyNow(target);
			}
			else
			{
				target.DestroyDetached();
			}
		}

		public static void DestroyImmediate(Behaviour target)
		{
			if (target == null || target.destroyed || target is Transform)
			{
				return;
			}

			Scene scene = target.gameObject?.scene;
			if (scene != null)
			{
				scene.DestroyComponentNow(target);
			}
			else
			{
				target.destroyed = true;
				target.ReleaseResources();
				target.gameObject?.RemoveComponentEntry(target);
			}
		}

		public override string ToString()
		{
			return Name;
		}

		#endregion

		#region InternalMethods

		internal void RemoveComponentEntry(Behaviour component)
		{
			components.Remove(component);
		}

		private void DestroyDetached()
		{
			if (transform != null)
			{
				foreach (Transform child in new List<Transform>(transform.Children))
				{
					DestroyImmediate(child.gameObject);
				}
				transform.SetParent(null, false);
			}

			destroyed = true;
			foreach (Behaviour component in components)
			{
				component.destroyed = true;
				component.ReleaseResources();
			}
		}

		#endregion
	}

	/// <summary>Random 64-bit identifiers for scene objects (unique within a scene for practical purposes).</summary>
	internal static class SceneIds
	{
		public static ulong Next()
		{
			ulong id;
			do
			{
				id = (ulong)Random.Shared.NextInt64(long.MinValue, long.MaxValue);
			}
			while (id == 0);

			return id;
		}
	}
}
