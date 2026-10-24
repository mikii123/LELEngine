using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Audio
{
	/// <summary>Moments in a GameObject's life an emitter or trigger reacts to.</summary>
	public enum EmitterGameEvent
	{
		None,
		ObjectStart,
		ObjectEnable,
		ObjectDisable,
		ObjectDestroy,
		TriggerEnter2D,
		TriggerExit2D,
		CollisionEnter2D,
		CollisionExit2D
	}

	/// <summary>A parameter name with the value to set.</summary>
	[Serializable]
	public sealed class AudioParameterValue
	{
		#region PublicFields

		public string Name = "";
		public float Value;

		#endregion
	}

	/// <summary>
	///     The ears of the game: its position and orientation spatialize every 3D event. One per scene; without any,
	///     the main camera listens.
	/// </summary>
	[InspectorColor(InspectorColorKind.Audio)]
	public sealed class AudioListener : Behaviour
	{
		#region PublicFields

		/// <summary>The listener enabled last.</summary>
		public static AudioListener Current { get; private set; }

		#endregion

		#region PrivateFields

		private Vector3 lastPosition;
		private bool hasLastPosition;

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			Current = this;
			hasLastPosition = false;
		}

		public override void OnDisable()
		{
			if (Current == this)
			{
				Current = null;
			}
		}

		public override void LateUpdate()
		{
			if (Current != this)
			{
				return;
			}

			Vector3 position = transform.position;
			Vector3 velocity = hasLastPosition && Time.deltaTime > 0f ? (position - lastPosition) / Time.deltaTime : Vector3.Zero;
			lastPosition = position;
			hasLastPosition = true;
			AudioSystem.SetListener(position, transform.forward, transform.up, velocity);
		}

		#endregion
	}

	/// <summary>
	///     Plays an event at the GameObject (FMOD's StudioEventEmitter): started and stopped by the chosen game events
	///     or from scripts (<see cref="Play" />, <see cref="Stop" />), its 3D attributes following the transform, with
	///     initial parameter values.
	/// </summary>
	[InspectorColor(InspectorColorKind.Audio)]
	public sealed class AudioEventEmitter : Behaviour
	{
		#region PublicFields

		[Tooltip("The event to play.")] public AudioEventReference Event;

		[Tooltip("When the event starts.")] public EmitterGameEvent PlayEvent = EmitterGameEvent.ObjectStart;

		[Tooltip("When the event stops.")] public EmitterGameEvent StopEvent = EmitterGameEvent.ObjectDestroy;

		[Tooltip("Stop lets the event fade out (its envelope's release) instead of cutting it.")] public bool AllowFadeout = true;

		[Tooltip("Play only once per emitter, whatever triggers it again.")] public bool TriggerOnce;

		[Range(0f, 2f)] [Tooltip("Volume as a gain on top of the event's own.")] public float Volume = 1f;

		[Range(0.25f, 4f)] [Tooltip("Pitch as a ratio on top of the event's own.")] public float Pitch = 1f;

		[Tooltip("Parameter values set when the event starts.")] public List<AudioParameterValue> Parameters = new List<AudioParameterValue>();

		/// <summary>The playing instance (null before the first play and after a stop completed).</summary>
		public EventInstance Instance => instance != null && instance.IsValid ? instance : null;

		public bool IsPlaying => instance != null && instance.IsValid && instance.IsPlaying;

		#endregion

		#region PrivateFields

		[NonSerialized] private EventInstance instance;
		[NonSerialized] private bool triggered;
		[NonSerialized] private Vector3 lastPosition;
		[NonSerialized] private bool hasLastPosition;

		#endregion

		#region UnityMethods

		public override void Start()
		{
			HandleGameEvent(EmitterGameEvent.ObjectStart);
		}

		public override void OnEnable()
		{
			HandleGameEvent(EmitterGameEvent.ObjectEnable);
		}

		public override void OnDisable()
		{
			HandleGameEvent(EmitterGameEvent.ObjectDisable);
		}

		public override void OnDestroy()
		{
			HandleGameEvent(EmitterGameEvent.ObjectDestroy);
			if (instance != null)
			{
				if (StopEvent == EmitterGameEvent.None)
				{
					// The sound outlives the emitter (one-shots on destroyed objects).
					instance.Release();
				}
				else
				{
					instance.Stop(AllowFadeout ? AudioStopMode.AllowFadeout : AudioStopMode.Immediate);
					instance.Release();
				}

				instance = null;
			}
		}

		public override void LateUpdate()
		{
			if (instance == null || !instance.IsValid)
			{
				return;
			}

			Update3D();
		}

		public override void OnTriggerEnter2D(Collider2D other)
		{
			HandleGameEvent(EmitterGameEvent.TriggerEnter2D);
		}

		public override void OnTriggerExit2D(Collider2D other)
		{
			HandleGameEvent(EmitterGameEvent.TriggerExit2D);
		}

		public override void OnCollisionEnter2D(Collision2D collision)
		{
			HandleGameEvent(EmitterGameEvent.CollisionEnter2D);
		}

		public override void OnCollisionExit2D(Collision2D collision)
		{
			HandleGameEvent(EmitterGameEvent.CollisionExit2D);
		}

		public override void OnValidate()
		{
			if (instance != null && instance.IsValid)
			{
				instance.SetVolume(Volume);
				instance.SetPitch(Pitch);
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Starts the event (restarting it when it plays).</summary>
		public void Play()
		{
			if (Event.IsNull || !AudioSystem.IsInitialized)
			{
				return;
			}

			if (TriggerOnce && triggered)
			{
				return;
			}

			triggered = true;
			if (instance == null || !instance.IsValid)
			{
				instance = AudioSystem.CreateInstance(Event);
				if (instance == null)
				{
					return;
				}
			}

			foreach (AudioParameterValue parameter in Parameters)
			{
				if (parameter != null && !string.IsNullOrEmpty(parameter.Name))
				{
					instance.SetParameter(parameter.Name, parameter.Value, true);
				}
			}

			instance.SetVolume(Volume);
			instance.SetPitch(Pitch);
			hasLastPosition = false;
			Update3D();
			instance.Start();
		}

		public void Stop()
		{
			instance?.Stop(AllowFadeout ? AudioStopMode.AllowFadeout : AudioStopMode.Immediate);
		}

		/// <summary>Sets a parameter on the playing instance and remembers it for the next play.</summary>
		public void SetParameter(string parameterName, float value, bool ignoreSeekSpeed = false)
		{
			bool found = false;
			foreach (AudioParameterValue parameter in Parameters)
			{
				if (parameter != null && string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase))
				{
					parameter.Value = value;
					found = true;
				}
			}

			if (!found)
			{
				Parameters.Add(new AudioParameterValue { Name = parameterName, Value = value });
			}

			if (instance != null && instance.IsValid)
			{
				instance.SetParameter(parameterName, value, ignoreSeekSpeed);
			}
		}

		#endregion

		#region PrivateMethods

		private void HandleGameEvent(EmitterGameEvent gameEvent)
		{
			if (gameEvent == EmitterGameEvent.None)
			{
				return;
			}

			if (PlayEvent == gameEvent)
			{
				Play();
			}

			if (StopEvent == gameEvent)
			{
				Stop();
			}
		}

		private void Update3D()
		{
			if (instance == null || !instance.IsValid || !instance.Is3D || transform == null)
			{
				return;
			}

			Vector3 position = transform.position;
			Vector3 velocity = hasLastPosition && Time.deltaTime > 0f ? (position - lastPosition) / Time.deltaTime : Vector3.Zero;
			lastPosition = position;
			hasLastPosition = true;
			instance.Set3DAttributes(position, velocity, transform.forward, transform.up);
		}

		#endregion
	}

	/// <summary>Loads (and optionally unloads) banks with the scene (FMOD's StudioBankLoader).</summary>
	[InspectorColor(InspectorColorKind.Audio)]
	public sealed class AudioBankLoader : Behaviour
	{
		#region PublicFields

		[Tooltip("Names of the banks to load.")] public List<string> Banks = new List<string>();

		[Tooltip("Load when the object starts (else call Load).")] public bool LoadOnStart = true;

		[Tooltip("Load in the background; events of the banks are available a little later.")] public bool LoadInBackground = true;

		[Tooltip("Unload the banks when the object is destroyed (the scene unloads).")] public bool UnloadOnDestroy;

		#endregion

		#region UnityMethods

		public override void Start()
		{
			if (LoadOnStart)
			{
				Load();
			}
		}

		public override void OnDestroy()
		{
			if (UnloadOnDestroy)
			{
				foreach (string bank in Banks)
				{
					if (!string.IsNullOrWhiteSpace(bank))
					{
						AudioSystem.UnloadBank(bank.Trim());
					}
				}
			}
		}

		#endregion

		#region PublicMethods

		public void Load()
		{
			foreach (string bank in Banks)
			{
				if (string.IsNullOrWhiteSpace(bank))
				{
					continue;
				}

				if (LoadInBackground)
				{
					AudioSystem.LoadBankAsync(bank.Trim());
				}
				else
				{
					AudioSystem.LoadBank(bank.Trim());
				}
			}
		}

		#endregion
	}

	/// <summary>Starts a mixer snapshot when a game event happens and stops it on another (a quiet room, a pause menu, underwater).</summary>
	[InspectorColor(InspectorColorKind.Audio)]
	public sealed class AudioSnapshotTrigger : Behaviour
	{
		#region PublicFields

		[Tooltip("The snapshot's name in the audio project.")] public string Snapshot = "";

		[Tooltip("When the snapshot fades in.")] public EmitterGameEvent StartEvent = EmitterGameEvent.ObjectEnable;

		[Tooltip("When the snapshot fades out.")] public EmitterGameEvent StopEvent = EmitterGameEvent.ObjectDisable;

		[Range(0f, 1f)] [Tooltip("How much of the snapshot applies.")] public float Intensity = 1f;

		/// <summary>The running snapshot (null before the start and after it faded out).</summary>
		public SnapshotInstance Instance => instance != null && instance.IsActive ? instance : null;

		#endregion

		#region PrivateFields

		[NonSerialized] private SnapshotInstance instance;

		#endregion

		#region UnityMethods

		public override void Start()
		{
			Handle(EmitterGameEvent.ObjectStart);
		}

		public override void OnEnable()
		{
			Handle(EmitterGameEvent.ObjectEnable);
		}

		public override void OnDisable()
		{
			Handle(EmitterGameEvent.ObjectDisable);
		}

		public override void OnDestroy()
		{
			Handle(EmitterGameEvent.ObjectDestroy);
			if (instance != null && instance.IsActive && StopEvent != EmitterGameEvent.None)
			{
				instance.Stop();
			}

			instance = null;
		}

		public override void OnTriggerEnter2D(Collider2D other)
		{
			Handle(EmitterGameEvent.TriggerEnter2D);
		}

		public override void OnTriggerExit2D(Collider2D other)
		{
			Handle(EmitterGameEvent.TriggerExit2D);
		}

		public override void OnCollisionEnter2D(Collision2D collision)
		{
			Handle(EmitterGameEvent.CollisionEnter2D);
		}

		public override void OnCollisionExit2D(Collision2D collision)
		{
			Handle(EmitterGameEvent.CollisionExit2D);
		}

		public override void OnValidate()
		{
			instance?.SetIntensity(Intensity);
		}

		#endregion

		#region PublicMethods

		public void StartSnapshot()
		{
			if (string.IsNullOrEmpty(Snapshot) || !AudioSystem.IsInitialized)
			{
				return;
			}

			if (instance != null && instance.IsActive)
			{
				instance.SetIntensity(Intensity);
				return;
			}

			instance = AudioSystem.StartSnapshot(Snapshot, Intensity);
		}

		public void StopSnapshot()
		{
			instance?.Stop();
		}

		#endregion

		#region PrivateMethods

		private void Handle(EmitterGameEvent gameEvent)
		{
			if (gameEvent == EmitterGameEvent.None)
			{
				return;
			}

			if (StartEvent == gameEvent)
			{
				StartSnapshot();
			}

			if (StopEvent == gameEvent)
			{
				StopSnapshot();
			}
		}

		#endregion
	}

	/// <summary>Sets parameters (of an emitter's event, or global ones) when a game event happens (FMOD's StudioParameterTrigger).</summary>
	[InspectorColor(InspectorColorKind.Audio)]
	public sealed class AudioParameterTrigger : Behaviour
	{
		#region PublicFields

		[Tooltip("The emitter whose event gets the values; none: global parameters.")] public AudioEventEmitter Emitter;

		[Tooltip("When the values are set.")] public EmitterGameEvent TriggerEvent = EmitterGameEvent.ObjectStart;

		public List<AudioParameterValue> Parameters = new List<AudioParameterValue>();

		#endregion

		#region UnityMethods

		public override void Start()
		{
			Handle(EmitterGameEvent.ObjectStart);
		}

		public override void OnEnable()
		{
			Handle(EmitterGameEvent.ObjectEnable);
		}

		public override void OnDisable()
		{
			Handle(EmitterGameEvent.ObjectDisable);
		}

		public override void OnDestroy()
		{
			Handle(EmitterGameEvent.ObjectDestroy);
		}

		public override void OnTriggerEnter2D(Collider2D other)
		{
			Handle(EmitterGameEvent.TriggerEnter2D);
		}

		public override void OnTriggerExit2D(Collider2D other)
		{
			Handle(EmitterGameEvent.TriggerExit2D);
		}

		public override void OnCollisionEnter2D(Collision2D collision)
		{
			Handle(EmitterGameEvent.CollisionEnter2D);
		}

		public override void OnCollisionExit2D(Collision2D collision)
		{
			Handle(EmitterGameEvent.CollisionExit2D);
		}

		#endregion

		#region PublicMethods

		/// <summary>Sets the values now.</summary>
		public void Trigger()
		{
			foreach (AudioParameterValue parameter in Parameters)
			{
				if (parameter == null || string.IsNullOrEmpty(parameter.Name))
				{
					continue;
				}

				if (Emitter != null && !Emitter.IsDestroyed)
				{
					Emitter.SetParameter(parameter.Name, parameter.Value);
				}
				else
				{
					AudioSystem.SetGlobalParameter(parameter.Name, parameter.Value);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void Handle(EmitterGameEvent gameEvent)
		{
			if (TriggerEvent == gameEvent && gameEvent != EmitterGameEvent.None)
			{
				Trigger();
			}
		}

		#endregion
	}
}
