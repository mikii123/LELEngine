using System;
using System.Collections.Generic;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     Inspector-style widgets for custom inspectors and editor windows: a label column, then the editor's own widget
	///     for the value (numbers, vectors, colors, enums, strings, lists, scene references, assets). Every call is one
	///     line; mix them freely with raw ImGui calls. Inside <see cref="Editor.OnInspectorGUI" /> edits of the target get
	///     undo by themselves; <see cref="PropertyField" /> gets undo for any component it edits.
	///     Only valid inside <see cref="Editor.OnInspectorGUI" /> and <see cref="EditorWindow" /> OnGUI.
	/// </summary>
	public static class EditorGUILayout
	{
		#region PrivateFields

		private static InspectorContext context;
		private static Behaviour inspected;
		private static bool changed;

		/// <summary>Per tab strip (its ImGui id): the tab it showed last, and in which frame of which ImGui context (<see cref="Tabs" />).</summary>
		private static readonly Dictionary<uint, (int Tab, int Frame, ImGuiContextPtr Context)> shownTabs = new Dictionary<uint, (int, int, ImGuiContextPtr)>();

		#endregion

		#region PublicFields

		/// <summary>Width of the label column (pixels from the left edge of the content).</summary>
		public static float labelWidth
		{
			get => Context.LabelWidth;
			set => Context.LabelWidth = value;
		}

		private static InspectorContext Context => context ?? throw new InvalidOperationException("EditorGUILayout can only be used inside Editor.OnInspectorGUI or EditorWindow.OnGUI.");

		#endregion

		#region PublicMethods

		/// <summary>The label column; the next ImGui widget fills the rest of the line (give it a "##id" label).</summary>
		public static void PrefixLabel(string label)
		{
			FieldDrawer.Label(label, null, Context);
		}

		/// <summary>A labeled widget for <paramref name="value" />, chosen by its type; returns the (possibly edited) value.</summary>
		public static T Field<T>(string label, T value)
		{
			Field(label, ref value);
			return value;
		}

		/// <summary>A labeled widget for <paramref name="value" />, chosen by its type; true when it changed.</summary>
		public static bool Field<T>(string label, ref T value)
		{
			object boxed = value;
			ImGui.PushID(label);
			bool edited = FieldDrawer.DrawValue(label, typeof(T), ref boxed, null, Context, 0);
			ImGui.PopID();
			if (edited)
			{
				value = (T)boxed;
				changed = true;
			}

			return edited;
		}

		/// <summary>A curve's preview line; a click opens the curve editor (keys, tangents, presets). Returns the curve (created when null).</summary>
		public static AnimationCurve CurveField(string label, AnimationCurve curve)
		{
			Field(label, ref curve);
			return curve;
		}

		/// <summary>A gradient's bar; a click opens the gradient editor (mode, color and alpha keys). Returns the gradient (created when null).</summary>
		public static Gradient GradientField(string label, Gradient gradient)
		{
			Field(label, ref gradient);
			return gradient;
		}

		/// <summary>Unity's slider: drag it, or type the value into the number field beside it (clamped to min..max).</summary>
		public static float Slider(string label, float value, float min, float max)
		{
			PrefixLabel(label);
			ImGui.PushID(label);
			bool edited = EditorGui.SliderFloat("##v", ref value, min, max);
			Context.Track(edited);
			ImGui.PopID();
			changed |= edited;
			return value;
		}

		/// <summary>A whole-number slider with its number field (typed values clamped to min..max).</summary>
		public static int IntSlider(string label, int value, int min, int max)
		{
			PrefixLabel(label);
			ImGui.PushID(label);
			bool edited = EditorGui.SliderInt("##v", ref value, min, max);
			Context.Track(edited);
			ImGui.PopID();
			changed |= edited;
			return value;
		}

		/// <summary>
		///     A button (<paramref name="width" /> 0: as wide as its text, -1: the rest of the line); true when pressed. Unlike
		///     a raw ImGui.Button it is an automation element ("button:Label"): scripts and the CLI can press it.
		/// </summary>
		public static bool Button(string label, float width = 0f)
		{
			return EditorGui.Button(label, new System.Numerics.Vector2(width, 0f));
		}

		/// <summary>A checkbox after the label column; returns the (possibly toggled) value. Automation element "checkbox:Label".</summary>
		public static bool Toggle(string label, bool value)
		{
			PrefixLabel(label);
			bool edited = EditorGui.Checkbox("##" + label, ref value);
			Context.Track(edited);
			changed |= edited;
			return value;
		}

		/// <summary>
		///     Unity's Popup: a drop-down after the label column showing the selected option; returns the selected index
		///     (an index outside the options shows an empty box and stays as it is until one is picked). Options may
		///     repeat. Automation element "combo:Label", its options "item:Label/Option" while it is open.
		/// </summary>
		public static int Popup(string label, int selectedIndex, IReadOnlyList<string> options)
		{
			PrefixLabel(label);
			ImGui.SetNextItemWidth(-1f);
			int count = options?.Count ?? 0;
			int result = selectedIndex;
			if (EditorGui.BeginCombo("##" + label, selectedIndex >= 0 && selectedIndex < count ? options[selectedIndex] ?? "" : ""))
			{
				for (int i = 0; i < count; i++)
				{
					ImGui.PushID(i);
					if (EditorGui.Selectable(options[i] ?? "", i == selectedIndex))
					{
						result = i;
					}

					if (i == selectedIndex)
					{
						ImGui.SetItemDefaultFocus();
					}

					ImGui.PopID();
				}

				EditorGui.EndCombo();
			}

			bool edited = result != selectedIndex;
			Context.Track(edited);
			changed |= edited;
			return result;
		}

		/// <summary>
		///     A strip of tabs (Unity's Toolbar), one per label; returns the index of the tab that shows. A window keeps the
		///     index (in a serialized field, to open on the same tab again) and draws that tab's content after the call. An
		///     index other than the tab on show switches the strip to it: set from code, or restored with the window.
		///     <paramref name="id" /> tells the strips of one window apart. Automation elements "tab:Label".
		/// </summary>
		public static int Tabs(string id, int selected, IReadOnlyList<string> labels)
		{
			int count = labels?.Count ?? 0;
			uint key = ImGui.GetID("##" + id);
			if (count == 0 || !ImGui.BeginTabBar("##" + id))
			{
				return selected;
			}

			selected = Math.Clamp(selected, 0, count - 1);
			// Which tab shows is ImGui's own state: it is told when the caller's index is not the one it showed last, and
			// when that state is new (the strip was not drawn the frame before: first shown, hidden behind another window's
			// tab, or drawn for a screenshot), where it would start on the first tab again.
			int frame = ImGui.GetFrameCount();
			ImGuiContextPtr contextNow = ImGui.GetCurrentContext();
			bool continued = shownTabs.TryGetValue(key, out (int Tab, int Frame, ImGuiContextPtr Context) last) && last.Context.Equals(contextNow) && frame - last.Frame <= 1;
			bool force = !continued || last.Tab != selected;
			int open = selected;
			for (int i = 0; i < count; i++)
			{
				ImGui.PushID(i);
				if (EditorGui.BeginTabItem(labels[i] ?? "", force && i == selected ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
				{
					open = i;
					ImGui.EndTabItem();
				}

				ImGui.PopID();
			}

			ImGui.EndTabBar();
			// The switch asked for takes ImGui a frame: until then the asked tab counts as the one on show.
			int result = force ? selected : open;
			shownTabs[key] = (result, frame, contextNow);
			bool edited = result != selected;
			changed |= edited;
			return result;
		}

		/// <summary>A one-line text field after the label column; returns the (possibly edited) text. Automation element "input:Label".</summary>
		public static string TextField(string label, string text)
		{
			text ??= "";
			PrefixLabel(label);
			ImGui.SetNextItemWidth(-1f);
			bool edited = EditorGui.InputText("##" + label, ref text, 4096);
			Context.Track(edited);
			changed |= edited;
			return text;
		}

		/// <summary>A collapsible header; returns whether it is open. Automation element "header:Label".</summary>
		public static bool Foldout(string label, bool open = false)
		{
			return EditorGui.CollapsingHeader(label, open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
		}

		/// <summary>A read-only line: label column and text.</summary>
		public static void LabelField(string label, string text)
		{
			PrefixLabel(label);
			ImGui.TextUnformatted(text ?? "");
		}

		/// <summary>Wrapped message text, colored as a warning or an error when asked.</summary>
		public static void HelpBox(string message, MessageType type = MessageType.Info)
		{
			if (type != MessageType.Info)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, type == MessageType.Error ? EditorStyle.Error : EditorStyle.Warning);
			}

			// TextWrapped would treat '%' as a format string.
			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted(message ?? "");
			ImGui.PopTextWrapPos();
			if (type != MessageType.Info)
			{
				ImGui.PopStyleColor();
			}
		}

		/// <summary>
		///     The serialized field <paramref name="fieldName" /> of <paramref name="target" /> with the inspector's own
		///     widget (headers, tooltips, [Range] included). Edits of a component's field are undoable and mark the scene
		///     dirty, also from an editor window.
		/// </summary>
		/// <returns>True when the value changed.</returns>
		public static bool PropertyField(object target, string fieldName, string label = null)
		{
			InspectorContext current = Context;
			if (target == null)
			{
				ImGui.TextDisabled("(no target)");
				return false;
			}

			SerializedField field = FindField(target.GetType(), fieldName);
			if (field == null)
			{
				ImGui.TextColored(EditorStyle.Warning, "No serialized field '" + fieldName + "' in " + target.GetType().Name);
				return false;
			}

			foreach (HeaderAttribute header in field.Field.GetCustomAttributes<HeaderAttribute>(false))
			{
				ImGui.SeparatorText(header.Text);
			}

			object undoTarget = current.UndoTarget;
			Behaviour component = target as Behaviour;
			if (component != null)
			{
				current.UndoTarget = component;
			}

			bool edited;
			try
			{
				object value = field.GetValue(target);
				ImGui.PushID(field.Name);
				edited = FieldDrawer.DrawValue(label ?? field.DisplayName, field.FieldType, ref value, field.Field, current, 0);
				ImGui.PopID();
				if (edited)
				{
					field.SetValue(target, value);
				}
			}
			finally
			{
				current.UndoTarget = undoTarget;
			}

			if (edited)
			{
				changed = true;
				if (component != null && component != inspected)
				{
					EditorExtensions.Current.MarkChanged(component);
				}
			}

			return edited;
		}

		#endregion

		#region InternalMethods

		/// <summary>Starts a custom inspector's or window's draw (<paramref name="target" />: the inspected component).</summary>
		internal static void Begin(InspectorContext drawContext, Behaviour target)
		{
			context = drawContext;
			inspected = target;
			changed = false;
		}

		/// <summary>Ends the draw; true when a widget of this API changed a value.</summary>
		internal static bool End()
		{
			bool result = changed;
			context = null;
			inspected = null;
			changed = false;
			return result;
		}

		internal static bool DefaultFields(Behaviour target)
		{
			if (target == null)
			{
				return false;
			}

			bool edited = FieldDrawer.DrawFields(target, Context);
			changed |= edited;
			return edited;
		}

		#endregion

		#region PrivateMethods

		private static SerializedField FindField(Type type, string name)
		{
			foreach (SerializedField field in SerializationUtility.GetFields(type))
			{
				if (field.Name == name || field.Field.Name == name)
				{
					return field;
				}
			}

			return null;
		}

		#endregion
	}

	/// <summary>Kind of a <see cref="EditorGUILayout.HelpBox" /> message.</summary>
	public enum MessageType
	{
		Info,
		Warning,
		Error
	}
}
