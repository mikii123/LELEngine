using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>
	///     Resolves the parameter ids of one controller asset to the slots of a player: the root asset's parameters
	///     map to their own slots; a sub-machine playing another asset gets a scope mapping that asset's ids to the
	///     parent slots they are fed from (<see cref="ParameterMapping" />) or to private slots of their own.
	/// </summary>
	public sealed class ParameterScope
	{
		#region PublicFields

		/// <summary>The asset whose parameter ids this scope resolves.</summary>
		public readonly AnimatorController Asset;

		/// <summary>"Combat/" for the parameters private to the sub-machine Combat (empty for the root).</summary>
		public readonly string Prefix;

		#endregion

		#region PrivateFields

		private readonly Dictionary<ulong, int> byId = new Dictionary<ulong, int>();

		#endregion

		#region Constructors

		internal ParameterScope(AnimatorController asset, string prefix)
		{
			Asset = asset;
			Prefix = prefix;
		}

		#endregion

		#region PublicMethods

		/// <summary>The player slot of a parameter id of the scope's asset (-1: none).</summary>
		public int Index(ulong parameterId)
		{
			return parameterId != 0 && byId.TryGetValue(parameterId, out int index) ? index : -1;
		}

		internal void Map(ulong parameterId, int index)
		{
			byId[parameterId] = index;
		}

		#endregion
	}

	/// <summary>
	///     An <see cref="AnimatorController" /> compiled for playback: layers and state machines flattened into arrays,
	///     ids resolved to objects, parameters indexed, blend trees prepared, the sub-machines playing other controller
	///     assets expanded in place with their parameters scoped. Immutable and shared by every animator playing the
	///     controller; rebuilt when the controller or any asset it embeds changes.
	/// </summary>
	public sealed class RuntimeController
	{
		#region PublicFields

		public readonly AnimatorController Source;

		/// <summary>The root asset's parameters, then the private parameters of the embedded assets ("Node/Parameter").</summary>
		public readonly List<AnimatorParameter> Parameters = new List<AnimatorParameter>();

		public readonly RuntimeLayer[] Layers;
		public readonly List<AnimationClip> Clips = new List<AnimationClip>();
		public readonly List<RuntimeTransition> Transitions = new List<RuntimeTransition>();
		public readonly List<RuntimeState> States = new List<RuntimeState>();

		/// <summary>The root asset and the assets its sub-machines play, with the versions this build saw.</summary>
		public readonly List<AnimatorController> Assets = new List<AnimatorController>();

		/// <summary>Problems met while compiling (a sub-machine asset embedding its embedder, a missing layer): logged once per animator.</summary>
		public readonly List<string> Warnings = new List<string>();

		public readonly ParameterScope RootScope;

		/// <summary>A <see cref="ScriptCondition" /> somewhere: this controller's animators evaluate on the main thread.</summary>
		public bool HasScriptConditions { get; private set; }

		/// <summary>Any state plays a clip animating the root transform.</summary>
		public bool HasRootMotion { get; private set; }

		/// <summary>The version of the root asset the build saw (the sum with the embedded assets: <see cref="IsCurrent" />).</summary>
		public int Version { get; }

		/// <summary>True while none of the assets changed since the build.</summary>
		public bool IsCurrent
		{
			get
			{
				for (int i = 0; i < Assets.Count; i++)
				{
					if (Assets[i].Version != versions[i])
					{
						return false;
					}
				}

				return true;
			}
		}

		#endregion

		#region PrivateFields

		private static readonly ConditionalWeakTable<AnimatorController, RuntimeController> cache = new ConditionalWeakTable<AnimatorController, RuntimeController>();
		private readonly Dictionary<string, int> parameterByName = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly List<int> versions = new List<int>();

		#endregion

		#region Constructors

		private RuntimeController(AnimatorController source)
		{
			Source = source;
			Version = source.Version;
			NoteAsset(source);
			RootScope = new ParameterScope(source, "");
			foreach (AnimatorParameter parameter in source.parameters)
			{
				RootScope.Map(parameter.id, AddParameter(parameter));
			}

			var layers = new List<RuntimeLayer>();
			var stack = new List<AnimatorController> { source };
			for (int i = 0; i < source.layers.Count; i++)
			{
				layers.Add(new RuntimeLayer(this, source.layers[i], i, stack));
			}

			Layers = layers.ToArray();
			foreach (RuntimeLayer layer in Layers)
			{
				layer.Link();
			}

			foreach (RuntimeState state in States)
			{
				state.Motion?.CollectClips(Clips);
				if (state.Motion != null && state.Motion.HasRootMotion)
				{
					HasRootMotion = true;
				}
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>The compiled form of a controller (compiled on first use and after every change of it or an embedded asset). Main thread.</summary>
		public static RuntimeController Get(AnimatorController controller)
		{
			if (controller == null)
			{
				return null;
			}

			lock (cache)
			{
				if (cache.TryGetValue(controller, out RuntimeController compiled) && compiled.IsCurrent)
				{
					return compiled;
				}

				compiled = new RuntimeController(controller);
				cache.AddOrUpdate(controller, compiled);
				return compiled;
			}
		}

		/// <summary>The slot of a parameter by name: the root's names, and "Node/Parameter" for a sub-machine asset's private ones.</summary>
		public int ParameterIndex(string parameterName)
		{
			return parameterName != null && parameterByName.TryGetValue(parameterName, out int index) ? index : -1;
		}

		/// <summary>The slot of a root parameter id (embedded assets' ids resolve through their machine's scope).</summary>
		public int ParameterIndex(ulong parameterId)
		{
			return RootScope.Index(parameterId);
		}

		#endregion

		#region InternalMethods

		internal void NoteScriptCondition()
		{
			HasScriptConditions = true;
		}

		internal void NoteAsset(AnimatorController asset)
		{
			if (!Assets.Contains(asset))
			{
				Assets.Add(asset);
				versions.Add(asset.Version);
			}
		}

		internal int AddParameter(AnimatorParameter parameter)
		{
			int index = Parameters.Count;
			Parameters.Add(parameter);
			if (!parameterByName.ContainsKey(parameter.name ?? ""))
			{
				parameterByName[parameter.name ?? ""] = index;
			}

			return index;
		}

		#endregion
	}

	public sealed class RuntimeLayer
	{
		#region PublicFields

		public readonly RuntimeController Controller;
		public readonly AnimatorLayer Source;
		public readonly int Index;
		public readonly string Name;
		public readonly RuntimeMachine Root;
		public readonly List<RuntimeState> States = new List<RuntimeState>();
		public readonly List<RuntimeMachine> Machines = new List<RuntimeMachine>();
		public LayerBlendingMode Blending => Source.blending;
		public AnimationMask Mask => Source.mask;
		public float DefaultWeight => Index == 0 ? 1f : Source.weight;
		public bool Enabled => Source.enabled;

		#endregion

		#region PrivateFields

		private readonly Dictionary<ulong, RuntimeState> statesById = new Dictionary<ulong, RuntimeState>();
		private readonly Dictionary<string, RuntimeState> statesByName = new Dictionary<string, RuntimeState>(StringComparer.Ordinal);
		private readonly Dictionary<ulong, RuntimeMachine> machinesById = new Dictionary<ulong, RuntimeMachine>();
		private readonly List<(RuntimeTransition transition, AnimatorTransition source)> unresolved = new List<(RuntimeTransition, AnimatorTransition)>();

		#endregion

		#region Constructors

		internal RuntimeLayer(RuntimeController controller, AnimatorLayer source, int index, List<AnimatorController> stack)
		{
			Controller = controller;
			Source = source;
			Index = index;
			Name = source.name;
			Root = new RuntimeMachine(this, source.stateMachine, null, "", controller.RootScope, stack);
		}

		#endregion

		#region PublicMethods

		public RuntimeState FindState(ulong id)
		{
			return statesById.TryGetValue(id, out RuntimeState state) ? state : null;
		}

		/// <summary>A state by full path ("Sub/Idle") or, when unique, by its short name.</summary>
		public RuntimeState FindState(string name)
		{
			return name != null && statesByName.TryGetValue(name, out RuntimeState state) ? state : null;
		}

		public RuntimeMachine FindMachine(ulong id)
		{
			return machinesById.TryGetValue(id, out RuntimeMachine machine) ? machine : null;
		}

		#endregion

		#region InternalMethods

		internal void Register(RuntimeState state)
		{
			States.Add(state);
			state.Index = Controller.States.Count;
			Controller.States.Add(state);
			statesById[state.Id] = state;
			statesByName[state.FullName] = state;
			if (!statesByName.ContainsKey(state.Name))
			{
				statesByName[state.Name] = state;
			}
			else if (statesByName[state.Name] != state && statesByName[state.Name].FullName != state.Name)
			{
				// Two states share a short name: neither is found by it.
				statesByName.Remove(state.Name);
			}
		}

		internal void Register(RuntimeMachine machine)
		{
			Machines.Add(machine);
			machinesById[machine.Id] = machine;
		}

		internal RuntimeTransition Register(AnimatorTransition source, RuntimeMachine machine, RuntimeState from, bool anyState, int order)
		{
			var transition = new RuntimeTransition(source, machine, from, anyState, order) { Index = Controller.Transitions.Count };
			Controller.Transitions.Add(transition);
			unresolved.Add((transition, source));
			foreach (AnimatorCondition condition in source.conditions)
			{
				NoteConditions(condition);
			}

			return transition;
		}

		/// <summary>Second pass: destinations resolved to states and machines, any-state lists collected per state.</summary>
		internal void Link()
		{
			foreach ((RuntimeTransition transition, AnimatorTransition source) in unresolved)
			{
				transition.Destination = source.destinationState != 0 ? FindState(source.destinationState) : null;
				transition.DestinationMachine = source.destinationMachine != 0 ? FindMachine(source.destinationMachine) : null;
				transition.ToExit = source.toExit;
			}

			unresolved.Clear();
			foreach (RuntimeState state in States)
			{
				var any = new List<RuntimeTransition>();
				for (RuntimeMachine machine = state.Machine; machine != null; machine = machine.Parent)
				{
					any.AddRange(machine.AnyTransitions);
				}

				state.AnyTransitions = any.ToArray();
				state.Transitions = ApplySoloMute(state.Transitions);
			}

			foreach (RuntimeMachine machine in Machines)
			{
				machine.AnyTransitions = ApplySoloMute(machine.AnyTransitions);
				machine.EntryTransitions = ApplySoloMute(machine.EntryTransitions);
				machine.ExitTransitions = ApplySoloMute(machine.ExitTransitions);
				ulong defaultId = machine.Content?.defaultState ?? 0;
				machine.DefaultState = defaultId != 0 ? FindState(defaultId) : null;
				if (machine.DefaultState == null || machine.DefaultState.Machine != machine)
				{
					machine.DefaultState = machine.States.Count > 0 ? machine.States[0] : null;
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void NoteConditions(AnimatorCondition condition)
		{
			switch (condition)
			{
				case ScriptCondition _:
					Controller.NoteScriptCondition();
					break;
				case ConditionGroup group:
					foreach (AnimatorCondition nested in group.conditions)
					{
						NoteConditions(nested);
					}

					break;
			}
		}

		private static RuntimeTransition[] ApplySoloMute(RuntimeTransition[] transitions)
		{
			bool anySolo = false;
			foreach (RuntimeTransition transition in transitions)
			{
				anySolo |= transition.Source.solo && !transition.Source.mute;
			}

			var result = new List<RuntimeTransition>();
			foreach (RuntimeTransition transition in transitions)
			{
				if (transition.Source.mute)
				{
					continue;
				}

				if (anySolo && !transition.Source.solo)
				{
					continue;
				}

				result.Add(transition);
			}

			return result.ToArray();
		}

		#endregion
	}

	public sealed class RuntimeMachine
	{
		#region PublicFields

		public readonly RuntimeLayer Layer;

		/// <summary>The machine as written in the embedding controller (the node: its name, position and exit transitions).</summary>
		public readonly AnimatorStateMachine Source;

		/// <summary>The machine whose states play here: <see cref="Source" />, or the asset's machine when the node plays another controller (null: cut, empty).</summary>
		public readonly AnimatorStateMachine Content;

		/// <summary>The asset <see cref="Content" /> belongs to.</summary>
		public readonly AnimatorController Asset;

		public readonly RuntimeMachine Parent;
		public readonly ulong Id;
		public readonly string Name;

		/// <summary>"Sub/Inner" from the layer root (empty for the root).</summary>
		public readonly string Path;

		/// <summary>Resolves the parameter ids used by this node itself (its exit transitions in the parent).</summary>
		public readonly ParameterScope Scope;

		/// <summary>Resolves the parameter ids used inside (states, any-state and entry transitions, nested machines).</summary>
		public readonly ParameterScope ContentScope;

		public readonly List<RuntimeState> States = new List<RuntimeState>();
		public readonly List<RuntimeMachine> SubMachines = new List<RuntimeMachine>();
		public RuntimeTransition[] AnyTransitions;
		public RuntimeTransition[] EntryTransitions;

		/// <summary>The transitions from this machine's node in the parent (taken when a state inside exits).</summary>
		public RuntimeTransition[] ExitTransitions;

		public RuntimeState DefaultState;
		public bool IsRoot => Parent == null;

		/// <summary>The node plays another controller asset.</summary>
		public bool IsExternal => Source.IsExternal;

		#endregion

		#region Constructors

		internal RuntimeMachine(RuntimeLayer layer, AnimatorStateMachine source, RuntimeMachine parent, string path, ParameterScope scope, List<AnimatorController> stack)
		{
			Layer = layer;
			Source = source;
			Parent = parent;
			Id = source.id;
			Name = source.name;
			Path = path;
			Scope = scope;
			ContentScope = scope;
			Content = source;
			Asset = scope.Asset;
			RuntimeController controller = layer.Controller;
			if (source.IsExternal)
			{
				AnimatorController asset = source.controller;
				AnimatorStateMachine external = source.ExternalMachine;
				if (stack.Contains(asset))
				{
					controller.Warnings.Add($"Sub-state machine '{Path}' plays '{asset.name}', which embeds its embedder: the loop is cut, it plays nothing.");
					Content = null;
				}
				else if (external == null)
				{
					controller.Warnings.Add($"Sub-state machine '{Path}': '{asset.name}' has no layer {source.controllerLayer}; it plays nothing.");
					Content = null;
				}
				else
				{
					Content = external;
					Asset = asset;
					controller.NoteAsset(asset);
					ContentScope = BuildScope(controller, source, asset, scope, path);
				}
			}

			layer.Register(this);
			if (Content == null)
			{
				AnyTransitions = Array.Empty<RuntimeTransition>();
				EntryTransitions = Array.Empty<RuntimeTransition>();
				ExitTransitions = RegisterExit(layer, source, parent);
				return;
			}

			if (Content != source)
			{
				stack.Add(Asset);
			}

			foreach (AnimatorState stateSource in Content.states)
			{
				var state = new RuntimeState(this, stateSource, path.Length == 0 ? stateSource.name : path + "/" + stateSource.name);
				States.Add(state);
				layer.Register(state);
			}

			foreach (AnimatorStateMachine sub in Content.subMachines)
			{
				SubMachines.Add(new RuntimeMachine(layer, sub, this, path.Length == 0 ? sub.name : path + "/" + sub.name, ContentScope, stack));
			}

			if (Content != source)
			{
				stack.Remove(Asset);
			}

			// Transitions (registered after every state exists in this machine; destinations link later).
			foreach (RuntimeState state in States)
			{
				var list = new List<RuntimeTransition>();
				for (int i = 0; i < state.Source.transitions.Count; i++)
				{
					list.Add(layer.Register(state.Source.transitions[i], this, state, false, i));
				}

				state.Transitions = list.ToArray();
			}

			var any = new List<RuntimeTransition>();
			for (int i = 0; i < Content.anyStateTransitions.Count; i++)
			{
				any.Add(layer.Register(Content.anyStateTransitions[i], this, null, true, i));
			}

			AnyTransitions = any.ToArray();
			var entry = new List<RuntimeTransition>();
			for (int i = 0; i < Content.entryTransitions.Count; i++)
			{
				entry.Add(layer.Register(Content.entryTransitions[i], this, null, false, i));
			}

			EntryTransitions = entry.ToArray();
			ExitTransitions = RegisterExit(layer, source, parent);
		}

		#endregion

		#region PrivateMethods

		/// <summary>The exit transitions live on the node in the parent: registered with the parent (its scope), or the root itself.</summary>
		private RuntimeTransition[] RegisterExit(RuntimeLayer layer, AnimatorStateMachine source, RuntimeMachine parent)
		{
			var exit = new List<RuntimeTransition>();
			for (int i = 0; i < source.transitions.Count; i++)
			{
				exit.Add(layer.Register(source.transitions[i], parent ?? this, null, false, i));
			}

			return exit.ToArray();
		}

		/// <summary>
		///     The scope of an embedded asset: mapped parameters share the parent's slots (same value type), the others
		///     get private slots named "Node/Parameter" with the asset's defaults.
		/// </summary>
		private static ParameterScope BuildScope(RuntimeController controller, AnimatorStateMachine node, AnimatorController asset, ParameterScope parentScope, string path)
		{
			var scope = new ParameterScope(asset, path + "/");
			foreach (AnimatorParameter child in asset.parameters)
			{
				ParameterMapping mapping = node.FindMapping(child.id);
				int parentIndex = mapping != null ? parentScope.Index(mapping.parentParameter) : -1;
				if (parentIndex >= 0 && controller.Parameters[parentIndex].ValueType == child.ValueType && controller.Parameters[parentIndex].IsTrigger == child.IsTrigger)
				{
					scope.Map(child.id, parentIndex);
					continue;
				}

				if (mapping != null && mapping.parentParameter != 0)
				{
					controller.Warnings.Add($"Sub-state machine '{path}': the parameter '{child.name}' of '{asset.name}' cannot be fed from the mapped parameter (missing or another type); it keeps its own value.");
				}

				AnimatorParameter copy = child.Clone();
				copy.name = path + "/" + child.name;
				scope.Map(child.id, controller.AddParameter(copy));
			}

			return scope;
		}

		#endregion
	}

	public sealed class RuntimeState
	{
		#region PublicFields

		public readonly RuntimeMachine Machine;
		public readonly AnimatorState Source;
		public readonly ulong Id;
		public readonly string Name;
		public readonly string FullName;
		public readonly int NameHash;
		public readonly int FullNameHash;
		public readonly int TagHash;
		public readonly RuntimeMotion Motion;
		public readonly int SpeedParameter;
		public readonly int MotionTimeParameter;
		public readonly int CycleOffsetParameter;
		public int Index;
		public RuntimeTransition[] Transitions = Array.Empty<RuntimeTransition>();

		/// <summary>Any-state transitions that apply here: the state's machine's first, then the parents'.</summary>
		public RuntimeTransition[] AnyTransitions = Array.Empty<RuntimeTransition>();

		public float Speed => Source.speed;
		public float CycleOffset => Source.cycleOffset;
		public bool WriteDefaults => Source.writeDefaults;
		public string Tag => Source.tag ?? "";
		public RuntimeLayer Layer => Machine.Layer;

		/// <summary>The motion's nominal length in seconds (a blend tree: weights decide at playback; here the average).</summary>
		public float Length => Motion?.NominalLength ?? 0f;

		public bool Loop
		{
			get
			{
				switch (Source.loop)
				{
					case StateLoopMode.Loop: return true;
					case StateLoopMode.Once: return false;
					default: return Motion != null && Motion.Loop;
				}
			}
		}

		#endregion

		#region Constructors

		internal RuntimeState(RuntimeMachine machine, AnimatorState source, string fullName)
		{
			Machine = machine;
			Source = source;
			Id = source.id;
			Name = source.name ?? "";
			FullName = fullName;
			NameHash = Animator.StringToHash(Name);
			FullNameHash = Animator.StringToHash(machine.Layer.Name + "." + fullName);
			TagHash = Animator.StringToHash(Tag);
			ParameterScope scope = machine.ContentScope;
			Motion = RuntimeMotion.Create(source.motion, scope);
			SpeedParameter = scope.Index(source.speedParameter);
			MotionTimeParameter = scope.Index(source.motionTimeParameter);
			CycleOffsetParameter = scope.Index(source.cycleOffsetParameter);
		}

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return FullName;
		}

		#endregion
	}

	public sealed class RuntimeTransition
	{
		#region PublicFields

		public readonly AnimatorTransition Source;

		/// <summary>The machine whose lists hold the transition (its <see cref="RuntimeMachine.ContentScope" /> resolves the conditions' parameters).</summary>
		public readonly RuntimeMachine Machine;

		/// <summary>The source state (null for any-state, entry and machine exit transitions).</summary>
		public readonly RuntimeState From;

		public readonly bool IsAnyState;

		/// <summary>Position in its list (ordered interruption).</summary>
		public readonly int Order;

		public int Index;
		public RuntimeState Destination;
		public RuntimeMachine DestinationMachine;
		public bool ToExit;

		public ParameterScope Scope => Machine.ContentScope;
		public bool HasExitTime => Source.hasExitTime;
		public float ExitTime => Source.exitTime;
		public bool ExitTimeInSeconds => Source.exitTimeInSeconds;
		public bool FixedDuration => Source.fixedDuration;
		public float Duration => Source.duration;
		public float Offset => Source.offset;
		public TransitionInterruptionSource Interruption => Source.interruptionSource;
		public bool OrderedInterruption => Source.orderedInterruption;
		public bool CanTransitionToSelf => Source.canTransitionToSelf;
		public string Name => Source.name ?? "";
		public int NameHash => Animator.StringToHash(Name);
		public bool HasConditions => Source.conditions.Count > 0;

		#endregion

		#region Constructors

		internal RuntimeTransition(AnimatorTransition source, RuntimeMachine machine, RuntimeState from, bool anyState, int order)
		{
			Source = source;
			Machine = machine;
			From = from;
			IsAnyState = anyState;
			Order = order;
		}

		#endregion

		#region PublicMethods

		public string DescribeDestination()
		{
			if (ToExit) return "Exit";
			if (Destination != null) return Destination.FullName;
			if (DestinationMachine != null) return DestinationMachine.Name;
			return "?";
		}

		public override string ToString()
		{
			return (From?.Name ?? (IsAnyState ? "Any State" : Machine?.Name ?? "?")) + " -> " + DescribeDestination();
		}

		#endregion
	}

	/// <summary>A state's motion prepared for playback: a clip, or a blend tree with its parameters resolved.</summary>
	public abstract class RuntimeMotion
	{
		#region PublicFields

		/// <summary>Length without blending (a tree: the plain average of its children).</summary>
		public abstract float NominalLength { get; }

		public abstract bool Loop { get; }
		public abstract bool HasRootMotion { get; }

		#endregion

		#region PublicMethods

		public static RuntimeMotion Create(Motion motion, ParameterScope scope)
		{
			switch (motion)
			{
				case ClipMotion clipMotion:
					return clipMotion.clip != null ? new RuntimeClipMotion(clipMotion.clip) : null;
				case BlendTree tree:
					return new RuntimeBlendTree(tree, scope);
				default:
					return null;
			}
		}

		public abstract void CollectClips(List<AnimationClip> clips);

		#endregion
	}

	public sealed class RuntimeClipMotion : RuntimeMotion
	{
		public readonly AnimationClip Clip;

		public RuntimeClipMotion(AnimationClip clip)
		{
			Clip = clip;
		}

		public override float NominalLength => Clip.length;
		public override bool Loop => Clip.loop;

		public override bool HasRootMotion
		{
			get
			{
				foreach (AnimationBinding binding in Clip.bindings)
				{
					if (string.IsNullOrEmpty(binding.path) && binding.IsTransform && binding.property != AnimationBinder.LocalScale)
					{
						return true;
					}
				}

				return false;
			}
		}

		public override void CollectClips(List<AnimationClip> clips)
		{
			if (!clips.Contains(Clip))
			{
				clips.Add(Clip);
			}
		}
	}

	public sealed class RuntimeBlendTree : RuntimeMotion
	{
		#region PublicFields

		public sealed class Child
		{
			public RuntimeMotion Motion;
			public float Threshold;
			public Vector2 Position;
			public float TimeScale;
			public int DirectParameter;
		}

		public readonly BlendTree Source;
		public readonly BlendTreeType Type;
		public readonly int ParameterX;
		public readonly int ParameterY;
		public readonly Child[] Children;

		/// <summary>The number of leaf clips below this tree (all nesting levels): the size of a weight buffer.</summary>
		public readonly int LeafCount;

		#endregion

		#region Constructors

		public RuntimeBlendTree(BlendTree source, ParameterScope scope)
		{
			Source = source;
			Type = source.blendType;
			ParameterX = scope.Index(source.parameter);
			ParameterY = scope.Index(source.parameterY);
			var children = new List<Child>();
			foreach (BlendTreeChild child in source.children)
			{
				RuntimeMotion motion = Create(child.motion, scope);
				if (motion == null)
				{
					continue;
				}

				children.Add(new Child
				{
					Motion = motion, Threshold = child.threshold, Position = child.position, TimeScale = child.timeScale > 1e-5f || child.timeScale < -1e-5f ? child.timeScale : 1f,
					DirectParameter = scope.Index(child.directParameter)
				});
			}

			if (Type == BlendTreeType.Simple1D)
			{
				children.Sort((a, b) => a.Threshold.CompareTo(b.Threshold));
			}

			Children = children.ToArray();
			LeafCount = 0;
			foreach (Child child in Children)
			{
				LeafCount += child.Motion is RuntimeBlendTree nested ? nested.LeafCount : 1;
			}
		}

		#endregion

		#region PublicMethods

		public override float NominalLength
		{
			get
			{
				if (Children.Length == 0)
				{
					return 0f;
				}

				float sum = 0f;
				foreach (Child child in Children)
				{
					sum += child.Motion.NominalLength / Math.Abs(child.TimeScale);
				}

				return sum / Children.Length;
			}
		}

		public override bool Loop
		{
			get
			{
				foreach (Child child in Children)
				{
					if (child.Motion.Loop)
					{
						return true;
					}
				}

				return false;
			}
		}

		public override bool HasRootMotion
		{
			get
			{
				foreach (Child child in Children)
				{
					if (child.Motion.HasRootMotion)
					{
						return true;
					}
				}

				return false;
			}
		}

		public override void CollectClips(List<AnimationClip> clips)
		{
			foreach (Child child in Children)
			{
				child.Motion.CollectClips(clips);
			}
		}

		/// <summary>The children's weights for the parameter values (sum 1 unless Direct); <paramref name="weights" /> has at least Children.Length entries.</summary>
		public void ComputeWeights(float x, float y, ParameterState[] parameters, float[] weights)
		{
			int n = Children.Length;
			for (int i = 0; i < n; i++)
			{
				weights[i] = 0f;
			}

			if (n == 0)
			{
				return;
			}

			switch (Type)
			{
				case BlendTreeType.Simple1D:
					BlendTreeMath.Weights1D(Children, x, weights);
					break;
				case BlendTreeType.SimpleDirectional2D:
					BlendTreeMath.WeightsSimpleDirectional(Children, new Vector2(x, y), weights);
					break;
				case BlendTreeType.FreeformDirectional2D:
					BlendTreeMath.WeightsFreeformDirectional(Children, new Vector2(x, y), weights);
					break;
				case BlendTreeType.FreeformCartesian2D:
					BlendTreeMath.WeightsFreeformCartesian(Children, new Vector2(x, y), weights);
					break;
				case BlendTreeType.Direct:
					for (int i = 0; i < n; i++)
					{
						int p = Children[i].DirectParameter;
						weights[i] = p >= 0 && p < parameters.Length ? Math.Max(0f, parameters[p].AsFloat()) : 0f;
					}

					break;
			}
		}

		#endregion
	}

	/// <summary>The blend weight algorithms of the tree types (Unity's semantics; gradient band interpolation for freeform).</summary>
	public static class BlendTreeMath
	{
		#region PublicMethods

		public static void Weights1D(RuntimeBlendTree.Child[] children, float x, float[] weights)
		{
			int n = children.Length;
			if (n == 1)
			{
				weights[0] = 1f;
				return;
			}

			if (x <= children[0].Threshold)
			{
				weights[0] = 1f;
				return;
			}

			if (x >= children[n - 1].Threshold)
			{
				weights[n - 1] = 1f;
				return;
			}

			for (int i = 0; i < n - 1; i++)
			{
				float a = children[i].Threshold;
				float b = children[i + 1].Threshold;
				if (x >= a && x <= b)
				{
					float t = b - a > 1e-6f ? (x - a) / (b - a) : 0f;
					weights[i] = 1f - t;
					weights[i + 1] = t;
					return;
				}
			}
		}

		/// <summary>Two neighbours by angle around the input, the centre child (at the origin) by the input's magnitude.</summary>
		public static void WeightsSimpleDirectional(RuntimeBlendTree.Child[] children, Vector2 input, float[] weights)
		{
			int n = children.Length;
			int center = -1;
			var ring = new List<int>();
			for (int i = 0; i < n; i++)
			{
				if (children[i].Position.LengthSquared < 1e-8f)
				{
					if (center < 0) center = i;
				}
				else
				{
					ring.Add(i);
				}
			}

			float magnitude = input.Length;
			if (ring.Count == 0)
			{
				if (center >= 0) weights[center] = 1f;
				else weights[0] = 1f;
				return;
			}

			if (magnitude < 1e-5f)
			{
				if (center >= 0)
				{
					weights[center] = 1f;
				}
				else
				{
					// No centre child: share equally (Unity blends all ring children).
					foreach (int i in ring) weights[i] = 1f / ring.Count;
				}

				return;
			}

			float angle = (float)Math.Atan2(input.Y, input.X);
			ring.Sort((a, b) => Angle(children[a].Position).CompareTo(Angle(children[b].Position)));
			int count = ring.Count;
			for (int k = 0; k < count; k++)
			{
				float a = Angle(children[ring[k]].Position);
				float b = Angle(children[ring[(k + 1) % count]].Position);
				float span = b - a;
				if (span <= 0f) span += 2f * (float)Math.PI;
				float offset = angle - a;
				if (offset < 0f) offset += 2f * (float)Math.PI;
				if (offset <= span + 1e-6f)
				{
					int lower = ring[k];
					int upper = ring[(k + 1) % count];
					float t = span > 1e-6f ? offset / span : 0f;
					if (count == 1)
					{
						lower = upper = ring[0];
						t = 0f;
					}

					float radius = children[lower].Position.Length * (1f - t) + children[upper].Position.Length * t;
					float radial = radius > 1e-6f ? Math.Min(magnitude / radius, 1f) : 1f;
					float centerWeight = center >= 0 ? 1f - radial : 0f;
					if (center < 0)
					{
						radial = 1f;
					}

					weights[lower] += (1f - t) * radial;
					weights[upper] += t * radial;
					if (center >= 0)
					{
						weights[center] += centerWeight;
					}

					return;
				}
			}

			if (center >= 0) weights[center] = 1f;
			else weights[ring[0]] = 1f;
		}

		/// <summary>Gradient band interpolation in the plane (Rune Skovbo Johansen): every child's influence shrinks towards each other child.</summary>
		public static void WeightsFreeformCartesian(RuntimeBlendTree.Child[] children, Vector2 input, float[] weights)
		{
			int n = children.Length;
			float total = 0f;
			for (int i = 0; i < n; i++)
			{
				Vector2 pi = children[i].Position;
				Vector2 pip = input - pi;
				float weight = 1f;
				for (int j = 0; j < n; j++)
				{
					if (j == i) continue;
					Vector2 pij = children[j].Position - pi;
					float lengthSquared = pij.LengthSquared;
					float w = lengthSquared > 1e-10f ? 1f - Vector2.Dot(pip, pij) / lengthSquared : 0f;
					weight = Math.Min(weight, Math.Clamp(w, 0f, 1f));
				}

				weights[i] = weight;
				total += weight;
			}

			Finish(weights, n, total);
		}

		/// <summary>Gradient band interpolation in polar space: magnitude differences scaled by the mean magnitude, angles in radians x 2.</summary>
		public static void WeightsFreeformDirectional(RuntimeBlendTree.Child[] children, Vector2 input, float[] weights)
		{
			int n = children.Length;
			float total = 0f;
			float inputMagnitude = input.Length;
			for (int i = 0; i < n; i++)
			{
				Vector2 pi = children[i].Position;
				float weight = 1f;
				for (int j = 0; j < n; j++)
				{
					if (j == i) continue;
					Vector2 pj = children[j].Position;
					Vector2 pij = PolarDifference(pi, pj);
					Vector2 pip = PolarDifference(pi, input, inputMagnitude);
					float lengthSquared = pij.LengthSquared;
					float w = lengthSquared > 1e-10f ? 1f - Vector2.Dot(pip, pij) / lengthSquared : 0f;
					weight = Math.Min(weight, Math.Clamp(w, 0f, 1f));
				}

				weights[i] = weight;
				total += weight;
			}

			Finish(weights, n, total);
		}

		#endregion

		#region PrivateMethods

		private static void Finish(float[] weights, int n, float total)
		{
			if (total <= 1e-8f)
			{
				// Outside every band: the nearest child alone.
				if (n > 0) weights[0] = 1f;
				return;
			}

			for (int i = 0; i < n; i++)
			{
				weights[i] /= total;
			}
		}

		private static float Angle(Vector2 v)
		{
			return (float)Math.Atan2(v.Y, v.X);
		}

		private static Vector2 PolarDifference(Vector2 a, Vector2 b)
		{
			return PolarDifference(a, b, b.Length);
		}

		private static Vector2 PolarDifference(Vector2 a, Vector2 b, float bMagnitude)
		{
			float aMagnitude = a.Length;
			float mean = (aMagnitude + bMagnitude) * 0.5f;
			float magnitudeDelta = mean > 1e-6f ? (bMagnitude - aMagnitude) / mean : 0f;
			float angle;
			if (aMagnitude < 1e-6f || bMagnitude < 1e-6f)
			{
				angle = 0f;
			}
			else
			{
				float cross = a.X * b.Y - a.Y * b.X;
				float dot = Vector2.Dot(a, b);
				angle = (float)Math.Atan2(cross, dot);
			}

			return new Vector2(magnitudeDelta, angle * 2f);
		}

		#endregion
	}
}
