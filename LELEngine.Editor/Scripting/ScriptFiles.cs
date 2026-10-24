using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LELEngine.Editor
{
	/// <summary>
	///     The project's script file declaring a class (components, ScriptableObjects): "ClassName.cs", else the project
	///     script containing "class ClassName". Engine classes have none. Cached while the project's files stay the same.
	/// </summary>
	internal static class ScriptFiles
	{
		#region PrivateFields

		private static readonly Dictionary<string, string> scripts = new Dictionary<string, string>(StringComparer.Ordinal);
		private static int scriptsVersion = -1;

		#endregion

		#region PublicMethods

		/// <summary>The asset path of the script declaring <paramref name="type" />; null when there is none.</summary>
		public static string Find(Type type)
		{
			int version = EditorApplication.Instance?.AssetsVersion ?? 0;
			if (version != scriptsVersion)
			{
				scriptsVersion = version;
				scripts.Clear();
			}

			if (scripts.TryGetValue(type.Name, out string cached))
			{
				return cached;
			}

			string path = AssetDatabase.Resolve(type.Name + ".cs", null);
			if (path == null || !File.Exists(AssetDatabase.ToAbsolute(path)))
			{
				path = null;
				Regex declaration = Declaration(type);
				foreach (string script in AssetDatabase.GetAllAssetPaths(".cs"))
				{
					try
					{
						if (declaration.IsMatch(File.ReadAllText(AssetDatabase.ToAbsolute(script))))
						{
							path = script;
							break;
						}
					}
					catch (IOException)
					{
						// Unreadable right now: the next files.
					}
				}
			}

			scripts[type.Name] = path;
			return path;
		}

		/// <summary>Opens the script declaring <paramref name="type" /> in the external editor at the class's line; false when there is none.</summary>
		public static bool Open(Type type)
		{
			string path = Find(type);
			if (path == null)
			{
				return false;
			}

			string absolute = AssetDatabase.ToAbsolute(path);
			return ExternalScriptEditor.Open(absolute, DeclarationLine(absolute, type));
		}

		#endregion

		#region PrivateMethods

		private static Regex Declaration(Type type)
		{
			return new Regex(@"\bclass\s+" + Regex.Escape(type.Name) + @"\b");
		}

		/// <summary>The 1-based line declaring the class (1 when it is not found).</summary>
		private static int DeclarationLine(string file, Type type)
		{
			try
			{
				Regex declaration = Declaration(type);
				string[] lines = File.ReadAllLines(file);
				for (int i = 0; i < lines.Length; i++)
				{
					if (declaration.IsMatch(lines[i]))
					{
						return i + 1;
					}
				}
			}
			catch (IOException)
			{
				// Opened at the top.
			}

			return 1;
		}

		#endregion
	}
}
