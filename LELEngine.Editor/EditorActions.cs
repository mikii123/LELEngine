using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>What an action does with unsaved changes it would otherwise lose (automation's --save / --discard).</summary>
	public enum UnsavedChanges
	{
		/// <summary>The action does not run while there are any (it says what is unsaved).</summary>
		Refuse,
		Save,
		Discard
	}

	/// <summary>
	///     An editor action named by its menu path ("File/Save", "Assets/Force Reserialize All Assets", "GameObject/Cube").
	///     <see cref="Run" /> is the action itself: it asks nothing and draws nothing; what it cannot do without the user's
	///     word (unsaved changes it would lose, a file name) makes it throw <see cref="InvalidOperationException" /> with the
	///     reason and what to do instead. <see cref="Click" /> is what the menu item does when that differs (it asks first).
	/// </summary>
	internal sealed class EditorAction
	{
		#region Constructors

		public EditorAction(string path, Action run)
		{
			Path = EditorActions.Normalize(path);
			Run = run;
		}

		#endregion

		#region PublicFields

		public string Path { get; }

		/// <summary>The action, for automation and for the menu when <see cref="Click" /> is null.</summary>
		public Action Run { get; }

		/// <summary>The menu item's click when it asks before the action (null: <see cref="Run" />).</summary>
		public Action Click { get; init; }

		/// <summary>The menu's text when it changes ("Undo Move"); null: the path's last part.</summary>
		public Func<string> Label { get; init; }

		public string Shortcut { get; init; }

		/// <summary>Null: always enabled.</summary>
		public Func<bool> Enabled { get; init; }

		/// <summary>A ticked item (an open window, a setting that is on); null: not one.</summary>
		public Func<bool> Checked { get; init; }

		/// <summary>The menu draws a separator before the item (or before its sub-menu, for the first item of one).</summary>
		public bool SeparatorBefore { get; init; }

		public string Name => Path.Substring(Path.LastIndexOf('/') + 1);
		public bool IsEnabled => Enabled == null || Enabled();
		public bool IsChecked => Checked != null && Checked();

		#endregion
	}

	/// <summary>
	///     The editor's actions (<see cref="EditorAction" />): the main menu bar draws them, automation runs them by path (the
	///     action, never the menu: CLI <c>action</c>, <see cref="Automation.EditorAutomation.RunAction" />).
	/// </summary>
	internal sealed class EditorActions
	{
		#region PrivateFields

		private readonly List<EditorAction> actions = new List<EditorAction>();

		#endregion

		#region PublicFields

		public IReadOnlyList<EditorAction> All => actions;

		#endregion

		#region PublicMethods

		/// <summary>"File / Save " -> "File/Save".</summary>
		public static string Normalize(string path)
		{
			return string.Join("/", Array.ConvertAll(path.Split('/'), part => part.Trim()));
		}

		public void Add(EditorAction action)
		{
			if (Find(action.Path) != null)
			{
				throw new InvalidOperationException("The editor action " + action.Path + " exists twice.");
			}

			actions.Add(action);
		}

		/// <summary>The action at <paramref name="path" /> (case does not matter), or null.</summary>
		public EditorAction Find(string path)
		{
			string normalized = Normalize(path);
			return actions.Find(a => string.Equals(a.Path, normalized, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		///     Draws the actions under <paramref name="menu" /> inside its open menu (sub-menus by their paths). A click runs
		///     <paramref name="invoke" /> (default: the item's <see cref="EditorAction.Click" /> or <see cref="EditorAction.Run" />,
		///     an exception logged as a warning).
		/// </summary>
		public void DrawMenu(string menu, Action<EditorAction> invoke = null)
		{
			DrawLevel(Normalize(menu), invoke ?? InvokeFromMenu);
		}

		/// <summary>The action's click from a menu: its reason is logged when it cannot run.</summary>
		public static void InvokeFromMenu(EditorAction action)
		{
			try
			{
				(action.Click ?? action.Run)();
			}
			catch (InvalidOperationException e)
			{
				Debug.LogWarning("[Editor] " + action.Path + ": " + e.Message);
			}
		}

		#endregion

		#region PrivateMethods

		private void DrawLevel(string prefix, Action<EditorAction> invoke)
		{
			string start = prefix + "/";
			var subMenus = new HashSet<string>(StringComparer.Ordinal);
			bool drawn = false;
			foreach (EditorAction action in actions)
			{
				if (!action.Path.StartsWith(start, StringComparison.Ordinal))
				{
					continue;
				}

				string rest = action.Path.Substring(start.Length);
				int slash = rest.IndexOf('/');
				if (drawn && action.SeparatorBefore && (slash < 0 || !subMenus.Contains(rest.Substring(0, slash))))
				{
					ImGui.Separator();
				}

				if (slash >= 0)
				{
					string subMenu = rest.Substring(0, slash);
					if (subMenus.Add(subMenu) && EditorMenu.Begin(subMenu))
					{
						DrawLevel(start + subMenu, invoke);
						EditorMenu.End();
					}
				}
				else
				{
					string label = action.Label != null ? action.Label() + "###" + action.Name : action.Name;
					if (EditorMenu.Item(label, action.Shortcut, action.IsChecked, action.IsEnabled))
					{
						invoke(action);
					}
				}

				drawn = true;
			}
		}

		#endregion
	}
}
