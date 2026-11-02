using System;
using System.Collections.Generic;
using LELEngine.Serialization;

namespace LELEngine.Animation
{
	/// <summary>What happens to the animated properties when a timeline ends.</summary>
	public enum TimelineEndBehaviour
	{
		/// <summary>They keep the values written last.</summary>
		Hold,

		/// <summary>They go back to what they held without the timeline.</summary>
		Restore,

		/// <summary>The timeline starts again from its beginning.</summary>
		Loop
	}

	/// <summary>A component that wants to hear about its object's timeline (found on the Timeline's GameObject).</summary>
	public interface ITimelineReceiver
	{
		/// <summary>The cursor reached or jumped to a destination marker.</summary>
		void OnTimelineMarker(Timeline timeline, string marker);
	}

	/// <summary>
	///     Which object of the scene a channel of the timeline animates: a scene reference, so by the object's id
	///     (<see cref="GameObject.Id" />) and not by its name or place in the hierarchy. The id is also kept as a value: it
	///     finds the object again when the reference cannot be written or was lost (another loaded scene, a deletion undone).
	/// </summary>
	[Serializable]
	public sealed class TimelineBinding
	{
		/// <summary><see cref="TimelineChannel.id" />.</summary>
		public string channel;

		public GameObject target;

		/// <summary>The object's id when it was last seen.</summary>
		public ulong targetId;

		/// <summary>The object's name when it was last seen (shown while it is missing).</summary>
		public string targetName;
	}

	/// <summary>
	///     Plays a <see cref="TimelineAsset" /> on objects of the scene (Unity's PlayableDirector): each channel of the asset
	///     is bound to any GameObject, not only to children of this one. Writes only the properties its clips have keys for,
	///     after the scene's animators (so it overrides them) and before LateUpdate. The game drives it through
	///     <see cref="Play" /> / <see cref="Stop" /> / <see cref="SetParameter" /> / <see cref="KeyOff" /> and hears of markers
	///     through <see cref="MarkerReached" /> or <see cref="ITimelineReceiver" />.
	/// </summary>
	[InspectorColor(0.35f, 0.7f, 0.65f)]
	public sealed class Timeline : Behaviour, ISerializationCallbackReceiver
	{
		#region PublicFields

		/// <summary>The timeline played (changing it stops the playback).</summary>
		public TimelineAsset asset
		{
			get => m_Asset;
			set
			{
				if (m_Asset == value)
				{
					return;
				}

				player?.Stop();
				player = null;
				m_Asset = value;
			}
		}

		/// <summary>Starts by itself when the component is enabled.</summary>
		public bool playOnAwake = true;

		public AnimatorUpdateMode updateMode = AnimatorUpdateMode.Normal;

		/// <summary>Runs on unscaled time (a cutscene while the game is paused).</summary>
		public bool ignoreTimeScale;

		/// <summary>Playback speed (1 = as authored).</summary>
		public float speed = 1f;

		public TimelineEndBehaviour endBehaviour = TimelineEndBehaviour.Hold;

		/// <summary>Which object each channel animates.</summary>
		[HideInInspector] public List<TimelineBinding> bindings = new List<TimelineBinding>();

		/// <summary>The playback (made by the first <see cref="Play" />); null before.</summary>
		public TimelinePlayer Player => player;

		public bool isPlaying => player != null && player.IsPlaying;

		public TimelinePlaybackState state => player?.State ?? TimelinePlaybackState.Stopped;

		/// <summary>The cursor stands still; the pose is still written.</summary>
		public bool paused
		{
			get => player != null && player.Paused;
			set
			{
				if (player != null)
				{
					player.Paused = value;
				}
			}
		}

		/// <summary>Seconds on the timeline; setting it moves the cursor.</summary>
		public double time
		{
			get => player?.Time ?? 0.0;
			set => player?.Seek(value);
		}

		/// <summary>The playback started.</summary>
		public event Action<Timeline> Started;

		/// <summary>The playback ended or was stopped.</summary>
		public event Action<Timeline> Stopped;

		/// <summary>The cursor reached or jumped to a destination marker (its name).</summary>
		public event Action<string> MarkerReached;

		/// <summary>The cursor stopped at a sustain point (its name), until <see cref="KeyOff" />.</summary>
		public event Action<string> Sustained;

		#endregion

		#region PrivateFields

		[SerializeField] private TimelineAsset m_Asset;
		private TimelinePlayer player;
		private bool autoPlayPending;
		private bool stopRequested;

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			AnimationSystem.Register(this);
			autoPlayPending = playOnAwake;
		}

		public override void OnDisable()
		{
			AnimationSystem.Unregister(this);
			autoPlayPending = false;
			if (player != null && player.IsPlaying)
			{
				Stop();
			}
		}

		public override void OnDestroy()
		{
			AnimationSystem.Unregister(this);
			player = null;
		}

		#endregion

		#region PublicMethods

		/// <summary>Starts (or restarts) at a time of the timeline.</summary>
		public void Play(double from = 0.0)
		{
			autoPlayPending = false;
			if (m_Asset == null)
			{
				return;
			}

			stopRequested = false;
			EnsurePlayer().Play(from);
		}

		/// <summary>Stops: at once, or fading the clips out over <paramref name="fade" /> seconds.</summary>
		public void Stop(double fade = 0.0)
		{
			autoPlayPending = false;
			if (player == null || !player.IsPlaying)
			{
				return;
			}

			stopRequested = true;
			player.Stop(fade);
			if (!player.IsPlaying)
			{
				// Stopped at once: nothing advances any more, so the end is handled here.
				OnPlaybackEnded();
			}
		}

		/// <summary>Moves on from a sustain point.</summary>
		public void KeyOff()
		{
			player?.KeyOff();
		}

		/// <summary>Sets a parameter of the timeline by name (clamped to its range).</summary>
		public void SetParameter(string parameterName, float value, bool ignoreSeek = false)
		{
			if (m_Asset != null)
			{
				EnsurePlayer().SetParameter(parameterName, value, ignoreSeek);
			}
		}

		/// <summary>A parameter's current value (NaN when the timeline has no such parameter).</summary>
		public float GetParameter(string parameterName)
		{
			return m_Asset != null ? EnsurePlayer().GetParameter(parameterName) : float.NaN;
		}

		/// <summary>The object a channel (by name or id) animates, or null.</summary>
		public GameObject GetBinding(string channelNameOrId)
		{
			TimelineChannel channel = FindChannel(channelNameOrId);
			return channel != null ? ResolveTarget(channel) : null;
		}

		/// <summary>Binds a channel (by name or id) to an object: an object spawned at run time, for example.</summary>
		public void SetBinding(string channelNameOrId, GameObject target)
		{
			TimelineChannel channel = FindChannel(channelNameOrId);
			if (channel != null)
			{
				SetBinding(channel, target);
			}
		}

		/// <summary>Binds a channel to an object (null clears it).</summary>
		public void SetBinding(TimelineChannel channel, GameObject target)
		{
			TimelineBinding binding = FindBinding(channel.id);
			if (binding == null)
			{
				binding = new TimelineBinding { channel = channel.id };
				bindings.Add(binding);
			}

			binding.target = target;
			binding.targetId = target != null ? target.Id : 0;
			binding.targetName = target != null ? target.Name : null;
		}

		/// <summary>The binding of a channel, or null.</summary>
		public TimelineBinding FindBinding(string channelId)
		{
			foreach (TimelineBinding binding in bindings)
			{
				if (binding.channel == channelId)
				{
					return binding;
				}
			}

			return null;
		}

		/// <summary>The object of a channel: its binding's reference, else the object with the binding's id in the loaded scenes.</summary>
		public GameObject ResolveTarget(TimelineChannel channel)
		{
			TimelineBinding binding = channel != null ? FindBinding(channel.id) : null;
			if (binding == null)
			{
				return null;
			}

			if (IsAlive(binding.target))
			{
				return binding.target;
			}

			if (binding.targetId != 0)
			{
				GameObject found = gameObject?.scene?.FindObjectById(binding.targetId) ?? SceneManager.FindObjectById(binding.targetId);
				if (found != null)
				{
					binding.target = found;
					return found;
				}
			}

			return null;
		}

		public void OnBeforeSerialize()
		{
			foreach (TimelineBinding binding in bindings)
			{
				if (IsAlive(binding.target))
				{
					binding.targetId = binding.target.Id;
					binding.targetName = binding.target.Name;
				}
			}
		}

		public void OnAfterDeserialize()
		{
			bindings = bindings ?? new List<TimelineBinding>();
		}

		#endregion

		#region InternalMethods

		/// <summary>One frame (called by the scene's animation system).</summary>
		internal void Tick(float deltaTime)
		{
			if (autoPlayPending)
			{
				Play();
			}

			if (player == null || !player.IsPlaying)
			{
				return;
			}

			double dt = ignoreTimeScale && updateMode == AnimatorUpdateMode.Normal ? Time.unscaledDeltaTimeD : deltaTime;
			player.Advance(dt * Math.Max(speed, 0f));
			if (!player.IsPlaying)
			{
				OnPlaybackEnded();
			}
		}

		#endregion

		#region PrivateMethods

		private TimelinePlayer EnsurePlayer()
		{
			if (player == null || player.Asset != m_Asset)
			{
				player = new TimelinePlayer(m_Asset, ResolveTarget);
				player.Notified += OnNotified;
			}

			return player;
		}

		private TimelineChannel FindChannel(string nameOrId)
		{
			if (m_Asset == null)
			{
				return null;
			}

			foreach (TimelineChannel channel in m_Asset.channels)
			{
				if (channel.id == nameOrId || channel.name == nameOrId)
				{
					return channel;
				}
			}

			return null;
		}

		private void OnPlaybackEnded()
		{
			if (endBehaviour == TimelineEndBehaviour.Restore)
			{
				player.Restore();
			}
			else if (endBehaviour == TimelineEndBehaviour.Loop && !stopRequested && isActiveAndEnabled)
			{
				player.Play();
			}
		}

		private void OnNotified(TimelineNotification notification)
		{
			try
			{
				switch (notification.Kind)
				{
					case TimelineNotificationKind.Started:
						Started?.Invoke(this);
						break;
					case TimelineNotificationKind.Stopped:
						Stopped?.Invoke(this);
						break;
					case TimelineNotificationKind.Sustained:
						Sustained?.Invoke(notification.Text);
						break;
					case TimelineNotificationKind.MarkerReached:
						MarkerReached?.Invoke(notification.Text);
						break;
				}
			}
			catch (Exception e)
			{
				Debug.LogException(e, this);
			}

			if (notification.Kind != TimelineNotificationKind.MarkerReached || gameObject == null)
			{
				return;
			}

			IReadOnlyList<Behaviour> components = gameObject.Components;
			for (int i = 0; i < components.Count; i++)
			{
				if (components[i] is ITimelineReceiver receiver && components[i].enabled)
				{
					try
					{
						receiver.OnTimelineMarker(this, notification.Text);
					}
					catch (Exception e)
					{
						Debug.LogException(e, components[i]);
					}
				}
			}
		}

		#endregion
	}
}
