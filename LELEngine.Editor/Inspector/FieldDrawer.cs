using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>Per-panel state of field drawing: the undo target being edited and the label column width.</summary>
	internal sealed class InspectorContext
	{
		#region PublicFields

		public EditorApplication Editor;
		public Scene Scene;

		/// <summary>Object whose state an edit changes (component or scene settings): undo snapshots capture it.</summary>
		public object UndoTarget;

		public float LabelWidth = 130f;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Call right after a widget, before its new value is written: the edit's "before" state is captured on the
		///     first change, and the undo record is committed when the widget is released.
		/// </summary>
		public void Track(bool changed)
		{
			if (ImGui.IsItemActivated() || changed)
			{
				Editor.Edits.Begin(UndoTarget);
			}

			if (ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive()))
			{
				Editor.Edits.RequestCommit();
			}
		}

		/// <summary>
		///     Drops the scene objects the last draw used. Panels keep their context for the whole session, and a
		///     panel that stops drawing (hidden tab) must not keep an unloaded scene and its script assembly alive.
		/// </summary>
		public void Release()
		{
			Scene = null;
			UndoTarget = null;
		}

		#endregion
	}

	/// <summary>
	///     Inspector widgets for serialized fields (the same fields the scene file stores): numbers, bools, strings,
	///     enums, vectors, quaternions (as Euler angles), colors, arrays / lists, nested [Serializable] objects,
	///     references to scene objects (pick or drag from the hierarchy) and assets (pick or drag from the project).
	/// </summary>
	internal static class FieldDrawer
	{
		#region PublicMethods

		/// <summary>Draws every serialized field of <paramref name="target" />; true when one changed.</summary>
		public static bool DrawFields(object target, InspectorContext context, int depth = 0)
		{
			bool changed = false;
			foreach (SerializedField field in SerializationUtility.GetFields(target.GetType()))
			{
				if (field.HideInInspector)
				{
					continue;
				}

				foreach (HeaderAttribute header in field.Field.GetCustomAttributes<HeaderAttribute>(false))
				{
					ImGui.SeparatorText(header.Text);
				}

				object value = field.GetValue(target);
				ImGui.PushID(field.Name);
				if (DrawValue(field.DisplayName, field.FieldType, ref value, field.Field, context, depth))
				{
					field.SetValue(target, value);
					changed = true;
				}

				ImGui.PopID();
			}

			return changed;
		}

		public static bool DrawValue(string label, Type type, ref object value, FieldInfo info, InspectorContext context, int depth)
		{
			Type element = SerializationUtility.GetElementType(type);
			if (element != null)
			{
				return DrawList(label, type, element, ref value, context, depth);
			}

			if (IsNested(type))
			{
				return DrawNested(label, type, ref value, context, depth);
			}

			Label(label, info, context);
			bool changed = DrawLeaf(type, ref value, info, context);
			return changed;
		}

		/// <summary>Label column; the next widget fills the rest of the line.</summary>
		public static void Label(string label, FieldInfo info, InspectorContext context)
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			TooltipAttribute tooltip = info?.GetCustomAttribute<TooltipAttribute>(false);
			if (tooltip != null && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(tooltip.Text);
			}

			ImGui.SameLine(context.LabelWidth);
			ImGui.SetNextItemWidth(-1);
		}

		/// <summary>Euler angles in degrees (x = bank, y = heading, z = attitude) matching <see cref="QuaternionHelper.Euler(float, float, float)" />.</summary>
		public static Vector3 ToEuler(Quaternion q)
		{
			q.Normalize();
			double test = q.X * q.Y + q.Z * q.W;
			double heading, attitude, bank;
			if (test > 0.4999)
			{
				heading = 2 * Math.Atan2(q.X, q.W);
				attitude = Math.PI / 2;
				bank = 0;
			}
			else if (test < -0.4999)
			{
				heading = -2 * Math.Atan2(q.X, q.W);
				attitude = -Math.PI / 2;
				bank = 0;
			}
			else
			{
				double sqx = q.X * q.X;
				double sqy = q.Y * q.Y;
				double sqz = q.Z * q.Z;
				heading = Math.Atan2(2 * q.Y * q.W - 2 * q.X * q.Z, 1 - 2 * sqy - 2 * sqz);
				attitude = Math.Asin(2 * test);
				bank = Math.Atan2(2 * q.X * q.W - 2 * q.Y * q.Z, 1 - 2 * sqx - 2 * sqz);
			}

			const double toDegrees = 180.0 / Math.PI;
			return new Vector3((float)(bank * toDegrees), (float)(heading * toDegrees), (float)(attitude * toDegrees));
		}

		#endregion

		#region PrivateMethods

		private static bool IsNested(Type type)
		{
			return !type.IsPrimitive && !type.IsEnum && type != typeof(string) && !IsMath(type)
				&& !SerializationUtility.IsReferenceType(type) && !SerializationUtility.IsAssetType(type)
				&& type.IsDefined(typeof(SerializableAttribute), false);
		}

		private static bool IsMath(Type type)
		{
			return type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion)
				|| type == typeof(Color4) || type == typeof(Vector2i) || type == typeof(Vector3i) || type == typeof(Vector4i);
		}

		private static bool DrawLeaf(Type type, ref object value, FieldInfo info, InspectorContext context)
		{
			RangeAttribute range = info?.GetCustomAttribute<RangeAttribute>(false);
			bool changed = false;

			if (type == typeof(bool))
			{
				bool b = (bool)value;
				changed = ImGui.Checkbox("##v", ref b);
				context.Track(changed);
				value = b;
			}
			else if (type == typeof(float) || type == typeof(double))
			{
				float f = Convert.ToSingle(value);
				changed = range != null ? ImGui.SliderFloat("##v", ref f, range.Min, range.Max) : ImGui.DragFloat("##v", ref f, DragSpeed(f));
				context.Track(changed);
				value = type == typeof(float) ? (object)f : (double)f;
			}
			else if (type.IsPrimitive && type != typeof(char))
			{
				int i = Convert.ToInt32(value);
				changed = range != null ? ImGui.SliderInt("##v", ref i, (int)range.Min, (int)range.Max) : ImGui.DragInt("##v", ref i, 0.2f);
				context.Track(changed);
				if (changed)
				{
					value = Convert.ChangeType(Math.Max(i, MinValue(type)), type);
				}
			}
			else if (type == typeof(string) || type == typeof(char))
			{
				string s = value?.ToString() ?? "";
				changed = ImGui.InputText("##v", ref s, type == typeof(char) ? 2u : 2048u);
				context.Track(changed);
				value = type == typeof(char) ? (object)(s.Length > 0 ? s[0] : '\0') : s;
			}
			else if (type.IsEnum)
			{
				changed = DrawEnum(type, ref value);
				context.Track(changed);
			}
			else if (type == typeof(Vector2))
			{
				Vector2 v = (Vector2)value;
				NVector2 n = new NVector2(v.X, v.Y);
				changed = ImGui.DragFloat2("##v", ref n, 0.05f);
				context.Track(changed);
				value = new Vector2(n.X, n.Y);
			}
			else if (type == typeof(Vector3))
			{
				Vector3 v = (Vector3)value;
				NVector3 n = new NVector3(v.X, v.Y, v.Z);
				changed = ImGui.DragFloat3("##v", ref n, 0.05f);
				context.Track(changed);
				value = new Vector3(n.X, n.Y, n.Z);
			}
			else if (type == typeof(Vector4))
			{
				Vector4 v = (Vector4)value;
				NVector4 n = new NVector4(v.X, v.Y, v.Z, v.W);
				changed = ImGui.DragFloat4("##v", ref n, 0.05f);
				context.Track(changed);
				value = new Vector4(n.X, n.Y, n.Z, n.W);
			}
			else if (type == typeof(Quaternion))
			{
				Vector3 euler = ToEuler((Quaternion)value);
				NVector3 n = new NVector3(euler.X, euler.Y, euler.Z);
				changed = ImGui.DragFloat3("##v", ref n, 0.5f);
				context.Track(changed);
				if (changed)
				{
					value = QuaternionHelper.Euler(n.X, n.Y, n.Z);
				}
			}
			else if (type == typeof(Color4))
			{
				Color4 c = (Color4)value;
				NVector4 n = new NVector4(c.R, c.G, c.B, c.A);
				changed = ImGui.ColorEdit4("##v", ref n, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.Hdr);
				context.Track(changed);
				value = new Color4(n.X, n.Y, n.Z, n.W);
			}
			else if (type == typeof(Vector2i) || type == typeof(Vector3i) || type == typeof(Vector4i))
			{
				changed = DrawIntVector(type, ref value);
				context.Track(changed);
			}
			else if (SerializationUtility.IsReferenceType(type))
			{
				changed = DrawReference(type, ref value, context);
				context.Track(changed);
			}
			else if (SerializationUtility.IsAssetType(type))
			{
				changed = DrawAsset(type, ref value);
				context.Track(changed);
			}
			else
			{
				ImGui.TextDisabled(type.Name);
			}

			return changed;
		}

		private static float DragSpeed(float value)
		{
			return Math.Max(0.005f, Math.Abs(value) * 0.01f);
		}

		private static int MinValue(Type type)
		{
			return type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong) ? 0 : int.MinValue;
		}

		private static bool DrawEnum(Type type, ref object value)
		{
			bool changed = false;
			bool flags = type.IsDefined(typeof(FlagsAttribute), false);
			if (ImGui.BeginCombo("##v", value.ToString()))
			{
				long current = Convert.ToInt64(value);
				foreach (object option in Enum.GetValues(type))
				{
					long bits = Convert.ToInt64(option);
					bool selected = flags ? bits != 0 && (current & bits) == bits : current == bits;
					if (ImGui.Selectable(option.ToString(), selected))
					{
						current = flags ? (selected ? current & ~bits : current | bits) : bits;
						value = Enum.ToObject(type, current);
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			return changed;
		}

		private static bool DrawIntVector(Type type, ref object value)
		{
			bool changed;
			if (type == typeof(Vector2i))
			{
				Vector2i v = (Vector2i)value;
				int x = v.X, y = v.Y;
				int[] values = { x, y };
				changed = ImGui.DragInt2("##v", ref values[0], 0.2f);
				value = new Vector2i(values[0], values[1]);
			}
			else if (type == typeof(Vector3i))
			{
				Vector3i v = (Vector3i)value;
				int[] values = { v.X, v.Y, v.Z };
				changed = ImGui.DragInt3("##v", ref values[0], 0.2f);
				value = new Vector3i(values[0], values[1], values[2]);
			}
			else
			{
				Vector4i v = (Vector4i)value;
				int[] values = { v.X, v.Y, v.Z, v.W };
				changed = ImGui.DragInt4("##v", ref values[0], 0.2f);
				value = new Vector4i(values[0], values[1], values[2], values[3]);
			}

			return changed;
		}

		private static bool DrawReference(Type type, ref object value, InspectorContext context)
		{
			bool changed = false;
			string preview = value switch
			{
				null => "None",
				GameObject go => go.Name,
				Behaviour component => component.gameObject?.Name + " (" + component.GetType().Name + ")",
				_ => value.ToString()
			};

			if (ImGui.BeginCombo("##v", preview + "  [" + type.Name + "]"))
			{
				if (ImGui.Selectable("None", value == null))
				{
					value = null;
					changed = true;
				}

				if (context.Scene != null)
				{
					foreach (GameObject go in context.Scene.GameObjects)
					{
						object candidate = type == typeof(GameObject) ? go : go.GetComponent(type);
						if (candidate == null)
						{
							continue;
						}

						if (ImGui.Selectable(go.Name + "##" + go.Id, candidate == value))
						{
							value = candidate;
							changed = true;
						}
					}
				}

				ImGui.EndCombo();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(HierarchyPanel.DragType).IsNull && HierarchyPanel.Dragged != null)
				{
					object candidate = type == typeof(GameObject) ? HierarchyPanel.Dragged : HierarchyPanel.Dragged.GetComponent(type);
					if (candidate != null)
					{
						value = candidate;
						changed = true;
					}
				}

				ImGui.EndDragDropTarget();
			}

			return changed;
		}

		private static bool DrawAsset(Type type, ref object value)
		{
			bool changed = false;
			string current = (value as IAsset)?.AssetPath;
			if (ImGui.BeginCombo("##v", current ?? "None"))
			{
				if (ImGui.Selectable("None", current == null))
				{
					value = null;
					changed = true;
				}

				foreach (string path in AssetDatabase.GetAllAssetPaths(AssetExtensions(type)))
				{
					if (ImGui.Selectable(path, string.Equals(path, current, StringComparison.OrdinalIgnoreCase)))
					{
						value = AssetDatabase.Load(type, path);
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ProjectPanel.DragType).IsNull && ProjectPanel.DraggedAsset != null)
				{
					string path = ProjectPanel.DraggedAsset;
					foreach (string extension in AssetExtensions(type))
					{
						if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
						{
							value = AssetDatabase.Load(type, path);
							changed = true;
							break;
						}
					}
				}

				ImGui.EndDragDropTarget();
			}

			return changed;
		}

		/// <summary>File extensions of the assets a field of this type accepts.</summary>
		public static string[] AssetExtensions(Type type)
		{
			if (type == typeof(Mesh))
			{
				return new[] { ".obj" };
			}

			if (type == typeof(Shaders.Material))
			{
				return new[] { ".material" };
			}

			return new string[0];
		}

		private static bool DrawList(string label, Type type, Type element, ref object value, InspectorContext context, int depth)
		{
			IList list = value as IList ?? (type.IsArray ? Array.CreateInstance(element, 0) : (IList)Activator.CreateInstance(type));
			bool changed = false;
			bool open = ImGui.TreeNodeEx(label + "  [" + list.Count + "]", ImGuiTreeNodeFlags.SpanAvailWidth);
			if (open)
			{
				int count = list.Count;
				Label("Size", null, context);
				if (ImGui.InputInt("##size", ref count) && count >= 0 && count != list.Count)
				{
					// Lists are edited in place: capture the state before resizing.
					context.Editor.Edits.Begin(context.UndoTarget);
					list = Resize(list, type, element, Math.Min(count, 4096));
					context.Editor.Edits.RequestCommit();
					changed = true;
				}

				for (int i = 0; i < list.Count; i++)
				{
					ImGui.PushID(i);
					object item = list[i];
					if (DrawValue("Element " + i, element, ref item, null, context, depth + 1))
					{
						list[i] = item;
						changed = true;
					}

					ImGui.PopID();
				}

				ImGui.TreePop();
			}

			value = list;
			return changed;
		}

		private static IList Resize(IList list, Type type, Type element, int count)
		{
			if (type.IsArray)
			{
				Array array = Array.CreateInstance(element, count);
				for (int i = 0; i < Math.Min(count, list.Count); i++)
				{
					array.SetValue(list[i], i);
				}

				for (int i = list.Count; i < count; i++)
				{
					array.SetValue(DefaultValue(element, i > 0 ? list[list.Count - 1] : null), i);
				}

				return array;
			}

			while (list.Count > count)
			{
				list.RemoveAt(list.Count - 1);
			}

			while (list.Count < count)
			{
				list.Add(DefaultValue(element, list.Count > 0 ? list[list.Count - 1] : null));
			}

			return list;
		}

		// New elements copy the previous value types (as Unity does); objects start fresh.
		private static object DefaultValue(Type element, object previous)
		{
			if (element.IsValueType)
			{
				return previous ?? Activator.CreateInstance(element);
			}

			if (element == typeof(string))
			{
				return previous ?? "";
			}

			return IsNested(element) ? Activator.CreateInstance(element) : null;
		}

		private static bool DrawNested(string label, Type type, ref object value, InspectorContext context, int depth)
		{
			if (depth >= SerializationUtility.MaxDepth)
			{
				return false;
			}

			bool changed = false;
			bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth);
			if (open)
			{
				if (value == null)
				{
					if (ImGui.Button("Create " + type.Name))
					{
						context.Editor.Edits.Begin(context.UndoTarget);
						value = Activator.CreateInstance(type);
						context.Editor.Edits.RequestCommit();
						changed = true;
					}
				}
				else
				{
					changed = DrawFields(value, context, depth + 1);
				}

				ImGui.TreePop();
			}

			return changed;
		}

		#endregion
	}
}
