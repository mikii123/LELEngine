using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using LELEngine.Serialization;
using Mono.Cecil;

namespace LELEngine.Editor
{
	/// <summary>
	///     The assets a game build ships: its scenes, everything in Resources folders (scripts load those by name,
	///     <see cref="Resources" />), and what those use, transitively. A document (scene, material, ScriptableObject,
	///     VFX graph, physics material) uses the assets its values reference, wherever they sit (components, nested and
	///     polymorphic objects, interface slots, lists, dictionaries); a model uses its imported materials; a project
	///     shader its #include files; style sheets and UI documents the files their url() / resource() / src name.
	///     Everything else is left out.
	///     <para>
	///         It also lists the types the shipped documents store (components, ScriptableObjects, inline objects of
	///         polymorphic fields): <see cref="CheckTypes" /> reports those the player does not have.
	///     </para>
	/// </summary>
	internal sealed class BuildDependencies
	{
		#region PublicFields

		/// <summary>Asset paths (root-relative) of the files the build ships.</summary>
		public readonly HashSet<string> Included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Why each file is included: "build scene", "Resources", or the asset that uses it.</summary>
		public readonly Dictionary<string, string> Reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		///     The engine resources the build uses (engine paths of built-in meshes, materials, shaders) with the asset
		///     using each: the player has them next to its binaries, so they are not in <see cref="Included" />; what they
		///     use is followed all the same (a built-in model's default material, a material's shader variant).
		/// </summary>
		public readonly Dictionary<string, string> EngineResources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Stored type names and the first included document storing each.</summary>
		public readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.Ordinal);

		/// <summary>Missing references and unreadable files.</summary>
		public readonly List<string> Warnings = new List<string>();

		public int SceneCount;
		public int ResourceCount;

		#endregion

		#region PrivateFields

		private static readonly Regex StyleReference = new Regex(@"(url|resource)\(\s*['""]?([^'""\)]+)['""]?\s*\)", RegexOptions.IgnoreCase);
		private static readonly Regex StyleImport = new Regex(@"@import\s+(?:url\()?\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase);
		private static readonly Regex Include = new Regex(@"^\s*#include\s+""([^""]+)""", RegexOptions.Multiline);

		private readonly Queue<string> pending = new Queue<string>();

		#endregion

		#region PublicMethods

		/// <summary>Collects the files of a build of <paramref name="scenes" /> (asset paths) from the project's Assets folder.</summary>
		public static BuildDependencies Collect(ProjectInfo project, IEnumerable<string> scenes)
		{
			var result = new BuildDependencies();
			foreach (string scene in scenes)
			{
				if (result.Add(scene, "build scene"))
				{
					result.SceneCount++;
				}
			}

			foreach (string file in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories))
			{
				string assetPath = AssetDatabase.ToAssetPath(file);
				// A Resources folder inside a Tests folder holds test data: never shipped for being there.
				if (assetPath != null && IsShippable(assetPath) && Resources.IsInResources(assetPath) && !IsTestAsset(assetPath) && result.Add(assetPath, "Resources"))
				{
					result.ResourceCount++;
				}
			}

			// The input configuration Project Settings > Input names (the player loads it on its first frame).
			string input = ProjectSettings.Current.Input.Configuration;
			if (!string.IsNullOrEmpty(input) && File.Exists(AssetDatabase.ToAbsolute(input)))
			{
				result.Add(input, "Project Settings > Input");
			}

			while (result.pending.Count > 0)
			{
				string assetPath = result.pending.Dequeue();
				try
				{
					result.AddDependencies(assetPath);
				}
				catch (Exception e)
				{
					result.Warnings.Add(assetPath + " could not be read for its dependencies (" + e.Message + ")");
				}
			}

			return result;
		}

		/// <summary>Files the build never ships themselves: scripts, meta files (they go with their asset), dot files.</summary>
		/// <summary>The asset lies in a Tests folder (any depth): the tests' scripts and data.</summary>
		public static bool IsTestAsset(string assetPath)
		{
			string[] parts = assetPath.Split('/', '\\');
			for (int i = 0; i < parts.Length - 1; i++)
			{
				if (string.Equals(parts[i], ProjectGenerator.TestsFolderName, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		public static bool IsShippable(string assetPath)
		{
			string name = Path.GetFileName(assetPath);
			return !name.StartsWith(".", StringComparison.Ordinal)
				&& !assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
				&& !assetPath.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		///     Checks every stored type name against what the player loads: the engine and the scripts it was built
		///     with (<paramref name="playerScripts" />: the folder with the player's Game.dll and its packages, null
		///     without scripts). Returns one message per type the player lacks.
		/// </summary>
		public List<string> CheckTypes(string playerScripts)
		{
			var known = new HashSet<string>(StringComparer.Ordinal);
			foreach (Type type in typeof(EngineObject).Assembly.GetTypes())
			{
				known.Add(TypeRegistry.GetName(type));
			}

			var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { typeof(EngineObject).Assembly.GetName().Name };
			if (playerScripts != null && Directory.Exists(playerScripts))
			{
				foreach (string dll in Directory.GetFiles(playerScripts, "*.dll"))
				{
					string name = Path.GetFileNameWithoutExtension(dll);
					if (assemblies.Contains(name))
					{
						continue;
					}

					try
					{
						using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(dll))
						{
							assemblies.Add(assembly.Name.Name);
							foreach (TypeDefinition type in assembly.MainModule.Types)
							{
								AddTypeNames(type, assembly.Name.Name, known);
							}
						}
					}
					catch (Exception)
					{
						// Native or unreadable: no serializable types there.
					}
				}
			}

			var problems = new List<string>();
			foreach (KeyValuePair<string, string> entry in Types)
			{
				string typeName = entry.Key;
				int comma = typeName.IndexOf(',');
				string assembly = comma >= 0 ? typeName.Substring(comma + 1).Trim() : null;
				string normalized = comma >= 0 ? typeName.Substring(0, comma).Trim() + ", " + assembly : typeName;
				if (known.Contains(normalized))
				{
					continue;
				}

				string why = assembly != null && assembly.EndsWith(".Editor", StringComparison.OrdinalIgnoreCase)
					? "it is an editor script (Editor folders do not go into builds)"
					: assembly != null && !assemblies.Contains(assembly)
						? "its assembly " + assembly + " is not part of the build"
						: "the class is missing (deleted, renamed, or not compiled into the player)";
				problems.Add($"{entry.Value} stores the type '{typeName}', which the player does not have: {why}. Its data loads as a missing script.");
			}

			return problems;
		}

		/// <summary>
		///     The shader variants the build's materials draw with (each material's shader and the keywords of its values),
		///     shipped materials and the built-in ones they use alike: the build compiles them and lists them for the player
		///     (<see cref="Shaders.ShaderVariantList" />).
		/// </summary>
		public List<Shaders.ShaderVariant> CollectShaderVariants()
		{
			var variants = new List<Shaders.ShaderVariant>();
			var paths = new List<string>(Included);
			paths.AddRange(EngineResources.Keys);
			paths.Sort(StringComparer.OrdinalIgnoreCase);
			foreach (string path in paths)
			{
				if (!path.EndsWith(Shaders.Material.Extension, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				Shaders.Material material = InternalStorage.GetOrCreateMaterial(path);
				if (material?.UsingShader?.AssetPath == null)
				{
					continue;
				}

				Shaders.ShaderVariant variant = Shaders.ShaderVariant.Of(material);
				if (!variants.Contains(variant))
				{
					variants.Add(variant);
				}
			}

			return variants;
		}

		/// <summary>The report written next to the project's other build data: every shipped file with its reason, then the files left out.</summary>
		public string Report(ProjectInfo project)
		{
			var text = new StringBuilder();
			var included = new List<string>(Included);
			included.Sort(StringComparer.OrdinalIgnoreCase);
			text.Append("Included assets (").Append(included.Count).Append(")\n");
			foreach (string path in included)
			{
				text.Append("  ").Append(path).Append("    <- ").Append(Reasons.TryGetValue(path, out string reason) ? reason : "?").Append('\n');
			}

			var excluded = new List<string>();
			foreach (string file in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories))
			{
				string assetPath = AssetDatabase.ToAssetPath(file);
				if (assetPath != null && IsShippable(assetPath) && !Included.Contains(assetPath))
				{
					excluded.Add(assetPath);
				}
			}

			var engine = new List<string>(EngineResources.Keys);
			engine.Sort(StringComparer.OrdinalIgnoreCase);
			text.Append("\nBuilt-in engine resources used (the player has them) (").Append(engine.Count).Append(")\n");
			foreach (string path in engine)
			{
				text.Append("  ").Append(path).Append("    <- ").Append(EngineResources[path]).Append('\n');
			}

			excluded.Sort(StringComparer.OrdinalIgnoreCase);
			text.Append("\nLeft out: not used by the build's scenes, not in a Resources folder (").Append(excluded.Count).Append(")\n");
			foreach (string path in excluded)
			{
				text.Append("  ").Append(path).Append('\n');
			}

			if (Warnings.Count > 0)
			{
				text.Append("\nWarnings\n");
				foreach (string warning in Warnings)
				{
					text.Append("  ").Append(warning).Append('\n');
				}
			}

			return text.ToString();
		}

		#endregion

		#region PrivateMethods

		/// <summary>Queues a file (once); false when it was included already or does not exist.</summary>
		private bool Add(string assetPath, string reason)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return false;
			}

			assetPath = assetPath.Replace('\\', '/').TrimStart('/');
			if (AssetDatabase.IsEngineResource(assetPath))
			{
				if (EngineResources.TryAdd(assetPath, reason))
				{
					pending.Enqueue(assetPath);
				}

				return false;
			}

			if (Included.Contains(assetPath) || !IsShippable(assetPath))
			{
				return false;
			}

			if (!File.Exists(AssetDatabase.ToAbsolute(assetPath)))
			{
				Warnings.Add($"{reason}: '{assetPath}' does not exist");
				return false;
			}

			Included.Add(assetPath);
			Reasons[assetPath] = reason;
			pending.Enqueue(assetPath);
			return true;
		}

		private void AddDependencies(string assetPath)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			string reason = "used by " + assetPath;
			if (Documents.IsDocumentFile(assetPath))
			{
				byte[] data = File.ReadAllBytes(absolute);
				if (!Documents.IsDocument(data))
				{
					return;
				}

				DocumentReferences references = DocumentReferences.Collect(data);
				foreach (string typeName in references.TypeNames)
				{
					Types.TryAdd(typeName, assetPath);
				}

				foreach (AssetReference reference in references.Assets)
				{
					string target = ResolveReference(reference);
					if (target == null)
					{
						// An engine resource, or a project copy of an engine shader that is gone (the engine's stands in).
						string engine = reference.Path == null ? null : AssetDatabase.IsEngineResource(reference.Path) ? reference.Path : AssetDatabase.EngineShaderFallback(reference.Path);
						if (engine == null)
						{
							Warnings.Add($"{assetPath} references a missing asset (guid {reference.Guid ?? "none"}, last path '{reference.Path}')");
						}

						Add(engine, reason);
						continue;
					}

					Add(target, reason);
				}

				return;
			}

			if (ModelAssets.IsModelFile(assetPath))
			{
				// Instantiating the model (at runtime too: a model in Resources) uses its imported materials.
				ModelData model = ModelAssets.LoadData(assetPath);
				bool needsDefault = false;
				foreach (ModelMeshData mesh in model.Meshes)
				{
					foreach (int index in mesh.Materials)
					{
						string material = index >= 0 && index < model.Materials.Count ? model.Materials[index].AssetPath : null;
						if (!string.IsNullOrEmpty(material) && File.Exists(AssetDatabase.ToAbsolute(material)))
						{
							Add(material, reason);
						}
						else
						{
							needsDefault = true;
						}
					}
				}

				if (needsDefault && File.Exists(AssetDatabase.ToAbsolute(Model.DefaultMaterialPath)))
				{
					Add(Model.DefaultMaterialPath, reason);
				}

				return;
			}

			string extension = Path.GetExtension(assetPath).ToLowerInvariant();
			switch (extension)
			{
				case ".shader":
				case ".glsl":
				case ".vert":
				case ".frag":
				case ".comp":
					foreach (Match match in Include.Matches(File.ReadAllText(absolute)))
					{
						AddShaderInclude(match.Groups[1].Value, absolute, reason);
					}

					break;
				case ".uss":
				{
					string text = File.ReadAllText(absolute);
					foreach (Match match in StyleReference.Matches(text))
					{
						AddStyleReference(match.Groups[1].Value, match.Groups[2].Value.Trim(), assetPath, reason);
					}

					foreach (Match match in StyleImport.Matches(text))
					{
						Add(UIElements.VisualTreeAsset.ResolveSourcePath(match.Groups[1].Value, assetPath), reason);
					}

					break;
				}
				case ".uxml":
					AddUxmlReferences(absolute, assetPath, reason);
					break;
			}
		}

		private static string ResolveReference(AssetReference reference)
		{
			string path = reference.Guid != null ? AssetDatabase.GetPath(reference.Guid) : null;
			if (path == null && reference.Path != null && !AssetDatabase.IsEngineResource(reference.Path))
			{
				path = AssetDatabase.Resolve(reference.Path, null);
			}

			return path;
		}

		/// <summary>An #include of a project shader: engine library files are engine resources (shipped with the player), project files are included.</summary>
		private void AddShaderInclude(string name, string includingFile, string reason)
		{
			if (File.Exists(Path.Combine(Shaders.ShaderProgram.ShadersRoot, name)))
			{
				return;
			}

			string relative = Path.Combine(Path.GetDirectoryName(includingFile) ?? "", name);
			string assetPath = File.Exists(relative) ? AssetDatabase.ToAssetPath(relative) : AssetDatabase.Resolve(name, "Shaders");
			if (assetPath != null)
			{
				Add(assetPath, reason);
			}
		}

		/// <summary>A url() (relative to the sheet, or "/..." / "project:/..." from the root) or resource() (by name, any extension) of a style sheet.</summary>
		private void AddStyleReference(string function, string path, string sheet, string reason)
		{
			if (string.Equals(function, "resource", StringComparison.OrdinalIgnoreCase))
			{
				string normalized = path.Replace('\\', '/').TrimStart('/');
				string resolved = AssetDatabase.Resolve(normalized, null);
				foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".ttf", ".otf", ".uss" })
				{
					resolved ??= AssetDatabase.Resolve(normalized + extension, null);
				}

				Add(resolved, reason);
				return;
			}

			Add(UIElements.VisualTreeAsset.ResolveSourcePath(path, sheet), reason);
		}

		/// <summary>The files a UI document names: src of Style / Template / Instance elements, url() / resource() in style attributes.</summary>
		private void AddUxmlReferences(string absolute, string assetPath, string reason)
		{
			var document = new XmlDocument { XmlResolver = null };
			document.Load(absolute);
			foreach (XmlNode node in document.SelectNodes("//*"))
			{
				if (node.Attributes == null)
				{
					continue;
				}

				foreach (XmlAttribute attribute in node.Attributes)
				{
					// <Style src>, <Template src> (an <Instance template> names a Template of the same document).
					if (attribute.Name == "src")
					{
						Add(UIElements.VisualTreeAsset.ResolveSourcePath(attribute.Value, assetPath), reason);
					}
					else if (attribute.Value.IndexOf('(') >= 0)
					{
						foreach (Match match in StyleReference.Matches(attribute.Value))
						{
							AddStyleReference(match.Groups[1].Value, match.Groups[2].Value.Trim(), assetPath, reason);
						}
					}
				}
			}
		}

		private static void AddTypeNames(TypeDefinition type, string assembly, HashSet<string> names)
		{
			names.Add(type.FullName.Replace('/', '+') + ", " + assembly);
			foreach (TypeDefinition nested in type.NestedTypes)
			{
				AddTypeNames(nested, assembly, names);
			}
		}

		#endregion
	}
}
