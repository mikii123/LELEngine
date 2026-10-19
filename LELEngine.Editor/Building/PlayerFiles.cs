using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LELEngine.Editor
{
	/// <summary>
	///     The players next to the editor that game builds start from:
	///     <code>
	///     Player/                framework-dependent: the target machine needs the .NET runtime
	///     PlayerSelfContained/   with the .NET runtime, for one platform (runtime identifier)
	///     </code>
	///     `dotnet publish` of the editor writes both (see LELEngine.Editor.csproj). `dotnet build` copies only the
	///     framework-dependent one; the self-contained one is then published from the engine sources the first time a
	///     build asks for it, and again after the player in Player/ changed.
	/// </summary>
	internal static class PlayerFiles
	{
		#region PublicFields

		public const string AssemblyName = "LELEngine.Player";

		public static string FrameworkDependentFolder => Path.Combine(AppContext.BaseDirectory, "Player");

		public static string SelfContainedFolder => Path.Combine(AppContext.BaseDirectory, "PlayerSelfContained");

		/// <summary>
		///     Runtime identifier of the self-contained player (the platform its games run on), null when this editor
		///     cannot build self-contained games (published without a runtime identifier).
		/// </summary>
		public static string SelfContainedRuntime
		{
			get
			{
				if (!selfContainedRuntimeRead)
				{
					selfContainedRuntime = IsPlayer(SelfContainedFolder)
						? ReadRuntimeIdentifier(SelfContainedFolder) ?? RuntimeInformation.RuntimeIdentifier
						: SourceProject != null ? RuntimeInformation.RuntimeIdentifier : null;
					selfContainedRuntimeRead = true;
				}

				return selfContainedRuntime;
			}
		}

		#endregion

		#region PrivateFields

		/// <summary>Host libraries of a self-contained app: next to a framework-dependent one they make it start as self-contained.</summary>
		private static readonly string[] HostLibraries =
		{
			"hostfxr.dll", "hostpolicy.dll", "libhostfxr.so", "libhostpolicy.so", "libhostfxr.dylib", "libhostpolicy.dylib"
		};

		private static readonly object Sync = new object();
		private static string selfContainedRuntime;
		private static bool selfContainedRuntimeRead;

		/// <summary>Records which Player/ the self-contained player was published with (outside its folder: builds copy the folder).</summary>
		private static string StampFile => Path.Combine(AppContext.BaseDirectory, "PlayerSelfContained.stamp");

		/// <summary>The player's project in a development build of the editor (assembly metadata, absent in published editors).</summary>
		private static string SourceProject
		{
			get
			{
				string path = Metadata("LELEngine.PlayerProject");
				return path != null && File.Exists(path) ? path : null;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     The folder of the requested player, published first when a development editor has none or an outdated one;
		///     null, with the reason logged, when there is none.
		/// </summary>
		public static string Resolve(bool selfContained, Action<string> log)
		{
			if (!selfContained)
			{
				if (IsPlayer(FrameworkDependentFolder))
				{
					return FrameworkDependentFolder;
				}

				log("Error: the player is missing next to the editor (" + FrameworkDependentFolder + ").");
				return null;
			}

			lock (Sync)
			{
				string source = SourceProject;
				if (IsPlayer(SelfContainedFolder) && (source == null || IsPublishCurrent()))
				{
					return SelfContainedFolder;
				}

				if (source == null)
				{
					log("Error: this editor has no self-contained player. Publish the editor with a runtime identifier " +
						"(dotnet publish LELEngine.Editor -c Release -r <rid>) or turn Self-contained off.");
					return null;
				}

				return Publish(source, log) ? SelfContainedFolder : null;
			}
		}

		/// <summary>
		///     Deletes from <paramref name="output" /> the files an earlier build with the other player left there and
		///     <paramref name="player" /> does not have: a self-contained player's host libraries next to a
		///     framework-dependent player start it as self-contained, which fails, and its runtime is ~70 MB.
		/// </summary>
		public static void RemoveOtherPlayer(string output, string player, Action<string> log)
		{
			bool selfContained = string.Equals(player, SelfContainedFolder, StringComparison.OrdinalIgnoreCase);
			string other = selfContained ? FrameworkDependentFolder : SelfContainedFolder;
			var keep = new HashSet<string>(RelativeFiles(player), StringComparer.OrdinalIgnoreCase);
			var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (IsPlayer(other))
			{
				stale.UnionWith(RelativeFiles(other));
			}

			if (!selfContained)
			{
				stale.UnionWith(HostLibraries);
			}

			int removed = 0;
			var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string relative in stale)
			{
				string file = Path.Combine(output, relative);
				if (keep.Contains(relative) || !File.Exists(file))
				{
					continue;
				}

				File.Delete(file);
				removed++;
				for (string folder = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
				{
					folders.Add(folder);
				}
			}

			// Deepest first, so a folder emptied by its subfolders' removal goes too.
			var ordered = new List<string>(folders);
			ordered.Sort((a, b) => b.Length.CompareTo(a.Length));
			foreach (string folder in ordered)
			{
				string path = Path.Combine(output, folder);
				if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
				{
					Directory.Delete(path);
				}
			}

			if (removed > 0)
			{
				log($"Removed {removed} files of the earlier {(selfContained ? "framework-dependent" : "self-contained")} player.");
			}
		}

		#endregion

		#region PrivateMethods

		private static bool IsPlayer(string folder)
		{
			return File.Exists(Path.Combine(folder, AssemblyName + ".dll"));
		}

		private static IEnumerable<string> RelativeFiles(string folder)
		{
			foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
			{
				yield return Path.GetRelativePath(folder, file);
			}
		}

		/// <summary>The platform a published player is for: the runtime target of its deps file (".NETCoreApp,Version=v10.0/win-x64").</summary>
		private static string ReadRuntimeIdentifier(string folder)
		{
			try
			{
				using JsonDocument deps = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, AssemblyName + ".deps.json")));
				string name = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString();
				int slash = name?.IndexOf('/') ?? -1;
				return slash >= 0 ? name.Substring(slash + 1) : null;
			}
			catch (Exception e) when (e is IOException || e is JsonException || e is KeyNotFoundException || e is InvalidOperationException || e is UnauthorizedAccessException)
			{
				return null;
			}
		}

		private static string Metadata(string key)
		{
			foreach (AssemblyMetadataAttribute attribute in typeof(PlayerFiles).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
			{
				if (attribute.Key == key)
				{
					return attribute.Value;
				}
			}

			return null;
		}

		/// <summary>Every file of Player/ (path, size, time): the self-contained player is current while this is what it was published with.</summary>
		private static string FrameworkDependentStamp()
		{
			var lines = new List<string>();
			foreach (string relative in RelativeFiles(FrameworkDependentFolder))
			{
				var info = new FileInfo(Path.Combine(FrameworkDependentFolder, relative));
				lines.Add(relative + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
			}

			lines.Sort(StringComparer.Ordinal);
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
		}

		private static bool IsPublishCurrent()
		{
			try
			{
				return File.Exists(StampFile) && File.ReadAllText(StampFile).Trim() == FrameworkDependentStamp();
			}
			catch (IOException)
			{
				return false;
			}
		}

		/// <summary>Publishes the player project for this machine's platform, self-contained, into PlayerSelfContained/.</summary>
		private static bool Publish(string project, Action<string> log)
		{
			string runtime = RuntimeInformation.RuntimeIdentifier;
			string configuration = Metadata("LELEngine.Configuration") ?? "Release";
			log($"Publishing the self-contained player ({runtime}) from the engine sources (after every change of the player)...");
			var watch = Stopwatch.StartNew();
			var start = new ProcessStartInfo("dotnet",
				$"publish \"{project}\" -c {configuration} -r {runtime} --self-contained true -o \"{SelfContainedFolder}\" -nologo -v q -clp:NoSummary")
			{
				WorkingDirectory = Path.GetDirectoryName(project),
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
			start.Environment["DOTNET_NOLOGO"] = "1";

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
				try
				{
					process.Start();
				}
				catch (Exception e)
				{
					log("Error: cannot start 'dotnet' (is the .NET SDK installed?): " + e.Message);
					return false;
				}

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				process.WaitForExit();
				lock (output)
				{
					foreach (string line in output)
					{
						log(line);
					}
				}

				if (process.ExitCode != 0 || !IsPlayer(SelfContainedFolder))
				{
					log($"Error: publishing the self-contained player failed (exit code {process.ExitCode}).");
					return false;
				}
			}

			File.WriteAllText(StampFile, FrameworkDependentStamp());
			selfContainedRuntimeRead = false;
			log($"Published the self-contained player in {watch.Elapsed.TotalSeconds:0.0} s.");
			return true;
		}

		#endregion
	}
}
