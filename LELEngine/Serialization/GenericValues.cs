using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Mathematics;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Operations on generic data (<see cref="SerializedNode" />, <see cref="SerializedArray" />,
	///     <see cref="SerializedDictionary" />, boxed values) that prefab resolution and overrides need without loading
	///     any type: deep copies with scene reference ids remapped, deep comparison, and reading or writing the value
	///     at a property path. Generic data of a resolved prefab is shared: it is copied before it is changed.
	/// </summary>
	public static class GenericValues
	{
		#region PublicMethods

		/// <summary>True when values of this encoding hold scene reference ids (SceneRef, and an Object slot holding a ulong).</summary>
		public static bool IsReference(object value, ValueTag tag)
		{
			return value is ulong && (tag == ValueTag.SceneRef || tag == ValueTag.Object);
		}

		/// <summary>
		///     A deep copy of a generic value; scene reference ids go through <paramref name="mapReference" /> (null: kept),
		///     0 stays 0. Boxed primitives, strings and asset references are immutable and shared.
		/// </summary>
		public static object Clone(object value, ValueTag tag, Func<ulong, ulong> mapReference = null, int depth = 0)
		{
			if (value == null || depth > 64)
			{
				return value;
			}

			switch (value)
			{
				case ulong id when tag == ValueTag.SceneRef || tag == ValueTag.Object:
					return id == 0 || mapReference == null ? id : mapReference(id);
				case SerializedNode node:
					return CloneNode(node, mapReference, depth);
				case SerializedArray array:
				{
					var copy = new SerializedArray { ElementTag = array.ElementTag };
					if (array.Bytes != null)
					{
						copy.Bytes = array.Bytes;
						return copy;
					}

					foreach (object item in array.Items)
					{
						copy.Items.Add(Clone(item, array.ElementTag, mapReference, depth + 1));
					}

					return copy;
				}
				case SerializedDictionary dictionary:
				{
					var copy = new SerializedDictionary { KeyTag = dictionary.KeyTag, ValueTag = dictionary.ValueTag };
					foreach (KeyValuePair<object, object> entry in dictionary.Entries)
					{
						copy.Entries.Add(new KeyValuePair<object, object>(Clone(entry.Key, dictionary.KeyTag, mapReference, depth + 1), Clone(entry.Value, dictionary.ValueTag, mapReference, depth + 1)));
					}

					return copy;
				}
				default:
					return value;
			}
		}

		public static SerializedNode CloneNode(SerializedNode node, Func<ulong, ulong> mapReference = null, int depth = 0)
		{
			if (node == null)
			{
				return null;
			}

			var copy = new SerializedNode { TypeName = node.TypeName };
			foreach (SerializedProperty property in node.Properties)
			{
				copy.Properties.Add(new SerializedProperty { Name = property.Name, Tag = property.Tag, ElementTag = property.ElementTag, Value = Clone(property.Value, property.Tag, mapReference, depth + 1) });
			}

			return copy;
		}

		/// <summary>
		///     Deep equality of two generic values of the same encoding; reference ids of <paramref name="a" /> go through
		///     <paramref name="mapA" /> first (a prefab's ids compared with an instance's).
		/// </summary>
		public static bool AreEqual(object a, object b, ValueTag tag, Func<ulong, ulong> mapA = null, int depth = 0)
		{
			if (depth > 64)
			{
				return true;
			}

			if (a == null || b == null)
			{
				if (a == null && b == null)
				{
					return true;
				}

				// A reference 0 is a null reference.
				return (a is ulong za && za == 0 && b == null) || (b is ulong zb && zb == 0 && a == null) || (a is AssetReference ra && ra.IsNull && b == null) || (b is AssetReference rb && rb.IsNull && a == null);
			}

			switch (a)
			{
				case ulong idA when b is ulong idB && (tag == ValueTag.SceneRef || tag == ValueTag.Object):
					return (idA == 0 || mapA == null ? idA : mapA(idA)) == idB;
				case AssetReference assetA when b is AssetReference assetB:
					return SameAsset(assetA, assetB);
				case SerializedNode nodeA when b is SerializedNode nodeB:
					return NodesEqual(nodeA, nodeB, mapA, depth);
				case SerializedArray arrayA when b is SerializedArray arrayB:
				{
					if (arrayA.Count != arrayB.Count)
					{
						return false;
					}

					if (arrayA.Bytes != null && arrayB.Bytes != null)
					{
						return arrayA.Bytes.AsSpan().SequenceEqual(arrayB.Bytes);
					}

					List<object> itemsA = DocumentValues.ExpandItems(arrayA);
					List<object> itemsB = DocumentValues.ExpandItems(arrayB);
					ValueTag element = arrayA.ElementTag != ValueTag.Null ? arrayA.ElementTag : arrayB.ElementTag;
					for (int i = 0; i < itemsA.Count; i++)
					{
						if (!AreEqual(itemsA[i], itemsB[i], element, mapA, depth + 1))
						{
							return false;
						}
					}

					return true;
				}
				case SerializedDictionary dictionaryA when b is SerializedDictionary dictionaryB:
				{
					if (dictionaryA.Entries.Count != dictionaryB.Entries.Count)
					{
						return false;
					}

					foreach (KeyValuePair<object, object> entry in dictionaryA.Entries)
					{
						object mappedKey = Clone(entry.Key, dictionaryA.KeyTag, mapA);
						int index = FindEntry(dictionaryB, mappedKey, dictionaryA.KeyTag);
						if (index < 0 || !AreEqual(entry.Value, dictionaryB.Entries[index].Value, dictionaryA.ValueTag, mapA, depth + 1))
						{
							return false;
						}
					}

					return true;
				}
				default:
					return ValuesEqual(a, b);
			}
		}

		/// <summary>Two nodes of the same class with equal properties (a property one of them lacks is compared with the class default).</summary>
		public static bool NodesEqual(SerializedNode a, SerializedNode b, Func<ulong, ulong> mapA = null, int depth = 0)
		{
			if (!string.Equals(a.TypeName, b.TypeName, StringComparison.Ordinal))
			{
				return false;
			}

			foreach (SerializedProperty property in b.Properties)
			{
				SerializedProperty other = a.Find(property.Name) ?? DefaultProperty(a.TypeName, property.Name);
				if (other == null)
				{
					continue;
				}

				if (other.Tag != property.Tag || !AreEqual(other.Value, property.Value, property.Tag, mapA, depth + 1))
				{
					return false;
				}
			}

			foreach (SerializedProperty property in a.Properties)
			{
				if (b.Find(property.Name) == null)
				{
					SerializedProperty other = DefaultProperty(b.TypeName, property.Name);
					if (other != null && (other.Tag != property.Tag || !AreEqual(property.Value, other.Value, property.Tag, mapA, depth + 1)))
					{
						return false;
					}
				}
			}

			return true;
		}

		/// <summary>The same asset file (by GUID when both have one, else by path) and sub-asset.</summary>
		public static bool SameAsset(AssetReference a, AssetReference b)
		{
			if (a.IsNull || b.IsNull)
			{
				return a.IsNull && b.IsNull;
			}

			bool sameFile = a.Guid != null && b.Guid != null
				? string.Equals(a.Guid, b.Guid, StringComparison.OrdinalIgnoreCase)
				: string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
			return sameFile && string.Equals(a.Name, b.Name, StringComparison.Ordinal);
		}

		/// <summary>Index of the entry with an equal key, or -1.</summary>
		public static int FindEntry(SerializedDictionary dictionary, object key, ValueTag keyTag)
		{
			for (int i = 0; i < dictionary.Entries.Count; i++)
			{
				if (KeyEquals(dictionary.Entries[i].Key, dictionary.KeyTag, key, keyTag))
				{
					return i;
				}
			}

			return -1;
		}

		public static bool KeyEquals(object a, ValueTag tagA, object b, ValueTag tagB)
		{
			if (a == null || b == null)
			{
				return a == null && b == null;
			}

			if (a is AssetReference assetA && b is AssetReference assetB)
			{
				return SameAsset(assetA, assetB);
			}

			return string.Equals(KeyString(a, tagA), KeyString(b, tagB), StringComparison.Ordinal);
		}

		/// <summary>
		///     A dictionary key as text for paths and lookups: numbers and enums by value, strings as they are, references
		///     as "#id", assets by GUID (or path), vectors as their components.
		/// </summary>
		public static string KeyString(object key, ValueTag tag)
		{
			switch (key)
			{
				case null:
					return "";
				case string text:
					return text;
				case ulong id when tag == ValueTag.SceneRef || tag == ValueTag.Object:
					return "#" + id.ToString(CultureInfo.InvariantCulture);
				case AssetReference asset:
					return "@" + (asset.Guid ?? asset.Path) + (asset.Name != null ? "/" + asset.Name : "");
				case bool flag:
					return flag ? "true" : "false";
				case char c:
					return c.ToString();
				case Enum named:
					return Convert.ToInt64(named, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
				case float f:
					return f.ToString("R", CultureInfo.InvariantCulture);
				case double d:
					return d.ToString("R", CultureInfo.InvariantCulture);
				case Vector2 v:
					return Join(v.X, v.Y);
				case Vector3 v:
					return Join(v.X, v.Y, v.Z);
				case Vector4 v:
					return Join(v.X, v.Y, v.Z, v.W);
				case Quaternion q:
					return Join(q.X, q.Y, q.Z, q.W);
				case Color4 c:
					return Join(c.R, c.G, c.B, c.A);
				case Vector2i v:
					return v.X + "," + v.Y;
				case Vector3i v:
					return v.X + "," + v.Y + "," + v.Z;
				case Vector4i v:
					return v.X + "," + v.Y + "," + v.Z + "," + v.W;
				default:
					return Convert.ToString(key, CultureInfo.InvariantCulture);
			}
		}

		/// <summary>Short text of a generic value (overrides lists, messages).</summary>
		public static string Describe(object value, ValueTag tag)
		{
			switch (value)
			{
				case null:
					return "None";
				case ulong id when tag == ValueTag.SceneRef || tag == ValueTag.Object:
					return id == 0 ? "None" : "object #" + id.ToString("X", CultureInfo.InvariantCulture);
				case AssetReference asset:
					return asset.IsNull ? "None" : System.IO.Path.GetFileName(asset.Path ?? asset.Guid) + (asset.Name != null ? " / " + asset.Name : "");
				case SerializedNode node:
				{
					string name = node.TypeName ?? "?";
					int comma = name.IndexOf(',');
					name = comma > 0 ? name.Substring(0, comma) : name;
					return name.Substring(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+')) + 1);
				}
				case SerializedArray array:
					return array.Count + " elements";
				case SerializedDictionary dictionary:
					return dictionary.Entries.Count + " entries";
				case string text:
					return "\"" + text + "\"";
				default:
					return KeyString(value, tag);
			}
		}

		// ---------------------------------------------------------------- paths

		/// <summary>The value at a property path of a component's data (<see cref="PropertyPath" />); false when the path does not exist.</summary>
		public static bool TryGet(SerializedNode root, string path, out object value, out ValueTag tag, out ValueTag elementTag)
		{
			value = null;
			tag = ValueTag.Null;
			elementTag = ValueTag.Null;
			if (root == null || !PropertyPath.TryParse(path, out List<PathSegment> segments))
			{
				return false;
			}

			object current = root;
			ValueTag currentTag = ValueTag.Object;
			ValueTag currentElement = ValueTag.Null;
			foreach (PathSegment segment in segments)
			{
				if (segment.IsField)
				{
					SerializedProperty property = (current as SerializedNode)?.Find(segment.Name);
					if (property == null)
					{
						return false;
					}

					current = property.Value;
					currentTag = property.Tag;
					currentElement = property.ElementTag;
				}
				else if (segment.IsSize)
				{
					if (!(current is SerializedArray array))
					{
						return false;
					}

					current = array.Count;
					currentTag = ValueTag.Int32;
					currentElement = ValueTag.Null;
				}
				else
				{
					if (!(current is SerializedArray array) || segment.Index >= array.Count)
					{
						return false;
					}

					current = DocumentValues.ExpandItems(array)[segment.Index];
					currentTag = array.ElementTag;
					currentElement = SerializationUtility.ElementTagOf(current);
				}
			}

			value = current;
			tag = currentTag;
			elementTag = currentElement;
			return true;
		}

		/// <summary>
		///     Writes a value at a property path of a component's data (changed in place: copy shared data first). A list
		///     element must exist (a "#size" modification grows the list first); a length pads with defaults. False when
		///     the path does not exist or the value does not fit (another encoding than the list's elements).
		/// </summary>
		public static bool TrySet(SerializedNode root, string path, object value, ValueTag tag, ValueTag elementTag)
		{
			if (root == null || !PropertyPath.TryParse(path, out List<PathSegment> segments))
			{
				return false;
			}

			object container = root;
			for (int i = 0; i < segments.Count; i++)
			{
				PathSegment segment = segments[i];
				bool last = i == segments.Count - 1;
				if (segment.IsField)
				{
					SerializedProperty property = (container as SerializedNode)?.Find(segment.Name);
					if (property == null)
					{
						return false;
					}

					if (last)
					{
						property.Value = value;
						property.Tag = tag;
						property.ElementTag = elementTag != ValueTag.Null ? elementTag : SerializationUtility.ElementTagOf(value);
						return true;
					}

					container = property.Value;
					continue;
				}

				if (!(container is SerializedArray array))
				{
					return false;
				}

				Materialize(array);
				if (segment.IsSize)
				{
					if (!last || !TryGetCount(value, out int count))
					{
						return false;
					}

					while (array.Items.Count > count)
					{
						array.Items.RemoveAt(array.Items.Count - 1);
					}

					while (array.Items.Count < count)
					{
						array.Items.Add(null);
					}

					return true;
				}

				if (segment.Index >= array.Items.Count)
				{
					return false;
				}

				if (last)
				{
					if (tag != array.ElementTag && value != null)
					{
						return false;
					}

					array.Items[segment.Index] = value;
					return true;
				}

				container = array.Items[segment.Index];
			}

			return false;
		}

		/// <summary>The dictionary at a property path (changed in place), or null.</summary>
		public static SerializedDictionary FindDictionary(SerializedNode root, string path)
		{
			return TryGet(root, path, out object value, out _, out _) ? value as SerializedDictionary : null;
		}

		/// <summary>Applies one modification to a component's data (changed in place); false when it does not fit.</summary>
		public static bool Apply(SerializedNode data, PropertyModification modification)
		{
			if (!modification.HasKey)
			{
				return TrySet(data, modification.Path, modification.Value, modification.Tag, modification.ElementTag);
			}

			SerializedDictionary dictionary = FindDictionary(data, modification.Path);
			if (dictionary == null)
			{
				return false;
			}

			int index = FindEntry(dictionary, modification.Key, modification.KeyTag);
			if (modification.Removed)
			{
				if (index >= 0)
				{
					dictionary.Entries.RemoveAt(index);
				}

				return true;
			}

			if (modification.Tag != dictionary.ValueTag && modification.Value != null)
			{
				return false;
			}

			var entry = new KeyValuePair<object, object>(modification.Key, modification.Value);
			if (index >= 0)
			{
				dictionary.Entries[index] = entry;
			}
			else
			{
				dictionary.Entries.Add(entry);
			}

			return true;
		}

		/// <summary>The value a fresh instance of the class gives a field (for data written before the field existed), or null.</summary>
		public static SerializedProperty DefaultProperty(string typeName, string field)
		{
			return DefaultNode(typeName)?.Find(field);
		}

		/// <summary>Generic data of a new instance of the class (its constructor defaults); null when the class is unknown.</summary>
		public static SerializedNode DefaultNode(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
			{
				return null;
			}

			lock (defaults)
			{
				if (defaults.TryGetValue(typeName, out SerializedNode cached))
				{
					return cached;
				}
			}

			SerializedNode node = null;
			Type type = TypeRegistry.Resolve(typeName);
			if (type != null && !type.IsAbstract && !type.IsInterface)
			{
				try
				{
					object instance = Activator.CreateInstance(type, true);
					node = DocumentValues.ToNode(instance, new SerializationContext());
				}
				catch (Exception)
				{
					node = null;
				}
			}

			lock (defaults)
			{
				defaults[typeName] = node;
			}

			return node;
		}

		/// <summary>Forgets the class defaults (game types; before a script reload).</summary>
		public static void ClearCache()
		{
			lock (defaults)
			{
				defaults.Clear();
			}
		}

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, SerializedNode> defaults = new Dictionary<string, SerializedNode>(StringComparer.Ordinal);

		#endregion

		#region PrivateMethods

		/// <summary>Bulk element bytes become boxed items (so single elements can change).</summary>
		private static void Materialize(SerializedArray array)
		{
			if (array.Bytes == null)
			{
				return;
			}

			List<object> items = DocumentValues.ExpandItems(array);
			array.Bytes = null;
			array.Items.Clear();
			array.Items.AddRange(items);
		}

		private static bool TryGetCount(object value, out int count)
		{
			count = 0;
			try
			{
				count = Convert.ToInt32(value, CultureInfo.InvariantCulture);
				return count >= 0;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool ValuesEqual(object a, object b)
		{
			if (a.GetType() == b.GetType())
			{
				return a.Equals(b);
			}

			// The same number boxed as two types (generic data read without a type table).
			if (IsNumeric(a) && IsNumeric(b))
			{
				return Convert.ToDouble(a, CultureInfo.InvariantCulture).Equals(Convert.ToDouble(b, CultureInfo.InvariantCulture));
			}

			return a.Equals(b);
		}

		private static bool IsNumeric(object value)
		{
			switch (value)
			{
				case sbyte _:
				case byte _:
				case short _:
				case ushort _:
				case int _:
				case uint _:
				case long _:
				case ulong _:
				case float _:
				case double _:
					return true;
				default:
					return false;
			}
		}

		private static string Join(params float[] values)
		{
			var parts = new string[values.Length];
			for (int i = 0; i < values.Length; i++)
			{
				parts[i] = values[i].ToString("R", CultureInfo.InvariantCulture);
			}

			return string.Join(",", parts);
		}

		#endregion
	}
}
