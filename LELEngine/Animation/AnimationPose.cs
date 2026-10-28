using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>One animated property in a <see cref="PoseLayout" />: where its channels live in a <see cref="Pose" /> and what it writes to.</summary>
	public sealed class PoseSlot
	{
		public int Index;
		public string Path;
		public string TypeName;
		public string Property;

		/// <summary>Storage kind of the channels (an Euler binding stores a quaternion).</summary>
		public AnimationValueKind Kind;

		/// <summary>Offset of the first channel in <see cref="Pose.Values" /> (-1 for discrete slots).</summary>
		public int Offset = -1;

		public int Channels;

		/// <summary>Index into <see cref="Pose.Discrete" /> for string and reference slots (-1 otherwise).</summary>
		public int DiscreteIndex = -1;

		/// <summary>The resolved property (null until bound, or when the object or property is missing).</summary>
		public BoundProperty Target;

		/// <summary>The root transform's position / rotation: root motion is taken from these when the animator applies it.</summary>
		public bool IsRootPosition;

		public bool IsRootRotation;

		public bool IsRotation => Kind == AnimationValueKind.Quaternion || Kind == AnimationValueKind.Euler;
		public bool IsContinuous => AnimationBinding.IsContinuousKind(Kind);
		public bool IsDiscrete => DiscreteIndex >= 0;
		public string Key => (Path ?? "") + "|" + AnimationBinder.ShortTypeName(TypeName) + "|" + (Property ?? "");

		public override string ToString()
		{
			return Key + " (" + Kind + ")";
		}
	}

	/// <summary>
	///     The set of properties an animator writes: the union of the bindings of every clip it may play, each as a
	///     <see cref="PoseSlot" /> with channel offsets. Built once per controller and bound to a hierarchy
	///     (<see cref="Bind" />), which also records the objects' values at that moment as the defaults (write
	///     defaults) and the last written values.
	/// </summary>
	public sealed class PoseLayout
	{
		#region PublicFields

		public readonly List<PoseSlot> Slots = new List<PoseSlot>();
		public int FloatCount { get; private set; }
		public int DiscreteCount { get; private set; }

		/// <summary>The hierarchy the slots were bound to.</summary>
		public GameObject Root { get; private set; }

		/// <summary>Values of the properties when they were bound (write defaults).</summary>
		public float[] DefaultValues = Array.Empty<float>();

		public object[] DefaultDiscrete = Array.Empty<object>();

		/// <summary>The values written last (a slot no clip of the current blend touches keeps blending from them).</summary>
		public float[] LastWritten = Array.Empty<float>();

		public object[] LastWrittenDiscrete = Array.Empty<object>();

		/// <summary>Slots whose property could not be resolved at the last <see cref="Bind" /> (the keys), for diagnostics.</summary>
		public readonly List<string> Unresolved = new List<string>();

		/// <summary>Clips the layout was built from.</summary>
		public readonly List<AnimationClip> Clips = new List<AnimationClip>();

		#endregion

		#region PrivateFields

		private readonly Dictionary<string, int> slotsByKey = new Dictionary<string, int>(StringComparer.Ordinal);

		#endregion

		#region PublicMethods

		/// <summary>Adds the clip's bindings as slots (existing slots of the same property are shared).</summary>
		public void AddClip(AnimationClip clip)
		{
			if (clip == null)
			{
				return;
			}

			if (!Clips.Contains(clip))
			{
				Clips.Add(clip);
			}

			foreach (AnimationBinding binding in clip.bindings)
			{
				AddBinding(binding);
			}
		}

		/// <summary>The slot of a binding (created when missing); null when the property exists with another channel count.</summary>
		public PoseSlot AddBinding(AnimationBinding binding)
		{
			AnimationValueKind storage = binding.kind == AnimationValueKind.Euler ? AnimationValueKind.Quaternion : binding.kind;
			int channels = AnimationBinding.ChannelCountOf(storage);
			string key = binding.TargetKey;
			if (slotsByKey.TryGetValue(key, out int index))
			{
				PoseSlot existing = Slots[index];
				return existing.Channels == channels && existing.IsDiscrete == (channels == 0) ? existing : null;
			}

			var slot = new PoseSlot
			{
				Index = Slots.Count, Path = binding.path ?? "", TypeName = binding.type ?? "", Property = binding.property ?? "", Kind = storage, Channels = channels
			};
			if (channels > 0)
			{
				slot.Offset = FloatCount;
				FloatCount += channels;
			}
			else
			{
				slot.DiscreteIndex = DiscreteCount++;
			}

			if (slot.Path.Length == 0 && AnimationBinder.IsTransformProperty(slot.TypeName, slot.Property))
			{
				slot.IsRootPosition = slot.Property == AnimationBinder.LocalPosition;
				slot.IsRootRotation = slot.Property == AnimationBinder.LocalRotation || slot.Property == AnimationBinder.LocalEulerAngles;
			}

			Slots.Add(slot);
			slotsByKey[key] = slot.Index;
			return slot;
		}

		/// <summary>The slot index of a binding, or -1.</summary>
		public int SlotOf(AnimationBinding binding)
		{
			return slotsByKey.TryGetValue(binding.TargetKey, out int index) ? index : -1;
		}

		public PoseSlot FindSlot(string key)
		{
			return slotsByKey.TryGetValue(key, out int index) ? Slots[index] : null;
		}

		/// <summary>Resolves every slot's property under <paramref name="root" /> and records the current values as defaults.</summary>
		public void Bind(GameObject root)
		{
			Root = root;
			Unresolved.Clear();
			if (DefaultValues.Length != FloatCount)
			{
				DefaultValues = new float[FloatCount];
				LastWritten = new float[FloatCount];
			}

			if (DefaultDiscrete.Length != DiscreteCount)
			{
				DefaultDiscrete = new object[DiscreteCount];
				LastWrittenDiscrete = new object[DiscreteCount];
			}

			var binding = new AnimationBinding();
			foreach (PoseSlot slot in Slots)
			{
				binding.path = slot.Path;
				binding.type = slot.TypeName;
				binding.property = slot.Property;
				binding.kind = slot.Kind;
				slot.Target = root != null ? AnimationBinder.Bind(root, binding) : null;
				if (slot.Target == null)
				{
					Unresolved.Add(slot.Key);
					continue;
				}

				if (slot.IsDiscrete)
				{
					DefaultDiscrete[slot.DiscreteIndex] = slot.Target.ReadDiscrete();
					LastWrittenDiscrete[slot.DiscreteIndex] = DefaultDiscrete[slot.DiscreteIndex];
				}
				else
				{
					slot.Target.ReadChannels(DefaultValues, slot.Offset);
					Array.Copy(DefaultValues, slot.Offset, LastWritten, slot.Offset, slot.Channels);
				}
			}
		}

		/// <summary>Reads the objects' current values as the defaults again (the editor's preview after the user edited the object).</summary>
		public void CaptureDefaults()
		{
			foreach (PoseSlot slot in Slots)
			{
				if (slot.Target == null || !slot.Target.IsAlive)
				{
					continue;
				}

				if (slot.IsDiscrete)
				{
					DefaultDiscrete[slot.DiscreteIndex] = slot.Target.ReadDiscrete();
				}
				else
				{
					slot.Target.ReadChannels(DefaultValues, slot.Offset);
				}
			}
		}

		/// <summary>Writes the defaults back to the objects (the editor leaving a preview).</summary>
		public void RestoreDefaults()
		{
			foreach (PoseSlot slot in Slots)
			{
				if (slot.Target == null || !slot.Target.IsAlive)
				{
					continue;
				}

				if (slot.IsDiscrete)
				{
					slot.Target.WriteDiscrete(DefaultDiscrete[slot.DiscreteIndex]);
				}
				else
				{
					slot.Target.WriteChannels(DefaultValues, slot.Offset);
				}
			}
		}

		#endregion
	}

	/// <summary>
	///     Channel values of every slot of a <see cref="PoseLayout" /> with the weight each slot was written with
	///     (0: no clip of the blend animates it). Clips accumulate weighted samples into it, states and layers blend
	///     poses, and <see cref="Apply" /> writes the result to the objects.
	/// </summary>
	public sealed class Pose
	{
		#region PublicFields

		public float[] Values;
		public float[] Weights;
		public object[] Discrete;
		public float[] DiscreteWeights;
		public readonly PoseLayout Layout;

		#endregion

		#region Constructors

		public Pose(PoseLayout layout)
		{
			Layout = layout;
			Values = new float[layout.FloatCount];
			Weights = new float[layout.Slots.Count];
			Discrete = new object[layout.DiscreteCount];
			DiscreteWeights = new float[layout.DiscreteCount];
		}

		#endregion

		#region PublicMethods

		public void Clear()
		{
			Array.Clear(Values, 0, Values.Length);
			Array.Clear(Weights, 0, Weights.Length);
			Array.Clear(Discrete, 0, Discrete.Length);
			Array.Clear(DiscreteWeights, 0, DiscreteWeights.Length);
		}

		public void CopyFrom(Pose source)
		{
			Array.Copy(source.Values, Values, Values.Length);
			Array.Copy(source.Weights, Weights, Weights.Length);
			Array.Copy(source.Discrete, Discrete, Discrete.Length);
			Array.Copy(source.DiscreteWeights, DiscreteWeights, DiscreteWeights.Length);
		}

		/// <summary>Adds a weighted sample of a slot's channels (<paramref name="sample" /> holds the channels from index 0).</summary>
		public void AddSample(PoseSlot slot, float[] sample, float weight)
		{
			int offset = slot.Offset;
			int channels = slot.Channels;
			if (slot.IsRotation)
			{
				// Normalized lerp: the sample is flipped onto the hemisphere of what was accumulated so far.
				float dot = 0f;
				if (Weights[slot.Index] > 0f)
				{
					for (int c = 0; c < 4; c++)
					{
						dot += Values[offset + c] * sample[c];
					}
				}

				float sign = dot < 0f ? -1f : 1f;
				for (int c = 0; c < 4; c++)
				{
					Values[offset + c] += sign * weight * sample[c];
				}
			}
			else if (slot.IsContinuous)
			{
				for (int c = 0; c < channels; c++)
				{
					Values[offset + c] += weight * sample[c];
				}
			}
			else
			{
				// Discrete numbers (ints, bools, enums): the heaviest sample wins.
				if (weight > Weights[slot.Index] || Weights[slot.Index] <= 0f)
				{
					for (int c = 0; c < channels; c++)
					{
						Values[offset + c] = sample[c];
					}

					Weights[slot.Index] = weight;
					return;
				}

				Weights[slot.Index] += 0f;
				return;
			}

			Weights[slot.Index] += weight;
		}

		/// <summary>Adds a weighted string or reference sample (the heaviest wins).</summary>
		public void AddDiscrete(PoseSlot slot, object value, float weight)
		{
			int index = slot.DiscreteIndex;
			if (weight > DiscreteWeights[index])
			{
				Discrete[index] = value;
				DiscreteWeights[index] = weight;
			}
		}

		/// <summary>Divides every continuous slot by its accumulated weight when the weights do not sum to 1 (a partial blend).</summary>
		public void Normalize()
		{
			foreach (PoseSlot slot in Layout.Slots)
			{
				float weight = Weights[slot.Index];
				if (!slot.IsContinuous || weight <= 0f || Math.Abs(weight - 1f) < 1e-5f || slot.IsRotation)
				{
					continue;
				}

				float inverse = 1f / weight;
				for (int c = 0; c < slot.Channels; c++)
				{
					Values[slot.Offset + c] *= inverse;
				}
			}
		}

		/// <summary>Gives every untouched slot the layout's default value (a state with Write Defaults).</summary>
		public void FillDefaults(float weight)
		{
			foreach (PoseSlot slot in Layout.Slots)
			{
				if (slot.IsDiscrete)
				{
					if (DiscreteWeights[slot.DiscreteIndex] <= 0f && slot.Target != null)
					{
						Discrete[slot.DiscreteIndex] = Layout.DefaultDiscrete[slot.DiscreteIndex];
						DiscreteWeights[slot.DiscreteIndex] = weight;
					}
				}
				else if (Weights[slot.Index] <= 0f && slot.Target != null)
				{
					Array.Copy(Layout.DefaultValues, slot.Offset, Values, slot.Offset, slot.Channels);
					Weights[slot.Index] = weight;
				}
			}
		}

		/// <summary>Scales every slot's weight (a layer's weight, a transition's share).</summary>
		public void ScaleWeights(float factor)
		{
			for (int i = 0; i < Weights.Length; i++)
			{
				Weights[i] *= factor;
			}

			for (int i = 0; i < DiscreteWeights.Length; i++)
			{
				DiscreteWeights[i] *= factor;
			}
		}

		/// <summary>
		///     This = a blended towards b by <paramref name="t" /> (0: a, 1: b). A slot only one side touches blends
		///     between that side's value and the layout's last written value, so a property stops being animated smoothly.
		/// </summary>
		public void Lerp(Pose a, Pose b, float t)
		{
			float[] last = Layout.LastWritten;
			foreach (PoseSlot slot in Layout.Slots)
			{
				if (slot.IsDiscrete)
				{
					int d = slot.DiscreteIndex;
					float wa = a.DiscreteWeights[d];
					float wb = b.DiscreteWeights[d];
					if (wa <= 0f && wb <= 0f)
					{
						DiscreteWeights[d] = 0f;
						Discrete[d] = null;
						continue;
					}

					bool pickB = wb > 0f && (wa <= 0f || t >= 0.5f);
					Discrete[d] = pickB ? b.Discrete[d] : a.Discrete[d];
					DiscreteWeights[d] = wa * (1f - t) + wb * t;
					continue;
				}

				int s = slot.Index;
				float weightA = a.Weights[s];
				float weightB = b.Weights[s];
				int offset = slot.Offset;
				int channels = slot.Channels;
				if (weightA <= 0f && weightB <= 0f)
				{
					Weights[s] = 0f;
					continue;
				}

				if (!slot.IsContinuous)
				{
					bool pickB = weightB > 0f && (weightA <= 0f || t >= 0.5f);
					float[] source = pickB ? b.Values : a.Values;
					Array.Copy(source, offset, Values, offset, channels);
					Weights[s] = weightA * (1f - t) + weightB * t;
					continue;
				}

				// Each side's value, the last written one standing in for an untouched side.
				float[] valuesA = weightA > 0f ? a.Values : last;
				float[] valuesB = weightB > 0f ? b.Values : last;
				if (slot.IsRotation)
				{
					float dot = 0f;
					for (int c = 0; c < 4; c++)
					{
						dot += valuesA[offset + c] * valuesB[offset + c];
					}

					float sign = dot < 0f ? -1f : 1f;
					for (int c = 0; c < 4; c++)
					{
						Values[offset + c] = valuesA[offset + c] * (1f - t) + sign * valuesB[offset + c] * t;
					}
				}
				else
				{
					for (int c = 0; c < channels; c++)
					{
						Values[offset + c] = valuesA[offset + c] * (1f - t) + valuesB[offset + c] * t;
					}
				}

				Weights[s] = weightA * (1f - t) + weightB * t;
			}
		}

		/// <summary>
		///     Blends <paramref name="layer" /> over this pose by <paramref name="weight" /> on the slots its mask allows
		///     (null: all): an override layer.
		/// </summary>
		public void Override(Pose layer, float weight, bool[] mask)
		{
			if (weight <= 0f)
			{
				return;
			}

			float[] last = Layout.LastWritten;
			foreach (PoseSlot slot in Layout.Slots)
			{
				if (mask != null && !mask[slot.Index])
				{
					continue;
				}

				if (slot.IsDiscrete)
				{
					int d = slot.DiscreteIndex;
					if (layer.DiscreteWeights[d] <= 0f)
					{
						continue;
					}

					if (DiscreteWeights[d] <= 0f || weight >= 0.5f)
					{
						Discrete[d] = layer.Discrete[d];
					}

					DiscreteWeights[d] = Math.Max(DiscreteWeights[d], layer.DiscreteWeights[d] * weight);
					continue;
				}

				int s = slot.Index;
				float layerWeight = layer.Weights[s];
				if (layerWeight <= 0f)
				{
					continue;
				}

				int offset = slot.Offset;
				int channels = slot.Channels;
				float t = weight * Math.Min(layerWeight, 1f);
				if (!slot.IsContinuous)
				{
					if (Weights[s] <= 0f || t >= 0.5f)
					{
						Array.Copy(layer.Values, offset, Values, offset, channels);
					}

					Weights[s] = Math.Max(Weights[s], t);
					continue;
				}

				float[] under = Weights[s] > 0f ? Values : last;
				if (slot.IsRotation)
				{
					float dot = 0f;
					for (int c = 0; c < 4; c++)
					{
						dot += under[offset + c] * layer.Values[offset + c];
					}

					float sign = dot < 0f ? -1f : 1f;
					for (int c = 0; c < 4; c++)
					{
						Values[offset + c] = under[offset + c] * (1f - t) + sign * layer.Values[offset + c] * t;
					}
				}
				else
				{
					for (int c = 0; c < channels; c++)
					{
						Values[offset + c] = under[offset + c] * (1f - t) + layer.Values[offset + c] * t;
					}
				}

				Weights[s] = Math.Max(Weights[s], t);
			}
		}

		/// <summary>
		///     Adds <paramref name="layer" /> (a pose of differences to the clips' reference poses) scaled by
		///     <paramref name="weight" /> on the slots the mask allows: an additive layer. Rotations multiply.
		/// </summary>
		public void Additive(Pose layer, float weight, bool[] mask)
		{
			if (weight <= 0f)
			{
				return;
			}

			float[] last = Layout.LastWritten;
			foreach (PoseSlot slot in Layout.Slots)
			{
				if (slot.IsDiscrete || !slot.IsContinuous || (mask != null && !mask[slot.Index]))
				{
					continue;
				}

				int s = slot.Index;
				float layerWeight = layer.Weights[s];
				if (layerWeight <= 0f)
				{
					continue;
				}

				int offset = slot.Offset;
				float t = weight * Math.Min(layerWeight, 1f);
				if (Weights[s] <= 0f)
				{
					Array.Copy(last, offset, Values, offset, slot.Channels);
					Weights[s] = 1f;
				}

				if (slot.IsRotation)
				{
					// The delta rotation scaled towards identity, then multiplied onto the base.
					var delta = new Quaternion(layer.Values[offset], layer.Values[offset + 1], layer.Values[offset + 2], layer.Values[offset + 3]);
					if (delta.LengthSquared < 1e-12f)
					{
						continue;
					}

					delta.Normalize();
					if (delta.W < 0f)
					{
						delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
					}

					Quaternion scaled = Quaternion.Slerp(Quaternion.Identity, delta, t);
					var baseRotation = new Quaternion(Values[offset], Values[offset + 1], Values[offset + 2], Values[offset + 3]);
					if (baseRotation.LengthSquared < 1e-12f)
					{
						baseRotation = Quaternion.Identity;
					}

					baseRotation.Normalize();
					Quaternion result = baseRotation * scaled;
					Values[offset] = result.X;
					Values[offset + 1] = result.Y;
					Values[offset + 2] = result.Z;
					Values[offset + 3] = result.W;
				}
				else
				{
					for (int c = 0; c < slot.Channels; c++)
					{
						Values[offset + c] += layer.Values[offset + c] * t;
					}
				}
			}
		}

		/// <summary>
		///     Writes the pose to the objects: every touched slot (weight above 0; a partial weight blends with the last
		///     written value) and records what was written. Root transform slots are skipped when
		///     <paramref name="skipRootMotion" /> (the animator moves the object instead).
		/// </summary>
		public void Apply(PoseLayout layout, bool skipRootMotion)
		{
			float[] last = layout.LastWritten;
			object[] lastDiscrete = layout.LastWrittenDiscrete;
			foreach (PoseSlot slot in layout.Slots)
			{
				BoundProperty target = slot.Target;
				if (target == null)
				{
					continue;
				}

				if (slot.IsDiscrete)
				{
					int d = slot.DiscreteIndex;
					if (DiscreteWeights[d] <= 0f)
					{
						continue;
					}

					if (!target.IsAlive)
					{
						continue;
					}

					object value = DiscreteWeights[d] < 0.5f ? lastDiscrete[d] : Discrete[d];
					target.WriteDiscrete(value);
					lastDiscrete[d] = value;
					continue;
				}

				int s = slot.Index;
				float weight = Weights[s];
				if (weight <= 0f)
				{
					continue;
				}

				if (skipRootMotion && (slot.IsRootPosition || slot.IsRootRotation))
				{
					continue;
				}

				if (!target.IsAlive)
				{
					continue;
				}

				int offset = slot.Offset;
				int channels = slot.Channels;
				if (weight < 1f - 1e-5f)
				{
					if (slot.IsContinuous)
					{
						if (slot.IsRotation)
						{
							float dot = 0f;
							for (int c = 0; c < 4; c++)
							{
								dot += last[offset + c] * Values[offset + c];
							}

							float sign = dot < 0f ? -1f : 1f;
							for (int c = 0; c < 4; c++)
							{
								Values[offset + c] = last[offset + c] * (1f - weight) + sign * Values[offset + c] * weight;
							}
						}
						else
						{
							for (int c = 0; c < channels; c++)
							{
								Values[offset + c] = last[offset + c] * (1f - weight) + Values[offset + c] * weight;
							}
						}
					}
					else if (weight < 0.5f)
					{
						Array.Copy(last, offset, Values, offset, channels);
					}
				}

				target.WriteChannels(Values, offset);
				Array.Copy(Values, offset, last, offset, channels);
			}
		}

		/// <summary>The slot's channels as a quaternion (rotation slots).</summary>
		public Quaternion GetQuaternion(PoseSlot slot)
		{
			int o = slot.Offset;
			var q = new Quaternion(Values[o], Values[o + 1], Values[o + 2], Values[o + 3]);
			return q.LengthSquared > 1e-12f ? q.Normalized() : Quaternion.Identity;
		}

		public Vector3 GetVector3(PoseSlot slot)
		{
			int o = slot.Offset;
			return new Vector3(Values[o], Values[o + 1], Values[o + 2]);
		}

		#endregion
	}

	/// <summary>
	///     Samples one clip into poses of a layout: the clip's bindings mapped to slots once, with a per-channel
	///     segment hint so sequential playback never searches. One sampler per animator and clip.
	/// </summary>
	public sealed class ClipSampler
	{
		#region PublicFields

		public readonly AnimationClip Clip;
		public readonly PoseLayout Layout;

		/// <summary>The clip version the mapping was built for; a changed clip needs a new sampler.</summary>
		public readonly int Version;

		/// <summary>The root position / rotation slots (null when the clip does not animate them).</summary>
		public PoseSlot RootPositionSlot;

		public PoseSlot RootRotationSlot;

		#endregion

		#region PrivateFields

		private readonly int[] slotOfBinding;
		private readonly int[][] hints;
		private readonly AnimationBinding rootPosition;
		private readonly AnimationBinding rootRotation;
		private readonly float[] sample = new float[4];
		private readonly float[] reference = new float[4];
		private float[] referencePose;
		private readonly int[] rootHints = new int[8];

		#endregion

		#region Constructors

		public ClipSampler(AnimationClip clip, PoseLayout layout)
		{
			Clip = clip;
			Layout = layout;
			Version = clip.Version;
			slotOfBinding = new int[clip.bindings.Count];
			hints = new int[clip.bindings.Count][];
			for (int i = 0; i < clip.bindings.Count; i++)
			{
				AnimationBinding binding = clip.bindings[i];
				slotOfBinding[i] = layout.SlotOf(binding);
				hints[i] = new int[Math.Max(binding.ChannelCount, 1)];
				for (int c = 0; c < hints[i].Length; c++)
				{
					hints[i][c] = -1;
				}

				if (slotOfBinding[i] >= 0)
				{
					PoseSlot slot = layout.Slots[slotOfBinding[i]];
					if (slot.IsRootPosition)
					{
						RootPositionSlot = slot;
						rootPosition = binding;
					}
					else if (slot.IsRootRotation)
					{
						RootRotationSlot = slot;
						rootRotation = binding;
					}
				}
			}

			for (int i = 0; i < rootHints.Length; i++)
			{
				rootHints[i] = -1;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Adds the clip at <paramref name="time" /> (seconds, within the clip) to the pose with a weight.</summary>
		public void Sample(float time, float weight, Pose pose)
		{
			if (weight <= 0f)
			{
				return;
			}

			List<AnimationBinding> bindings = Clip.bindings;
			for (int i = 0; i < bindings.Count && i < slotOfBinding.Length; i++)
			{
				int slotIndex = slotOfBinding[i];
				if (slotIndex < 0)
				{
					continue;
				}

				AnimationBinding binding = bindings[i];
				PoseSlot slot = Layout.Slots[slotIndex];
				if (slot.IsDiscrete)
				{
					object value = binding.kind == AnimationValueKind.String ? binding.EvaluateString(time) : binding.EvaluateReference(time);
					pose.AddDiscrete(slot, value, weight);
					continue;
				}

				if (!Evaluate(binding, i, time, sample))
				{
					continue;
				}

				pose.AddSample(slot, sample, weight);
			}
		}

		/// <summary>
		///     Adds the difference between the clip at <paramref name="time" /> and at its reference time (the first
		///     frame) to the pose: additive layers. Rotations give the delta rotation.
		/// </summary>
		public void SampleAdditive(float time, float referenceTime, float weight, Pose pose)
		{
			if (weight <= 0f)
			{
				return;
			}

			List<AnimationBinding> bindings = Clip.bindings;
			for (int i = 0; i < bindings.Count && i < slotOfBinding.Length; i++)
			{
				int slotIndex = slotOfBinding[i];
				if (slotIndex < 0)
				{
					continue;
				}

				AnimationBinding binding = bindings[i];
				PoseSlot slot = Layout.Slots[slotIndex];
				if (slot.IsDiscrete || !slot.IsContinuous)
				{
					continue;
				}

				if (!Evaluate(binding, i, time, sample) || !EvaluateNoHint(binding, referenceTime, reference))
				{
					continue;
				}

				if (slot.IsRotation)
				{
					var q = new Quaternion(sample[0], sample[1], sample[2], sample[3]);
					var r = new Quaternion(reference[0], reference[1], reference[2], reference[3]);
					if (q.LengthSquared < 1e-12f || r.LengthSquared < 1e-12f)
					{
						continue;
					}

					q.Normalize();
					r.Normalize();
					Quaternion delta = r.Inverted() * q;
					sample[0] = delta.X;
					sample[1] = delta.Y;
					sample[2] = delta.Z;
					sample[3] = delta.W;
					// Accumulated with sign alignment, so the sum stays a lerp towards the delta from identity.
					if (sample[3] < 0f)
					{
						for (int c = 0; c < 4; c++) sample[c] = -sample[c];
					}
				}
				else
				{
					for (int c = 0; c < slot.Channels; c++)
					{
						sample[c] -= reference[c];
					}
				}

				pose.AddSample(slot, sample, weight);
			}
		}

		/// <summary>True when the clip animates the root transform's position or rotation.</summary>
		public bool HasRootMotion => rootPosition != null || rootRotation != null;

		/// <summary>The root position at a time (zero without a root position binding).</summary>
		public Vector3 RootPosition(float time)
		{
			if (rootPosition == null)
			{
				return Vector3.Zero;
			}

			return new Vector3(rootPosition.channels[0].Evaluate(time, ref rootHints[0]), rootPosition.channels[1].Evaluate(time, ref rootHints[1]), rootPosition.channels[2].Evaluate(time, ref rootHints[2]));
		}

		/// <summary>The root rotation at a time (identity without a root rotation binding).</summary>
		public Quaternion RootRotation(float time)
		{
			if (rootRotation == null)
			{
				return Quaternion.Identity;
			}

			if (rootRotation.kind == AnimationValueKind.Euler)
			{
				return QuaternionHelper.Euler(rootRotation.channels[0].Evaluate(time, ref rootHints[3]), rootRotation.channels[1].Evaluate(time, ref rootHints[4]), rootRotation.channels[2].Evaluate(time, ref rootHints[5]));
			}

			var q = new Quaternion(rootRotation.channels[0].Evaluate(time, ref rootHints[3]), rootRotation.channels[1].Evaluate(time, ref rootHints[4]), rootRotation.channels[2].Evaluate(time, ref rootHints[5]), rootRotation.channels[3].Evaluate(time, ref rootHints[6]));
			return q.LengthSquared > 1e-12f ? q.Normalized() : Quaternion.Identity;
		}

		/// <summary>
		///     Collects the events between <paramref name="previous" /> (exclusive) and <paramref name="time" />
		///     (inclusive) in clip seconds, handling a loop boundary crossed in between and playback backwards.
		///     <paramref name="includeStart" />: an event exactly at <paramref name="previous" /> fires too (a state's first frame).
		/// </summary>
		public void CollectEvents(float previous, float time, bool loop, bool includeStart, List<AnimationEvent> output)
		{
			List<AnimationEvent> events = Clip.events;
			if (events.Count == 0)
			{
				return;
			}

			float length = Clip.length;
			if (!loop || length <= 1e-6f)
			{
				CollectRange(events, previous, time, includeStart, output);
				return;
			}

			// Loop: both times reduced into [0, length); a wrap in between splits the range.
			float a = Wrap(previous, length);
			float b = Wrap(time, length);
			if (time >= previous)
			{
				if (b >= a && time - previous < length)
				{
					CollectRange(events, a, b, includeStart, output);
				}
				else
				{
					CollectRange(events, a, length, includeStart, output);
					CollectRange(events, -1e-6f, b, false, output);
				}
			}
			else
			{
				if (b <= a && previous - time < length)
				{
					CollectRange(events, b, a, includeStart, output);
				}
				else
				{
					CollectRange(events, -1e-6f, a, includeStart, output);
					CollectRange(events, b, length, false, output);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private bool Evaluate(AnimationBinding binding, int index, float time, float[] result)
		{
			List<FloatCurve> channels = binding.channels;
			int count = binding.ChannelCount;
			if (channels == null || channels.Count < count)
			{
				return false;
			}

			int[] hint = hints[index];
			bool any = false;
			for (int c = 0; c < count; c++)
			{
				FloatCurve curve = channels[c];
				if (curve.Length == 0)
				{
					result[c] = 0f;
					continue;
				}

				any = true;
				result[c] = curve.Evaluate(time, ref hint[c]);
			}

			if (!any)
			{
				return false;
			}

			if (binding.kind == AnimationValueKind.Euler)
			{
				Quaternion q = QuaternionHelper.Euler(result[0], result[1], result[2]);
				result[0] = q.X;
				result[1] = q.Y;
				result[2] = q.Z;
				result[3] = q.W;
			}

			return true;
		}

		private static bool EvaluateNoHint(AnimationBinding binding, float time, float[] result)
		{
			List<FloatCurve> channels = binding.channels;
			int count = binding.ChannelCount;
			if (channels == null || channels.Count < count)
			{
				return false;
			}

			bool any = false;
			for (int c = 0; c < count; c++)
			{
				FloatCurve curve = channels[c];
				if (curve.Length == 0)
				{
					result[c] = 0f;
					continue;
				}

				any = true;
				result[c] = curve.Evaluate(time);
			}

			if (!any)
			{
				return false;
			}

			if (binding.kind == AnimationValueKind.Euler)
			{
				Quaternion q = QuaternionHelper.Euler(result[0], result[1], result[2]);
				result[0] = q.X;
				result[1] = q.Y;
				result[2] = q.Z;
				result[3] = q.W;
			}

			return true;
		}

		private static float Wrap(float time, float length)
		{
			float t = time % length;
			if (t < 0f)
			{
				t += length;
			}

			return t;
		}

		private static void CollectRange(List<AnimationEvent> events, float from, float to, bool includeStart, List<AnimationEvent> output)
		{
			if (to < from)
			{
				(from, to) = (to, from);
				includeStart = false;
			}

			for (int i = 0; i < events.Count; i++)
			{
				float t = events[i].time;
				if ((t > from || (includeStart && t == from)) && t <= to)
				{
					output.Add(events[i]);
				}
			}
		}

		#endregion
	}
}
