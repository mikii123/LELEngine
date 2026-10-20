using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>Element states selectors can match with pseudo-classes (:hover, :active, ...).</summary>
	[Flags]
	public enum PseudoStates
	{
		None = 0,
		Active = 1,
		Hover = 2,
		Checked = 8,
		Disabled = 32,
		Focus = 64,
		Root = 128
	}

	public enum SelectorCombinator
	{
		/// <summary>"A B": B anywhere below A.</summary>
		Descendant,

		/// <summary>"A > B": B a direct child of A.</summary>
		Child
	}

	/// <summary>One compound selector: Type#name.class1.class2:hover (any part may be absent; "*" matches everything).</summary>
	public sealed class SimpleSelector
	{
		#region PublicFields

		/// <summary>Element type name (the C# class name), null for any type.</summary>
		public string type;

		/// <summary>#name, null for any name.</summary>
		public string name;

		/// <summary>.classes the element must all have.</summary>
		public string[] classes = Array.Empty<string>();

		/// <summary>Pseudo states the element must have.</summary>
		public PseudoStates pseudoMask;

		/// <summary>Pseudo states the element must not have (:enabled).</summary>
		public PseudoStates negatedPseudoMask;

		/// <summary>The selector contains a pseudo-class no element can have (unknown pseudo-class): never matches.</summary>
		public bool neverMatches;

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			var builder = new System.Text.StringBuilder();
			builder.Append(type ?? (name == null && classes.Length == 0 && pseudoMask == 0 && negatedPseudoMask == 0 ? "*" : ""));
			if (name != null) builder.Append('#').Append(name);
			foreach (string c in classes) builder.Append('.').Append(c);
			foreach (PseudoStates state in Enum.GetValues(typeof(PseudoStates)))
			{
				if (state != 0 && (pseudoMask & state) != 0) builder.Append(':').Append(state.ToString().ToLowerInvariant());
			}

			if ((negatedPseudoMask & PseudoStates.Disabled) != 0) builder.Append(":enabled");
			return builder.ToString();
		}

		#endregion
	}

	/// <summary>A selector: simple selectors joined by combinators, with its specificity.</summary>
	public sealed class StyleSelector
	{
		#region PublicFields

		public SimpleSelector[] parts;

		/// <summary>combinators[i] joins parts[i] and parts[i + 1].</summary>
		public SelectorCombinator[] combinators;

		/// <summary>CSS specificity: ids * 10000 + (classes + pseudo-classes) * 100 + types.</summary>
		public int specificity;

		public SimpleSelector rightmost => parts[parts.Length - 1];

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			var builder = new System.Text.StringBuilder();
			for (int i = 0; i < parts.Length; i++)
			{
				if (i > 0)
				{
					builder.Append(combinators[i - 1] == SelectorCombinator.Child ? " > " : " ");
				}

				builder.Append(parts[i]);
			}

			return builder.ToString();
		}

		#endregion
	}

	/// <summary>A property declaration of a rule.</summary>
	public struct StyleDeclaration
	{
		#region PublicFields

		public StylePropertyId id;

		/// <summary>The property name as written (the variable name for custom properties, the name of unknown properties).</summary>
		public string name;

		public StyleValue[] values;

		/// <summary>The value references a USS variable (var()): it must be resolved per element.</summary>
		public bool usesVariables;

		/// <summary>Declaration line in the sheet (diagnostics).</summary>
		public int line;

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return name + ": " + StyleValue.ToString(values) + ";";
		}

		#endregion
	}

	/// <summary>Selectors sharing one declaration block.</summary>
	public sealed class StyleRule
	{
		#region PublicFields

		public StyleSelector[] selectors;
		public StyleDeclaration[] declarations;

		/// <summary>Position in the sheet; later rules win over earlier ones of equal specificity.</summary>
		public int order;

		public StyleSheet sheet;
		public int line;

		#endregion
	}

	/// <summary>
	///     A parsed USS style sheet (an asset, or built from text at runtime). Attach it to a panel or any element
	///     with <see cref="VisualElement.styleSheets" />: its rules apply to that element and its descendants.
	/// </summary>
	public sealed class StyleSheet : IAsset
	{
		#region PublicFields

		public string AssetPath { get; internal set; }

		/// <summary>Display name (the file name, or a name given to a runtime sheet).</summary>
		public string name;

		public List<StyleRule> rules = new List<StyleRule>();

		/// <summary>Sheets this one @imports; their rules apply before this sheet's.</summary>
		public List<StyleSheet> imports = new List<StyleSheet>();

		/// <summary>Parse problems (line + message); empty for a clean sheet.</summary>
		public List<string> errors = new List<string>();

		/// <summary>Bumped whenever the rules change (rebuilt from a file or edited): panels drop their caches.</summary>
		public int version;

		/// <summary>Sequential id for cache keys (sheets are parsed on the loading thread too).</summary>
		internal readonly int id = System.Threading.Interlocked.Increment(ref nextId);

		#endregion

		#region PrivateFields

		private static int nextId;
		private SelectorIndex index;
		private int indexedVersion = -1;

		#endregion

		#region PublicMethods

		/// <summary>Parses USS text. Resource paths (url, resource) resolve relative to <paramref name="assetPath" />.</summary>
		public static StyleSheet Parse(string text, string assetPath = null, string name = null)
		{
			var sheet = new StyleSheet { AssetPath = assetPath, name = name ?? (assetPath != null ? System.IO.Path.GetFileName(assetPath) : "runtime") };
			new StyleSheetParser(sheet, text).Parse();
			return sheet;
		}

		/// <summary>Replaces the contents with a fresh parse of the text (file reload).</summary>
		public void Reparse(string text)
		{
			rules.Clear();
			imports.Clear();
			errors.Clear();
			new StyleSheetParser(this, text).Parse();
			version++;
		}

		/// <summary>Adds a rule from code: selector text and "property: value; ..." declarations.</summary>
		public StyleRule AddRule(string selector, string declarations)
		{
			var parser = new StyleSheetParser(this, selector + " { " + declarations + " }");
			parser.Parse();
			version++;
			return rules.Count > 0 ? rules[rules.Count - 1] : null;
		}

		/// <summary>Rules whose rightmost simple selector may match an element with this type, name and classes.</summary>
		internal SelectorIndex GetIndex()
		{
			if (index == null || indexedVersion != version)
			{
				index = new SelectorIndex(this);
				indexedVersion = version;
			}

			return index;
		}

		public override string ToString()
		{
			return "StyleSheet(" + name + ", " + rules.Count + " rules)";
		}

		#endregion

		#region InternalMethods

		/// <summary>The sheets it imports and the assets its declarations reference (url(), resource(): textures, fonts).</summary>
		internal void CollectReferencedAssets(Action<object> found)
		{
			foreach (StyleSheet import in imports)
			{
				found(import);
			}

			foreach (StyleRule rule in rules)
			{
				if (rule.declarations == null)
				{
					continue;
				}

				foreach (StyleDeclaration declaration in rule.declarations)
				{
					if (declaration.values == null)
					{
						continue;
					}

					foreach (StyleValue value in declaration.values)
					{
						if (value.reference != null)
						{
							found(value.reference);
						}
					}
				}
			}
		}

		#endregion
	}

	/// <summary>
	///     Buckets a sheet's selectors by the rightmost simple selector's most specific key (name, then class, then
	///     type, then universal), so matching an element only tests the selectors that could apply to it.
	/// </summary>
	internal sealed class SelectorIndex
	{
		#region PublicFields

		public readonly Dictionary<string, List<SelectorEntry>> byName = new Dictionary<string, List<SelectorEntry>>(StringComparer.Ordinal);
		public readonly Dictionary<string, List<SelectorEntry>> byClass = new Dictionary<string, List<SelectorEntry>>(StringComparer.Ordinal);
		public readonly Dictionary<string, List<SelectorEntry>> byType = new Dictionary<string, List<SelectorEntry>>(StringComparer.Ordinal);
		public readonly List<SelectorEntry> universal = new List<SelectorEntry>();

		#endregion

		#region Constructors

		public SelectorIndex(StyleSheet sheet)
		{
			foreach (StyleRule rule in sheet.rules)
			{
				foreach (StyleSelector selector in rule.selectors)
				{
					SimpleSelector last = selector.rightmost;
					if (last.neverMatches)
					{
						continue;
					}

					var entry = new SelectorEntry { rule = rule, selector = selector };
					if (last.name != null)
					{
						Add(byName, last.name, entry);
					}
					else if (last.classes.Length > 0)
					{
						Add(byClass, last.classes[0], entry);
					}
					else if (last.type != null)
					{
						Add(byType, last.type, entry);
					}
					else
					{
						universal.Add(entry);
					}
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void Add(Dictionary<string, List<SelectorEntry>> map, string key, SelectorEntry entry)
		{
			List<SelectorEntry> list;
			if (!map.TryGetValue(key, out list))
			{
				list = new List<SelectorEntry>();
				map[key] = list;
			}

			list.Add(entry);
		}

		#endregion
	}

	internal struct SelectorEntry
	{
		public StyleRule rule;
		public StyleSelector selector;
	}
}
