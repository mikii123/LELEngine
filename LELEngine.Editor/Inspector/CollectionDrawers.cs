using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Odin-style collection inspectors.
	///     <para>
	///         Lists and arrays: a foldout header with the element count (editable) and an add button; objects
	///         dropped on the header are appended (every dragged object that fits). Each row has a drag handle to
	///         reorder (drop above or below another row), its index, the element's own widget (any field kind, nested
	///         objects and collections unfold in place) and a remove button; the handle's menu duplicates, inserts and
	///         removes. Long lists show pages.
	///     </para>
	///     <para>
	///         Dictionaries: a foldout header with the entry count and an add button that opens a row for the new key
	///         (it must be unique); entries in a two-column table, key and value, each editable (a key changes only to
	///         one not in use), with a remove button.
	///     </para>
	///     Every change is one undo step of the edited object.
	/// </summary>
	internal static class CollectionDrawers
	{
		#region PrivateFields

		private const string RowPayload = "LEL_LIST_ROW";
		private const int PageSize = 50;

		// The row being dragged to reorder (the list instance and its index).
		private static IList draggedList;
		private static int draggedIndex = -1;

		// A count typed into a list header, applied when the field is left (typing "12" must not resize to 1 first).
		private static uint countEditId;
		private static int countEditValue;

		// The page shown per list header (by ImGui id).
		private static readonly Dictionary<uint, int> pages = new Dictionary<uint, int>();

		// The dictionary whose "new key" row is open, and the key being typed.
		private static IDictionary pendingOwner;
		private static object pendingKey;

		#endregion

		#region PublicMethods

		/// <summary>Drops every reference to edited collections (before the scene or the scripts unload).</summary>
		public static void Reset()
		{
			draggedList = null;
			draggedIndex = -1;
			pendingOwner = null;
			pendingKey = null;
			pages.Clear();
		}

		// ---------------------------------------------------------------- lists

		public static bool DrawList(string label, Type type, ref object value, FieldInfo info, InspectorContext context, int depth)
		{
			Type element = SerializationUtility.GetElementType(type);
			IList list = value as IList;
			int count = list?.Count ?? 0;
			bool changed = false;
			IList created = null;
			List<object> dropped = null;
			int resizeTo = -1;
			bool add = false;
			uint headerId = ImGui.GetID("##list_header");

			bool acceptsObjects = AcceptsObjects(element);
			bool open = FieldDrawer.Header(label, info, context, width =>
			{
				// The count (applied when left) and the add button, at the right end of the line.
				float button = ImGui.GetFrameHeight();
				float countWidth = Math.Min(64f, Math.Max(36f, width - button - ImGui.GetStyle().ItemInnerSpacing.X));
				float x = ImGui.GetCursorScreenPos().X + width - countWidth - button - ImGui.GetStyle().ItemInnerSpacing.X;
				ImGui.SetCursorScreenPos(new NVector2(x, ImGui.GetCursorScreenPos().Y));
				uint id = ImGui.GetID("##count");
				int shown = countEditId == id ? countEditValue : count;
				ImGui.SetNextItemWidth(countWidth);
				if (ImGui.InputInt("##count", ref shown, 0, 0))
				{
					countEditId = id;
					countEditValue = Math.Clamp(shown, 0, 100000);
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Number of elements");
				}

				if (ImGui.IsItemDeactivated() && countEditId == id)
				{
					resizeTo = countEditValue;
					countEditId = 0;
				}

				ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
				if (EditorIcons.Button("add", IconKind.Plus, "Add an element", button))
				{
					add = true;
				}
			}, () =>
			{
				if (acceptsObjects && DragAndDrop.AcceptValues(element, out List<object> values))
				{
					dropped = values;
				}
			}, (info?.GetCustomAttribute<TooltipAttribute>(false)?.Text != null ? info.GetCustomAttribute<TooltipAttribute>(false).Text + "\n" : "")
				+ ObjectField.TypeLabel(type.IsArray ? element : type) + (type.IsArray ? "[]" : "<" + ObjectField.TypeLabel(element) + ">") + (acceptsObjects ? "; drop objects here to add them" : ""));
			uint foldout = FieldDrawer.LastHeaderId;

			// Header actions: one undo step each.
			if (add || resizeTo >= 0 || dropped != null)
			{
				context.BeginEdit();
				list = list ?? (created = CreateList(type, element, 0));
				if (add)
				{
					list = Insert(list, type, element, list.Count, FieldDrawer.DefaultValue(element, list.Count > 0 ? list[list.Count - 1] : null));
				}

				if (resizeTo >= 0 && resizeTo != list.Count)
				{
					list = Resize(list, type, element, resizeTo);
				}

				if (dropped != null)
				{
					foreach (object item in dropped)
					{
						list = Insert(list, type, element, list.Count, item);
					}
				}

				context.EndEdit();
				changed = true;
				FieldDrawer.OpenHeader(foldout);
			}

			if (open)
			{
				if (list == null)
				{
					ImGui.TextDisabled("None");
				}
				else
				{
					changed |= DrawRows(ref list, type, element, context, depth, headerId, label);
				}

				ImGui.TreePop();
			}

			if (changed || created != null)
			{
				value = list;
			}

			return changed;
		}

		// ---------------------------------------------------------------- dictionaries

		public static bool DrawDictionary(string label, Type type, ref object value, FieldInfo info, InspectorContext context, int depth)
		{
			SerializationUtility.GetDictionaryTypes(type, out Type keyType, out Type valueType);
			var dictionary = value as IDictionary;
			bool changed = false;
			bool add = false;
			int count = dictionary?.Count ?? 0;
			bool open = FieldDrawer.Header(label, info, context, width =>
			{
				float button = ImGui.GetFrameHeight();
				string text = count == 1 ? "1 entry" : count + " entries";
				float textWidth = ImGui.CalcTextSize(text).X;
				float x = ImGui.GetCursorScreenPos().X + width - button - textWidth - ImGui.GetStyle().ItemSpacing.X;
				ImGui.SetCursorScreenPos(new NVector2(x, ImGui.GetCursorScreenPos().Y));
				ImGui.AlignTextToFramePadding();
				ImGui.TextDisabled(text);
				ImGui.SameLine();
				if (EditorIcons.Button("add", IconKind.Plus, "Add an entry", button))
				{
					add = true;
				}
			}, null, "Dictionary<" + ObjectField.TypeLabel(keyType) + ", " + ObjectField.TypeLabel(valueType) + ">");
			uint foldout = FieldDrawer.LastHeaderId;

			if (add)
			{
				if (dictionary == null)
				{
					context.BeginEdit();
					dictionary = (IDictionary)Activator.CreateInstance(type);
					value = dictionary;
					context.EndEdit();
					changed = true;
				}

				pendingOwner = dictionary;
				pendingKey = SuggestKey(dictionary, keyType);
				FieldDrawer.OpenHeader(foldout);
			}

			if (open)
			{
				if (dictionary == null)
				{
					ImGui.TextDisabled("None");
				}
				else
				{
					changed |= DrawEntries(dictionary, keyType, valueType, context, depth);
				}

				ImGui.TreePop();
			}

			return changed;
		}

		#endregion

		#region PrivateMethods

		/// <summary>Elements objects can be dropped as: references (GameObjects, components, assets) and interface slots.</summary>
		private static bool AcceptsObjects(Type element)
		{
			return SerializationUtility.IsReferenceType(element) || SerializationUtility.IsAssetType(element) || SerializationUtility.CanHoldReference(element);
		}

		private static bool DrawRows(ref IList list, Type type, Type element, InspectorContext context, int depth, uint headerId, string label)
		{
			bool changed = false;
			int count = list.Count;
			int first = 0;
			int last = count;
			if (count > PageSize)
			{
				int pageCount = (count + PageSize - 1) / PageSize;
				pages.TryGetValue(headerId, out int page);
				page = Math.Clamp(page, 0, pageCount - 1);
				if (ImGui.SmallButton("<##page") && page > 0)
				{
					page--;
				}

				ImGui.SameLine();
				ImGui.TextDisabled($"{page * PageSize}..{Math.Min(count, (page + 1) * PageSize) - 1} of {count}");
				ImGui.SameLine();
				if (ImGui.SmallButton(">##page") && page < pageCount - 1)
				{
					page++;
				}

				pages[headerId] = page;
				first = page * PageSize;
				last = Math.Min(count, first + PageSize);
			}

			if (count == 0)
			{
				ImGui.TextDisabled("Empty" + (AcceptsObjects(element) ? ": drop objects on the header to add them" : ""));
				return false;
			}

			int removeAt = -1;
			int duplicateAt = -1;
			int insertAt = -1;
			int moveFrom = -1;
			int moveTo = -1;
			string anchorPrefix = TestAnchors.Enabled ? "row:" + label + "/" : null;
			float indexWidth = ImGui.CalcTextSize((last - 1).ToString()).X + ImGui.GetStyle().ItemSpacing.X;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			IList current = list;
			for (int i = first; i < last; i++)
			{
				int index = i;
				ImGui.PushID(i);
				NVector2 rowStart = ImGui.GetCursorScreenPos();
				float rowRight = rowStart.X + ImGui.GetContentRegionAvail().X;
				context.PendingRow = new RowDecoration
				{
					Prefix = () =>
					{
						DrawGrip(current, index);
						if (anchorPrefix != null)
						{
							TestAnchors.RecordItem(anchorPrefix + index);
						}

						if (ImGui.BeginPopupContextItem("##row_menu"))
						{
							if (ImGui.MenuItem("Duplicate")) duplicateAt = index;
							if (ImGui.MenuItem("Insert Above")) insertAt = index;
							if (ImGui.MenuItem("Remove")) removeAt = index;
							ImGui.EndPopup();
						}
					},
					Label = index.ToString(),
					LabelWidth = indexWidth,
					Suffix = () =>
					{
						if (EditorIcons.Button("remove", IconKind.Cross, "Remove element " + index))
						{
							removeAt = index;
						}
					},
					SuffixWidth = ImGui.GetFrameHeight()
				};

				object item = list[i];
				if (FieldDrawer.DrawValue("Element " + i, element, ref item, null, context, depth + 1))
				{
					list[i] = item;
					changed = true;
				}

				context.PendingRow = null;
				NVector2 rowEnd = new NVector2(rowRight, ImGui.GetCursorScreenPos().Y);

				// Reordering: rows take the dragged row of the same list; the line shows where it goes.
				if (anchorPrefix != null)
				{
					TestAnchors.Record(anchorPrefix + index + "/row", rowStart, rowEnd);
				}

				if (draggedList == list && ImGuiP.BeginDragDropTargetCustom(new ImRect(rowStart, rowEnd), ImGui.GetID("##row_target")))
				{
					ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(RowPayload, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
					if (!payload.IsNull)
					{
						bool below = ImGui.GetMousePos().Y > (rowStart.Y + rowEnd.Y) * 0.5f;
						float y = below ? rowEnd.Y - 1f : rowStart.Y - 1f;
						draw.AddLine(new NVector2(rowStart.X, y), new NVector2(rowRight, y), ImGui.GetColorU32(EditorIcons.Accept), 2f);
						if (payload.IsDelivery())
						{
							moveFrom = draggedIndex;
							moveTo = below ? index + 1 : index;
						}
					}

					ImGui.EndDragDropTarget();
				}

				// A thin separator between rows.
				if (i < last - 1)
				{
					draw.AddLine(new NVector2(rowStart.X + 2f, rowEnd.Y - 2f), new NVector2(rowRight, rowEnd.Y - 2f), ImGui.GetColorU32(ImGuiCol.Separator, 0.35f));
				}

				ImGui.PopID();
			}

			if (removeAt >= 0 || duplicateAt >= 0 || insertAt >= 0 || (moveFrom >= 0 && moveTo >= 0 && moveFrom != moveTo && moveFrom != moveTo - 1))
			{
				context.BeginEdit();
				if (removeAt >= 0)
				{
					list = RemoveAt(list, type, element, removeAt);
				}
				else if (duplicateAt >= 0)
				{
					list = Insert(list, type, element, duplicateAt + 1, Duplicate(list[duplicateAt], context));
				}
				else if (insertAt >= 0)
				{
					list = Insert(list, type, element, insertAt, FieldDrawer.DefaultValue(element, null));
				}
				else
				{
					Move(list, moveFrom, moveTo);
				}

				context.EndEdit();
				changed = true;
			}

			return changed;
		}

		/// <summary>The drag handle of a row: three lines; dragging it carries the row to another place in the same list.</summary>
		private static void DrawGrip(IList list, int index)
		{
			float height = ImGui.GetFrameHeight();
			float width = height * 0.6f;
			NVector2 start = ImGui.GetCursorScreenPos();
			ImGui.InvisibleButton("##grip", new NVector2(width, height));
			bool hovered = ImGui.IsItemHovered();
			float icon = Math.Min(width, height) * 0.9f;
			EditorIcons.Draw(ImGui.GetWindowDrawList(), start + new NVector2((width - icon) * 0.5f, (height - icon) * 0.5f), icon, IconKind.Grip, ImGui.GetColorU32(hovered ? ImGuiCol.Text : ImGuiCol.TextDisabled));
			if (hovered && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				ImGui.SetTooltip("Drag to reorder; right click for more");
			}

			if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceNoDisableHover))
			{
				draggedList = list;
				draggedIndex = index;
				unsafe
				{
					ImGui.SetDragDropPayload(RowPayload, null, 0);
				}

				ImGui.TextUnformatted("Element " + index);
				ImGui.EndDragDropSource();
			}
		}

		private static IList CreateList(Type type, Type element, int count)
		{
			return type.IsArray ? Array.CreateInstance(element, count) : (IList)Activator.CreateInstance(type);
		}

		/// <summary>The list with <paramref name="item" /> inserted (a new array for arrays).</summary>
		private static IList Insert(IList list, Type type, Type element, int index, object item)
		{
			index = Math.Clamp(index, 0, list.Count);
			if (!type.IsArray)
			{
				list.Insert(index, item);
				return list;
			}

			Array array = Array.CreateInstance(element, list.Count + 1);
			for (int i = 0, j = 0; i < array.Length; i++)
			{
				array.SetValue(i == index ? item : list[j++], i);
			}

			return array;
		}

		private static IList RemoveAt(IList list, Type type, Type element, int index)
		{
			if (!type.IsArray)
			{
				list.RemoveAt(index);
				return list;
			}

			Array array = Array.CreateInstance(element, list.Count - 1);
			for (int i = 0, j = 0; i < list.Count; i++)
			{
				if (i != index)
				{
					array.SetValue(list[i], j++);
				}
			}

			return array;
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
					array.SetValue(FieldDrawer.DefaultValue(element, i > 0 && list.Count > 0 ? list[list.Count - 1] : null), i);
				}

				return array;
			}

			while (list.Count > count)
			{
				list.RemoveAt(list.Count - 1);
			}

			while (list.Count < count)
			{
				list.Add(FieldDrawer.DefaultValue(element, list.Count > 0 ? list[list.Count - 1] : null));
			}

			return list;
		}

		/// <summary>Moves an element to before position <paramref name="to" /> (in place; arrays keep their length).</summary>
		private static void Move(IList list, int from, int to)
		{
			if (from < 0 || from >= list.Count)
			{
				return;
			}

			var items = new List<object>(list.Count);
			foreach (object item in list)
			{
				items.Add(item);
			}

			object moved = items[from];
			items.RemoveAt(from);
			items.Insert(to > from ? to - 1 : to, moved);
			for (int i = 0; i < items.Count; i++)
			{
				list[i] = items[i];
			}
		}

		/// <summary>A copy of an element: value types and references as they are, objects held by value deep-copied.</summary>
		private static object Duplicate(object item, InspectorContext context)
		{
			if (item == null || item is string || item.GetType().IsValueType || item is EngineObject || item is IAsset)
			{
				return item;
			}

			try
			{
				var writer = new BinaryDocumentWriter(new SerializationContext { Scene = context.Scene });
				writer.WriteObjectValue(item, 0);
				var reader = new BinaryDocumentReader(writer.ToArray(), EditorSerialization.ContextFor(context.Scene));
				return reader.ReadObjectValue(item.GetType(), null, 0) ?? item;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot copy the element: " + e.Message);
				return item;
			}
		}

		// ---------------------------------------------------------------- dictionary entries

		private static bool DrawEntries(IDictionary dictionary, Type keyType, Type valueType, InspectorContext context, int depth)
		{
			bool changed = false;
			if (pendingOwner == dictionary)
			{
				changed |= DrawNewKeyRow(dictionary, keyType, valueType, context, depth);
			}

			if (dictionary.Count == 0)
			{
				if (pendingOwner != dictionary)
				{
					ImGui.TextDisabled("Empty: the + button adds an entry");
				}

				return changed;
			}

			// A snapshot: the dictionary may change while its rows are drawn.
			var keys = new List<object>(dictionary.Count);
			foreach (object key in dictionary.Keys)
			{
				keys.Add(key);
			}

			object removeKey = null;
			object renameFrom = null;
			object renameTo = null;
			float savedLabelWidth = context.LabelWidth;
			if (ImGui.BeginTable("##entries", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
			{
				ImGui.TableSetupColumn("Key", ImGuiTableColumnFlags.WidthStretch, 0.38f);
				ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch, 0.62f);
				ImGui.TableHeadersRow();
				for (int i = 0; i < keys.Count; i++)
				{
					object key = keys[i];
					ImGui.TableNextRow();
					ImGui.PushID(i);

					// Key: changed in place when the new key is free (an entry keeps its place in the dictionary).
					ImGui.TableSetColumnIndex(0);
					context.LabelWidth = Math.Max(40f, ImGui.GetContentRegionAvail().X * 0.4f);
					object editedKey = key;
					context.PendingRow = new RowDecoration { Label = "" };
					ImGui.PushID("key");
					bool keyChanged = FieldDrawer.DrawValue("Key", keyType, ref editedKey, null, context, depth + 1);
					ImGui.PopID();
					context.PendingRow = null;
					if (keyChanged)
					{
						if (DocumentValues.IsUsableKey(editedKey) && !dictionary.Contains(editedKey))
						{
							renameFrom = key;
							renameTo = editedKey;
						}
						else if (!Equals(editedKey, key))
						{
							ImGui.SetTooltip(editedKey == null ? "A key cannot be empty" : "The key " + editedKey + " is in use already");
						}
					}

					// Value, with the remove button at the end of its first line.
					ImGui.TableSetColumnIndex(1);
					context.LabelWidth = Math.Max(60f, ImGui.GetContentRegionAvail().X * 0.4f);
					object item = dictionary[key];
					object entryKey = key;
					context.PendingRow = new RowDecoration
					{
						Label = "",
						HeaderLabel = ValueLabel(item, valueType),
						Suffix = () =>
						{
							if (EditorIcons.Button("remove", IconKind.Cross, "Remove this entry"))
							{
								removeKey = entryKey;
							}
						},
						SuffixWidth = ImGui.GetFrameHeight()
					};
					ImGui.PushID("value");
					if (FieldDrawer.DrawValue(ValueLabel(item, valueType), valueType, ref item, null, context, depth + 1))
					{
						// The widget captured the state before the change (Track): write it in place.
						dictionary[key] = item;
						changed = true;
					}

					ImGui.PopID();
					context.PendingRow = null;
					ImGui.PopID();
				}

				ImGui.EndTable();
			}

			context.LabelWidth = savedLabelWidth;
			if (removeKey != null || renameFrom != null)
			{
				context.BeginEdit();
				if (removeKey != null)
				{
					dictionary.Remove(removeKey);
				}
				else
				{
					object item = dictionary[renameFrom];
					dictionary.Remove(renameFrom);
					dictionary[renameTo] = item;
				}

				context.EndEdit();
				changed = true;
			}

			return changed;
		}

		/// <summary>The row of a new entry: its key (unique, not empty), Add and Cancel.</summary>
		private static bool DrawNewKeyRow(IDictionary dictionary, Type keyType, Type valueType, InspectorContext context, int depth)
		{
			bool changed = false;
			ImGui.PushID("##new_entry");
			object key = pendingKey;
			float button = ImGui.CalcTextSize("Cancel").X + ImGui.GetStyle().FramePadding.X * 2f;
			context.PendingRow = new RowDecoration
			{
				Label = "New key",
				Suffix = () => { },
				SuffixWidth = button * 2f + ImGui.GetStyle().ItemSpacing.X
			};
			FieldDrawer.DrawValue("New key", keyType, ref key, null, context, depth + 1);
			context.PendingRow = null;
			pendingKey = key;
			bool free = DocumentValues.IsUsableKey(key) && !dictionary.Contains(key);
			ImGui.SameLine();
			ImGui.BeginDisabled(!free);
			bool confirm = ImGui.Button("Add##add_entry", new NVector2(button, 0f)) || (free && ImGui.IsKeyPressed(ImGuiKey.Enter, false) && ImGui.IsWindowFocused());
			TestAnchors.RecordItem("button:AddEntry");
			ImGui.EndDisabled();
			if (!free && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(key == null || (key is string text && text.Length == 0 && keyType != typeof(string)) ? "Set the key first" : "This key is in use already");
			}

			ImGui.SameLine();
			if (ImGui.Button("Cancel##cancel_entry", new NVector2(button, 0f)))
			{
				pendingOwner = null;
				pendingKey = null;
			}

			if (confirm && free)
			{
				context.BeginEdit();
				dictionary[key] = FieldDrawer.DefaultValue(valueType, null);
				context.EndEdit();
				changed = true;
				pendingOwner = null;
				pendingKey = null;
			}

			ImGui.PopID();
			return changed;
		}

		/// <summary>A first key to offer: "" (or "Key 2", ...) for strings, the next free number, the first free enum member; null for references.</summary>
		private static object SuggestKey(IDictionary dictionary, Type keyType)
		{
			if (keyType == typeof(string))
			{
				string key = "Key";
				for (int n = 2; dictionary.Contains(key); n++)
				{
					key = "Key " + n;
				}

				return key;
			}

			if (keyType.IsEnum)
			{
				foreach (object member in Enum.GetValues(keyType))
				{
					if (!dictionary.Contains(member))
					{
						return member;
					}
				}

				return Enum.GetValues(keyType).GetValue(0);
			}

			if (keyType.IsPrimitive && keyType != typeof(bool) && keyType != typeof(char))
			{
				long next = 0;
				foreach (object existing in dictionary.Keys)
				{
					next = Math.Max(next, Convert.ToInt64(existing) + 1);
				}

				return Convert.ChangeType(next, keyType);
			}

			return keyType.IsValueType ? Activator.CreateInstance(keyType) : null;
		}

		/// <summary>
		///     The header text of a value that unfolds: a nested object's class name; nothing for collections (their
		///     header shows the count) and polymorphic values (their class selector shows the class).
		/// </summary>
		private static string ValueLabel(object value, Type valueType)
		{
			if (value == null || value is EngineObject || value.GetType().IsPrimitive || SerializationUtility.GetElementType(valueType) != null
				|| SerializationUtility.IsDictionaryType(valueType) || TypeSelector.IsPolymorphic(valueType))
			{
				return "";
			}

			return TypeSelector.DisplayName(value.GetType());
		}

		#endregion
	}
}
