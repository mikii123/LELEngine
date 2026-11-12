using System;
using LELEngine;
using OpenTK.Mathematics;

/// <summary>
///     A shake that is playing: what <see cref="CameraShake.Play" />, <see cref="CameraShake.Impact" /> and the shakes
///     of the world (<see cref="CameraShake.PlayAt" />, <see cref="CameraShakeSource" />, ...) hand back. Keep it to
///     change the shake while it plays (<see cref="Strength" />, <see cref="Speed" />, <see cref="Position" />) and to
///     <see cref="Stop" /> it; an impact ends by itself. Once it has ended (<see cref="IsPlaying" /> false) it does
///     nothing any more and can be forgotten.
/// </summary>
public sealed class CameraShakeInstance
{
	#region PublicFields

	/// <summary>The profile played; its values are read as it plays, so editing the profile shows at once.</summary>
	public CameraShakeProfile Profile { get; }

	/// <summary>How strong the shake is (0 none, 1 the profile as it is; more is allowed). Followed over <see cref="Smoothing" /> seconds.</summary>
	public float Strength { get; set; } = 1f;

	/// <summary>Tempo on top of the profile's own (1 as authored).</summary>
	public float Speed { get; set; } = 1f;

	/// <summary>Seconds the shake takes to follow a change of <see cref="Strength" />, so setting it never jolts the picture (0: at once).</summary>
	public float Smoothing { get; set; } = 0.05f;

	/// <summary>Seconds to come in (from the profile's <see cref="CameraShakeProfile.Attack" />).</summary>
	public float Attack { get; set; }

	/// <summary>Seconds to die away after <see cref="Stop" /> (from the profile's <see cref="CameraShakeProfile.Release" />).</summary>
	public float Release { get; set; }

	/// <summary>An impact: seconds it lasts (from the profile's <see cref="CameraShakeProfile.Duration" />).</summary>
	public float Duration { get; set; }

	/// <summary>Runs on real time (from the profile's <see cref="CameraShakeProfile.IgnoreTimeScale" />).</summary>
	public bool IgnoreTimeScale { get; set; }

	/// <summary>
	///     Where the kick pushes the camera, in the world (any length; zero: the profile's own kick, or away from
	///     <see cref="Position" /> for a shake that has a place).
	/// </summary>
	public Vector3 Direction { get; set; }

	/// <summary>The shake is somewhere in the world: cameras feel it by their distance from <see cref="Position" />.</summary>
	public bool IsPositional { get; internal set; }

	/// <summary>Where the shake comes from (a shake with a place).</summary>
	public Vector3 Position { get; set; }

	/// <summary>Metres from <see cref="Position" /> at which nothing is felt any more.</summary>
	public float Radius { get; set; } = 30f;

	/// <summary>Metres from <see cref="Position" /> within which the shake is felt in full.</summary>
	public float InnerRadius { get; set; }

	/// <summary>How the shake falls off between <see cref="InnerRadius" /> and <see cref="Radius" />: 1 evenly, 2 and above quickly at first.</summary>
	public float Falloff { get; set; } = 2f;

	/// <summary>Metres a second the shake travels from <see cref="Position" />: a far camera feels it later (0: everywhere at once).</summary>
	public float PropagationSpeed { get; set; }

	/// <summary>The kick of a shake with a place pushes cameras away from it (unless <see cref="Direction" /> says where).</summary>
	public bool PushesAway { get; set; } = true;

	/// <summary>Plays once and dies away (an impact), instead of until it is stopped.</summary>
	public bool IsOneShot { get; }

	/// <summary>Seconds since it started.</summary>
	public float Age => age;

	/// <summary>Still playing (or dying away); false once it has ended.</summary>
	public bool IsPlaying => !ended;

	/// <summary><see cref="Stop" /> was called and it is dying away.</summary>
	public bool IsStopping => !ended && stoppedAt >= 0f;

	/// <summary>How much of the profile shows now: the strength followed, coming in and dying away included (not a camera's distance).</summary>
	public float Level => ended || Profile is null ? 0f : level * Envelope(age) * CameraShakeNoise.Clamp01(Profile.Strength);

	#endregion

	#region InternalFields

	/// <summary>The camera the shake belongs to (<see cref="CameraShake.Play" />); null: a shake of the world.</summary>
	internal CameraShake Owner;

	/// <summary>Started by an entry of the camera's <see cref="CameraShake.Profiles" /> list, not by a script.</summary>
	internal bool Listed;

	/// <summary>A shake that plays until it is stopped and has not been: a shake of the world is kept while no camera listens.</summary>
	internal bool IsHeld => !ended && !IsOneShot && stoppedAt < 0f;

	#endregion

	#region PrivateFields

	private readonly int seed;
	private double line;
	private float age;
	private float level;
	private float stoppedAt = -1f;
	private bool ended;

	#endregion

	#region Constructors

	internal CameraShakeInstance(CameraShakeProfile profile, float strength, bool oneShot, int seed)
	{
		Profile = profile;
		IsOneShot = oneShot;
		this.seed = seed;
		Strength = MathF.Max(0f, strength);
		level = Strength;
		if (profile is null)
		{
			ended = true;
			return;
		}

		Attack = profile.Attack;
		Release = profile.Release;
		Duration = profile.Duration;
		IgnoreTimeScale = profile.IgnoreTimeScale;
	}

	#endregion

	#region PublicMethods

	/// <summary>
	///     Ends the shake: it dies away over <paramref name="fadeOut" /> seconds (negative: its <see cref="Release" />;
	///     0: at once).
	/// </summary>
	public void Stop(float fadeOut = -1f)
	{
		if (ended)
		{
			return;
		}

		if (fadeOut >= 0f)
		{
			Release = fadeOut;
		}

		if (stoppedAt < 0f)
		{
			stoppedAt = age;
		}

		// Nobody is left to hear a shake of the world die away.
		if (Release <= 0f || !CameraShake.AnyoneListening(this))
		{
			End();
		}
	}

	#endregion

	#region InternalMethods

	/// <summary>Ends it now, without dying away. An ended shake knows its camera no longer: whoever still holds it holds nothing of a scene.</summary>
	internal void End()
	{
		ended = true;
		Owner = null;
	}

	/// <summary>A frame went by: <paramref name="scaled" /> seconds of the game's time, <paramref name="unscaled" /> of real time.</summary>
	internal void Advance(float scaled, float unscaled)
	{
		if (ended)
		{
			return;
		}

		if (Profile is null || Profile.IsDestroyed)
		{
			End();
			return;
		}

		float delta = IgnoreTimeScale ? unscaled : scaled;
		if (delta <= 0f)
		{
			return;
		}

		age += delta;
		line += (double)delta * Speed * Profile.Speed;
		float target = MathF.Max(0f, Strength);
		level = Smoothing > 1e-4f ? level + (target - level) * (1f - MathF.Exp(-delta / Smoothing)) : target;

		// The farthest camera in reach hears the end last.
		float late = IsPositional && PropagationSpeed > 0.01f ? MathF.Max(Radius, 0f) / PropagationSpeed : 0f;
		if (stoppedAt >= 0f && age >= stoppedAt + Release + late)
		{
			End();
		}
		else if (IsOneShot && age >= MathF.Max(Profile.HasMovement ? Duration : 0f, Profile.KickLength) + late)
		{
			End();
		}
	}

	/// <summary>
	///     Adds what a camera standing at <paramref name="position" /> and looking with <paramref name="rotation" /> (world)
	///     gets of the shake to <paramref name="sum" />, and takes its share out of <paramref name="quiet" /> (1: nothing
	///     shakes).
	/// </summary>
	internal void AddTo(Vector3 position, Quaternion rotation, ref CameraShakePose sum, ref float quiet)
	{
		if (ended || Profile is null)
		{
			return;
		}

		float reach = 1f;
		float late = 0f;
		Vector3 direction = Direction;
		if (IsPositional)
		{
			Vector3 away = position - Position;
			float distance = away.Length;
			reach = Reach(distance);
			if (reach <= 0f)
			{
				return;
			}

			if (PropagationSpeed > 0.01f)
			{
				late = distance / PropagationSpeed;
			}

			if (direction == Vector3.Zero && PushesAway && distance > 1e-3f)
			{
				direction = away / distance;
			}
		}

		float heard = age - late;
		if (heard <= 0f)
		{
			return;
		}

		float gain = CameraShakeNoise.Clamp01(Profile.Strength) * reach * level;
		if (gain <= 0f)
		{
			return;
		}

		float steady = gain * Envelope(heard);
		if (steady > 1e-6f && Profile.HasMovement)
		{
			sum += Profile.Movement(line - (double)late * Speed * Profile.Speed, seed) * steady;
			quiet *= 1f - MathF.Min(1f, steady);
		}

		if (Profile.HasKick)
		{
			float kicked = gain * Fade(heard);
			if (kicked > 1e-6f)
			{
				// Into the camera's own picture: its transform's +X is the left of the picture.
				Vector3 own = direction != Vector3.Zero ? rotation.Inverted() * direction : Vector3.Zero;
				sum += Profile.Kick(heard, new Vector3(-own.X, own.Y, own.Z)) * kicked;
				quiet *= 1f - MathF.Min(1f, kicked * MathF.Max(0f, 1f - heard / MathF.Max(Profile.KickLength, 1e-3f)));
			}
		}
	}

	#endregion

	#region PrivateMethods

	// How much of the shake reaches a camera this far from it.
	private float Reach(float distance)
	{
		float inner = MathF.Max(InnerRadius, 0f);
		if (distance <= inner)
		{
			return 1f;
		}

		if (distance >= Radius)
		{
			return 0f;
		}

		return MathF.Pow(1f - (distance - inner) / (Radius - inner), MathF.Max(Falloff, 0.01f));
	}

	// Coming in, an impact's dying away, and the dying away after a stop.
	private float Envelope(float at)
	{
		float value = Attack > 1e-4f ? CameraShakeNoise.SmoothStep(at / Attack) : 1f;
		if (IsOneShot)
		{
			if (Duration <= 1e-4f || at >= Duration)
			{
				return 0f;
			}

			value *= MathF.Pow(1f - at / Duration, MathF.Max(Profile.Decay, 0.01f));
		}

		return value * Fade(at);
	}

	// The dying away after a stop alone.
	private float Fade(float at)
	{
		if (stoppedAt < 0f || at <= stoppedAt)
		{
			return 1f;
		}

		return Release > 1e-4f ? 1f - CameraShakeNoise.SmoothStep((at - stoppedAt) / Release) : 0f;
	}

	#endregion
}
