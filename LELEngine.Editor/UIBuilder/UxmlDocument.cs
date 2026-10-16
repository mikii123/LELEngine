using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using LELEngine.UIElements;

namespace LELEngine.Editor.UIBuilder
{
	/// <summary>
	///     An element of a UXML document as authored: its tag, attributes (as strings, in file order), bindings,
	///     nested style sheets and children. The builder edits this model and re-instantiates the preview from it.
	/// </summary>
	internal sealed class UxmlNode
	{
		#region PublicFields

		public readonly int id = ++nextId;
		public string tag;
		public readonly List<KeyValuePair<string, string>> attributes = new List<KeyValuePair<string, string>>();
		public readonly List<UxmlNode> children = new List<UxmlNode>();
		public readonly List<Dictionary<string, string>> bindings = new List<Dictionary<string, string>>();
		public readonly List<string> styleSheets = new List<string>();

		public string name => GetAttribute("name");
		public bool isInstance => tag == "Instance";

		#endregion

		#region PrivateFields

		private static int nextId;

		#endregion

		#region PublicMethods

		public string GetAttribute(string key)
		{
			foreach (KeyValuePair<string, string> pair in attributes)
			{
				if (pair.Key == key)
				{
					return pair.Value;
				}
			}

			return null;
		}

		/// <summary>Sets an attribute; null or empty removes it.</summary>
		public void SetAttribute(string key, string value)
		{
			for (int i = 0; i < attributes.Count; i++)
			{
				if (attributes[i].Key != key)
				{
					continue;
				}

				if (string.IsNullOrEmpty(value))
				{
					attributes.RemoveAt(i);
				}
				else
				{
					attributes[i] = new KeyValuePair<string, string>(key, value);
				}

				return;
			}

			if (!string.IsNullOrEmpty(value))
			{
				attributes.Add(new KeyValuePair<string, string>(key, value));
			}
		}

		/// <summary>Value of a property in the inline style attribute, or null.</summary>
		public string GetStyleProperty(string property)
		{
			foreach (KeyValuePair<string, string> declaration in ParseStyle(GetAttribute("style")))
			{
				if (declaration.Key == property)
				{
					return declaration.Value;
				}
			}

			return null;
		}

		/// <summary>Sets (or removes with null / empty) a property of the inline style attribute.</summary>
		public void SetStyleProperty(string property, string value)
		{
			List<KeyValuePair<string, string>> declarations = ParseStyle(GetAttribute("style"));
			bool found = false;
			for (int i = 0; i < declarations.Count; i++)
			{
				if (declarations[i].Key != property)
				{
					continue;
				}

				found = true;
				if (string.IsNullOrWhiteSpace(value))
				{
					declarations.RemoveAt(i);
				}
				else
				{
					declarations[i] = new KeyValuePair<string, string>(property, value.Trim());
				}

				break;
			}

			if (!found && !string.IsNullOrWhiteSpace(value))
			{
				declarations.Add(new KeyValuePair<string, string>(property, value.Trim()));
			}

			SetAttribute("style", FormatStyle(declarations));
		}

		public UxmlNode Clone()
		{
			var copy = new UxmlNode { tag = tag };
			copy.attributes.AddRange(attributes);
			copy.styleSheets.AddRange(styleSheets);
			foreach (Dictionary<string, string> binding in bindings)
			{
				copy.bindings.Add(new Dictionary<string, string>(binding, StringComparer.Ordinal));
			}

			foreach (UxmlNode child in children)
			{
				copy.children.Add(child.Clone());
			}

			return copy;
		}

		public override string ToString()
		{
			string n = name;
			return tag + (string.IsNullOrEmpty(n) ? "" : " #" + n);
		}

		/// <summary>Splits "a: 1; b: 2" into pairs (semicolons inside parentheses and quotes are kept).</summary>
		public static List<KeyValuePair<string, string>> ParseStyle(string style)
		{
			var result = new List<KeyValuePair<string, string>>();
			if (string.IsNullOrWhiteSpace(style))
			{
				return result;
			}

			var current = new StringBuilder();
			int depth = 0;
			char quote = '\0';
			void Flush()
			{
				string declaration = current.ToString().Trim();
				current.Clear();
				int colon = declaration.IndexOf(':');
				if (colon > 0)
				{
					result.Add(new KeyValuePair<string, string>(declaration.Substring(0, colon).Trim(), declaration.Substring(colon + 1).Trim()));
				}
			}

			foreach (char c in style)
			{
				if (quote != '\0')
				{
					current.Append(c);
					if (c == quote) quote = '\0';
					continue;
				}

				if (c == '"' || c == '\'')
				{
					quote = c;
					current.Append(c);
				}
				else if (c == '(')
				{
					depth++;
					current.Append(c);
				}
				else if (c == ')')
				{
					depth = Math.Max(0, depth - 1);
					current.Append(c);
				}
				else if (c == ';' && depth == 0)
				{
					Flush();
				}
				else
				{
					current.Append(c);
				}
			}

			Flush();
			return result;
		}

		public static string FormatStyle(List<KeyValuePair<string, string>> declarations)
		{
			if (declarations.Count == 0)
			{
				return null;
			}

			var builder = new StringBuilder();
			foreach (KeyValuePair<string, string> declaration in declarations)
			{
				if (builder.Length > 0) builder.Append(' ');
				builder.Append(declaration.Key).Append(": ").Append(declaration.Value).Append(';');
			}

			return builder.ToString();
		}

		#endregion
	}

	/// <summary>A UXML file as a tree of <see cref="UxmlNode" />: parsed from and written back to XML.</summary>
	internal sealed class UxmlDocument
	{
		#region PublicFields

		/// <summary>Root-relative asset path of the file (null for an unsaved document).</summary>
		public string assetPath;

		/// <summary>&lt;Style src&gt; entries at the document level, as written.</summary>
		public readonly List<string> styleSheets = new List<string>();

		/// <summary>&lt;Template name src&gt; entries.</summary>
		public readonly List<KeyValuePair<string, string>> templates = new List<KeyValuePair<string, string>>();

		/// <summary>Virtual root holding the top-level elements.</summary>
		public readonly UxmlNode root = new UxmlNode { tag = "UXML" };

		#endregion

		#region PublicMethods

		public static UxmlDocument Parse(string xml, string assetPath)
		{
			var document = new UxmlDocument { assetPath = assetPath };
			if (string.IsNullOrWhiteSpace(xml))
			{
				return document;
			}

			var xmlDocument = new XmlDocument { XmlResolver = null };
			xmlDocument.LoadXml(xml);
			XmlElement rootElement = xmlDocument.DocumentElement;
			if (rootElement == null)
			{
				return document;
			}

			if (!string.Equals(rootElement.LocalName, "UXML", StringComparison.OrdinalIgnoreCase))
			{
				document.root.children.Add(ParseElement(rootElement));
				return document;
			}

			foreach (XmlNode child in rootElement.ChildNodes)
			{
				if (!(child is XmlElement element))
				{
					continue;
				}

				switch (element.LocalName)
				{
					case "Style":
						document.styleSheets.Add(element.GetAttribute("src"));
						break;
					case "Template":
						document.templates.Add(new KeyValuePair<string, string>(element.GetAttribute("name"), element.GetAttribute("src")));
						break;
					default:
						document.root.children.Add(ParseElement(element));
						break;
				}
			}

			return document;
		}

		public string ToXml()
		{
			var settings = new XmlWriterSettings { Indent = true, IndentChars = "    ", OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
			var builder = new StringBuilder();
			using (XmlWriter writer = XmlWriter.Create(builder, settings))
			{
				writer.WriteStartElement("ui", "UXML", "UnityEngine.UIElements");
				foreach (string sheet in styleSheets)
				{
					writer.WriteStartElement("Style");
					writer.WriteAttributeString("src", sheet);
					writer.WriteEndElement();
				}

				foreach (KeyValuePair<string, string> template in templates)
				{
					writer.WriteStartElement("Template");
					writer.WriteAttributeString("name", template.Key);
					writer.WriteAttributeString("src", template.Value);
					writer.WriteEndElement();
				}

				foreach (UxmlNode node in root.children)
				{
					WriteNode(writer, node);
				}

				writer.WriteEndElement();
			}

			return builder.ToString() + "\n";
		}

		public UxmlNode ParentOf(UxmlNode node)
		{
			return FindParent(root, node);
		}

		public UxmlNode FindById(int id)
		{
			return Find(root, n => n.id == id);
		}

		public UxmlNode Find(Func<UxmlNode, bool> predicate)
		{
			return Find(root, predicate);
		}

		/// <summary>Index path of a node from the root ("0/2/1"), stable across re-parses.</summary>
		public string PathOf(UxmlNode node)
		{
			var indices = new List<int>();
			for (UxmlNode current = node; current != null && current != root; current = ParentOf(current))
			{
				UxmlNode parent = ParentOf(current);
				if (parent == null) return null;
				indices.Insert(0, parent.children.IndexOf(current));
			}

			return string.Join("/", indices);
		}

		public UxmlNode NodeAt(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return null;
			}

			UxmlNode current = root;
			foreach (string part in path.Split('/'))
			{
				int index;
				if (!int.TryParse(part, out index) || index < 0 || index >= current.children.Count)
				{
					return null;
				}

				current = current.children[index];
			}

			return current;
		}

		/// <summary>Resolves a src attribute of this document to a root-relative asset path.</summary>
		public string ResolveSource(string src)
		{
			return VisualTreeAsset.ResolveSourcePath(src, assetPath);
		}

		/// <summary>Path to write into a src attribute for an asset: relative to this document's folder.</summary>
		public string RelativeSource(string targetAssetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return "/" + targetAssetPath;
			}

			string folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
			if (folder.Length == 0)
			{
				return targetAssetPath;
			}

			if (targetAssetPath.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
			{
				return targetAssetPath.Substring(folder.Length + 1);
			}

			// Walk up as far as needed.
			string[] from = folder.Split('/');
			string[] to = targetAssetPath.Split('/');
			int common = 0;
			while (common < from.Length && common < to.Length - 1 && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase))
			{
				common++;
			}

			var builder = new StringBuilder();
			for (int i = common; i < from.Length; i++) builder.Append("../");
			for (int i = common; i < to.Length; i++)
			{
				builder.Append(to[i]);
				if (i < to.Length - 1) builder.Append('/');
			}

			return builder.ToString();
		}

		#endregion

		#region PrivateMethods

		private static UxmlNode ParseElement(XmlElement element)
		{
			var node = new UxmlNode { tag = element.LocalName };
			foreach (XmlAttribute attribute in element.Attributes)
			{
				if (attribute.Name.StartsWith("xmlns", StringComparison.Ordinal))
				{
					continue;
				}

				node.attributes.Add(new KeyValuePair<string, string>(attribute.LocalName, attribute.Value));
			}

			foreach (XmlNode child in element.ChildNodes)
			{
				if (!(child is XmlElement childElement))
				{
					if (child is XmlText text && !string.IsNullOrWhiteSpace(text.Value) && node.GetAttribute("text") == null)
					{
						node.SetAttribute("text", text.Value.Trim());
					}

					continue;
				}

				switch (childElement.LocalName)
				{
					case "Style":
						node.styleSheets.Add(childElement.GetAttribute("src"));
						break;
					case "Bindings":
						foreach (XmlNode bindingNode in childElement.ChildNodes)
						{
							if (bindingNode is XmlElement bindingElement)
							{
								var binding = new Dictionary<string, string>(StringComparer.Ordinal);
								foreach (XmlAttribute attribute in bindingElement.Attributes)
								{
									binding[attribute.LocalName] = attribute.Value;
								}

								node.bindings.Add(binding);
							}
						}

						break;
					default:
						node.children.Add(ParseElement(childElement));
						break;
				}
			}

			return node;
		}

		private static void WriteNode(XmlWriter writer, UxmlNode node)
		{
			bool builtIn = node.isInstance || IsEngineType(node.tag);
			if (builtIn)
			{
				writer.WriteStartElement("ui", node.tag, "UnityEngine.UIElements");
			}
			else
			{
				writer.WriteStartElement(node.tag);
			}

			foreach (KeyValuePair<string, string> attribute in node.attributes)
			{
				writer.WriteAttributeString(attribute.Key, attribute.Value ?? "");
			}

			foreach (string sheet in node.styleSheets)
			{
				writer.WriteStartElement("Style");
				writer.WriteAttributeString("src", sheet);
				writer.WriteEndElement();
			}

			if (node.bindings.Count > 0)
			{
				writer.WriteStartElement("Bindings");
				foreach (Dictionary<string, string> binding in node.bindings)
				{
					writer.WriteStartElement("ui", "DataBinding", "UnityEngine.UIElements");
					foreach (KeyValuePair<string, string> attribute in binding)
					{
						writer.WriteAttributeString(attribute.Key, attribute.Value ?? "");
					}

					writer.WriteEndElement();
				}

				writer.WriteEndElement();
			}

			foreach (UxmlNode child in node.children)
			{
				WriteNode(writer, child);
			}

			writer.WriteEndElement();
		}

		private static bool IsEngineType(string tag)
		{
			Type type = UxmlRegistry.Resolve(tag);
			return type == null || type.Assembly == typeof(VisualElement).Assembly;
		}

		private static UxmlNode FindParent(UxmlNode parent, UxmlNode target)
		{
			foreach (UxmlNode child in parent.children)
			{
				if (child == target)
				{
					return parent;
				}

				UxmlNode found = FindParent(child, target);
				if (found != null)
				{
					return found;
				}
			}

			return null;
		}

		private static UxmlNode Find(UxmlNode parent, Func<UxmlNode, bool> predicate)
		{
			foreach (UxmlNode child in parent.children)
			{
				if (predicate(child))
				{
					return child;
				}

				UxmlNode found = Find(child, predicate);
				if (found != null)
				{
					return found;
				}
			}

			return null;
		}

		#endregion
	}
}
