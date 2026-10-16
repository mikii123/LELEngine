using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>Marks a VisualElement class as usable from UXML (by its class name, or the given name).</summary>
	[AttributeUsage(AttributeTargets.Class, Inherited = false)]
	public sealed class UxmlElementAttribute : Attribute
	{
		public string name { get; }

		public UxmlElementAttribute(string name = null)
		{
			this.name = name;
		}
	}

	/// <summary>
	///     Exposes a property to UXML. The attribute name defaults to the kebab-case of the member name
	///     (lowValue -> low-value). Supported types: string, bool, numbers, enums, Color4, Length, vectors, string lists
	///     and the UI asset types (paths).
	/// </summary>
	[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
	public sealed class UxmlAttributeAttribute : Attribute
	{
		public string name { get; }

		public UxmlAttributeAttribute(string name = null)
		{
			this.name = name;
		}
	}

	/// <summary>Element types and their UXML attributes, found by reflection over the registered assemblies.</summary>
	public static class UxmlRegistry
	{
		#region PrivateFields

		private static readonly object sync = new object();
		private static Dictionary<string, Type> typesByName;
		private static readonly Dictionary<Type, UxmlTypeInfo> infos = new Dictionary<Type, UxmlTypeInfo>();

		#endregion

		#region PublicMethods

		/// <summary>The element type for a UXML tag ("Button", "ui:Button", "MyGame.HealthBar").</summary>
		public static Type Resolve(string tagName)
		{
			if (string.IsNullOrEmpty(tagName))
			{
				return null;
			}

			int colon = tagName.IndexOf(':');
			if (colon >= 0)
			{
				tagName = tagName.Substring(colon + 1);
			}

			lock (sync)
			{
				EnsureTypes();
				Type type;
				if (typesByName.TryGetValue(tagName, out type))
				{
					return type;
				}

				// Unity namespaces: "UnityEngine.UIElements.Button" -> "Button".
				int dot = tagName.LastIndexOf('.');
				if (dot >= 0 && typesByName.TryGetValue(tagName.Substring(dot + 1), out type))
				{
					return type;
				}

				Type resolved = TypeRegistry.Resolve(tagName);
				return resolved != null && typeof(VisualElement).IsAssignableFrom(resolved) ? resolved : null;
			}
		}

		public static UxmlTypeInfo GetInfo(Type type)
		{
			lock (sync)
			{
				UxmlTypeInfo info;
				if (!infos.TryGetValue(type, out info))
				{
					info = new UxmlTypeInfo(type);
					infos[type] = info;
				}

				return info;
			}
		}

		/// <summary>Drops the cached types (game assemblies reloaded).</summary>
		public static void Clear()
		{
			lock (sync)
			{
				typesByName = null;
				infos.Clear();
			}
		}

		// ------------------------------------------------------------------ value conversion

		public static bool TryConvert(string text, Type type, string assetPath, out object value)
		{
			value = null;
			if (text == null)
			{
				return false;
			}

			try
			{
				if (type == typeof(string)) { value = text; return true; }
				if (type == typeof(bool)) { bool b; if (!bool.TryParse(text.Trim(), out b)) return false; value = b; return true; }
				if (type == typeof(int)) { value = (int)ParseFloat(text); return true; }
				if (type == typeof(long)) { value = (long)ParseFloat(text); return true; }
				if (type == typeof(float)) { value = ParseFloat(text); return true; }
				if (type == typeof(double)) { value = double.Parse(text.Trim().Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture); return true; }
				if (type == typeof(char)) { value = text.Length > 0 ? text[0] : '\0'; return true; }
				if (type.IsEnum)
				{
					string normalized = Normalize(text);
					foreach (object candidate in Enum.GetValues(type))
					{
						if (Normalize(candidate.ToString()) == normalized)
						{
							value = candidate;
							return true;
						}
					}

					return false;
				}

				if (type == typeof(Color4))
				{
					Color4 color;
					if (StyleColorUtility.TryParse(text, out color))
					{
						value = color;
						return true;
					}

					StyleValue[] values = StyleSheetParser.ParseValue(text);
					if (values.Length > 0 && values[0].type == StyleValueType.Color)
					{
						value = values[0].color;
						return true;
					}

					return false;
				}

				if (type == typeof(Length))
				{
					StyleValue[] values = StyleSheetParser.ParseValue(text);
					if (values.Length > 0 && (values[0].IsLength || values[0].type == StyleValueType.Keyword))
					{
						value = values[0].ToLength();
						return true;
					}

					return false;
				}

				if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Vector2i))
				{
					string[] parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
					float[] numbers = Array.ConvertAll(parts, ParseFloat);
					if (type == typeof(Vector2)) { value = new Vector2(At(numbers, 0), At(numbers, 1)); return true; }
					if (type == typeof(Vector2i)) { value = new Vector2i((int)At(numbers, 0), (int)At(numbers, 1)); return true; }
					if (type == typeof(Vector3)) { value = new Vector3(At(numbers, 0), At(numbers, 1), At(numbers, 2)); return true; }
					value = new Vector4(At(numbers, 0), At(numbers, 1), At(numbers, 2), At(numbers, 3));
					return true;
				}

				if (type == typeof(List<string>) || type == typeof(string[]))
				{
					string[] parts = text.Split(',');
					for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
					value = type == typeof(string[]) ? parts : (object)new List<string>(parts);
					return true;
				}

				if (UIAssets.IsUIAssetType(type))
				{
					string path = VisualTreeAsset.ResolveSourcePath(text, assetPath);
					value = path != null ? UIAssets.Load(type, path) : null;
					return value != null;
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

		private static float At(float[] numbers, int index)
		{
			return index < numbers.Length ? numbers[index] : 0f;
		}

		private static float ParseFloat(string text)
		{
			return float.Parse(text.Trim().Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		/// <summary>Every element type usable from UXML (built-in controls and game types), sorted by name.</summary>
		public static List<Type> ElementTypes()
		{
			lock (sync)
			{
				EnsureTypes();
				var result = new List<Type>();
				foreach (KeyValuePair<string, Type> pair in typesByName)
				{
					if (!pair.Key.Contains('.') && !result.Contains(pair.Value))
					{
						result.Add(pair.Value);
					}
				}

				result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
				return result;
			}
		}

		/// <summary>UXML tag of a type: the short name of built-in types, the full name of game types.</summary>
		public static string TagOf(Type type)
		{
			var attribute = type.GetCustomAttribute<UxmlElementAttribute>(false);
			if (attribute?.name != null)
			{
				return attribute.name;
			}

			return type.Assembly == typeof(VisualElement).Assembly ? type.Name : type.FullName;
		}

		public static string Normalize(string name)
		{
			var builder = new System.Text.StringBuilder(name.Length);
			foreach (char c in name)
			{
				if (c != '-' && c != '_' && c != ' ')
				{
					builder.Append(char.ToLowerInvariant(c));
				}
			}

			return builder.ToString();
		}

		/// <summary>"lowValue" -> "low-value".</summary>
		public static string ToKebab(string name)
		{
			var builder = new System.Text.StringBuilder(name.Length + 4);
			for (int i = 0; i < name.Length; i++)
			{
				char c = name[i];
				if (char.IsUpper(c))
				{
					if (i > 0) builder.Append('-');
					builder.Append(char.ToLowerInvariant(c));
				}
				else
				{
					builder.Append(c);
				}
			}

			return builder.ToString();
		}

		private static void EnsureTypes()
		{
			if (typesByName != null)
			{
				return;
			}

			typesByName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
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
					if (type == null || type.IsAbstract || type.IsGenericTypeDefinition || !typeof(VisualElement).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
					{
						continue;
					}

					var attribute = type.GetCustomAttribute<UxmlElementAttribute>(false);
					string name = attribute?.name ?? type.Name;
					if (!typesByName.ContainsKey(name) || attribute != null)
					{
						typesByName[name] = type;
					}

					typesByName[type.FullName] = type;
				}
			}
		}

		#endregion
	}

	/// <summary>UXML attributes of one element type: name -> setter.</summary>
	public sealed class UxmlTypeInfo
	{
		#region PublicFields

		public readonly Type type;
		public readonly Dictionary<string, UxmlAttributeInfo> attributes = new Dictionary<string, UxmlAttributeInfo>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region Constructors

		public UxmlTypeInfo(Type type)
		{
			this.type = type;
			for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
			{
				foreach (PropertyInfo property in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					var attribute = property.GetCustomAttribute<UxmlAttributeAttribute>(false);
					if (attribute == null || !property.CanWrite)
					{
						continue;
					}

					Add(attribute.name ?? UxmlRegistry.ToKebab(property.Name), new UxmlAttributeInfo(property));
				}

				foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					var attribute = field.GetCustomAttribute<UxmlAttributeAttribute>(false);
					if (attribute == null)
					{
						continue;
					}

					Add(attribute.name ?? UxmlRegistry.ToKebab(field.Name), new UxmlAttributeInfo(field));
				}
			}
		}

		#endregion

		#region PublicMethods

		public bool TryGet(string name, out UxmlAttributeInfo info)
		{
			if (attributes.TryGetValue(name, out info))
			{
				return true;
			}

			// Tolerate "lowValue", "low_value" and "LowValue" in files.
			string normalized = UxmlRegistry.Normalize(name);
			foreach (KeyValuePair<string, UxmlAttributeInfo> pair in attributes)
			{
				if (UxmlRegistry.Normalize(pair.Key) == normalized)
				{
					info = pair.Value;
					return true;
				}
			}

			return false;
		}

		#endregion

		#region PrivateMethods

		private void Add(string name, UxmlAttributeInfo info)
		{
			if (!attributes.ContainsKey(name))
			{
				attributes[name] = info;
			}
		}

		#endregion
	}

	public sealed class UxmlAttributeInfo
	{
		public readonly string memberName;
		public readonly Type valueType;
		private readonly PropertyInfo property;
		private readonly FieldInfo field;

		public UxmlAttributeInfo(PropertyInfo property)
		{
			this.property = property;
			memberName = property.Name;
			valueType = property.PropertyType;
		}

		public UxmlAttributeInfo(FieldInfo field)
		{
			this.field = field;
			memberName = field.Name;
			valueType = field.FieldType;
		}

		public void SetValue(object target, object value)
		{
			if (property != null)
			{
				property.SetValue(target, value);
			}
			else
			{
				field.SetValue(target, value);
			}
		}

		public object GetValue(object target)
		{
			return property != null ? property.GetValue(target) : field.GetValue(target);
		}
	}
}
