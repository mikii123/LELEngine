using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace LELEngine.Editor
{
	/// <summary>
	///     Writes the C# projects of a game project, so the scripts can be edited (and debugged, by attaching to the
	///     editor) in any IDE: &lt;Name&gt;.csproj compiling Assets/**/*.cs except Editor and Tests folders into Game.dll
	///     against the editor's engine assemblies, &lt;Name&gt;.Editor.csproj compiling the scripts of every Editor folder
	///     (Assets/**/Editor/**/*.cs: custom inspectors, editor windows, menu items) into Game.Editor.dll against the
	///     game scripts, the editor and Dear ImGui, &lt;Name&gt;.Tests.csproj compiling the scripts of every Tests folder
	///     (Assets/**/Tests/**/*.cs: unit tests, also inside an Editor folder) into Game.Tests.dll against the game
	///     scripts, the engine and the test framework, &lt;Name&gt;.sln, and Directory.Build.props moving obj/ into
	///     Library/. Editor scripts and tests never reach a player build. The files are regenerated when they change
	///     (e.g. the editor moved); NuGet packages go into &lt;Name&gt;.packages.props, which is never overwritten.
	/// </summary>
	internal static class ProjectGenerator
	{
		#region PublicFields

		public const string AssemblyName = "Game";
		public const string EditorAssemblyName = "Game.Editor";
		public const string TestsAssemblyName = "Game.Tests";

		/// <summary>Folder name that moves the scripts below it into the editor assembly (any depth under Assets).</summary>
		public const string EditorFolderName = "Editor";

		/// <summary>
		///     Folder name that moves the scripts below it into the test assembly (any depth under Assets; it wins over an
		///     Editor folder around or inside it).
		/// </summary>
		public const string TestsFolderName = "Tests";

		/// <summary>The test framework the tests compile against (next to the editor).</summary>
		public const string TestFrameworkAssembly = "LELEngine.TestFramework.dll";

		#endregion

		#region PublicMethods

		public static string ProjectName(ProjectInfo project)
		{
			var builder = new StringBuilder();
			foreach (char c in project.Name)
			{
				if (char.IsLetterOrDigit(c) || c == '_' || c == '.')
				{
					builder.Append(c);
				}
			}

			return builder.Length > 0 ? builder.ToString() : "Game";
		}

		public static string ProjectFile(ProjectInfo project)
		{
			return Path.Combine(project.RootPath, ProjectName(project) + ".csproj");
		}

		/// <summary>The project of the editor scripts (next to the game project).</summary>
		public static string EditorProjectFile(ProjectInfo project)
		{
			return EditorProjectFile(ProjectFile(project));
		}

		public static string EditorProjectFile(string projectFile)
		{
			return Path.Combine(Path.GetDirectoryName(projectFile), Path.GetFileNameWithoutExtension(projectFile) + ".Editor.csproj");
		}

		/// <summary>The project of the tests (next to the game project).</summary>
		public static string TestsProjectFile(ProjectInfo project)
		{
			return TestsProjectFile(ProjectFile(project));
		}

		public static string TestsProjectFile(string projectFile)
		{
			return Path.Combine(Path.GetDirectoryName(projectFile), Path.GetFileNameWithoutExtension(projectFile) + ".Tests.csproj");
		}

		public static string SolutionFile(ProjectInfo project)
		{
			return Path.Combine(project.RootPath, ProjectName(project) + ".sln");
		}

		/// <summary>True when the script lies in an Editor folder (somewhere under Assets) and in no Tests folder.</summary>
		public static bool IsEditorScript(ProjectInfo project, string file)
		{
			return InFolder(project, file, EditorFolderName) && !IsTestScript(project, file);
		}

		/// <summary>True when the script lies in a Tests folder (somewhere under Assets).</summary>
		public static bool IsTestScript(ProjectInfo project, string file)
		{
			return InFolder(project, file, TestsFolderName);
		}

		public static void Generate(ProjectInfo project)
		{
			string name = ProjectName(project);
			WriteIfChanged(ProjectFile(project), Csproj(name));
			WriteIfChanged(EditorProjectFile(project), EditorCsproj(name));
			WriteIfChanged(TestsProjectFile(project), TestsCsproj(name));
			WriteIfChanged(SolutionFile(project), Sln(name));
			WriteIfChanged(Path.Combine(project.RootPath, "Directory.Build.props"), BuildProps());

			string packages = Path.Combine(project.RootPath, name + ".packages.props");
			if (!File.Exists(packages))
			{
				File.WriteAllText(packages,
					"<Project>\n  <!-- NuGet packages of the game scripts. Example:\n" +
					"  <ItemGroup>\n    <PackageReference Include=\"Some.Package\" Version=\"1.2.3\" />\n  </ItemGroup>\n" +
					"  This file is yours: the editor never overwrites it. -->\n</Project>\n");
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>A folder of the file's path below Assets has this name.</summary>
		private static bool InFolder(ProjectInfo project, string file, string folderName)
		{
			string relative = Path.GetRelativePath(project.AssetsPath, file);
			string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			for (int i = 0; i < parts.Length - 1; i++)
			{
				if (string.Equals(parts[i], folderName, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		private static string Csproj(string name)
		{
			string editor = AppContext.BaseDirectory;
			var references = new List<string> { "LELEngine.dll" };
			references.AddRange(Directory.GetFiles(editor, "OpenTK*.dll").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

			var builder = new StringBuilder();
			builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
			builder.AppendLine();
			builder.AppendLine("  <!-- Generated by the LELEngine editor; it is rewritten when the editor needs other settings.");
			builder.AppendLine("       Add NuGet packages in " + name + ".packages.props. -->");
			builder.AppendLine();
			AppendProperties(builder, AssemblyName, name, false);
			builder.AppendLine("  <!-- Scripts in Editor folders belong to " + name + ".Editor.csproj, scripts in Tests folders to " + name + ".Tests.csproj. -->");
			builder.AppendLine("  <ItemGroup>");
			builder.AppendLine("    <Compile Include=\"Assets\\**\\*.cs\" Exclude=\"Assets\\**\\" + EditorFolderName + "\\**\\*.cs;Assets\\**\\" + TestsFolderName + "\\**\\*.cs\" />");
			builder.AppendLine("  </ItemGroup>");
			builder.AppendLine();
			AppendReferences(builder, editor, references, "Engine assemblies of the editor (not copied: the editor and the player provide them).");
			builder.AppendLine("  <Import Project=\"" + Escape(name) + ".packages.props\" Condition=\"Exists('" + Escape(name) + ".packages.props')\" />");
			builder.AppendLine();
			builder.AppendLine("</Project>");
			return builder.ToString();
		}

		/// <summary>Editor scripts: the game scripts, the engine, the editor's public API and Dear ImGui.</summary>
		private static string EditorCsproj(string name)
		{
			string editor = AppContext.BaseDirectory;
			var references = new List<string> { "LELEngine.dll", "LELEngine.Editor.dll" };
			references.AddRange(Directory.GetFiles(editor, "Hexa*.dll").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
			references.AddRange(Directory.GetFiles(editor, "OpenTK*.dll").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

			var builder = new StringBuilder();
			builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
			builder.AppendLine();
			builder.AppendLine("  <!-- Generated by the LELEngine editor; it is rewritten when the editor needs other settings.");
			builder.AppendLine("       Editor-only scripts (custom inspectors, editor windows, menu items): every .cs file in a folder named");
			builder.AppendLine("       Editor under Assets. They run in the editor only and never reach a player build. -->");
			builder.AppendLine();
			AppendProperties(builder, EditorAssemblyName, name + ".Editor", true);
			builder.AppendLine("  <!-- Tests folders inside Editor folders belong to " + name + ".Tests.csproj. -->");
			builder.AppendLine("  <ItemGroup>");
			builder.AppendLine("    <Compile Include=\"Assets\\**\\" + EditorFolderName + "\\**\\*.cs\" Exclude=\"Assets\\**\\" + TestsFolderName + "\\**\\*.cs\" />");
			builder.AppendLine("  </ItemGroup>");
			builder.AppendLine();
			AppendGameReference(builder, name);
			AppendReferences(builder, editor, references, "Engine, editor and Dear ImGui assemblies of the editor (not copied: the editor provides them).");
			builder.AppendLine("  <Import Project=\"" + Escape(name) + ".packages.props\" Condition=\"Exists('" + Escape(name) + ".packages.props')\" />");
			builder.AppendLine();
			builder.AppendLine("</Project>");
			return builder.ToString();
		}

		/// <summary>Tests: the game scripts, the engine and the test framework (no editor: tests see what the game sees).</summary>
		private static string TestsCsproj(string name)
		{
			string editor = AppContext.BaseDirectory;
			var references = new List<string> { "LELEngine.dll", TestFrameworkAssembly };
			references.AddRange(Directory.GetFiles(editor, "OpenTK*.dll").Select(Path.GetFileName).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

			var builder = new StringBuilder();
			builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
			builder.AppendLine();
			builder.AppendLine("  <!-- Generated by the LELEngine editor; it is rewritten when the editor needs other settings.");
			builder.AppendLine("       Unit tests: every .cs file in a folder named Tests under Assets ([Test] methods, using LELEngine.Testing).");
			builder.AppendLine("       They run in the editor (Window > Test Runner, batch mode -runTests) and never reach a player build. -->");
			builder.AppendLine();
			AppendProperties(builder, TestsAssemblyName, name + ".Tests", true);
			builder.AppendLine("  <ItemGroup>");
			builder.AppendLine("    <Compile Include=\"Assets\\**\\" + TestsFolderName + "\\**\\*.cs\" />");
			builder.AppendLine("  </ItemGroup>");
			builder.AppendLine();
			AppendGameReference(builder, name);
			AppendReferences(builder, editor, references, "Engine and test framework assemblies of the editor (not copied: the editor provides them).");
			builder.AppendLine("  <Import Project=\"" + Escape(name) + ".packages.props\" Condition=\"Exists('" + Escape(name) + ".packages.props')\" />");
			builder.AppendLine();
			builder.AppendLine("</Project>");
			return builder.ToString();
		}

		private static void AppendGameReference(StringBuilder builder, string name)
		{
			builder.AppendLine("  <!-- The game scripts (already in the output folder: not copied). -->");
			builder.AppendLine("  <ItemGroup>");
			builder.AppendLine("    <ProjectReference Include=\"" + Escape(name) + ".csproj\">");
			builder.AppendLine("      <Private>false</Private>");
			builder.AppendLine("    </ProjectReference>");
			builder.AppendLine("  </ItemGroup>");
			builder.AppendLine();
		}

		private static void AppendProperties(StringBuilder builder, string assemblyName, string rootNamespace, bool allowUnsafe)
		{
			builder.AppendLine("  <PropertyGroup>");
			builder.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
			builder.AppendLine("    <AssemblyName>" + assemblyName + "</AssemblyName>");
			builder.AppendLine("    <RootNamespace>" + Escape(rootNamespace) + "</RootNamespace>");
			builder.AppendLine("    <Nullable>disable</Nullable>");
			builder.AppendLine("    <ImplicitUsings>disable</ImplicitUsings>");
			builder.AppendLine("    <LangVersion>latest</LangVersion>");
			if (allowUnsafe)
			{
				builder.AppendLine("    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>");
			}

			builder.AppendLine("    <EnableDefaultItems>false</EnableDefaultItems>");
			builder.AppendLine("    <OutputPath>Library\\ScriptAssemblies\\</OutputPath>");
			builder.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
			builder.AppendLine("    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>");
			builder.AppendLine("    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>");
			builder.AppendLine("    <GenerateDependencyFile>false</GenerateDependencyFile>");
			builder.AppendLine("    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>");
			builder.AppendLine("    <DebugType>portable</DebugType>");
			builder.AppendLine("  </PropertyGroup>");
			builder.AppendLine();
		}

		private static void AppendReferences(StringBuilder builder, string folder, List<string> references, string comment)
		{
			builder.AppendLine("  <!-- " + comment + " -->");
			builder.AppendLine("  <ItemGroup>");
			foreach (string reference in references)
			{
				builder.AppendLine("    <Reference Include=\"" + Escape(Path.GetFileNameWithoutExtension(reference)) + "\">");
				builder.AppendLine("      <HintPath>" + Escape(Path.Combine(folder, reference)) + "</HintPath>");
				builder.AppendLine("      <Private>false</Private>");
				builder.AppendLine("    </Reference>");
			}

			builder.AppendLine("  </ItemGroup>");
			builder.AppendLine();
		}

		/// <summary>One obj folder per project: both projects live in the project root and would share it otherwise.</summary>
		private static string BuildProps()
		{
			return "<Project>\n  <!-- Generated by the LELEngine editor: intermediate files go to Library/obj/<project>/. -->\n" +
				"  <PropertyGroup>\n" +
				"    <BaseIntermediateOutputPath>$(MSBuildThisFileDirectory)Library\\obj\\$(MSBuildProjectName)\\</BaseIntermediateOutputPath>\n" +
				"    <MSBuildProjectExtensionsPath>$(BaseIntermediateOutputPath)</MSBuildProjectExtensionsPath>\n" +
				"  </PropertyGroup>\n</Project>\n";
		}

		private static string Sln(string name)
		{
			string projectGuid = StableGuid(name).ToString("B").ToUpperInvariant();
			string editorGuid = StableGuid(name + ".Editor").ToString("B").ToUpperInvariant();
			string testsGuid = StableGuid(name + ".Tests").ToString("B").ToUpperInvariant();
			const string csharp = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
			var builder = new StringBuilder();
			builder.Append("\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n");
			builder.Append("Project(\"" + csharp + "\") = \"" + name + "\", \"" + name + ".csproj\", \"" + projectGuid + "\"\nEndProject\n");
			builder.Append("Project(\"" + csharp + "\") = \"" + name + ".Editor\", \"" + name + ".Editor.csproj\", \"" + editorGuid + "\"\nEndProject\n");
			builder.Append("Project(\"" + csharp + "\") = \"" + name + ".Tests\", \"" + name + ".Tests.csproj\", \"" + testsGuid + "\"\nEndProject\n");
			builder.Append("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\t\tRelease|Any CPU = Release|Any CPU\n\tEndGlobalSection\n");
			builder.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n");
			foreach (string guid in new[] { projectGuid, editorGuid, testsGuid })
			{
				builder.Append("\t\t" + guid + ".Debug|Any CPU.ActiveCfg = Debug|Any CPU\n\t\t" + guid + ".Debug|Any CPU.Build.0 = Debug|Any CPU\n");
				builder.Append("\t\t" + guid + ".Release|Any CPU.ActiveCfg = Release|Any CPU\n\t\t" + guid + ".Release|Any CPU.Build.0 = Release|Any CPU\n");
			}

			builder.Append("\tEndGlobalSection\nEndGlobal\n");
			return builder.ToString();
		}

		private static Guid StableGuid(string text)
		{
			using (var md5 = System.Security.Cryptography.MD5.Create())
			{
				return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes("LELEngine:" + text)));
			}
		}

		private static void WriteIfChanged(string path, string content)
		{
			if (File.Exists(path) && File.ReadAllText(path) == content)
			{
				return;
			}

			File.WriteAllText(path, content);
		}

		private static string Escape(string text)
		{
			return SecurityElement.Escape(text);
		}

		#endregion
	}
}
