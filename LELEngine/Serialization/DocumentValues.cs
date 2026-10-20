using System;
using System.Collections;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Value logic shared by the binary and JSON readers: scene reference resolution, asset loading, instantiating
	///     live objects from generic data and the conversions applied when a field changed type since the data was
	///     written (numbers, enums by name or value, number arrays to vectors, nodes to objects).
	/// </summary>
	internal static class DocumentValues
	{
		#region PublicMethods

		public static object ResolveReference(ulong id, Type type, SerializationContext context)
		{
			if (id == 0 || context == null)
			{
				return null;
			}

			object target;
			if (!context.Objects.TryGetValue(id, out target))
			{
				Debug.LogWarning($"[Serialization] {context.Location}: reference to missing object {id}");
				return null;
			}

			// A component field may point at the GameObject's component of that type.
			if (target is GameObject go && type != typeof(GameObject))
			{
				return go.GetComponent(type);
			}

			return type.IsInstanceOfType(target) ? target : null;
		}

		public static object LoadAsset(AssetReference reference, Type type)
		{
			if (reference.Guid == null && reference.Path == null)
			{
				return null;
			}

			if (reference.Guid != null)
			{
				return AssetDatabase.LoadByGuid(type, reference.Guid, reference.Path, reference.Name);
			}

			// Path only: a project asset without a meta file or an engine resource (engine shaders).
			return AssetDatabase.Load(type, AssetDatabase.Resolve(reference.Path, null) ?? reference.Path, reference.Name);
		}

		/// <summary>The reference written for an asset: GUID and path of its file, plus the sub-asset name.</summary>
		public static AssetReference ReferenceOf(IAsset asset, SerializationContext context)
		{
			if (asset == null)
			{
				return default;
			}

			string path = asset.AssetPath;
			if (path == null)
			{
				// A runtime-created object (procedural mesh, code-built material) has no file to point at.
				Debug.LogWarning($"[Serialization] {context?.Location}: {asset.GetType().Name} is not an asset file; saved as null");
				return default;
			}

			string guid = AssetDatabase.GetGuid(path);
			if (guid == null && !AssetDatabase.IsEngineResource(path))
			{
				Debug.LogWarning($"[Serialization] {context?.Location}: '{path}' has no meta file; saved by path only");
			}

			return new AssetReference { Guid = guid, Path = path, Name = asset.SubAssetName };
		}

		public static object TryCreate(Type type)
		{
			try
			{
				return Activator.CreateInstance(type, true);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Serialization] Cannot create {type.Name}: {e.Message}");
				return null;
			}
		}

		public static bool IsNumber(ValueTag tag)
		{
			return tag >= ValueTag.SByte && tag <= ValueTag.Double;
		}

		/// <summary>Conversions between encodings of a field that changed type (numbers, enums, number arrays to vectors).</summary>
		public static bool TryConvert(object value, ValueTag storedTag, Type targetType, SerializationContext context, out object result)
		{
			result = null;
			ValueTag wanted = SerializationUtility.TagOf(targetType, out ValueTag wantedElement);
			if (value == null)
			{
				return !targetType.IsValueType;
			}

			try
			{
				if (targetType.IsEnum)
				{
					if (value is string name)
					{
						result = Enum.Parse(targetType, name, true);
						return true;
					}

					if (IsNumber(storedTag) || storedTag == ValueTag.Enum)
					{
						result = Enum.ToObject(targetType, Convert.ToInt64(value));
						return true;
					}

					return false;
				}

				if (IsNumber(wanted) || wanted == ValueTag.Bool || wanted == ValueTag.Char || wanted == ValueTag.String)
				{
					if (storedTag == ValueTag.Enum && !(value is string))
					{
						value = Convert.ToInt64(value);
					}

					if (IsNumber(storedTag) || storedTag == ValueTag.Bool || storedTag == ValueTag.Char || storedTag == ValueTag.String || storedTag == ValueTag.Enum)
					{
						result = Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
						return true;
					}

					return false;
				}

				if (value is SerializedArray numbers && numbers.Items.Count >= 2 && IsNumber(numbers.ElementTag))
				{
					float[] f = new float[4];
					for (int i = 0; i < Math.Min(4, numbers.Items.Count); i++)
					{
						f[i] = Convert.ToSingle(numbers.Items[i], System.Globalization.CultureInfo.InvariantCulture);
					}

					switch (wanted)
					{
						case ValueTag.Vector2: result = new Vector2(f[0], f[1]); return true;
						case ValueTag.Vector3: result = new Vector3(f[0], f[1], f[2]); return true;
						case ValueTag.Vector4: result = new Vector4(f[0], f[1], f[2], f[3]); return true;
						case ValueTag.Quaternion: result = new Quaternion(f[0], f[1], f[2], f[3]); return true;
						case ValueTag.Color4: result = new Color4(f[0], f[1], f[2], f[3]); return true;
						case ValueTag.Vector2i: result = new Vector2i((int)f[0], (int)f[1]); return true;
						case ValueTag.Vector3i: result = new Vector3i((int)f[0], (int)f[1], (int)f[2]); return true;
						case ValueTag.Vector4i: result = new Vector4i((int)f[0], (int)f[1], (int)f[2], (int)f[3]); return true;
					}
				}

				if (wanted == ValueTag.Array && value is SerializedArray items)
				{
					Type elementType = SerializationUtility.GetElementType(targetType);
					IList list = targetType.IsArray ? Array.CreateInstance(elementType, items.Items.Count) : (IList)Activator.CreateInstance(targetType);
					for (int i = 0; i < items.Items.Count; i++)
					{
						object item;
						if (!TryConvert(items.Items[i], items.ElementTag, elementType, context, out item))
						{
							return false;
						}

						if (targetType.IsArray)
						{
							list[i] = item;
						}
						else
						{
							list.Add(item);
						}
					}

					result = list;
					return true;
				}

				if (wanted == ValueTag.SceneRef && value is ulong id)
				{
					result = ResolveReference(id, targetType, context);
					return true;
				}

				if (wanted == ValueTag.Asset && value is AssetReference asset)
				{
					result = LoadAsset(asset, targetType);
					return true;
				}

				if (wanted == ValueTag.Object && value is SerializedNode node)
				{
					result = FromNode(node, targetType, context);
					return result != null;
				}

				if (targetType.IsInstanceOfType(value))
				{
					result = value;
					return true;
				}
			}
			catch (Exception)
			{
				// Not convertible.
			}

			return false;
		}

		/// <summary>Instantiates a live object from generic data (a script that came back with a changed field layout).</summary>
		public static object FromNode(SerializedNode node, Type declaredType, SerializationContext context)
		{
			Type type = TypeRegistry.Resolve(node.TypeName);
			if (type == null || !declaredType.IsAssignableFrom(type))
			{
				type = !declaredType.IsAbstract && !declaredType.IsInterface ? declaredType : null;
			}

			if (type == null)
			{
				return null;
			}

			object target = TryCreate(type);
			if (target == null)
			{
				return null;
			}

			foreach (SerializedField field in SerializationUtility.GetFields(type))
			{
				SerializedProperty property = node.Find(field.Name);
				object converted;
				if (property != null && TryConvert(property.Value, property.Tag, field.FieldType, context, out converted))
				{
					field.Accessor.SetBoxed(target, converted);
				}
			}

			if (target is ISerializationCallbackReceiver receiver)
			{
				try
				{
					receiver.OnAfterDeserialize();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			return target;
		}

		/// <summary>Generic data of a live object: every serialized field boxed with its encoding (missing-type round trips, conversions).</summary>
		public static SerializedNode ToNode(object target, SerializationContext context, int depth = 0)
		{
			if (target == null)
			{
				return null;
			}

			if (target is SerializedNode existing)
			{
				return existing;
			}

			Type type = target.GetType();
			var node = new SerializedNode { TypeName = TypeRegistry.GetName(type) };
			foreach (SerializedField field in SerializationUtility.GetFields(type))
			{
				object value = field.GetValue(target);
				node.Properties.Add(new SerializedProperty { Name = field.Name, Tag = field.Tag, ElementTag = field.ElementTag, Value = ToGeneric(value, field.Tag, field.ElementTag, context, depth) });
			}

			return node;
		}

		/// <summary>Boxed generic form of a value: nodes for objects, generic arrays, ids for references, references for assets.</summary>
		public static object ToGeneric(object value, ValueTag tag, ValueTag elementTag, SerializationContext context, int depth)
		{
			if (value == null)
			{
				return null;
			}

			switch (tag)
			{
				case ValueTag.Enum:
					return Convert.ToInt64(value);
				case ValueTag.Object:
					return depth >= SerializationUtility.MaxDepth ? null : ToNode(value, context, depth + 1);
				case ValueTag.SceneRef:
					return SceneReferenceId(value, context);
				case ValueTag.Asset:
					return ReferenceOf(value as IAsset, context);
				case ValueTag.Array:
				{
					if (value is SerializedArray generic)
					{
						return generic;
					}

					var array = new SerializedArray { ElementTag = elementTag };
					foreach (object item in (IList)value)
					{
						array.Items.Add(ToGeneric(item, elementTag, ValueTag.Null, context, depth + 1));
					}

					return array;
				}
				default:
					return value;
			}
		}

		/// <summary>Id written for a scene reference: the object or component id when it belongs to the context's scene and is saved, else 0.</summary>
		public static ulong SceneReferenceId(object value, SerializationContext context)
		{
			Scene scene = context?.Scene;
			if (value is GameObject go)
			{
				return go.scene == scene && (go.hideFlags & HideFlags.DontSave) == 0 && !go.dead ? go.Id : 0;
			}

			if (value is Behaviour component)
			{
				GameObject owner = component.gameObject;
				bool saved = owner != null && owner.scene == scene && (owner.hideFlags & HideFlags.DontSave) == 0 && !component.dead;
				return saved ? component.Id : 0;
			}

			if (value is ulong id)
			{
				return id;
			}

			return 0;
		}

		/// <summary>Enum member names and values of a field type (for the readable schema and name-based conversion); null for non-enum types.</summary>
		public static KeyValuePair<string, long>[] EnumMembers(Type type)
		{
			if (type == null || !type.IsEnum)
			{
				return null;
			}

			Array values = Enum.GetValues(type);
			string[] names = Enum.GetNames(type);
			var result = new KeyValuePair<string, long>[names.Length];
			for (int i = 0; i < names.Length; i++)
			{
				result[i] = new KeyValuePair<string, long>(names[i], Convert.ToInt64(values.GetValue(i)));
			}

			return result;
		}

		#endregion
	}
}
