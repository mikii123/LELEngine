using System;
using System.Collections.Generic;

namespace LELEngine.Serialization
{
	/// <summary>
	///     One override of a prefab instance: a value of one object or component of the prefab replaced by the
	///     instance. <see cref="Target" /> is the object's id in the prefab's own space, <see cref="Path" /> the
	///     property inside it (<see cref="PropertyPath" />), the value is generic data (<see cref="SerializedProperty" />
	///     conventions) whose scene references are ids of the document holding the instance. A modification of a
	///     dictionary entry names the entry's key (<see cref="HasKey" />); <see cref="Removed" /> drops that key.
	///     <code>
	///     "speed"                    a field
	///     "weapon.damage"            a field of a nested (or polymorphic) object of the same class as the prefab's
	///     "weapon"                   the whole value (another class, null, a reference): written with its type
	///     "drops[2]", "drops.#size"  a list element, the list's length
	///     "table" + key              a dictionary entry
	///     "m_Name", "m_IsActive", "m_Layer"   GameObject header; "m_Enabled" a component's enabled flag
	///     </code>
	/// </summary>
	public sealed class PropertyModification
	{
		#region PublicFields

		public const string NameProperty = "m_Name";
		public const string ActiveProperty = "m_IsActive";
		public const string LayerProperty = "m_Layer";
		public const string EnabledProperty = "m_Enabled";

		/// <summary>Id of the modified GameObject or component in the prefab's space.</summary>
		public ulong Target;

		public string Path;

		/// <summary>The modification is of the dictionary entry with <see cref="Key" /> at <see cref="Path" />.</summary>
		public bool HasKey;

		public object Key;
		public ValueTag KeyTag;

		/// <summary>A dictionary entry the instance does not have.</summary>
		public bool Removed;

		public ValueTag Tag;
		public ValueTag ElementTag;
		public object Value;

		#endregion

		#region PublicMethods

		/// <summary>Same target, path and key (the value aside).</summary>
		public bool SameProperty(PropertyModification other)
		{
			return other != null && other.Target == Target && other.Path == Path && other.HasKey == HasKey
				&& (!HasKey || GenericValues.KeyEquals(Key, KeyTag, other.Key, other.KeyTag));
		}

		/// <summary>The property path with the dictionary key, for lookups ("table{gold}").</summary>
		public string FullPath => HasKey ? PropertyPath.WithKey(Path, GenericValues.KeyString(Key, KeyTag)) : Path;

		public override string ToString()
		{
			return Target + ":" + FullPath + (Removed ? " (removed)" : " = " + GenericValues.Describe(Value, Tag));
		}

		#endregion
	}

	/// <summary>
	///     Property paths of serialized fields: field names joined by '.', "[i]" for list elements, "#size" for a list's
	///     length; inspector lookups append a dictionary key as "{key}".
	/// </summary>
	public static class PropertyPath
	{
		#region PublicFields

		public const string Size = "#size";

		#endregion

		#region PublicMethods

		public static string Field(string path, string name)
		{
			return string.IsNullOrEmpty(path) ? name : path + "." + name;
		}

		public static string Element(string path, int index)
		{
			return path + "[" + index + "]";
		}

		public static string SizeOf(string path)
		{
			return path + "." + Size;
		}

		public static string WithKey(string path, string key)
		{
			return path + "{" + key + "}";
		}

		/// <summary>True when <paramref name="path" /> is <paramref name="ancestor" /> or lies inside it ("a.b[2]" in "a.b").</summary>
		public static bool IsWithin(string path, string ancestor)
		{
			if (path == null || ancestor == null)
			{
				return false;
			}

			if (path.Length == ancestor.Length)
			{
				return string.Equals(path, ancestor, StringComparison.Ordinal);
			}

			return path.Length > ancestor.Length && path.StartsWith(ancestor, StringComparison.Ordinal)
				&& (path[ancestor.Length] == '.' || path[ancestor.Length] == '[' || path[ancestor.Length] == '{');
		}

		/// <summary>The field name a path starts with ("weapon" of "weapon.damage", "drops" of "drops[2]").</summary>
		public static string RootField(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return path;
			}

			int end = path.IndexOfAny(new[] { '.', '[', '{' });
			return end < 0 ? path : path.Substring(0, end);
		}

		/// <summary>The segments of a path; false for malformed paths.</summary>
		internal static bool TryParse(string path, out List<PathSegment> segments)
		{
			segments = new List<PathSegment>();
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}

			foreach (string part in path.Split('.'))
			{
				if (part.Length == 0)
				{
					return false;
				}

				if (part == Size)
				{
					segments.Add(PathSegment.SizeSegment);
					continue;
				}

				int bracket = part.IndexOf('[');
				string name = bracket < 0 ? part : part.Substring(0, bracket);
				if (name.Length > 0)
				{
					segments.Add(PathSegment.FieldSegment(name));
				}
				else if (bracket != 0)
				{
					return false;
				}

				while (bracket >= 0)
				{
					int close = part.IndexOf(']', bracket);
					if (close < 0 || !int.TryParse(part.Substring(bracket + 1, close - bracket - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
					{
						return false;
					}

					segments.Add(PathSegment.IndexSegment(index));
					bracket = close + 1 < part.Length ? (part[close + 1] == '[' ? close + 1 : -2) : -1;
					if (bracket == -2)
					{
						return false;
					}
				}
			}

			return segments.Count > 0;
		}

		#endregion
	}

	/// <summary>One step of a property path: a field, a list element or a list's length.</summary>
	internal readonly struct PathSegment
	{
		public readonly string Name;

		/// <summary>Element index; -1 for a field, -2 for the length.</summary>
		public readonly int Index;

		private PathSegment(string name, int index)
		{
			Name = name;
			Index = index;
		}

		public bool IsField => Index == -1;
		public bool IsSize => Index == -2;
		public bool IsElement => Index >= 0;

		public static PathSegment FieldSegment(string name)
		{
			return new PathSegment(name, -1);
		}

		public static PathSegment IndexSegment(int index)
		{
			return new PathSegment(null, index);
		}

		public static readonly PathSegment SizeSegment = new PathSegment(null, -2);
	}
}
