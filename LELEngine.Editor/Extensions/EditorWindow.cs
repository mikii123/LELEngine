using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     A window of the editor written in the project's editor scripts (an Editor folder), drawn with Dear ImGui
	///     (Hexa.NET.ImGui) in <see cref="OnGUI" />: it docks like the editor's own panels. Open it with
	///     <see cref="GetWindow{T}" />, usually from a <see cref="MenuItemAttribute" /> method.
	///     <para>
	///         Open windows survive script reloads and editor restarts: before the scripts reload, the serialized fields
	///         of every window (public, or private with [SerializeField]) are saved, <see cref="OnDisable" /> runs, and the
	///         new instance gets the values back before its <see cref="OnEnable" />. <see cref="OnDestroy" /> runs only
	///         when the window is closed.
	///     </para>
	/// </summary>
	public abstract class EditorWindow
	{
		#region PrivateFields

		private string windowTitle;

		#endregion

		#region InternalFields

		/// <summary>Stable ImGui id of the window (type name, "#n" for further instances): keeps its dock place.</summary>
		internal string Key;

		internal bool IsOpen;
		internal bool FocusRequested;

		/// <summary>Message of the last exception (logged once per message, shown in the window).</summary>
		internal string LastError;

		internal string TitleOverride => windowTitle;

		#endregion

		#region PublicFields

		/// <summary>Text of the window's tab (default: the class name, spaced).</summary>
		public string title
		{
			get => windowTitle ?? SerializationUtility.NicifyName(GetType().Name);
			set => windowTitle = value;
		}

		/// <summary>Size the window opens with the first time (afterwards the layout remembers it).</summary>
		public NVector2 defaultSize { get; set; } = new NVector2(420f, 320f);

		/// <summary>Smallest size the window can be resized to (zero: no limit).</summary>
		public NVector2 minSize { get; set; }

		/// <summary>ImGui flags of the window (e.g. <see cref="ImGuiWindowFlags.MenuBar" />).</summary>
		public ImGuiWindowFlags windowFlags { get; set; }

		/// <summary>The window (or one of its child windows) has the keyboard focus.</summary>
		public bool hasFocus { get; internal set; }

		#endregion

		#region PublicMethods

		/// <summary>The open window of type <typeparamref name="T" />, opened when there is none.</summary>
		/// <param name="title">Tab text (null: keep the window's own).</param>
		/// <param name="focus">Bring the window to the front.</param>
		public static T GetWindow<T>(string title = null, bool focus = true) where T : EditorWindow, new()
		{
			return EditorExtensions.Current.GetWindow<T>(title, focus);
		}

		/// <summary>A new window of type <typeparamref name="T" />, even when one is open already.</summary>
		public static T CreateWindow<T>(string title = null) where T : EditorWindow, new()
		{
			return EditorExtensions.Current.CreateWindow<T>(title);
		}

		public static bool HasOpenInstances<T>() where T : EditorWindow
		{
			return EditorExtensions.Current.HasOpenInstances(typeof(T));
		}

		/// <summary>Opens the window (an instance created with new).</summary>
		public void Show()
		{
			EditorExtensions.Current.OpenWindow(this, null);
		}

		/// <summary>Brings the window to the front on the next frame.</summary>
		public void Focus()
		{
			FocusRequested = true;
		}

		/// <summary>Closes the window: <see cref="OnDisable" />, then <see cref="OnDestroy" />.</summary>
		public void Close()
		{
			EditorExtensions.Current.CloseWindow(this);
		}

		/// <summary>
		///     An image of this window, <paramref name="width" /> x <paramref name="height" /> pixels (0: its size on screen).
		///     At its size on screen it is the editor's last frame; at another size (and in batch mode, where nothing else is
		///     drawn) the window is laid out again off screen at that size. Call it between frames (Update, a menu item,
		///     automation code), not from <see cref="OnGUI" />. See <see cref="EditorAutomation.GetScreenshot" />.
		/// </summary>
		public EditorScreenshot GetScreenshot(int width = 0, int height = 0)
		{
			return EditorExtensions.Current.PanelOf(this).GetScreenshot(width, height);
		}

		#endregion

		#region ProtectedMethods

		/// <summary>Draws the window's content (inside its ImGui window, every frame while it is visible).</summary>
		protected virtual void OnGUI()
		{
		}

		/// <summary>Called every editor frame while the window is open, visible or not.</summary>
		protected virtual void Update()
		{
		}

		protected virtual void OnEnable()
		{
		}

		protected virtual void OnDisable()
		{
		}

		protected virtual void OnDestroy()
		{
		}

		/// <summary>The selected GameObject (or project file) changed.</summary>
		protected virtual void OnSelectionChange()
		{
		}

		#endregion

		#region InternalMethods

		internal void InvokeGUI()
		{
			OnGUI();
		}

		internal void InvokeUpdate()
		{
			Update();
		}

		internal void InvokeEnable()
		{
			OnEnable();
		}

		internal void InvokeDisable()
		{
			OnDisable();
		}

		internal void InvokeDestroy()
		{
			OnDestroy();
		}

		internal void InvokeSelectionChange()
		{
			OnSelectionChange();
		}

		#endregion
	}

	/// <summary>
	///     Adds a static, parameterless method of the editor scripts to the editor's main menu (Unity's [MenuItem]):
	///     "Window/My Tool" appends to the Window menu, "Tools/Generate/Level" creates a Tools menu with a submenu.
	///     Items are sorted by <see cref="priority" />; a gap of more than 10 starts a new group (a separator).
	/// </summary>
	[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
	public sealed class MenuItemAttribute : System.Attribute
	{
		#region Constructors

		public MenuItemAttribute(string itemName, int priority = 1000)
		{
			menuItem = itemName;
			this.priority = priority;
		}

		#endregion

		#region PublicFields

		public string menuItem { get; }
		public int priority { get; }

		#endregion
	}
}
