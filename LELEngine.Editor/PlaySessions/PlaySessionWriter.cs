using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace LELEngine.Editor
{
	/// <summary>What a session records about where it ran (session.json), gathered on the main thread when it starts.</summary>
	internal sealed class SessionHeader
	{
		public DateTimeOffset Started;
		public string ProjectName;
		public string ProjectPath;
		public string Scene;
		public string EditorVersion;
		public string EditorBuilt;
		public string EditorFolder;
		public int ProcessId;
		public DateTimeOffset ProcessStarted;
		public string CommandLine;
		public bool Automated;
		public bool BatchMode;
		public bool ReloadScriptsOnPlay;
		public bool MaximizeOnPlay;
		public bool DebugOverlay;
		public bool AlwaysRefresh;
		public bool HitchReports;
		public double HitchThresholdMs;
		public string GameScripts;
		public string GraphicsApi;
		public string GpuVendor;
		public string GpuRenderer;
		public string GpuDriver;
		public bool DiscreteGpu;
		public float SuperSampling;
		public double FixedDeltaTime;
		public double MaximumDeltaTime;
	}

	/// <summary>The counters a session ends with that only the main thread knows.</summary>
	internal struct SessionTotals
	{
		public int Gen0;
		public int Gen1;
		public int Gen2;
		public double AllocatedMB;
		public double PausedSeconds;
		public double GcPauseMs;
		public int JitMethods;
	}

	/// <summary>
	///     Writes one play session into its folder on a thread of its own: the main thread only queues frames and events
	///     (<see cref="Add(in SessionFrame)" />, <see cref="Add(SessionEvent)" />), the thread formats them and writes them
	///     four times a second, so a crash loses at most that much. Files: session.json (where it ran, written at the start;
	///     at the end again with how it ended and a summary), frames.csv (a row per frame), events.jsonl (a JSON object per
	///     line: messages, input, scenes, state changes, hitches, GPU passes). The thread allocates little (numbers are
	///     formatted into buffers): the garbage collector stops the game's thread too.
	/// </summary>
	internal sealed class PlaySessionWriter
	{
		#region PublicFields

		/// <summary>The columns of frames.csv, in order.</summary>
		public const string FrameColumns = "row,seconds,game_frame,state,delta_ms,time_scale,frame_ms,update_ms,render_ms,gpu_ms,scene_view_ms,swap_ms,"
			+ "alloc_kb,process_alloc_kb,gc,gc_pause_ms,jit_methods,jit_ms,draw_calls,indirect_draws,triangles,opaque,transparent,culled,gpu_instances,lights,"
			+ "objects,behaviours,bodies,particles,heap_mb,process_mb,pool_threads,pool_queue,camera_x,camera_y,camera_z,mouse_x,mouse_y";

		public const string HeaderFile = "session.json";
		public const string FramesFile = "frames.csv";
		public const string EventsFile = "events.jsonl";

		/// <summary>The session's folder.</summary>
		public string Folder { get; }

		/// <summary>The thread has written everything and closed the files.</summary>
		public bool Finished => finished;

		/// <summary>Why writing stopped (the disk full, the folder gone), or null.</summary>
		public string Failure => failure;

		#endregion

		#region PrivateFields

		// How often the queued records are written (and the files flushed to the system: a crash of the editor loses at most
		// this much).
		private const int WriteIntervalMs = 250;

		private static readonly JsonWriterOptions JsonOptions = new JsonWriterOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			Indented = false
		};

		private readonly SessionHeader header;
		private readonly Thread thread;
		private readonly AutoResetEvent wake = new AutoResetEvent(false);
		private readonly object queueSync = new object();
		private readonly object ioSync = new object();
		private List<SessionFrame> frames = new List<SessionFrame>(1024);
		private List<SessionFrame> framesOut = new List<SessionFrame>(1024);
		private List<SessionEvent> events = new List<SessionEvent>(256);
		private List<SessionEvent> eventsOut = new List<SessionEvent>(256);
		private volatile bool ending;
		private volatile bool finished;
		private volatile string failure;
		private string endReason;
		private DateTimeOffset ended;
		private SessionTotals totals;

		// The files (the thread's; under ioSync when a crash flushes from another thread).
		private StreamWriter framesWriter;
		private FileStream eventsStream;
		private readonly ArrayBufferWriter<byte> eventBuffer = new ArrayBufferWriter<byte>(16384);
		private Utf8JsonWriter json;
		private readonly char[] number = new char[64];

		// The summary, gathered as the records are written.
		private long frameCount;
		private long firstGameFrame = -1;
		private long lastGameFrame;
		private double lastSeconds;
		private readonly List<float> frameTimes = new List<float>(4096);
		private readonly List<float> updateTimes = new List<float>(4096);
		private readonly List<float> gpuTimes = new List<float>(4096);
		private readonly List<SessionFrame> worst = new List<SessionFrame>();
		private float peakHeapMB;
		private float peakProcessMB;
		private int hitches;
		private int hitchReports;
		private readonly int[] messages = new int[5];
		private SessionLogEvent firstError;
		private SessionLogEvent firstException;
		private readonly List<string> scenes = new List<string>();
		private string projectGit;
		private string engineGit;

		#endregion

		#region Constructors

		public PlaySessionWriter(string folder, SessionHeader header)
		{
			Folder = folder;
			this.header = header;
			thread = new Thread(Run) { IsBackground = true, Name = "Play session recorder", Priority = ThreadPriority.BelowNormal };
			thread.Start();
		}

		#endregion

		#region PublicMethods

		public void Add(in SessionFrame frame)
		{
			if (ending)
			{
				return;
			}

			lock (queueSync)
			{
				frames.Add(frame);
			}
		}

		/// <summary>Any thread (messages come from wherever they are logged).</summary>
		public void Add(SessionEvent record)
		{
			if (ending)
			{
				return;
			}

			lock (queueSync)
			{
				events.Add(record);
			}
		}

		/// <summary>The session ended: what is queued is written, then the summary, and the thread ends.</summary>
		public void End(string reason, in SessionTotals sessionTotals)
		{
			if (ending)
			{
				return;
			}

			endReason = reason;
			ended = DateTimeOffset.Now;
			totals = sessionTotals;
			ending = true;
			wake.Set();
		}

		/// <summary>Writes what is queued now, on the calling thread (an exception nobody caught is ending the process).</summary>
		public void FlushNow()
		{
			Drain();
		}

		/// <summary>Waits for the thread to finish (the editor closing).</summary>
		public bool Join(int milliseconds)
		{
			return thread.Join(milliseconds);
		}

		#endregion

		#region PrivateMethods

		private void Run()
		{
			try
			{
				Directory.CreateDirectory(Folder);
				lock (ioSync)
				{
					framesWriter = new StreamWriter(new FileStream(Path.Combine(Folder, FramesFile), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536), new UTF8Encoding(false), 65536);
					framesWriter.WriteLine(FrameColumns);
					eventsStream = new FileStream(Path.Combine(Folder, EventsFile), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 65536);
					json = new Utf8JsonWriter(eventBuffer, JsonOptions);
				}

				projectGit = GitState(header.ProjectPath);
				engineGit = GitState(header.EditorFolder);
				WriteHeader(false);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				failure = e.Message;
			}

			while (true)
			{
				// Read before the drain: everything queued before End is written by it.
				bool last = ending;
				if (!last)
				{
					wake.WaitOne(WriteIntervalMs);
				}

				Drain();
				if (last)
				{
					break;
				}
			}

			try
			{
				WriteHeader(true);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				failure ??= e.Message;
			}

			lock (ioSync)
			{
				try
				{
					framesWriter?.Dispose();
					eventsStream?.Dispose();
				}
				catch (IOException)
				{
				}

				json?.Dispose();
				framesWriter = null;
				eventsStream = null;
			}

			finished = true;
		}

		private void Drain()
		{
			lock (ioSync)
			{
				lock (queueSync)
				{
					(frames, framesOut) = (framesOut, frames);
					(events, eventsOut) = (eventsOut, events);
				}

				try
				{
					if (failure == null && framesWriter != null)
					{
						foreach (SessionFrame frame in framesOut)
						{
							WriteFrame(frame);
						}

						foreach (SessionEvent record in eventsOut)
						{
							WriteEvent(record);
						}

						framesWriter.Flush();
						if (eventBuffer.WrittenCount > 0)
						{
							eventsStream.Write(eventBuffer.WrittenSpan);
							eventBuffer.ResetWrittenCount();
						}

						eventsStream.Flush();
					}
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ObjectDisposedException)
				{
					failure = e.Message;
				}
				finally
				{
					framesOut.Clear();
					eventsOut.Clear();
				}
			}
		}

		private void WriteFrame(in SessionFrame f)
		{
			Summarize(f);
			StreamWriter w = framesWriter;
			Integer(f.Row);
			w.Write(',');
			Decimal(f.Seconds, "0.0000");
			w.Write(',');
			Integer(f.GameFrame);
			w.Write(f.Step ? ",step," : ",play,");
			Float(f.DeltaMs, "0.###");
			w.Write(',');
			Float(f.TimeScale, "0.###");
			w.Write(',');
			Float(f.FrameMs, "0.###");
			w.Write(',');
			Float(f.UpdateMs, "0.###");
			w.Write(',');
			Float(f.RenderMs, "0.###");
			w.Write(',');
			Float(f.GpuMs, "0.###");
			w.Write(',');
			Float(f.SceneViewMs, "0.###");
			w.Write(',');
			Float(f.SwapMs, "0.###");
			w.Write(',');
			Float(f.AllocatedKB, "0.#");
			w.Write(',');
			Float(f.ProcessAllocatedKB, "0.#");
			w.Write(',');
			Integer(f.Collected);
			w.Write(',');
			Float(f.GcPauseMs, "0.###");
			w.Write(',');
			Integer(f.JitMethods);
			w.Write(',');
			Float(f.JitMs, "0.###");
			w.Write(',');
			Count(f.DrawCalls);
			w.Write(',');
			Count(f.IndirectDrawCalls);
			w.Write(',');
			Count(f.Triangles);
			w.Write(',');
			Count(f.Opaque);
			w.Write(',');
			Count(f.Transparent);
			w.Write(',');
			Count(f.Culled);
			w.Write(',');
			Count(f.GpuInstances);
			w.Write(',');
			Count(f.Lights);
			w.Write(',');
			Count(f.Objects);
			w.Write(',');
			Count(f.Behaviours);
			w.Write(',');
			Count(f.Bodies);
			w.Write(',');
			Count(f.Particles);
			w.Write(',');
			Float(f.HeapMB, "0.#");
			w.Write(',');
			Float(f.ProcessMB, "0.#");
			w.Write(',');
			Count(f.PoolThreads);
			w.Write(',');
			Count(f.PoolQueue);
			w.Write(',');
			Float(f.CameraX, "0.##");
			w.Write(',');
			Float(f.CameraY, "0.##");
			w.Write(',');
			Float(f.CameraZ, "0.##");
			w.Write(',');
			Float(f.MouseX, "0");
			w.Write(',');
			Float(f.MouseY, "0");
			w.WriteLine();
		}

		private void WriteEvent(SessionEvent record)
		{
			switch (record)
			{
				case SessionLogEvent log:
					messages[Math.Clamp((int)log.Type, 0, messages.Length - 1)]++;
					if (log.Type == LogType.Error && firstError == null)
					{
						firstError = log;
					}
					else if (log.Type == LogType.Exception && firstException == null)
					{
						firstException = log;
					}

					break;
				case SessionHitchEvent:
					hitches++;
					break;
				case SessionTextEvent text when text.What == "hitchReport":
					hitchReports++;
					break;
				// Play mode's own scene is not loaded but made the active one.
				case SessionSceneEvent scene when (scene.Action == "loaded" || scene.Action == "active") && scene.Name != null && !scenes.Contains(scene.Name):
					scenes.Add(scene.Name);
					break;
			}

			record.Write(json);
			json.Flush();
			eventBuffer.Write("\n"u8);
			json.Reset();
		}

		private void Summarize(in SessionFrame f)
		{
			frameCount++;
			if (firstGameFrame < 0)
			{
				firstGameFrame = f.GameFrame;
			}

			lastGameFrame = f.GameFrame;
			lastSeconds = f.Seconds;
			frameTimes.Add(f.FrameMs);
			updateTimes.Add(f.UpdateMs);
			if (!float.IsNaN(f.GpuMs))
			{
				gpuTimes.Add(f.GpuMs);
			}

			peakHeapMB = Math.Max(peakHeapMB, f.HeapMB);
			peakProcessMB = Math.Max(peakProcessMB, f.ProcessMB);

			// The ten longest frames, longest first (the first frame, the scene's start, is one of them on purpose).
			int at = worst.Count;
			while (at > 0 && worst[at - 1].FrameMs < f.FrameMs)
			{
				at--;
			}

			if (at < 10)
			{
				worst.Insert(at, f);
				if (worst.Count > 10)
				{
					worst.RemoveAt(10);
				}
			}
		}

		// ---------------------------------------------------------------- numbers without allocations

		private void Integer(long value)
		{
			value.TryFormat(number, out int written, default, CultureInfo.InvariantCulture);
			framesWriter.Write(number, 0, written);
		}

		private void Count(long value)
		{
			if (value >= 0)
			{
				Integer(value);
			}
		}

		private void Float(float value, string format)
		{
			if (float.IsNaN(value) || float.IsInfinity(value))
			{
				return;
			}

			value.TryFormat(number, out int written, format, CultureInfo.InvariantCulture);
			framesWriter.Write(number, 0, written);
		}

		private void Decimal(double value, string format)
		{
			value.TryFormat(number, out int written, format, CultureInfo.InvariantCulture);
			framesWriter.Write(number, 0, written);
		}

		// ---------------------------------------------------------------- session.json

		/// <summary>session.json: where the session ran; at the end also how it ended and what it held.</summary>
		private void WriteHeader(bool final)
		{
			var buffer = new ArrayBufferWriter<byte>(8192);
			using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = true }))
			{
				w.WriteStartObject();
				w.WriteString("format", "LELEngine play session");
				w.WriteNumber("version", 1);
				w.WriteString("started", header.Started.ToString("o", CultureInfo.InvariantCulture));
				if (final)
				{
					w.WriteString("ended", ended.ToString("o", CultureInfo.InvariantCulture));
					w.WriteString("endReason", endReason);
					w.WriteNumber("seconds", Math.Round((ended - header.Started).TotalSeconds, 3));
				}
				else
				{
					// Still recording, or the editor stopped without ending it (a crash): the next start of the project says so.
					w.WriteNull("ended");
				}

				if (failure != null)
				{
					w.WriteString("writeFailure", failure);
				}

				w.WriteString("scene", header.Scene ?? "");
				w.WriteStartObject("project");
				w.WriteString("name", header.ProjectName ?? "");
				w.WriteString("path", header.ProjectPath ?? "");
				Git(w, projectGit);
				w.WriteString("gameScripts", header.GameScripts ?? "");
				w.WriteEndObject();

				w.WriteStartObject("editor");
				w.WriteString("version", header.EditorVersion ?? "");
				w.WriteString("built", header.EditorBuilt ?? "");
				w.WriteString("folder", header.EditorFolder ?? "");
				Git(w, engineGit);
				w.WriteNumber("pid", header.ProcessId);
				w.WriteString("processStarted", header.ProcessStarted.ToString("o", CultureInfo.InvariantCulture));
				w.WriteString("commandLine", header.CommandLine ?? "");
				w.WriteBoolean("automated", header.Automated);
				w.WriteBoolean("batchMode", header.BatchMode);
				w.WriteBoolean("reloadScriptsOnPlay", header.ReloadScriptsOnPlay);
				w.WriteBoolean("maximizeOnPlay", header.MaximizeOnPlay);
				w.WriteBoolean("debugOverlay", header.DebugOverlay);
				w.WriteBoolean("sceneViewAlwaysRefresh", header.AlwaysRefresh);
				w.WriteBoolean("hitchReports", header.HitchReports);
				w.WriteNumber("hitchThresholdMs", header.HitchThresholdMs);
				w.WriteEndObject();

				w.WriteStartObject("machine");
				w.WriteString("name", Environment.MachineName);
				w.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
				w.WriteString("cpu", CpuName());
				w.WriteNumber("logicalProcessors", Environment.ProcessorCount);
				w.WriteNumber("memoryMB", Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0)));
				w.WriteString("runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
				w.WriteEndObject();

				w.WriteStartObject("gpu");
				w.WriteString("api", header.GraphicsApi ?? "");
				w.WriteString("vendor", header.GpuVendor ?? "");
				w.WriteString("renderer", header.GpuRenderer ?? "");
				w.WriteString("driver", header.GpuDriver ?? "");
				w.WriteBoolean("discrete", header.DiscreteGpu);
				w.WriteEndObject();

				w.WriteStartObject("settings");
				w.WriteNumber("superSampling", header.SuperSampling);
				w.WriteNumber("fixedDeltaTime", header.FixedDeltaTime);
				w.WriteNumber("maximumDeltaTime", header.MaximumDeltaTime);
				w.WriteEndObject();

				w.WriteStartObject("files");
				w.WriteString("frames", FramesFile + ": a row per frame of the game, columns as its first line names them; an empty field is not known that frame");
				w.WriteString("events", EventsFile + ": an object per line (t: seconds since the start; row: the frames.csv row of the frame it happened in, row 0 the frame that entered play mode; "
					+ "frame: Time.frameCount; kind: log, input, scene, state, view, gpu, hitch, hitchReport, controller, crash)");
				w.WriteEndObject();

				if (final)
				{
					WriteSummary(w);
				}

				w.WriteEndObject();
			}

			string path = Path.Combine(Folder, HeaderFile);
			string temporary = path + ".tmp";
			using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				stream.Write(buffer.WrittenSpan);
				stream.Flush(true);
			}

			File.Move(temporary, path, true);
		}

		private void WriteSummary(Utf8JsonWriter w)
		{
			w.WriteStartObject("summary");
			w.WriteNumber("frames", frameCount);
			w.WriteNumber("gameFrames", frameCount > 0 ? lastGameFrame - firstGameFrame + 1 : 0);
			w.WriteNumber("recordedSeconds", Math.Round(lastSeconds, 3));
			w.WriteNumber("pausedSeconds", Math.Round(totals.PausedSeconds, 3));
			Statistics(w, "frameMs", frameTimes);
			Statistics(w, "updateMs", updateTimes);
			Statistics(w, "gpuMs", gpuTimes);
			w.WriteNumber("hitches", hitches);
			w.WriteNumber("hitchReports", hitchReports);
			w.WriteStartArray("worstFrames");
			foreach (SessionFrame frame in worst)
			{
				w.WriteStartObject();
				w.WriteNumber("row", frame.Row);
				w.WriteNumber("seconds", Math.Round(frame.Seconds, 3));
				w.WriteNumber("gameFrame", frame.GameFrame);
				w.WriteNumber("frameMs", MathF.Round(frame.FrameMs, 2));
				w.WriteNumber("updateMs", MathF.Round(frame.UpdateMs, 2));
				if (!float.IsNaN(frame.RenderMs))
				{
					w.WriteNumber("renderMs", MathF.Round(frame.RenderMs, 2));
				}

				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteStartObject("messages");
			w.WriteNumber("log", messages[(int)LogType.Log]);
			w.WriteNumber("warning", messages[(int)LogType.Warning]);
			w.WriteNumber("error", messages[(int)LogType.Error]);
			w.WriteNumber("exception", messages[(int)LogType.Exception]);
			w.WriteEndObject();
			FirstMessage(w, "firstError", firstError);
			FirstMessage(w, "firstException", firstException);
			w.WriteStartArray("scenes");
			foreach (string scene in scenes)
			{
				w.WriteStringValue(scene);
			}

			w.WriteEndArray();
			w.WriteNumber("peakHeapMB", Math.Round(peakHeapMB, 1));
			w.WriteNumber("peakProcessMB", Math.Round(peakProcessMB, 1));
			w.WriteStartObject("collections");
			w.WriteNumber("gen0", totals.Gen0);
			w.WriteNumber("gen1", totals.Gen1);
			w.WriteNumber("gen2", totals.Gen2);
			w.WriteNumber("pauseMs", Math.Round(totals.GcPauseMs, 2));
			w.WriteEndObject();
			w.WriteNumber("allocatedMB", Math.Round(totals.AllocatedMB, 1));
			w.WriteNumber("jitMethods", totals.JitMethods);
			w.WriteEndObject();
		}

		private static void Statistics(Utf8JsonWriter w, string name, List<float> values)
		{
			if (values.Count == 0)
			{
				return;
			}

			float[] sorted = values.ToArray();
			Array.Sort(sorted);
			double sum = 0;
			foreach (float value in sorted)
			{
				sum += value;
			}

			w.WriteStartObject(name);
			w.WriteNumber("average", Math.Round(sum / sorted.Length, 3));
			w.WriteNumber("p50", MathF.Round(Percentile(sorted, 0.5), 3));
			w.WriteNumber("p95", MathF.Round(Percentile(sorted, 0.95), 3));
			w.WriteNumber("p99", MathF.Round(Percentile(sorted, 0.99), 3));
			w.WriteNumber("max", MathF.Round(sorted[sorted.Length - 1], 3));
			w.WriteEndObject();
		}

		private static float Percentile(float[] sorted, double fraction)
		{
			return sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
		}

		private static void FirstMessage(Utf8JsonWriter w, string name, SessionLogEvent message)
		{
			if (message == null)
			{
				return;
			}

			w.WriteStartObject(name);
			w.WriteNumber("t", Math.Round(message.Seconds, 3));
			w.WriteNumber("row", message.Row);
			w.WriteNumber("frame", message.Frame);
			w.WriteString("text", message.Text);
			w.WriteEndObject();
		}

		private static void Git(Utf8JsonWriter w, string state)
		{
			if (state != null)
			{
				w.WriteString("git", state);
			}
		}

		/// <summary>
		///     The branch and commit of the git repository holding <paramref name="folder" /> (read from its files, no git
		///     process; a submodule's .git file followed): "master 0123abcd...", or null outside a repository.
		/// </summary>
		private static string GitState(string folder)
		{
			try
			{
				for (string at = folder; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
				{
					string dotGit = Path.Combine(at, ".git");
					string gitDir = null;
					if (Directory.Exists(dotGit))
					{
						gitDir = dotGit;
					}
					else if (File.Exists(dotGit))
					{
						string line = File.ReadAllText(dotGit).Trim();
						if (line.StartsWith("gitdir:", StringComparison.Ordinal))
						{
							gitDir = Path.GetFullPath(Path.Combine(at, line.Substring("gitdir:".Length).Trim()));
						}
					}

					if (gitDir == null)
					{
						continue;
					}

					string head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
					if (!head.StartsWith("ref:", StringComparison.Ordinal))
					{
						return "(detached) " + head;
					}

					string reference = head.Substring(4).Trim();
					string branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference.Substring("refs/heads/".Length) : reference;
					string loose = Path.Combine(gitDir, reference.Replace('/', Path.DirectorySeparatorChar));
					if (File.Exists(loose))
					{
						return branch + " " + File.ReadAllText(loose).Trim();
					}

					string packed = Path.Combine(gitDir, "packed-refs");
					if (File.Exists(packed))
					{
						foreach (string line in File.ReadLines(packed))
						{
							if (line.EndsWith(" " + reference, StringComparison.Ordinal))
							{
								return branch + " " + line.Substring(0, line.IndexOf(' '));
							}
						}
					}

					return branch;
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
			{
			}

			return null;
		}

		private static string CpuName()
		{
			if (OperatingSystem.IsWindows())
			{
				try
				{
					using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
					if (key?.GetValue("ProcessorNameString") is string name)
					{
						return name.Trim();
					}
				}
				catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException)
				{
				}
			}

			return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
		}

		#endregion
	}
}
