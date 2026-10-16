using System.Text;

namespace LELEngine.UIElements
{
	/// <summary>Diagnostics: text dumps of a visual tree with layout rectangles.</summary>
	public static class UIDebug
	{
		#region PublicMethods

		/// <summary>The tree below <paramref name="root" /> (inclusive): type, name, classes and layout per element.</summary>
		public static string DumpTree(VisualElement root, int maxDepth = 32)
		{
			var builder = new StringBuilder();
			Dump(root, builder, 0, maxDepth);
			return builder.ToString();
		}

		/// <summary>The chain of ancestors of an element with their layouts (debugging a misplaced element).</summary>
		public static string DumpAncestry(VisualElement element)
		{
			var builder = new StringBuilder();
			for (VisualElement e = element; e != null; e = e.hierarchy.parent)
			{
				builder.Insert(0, Describe(e) + "\n");
			}

			return builder.ToString();
		}

		#endregion

		#region PrivateMethods

		private static void Dump(VisualElement element, StringBuilder builder, int depth, int maxDepth)
		{
			builder.Append(' ', depth * 2).Append(Describe(element)).Append('\n');
			if (depth >= maxDepth)
			{
				return;
			}

			int count = element.hierarchy.childCount;
			for (int i = 0; i < count; i++)
			{
				Dump(element.hierarchy[i], builder, depth + 1, maxDepth);
			}
		}

		/// <summary>Also print the layout engine's raw state (constraints of the last pass, dirty flags).</summary>
		public static bool verbose;

		private static string Describe(VisualElement element)
		{
			Rect r = element.layout;
			string text = element is TextElement textElement && !string.IsNullOrEmpty(textElement.text) ? " \"" + Shorten(textElement.text) + "\"" : "";
			string display = element.resolvedStyle.display == DisplayStyle.None ? " [display:none]" : element.resolvedStyle.visibility == Visibility.Hidden ? " [hidden]" : "";
			string raw = "";
			if (verbose)
			{
				LayoutNodeData n = element.layoutNode;
				raw = $" raw=({n.rawX:0.#},{n.rawY:0.#},{n.rawWidth:0.#},{n.rawHeight:0.#}) last=({n.lastAvailableWidth:0.#} {n.lastWidthMode},{n.lastAvailableHeight:0.#} {n.lastHeightMode} -> {n.lastLayoutWidth:0.#}x{n.lastLayoutHeight:0.#} valid={n.lastLayoutValid}) dirty={n.dirty} desc={element.hasDirtyLayoutDescendant} measures={n.measureCacheCount}";
			}

			return element + text + " layout=" + r + display + raw;
		}

		private static string Shorten(string text)
		{
			text = text.Replace("\n", "\\n");
			return text.Length > 30 ? text.Substring(0, 30) + "..." : text;
		}

		#endregion
	}
}
