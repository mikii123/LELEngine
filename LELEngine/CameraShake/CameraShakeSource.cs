using LELEngine;
using OpenTK.Mathematics;

/// <summary>How a <see cref="CameraShakeSource" /> shakes.</summary>
public enum CameraShakeSourceMode
{
	/// <summary>All the time while it plays: an engine, a waterfall, a train going by.</summary>
	Constant,

	/// <summary>Once, dying away: an explosion, a door slammed, a giant's step.</summary>
	Impact
}

/// <summary>
///     A place in the world that shakes cameras: every <see cref="CameraShake" /> that listens feels it by its distance
///     from this object (in full within <see cref="InnerRadius" />, not at all past <see cref="Radius" />), or in full
///     wherever it is (<see cref="Everywhere" />). Put it on what shakes the ground and no script is needed: an
///     explosion's prefab with an <see cref="CameraShakeSourceMode.Impact" /> that plays when it appears, an engine with
///     a <see cref="CameraShakeSourceMode.Constant" /> shake that travels with it. <see cref="Strength" /> can be
///     animated (a Timeline, an Animator) or set by a script while it plays; scripts start and end it with
///     <see cref="Play" />, <see cref="Impact" /> and <see cref="Stop" />.
/// </summary>
[InspectorColor(InspectorColorKind.Camera)]
public sealed class CameraShakeSource : Behaviour
{
	#region PublicFields

	[InlineEditor]
	[Tooltip("What the shake looks like (an impact without one: the Impact preset). Another profile while a constant shake plays fades over to it")]
	public CameraShakeProfile Profile;

	[Tooltip("Constant: shakes until it is stopped. Impact: one shake that dies away")]
	public CameraShakeSourceMode Mode = CameraShakeSourceMode.Constant;

	[Range(0f, 1f)]
	[Tooltip("How strong the shake is at the source: 0 none, 1 the profile as it is")]
	public float Strength = 1f;

	[Tooltip("Starts by itself when the component is enabled (an impact: when the object appears)")]
	public bool PlayOnEnable = true;

	[Header("Reach")]
	[Tooltip("Every listening camera feels it in full, wherever it is")]
	public bool Everywhere;

	[HideIf(nameof(Everywhere))]
	[Tooltip("Metres from this object at which nothing is felt any more")]
	public float Radius = 30f;

	[HideIf(nameof(Everywhere))]
	[Tooltip("Metres from this object within which the shake is felt in full")]
	public float InnerRadius = 2f;

	[HideIf(nameof(Everywhere))]
	[Range(0.5f, 4f)]
	[Tooltip("How the shake falls off between the two: 1 evenly, 2 and above quickly at first")]
	public float Falloff = 2f;

	[HideIf(nameof(Everywhere))]
	[Tooltip("Metres a second the shake travels: a far camera feels it later, as the ground wave of a far explosion (0: everywhere at once)")]
	public float PropagationSpeed;

	[HideIf(nameof(Everywhere))]
	[Tooltip("The profile's kick pushes cameras away from this object")]
	public bool PushesAway = true;

	/// <summary>The shake it plays now, or played last; null before the first.</summary>
	public CameraShakeInstance Instance => instance;

	/// <summary>A shake of this source is playing (or dying away).</summary>
	public bool IsPlaying => instance != null && instance.IsPlaying;

	#endregion

	#region PrivateFields

	private CameraShakeInstance instance;

	// The constant shake is wanted: started (by itself or by Play) and not stopped since.
	private bool playing;

	#endregion

	#region UnityMethods

	public override void OnEnable()
	{
		if (!PlayOnEnable)
		{
			return;
		}

		if (Mode == CameraShakeSourceMode.Impact)
		{
			Impact();
		}
		else
		{
			Play();
		}
	}

	public override void OnDisable()
	{
		// A constant shake dies away with its source; an impact that is under way plays out where it was.
		playing = false;
		if (instance != null && !instance.IsOneShot)
		{
			instance.Stop();
		}
	}

	public override void LateUpdate()
	{
		Follow();
	}

	#endregion

	#region PublicMethods

	/// <summary>Starts the constant shake (again from its beginning when it plays already).</summary>
	public CameraShakeInstance Play()
	{
		if (instance != null && !instance.IsOneShot)
		{
			instance.Stop();
		}

		instance = null;
		playing = true;
		Follow();
		return instance;
	}

	/// <summary>
	///     One impact from where the object is now, <paramref name="scale" /> times its <see cref="Strength" />. It stays
	///     where it happened; a constant shake of this source ends, impacts already under way play out.
	/// </summary>
	public CameraShakeInstance Impact(float scale = 1f)
	{
		Stop();
		instance = Everywhere ? CameraShake.ImpactEverywhere(Strength * scale, Profile) : CameraShake.ImpactAt(transform.position, Strength * scale, Radius, Profile);
		Place(instance);
		return instance;
	}

	/// <summary>Ends the constant shake: it dies away over <paramref name="fadeOut" /> seconds (negative: the profile's release; 0: at once).</summary>
	public void Stop(float fadeOut = -1f)
	{
		playing = false;
		if (instance != null && !instance.IsOneShot)
		{
			instance.Stop(fadeOut);
		}
	}

	#endregion

	#region PrivateMethods

	// The constant shake while it is wanted: it plays the profile the source has now (a profile given later starts
	// it, another one fades over), as strong as the source says, from where its object is.
	private void Follow()
	{
		if (!playing)
		{
			return;
		}

		CameraShakeProfile wanted = Profile is null || Profile.IsDestroyed ? null : Profile;
		bool current = instance != null && instance.IsPlaying && !instance.IsStopping && !instance.IsOneShot && ReferenceEquals(instance.Profile, wanted);
		if (!current)
		{
			if (instance != null && !instance.IsOneShot)
			{
				instance.Stop();
			}

			instance = wanted is null ? null : Everywhere ? CameraShake.PlayEverywhere(wanted, Strength) : CameraShake.PlayAt(transform.position, wanted, Strength, Radius);
		}

		if (instance != null)
		{
			instance.Strength = Strength;
			Place(instance);
		}
	}

	private void Place(CameraShakeInstance shake)
	{
		shake.IsPositional = !Everywhere;
		if (Everywhere)
		{
			return;
		}

		shake.Position = transform.position;
		shake.Radius = Radius;
		shake.InnerRadius = InnerRadius;
		shake.Falloff = Falloff;
		shake.PropagationSpeed = PropagationSpeed;
		shake.PushesAway = PushesAway;
	}

	#endregion
}
