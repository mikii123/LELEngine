using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     One particle system of a playing instance: its GPU buffers (attributes, the two alive lists, the dead stack,
	///     curves, strips, GPU event payloads, sort keys), its block in the runtime's counter pool, its parameter block,
	///     its spawners and what the CPU knows about it (alive count and bounds from the readback, an upper bound of
	///     the alive particles from the spawns since).
	/// </summary>
	internal sealed class VfxSystemState
	{
		#region PublicFields

		public VfxInstance Instance;
		public int Index;
		public VfxSystemDescription Description;
		public VfxProgramSet.SystemPrograms Programs;

		/// <summary>The system whose GPU events spawn this one (null for CPU-spawned systems).</summary>
		public VfxSystemState Parent;

		/// <summary>GPU-event depth: 0 for CPU-spawned systems, the parent's layer + 1 otherwise.</summary>
		public int Layer;

		/// <summary>Orders the systems of a dispatch phase so instances of one graph run back to back.</summary>
		public int ProgramOrder;

		/// <summary>First attribute plane of the position (the generic sort kernel reads it).</summary>
		public int PositionPlane = -1;

		public int Attributes;
		public int AliveA;
		public int AliveB;
		public int Dead;
		public int Curves;
		public int Strip;
		public int AliveMask;

		/// <summary>This frame the update walks every slot (most are alive) instead of the alive list.</summary>
		public bool Dense;
		public readonly int[] EventOut = new int[VfxCompiler.MaxEventsPerSystem];
		public int[] SortKeys;
		public int SortLength;

		/// <summary>Low bits of a sort key that hold the particle index (2 ^ bits is above the capacity).</summary>
		public int SortIndexBits;
		public int CounterBlock = -1;

		public float[] UboData;
		public int UboOffset;
		public int UboBytes;
		public int StaticVersion = -1;

		public readonly List<VfxSpawnerState> Spawners = new List<VfxSpawnerState>();
		public uint SpawnedTotal;
		public uint SystemSeed;
		public bool CurrentIsA = true;
		public int CurvesVersion = -1;
		public int[] MeshRanges;

		// This frame.
		public uint SpawnRequest;
		public uint FrameFlags;
		public bool NeedsInit;
		public bool SpawnsFromParent;
		public bool Simulating;

		// Readback.
		public int AliveCount;
		public Vector3 BoundsMin;
		public Vector3 BoundsMax;
		public bool BoundsValid;
		public long ValidFromFrame;
		public long ReadbackFrame = -1;

		public int CounterBase => CounterBlock * VfxCodegen.CounterBlock;

		/// <summary>The list the last update wrote: this frame's draw list and the next update's input.</summary>
		public int Current => CurrentIsA ? AliveA : AliveB;

		public int Other => CurrentIsA ? AliveB : AliveA;

		/// <summary>
		///     At least the particles alive on the GPU right now: the last readback's count plus everything spawned
		///     since. Zero means the system is empty (nothing to simulate, draw or sort).
		/// </summary>
		public int AliveUpperBound { get; private set; }

		#endregion

		#region PrivateFields

		private const int PendingFrames = 32;
		private readonly long[] pendingFrames = new long[PendingFrames];
		private readonly uint[] pendingSpawns = new uint[PendingFrames];
		private int pendingCount;
		private uint aliveAtReadback;

		#endregion

		#region PublicMethods

		/// <summary>Forgets what the CPU knew (buffers emptied); readbacks older than <paramref name="validFrom" /> are ignored.</summary>
		public void ResetTracking(long validFrom)
		{
			ValidFromFrame = validFrom;
			ReadbackFrame = validFrom - 1;
			pendingCount = 0;
			aliveAtReadback = 0;
			AliveUpperBound = 0;
			AliveCount = 0;
			if (Description.BoundsMode == VfxBoundsMode.Automatic)
			{
				BoundsValid = false;
			}
		}

		/// <summary>Records how many particles a simulated frame may have added (requested spawns; the capacity for GPU-event children).</summary>
		public void RecordSpawn(long frame, uint spawned)
		{
			if (spawned == 0)
			{
				return;
			}

			if (pendingCount == PendingFrames)
			{
				// Too long without a readback: merge the two oldest frames (the sum stays an upper bound).
				pendingSpawns[1] = (uint)Math.Min((ulong)pendingSpawns[0] + pendingSpawns[1], uint.MaxValue);
				Array.Copy(pendingFrames, 1, pendingFrames, 0, PendingFrames - 1);
				Array.Copy(pendingSpawns, 1, pendingSpawns, 0, PendingFrames - 1);
				pendingCount--;
			}

			pendingFrames[pendingCount] = frame;
			pendingSpawns[pendingCount] = spawned;
			pendingCount++;
			UpdateUpperBound();
		}

		/// <summary>The counters of the frame <paramref name="frame" /> (after its update) arrived: alive count and bounds.</summary>
		public void ApplyReadback(long frame, uint[] data, int offset)
		{
			if (frame <= ReadbackFrame)
			{
				return;
			}

			ReadbackFrame = frame;
			uint capacity = (uint)Description.Capacity;
			uint alive = Math.Min(data[offset + VfxCodegen.CounterAliveOut], capacity);
			AliveCount = (int)alive;
			aliveAtReadback = alive;
			int keep = 0;
			for (int i = 0; i < pendingCount; i++)
			{
				if (pendingFrames[i] > frame)
				{
					pendingFrames[keep] = pendingFrames[i];
					pendingSpawns[keep] = pendingSpawns[i];
					keep++;
				}
			}

			pendingCount = keep;
			UpdateUpperBound();
			if (Description.BoundsMode == VfxBoundsMode.Automatic)
			{
				int b = offset + VfxCodegen.CounterBounds;
				if (alive > 0 && data[b] != 0xFFFFFFFFu)
				{
					var min = new Vector3(OrderedFloat(data[b]), OrderedFloat(data[b + 1]), OrderedFloat(data[b + 2]));
					var max = new Vector3(OrderedFloat(data[b + 3]), OrderedFloat(data[b + 4]), OrderedFloat(data[b + 5]));
					// A margin for the particles' size and the frames of delay.
					Vector3 margin = (max - min) * 0.1f + new Vector3(0.5f);
					BoundsMin = min - margin;
					BoundsMax = max + margin;
					BoundsValid = true;
				}
				else if (AliveUpperBound == 0)
				{
					BoundsValid = false;
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void UpdateUpperBound()
		{
			ulong sum = aliveAtReadback;
			for (int i = 0; i < pendingCount; i++)
			{
				sum += pendingSpawns[i];
			}

			AliveUpperBound = (int)Math.Min(sum, (ulong)Description.Capacity);
		}

		private static float OrderedFloat(uint u)
		{
			return BitConverter.UInt32BitsToSingle((u & 0x80000000u) != 0 ? u & 0x7FFFFFFFu : ~u);
		}

		#endregion
	}

	/// <summary>
	///     A playing copy of a graph: the GPU state of its systems (<see cref="VfxSystemState" />), the CPU spawners,
	///     the parameter blocks and the property overrides. <see cref="Simulate" /> queues a frame; the runtime runs
	///     the frames of every instance together (<see cref="VfxRuntime.RunSimulation" />) and the renderer's passes draw
	///     the outputs through <see cref="CollectDraws" /> and <see cref="Draw" />. Systems with no particles and
	///     nothing to spawn cost nothing, and with <see cref="VfxCullingMode.SimulateWhenVisible" /> neither does an
	///     effect no view sees.
	/// </summary>
	public sealed class VfxInstance : IDisposable
	{
		#region PublicFields

		public VfxGraph Graph { get; private set; }
		public string Name = "VisualEffect";

		/// <summary>The component's transform (set before <see cref="Simulate" />).</summary>
		public Matrix4 LocalToWorld = Matrix4.Identity;

		/// <summary>The component's layer: the cameras whose culling mask has it draw the effect (and its shadows count with the lighting camera's).</summary>
		public int Layer;

		public bool Playing { get; private set; }
		public bool Paused;
		public float PlayRate = 1f;

		/// <summary>Seconds the effect has played (scaled by the play rate).</summary>
		public float Time { get; private set; }

		public uint FrameIndex { get; private set; }

		/// <summary>Random seed; <see cref="ResetSeedOnPlay" /> re-draws it on every play.</summary>
		public uint Seed;

		public bool ResetSeedOnPlay = true;

		/// <summary>Enabled and playing (or still showing particles): the editor keeps rendering.</summary>
		public bool IsActive => enabled && (Playing && !Paused || AliveCount > 0);

		/// <summary>Alive particles after the last readback (a few frames behind).</summary>
		public int AliveCount { get; private set; }

		/// <summary>Every system's capacity summed.</summary>
		public int Capacity { get; private set; }

		/// <summary>Every output of every system was outside the last view drawn.</summary>
		public bool Culled { get; private set; }

		/// <summary>The simulation is frozen because no view saw the effect (<see cref="VfxCullingMode.SimulateWhenVisible" />).</summary>
		public bool Frozen { get; private set; }

		/// <summary>Some kernel reads the depth buffer (collisions, soft particles).</summary>
		public bool NeedsDepth { get; private set; }

		/// <summary>Some output casts shadows.</summary>
		public bool HasShadowCasters { get; private set; }

		/// <summary>The compiled programs in use (null until the first simulation, or when the graph does not compile).</summary>
		public VfxProgramSet Programs => programs;

		/// <summary>Bumped when a property override changes (curves and textures re-upload).</summary>
		public int PropertyVersion { get; private set; } = 1;

		internal List<VfxSystemState> SystemStates => systems;

		#endregion

		#region PrivateFields

		private readonly List<VfxSystemState> systems = new List<VfxSystemState>();
		private readonly Dictionary<string, VfxValue> overrides = new Dictionary<string, VfxValue>(StringComparer.Ordinal);
		private readonly uint[] counterInit = new uint[VfxCodegen.CounterBlock];
		private readonly VfxEvalContext context = new VfxEvalContext();
		private VfxProgramSet programs;
		private int builtVersion = -1;
		private bool enabled;
		private bool disposed;
		private Vector3 lastPosition;
		private bool hasLastPosition;
		private bool pendingPlay;
		private float pendingDelta;
		private bool simulationQueued;
		private long lastVisibleFrame = long.MaxValue / 2;

		#endregion

		#region Constructors

		public VfxInstance(VfxGraph graph)
		{
			Graph = graph;
			Seed = (uint)Random.Shared.Next();
			context.PropertyLookup = LookupProperty;
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- lifecycle

		/// <summary>Registers the instance with the runtime (drawn and simulated).</summary>
		public void Enable()
		{
			if (disposed || enabled)
			{
				return;
			}

			enabled = true;
			lastVisibleFrame = VfxRuntime.Frame;
			VfxRuntime.Register(this);
		}

		public void Disable()
		{
			if (!enabled)
			{
				return;
			}

			enabled = false;
			simulationQueued = false;
			VfxRuntime.Unregister(this);
		}

		/// <summary>Swaps the graph (a new asset assigned); the buffers rebuild on the next simulation.</summary>
		public void SetGraph(VfxGraph graph)
		{
			if (graph == Graph)
			{
				return;
			}

			Graph = graph;
			ReleaseSystems();
			programs = null;
			builtVersion = -1;
		}

		/// <summary>Starts the spawners listening to OnPlay (and raises OnPlay).</summary>
		public void Play()
		{
			if (ResetSeedOnPlay)
			{
				Seed = (uint)Random.Shared.Next();
			}

			Playing = true;
			pendingPlay = true;
			lastVisibleFrame = VfxRuntime.Frame;
			SendEvent(VfxCompiler.OnPlayEvent);
		}

		/// <summary>Stops the spawners (raises OnStop); alive particles finish their lives.</summary>
		public void Stop()
		{
			Playing = false;
			SendEvent(VfxCompiler.OnStopEvent);
		}

		/// <summary>Kills every particle, restarts the clock and plays again when it was playing.</summary>
		public void Reinit()
		{
			Time = 0f;
			FrameIndex = 0;
			hasLastPosition = false;
			foreach (VfxSystemState state in systems)
			{
				ResetBuffers(state, VfxRuntime.Frame + 1);
				foreach (VfxSpawnerState spawner in state.Spawners)
				{
					spawner.Reset();
				}
			}

			AliveCount = 0;
			if (Playing)
			{
				Play();
			}
		}

		/// <summary>Raises a named event: spawners that start or stop on it react (OnPlay, OnStop, custom names).</summary>
		public void SendEvent(string eventName)
		{
			if (string.IsNullOrEmpty(eventName))
			{
				return;
			}

			if (systems.Count == 0)
			{
				// Not built yet: the spawners start when the buffers exist.
				pendingPlay |= eventName == VfxCompiler.OnPlayEvent;
				return;
			}

			foreach (VfxSystemState state in systems)
			{
				foreach (VfxSpawnerState spawner in state.Spawners)
				{
					spawner.OnEvent(eventName);
				}
			}
		}

		// ---------------------------------------------------------------- properties

		public bool HasProperty(string propertyName)
		{
			return Graph?.FindProperty(propertyName) != null;
		}

		/// <summary>The value a property has for this instance: the override, else the graph's default.</summary>
		public VfxValue GetProperty(string propertyName)
		{
			VfxValue value;
			if (overrides.TryGetValue(propertyName, out value))
			{
				return value;
			}

			return Graph?.FindProperty(propertyName)?.Value;
		}

		/// <summary>Overrides a property for this instance (null removes the override).</summary>
		public void SetProperty(string propertyName, VfxValue value)
		{
			if (value == null)
			{
				overrides.Remove(propertyName);
			}
			else
			{
				overrides[propertyName] = value;
			}

			PropertyVersion++;
		}

		public void SetFloat(string propertyName, float value) => SetProperty(propertyName, VfxValue.FromFloat(value));
		public void SetInt(string propertyName, int value) => SetProperty(propertyName, VfxValue.FromInt(value));
		public void SetBool(string propertyName, bool value) => SetProperty(propertyName, VfxValue.FromBool(value));
		public void SetVector2(string propertyName, Vector2 value) => SetProperty(propertyName, VfxValue.FromVector2(value));
		public void SetVector3(string propertyName, Vector3 value) => SetProperty(propertyName, VfxValue.FromVector3(value));
		public void SetVector4(string propertyName, Vector4 value) => SetProperty(propertyName, VfxValue.FromVector4(value));
		public void SetColor(string propertyName, Color4 value) => SetProperty(propertyName, VfxValue.FromColor(value));
		public void SetTexture(string propertyName, Texture2D value) => SetProperty(propertyName, VfxValue.FromTexture(value));
		public void SetGradient(string propertyName, VfxGradient value) => SetProperty(propertyName, VfxValue.FromGradient(value));
		public void SetCurve(string propertyName, VfxCurve value) => SetProperty(propertyName, VfxValue.FromCurve(value));
		public void SetMesh(string propertyName, Mesh value) => SetProperty(propertyName, VfxValue.FromMesh(value));

		/// <summary>Replaces every override (the component's serialized list).</summary>
		public void SetOverrides(IEnumerable<VfxPropertyOverride> list)
		{
			overrides.Clear();
			if (list != null)
			{
				foreach (VfxPropertyOverride entry in list)
				{
					if (entry != null && entry.Enabled && !string.IsNullOrEmpty(entry.Name) && entry.Value != null)
					{
						overrides[entry.Name] = entry.Value;
					}
				}
			}

			PropertyVersion++;
		}

		// ---------------------------------------------------------------- simulation

		/// <summary>Queues one frame of every system; the renderer's simulation pass runs it (<see cref="VfxRuntime.RunSimulation" />).</summary>
		public void Simulate(float deltaTime)
		{
			if (disposed || !enabled)
			{
				return;
			}

			if (simulationQueued)
			{
				// Two updates without a render: keep the time of both.
				pendingDelta += deltaTime;
				return;
			}

			pendingDelta = deltaTime;
			simulationQueued = true;
			VfxRuntime.QueueSimulation(this);
		}

		// ---------------------------------------------------------------- drawing

		/// <summary>The effect's layer is in the view's culling mask.</summary>
		private bool OnLayers(int layerMask)
		{
			return (layerMask & (1 << (Layer & 31))) != 0;
		}

		/// <summary>The outputs of the given kind visible in the view, for the renderer's passes.</summary>
		public void CollectDraws(RenderContext renderContext, List<VfxDrawEntry> entries, bool transparent)
		{
			if (disposed || !enabled || programs == null || programs.HasErrors || !OnLayers(renderContext.CullingMask))
			{
				return;
			}

			Vector4[] planes = VfxRuntime.Frustum(renderContext.ViewProjection);
			bool anyVisible = false;
			for (int s = 0; s < systems.Count; s++)
			{
				VfxSystemState state = systems[s];
				Vector3 min;
				Vector3 max;
				bool bounded = WorldBounds(state, out min, out max);
				if (bounded && !VfxRuntime.BoxInFrustum(planes, min, max))
				{
					continue;
				}

				anyVisible = true;
				if (state.AliveUpperBound == 0)
				{
					continue;
				}

				Vector3 center = bounded ? (min + max) * 0.5f : LocalToWorld.ExtractTranslation();
				float distance = (center - renderContext.CameraPosition).LengthSquared;
				for (int o = 0; o < state.Description.Outputs.Count && o < VfxCodegen.MaxOutputs; o++)
				{
					VfxOutputDescription output = state.Description.Outputs[o];
					if (output.IsTransparent != transparent || state.Programs.Outputs[o].Draw == null)
					{
						continue;
					}

					VfxDrawEntry entry = VfxRuntime.RentEntry();
					entry.Instance = this;
					entry.System = s;
					entry.Output = o;
					entry.Queue = output.Queue;
					entry.DistanceSquared = distance;
					entry.State = state;
					entry.SortCount = 0;
					entries.Add(entry);
				}
			}

			if (anyVisible)
			{
				lastVisibleFrame = VfxRuntime.Frame;
			}

			Culled = !anyVisible;
		}

		/// <summary>Draws one output of one system into the bound target with the view of the context.</summary>
		public void Draw(RenderContext renderContext, int systemIndex, int outputIndex, bool shadowPass, VfxDepthCapture depth)
		{
			if (disposed || systemIndex >= systems.Count)
			{
				return;
			}

			VfxSystemState state = systems[systemIndex];
			VfxOutputDescription output = state.Description.Outputs[outputIndex];
			VfxProgramSet.OutputPrograms outputPrograms = state.Programs.Outputs[outputIndex];
			ShaderProgram program = shadowPass ? outputPrograms.Shadow : outputPrograms.Draw;
			if (program == null)
			{
				return;
			}

			DrawOutput(renderContext, state, output, program, shadowPass, renderContext.View, renderContext.Projection, depth);
		}

		/// <summary>Draws the shadow-casting outputs into the bound shadow map (the light's view, culled by its frustum).</summary>
		public void DrawShadows(RenderContext renderContext, Matrix4 lightView, Matrix4 lightProjection)
		{
			if (disposed || !enabled || programs == null || programs.HasErrors || !HasShadowCasters || !OnLayers(renderContext.CullingMask))
			{
				return;
			}

			Vector4[] planes = VfxRuntime.Frustum(lightView * lightProjection);
			foreach (VfxSystemState state in systems)
			{
				if (state.AliveUpperBound == 0)
				{
					continue;
				}

				Vector3 min;
				Vector3 max;
				if (WorldBounds(state, out min, out max) && !VfxRuntime.BoxInFrustum(planes, min, max))
				{
					continue;
				}

				for (int o = 0; o < state.Description.Outputs.Count && o < VfxCodegen.MaxOutputs; o++)
				{
					VfxOutputDescription output = state.Description.Outputs[o];
					VfxProgramSet.OutputPrograms outputPrograms = state.Programs.Outputs[o];
					if (!output.CastShadows || outputPrograms.Shadow == null)
					{
						continue;
					}

					DrawOutput(renderContext, state, output, outputPrograms.Shadow, true, lightView, lightProjection, null);
				}
			}
		}

		/// <summary>World-space bounds of one system (false when they are not known yet).</summary>
		internal bool TryGetSystemBounds(int systemIndex, out Vector3 min, out Vector3 max)
		{
			if (systemIndex < 0 || systemIndex >= systems.Count)
			{
				min = max = Vector3.Zero;
				return false;
			}

			return WorldBounds(systems[systemIndex], out min, out max);
		}

		/// <summary>World-space bounds of every system (false when none is known yet).</summary>
		public bool GetWorldBounds(out Vector3 min, out Vector3 max)
		{
			min = new Vector3(float.MaxValue);
			max = new Vector3(float.MinValue);
			bool any = false;
			foreach (VfxSystemState state in systems)
			{
				Vector3 a;
				Vector3 b;
				if (WorldBounds(state, out a, out b))
				{
					min = Vector3.ComponentMin(min, a);
					max = Vector3.ComponentMax(max, b);
					any = true;
				}
			}

			return any;
		}

		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			Disable();
			disposed = true;
			ReleaseSystems();
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     The CPU half of a queued frame (the runtime calls it at the start of the frame's GPU work): the spawners,
		///     the parameter blocks, and which systems need the GPU this frame (added to <paramref name="simulated" />).
		/// </summary>
		internal void PrepareFrame(long frame, List<VfxSystemState> simulated)
		{
			if (!simulationQueued)
			{
				return;
			}

			simulationQueued = false;
			float deltaTime = pendingDelta;
			pendingDelta = 0f;
			if (disposed || !enabled || !VfxRuntime.Available || Graph == null || !EnsureBuilt(frame))
			{
				return;
			}

			if (pendingPlay)
			{
				pendingPlay = false;
				foreach (VfxSystemState state in systems)
				{
					foreach (VfxSpawnerState spawner in state.Spawners)
					{
						spawner.OnEvent(VfxCompiler.OnPlayEvent);
					}
				}
			}

			// Off-screen effects freeze (Unity's "simulate when visible"): a frame of grace after the last view that saw them.
			Frozen = Graph.CullingMode == VfxCullingMode.SimulateWhenVisible && frame - lastVisibleFrame > 2;
			if (Paused || Frozen)
			{
				return;
			}

			deltaTime = Math.Min(Math.Max(0f, deltaTime) * Math.Max(0f, PlayRate), 0.1f);
			Time += deltaTime;
			FrameIndex++;
			if (VfxRuntime.DebugCounters && frame % 60 == 0)
			{
				Debug.Log($"[VFX] {Name} frame {frame}: dt {deltaTime:0.0000} time {Time:0.00} frozen {Frozen} last visible {lastVisibleFrame}");
			}

			Vector3 position = LocalToWorld.ExtractTranslation();
			float moved = hasLastPosition ? (position - lastPosition).Length : 0f;
			lastPosition = position;
			hasLastPosition = true;

			context.DeltaTime = deltaTime;
			context.TotalTime = Time;
			context.FrameIndex = FrameIndex;
			context.PlayRate = PlayRate;
			context.LocalToWorld = LocalToWorld;
			context.WorldToLocal = LocalToWorld.Inverted();
			context.ComponentSeed = Seed;
			SetCamera(context, Camera.main ?? VfxRuntime.FallbackCamera);

			int alive = 0;
			foreach (VfxSystemState state in systems)
			{
				PrepareSystem(state, frame, deltaTime, moved, simulated);
				alive += state.AliveCount;
			}

			AliveCount = alive;
		}

		/// <summary>Binds a system's buffers and resources for its initialize or update kernel (the program is in use).</summary>
		internal void BindForKernel(VfxSystemState state, ShaderProgram program, bool update)
		{
			VfxSystemDescription description = state.Description;
			VfxRuntime.BindParameters(state);
			VfxRuntime.BindStorage(0, state.Attributes);
			VfxRuntime.BindStorage(1, state.Current);
			VfxRuntime.BindStorage(3, state.Dead);
			VfxRuntime.BindStorage(12, state.AliveMask);
			VfxRuntime.BindStorage(4, VfxRuntime.CounterBuffer);
			VfxRuntime.BindStorage(5, state.Curves);
			if (update)
			{
				VfxRuntime.BindStorage(2, state.Other);
				for (int e = 0; e < VfxCompiler.MaxEventsPerSystem; e++)
				{
					if (state.EventOut[e] != 0)
					{
						VfxRuntime.BindStorage(7 + e, state.EventOut[e]);
					}
				}
			}
			else if (state.Parent != null && description.ParentEvent >= 0 && description.ParentEvent < VfxCompiler.MaxEventsPerSystem)
			{
				int eventBuffer = state.Parent.EventOut[description.ParentEvent];
				if (eventBuffer != 0)
				{
					VfxRuntime.BindStorage(6, eventBuffer);
				}
			}

			if (description.Resources.Meshes.Count > 0)
			{
				VfxRuntime.BindStorage(9, GeometryPool.VertexBufferHandle);
			}

			if (state.Strip != 0)
			{
				VfxRuntime.BindStorage(10, state.Strip);
			}

			BindResources(program, state);
			if (description.UsesDepth)
			{
				VfxRuntime.SetDepthUniforms(program, VfxRuntime.LastDepth);
			}

			if (description.UsesSdf)
			{
				SetSdfUniforms(program);
			}
		}

		/// <summary>After the frame's kernels: the lists swap and the spawns count towards the alive bound.</summary>
		internal void AfterDispatch(VfxSystemState state, long frame)
		{
			state.CurrentIsA = !state.CurrentIsA;
			state.SpawnedTotal += state.SpawnRequest;
			uint added = state.Parent != null ? (state.SpawnsFromParent ? (uint)state.Description.Capacity : 0u) : state.SpawnRequest;
			state.RecordSpawn(frame, added);
		}

		/// <summary>Whether a transparent entry sorts this view; sizes the sort (a power of two over the alive bound).</summary>
		internal bool PrepareSort(VfxDrawEntry entry)
		{
			VfxSystemState state = entry.State;
			if (state == null || state.SortKeys == null || entry.Output >= state.SortKeys.Length || state.SortKeys[entry.Output] == 0 || !state.Description.Outputs[entry.Output].Sorted)
			{
				entry.SortCount = 0;
				return false;
			}

			int n = 1;
			while (n < state.AliveUpperBound)
			{
				n <<= 1;
			}

			entry.SortCount = Math.Min(n, state.SortLength);

			// The keys quantize the depth over the system's bounds along the view (a coarse range until they are known).
			Vector3 min;
			Vector3 max;
			float near = 0f;
			float far = 1000f;
			if (WorldBounds(state, out min, out max))
			{
				near = float.MaxValue;
				far = float.MinValue;
				for (int i = 0; i < 8; i++)
				{
					var corner = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
					float depth = Vector3.Dot(corner - VfxRuntime.ViewPosition, VfxRuntime.ViewForward);
					near = Math.Min(near, depth);
					far = Math.Max(far, depth);
				}
			}

			entry.SortDepthNear = near;
			entry.SortDepthScale = far - near > 1e-5f ? 1f / (far - near) : 0f;
			return state.PositionPlane >= 0;
		}

		/// <summary>Binds the buffers the sort kernel reads for an output: attributes, the draw list and the output's keys.</summary>
		internal void BindForSort(VfxSystemState state, int output)
		{
			VfxRuntime.BindStorage(0, state.Attributes);
			VfxRuntime.BindStorage(1, state.Current);
			VfxRuntime.BindStorage(2, state.SortKeys[output]);
		}

		#endregion

		#region PrivateMethods

		private VfxValue LookupProperty(string propertyName, VfxValueType type)
		{
			return GetProperty(propertyName);
		}

		private static void SetCamera(VfxEvalContext evalContext, Camera camera)
		{
			if (camera == null)
			{
				return;
			}

			evalContext.CameraPosition = camera.transform.position;
			evalContext.CameraForward = camera.transform.forward;
			evalContext.CameraUp = camera.transform.up;
			evalContext.CameraRight = camera.transform.right;
		}

		/// <summary>One system's CPU frame: spawn count, parameters, curves, bounds, and whether its kernels run.</summary>
		private void PrepareSystem(VfxSystemState state, long frame, float deltaTime, float moved, List<VfxSystemState> simulated)
		{
			VfxSystemDescription description = state.Description;
			state.Simulating = false;
			if (state.Programs.Initialize == null || state.Programs.Update == null)
			{
				return;
			}

			uint spawnCount = 0;
			foreach (VfxSpawnerState spawner in state.Spawners)
			{
				spawnCount += spawner.Advance(deltaTime, context, moved);
			}

			spawnCount = Math.Min(spawnCount, (uint)description.Capacity);
			context.SpawnCount = spawnCount;
			context.SpawnedTotal = state.SpawnedTotal;
			context.SystemSeed = state.SystemSeed;
			context.Capacity = (uint)description.Capacity;
			UpdateMeshDraws(state);

			bool child = state.Parent != null;
			state.SpawnsFromParent = child && state.Parent.Simulating;
			if (spawnCount == 0 && state.AliveUpperBound == 0 && !state.SpawnsFromParent)
			{
				// Empty and nothing to spawn: no kernels, no draws.
				VfxRuntime.CountSkipped();
				return;
			}

			// Dense systems update every slot in order (coalesced); sparse ones walk their alive list.
			state.Dense = (long)(state.AliveUpperBound + spawnCount) * 4 >= description.Capacity;
			WriteParameters(state, deltaTime);
			UploadCurves(state);
			UpdateManualBounds(state);
			state.Simulating = true;
			state.SpawnRequest = child ? 0u : spawnCount;
			state.NeedsInit = child ? state.SpawnsFromParent : spawnCount > 0;
			state.FrameFlags = FrameFlags(description, child);
			simulated.Add(state);
		}

		/// <summary>The frame kernel's flags of a system: output count, the outputs whose draws count instances, GPU-event child.</summary>
		private static uint FrameFlags(VfxSystemDescription description, bool child)
		{
			int outputs = Math.Min(description.Outputs.Count, VfxCodegen.MaxOutputs);
			uint flags = (uint)outputs;
			for (int o = 0; o < outputs; o++)
			{
				if (description.Outputs[o].DrawCountField == 1)
				{
					flags |= 1u << (4 + o);
				}
			}

			if (child)
			{
				flags |= 4096u;
			}

			return flags;
		}

		// ---------------------------------------------------------------- build

		private bool EnsureBuilt(long frame)
		{
			VfxProgramSet set = VfxRuntime.GetPrograms(Graph);
			if (set == null || set.HasErrors)
			{
				if (programs != set)
				{
					ReleaseSystems();
					programs = set;
					builtVersion = set?.GraphVersion ?? -1;
				}

				return false;
			}

			if (set == programs && builtVersion == set.GraphVersion && systems.Count == set.Systems.Count)
			{
				return true;
			}

			bool wasPlaying = Playing || pendingPlay;
			ReleaseSystems();
			programs = set;
			builtVersion = set.GraphVersion;
			Capacity = 0;
			NeedsDepth = false;
			HasShadowCasters = false;
			for (int i = 0; i < set.Compiled.Systems.Count; i++)
			{
				VfxSystemDescription description = set.Compiled.Systems[i];
				var state = new VfxSystemState { Instance = this, Index = i, Description = description, Programs = set.Systems[i] };
				if (description.ParentSystem >= 0 && description.ParentSystem < systems.Count)
				{
					state.Parent = systems[description.ParentSystem];
					state.Layer = state.Parent.Layer + 1;
				}

				state.ProgramOrder = (state.Programs.Update?.Program.GetHashCode() ?? 0) ^ i;
				state.PositionPlane = description.FindStored(VfxAttributes.Position.Name)?.Plane ?? -1;
				CreateBuffers(state, frame);
				foreach (VfxSpawnerDescription spawner in description.Spawners)
				{
					state.Spawners.Add(new VfxSpawnerState(spawner));
				}

				state.SystemSeed = VfxRandomHash.Pcg(Seed ^ (uint)(i * 0x3C6EF372));
				Capacity += description.Capacity;
				NeedsDepth |= description.UsesDepth;
				for (int o = 0; o < description.Outputs.Count; o++)
				{
					NeedsDepth |= description.Outputs[o].SoftParticles;
					HasShadowCasters |= description.Outputs[o].CastShadows && state.Programs.Outputs[o].Shadow != null;
				}

				systems.Add(state);
			}

			VfxRuntime.MarkParamsDirty();
			if (wasPlaying)
			{
				pendingPlay = true;
				Playing = true;
			}

			return true;
		}

		private void CreateBuffers(VfxSystemState state, long frame)
		{
			VfxSystemDescription description = state.Description;
			int capacity = description.Capacity;
			state.Attributes = CreateBuffer(Math.Max(1, description.Planes * capacity) * sizeof(float));
			state.AliveA = CreateBuffer(capacity * sizeof(uint));
			state.AliveB = CreateBuffer(capacity * sizeof(uint));
			state.Dead = CreateBuffer(capacity * sizeof(uint));
			state.AliveMask = CreateBuffer(capacity * sizeof(uint));
			state.Curves = CreateBuffer(Math.Max(1, description.Resources.CurveBufferLength) * 16);
			if (description.UsesStrip)
			{
				state.Strip = CreateBuffer(Math.Max(1, capacity * Math.Max(2, description.StripLength)) * 16);
			}

			for (int e = 0; e < description.Events.Count && e < VfxCompiler.MaxEventsPerSystem; e++)
			{
				// Sized for the largest system the event feeds (none: nothing reads it).
				state.EventOut[e] = CreateBuffer(Math.Max(1, description.EventCapacities[e]) * 64);
			}

			state.SortIndexBits = 1;
			while ((1L << state.SortIndexBits) <= capacity)
			{
				state.SortIndexBits++;
			}

			state.SortLength = 1;
			while (state.SortLength < capacity)
			{
				state.SortLength *= 2;
			}

			state.SortKeys = new int[description.Outputs.Count];
			for (int o = 0; o < description.Outputs.Count; o++)
			{
				if (description.Outputs[o].Sorted)
				{
					state.SortKeys[o] = CreateBuffer(state.SortLength * 4);
				}
			}

			int vectors = Math.Max(VfxUniformTable.HeaderVectors, description.Uniforms.VectorCount);
			int matrices = Math.Max(VfxUniformTable.HeaderMatrices, description.Uniforms.MatrixCount);
			state.UboData = new float[vectors * 4 + matrices * 16];
			state.MeshRanges = new int[description.Outputs.Count * 3];
			for (int i = 0; i < state.MeshRanges.Length; i++)
			{
				state.MeshRanges[i] = -1;
			}

			state.CounterBlock = VfxRuntime.AllocateCounterBlock(state);
			ResetBuffers(state, frame);
		}

		private static int CreateBuffer(int bytes)
		{
			int buffer = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, Math.Max(16, bytes), IntPtr.Zero, BufferUsageHint.DynamicCopy);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			return buffer;
		}

		/// <summary>Empties a system: every slot on the dead stack (slot 0 on top), the counters at zero, the draw arguments set up.</summary>
		private void ResetBuffers(VfxSystemState state, long validFrom)
		{
			VfxSystemDescription description = state.Description;
			int capacity = description.Capacity;
			var dead = new uint[capacity];
			for (int i = 0; i < capacity; i++)
			{
				dead[i] = (uint)(capacity - 1 - i);
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, state.Dead);
			GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, capacity * sizeof(uint), dead);
			Array.Clear(dead, 0, dead.Length);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, state.AliveMask);
			GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, capacity * sizeof(uint), dead);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

			uint[] block = counterInit;
			Array.Clear(block, 0, block.Length);
			block[VfxCodegen.CounterInitDispatch + 1] = 1;
			block[VfxCodegen.CounterInitDispatch + 2] = 1;
			block[VfxCodegen.CounterUpdateDispatch + 1] = 1;
			block[VfxCodegen.CounterUpdateDispatch + 2] = 1;
			block[VfxCodegen.CounterDead] = (uint)capacity;
			for (int i = 0; i < 3; i++)
			{
				block[VfxCodegen.CounterBounds + i] = 0xFFFFFFFFu;
			}

			for (int o = 0; o < description.Outputs.Count && o < VfxCodegen.MaxOutputs; o++)
			{
				int b = VfxCodegen.CounterDraw + o * VfxCodegen.CounterDrawStride;
				if (description.Outputs[o].Kind == VfxOutputKind.Mesh)
				{
					Mesh mesh = ResolveMesh(description.Outputs[o].Mesh);
					int baseVertex;
					int vertexCount;
					int firstIndex;
					int indexCount;
					GeometryPool.GetRange(mesh, out baseVertex, out vertexCount, out firstIndex, out indexCount);
					block[b] = (uint)indexCount;
					block[b + 2] = (uint)firstIndex;
					block[b + 3] = (uint)baseVertex;
					state.MeshRanges[o * 3] = indexCount;
					state.MeshRanges[o * 3 + 1] = firstIndex;
					state.MeshRanges[o * 3 + 2] = baseVertex;
				}
				else
				{
					block[b + 1] = 1; // instance count
				}
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, VfxRuntime.CounterBuffer);
			GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)(state.CounterBase * sizeof(uint)), block.Length * sizeof(uint), block);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

			state.SpawnedTotal = 0;
			state.CurrentIsA = true;
			state.ResetTracking(validFrom);
		}

		/// <summary>A mesh output whose mesh moved in the geometry pool (or changed) gets its draw arguments rewritten.</summary>
		private void UpdateMeshDraws(VfxSystemState state)
		{
			VfxSystemDescription description = state.Description;
			for (int o = 0; o < description.Outputs.Count && o < VfxCodegen.MaxOutputs; o++)
			{
				if (description.Outputs[o].Kind != VfxOutputKind.Mesh)
				{
					continue;
				}

				Mesh mesh = ResolveMesh(description.Outputs[o].Mesh);
				int baseVertex;
				int vertexCount;
				int firstIndex;
				int indexCount;
				GeometryPool.GetRange(mesh, out baseVertex, out vertexCount, out firstIndex, out indexCount);
				if (state.MeshRanges[o * 3] == indexCount && state.MeshRanges[o * 3 + 1] == firstIndex && state.MeshRanges[o * 3 + 2] == baseVertex)
				{
					continue;
				}

				state.MeshRanges[o * 3] = indexCount;
				state.MeshRanges[o * 3 + 1] = firstIndex;
				state.MeshRanges[o * 3 + 2] = baseVertex;
				uint[] fields = { (uint)indexCount };
				int b = state.CounterBase + VfxCodegen.CounterDraw + o * VfxCodegen.CounterDrawStride;
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, VfxRuntime.CounterBuffer);
				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)(b * sizeof(uint)), sizeof(uint), fields);
				fields[0] = (uint)firstIndex;
				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((b + 2) * sizeof(uint)), sizeof(uint), fields);
				fields[0] = (uint)baseVertex;
				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((b + 3) * sizeof(uint)), sizeof(uint), fields);
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			}
		}

		private void ReleaseSystems()
		{
			foreach (VfxSystemState state in systems)
			{
				DeleteBuffer(ref state.Attributes);
				DeleteBuffer(ref state.AliveA);
				DeleteBuffer(ref state.AliveB);
				DeleteBuffer(ref state.Dead);
				DeleteBuffer(ref state.AliveMask);
				DeleteBuffer(ref state.Curves);
				DeleteBuffer(ref state.Strip);
				for (int e = 0; e < state.EventOut.Length; e++)
				{
					DeleteBuffer(ref state.EventOut[e]);
				}

				if (state.SortKeys != null)
				{
					for (int o = 0; o < state.SortKeys.Length; o++)
					{
						DeleteBuffer(ref state.SortKeys[o]);
					}
				}

				VfxRuntime.FreeCounterBlock(state.CounterBlock);
				state.CounterBlock = -1;
				state.UboData = null;
			}

			if (systems.Count > 0)
			{
				VfxRuntime.MarkParamsDirty();
			}

			systems.Clear();
			AliveCount = 0;
		}

		private static void DeleteBuffer(ref int buffer)
		{
			if (buffer != 0)
			{
				GL.DeleteBuffer(buffer);
				buffer = 0;
			}
		}

		private Mesh ResolveMesh(VfxExpression expression)
		{
			if (expression == null)
			{
				return null;
			}

			VfxValue value = expression.Kind == VfxExprKind.Property ? GetProperty(expression.Name) : expression.Constant;
			return value?.Mesh;
		}

		/// <summary>
		///     Textures and meshes of the system's resource table on a program: the textures on their units (skipped
		///     when bound already), the samplers pointed at the units once per program.
		/// </summary>
		private void BindResources(ShaderProgram program, VfxSystemState state)
		{
			VfxResourceTable resources = state.Description.Resources;
			VfxRuntime.ProgramState programState = VfxRuntime.StateOf(program);
			bool setSamplers = !programState.SamplersSet;
			programState.SamplersSet = true;
			foreach (VfxResourceTable.TextureSlot slot in resources.Textures)
			{
				VfxValue value = ResolveResource(slot.Expression);
				Texture texture = slot.Volume ? (Texture)value?.Texture3D : value?.Texture;
				TextureTarget target;
				int handle;
				if (texture != null && texture.Handle != 0)
				{
					target = texture.Target;
					handle = texture.Handle;
				}
				else if (slot.Volume)
				{
					target = TextureTarget.Texture3D;
					handle = VfxDefaults.WhiteVolume.Handle;
				}
				else
				{
					target = TextureTarget.Texture2D;
					handle = slot.StripeDefault ? VfxDefaults.SoftStripe.Handle : slot.SoftDefault ? VfxDefaults.SoftDisc.Handle : Texture2D.whiteTexture.Handle;
				}

				VfxRuntime.BindTexture(slot.Index, target, handle);
				if (setSamplers)
				{
					program.SetInt(slot.Uniform, slot.Index);
				}
			}

			foreach (VfxResourceTable.MeshSlot slot in resources.Meshes)
			{
				int location = program.GetUniformLocation(slot.InfoUniform);
				if (location < 0)
				{
					continue;
				}

				Mesh mesh = ResolveMesh(slot.Expression);
				int baseVertex;
				int vertexCount;
				int firstIndex;
				int indexCount;
				GeometryPool.GetRange(mesh, out baseVertex, out vertexCount, out firstIndex, out indexCount);
				GL.Uniform4(location, (uint)baseVertex, (uint)vertexCount, (uint)firstIndex, (uint)indexCount);
			}
		}

		private VfxValue ResolveResource(VfxExpression expression)
		{
			if (expression == null)
			{
				return null;
			}

			return expression.Kind == VfxExprKind.Property ? GetProperty(expression.Name) : expression.Constant;
		}

		private static void SetSdfUniforms(ShaderProgram program)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			bool available = gi.DistanceFieldEnabled && gi.GlobalSdf != 0 && gi.Enabled;
			if (available)
			{
				Lighting.SetSdfUniforms(program);
			}
			else
			{
				program.SetInt("sdfResolution", 0);
			}
		}

		// ---------------------------------------------------------------- parameters

		/// <summary>
		///     Fills the system's parameter block (the runtime packs and uploads every block at once): the header every
		///     frame, the hoisted expressions that change per frame every frame, the ones that only depend on properties
		///     when a property changed.
		/// </summary>
		private void WriteParameters(VfxSystemState state, float deltaTime)
		{
			float[] data = state.UboData;
			VfxSystemDescription description = state.Description;
			int matrixBase = Math.Max(VfxUniformTable.HeaderVectors, description.Uniforms.VectorCount) * 4;
			WriteVector(data, VfxUniformTable.TimeSlot, deltaTime, Time, PlayRate, Bits(FrameIndex));
			WriteVector(data, VfxUniformTable.CountsSlot, Bits(context.SpawnCount), Bits(state.SpawnedTotal), Bits(state.SystemSeed), Bits((uint)description.Capacity));
			WriteVector(data, VfxUniformTable.CameraPositionSlot, context.CameraPosition);
			WriteVector(data, VfxUniformTable.CameraForwardSlot, context.CameraForward);
			WriteVector(data, VfxUniformTable.CameraUpSlot, context.CameraUp);
			WriteVector(data, VfxUniformTable.CameraRightSlot, context.CameraRight);
			WriteVector(data, VfxUniformTable.SortSlot, context.CameraPosition);
			WriteVector(data, VfxUniformTable.ReservedSlot, Bits((uint)state.CounterBase), Bits(state.Dense ? 1u : 0u), 0f, 0f);
			WriteMatrix(data, matrixBase, VfxUniformTable.LocalToWorldSlot, context.LocalToWorld);
			WriteMatrix(data, matrixBase, VfxUniformTable.WorldToLocalSlot, context.WorldToLocal);
			bool refreshStatic = state.StaticVersion != PropertyVersion;
			foreach (VfxUniformTable.Entry entry in description.Uniforms.Entries)
			{
				if (!refreshStatic && entry.Expression.IsFrameInvariant)
				{
					continue;
				}

				VfxValue value;
				try
				{
					value = entry.Expression.Evaluate(context);
				}
				catch (Exception)
				{
					value = VfxValue.Default(entry.Type);
				}

				if (entry.Type == VfxValueType.Matrix)
				{
					WriteMatrix(data, matrixBase, entry.Slot, value.Matrix);
					continue;
				}

				if (value.Type != entry.Type)
				{
					value = value.ConvertTo(entry.Type);
				}

				switch (entry.Type)
				{
					case VfxValueType.Int:
						WriteVector(data, entry.Slot, BitConverter.Int32BitsToSingle(value.Int), 0f, 0f, 0f);
						break;
					case VfxValueType.Uint:
						WriteVector(data, entry.Slot, Bits(value.Uint), 0f, 0f, 0f);
						break;
					case VfxValueType.Bool:
						WriteVector(data, entry.Slot, value.Bool ? 1f : 0f, 0f, 0f, 0f);
						break;
					default:
						WriteVector(data, entry.Slot, value.Vector.X, value.Vector.Y, value.Vector.Z, value.Vector.W);
						break;
				}
			}

			state.StaticVersion = PropertyVersion;
		}

		private static float Bits(uint value)
		{
			return BitConverter.UInt32BitsToSingle(value);
		}

		private static void WriteVector(float[] data, int slot, float x, float y, float z, float w)
		{
			int i = slot * 4;
			if (i + 3 >= data.Length)
			{
				return;
			}

			data[i] = x;
			data[i + 1] = y;
			data[i + 2] = z;
			data[i + 3] = w;
		}

		private static void WriteVector(float[] data, int slot, Vector3 v)
		{
			WriteVector(data, slot, v.X, v.Y, v.Z, 0f);
		}

		private static void WriteMatrix(float[] data, int matrixBase, int slot, Matrix4 m)
		{
			int i = matrixBase + slot * 16;
			if (i + 15 >= data.Length)
			{
				return;
			}

			data[i] = m.Row0.X;
			data[i + 1] = m.Row0.Y;
			data[i + 2] = m.Row0.Z;
			data[i + 3] = m.Row0.W;
			data[i + 4] = m.Row1.X;
			data[i + 5] = m.Row1.Y;
			data[i + 6] = m.Row1.Z;
			data[i + 7] = m.Row1.W;
			data[i + 8] = m.Row2.X;
			data[i + 9] = m.Row2.Y;
			data[i + 10] = m.Row2.Z;
			data[i + 11] = m.Row2.W;
			data[i + 12] = m.Row3.X;
			data[i + 13] = m.Row3.Y;
			data[i + 14] = m.Row3.Z;
			data[i + 15] = m.Row3.W;
		}

		private void UploadCurves(VfxSystemState state)
		{
			VfxResourceTable resources = state.Description.Resources;
			if (resources.CurveBufferLength == 0 || state.CurvesVersion == PropertyVersion)
			{
				return;
			}

			state.CurvesVersion = PropertyVersion;
			float[] data = resources.Bake(ResolveResource);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, state.Curves);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, data.Length * sizeof(float), data, BufferUsageHint.DynamicDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
		}

		// ---------------------------------------------------------------- bounds

		private void UpdateManualBounds(VfxSystemState state)
		{
			VfxSystemDescription description = state.Description;
			if (description.BoundsMode != VfxBoundsMode.Manual)
			{
				return;
			}

			if (state.BoundsValid && state.StaticVersion == PropertyVersion && description.BoundsCenter.IsFrameInvariant && description.BoundsSize.IsFrameInvariant)
			{
				return;
			}

			try
			{
				Vector3 center = description.BoundsCenter.Evaluate(context).Vector3;
				Vector3 size = description.BoundsSize.Evaluate(context).Vector3;
				Vector3 half = new Vector3(Math.Abs(size.X), Math.Abs(size.Y), Math.Abs(size.Z)) * 0.5f;
				state.BoundsMin = center - half;
				state.BoundsMax = center + half;
				state.BoundsValid = true;
			}
			catch (Exception)
			{
				state.BoundsValid = false;
			}
		}

		private bool WorldBounds(VfxSystemState state, out Vector3 min, out Vector3 max)
		{
			if (!state.BoundsValid)
			{
				min = max = Vector3.Zero;
				return false;
			}

			if (state.Description.InitWriter.WorldSpace)
			{
				min = state.BoundsMin;
				max = state.BoundsMax;
				return true;
			}

			min = new Vector3(float.MaxValue);
			max = new Vector3(float.MinValue);
			for (int i = 0; i < 8; i++)
			{
				var corner = new Vector3((i & 1) != 0 ? state.BoundsMax.X : state.BoundsMin.X, (i & 2) != 0 ? state.BoundsMax.Y : state.BoundsMin.Y, (i & 4) != 0 ? state.BoundsMax.Z : state.BoundsMin.Z);
				Vector3 world = Vector3.TransformPosition(corner, LocalToWorld);
				min = Vector3.ComponentMin(min, world);
				max = Vector3.ComponentMax(max, world);
			}

			return true;
		}

		// ---------------------------------------------------------------- draw

		private void DrawOutput(RenderContext renderContext, VfxSystemState state, VfxOutputDescription output, ShaderProgram program, bool shadowPass, Matrix4 view, Matrix4 projection, VfxDepthCapture depth)
		{
			VfxRuntime.BindParameters(state);
			VfxRuntime.BindStorage(0, state.Attributes);
			VfxRuntime.BindStorage(5, state.Curves);
			if (state.Strip != 0)
			{
				VfxRuntime.BindStorage(10, state.Strip);
			}

			uint mask = 0xFFFFFFFFu;
			if (output.Sorted && !shadowPass && state.SortKeys != null && output.Index < state.SortKeys.Length && state.SortKeys[output.Index] != 0 && state.PositionPlane >= 0)
			{
				VfxRuntime.BindStorage(1, state.SortKeys[output.Index]);
				mask = (1u << state.SortIndexBits) - 1u;
			}
			else
			{
				VfxRuntime.BindStorage(1, state.Current);
			}

			program.Use();
			// View uniforms once per program and view (a program keeps its uniform values between draws).
			VfxRuntime.ProgramState programState = VfxRuntime.StateOf(program);
			if (programState.ViewStamp != VfxRuntime.ViewStamp)
			{
				programState.ViewStamp = VfxRuntime.ViewStamp;
				program.SetMatrix4("projectionMatrix", projection);
				program.SetMatrix4("viewMatrix", view);
				program.SetFloat("vfxViewportHeight", renderContext.Height);
				program.SetVector4("vfxFragmentParams", new Vector4(output.AlphaThreshold, output.Roughness, 0f, 0f));
				programState.DepthSet = false;
				if (output.Lighting == VfxLightingMode.Lit && !shadowPass)
				{
					Lighting.SetUniforms(program, output.ReceiveShadows, false);
					Lighting.SetVolumeGIUniforms(program, output.ReceiveGI);
				}
			}

			if (programState.Mask != mask)
			{
				programState.Mask = mask;
				program.SetUint("vfxDrawListMask", mask);
			}

			BindResources(program, state);
			if (output.SoftParticles)
			{
				if (!programState.DepthSet || programState.Depth != depth)
				{
					programState.DepthSet = true;
					programState.Depth = depth;
					VfxRuntime.SetDepthUniforms(program, depth);
				}
				else if (depth != null && depth.Valid && depth.Depth != null)
				{
					VfxRuntime.BindTexture(VfxRuntime.DepthTextureUnit, TextureTarget.Texture2D, depth.Depth.Handle);
				}
			}

			// Fixed-function state of the output (skipped when the previous particle draw set the same).
			if (shadowPass)
			{
				VfxRuntime.SetDrawState(0, true, false);
			}
			else
			{
				int blend = output.Blend == VfxBlendMode.Additive ? 1 : output.Blend == VfxBlendMode.Premultiplied ? 2 : output.Blend == VfxBlendMode.Opaque || output.Blend == VfxBlendMode.AlphaClip ? 0 : 3;
				VfxRuntime.SetDrawState(blend, output.ZWrite, output.CullBack);
			}

			// The draw arguments the update kernel wrote (the frame's simulation already ended with a barrier).
			VfxRuntime.CountDraw();
			GL.BindBuffer(BufferTarget.DrawIndirectBuffer, VfxRuntime.CounterBuffer);
			var offset = (IntPtr)((state.CounterBase + VfxCodegen.CounterDraw + output.Index * VfxCodegen.CounterDrawStride) * sizeof(uint));
			switch (output.Kind)
			{
				case VfxOutputKind.Mesh:
					GeometryPool.Bind();
					GL.DrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, offset);
					break;
				case VfxOutputKind.Point:
					GL.BindVertexArray(VfxRuntime.EmptyVao);
					GL.Enable(EnableCap.ProgramPointSize);
					GL.DrawArraysIndirect(PrimitiveType.Points, offset);
					GL.Disable(EnableCap.ProgramPointSize);
					break;
				case VfxOutputKind.Line:
					GL.BindVertexArray(VfxRuntime.EmptyVao);
					GL.DrawArraysIndirect(PrimitiveType.Lines, offset);
					break;
				default:
					// Quads and strips: indexed, so the corners shared by their triangles are shaded once.
					GL.BindVertexArray(VfxRuntime.IndexedVao(output, state.Description.Capacity));
					GL.DrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, offset);
					break;
			}

			GL.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
			GL.BindVertexArray(0);
		}

		#endregion
	}

	/// <summary>Fallback resources of unbound texture slots.</summary>
	public static class VfxDefaults
	{
		private static Texture3D whiteVolume;
		private static Texture2D softDisc;
		private static Texture2D softStripe;

		public static Texture3D WhiteVolume => whiteVolume ??= new Texture3D(1, 1, 1, new byte[] { 255, 255, 255, 255 }, "white");

		/// <summary>A 64x4 white stripe fading to transparent at its left and right edges: the look of untextured strips.</summary>
		public static Texture2D SoftStripe
		{
			get
			{
				if (softStripe == null)
				{
					const int width = 64;
					const int height = 4;
					var pixels = new byte[width * height * 4];
					for (int y = 0; y < height; y++)
					{
						for (int x = 0; x < width; x++)
						{
							float d = Math.Abs((x + 0.5f) / width * 2f - 1f);
							float a = Math.Clamp(1f - d, 0f, 1f);
							a = a * a * (3f - 2f * a);
							int i = (y * width + x) * 4;
							pixels[i] = 255;
							pixels[i + 1] = 255;
							pixels[i + 2] = 255;
							pixels[i + 3] = (byte)(a * 255f + 0.5f);
						}
					}

					softStripe = new Texture2D(width, height, pixels, "vfx-soft-stripe", true);
					softStripe.wrapMode = TextureWrap.Clamp;
				}

				return softStripe;
			}
		}

		/// <summary>A 64x64 white disc fading to transparent at the edge: the look of untextured quads.</summary>
		public static Texture2D SoftDisc
		{
			get
			{
				if (softDisc == null)
				{
					const int size = 64;
					var pixels = new byte[size * size * 4];
					for (int y = 0; y < size; y++)
					{
						for (int x = 0; x < size; x++)
						{
							float dx = (x + 0.5f) / size * 2f - 1f;
							float dy = (y + 0.5f) / size * 2f - 1f;
							float d = MathF.Sqrt(dx * dx + dy * dy);
							float a = Math.Clamp(1f - d, 0f, 1f);
							a = a * a * (3f - 2f * a);
							int i = (y * size + x) * 4;
							pixels[i] = 255;
							pixels[i + 1] = 255;
							pixels[i + 2] = 255;
							pixels[i + 3] = (byte)(a * 255f + 0.5f);
						}
					}

					softDisc = new Texture2D(size, size, pixels, "vfx-soft-disc", true);
					softDisc.wrapMode = TextureWrap.Clamp;
				}

				return softDisc;
			}
		}
	}
}
