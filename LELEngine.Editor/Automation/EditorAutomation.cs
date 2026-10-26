using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using LELEngine.Editor.Automation;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Driving the editor from code: its windows (list, open, close, focus, <see cref="GetScreenshot" />), their named
	///     items (<see cref="GetAnchors" />), mouse and keyboard input, menus, the scene and selection, play mode, undo, the
	///     console, and waiting for frames or for the editor to settle.
	///     <para>
	///         Everything runs on the main thread between two editor frames. Methods returning a Task run over several frames
	///         (input sequences, waits): start automation code with <see cref="Run" /> (or from -executeMethod / an automation
	///         command, which do it), so its awaits continue between frames too. The same operations are commands of the
	///         editor's automation server (<see cref="AutomationCommands" />), which LELEngine.Editor.Console -command sends.
	///     </para>
	///     <para>
	///         Batch mode has no UI: nothing is drawn but the windows captured on request; mouse and keyboard input need the
	///         editor's window (the other operations work in both).
	///     </para>
	/// </summary>
	public static class EditorAutomation
	{
		#region PrivateFields

		private static EditorApplication editor;
		private static readonly AutomationContext context = new AutomationContext();
		private static readonly List<(int frame, TaskCompletionSource tcs)> frameWaiters = new List<(int, TaskCompletionSource)>();
		private static int frame;
		private static AutomationServer server;
		private static string pendingMethod;
		private static string[] pendingMethodArgs;

		#endregion

		#region PublicFields

		/// <summary>An editor is running with a project open.</summary>
		public static bool IsRunning => editor?.Project != null;

		/// <summary>The editor draws its UI (false in batch mode: windows are drawn only when captured).</summary>
		public static bool HasUI => editor != null && editor.HasUI;

		/// <summary>Ticks of the editor since automation started (one per editor frame; one per batch-mode tick).</summary>
		public static int Frame => frame;

		public static string ProjectPath => Editor.Project?.RootPath;

		/// <summary>The open scene's asset path (null for an unsaved scene).</summary>
		public static string ScenePath => Editor.Scene?.Path;

		public static bool IsPlaying => Editor.IsPlaying;
		public static bool IsPaused => Editor.PlayState == PlayState.Paused;

		/// <summary>The scene has unsaved changes.</summary>
		public static bool IsSceneDirty => Editor.SceneDirty;

		/// <summary>Scripts are building, assets refreshing or a scene loading (see <see cref="WaitForIdle" />).</summary>
		public static bool IsBusy => Editor.IsBusy;

		/// <summary>The last script build failed (the previous scripts stay loaded).</summary>
		public static bool HasScriptErrors => Editor.Scripts?.HasErrors ?? false;

		/// <summary>The name of the automation server's pipe (null when it is not listening).</summary>
		public static string ServerName => server?.PipeName;

		internal static EditorApplication Editor => editor ?? throw new InvalidOperationException("The editor is not running.");

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- running

		/// <summary>
		///     Runs automation code on the main thread between two editor frames; its awaits continue there. The task ends
		///     when the code does (with its exception, if any).
		/// </summary>
		public static Task Run(Func<Task> script)
		{
			var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			context.Post(async _ =>
			{
				try
				{
					await script();
					done.SetResult();
				}
				catch (Exception e)
				{
					done.SetException(e);
				}
			}, null);
			return done.Task;
		}

		/// <summary>As <see cref="Run(Func{Task})" />, with a result.</summary>
		public static Task<T> Run<T>(Func<Task<T>> script)
		{
			var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			context.Post(async _ =>
			{
				try
				{
					done.SetResult(await script());
				}
				catch (Exception e)
				{
					done.SetException(e);
				}
			}, null);
			return done.Task;
		}

		/// <summary>Completes after <paramref name="frames" /> editor frames (UI drawn, views rendered in between).</summary>
		public static Task NextFrame(int frames = 1)
		{
			var tcs = new TaskCompletionSource();
			frameWaiters.Add((frame + Math.Max(1, frames), tcs));
			return tcs.Task;
		}

		/// <summary>Waits frame by frame until <paramref name="condition" /> holds; false after the timeout.</summary>
		public static async Task<bool> WaitUntil(Func<bool> condition, double timeoutSeconds = 60)
		{
			DateTime end = DateTime.UtcNow.AddSeconds(timeoutSeconds);
			while (!condition())
			{
				if (DateTime.UtcNow > end)
				{
					return false;
				}

				await NextFrame();
			}

			return true;
		}

		/// <summary>Waits until scripts are built, assets refreshed and loads done (and a few frames for the UI to show it); false after the timeout.</summary>
		public static async Task<bool> WaitForIdle(double timeoutSeconds = 120)
		{
			// The file watchers report a change with a delay: give them a moment before trusting "idle".
			await NextFrame(3);
			bool idle = await WaitUntil(() => !Editor.IsBusy, timeoutSeconds);
			await NextFrame(2);
			return idle && !Editor.IsBusy;
		}

		// ---------------------------------------------------------------- windows

		/// <summary>Every window of the editor: "Editor" (the main window), the panels, the scripts' windows, UI Builder, Audio Studio.</summary>
		public static IReadOnlyList<EditorWindowInfo> GetWindows()
		{
			return Editor.GetPanels().Select(Describe).ToList();
		}

		/// <summary>A window by name (case-insensitive; a script window also by its class name), or null.</summary>
		public static EditorWindowInfo? GetWindow(string name)
		{
			IEditorPanel panel = FindPanel(name);
			return panel != null ? Describe(panel) : null;
		}

		/// <summary>Opens (and brings to the front) a window: a panel, "UI Builder", "Audio Studio", or a script window by its class name.</summary>
		public static void OpenWindow(string name)
		{
			IEditorPanel panel = FindPanel(name);
			if (panel != null)
			{
				panel.Focus();
				return;
			}

			if (Editor.Extensions.OpenWindowOfType(name) == null)
			{
				throw new ArgumentException(UnknownWindow(name));
			}
		}

		public static void CloseWindow(string name)
		{
			IEditorPanel panel = RequirePanel(name);
			if (panel.IsWholeContext && panel is MainWindowPanel)
			{
				throw new InvalidOperationException("The editor's main window is not closed through automation.");
			}

			panel.PanelOpen = false;
		}

		public static void FocusWindow(string name)
		{
			RequirePanel(name).Focus();
		}

		/// <summary>
		///     An image of a window: <paramref name="width" /> x <paramref name="height" /> pixels (0: its size on screen, or a
		///     default when it is not shown). Only that window is in the image, never another one over it. At its size on screen
		///     it is drawn as the editor's last frame drew it (with the menus and popups it opened); at any other size, with
		///     <paramref name="layout" />, or in batch mode, it is laid out again off screen at that size, as it is now (open
		///     headers, scroll), without input. A view (Scene, Game) renders <paramref name="viewFrames" /> frames for a size it
		///     does not show.
		/// </summary>
		public static EditorScreenshot GetScreenshot(string window, int width = 0, int height = 0, bool layout = false, int viewFrames = WindowCapture.DefaultViewFrames)
		{
			return WindowCapture.Capture(RequirePanel(window), width, height, new CaptureOptions { Layout = layout, ViewFrames = viewFrames });
		}

		/// <summary>
		///     The UI elements a window drew in the last frames ("button:Add Component", "checkbox:Always Refresh", "tree:Cube",
		///     "hierarchy:Cube", "menu:Duplicate", ...), with their rectangles relative to its top-left corner (the main window:
		///     every element). Invoke them by name: <see cref="Click" />, <see cref="DoubleClick" />, <see cref="OpenContextMenu" />,
		///     <see cref="Input" />. Without UI (batch mode): the elements of a capture at the window's default size.
		/// </summary>
		public static IReadOnlyList<UIAnchor> GetElements(string window)
		{
			IEditorPanel panel = RequirePanel(window);
			if (!HasUI || !WindowCapture.TryGetScreenRect(panel, Editor, out NVector2 min, out _))
			{
				return GetScreenshot(window, 0, 0, true).Anchors;
			}

			uint? id = panel.IsWholeContext ? null : WindowCapture.FindWindow(panel.Owner.Context, panel.ImGuiName).ID;
			return TestAnchors.GetAnchors(id, min);
		}

		// ---------------------------------------------------------------- UI elements (need the editor's window)

		/// <summary>
		///     Clicks a UI element (<see cref="GetElements" />): its action runs as if clicked (a button presses, a row is
		///     selected, a checkbox toggles, a menu item runs, a tab is chosen, a text field starts editing), without the mouse,
		///     whatever covers it; a window behind another tab is brought forward first. <paramref name="window" />: the window
		///     whose element is meant, when several windows have one of that name.
		/// </summary>
		public static Task Click(string element, string window = null)
		{
			return Run(() => InvokeElement(element, window, UIAutomation.Action.Click));
		}

		/// <summary>Double-clicks a UI element (rows that open on a double click: project files, console messages, object fields).</summary>
		public static Task DoubleClick(string element, string window = null)
		{
			return Run(() => InvokeElement(element, window, UIAutomation.Action.DoubleClick));
		}

		/// <summary>Opens a UI element's context menu (its right-click menu); its items are then elements too ("menu:Duplicate").</summary>
		public static Task OpenContextMenu(string element, string window = null)
		{
			return Run(() => InvokeElement(element, window, UIAutomation.Action.ContextMenu));
		}

		/// <summary>
		///     Sets a value field's value (its element: a text, number, vector, color field or checkbox, an enum or other
		///     combo), as an edit of it that ended: the code drawing it applies it as a typed value (with undo in the
		///     inspector). <paramref name="text" />: "12.5", "1, 2, 3" (vectors), "#FF8800" or "1, 0.5, 0" (colors), "true",
		///     a combo's option ("Fast"). Elements are named as the inspector shows them: "Speed", "Mover/Speed",
		///     "input:Mover/Speed".
		/// </summary>
		public static Task Input(string element, string text, string window = null)
		{
			return Run(async () =>
			{
				TestAnchors.Match target = await ResolveElement(element, window);
				string kind = TestAnchors.KindOf(target.Name);
				if (kind == "combo")
				{
					await InvokeResolved(target, UIAutomation.Action.Click);
					await ChooseOption(target, text);
					return;
				}

				if (kind != "input" && kind != "color" && kind != "checkbox")
				{
					throw new ArgumentException($"'{target.Name}' is not a value field (click it instead).");
				}

				ImGuiController owner = OwnerOfWindow(target.Window);
				if (!await UIAutomation.Invoke(owner, target.Window, target.Entry.Item, target.Name, UIAutomation.Action.SetValue, 0, text))
				{
					throw new InvalidOperationException(UIAutomation.LastError ?? $"'{target.Name}' was not drawn in the next frames.");
				}

				await NextFrame(2);
			});
		}

		/// <summary>
		///     Scrolls a window by <paramref name="pixels" /> (positive: down), or to its top / bottom
		///     (<see cref="float.NegativeInfinity" /> / <see cref="float.PositiveInfinity" />): the part of it that scrolls (its
		///     own content, or the child area with the most to scroll, as the hierarchy's list).
		/// </summary>
		public static Task Scroll(string window, float pixels)
		{
			return Run(async () =>
			{
				RequireUI();
				IEditorPanel panel = RequirePanel(window);
				if (panel.IsWholeContext || !WindowCapture.TryGetScreenRect(panel, Editor, out _, out _))
				{
					throw new InvalidOperationException($"{panel.PanelName} is not a window on screen (open or focus it first).");
				}

				WindowCapture.ScrollWindow(panel.Owner.Context, panel.ImGuiName, pixels);
				await NextFrame(2);
			});
		}

		/// <summary>
		///     Presses keys for the focused window: "Enter", "F2", "Ctrl+S", "Ctrl+Shift+Z", "Delete" (ImGui key names; Ctrl /
		///     Shift / Alt / Super modifiers). <paramref name="window" />: the OS window's editor window to send them to (null:
		///     the main window).
		/// </summary>
		public static Task Key(string keys, string window = null)
		{
			return Run(async () => await EditorInput.Keys(InputOwner(window), keys));
		}

		/// <summary>Types text into the active text field.</summary>
		public static Task TypeText(string text, string window = null)
		{
			return Run(async () => await EditorInput.Text(InputOwner(window), text));
		}

		// ---------------------------------------------------------------- menus

		/// <summary>Every item of the main menu bar ("File/Save Scene", "GameObject/Cube", "Tools/..." of editor scripts).</summary>
		public static IReadOnlyList<MenuItemInfo> GetMenuItems()
		{
			return Editor.ListMenuItems().Select(e => new MenuItemInfo(e.Path, e.Enabled, e.Selected, e.Shortcut)).ToList();
		}

		/// <summary>Runs a main menu item as if clicked; false when there is no such item or it is disabled.</summary>
		public static bool ExecuteMenuItem(string path)
		{
			return Editor.ExecuteMenuItem(path);
		}

		// ---------------------------------------------------------------- scene, selection, objects

		public static void OpenScene(string assetPath)
		{
			if (!System.IO.File.Exists(AssetDatabase.ToAbsolute(assetPath)))
			{
				throw new ArgumentException("No scene at " + assetPath);
			}

			Editor.OpenScene(assetPath);
		}

		public static void SaveScene()
		{
			Editor.SaveScene();
		}

		/// <summary>
		///     A GameObject of the open scene: "Parent/Child" (a path from a root), a name when it is unique, or "#id" (its
		///     object id); null when there is none.
		/// </summary>
		public static GameObject FindObject(string path)
		{
			Scene scene = Editor.Scene ?? throw new InvalidOperationException("No scene is open.");
			if (path.StartsWith("#", StringComparison.Ordinal) && ulong.TryParse(path.Substring(1), out ulong id))
			{
				return scene.GameObjects.FirstOrDefault(go => go.Id == id && !go.IsDestroyed);
			}

			string[] parts = path.Split('/');
			if (parts.Length > 1)
			{
				GameObject current = scene.GetRootGameObjects().FirstOrDefault(go => go.Name == parts[0] && !go.IsDestroyed);
				for (int i = 1; i < parts.Length && current != null; i++)
				{
					current = current.transform.Children.Select(t => t.gameObject).FirstOrDefault(go => go.Name == parts[i] && !go.IsDestroyed);
				}

				return current;
			}

			List<GameObject> named = scene.GameObjects.Where(go => go.Name == path && !go.IsDestroyed).ToList();
			if (named.Count > 1)
			{
				throw new ArgumentException($"{named.Count} objects are named '{path}': give the path from a root (Parent/Child) or #id.");
			}

			return named.FirstOrDefault();
		}

		/// <summary>Selects a GameObject (path as in <see cref="FindObject" />; null or "none" clears the selection).</summary>
		public static void Select(string path)
		{
			if (string.IsNullOrEmpty(path) || string.Equals(path, "none", StringComparison.OrdinalIgnoreCase))
			{
				Editor.ClearSelection();
				return;
			}

			Editor.Select(RequireObject(path));
		}

		/// <summary>Selects a project file (the inspector shows it): "Materials/Ground.material".</summary>
		public static void SelectAsset(string assetPath)
		{
			Editor.SelectAsset(assetPath);
		}

		/// <summary>The open scene's objects as an indented tree: name, components, inactive marks, ids.</summary>
		public static string DescribeHierarchy()
		{
			Scene scene = Editor.Scene ?? throw new InvalidOperationException("No scene is open.");
			var text = new StringBuilder();
			text.Append("Scene ").Append(scene.Path ?? "(unsaved)").Append(Editor.SceneDirty ? " *" : "").Append('\n');
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				DescribeTree(root, 1, text);
			}

			return text.ToString();
		}

		/// <summary>A GameObject's components and their serialized fields with values.</summary>
		public static string DescribeObject(string path)
		{
			GameObject go = RequireObject(path);
			var text = new StringBuilder();
			text.Append(go.Name).Append(" #").Append(go.Id).Append(go.activeSelf ? "" : " (inactive)").Append(", layer ").Append(go.layer).Append('\n');
			foreach (Behaviour component in go.Components)
			{
				if (component.IsDestroyed)
				{
					continue;
				}

				text.Append("  ").Append(component.GetType().Name).Append('\n');
				foreach (Serialization.SerializedField field in Serialization.SerializationUtility.GetFields(component.GetType()))
				{
					text.Append("    ").Append(field.Name).Append(" = ").Append(Clip(TestHooks.Describe(field.GetValue(component)), 300)).Append('\n');
				}
			}

			return text.ToString();
		}

		/// <summary>A component's field or property: "Light.intensity", "Transform.position", "Ship.stats.speed" (nested).</summary>
		public static string GetField(string objectPath, string fieldPath)
		{
			(Behaviour component, string[] path) = ResolveField(objectPath, fieldPath);
			object value = component;
			for (int i = 1; i < path.Length; i++)
			{
				value = ReadMember(value, path[i]);
			}

			return TestHooks.Describe(value);
		}

		/// <summary>
		///     Sets a component's field or property from text, as one undo step: numbers, true / false, enum names, vectors
		///     "x,y,z", "null", "new:ClassName" (a polymorphic field), asset paths for Prefab fields.
		/// </summary>
		public static void SetField(string objectPath, string fieldPath, string value)
		{
			(Behaviour component, string[] path) = ResolveField(objectPath, fieldPath);
			Editor.Edits.Begin(component);
			WriteMember(component, path, 1, value);
			Editor.Edits.RequestCommit();
			Editor.NotifyChanged(component.gameObject);
		}

		/// <summary>Moves an object under another (null or "none": to the scene's root), keeping its place in the world; one undo step.</summary>
		public static void Reparent(string objectPath, string parentPath)
		{
			GameObject go = RequireObject(objectPath);
			GameObject parent = string.IsNullOrEmpty(parentPath) || string.Equals(parentPath, "none", StringComparison.OrdinalIgnoreCase) ? null : RequireObject(parentPath);
			if (parent != null && (parent == go || parent.transform.IsChildOf(go.transform)))
			{
				throw new ArgumentException($"{parent.Name} is {go.Name} or inside it.");
			}

			Editor.RecordStructural("Re-parent " + go.Name, () => go.transform.SetParent(parent?.transform, true));
		}

		/// <summary>Renames an object; one undo step.</summary>
		public static void Rename(string objectPath, string name)
		{
			GameObject go = RequireObject(objectPath);
			Editor.RecordStructural("Rename " + go.Name, () => go.Name = name);
		}

		/// <summary>Adds a component by type name (as one undo step) and returns it.</summary>
		public static Behaviour AddComponent(string objectPath, string typeName)
		{
			GameObject go = RequireObject(objectPath);
			Type type = TypeRegistry.Resolve(typeName) ?? throw new ArgumentException("Unknown component type " + typeName);
			Behaviour added = null;
			Editor.RecordStructural("Add " + type.Name, () =>
			{
				added = go.AddComponent(type);
				added?.Reset();
			});
			return added;
		}

		// ---------------------------------------------------------------- play mode, undo

		public static void Play()
		{
			if (!Editor.IsPlaying)
			{
				Editor.EnterPlayMode();
			}
		}

		public static void Stop()
		{
			if (Editor.IsPlaying)
			{
				Editor.ExitPlayMode();
			}
		}

		/// <summary>Pauses or resumes play mode.</summary>
		public static void Pause()
		{
			if (Editor.IsPlaying)
			{
				Editor.TogglePause();
			}
		}

		/// <summary>One game frame while paused.</summary>
		public static void Step()
		{
			Editor.Step();
		}

		public static void Undo()
		{
			Editor.UndoLast();
		}

		public static void Redo()
		{
			Editor.RedoLast();
		}

		/// <summary>Reads changed asset files now (as the file watcher does after a moment).</summary>
		public static void RefreshAssets()
		{
			AssetDatabase.Refresh(true);
			Editor.NotifyAssetsChanged();
		}

		// ---------------------------------------------------------------- console

		/// <summary>The console's last <paramref name="count" /> messages (oldest first), optionally only warnings and errors.</summary>
		public static IReadOnlyList<ConsoleMessage> GetConsole(int count = 50, bool warningsAndErrors = false)
		{
			List<ConsoleEntry> entries = Editor.Log.Snapshot();
			IEnumerable<ConsoleEntry> shown = warningsAndErrors ? entries.Where(e => e.Type != LogType.Log) : entries;
			return shown.TakeLast(Math.Max(1, count)).Select(e => new ConsoleMessage(e.Type, e.Text, e.StackTrace, e.Time, e.File, e.Line)).ToList();
		}

		public static void ClearConsole()
		{
			Editor.Log.Clear();
		}

		#endregion

		#region InternalMethods

		/// <summary>The editor opened: automation is ready; the server listens unless listen=0 (batch mode: with -listen).</summary>
		internal static void Start(EditorApplication application, string[] args, bool listen = true)
		{
			editor = application;
			TestAnchors.Enabled = true;
			CommandLine options = CommandLine.Parse(args);
			if (listen && Array.IndexOf(args, "listen=0") < 0 && application.Project != null)
			{
				try
				{
					server = new AutomationServer(application.Project.RootPath);
					server.Start();
					Debug.Log("[Automation] Listening on " + server.PipeName + " (LELEngine.Editor.Console -command help)");
				}
				catch (Exception e)
				{
					Debug.LogWarning("[Automation] The command server could not start: " + e.Message);
					server = null;
				}
			}

			// -executeMethod in the editor: runs once the project's scripts are built (see Pump).
			pendingMethod = application.HasUI ? options.Get("executemethod") ?? GetValue(args, "executemethod") : null;
			pendingMethodArgs = args;
		}

		/// <summary>An automation command asked to end: batch mode's loop ends (see <see cref="BatchEditor" />).</summary>
		internal static bool QuitRequested { get; private set; }

		/// <summary>Batch mode: ends the run. The editor's window: closes it (unsaved changes are asked about unless <paramref name="discard" />).</summary>
		internal static void Quit(bool discard)
		{
			QuitRequested = true;
			if (Editor.HasUI)
			{
				if (discard)
				{
					Editor.DiscardChangesOnClose();
				}

				Editor.Window.Close();
			}
		}

		/// <summary>The editor is closing: the server stops, nothing more runs.</summary>
		internal static void Shutdown()
		{
			server?.Dispose();
			server = null;
			editor = null;
		}

		/// <summary>
		///     Once per editor tick, between frames: frame waiters, then everything posted (scripts, commands, continuations).
		///     The editor's window calls it after its update and before its next UI frame; batch mode once per tick.
		/// </summary>
		internal static void Pump()
		{
			if (editor == null)
			{
				return;
			}

			frame++;
			UIAutomation.Tick();
			for (int i = frameWaiters.Count - 1; i >= 0; i--)
			{
				if (frameWaiters[i].frame <= frame)
				{
					TaskCompletionSource tcs = frameWaiters[i].tcs;
					frameWaiters.RemoveAt(i);
					context.Post(_ => tcs.TrySetResult(), null);
				}
			}

			if (pendingMethod != null && frame > 3 && editor.Project != null && !editor.IsBusy)
			{
				string method = pendingMethod;
				pendingMethod = null;
				_ = ExecuteMethod(method, pendingMethodArgs).ContinueWith(t =>
				{
					if (t.IsFaulted)
					{
						Debug.LogException(t.Exception?.InnerException ?? t.Exception);
					}
				}, TaskScheduler.Default);
			}

			context.Drain();
		}

		/// <summary>
		///     Runs a static method of the scripts (Editor folders included), the engine or the editor between frames:
		///     no parameters or (string[] args); void, bool, int, Task, Task&lt;bool&gt; or Task&lt;int&gt;. The result as an
		///     exit code: 0 success.
		/// </summary>
		internal static Task<int> ExecuteMethod(string name, string[] args)
		{
			return Run(async () =>
			{
				MethodInfo method = FindMethod(name, out object[] parameters, args);
				Debug.Log("[Automation] Executing " + method.DeclaringType?.FullName + "." + method.Name);
				object result;
				try
				{
					result = method.Invoke(null, parameters);
				}
				catch (TargetInvocationException e)
				{
					throw e.InnerException ?? e;
				}

				if (result is Task task)
				{
					await task;
					PropertyInfo resultProperty = task.GetType().GetProperty("Result");
					result = task.GetType().IsGenericType ? resultProperty?.GetValue(task) : null;
				}

				switch (result)
				{
					case bool success:
						return success ? 0 : 1;
					case int code:
						return code;
					default:
						return 0;
				}
			});
		}

		internal static IEditorPanel FindPanel(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return null;
			}

			name = name.Trim();
			IReadOnlyList<IEditorPanel> panels = Editor.GetPanels();
			return panels.FirstOrDefault(p => string.Equals(p.PanelName, name, StringComparison.OrdinalIgnoreCase))
				?? panels.FirstOrDefault(p => p is ScriptWindowPanel script && (string.Equals(script.Window.GetType().Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(script.Window.GetType().FullName, name, StringComparison.OrdinalIgnoreCase)))
				?? panels.FirstOrDefault(p => p.ImGuiName != null && string.Equals(p.ImGuiName.TrimStart('#'), name.TrimStart('#'), StringComparison.OrdinalIgnoreCase));
		}

		internal static IEditorPanel RequirePanel(string name)
		{
			return FindPanel(name) ?? throw new ArgumentException(UnknownWindow(name));
		}

		internal static EditorWindowInfo Describe(IEditorPanel panel)
		{
			bool shown = WindowCapture.TryGetScreenRect(panel, Editor, out NVector2 min, out NVector2 size);
			string kind = panel switch
			{
				MainWindowPanel _ => "editor",
				UIBuilderPanel _ => "os-window",
				AudioStudioPanel _ => "os-window",
				ScriptWindowPanel _ => "script",
				_ => "panel"
			};
			return new EditorWindowInfo(panel.PanelName, kind, panel.PanelOpen, shown, min.X, min.Y, size.X, size.Y);
		}

		#endregion

		#region PrivateMethods

		private static string UnknownWindow(string name)
		{
			return $"No window '{name}'. Windows: {string.Join(", ", Editor.GetPanels().Select(p => p.PanelName))}";
		}

		/// <summary>
		///     Asks for a UI element to be invoked (<see cref="UIAutomation" />) and waits for it to react; the element's OS
		///     window's ImGui context. Its window comes forward first when it was not drawn lately (a tab behind another).
		/// </summary>
		private static async Task<ImGuiController> InvokeElement(string element, string window, UIAutomation.Action action)
		{
			return await InvokeResolved(await ResolveElement(element, window), action);
		}

		/// <summary>
		///     The one element a name means (<see cref="TestAnchors.FindElements" />: whole name, path or its end, as the
		///     inspector names fields), in <paramref name="window" /> when given; drawn now (a window behind a tab is
		///     brought forward). No element or several: an error naming them.
		/// </summary>
		private static async Task<TestAnchors.Match> ResolveElement(string element, string window)
		{
			RequireUI();
			uint? windowId = null;
			if (window != null)
			{
				IEditorPanel panel = RequirePanel(window);
				windowId = panel.IsWholeContext ? null : WindowCapture.FindWindow(panel.Owner.Context, panel.ImGuiName).ID;
			}

			List<TestAnchors.Match> found = TestAnchors.FindElements(element, windowId);
			if (found.Count == 0)
			{
				// Elements are recorded as they are drawn: one may appear on the next frame (a window just opened).
				await NextFrame(2);
				found = TestAnchors.FindElements(element, windowId);
			}

			if (found.Count == 0)
			{
				throw new ArgumentException($"No element '{element}' was drawn{(window != null ? " in " + window : "")} (list a window's elements: elements <window>).");
			}

			if (found.Count > 1)
			{
				throw new ArgumentException($"'{element}' means several elements: {string.Join(", ", found.Take(8).Select(m => m.Name + " (" + WindowNameOf(m.Window) + ")"))}. Give more of the path, the kind or the window.");
			}

			TestAnchors.Match match = found[0];
			if (TestAnchors.Frame - match.Entry.Frame > 2)
			{
				// Drawn before, not now: its window is behind another tab (or closed). Bring it forward.
				IEditorPanel owning = PanelOfWindow(match.Window) ?? throw new InvalidOperationException($"'{match.Name}' is not shown any more (its window is closed).");
				owning.Focus();
				await NextFrame(3);
				found = TestAnchors.FindElements(match.Name, match.Window);
				if (found.Count != 1 || TestAnchors.Frame - found[0].Entry.Frame > 2)
				{
					throw new InvalidOperationException($"'{match.Name}' is not shown any more (another object or asset is shown, or it is folded away).");
				}

				match = found[0];
			}

			return match;
		}

		private static async Task<ImGuiController> InvokeResolved(TestAnchors.Match match, UIAutomation.Action action)
		{
			ImGuiController owner = OwnerOfWindow(match.Window);
			if (!await UIAutomation.Invoke(owner, match.Window, match.Entry.Item, match.Name, action, 0))
			{
				string what = action == UIAutomation.Action.ContextMenu ? "has no context menu" : action == UIAutomation.Action.DoubleClick ? "does nothing on a double click" : "did not react to a click";
				throw new InvalidOperationException($"'{match.Name}' {what} (or it was not drawn in the next frames).");
			}

			await NextFrame(2);
			return owner;
		}

		/// <summary>In a combo just opened: clicks its option <paramref name="option" /> (its items are in its scope: "item:Mover/Mode/Fast").</summary>
		private static async Task ChooseOption(TestAnchors.Match combo, string option)
		{
			string path = TestAnchors.PathOf(combo.Name) + "/" + option.Trim();
			List<TestAnchors.Match> options = TestAnchors.FindElements(path, combo.Window).Where(m => TestAnchors.Frame - m.Entry.Frame <= 2).ToList();
			if (options.Count != 1)
			{
				string prefix = TestAnchors.PathOf(combo.Name) + "/";
				IEnumerable<string> shown = TestAnchors.GetAnchors(combo.Window, NVector2.Zero).Select(a => TestAnchors.PathOf(a.Name))
					.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(p => p.Substring(prefix.Length));
				await EditorInput.Keys(OwnerOfWindow(combo.Window), "Escape");
				throw new ArgumentException(options.Count == 0
					? $"'{combo.Name}' has no option '{option}' (options: {string.Join(", ", shown)})."
					: $"'{option}' means several options of '{combo.Name}': {string.Join(", ", options.Select(m => m.Name))}.");
			}

			await InvokeResolved(options[0], UIAutomation.Action.Click);
		}

		/// <summary>The window a panel draws (by its ImGui window's id), or null.</summary>
		private static IEditorPanel PanelOfWindow(uint windowId)
		{
			foreach (IEditorPanel panel in Editor.GetPanels())
			{
				if (!panel.IsWholeContext && panel.Owner != null && WindowCapture.FindWindow(panel.Owner.Context, panel.ImGuiName).ID == windowId)
				{
					return panel;
				}
			}

			return null;
		}

		private static string WindowNameOf(uint windowId)
		{
			return PanelOfWindow(windowId)?.PanelName ?? "window " + windowId.ToString("X8");
		}

		/// <summary>The ImGui context that has a window with this id (main window, UI Builder, Audio Studio).</summary>
		private static ImGuiController OwnerOfWindow(uint? windowId)
		{
			var owners = new List<ImGuiController> { Editor.Window.Controller, Editor.UIBuilderWindow?.Controller, Editor.AudioStudioWindow?.Controller };
			if (windowId != null)
			{
				foreach (ImGuiController owner in owners)
				{
					if (owner == null)
					{
						continue;
					}

					ImGuiContextPtr previous = ImGui.GetCurrentContext();
					ImGui.SetCurrentContext(owner.Context);
					bool has = !ImGuiP.FindWindowByID(windowId.Value).IsNull;
					ImGui.SetCurrentContext(previous);
					if (has)
					{
						return owner;
					}
				}
			}

			return Editor.Window.Controller;
		}

		private static ImGuiController InputOwner(string window)
		{
			RequireUI();
			return window == null ? Editor.Window.Controller : RequirePanel(window).Owner ?? throw new InvalidOperationException(window + " is not open.");
		}

		private static void RequireUI()
		{
			if (!HasUI)
			{
				throw new InvalidOperationException("UI elements and keys need the editor's window; batch mode draws no UI (use the editor's actions: select, set, menu, ...).");
			}
		}

		private static GameObject RequireObject(string path)
		{
			return FindObject(path) ?? throw new ArgumentException($"No object '{path}' in the open scene.");
		}

		private static (Behaviour component, string[] path) ResolveField(string objectPath, string fieldPath)
		{
			GameObject go = RequireObject(objectPath);
			string[] path = fieldPath.Split('.');
			if (path.Length < 2)
			{
				throw new ArgumentException("A field is Component.field: " + fieldPath);
			}

			Type type = TypeRegistry.Resolve(path[0]);
			Behaviour component = (type != null ? go.GetComponent(type) : null) ?? TestHooks.FindComponentByName(go, path[0]);
			return (component ?? throw new ArgumentException($"{go.Name} has no {path[0]}"), path);
		}

		private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		private static object ReadMember(object target, string name)
		{
			if (target == null)
			{
				throw new ArgumentException("null has no member " + name);
			}

			Type type = target.GetType();
			FieldInfo field = FindField(type, name);
			if (field != null)
			{
				return field.GetValue(target);
			}

			PropertyInfo property = type.GetProperty(name, MemberFlags);
			if (property != null && property.GetIndexParameters().Length == 0)
			{
				return property.GetValue(target);
			}

			throw new ArgumentException($"{type.Name} has no field or property {name}");
		}

		/// <summary>Sets the member at path[index] (through nested fields: value types are written back on the way out).</summary>
		private static void WriteMember(object target, string[] path, int index, string text)
		{
			Type type = target.GetType();
			string name = path[index];
			FieldInfo field = FindField(type, name);
			PropertyInfo property = field == null ? type.GetProperty(name, MemberFlags) : null;
			if (field == null && (property == null || property.GetIndexParameters().Length != 0))
			{
				throw new ArgumentException($"{type.Name} has no field or property {name}");
			}

			Type memberType = field?.FieldType ?? property.PropertyType;
			object value;
			if (index < path.Length - 1)
			{
				value = field != null ? field.GetValue(target) : property.GetValue(target);
				WriteMember(value, path, index + 1, text);
				if (!memberType.IsValueType)
				{
					return;
				}
			}
			else
			{
				value = ParseValue(memberType, text);
			}

			if (field != null)
			{
				field.SetValue(target, value);
			}
			else if (property.CanWrite)
			{
				property.SetValue(target, value);
			}
			else
			{
				throw new ArgumentException($"{type.Name}.{name} cannot be set");
			}
		}

		private static FieldInfo FindField(Type type, string name)
		{
			for (Type t = type; t != null; t = t.BaseType)
			{
				FieldInfo field = t.GetField(name, MemberFlags | BindingFlags.DeclaredOnly);
				if (field != null)
				{
					return field;
				}
			}

			return null;
		}

		/// <summary>Text as a value of the type: the test hooks' rules plus vectors of 2 and 4, quaternions (Euler degrees "x,y,z"), colors and booleans.</summary>
		private static object ParseValue(Type type, string text)
		{
			var culture = CultureInfo.InvariantCulture;
			float[] Numbers()
			{
				return text.Split(',').Select(n => float.Parse(n.Trim(), culture)).ToArray();
			}

			if (type == typeof(OpenTK.Mathematics.Vector2))
			{
				float[] v = Numbers();
				return new OpenTK.Mathematics.Vector2(v[0], v[1]);
			}

			if (type == typeof(OpenTK.Mathematics.Vector4))
			{
				float[] v = Numbers();
				return new OpenTK.Mathematics.Vector4(v[0], v[1], v[2], v[3]);
			}

			if (type == typeof(OpenTK.Mathematics.Color4))
			{
				float[] v = Numbers();
				return new OpenTK.Mathematics.Color4(v[0], v[1], v[2], v.Length > 3 ? v[3] : 1f);
			}

			if (type == typeof(OpenTK.Mathematics.Quaternion))
			{
				float[] v = Numbers();
				return OpenTK.Mathematics.Quaternion.FromEulerAngles(OpenTK.Mathematics.MathHelper.DegreesToRadians(v[0]), OpenTK.Mathematics.MathHelper.DegreesToRadians(v[1]), OpenTK.Mathematics.MathHelper.DegreesToRadians(v[2]));
			}

			if (type == typeof(bool))
			{
				return text == "1" || bool.Parse(text);
			}

			return TestHooks.ParseValue(type, text);
		}

		private static void DescribeTree(GameObject go, int depth, StringBuilder text)
		{
			if (go.IsDestroyed || (go.hideFlags & HideFlags.HideInHierarchy) != 0)
			{
				return;
			}

			text.Append(' ', depth * 2).Append(go.Name).Append(" #").Append(go.Id);
			if (!go.activeSelf)
			{
				text.Append(" (inactive)");
			}

			if (go.prefabInstance != null)
			{
				text.Append(" (prefab)");
			}

			string components = string.Join(", ", go.Components.Where(c => !c.IsDestroyed && c is not Transform).Select(c => c.GetType().Name));
			if (components.Length > 0)
			{
				text.Append(" [").Append(components).Append(']');
			}

			text.Append('\n');
			foreach (Transform child in go.transform.Children)
			{
				DescribeTree(child.gameObject, depth + 1, text);
			}
		}

		private static string Clip(string text, int length)
		{
			return text == null || text.Length <= length ? text : text.Substring(0, length) + "...";
		}

		private static MethodInfo FindMethod(string name, out object[] parameters, string[] args)
		{
			int dot = name.LastIndexOf('.');
			if (dot <= 0 || dot == name.Length - 1)
			{
				throw new ArgumentException("A method is Namespace.Type.Method: " + name);
			}

			string typeName = name.Substring(0, dot);
			string memberName = name.Substring(dot + 1);
			var assemblies = new List<Assembly>(TypeRegistry.Assemblies);
			if (Editor.Extensions.ScriptsAssembly != null)
			{
				assemblies.Add(Editor.Extensions.ScriptsAssembly);
			}

			assemblies.Add(typeof(EditorAutomation).Assembly);
			Type type = assemblies.Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t != null);
			if (type == null)
			{
				List<Type> bySimpleName = assemblies.SelectMany(SafeTypes).Where(t => t.Name == typeName).ToList();
				type = bySimpleName.Count == 1 ? bySimpleName[0] : throw new ArgumentException(bySimpleName.Count == 0 ? "Type not found: " + typeName : "Several types are named " + typeName + ": give the namespace");
			}

			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
			parameters = null;
			MethodInfo method = type.GetMethod(memberName, flags, null, Type.EmptyTypes, null);
			if (method == null)
			{
				method = type.GetMethod(memberName, flags, null, new[] { typeof(string[]) }, null);
				parameters = new object[] { args };
			}

			return method ?? throw new ArgumentException($"{type.FullName} has no static method {memberName}() or {memberName}(string[])");
		}

		private static IEnumerable<Type> SafeTypes(Assembly assembly)
		{
			try
			{
				return assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException e)
			{
				return e.Types.Where(t => t != null);
			}
		}

		/// <summary>name=value arguments of the editor's own command line style.</summary>
		private static string GetValue(string[] args, string name)
		{
			foreach (string arg in args)
			{
				if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
				{
					return arg.Substring(name.Length + 1).Trim('"');
				}
			}

			return null;
		}

		#endregion
	}

	/// <summary>A window of the editor as <see cref="EditorAutomation.GetWindows" /> lists it.</summary>
	public readonly struct EditorWindowInfo
	{
		public EditorWindowInfo(string name, string kind, bool isOpen, bool isOnScreen, float x, float y, float width, float height)
		{
			Name = name;
			Kind = kind;
			IsOpen = isOpen;
			IsOnScreen = isOnScreen;
			X = x;
			Y = y;
			Width = width;
			Height = height;
		}

		public string Name { get; }

		/// <summary>"editor" (the main window), "panel", "script" (an editor script's window) or "os-window" (UI Builder, Audio Studio).</summary>
		public string Kind { get; }

		public bool IsOpen { get; }

		/// <summary>Drawn in the last frame and visible (not a tab behind another, not minimized). Never in batch mode.</summary>
		public bool IsOnScreen { get; }

		/// <summary>Place and size in its OS window (when on screen).</summary>
		public float X { get; }

		public float Y { get; }
		public float Width { get; }
		public float Height { get; }

		public override string ToString()
		{
			return $"{Name} [{Kind}] {(IsOpen ? IsOnScreen ? $"on screen at {X:0},{Y:0} {Width:0}x{Height:0}" : "open, not on screen" : "closed")}";
		}
	}

	/// <summary>An item of the main menu bar.</summary>
	public readonly struct MenuItemInfo
	{
		public MenuItemInfo(string path, bool enabled, bool selected, string shortcut)
		{
			Path = path;
			Enabled = enabled;
			Selected = selected;
			Shortcut = shortcut;
		}

		public string Path { get; }
		public bool Enabled { get; }

		/// <summary>Ticked (an open window, a setting that is on).</summary>
		public bool Selected { get; }

		public string Shortcut { get; }

		public override string ToString()
		{
			return Path + (Selected ? " [x]" : "") + (Enabled ? "" : " (disabled)") + (Shortcut != null ? "  " + Shortcut : "");
		}
	}

	/// <summary>A console message.</summary>
	public readonly struct ConsoleMessage
	{
		public ConsoleMessage(LogType type, string text, string stackTrace, DateTime time, string file, int line)
		{
			Type = type;
			Text = text;
			StackTrace = stackTrace;
			Time = time;
			File = file;
			Line = line;
		}

		public LogType Type { get; }
		public string Text { get; }
		public string StackTrace { get; }
		public DateTime Time { get; }

		/// <summary>The source file a script diagnostic names (null otherwise).</summary>
		public string File { get; }

		public int Line { get; }

		public override string ToString()
		{
			return $"[{Time:HH:mm:ss}] {Type}: {Text}" + (File != null ? $" ({File}:{Line})" : "");
		}
	}
}
