using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace LELEngine.UIElements
{
	/// <summary>A path into an object: "player.stats.health", "items[2].name" (public fields, properties, list indices).</summary>
	public readonly struct PropertyPath : IEquatable<PropertyPath>
	{
		#region PublicFields

		public readonly string path;

		public bool isEmpty => string.IsNullOrEmpty(path);

		#endregion

		#region Constructors

		public PropertyPath(string path)
		{
			this.path = path ?? "";
		}

		#endregion

		#region PublicMethods

		public static implicit operator PropertyPath(string path)
		{
			return new PropertyPath(path);
		}

		/// <summary>"a.b" + "c" = "a.b.c"; "a" + "[2]" = "a[2]".</summary>
		public static PropertyPath Combine(PropertyPath left, PropertyPath right)
		{
			if (left.isEmpty) return right;
			if (right.isEmpty) return left;
			return new PropertyPath(right.path.StartsWith("[", StringComparison.Ordinal) ? left.path + right.path : left.path + "." + right.path);
		}

		public bool Equals(PropertyPath other)
		{
			return string.Equals(path, other.path, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is PropertyPath other && Equals(other);
		}

		public override int GetHashCode()
		{
			return path != null ? path.GetHashCode() : 0;
		}

		public override string ToString()
		{
			return path ?? "";
		}

		#endregion
	}

	/// <summary>
	///     Reads and writes values along a <see cref="PropertyPath" /> with reflection; member accessors are compiled
	///     once per (type, member) so bindings cost a few delegate calls per update.
	/// </summary>
	public static class PropertyPathAccess
	{
		#region PrivateFields

		private static readonly Dictionary<(Type, string), MemberAccessor> accessors = new Dictionary<(Type, string), MemberAccessor>();
		private static readonly Dictionary<string, string[]> splitCache = new Dictionary<string, string[]>(StringComparer.Ordinal);

		#endregion

		#region PublicMethods

		/// <summary>The value at the path; false when a step is missing (null object, unknown member, bad index).</summary>
		public static bool TryGet(object source, PropertyPath path, out object value, out Type valueType)
		{
			value = source;
			valueType = source?.GetType();
			if (path.isEmpty)
			{
				return source != null;
			}

			object current = source;
			Type currentType = source?.GetType();
			foreach (string part in Split(path.path))
			{
				if (current == null)
				{
					value = null;
					return false;
				}

				if (!Step(current, part, out current, out currentType))
				{
					value = null;
					return false;
				}
			}

			value = current;
			valueType = current != null ? current.GetType() : currentType;
			return true;
		}

		/// <summary>Writes the value at the path (the last step must be a settable member or a list index).</summary>
		public static bool TrySet(object source, PropertyPath path, object value)
		{
			if (source == null || path.isEmpty)
			{
				return false;
			}

			string[] parts = Split(path.path);
			object current = source;
			for (int i = 0; i < parts.Length - 1; i++)
			{
				Type ignored;
				if (!Step(current, parts[i], out current, out ignored) || current == null)
				{
					return false;
				}
			}

			string last = parts[parts.Length - 1];
			if (last.StartsWith("[", StringComparison.Ordinal))
			{
				int index;
				if (current is IList list && int.TryParse(last.Trim('[', ']'), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < list.Count)
				{
					Type elementType = ElementType(list);
					object converted;
					if (!BindingConversion.TryConvert(value, elementType, out converted))
					{
						return false;
					}

					list[index] = converted;
					return true;
				}

				return false;
			}

			MemberAccessor accessor = GetAccessor(current.GetType(), last);
			if (accessor == null || accessor.set == null)
			{
				return false;
			}

			object convertedValue;
			if (!BindingConversion.TryConvert(value, accessor.type, out convertedValue))
			{
				return false;
			}

			accessor.set(current, convertedValue);
			return true;
		}

		/// <summary>The declared type of the member at the end of the path (null when unknown).</summary>
		public static Type GetValueType(object source, PropertyPath path)
		{
			if (source == null || path.isEmpty)
			{
				return source?.GetType();
			}

			string[] parts = Split(path.path);
			object current = source;
			Type type = source.GetType();
			for (int i = 0; i < parts.Length; i++)
			{
				if (i == parts.Length - 1)
				{
					if (parts[i].StartsWith("[", StringComparison.Ordinal))
					{
						return current is IList list ? ElementType(list) : typeof(object);
					}

					MemberAccessor accessor = GetAccessor(type, parts[i]);
					return accessor?.type;
				}

				if (!Step(current, parts[i], out current, out type) || current == null)
				{
					return null;
				}

				type = current.GetType();
			}

			return type;
		}

		/// <summary>Members a binding can target on this type (public fields and properties).</summary>
		public static List<(string name, Type type, bool writable)> ListMembers(Type type)
		{
			var result = new List<(string, Type, bool)>();
			foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
			{
				if (property.GetIndexParameters().Length == 0 && property.CanRead)
				{
					result.Add((property.Name, property.PropertyType, property.CanWrite));
				}
			}

			foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
			{
				result.Add((field.Name, field.FieldType, !field.IsInitOnly));
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private static string[] Split(string path)
		{
			string[] parts;
			lock (splitCache)
			{
				if (splitCache.TryGetValue(path, out parts))
				{
					return parts;
				}
			}

			var list = new List<string>();
			var builder = new System.Text.StringBuilder();
			for (int i = 0; i < path.Length; i++)
			{
				char c = path[i];
				if (c == '.')
				{
					if (builder.Length > 0) list.Add(builder.ToString());
					builder.Clear();
				}
				else if (c == '[')
				{
					if (builder.Length > 0) list.Add(builder.ToString());
					builder.Clear();
					int close = path.IndexOf(']', i);
					if (close < 0) close = path.Length - 1;
					list.Add(path.Substring(i, close - i + 1));
					i = close;
				}
				else
				{
					builder.Append(c);
				}
			}

			if (builder.Length > 0) list.Add(builder.ToString());
			parts = list.ToArray();
			lock (splitCache)
			{
				splitCache[path] = parts;
			}

			return parts;
		}

		private static bool Step(object current, string part, out object next, out Type nextType)
		{
			next = null;
			nextType = null;
			if (part.StartsWith("[", StringComparison.Ordinal))
			{
				int index;
				if (current is IList list && int.TryParse(part.Trim('[', ']'), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index >= 0 && index < list.Count)
				{
					next = list[index];
					nextType = next?.GetType() ?? ElementType(list);
					return true;
				}

				if (current is IDictionary dictionary)
				{
					string key = part.Trim('[', ']').Trim('"', '\'');
					if (dictionary.Contains(key))
					{
						next = dictionary[key];
						nextType = next?.GetType() ?? typeof(object);
						return true;
					}
				}

				return false;
			}

			MemberAccessor accessor = GetAccessor(current.GetType(), part);
			if (accessor == null)
			{
				return false;
			}

			next = accessor.get(current);
			nextType = next?.GetType() ?? accessor.type;
			return true;
		}

		private static Type ElementType(IList list)
		{
			Type type = list.GetType();
			if (type.IsArray) return type.GetElementType();
			foreach (Type i in type.GetInterfaces())
			{
				if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
				{
					return i.GetGenericArguments()[0];
				}
			}

			return typeof(object);
		}

		private static MemberAccessor GetAccessor(Type type, string member)
		{
			MemberAccessor accessor;
			lock (accessors)
			{
				if (accessors.TryGetValue((type, member), out accessor))
				{
					return accessor;
				}
			}

			accessor = Build(type, member);
			lock (accessors)
			{
				accessors[(type, member)] = accessor;
			}

			return accessor;
		}

		private static MemberAccessor Build(Type type, string member)
		{
			PropertyInfo property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
			if (property != null && property.GetIndexParameters().Length == 0)
			{
				return new MemberAccessor
				{
					type = property.PropertyType,
					get = property.CanRead ? CompileGetter(type, property) : null,
					set = property.CanWrite ? CompileSetter(type, property) : null
				};
			}

			FieldInfo field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
			if (field != null)
			{
				return new MemberAccessor
				{
					type = field.FieldType,
					get = target => field.GetValue(target),
					set = field.IsInitOnly ? null : (target, value) => field.SetValue(target, value)
				};
			}

			return null;
		}

		private static Func<object, object> CompileGetter(Type type, PropertyInfo property)
		{
			try
			{
				ParameterExpression target = Expression.Parameter(typeof(object), "target");
				Expression body = Expression.Convert(Expression.Property(Expression.Convert(target, type), property), typeof(object));
				return Expression.Lambda<Func<object, object>>(body, target).Compile();
			}
			catch (Exception)
			{
				return target => property.GetValue(target);
			}
		}

		private static Action<object, object> CompileSetter(Type type, PropertyInfo property)
		{
			if (type.IsValueType)
			{
				// Boxed structs must be mutated in place: reflection does that; expression trees copy them.
				return (target, value) => property.SetValue(target, value);
			}

			try
			{
				ParameterExpression target = Expression.Parameter(typeof(object), "target");
				ParameterExpression value = Expression.Parameter(typeof(object), "value");
				Expression body = Expression.Assign(Expression.Property(Expression.Convert(target, type), property), Expression.Convert(value, property.PropertyType));
				return Expression.Lambda<Action<object, object>>(body, target, value).Compile();
			}
			catch (Exception)
			{
				return (target, value) => property.SetValue(target, value);
			}
		}

		#endregion

		#region NestedTypes

		private sealed class MemberAccessor
		{
			public Type type;
			public Func<object, object> get;
			public Action<object, object> set;
		}

		#endregion
	}

	/// <summary>Value conversions bindings apply between a data source member and a UI property.</summary>
	public static class BindingConversion
	{
		#region PrivateFields

		private static readonly Dictionary<(Type, Type), MethodInfo> implicitCache = new Dictionary<(Type, Type), MethodInfo>();

		#endregion

		#region PublicMethods

		/// <summary>Converts a value to the target type: numbers, strings, enums, colors, style wrappers (implicit operators).</summary>
		public static bool TryConvert(object value, Type targetType, out object result)
		{
			result = value;
			if (targetType == null || targetType == typeof(object))
			{
				return true;
			}

			Type underlying = Nullable.GetUnderlyingType(targetType);
			if (value == null)
			{
				result = targetType == typeof(string) ? "" : targetType.IsValueType && underlying == null ? Activator.CreateInstance(targetType) : null;
				return true;
			}

			if (underlying != null)
			{
				targetType = underlying;
			}

			Type sourceType = value.GetType();
			if (targetType.IsAssignableFrom(sourceType))
			{
				return true;
			}

			try
			{
				if (targetType == typeof(string))
				{
					result = value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
					return true;
				}

				if (targetType.IsEnum)
				{
					if (value is string enumName)
					{
						object parsed;
						if (Enum.TryParse(targetType, enumName.Trim(), true, out parsed))
						{
							result = parsed;
							return true;
						}

						return false;
					}

					result = Enum.ToObject(targetType, Convert.ChangeType(value, Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture));
					return true;
				}

				if (targetType == typeof(OpenTK.Mathematics.Color4) && value is string colorText)
				{
					OpenTK.Mathematics.Color4 color;
					if (StyleColorUtility.TryParse(colorText, out color))
					{
						result = color;
						return true;
					}

					return false;
				}

				if (targetType == typeof(bool) && value is string boolText)
				{
					bool b;
					if (bool.TryParse(boolText.Trim(), out b))
					{
						result = b;
						return true;
					}

					double number;
					if (double.TryParse(boolText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
					{
						result = number != 0;
						return true;
					}

					return false;
				}

				if (IsNumeric(targetType))
				{
					if (value is string text)
					{
						double parsed;
						if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
						{
							return false;
						}

						value = parsed;
					}
					else if (value is bool flag)
					{
						value = flag ? 1 : 0;
					}
					else if (value is Enum)
					{
						value = Convert.ChangeType(value, Enum.GetUnderlyingType(sourceType), CultureInfo.InvariantCulture);
					}

					result = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
					return true;
				}

				// Style wrappers and other types with an implicit conversion from the value's type.
				MethodInfo implicitOperator = FindImplicit(sourceType, targetType);
				if (implicitOperator != null)
				{
					result = implicitOperator.Invoke(null, new[] { value });
					return true;
				}

				// Two steps: a number into a wrapper that accepts float (StyleLength from int, StyleFloat from double).
				if (IsNumeric(sourceType))
				{
					MethodInfo fromFloat = FindImplicit(typeof(float), targetType);
					if (fromFloat != null)
					{
						result = fromFloat.Invoke(null, new object[] { Convert.ToSingle(value, CultureInfo.InvariantCulture) });
						return true;
					}
				}

				if (value is string wrapperText)
				{
					// Enum wrappers (StyleEnum<T>) and lengths from text.
					if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(StyleEnum<>))
					{
						Type enumType = targetType.GetGenericArguments()[0];
						object enumValue;
						if (Enum.TryParse(enumType, wrapperText.Replace("-", ""), true, out enumValue))
						{
							result = Activator.CreateInstance(targetType, enumValue);
							return true;
						}
					}

					if (targetType == typeof(StyleLength) || targetType == typeof(Length))
					{
						StyleValue[] values = StyleSheetParser.ParseValue(wrapperText);
						if (values.Length > 0 && (values[0].IsLength || values[0].type == StyleValueType.Keyword))
						{
							Length length = values[0].ToLength();
							result = targetType == typeof(Length) ? length : (object)new StyleLength(length);
							return true;
						}
					}

					if (targetType == typeof(StyleColor))
					{
						OpenTK.Mathematics.Color4 color;
						if (StyleColorUtility.TryParse(wrapperText, out color))
						{
							result = new StyleColor(color);
							return true;
						}
					}
				}

				if (value is IConvertible)
				{
					result = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
					return true;
				}
			}
			catch (Exception)
			{
				return false;
			}

			return false;
		}

		#endregion

		#region PrivateMethods

		private static bool IsNumeric(Type type)
		{
			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Byte:
				case TypeCode.SByte:
				case TypeCode.Int16:
				case TypeCode.UInt16:
				case TypeCode.Int32:
				case TypeCode.UInt32:
				case TypeCode.Int64:
				case TypeCode.UInt64:
				case TypeCode.Single:
				case TypeCode.Double:
				case TypeCode.Decimal:
					return true;
				default:
					return false;
			}
		}

		private static MethodInfo FindImplicit(Type from, Type to)
		{
			MethodInfo method;
			lock (implicitCache)
			{
				if (implicitCache.TryGetValue((from, to), out method))
				{
					return method;
				}
			}

			method = null;
			foreach (Type holder in new[] { to, from })
			{
				foreach (MethodInfo candidate in holder.GetMethods(BindingFlags.Static | BindingFlags.Public))
				{
					if (candidate.Name != "op_Implicit" || candidate.ReturnType != to)
					{
						continue;
					}

					ParameterInfo[] parameters = candidate.GetParameters();
					if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(from))
					{
						method = candidate;
						break;
					}
				}

				if (method != null) break;
			}

			lock (implicitCache)
			{
				implicitCache[(from, to)] = method;
			}

			return method;
		}

		#endregion
	}
}
