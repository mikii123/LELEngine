using System;
using System.Collections.Generic;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Types the inspector offers (Odin-style polymorphic fields: the classes implementing an interface or deriving
	///     from a base class; ScriptableObject classes for the Create menu), cached until the scripts reload, and the
	///     searchable popup choosing one.
	/// </summary>
	internal static class TypeSelector
	{
		#region PrivateFields

		private static readonly Dictionary<Type, List<Type>> concreteTypes = new Dictionary<Type, List<Type>>();
		private static List<Type> scriptableObjectTypes;
		private static int cachedVersion = -1;
		private static string search = "";

		#endregion

		#region PublicMethods

		/// <summary>The classes and structs a slot of <paramref name="baseType" /> can hold by value (see <see cref="SerializationUtility.GetConcreteTypes" />).</summary>
		public static List<Type> ConcreteTypes(Type baseType)
		{
			Validate();
			List<Type> types;
			if (!concreteTypes.TryGetValue(baseType, out types))
			{
				types = SerializationUtility.GetConcreteTypes(baseType);
				concreteTypes[baseType] = types;
			}

			return types;
		}

		/// <summary>
		///     True when a field of the type is polymorphic: an interface, an abstract class, EngineObject, or a class
		///     other creatable classes derive from (the inspector offers a type choice).
		/// </summary>
		public static bool IsPolymorphic(Type declaredType)
		{
			if (declaredType.IsValueType || declaredType == typeof(string) || SerializationUtility.TagOf(declaredType, out _) != ValueTag.Object)
			{
				return false;
			}

			if (declaredType.IsInterface || declaredType.IsAbstract || declaredType == typeof(EngineObject))
			{
				return true;
			}

			List<Type> types = ConcreteTypes(declaredType);
			return types.Count > 1 || (types.Count == 1 && types[0] != declaredType);
		}

		/// <summary>Concrete ScriptableObject classes of the engine and the scripts (the Create menu lists them).</summary>
		public static List<Type> ScriptableObjectTypes()
		{
			Validate();
			if (scriptableObjectTypes != null)
			{
				return scriptableObjectTypes;
			}

			scriptableObjectTypes = new List<Type>();
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
					if (type != null && !type.IsAbstract && (type.IsPublic || type.IsNestedPublic) && typeof(ScriptableObject).IsAssignableFrom(type) && !type.ContainsGenericParameters)
					{
						scriptableObjectTypes.Add(type);
					}
				}
			}

			scriptableObjectTypes.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));
			return scriptableObjectTypes;
		}

		/// <summary>Drops every cached type (before the scripts unload).</summary>
		public static void ClearCache()
		{
			concreteTypes.Clear();
			scriptableObjectTypes = null;
		}

		/// <summary>"Fire Sword" for FireSword (nested types with their outer class).</summary>
		public static string DisplayName(Type type)
		{
			if (type == null)
			{
				return "None";
			}

			string name = SerializationUtility.NicifyName(ObjectField.TypeLabel(type));
			return type.IsNested && type.DeclaringType != null ? ObjectField.TypeLabel(type.DeclaringType) + "." + name : name;
		}

		/// <summary>
		///     The popup <paramref name="popupId" /> (opened by the caller with ImGui.OpenPopup) listing the types a slot of
		///     <paramref name="baseType" /> can hold, with a search box; true when one was chosen (null: None).
		/// </summary>
		public static bool Popup(string popupId, Type baseType, Type current, bool allowNone, out Type chosen)
		{
			chosen = null;
			bool result = false;
			if (!ImGui.BeginPopup(popupId))
			{
				return false;
			}

			if (ImGui.IsWindowAppearing())
			{
				search = "";
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(300f);
			ImGui.InputTextWithHint("##type_search", "Search " + ObjectField.TypeLabel(baseType) + " types", ref search, 128);
			ImGui.Separator();
			if (allowNone && ImGui.Selectable("None", current == null))
			{
				result = true;
				ImGui.CloseCurrentPopup();
			}

			List<Type> types = ConcreteTypes(baseType);
			if (types.Count == 0)
			{
				ImGui.TextDisabled("No class " + (baseType.IsInterface ? "implements " : "derives from ") + ObjectField.TypeLabel(baseType) + ".");
				if (SerializationUtility.CanHoldReference(baseType))
				{
					ImGui.TextDisabled("Drop a component or a ScriptableObject asset on the field.");
				}
			}

			float height = Math.Min(320f, Math.Max(1, types.Count) * ImGui.GetTextLineHeightWithSpacing() + 4f);
			if (types.Count > 0 && ImGui.BeginChild("##types", new NVector2(300f, height)))
			{
				foreach (Type type in types)
				{
					string name = DisplayName(type);
					if (search.Length > 0 && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && (type.FullName ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					if (ImGui.Selectable(name + "##" + type.FullName, type == current))
					{
						chosen = type;
						result = true;
						ImGui.CloseCurrentPopup();
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip(type.FullName + (type.IsDefined(typeof(SerializableAttribute), false) ? "" : "\n(not marked [Serializable]: its fields are saved here, but a field of this exact class would not be)"));
					}

					if (type.Namespace != null)
					{
						ImGui.SameLine();
						ImGui.TextDisabled(type.Namespace);
					}
				}

				ImGui.EndChild();
			}

			ImGui.EndPopup();
			return result;
		}

		#endregion

		#region PrivateMethods

		private static void Validate()
		{
			int version = EditorApplication.Instance?.Scripts?.Version ?? 0;
			if (version != cachedVersion)
			{
				cachedVersion = version;
				ClearCache();
			}
		}

		#endregion
	}
}
