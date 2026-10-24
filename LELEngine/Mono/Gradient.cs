using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>How a <see cref="Gradient" /> goes from one key to the next (Unity's GradientMode).</summary>
	public enum GradientMode
	{
		/// <summary>Linear interpolation of the values between the keys.</summary>
		Blend = 0,

		/// <summary>No interpolation: each key's value holds from the previous key up to its own time.</summary>
		Fixed = 1,

		/// <summary>Colors interpolated in Oklab (even perceived steps); alpha linearly.</summary>
		PerceptualBlend = 2
	}

	/// <summary>A color key of a <see cref="Gradient" />: rgb at a time in [0, 1] (alpha comes from the alpha keys).</summary>
	[Serializable]
	public struct GradientColorKey
	{
		public Color4 color;
		public float time;

		public GradientColorKey(Color4 color, float time)
		{
			this.color = color;
			this.time = time;
		}

		public override string ToString()
		{
			return $"({color.R}, {color.G}, {color.B} at {time})";
		}
	}

	/// <summary>An alpha key of a <see cref="Gradient" />: alpha at a time in [0, 1].</summary>
	[Serializable]
	public struct GradientAlphaKey
	{
		public float alpha;
		public float time;

		public GradientAlphaKey(float alpha, float time)
		{
			this.alpha = alpha;
			this.time = time;
		}

		public override string ToString()
		{
			return $"({alpha} at {time})";
		}
	}

	/// <summary>
	///     A color over [0, 1] (Unity's Gradient): rgb from the color keys, alpha from the alpha keys, each list at most
	///     <see cref="MaxKeys" /> keys sorted by time, blended as <see cref="mode" /> says; before the first key and after
	///     the last one the end keys hold. The values are used as stored (no color space conversion). Serialized inline;
	///     the inspector draws it as a bar that opens the gradient editor.
	/// </summary>
	[Serializable]
	public sealed class Gradient
	{
		#region PublicFields

		/// <summary>Most keys per list (Unity's limit).</summary>
		public const int MaxKeys = 8;

		/// <summary>A copy of the color keys, by time; setting replaces them (sorted, at most <see cref="MaxKeys" />).</summary>
		public GradientColorKey[] colorKeys
		{
			get => m_ColorKeys.ToArray();
			set
			{
				m_ColorKeys.Clear();
				if (value != null)
				{
					m_ColorKeys.AddRange(value);
				}

				Sort();
			}
		}

		/// <summary>A copy of the alpha keys, by time; setting replaces them (sorted, at most <see cref="MaxKeys" />).</summary>
		public GradientAlphaKey[] alphaKeys
		{
			get => m_AlphaKeys.ToArray();
			set
			{
				m_AlphaKeys.Clear();
				if (value != null)
				{
					m_AlphaKeys.AddRange(value);
				}

				Sort();
			}
		}

		public GradientMode mode
		{
			get => m_Mode;
			set => m_Mode = value;
		}

		public int colorKeyCount => m_ColorKeys.Count;
		public int alphaKeyCount => m_AlphaKeys.Count;

		#endregion

		#region PrivateFields

		[SerializeField] private List<GradientColorKey> m_ColorKeys = new List<GradientColorKey> { new GradientColorKey(Color4.White, 0f), new GradientColorKey(Color4.White, 1f) };
		[SerializeField] private List<GradientAlphaKey> m_AlphaKeys = new List<GradientAlphaKey> { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) };
		[SerializeField] private GradientMode m_Mode = GradientMode.Blend;

		#endregion

		#region Constructors

		/// <summary>White, opaque (two keys of each kind, as Unity's new gradient).</summary>
		public Gradient()
		{ }

		/// <summary>An independent copy of another gradient.</summary>
		public Gradient(Gradient source)
		{
			CopyFrom(source);
		}

		#endregion

		#region PublicMethods

		/// <summary>One color from start to end.</summary>
		public static Gradient Solid(Color4 color)
		{
			var gradient = new Gradient();
			gradient.SetKeys(new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) }, new[] { new GradientAlphaKey(color.A, 0f), new GradientAlphaKey(color.A, 1f) });
			return gradient;
		}

		/// <summary>From one color to another (rgb and alpha).</summary>
		public static Gradient Linear(Color4 start, Color4 end)
		{
			var gradient = new Gradient();
			gradient.SetKeys(new[] { new GradientColorKey(start, 0f), new GradientColorKey(end, 1f) }, new[] { new GradientAlphaKey(start.A, 0f), new GradientAlphaKey(end.A, 1f) });
			return gradient;
		}

		/// <summary>Replaces both key lists (Unity's SetKeys).</summary>
		public void SetKeys(GradientColorKey[] colorKeys, GradientAlphaKey[] alphaKeys)
		{
			m_ColorKeys.Clear();
			m_AlphaKeys.Clear();
			if (colorKeys != null)
			{
				m_ColorKeys.AddRange(colorKeys);
			}

			if (alphaKeys != null)
			{
				m_AlphaKeys.AddRange(alphaKeys);
			}

			Sort();
		}

		public void CopyFrom(Gradient source)
		{
			if (source == null || ReferenceEquals(source, this))
			{
				return;
			}

			m_ColorKeys = new List<GradientColorKey>(source.m_ColorKeys);
			m_AlphaKeys = new List<GradientAlphaKey>(source.m_AlphaKeys);
			m_Mode = source.m_Mode;
		}

		/// <summary>The color at a time (rgb from the color keys, a from the alpha keys); white when a list is empty.</summary>
		public Color4 Evaluate(float time)
		{
			Vector3 rgb = EvaluateColor(time);
			return new Color4(rgb.X, rgb.Y, rgb.Z, EvaluateAlpha(time));
		}

		/// <summary>Keys sorted by time and clamped to [0, 1]; lists longer than <see cref="MaxKeys" /> lose their last keys.</summary>
		public void Sort()
		{
			m_ColorKeys ??= new List<GradientColorKey>();
			m_AlphaKeys ??= new List<GradientAlphaKey>();
			for (int i = 0; i < m_ColorKeys.Count; i++)
			{
				GradientColorKey key = m_ColorKeys[i];
				key.time = Math.Clamp(float.IsNaN(key.time) ? 0f : key.time, 0f, 1f);
				m_ColorKeys[i] = key;
			}

			for (int i = 0; i < m_AlphaKeys.Count; i++)
			{
				GradientAlphaKey key = m_AlphaKeys[i];
				key.time = Math.Clamp(float.IsNaN(key.time) ? 0f : key.time, 0f, 1f);
				m_AlphaKeys[i] = key;
			}

			// Stable: keys at the same time keep their order.
			StableSort(m_ColorKeys, (a, b) => a.time.CompareTo(b.time));
			StableSort(m_AlphaKeys, (a, b) => a.time.CompareTo(b.time));
			if (m_ColorKeys.Count > MaxKeys)
			{
				m_ColorKeys.RemoveRange(MaxKeys, m_ColorKeys.Count - MaxKeys);
			}

			if (m_AlphaKeys.Count > MaxKeys)
			{
				m_AlphaKeys.RemoveRange(MaxKeys, m_AlphaKeys.Count - MaxKeys);
			}
		}

		public override string ToString()
		{
			return $"Gradient ({m_Mode}, {m_ColorKeys.Count} color keys, {m_AlphaKeys.Count} alpha keys)";
		}

		#endregion

		#region PrivateMethods

		private Vector3 EvaluateColor(float time)
		{
			List<GradientColorKey> keys = m_ColorKeys;
			if (keys == null || keys.Count == 0)
			{
				return Vector3.One;
			}

			int next = 0;
			while (next < keys.Count && time > keys[next].time)
			{
				next++;
			}

			if (next == 0 || next == keys.Count)
			{
				return Rgb(keys[next == 0 ? 0 : keys.Count - 1].color);
			}

			GradientColorKey a = keys[next - 1];
			GradientColorKey b = keys[next];
			if (m_Mode == GradientMode.Fixed)
			{
				// After a's time (the search passed it), up to and at b's: b's value.
				return Rgb(b.color);
			}

			float t = Fraction(a.time, b.time, time);
			if (m_Mode == GradientMode.PerceptualBlend)
			{
				return OklabToLinear(Vector3.Lerp(LinearToOklab(Rgb(a.color)), LinearToOklab(Rgb(b.color)), t));
			}

			return Vector3.Lerp(Rgb(a.color), Rgb(b.color), t);
		}

		private float EvaluateAlpha(float time)
		{
			List<GradientAlphaKey> keys = m_AlphaKeys;
			if (keys == null || keys.Count == 0)
			{
				return 1f;
			}

			int next = 0;
			while (next < keys.Count && time > keys[next].time)
			{
				next++;
			}

			if (next == 0 || next == keys.Count)
			{
				return keys[next == 0 ? 0 : keys.Count - 1].alpha;
			}

			GradientAlphaKey a = keys[next - 1];
			GradientAlphaKey b = keys[next];
			if (m_Mode == GradientMode.Fixed)
			{
				return b.alpha;
			}

			return a.alpha + (b.alpha - a.alpha) * Fraction(a.time, b.time, time);
		}

		private static float Fraction(float from, float to, float time)
		{
			float span = to - from;
			return span > 1e-6f ? Math.Clamp((time - from) / span, 0f, 1f) : 1f;
		}

		private static Vector3 Rgb(Color4 color)
		{
			return new Vector3(color.R, color.G, color.B);
		}

		// Oklab (Björn Ottosson) from and to linear sRGB.
		private static Vector3 LinearToOklab(Vector3 c)
		{
			float l = MathF.Cbrt(0.4122214708f * c.X + 0.5363325363f * c.Y + 0.0514459929f * c.Z);
			float m = MathF.Cbrt(0.2119034982f * c.X + 0.6806995451f * c.Y + 0.1073969566f * c.Z);
			float s = MathF.Cbrt(0.0883024619f * c.X + 0.2817188376f * c.Y + 0.6299787005f * c.Z);
			return new Vector3(
				0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
				1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
				0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
		}

		private static Vector3 OklabToLinear(Vector3 lab)
		{
			float l = lab.X + 0.3963377774f * lab.Y + 0.2158037573f * lab.Z;
			float m = lab.X - 0.1055613458f * lab.Y - 0.0638541728f * lab.Z;
			float s = lab.X - 0.0894841775f * lab.Y - 1.2914855480f * lab.Z;
			l = l * l * l;
			m = m * m * m;
			s = s * s * s;
			return new Vector3(
				4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
				-1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
				-0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
		}

		private static void StableSort<T>(List<T> list, Comparison<T> comparison)
		{
			for (int i = 1; i < list.Count; i++)
			{
				T item = list[i];
				int j = i - 1;
				while (j >= 0 && comparison(list[j], item) > 0)
				{
					list[j + 1] = list[j];
					j--;
				}

				list[j + 1] = item;
			}
		}

		#endregion
	}
}
