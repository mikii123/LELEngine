using System;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>
	///     A data object that lives in the project as an asset (Unity's ScriptableObject): derive from it and add
	///     serialized fields, then create assets with the Project panel's Create menu (see
	///     <see cref="CreateAssetMenuAttribute" />) or instances in code with <see cref="CreateInstance{T}" />.
	///     <para>
	///         An asset is an ".asset" document (readable JSON in the project, binary in builds) loaded once and shared:
	///         every field referencing it (a field of its type, of an interface it implements, or of
	///         <see cref="EngineObject" />) gets the same instance. Builds include it when a scene uses it or it lies in
	///         a Resources folder (<see cref="Resources.Load{T}" />).
	///     </para>
	///     <para>
	///         Lifecycle: <see cref="Awake" /> and <see cref="OnEnable" /> once it is created or loaded (its fields are
	///         read by then; on the thread that loads it, but a background load, <see cref="Resources.LoadAsync{T}" />
	///         or <see cref="SceneManager.LoadSceneAsync" />, holds them back for the main thread: they run when it
	///         completes, or when a load on the main thread meets the asset first), <see cref="OnDisable" /> before the scripts
	///         reload or the asset is unloaded, <see cref="OnDestroy" /> when it is destroyed. In the editor,
	///         <see cref="OnValidate" /> runs after an edit in the inspector and <see cref="Reset" /> when the editor
	///         creates the asset. Exceptions of these callbacks are logged.
	///     </para>
	/// </summary>
	public abstract class ScriptableObject : EngineObject, IAsset
	{
		#region PublicFields

		/// <summary>Name of the object: the asset's file name without extension for assets.</summary>
		public string name { get; set; } = "";

		/// <summary>The ".asset" file it was loaded from or saved to (root-relative), null for an instance made in code.</summary>
		public string AssetPath { get; internal set; }

		#endregion

		#region InternalFields

		internal bool enabledState;

		/// <summary>Read by a background load: Awake and OnEnable wait for the main thread (<see cref="ScriptableObjectAssets.EnableHeld" />).</summary>
		internal bool heldBack;

		internal override string DisplayName => "'" + (string.IsNullOrEmpty(name) ? GetType().Name : name) + "' (" + GetType().Name + ")";

		#endregion

		#region UnityMethods

		/// <summary>Called once the instance was created or loaded (after its fields were read).</summary>
		public virtual void Awake()
		{ }

		/// <summary>Called after <see cref="Awake" />, and again after a script reload.</summary>
		public virtual void OnEnable()
		{ }

		/// <summary>Called before the scripts reload, the asset is unloaded or the object is destroyed.</summary>
		public virtual void OnDisable()
		{ }

		/// <summary>Called when the object is destroyed (<see cref="Destroy" />).</summary>
		public virtual void OnDestroy()
		{ }

		/// <summary>Editor: called after a serialized field was changed in the inspector.</summary>
		public virtual void OnValidate()
		{ }

		/// <summary>Editor: called when the editor creates the asset, to set its defaults.</summary>
		public virtual void Reset()
		{ }

		#endregion

		#region PublicMethods

		/// <summary>A new instance (not an asset): Awake and OnEnable run before it is returned.</summary>
		public static T CreateInstance<T>()
			where T : ScriptableObject
		{
			return (T)CreateInstance(typeof(T));
		}

		/// <summary>A new instance of a ScriptableObject type (not an asset): Awake and OnEnable run before it is returned.</summary>
		public static ScriptableObject CreateInstance(Type type)
		{
			ScriptableObject instance = CreateUninitialized(type);
			instance.name = "";
			instance.Enable(true);
			return instance;
		}

		/// <summary>A new instance of the type with this name ("Namespace.Type" or "Namespace.Type, Assembly").</summary>
		public static ScriptableObject CreateInstance(string typeName)
		{
			Type type = TypeRegistry.Resolve(typeName);
			if (type == null)
			{
				throw new ArgumentException("No ScriptableObject type named '" + typeName + "'", nameof(typeName));
			}

			return CreateInstance(type);
		}

		/// <summary>A copy of the object's serialized state as a new instance (not an asset); references stay shared.</summary>
		public static T Instantiate<T>(T original)
			where T : ScriptableObject
		{
			if (original is null)
			{
				throw new ArgumentNullException(nameof(original));
			}

			original.ThrowIfDestroyed();
			ScriptableObject copy = CreateUninitialized(original.GetType());
			Documents.ReadObject(Documents.WriteObject(original, DocumentFormat.Binary), original.GetType(), copy);
			copy.name = original.name + " (Clone)";
			copy.Enable(true);
			return (T)copy;
		}

		/// <summary>
		///     Destroys the object: OnDisable and OnDestroy run now and it reads as null for the game scripts from then
		///     on. A loaded asset is dropped from the asset cache (the next load reads the file again).
		/// </summary>
		public static void Destroy(ScriptableObject target)
		{
			if (target is null || target.dead)
			{
				return;
			}

			target.Kill(CaptureDestroyTrace());
			target.Disable();
			target.Guard(target.OnDestroy, nameof(OnDestroy));
			target.destroyCallbacksRun = true;
			target.destroyed = true;
			target.RaiseDestroyed();
			if (target.AssetPath != null)
			{
				ScriptableObjectAssets.Forget(target);
			}
		}

		public override string ToString()
		{
			return string.IsNullOrEmpty(name) ? GetType().Name : name;
		}

		#endregion

		#region InternalMethods

		/// <summary>An instance without its callbacks (loaders read the fields first).</summary>
		internal static ScriptableObject CreateUninitialized(Type type)
		{
			if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type) || type.IsAbstract)
			{
				throw new ArgumentException((type?.FullName ?? "null") + " is not a concrete ScriptableObject type", nameof(type));
			}

			return (ScriptableObject)Activator.CreateInstance(type, true);
		}

		/// <summary>Runs Awake (when <paramref name="awake" />) and OnEnable unless it is enabled already.</summary>
		internal void Enable(bool awake)
		{
			if (enabledState || dead)
			{
				return;
			}

			if (awake)
			{
				Guard(Awake, nameof(Awake));
			}

			enabledState = true;
			Guard(OnEnable, nameof(OnEnable));
		}

		internal void Disable()
		{
			if (!enabledState)
			{
				return;
			}

			enabledState = false;
			Guard(OnDisable, nameof(OnDisable));
		}

		internal void Validate()
		{
			Guard(OnValidate, nameof(OnValidate));
		}

		private void Guard(Action callback, string name)
		{
			try
			{
				callback();
			}
			catch (Exception e)
			{
				Debug.LogError($"[ScriptableObject] {DisplayName}.{name}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
			}
		}

		#endregion
	}

	/// <summary>
	///     Lists a ScriptableObject type in the editor's Create menu (Project panel, right click > Create): the menu
	///     item writes a new asset of the type into the current folder.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, Inherited = false)]
	public sealed class CreateAssetMenuAttribute : Attribute
	{
		/// <summary>File name of new assets (default: "New " + the type name).</summary>
		public string fileName { get; set; }

		/// <summary>Path of the menu item under Create ("Game/Weapon"); default: the type name.</summary>
		public string menuName { get; set; }

		/// <summary>Position among the Create menu's items (lower first).</summary>
		public int order { get; set; }
	}
}
