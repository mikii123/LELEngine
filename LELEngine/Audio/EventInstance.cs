using System;
using OpenTK.Mathematics;

namespace LELEngine.Audio
{
	/// <summary>
	///     The game's handle of a playing event (FMOD's EventInstance): start, stop, parameters, 3D attributes, volume
	///     and pitch, callbacks. Everything is forwarded to the audio thread; the state read back is a few
	///     milliseconds behind. <see cref="Release" /> lets the instance go once it stopped (a one-shot releases
	///     itself); an instance never released stays in the mixer until <see cref="AudioSystem.StopAll" />.
	/// </summary>
	public sealed class EventInstance
	{
		#region PublicFields

		/// <summary>The event description the instance plays.</summary>
		public CompiledEvent Description => Playback.Event;

		public string Path => Playback.Event.Path;
		public int Id => Playback.Id;

		public PlaybackState State => (PlaybackState)Playback.StateValue;
		public bool IsPlaying => State == PlaybackState.Playing || State == PlaybackState.Sustaining || State == PlaybackState.Stopping;

		/// <summary>True until the instance was released and stopped.</summary>
		public bool IsValid => !gone;

		/// <summary>The timeline cursor, seconds.</summary>
		public float TimelinePosition => Playback.TimelineSeconds;

		public float PeakLeft => Playback.PeakLeft;
		public float PeakRight => Playback.PeakRight;
		public int VoiceCount => Playback.VoiceCount;

		/// <summary>A track's peak level (linear) in the last mixed block, by the track's id or name; 0 when unknown (meters, visualizers).</summary>
		public float GetTrackPeak(string trackIdOrName)
		{
			CompiledTrack[] tracks = Playback.Event.Tracks;
			float[] peaks = Playback.TrackPeaks;
			for (int i = 0; i < tracks.Length && i < peaks.Length; i++)
			{
				if (tracks[i].Id == trackIdOrName || tracks[i].Source?.Name == trackIdOrName)
				{
					return peaks[i];
				}
			}

			return 0f;
		}
		public bool Is3D => Playback.Event.Spatializer != null && Playback.Event.Spatializer.Enabled;

		/// <summary>The last 3D position set.</summary>
		public Vector3 Position { get; private set; }

		public event Action<EventInstance> Started;
		public event Action<EventInstance> Stopped;
		public event Action<EventInstance> StartRefused;

		/// <summary>The cursor reached a destination marker (its name).</summary>
		public event Action<EventInstance, string> MarkerReached;

		/// <summary>The cursor reached a beat: bar and beat numbers (1-based) and the tempo.</summary>
		public event Action<EventInstance, int, int, float> Beat;

		/// <summary>The cursor stopped at a sustain point (its name); <see cref="KeyOff" /> resumes.</summary>
		public event Action<EventInstance, string> Sustained;

		#endregion

		#region InternalFields

		internal readonly EventPlayback Playback;
		internal bool Released;

		#endregion

		#region PrivateFields

		private bool gone;
		private Vector3 velocity;
		private Vector3 forward = -Vector3.UnitZ;
		private Vector3 up = Vector3.UnitY;

		#endregion

		#region Constructors

		internal EventInstance(EventPlayback playback)
		{
			Playback = playback;
		}

		#endregion

		#region PublicMethods

		/// <summary>Starts the timeline (a start while playing restarts), optionally at an offset in seconds.</summary>
		public void Start(float atSeconds = 0f)
		{
			if (gone)
			{
				return;
			}

			AudioSystem.Post(MixerCommandKind.StartInstance, Id, Math.Max(0f, atSeconds));
		}

		public void Stop(AudioStopMode mode = AudioStopMode.AllowFadeout)
		{
			if (gone)
			{
				return;
			}

			AudioSystem.Post(MixerCommandKind.StopInstance, Id, 0f, mode == AudioStopMode.Immediate);
		}

		/// <summary>The instance is dropped once it stops (stop it to drop it now).</summary>
		public void Release()
		{
			if (gone || Released)
			{
				return;
			}

			Released = true;
			AudioSystem.Post(MixerCommandKind.ReleaseInstance, Id);
		}

		/// <summary>Sets a parameter by name (or id); global parameters go to the whole system.</summary>
		public void SetParameter(string name, float value, bool ignoreSeekSpeed = false)
		{
			if (gone || string.IsNullOrEmpty(name))
			{
				return;
			}

			int index = Playback.Event.FindParameterByName(name);
			if (index < 0)
			{
				index = Playback.Event.FindParameter(name);
			}

			if (index < 0)
			{
				AudioSystem.WarnOnce("parameter:" + Path + ":" + name, $"[Audio] Event '{Path}' has no parameter '{name}'");
				return;
			}

			CompiledParameter parameter = Playback.Event.Parameters[index];
			if (parameter.IsGlobal)
			{
				AudioSystem.SetGlobalParameter(parameter.Name, value, ignoreSeekSpeed);
				return;
			}

			AudioSystem.Post(MixerCommandKind.SetInstanceParameter, Id, value, ignoreSeekSpeed, parameter.Id);
		}

		/// <summary>Sets a labeled parameter by one of its labels.</summary>
		public void SetParameterByLabel(string name, string label, bool ignoreSeekSpeed = false)
		{
			int index = Playback.Event.FindParameterByName(name);
			if (index < 0)
			{
				return;
			}

			CompiledParameter parameter = Playback.Event.Parameters[index];
			int labelIndex = parameter.Source?.Labels?.FindIndex(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase)) ?? -1;
			if (labelIndex >= 0)
			{
				SetParameter(name, parameter.Min + labelIndex, ignoreSeekSpeed);
			}
		}

		/// <summary>The current value of a parameter (a few milliseconds behind), or NaN when unknown.</summary>
		public float GetParameter(string name)
		{
			int index = Playback.Event.FindParameterByName(name);
			if (index < 0)
			{
				index = Playback.Event.FindParameter(name);
			}

			if (index < 0)
			{
				return float.NaN;
			}

			if (Playback.Event.Parameters[index].IsGlobal)
			{
				return AudioSystem.GetGlobalParameter(Playback.Event.Parameters[index].Name);
			}

			float[] values = Playback.PublishedParameters;
			return index < values.Length ? values[index] : float.NaN;
		}

		/// <summary>Where the instance is (and moves) for spatialization; call every frame it moves.</summary>
		public void Set3DAttributes(Vector3 position, Vector3 newVelocity = default, Vector3 newForward = default, Vector3 newUp = default)
		{
			if (gone)
			{
				return;
			}

			Position = position;
			velocity = newVelocity;
			forward = newForward.LengthSquared > 1e-8f ? newForward : forward;
			up = newUp.LengthSquared > 1e-8f ? newUp : up;
			AudioSystem.Post(new MixerCommand { Kind = MixerCommandKind.SetInstance3D, Id = Id, V0 = position, V1 = velocity, V2 = forward, V3 = up, Flag = true });
		}

		/// <summary>Volume as a gain (1 = as authored).</summary>
		public void SetVolume(float gain)
		{
			if (!gone)
			{
				AudioSystem.Post(MixerCommandKind.SetInstanceVolume, Id, gain);
			}
		}

		/// <summary>Pitch as a ratio (1 = as authored, 2 = an octave up).</summary>
		public void SetPitch(float ratio)
		{
			if (!gone)
			{
				AudioSystem.Post(MixerCommandKind.SetInstancePitch, Id, ratio);
			}
		}

		public void SetPaused(bool paused)
		{
			if (!gone)
			{
				AudioSystem.Post(MixerCommandKind.SetInstancePaused, Id, 0f, paused);
			}
		}

		/// <summary>Resumes the timeline from a sustain point.</summary>
		public void KeyOff()
		{
			if (!gone)
			{
				AudioSystem.Post(MixerCommandKind.KeyOff, Id);
			}
		}

		/// <summary>Moves the timeline cursor (sounds under it restart at the right offset).</summary>
		public void SetTimelinePosition(float seconds)
		{
			if (!gone)
			{
				AudioSystem.Post(MixerCommandKind.SetTimelinePosition, Id, Math.Max(0f, seconds));
			}
		}

		public override string ToString()
		{
			return Path + " #" + Id;
		}

		#endregion

		#region InternalMethods

		internal void Dispatch(AudioNotification notification)
		{
			try
			{
				switch (notification.Kind)
				{
					case AudioNotificationKind.Started:
						Started?.Invoke(this);
						break;
					case AudioNotificationKind.Stopped:
						Stopped?.Invoke(this);
						break;
					case AudioNotificationKind.StartRefused:
						StartRefused?.Invoke(this);
						break;
					case AudioNotificationKind.MarkerReached:
						MarkerReached?.Invoke(this, notification.Text);
						break;
					case AudioNotificationKind.Beat:
						Beat?.Invoke(this, notification.Bar, notification.Beat, notification.Tempo);
						break;
					case AudioNotificationKind.Sustained:
						Sustained?.Invoke(this, notification.Text);
						break;
				}
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
		}

		internal void MarkGone()
		{
			gone = true;
		}

		#endregion
	}
}
