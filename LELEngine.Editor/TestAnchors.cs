using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Screen rectangles of named UI items, recorded while an automated run asks for them (<see cref="Enabled" />: test
	///     hooks, the automation server): scripted mouse input aims at them. Names: "hierarchy:Object",
	///     "project:Folder/File.ext", "component:Type", "field:Type.field" (the field's first line), "menu:File/Save Scene".
	///     Each one remembers the window it was drawn in and the frame (<see cref="GetAnchors" /> lists a window's current
	///     ones); a window capture records into its own set (<see cref="BeginCapture" />), in the capture's pixels.
	/// </summary>
	internal static class TestAnchors
	{
		#region PublicFields

		/// <summary>Items are recorded: test hooks or the automation server asked for them, or a window capture draws.</summary>
		public static bool Enabled
		{
			get => requested || capture != null;
			set => requested = value;
		}

		/// <summary>Frames of the editor's main window (counted before its UI is built).</summary>
		public static int Frame { get; private set; }

		#endregion

		#region PrivateFields

		/// <summary>A named UI element as last drawn: its rectangle, ImGui window (root), ImGui item id, frame and value (value fields).</summary>
		public struct Entry
		{
			public NVector2 Min;
			public NVector2 Max;
			public uint Window;
			public uint Item;
			public int Frame;
			public string Value;

			/// <summary>A control (<see cref="RecordElement" />), not an anchor of the test hooks ("row:", "field:", "hierarchy:").</summary>
			public bool IsElement;
		}

		/// <summary>An element <see cref="FindElements" /> found: its window, full name and entry.</summary>
		public readonly record struct Match(uint Window, string Name, Entry Entry);

		// Keyed by window id and name: the same name in two windows is two elements.
		private static readonly Dictionary<(uint window, string name), Entry> elements = new Dictionary<(uint, string), Entry>();
		private static readonly Dictionary<string, Entry> rects = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

		// Names given in this frame per window (a repeated label gets "#2", "#3").
		private static readonly Dictionary<(uint window, string name), int> namesThisFrame = new Dictionary<(uint, string), int>();

		// The same for the frame a window capture draws (each of its frames starts over).
		private static readonly Dictionary<(uint window, string name), int> captureNames = new Dictionary<(uint, string), int>();

		// The last label column drawn (window, top of its line, text): names the widget after it.
		private static (uint Window, float Y, string Text) lineLabel;

		// What the elements drawn now are part of, outermost first (a component, a field, a list row; null: a level that
		// repeats its parent's name and adds none): "Mover/Waypoints/2".
		private static readonly List<string> scopes = new List<string>();
		private static bool requested;

		// Elements not drawn for this many frames are forgotten (checked every PruneEvery frames).
		private const int ForgetAfterFrames = 36000;
		private const int PruneEvery = 3600;

		/// <summary>A window capture is drawing: its items go here (null otherwise).</summary>
		private static Dictionary<string, Entry> capture;

		#endregion

		#region PublicMethods

		/// <summary>The main window starts building a frame's UI.</summary>
		public static void NextFrame()
		{
			Frame++;
			namesThisFrame.Clear();
			scopes.Clear();
			if (Frame % PruneEvery == 0)
			{
				var forgotten = new List<(uint, string)>();
				foreach (KeyValuePair<(uint window, string name), Entry> pair in elements)
				{
					if (Frame - pair.Value.Frame > ForgetAfterFrames)
					{
						forgotten.Add(pair.Key);
					}
				}

				foreach ((uint, string) key in forgotten)
				{
					elements.Remove(key);
				}
			}
		}

		/// <summary>
		///     The elements drawn until <see cref="PopScope" /> are part of <paramref name="name" /> (its visible text): their
		///     names get it as a path segment, "input:Mover/Speed" for the field Speed of the component Mover. Pushed by the
		///     inspector (components, materials, nested objects, collections and their rows) and by open combos.
		/// </summary>
		public static void PushScope(string name)
		{
			string text = VisibleText(name);
			string parent = Scope();
			bool repeats = text.Length == 0 || (parent != null && string.Equals(LastSegment(parent), text, StringComparison.OrdinalIgnoreCase));
			scopes.Add(repeats ? null : text);
		}

		public static void PopScope()
		{
			if (scopes.Count > 0)
			{
				scopes.RemoveAt(scopes.Count - 1);
			}
		}

		/// <summary>The visible text the last <see cref="RecordElement" /> gave its element (without the scope path).</summary>
		public static string LastElementText { get; private set; }

		public static void Record(string name, NVector2 min, NVector2 max)
		{
			Dictionary<string, Entry> target = capture ?? (requested ? rects : null);
			if (target == null)
			{
				return;
			}

			// The last item is the element's (the anchors are recorded right after it): automation invokes it by its id.
			var entry = new Entry { Min = min, Max = max, Window = CurrentWindowId(), Item = ImGui.GetItemID(), Frame = Frame };
			target[name] = entry;
			if (capture == null)
			{
				elements[(entry.Window, name)] = entry;
				Automation.UIAutomation.Recorded(name, entry);
			}
		}

		/// <summary>
		///     Records the last item as an element "kind:scope/text" (the label's visible text, else its id part, after the
		///     scopes it is drawn in (<see cref="PushScope" />); "#2", "#3" when the window has the name already this frame).
		///     <paramref name="item" />: its id when the last item is not it (groups: vectors, combos). <paramref name="value" />:
		///     a value field's value as text.
		/// </summary>
		public static void RecordElement(string kind, string label, uint item = 0, string value = null)
		{
			if (Enabled)
			{
				RecordElement(kind, label, item != 0 ? item : ImGui.GetItemID(), value, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
			}
		}

		/// <summary>An element whose widget is not the last item (drawn next, at this rectangle: a combo, whose popup follows it).</summary>
		public static void RecordElement(string kind, string label, uint item, string value, NVector2 min, NVector2 max)
		{
			if (!Enabled)
			{
				return;
			}

			var entry = new Entry { Min = min, Max = max, Window = CurrentWindowId(), Item = item, Frame = Frame, Value = value, IsElement = true };

			// A widget with a hidden label ("##v") right after a label column on its line is named after that label: the
			// name the inspector shows for the field.
			string text = VisibleText(label);
			if (label != null && label.StartsWith("##", StringComparison.Ordinal) && lineLabel.Text != null && lineLabel.Window == entry.Window
				&& Math.Abs(entry.Min.Y - lineLabel.Y) < ImGui.GetFrameHeight() * 0.5f)
			{
				text = lineLabel.Text;
			}

			lineLabel = default;
			LastElementText = text;
			string scope = Scope();
			string path = scope == null ? text : text.Length == 0 || string.Equals(LastSegment(scope), text, StringComparison.OrdinalIgnoreCase) ? scope : scope + "/" + text;
			string name = UniqueName(kind + ":" + path);
			Names[(entry.Window, name)] = 0;
			Dictionary<string, Entry> target = capture ?? rects;
			target[name] = entry;
			if (capture == null)
			{
				elements[(entry.Window, name)] = entry;
			}
		}

		/// <summary>The name the next element of this kind and label gets in the current window (see <see cref="RecordElement" />).</summary>
		public static string PeekName(string kind, string label)
		{
			return UniqueName(kind + ":" + VisibleText(label));
		}

		/// <summary>
		///     A field's label column was just drawn (<see cref="FieldDrawer" />): the widget on its line whose own label is
		///     hidden gets its text as its element name ("checkbox:Cast Shadows" instead of "checkbox:v").
		/// </summary>
		public static void LineLabel(string text)
		{
			if (Enabled)
			{
				lineLabel = (CurrentWindowId(), ImGui.GetItemRectMin().Y, text?.Trim());
			}
		}

		/// <summary>The widget drawn next on this line with a hidden label is named <paramref name="text" /> (a cell without a label column).</summary>
		public static void NameNextElement(string text)
		{
			if (Enabled)
			{
				lineLabel = (CurrentWindowId(), ImGui.GetCursorScreenPos().Y, text);
			}
		}

		private static string UniqueName(string baseName)
		{
			uint window = CurrentWindowId();
			Dictionary<(uint window, string name), int> names = Names;
			if (!names.ContainsKey((window, baseName)))
			{
				return baseName;
			}

			for (int n = 2; ; n++)
			{
				string name = baseName + "#" + n;
				if (!names.ContainsKey((window, name)))
				{
					return name;
				}
			}
		}

		/// <summary>
		///     The elements a name means (in the window with that id, or any window): "kind:path" or the path alone, the
		///     path whole ("Mover/Speed") or its end ("Speed": every element whose path ends with "/Speed"). Of those, the
		///     ones drawn most recently (another object selected since, a window behind a tab: older drawings lose), controls
		///     before the hooks' anchors when no kind is named, whole paths before ends; one element recorded under two names
		///     once. One result: that element; more: ambiguous.
		/// </summary>
		public static List<Match> FindElements(string query, uint? window)
		{
			List<Match> found = Collect(query, window, true);
			if (found.Count == 0)
			{
				// "Time: 5" is a label, not a kind and a path.
				found = Collect(query, window, false);
			}

			if (found.Count == 0)
			{
				return found;
			}

			int newest = 0;
			foreach (Match match in found)
			{
				newest = Math.Max(newest, match.Entry.Frame);
			}

			found.RemoveAll(m => m.Entry.Frame < newest - 2);
			string path = SplitName(query, true, out string kind);
			if (kind == null && found.Exists(m => m.Entry.IsElement))
			{
				// A name without its kind means a control: "Weights/1" is the list row's field, not the hooks' "row:Weights/1".
				found.RemoveAll(m => !m.Entry.IsElement);
			}

			if (found.Exists(m => string.Equals(SplitName(m.Name, true, out _), path, StringComparison.OrdinalIgnoreCase)))
			{
				found.RemoveAll(m => !string.Equals(SplitName(m.Name, true, out _), path, StringComparison.OrdinalIgnoreCase));
			}

			var seen = new HashSet<(uint, uint)>();
			found.RemoveAll(m => m.Entry.Item != 0 && !seen.Add((m.Window, m.Entry.Item)));
			return found;
		}

		/// <summary>The kind of an element's name ("input" of "input:Mover/Speed"), or null.</summary>
		public static string KindOf(string name)
		{
			SplitName(name, true, out string kind);
			return kind;
		}

		/// <summary>The path of an element's name ("Mover/Speed" of "input:Mover/Speed").</summary>
		public static string PathOf(string name)
		{
			return SplitName(name, true, out _);
		}

		/// <summary>A label's visible text: "Title###id" is "Title", "##id" alone is "id", "Text##id" is "Text".</summary>
		public static string VisibleText(string label)
		{
			if (string.IsNullOrEmpty(label))
			{
				return "";
			}

			int id = label.IndexOf("###", StringComparison.Ordinal);
			if (id >= 0)
			{
				string shown = label.Substring(0, id).Trim();
				return shown.Length > 0 ? shown : label.Substring(id + 3).Trim();
			}

			int hidden = label.IndexOf("##", StringComparison.Ordinal);
			if (hidden >= 0)
			{
				string shown = label.Substring(0, hidden).Trim();
				return shown.Length > 0 ? shown : label.Substring(hidden + 2).Trim();
			}

			return label.Trim();
		}

		/// <summary>Records the last item's rectangle under the name.</summary>
		public static void RecordItem(string name)
		{
			if (Enabled)
			{
				Record(name, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
			}
		}

		/// <summary>A window capture draws: what it records is kept apart (<see cref="EndCapture" /> returns it).</summary>
		public static void BeginCapture()
		{
			capture = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
			captureNames.Clear();
			scopes.Clear();
		}

		/// <summary>The capture's items, offset by <paramref name="origin" /> (the window's corner in the capture).</summary>
		public static List<UIAnchor> EndCapture(NVector2 origin, NVector2 size)
		{
			var result = new List<UIAnchor>();
			foreach (KeyValuePair<string, Entry> pair in capture ?? new Dictionary<string, Entry>())
			{
				NVector2 min = pair.Value.Min - origin;
				NVector2 max = pair.Value.Max - origin;
				if (max.X > 0 && max.Y > 0 && min.X < size.X && min.Y < size.Y)
				{
					result.Add(new UIAnchor(pair.Key, min.X, min.Y, max.X - min.X, max.Y - min.Y, pair.Value.Value));
				}
			}

			capture = null;
			result.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
			return result;
		}

		/// <summary>
		///     The items recorded in the last frames in the window with ImGui id <paramref name="windowId" /> (null: every
		///     window), relative to <paramref name="origin" />.
		/// </summary>
		public static List<UIAnchor> GetAnchors(uint? windowId, NVector2 origin)
		{
			var result = new List<UIAnchor>();
			foreach (KeyValuePair<(uint window, string name), Entry> pair in elements)
			{
				if (Frame - pair.Value.Frame > 2 || (windowId != null && pair.Key.window != windowId.Value))
				{
					continue;
				}

				NVector2 min = pair.Value.Min - origin;
				NVector2 max = pair.Value.Max - origin;
				result.Add(new UIAnchor(pair.Key.name, min.X, min.Y, max.X - min.X, max.Y - min.Y, pair.Value.Value));
			}

			result.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
			return result;
		}

		/// <summary>The window an item was drawn in (its top-level ImGui window's id), or null when it was never drawn.</summary>
		public static uint? WindowOf(string name)
		{
			int at = name.IndexOf('@');
			return rects.TryGetValue(at > 0 ? name.Substring(0, at) : name, out Entry entry) ? entry.Window : null;
		}

		/// <summary>
		///     The point a scripted mouse aims at: a field's value column (70% across its first line), the label of other
		///     items, or "name@0.93": that fraction across (the buttons at the end of a field's line); false when the
		///     item was not drawn.
		/// </summary>
		public static bool TryGetPoint(string name, out NVector2 point)
		{
			point = default;
			float fraction = -1f;
			int at = name.IndexOf('@');
			if (at > 0)
			{
				float.TryParse(name.Substring(at + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fraction);
				name = name.Substring(0, at);
			}

			if (!rects.TryGetValue(name, out Entry rect))
			{
				return false;
			}

			float lineY = rect.Min.Y + Math.Min(rect.Max.Y - rect.Min.Y, ImGui.GetFrameHeight()) * 0.5f;
			if (fraction >= 0f)
			{
				point = new NVector2(rect.Min.X + (rect.Max.X - rect.Min.X) * fraction, lineY);
			}
			else
			{
				point = name.StartsWith("field:", StringComparison.OrdinalIgnoreCase)
					? new NVector2(rect.Min.X + (rect.Max.X - rect.Min.X) * 0.7f, lineY)
					: new NVector2(rect.Min.X + Math.Min(40f, (rect.Max.X - rect.Min.X) * 0.5f), lineY);
			}

			return true;
		}

		#endregion

		#region PrivateMethods

		private static Dictionary<(uint window, string name), int> Names => capture != null ? captureNames : namesThisFrame;

		/// <summary>The current scope path ("Mover/Waypoints/2"), or null outside any.</summary>
		private static string Scope()
		{
			string path = null;
			foreach (string segment in scopes)
			{
				if (segment != null)
				{
					path = path == null ? segment : path + "/" + segment;
				}
			}

			return path;
		}

		private static string LastSegment(string path)
		{
			int slash = path.LastIndexOf('/');
			return slash >= 0 ? path.Substring(slash + 1) : path;
		}

		/// <summary>
		///     "kind:path" → path and kind; with <paramref name="kinds" /> a prefix of lowercase letters before the first ':'
		///     is the kind, without it (or when there is none) the whole text is the path and the kind is null.
		/// </summary>
		private static string SplitName(string name, bool kinds, out string kind)
		{
			kind = null;
			int colon = name.IndexOf(':');
			if (!kinds || colon <= 0)
			{
				return name;
			}

			for (int i = 0; i < colon; i++)
			{
				if (name[i] < 'a' || name[i] > 'z')
				{
					return name;
				}
			}

			kind = name.Substring(0, colon);
			return name.Substring(colon + 1);
		}

		private static List<Match> Collect(string query, uint? window, bool kinds)
		{
			string path = SplitName(query, kinds, out string kind);
			var found = new List<Match>();
			foreach (KeyValuePair<(uint window, string name), Entry> pair in elements)
			{
				if (window != null && pair.Key.window != window.Value)
				{
					continue;
				}

				string elementPath = SplitName(pair.Key.name, true, out string elementKind);
				if (kind != null && !string.Equals(kind, elementKind, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (string.Equals(elementPath, path, StringComparison.OrdinalIgnoreCase)
					|| (elementPath.Length > path.Length && elementPath[elementPath.Length - path.Length - 1] == '/' && elementPath.EndsWith(path, StringComparison.OrdinalIgnoreCase)))
				{
					found.Add(new Match(pair.Key.window, pair.Key.name, pair.Value));
				}
			}

			return found;
		}

		/// <summary>Id of the top-level window being drawn (a docked panel's own; child windows give their root's).</summary>
		internal static uint CurrentWindowId()
		{
			ImGuiWindowPtr window = ImGuiP.GetCurrentWindow();
			if (window.IsNull)
			{
				return 0;
			}

			ImGuiWindowPtr root = window.RootWindow;
			// Popups and menus belong to the window that opened them.
			while (!root.IsNull && !root.ParentWindow.IsNull && (root.Flags & (ImGuiWindowFlags.Popup | ImGuiWindowFlags.ChildMenu)) != 0)
			{
				root = root.ParentWindow.RootWindow;
			}

			return root.IsNull ? 0 : root.ID;
		}

		#endregion
	}
}
