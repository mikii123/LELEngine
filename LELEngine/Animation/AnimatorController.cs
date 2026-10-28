using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	public enum LayerBlendingMode
	{
		/// <summary>The layer's pose replaces the layers below it (by its weight) on the properties its mask allows.</summary>
		Override,

		/// <summary>The layer's clips' differences to their first frame are added on top.</summary>
		Additive
	}

	/// <summary>Which transitions may interrupt a running transition (Unity's interruption source).</summary>
	public enum TransitionInterruptionSource
	{
		None,
		CurrentState,
		NextState,
		CurrentStateThenNextState,
		NextStateThenCurrentState
	}

	public enum BlendTreeType
	{
		/// <summary>One parameter picks between children placed on a line by their thresholds.</summary>
		Simple1D,

		/// <summary>Two parameters as a direction; children at positions around the origin (plus one at it).</summary>
		SimpleDirectional2D,

		/// <summary>Two parameters as a direction, gradient band interpolation in polar space.</summary>
		FreeformDirectional2D,

		/// <summary>Two parameters as a point, gradient band interpolation in the plane.</summary>
		FreeformCartesian2D,

		/// <summary>Each child's weight is a parameter of its own.</summary>
		Direct
	}

	/// <summary>Whether a state loops its motion.</summary>
	public enum StateLoopMode
	{
		/// <summary>As the clip says (blend trees: as their clips).</summary>
		FromClip,
		Loop,
		Once
	}

	/// <summary>What a state plays: a clip (<see cref="ClipMotion" />) or a blend of motions (<see cref="BlendTree" />).</summary>
	[Serializable]
	public abstract class Motion
	{
		public abstract string DisplayName { get; }

		/// <summary>Every clip the motion plays (nested trees included).</summary>
		public abstract void CollectClips(List<AnimationClip> clips);

		public abstract Motion Clone();

		public virtual bool UsesParameter(ulong parameterId)
		{
			return false;
		}

		public virtual void RemapParameter(ulong fromId, ulong toId)
		{ }
	}

	[Serializable]
	public sealed class ClipMotion : Motion
	{
		public AnimationClip clip;

		public override string DisplayName => clip != null ? clip.name : "(no clip)";

		public override void CollectClips(List<AnimationClip> clips)
		{
			if (clip != null && !clips.Contains(clip))
			{
				clips.Add(clip);
			}
		}

		public override Motion Clone()
		{
			return new ClipMotion { clip = clip };
		}
	}

	/// <summary>A child motion of a <see cref="BlendTree" /> with its place in the blend space.</summary>
	[Serializable]
	public sealed class BlendTreeChild
	{
		public Motion motion;

		/// <summary>1D: the parameter value the child plays alone at.</summary>
		public float threshold;

		/// <summary>2D: the parameter values the child plays alone at.</summary>
		public Vector2 position;

		/// <summary>Playback speed of the child.</summary>
		public float timeScale = 1f;

		/// <summary>Direct trees: the parameter giving the child's weight.</summary>
		public ulong directParameter;

		public BlendTreeChild Clone()
		{
			return new BlendTreeChild { motion = motion?.Clone(), threshold = threshold, position = position, timeScale = timeScale, directParameter = directParameter };
		}
	}

	/// <summary>
	///     Blends child motions (clips or nested trees) by one or two parameters, or by a parameter per child
	///     (<see cref="BlendTreeType" />). Children play with synchronized normalized time; the tree's length is the
	///     weighted average of its children's.
	/// </summary>
	[Serializable]
	public sealed class BlendTree : Motion
	{
		public string name = "Blend Tree";
		public BlendTreeType blendType = BlendTreeType.Simple1D;

		/// <summary>The parameter (1D), or the X parameter (2D).</summary>
		public ulong parameter;

		/// <summary>The Y parameter (2D).</summary>
		public ulong parameterY;

		public List<BlendTreeChild> children = new List<BlendTreeChild>();

		/// <summary>1D: thresholds are spread evenly between <see cref="minThreshold" /> and <see cref="maxThreshold" />.</summary>
		public bool automateThresholds = true;

		public float minThreshold;
		public float maxThreshold = 1f;

		public override string DisplayName => string.IsNullOrEmpty(name) ? "Blend Tree" : name;

		public override void CollectClips(List<AnimationClip> clips)
		{
			foreach (BlendTreeChild child in children)
			{
				child.motion?.CollectClips(clips);
			}
		}

		public override Motion Clone()
		{
			var copy = new BlendTree { name = name, blendType = blendType, parameter = parameter, parameterY = parameterY, automateThresholds = automateThresholds, minThreshold = minThreshold, maxThreshold = maxThreshold };
			foreach (BlendTreeChild child in children)
			{
				copy.children.Add(child.Clone());
			}

			return copy;
		}

		public override bool UsesParameter(ulong parameterId)
		{
			if (parameter == parameterId || parameterY == parameterId)
			{
				return true;
			}

			foreach (BlendTreeChild child in children)
			{
				if (child.directParameter == parameterId || (child.motion != null && child.motion.UsesParameter(parameterId)))
				{
					return true;
				}
			}

			return false;
		}

		public override void RemapParameter(ulong fromId, ulong toId)
		{
			if (parameter == fromId) parameter = toId;
			if (parameterY == fromId) parameterY = toId;
			foreach (BlendTreeChild child in children)
			{
				if (child.directParameter == fromId) child.directParameter = toId;
				child.motion?.RemapParameter(fromId, toId);
			}
		}

		/// <summary>Spreads the thresholds evenly (automate thresholds), keeping the children's order.</summary>
		public void SpreadThresholds()
		{
			int n = children.Count;
			for (int i = 0; i < n; i++)
			{
				children[i].threshold = n > 1 ? minThreshold + (maxThreshold - minThreshold) * i / (n - 1) : minThreshold;
			}
		}

		public BlendTreeChild AddChild(Motion motion)
		{
			var child = new BlendTreeChild { motion = motion };
			children.Add(child);
			if (automateThresholds)
			{
				SpreadThresholds();
			}

			return child;
		}

		public BlendTreeChild AddClip(AnimationClip clip, float threshold = 0f)
		{
			var child = new BlendTreeChild { motion = new ClipMotion { clip = clip }, threshold = threshold };
			children.Add(child);
			if (automateThresholds)
			{
				SpreadThresholds();
			}
			else
			{
				children.Sort((a, b) => a.threshold.CompareTo(b.threshold));
			}

			return child;
		}

		public BlendTreeChild AddClip(AnimationClip clip, Vector2 position)
		{
			var child = new BlendTreeChild { motion = new ClipMotion { clip = clip }, position = position };
			children.Add(child);
			return child;
		}
	}

	/// <summary>
	///     Code attached to a state: derive in game scripts, add serialized fields (edited on the state), and get the
	///     callbacks. Every animator gets its own instance (a copy of the state's), so fields may hold per-animator
	///     data. Callbacks run on the main thread after the animators updated.
	/// </summary>
	[Serializable]
	public abstract class StateMachineBehaviour
	{
		/// <summary>The animator's state and layer this instance serves (set before the first callback).</summary>
		[NonSerialized] public Animator animator;

		[NonSerialized] public int layerIndex;

		/// <summary>The first frame of the state (also when a transition to it starts).</summary>
		public virtual void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
		{ }

		/// <summary>Every frame the state plays (transitions in and out included), after the pose was written.</summary>
		public virtual void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
		{ }

		/// <summary>The last frame of the state (the transition away from it completed, or it was left at once).</summary>
		public virtual void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
		{ }

		/// <summary>A sub-state machine holding this behaviour's state was entered.</summary>
		public virtual void OnStateMachineEnter(Animator animator, ulong stateMachineId)
		{ }

		public virtual void OnStateMachineExit(Animator animator, ulong stateMachineId)
		{ }

		/// <summary>Root motion of this frame is about to be applied (set <see cref="Animator.deltaPosition" /> handling in the animator).</summary>
		public virtual void OnStateMove(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
		{ }
	}

	/// <summary>One entry of an <see cref="AnimationMask" />: a transform path and whether the layer animates it (and its children).</summary>
	[Serializable]
	public sealed class AnimationMaskEntry
	{
		public string path = "";
		public bool include = true;

		/// <summary>The entry applies to the transforms under the path as well (the deepest matching entry wins).</summary>
		public bool recursive = true;
	}

	/// <summary>
	///     Which transforms (by path from the animated root) a layer may animate: entries per path, the deepest
	///     matching one deciding, <see cref="includeOthers" /> for paths no entry covers. Properties of the root's
	///     components other than the Transform count as the root path.
	/// </summary>
	[Serializable]
	public sealed class AnimationMask
	{
		public List<AnimationMaskEntry> entries = new List<AnimationMaskEntry>();

		/// <summary>Paths no entry covers are animated.</summary>
		public bool includeOthers = true;

		public bool IsEmpty => entries.Count == 0 && includeOthers;

		/// <summary>True when the layer animates the transform at <paramref name="path" />.</summary>
		public bool Includes(string path)
		{
			path = path ?? "";
			AnimationMaskEntry best = null;
			int bestLength = -1;
			foreach (AnimationMaskEntry entry in entries)
			{
				string entryPath = entry.path ?? "";
				bool matches = entryPath == path || (entry.recursive && (entryPath.Length == 0 || (path.Length > entryPath.Length && path.StartsWith(entryPath, StringComparison.Ordinal) && path[entryPath.Length] == '/')));
				if (matches && entryPath.Length > bestLength)
				{
					best = entry;
					bestLength = entryPath.Length;
				}
			}

			return best != null ? best.include : includeOthers;
		}

		public AnimationMask Clone()
		{
			var copy = new AnimationMask { includeOthers = includeOthers };
			foreach (AnimationMaskEntry entry in entries)
			{
				copy.entries.Add(new AnimationMaskEntry { path = entry.path, include = entry.include, recursive = entry.recursive });
			}

			return copy;
		}
	}

	/// <summary>A transition between states (or from Any State / Entry / a sub-state machine, to a state, a sub-state machine or Exit).</summary>
	[Serializable]
	public sealed class AnimatorTransition
	{
		#region PublicFields

		public ulong id;

		/// <summary>Optional name shown on the edge and in lists.</summary>
		public string name = "";

		/// <summary>The destination state (0: see <see cref="destinationMachine" /> / <see cref="toExit" />).</summary>
		public ulong destinationState;

		/// <summary>The destination sub-state machine (its entry picks the state).</summary>
		public ulong destinationMachine;

		/// <summary>Leaves the state machine (back to the parent machine, or the layer restarts at its entry).</summary>
		public bool toExit;

		/// <summary>Every condition must hold (use a <see cref="ConditionGroup" /> for "any of").</summary>
		public List<AnimatorCondition> conditions = new List<AnimatorCondition>();

		/// <summary>The transition is only taken when the state's time passes <see cref="exitTime" />.</summary>
		public bool hasExitTime;

		/// <summary>In cycles of the state's motion (0.75: three quarters through; above 1 after that many cycles), or seconds when <see cref="exitTimeInSeconds" />.</summary>
		public float exitTime = 0.75f;

		public bool exitTimeInSeconds;

		/// <summary>Duration is in seconds (else in cycles of the source state).</summary>
		public bool fixedDuration = true;

		public float duration = 0.25f;

		/// <summary>Where the destination starts, in cycles of its motion.</summary>
		public float offset;

		public TransitionInterruptionSource interruptionSource = TransitionInterruptionSource.None;

		/// <summary>Only transitions before the running one in its list may interrupt it (else any of them).</summary>
		public bool orderedInterruption = true;

		/// <summary>Any State transitions: may re-enter the state already playing.</summary>
		public bool canTransitionToSelf;

		/// <summary>Never taken (editing aid).</summary>
		public bool mute;

		/// <summary>When any transition of a state is solo, only its solo transitions are taken.</summary>
		public bool solo;

		public string note = "";

		public bool HasDestination => destinationState != 0 || destinationMachine != 0 || toExit;

		#endregion

		#region PublicMethods

		public AnimatorTransition Clone()
		{
			var copy = (AnimatorTransition)MemberwiseClone();
			copy.conditions = new List<AnimatorCondition>();
			foreach (AnimatorCondition condition in conditions)
			{
				copy.conditions.Add(condition?.Clone());
			}

			return copy;
		}

		/// <summary>The conditions as one line ("Speed > 0.5 and Grounded"), "(always)" without any.</summary>
		public string DescribeConditions(AnimatorController controller)
		{
			if (conditions.Count == 0)
			{
				return hasExitTime ? "exit time " + exitTime.ToString("0.##") : "(always)";
			}

			var parts = new List<string>();
			foreach (AnimatorCondition condition in conditions)
			{
				parts.Add(condition?.Describe(controller) ?? "?");
			}

			return string.Join(" and ", parts);
		}

		public bool UsesParameter(ulong parameterId)
		{
			foreach (AnimatorCondition condition in conditions)
			{
				if (condition != null && condition.UsesParameter(parameterId))
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}

	/// <summary>A state of a state machine: a motion with playback settings, its transitions and behaviours.</summary>
	[Serializable]
	public sealed class AnimatorState
	{
		#region PublicFields

		public ulong id;
		public string name = "New State";
		public Vector2 position;

		/// <summary>What plays (null: nothing, the state only waits).</summary>
		public Motion motion;

		public float speed = 1f;

		/// <summary>Multiplies the speed by this parameter (0: none).</summary>
		public ulong speedParameter;

		/// <summary>The motion's normalized time is this parameter instead of advancing (0: none).</summary>
		public ulong motionTimeParameter;

		/// <summary>Start in cycles (0.5: half way).</summary>
		public float cycleOffset;

		public ulong cycleOffsetParameter;

		/// <summary>Properties the motion does not animate get the values they had when the animator bound (else they keep their last values).</summary>
		public bool writeDefaults = true;

		public StateLoopMode loop = StateLoopMode.FromClip;

		/// <summary>A label for scripts (AnimatorStateInfo.IsTag).</summary>
		public string tag = "";

		public List<AnimatorTransition> transitions = new List<AnimatorTransition>();

		/// <summary>Behaviours attached to the state (any <see cref="StateMachineBehaviour" /> class of the scripts).</summary>
		public List<StateMachineBehaviour> behaviours = new List<StateMachineBehaviour>();

		/// <summary>Palette index of the node's color (0: by kind).</summary>
		public int colorIndex;

		public string note = "";

		#endregion

		#region PublicMethods

		public AnimatorTransition AddTransition(AnimatorState destination)
		{
			var transition = new AnimatorTransition { id = AnimatorController.NewId(), destinationState = destination.id };
			transitions.Add(transition);
			return transition;
		}

		public AnimatorTransition AddExitTransition()
		{
			var transition = new AnimatorTransition { id = AnimatorController.NewId(), toExit = true };
			transitions.Add(transition);
			return transition;
		}

		public AnimatorState Clone()
		{
			var copy = new AnimatorState
			{
				id = id, name = name, position = position, motion = motion?.Clone(), speed = speed, speedParameter = speedParameter, motionTimeParameter = motionTimeParameter,
				cycleOffset = cycleOffset, cycleOffsetParameter = cycleOffsetParameter, writeDefaults = writeDefaults, loop = loop, tag = tag, colorIndex = colorIndex, note = note
			};
			foreach (AnimatorTransition transition in transitions)
			{
				copy.transitions.Add(transition.Clone());
			}

			foreach (StateMachineBehaviour behaviour in behaviours)
			{
				copy.behaviours.Add(behaviour != null ? AnimatorController.CloneBehaviour(behaviour) : null);
			}

			return copy;
		}

		public override string ToString()
		{
			return name;
		}

		#endregion
	}

	/// <summary>A sticky note on the graph.</summary>
	[Serializable]
	public sealed class GraphNote
	{
		public ulong id;
		public Vector2 position;
		public Vector2 size = new Vector2(200f, 80f);
		public string text = "";
		public int colorIndex;
	}

	/// <summary>A titled group frame around nodes of the graph.</summary>
	[Serializable]
	public sealed class GraphGroupData
	{
		public ulong id;
		public string title = "Group";
		public List<ulong> members = new List<ulong>();
		public int colorIndex;
	}

	/// <summary>A parent parameter feeding a parameter of a sub-state machine's controller asset (same value type).</summary>
	[Serializable]
	public sealed class ParameterMapping
	{
		/// <summary>Id of the parameter in the sub-machine's controller asset.</summary>
		public ulong childParameter;

		/// <summary>Id of the parameter in the controller holding the sub-machine (0: the child keeps a value of its own).</summary>
		public ulong parentParameter;
	}

	/// <summary>
	///     A state machine: states, nested machines, the transitions from Any State and Entry, the default state,
	///     and the transitions taken when a state inside reaches Exit (listed on the machine's node in the parent).
	///     With <see cref="controller" /> set, the machine is a reference to another controller asset: a layer of that
	///     asset plays in its place (its own states, transitions and behaviours), its parameters fed by
	///     <see cref="parameterMappings" /> (the rest keep private values per animator, addressed as "Node/Parameter").
	/// </summary>
	[Serializable]
	public sealed class AnimatorStateMachine
	{
		#region PublicFields

		public ulong id;
		public string name = "State Machine";

		/// <summary>The machine's node position in its parent (unused for a layer's root machine).</summary>
		public Vector2 position;

		/// <summary>Another controller asset whose layer plays here (null: the machine's own content below).</summary>
		public AnimatorController controller;

		/// <summary>Which layer of <see cref="controller" /> plays here.</summary>
		public int controllerLayer;

		/// <summary>Parent parameters feeding the asset's parameters.</summary>
		public List<ParameterMapping> parameterMappings = new List<ParameterMapping>();

		/// <summary>The machine plays another controller asset.</summary>
		public bool IsExternal => controller != null;

		/// <summary>The asset's machine that plays here (null when not external or the layer is missing).</summary>
		public AnimatorStateMachine ExternalMachine => controller != null && controllerLayer >= 0 && controllerLayer < controller.layers.Count ? controller.layers[controllerLayer].stateMachine : null;

		public List<AnimatorState> states = new List<AnimatorState>();
		public List<AnimatorStateMachine> subMachines = new List<AnimatorStateMachine>();

		/// <summary>Transitions checked every frame from any state of this machine (and its sub-machines).</summary>
		public List<AnimatorTransition> anyStateTransitions = new List<AnimatorTransition>();

		/// <summary>Taken when the machine is entered: the first whose conditions hold, else <see cref="defaultState" />.</summary>
		public List<AnimatorTransition> entryTransitions = new List<AnimatorTransition>();

		/// <summary>Taken in the parent machine when a state of this machine exits (this machine's node is the source).</summary>
		public List<AnimatorTransition> transitions = new List<AnimatorTransition>();

		public ulong defaultState;

		public Vector2 entryPosition = new Vector2(50f, 100f);
		public Vector2 anyStatePosition = new Vector2(50f, 220f);
		public Vector2 exitPosition = new Vector2(700f, 100f);

		public List<GraphNote> notes = new List<GraphNote>();
		public List<GraphGroupData> groups = new List<GraphGroupData>();

		/// <summary>The editor's view of this machine.</summary>
		public Vector2 viewOffset;

		public float viewZoom = 1f;

		public int colorIndex;
		public string note = "";

		#endregion

		#region PublicMethods

		public AnimatorState AddState(string stateName, Vector2 at)
		{
			var state = new AnimatorState { id = AnimatorController.NewId(), name = UniqueStateName(stateName), position = at };
			states.Add(state);
			if (defaultState == 0)
			{
				defaultState = state.id;
			}

			return state;
		}

		public AnimatorState AddState(string stateName, AnimationClip clip, Vector2 at)
		{
			AnimatorState state = AddState(stateName, at);
			state.motion = new ClipMotion { clip = clip };
			return state;
		}

		public AnimatorStateMachine AddSubMachine(string machineName, Vector2 at)
		{
			var machine = new AnimatorStateMachine { id = AnimatorController.NewId(), name = machineName, position = at };
			subMachines.Add(machine);
			return machine;
		}

		public AnimatorTransition AddAnyStateTransition(AnimatorState destination)
		{
			var transition = new AnimatorTransition { id = AnimatorController.NewId(), destinationState = destination.id };
			anyStateTransitions.Add(transition);
			return transition;
		}

		public AnimatorTransition AddEntryTransition(AnimatorState destination)
		{
			var transition = new AnimatorTransition { id = AnimatorController.NewId(), destinationState = destination.id };
			entryTransitions.Add(transition);
			return transition;
		}

		public AnimatorState FindState(string stateName)
		{
			foreach (AnimatorState state in states)
			{
				if (state.name == stateName)
				{
					return state;
				}
			}

			return null;
		}

		public AnimatorState FindStateById(ulong stateId)
		{
			foreach (AnimatorState state in states)
			{
				if (state.id == stateId)
				{
					return state;
				}
			}

			return null;
		}

		public string UniqueStateName(string baseName)
		{
			string candidate = string.IsNullOrWhiteSpace(baseName) ? "New State" : baseName;
			int n = 1;
			while (FindState(candidate) != null || FindSubMachine(candidate) != null)
			{
				candidate = baseName + " " + ++n;
			}

			return candidate;
		}

		public AnimatorStateMachine FindSubMachine(string machineName)
		{
			foreach (AnimatorStateMachine machine in subMachines)
			{
				if (machine.name == machineName)
				{
					return machine;
				}
			}

			return null;
		}

		/// <summary>Every state of this machine and the machines below it.</summary>
		public void CollectStates(List<AnimatorState> result)
		{
			result.AddRange(states);
			foreach (AnimatorStateMachine machine in subMachines)
			{
				machine.CollectStates(result);
			}
		}

		public void CollectMachines(List<AnimatorStateMachine> result)
		{
			result.Add(this);
			foreach (AnimatorStateMachine machine in subMachines)
			{
				machine.CollectMachines(result);
			}
		}

		/// <summary>Every transition of the machine and below: states', any state, entry and the machines' exit transitions.</summary>
		public void CollectTransitions(List<AnimatorTransition> result)
		{
			foreach (AnimatorState state in states)
			{
				result.AddRange(state.transitions);
			}

			result.AddRange(anyStateTransitions);
			result.AddRange(entryTransitions);
			foreach (AnimatorStateMachine machine in subMachines)
			{
				result.AddRange(machine.transitions);
				machine.CollectTransitions(result);
			}
		}

		/// <summary>The mapping of a child parameter (null when it keeps its own value).</summary>
		public ParameterMapping FindMapping(ulong childParameter)
		{
			foreach (ParameterMapping mapping in parameterMappings)
			{
				if (mapping.childParameter == childParameter)
				{
					return mapping;
				}
			}

			return null;
		}

		/// <summary>Maps a child parameter to a parent one (0 removes the mapping).</summary>
		public void SetMapping(ulong childParameter, ulong parentParameter)
		{
			ParameterMapping mapping = FindMapping(childParameter);
			if (parentParameter == 0)
			{
				if (mapping != null)
				{
					parameterMappings.Remove(mapping);
				}

				return;
			}

			if (mapping == null)
			{
				parameterMappings.Add(new ParameterMapping { childParameter = childParameter, parentParameter = parentParameter });
			}
			else
			{
				mapping.parentParameter = parentParameter;
			}
		}

		public AnimatorStateMachine Clone()
		{
			var copy = new AnimatorStateMachine
			{
				id = id, name = name, position = position, defaultState = defaultState, entryPosition = entryPosition, anyStatePosition = anyStatePosition, exitPosition = exitPosition,
				viewOffset = viewOffset, viewZoom = viewZoom, colorIndex = colorIndex, note = note, controller = controller, controllerLayer = controllerLayer
			};
			foreach (ParameterMapping mapping in parameterMappings) copy.parameterMappings.Add(new ParameterMapping { childParameter = mapping.childParameter, parentParameter = mapping.parentParameter });
			foreach (AnimatorState state in states) copy.states.Add(state.Clone());
			foreach (AnimatorStateMachine machine in subMachines) copy.subMachines.Add(machine.Clone());
			foreach (AnimatorTransition t in anyStateTransitions) copy.anyStateTransitions.Add(t.Clone());
			foreach (AnimatorTransition t in entryTransitions) copy.entryTransitions.Add(t.Clone());
			foreach (AnimatorTransition t in transitions) copy.transitions.Add(t.Clone());
			foreach (GraphNote n in notes) copy.notes.Add(new GraphNote { id = n.id, position = n.position, size = n.size, text = n.text, colorIndex = n.colorIndex });
			foreach (GraphGroupData g in groups) copy.groups.Add(new GraphGroupData { id = g.id, title = g.title, members = new List<ulong>(g.members), colorIndex = g.colorIndex });
			return copy;
		}

		public override string ToString()
		{
			return name;
		}

		#endregion
	}

	/// <summary>A layer of an <see cref="AnimatorController" />: a state machine blended over the layers below by weight, mask and mode.</summary>
	[Serializable]
	public sealed class AnimatorLayer
	{
		public ulong id;
		public string name = "Base Layer";

		/// <summary>Blend weight over the layers below (the first layer always has 1).</summary>
		[Range(0f, 1f)] public float weight = 1f;

		public LayerBlendingMode blending = LayerBlendingMode.Override;

		/// <summary>Which transforms the layer animates (empty: all).</summary>
		public AnimationMask mask = new AnimationMask();

		public AnimatorStateMachine stateMachine = new AnimatorStateMachine { name = "Base Layer" };

		/// <summary>A disabled layer is skipped entirely.</summary>
		public bool enabled = true;

		public string note = "";

		public AnimatorLayer Clone()
		{
			return new AnimatorLayer { id = id, name = name, weight = weight, blending = blending, mask = mask?.Clone() ?? new AnimationMask(), stateMachine = stateMachine?.Clone() ?? new AnimatorStateMachine(), enabled = enabled, note = note };
		}

		public override string ToString()
		{
			return name;
		}
	}

	/// <summary>Values new transitions get in the editor (Unity's are has exit time, 0.75, fixed 0.25 s).</summary>
	[Serializable]
	public sealed class TransitionDefaults
	{
		public bool hasExitTime = true;
		public float exitTime = 0.75f;
		public bool fixedDuration = true;
		public float duration = 0.25f;
		public TransitionInterruptionSource interruptionSource = TransitionInterruptionSource.None;

		public void Apply(AnimatorTransition transition)
		{
			transition.hasExitTime = hasExitTime;
			transition.exitTime = exitTime;
			transition.fixedDuration = fixedDuration;
			transition.duration = duration;
			transition.interruptionSource = interruptionSource;
		}
	}

	/// <summary>
	///     An animator controller asset (".animator": readable JSON in the project, binary in builds): parameters of
	///     any serializable type (<see cref="AnimatorValue" />), layers of state machines with sub-machines, any state
	///     and entry transitions, blend trees and state behaviours. Loaded once per path and shared; the Animator
	///     window edits it in place and bumps <see cref="Version" />, so animators rebuild their runtime.
	/// </summary>
	public sealed class AnimatorController : IAsset, ISerializationCallbackReceiver
	{
		#region PublicFields

		public const string Extension = ".animator";

		public string AssetPath { get; internal set; }
		public string SubAssetName => null;
		public string name = "";

		public List<AnimatorParameter> parameters = new List<AnimatorParameter>();
		public List<AnimatorLayer> layers = new List<AnimatorLayer>();

		/// <summary>What new transitions start with (edited in the controller's inspector).</summary>
		public TransitionDefaults transitionDefaults = new TransitionDefaults();

		public int FormatVersion = 1;

		/// <summary>Bumped by every edit; animators recompile when it changes.</summary>
		[NonSerialized] public int Version = 1;

		public event Action Changed;

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, AnimatorController> cache = new Dictionary<string, AnimatorController>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- files

		public static AnimatorController Load(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			lock (cache)
			{
				if (cache.TryGetValue(assetPath, out AnimatorController cached))
				{
					return cached;
				}
			}

			AnimatorController controller = null;
			try
			{
				byte[] data = File.ReadAllBytes(AssetDatabase.ToAbsolute(assetPath));
				if (Documents.IsDocument(data))
				{
					controller = Documents.ReadObject(data, typeof(AnimatorController), null, new SerializationContext { Location = assetPath }) as AnimatorController;
				}
				else
				{
					Debug.LogError($"[Animation] {assetPath} is not an animator controller document");
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Animation] Cannot load {assetPath}: {e.Message}");
			}

			controller = controller ?? new AnimatorController();
			controller.AssetPath = assetPath.Replace('\\', '/');
			controller.name = Path.GetFileNameWithoutExtension(assetPath);
			lock (cache)
			{
				cache[assetPath] = controller;
			}

			return controller;
		}

		public static AnimatorController FindLoaded(string assetPath)
		{
			lock (cache)
			{
				return assetPath != null && cache.TryGetValue(assetPath, out AnimatorController controller) ? controller : null;
			}
		}

		public void Save(string assetPath = null)
		{
			assetPath = assetPath ?? AssetPath;
			if (string.IsNullOrEmpty(assetPath))
			{
				throw new InvalidOperationException("The controller has no asset path");
			}

			AssetPath = assetPath.Replace('\\', '/');
			name = Path.GetFileNameWithoutExtension(AssetPath);
			Documents.WriteObjectFile(AssetDatabase.ToAbsolute(AssetPath), this);
			AssetDatabase.EnsureMeta(AssetPath);
			lock (cache)
			{
				cache[AssetPath] = this;
			}
		}

		public byte[] ToBytes(DocumentFormat format = DocumentFormat.Binary)
		{
			return Documents.WriteObject(this, format, new SerializationContext { Location = name ?? "AnimatorController" });
		}

		/// <summary>Replaces the content from a document written by <see cref="ToBytes" /> and marks the controller changed.</summary>
		public void ApplyBytes(byte[] data)
		{
			parameters.Clear();
			layers.Clear();
			Documents.ReadObject(data, typeof(AnimatorController), this, new SerializationContext { Location = name ?? "AnimatorController" });
			MarkChanged();
		}

		public AnimatorController Clone()
		{
			var copy = Documents.ReadObject(ToBytes(), typeof(AnimatorController), null) as AnimatorController ?? new AnimatorController();
			copy.AssetPath = AssetPath;
			copy.name = name;
			return copy;
		}

		public static void ClearCache()
		{
			lock (cache)
			{
				cache.Clear();
			}
		}

		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (cache)
			{
				AssetDatabase.RekeyMoved(cache, fromPath, toPath, (controller, path) =>
				{
					controller.AssetPath = path;
					controller.name = Path.GetFileNameWithoutExtension(path);
				});
			}
		}

		public static void Forget(string assetPath)
		{
			lock (cache)
			{
				cache.Remove(assetPath);
			}
		}

		public static List<AnimatorController> GetLoaded()
		{
			lock (cache)
			{
				return new List<AnimatorController>(cache.Values);
			}
		}

		/// <summary>A controller with one empty base layer (what the project panel creates).</summary>
		public static AnimatorController CreateDefault(string controllerName)
		{
			var controller = new AnimatorController { name = controllerName };
			controller.AddLayer("Base Layer");
			return controller;
		}

		// ---------------------------------------------------------------- editing

		/// <summary>Call after any edit: bumps <see cref="Version" /> and raises <see cref="Changed" />.</summary>
		public void MarkChanged()
		{
			Version++;
			Changed?.Invoke();
		}

		public static ulong NewId()
		{
			ulong id;
			do
			{
				id = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
			}
			while (id == 0);

			return id;
		}

		/// <summary>A copy of a behaviour with its serialized fields (a new instance per animator).</summary>
		public static StateMachineBehaviour CloneBehaviour(StateMachineBehaviour behaviour)
		{
			if (behaviour == null)
			{
				return null;
			}

			try
			{
				byte[] data = Documents.WriteObject(behaviour, DocumentFormat.Binary);
				return Documents.ReadObject(data, behaviour.GetType(), null, null) as StateMachineBehaviour;
			}
			catch (Exception e)
			{
				Debug.LogError("[Animation] Cannot copy the behaviour " + behaviour.GetType().Name + ": " + e.Message);
				return null;
			}
		}

		public AnimatorParameter AddParameter(string parameterName, AnimatorValue defaultValue)
		{
			var parameter = new AnimatorParameter { id = NewId(), name = UniqueParameterName(parameterName), defaultValue = defaultValue ?? new FloatValue() };
			parameters.Add(parameter);
			return parameter;
		}

		public AnimatorParameter AddFloat(string parameterName, float defaultValue = 0f) => AddParameter(parameterName, new FloatValue { value = defaultValue });
		public AnimatorParameter AddInt(string parameterName, int defaultValue = 0) => AddParameter(parameterName, new IntValue { value = defaultValue });
		public AnimatorParameter AddBool(string parameterName, bool defaultValue = false) => AddParameter(parameterName, new BoolValue { value = defaultValue });
		public AnimatorParameter AddTrigger(string parameterName) => AddParameter(parameterName, new TriggerValue());

		public string UniqueParameterName(string baseName)
		{
			string candidate = string.IsNullOrWhiteSpace(baseName) ? "Parameter" : baseName;
			int n = 1;
			while (FindParameter(candidate) != null)
			{
				candidate = baseName + " " + ++n;
			}

			return candidate;
		}

		/// <summary>Removes the parameter and every condition, blend tree reference and sub-machine mapping using it.</summary>
		public void RemoveParameter(AnimatorParameter parameter)
		{
			if (parameter == null || !parameters.Remove(parameter))
			{
				return;
			}

			foreach (AnimatorLayer layer in layers)
			{
				var transitions = new List<AnimatorTransition>();
				layer.stateMachine.CollectTransitions(transitions);
				foreach (AnimatorTransition transition in transitions)
				{
					transition.conditions.RemoveAll(c => c == null || c.UsesParameter(parameter.id) && c is ParameterCondition);
					foreach (AnimatorCondition condition in transition.conditions)
					{
						condition.RemapParameter(parameter.id, 0);
					}
				}

				var states = new List<AnimatorState>();
				layer.stateMachine.CollectStates(states);
				foreach (AnimatorState state in states)
				{
					if (state.speedParameter == parameter.id) state.speedParameter = 0;
					if (state.motionTimeParameter == parameter.id) state.motionTimeParameter = 0;
					if (state.cycleOffsetParameter == parameter.id) state.cycleOffsetParameter = 0;
					state.motion?.RemapParameter(parameter.id, 0);
				}

				var machines = new List<AnimatorStateMachine>();
				layer.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine machine in machines)
				{
					machine.parameterMappings.RemoveAll(m => m.parentParameter == parameter.id);
				}
			}
		}

		/// <summary>
		///     The controller assets the sub-machines of this controller play, transitively (cycles stop the walk), this
		///     controller first.
		/// </summary>
		public List<AnimatorController> CollectControllers()
		{
			var result = new List<AnimatorController>();
			CollectControllers(this, result);
			return result;
		}

		private static void CollectControllers(AnimatorController controller, List<AnimatorController> result)
		{
			if (controller == null || result.Contains(controller))
			{
				return;
			}

			result.Add(controller);
			foreach (AnimatorLayer layer in controller.layers)
			{
				var machines = new List<AnimatorStateMachine>();
				layer.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine machine in machines)
				{
					if (machine.controller != null)
					{
						CollectControllers(machine.controller, result);
					}
				}
			}
		}

		/// <summary>The sum of the versions of this controller and the assets its sub-machines play (a change anywhere rebuilds the players).</summary>
		public int CombinedVersion
		{
			get
			{
				int sum = 0;
				foreach (AnimatorController controller in CollectControllers())
				{
					sum += controller.Version;
				}

				return sum;
			}
		}

		/// <summary>True when the asset embeds <paramref name="candidate" /> somewhere (or is it): embedding it back would loop.</summary>
		public bool Embeds(AnimatorController candidate)
		{
			return candidate != null && CollectControllers().Contains(candidate);
		}

		public AnimatorParameter FindParameter(ulong id)
		{
			if (id == 0)
			{
				return null;
			}

			foreach (AnimatorParameter parameter in parameters)
			{
				if (parameter.id == id)
				{
					return parameter;
				}
			}

			return null;
		}

		public AnimatorParameter FindParameter(string parameterName)
		{
			foreach (AnimatorParameter parameter in parameters)
			{
				if (parameter.name == parameterName)
				{
					return parameter;
				}
			}

			return null;
		}

		/// <summary>Where a parameter is used: states (speed, motion time, blend trees) and transitions (conditions).</summary>
		public List<string> FindParameterUses(ulong parameterId)
		{
			var uses = new List<string>();
			foreach (AnimatorLayer layer in layers)
			{
				var states = new List<AnimatorState>();
				layer.stateMachine.CollectStates(states);
				foreach (AnimatorState state in states)
				{
					if (state.speedParameter == parameterId) uses.Add(layer.name + ": " + state.name + " (speed)");
					if (state.motionTimeParameter == parameterId) uses.Add(layer.name + ": " + state.name + " (motion time)");
					if (state.cycleOffsetParameter == parameterId) uses.Add(layer.name + ": " + state.name + " (cycle offset)");
					if (state.motion != null && state.motion.UsesParameter(parameterId)) uses.Add(layer.name + ": " + state.name + " (blend tree)");
					foreach (AnimatorTransition transition in state.transitions)
					{
						if (transition.UsesParameter(parameterId)) uses.Add(layer.name + ": " + state.name + " -> " + DescribeDestination(transition));
					}
				}

				var machines = new List<AnimatorStateMachine>();
				layer.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine machine in machines)
				{
					foreach (AnimatorTransition transition in machine.anyStateTransitions)
					{
						if (transition.UsesParameter(parameterId)) uses.Add(layer.name + ": Any State -> " + DescribeDestination(transition));
					}

					foreach (AnimatorTransition transition in machine.entryTransitions)
					{
						if (transition.UsesParameter(parameterId)) uses.Add(layer.name + ": Entry -> " + DescribeDestination(transition));
					}

					foreach (AnimatorTransition transition in machine.transitions)
					{
						if (transition.UsesParameter(parameterId)) uses.Add(layer.name + ": " + machine.name + " (exit) -> " + DescribeDestination(transition));
					}

					foreach (ParameterMapping mapping in machine.parameterMappings)
					{
						if (mapping.parentParameter == parameterId)
						{
							uses.Add(layer.name + ": " + machine.name + " (feeds " + (machine.controller?.FindParameter(mapping.childParameter)?.name ?? "?") + " of " + (machine.controller?.name ?? "?") + ")");
						}
					}
				}
			}

			return uses;
		}

		public AnimatorLayer AddLayer(string layerName)
		{
			var layer = new AnimatorLayer { id = NewId(), name = layerName, stateMachine = new AnimatorStateMachine { id = NewId(), name = layerName } };
			layers.Add(layer);
			return layer;
		}

		public AnimatorLayer FindLayer(ulong id)
		{
			foreach (AnimatorLayer layer in layers)
			{
				if (layer.id == id)
				{
					return layer;
				}
			}

			return null;
		}

		public AnimatorLayer FindLayer(string layerName)
		{
			foreach (AnimatorLayer layer in layers)
			{
				if (layer.name == layerName)
				{
					return layer;
				}
			}

			return null;
		}

		/// <summary>The state with the id anywhere in the controller (and its machine).</summary>
		public AnimatorState FindState(ulong stateId, out AnimatorStateMachine machine, out AnimatorLayer layer)
		{
			foreach (AnimatorLayer l in layers)
			{
				var machines = new List<AnimatorStateMachine>();
				l.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine m in machines)
				{
					AnimatorState state = m.FindStateById(stateId);
					if (state != null)
					{
						machine = m;
						layer = l;
						return state;
					}
				}
			}

			machine = null;
			layer = null;
			return null;
		}

		public AnimatorState FindState(ulong stateId)
		{
			return FindState(stateId, out _, out _);
		}

		/// <summary>A state by name ("Idle", or "Sub Machine/Idle" for a nested one) in a layer (0: the first).</summary>
		public AnimatorState FindState(string statePath, int layerIndex = 0)
		{
			if (layerIndex < 0 || layerIndex >= layers.Count || string.IsNullOrEmpty(statePath))
			{
				return null;
			}

			AnimatorStateMachine machine = layers[layerIndex].stateMachine;
			string[] parts = statePath.Split('/');
			for (int i = 0; i < parts.Length - 1; i++)
			{
				machine = machine.FindSubMachine(parts[i]);
				if (machine == null)
				{
					return null;
				}
			}

			AnimatorState state = machine.FindState(parts[parts.Length - 1]);
			if (state == null && parts.Length == 1)
			{
				// A short name anywhere in the layer.
				var all = new List<AnimatorState>();
				layers[layerIndex].stateMachine.CollectStates(all);
				foreach (AnimatorState candidate in all)
				{
					if (candidate.name == statePath)
					{
						return candidate;
					}
				}
			}

			return state;
		}

		public AnimatorStateMachine FindMachine(ulong machineId, out AnimatorLayer layer)
		{
			foreach (AnimatorLayer l in layers)
			{
				var machines = new List<AnimatorStateMachine>();
				l.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine m in machines)
				{
					if (m.id == machineId)
					{
						layer = l;
						return m;
					}
				}
			}

			layer = null;
			return null;
		}

		public AnimatorStateMachine FindMachine(ulong machineId)
		{
			return FindMachine(machineId, out _);
		}

		/// <summary>The machine containing <paramref name="machine" /> (null for a layer's root).</summary>
		public AnimatorStateMachine ParentOf(AnimatorStateMachine machine)
		{
			foreach (AnimatorLayer layer in layers)
			{
				AnimatorStateMachine parent = ParentOf(layer.stateMachine, machine);
				if (parent != null)
				{
					return parent;
				}
			}

			return null;
		}

		/// <summary>The machine holding the state.</summary>
		public AnimatorStateMachine MachineOf(AnimatorState state)
		{
			FindState(state.id, out AnimatorStateMachine machine, out _);
			return machine;
		}

		/// <summary>The machine holding a sub-machine node (null for a root or an unknown node).</summary>
		public AnimatorStateMachine MachineOfNode(AnimatorStateMachine node)
		{
			return node != null ? ParentOf(node) : null;
		}

		/// <summary>"Sub Machine/Idle": the state's path in its layer.</summary>
		public string FullName(AnimatorState state)
		{
			AnimatorState found = FindState(state.id, out AnimatorStateMachine machine, out AnimatorLayer layer);
			if (found == null)
			{
				return state.name;
			}

			var parts = new List<string> { state.name };
			for (AnimatorStateMachine m = machine; m != null && m != layer.stateMachine; m = ParentOf(m))
			{
				parts.Insert(0, m.name);
			}

			return string.Join("/", parts);
		}

		public AnimatorTransition FindTransition(ulong transitionId)
		{
			foreach (AnimatorLayer layer in layers)
			{
				var transitions = new List<AnimatorTransition>();
				layer.stateMachine.CollectTransitions(transitions);
				foreach (AnimatorTransition transition in transitions)
				{
					if (transition.id == transitionId)
					{
						return transition;
					}
				}
			}

			return null;
		}

		/// <summary>Removes a state with every transition leading to it (in its layer).</summary>
		public void RemoveState(AnimatorState state)
		{
			AnimatorState found = FindState(state.id, out AnimatorStateMachine machine, out AnimatorLayer layer);
			if (found == null)
			{
				return;
			}

			machine.states.Remove(found);
			if (machine.defaultState == found.id)
			{
				machine.defaultState = machine.states.Count > 0 ? machine.states[0].id : 0;
			}

			RemoveTransitionsTo(layer, found.id, 0);
			foreach (GraphGroupData group in machine.groups)
			{
				group.members.Remove(found.id);
			}
		}

		/// <summary>Removes a sub-state machine with its content and every transition leading to it.</summary>
		public void RemoveMachine(AnimatorStateMachine machine)
		{
			AnimatorStateMachine parent = ParentOf(machine);
			if (parent == null)
			{
				return;
			}

			FindMachine(machine.id, out AnimatorLayer layer);
			parent.subMachines.Remove(machine);
			RemoveTransitionsTo(layer, 0, machine.id);
			foreach (GraphGroupData group in parent.groups)
			{
				group.members.Remove(machine.id);
			}
		}

		public bool RemoveTransition(AnimatorTransition transition)
		{
			foreach (AnimatorLayer layer in layers)
			{
				var machines = new List<AnimatorStateMachine>();
				layer.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine machine in machines)
				{
					if (machine.anyStateTransitions.Remove(transition) || machine.entryTransitions.Remove(transition) || machine.transitions.Remove(transition))
					{
						return true;
					}

					foreach (AnimatorState state in machine.states)
					{
						if (state.transitions.Remove(transition))
						{
							return true;
						}
					}
				}
			}

			return false;
		}

		/// <summary>The list holding a transition and what owns it ("state", "any", "entry", "machine").</summary>
		public List<AnimatorTransition> OwnerOf(AnimatorTransition transition, out object owner, out string kind)
		{
			foreach (AnimatorLayer layer in layers)
			{
				var machines = new List<AnimatorStateMachine>();
				layer.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine machine in machines)
				{
					if (machine.anyStateTransitions.Contains(transition)) { owner = machine; kind = "any"; return machine.anyStateTransitions; }
					if (machine.entryTransitions.Contains(transition)) { owner = machine; kind = "entry"; return machine.entryTransitions; }
					if (machine.transitions.Contains(transition)) { owner = machine; kind = "machine"; return machine.transitions; }
					foreach (AnimatorState state in machine.states)
					{
						if (state.transitions.Contains(transition)) { owner = state; kind = "state"; return state.transitions; }
					}
				}
			}

			owner = null;
			kind = null;
			return null;
		}

		public string DescribeDestination(AnimatorTransition transition)
		{
			if (transition.toExit) return "Exit";
			if (transition.destinationState != 0) return FindState(transition.destinationState)?.name ?? "(missing state)";
			if (transition.destinationMachine != 0) return FindMachine(transition.destinationMachine)?.name ?? "(missing machine)";
			return "(nowhere)";
		}

		/// <summary>Every clip any state of the controller plays, the sub-machines' assets included.</summary>
		public List<AnimationClip> CollectClips()
		{
			var clips = new List<AnimationClip>();
			foreach (AnimatorController controller in CollectControllers())
			{
				foreach (AnimatorLayer layer in controller.layers)
				{
					var states = new List<AnimatorState>();
					layer.stateMachine.CollectStates(states);
					foreach (AnimatorState state in states)
					{
						state.motion?.CollectClips(clips);
					}
				}
			}

			return clips;
		}

		/// <summary>Problems a human should look at (the Animator window shows them).</summary>
		public List<string> Validate()
		{
			var problems = new List<string>();
			if (layers.Count == 0)
			{
				problems.Add("The controller has no layer.");
			}

			var seenIds = new HashSet<ulong>();
			foreach (AnimatorParameter parameter in parameters)
			{
				if (parameter.defaultValue == null) problems.Add("Parameter '" + parameter.name + "' has no type.");
				if (!seenIds.Add(parameter.id)) problems.Add("Parameter '" + parameter.name + "' shares its id with another one.");
			}

			foreach (AnimatorLayer layer in layers)
			{
				var states = new List<AnimatorState>();
				layer.stateMachine.CollectStates(states);
				if (states.Count == 0)
				{
					problems.Add("Layer '" + layer.name + "' has no state.");
				}
				else if (layer.stateMachine.defaultState == 0 || layer.stateMachine.FindStateById(layer.stateMachine.defaultState) == null)
				{
					problems.Add("Layer '" + layer.name + "' has no default state.");
				}

				foreach (AnimatorState state in states)
				{
					if (!seenIds.Add(state.id)) problems.Add("State '" + state.name + "' shares its id with another node.");
					if (state.motion is ClipMotion clipMotion && clipMotion.clip == null) problems.Add("State '" + state.name + "' has no clip.");
					if (state.motion is BlendTree tree) ValidateTree(tree, state.name, problems);
					foreach (AnimatorTransition transition in state.transitions)
					{
						ValidateTransition(transition, state.name, problems, true);
					}
				}

				var machines = new List<AnimatorStateMachine>();
				layer.stateMachine.CollectMachines(machines);
				foreach (AnimatorStateMachine machine in machines)
				{
					foreach (AnimatorTransition transition in machine.anyStateTransitions) ValidateTransition(transition, "Any State", problems, true);
					foreach (AnimatorTransition transition in machine.entryTransitions) ValidateTransition(transition, "Entry", problems, false);
					foreach (AnimatorTransition transition in machine.transitions) ValidateTransition(transition, machine.name, problems, false);
					if (machine.IsExternal)
					{
						ValidateExternal(machine, layer, problems);
					}
				}
			}

			return problems;
		}

		private void ValidateExternal(AnimatorStateMachine machine, AnimatorLayer layer, List<string> problems)
		{
			AnimatorController asset = machine.controller;
			if (asset == this || asset.Embeds(this))
			{
				problems.Add("Sub-state machine '" + machine.name + "' plays '" + asset.name + "', which embeds this controller: the loop is cut (it plays nothing).");
				return;
			}

			if (machine.ExternalMachine == null)
			{
				problems.Add("Sub-state machine '" + machine.name + "': '" + asset.name + "' has no layer " + machine.controllerLayer + ".");
			}

			foreach (ParameterMapping mapping in machine.parameterMappings)
			{
				AnimatorParameter child = asset.FindParameter(mapping.childParameter);
				AnimatorParameter parent = FindParameter(mapping.parentParameter);
				if (child == null)
				{
					problems.Add("Sub-state machine '" + machine.name + "' maps a parameter '" + asset.name + "' no longer has.");
				}
				else if (parent == null)
				{
					problems.Add("Sub-state machine '" + machine.name + "' feeds '" + child.name + "' from a parameter this controller no longer has.");
				}
				else if (parent.ValueType != child.ValueType)
				{
					problems.Add("Sub-state machine '" + machine.name + "': '" + parent.name + "' (" + parent.TypeLabel + ") cannot feed '" + child.name + "' (" + child.TypeLabel + ").");
				}
			}

			if (machine.states.Count > 0 || machine.subMachines.Count > 0)
			{
				problems.Add("Sub-state machine '" + machine.name + "' plays '" + asset.name + "': its own " + machine.states.Count + " states are ignored.");
			}
		}

		public void OnBeforeSerialize()
		{ }

		public void OnAfterDeserialize()
		{
			parameters = parameters ?? new List<AnimatorParameter>();
			layers = layers ?? new List<AnimatorLayer>();
			transitionDefaults = transitionDefaults ?? new TransitionDefaults();
			foreach (AnimatorLayer layer in layers)
			{
				layer.mask = layer.mask ?? new AnimationMask();
				layer.stateMachine = layer.stateMachine ?? new AnimatorStateMachine { id = NewId(), name = layer.name };
				Repair(layer.stateMachine);
			}
		}

		public override string ToString()
		{
			return $"{name} ({layers.Count} layers, {parameters.Count} parameters)";
		}

		#endregion

		#region PrivateMethods

		private static void Repair(AnimatorStateMachine machine)
		{
			machine.states = machine.states ?? new List<AnimatorState>();
			machine.subMachines = machine.subMachines ?? new List<AnimatorStateMachine>();
			machine.anyStateTransitions = machine.anyStateTransitions ?? new List<AnimatorTransition>();
			machine.entryTransitions = machine.entryTransitions ?? new List<AnimatorTransition>();
			machine.transitions = machine.transitions ?? new List<AnimatorTransition>();
			machine.notes = machine.notes ?? new List<GraphNote>();
			machine.groups = machine.groups ?? new List<GraphGroupData>();
			machine.parameterMappings = machine.parameterMappings ?? new List<ParameterMapping>();
			if (machine.id == 0)
			{
				machine.id = NewId();
			}

			foreach (AnimatorState state in machine.states)
			{
				state.transitions = state.transitions ?? new List<AnimatorTransition>();
				state.behaviours = state.behaviours ?? new List<StateMachineBehaviour>();
				if (state.id == 0)
				{
					state.id = NewId();
				}

				foreach (AnimatorTransition transition in state.transitions)
				{
					transition.conditions = transition.conditions ?? new List<AnimatorCondition>();
				}
			}

			foreach (AnimatorStateMachine sub in machine.subMachines)
			{
				Repair(sub);
			}
		}

		private static void ValidateTree(BlendTree tree, string stateName, List<string> problems)
		{
			if (tree.children.Count == 0)
			{
				problems.Add("Blend tree '" + tree.name + "' of state '" + stateName + "' has no children.");
			}

			if (tree.blendType != BlendTreeType.Direct && tree.parameter == 0)
			{
				problems.Add("Blend tree '" + tree.name + "' of state '" + stateName + "' has no parameter.");
			}

			foreach (BlendTreeChild child in tree.children)
			{
				if (child.motion == null) problems.Add("Blend tree '" + tree.name + "' of state '" + stateName + "' has an empty child.");
				else if (child.motion is BlendTree nested) ValidateTree(nested, stateName, problems);
				else if (child.motion is ClipMotion clipMotion && clipMotion.clip == null) problems.Add("Blend tree '" + tree.name + "' of state '" + stateName + "' has a child without a clip.");
			}
		}

		private void ValidateTransition(AnimatorTransition transition, string source, List<string> problems, bool fromPlayingState)
		{
			if (!transition.HasDestination)
			{
				problems.Add("A transition from '" + source + "' has no destination.");
			}
			else if (transition.destinationState != 0 && FindState(transition.destinationState) == null)
			{
				problems.Add("A transition from '" + source + "' leads to a missing state.");
			}

			// Entry and machine exit transitions without conditions are the fallthrough; a state's is a mistake.
			if (fromPlayingState && !transition.hasExitTime && transition.conditions.Count == 0 && !transition.mute)
			{
				problems.Add("The transition from '" + source + "' to '" + DescribeDestination(transition) + "' has neither conditions nor an exit time: it is taken at once.");
			}

			foreach (AnimatorCondition condition in transition.conditions)
			{
				if (condition is ParameterCondition pc)
				{
					AnimatorParameter parameter = FindParameter(pc.parameter);
					if (parameter == null)
					{
						problems.Add("A condition of the transition from '" + source + "' to '" + DescribeDestination(transition) + "' refers to a missing parameter.");
					}
					else if (pc.value != null && parameter.defaultValue != null && pc.value.GetType() != parameter.defaultValue.GetType() && pc.otherParameter == 0 && pc.mode != ConditionMode.If && pc.mode != ConditionMode.IfNot && pc.mode != ConditionMode.Trigger && pc.mode != ConditionMode.IsNull && pc.mode != ConditionMode.IsNotNull)
					{
						problems.Add("A condition on '" + parameter.name + "' (transition from '" + source + "') compares with a value of another type.");
					}
				}
			}
		}

		private static AnimatorStateMachine ParentOf(AnimatorStateMachine root, AnimatorStateMachine machine)
		{
			foreach (AnimatorStateMachine sub in root.subMachines)
			{
				if (sub == machine)
				{
					return root;
				}

				AnimatorStateMachine found = ParentOf(sub, machine);
				if (found != null)
				{
					return found;
				}
			}

			return null;
		}

		private static void RemoveTransitionsTo(AnimatorLayer layer, ulong stateId, ulong machineId)
		{
			var machines = new List<AnimatorStateMachine>();
			layer.stateMachine.CollectMachines(machines);
			bool Leads(AnimatorTransition t) => (stateId != 0 && t.destinationState == stateId) || (machineId != 0 && t.destinationMachine == machineId);
			foreach (AnimatorStateMachine machine in machines)
			{
				machine.anyStateTransitions.RemoveAll(Leads);
				machine.entryTransitions.RemoveAll(Leads);
				machine.transitions.RemoveAll(Leads);
				foreach (AnimatorState state in machine.states)
				{
					state.transitions.RemoveAll(Leads);
				}
			}
		}

		#endregion
	}
}
