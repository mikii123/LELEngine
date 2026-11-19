using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LELEngine.Rendering;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Compiling a game's shader variants before they are first drawn, when the game says so. A player compiles only
	///     what its start scene uses while the splash screen shows, so the splash stays short; the game's logic asks for the
	///     rest when it suits it: a menu as it opens, a loading screen before a level.
	///     <code>
	///     ShaderCompileOperation shaders = ShaderCompilation.CompileAll();
	///     // a progress bar: shaders.Progress
	///     await shaders;
	///     SceneManager.LoadScene("Scenes/Level.scene");
	///     </code>
	///     The engine does the work while the game keeps running: on the shader compile thread, a GL context of its own
	///     shared with the main one (<see cref="ShaderCompileThread" />, <see cref="UseCompileThread" />), and in a player
	///     first in a helper process (<see cref="ShaderCompileHelper" />), whose binaries the compile thread loads: some
	///     drivers stop every context of a process while one compiles. Without a shared context the main thread compiles a
	///     few milliseconds per frame (<see cref="SceneManager.BackgroundLoadingBudget" />, shared with background loads; at
	///     least one program a frame). A program the binary cache has (<see cref="ProgramBinaryCache" />, every start after
	///     the first) loads from disk. <see cref="ShaderCompileOperation.Complete" /> waits for the rest.
	///     <para>
	///         The variants are the build's (<see cref="ShaderVariantList" />, Data/ShaderVariants.txt): every material it
	///         ships, and per build scene what that scene uses. In the editor's play mode the editor makes the same list
	///         from the project on a worker thread at the first request (the operations wait for it,
	///         <see cref="ShaderCompileOperation.IsListing" />). Variants a script makes at runtime (materials created or
	///         switched in code) are in no list and compile on first use.
	///     </para>
	/// </summary>
	public static class ShaderCompilation
	{
		#region PublicFields

		/// <summary>The build's list file (the player sets it; null: none).</summary>
		public static string ListFile
		{
			get => listFile;
			set
			{
				listFile = value;
				set = null;
			}
		}

		/// <summary>Every variant the build lists (in the editor this waits for the list to be made).</summary>
		public static IReadOnlyList<ShaderVariant> AllVariants => WaitForSet().Variants;

		/// <summary>Programs compiled for an operation, including instanced variants, since the start.</summary>
		public static int ProgramsCompiled => programsCompiled;

		internal static int programsCompiled;

		/// <summary>
		///     Compile on a thread of its own, with a GL context shared with the main one (default): the main thread only
		///     checks the progress, its frames go on while the driver compiles. Off (or no shared context from the driver):
		///     the main thread compiles within the frame budget, and drivers that compile on their own threads may still
		///     hold its frames up (AMD's does). Read when an operation starts; LEL_GL_DISABLE=compilethread turns it off.
		/// </summary>
		public static bool UseCompileThread { get; set; } = true;

		#endregion

		#region PrivateFields

		private static readonly List<ShaderCompileOperation> running = new List<ShaderCompileOperation>();
		private static ShaderCompileThread compileThread;
		private static bool compileThreadFailed;

		[ThreadStatic]
		private static bool onCompileThread;

		private static string listFile;
		private static ShaderVariantSet set;
		private static Task<ShaderVariantSet> listing;
		private static ShaderCompileOperation all;

		#endregion

		#region InternalFields

		/// <summary>
		///     The editor: the build's list made from the open project, on a worker thread (play mode, on the first request;
		///     operations wait for it).
		/// </summary>
		internal static Func<ShaderVariantSet> EditorVariants;

		#endregion

		#region PublicMethods

		/// <summary>
		///     The variants a build scene uses (with what it references, and the always included shaders); empty when the
		///     build has no list for it. In the editor this waits for the list to be made.
		/// </summary>
		public static IReadOnlyList<ShaderVariant> VariantsOf(string scenePath)
		{
			return (IReadOnlyList<ShaderVariant>)WaitForSet().Of(scenePath) ?? Array.Empty<ShaderVariant>();
		}

		/// <summary>
		///     Compiles every variant of the build over the following frames. One operation: while it runs, and after it
		///     finished, the same operation is returned (until the scenes are unloaded: the editor leaving play mode).
		/// </summary>
		public static ShaderCompileOperation CompileAll()
		{
			return all ??= Start(listed => listed.Variants, "every shader variant");
		}

		/// <summary>Compiles the variants a build scene uses over the following frames (before loading it, for example).</summary>
		public static ShaderCompileOperation CompileScene(string scenePath)
		{
			return Start(listed => listed.Of(scenePath) ?? (IEnumerable<ShaderVariant>)Array.Empty<ShaderVariant>(), "the shader variants of " + scenePath);
		}

		/// <summary>Compiles the given variants over the following frames (those built already count as done at once).</summary>
		public static ShaderCompileOperation Compile(IEnumerable<ShaderVariant> variants, string name = null)
		{
			IEnumerable<ShaderVariant> given = variants ?? Array.Empty<ShaderVariant>();
			return Start(_ => given, name ?? "shader variants");
		}

		#endregion

		#region InternalMethods

		/// <summary>A frame of work (main thread, <see cref="SceneManager.Update" />): every running operation, in request order, until the deadline.</summary>
		internal static void Update(long deadline)
		{
			if (running.Count == 0 || !GraphicsThread.HasContext)
			{
				return;
			}

			ShaderVariantSet listed = SetIfReady();
			bool gpuDriven = InstancedVariantsUsed;
			foreach (ShaderCompileOperation operation in running.ToArray())
			{
				if (operation.IsListing)
				{
					if (listed == null)
					{
						continue;
					}

					operation.SetVariants(listed);
				}

				if (!operation.Assigned && operation.VariantCount > 0 && CompileThread() is ShaderCompileThread thread)
				{
					operation.AssignToCompileThread();
					thread.Enqueue(operation, gpuDriven);
				}

				operation.Step(deadline, gpuDriven);
				if (operation.IsDone)
				{
					running.Remove(operation);
				}
			}
		}

		/// <summary>
		///     The scenes were unloaded (the editor leaving play mode, the game quitting): running operations stop and are
		///     never done (the compile thread ends the program it is building first); the editor's list is made again next
		///     time.
		/// </summary>
		internal static void DropAll()
		{
			foreach (ShaderCompileOperation operation in running)
			{
				operation.Drop();
			}

			running.Clear();
			all = null;
			if (listFile == null)
			{
				// A listing still running ends unseen.
				set = null;
				listing = null;
			}

			if (compileThread != null && !compileThread.WaitIdle(10000))
			{
				Debug.LogWarning("[Shader] The shader compile thread is still busy after 10 s");
			}
		}

		/// <summary>This thread is the shader compile thread (<see cref="ShaderCompileThread" />).</summary>
		internal static bool OnCompileThread => onCompileThread;

		internal static void MarkCompileThread()
		{
			onCompileThread = true;
		}

		/// <summary>The renderer draws GPU-driven: the variants' instanced programs are compiled too.</summary>
		internal static bool InstancedVariantsUsed => Game.Renderer != null && Game.Renderer.Settings.GpuDriven && GpuScene.IsSupported;

		#endregion

		#region PrivateMethods

		// The compile thread, made on the first operation that has work (main thread, its context current); null when it is
		// off or the driver gave no shared context (then the main thread compiles within the frame budget).
		private static ShaderCompileThread CompileThread()
		{
			if (compileThread != null || compileThreadFailed || !UseCompileThread)
			{
				return compileThread;
			}

			compileThread = ShaderCompileThread.TryCreate(out string why);
			if (compileThread == null)
			{
				compileThreadFailed = true;
				Debug.LogWarning("[Shader] No shader compile thread (" + why + "): shader variants compile on the main thread, a few milliseconds per frame");
			}

			return compileThread;
		}

		private static ShaderCompileOperation Start(Func<ShaderVariantSet, IEnumerable<ShaderVariant>> select, string name)
		{
			var operation = new ShaderCompileOperation(select, name);
			ShaderVariantSet listed = SetIfReady();
			if (listed != null)
			{
				operation.SetVariants(listed);
				if (operation.VariantCount == 0)
				{
					operation.Finish();
					return operation;
				}
			}

			running.Add(operation);
			return operation;
		}

		// The list when it is there; null while the editor makes it (started on the first call).
		private static ShaderVariantSet SetIfReady()
		{
			if (set != null)
			{
				return set;
			}

			if (listFile != null || EditorVariants == null)
			{
				set = listFile != null ? ShaderVariantList.Read(listFile) : new ShaderVariantSet();
				return set;
			}

			if (listing == null)
			{
				Func<ShaderVariantSet> make = EditorVariants;
				listing = Task.Run(() =>
				{
					try
					{
						return make() ?? new ShaderVariantSet();
					}
					catch (Exception e)
					{
						Debug.LogWarning("[Shader] The project's shader variants could not be listed: " + e.Message);
						return new ShaderVariantSet();
					}
				});
			}

			if (!listing.IsCompleted)
			{
				return null;
			}

			set = listing.Result;
			listing = null;
			return set;
		}

		internal static ShaderVariantSet WaitForSet()
		{
			ShaderVariantSet listed = SetIfReady();
			if (listed != null)
			{
				return listed;
			}

			Task<ShaderVariantSet> pending = listing;
			pending?.Wait();
			return SetIfReady() ?? new ShaderVariantSet();
		}

		#endregion
	}

	/// <summary>
	///     Shader variants compiling over the following frames (<see cref="ShaderCompilation" />): progress, the end,
	///     awaitable (<c>await operation;</c> continues on the main thread once every variant is compiled).
	/// </summary>
	public sealed class ShaderCompileOperation
	{
		#region PublicFields

		/// <summary>What is compiled ("every shader variant", "the shader variants of Scenes/Level.scene").</summary>
		public string Name { get; }

		/// <summary>The variants asked for (each once).</summary>
		public int VariantCount => variants.Count;

		/// <summary>The variants done so far: compiled, found compiled already, or failed.</summary>
		public int DoneCount => Volatile.Read(ref doneCount);

		/// <summary>The variants that were compiled already when the operation reached them.</summary>
		public int AlreadyCompiledCount => Volatile.Read(ref alreadyCompiledCount);

		/// <summary>Programs compiled (or loaded from the binary cache) by this operation, instanced variants included.</summary>
		public int ProgramCount => Volatile.Read(ref programCount);

		/// <summary>It runs on the shader compile thread (<see cref="ShaderCompilation.UseCompileThread" />), not in the main thread's frames.</summary>
		public bool OnCompileThread => assigned;

		/// <summary>The compile thread's time in the operation (milliseconds).</summary>
		public double CompileThreadMilliseconds { get; private set; }

		/// <summary>The variants the binary cache lacked, for the helper process to compile (<see cref="ShaderCompileHelper" />).</summary>
		public int HelperVariants { get; private set; }

		/// <summary>0..1, by variants (0 while the list is made: <see cref="IsListing" />).</summary>
		public float Progress => IsListing ? 0f : VariantCount == 0 ? 1f : (float)DoneCount / VariantCount;

		/// <summary>The editor is still making the build's list: <see cref="VariantCount" /> is not known yet.</summary>
		public bool IsListing => select != null;

		/// <summary>Every variant is done (also when some failed: <see cref="Failures" />).</summary>
		public bool IsDone { get; private set; }

		/// <summary>One message per variant that did not compile, with the compiler's output.</summary>
		public IReadOnlyList<string> Failures => failures;

		/// <summary>From the request to the end (milliseconds), and the frames it took.</summary>
		public long Milliseconds { get; private set; }

		public int Frames { get; private set; }

		/// <summary>The main thread's time in the operation (milliseconds): in all, and in its longest frame.</summary>
		public double MainThreadMilliseconds { get; private set; }

		public double LongestFrameMilliseconds { get; private set; }

		/// <summary>Raised on the main thread when it is done; a handler added after that runs at once.</summary>
		public event Action<ShaderCompileOperation> Completed
		{
			add
			{
				if (value == null)
				{
					return;
				}

				if (IsDone)
				{
					Invoke(value);
				}
				else
				{
					completed += value;
				}
			}
			remove => completed -= value;
		}

		#endregion

		#region PrivateFields

		// Programs started and not finished at a time: with parallel shader compile the driver works on these together;
		// without it a program compiles when it is finished, so one at a time.
		private const int ParallelPrograms = 16;

		private readonly List<ShaderVariant> variants = new List<ShaderVariant>();
		private readonly List<string> failures = new List<string>();
		private readonly List<Entry> inFlight = new List<Entry>();
		private readonly List<Action> continuations = new List<Action>();
		private readonly Stopwatch watch = Stopwatch.StartNew();
		private Action<ShaderCompileOperation> completed;
		private Func<ShaderVariantSet, IEnumerable<ShaderVariant>> select;
		private int next;
		private int doneCount;
		private int alreadyCompiledCount;
		private int programCount;
		private volatile bool dropped;

		// Given to the compile thread (set on the main thread before it is queued), and that thread's end.
		private volatile bool assigned;
		private volatile bool compileThreadDone;

		// The helper process compiling for the compile thread (null: none), and the variants it did not report.
		private ShaderCompileHelper.Run helper;
		private int helperMissed;

		#endregion

		#region Constructors

		/// <param name="select">Picks the variants from the build's list once it is there (<see cref="SetVariants" />).</param>
		internal ShaderCompileOperation(Func<ShaderVariantSet, IEnumerable<ShaderVariant>> select, string name)
		{
			Name = name;
			this.select = select;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Waits until every variant is compiled (the main thread: it waits for the compile thread, or compiles what is left
		///     itself when there is none), then it is done: for a loading screen that must not show the next scene before.
		/// </summary>
		public void Complete()
		{
			if (IsDone || dropped)
			{
				return;
			}

			if (!GraphicsThread.HasContext)
			{
				throw new InvalidOperationException("Shader variants compile on the thread with the GL context (the main thread).");
			}

			if (IsListing)
			{
				SetVariants(ShaderCompilation.WaitForSet());
			}

			if (assigned)
			{
				var wait = new SpinWait();
				while (!compileThreadDone)
				{
					wait.SpinOnce(1);
				}
			}

			Step(long.MaxValue, ShaderCompilation.InstancedVariantsUsed);
		}

		/// <summary>Lets <c>await</c> wait for the operation (continues on the main thread).</summary>
		public ShaderCompileAwaiter GetAwaiter()
		{
			return new ShaderCompileAwaiter(this);
		}

		public override string ToString()
		{
			return $"{Name} ({DoneCount} of {VariantCount}{(IsDone ? ", done" : "")})";
		}

		#endregion

		#region InternalMethods

		/// <summary>The build's list is there: the operation's variants (each once).</summary>
		internal void SetVariants(ShaderVariantSet listed)
		{
			if (select == null)
			{
				return;
			}

			IEnumerable<ShaderVariant> asked = select(listed) ?? Array.Empty<ShaderVariant>();
			select = null;
			var seen = new HashSet<ShaderVariant>();
			foreach (ShaderVariant variant in asked)
			{
				if (variant.Shader != null && seen.Add(variant))
				{
					variants.Add(variant);
				}
			}
		}

		/// <summary>Given to the compile thread (<see cref="AssignToCompileThread" />).</summary>
		internal bool Assigned => assigned;

		/// <summary>Main thread, before it is queued on the compile thread: from now on only that thread compiles it.</summary>
		internal void AssignToCompileThread()
		{
			assigned = true;
		}

		/// <summary>
		///     The compile thread (its context current): every variant built whole, one after another, until done or
		///     dropped. Those whose programs the binary cache lacks are compiled first by the helper process
		///     (<see cref="ShaderCompileHelper" />, when the host has one), the thread loading each binary once the helper
		///     reports it. The main thread sees the progress and finishes the operation (<see cref="Step" />).
		/// </summary>
		internal void RunOnCompileThread(bool gpuDriven)
		{
			long start = Stopwatch.GetTimestamp();
			try
			{
				// Which variants the helper compiles (their place in its list), when there is a helper.
				int[] helperIndex = null;
				if (ShaderCompileHelper.Executable != null && !dropped)
				{
					helperIndex = new int[variants.Count];
					var missing = new List<ShaderVariant>();
					for (int i = 0; i < variants.Count; i++)
					{
						helperIndex[i] = -1;
						if (!ShaderProgram.BinariesCached(variants[i], gpuDriven))
						{
							helperIndex[i] = missing.Count;
							missing.Add(variants[i]);
						}
					}

					HelperVariants = missing.Count;
					helper = missing.Count > 0 ? ShaderCompileHelper.TryStart(missing, gpuDriven) : null;
				}

				for (int i = 0; i < variants.Count; i++)
				{
					if (dropped)
					{
						break;
					}

					// The helper's binary first; a variant it did not report (it failed, or ended) compiles here.
					if (helper != null && helperIndex[i] >= 0 && !helper.WaitFor(helperIndex[i], () => dropped))
					{
						if (dropped)
						{
							break;
						}

						helperMissed++;
					}

					CompileHere(variants[i], gpuDriven);
					Interlocked.Increment(ref doneCount);
				}
			}
			finally
			{
				helper?.Stop();
				CompileThreadMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
				compileThreadDone = true;
			}
		}

		/// <summary>
		///     A frame (main thread): work until <paramref name="deadline" /> (at least one program started or finished); on
		///     the compile thread's operations only the check whether it is done.
		/// </summary>
		internal void Step(long deadline, bool gpuDriven)
		{
			if (IsDone || dropped)
			{
				return;
			}

			Frames++;
			if (assigned)
			{
				if (compileThreadDone)
				{
					Finish();
				}

				return;
			}

			long start = Stopwatch.GetTimestamp();
			try
			{
				Work(deadline, gpuDriven);
			}
			finally
			{
				double milliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
				MainThreadMilliseconds += milliseconds;
				LongestFrameMilliseconds = Math.Max(LongestFrameMilliseconds, milliseconds);
			}
		}

		private void Work(long deadline, bool gpuDriven)
		{
			int limit = GLCapabilities.ParallelShaderCompile ? ParallelPrograms : 1;
			bool worked = false;
			while (true)
			{
				bool progressed = false;

				// The programs the driver has finished: their results read (cheap), the instanced one started next.
				for (int i = 0; i < inFlight.Count; i++)
				{
					if ((worked || progressed) && Stopwatch.GetTimestamp() >= deadline)
					{
						break;
					}

					Entry entry = inFlight[i];
					ShaderProgram waiting = entry.Instanced ?? entry.Program;
					if (!waiting.IsBuildReady && deadline != long.MaxValue)
					{
						continue;
					}

					progressed = true;
					if (Advance(entry, gpuDriven))
					{
						inFlight.RemoveAt(i);
						i--;
						doneCount++;
					}
				}

				// New ones started while the driver has room.
				while (next < variants.Count && inFlight.Count < limit && (!(worked || progressed) || Stopwatch.GetTimestamp() < deadline))
				{
					ShaderVariant variant = variants[next++];
					progressed = true;
					Entry entry = Start(variant, gpuDriven);
					if (entry == null)
					{
						doneCount++;
					}
					else
					{
						inFlight.Add(entry);
					}
				}

				worked |= progressed;
				if (next >= variants.Count && inFlight.Count == 0)
				{
					Finish();
					return;
				}

				if (!progressed || Stopwatch.GetTimestamp() >= deadline)
				{
					return;
				}
			}
		}

		internal void Finish()
		{
			if (IsDone)
			{
				return;
			}

			IsDone = true;
			Milliseconds = watch.ElapsedMilliseconds;
			if (VariantCount > 0)
			{
				Console.WriteLine($"[Shader] Compiled {Name}: {ProgramCount} programs for {VariantCount - AlreadyCompiledCount} variants ({AlreadyCompiledCount} compiled already) in {Milliseconds} ms over {Frames} frames"
					+ (assigned ? $" (on the shader compile thread: {CompileThreadMilliseconds:0} ms"
							+ (helper != null ? $"; the compile helper process compiled {HelperVariants - helperMissed} of {HelperVariants} variants the binary cache lacked{(helperMissed > 0 ? $" (exit code {helper.ExitCode})" : "")}"
								: ShaderCompileHelper.Executable != null && HelperVariants == 0 ? "; all in the binary cache" : "")
							+ ")"
						: $" (main thread {MainThreadMilliseconds:0} ms, at most {LongestFrameMilliseconds:0.0} ms a frame)")
					+ (failures.Count > 0 ? $", {failures.Count} failed" : ""));
			}

			foreach (string failure in failures)
			{
				Debug.LogWarning("[Shader] Shader variant " + failure);
			}

			Action<ShaderCompileOperation> handlers = completed;
			completed = null;
			if (handlers != null)
			{
				foreach (Delegate handler in handlers.GetInvocationList())
				{
					Invoke((Action<ShaderCompileOperation>)handler);
				}
			}

			Action[] waiting = continuations.ToArray();
			continuations.Clear();
			foreach (Action continuation in waiting)
			{
				continuation();
			}
		}

		internal void Drop()
		{
			dropped = true;
			helper?.Stop();
			inFlight.Clear();
			continuations.Clear();
			completed = null;
		}

		internal void AddContinuation(Action continuation)
		{
			if (IsDone)
			{
				continuation();
			}
			else if (!dropped)
			{
				continuations.Add(continuation);
			}
		}

		#endregion

		#region PrivateMethods

		// The variant's program made, its build started; null when the variant is done already (compiled before, or failed).
		private Entry Start(ShaderVariant variant, bool gpuDriven)
		{
			ShaderProgram program;
			bool made;
			try
			{
				int started = ShaderProgram.StartedOnThisThread;
				program = variant.GetProgram();
				program.StartBuild();
				made = ShaderProgram.StartedOnThisThread != started;
			}
			catch (Exception e)
			{
				failures.Add(variant + ": " + e.Message);
				return null;
			}

			if (!program.IsBuilt)
			{
				return new Entry { Variant = variant, Program = program };
			}

			if (!program.IsLinked)
			{
				failures.Add(variant + " does not compile:\n" + program.BuildLog.TrimEnd());
				return null;
			}

			if (made)
			{
				// Started now and built at once: loaded from the binary cache.
				CountProgram();
			}

			// Built already: only its instanced program may be new.
			ShaderProgram instanced = gpuDriven ? program.StartInstancedVariant() : null;
			if (instanced == null)
			{
				if (!made)
				{
					alreadyCompiledCount++;
				}

				BuildPassVariants(program, gpuDriven);
				return null;
			}

			return new Entry { Variant = variant, Program = program, BaseDone = true, Instanced = instanced };
		}

		// The programs of the passes the variant's shader draws itself (its own shadow caster, geometry prepass), of the
		// variant's program and of its instanced one, built on this thread (few shaders have any).
		private void BuildPassVariants(ShaderProgram program, bool gpuDriven)
		{
			int builtNow = program.BuildPassVariants();
			if (gpuDriven && program.SupportsInstancing)
			{
				builtNow += program.GetInstancedVariant()?.BuildPassVariants() ?? 0;
			}

			for (int i = 0; i < builtNow; i++)
			{
				CountProgram();
			}
		}

		// The compile thread: the variant's program and its instanced one built whole here. A program whose build another
		// context started (the main thread's scene loads) is left to that context.
		private void CompileHere(ShaderVariant variant, bool gpuDriven)
		{
			int started = ShaderProgram.StartedOnThisThread;
			try
			{
				ShaderProgram program = variant.GetProgram();
				if (program.BuildStartedElsewhere)
				{
					return;
				}

				program.EnsureBuilt();
				if (!program.IsLinked)
				{
					lock (failures)
					{
						failures.Add(variant + " does not compile:\n" + program.BuildLog.TrimEnd());
					}

					return;
				}

				// With it the programs of the passes the shader draws itself (its own shadow caster, geometry prepass).
				program.BuildPassVariants();
				if (gpuDriven && program.SupportsInstancing)
				{
					program.GetInstancedVariant()?.BuildPassVariants();
				}
			}
			catch (Exception e)
			{
				lock (failures)
				{
					failures.Add(variant + ": " + e.Message);
				}
			}
			finally
			{
				// Programs built here for it: the variant's (it may have brought its shader's own) and its instanced one.
				int built = ShaderProgram.StartedOnThisThread - started;
				if (built == 0)
				{
					Interlocked.Increment(ref alreadyCompiledCount);
				}

				for (int i = 0; i < built; i++)
				{
					CountProgram();
				}
			}
		}

		private void CountProgram()
		{
			Interlocked.Increment(ref programCount);
			Interlocked.Increment(ref ShaderCompilation.programsCompiled);
		}

		// The entry's program the driver finished: its results read; true when the variant is done.
		private bool Advance(Entry entry, bool gpuDriven)
		{
			try
			{
				if (!entry.BaseDone)
				{
					entry.BaseDone = true;
					entry.Program.EnsureBuilt();
					CountProgram();
					if (!entry.Program.IsLinked)
					{
						failures.Add(entry.Variant + " does not compile:\n" + entry.Program.BuildLog.TrimEnd());
						return true;
					}

					entry.Instanced = gpuDriven ? entry.Program.StartInstancedVariant() : null;
					if (entry.Instanced == null)
					{
						BuildPassVariants(entry.Program, gpuDriven);
					}

					return entry.Instanced == null;
				}

				if (entry.Program.GetInstancedVariant() != null)
				{
					CountProgram();
				}

				BuildPassVariants(entry.Program, gpuDriven);
				return true;
			}
			catch (Exception e)
			{
				failures.Add(entry.Variant + ": " + e.Message);
				return true;
			}
		}

		private void Invoke(Action<ShaderCompileOperation> handler)
		{
			try
			{
				handler(this);
			}
			catch (Exception e)
			{
				Debug.LogError("[Shader] A Completed handler of " + Name + " threw: " + e);
			}
		}

		#endregion

		#region Nested Types

		private sealed class Entry
		{
			public ShaderVariant Variant;
			public ShaderProgram Program;
			public ShaderProgram Instanced;
			public bool BaseDone;
		}

		#endregion
	}

	/// <summary>Awaits a <see cref="ShaderCompileOperation" />: the code after the await runs on the main thread when it is done.</summary>
	public readonly struct ShaderCompileAwaiter : INotifyCompletion
	{
		private readonly ShaderCompileOperation operation;

		internal ShaderCompileAwaiter(ShaderCompileOperation operation)
		{
			this.operation = operation;
		}

		public bool IsCompleted => operation.IsDone;

		public ShaderCompileOperation GetResult()
		{
			return operation;
		}

		public void OnCompleted(Action continuation)
		{
			operation.AddContinuation(continuation);
		}
	}
}
