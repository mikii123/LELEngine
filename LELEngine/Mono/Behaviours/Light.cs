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

/// <summary>How the volumetric objects take a <see cref="Light" />: set on each light, never chosen by the engine.</summary>
public enum LightVolumeMode
{
	/// <summary>
	///     A channel of each cloud's light transport of its own: its colour and intensity are free every frame, and it may
	///     move (its part is worked out again around it). A cloud has 8 channels, shared with its own lights and the scene's
	///     other directional lights; live lights past them are left out (warned once a cloud).
	/// </summary>
	Live,

	/// <summary>
	///     Summed into one texture of each cloud with its other baked lights (up to 64 a cloud, the nearest): they cost the
	///     same however many there are, and any change of one (place, range, colour, intensity, on or off) works its part
	///     of the texture out again. For lights that stay as they are (lamps).
	/// </summary>
	Baked,

	/// <summary>
	///     Worked out where the cloud is drawn, at each step of its march, with nothing made beforehand: it may move,
	///     come and go every frame for nothing (bullets, explosions, engines). It takes no channel (up to 128 a cloud, the
	///     nearest to the camera; a pixel's ray takes the first 16 whose reach it crosses) and costs the pixels whose rays
	///     pass through its reach. The cloud between it and a point is a rough estimate (two samples), and its light is not
	///     in the cloud's light in the GI.
	/// </summary>
	Dynamic
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

	[Tooltip("How the volumetric objects take it. Live: a channel of the cloud's light transport of its own (8 a cloud, shared with the cloud's own lights and the other directional lights; live lights past them are left out, with a warning): colour and intensity free every frame; moving works its part out again. Baked: summed into one texture with the cloud's other baked lights (up to 64): any change of it (place, range, colour, intensity, on or off) works its part out again, so it suits lights that stay as they are. Dynamic: worked out at each step of the cloud's march, nothing made beforehand: it may move, come and go every frame for nothing (bullets, explosions), costs the pixels whose rays cross its reach; the cloud between it and a point is a rough estimate. A script that makes a light sets this right after AddComponent, before the frame renders")]
	[ShowIf(nameof(AffectsVolumes))]
	public LightVolumeMode VolumeMode = LightVolumeMode.Live;

	[Tooltip("How strongly it lights the volumetric objects, relative to surfaces (HDRP's Volumetric Multiplier): a flash inside a cloud can light the ships around brightly and the cloud less")]
	[Range(0f, 10f)]
	[ShowIf(nameof(AffectsVolumes))]
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

	/// <summary>
	///     What <see cref="CloudTransmittance" /> was worked out for (the light's place and range, the clouds within it):
	///     Volumetrics.VolumetricRuntime works it out again only when that changes.
	/// </summary>
	internal int CloudTransmittanceKey;

	internal bool CloudTransmittanceKnown;

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
		// The light list and the GI's tracking keep no light that is off (it held its scene until frames rendered).
		LELEngine.Rendering.LightBuffer.Forget(this);
	}

	public override void OnDestroy()
	{
		active.Remove(this);
		LELEngine.Rendering.LightBuffer.Forget(this);
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
