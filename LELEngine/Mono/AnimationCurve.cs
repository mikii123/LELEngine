using System;
using System.Collections.Generic;

namespace LELEngine
{
	/// <summary>How a curve key's tangents are kept (the curve editor's key menu).</summary>
	public enum TangentMode
	{
		/// <summary>The tangents as set (smooth: one slope on both sides when edited in the curve editor).</summary>
		Free,

		/// <summary>Smooth tangents computed from the neighbours that never overshoot them (flat at the ends and at extremes).</summary>
		ClampedAuto,

		/// <summary>Straight lines to the neighbouring keys.</summary>
		Linear,

		/// <summary>The value holds until the next key (a step).</summary>
		Constant,

		/// <summary>In and out tangents set independently.</summary>
		Broken
	}

	/// <summary>What a curve does outside its keys (Unity's WrapMode for curves).</summary>
	public enum WrapMode
	{
		/// <summary>Holds the end key's value (same as <see cref="ClampForever" />).</summary>
		Default = 0,

		Once = 1,
		Loop = 2,
		PingPong = 4,
		ClampForever = 8
	}

	/// <summary>A key of an <see cref="AnimationCurve" />: a value at a time with the slopes into and out of it.</summary>
	[Serializable]
	public struct Keyframe
	{
		public float time;
		public float value;

		/// <summary>Slope (value per time unit) arriving at the key.</summary>
		public float inTangent;

		/// <summary>Slope leaving the key.</summary>
		public float outTangent;

		public TangentMode tangentMode;

		public Keyframe(float time, float value)
			: this(time, value, 0f, 0f)
		{ }

		public Keyframe(float time, float value, float inTangent, float outTangent)
		{
			this.time = time;
			this.value = value;
			this.inTangent = inTangent;
			this.outTangent = outTangent;
			tangentMode = TangentMode.Free;
		}

		public override string ToString()
		{
			return $"({time}, {value}; in {inTangent}, out {outTangent}, {tangentMode})";
		}
	}

	/// <summary>
	///     A float over time (Unity's AnimationCurve): cubic Hermite segments between keys, each key with its in and out
	///     slopes; outside the keys the wrap modes clamp, loop or ping-pong. Serialized inline; the inspector draws it
	///     as a curve preview that opens the curve editor (keys, tangents, presets).
	///     Infinite tangents (Unity's way of writing a step) evaluate as <see cref="TangentMode.Constant" />.
	/// </summary>
	[Serializable]
	public sealed class AnimationCurve
	{
		#region PrivateFields

		[SerializeField] private List<Keyframe> m_Keys = new List<Keyframe>();
		[SerializeField] private WrapMode m_PreWrapMode = WrapMode.ClampForever;
		[SerializeField] private WrapMode m_PostWrapMode = WrapMode.ClampForever;

		#endregion

		#region Constructors

		public AnimationCurve()
		{ }

		public AnimationCurve(params Keyframe[] keys)
		{
			this.keys = keys;
		}

		/// <summary>An independent copy of another curve.</summary>
		public AnimationCurve(AnimationCurve source)
		{
			CopyFrom(source);
		}

		#endregion

		#region PublicFields

		/// <summary>A copy of the keys, by time; setting replaces them (sorted).</summary>
		public Keyframe[] keys
		{
			get => m_Keys.ToArray();
			set
			{
				m_Keys.Clear();
				if (value != null)
				{
					m_Keys.AddRange(value);
				}

				Sort();
			}
		}

		public int length => m_Keys.Count;

		public Keyframe this[int index] => m_Keys[index];

		public WrapMode preWrapMode
		{
			get => m_PreWrapMode;
			set => m_PreWrapMode = value;
		}

		public WrapMode postWrapMode
		{
			get => m_PostWrapMode;
			set => m_PostWrapMode = value;
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- presets

		/// <summary>The same value from <paramref name="timeStart" /> to <paramref name="timeEnd" />.</summary>
		public static AnimationCurve Constant(float timeStart, float timeEnd, float value)
		{
			return Linear(timeStart, value, timeEnd, value);
		}

		/// <summary>A straight line between two points.</summary>
		public static AnimationCurve Linear(float timeStart, float valueStart, float timeEnd, float valueEnd)
		{
			float slope = Math.Abs(timeEnd - timeStart) > 1e-12f ? (valueEnd - valueStart) / (timeEnd - timeStart) : 0f;
			return new AnimationCurve(
				new Keyframe(timeStart, valueStart, 0f, slope) { tangentMode = TangentMode.Linear },
				new Keyframe(timeEnd, valueEnd, slope, 0f) { tangentMode = TangentMode.Linear });
		}

		/// <summary>Starts and ends flat (smoothstep).</summary>
		public static AnimationCurve EaseInOut(float timeStart, float valueStart, float timeEnd, float valueEnd)
		{
			return new AnimationCurve(
				new Keyframe(timeStart, valueStart, 0f, 0f),
				new Keyframe(timeEnd, valueEnd, 0f, 0f));
		}

		/// <summary>Starts flat, ends at full slope (accelerating).</summary>
		public static AnimationCurve EaseIn(float timeStart, float valueStart, float timeEnd, float valueEnd)
		{
			float slope = Math.Abs(timeEnd - timeStart) > 1e-12f ? 2f * (valueEnd - valueStart) / (timeEnd - timeStart) : 0f;
			return new AnimationCurve(
				new Keyframe(timeStart, valueStart, 0f, 0f),
				new Keyframe(timeEnd, valueEnd, slope, slope));
		}

		/// <summary>Starts at full slope, ends flat (decelerating).</summary>
		public static AnimationCurve EaseOut(float timeStart, float valueStart, float timeEnd, float valueEnd)
		{
			float slope = Math.Abs(timeEnd - timeStart) > 1e-12f ? 2f * (valueEnd - valueStart) / (timeEnd - timeStart) : 0f;
			return new AnimationCurve(
				new Keyframe(timeStart, valueStart, slope, slope),
				new Keyframe(timeEnd, valueEnd, 0f, 0f));
		}

		// ---------------------------------------------------------------- evaluation

		/// <summary>The curve's value at <paramref name="time" /> (0 without keys).</summary>
		public float Evaluate(float time)
		{
			int count = m_Keys.Count;
			if (count == 0)
			{
				return 0f;
			}

			Keyframe first = m_Keys[0];
			if (count == 1)
			{
				return first.value;
			}

			Keyframe last = m_Keys[count - 1];
			float span = last.time - first.time;
			if (time < first.time)
			{
				if (!Wrap(m_PreWrapMode, ref time, first.time, span))
				{
					return first.value;
				}
			}
			else if (time > last.time)
			{
				if (!Wrap(m_PostWrapMode, ref time, first.time, span))
				{
					return last.value;
				}
			}

			// The segment [i - 1, i] holding the time.
			int low = 1;
			int high = count - 1;
			while (low < high)
			{
				int mid = (low + high) >> 1;
				if (m_Keys[mid].time < time)
				{
					low = mid + 1;
				}
				else
				{
					high = mid;
				}
			}

			return EvaluateSegment(m_Keys[low - 1], m_Keys[low], time);
		}

		/// <summary>The value between two keys (cubic Hermite with the slopes scaled by the segment's length).</summary>
		public static float EvaluateSegment(Keyframe a, Keyframe b, float time)
		{
			float dt = b.time - a.time;
			if (dt <= 1e-12f)
			{
				return b.value;
			}

			if (a.tangentMode == TangentMode.Constant || float.IsInfinity(a.outTangent) || float.IsInfinity(b.inTangent))
			{
				return time >= b.time ? b.value : a.value;
			}

			float t = (time - a.time) / dt;
			float t2 = t * t;
			float t3 = t2 * t;
			float h00 = 2f * t3 - 3f * t2 + 1f;
			float h10 = t3 - 2f * t2 + t;
			float h01 = -2f * t3 + 3f * t2;
			float h11 = t3 - t2;
			return h00 * a.value + h10 * a.outTangent * dt + h01 * b.value + h11 * b.inTangent * dt;
		}

		// ---------------------------------------------------------------- editing

		/// <summary>Adds a key with smooth (clamped auto) tangents; returns its index, or -1 when a key has that time already.</summary>
		public int AddKey(float time, float value)
		{
			int index = AddKey(new Keyframe(time, value) { tangentMode = TangentMode.ClampedAuto });
			if (index >= 0)
			{
				RecalculateTangents();
			}

			return index;
		}

		/// <summary>Adds a key as given; returns its index, or -1 when a key has that time already.</summary>
		public int AddKey(Keyframe key)
		{
			int index = InsertionIndex(key.time);
			if (index < m_Keys.Count && Math.Abs(m_Keys[index].time - key.time) < 1e-6f)
			{
				return -1;
			}

			m_Keys.Insert(index, key);
			return index;
		}

		/// <summary>Replaces key <paramref name="index" />; returns its index after re-sorting by time.</summary>
		public int MoveKey(int index, Keyframe key)
		{
			m_Keys.RemoveAt(index);
			int target = InsertionIndex(key.time);
			m_Keys.Insert(target, key);
			return target;
		}

		public void RemoveKey(int index)
		{
			m_Keys.RemoveAt(index);
		}

		public void ClearKeys()
		{
			m_Keys.Clear();
		}

		/// <summary>Makes this curve a copy of another (keys and wrap modes).</summary>
		public void CopyFrom(AnimationCurve source)
		{
			m_Keys.Clear();
			if (source == null)
			{
				return;
			}

			m_Keys.AddRange(source.m_Keys);
			m_PreWrapMode = source.m_PreWrapMode;
			m_PostWrapMode = source.m_PostWrapMode;
		}

		/// <summary>
		///     Smooths key <paramref name="index" />: one slope through it from its neighbours; <paramref name="weight" /> 0
		///     follows the neighbours, 1 the flat-near-extremes rule of clamped auto (Unity's SmoothTangents).
		/// </summary>
		public void SmoothTangents(int index, float weight)
		{
			Keyframe key = m_Keys[index];
			float slope = Slope(index, false) * (1f - Math.Clamp(weight, 0f, 1f)) + ClampedAutoSlope(index) * Math.Clamp(weight, 0f, 1f);
			key.inTangent = slope;
			key.outTangent = slope;
			key.tangentMode = TangentMode.Free;
			m_Keys[index] = key;
		}

		/// <summary>Recomputes the tangents of the keys whose mode derives them (clamped auto, linear, constant).</summary>
		public void RecalculateTangents()
		{
			for (int i = 0; i < m_Keys.Count; i++)
			{
				Keyframe key = m_Keys[i];
				switch (key.tangentMode)
				{
					case TangentMode.ClampedAuto:
						key.inTangent = key.outTangent = ClampedAutoSlope(i);
						break;
					case TangentMode.Linear:
						key.inTangent = i > 0 ? SegmentSlope(i - 1, i) : (m_Keys.Count > 1 ? SegmentSlope(0, 1) : 0f);
						key.outTangent = i < m_Keys.Count - 1 ? SegmentSlope(i, i + 1) : key.inTangent;
						break;
					case TangentMode.Constant:
						key.inTangent = key.outTangent = 0f;
						break;
					default:
						continue;
				}

				m_Keys[i] = key;
			}
		}

		public override string ToString()
		{
			return $"AnimationCurve({m_Keys.Count} keys: {string.Join(", ", m_Keys)}; {m_PreWrapMode} / {m_PostWrapMode})";
		}

		/// <summary>The time range of the keys (0, 0 without keys).</summary>
		public void GetRange(out float timeStart, out float timeEnd)
		{
			timeStart = m_Keys.Count > 0 ? m_Keys[0].time : 0f;
			timeEnd = m_Keys.Count > 0 ? m_Keys[m_Keys.Count - 1].time : 0f;
		}

		#endregion

		#region PrivateMethods

		private void Sort()
		{
			m_Keys.Sort((a, b) => a.time.CompareTo(b.time));
		}

		private int InsertionIndex(float time)
		{
			int index = 0;
			while (index < m_Keys.Count && m_Keys[index].time < time)
			{
				index++;
			}

			return index;
		}

		private float SegmentSlope(int a, int b)
		{
			float dt = m_Keys[b].time - m_Keys[a].time;
			return dt > 1e-12f ? (m_Keys[b].value - m_Keys[a].value) / dt : 0f;
		}

		/// <summary>The average slope of the two segments at a key (the one segment at an end; flat with <paramref name="flatEnds" />).</summary>
		private float Slope(int index, bool flatEnds)
		{
			int count = m_Keys.Count;
			if (count < 2)
			{
				return 0f;
			}

			if (index == 0)
			{
				return flatEnds ? 0f : SegmentSlope(0, 1);
			}

			if (index == count - 1)
			{
				return flatEnds ? 0f : SegmentSlope(count - 2, count - 1);
			}

			float left = m_Keys[index].time - m_Keys[index - 1].time;
			float right = m_Keys[index + 1].time - m_Keys[index].time;
			return (SegmentSlope(index - 1, index) * right + SegmentSlope(index, index + 1) * left) / Math.Max(1e-12f, left + right);
		}

		/// <summary>Monotone cubic slope (Fritsch-Carlson): flat at the ends and where the curve turns, never overshooting.</summary>
		private float ClampedAutoSlope(int index)
		{
			int count = m_Keys.Count;
			if (index == 0 || index == count - 1)
			{
				return 0f;
			}

			float left = SegmentSlope(index - 1, index);
			float right = SegmentSlope(index, index + 1);
			if (left * right <= 0f)
			{
				return 0f;
			}

			float slope = Slope(index, true);
			float limit = 3f * Math.Min(Math.Abs(left), Math.Abs(right));
			return Math.Sign(slope) * Math.Min(Math.Abs(slope), limit);
		}

		/// <summary>Maps a time outside the keys into them; false: clamp.</summary>
		private static bool Wrap(WrapMode mode, ref float time, float start, float span)
		{
			if (span <= 1e-12f)
			{
				return false;
			}

			float offset = time - start;
			switch (mode)
			{
				case WrapMode.Loop:
					offset -= MathF.Floor(offset / span) * span;
					time = start + offset;
					return true;
				case WrapMode.PingPong:
					float cycle = offset - MathF.Floor(offset / (2f * span)) * 2f * span;
					time = start + (cycle <= span ? cycle : 2f * span - cycle);
					return true;
				default:
					return false;
			}
		}

		#endregion
	}
}
