using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using OpenTK.Mathematics;

namespace LELEngine.Serialization
{
	/// <summary>Receives notifications around (de)serialization, e.g. to rebuild derived state.</summary>
	public interface ISerializationCallbackReceiver
	{
		void OnBeforeSerialize();
		void OnAfterDeserialize();
	}

	/// <summary>
	///     How a value is encoded in the binary format (see <see cref="BinaryDocument" />). Written with every field
	///     of a type's schema, so a reader can skip or convert values whose field changed type or disappeared.
	/// </summary>
	public enum ValueTag : byte
	{
		Null = 0,
		Bool,
		SByte,
		Byte,
		Int16,
		UInt16,
		Int32,
		UInt32,
		Int64,
		UInt64,
		Single,
		Double,
		Char,
		String,
		Vector2,
		Vector3,
		Vector4,
		Quaternion,
		Color4,
		Vector2i,
		Vector3i,
		Vector4i,

		/// <summary>Enum, stored as its 64-bit value.</summary>
		Enum,

		/// <summary>
		///     [Serializable] class or struct, stored by value with its runtime type: a field declared as a base class or
		///     interface holds any serializable subtype. A slot declared as an interface or <see cref="EngineObject" />
		///     may instead hold a reference: a component or GameObject of the same scene, or a ScriptableObject asset
		///     (see <see cref="BinaryDocument" /> for the encodings).
		/// </summary>
		Object,

		/// <summary>One-dimensional array or List&lt;T&gt; (element tag follows; elements may be collections themselves).</summary>
		Array,

		/// <summary>GameObject or component of the same scene, by id.</summary>
		SceneRef,

		/// <summary>Asset file (<see cref="IAsset" />) by GUID and path.</summary>
		Asset,

		/// <summary>Dictionary&lt;K, V&gt;: the key and value encodings are stored with every value (the schema records the value's).</summary>
		Dictionary
	}

	/// <summary>
	///     Which fields are serialized (Unity rules): instance fields that are public or marked
	///     <see cref="SerializeField" />, not <see cref="NonSerializedAttribute" />, not readonly, of a supported type.
	///     Supported: primitives, string, enums, OpenTK vectors / quaternion / Color4, one-dimensional arrays and
	///     List&lt;T&gt; of supported element types (collections of collections too), Dictionary&lt;K, V&gt; with keys of
	///     a primitive, string, enum, vector, reference or asset type and any supported value type, [Serializable]
	///     classes and structs (nested), references to GameObjects and components of the same scene, and assets
	///     (<see cref="IAsset" />, ScriptableObjects included).
	///     <para>
	///         Polymorphism: a field declared as an abstract class or an interface holds an instance of any class that
	///         derives from or implements it, stored by value with its runtime type. A field declared as an interface (or
	///         as <see cref="EngineObject" />) may also hold a reference: a component or GameObject of the same scene, or
	///         a ScriptableObject asset implementing it.
	///     </para>
	///
	///     The per-type field cache (with compiled field accessors) holds game types; <see cref="ClearCache" /> must
	///     run before a game assembly is unloaded.
	/// </summary>
	public static class SerializationUtility
	{
		#region PublicFields

		/// <summary>Maximum nesting of [Serializable] objects (guards against reference cycles).</summary>
		public const int MaxDepth = 10;

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly Dictionary<Type, SerializedField[]> cache = new Dictionary<Type, SerializedField[]>();

		#endregion

		#region PublicMethods

		/// <summary>Serialized fields of a component or [Serializable] type, base class fields first.</summary>
		public static SerializedField[] GetFields(Type type)
		{
			lock (sync)
			{
				SerializedField[] fields;
				if (!cache.TryGetValue(type, out fields))
				{
					fields = Collect(type);
					cache[type] = fields;
				}

				return fields;
			}
		}

		/// <summary>Drops every cached type (call before unloading game assemblies).</summary>
		public static void ClearCache()
		{
			lock (sync)
			{
				cache.Clear();
			}

			BulkListAccess.Clear();
			GenericValues.ClearCache();
			UIElements.UxmlRegistry.Clear();
			PhysicsCallbacks.ClearCache();
		}

		public static bool IsSupported(Type type)
		{
			return IsSupported(type, 0);
		}

		public static bool IsReferenceType(Type type)
		{
			return type == typeof(GameObject) || typeof(Behaviour).IsAssignableFrom(type);
		}

		public static bool IsAssetType(Type type)
		{
			return typeof(IAsset).IsAssignableFrom(type);
		}

		/// <summary>A [Serializable] class or struct stored by value (nested object), or a polymorphic slot.</summary>
		public static bool IsObjectType(Type type)
		{
			return TagOf(type, out _) == ValueTag.Object;
		}

		/// <summary>
		///     An Object slot that may hold a reference instead of an inline instance: an interface (a component or a
		///     ScriptableObject implementing it) or <see cref="EngineObject" /> itself (any GameObject, component or
		///     ScriptableObject).
		/// </summary>
		public static bool CanHoldReference(Type declaredType)
		{
			return declaredType != null && (declaredType.IsInterface || declaredType == typeof(EngineObject));
		}

		/// <summary>Element type of an array or List&lt;T&gt;, else null.</summary>
		public static Type GetElementType(Type type)
		{
			if (type.IsArray)
			{
				return type.GetArrayRank() == 1 ? type.GetElementType() : null;
			}

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				return type.GetGenericArguments()[0];
			}

			return null;
		}

		/// <summary>Key and value types of a Dictionary&lt;K, V&gt;; false for other types.</summary>
		public static bool GetDictionaryTypes(Type type, out Type keyType, out Type valueType)
		{
			if (type != null && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
			{
				Type[] arguments = type.GetGenericArguments();
				keyType = arguments[0];
				valueType = arguments[1];
				return true;
			}

			keyType = null;
			valueType = null;
			return false;
		}

		public static bool IsDictionaryType(Type type)
		{
			return GetDictionaryTypes(type, out _, out _);
		}

		/// <summary>
		///     Types a dictionary key may have: primitives, strings, enums, vectors, references and assets (values
		///     with a stable identity; inline objects and collections are left out).
		/// </summary>
		public static bool IsSupportedKey(Type type)
		{
			if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
			{
				return type != typeof(IntPtr) && type != typeof(UIntPtr);
			}

			return IsMathType(type) || IsReferenceType(type) || IsAssetType(type);
		}

		/// <summary>OpenTK vectors, quaternion and Color4 (stored as fixed-size values).</summary>
		public static bool IsMathType(Type type)
		{
			return type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion)
				|| type == typeof(Color4) || type == typeof(Vector2i) || type == typeof(Vector3i) || type == typeof(Vector4i);
		}

		/// <summary>The encoding of a dictionary type's keys (Null for other types).</summary>
		public static ValueTag KeyTagOf(Type dictionaryType)
		{
			return GetDictionaryTypes(dictionaryType, out Type keyType, out _) ? TagOf(keyType, out _) : ValueTag.Null;
		}

		/// <summary>
		///     The encoding to write a collection's elements with when the caller does not know it (an element that is
		///     itself a collection): taken from the runtime type of the collection.
		/// </summary>
		public static ValueTag ElementTagOf(object collection)
		{
			if (collection is SerializedArray generic)
			{
				return generic.ElementTag;
			}

			Type element = collection != null ? GetElementType(collection.GetType()) : null;
			return element != null ? TagOf(element, out _) : ValueTag.Null;
		}

		/// <summary>The encoding of values of a type; for arrays and lists also the element encoding.</summary>
		public static ValueTag TagOf(Type type, out ValueTag element)
		{
			element = ValueTag.Null;
			if (type.IsEnum)
			{
				return ValueTag.Enum;
			}

			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Boolean: return ValueTag.Bool;
				case TypeCode.SByte: return ValueTag.SByte;
				case TypeCode.Byte: return ValueTag.Byte;
				case TypeCode.Int16: return ValueTag.Int16;
				case TypeCode.UInt16: return ValueTag.UInt16;
				case TypeCode.Int32: return ValueTag.Int32;
				case TypeCode.UInt32: return ValueTag.UInt32;
				case TypeCode.Int64: return ValueTag.Int64;
				case TypeCode.UInt64: return ValueTag.UInt64;
				case TypeCode.Single: return ValueTag.Single;
				case TypeCode.Double: return ValueTag.Double;
				case TypeCode.Char: return ValueTag.Char;
				case TypeCode.String: return ValueTag.String;
			}

			if (type == typeof(Vector2)) return ValueTag.Vector2;
			if (type == typeof(Vector3)) return ValueTag.Vector3;
			if (type == typeof(Vector4)) return ValueTag.Vector4;
			if (type == typeof(Quaternion)) return ValueTag.Quaternion;
			if (type == typeof(Color4)) return ValueTag.Color4;
			if (type == typeof(Vector2i)) return ValueTag.Vector2i;
			if (type == typeof(Vector3i)) return ValueTag.Vector3i;
			if (type == typeof(Vector4i)) return ValueTag.Vector4i;

			if (IsReferenceType(type))
			{
				return ValueTag.SceneRef;
			}

			if (IsAssetType(type))
			{
				return ValueTag.Asset;
			}

			Type elementType = GetElementType(type);
			if (elementType != null)
			{
				element = TagOf(elementType, out _);
				return ValueTag.Array;
			}

			if (GetDictionaryTypes(type, out _, out Type valueType))
			{
				element = TagOf(valueType, out _);
				return ValueTag.Dictionary;
			}

			return ValueTag.Object;
		}

		/// <summary>Size in bytes of a fixed-size value, 0 for variable-size tags.</summary>
		public static int FixedSize(ValueTag tag)
		{
			switch (tag)
			{
				case ValueTag.Bool:
				case ValueTag.SByte:
				case ValueTag.Byte:
					return 1;
				case ValueTag.Int16:
				case ValueTag.UInt16:
				case ValueTag.Char:
					return 2;
				case ValueTag.Int32:
				case ValueTag.UInt32:
				case ValueTag.Single:
					return 4;
				case ValueTag.Int64:
				case ValueTag.UInt64:
				case ValueTag.Double:
				case ValueTag.Enum:
				case ValueTag.Vector2:
				case ValueTag.Vector2i:
				case ValueTag.SceneRef:
					return 8;
				case ValueTag.Vector3:
				case ValueTag.Vector3i:
					return 12;
				case ValueTag.Vector4:
				case ValueTag.Vector4i:
				case ValueTag.Quaternion:
				case ValueTag.Color4:
					return 16;
				default:
					return 0;
			}
		}

		/// <summary>Fixed-size values whose memory layout is the file layout (bulk-copied arrays).</summary>
		public static bool IsBlittable(ValueTag tag)
		{
			return tag != ValueTag.Enum && tag != ValueTag.SceneRef && FixedSize(tag) > 0;
		}

		/// <summary>
		///     The encoding of a field as written in a JSON type table: "Single", "Array&lt;Vector3&gt;",
		///     "Enum(Standard=0,Lumen=2)", "Array&lt;Enum(A=1,B=2)&gt;", "Dictionary&lt;String,Array&lt;Int32&gt;&gt;".
		/// </summary>
		public static string TagSpec(ValueTag tag, ValueTag elementTag, KeyValuePair<string, long>[] enumMembers)
		{
			return TagSpec(new SchemaField { Tag = tag, ElementTag = elementTag, EnumMembers = enumMembers });
		}

		/// <summary>The type-table text of a schema field, nested specs of collections included.</summary>
		public static string TagSpec(SchemaField field)
		{
			var builder = new System.Text.StringBuilder();
			AppendTagSpec(builder, field);
			return builder.ToString();
		}

		/// <summary>Parses a <see cref="TagSpec(ValueTag, ValueTag, KeyValuePair{string, long}[])" /> string (the outer level).</summary>
		public static bool TryParseTagSpec(string spec, out ValueTag tag, out ValueTag elementTag, out KeyValuePair<string, long>[] enumMembers)
		{
			bool parsed = TryParseTagSpec(spec, out SchemaField field);
			tag = parsed ? field.Tag : ValueTag.Null;
			elementTag = parsed ? field.ElementTag : ValueTag.Null;
			enumMembers = parsed ? field.EnumMembers : null;
			return parsed;
		}

		/// <summary>Parses a type-table spec with its nested collection specs ("Dictionary&lt;Enum(A=0),Array&lt;Vector3&gt;&gt;").</summary>
		public static bool TryParseTagSpec(string spec, out SchemaField field)
		{
			field = null;
			if (string.IsNullOrWhiteSpace(spec))
			{
				return false;
			}

			int position = 0;
			field = ParseTagSpec(spec, ref position, 0);
			return field != null;
		}

		/// <summary>The spec of a collection's elements (a dictionary's values): the nested spec when recorded, else the element tag.</summary>
		public static SchemaField ElementSpec(SchemaField field)
		{
			return field.Element ?? new SchemaField { Tag = field.ElementTag, EnumMembers = field.ElementTag == ValueTag.Enum ? field.EnumMembers : null };
		}

		/// <summary>The spec of a dictionary's keys.</summary>
		public static SchemaField KeySpec(SchemaField field)
		{
			return field.Key ?? new SchemaField { Tag = field.KeyTag };
		}

		/// <summary>The schema entry of a field of this type, with nested specs of collections and enum members.</summary>
		public static SchemaField SchemaOf(string name, Type type, int depth = 0)
		{
			var field = new SchemaField { Name = name, Tag = TagOf(type, out ValueTag element), ElementTag = element };
			if (depth > MaxDepth)
			{
				return field;
			}

			switch (field.Tag)
			{
				case ValueTag.Enum:
					field.EnumMembers = DocumentValues.EnumMembers(type);
					break;
				case ValueTag.Array:
					SetElementSpec(field, SchemaOf(null, GetElementType(type), depth + 1));
					break;
				case ValueTag.Dictionary:
					GetDictionaryTypes(type, out Type keyType, out Type valueType);
					field.Key = SchemaOf(null, keyType, depth + 1);
					field.KeyTag = field.Key.Tag;
					SetElementSpec(field, SchemaOf(null, valueType, depth + 1));
					break;
			}

			return field;
		}

		/// <summary>
		///     Sets a collection's element spec: the element tag always, the nested spec when it says more than the tag
		///     (a collection, enum members); an enum element's members also on the field (the format before nesting).
		/// </summary>
		public static void SetElementSpec(SchemaField field, SchemaField element)
		{
			field.ElementTag = element.Tag;
			if (element.Tag == ValueTag.Enum && field.EnumMembers == null)
			{
				field.EnumMembers = element.EnumMembers;
			}

			if (element.Tag == ValueTag.Array || element.Tag == ValueTag.Dictionary || element.EnumMembers != null)
			{
				field.Element = element;
			}
		}

		private static void AppendTagSpec(System.Text.StringBuilder builder, SchemaField field)
		{
			switch (field.Tag)
			{
				case ValueTag.Array:
					builder.Append("Array<");
					AppendTagSpec(builder, ElementSpec(field));
					builder.Append('>');
					return;
				case ValueTag.Dictionary:
					builder.Append("Dictionary<");
					AppendTagSpec(builder, KeySpec(field));
					builder.Append(',');
					AppendTagSpec(builder, ElementSpec(field));
					builder.Append('>');
					return;
				default:
					builder.Append(SingleTagSpec(field.Tag, field.Tag == ValueTag.Enum ? field.EnumMembers : null));
					return;
			}
		}

		private static SchemaField ParseTagSpec(string spec, ref int position, int depth)
		{
			if (depth > MaxDepth)
			{
				return null;
			}

			SkipSpaces(spec, ref position);
			int start = position;
			while (position < spec.Length && char.IsLetterOrDigit(spec[position]))
			{
				position++;
			}

			ValueTag tag;
			if (position == start || !Enum.TryParse(spec.Substring(start, position - start), true, out tag))
			{
				return null;
			}

			var field = new SchemaField { Tag = tag };
			SkipSpaces(spec, ref position);
			if (position < spec.Length && spec[position] == '(')
			{
				int close = spec.IndexOf(')', position);
				if (close < 0)
				{
					return null;
				}

				field.EnumMembers = ParseEnumMembers(spec.Substring(position + 1, close - position - 1));
				position = close + 1;
				SkipSpaces(spec, ref position);
			}

			if (position < spec.Length && spec[position] == '<')
			{
				position++;
				SchemaField first = ParseTagSpec(spec, ref position, depth + 1);
				if (first == null)
				{
					return null;
				}

				SchemaField second = null;
				SkipSpaces(spec, ref position);
				if (position < spec.Length && spec[position] == ',')
				{
					position++;
					second = ParseTagSpec(spec, ref position, depth + 1);
					if (second == null)
					{
						return null;
					}

					SkipSpaces(spec, ref position);
				}

				if (position >= spec.Length || spec[position] != '>')
				{
					return null;
				}

				position++;
				if (tag == ValueTag.Dictionary && second != null)
				{
					field.Key = first;
					field.KeyTag = first.Tag;
					SetElementSpec(field, second);
				}
				else
				{
					SetElementSpec(field, first);
				}
			}

			return field;
		}

		private static void SkipSpaces(string text, ref int position)
		{
			while (position < text.Length && char.IsWhiteSpace(text[position]))
			{
				position++;
			}
		}

		private static KeyValuePair<string, long>[] ParseEnumMembers(string list)
		{
			var members = new List<KeyValuePair<string, long>>();
			foreach (string entry in list.Split(','))
			{
				int eq = entry.IndexOf('=');
				long value;
				if (eq > 0 && long.TryParse(entry.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
				{
					members.Add(new KeyValuePair<string, long>(entry.Substring(0, eq).Trim(), value));
				}
			}

			return members.Count > 0 ? members.ToArray() : null;
		}

		private static string SingleTagSpec(ValueTag tag, KeyValuePair<string, long>[] enumMembers)
		{
			if (tag != ValueTag.Enum || enumMembers == null || enumMembers.Length == 0)
			{
				return tag.ToString();
			}

			var builder = new System.Text.StringBuilder("Enum(");
			for (int i = 0; i < enumMembers.Length; i++)
			{
				if (i > 0)
				{
					builder.Append(',');
				}

				builder.Append(enumMembers[i].Key).Append('=').Append(enumMembers[i].Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
			}

			return builder.Append(')').ToString();
		}

		/// <summary>Name shown in the inspector: "m_LocalPosition" -> "Local Position", "moveSpeed" -> "Move Speed".</summary>
		public static string NicifyName(string name)
		{
			if (name.StartsWith("m_", StringComparison.Ordinal))
			{
				name = name.Substring(2);
			}
			else if (name.StartsWith("_", StringComparison.Ordinal))
			{
				name = name.Substring(1);
			}

			var builder = new System.Text.StringBuilder(name.Length + 8);
			for (int i = 0; i < name.Length; i++)
			{
				char c = name[i];
				if (i == 0)
				{
					builder.Append(char.ToUpperInvariant(c));
					continue;
				}

				bool boundary = char.IsUpper(c) && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])));
				if (boundary || (char.IsDigit(c) && !char.IsDigit(name[i - 1])))
				{
					builder.Append(' ');
				}

				builder.Append(c);
			}

			return builder.ToString();
		}

		/// <summary>
		///     The classes and structs an inspector can create for a slot of <paramref name="baseType" /> (Odin-style
		///     polymorphic fields): concrete public types of the registered assemblies that derive from or implement it
		///     and have a parameterless constructor. Engine objects are left out (components and ScriptableObjects are
		///     referenced, not created inline), and so are assets, delegates and compiler-generated types.
		/// </summary>
		public static List<Type> GetConcreteTypes(Type baseType)
		{
			var result = new List<Type>();
			foreach (Assembly assembly in TypeRegistry.Assemblies)
			{
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types;
				}

				foreach (Type type in types)
				{
					if (type != null && IsInlineCreatable(type) && baseType.IsAssignableFrom(type))
					{
						result.Add(type);
					}
				}
			}

			result.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
			return result;
		}

		/// <summary>A type an Object slot can hold by value and the inspector can create (see <see cref="GetConcreteTypes" />).</summary>
		public static bool IsInlineCreatable(Type type)
		{
			if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || type.IsPointer || type.IsArray || type.IsPrimitive || type.IsEnum
				|| !(type.IsPublic || type.IsNestedPublic) || type == typeof(string)
				|| typeof(EngineObject).IsAssignableFrom(type) || typeof(IAsset).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type)
				|| type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
			{
				return false;
			}

			return type.IsValueType || type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null;
		}

		#endregion

		#region PrivateMethods

		private static bool IsSupported(Type type, int depth)
		{
			if (depth > MaxDepth)
			{
				return false;
			}

			if (type.IsPrimitive || type == typeof(string) || type.IsEnum)
			{
				return type != typeof(IntPtr) && type != typeof(UIntPtr);
			}

			if (IsMathType(type))
			{
				return true;
			}

			if (IsReferenceType(type) || IsAssetType(type))
			{
				return true;
			}

			// Collections of collections are fine: every array and dictionary value records its own encodings.
			Type element = GetElementType(type);
			if (element != null)
			{
				return IsSupported(element, depth + 1);
			}

			if (GetDictionaryTypes(type, out Type keyType, out Type valueType))
			{
				return IsSupportedKey(keyType) && IsSupported(valueType, depth + 1);
			}

			if (type.IsArray || type.IsGenericType || type.IsPointer || typeof(Delegate).IsAssignableFrom(type))
			{
				return false;
			}

			// Polymorphic slot: any [Serializable] implementation is stored with its type.
			if (type.IsInterface || type.IsAbstract)
			{
				return type != typeof(object);
			}

			// User data: [Serializable] class or struct.
			return type.IsDefined(typeof(SerializableAttribute), false);
		}

		private static SerializedField[] Collect(Type type)
		{
			// The engine bases (components, ScriptableObjects) hold no serialized fields of their own.
			var chain = new List<Type>();
			for (Type t = type; t != null && t != typeof(object) && t != typeof(Behaviour) && t != typeof(ScriptableObject) && t != typeof(EngineObject); t = t.BaseType)
			{
				chain.Add(t);
			}

			chain.Reverse();
			var result = new List<SerializedField>();
			var names = new HashSet<string>();
			foreach (Type t in chain)
			{
				foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (field.IsInitOnly || field.IsLiteral || field.IsDefined(typeof(NonSerializedAttribute), false))
					{
						continue;
					}

					bool serialize = field.IsPublic || field.IsDefined(typeof(SerializeField), false);
					if (!serialize || !IsSupported(field.FieldType))
					{
						continue;
					}

					string name = field.Name;
					// [field: SerializeField] on an auto-property: "<Speed>k__BackingField" -> "Speed".
					if (name.StartsWith("<", StringComparison.Ordinal))
					{
						int end = name.IndexOf('>');
						name = end > 1 ? name.Substring(1, end - 1) : name;
					}

					if (!names.Add(name))
					{
						Debug.LogWarning($"[Serialization] {type.Name}: field '{name}' is declared twice in the class hierarchy; the base one is serialized");
						continue;
					}

					result.Add(new SerializedField(field, name));
				}
			}

			return result.ToArray();
		}

		#endregion
	}

	/// <summary>A serialized field: its reflection info, the name it is stored under and a compiled accessor.</summary>
	public sealed class SerializedField
	{
		#region PublicFields

		public readonly FieldInfo Field;
		public readonly string Name;
		public readonly string DisplayName;
		public readonly ValueTag Tag;

		/// <summary>Arrays: the element encoding; dictionaries: the value encoding.</summary>
		public readonly ValueTag ElementTag;

		/// <summary>Dictionaries: the key encoding (Null otherwise).</summary>
		public readonly ValueTag KeyTag;

		public Type FieldType => Field.FieldType;
		public bool HideInInspector => Field.IsDefined(typeof(LELEngine.HideInInspector), false);

		/// <summary>Typed getter / setter (compiled once per field).</summary>
		public FieldAccessor Accessor
		{
			get
			{
				if (accessor == null)
				{
					accessor = FieldAccessor.Create(Field);
				}

				return accessor;
			}
		}

		#endregion

		#region PrivateFields

		private FieldAccessor accessor;

		#endregion

		#region Constructors

		public SerializedField(FieldInfo field, string name)
		{
			Field = field;
			Name = name;
			DisplayName = SerializationUtility.NicifyName(name);
			Tag = SerializationUtility.TagOf(field.FieldType, out ElementTag);
			KeyTag = Tag == ValueTag.Dictionary ? SerializationUtility.KeyTagOf(field.FieldType) : ValueTag.Null;
		}

		#endregion

		#region PublicMethods

		public object GetValue(object target)
		{
			return Accessor.GetBoxed(target);
		}

		public void SetValue(object target, object value)
		{
			Accessor.SetBoxed(target, value);
		}

		#endregion
	}

	/// <summary>
	///     Reads and writes one field without reflection per access: a delegate pair compiled from an expression
	///     tree (fields of classes) or the reflection fallback (fields of structs, which must mutate the boxed copy).
	/// </summary>
	public abstract class FieldAccessor
	{
		#region PublicMethods

		public abstract object GetBoxed(object target);
		public abstract void SetBoxed(object target, object value);

		public static FieldAccessor Create(FieldInfo field)
		{
			MethodInfo method = typeof(FieldAccessor).GetMethod(nameof(CreateTyped), BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(field.FieldType);
			return (FieldAccessor)method.Invoke(null, new object[] { field });
		}

		#endregion

		#region PrivateMethods

		private static FieldAccessor CreateTyped<T>(FieldInfo field)
		{
			if (!field.DeclaringType.IsValueType)
			{
				try
				{
					ParameterExpression target = Expression.Parameter(typeof(object), "target");
					ParameterExpression value = Expression.Parameter(typeof(T), "value");
					MemberExpression member = Expression.Field(Expression.Convert(target, field.DeclaringType), field);
					Func<object, T> getter = Expression.Lambda<Func<object, T>>(member, target).Compile();
					Action<object, T> setter = Expression.Lambda<Action<object, T>>(Expression.Assign(member, value), target, value).Compile();
					return new FieldAccessor<T>(getter, setter);
				}
				catch (Exception)
				{
					// Falls through to reflection.
				}
			}

			return new FieldAccessor<T>(target => (T)field.GetValue(target), (target, value) => field.SetValue(target, value));
		}

		#endregion
	}

	public sealed class FieldAccessor<T> : FieldAccessor
	{
		#region PublicFields

		public readonly Func<object, T> Get;
		public readonly Action<object, T> Set;

		#endregion

		#region Constructors

		public FieldAccessor(Func<object, T> get, Action<object, T> set)
		{
			Get = get;
			Set = set;
		}

		#endregion

		#region PublicMethods

		public override object GetBoxed(object target)
		{
			return Get(target);
		}

		public override void SetBoxed(object target, object value)
		{
			Set(target, value == null ? default : (T)value);
		}

		#endregion
	}
}
