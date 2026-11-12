using System;
using System.Collections.Generic;
using LELEngine;
using OpenTK.Mathematics;

/// <summary>One profile that plays all the time on a <see cref="CameraShake" /> (<see cref="CameraShake.Profiles" />), and how much of it.</summary>
[Serializable]
public sealed class CameraShakeSlot
{
	#region PublicFields

	[InlineEditor]
	[Tooltip("The shake that plays (another profile fades over to it)")]
	public CameraShakeProfile Profile;

	[Range(0f, 1f)]
	[Tooltip("How much of it: 0 none, 1 the profile as it is")]
	public float Strength = 1f;

	/// <summary>The shake playing for this entry (null while the component is off or the entry has no profile).</summary>
	public CameraShakeInstance Instance => instance;

	#endregion

	#region InternalFields

	[NonSerialized] internal CameraShakeInstance instance;
	[NonSerialized] internal CameraShakeProfile playing;

	#endregion
}

/// <summary>
///     Shakes the object it is on: a camera, or an object a camera (and anything else that should shake with it: a
///     cockpit, a weapon) hangs under. Every frame, in LateUpdate, the shakes playing on it are added up and written to
///     the object's transform (its local position and rotation) and to the field of view of its camera. The transform
///     is all there is to it: scripts, children and the renderer see the shaken object as they see any other.
///     <list type="bullet">
///         <item>
///             What plays: the <see cref="Profiles" /> list, all the time (a handheld camera, an engine's hum); shakes
///             started by scripts: <see cref="Impact" /> once, dying away, and <see cref="Play" /> until
///             <see cref="Stop" /> (constant: keep the <see cref="CameraShakeInstance" /> and set its strength as the
///             game goes); and the shakes of the world when <see cref="Listen" /> is on (<see cref="CameraShakeSource" />,
///             <see cref="ImpactAt" />, <see cref="PlayAt" />, <see cref="ImpactEverywhere" />,
///             <see cref="PlayEverywhere" />: felt by every listening camera, by its distance).
///         </item>
///         <item>
///             Where the object stands without the shake (its rest) is whatever its transform holds that this
///             component did not write: the pose it had when the shaking began, and any pose something else gives it
///             later. So what steers the camera should move a parent of this object (then the order of the scripts does
///             not matter), or set this object's pose anew every frame before this component's LateUpdate. Nothing
///             shaking: the transform is not touched.
///         </item>
///         <item>
///             What a profile's shake looks like is the profile's (<see cref="CameraShakeProfile" />); here are the
///             camera's own controls: <see cref="Strength" /> of everything, the share of turning, moving and field of
///             view, the limits the sum bends towards (many shakes at once never throw the camera about), and whether
///             the shake keeps its size in the picture when the lens zooms (<see cref="LensCompensation" />).
///             <see cref="GlobalStrength" /> is the player's own setting, for every camera.
///         </item>
///     </list>
///     Runs while the game plays (not in edit mode). <see cref="Main" /> is the main camera's.
/// </summary>
[InspectorColor(InspectorColorKind.Camera)]
public sealed class CameraShake : Behaviour
{
	#region PublicFields

	[Range(0f, 2f)]
	[Tooltip("How much of every shake this camera shows: 0 none, 1 as they are")]
	public float Strength = 1f;

	[Tooltip("Shakes that play all the time while the component is on, each with its own strength: a handheld camera, an engine's hum")]
	public List<CameraShakeSlot> Profiles = new List<CameraShakeSlot>();

	[Tooltip("The camera also feels the shakes of the world: Camera Shake Sources and what scripts start with ImpactAt, PlayAt, ImpactEverywhere, PlayEverywhere")]
	public bool Listen = true;

	[Tooltip("Where the world's shakes are felt from (how far they are, when they arrive, which way they push): a camera high above the player, or far behind it, feels them where the player is. Empty: where the camera is")]
	public Transform Listener;

	[Header("Axes")]
	[Range(0f, 2f)]
	[Tooltip("Share of the shakes' turning of the view")]
	public float RotationAmount = 1f;

	[Range(0f, 2f)]
	[Tooltip("Share of the shakes' moving of the camera (0: the camera only turns)")]
	public float PositionAmount = 1f;

	[Range(0f, 2f)]
	[Tooltip("Share of the shakes' opening and closing of the field of view")]
	public float FieldOfViewAmount = 1f;

	[Header("Limits")]
	[Tooltip("Degrees the view is never turned past, however many shakes add up (they bend towards it, no hard stop); 0: no limit")]
	public float MaxAngle = 10f;

	[Tooltip("Metres the camera is never moved past; 0: no limit")]
	public float MaxOffset = 1f;

	[Tooltip("Degrees the field of view is never changed past; 0: no limit")]
	public float MaxFieldOfView = 15f;

	[Header("Lens")]
	[Tooltip("The camera whose field of view the shakes change and the lens compensation reads; found on this object or below it when empty")]
	public Camera Camera;

	[Range(0f, 1f)]
	[Tooltip("0: the angles are what they are, so a long lens shakes more in the picture, as in life. 1: the shake stays the same size in the picture at every field of view (as large as at the Lens Reference)")]
	public float LensCompensation;

	[Tooltip("The vertical field of view (degrees) the shakes are sized for: where Lens Compensation changes nothing")]
	public float LensReference = 50f;

	[Header("Random")]
	[Tooltip("Which run of the random movement this camera gets; 0: its own")]
	public int Seed;

	/// <summary>What the shakes add up to on this object now: what the last LateUpdate wrote on top of its rest pose.</summary>
	public CameraShakePose Pose { get; private set; }

	/// <summary>How hard the camera is being shaken, 0..1: the shakes' strengths together, not their swings (for a rumble in the pad, a sound).</summary>
	public float Intensity { get; private set; }

	/// <summary>The shakes playing on this camera alone (the <see cref="Profiles" /> list's and those scripts started; not the world's).</summary>
	public IReadOnlyList<CameraShakeInstance> Playing => instances;

	/// <summary>The player's own setting for every camera (0 off, 1 full): an options menu's "camera shake" slider.</summary>
	public static float GlobalStrength
	{
		get => globalStrength;
		set => globalStrength = MathF.Max(0f, value);
	}

	/// <summary>The enabled camera shakes of the running game.</summary>
	public static IReadOnlyList<CameraShake> Active => active;

	/// <summary>The shakes of the world playing now (<see cref="PlayAt" />, <see cref="ImpactAt" />, <see cref="CameraShakeSource" />, ...).</summary>
	public static IReadOnlyList<CameraShakeInstance> World => world;

	/// <summary>The main camera's shake (on <see cref="global::Camera.main" /> or above it), else the first enabled one; null when there is none.</summary>
	public static CameraShake Main
	{
		get
		{
			Camera camera = global::Camera.main;
			CameraShake shake = camera != null && !camera.dead ? camera.GetComponentInParent<CameraShake>() : null;
			if (shake != null && shake.registered)
			{
				return shake;
			}

			return active.Count > 0 ? active[0] : null;
		}
	}

	#endregion

	#region PrivateFields

	// A frame longer than this moves the shakes on by this much: a hitch does not skip an impact.
	private const float MaxStep = 0.1f;
	private const int MaxPlaying = 48;
	private const int MaxWorld = 96;
	private const float ToRadians = MathF.PI / 180f;

	private static readonly List<CameraShake> active = new List<CameraShake>();
	private static readonly List<CameraShakeInstance> world = new List<CameraShakeInstance>();
	private static long worldFrame = -1;
	private static int worldStarted;
	private static float globalStrength = 1f;

	private readonly List<CameraShakeInstance> instances = new List<CameraShakeInstance>();
	private bool registered;
	private int seed;
	private int started;

	// The pose without the shake, and what was written over it last (to tell somebody else's change from ours).
	private bool written;
	private Vector3 restPosition;
	private Quaternion restRotation = Quaternion.Identity;
	private Vector3 writtenPosition;
	private Quaternion writtenRotation = Quaternion.Identity;
	private bool fieldWritten;
	private float restFieldOfView;
	private float writtenFieldOfView;

	#endregion

	#region UnityMethods

	public override void OnEnable()
	{
		if (!active.Contains(this))
		{
			active.Add(this);
		}

		registered = true;
		if (Camera == null || Camera.dead)
		{
			Camera = GetComponent<Camera>() ?? GetComponentInChildren<Camera>();
		}

		seed = Seed != 0 ? Seed : unchecked((int)(Id ^ (Id >> 32)));
		started = 0;
	}

	public override void OnDisable()
	{
		Restore();
		registered = false;
		active.Remove(this);
		foreach (CameraShakeInstance instance in instances)
		{
			instance.End();
		}

		instances.Clear();
		if (Profiles != null)
		{
			foreach (CameraShakeSlot slot in Profiles)
			{
				if (slot != null)
				{
					slot.instance = null;
					slot.playing = null;
				}
			}
		}

		Pose = default;
		Intensity = 0f;
		if (active.Count == 0)
		{
			// Nobody is left to carry the world's shakes on: only what plays until it is stopped waits for a camera.
			for (int i = world.Count - 1; i >= 0; i--)
			{
				if (!world[i].IsHeld)
				{
					world[i].End();
					world.RemoveAt(i);
				}
			}
		}
	}

	public override void LateUpdate()
	{
		float scaled = MathF.Min(Time.deltaTime, MaxStep);
		float unscaled = MathF.Min(Time.unscaledDeltaTime, MaxStep);
		AdvanceWorld(scaled, unscaled);
		SyncProfiles();
		for (int i = instances.Count - 1; i >= 0; i--)
		{
			instances[i].Advance(scaled, unscaled);
			if (!instances[i].IsPlaying)
			{
				instances.RemoveAt(i);
			}
		}

		Pose = Evaluate();
		Write(Pose);
	}

	#endregion

	#region PublicMethods

	/// <summary>
	///     Starts a constant shake on this camera: it plays until it is stopped (<see cref="Stop" />, or the instance's own
	///     <see cref="CameraShakeInstance.Stop" />). Keep what it returns to set its strength and tempo as the game goes.
	/// </summary>
	/// <param name="profile">What it looks like.</param>
	/// <param name="strength">0 none, 1 the profile as it is.</param>
	/// <param name="fadeIn">Seconds it takes to come in (negative: the profile's <see cref="CameraShakeProfile.Attack" />).</param>
	public CameraShakeInstance Play(CameraShakeProfile profile, float strength = 1f, float fadeIn = -1f)
	{
		CameraShakeInstance instance = Begin(profile, strength, false);
		if (fadeIn >= 0f)
		{
			instance.Attack = fadeIn;
		}

		return instance;
	}

	/// <summary>An impact on this camera: one shake that dies away by itself.</summary>
	/// <param name="strength">0 none, 1 the profile as it is (more is allowed: the camera's limits hold it).</param>
	/// <param name="profile">What it looks like (none: <see cref="CameraShakePreset.Impact" />).</param>
	/// <param name="direction">Where the impact pushes the camera, in the world (zero: the profile's own kick).</param>
	/// <param name="duration">Seconds it lasts (negative: the profile's <see cref="CameraShakeProfile.Duration" />).</param>
	public CameraShakeInstance Impact(float strength = 1f, CameraShakeProfile profile = null, Vector3 direction = default, float duration = -1f)
	{
		CameraShakeInstance instance = Begin(profile is null ? CameraShakeProfile.Get(CameraShakePreset.Impact) : profile, strength, true);
		instance.Direction = direction;
		if (duration >= 0f)
		{
			instance.Duration = duration;
		}

		return instance;
	}

	/// <summary>Stops a shake: it dies away over <paramref name="fadeOut" /> seconds (negative: its own release; 0: at once).</summary>
	public void Stop(CameraShakeInstance instance, float fadeOut = -1f)
	{
		instance?.Stop(fadeOut);
	}

	/// <summary>
	///     Stops every shake scripts started on this camera (<see cref="Play" />, <see cref="Impact" />). The
	///     <see cref="Profiles" /> list plays on (silence it with its strengths, or by disabling the component), and so do
	///     the shakes of the world.
	/// </summary>
	public void StopAll(float fadeOut = -1f)
	{
		foreach (CameraShakeInstance instance in instances)
		{
			if (!instance.Listed)
			{
				instance.Stop(fadeOut);
			}
		}
	}

	/// <summary>
	///     What the shakes add up to on this camera at this moment, with its strength, shares and limits applied: what
	///     the next LateUpdate writes if nothing changes. Does not move the shakes on.
	/// </summary>
	public CameraShakePose Evaluate()
	{
		TakeRest();
		Transform parent = transform.parent;
		Vector3 position = parent != null ? parent.TransformPoint(restPosition) : restPosition;
		Quaternion rotation = parent != null ? parent.rotation * restRotation : restRotation;

		var sum = new CameraShakePose();
		float quiet = 1f;
		for (int i = 0; i < instances.Count; i++)
		{
			instances[i].AddTo(position, rotation, ref sum, ref quiet);
		}

		if (Listen)
		{
			// The world's shakes are as far as they are from the listener; they still turn and push this camera.
			Vector3 heard = Listener != null && !Listener.dead ? Listener.position : position;
			for (int i = 0; i < world.Count; i++)
			{
				world[i].AddTo(heard, rotation, ref sum, ref quiet);
			}
		}

		float strength = MathF.Max(0f, Strength) * globalStrength;
		Intensity = CameraShakeNoise.Clamp01((1f - quiet) * strength);

		// A lens zoomed in shows the same angle larger: turn the view less, as far as the compensation says.
		float lens = 1f;
		if (LensCompensation > 0f && Camera != null && !Camera.dead)
		{
			float fieldOfView = Math.Clamp(fieldWritten ? restFieldOfView : Camera.FoV, 1f, 179f);
			float reference = Math.Clamp(LensReference, 1f, 179f);
			float ratio = MathF.Tan(fieldOfView * 0.5f * ToRadians) / MathF.Tan(reference * 0.5f * ToRadians);
			lens = 1f + (ratio - 1f) * CameraShakeNoise.Clamp01(LensCompensation);
		}

		sum.Rotation = CameraShakeNoise.SoftLimit(sum.Rotation * (strength * MathF.Max(0f, RotationAmount) * lens), MaxAngle);
		sum.Position = CameraShakeNoise.SoftLimit(sum.Position * (strength * MathF.Max(0f, PositionAmount)), MaxOffset);
		sum.FieldOfView = CameraShakeNoise.SoftLimit(sum.FieldOfView * (strength * MathF.Max(0f, FieldOfViewAmount)), MaxFieldOfView);
		return sum;
	}

	/// <summary>
	///     An impact somewhere in the world: every listening camera within <paramref name="radius" /> metres of
	///     <paramref name="position" /> feels it, less the farther it is, and is pushed away from it (when the profile
	///     has a kick). Change the instance for more: <see cref="CameraShakeInstance.InnerRadius" />,
	///     <see cref="CameraShakeInstance.Falloff" />, <see cref="CameraShakeInstance.PropagationSpeed" /> (the shake
	///     arrives later far away). Nothing happens while no camera listens.
	/// </summary>
	public static CameraShakeInstance ImpactAt(Vector3 position, float strength = 1f, float radius = 30f, CameraShakeProfile profile = null)
	{
		CameraShakeInstance instance = BeginWorld(profile is null ? CameraShakeProfile.Get(CameraShakePreset.Impact) : profile, strength, true);
		instance.IsPositional = true;
		instance.Position = position;
		instance.Radius = radius;
		return instance;
	}

	/// <summary>A constant shake somewhere in the world, until it is stopped: move it with <see cref="CameraShakeInstance.Position" /> (a <see cref="CameraShakeSource" /> does all this as a component).</summary>
	public static CameraShakeInstance PlayAt(Vector3 position, CameraShakeProfile profile, float strength = 1f, float radius = 30f)
	{
		CameraShakeInstance instance = BeginWorld(profile, strength, false);
		instance.IsPositional = true;
		instance.Position = position;
		instance.Radius = radius;
		return instance;
	}

	/// <summary>An impact every listening camera feels in full, wherever it is.</summary>
	public static CameraShakeInstance ImpactEverywhere(float strength = 1f, CameraShakeProfile profile = null)
	{
		return BeginWorld(profile is null ? CameraShakeProfile.Get(CameraShakePreset.Impact) : profile, strength, true);
	}

	/// <summary>A constant shake every listening camera feels in full, until it is stopped.</summary>
	public static CameraShakeInstance PlayEverywhere(CameraShakeProfile profile, float strength = 1f)
	{
		return BeginWorld(profile, strength, false);
	}

	#endregion

	#region InternalMethods

	/// <summary>Somebody will carry the shake on: its own camera is on, or (a shake of the world) any camera is.</summary>
	internal static bool AnyoneListening(CameraShakeInstance instance)
	{
		return instance.Owner != null ? instance.Owner.registered : active.Count > 0;
	}

	#endregion

	#region PrivateMethods

	private CameraShakeInstance Begin(CameraShakeProfile profile, float strength, bool oneShot)
	{
		if (!registered || profile is null || profile.IsDestroyed)
		{
			// Nothing to play, or nobody to play it: a shake that has ended already.
			return new CameraShakeInstance(null, 0f, oneShot, 0);
		}

		MakeRoom(instances, MaxPlaying);
		var instance = new CameraShakeInstance(profile, strength, oneShot, unchecked(seed * 31 + ++started * 7919)) { Owner = this };
		instances.Add(instance);
		return instance;
	}

	private static CameraShakeInstance BeginWorld(CameraShakeProfile profile, float strength, bool oneShot)
	{
		if (profile is null || profile.IsDestroyed || (oneShot && active.Count == 0))
		{
			return new CameraShakeInstance(null, 0f, oneShot, 0);
		}

		MakeRoom(world, MaxWorld);
		var instance = new CameraShakeInstance(profile, strength, oneShot, unchecked(++worldStarted * 104729 + 12345));
		world.Add(instance);
		return instance;
	}

	// A full list lets go of its oldest impact (else its oldest shake): something runs away with starting them.
	private static void MakeRoom(List<CameraShakeInstance> list, int most)
	{
		if (list.Count < most)
		{
			return;
		}

		int index = list.FindIndex(i => i.IsOneShot);
		index = index < 0 ? 0 : index;
		list[index].End();
		list.RemoveAt(index);
	}

	// The shakes of the world move on once a frame, with the first camera that asks.
	private static void AdvanceWorld(float scaled, float unscaled)
	{
		if (worldFrame == Time.frameCount)
		{
			return;
		}

		worldFrame = Time.frameCount;
		for (int i = world.Count - 1; i >= 0; i--)
		{
			world[i].Advance(scaled, unscaled);
			if (!world[i].IsPlaying)
			{
				world.RemoveAt(i);
			}
		}
	}

	// The list's entries play: a new profile in an entry fades over, an entry's strength is followed, an entry gone dies away.
	private void SyncProfiles()
	{
		if (Profiles != null)
		{
			foreach (CameraShakeSlot slot in Profiles)
			{
				if (slot == null)
				{
					continue;
				}

				CameraShakeProfile wanted = slot.Profile is null || slot.Profile.IsDestroyed ? null : slot.Profile;
				if (!ReferenceEquals(slot.playing, wanted) || (wanted != null && slot.instance == null))
				{
					slot.instance?.Stop();
					slot.playing = wanted;
					slot.instance = wanted != null ? Begin(wanted, slot.Strength, false) : null;
					if (slot.instance != null)
					{
						slot.instance.Listed = true;
					}
				}

				if (slot.instance != null)
				{
					slot.instance.Strength = slot.Strength;
				}
			}
		}

		foreach (CameraShakeInstance instance in instances)
		{
			if (instance.Listed && !instance.IsStopping && !InList(instance))
			{
				instance.Stop();
			}
		}
	}

	private bool InList(CameraShakeInstance instance)
	{
		if (Profiles != null)
		{
			foreach (CameraShakeSlot slot in Profiles)
			{
				if (slot != null && ReferenceEquals(slot.instance, instance))
				{
					return true;
				}
			}
		}

		return false;
	}

	// The object's pose without the shake: what it holds that this component did not write.
	private void TakeRest()
	{
		Transform own = transform;
		if (!written || own.localPosition != writtenPosition)
		{
			restPosition = own.localPosition;
		}

		if (!written || own.localRotation != writtenRotation)
		{
			restRotation = own.localRotation;
		}

		if (Camera != null && !Camera.dead && (!fieldWritten || Camera.FoV != writtenFieldOfView))
		{
			restFieldOfView = Camera.FoV;
		}
	}

	private void Write(CameraShakePose pose)
	{
		Transform own = transform;
		if (pose.Position == Vector3.Zero && pose.Rotation == Vector3.Zero)
		{
			// Nothing shakes: the object is left alone (back at its rest once).
			if (written)
			{
				own.localPosition = restPosition;
				own.localRotation = restRotation;
				written = false;
			}
		}
		else
		{
			// Along the object's own axes at rest, in metres of the world whatever its parents' scale.
			Vector3 offset = restRotation * pose.LocalPosition;
			Transform parent = own.parent;
			if (parent != null)
			{
				Vector3 scale = parent.lossyScale;
				offset = new Vector3(Divide(offset.X, scale.X), Divide(offset.Y, scale.Y), Divide(offset.Z, scale.Z));
			}

			writtenPosition = restPosition + offset;
			writtenRotation = (restRotation * pose.LocalRotation).Normalized();
			own.localPosition = writtenPosition;
			own.localRotation = writtenRotation;
			written = true;
		}

		if (Camera == null || Camera.dead)
		{
			fieldWritten = false;
		}
		else if (pose.FieldOfView != 0f)
		{
			writtenFieldOfView = Math.Clamp(restFieldOfView + pose.FieldOfView, 1f, 179f);
			Camera.FoV = writtenFieldOfView;
			fieldWritten = true;
		}
		else if (fieldWritten)
		{
			Camera.FoV = restFieldOfView;
			fieldWritten = false;
		}
	}

	// The shake is taken off the object: where this component's last pose still stands, the rest pose comes back.
	private void Restore()
	{
		Transform own = transform;
		if (written && own != null)
		{
			if (own.localPosition == writtenPosition)
			{
				own.localPosition = restPosition;
			}

			if (own.localRotation == writtenRotation)
			{
				own.localRotation = restRotation;
			}
		}

		written = false;
		if (fieldWritten && Camera != null && !Camera.dead && Camera.FoV == writtenFieldOfView)
		{
			Camera.FoV = restFieldOfView;
		}

		fieldWritten = false;
	}

	private static float Divide(float value, float by)
	{
		return MathF.Abs(by) > 1e-6f ? value / by : value;
	}

	#endregion
}
