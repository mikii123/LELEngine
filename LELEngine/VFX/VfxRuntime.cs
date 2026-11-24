using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>The GL programs of one graph version, shared by every instance playing the graph.</summary>
	public sealed class VfxProgramSet : IDisposable
	{
		#region PublicFields

		public sealed class SystemPrograms
		{
			public ComputeShader Initialize;
			public ComputeShader Update;

			/// <summary>
			///     The kernels compiled with the ray query define (a system that reads the scene's distance field, on a device
			///     with ray queries): they trace the scene's triangles instead while the GI's rays do; null otherwise.
			/// </summary>
			public ComputeShader InitializeRays;

			public ComputeShader UpdateRays;
			public readonly List<OutputPrograms> Outputs = new List<OutputPrograms>();
		}

		public sealed class OutputPrograms
		{
			public ShaderProgram Draw;
			public ShaderProgram Shadow;
		}

		public int GraphVersion;
		public VfxCompiledGraph Compiled;
		public readonly List<SystemPrograms> Systems = new List<SystemPrograms>();
		public readonly List<string> ShaderErrors = new List<string>();
		public bool HasErrors => Compiled == null || Compiled.HasErrors || ShaderErrors.Count > 0;

		#endregion

		#region InternalFields

		/// <summary>
		///     The programs whose builds were started and not looked at yet (name, program, what takes a failed one out of
		///     the set); empty once <see cref="VfxRuntime" /> finished the set.
		/// </summary>
		internal readonly List<(string Name, ShaderProgram Program, Action Drop)> Started = new List<(string, ShaderProgram, Action)>();

		#endregion

		#region PublicMethods

		public void Dispose()
		{
			foreach (SystemPrograms system in Systems)
			{
				system.Initialize?.Delete();
				system.Update?.Delete();
				system.InitializeRays?.Delete();
				system.UpdateRays?.Delete();
				foreach (OutputPrograms output in system.Outputs)
				{
					output.Draw?.Delete();
					output.Shadow?.Delete();
				}
			}

			Systems.Clear();
			Started.Clear();
		}

		#endregion
	}

	/// <summary>A depth buffer copy of a view for soft particles and depth collisions, with the matrices of the frame it was taken in.</summary>
	public sealed class VfxDepthCapture
	{
		public RenderTexture Depth;
		public Matrix4 ViewProjection;
		public Matrix4 InverseViewProjection;
		public int Width;
		public int Height;
		public bool Valid;
	}

	/// <summary>Work and timings of the last frame (stats, profiling).</summary>
	public struct VfxFrameStats
	{
		public int Instances;
		public int SimulatedSystems;
		public int SkippedSystems;
		public int Dispatches;
		public int Barriers;
		public int Sorts;
		public int SortDispatches;
		public int Draws;

		/// <summary>CPU milliseconds of the simulation's stages: spawners and parameters, the parameter upload, the GPU commands, the readback.</summary>
		public double PrepareMs;

		public double PackMs;
		public double DispatchMs;
		public double ReadbackMs;

		/// <summary>Effects frozen because no view saw them, and bytes the simulation pass allocated (garbage).</summary>
		public int FrozenInstances;

		public long AllocatedBytes;
	}

	/// <summary>
	///     Engine-wide state of the particle system: the compiled programs per graph, the registry of playing
	///     instances and the shared GPU resources every system lives in. Each frame <see cref="RunSimulation" />
	///     (the renderer's first pass) runs the simulation of every effect at once: one frame kernel prepares every
	///     system's counters, then the initialize kernels of all systems run back to back, then their update kernels,
	///     each phase reading what the one before wrote. The draw hooks
	///     (<see cref="CollectTransparent" />, <see cref="DrawOpaque" />, <see cref="DrawShadowCasters" />) draw from the
	///     same buffers with indirect arguments the update kernels wrote.
	/// </summary>
	public static class VfxRuntime
	{
		#region PublicFields

		/// <summary>Playing instances (components that are enabled).</summary>
		public static IReadOnlyList<VfxInstance> Instances => instances;

		/// <summary>True while any enabled effect simulates.</summary>
		public static bool AnyActive
		{
			get
			{
				foreach (VfxInstance instance in instances)
				{
					if (instance.IsActive)
					{
						return true;
					}
				}

				return false;
			}
		}

		/// <summary>The GL context is up: effects can allocate buffers and run.</summary>
		public static bool Available => GraphicsCapabilities.Initialized;

		/// <summary>The depth of the view rendered last (collisions in the next simulation use it).</summary>
		public static VfxDepthCapture LastDepth { get; private set; }

		/// <summary>Camera the simulation orients and sorts for when the scene has none (the editor's scene camera).</summary>
		public static Camera FallbackCamera;

		/// <summary>Alive particles of every instance after the last readback (stats).</summary>
		public static int TotalAlive
		{
			get
			{
				int total = 0;
				foreach (VfxInstance instance in instances)
				{
					total += instance.AliveCount;
				}

				return total;
			}
		}

		/// <summary>Simulation frames run so far (readbacks and visibility are stamped with it).</summary>
		public static long Frame { get; private set; }

		/// <summary>CPU milliseconds of the last <see cref="RunSimulation" />.</summary>
		public static double LastSimulationCpuMs { get; private set; }

		/// <summary>Work of the last frame.</summary>
		public static VfxFrameStats Stats => stats;

		/// <summary>Write every generated GLSL source to %TEMP%/LELEngine/VFX (environment LEL_VFX_DUMP=1; debugging).</summary>
		public static bool DumpSources = Environment.GetEnvironmentVariable("LEL_VFX_DUMP") == "1";

		/// <summary>Log every system's counters from the readback once per 60 frames (environment LEL_VFX_DEBUG=1; debugging).</summary>
		public static bool DebugCounters = Environment.GetEnvironmentVariable("LEL_VFX_DEBUG") == "1";

		private static long lastDebugPrint = -1;

		/// <summary>The shared counter pool (a block of uints per system; layout in VfxCodegen.Counter*).</summary>
		internal static GpuBuffer CounterBuffer => counterBuffer;

		public static ComputeShader Sort => sort ??= new ComputeShader("Engine/VFX/VfxSort.shader");
		public static ComputeShader FrameKernel => frameKernel ??= new ComputeShader("Engine/VFX/VfxFrame.shader");

		/// <summary>Any instance's system samples the depth buffer (soft particles, depth collisions).</summary>
		public static bool NeedsDepth
		{
			get
			{
				foreach (VfxInstance instance in instances)
				{
					if (instance.NeedsDepth)
					{
						return true;
					}
				}

				return false;
			}
		}

		#endregion

		#region PrivateFields

		private const int ReadbackSlots = 4;
		private const int SortChunk = 4096;
		private const int SortThreads = 512;

		// The parameter blocks start at multiples of this in their buffer: no device asks for a larger alignment of
		// uniform block ranges.
		private const int UniformAlignment = 256;

		private sealed class Readback
		{
			public GpuBuffer Buffer;
			public GpuFence Fence;
			public long Frame;
			public int Blocks;
		}

		private sealed class IndexBuffer
		{
			public GpuBuffer Buffer;
			public int Particles;
		}

		private static readonly List<VfxInstance> instances = new List<VfxInstance>();
		private static readonly List<VfxInstance> queued = new List<VfxInstance>();
		private static readonly Dictionary<VfxGraph, VfxProgramSet> programs = new Dictionary<VfxGraph, VfxProgramSet>();
		private static readonly Dictionary<Renderer, VfxDepthCapture> depthCaptures = new Dictionary<Renderer, VfxDepthCapture>();

		/// <summary>The depth of the other cameras of a renderer's stack: soft particles only (collisions use the lighting camera's).</summary>
		private static readonly Dictionary<Renderer, VfxDepthCapture> stackDepthCaptures = new Dictionary<Renderer, VfxDepthCapture>();
		private static ComputeShader sort;
		private static ComputeShader frameKernel;
		private static VfxFrameStats stats;

		// The counter pool: one block per system, a free list, grown by doubling.
		private static GpuBuffer counterBuffer;
		private static int counterBlocks;
		private static int counterHigh;
		private static readonly Stack<int> freeBlocks = new Stack<int>();
		private static VfxSystemState[] blockOwners = Array.Empty<VfxSystemState>();

		// The parameter blocks of every system, packed into one uniform buffer per frame.
		private static GpuBuffer paramsBuffer;
		private static float[] paramsStaging = new float[1024];
		private static bool paramsDirty = true;

		// Frame kernel entries.
		private static GpuBuffer entryBuffer;
		private static uint[] entryStaging = new uint[256];
		private static readonly List<VfxSystemState> simulated = new List<VfxSystemState>();
		private static readonly List<VfxSystemState> layerScratch = new List<VfxSystemState>();
		private static int[] layerStarts = new int[4];

		// The readback ring of the counter pool (alive counts and bounds a few frames late, never waited for).
		private static readonly Readback[] readbacks = new Readback[ReadbackSlots];
		private static uint[] readbackData = new uint[256];

		// The per-view block of the draws (camera basis).
		private static GpuBuffer viewBuffer;
		private static readonly float[] viewData = new float[16];

		// Index buffers of the indexed outputs, per shape.
		private static readonly Dictionary<long, IndexBuffer> indexBuffers = new Dictionary<long, IndexBuffer>();

		// Draw entries reused every frame.
		private static readonly List<VfxDrawEntry> entryPool = new List<VfxDrawEntry>();
		private static int entryPoolUsed;
		private static readonly List<VfxDrawEntry> opaqueScratch = new List<VfxDrawEntry>();

		// Sorting: the jobs of the view and the uniform slots of the sort kernel.
		private static readonly List<VfxDrawEntry> sortJobs = new List<VfxDrawEntry>();
		private static int sortMode = -2;
		private static int sortCount;
		private static int sortK;
		private static int sortJ;
		private static int sortAliveCounter;
		private static int sortPositionPlane;
		private static int sortCapacity;
		private static int sortWorldSpace;
		private static int sortLocalToWorld;
		private static int sortViewPosition;
		private static int sortViewForward;
		private static int sortIndexBits;
		private static int sortDepthRange;
		private static Vector3 viewPosition;
		private static Vector3 viewForward;

		#endregion

		#region PublicMethods

		internal static void Register(VfxInstance instance)
		{
			if (!instances.Contains(instance))
			{
				instances.Add(instance);
				paramsDirty = true;
			}
		}

		internal static void Unregister(VfxInstance instance)
		{
			instances.Remove(instance);
			queued.Remove(instance);
			paramsDirty = true;
		}

		/// <summary>The parameter blocks must be packed again (systems were built, released or changed size).</summary>
		internal static void MarkParamsDirty()
		{
			paramsDirty = true;
		}

		internal static void QueueSimulation(VfxInstance instance)
		{
			queued.Add(instance);
		}

		/// <summary>The programs of a graph at its current version, compiling when the version changed. Null when the graph does not compile.</summary>
		public static VfxProgramSet GetPrograms(VfxGraph graph)
		{
			VfxProgramSet set = StartPrograms(graph);
			Finish(graph, set);
			return set;
		}

		/// <summary>
		///     Compiles a graph to GLSL when its version changed and starts the builds of its programs without waiting for
		///     them (<see cref="GetPrograms" /> does): a load starts every graph it brought, then prepares them, and the
		///     driver compiles all their programs together instead of one after another.
		/// </summary>
		public static VfxProgramSet StartPrograms(VfxGraph graph)
		{
			VfxProgramSet set;
			if (programs.TryGetValue(graph, out set) && set.GraphVersion == graph.Version)
			{
				return set;
			}

			set?.Dispose();
			long started = FrameProfiler.Timestamp;
			set = Build(graph);
			// Only with the hitch profiler on (its text is not built otherwise).
			if (started != 0)
			{
				FrameProfiler.EventSince(started, "vfx graph", graph.AssetPath ?? graph.name ?? "graph", $"compiled to GLSL, the builds of its {set.Started.Count} programs started");
			}
			programs[graph] = set;
			return set;
		}

		/// <summary>
		///     A graph a load brought (a scene's or a preloaded prefab's effect): its programs are compiled now, with the
		///     runtime's own kernels, on the thread that holds the GL context, instead of in the frame its first effect
		///     simulates (on the 780M 200 to 730 ms a graph with nothing compiled yet, 30 to 50 ms from the binary cache).
		/// </summary>
		public static void Prepare(VfxGraph graph)
		{
			if (graph == null || !GraphicsThread.HasContext)
			{
				return;
			}

			FrameKernel.Program.EnsureBuilt();
			Sort.Program.EnsureBuilt();
			GetPrograms(graph);
		}

		/// <summary>The last compiled programs of a graph without recompiling (editor status), or null.</summary>
		public static VfxProgramSet PeekPrograms(VfxGraph graph)
		{
			VfxProgramSet set;
			if (!programs.TryGetValue(graph, out set))
			{
				return null;
			}

			Finish(graph, set);
			return set;
		}

		/// <summary>Drops the compiled programs of a graph (recompiled on next use).</summary>
		public static void Invalidate(VfxGraph graph)
		{
			VfxProgramSet set;
			if (programs.TryGetValue(graph, out set))
			{
				set.Dispose();
				programs.Remove(graph);
			}
		}

		// ---------------------------------------------------------------- simulation

		/// <summary>
		///     Runs the simulation the effects queued this frame (the renderer's first pass; the other renderers of the
		///     frame find nothing queued): the CPU half of every effect (spawners, parameters), then the GPU phases of
		///     all systems together.
		/// </summary>
		public static void RunSimulation()
		{
			entryPoolUsed = 0;
			if (queued.Count == 0 && !paramsDirty)
			{
				return;
			}

			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
			stats = new VfxFrameStats { Instances = instances.Count };
			InvalidateDrawState();
			if (queued.Count > 0)
			{
				Frame++;
			}

			simulated.Clear();
			int maxLayer = 0;
			foreach (VfxInstance instance in queued)
			{
				instance.PrepareFrame(Frame, simulated);
				if (instance.Frozen)
				{
					stats.FrozenInstances++;
				}
			}

			queued.Clear();
			long prepared = System.Diagnostics.Stopwatch.GetTimestamp();
			foreach (VfxSystemState state in simulated)
			{
				maxLayer = Math.Max(maxLayer, state.Layer);
			}

			PackParameters();
			long packed = System.Diagnostics.Stopwatch.GetTimestamp();
			if (simulated.Count > 0)
			{
				Dispatch(maxLayer);
				ScheduleReadback();
			}

			long dispatched = System.Diagnostics.Stopwatch.GetTimestamp();
			PollReadbacks();
			stats.SimulatedSystems = simulated.Count;
			stats.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedStart;
			Gfx.Commands.SetProgram(null);
			long end = System.Diagnostics.Stopwatch.GetTimestamp();
			double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			LastSimulationCpuMs = (end - start) * toMs;
			stats.PrepareMs = (prepared - start) * toMs;
			stats.PackMs = (packed - prepared) * toMs;
			stats.DispatchMs = (dispatched - packed) * toMs;
			stats.ReadbackMs = (end - dispatched) * toMs;
		}

		/// <summary>Takes a counter block for a new system (its uints start at block * <see cref="VfxCodegen.CounterBlock" />).</summary>
		internal static int AllocateCounterBlock(VfxSystemState owner)
		{
			int block = freeBlocks.Count > 0 ? freeBlocks.Pop() : counterHigh++;
			if (block >= counterBlocks || counterBuffer == null)
			{
				GrowCounterPool(Math.Max(64, Math.Max(counterBlocks * 2, block + 1)));
			}

			blockOwners[block] = owner;
			return block;
		}

		internal static void FreeCounterBlock(int block)
		{
			if (block < 0 || block >= blockOwners.Length)
			{
				return;
			}

			blockOwners[block] = null;
			freeBlocks.Push(block);
		}

		// ---------------------------------------------------------------- views and draws

		/// <summary>The camera basis particle draws face and fade towards, for the following draws of a view.</summary>
		public static void BeginView(Vector3 position, Vector3 forward, Vector3 up, Vector3 right)
		{
			viewPosition = position;
			viewForward = forward;
			viewData[0] = position.X;
			viewData[1] = position.Y;
			viewData[2] = position.Z;
			viewData[4] = forward.X;
			viewData[5] = forward.Y;
			viewData[6] = forward.Z;
			viewData[8] = up.X;
			viewData[9] = up.Y;
			viewData[10] = up.Z;
			viewData[12] = right.X;
			viewData[13] = right.Y;
			viewData[14] = right.Z;
			viewBuffer ??= Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Uniform, 0, true, "VFX view"));
			viewBuffer.SetData(viewData);
			Gfx.Commands.SetUniformBuffer(2, viewBuffer);
			viewStamp++;
			InvalidateDrawState();
		}

		/// <summary>The view basis of a camera, or of an inverted view matrix (a light).</summary>
		public static void BeginView(Matrix4 view, Camera camera)
		{
			if (camera != null)
			{
				BeginView(camera.transform.position, camera.transform.forward, camera.transform.up, camera.transform.right);
				return;
			}

			Matrix4 inverse = view.Inverted();
			BeginView(inverse.ExtractTranslation(), -Vector3.TransformNormal(Vector3.UnitZ, inverse).Normalized(), Vector3.TransformNormal(Vector3.UnitY, inverse).Normalized(), Vector3.TransformNormal(Vector3.UnitX, inverse).Normalized());
		}

		/// <summary>The transparent outputs of every visible instance, for the transparent pass's sorting.</summary>
		public static void CollectTransparent(RenderContext context, List<VfxDrawEntry> entries)
		{
			foreach (VfxInstance instance in instances)
			{
				instance.CollectDraws(context, entries, true);
			}
		}

		/// <summary>
		///     Before the transparent pass draws its particle entries: the view block, then the back-to-front sort of
		///     every sorted output of the view in one batch (their passes interleaved).
		/// </summary>
		public static void PrepareDraws(RenderContext context, List<VfxDrawEntry> entries)
		{
			BeginView(context.View, context.Camera);
			sortJobs.Clear();
			foreach (VfxDrawEntry entry in entries)
			{
				if (entry.Instance.PrepareSort(entry))
				{
					sortJobs.Add(entry);
				}
			}

			if (sortJobs.Count > 0)
			{
				RunSorts();
			}
		}

		/// <summary>Draws the opaque outputs (depth written) of every visible instance.</summary>
		public static void DrawOpaque(RenderContext context)
		{
			opaqueScratch.Clear();
			foreach (VfxInstance instance in instances)
			{
				instance.CollectDraws(context, opaqueScratch, false);
			}

			if (opaqueScratch.Count == 0)
			{
				return;
			}

			BeginView(context.View, context.Camera);
			foreach (VfxDrawEntry entry in opaqueScratch)
			{
				entry.Instance.Draw(context, entry.System, entry.Output, false, null);
			}

			opaqueScratch.Clear();
		}

		/// <summary>Draws the shadow-casting outputs into the bound shadow map.</summary>
		public static void DrawShadowCasters(RenderContext context, Matrix4 lightView, Matrix4 lightProjection)
		{
			bool any = false;
			foreach (VfxInstance instance in instances)
			{
				if (instance.HasShadowCasters)
				{
					any = true;
					break;
				}
			}

			if (!any)
			{
				return;
			}

			BeginView(lightView, null);
			foreach (VfxInstance instance in instances)
			{
				instance.DrawShadows(context, lightView, lightProjection);
			}
		}

		/// <summary>An entry of the pool (valid until the next frame).</summary>
		internal static void CountDraw()
		{
			stats.Draws++;
		}

		internal static void CountSkipped()
		{
			stats.SkippedSystems++;
		}

		internal static VfxDrawEntry RentEntry()
		{
			if (entryPoolUsed == entryPool.Count)
			{
				entryPool.Add(new VfxDrawEntry());
			}

			return entryPool[entryPoolUsed++];
		}

		/// <summary>
		///     The index buffer of an indexed output shape for at least the given particles: per particle
		///     <paramref name="vertices" /> vertices and the triangles of the shape (quad, triangle, octagon, or a strip of
		///     <paramref name="vertices" /> / 2 points). The draws have no vertex buffers: their vertices come from the index.
		/// </summary>
		internal static GpuBuffer ShapeIndexBuffer(VfxOutputDescription output, int particles)
		{
			int vertices = output.VerticesPerParticle;
			int indices = output.IndicesPerParticle;
			long key = ((long)output.Kind << 40) | ((long)vertices << 20) | (uint)indices;
			IndexBuffer entry;
			if (!indexBuffers.TryGetValue(key, out entry))
			{
				entry = new IndexBuffer();
				indexBuffers[key] = entry;
			}

			if (entry.Particles < particles || entry.Buffer == null)
			{
				int count = Math.Max(particles, Math.Max(64, entry.Particles * 2));
				int[] pattern = Pattern(output);
				var data = new uint[(long)count * pattern.Length > int.MaxValue / 4 ? int.MaxValue / 4 : count * pattern.Length];
				count = data.Length / pattern.Length;
				for (int p = 0, i = 0; p < count; p++)
				{
					uint baseVertex = (uint)(p * vertices);
					for (int k = 0; k < pattern.Length; k++)
					{
						data[i++] = baseVertex + (uint)pattern[k];
					}
				}

				entry.Buffer ??= Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Index, 0, false, "VFX shape indices"));
				entry.Buffer.SetData(data);
				entry.Particles = count;
			}

			return entry.Buffer;
		}

		// ---------------------------------------------------------------- depth

		/// <summary>
		///     Copies the view's scene depth (opaques drawn) for this frame's soft particles and, the lighting camera of
		///     a stack, the next simulation's depth collisions. Called by the transparent pass before any particle draws,
		///     once the opaque surfaces it draws itself (queues up to GeometryLast + 1: alpha clip, vertex functions) are in.
		/// </summary>
		public static VfxDepthCapture CaptureDepth(RenderContext context)
		{
			// The other cameras of a stack draw only their layers: their depth must not replace the world's.
			Dictionary<Renderer, VfxDepthCapture> captures = context.IsLightingCamera ? depthCaptures : stackDepthCaptures;
			VfxDepthCapture capture;
			if (!captures.TryGetValue(context.Renderer, out capture))
			{
				capture = new VfxDepthCapture();
				captures[context.Renderer] = capture;
			}

			RenderTexture source = context.SceneDepth;
			if (source == null)
			{
				capture.Valid = false;
				return capture;
			}

			if (capture.Depth == null || capture.Width != source.Width || capture.Height != source.Height)
			{
				capture.Depth?.Delete();
				capture.Depth = new RenderTexture(source.Width, source.Height, RenderTextureFormat.Depth32F, false);
				capture.Width = source.Width;
				capture.Height = source.Height;
			}

			Gfx.Commands.CopyTexture(source.Gpu, capture.Depth.Gpu);
			capture.ViewProjection = context.ViewProjection;
			capture.InverseViewProjection = context.ViewProjection.Inverted();
			capture.Valid = true;
			if (context.IsLightingCamera)
			{
				LastDepth = capture;
			}

			return capture;
		}

		/// <summary>Binds a depth capture (or "none") to a program's depth uniforms.</summary>
		public static void SetDepthUniforms(ShaderProgram program, VfxDepthCapture capture)
		{
			bool valid = capture != null && capture.Valid && capture.Depth != null;
			program.SetVector4("vfxDepthParams", valid ? new Vector4(capture.Width, capture.Height, 1f, 0f) : new Vector4(1f, 1f, 0f, 0f));
			if (valid)
			{
				program.SetMatrix4("vfxDepthViewProjection", capture.ViewProjection);
				program.SetMatrix4("vfxDepthInvViewProjection", capture.InverseViewProjection);
				program.SetTexture("vfxSceneDepth", capture.Depth);
			}
		}

		// ---------------------------------------------------------------- lifetime

		/// <summary>Releases every program, buffer and capture (project closed).</summary>
		public static void Clear()
		{
			foreach (VfxInstance instance in new List<VfxInstance>(instances))
			{
				instance.Dispose();
			}

			instances.Clear();
			queued.Clear();
			foreach (VfxProgramSet set in programs.Values)
			{
				set.Dispose();
			}

			programs.Clear();
			foreach (VfxDepthCapture capture in depthCaptures.Values)
			{
				capture.Depth?.Delete();
			}

			foreach (VfxDepthCapture capture in stackDepthCaptures.Values)
			{
				capture.Depth?.Delete();
			}

			depthCaptures.Clear();
			stackDepthCaptures.Clear();
			LastDepth = null;
			foreach (IndexBuffer buffer in indexBuffers.Values)
			{
				buffer.Buffer?.Dispose();
			}

			indexBuffers.Clear();
			for (int i = 0; i < readbacks.Length; i++)
			{
				if (readbacks[i] != null)
				{
					readbacks[i].Fence?.Dispose();
					readbacks[i].Buffer?.Dispose();
					readbacks[i] = null;
				}
			}

			DeleteBuffer(ref counterBuffer);
			DeleteBuffer(ref paramsBuffer);
			DeleteBuffer(ref entryBuffer);
			DeleteBuffer(ref viewBuffer);
			counterBlocks = 0;
			counterHigh = 0;
			freeBlocks.Clear();
			blockOwners = Array.Empty<VfxSystemState>();
			paramsDirty = true;
		}

		/// <summary>Drops the depth captures of a renderer being disposed.</summary>
		public static void ReleaseRenderer(Renderer renderer)
		{
			VfxDepthCapture capture;
			if (depthCaptures.TryGetValue(renderer, out capture))
			{
				capture.Depth?.Delete();
				depthCaptures.Remove(renderer);
				if (LastDepth == capture)
				{
					LastDepth = null;
				}
			}

			if (stackDepthCaptures.TryGetValue(renderer, out capture))
			{
				capture.Depth?.Delete();
				stackDepthCaptures.Remove(renderer);
			}
		}

		// ---------------------------------------------------------------- culling helpers

		/// <summary>World-space frustum planes (inside: dot(n, p) + d >= 0) of a row-vector view-projection matrix.</summary>
		public static void FrustumPlanes(Matrix4 m, Vector4[] planes)
		{
			Vector4 c0 = m.Column0;
			Vector4 c1 = m.Column1;
			Vector4 c2 = m.Column2;
			Vector4 c3 = m.Column3;
			planes[0] = c3 + c0;
			planes[1] = c3 - c0;
			planes[2] = c3 + c1;
			planes[3] = c3 - c1;
			planes[4] = c3 + c2;
			planes[5] = c3 - c2;
			for (int i = 0; i < 6; i++)
			{
				float length = planes[i].Xyz.Length;
				if (length > 0f)
				{
					planes[i] /= length;
				}
			}
		}

		public static bool BoxInFrustum(Vector4[] planes, Vector3 min, Vector3 max)
		{
			Vector3 center = (min + max) * 0.5f;
			Vector3 extent = (max - min) * 0.5f;
			for (int i = 0; i < 6; i++)
			{
				Vector4 plane = planes[i];
				float radius = Math.Abs(plane.X) * extent.X + Math.Abs(plane.Y) * extent.Y + Math.Abs(plane.Z) * extent.Z;
				if (Vector3.Dot(plane.Xyz, center) + plane.W + radius < 0f)
				{
					return false;
				}
			}

			return true;
		}

		#endregion

		#region PrivateMethods

		/// <summary>Packs the parameter block of every built system into the shared uniform buffer (one upload).</summary>
		private static void PackParameters()
		{
			int alignFloats = UniformAlignment / sizeof(float);
			int cursor = 0;
			foreach (VfxInstance instance in instances)
			{
				foreach (VfxSystemState state in instance.SystemStates)
				{
					float[] data = state.UboData;
					if (data == null)
					{
						continue;
					}

					cursor = (cursor + alignFloats - 1) / alignFloats * alignFloats;
					if (cursor + data.Length > paramsStaging.Length)
					{
						Array.Resize(ref paramsStaging, Math.Max(paramsStaging.Length * 2, cursor + data.Length));
					}

					Array.Copy(data, 0, paramsStaging, cursor, data.Length);
					state.UboOffset = cursor * sizeof(float);
					state.UboBytes = data.Length * sizeof(float);
					cursor += data.Length;
				}
			}

			paramsDirty = false;
			if (cursor == 0)
			{
				return;
			}

			paramsBuffer ??= Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Uniform, 0, true, "VFX parameters"));
			paramsBuffer.SetData(paramsStaging, cursor);
		}

		/// <summary>Binds a system's parameter block (its range of the shared buffer).</summary>
		internal static void BindParameters(VfxSystemState state)
		{
			if (paramsBuffer != null && state.UboBytes > 0 && (boundParamsOffset != state.UboOffset || boundParamsBytes != state.UboBytes))
			{
				Gfx.Commands.SetUniformBuffer(1, paramsBuffer, state.UboOffset, state.UboBytes);
				boundParamsOffset = state.UboOffset;
				boundParamsBytes = state.UboBytes;
			}
		}

		// ---------------------------------------------------------------- binding cache

		// What the particle draws and kernels bound last, so consecutive ones skip unchanged bindings. Forgotten
		// whenever other rendering may have changed them (frame start, a new view, a mesh draw in between).
		private static readonly GpuBuffer[] boundStorage = new GpuBuffer[16];
		private static int boundParamsOffset = -1;
		private static int boundParamsBytes = -1;
		private static readonly Dictionary<ShaderProgram, ProgramState> programStates = new Dictionary<ShaderProgram, ProgramState>();
		private static long viewStamp;
		private static Matrix4 frustumMatrix;
		private static readonly Vector4[] frustumPlanes = new Vector4[6];
		private static bool frustumValid;

		/// <summary>Per program: the view its view uniforms were set for, and the values set since.</summary>
		internal sealed class ProgramState
		{
			public long ViewStamp = -1;
			public long Mask = -1;
			public VfxDepthCapture Depth;
			public bool DepthSet;
		}

		/// <summary>Forgets the bindings and render state particle draws assume (other rendering ran since).</summary>
		public static void InvalidateDrawState()
		{
			Array.Clear(boundStorage);
			boundParamsOffset = -1;
			boundParamsBytes = -1;
			lastBlend = -1;
			lastDepthWrite = -1;
			lastCull = -1;
		}

		/// <summary>Binds a storage buffer unless it is bound there already.</summary>
		internal static void BindStorage(int binding, GpuBuffer buffer)
		{
			if (binding < boundStorage.Length && boundStorage[binding] == buffer)
			{
				return;
			}

			Gfx.Commands.SetStorageBuffer(binding, buffer);
			if (binding < boundStorage.Length)
			{
				boundStorage[binding] = buffer;
			}
		}

		/// <summary>The view stamp and cached values of a program (its uniforms keep their values between draws).</summary>
		internal static ProgramState StateOf(ShaderProgram program)
		{
			ProgramState state;
			if (!programStates.TryGetValue(program, out state))
			{
				state = new ProgramState();
				programStates[program] = state;
			}

			return state;
		}

		internal static long ViewStamp => viewStamp;

		/// <summary>The position and forward axis of the current view (sort keys measure depth along it).</summary>
		internal static Vector3 ViewPosition => viewPosition;

		internal static Vector3 ViewForward => viewForward;

		/// <summary>The frustum planes of a view-projection matrix (computed once per view).</summary>
		internal static Vector4[] Frustum(Matrix4 viewProjection)
		{
			if (!frustumValid || frustumMatrix != viewProjection)
			{
				FrustumPlanes(viewProjection, frustumPlanes);
				frustumMatrix = viewProjection;
				frustumValid = true;
			}

			return frustumPlanes;
		}

		private static int lastBlend = -1;
		private static int lastDepthWrite = -1;
		private static int lastCull = -1;

		/// <summary>Blend, depth write and culling of a particle draw, skipping what is set already.</summary>
		internal static void SetDrawState(int blend, bool depthWrite, bool cullBack)
		{
			ref RenderState state = ref Gfx.Commands.State;
			if (blend != lastBlend)
			{
				switch (blend)
				{
					case 1:
						state.SetBlend(BlendFactor.One, BlendFactor.One);
						break;
					case 2:
						state.SetBlend(BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
						break;
					case 3:
						state.SetBlend(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);
						break;

					// The same into a low-resolution layer (Rendering.LowResolutionTransparency): its alpha starts at 1 and
					// keeps what the scene will show through (each layer takes its alpha's share away; additive takes none).
					case 4:
						state.SetBlend(BlendFactor.One, BlendFactor.One, BlendFactor.Zero, BlendFactor.One);
						break;
					case 5:
						state.SetBlend(BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha);
						break;
					case 6:
						state.SetBlend(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha);
						break;
					default:
						state.Blend = false;
						break;
				}

				lastBlend = blend;
			}

			int write = depthWrite ? 1 : 0;
			if (write != lastDepthWrite)
			{
				state.SetDepth(true, depthWrite, CompareFunction.LessEqual);
				lastDepthWrite = write;
			}

			int cull = cullBack ? 1 : 0;
			if (cull != lastCull)
			{
				state.Culling = cullBack ? FaceCulling.Back : FaceCulling.None;
				lastCull = cull;
			}
		}

		/// <summary>The GPU phases of every simulated system: frame kernel, then per layer of GPU events the spawns, the initialize kernels and the update kernels.</summary>
		private static void Dispatch(int maxLayer)
		{
			// Frame kernel entries: every simulated system for mode 0, then the GPU-event children per layer for mode 1.
			int entries = simulated.Count;
			int needed = entries * 4 * 2;
			if (entryStaging.Length < needed)
			{
				Array.Resize(ref entryStaging, Math.Max(needed, entryStaging.Length * 2));
			}

			int write = 0;
			foreach (VfxSystemState state in simulated)
			{
				entryStaging[write++] = (uint)state.CounterBase;
				entryStaging[write++] = (uint)state.Description.Capacity;
				entryStaging[write++] = state.SpawnRequest;
				entryStaging[write++] = state.FrameFlags;
			}

			int childStart = write / 4;
			if (layerStarts.Length < maxLayer + 2)
			{
				layerStarts = new int[maxLayer + 2];
			}
			for (int layer = 1; layer <= maxLayer; layer++)
			{
				layerStarts[layer] = write / 4;
				foreach (VfxSystemState state in simulated)
				{
					if (state.Layer == layer && state.SpawnsFromParent)
					{
						entryStaging[write++] = (uint)state.CounterBase;
						entryStaging[write++] = (uint)state.Description.Capacity;
						entryStaging[write++] = (uint)(state.Parent.CounterBase + VfxCodegen.CounterEvents + state.Description.ParentEvent);
						entryStaging[write++] = state.FrameFlags;
					}
				}
			}

			layerStarts[maxLayer + 1] = write / 4;
			_ = childStart;
			entryBuffer ??= Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, "VFX frame entries"));
			entryBuffer.SetData(entryStaging, write);
			BindStorage(4, counterBuffer);
			BindStorage(11, entryBuffer);

			ComputeShader frame = FrameKernel;
			frame.Use();
			frame.Program.SetInt("vfxMode", 0);
			frame.Program.SetUint("vfxEntryOffset", 0u);
			frame.Program.SetUint("vfxEntryCount", (uint)entries);
			frame.Dispatch((entries + 63) / 64);
			stats.Dispatches++;
			EndPhase();

			for (int layer = 0; layer <= maxLayer; layer++)
			{
				if (layer > 0)
				{
					int count = layerStarts[layer + 1] - layerStarts[layer];
					if (count > 0)
					{
						frame.Use();
						BindStorage(4, counterBuffer);
						BindStorage(11, entryBuffer);
						frame.Program.SetInt("vfxMode", 1);
						frame.Program.SetUint("vfxEntryOffset", (uint)layerStarts[layer]);
						frame.Program.SetUint("vfxEntryCount", (uint)count);
						frame.Dispatch((count + 63) / 64);
						stats.Dispatches++;
						EndPhase();
					}
				}

				// The systems of the layer, grouped by program so instances of a graph share the program switch.
				layerScratch.Clear();
				foreach (VfxSystemState state in simulated)
				{
					if (state.Layer == layer)
					{
						layerScratch.Add(state);
					}
				}

				layerScratch.Sort(CompareByProgram);
				bool anyInit = false;
				ShaderProgram current = null;
				foreach (VfxSystemState state in layerScratch)
				{
					if (!state.NeedsInit)
					{
						continue;
					}

					ComputeShader initialize = Kernel(state, false);
					ShaderProgram program = initialize.Program;
					if (program != current)
					{
						program.Use();
						current = program;
					}

					state.Instance.BindForKernel(state, program, false);
					initialize.DispatchIndirect(counterBuffer, (state.CounterBase + VfxCodegen.CounterInitDispatch) * sizeof(uint));
					stats.Dispatches++;
					anyInit = true;
				}

				if (anyInit)
				{
					EndPhase();
				}

				current = null;
				foreach (VfxSystemState state in layerScratch)
				{
					ComputeShader update = Kernel(state, true);
					ShaderProgram program = update.Program;
					if (program != current)
					{
						program.Use();
						current = program;
					}

					state.Instance.BindForKernel(state, program, true);
					if (state.Dense)
					{
						update.Dispatch((state.Description.Capacity + VfxCodegen.GroupSize - 1) / VfxCodegen.GroupSize);
					}
					else
					{
						update.DispatchIndirect(counterBuffer, (state.CounterBase + VfxCodegen.CounterUpdateDispatch) * sizeof(uint));
					}
					stats.Dispatches++;
				}

				EndPhase();
			}

			foreach (VfxSystemState state in simulated)
			{
				state.Instance.AfterDispatch(state, Frame);
			}
		}

		private static int CompareByProgram(VfxSystemState a, VfxSystemState b)
		{
			return a.ProgramOrder.CompareTo(b.ProgramOrder);
		}

		/// <summary>
		///     A system's initialize or update kernel for this frame: the ray query variant of a system that reads the scene's
		///     distance field (collisions, conforming, its samples) while the GI's rays trace the scene's triangles
		///     (<see cref="SceneRays" />: no distance field is built then), the plain one otherwise.
		/// </summary>
		private static ComputeShader Kernel(VfxSystemState state, bool update)
		{
			VfxProgramSet.SystemPrograms kernels = state.Programs;
			ComputeShader rays = update ? kernels.UpdateRays : kernels.InitializeRays;
			return rays != null && SceneRays != null && TraceScene ? rays : update ? kernels.Update : kernels.Initialize;
		}

		/// <summary>
		///     The effects that read the scene's surfaces trace its triangles while the GI's rays do (true), or ask for the
		///     global distance field as they did before (false: a measurement switch, TestGame vfxrays=0).
		/// </summary>
		public static bool TraceScene = true;

		/// <summary>
		///     The scene whose triangles the effects that read the scene's surfaces trace this frame: the renderer's GI scene
		///     while the GI's rays trace with ray queries and its top level exists (set by the simulation pass); null
		///     otherwise (they read the global distance field, asking for it).
		/// </summary>
		internal static Rendering.Lumen.LumenScene SceneRays;

		/// <summary>
		///     A phase's dispatches are given: the next phase reads what they wrote (the device makes it visible), so its
		///     dispatches cannot run alongside them. Counted in <see cref="VfxFrameStats.Barriers" />.
		/// </summary>
		private static void EndPhase()
		{
			stats.Barriers++;
		}

		/// <summary>Copies the counter pool into a free readback slot with a fence (skipped when every slot is still in flight).</summary>
		private static void ScheduleReadback()
		{
			int bytes = counterHigh * VfxCodegen.CounterBlock * sizeof(uint);
			if (bytes == 0)
			{
				return;
			}

			Readback free = null;
			for (int i = 0; i < readbacks.Length; i++)
			{
				readbacks[i] ??= new Readback();
				if (readbacks[i].Fence == null)
				{
					free = readbacks[i];
					break;
				}
			}

			if (free == null)
			{
				return;
			}

			if (free.Buffer == null || free.Buffer.Size < bytes)
			{
				free.Buffer?.Dispose();
				free.Buffer = Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Readback, Math.Max(bytes, 4096), false, "VFX counter readback"));
			}

			Gfx.Commands.CopyBuffer(counterBuffer, 0, free.Buffer, 0, bytes);
			free.Fence = Gfx.Device.CreateFence();
			free.Frame = Frame;
			free.Blocks = counterHigh;
		}

		/// <summary>Reads the newest finished readback (never waits) and hands every system its alive count and bounds.</summary>
		private static void PollReadbacks()
		{
			Readback newest = null;
			for (int i = 0; i < readbacks.Length; i++)
			{
				Readback slot = readbacks[i];
				if (slot == null || slot.Fence == null)
				{
					continue;
				}

				if (!slot.Fence.IsSignaled)
				{
					continue;
				}

				if (newest == null || slot.Frame > newest.Frame)
				{
					if (newest != null)
					{
						newest.Fence.Dispose();
						newest.Fence = null;
					}

					newest = slot;
				}
				else
				{
					slot.Fence.Dispose();
					slot.Fence = null;
				}
			}

			if (newest == null)
			{
				return;
			}

			newest.Fence.Dispose();
			newest.Fence = null;
			int uints = newest.Blocks * VfxCodegen.CounterBlock;
			if (readbackData.Length < uints)
			{
				Array.Resize(ref readbackData, Math.Max(uints, readbackData.Length * 2));
			}

			newest.Buffer.GetData(0, readbackData, uints);
			int blocks = Math.Min(newest.Blocks, blockOwners.Length);
			bool print = DebugCounters && newest.Frame / 60 != lastDebugPrint;
			if (print)
			{
				lastDebugPrint = newest.Frame / 60;
			}

			for (int b = 0; b < blocks; b++)
			{
				VfxSystemState owner = blockOwners[b];
				if (owner != null && owner.ValidFromFrame <= newest.Frame)
				{
					owner.ApplyReadback(newest.Frame, readbackData, b * VfxCodegen.CounterBlock);
					if (print)
					{
						int o = b * VfxCodegen.CounterBlock;
						Debug.Log($"[VFX] frame {newest.Frame} (now {Frame}) {owner.Instance.Name}/{owner.Description.Name}: spawn {readbackData[o + VfxCodegen.CounterSpawn]} in {readbackData[o + VfxCodegen.CounterAliveIn]} out {readbackData[o + VfxCodegen.CounterAliveOut]} dead {readbackData[o + VfxCodegen.CounterDead]} draw0 {readbackData[o + VfxCodegen.CounterDraw]}/{readbackData[o + VfxCodegen.CounterDraw + 1]} ids {readbackData[o + VfxCodegen.CounterIdNext]} upper {owner.AliveUpperBound}");
					}
				}
			}
		}

		private static void GrowCounterPool(int blocks)
		{
			long bytes = (long)blocks * VfxCodegen.CounterBlock * sizeof(uint);
			if (counterBuffer == null)
			{
				// The kernels write their dispatch and draw arguments here, and the dispatches and draws read them.
				counterBuffer = Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage | BufferUsage.Indirect, bytes, false, "VFX counters"));
			}
			else
			{
				counterBuffer.Resize(bytes, (long)counterBlocks * VfxCodegen.CounterBlock * sizeof(uint));
			}

			// The pool has new storage: it is bound again wherever it was.
			Array.Clear(boundStorage);
			counterBlocks = blocks;
			Array.Resize(ref blockOwners, blocks);
		}

		// ---------------------------------------------------------------- sorting

		/// <summary>The sort passes of every job of the view, interleaved: pass r of every job, then pass r + 1 of every job.</summary>
		private static void RunSorts()
		{
			ComputeShader kernel = Sort;
			kernel.Use();
			ShaderProgram program = kernel.Program;
			if (sortMode == -2)
			{
				sortMode = program.GetSlot("vfxSortMode");
				sortCount = program.GetSlot("vfxSortCount");
				sortK = program.GetSlot("vfxSortK");
				sortJ = program.GetSlot("vfxSortJ");
				sortAliveCounter = program.GetSlot("vfxSortAliveCounter");
				sortPositionPlane = program.GetSlot("vfxSortPositionPlane");
				sortCapacity = program.GetSlot("vfxSortCapacity");
				sortWorldSpace = program.GetSlot("vfxSortWorldSpace");
				sortLocalToWorld = program.GetSlot("vfxSortLocalToWorld");
				sortViewPosition = program.GetSlot("vfxSortViewPosition");
				sortViewForward = program.GetSlot("vfxSortViewForward");
				sortIndexBits = program.GetSlot("vfxSortIndexBits");
				sortDepthRange = program.GetSlot("vfxSortDepthRange");
			}

			GpuProgram gpu = program.Gpu;
			gpu.SetVector3(sortViewPosition, viewPosition);
			gpu.SetVector3(sortViewForward, viewForward);
			BindStorage(4, counterBuffer);

			// Pass 0 of every job: keys and the sort of every chunk.
			int rounds = 0;
			foreach (VfxDrawEntry job in sortJobs)
			{
				VfxSystemState state = job.State;
				int n = job.SortCount;
				state.Instance.BindForSort(state, job.Output);
				gpu.SetInt(sortMode, 0);
				gpu.SetUInt(sortCount, (uint)n);
				gpu.SetUInt(sortAliveCounter, (uint)(state.CounterBase + VfxCodegen.CounterAliveOut));
				gpu.SetUInt(sortPositionPlane, (uint)Math.Max(0, state.PositionPlane));
				gpu.SetUInt(sortCapacity, (uint)state.Description.Capacity);
				gpu.SetUInt(sortIndexBits, (uint)state.SortIndexBits);
				gpu.SetVector2(sortDepthRange, new Vector2(job.SortDepthNear, job.SortDepthScale));
				gpu.SetInt(sortWorldSpace, state.Description.InitWriter.WorldSpace ? 1 : 0);
				gpu.SetMatrix4(sortLocalToWorld, state.Instance.LocalToWorld);
				kernel.Dispatch(Math.Max(1, n / SortChunk));
				stats.SortDispatches++;
				rounds = Math.Max(rounds, SortPassCount(n));
			}

			stats.Sorts += sortJobs.Count;
			EndPhase();

			// The merges of the lists longer than a chunk, pass by pass.
			for (int round = 1; round < rounds; round++)
			{
				foreach (VfxDrawEntry job in sortJobs)
				{
					int n = job.SortCount;
					int mode;
					int k;
					int j;
					if (!SortPass(n, round, out mode, out k, out j))
					{
						continue;
					}

					job.State.Instance.BindForSort(job.State, job.Output);
					gpu.SetInt(sortMode, mode);
					gpu.SetUInt(sortCount, (uint)n);
					gpu.SetUInt(sortK, (uint)k);
					gpu.SetUInt(sortJ, (uint)j);
					kernel.Dispatch(mode == 1 ? Math.Max(1, n / 2 / SortThreads) : mode == 3 ? Math.Max(1, n / 4 / SortThreads) : Math.Max(1, n / SortChunk));
					stats.SortDispatches++;
				}

				EndPhase();
			}

			Gfx.Commands.SetProgram(null);
			if (CheckSorts && Frame % 120 == 0)
			{
				ValidateSorts();
			}
		}

		/// <summary>Validate every sort of the view on the CPU (environment LEL_VFX_CHECKSORT=1; stalls, debugging only).</summary>
		public static bool CheckSorts = Environment.GetEnvironmentVariable("LEL_VFX_CHECKSORT") == "1";

		private static void ValidateSorts()
		{
			Gfx.Device.Finish();
			var counters = new uint[VfxCodegen.CounterBlock];
			foreach (VfxDrawEntry job in sortJobs)
			{
				VfxSystemState state = job.State;
				int n = job.SortCount;
				var keys = new uint[n];
				state.SortKeys[job.Output].GetData(0, keys);
				counterBuffer.GetData((long)state.CounterBase * sizeof(uint), counters);
				uint alive = counters[VfxCodegen.CounterAliveOut];
				uint mask = (1u << state.SortIndexBits) - 1u;
				var seen = new bool[state.Description.Capacity];
				int disorder = 0;
				int real = 0;
				int duplicates = 0;
				for (int i = 0; i < n; i++)
				{
					if (keys[i] != 0xFFFFFFFFu)
					{
						real++;
						uint particle = keys[i] & mask;
						if (particle >= seen.Length || seen[particle])
						{
							duplicates++;
						}
						else
						{
							seen[particle] = true;
						}
					}

					if (i > 0 && keys[i] < keys[i - 1])
					{
						disorder++;
					}
				}

				bool ok = disorder == 0 && real == alive && duplicates == 0;
				Debug.Log($"[VFX] sort check {state.Instance.Name}/{state.Description.Name}: n {n}, alive {alive}, keyed {real}, out of order {disorder}, bad indices {duplicates}{(ok ? " OK" : " FAILED")}");
			}
		}

		/// <summary>
		///     Passes of a list of n (a power of two): the chunk sort, then per merge stage above the chunk its global
		///     steps (two per pass while two remain) and the local finish.
		/// </summary>
		private static int SortPassCount(int n)
		{
			int passes = 1;
			for (int k = SortChunk * 2; k <= n; k <<= 1)
			{
				for (int j = k >> 1; j >= SortChunk; j >>= (j >> 1) >= SortChunk ? 2 : 1)
				{
					passes++;
				}

				passes++;
			}

			return passes;
		}

		/// <summary>The pass <paramref name="index" /> of a sort of n: mode 1 one global step, 3 two global steps (j and j / 2), 2 the local finish.</summary>
		private static bool SortPass(int n, int index, out int mode, out int k, out int j)
		{
			int pass = 1;
			for (k = SortChunk * 2; k <= n; k <<= 1)
			{
				for (j = k >> 1; j >= SortChunk; j >>= (j >> 1) >= SortChunk ? 2 : 1)
				{
					if (pass++ == index)
					{
						mode = (j >> 1) >= SortChunk ? 3 : 1;
						return true;
					}
				}

				if (pass++ == index)
				{
					mode = 2;
					j = SortChunk / 2;
					return true;
				}
			}

			mode = 0;
			k = 0;
			j = 0;
			return false;
		}

		private static int[] Pattern(VfxOutputDescription output)
		{
			if (output.Kind == VfxOutputKind.Strip)
			{
				int points = Math.Max(2, output.VerticesPerParticle / 2);
				var strip = new int[(points - 1) * 6];
				for (int s = 0; s < points - 1; s++)
				{
					int v = s * 2;
					strip[s * 6] = v;
					strip[s * 6 + 1] = v + 1;
					strip[s * 6 + 2] = v + 2;
					strip[s * 6 + 3] = v + 2;
					strip[s * 6 + 4] = v + 1;
					strip[s * 6 + 5] = v + 3;
				}

				return strip;
			}

			switch (output.Primitive)
			{
				case VfxQuadPrimitive.Triangle:
					return new[] { 0, 1, 2 };
				case VfxQuadPrimitive.Octagon:
					return new[] { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5, 0, 5, 6, 0, 6, 7 };
				default:
					return new[] { 0, 1, 2, 0, 2, 3 };
			}
		}

		private static void DeleteBuffer(ref GpuBuffer buffer)
		{
			buffer?.Dispose();
			buffer = null;
		}

		private static VfxProgramSet Build(VfxGraph graph)
		{
			var set = new VfxProgramSet { GraphVersion = graph.Version, Compiled = VfxCompiler.Compile(graph) };
			foreach (VfxCompileMessage message in set.Compiled.Messages)
			{
				if (message.IsError)
				{
					Debug.LogWarning("[VFX] " + graph.name + ": " + message.Message);
				}
			}

			if (DumpSources)
			{
				WriteSources(graph, set.Compiled);
			}

			if (set.Compiled.HasErrors)
			{
				return set;
			}

			// Every program's build is started here (from the binary cache, or handed to the driver); Finish waits for them.
			foreach (VfxSystemDescription system in set.Compiled.Systems)
			{
				var programsOfSystem = new VfxProgramSet.SystemPrograms();
				programsOfSystem.Initialize = Compute(set, system.Name + ".Initialize", system.InitSource, () => programsOfSystem.Initialize = null);
				programsOfSystem.Update = Compute(set, system.Name + ".Update", system.UpdateSource, () => programsOfSystem.Update = null);
				if (system.UsesSdf && GraphicsCapabilities.RayQueries)
				{
					// The kernels that trace the scene's triangles where the GI's rays do (no distance field then).
					programsOfSystem.InitializeRays = Compute(set, system.Name + ".Initialize.Rays", system.InitSource, () => programsOfSystem.InitializeRays = null, ShaderProgram.RayQueryDefine);
					programsOfSystem.UpdateRays = Compute(set, system.Name + ".Update.Rays", system.UpdateSource, () => programsOfSystem.UpdateRays = null, ShaderProgram.RayQueryDefine);
				}
				foreach (VfxOutputDescription output in system.Outputs)
				{
					var outputPrograms = new VfxProgramSet.OutputPrograms();
					string name = system.Name + "." + output.Name;
					outputPrograms.Draw = Graphics(set, name, output.VertexSource, output.FragmentSource, Array.Empty<string>(), () => outputPrograms.Draw = null);
					if (output.CastShadows)
					{
						outputPrograms.Shadow = Graphics(set, name + ".Shadow", output.VertexSource, output.FragmentSource, new[] { "VFX_SHADOW_PASS" }, () => outputPrograms.Shadow = null);
					}

					programsOfSystem.Outputs.Add(outputPrograms);
				}

				set.Systems.Add(programsOfSystem);
			}

			return set;
		}

		// Waits for the set's started programs: one that does not link is reported and taken out of the set.
		private static void Finish(VfxGraph graph, VfxProgramSet set)
		{
			if (set.Started.Count == 0 || !GraphicsThread.HasContext)
			{
				return;
			}

			long started = FrameProfiler.Timestamp;
			int count = set.Started.Count;
			foreach ((string name, ShaderProgram program, Action drop) in set.Started)
			{
				if (!program.IsLinked)
				{
					set.ShaderErrors.Add(name + ": " + program.BuildLog);
					program.Delete();
					drop();
				}
			}

			set.Started.Clear();
			// Only with the hitch profiler on (its text is not built otherwise).
			if (started != 0)
			{
				FrameProfiler.EventSince(started, "vfx graph", graph.AssetPath ?? graph.name ?? "graph", $"waited for its {count} programs (first effect of this graph, or the load that brought it)");
			}
		}

		private static void WriteSources(VfxGraph graph, VfxCompiledGraph compiled)
		{
			try
			{
				string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LELEngine", "VFX");
				System.IO.Directory.CreateDirectory(folder);
				string prefix = (graph.name ?? "graph").Replace(' ', '_') + ".";
				var messages = new System.Text.StringBuilder();
				foreach (VfxCompileMessage message in compiled.Messages)
				{
					messages.Append(message).Append('\n');
				}

				System.IO.File.WriteAllText(System.IO.Path.Combine(folder, prefix + "messages.txt"), messages.ToString());
				foreach (VfxSystemDescription system in compiled.Systems)
				{
					string name = prefix + system.Name.Replace(' ', '_') + ".";
					System.IO.File.WriteAllText(System.IO.Path.Combine(folder, name + "init.comp"), system.InitSource ?? "");
					System.IO.File.WriteAllText(System.IO.Path.Combine(folder, name + "update.comp"), system.UpdateSource ?? "");
					foreach (VfxOutputDescription output in system.Outputs)
					{
						string outputName = name + output.Index + ".";
						System.IO.File.WriteAllText(System.IO.Path.Combine(folder, outputName + "vert"), output.VertexSource ?? "");
						System.IO.File.WriteAllText(System.IO.Path.Combine(folder, outputName + "frag"), output.FragmentSource ?? "");
					}
				}

				Debug.Log("[VFX] Generated sources of " + graph.name + " written to " + folder);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[VFX] Cannot write the generated sources: " + e.Message);
			}
		}

		private static ComputeShader Compute(VfxProgramSet set, string name, string source, Action drop, params string[] defines)
		{
			ShaderProgram program = ShaderProgram.StartFromSources("VFX/" + name, new[] { new KeyValuePair<ShaderStage, string>(ShaderStage.Compute, source) }, defines);
			set.Started.Add((name, program, drop));
			return ComputeShader.FromProgram(program);
		}

		private static ShaderProgram Graphics(VfxProgramSet set, string name, string vertex, string fragment, string[] defines, Action drop)
		{
			ShaderProgram program = ShaderProgram.StartFromSources("VFX/" + name, new[]
			{
				new KeyValuePair<ShaderStage, string>(ShaderStage.Vertex, vertex),
				new KeyValuePair<ShaderStage, string>(ShaderStage.Fragment, fragment)
			}, defines);
			set.Started.Add((name, program, drop));
			return program;
		}

		#endregion
	}

	/// <summary>One output of one system of one instance to draw (collected per view, sorted among the transparents).</summary>
	public sealed class VfxDrawEntry
	{
		public VfxInstance Instance;
		public int System;
		public int Output;
		public int Queue;
		public float DistanceSquared;

		/// <summary>Drawn at half resolution (<see cref="VfxOutputDescription.LowResolution" />).</summary>
		public bool LowResolution;

		/// <summary>The system drawn, and the length of its sort for this view (a power of two at least its alive count).</summary>
		internal VfxSystemState State;

		internal int SortCount;

		/// <summary>The depth range the sort keys of this view span (nearest depth, 1 / span).</summary>
		internal float SortDepthNear;

		internal float SortDepthScale;
	}
}
