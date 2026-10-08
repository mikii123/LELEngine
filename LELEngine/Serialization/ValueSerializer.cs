using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;

namespace LELEngine.Serialization
{
	/// <summary>
	///     State shared while one scene is written or read: the scene references must belong to, and (reading)
	///     every object and component of the file by id.
	/// </summary>
	public sealed class SerializationContext
	{
		#region PublicFields

		public Scene Scene;
		public readonly Dictionary<ulong, object> Objects = new Dictionary<ulong, object>();

		/// <summary>Where the value being read or written lives, for messages ("Floor/MeshRenderer.mesh").</summary>
		public string Location;

		#endregion
	}

	/// <summary>
	///     JSON encoding of serialized values on the low-level System.Text.Json reader / writer (no reflection
	///     caches of its own, so game types are never pinned by the serializer). Formats:
	///     numbers / bools / strings as such, enums by name, vectors / quaternions / colors as number arrays,
	///     arrays and lists as JSON arrays, [Serializable] objects as objects of their fields, scene references
	///     as {"ref": id}, assets as {"guid": "...", "path": "..."}.
	/// </summary>
	public static class ValueSerializer
	{
		#region PublicMethods

		/// <summary>Writes the serialized fields of <paramref name="target" /> as properties of the current JSON object.</summary>
		public static void WriteFields(Utf8JsonWriter writer, object target, SerializationContext context, int depth = 0)
		{
			foreach (SerializedField field in SerializationUtility.GetFields(target.GetType()))
			{
				writer.WritePropertyName(field.Name);
				WriteValue(writer, field.GetValue(target), field.FieldType, context, depth);
			}
		}

		/// <summary>
		///     Assigns the fields present in <paramref name="json" />; absent fields keep their current (default) values,
		///     unknown properties are ignored, so scripts can gain and lose fields between saves.
		/// </summary>
		public static void ReadFields(JsonElement json, object target, SerializationContext context, int depth = 0)
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

		public static void WriteValue(Utf8JsonWriter writer, object value, Type type, SerializationContext context, int depth)
		{
			if (value == null)
			{
				writer.WriteNullValue();
				return;
			}

			if (type.IsEnum)
			{
				writer.WriteStringValue(value.ToString());
				return;
			}

			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Boolean:
					writer.WriteBooleanValue((bool)value);
					return;
				case TypeCode.Char:
					writer.WriteStringValue(value.ToString());
					return;
				case TypeCode.SByte:
				case TypeCode.Int16:
				case TypeCode.Int32:
				case TypeCode.Int64:
					writer.WriteNumberValue(Convert.ToInt64(value));
					return;
				case TypeCode.Byte:
				case TypeCode.UInt16:
				case TypeCode.UInt32:
				case TypeCode.UInt64:
					writer.WriteNumberValue(Convert.ToUInt64(value));
					return;
				case TypeCode.Single:
					writer.WriteNumberValue((float)value);
					return;
				case TypeCode.Double:
					writer.WriteNumberValue((double)value);
					return;
				case TypeCode.String:
					writer.WriteStringValue((string)value);
					return;
			}

			if (WriteMath(writer, value))
			{
				return;
			}

			if (value is GameObject go)
			{
				WriteReference(writer, go.scene == context?.Scene && (go.hideFlags & HideFlags.DontSave) == 0 && !go.destroyed ? go.Id : 0);
				return;
			}

			if (value is Behaviour component)
			{
				GameObject owner = component.gameObject;
				bool saved = owner != null && owner.scene == context?.Scene && (owner.hideFlags & HideFlags.DontSave) == 0 && !component.destroyed;
				WriteReference(writer, saved ? component.Id : 0);
				return;
			}

			if (value is IAsset asset)
			{
				WriteAsset(writer, asset, context);
				return;
			}

			Type element = SerializationUtility.GetElementType(type);
			if (element != null)
			{
				writer.WriteStartArray();
				foreach (object item in (IEnumerable)value)
				{
					WriteValue(writer, item, element, context, depth + 1);
				}

				writer.WriteEndArray();
				return;
			}

			if (depth >= SerializationUtility.MaxDepth)
			{
				writer.WriteNullValue();
				return;
			}

			writer.WriteStartObject();
			WriteFields(writer, value, context, depth + 1);
			writer.WriteEndObject();
		}

		public static object ReadValue(JsonElement json, Type type, object current, SerializationContext context, int depth)
		{
			if (json.ValueKind == JsonValueKind.Null)
			{
				return type.IsValueType ? Activator.CreateInstance(type) : null;
			}

			if (type.IsEnum)
			{
				return json.ValueKind == JsonValueKind.Number
					? Enum.ToObject(type, json.GetInt64())
					: Enum.Parse(type, json.GetString(), true);
			}

			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Boolean:
					return json.GetBoolean();
				case TypeCode.Char:
					string text = json.GetString();
					return string.IsNullOrEmpty(text) ? '\0' : text[0];
				case TypeCode.SByte:
					return (sbyte)json.GetInt64();
				case TypeCode.Int16:
					return (short)json.GetInt64();
				case TypeCode.Int32:
					return json.TryGetInt32(out int i32) ? i32 : (int)json.GetDouble();
				case TypeCode.Int64:
					return json.GetInt64();
				case TypeCode.Byte:
					return (byte)json.GetUInt64();
				case TypeCode.UInt16:
					return (ushort)json.GetUInt64();
				case TypeCode.UInt32:
					return (uint)json.GetUInt64();
				case TypeCode.UInt64:
					return json.GetUInt64();
				case TypeCode.Single:
					return json.GetSingle();
				case TypeCode.Double:
					return json.GetDouble();
				case TypeCode.String:
					return json.GetString();
			}

			object math;
			if (ReadMath(json, type, out math))
			{
				return math;
			}

			if (SerializationUtility.IsReferenceType(type))
			{
				return ReadReference(json, type, context);
			}

			if (SerializationUtility.IsAssetType(type))
			{
				return ReadAsset(json, type);
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

			if (depth >= SerializationUtility.MaxDepth || json.ValueKind != JsonValueKind.Object)
			{
				return current;
			}

			object target = current ?? Activator.CreateInstance(type);
			ReadFields(json, target, context, depth + 1);
			return target;
		}

		#endregion

		#region PrivateMethods

		private static void WriteReference(Utf8JsonWriter writer, ulong id)
		{
			if (id == 0)
			{
				writer.WriteNullValue();
				return;
			}

			writer.WriteStartObject();
			writer.WriteNumber("ref", id);
			writer.WriteEndObject();
		}

		private static object ReadReference(JsonElement json, Type type, SerializationContext context)
		{
			JsonElement id;
			if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("ref", out id) || context == null)
			{
				return null;
			}

			object target;
			if (!context.Objects.TryGetValue(id.GetUInt64(), out target))
			{
				Debug.LogWarning($"[Serialization] {context.Location}: reference to missing object {id.GetUInt64()}");
				return null;
			}

			// A component field may point at the GameObject's component of that type.
			if (target is GameObject go && type != typeof(GameObject))
			{
				return go.GetComponent(type);
			}

			return type.IsInstanceOfType(target) ? target : null;
		}

		private static void WriteAsset(Utf8JsonWriter writer, IAsset asset, SerializationContext context)
		{
			string path = asset.AssetPath;
			string guid = path != null ? AssetDatabase.GetGuid(path) : null;
			if (path == null)
			{
				// A runtime-created object (procedural mesh, code-built material) has no file to point at.
				Debug.LogWarning($"[Serialization] {context?.Location}: {asset.GetType().Name} is not an asset file; saved as null");
				writer.WriteNullValue();
				return;
			}

			if (guid == null)
			{
				Debug.LogWarning($"[Serialization] {context?.Location}: '{path}' has no meta file; saved by path only");
			}

			writer.WriteStartObject();
			writer.WriteString("guid", guid);
			writer.WriteString("path", path);
			writer.WriteEndObject();
		}

		private static object ReadAsset(JsonElement json, Type type)
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

		private static bool WriteMath(Utf8JsonWriter writer, object value)
		{
			switch (value)
			{
				case Vector2 v:
					WriteFloats(writer, v.X, v.Y);
					return true;
				case Vector3 v:
					WriteFloats(writer, v.X, v.Y, v.Z);
					return true;
				case Vector4 v:
					WriteFloats(writer, v.X, v.Y, v.Z, v.W);
					return true;
				case Quaternion q:
					WriteFloats(writer, q.X, q.Y, q.Z, q.W);
					return true;
				case Color4 c:
					WriteFloats(writer, c.R, c.G, c.B, c.A);
					return true;
				case Vector2i v:
					WriteInts(writer, v.X, v.Y);
					return true;
				case Vector3i v:
					WriteInts(writer, v.X, v.Y, v.Z);
					return true;
				case Vector4i v:
					WriteInts(writer, v.X, v.Y, v.Z, v.W);
					return true;
			}

			return false;
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

		// Vectors are written on one line ("[0, -0.25, 0]"): readable and diff friendly. float.ToString() is the
		// shortest round-trippable form, so values read back bit-exact.
		private static void WriteFloats(Utf8JsonWriter writer, params float[] values)
		{
			var builder = new StringBuilder(values.Length * 10);
			builder.Append('[');
			for (int i = 0; i < values.Length; i++)
			{
				if (i > 0)
				{
					builder.Append(", ");
				}

				float v = values[i];
				builder.Append(float.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture) : "0");
			}

			builder.Append(']');
			writer.WriteRawValue(builder.ToString(), true);
		}

		private static void WriteInts(Utf8JsonWriter writer, params int[] values)
		{
			var builder = new StringBuilder(values.Length * 6);
			builder.Append('[');
			for (int i = 0; i < values.Length; i++)
			{
				if (i > 0)
				{
					builder.Append(", ");
				}

				builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
			}

			builder.Append(']');
			writer.WriteRawValue(builder.ToString(), true);
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

		#endregion
	}
}
