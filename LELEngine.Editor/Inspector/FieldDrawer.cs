using System;
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
	/// <summary>
	///     Decorations a list or dictionary row adds to the first line of the field it draws (any kind of field: a
	///     number, an object field, a foldout): a prefix before it (the drag handle), a compact label in place of the
	///     label column (the element's index) and a suffix at the end of the line (the remove button).
	/// </summary>
	internal sealed class RowDecoration
	{
		public Action Prefix;

		/// <summary>Replaces the label column: the text (null: the field's own label in the label column; "": none).</summary>
		public string Label;

		/// <summary>Width the compact label takes (aligned indices).</summary>
		public float LabelWidth;

		/// <summary>Text of a foldout header in place of <see cref="Label" /> (a dictionary value's class name); null: <see cref="Label" />.</summary>
		public string HeaderLabel;

		public Action Suffix;
		public float SuffixWidth;
	}

	/// <summary>Per-panel state of field drawing: the undo target being edited and the label column width.</summary>
	internal sealed class InspectorContext
	{
		#region PublicFields

		public EditorApplication Editor;
		public Scene Scene;

		/// <summary>Object whose state an edit changes (component or scene settings): undo snapshots capture it.</summary>
		public object UndoTarget;

		public float LabelWidth = 130f;

		/// <summary>Decorations of the next field line (list and dictionary rows): the line that draws them takes them.</summary>
		public RowDecoration PendingRow;

		/// <summary>The prefab overrides of the object shown (null: not part of a prefab instance).</summary>
		public PrefabInstanceOverrides Overrides;

		/// <summary>Id in the prefab of the component (or GameObject) whose fields are drawn: the target of their overrides (0: none).</summary>
		public ulong OverrideTarget;

		/// <summary>The list or dictionary being drawn has [InlineEditor]: its elements' ScriptableObjects unfold.</summary>
		public bool InlineElements;

		/// <summary>Property path of the field being drawn ("weapon.damage", "drops[2]", "loot{gold}"): kept by the drawers.</summary>
		public string Path = "";

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

		/// <summary>Before a change that is not a widget's (a button, a drop, a list operation): captures the "before" state.</summary>
		public void BeginEdit()
		{
			Editor?.Edits.Begin(UndoTarget);
		}

		/// <summary>After such a change: the undo record is committed at the end of the frame.</summary>
		public void EndEdit()
		{
			Editor?.Edits.RequestCommit();
		}

		/// <summary>
		///     Drops the scene objects the last draw used. Panels keep their context for the whole session, and a
		///     panel that stops drawing (hidden tab) must not keep an unloaded scene and its script assembly alive.
		/// </summary>
		public void Release()
		{
			Scene = null;
			UndoTarget = null;
			PendingRow = null;
			Overrides = null;
			OverrideTarget = 0;
			Path = "";
		}

		/// <summary>
		///     The field being drawn is a prefab override (a value of its own, or part of a whole value replaced); with
		///     <paramref name="within" /> also when something inside it is (foldouts of objects and collections).
		/// </summary>
		public bool IsOverridden(bool within)
		{
			return Overrides != null && OverrideTarget != 0 && !string.IsNullOrEmpty(Path) && Overrides.IsOverridden(OverrideTarget, Path, within);
		}

		#endregion
	}

	/// <summary>
	///     Inspector widgets for serialized fields (the same fields the scene file stores): numbers, bools, strings,
	///     enums, vectors, quaternions (as Euler angles), colors, nested [Serializable] objects, object fields for
	///     references (scene objects, assets, ScriptableObjects: drag and drop, picker), Odin-style polymorphic fields
	///     (interfaces and base classes: a reference, or an instance of a class chosen from the arrow's list), and
	///     reorderable lists and dictionaries (<see cref="CollectionDrawers" />).
	/// </summary>
	internal static class FieldDrawer
	{
		#region PublicMethods

		/// <summary>
		///     Draws every serialized field of <paramref name="target" /> (only those <paramref name="filter" /> accepts,
		///     when given); true when one changed.
		/// </summary>
		public static bool DrawFields(object target, InspectorContext context, int depth = 0, Func<SerializedField, bool> filter = null)
		{
			bool changed = false;
			foreach (SerializedField field in SerializationUtility.GetFields(target.GetType()))
			{
				if (field.HideInInspector || (filter != null && !filter(field)))
				{
					continue;
				}

				foreach (HeaderAttribute header in field.Field.GetCustomAttributes<HeaderAttribute>(false))
				{
					ImGui.SeparatorText(header.Text);
				}

				object value = field.GetValue(target);
				NVector2 lineStart = ImGui.GetCursorScreenPos();
				float lineRight = lineStart.X + ImGui.GetContentRegionAvail().X;
				string parentPath = context.Path;
				context.Path = LELEngine.Serialization.PropertyPath.Field(parentPath, field.Name);
				ImGui.PushID(field.Name);
				try
				{
					if (DrawValue(field.DisplayName, field.FieldType, ref value, field.Field, context, depth))
					{
						field.SetValue(target, value);
						changed = true;
					}
				}
				finally
				{
					context.Path = parentPath;
				}

				ImGui.PopID();
				if (TestAnchors.Enabled)
				{
					// Nested fields (objects, inline editors) apart, so they never move a top-level anchor.
					TestAnchors.Record((depth == 0 ? "field:" : "nestedfield:") + target.GetType().Name + "." + field.Name, lineStart, new NVector2(lineRight, lineStart.Y + ImGui.GetFrameHeight()));
				}
			}

			return changed;
		}

		/// <summary>Test runs: every foldout of the inspector opens (screenshots of nested fields, lists and dictionaries).</summary>
		public static bool ForceOpen;

		/// <summary>ImGui id of the last foldout <see cref="Header" /> drew (its open state lives in the window's storage under it).</summary>
		public static uint LastHeaderId { get; private set; }

		/// <summary>Opens the foldout with this id on the next frame (a list after its add button).</summary>
		public static void OpenHeader(uint headerId)
		{
			ImGui.GetStateStorage().SetBool(headerId, true);
		}

		public static bool DrawValue(string label, Type type, ref object value, FieldInfo info, InspectorContext context, int depth)
		{
			if (depth > SerializationUtility.MaxDepth)
			{
				RowDecoration row = BeginLine(label, info, context);
				ImGui.TextDisabled("(nested too deep)");
				EndLine(row);
				return false;
			}

			if (SerializationUtility.GetElementType(type) != null)
			{
				return CollectionDrawers.DrawList(label, type, ref value, info, context, depth);
			}

			if (SerializationUtility.IsDictionaryType(type))
			{
				return CollectionDrawers.DrawDictionary(label, type, ref value, info, context, depth);
			}

			if (type == typeof(LayerMask))
			{
				RowDecoration row = BeginLine(label, info, context);
				bool maskChanged = DrawLayerMask(ref value, context);
				EndLine(row);
				return maskChanged;
			}

			if (type == typeof(AnimationCurve))
			{
				// A preview that opens the curve editor.
				return CurveEditor.DrawField(label, ref value, info, context);
			}

			if (type == typeof(LELEngine.Audio.AudioEventReference))
			{
				// An event of the audio project, picked from a list.
				RowDecoration row = BeginLine(label, info, context);
				bool eventChanged = Audio.AudioInspectors.DrawEventReferenceCombo(ref value, context);
				context.Track(eventChanged);
				EndLine(row);
				return eventChanged;
			}

			if (SerializationUtility.IsReferenceType(type) || SerializationUtility.IsAssetType(type))
			{
				return DrawReference(label, type, ref value, info, context, depth);
			}

			if (TypeSelector.IsPolymorphic(type))
			{
				return DrawPolymorphic(label, type, ref value, info, context, depth);
			}

			if (IsNested(type))
			{
				return DrawNested(label, type, ref value, info, context, depth);
			}

			RowDecoration line = BeginLine(label, info, context);
			bool changed = DrawLeaf(type, ref value, info, context);
			EndLine(line);
			return changed;
		}

		/// <summary>Label column; the next widget fills the rest of the line.</summary>
		public static void Label(string label, FieldInfo info, InspectorContext context)
		{
			ImGui.AlignTextToFramePadding();
			OverrideLabel(label, context, false, false);
			ShowTooltip(info);
			ImGui.SameLine(context.LabelWidth);
			ImGui.SetNextItemWidth(-1);
		}

		/// <summary>A field's label text; a prefab override's in bold with the override bar and its Apply / Revert menu (right click).</summary>
		private static void OverrideLabel(string label, InspectorContext context, bool disabled, bool within)
		{
			bool overridden = context.IsOverridden(within);
			bool bold = overridden && EditorFonts.PushBold();
			if (disabled && !overridden)
			{
				ImGui.TextDisabled(label);
			}
			else
			{
				ImGui.TextUnformatted(label);
			}

			EditorFonts.Pop(bold);
			if (overridden)
			{
				PrefabInspector.MarkOverride(context);
			}
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

		/// <summary>What a closed layer mask field shows: Nothing, Everything, the layer names, or Mixed.</summary>
		public static string LayerMaskPreview(int mask)
		{
			if (mask == 0)
			{
				return "Nothing";
			}

			if (mask == -1)
			{
				return "Everything";
			}

			string names = null;
			int count = 0;
			for (int layer = 0; layer < LayerMask.LayerCount; layer++)
			{
				if ((mask & (1 << layer)) == 0)
				{
					continue;
				}

				count++;
				names = names == null ? LayerMask.DisplayName(layer) : names + ", " + LayerMask.DisplayName(layer);
			}

			return count <= 2 ? names : "Mixed (" + count + " layers)";
		}

		/// <summary>File extensions of the assets a field of this type accepts.</summary>
		public static string[] AssetExtensions(Type type)
		{
			return AssetDatabase.GetExtensions(type);
		}

		// ---------------------------------------------------------------- lines (used by the collection drawers)

		/// <summary>
		///     The start of a one-line field: the label column, or the decorations of a list / dictionary row (prefix,
		///     compact label); sets the width of the next widget so the row's suffix fits after it.
		/// </summary>
		internal static RowDecoration BeginLine(string label, FieldInfo info, InspectorContext context)
		{
			RowDecoration row = context.PendingRow;
			context.PendingRow = null;
			if (row == null)
			{
				Label(label, info, context);
				return null;
			}

			float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
			if (row.Prefix != null)
			{
				row.Prefix();
				ImGui.SameLine();
			}

			if (row.Label == null)
			{
				ImGui.AlignTextToFramePadding();
				OverrideLabel(label, context, false, false);
				ShowTooltip(info);
				ImGui.SameLine(context.LabelWidth);
			}
			else if (row.Label.Length > 0)
			{
				NVector2 start = ImGui.GetCursorScreenPos();
				ImGui.AlignTextToFramePadding();
				OverrideLabel(row.Label, context, true, false);
				ImGui.SameLine();
				if (row.LabelWidth > 0f)
				{
					ImGui.SetCursorScreenPos(new NVector2(Math.Max(ImGui.GetCursorScreenPos().X, start.X + row.LabelWidth), ImGui.GetCursorScreenPos().Y));
				}
			}
			else if (context.IsOverridden(false))
			{
				// A dictionary cell has no label: the bar alone marks it.
				PrefabInspector.Bar(ImGui.GetCursorScreenPos().Y, ImGui.GetCursorScreenPos().Y + ImGui.GetFrameHeight());
			}

			float suffix = row.Suffix != null ? row.SuffixWidth + ImGui.GetStyle().ItemSpacing.X : 0f;
			ImGui.SetNextItemWidth(Math.Max(24f, right - ImGui.GetCursorScreenPos().X - suffix));
			return row;
		}

		/// <summary>The end of a one-line field: the row's suffix after the widget.</summary>
		internal static void EndLine(RowDecoration row)
		{
			if (row?.Suffix != null)
			{
				ImGui.SameLine();
				row.Suffix();
			}
		}

		/// <summary>
		///     A foldout header line (nested objects, collections, polymorphic instances): the row's prefix, the
		///     arrow and label, then <paramref name="valueArea" /> from the label column on (given its width; type
		///     selectors, counts, buttons), then the row's suffix at the end. <paramref name="onHeaderItem" /> runs right
		///     after the arrow (the header is the last item then: drop targets, context menus). True when open (call
		///     TreePop after the body).
		/// </summary>
		internal static bool Header(string label, FieldInfo info, InspectorContext context, Action<float> valueArea, Action onHeaderItem = null, string tooltip = null)
		{
			RowDecoration row = context.PendingRow;
			context.PendingRow = null;
			float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
			if (row?.Prefix != null)
			{
				row.Prefix();
				ImGui.SameLine();
			}

			float y = ImGui.GetCursorScreenPos().Y;
			string text = row != null ? row.HeaderLabel ?? row.Label ?? label : label;
			if (ForceOpen)
			{
				ImGui.SetNextItemOpen(true, ImGuiCond.Always);
			}

			ImGui.SetNextItemAllowOverlap();
			LastHeaderId = ImGui.GetID(text + "##header");
			// Prefab overrides inside (or of) the value: the foldout's label in bold, with the bar and the Apply / Revert menu.
			bool overridden = context.IsOverridden(true);
			bool bold = overridden && EditorFonts.PushBold();
			bool open = ImGui.TreeNodeEx(text + "##header", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.FramePadding);
			EditorFonts.Pop(bold);
			NVector2 headerMin = ImGui.GetItemRectMin();
			if (ImGui.IsItemHovered() && !DragAndDrop.IsDragging)
			{
				string hint = tooltip ?? info?.GetCustomAttribute<TooltipAttribute>(false)?.Text;
				if (hint != null)
				{
					ImGui.SetTooltip(hint);
				}
			}

			onHeaderItem?.Invoke();
			if (overridden)
			{
				PrefabInspector.MarkOverride(context);
			}

			float suffixWidth = row?.Suffix != null ? row.SuffixWidth + ImGui.GetStyle().ItemSpacing.X : 0f;
			if (valueArea != null)
			{
				float labelEnd = headerMin.X + ImGui.GetTreeNodeToLabelSpacing() + ImGui.CalcTextSize(text).X + ImGui.GetStyle().ItemSpacing.X;
				ImGui.SameLine(context.LabelWidth);
				float start = Math.Max(ImGui.GetCursorScreenPos().X, labelEnd);
				float width = right - suffixWidth - start;
				ImGui.SetCursorScreenPos(new NVector2(start, y));
				if (width > 12f)
				{
					valueArea(width);
				}
				else
				{
					ImGui.Dummy(new NVector2(1f, ImGui.GetFrameHeight()));
				}
			}

			if (row?.Suffix != null)
			{
				ImGui.SameLine();
				ImGui.SetCursorScreenPos(new NVector2(right - row.SuffixWidth, y));
				row.Suffix();
			}

			return open;
		}

		/// <summary>A new instance of a class for a field (its fields copied from <paramref name="previous" /> where they match), or null.</summary>
		internal static object CreateInstance(Type type, object previous)
		{
			object instance;
			try
			{
				instance = Activator.CreateInstance(type, true);
			}
			catch (Exception e)
			{
				Debug.LogError($"[Editor] Cannot create {type.Name}: {e.GetBaseException().Message}");
				return previous;
			}

			if (previous != null)
			{
				// Fields with the same name and type keep their values across the type change (as far as they fit).
				foreach (SerializedField target in SerializationUtility.GetFields(type))
				{
					foreach (SerializedField source in SerializationUtility.GetFields(previous.GetType()))
					{
						if (source.Name == target.Name && target.FieldType.IsAssignableFrom(source.FieldType))
						{
							target.SetValue(instance, source.GetValue(previous));
							break;
						}
					}
				}
			}

			return instance;
		}

		/// <summary>A value for a new list element or dictionary entry: the previous value type, "" for strings, a new [Serializable] object.</summary>
		internal static object DefaultValue(Type element, object previous)
		{
			if (element.IsValueType)
			{
				return previous ?? Activator.CreateInstance(element);
			}

			if (element == typeof(string))
			{
				return previous ?? "";
			}

			if (SerializationUtility.GetElementType(element) != null)
			{
				return element.IsArray ? Array.CreateInstance(SerializationUtility.GetElementType(element), 0) : Activator.CreateInstance(element);
			}

			if (SerializationUtility.IsDictionaryType(element))
			{
				return Activator.CreateInstance(element);
			}

			return IsNested(element) && !TypeSelector.IsPolymorphic(element) ? Activator.CreateInstance(element, true) : null;
		}

		#endregion

		#region PrivateMethods

		private static void ShowTooltip(FieldInfo info)
		{
			TooltipAttribute tooltip = info?.GetCustomAttribute<TooltipAttribute>(false);
			if (tooltip != null && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(tooltip.Text);
			}
		}

		private static bool IsNested(Type type)
		{
			return !type.IsPrimitive && !type.IsEnum && type != typeof(string) && !SerializationUtility.IsMathType(type)
				&& !SerializationUtility.IsReferenceType(type) && !SerializationUtility.IsAssetType(type)
				&& type.IsDefined(typeof(SerializableAttribute), false);
		}

		/// <summary>
		///     A reference (GameObject, component, prefab, asset, ScriptableObject) as an object field. With [InlineEditor]
		///     (on the field, or on the list / dictionary holding it) what it points at can be unfolded and edited in place
		///     (<see cref="InlineEditors" />).
		/// </summary>
		private static bool DrawReference(string label, Type type, ref object value, FieldInfo info, InspectorContext context, int depth)
		{
			if (Inlines(value, info, context, depth))
			{
				object target = value;
				object current = value;
				bool fieldChanged = false;
				bool open = Header(label, info, context, width =>
				{
					ImGui.SetNextItemWidth(width);
					fieldChanged = ObjectField.Draw(type, ref current, context);
					context.Track(fieldChanged);
				}, null, InlineEditors.Hint(target));
				if (fieldChanged)
				{
					value = current;
				}

				if (open)
				{
					if (!fieldChanged)
					{
						InlineEditors.Draw(target, context, depth + 1);
					}

					ImGui.TreePop();
				}

				return fieldChanged;
			}

			RowDecoration row = BeginLine(label, info, context);
			bool changed = ObjectField.Draw(type, ref value, context);
			context.Track(changed);
			EndLine(row);
			return changed;
		}

		/// <summary>The field (or the list / dictionary holding the value) has [InlineEditor] and the value can unfold.</summary>
		private static bool Inlines(object value, FieldInfo info, InspectorContext context, int depth)
		{
			bool inline = info != null ? info.IsDefined(typeof(InlineEditorAttribute), true) : context.InlineElements;
			return inline && depth < SerializationUtility.MaxDepth - 1 && InlineEditors.CanInline(value);
		}

		/// <summary>The fields of a ScriptableObject asset drawn inside another inspector (edits are the asset's: undo, save, OnValidate).</summary>
		internal static bool DrawScriptableObject(ScriptableObject asset, InspectorContext context, int depth)
		{
			object previousTarget = context.UndoTarget;
			PrefabInstanceOverrides previousOverrides = context.Overrides;
			string previousPath = context.Path;
			context.UndoTarget = asset;
			// The asset's fields are its own, not the prefab instance's.
			context.Overrides = null;
			context.Path = "";
			bool changed;
			try
			{
				changed = DrawFields(asset, context, depth);
			}
			finally
			{
				context.UndoTarget = previousTarget;
				context.Overrides = previousOverrides;
				context.Path = previousPath;
			}

			if (changed)
			{
				asset.Validate();
				context.Editor?.NotifySceneChanged();
			}

			return changed;
		}

		/// <summary>
		///     An interface, abstract class, EngineObject or base class field (Odin-style): a reference (a component, a
		///     GameObject or a ScriptableObject asset fitting it, dropped or picked) on one line, or an instance of a class
		///     chosen from the arrow's list, unfolded with its fields. The class of an instance can be changed in place.
		/// </summary>
		private static bool DrawPolymorphic(string label, Type declaredType, ref object value, FieldInfo info, InspectorContext context, int depth)
		{
			const string popupId = "##type_popup";
			bool canReference = SerializationUtility.CanHoldReference(declaredType);
			List<Type> creatable = TypeSelector.ConcreteTypes(declaredType);
			string hint = PolymorphicHint(declaredType, canReference, creatable.Count);
			if (value == null || value is EngineObject)
			{
				object current = value;
				bool changed = false;
				// The reference (and the arrow creating an instance) on one line: a plain field line, or the header of
				// an [InlineEditor] foldout.
				void DrawLine(float width)
				{
					float arrow = creatable.Count > 0 ? ImGui.GetFrameHeight() : 0f;
					float fieldWidth = Math.Max(24f, width - (arrow > 0f ? arrow + ImGui.GetStyle().ItemInnerSpacing.X : 0f));
					if (canReference)
					{
						ImGui.SetNextItemWidth(fieldWidth);
						bool picked = ObjectField.Draw(declaredType, ref current, context);
						context.Track(picked);
						changed |= picked;
					}
					else
					{
						NVector2 start = ImGui.GetCursorScreenPos();
						float height = ImGui.GetFrameHeight();
						ImGui.InvisibleButton("##none", new NVector2(fieldWidth, height));
						ImGui.GetWindowDrawList().AddRectFilled(start, start + new NVector2(fieldWidth, height), ImGui.GetColorU32(ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
						ImGui.GetWindowDrawList().AddText(start + new NVector2(5f, (height - ImGui.GetTextLineHeight()) * 0.5f), ImGui.GetColorU32(ImGuiCol.TextDisabled), "None (" + ObjectField.TypeLabel(declaredType) + ")");
						if (ImGui.IsItemHovered())
						{
							ImGui.SetTooltip(hint);
						}

						if (ImGui.IsItemClicked() && creatable.Count > 0)
						{
							ImGui.OpenPopup(popupId);
						}
					}

					if (creatable.Count > 0)
					{
						ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
						if (EditorIcons.Button("create", IconKind.Dropdown, hint, arrow))
						{
							ImGui.OpenPopup(popupId);
						}
					}

					if (TypeSelector.Popup(popupId, declaredType, null, current != null, out Type chosen))
					{
						context.BeginEdit();
						current = chosen != null ? CreateInstance(chosen, null) : null;
						context.EndEdit();
						changed = true;
					}
				}

				if (Inlines(value, info, context, depth))
				{
					object target = value;
					if (Header(label, info, context, DrawLine, null, InlineEditors.Hint(target)))
					{
						if (!changed)
						{
							InlineEditors.Draw(target, context, depth + 1);
						}

						ImGui.TreePop();
					}
				}
				else
				{
					RowDecoration row = BeginLine(label, info, context);
					DrawLine(ImGui.CalcItemWidth());
					EndLine(row);
				}

				if (changed)
				{
					value = current;
				}

				return changed;
			}

			// An instance held by value: a foldout with its class selector next to the label. A reference dropped on the
			// header replaces the instance.
			Type valueType = value.GetType();
			object instance = value;
			bool typeChosen = false;
			Type chosenType = null;
			object dropped = null;
			bool open = Header(label, info, context, width =>
			{
				if (TypeButton(TypeSelector.DisplayName(valueType), width))
				{
					ImGui.OpenPopup(popupId);
				}

				if (ImGui.IsItemHovered() && !DragAndDrop.IsDragging)
				{
					ImGui.SetTooltip(valueType.FullName + "\n" + hint);
				}

				if (canReference && DragAndDrop.AcceptValue(declaredType, out object droppedOnType))
				{
					dropped = droppedOnType;
				}

				typeChosen = TypeSelector.Popup(popupId, declaredType, valueType, true, out chosenType);
			}, () =>
			{
				if (canReference && DragAndDrop.AcceptValue(declaredType, out object droppedOnHeader))
				{
					dropped = droppedOnHeader;
				}
			});

			bool result = false;
			if (dropped != null)
			{
				context.BeginEdit();
				value = dropped;
				context.EndEdit();
				result = true;
			}
			else if (typeChosen && chosenType != valueType)
			{
				context.BeginEdit();
				value = chosenType != null ? CreateInstance(chosenType, instance) : null;
				context.EndEdit();
				result = true;
			}

			if (open)
			{
				if (!result)
				{
					result = DrawFields(instance, context, depth + 1);
				}

				ImGui.TreePop();
			}

			return result;
		}

		private static string PolymorphicHint(Type declaredType, bool canReference, int creatable)
		{
			string name = ObjectField.TypeLabel(declaredType);
			string text = "";
			if (canReference)
			{
				text = "Drop a GameObject (its component implementing " + name + "), a component or a ScriptableObject asset here, or pick one with the round button.";
			}

			if (creatable > 0)
			{
				text += (text.Length > 0 ? "\n" : "") + "The arrow creates an instance of a class " + (declaredType.IsInterface ? "implementing " : "deriving from ") + name + ", stored in this field.";
			}

			return text.Length > 0 ? text : "No class " + (declaredType.IsInterface ? "implements " : "derives from ") + name + " yet.";
		}

		/// <summary>A combo-like button showing a type name, the arrow at its right; true when clicked.</summary>
		private static bool TypeButton(string text, float width)
		{
			ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new NVector2(0f, 0.5f));
			ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered]);
			bool pressed = ImGui.Button(text + "##type", new NVector2(width, 0f));
			ImGui.PopStyleColor(2);
			ImGui.PopStyleVar();
			NVector2 max = ImGui.GetItemRectMax();
			float size = ImGui.GetFrameHeight() * 0.5f;
			EditorIcons.Draw(ImGui.GetWindowDrawList(), new NVector2(max.X - size * 1.5f, max.Y - ImGui.GetFrameHeight() * 0.5f - size * 0.5f), size, IconKind.Dropdown, ImGui.GetColorU32(ImGuiCol.Text));
			return pressed;
		}

		/// <summary>A [Serializable] class or struct: a foldout with its fields (a null class value can be created).</summary>
		private static bool DrawNested(string label, Type type, ref object value, FieldInfo info, InspectorContext context, int depth)
		{
			bool create = false;
			Action<float> valueArea = null;
			if (value == null && !type.IsValueType)
			{
				valueArea = width =>
				{
					ImGui.AlignTextToFramePadding();
					ImGui.TextDisabled("None");
					ImGui.SameLine();
					if (ImGui.SmallButton("Create##create"))
					{
						create = true;
					}
				};
			}

			bool open = Header(label, info, context, valueArea);
			bool changed = false;
			if (create)
			{
				context.BeginEdit();
				value = CreateInstance(type, null);
				context.EndEdit();
				changed = true;
			}

			if (open)
			{
				if (value != null && !create)
				{
					changed |= DrawFields(value, context, depth + 1);
				}

				ImGui.TreePop();
			}

			return changed;
		}

		private static bool DrawLeaf(Type type, ref object value, FieldInfo info, InspectorContext context)
		{
			RangeAttribute range = info?.GetCustomAttribute<RangeAttribute>(false);
			bool changed = false;

			if (type == typeof(bool))
			{
				bool b = value is bool flag && flag;
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

		/// <summary>A layer mask as a dropdown of the named layers to tick, with Nothing / Everything (as Unity draws it).</summary>
		private static bool DrawLayerMask(ref object value, InspectorContext context)
		{
			int mask = ((LayerMask)value).value;
			bool changed = false;
			if (ImGui.BeginCombo("##v", LayerMaskPreview(mask)))
			{
				if (ImGui.Selectable("Nothing", mask == 0))
				{
					mask = 0;
					changed = true;
				}

				if (ImGui.Selectable("Everything", mask == -1))
				{
					mask = -1;
					changed = true;
				}

				ImGui.Separator();
				for (int layer = 0; layer < LayerMask.LayerCount; layer++)
				{
					string name = LayerMask.LayerToName(layer);
					if (name.Length == 0)
					{
						continue;
					}

					// Checkboxes keep the list open while several layers are ticked.
					bool on = (mask & (1 << layer)) != 0;
					if (ImGui.Checkbox(name + "##" + layer, ref on))
					{
						mask = on ? mask | (1 << layer) : mask & ~(1 << layer);
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			context.Track(changed);
			if (changed)
			{
				value = (LayerMask)mask;
			}

			return changed;
		}

		private static bool DrawIntVector(Type type, ref object value)
		{
			bool changed;
			if (type == typeof(Vector2i))
			{
				Vector2i v = (Vector2i)value;
				int[] values = { v.X, v.Y };
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

		#endregion
	}
}
