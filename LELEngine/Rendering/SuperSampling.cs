using System;

namespace LELEngine.Rendering
{
	/// <summary>How a super sampled frame is filtered down to the window (<see cref="SuperSamplingSettings.Filter" />).</summary>
	public enum SuperSamplingFilter
	{
		/// <summary>
		///     A Gaussian over the window pixel's footprint, wider with <see cref="SuperSamplingSettings.Smoothness" /> (NVIDIA
		///     DSR's filter and its Smoothness): soft, no ringing, no moire at the uneven factors.
		/// </summary>
		Gaussian,

		/// <summary>
		///     Each window pixel the exact average of the area it covers (a box over the footprint, partial texels weighted
		///     by their share): the sharpest without ringing; at 4x the plain 2 x 2 average.
		/// </summary>
		Area,

		/// <summary>Lanczos-2 over the footprint: the crispest image (a sharpened look, like DLDSR's), a little ringing at hard edges.</summary>
		Lanczos
	}

	/// <summary>
	///     Super sampling anti-aliasing (Project Settings > Graphics, <see cref="QualitySettings.superSampling" /> at run
	///     time): the scene renders at <see cref="Factor" /> times the window's pixels and is filtered down to the window
	///     after tonemapping, in linear light; the UI draws at the window's resolution over it. The factors are NVIDIA DSR's
	///     (and DLDSR's 1.78x and 2.25x): the same pixel counts a player knows from the driver, 1.78x = 4/3 per axis, 2.25x
	///     = 1.5, 4x = 2. The driver's DSR does the same thing outside the game (the game renders at a larger "display"
	///     resolution and the driver filters it); both at once multiply.
	/// </summary>
	[Serializable]
	public sealed class SuperSamplingSettings
	{
		#region PublicFields

		/// <summary>The pixel count of the scene against the window's: 1 off, up to 4 (2 x 2). <see cref="Factors" /> are the usual ones.</summary>
		public float Factor = 1f;

		public SuperSamplingFilter Filter = SuperSamplingFilter.Gaussian;

		/// <summary>The Gaussian's width (DSR's Smoothness, 0..1, default 0.33): 0 keeps the most detail, 1 is soft.</summary>
		[Range(0f, 1f)]
		public float Smoothness = 0.33f;

		/// <summary>NVIDIA DSR's factors (1.78x and 2.25x also DLDSR's), 1 = off.</summary>
		public static readonly float[] Factors = { 1f, 1.2f, 1.5f, 1.78f, 2f, 2.25f, 3f, 4f };

		public const float MaxFactor = 4f;

		#endregion

		#region PublicMethods

		/// <summary>
		///     The scale along each axis for a factor: its square root, with DSR's rounded labels taken exactly (1.78x is
		///     16/9: 4/3 per axis, 2560 x 1440 from 1920 x 1080).
		/// </summary>
		public static float AxisScale(float factor)
		{
			if (!(factor > 1.001f))
			{
				return 1f;
			}

			factor = Math.Min(factor, MaxFactor);
			if (Math.Abs(factor - 1.78f) < 0.005f)
			{
				factor = 16f / 9f;
			}

			return MathF.Sqrt(factor);
		}

		/// <summary>The scene's size for a window of this size at this factor.</summary>
		public static (int width, int height) RenderSize(int width, int height, float factor)
		{
			float scale = AxisScale(factor);
			return (Math.Max(1, (int)MathF.Round(width * scale)), Math.Max(1, (int)MathF.Round(height * scale)));
		}

		/// <summary>The factor's label as DSR shows it ("1.78x"; "Off" for 1).</summary>
		public static string Label(float factor)
		{
			return factor > 1.001f ? factor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "x" : "Off";
		}

		#endregion
	}
}
