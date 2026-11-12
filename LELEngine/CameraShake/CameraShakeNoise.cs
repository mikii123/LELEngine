using System;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     What camera shakes are made of: pure functions of time (no state, no frame rate in them), so a shake can be
	///     worked out anywhere on its line (the inspector's preview, tests, a shake arriving late at a far camera) and two
	///     cameras hearing one shake agree about it.
	/// </summary>
	internal static class CameraShakeNoise
	{
		#region PrivateFields

		// Lattice cells of the gradient noise per cycle: the signal then crosses zero twice a cycle on average
		// (measured: 0.573 cycles per cell).
		private const double CellsPerCycle = 1.745;

		// 1 / RMS of the two gradient noises summed (measured: 0.199).
		private const float ToUnitRms = 5.025f;

		#endregion

		#region PublicMethods

		/// <summary>
		///     A smooth random signal around zero with an RMS of 1 (it seldom passes 3 and never 4.8), swinging about once
		///     per unit of <paramref name="cycles" /> (seconds x frequency). Another <paramref name="seed" /> is another
		///     signal. Two gradient noises on lattices that do not line up: one alone stands at zero on every lattice point.
		/// </summary>
		public static float Signal(double cycles, int seed)
		{
			double x = cycles * CellsPerCycle;
			return (Gradient(x + Hash01(1, seed), seed) + Gradient(x * 0.8731 + Hash01(2, seed), seed ^ 0x5F3759DF)) * ToUnitRms;
		}

		/// <summary>
		///     Octaves of <see cref="Signal" /> from the base frequency up, each <paramref name="gain" /> times the one
		///     below it (0.5: a spectrum falling as 1 / f squared, a random walk's). An octave at <paramref name="cutoff" />
		///     times the base frequency and above is damped (<see cref="LowPass" />). Without that the RMS is 1.
		/// </summary>
		public static float Fractal(double cycles, int seed, int octaves, float gain, float cutoff)
		{
			float sum = 0f;
			float power = 0f;
			float weight = 1f;
			float ratio = 1f;
			for (int i = 0; i < octaves; i++)
			{
				sum += weight * LowPass(ratio / cutoff) * Signal(cycles, unchecked(seed + i * 7919));
				power += weight * weight;
				weight *= gain;
				cycles *= 2.0;
				ratio *= 2f;
			}

			return power > 0f ? sum / MathF.Sqrt(power) : 0f;
		}

		/// <summary>
		///     A value in -1..1 that holds, then goes to another in one quick eased move, about once per unit of
		///     <paramref name="moves" /> (at uneven moments). Signals with the same <paramref name="timing" /> move at the
		///     same moments; <paramref name="values" /> picks where to. <paramref name="moveLength" />: how long a move
		///     takes, as a share of a unit (0.3 at most: the moves never run into each other).
		/// </summary>
		public static float Holds(double moves, int timing, int values, float moveLength)
		{
			double cell = Math.Floor(moves);
			long move = (long)cell;
			float within = (float)(moves - cell);
			float start = 0.7f * Hash01(move, timing);
			if (within < start)
			{
				// This cell's move has not begun: the last one is the cell's before.
				move--;
				within += 1f;
				start = 0.7f * Hash01(move, timing);
			}

			float from = Hash(move - 1, values);
			float to = Hash(move, values);
			return from + (to - from) * SmootherStep((within - start) / MathF.Max(MathF.Min(moveLength, 0.3f), 1e-4f));
		}

		/// <summary>
		///     How far something knocked at time 0 stands from its rest <paramref name="time" /> seconds later, hanging on
		///     a spring of <paramref name="frequency" /> Hz: out to 1 (its farthest), then back, swinging through as far
		///     as <paramref name="damping" /> lets it (1 and above: back without swinging through).
		/// </summary>
		public static float Kick(float time, float frequency, float damping)
		{
			if (time <= 0f || frequency <= 0f)
			{
				return 0f;
			}

			float omega = MathF.Tau * frequency;
			if (damping >= 0.999f)
			{
				float x = omega * time;
				return x * MathF.Exp(1f - x);
			}

			damping = MathF.Max(damping, 0.02f);
			float damped = omega * MathF.Sqrt(1f - damping * damping);
			float peakTime = MathF.Atan2(damped, damping * omega) / damped;
			float peak = MathF.Exp(-damping * omega * peakTime) * MathF.Sin(damped * peakTime);
			return MathF.Exp(-damping * omega * time) * MathF.Sin(damped * time) / peak;
		}

		/// <summary>Seconds after which <see cref="Kick" /> stays within a hundredth of its farthest.</summary>
		public static float KickLength(float frequency, float damping)
		{
			if (frequency <= 0f)
			{
				return 0f;
			}

			float omega = MathF.Tau * frequency;
			return damping >= 0.999f ? 8.5f / omega : 4.6f / (MathF.Max(damping, 0.02f) * omega);
		}

		/// <summary>What a low pass of 12 dB per octave lets through of a frequency <paramref name="ratio" /> times its cutoff.</summary>
		public static float LowPass(float ratio)
		{
			float squared = ratio * ratio;
			return 1f / MathF.Sqrt(1f + squared * squared);
		}

		/// <summary>
		///     <paramref name="value" /> held within <paramref name="limit" /> without a hard stop: untouched up to 0.6 of
		///     the limit, then bending towards it. No limit at 0 or below.
		/// </summary>
		public static Vector3 SoftLimit(Vector3 value, float limit)
		{
			if (limit <= 0f)
			{
				return value;
			}

			float length = value.Length;
			float knee = 0.6f * limit;
			if (length <= knee)
			{
				return value;
			}

			float room = limit - knee;
			return value * (MathF.Min(knee + room * MathF.Tanh((length - knee) / room), limit) / length);
		}

		public static float SoftLimit(float value, float limit)
		{
			if (limit <= 0f)
			{
				return value;
			}

			float length = MathF.Abs(value);
			float knee = 0.6f * limit;
			if (length <= knee)
			{
				return value;
			}

			float room = limit - knee;
			return MathF.Sign(value) * MathF.Min(knee + room * MathF.Tanh((length - knee) / room), limit);
		}

		public static float Clamp01(float value)
		{
			return value < 0f ? 0f : value > 1f ? 1f : value;
		}

		/// <summary>0 at 0 and below, 1 at 1 and above, an S between (flat at both ends).</summary>
		public static float SmoothStep(float t)
		{
			t = Clamp01(t);
			return t * t * (3f - 2f * t);
		}

		/// <summary>As <see cref="SmoothStep" />, with no jolt at the ends either (the second derivative is 0 there).</summary>
		public static float SmootherStep(float t)
		{
			t = Clamp01(t);
			return t * t * t * (t * (t * 6f - 15f) + 10f);
		}

		/// <summary>A number in -1..1 from a whole number and a seed.</summary>
		public static float Hash(long n, int seed)
		{
			unchecked
			{
				ulong h = (ulong)n * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)seed + 0x632BE59BD9B4E019UL) * 0xC2B2AE3D27D4EB4FUL;
				h ^= h >> 29;
				h *= 0xBF58476D1CE4E5B9UL;
				h ^= h >> 32;
				h *= 0x94D049BB133111EBUL;
				h ^= h >> 29;
				return (h >> 40) / (float)(1 << 23) - 1f;
			}
		}

		/// <summary>A number in 0..1 from a whole number and a seed.</summary>
		public static float Hash01(long n, int seed)
		{
			return Hash(n, unchecked(seed + 0x3C6EF372)) * 0.5f + 0.5f;
		}

		#endregion

		#region PrivateMethods

		// Gradient noise on a lattice of whole numbers: about -0.5..0.5, zero on every lattice point.
		private static float Gradient(double x, int seed)
		{
			double cell = Math.Floor(x);
			float f = (float)(x - cell);
			long i = (long)cell;
			float u = f * f * f * (f * (f * 6f - 15f) + 10f);
			float a = Hash(i, seed) * f;
			float b = Hash(i + 1, seed) * (f - 1f);
			return a + (b - a) * u;
		}

		#endregion
	}
}
