using System;
using System.Collections.Generic;

namespace LELEngine.Animation
{
	/// <summary>
	///     A float over time stored as flat arrays (one per key field): the form animation clips keep their channels in.
	///     The arrays serialize as blocks (a binary document copies them, no per-key objects), and evaluation reads them
	///     in place. Same curve model as <see cref="AnimationCurve" /> (cubic Hermite segments, tangent modes, wrap
	///     modes); <see cref="ToAnimationCurve" /> / <see cref="FromAnimationCurve" /> convert for the curve editor.
	/// </summary>
	[Serializable]
	public sealed class FloatCurve
	{
		#region PublicFields

		public float[] Times = Array.Empty<float>();
		public float[] Values = Array.Empty<float>();
		public float[] InTangents = Array.Empty<float>();
		public float[] OutTangents = Array.Empty<float>();

		/// <summary>One <see cref="TangentMode" /> per key.</summary>
		public byte[] Modes = Array.Empty<byte>();

		public WrapMode PreWrap = WrapMode.ClampForever;
		public WrapMode PostWrap = WrapMode.ClampForever;

		/// <summary>Number of keys.</summary>
		public int Length => Times.Length;

		public float StartTime => Times.Length > 0 ? Times[0] : 0f;
		public float EndTime => Times.Length > 0 ? Times[Times.Length - 1] : 0f;

		#endregion

		#region Constructors

		public FloatCurve()
		{ }

		public FloatCurve(params Keyframe[] keys)
		{
			SetKeys(keys);
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- evaluation

		/// <summary>The value at <paramref name="time" /> (0 without keys).</summary>
		public float Evaluate(float time)
		{
			int hint = -1;
			return Evaluate(time, ref hint);
		}

		/// <summary>
		///     The value at <paramref name="time" />; <paramref name="hint" /> is the segment found last time (sequential
		///     playback finds the next segment without a search). Pass -1 the first time.
		/// </summary>
		public float Evaluate(float time, ref int hint)
		{
			float[] times = Times;
			int count = times.Length;
			if (count == 0)
			{
				return 0f;
			}

			if (count == 1)
			{
				return Values[0];
			}

			float first = times[0];
			float last = times[count - 1];
			float span = last - first;
			if (time < first)
			{
				if (!Wrap(PreWrap, ref time, first, span))
				{
					return Values[0];
				}
			}
			else if (time > last)
			{
				if (!Wrap(PostWrap, ref time, first, span))
				{
					return Values[count - 1];
				}
			}

			// The segment [i - 1, i] holding the time: try the hinted one and its neighbour, else binary search.
			int low;
			if (hint > 0 && hint < count && times[hint - 1] <= time && time <= times[hint])
			{
				low = hint;
			}
			else if (hint > 0 && hint + 1 < count && times[hint] < time && time <= times[hint + 1])
			{
				low = hint + 1;
			}
			else
			{
				low = 1;
				int high = count - 1;
				while (low < high)
				{
					int mid = (low + high) >> 1;
					if (times[mid] < time)
					{
						low = mid + 1;
					}
					else
					{
						high = mid;
					}
				}
			}

			hint = low;
			return EvaluateSegment(low - 1, low, time);
		}

		/// <summary>The value between keys <paramref name="a" /> and <paramref name="b" /> (indices) at <paramref name="time" />.</summary>
		public float EvaluateSegment(int a, int b, float time)
		{
			float ta = Times[a];
			float tb = Times[b];
			float dt = tb - ta;
			float va = Values[a];
			float vb = Values[b];
			if (dt <= 1e-12f)
			{
				return vb;
			}

			float outTangent = OutTangents[a];
			float inTangent = InTangents[b];
			if (Modes[a] == (byte)TangentMode.Constant || float.IsInfinity(outTangent) || float.IsInfinity(inTangent))
			{
				return time >= tb ? vb : va;
			}

			float t = (time - ta) / dt;
			float t2 = t * t;
			float t3 = t2 * t;
			float h00 = 2f * t3 - 3f * t2 + 1f;
			float h10 = t3 - 2f * t2 + t;
			float h01 = -2f * t3 + 3f * t2;
			float h11 = t3 - t2;
			return h00 * va + h10 * outTangent * dt + h01 * vb + h11 * inTangent * dt;
		}

		// ---------------------------------------------------------------- keys

		public Keyframe GetKey(int index)
		{
			return new Keyframe(Times[index], Values[index], InTangents[index], OutTangents[index]) { tangentMode = (TangentMode)Modes[index] };
		}

		/// <summary>Replaces the key's value and tangents (its time stays; use <see cref="MoveKey" /> to change the time).</summary>
		public void SetKey(int index, Keyframe key)
		{
			Values[index] = key.value;
			InTangents[index] = key.inTangent;
			OutTangents[index] = key.outTangent;
			Modes[index] = (byte)key.tangentMode;
		}

		/// <summary>Adds a key (sorted by time); a key at the same time is replaced. Returns the key's index.</summary>
		public int AddKey(Keyframe key)
		{
			int index = FindIndex(key.time, out bool exact);
			if (exact)
			{
				Times[index] = key.time;
				SetKey(index, key);
				return index;
			}

			Times = Insert(Times, index, key.time);
			Values = Insert(Values, index, key.value);
			InTangents = Insert(InTangents, index, key.inTangent);
			OutTangents = Insert(OutTangents, index, key.outTangent);
			Modes = Insert(Modes, index, (byte)key.tangentMode);
			return index;
		}

		/// <summary>Adds a key with smooth (clamped auto) tangents; the neighbours' derived tangents are recomputed.</summary>
		public int AddKey(float time, float value)
		{
			int index = AddKey(new Keyframe(time, value) { tangentMode = TangentMode.ClampedAuto });
			RecalculateTangents();
			return index;
		}

		/// <summary>Moves a key to another time (and value); returns its new index.</summary>
		public int MoveKey(int index, Keyframe key)
		{
			RemoveKey(index);
			return AddKey(key);
		}

		public void RemoveKey(int index)
		{
			Times = RemoveAt(Times, index);
			Values = RemoveAt(Values, index);
			InTangents = RemoveAt(InTangents, index);
			OutTangents = RemoveAt(OutTangents, index);
			Modes = RemoveAt(Modes, index);
		}

		public void ClearKeys()
		{
			Times = Array.Empty<float>();
			Values = Array.Empty<float>();
			InTangents = Array.Empty<float>();
			OutTangents = Array.Empty<float>();
			Modes = Array.Empty<byte>();
		}

		/// <summary>Replaces every key (sorted by time; equal times keep the last one).</summary>
		public void SetKeys(IList<Keyframe> keys)
		{
			ClearKeys();
			if (keys == null)
			{
				return;
			}

			var sorted = new List<Keyframe>(keys);
			sorted.Sort((a, b) => a.time.CompareTo(b.time));
			int n = sorted.Count;
			Times = new float[n];
			Values = new float[n];
			InTangents = new float[n];
			OutTangents = new float[n];
			Modes = new byte[n];
			int written = 0;
			for (int i = 0; i < n; i++)
			{
				Keyframe key = sorted[i];
				if (written > 0 && Times[written - 1] == key.time)
				{
					written--;
				}

				Times[written] = key.time;
				Values[written] = key.value;
				InTangents[written] = key.inTangent;
				OutTangents[written] = key.outTangent;
				Modes[written] = (byte)key.tangentMode;
				written++;
			}

			if (written != n)
			{
				Array.Resize(ref Times, written);
				Array.Resize(ref Values, written);
				Array.Resize(ref InTangents, written);
				Array.Resize(ref OutTangents, written);
				Array.Resize(ref Modes, written);
			}
		}

		/// <summary>The index of the key at <paramref name="time" /> (exact) or of the first key after it.</summary>
		public int FindIndex(float time, out bool exact)
		{
			int low = 0;
			int high = Times.Length;
			while (low < high)
			{
				int mid = (low + high) >> 1;
				if (Times[mid] < time)
				{
					low = mid + 1;
				}
				else
				{
					high = mid;
				}
			}

			exact = low < Times.Length && Times[low] == time;
			return low;
		}

		/// <summary>Index of the key at exactly this time (within <paramref name="tolerance" />), or -1.</summary>
		public int IndexOf(float time, float tolerance = 1e-5f)
		{
			int index = FindIndex(time, out bool exact);
			if (exact)
			{
				return index;
			}

			if (index < Times.Length && Math.Abs(Times[index] - time) <= tolerance)
			{
				return index;
			}

			if (index > 0 && Math.Abs(Times[index - 1] - time) <= tolerance)
			{
				return index - 1;
			}

			return -1;
		}

		/// <summary>Recomputes the tangents of the keys whose mode derives them (clamped auto, linear, constant).</summary>
		public void RecalculateTangents()
		{
			int n = Times.Length;
			for (int i = 0; i < n; i++)
			{
				switch ((TangentMode)Modes[i])
				{
					case TangentMode.ClampedAuto:
						InTangents[i] = OutTangents[i] = ClampedAutoSlope(i);
						break;
					case TangentMode.Linear:
						InTangents[i] = i > 0 ? SegmentSlope(i - 1, i) : n > 1 ? SegmentSlope(0, 1) : 0f;
						OutTangents[i] = i < n - 1 ? SegmentSlope(i, i + 1) : InTangents[i];
						break;
					case TangentMode.Constant:
						InTangents[i] = OutTangents[i] = 0f;
						break;
				}
			}
		}

		/// <summary>Shifts every key in time (the clip editor's move of a whole channel) and scales their times.</summary>
		public void TransformTimes(float offset, float scale)
		{
			for (int i = 0; i < Times.Length; i++)
			{
				Times[i] = Times[i] * scale + offset;
			}

			if (scale != 1f && scale > 0f)
			{
				for (int i = 0; i < Times.Length; i++)
				{
					InTangents[i] /= scale;
					OutTangents[i] /= scale;
				}
			}
		}

		// ---------------------------------------------------------------- conversions

		public AnimationCurve ToAnimationCurve()
		{
			var keys = new Keyframe[Times.Length];
			for (int i = 0; i < keys.Length; i++)
			{
				keys[i] = GetKey(i);
			}

			return new AnimationCurve(keys) { preWrapMode = PreWrap, postWrapMode = PostWrap };
		}

		public static FloatCurve FromAnimationCurve(AnimationCurve curve)
		{
			var result = new FloatCurve();
			if (curve != null)
			{
				result.SetKeys(curve.keys);
				result.PreWrap = curve.preWrapMode;
				result.PostWrap = curve.postWrapMode;
			}

			return result;
		}

		public void CopyFrom(FloatCurve source)
		{
			Times = (float[])source.Times.Clone();
			Values = (float[])source.Values.Clone();
			InTangents = (float[])source.InTangents.Clone();
			OutTangents = (float[])source.OutTangents.Clone();
			Modes = (byte[])source.Modes.Clone();
			PreWrap = source.PreWrap;
			PostWrap = source.PostWrap;
		}

		public FloatCurve Clone()
		{
			var copy = new FloatCurve();
			copy.CopyFrom(this);
			return copy;
		}

		/// <summary>A curve holding one value for all times.</summary>
		public static FloatCurve Constant(float value)
		{
			return new FloatCurve(new Keyframe(0f, value) { tangentMode = TangentMode.Constant });
		}

		/// <summary>Repairs arrays of unequal length (a hand-edited file): every array takes the key count of Times.</summary>
		public void Validate()
		{
			int n = Times?.Length ?? 0;
			Times = Times ?? Array.Empty<float>();
			Values = Fit(Values, n, 0f);
			InTangents = Fit(InTangents, n, 0f);
			OutTangents = Fit(OutTangents, n, 0f);
			Modes = Fit(Modes, n, (byte)TangentMode.Free);
		}

		public override string ToString()
		{
			return $"FloatCurve({Length} keys, {StartTime}..{EndTime})";
		}

		#endregion

		#region PrivateMethods

		private static bool Wrap(WrapMode mode, ref float time, float start, float span)
		{
			if (span <= 1e-12f)
			{
				return false;
			}

			switch (mode)
			{
				case WrapMode.Loop:
				{
					float local = (time - start) % span;
					if (local < 0f)
					{
						local += span;
					}

					time = start + local;
					return true;
				}
				case WrapMode.PingPong:
				{
					float local = (time - start) % (2f * span);
					if (local < 0f)
					{
						local += 2f * span;
					}

					time = start + (local > span ? 2f * span - local : local);
					return true;
				}
				default:
					return false;
			}
		}

		private float SegmentSlope(int a, int b)
		{
			float dt = Times[b] - Times[a];
			return dt > 1e-12f ? (Values[b] - Values[a]) / dt : 0f;
		}

		private float ClampedAutoSlope(int index)
		{
			int n = Times.Length;
			if (n < 2 || index == 0 || index == n - 1)
			{
				return 0f;
			}

			float left = SegmentSlope(index - 1, index);
			float right = SegmentSlope(index, index + 1);
			if (left * right <= 0f)
			{
				return 0f;
			}

			// Fritsch-Carlson: harmonic mean of the neighbouring slopes, weighted by the segment lengths.
			float dtLeft = Times[index] - Times[index - 1];
			float dtRight = Times[index + 1] - Times[index];
			float weightLeft = 2f * dtLeft + dtRight;
			float weightRight = dtLeft + 2f * dtRight;
			return (weightLeft + weightRight) / (weightLeft / left + weightRight / right);
		}

		private static T[] Insert<T>(T[] array, int index, T value)
		{
			var result = new T[array.Length + 1];
			Array.Copy(array, 0, result, 0, index);
			result[index] = value;
			Array.Copy(array, index, result, index + 1, array.Length - index);
			return result;
		}

		private static T[] RemoveAt<T>(T[] array, int index)
		{
			var result = new T[array.Length - 1];
			Array.Copy(array, 0, result, 0, index);
			Array.Copy(array, index + 1, result, index, array.Length - index - 1);
			return result;
		}

		private static T[] Fit<T>(T[] array, int length, T fill)
		{
			if (array != null && array.Length == length)
			{
				return array;
			}

			var result = new T[length];
			int copy = Math.Min(length, array?.Length ?? 0);
			if (copy > 0)
			{
				Array.Copy(array, result, copy);
			}

			for (int i = copy; i < length; i++)
			{
				result[i] = fill;
			}

			return result;
		}

		#endregion
	}
}
