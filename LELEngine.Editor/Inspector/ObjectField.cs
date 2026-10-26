using System;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Unity-style object field for every reference: GameObjects and components of the scene, assets, ScriptableObjects
	///     and references held by interface or EngineObject fields. It shows the referenced object (icon or thumbnail,
	///     name, type) and takes objects dragged onto it from the hierarchy, a component header, the project panel or
	///     another object field; what does not fit the field's type is refused (red outline). A click pings the object
	///     (the hierarchy or the project panel shows it), a double click selects it, dragging the field drags its object,
	///     the context menu clears it, and the button opens the picker (scene objects and assets of the type).
	/// </summary>
	internal static class ObjectField
	{
		#region PublicMethods

		/// <summary>
		///     Draws the field in the current line (the next item width, or the rest of the line); true when the value
		///     changed. Without a context the editor's open scene is the one offered.
		/// </summary>
		public static bool Draw(Type declaredType, ref object value, InspectorContext context)
		{
			EditorApplication editor = context?.Editor ?? EditorApplication.Instance;
			Scene scene = context?.Scene ?? editor?.Scene;
			bool changed = false;
			uint key = ImGui.GetID("##object_field");
			float height = ImGui.GetFrameHeight();
			float buttonWidth = height;
			float width = ImGui.CalcItemWidth();
			if (width <= 0f)
			{
				width = ImGui.GetContentRegionAvail().X;
			}

			float boxWidth = Math.Max(24f, width - buttonWidth - ImGui.GetStyle().ItemInnerSpacing.X);
			NVector2 start = ImGui.GetCursorScreenPos();
			EditorGui.InvisibleButton("##field", new NVector2(boxWidth, height));
			bool hovered = ImGui.IsItemHovered();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(start, start + new NVector2(boxWidth, height), ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);

			bool missing = value is EngineObject engineObject && engineObject.IsDestroyed;
			DrawContent(draw, start, boxWidth, height, declaredType, value, missing);

			if (hovered && !missing)
			{
				string tooltip = Describe(value);
				if (tooltip != null && !DragAndDrop.IsDragging)
				{
					ImGui.SetTooltip(tooltip + "\nClick: show it, double click: select it");
				}
			}

			// Click pings, double click selects.
			if (value != null && !missing && editor != null)
			{
				if (EditorGui.IsItemDoubleClicked())
				{
					editor.SelectObject(value);
				}
				else if (EditorGui.IsItemClicked(ImGuiMouseButton.Left))
				{
					editor.Ping(value);
				}
			}

			// The field's object can be dragged on to another field.
			if (value != null && !missing && ImGui.BeginDragDropSource())
			{
				DragAndDrop.SetPayload(DragAndDrop.ReferencePayload, DragItem.FromObject(value));
				ImGui.EndDragDropSource();
			}

			object dropped;
			if (DragAndDrop.AcceptValue(declaredType, out dropped))
			{
				value = dropped;
				changed = true;
			}

			if (EditorGui.BeginPopupContextItem("##field_context"))
			{
				if (EditorMenu.Item("Clear", (string)null, false, value != null))
				{
					value = null;
					changed = true;
				}

				if (EditorMenu.Item("Show", (string)null, false, value != null && !missing && editor != null))
				{
					editor.Ping(value);
				}

				if (EditorMenu.Item("Select", (string)null, false, value != null && !missing && editor != null))
				{
					editor.SelectObject(value);
				}

				ImGui.EndPopup();
			}

			ImGui.SameLine(0f, ImGui.GetStyle().ItemInnerSpacing.X);
			if (EditorIcons.Button("pick", IconKind.Picker, "Select " + TypeLabel(declaredType), buttonWidth))
			{
				ObjectPicker.Open(key, declaredType, value, scene);
			}

			object picked;
			if (ObjectPicker.TakeResult(key, out picked))
			{
				value = picked;
				changed = true;
			}

			return changed;
		}

		/// <summary>Type name as fields show it ("Shader" for shader programs, interfaces and classes by their name).</summary>
		public static string TypeLabel(Type type)
		{
			if (type == null)
			{
				return "Object";
			}

			if (type == typeof(Shaders.ShaderProgram))
			{
				return "Shader";
			}

			return type.IsGenericType ? type.Name.Substring(0, Math.Max(1, type.Name.IndexOf('`'))) : type.Name;
		}

		/// <summary>Short text of a referenced object: "Player", "Player (Rigidbody2D)", "Grass.png", "Sword (WeaponConfig)".</summary>
		public static string NameOf(object value)
		{
			switch (value)
			{
				case null:
					return "None";
				case GameObject go:
					return go.Name;
				case Behaviour component:
					return (component.gameObject?.Name ?? "?") + " (" + component.GetType().Name + ")";
				case ScriptableObject scriptableObject:
					return scriptableObject.name + " (" + scriptableObject.GetType().Name + ")";
				case IAsset asset:
					return asset.AssetPath != null ? Path.GetFileName(asset.AssetPath) + (asset.SubAssetName != null ? " / " + asset.SubAssetName : "") : (asset.SubAssetName ?? value.GetType().Name) + " (runtime)";
				default:
					return value.ToString();
			}
		}

		#endregion

		#region PrivateMethods

		private static void DrawContent(ImDrawListPtr draw, NVector2 start, float width, float height, Type declaredType, object value, bool missing)
		{
			float textX = start.X + 5f;
			float icon = height - 6f;
			int handle;
			NVector2 uv0;
			NVector2 uv1;
			if (value is Texture texture && AssetThumbnails.TryGet(texture, out handle, out uv0, out uv1))
			{
				draw.AddImage(EditorGui.Texture(handle), start + new NVector2(2f, 2f), start + new NVector2(height - 2f, height - 2f), uv0, uv1);
				textX = start.X + height + 2f;
			}
			else if (value is Sprite sprite && AssetThumbnails.TryGet(sprite.texture, out handle, out uv0, out uv1))
			{
				OpenTK.Mathematics.Vector4 uv = sprite.uv;
				draw.AddImage(EditorGui.Texture(handle), start + new NVector2(2f, 2f), start + new NVector2(height - 2f, height - 2f), new NVector2(uv.X, uv.W), new NVector2(uv.Z, uv.Y));
				textX = start.X + height + 2f;
			}
			else if (value != null && !missing)
			{
				IconKind kind = EditorIcons.KindOf(value);
				EditorIcons.Draw(draw, start + new NVector2(4f, 3f), icon, kind, EditorIcons.ReferenceColor(kind));
				textX = start.X + icon + 9f;
			}

			string text;
			uint color;
			if (missing)
			{
				text = "Missing (" + TypeLabel(declaredType) + ")";
				color = ImGui.GetColorU32(EditorStyle.Error);
			}
			else if (value == null)
			{
				text = "None (" + TypeLabel(declaredType) + ")";
				color = ImGui.GetColorU32(ImGuiCol.TextDisabled);
			}
			else
			{
				text = NameOf(value);
				color = ImGui.GetColorU32(ImGuiCol.Text);
			}

			draw.PushClipRect(start, start + new NVector2(width - 3f, height), true);
			draw.AddText(new NVector2(textX, start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f), color, text);
			draw.PopClipRect();
		}

		/// <summary>Tooltip of a referenced object: its hierarchy path or asset path.</summary>
		private static string Describe(object value)
		{
			switch (value)
			{
				case GameObject go:
					return HierarchyPath(go);
				case Behaviour component:
					return component.GetType().Name + " on " + HierarchyPath(component.gameObject);
				case IAsset asset:
					return asset.AssetPath != null ? asset.AssetPath + (asset.SubAssetName != null ? " / " + asset.SubAssetName : "") + "  (" + value.GetType().Name + ")" : value.GetType().Name + " made at runtime";
				default:
					return null;
			}
		}

		public static string HierarchyPath(GameObject go)
		{
			if (go == null)
			{
				return "?";
			}

			string path = go.Name;
			for (Transform parent = go.transform?.parent; parent != null; parent = parent.parent)
			{
				path = parent.gameObject.Name + "/" + path;
			}

			return path;
		}

		#endregion
	}
}
