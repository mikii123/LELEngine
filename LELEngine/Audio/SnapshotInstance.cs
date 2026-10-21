using System;

namespace LELEngine.Audio
{
	/// <summary>
	///     The game's handle of a mixer snapshot (as FMOD's snapshot instances): start it to blend its bus volumes and
	///     effect values in, set its intensity, stop it to fade it out. Once stopped the handle is spent; starting a
	///     snapshot again gives a new handle (<see cref="AudioSystem.StartSnapshot" />).
	/// </summary>
	public sealed class SnapshotInstance
	{
		#region PublicFields

		public int Id { get; }
		public AudioSnapshot Definition { get; }
		public string Name => Definition.Name;

		/// <summary>Started and not yet faded out.</summary>
		public bool IsActive => active;

		/// <summary>Raised on the main thread when the snapshot faded out.</summary>
		public event Action<SnapshotInstance> Stopped;

		#endregion

		#region PrivateFields

		private volatile bool active;
		private float intensity = 1f;

		#endregion

		#region Constructors

		internal SnapshotInstance(int id, AudioSnapshot definition)
		{
			Id = id;
			Definition = definition;
		}

		#endregion

		#region PublicMethods

		/// <summary>Fades the snapshot in (its intensity envelope's attack) at the given intensity.</summary>
		public void Start(float startIntensity = 1f)
		{
			intensity = Math.Clamp(startIntensity, 0f, 1f);
			if (active)
			{
				AudioSystem.Post(MixerCommandKind.SetSnapshotIntensity, Id, intensity);
				return;
			}

			active = true;
			AudioSystem.RegisterSnapshotHandle(this);
			AudioSystem.Post(MixerCommandKind.StartSnapshot, Id, intensity, false, Definition.Id);
		}

		/// <summary>Fades the snapshot out (its intensity envelope's release), or cuts it.</summary>
		public void Stop(bool immediate = false)
		{
			if (active)
			{
				AudioSystem.Post(MixerCommandKind.StopSnapshot, Id, 0f, immediate);
			}
		}

		/// <summary>How much of the snapshot applies, 0..1 (on top of its fades).</summary>
		public void SetIntensity(float value)
		{
			intensity = Math.Clamp(value, 0f, 1f);
			if (active)
			{
				AudioSystem.Post(MixerCommandKind.SetSnapshotIntensity, Id, intensity);
			}
		}

		public float Intensity => intensity;

		public override string ToString()
		{
			return "snapshot:" + Name + " #" + Id;
		}

		#endregion

		#region InternalMethods

		internal void MarkStopped()
		{
			active = false;
			try
			{
				Stopped?.Invoke(this);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
		}

		#endregion
	}
}
