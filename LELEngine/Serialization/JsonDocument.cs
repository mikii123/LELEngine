using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenTK.Mathematics;

namespace LELEngine.Serialization
{
	/// <summary>
	///     The readable twin of the binary document (<see cref="BinaryDocument" />): the same model (typed fields,
	///     polymorphic objects, scene references, assets, generic data of unknown types) as indented JSON, written by
	///     the editor so project files can be read, diffed and merged. Game builds convert every document to the
	///     binary form (<see cref="Documents.Convert" />), which needs no types: the "$types" table records every
	///     type's fields with their encodings (enums with their members), exactly like the binary type table.
	///     <code>
	///     {
	///       "$format": "LELEngine", "$version": 2, "$kind": "Scene" | "Object",
	///       ...body laid out by the caller...
	///       "$types": { "Namespace.Type, Assembly": { "field": "Single" | "Array&lt;Vector3&gt;" | "Enum(A=0,B=1)" | ... } }
	///     }
	///     Object value:  { "$type": "Namespace.Type, Assembly", "field": value, ... } or null; an interface / EngineObject
	///                    slot holding a reference: a scene ref or an asset value as below
	///     Vectors:       [x, y, z]; colors [r, g, b, a]; quaternions [x, y, z, w]
	///     Enums:         "Name" (flags: "A, B")
	///     Arrays:        [ ... ] or, for long arrays of fixed-size elements, { "$bytes": "base64 of the element bytes" }
	///     Dictionaries:  { "key": value, ... } for string, enum, integer, bool and char keys,
	///                    [ { "key": k, "value": v }, ... ] for the other key types
	///     Scene refs:    { "$ref": id } or null;  assets: { "guid": "...", "path": "...", "name": "sub-asset" } or null
	///     </code>
	/// </summary>
	public static class JsonDocumentFormat
	{
		public const string Format = "LELEngine";
		public const int Version = 2;
		public const string FormatKey = "$format";
		public const string VersionKey = "$version";
		public const string KindKey = "$kind";
		public const string TypesKey = "$types";
		public const string TypeKey = "$type";
		public const string RefKey = "$ref";
		public const string BytesKey = "$bytes";

		/// <summary>Arrays of fixed-size elements longer than this are written as base64 blocks instead of JSON arrays.</summary>
		public const int BulkThreshold = 64;

		/// <summary>True when the bytes look like a JSON object (an optional BOM / whitespace, then '{').</summary>
		public static bool IsJson(byte[] data)
		{
			if (data == null)
			{
				return false;
			}

			int i = 0;
			if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
			{
				i = 3;
			}

			while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\r' || data[i] == '\n'))
			{
				i++;
			}

			return i < data.Length && data[i] == (byte)'{';
		}

		/// <summary>
		///     Puts short arrays of primitives on one line ("[0, 0.5, 0]"): Utf8JsonWriter indents every element on its
		///     own line, which makes vectors unreadable.
		/// </summary>
		public static byte[] CollapseShortArrays(ReadOnlySpan<byte> json)
		{
			var output = new ArrayBufferWriter<byte>(json.Length);
			int i = 0;
			bool inString = false;
			while (i < json.Length)
			{
				byte c = json[i];
				if (inString)
				{
					Write(output, c);
					if (c == (byte)'\\' && i + 1 < json.Length)
					{
						Write(output, json[++i]);
					}
					else if (c == (byte)'"')
					{
						inString = false;
					}

					i++;
					continue;
				}

				if (c == (byte)'"')
				{
					inString = true;
					Write(output, c);
					i++;
					continue;
				}

				if (c == (byte)'[')
				{
					int end = FindPrimitiveArrayEnd(json, i);
					if (end > 0)
					{
						WriteCollapsed(output, json.Slice(i, end - i + 1));
						i = end + 1;
						continue;
					}
				}

				Write(output, c);
				i++;
			}

			return output.WrittenSpan.ToArray();
		}

		#region PrivateMethods

		private static void Write(ArrayBufferWriter<byte> output, byte value)
		{
			output.GetSpan(1)[0] = value;
			output.Advance(1);
		}

		/// <summary>Index of the ']' closing an array holding only primitives and short enough to collapse, else -1.</summary>
		private static int FindPrimitiveArrayEnd(ReadOnlySpan<byte> json, int start)
		{
			int significant = 0;
			bool inString = false;
			for (int i = start + 1; i < json.Length; i++)
			{
				byte c = json[i];
				if (inString)
				{
					significant++;
					if (c == (byte)'\\')
					{
						i++;
						significant++;
					}
					else if (c == (byte)'"')
					{
						inString = false;
					}

					continue;
				}

				switch (c)
				{
					case (byte)'"':
						inString = true;
						significant++;
						break;
					case (byte)'[':
					case (byte)'{':
						return -1;
					case (byte)']':
						return significant <= 96 ? i : -1;
					case (byte)' ':
					case (byte)'\t':
					case (byte)'\r':
					case (byte)'\n':
						break;
					default:
						significant++;
						break;
				}

				if (significant > 96)
				{
					return -1;
				}
			}

			return -1;
		}

		private static void WriteCollapsed(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> array)
		{
			bool inString = false;
			for (int i = 0; i < array.Length; i++)
			{
				byte c = array[i];
				if (inString)
				{
					Write(output, c);
					if (c == (byte)'\\' && i + 1 < array.Length)
					{
						Write(output, array[++i]);
					}
					else if (c == (byte)'"')
					{
						inString = false;
					}

					continue;
				}

				if (c == (byte)'"')
				{
					inString = true;
					Write(output, c);
				}
				else if (c == (byte)',')
				{
					Write(output, c);
					Write(output, (byte)' ');
				}
				else if (c != (byte)' ' && c != (byte)'\t' && c != (byte)'\r' && c != (byte)'\n')
				{
					Write(output, c);
				}
			}
		}

		#endregion
	}

	/// <summary>Writes one JSON document: callers lay out the body with <see cref="Json" /> and the value writers.</summary>
	public sealed class JsonDocumentWriter : IDisposable
	{
		#region PublicFields

		public readonly Utf8JsonWriter Json;
		public readonly SerializationContext Context;
		public string Kind { get; }

		#endregion

		#region PrivateFields

		private readonly ArrayBufferWriter<byte> buffer = new ArrayBufferWriter<byte>(16384);
		private readonly List<SchemaType> schemas = new List<SchemaType>();
		private readonly Dictionary<string, SchemaType> schemasByName = new Dictionary<string, SchemaType>(StringComparer.Ordinal);
		private readonly Dictionary<Type, SchemaType> schemasByType = new Dictionary<Type, SchemaType>();
		private byte[] result;

		#endregion

		#region Constructors

		public JsonDocumentWriter(SerializationContext context, string kind)
		{
			Context = context ?? new SerializationContext();
			Kind = kind ?? BinaryDocument.KindObject;
			Json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
			Json.WriteStartObject();
			Json.WriteString(JsonDocumentFormat.FormatKey, JsonDocumentFormat.Format);
			Json.WriteNumber(JsonDocumentFormat.VersionKey, JsonDocumentFormat.Version);
			Json.WriteString(JsonDocumentFormat.KindKey, Kind);
		}

		#endregion

		#region PublicMethods

		/// <summary>Closes the document (writing the type table) and returns its UTF-8 text.</summary>
		public byte[] ToArray()
		{
			if (result == null)
			{
				WriteTypes();
				Json.WriteEndObject();
				Json.Flush();
				result = JsonDocumentFormat.CollapseShortArrays(buffer.WrittenSpan);
			}

			return result;
		}

		public string ToText()
		{
			return Encoding.UTF8.GetString(ToArray());
		}

		public void Dispose()
		{
			Json.Dispose();
		}

		/// <summary>Registers a live type in the type table (its serialized fields with encodings and enum members).</summary>
		public SchemaType Register(Type type)
		{
			SchemaType schema;
			if (schemasByType.TryGetValue(type, out schema))
			{
				return schema;
			}

			SerializedField[] fields = SerializationUtility.GetFields(type);
			schema = new SchemaType { Name = TypeRegistry.GetName(type), Fields = new SchemaField[fields.Length] };
			for (int i = 0; i < fields.Length; i++)
			{
				// With the nested specs of collections and the members of enums (typeless conversion reads exactly).
				schema.Fields[i] = SerializationUtility.SchemaOf(fields[i].Name, fields[i].FieldType);
			}

			schemasByType[type] = schema;
			AddSchema(schema);
			return schema;
		}

		/// <summary>Registers a schema read from another document (conversion) or built from generic data.</summary>
		public void RegisterSchema(SchemaType schema)
		{
			if (schema != null)
			{
				AddSchema(schema);
			}
		}

		public void Register(SerializedNode node)
		{
			var schema = new SchemaType { Name = node.TypeName, Fields = new SchemaField[node.Properties.Count] };
			for (int i = 0; i < node.Properties.Count; i++)
			{
				SerializedProperty property = node.Properties[i];
				schema.Fields[i] = GenericSpec(property.Name, property.Tag, property.ElementTag, property.Value, 0);
			}

			AddSchema(schema);
		}

		/// <summary>
		///     The spec of generic data (no live type): the stored encodings, and for collections of collections the
		///     nested encodings the data itself carries (taken from its first element).
		/// </summary>
		internal static SchemaField GenericSpec(string name, ValueTag tag, ValueTag elementTag, object value, int depth)
		{
			var field = new SchemaField { Name = name, Tag = tag, ElementTag = elementTag };
			if (depth > SerializationUtility.MaxDepth)
			{
				return field;
			}

			if (tag == ValueTag.Array && value is SerializedArray array)
			{
				field.ElementTag = array.ElementTag;
				if (array.ElementTag == ValueTag.Array || array.ElementTag == ValueTag.Dictionary)
				{
					object first = array.Bytes == null ? array.Items.Find(item => item != null) : null;
					field.Element = GenericSpec(null, array.ElementTag, ValueTag.Null, first, depth + 1);
				}
			}
			else if (tag == ValueTag.Dictionary && value is SerializedDictionary dictionary)
			{
				field.KeyTag = dictionary.KeyTag;
				field.ElementTag = dictionary.ValueTag;
				field.Key = new SchemaField { Tag = dictionary.KeyTag };
				if (dictionary.ValueTag == ValueTag.Array || dictionary.ValueTag == ValueTag.Dictionary)
				{
					object first = null;
					foreach (KeyValuePair<object, object> entry in dictionary.Entries)
					{
						if (entry.Value != null)
						{
							first = entry.Value;
							break;
						}
					}

					field.Element = GenericSpec(null, dictionary.ValueTag, ValueTag.Null, first, depth + 1);
				}
			}

			return field;
		}

		/// <summary>Writes the serialized fields of <paramref name="target" /> into the JSON object being written.</summary>
		public void WriteFields(object target, int depth = 0)
		{
			Type type = target.GetType();
			Register(type);
			foreach (SerializedField field in SerializationUtility.GetFields(type))
			{
				Json.WritePropertyName(field.Name);
				WriteField(target, field, depth);
			}
		}

		/// <summary>A named property holding an Object value.</summary>
		public void WriteObjectProperty(string name, object value, int depth = 0)
		{
			Json.WritePropertyName(name);
			WriteObjectValue(value, depth);
		}

		/// <summary>
		///     An Object value: null, or an object with "$type" and the fields of the runtime type; a GameObject or
		///     component (interface / EngineObject slots) as { "$ref": id }, a ScriptableObject as its asset reference.
		/// </summary>
		public void WriteObjectValue(object value, int depth)
		{
			switch (value)
			{
				case null:
					Json.WriteNullValue();
					return;
				case SerializedNode node:
					WriteGenericObject(node);
					return;
				case ulong sceneId:
					WriteReference(sceneId);
					return;
				case AssetReference assetReference:
					WriteAsset(assetReference);
					return;
				case GameObject _:
				case Behaviour _:
					WriteReference(DocumentValues.SceneReferenceId(value, Context));
					return;
				case ScriptableObject scriptableObject:
					WriteAsset(DocumentValues.ReferenceOf(scriptableObject, Context));
					return;
			}

			if (depth >= SerializationUtility.MaxDepth)
			{
				SerializationUtility.WarnDepth(value, Context);
				Json.WriteNullValue();
				return;
			}

			if (value is ISerializationCallbackReceiver receiver)
			{
				try
				{
					receiver.OnBeforeSerialize();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			Json.WriteStartObject();
			Json.WriteString(JsonDocumentFormat.TypeKey, TypeRegistry.GetName(value.GetType()));
			WriteFields(value, depth + 1);
			Json.WriteEndObject();
		}

		/// <summary>Writes the properties of an unknown-typed object (with "$type") exactly as they were read.</summary>
		public void WriteGenericObject(SerializedNode node)
		{
			if (node == null)
			{
				Json.WriteNullValue();
				return;
			}

			Register(node);
			Json.WriteStartObject();
			Json.WriteString(JsonDocumentFormat.TypeKey, node.TypeName);
			WriteGenericFields(node);
			Json.WriteEndObject();
		}

		/// <summary>The property values of a node into the JSON object being written.</summary>
		public void WriteGenericFields(SerializedNode node)
		{
			foreach (SerializedProperty property in node.Properties)
			{
				Json.WritePropertyName(property.Name);
				WriteValue(property.Value, property.Tag, property.ElementTag, 0);
			}
		}

		/// <summary>Writes a value of a known encoding at the current position.</summary>
		public void WriteValue(object value, ValueTag tag, ValueTag elementTag, int depth)
		{
			if (value == null && tag != ValueTag.Object && tag != ValueTag.Array && tag != ValueTag.SceneRef && tag != ValueTag.Asset && tag != ValueTag.String && tag != ValueTag.Dictionary)
			{
				// Missing primitive: its default.
				value = DefaultOf(tag);
			}

			switch (tag)
			{
				case ValueTag.Bool: Json.WriteBooleanValue(Convert.ToBoolean(value)); break;
				case ValueTag.SByte:
				case ValueTag.Int16:
				case ValueTag.Int32:
				case ValueTag.Int64: Json.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
				case ValueTag.Byte:
				case ValueTag.UInt16:
				case ValueTag.UInt32:
				case ValueTag.UInt64: Json.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture)); break;
				case ValueTag.Single: WriteFloat(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
				case ValueTag.Double: WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
				case ValueTag.Char: Json.WriteStringValue(value is char c ? c.ToString() : Convert.ToString(value, CultureInfo.InvariantCulture)); break;
				case ValueTag.String:
					if (value == null) Json.WriteNullValue();
					else Json.WriteStringValue(value.ToString());
					break;
				case ValueTag.Vector2: WriteVector((Vector2)value); break;
				case ValueTag.Vector3: WriteVector((Vector3)value); break;
				case ValueTag.Vector4: WriteVector((Vector4)value); break;
				case ValueTag.Quaternion: WriteQuaternion((Quaternion)value); break;
				case ValueTag.Color4: WriteColor((Color4)value); break;
				case ValueTag.Vector2i: WriteVector((Vector2i)value); break;
				case ValueTag.Vector3i: WriteVector((Vector3i)value); break;
				case ValueTag.Vector4i: WriteVector((Vector4i)value); break;
				case ValueTag.Enum: WriteEnum(value); break;
				case ValueTag.Object: WriteObjectValue(value, depth); break;
				case ValueTag.Array: WriteArrayValue(value, elementTag, depth); break;
				case ValueTag.SceneRef: WriteReference(DocumentValues.SceneReferenceId(value, Context)); break;
				case ValueTag.Asset: WriteAsset(value is AssetReference stored ? stored : DocumentValues.ReferenceOf(value as IAsset, Context)); break;
				case ValueTag.Dictionary: WriteDictionaryValue(value, depth); break;
				default: Json.WriteNullValue(); break;
			}
		}

		/// <summary>True when keys of this encoding are written as JSON property names (the dictionary is a JSON object).</summary>
		public static bool IsNameKey(ValueTag keyTag)
		{
			switch (keyTag)
			{
				case ValueTag.String:
				case ValueTag.Enum:
				case ValueTag.Bool:
				case ValueTag.Char:
				case ValueTag.SByte:
				case ValueTag.Byte:
				case ValueTag.Int16:
				case ValueTag.UInt16:
				case ValueTag.Int32:
				case ValueTag.UInt32:
				case ValueTag.Int64:
				case ValueTag.UInt64:
					return true;
				default:
					return false;
			}
		}

		#endregion

		#region PrivateMethods

		private void AddSchema(SchemaType schema)
		{
			SchemaType existing;
			if (!schemasByName.TryGetValue(schema.Name, out existing))
			{
				schemasByName[schema.Name] = schema;
				schemas.Add(schema);
				return;
			}

			if (ReferenceEquals(existing, schema))
			{
				return;
			}

			// The same type name with another layout (generic data of two versions of a script): keep the union.
			var fields = new List<SchemaField>(existing.Fields);
			foreach (SchemaField field in schema.Fields)
			{
				bool known = false;
				foreach (SchemaField present in fields)
				{
					if (present.Name == field.Name)
					{
						known = true;
						if (present.EnumMembers == null && field.EnumMembers != null)
						{
							present.EnumMembers = field.EnumMembers;
						}

						if (present.Tag == field.Tag)
						{
							present.Element ??= field.Element;
							present.Key ??= field.Key;
							if (present.KeyTag == ValueTag.Null)
							{
								present.KeyTag = field.KeyTag;
							}
						}

						break;
					}
				}

				if (!known)
				{
					fields.Add(field);
				}
			}

			existing.Fields = fields.ToArray();
		}

		private void WriteTypes()
		{
			Json.WriteStartObject(JsonDocumentFormat.TypesKey);
			foreach (SchemaType schema in schemas)
			{
				Json.WriteStartObject(schema.Name);
				foreach (SchemaField field in schema.Fields)
				{
					Json.WriteString(field.Name, SerializationUtility.TagSpec(field));
				}

				Json.WriteEndObject();
			}

			Json.WriteEndObject();
		}

		private void WriteField(object target, SerializedField field, int depth)
		{
			FieldAccessor accessor = field.Accessor;
			switch (field.Tag)
			{
				case ValueTag.Bool: Json.WriteBooleanValue(((FieldAccessor<bool>)accessor).Get(target)); return;
				case ValueTag.SByte: Json.WriteNumberValue(((FieldAccessor<sbyte>)accessor).Get(target)); return;
				case ValueTag.Byte: Json.WriteNumberValue(((FieldAccessor<byte>)accessor).Get(target)); return;
				case ValueTag.Int16: Json.WriteNumberValue(((FieldAccessor<short>)accessor).Get(target)); return;
				case ValueTag.UInt16: Json.WriteNumberValue(((FieldAccessor<ushort>)accessor).Get(target)); return;
				case ValueTag.Int32: Json.WriteNumberValue(((FieldAccessor<int>)accessor).Get(target)); return;
				case ValueTag.UInt32: Json.WriteNumberValue(((FieldAccessor<uint>)accessor).Get(target)); return;
				case ValueTag.Int64: Json.WriteNumberValue(((FieldAccessor<long>)accessor).Get(target)); return;
				case ValueTag.UInt64: Json.WriteNumberValue(((FieldAccessor<ulong>)accessor).Get(target)); return;
				case ValueTag.Single: WriteFloat(((FieldAccessor<float>)accessor).Get(target)); return;
				case ValueTag.Double: WriteDouble(((FieldAccessor<double>)accessor).Get(target)); return;
				case ValueTag.Char: Json.WriteStringValue(((FieldAccessor<char>)accessor).Get(target).ToString()); return;
				case ValueTag.String:
				{
					string text = ((FieldAccessor<string>)accessor).Get(target);
					if (text == null) Json.WriteNullValue();
					else Json.WriteStringValue(text);
					return;
				}
				case ValueTag.Vector2: WriteVector(((FieldAccessor<Vector2>)accessor).Get(target)); return;
				case ValueTag.Vector3: WriteVector(((FieldAccessor<Vector3>)accessor).Get(target)); return;
				case ValueTag.Vector4: WriteVector(((FieldAccessor<Vector4>)accessor).Get(target)); return;
				case ValueTag.Quaternion: WriteQuaternion(((FieldAccessor<Quaternion>)accessor).Get(target)); return;
				case ValueTag.Color4: WriteColor(((FieldAccessor<Color4>)accessor).Get(target)); return;
				case ValueTag.Vector2i: WriteVector(((FieldAccessor<Vector2i>)accessor).Get(target)); return;
				case ValueTag.Vector3i: WriteVector(((FieldAccessor<Vector3i>)accessor).Get(target)); return;
				case ValueTag.Vector4i: WriteVector(((FieldAccessor<Vector4i>)accessor).Get(target)); return;
				default:
					WriteValue(accessor.GetBoxed(target), field.Tag, field.ElementTag, depth);
					return;
			}
		}

		private void WriteFloat(float value)
		{
			if (float.IsFinite(value)) Json.WriteNumberValue(value);
			else Json.WriteStringValue(float.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
		}

		private void WriteDouble(double value)
		{
			if (double.IsFinite(value)) Json.WriteNumberValue(value);
			else Json.WriteStringValue(double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
		}

		private void WriteVector(Vector2 v)
		{
			Json.WriteStartArray();
			WriteFloat(v.X);
			WriteFloat(v.Y);
			Json.WriteEndArray();
		}

		private void WriteVector(Vector3 v)
		{
			Json.WriteStartArray();
			WriteFloat(v.X);
			WriteFloat(v.Y);
			WriteFloat(v.Z);
			Json.WriteEndArray();
		}

		private void WriteVector(Vector4 v)
		{
			Json.WriteStartArray();
			WriteFloat(v.X);
			WriteFloat(v.Y);
			WriteFloat(v.Z);
			WriteFloat(v.W);
			Json.WriteEndArray();
		}

		private void WriteQuaternion(Quaternion q)
		{
			Json.WriteStartArray();
			WriteFloat(q.X);
			WriteFloat(q.Y);
			WriteFloat(q.Z);
			WriteFloat(q.W);
			Json.WriteEndArray();
		}

		private void WriteColor(Color4 c)
		{
			Json.WriteStartArray();
			WriteFloat(c.R);
			WriteFloat(c.G);
			WriteFloat(c.B);
			WriteFloat(c.A);
			Json.WriteEndArray();
		}

		private void WriteVector(Vector2i v)
		{
			Json.WriteStartArray();
			Json.WriteNumberValue(v.X);
			Json.WriteNumberValue(v.Y);
			Json.WriteEndArray();
		}

		private void WriteVector(Vector3i v)
		{
			Json.WriteStartArray();
			Json.WriteNumberValue(v.X);
			Json.WriteNumberValue(v.Y);
			Json.WriteNumberValue(v.Z);
			Json.WriteEndArray();
		}

		private void WriteVector(Vector4i v)
		{
			Json.WriteStartArray();
			Json.WriteNumberValue(v.X);
			Json.WriteNumberValue(v.Y);
			Json.WriteNumberValue(v.Z);
			Json.WriteNumberValue(v.W);
			Json.WriteEndArray();
		}

		private void WriteEnum(object value)
		{
			if (value is string name)
			{
				Json.WriteStringValue(name);
			}
			else if (value != null && value.GetType().IsEnum)
			{
				Json.WriteStringValue(value.ToString());
			}
			else
			{
				Json.WriteNumberValue(value != null ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0L);
			}
		}

		private void WriteReference(ulong id)
		{
			if (id == 0)
			{
				Json.WriteNullValue();
				return;
			}

			Json.WriteStartObject();
			Json.WriteNumber(JsonDocumentFormat.RefKey, id);
			Json.WriteEndObject();
		}

		private void WriteAsset(AssetReference reference)
		{
			if (reference.IsNull)
			{
				Json.WriteNullValue();
				return;
			}

			Json.WriteStartObject();
			if (reference.Guid != null) Json.WriteString("guid", reference.Guid);
			if (reference.Path != null) Json.WriteString("path", reference.Path);
			if (reference.Name != null) Json.WriteString("name", reference.Name);
			Json.WriteEndObject();
		}

		private void WriteArrayValue(object value, ValueTag elementTag, int depth)
		{
			if (value == null)
			{
				Json.WriteNullValue();
				return;
			}

			if (elementTag == ValueTag.Null)
			{
				// An element of another collection: its own element type decides.
				elementTag = SerializationUtility.ElementTagOf(value);
			}

			if (value is SerializedArray generic)
			{
				if (generic.Bytes != null)
				{
					if (generic.Count > JsonDocumentFormat.BulkThreshold || !SerializationUtility.IsBlittable(generic.ElementTag))
					{
						WriteBulk(generic.Bytes);
						return;
					}

					// Short enough to read: expand the stored bytes into values.
					var reader = new ByteReader(generic.Bytes);
					Json.WriteStartArray();
					for (int i = 0; i < generic.Count; i++)
					{
						WriteValue(ReadBlittable(reader, generic.ElementTag), generic.ElementTag, ValueTag.Null, depth + 1);
					}

					Json.WriteEndArray();
					return;
				}

				Json.WriteStartArray();
				foreach (object item in generic.Items)
				{
					WriteValue(item, generic.ElementTag, ValueTag.Null, depth + 1);
				}

				Json.WriteEndArray();
				return;
			}

			var list = (IList)value;
			if (SerializationUtility.IsBlittable(elementTag) && list.Count > JsonDocumentFormat.BulkThreshold)
			{
				int size = SerializationUtility.FixedSize(elementTag);
				if (value is Array array)
				{
					WriteBulk(ArrayBytes.Of(array, size));
				}
				else
				{
					var bytes = new ByteWriter(list.Count * size);
					BulkListAccess.Write(bytes, list, SerializationUtility.GetElementType(value.GetType()));
					WriteBulk(bytes.WrittenSpan);
				}

				return;
			}

			Json.WriteStartArray();
			foreach (object item in list)
			{
				WriteValue(item, elementTag, ValueTag.Null, depth + 1);
			}

			Json.WriteEndArray();
		}

		private void WriteDictionaryValue(object value, int depth)
		{
			if (value == null)
			{
				Json.WriteNullValue();
				return;
			}

			ValueTag keyTag;
			ValueTag valueTag;
			var entries = new List<KeyValuePair<object, object>>();
			if (value is SerializedDictionary generic)
			{
				keyTag = generic.KeyTag;
				valueTag = generic.ValueTag;
				entries.AddRange(generic.Entries);
			}
			else
			{
				SerializationUtility.GetDictionaryTypes(value.GetType(), out Type keyType, out Type valueType);
				keyTag = SerializationUtility.TagOf(keyType, out _);
				valueTag = SerializationUtility.TagOf(valueType, out _);
				foreach (DictionaryEntry entry in (IDictionary)value)
				{
					entries.Add(new KeyValuePair<object, object>(entry.Key, entry.Value));
				}
			}

			if (IsNameKey(keyTag))
			{
				Json.WriteStartObject();
				foreach (KeyValuePair<object, object> entry in entries)
				{
					Json.WritePropertyName(KeyName(entry.Key));
					WriteValue(entry.Value, valueTag, ValueTag.Null, depth + 1);
				}

				Json.WriteEndObject();
				return;
			}

			Json.WriteStartArray();
			foreach (KeyValuePair<object, object> entry in entries)
			{
				Json.WriteStartObject();
				Json.WritePropertyName("key");
				WriteValue(entry.Key, keyTag, ValueTag.Null, depth + 1);
				Json.WritePropertyName("value");
				WriteValue(entry.Value, valueTag, ValueTag.Null, depth + 1);
				Json.WriteEndObject();
			}

			Json.WriteEndArray();
		}

		/// <summary>A dictionary key as a JSON property name: strings as they are, enums by name, numbers, bools and chars as text.</summary>
		private static string KeyName(object key)
		{
			switch (key)
			{
				case null:
					return "";
				case string text:
					return text;
				case bool flag:
					return flag ? "true" : "false";
				case char c:
					return c.ToString();
				case Enum named:
					return named.ToString();
				default:
					return Convert.ToString(key, CultureInfo.InvariantCulture);
			}
		}

		private void WriteBulk(ReadOnlySpan<byte> bytes)
		{
			Json.WriteStartObject();
			Json.WriteBase64String(JsonDocumentFormat.BytesKey, bytes);
			Json.WriteEndObject();
		}

		internal static object ReadBlittable(ByteReader reader, ValueTag tag)
		{
			switch (tag)
			{
				case ValueTag.Bool: return reader.ReadBool();
				case ValueTag.SByte: return reader.ReadSByte();
				case ValueTag.Byte: return reader.ReadByte();
				case ValueTag.Int16: return reader.ReadInt16();
				case ValueTag.UInt16: return reader.ReadUInt16();
				case ValueTag.Int32: return reader.ReadInt32();
				case ValueTag.UInt32: return reader.ReadUInt32();
				case ValueTag.Int64: return reader.ReadInt64();
				case ValueTag.UInt64: return reader.ReadUInt64();
				case ValueTag.Single: return reader.ReadSingle();
				case ValueTag.Double: return reader.ReadDouble();
				case ValueTag.Char: return reader.ReadChar();
				case ValueTag.Vector2: return reader.ReadVector2();
				case ValueTag.Vector3: return reader.ReadVector3();
				case ValueTag.Vector4: return reader.ReadVector4();
				case ValueTag.Quaternion: return reader.ReadQuaternion();
				case ValueTag.Color4: return reader.ReadColor4();
				case ValueTag.Vector2i: return reader.ReadVector2i();
				case ValueTag.Vector3i: return reader.ReadVector3i();
				case ValueTag.Vector4i: return reader.ReadVector4i();
				default: return null;
			}
		}

		private static object DefaultOf(ValueTag tag)
		{
			switch (tag)
			{
				case ValueTag.Bool: return false;
				case ValueTag.Char: return '\0';
				case ValueTag.Vector2: return Vector2.Zero;
				case ValueTag.Vector3: return Vector3.Zero;
				case ValueTag.Vector4: return Vector4.Zero;
				case ValueTag.Quaternion: return Quaternion.Identity;
				case ValueTag.Color4: return default(Color4);
				case ValueTag.Vector2i: return Vector2i.Zero;
				case ValueTag.Vector3i: return Vector3i.Zero;
				case ValueTag.Vector4i: return Vector4i.Zero;
				default: return 0;
			}
		}

		#endregion
	}

	/// <summary>Reads one JSON document written by <see cref="JsonDocumentWriter" /> (or hand-written in its layout).</summary>
	public sealed class JsonDocumentReader : IDisposable
	{
		#region PublicFields

		public readonly JsonElement Root;
		public readonly SerializationContext Context;
		public int Version { get; }
		public string Kind { get; }

		#endregion

		#region PrivateFields

		private readonly JsonDocument document;
		private readonly Dictionary<string, SchemaType> schemas = new Dictionary<string, SchemaType>(StringComparer.Ordinal);

		#endregion

		#region Constructors

		public JsonDocumentReader(byte[] data, SerializationContext context)
			: this(JsonDocument.Parse(data, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }), context)
		{ }

		public JsonDocumentReader(string text, SerializationContext context)
			: this(JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }), context)
		{ }

		private JsonDocumentReader(JsonDocument document, SerializationContext context)
		{
			this.document = document;
			Context = context ?? new SerializationContext();
			Root = document.RootElement;
			if (Root.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidOperationException("Not a LELEngine document (the root is not an object)");
			}

			JsonElement value;
			Version = Root.TryGetProperty(JsonDocumentFormat.VersionKey, out value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 1;
			if (Root.TryGetProperty(JsonDocumentFormat.KindKey, out value) && value.ValueKind == JsonValueKind.String)
			{
				Kind = value.GetString();
			}
			else
			{
				Kind = Root.TryGetProperty("gameObjects", out _) ? BinaryDocument.KindScene : BinaryDocument.KindObject;
			}

			if (Root.TryGetProperty(JsonDocumentFormat.TypesKey, out value) && value.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty type in value.EnumerateObject())
				{
					var fields = new List<SchemaField>();
					if (type.Value.ValueKind == JsonValueKind.Object)
					{
						foreach (JsonProperty field in type.Value.EnumerateObject())
						{
							SchemaField parsed;
							if (field.Value.ValueKind == JsonValueKind.String && SerializationUtility.TryParseTagSpec(field.Value.GetString(), out parsed))
							{
								parsed.Name = field.Name;
								fields.Add(parsed);
							}
						}
					}

					schemas[type.Name] = new SchemaType { Name = type.Name, Fields = fields.ToArray() };
				}
			}
		}

		#endregion

		#region PublicMethods

		public void Dispose()
		{
			document.Dispose();
		}

		/// <summary>The schema recorded for a type name, or null.</summary>
		public SchemaType Schema(string typeName)
		{
			SchemaType schema;
			return typeName != null && schemas.TryGetValue(typeName, out schema) ? schema : null;
		}

		public IEnumerable<SchemaType> Schemas => schemas.Values;

		/// <summary>The "$type" of an object element, or null.</summary>
		public static string TypeNameOf(JsonElement element)
		{
			JsonElement type;
			return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(JsonDocumentFormat.TypeKey, out type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
		}

		/// <summary>
		///     Reads the properties of <paramref name="element" /> into the serialized fields of <paramref name="target" />:
		///     properties the type lacks are ignored, fields the element lacks keep their values, retyped values are
		///     converted when possible.
		/// </summary>
		public void ReadFields(object target, JsonElement element, int depth = 0)
		{
			if (element.ValueKind != JsonValueKind.Object)
			{
				return;
			}

			foreach (SerializedField field in SerializationUtility.GetFields(target.GetType()))
			{
				JsonElement value;
				if (!element.TryGetProperty(field.Name, out value))
				{
					continue;
				}

				try
				{
					object current = field.Tag == ValueTag.Object || field.Tag == ValueTag.Array || field.Tag == ValueTag.Dictionary ? field.Accessor.GetBoxed(target) : null;
					object read = ReadBoxed(field.Tag, field.ElementTag, field.FieldType, current, value, depth);
					if (!ReferenceEquals(read, current) || field.FieldType.IsValueType || read == null)
					{
						field.Accessor.SetBoxed(target, read);
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Serialization] {Context?.Location}.{field.Name}: cannot read {value.ValueKind} as {field.FieldType.Name} ({e.Message}); keeping the default");
				}
			}
		}

		/// <summary>An Object value (an element with "$type") into <paramref name="current" /> or a new instance.</summary>
		public object ReadObjectValue(Type declaredType, object current, JsonElement element, int depth)
		{
			if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
			{
				return declaredType != null && declaredType.IsValueType ? current ?? Activator.CreateInstance(declaredType) : null;
			}

			if (element.ValueKind != JsonValueKind.Object)
			{
				return current;
			}

			if (declaredType == null)
			{
				return ReadGenericObject(element);
			}

			string typeName = TypeNameOf(element);
			Type type = typeName != null ? TypeRegistry.Resolve(typeName) : null;
			if (type == null || !declaredType.IsAssignableFrom(type))
			{
				if (type != null)
				{
					Debug.LogWarning($"[Serialization] {Context?.Location}: stored {type.Name} does not fit a {declaredType.Name} field; using the field's type");
				}

				type = !declaredType.IsAbstract && !declaredType.IsInterface ? declaredType : null;
			}

			if (type == null || depth >= SerializationUtility.MaxDepth)
			{
				if (type == null)
				{
					Debug.LogWarning($"[Serialization] {Context?.Location}: type '{typeName}' not found; value dropped");
				}

				return current;
			}

			object target = current != null && current.GetType() == type ? current : DocumentValues.TryCreate(type);
			if (target == null)
			{
				return current;
			}

			ReadFields(target, element, depth + 1);
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

		/// <summary>
		///     Reads any value as a boxed object of <paramref name="targetType" /> (null: generic form, see
		///     <see cref="ReadGenericValue" />); values of another kind than the field expects are converted when possible.
		/// </summary>
		public object ReadBoxed(ValueTag tag, ValueTag elementTag, Type targetType, object current, JsonElement e, int depth)
		{
			if (e.ValueKind == JsonValueKind.Null || e.ValueKind == JsonValueKind.Undefined)
			{
				return targetType != null && targetType.IsValueType ? current ?? Activator.CreateInstance(targetType) : null;
			}

			switch (tag)
			{
				case ValueTag.Bool:
					if (e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False) return e.GetBoolean();
					break;
				case ValueTag.SByte: if (e.ValueKind == JsonValueKind.Number) return (sbyte)ReadInteger(e); break;
				case ValueTag.Byte: if (e.ValueKind == JsonValueKind.Number) return (byte)ReadInteger(e); break;
				case ValueTag.Int16: if (e.ValueKind == JsonValueKind.Number) return (short)ReadInteger(e); break;
				case ValueTag.UInt16: if (e.ValueKind == JsonValueKind.Number) return (ushort)ReadInteger(e); break;
				case ValueTag.Int32: if (e.ValueKind == JsonValueKind.Number) return (int)ReadInteger(e); break;
				case ValueTag.UInt32: if (e.ValueKind == JsonValueKind.Number) return (uint)ReadInteger(e); break;
				case ValueTag.Int64: if (e.ValueKind == JsonValueKind.Number) return ReadInteger(e); break;
				case ValueTag.UInt64:
					if (e.ValueKind == JsonValueKind.Number) return e.TryGetUInt64(out ulong u) ? u : (ulong)ReadInteger(e);
					break;
				case ValueTag.Single:
					if (e.ValueKind == JsonValueKind.Number) return e.GetSingle();
					if (e.ValueKind == JsonValueKind.String && TryParseSpecial(e.GetString(), out double special)) return (float)special;
					break;
				case ValueTag.Double:
					if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
					if (e.ValueKind == JsonValueKind.String && TryParseSpecial(e.GetString(), out double specialDouble)) return specialDouble;
					break;
				case ValueTag.Char:
					if (e.ValueKind == JsonValueKind.String)
					{
						string text = e.GetString();
						return text.Length > 0 ? text[0] : '\0';
					}

					break;
				case ValueTag.String:
					if (e.ValueKind == JsonValueKind.String) return e.GetString();
					break;
				case ValueTag.Vector2:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 2); return new Vector2(f[0], f[1]); }
					break;
				case ValueTag.Vector3:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 3); return new Vector3(f[0], f[1], f[2]); }
					break;
				case ValueTag.Vector4:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 4); return new Vector4(f[0], f[1], f[2], f[3]); }
					break;
				case ValueTag.Quaternion:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 4); return new Quaternion(f[0], f[1], f[2], f[3]); }
					break;
				case ValueTag.Color4:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 4); return new Color4(f[0], f[1], f[2], f[3]); }
					break;
				case ValueTag.Vector2i:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 2); return new Vector2i((int)f[0], (int)f[1]); }
					break;
				case ValueTag.Vector3i:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 3); return new Vector3i((int)f[0], (int)f[1], (int)f[2]); }
					break;
				case ValueTag.Vector4i:
					if (e.ValueKind == JsonValueKind.Array) { float[] f = ReadFloats(e, 4); return new Vector4i((int)f[0], (int)f[1], (int)f[2], (int)f[3]); }
					break;
				case ValueTag.Enum:
					if (targetType != null && targetType.IsEnum)
					{
						if (e.ValueKind == JsonValueKind.String) return Enum.Parse(targetType, e.GetString(), true);
						if (e.ValueKind == JsonValueKind.Number) return Enum.ToObject(targetType, ReadInteger(e));
					}
					else if (e.ValueKind == JsonValueKind.Number)
					{
						return ReadInteger(e);
					}

					break;
				case ValueTag.Object:
					if (e.ValueKind == JsonValueKind.Object)
					{
						// Interface / EngineObject slots hold references too: a scene object or a ScriptableObject asset.
						JsonElement id;
						if (e.TryGetProperty(JsonDocumentFormat.RefKey, out id) && id.ValueKind == JsonValueKind.Number)
						{
							ulong value = id.TryGetUInt64(out ulong unsigned) ? unsigned : 0;
							return targetType != null ? DocumentValues.ResolveReference(value, targetType, Context) : value;
						}

						if ((targetType == null || SerializationUtility.CanHoldReference(targetType)) && IsAssetElement(e))
						{
							AssetReference reference = ReadAssetReference(e);
							return targetType != null ? DocumentValues.LoadAsset(reference, targetType) : reference;
						}

						if (!e.TryGetProperty(JsonDocumentFormat.BytesKey, out _))
						{
							return ReadObjectValue(targetType, current, e, depth);
						}
					}

					break;
				case ValueTag.Dictionary:
					if (e.ValueKind == JsonValueKind.Object || e.ValueKind == JsonValueKind.Array)
					{
						return ReadDictionaryValue(targetType, current, e, depth);
					}

					break;
				case ValueTag.Array:
					if (e.ValueKind == JsonValueKind.Array || (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(JsonDocumentFormat.BytesKey, out _)))
					{
						return ReadArrayValue(elementTag, targetType, current, e, depth);
					}

					break;
				case ValueTag.SceneRef:
				{
					JsonElement id;
					if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(JsonDocumentFormat.RefKey, out id) && id.ValueKind == JsonValueKind.Number)
					{
						ulong value = id.GetUInt64();
						return targetType != null ? DocumentValues.ResolveReference(value, targetType, Context) : value;
					}

					break;
				}
				case ValueTag.Asset:
					if (e.ValueKind == JsonValueKind.Object && TypeNameOf(e) == null)
					{
						AssetReference reference = ReadAssetReference(e);
						return targetType != null ? DocumentValues.LoadAsset(reference, targetType) : reference;
					}

					break;
			}

			// The stored value has another shape than the field: read it as what it is and convert.
			ValueTag storedTag;
			object generic = ReadGenericValue(e, null, out storedTag, out _);
			if (targetType == null)
			{
				return generic;
			}

			object converted;
			if (DocumentValues.TryConvert(generic, storedTag, targetType, Context, out converted))
			{
				return converted;
			}

			Debug.LogWarning($"[Serialization] {Context?.Location}: cannot read {e.ValueKind} as {targetType.Name}; keeping the default");
			return current;
		}

		/// <summary>An unknown-typed object as generic data; the encodings come from the "$types" table or the JSON values.</summary>
		public SerializedNode ReadGenericObject(JsonElement element)
		{
			var node = new SerializedNode { TypeName = TypeNameOf(element) ?? "Object" };
			SchemaType schema = Schema(node.TypeName);
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (property.Name.StartsWith("$", StringComparison.Ordinal))
				{
					continue;
				}

				var entry = new SerializedProperty { Name = property.Name };
				entry.Value = ReadGenericValue(property.Value, FindField(schema, property.Name), out entry.Tag, out entry.ElementTag);
				node.Properties.Add(entry);
			}

			return node;
		}

		/// <summary>
		///     Any value in generic form (numbers boxed by their recorded encoding, enums as their values, nodes, generic
		///     arrays, reference ids, asset references); <paramref name="hint" /> is the recorded encoding when known.
		/// </summary>
		public object ReadGenericValue(JsonElement e, SchemaField hint, out ValueTag tag, out ValueTag elementTag)
		{
			elementTag = hint != null ? hint.ElementTag : ValueTag.Null;
			if (hint != null && hint.Tag == ValueTag.Dictionary && (e.ValueKind == JsonValueKind.Object || e.ValueKind == JsonValueKind.Array))
			{
				tag = ValueTag.Dictionary;
				SerializedDictionary dictionary = ReadGenericDictionary(e, hint);
				elementTag = dictionary.ValueTag;
				return dictionary;
			}

			switch (e.ValueKind)
			{
				case JsonValueKind.True:
				case JsonValueKind.False:
					tag = ValueTag.Bool;
					return e.GetBoolean();
				case JsonValueKind.Number:
				{
					ValueTag wanted = hint != null && (DocumentValues.IsNumber(hint.Tag) || hint.Tag == ValueTag.Enum || hint.Tag == ValueTag.Bool || hint.Tag == ValueTag.Char) ? hint.Tag : ValueTag.Null;
					if (wanted == ValueTag.Null)
					{
						long integral;
						if (e.TryGetInt64(out integral))
						{
							tag = ValueTag.Int64;
							return integral;
						}

						tag = ValueTag.Double;
						return e.GetDouble();
					}

					tag = wanted;
					switch (wanted)
					{
						case ValueTag.Bool: return ReadInteger(e) != 0;
						case ValueTag.SByte: return (sbyte)ReadInteger(e);
						case ValueTag.Byte: return (byte)ReadInteger(e);
						case ValueTag.Int16: return (short)ReadInteger(e);
						case ValueTag.UInt16: return (ushort)ReadInteger(e);
						case ValueTag.Int32: return (int)ReadInteger(e);
						case ValueTag.UInt32: return (uint)ReadInteger(e);
						case ValueTag.Int64: return ReadInteger(e);
						case ValueTag.UInt64: return e.TryGetUInt64(out ulong u) ? u : (ulong)ReadInteger(e);
						case ValueTag.Single: return e.GetSingle();
						case ValueTag.Double: return e.GetDouble();
						case ValueTag.Char: return (char)ReadInteger(e);
						default: return ReadInteger(e); // Enum
					}
				}
				case JsonValueKind.String:
				{
					string text = e.GetString();
					if (hint != null)
					{
						switch (hint.Tag)
						{
							case ValueTag.Enum:
							{
								long value;
								if (TryMapEnum(text, hint.EnumMembers, out value))
								{
									tag = ValueTag.Enum;
									return value;
								}

								break;
							}
							case ValueTag.Single:
							case ValueTag.Double:
							{
								double special;
								if (TryParseSpecial(text, out special))
								{
									tag = hint.Tag;
									return hint.Tag == ValueTag.Single ? (object)(float)special : special;
								}

								break;
							}
							case ValueTag.Char:
								tag = ValueTag.Char;
								return text.Length > 0 ? text[0] : '\0';
						}
					}

					tag = ValueTag.String;
					return text;
				}
				case JsonValueKind.Array:
				{
					// Vectors, quaternions and colors are number arrays: the recorded encoding tells them apart from arrays.
					if (hint != null && IsMathTag(hint.Tag))
					{
						tag = hint.Tag;
						return ReadMath(e, hint.Tag);
					}

					tag = ValueTag.Array;
					var array = new SerializedArray();
					ValueTag element = elementTag;
					if (element == ValueTag.Null)
					{
						element = InferElementTag(e);
					}

					array.ElementTag = element;
					// The recorded element spec reads nested collections and enum names exactly.
					SchemaField elementHint = hint != null && hint.Tag == ValueTag.Array && hint.ElementTag == element
						? SerializationUtility.ElementSpec(hint)
						: new SchemaField { Tag = element, EnumMembers = hint?.EnumMembers };
					foreach (JsonElement item in e.EnumerateArray())
					{
						ValueTag itemTag;
						object value = ReadGenericValue(item, elementHint, out itemTag, out _);
						if (itemTag != element && value != null)
						{
							object converted;
							if (!ConvertGeneric(value, itemTag, element, out converted))
							{
								continue;
							}

							value = converted;
						}

						array.Items.Add(value);
					}

					elementTag = element;
					return array;
				}
				case JsonValueKind.Object:
				{
					// A reference in an interface / EngineObject slot keeps the slot's Object encoding (ulong / AssetReference value).
					bool objectSlot = hint?.Tag == ValueTag.Object;
					JsonElement inner;
					if (e.TryGetProperty(JsonDocumentFormat.RefKey, out inner) && inner.ValueKind == JsonValueKind.Number)
					{
						tag = objectSlot ? ValueTag.Object : ValueTag.SceneRef;
						return inner.TryGetUInt64(out ulong id) ? id : 0UL;
					}

					if (e.TryGetProperty(JsonDocumentFormat.BytesKey, out inner) && inner.ValueKind == JsonValueKind.String)
					{
						tag = ValueTag.Array;
						if (elementTag == ValueTag.Null || !SerializationUtility.IsBlittable(elementTag))
						{
							elementTag = ValueTag.Byte;
						}

						return new SerializedArray { ElementTag = elementTag, Bytes = inner.GetBytesFromBase64() };
					}

					if (TypeNameOf(e) == null && (hint?.Tag == ValueTag.Asset || IsAssetElement(e)))
					{
						tag = objectSlot ? ValueTag.Object : ValueTag.Asset;
						return ReadAssetReference(e);
					}

					tag = ValueTag.Object;
					return ReadGenericObject(e);
				}
				default:
					tag = hint != null ? hint.Tag : ValueTag.Null;
					return null;
			}
		}

		/// <summary>An asset reference element: guid / path / name.</summary>
		public static AssetReference ReadAssetReference(JsonElement e)
		{
			JsonElement value;
			return new AssetReference
			{
				Guid = e.TryGetProperty("guid", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null,
				Path = e.TryGetProperty("path", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null,
				Name = e.TryGetProperty("name", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null
			};
		}

		/// <summary>An object element shaped like an asset reference: no "$type", a "guid" or "path" string.</summary>
		public static bool IsAssetElement(JsonElement e)
		{
			JsonElement value;
			return e.ValueKind == JsonValueKind.Object && TypeNameOf(e) == null
				&& ((e.TryGetProperty("guid", out value) && value.ValueKind == JsonValueKind.String) || (e.TryGetProperty("path", out value) && value.ValueKind == JsonValueKind.String));
		}

		#endregion

		#region PrivateMethods

		/// <summary>A dictionary (object form or { "key", "value" } pairs) into <paramref name="current" /> (cleared) or a new dictionary.</summary>
		private object ReadDictionaryValue(Type targetType, object current, JsonElement e, int depth)
		{
			if (targetType == null || !SerializationUtility.GetDictionaryTypes(targetType, out Type keyType, out Type valueType))
			{
				return ReadGenericValue(e, new SchemaField { Tag = ValueTag.Dictionary }, out _, out _);
			}

			IDictionary dictionary = DocumentValues.ReuseDictionary(targetType, current);
			ValueTag keyTag = SerializationUtility.TagOf(keyType, out ValueTag keyElementTag);
			ValueTag valueTag = SerializationUtility.TagOf(valueType, out ValueTag valueElementTag);
			if (e.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty property in e.EnumerateObject())
				{
					object key;
					if (!TryParseKey(property.Name, keyType, out key))
					{
						Debug.LogWarning($"[Serialization] {Context?.Location}: dictionary key '{property.Name}' is not a {keyType.Name}; entry dropped");
						continue;
					}

					dictionary[key] = ReadBoxed(valueTag, valueElementTag, valueType, null, property.Value, depth + 1);
				}

				return dictionary;
			}

			foreach (JsonElement item in e.EnumerateArray())
			{
				JsonElement keyJson;
				if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("key", out keyJson))
				{
					continue;
				}

				object key = ReadBoxed(keyTag, keyElementTag, keyType, null, keyJson, depth + 1);
				if (!DocumentValues.IsUsableKey(key))
				{
					continue;
				}

				JsonElement valueJson;
				object value = item.TryGetProperty("value", out valueJson)
					? ReadBoxed(valueTag, valueElementTag, valueType, null, valueJson, depth + 1)
					: valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
				dictionary[key] = value;
			}

			return dictionary;
		}

		/// <summary>A dictionary key from a JSON property name (the object form): strings, enums (names or numbers), numbers, bools, chars.</summary>
		private static bool TryParseKey(string name, Type keyType, out object key)
		{
			key = null;
			try
			{
				if (keyType == typeof(string))
				{
					key = name;
					return true;
				}

				if (keyType.IsEnum)
				{
					object parsed;
					if (Enum.TryParse(keyType, name, true, out parsed))
					{
						key = parsed;
						return true;
					}

					return false;
				}

				if (keyType == typeof(char))
				{
					key = name.Length > 0 ? name[0] : '\0';
					return true;
				}

				if (keyType == typeof(bool))
				{
					bool flag;
					if (bool.TryParse(name, out flag))
					{
						key = flag;
						return true;
					}

					return false;
				}

				key = Convert.ChangeType(name, keyType, CultureInfo.InvariantCulture);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>Generic dictionary data with the recorded key and value specs (typeless conversion).</summary>
		private SerializedDictionary ReadGenericDictionary(JsonElement e, SchemaField hint)
		{
			SchemaField keySpec = SerializationUtility.KeySpec(hint);
			SchemaField valueSpec = SerializationUtility.ElementSpec(hint);
			var result = new SerializedDictionary { KeyTag = keySpec.Tag, ValueTag = valueSpec.Tag };
			if (e.ValueKind == JsonValueKind.Object)
			{
				if (result.KeyTag == ValueTag.Null)
				{
					result.KeyTag = ValueTag.String;
				}

				foreach (JsonProperty property in e.EnumerateObject())
				{
					object key;
					if (!TryGenericKey(property.Name, result.KeyTag, keySpec.EnumMembers, out key))
					{
						continue;
					}

					result.Entries.Add(new KeyValuePair<object, object>(key, ReadGenericMember(property.Value, valueSpec, ref result.ValueTag)));
				}

				return result;
			}

			foreach (JsonElement item in e.EnumerateArray())
			{
				JsonElement keyElement;
				if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("key", out keyElement))
				{
					continue;
				}

				object key = ReadGenericMember(keyElement, keySpec, ref result.KeyTag);
				if (key == null)
				{
					continue;
				}

				JsonElement valueElement;
				object value = item.TryGetProperty("value", out valueElement) ? ReadGenericMember(valueElement, valueSpec, ref result.ValueTag) : null;
				result.Entries.Add(new KeyValuePair<object, object>(key, value));
			}

			return result;
		}

		/// <summary>A key or value of generic dictionary data in the dictionary's encoding (the first one decides when none is recorded).</summary>
		private object ReadGenericMember(JsonElement e, SchemaField spec, ref ValueTag expected)
		{
			ValueTag tag;
			object value = ReadGenericValue(e, spec.Tag != ValueTag.Null ? spec : null, out tag, out _);
			if (expected == ValueTag.Null)
			{
				expected = tag;
				return value;
			}

			if (value == null || tag == expected)
			{
				return value;
			}

			object converted;
			return ConvertGeneric(value, tag, expected, out converted) ? converted : null;
		}

		/// <summary>A key of generic dictionary data from a property name, boxed in its encoding's type.</summary>
		private static bool TryGenericKey(string name, ValueTag keyTag, KeyValuePair<string, long>[] enumMembers, out object key)
		{
			key = null;
			switch (keyTag)
			{
				case ValueTag.String:
					key = name;
					return true;
				case ValueTag.Char:
					key = name.Length > 0 ? name[0] : '\0';
					return true;
				case ValueTag.Bool:
				{
					bool flag;
					if (!bool.TryParse(name, out flag))
					{
						return false;
					}

					key = flag;
					return true;
				}
				case ValueTag.Enum:
				{
					long value;
					if (TryMapEnum(name, enumMembers, out value) || long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
					{
						key = value;
						return true;
					}

					// A member name the type table does not list: the value is unknown without the type.
					return false;
				}
				default:
					return ConvertGeneric(name, ValueTag.String, keyTag, out key);
			}
		}

		private static SchemaField FindField(SchemaType schema, string name)
		{
			if (schema == null)
			{
				return null;
			}

			foreach (SchemaField field in schema.Fields)
			{
				if (field.Name == name)
				{
					return field;
				}
			}

			return null;
		}

		private static long ReadInteger(JsonElement e)
		{
			long value;
			if (e.TryGetInt64(out value))
			{
				return value;
			}

			ulong unsigned;
			if (e.TryGetUInt64(out unsigned))
			{
				return unchecked((long)unsigned);
			}

			return (long)e.GetDouble();
		}

		private static bool TryParseSpecial(string text, out double value)
		{
			switch (text)
			{
				case "NaN": value = double.NaN; return true;
				case "Infinity": value = double.PositiveInfinity; return true;
				case "-Infinity": value = double.NegativeInfinity; return true;
			}

			return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}

		private static float[] ReadFloats(JsonElement e, int count)
		{
			var result = new float[count];
			int i = 0;
			foreach (JsonElement item in e.EnumerateArray())
			{
				if (i >= count)
				{
					break;
				}

				if (item.ValueKind == JsonValueKind.Number)
				{
					result[i] = item.GetSingle();
				}
				else if (item.ValueKind == JsonValueKind.String && TryParseSpecial(item.GetString(), out double special))
				{
					result[i] = (float)special;
				}

				i++;
			}

			return result;
		}

		private static bool IsMathTag(ValueTag tag)
		{
			return tag >= ValueTag.Vector2 && tag <= ValueTag.Vector4i;
		}

		private static object ReadMath(JsonElement e, ValueTag tag)
		{
			switch (tag)
			{
				case ValueTag.Vector2: { float[] f = ReadFloats(e, 2); return new Vector2(f[0], f[1]); }
				case ValueTag.Vector3: { float[] f = ReadFloats(e, 3); return new Vector3(f[0], f[1], f[2]); }
				case ValueTag.Vector4: { float[] f = ReadFloats(e, 4); return new Vector4(f[0], f[1], f[2], f[3]); }
				case ValueTag.Quaternion: { float[] f = ReadFloats(e, 4); return new Quaternion(f[0], f[1], f[2], f[3]); }
				case ValueTag.Color4: { float[] f = ReadFloats(e, 4); return new Color4(f[0], f[1], f[2], f[3]); }
				case ValueTag.Vector2i: { float[] f = ReadFloats(e, 2); return new Vector2i((int)f[0], (int)f[1]); }
				case ValueTag.Vector3i: { float[] f = ReadFloats(e, 3); return new Vector3i((int)f[0], (int)f[1], (int)f[2]); }
				default: { float[] f = ReadFloats(e, 4); return new Vector4i((int)f[0], (int)f[1], (int)f[2], (int)f[3]); }
			}
		}

		private static bool TryMapEnum(string text, KeyValuePair<string, long>[] members, out long value)
		{
			value = 0;
			if (members == null)
			{
				return false;
			}

			foreach (string part in text.Split(','))
			{
				string name = part.Trim();
				if (name.Length == 0)
				{
					continue;
				}

				bool found = false;
				foreach (KeyValuePair<string, long> member in members)
				{
					if (string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase))
					{
						value |= member.Value;
						found = true;
						break;
					}
				}

				if (!found)
				{
					long number;
					if (!long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
					{
						return false;
					}

					value |= number;
				}
			}

			return true;
		}

		private static ValueTag InferElementTag(JsonElement array)
		{
			foreach (JsonElement item in array.EnumerateArray())
			{
				switch (item.ValueKind)
				{
					case JsonValueKind.True:
					case JsonValueKind.False:
						return ValueTag.Bool;
					case JsonValueKind.Number:
						return ValueTag.Double;
					case JsonValueKind.String:
						return ValueTag.String;
					case JsonValueKind.Object:
						if (item.TryGetProperty(JsonDocumentFormat.RefKey, out _)) return ValueTag.SceneRef;
						if (TypeNameOf(item) == null && (item.TryGetProperty("guid", out _) || item.TryGetProperty("path", out _))) return ValueTag.Asset;
						return ValueTag.Object;
					case JsonValueKind.Array:
						return ValueTag.Null;
				}
			}

			return ValueTag.Null;
		}

		/// <summary>Converts a generic value to another primitive encoding (an array holding mixed number kinds).</summary>
		private static bool ConvertGeneric(object value, ValueTag from, ValueTag to, out object result)
		{
			result = null;
			try
			{
				switch (to)
				{
					case ValueTag.Bool: result = Convert.ToBoolean(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.SByte: result = Convert.ToSByte(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.Byte: result = Convert.ToByte(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.Int16: result = Convert.ToInt16(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.UInt16: result = Convert.ToUInt16(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.Int32: result = Convert.ToInt32(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.UInt32: result = Convert.ToUInt32(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.Int64:
					case ValueTag.Enum: result = Convert.ToInt64(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.UInt64: result = Convert.ToUInt64(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.Single: result = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.Double: result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true;
					case ValueTag.String: result = Convert.ToString(value, CultureInfo.InvariantCulture); return true;
				}
			}
			catch (Exception)
			{
				// Not convertible.
			}

			return false;
		}

		private object ReadArrayValue(ValueTag elementTag, Type targetType, object current, JsonElement e, int depth)
		{
			Type elementType = targetType != null ? SerializationUtility.GetElementType(targetType) : null;
			JsonElement bytesElement;
			if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(JsonDocumentFormat.BytesKey, out bytesElement))
			{
				byte[] bytes = bytesElement.GetBytesFromBase64();
				if (targetType == null)
				{
					return new SerializedArray { ElementTag = elementTag, Bytes = bytes };
				}

				ValueTag stored = SerializationUtility.TagOf(elementType, out _);
				if (!SerializationUtility.IsBlittable(stored))
				{
					Debug.LogWarning($"[Serialization] {Context?.Location}: a byte block cannot fill an array of {elementType.Name}");
					return current;
				}

				int size = SerializationUtility.FixedSize(stored);
				int count = bytes.Length / size;
				if (targetType.IsArray)
				{
					Array array = Array.CreateInstance(elementType, count);
					new ReadOnlySpan<byte>(bytes, 0, count * size).CopyTo(ArrayBytes.Of(array, size));
					return array;
				}

				IList list = ReuseList(targetType, current, count);
				BulkListAccess.Read(new ByteReader(bytes), list, elementType, count);
				return list;
			}

			if (e.ValueKind != JsonValueKind.Array)
			{
				return current;
			}

			int length = e.GetArrayLength();
			if (targetType == null)
			{
				ValueTag ignored;
				return ReadGenericValue(e, new SchemaField { Tag = ValueTag.Array, ElementTag = elementTag }, out ignored, out _);
			}

			ValueTag wanted = SerializationUtility.TagOf(elementType, out _);
			if (targetType.IsArray)
			{
				Array array = Array.CreateInstance(elementType, length);
				int i = 0;
				foreach (JsonElement item in e.EnumerateArray())
				{
					array.SetValue(ReadBoxed(wanted, ValueTag.Null, elementType, null, item, depth + 1), i++);
				}

				return array;
			}

			IList result = ReuseList(targetType, current, length);
			foreach (JsonElement item in e.EnumerateArray())
			{
				result.Add(ReadBoxed(wanted, ValueTag.Null, elementType, null, item, depth + 1));
			}

			return result;
		}

		private static IList ReuseList(Type listType, object current, int capacity)
		{
			IList list = current as IList;
			if (list != null && list.GetType() == listType)
			{
				list.Clear();
				return list;
			}

			return (IList)Activator.CreateInstance(listType, capacity);
		}

		#endregion
	}
}
