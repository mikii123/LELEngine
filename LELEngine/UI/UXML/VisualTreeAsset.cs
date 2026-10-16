using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace LELEngine.UIElements
{
	/// <summary>The element a UXML template instantiates into (its children are the template's root elements).</summary>
	[UxmlElement]
	public class TemplateContainer : VisualElement
	{
		public static readonly string ussClassName = "unity-template-container";

		public string templateId { get; internal set; }
		public VisualTreeAsset templateSource { get; internal set; }

		public TemplateContainer()
		{
			AddToClassList(ussClassName);
			pickingMode = PickingMode.Ignore;
		}

		public TemplateContainer(string templateId)
			: this()
		{
			this.templateId = templateId;
			name = templateId;
		}
	}

	/// <summary>
	///     A parsed UXML document (.uxml asset): the element tree, the style sheets it references (&lt;Style src&gt;) and
	///     the templates it imports (&lt;Template name src&gt;). Instantiate it with <see cref="Instantiate" /> or clone
	///     it into an existing element with <see cref="CloneTree(VisualElement)" />.
	/// </summary>
	public sealed class VisualTreeAsset : IAsset
	{
		#region PublicFields

		public string AssetPath { get; internal set; }
		public string name;
		public List<StyleSheet> stylesheets { get; } = new List<StyleSheet>();
		public List<string> errors { get; } = new List<string>();

		/// <summary>Bumped when the asset is re-read from disk.</summary>
		public int version { get; private set; }

		#endregion

		#region PrivateFields

		private readonly List<UxmlNode> roots = new List<UxmlNode>();
		private readonly Dictionary<string, VisualTreeAsset> templates = new Dictionary<string, VisualTreeAsset>(StringComparer.Ordinal);
		private static readonly HashSet<VisualTreeAsset> instantiating = new HashSet<VisualTreeAsset>();

		#endregion

		#region PublicMethods

		public static VisualTreeAsset Parse(string xml, string assetPath = null, string name = null)
		{
			var asset = new VisualTreeAsset { AssetPath = assetPath, name = name ?? (assetPath != null ? Path.GetFileNameWithoutExtension(assetPath) : "runtime") };
			asset.ParseInto(xml);
			return asset;
		}

		public void Reparse(string xml)
		{
			roots.Clear();
			stylesheets.Clear();
			templates.Clear();
			errors.Clear();
			ParseInto(xml);
			version++;
		}

		/// <summary>Creates the tree inside a new <see cref="TemplateContainer" /> carrying the asset's style sheets.</summary>
		public TemplateContainer Instantiate()
		{
			var container = new TemplateContainer(name) { templateSource = this };
			CloneTree(container);
			return container;
		}

		public TemplateContainer Instantiate(string bindingPath)
		{
			return Instantiate();
		}

		/// <summary>Adds the asset's root elements (and style sheets) to <paramref name="target" />.</summary>
		public void CloneTree(VisualElement target)
		{
			int first, count;
			CloneTree(target, out first, out count);
		}

		public void CloneTree(VisualElement target, out int firstElementIndex, out int elementAddedCount)
		{
			firstElementIndex = target.childCount;
			elementAddedCount = 0;
			if (!instantiating.Add(this))
			{
				Debug.LogWarning("[UXML] " + name + " instantiates itself recursively");
				return;
			}

			try
			{
				foreach (StyleSheet sheet in stylesheets)
				{
					target.styleSheets.Add(sheet);
				}

				foreach (UxmlNode node in roots)
				{
					VisualElement element = Create(node);
					if (element != null)
					{
						target.Add(element);
						elementAddedCount++;
					}
				}
			}
			finally
			{
				instantiating.Remove(this);
			}
		}

		/// <summary>Resolves a src path of a UXML / USS file: relative to the file, "/Assets/..." or "project://database/Assets/...".</summary>
		public static string ResolveSourcePath(string src, string assetPath)
		{
			if (string.IsNullOrWhiteSpace(src))
			{
				return null;
			}

			src = src.Trim().Replace('\\', '/');
			if (src.StartsWith("project://database/", StringComparison.OrdinalIgnoreCase))
			{
				src = src.Substring("project://database/".Length);
				int query = src.IndexOf('?');
				if (query >= 0) src = src.Substring(0, query);
			}

			if (src.StartsWith("project:/", StringComparison.OrdinalIgnoreCase))
			{
				src = src.Substring("project:/".Length);
			}

			if (src.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
			{
				src = src.Substring("Assets/".Length);
			}
			else if (src.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase))
			{
				src = src.Substring("/Assets/".Length);
			}
			else if (src.StartsWith("/"))
			{
				src = src.TrimStart('/');
			}
			else if (assetPath != null)
			{
				string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
				src = folder.Length > 0 ? folder + "/" + src : src;
			}

			var parts = new List<string>();
			foreach (string part in src.Split('/'))
			{
				if (part.Length == 0 || part == ".") continue;
				if (part == "..")
				{
					if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
					continue;
				}

				parts.Add(part);
			}

			string normalized = string.Join("/", parts);
			return AssetDatabase.Resolve(normalized, null) ?? normalized;
		}

		public override string ToString()
		{
			return "VisualTreeAsset(" + (AssetPath ?? name) + ")";
		}

		#endregion

		#region PrivateMethods

		// ------------------------------------------------------------------ parsing

		private void ParseInto(string xml)
		{
			var document = new XmlDocument { XmlResolver = null };
			try
			{
				document.LoadXml(xml);
			}
			catch (XmlException e)
			{
				errors.Add(e.Message);
				Debug.LogWarning("[UXML] " + name + ": " + e.Message);
				return;
			}

			XmlElement root = document.DocumentElement;
			if (root == null)
			{
				return;
			}

			// The root is <UXML> (any prefix); a file holding one element directly is accepted too.
			if (!string.Equals(root.LocalName, "UXML", StringComparison.OrdinalIgnoreCase))
			{
				UxmlNode single = ParseElement(root);
				if (single != null) roots.Add(single);
				return;
			}

			foreach (XmlNode child in root.ChildNodes)
			{
				var element = child as XmlElement;
				if (element == null)
				{
					continue;
				}

				switch (element.LocalName)
				{
					case "Style":
					{
						string src = element.GetAttribute("src");
						string path = ResolveSourcePath(src, AssetPath);
						StyleSheet sheet = path != null ? UIAssets.LoadStyleSheet(path) : null;
						if (sheet != null)
						{
							stylesheets.Add(sheet);
						}
						else
						{
							errors.Add("Style sheet not found: " + src);
							Debug.LogWarning("[UXML] " + name + ": style sheet not found: " + src);
						}

						break;
					}

					case "Template":
					{
						string templateName = element.GetAttribute("name");
						string src = element.GetAttribute("src");
						string path = ResolveSourcePath(src, AssetPath);
						VisualTreeAsset template = path != null ? UIAssets.LoadVisualTree(path) : null;
						if (template != null && !string.IsNullOrEmpty(templateName))
						{
							templates[templateName] = template;
						}
						else
						{
							errors.Add("Template not found: " + src);
							Debug.LogWarning("[UXML] " + name + ": template not found: " + src);
						}

						break;
					}

					default:
					{
						UxmlNode node = ParseElement(element);
						if (node != null) roots.Add(node);
						break;
					}
				}
			}
		}

		private UxmlNode ParseElement(XmlElement element)
		{
			var node = new UxmlNode { typeName = element.LocalName };
			foreach (XmlAttribute attribute in element.Attributes)
			{
				if (attribute.Name.StartsWith("xmlns", StringComparison.Ordinal) || attribute.Name.StartsWith("xsi:", StringComparison.Ordinal))
				{
					continue;
				}

				node.attributes[attribute.LocalName] = attribute.Value;
			}

			foreach (XmlNode child in element.ChildNodes)
			{
				if (child is XmlElement childElement)
				{
					if (string.Equals(childElement.LocalName, "Style", StringComparison.Ordinal))
					{
						// Nested <Style> applies its sheet to this element.
						string path = ResolveSourcePath(childElement.GetAttribute("src"), AssetPath);
						StyleSheet sheet = path != null ? UIAssets.LoadStyleSheet(path) : null;
						if (sheet != null) node.styleSheets.Add(sheet);
						continue;
					}

					if (string.Equals(childElement.LocalName, "Bindings", StringComparison.Ordinal))
					{
						// <Bindings><DataBinding property="text" data-source-path="name" binding-mode="ToTarget" /></Bindings>
						foreach (XmlNode bindingNode in childElement.ChildNodes)
						{
							if (bindingNode is XmlElement bindingElement && string.Equals(bindingElement.LocalName, "DataBinding", StringComparison.Ordinal))
							{
								var binding = new UxmlBindingNode();
								foreach (XmlAttribute attribute in bindingElement.Attributes)
								{
									binding.attributes[attribute.LocalName] = attribute.Value;
								}

								node.bindings.Add(binding);
							}
						}

						continue;
					}

					UxmlNode childNode = ParseElement(childElement);
					if (childNode != null) node.children.Add(childNode);
				}
				else if (child is XmlText text && !string.IsNullOrWhiteSpace(text.Value) && !node.attributes.ContainsKey("text"))
				{
					node.attributes["text"] = text.Value.Trim();
				}
			}

			return node;
		}

		// ------------------------------------------------------------------ instantiation

		private VisualElement Create(UxmlNode node)
		{
			VisualElement element;
			if (string.Equals(node.typeName, "Instance", StringComparison.Ordinal))
			{
				string templateName;
				VisualTreeAsset template;
				if (!node.attributes.TryGetValue("template", out templateName) || !templates.TryGetValue(templateName, out template))
				{
					Debug.LogWarning("[UXML] " + name + ": unknown template in <Instance>");
					return null;
				}

				TemplateContainer container = template.Instantiate();
				container.templateId = templateName;
				element = container;
			}
			else
			{
				Type type = UxmlRegistry.Resolve(node.typeName);
				if (type == null)
				{
					Debug.LogWarning("[UXML] " + name + ": unknown element <" + node.typeName + ">, using VisualElement");
					type = typeof(VisualElement);
				}

				try
				{
					element = (VisualElement)Activator.CreateInstance(type);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
					return null;
				}
			}

			ApplyAttributes(element, node);
			foreach (StyleSheet sheet in node.styleSheets)
			{
				element.styleSheets.Add(sheet);
			}

			foreach (UxmlBindingNode bindingNode in node.bindings)
			{
				ApplyBinding(element, bindingNode);
			}

			foreach (UxmlNode child in node.children)
			{
				VisualElement childElement = Create(child);
				if (childElement != null)
				{
					element.Add(childElement);
				}
			}

			return element;
		}

		private void ApplyAttributes(VisualElement element, UxmlNode node)
		{
			UxmlTypeInfo info = UxmlRegistry.GetInfo(element.GetType());
			foreach (KeyValuePair<string, string> pair in node.attributes)
			{
				string key = pair.Key;
				string value = pair.Value;
				switch (key)
				{
					case "name": element.name = value; continue;
					case "class":
						foreach (string className in value.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
						{
							element.AddToClassList(className);
						}

						continue;
					case "style": ((InlineStyle)element.style).SetFromDeclarations(value, AssetPath); continue;
					case "tooltip": element.tooltip = value; continue;
					case "picking-mode":
					{
						object mode;
						if (UxmlRegistry.TryConvert(value, typeof(PickingMode), AssetPath, out mode)) element.pickingMode = (PickingMode)mode;
						continue;
					}

					case "focusable":
					{
						bool focusable;
						if (bool.TryParse(value, out focusable)) element.focusable = focusable;
						continue;
					}

					case "tabindex":
					{
						int index;
						if (int.TryParse(value, out index)) element.tabIndex = index;
						continue;
					}

					case "view-data-key": element.viewDataKey = value; continue;
					case "usage-hints":
					{
						object hints;
						if (UxmlRegistry.TryConvert(value, typeof(UsageHints), AssetPath, out hints)) element.usageHints = (UsageHints)hints;
						continue;
					}

					case "enabled":
					{
						bool enabled;
						if (bool.TryParse(value, out enabled)) element.SetEnabled(enabled);
						continue;
					}

					case "template": continue;
					case "binding-path": continue;
					case "data-source": continue;
					case "data-source-type": element.dataSourceTypeName = value; continue;
					case "data-source-path": element.dataSourcePath = new PropertyPath(value); continue;
				}

				UxmlAttributeInfo attribute;
				if (!info.TryGet(key, out attribute))
				{
					Debug.LogWarning("[UXML] " + name + ": <" + node.typeName + "> has no attribute '" + key + "'");
					continue;
				}

				object converted;
				if (!UxmlRegistry.TryConvert(value, attribute.valueType, AssetPath, out converted))
				{
					Debug.LogWarning("[UXML] " + name + ": cannot convert '" + value + "' for attribute '" + key + "' of <" + node.typeName + ">");
					continue;
				}

				try
				{
					attribute.SetValue(element, converted);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}

		#endregion

		private void ApplyBinding(VisualElement element, UxmlBindingNode node)
		{
			string property;
			if (!node.attributes.TryGetValue("property", out property) || string.IsNullOrWhiteSpace(property))
			{
				Debug.LogWarning("[UXML] " + name + ": <DataBinding> without a property");
				return;
			}

			var binding = new DataBinding();
			string value;
			if (node.attributes.TryGetValue("data-source-path", out value)) binding.dataSourcePath = new PropertyPath(value);
			if (node.attributes.TryGetValue("binding-mode", out value))
			{
				object mode;
				if (UxmlRegistry.TryConvert(value, typeof(BindingMode), AssetPath, out mode)) binding.bindingMode = (BindingMode)mode;
			}

			if (node.attributes.TryGetValue("update-trigger", out value))
			{
				object trigger;
				if (UxmlRegistry.TryConvert(value, typeof(BindingUpdateTrigger), AssetPath, out trigger)) binding.updateTrigger = (BindingUpdateTrigger)trigger;
			}

			if (node.attributes.TryGetValue("source-to-ui-converters", out value)) binding.sourceToUiConverters = ConverterGroups.GetConverterGroup(value.Trim());
			if (node.attributes.TryGetValue("ui-to-source-converters", out value)) binding.uiToSourceConverters = ConverterGroups.GetConverterGroup(value.Trim());
			if (node.attributes.TryGetValue("data-source-type", out value))
			{
				Type type = TypeRegistry.Resolve(value);
				if (type != null) binding.dataSourceType = type;
			}

			element.SetBinding(new BindingId(property.Trim()), binding);
		}

		#region NestedTypes

		private sealed class UxmlNode
		{
			public string typeName;
			public readonly Dictionary<string, string> attributes = new Dictionary<string, string>(StringComparer.Ordinal);
			public readonly List<UxmlNode> children = new List<UxmlNode>();
			public readonly List<StyleSheet> styleSheets = new List<StyleSheet>();
			public readonly List<UxmlBindingNode> bindings = new List<UxmlBindingNode>();
		}

		private sealed class UxmlBindingNode
		{
			public readonly Dictionary<string, string> attributes = new Dictionary<string, string>(StringComparer.Ordinal);
		}

		#endregion
	}
}
