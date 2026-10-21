using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>One dragged object: a scene object, a component, a project file (a sub-asset of it) or an asset object.</summary>
	internal readonly struct DragItem
	{
		#region PublicFields

		public readonly GameObject GameObject;
		public readonly Behaviour Component;
		public readonly string AssetPath;
		public readonly string SubAsset;
		public readonly object Asset;

		#endregion

		#region Constructors

		private DragItem(GameObject gameObject, Behaviour component, string assetPath, string subAsset, object asset)
		{
			GameObject = gameObject;
			Component = component;
			AssetPath = assetPath;
			SubAsset = subAsset;
			Asset = asset;
		}

		#endregion

		#region PublicMethods

		public static DragItem FromGameObject(GameObject go)
		{
			return new DragItem(go, null, null, null, null);
		}

		public static DragItem FromComponent(Behaviour component)
		{
			return new DragItem(component.gameObject, component, null, null, null);
		}

		public static DragItem FromFile(string assetPath, string subAsset = null)
		{
			return new DragItem(null, null, assetPath, subAsset, null);
		}

		/// <summary>A referenced object (an object field's value): scene objects and components as such, assets with their file.</summary>
		public static DragItem FromObject(object value)
		{
			switch (value)
			{
				case GameObject go:
					return FromGameObject(go);
				case Behaviour component:
					return FromComponent(component);
				case IAsset asset:
					return new DragItem(null, null, asset.AssetPath, asset.SubAssetName, value);
				default:
					return default;
			}
		}

		public bool IsEmpty => GameObject == null && Component == null && AssetPath == null && Asset == null;

		public string Label
		{
			get
			{
				if (Component != null)
				{
					return Component.gameObject?.Name + " (" + Component.GetType().Name + ")";
				}

				if (GameObject != null)
				{
					return GameObject.Name;
				}

				if (AssetPath != null)
				{
					return Path.GetFileName(AssetPath) + (SubAsset != null ? " / " + SubAsset : "");
				}

				return Asset?.ToString() ?? "";
			}
		}

		public IconKind Icon
		{
			get
			{
				if (Component != null) return IconKind.Component;
				if (GameObject != null) return IconKind.GameObject;
				if (Asset is ScriptableObject || ScriptableObjectAssets.IsAssetFile(AssetPath)) return IconKind.ScriptableObject;
				if (Asset is Prefab || (AssetPath != null && AssetPath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))) return IconKind.Prefab;
				return IconKind.Asset;
			}
		}

		#endregion
	}

	/// <summary>
	///     Drag and drop of objects between the editor's panels (Unity's DragAndDrop). Sources (hierarchy rows,
	///     component headers, project files, object fields) put the dragged objects here with <see cref="SetPayload" />;
	///     targets (object fields, list headers) take what fits their type with <see cref="AcceptValue" /> /
	///     <see cref="AcceptValues" />. A slot takes:
	///     <list type="bullet">
	///         <item>a GameObject: the object itself (GameObject, EngineObject slots) or its first component of the slot's
	///         type, interfaces included (drop an enemy on an IDamageable field: its component implementing it);</item>
	///         <item>a component: itself, its GameObject, or a sibling component of the slot's type;</item>
	///         <item>a project file: the asset loaded as the slot's type (a ScriptableObject asset when the class it stores
	///         fits; images of the slot's texture kind).</item>
	///     </list>
	///     While a drag hovers a target, the target is outlined green when the object fits and red when it does not.
	/// </summary>
	internal static class DragAndDrop
	{
		#region PublicFields

		/// <summary>Hierarchy rows (the hierarchy re-parents on these).</summary>
		public const string GameObjectPayload = HierarchyPanel.DragType;

		/// <summary>Project files (the hierarchy and the scene view instantiate models from these).</summary>
		public const string AssetPayload = ProjectPanel.DragType;

		/// <summary>Component headers and object fields: only object fields take these.</summary>
		public const string ReferencePayload = "LEL_REFERENCE";

		/// <summary>The objects of the drag in progress (or of the last one).</summary>
		public static IReadOnlyList<DragItem> Items => items;

		#endregion

		#region PrivateFields

		private static readonly List<DragItem> items = new List<DragItem>();
		private static readonly string[] PayloadTypes = { ReferencePayload, GameObjectPayload, AssetPayload };

		#endregion

		#region PublicMethods

		/// <summary>Inside BeginDragDropSource: the dragged objects become the payload, and their names follow the cursor.</summary>
		public static unsafe void SetPayload(string payloadType, IEnumerable<DragItem> dragged)
		{
			items.Clear();
			foreach (DragItem item in dragged)
			{
				if (!item.IsEmpty)
				{
					items.Add(item);
				}
			}

			ImGui.SetDragDropPayload(payloadType, null, 0);
			DrawPreview();
		}

		public static void SetPayload(string payloadType, DragItem item)
		{
			SetPayload(payloadType, new[] { item });
		}

		/// <summary>A drag of objects is in progress.</summary>
		public static bool IsDragging
		{
			get
			{
				ImGuiPayloadPtr payload = ImGui.GetDragDropPayload();
				if (payload.IsNull)
				{
					return false;
				}

				foreach (string type in PayloadTypes)
				{
					if (payload.IsDataType(type))
					{
						return true;
					}
				}

				return false;
			}
		}

		/// <summary>Forgets the dragged objects (before the scene or the scripts go away).</summary>
		public static void Clear()
		{
			items.Clear();
		}

		/// <summary>
		///     The value a dragged object gives a slot of <paramref name="declaredType" />; false when it does not fit.
		///     Without <paramref name="load" /> project files are only checked (by extension, stored type, texture kind),
		///     and the value is null.
		/// </summary>
		public static bool TryResolve(DragItem item, Type declaredType, bool load, out object value)
		{
			value = null;
			if (declaredType == null || item.IsEmpty)
			{
				return false;
			}

			GameObject go = item.Component?.gameObject ?? item.GameObject;
			if (go != null)
			{
				if (go.dead)
				{
					return false;
				}

				// A prefab field takes a prefab instance's root as its prefab.
				if (declaredType == typeof(Prefab))
				{
					value = item.Component == null && PrefabUtility.IsPrefabInstanceRoot(go) ? go.prefabInstance.Prefab : null;
					return value != null;
				}

				if (item.Component != null && declaredType.IsInstanceOfType(item.Component))
				{
					value = item.Component;
					return true;
				}

				if (declaredType.IsInstanceOfType(go))
				{
					value = go;
					return true;
				}

				if (typeof(Behaviour).IsAssignableFrom(declaredType) || declaredType.IsInterface)
				{
					value = go.GetComponent(declaredType);
					return value != null;
				}

				return false;
			}

			if (item.Asset != null && !(item.Asset is EngineObject engineObject && engineObject.dead) && declaredType.IsInstanceOfType(item.Asset))
			{
				value = item.Asset;
				return true;
			}

			if (item.AssetPath == null || !AcceptsAssetFile(declaredType, item.AssetPath))
			{
				return false;
			}

			if (!load)
			{
				return true;
			}

			value = declaredType.IsInterface || declaredType == typeof(EngineObject)
				? AssetDatabase.LoadMainAsset(item.AssetPath, item.SubAsset)
				: AssetDatabase.Load(declaredType, item.AssetPath, item.SubAsset);
			return value != null && declaredType.IsInstanceOfType(value);
		}

		/// <summary>True when a project file can fill a slot of the type (checked without loading it).</summary>
		public static bool AcceptsAssetFile(Type declaredType, string assetPath)
		{
			if (!SerializationUtility.IsAssetType(declaredType) && !SerializationUtility.CanHoldReference(declaredType))
			{
				return false;
			}

			if (!AssetDatabase.AcceptsFile(declaredType, assetPath))
			{
				return false;
			}

			// Image files import as one texture kind: only files of the field's kind fit.
			if (declaredType == typeof(Texture2D) || declaredType == typeof(Cubemap) || declaredType == typeof(Texture3D) || declaredType == typeof(Sprite))
			{
				return MaterialEditor.ShapeMatches(assetPath, declaredType);
			}

			return true;
		}

		/// <summary>
		///     Drop target on the last item for one value of <paramref name="declaredType" />: outlines it while a drag
		///     hovers (green: an object fits, red: none does) and returns true on the drop with the first fitting value.
		/// </summary>
		public static bool AcceptValue(Type declaredType, out object value)
		{
			value = null;
			NVector2 min = ImGui.GetItemRectMin();
			NVector2 max = ImGui.GetItemRectMax();
			if (!ImGui.BeginDragDropTarget())
			{
				return false;
			}

			bool delivered = false;
			ImGuiPayloadPtr payload = AcceptOurs();
			if (!payload.IsNull)
			{
				bool fits = false;
				bool delivery = payload.IsDelivery();
				object resolved = null;
				foreach (DragItem item in items)
				{
					if (TryResolve(item, declaredType, delivery, out resolved))
					{
						fits = true;
						break;
					}
				}

				EditorIcons.DropHighlight(min, max, fits);
				if (!fits)
				{
					ImGui.SetMouseCursor(ImGuiMouseCursor.NotAllowed);
				}

				if (fits && delivery)
				{
					value = resolved;
					delivered = true;
				}
			}

			ImGui.EndDragDropTarget();
			return delivered;
		}

		/// <summary>
		///     Drop target on the last item for any number of values of <paramref name="elementType" /> (a list header):
		///     true on the drop with every fitting value.
		/// </summary>
		public static bool AcceptValues(Type elementType, out List<object> values)
		{
			values = null;
			NVector2 min = ImGui.GetItemRectMin();
			NVector2 max = ImGui.GetItemRectMax();
			if (!ImGui.BeginDragDropTarget())
			{
				return false;
			}

			bool delivered = false;
			ImGuiPayloadPtr payload = AcceptOurs();
			if (!payload.IsNull)
			{
				bool delivery = payload.IsDelivery();
				var found = new List<object>();
				foreach (DragItem item in items)
				{
					if (TryResolve(item, elementType, delivery, out object resolved))
					{
						found.Add(resolved);
					}
				}

				EditorIcons.DropHighlight(min, max, found.Count > 0);
				if (found.Count == 0)
				{
					ImGui.SetMouseCursor(ImGuiMouseCursor.NotAllowed);
				}

				if (found.Count > 0 && delivery)
				{
					values = found;
					delivered = true;
				}
			}

			ImGui.EndDragDropTarget();
			return delivered;
		}

		/// <summary>True when the dragged objects hold something a slot of the type takes (no loading).</summary>
		public static bool AnyFits(Type declaredType)
		{
			foreach (DragItem item in items)
			{
				if (TryResolve(item, declaredType, false, out _))
				{
					return true;
				}
			}

			return false;
		}

		#endregion

		#region PrivateMethods

		/// <summary>The payload of a drag of ours over the current target (without ImGui's own rectangle), or null.</summary>
		private static ImGuiPayloadPtr AcceptOurs()
		{
			const ImGuiDragDropFlags flags = ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect;
			foreach (string type in PayloadTypes)
			{
				ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(type, flags);
				if (!payload.IsNull)
				{
					return payload;
				}
			}

			return default;
		}

		/// <summary>The drag tooltip: the first object's icon and name, and how many more there are.</summary>
		private static void DrawPreview()
		{
			if (items.Count == 0)
			{
				return;
			}

			float size = ImGui.GetTextLineHeight();
			NVector2 start = ImGui.GetCursorScreenPos();
			EditorIcons.Draw(ImGui.GetWindowDrawList(), start, size, items[0].Icon, ImGui.GetColorU32(ImGuiCol.Text));
			ImGui.Dummy(new NVector2(size, size));
			ImGui.SameLine();
			ImGui.TextUnformatted(items[0].Label + (items.Count > 1 ? "  (+" + (items.Count - 1) + " more)" : ""));
		}

		#endregion
	}
}
