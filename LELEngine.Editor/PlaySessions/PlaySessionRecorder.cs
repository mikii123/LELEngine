using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LELEngine.InputSystem;
using LELEngine.Rendering;

namespace LELEngine.Editor
{
	/// <summary>
	///     Records every play mode session into the project's Library/PlaySessions (Preferences > Play Sessions: on or off,
	///     how many days and how much space they keep), so a problem seen once and hard to reproduce can be looked at
	///     afterwards. Per frame what the Game view's debug overlay shows (frame time, the game's update, the view's render
	///     on the CPU and the GPU, allocations and collections, draw calls, triangles, renderers, lights, objects, updating
	///     components, physics bodies, particles, memory) and more (the swap's wait, the Scene view's render, GC pauses,
	///     methods the JIT compiled, the thread pool, the camera's place, the mouse); as events every console message
	///     (with the frames.csv row of its frame), the player's input, scenes loaded, pauses and steps, the Game view's size,
	///     its GPU passes once a second, hitches and the hitch profiler's reports of them, controllers, an exception that
	///     ends the editor. The main thread only copies numbers into a queue; a <see cref="PlaySessionWriter" /> thread per
	///     session formats and writes them.
	///     <para>
	///         Automated runs record only with a preferences file of their own (preferencesfile=), as they save no other
	///         preference.
	///     </para>
	/// </summary>
	internal sealed class PlaySessionRecorder
	{
		#region PublicFields

		/// <summary>Bumped when the project's sessions change (one started, ended, written, deleted): lists read them again.</summary>
		public int Version => version;

		/// <summary>The project's sessions folder (null: no project).</summary>
		public string Root { get; private set; }

		/// <summary>A session records now.</summary>
		public bool IsRecording => current != null;

		/// <summary>The folder being recorded into now, or null.</summary>
		public string CurrentFolder => current?.Folder;

		/// <summary>Why the session that ends next ends, when the editor ends it for a reason of its own (the project closing).</summary>
		public string EndReason;

		/// <summary>The last listing of the project's sessions (<see cref="RequestListing" />), newest first; null before the first.</summary>
		public List<PlaySessionListing> Listing => listing;

		/// <summary>The <see cref="Version" /> the <see cref="Listing" /> was read at.</summary>
		public int ListingVersion => listingVersion;

		/// <summary>Sessions are recorded: the preference, and a preferences file (automated runs without one record nothing).</summary>
		public static bool Enabled => EditorPreferences.File != null && EditorPreferences.Current.PlaySessions.Record;

		#endregion

		#region PrivateFields

		// The slow numbers are read four times a second (as the overlay does), the GPU passes once a second.
		private const double SlowSeconds = 0.25;
		private const double GpuSeconds = 1.0;

		private readonly EditorApplication editor;
		private readonly object writersSync = new object();
		private readonly List<PlaySessionWriter> writers = new List<PlaySessionWriter>();
		private volatile PlaySessionWriter current;
		private int version;
		private volatile List<PlaySessionListing> listing;
		private volatile int listingVersion = -1;
		private int maintaining;
		private int listingRunning;

		// The session's clock and rows.
		private long started;
		private long rows;
		private long statsTaken;
		private bool stepped;
		private bool profilerStarted;

		// Paused time.
		private long pausedAt;
		private double pausedSeconds;

		// The runtime's counters at the last row (and at the start, for the totals).
		private TimeSpan gcPause;
		private TimeSpan gcPauseStart;
		private long jitMethods;
		private long jitMethodsStart;
		private TimeSpan jitTime;
		private long processAllocated;
		private long processAllocatedStart;
		private readonly int[] collectionsStart = new int[3];

		// What was last seen of the views.
		private int gameViewFrame;
		private int sceneViewFrame;
		private int viewWidth;
		private int viewHeight;
		private int viewRenderWidth;
		private int viewRenderHeight;
		private int viewCameras;
		private bool inFront = true;

		// The slow numbers.
		private long nextSlow;
		private long nextGpu;
		private int objects;
		private int behaviours;
		private int bodies;
		private int particles;
		private float heapMB;
		private float processMB;
		private int poolThreads;
		private long poolQueue;

		// Hitches: the frame times before this one.
		private readonly float[] recent = new float[32];
		private readonly float[] sorted = new float[32];
		private int recentCount;

		// The frame's input, read before the game's frame ends (Input.EndFrame forgets it) into buffers that do not allocate:
		// the game's update measures its allocations.
		private readonly List<int> keysDown = new List<int>(16);
		private readonly List<int> keysUp = new List<int>(16);
		private int mouseDown;
		private int mouseUp;
		private float wheelX;
		private float wheelY;
		private InputConfiguration actionsConfiguration;
		private int actionsPlayers = -1;
		private string[] actionNames = Array.Empty<string>();
		private int[] actionIds = Array.Empty<int>();
		private InputPlayer[] actionPlayers = Array.Empty<InputPlayer>();
		private float[] actionValues = Array.Empty<float>();
		private float[] actionWritten = Array.Empty<float>();
		private bool actionsSampled;

		#endregion

		#region Constructors

		public PlaySessionRecorder(EditorApplication editor)
		{
			this.editor = editor;
			editor.Log.EntryAdded += OnEntry;
			FrameProfiler.HitchReported += OnHitchReport;
			SceneManager.SceneLoaded += OnSceneLoaded;
			SceneManager.SceneUnloaded += OnSceneUnloaded;
			SceneManager.ActiveSceneChanged += OnActiveSceneChanged;
			InputManager.ControllerConnected += OnControllerConnected;
			InputManager.ControllerDisconnected += OnControllerDisconnected;
			AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
			AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
		}

		#endregion

		#region PublicMethods

		/// <summary>A project opened: its sessions folder, kept within the preferences (on a worker thread).</summary>
		public void ProjectOpened(ProjectInfo project)
		{
			Root = PlaySessionStore.RootOf(project);
			listing = null;
			listingVersion = -1;
			version++;
			MaintainLater();
		}

		public void ProjectClosed()
		{
			End(EndReason ?? "the project closed");
			Root = null;
			listing = null;
			listingVersion = -1;
			version++;
		}

		/// <summary>Play mode starts (before the edit scene goes): a session of <paramref name="scenePath" /> begins recording.</summary>
		public void Begin(string scenePath)
		{
			End(EndReason ?? "a new session started");
			EndReason = null;
			if (!Enabled || Root == null)
			{
				return;
			}

			DateTimeOffset now = DateTimeOffset.Now;
			PlaySessionPreferences preferences = EditorPreferences.Current.PlaySessions;
			if (preferences.HitchReports && !FrameProfiler.Enabled)
			{
				// Only while the session records, its reports into the session alone (the console would get each line as a
				// message); a profiler switched on from outside (LEL_HITCH) stays as it is and logs as asked.
				FrameProfiler.WritesReports = false;
				FrameProfiler.Configure("1", null, null);
				profilerStarted = true;
			}

			SessionHeader header = Header(now, scenePath);
			started = Stopwatch.GetTimestamp();
			Interlocked.Exchange(ref rows, 0);
			statsTaken = editor.GameView.Stats.Taken;
			stepped = false;
			pausedAt = 0;
			pausedSeconds = 0;
			gcPause = gcPauseStart = GC.GetTotalPauseDuration();
			jitMethods = jitMethodsStart = System.Runtime.JitInfo.GetCompiledMethodCount();
			jitTime = System.Runtime.JitInfo.GetCompilationTime();
			processAllocated = processAllocatedStart = GC.GetTotalAllocatedBytes();
			for (int generation = 0; generation < collectionsStart.Length; generation++)
			{
				collectionsStart[generation] = GC.CollectionCount(generation);
			}

			gameViewFrame = editor.GameView.ViewRenderer?.Renderer?.LastFrame.Frame ?? 0;
			sceneViewFrame = editor.SceneView.ViewRenderer?.Renderer?.LastFrame.Frame ?? 0;
			viewWidth = viewHeight = viewRenderWidth = viewRenderHeight = viewCameras = -1;
			inFront = true;
			nextSlow = 0;
			nextGpu = started + (long)(GpuSeconds * Stopwatch.Frequency);
			recentCount = 0;
			ClearInput();
			actionsConfiguration = null;
			actionsPlayers = -1;

			var writer = new PlaySessionWriter(PlaySessionStore.NewFolder(Root, now.LocalDateTime, scenePath), header);
			lock (writersSync)
			{
				writers.RemoveAll(w => w.Finished);
				writers.Add(writer);
			}

			current = writer;
			version++;
		}

		/// <summary>
		///     The game's frame is about to end (before <see cref="Input.EndFrame" />): the keys and buttons pressed and released
		///     in it, and the input actions' values. Allocates nothing (the game's update measures what it allocates).
		/// </summary>
		public void SampleInput()
		{
			if (current == null)
			{
				return;
			}

			foreach (Input.KeyController key in Input.Pressed)
			{
				if (key.frame == 0 && keysDown.Count < 64)
				{
					keysDown.Add((int)key.key);
				}
			}

			foreach (Input.KeyController key in Input.UnPressed)
			{
				if (key.frame == 0 && keysUp.Count < 64)
				{
					keysUp.Add((int)key.key);
				}
			}

			for (int button = 0; button < 8; button++)
			{
				if (Input.GetMouseButtonDown((LELEngine.Windowing.MouseButton)button))
				{
					mouseDown |= 1 << button;
				}

				if (Input.GetMouseButtonUp((LELEngine.Windowing.MouseButton)button))
				{
					mouseUp |= 1 << button;
				}
			}

			wheelX += Input.mouseScrollDelta.X;
			wheelY += Input.mouseScrollDelta.Y;
			if (InputManager.Configuration != actionsConfiguration || InputManager.Players.Count != actionsPlayers)
			{
				// Read the next time outside the game's update (it allocates the names once).
				actionsSampled = false;
				return;
			}

			for (int i = 0; i < actionIds.Length; i++)
			{
				actionValues[i] = actionPlayers[i].GetAxis(actionIds[i]);
			}

			actionsSampled = true;
		}

		/// <summary>One frame of the paused game ran (Step): its row is marked as a step.</summary>
		public void Stepped()
		{
			if (current != null)
			{
				stepped = true;
				Note("step");
			}
		}

		/// <summary>The game paused (true) or went on (false).</summary>
		public void Paused(bool paused)
		{
			if (current == null)
			{
				return;
			}

			long now = Stopwatch.GetTimestamp();
			if (paused && pausedAt == 0)
			{
				pausedAt = now;
			}
			else if (!paused && pausedAt != 0)
			{
				pausedSeconds += (now - pausedAt) / (double)Stopwatch.Frequency;
				pausedAt = 0;
			}

			Note(paused ? "paused" : "resumed");
		}

		/// <summary>A change of the session's state worth a line (paused, the scripts reloaded, ...).</summary>
		public void Note(string what, string detail = null)
		{
			Add(new SessionNoteEvent { What = what, Detail = detail });
		}

		/// <summary>
		///     The editor's frame ended (the views rendered, the overlay's sample taken): the frame's row, when the game ran in
		///     it (playing, or a step while paused). <paramref name="gameView" />, <paramref name="sceneView" />: the views'
		///     renderers (null: none).
		/// </summary>
		public void EndFrame(GameViewStats stats, Renderer gameView, Renderer sceneView)
		{
			PlaySessionWriter writer = current;
			if (writer == null)
			{
				FinishedWriters();
				return;
			}

			if (!editor.IsPlaying)
			{
				End(EndReason ?? "play mode ended");
				return;
			}

			if (writer.Failure != null)
			{
				End("writing failed");
				Debug.LogWarning("[Editor] The play session stopped recording: " + writer.Failure + " (" + writer.Folder + ")");
				return;
			}

			long now = Stopwatch.GetTimestamp();
			if (editor.HasUI)
			{
				bool front = EditorFocus.IsInFront(editor.Window);
				if (front != inFront)
				{
					inFront = front;
					Note(front ? "editor in front" : "editor in the background");
				}
			}

			bool step = stepped;
			stepped = false;
			if ((editor.PlayState != PlayState.Playing && !step) || stats.Taken == statsTaken || stats.Count == 0)
			{
				// Paused (no frame of the game), or no sample yet.
				statsTaken = stats.Taken;
				return;
			}

			statsTaken = stats.Taken;
			int slot = stats.Newest;
			long row = Interlocked.Read(ref rows);
			var frame = new SessionFrame
			{
				Row = row,
				Seconds = (now - started) / (double)Stopwatch.Frequency,
				GameFrame = Time.frameCount,
				Step = step,
				DeltaMs = (float)(Time.deltaTimeD * 1000.0),
				TimeScale = Time.timeScale,
				FrameMs = stats.FrameMs[slot],
				UpdateMs = stats.UpdateMs[slot],
				SwapMs = (float)Time.swapMs,
				AllocatedKB = stats.AllocatedKB[slot],
				Collected = stats.Collections[slot],
				RenderMs = float.NaN,
				GpuMs = float.NaN,
				SceneViewMs = float.NaN,
				DrawCalls = -1,
				IndirectDrawCalls = -1,
				Triangles = -1,
				Opaque = -1,
				Transparent = -1,
				Culled = -1,
				GpuInstances = -1,
				Lights = -1
			};

			// The runtime since the row before.
			TimeSpan pause = GC.GetTotalPauseDuration();
			frame.GcPauseMs = (float)(pause - gcPause).TotalMilliseconds;
			gcPause = pause;
			long methods = System.Runtime.JitInfo.GetCompiledMethodCount();
			TimeSpan compiling = System.Runtime.JitInfo.GetCompilationTime();
			frame.JitMethods = (int)(methods - jitMethods);
			frame.JitMs = (float)(compiling - jitTime).TotalMilliseconds;
			jitMethods = methods;
			jitTime = compiling;
			long allocated = GC.GetTotalAllocatedBytes();
			frame.ProcessAllocatedKB = (allocated - processAllocated) / 1024f;
			processAllocated = allocated;

			// What the Game view drew this frame (as the overlay shows it); nothing when it did not render.
			if (gameView != null && gameView.LastFrame.Frame != gameViewFrame)
			{
				RenderFrameStatistics drawn = gameView.LastFrame;
				gameViewFrame = drawn.Frame;
				frame.RenderMs = (float)drawn.CpuMilliseconds;
				frame.GpuMs = (float)gameView.Profiler.TotalMilliseconds;
				frame.DrawCalls = drawn.DrawCalls;
				frame.IndirectDrawCalls = drawn.IndirectDrawCalls;
				frame.Triangles = drawn.Triangles;
				frame.Opaque = drawn.OpaqueRenderers;
				frame.Transparent = drawn.TransparentRenderers;
				frame.Culled = drawn.CulledRenderers;
				frame.GpuInstances = drawn.GpuInstances;
				frame.Lights = drawn.Lights;
				if (drawn.Width != viewWidth || drawn.Height != viewHeight || drawn.RenderWidth != viewRenderWidth || drawn.RenderHeight != viewRenderHeight || drawn.Cameras != viewCameras)
				{
					viewWidth = drawn.Width;
					viewHeight = drawn.Height;
					viewRenderWidth = drawn.RenderWidth;
					viewRenderHeight = drawn.RenderHeight;
					viewCameras = drawn.Cameras;
					Add(new SessionViewEvent { Width = viewWidth, Height = viewHeight, RenderWidth = viewRenderWidth, RenderHeight = viewRenderHeight, Cameras = viewCameras });
				}

				if (now >= nextGpu)
				{
					nextGpu = now + (long)(GpuSeconds * Stopwatch.Frequency);
					var passes = new List<KeyValuePair<string, double>>(gameView.Profiler.Results);
					if (passes.Count > 0)
					{
						Add(new SessionGpuEvent { Passes = passes.ToArray() });
					}
				}
			}

			// The Scene view renders during play too when it is shown (its cost is in the frame).
			if (sceneView != null && sceneView.LastFrame.Frame != sceneViewFrame)
			{
				sceneViewFrame = sceneView.LastFrame.Frame;
				frame.SceneViewMs = (float)sceneView.LastFrame.CpuMilliseconds;
			}

			RefreshSlowNumbers(now);
			frame.Objects = objects;
			frame.Behaviours = behaviours;
			frame.Bodies = bodies;
			frame.Particles = particles;
			frame.HeapMB = heapMB;
			frame.ProcessMB = processMB;
			frame.PoolThreads = poolThreads;
			frame.PoolQueue = poolQueue;
			Camera camera = SceneManager.MainCamera;
			if (camera != null && camera.gameObject != null)
			{
				LELEngine.Mathematics.Vector3 position = camera.transform.position;
				frame.CameraX = position.X;
				frame.CameraY = position.Y;
				frame.CameraZ = position.Z;
			}
			else
			{
				frame.CameraX = frame.CameraY = frame.CameraZ = float.NaN;
			}

			frame.MouseX = Input.mousePosition.X;
			frame.MouseY = Input.mousePosition.Y;

			writer.Add(frame);
			WriteInput();
			DetectHitch(frame);
			Interlocked.Increment(ref rows);
		}

		/// <summary>The session ends: the writer finishes it on its thread (the summary, the files closed).</summary>
		public void End(string reason)
		{
			PlaySessionWriter writer = current;
			if (writer == null)
			{
				return;
			}

			if (pausedAt != 0)
			{
				pausedSeconds += (Stopwatch.GetTimestamp() - pausedAt) / (double)Stopwatch.Frequency;
				pausedAt = 0;
			}

			writer.Add(new SessionNoteEvent { Seconds = Seconds(), Row = Interlocked.Read(ref rows), Frame = Time.frameCount, What = "ended", Detail = reason });
			current = null;
			EndReason = null;
			var totals = new SessionTotals
			{
				Gen0 = GC.CollectionCount(0) - collectionsStart[0],
				Gen1 = GC.CollectionCount(1) - collectionsStart[1],
				Gen2 = GC.CollectionCount(2) - collectionsStart[2],
				AllocatedMB = (GC.GetTotalAllocatedBytes() - processAllocatedStart) / (1024.0 * 1024.0),
				PausedSeconds = pausedSeconds,
				GcPauseMs = (GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds,
				JitMethods = (int)(System.Runtime.JitInfo.GetCompiledMethodCount() - jitMethodsStart)
			};
			writer.End(reason, totals);
			if (profilerStarted)
			{
				profilerStarted = false;
				FrameProfiler.Configure("0", null, null);
				FrameProfiler.WritesReports = true;
			}

			version++;
			Debug.Log("[Editor] Play session recorded: " + writer.Folder);
			MaintainLater();
		}

		/// <summary>The editor closes: the session ends and every writer gets up to <paramref name="milliseconds" /> to finish.</summary>
		public void Shutdown(int milliseconds)
		{
			End(EndReason ?? "the editor closed");
			JoinWriters(milliseconds);
		}

		/// <summary>A folder is being written now (never deleted).</summary>
		public bool InUse(string folder)
		{
			lock (writersSync)
			{
				foreach (PlaySessionWriter writer in writers)
				{
					if (!writer.Finished && string.Equals(writer.Folder, folder, StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>Reads the project's sessions again on a worker thread (<see cref="Listing" /> when done).</summary>
		public void RequestListing()
		{
			string root = Root;
			if (root == null || Interlocked.Exchange(ref listingRunning, 1) == 1)
			{
				return;
			}

			int at = version;
			Task.Run(() =>
			{
				try
				{
					listing = PlaySessionStore.List(root, InUse);
					listingVersion = at;
				}
				finally
				{
					Interlocked.Exchange(ref listingRunning, 0);
				}
			});
		}

		/// <summary>Deletes every session of the project but the one recording now (worker thread).</summary>
		public void DeleteAll()
		{
			string root = Root;
			if (root == null)
			{
				return;
			}

			Task.Run(() =>
			{
				int deleted = PlaySessionStore.DeleteAll(root, InUse);
				Interlocked.Increment(ref version);
				Console.WriteLine($"[Editor] Deleted {deleted} recorded play session{(deleted == 1 ? "" : "s")} in {root}");
			});
		}

		#endregion

		#region PrivateMethods

		private double Seconds()
		{
			return (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
		}

		/// <summary>Stamped with the session's time, the row of the frame being built and the game's frame; any thread.</summary>
		private void Add(SessionEvent record)
		{
			PlaySessionWriter writer = current;
			if (writer == null)
			{
				return;
			}

			record.Seconds = Seconds();
			record.Row = Interlocked.Read(ref rows);
			record.Frame = Time.frameCount;
			writer.Add(record);
		}

		/// <summary>
		///     The old sessions go (older than the preferences keep them, over their size) and interrupted ones are marked, on
		///     a worker thread.
		/// </summary>
		private void MaintainLater()
		{
			string root = Root;
			if (root == null || Interlocked.Exchange(ref maintaining, 1) == 1)
			{
				return;
			}

			PlaySessionPreferences source = EditorPreferences.Current.PlaySessions;
			var preferences = new PlaySessionPreferences { KeepDays = source.KeepDays, MaxTotalMB = source.MaxTotalMB };
			Task.Run(() =>
			{
				try
				{
					int deleted = PlaySessionStore.Maintain(root, preferences, InUse);
					if (deleted > 0)
					{
						Interlocked.Increment(ref version);
						Console.WriteLine($"[Editor] Deleted {deleted} play session{(deleted == 1 ? "" : "s")} older than {preferences.KeepDays} days"
							+ (preferences.MaxTotalMB > 0 ? $" or over {preferences.MaxTotalMB} MB in all" : "") + $" ({root})");
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("[Editor] Cannot clean up the play sessions in " + root + ": " + e.Message);
				}
				finally
				{
					Interlocked.Exchange(ref maintaining, 0);
				}
			});
		}

		/// <summary>Writers that finished since are dropped; the sessions changed (a listing reads them again).</summary>
		private void FinishedWriters()
		{
			lock (writersSync)
			{
				if (writers.Count > 0 && writers.RemoveAll(w => w.Finished) > 0)
				{
					version++;
				}
			}
		}

		private void JoinWriters(int milliseconds)
		{
			PlaySessionWriter[] all;
			lock (writersSync)
			{
				all = writers.ToArray();
			}

			var clock = Stopwatch.StartNew();
			foreach (PlaySessionWriter writer in all)
			{
				writer.Join(Math.Max(0, milliseconds - (int)clock.ElapsedMilliseconds));
			}
		}

		private SessionHeader Header(DateTimeOffset now, string scenePath)
		{
			Assembly assembly = typeof(EditorApplication).Assembly;
			string built = "";
			try
			{
				built = System.IO.File.GetLastWriteTime(assembly.Location).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
			}
			catch (Exception e) when (e is System.IO.IOException || e is ArgumentException || e is NotSupportedException)
			{
			}

			DateTimeOffset processStarted;
			using (Process process = Process.GetCurrentProcess())
			{
				processStarted = new DateTimeOffset(process.StartTime);
			}

			LELEngine.Graphics.DeviceCapabilities caps = LELEngine.Graphics.GraphicsDevice.Current?.Capabilities;
			Assembly game = editor.Scripts?.GameAssembly;
			return new SessionHeader
			{
				Started = now,
				ProjectName = editor.Project?.Name,
				ProjectPath = editor.Project?.RootPath,
				Scene = scenePath,
				EditorVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(),
				EditorBuilt = built,
				EditorFolder = AppContext.BaseDirectory,
				ProcessId = Environment.ProcessId,
				ProcessStarted = processStarted,
				CommandLine = Environment.CommandLine,
				Automated = editor.Automated,
				BatchMode = !editor.HasUI,
				ReloadScriptsOnPlay = editor.ReloadScriptsOnPlay,
				MaximizeOnPlay = GameViewPanel.MaximizeOnPlay,
				DebugOverlay = GameViewPanel.ShowOverlay,
				AlwaysRefresh = editor.SceneView.AlwaysRefresh,
				HitchReports = FrameProfiler.Enabled,
				HitchThresholdMs = FrameProfiler.ThresholdMs,
				GameScripts = game != null ? game.GetName().Name + " " + game.ManifestModule.ModuleVersionId : null,
				GraphicsApi = LELEngine.Graphics.GraphicsDevice.CurrentApi.ToString(),
				GpuVendor = caps?.Vendor,
				GpuRenderer = caps?.Renderer,
				GpuDriver = caps?.DriverVersion,
				DiscreteGpu = caps?.DiscreteGpu ?? false,
				SuperSampling = QualitySettings.superSampling,
				FixedDeltaTime = Time.fixedDeltaTimeD,
				MaximumDeltaTime = Time.maximumDeltaTimeD
			};
		}

		private void RefreshSlowNumbers(long now)
		{
			if (now < nextSlow)
			{
				return;
			}

			nextSlow = now + (long)(SlowSeconds * Stopwatch.Frequency);
			objects = 0;
			behaviours = 0;
			bodies = 0;
			foreach (Scene scene in SceneManager.LoadedScenes)
			{
				objects += scene.GameObjects.Count;
				behaviours += scene.ActiveBehaviours.Count;
				bodies += scene.Physics2DIfCreated?.bodyCount ?? 0;
			}

			particles = 0;
			foreach (LELEngine.VFX.VfxInstance effect in LELEngine.VFX.VfxRuntime.Instances)
			{
				particles += effect.AliveCount;
			}

			heapMB = GC.GetTotalMemory(false) / (1024f * 1024f);
			processMB = Environment.WorkingSet / (1024f * 1024f);
			poolThreads = ThreadPool.ThreadCount;
			poolQueue = ThreadPool.PendingWorkItemCount;
		}

		/// <summary>The input read in the frame's update, as an event when anything happened or an action changed.</summary>
		private void WriteInput()
		{
			KeyValuePair<string, float>[] changed = null;
			if (!actionsSampled)
			{
				MapActions();
			}
			else
			{
				int count = 0;
				for (int i = 0; i < actionValues.Length; i++)
				{
					if (Changed(actionValues[i], actionWritten[i]))
					{
						count++;
					}
				}

				if (count > 0)
				{
					changed = new KeyValuePair<string, float>[count];
					count = 0;
					for (int i = 0; i < actionValues.Length; i++)
					{
						if (Changed(actionValues[i], actionWritten[i]))
						{
							changed[count++] = new KeyValuePair<string, float>(actionNames[i], actionValues[i]);
							actionWritten[i] = actionValues[i];
						}
					}
				}
			}

			actionsSampled = false;
			if (keysDown.Count > 0 || keysUp.Count > 0 || mouseDown != 0 || mouseUp != 0 || wheelX != 0f || wheelY != 0f || changed != null)
			{
				Add(new SessionInputEvent
				{
					KeysDown = keysDown.ToArray(),
					KeysUp = keysUp.ToArray(),
					MouseDown = mouseDown,
					MouseUp = mouseUp,
					WheelX = wheelX,
					WheelY = wheelY,
					Actions = changed
				});
			}

			ClearInput();
		}

		// An action's value is written when it moved by a hundredth, or reached (left) rest or a full press.
		private static bool Changed(float value, float written)
		{
			return MathF.Abs(value - written) >= 0.01f || (value != written && (value == 0f || MathF.Abs(value) == 1f));
		}

		/// <summary>The input system's actions per player, named "Player/Action" ("Action" with one player).</summary>
		private void MapActions()
		{
			InputConfiguration configuration = InputManager.Configuration;
			IReadOnlyList<InputPlayer> players = InputManager.Players;
			actionsConfiguration = configuration;
			actionsPlayers = players.Count;
			var names = new List<string>();
			var ids = new List<int>();
			var owners = new List<InputPlayer>();
			if (configuration != null)
			{
				foreach (InputPlayer player in players)
				{
					foreach (InputAction action in configuration.Actions)
					{
						names.Add(players.Count > 1 ? player.Name + "/" + action.Name : action.Name);
						ids.Add(action.Id);
						owners.Add(player);
					}
				}
			}

			actionNames = names.ToArray();
			actionIds = ids.ToArray();
			actionPlayers = owners.ToArray();
			actionValues = new float[actionIds.Length];
			actionWritten = new float[actionIds.Length];
		}

		private void ClearInput()
		{
			keysDown.Clear();
			keysUp.Clear();
			mouseDown = 0;
			mouseUp = 0;
			wheelX = 0f;
			wheelY = 0f;
		}

		/// <summary>
		///     A frame over the hitch profiler's threshold and its factor over the median of the frames before it is a hitch
		///     (the first frames, the scene's start, are always: there is nothing before them).
		/// </summary>
		private void DetectHitch(in SessionFrame frame)
		{
			float median = 0f;
			if (recentCount >= 8)
			{
				int count = Math.Min(recentCount, recent.Length);
				Array.Copy(recent, sorted, count);
				Array.Sort(sorted, 0, count);
				median = sorted[count / 2];
			}

			if (frame.FrameMs > FrameProfiler.ThresholdMs && frame.FrameMs > FrameProfiler.SpikeFactor * median)
			{
				Add(new SessionHitchEvent
				{
					FrameMs = frame.FrameMs,
					MedianMs = median,
					UpdateMs = frame.UpdateMs,
					RenderMs = float.IsNaN(frame.RenderMs) ? 0f : frame.RenderMs,
					GpuMs = float.IsNaN(frame.GpuMs) ? 0f : frame.GpuMs,
					SwapMs = frame.SwapMs,
					GcPauseMs = frame.GcPauseMs,
					Collected = frame.Collected,
					JitMs = frame.JitMs,
					JitMethods = frame.JitMethods
				});
			}

			recent[recentCount % recent.Length] = frame.FrameMs;
			recentCount++;
		}

		// ---------------------------------------------------------------- events of the engine and the runtime

		private void OnEntry(ConsoleEntry entry)
		{
			if (current == null)
			{
				return;
			}

			// Warnings, errors and exceptions with their whole call stack, plain messages with where they were logged.
			bool whole = entry.Type != LogType.Log;
			string stack = entry.StackTrace ?? (entry.File != null ? "   at " + entry.File + ":line " + entry.Line : null);
			if (!whole && stack != null)
			{
				int newline = stack.IndexOf('\n');
				stack = (newline >= 0 ? stack.Substring(0, newline) : stack).Trim();
			}

			Add(new SessionLogEvent { Id = entry.Id, Type = entry.Type, Source = entry.Source, Text = entry.Text, Stack = stack, FirstLineOnly = !whole });
		}

		private void OnHitchReport(string report)
		{
			PlaySessionWriter writer = current;
			if (writer == null)
			{
				return;
			}

			// The profiler reports a frame when it ends, after the frame's row was queued: that row.
			writer.Add(new SessionTextEvent { Seconds = Seconds(), Row = Math.Max(0, Interlocked.Read(ref rows) - 1), Frame = Time.frameCount, What = "hitchReport", Text = report });
		}

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
		{
			Add(new SessionSceneEvent { Action = "loaded", Name = scene?.Name, Path = scene?.Path, Mode = mode.ToString() });
		}

		private void OnSceneUnloaded(Scene scene)
		{
			Add(new SessionSceneEvent { Action = "unloaded", Name = scene?.Name, Path = scene?.Path });
		}

		private void OnActiveSceneChanged(Scene before, Scene now)
		{
			Add(new SessionSceneEvent { Action = "active", Name = now?.Name, Path = now?.Path });
		}

		private void OnControllerConnected(Controller controller)
		{
			Add(new SessionControllerEvent { Connected = true, Name = controller.Name, Type = controller.Type.ToString() });
		}

		private void OnControllerDisconnected(Controller controller)
		{
			Add(new SessionControllerEvent { Connected = false, Name = controller.Name, Type = controller.Type.ToString() });
		}

		/// <summary>An exception nobody caught ends the editor: it goes into the session, which is ended and written now.</summary>
		private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			PlaySessionWriter writer = current;
			if (writer == null)
			{
				return;
			}

			try
			{
				Add(new SessionTextEvent { What = "crash", Text = e.ExceptionObject?.ToString() ?? "(no exception object)" });
				writer.FlushNow();
				End("crashed: " + (e.ExceptionObject?.GetType().Name ?? "an exception nobody caught"));
				writer.Join(3000);
			}
			catch (Exception)
			{
				// The process is ending: nothing more to do.
			}
		}

		private void OnProcessExit(object sender, EventArgs e)
		{
			try
			{
				Shutdown(2000);
			}
			catch (Exception)
			{
			}
		}

		#endregion
	}
}
