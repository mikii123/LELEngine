using System;
using System.Collections.Generic;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using LELEngine.Editor.Graphs;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The side panels of the Animator window: the layers and parameters on the left (live values in play mode),
	///     and on the right the inspector of what is selected in the graph (state, transitions of an edge, sub-state
	///     machine, Entry / Any State / Exit, note, group, blend tree and its children) or in the sidebar (layer,
	///     parameter), else the controller's settings. Value widgets use the inspector's field drawers, so parameter
	///     values and condition thresholds of any serializable type are edited like component fields, with undo.
	/// </summary>
	internal sealed class AnimatorInspector
	{
		#region PrivateFields

		private enum Mode
		{
			Graph,
			Layer,
			Parameter
		}

		private readonly EditorApplication editor;
		private readonly AnimatorWindow window;
		private readonly InspectorContext context;
		private readonly InspectorContext liveContext;
		private Mode mode;
		private ulong selectedLayer;
		private ulong selectedParameter;
		private ulong renamingParameter;
		private string renameBuffer = "";
		private string parameterSearch = "";
		private string stateNameBuffer;
		private ulong stateNameFor;
		private readonly Dictionary<ulong, bool> expandedTransitions = new Dictionary<ulong, bool>();
		private static readonly FieldInfo BehavioursField = typeof(AnimatorState).GetField(nameof(AnimatorState.behaviours));
		private static readonly string[] BlendTypeNames = { "1D", "2D Simple Directional", "2D Freeform Directional", "2D Freeform Cartesian", "Direct" };
		private static readonly string[] InterruptionNames = { "None", "Current State", "Next State", "Current State Then Next State", "Next State Then Current State" };

		#endregion

		#region Constructors

		public AnimatorInspector(EditorApplication editor, AnimatorWindow window)
		{
			this.editor = editor;
			this.window = window;
			context = new InspectorContext { Editor = editor };
			liveContext = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		public void Reset()
		{
			mode = Mode.Graph;
			selectedLayer = 0;
			selectedParameter = 0;
			renamingParameter = 0;
			stateNameFor = 0;
			expandedTransitions.Clear();
			context.Release();
			liveContext.Release();
		}

		// ---------------------------------------------------------------- sidebar

		public void DrawSidebar()
		{
			AnimatorDocument document = window.Document;
			if (document == null)
			{
				return;
			}

			Prepare();
			AnimatorController controller = document.Controller;
			float height = ImGui.GetContentRegionAvail().Y;
			float layersHeight = Math.Min(height * 0.4f, 40f + Math.Max(controller.layers.Count, 1) * (ImGui.GetTextLineHeightWithSpacing() + 4f));
			if (ImGui.BeginChild("##layers", new NVector2(0, layersHeight), ImGuiChildFlags.None))
			{
				DrawLayers(controller);
			}

			ImGui.EndChild();
			ImGui.Separator();
			if (ImGui.BeginChild("##parameters", new NVector2(0, 0), ImGuiChildFlags.None))
			{
				DrawParameters(controller);
			}

			ImGui.EndChild();
		}

		public void AddLayer()
		{
			AnimatorDocument document = window.Document;
			if (document == null)
			{
				return;
			}

			AnimatorLayer created = null;
			document.Edit("Add layer", () => created = document.Controller.AddLayer("Layer " + (document.Controller.layers.Count + 1)));
			selectedLayer = created.id;
			mode = Mode.Layer;
			window.Graph.ShowLayer(created);
		}

		/// <summary>The items of the "add parameter" menu: one per parameter value type (built-ins first, then the scripts').</summary>
		public void DrawParameterTypeMenu()
		{
			AnimatorDocument document = window.Document;
			if (document == null)
			{
				return;
			}

			foreach (Type type in ParameterTypes())
			{
				string label = TypeLabelOf(type);
				if (EditorMenu.Item(label))
				{
					AddParameter(type);
				}
			}
		}

		// ---------------------------------------------------------------- inspector

		public void DrawInspector()
		{
			AnimatorDocument document = window.Document;
			if (document == null)
			{
				return;
			}

			Prepare();
			AnimatorController controller = document.Controller;
			GraphView view = window.View;
			AnimatorGraph graph = window.Graph;
			if (view.Primary != 0 && mode != Mode.Graph)
			{
				mode = Mode.Graph;
			}

			if (mode == Mode.Layer)
			{
				AnimatorLayer layer = controller.FindLayer(selectedLayer);
				if (layer != null)
				{
					DrawLayer(layer);
					return;
				}

				mode = Mode.Graph;
			}
			else if (mode == Mode.Parameter)
			{
				AnimatorParameter parameter = controller.FindParameter(selectedParameter);
				if (parameter != null)
				{
					DrawParameter(parameter);
					return;
				}

				mode = Mode.Graph;
			}

			ulong id = view.Primary;
			if (id == 0 || !view.Selection.Contains(id))
			{
				DrawControllerSettings(controller);
				return;
			}

			if (graph.Page.IsTree)
			{
				if (graph.TreeNodes.TryGetValue(id, out AnimatorGraph.TreeNodeInfo info))
				{
					if (info.Child == null)
					{
						DrawBlendTree(info.Tree);
					}
					else
					{
						DrawTreeChild(info.Tree, info.Child, info.Index);
					}
				}

				return;
			}

			AnimatorState state = graph.StateOf(id);
			if (state != null)
			{
				if (view.Selection.Count > 1)
				{
					ImGui.TextDisabled(view.Selection.Count + " selected: showing " + state.name);
				}

				DrawState(state);
				return;
			}

			AnimatorStateMachine sub = graph.SubMachineOf(id);
			if (sub != null)
			{
				DrawMachineNode(sub);
				return;
			}

			if (graph.EdgeInfos.TryGetValue(id, out AnimatorGraph.EdgeInfo edge))
			{
				DrawTransitions(edge);
				return;
			}

			if (graph.IsEntry(id))
			{
				DrawEntry(graph.Page.Machine);
				return;
			}

			if (graph.IsAnyState(id))
			{
				DrawAnyState(graph.Page.Machine);
				return;
			}

			if (graph.IsExit(id))
			{
				ImGui.TextUnformatted("Exit");
				ImGui.Separator();
				ImGui.TextWrapped(controller.ParentOf(graph.Page.Machine) == null
					? "A transition to Exit leaves the layer's state machine; the layer starts again at Entry."
					: "A transition to Exit leaves this sub-state machine; the parent continues with the transitions of this machine's node (select the node in the parent to edit them).");
				return;
			}

			LELEngine.Animation.GraphNote note = graph.NoteDataOf(id);
			if (note != null)
			{
				DrawNote(note);
				return;
			}

			GraphGroupData group = graph.GroupOf(id);
			if (group != null)
			{
				DrawGroup(group);
				return;
			}

			DrawControllerSettings(controller);
		}

		#endregion

		#region PrivateMethods

		private AnimatorDocument Document => window.Document;
		private AnimatorController Controller => window.Document?.Controller;

		private void Prepare()
		{
			context.Scene = editor.Scene;
			context.UndoTarget = Controller;
			context.LabelWidth = Math.Max(90f, ImGui.GetContentRegionAvail().X * 0.38f);
			liveContext.Scene = editor.Scene;
			liveContext.UndoTarget = null;
			liveContext.LabelWidth = context.LabelWidth;
		}

		// ---------------------------------------------------------------- layers

		private void DrawLayers(AnimatorController controller)
		{
			ImGui.TextDisabled("Layers");
			ImGui.SameLine();
			float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
			ImGui.SetCursorPosX(right - 24f);
			if (EditorGui.SmallButton("+##addlayer"))
			{
				AddLayer();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Add a layer");
			}

			AnimatorPage page = window.Graph.Page;
			for (int i = 0; i < controller.layers.Count; i++)
			{
				AnimatorLayer layer = controller.layers[i];
				ImGui.PushID((int)(layer.id & 0x7FFFFFFF));
				bool shown = page.Layer == layer;
				bool selected = mode == Mode.Layer && selectedLayer == layer.id;
				string label = layer.name + (layer.blending == LayerBlendingMode.Additive ? "  (additive)" : "") + (!layer.mask.IsEmpty ? "  [mask]" : "") + (!layer.enabled ? "  (off)" : "");
				if (EditorGui.Selectable(label, selected || shown, ImGuiSelectableFlags.AllowOverlap, new NVector2(ImGui.GetContentRegionAvail().X - (i > 0 ? 70f : 0f), 0)))
				{
					selectedLayer = layer.id;
					mode = Mode.Layer;
					window.Graph.ShowLayer(layer);
					window.View.ClearSelection();
				}

				if (EditorGui.BeginPopupContextItem("##layer_context"))
				{
					if (EditorMenu.Item("Move Up", null, false, i > 0)) Document.Edit("Move layer", () => { controller.layers.RemoveAt(i); controller.layers.Insert(i - 1, layer); });
					if (EditorMenu.Item("Move Down", null, false, i < controller.layers.Count - 1)) Document.Edit("Move layer", () => { controller.layers.RemoveAt(i); controller.layers.Insert(i + 1, layer); });
					if (EditorMenu.Item("Duplicate")) Document.Edit("Duplicate layer", () =>
					{
						AnimatorLayer copy = layer.Clone();
						copy.id = AnimatorController.NewId();
						copy.name = layer.name + " Copy";
						controller.layers.Insert(i + 1, copy);
					});
					if (EditorMenu.Item("Delete", null, false, controller.layers.Count > 1))
					{
						Document.Edit("Delete layer", () => controller.layers.Remove(layer));
						if (page.Layer == layer) window.Graph.ShowLayer(controller.layers[0]);
					}

					ImGui.EndPopup();
				}

				if (i > 0)
				{
					ImGui.SameLine();
					ImGui.SetNextItemWidth(64f);
					float weight = layer.weight;
					bool changed = EditorGui.SliderFloat("##weight", ref weight, 0f, 1f, "%.2f");
					context.Track(changed);
					if (changed)
					{
						layer.weight = weight;
						if (window.LiveAnimator != null) window.LiveAnimator.SetLayerWeight(i, weight);
					}
				}

				ImGui.PopID();
			}
		}

		private void DrawLayer(AnimatorLayer layer)
		{
			AnimatorController controller = Controller;
			ImGui.TextUnformatted("Layer");
			ImGui.Separator();
			string name = layer.name;
			FieldDrawer.Label("Name", null, context);
			bool renamed = EditorGui.InputText("##layername", ref name, 64);
			context.Track(renamed);
			if (renamed)
			{
				layer.name = name;
				if (controller.layers.IndexOf(layer) == 0) layer.stateMachine.name = name;
			}

			int index = controller.layers.IndexOf(layer);
			if (index > 0)
			{
				object weight = layer.weight;
				if (FieldDrawer.DrawValue("Weight", typeof(float), ref weight, typeof(AnimatorLayer).GetField(nameof(AnimatorLayer.weight)), context, 0)) layer.weight = (float)weight;
				object blending = layer.blending;
				if (FieldDrawer.DrawValue("Blending", typeof(LayerBlendingMode), ref blending, null, context, 0)) layer.blending = (LayerBlendingMode)blending;
			}
			else
			{
				ImGui.TextDisabled("The base layer always has weight 1 and overrides nothing.");
			}

			object enabled = layer.enabled;
			if (FieldDrawer.DrawValue("Enabled", typeof(bool), ref enabled, null, context, 0)) layer.enabled = (bool)enabled;
			ImGui.SeparatorText("Mask");
			DrawMask(layer.mask);
			ImGui.SeparatorText("Note");
			string note = layer.note ?? "";
			bool noteChanged = EditorGui.InputTextMultiline("##layernote", ref note, 1024, new NVector2(-1, 60));
			context.Track(noteChanged);
			if (noteChanged) layer.note = note;
		}

		private void DrawMask(AnimationMask mask)
		{
			ImGui.TextWrapped("Which transforms (paths from the animated root) the layer animates; the deepest matching entry decides, other paths follow Include Others.");
			object others = mask.includeOthers;
			if (FieldDrawer.DrawValue("Include Others", typeof(bool), ref others, null, context, 0)) mask.includeOthers = (bool)others;
			for (int i = 0; i < mask.entries.Count; i++)
			{
				AnimationMaskEntry entry = mask.entries[i];
				ImGui.PushID(i);
				string path = entry.path ?? "";
				ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 150f);
				bool changed = EditorGui.InputTextWithHint("##path", "path (empty = root)", ref path, 256);
				context.Track(changed);
				if (changed) entry.path = path;
				ImGui.SameLine();
				bool include = entry.include;
				changed = EditorGui.Checkbox("in##inc", ref include);
				context.Track(changed);
				if (changed) entry.include = include;
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Animated (ticked) or left alone");
				ImGui.SameLine();
				bool recursive = entry.recursive;
				changed = EditorGui.Checkbox("sub##rec", ref recursive);
				context.Track(changed);
				if (changed) entry.recursive = recursive;
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Applies to the transforms under the path too");
				ImGui.SameLine();
				if (EditorGui.SmallButton("x"))
				{
					int remove = i;
					Document.Edit("Remove mask entry", () => mask.entries.RemoveAt(remove));
					ImGui.PopID();
					break;
				}

				ImGui.PopID();
			}

			if (EditorGui.SmallButton("Add Entry"))
			{
				Document.Edit("Add mask entry", () => mask.entries.Add(new AnimationMaskEntry()));
			}

			GameObject selected = editor.Selected;
			if (selected != null && !selected.IsDestroyed)
			{
				ImGui.SameLine();
				if (EditorGui.SmallButton("From '" + selected.Name + "'"))
				{
					ImGui.OpenPopup("##mask_from_object");
				}

				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tick the transforms of the selected object's hierarchy the layer should animate.");
				if (ImGui.BeginPopup("##mask_from_object"))
				{
					ImGui.TextDisabled("Transforms under " + selected.Name);
					DrawMaskTree(mask, selected.transform, selected.transform, 0);
					ImGui.EndPopup();
				}
			}
		}

		private void DrawMaskTree(AnimationMask mask, Transform root, Transform transform, int depth)
		{
			string path = AnimationBinder.PathOf(root, transform) ?? "";
			bool included = mask.Includes(path);
			ImGui.PushID(transform.Id.GetHashCode());
			ImGui.Indent(depth * 12f);
			bool value = included;
			if (EditorGui.Checkbox(path.Length == 0 ? "(root)" : transform.gameObject.Name, ref value))
			{
				Document.Edit("Edit mask", () =>
				{
					AnimationMaskEntry entry = mask.entries.Find(e => (e.path ?? "") == path);
					if (entry == null)
					{
						entry = new AnimationMaskEntry { path = path };
						mask.entries.Add(entry);
					}

					entry.include = value;
				});
			}

			ImGui.Unindent(depth * 12f);
			ImGui.PopID();
			foreach (Transform child in transform.Children)
			{
				DrawMaskTree(mask, root, child, depth + 1);
			}
		}

		// ---------------------------------------------------------------- parameters

		private void DrawParameters(AnimatorController controller)
		{
			ImGui.TextDisabled("Parameters");
			ImGui.SameLine();
			float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
			ImGui.SetCursorPosX(right - 24f);
			if (EditorGui.SmallButton("+##addparameter"))
			{
				ImGui.OpenPopup("##add_parameter");
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Add a parameter (any serializable type)");
			}

			if (ImGui.BeginPopup("##add_parameter"))
			{
				DrawParameterTypeMenu();
				ImGui.EndPopup();
			}

			ImGui.SetNextItemWidth(-1);
			EditorGui.InputTextWithHint("##parameter_search", "Filter", ref parameterSearch, 64);
			Animator live = window.LiveAnimator;
			for (int i = 0; i < controller.parameters.Count; i++)
			{
				AnimatorParameter parameter = controller.parameters[i];
				if (parameterSearch.Length > 0 && parameter.name.IndexOf(parameterSearch, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}

				ImGui.PushID((int)(parameter.id & 0x7FFFFFFF));
				float available = ImGui.GetContentRegionAvail().X;
				float valueWidth = Math.Max(70f, available * 0.42f);
				if (renamingParameter == parameter.id)
				{
					ImGui.SetNextItemWidth(available - valueWidth - 8f);
					if (ImGui.IsWindowAppearing() || renameBuffer == null) ImGui.SetKeyboardFocusHere();
					bool done = EditorGui.InputText("##rename", ref renameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
					if (done || (!ImGui.IsItemActive() && !ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left)))
					{
						if (done && renameBuffer.Trim().Length > 0 && renameBuffer != parameter.name)
						{
							string unique = controller.UniqueParameterName(renameBuffer.Trim());
							Document.Edit("Rename parameter", () => parameter.name = unique);
						}

						renamingParameter = 0;
					}
				}
				else
				{
					bool selected = mode == Mode.Parameter && selectedParameter == parameter.id;
					if (EditorGui.Selectable(parameter.name + "##row", selected, ImGuiSelectableFlags.AllowOverlap | ImGuiSelectableFlags.AllowDoubleClick, new NVector2(available - valueWidth - 8f, 0)))
					{
						selectedParameter = parameter.id;
						mode = Mode.Parameter;
						window.View.ClearSelection();
						if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							renamingParameter = parameter.id;
							renameBuffer = parameter.name;
						}
					}

					if (ImGui.IsItemHovered())
					{
						List<string> uses = controller.FindParameterUses(parameter.id);
						string tooltip = parameter.TypeLabel + (string.IsNullOrEmpty(parameter.note) ? "" : "\n" + parameter.note) + "\n" + (uses.Count == 0 ? "Not used yet." : "Used by:\n  " + string.Join("\n  ", uses)) + "\nDouble click: rename";
						ImGui.SetTooltip(tooltip);
					}

					if (EditorGui.BeginPopupContextItem("##parameter_context"))
					{
						if (EditorMenu.Item("Rename")) { renamingParameter = parameter.id; renameBuffer = parameter.name; }
						if (EditorMenu.Item("Duplicate")) Document.Edit("Duplicate parameter", () =>
						{
							AnimatorParameter copy = parameter.Clone();
							copy.id = AnimatorController.NewId();
							copy.name = controller.UniqueParameterName(parameter.name);
							controller.parameters.Insert(i + 1, copy);
						});
						if (EditorMenu.Item("Move Up", null, false, i > 0)) Document.Edit("Move parameter", () => { controller.parameters.RemoveAt(i); controller.parameters.Insert(i - 1, parameter); });
						if (EditorMenu.Item("Move Down", null, false, i < controller.parameters.Count - 1)) Document.Edit("Move parameter", () => { controller.parameters.RemoveAt(i); controller.parameters.Insert(i + 1, parameter); });
						int useCount = controller.FindParameterUses(parameter.id).Count;
						if (EditorMenu.Item(useCount > 0 ? "Delete (used " + useCount + "x: conditions are removed)" : "Delete")) Document.Edit("Delete parameter", () => controller.RemoveParameter(parameter));
						ImGui.EndPopup();
					}
				}

				ImGui.SameLine();
				ImGui.SetNextItemWidth(valueWidth);
				ParameterState liveState = live?.GetParameterState(parameter.name);
				if (liveState != null)
				{
					DrawLiveValue(liveState, valueWidth);
				}
				else if (parameter.defaultValue != null)
				{
					ImGui.PushItemWidth(valueWidth);
					ValueField("##default", parameter.defaultValue, context);
					ImGui.PopItemWidth();
				}
				else
				{
					ImGui.TextDisabled("(no type)");
				}

				ImGui.PopID();
			}

			if (controller.parameters.Count == 0)
			{
				ImGui.TextDisabled("No parameters. Click + to add one.");
			}
		}

		private void DrawLiveValue(ParameterState state, float width)
		{
			if (state.IsTrigger)
			{
				bool set = state is ParameterState<bool> b && b.value;
				if (EditorGui.Button(set ? "Set!" : "Fire", new NVector2(width, 0)))
				{
					state.Boxed = true;
				}

				return;
			}

			object boxed = state.Boxed;
			ImGui.PushItemWidth(width);
			ImGui.PushID("live");
			bool changed = FieldDrawer.DrawValue("##live", state.ValueType, ref boxed, null, liveContext, 1);
			ImGui.PopID();
			ImGui.PopItemWidth();
			if (changed)
			{
				state.Boxed = boxed;
			}
		}

		private void DrawParameter(AnimatorParameter parameter)
		{
			AnimatorController controller = Controller;
			ImGui.TextUnformatted("Parameter");
			ImGui.SameLine();
			ImGui.TextDisabled("(" + parameter.TypeLabel + ")");
			ImGui.Separator();
			string name = parameter.name;
			FieldDrawer.Label("Name", null, context);
			bool renamed = EditorGui.InputText("##parametername", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue);
			if (renamed && name.Trim().Length > 0 && name != parameter.name)
			{
				string unique = controller.UniqueParameterName(name.Trim());
				Document.Edit("Rename parameter", () => parameter.name = unique);
			}

			if (parameter.defaultValue != null)
			{
				ValueField("Default", parameter.defaultValue, context);
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("The value every animator starts with.");
			}

			FieldDrawer.Label("Type", null, context);
			if (EditorGui.BeginCombo("##parametertype", parameter.TypeLabel))
			{
				foreach (Type type in ParameterTypes())
				{
					if (EditorGui.Selectable(TypeLabelOf(type), parameter.defaultValue?.GetType() == type))
					{
						Type chosen = type;
						Document.Edit("Change parameter type", () => parameter.defaultValue = (AnimatorValue)Activator.CreateInstance(chosen));
					}
				}

				EditorGui.EndCombo();
			}

			FieldDrawer.Label("Note", null, context);
			string note = parameter.note ?? "";
			bool noteChanged = EditorGui.InputTextMultiline("##parameternote", ref note, 512, new NVector2(-1, 50));
			context.Track(noteChanged);
			if (noteChanged) parameter.note = note;
			ImGui.SeparatorText("Used by");
			List<string> uses = controller.FindParameterUses(parameter.id);
			if (uses.Count == 0) ImGui.TextDisabled("Nothing yet.");
			foreach (string use in uses) ImGui.BulletText(use);
			Animator live = window.LiveAnimator;
			ParameterState liveState = live?.GetParameterState(parameter.name);
			if (liveState != null)
			{
				ImGui.SeparatorText("Live");
				DrawLiveValue(liveState, ImGui.GetContentRegionAvail().X);
			}
		}

		private void AddParameter(Type valueType)
		{
			AnimatorController controller = Controller;
			var value = (AnimatorValue)Activator.CreateInstance(valueType);
			AnimatorParameter created = null;
			Document.Edit("Add parameter", () => created = controller.AddParameter("New " + value.TypeLabel, value));
			selectedParameter = created.id;
			mode = Mode.Parameter;
			renamingParameter = created.id;
			renameBuffer = created.name;
		}

		private static List<Type> ParameterTypes()
		{
			var builtIn = new List<Type> { typeof(FloatValue), typeof(IntValue), typeof(BoolValue), typeof(TriggerValue), typeof(StringValue), typeof(Vector2Value), typeof(Vector3Value), typeof(Vector4Value), typeof(QuaternionValue), typeof(ColorValue), typeof(ObjectValue), typeof(AssetValue) };
			var result = new List<Type>(builtIn);
			foreach (Type type in TypeSelector.ConcreteTypes(typeof(AnimatorValue)))
			{
				if (!result.Contains(type))
				{
					result.Add(type);
				}
			}

			return result;
		}

		private static string TypeLabelOf(Type type)
		{
			try
			{
				return ((AnimatorValue)Activator.CreateInstance(type)).TypeLabel + (type.Assembly == typeof(AnimatorValue).Assembly ? "" : "  (" + type.Name + ")");
			}
			catch (Exception)
			{
				return type.Name;
			}
		}

		/// <summary>The value widget of an <see cref="AnimatorValue" /> (any serializable type, through the field drawers).</summary>
		private static bool ValueField(string label, AnimatorValue value, InspectorContext context)
		{
			object boxed = value.BoxedValue;
			bool changed = FieldDrawer.DrawValue(label, value.ValueType, ref boxed, null, context, 1);
			if (changed)
			{
				value.BoxedValue = boxed;
			}

			return changed;
		}

		// ---------------------------------------------------------------- states

		private void DrawState(AnimatorState state)
		{
			AnimatorController controller = Controller;
			AnimatorStateMachine machine = window.Graph.Page.Machine;
			ImGui.TextUnformatted("State");
			if (machine.defaultState == state.id)
			{
				ImGui.SameLine();
				ImGui.TextColored(AnimatorGraph.DefaultStateColor, "(default)");
			}
			else
			{
				ImGui.SameLine();
				if (EditorGui.SmallButton("Set as default"))
				{
					window.Graph.SetDefaultState(state);
				}
			}

			ImGui.Separator();
			if (stateNameFor != state.id)
			{
				stateNameFor = state.id;
				stateNameBuffer = state.name;
			}

			FieldDrawer.Label("Name", null, context);
			bool renamed = EditorGui.InputText("##statename", ref stateNameBuffer, 64, ImGuiInputTextFlags.EnterReturnsTrue);
			if (renamed && stateNameBuffer.Trim().Length > 0 && stateNameBuffer != state.name)
			{
				window.Graph.Rename(state.id, stateNameBuffer.Trim());
				stateNameBuffer = state.name;
			}
			else if (!ImGui.IsItemActive())
			{
				stateNameBuffer = state.name;
			}

			FieldDrawer.Label("Tag", null, context);
			string tag = state.tag ?? "";
			bool tagChanged = EditorGui.InputText("##statetag", ref tag, 64);
			context.Track(tagChanged);
			if (tagChanged) state.tag = tag;

			// Motion.
			ImGui.SeparatorText("Motion");
			FieldDrawer.Label("Kind", null, context);
			string kind = state.motion == null ? "None" : state.motion is BlendTree ? "Blend Tree" : "Clip";
			if (EditorGui.BeginCombo("##motionkind", kind))
			{
				if (EditorGui.Selectable("None", state.motion == null)) Document.Edit("Set motion", () => state.motion = null);
				if (EditorGui.Selectable("Clip", state.motion is ClipMotion)) Document.Edit("Set motion", () => state.motion = state.motion is ClipMotion ? state.motion : new ClipMotion());
				if (EditorGui.Selectable("Blend Tree", state.motion is BlendTree)) Document.Edit("Set motion", () => state.motion = state.motion is BlendTree ? state.motion : new BlendTree { name = state.name + " Tree" });
				EditorGui.EndCombo();
			}

			if (state.motion is ClipMotion clipMotion)
			{
				object clip = clipMotion.clip;
				if (FieldDrawer.DrawValue("Clip", typeof(AnimationClip), ref clip, null, context, 0))
				{
					clipMotion.clip = clip as AnimationClip;
				}

				if (clipMotion.clip != null)
				{
					ImGui.TextDisabled("  " + clipMotion.clip.length.ToString("0.###") + " s, " + clipMotion.clip.bindings.Count + " properties" + (clipMotion.clip.loop ? ", loops" : "") + ", " + clipMotion.clip.events.Count + " events");
					ImGui.SameLine();
					if (EditorGui.SmallButton("Edit Clip"))
					{
						editor.OpenAnimationWindow(clipMotion.clip.AssetPath);
					}
				}
			}
			else if (state.motion is BlendTree tree)
			{
				ImGui.TextDisabled("  " + tree.blendType + ", " + tree.children.Count + " children");
				ImGui.SameLine();
				if (EditorGui.SmallButton("Open Blend Tree"))
				{
					window.NavigateToTree(state, tree, new List<int>());
				}
			}

			// Playback.
			ImGui.SeparatorText("Playback");
			object speed = state.speed;
			if (FieldDrawer.DrawValue("Speed", typeof(float), ref speed, null, context, 0)) state.speed = (float)speed;
			ParameterCombo("Speed Multiplier", ref state.speedParameter, p => IsNumeric(p), "(none)", "Multiply the speed by this parameter");
			ParameterCombo("Motion Time", ref state.motionTimeParameter, p => IsNumeric(p), "(advance by time)", "The motion's normalized time is this parameter (0..1), e.g. an aim pose from a stick");
			object cycle = state.cycleOffset;
			if (FieldDrawer.DrawValue("Cycle Offset", typeof(float), ref cycle, null, context, 0)) state.cycleOffset = (float)cycle;
			ParameterCombo("Cycle Offset Param", ref state.cycleOffsetParameter, p => IsNumeric(p), "(none)", "Cycle offset from this parameter instead");
			object loop = state.loop;
			if (FieldDrawer.DrawValue("Loop", typeof(StateLoopMode), ref loop, null, context, 0)) state.loop = (StateLoopMode)loop;
			object writeDefaults = state.writeDefaults;
			if (FieldDrawer.DrawValue("Write Defaults", typeof(bool), ref writeDefaults, null, context, 0)) state.writeDefaults = (bool)writeDefaults;
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Properties the motion does not animate get the values the objects had when the animator bound; off: they keep their last values.");

			// Transitions.
			ImGui.SeparatorText("Transitions (" + state.transitions.Count + ")");
			DrawTransitionList(state.transitions, state.name);

			// Behaviours.
			ImGui.SeparatorText("Behaviours");
			object behaviours = state.behaviours;
			if (FieldDrawer.DrawValue("Behaviours", typeof(List<StateMachineBehaviour>), ref behaviours, BehavioursField, context, 0))
			{
				state.behaviours = behaviours as List<StateMachineBehaviour> ?? new List<StateMachineBehaviour>();
			}

			ImGui.SeparatorText("Appearance");
			ColorCombo("Color", state.colorIndex, index => Document.EditLayout("Color state", () => state.colorIndex = index));
			FieldDrawer.Label("Note", null, context);
			string note = state.note ?? "";
			bool noteChanged = EditorGui.InputTextMultiline("##statenote", ref note, 1024, new NVector2(-1, 50));
			context.Track(noteChanged);
			if (noteChanged) state.note = note;
		}

		private static bool IsNumeric(AnimatorParameter parameter)
		{
			Type type = parameter.ValueType;
			return type == typeof(float) || type == typeof(int) || type == typeof(bool) || type == typeof(double);
		}

		/// <summary>A compact list of transitions with their conditions; clicking one selects its edge in the graph.</summary>
		private void DrawTransitionList(List<AnimatorTransition> transitions, string sourceName)
		{
			AnimatorController controller = Controller;
			for (int i = 0; i < transitions.Count; i++)
			{
				AnimatorTransition transition = transitions[i];
				ImGui.PushID((int)(transition.id & 0x7FFFFFFF));
				string label = (string.IsNullOrEmpty(transition.name) ? "" : transition.name + ": ") + "-> " + controller.DescribeDestination(transition);
				if (EditorGui.Selectable(label + "##t", false, ImGuiSelectableFlags.AllowOverlap, new NVector2(ImGui.GetContentRegionAvail().X - 100f, 0)))
				{
					window.Graph.SelectTransition(transition);
				}

				if (ImGui.IsItemHovered()) ImGui.SetTooltip(transition.DescribeConditions(controller) + "\nClick to edit");
				ImGui.SameLine();
				bool solo = transition.solo;
				bool changed = EditorGui.Checkbox("S##solo", ref solo);
				context.Track(changed);
				if (changed) transition.solo = solo;
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Solo: only solo transitions of the state are taken");
				ImGui.SameLine();
				bool mute = transition.mute;
				changed = EditorGui.Checkbox("M##mute", ref mute);
				context.Track(changed);
				if (changed) transition.mute = mute;
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Mute: never taken");
				ImGui.SameLine();
				if (EditorGui.SmallButton("^") && i > 0)
				{
					int index = i;
					Document.Edit("Reorder transitions", () => { transitions.RemoveAt(index); transitions.Insert(index - 1, transition); });
				}

				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Earlier transitions win when several could be taken");
				ImGui.PopID();
			}

			if (transitions.Count == 0)
			{
				ImGui.TextDisabled("None: drag the node's handle onto another node, or right click > Make Transition.");
			}
		}

		// ---------------------------------------------------------------- transitions

		private void DrawTransitions(AnimatorGraph.EdgeInfo edge)
		{
			AnimatorController controller = Controller;
			string from = edge.SourceKind == "any" ? "Any State" : edge.SourceKind == "entry" ? "Entry" : edge.Source is AnimatorState s ? s.name : edge.Source is AnimatorStateMachine m ? m.name : "?";
			string to = controller.DescribeDestination(edge.Transitions[0]);
			ImGui.TextUnformatted(from + "  ->  " + to);
			ImGui.SameLine();
			if (EditorGui.SmallButton("+ transition"))
			{
				window.Graph.Connect(edge.From, edge.To);
			}

			ImGui.Separator();
			for (int i = 0; i < edge.Transitions.Count; i++)
			{
				AnimatorTransition transition = edge.Transitions[i];
				ImGui.PushID((int)(transition.id & 0x7FFFFFFF));
				bool open = true;
				if (edge.Transitions.Count > 1)
				{
					if (!expandedTransitions.TryGetValue(transition.id, out open))
					{
						open = i == 0;
					}

					ImGui.SetNextItemOpen(open);
					string header = (string.IsNullOrEmpty(transition.name) ? "Transition " + (i + 1) : transition.name) + "  [" + transition.DescribeConditions(controller) + "]";
					open = EditorGui.CollapsingHeader(header);
					expandedTransitions[transition.id] = open;
				}

				if (open)
				{
					DrawTransition(transition, edge);
				}

				ImGui.PopID();
			}
		}

		private void DrawTransition(AnimatorTransition transition, AnimatorGraph.EdgeInfo edge)
		{
			AnimatorController controller = Controller;
			FieldDrawer.Label("Name", null, context);
			string name = transition.name ?? "";
			bool changed = EditorGui.InputTextWithHint("##transitionname", "(optional)", ref name, 64);
			context.Track(changed);
			if (changed) transition.name = name;

			// Destination (another state of the machine, a sub-machine or Exit).
			FieldDrawer.Label("Destination", null, context);
			if (EditorGui.BeginCombo("##destination", controller.DescribeDestination(transition)))
			{
				AnimatorStateMachine machine = window.Graph.Page.Machine;
				foreach (AnimatorState state in machine.states)
				{
					if (EditorGui.Selectable(state.name, transition.destinationState == state.id))
					{
						AnimatorState target = state;
						Document.Edit("Change destination", () => { transition.destinationState = target.id; transition.destinationMachine = 0; transition.toExit = false; });
					}
				}

				foreach (AnimatorStateMachine sub in machine.subMachines)
				{
					if (EditorGui.Selectable(sub.name + " (state machine)", transition.destinationMachine == sub.id))
					{
						AnimatorStateMachine target = sub;
						Document.Edit("Change destination", () => { transition.destinationState = 0; transition.destinationMachine = target.id; transition.toExit = false; });
					}
				}

				if (edge.SourceKind != "entry" && EditorGui.Selectable("Exit", transition.toExit))
				{
					Document.Edit("Change destination", () => { transition.destinationState = 0; transition.destinationMachine = 0; transition.toExit = true; });
				}

				EditorGui.EndCombo();
			}

			bool fromEntry = edge.SourceKind == "entry";
			if (!fromEntry)
			{
				ImGui.SeparatorText("Timing");
				object hasExit = transition.hasExitTime;
				if (FieldDrawer.DrawValue("Has Exit Time", typeof(bool), ref hasExit, null, context, 0)) transition.hasExitTime = (bool)hasExit;
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Without conditions: taken when the state's time passes the exit time (every cycle when looping).\nWith conditions: they count only after the exit time.");
				if (transition.hasExitTime)
				{
					object exitTime = transition.exitTime;
					if (FieldDrawer.DrawValue(transition.exitTimeInSeconds ? "Exit Time (s)" : "Exit Time (cycles)", typeof(float), ref exitTime, null, context, 0)) transition.exitTime = (float)exitTime;
					object inSeconds = transition.exitTimeInSeconds;
					if (FieldDrawer.DrawValue("Exit Time In Seconds", typeof(bool), ref inSeconds, null, context, 0)) transition.exitTimeInSeconds = (bool)inSeconds;
				}

				object fixedDuration = transition.fixedDuration;
				if (FieldDrawer.DrawValue("Fixed Duration", typeof(bool), ref fixedDuration, null, context, 0)) transition.fixedDuration = (bool)fixedDuration;
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Duration in seconds; off: in cycles of the source state");
				object duration = transition.duration;
				if (FieldDrawer.DrawValue(transition.fixedDuration ? "Duration (s)" : "Duration (cycles)", typeof(float), ref duration, null, context, 0)) transition.duration = Math.Max(0f, (float)duration);
				object offset = transition.offset;
				if (FieldDrawer.DrawValue("Offset (cycles)", typeof(float), ref offset, null, context, 0)) transition.offset = (float)offset;
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Where the destination starts");
				FieldDrawer.Label("Interruption", null, context);
				int interruption = (int)transition.interruptionSource;
				if (EditorGui.BeginCombo("##interruption", InterruptionNames[Math.Clamp(interruption, 0, InterruptionNames.Length - 1)]))
				{
					for (int i = 0; i < InterruptionNames.Length; i++)
					{
						if (EditorGui.Selectable(InterruptionNames[i], interruption == i))
						{
							var chosen = (TransitionInterruptionSource)i;
							Document.Edit("Change interruption source", () => transition.interruptionSource = chosen);
						}
					}

					EditorGui.EndCombo();
				}

				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Which transitions may cut this one short while it runs");
				if (transition.interruptionSource != TransitionInterruptionSource.None)
				{
					object ordered = transition.orderedInterruption;
					if (FieldDrawer.DrawValue("Ordered Interruption", typeof(bool), ref ordered, null, context, 0)) transition.orderedInterruption = (bool)ordered;
					if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only transitions listed before this one on the state may interrupt it");
				}

				if (edge.SourceKind == "any")
				{
					object self = transition.canTransitionToSelf;
					if (FieldDrawer.DrawValue("Can Transition To Self", typeof(bool), ref self, null, context, 0)) transition.canTransitionToSelf = (bool)self;
				}

				TransitionPreview(transition);
			}

			ImGui.SeparatorText("Conditions");
			DrawConditions(transition.conditions, transition, 0);
			ImGui.Spacing();
			object mute = transition.mute;
			if (FieldDrawer.DrawValue("Mute", typeof(bool), ref mute, null, context, 0)) transition.mute = (bool)mute;
			object solo = transition.solo;
			if (FieldDrawer.DrawValue("Solo", typeof(bool), ref solo, null, context, 0)) transition.solo = (bool)solo;
			FieldDrawer.Label("Note", null, context);
			string note = transition.note ?? "";
			bool noteChanged = EditorGui.InputTextMultiline("##transitionnote", ref note, 1024, new NVector2(-1, 40));
			context.Track(noteChanged);
			if (noteChanged) transition.note = note;
			if (EditorGui.Button("Delete Transition"))
			{
				Document.Edit("Delete transition", () => controller.RemoveTransition(transition));
				window.View.ClearSelection();
			}
		}

		/// <summary>Two bars: the source state's cycle with the exit point, and where the destination overlaps during the blend.</summary>
		private void TransitionPreview(AnimatorTransition transition)
		{
			AnimatorController controller = Controller;
			AnimatorState destination = transition.destinationState != 0 ? controller.FindState(transition.destinationState) : null;
			float sourceLength = 1f;
			if (window.Graph.EdgeInfos.TryGetValue(window.View.Primary, out AnimatorGraph.EdgeInfo info) && info.Source is AnimatorState source)
			{
				sourceLength = Math.Max(MotionLength(source.motion), 0.01f);
			}

			float destinationLength = Math.Max(MotionLength(destination?.motion), 0.01f);
			float exit = transition.hasExitTime ? (transition.exitTimeInSeconds ? transition.exitTime / sourceLength : transition.exitTime) : 0f;
			float durationSeconds = transition.fixedDuration ? transition.duration : transition.duration * sourceLength;
			float total = Math.Max(sourceLength * Math.Max(exit, 1f), (exit * sourceLength) + durationSeconds + 0.001f);
			float scale = Math.Max(total, (exit * sourceLength) + destinationLength * 0.5f);
			NVector2 min = ImGui.GetCursorScreenPos();
			float width = ImGui.GetContentRegionAvail().X;
			float height = 46f;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, min + new NVector2(width, height), ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.3f)), 3f);
			float px(float seconds) => min.X + 4f + (width - 8f) * Math.Clamp(seconds / scale, 0f, 1f);
			// Source bar (its cycles), exit point, destination bar from the exit point, blend band.
			draw.AddRectFilled(new NVector2(px(0f), min.Y + 6f), new NVector2(px(sourceLength), min.Y + 20f), ImGui.GetColorU32(new NVector4(0.4f, 0.6f, 0.9f, 0.8f)), 2f);
			float start = exit * sourceLength;
			draw.AddRectFilled(new NVector2(px(start), min.Y + 26f), new NVector2(px(start + destinationLength), min.Y + 40f), ImGui.GetColorU32(new NVector4(0.9f, 0.6f, 0.3f, 0.8f)), 2f);
			draw.AddRectFilled(new NVector2(px(start), min.Y + 4f), new NVector2(px(start + durationSeconds), min.Y + 42f), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.18f)));
			draw.AddLine(new NVector2(px(start), min.Y + 2f), new NVector2(px(start), min.Y + 44f), ImGui.GetColorU32(EditorStyle.Accent), 2f);
			ImGui.Dummy(new NVector2(width, height));
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip($"Source: {sourceLength:0.##} s, exit at {start:0.##} s; blend {durationSeconds:0.##} s into the destination ({destinationLength:0.##} s)");
			}
		}

		private static float MotionLength(Motion motion)
		{
			switch (motion)
			{
				case ClipMotion clip: return clip.clip?.length ?? 1f;
				case BlendTree tree:
				{
					float sum = 0f;
					int count = 0;
					foreach (BlendTreeChild child in tree.children)
					{
						sum += MotionLength(child.motion);
						count++;
					}

					return count > 0 ? sum / count : 1f;
				}
				default: return 1f;
			}
		}

		private void DrawConditions(List<AnimatorCondition> conditions, AnimatorTransition transition, int depth)
		{
			AnimatorController controller = Controller;
			for (int i = 0; i < conditions.Count; i++)
			{
				AnimatorCondition condition = conditions[i];
				ImGui.PushID(i);
				bool remove = false;
				switch (condition)
				{
					case ParameterCondition pc:
						remove = DrawParameterCondition(pc);
						break;
					case ConditionGroup group:
					{
						string header = (group.any ? "Any of" : "All of") + " (" + group.conditions.Count + ")";
						bool open = EditorGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.SpanAvailWidth);
						ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60f);
						if (EditorGui.SmallButton(group.any ? "any" : "all"))
						{
							Document.Edit("Toggle group mode", () => group.any = !group.any);
						}

						ImGui.SameLine();
						if (EditorGui.SmallButton("x")) remove = true;
						if (open)
						{
							ImGui.Indent(8f);
							DrawConditions(group.conditions, transition, depth + 1);
							ImGui.Unindent(8f);
							ImGui.TreePop();
						}

						break;
					}
					case ScriptCondition script:
					{
						ImGui.TextUnformatted(TypeSelector.DisplayName(script.GetType()));
						ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20f);
						if (EditorGui.SmallButton("x")) remove = true;
						ImGui.Indent(8f);
						FieldDrawer.DrawFields(script, context, depth + 1);
						ImGui.Unindent(8f);
						break;
					}
					case null:
						ImGui.TextDisabled("(empty)");
						ImGui.SameLine();
						if (EditorGui.SmallButton("x")) remove = true;
						break;
					default:
						ImGui.TextUnformatted(condition.GetType().Name);
						ImGui.SameLine();
						if (EditorGui.SmallButton("x")) remove = true;
						break;
				}

				ImGui.PopID();
				if (remove)
				{
					int index = i;
					Document.Edit("Remove condition", () => conditions.RemoveAt(index));
					break;
				}
			}

			if (conditions.Count == 0 && depth == 0)
			{
				ImGui.TextDisabled(transition.hasExitTime ? "No conditions: the exit time alone decides." : "No conditions: taken at once.");
			}

			if (EditorGui.SmallButton("+ Condition##" + depth))
			{
				ImGui.OpenPopup("##add_condition");
			}

			if (ImGui.BeginPopup("##add_condition"))
			{
				ImGui.TextDisabled("Parameter");
				foreach (AnimatorParameter parameter in controller.parameters)
				{
					if (EditorGui.Selectable(parameter.name + "  (" + parameter.TypeLabel + ")"))
					{
						AnimatorParameter chosen = parameter;
						Document.Edit("Add condition", () => conditions.Add(NewCondition(chosen)));
					}
				}

				if (controller.parameters.Count == 0) ImGui.TextDisabled("  (no parameters yet)");
				ImGui.Separator();
				if (EditorGui.Selectable("Group: any of")) Document.Edit("Add condition group", () => conditions.Add(new ConditionGroup { any = true }));
				if (EditorGui.Selectable("Group: all of")) Document.Edit("Add condition group", () => conditions.Add(new ConditionGroup { any = false }));
				List<Type> scripts = TypeSelector.ConcreteTypes(typeof(ScriptCondition));
				if (scripts.Count > 0)
				{
					ImGui.Separator();
					ImGui.TextDisabled("Script conditions");
					foreach (Type type in scripts)
					{
						if (EditorGui.Selectable(TypeSelector.DisplayName(type)))
						{
							Type chosen = type;
							Document.Edit("Add script condition", () => conditions.Add((AnimatorCondition)Activator.CreateInstance(chosen)));
						}
					}
				}

				ImGui.EndPopup();
			}
		}

		private static ParameterCondition NewCondition(AnimatorParameter parameter)
		{
			var condition = new ParameterCondition { parameter = parameter.id, value = parameter.defaultValue?.Clone() };
			List<ConditionMode> modes = ParameterCondition.ModesFor(parameter.defaultValue);
			condition.mode = modes.Count > 0 ? modes[0] : ConditionMode.If;
			return condition;
		}

		/// <summary>One parameter condition row: parameter, mode, value or other parameter; true when its remove button was pressed.</summary>
		private bool DrawParameterCondition(ParameterCondition pc)
		{
			AnimatorController controller = Controller;
			AnimatorParameter parameter = controller.FindParameter(pc.parameter);
			float width = ImGui.GetContentRegionAvail().X;
			float removeWidth = 24f;
			float columns = (width - removeWidth - 12f) / 3f;
			ImGui.SetNextItemWidth(columns);
			if (EditorGui.BeginCombo("##cparam", parameter?.name ?? "(missing)"))
			{
				foreach (AnimatorParameter candidate in controller.parameters)
				{
					if (EditorGui.Selectable(candidate.name, pc.parameter == candidate.id))
					{
						AnimatorParameter chosen = candidate;
						Document.Edit("Change condition parameter", () =>
						{
							pc.parameter = chosen.id;
							if (pc.value == null || pc.value.GetType() != chosen.defaultValue?.GetType()) pc.value = chosen.defaultValue?.Clone();
							List<ConditionMode> modes = ParameterCondition.ModesFor(chosen.defaultValue);
							if (!modes.Contains(pc.mode)) pc.mode = modes.Count > 0 ? modes[0] : ConditionMode.If;
							if (pc.otherParameter != 0 && controller.FindParameter(pc.otherParameter)?.ValueType != chosen.ValueType) pc.otherParameter = 0;
						});
					}
				}

				EditorGui.EndCombo();
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(columns);
			List<ConditionMode> allowed = parameter != null ? ParameterCondition.ModesFor(parameter.defaultValue) : new List<ConditionMode>((ConditionMode[])Enum.GetValues(typeof(ConditionMode)));
			if (EditorGui.BeginCombo("##cmode", ModeLabel(pc.mode)))
			{
				foreach (ConditionMode candidate in allowed)
				{
					if (EditorGui.Selectable(ModeLabel(candidate), pc.mode == candidate))
					{
						ConditionMode chosen = candidate;
						Document.Edit("Change condition mode", () => pc.mode = chosen);
					}
				}

				EditorGui.EndCombo();
			}

			bool needsValue = pc.mode == ConditionMode.Greater || pc.mode == ConditionMode.Less || pc.mode == ConditionMode.GreaterOrEqual || pc.mode == ConditionMode.LessOrEqual || pc.mode == ConditionMode.Equals || pc.mode == ConditionMode.NotEquals;
			ImGui.SameLine();
			if (needsValue && parameter != null)
			{
				if (pc.value == null || pc.value.GetType() != parameter.defaultValue?.GetType())
				{
					pc.value = parameter.defaultValue?.Clone();
				}

				ImGui.PushItemWidth(columns - 22f);
				if (pc.otherParameter != 0)
				{
					ImGui.SetNextItemWidth(columns - 22f);
					if (EditorGui.BeginCombo("##cother", controller.FindParameter(pc.otherParameter)?.name ?? "(missing)"))
					{
						foreach (AnimatorParameter candidate in controller.parameters)
						{
							if (candidate.id != parameter.id && candidate.ValueType == parameter.ValueType && EditorGui.Selectable(candidate.name, pc.otherParameter == candidate.id))
							{
								AnimatorParameter chosen = candidate;
								Document.Edit("Change compared parameter", () => pc.otherParameter = chosen.id);
							}
						}

						EditorGui.EndCombo();
					}
				}
				else if (pc.value != null)
				{
					ValueField("##cvalue", pc.value, context);
				}

				ImGui.PopItemWidth();
				ImGui.SameLine();
				bool other = pc.otherParameter != 0;
				if (EditorGui.SmallButton(other ? "P" : "#"))
				{
					if (other)
					{
						Document.Edit("Compare with a value", () => pc.otherParameter = 0);
					}
					else
					{
						AnimatorParameter first = controller.parameters.Find(p => p.id != parameter.id && p.ValueType == parameter.ValueType);
						if (first != null) Document.Edit("Compare with a parameter", () => pc.otherParameter = first.id);
					}
				}

				if (ImGui.IsItemHovered()) ImGui.SetTooltip(other ? "Comparing with another parameter (click: with a value)" : "Comparing with a value (click: with another parameter of the same type)");
			}
			else
			{
				ImGui.Dummy(new NVector2(columns, 0));
			}

			ImGui.SameLine();
			return EditorGui.SmallButton("x##cremove");
		}

		private static string ModeLabel(ConditionMode mode)
		{
			switch (mode)
			{
				case ConditionMode.If: return "true";
				case ConditionMode.IfNot: return "false";
				case ConditionMode.Greater: return ">";
				case ConditionMode.Less: return "<";
				case ConditionMode.GreaterOrEqual: return ">=";
				case ConditionMode.LessOrEqual: return "<=";
				case ConditionMode.Equals: return "==";
				case ConditionMode.NotEquals: return "!=";
				case ConditionMode.Trigger: return "trigger";
				case ConditionMode.IsNull: return "is null";
				case ConditionMode.IsNotNull: return "is not null";
				default: return mode.ToString();
			}
		}

		// ---------------------------------------------------------------- other nodes

		private void DrawMachineNode(AnimatorStateMachine machine)
		{
			AnimatorController controller = Controller;
			bool readOnly = window.Graph.ReadOnly;
			ImGui.TextUnformatted(machine.IsExternal ? "Sub-State Machine (asset)" : "Sub-State Machine");
			ImGui.SameLine();
			if (EditorGui.SmallButton(machine.IsExternal ? "Look Inside" : "Open"))
			{
				window.Graph.Open(machine.id);
			}

			ImGui.Separator();
			if (readOnly)
			{
				ImGui.TextDisabled("Part of " + window.Graph.Page.External.name + ": open that asset to edit it.");
				return;
			}

			FieldDrawer.Label("Name", null, context);
			string name = machine.name;
			bool renamed = EditorGui.InputText("##machinename", ref name, 64, ImGuiInputTextFlags.EnterReturnsTrue);
			if (renamed && name.Trim().Length > 0) window.Graph.Rename(machine.id, name.Trim());

			// The asset the node plays (null: its own content).
			object asset = machine.controller;
			object undoTarget = context.UndoTarget;
			context.UndoTarget = null;
			bool assetChanged = FieldDrawer.DrawValue("Controller Asset", typeof(AnimatorController), ref asset, null, context, 0);
			context.UndoTarget = undoTarget;
			if (ImGui.IsItemHovered()) ImGui.SetTooltip("Another .animator asset whose layer plays in this node (its states, transitions and behaviours); its parameters are fed from this controller's through the mapping below.");
			if (assetChanged)
			{
				window.Graph.SetExternal(machine, asset as AnimatorController);
			}

			if (machine.IsExternal)
			{
				DrawExternalMachine(machine);
			}
			else
			{
				FieldDrawer.Label("Default State", null, context);
				AnimatorState current = machine.FindStateById(machine.defaultState);
				if (EditorGui.BeginCombo("##machinedefault", current?.name ?? "(none)"))
				{
					foreach (AnimatorState state in machine.states)
					{
						if (EditorGui.Selectable(state.name, machine.defaultState == state.id))
						{
							AnimatorState chosen = state;
							Document.Edit("Set default state", () => machine.defaultState = chosen.id);
						}
					}

					EditorGui.EndCombo();
				}

				var states = new List<AnimatorState>();
				machine.CollectStates(states);
				ImGui.TextDisabled(states.Count + " states, " + machine.subMachines.Count + " nested machines");
			}

			ImGui.SeparatorText("Transitions from this machine (taken when a state inside exits)");
			DrawTransitionList(machine.transitions, machine.name);
			ColorCombo("Color", machine.colorIndex, index => Document.EditLayout("Color state machine", () => machine.colorIndex = index));
			FieldDrawer.Label("Note", null, context);
			string note = machine.note ?? "";
			bool noteChanged = EditorGui.InputTextMultiline("##machinenote", ref note, 1024, new NVector2(-1, 50));
			context.Track(noteChanged);
			if (noteChanged) machine.note = note;
		}

		/// <summary>The asset part of a sub-machine node: the layer played, the parameter mapping, the mapping helpers.</summary>
		private void DrawExternalMachine(AnimatorStateMachine machine)
		{
			AnimatorController owner = Controller;
			AnimatorController asset = machine.controller;
			bool loop = asset == owner || asset.Embeds(owner);
			if (loop)
			{
				ImGui.TextColored(EditorStyle.Error, asset.name + " embeds this controller: the loop is cut, the node plays nothing.");
			}

			if (asset.layers.Count > 1)
			{
				FieldDrawer.Label("Layer", null, context);
				string current = machine.controllerLayer >= 0 && machine.controllerLayer < asset.layers.Count ? asset.layers[machine.controllerLayer].name : "(missing)";
				if (EditorGui.BeginCombo("##assetlayer", current))
				{
					for (int i = 0; i < asset.layers.Count; i++)
					{
						if (EditorGui.Selectable(asset.layers[i].name, machine.controllerLayer == i))
						{
							int chosen = i;
							Document.Edit("Change asset layer", () => machine.controllerLayer = chosen);
						}
					}

					EditorGui.EndCombo();
				}
			}

			AnimatorStateMachine content = machine.ExternalMachine;
			var states = new List<AnimatorState>();
			content?.CollectStates(states);
			ImGui.TextDisabled(states.Count + " states in " + asset.name + (content != null ? ", default " + (content.FindStateById(content.defaultState)?.name ?? "(none)") : ""));
			ImGui.SameLine();
			if (EditorGui.SmallButton("Open asset"))
			{
				window.Show(asset);
			}

			ImGui.SeparatorText("Parameters of " + asset.name);
			ImGui.TextWrapped("Each parameter of the asset is fed from a parameter of this controller with the same type, or keeps a value of its own per animator (set from code as \"" + machine.name + "/<name>\").");
			if (asset.parameters.Count == 0)
			{
				ImGui.TextDisabled("The asset has no parameters.");
			}

			float width = ImGui.GetContentRegionAvail().X;
			foreach (AnimatorParameter child in asset.parameters)
			{
				ImGui.PushID((int)(child.id & 0x7FFFFFFF));
				ParameterMapping mapping = machine.FindMapping(child.id);
				AnimatorParameter parent = mapping != null ? owner.FindParameter(mapping.parentParameter) : null;
				bool bad = mapping != null && (parent == null || parent.ValueType != child.ValueType || parent.IsTrigger != child.IsTrigger);
				FieldDrawer.Label(child.name + "  (" + child.TypeLabel + ")", null, context);
				string preview = mapping == null ? "(own value: " + (child.defaultValue?.ToString() ?? "") + ")" : parent == null ? "(missing parameter)" : bad ? parent.name + " (wrong type)" : parent.name;
				if (bad) ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Error);
				bool open = EditorGui.BeginCombo("##map", preview);
				if (bad) ImGui.PopStyleColor();
				if (open)
				{
					if (EditorGui.Selectable("(own value: " + (child.defaultValue?.ToString() ?? "") + ")", mapping == null))
					{
						Document.Edit("Unmap parameter", () => machine.SetMapping(child.id, 0));
					}

					bool anyCandidate = false;
					foreach (AnimatorParameter candidate in owner.parameters)
					{
						if (candidate.ValueType != child.ValueType || candidate.IsTrigger != child.IsTrigger)
						{
							continue;
						}

						anyCandidate = true;
						if (EditorGui.Selectable(candidate.name, mapping != null && mapping.parentParameter == candidate.id))
						{
							AnimatorParameter chosen = candidate;
							Document.Edit("Map parameter", () => machine.SetMapping(child.id, chosen.id));
						}
					}

					if (!anyCandidate)
					{
						ImGui.TextDisabled("No " + child.TypeLabel + " parameter here yet.");
					}

					if (EditorGui.Selectable("+ New '" + child.name + "' here and map it"))
					{
						Document.Edit("Create and map parameter", () =>
						{
							AnimatorParameter created = owner.AddParameter(child.name, child.defaultValue?.Clone());
							machine.SetMapping(child.id, created.id);
						});
					}

					EditorGui.EndCombo();
				}

				ImGui.PopID();
			}

			if (asset.parameters.Count > 0)
			{
				if (EditorGui.SmallButton("Map by name"))
				{
					Document.Edit("Map parameters by name", () => AnimatorGraph.MapByName(machine, owner));
				}

				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Feed every unmapped parameter from a parameter of this controller with the same name and type");
				ImGui.SameLine();
				if (EditorGui.SmallButton("Create missing"))
				{
					Document.Edit("Create missing parameters", () =>
					{
						foreach (AnimatorParameter child in asset.parameters)
						{
							if (machine.FindMapping(child.id) != null)
							{
								continue;
							}

							AnimatorParameter parent = owner.FindParameter(child.name);
							if (parent == null || parent.ValueType != child.ValueType || parent.IsTrigger != child.IsTrigger)
							{
								parent = owner.AddParameter(child.name, child.defaultValue?.Clone());
							}

							machine.SetMapping(child.id, parent.id);
						}
					});
				}

				if (ImGui.IsItemHovered()) ImGui.SetTooltip("Add parameters of the same name, type and default to this controller for every unmapped one, and map them");
				ImGui.SameLine();
				if (EditorGui.SmallButton("Unmap all"))
				{
					Document.Edit("Unmap parameters", () => machine.parameterMappings.Clear());
				}
			}
		}

		private void DrawEntry(AnimatorStateMachine machine)
		{
			ImGui.TextUnformatted("Entry");
			ImGui.Separator();
			ImGui.TextWrapped("When the machine is entered, the first entry transition whose conditions hold picks the state; without one, the default state plays.");
			FieldDrawer.Label("Default State", null, context);
			AnimatorState current = machine.FindStateById(machine.defaultState);
			if (EditorGui.BeginCombo("##entrydefault", current?.name ?? "(none)"))
			{
				foreach (AnimatorState state in machine.states)
				{
					if (EditorGui.Selectable(state.name, machine.defaultState == state.id))
					{
						AnimatorState chosen = state;
						Document.Edit("Set default state", () => machine.defaultState = chosen.id);
					}
				}

				EditorGui.EndCombo();
			}

			ImGui.SeparatorText("Entry transitions (" + machine.entryTransitions.Count + ")");
			DrawTransitionList(machine.entryTransitions, "Entry");
		}

		private void DrawAnyState(AnimatorStateMachine machine)
		{
			ImGui.TextUnformatted("Any State");
			ImGui.Separator();
			ImGui.TextWrapped("Transitions from here are checked every frame, whatever state of this machine (or its sub-machines) plays, before the state's own transitions. Nested machines' Any State transitions come first.");
			ImGui.SeparatorText("Transitions (" + machine.anyStateTransitions.Count + ")");
			DrawTransitionList(machine.anyStateTransitions, "Any State");
		}

		private void DrawNote(LELEngine.Animation.GraphNote note)
		{
			ImGui.TextUnformatted("Note");
			ImGui.Separator();
			string text = note.text ?? "";
			bool changed = EditorGui.InputTextMultiline("##notetext", ref text, 4096, new NVector2(-1, 160));
			context.Track(changed);
			if (changed) note.text = text;
			ColorCombo("Color", note.colorIndex, index => Document.EditLayout("Color note", () => note.colorIndex = index));
		}

		private void DrawGroup(GraphGroupData group)
		{
			ImGui.TextUnformatted("Group");
			ImGui.Separator();
			FieldDrawer.Label("Title", null, context);
			string title = group.title ?? "";
			bool changed = EditorGui.InputText("##grouptitle", ref title, 64);
			context.Track(changed);
			if (changed) group.title = title;
			ColorCombo("Color", group.colorIndex, index => Document.EditLayout("Color group", () => group.colorIndex = index));
			ImGui.TextDisabled(group.members.Count + " members");
			if (EditorGui.Button("Add Selection"))
			{
				Document.EditLayout("Add to group", () =>
				{
					foreach (ulong id in window.View.Selection)
					{
						if (id != group.id && !group.members.Contains(id) && (window.Graph.StateOf(id) != null || window.Graph.SubMachineOf(id) != null || window.Graph.NoteDataOf(id) != null)) group.members.Add(id);
					}
				});
			}

			ImGui.SameLine();
			if (EditorGui.Button("Ungroup"))
			{
				Document.EditLayout("Ungroup", () => window.Graph.Page.Machine.groups.Remove(group));
				window.View.ClearSelection();
			}
		}

		private void ColorCombo(string label, int current, Action<int> set)
		{
			FieldDrawer.Label(label, null, context);
			if (EditorGui.BeginCombo("##color" + label, AnimatorGraph.PaletteNames[Math.Clamp(current, 0, AnimatorGraph.PaletteNames.Length - 1)]))
			{
				for (int i = 0; i < AnimatorGraph.PaletteNames.Length; i++)
				{
					NVector4 color = AnimatorGraph.Palette[i];
					ImGui.PushStyleColor(ImGuiCol.Text, new NVector4(Math.Min(color.X + 0.3f, 1f), Math.Min(color.Y + 0.3f, 1f), Math.Min(color.Z + 0.3f, 1f), 1f));
					if (EditorGui.Selectable(AnimatorGraph.PaletteNames[i], current == i))
					{
						set(i);
					}

					ImGui.PopStyleColor();
				}

				EditorGui.EndCombo();
			}
		}

		// ---------------------------------------------------------------- blend trees

		private void DrawBlendTree(BlendTree tree)
		{
			AnimatorController controller = Controller;
			ImGui.TextUnformatted("Blend Tree");
			ImGui.Separator();
			FieldDrawer.Label("Name", null, context);
			string name = tree.name ?? "";
			bool changed = EditorGui.InputText("##treename", ref name, 64);
			context.Track(changed);
			if (changed) tree.name = name;
			FieldDrawer.Label("Blend Type", null, context);
			if (EditorGui.BeginCombo("##blendtype", BlendTypeNames[(int)tree.blendType]))
			{
				for (int i = 0; i < BlendTypeNames.Length; i++)
				{
					if (EditorGui.Selectable(BlendTypeNames[i], (int)tree.blendType == i))
					{
						var chosen = (BlendTreeType)i;
						Document.Edit("Change blend type", () => tree.blendType = chosen);
					}
				}

				EditorGui.EndCombo();
			}

			if (tree.blendType != BlendTreeType.Direct)
			{
				ParameterCombo(tree.blendType == BlendTreeType.Simple1D ? "Parameter" : "Parameter X", ref tree.parameter, IsNumeric, "(none)", null);
				if (tree.blendType != BlendTreeType.Simple1D)
				{
					ParameterCombo("Parameter Y", ref tree.parameterY, IsNumeric, "(none)", null);
				}
			}

			if (tree.blendType == BlendTreeType.Simple1D)
			{
				object automate = tree.automateThresholds;
				if (FieldDrawer.DrawValue("Automate Thresholds", typeof(bool), ref automate, null, context, 0))
				{
					tree.automateThresholds = (bool)automate;
					if (tree.automateThresholds) tree.SpreadThresholds();
				}

				if (tree.automateThresholds)
				{
					object min = tree.minThreshold;
					if (FieldDrawer.DrawValue("Min", typeof(float), ref min, null, context, 0)) { tree.minThreshold = (float)min; tree.SpreadThresholds(); }
					object max = tree.maxThreshold;
					if (FieldDrawer.DrawValue("Max", typeof(float), ref max, null, context, 0)) { tree.maxThreshold = (float)max; tree.SpreadThresholds(); }
				}
			}

			ImGui.SeparatorText("Children (" + tree.children.Count + ")");
			for (int i = 0; i < tree.children.Count; i++)
			{
				BlendTreeChild child = tree.children[i];
				ImGui.PushID(i);
				if (EditorGui.TreeNodeEx(ChildTitle(tree, child, i), ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
				{
					DrawChildFields(tree, child, i);
					ImGui.TreePop();
				}

				ImGui.PopID();
			}

			if (EditorGui.Button("+ Clip"))
			{
				Document.Edit("Add blend tree child", () =>
				{
					BlendTreeChild child = tree.AddChild(new ClipMotion());
					if (!tree.automateThresholds && tree.children.Count > 1) child.threshold = tree.children[tree.children.Count - 2].threshold + 1f;
				});
			}

			ImGui.SameLine();
			if (EditorGui.Button("+ Blend Tree"))
			{
				Document.Edit("Add blend tree child", () => tree.AddChild(new BlendTree { name = "Blend Tree" }));
			}

			if (tree.blendType != BlendTreeType.Simple1D && tree.blendType != BlendTreeType.Direct)
			{
				ImGui.SeparatorText("Blend space");
				DrawBlendSpace(tree);
			}
		}

		private string ChildTitle(BlendTree tree, BlendTreeChild child, int index)
		{
			string motion = child.motion?.DisplayName ?? "(none)";
			switch (tree.blendType)
			{
				case BlendTreeType.Simple1D: return index + ": " + motion + "  @ " + child.threshold.ToString("0.##");
				case BlendTreeType.Direct: return index + ": " + motion + "  by " + (Controller.FindParameter(child.directParameter)?.name ?? "(no parameter)");
				default: return index + ": " + motion + "  @ (" + child.position.X.ToString("0.##") + ", " + child.position.Y.ToString("0.##") + ")";
			}
		}

		private void DrawTreeChild(BlendTree tree, BlendTreeChild child, int index)
		{
			ImGui.TextUnformatted("Blend Tree Child " + index);
			ImGui.Separator();
			DrawChildFields(tree, child, index);
		}

		private void DrawChildFields(BlendTree tree, BlendTreeChild child, int index)
		{
			if (child.motion is BlendTree nested)
			{
				ImGui.TextDisabled("Nested blend tree: " + nested.DisplayName + " (" + nested.children.Count + " children)");
				ImGui.SameLine();
				if (EditorGui.SmallButton("Open##nested"))
				{
					var path = new List<int>(window.Graph.Page.TreePath) { index };
					window.NavigateToTree(window.Graph.Page.TreeState, nested, path);
				}
			}
			else
			{
				var clipMotion = child.motion as ClipMotion;
				object clip = clipMotion?.clip;
				if (FieldDrawer.DrawValue("Clip", typeof(AnimationClip), ref clip, null, context, 0))
				{
					if (clipMotion == null) child.motion = clipMotion = new ClipMotion();
					clipMotion.clip = clip as AnimationClip;
				}
			}

			switch (tree.blendType)
			{
				case BlendTreeType.Simple1D:
				{
					ImGui.BeginDisabled(tree.automateThresholds);
					object threshold = child.threshold;
					if (FieldDrawer.DrawValue("Threshold", typeof(float), ref threshold, null, context, 0))
					{
						child.threshold = (float)threshold;
					}

					ImGui.EndDisabled();
					break;
				}
				case BlendTreeType.Direct:
					ParameterCombo("Weight Parameter", ref child.directParameter, IsNumeric, "(none)", null);
					break;
				default:
				{
					object position = child.position;
					if (FieldDrawer.DrawValue("Position", typeof(Vector2), ref position, null, context, 0)) child.position = (Vector2)position;
					break;
				}
			}

			object timeScale = child.timeScale;
			if (FieldDrawer.DrawValue("Time Scale", typeof(float), ref timeScale, null, context, 0)) child.timeScale = (float)timeScale;
			if (EditorGui.SmallButton("Remove##child" + index))
			{
				Document.Edit("Remove blend tree child", () =>
				{
					tree.children.Remove(child);
					if (tree.automateThresholds) tree.SpreadThresholds();
				});
			}

			if (index > 0)
			{
				ImGui.SameLine();
				if (EditorGui.SmallButton("Up##child" + index))
				{
					Document.Edit("Reorder children", () =>
					{
						tree.children.RemoveAt(index);
						tree.children.Insert(index - 1, child);
						if (tree.automateThresholds) tree.SpreadThresholds();
					});
				}
			}
		}

		/// <summary>The children's positions in the two parameters' plane, with the live point in play mode (drag a point to move the child).</summary>
		private void DrawBlendSpace(BlendTree tree)
		{
			float size = Math.Min(ImGui.GetContentRegionAvail().X, 220f);
			NVector2 min = ImGui.GetCursorScreenPos();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, min + new NVector2(size, size), ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.3f)), 3f);
			float range = 1f;
			foreach (BlendTreeChild child in tree.children)
			{
				range = Math.Max(range, Math.Max(Math.Abs(child.position.X), Math.Abs(child.position.Y)) * 1.2f);
			}

			NVector2 center = min + new NVector2(size * 0.5f, size * 0.5f);
			draw.AddLine(new NVector2(min.X, center.Y), new NVector2(min.X + size, center.Y), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.15f)));
			draw.AddLine(new NVector2(center.X, min.Y), new NVector2(center.X, min.Y + size), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.15f)));
			float scale = size * 0.5f / range;
			EditorGui.InvisibleButton("##blendspace", new NVector2(size, size));
			bool hovered = ImGui.IsItemHovered();
			NVector2 mouse = ImGui.GetMousePos();
			for (int i = 0; i < tree.children.Count; i++)
			{
				BlendTreeChild child = tree.children[i];
				NVector2 p = center + new NVector2(child.position.X * scale, -child.position.Y * scale);
				bool near = hovered && NVector2.DistanceSquared(mouse, p) < 64f;
				draw.AddCircleFilled(p, near ? 6f : 4f, ImGui.GetColorU32(near ? EditorStyle.Accent : new NVector4(0.9f, 0.6f, 0.3f, 1f)), 12);
				draw.AddText(p + new NVector2(7f, -7f), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.8f)), child.motion?.DisplayName ?? i.ToString());
				if (near && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
				{
					draggingChild = i;
					Document.BeginEdit("Move blend tree child");
				}
			}

			if (draggingChild >= 0 && draggingChild < tree.children.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				NVector2 local = (mouse - center) / scale;
				tree.children[draggingChild].position = new Vector2((float)Math.Round(local.X, 2), (float)Math.Round(-local.Y, 2));
			}
			else if (draggingChild >= 0)
			{
				draggingChild = -1;
				Document.EndEdit();
			}

			Animator live = window.LiveAnimator;
			if (live != null)
			{
				AnimatorController controller = Controller;
				float x = live.GetParameterState(controller.FindParameter(tree.parameter)?.name ?? "")?.AsFloat() ?? 0f;
				float y = live.GetParameterState(controller.FindParameter(tree.parameterY)?.name ?? "")?.AsFloat() ?? 0f;
				NVector2 p = center + new NVector2(x * scale, -y * scale);
				draw.AddCircle(p, 7f, ImGui.GetColorU32(EditorStyle.Accent), 16, 2f);
			}
		}

		private int draggingChild = -1;

		// ---------------------------------------------------------------- controller

		private void DrawControllerSettings(AnimatorController controller)
		{
			ImGui.TextUnformatted(controller.name);
			ImGui.SameLine();
			ImGui.TextDisabled("(animator controller)");
			ImGui.Separator();
			var states = new List<AnimatorState>();
			foreach (AnimatorLayer layer in controller.layers) layer.stateMachine.CollectStates(states);
			ImGui.TextDisabled(controller.layers.Count + (controller.layers.Count == 1 ? " layer, " : " layers, ") + states.Count + " states, " + controller.parameters.Count + " parameters, " + controller.CollectClips().Count + " clips");
			ImGui.Spacing();
			ImGui.TextWrapped("Select a state, a transition or a node to edit it. Right click the canvas to create states; drag a node's handle (or right click > Make Transition) to connect; double click a sub-state machine or blend tree state to open it. Drop .anim assets on the canvas to make states.");
			ImGui.SeparatorText("Defaults for new transitions");
			FieldDrawer.DrawFields(controller.transitionDefaults, context);
			if (window.LiveAnimator != null)
			{
				ImGui.SeparatorText("Live animator");
				Animator live = window.LiveAnimator;
				ImGui.TextUnformatted(live.gameObject.Name);
				object speed = live.speed;
				if (FieldDrawer.DrawValue("Speed", typeof(float), ref speed, null, liveContext, 0)) live.speed = (float)speed;
				if (live.Player != null)
				{
					ImGui.TextDisabled(live.Player.Layout.Slots.Count + " animated properties, " + live.Player.Layout.Unresolved.Count + " unresolved");
					foreach (string unresolved in live.Player.Layout.Unresolved)
					{
						ImGui.BulletText(unresolved);
					}
				}
			}
		}

		// ---------------------------------------------------------------- helpers

		/// <summary>A combo of the controller's parameters (filtered) writing the chosen id; "(none)" writes 0.</summary>
		private void ParameterCombo(string label, ref ulong parameterId, Func<AnimatorParameter, bool> filter, string noneLabel, string tooltip)
		{
			AnimatorController controller = Controller;
			FieldDrawer.Label(label, null, context);
			AnimatorParameter current = controller.FindParameter(parameterId);
			ulong chosenId = parameterId;
			bool changed = false;
			if (EditorGui.BeginCombo("##param_" + label, current?.name ?? (parameterId != 0 ? "(missing)" : noneLabel)))
			{
				if (EditorGui.Selectable(noneLabel, parameterId == 0))
				{
					chosenId = 0;
					changed = true;
				}

				foreach (AnimatorParameter parameter in controller.parameters)
				{
					if (filter != null && !filter(parameter))
					{
						continue;
					}

					if (EditorGui.Selectable(parameter.name + "  (" + parameter.TypeLabel + ")", parameterId == parameter.id))
					{
						chosenId = parameter.id;
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			if (tooltip != null && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(tooltip);
			}

			if (changed && chosenId != parameterId)
			{
				ulong before = parameterId;
				parameterId = chosenId;
				// The field was written by the ref: snapshot around it for undo.
				ulong after = chosenId;
				parameterId = before;
				Document.BeginEdit("Change parameter");
				parameterId = after;
				Document.EndEdit();
			}
		}

		#endregion
	}
}
