using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.VFX;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.VFX
{
	/// <summary>
	///     The side panels of the VFX Graph window: the inspector of the selected node (title, settings, inline input
	///     values, connections) and the blackboard (the graph's properties: name, type, value, exposed flag).
	/// </summary>
	internal sealed class VfxNodeInspector
	{
		#region PrivateFields

		private readonly Dictionary<ulong, string> titleBuffers = new Dictionary<ulong, string>();
		private string newPropertyName = "New Property";
		private int newPropertyType;
		private float labelWidth = 120f;

		private static readonly string[] PropertyTypes = { "Float", "Int", "Uint", "Bool", "Vector2", "Vector3", "Vector4", "Color", "Gradient", "Curve", "Texture2D", "Texture3D", "Mesh" };

		#endregion

		#region PublicMethods

		/// <summary>The inspector of the canvas's primary selected node.</summary>
		public void DrawInspector(VfxGraphDocument document, VfxGraphCanvas canvas)
		{
			labelWidth = Math.Max(90f, ImGui.GetContentRegionAvail().X * 0.4f);
			VfxNodeData node = document.Graph.FindNode(canvas.Primary);
			VfxNodeDefinition definition = node != null ? VfxNodeLibrary.Find(node.Type) : null;
			if (node == null || definition == null)
			{
				DrawGraphSettings(document);
				ImGui.Spacing();
				ImGui.Separator();
				ImGui.TextDisabled("Select a node to edit it.");
				ImGui.Spacing();
				ImGui.TextWrapped("Right click the canvas to create nodes, drag ports to connect them, click + Add Block inside a context. Space opens the node search, F frames the graph, Delete removes the selection.");
				return;
			}

			ImGui.TextUnformatted(definition.Name);
			ImGui.SameLine();
			ImGui.TextDisabled("(" + definition.Kind + ")");
			if (!string.IsNullOrEmpty(definition.Description))
			{
				ImGui.TextWrapped(definition.Description);
			}

			ImGui.Separator();
			if (definition.Kind == VfxNodeKind.Context)
			{
				DrawTitle(document, node);
			}

			if (definition.Kind == VfxNodeKind.Block)
			{
				bool enabled = node.Enabled;
				Label("Enabled");
				if (ImGui.Checkbox("##enabled", ref enabled))
				{
					document.Edit("Toggle block", () => node.Enabled = enabled);
				}
			}

			if (definition.Settings.Count > 0)
			{
				ImGui.SeparatorText("Settings");
				foreach (VfxSettingDefinition setting in definition.Settings)
				{
					ImGui.PushID(setting.Name);
					DrawSetting(document, node, definition, setting);
					ImGui.PopID();
				}
			}

			List<VfxSlotDefinition> inputs = definition.GetInputs(node);
			if (inputs.Count > 0)
			{
				ImGui.SeparatorText("Inputs");
				foreach (VfxSlotDefinition slot in inputs)
				{
					ImGui.PushID(slot.Name);
					DrawInput(document, node, slot);
					ImGui.PopID();
				}
			}

			List<VfxSlotDefinition> outputs = definition.GetOutputs(node);
			if (outputs.Count > 0 && definition.Kind == VfxNodeKind.Operator)
			{
				ImGui.SeparatorText("Outputs");
				foreach (VfxSlotDefinition slot in outputs)
				{
					ImGui.BulletText(slot.DisplayName + "  (" + slot.Type + ")");
				}
			}
		}

		/// <summary>The blackboard: the graph's properties.</summary>
		public void DrawBlackboard(VfxGraphDocument document, VfxGraphCanvas canvas)
		{
			VfxGraph graph = document.Graph;
			labelWidth = Math.Max(80f, ImGui.GetContentRegionAvail().X * 0.35f);
			ImGui.TextDisabled("Drag a property onto the canvas to read it.");
			ImGui.Spacing();
			VfxProperty remove = null;
			foreach (VfxProperty property in graph.Properties)
			{
				ImGui.PushID(property.Name);
				bool open = ImGui.TreeNodeEx("##prop", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.SpanAvailWidth);
				if (ImGui.BeginDragDropSource())
				{
					VfxGraphCanvas.DraggedProperty = property.Name;
					unsafe
					{
						ImGui.SetDragDropPayload(VfxGraphCanvas.PropertyDragType, null, 0);
					}

					ImGui.Text(property.Name);
					ImGui.EndDragDropSource();
				}

				ImGui.SameLine();
				ImGui.TextUnformatted(property.Name);
				ImGui.SameLine();
				ImGui.TextDisabled(property.Value.Type.ToString() + (property.Exposed ? "" : " (hidden)"));
				if (open)
				{
					// Name.
					Label("Name");
					string name = property.Name;
					ImGui.InputText("##name", ref name, 64);
					if (ImGui.IsItemDeactivatedAfterEdit() && name != property.Name && !string.IsNullOrWhiteSpace(name))
					{
						document.Edit("Rename property", () => graph.RenameProperty(property, name.Trim()));
					}

					Label("Type");
					int typeIndex = Array.IndexOf(PropertyTypes, property.Value.Type.ToString());
					if (ImGui.Combo("##type", ref typeIndex, PropertyTypes, PropertyTypes.Length) && typeIndex >= 0)
					{
						VfxValueType newType;
						if (Enum.TryParse(PropertyTypes[typeIndex], out newType) && newType != property.Value.Type)
						{
							document.Edit("Change property type", () =>
							{
								property.Value = VfxValueTypes.IsNumeric(newType) && VfxValueTypes.IsNumeric(property.Value.Type) ? property.Value.ConvertTo(newType) : VfxValue.Default(newType);
								property.Value.Type = newType;
								foreach (VfxNodeData node in graph.Nodes)
								{
									if (node.Type == VfxNodeLibrary.PropertyNodeType && node.GetSetting("Property") == property.Name)
									{
										node.SetSetting("PropertyType", newType.ToString());
									}
								}
							});
						}
					}

					Label("Value");
					DrawValueEditor(document, "Edit property", property.Value, null);
					Label("Exposed");
					bool exposed = property.Exposed;
					if (ImGui.Checkbox("##exposed", ref exposed))
					{
						document.Edit("Toggle exposed", () => property.Exposed = exposed);
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Exposed properties show in the component's inspector and can be set from scripts.");
					}

					Label("Tooltip");
					string tooltip = property.Tooltip ?? "";
					ImGui.InputText("##tooltip", ref tooltip, 256);
					if (ImGui.IsItemDeactivatedAfterEdit() && tooltip != (property.Tooltip ?? ""))
					{
						document.Edit("Edit tooltip", () => property.Tooltip = tooltip);
					}

					if (ImGui.SmallButton("Remove property"))
					{
						remove = property;
					}

					ImGui.TreePop();
				}

				ImGui.PopID();
			}

			if (remove != null)
			{
				document.Edit("Remove property", () =>
				{
					graph.RemoveProperty(remove);
					graph.Nodes.RemoveAll(node => node.Type == VfxNodeLibrary.PropertyNodeType && node.GetSetting("Property") == remove.Name && RemoveEdgesOf(graph, node));
				});
			}

			ImGui.Spacing();
			ImGui.SeparatorText("Add property");
			ImGui.SetNextItemWidth(-1);
			ImGui.InputText("##newname", ref newPropertyName, 64);
			ImGui.SetNextItemWidth(-1);
			ImGui.Combo("##newtype", ref newPropertyType, PropertyTypes, PropertyTypes.Length);
			if (ImGui.Button("Add", new NVector2(-1, 0)) && !string.IsNullOrWhiteSpace(newPropertyName))
			{
				VfxValueType type;
				Enum.TryParse(PropertyTypes[Math.Clamp(newPropertyType, 0, PropertyTypes.Length - 1)], out type);
				string name = newPropertyName.Trim();
				document.Edit("Add property", () => graph.AddProperty(name, VfxValue.Default(type)));
			}
		}

		#endregion

		#region PrivateMethods

		private static bool RemoveEdgesOf(VfxGraph graph, VfxNodeData node)
		{
			graph.Edges.RemoveAll(edge => edge.From == node.Id || edge.To == node.Id);
			return true;
		}

		private void Label(string text)
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(text);
			ImGui.SameLine(labelWidth);
			ImGui.SetNextItemWidth(-1);
		}

		private static readonly string[] CullingOptions = { "Simulate When Visible", "Always Simulate" };

		/// <summary>The settings of the whole graph (shown while no node is selected).</summary>
		private void DrawGraphSettings(VfxGraphDocument document)
		{
			VfxGraph graph = document.Graph;
			ImGui.TextUnformatted("Graph");
			ImGui.Spacing();
			Label("Culling");
			int culling = (int)graph.CullingMode;
			if (ImGui.Combo("##culling", ref culling, CullingOptions, CullingOptions.Length) && culling != (int)graph.CullingMode)
			{
				// Read every frame by the playing effects: no recompilation needed.
				document.EditLayout("Change culling", () => graph.CullingMode = (VfxCullingMode)culling);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Simulate When Visible: an effect no view sees freezes and costs nothing until it is seen again (Unity's default).\nAlways Simulate: it keeps running off-screen (for effects whose state matters while hidden).");
			}
		}

		private void DrawTitle(VfxGraphDocument document, VfxNodeData node)
		{
			string buffer;
			if (!titleBuffers.TryGetValue(node.Id, out buffer))
			{
				buffer = node.Title ?? "";
			}

			Label("Name");
			ImGui.InputTextWithHint("##title", "System name", ref buffer, 64);
			titleBuffers[node.Id] = buffer;
			if (ImGui.IsItemDeactivatedAfterEdit())
			{
				string title = string.IsNullOrWhiteSpace(buffer) ? null : buffer.Trim();
				if (title != node.Title)
				{
					document.EditLayout("Rename context", () => node.Title = title);
				}

				titleBuffers.Remove(node.Id);
			}
		}

		private void DrawSetting(VfxGraphDocument document, VfxNodeData node, VfxNodeDefinition definition, VfxSettingDefinition setting)
		{
			string value = node.GetSetting(setting.Name, setting.Default);
			Label(setting.DisplayName);
			bool changed = false;
			string newValue = value;
			switch (setting.Kind)
			{
				case VfxSettingKind.Enum:
				{
					string[] options = setting.Options ?? Array.Empty<string>();
					int index = Math.Max(0, Array.IndexOf(options, value));
					if (ImGui.Combo("##v", ref index, options, options.Length) && index >= 0 && index < options.Length)
					{
						newValue = options[index];
						changed = newValue != value;
					}

					break;
				}
				case VfxSettingKind.Attribute:
				{
					string[] options = setting.Options ?? VfxAttributes.Readable;
					int index = Math.Max(0, Array.IndexOf(options, value));
					if (ImGui.Combo("##v", ref index, options, options.Length) && index >= 0)
					{
						newValue = options[index];
						changed = newValue != value;
					}

					break;
				}
				case VfxSettingKind.Property:
				{
					var names = new List<string>();
					foreach (VfxProperty property in document.Graph.Properties)
					{
						names.Add(property.Name);
					}

					if (names.Count == 0)
					{
						ImGui.TextDisabled("No properties (add one in the blackboard).");
						break;
					}

					string[] options = names.ToArray();
					int index = Math.Max(0, Array.IndexOf(options, value));
					if (ImGui.Combo("##v", ref index, options, options.Length) && index >= 0)
					{
						newValue = options[index];
						changed = newValue != value;
					}

					break;
				}
				case VfxSettingKind.Bool:
				{
					bool b = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
					if (ImGui.Checkbox("##v", ref b))
					{
						newValue = b ? "1" : "0";
						changed = true;
					}

					break;
				}
				case VfxSettingKind.Int:
				{
					int i = node.GetInt(setting.Name, 0);
					if (ImGui.InputInt("##v", ref i))
					{
						newValue = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
						changed = true;
					}

					break;
				}
				case VfxSettingKind.Float:
				{
					float f = node.GetFloat(setting.Name, 0f);
					if (ImGui.DragFloat("##v", ref f, 0.01f))
					{
						newValue = f.ToString(System.Globalization.CultureInfo.InvariantCulture);
						changed = true;
					}

					break;
				}
				case VfxSettingKind.Code:
				{
					string text = value ?? "";
					ImGui.InputTextMultiline("##v", ref text, 16384, new NVector2(-1, 140), ImGuiInputTextFlags.AllowTabInput);
					if (ImGui.IsItemDeactivatedAfterEdit() && text != value)
					{
						newValue = text;
						changed = true;
					}

					break;
				}
				default:
				{
					string text = value ?? "";
					if (setting.AffectsSlots && (setting.Name == "Inputs"))
					{
						ImGui.InputTextMultiline("##v", ref text, 4096, new NVector2(-1, 70));
					}
					else
					{
						ImGui.InputText("##v", ref text, 256);
					}

					if (ImGui.IsItemDeactivatedAfterEdit() && text != value)
					{
						newValue = text;
						changed = true;
					}

					break;
				}
			}

			if (!string.IsNullOrEmpty(setting.Tooltip) && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(setting.Tooltip);
			}

			if (!changed)
			{
				return;
			}

			document.Edit("Change " + setting.DisplayName, () =>
			{
				node.SetSetting(setting.Name, newValue);
				if (setting.AffectsSlots)
				{
					new VfxGraphBuilder(document.Graph).RebuildSlots(node);
					// Connections into slots that no longer exist (or changed type) go.
					List<VfxSlotDefinition> inputs = definition.GetInputs(node);
					document.Graph.Edges.RemoveAll(edge => edge.To == node.Id && inputs.Find(s => s.Name == edge.ToSlot) == null);
				}
			});
		}

		private void DrawInput(VfxGraphDocument document, VfxNodeData node, VfxSlotDefinition slot)
		{
			VfxGraph graph = document.Graph;
			VfxEdge edge = graph.FindInputEdge(node.Id, slot.Name);
			Label(slot.DisplayName);
			if (edge != null)
			{
				VfxNodeData source = graph.FindNode(edge.From);
				VfxNodeDefinition sourceDefinition = source != null ? VfxNodeLibrary.Find(source.Type) : null;
				ImGui.TextDisabled("<- " + (sourceDefinition?.GetTitle(source) ?? "?") + "." + edge.FromSlot);
				ImGui.SameLine();
				if (ImGui.SmallButton("x"))
				{
					document.Edit("Disconnect", () => graph.Disconnect(node, slot.Name));
				}

				return;
			}

			VfxSlotValue stored = node.FindInput(slot.Name);
			if (stored == null)
			{
				stored = new VfxSlotValue(slot.Name, slot.CreateDefault());
				node.Inputs.Add(stored);
			}

			if (stored.Value == null || stored.Value.Type != slot.Type)
			{
				stored.Value = stored.Value != null && VfxValueTypes.CanConvert(stored.Value.Type, slot.Type) ? stored.Value.ConvertTo(slot.Type) : slot.CreateDefault();
				stored.Value.Type = slot.Type;
			}

			DrawValueEditor(document, "Edit " + slot.DisplayName, stored.Value, slot);
			if (!string.IsNullOrEmpty(slot.Tooltip) && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(slot.Tooltip);
			}
		}

		/// <summary>A value editor whose drags become one undo record (begin on activation, end on release).</summary>
		private static void DrawValueEditor(VfxGraphDocument document, string editName, VfxValue value, VfxSlotDefinition slot)
		{
			bool changed = VfxValueEditors.Draw("value", value, slot);
			VfxValueEditors.EditState state = VfxValueEditors.LastEditState;
			if (state == VfxValueEditors.EditState.Began || (changed && !document.Editing))
			{
				// The value already changed when we hear about it: capture the state before by undoing the live edit
				// is not possible, so the snapshot is taken at activation (before the first change) where possible.
				document.BeginEdit(editName);
			}

			if (changed)
			{
				document.Changed();
			}

			if (state == VfxValueEditors.EditState.Ended)
			{
				document.EndEdit();
			}
		}

		#endregion
	}
}
