using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	public enum VfxBlendMode
	{
		Alpha,
		Additive,
		Premultiplied,
		Opaque,
		AlphaClip
	}

	public enum VfxLightingMode
	{
		Unlit,
		Lit
	}

	public enum VfxOrientMode
	{
		/// <summary>Quads face the camera plane (classic billboards).</summary>
		FaceCameraPlane,

		/// <summary>Quads face the camera position (spherical billboards).</summary>
		FaceCamera,

		/// <summary>Quads lie in a fixed plane given by an up and a right axis.</summary>
		FixedAxis,

		/// <summary>Quads stretch along their velocity (the quad's y axis follows the motion).</summary>
		AlongVelocity,

		/// <summary>Quads rotate around a fixed axis to face the camera (grass, trees).</summary>
		AxisAlignedBillboard
	}

	public enum VfxUvMode
	{
		Simple,
		Flipbook,
		FlipbookBlend
	}

	public enum VfxSortMode
	{
		/// <summary>Sorted when blended with alpha, unsorted otherwise.</summary>
		Auto,
		Off,
		ByDistance
	}

	/// <summary>The shape a quad output draws per particle (Unity's primitive type).</summary>
	public enum VfxQuadPrimitive
	{
		/// <summary>Two triangles covering the texture.</summary>
		Quad,

		/// <summary>One triangle around the texture: fewer vertices, more fill (round, transparent-edged textures).</summary>
		Triangle,

		/// <summary>Six triangles with the corners cut (Crop Factor): less fill over transparent corners.</summary>
		Octagon
	}

	public enum VfxBoundsMode
	{
		/// <summary>The bounds set in the initialize context (centre and size in the effect's space).</summary>
		Manual,

		/// <summary>The bounds of the alive particles, reduced on the GPU and read back with a delay.</summary>
		Automatic
	}

	public enum VfxSpawnBlockKind
	{
		ConstantRate,
		SingleBurst,
		PeriodicBurst,
		VariableRate,
		SpawnOverDistance
	}

	public enum VfxLoopMode
	{
		Infinite,
		Constant
	}

	/// <summary>A compile error or warning attached to a node.</summary>
	public sealed class VfxCompileMessage
	{
		public ulong NodeId;
		public string Message;
		public bool IsError = true;

		public override string ToString()
		{
			return (IsError ? "Error: " : "Warning: ") + Message;
		}
	}

	/// <summary>One stored particle attribute: its range in the attribute buffer (structure of arrays, one float plane per component).</summary>
	public sealed class VfxStoredAttribute
	{
		public VfxAttribute Attribute;

		/// <summary>Index of the attribute's first component plane.</summary>
		public int Plane;

		public int Components;
	}

	/// <summary>A spawn block as the CPU spawner runs it.</summary>
	public sealed class VfxSpawnBlockDescription
	{
		public ulong NodeId;
		public VfxSpawnBlockKind Kind;

		/// <summary>Particles per second (constant / variable rate) or per unit moved (over distance).</summary>
		public VfxExpression Rate;

		/// <summary>Particles per burst.</summary>
		public VfxExpression Count;

		/// <summary>Seconds into the loop before the (first) burst.</summary>
		public VfxExpression Delay;

		/// <summary>Seconds between periodic bursts.</summary>
		public VfxExpression Period;
	}

	/// <summary>A spawn context: its blocks, loop settings and the events that start and stop it.</summary>
	public sealed class VfxSpawnerDescription
	{
		public ulong NodeId;
		public string Name;
		public readonly List<VfxSpawnBlockDescription> Blocks = new List<VfxSpawnBlockDescription>();
		public VfxLoopMode LoopMode;
		public VfxExpression LoopDuration;
		public VfxExpression LoopCount;
		public VfxExpression DelayBeforeLoop;
		public VfxExpression DelayAfterLoop;
		public readonly List<string> StartEvents = new List<string>();
		public readonly List<string> StopEvents = new List<string>();
	}

	/// <summary>An output context compiled: its draw settings, draw programs and the attributes it reads.</summary>
	public sealed class VfxOutputDescription
	{
		public ulong NodeId;
		public int Index;
		public string Name;
		public VfxOutputKind Kind;
		public VfxBlendMode Blend;
		public VfxLightingMode Lighting;
		public VfxOrientMode Orient;
		public VfxUvMode Uv;
		public VfxSortMode Sort;
		public bool SoftParticles;
		public bool CastShadows;
		public bool ReceiveShadows = true;
		public bool ReceiveGI = true;
		public bool ZWrite;
		public bool CullBack;
		public bool TwoSidedLighting;
		public int Queue = Shaders.MaterialQueue.Transparent;
		public float AlphaThreshold = 0.5f;
		public float NormalBending;
		public float Roughness = 0.8f;
		public int StripLength = 16;
		public bool StripTile;
		public VfxExpression Mesh;
		public VfxGlslWriter Writer;
		public string VertexSource;
		public string FragmentSource;

		/// <summary>Line outputs: "Velocity" (position along the velocity) or "Target" (to the target position attribute).</summary>
		public string LineMode = "Velocity";

		/// <summary>Sampler uniform per texture input of the context ("Main Texture" -> "vfxTex0").</summary>
		public readonly Dictionary<string, string> Textures = new Dictionary<string, string>(StringComparer.Ordinal);

		/// <summary>Vertex-stage local per numeric input of the context ("Flipbook Size" -> "out_Flipbook_Size").</summary>
		public readonly Dictionary<string, string> Locals = new Dictionary<string, string>(StringComparer.Ordinal);

		/// <summary>Quad outputs: the shape drawn per particle.</summary>
		public VfxQuadPrimitive Primitive;

		public bool IsTransparent => Blend != VfxBlendMode.Opaque && Blend != VfxBlendMode.AlphaClip;
		public bool Sorted => IsTransparent && (Sort == VfxSortMode.ByDistance || (Sort == VfxSortMode.Auto && Blend != VfxBlendMode.Additive));

		/// <summary>
		///     Distinct vertices per particle: quads 4 (triangle 3, octagon 8), strips 2 per recorded position, lines 2,
		///     points 1; meshes draw instances instead (0).
		/// </summary>
		public int VerticesPerParticle
		{
			get
			{
				switch (Kind)
				{
					case VfxOutputKind.Quad: return Primitive == VfxQuadPrimitive.Triangle ? 3 : Primitive == VfxQuadPrimitive.Octagon ? 8 : 4;
					case VfxOutputKind.Line: return 2;
					case VfxOutputKind.Point: return 1;
					case VfxOutputKind.Strip: return 2 * Math.Max(2, StripLength);
					default: return 0;
				}
			}
		}

		/// <summary>
		///     Quads and strips draw indexed (a shared index buffer per shape), so the vertices shared by their triangles
		///     run the vertex shader once: indices per particle, 0 for the other outputs.
		/// </summary>
		public int IndicesPerParticle
		{
			get
			{
				switch (Kind)
				{
					case VfxOutputKind.Quad: return Primitive == VfxQuadPrimitive.Triangle ? 3 : Primitive == VfxQuadPrimitive.Octagon ? 18 : 6;
					case VfxOutputKind.Strip: return 6 * (Math.Max(2, StripLength) - 1);
					default: return 0;
				}
			}
		}

		public bool Indexed => IndicesPerParticle > 0;

		/// <summary>What the update kernel adds to the output's draw arguments per surviving particle.</summary>
		public int DrawCountPerParticle => Kind == VfxOutputKind.Mesh ? 1 : Indexed ? IndicesPerParticle : VerticesPerParticle;

		/// <summary>The draw argument that grows with the particles: the instance count of meshes, the element count otherwise.</summary>
		public int DrawCountField => Kind == VfxOutputKind.Mesh ? 1 : 0;
	}

	/// <summary>One particle system of a graph as compiled: capacity, storage layout, kernels, outputs and spawners.</summary>
	public sealed class VfxSystemDescription
	{
		public string Name;
		public int Index;
		public ulong InitializeId;
		public ulong UpdateId;
		public int Capacity = 1024;
		public VfxBoundsMode BoundsMode;
		public VfxExpression BoundsCenter;
		public VfxExpression BoundsSize;
		public readonly VfxUniformTable Uniforms = new VfxUniformTable();
		public readonly VfxResourceTable Resources = new VfxResourceTable();
		public readonly List<VfxSpawnerDescription> Spawners = new List<VfxSpawnerDescription>();
		public readonly List<VfxOutputDescription> Outputs = new List<VfxOutputDescription>();
		public readonly List<VfxStoredAttribute> Stored = new List<VfxStoredAttribute>();
		public readonly Dictionary<string, VfxAttribute> Attributes = new Dictionary<string, VfxAttribute>(StringComparer.Ordinal);

		/// <summary>Float planes per particle (storage = planes * capacity floats).</summary>
		public int Planes;

		/// <summary>System whose GPU event spawns this one (-1 for CPU-spawned systems) and the event's index in it.</summary>
		public int ParentSystem = -1;

		public int ParentEvent = -1;
		public List<VfxEventOutput> Events => UpdateWriter.Events;

		/// <summary>Payloads each GPU event buffer holds per frame: the largest capacity among the systems it feeds.</summary>
		public readonly int[] EventCapacities = new int[VfxCompiler.MaxEventsPerSystem];
		public VfxGlslWriter InitWriter;
		public VfxGlslWriter UpdateWriter;
		public bool AgeParticles = true;
		public bool ReapParticles = true;
		public bool IntegratePosition = true;
		public bool IntegrateAngle = true;
		public bool TrackOldPosition;
		public bool UsesDepth;
		public bool UsesSdf;
		public bool UsesStrip;
		public int StripLength;
		public float StripSpacing;
		public string InitSource;
		public string UpdateSource;

		/// <summary>Attributes stored in the buffer by name.</summary>
		public VfxStoredAttribute FindStored(string attributeName)
		{
			foreach (VfxStoredAttribute stored in Stored)
			{
				if (stored.Attribute.Name == attributeName)
				{
					return stored;
				}
			}

			return null;
		}

		public bool HasGpuEventParent => ParentSystem >= 0;
	}

	/// <summary>The result of compiling a graph: the systems with their GLSL sources, and the messages.</summary>
	public sealed class VfxCompiledGraph
	{
		public int GraphVersion;
		public readonly List<VfxSystemDescription> Systems = new List<VfxSystemDescription>();
		public readonly List<VfxCompileMessage> Messages = new List<VfxCompileMessage>();

		/// <summary>Names of the events the spawners react to (OnPlay / OnStop and custom ones).</summary>
		public readonly List<string> EventNames = new List<string>();

		public bool HasErrors
		{
			get
			{
				foreach (VfxCompileMessage message in Messages)
				{
					if (message.IsError)
					{
						return true;
					}
				}

				return false;
			}
		}
	}

	/// <summary>
	///     Turns a <see cref="VfxGraph" /> into <see cref="VfxCompiledGraph" />: finds the systems (one per initialize
	///     context, with their spawners, update and outputs), resolves every block input to an expression tree through
	///     the operator graph, lets the blocks emit their GLSL, decides which attributes are stored, and assembles the
	///     kernels and draw programs (<see cref="VfxCodegen" />).
	/// </summary>
	public sealed class VfxCompiler
	{
		#region PublicFields

		public const int MaxEventsPerSystem = 2;
		public const string OnPlayEvent = "OnPlay";
		public const string OnStopEvent = "OnStop";

		public VfxGraph Graph { get; }
		public VfxCompiledGraph Result { get; }

		#endregion

		#region PrivateFields

		private readonly Dictionary<ulong, VfxExpression[]> operatorOutputs = new Dictionary<ulong, VfxExpression[]>();
		private readonly HashSet<ulong> resolving = new HashSet<ulong>();
		private readonly Dictionary<ulong, VfxSystemDescription> systemsByInitialize = new Dictionary<ulong, VfxSystemDescription>();

		#endregion

		#region Constructors

		private VfxCompiler(VfxGraph graph)
		{
			Graph = graph;
			Result = new VfxCompiledGraph { GraphVersion = graph.Version };
		}

		#endregion

		#region PublicMethods

		public static VfxCompiledGraph Compile(VfxGraph graph)
		{
			var compiler = new VfxCompiler(graph);
			try
			{
				compiler.Run();
			}
			catch (Exception e)
			{
				compiler.Result.Messages.Add(new VfxCompileMessage { Message = "Compiler failure: " + e.Message + "\n" + e.StackTrace });
			}

			return compiler.Result;
		}

		public void Error(VfxNodeData node, string message)
		{
			Result.Messages.Add(new VfxCompileMessage { NodeId = node?.Id ?? 0, Message = message, IsError = true });
		}

		public void Warning(VfxNodeData node, string message)
		{
			Result.Messages.Add(new VfxCompileMessage { NodeId = node?.Id ?? 0, Message = message, IsError = false });
		}

		/// <summary>Registers an event a kernel raises (at most <see cref="MaxEventsPerSystem" /> per system); returns its index or -1.</summary>
		public int RegisterEvent(VfxGlslWriter writer, string eventName)
		{
			eventName = string.IsNullOrWhiteSpace(eventName) ? "OnDie" : eventName.Trim();
			foreach (VfxEventOutput existing in writer.Events)
			{
				if (existing.Name == eventName)
				{
					return existing.Index;
				}
			}

			if (writer.Events.Count >= MaxEventsPerSystem)
			{
				Result.Messages.Add(new VfxCompileMessage { Message = "A system raises at most " + MaxEventsPerSystem + " GPU events" });
				return -1;
			}

			var created = new VfxEventOutput { Name = eventName, Index = writer.Events.Count };
			writer.Events.Add(created);
			return created.Index;
		}

		/// <summary>The expression of an input slot: the connected output, or the inline value.</summary>
		public VfxExpression ResolveInput(VfxNodeData node, VfxSlotDefinition slot)
		{
			VfxEdge edge = Graph.FindInputEdge(node.Id, slot.Name);
			VfxExpression result = null;
			if (edge != null)
			{
				VfxNodeData source = Graph.FindNode(edge.From);
				if (source != null)
				{
					result = ResolveOutput(source, edge.FromSlot);
				}
			}

			if (result == null)
			{
				VfxValue value = node.GetInput(slot.Name) ?? slot.CreateDefault();
				if (value.Type != slot.Type && VfxValueTypes.IsNumeric(slot.Type))
				{
					value = value.ConvertTo(slot.Type);
				}

				result = VfxExpression.Const(value);
			}

			if (result.Type != slot.Type)
			{
				if (VfxValueTypes.CanConvert(result.Type, slot.Type))
				{
					result = VfxExpression.ConvertTo(result, slot.Type);
				}
				else
				{
					Error(node, "The input '" + slot.DisplayName + "' expects " + slot.Type + " but gets " + result.Type);
					result = VfxExpression.Const(slot.CreateDefault());
				}
			}

			return result;
		}

		/// <summary>The expression of an operator's output slot (computed once per node).</summary>
		public VfxExpression ResolveOutput(VfxNodeData node, string slotName)
		{
			VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
			if (definition == null || definition.Kind != VfxNodeKind.Operator)
			{
				Error(node, "Not an operator: " + node.Type);
				return null;
			}

			VfxExpression[] outputs;
			if (!operatorOutputs.TryGetValue(node.Id, out outputs))
			{
				if (!resolving.Add(node.Id))
				{
					Error(node, "The graph has a cycle through " + definition.Name);
					return null;
				}

				try
				{
					outputs = BuildOperator(node, definition);
				}
				finally
				{
					resolving.Remove(node.Id);
				}

				operatorOutputs[node.Id] = outputs;
			}

			List<VfxSlotDefinition> slots = definition.GetOutputs(node);
			for (int i = 0; i < slots.Count && i < outputs.Length; i++)
			{
				if (slots[i].Name == slotName)
				{
					return outputs[i];
				}
			}

			return outputs.Length > 0 ? outputs[0] : null;
		}

		#endregion

		#region PrivateMethods

		private void Run()
		{
			List<VfxNodeData> initializes = FindContexts(VfxContextKind.Initialize);
			if (initializes.Count == 0)
			{
				Result.Messages.Add(new VfxCompileMessage { Message = "The graph has no Initialize context: nothing to simulate", IsError = false });
				return;
			}

			// Systems in a stable order (by position), parents before their GPU-event children.
			initializes.Sort((a, b) => a.Position.X != b.Position.X ? a.Position.X.CompareTo(b.Position.X) : a.Position.Y.CompareTo(b.Position.Y));
			var ordered = new List<VfxNodeData>();
			var visited = new HashSet<ulong>();
			foreach (VfxNodeData initialize in initializes)
			{
				OrderSystem(initialize, ordered, visited, 0);
			}

			foreach (VfxNodeData initialize in ordered)
			{
				var system = new VfxSystemDescription { Index = Result.Systems.Count, InitializeId = initialize.Id, Name = initialize.Title ?? "System " + (Result.Systems.Count + 1) };
				Result.Systems.Add(system);
				systemsByInitialize[initialize.Id] = system;
				CompileSystem(system, initialize);
			}

			// A GPU event buffer holds what its largest consumer can spawn in a frame (the consumer's capacity).
			foreach (VfxSystemDescription system in Result.Systems)
			{
				if (system.ParentSystem >= 0 && system.ParentEvent >= 0 && system.ParentEvent < VfxCompiler.MaxEventsPerSystem)
				{
					VfxSystemDescription parent = Result.Systems[system.ParentSystem];
					parent.EventCapacities[system.ParentEvent] = Math.Max(parent.EventCapacities[system.ParentEvent], system.Capacity);
				}
			}

			// Sources last: the update kernels need the event capacities of the whole graph.
			foreach (VfxSystemDescription system in Result.Systems)
			{
				if (system.InitWriter == null || system.UpdateWriter == null)
				{
					continue;
				}

				system.InitSource = VfxCodegen.AssembleInitialize(system);
				system.UpdateSource = VfxCodegen.AssembleUpdate(system);
				foreach (VfxOutputDescription output in system.Outputs)
				{
					VfxCodegen.AssembleOutput(system, output);
				}
			}

			foreach (VfxSystemDescription system in Result.Systems)
			{
				foreach (VfxSpawnerDescription spawner in system.Spawners)
				{
					foreach (string name in spawner.StartEvents) AddEventName(name);
					foreach (string name in spawner.StopEvents) AddEventName(name);
				}
			}
		}

		private void AddEventName(string name)
		{
			if (!Result.EventNames.Contains(name))
			{
				Result.EventNames.Add(name);
			}
		}

		/// <summary>Depth-first: a system's GPU-event parent is ordered before it.</summary>
		private void OrderSystem(VfxNodeData initialize, List<VfxNodeData> ordered, HashSet<ulong> visited, int depth)
		{
			if (!visited.Add(initialize.Id) || depth > 16)
			{
				return;
			}

			foreach (VfxFlowEdge edge in Graph.GetFlowInputs(initialize.Id, 0))
			{
				VfxNodeData source = Graph.FindNode(edge.From);
				if (source != null && KindOf(source) == VfxContextKind.Update && edge.FromPort >= 1)
				{
					VfxNodeData parentInitialize = FindInitializeOf(source);
					if (parentInitialize != null)
					{
						OrderSystem(parentInitialize, ordered, visited, depth + 1);
					}
				}
			}

			ordered.Add(initialize);
		}

		private List<VfxNodeData> FindContexts(VfxContextKind kind)
		{
			var result = new List<VfxNodeData>();
			foreach (VfxNodeData node in Graph.Nodes)
			{
				if (KindOf(node) == kind)
				{
					result.Add(node);
				}
			}

			return result;
		}

		private static VfxContextKind KindOf(VfxNodeData node)
		{
			VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
			return definition != null && definition.Kind == VfxNodeKind.Context ? definition.ContextKind : VfxContextKind.None;
		}

		/// <summary>The initialize context upstream of an update context (through port 0 flow edges).</summary>
		private VfxNodeData FindInitializeOf(VfxNodeData update)
		{
			foreach (VfxFlowEdge edge in Graph.GetFlowInputs(update.Id, 0))
			{
				VfxNodeData source = Graph.FindNode(edge.From);
				if (source == null)
				{
					continue;
				}

				VfxContextKind kind = KindOf(source);
				if (kind == VfxContextKind.Initialize)
				{
					return source;
				}
			}

			return null;
		}

		private void CompileSystem(VfxSystemDescription system, VfxNodeData initialize)
		{
			VfxNodeDefinition initDefinition = VfxNodeLibrary.Find(initialize.Type);
			system.Capacity = Math.Clamp(initialize.GetInt("Capacity", 1024), 1, 1 << 22);
			system.BoundsMode = Parse(initialize.GetSetting("BoundsMode"), VfxBoundsMode.Manual);

			// Sources: CPU spawners and GPU-event parents.
			foreach (VfxFlowEdge edge in Graph.GetFlowInputs(initialize.Id, 0))
			{
				VfxNodeData source = Graph.FindNode(edge.From);
				if (source == null)
				{
					continue;
				}

				VfxContextKind kind = KindOf(source);
				if (kind == VfxContextKind.Spawn)
				{
					system.Spawners.Add(CompileSpawner(source));
				}
				else if (kind == VfxContextKind.Update && edge.FromPort >= 1)
				{
					VfxNodeData parentInitialize = FindInitializeOf(source);
					VfxSystemDescription parent;
					if (parentInitialize == null || !systemsByInitialize.TryGetValue(parentInitialize.Id, out parent))
					{
						Error(initialize, "The GPU event source is not part of a compiled system");
						continue;
					}

					if (system.ParentSystem >= 0)
					{
						Error(initialize, "A system spawns from one GPU event at most");
						continue;
					}

					int eventIndex = edge.FromPort - 1;
					if (parent.UpdateWriter == null || eventIndex >= parent.Events.Count)
					{
						Error(initialize, "The parent system has no GPU event on that port (add a Trigger block to its Update context)");
						continue;
					}

					system.ParentSystem = parent.Index;
					system.ParentEvent = eventIndex;
				}
			}

			if (system.Spawners.Count == 0 && system.ParentSystem < 0)
			{
				Warning(initialize, "The system '" + system.Name + "' has no spawn context and no GPU event: it never spawns particles");
			}

			// Update context (one), then outputs (from the update, or from the initialize when there is none).
			VfxNodeData update = null;
			foreach (VfxFlowEdge edge in Graph.GetFlowOutputs(initialize.Id, 0))
			{
				VfxNodeData target = Graph.FindNode(edge.To);
				if (target != null && KindOf(target) == VfxContextKind.Update)
				{
					if (update != null)
					{
						Error(target, "A system has one Update context");
						continue;
					}

					update = target;
				}
			}

			var outputs = new List<VfxNodeData>();
			ulong outputSource = update?.Id ?? initialize.Id;
			foreach (VfxFlowEdge edge in Graph.GetFlowOutputs(outputSource, 0))
			{
				VfxNodeData target = Graph.FindNode(edge.To);
				if (target != null && KindOf(target) == VfxContextKind.Output)
				{
					outputs.Add(target);
				}
			}

			if (update == null)
			{
				foreach (VfxFlowEdge edge in Graph.GetFlowOutputs(initialize.Id, 0))
				{
					VfxNodeData target = Graph.FindNode(edge.To);
					if (target != null && KindOf(target) == VfxContextKind.Output && !outputs.Contains(target))
					{
						outputs.Add(target);
					}
				}
			}

			outputs.Sort((a, b) => a.Position.X != b.Position.X ? a.Position.X.CompareTo(b.Position.X) : a.Position.Y.CompareTo(b.Position.Y));
			if (outputs.Count == 0)
			{
				Warning(initialize, "The system '" + system.Name + "' has no output context: it simulates but draws nothing");
			}

			// Strip outputs need the position history recorded by the update kernel.
			foreach (VfxNodeData output in outputs)
			{
				VfxNodeDefinition definition = VfxNodeLibrary.Find(output.Type);
				if (definition != null && definition.OutputKind == VfxOutputKind.Strip)
				{
					system.UsesStrip = true;
					system.StripLength = Math.Max(system.StripLength, Math.Clamp(output.GetInt("StripLength", 16), 2, 64));
					system.StripSpacing = Math.Max(system.StripSpacing, Math.Max(0f, output.GetFloat("StripSpacing", 0.05f)));
				}
			}

			// Initialize kernel: the context's blocks.
			bool worldSpace = string.Equals(initialize.GetSetting("Space", "Local"), "World", StringComparison.OrdinalIgnoreCase);
			system.InitWriter = new VfxGlslWriter(VfxKernelKind.Initialize, system.Uniforms, system.Resources) { WorldSpace = worldSpace };
			system.BoundsCenter = ResolveInput(initialize, initDefinition.FindInput(initialize, "Bounds Center") ?? new VfxSlotDefinition("Bounds Center", VfxValueType.Vector3));
			system.BoundsSize = ResolveInput(initialize, initDefinition.FindInput(initialize, "Bounds Size") ?? new VfxSlotDefinition("Bounds Size", VfxValueType.Vector3, VfxValue.FromVector3(10f, 10f, 10f)));
			if (!system.BoundsCenter.IsCpuEvaluable || !system.BoundsSize.IsCpuEvaluable)
			{
				Error(initialize, "The bounds must not depend on particle attributes");
			}

			system.UpdateId = update?.Id ?? 0;
			if (update != null)
			{
				system.AgeParticles = update.GetBool("AgeParticles", true);
				system.ReapParticles = update.GetBool("ReapParticles", true);
				system.IntegratePosition = update.GetBool("IntegratePosition", true);
				system.IntegrateAngle = update.GetBool("IntegrateAngle", true);
				system.TrackOldPosition = update.GetBool("TrackOldPosition", false);
			}

			CompileBlocks(system.InitWriter, initialize, VfxKernelKind.Initialize, null);

			system.UpdateWriter = new VfxGlslWriter(VfxKernelKind.Update, system.Uniforms, system.Resources) { WorldSpace = worldSpace };
			if (update != null)
			{
				CompileBlocks(system.UpdateWriter, update, VfxKernelKind.Update, null);
			}

			VfxCodegen.EmitUpdateImplicit(system);

			// Outputs.
			foreach (VfxNodeData outputNode in outputs)
			{
				VfxOutputDescription output = CompileOutput(system, outputNode);
				if (output != null)
				{
					output.Index = system.Outputs.Count;
					system.Outputs.Add(output);
				}
			}

			foreach (VfxGlslWriter writer in AllWriters(system))
			{
				foreach (string error in writer.Errors)
				{
					Error(null, error);
				}

				system.UsesDepth |= writer.UsesDepth;
				system.UsesSdf |= writer.UsesSdf;
			}

			// Which attributes live in the buffer (the sources are assembled once every system is compiled).
			DecideStorage(system);
		}

		private IEnumerable<VfxGlslWriter> AllWriters(VfxSystemDescription system)
		{
			yield return system.InitWriter;
			yield return system.UpdateWriter;
			foreach (VfxOutputDescription output in system.Outputs)
			{
				if (output.Writer != null)
				{
					yield return output.Writer;
				}
			}
		}

		/// <summary>Runs every enabled block of a context through its definition into the writer.</summary>
		private void CompileBlocks(VfxGlslWriter writer, VfxNodeData context, VfxKernelKind kernel, VfxSpawnerDescription spawner)
		{
			foreach (VfxNodeData block in Graph.GetBlocks(context))
			{
				if (!block.Enabled)
				{
					continue;
				}

				VfxNodeDefinition definition = VfxNodeLibrary.Find(block.Type);
				if (definition == null || definition.Kind != VfxNodeKind.Block)
				{
					Error(block, "Unknown block type " + block.Type);
					continue;
				}

				if ((definition.AllowedContexts & VfxNodeLibrary.MaskOf(KindOf(context))) == 0)
				{
					Error(block, definition.Name + " cannot be used in this context");
					continue;
				}

				List<VfxSlotDefinition> slots = definition.GetInputs(block);
				var inputs = new VfxExpression[slots.Count];
				for (int i = 0; i < slots.Count; i++)
				{
					inputs[i] = ResolveInput(block, slots[i]);
					if (kernel == VfxKernelKind.Cpu && !inputs[i].IsCpuEvaluable)
					{
						Error(block, "The input '" + slots[i].DisplayName + "' of a spawn block must not depend on particle data or GPU-only operators");
						inputs[i] = VfxExpression.Const(slots[i].CreateDefault());
					}
				}

				var build = new VfxBlockBuild { Node = block, Definition = definition, Compiler = this, Writer = writer, Kernel = kernel, InputSlots = slots, Inputs = inputs, Spawner = spawner };
				if (writer != null)
				{
					writer.Comment(definition.GetTitle(block));
				}

				try
				{
					definition.Block?.Invoke(build);
				}
				catch (Exception e)
				{
					Error(block, definition.Name + ": " + e.Message);
				}
			}
		}

		private VfxExpression[] BuildOperator(VfxNodeData node, VfxNodeDefinition definition)
		{
			List<VfxSlotDefinition> slots = definition.GetInputs(node);
			var inputs = new VfxExpression[slots.Count];
			for (int i = 0; i < slots.Count; i++)
			{
				inputs[i] = ResolveInput(node, slots[i]);
			}

			var build = new VfxOperatorBuild { Node = node, Definition = definition, Compiler = this, InputSlots = slots, Inputs = inputs };
			VfxExpression[] outputs;
			try
			{
				outputs = definition.Operator != null ? definition.Operator(build) : Array.Empty<VfxExpression>();
			}
			catch (Exception e)
			{
				Error(node, definition.Name + ": " + e.Message);
				outputs = Array.Empty<VfxExpression>();
			}

			// Every declared output gets an expression (a default when the operator produced fewer).
			List<VfxSlotDefinition> declared = definition.GetOutputs(node);
			if (outputs.Length < declared.Count)
			{
				var padded = new VfxExpression[declared.Count];
				for (int i = 0; i < declared.Count; i++)
				{
					padded[i] = i < outputs.Length && outputs[i] != null ? outputs[i] : VfxExpression.Const(declared[i].CreateDefault());
				}

				outputs = padded;
			}

			return outputs;
		}

		private VfxSpawnerDescription CompileSpawner(VfxNodeData spawn)
		{
			VfxNodeDefinition definition = VfxNodeLibrary.Find(spawn.Type);
			var spawner = new VfxSpawnerDescription { NodeId = spawn.Id, Name = definition.GetTitle(spawn) };
			spawner.LoopMode = Parse(spawn.GetSetting("LoopMode"), VfxLoopMode.Infinite);
			spawner.LoopDuration = ResolveCpuInput(spawn, definition, "Loop Duration", VfxValue.FromFloat(1f));
			spawner.LoopCount = ResolveCpuInput(spawn, definition, "Loop Count", VfxValue.FromInt(1));
			spawner.DelayBeforeLoop = ResolveCpuInput(spawn, definition, "Delay Before Loop", VfxValue.FromFloat(0f));
			spawner.DelayAfterLoop = ResolveCpuInput(spawn, definition, "Delay After Loop", VfxValue.FromFloat(0f));

			// Start / stop events: event contexts connected to the ports; none connected to Start means OnPlay.
			foreach (VfxFlowEdge edge in Graph.GetFlowInputs(spawn.Id, 0))
			{
				string eventName = EventNameOf(Graph.FindNode(edge.From));
				if (eventName != null && !spawner.StartEvents.Contains(eventName))
				{
					spawner.StartEvents.Add(eventName);
				}
			}

			foreach (VfxFlowEdge edge in Graph.GetFlowInputs(spawn.Id, 1))
			{
				string eventName = EventNameOf(Graph.FindNode(edge.From));
				if (eventName != null && !spawner.StopEvents.Contains(eventName))
				{
					spawner.StopEvents.Add(eventName);
				}
			}

			if (spawner.StartEvents.Count == 0)
			{
				spawner.StartEvents.Add(OnPlayEvent);
			}

			if (spawner.StopEvents.Count == 0)
			{
				spawner.StopEvents.Add(OnStopEvent);
			}

			CompileBlocks(null, spawn, VfxKernelKind.Cpu, spawner);
			return spawner;
		}

		private string EventNameOf(VfxNodeData node)
		{
			if (node == null || KindOf(node) != VfxContextKind.Event)
			{
				return null;
			}

			string name = node.GetSetting("Event", OnPlayEvent);
			return string.IsNullOrWhiteSpace(name) ? OnPlayEvent : name.Trim();
		}

		private VfxExpression ResolveCpuInput(VfxNodeData node, VfxNodeDefinition definition, string slotName, VfxValue fallback)
		{
			VfxSlotDefinition slot = definition.FindInput(node, slotName);
			if (slot == null)
			{
				return VfxExpression.Const(fallback);
			}

			VfxExpression expression = ResolveInput(node, slot);
			if (!expression.IsCpuEvaluable)
			{
				Error(node, "'" + slotName + "' must not depend on particle data");
				return VfxExpression.Const(fallback);
			}

			return expression;
		}

		private VfxOutputDescription CompileOutput(VfxSystemDescription system, VfxNodeData node)
		{
			VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
			var output = new VfxOutputDescription
			{
				NodeId = node.Id,
				Name = definition.GetTitle(node),
				Kind = definition.OutputKind,
				Blend = Parse(node.GetSetting("Blend"), VfxBlendMode.Alpha),
				Lighting = Parse(node.GetSetting("Lighting"), VfxLightingMode.Unlit),
				Orient = Parse(node.GetSetting("Orient"), VfxOrientMode.FaceCameraPlane),
				Uv = Parse(node.GetSetting("UvMode"), VfxUvMode.Simple),
				Sort = Parse(node.GetSetting("Sort"), VfxSortMode.Auto),
				SoftParticles = node.GetBool("SoftParticles", false),
				CastShadows = node.GetBool("CastShadows", false),
				ReceiveShadows = node.GetBool("ReceiveShadows", true),
				ReceiveGI = node.GetBool("ReceiveGI", true),
				ZWrite = node.GetBool("ZWrite", false),
				CullBack = node.GetBool("CullBack", false),
				TwoSidedLighting = node.GetBool("TwoSidedLighting", true),
				AlphaThreshold = node.GetFloat("AlphaThreshold", 0.5f),
				NormalBending = node.GetFloat("NormalBending", 0f),
				Roughness = node.GetFloat("Roughness", 0.8f),
				StripLength = Math.Clamp(node.GetInt("StripLength", 16), 2, 64),
				StripTile = node.GetBool("StripTile", false),
				LineMode = node.GetSetting("LineMode", "Velocity"),
				Primitive = Parse(node.GetSetting("Primitive"), VfxQuadPrimitive.Quad)
			};
			output.Queue = node.GetInt("Queue", output.IsTransparent ? Shaders.MaterialQueue.Transparent : Shaders.MaterialQueue.Geometry);
			if (output.Blend == VfxBlendMode.Opaque || output.Blend == VfxBlendMode.AlphaClip)
			{
				output.ZWrite = true;
			}

			output.Writer = new VfxGlslWriter(VfxKernelKind.Output, system.Uniforms, system.Resources);
			if (output.Kind == VfxOutputKind.Mesh)
			{
				VfxSlotDefinition meshSlot = definition.FindInput(node, "Mesh");
				output.Mesh = meshSlot != null ? ResolveInput(node, meshSlot) : VfxExpression.Const(new VfxValue(VfxValueType.Mesh));
				if (output.Mesh.Kind != VfxExprKind.Constant && output.Mesh.Kind != VfxExprKind.Property)
				{
					Error(node, "The mesh must be a constant or a property");
				}
			}

			// The output's own inputs (texture, flipbook, soft particle distance, ...) first, then its blocks.
			VfxCodegen.EmitOutputInputs(this, system, output, node, definition);
			CompileBlocks(output.Writer, node, VfxKernelKind.Output, null);
			VfxCodegen.EmitOutputBody(system, output);
			return output;
		}

		/// <summary>
		///     Decides which attributes the buffer holds: those some kernel writes that a later kernel (or the next
		///     frame's update) reads. Attributes never written keep their defaults as constants; attributes written but
		///     never read later are kernel locals.
		/// </summary>
		private void DecideStorage(VfxSystemDescription system)
		{
			var reads = new HashSet<string>(StringComparer.Ordinal);
			var writes = new HashSet<string>(StringComparer.Ordinal);
			foreach (VfxGlslWriter writer in AllWriters(system))
			{
				foreach (KeyValuePair<string, VfxAttribute> custom in writer.CustomAttributes)
				{
					system.Attributes[custom.Key] = custom.Value;
				}
			}

			foreach (VfxAttribute attribute in VfxAttributes.Standard)
			{
				system.Attributes[attribute.Name] = attribute;
			}

			// Reads that matter for storage: update (next frame), outputs and sort keys. Initialize reads see only
			// this kernel's own values, so they never need storage by themselves.
			reads.UnionWith(system.UpdateWriter.AttributesRead);
			foreach (VfxOutputDescription output in system.Outputs)
			{
				reads.UnionWith(output.Writer.AttributesRead);
			}

			writes.UnionWith(system.InitWriter.AttributesWritten);
			writes.UnionWith(system.UpdateWriter.AttributesWritten);
			// Implicit writes of the system itself.
			writes.Add(VfxAttributes.Seed.Name);
			writes.Add(VfxAttributes.ParticleId.Name);
			writes.Add(VfxAttributes.SpawnTime.Name);
			if (system.AgeParticles)
			{
				writes.Add(VfxAttributes.Age.Name);
				reads.Add(VfxAttributes.Age.Name);
			}

			if (system.ReapParticles)
			{
				reads.Add(VfxAttributes.Lifetime.Name);
				reads.Add(VfxAttributes.Age.Name);
			}

			if (system.IntegratePosition && writes.Contains(VfxAttributes.Velocity.Name))
			{
				reads.Add(VfxAttributes.Velocity.Name);
				reads.Add(VfxAttributes.Position.Name);
				writes.Add(VfxAttributes.Position.Name);
			}

			if (system.IntegrateAngle && writes.Contains(VfxAttributes.AngularVelocity.Name))
			{
				reads.Add(VfxAttributes.AngularVelocity.Name);
				reads.Add(VfxAttributes.Angle.Name);
				writes.Add(VfxAttributes.Angle.Name);
			}

			if (system.TrackOldPosition || reads.Contains(VfxAttributes.OldPosition.Name))
			{
				writes.Add(VfxAttributes.OldPosition.Name);
				reads.Add(VfxAttributes.OldPosition.Name);
				reads.Add(VfxAttributes.Position.Name);
			}

			if (system.UsesStrip)
			{
				writes.Add(VfxAttributes.StripHead.Name);
				writes.Add(VfxAttributes.StripCount.Name);
				reads.Add(VfxAttributes.StripHead.Name);
				reads.Add(VfxAttributes.StripCount.Name);
				reads.Add(VfxAttributes.Position.Name);
			}

			// Position is always stored: every output and the bounds read it.
			writes.Add(VfxAttributes.Position.Name);
			reads.Add(VfxAttributes.Position.Name);

			// Random numbers in update or output kernels read the seed.
			if (system.UpdateWriter.UsesRandom)
			{
				reads.Add(VfxAttributes.Seed.Name);
			}

			foreach (VfxOutputDescription output in system.Outputs)
			{
				if (output.Writer.UsesRandom)
				{
					reads.Add(VfxAttributes.Seed.Name);
				}
			}

			int plane = 0;
			var names = new List<string>();
			foreach (string name in reads)
			{
				if (writes.Contains(name) && name != VfxAttributes.Alive.Name)
				{
					names.Add(name);
				}
			}

			names.Sort(StringComparer.Ordinal);
			foreach (string name in names)
			{
				VfxAttribute attribute;
				if (!system.Attributes.TryGetValue(name, out attribute))
				{
					continue;
				}

				int components = Math.Max(1, VfxValueTypes.Components(attribute.Type));
				system.Stored.Add(new VfxStoredAttribute { Attribute = attribute, Plane = plane, Components = components });
				plane += components;
			}

			system.Planes = plane;
		}

		private static TEnum Parse<TEnum>(string text, TEnum fallback)
			where TEnum : struct
		{
			TEnum value;
			return text != null && Enum.TryParse(text, true, out value) ? value : fallback;
		}

		#endregion
	}
}
