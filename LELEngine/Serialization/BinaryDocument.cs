using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
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

	/// <summary>Field of a type as stored in a document: name and encoding.</summary>
	public sealed class SchemaField
	{
		public string Name;
		public ValueTag Tag;
		public ValueTag ElementTag;
	}

	/// <summary>A type as stored in a document: its name (see <see cref="TypeRegistry.GetName" />) and fields.</summary>
	public sealed class SchemaType
	{
		public string Name;
		public SchemaField[] Fields;
	}

	/// <summary>
	///     Serialized data of an object whose type is unknown (a missing script): the values by field name, written
	///     back unchanged when the scene is saved.
	/// </summary>
	public sealed class SerializedNode
	{
		public string TypeName;
		public readonly List<SerializedProperty> Properties = new List<SerializedProperty>();

		public SerializedProperty Find(string name)
		{
			foreach (SerializedProperty property in Properties)
			{
				if (property.Name == name)
				{
					return property;
				}
			}

			return null;
		}
	}

	/// <summary>
	///     One value of a <see cref="SerializedNode" />: primitives, strings and math types boxed, enums as long,
	///     scene references as ulong, assets as <see cref="AssetReference" />, nested objects as
	///     <see cref="SerializedNode" />, arrays as <see cref="SerializedArray" />.
	/// </summary>
	public sealed class SerializedProperty
	{
		public string Name;
		public ValueTag Tag;
		public ValueTag ElementTag;
		public object Value;
	}

	public sealed class SerializedArray
	{
		public ValueTag ElementTag;
		public readonly List<object> Items = new List<object>();
	}

	public struct AssetReference
	{
		public string Guid;
		public string Path;
	}

	/// <summary>
	///     The binary container of scenes and snapshots ("LELB"): a string table, a type table (every type with
	///     the names and encodings of its fields, so data survives scripts gaining, losing or retyping fields and
	///     unknown types are kept verbatim), then the body the caller lays out. Field values are written by type
	///     (no boxing for primitives and math types), arrays of blittable elements as one block of bytes, nested
	///     objects with their runtime type (polymorphic fields).
	///     <code>
	///     "LELB" version:int32
	///     strings: count:int32, (len:int32, utf8)*
	///     types: count:int32, (name:string-id, fieldCount:int32, (name:string-id, tag:byte, elementTag:byte)*)*
	///     body: length:int32, bytes
	///     Object value: typeId:int32 (-1 = null), length:int32, fields in schema order
	///     Array value: count:int32 (-1 = null), elementTag:byte, elements
	///     </code>
	/// </summary>
	public static class BinaryDocument
	{
		public const int Magic = 0x424C454C; // "LELB"
		public const int Version = 1;

		/// <summary>True when the bytes start with the binary header (false for the legacy JSON scenes).</summary>
		public static bool IsBinary(byte[] data)
		{
			return data != null && data.Length >= 8 && data[0] == (byte)'L' && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'B';
		}
	}

	/// <summary>Writes one document: callers lay out the body and use the value writers for fields.</summary>
	public sealed class BinaryDocumentWriter
	{
		#region PublicFields

		public readonly ByteWriter Body;
		public readonly SerializationContext Context;

		#endregion

		#region PrivateFields

		private readonly List<string> strings = new List<string>();
		private readonly Dictionary<string, int> stringIds = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly List<SchemaType> types = new List<SchemaType>();
		private readonly Dictionary<Type, int> typeIds = new Dictionary<Type, int>();
		private readonly Dictionary<string, int> genericTypeIds = new Dictionary<string, int>(StringComparer.Ordinal);

		#endregion

		#region Constructors

		public BinaryDocumentWriter(SerializationContext context, int capacity = 16384)
		{
			Context = context;
			Body = new ByteWriter(capacity);
		}

		#endregion

		#region PublicMethods

		public int StringId(string value)
		{
			if (value == null)
			{
				return -1;
			}

			int id;
			if (!stringIds.TryGetValue(value, out id))
			{
				id = strings.Count;
				strings.Add(value);
				stringIds[value] = id;
			}

			return id;
		}

		/// <summary>The document's schema id of a type (registered with its serialized fields on first use).</summary>
		public int TypeId(Type type)
		{
			int id;
			if (typeIds.TryGetValue(type, out id))
			{
				return id;
			}

			SerializedField[] fields = SerializationUtility.GetFields(type);
			var schema = new SchemaType { Name = TypeRegistry.GetName(type), Fields = new SchemaField[fields.Length] };
			StringId(schema.Name);
			for (int i = 0; i < fields.Length; i++)
			{
				schema.Fields[i] = new SchemaField { Name = fields[i].Name, Tag = fields[i].Tag, ElementTag = fields[i].ElementTag };
				StringId(fields[i].Name);
			}

			id = types.Count;
			types.Add(schema);
			typeIds[type] = id;
			return id;
		}

		/// <summary>Writes the serialized fields of <paramref name="target" /> in the order of its type's schema.</summary>
		public void WriteFields(object target, int depth = 0)
		{
			Type type = target.GetType();
			TypeId(type);
			foreach (SerializedField field in SerializationUtility.GetFields(type))
			{
				WriteField(target, field, depth);
			}
		}

		/// <summary>An Object value: the runtime type, the block length and the fields (null: type -1).</summary>
		public void WriteObjectValue(object value, int depth)
		{
			if (value == null || depth >= SerializationUtility.MaxDepth)
			{
				Body.WriteInt32(-1);
				return;
			}

			if (value is SerializedNode node)
			{
				WriteGenericObject(node);
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

			Body.WriteInt32(TypeId(value.GetType()));
			int lengthPosition = Body.ReserveInt32();
			WriteFields(value, depth + 1);
			Body.PatchInt32(lengthPosition, Body.Length - lengthPosition - 4);
		}

		/// <summary>Writes a value of a known encoding (the generic entry used by arrays and the boxed path).</summary>
		public void WriteValue(object value, ValueTag tag, ValueTag elementTag, int depth)
		{
			switch (tag)
			{
				case ValueTag.Bool: Body.WriteBool(value != null && (bool)value); break;
				case ValueTag.SByte: Body.WriteSByte(value != null ? (sbyte)value : (sbyte)0); break;
				case ValueTag.Byte: Body.WriteByte(value != null ? (byte)value : (byte)0); break;
				case ValueTag.Int16: Body.WriteInt16(value != null ? (short)value : (short)0); break;
				case ValueTag.UInt16: Body.WriteUInt16(value != null ? (ushort)value : (ushort)0); break;
				case ValueTag.Int32: Body.WriteInt32(value != null ? (int)value : 0); break;
				case ValueTag.UInt32: Body.WriteUInt32(value != null ? (uint)value : 0u); break;
				case ValueTag.Int64: Body.WriteInt64(value != null ? (long)value : 0L); break;
				case ValueTag.UInt64: Body.WriteUInt64(value != null ? (ulong)value : 0ul); break;
				case ValueTag.Single: Body.WriteSingle(value != null ? (float)value : 0f); break;
				case ValueTag.Double: Body.WriteDouble(value != null ? (double)value : 0d); break;
				case ValueTag.Char: Body.WriteChar(value != null ? (char)value : '\0'); break;
				case ValueTag.String: Body.WriteString(value as string); break;
				case ValueTag.Vector2: Body.WriteVector2(value != null ? (Vector2)value : Vector2.Zero); break;
				case ValueTag.Vector3: Body.WriteVector3(value != null ? (Vector3)value : Vector3.Zero); break;
				case ValueTag.Vector4: Body.WriteVector4(value != null ? (Vector4)value : Vector4.Zero); break;
				case ValueTag.Quaternion: Body.WriteQuaternion(value != null ? (Quaternion)value : Quaternion.Identity); break;
				case ValueTag.Color4: Body.WriteColor4(value != null ? (Color4)value : default); break;
				case ValueTag.Vector2i: Body.WriteVector2i(value != null ? (Vector2i)value : Vector2i.Zero); break;
				case ValueTag.Vector3i: Body.WriteVector3i(value != null ? (Vector3i)value : Vector3i.Zero); break;
				case ValueTag.Vector4i: Body.WriteVector4i(value != null ? (Vector4i)value : Vector4i.Zero); break;
				case ValueTag.Enum: Body.WriteInt64(value != null ? Convert.ToInt64(value) : 0L); break;
				case ValueTag.Object: WriteObjectValue(value, depth); break;
				case ValueTag.Array: WriteArrayValue(value, elementTag, depth); break;
				case ValueTag.SceneRef: Body.WriteUInt64(ReferenceId(value)); break;
				case ValueTag.Asset: WriteAsset(value as IAsset); break;
				default: break;
			}
		}

		/// <summary>Writes the properties of an unknown-typed object exactly as they were read.</summary>
		public void WriteGenericObject(SerializedNode node)
		{
			if (node == null)
			{
				Body.WriteInt32(-1);
				return;
			}

			Body.WriteInt32(GenericTypeId(node));
			int lengthPosition = Body.ReserveInt32();
			WriteGenericFields(node);
			Body.PatchInt32(lengthPosition, Body.Length - lengthPosition - 4);
		}

		/// <summary>The property values of a node, in the order of the schema <see cref="GenericTypeId" /> registered.</summary>
		public void WriteGenericFields(SerializedNode node)
		{
			foreach (SerializedProperty property in node.Properties)
			{
				WriteValue(property.Value, property.Tag, property.ElementTag, 0);
			}
		}

		/// <summary>The whole document: header, tables and body.</summary>
		public byte[] ToArray()
		{
			var output = new ByteWriter(Body.Length + 1024);
			output.WriteInt32(BinaryDocument.Magic);
			output.WriteInt32(BinaryDocument.Version);
			output.WriteInt32(strings.Count);
			foreach (string value in strings)
			{
				output.WriteString(value);
			}

			output.WriteInt32(types.Count);
			foreach (SchemaType type in types)
			{
				output.WriteInt32(stringIds[type.Name]);
				output.WriteInt32(type.Fields.Length);
				foreach (SchemaField field in type.Fields)
				{
					output.WriteInt32(stringIds[field.Name]);
					output.WriteByte((byte)field.Tag);
					output.WriteByte((byte)field.ElementTag);
				}
			}

			output.WriteInt32(Body.Length);
			output.WriteBytes(Body.WrittenSpan);
			return output.ToArray();
		}

		#endregion

		#region PrivateMethods

		private void WriteField(object target, SerializedField field, int depth)
		{
			FieldAccessor accessor = field.Accessor;
			switch (field.Tag)
			{
				case ValueTag.Bool: Body.WriteBool(((FieldAccessor<bool>)accessor).Get(target)); return;
				case ValueTag.SByte: Body.WriteSByte(((FieldAccessor<sbyte>)accessor).Get(target)); return;
				case ValueTag.Byte: Body.WriteByte(((FieldAccessor<byte>)accessor).Get(target)); return;
				case ValueTag.Int16: Body.WriteInt16(((FieldAccessor<short>)accessor).Get(target)); return;
				case ValueTag.UInt16: Body.WriteUInt16(((FieldAccessor<ushort>)accessor).Get(target)); return;
				case ValueTag.Int32: Body.WriteInt32(((FieldAccessor<int>)accessor).Get(target)); return;
				case ValueTag.UInt32: Body.WriteUInt32(((FieldAccessor<uint>)accessor).Get(target)); return;
				case ValueTag.Int64: Body.WriteInt64(((FieldAccessor<long>)accessor).Get(target)); return;
				case ValueTag.UInt64: Body.WriteUInt64(((FieldAccessor<ulong>)accessor).Get(target)); return;
				case ValueTag.Single: Body.WriteSingle(((FieldAccessor<float>)accessor).Get(target)); return;
				case ValueTag.Double: Body.WriteDouble(((FieldAccessor<double>)accessor).Get(target)); return;
				case ValueTag.Char: Body.WriteChar(((FieldAccessor<char>)accessor).Get(target)); return;
				case ValueTag.String: Body.WriteString(((FieldAccessor<string>)accessor).Get(target)); return;
				case ValueTag.Vector2: Body.WriteVector2(((FieldAccessor<Vector2>)accessor).Get(target)); return;
				case ValueTag.Vector3: Body.WriteVector3(((FieldAccessor<Vector3>)accessor).Get(target)); return;
				case ValueTag.Vector4: Body.WriteVector4(((FieldAccessor<Vector4>)accessor).Get(target)); return;
				case ValueTag.Quaternion: Body.WriteQuaternion(((FieldAccessor<Quaternion>)accessor).Get(target)); return;
				case ValueTag.Color4: Body.WriteColor4(((FieldAccessor<Color4>)accessor).Get(target)); return;
				case ValueTag.Vector2i: Body.WriteVector2i(((FieldAccessor<Vector2i>)accessor).Get(target)); return;
				case ValueTag.Vector3i: Body.WriteVector3i(((FieldAccessor<Vector3i>)accessor).Get(target)); return;
				case ValueTag.Vector4i: Body.WriteVector4i(((FieldAccessor<Vector4i>)accessor).Get(target)); return;
				default:
					WriteValue(accessor.GetBoxed(target), field.Tag, field.ElementTag, depth);
					return;
			}
		}

		private void WriteArrayValue(object value, ValueTag elementTag, int depth)
		{
			if (value == null)
			{
				Body.WriteInt32(-1);
				return;
			}

			if (value is SerializedArray generic)
			{
				Body.WriteInt32(generic.Items.Count);
				Body.WriteByte((byte)generic.ElementTag);
				foreach (object item in generic.Items)
				{
					WriteValue(item, generic.ElementTag, ValueTag.Null, depth + 1);
				}

				return;
			}

			var list = (IList)value;
			Body.WriteInt32(list.Count);
			Body.WriteByte((byte)elementTag);
			if (list.Count == 0)
			{
				return;
			}

			if (SerializationUtility.IsBlittable(elementTag))
			{
				int size = SerializationUtility.FixedSize(elementTag);
				if (value is Array array)
				{
					Body.WriteBytes(ArrayBytes.Of(array, size));
				}
				else
				{
					BulkListAccess.Write(Body, list, SerializationUtility.GetElementType(value.GetType()));
				}

				return;
			}

			foreach (object item in list)
			{
				WriteValue(item, elementTag, ValueTag.Null, depth + 1);
			}
		}

		private ulong ReferenceId(object value)
		{
			Scene scene = Context?.Scene;
			if (value is GameObject go)
			{
				return go.scene == scene && (go.hideFlags & HideFlags.DontSave) == 0 && !go.destroyed ? go.Id : 0;
			}

			if (value is Behaviour component)
			{
				GameObject owner = component.gameObject;
				bool saved = owner != null && owner.scene == scene && (owner.hideFlags & HideFlags.DontSave) == 0 && !component.destroyed;
				return saved ? component.Id : 0;
			}

			if (value is ulong id)
			{
				return id;
			}

			return 0;
		}

		private void WriteAsset(IAsset asset)
		{
			if (asset == null)
			{
				Body.WriteInt32(-1);
				Body.WriteInt32(-1);
				return;
			}

			string path = asset.AssetPath;
			if (path == null)
			{
				// A runtime-created object (procedural mesh, code-built material) has no file to point at.
				Debug.LogWarning($"[Serialization] {Context?.Location}: {asset.GetType().Name} is not an asset file; saved as null");
				Body.WriteInt32(-1);
				Body.WriteInt32(-1);
				return;
			}

			string guid = AssetDatabase.GetGuid(path);
			if (guid == null)
			{
				Debug.LogWarning($"[Serialization] {Context?.Location}: '{path}' has no meta file; saved by path only");
			}

			Body.WriteInt32(StringId(guid));
			Body.WriteInt32(StringId(path));
		}

		/// <summary>Schema id of an unknown-typed object's data (name and the stored property layout).</summary>
		public int GenericTypeId(SerializedNode node)
		{
			var signature = new System.Text.StringBuilder(node.TypeName);
			foreach (SerializedProperty property in node.Properties)
			{
				signature.Append('|').Append(property.Name).Append(':').Append((int)property.Tag).Append(':').Append((int)property.ElementTag);
			}

			string key = signature.ToString();
			int id;
			if (genericTypeIds.TryGetValue(key, out id))
			{
				return id;
			}

			var schema = new SchemaType { Name = node.TypeName, Fields = new SchemaField[node.Properties.Count] };
			for (int i = 0; i < node.Properties.Count; i++)
			{
				SerializedProperty property = node.Properties[i];
				schema.Fields[i] = new SchemaField { Name = property.Name, Tag = property.Tag, ElementTag = property.ElementTag };
			}

			StringId(schema.Name);
			foreach (SchemaField field in schema.Fields)
			{
				StringId(field.Name);
			}

			id = types.Count;
			types.Add(schema);
			genericTypeIds[key] = id;
			return id;
		}

		#endregion
	}

	/// <summary>Reads one document written by <see cref="BinaryDocumentWriter" />.</summary>
	public sealed class BinaryDocumentReader
	{
		#region PublicFields

		public readonly ByteReader Body;
		public readonly SerializationContext Context;
		public int TypeCount => types.Length;

		#endregion

		#region PrivateFields

		private readonly string[] strings;
		private readonly SchemaType[] types;
		private readonly Type[] resolvedTypes;
		private readonly bool[] resolveTried;
		private readonly Dictionary<Type, SerializedField[]>[] plans;

		#endregion

		#region Constructors

		public BinaryDocumentReader(byte[] data, SerializationContext context)
		{
			Context = context;
			var header = new ByteReader(data);
			if (header.ReadInt32() != BinaryDocument.Magic)
			{
				throw new InvalidOperationException("Not a LELEngine binary document");
			}

			int version = header.ReadInt32();
			if (version > BinaryDocument.Version)
			{
				throw new InvalidOperationException($"Document version {version} is newer than this engine supports ({BinaryDocument.Version})");
			}

			strings = new string[header.ReadInt32()];
			for (int i = 0; i < strings.Length; i++)
			{
				strings[i] = header.ReadString();
			}

			types = new SchemaType[header.ReadInt32()];
			for (int i = 0; i < types.Length; i++)
			{
				var type = new SchemaType { Name = String(header.ReadInt32()), Fields = new SchemaField[header.ReadInt32()] };
				for (int f = 0; f < type.Fields.Length; f++)
				{
					type.Fields[f] = new SchemaField { Name = String(header.ReadInt32()), Tag = (ValueTag)header.ReadByte(), ElementTag = (ValueTag)header.ReadByte() };
				}

				types[i] = type;
			}

			int bodyLength = header.ReadInt32();
			Body = new ByteReader(data, header.Position, bodyLength);
			resolvedTypes = new Type[types.Length];
			resolveTried = new bool[types.Length];
			plans = new Dictionary<Type, SerializedField[]>[types.Length];
		}

		#endregion

		#region PublicMethods

		public string String(int id)
		{
			return id >= 0 && id < strings.Length ? strings[id] : null;
		}

		public SchemaType Schema(int typeId)
		{
			return typeId >= 0 && typeId < types.Length ? types[typeId] : null;
		}

		/// <summary>The live type behind a schema, or null when it does not exist (a missing script).</summary>
		public Type ResolveType(int typeId)
		{
			if (typeId < 0 || typeId >= types.Length)
			{
				return null;
			}

			if (!resolveTried[typeId])
			{
				resolveTried[typeId] = true;
				resolvedTypes[typeId] = TypeRegistry.Resolve(types[typeId].Name);
			}

			return resolvedTypes[typeId];
		}

		/// <summary>
		///     Reads a block "length, fields" of schema <paramref name="typeId" /> into <paramref name="target" />:
		///     fields the target's type lacks are skipped, fields it gained keep their values, retyped fields are
		///     converted when possible.
		/// </summary>
		public void ReadFieldBlock(object target, int typeId, int depth = 0)
		{
			int length = Body.ReadInt32();
			int end = Body.Position + length;
			try
			{
				ReadFields(target, typeId, depth);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Serialization] {Context?.Location}: {e.Message}; the remaining fields keep their defaults");
			}

			Body.Position = end;
		}

		/// <summary>Reads a block "length, fields" into a <see cref="SerializedNode" /> (unknown type).</summary>
		public SerializedNode ReadGenericBlock(int typeId)
		{
			int length = Body.ReadInt32();
			int end = Body.Position + length;
			var node = new SerializedNode { TypeName = types[typeId].Name };
			try
			{
				foreach (SchemaField field in types[typeId].Fields)
				{
					node.Properties.Add(new SerializedProperty { Name = field.Name, Tag = field.Tag, ElementTag = field.ElementTag, Value = ReadBoxed(field.Tag, field.ElementTag, null, null, 0) });
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Serialization] {Context?.Location}: {e.Message} (missing script data truncated)");
			}

			Body.Position = end;
			return node;
		}

		/// <summary>An Object value (type id, length, fields) into <paramref name="current" /> or a new instance.</summary>
		public object ReadObjectValue(Type declaredType, object current, int depth)
		{
			int typeId = Body.ReadInt32();
			if (typeId < 0)
			{
				return declaredType != null && declaredType.IsValueType ? current ?? Activator.CreateInstance(declaredType) : null;
			}

			int length = Body.ReadInt32();
			int end = Body.Position + length;
			if (declaredType == null)
			{
				// Generic read (no live type to fill).
				Body.Position -= 4;
				return ReadGenericBlock(typeId);
			}

			Type type = ResolveType(typeId);
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
					Debug.LogWarning($"[Serialization] {Context?.Location}: type '{types[typeId].Name}' not found; value dropped");
				}

				Body.Position = end;
				return current;
			}

			object target = current != null && current.GetType() == type ? current : TryCreate(type);
			if (target == null)
			{
				Body.Position = end;
				return current;
			}

			try
			{
				ReadFields(target, typeId, depth + 1);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Serialization] {Context?.Location}: {e.Message}; the remaining fields of {type.Name} keep their defaults");
			}

			Body.Position = end;
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
		///     Reads any value as a boxed object. With <paramref name="targetType" /> null (generic reads) nested
		///     objects become <see cref="SerializedNode" /> and arrays <see cref="SerializedArray" />.
		/// </summary>
		public object ReadBoxed(ValueTag tag, ValueTag elementTag, Type targetType, object current, int depth)
		{
			switch (tag)
			{
				case ValueTag.Bool: return Body.ReadBool();
				case ValueTag.SByte: return Body.ReadSByte();
				case ValueTag.Byte: return Body.ReadByte();
				case ValueTag.Int16: return Body.ReadInt16();
				case ValueTag.UInt16: return Body.ReadUInt16();
				case ValueTag.Int32: return Body.ReadInt32();
				case ValueTag.UInt32: return Body.ReadUInt32();
				case ValueTag.Int64: return Body.ReadInt64();
				case ValueTag.UInt64: return Body.ReadUInt64();
				case ValueTag.Single: return Body.ReadSingle();
				case ValueTag.Double: return Body.ReadDouble();
				case ValueTag.Char: return Body.ReadChar();
				case ValueTag.String: return Body.ReadString();
				case ValueTag.Vector2: return Body.ReadVector2();
				case ValueTag.Vector3: return Body.ReadVector3();
				case ValueTag.Vector4: return Body.ReadVector4();
				case ValueTag.Quaternion: return Body.ReadQuaternion();
				case ValueTag.Color4: return Body.ReadColor4();
				case ValueTag.Vector2i: return Body.ReadVector2i();
				case ValueTag.Vector3i: return Body.ReadVector3i();
				case ValueTag.Vector4i: return Body.ReadVector4i();
				case ValueTag.Enum:
				{
					long value = Body.ReadInt64();
					return targetType != null && targetType.IsEnum ? Enum.ToObject(targetType, value) : value;
				}
				case ValueTag.Object:
					return ReadObjectValue(targetType, current, depth);
				case ValueTag.Array:
					return ReadArrayValue(elementTag, targetType, current, depth);
				case ValueTag.SceneRef:
				{
					ulong id = Body.ReadUInt64();
					return targetType != null ? ResolveReference(id, targetType) : id;
				}
				case ValueTag.Asset:
				{
					var reference = new AssetReference { Guid = String(Body.ReadInt32()), Path = String(Body.ReadInt32()) };
					return targetType != null ? LoadAsset(reference, targetType) : reference;
				}
				default:
					return null;
			}
		}

		/// <summary>Advances past a value without decoding it.</summary>
		public void SkipValue(ValueTag tag, ValueTag elementTag)
		{
			int fixedSize = SerializationUtility.FixedSize(tag);
			if (fixedSize > 0)
			{
				Body.Skip(fixedSize);
				return;
			}

			switch (tag)
			{
				case ValueTag.String:
				{
					int bytes = Body.ReadInt32();
					if (bytes > 0)
					{
						Body.Skip(bytes);
					}

					return;
				}
				case ValueTag.Asset:
					Body.Skip(8);
					return;
				case ValueTag.Object:
				{
					int typeId = Body.ReadInt32();
					if (typeId >= 0)
					{
						Body.Skip(Body.ReadInt32());
					}

					return;
				}
				case ValueTag.Array:
				{
					int count = Body.ReadInt32();
					if (count < 0)
					{
						return;
					}

					ValueTag stored = (ValueTag)Body.ReadByte();
					int elementSize = stored == ValueTag.Asset ? 8 : SerializationUtility.FixedSize(stored);
					if (elementSize > 0)
					{
						Body.Skip(count * elementSize);
						return;
					}

					for (int i = 0; i < count; i++)
					{
						SkipValue(stored, ValueTag.Null);
					}

					return;
				}
				default:
					return;
			}
		}

		#endregion

		#region PrivateMethods

		private void ReadFields(object target, int typeId, int depth)
		{
			SchemaType schema = types[typeId];
			SerializedField[] plan = Plan(typeId, target.GetType());
			for (int i = 0; i < schema.Fields.Length; i++)
			{
				SchemaField stored = schema.Fields[i];
				SerializedField field = plan[i];
				if (field == null)
				{
					SkipValue(stored.Tag, stored.ElementTag);
					continue;
				}

				if (field.Tag == stored.Tag && (stored.Tag != ValueTag.Array || field.ElementTag == stored.ElementTag))
				{
					ReadField(target, field, depth);
					continue;
				}

				// The field changed type since the data was written: convert what converts, drop the rest.
				object boxed = ReadBoxed(stored.Tag, stored.ElementTag, null, null, depth);
				object converted;
				if (TryConvert(boxed, stored.Tag, field.FieldType, out converted))
				{
					field.Accessor.SetBoxed(target, converted);
				}
				else
				{
					Debug.LogWarning($"[Serialization] {Context?.Location}.{field.Name}: cannot read {stored.Tag} as {field.FieldType.Name}; keeping the default");
				}
			}
		}

		private void ReadField(object target, SerializedField field, int depth)
		{
			FieldAccessor accessor = field.Accessor;
			switch (field.Tag)
			{
				case ValueTag.Bool: ((FieldAccessor<bool>)accessor).Set(target, Body.ReadBool()); return;
				case ValueTag.SByte: ((FieldAccessor<sbyte>)accessor).Set(target, Body.ReadSByte()); return;
				case ValueTag.Byte: ((FieldAccessor<byte>)accessor).Set(target, Body.ReadByte()); return;
				case ValueTag.Int16: ((FieldAccessor<short>)accessor).Set(target, Body.ReadInt16()); return;
				case ValueTag.UInt16: ((FieldAccessor<ushort>)accessor).Set(target, Body.ReadUInt16()); return;
				case ValueTag.Int32: ((FieldAccessor<int>)accessor).Set(target, Body.ReadInt32()); return;
				case ValueTag.UInt32: ((FieldAccessor<uint>)accessor).Set(target, Body.ReadUInt32()); return;
				case ValueTag.Int64: ((FieldAccessor<long>)accessor).Set(target, Body.ReadInt64()); return;
				case ValueTag.UInt64: ((FieldAccessor<ulong>)accessor).Set(target, Body.ReadUInt64()); return;
				case ValueTag.Single: ((FieldAccessor<float>)accessor).Set(target, Body.ReadSingle()); return;
				case ValueTag.Double: ((FieldAccessor<double>)accessor).Set(target, Body.ReadDouble()); return;
				case ValueTag.Char: ((FieldAccessor<char>)accessor).Set(target, Body.ReadChar()); return;
				case ValueTag.String: ((FieldAccessor<string>)accessor).Set(target, Body.ReadString()); return;
				case ValueTag.Vector2: ((FieldAccessor<Vector2>)accessor).Set(target, Body.ReadVector2()); return;
				case ValueTag.Vector3: ((FieldAccessor<Vector3>)accessor).Set(target, Body.ReadVector3()); return;
				case ValueTag.Vector4: ((FieldAccessor<Vector4>)accessor).Set(target, Body.ReadVector4()); return;
				case ValueTag.Quaternion: ((FieldAccessor<Quaternion>)accessor).Set(target, Body.ReadQuaternion()); return;
				case ValueTag.Color4: ((FieldAccessor<Color4>)accessor).Set(target, Body.ReadColor4()); return;
				case ValueTag.Vector2i: ((FieldAccessor<Vector2i>)accessor).Set(target, Body.ReadVector2i()); return;
				case ValueTag.Vector3i: ((FieldAccessor<Vector3i>)accessor).Set(target, Body.ReadVector3i()); return;
				case ValueTag.Vector4i: ((FieldAccessor<Vector4i>)accessor).Set(target, Body.ReadVector4i()); return;
				case ValueTag.Enum: accessor.SetBoxed(target, Enum.ToObject(field.FieldType, Body.ReadInt64())); return;
				case ValueTag.Object:
				case ValueTag.Array:
				{
					object current = accessor.GetBoxed(target);
					object value = ReadBoxed(field.Tag, field.ElementTag, field.FieldType, current, depth);
					if (!ReferenceEquals(value, current) || field.FieldType.IsValueType)
					{
						accessor.SetBoxed(target, value);
					}

					return;
				}
				case ValueTag.SceneRef: accessor.SetBoxed(target, ResolveReference(Body.ReadUInt64(), field.FieldType)); return;
				case ValueTag.Asset: accessor.SetBoxed(target, LoadAsset(new AssetReference { Guid = String(Body.ReadInt32()), Path = String(Body.ReadInt32()) }, field.FieldType)); return;
				default: return;
			}
		}

		private object ReadArrayValue(ValueTag elementTag, Type targetType, object current, int depth)
		{
			int count = Body.ReadInt32();
			if (count < 0)
			{
				return null;
			}

			ValueTag stored = (ValueTag)Body.ReadByte();
			if (targetType == null)
			{
				var generic = new SerializedArray { ElementTag = stored };
				for (int i = 0; i < count; i++)
				{
					generic.Items.Add(ReadBoxed(stored, ValueTag.Null, null, null, depth + 1));
				}

				return generic;
			}

			Type elementType = SerializationUtility.GetElementType(targetType);
			ValueTag wanted = SerializationUtility.TagOf(elementType, out _);
			bool isArray = targetType.IsArray;
			if (stored != wanted)
			{
				// Element type changed: convert element by element.
				IList converted = isArray ? Array.CreateInstance(elementType, count) : ReuseList(targetType, current, count);
				for (int i = 0; i < count; i++)
				{
					object boxed = ReadBoxed(stored, ValueTag.Null, null, null, depth + 1);
					object item;
					if (!TryConvert(boxed, stored, elementType, out item))
					{
						item = elementType.IsValueType ? Activator.CreateInstance(elementType) : null;
					}

					if (isArray)
					{
						converted[i] = item;
					}
					else
					{
						converted.Add(item);
					}
				}

				return converted;
			}

			if (SerializationUtility.IsBlittable(stored))
			{
				int size = SerializationUtility.FixedSize(stored);
				if (isArray)
				{
					Array array = Array.CreateInstance(elementType, count);
					Body.ReadBytes(count * size).CopyTo(ArrayBytes.Of(array, size));
					return array;
				}

				IList list = ReuseList(targetType, current, count);
				BulkListAccess.Read(Body, list, elementType, count);
				return list;
			}

			if (isArray)
			{
				Array array = Array.CreateInstance(elementType, count);
				for (int i = 0; i < count; i++)
				{
					array.SetValue(ReadBoxed(stored, ValueTag.Null, elementType, null, depth + 1), i);
				}

				return array;
			}

			IList result = ReuseList(targetType, current, count);
			for (int i = 0; i < count; i++)
			{
				result.Add(ReadBoxed(stored, ValueTag.Null, elementType, null, depth + 1));
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

		private SerializedField[] Plan(int typeId, Type type)
		{
			Dictionary<Type, SerializedField[]> perType = plans[typeId] ??= new Dictionary<Type, SerializedField[]>();
			SerializedField[] plan;
			if (perType.TryGetValue(type, out plan))
			{
				return plan;
			}

			SchemaField[] schema = types[typeId].Fields;
			SerializedField[] fields = SerializationUtility.GetFields(type);
			plan = new SerializedField[schema.Length];
			for (int i = 0; i < schema.Length; i++)
			{
				foreach (SerializedField field in fields)
				{
					if (field.Name == schema[i].Name)
					{
						plan[i] = field;
						break;
					}
				}
			}

			perType[type] = plan;
			return plan;
		}

		private object ResolveReference(ulong id, Type type)
		{
			if (id == 0 || Context == null)
			{
				return null;
			}

			object target;
			if (!Context.Objects.TryGetValue(id, out target))
			{
				Debug.LogWarning($"[Serialization] {Context.Location}: reference to missing object {id}");
				return null;
			}

			// A component field may point at the GameObject's component of that type.
			if (target is GameObject go && type != typeof(GameObject))
			{
				return go.GetComponent(type);
			}

			return type.IsInstanceOfType(target) ? target : null;
		}

		private static object LoadAsset(AssetReference reference, Type type)
		{
			if (reference.Guid == null && reference.Path == null)
			{
				return null;
			}

			return reference.Guid != null ? AssetDatabase.LoadByGuid(type, reference.Guid, reference.Path) : AssetDatabase.Load(type, AssetDatabase.Resolve(reference.Path, null));
		}

		private static object TryCreate(Type type)
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

		/// <summary>Conversions between encodings of a field that changed type (numbers, enums, number arrays to vectors).</summary>
		private bool TryConvert(object value, ValueTag storedTag, Type targetType, out object result)
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

					if (IsNumber(storedTag))
					{
						result = Enum.ToObject(targetType, Convert.ToInt64(value));
						return true;
					}

					return false;
				}

				if (IsNumber(wanted) || wanted == ValueTag.Bool || wanted == ValueTag.Char || wanted == ValueTag.String)
				{
					if (storedTag == ValueTag.Enum)
					{
						value = (long)value;
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
						f[i] = Convert.ToSingle(numbers.Items[i]);
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
						if (!TryConvert(items.Items[i], items.ElementTag, elementType, out item))
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
					result = ResolveReference(id, targetType);
					return true;
				}

				if (wanted == ValueTag.Asset && value is AssetReference asset)
				{
					result = LoadAsset(asset, targetType);
					return true;
				}

				if (wanted == ValueTag.Object && value is SerializedNode node)
				{
					result = FromNode(node, targetType);
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
		private object FromNode(SerializedNode node, Type declaredType)
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
				if (property != null && TryConvert(property.Value, property.Tag, field.FieldType, out converted))
				{
					field.Accessor.SetBoxed(target, converted);
				}
			}

			return target;
		}

		private static bool IsNumber(ValueTag tag)
		{
			return tag >= ValueTag.SByte && tag <= ValueTag.Double;
		}

		#endregion
	}

	/// <summary>Bulk copies between List&lt;T&gt; of unmanaged elements and the byte buffers (generic helpers cached per element type).</summary>
	internal static class BulkListAccess
	{
		private static readonly Dictionary<Type, Action<ByteWriter, IList>> writers = new Dictionary<Type, Action<ByteWriter, IList>>();
		private static readonly Dictionary<Type, Action<ByteReader, IList, int>> readers = new Dictionary<Type, Action<ByteReader, IList, int>>();
		private static readonly object sync = new object();

		public static void Write(ByteWriter writer, IList list, Type elementType)
		{
			Action<ByteWriter, IList> action;
			lock (sync)
			{
				if (!writers.TryGetValue(elementType, out action))
				{
					MethodInfo method = typeof(BulkListAccess).GetMethod(nameof(WriteList), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(elementType);
					action = (Action<ByteWriter, IList>)Delegate.CreateDelegate(typeof(Action<ByteWriter, IList>), method);
					writers[elementType] = action;
				}
			}

			action(writer, list);
		}

		public static void Read(ByteReader reader, IList list, Type elementType, int count)
		{
			Action<ByteReader, IList, int> action;
			lock (sync)
			{
				if (!readers.TryGetValue(elementType, out action))
				{
					MethodInfo method = typeof(BulkListAccess).GetMethod(nameof(ReadList), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(elementType);
					action = (Action<ByteReader, IList, int>)Delegate.CreateDelegate(typeof(Action<ByteReader, IList, int>), method);
					readers[elementType] = action;
				}
			}

			action(reader, list, count);
		}

		/// <summary>Drops the cached delegates (they could hold types of an unloaded game assembly).</summary>
		public static void Clear()
		{
			lock (sync)
			{
				writers.Clear();
				readers.Clear();
			}
		}

		private static void WriteList<T>(ByteWriter writer, IList list)
			where T : unmanaged
		{
			writer.WriteBytes(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan((List<T>)list)));
		}

		private static void ReadList<T>(ByteReader reader, IList list, int count)
			where T : unmanaged
		{
			var typed = (List<T>)list;
			CollectionsMarshal.SetCount(typed, count);
			reader.ReadBytes(count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()).CopyTo(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(typed)));
		}
	}
}
