using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     The editor's automation server: a named pipe per project ("LELEngine.Editor." + a hash of the project folder),
	///     reachable by the user who runs the editor only (<see cref="PipeOptions.CurrentUserOnly" />). A client
	///     (LELEngine.Editor.Console -command, <see cref="AutomationClient" />) connects, sends one request line
	///     <c>{"command": "screenshot Inspector"}</c> and reads one answer line <c>{"ok", "output", "error", "files"}</c>.
	///     Commands run on the main thread between frames (<see cref="AutomationCommands" />); connections are served in
	///     parallel, their commands interleave at their awaits.
	/// </summary>
	internal sealed class AutomationServer : IDisposable
	{
		#region PrivateFields

		private readonly CancellationTokenSource cancel = new CancellationTokenSource();

		#endregion

		#region Constructors

		public AutomationServer(string projectRoot)
		{
			PipeName = PipeNameFor(projectRoot);
		}

		#endregion

		#region PublicFields

		public string PipeName { get; }

		#endregion

		#region PublicMethods

		/// <summary>The pipe of the editor that has this project open (the same name the client computes).</summary>
		public static string PipeNameFor(string projectRoot)
		{
			string normalized = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (OperatingSystem.IsWindows())
			{
				normalized = normalized.ToLowerInvariant();
			}

			byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
			return "LELEngine.Editor." + Convert.ToHexString(hash, 0, 8);
		}

		public void Start()
		{
			// Fails now (not in the loop) when another editor serves this project already.
			NamedPipeServerStream first = CreatePipe();
			Task.Run(() => AcceptLoop(first));
		}

		public void Dispose()
		{
			cancel.Cancel();
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     The user's own pipe. On Windows its owner and only access rule are the user's SID, set explicitly: an elevated
		///     editor's objects are owned by the Administrators group by default, and clients (which check that the owner is
		///     their user, <see cref="PipeOptions.CurrentUserOnly" />) running without elevation refused it.
		/// </summary>
		private NamedPipeServerStream CreatePipe()
		{
			if (!OperatingSystem.IsWindows())
			{
				return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			}

			SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
			var security = new PipeSecurity();
			security.SetOwner(user);
			security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
			return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
		}

		private async Task AcceptLoop(NamedPipeServerStream pipe)
		{
			while (!cancel.IsCancellationRequested)
			{
				try
				{
					await pipe.WaitForConnectionAsync(cancel.Token);
					NamedPipeServerStream connected = pipe;
					_ = Task.Run(() => Serve(connected));
					pipe = CreatePipe();
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception e)
				{
					Console.WriteLine("[Automation] Pipe error: " + e.Message);
					pipe.Dispose();
					await Task.Delay(500);
					pipe = CreatePipe();
				}
			}

			pipe.Dispose();
		}

		private async Task Serve(NamedPipeServerStream pipe)
		{
			using (pipe)
			{
				try
				{
					var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
					var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\n" };
					string line = await reader.ReadLineAsync(cancel.Token);
					if (line == null)
					{
						return;
					}

					string command;
					using (JsonDocument request = JsonDocument.Parse(line))
					{
						command = request.RootElement.GetProperty("command").GetString() ?? "";
					}

					CommandResult result;
					if (!EditorAutomation.IsRunning)
					{
						result = CommandResult.Fail("The editor has no project open.");
					}
					else
					{
						try
						{
							result = await EditorAutomation.Run(() => AutomationCommands.Execute(command));
						}
						catch (Exception e)
						{
							Debug.LogException(e);
							result = CommandResult.Fail(e.GetType().Name + ": " + e.Message);
						}
					}

					await writer.WriteLineAsync(JsonSerializer.Serialize(new Answer
					{
						ok = result.Ok,
						output = result.Output,
						error = result.Error,
						files = result.Files.ToArray()
					}));
				}
				catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
				{
					// The client went away or sent something else: nothing to answer.
					Console.WriteLine("[Automation] Request dropped: " + e.Message);
				}
			}
		}

		#endregion

		#region NestedTypes

		// Lower-case names: the wire format.
		private sealed class Answer
		{
			public bool ok { get; set; }
			public string output { get; set; }
			public string error { get; set; }
			public string[] files { get; set; }
		}

		#endregion
	}
}
