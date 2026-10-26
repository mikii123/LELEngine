using System;
using System.Collections.Generic;
using System.Globalization;
using Hexa.NET.ImGui;
using LELEngine.UIElements;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.UIBuilder
{
	/// <summary>Inspector of the selected element: UXML attributes, bindings and inline USS styles.</summary>
	internal sealed partial class UIBuilder
	{
		#region PrivateFields

		private const float LabelWidth = 120f;

		private static readonly string[] CommonAttributes = { "name", "class", "style", "tooltip", "picking-mode", "focusable", "tabindex", "enabled", "data-source-path", "data-source-type", "usage-hints", "view-data-key", "template" };

		private static readonly string[] BindingModes = { "TwoWay", "ToSource", "ToTarget", "ToTargetOnce" };
		private static readonly string[] UpdateTriggers = { "OnSourceChanged", "EveryUpdate", "WhenDirty" };

		#endregion

		#region PrivateMethods

		private void DrawInspector()
		{
			if (ImGui.Begin("Inspector"))
			{
				UxmlNode node = Selected;
				if (node == null)
				{
					ImGui.TextDisabled("Select an element in the hierarchy or the canvas.");
				}
				else
				{
					ImGui.TextUnformatted(node.isInstance ? "Template instance" : node.tag);
					ImGui.SameLine();
					ImGui.TextDisabled(selectedPath);
					if (EditorGui.CollapsingHeader("Attributes", ImGuiTreeNodeFlags.DefaultOpen))
					{
						DrawAttributes(node);
					}

					// The label carries the count, so a bound element's section starts open (its own stored state).
					string bindingsHeader = node.bindings.Count > 0 ? "Bindings (" + node.bindings.Count + ")" : "Bindings";
					if (EditorGui.CollapsingHeader(bindingsHeader, node.bindings.Count > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
					{
						DrawBindings(node);
					}

					if (EditorGui.CollapsingHeader("Inline Styles", ImGuiTreeNodeFlags.DefaultOpen))
					{
						DrawInlineStyles(node);
					}
				}
			}

			ImGui.End();
		}

		// ------------------------------------------------------------------ attributes

		private void DrawAttributes(UxmlNode node)
		{
			AttributeText(node, "Name", "name");
			AttributeText(node, "Class", "class");
			AttributeText(node, "Tooltip", "tooltip");
			AttributeCombo(node, "Picking Mode", "picking-mode", new[] { "Position", "Ignore" }, "Position");
			AttributeBool(node, "Focusable", "focusable", false);
			AttributeInt(node, "Tab Index", "tabindex", 0);
			AttributeBool(node, "Enabled", "enabled", true);

			if (node.isInstance)
			{
				AttributeText(node, "Template", "template");
				return;
			}

			Type type = UxmlRegistry.Resolve(node.tag);
			if (type == null)
			{
				ImGui.TextColored(EditorStyle.Warning, "Unknown element type: " + node.tag);
				return;
			}

			VisualElement element = preview.ElementOf(node);
			UxmlTypeInfo info = UxmlRegistry.GetInfo(type);
			var names = new List<string>(info.attributes.Keys);
			names.Sort(StringComparer.Ordinal);
			bool any = false;
			foreach (string key in names)
			{
				if (Array.IndexOf(CommonAttributes, key) >= 0)
				{
					continue;
				}

				if (!any)
				{
					ImGui.SeparatorText(type.Name);
					any = true;
				}

				DrawTypedAttribute(node, key, info.attributes[key], element);
			}
		}

		private void DrawTypedAttribute(UxmlNode node, string key, UxmlAttributeInfo info, VisualElement element)
		{
			Type valueType = info.valueType;
			string label = Serialization.SerializationUtility.NicifyName(info.memberName);
			object current = null;
			if (element != null)
			{
				try
				{
					current = info.GetValue(element);
				}
				catch (Exception)
				{
					current = null;
				}
			}

			string attribute = node.GetAttribute(key);
			if (valueType == typeof(bool))
			{
				bool value = attribute != null ? string.Equals(attribute, "true", StringComparison.OrdinalIgnoreCase) : current is bool b && b;
				AttributeBool(node, label, key, value);
			}
			else if (valueType == typeof(int) || valueType == typeof(long))
			{
				int value = attribute != null ? (int)ParseFloat(attribute, 0f) : current != null ? Convert.ToInt32(current) : 0;
				AttributeInt(node, label, key, value);
			}
			else if (valueType == typeof(float) || valueType == typeof(double))
			{
				float value = attribute != null ? ParseFloat(attribute, 0f) : current != null ? Convert.ToSingle(current) : 0f;
				Label(label);
				bool changed = EditorGui.DragFloat("##" + key, ref value, 0.1f);
				Track(changed, () => node.SetAttribute(key, value.ToString("0.###", CultureInfo.InvariantCulture)));
			}
			else if (valueType.IsEnum)
			{
				string[] options = Enum.GetNames(valueType);
				string fallback = current != null ? current.ToString() : options.Length > 0 ? options[0] : "";
				AttributeCombo(node, label, key, options, fallback);
			}
			else if (valueType == typeof(Color4))
			{
				Color4 color = current is Color4 c ? c : new Color4(1f, 1f, 1f, 1f);
				Color4 parsed;
				if (attribute != null && StyleColorUtility.TryParse(attribute, out parsed)) color = parsed;
				var n = new NVector4(color.R, color.G, color.B, color.A);
				Label(label);
				bool changed = EditorGui.ColorEdit4("##" + key, ref n, ImGuiColorEditFlags.AlphaBar);
				Track(changed, () => node.SetAttribute(key, StyleColorUtility.ToString(new Color4(n.X, n.Y, n.Z, n.W))));
			}
			else
			{
				// Strings, lists (comma separated), asset paths, anything else as text.
				string text = attribute ?? (current is string s ? s : current is System.Collections.IEnumerable list && !(current is string) ? Join(list) : current?.ToString() ?? "");
				Label(label);
				bool changed = EditorGui.InputText("##" + key, ref text, 2048u);
				Track(changed, () => node.SetAttribute(key, text));
			}
		}

		private static string Join(System.Collections.IEnumerable list)
		{
			var parts = new List<string>();
			foreach (object item in list)
			{
				parts.Add(item?.ToString() ?? "");
			}

			return string.Join(",", parts);
		}

		private void AttributeText(UxmlNode node, string label, string key)
		{
			string value = node.GetAttribute(key) ?? "";
			Label(label);
			bool changed = EditorGui.InputText("##" + key, ref value, 2048u);
			Track(changed, () => node.SetAttribute(key, value));
		}

		private void AttributeBool(UxmlNode node, string label, string key, bool fallback)
		{
			string attribute = node.GetAttribute(key);
			bool value = attribute != null ? string.Equals(attribute, "true", StringComparison.OrdinalIgnoreCase) : fallback;
			Label(label);
			if (EditorGui.Checkbox("##" + key, ref value))
			{
				Apply(() => node.SetAttribute(key, value == fallback && attribute == null ? null : value ? "true" : "false"));
			}
		}

		private void AttributeInt(UxmlNode node, string label, string key, int fallback)
		{
			string attribute = node.GetAttribute(key);
			int value = attribute != null ? (int)ParseFloat(attribute, fallback) : fallback;
			Label(label);
			bool changed = EditorGui.InputInt("##" + key, ref value);
			Track(changed, () => node.SetAttribute(key, value.ToString(CultureInfo.InvariantCulture)));
		}

		private void AttributeCombo(UxmlNode node, string label, string key, string[] options, string fallback)
		{
			string current = node.GetAttribute(key) ?? fallback;
			Label(label);
			if (EditorGui.BeginCombo("##" + key, current))
			{
				foreach (string option in options)
				{
					if (EditorGui.Selectable(option, string.Equals(option, current, StringComparison.OrdinalIgnoreCase)))
					{
						string chosen = option;
						Apply(() => node.SetAttribute(key, chosen));
					}
				}

				EditorGui.EndCombo();
			}
		}

		// ------------------------------------------------------------------ bindings

		private void DrawBindings(UxmlNode node)
		{
			AttributeText(node, "Data Source Path", "data-source-path");
			AttributeText(node, "Data Source Type", "data-source-type");
			ImGui.TextDisabled("Bindings of this element's properties:");
			for (int i = 0; i < node.bindings.Count; i++)
			{
				Dictionary<string, string> binding = node.bindings[i];
				ImGui.PushID(i);
				string property = binding.TryGetValue("property", out string p) ? p : "";
				string path = binding.TryGetValue("data-source-path", out string dp) ? dp : "";
				string mode = binding.TryGetValue("binding-mode", out string bm) ? bm : "TwoWay";
				string trigger = binding.TryGetValue("update-trigger", out string ut) ? ut : "OnSourceChanged";

				Label("Property");
				bool changed = EditorGui.InputText("##bprop", ref property, 128u);
				Track(changed, () => binding["property"] = property);
				Label("Source Path");
				changed = EditorGui.InputText("##bpath", ref path, 256u);
				Track(changed, () => binding["data-source-path"] = path);
				Label("Mode");
				if (EditorGui.BeginCombo("##bmode", mode))
				{
					foreach (string option in BindingModes)
					{
						if (EditorGui.Selectable(option, option == mode))
						{
							string chosen = option;
							Apply(() => binding["binding-mode"] = chosen);
						}
					}

					EditorGui.EndCombo();
				}

				Label("Trigger");
				if (EditorGui.BeginCombo("##btrigger", trigger))
				{
					foreach (string option in UpdateTriggers)
					{
						if (EditorGui.Selectable(option, option == trigger))
						{
							string chosen = option;
							Apply(() => binding["update-trigger"] = chosen);
						}
					}

					EditorGui.EndCombo();
				}

				if (EditorGui.SmallButton("Remove binding"))
				{
					int index = i;
					Apply(() => node.bindings.RemoveAt(index));
				}

				ImGui.Separator();
				ImGui.PopID();
			}

			if (EditorGui.Button("Add binding"))
			{
				Apply(() => node.bindings.Add(new Dictionary<string, string>(StringComparer.Ordinal)
				{
					{ "property", node.tag == "Label" || node.tag == "Button" ? "text" : "value" },
					{ "data-source-path", "" },
					{ "binding-mode", "TwoWay" }
				}));
			}
		}

		// ------------------------------------------------------------------ inline styles

		private void DrawInlineStyles(UxmlNode node)
		{
			if (EditorGui.TreeNodeEx("Display", ImGuiTreeNodeFlags.DefaultOpen))
			{
				StyleCombo(node, "Display", "display", new[] { "flex", "none" });
				StyleCombo(node, "Visibility", "visibility", new[] { "visible", "hidden" });
				StyleFloat(node, "Opacity", "opacity", 0f, 1f, "");
				StyleCombo(node, "Overflow", "overflow", new[] { "visible", "hidden" });
				ImGui.TreePop();
			}

			if (EditorGui.TreeNodeEx("Position", ImGuiTreeNodeFlags.DefaultOpen))
			{
				StyleCombo(node, "Position", "position", new[] { "relative", "absolute" });
				StyleFour(node, "Offsets", new[] { "left", "top", "right", "bottom" }, new[] { "L", "T", "R", "B" });
				ImGui.TreePop();
			}

			if (EditorGui.TreeNodeEx("Flex", ImGuiTreeNodeFlags.DefaultOpen))
			{
				StyleCombo(node, "Direction", "flex-direction", new[] { "column", "column-reverse", "row", "row-reverse" });
				StyleCombo(node, "Wrap", "flex-wrap", new[] { "nowrap", "wrap", "wrap-reverse" });
				StyleCombo(node, "Justify Content", "justify-content", new[] { "flex-start", "center", "flex-end", "space-between", "space-around", "space-evenly" });
				StyleCombo(node, "Align Items", "align-items", new[] { "auto", "flex-start", "center", "flex-end", "stretch" });
				StyleCombo(node, "Align Self", "align-self", new[] { "auto", "flex-start", "center", "flex-end", "stretch" });
				StyleCombo(node, "Align Content", "align-content", new[] { "auto", "flex-start", "center", "flex-end", "stretch" });
				StyleFloat(node, "Grow", "flex-grow", 0f, 10f, "");
				StyleFloat(node, "Shrink", "flex-shrink", 0f, 10f, "");
				StyleText(node, "Basis", "flex-basis");
				ImGui.TreePop();
			}

			if (EditorGui.TreeNodeEx("Size", ImGuiTreeNodeFlags.DefaultOpen))
			{
				StyleText(node, "Width", "width");
				StyleText(node, "Height", "height");
				StyleText(node, "Min Width", "min-width");
				StyleText(node, "Min Height", "min-height");
				StyleText(node, "Max Width", "max-width");
				StyleText(node, "Max Height", "max-height");
				ImGui.TreePop();
			}

			if (EditorGui.TreeNodeEx("Margin & Padding"))
			{
				StyleFour(node, "Margin", new[] { "margin-top", "margin-right", "margin-bottom", "margin-left" }, new[] { "T", "R", "B", "L" });
				StyleFour(node, "Padding", new[] { "padding-top", "padding-right", "padding-bottom", "padding-left" }, new[] { "T", "R", "B", "L" });
				ImGui.TreePop();
			}

			if (EditorGui.TreeNodeEx("Background & Border"))
			{
				StyleColor(node, "Background", "background-color");
				StyleText(node, "Image", "background-image");
				StyleCombo(node, "Scale Mode", "-unity-background-scale-mode", new[] { "stretch-to-fill", "scale-and-crop", "scale-to-fit" });
				StyleColor(node, "Image Tint", "-unity-background-image-tint-color");
				StyleColor(node, "Border Color", "border-color");
				StyleFour(node, "Border Width", new[] { "border-top-width", "border-right-width", "border-bottom-width", "border-left-width" }, new[] { "T", "R", "B", "L" });
				StyleFour(node, "Radius", new[] { "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius" }, new[] { "TL", "TR", "BR", "BL" });
				ImGui.TreePop();
			}

			if (EditorGui.TreeNodeEx("Text"))
			{
				StyleColor(node, "Color", "color");
				StyleText(node, "Font Size", "font-size");
				StyleText(node, "Font", "-unity-font");
				StyleCombo(node, "Font Style", "-unity-font-style", new[] { "normal", "bold", "italic", "bold-and-italic" });
				StyleCombo(node, "Align", "-unity-text-align", new[] { "upper-left", "upper-center", "upper-right", "middle-left", "middle-center", "middle-right", "lower-left", "lower-center", "lower-right" });
				StyleCombo(node, "White Space", "white-space", new[] { "normal", "nowrap", "pre", "pre-wrap" });
				StyleCombo(node, "Overflow", "text-overflow", new[] { "clip", "ellipsis" });
				StyleText(node, "Letter Spacing", "letter-spacing");
				StyleText(node, "Text Shadow", "text-shadow");
				ImGui.TreePop();
			}

			if (EditorGui.TreeNodeEx("Transform & Transition"))
			{
				StyleText(node, "Translate", "translate");
				StyleText(node, "Rotate", "rotate");
				StyleText(node, "Scale", "scale");
				StyleText(node, "Origin", "transform-origin");
				StyleText(node, "Transition", "transition");
				StyleText(node, "Cursor", "cursor");
				ImGui.TreePop();
			}

			ImGui.Separator();
			string raw = node.GetAttribute("style") ?? "";
			ImGui.TextDisabled("Raw inline style");
			ImGui.SetNextItemWidth(-1);
			bool rawChanged = EditorGui.InputTextMultiline("##rawstyle", ref raw, 4096u, new NVector2(-1, 60));
			Track(rawChanged, () => node.SetAttribute("style", raw.Trim()));
		}

		private void StyleText(UxmlNode node, string label, string property)
		{
			string value = node.GetStyleProperty(property) ?? "";
			Label(label);
			bool changed = EditorGui.InputTextWithHint("##s_" + property, "inherit", ref value, 256u);
			Track(changed, () => node.SetStyleProperty(property, value));
		}

		private void StyleCombo(UxmlNode node, string label, string property, string[] options)
		{
			string current = node.GetStyleProperty(property);
			Label(label);
			if (EditorGui.BeginCombo("##s_" + property, current ?? "(default)"))
			{
				if (EditorGui.Selectable("(default)", current == null))
				{
					Apply(() => node.SetStyleProperty(property, null));
				}

				foreach (string option in options)
				{
					if (EditorGui.Selectable(option, option == current))
					{
						string chosen = option;
						Apply(() => node.SetStyleProperty(property, chosen));
					}
				}

				EditorGui.EndCombo();
			}
		}

		private void StyleFloat(UxmlNode node, string label, string property, float min, float max, string unit)
		{
			string current = node.GetStyleProperty(property);
			float value = ParseFloat(current, property == "opacity" || property == "flex-shrink" ? 1f : 0f);
			Label(label);
			bool changed = EditorGui.SliderFloat("##s_" + property, ref value, min, max, current == null ? "default" : "%.2f");
			Track(changed, () => node.SetStyleProperty(property, value.ToString("0.##", CultureInfo.InvariantCulture) + unit));
			if (current != null)
			{
				ImGui.SameLine();
				if (EditorGui.SmallButton("x##" + property)) Apply(() => node.SetStyleProperty(property, null));
			}
		}

		private void StyleColor(UxmlNode node, string label, string property)
		{
			string current = node.GetStyleProperty(property);
			Color4 color = new Color4(1f, 1f, 1f, 1f);
			if (current != null)
			{
				StyleValue[] values = StyleSheetParser.ParseValue(current);
				if (values.Length > 0 && values[0].type == StyleValueType.Color) color = values[0].color;
			}

			var n = new NVector4(color.R, color.G, color.B, color.A);
			Label(label);
			ImGui.SetNextItemWidth(current != null ? -34 : -1);
			bool changed = EditorGui.ColorEdit4("##s_" + property, ref n, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf);
			Track(changed, () => node.SetStyleProperty(property, StyleColorUtility.ToString(new Color4(n.X, n.Y, n.Z, n.W))));
			if (current != null)
			{
				ImGui.SameLine();
				if (EditorGui.SmallButton("x##" + property)) Apply(() => node.SetStyleProperty(property, null));
			}
		}

		private void StyleFour(UxmlNode node, string label, string[] properties, string[] hints)
		{
			Label(label);
			float width = (ImGui.GetContentRegionAvail().X - 3 * ImGui.GetStyle().ItemSpacing.X) / 4f;
			for (int i = 0; i < 4; i++)
			{
				if (i > 0) ImGui.SameLine();
				string value = node.GetStyleProperty(properties[i]) ?? "";
				ImGui.SetNextItemWidth(width);
				bool changed = EditorGui.InputTextWithHint("##s_" + properties[i], hints[i], ref value, 32u);
				string property = properties[i];
				Track(changed, () => node.SetStyleProperty(property, value));
			}
		}

		private static void Label(string text)
		{
			ImGui.AlignTextToFramePadding();
			// Long labels are clipped at the column (the full text as a tooltip) instead of running under the widget.
			NVector2 pos = ImGui.GetCursorScreenPos();
			ImGui.PushClipRect(pos, new NVector2(pos.X + LabelWidth - 6, pos.Y + ImGui.GetFrameHeight()), true);
			ImGui.TextUnformatted(text);
			ImGui.PopClipRect();
			if (ImGui.IsItemHovered() && ImGui.CalcTextSize(text).X > LabelWidth - 6) ImGui.SetTooltip(text);
			ImGui.SameLine(LabelWidth);
			ImGui.SetNextItemWidth(-1);
		}

		private static float ParseFloat(string text, float fallback)
		{
			if (string.IsNullOrEmpty(text))
			{
				return fallback;
			}

			float value;
			return float.TryParse(text.Replace("px", "").Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
		}

		#endregion
	}
}
