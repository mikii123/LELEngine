using System;
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

		public static float Slider(string label, float value, float min, float max)
		{
			PrefixLabel(label);
			ImGui.PushID(label);
			bool edited = ImGui.SliderFloat("##v", ref value, min, max);
			Context.Track(edited);
			ImGui.PopID();
			changed |= edited;
			return value;
		}

		public static int IntSlider(string label, int value, int min, int max)
		{
			PrefixLabel(label);
			ImGui.PushID(label);
			bool edited = ImGui.SliderInt("##v", ref value, min, max);
			Context.Track(edited);
			ImGui.PopID();
			changed |= edited;
			return value;
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
