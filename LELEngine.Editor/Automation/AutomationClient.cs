using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     The command line client of the automation server (LELEngine.Editor.Console -command "..."): finds the editor that
	///     has the project open (-projectPath, else the project the current folder is in, else the only editor listening),
	///     sends the command, prints the answer. Exit code 0: done; 1: the command failed; 2: no editor to talk to.
	/// </summary>
	internal static class AutomationClient
	{
		#region PublicFields

		public const string Usage =
			"Editor automation (talks to a running editor, or to a batch-mode editor started with -listen):\n" +
			"  LELEngine.Editor.Console [-projectPath <folder>] [-timeout <seconds>] [-json] -command <command words...>\n" +
			"                             (last: everything after -command is the command, e.g. -command click \"button:Add Component\")\n" +
			"  LELEngine.Editor.Console -script <file> [...]        one command per line (# comments), stops at a failure\n" +
			"  LELEngine.Editor.Console -command help               the commands\n" +
			"  The editor to talk to: -projectPath, else the project the current folder is in, else the only editor listening.\n" +
			"  Batch mode without UI, serving commands until 'quit':\n" +
			"  LELEngine.Editor.Console -batchmode -projectPath <folder> -listen";

		#endregion

		#region PublicMethods

		public static int Run(string[] args)
		{
			// Everything after -command is the command: one argument as it is, several joined (those with spaces quoted), so
			// shells that mangle nested quotes (PowerShell 5) can pass: -command screenshot "Probe Window" 800x600.
			int at = Array.FindIndex(args, a => string.Equals(a, "-command", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--command", StringComparison.OrdinalIgnoreCase));
			CommandLine options = CommandLine.Parse(at >= 0 ? args[..at] : args);
			var commands = new List<string>();
			if (at >= 0 && at + 1 < args.Length)
			{
				string[] words = args[(at + 1)..];
				commands.Add(words.Length == 1 ? words[0] : string.Join(" ", words.Select(w => w.Any(char.IsWhiteSpace) ? "\"" + w.Replace("\"", "\\\"") + "\"" : w)));
			}

			if (options.Get("script") is string script)
			{
				commands.AddRange(File.ReadAllLines(script).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal)));
			}

			if (commands.Count == 0)
			{
				Console.Error.WriteLine(Usage);
				return 2;
			}

			string pipeName;
			try
			{
				pipeName = FindPipe(options);
			}
			catch (InvalidOperationException e)
			{
				Console.Error.WriteLine(e.Message);
				return 2;
			}

			double timeout = double.TryParse(options.Get("timeout"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) ? seconds : 600;
			bool json = options.Has("json");
			foreach (string line in commands)
			{
				int code = Send(pipeName, line, timeout, json);
				if (code != 0)
				{
					return code;
				}
			}

			return 0;
		}

		#endregion

		#region PrivateMethods

		private static int Send(string pipeName, string command, double timeoutSeconds, bool json)
		{
			// Windows: the owner is checked here, not with PipeOptions.CurrentUserOnly, which compares it with the token's
			// default owner: the Administrators group in an elevated process, the user otherwise, so an editor and a client of
			// different elevation refused each other. The editor's pipe is owned by its user (AutomationServer).
			using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, OperatingSystem.IsWindows() ? PipeOptions.None : PipeOptions.CurrentUserOnly);
			try
			{
				pipe.Connect(3000);
				if (OperatingSystem.IsWindows() && !IsOwnPipe(pipe))
				{
					Console.Error.WriteLine($"The pipe {pipeName} belongs to another user: not sending anything.");
					return 2;
				}
			}
			catch (TimeoutException)
			{
				Console.Error.WriteLine($"No editor answers on {pipeName}: start the editor on the project, or a batch-mode editor with -listen.");
				return 2;
			}
			catch (Exception e) when (e is UnauthorizedAccessException or IOException)
			{
				Console.Error.WriteLine($"Cannot talk to the editor on {pipeName}: {e.Message}");
				return 2;
			}

			var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
			var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
			writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command }));
			System.Threading.Tasks.Task<string> answer = reader.ReadLineAsync();
			if (!answer.Wait(TimeSpan.FromSeconds(timeoutSeconds)) || answer.Result == null)
			{
				Console.Error.WriteLine(answer.IsCompleted ? "The editor closed the connection." : $"No answer within {timeoutSeconds:0} s (the command may still run in the editor).");
				return 1;
			}

			if (json)
			{
				Console.WriteLine(answer.Result);
			}

			using JsonDocument document = JsonDocument.Parse(answer.Result);
			JsonElement root = document.RootElement;
			bool ok = root.GetProperty("ok").GetBoolean();
			if (!json)
			{
				string output = root.TryGetProperty("output", out JsonElement o) ? o.GetString() : null;
				if (!string.IsNullOrEmpty(output))
				{
					Console.Write(output.EndsWith("\n", StringComparison.Ordinal) ? output : output + "\n");
				}

				if (!ok)
				{
					string error = root.TryGetProperty("error", out JsonElement e) ? e.GetString() : "failed";
					Console.Error.WriteLine("Error: " + error);
				}
			}

			return ok ? 0 : 1;
		}

		/// <summary>The pipe's owner is this user (or this process's default owner, as for a pipe of an elevated editor of older builds).</summary>
		[System.Runtime.Versioning.SupportedOSPlatform("windows")]
		private static bool IsOwnPipe(NamedPipeClientStream pipe)
		{
			var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
			System.Security.Principal.IdentityReference owner = pipe.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
			return owner != null && (owner.Equals(identity.User) || owner.Equals(identity.Owner));
		}

		/// <summary>The pipe of the editor to talk to (see <see cref="Usage" />).</summary>
		private static string FindPipe(CommandLine options)
		{
			if (options.Get("pipe") is string pipe)
			{
				return pipe;
			}

			string project = options.Get("projectpath") ?? FindProjectAbove(Environment.CurrentDirectory);
			if (project != null)
			{
				return AutomationServer.PipeNameFor(project);
			}

			if (OperatingSystem.IsWindows())
			{
				string[] listening = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).Where(n => n.StartsWith("LELEngine.Editor.", StringComparison.Ordinal)).Distinct().ToArray();
				if (listening.Length == 1)
				{
					return listening[0];
				}

				if (listening.Length > 1)
				{
					throw new InvalidOperationException("Several editors listen: give -projectPath <folder> (or run from inside the project).");
				}
			}

			throw new InvalidOperationException("No editor to talk to: give -projectPath <folder>, run from inside a project, or start an editor.");
		}

		private static string FindProjectAbove(string folder)
		{
			for (DirectoryInfo dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
			{
				if (ProjectInfo.IsProjectFolder(dir.FullName))
				{
					return dir.FullName;
				}
			}

			return null;
		}

		#endregion
	}
}
