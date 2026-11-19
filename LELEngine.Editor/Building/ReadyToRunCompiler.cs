using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace LELEngine.Editor
{
	/// <summary>
	///     Compile Ahead Of Time (the build setting): compiles a game's scripts assembly ahead of time (ReadyToRun), as
	///     the player's own assemblies are when it is published for a platform (LELEngine.Player.csproj); without it the
	///     scripts compile method by method as they first run, most of them in the frame a scene starts. Uses the SDK's
	///     crossgen compiler: the one a published editor carries (Crossgen/, copied there by its publish), else the one
	///     in the NuGet packages folder (restored there when a player was published with ReadyToRun). Nothing here is
	///     optional: every way it cannot be done is an error text for the build to fail with.
	/// </summary>
	internal static class ReadyToRunCompiler
	{
		#region PublicMethods

		/// <summary>
		///     Null when a build from the player in <paramref name="playerSource" /> can be compiled ahead of time, else
		///     why not: the player itself is not (a development editor's Player/, a portable one), its platform is not
		///     one crossgen compiles for, or there is no compiler. Asked before anything is built.
		/// </summary>
		public static string CheckPlayer(string playerSource)
		{
			const string Setting = "Compile Ahead Of Time is on in the build settings, but ";
			string runtimeIdentifier = PlayerFiles.RuntimeIdentifierOf(playerSource);
			if (runtimeIdentifier == null)
			{
				return Setting + "the player in " + playerSource + " is not published for a platform (publish the editor with a runtime " +
					"identifier: dotnet publish LELEngine.Editor -c Release -r <rid>, or build self-contained).";
			}

			if (!TryTarget(runtimeIdentifier, out _, out _))
			{
				return Setting + "there is no ReadyToRun target for the player's platform '" + runtimeIdentifier + "'.";
			}

			if (!IsReadyToRun(Path.Combine(playerSource, "LELEngine.dll")))
			{
				return Setting + "the player in " + playerSource + " is not compiled ahead of time itself (its LELEngine.dll holds no native " +
					"code): publish it with a runtime identifier.";
			}

			if (FindCompiler(out string why) == null)
			{
				return Setting + why + ".";
			}

			return FrameworkFolder(playerSource, runtimeIdentifier, out why) == null && why != null ? Setting + why + "." : null;
		}

		/// <summary>
		///     Replaces <paramref name="assembly" /> (in the build folder) with its ahead-of-time compiled image, made from
		///     <paramref name="source" /> (the same assembly outside that folder) for the platform of the player in
		///     <paramref name="playerSource" />; the other managed assemblies of <paramref name="buildFolder" /> are its
		///     references (and .NET's own of that platform when the build does not carry them). Null when done, else the
		///     error (the compiler's last lines are logged before it).
		/// </summary>
		public static string Compile(string source, string assembly, string buildFolder, string playerSource, Action<string> log)
		{
			string name = Path.GetFileName(assembly);
			string runtimeIdentifier = PlayerFiles.RuntimeIdentifierOf(playerSource);
			if (!TryTarget(runtimeIdentifier, out string targetOS, out string targetArchitecture))
			{
				return $"{name} cannot be compiled ahead of time: no ReadyToRun target for '{runtimeIdentifier}'.";
			}

			string compiler = FindCompiler(out string why);
			if (compiler == null)
			{
				return $"{name} cannot be compiled ahead of time: {why}.";
			}

			string framework = FrameworkFolder(playerSource, runtimeIdentifier, out why);
			if (framework == null && why != null)
			{
				return $"{name} cannot be compiled ahead of time: {why}.";
			}

			var watch = Stopwatch.StartNew();
			string compiled = assembly + ".r2r";
			string response = assembly + ".r2r.rsp";
			try
			{
				// A response file: one argument a line, no command line length or quoting to mind.
				var arguments = new StringBuilder();
				arguments.Append('"').Append(source).Append("\"\n");
				arguments.Append("-o:\"").Append(compiled).Append("\"\n");
				arguments.Append("--targetos:").Append(targetOS).Append('\n');
				arguments.Append("--targetarch:").Append(targetArchitecture).Append('\n');
				arguments.Append("-O\n");
				foreach (string file in Directory.GetFiles(buildFolder, "*.dll"))
				{
					// Not itself, and only managed assemblies (the folder also holds the runtime's native libraries).
					if (!string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase) && IsManaged(file))
					{
						arguments.Append("-r:\"").Append(file).Append("\"\n");
					}
				}

				if (framework != null)
				{
					// A framework-dependent build: .NET's own assemblies are not in it.
					foreach (string file in Directory.GetFiles(framework, "*.dll"))
					{
						if (!File.Exists(Path.Combine(buildFolder, Path.GetFileName(file))) && IsManaged(file))
						{
							arguments.Append("-r:\"").Append(file).Append("\"\n");
						}
					}
				}

				File.WriteAllText(response, arguments.ToString(), new UTF8Encoding(false));
				bool viaDotnet = compiler.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
				var start = new ProcessStartInfo(viaDotnet ? "dotnet" : compiler, (viaDotnet ? "\"" + compiler + "\" " : "") + "@\"" + response + "\"")
				{
					WorkingDirectory = buildFolder,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					CreateNoWindow = true
				};

				var output = new List<string>();
				using (var process = new Process { StartInfo = start })
				{
					DataReceivedEventHandler collect = (s, e) =>
					{
						if (!string.IsNullOrWhiteSpace(e.Data))
						{
							lock (output)
							{
								output.Add(e.Data.Trim());
							}
						}
					};
					process.OutputDataReceived += collect;
					process.ErrorDataReceived += collect;
					process.Start();
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();
					if (!process.WaitForExit(300000))
					{
						process.Kill();
						return $"{name} could not be compiled ahead of time: {compiler} did not finish in five minutes.";
					}

					process.WaitForExit();
					if (process.ExitCode != 0 || !File.Exists(compiled) || new FileInfo(compiled).Length == 0)
					{
						lock (output)
						{
							foreach (string line in output.Count > 12 ? output.GetRange(output.Count - 12, 12) : output)
							{
								log(line);
							}
						}

						return $"{name} could not be compiled ahead of time ({compiler}, exit code {process.ExitCode}).";
					}
				}

				if (!IsReadyToRun(compiled))
				{
					return $"{name} could not be compiled ahead of time: {compiler} wrote an assembly without native code.";
				}

				long before = new FileInfo(assembly).Length;
				File.Move(compiled, assembly, true);
				log($"{name} compiled ahead of time (ReadyToRun, {runtimeIdentifier}) in {watch.Elapsed.TotalSeconds:0.0} s: {before / 1024} KB -> {new FileInfo(assembly).Length / 1024} KB.");
				return null;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
			{
				return $"{name} could not be compiled ahead of time with {compiler}: {e.Message}";
			}
			finally
			{
				TryDelete(compiled);
				TryDelete(response);
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>The crossgen operating system and architecture names of a runtime identifier ("win-x64", "linux-arm64", "osx-arm64").</summary>
		private static bool TryTarget(string runtimeIdentifier, out string os, out string architecture)
		{
			os = null;
			architecture = null;
			if (string.IsNullOrEmpty(runtimeIdentifier))
			{
				return false;
			}

			int dash = runtimeIdentifier.LastIndexOf('-');
			if (dash <= 0)
			{
				return false;
			}

			architecture = runtimeIdentifier.Substring(dash + 1).ToLowerInvariant();
			switch (runtimeIdentifier.Substring(0, dash).ToLowerInvariant())
			{
				case "win":
					os = "windows";
					break;
				case "linux":
					os = "linux";
					break;
				case "osx":
					os = "osx";
					break;
				default:
					return false;
			}

			return architecture == "x64" || architecture == "arm64" || architecture == "x86" || architecture == "arm";
		}

		/// <summary>
		///     The SDK's crossgen compiler for this machine, of the .NET version the editor runs on (the newest patch): the
		///     editor's own (Crossgen/&lt;version&gt;/tools, what its publish copied), else the NuGet packages folder's; or
		///     null with the reason.
		/// </summary>
		private static string FindCompiler(out string why)
		{
			string own = Path.Combine(AppContext.BaseDirectory, "Crossgen");
			string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
			if (string.IsNullOrEmpty(packages))
			{
				packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
			}

			string package = Path.Combine(packages, "microsoft.netcore.app.crossgen2." + RuntimeInformation.RuntimeIdentifier.ToLowerInvariant());
			why = null;
			string best = FindCompilerIn(own) ?? FindCompilerIn(package);
			if (best == null)
			{
				why = $"the .NET {Environment.Version.Major} SDK's crossgen compiler is neither next to the editor ({own}) nor in the NuGet " +
					$"packages ({package}): it comes with an editor published with a runtime identifier";
			}

			return best;
		}

		/// <summary>The newest compiler of the editor's .NET version under a folder of version folders ("10.0.12/tools/crossgen2.exe").</summary>
		private static string FindCompilerIn(string package)
		{
			if (!Directory.Exists(package))
			{
				return null;
			}

			string best = null;
			Version bestVersion = null;
			foreach (string folder in Directory.GetDirectories(package))
			{
				// "10.0.12", "10.0.0-rc.1...": the numbers before a pre-release suffix.
				string text = Path.GetFileName(folder);
				int suffix = text.IndexOf('-');
				if (!Version.TryParse(suffix >= 0 ? text.Substring(0, suffix) : text, out Version version) || version.Major != Environment.Version.Major)
				{
					continue;
				}

				// An app host (Windows: .exe), or an assembly for `dotnet` to run.
				foreach (string candidate in new[] { "crossgen2.exe", "crossgen2", "crossgen2.dll" })
				{
					string tool = Path.Combine(folder, "tools", candidate);
					if (File.Exists(tool) && (bestVersion == null || version > bestVersion))
					{
						best = tool;
						bestVersion = version;
						break;
					}
				}
			}

			return best;
		}

		/// <summary>
		///     Where .NET's own assemblies of the player's platform are when the player does not carry them (a
		///     framework-dependent one): the self-contained player of the same platform next to the editor, else the
		///     editor's own runtime when it is of that platform. Null without a reason: the player carries them.
		/// </summary>
		private static string FrameworkFolder(string playerSource, string runtimeIdentifier, out string why)
		{
			const string CoreLibrary = "System.Private.CoreLib.dll";
			why = null;
			if (File.Exists(Path.Combine(playerSource, CoreLibrary)))
			{
				return null;
			}

			string selfContained = PlayerFiles.SelfContainedFolder;
			if (File.Exists(Path.Combine(selfContained, CoreLibrary)) &&
				string.Equals(PlayerFiles.RuntimeIdentifierOf(selfContained), runtimeIdentifier, StringComparison.OrdinalIgnoreCase))
			{
				return selfContained;
			}

			string own = Path.GetDirectoryName(typeof(object).Assembly.Location);
			if (!string.IsNullOrEmpty(own) && string.Equals(RuntimeInformation.RuntimeIdentifier, runtimeIdentifier, StringComparison.OrdinalIgnoreCase))
			{
				return own;
			}

			why = $"a framework-dependent build needs .NET's assemblies for '{runtimeIdentifier}' to compile against, and this editor has no " +
				"self-contained player of that platform (PlayerSelfContained/)";
			return null;
		}

		/// <summary>Whether a managed assembly holds ahead-of-time compiled code (a ReadyToRun header named by its CLI header).</summary>
		private static bool IsReadyToRun(string file)
		{
			try
			{
				using var stream = File.OpenRead(file);
				using var reader = new PEReader(stream);
				return reader.PEHeaders.CorHeader != null && reader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size != 0;
			}
			catch (Exception e) when (e is BadImageFormatException || e is IOException || e is UnauthorizedAccessException)
			{
				return false;
			}
		}

		private static bool IsManaged(string file)
		{
			try
			{
				AssemblyName.GetAssemblyName(file);
				return true;
			}
			catch (Exception e) when (e is BadImageFormatException || e is IOException || e is UnauthorizedAccessException)
			{
				return false;
			}
		}

		private static void TryDelete(string file)
		{
			try
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// A leftover beside the assembly: harmless, the next build overwrites it.
			}
		}

		#endregion
	}
}
