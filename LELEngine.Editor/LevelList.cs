using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     A long list of choices split into levels by what the choices are: types by their namespace, files by their folder
	///     (the Add Component popup, the shader drop-down, type pickers, the Create menu's classes). Drawn as foldable groups
	///     in a popup or a drop-down (<see cref="Draw" />), or as sub-menus in a menu (<see cref="DrawMenu" />); a search
	///     shows the matches flat, each with its level. The levels split themselves: a level holding only one other level
	///     (and nothing else) is one level with both names ("LELEngine.Physics", "Models/Ships"), and the levels everything
	///     shares are left out (shaders all under "Shaders/" show its contents at the top). Groups come first, then the
	///     choices of the level itself, each in name order; a group shows how many choices it holds.
	/// </summary>
	internal sealed class LevelList
	{
		#region PublicFields

		/// <summary>One choice: its levels (a namespace's or a folder's parts), its name in the list, its id (unique).</summary>
		public readonly struct Item
		{
			public readonly string[] Levels;
			public readonly string Label;
			public readonly string Id;
			public readonly string Tooltip;

			public Item(string[] levels, string label, string id, string tooltip = null)
			{
				Levels = levels ?? Array.Empty<string>();
				Label = label;
				Id = id;
				Tooltip = tooltip;
			}
		}

		public readonly List<Item> Items;

		#endregion

		#region PrivateFields

		private sealed class Group
		{
			public string Label;
			public string Key;
			public string Path;
			public readonly List<Group> Groups = new List<Group>();
			public readonly List<int> Leaves = new List<int>();
			public int Count;
		}

		private readonly Group root;
		private readonly string separator;

		#endregion

		#region Constructors

		/// <param name="separator">Joins the names of levels shown as one ("." for namespaces, "/" for folders).</param>
		public LevelList(List<Item> items, string separator)
		{
			Items = items;
			this.separator = separator;
			root = new Group { Label = "", Key = "", Path = "" };
			for (int i = 0; i < items.Count; i++)
			{
				Group group = root;
				foreach (string level in items[i].Levels)
				{
					Group next = group.Groups.Find(g => string.Equals(g.Label, level, StringComparison.Ordinal));
					if (next == null)
					{
						string path = group.Path.Length == 0 ? level : group.Path + separator + level;
						next = new Group { Label = level, Key = path, Path = path };
						group.Groups.Add(next);
					}

					group = next;
				}

				group.Leaves.Add(i);
			}

			// The levels every choice shares are left out.
			while (root.Leaves.Count == 0 && root.Groups.Count == 1)
			{
				Group only = root.Groups[0];
				root.Groups.Clear();
				root.Groups.AddRange(only.Groups);
				root.Leaves.AddRange(only.Leaves);
			}

			Arrange(root);
		}

		#endregion

		#region PublicMethods

		/// <summary>Types by their namespace (none: at the top), named by <paramref name="label" /> (default: the name, nested ones with their outer class).</summary>
		public static LevelList OfTypes(IEnumerable<Type> types, Func<Type, string> label = null)
		{
			var items = new List<Item>();
			foreach (Type type in types)
			{
				string name = label != null ? label(type) : NestedName(type);
				string[] levels = string.IsNullOrEmpty(type.Namespace) ? Array.Empty<string>() : type.Namespace.Split('.');
				items.Add(new Item(levels, name, type.FullName ?? type.Name, type.FullName));
			}

			return new LevelList(items, ".");
		}

		/// <summary>Asset paths ("Shaders/Fog/Sheet.shader") by their folder, named by the file's name.</summary>
		public static LevelList OfPaths(IEnumerable<string> paths)
		{
			var items = new List<Item>();
			foreach (string path in paths)
			{
				string normal = path.Replace('\\', '/');
				int slash = normal.LastIndexOf('/');
				string[] levels = slash > 0 ? normal.Substring(0, slash).Split('/', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
				items.Add(new Item(levels, slash >= 0 ? normal.Substring(slash + 1) : normal, path, path));
			}

			return new LevelList(items, "/");
		}

		/// <summary>
		///     A search box for the list (focused when its popup appears); the caller keeps the text and passes it to
		///     <see cref="Draw" />.
		/// </summary>
		public static void SearchField(string id, ref string search, float width = -1f)
		{
			if (ImGui.IsWindowAppearing())
			{
				search = "";
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(width);
			EditorGui.InputTextWithHint(id, "Search", ref search, 128);
		}

		/// <summary>
		///     A child window for the list in a popup: as tall as what is open (groups unfold, a search narrows it) up to
		///     <paramref name="maxHeight" />, then it scrolls. Always end it with ImGui.EndChild.
		/// </summary>
		public static bool BeginArea(string id, float width, float maxHeight = 420f)
		{
			ImGui.SetNextWindowSizeConstraints(new NVector2(width, 0f), new NVector2(width, maxHeight));
			return ImGui.BeginChild(id, new NVector2(width, 0f), ImGuiChildFlags.AutoResizeY);
		}

		/// <summary>
		///     The list in the current window (a popup, a drop-down, a child): foldable groups, or the matches of
		///     <paramref name="search" /> flat with their level beside them. The groups holding a <paramref name="isSelected" />
		///     choice open when the window appears. The index of the choice clicked, else -1.
		/// </summary>
		public int Draw(string search = null, Func<int, bool> isSelected = null)
		{
			int clicked = -1;
			if (!string.IsNullOrEmpty(search))
			{
				bool any = false;
				for (int i = 0; i < Items.Count; i++)
				{
					Item item = Items[i];
					string level = string.Join(separator, item.Levels);
					if (item.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && item.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					any = true;
					if (Leaf(i, isSelected))
					{
						clicked = i;
					}

					if (level.Length > 0)
					{
						ImGui.SameLine();
						ImGui.TextDisabled(level);
					}
				}

				if (!any)
				{
					ImGui.TextDisabled("Nothing matches.");
				}

				return clicked;
			}

			bool appearing = ImGui.IsWindowAppearing();
			DrawGroup(root, isSelected, appearing, ref clicked);
			return clicked;
		}

		/// <summary>The list as menu items, a sub-menu for every group (inside an open menu or popup). The index of the choice clicked, else -1.</summary>
		public int DrawMenu(Func<int, bool> isSelected = null)
		{
			int clicked = -1;
			DrawMenuGroup(root, isSelected, ref clicked);
			return clicked;
		}

		#endregion

		#region PrivateMethods

		private static string NestedName(Type type)
		{
			return type.IsNested && type.DeclaringType != null ? NestedName(type.DeclaringType) + "." + type.Name : type.Name;
		}

		/// <summary>Levels holding only one other level become one; groups and choices in name order; counts.</summary>
		private void Arrange(Group group)
		{
			foreach (Group child in group.Groups)
			{
				while (child.Leaves.Count == 0 && child.Groups.Count == 1)
				{
					Group only = child.Groups[0];
					child.Label = child.Label + separator + only.Label;
					child.Key = only.Key;
					child.Path = only.Path;
					child.Groups.Clear();
					child.Groups.AddRange(only.Groups);
					child.Leaves.AddRange(only.Leaves);
				}

				Arrange(child);
			}

			group.Groups.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
			group.Leaves.Sort((a, b) => string.Compare(Items[a].Label, Items[b].Label, StringComparison.OrdinalIgnoreCase));
			group.Count = group.Leaves.Count;
			foreach (Group child in group.Groups)
			{
				group.Count += child.Count;
			}
		}

		private void DrawGroup(Group group, Func<int, bool> isSelected, bool appearing, ref int clicked)
		{
			foreach (Group child in group.Groups)
			{
				if (appearing && isSelected != null && Holds(child, isSelected))
				{
					ImGui.SetNextItemOpen(true);
				}

				bool open = EditorGui.TreeNodeEx("##level_" + child.Key, ImGuiTreeNodeFlags.SpanAvailWidth, child.Label);
				// How many choices it holds, at the row's right end (drawn, not an item: the row's name stays its own).
				string count = child.Count.ToString();
				float x = ImGui.GetItemRectMax().X - ImGui.CalcTextSize(count).X - ImGui.GetStyle().FramePadding.X;
				ImGui.GetWindowDrawList().AddText(new NVector2(x, ImGui.GetItemRectMin().Y + ImGui.GetStyle().FramePadding.Y), ImGui.GetColorU32(ImGuiCol.TextDisabled), count);
				if (open)
				{
					DrawGroup(child, isSelected, appearing, ref clicked);
					ImGui.TreePop();
				}
			}

			foreach (int index in group.Leaves)
			{
				if (Leaf(index, isSelected))
				{
					clicked = index;
				}
			}
		}

		private bool Leaf(int index, Func<int, bool> isSelected)
		{
			Item item = Items[index];
			bool pressed = EditorGui.Selectable(item.Label + "##" + item.Id, isSelected != null && isSelected(index));
			if (item.Tooltip != null && ImGui.IsItemHovered())
			{
				EditorGui.SetTooltip(item.Tooltip);
			}

			return pressed;
		}

		private void DrawMenuGroup(Group group, Func<int, bool> isSelected, ref int clicked)
		{
			foreach (Group child in group.Groups)
			{
				if (EditorMenu.Begin(child.Label + "##level_" + child.Key))
				{
					DrawMenuGroup(child, isSelected, ref clicked);
					EditorMenu.End();
				}
			}

			foreach (int index in group.Leaves)
			{
				Item item = Items[index];
				if (EditorMenu.Item(item.Label + "##" + item.Id, null, isSelected != null && isSelected(index)))
				{
					clicked = index;
				}

				if (item.Tooltip != null && ImGui.IsItemHovered())
				{
					EditorGui.SetTooltip(item.Tooltip);
				}
			}
		}

		private static bool Holds(Group group, Func<int, bool> isSelected)
		{
			foreach (int index in group.Leaves)
			{
				if (isSelected(index))
				{
					return true;
				}
			}

			foreach (Group child in group.Groups)
			{
				if (Holds(child, isSelected))
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}
}
