using System;
using LELEngine;

namespace LELEngine
{
	/// <summary>
	///     How <see cref="LODGroup" />s pick their levels, project-wide (<see cref="ProjectSettings.LOD" />; Unity's
	///     QualitySettings.lodBias and maximumLODLevel).
	/// </summary>
	[Serializable]
	public sealed class LODSettings
	{
		/// <summary>Multiplies every group's height on screen: above 1 keeps the detailed levels further away.</summary>
		[Range(0.01f, 8f)]
		public float Bias = 1f;

		/// <summary>The most detailed level drawn: groups never go below it (0: all levels).</summary>
		[Range(0, 7)]
		public int MaximumLevel;
	}
}

/// <summary>
///     Unity's QualitySettings for what the engine has of it, over the live project settings: the LOD settings and super
///     sampling (a game's options menu sets these; the next frame renders with them).
/// </summary>
public static class QualitySettings
{
	/// <summary>
	///     Super sampling: the scene's pixels against the window's (1 off, up to 4; NVIDIA DSR's factors are
	///     <see cref="superSamplingFactors" />). See <see cref="LELEngine.Rendering.SuperSamplingSettings" />.
	/// </summary>
	public static float superSampling
	{
		get => ProjectSettings.Current.Graphics.SuperSampling.Factor;
		set => ProjectSettings.Current.Graphics.SuperSampling.Factor = Math.Clamp(value, 1f, LELEngine.Rendering.SuperSamplingSettings.MaxFactor);
	}

	/// <summary>The filter a super sampled frame goes down to the window with.</summary>
	public static LELEngine.Rendering.SuperSamplingFilter superSamplingFilter
	{
		get => ProjectSettings.Current.Graphics.SuperSampling.Filter;
		set => ProjectSettings.Current.Graphics.SuperSampling.Filter = value;
	}

	/// <summary>The Gaussian filter's smoothness, 0..1 (DSR's Smoothness).</summary>
	public static float superSamplingSmoothness
	{
		get => ProjectSettings.Current.Graphics.SuperSampling.Smoothness;
		set => ProjectSettings.Current.Graphics.SuperSampling.Smoothness = Math.Clamp(value, 0f, 1f);
	}

	/// <summary>The factors to offer (NVIDIA DSR's: 1 = off, 1.2 .. 4; 1.78 and 2.25 are DLDSR's too).</summary>
	public static float[] superSamplingFactors => (float[])LELEngine.Rendering.SuperSamplingSettings.Factors.Clone();

	public static float lodBias
	{
		get => ProjectSettings.Current.LOD.Bias;
		set => ProjectSettings.Current.LOD.Bias = Math.Max(0.01f, value);
	}

	public static int maximumLODLevel
	{
		get => ProjectSettings.Current.LOD.MaximumLevel;
		set => ProjectSettings.Current.LOD.MaximumLevel = Math.Max(0, value);
	}
}
