using System.Collections.Generic;
using LELEngine;
using OpenTK.Mathematics;

/// <summary>What a <see cref="Light" /> is.</summary>
public enum LightType
{
	/// <summary>Shines from its position in every direction.</summary>
	Point,

	/// <summary>Shines from its position inside a cone along its transform's forward axis.</summary>
	Spot
}

/// <summary>
///     A local light: a point light at its transform's position, or a spot shining along its forward axis. Physical
///     falloff: the inverse square of the distance (from a source of <see cref="SourceRadius" />), faded to nothing at
///     <see cref="Range" />. It lights the lit materials, the GI (its light bounces off what it reaches;
///     <see cref="AffectsGI" />) and the volumetric objects within its range (through their cloud;
///     <see cref="AffectsVolumes" />, as strong as <see cref="VolumetricStrength" />); clouds between it and what it lights
///     dim it (<see cref="VolumetricShadows" />).
///     Geometry does not block its direct light (no shadow maps); in the GI it does (<see cref="GIShadows" />). At most
///     <see cref="MaxLights" /> lights shine at once: the ones nearest to the camera (Rendering.LightBuffer).
/// </summary>
[ExecuteAlways]
[InspectorColor(InspectorColorKind.Lighting)]
public sealed class Light : Behaviour
{
	#region PublicFields

	/// <summary>Lights that shine at once (the shaders' light list, Engine/Lights.glsl).</summary>
	public const int MaxLights = 32;

	public LightType Type = LightType.Point;

	public Color4 Color = Color4.White;

	[Tooltip("How bright it is: a white surface facing the light one unit away reflects about this much (radiance units, as a directional light's Strength). It falls off with the distance squared: at 10 units a hundredth of it")]
	public float Intensity = 10f;

	[Tooltip("Where the light ends: it fades to nothing at this distance and costs nothing beyond it")]
	public float Range = 10f;

	[Tooltip("The cone's full angle in degrees")]
	[Range(1f, 179f)]
	[ShowIf(nameof(Type), LightType.Spot)]
	public float SpotAngle = 60f;

	[Tooltip("The share of the cone at full brightness (0: dimming from the centre, 1: a hard edge)")]
	[Range(0f, 1f)]
	[ShowIf(nameof(Type), LightType.Spot)]
	public float InnerSpot = 0.7f;

	[Tooltip("The size of the light's source in units: closer than this the light stops growing")]
	public float SourceRadius = 0.1f;

	[Header("Global illumination")]
	[Tooltip("The light bounces off what it lights (the GI: voxel cone tracing, Lumen)")]
	public bool AffectsGI = true;

	[Tooltip("How much of the light goes into the GI")]
	[Range(0f, 10f)]
	public float IndirectStrength = 1f;

	[Tooltip("Geometry blocks the light in the GI: it bounces only off what it reaches. Off: it reaches through walls there (cheaper)")]
	public bool GIShadows = true;

	[Header("Volumes")]
	[Tooltip("The light lights the volumetric objects within its range, through their cloud")]
	public bool AffectsVolumes = true;

	[Tooltip("How strongly it lights the volumetric objects, relative to surfaces (HDRP's Volumetric Multiplier): a flash inside a cloud can light the ships around brightly and the cloud less")]
	[Range(0f, 10f)]
	public float VolumetricStrength = 1f;

	[Tooltip("Clouds between the light and what it lights dim it: a flash deep in a storm lights the ships above it less than one near its top")]
	public bool VolumetricShadows = true;

	#endregion

	#region PrivateFields

	private static readonly List<Light> active = new List<Light>();

	#endregion

	#region PublicProperties

	/// <summary>The enabled lights (edit mode too), in the order they were enabled.</summary>
	public static IReadOnlyList<Light> Active => active;

	/// <summary>The radiance a white surface facing the light one unit away reflects: colour x intensity.</summary>
	public Vector3 Radiance => new Vector3(Color.R, Color.G, Color.B) * Intensity;

	/// <summary>
	///     How much light gets through the clouds from the light towards +x, -x, +y, -y, +z, -z (world), worked out by the
	///     volumetric objects every frame for a light with <see cref="VolumetricShadows" /> (1: no cloud that way).
	/// </summary>
	public float[] CloudTransmittance { get; } = { 1f, 1f, 1f, 1f, 1f, 1f };

	#endregion

	#region LifecycleMethods

	public override void OnEnable()
	{
		if (!active.Contains(this))
		{
			active.Add(this);
		}
	}

	public override void OnDisable()
	{
		active.Remove(this);
	}

	public override void OnDestroy()
	{
		active.Remove(this);
	}

	#endregion

	#region PublicMethods

	/// <summary>
	///     The light's falloff at a distance (what <see cref="Radiance" /> is multiplied by there; the shaders' formula):
	///     1 / distance^2 (from the source's radius on), faded to 0 at <see cref="Range" />.
	/// </summary>
	public float Falloff(float distance)
	{
		float range = System.MathF.Max(Range, 1e-4f);
		float d2 = distance * distance;
		float fade = System.Math.Clamp(1f - d2 * d2 / (range * range * range * range), 0f, 1f);
		return fade * fade / System.MathF.Max(d2, SourceRadius * SourceRadius);
	}

	#endregion
}
