using System;
using System.Collections.Generic;

namespace LELEngine
{
	/// <summary>
	///     Named entity of a scene: a Transform plus any number of components. A GameObject without a scene
	///     (scene null) is detached: it is not updated, rendered or saved (editor-only helpers such as the scene
	///     view camera).
	/// </summary>
	public sealed class GameObject : EngineObject
	{
		#region PublicFields

		public string Name { get; set; }

		/// <summary>Identifier of this object in its scene file.</summary>
		public ulong Id { get; internal set; }

		public Transform transform { get; internal set; }

		public Scene scene { get; internal set; }

		public HideFlags hideFlags;

		/// <summary>
		///     Layer (0..31) the object belongs to: collision filtering and queries, and which cameras draw it
		///     (<see cref="Camera.CullingMask" />; see <see cref="LayerMask" />).
		/// </summary>
		public int layer
		{
			get => layerIndex;
			set
			{
				int clamped = Math.Clamp(value, 0, 31);
				if (layerIndex == clamped)
				{
					return;
				}

				layerIndex = clamped;
				foreach (Behaviour component in components)
				{
					component.OnLayerChanged();
				}
			}
		}

		private int layerIndex;

		/// <summary>
		///     The parent id the file gave this object when no object of the file had it (an object added under the
		///     objects of a prefab instance whose prefab is missing): written back while the object stays where it was put.
		/// </summary>
		internal ulong unresolvedParent;

		/// <summary>The object's own active flag.</summary>
		public bool activeSelf { get; private set; } = true;

		/// <summary>Active itself and all its parents.</summary>
		public bool activeInHierarchy
		{
			get
			{
				if (!activeSelf || dead)
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

		internal override string DisplayName => "'" + Name + "' (GameObject)";

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

		/// <summary>
		///     The first component of type <typeparamref name="T" />: a component class or an interface the component
		///     implements. Components destroyed on their own are skipped; on a destroyed object (until the end of its
		///     frame) its components are still found, but they are destroyed too.
		/// </summary>
		public T GetComponent<T>()
			where T : class
		{
			ThrowIfDestroyed();
			return FindComponent<T>();
		}

		/// <summary>The first component of a type (a component class or an interface), as <see cref="GetComponent{T}" />.</summary>
		public Behaviour GetComponent(Type type)
		{
			ThrowIfDestroyed();
			return FindComponent(type);
		}

		/// <summary>Every component of type <typeparamref name="T" /> (a class or an interface), in component order.</summary>
		public List<T> GetComponents<T>()
			where T : class
		{
			var result = new List<T>();
			GetComponents(result);
			return result;
		}

		/// <summary>Adds every component of type <typeparamref name="T" /> to <paramref name="results" /> (cleared first).</summary>
		public void GetComponents<T>(List<T> results)
			where T : class
		{
			ThrowIfDestroyed();
			results.Clear();
			CollectComponents(results);
		}

		/// <summary>
		///     The first component of type <typeparamref name="T" /> on this object or its descendants, depth first (this
		///     object first). Inactive objects are skipped unless <paramref name="includeInactive" />.
		/// </summary>
		public T GetComponentInChildren<T>(bool includeInactive = false)
			where T : class
		{
			ThrowIfDestroyed();
			return FindInChildren<T>(this, includeInactive, dead);
		}

		public Behaviour GetComponentInChildren(Type type, bool includeInactive = false)
		{
			ThrowIfDestroyed();
			return FindInChildren(this, type, includeInactive, dead);
		}

		/// <summary>Every component of type <typeparamref name="T" /> on this object and its descendants, depth first.</summary>
		public List<T> GetComponentsInChildren<T>(bool includeInactive = false)
			where T : class
		{
			var result = new List<T>();
			GetComponentsInChildren(includeInactive, result);
			return result;
		}

		/// <summary>Adds the components of <see cref="GetComponentsInChildren{T}(bool)" /> to <paramref name="results" /> (cleared first).</summary>
		public void GetComponentsInChildren<T>(bool includeInactive, List<T> results)
			where T : class
		{
			ThrowIfDestroyed();
			results.Clear();
			CollectInChildren(this, includeInactive, dead, results);
		}

		/// <summary>
		///     The first component of type <typeparamref name="T" /> on this object or its ancestors, nearest first.
		///     Inactive objects are skipped unless <paramref name="includeInactive" />.
		/// </summary>
		public T GetComponentInParent<T>(bool includeInactive = false)
			where T : class
		{
			ThrowIfDestroyed();
			for (GameObject go = this; go != null; go = go.transform?.parent?.gameObject)
			{
				if (Searchable(go, includeInactive, dead))
				{
					T found = go.FindComponent<T>();
					if (found != null)
					{
						return found;
					}
				}
			}

			return null;
		}

		public Behaviour GetComponentInParent(Type type, bool includeInactive = false)
		{
			ThrowIfDestroyed();
			for (GameObject go = this; go != null; go = go.transform?.parent?.gameObject)
			{
				if (Searchable(go, includeInactive, dead))
				{
					Behaviour found = go.FindComponent(type);
					if (found != null)
					{
						return found;
					}
				}
			}

			return null;
		}

		/// <summary>Every component of type <typeparamref name="T" /> on this object and its ancestors, nearest first.</summary>
		public List<T> GetComponentsInParent<T>(bool includeInactive = false)
			where T : class
		{
			ThrowIfDestroyed();
			var result = new List<T>();
			for (GameObject go = this; go != null; go = go.transform?.parent?.gameObject)
			{
				if (Searchable(go, includeInactive, dead))
				{
					go.CollectComponents(result);
				}
			}

			return result;
		}

		public T AddComponent<T>()
			where T : Behaviour
		{
			ThrowIfDestroyed();
			T component = Activator.CreateInstance<T>();
			LinkComponent(component);
			return component;
		}

		public Behaviour AddComponent(Type type)
		{
			ThrowIfDestroyed();
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
			if (dead)
			{
				// Added to a destroyed object (before the end of its frame): it goes with the object.
				component.Kill(destroyTrace);
			}

			scene?.AddComponent(component);
			return component;
		}

		public void SetActive(bool value)
		{
			ThrowIfDestroyed();
			if (activeSelf == value)
			{
				return;
			}

			activeSelf = value;
			scene?.OnHierarchyActiveChanged(this);
		}

		/// <summary>
		///     Destroys the object with its children and components: they are gone for the game at once (null in the
		///     scripts' checks, no more Update or physics callbacks); OnDisable, OnDestroy and the Destroyed events run at
		///     the end of the frame. Destroying an object twice does nothing.
		/// </summary>
		public static void Destroy(GameObject target)
		{
			if (target is null || target.dead)
			{
				return;
			}

			if (target.scene != null)
			{
				target.scene.Kill(target, CaptureDestroyTrace());
			}
			else
			{
				DestroyImmediate(target);
			}
		}

		/// <summary>Destroys the object after <paramref name="delay" /> seconds of game time (see <see cref="Destroy(GameObject)" />).</summary>
		public static void Destroy(GameObject target, float delay)
		{
			if (delay <= 0f || target?.scene == null)
			{
				Destroy(target);
			}
			else if (!target.dead)
			{
				target.scene.DestroyLater(target, delay, CaptureDestroyTrace());
			}
		}

		/// <summary>
		///     Removes a component: gone for the game at once, OnDisable and OnDestroy at the end of the frame. The
		///     Transform cannot be removed.
		/// </summary>
		public static void Destroy(Behaviour target)
		{
			if (target is null || target.dead || target is Transform)
			{
				return;
			}

			Scene scene = target.gameObject?.scene;
			if (scene != null)
			{
				scene.Kill(target, CaptureDestroyTrace());
			}
			else
			{
				DestroyImmediate(target);
			}
		}

		/// <summary>Removes the component after <paramref name="delay" /> seconds of game time.</summary>
		public static void Destroy(Behaviour target, float delay)
		{
			Scene scene = target?.gameObject?.scene;
			if (delay <= 0f || scene == null)
			{
				Destroy(target);
			}
			else if (!target.dead && target is not Transform)
			{
				scene.DestroyLater(target, delay, CaptureDestroyTrace());
			}
		}

		/// <summary>
		///     A copy of <paramref name="original" /> with its children and components (Unity's Instantiate): references
		///     between the copied objects point at the copies, others stay; a prefab instance's copy is an instance of the
		///     same prefab with the same overrides. The copy goes into the scene of <paramref name="parent" /> (else the
		///     original's); its components' Awake sees the copied values.
		/// </summary>
		public static GameObject Instantiate(GameObject original, Transform parent = null)
		{
			return Clone(original, parent, null, null);
		}

		/// <summary>A copy of <paramref name="original" /> with its root at a world position and rotation.</summary>
		public static GameObject Instantiate(GameObject original, OpenTK.Mathematics.Vector3 position, OpenTK.Mathematics.Quaternion rotation, Transform parent = null)
		{
			return Clone(original, parent, position, rotation);
		}

		/// <summary>An instance of a prefab (see <see cref="Prefab.Instantiate(Transform)" />).</summary>
		public static GameObject Instantiate(Prefab prefab, Transform parent = null)
		{
			return prefab?.Instantiate(parent);
		}

		public static GameObject Instantiate(Prefab prefab, OpenTK.Mathematics.Vector3 position, OpenTK.Mathematics.Quaternion rotation, Transform parent = null)
		{
			return prefab?.Instantiate(position, rotation, parent);
		}

		/// <summary>Destroys the object now, OnDestroy included (editor tools, scene unloading).</summary>
		public static void DestroyImmediate(GameObject target)
		{
			if (target is null || target.destroyed)
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
			if (target is null || target.destroyed || target is Transform)
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
				target.dead = true;
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

		private static GameObject Clone(GameObject original, Transform parent, OpenTK.Mathematics.Vector3? position, OpenTK.Mathematics.Quaternion? rotation)
		{
			if (!IsAlive(original))
			{
				throw new ArgumentException("Instantiate: the original is null or destroyed.", nameof(original));
			}

			Scene scene = parent?.gameObject.scene ?? original.scene ?? SceneManager.ActiveScene;
			if (scene == null)
			{
				throw new InvalidOperationException("Instantiate: there is no scene to put the copy into.");
			}

			byte[] data = Serialization.SceneSerializer.SerializeObjects(original.scene, new[] { original });
			List<GameObject> copies = Serialization.SceneSerializer.InstantiateObjects(data, scene, parent, true, roots =>
			{
				if (roots.Count == 0)
				{
					return;
				}

				if (position.HasValue)
				{
					roots[0].transform.position = position.Value;
				}

				if (rotation.HasValue)
				{
					roots[0].transform.rotation = rotation.Value;
				}
			});
			return copies.Count > 0 ? copies[0] : null;
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

			dead = true;
			destroyed = true;
			foreach (Behaviour component in components)
			{
				component.dead = true;
				component.destroyed = true;
				component.ReleaseResources();
			}
		}

		// ---------------------------------------------------------------- component lookups
		// A component destroyed on its own is skipped, unless its object is destroyed too (the object's own lookups
		// keep working until the end of its frame); searching from a destroyed object includes its destroyed subtree.

		private T FindComponent<T>()
			where T : class
		{
			foreach (Behaviour behaviour in components)
			{
				if (behaviour is T typed && (!behaviour.dead || dead))
				{
					return typed;
				}
			}

			return null;
		}

		private Behaviour FindComponent(Type type)
		{
			foreach (Behaviour behaviour in components)
			{
				if (type.IsInstanceOfType(behaviour) && (!behaviour.dead || dead))
				{
					return behaviour;
				}
			}

			return null;
		}

		private void CollectComponents<T>(List<T> results)
			where T : class
		{
			foreach (Behaviour behaviour in components)
			{
				if (behaviour is T typed && (!behaviour.dead || dead))
				{
					results.Add(typed);
				}
			}
		}

		/// <summary>Whether a lookup visits the object: alive (or the search started on a destroyed one) and active in the hierarchy.</summary>
		private static bool Searchable(GameObject go, bool includeInactive, bool searchDead)
		{
			if (go.dead && !searchDead)
			{
				return false;
			}

			if (includeInactive)
			{
				return true;
			}

			for (GameObject g = go; g != null; g = g.transform?.parent?.gameObject)
			{
				if (!g.activeSelf)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>A child is visited when alive and, unless inactive ones are included, active itself (its parent was).</summary>
		private static bool Descend(GameObject child, bool includeInactive, bool searchDead)
		{
			return (!child.dead || searchDead) && (includeInactive || child.activeSelf);
		}

		private static T FindInChildren<T>(GameObject root, bool includeInactive, bool searchDead)
			where T : class
		{
			return Searchable(root, includeInactive, searchDead) ? FindInSubtree<T>(root, includeInactive, searchDead) : null;
		}

		private static T FindInSubtree<T>(GameObject go, bool includeInactive, bool searchDead)
			where T : class
		{
			T found = go.FindComponent<T>();
			if (found != null || go.transform == null)
			{
				return found;
			}

			foreach (Transform child in go.transform.Children)
			{
				if (Descend(child.gameObject, includeInactive, searchDead))
				{
					found = FindInSubtree<T>(child.gameObject, includeInactive, searchDead);
					if (found != null)
					{
						return found;
					}
				}
			}

			return null;
		}

		private static Behaviour FindInChildren(GameObject root, Type type, bool includeInactive, bool searchDead)
		{
			return Searchable(root, includeInactive, searchDead) ? FindInSubtree(root, type, includeInactive, searchDead) : null;
		}

		private static Behaviour FindInSubtree(GameObject go, Type type, bool includeInactive, bool searchDead)
		{
			Behaviour found = go.FindComponent(type);
			if (found != null || go.transform == null)
			{
				return found;
			}

			foreach (Transform child in go.transform.Children)
			{
				if (Descend(child.gameObject, includeInactive, searchDead))
				{
					found = FindInSubtree(child.gameObject, type, includeInactive, searchDead);
					if (found != null)
					{
						return found;
					}
				}
			}

			return null;
		}

		private static void CollectInChildren<T>(GameObject root, bool includeInactive, bool searchDead, List<T> results)
			where T : class
		{
			if (!Searchable(root, includeInactive, searchDead))
			{
				return;
			}

			CollectSubtree(root, includeInactive, searchDead, results);
		}

		private static void CollectSubtree<T>(GameObject go, bool includeInactive, bool searchDead, List<T> results)
			where T : class
		{
			go.CollectComponents(results);
			if (go.transform == null)
			{
				return;
			}

			foreach (Transform child in go.transform.Children)
			{
				if (Descend(child.gameObject, includeInactive, searchDead))
				{
					CollectSubtree(child.gameObject, includeInactive, searchDead, results);
				}
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
