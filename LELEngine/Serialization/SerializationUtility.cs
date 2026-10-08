using System;
using System.Collections.Generic;
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
	///     Which fields are serialized (Unity rules): instance fields that are public or marked
	///     <see cref="SerializeField" />, not <see cref="NonSerializedAttribute" />, not readonly, of a supported type.
	///     Supported: primitives, string, enums, OpenTK vectors / quaternion / Color4, one-dimensional arrays and
	///     List&lt;T&gt; of supported element types, [Serializable] classes and structs (nested), references to
	///     GameObjects and components of the same scene, and assets (<see cref="IAsset" />).
	///
	///     The per-type field cache holds game types; <see cref="ClearCache" /> must run before a game assembly is
	///     unloaded.
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

			if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion)
				|| type == typeof(Color4) || type == typeof(Vector2i) || type == typeof(Vector3i) || type == typeof(Vector4i))
			{
				return true;
			}

			if (IsReferenceType(type) || IsAssetType(type))
			{
				return true;
			}

			Type element = GetElementType(type);
			if (element != null)
			{
				return GetElementType(element) == null && IsSupported(element, depth + 1);
			}

			if (type.IsArray || type.IsAbstract || type.IsInterface || type.IsGenericType || type.IsPointer)
			{
				return false;
			}

			// User data: [Serializable] class or struct.
			return type.IsDefined(typeof(SerializableAttribute), false) && !typeof(Delegate).IsAssignableFrom(type);
		}

		private static SerializedField[] Collect(Type type)
		{
			var chain = new List<Type>();
			for (Type t = type; t != null && t != typeof(object) && t != typeof(Behaviour); t = t.BaseType)
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

	/// <summary>A serialized field: its reflection info and the name it is stored under.</summary>
	public sealed class SerializedField
	{
		#region PublicFields

		public readonly FieldInfo Field;
		public readonly string Name;
		public readonly string DisplayName;
		public Type FieldType => Field.FieldType;
		public bool HideInInspector => Field.IsDefined(typeof(LELEngine.HideInInspector), false);

		#endregion

		#region Constructors

		public SerializedField(FieldInfo field, string name)
		{
			Field = field;
			Name = name;
			DisplayName = SerializationUtility.NicifyName(name);
		}

		#endregion

		#region PublicMethods

		public object GetValue(object target)
		{
			return Field.GetValue(target);
		}

		public void SetValue(object target, object value)
		{
			Field.SetValue(target, value);
		}

		#endregion
	}
}
