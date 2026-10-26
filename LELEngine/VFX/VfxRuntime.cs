using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

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

		#region PublicMethods

		public void Dispose()
		{
			foreach (SystemPrograms system in Systems)
			{
				system.Initialize?.Delete();
				system.Update?.Delete();
				foreach (OutputPrograms output in system.Outputs)
				{
					output.Draw?.Delete();
					output.Shadow?.Delete();
				}
			}

			Systems.Clear();
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
	///     with one memory barrier between the phases instead of several per system. The draw hooks
	///     (<see cref="CollectTransparent" />, <see cref="DrawOpaque" />, <see cref="DrawShadowCasters" />) draw from the
	///     same buffers with indirect arguments the update kernels wrote.
	/// </summary>
	public static class VfxRuntime
	{
		#region PublicFields

		/// <summary>Texture unit of the depth capture in kernels and draws (above the material units, below the lighting ones).</summary>
		public const int DepthTextureUnit = 7;

		/// <summary>Playing instances (components that are enabled).</summary>
		public static IReadOnlyList<VfxInstance> Instances => instances;

		/// <summary>True while any enabled effect simulates (the editor keeps its views refreshing).</summary>
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
		public static bool Available => GLCapabilities.Initialized;

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
		internal static int CounterBuffer => counterBuffer;

		public static ComputeShader Sort => sort ??= new ComputeShader("Engine/VFX/VfxSort.shader");
		public static ComputeShader FrameKernel => frameKernel ??= new ComputeShader("Engine/VFX/VfxFrame.shader");

		/// <summary>A vertex array without attributes for draws that build their vertices from gl_VertexID.</summary>
		public static int EmptyVao
		{
			get
			{
				if (emptyVao == 0)
				{
					emptyVao = GL.GenVertexArray();
				}

				return emptyVao;
			}
		}

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

		private sealed class Readback
		{
			public int Buffer;
			public int Bytes;
			public IntPtr Fence;
			public long Frame;
			public int Blocks;
		}

		private sealed class IndexBuffer
		{
			public int Vao;
			public int Buffer;
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
		private static int emptyVao;
		private static VfxFrameStats stats;

		// The counter pool: one block per system, a free list, grown by doubling.
		private static int counterBuffer;
		private static int counterBlocks;
		private static int counterHigh;
		private static readonly Stack<int> freeBlocks = new Stack<int>();
		private static VfxSystemState[] blockOwners = Array.Empty<VfxSystemState>();

		// The parameter blocks of every system, packed into one uniform buffer per frame.
		private static int paramsBuffer;
		private static float[] paramsStaging = new float[1024];
		private static int uniformAlignment;
		private static bool paramsDirty = true;

		// Frame kernel entries.
		private static int entryBuffer;
		private static uint[] entryStaging = new uint[256];
		private static readonly List<VfxSystemState> simulated = new List<VfxSystemState>();
		private static readonly List<VfxSystemState> layerScratch = new List<VfxSystemState>();
		private static int[] layerStarts = new int[4];

		// The readback ring of the counter pool (alive counts and bounds a few frames late, never waited for).
		private static readonly Readback[] readbacks = new Readback[ReadbackSlots];
		private static uint[] readbackData = new uint[256];

		// The per-view block of the draws (camera basis).
		private static int viewBuffer;
		private static readonly float[] viewData = new float[16];

		// Index buffers of the indexed outputs, per shape.
		private static readonly Dictionary<long, IndexBuffer> indexBuffers = new Dictionary<long, IndexBuffer>();

		// Draw entries reused every frame.
		private static readonly List<VfxDrawEntry> entryPool = new List<VfxDrawEntry>();
		private static int entryPoolUsed;
		private static readonly List<VfxDrawEntry> opaqueScratch = new List<VfxDrawEntry>();

		// Sorting: the jobs of the view and the uniform locations of the sort kernel.
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
			VfxProgramSet set;
			if (programs.TryGetValue(graph, out set) && set.GraphVersion == graph.Version)
			{
				return set;
			}

			set?.Dispose();
			set = Build(graph);
			programs[graph] = set;
			return set;
		}

		/// <summary>The last compiled programs of a graph without recompiling (editor status), or null.</summary>
		public static VfxProgramSet PeekPrograms(VfxGraph graph)
		{
			VfxProgramSet set;
			return programs.TryGetValue(graph, out set) ? set : null;
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
			GL.UseProgram(0);
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
			if (block >= counterBlocks || counterBuffer == 0)
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
			if (viewBuffer == 0)
			{
				viewBuffer = GL.GenBuffer();
			}

			GL.BindBuffer(BufferTarget.UniformBuffer, viewBuffer);
			GL.BufferData(BufferTarget.UniformBuffer, viewData.Length * sizeof(float), viewData, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.UniformBuffer, 0);
			GL.BindBufferBase(BufferRangeTarget.UniformBuffer, 2, viewBuffer);
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
		///     every sorted output of the view in one batch (their passes interleaved, one barrier per pass).
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
		///     The vertex array of an indexed output shape with an element buffer for at least the given particles: per
		///     particle <paramref name="vertices" /> vertices and the triangles of the shape (quad, triangle, octagon, or a
		///     strip of <paramref name="vertices" /> / 2 points).
		/// </summary>
		internal static int IndexedVao(VfxOutputDescription output, int particles)
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

			if (entry.Particles < particles || entry.Vao == 0)
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

				if (entry.Vao == 0)
				{
					entry.Vao = GL.GenVertexArray();
					entry.Buffer = GL.GenBuffer();
				}

				GL.BindVertexArray(entry.Vao);
				GL.BindBuffer(BufferTarget.ElementArrayBuffer, entry.Buffer);
				GL.BufferData(BufferTarget.ElementArrayBuffer, data.Length * sizeof(uint), data, BufferUsageHint.StaticDraw);
				GL.BindVertexArray(0);
				GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
				entry.Particles = count;
			}

			return entry.Vao;
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

			GL.CopyImageSubData(source.Handle, ImageTarget.Texture2D, 0, 0, 0, 0, capture.Depth.Handle, ImageTarget.Texture2D, 0, 0, 0, 0, source.Width, source.Height, 1);
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
				BindTexture(DepthTextureUnit, TextureTarget.Texture2D, capture.Depth.Handle);
				program.SetInt("vfxSceneDepth", DepthTextureUnit);
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
				if (buffer.Vao != 0) GL.DeleteVertexArray(buffer.Vao);
				if (buffer.Buffer != 0) GL.DeleteBuffer(buffer.Buffer);
			}

			indexBuffers.Clear();
			for (int i = 0; i < readbacks.Length; i++)
			{
				if (readbacks[i] != null)
				{
					if (readbacks[i].Fence != IntPtr.Zero) GL.DeleteSync(readbacks[i].Fence);
					if (readbacks[i].Buffer != 0) GL.DeleteBuffer(readbacks[i].Buffer);
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
			if (uniformAlignment == 0)
			{
				uniformAlignment = Math.Max(16, GL.GetInteger(GetPName.UniformBufferOffsetAlignment));
			}

			int alignFloats = uniformAlignment / sizeof(float);
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

			if (paramsBuffer == 0)
			{
				paramsBuffer = GL.GenBuffer();
			}

			GL.BindBuffer(BufferTarget.UniformBuffer, paramsBuffer);
			GL.BufferData(BufferTarget.UniformBuffer, cursor * sizeof(float), paramsStaging, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.UniformBuffer, 0);
		}

		/// <summary>Binds a system's parameter block (its range of the shared buffer).</summary>
		internal static void BindParameters(VfxSystemState state)
		{
			if (paramsBuffer != 0 && state.UboBytes > 0 && (boundParamsOffset != state.UboOffset || boundParamsBytes != state.UboBytes))
			{
				GL.BindBufferRange(BufferRangeTarget.UniformBuffer, 1, paramsBuffer, (IntPtr)state.UboOffset, state.UboBytes);
				boundParamsOffset = state.UboOffset;
				boundParamsBytes = state.UboBytes;
			}
		}

		// ---------------------------------------------------------------- binding cache

		// What the particle draws and kernels bound last, so consecutive ones skip unchanged bindings. Forgotten
		// whenever other rendering may have changed them (frame start, a new view, a mesh draw in between).
		private static readonly int[] boundStorage = new int[16];
		private static readonly int[] boundTextures = new int[16];
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
			public bool SamplersSet;
		}

		/// <summary>Forgets the bindings and render state particle draws assume (other rendering ran since).</summary>
		public static void InvalidateDrawState()
		{
			Array.Fill(boundStorage, -1);
			Array.Fill(boundTextures, -1);
			boundParamsOffset = -1;
			boundParamsBytes = -1;
			lastBlend = -1;
			lastDepthWrite = -1;
			lastCull = -1;
		}

		/// <summary>Binds a storage buffer unless it is bound there already.</summary>
		internal static void BindStorage(int binding, int buffer)
		{
			if (binding < boundStorage.Length && boundStorage[binding] == buffer)
			{
				return;
			}

			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, binding, buffer);
			if (binding < boundStorage.Length)
			{
				boundStorage[binding] = buffer;
			}
		}

		/// <summary>Binds a texture to a unit unless it is bound there already.</summary>
		internal static void BindTexture(int unit, TextureTarget target, int handle)
		{
			if (unit < boundTextures.Length && boundTextures[unit] == handle)
			{
				return;
			}

			GL.ActiveTexture(TextureUnit.Texture0 + unit);
			GL.BindTexture(target, handle);
			if (unit < boundTextures.Length)
			{
				boundTextures[unit] = handle;
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
			if (blend != lastBlend)
			{
				switch (blend)
				{
					case 1:
						GL.Enable(EnableCap.Blend);
						GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
						break;
					case 2:
						GL.Enable(EnableCap.Blend);
						GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
						break;
					case 3:
						GL.Enable(EnableCap.Blend);
						GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
						break;
					default:
						GL.Disable(EnableCap.Blend);
						break;
				}

				lastBlend = blend;
			}

			int write = depthWrite ? 1 : 0;
			if (write != lastDepthWrite)
			{
				GL.Enable(EnableCap.DepthTest);
				GL.DepthFunc(DepthFunction.Lequal);
				GL.DepthMask(depthWrite);
				lastDepthWrite = write;
			}

			int cull = cullBack ? 1 : 0;
			if (cull != lastCull)
			{
				if (cullBack)
				{
					GL.Enable(EnableCap.CullFace);
					GL.CullFace(TriangleFace.Back);
				}
				else
				{
					GL.Disable(EnableCap.CullFace);
				}

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
			if (entryBuffer == 0)
			{
				entryBuffer = GL.GenBuffer();
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, entryBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, write * sizeof(uint), entryStaging, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			BindStorage(4, counterBuffer);
			BindStorage(11, entryBuffer);

			ComputeShader frame = FrameKernel;
			frame.Use();
			frame.Program.SetInt("vfxMode", 0);
			frame.Program.SetUint("vfxEntryOffset", 0u);
			frame.Program.SetUint("vfxEntryCount", (uint)entries);
			frame.Dispatch((entries + 63) / 64);
			stats.Dispatches++;
			Barrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.CommandBarrierBit);

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
						Barrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.CommandBarrierBit);
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

					ShaderProgram program = state.Programs.Initialize.Program;
					if (program != current)
					{
						program.Use();
						current = program;
					}

					state.Instance.BindForKernel(state, program, false);
					state.Programs.Initialize.DispatchIndirect(counterBuffer, (state.CounterBase + VfxCodegen.CounterInitDispatch) * sizeof(uint));
					stats.Dispatches++;
					anyInit = true;
				}

				if (anyInit)
				{
					Barrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
				}

				current = null;
				foreach (VfxSystemState state in layerScratch)
				{
					ShaderProgram program = state.Programs.Update.Program;
					if (program != current)
					{
						program.Use();
						current = program;
					}

					state.Instance.BindForKernel(state, program, true);
					if (state.Dense)
					{
						state.Programs.Update.Dispatch((state.Description.Capacity + VfxCodegen.GroupSize - 1) / VfxCodegen.GroupSize);
					}
					else
					{
						state.Programs.Update.DispatchIndirect(counterBuffer, (state.CounterBase + VfxCodegen.CounterUpdateDispatch) * sizeof(uint));
					}
					stats.Dispatches++;
				}

				MemoryBarrierFlags flags = MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.CommandBarrierBit;
				if (layer == maxLayer)
				{
					flags |= MemoryBarrierFlags.BufferUpdateBarrierBit | MemoryBarrierFlags.ElementArrayBarrierBit;
				}

				Barrier(flags);
			}

			foreach (VfxSystemState state in simulated)
			{
				state.Instance.AfterDispatch(state, Frame);
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
		}

		private static int CompareByProgram(VfxSystemState a, VfxSystemState b)
		{
			return a.ProgramOrder.CompareTo(b.ProgramOrder);
		}

		private static void Barrier(MemoryBarrierFlags flags)
		{
			GL.MemoryBarrier(flags);
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
				if (readbacks[i].Fence == IntPtr.Zero)
				{
					free = readbacks[i];
					break;
				}
			}

			if (free == null)
			{
				return;
			}

			if (free.Buffer == 0 || free.Bytes < bytes)
			{
				if (free.Buffer != 0)
				{
					GL.DeleteBuffer(free.Buffer);
				}

				free.Buffer = GL.GenBuffer();
				free.Bytes = Math.Max(bytes, 4096);
				GL.BindBuffer(BufferTarget.CopyWriteBuffer, free.Buffer);
				GL.BufferData(BufferTarget.CopyWriteBuffer, free.Bytes, IntPtr.Zero, BufferUsageHint.StreamRead);
			}

			GL.BindBuffer(BufferTarget.CopyReadBuffer, counterBuffer);
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, free.Buffer);
			GL.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, IntPtr.Zero, bytes);
			GL.BindBuffer(BufferTarget.CopyReadBuffer, 0);
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
			free.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
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
				if (slot == null || slot.Fence == IntPtr.Zero)
				{
					continue;
				}

				WaitSyncStatus status = GL.ClientWaitSync(slot.Fence, ClientWaitSyncFlags.None, 0);
				if (status != WaitSyncStatus.AlreadySignaled && status != WaitSyncStatus.ConditionSatisfied)
				{
					continue;
				}

				if (newest == null || slot.Frame > newest.Frame)
				{
					if (newest != null)
					{
						GL.DeleteSync(newest.Fence);
						newest.Fence = IntPtr.Zero;
					}

					newest = slot;
				}
				else
				{
					GL.DeleteSync(slot.Fence);
					slot.Fence = IntPtr.Zero;
				}
			}

			if (newest == null)
			{
				return;
			}

			GL.DeleteSync(newest.Fence);
			newest.Fence = IntPtr.Zero;
			int uints = newest.Blocks * VfxCodegen.CounterBlock;
			if (readbackData.Length < uints)
			{
				Array.Resize(ref readbackData, Math.Max(uints, readbackData.Length * 2));
			}

			GL.BindBuffer(BufferTarget.CopyReadBuffer, newest.Buffer);
			GL.GetBufferSubData(BufferTarget.CopyReadBuffer, IntPtr.Zero, uints * sizeof(uint), readbackData);
			GL.BindBuffer(BufferTarget.CopyReadBuffer, 0);
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
			int buffer = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, blocks * VfxCodegen.CounterBlock * sizeof(uint), IntPtr.Zero, BufferUsageHint.DynamicCopy);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			if (counterBuffer != 0)
			{
				GL.BindBuffer(BufferTarget.CopyReadBuffer, counterBuffer);
				GL.BindBuffer(BufferTarget.CopyWriteBuffer, buffer);
				GL.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, IntPtr.Zero, counterBlocks * VfxCodegen.CounterBlock * sizeof(uint));
				GL.BindBuffer(BufferTarget.CopyReadBuffer, 0);
				GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
				GL.DeleteBuffer(counterBuffer);
			}

			counterBuffer = buffer;
			counterBlocks = blocks;
			Array.Resize(ref blockOwners, blocks);
		}

		// ---------------------------------------------------------------- sorting

		/// <summary>The sort passes of every job of the view, interleaved: pass r of every job, then one barrier.</summary>
		private static void RunSorts()
		{
			ComputeShader kernel = Sort;
			kernel.Use();
			ShaderProgram program = kernel.Program;
			if (sortMode == -2)
			{
				sortMode = program.GetUniformLocation("vfxSortMode");
				sortCount = program.GetUniformLocation("vfxSortCount");
				sortK = program.GetUniformLocation("vfxSortK");
				sortJ = program.GetUniformLocation("vfxSortJ");
				sortAliveCounter = program.GetUniformLocation("vfxSortAliveCounter");
				sortPositionPlane = program.GetUniformLocation("vfxSortPositionPlane");
				sortCapacity = program.GetUniformLocation("vfxSortCapacity");
				sortWorldSpace = program.GetUniformLocation("vfxSortWorldSpace");
				sortLocalToWorld = program.GetUniformLocation("vfxSortLocalToWorld");
				sortViewPosition = program.GetUniformLocation("vfxSortViewPosition");
				sortViewForward = program.GetUniformLocation("vfxSortViewForward");
				sortIndexBits = program.GetUniformLocation("vfxSortIndexBits");
				sortDepthRange = program.GetUniformLocation("vfxSortDepthRange");
			}

			GL.Uniform3(sortViewPosition, viewPosition);
			GL.Uniform3(sortViewForward, viewForward);
			BindStorage(4, counterBuffer);

			// Pass 0 of every job: keys and the sort of every chunk.
			int rounds = 0;
			foreach (VfxDrawEntry job in sortJobs)
			{
				VfxSystemState state = job.State;
				int n = job.SortCount;
				state.Instance.BindForSort(state, job.Output);
				GL.Uniform1(sortMode, 0);
				GL.Uniform1(sortCount, (uint)n);
				GL.Uniform1(sortAliveCounter, (uint)(state.CounterBase + VfxCodegen.CounterAliveOut));
				GL.Uniform1(sortPositionPlane, (uint)Math.Max(0, state.PositionPlane));
				GL.Uniform1(sortCapacity, (uint)state.Description.Capacity);
				GL.Uniform1(sortIndexBits, (uint)state.SortIndexBits);
				GL.Uniform2(sortDepthRange, job.SortDepthNear, job.SortDepthScale);
				GL.Uniform1(sortWorldSpace, state.Description.InitWriter.WorldSpace ? 1 : 0);
				Matrix4 localToWorld = state.Instance.LocalToWorld;
				GL.UniformMatrix4(sortLocalToWorld, false, ref localToWorld);
				kernel.Dispatch(Math.Max(1, n / SortChunk));
				stats.SortDispatches++;
				rounds = Math.Max(rounds, SortPassCount(n));
			}

			stats.Sorts += sortJobs.Count;
			Barrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

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
					GL.Uniform1(sortMode, mode);
					GL.Uniform1(sortCount, (uint)n);
					GL.Uniform1(sortK, (uint)k);
					GL.Uniform1(sortJ, (uint)j);
					kernel.Dispatch(mode == 1 ? Math.Max(1, n / 2 / SortThreads) : mode == 3 ? Math.Max(1, n / 4 / SortThreads) : Math.Max(1, n / SortChunk));
					stats.SortDispatches++;
				}

				Barrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
			}

			GL.UseProgram(0);
			if (CheckSorts && Frame % 120 == 0)
			{
				ValidateSorts();
			}
		}

		/// <summary>Validate every sort of the view on the CPU (environment LEL_VFX_CHECKSORT=1; stalls, debugging only).</summary>
		public static bool CheckSorts = Environment.GetEnvironmentVariable("LEL_VFX_CHECKSORT") == "1";

		private static void ValidateSorts()
		{
			GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);
			GL.Finish();
			var counters = new uint[VfxCodegen.CounterBlock];
			foreach (VfxDrawEntry job in sortJobs)
			{
				VfxSystemState state = job.State;
				int n = job.SortCount;
				var keys = new uint[n];
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, state.SortKeys[job.Output]);
				GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, n * sizeof(uint), keys);
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, counterBuffer);
				GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)(state.CounterBase * sizeof(uint)), counters.Length * sizeof(uint), counters);
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
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

		private static void DeleteBuffer(ref int buffer)
		{
			if (buffer != 0)
			{
				GL.DeleteBuffer(buffer);
				buffer = 0;
			}
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

			foreach (VfxSystemDescription system in set.Compiled.Systems)
			{
				var programsOfSystem = new VfxProgramSet.SystemPrograms();
				programsOfSystem.Initialize = Compute(set, system.Name + ".Initialize", system.InitSource);
				programsOfSystem.Update = Compute(set, system.Name + ".Update", system.UpdateSource);
				foreach (VfxOutputDescription output in system.Outputs)
				{
					var outputPrograms = new VfxProgramSet.OutputPrograms();
					string name = system.Name + "." + output.Name;
					outputPrograms.Draw = Graphics(set, name, output.VertexSource, output.FragmentSource, Array.Empty<string>());
					if (output.CastShadows)
					{
						outputPrograms.Shadow = Graphics(set, name + ".Shadow", output.VertexSource, output.FragmentSource, new[] { "VFX_SHADOW_PASS" });
					}

					programsOfSystem.Outputs.Add(outputPrograms);
				}

				set.Systems.Add(programsOfSystem);
			}

			return set;
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

		private static ComputeShader Compute(VfxProgramSet set, string name, string source)
		{
			ShaderProgram program = ShaderProgram.FromSources("VFX/" + name, new[] { new KeyValuePair<ShaderType, string>(ShaderType.ComputeShader, source) });
			if (!program.IsLinked)
			{
				set.ShaderErrors.Add(name + ": " + program.BuildLog);
				program.Delete();
				return null;
			}

			return ComputeShader.FromProgram(program);
		}

		private static ShaderProgram Graphics(VfxProgramSet set, string name, string vertex, string fragment, string[] defines)
		{
			ShaderProgram program = ShaderProgram.FromSources("VFX/" + name, new[]
			{
				new KeyValuePair<ShaderType, string>(ShaderType.VertexShader, vertex),
				new KeyValuePair<ShaderType, string>(ShaderType.FragmentShader, fragment)
			}, defines);
			if (!program.IsLinked)
			{
				set.ShaderErrors.Add(name + ": " + program.BuildLog);
				program.Delete();
				return null;
			}

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

		/// <summary>The system drawn, and the length of its sort for this view (a power of two at least its alive count).</summary>
		internal VfxSystemState State;

		internal int SortCount;

		/// <summary>The depth range the sort keys of this view span (nearest depth, 1 / span).</summary>
		internal float SortDepthNear;

		internal float SortDepthScale;
	}
}
