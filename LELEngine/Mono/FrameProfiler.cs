using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LELEngine
{
	/// <summary>
	///     Where a frame's CPU time went, and what was done for the first time in it: the tool for hitches ("the game
	///     stutters the first time a ship dies"). Off unless asked for; then every frame is measured, and a frame that takes
	///     longer than <see cref="ThresholdMs" /> is logged as one block: its scopes as a tree (the hosts and the engine mark
	///     them: update phases, physics, render passes, instantiation, ...), the one-off work that happened in it (a shader
	///     program compiled or loaded from the binary cache, a texture decoded or uploaded, a mesh uploaded, a prefab read,
	///     a distance field built, a VFX graph compiled: <see cref="Event" />), what the JIT compiled and what the GC did.
	///     <para>
	///         Switched on by the environment: <c>LEL_HITCH=1</c> (or a number: the threshold in milliseconds, default
	///         <see cref="DefaultThresholdMs" />); <c>LEL_HITCH_EVENTS=1</c> also logs every event as it happens (a load's
	///         whole story, also outside frames and on worker threads); <c>LEL_FRAME_TRACE=file.csv</c> writes one line per
	///         frame (times, JIT, GC, event count) for a look at the frames around a hitch; <c>LEL_HITCH_LOG=file.log</c>
	///         writes the reports and events there instead of the console (a console line costs a call stack in the hosts'
	///         logs, which would be measured with the frame); <c>LEL_HITCH_JIT=1</c> adds a line per method the runtime
	///         compiles, stamped with the profiler's clock (which code ran for the first time in a hitch). Hosts may set
	///         the same from their command line (<see cref="Configure" />). Scripts mark their own scopes with
	///         <see cref="Begin" />.
	///     </para>
	///     Scopes are the main thread's (the thread that calls <see cref="BeginFrame" />); other threads' are ignored.
	///     Events come from any thread and go to the frame they ended in. Disabled, a scope is one static read.
	/// </summary>
	public static class FrameProfiler
	{
		#region PublicFields

		public const double DefaultThresholdMs = 25;

		/// <summary>Frames are measured (scopes and events recorded).</summary>
		public static bool Enabled { get; private set; }

		/// <summary>
		///     A frame that takes longer (its work and its wait for the GPU and the display together: what the player sees)
		///     and stands out from the frames before it (over <see cref="SpikeFactor" /> times their median, so a game that
		///     simply runs slowly is not logged every frame) is logged.
		/// </summary>
		public static double ThresholdMs { get; set; } = DefaultThresholdMs;

		/// <summary>How far over the recent frames' median a frame must be to count as a hitch.</summary>
		public static double SpikeFactor { get; set; } = 1.7;

		/// <summary>Every event is logged when it happens, not only with the hitch it is part of.</summary>
		public static bool LogEvents { get; set; }

		/// <summary>Frames measured since the profiler was switched on.</summary>
		public static long FrameCount => frameNumber;

		/// <summary>The longest frame (its work) since <see cref="ResetStatistics" />, milliseconds.</summary>
		public static double WorstFrameMs { get; private set; }

		/// <summary>Frames over the threshold since <see cref="ResetStatistics" />.</summary>
		public static int HitchCount { get; private set; }

		/// <summary>
		///     A hitch's report (the text <see cref="Write" /> logs: the frame's scope tree, its one-off work, the GPU, the
		///     runtime), raised on the main thread at the end of the frame it is about. A host keeping a record of its frames
		///     (the editor's play sessions) takes it from here.
		/// </summary>
		public static event Action<string> HitchReported;

		/// <summary>
		///     Hitch reports (and the line saying the profiler is on) are written to the profiler's log: its file, else the
		///     console. False: only <see cref="HitchReported" /> gets them (a host that switched the profiler on to record
		///     them itself; the editor's console would get each report's lines as messages of their own).
		/// </summary>
		public static bool WritesReports { get; set; } = true;

		#endregion

		#region PrivateFields

		private const int MaxEntries = 512;
		private const int MaxDepth = 32;

		private struct Entry
		{
			public string Name;
			public int Parent;
			public int Depth;
			public long Ticks;
			public int Count;
		}

		private struct Recorded
		{
			public string Kind;
			public string Name;
			public string Detail;
			public double Milliseconds;
			public bool MainThread;
		}

		private static readonly Entry[] entries = new Entry[MaxEntries];
		private static int entryCount;
		private static readonly int[] stack = new int[MaxDepth];
		private static readonly long[] stackStart = new long[MaxDepth];
		private static int depth;
		private static int mainThread = -1;
		private static bool inFrame;
		private static long frameStart;
		private static long waitTicks;
		private static long frameNumber;
		private static readonly Stopwatch clock = Stopwatch.StartNew();

		private static readonly object eventSync = new object();
		private static List<Recorded> events = new List<Recorded>();
		private static List<Recorded> frameEvents = new List<Recorded>();

		// The runtime's counters at the frame's start (the JIT's: this thread's, which stall the frame, and every thread's).
		private static long jitMethods;
		private static TimeSpan jitTime;
		private static long jitMethodsAll;
		private static TimeSpan jitTimeAll;
		private static JitListener jitListener;
		private static DateTime jitClockUtc;
		private static double jitClockSeconds;
		private static int gen0;
		private static int gen1;
		private static int gen2;
		private static TimeSpan gcPause;
		private static long allocated;

		private static StreamWriter trace;
		private static StreamWriter log;
		private static readonly object logSync = new object();

		// A thread that only sleeps a millisecond at a time: how late it wakes shows when the whole process (or machine)
		// was held up, which no scope of the frame explains (every scope just takes longer). The longest sleep since the
		// frame started, in Stopwatch ticks; its usual length for comparison.
		private static Thread stallThread;
		private static readonly object stallSync = new object();
		private static long stallMaxTicks;
		private static double stallTypicalMs;
		private static readonly double[] recent = new double[32];
		private static readonly double[] sorted = new double[32];
		private static long previousFrameStart;
		private static double interval;
		private static bool configured;
		private static double gpuTotal;
		private static string gpuHeaviest;
		private static double gpuHeaviestMs;

		#endregion

		#region Constructors

		static FrameProfiler()
		{
			Configure(Environment.GetEnvironmentVariable("LEL_HITCH"), Environment.GetEnvironmentVariable("LEL_HITCH_EVENTS"), Environment.GetEnvironmentVariable("LEL_FRAME_TRACE"),
				Environment.GetEnvironmentVariable("LEL_HITCH_LOG"), Environment.GetEnvironmentVariable("LEL_HITCH_JIT"));
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Switches the profiler on as the environment variables do (null leaves a setting as it is): <paramref name="hitch" />
		///     "1" or a threshold in milliseconds ("0" off), <paramref name="logEvents" /> "1", <paramref name="traceFile" /> a
		///     path for the per-frame CSV, <paramref name="logFile" /> a file for the reports and events (else the console),
		///     <paramref name="logJit" /> "1" for a line per compiled method.
		/// </summary>
		public static void Configure(string hitch, string logEvents, string traceFile, string logFile = null, string logJit = null)
		{
			if (!string.IsNullOrWhiteSpace(logFile) && log == null)
			{
				try
				{
					string path = Path.GetFullPath(logFile);
					Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
					log = new StreamWriter(path, false, new UTF8Encoding(false));
				}
				catch (Exception e)
				{
					Console.WriteLine("[Hitch] Cannot write the log " + logFile + ": " + e.Message);
				}
			}

			if (!string.IsNullOrWhiteSpace(hitch))
			{
				if (double.TryParse(hitch, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
				{
					Enabled = value > 0;
					ThresholdMs = value > 1 ? value : DefaultThresholdMs;
				}
				else
				{
					Enabled = true;
				}

				if (!Enabled)
				{
					// Off: the frame open now is no frame of the next time it is switched on (it would span the whole pause
					// and be reported as a hitch), and the stall watch ends (it only sleeps while the profiler is on).
					inFrame = false;
					depth = 0;
					previousFrameStart = 0;
				}
			}

			if (!string.IsNullOrWhiteSpace(logEvents))
			{
				LogEvents = logEvents != "0";
				Enabled |= LogEvents;
			}

			if (!string.IsNullOrWhiteSpace(traceFile) && trace == null)
			{
				try
				{
					string path = Path.GetFullPath(traceFile);
					Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
					trace = new StreamWriter(path, false, new UTF8Encoding(false));
					trace.WriteLine("frame,seconds,work_ms,wait_ms,interval_ms,jit_methods,jit_ms,gc0,gc1,gc2,gc_pause_ms,allocated_kb,events,slowest_scope,slowest_scope_ms,gpu_ms_lagging,gpu_heaviest_pass,gpu_heaviest_ms,jit_methods_all_threads,jit_ms_all_threads,longest_1ms_sleep_ms");
					Enabled = true;
				}
				catch (Exception e)
				{
					Console.WriteLine("[Hitch] Cannot write the frame trace " + traceFile + ": " + e.Message);
				}
			}

			if (Enabled && !configured && WritesReports)
			{
				configured = true;
				string summary = $"[Hitch] Frame profiler on: frames over {ThresholdMs.ToString("0.#", CultureInfo.InvariantCulture)} ms are logged" + (LogEvents ? ", every first-use event too" : "") + (trace != null ? ", per-frame trace written" : "");
				if (log != null)
				{
					Console.WriteLine(summary + ", into its own log");
				}

				Write(summary);
			}

			if (Enabled && jitListener == null && !string.IsNullOrWhiteSpace(logJit) && logJit != "0")
			{
				jitClockUtc = DateTime.UtcNow;
				jitClockSeconds = clock.Elapsed.TotalSeconds;
				jitListener = new JitListener();
			}

			lock (stallSync)
			{
				if (Enabled && stallThread == null)
				{
					stallThread = new Thread(WatchForStalls) { IsBackground = true, Name = "Hitch profiler: stall watch", Priority = ThreadPriority.AboveNormal };
					stallThread.Start();
				}
			}
		}

		private static void WatchForStalls()
		{
			long last = Stopwatch.GetTimestamp();
			double typical = 0;
			while (true)
			{
				// The profiler switched off (Configure "0"): the watch ends; switching it on again starts another.
				lock (stallSync)
				{
					if (!Enabled)
					{
						stallThread = null;
						return;
					}
				}

				Thread.Sleep(1);
				long now = Stopwatch.GetTimestamp();
				long slept = now - last;
				last = now;
				long max;
				do
				{
					max = Interlocked.Read(ref stallMaxTicks);
				}
				while (slept > max && Interlocked.CompareExchange(ref stallMaxTicks, slept, max) != max);

				// The usual sleep (the system timer's step: 1 to 16 ms), followed slowly.
				double ms = Milliseconds(slept);
				typical = typical <= 0 ? ms : typical + (Math.Min(ms, typical * 2) - typical) * 0.01;
				Volatile.Write(ref stallTypicalMs, typical);
			}
		}

		/// <summary>
		///     A line of the profiler's log (reports, events; scripts add their own marks: a test's phases): its own file when
		///     one is set, else the console's output without the call stack the hosts' logs add to console lines.
		/// </summary>
		public static void Write(string text)
		{
			lock (logSync)
			{
				if (log != null)
				{
					log.WriteLine(text);
				}
				else
				{
					ConsoleLogWriter.ConsoleOutput.WriteLine(text);
				}
			}
		}

		/// <summary>Seconds on the profiler's own clock (events and frames are stamped with it).</summary>
		public static double Seconds => clock.Elapsed.TotalSeconds;

		/// <summary>A host's frame starts (its thread is the main thread from now on).</summary>
		public static void BeginFrame()
		{
			if (!Enabled)
			{
				return;
			}

			if (inFrame)
			{
				EndFrame();
			}

			if (mainThread < 0)
			{
				if (jitListener != null && OperatingSystem.IsWindows())
				{
					// The compiled methods' lines name the system's thread ids.
					Write($"[Hitch] The main thread is system thread {GetCurrentThreadId()}");
				}

				// What happened before the first frame (the host's loading) is no part of that frame.
				lock (eventSync)
				{
					events.Clear();
				}
			}

			mainThread = Environment.CurrentManagedThreadId;
			inFrame = true;
			entryCount = 0;
			depth = 0;
			waitTicks = 0;
			Interlocked.Exchange(ref stallMaxTicks, 0);
			frameStart = Stopwatch.GetTimestamp();
			interval = previousFrameStart != 0 ? Milliseconds(frameStart - previousFrameStart) : 0;
			previousFrameStart = frameStart;
			jitMethods = System.Runtime.JitInfo.GetCompiledMethodCount(true);
			jitTime = System.Runtime.JitInfo.GetCompilationTime(true);
			jitMethodsAll = System.Runtime.JitInfo.GetCompiledMethodCount();
			jitTimeAll = System.Runtime.JitInfo.GetCompilationTime();
			gen0 = GC.CollectionCount(0);
			gen1 = GC.CollectionCount(1);
			gen2 = GC.CollectionCount(2);
			gcPause = GC.GetTotalPauseDuration();
			allocated = GC.GetAllocatedBytesForCurrentThread();
		}

		/// <summary>The frame's work is done: logged when it took too long.</summary>
		public static void EndFrame()
		{
			if (!Enabled || !inFrame)
			{
				return;
			}

			long end = Stopwatch.GetTimestamp();
			inFrame = false;
			depth = 0;
			double total = Milliseconds(end - frameStart);
			double wait = Milliseconds(waitTicks);
			double work = Math.Max(0, total - wait);
			frameNumber++;

			lock (eventSync)
			{
				(frameEvents, events) = (events, frameEvents);
				events.Clear();
			}

			long methods = System.Runtime.JitInfo.GetCompiledMethodCount(true) - jitMethods;
			double jitMs = (System.Runtime.JitInfo.GetCompilationTime(true) - jitTime).TotalMilliseconds;
			long methodsAll = System.Runtime.JitInfo.GetCompiledMethodCount() - jitMethodsAll;
			double jitMsAll = (System.Runtime.JitInfo.GetCompilationTime() - jitTimeAll).TotalMilliseconds;
			int collections0 = GC.CollectionCount(0) - gen0;
			int collections1 = GC.CollectionCount(1) - gen1;
			int collections2 = GC.CollectionCount(2) - gen2;
			double pause = (GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds;
			long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
			double stallMs = Milliseconds(Interlocked.Read(ref stallMaxTicks));
			double stallTypical = Volatile.Read(ref stallTypicalMs);

			if (total > WorstFrameMs)
			{
				WorstFrameMs = total;
			}

			if (trace != null)
			{
				int slowest = SlowestLeaf();
				trace.Write(frameNumber.ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(clock.Elapsed.TotalSeconds.ToString("0.0000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(work.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(wait.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(interval.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(methods.ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(jitMs.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(collections0.ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(collections1.ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(collections2.ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(pause.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write((bytes / 1024).ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(frameEvents.Count.ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(slowest >= 0 ? PathOf(slowest).Replace(',', ';') : "");
				trace.Write(',');
				trace.Write(slowest >= 0 ? Milliseconds(entries[slowest].Ticks).ToString("0.000", CultureInfo.InvariantCulture) : "0");
				trace.Write(',');
				trace.Write(gpuTotal.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write((gpuHeaviest ?? "").Replace(',', ';'));
				trace.Write(',');
				trace.Write(gpuHeaviestMs.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(methodsAll.ToString(CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.Write(jitMsAll.ToString("0.000", CultureInfo.InvariantCulture));
				trace.Write(',');
				trace.WriteLine(stallMs.ToString("0.000", CultureInfo.InvariantCulture));
				if (total > ThresholdMs || frameNumber % 120 == 0)
				{
					trace.Flush();
				}
			}

			if (total > ThresholdMs && total > SpikeFactor * RecentMedian())
			{
				HitchCount++;
				Report(work, wait, methods, jitMs, methodsAll, jitMsAll, collections0, collections1, collections2, pause, bytes, stallMs, stallTypical);
			}

			recent[frameNumber % recent.Length] = total;
			frameEvents.Clear();
		}

		/// <summary>
		///     A named part of the frame on the main thread, as <c>using (FrameProfiler.Begin("Physics"))</c>: its time is
		///     added to the entry of that name under the scope it is opened in (a part entered many times is one line with
		///     its count). Free while the profiler is off.
		/// </summary>
		public static Scope Begin(string name)
		{
			if (!Enabled || !inFrame || Environment.CurrentManagedThreadId != mainThread || depth >= MaxDepth)
			{
				return default;
			}

			int parent = depth > 0 ? stack[depth - 1] : -1;
			int index = -1;
			for (int i = entryCount - 1; i >= 0; i--)
			{
				if (entries[i].Parent == parent && ReferenceEquals(entries[i].Name, name))
				{
					index = i;
					break;
				}
			}

			if (index < 0)
			{
				for (int i = entryCount - 1; i >= 0; i--)
				{
					if (entries[i].Parent == parent && entries[i].Name == name)
					{
						index = i;
						break;
					}
				}
			}

			if (index < 0)
			{
				if (entryCount >= MaxEntries)
				{
					return default;
				}

				index = entryCount++;
				entries[index] = new Entry { Name = name, Parent = parent, Depth = depth };
			}

			stack[depth] = index;
			stackStart[depth] = Stopwatch.GetTimestamp();
			depth++;
			return new Scope(depth);
		}

		/// <summary>
		///     A wait that is no work (the swap waiting for the display's refresh): its time is taken out of the frame's work,
		///     so a frame is never a hitch for having waited for vsync.
		/// </summary>
		public static void AddWait(long stopwatchTicks)
		{
			if (Enabled && inFrame && Environment.CurrentManagedThreadId == mainThread)
			{
				waitTicks += Math.Max(0, stopwatchTicks);
			}
		}

		/// <summary>
		///     Something done once (or rarely) that took time: a program compiled, a texture decoded, a prefab read. Any
		///     thread. <paramref name="kind" /> groups them ("shader", "texture", "mesh", "prefab", ...), <paramref name="name" />
		///     says which asset, <paramref name="detail" /> how (cache hit, sizes).
		/// </summary>
		public static void Event(string kind, string name, double milliseconds, string detail = null)
		{
			if (!Enabled)
			{
				return;
			}

			bool main = Environment.CurrentManagedThreadId == mainThread;
			var recorded = new Recorded { Kind = kind, Name = name, Detail = detail, Milliseconds = milliseconds, MainThread = main };
			lock (eventSync)
			{
				events.Add(recorded);
			}

			if (LogEvents)
			{
				Write($"[Event] {clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} s frame {(inFrame ? (frameNumber + 1).ToString(CultureInfo.InvariantCulture) : "-")} {Format(recorded)}");
			}
		}

		/// <summary>
		///     What the GPU's timer queries say now (the renderer, once a frame): the passes' total and the heaviest one.
		///     They are a few frames old (queries are read when ready, never waited for), so a frame's own GPU cost shows in
		///     the trace lines after it; a GPU-bound frame itself shows as a long swap wait.
		/// </summary>
		public static void NoteGpu(double totalMs, string heaviestPass, double heaviestMs)
		{
			gpuTotal = totalMs;
			gpuHeaviest = heaviestPass;
			gpuHeaviestMs = heaviestMs;
		}

		/// <summary>An event measured from a <see cref="Stopwatch.GetTimestamp" /> start to now.</summary>
		public static void EventSince(long startTimestamp, string kind, string name, string detail = null)
		{
			if (Enabled)
			{
				Event(kind, name, Milliseconds(Stopwatch.GetTimestamp() - startTimestamp), detail);
			}
		}

		/// <summary><see cref="Stopwatch.GetTimestamp" /> when the profiler is on (0 when off: pair it with <see cref="EventSince" />).</summary>
		public static long Timestamp => Enabled ? Stopwatch.GetTimestamp() : 0;

		/// <summary>Starts the worst-frame and hitch counters again (a test measures a stretch of the game).</summary>
		public static void ResetStatistics()
		{
			WorstFrameMs = 0;
			HitchCount = 0;
		}

		/// <summary>Writes what the trace and log files hold so far (a host closing).</summary>
		public static void Flush()
		{
			trace?.Flush();
			lock (logSync)
			{
				log?.Flush();
			}
		}

		#endregion

		#region InternalMethods

		internal static void End(int scopeDepth)
		{
			// A scope closed out of order (an exception unwound past inner scopes) closes those too.
			if (!inFrame || scopeDepth <= 0 || scopeDepth > depth || Environment.CurrentManagedThreadId != mainThread)
			{
				return;
			}

			long now = Stopwatch.GetTimestamp();
			while (depth >= scopeDepth)
			{
				depth--;
				int index = stack[depth];
				entries[index].Ticks += now - stackStart[depth];
				entries[index].Count++;
			}
		}

		#endregion

		#region PrivateMethods

		[System.Runtime.InteropServices.DllImport("kernel32.dll")]
		private static extern uint GetCurrentThreadId();

		private static double Milliseconds(long ticks)
		{
			return ticks * 1000.0 / Stopwatch.Frequency;
		}

		// The median of the frames before this one (0 until there are a few: the first frames are always reported).
		private static double RecentMedian()
		{
			int count = (int)Math.Min(frameNumber - 1, recent.Length);
			if (count < 8)
			{
				return 0;
			}

			Array.Copy(recent, sorted, recent.Length);
			Array.Sort(sorted, 0, recent.Length);
			// Unfilled slots are zeros at the front: the median of the filled ones.
			return sorted[recent.Length - count + count / 2];
		}

		private static string Format(Recorded e)
		{
			return $"{e.Kind}: {e.Name} {e.Milliseconds.ToString("0.00", CultureInfo.InvariantCulture)} ms" + (string.IsNullOrEmpty(e.Detail) ? "" : " (" + e.Detail + ")") + (e.MainThread ? "" : " [worker thread]");
		}

		// The entry without children that took the longest: what a one-line summary names.
		private static int SlowestLeaf()
		{
			int best = -1;
			for (int i = 0; i < entryCount; i++)
			{
				bool leaf = true;
				for (int j = i + 1; j < entryCount; j++)
				{
					if (entries[j].Parent == i)
					{
						leaf = false;
						break;
					}
				}

				if (leaf && (best < 0 || entries[i].Ticks > entries[best].Ticks))
				{
					best = i;
				}
			}

			return best;
		}

		private static string PathOf(int index)
		{
			string path = entries[index].Name;
			for (int parent = entries[index].Parent; parent >= 0; parent = entries[parent].Parent)
			{
				path = entries[parent].Name + " > " + path;
			}

			return path;
		}

		private static void Report(double work, double wait, long methods, double jitMs, long methodsAll, double jitMsAll, int collections0, int collections1, int collections2, double pause, long bytes, double stallMs, double stallTypical)
		{
			CultureInfo c = CultureInfo.InvariantCulture;
			var text = new StringBuilder();
			text.Append("[Hitch] frame ").Append(frameNumber).Append(" at ").Append(clock.Elapsed.TotalSeconds.ToString("0.00", c)).Append(" s: ")
				.Append((work + wait).ToString("0.0", c)).Append(" ms = ").Append(work.ToString("0.0", c)).Append(" ms of work + ")
				.Append(wait.ToString("0.0", c)).Append(" ms waiting in the swap (the GPU finishing the frame before, the display)");

			text.Append("; the frames before: ");
			for (int i = 1; i <= 4; i++)
			{
				long n = frameNumber - i;
				text.Append(n > 0 ? recent[n % recent.Length].ToString("0.0", c) : "-").Append(i < 4 ? " " : " ms\n");
			}

			// The tree, children under their parent in the order they first ran; small entries left out.
			double floor = Math.Max(0.2, work * 0.01);
			AppendChildren(text, -1, floor, c);

			if (frameEvents.Count > 0)
			{
				text.Append("  first use / one-off work in this frame:\n");
				double sum = 0;
				foreach (Recorded e in frameEvents)
				{
					text.Append("    ").Append(Format(e)).Append('\n');
					if (e.MainThread)
					{
						sum += e.Milliseconds;
					}
				}

				text.Append("    = ").Append(sum.ToString("0.0", c)).Append(" ms on the main thread in ").Append(frameEvents.Count).Append(" events\n");
			}

			if (gpuTotal > 0)
			{
				text.Append("  GPU passes (timer queries, a few frames old): ").Append(gpuTotal.ToString("0.0", c)).Append(" ms in all, the heaviest ")
					.Append(gpuHeaviest).Append(' ').Append(gpuHeaviestMs.ToString("0.0", c)).Append(" ms\n");
			}

			text.Append("  runtime: JIT on this thread ").Append(methods).Append(" methods ").Append(jitMs.ToString("0.0", c)).Append(" ms (every thread: ").Append(methodsAll)
				.Append(" methods ").Append(jitMsAll.ToString("0.0", c)).Append(" ms); GC gen0 +").Append(collections0)
				.Append(" gen1 +").Append(collections1).Append(" gen2 +").Append(collections2).Append(", paused ").Append(pause.ToString("0.0", c))
				.Append(" ms; allocated ").Append((bytes / 1024.0 / 1024.0).ToString("0.00", c)).Append(" MB on the main thread");
			// A sleeping thread woken late: the process was not running (other programs, the system, a driver), and every
			// scope above is longer by it.
			if (stallMs > Math.Max(8, stallTypical * 4))
			{
				text.Append("\n  the machine: a thread sleeping 1 ms at a time woke up to ").Append(stallMs.ToString("0.0", c)).Append(" ms late in this frame (usually ")
					.Append(stallTypical.ToString("0.0", c)).Append(" ms): the process was held up from outside (the system, another program, a driver), not by the scopes above");
			}

			string report = text.ToString();
			if (WritesReports)
			{
				Write(report);
				lock (logSync)
				{
					log?.Flush();
				}
			}

			HitchReported?.Invoke(report);
		}

		private static void AppendChildren(StringBuilder text, int parent, double floor, CultureInfo c)
		{
			for (int i = 0; i < entryCount; i++)
			{
				if (entries[i].Parent != parent)
				{
					continue;
				}

				double ms = Milliseconds(entries[i].Ticks);
				if (ms < floor)
				{
					continue;
				}

				text.Append(' ', 2 + entries[i].Depth * 2).Append(entries[i].Name).Append(' ').Append(ms.ToString("0.00", c)).Append(" ms");
				if (entries[i].Count > 1)
				{
					text.Append(" (x").Append(entries[i].Count).Append(')');
				}

				text.Append('\n');
				AppendChildren(text, i, floor, c);
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>
		///     The runtime's "method compiled" events as log lines stamped with the profiler's clock (they arrive a moment
		///     after the fact, on the runtime's own thread): tier 3 = quick first compile, 2 / 1 = compiled whole at once
		///     (dynamic methods: compiled expressions), 4 = the background's optimized recompile (stalls nothing).
		/// </summary>
		private sealed class JitListener : System.Diagnostics.Tracing.EventListener
		{
			protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
			{
				if (source.Name == "Microsoft-Windows-DotNETRuntime")
				{
					// Keyword 0x10 = JIT; verbose: the events that carry the method's names.
					EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x10);
				}
			}

			protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
			{
				if (e.EventName == null || e.Payload == null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal) || e.Payload.Count < 8)
				{
					return;
				}

				// MethodID, ModuleID, MethodStartAddress, MethodSize, MethodToken, MethodFlags, MethodNamespace, MethodName, ...
				uint flags = e.Payload[5] is uint f ? f : 0;
				double seconds = jitClockSeconds + (e.TimeStamp.ToUniversalTime() - jitClockUtc).TotalSeconds;
				Write($"[Jit] {seconds.ToString("0.0000", CultureInfo.InvariantCulture)} s thread {e.OSThreadId} tier {(flags >> 7) & 7} size {e.Payload[3]} {e.Payload[6]}.{e.Payload[7]}");
			}
		}

		/// <summary>An open scope of <see cref="Begin" />: disposing it ends the scope.</summary>
		public readonly struct Scope : IDisposable
		{
			private readonly int depth;

			internal Scope(int depth)
			{
				this.depth = depth;
			}

			public void Dispose()
			{
				if (depth > 0)
				{
					End(depth);
				}
			}
		}

		#endregion
	}
}
