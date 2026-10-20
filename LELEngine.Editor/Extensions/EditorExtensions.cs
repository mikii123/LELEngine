using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Hosts what the project's editor scripts (Game.Editor.dll) add to the editor: custom inspectors
	///     (<see cref="Editor" />), windows (<see cref="EditorWindow" />) and main menu items
	///     (<see cref="MenuItemAttribute" />). Everything that holds a type of the editor scripts is dropped before the
	///     script context unloads; open windows are saved (type, dock id, serialized fields) and come back after the
	///     reload, and across editor restarts (Library/EditorWindows.txt).
	///     <para>
	///         User code runs guarded: an exception is logged once per message, the ImGui stacks it left open are
	///         closed, and the rest of the editor keeps drawing.
	///     </para>
	/// </summary>
	internal sealed class EditorExtensions
	{
		#region PrivateFields

		private static readonly string[] BuiltInMenus = { "File", "Edit", "GameObject", "Window" };
		private const string WindowsFile = "EditorWindows.txt";

		private readonly EditorApplication editor;
		private Assembly assembly;

		// Custom inspectors: registered editor per inspected type, resolution per component type, live instances.
		private readonly Dictionary<Type, (Type editorType, bool forChildClasses)> editorTypes = new Dictionary<Type, (Type, bool)>();
		private readonly Dictionary<Type, Type> resolvedEditors = new Dictionary<Type, Type>();
		private readonly Dictionary<Behaviour, Editor> liveEditors = new Dictionary<Behaviour, Editor>();

		// Main menu items (top-level name -> tree) and the items clicked this frame (run after the menu bar).
		private readonly List<MenuNode> menus = new List<MenuNode>();
		private readonly List<MethodInfo> pendingMenuItems = new List<MethodInfo>();

		// Windows: open instances, saved states waiting for their types.
		private readonly List<EditorWindow> windows = new List<EditorWindow>();
		private readonly List<WindowState> savedWindows = new List<WindowState>();
		private bool windowsCaptured;
		private readonly InspectorContext windowContext;
		private GameObject lastSelected;
		private string lastSelectedAsset;

		// Undo.RecordObject edits waiting for the active ImGui item to be released; components to validate.
		private bool recordPending;
		private readonly HashSet<Behaviour> changedComponents = new HashSet<Behaviour>();

		// Last exception message per (object, callback): callbacks running every frame log a repeated error once.
		private readonly Dictionary<(object owner, string callback), string> lastErrors = new Dictionary<(object, string), string>();

		#endregion

		#region Constructors

		public EditorExtensions(EditorApplication editor)
		{
			this.editor = editor;
			windowContext = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicFields

		public static EditorExtensions Current => EditorApplication.Instance?.Extensions ?? throw new InvalidOperationException("The LELEngine editor is not running.");

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- domain

		/// <summary>Registers the custom editors and menu items of freshly loaded editor scripts (null: none).</summary>
		public void OnDomainLoaded(Assembly editorAssembly)
		{
			assembly = editorAssembly;
			if (assembly == null)
			{
				return;
			}

			Type[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException e)
			{
				types = e.Types.Where(t => t != null).ToArray();
			}

			var menuItems = new List<(string path, int priority, MethodInfo method)>();
			foreach (Type type in types)
			{
				if (typeof(Editor).IsAssignableFrom(type))
				{
					RegisterEditor(type);
				}

				foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					foreach (MenuItemAttribute item in method.GetCustomAttributes<MenuItemAttribute>(false))
					{
						if (method.GetParameters().Length != 0 || method.ContainsGenericParameters || string.IsNullOrWhiteSpace(item.menuItem))
						{
							Debug.LogWarning($"[Editor] [MenuItem(\"{item.menuItem}\")] on {type.Name}.{method.Name}: menu items must be static methods without parameters.");
							continue;
						}

						menuItems.Add((item.menuItem, item.priority, method));
					}
				}
			}

			BuildMenus(menuItems);
		}

		/// <summary>
		///     Saves the open windows (unless <see cref="CaptureWindowStates" /> did while the scene was still loaded),
		///     disables every window and custom editor, and forgets every type of the editor scripts.
		/// </summary>
		public void OnDomainUnloading()
		{
			if (!windowsCaptured)
			{
				CaptureWindowStates();
			}

			windowsCaptured = false;
			foreach (EditorWindow window in windows.ToArray())
			{
				window.IsOpen = false;
				Guard(window, w => w.InvokeDisable(), "OnDisable");
			}

			windows.Clear();
			DisposeEditors(_ => true);
			editorTypes.Clear();
			resolvedEditors.Clear();
			menus.Clear();
			pendingMenuItems.Clear();
			changedComponents.Clear();
			lastErrors.Clear();
			recordPending = false;
			lastSelected = null;
			windowContext.Release();
			assembly = null;
		}

		/// <summary>
		///     Saves the serialized fields of the open windows now, while the scene is loaded (their references to scene
		///     objects need it); the next unload uses these states.
		/// </summary>
		public void CaptureWindowStates()
		{
			var states = new List<WindowState>();
			foreach (EditorWindow window in windows)
			{
				states.Add(new WindowState
				{
					TypeName = window.GetType().FullName,
					Key = window.Key,
					Title = window.TitleOverride,
					State = Capture(window)
				});
			}

			// States not restored yet (their type did not load) stay for a later load.
			foreach (WindowState pending in savedWindows)
			{
				if (!states.Any(s => s.Key == pending.Key))
				{
					states.Add(pending);
				}
			}

			savedWindows.Clear();
			savedWindows.AddRange(states);
			windowsCaptured = true;
		}

		/// <summary>Reads the windows that were open when the project was last closed.</summary>
		public void LoadOpenWindows(ProjectInfo project)
		{
			savedWindows.Clear();
			string file = Path.Combine(project.LibraryPath, WindowsFile);
			if (!File.Exists(file))
			{
				return;
			}

			try
			{
				foreach (string line in File.ReadAllLines(file))
				{
					string[] parts = line.Split('\t');
					if (parts.Length != 4 || parts[0].Length == 0 || parts[1].Length == 0)
					{
						continue;
					}

					savedWindows.Add(new WindowState
					{
						Key = parts[0],
						TypeName = parts[1],
						Title = parts[2].Length > 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])) : null,
						State = parts[3].Length > 0 ? Convert.FromBase64String(parts[3]) : null
					});
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot read the open editor windows: " + e.Message);
				savedWindows.Clear();
			}
		}

		/// <summary>Writes the open windows (and the ones still waiting for their scripts) to the project's Library.</summary>
		public void SaveOpenWindows(ProjectInfo project)
		{
			var pending = new List<WindowState>(savedWindows);
			CaptureWindowStates();
			windowsCaptured = false;
			try
			{
				var builder = new StringBuilder();
				foreach (WindowState state in savedWindows)
				{
					builder.Append(state.Key).Append('\t').Append(state.TypeName).Append('\t')
						.Append(state.Title != null ? Convert.ToBase64String(Encoding.UTF8.GetBytes(state.Title)) : "").Append('\t')
						.Append(state.State != null ? Convert.ToBase64String(state.State) : "").Append('\n');
				}

				Directory.CreateDirectory(project.LibraryPath);
				File.WriteAllText(Path.Combine(project.LibraryPath, WindowsFile), builder.ToString());
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot save the open editor windows: " + e.Message);
			}

			// The open windows stay open; only the states still waiting for their scripts are kept.
			savedWindows.Clear();
			savedWindows.AddRange(pending);
		}

		/// <summary>The project closed: nothing waits to be restored.</summary>
		public void Reset()
		{
			savedWindows.Clear();
			windowsCaptured = false;
		}

		// ---------------------------------------------------------------- frame

		/// <summary>Runs clicked menu items, restores saved windows, then updates and draws every open window.</summary>
		public void DrawWindows()
		{
			windowsCaptured = false;
			if (editor.Project == null)
			{
				return;
			}

			if (assembly != null && savedWindows.Count > 0)
			{
				RestoreWindows();
			}

			RunPendingMenuItems();
			if (editor.Selected != lastSelected || editor.SelectedAsset != lastSelectedAsset)
			{
				lastSelected = editor.Selected;
				lastSelectedAsset = editor.SelectedAsset;
				foreach (EditorWindow window in windows.ToArray())
				{
					Guard(window, w => w.InvokeSelectionChange(), "OnSelectionChange");
				}
			}

			foreach (EditorWindow window in windows.ToArray())
			{
				if (window.IsOpen)
				{
					Guard(window, w => w.InvokeUpdate(), "Update");
				}
			}

			foreach (EditorWindow window in windows.ToArray())
			{
				if (window.IsOpen)
				{
					DrawWindow(window);
				}
			}
		}

		/// <summary>
		///     After the UI of the frame: ends <see cref="Undo.RecordObject" /> edits whose widget was released, marks the
		///     changed components and drops the custom editors of components that are no longer selected.
		/// </summary>
		public void EndFrame()
		{
			if (recordPending && !ImGui.IsAnyItemActive())
			{
				recordPending = false;
				editor.Edits.RequestCommit();
			}

			if (changedComponents.Count > 0)
			{
				foreach (Behaviour component in changedComponents)
				{
					if (component.gameObject != null)
					{
						editor.NotifyChanged(component.gameObject);
						editor.Scene?.Validate(component);
					}
				}

				changedComponents.Clear();
			}

			// The inspector may be locked on another object than the selection: its editors stay.
			GameObject selected = editor.InspectedObject;
			if (liveEditors.Count > 0)
			{
				DisposeEditors(component => selected == null || component.gameObject != selected || !selected.Components.Contains(component));
			}
		}

		// ---------------------------------------------------------------- inspectors

		/// <summary>The custom editor of the component (created on first use), or null when its type has none.</summary>
		public Editor GetEditor(Behaviour component)
		{
			if (assembly == null || component == null)
			{
				return null;
			}

			Type editorType = ResolveEditorType(component.GetType());
			if (editorType == null)
			{
				return null;
			}

			if (liveEditors.TryGetValue(component, out Editor existing) && existing.GetType() == editorType)
			{
				return existing;
			}

			Editor created;
			try
			{
				created = (Editor)Activator.CreateInstance(editorType);
			}
			catch (Exception e)
			{
				Debug.LogError($"[Editor] Cannot create the custom editor {editorType.Name}: {e.GetBaseException().Message}");
				// Drawn without the custom editor from now on (until the scripts reload).
				resolvedEditors[component.GetType()] = null;
				return null;
			}

			created.target = component;
			liveEditors[component] = created;
			Guard(created, e => e.InvokeEnable(), "OnEnable");
			return created;
		}

		/// <summary>Draws a custom editor's inspector; true when a value of its target changed.</summary>
		public bool DrawInspector(Editor custom, InspectorContext context)
		{
			// The last error first: what the failing code draws may hide anything below it.
			if (custom.LastError != null)
			{
				EditorGUILayout.HelpBox(custom.GetType().Name + ".OnInspectorGUI: " + custom.LastError + " (see the Console)", MessageType.Error);
			}

			EditorGUILayout.Begin(context, custom.target);
			var state = default(ImGuiErrorRecoveryState);
			ImGuiP.ErrorRecoveryStoreState(ref state);
			try
			{
				custom.OnInspectorGUI();
				CheckBalanced(ref state, custom.GetType().Name + ".OnInspectorGUI");
				custom.LastError = null;
			}
			catch (Exception e)
			{
				Recover(ref state);
				custom.LastError = Report(e, custom.LastError);
			}

			return EditorGUILayout.End();
		}

		/// <summary>Scene view gizmos of the selected object's custom editors (<see cref="Editor.OnSceneGUI" />).</summary>
		public void DrawSceneGUI(GameObject selected)
		{
			if (assembly == null || selected == null)
			{
				return;
			}

			foreach (Behaviour component in selected.Components.ToArray())
			{
				Editor custom = GetEditor(component);
				if (custom != null)
				{
					Guard(custom, e => e.OnSceneGUI(), "OnSceneGUI");
				}
			}
		}

		// ---------------------------------------------------------------- menus

		/// <summary>Items of the editor scripts under a built-in menu (called inside that menu, after its own items).</summary>
		public void DrawMenuItems(string menu)
		{
			MenuNode node = menus.Find(m => m.Name == menu);
			if (node == null)
			{
				return;
			}

			ImGui.Separator();
			DrawMenuChildren(node);
		}

		/// <summary>Top-level menus of the editor scripts that are not built-in ones.</summary>
		public void DrawExtraMenus()
		{
			foreach (MenuNode node in menus)
			{
				if (Array.IndexOf(BuiltInMenus, node.Name) < 0 && ImGui.BeginMenu(node.Name))
				{
					DrawMenuChildren(node);
					ImGui.EndMenu();
				}
			}
		}

		/// <summary>Runs the menu item at <paramref name="path" /> (test hooks); false when there is none.</summary>
		public bool ExecuteMenuItem(string path)
		{
			string[] parts = path.Split('/');
			List<MenuNode> level = menus;
			MenuNode node = null;
			foreach (string part in parts)
			{
				node = level.Find(n => n.Name == part);
				if (node == null)
				{
					return false;
				}

				level = node.Children;
			}

			if (node?.Method == null)
			{
				return false;
			}

			pendingMenuItems.Add(node.Method);
			return true;
		}

		// ---------------------------------------------------------------- windows

		public T GetWindow<T>(string title, bool focus) where T : EditorWindow, new()
		{
			// A window of the type waiting to be restored is the one to return.
			if (assembly != null && savedWindows.Count > 0)
			{
				RestoreWindows();
			}

			T window = (T)windows.Find(w => w.GetType() == typeof(T));
			if (window == null)
			{
				window = new T();
				OpenWindow(window, title);
			}
			else if (title != null)
			{
				window.title = title;
			}

			if (focus)
			{
				window.Focus();
			}

			return window;
		}

		public T CreateWindow<T>(string title) where T : EditorWindow, new()
		{
			var window = new T();
			OpenWindow(window, title);
			window.Focus();
			return window;
		}

		public bool HasOpenInstances(Type type)
		{
			return windows.Exists(w => type.IsInstanceOfType(w));
		}

		/// <summary>Adds the window to the open ones (OnEnable); an open window only changes its title.</summary>
		public void OpenWindow(EditorWindow window, string title)
		{
			if (title != null)
			{
				window.title = title;
			}

			if (window.IsOpen)
			{
				return;
			}

			window.Key ??= FreeKey(window.GetType());
			window.IsOpen = true;
			windows.Add(window);
			Guard(window, w => w.InvokeEnable(), "OnEnable");
		}

		public void CloseWindow(EditorWindow window)
		{
			if (!window.IsOpen)
			{
				return;
			}

			window.IsOpen = false;
			windows.Remove(window);
			Guard(window, w => w.InvokeDisable(), "OnDisable");
			Guard(window, w => w.InvokeDestroy(), "OnDestroy");
			ForgetErrors(window);
		}

		// ---------------------------------------------------------------- undo and selection

		public void RecordObject(Behaviour component, string name)
		{
			editor.Edits.Begin(component, name);
			recordPending = true;
			changedComponents.Add(component);
		}

		public void RecordSceneChange(string name, Action change)
		{
			editor.RecordStructural(name, change);
		}

		/// <summary>A component edited outside its inspector: the scene is dirty and OnValidate runs (end of frame).</summary>
		public void MarkChanged(Behaviour component)
		{
			changedComponents.Add(component);
		}

		public void Select(GameObject go)
		{
			editor.Select(go);
		}

		public void SelectAsset(string assetPath)
		{
			if (assetPath == null)
			{
				editor.Select(null);
				editor.SelectAsset(null);
				return;
			}

			editor.SelectAsset(assetPath);
		}

		#endregion

		#region PrivateMethods

		private void RegisterEditor(Type type)
		{
			if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
			{
				return;
			}

			foreach (CustomEditorAttribute attribute in type.GetCustomAttributes<CustomEditorAttribute>(false))
			{
				Type inspected = attribute.InspectedType;
				if (inspected == null || !typeof(Behaviour).IsAssignableFrom(inspected))
				{
					Debug.LogWarning($"[Editor] [CustomEditor] on {type.Name}: the inspected type must be a component (derived from Behaviour).");
					continue;
				}

				if (editorTypes.TryGetValue(inspected, out (Type editorType, bool) registered))
				{
					Debug.LogWarning($"[Editor] {inspected.Name} has two custom editors ({registered.editorType.Name}, {type.Name}); {registered.editorType.Name} is used.");
					continue;
				}

				editorTypes[inspected] = (type, attribute.EditorForChildClasses);
			}
		}

		/// <summary>The editor registered for the type itself, else for the nearest base type that covers child classes.</summary>
		private Type ResolveEditorType(Type componentType)
		{
			if (resolvedEditors.TryGetValue(componentType, out Type cached))
			{
				return cached;
			}

			Type result = null;
			for (Type type = componentType; type != null && type != typeof(Behaviour); type = type.BaseType)
			{
				if (editorTypes.TryGetValue(type, out (Type editorType, bool forChildClasses) entry) && (type == componentType || entry.forChildClasses))
				{
					result = entry.editorType;
					break;
				}
			}

			resolvedEditors[componentType] = result;
			return result;
		}

		private void DisposeEditors(Func<Behaviour, bool> predicate)
		{
			List<Behaviour> stale = null;
			foreach (KeyValuePair<Behaviour, Editor> pair in liveEditors)
			{
				if (predicate(pair.Key))
				{
					(stale ??= new List<Behaviour>()).Add(pair.Key);
				}
			}

			if (stale == null)
			{
				return;
			}

			foreach (Behaviour component in stale)
			{
				Editor custom = liveEditors[component];
				liveEditors.Remove(component);
				Guard(custom, e => e.InvokeDisable(), "OnDisable");
				custom.target = null;
				ForgetErrors(custom);
			}
		}

		private void ForgetErrors(object owner)
		{
			if (lastErrors.Count == 0)
			{
				return;
			}

			foreach ((object, string) key in lastErrors.Keys.Where(k => k.owner == owner).ToArray())
			{
				lastErrors.Remove(key);
			}
		}

		private void DrawWindow(EditorWindow window)
		{
			if (window.FocusRequested)
			{
				window.FocusRequested = false;
				ImGui.SetNextWindowFocus();
			}

			ImGui.SetNextWindowSize(window.defaultSize, ImGuiCond.FirstUseEver);
			if (window.minSize.X > 0f || window.minSize.Y > 0f)
			{
				ImGui.SetNextWindowSizeConstraints(window.minSize, new NVector2(float.MaxValue, float.MaxValue));
			}

			bool open = true;
			bool visible = ImGui.Begin(window.title + "###" + window.Key, ref open, window.windowFlags);
			window.hasFocus = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			if (visible)
			{
				// The last error first: what the failing code draws may hide anything below it.
				if (window.LastError != null)
				{
					EditorGUILayout.HelpBox(window.GetType().Name + ".OnGUI: " + window.LastError + " (see the Console)", MessageType.Error);
				}

				windowContext.Scene = editor.Scene;
				windowContext.UndoTarget = null;
				windowContext.LabelWidth = Math.Max(110f, ImGui.GetContentRegionAvail().X * 0.38f);
				EditorGUILayout.Begin(windowContext, null);
				var state = default(ImGuiErrorRecoveryState);
				ImGuiP.ErrorRecoveryStoreState(ref state);
				try
				{
					window.InvokeGUI();
					CheckBalanced(ref state, window.GetType().Name + ".OnGUI");
					window.LastError = null;
				}
				catch (Exception e)
				{
					Recover(ref state);
					window.LastError = Report(e, window.LastError);
				}
				finally
				{
					EditorGUILayout.End();
					windowContext.Release();
				}
			}

			ImGui.End();
			if (!open)
			{
				CloseWindow(window);
			}
		}

		private void RestoreWindows()
		{
			var states = new List<WindowState>(savedWindows);
			savedWindows.Clear();
			foreach (WindowState state in states)
			{
				if (windows.Exists(w => w.Key == state.Key))
				{
					continue;
				}

				Type type = assembly.GetType(state.TypeName, false);
				if (type == null || type.IsAbstract || !typeof(EditorWindow).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
				{
					// The window class is gone (renamed or deleted): the window stays closed.
					continue;
				}

				EditorWindow window;
				try
				{
					window = (EditorWindow)Activator.CreateInstance(type);
				}
				catch (Exception e)
				{
					Debug.LogError($"[Editor] Cannot reopen the window {type.Name}: {e.GetBaseException().Message}");
					continue;
				}

				if (state.State != null)
				{
					Apply(window, state.State);
				}

				window.Key = state.Key;
				OpenWindow(window, state.Title);
			}
		}

		/// <summary>Serialized fields of a window (references to scene objects need the scene loaded).</summary>
		private byte[] Capture(EditorWindow window)
		{
			try
			{
				return EditorSerialization.CaptureObject(window, editor.Scene);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Editor] Cannot save the state of the window {window.GetType().Name}: {e.Message}");
				return null;
			}
		}

		private void Apply(EditorWindow window, byte[] state)
		{
			// Field values of types from the editor scripts resolve by name while the assembly is registered.
			TypeRegistry.Register(assembly);
			try
			{
				EditorSerialization.ApplyObject(window, state, editor.Scene);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Editor] Cannot restore the state of the window {window.GetType().Name}: {e.Message}");
			}
			finally
			{
				TypeRegistry.Unregister(assembly);
			}
		}

		/// <summary>"Namespace.Type" for the first window of a type, "Namespace.Type#n" for the next ones.</summary>
		private string FreeKey(Type type)
		{
			string key = type.FullName;
			for (int n = 2; windows.Exists(w => w.Key == key) || savedWindows.Exists(s => s.Key == key); n++)
			{
				key = type.FullName + "#" + n;
			}

			return key;
		}

		private void BuildMenus(List<(string path, int priority, MethodInfo method)> items)
		{
			menus.Clear();
			foreach ((string path, int priority, MethodInfo method) in items.OrderBy(i => i.priority))
			{
				string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length < 2)
				{
					Debug.LogWarning($"[Editor] [MenuItem(\"{path}\")] on {method.DeclaringType?.Name}.{method.Name}: the path needs a menu and an item (\"Tools/My Item\").");
					continue;
				}

				List<MenuNode> level = menus;
				for (int i = 0; i < parts.Length; i++)
				{
					MenuNode node = level.Find(n => n.Name == parts[i]);
					if (node == null)
					{
						node = new MenuNode { Name = parts[i], Priority = priority };
						level.Add(node);
					}

					if (i == parts.Length - 1)
					{
						if (node.Method != null || node.Children.Count > 0)
						{
							Debug.LogWarning($"[Editor] The menu item \"{path}\" exists twice; {method.DeclaringType?.Name}.{method.Name} is left out.");
							break;
						}

						node.Method = method;
					}

					level = node.Children;
				}
			}
		}

		private void DrawMenuChildren(MenuNode parent)
		{
			for (int i = 0; i < parent.Children.Count; i++)
			{
				MenuNode node = parent.Children[i];
				if (i > 0 && node.Priority - parent.Children[i - 1].Priority > 10)
				{
					ImGui.Separator();
				}

				if (node.Method != null)
				{
					if (ImGui.MenuItem(node.Name))
					{
						pendingMenuItems.Add(node.Method);
					}
				}
				else if (ImGui.BeginMenu(node.Name))
				{
					DrawMenuChildren(node);
					ImGui.EndMenu();
				}
			}
		}

		private void RunPendingMenuItems()
		{
			if (pendingMenuItems.Count == 0)
			{
				return;
			}

			MethodInfo[] items = pendingMenuItems.ToArray();
			pendingMenuItems.Clear();
			foreach (MethodInfo method in items)
			{
				try
				{
					method.Invoke(null, null);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}

		/// <summary>Runs a callback of user code; an exception is logged (once while it repeats) and swallowed.</summary>
		private void Guard<T>(T owner, Action<T> action, string callback)
		{
			try
			{
				action(owner);
			}
			catch (Exception e)
			{
				Exception inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
				string message = inner.GetType().Name + ": " + inner.Message;
				if (!lastErrors.TryGetValue((owner, callback), out string previous) || previous != message)
				{
					lastErrors[(owner, callback)] = message;
					Debug.LogError($"[Editor] {owner.GetType().Name}.{callback}: {message}\n{inner.StackTrace}");
				}
			}
		}

		/// <summary>Logs the exception unless it repeats the previous one (OnGUI runs every frame); returns its message.</summary>
		private static string Report(Exception exception, string previous)
		{
			Exception inner = exception is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : exception;
			string message = inner.GetType().Name + ": " + inner.Message;
			if (message != previous)
			{
				Debug.LogException(inner);
			}

			return message;
		}

		/// <summary>
		///     Closes what user code left open (windows, child windows, IDs, styles, popups, groups), quietly: the
		///     exception is reported already, ImGui's own assert, log line and tooltip would repeat every frame.
		/// </summary>
		private static void Recover(ref ImGuiErrorRecoveryState state)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			bool assert = io.ConfigErrorRecoveryEnableAssert;
			bool log = io.ConfigErrorRecoveryEnableDebugLog;
			bool tooltip = io.ConfigErrorRecoveryEnableTooltip;
			io.ConfigErrorRecoveryEnableAssert = false;
			io.ConfigErrorRecoveryEnableDebugLog = false;
			io.ConfigErrorRecoveryEnableTooltip = false;
			ImGuiP.ErrorRecoveryTryToRecoverState(ref state);
			io.ConfigErrorRecoveryEnableAssert = assert;
			io.ConfigErrorRecoveryEnableDebugLog = log;
			io.ConfigErrorRecoveryEnableTooltip = tooltip;
		}

		/// <summary>A Begin / Push without its End / Pop: warned about (once per frame and owner) and closed.</summary>
		private static void CheckBalanced(ref ImGuiErrorRecoveryState before, string owner)
		{
			var after = default(ImGuiErrorRecoveryState);
			ImGuiP.ErrorRecoveryStoreState(ref after);
			if (MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref before, 1)).SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref after, 1))))
			{
				return;
			}

			throw new InvalidOperationException(owner + " left an ImGui scope open (a Begin / Push call without its End / Pop).");
		}

		#endregion

		#region NestedTypes

		private sealed class MenuNode
		{
			public string Name;
			public int Priority;
			public MethodInfo Method;
			public readonly List<MenuNode> Children = new List<MenuNode>();
		}

		private sealed class WindowState
		{
			public string TypeName;
			public string Key;
			public string Title;
			public byte[] State;
		}

		#endregion
	}
}
