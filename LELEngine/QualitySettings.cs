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

/// <summary>Unity's QualitySettings for what the engine has of it: the LOD settings of the live project settings.</summary>
public static class QualitySettings
{
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
