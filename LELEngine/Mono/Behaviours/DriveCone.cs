using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;

/// <summary>Ready-made looks for <see cref="DriveCone.ApplyPreset" /> (and the inspector's Preset menu).</summary>
public enum DriveConePreset
{
	/// <summary>A jet engine's afterburner: an orange flame round a white-blue core and a row of bright shock diamonds.</summary>
	Afterburner = 0,

	/// <summary>A rocket engine: a long, widening yellow-white plume, few diamonds, much turbulence.</summary>
	Rocket = 1,

	/// <summary>A fusion drive: a narrow, very long blue-white plume with many faint diamonds.</summary>
	FusionDrive = 2,

	/// <summary>An ion thruster: a short, dim, steady blue glow without diamonds.</summary>
	Ion = 3,

	/// <summary>
	///     A torch drive: a plume as wide as the nozzle, white-blue at the exit and deep blue along its length, fading out
	///     with soft streaks, no diamonds; the nozzle glows blue seen from behind.
	/// </summary>
	TorchDrive = 4
}

/// <summary>
///     The exhaust plume of an engine's nozzle, a drive cone: a jet's afterburner flame with its shock (Mach) diamonds, a
///     rocket's or a fusion drive's plume. It runs along the transform's +Z axis from its origin (put the object at the
///     nozzle's exit, +Z pointing out of it) and is drawn by <c>Engine/DriveCone.shader</c> in the transparent pass as two
///     cone shells, the flame and the hot core inside it: the vertex shader gives the shells the plume's current shape
///     (radius along it from <see cref="WidthCurve" />, length, ripples flowing out, the end swaying), the fragment shader
///     lights them (the flame's color along the plume fading towards the silhouette, the turbulence, the core and its
///     diamonds) and fades them where they meet the hull behind. The material holds the overall brightness and that fade;
///     the shape, colors and animation are this component's, sent per draw through a <see cref="MaterialPropertyBlock" />,
///     so every cone animates on its own with one shared material.
///     <para>
///         Animation, advanced in <see cref="LateUpdate" /> (edit mode too): <see cref="Throttle" /> is followed over
///         <see cref="SpoolUpTime" /> / <see cref="SpoolDownTime" /> (length, width, brightness and the diamonds grow with
///         it); <see cref="Burning" /> off lets the plume die down over <see cref="ShutdownTime" />; lighting up
///         (<see cref="Burning" /> turned on, <see cref="PlayIgnition" />, or the throttle going from idle to full) plays
///         <see cref="IgnitionCurve" />: a flash of extra brightness and length. On top: brightness flicker, a combustion
///         pulse in length and width, the end of the plume swaying sideways, and turbulence flowing out at a speed that
///         follows the throttle. Every field can be animated by an <c>AnimationClip</c> as well.
///     </para>
///     No light is cast and no shadow; the plume is additive light (it never darkens what is behind it).
/// </summary>
[ExecuteAlways]
[InspectorColor(InspectorColorKind.Rendering)]
public sealed class DriveCone : Behaviour
{
	#region PublicFields

	/// <summary>The built-in material a drive cone added in the editor starts with.</summary>
	public const string DefaultMaterialPath = "Engine/Materials/Default-DriveCone.material";

	/// <summary>Samples of the width curve the shader gets (radius at t = i / (ProfileSamples - 1)).</summary>
	public const int ProfileSamples = 17;

	/// <summary>Samples of the flame gradient the shader gets.</summary>
	public const int FlameSamples = 8;

	[Header("Thrust")]
	[Range(0f, 1f)]
	[Tooltip("Thrust share the plume shows: 0 idle, 1 full. Set it every frame (or animate it); the plume follows over Spool Up / Spool Down Time")]
	public float Throttle = 1f;

	[Tooltip("The engine burns. Off: the plume dies down over Shutdown Time; on again: it lights up with the ignition")]
	public bool Burning = true;

	[Tooltip("Seconds the plume takes to follow the throttle up (about two thirds of the way)")]
	public float SpoolUpTime = 0.12f;

	[Tooltip("Seconds the plume takes to follow the throttle down")]
	public float SpoolDownTime = 0.3f;

	[Tooltip("Seconds the plume takes to die down when the engine stops")]
	public float ShutdownTime = 0.2f;

	[Header("Shape")]
	[Tooltip("Radius of the nozzle's exit (local units); the other sizes count in it")]
	public float NozzleRadius = 0.5f;

	[Tooltip("Length of the plume at full throttle (local units)")]
	public float Length = 7f;

	[Range(0f, 1f)]
	[Tooltip("Length at throttle 0, as a share of Length")]
	public float IdleLength = 0.25f;

	[Range(0f, 1f)]
	[Tooltip("Width at throttle 0, as a share of the full width")]
	public float IdleWidth = 0.75f;

	[Tooltip("Radius along the plume (time 0 at the nozzle, 1 at the end) in nozzle radii")]
	public AnimationCurve WidthCurve = Curve((0f, 1f), (0.12f, 1.06f), (0.5f, 1.2f), (1f, 1.55f));

	[Header("Color")]
	[Tooltip("Flame color along the plume (0 at the nozzle, 1 at the end); alpha is how dense the flame is there")]
	public Gradient FlameColor = Flame((0f, 1f, 0.55f, 0.22f, 0.95f), (0.45f, 1f, 0.36f, 0.1f, 0.6f), (1f, 0.65f, 0.12f, 0.06f, 0f));

	[Tooltip("Brightness of the flame (above 1 is HDR glow)")]
	public float FlameIntensity = 1.6f;

	[Tooltip("Color of the hot core leaving the nozzle")]
	public Color4 CoreColor = new Color4(0.75f, 0.86f, 1f, 1f);

	[Tooltip("Brightness of the core")]
	public float CoreIntensity = 5f;

	[Range(0f, 1f)]
	[Tooltip("How far along the plume the core reaches")]
	public float CoreLength = 0.3f;

	[Range(0.05f, 1.5f)]
	[Tooltip("Radius of the core at the nozzle, as a share of the nozzle's")]
	public float CoreRadius = 0.65f;

	[Range(0.25f, 4f)]
	[Tooltip("How the flame thins out towards its edge: 1 evenly, above 1 the light gathers in the middle, below 1 a fuller flame")]
	public float Falloff = 1.4f;

	[Range(0f, 1f)]
	[Tooltip("Brightness at throttle 0, as a share of the full brightness")]
	public float IdleBrightness = 0.45f;

	[Range(0f, 4f)]
	[Tooltip("Brightness of the glowing disc on the nozzle's exit (the hot gas seen down the nozzle from behind); 0 none")]
	public float NozzleGlow = 0.6f;

	[Range(0f, 8f)]
	[Tooltip("Overall brightness")]
	public float Intensity = 1f;

	[Header("Shock Diamonds")]
	[Range(0, 16)]
	[Tooltip("Number of shock diamonds (Mach diamonds) in the jet; 0 none")]
	public int DiamondCount = 6;

	[Tooltip("Distance between two diamonds at full throttle, in nozzle radii (closer together at lower throttle)")]
	public float DiamondSpacing = 1.7f;

	[Range(0f, 4f)]
	[Tooltip("Where the first diamond sits, in spacings from the nozzle")]
	public float DiamondOffset = 0.8f;

	[Range(0.05f, 1f)]
	[Tooltip("Length of a diamond, as a share of the spacing")]
	public float DiamondLength = 0.55f;

	[Range(0.05f, 1f)]
	[Tooltip("Width of a diamond across the core: small values give tight bright knots on the axis")]
	public float DiamondRadius = 0.5f;

	[Tooltip("Color of the diamonds")]
	public Color4 DiamondColor = new Color4(1f, 0.86f, 0.62f, 1f);

	[Tooltip("Brightness of the first diamond")]
	public float DiamondIntensity = 5f;

	[Range(0f, 1f)]
	[Tooltip("Brightness each diamond keeps of the one before it")]
	public float DiamondFade = 0.72f;

	[Tooltip("Throttle at which the diamonds start to show (x) and are fully there (y)")]
	public Vector2 DiamondThrottle = new Vector2(0.55f, 0.92f);

	[Header("Motion")]
	[Tooltip("Speed the turbulence flows out at full throttle, in nozzle radii per second")]
	public float FlowSpeed = 16f;

	[Range(0f, 1f)]
	[Tooltip("How much the turbulence breaks the flame up (and its edge)")]
	public float Turbulence = 0.4f;

	[Tooltip("Length of the turbulence's streaks along the plume, in nozzle radii")]
	public float TurbulenceScale = 1.1f;

	[Range(0.5f, 8f)]
	[Tooltip("How finely the turbulence streaks round the plume: more and thinner streaks read as faster gas")]
	public float Streaks = 1.5f;

	[Range(0f, 1f)]
	[Tooltip("Brightness flicker")]
	public float Flicker = 0.1f;

	[Tooltip("Flickers per second")]
	public float FlickerRate = 16f;

	[Range(0f, 0.5f)]
	[Tooltip("How much the plume's length and width breathe (combustion pulse)")]
	public float Pulse = 0.03f;

	[Tooltip("Pulses per second")]
	public float PulseRate = 11f;

	[Range(0f, 2f)]
	[Tooltip("How far the end of the plume sways sideways, in nozzle radii")]
	public float Sway = 0.1f;

	[Tooltip("Sways per second")]
	public float SwayRate = 1.4f;

	[Header("Ignition")]
	[Tooltip("Played when the engine lights up: brightness added over the seconds since it lit (0 adds nothing); the plume stretches by Ignition Stretch x the same")]
	public AnimationCurve IgnitionCurve = Curve((0f, 0f), (0.05f, 1.6f), (0.18f, 0.45f), (0.45f, 0f));

	[Range(0f, 2f)]
	[Tooltip("Share of the ignition curve added to the plume's length")]
	public float IgnitionStretch = 0.35f;

	[Tooltip("The ignition also plays when the throttle goes from idle (below 0.1) to full (0.95 and up), as an afterburner lights")]
	public bool IgniteOnFullThrottle = true;

	[Header("Rendering")]
	[Tooltip("A material with Engine/DriveCone.shader (overall brightness, soft fade into the hull); the plume's shape and colors are this component's")]
	public Material Material;

	[Range(6, 48)]
	[Tooltip("Mesh detail around the plume")]
	public int RadialSegments = 24;

	[Range(2, 48)]
	[Tooltip("Mesh detail along the plume (the ripples and the bend need some)")]
	public int LengthSegments = 16;

	/// <summary>The throttle the plume shows now (follows <see cref="Throttle" />).</summary>
	public float CurrentThrottle => level;

	/// <summary>How much the engine burns now: 1 burning, 0 out (follows <see cref="Burning" />).</summary>
	public float CurrentBurn => burn;

	/// <summary>The plume's length now (local units), with the pulse and the ignition's stretch.</summary>
	public float CurrentLength => length;

	/// <summary>The plume's overall brightness now (intensity, throttle, burn, flicker and ignition).</summary>
	public float CurrentBrightness => brightness;

	/// <summary>Seconds since the ignition started while it plays; -1 when it does not.</summary>
	public float IgnitionTime => ignitionTime;

	/// <summary>The plume shows: it has a length and some light.</summary>
	public bool IsVisible => length > 1e-4f && brightness > 1e-4f && NozzleRadius > 0f;

	/// <summary>The material as a slot list (inspector, Unity's sharedMaterials).</summary>
	public IReadOnlyList<Material> Materials => Material != null ? new[] { Material } : Array.Empty<Material>();

	#endregion

	#region InternalFields

	/// <summary>The values the shader gets for the next draw (written by <see cref="PrepareDraw" />).</summary>
	internal MaterialPropertyBlock PropertyBlock => block;

	#endregion

	#region PrivateFields

	// Shell meshes by segment counts: unit rings (x, y on the unit circle, z the share of the length) of the flame and the
	// core shell the vertex shader shapes; shared by every cone.
	private static readonly Dictionary<int, Mesh> shellMeshes = new Dictionary<int, Mesh>();

	// Nozzle radii the gas flows before its distance wraps.
	private const float FlowWrap = 16384f;

	private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
	private readonly float[] profile = new float[ProfileSamples];
	private readonly Vector4[] flame = new Vector4[FlameSamples];

	// Animated state.
	private float level;
	private float burn;
	private float ignitionTime = -1f;
	private bool armed;
	private bool wasBurning;
	private bool started;
	private float clock;
	private float flowTime;
	private float length;
	private float widthScale;
	private float brightness;
	private Vector2 sway;
	private float seed = -1f;

	#endregion

	#region PublicMethods

	public override void OnEnable()
	{
		// Shows the state it is told at once (a spawned ship's engines do not spool up from nothing); changes from now on
		// are animated.
		Snap();
	}

	public override void LateUpdate()
	{
		Step(Time.deltaTime);
	}

	public override void Reset()
	{
		if (Material == null && System.IO.File.Exists(AssetDatabase.ToAbsolute(DefaultMaterialPath)))
		{
			Material = InternalStorage.GetOrCreateMaterial(DefaultMaterialPath);
		}
	}

	public override void OnValidate()
	{
		SpoolUpTime = MathF.Max(0f, SpoolUpTime);
		SpoolDownTime = MathF.Max(0f, SpoolDownTime);
		ShutdownTime = MathF.Max(0f, ShutdownTime);
		NozzleRadius = MathF.Max(0f, NozzleRadius);
		Length = MathF.Max(0f, Length);
		FlameIntensity = MathF.Max(0f, FlameIntensity);
		CoreIntensity = MathF.Max(0f, CoreIntensity);
		DiamondSpacing = MathF.Max(0.05f, DiamondSpacing);
		DiamondIntensity = MathF.Max(0f, DiamondIntensity);
		TurbulenceScale = MathF.Max(0.01f, TurbulenceScale);
		FlickerRate = MathF.Max(0f, FlickerRate);
		PulseRate = MathF.Max(0f, PulseRate);
		SwayRate = MathF.Max(0f, SwayRate);
		RadialSegments = Math.Clamp(RadialSegments, 6, 48);
		LengthSegments = Math.Clamp(LengthSegments, 2, 48);
		WidthCurve ??= Curve((0f, 1f), (1f, 1f));
		IgnitionCurve ??= new AnimationCurve();
		FlameColor ??= new Gradient();
		FlameColor.Sort();
	}

	/// <summary>Jumps to the state the fields ask for (no spooling, no ignition): pooled or teleported objects.</summary>
	public void Snap()
	{
		level = Math.Clamp(Throttle, 0f, 1f);
		burn = Burning ? 1f : 0f;
		wasBurning = Burning;
		armed = level < 0.1f;
		ignitionTime = -1f;
		started = true;
		Step(0f);
	}

	/// <summary>Plays the ignition (a flash of extra brightness and length) from its start.</summary>
	public void PlayIgnition()
	{
		ignitionTime = 0f;
	}

	/// <summary>Lights the engine: <see cref="Burning" /> on, with the ignition.</summary>
	public void Ignite()
	{
		Burning = true;
		wasBurning = true;
		PlayIgnition();
	}

	/// <summary>Stops the engine: the plume dies down over <see cref="ShutdownTime" />.</summary>
	public void Shutdown()
	{
		Burning = false;
	}

	/// <summary>
	///     Advances the animation by <paramref name="deltaTime" /> seconds (<see cref="LateUpdate" /> does it with the
	///     frame's time): the throttle and burn follow the fields, the ignition, flicker, pulse, sway and flow move on.
	/// </summary>
	public void Step(float deltaTime)
	{
		if (!started)
		{
			Snap();
			return;
		}

		float dt = MathF.Max(0f, deltaTime);
		clock += dt;
		float target = Math.Clamp(Throttle, 0f, 1f);

		// Lighting up: Burning turned on, or the throttle pushed from idle to full (an afterburner lighting).
		if (Burning && !wasBurning)
		{
			PlayIgnition();
		}

		wasBurning = Burning;
		if (!Burning || target < 0.1f)
		{
			armed = true;
		}
		else if (armed && target >= 0.95f)
		{
			armed = false;
			if (IgniteOnFullThrottle)
			{
				PlayIgnition();
			}
		}

		level = Follow(level, target, target > level ? SpoolUpTime : SpoolDownTime, dt);
		burn = Follow(burn, Burning ? 1f : 0f, Burning ? SpoolUpTime * 0.5f : ShutdownTime, dt);

		float ignition = 0f;
		if (ignitionTime >= 0f)
		{
			ignitionTime += dt;
			IgnitionCurve.GetRange(out _, out float end);
			if (IgnitionCurve.length == 0 || ignitionTime > end)
			{
				ignitionTime = -1f;
			}
			else
			{
				ignition = MathF.Max(0f, IgnitionCurve.Evaluate(ignitionTime));
			}
		}

		float s = Seed();
		float pulse = Pulse * (0.7f * MathF.Sin((clock * PulseRate + s * 7f) * MathF.Tau) + 0.3f * Noise(clock * PulseRate * 1.7f + s * 31f));
		float flicker = Flicker * Noise(clock * FlickerRate + s * 53f);
		length = MathF.Max(0f, Length) * Lerp(IdleLength, 1f, level) * (0.35f + 0.65f * burn) * (1f + IgnitionStretch * ignition) * (1f + pulse);
		widthScale = Lerp(IdleWidth, 1f, level) * (0.6f + 0.4f * burn) * (1f + 0.35f * pulse);
		brightness = MathF.Max(0f, Intensity) * burn * Lerp(IdleBrightness, 1f, level) * (1f + ignition) * MathF.Max(0f, 1f - flicker);
		sway = new Vector2(Noise(clock * SwayRate + s * 11f), Noise(clock * SwayRate + s * 19f + 5.3f)) * (Sway * NozzleRadius * burn);
		// The gas flows out faster at higher throttle; the phase is integrated so a throttle change never jumps the pattern.
		// It wraps after a long while (minutes at full throttle) to keep the shader's noise coordinates precise.
		flowTime += dt * FlowSpeed * Lerp(0.35f, 1f, level);
		if (flowTime > FlowWrap)
		{
			flowTime -= FlowWrap;
		}
	}

	/// <summary>Sets the look of a ready-made kind of engine; the nozzle's size stays, the length follows it.</summary>
	public void ApplyPreset(DriveConePreset preset)
	{
		float radius = NozzleRadius > 0f ? NozzleRadius : 0.5f;
		switch (preset)
		{
			case DriveConePreset.Afterburner:
				Length = radius * 14f;
				IdleLength = 0.25f;
				IdleWidth = 0.75f;
				WidthCurve = Curve((0f, 1f), (0.12f, 1.06f), (0.5f, 1.2f), (1f, 1.55f));
				FlameColor = Flame((0f, 1f, 0.55f, 0.22f, 0.95f), (0.45f, 1f, 0.36f, 0.1f, 0.6f), (1f, 0.65f, 0.12f, 0.06f, 0f));
				FlameIntensity = 1.6f;
				CoreColor = new Color4(0.75f, 0.86f, 1f, 1f);
				CoreIntensity = 5f;
				CoreLength = 0.3f;
				CoreRadius = 0.65f;
				Falloff = 1.4f;
				IdleBrightness = 0.45f;
				NozzleGlow = 0.6f;
				DiamondCount = 6;
				DiamondSpacing = 1.7f;
				DiamondOffset = 0.8f;
				DiamondLength = 0.55f;
				DiamondRadius = 0.5f;
				DiamondColor = new Color4(1f, 0.86f, 0.62f, 1f);
				DiamondIntensity = 5f;
				DiamondFade = 0.72f;
				DiamondThrottle = new Vector2(0.55f, 0.92f);
				FlowSpeed = 16f;
				Turbulence = 0.4f;
				TurbulenceScale = 1.1f;
				Flicker = 0.1f;
				FlickerRate = 16f;
				Pulse = 0.03f;
				PulseRate = 11f;
				Sway = 0.1f;
				SwayRate = 1.4f;
				IgnitionStretch = 0.35f;
				SpoolUpTime = 0.12f;
				SpoolDownTime = 0.3f;
				ShutdownTime = 0.2f;
				Streaks = 1.5f;
				IgnitionCurve = Curve((0f, 0f), (0.05f, 1.6f), (0.18f, 0.45f), (0.45f, 0f));
				break;
			case DriveConePreset.Rocket:
				Length = radius * 20f;
				IdleLength = 0.3f;
				IdleWidth = 0.7f;
				WidthCurve = Curve((0f, 1f), (0.2f, 1.3f), (0.6f, 1.9f), (1f, 2.6f));
				FlameColor = Flame((0f, 1f, 0.9f, 0.6f, 1f), (0.4f, 1f, 0.6f, 0.25f, 0.7f), (1f, 0.8f, 0.3f, 0.1f, 0f));
				FlameIntensity = 2f;
				CoreColor = new Color4(1f, 0.95f, 0.85f, 1f);
				CoreIntensity = 5f;
				CoreLength = 0.25f;
				CoreRadius = 0.75f;
				Falloff = 1f;
				IdleBrightness = 0.5f;
				NozzleGlow = 0.5f;
				DiamondCount = 3;
				DiamondSpacing = 2.4f;
				DiamondOffset = 1f;
				DiamondLength = 0.5f;
				DiamondRadius = 0.45f;
				DiamondColor = new Color4(1f, 0.95f, 0.8f, 1f);
				DiamondIntensity = 3f;
				DiamondFade = 0.55f;
				DiamondThrottle = new Vector2(0.3f, 0.8f);
				FlowSpeed = 22f;
				Turbulence = 0.6f;
				TurbulenceScale = 1.4f;
				Flicker = 0.15f;
				FlickerRate = 20f;
				Pulse = 0.05f;
				PulseRate = 8f;
				Sway = 0.2f;
				SwayRate = 1.1f;
				IgnitionStretch = 0.5f;
				SpoolUpTime = 0.08f;
				SpoolDownTime = 0.15f;
				ShutdownTime = 0.12f;
				Streaks = 1.5f;
				IgnitionCurve = Curve((0f, 0f), (0.04f, 1.8f), (0.15f, 0.5f), (0.4f, 0f));
				break;
			case DriveConePreset.FusionDrive:
				Length = radius * 40f;
				IdleLength = 0.15f;
				IdleWidth = 0.8f;
				WidthCurve = Curve((0f, 1f), (0.15f, 0.82f), (0.5f, 0.9f), (1f, 1.25f));
				FlameColor = Flame((0f, 0.55f, 0.75f, 1f, 0.9f), (0.5f, 0.35f, 0.5f, 1f, 0.45f), (1f, 0.3f, 0.25f, 0.9f, 0f));
				FlameIntensity = 2f;
				CoreColor = new Color4(0.65f, 0.82f, 1f, 1f);
				CoreIntensity = 6f;
				CoreLength = 0.55f;
				CoreRadius = 0.5f;
				Falloff = 1.6f;
				IdleBrightness = 0.35f;
				NozzleGlow = 1f;
				DiamondCount = 8;
				DiamondSpacing = 2.2f;
				DiamondOffset = 0.6f;
				DiamondLength = 0.4f;
				DiamondRadius = 0.45f;
				DiamondColor = new Color4(0.7f, 0.85f, 1f, 1f);
				DiamondIntensity = 4f;
				DiamondFade = 0.85f;
				DiamondThrottle = new Vector2(0.4f, 0.85f);
				FlowSpeed = 30f;
				Turbulence = 0.2f;
				TurbulenceScale = 1.6f;
				Flicker = 0.05f;
				FlickerRate = 25f;
				Pulse = 0.02f;
				PulseRate = 14f;
				Sway = 0.05f;
				SwayRate = 0.8f;
				IgnitionStretch = 0.25f;
				SpoolUpTime = 0.1f;
				SpoolDownTime = 0.2f;
				ShutdownTime = 0.15f;
				Streaks = 2f;
				IgnitionCurve = Curve((0f, 0f), (0.05f, 1.4f), (0.2f, 0.4f), (0.5f, 0f));
				break;
			case DriveConePreset.Ion:
				Length = radius * 6f;
				IdleLength = 0.4f;
				IdleWidth = 0.9f;
				WidthCurve = Curve((0f, 1f), (1f, 1.2f));
				FlameColor = Flame((0f, 0.4f, 0.7f, 1f, 0.7f), (1f, 0.3f, 0.5f, 1f, 0f));
				FlameIntensity = 1.2f;
				CoreColor = new Color4(0.7f, 0.85f, 1f, 1f);
				CoreIntensity = 2.5f;
				CoreLength = 0.2f;
				CoreRadius = 0.8f;
				Falloff = 1f;
				IdleBrightness = 0.6f;
				DiamondCount = 0;
				FlowSpeed = 6f;
				Turbulence = 0.15f;
				TurbulenceScale = 1.2f;
				Flicker = 0.03f;
				FlickerRate = 30f;
				Pulse = 0f;
				Sway = 0f;
				IgnitionStretch = 0.1f;
				SpoolUpTime = 0.4f;
				SpoolDownTime = 0.6f;
				ShutdownTime = 0.4f;
				Streaks = 1f;
				IgnitionCurve = Curve((0f, 0f), (0.1f, 0.4f), (0.3f, 0f));
				NozzleGlow = 1f;
				break;
			case DriveConePreset.TorchDrive:
				// Short and violent: the gas streams out fast in thin streaks, the plume snaps to the throttle, roars
				// (fast flicker and pulse) and tapers like a cutting torch's flame.
				Length = radius * 9f;
				IdleLength = 0.25f;
				IdleWidth = 0.8f;
				WidthCurve = Curve((0f, 1f), (0.3f, 1.08f), (1f, 0.8f));
				FlameColor = Flame((0f, 0.45f, 0.75f, 1f, 1f), (0.2f, 0.16f, 0.45f, 1f, 0.9f), (0.65f, 0.05f, 0.24f, 1f, 0.45f), (1f, 0.03f, 0.12f, 0.85f, 0f));
				FlameIntensity = 1.5f;
				CoreColor = new Color4(0.75f, 0.9f, 1f, 1f);
				CoreIntensity = 3.5f;
				CoreLength = 0.25f;
				CoreRadius = 0.8f;
				Falloff = 0.8f;
				IdleBrightness = 0.45f;
				NozzleGlow = 1.2f;
				DiamondCount = 0;
				FlowSpeed = 70f;
				Turbulence = 0.5f;
				TurbulenceScale = 2.5f;
				Streaks = 3.5f;
				Flicker = 0.16f;
				FlickerRate = 34f;
				Pulse = 0.06f;
				PulseRate = 24f;
				Sway = 0.06f;
				SwayRate = 3f;
				SpoolUpTime = 0.05f;
				SpoolDownTime = 0.1f;
				ShutdownTime = 0.08f;
				IgnitionStretch = 0.45f;
				IgnitionCurve = Curve((0f, 0f), (0.03f, 2.2f), (0.12f, 0.5f), (0.3f, 0f));
				break;
		}
	}

	/// <summary>The plume's bounds in the transform's space now, with the ripples and the bend of its end.</summary>
	public void GetLocalBounds(out Vector3 min, out Vector3 max)
	{
		float radius = MathF.Max(WidestRadius(), CoreRadiusNow()) * (1f + 0.6f * Math.Clamp(Turbulence, 0f, 1f)) + sway.Length;
		min = new Vector3(-radius, -radius, 0f);
		max = new Vector3(radius, radius, length);
	}

	/// <summary>The radius of the plume at a share of its length (0 nozzle, 1 end) now, local units, without turbulence.</summary>
	public float RadiusAt(float share)
	{
		return MathF.Max(0f, WidthCurve?.Evaluate(Math.Clamp(share, 0f, 1f)) ?? 1f) * NozzleRadius * widthScale;
	}

	#endregion

	#region InternalMethods

	/// <summary>
	///     For the transparent pass: the shells to draw and this draw's shader values in <see cref="PropertyBlock" />; false
	///     when nothing shows.
	/// </summary>
	internal bool PrepareDraw(out Mesh shells)
	{
		shells = null;
		if (!IsVisible)
		{
			return false;
		}

		shells = Shells(RadialSegments, LengthSegments);
		WriteBlock();
		return true;
	}

	#endregion

	#region PrivateMethods

	private void WriteBlock()
	{
		float r0 = NozzleRadius;
		for (int i = 0; i < ProfileSamples; i++)
		{
			profile[i] = RadiusAt(i / (float)(ProfileSamples - 1));
		}

		Gradient gradient = FlameColor ?? new Gradient();
		float flameScale = FlameIntensity * brightness;
		for (int i = 0; i < FlameSamples; i++)
		{
			Color4 c = gradient.Evaluate(i / (float)(FlameSamples - 1));
			flame[i] = new Vector4(c.R * flameScale, c.G * flameScale, c.B * flameScale, c.A);
		}

		float coreScale = CoreIntensity * brightness;
		float diamondsShown = Smooth(DiamondThrottle.X, MathF.Max(DiamondThrottle.Y, DiamondThrottle.X + 1e-3f), level) * burn;
		float diamondScale = DiamondIntensity * brightness * diamondsShown;
		int diamonds = diamondScale > 1e-4f ? Math.Max(0, DiamondCount) : 0;
		// Diamonds sit closer at lower throttle (a lower pressure ratio), as the plume shortens.
		float spacing = MathF.Max(0.05f, DiamondSpacing) * r0 * Lerp(0.6f, 1f, level);
		float firstDiamond = DiamondOffset * spacing;
		// The core shell reaches as far as the core or the last diamond, whichever is further (never past the plume).
		float coreLength = MathF.Max(CoreLength * length, 1e-4f);
		float diamondsEnd = diamonds > 0 ? firstDiamond + (diamonds - 1 + 0.5f * DiamondLength) * spacing : 0f;
		float coreShell = MathF.Min(length, MathF.Max(coreLength, diamondsEnd));

		block.SetFloatArray("DriveProfile", profile);
		block.SetVectorArray("DriveFlame", flame);
		block.SetVector("DriveShape", new Vector4(r0, length, coreShell, flowTime * r0));
		block.SetVector("DriveBend", new Vector4(sway.X, sway.Y, MathF.Max(0.1f, Streaks), Seed()));
		block.SetVector("DriveCore", new Vector4(CoreColor.R * coreScale, CoreColor.G * coreScale, CoreColor.B * coreScale, coreLength));
		block.SetVector("DriveParams", new Vector4(CoreRadiusNow(), Falloff, Turbulence, MathF.Max(TurbulenceScale, 0.01f) * r0));
		block.SetVector("DriveDiamonds", new Vector4(diamonds, spacing, firstDiamond, DiamondLength));
		block.SetVector("DriveDiamondShape", new Vector4(1f / MathF.Max(DiamondRadius, 0.05f), DiamondFade, MathF.Max(0f, NozzleGlow) * brightness, 0f));
		block.SetVector("DriveDiamondColor", new Vector4(DiamondColor.R * diamondScale, DiamondColor.G * diamondScale, DiamondColor.B * diamondScale, 0f));
	}

	// The widest the plume is along its length now (local units).
	private float WidestRadius()
	{
		float widest = 0f;
		for (int i = 0; i < ProfileSamples; i++)
		{
			widest = MathF.Max(widest, RadiusAt(i / (float)(ProfileSamples - 1)));
		}

		return widest;
	}

	// The core's radius at the nozzle now (local units).
	private float CoreRadiusNow()
	{
		return MathF.Max(CoreRadius * NozzleRadius * widthScale, 1e-4f);
	}

	private float Seed()
	{
		if (seed < 0f)
		{
			ulong id = Id != 0 ? Id : (ulong)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
			seed = (id * 0x9E3779B97F4A7C15UL >> 40) / (float)(1UL << 24);
		}

		return seed;
	}

	/// <summary>
	///     The plume's two open shells, the flame (second UV x = 0) and the core (x = 1): rings of unit radius at the shares
	///     0..1 of the shell's length (z), normals pointing out. The vertex shader scales each ring to the radius there,
	///     stretches z to the shell's length, ripples and bends it.
	/// </summary>
	private static Mesh Shells(int radial, int along)
	{
		radial = Math.Clamp(radial, 6, 48);
		along = Math.Clamp(along, 2, 48);
		int key = radial * 1000 + along;
		lock (shellMeshes)
		{
			if (shellMeshes.TryGetValue(key, out Mesh cached))
			{
				return cached;
			}
		}

		const int DiscRings = 3;
		int ring = radial + 1; // the seam's vertex twice, so u runs 0..1 round it
		var vertices = new Vertex[2 * (along + 1) * ring + (DiscRings + 1) * ring];
		var indices = new uint[(2 * along + DiscRings) * radial * 6];
		int index = 0;
		for (int layer = 0; layer < 2; layer++)
		{
			int first = layer * (along + 1) * ring;
			for (int j = 0; j <= along; j++)
			{
				float z = j / (float)along;
				for (int i = 0; i < ring; i++)
				{
					float angle = i / (float)radial * MathF.Tau;
					var normal = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
					var tangent = new Vector3(-normal.Y, normal.X, 0f);
					vertices[first + j * ring + i] = new Vertex(new Vector3(normal.X, normal.Y, z), normal, new Vector2(i / (float)radial, z), tangent, Vector3.UnitZ, Color4.White, new Vector2(layer, 0f));
				}
			}

			for (int j = 0; j < along; j++)
			{
				for (int i = 0; i < radial; i++)
				{
					// Counter-clockwise seen from outside (the material draws both faces).
					uint a = (uint)(first + j * ring + i);
					uint b = a + 1;
					uint c = b + (uint)ring;
					uint d = a + (uint)ring;
					indices[index++] = a;
					indices[index++] = b;
					indices[index++] = c;
					indices[index++] = a;
					indices[index++] = c;
					indices[index++] = d;
				}
			}
		}

		// The nozzle's disc (second UV x = 2): rings of radius 0..1 at z 0, facing +Z.
		int disc = 2 * (along + 1) * ring;
		for (int k = 0; k <= DiscRings; k++)
		{
			float radius = k / (float)DiscRings;
			for (int i = 0; i < ring; i++)
			{
				float angle = i / (float)radial * MathF.Tau;
				var position = new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);
				vertices[disc + k * ring + i] = new Vertex(position, Vector3.UnitZ, new Vector2(i / (float)radial, radius), Vector3.UnitX, Vector3.UnitY, Color4.White, new Vector2(2f, 0f));
			}
		}

		for (int k = 0; k < DiscRings; k++)
		{
			for (int i = 0; i < radial; i++)
			{
				uint a = (uint)(disc + k * ring + i);
				uint b = a + 1;
				uint c = b + (uint)ring;
				uint d = a + (uint)ring;
				indices[index++] = a;
				indices[index++] = b;
				indices[index++] = c;
				indices[index++] = a;
				indices[index++] = c;
				indices[index++] = d;
			}
		}

		var mesh = new Mesh($"DriveCone Shells {radial}x{along}", vertices, indices);
		lock (shellMeshes)
		{
			shellMeshes[key] = mesh;
		}

		return mesh;
	}

	// Exponential approach: about two thirds of the way in `time` seconds (0: at once).
	private static float Follow(float current, float target, float time, float dt)
	{
		if (time <= 1e-5f)
		{
			return target;
		}

		float next = target + (current - target) * MathF.Exp(-dt / time);
		return MathF.Abs(next - target) < 1e-4f ? target : next;
	}

	private static float Lerp(float a, float b, float t)
	{
		return a + (b - a) * t;
	}

	private static float Smooth(float edge0, float edge1, float x)
	{
		float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
		return t * t * (3f - 2f * t);
	}

	// Smooth value noise in [-1, 1].
	private static float Noise(float x)
	{
		float cell = MathF.Floor(x);
		float f = x - cell;
		f = f * f * (3f - 2f * f);
		return Lerp(Hash((int)cell), Hash((int)cell + 1), f);
	}

	private static float Hash(int n)
	{
		unchecked
		{
			uint h = (uint)n * 0x27D4EB2Du;
			h ^= h >> 15;
			h *= 0x85EBCA6Bu;
			h ^= h >> 13;
			return (h & 0xFFFFFF) / (float)0x7FFFFF - 1f;
		}
	}

	private static AnimationCurve Curve(params (float Time, float Value)[] points)
	{
		var keys = new Keyframe[points.Length];
		for (int i = 0; i < points.Length; i++)
		{
			keys[i] = new Keyframe(points[i].Time, points[i].Value) { tangentMode = TangentMode.ClampedAuto };
		}

		var curve = new AnimationCurve(keys);
		curve.RecalculateTangents();
		return curve;
	}

	private static Gradient Flame(params (float Time, float R, float G, float B, float A)[] points)
	{
		var colors = new GradientColorKey[points.Length];
		var alphas = new GradientAlphaKey[points.Length];
		for (int i = 0; i < points.Length; i++)
		{
			colors[i] = new GradientColorKey(new Color4(points[i].R, points[i].G, points[i].B, 1f), points[i].Time);
			alphas[i] = new GradientAlphaKey(points[i].A, points[i].Time);
		}

		var gradient = new Gradient();
		gradient.SetKeys(colors, alphas);
		return gradient;
	}

	#endregion
}
