using System;
using System.Collections.Generic;
using System.Reflection;

namespace LELEngine
{
	/// <summary>
	///     Resolves component and serialized type names. Game assemblies loaded into their own (collectible)
	///     load context are not visible to <see cref="Type.GetType(string)" />, so hosts register them here and
	///     unregister them before the context is unloaded.
	/// </summary>
	public static class TypeRegistry
	{
		#region PrivateFields

		private static readonly List<Assembly> assemblies = new List<Assembly> { typeof(TypeRegistry).Assembly };

		#endregion

		#region PublicMethods

		public static IReadOnlyList<Assembly> Assemblies => assemblies;

		public static void Register(Assembly assembly)
		{
			if (assembly != null && !assemblies.Contains(assembly))
			{
				assemblies.Add(assembly);
			}
		}

		public static void Unregister(Assembly assembly)
		{
			if (assembly != typeof(TypeRegistry).Assembly)
			{
				assemblies.Remove(assembly);
			}
		}

		/// <summary>Stable name written to files: "Namespace.Type, Assembly" (no version, culture or key).</summary>
		public static string GetName(Type type)
		{
			return type.FullName + ", " + type.Assembly.GetName().Name;
		}

		/// <summary>Type from "Namespace.Type", "Namespace.Type, Assembly" or a full assembly-qualified name.</summary>
		public static Type Resolve(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return null;
			}

			string typeName = name;
			string assemblyName = null;
			int comma = name.IndexOf(',');
			if (comma >= 0)
			{
				typeName = name.Substring(0, comma).Trim();
				string rest = name.Substring(comma + 1);
				int nextComma = rest.IndexOf(',');
				assemblyName = (nextComma >= 0 ? rest.Substring(0, nextComma) : rest).Trim();
			}

			foreach (Assembly assembly in assemblies)
			{
				if (assemblyName != null && !string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
				{
					continue;
				}

				Type type = assembly.GetType(typeName, false);
				if (type != null)
				{
					return type;
				}
			}

			// Engine-side and statically referenced assemblies (standalone hosts).
			return Type.GetType(name, false);
		}

		#endregion
	}
}
