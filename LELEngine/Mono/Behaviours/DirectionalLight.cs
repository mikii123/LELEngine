using LELEngine;
using LELEngine.Mathematics;

/// <summary>
///     A light from infinitely far away, shining along its transform's forward axis: a sun, a moon, the light of a planet
///     nearby. The loaded scenes may hold several: at most <see cref="MaxLights" /> shine at once (the main light, then
///     the brightest; Rendering.DirectionalLightBuffer). Each lights the lit materials, the particles and the volumetric
///     objects (<see cref="AffectsVolumes" />), goes into the GI (<see cref="AffectsGI" />) and casts shadows
///     (<see cref="CastShadows" />; how many lights cast them at once and how fine the other lights' maps are is the
///     project's <see cref="ShadowSettings" />). <see cref="This" /> is the main light: the active scene's first one.
/// </summary>
[ExecuteAlways]
[InspectorColor(InspectorColorKind.Lighting)]
public sealed class DirectionalLight : Behaviour
{
	#region PublicFields

	/// <summary>Directional lights that shine at once (the shaders' list, Engine/DirectionalLights.glsl).</summary>
	public const int MaxLights = 4;

	public static DirectionalLight This => SceneManager.MainLight;

	public Color4 Color = Color4.White;

	/// <summary>Radiance units: a white surface facing the sun at strength 1 reflects about 1.</summary>
	public float Strength = 1f;

	[Header("Shadows")]
	[Tooltip("Geometry blocks the light: it gets a shadow map of its own, when it is one of the lights the project lets cast shadows (Project Settings > Lighting > Shadows: the main light first, then the brightest)")]
	public bool CastShadows = true;

	[Tooltip("The angle the light's source spans in the sky, in degrees (the sun: about 0.5): traced shadows (Project Settings > Lighting > Shadows > Ray Traced: Directional Light Shadows, Soft Shadows) grow as soft as it makes them with the distance from what casts them. Shadow maps do not read it")]
	[Range(0f, 10f)]
	[ShowIf(nameof(SoftShadowsTraced))]
	public float AngularDiameter = 0.5f;

	/// <summary>
	///     The inspector shows <see cref="AngularDiameter" /> only while it can change something: the light casts shadows and
	///     the project traces directional shadows (on every GPU or on discrete ones) soft.
	/// </summary>
	private bool SoftShadowsTraced
	{
		get
		{
			RayTracingSettings rays = ProjectSettings.Current.RayTracing;
			return CastShadows && ProjectSettings.Current.Shadows.Enabled && rays.SoftShadows && (rays.DirectionalLightShadows || rays.DirectionalLightShadowsOnDiscreteGpus);
		}
	}

	[Header("Global illumination")]
	[Tooltip("The light bounces off what it lights (the GI: voxel cone tracing, Lumen)")]
	public bool AffectsGI = true;

	[Tooltip("How much of the light goes into the GI")]
	[Range(0f, 10f)]
	public float IndirectStrength = 1f;

	[Header("Volumes")]
	[Tooltip("The light lights the volumetric objects, through their cloud. Off: no cloud takes its light, also not one whose Sun it is (the main light is every cloud's sun unless the cloud names another)")]
	public bool AffectsVolumes = true;

	[Tooltip("How strongly it lights the volumetric objects, relative to surfaces (also as a cloud's sun)")]
	[Range(0f, 10f)]
	public float VolumetricStrength = 1f;

	#endregion

	#region PublicProperties

	/// <summary>The radiance a white surface facing the light reflects: colour x strength.</summary>
	public Vector3 Radiance => new Vector3(Color.R, Color.G, Color.B) * Strength;

	/// <summary>The unit vector towards the light (against its forward axis; straight up when the axis is degenerate).</summary>
	public Vector3 Towards
	{
		get
		{
			Vector3 forward = transform.forward;
			return forward.LengthSquared > 1e-12f ? -forward.Normalized() : Vector3.UnitY;
		}
	}

	/// <summary>It shines: enabled on an active object, with a strength and a colour.</summary>
	public bool Shines => !IsDestroyed && isActiveAndEnabled && Strength > 0f && (Color.R > 0f || Color.G > 0f || Color.B > 0f);

	#endregion

	#region LifecycleMethods

	public override void OnDisable()
	{
		// The frame's list keeps no light that is off (it would hold its scene until the next frame rendered).
		LELEngine.Rendering.DirectionalLightBuffer.Forget(this);
	}

	public override void OnDestroy()
	{
		LELEngine.Rendering.DirectionalLightBuffer.Forget(this);
	}

	#endregion
}
