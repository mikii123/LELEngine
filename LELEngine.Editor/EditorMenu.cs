using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's menus (main menu bar, the editor scripts' menus, context menus) go through these instead of ImGui's own
	///     calls: ImGui's menus, and while test anchors are on every menu and item records one ("menu:File/Save") for
	///     automation acting on the UI (<c>click "menu:..."</c>). Automation runs the main menu's actions themselves
	///     (<see cref="EditorActions" />), never through a menu. Path segments are the labels; a label with "###id" is named
	///     by its id ("Undo Move###Undo" is "Undo"), "##" hides the rest ("Box##2" is "Box").
	/// </summary>
	internal static class EditorMenu
	{
		#region PrivateFields

		private static readonly List<string> path = new List<string>();

		#endregion

		#region PublicMethods

		/// <summary>ImGui.BeginMenu: true when the menu is open.</summary>
		public static bool Begin(string label, bool enabled = true)
		{
			string name = PathName(label);
			bool activated = Automation.UIAutomation.BeginActivation(label);
			bool open = ImGui.BeginMenu(label, enabled);
			Automation.UIAutomation.EndActivation(activated);
			TestAnchors.RecordItem("menu:" + Joined(name));
			if (open)
			{
				path.Add(name);
			}

			return open;
		}

		/// <summary>ImGui.EndMenu for a menu <see cref="Begin" /> opened.</summary>
		public static void End()
		{
			if (path.Count > 0)
			{
				path.RemoveAt(path.Count - 1);
			}

			ImGui.EndMenu();
		}

		/// <summary>ImGui.MenuItem: true when clicked.</summary>
		public static bool Item(string label, string shortcut = null, bool selected = false, bool enabled = true)
		{
			string name = PathName(label);
			bool activated = Automation.UIAutomation.BeginActivation(label);
			bool clicked = ImGui.MenuItem(label, shortcut ?? "", selected, enabled);
			Automation.UIAutomation.EndActivation(activated);
			TestAnchors.RecordItem("menu:" + Joined(name));
			return clicked;
		}

		/// <summary>A UI frame starts: a menu path left open by an exception between Begin and End is dropped.</summary>
		public static void NewFrame()
		{
			path.Clear();
		}

		/// <summary>ImGui.MenuItem with a ticked state the click toggles (true when clicked).</summary>
		public static bool Item(string label, string shortcut, ref bool selected, bool enabled = true)
		{
			if (!Item(label, shortcut, selected, enabled))
			{
				return false;
			}

			selected = !selected;
			return true;
		}

		/// <summary>A ticked item whose state is kept in the preferences under <paramref name="key" />.</summary>
		public static void Toggle(string label, string key, ref bool selected)
		{
			if (Item(label, "", ref selected))
			{
				EditorPreferences.SetToggle(key, selected);
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>A label as a path segment ("###id" names it by the id, "##" hides the rest).</summary>
		private static string PathName(string label)
		{
			int id = label.IndexOf("###", StringComparison.Ordinal);
			if (id >= 0)
			{
				return label.Substring(id + 3);
			}

			int hidden = label.IndexOf("##", StringComparison.Ordinal);
			return hidden >= 0 ? label.Substring(0, hidden) : label;
		}

		private static string Joined(string name)
		{
			return path.Count == 0 ? name : string.Join("/", path) + "/" + name;
		}

		#endregion
	}
}
