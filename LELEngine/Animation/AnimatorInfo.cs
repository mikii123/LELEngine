using System;

namespace LELEngine.Animation
{
	/// <summary>When an animator advances.</summary>
	public enum AnimatorUpdateMode
	{
		/// <summary>Every frame after the Update calls, before LateUpdate.</summary>
		Normal,

		/// <summary>In every fixed step before the physics step (objects moved by root motion with rigidbodies).</summary>
		AnimatePhysics
	}

	/// <summary>Information about a state playing on a layer (Unity's AnimatorStateInfo).</summary>
	public struct AnimatorStateInfo
	{
		/// <summary>Hash of "Layer.Sub/State".</summary>
		public int fullPathHash;

		/// <summary>Hash of the state's name.</summary>
		public int shortNameHash;

		public int tagHash;

		/// <summary>Time in cycles of the motion (1 = one full play; past 1 for looping states).</summary>
		public float normalizedTime;

		/// <summary>Length of the motion in seconds (a blend tree: weighted by the current blend).</summary>
		public float length;

		public float speed;
		public float speedMultiplier;
		public bool loop;

		/// <summary>"Sub/State" (for display; compare hashes in code).</summary>
		public string name;

		public ulong stateId;

		public bool IsName(string stateName)
		{
			int hash = Animator.StringToHash(stateName);
			return hash == shortNameHash || hash == fullPathHash || (name != null && (name == stateName || name.EndsWith("/" + stateName, StringComparison.Ordinal)));
		}

		public bool IsTag(string tag)
		{
			return Animator.StringToHash(tag) == tagHash;
		}

		public override string ToString()
		{
			return name + " @ " + normalizedTime.ToString("0.00");
		}
	}

	/// <summary>Information about a transition in progress (Unity's AnimatorTransitionInfo).</summary>
	public struct AnimatorTransitionInfo
	{
		public int nameHash;

		/// <summary>Progress 0..1.</summary>
		public float normalizedTime;

		/// <summary>Duration in seconds.</summary>
		public float duration;

		public bool anyState;
		public string name;
		public ulong transitionId;
		public ulong sourceStateId;
		public ulong destinationStateId;

		public bool IsName(string transitionName)
		{
			return Animator.StringToHash(transitionName) == nameHash;
		}
	}

	/// <summary>A clip and its weight in a layer's current blend.</summary>
	public struct AnimatorClipInfo
	{
		public AnimationClip clip;
		public float weight;

		/// <summary>The clip's time in seconds at the last sample.</summary>
		public float time;
	}
}
