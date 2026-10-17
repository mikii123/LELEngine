using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LELEngine.Editor
{
	internal struct BuildDiagnostic
	{
		public bool IsError;
		public string File;
		public int Line;
		public int Column;
		public string Code;
		public string Message;

		public override string ToString()
		{
			string location = File != null ? File + "(" + Line + "," + Column + "): " : "";
			return location + (IsError ? "error " : "warning ") + Code + ": " + Message;
		}
	}

	internal sealed class BuildResult
	{
		public bool Success;
		public readonly List<BuildDiagnostic> Diagnostics = new List<BuildDiagnostic>();
		public byte[] Assembly;
		public byte[] Symbols;

		/// <summary>Game.Editor.dll (builds that included the editor scripts).</summary>
		public byte[] EditorAssembly;
		public byte[] EditorSymbols;
		public double Seconds;
		public string Output;
		public int ErrorCount => Diagnostics.FindAll(d => d.IsError).Count;
	}

	/// <summary>
	///     Runs "dotnet build" on the game's script project, or on the editor scripts' project (which builds the game
	///     project first), in the calling thread and reads the result.
	/// </summary>
	internal static class ScriptBuilder
	{
		#region PrivateFields

		private static readonly Regex DiagnosticPattern = new Regex(
			@"^\s*(?<file>[^\(\r\n]+?)\((?<line>\d+),(?<column>\d+)\):\s+(?<kind>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(\s+\[[^\]]+\])?\s*$",
			RegexOptions.Compiled);

		private static readonly Regex GeneralPattern = new Regex(
			@"^\s*(?<origin>[^:\r\n]*?)\s*:\s+(?<kind>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(\s+\[[^\]]+\])?\s*$",
			RegexOptions.Compiled);

		#endregion

		#region PublicMethods

		/// <param name="outputPath">Folder for the built assembly (null: the project's OutputPath).</param>
		/// <param name="includeEditor">
		///     Builds the editor scripts' project instead (it builds the game project first) and reads Game.Editor.dll
		///     too.
		/// </param>
		public static BuildResult Build(string projectFile, string configuration, string outputPath, bool includeEditor = false)
		{
			var result = new BuildResult();
			var watch = Stopwatch.StartNew();
			string projectFolder = Path.GetDirectoryName(projectFile);
			string packages = Path.ChangeExtension(projectFile, ".packages.props");
			string editorProject = ProjectGenerator.EditorProjectFile(projectFile);
			string built = includeEditor ? editorProject : projectFile;
			// Restore only when a project or its packages changed since the last restore (it costs about a second).
			bool restored = IsRestored(projectFile, packages) && (!includeEditor || IsRestored(editorProject, packages));

			var arguments = new StringBuilder();
			arguments.Append("build \"").Append(built).Append("\" -c ").Append(configuration).Append(" -nologo -v q -clp:NoSummary");
			if (restored)
			{
				arguments.Append(" --no-restore");
			}

			if (outputPath != null)
			{
				arguments.Append(" \"-p:OutputPath=").Append(outputPath.TrimEnd('\\', '/')).Append("\\\\\"");
			}

			var start = new ProcessStartInfo("dotnet", arguments.ToString())
			{
				WorkingDirectory = projectFolder,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
			start.Environment["DOTNET_NOLOGO"] = "1";

			var output = new StringBuilder();
			using (Process process = new Process { StartInfo = start })
			{
				process.OutputDataReceived += (s, e) =>
				{
					if (e.Data != null)
					{
						lock (output)
						{
							output.AppendLine(e.Data);
						}
					}
				};
				process.ErrorDataReceived += (s, e) =>
				{
					if (e.Data != null)
					{
						lock (output)
						{
							output.AppendLine(e.Data);
						}
					}
				};

				try
				{
					process.Start();
				}
				catch (Exception e)
				{
					result.Diagnostics.Add(new BuildDiagnostic { IsError = true, Code = "LEL0001", Message = "Cannot start 'dotnet' (is the .NET SDK installed?): " + e.Message });
					return result;
				}

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.WaitForExit();
				result.Output = output.ToString();
				ParseDiagnostics(result);

				string folder = outputPath ?? Path.Combine(projectFolder, "Library", "ScriptAssemblies");
				string dll = Path.Combine(folder, ProjectGenerator.AssemblyName + ".dll");
				string editorDll = Path.Combine(folder, ProjectGenerator.EditorAssemblyName + ".dll");
				result.Success = process.ExitCode == 0 && File.Exists(dll) && (!includeEditor || File.Exists(editorDll));
				if (result.Success)
				{
					result.Assembly = File.ReadAllBytes(dll);
					result.Symbols = ReadSymbols(dll);
					if (includeEditor)
					{
						result.EditorAssembly = File.ReadAllBytes(editorDll);
						result.EditorSymbols = ReadSymbols(editorDll);
					}
				}
				else if (result.ErrorCount == 0)
				{
					// Failed without a parsable diagnostic: show the raw output.
					result.Diagnostics.Add(new BuildDiagnostic { IsError = true, Code = "LEL0002", Message = "Build failed:\n" + result.Output.Trim() });
				}
			}

			result.Seconds = watch.Elapsed.TotalSeconds;
			return result;
		}

		#endregion

		#region PrivateMethods

		/// <summary>The project's restore output (Library/obj/&lt;project&gt;/) is newer than the project and its packages.</summary>
		private static bool IsRestored(string projectFile, string packages)
		{
			string assets = Path.Combine(Path.GetDirectoryName(projectFile), "Library", "obj", Path.GetFileNameWithoutExtension(projectFile), "project.assets.json");
			return File.Exists(assets)
				&& File.GetLastWriteTimeUtc(assets) >= File.GetLastWriteTimeUtc(projectFile)
				&& (!File.Exists(packages) || File.GetLastWriteTimeUtc(assets) >= File.GetLastWriteTimeUtc(packages));
		}

		private static byte[] ReadSymbols(string dll)
		{
			string pdb = Path.ChangeExtension(dll, ".pdb");
			return File.Exists(pdb) ? File.ReadAllBytes(pdb) : null;
		}

		private static void ParseDiagnostics(BuildResult result)
		{
			var seen = new HashSet<string>();
			foreach (string rawLine in result.Output.Split('\n'))
			{
				string line = rawLine.TrimEnd('\r');
				Match match = DiagnosticPattern.Match(line);
				BuildDiagnostic diagnostic;
				if (match.Success)
				{
					diagnostic = new BuildDiagnostic
					{
						IsError = match.Groups["kind"].Value == "error",
						File = match.Groups["file"].Value.Trim(),
						Line = int.Parse(match.Groups["line"].Value),
						Column = int.Parse(match.Groups["column"].Value),
						Code = match.Groups["code"].Value,
						Message = match.Groups["message"].Value
					};
				}
				else
				{
					match = GeneralPattern.Match(line);
					if (!match.Success)
					{
						continue;
					}

					diagnostic = new BuildDiagnostic
					{
						IsError = match.Groups["kind"].Value == "error",
						Code = match.Groups["code"].Value,
						Message = match.Groups["message"].Value
					};
				}

				if (seen.Add(diagnostic.ToString()))
				{
					result.Diagnostics.Add(diagnostic);
				}
			}
		}

		#endregion
	}
}
