using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using LELEngine.Graphics;
using LELEngine.Rendering;
using LELEngine.Windowing;

namespace LELEngine.Shaders
{
	/// <summary>
	///     The shader compile helper: the game's own executable started in a mode that only compiles shader variants into
	///     the program binary cache (<see cref="ProgramBinaryCache" />) and reports each one done; the game's compile thread
	///     (<see cref="ShaderCompileThread" />) then loads them from there. Some drivers (AMD's OpenGL driver on Windows)
	///     hold every context of a process while one of them compiles, so a compile in the game's process, on any thread,
	///     stalls its frames for as long as the compile takes (cold: about a second per large program); another process has a
	///     driver of its own, and loading a binary takes tens of milliseconds. Used when the host sets
	///     <see cref="Executable" /> (the player: its own executable, which runs <see cref="RunHelper" /> when started with
	///     <see cref="Argument" />) and the binary cache works; otherwise the compile thread compiles itself.
	/// </summary>
	public static class ShaderCompileHelper
	{
		#region PublicFields

		/// <summary>The command line argument that starts the helper: compileshaders=&lt;request file&gt;.</summary>
		public const string Argument = "compileshaders";

		/// <summary>The executable started as the helper (null: none).</summary>
		public static string Executable { get; set; }

		#endregion

		#region PrivateFields

		private const string DoneLine = "done ";
		private static int requests;

		#endregion

		#region PublicMethods

		/// <summary>
		///     The helper process's work (call it before anything else of the game starts: no window, no log of the game's
		///     own): makes a hidden GL context like the game's, compiles the request's variants into the binary cache and
		///     writes "done &lt;index&gt;" for each on the standard output. Returns the exit code.
		/// </summary>
		public static int RunHelper(string requestFile)
		{
			Request request;
			try
			{
				request = Request.Read(requestFile);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("Cannot read the request " + requestFile + ": " + e.Message);
				return 2;
			}

			Window.InitializeGlfw();
			// A hidden window of its own: the graphics device is made with it, as the game's is with its window.
			NativeWindow window;
			try
			{
				window = new NativeWindow(new NativeWindowSettings
				{
					ClientSize = new Mathematics.Vector2i(1, 1),
					Title = "LELEngine shader compile helper",
					APIVersion = new Version(request.GLMajor, request.GLMinor),
					Profile = ContextProfile.Core,
					Flags = ContextFlags.ForwardCompatible,
					StartVisible = false,
					StartFocused = false
				});
			}
			catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException)
			{
				Console.Error.WriteLine($"No GL {request.GLMajor}.{request.GLMinor} context");
				return 3;
			}

			GLCapabilities.Initialize();
			AssetDatabase.Initialize(request.DataFolder);
			ProgramBinaryCache.Folder = request.CacheFolder;

			// A chunk at a time: started together (the driver compiles them on its threads), then finished in order.
			const int Chunk = 8;
			var programs = new ShaderProgram[Chunk];
			for (int first = 0; first < request.Variants.Count; first += Chunk)
			{
				if (ParentGone(request.ParentProcess))
				{
					return 0;
				}

				int count = Math.Min(Chunk, request.Variants.Count - first);
				for (int i = 0; i < count; i++)
				{
					programs[i] = TryGetProgram(request.Variants[first + i]);
				}

				for (int i = 0; i < count; i++)
				{
					ShaderProgram program = programs[i];
					if (program != null && request.GpuDriven && program.IsLinked)
					{
						program.StartInstancedVariant();
					}
				}

				for (int i = 0; i < count; i++)
				{
					ShaderProgram program = programs[i];
					if (program != null && program.IsLinked)
					{
						// With it the programs of the passes the shader draws itself (its own shadow caster, geometry prepass).
						program.BuildPassVariants();
						if (request.GpuDriven)
						{
							program.GetInstancedVariant()?.BuildPassVariants();
						}
					}

					Console.Out.WriteLine(DoneLine + (first + i).ToString(CultureInfo.InvariantCulture));
					Console.Out.Flush();
				}
			}

			window.Dispose();
			return 0;
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     Starts a helper for the variants (main thread, the game's context current: its GL version and the binary
		///     cache's state are read); null when there is none to start (no executable, no binary cache) or it does not start.
		/// </summary>
		internal static Run TryStart(IReadOnlyList<ShaderVariant> variants, bool gpuDriven)
		{
			if (string.IsNullOrEmpty(Executable) || !File.Exists(Executable) || !ProgramBinaryCache.Enabled || variants.Count == 0)
			{
				return null;
			}

			string requestFile = Path.Combine(Path.GetTempPath(), $"LELEngine-shaders-{Environment.ProcessId}-{Interlocked.Increment(ref requests)}.txt");
			try
			{
				new Request
				{
					GLMajor = GLCapabilities.Major,
					GLMinor = GLCapabilities.Minor,
					DataFolder = AssetDatabase.Root,
					CacheFolder = ProgramBinaryCache.Folder,
					GpuDriven = gpuDriven,
					ParentProcess = Environment.ProcessId,
					Variants = new List<ShaderVariant>(variants)
				}.Write(requestFile);

				var start = new ProcessStartInfo(Executable)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					WorkingDirectory = Path.GetDirectoryName(Executable) ?? ""
				};
				start.ArgumentList.Add(Argument + "=" + requestFile);
				Process process = Process.Start(start);
				if (process == null)
				{
					File.Delete(requestFile);
					return null;
				}

				try
				{
					process.PriorityClass = ProcessPriorityClass.BelowNormal;
				}
				catch (Exception)
				{
					// It runs at the normal priority then.
				}

				return new Run(process, variants.Count, requestFile);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Shader] The shader compile helper did not start (" + e.Message + "): the compile thread compiles itself");
				TryDelete(requestFile);
				return null;
			}
		}

		#endregion

		#region PrivateMethods

		private static ShaderProgram TryGetProgram(ShaderVariant variant)
		{
			try
			{
				ShaderProgram program = variant.GetProgram();
				program.StartBuild();
				return program;
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(variant + ": " + e.Message);
				return null;
			}
		}

		private static bool ParentGone(int id)
		{
			try
			{
				using (Process parent = Process.GetProcessById(id))
				{
					return parent.HasExited;
				}
			}
			catch (ArgumentException)
			{
				return true;
			}
		}

		private static void TryDelete(string file)
		{
			try
			{
				File.Delete(file);
			}
			catch (Exception)
			{
				// A temporary file: Windows cleans its temp folder.
			}
		}

		#endregion

		#region Nested Types

		/// <summary>A running helper (game side): which variants it has reported done, and its end.</summary>
		internal sealed class Run
		{
			private readonly Process process;
			private readonly bool[] done;
			private readonly string requestFile;
			private readonly StringBuilder errors = new StringBuilder();
			private bool exited;

			public Run(Process process, int count, string requestFile)
			{
				this.process = process;
				this.requestFile = requestFile;
				done = new bool[count];
				process.OutputDataReceived += (_, e) => OnLine(e.Data);
				process.ErrorDataReceived += (_, e) =>
				{
					if (e.Data != null)
					{
						lock (errors)
						{
							errors.AppendLine(e.Data);
						}
					}
				};
				process.EnableRaisingEvents = true;
				process.Exited += (_, _) => OnExited();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
				if (process.HasExited)
				{
					OnExited();
				}
			}

			/// <summary>What the helper wrote on its error output (compile errors are the game's to report: it compiles those itself).</summary>
			public string Errors
			{
				get
				{
					lock (errors)
					{
						return errors.ToString();
					}
				}
			}

			public int ExitCode => exited ? SafeExitCode() : -1;

			/// <summary>Waits until the helper reported the variant done (true) or ended without it (false).</summary>
			public bool WaitFor(int index, Func<bool> cancelled)
			{
				lock (done)
				{
					while (!done[index] && !exited)
					{
						if (cancelled())
						{
							return false;
						}

						System.Threading.Monitor.Wait(done, 100);
					}

					return done[index];
				}
			}

			/// <summary>Ends the helper (the operation was dropped, or is done).</summary>
			public void Stop()
			{
				try
				{
					if (!process.HasExited)
					{
						process.Kill();
					}
				}
				catch (Exception)
				{
					// Gone already.
				}
			}

			private void OnLine(string line)
			{
				if (line == null || !line.StartsWith(DoneLine, StringComparison.Ordinal) || !int.TryParse(line.Substring(DoneLine.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
				{
					return;
				}

				lock (done)
				{
					if (index >= 0 && index < done.Length)
					{
						done[index] = true;
						System.Threading.Monitor.PulseAll(done);
					}
				}
			}

			private void OnExited()
			{
				// The output's last lines may still be on their way: wait for the reader to reach the end.
				try
				{
					process.WaitForExit();
				}
				catch (Exception)
				{
					// Gone.
				}

				lock (done)
				{
					exited = true;
					System.Threading.Monitor.PulseAll(done);
				}

				TryDelete(requestFile);
			}

			private int SafeExitCode()
			{
				try
				{
					return process.ExitCode;
				}
				catch (Exception)
				{
					return -1;
				}
			}
		}

		/// <summary>The request file: the game's context and folders, then a line per variant ("shader|KEYWORDS").</summary>
		private sealed class Request
		{
			public int GLMajor;
			public int GLMinor;
			public string DataFolder;
			public string CacheFolder;
			public bool GpuDriven;
			public int ParentProcess;
			public List<ShaderVariant> Variants = new List<ShaderVariant>();

			public void Write(string file)
			{
				var text = new StringBuilder();
				text.Append("gl ").Append(GLMajor).Append('.').Append(GLMinor).Append('\n');
				text.Append("data ").Append(DataFolder).Append('\n');
				text.Append("cache ").Append(CacheFolder).Append('\n');
				text.Append("instanced ").Append(GpuDriven ? 1 : 0).Append('\n');
				text.Append("parent ").Append(ParentProcess).Append('\n');
				foreach (ShaderVariant variant in Variants)
				{
					text.Append("variant ").Append(variant.Shader).Append('|').Append(string.Join(" ", variant.Keywords)).Append('\n');
				}

				File.WriteAllText(file, text.ToString());
			}

			public static Request Read(string file)
			{
				var request = new Request();
				foreach (string line in File.ReadAllLines(file))
				{
					int space = line.IndexOf(' ');
					if (space < 0)
					{
						continue;
					}

					string key = line.Substring(0, space);
					string value = line.Substring(space + 1);
					switch (key)
					{
						case "gl":
							string[] version = value.Split('.');
							request.GLMajor = int.Parse(version[0], CultureInfo.InvariantCulture);
							request.GLMinor = int.Parse(version[1], CultureInfo.InvariantCulture);
							break;
						case "data":
							request.DataFolder = value;
							break;
						case "cache":
							request.CacheFolder = value;
							break;
						case "instanced":
							request.GpuDriven = value == "1";
							break;
						case "parent":
							request.ParentProcess = int.Parse(value, CultureInfo.InvariantCulture);
							break;
						case "variant":
							int separator = value.IndexOf('|');
							string shader = separator < 0 ? value : value.Substring(0, separator);
							string keywords = separator < 0 ? "" : value.Substring(separator + 1);
							request.Variants.Add(new ShaderVariant(shader, keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
							break;
					}
				}

				return request;
			}
		}

		#endregion
	}
}
