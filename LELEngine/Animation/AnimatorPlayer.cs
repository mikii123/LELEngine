using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>
	///     The playback state of one <see cref="Animator" />: its parameter values, one <see cref="LayerPlayer" /> per
	///     layer, the pose layout bound to its hierarchy and the samplers of its clips. <see cref="Advance" /> runs the
	///     state machines and blends the pose (on any thread: it touches only this instance and immutable data) and
	///     <see cref="Apply" /> writes the pose and delivers the callbacks on the main thread.
	/// </summary>
	public sealed class AnimatorPlayer
	{
		#region PublicFields

		public readonly Animator Animator;
		public RuntimeController Controller { get; private set; }
		public PoseLayout Layout { get; private set; }
		public Pose FinalPose { get; private set; }
		public LayerPlayer[] Layers { get; private set; } = Array.Empty<LayerPlayer>();
		public ParameterState[] Parameters { get; private set; } = Array.Empty<ParameterState>();

		/// <summary>One delegate per transition (by <see cref="RuntimeTransition.Index" />): all its conditions; null = none.</summary>
		public Func<bool>[] Conditions { get; private set; } = Array.Empty<Func<bool>>();

		/// <summary>Root motion of the last advance, in the root's own frame (position) and as a local rotation delta.</summary>
		public Vector3 DeltaPosition;

		public Quaternion DeltaRotation = Quaternion.Identity;
		public bool RootMotionSampled;

		/// <summary>An exception thrown by the last advance (logged by the system on the main thread).</summary>
		public Exception LastError;

		/// <summary>The version of the controller and the hierarchy the player was built for.</summary>
		public int ControllerVersion { get; private set; } = -1;

		public bool IsBuilt => Controller != null && Layout != null;

		/// <summary>Frames advanced since the build.</summary>
		public long FrameCount;

		#endregion

		#region InternalFields

		internal readonly List<AnimationEvent> PendingEvents = new List<AnimationEvent>();
		internal readonly List<Callback> PendingCallbacks = new List<Callback>();
		internal readonly List<string> PendingLogs = new List<string>();

		internal struct Callback
		{
			public CallbackKind Kind;
			public StateMachineBehaviour Behaviour;
			public AnimatorStateInfo Info;
			public int Layer;
			public ulong MachineId;
		}

		internal enum CallbackKind
		{
			Enter,
			Update,
			Exit,
			MachineEnter,
			MachineExit,
			Move,
			StateChanged
		}

		#endregion

		#region PrivateFields

		private readonly Dictionary<AnimationClip, ClipSampler> samplers = new Dictionary<AnimationClip, ClipSampler>();
		private readonly List<float[]> weightScratch = new List<float[]>();
		private int clipVersionSum;

		#endregion

		#region Constructors

		public AnimatorPlayer(Animator animator)
		{
			Animator = animator;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Builds (or rebuilds) the runtime for the animator's controller and hierarchy. Parameter values and the
		///     layers' current states survive a rebuild of the same controller (live edits), matched by id.
		/// </summary>
		public void Build()
		{
			AnimatorController source = Animator.controller;
			RuntimeController compiled = source != null ? RuntimeController.Get(source) : null;
			ParameterState[] oldParameters = Parameters;
			LayerPlayer[] oldLayers = Layers;
			Controller = compiled;
			ControllerVersion = compiled?.Version ?? -1;
			samplers.Clear();
			if (compiled == null)
			{
				Layers = Array.Empty<LayerPlayer>();
				Parameters = Array.Empty<ParameterState>();
				Conditions = Array.Empty<Func<bool>>();
				Layout = null;
				FinalPose = null;
				return;
			}

			// Parameters (the root's, then the private ones of embedded assets), keeping the values of the ones that still exist.
			Parameters = new ParameterState[compiled.Parameters.Count];
			for (int i = 0; i < Parameters.Length; i++)
			{
				AnimatorParameter parameter = compiled.Parameters[i];
				ParameterState state = (parameter.defaultValue ?? new FloatValue()).CreateState(parameter);
				foreach (ParameterState old in oldParameters)
				{
					if (old.Id == parameter.id && old.Name == parameter.name && old.ValueType == state.ValueType)
					{
						state.Boxed = old.Boxed;
						break;
					}
				}

				Parameters[i] = state;
			}

			PendingLogs.AddRange(compiled.Warnings);

			// The pose layout of every clip, bound to the hierarchy.
			Layout = new PoseLayout();
			clipVersionSum = 0;
			foreach (AnimationClip clip in compiled.Clips)
			{
				Layout.AddClip(clip);
				clipVersionSum += clip.Version;
			}

			Layout.Bind(Animator.gameObject);
			FinalPose = new Pose(Layout);

			// Conditions compiled against this player's parameter slots.
			Conditions = new Func<bool>[compiled.Transitions.Count];
			foreach (RuntimeTransition transition in compiled.Transitions)
			{
				Conditions[transition.Index] = CompileConditions(transition);
			}

			// Layers (the current states carry over by id).
			Layers = new LayerPlayer[compiled.Layers.Length];
			for (int i = 0; i < Layers.Length; i++)
			{
				Layers[i] = new LayerPlayer(this, compiled.Layers[i]);
				if (i < oldLayers.Length && oldLayers[i] != null)
				{
					Layers[i].CarryOver(oldLayers[i]);
				}
			}

			PendingEvents.Clear();
			PendingCallbacks.Clear();
			FrameCount = 0;
		}

		/// <summary>True when the controller or a clip changed since the build (main thread: a rebuild is due).</summary>
		public bool NeedsRebuild()
		{
			AnimatorController source = Animator.controller;
			if (source == null)
			{
				return Controller != null;
			}

			if (Controller == null || Controller.Source != source || !Controller.IsCurrent)
			{
				return true;
			}

			int sum = 0;
			foreach (AnimationClip clip in Controller.Clips)
			{
				sum += clip.Version;
			}

			return sum != clipVersionSum;
		}

		/// <summary>Advances every layer by <paramref name="deltaTime" /> and blends the final pose. Thread-safe per instance.</summary>
		public void Advance(float deltaTime)
		{
			if (!IsBuilt)
			{
				return;
			}

			LastError = null;
			try
			{
				RootMotionSampled = false;
				DeltaPosition = Vector3.Zero;
				DeltaRotation = Quaternion.Identity;
				for (int i = 0; i < Layers.Length; i++)
				{
					Layers[i].Advance(deltaTime);
				}

				Combine();
				FrameCount++;
			}
			catch (Exception e)
			{
				LastError = e;
			}
		}

		/// <summary>Writes the pose, applies root motion and delivers events and callbacks. Main thread.</summary>
		public void Apply()
		{
			if (!IsBuilt || LastError != null)
			{
				return;
			}

			bool rootMotion = Animator.applyRootMotion && RootMotionSampled;
			FinalPose.Apply(Layout, rootMotion);
			if (rootMotion)
			{
				Animator.ApplyRootMotion(DeltaPosition, DeltaRotation);
			}

			Animator.DeliverCallbacks(PendingCallbacks, PendingEvents);
			PendingCallbacks.Clear();
			PendingEvents.Clear();
		}

		/// <summary>The sampler of a clip in this player's layout (created on first use).</summary>
		public ClipSampler SamplerFor(AnimationClip clip)
		{
			if (!samplers.TryGetValue(clip, out ClipSampler sampler))
			{
				sampler = new ClipSampler(clip, Layout);
				samplers[clip] = sampler;
			}

			return sampler;
		}

		public int ParameterIndex(string name)
		{
			return Controller?.ParameterIndex(name) ?? -1;
		}

		public int ParameterIndex(ulong id)
		{
			return Controller?.ParameterIndex(id) ?? -1;
		}

		/// <summary>A parameter's value as a float (0 for non-numbers or a missing index).</summary>
		public float ParameterFloat(int index)
		{
			return index >= 0 && index < Parameters.Length ? Parameters[index].AsFloat() : 0f;
		}

		/// <summary>Resets every trigger a transition's conditions read (the transition was taken).</summary>
		public void ConsumeTriggers(RuntimeTransition transition)
		{
			foreach (AnimatorCondition condition in transition.Source.conditions)
			{
				ConsumeTriggers(condition, transition.Scope);
			}
		}

		internal float[] WeightBuffer(int depth, int size)
		{
			while (weightScratch.Count <= depth)
			{
				weightScratch.Add(new float[Math.Max(size, 8)]);
			}

			if (weightScratch[depth].Length < size)
			{
				weightScratch[depth] = new float[size];
			}

			return weightScratch[depth];
		}

		#endregion

		#region PrivateMethods

		private void Combine()
		{
			FinalPose.Clear();
			bool first = true;
			for (int i = 0; i < Layers.Length; i++)
			{
				LayerPlayer layer = Layers[i];
				if (!layer.Enabled || !layer.HasState)
				{
					continue;
				}

				float weight = i == 0 ? 1f : layer.Weight;
				if (weight <= 0f)
				{
					continue;
				}

				if (first)
				{
					FinalPose.CopyFrom(layer.Pose);
					if (layer.Layer.Blending == LayerBlendingMode.Additive)
					{
						// An additive first layer adds to the last written values.
						FinalPose.Clear();
						FinalPose.Additive(layer.Pose, weight, layer.Mask);
					}
					else if (weight < 1f)
					{
						FinalPose.ScaleWeights(weight);
					}

					first = false;
				}
				else if (layer.Layer.Blending == LayerBlendingMode.Additive)
				{
					FinalPose.Additive(layer.Pose, weight, layer.Mask);
				}
				else
				{
					FinalPose.Override(layer.Pose, weight, layer.Mask);
				}

				if (layer.RootMotionSampled && (layer.Mask == null || layer.RootIncluded))
				{
					if (!RootMotionSampled)
					{
						DeltaPosition = layer.DeltaPosition * weight;
						DeltaRotation = Quaternion.Slerp(Quaternion.Identity, layer.DeltaRotation, weight);
						RootMotionSampled = true;
					}
					else if (layer.Layer.Blending == LayerBlendingMode.Additive)
					{
						DeltaPosition += layer.DeltaPosition * weight;
						DeltaRotation = DeltaRotation * Quaternion.Slerp(Quaternion.Identity, layer.DeltaRotation, weight);
					}
					else
					{
						DeltaPosition = DeltaPosition * (1f - weight) + layer.DeltaPosition * weight;
						DeltaRotation = Quaternion.Slerp(DeltaRotation, layer.DeltaRotation, weight);
					}
				}
			}
		}

		private Func<bool> CompileConditions(RuntimeTransition transition)
		{
			var list = new List<Func<bool>>();
			foreach (AnimatorCondition condition in transition.Source.conditions)
			{
				Func<bool> compiled = Compile(condition, transition.Machine.Layer.Index, transition.Scope);
				if (compiled != null)
				{
					list.Add(compiled);
				}
			}

			if (list.Count == 0)
			{
				return null;
			}

			if (list.Count == 1)
			{
				return list[0];
			}

			Func<bool>[] all = list.ToArray();
			return () =>
			{
				foreach (Func<bool> condition in all)
				{
					if (!condition())
					{
						return false;
					}
				}

				return true;
			};
		}

		private Func<bool> Compile(AnimatorCondition condition, int layerIndex, ParameterScope scope)
		{
			switch (condition)
			{
				case ParameterCondition pc:
				{
					int index = scope.Index(pc.parameter);
					if (index < 0)
					{
						PendingLogs.Add("A condition refers to a missing parameter: the transition is never taken.");
						return () => false;
					}

					ParameterState state = Parameters[index];
					ParameterState other = pc.otherParameter != 0 ? (scope.Index(pc.otherParameter) is int o && o >= 0 ? Parameters[o] : null) : null;
					AnimatorValue threshold = pc.value;
					if (threshold == null || threshold.ValueType != state.ValueType)
					{
						// No threshold of the right type: the parameter's own default class gives the comparison (its value is the default).
						threshold = state.Parameter.defaultValue;
						if (pc.value != null && pc.mode != ConditionMode.If && pc.mode != ConditionMode.IfNot && pc.mode != ConditionMode.Trigger && pc.mode != ConditionMode.IsNull && pc.mode != ConditionMode.IsNotNull && other == null)
						{
							// Numbers of another type convert.
							threshold = threshold.Clone();
							threshold.BoxedValue = ConvertValue(pc.value.BoxedValue, threshold.ValueType) ?? threshold.BoxedValue;
						}
					}

					Func<bool> compiled = threshold?.CompileCondition(state, pc.mode, other);
					if (compiled == null)
					{
						PendingLogs.Add($"The condition on '{state.Name}' ({pc.mode}) is not supported for {state.ValueType.Name}: the transition is never taken.");
						return () => false;
					}

					return compiled;
				}
				case ConditionGroup group:
				{
					var members = new List<Func<bool>>();
					foreach (AnimatorCondition nested in group.conditions)
					{
						Func<bool> compiled = Compile(nested, layerIndex, scope);
						if (compiled != null)
						{
							members.Add(compiled);
						}
					}

					if (members.Count == 0)
					{
						return null;
					}

					Func<bool>[] all = members.ToArray();
					if (group.any)
					{
						return () =>
						{
							foreach (Func<bool> member in all)
							{
								if (member()) return true;
							}

							return false;
						};
					}

					return () =>
					{
						foreach (Func<bool> member in all)
						{
							if (!member()) return false;
						}

						return true;
					};
				}
				case ScriptCondition script:
				{
					Animator animator = Animator;
					return () =>
					{
						try
						{
							return script.Evaluate(animator, layerIndex);
						}
						catch (Exception e)
						{
							PendingLogs.Add("Script condition " + script.GetType().Name + " threw: " + e.Message);
							return false;
						}
					};
				}
				default:
					return null;
			}
		}

		private static object ConvertValue(object value, Type type)
		{
			if (value == null || type == null)
			{
				return null;
			}

			if (type.IsInstanceOfType(value))
			{
				return value;
			}

			try
			{
				return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private void ConsumeTriggers(AnimatorCondition condition, ParameterScope scope)
		{
			switch (condition)
			{
				case ParameterCondition pc when pc.mode == ConditionMode.Trigger || (pc.mode == ConditionMode.If && IsTrigger(pc.parameter, scope)):
				{
					int index = scope.Index(pc.parameter);
					if (index >= 0)
					{
						Parameters[index].ResetTrigger();
					}

					break;
				}
				case ConditionGroup group:
					foreach (AnimatorCondition nested in group.conditions)
					{
						ConsumeTriggers(nested, scope);
					}

					break;
			}
		}

		private bool IsTrigger(ulong parameterId, ParameterScope scope)
		{
			int index = scope.Index(parameterId);
			return index >= 0 && Parameters[index].IsTrigger;
		}

		#endregion
	}

	/// <summary>A state playing on a layer: which, how far, how fast.</summary>
	public sealed class StatePlayback
	{
		#region PublicFields

		public readonly RuntimeState State;

		/// <summary>Time in cycles (unbounded; sampling wraps or clamps).</summary>
		public double NormalizedTime;

		public double PreviousNormalizedTime;

		/// <summary>Length in seconds of the motion under the current blend (1 for an empty state).</summary>
		public float Length;

		public bool Loop;
		public float SpeedMultiplier = 1f;
		public bool FirstFrame = true;
		public long EnteredFrame;

		#endregion

		#region Constructors

		public StatePlayback(RuntimeState state, double startNormalized)
		{
			State = state;
			NormalizedTime = startNormalized;
			PreviousNormalizedTime = startNormalized;
			Length = Math.Max(state.Length, 1e-4f);
			if (state.Motion == null)
			{
				Length = 1f;
			}

			Loop = state.Loop;
		}

		#endregion

		#region PublicMethods

		public float TimeSeconds => (float)NormalizedTime * Length;

		/// <summary>The clip time for a motion of <paramref name="length" /> at <paramref name="normalized" /> cycles.</summary>
		public static float ClipTime(double normalized, float length, bool loop)
		{
			if (length <= 1e-6f)
			{
				return 0f;
			}

			if (loop)
			{
				double cycle = normalized - Math.Floor(normalized);
				return (float)(cycle * length);
			}

			return (float)Math.Clamp(normalized, 0d, 1d) * length;
		}

		public AnimatorStateInfo Info(RuntimeState fallback = null)
		{
			RuntimeState state = State ?? fallback;
			return new AnimatorStateInfo
			{
				fullPathHash = state?.FullNameHash ?? 0, shortNameHash = state?.NameHash ?? 0, tagHash = state?.TagHash ?? 0, normalizedTime = (float)NormalizedTime, length = Length,
				speed = state?.Speed ?? 1f, speedMultiplier = SpeedMultiplier, loop = Loop, name = state?.FullName, stateId = state?.Id ?? 0
			};
		}

		#endregion
	}

	/// <summary>
	///     One layer of an <see cref="AnimatorPlayer" />: runs the layer's state machine (transitions, interruptions,
	///     entry / exit of sub-machines, any state), samples the current and next states' motions and blends them into
	///     the layer's pose, collects events, root motion and behaviour callbacks.
	/// </summary>
	public sealed class LayerPlayer
	{
		#region PublicFields

		public readonly AnimatorPlayer Owner;
		public readonly RuntimeLayer Layer;
		public readonly Pose Pose;
		public float Weight;
		public bool Enabled;

		/// <summary>Slots the layer's mask allows (null: all).</summary>
		public readonly bool[] Mask;

		public readonly bool RootIncluded;

		public StatePlayback Current { get; private set; }
		public StatePlayback Next { get; private set; }
		public RuntimeTransition Transition { get; private set; }
		public float TransitionTime { get; private set; }
		public float TransitionDuration { get; private set; }
		/// <summary>A transition (of the controller, or a CrossFade from code: then <see cref="Transition" /> is null) is running.</summary>
		public bool InTransition => Next != null;

		public bool HasState => Current != null;
		public float TransitionProgress => InTransition && TransitionDuration > 1e-6f ? Math.Clamp(TransitionTime / TransitionDuration, 0f, 1f) : 0f;

		/// <summary>Clips sampled in the last advance with their weights (debugging, AnimatorClipInfo).</summary>
		public readonly List<AnimatorClipInfo> SampledClips = new List<AnimatorClipInfo>();

		/// <summary>The states entered so far, newest last (the editor's history; capped).</summary>
		public readonly List<(string state, long frame)> History = new List<(string, long)>();

		public Vector3 DeltaPosition;
		public Quaternion DeltaRotation = Quaternion.Identity;
		public bool RootMotionSampled;

		#endregion

		#region PrivateFields

		private const int MaxHistory = 32;
		private readonly Pose poseA;
		private readonly Pose poseB;
		private readonly Pose frozen;
		private bool fromFrozen;
		private readonly Dictionary<int, StateMachineBehaviour[]> behaviours = new Dictionary<int, StateMachineBehaviour[]>();
		private readonly List<SampledClip> sampled = new List<SampledClip>();
		private readonly List<AnimationEvent> eventBuffer = new List<AnimationEvent>();

		private struct SampledClip
		{
			public ClipSampler Sampler;
			public float PreviousTime;
			public float Time;
			public double PreviousNormalized;
			public double Normalized;
			public bool Loop;
			public float Weight;
			public bool FirstFrame;
		}

		#endregion

		#region Constructors

		public LayerPlayer(AnimatorPlayer owner, RuntimeLayer layer)
		{
			Owner = owner;
			Layer = layer;
			Pose = new Pose(owner.Layout);
			poseA = new Pose(owner.Layout);
			poseB = new Pose(owner.Layout);
			frozen = new Pose(owner.Layout);
			Weight = layer.DefaultWeight;
			Enabled = layer.Enabled;
			AnimationMask mask = layer.Mask;
			if (mask != null && !mask.IsEmpty)
			{
				Mask = new bool[owner.Layout.Slots.Count];
				foreach (PoseSlot slot in owner.Layout.Slots)
				{
					Mask[slot.Index] = mask.Includes(slot.Path);
				}

				RootIncluded = mask.Includes("");
			}
			else
			{
				RootIncluded = true;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Continues where an older player of the same layer was (a rebuild after a live edit).</summary>
		public void CarryOver(LayerPlayer old)
		{
			Weight = old.Weight;
			if (old.Current != null)
			{
				RuntimeState state = Layer.FindState(old.Current.State.Id) ?? Layer.FindState(old.Current.State.FullName);
				if (state != null)
				{
					Current = new StatePlayback(state, old.Current.NormalizedTime) { PreviousNormalizedTime = old.Current.PreviousNormalizedTime, FirstFrame = false, EnteredFrame = old.Current.EnteredFrame };
				}
			}

			History.AddRange(old.History);
		}

		/// <summary>Advances the layer (state machine, sampling, events).</summary>
		public void Advance(float deltaTime)
		{
			sampled.Clear();
			SampledClips.Clear();
			RootMotionSampled = false;
			DeltaPosition = Vector3.Zero;
			DeltaRotation = Quaternion.Identity;
			if (!Enabled)
			{
				return;
			}

			if (Current == null)
			{
				RuntimeState start = ResolveEntry(Layer.Root, 0);
				if (start == null)
				{
					return;
				}

				Current = new StatePlayback(start, StartOffset(start, 0f)) { EnteredFrame = Owner.FrameCount };
				QueueEnter(Current, null);
			}

			AdvanceTime(Current, deltaTime);
			if (Next != null)
			{
				AdvanceTime(Next, deltaTime);
				TransitionTime += deltaTime * Owner.Animator.speed;
			}

			EvaluateTransitions();
			if (Next != null && TransitionTime >= TransitionDuration)
			{
				CompleteTransition();
			}

			Sample();
			CollectEvents();
			CollectRootMotion();
			QueueUpdates();
			Current.FirstFrame = false;
			if (Next != null)
			{
				Next.FirstFrame = false;
			}
		}

		/// <summary>Plays a state at once (Animator.Play).</summary>
		public void Play(RuntimeState state, float normalizedTime)
		{
			if (state == null)
			{
				return;
			}

			double start = float.IsNaN(normalizedTime) ? StartOffset(state, 0f) : normalizedTime;
			SwitchImmediately(state, start);
		}

		/// <summary>Starts a transition to a state (Animator.CrossFade); <paramref name="durationSeconds" /> 0 switches at once.</summary>
		public void CrossFade(RuntimeState state, float durationSeconds, float normalizedOffset)
		{
			if (state == null)
			{
				return;
			}

			if (durationSeconds <= 1e-5f || Current == null)
			{
				SwitchImmediately(state, normalizedOffset);
				return;
			}

			BeginTransition(null, state, durationSeconds, normalizedOffset, false);
		}

		/// <summary>Forgets the current state: the next advance starts at the layer's entry.</summary>
		public void Reset()
		{
			Current = null;
			Next = null;
			Transition = null;
			fromFrozen = false;
			TransitionTime = 0f;
		}

		public AnimatorStateInfo CurrentInfo => Current?.Info() ?? default;
		public AnimatorStateInfo NextInfo => Next?.Info() ?? default;

		public AnimatorTransitionInfo TransitionInfo
		{
			get
			{
				if (!InTransition)
				{
					return default;
				}

				return new AnimatorTransitionInfo
				{
					nameHash = Transition?.NameHash ?? 0, normalizedTime = TransitionProgress, duration = TransitionDuration, anyState = Transition?.IsAnyState ?? false, name = Transition?.Name ?? "",
					transitionId = Transition?.Source.id ?? 0, sourceStateId = Current?.State.Id ?? 0, destinationStateId = Next?.State.Id ?? 0
				};
			}
		}

		/// <summary>The behaviour instances of a state on this layer (created on first use from the state's prototypes).</summary>
		public StateMachineBehaviour[] BehavioursOf(RuntimeState state)
		{
			if (!behaviours.TryGetValue(state.Index, out StateMachineBehaviour[] instances))
			{
				var list = new List<StateMachineBehaviour>();
				foreach (StateMachineBehaviour prototype in state.Source.behaviours)
				{
					StateMachineBehaviour instance = AnimatorController.CloneBehaviour(prototype);
					if (instance != null)
					{
						instance.animator = Owner.Animator;
						instance.layerIndex = Layer.Index;
						list.Add(instance);
					}
				}

				instances = list.ToArray();
				behaviours[state.Index] = instances;
			}

			return instances;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- time

		private void AdvanceTime(StatePlayback playback, float deltaTime)
		{
			RuntimeState state = playback.State;
			float multiplier = state.SpeedParameter >= 0 ? Owner.ParameterFloat(state.SpeedParameter) : 1f;
			playback.SpeedMultiplier = multiplier;
			playback.PreviousNormalizedTime = playback.NormalizedTime;
			if (state.MotionTimeParameter >= 0)
			{
				playback.NormalizedTime = Owner.ParameterFloat(state.MotionTimeParameter);
				return;
			}

			float speed = state.Speed * multiplier * Owner.Animator.speed;
			playback.NormalizedTime += deltaTime * speed / Math.Max(playback.Length, 1e-4f);
		}

		private double StartOffset(RuntimeState state, float transitionOffset)
		{
			float cycle = state.CycleOffsetParameter >= 0 ? Owner.ParameterFloat(state.CycleOffsetParameter) : state.CycleOffset;
			return cycle + transitionOffset;
		}

		// ---------------------------------------------------------------- transitions

		private void EvaluateTransitions()
		{
			if (Current == null)
			{
				return;
			}

			if (Next == null)
			{
				RuntimeTransition found = FindTransition(Current, null, true, false);
				if (found != null)
				{
					Take(found, Current, false);
				}

				return;
			}

			// A running transition: what may interrupt it (a CrossFade from code: nothing).
			RuntimeTransition interrupting = null;
			bool fromNext = false;
			switch (Transition?.Interruption ?? TransitionInterruptionSource.None)
			{
				case TransitionInterruptionSource.CurrentState:
					interrupting = FindTransition(Current, Transition, true, true);
					break;
				case TransitionInterruptionSource.NextState:
					interrupting = FindTransition(Next, Transition, false, true);
					fromNext = interrupting != null;
					break;
				case TransitionInterruptionSource.CurrentStateThenNextState:
					interrupting = FindTransition(Current, Transition, true, true);
					if (interrupting == null)
					{
						interrupting = FindTransition(Next, Transition, false, true);
						fromNext = interrupting != null;
					}

					break;
				case TransitionInterruptionSource.NextStateThenCurrentState:
					interrupting = FindTransition(Next, Transition, false, true);
					fromNext = interrupting != null;
					if (interrupting == null)
					{
						interrupting = FindTransition(Current, Transition, true, true);
					}

					break;
			}

			if (interrupting != null)
			{
				Take(interrupting, fromNext ? Next : Current, fromNext);
			}
		}

		private RuntimeTransition FindTransition(StatePlayback source, RuntimeTransition running, bool includeAny, bool interrupting)
		{
			RuntimeState state = source.State;
			if (includeAny)
			{
				foreach (RuntimeTransition any in state.AnyTransitions)
				{
					if (any == running)
					{
						continue;
					}

					if (Eligible(any, source, true))
					{
						return any;
					}
				}
			}

			foreach (RuntimeTransition own in state.Transitions)
			{
				if (own == running)
				{
					continue;
				}

				if (interrupting && running != null && running.OrderedInterruption && running.From == state && own.Order >= running.Order)
				{
					continue;
				}

				if (interrupting && Next != null && own.Destination != null && own.Destination == Next.State && !own.CanTransitionToSelf)
				{
					continue;
				}

				if (Eligible(own, source, false))
				{
					return own;
				}
			}

			return null;
		}

		private bool Eligible(RuntimeTransition transition, StatePlayback source, bool fromAny)
		{
			if (transition.Destination == null && transition.DestinationMachine == null && !transition.ToExit)
			{
				return false;
			}

			if (fromAny && !transition.CanTransitionToSelf)
			{
				if (transition.Destination != null && (transition.Destination == Current?.State || (Next != null && transition.Destination == Next.State)))
				{
					return false;
				}
			}

			if (transition.HasExitTime && !ExitTimeReached(transition, source))
			{
				return false;
			}

			Func<bool> conditions = Owner.Conditions[transition.Index];
			return conditions == null || conditions();
		}

		private bool ExitTimeReached(RuntimeTransition transition, StatePlayback playback)
		{
			double previous = playback.PreviousNormalizedTime;
			double current = playback.NormalizedTime;
			float exit = transition.ExitTimeInSeconds ? transition.ExitTime / Math.Max(playback.Length, 1e-4f) : transition.ExitTime;
			bool perCycle = playback.Loop && exit < 1f;
			if (transition.HasConditions)
			{
				// With conditions the exit time is a window: from the exit point to the end of the cycle (or forever).
				if (perCycle)
				{
					return current - Math.Floor(current) >= exit || current < previous;
				}

				return current >= exit;
			}

			// Without conditions it fires when the time crosses the exit point (every cycle for looping states).
			if (perCycle)
			{
				return Math.Floor(current - exit) != Math.Floor(previous - exit);
			}

			return (previous < exit && current >= exit) || (previous > exit && current <= exit);
		}

		private void Take(RuntimeTransition transition, StatePlayback source, bool fromNext)
		{
			RuntimeState destination = Resolve(transition, 0);
			if (destination == null)
			{
				return;
			}

			Owner.ConsumeTriggers(transition);
			float length = Math.Max(source?.Length ?? 1f, 1e-4f);
			float durationSeconds = transition.FixedDuration ? transition.Duration : transition.Duration * length;
			if (durationSeconds <= 1e-5f)
			{
				SwitchImmediately(destination, StartOffset(destination, transition.Offset));
			}
			else
			{
				BeginTransition(transition, destination, durationSeconds, transition.Offset, fromNext);
			}
		}

		/// <summary>The state a transition leads to: directly, through a machine's entry, or out of a machine.</summary>
		private RuntimeState Resolve(RuntimeTransition transition, int depth)
		{
			if (depth > 16)
			{
				return null;
			}

			if (transition.Destination != null)
			{
				return transition.Destination;
			}

			if (transition.DestinationMachine != null)
			{
				return ResolveEntry(transition.DestinationMachine, depth + 1);
			}

			if (transition.ToExit)
			{
				RuntimeMachine machine = transition.From?.Machine ?? transition.Machine;
				return ResolveExit(machine, depth + 1);
			}

			return null;
		}

		private RuntimeState ResolveEntry(RuntimeMachine machine, int depth)
		{
			if (machine == null || depth > 16)
			{
				return null;
			}

			foreach (RuntimeTransition entry in machine.EntryTransitions)
			{
				Func<bool> conditions = Owner.Conditions[entry.Index];
				if (conditions != null && !conditions())
				{
					continue;
				}

				if (entry.Destination != null)
				{
					return entry.Destination;
				}

				if (entry.DestinationMachine != null)
				{
					RuntimeState nested = ResolveEntry(entry.DestinationMachine, depth + 1);
					if (nested != null)
					{
						return nested;
					}
				}
			}

			if (machine.DefaultState != null)
			{
				return machine.DefaultState;
			}

			foreach (RuntimeMachine sub in machine.SubMachines)
			{
				RuntimeState nested = ResolveEntry(sub, depth + 1);
				if (nested != null)
				{
					return nested;
				}
			}

			return null;
		}

		private RuntimeState ResolveExit(RuntimeMachine machine, int depth)
		{
			if (machine == null || depth > 16)
			{
				return null;
			}

			if (machine.IsRoot)
			{
				return ResolveEntry(machine, depth + 1);
			}

			foreach (RuntimeTransition exit in machine.ExitTransitions)
			{
				Func<bool> conditions = Owner.Conditions[exit.Index];
				if (conditions != null && !conditions())
				{
					continue;
				}

				if (exit.Destination != null)
				{
					return exit.Destination;
				}

				if (exit.DestinationMachine != null)
				{
					return ResolveEntry(exit.DestinationMachine, depth + 1);
				}

				if (exit.ToExit)
				{
					return ResolveExit(machine.Parent, depth + 1);
				}
			}

			return null;
		}

		private void SwitchImmediately(RuntimeState destination, double startNormalized)
		{
			if (Next != null)
			{
				QueueExit(Next, destination);
				Next = null;
				Transition = null;
				fromFrozen = false;
			}

			StatePlayback previous = Current;
			if (previous != null)
			{
				QueueExit(previous, destination);
			}

			Current = new StatePlayback(destination, startNormalized) { EnteredFrame = Owner.FrameCount };
			TransitionTime = 0f;
			QueueEnter(Current, previous?.State);
		}

		private void BeginTransition(RuntimeTransition transition, RuntimeState destination, float durationSeconds, float offset, bool fromNext)
		{
			if (Next != null)
			{
				// Interrupting: the pose blended so far is frozen and the blend restarts from it.
				frozen.CopyFrom(Pose);
				fromFrozen = true;
				if (fromNext)
				{
					QueueExit(Current, destination);
					Current = Next;
				}
				else
				{
					QueueExit(Next, destination);
				}

				Next = null;
			}
			else
			{
				fromFrozen = false;
			}

			RuntimeState previous = Current?.State;
			Next = new StatePlayback(destination, StartOffset(destination, offset)) { EnteredFrame = Owner.FrameCount };
			Transition = transition;
			TransitionTime = 0f;
			TransitionDuration = Math.Max(durationSeconds, 1e-5f);
			QueueEnter(Next, previous);
		}

		private void CompleteTransition()
		{
			StatePlayback previous = Current;
			Current = Next;
			Next = null;
			Transition = null;
			fromFrozen = false;
			TransitionTime = 0f;
			QueueExit(previous, Current.State);
		}

		// ---------------------------------------------------------------- sampling

		private void Sample()
		{
			if (Current == null)
			{
				Pose.Clear();
				return;
			}

			float progress = TransitionProgress;
			SampleState(Current, poseA, Next != null ? 1f - progress : 1f);
			if (Next != null)
			{
				SampleState(Next, poseB, progress);
				Pose.Lerp(fromFrozen ? frozen : poseA, poseB, progress);
			}
			else
			{
				Pose.CopyFrom(poseA);
			}
		}

		private void SampleState(StatePlayback playback, Pose pose, float stateWeight)
		{
			pose.Clear();
			RuntimeState state = playback.State;
			if (state.Motion == null)
			{
				if (state.WriteDefaults)
				{
					pose.FillDefaults(1f);
				}

				playback.Length = 1f;
				return;
			}

			float length = SampleMotion(state.Motion, playback, playback.NormalizedTime, playback.PreviousNormalizedTime, 1f, stateWeight, pose, 0);
			playback.Length = Math.Max(length, 1e-4f);
			playback.Loop = state.Loop;
			if (state.WriteDefaults)
			{
				pose.FillDefaults(1f);
			}
		}

		/// <summary>Samples a motion into the pose; returns its length under the blend (seconds).</summary>
		private float SampleMotion(RuntimeMotion motion, StatePlayback playback, double normalized, double previousNormalized, float weight, float stateWeight, Pose pose, int depth)
		{
			switch (motion)
			{
				case RuntimeClipMotion clipMotion:
				{
					AnimationClip clip = clipMotion.Clip;
					float length = clip.length;
					bool loop = playback.State.Source.loop == StateLoopMode.FromClip ? clip.loop : playback.State.Loop;
					float time = StatePlayback.ClipTime(normalized, length, loop);
					ClipSampler sampler = Owner.SamplerFor(clip);
					if (Layer.Blending == LayerBlendingMode.Additive)
					{
						sampler.SampleAdditive(time, 0f, weight, pose);
					}
					else
					{
						sampler.Sample(time, weight, pose);
					}

					float previousTime = loop ? (float)(previousNormalized * length) : StatePlayback.ClipTime(previousNormalized, length, false);
					float unwrappedTime = loop ? (float)(normalized * length) : time;
					sampled.Add(new SampledClip { Sampler = sampler, PreviousTime = previousTime, Time = unwrappedTime, PreviousNormalized = previousNormalized, Normalized = normalized, Loop = loop, Weight = weight * stateWeight, FirstFrame = playback.FirstFrame });
					SampledClips.Add(new AnimatorClipInfo { clip = clip, weight = weight * stateWeight, time = time });
					return Math.Max(length, 1e-4f);
				}
				case RuntimeBlendTree tree:
				{
					int n = tree.Children.Length;
					if (n == 0)
					{
						return 1f;
					}

					float[] weights = Owner.WeightBuffer(depth, n);
					float x = tree.ParameterX >= 0 ? Owner.ParameterFloat(tree.ParameterX) : 0f;
					float y = tree.ParameterY >= 0 ? Owner.ParameterFloat(tree.ParameterY) : 0f;
					tree.ComputeWeights(x, y, Owner.Parameters, weights);
					float length = 0f;
					float total = 0f;
					for (int i = 0; i < n; i++)
					{
						float w = weights[i];
						if (w <= 1e-5f)
						{
							continue;
						}

						RuntimeBlendTree.Child child = tree.Children[i];
						float childLength = SampleMotion(child.Motion, playback, normalized * child.TimeScale, previousNormalized * child.TimeScale, weight * w, stateWeight, pose, depth + 1);
						length += w * childLength / Math.Abs(child.TimeScale);
						total += w;
					}

					return total > 1e-6f ? length / total : tree.NominalLength;
				}
				default:
					return 1f;
			}
		}

		/// <summary>Events of a motion for the frame's time advance, without sampling a pose (a state left before sampling).</summary>
		private void CollectExitEvents(RuntimeMotion motion, StatePlayback playback, float weight, int depth)
		{
			switch (motion)
			{
				case RuntimeClipMotion clipMotion:
				{
					AnimationClip clip = clipMotion.Clip;
					if (clip.events.Count == 0)
					{
						return;
					}

					float length = clip.length;
					bool loop = playback.State.Source.loop == StateLoopMode.FromClip ? clip.loop : playback.State.Loop;
					float previousTime = loop ? (float)(playback.PreviousNormalizedTime * length) : StatePlayback.ClipTime(playback.PreviousNormalizedTime, length, false);
					float time = loop ? (float)(playback.NormalizedTime * length) : StatePlayback.ClipTime(playback.NormalizedTime, length, false);
					ClipSampler sampler = Owner.SamplerFor(clip);
					eventBuffer.Clear();
					sampler.CollectEvents(previousTime, time, loop, playback.FirstFrame, eventBuffer);
					foreach (AnimationEvent e in eventBuffer)
					{
						if (weight + 1e-6f < e.minimumWeight)
						{
							continue;
						}

						AnimationEvent fired = e.Clone();
						fired.clip = clip;
						fired.animator = Owner.Animator;
						fired.animatorClipWeight = weight;
						fired.layerIndex = Layer.Index;
						Owner.PendingEvents.Add(fired);
					}

					break;
				}
				case RuntimeBlendTree tree:
				{
					int n = tree.Children.Length;
					if (n == 0)
					{
						return;
					}

					float[] weights = Owner.WeightBuffer(depth + 8, n);
					float x = tree.ParameterX >= 0 ? Owner.ParameterFloat(tree.ParameterX) : 0f;
					float y = tree.ParameterY >= 0 ? Owner.ParameterFloat(tree.ParameterY) : 0f;
					tree.ComputeWeights(x, y, Owner.Parameters, weights);
					for (int i = 0; i < n; i++)
					{
						if (weights[i] > 1e-5f)
						{
							CollectExitEvents(tree.Children[i].Motion, playback, weight * weights[i], depth + 1);
						}
					}

					break;
				}
			}
		}

		private void CollectEvents()
		{
			foreach (SampledClip entry in sampled)
			{
				if (entry.Sampler.Clip.events.Count == 0)
				{
					continue;
				}

				eventBuffer.Clear();
				entry.Sampler.CollectEvents(entry.PreviousTime, entry.Time, entry.Loop, entry.FirstFrame, eventBuffer);
				foreach (AnimationEvent e in eventBuffer)
				{
					if (entry.Weight + 1e-6f < e.minimumWeight || entry.Weight <= 0f)
					{
						continue;
					}

					AnimationEvent fired = e.Clone();
					fired.clip = entry.Sampler.Clip;
					fired.animator = Owner.Animator;
					fired.animatorClipWeight = entry.Weight;
					fired.layerIndex = Layer.Index;
					Owner.PendingEvents.Add(fired);
				}
			}
		}

		private void CollectRootMotion()
		{
			if (!Owner.Animator.applyRootMotion)
			{
				return;
			}

			foreach (SampledClip entry in sampled)
			{
				ClipSampler sampler = entry.Sampler;
				if (!sampler.HasRootMotion || entry.Weight <= 0f)
				{
					continue;
				}

				float length = sampler.Clip.length;
				float previous = entry.PreviousTime;
				float time = entry.Time;
				Vector3 deltaPosition;
				Quaternion rotationPrevious;
				if (entry.Loop && length > 1e-6f && Math.Floor(entry.Normalized) != Math.Floor(entry.PreviousNormalized))
				{
					// Crossed a loop boundary: the way to the end, the whole cycles in between, and the way from the start.
					float wrappedPrevious = (float)((entry.PreviousNormalized - Math.Floor(entry.PreviousNormalized)) * length);
					float wrappedTime = (float)((entry.Normalized - Math.Floor(entry.Normalized)) * length);
					int cycles = (int)(Math.Floor(entry.Normalized) - Math.Floor(entry.PreviousNormalized));
					Vector3 perCycle = sampler.RootPosition(length) - sampler.RootPosition(0f);
					if (cycles > 0)
					{
						deltaPosition = sampler.RootPosition(length) - sampler.RootPosition(wrappedPrevious) + sampler.RootPosition(wrappedTime) - sampler.RootPosition(0f) + perCycle * (cycles - 1);
					}
					else
					{
						deltaPosition = sampler.RootPosition(0f) - sampler.RootPosition(wrappedPrevious) + sampler.RootPosition(wrappedTime) - sampler.RootPosition(length) - perCycle * (-cycles - 1);
					}

					rotationPrevious = sampler.RootRotation(wrappedPrevious);
					previous = wrappedPrevious;
					time = wrappedTime;
				}
				else
				{
					if (entry.Loop && length > 1e-6f)
					{
						previous = (float)((entry.PreviousNormalized - Math.Floor(entry.PreviousNormalized)) * length);
						time = (float)((entry.Normalized - Math.Floor(entry.Normalized)) * length);
					}

					deltaPosition = sampler.RootPosition(time) - sampler.RootPosition(previous);
					rotationPrevious = sampler.RootRotation(previous);
				}

				Quaternion rotationNow = sampler.RootRotation(time);
				Quaternion deltaRotation = rotationPrevious.Inverted() * rotationNow;
				Vector3 localDelta = rotationPrevious.Inverted() * deltaPosition;
				RootMotionChannels channels = sampler.Clip.rootMotion;
				if ((channels & RootMotionChannels.PositionX) == 0) localDelta.X = 0f;
				if ((channels & RootMotionChannels.PositionY) == 0) localDelta.Y = 0f;
				if ((channels & RootMotionChannels.PositionZ) == 0) localDelta.Z = 0f;
				if ((channels & RootMotionChannels.Rotation) == 0) deltaRotation = Quaternion.Identity;
				DeltaPosition += localDelta * entry.Weight;
				DeltaRotation = DeltaRotation * Quaternion.Slerp(Quaternion.Identity, deltaRotation, entry.Weight);
				RootMotionSampled = true;
			}
		}

		// ---------------------------------------------------------------- callbacks

		private void QueueEnter(StatePlayback playback, RuntimeState previous)
		{
			RuntimeState state = playback.State;
			if (History.Count >= MaxHistory)
			{
				History.RemoveAt(0);
			}

			History.Add((state.FullName, Owner.FrameCount));

			// Machines entered: from the common ancestor down to the state's machine.
			var entered = new List<RuntimeMachine>();
			for (RuntimeMachine m = state.Machine; m != null && !IsAncestorOf(m, previous); m = m.Parent)
			{
				entered.Insert(0, m);
			}

			StateMachineBehaviour[] behaviours = BehavioursOf(state);
			AnimatorStateInfo info = playback.Info();
			foreach (RuntimeMachine machine in entered)
			{
				if (machine.IsRoot && previous != null)
				{
					continue;
				}

				foreach (StateMachineBehaviour behaviour in behaviours)
				{
					Owner.PendingCallbacks.Add(new AnimatorPlayer.Callback { Kind = AnimatorPlayer.CallbackKind.MachineEnter, Behaviour = behaviour, Info = info, Layer = Layer.Index, MachineId = machine.Id });
				}
			}

			Owner.PendingCallbacks.Add(new AnimatorPlayer.Callback { Kind = AnimatorPlayer.CallbackKind.StateChanged, Info = info, Layer = Layer.Index });
			foreach (StateMachineBehaviour behaviour in behaviours)
			{
				Owner.PendingCallbacks.Add(new AnimatorPlayer.Callback { Kind = AnimatorPlayer.CallbackKind.Enter, Behaviour = behaviour, Info = info, Layer = Layer.Index });
			}
		}

		private void QueueExit(StatePlayback playback, RuntimeState destination)
		{
			RuntimeState state = playback.State;
			// The state leaves before this frame's sampling: its events of the time it still advanced fire now.
			if (state.Motion != null && playback.NormalizedTime != playback.PreviousNormalizedTime)
			{
				CollectExitEvents(state.Motion, playback, 1f, 0);
			}

			StateMachineBehaviour[] behaviours = BehavioursOf(state);
			AnimatorStateInfo info = playback.Info();
			foreach (StateMachineBehaviour behaviour in behaviours)
			{
				Owner.PendingCallbacks.Add(new AnimatorPlayer.Callback { Kind = AnimatorPlayer.CallbackKind.Exit, Behaviour = behaviour, Info = info, Layer = Layer.Index });
			}

			// Machines left: from the state's machine up to (excluding) the common ancestor with the destination.
			for (RuntimeMachine m = state.Machine; m != null && !IsAncestorOf(m, destination); m = m.Parent)
			{
				if (m.IsRoot)
				{
					break;
				}

				foreach (StateMachineBehaviour behaviour in behaviours)
				{
					Owner.PendingCallbacks.Add(new AnimatorPlayer.Callback { Kind = AnimatorPlayer.CallbackKind.MachineExit, Behaviour = behaviour, Info = info, Layer = Layer.Index, MachineId = m.Id });
				}
			}
		}

		private void QueueUpdates()
		{
			if (Current != null)
			{
				QueueUpdate(Current);
			}

			if (Next != null)
			{
				QueueUpdate(Next);
			}
		}

		private void QueueUpdate(StatePlayback playback)
		{
			StateMachineBehaviour[] behaviours = BehavioursOf(playback.State);
			if (behaviours.Length == 0)
			{
				return;
			}

			AnimatorStateInfo info = playback.Info();
			foreach (StateMachineBehaviour behaviour in behaviours)
			{
				Owner.PendingCallbacks.Add(new AnimatorPlayer.Callback { Kind = AnimatorPlayer.CallbackKind.Update, Behaviour = behaviour, Info = info, Layer = Layer.Index });
				if (RootMotionSampled)
				{
					Owner.PendingCallbacks.Add(new AnimatorPlayer.Callback { Kind = AnimatorPlayer.CallbackKind.Move, Behaviour = behaviour, Info = info, Layer = Layer.Index });
				}
			}
		}

		private static bool IsAncestorOf(RuntimeMachine machine, RuntimeState state)
		{
			if (state == null)
			{
				return false;
			}

			for (RuntimeMachine m = state.Machine; m != null; m = m.Parent)
			{
				if (m == machine)
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}
}
