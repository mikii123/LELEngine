using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's menus (main menu bar, the editor scripts' menus, the Create menu) go through these instead of ImGui's
	///     own calls. Drawn normally they are ImGui's menus, and while test anchors are on every menu and item records one
	///     ("menu:File/Save Scene"). In a menu pass (<see cref="List" />, <see cref="Execute" />) nothing is shown: every menu
	///     counts as open and its items are listed, or the item asked for reports a click, so its action runs as if clicked.
	///     Path segments are the labels; a label with "###id" is named by its id ("Undo Move###Undo" is "Undo"), "##" hides
	///     the rest ("Box##2" is "Box").
	/// </summary>
	internal static unsafe class EditorMenu
	{
		#region PublicFields

		/// <summary>A menu item of a menu pass's list.</summary>
		public readonly struct Entry
		{
			public Entry(string path, bool enabled, bool selected, string shortcut)
			{
				Path = path;
				Enabled = enabled;
				Selected = selected;
				Shortcut = shortcut;
			}

			public string Path { get; }
			public bool Enabled { get; }

			/// <summary>Ticked (a window that is open, a setting that is on).</summary>
			public bool Selected { get; }

			public string Shortcut { get; }
		}

		#endregion

		#region PrivateFields

		private enum Mode
		{
			Draw,
			List,
			Execute
		}

		private static Mode mode;
		private static readonly List<string> path = new List<string>();

		// Per open menu of the path: whether it is enabled (a disabled menu's items are disabled).
		private static readonly List<bool> enabledMenus = new List<bool>();
		private static string target;
		private static bool executed;
		private static List<Entry> listed;

		#endregion

		#region PublicMethods

		/// <summary>ImGui.BeginMenu: true when the menu is open (in a pass: when the items asked for may be inside).</summary>
		public static bool Begin(string label, bool enabled = true)
		{
			string name = PathName(label);
			if (mode == Mode.Draw)
			{
				bool activated = Automation.UIAutomation.BeginActivation(label);
				bool open = ImGui.BeginMenu(label, enabled);
				Automation.UIAutomation.EndActivation(activated);
				TestAnchors.RecordItem("menu:" + Joined(name));
				if (open)
				{
					path.Add(name);
					enabledMenus.Add(enabled);
				}

				return open;
			}

			if (mode == Mode.Execute && !target.StartsWith(Joined(name) + "/", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			path.Add(name);
			enabledMenus.Add(enabled);
			return true;
		}

		/// <summary>ImGui.EndMenu for a menu <see cref="Begin" /> opened.</summary>
		public static void End()
		{
			if (path.Count > 0)
			{
				path.RemoveAt(path.Count - 1);
				enabledMenus.RemoveAt(enabledMenus.Count - 1);
			}

			if (mode == Mode.Draw)
			{
				ImGui.EndMenu();
			}
		}

		/// <summary>ImGui.MenuItem: true when clicked (in an <see cref="Execute" /> pass: when it is the item asked for and enabled).</summary>
		public static bool Item(string label, string shortcut = null, bool selected = false, bool enabled = true)
		{
			string name = PathName(label);
			if (mode == Mode.Draw)
			{
				bool activated = Automation.UIAutomation.BeginActivation(label);
				bool clicked = ImGui.MenuItem(label, shortcut ?? "", selected, enabled);
				Automation.UIAutomation.EndActivation(activated);
				TestAnchors.RecordItem("menu:" + Joined(name));
				return clicked;
			}

			string full = Joined(name);
			if (mode == Mode.List)
			{
				listed.Add(new Entry(full, enabled && !enabledMenus.Contains(false), selected, string.IsNullOrEmpty(shortcut) ? null : shortcut));
				return false;
			}

			if (!executed && enabled && !enabledMenus.Contains(false) && string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
			{
				executed = true;
				return true;
			}

			return false;
		}

		/// <summary>A UI frame starts: a menu path left open by an exception between Begin and End is dropped.</summary>
		public static void NewFrame()
		{
			if (mode == Mode.Draw)
			{
				path.Clear();
				enabledMenus.Clear();
			}
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

		/// <summary>Every item <paramref name="drawMenus" /> draws, with its path, without showing anything.</summary>
		public static List<Entry> List(Action drawMenus)
		{
			listed = new List<Entry>();
			RunPass(Mode.List, drawMenus);
			List<Entry> result = listed;
			listed = null;
			return result;
		}

		/// <summary>Runs the item at <paramref name="menuPath" /> as if clicked; false when there is none or it is disabled.</summary>
		public static bool Execute(string menuPath, Action drawMenus)
		{
			target = string.Join("/", Array.ConvertAll(menuPath.Split('/'), part => part.Trim()));
			executed = false;
			RunPass(Mode.Execute, drawMenus);
			target = null;
			return executed;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     A pass runs in an ImGui context of its own (sharing the editor's fonts), inside a window nobody sees: what the
		///     menus draw besides menus (separators) goes there, and the editor's own context is left alone. Called between
		///     frames (automation).
		/// </summary>
		private static void RunPass(Mode passMode, Action drawMenus)
		{
			if (mode != Mode.Draw)
			{
				throw new InvalidOperationException("A menu pass is running already.");
			}

			ImGuiController owner = EditorApplication.Instance?.Window?.Controller ?? throw new InvalidOperationException("The editor is not running.");
			owner.EnsureAtlasReady();
			ImGuiContextPtr previous = ImGui.GetCurrentContext();
			ImGuiContextPtr context = ImGui.CreateContext(owner.Context.IO.Fonts);
			try
			{
				ImGui.SetCurrentContext(context);
				ImGuiIOPtr io = ImGui.GetIO();
				io.IniFilename = null;
				io.BackendFlags = owner.Context.IO.BackendFlags;
				io.FontDefault = owner.Context.IO.FontDefault;
				io.DisplaySize = new NVector2(256, 256);
				io.DeltaTime = 1f / 60f;
				ImGui.NewFrame();
				ImGui.SetNextWindowPos(new NVector2(-10000, -10000));
				ImGui.Begin("##menu_pass", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing);
				mode = passMode;
				try
				{
					drawMenus();
				}
				finally
				{
					mode = Mode.Draw;
					path.Clear();
					enabledMenus.Clear();
					ImGui.End();
					ImGui.EndFrame();
				}
			}
			finally
			{
				ImGui.DestroyContext(context);
				ImGui.SetCurrentContext(previous);
			}
		}

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
