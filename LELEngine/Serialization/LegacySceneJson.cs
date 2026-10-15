using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using OpenTK.Mathematics;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Reads scenes saved by engine versions that wrote JSON (format "LELEngine.Scene" version 1). Kept so old
	///     projects open; the scene is written back in the binary format. Values: numbers / bools / strings, enums by
	///     name, vectors / quaternions / colors as number arrays, [Serializable] objects as objects of their fields,
	///     scene references as {"ref": id}, assets as {"guid": "...", "path": "..."}.
	/// </summary>
	public static class LegacySceneJson
	{
		#region PublicMethods

		public static Scene Load(string json, string fallbackName)
		{
			using (JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
			{
				JsonElement root = document.RootElement;
				JsonElement value;
				string name = root.TryGetProperty("name", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : fallbackName ?? "Untitled";
				var scene = new Scene(name);
				var context = new SerializationContext { Scene = scene };

				if (root.TryGetProperty("settings", out value))
				{
					context.Location = "settings";
					ReadFields(value, scene.Settings, context, 0);
				}

				var pending = new List<(Behaviour component, JsonElement fields, string location)>();
				if (root.TryGetProperty("gameObjects", out value))
				{
					foreach (JsonElement entry in value.EnumerateArray())
					{
						CreateObject(entry, scene, context, pending);
					}
				}

				foreach ((Behaviour component, JsonElement fields, string location) item in pending)
				{
					context.Location = item.location;
					ReadFields(item.fields, item.component, context, 0);
				}

				foreach ((Behaviour component, JsonElement fields, string location) item in pending)
				{
					if (item.component is ISerializationCallbackReceiver receiver)
					{
						try
						{
							receiver.OnAfterDeserialize();
						}
						catch (Exception e)
						{
							Debug.LogException(e, item.component);
						}
					}
				}

				return scene;
			}
		}

		#endregion

		#region PrivateMethods

		private static void CreateObject(JsonElement entry, Scene scene, SerializationContext context, List<(Behaviour, JsonElement, string)> pending)
		{
			JsonElement value;
			ulong id = entry.TryGetProperty("id", out value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : 0;
			string name = entry.TryGetProperty("name", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "GameObject";
			var go = new GameObject(name, scene, id);
			context.Objects[go.Id] = go;

			if (entry.TryGetProperty("hideFlags", out value) && value.ValueKind == JsonValueKind.Number)
			{
				go.hideFlags = (HideFlags)value.GetInt32();
			}

			ulong parentId = entry.TryGetProperty("parent", out value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : 0;
			if (parentId != 0)
			{
				object parent;
				if (context.Objects.TryGetValue(parentId, out parent) && parent is GameObject parentObject)
				{
					go.transform.SetParent(parentObject.transform, false);
				}
			}

			if (entry.TryGetProperty("active", out value) && value.ValueKind == JsonValueKind.False)
			{
				go.SetActive(false);
			}

			if (!entry.TryGetProperty("components", out value))
			{
				return;
			}

			foreach (JsonElement componentEntry in value.EnumerateArray())
			{
				JsonElement property;
				string typeName = componentEntry.TryGetProperty("type", out property) ? property.GetString() : null;
				ulong componentId = componentEntry.TryGetProperty("id", out property) && property.ValueKind == JsonValueKind.Number ? property.GetUInt64() : 0;
				bool enabled = !componentEntry.TryGetProperty("enabled", out property) || property.ValueKind != JsonValueKind.False;
				JsonElement fields = componentEntry.TryGetProperty("fields", out property) ? property : default;

				Type type = TypeRegistry.Resolve(typeName);
				Behaviour component;
				if (type == typeof(Transform))
				{
					component = go.transform;
					if (componentId != 0)
					{
						component.Id = componentId;
					}
				}
				else if (type != null && typeof(Behaviour).IsAssignableFrom(type) && !type.IsAbstract)
				{
					component = (Behaviour)Activator.CreateInstance(type);
					go.LinkComponent(component, componentId);
				}
				else
				{
					Debug.LogWarning($"[Scene] '{name}': component type '{typeName}' not found; kept as a missing component");
					component = new MissingComponent { TypeName = typeName, Data = ToNode(typeName, fields) };
					go.LinkComponent(component, componentId);
				}

				context.Objects[component.Id] = component;
				if (!enabled)
				{
					component.enabled = false;
				}

				if (!(component is MissingComponent) && fields.ValueKind == JsonValueKind.Object)
				{
					pending.Add((component, fields, name + "/" + type.Name));
				}
			}
		}

		private static void ReadFields(JsonElement json, object target, SerializationContext context, int depth)
		{
			if (json.ValueKind != JsonValueKind.Object)
			{
				return;
			}

			foreach (SerializedField field in SerializationUtility.GetFields(target.GetType()))
			{
				JsonElement value;
				if (!json.TryGetProperty(field.Name, out value))
				{
					continue;
				}

				try
				{
					object current = field.GetValue(target);
					field.SetValue(target, ReadValue(value, field.FieldType, current, context, depth));
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Serialization] {context?.Location}.{field.Name}: cannot read {value.ValueKind} as {field.FieldType.Name} ({e.Message}); keeping the default");
				}
			}
		}

		private static object ReadValue(JsonElement json, Type type, object current, SerializationContext context, int depth)
		{
			if (json.ValueKind == JsonValueKind.Null)
			{
				return type.IsValueType ? Activator.CreateInstance(type) : null;
			}

			if (type.IsEnum)
			{
				return json.ValueKind == JsonValueKind.Number ? Enum.ToObject(type, json.GetInt64()) : Enum.Parse(type, json.GetString(), true);
			}

			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Boolean: return json.GetBoolean();
				case TypeCode.Char:
					string text = json.GetString();
					return string.IsNullOrEmpty(text) ? '\0' : text[0];
				case TypeCode.SByte: return (sbyte)json.GetInt64();
				case TypeCode.Int16: return (short)json.GetInt64();
				case TypeCode.Int32: return json.TryGetInt32(out int i32) ? i32 : (int)json.GetDouble();
				case TypeCode.Int64: return json.GetInt64();
				case TypeCode.Byte: return (byte)json.GetUInt64();
				case TypeCode.UInt16: return (ushort)json.GetUInt64();
				case TypeCode.UInt32: return (uint)json.GetUInt64();
				case TypeCode.UInt64: return json.GetUInt64();
				case TypeCode.Single: return json.GetSingle();
				case TypeCode.Double: return json.GetDouble();
				case TypeCode.String: return json.GetString();
			}

			object math;
			if (ReadMath(json, type, out math))
			{
				return math;
			}

			if (SerializationUtility.IsReferenceType(type))
			{
				JsonElement id;
				if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("ref", out id))
				{
					return null;
				}

				object target;
				if (!context.Objects.TryGetValue(id.GetUInt64(), out target))
				{
					return null;
				}

				if (target is GameObject go && type != typeof(GameObject))
				{
					return go.GetComponent(type);
				}

				return type.IsInstanceOfType(target) ? target : null;
			}

			if (SerializationUtility.IsAssetType(type))
			{
				if (json.ValueKind != JsonValueKind.Object)
				{
					return null;
				}

				JsonElement guid;
				JsonElement path;
				string guidValue = json.TryGetProperty("guid", out guid) && guid.ValueKind == JsonValueKind.String ? guid.GetString() : null;
				string pathValue = json.TryGetProperty("path", out path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
				return guidValue != null ? AssetDatabase.LoadByGuid(type, guidValue, pathValue) : AssetDatabase.Load(type, AssetDatabase.Resolve(pathValue, null));
			}

			Type element = SerializationUtility.GetElementType(type);
			if (element != null)
			{
				var items = new List<object>();
				foreach (JsonElement item in json.EnumerateArray())
				{
					items.Add(ReadValue(item, element, null, context, depth + 1));
				}

				if (type.IsArray)
				{
					Array array = Array.CreateInstance(element, items.Count);
					for (int i = 0; i < items.Count; i++)
					{
						array.SetValue(items[i], i);
					}

					return array;
				}

				IList list = (IList)Activator.CreateInstance(type);
				foreach (object item in items)
				{
					list.Add(item);
				}

				return list;
			}

			if (depth >= SerializationUtility.MaxDepth || json.ValueKind != JsonValueKind.Object || type.IsAbstract || type.IsInterface)
			{
				return current;
			}

			object nested = current ?? Activator.CreateInstance(type);
			ReadFields(json, nested, context, depth + 1);
			return nested;
		}

		private static bool ReadMath(JsonElement json, Type type, out object value)
		{
			value = null;
			if (type == typeof(Vector2))
			{
				float[] f = ReadFloats(json, 2);
				value = new Vector2(f[0], f[1]);
			}
			else if (type == typeof(Vector3))
			{
				float[] f = ReadFloats(json, 3);
				value = new Vector3(f[0], f[1], f[2]);
			}
			else if (type == typeof(Vector4))
			{
				float[] f = ReadFloats(json, 4);
				value = new Vector4(f[0], f[1], f[2], f[3]);
			}
			else if (type == typeof(Quaternion))
			{
				float[] f = ReadFloats(json, 4);
				value = new Quaternion(f[0], f[1], f[2], f[3]);
			}
			else if (type == typeof(Color4))
			{
				float[] f = ReadFloats(json, 4);
				value = new Color4(f[0], f[1], f[2], f[3]);
			}
			else if (type == typeof(Vector2i))
			{
				float[] f = ReadFloats(json, 2);
				value = new Vector2i((int)f[0], (int)f[1]);
			}
			else if (type == typeof(Vector3i))
			{
				float[] f = ReadFloats(json, 3);
				value = new Vector3i((int)f[0], (int)f[1], (int)f[2]);
			}
			else if (type == typeof(Vector4i))
			{
				float[] f = ReadFloats(json, 4);
				value = new Vector4i((int)f[0], (int)f[1], (int)f[2], (int)f[3]);
			}

			return value != null;
		}

		private static float[] ReadFloats(JsonElement json, int count)
		{
			var result = new float[count];
			int i = 0;
			foreach (JsonElement item in json.EnumerateArray())
			{
				if (i >= count)
				{
					break;
				}

				result[i++] = item.GetSingle();
			}

			return result;
		}

		/// <summary>Field values of a missing script as generic data (numbers as doubles, number arrays as arrays).</summary>
		private static SerializedNode ToNode(string typeName, JsonElement fields)
		{
			var node = new SerializedNode { TypeName = typeName };
			if (fields.ValueKind != JsonValueKind.Object)
			{
				return node;
			}

			foreach (JsonProperty property in fields.EnumerateObject())
			{
				var entry = new SerializedProperty { Name = property.Name };
				entry.Value = ToGeneric(property.Value, out entry.Tag, out entry.ElementTag);
				node.Properties.Add(entry);
			}

			return node;
		}

		private static object ToGeneric(JsonElement json, out ValueTag tag, out ValueTag elementTag)
		{
			elementTag = ValueTag.Null;
			switch (json.ValueKind)
			{
				case JsonValueKind.True:
				case JsonValueKind.False:
					tag = ValueTag.Bool;
					return json.GetBoolean();
				case JsonValueKind.Number:
					tag = ValueTag.Double;
					return json.GetDouble();
				case JsonValueKind.String:
					tag = ValueTag.String;
					return json.GetString();
				case JsonValueKind.Array:
				{
					tag = ValueTag.Array;
					var array = new SerializedArray { ElementTag = ValueTag.Null };
					foreach (JsonElement item in json.EnumerateArray())
					{
						ValueTag itemTag;
						object value = ToGeneric(item, out itemTag, out _);
						if (array.ElementTag == ValueTag.Null)
						{
							array.ElementTag = itemTag;
						}

						if (itemTag == array.ElementTag)
						{
							array.Items.Add(value);
						}
					}

					elementTag = array.ElementTag;
					return array;
				}
				case JsonValueKind.Object:
				{
					JsonElement inner;
					if (json.TryGetProperty("ref", out inner) && inner.ValueKind == JsonValueKind.Number)
					{
						tag = ValueTag.SceneRef;
						return inner.GetUInt64();
					}

					if (json.TryGetProperty("guid", out inner) || json.TryGetProperty("path", out inner))
					{
						tag = ValueTag.Asset;
						JsonElement path;
						return new AssetReference
						{
							Guid = json.TryGetProperty("guid", out inner) && inner.ValueKind == JsonValueKind.String ? inner.GetString() : null,
							Path = json.TryGetProperty("path", out path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null
						};
					}

					tag = ValueTag.Object;
					return ToNode("Object", json);
				}
				default:
					tag = ValueTag.Null;
					return null;
			}
		}

		#endregion
	}
}
