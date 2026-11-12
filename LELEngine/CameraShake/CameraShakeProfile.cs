using System;
using LELEngine;
using OpenTK.Mathematics;

/// <summary>
///     What a camera shake looks like (an asset; Create > Camera Shake Profile): layers of movement that add up. Five
///     are a camera operator's (<see cref="Sway" />, <see cref="Tremor" />, <see cref="Breathing" />, <see cref="Steps" />,
///     <see cref="Reframing" />), one is whatever the camera is fixed to (<see cref="Vibration" />), and the kick is the
///     push an impact gives at its start (<see cref="KickPosition" />, <see cref="KickRotation" />).
///     <para>
///         An amount of 1 of an operator's layer is what a careful operator with a camera of a few kilograms and a normal
///         lens does (measured hand shake: the view wanders about half a degree, at one or two degrees a second), so the
///         layers are mixed by eye, not in degrees: 0 none, 2 twice as much. <see cref="Rotation" /> and
///         <see cref="Position" /> scale the whole result per axis, <see cref="Strength" /> all of it.
///     </para>
///     <para>
///         A profile only describes the movement. <see cref="CameraShake" /> plays it: all the time
///         (<see cref="CameraShake.Play" />) or once, dying away (<see cref="CameraShake.Impact" />); how it fades in and
///         out is under Playing. <see cref="Load" /> fills a profile from a preset (<see cref="CameraShakePreset" />),
///         <see cref="Get" /> hands out the presets themselves for scripts that need no asset.
///     </para>
/// </summary>
[CreateAssetMenu(menuName = "Camera Shake Profile", fileName = "New Camera Shake")]
public sealed class CameraShakeProfile : ScriptableObject
{
	#region PublicFields

	[Header("Overall")]
	[Range(0f, 1f)]
	[Tooltip("How much of this profile there is: 0 none, 1 as it is set up below")]
	public float Strength = 1f;

	[Range(0.1f, 4f)]
	[Tooltip("Tempo of everything below: 2 moves twice as fast")]
	public float Speed = 1f;

	[Tooltip("Scale of the view's turning: x tilt (up and down), y pan (left and right), z roll")]
	public Vector3 Rotation = Vector3.One;

	[Tooltip("Scale of the camera's moving: x sideways, y up and down, z along the view. 0, 0, 0: the camera only turns, it never leaves its place")]
	public Vector3 Position = Vector3.One;

	[Range(0f, 1f)]
	[Tooltip("How heavy the camera is. 0: a phone, every tremble of the hands shows; 1: a heavy camera on the shoulder, it floats and its quick movements are gone. It smooths the operator's layers (not the vibration)")]
	public float Weight = 0.4f;

	[Tooltip("Metres behind the lens the camera turns about (the wrists, the shoulder): turning then also moves the lens, and near things shift against far ones as with a real camera")]
	public float Pivot = 0.25f;

	[Header("Sway")]
	[Range(0f, 3f)]
	[Tooltip("The slow float of a camera held by hand: the operator's balance and the framing drifting. 1: a careful operator")]
	public float Sway = 1f;

	[Range(0.05f, 3f)]
	[Tooltip("Swings per second of the slowest part of the sway")]
	public float SwayFrequency = 0.35f;

	[Range(0f, 1f)]
	[Tooltip("How much quick movement rides on the slow one. 0: a smooth float; 1: restless")]
	public float Roughness = 0.5f;

	[Header("Tremor")]
	[Range(0f, 3f)]
	[Tooltip("The fine tremble of the hands (it shows with a light camera or a long lens)")]
	public float Tremor = 1f;

	[Range(4f, 16f)]
	[Tooltip("Trembles per second (hands: 8 to 12)")]
	public float TremorFrequency = 9f;

	[Header("Breathing")]
	[Range(0f, 3f)]
	[Tooltip("The operator's breath lifting and lowering the camera")]
	public float Breathing = 1f;

	[Range(6f, 60f)]
	[Tooltip("Breaths per minute (at rest 12 to 18, after a run 30 and more)")]
	public float BreathRate = 15f;

	[Header("Footsteps")]
	[Range(0f, 3f)]
	[Tooltip("The operator walking: the camera bobs with every step and leans from foot to foot. 1: walking carefully")]
	public float Steps;

	[Range(60f, 220f)]
	[Tooltip("Steps per minute (walking 100 to 120, running 160 to 180)")]
	public float Cadence = 110f;

	[Header("Reframing")]
	[Range(0f, 3f)]
	[Tooltip("The operator correcting the framing: the view holds, then is brought somewhere else in one quick move (the documentary look)")]
	public float Reframing = 0.4f;

	[Range(0.05f, 3f)]
	[Tooltip("Corrections per second")]
	public float ReframingRate = 0.35f;

	[Header("Vibration")]
	[Range(0f, 3f)]
	[Tooltip("Shaking that comes from what the camera is fixed to or stands on: an engine, a vehicle, the ground. 1: the view jumps by about a degree")]
	public float Vibration;

	[Range(1f, 40f)]
	[Tooltip("Shakes per second")]
	public float VibrationFrequency = 13f;

	[Range(0f, 1f)]
	[Tooltip("0: an even hum; 1: rattling")]
	public float VibrationRoughness = 0.5f;

	[Header("Kick")]
	[Tooltip("Metres the camera is pushed when a shake of this profile starts (x to the right of the picture, y up, z forward); it swings back by itself. An impact that has a direction pushes along it, this far")]
	public Vector3 KickPosition;

	[Tooltip("Degrees the view is thrown at the same moment: x tilt (up), y pan (to the right), z roll (clockwise)")]
	public Vector3 KickRotation;

	[Tooltip("Degrees the field of view opens at the same moment (negative: closes)")]
	public float KickFieldOfView;

	[Range(0.5f, 12f)]
	[Tooltip("Swings per second of the kick coming back")]
	public float KickFrequency = 3.5f;

	[Range(0.1f, 1.5f)]
	[Tooltip("How quickly the kick settles. Low: it swings to and fro several times; 1 and above: back without swinging through")]
	public float KickDamping = 0.55f;

	[Header("Playing")]
	[Tooltip("Seconds a shake of this profile takes to come in")]
	public float Attack = 0.4f;

	[Tooltip("Seconds it takes to die away after it was stopped")]
	public float Release = 0.6f;

	[Tooltip("Seconds an impact of this profile lasts")]
	public float Duration = 0.6f;

	[Range(0.5f, 4f)]
	[Tooltip("How an impact dies away. 1: evenly; 2 and above: most of it goes early and a tail lingers")]
	public float Decay = 2f;

	[Tooltip("Runs on real time: it goes on while the game is paused or slowed (Time.timeScale)")]
	public bool IgnoreTimeScale;

	/// <summary>The profile has a kick (<see cref="KickPosition" />, <see cref="KickRotation" /> or <see cref="KickFieldOfView" />).</summary>
	public bool HasKick => KickPosition != Vector3.Zero || KickRotation != Vector3.Zero || KickFieldOfView != 0f;

	/// <summary>The profile has movement that goes on (any layer but the kick).</summary>
	public bool HasMovement => Sway > 0f || Tremor > 0f || Breathing > 0f || Steps > 0f || Reframing > 0f || Vibration > 0f;

	/// <summary>Seconds the kick takes to settle.</summary>
	public float KickLength => HasKick ? CameraShakeNoise.KickLength(KickFrequency, KickDamping) : 0f;

	/// <summary>Movement above this many swings a second is smoothed away by the camera's <see cref="Weight" />.</summary>
	public float Cutoff => 14f * MathF.Pow(1.3f / 14f, CameraShakeNoise.Clamp01(Weight));

	#endregion

	#region PrivateFields

	private const int SwayOctaves = 5;
	private const int VibrationOctaves = 3;
	private const float ToRadians = MathF.PI / 180f;

	// What 1 of each layer is (degrees: tilt, pan, roll; metres: right, up, forward). The noise layers as RMS (their
	// peaks are about three times that), before the camera's weight smooths them.
	private static readonly Vector3 SwayAngles = new Vector3(0.42f, 0.5f, 0.22f);
	private static readonly Vector3 SwayShift = new Vector3(0.012f, 0.008f, 0.009f);
	private static readonly Vector3 TremorAngles = new Vector3(0.09f, 0.08f, 0.04f);
	private static readonly Vector3 TremorShift = new Vector3(0.0015f, 0.0015f, 0.001f);
	private static readonly Vector3 BreathAngles = new Vector3(0.1f, 0.012f, 0.02f);
	private static readonly Vector3 BreathShift = new Vector3(0f, 0.004f, 0.0015f);
	private static readonly Vector3 StepAngles = new Vector3(0.3f, 0.3f, 0.35f);
	private static readonly Vector3 StepShift = new Vector3(0.014f, 0.018f, 0.006f);
	private static readonly Vector3 ReframeAngles = new Vector3(0.4f, 0.55f, 0.06f);
	private static readonly Vector3 VibrationAngles = new Vector3(0.35f, 0.35f, 0.35f);
	private static readonly Vector3 VibrationShift = new Vector3(0.008f, 0.008f, 0.005f);

	private static readonly CameraShakeProfile[] presets = new CameraShakeProfile[Enum.GetValues(typeof(CameraShakePreset)).Length];

	#endregion

	#region LifecycleMethods

	/// <summary>A new asset starts as the plain handheld camera.</summary>
	public override void Reset()
	{
		Load(CameraShakePreset.Handheld);
	}

	#endregion

	#region PublicMethods

	/// <summary>
	///     The movement that goes on (every layer but the kick) at <paramref name="time" /> seconds, as a shake of full
	///     strength shows it. A pure function: the same time and <paramref name="seed" /> give the same pose, another seed
	///     another run of the same character.
	/// </summary>
	public CameraShakePose Evaluate(double time, int seed = 0)
	{
		return Movement(time * Speed, seed) * CameraShakeNoise.Clamp01(Strength);
	}

	/// <summary>
	///     The kick <paramref name="age" /> seconds after the shake started. <paramref name="direction" />: where the
	///     impact pushes, in the camera's picture (x right, y up, z forward; any length but zero), or zero for the
	///     profile's own (<see cref="KickPosition" />, <see cref="KickRotation" />).
	/// </summary>
	public CameraShakePose EvaluateKick(float age, Vector3 direction = default)
	{
		return Kick(age, direction) * CameraShakeNoise.Clamp01(Strength);
	}

	/// <summary>The shared profile of a preset, for scripts that need no asset. Shared by everything that asks: do not change it (<see cref="Create" /> gives a copy to change).</summary>
	public static CameraShakeProfile Get(CameraShakePreset preset)
	{
		int index = (int)preset;
		if (index < 0 || index >= presets.Length)
		{
			throw new ArgumentOutOfRangeException(nameof(preset));
		}

		CameraShakeProfile profile = presets[index];
		if (profile is null || profile.IsDestroyed)
		{
			profile = Create(preset);
			presets[index] = profile;
		}

		return profile;
	}

	/// <summary>A new profile (not an asset) set up as a preset.</summary>
	public static CameraShakeProfile Create(CameraShakePreset preset)
	{
		CameraShakeProfile profile = CreateInstance<CameraShakeProfile>();
		profile.name = preset.ToString();
		profile.Load(preset);
		return profile;
	}

	/// <summary>Sets every value of the profile to a preset's.</summary>
	public void Load(CameraShakePreset preset)
	{
		Clear();
		switch (preset)
		{
			case CameraShakePreset.HandheldSteady:
				Operator(sway: 0.45f, swayFrequency: 0.28f, roughness: 0.35f, tremor: 0.5f, breathing: 0.9f, breathRate: 14f, reframing: 0.15f, reframingRate: 0.2f);
				Weight = 0.55f;
				Pivot = 0.2f;
				break;
			case CameraShakePreset.Handheld:
				Operator(sway: 1f, swayFrequency: 0.35f, roughness: 0.5f, tremor: 1f, breathing: 1f, breathRate: 15f, reframing: 0.4f, reframingRate: 0.35f);
				break;
			case CameraShakePreset.HandheldTelephoto:
				// The same hands through a long lens: what they do to the view is large in the picture, and the
				// operator keeps correcting. Nothing near enough for the camera's moving to show.
				Operator(sway: 1f, swayFrequency: 0.4f, roughness: 0.55f, tremor: 1.6f, breathing: 1.3f, breathRate: 15f, reframing: 0.8f, reframingRate: 0.5f);
				Rotation = new Vector3(2.6f, 2.6f, 1.4f);
				Position = new Vector3(0.3f, 0.3f, 0.3f);
				Weight = 0.45f;
				break;
			case CameraShakePreset.HandheldWide:
				// A wide lens close to its subject: turning shows little, moving shows much.
				Operator(sway: 1.1f, swayFrequency: 0.3f, roughness: 0.45f, tremor: 0.6f, breathing: 1f, breathRate: 15f, reframing: 0.3f, reframingRate: 0.3f);
				Rotation = new Vector3(0.7f, 0.7f, 0.9f);
				Position = new Vector3(2.2f, 2.2f, 2.2f);
				Weight = 0.35f;
				Pivot = 0.35f;
				break;
			case CameraShakePreset.ShoulderRig:
				Operator(sway: 0.8f, swayFrequency: 0.24f, roughness: 0.25f, tremor: 0.25f, breathing: 1.6f, breathRate: 13f, reframing: 0.3f, reframingRate: 0.25f);
				TremorFrequency = 7f;
				Rotation = new Vector3(1f, 1f, 1.3f);
				Weight = 0.8f;
				Pivot = 0.15f;
				Attack = 0.6f;
				break;
			case CameraShakePreset.Documentary:
				Operator(sway: 1.5f, swayFrequency: 0.42f, roughness: 0.6f, tremor: 1.2f, breathing: 1f, breathRate: 17f, reframing: 1.2f, reframingRate: 0.7f);
				Weight = 0.3f;
				Pivot = 0.3f;
				break;
			case CameraShakePreset.Walking:
				Operator(sway: 1f, swayFrequency: 0.4f, roughness: 0.5f, tremor: 0.8f, breathing: 0.8f, breathRate: 20f, reframing: 0.6f, reframingRate: 0.5f);
				Steps = 1f;
				Cadence = 108f;
				break;
			case CameraShakePreset.Running:
				Operator(sway: 1.6f, swayFrequency: 0.55f, roughness: 0.65f, tremor: 1.2f, breathing: 0.9f, breathRate: 34f, reframing: 1f, reframingRate: 0.9f);
				Steps = 2.4f;
				Cadence = 168f;
				Weight = 0.25f;
				Attack = 0.25f;
				break;
			case CameraShakePreset.Shaky:
				Operator(sway: 2.4f, swayFrequency: 0.7f, roughness: 0.75f, tremor: 2f, breathing: 0.6f, breathRate: 24f, reframing: 2f, reframingRate: 1.6f);
				TremorFrequency = 10f;
				Steps = 0.6f;
				Cadence = 150f;
				Weight = 0.15f;
				Attack = 0.2f;
				break;
			case CameraShakePreset.Phone:
				Operator(sway: 0.9f, swayFrequency: 0.45f, roughness: 0.65f, tremor: 2.2f, breathing: 0.5f, breathRate: 15f, reframing: 0.7f, reframingRate: 0.6f);
				TremorFrequency = 10f;
				Position = new Vector3(1.2f, 1.2f, 1.2f);
				Weight = 0.05f;
				Pivot = 0.45f;
				break;
			case CameraShakePreset.Aerial:
				// A camera on a helicopter or a camera ship: a slow float, the airframe's hum under it.
				Operator(sway: 0.7f, swayFrequency: 0.2f, roughness: 0.3f, tremor: 0f, breathing: 0f, breathRate: 15f, reframing: 0.25f, reframingRate: 0.2f);
				Vibration = 0.12f;
				VibrationFrequency = 11f;
				Position = new Vector3(1.5f, 1.5f, 1.5f);
				Weight = 0.6f;
				Pivot = 0f;
				Attack = 0.8f;
				break;
			case CameraShakePreset.Vehicle:
				// Bolted to a car: the engine and the road, and the body rocking on its springs.
				Operator(sway: 0.25f, swayFrequency: 0.8f, roughness: 0.7f, tremor: 0f, breathing: 0f, breathRate: 15f, reframing: 0f, reframingRate: 0.35f);
				Vibration = 0.5f;
				VibrationFrequency = 14f;
				VibrationRoughness = 0.6f;
				Weight = 0.1f;
				Pivot = 0f;
				Attack = 0.2f;
				Release = 0.4f;
				break;
			case CameraShakePreset.Engine:
				Vibration = 0.25f;
				VibrationFrequency = 22f;
				VibrationRoughness = 0.4f;
				Attack = 0.2f;
				Release = 0.3f;
				break;
			case CameraShakePreset.Rumble:
				// The ground itself: slow, heavy, moving the camera more than turning it.
				Vibration = 1f;
				VibrationFrequency = 5f;
				VibrationRoughness = 0.45f;
				Position = new Vector3(2f, 2f, 1.5f);
				Attack = 0.3f;
				Release = 0.8f;
				Duration = 2f;
				break;
			case CameraShakePreset.Impact:
				Vibration = 1f;
				VibrationFrequency = 16f;
				VibrationRoughness = 0.6f;
				KickPosition = new Vector3(0f, -0.012f, -0.02f);
				KickRotation = new Vector3(-0.5f, 0f, 0.3f);
				KickFrequency = 5f;
				KickDamping = 0.5f;
				Attack = 0f;
				Release = 0.15f;
				Duration = 0.5f;
				break;
			case CameraShakePreset.Explosion:
				// A blow, then the ground rolling under the camera for a while.
				Vibration = 1.6f;
				VibrationFrequency = 7f;
				VibrationRoughness = 0.55f;
				Position = new Vector3(1.5f, 1.5f, 1.5f);
				KickPosition = new Vector3(0f, 0.02f, -0.08f);
				KickRotation = new Vector3(1.6f, 0f, -0.8f);
				KickFieldOfView = 2.5f;
				KickFrequency = 2.2f;
				KickDamping = 0.5f;
				Attack = 0f;
				Release = 0.3f;
				Duration = 1.6f;
				Decay = 2.2f;
				break;
			case CameraShakePreset.Recoil:
				// A shot: back and up, and back on the target at once.
				Vibration = 0.25f;
				VibrationFrequency = 20f;
				KickPosition = new Vector3(0f, 0.004f, -0.03f);
				KickRotation = new Vector3(1.6f, 0.2f, 0f);
				KickFrequency = 5f;
				KickDamping = 0.75f;
				Attack = 0f;
				Release = 0.1f;
				Duration = 0.25f;
				break;
			case CameraShakePreset.Landing:
				// Coming down hard: the camera sinks and nods, and comes back up.
				Vibration = 0.5f;
				VibrationFrequency = 9f;
				KickPosition = new Vector3(0f, -0.1f, 0f);
				KickRotation = new Vector3(-1.2f, 0f, 0f);
				KickFrequency = 2.8f;
				KickDamping = 0.6f;
				Attack = 0f;
				Release = 0.2f;
				Duration = 0.6f;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(preset));
		}
	}

	#endregion

	#region InternalMethods

	/// <summary>The movement that goes on at a place on the shake's own line (seconds, the profile's <see cref="Speed" /> already in), at full strength.</summary>
	internal CameraShakePose Movement(double time, int seed)
	{
		Vector3 rotation = Vector3.Zero;
		Vector3 position = Vector3.Zero;
		float cutoff = Cutoff;

		if (Sway > 0f)
		{
			float frequency = MathF.Max(SwayFrequency, 0.01f);
			// A spectrum falling as 1 / f cubed (a smooth float) to 1 / f (restless): hands are in between.
			float gain = MathF.Pow(2f, -(3f - 2f * CameraShakeNoise.Clamp01(Roughness)) * 0.5f);
			double cycles = time * frequency;
			// The hold comes and goes: steadier moments, shakier ones.
			float amount = Sway * MathF.Max(0.35f, 1f + 0.25f * CameraShakeNoise.Signal(time * 0.07, Channel(seed, 0, 9)));
			float turning = cutoff / frequency;
			// The body carries the camera's place: it moves more slowly than the hands turn the view.
			float moving = turning * 0.5f;
			rotation.X += amount * SwayAngles.X * CameraShakeNoise.Fractal(cycles, Channel(seed, 0, 0), SwayOctaves, gain, turning);
			rotation.Y += amount * SwayAngles.Y * CameraShakeNoise.Fractal(cycles, Channel(seed, 0, 1), SwayOctaves, gain, turning);
			rotation.Z += amount * SwayAngles.Z * CameraShakeNoise.Fractal(cycles, Channel(seed, 0, 2), SwayOctaves, gain, turning);
			position.X += amount * SwayShift.X * CameraShakeNoise.Fractal(cycles, Channel(seed, 0, 3), SwayOctaves, gain, moving);
			position.Y += amount * SwayShift.Y * CameraShakeNoise.Fractal(cycles, Channel(seed, 0, 4), SwayOctaves, gain, moving);
			position.Z += amount * SwayShift.Z * CameraShakeNoise.Fractal(cycles, Channel(seed, 0, 5), SwayOctaves, gain, moving);
		}

		if (Tremor > 0f)
		{
			float frequency = MathF.Max(TremorFrequency, 0.1f);
			double cycles = time * frequency;
			// It comes in spells, and a heavy camera does not follow it.
			float amount = Tremor * CameraShakeNoise.LowPass(frequency / cutoff) * MathF.Max(0f, 1f + 0.45f * CameraShakeNoise.Signal(time * 0.31, Channel(seed, 1, 9)));
			rotation.X += amount * TremorAngles.X * CameraShakeNoise.Signal(cycles, Channel(seed, 1, 0));
			rotation.Y += amount * TremorAngles.Y * CameraShakeNoise.Signal(cycles, Channel(seed, 1, 1));
			rotation.Z += amount * TremorAngles.Z * CameraShakeNoise.Signal(cycles, Channel(seed, 1, 2));
			position.X += amount * TremorShift.X * CameraShakeNoise.Signal(cycles, Channel(seed, 1, 3));
			position.Y += amount * TremorShift.Y * CameraShakeNoise.Signal(cycles, Channel(seed, 1, 4));
			position.Z += amount * TremorShift.Z * CameraShakeNoise.Signal(cycles, Channel(seed, 1, 5));
		}

		if (Breathing > 0f)
		{
			float rate = MathF.Max(BreathRate, 1f) / 60f;
			// Never quite regular, in pace or in depth.
			double breaths = time * rate + 0.12 * CameraShakeNoise.Signal(time * rate * 0.21, Channel(seed, 2, 8));
			float phase = (float)((breaths - Math.Floor(breaths)) * Math.PI * 2.0);
			float depth = Breathing * MathF.Max(0.4f, 1f + 0.2f * CameraShakeNoise.Signal(time * rate * 0.13, Channel(seed, 2, 9)));
			// Breathing in is quicker than breathing out.
			float wave = (MathF.Sin(phase) + 0.22f * MathF.Sin(2f * phase + 0.9f)) * 0.9f;
			rotation.X += depth * BreathAngles.X * wave;
			rotation.Y += depth * BreathAngles.Y * MathF.Sin(phase + 2.1f);
			rotation.Z += depth * BreathAngles.Z * MathF.Sin(phase + 1.3f);
			position.Y += depth * BreathShift.Y * MathF.Sin(phase - 0.3f);
			position.Z += depth * BreathShift.Z * MathF.Sin(phase + 0.6f);
		}

		if (Steps > 0f)
		{
			float pace = MathF.Max(Cadence, 1f) / 60f;
			double steps = time * pace + 0.05 * CameraShakeNoise.Signal(time * pace * 0.17, Channel(seed, 3, 8));
			float step = (float)((steps - Math.Floor(steps)) * Math.PI * 2.0);
			double strides = steps * 0.5;
			float stride = (float)((strides - Math.Floor(strides)) * Math.PI * 2.0);
			float amount = Steps * MathF.Max(0.4f, 1f + 0.18f * CameraShakeNoise.Signal(time * pace * 0.5, Channel(seed, 3, 9)));
			// The foot coming down is sharper than the body rising: overtones of the step, as far as the camera's
			// weight lets them through.
			float second = 0.38f * CameraShakeNoise.LowPass(2f * pace / cutoff);
			float third = 0.17f * CameraShakeNoise.LowPass(3f * pace / cutoff);
			float fourth = 0.08f * CameraShakeNoise.LowPass(4f * pace / cutoff);
			float bob = MathF.Cos(step) + second * MathF.Cos(2f * step + 0.6f) + third * MathF.Cos(3f * step + 1.4f) + fourth * MathF.Cos(4f * step + 2f);
			position.Y += amount * StepShift.Y * bob;
			// From foot to foot: once every two steps.
			position.X += amount * StepShift.X * MathF.Sin(stride);
			position.Z += amount * StepShift.Z * MathF.Sin(step + 1.1f);
			rotation.X += amount * StepAngles.X * (MathF.Cos(step + 0.8f) + second * 0.8f * MathF.Cos(2f * step + 1.7f));
			rotation.Y += amount * StepAngles.Y * MathF.Sin(stride + 1.9f);
			rotation.Z += amount * StepAngles.Z * MathF.Sin(stride + 0.5f);
		}

		if (Reframing > 0f)
		{
			float rate = MathF.Max(ReframingRate, 0.01f);
			// A heavier camera is brought round more slowly.
			float move = MathF.Min(0.28f, (0.16f + 0.4f * CameraShakeNoise.Clamp01(Weight)) * rate);
			double moves = time * rate;
			int timing = Channel(seed, 4, 9);
			rotation.X += Reframing * ReframeAngles.X * CameraShakeNoise.Holds(moves, timing, Channel(seed, 4, 0), move);
			rotation.Y += Reframing * ReframeAngles.Y * CameraShakeNoise.Holds(moves, timing, Channel(seed, 4, 1), move);
			rotation.Z += Reframing * ReframeAngles.Z * CameraShakeNoise.Holds(moves, timing, Channel(seed, 4, 2), move);
		}

		// The operator turns the camera about a point behind the lens: the lens moves with the turn.
		if (Pivot != 0f)
		{
			position.X += Pivot * MathF.Sin(rotation.Y * ToRadians);
			position.Y += Pivot * MathF.Sin(rotation.X * ToRadians);
		}

		if (Vibration > 0f)
		{
			float frequency = MathF.Max(VibrationFrequency, 0.1f);
			float gain = MathF.Pow(2f, -(3f - 2f * CameraShakeNoise.Clamp01(VibrationRoughness)) * 0.5f);
			double cycles = time * frequency;
			rotation.X += Vibration * VibrationAngles.X * CameraShakeNoise.Fractal(cycles, Channel(seed, 5, 0), VibrationOctaves, gain, float.PositiveInfinity);
			rotation.Y += Vibration * VibrationAngles.Y * CameraShakeNoise.Fractal(cycles, Channel(seed, 5, 1), VibrationOctaves, gain, float.PositiveInfinity);
			rotation.Z += Vibration * VibrationAngles.Z * CameraShakeNoise.Fractal(cycles, Channel(seed, 5, 2), VibrationOctaves, gain, float.PositiveInfinity);
			position.X += Vibration * VibrationShift.X * CameraShakeNoise.Fractal(cycles, Channel(seed, 5, 3), VibrationOctaves, gain, float.PositiveInfinity);
			position.Y += Vibration * VibrationShift.Y * CameraShakeNoise.Fractal(cycles, Channel(seed, 5, 4), VibrationOctaves, gain, float.PositiveInfinity);
			position.Z += Vibration * VibrationShift.Z * CameraShakeNoise.Fractal(cycles, Channel(seed, 5, 5), VibrationOctaves, gain, float.PositiveInfinity);
		}

		return new CameraShakePose { Rotation = rotation * Rotation, Position = position * Position };
	}

	/// <summary>The kick at an age (seconds), at full strength; a <paramref name="direction" /> of zero is the profile's own.</summary>
	internal CameraShakePose Kick(float age, Vector3 direction)
	{
		if (age <= 0f || !HasKick)
		{
			return default;
		}

		Vector3 push = KickPosition;
		Vector3 thrown = KickRotation;
		if (direction != Vector3.Zero)
		{
			// Along the impact's own direction, as hard as the profile kicks: the view is thrown the way the camera is
			// pushed (and up a little when it is pushed back), leaning away from the side it came from.
			direction.Normalize();
			push = direction * KickPosition.Length;
			var way = new Vector3(direction.Y - 0.35f * direction.Z, direction.X, -0.5f * direction.X);
			if (way.LengthSquared > 1f)
			{
				way.Normalize();
			}

			thrown = way * KickRotation.Length;
		}

		float frequency = MathF.Max(KickFrequency, 0.05f);
		float pushed = CameraShakeNoise.Kick(age, frequency, KickDamping);
		// The view comes back a little quicker than the camera's place, so the two do not move as one; the field
		// of view opens and closes once, it never swings.
		float turned = CameraShakeNoise.Kick(age, frequency * 1.18f, KickDamping);
		float opened = CameraShakeNoise.Kick(age, frequency, MathF.Max(KickDamping, 1f));
		return new CameraShakePose { Position = push * Position * pushed, Rotation = thrown * Rotation * turned, FieldOfView = KickFieldOfView * opened };
	}

	#endregion

	#region PrivateMethods

	// Every layer off, everything else as a new profile has it.
	private void Clear()
	{
		Strength = 1f;
		Speed = 1f;
		Rotation = Vector3.One;
		Position = Vector3.One;
		Weight = 0.4f;
		Pivot = 0.25f;
		Sway = 0f;
		SwayFrequency = 0.35f;
		Roughness = 0.5f;
		Tremor = 0f;
		TremorFrequency = 9f;
		Breathing = 0f;
		BreathRate = 15f;
		Steps = 0f;
		Cadence = 110f;
		Reframing = 0f;
		ReframingRate = 0.35f;
		Vibration = 0f;
		VibrationFrequency = 13f;
		VibrationRoughness = 0.5f;
		KickPosition = Vector3.Zero;
		KickRotation = Vector3.Zero;
		KickFieldOfView = 0f;
		KickFrequency = 3.5f;
		KickDamping = 0.55f;
		Attack = 0.4f;
		Release = 0.6f;
		Duration = 0.6f;
		Decay = 2f;
		IgnoreTimeScale = false;
	}

	private void Operator(float sway, float swayFrequency, float roughness, float tremor, float breathing, float breathRate, float reframing, float reframingRate)
	{
		Sway = sway;
		SwayFrequency = swayFrequency;
		Roughness = roughness;
		Tremor = tremor;
		Breathing = breathing;
		BreathRate = breathRate;
		Reframing = reframing;
		ReframingRate = reframingRate;
	}

	// The seed of one signal: a layer's channel (0..5: tilt, pan, roll, right, up, forward; 8, 9: the layer's own).
	private static int Channel(int seed, int layer, int channel)
	{
		return unchecked(seed * 486187739 + layer * 1009 + channel * 9176 + 17);
	}

	#endregion
}

/// <summary>
///     The built-in looks of a camera shake (<see cref="CameraShakeProfile.Load" />, <see cref="CameraShakeProfile.Get" />).
///     The first ten are a camera in an operator's hands, after how handheld film looks; then cameras fixed to something
///     that shakes; the last four are impacts (played once: <see cref="CameraShake.Impact" />).
/// </summary>
public enum CameraShakePreset
{
	/// <summary>An operator standing braced: a slight float and the breath. A dialogue scene shot by hand.</summary>
	HandheldSteady,

	/// <summary>The plain handheld camera, a normal lens: it floats, breathes, and is corrected now and then.</summary>
	Handheld,

	/// <summary>A long lens by hand: the same hands, large in the picture, with the operator correcting all the time.</summary>
	HandheldTelephoto,

	/// <summary>A wide lens close to its subject: the view turns little, the camera's moving shows.</summary>
	HandheldWide,

	/// <summary>A heavy camera on the shoulder: slow and smooth, rising and falling with the breath, no tremble.</summary>
	ShoulderRig,

	/// <summary>Run and gun: a light camera following what happens, restless, reframing often.</summary>
	Documentary,

	/// <summary>The operator walking with the camera.</summary>
	Walking,

	/// <summary>The operator running with it: hard steps, out of breath.</summary>
	Running,

	/// <summary>The action film's shaky camera: nervous, never still.</summary>
	Shaky,

	/// <summary>A phone held out at arm's length: light, every tremble of the hand in the picture.</summary>
	Phone,

	/// <summary>A camera on a helicopter or a camera ship: a slow float over the airframe's hum.</summary>
	Aerial,

	/// <summary>A camera bolted to a car: the engine, the road, the body rocking.</summary>
	Vehicle,

	/// <summary>An engine's even hum.</summary>
	Engine,

	/// <summary>The ground shaking: slow and heavy (an earthquake, something huge going by).</summary>
	Rumble,

	/// <summary>A hit: a short hard rattle and a small jolt.</summary>
	Impact,

	/// <summary>An explosion: a blow that throws the camera, then the ground rolling under it.</summary>
	Explosion,

	/// <summary>A shot's recoil: back and up, and back on the target at once.</summary>
	Recoil,

	/// <summary>Coming down hard: the camera sinks, nods and comes back up.</summary>
	Landing
}
