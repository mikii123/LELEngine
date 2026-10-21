using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using OpenTK.Mathematics;

namespace LELEngine.Audio
{
	/// <summary>What the game (main thread) asks the mixer (audio thread) to do.</summary>
	public enum MixerCommandKind
	{
		RegisterEvent,
		UnregisterEvent,
		UnregisterBank,
		SetGlobals,
		CreateInstance,
		StartInstance,
		StopInstance,
		ReleaseInstance,
		DestroyInstance,
		SetInstanceParameter,
		SetInstance3D,
		SetInstanceVolume,
		SetInstancePitch,
		SetInstancePaused,
		KeyOff,
		SetTimelinePosition,
		SetGlobalParameter,
		SetBusVolume,
		SetBusMute,
		SetListener,
		StopAll,
		PauseAll,
		SetMaxVoices,
		SetMasterVolume,
		StartSnapshot,
		StopSnapshot,
		SetSnapshotIntensity,
		StopAllSnapshots,
		SetDoppler
	}

	public struct MixerCommand
	{
		public MixerCommandKind Kind;
		public int Id;
		public int IntValue;
		public float Value;
		public bool Flag;
		public object Payload;
		public Vector3 V0;
		public Vector3 V1;
		public Vector3 V2;
		public Vector3 V3;
	}

	public enum AudioNotificationKind
	{
		Started,
		Stopped,

		/// <summary>The instance did not start (polyphony with <see cref="StealingMode.None" />, or an unknown event).</summary>
		StartRefused,
		MarkerReached,
		Beat,
		Sustained,

		/// <summary>A snapshot started by the game faded out (InstanceId is the snapshot handle's id).</summary>
		SnapshotStopped
	}

	/// <summary>What the mixer tells the game: delivered on the main thread by <see cref="AudioSystem.Update" />.</summary>
	public struct AudioNotification
	{
		public AudioNotificationKind Kind;
		public int InstanceId;
		public int Bar;
		public int Beat;
		public float Tempo;
		public string Text;
		public double Position;
	}

	/// <summary>Global parameters and buses as the mixer takes them (<see cref="MixerCommandKind.SetGlobals" />).</summary>
	public sealed class MixerGlobals
	{
		public List<AudioParameter> Parameters = new List<AudioParameter>();

		/// <summary>In tree order, parents before children (<see cref="AudioProject.BusesInTreeOrder" />).</summary>
		public List<AudioBus> Buses = new List<AudioBus>();

		/// <summary>In priority order (the first wins).</summary>
		public List<AudioSnapshot> Snapshots = new List<AudioSnapshot>();

		public int MaxVoices = 64;
	}

	/// <summary>An active snapshot in the mixer: its definition, its intensity and the envelope fading it in and out.</summary>
	public sealed class SnapshotState
	{
		#region PublicFields

		/// <summary>The game's handle id (0 for snapshots an instrument owns).</summary>
		public int Id;

		public AudioSnapshot Definition;

		/// <summary>Index in the project's list: lower wins.</summary>
		public int Priority;

		/// <summary>Set by the game, 0..1.</summary>
		public float Intensity = 1f;

		public EnvelopeRunner Envelope;

		/// <summary>Envelope times intensity this block.</summary>
		public float Level;

		public bool Done;

		/// <summary>Started by an event instrument: no notification when it ends.</summary>
		public bool Owned;

		#endregion
	}

	/// <summary>Attack-hold-decay-sustain-release progression of one voice or instance.</summary>
	public struct EnvelopeRunner
	{
		#region PublicFields

		public enum Stage
		{
			Attack,
			Hold,
			Decay,
			Sustain,
			Release,
			Done
		}

		public Stage Current;
		public double Time;
		public float Level;
		public float ReleaseFrom;
		public float ReleaseSeconds;
		public float AttackOverride;

		#endregion

		#region PublicMethods

		/// <summary>Starts at silence (attack) or at full level when the envelope has no attack.</summary>
		public static EnvelopeRunner Start(AudioEnvelope envelope, float attackOverride)
		{
			var runner = new EnvelopeRunner { Current = Stage.Attack, Time = 0.0, Level = 0f, AttackOverride = attackOverride, ReleaseSeconds = -1f };
			float attack = Math.Max(envelope?.Attack ?? 0f, attackOverride);
			if (attack <= 0f)
			{
				runner.Level = 1f;
				runner.Current = Stage.Hold;
			}

			return runner;
		}

		public bool IsDone => Current == Stage.Done;
		public bool IsReleasing => Current == Stage.Release || Current == Stage.Done;

		/// <summary>Enters the release (<paramref name="seconds" /> overrides the envelope's release; -1 keeps it).</summary>
		public void Release(float seconds)
		{
			if (Current == Stage.Release || Current == Stage.Done)
			{
				if (seconds >= 0f && (ReleaseSeconds < 0f || seconds < ReleaseSeconds))
				{
					// A faster release asked for mid-release: continue from the current level.
					ReleaseFrom = Level;
					Time = 0.0;
					ReleaseSeconds = seconds;
				}

				return;
			}

			Current = Stage.Release;
			ReleaseFrom = Level;
			Time = 0.0;
			ReleaseSeconds = seconds;
		}

		/// <summary>Advances by <paramref name="seconds" /> and returns the level after it.</summary>
		public float Advance(AudioEnvelope envelope, double seconds, float minimumRelease)
		{
			float attack = Math.Max(envelope?.Attack ?? 0f, AttackOverride);
			float hold = envelope?.Hold ?? 0f;
			float decay = envelope?.Decay ?? 0f;
			float sustain = envelope == null ? 1f : AudioMath.DbToGain(Math.Min(0f, envelope.SustainDb));
			float release = ReleaseSeconds >= 0f ? ReleaseSeconds : envelope?.Release ?? 0f;
			release = Math.Max(release, minimumRelease);
			Time += seconds;
			switch (Current)
			{
				case Stage.Attack:
					if (attack <= 0f || Time >= attack)
					{
						Level = 1f;
						Time = attack > 0f ? Time - attack : 0.0;
						Current = Stage.Hold;
						goto case Stage.Hold;
					}

					Level = (float)(Time / attack);
					break;
				case Stage.Hold:
					Level = 1f;
					if (Time >= hold)
					{
						Time -= hold;
						Current = Stage.Decay;
						goto case Stage.Decay;
					}

					break;
				case Stage.Decay:
					if (decay <= 0f || Time >= decay)
					{
						Level = sustain;
						Current = Stage.Sustain;
						break;
					}

					Level = 1f + (sustain - 1f) * (float)(Time / decay);
					break;
				case Stage.Sustain:
					Level = sustain;
					break;
				case Stage.Release:
					if (release <= 0f || Time >= release)
					{
						Level = 0f;
						Current = Stage.Done;
						break;
					}

					Level = ReleaseFrom * (1f - (float)(Time / release));
					break;
				case Stage.Done:
					Level = 0f;
					break;
			}

			return Level;
		}

		#endregion
	}

	/// <summary>One playing sound: a sample read at a pitch through gains and an envelope into its track's buffer.</summary>
	public sealed class Voice
	{
		#region PublicFields

		public SampleData Sample;
		public StreamingReader Stream;
		public double Position;

		/// <summary>Pitch of the trigger (instrument, randomization, playlist entry) as a ratio.</summary>
		public float BaseRatio = 1f;

		/// <summary>Source frames per output frame this block.</summary>
		public double Step = 1.0;

		public bool Loop;
		public float Gain = 1f;
		public float PanLeft = 1f;
		public float PanRight = 1f;

		/// <summary>Gain of a transition's source or destination fade (1 outside transitions).</summary>
		public float FadeGain = 1f;

		public float CurrentLeft;
		public float CurrentRight;
		public bool GainsInitialized;
		public EnvelopeRunner Envelope;
		public AudioEnvelope EnvelopeShape;
		public int Instrument;
		public int Track;
		public bool Finished;
		public bool Virtual;
		public bool HasOffset;
		public Vector3 Offset;
		public long StartedAt;

		/// <summary>Loudness estimate for the voice limit.</summary>
		public float Audibility;

		#endregion

		#region PublicMethods

		public void Reset()
		{
			Stream?.Dispose();
			Stream = null;
			Sample = null;
			Position = 0.0;
			BaseRatio = 1f;
			Step = 1.0;
			Loop = false;
			Gain = 1f;
			PanLeft = 1f;
			PanRight = 1f;
			FadeGain = 1f;
			CurrentLeft = 0f;
			CurrentRight = 0f;
			GainsInitialized = false;
			Envelope = default;
			EnvelopeShape = null;
			Instrument = -1;
			Track = -1;
			Finished = false;
			Virtual = false;
			HasOffset = false;
			Offset = Vector3.Zero;
			StartedAt = 0;
			Audibility = 0f;
		}

		#endregion
	}

	/// <summary>An effect of the model run by its DSP processor (configured from the model's values every block).</summary>
	public sealed class EffectRunner
	{
		#region PublicFields

		public AudioEffect Effect;

		#endregion

		#region PrivateFields

		private BiquadFilter filter;
		private ReverbProcessor reverb;
		private DelayProcessor delay;
		private float a, b, c, d, e;
		private bool configured;

		#endregion

		#region Constructors

		public EffectRunner(AudioEffect effect)
		{
			Effect = effect;
			switch (effect)
			{
				case LowPassEffect _:
				case HighPassEffect _:
					filter = new BiquadFilter(2);
					break;
				case ReverbEffect _:
					reverb = new ReverbProcessor(AudioOutput.SampleRate);
					break;
				case DelayEffect _:
					delay = new DelayProcessor(AudioOutput.SampleRate, 5f);
					break;
			}
		}

		#endregion

		#region PublicMethods

		public void Process(float[] buffer, int frames)
		{
			if (Effect == null || Effect.Bypass || frames <= 0)
			{
				return;
			}

			switch (Effect)
			{
				case LowPassEffect low:
					if (!configured || a != low.CutoffHz || b != low.Q)
					{
						a = low.CutoffHz;
						b = low.Q;
						configured = true;
						filter.Configure(BiquadType.LowPass, a, b, 0f, AudioOutput.SampleRate);
					}

					if (!filter.IsIdentity)
					{
						filter.Process(buffer, 0, frames, 2);
					}

					break;
				case HighPassEffect high:
					if (!configured || a != high.CutoffHz || b != high.Q)
					{
						a = high.CutoffHz;
						b = high.Q;
						configured = true;
						filter.Configure(BiquadType.HighPass, a, b, 0f, AudioOutput.SampleRate);
					}

					if (!filter.IsIdentity)
					{
						filter.Process(buffer, 0, frames, 2);
					}

					break;
				case ReverbEffect verb:
					if (!configured || a != verb.RoomSize || b != verb.Damping || c != verb.Width || d != verb.WetDb || e != verb.DryDb)
					{
						a = verb.RoomSize;
						b = verb.Damping;
						c = verb.Width;
						d = verb.WetDb;
						e = verb.DryDb;
						configured = true;
						reverb.SetParameters(a, b, c, d, e);
					}

					reverb.Process(buffer, 0, frames);
					break;
				case DelayEffect echo:
					if (!configured || a != echo.DelaySeconds || b != echo.Feedback || c != echo.WetDb || d != echo.DryDb || e != echo.FeedbackLowPassHz)
					{
						a = echo.DelaySeconds;
						b = echo.Feedback;
						c = echo.WetDb;
						d = echo.DryDb;
						e = echo.FeedbackLowPassHz;
						configured = true;
						delay.SetParameters(a, b, c, d, e);
					}

					delay.Process(buffer, 0, frames);
					break;
			}
		}

		#endregion
	}

	/// <summary>A bus of the mixer: its block buffer, gain, mute, effects and meter.</summary>
	public sealed class BusState
	{
		#region PublicFields

		public string Id;
		public string Name;
		public int Parent = -1;
		public float[] Buffer = new float[Mixer.BlockFrames * 2];
		public float TargetGain = 1f;
		public float CurrentGain = 1f;
		public float GameGain = 1f;
		public bool Mute;
		public bool GameMute;
		public EffectRunner[] Effects = Array.Empty<EffectRunner>();

		/// <summary>The bus definition's volume; snapshots build on it every block.</summary>
		public float BaseVolumeDb;

		/// <summary>The bus definition's effects, one per runner: the values snapshots start from.</summary>
		public AudioEffect[] BaseEffects = Array.Empty<AudioEffect>();

		public volatile float PeakLeft;
		public volatile float PeakRight;

		/// <summary>The volume after the snapshots, decibels (what the mixer panel shows).</summary>
		public volatile float EffectiveVolumeDb;

		#endregion
	}

	/// <summary>
	///     The audio thread's mixer: runs every event instance (<see cref="EventPlayback" />), mixes their voices through
	///     their tracks into the buses and the buses into the output, in sub-blocks of <see cref="BlockFrames" />.
	///     Everything it holds is touched by the audio thread only; the game talks to it through
	///     <see cref="Post" /> (commands) and reads its published fields and <see cref="Notifications" />.
	/// </summary>
	public sealed class Mixer : IAudioRenderer
	{
		#region PublicFields

		public const int BlockFrames = 256;
		public const int SampleRate = AudioOutput.SampleRate;
		public const double SecondsPerBlock = BlockFrames / (double)SampleRate;

		/// <summary>Shortest fade applied when a voice stops, so nothing clicks.</summary>
		public const float MinimumRelease = 0.004f;

		public readonly ConcurrentQueue<AudioNotification> Notifications = new ConcurrentQueue<AudioNotification>();

		/// <summary>Frames rendered so far (the audio clock).</summary>
		public long Time => Interlocked.Read(ref time);

		public volatile int ActiveVoices;
		public volatile int VirtualVoices;
		public volatile int ActiveInstances;
		public volatile float MasterPeakLeft;
		public volatile float MasterPeakRight;
		public volatile int StreamUnderruns;

		/// <summary>Bus states in tree order (read by the editor's meters; the array is replaced, never edited, after creation).</summary>
		public BusState[] Buses => buses;

		public int MaxVoices = 64;

		/// <summary>Speed of sound (world units per second) and the global Doppler factor.</summary>
		public float SpeedOfSound = 343f;

		public float DopplerScale = 1f;

		/// <summary>Active snapshots (read by the editor's meters; replaced, never edited, on the game thread).</summary>
		public int ActiveSnapshotCount => snapshots.Count;

		#endregion

		#region PrivateFields

		private readonly ConcurrentQueue<MixerCommand> commands = new ConcurrentQueue<MixerCommand>();
		private readonly List<SnapshotState> snapshots = new List<SnapshotState>();
		private readonly List<SnapshotState> snapshotOrder = new List<SnapshotState>();
		private readonly Dictionary<string, AudioSnapshot> snapshotDefinitions = new Dictionary<string, AudioSnapshot>(StringComparer.Ordinal);
		private readonly Dictionary<string, int> snapshotPriorities = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly List<TouchedEffectValue> touchedEffects = new List<TouchedEffectValue>();
		private bool snapshotsWereActive;
		private readonly Comparison<SnapshotState> byPriorityDescending = (a, b) => b.Priority.CompareTo(a.Priority);
		private readonly Dictionary<string, CompiledEvent> eventsByGuid = new Dictionary<string, CompiledEvent>(StringComparer.Ordinal);
		private readonly Dictionary<string, CompiledEvent> eventsByPath = new Dictionary<string, CompiledEvent>(StringComparer.OrdinalIgnoreCase);
		private readonly List<EventPlayback> instances = new List<EventPlayback>();
		private readonly Dictionary<int, EventPlayback> instancesById = new Dictionary<int, EventPlayback>();
		private readonly Stack<Voice> voicePool = new Stack<Voice>();
		private readonly List<Voice> voiceScratch = new List<Voice>();
		private readonly Comparison<Voice> byAudibility = (a, b) => b.Audibility.CompareTo(a.Audibility);
		private BusState[] buses = Array.Empty<BusState>();
		private readonly float[] fetch = new float[(4096 + 8) * 2];
		private readonly float[] masterBlock = new float[BlockFrames * 2];
		private readonly List<GlobalParameterState> globals = new List<GlobalParameterState>();
		private readonly Dictionary<string, int> globalIndex = new Dictionary<string, int>(StringComparer.Ordinal);
		private long time;
		private float masterGain = 1f;
		private float masterCurrent = 1f;
		private bool pausedAll;
		private int randomSeed = 12345;

		// Listener.
		private Vector3 listenerPosition;
		private Vector3 listenerForward = -Vector3.UnitZ;
		private Vector3 listenerUp = Vector3.UnitY;
		private Vector3 listenerRight = Vector3.UnitX;
		private Vector3 listenerVelocity;

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- game thread

		/// <summary>Queues a command for the audio thread.</summary>
		public void Post(MixerCommand command)
		{
			commands.Enqueue(command);
		}

		public void Post(MixerCommandKind kind, int id = 0, float value = 0f, bool flag = false, object payload = null, int intValue = 0)
		{
			commands.Enqueue(new MixerCommand { Kind = kind, Id = id, Value = value, Flag = flag, Payload = payload, IntValue = intValue });
		}

		// ---------------------------------------------------------------- audio thread

		public void Render(float[] buffer, int frames)
		{
			int done = 0;
			while (done < frames)
			{
				int count = Math.Min(BlockFrames, frames - done);
				DrainCommands();
				AdvanceGlobals(count);
				AdvanceSnapshots(count);
				for (int i = 0; i < buses.Length; i++)
				{
					Array.Clear(buses[i].Buffer, 0, count * 2);
				}

				if (!pausedAll)
				{
					LimitVoices();
				}

				int voices = 0;
				int virtuals = 0;
				for (int i = 0; i < instances.Count; i++)
				{
					EventPlayback instance = instances[i];
					try
					{
						instance.Process(this, count, pausedAll);
					}
					catch (Exception e)
					{
						instance.Fail(this, e);
					}

					voices += instance.VoiceCount;
					virtuals += instance.VirtualVoiceCount;
				}

				ActiveVoices = voices;
				VirtualVoices = virtuals;
				ProcessBuses(count);
				Array.Copy(masterBlock, 0, buffer, done * 2, count * 2);

				// Finished instances leave (their handles learn it through the notification).
				for (int i = instances.Count - 1; i >= 0; i--)
				{
					EventPlayback instance = instances[i];
					if (instance.IsFinished)
					{
						instance.Destroy(this);
						instances.RemoveAt(i);
						instancesById.Remove(instance.Id);
					}
				}

				ActiveInstances = instances.Count;
				Interlocked.Add(ref time, count);
				done += count;
			}
		}

		/// <summary>The compiled event of a GUID or path (nested instruments, the game).</summary>
		public CompiledEvent FindEvent(AudioEventReference reference)
		{
			CompiledEvent result;
			if (!string.IsNullOrEmpty(reference.Guid) && eventsByGuid.TryGetValue(reference.Guid, out result))
			{
				return result;
			}

			if (!string.IsNullOrEmpty(reference.Path) && eventsByPath.TryGetValue(reference.Path, out result))
			{
				return result;
			}

			return null;
		}

		public Voice RentVoice()
		{
			Voice voice = voicePool.Count > 0 ? voicePool.Pop() : new Voice();
			voice.Reset();
			voice.StartedAt = time;
			return voice;
		}

		public void ReturnVoice(Voice voice)
		{
			voice.Reset();
			if (voicePool.Count < 512)
			{
				voicePool.Push(voice);
			}
		}

		public Random NewRandom()
		{
			randomSeed = randomSeed * 1103515245 + 12345;
			return new Random(randomSeed ^ (int)time);
		}

		public void Notify(AudioNotification notification)
		{
			if (Notifications.Count < 4096)
			{
				Notifications.Enqueue(notification);
			}
		}

		/// <summary>Index of a bus by id (the master bus when unknown).</summary>
		public int FindBus(string id)
		{
			for (int i = 0; i < buses.Length; i++)
			{
				if (buses[i].Id == id)
				{
					return i;
				}
			}

			return buses.Length > 0 ? 0 : -1;
		}

		public BusState Bus(int index)
		{
			return index >= 0 && index < buses.Length ? buses[index] : null;
		}

		/// <summary>Current value of a global parameter by id (NaN when unknown).</summary>
		public float GlobalValue(string id)
		{
			int index;
			return id != null && globalIndex.TryGetValue(id, out index) ? globals[index].Current : float.NaN;
		}

		public Vector3 ListenerPosition => listenerPosition;
		public Vector3 ListenerForward => listenerForward;
		public Vector3 ListenerUp => listenerUp;
		public Vector3 ListenerRight => listenerRight;
		public Vector3 ListenerVelocity => listenerVelocity;

		/// <summary>Instances of an event playing now (polyphony).</summary>
		public int CountInstances(CompiledEvent compiled, List<EventPlayback> result)
		{
			result.Clear();
			foreach (EventPlayback instance in instances)
			{
				if (instance.Event.Id == compiled.Id && instance.IsPlaying)
				{
					result.Add(instance);
				}
			}

			return result.Count;
		}

		/// <summary>Audio thread: starts a snapshot an event instrument owns (null when the snapshot is unknown).</summary>
		public SnapshotState StartOwnedSnapshot(string definitionId)
		{
			AudioSnapshot definition;
			if (definitionId == null || !snapshotDefinitions.TryGetValue(definitionId, out definition))
			{
				return null;
			}

			return StartSnapshotState(0, definition, 1f, true);
		}

		/// <summary>Audio thread: fades an owned snapshot out.</summary>
		public void StopOwnedSnapshot(SnapshotState state, bool immediate)
		{
			if (state != null && !state.Done)
			{
				state.Envelope.Release(immediate ? 0.002f : -1f);
			}
		}

		/// <summary>Starts a nested event's instance for an event instrument (audio thread).</summary>
		public EventPlayback CreateNested(CompiledEvent compiled, EventPlayback parent)
		{
			var nested = new EventPlayback(parent.AllocateNestedId(), compiled, parent.Depth + 1, NewRandom(), this) { Parent = parent, ReleaseWhenStopped = true };
			instances.Add(nested);
			instancesById[nested.Id] = nested;
			return nested;
		}

		/// <summary>
		///     Reads frames [<paramref name="start" />, start + count) of a voice's source as stereo floats into the shared
		///     fetch buffer and returns it.
		/// </summary>
		public float[] Fetch(Voice voice, long start, int count)
		{
			if (voice.Stream != null)
			{
				voice.Stream.Fetch(start, count, fetch);
			}
			else
			{
				voice.Sample.Fetch(start, count, fetch, voice.Loop);
			}

			return fetch;
		}

		public int FetchCapacityFrames => fetch.Length / 2;

		#endregion

		#region PrivateMethods

		private void DrainCommands()
		{
			MixerCommand command;
			while (commands.TryDequeue(out command))
			{
				try
				{
					Execute(command);
				}
				catch (Exception e)
				{
					Debug.LogWarning("[Audio] Command " + command.Kind + " failed: " + e.Message);
				}
			}
		}

		private void Execute(MixerCommand command)
		{
			EventPlayback instance;
			switch (command.Kind)
			{
				case MixerCommandKind.RegisterEvent:
				{
					var compiled = (CompiledEvent)command.Payload;
					CompiledEvent previous;
					if (eventsByGuid.TryGetValue(compiled.Id, out previous) && !string.IsNullOrEmpty(previous.Path))
					{
						eventsByPath.Remove(previous.Path);
					}

					eventsByGuid[compiled.Id] = compiled;
					if (!string.IsNullOrEmpty(compiled.Path))
					{
						eventsByPath[compiled.Path] = compiled;
					}

					// Live update: instances of the event take the new description.
					foreach (EventPlayback playing in instances)
					{
						if (playing.Event.Id == compiled.Id && playing.Event != compiled)
						{
							playing.ReplaceEvent(this, compiled);
						}
					}

					break;
				}
				case MixerCommandKind.UnregisterEvent:
				{
					var guid = (string)command.Payload;
					CompiledEvent compiled;
					if (guid != null && eventsByGuid.TryGetValue(guid, out compiled))
					{
						eventsByGuid.Remove(guid);
						if (!string.IsNullOrEmpty(compiled.Path))
						{
							eventsByPath.Remove(compiled.Path);
						}

						foreach (EventPlayback playing in instances)
						{
							if (playing.Event == compiled)
							{
								playing.Stop(this, AudioStopMode.AllowFadeout);
							}
						}
					}

					break;
				}
				case MixerCommandKind.UnregisterBank:
				{
					var bankName = (string)command.Payload;
					var remove = new List<string>();
					foreach (KeyValuePair<string, CompiledEvent> entry in eventsByGuid)
					{
						if (string.Equals(entry.Value.BankName, bankName, StringComparison.OrdinalIgnoreCase))
						{
							remove.Add(entry.Key);
						}
					}

					foreach (string guid in remove)
					{
						CompiledEvent compiled = eventsByGuid[guid];
						eventsByGuid.Remove(guid);
						if (!string.IsNullOrEmpty(compiled.Path))
						{
							eventsByPath.Remove(compiled.Path);
						}

						foreach (EventPlayback playing in instances)
						{
							if (playing.Event == compiled)
							{
								playing.Stop(this, AudioStopMode.Immediate);
							}
						}
					}

					break;
				}
				case MixerCommandKind.SetGlobals:
					ApplyGlobals((MixerGlobals)command.Payload);
					break;
				case MixerCommandKind.CreateInstance:
				{
					instance = (EventPlayback)command.Payload;
					instance.Random = NewRandom();
					instances.Add(instance);
					instancesById[instance.Id] = instance;
					instance.Attach(this);
					break;
				}
				case MixerCommandKind.StartInstance:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.Start(this, (long)(command.Value * SampleRate));
					}

					break;
				case MixerCommandKind.StopInstance:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.Stop(this, command.Flag ? AudioStopMode.Immediate : AudioStopMode.AllowFadeout);
					}

					break;
				case MixerCommandKind.ReleaseInstance:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.ReleaseWhenStopped = true;
					}

					break;
				case MixerCommandKind.DestroyInstance:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.Stop(this, AudioStopMode.Immediate);
						instance.ReleaseWhenStopped = true;
					}

					break;
				case MixerCommandKind.SetInstanceParameter:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.SetParameter(this, (string)command.Payload, command.Value, command.Flag);
					}

					break;
				case MixerCommandKind.SetInstance3D:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.Set3D(command.V0, command.V1, command.V2, command.V3, command.Flag);
					}

					break;
				case MixerCommandKind.SetInstanceVolume:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.VolumeGain = Math.Max(0f, command.Value);
					}

					break;
				case MixerCommandKind.SetInstancePitch:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.PitchRatio = Math.Clamp(command.Value, 0.01f, 16f);
					}

					break;
				case MixerCommandKind.SetInstancePaused:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.Paused = command.Flag;
					}

					break;
				case MixerCommandKind.KeyOff:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.KeyOff(this);
					}

					break;
				case MixerCommandKind.SetTimelinePosition:
					if (instancesById.TryGetValue(command.Id, out instance))
					{
						instance.SetTimelinePosition(this, (long)(command.Value * SampleRate));
					}

					break;
				case MixerCommandKind.SetGlobalParameter:
				{
					int index;
					if (command.Payload is string id && globalIndex.TryGetValue(id, out index))
					{
						GlobalParameterState state = globals[index];
						state.Target = state.Definition.Clamp(command.Value);
						if (command.Flag || state.Definition.SeekSpeed <= 0f)
						{
							state.Current = state.Target;
						}
					}

					break;
				}
				case MixerCommandKind.SetBusVolume:
				{
					int index = FindBusExact((string)command.Payload);
					if (index >= 0)
					{
						buses[index].GameGain = Math.Max(0f, command.Value);
					}

					break;
				}
				case MixerCommandKind.SetBusMute:
				{
					int index = FindBusExact((string)command.Payload);
					if (index >= 0)
					{
						buses[index].GameMute = command.Flag;
					}

					break;
				}
				case MixerCommandKind.SetListener:
					listenerPosition = command.V0;
					listenerForward = command.V1.LengthSquared > 1e-8f ? command.V1.Normalized() : -Vector3.UnitZ;
					listenerUp = command.V2.LengthSquared > 1e-8f ? command.V2.Normalized() : Vector3.UnitY;
					listenerRight = Vector3.Cross(listenerForward, listenerUp);
					if (listenerRight.LengthSquared < 1e-8f)
					{
						listenerRight = Vector3.UnitX;
					}
					else
					{
						listenerRight.Normalize();
					}

					listenerVelocity = command.V3;
					break;
				case MixerCommandKind.StopAll:
					foreach (EventPlayback playing in instances)
					{
						if (playing.Parent == null)
						{
							playing.Stop(this, command.Flag ? AudioStopMode.Immediate : AudioStopMode.AllowFadeout);
							if (command.IntValue != 0)
							{
								playing.ReleaseWhenStopped = true;
							}
						}
					}

					break;
				case MixerCommandKind.PauseAll:
					pausedAll = command.Flag;
					break;
				case MixerCommandKind.SetMaxVoices:
					MaxVoices = Math.Max(1, command.IntValue);
					break;
				case MixerCommandKind.SetMasterVolume:
					masterGain = Math.Max(0f, command.Value);
					break;
				case MixerCommandKind.StartSnapshot:
				{
					AudioSnapshot definition;
					if (command.Payload is string definitionId && snapshotDefinitions.TryGetValue(definitionId, out definition))
					{
						StartSnapshotState(command.Id, definition, Math.Clamp(command.Value, 0f, 1f), false);
					}
					else
					{
						Notify(new AudioNotification { Kind = AudioNotificationKind.SnapshotStopped, InstanceId = command.Id });
					}

					break;
				}
				case MixerCommandKind.StopSnapshot:
					foreach (SnapshotState state in snapshots)
					{
						if (state.Id == command.Id && !state.Owned)
						{
							state.Envelope.Release(command.Flag ? 0.002f : -1f);
						}
					}

					break;
				case MixerCommandKind.SetSnapshotIntensity:
					foreach (SnapshotState state in snapshots)
					{
						if (state.Id == command.Id)
						{
							state.Intensity = Math.Clamp(command.Value, 0f, 1f);
						}
					}

					break;
				case MixerCommandKind.StopAllSnapshots:
					foreach (SnapshotState state in snapshots)
					{
						state.Envelope.Release(command.Flag ? 0.002f : -1f);
					}

					break;
				case MixerCommandKind.SetDoppler:
					SpeedOfSound = Math.Max(1f, command.V0.X);
					DopplerScale = Math.Max(0f, command.V0.Y);
					break;
			}
		}

		private SnapshotState StartSnapshotState(int id, AudioSnapshot definition, float intensity, bool owned)
		{
			int priority;
			snapshotPriorities.TryGetValue(definition.Id, out priority);
			var state = new SnapshotState
			{
				Id = id,
				Definition = definition,
				Priority = priority,
				Intensity = intensity,
				Owned = owned,
				Envelope = EnvelopeRunner.Start(definition.Intensity, 0f)
			};
			snapshots.Add(state);
			return state;
		}

		/// <summary>Advances the snapshot envelopes, drops the finished ones and applies the active ones to the buses.</summary>
		private void AdvanceSnapshots(int frames)
		{
			double seconds = frames / (double)SampleRate;
			for (int i = snapshots.Count - 1; i >= 0; i--)
			{
				SnapshotState state = snapshots[i];
				float level = state.Envelope.Advance(state.Definition.Intensity, seconds, 0.005f);
				state.Level = level * state.Intensity;
				if (state.Envelope.IsDone)
				{
					state.Done = true;
					snapshots.RemoveAt(i);
					if (!state.Owned)
					{
						Notify(new AudioNotification { Kind = AudioNotificationKind.SnapshotStopped, InstanceId = state.Id });
					}
				}
			}

			if (snapshots.Count == 0)
			{
				if (snapshotsWereActive)
				{
					// Back to the definitions' values once.
					snapshotsWereActive = false;
					ResetSnapshotTargets();
				}

				return;
			}

			snapshotsWereActive = true;
			ResetSnapshotTargets();

			// Lowest priority first, so the highest one ends up on top.
			snapshotOrder.Clear();
			snapshotOrder.AddRange(snapshots);
			snapshotOrder.Sort(byPriorityDescending);
			foreach (SnapshotState state in snapshotOrder)
			{
				float level = Math.Clamp(state.Level, 0f, 1f);
				if (level <= 0f)
				{
					continue;
				}

				foreach (SnapshotBusValue value in state.Definition.Buses)
				{
					int busIndex = FindBusIndex(value.BusId);
					if (busIndex < 0)
					{
						continue;
					}

					BusState bus = buses[busIndex];
					if (value.SetVolume)
					{
						bus.EffectiveVolumeDb = state.Definition.Mode == SnapshotMode.Blending
							? bus.EffectiveVolumeDb + value.VolumeDb * level
							: bus.EffectiveVolumeDb + (value.VolumeDb - bus.EffectiveVolumeDb) * level;
					}

					foreach (SnapshotEffectValue effectValue in value.Effects)
					{
						EffectRunner runner = FindRunner(bus, effectValue.EffectId);
						if (runner == null)
						{
							continue;
						}

						float current = runner.Effect.GetParameter(effectValue.Parameter);
						runner.Effect.SetParameter(effectValue.Parameter, current + (effectValue.Value - current) * level);
					}
				}
			}

			foreach (BusState bus in buses)
			{
				bus.TargetGain = AudioMath.DbToGain(bus.EffectiveVolumeDb);
			}
		}

		/// <summary>Every bus volume and every effect value some snapshot touches goes back to its definition's value.</summary>
		private void ResetSnapshotTargets()
		{
			foreach (BusState bus in buses)
			{
				bus.EffectiveVolumeDb = bus.BaseVolumeDb;
				bus.TargetGain = AudioMath.DbToGain(bus.BaseVolumeDb);
			}

			foreach (TouchedEffectValue touched in touchedEffects)
			{
				if (touched.Bus < buses.Length && touched.Effect < buses[touched.Bus].Effects.Length)
				{
					BusState bus = buses[touched.Bus];
					bus.Effects[touched.Effect].Effect.SetParameter(touched.Parameter, bus.BaseEffects[touched.Effect].GetParameter(touched.Parameter));
				}
			}
		}

		private int FindBusIndex(string id)
		{
			for (int i = 0; i < buses.Length; i++)
			{
				if (buses[i].Id == id)
				{
					return i;
				}
			}

			return -1;
		}

		private static EffectRunner FindRunner(BusState bus, string effectId)
		{
			foreach (EffectRunner runner in bus.Effects)
			{
				if (runner.Effect.Id == effectId)
				{
					return runner;
				}
			}

			return null;
		}

		private int FindBusExact(string idOrName)
		{
			if (idOrName == null)
			{
				return -1;
			}

			for (int i = 0; i < buses.Length; i++)
			{
				if (buses[i].Id == idOrName || string.Equals(buses[i].Name, idOrName, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		private void ApplyGlobals(MixerGlobals data)
		{
			// Parameters: values of ones that stay are kept.
			var newGlobals = new List<GlobalParameterState>();
			var newIndex = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (AudioParameter parameter in data.Parameters)
			{
				if (parameter == null || string.IsNullOrEmpty(parameter.Id) || newIndex.ContainsKey(parameter.Id))
				{
					continue;
				}

				int existing;
				GlobalParameterState state;
				if (globalIndex.TryGetValue(parameter.Id, out existing))
				{
					state = globals[existing];
					state.Definition = parameter;
					state.Target = parameter.Clamp(state.Target);
					state.Current = parameter.Clamp(state.Current);
				}
				else
				{
					state = new GlobalParameterState { Definition = parameter, Target = parameter.Clamp(parameter.Default), Current = parameter.Clamp(parameter.Default) };
				}

				newIndex[parameter.Id] = newGlobals.Count;
				newGlobals.Add(state);
			}

			globals.Clear();
			globals.AddRange(newGlobals);
			globalIndex.Clear();
			foreach (KeyValuePair<string, int> entry in newIndex)
			{
				globalIndex[entry.Key] = entry.Value;
			}

			// Buses: existing states (effects' memory, meters) are kept by id.
			var newBuses = new List<BusState>();
			foreach (AudioBus bus in data.Buses)
			{
				if (bus == null)
				{
					continue;
				}

				BusState state = null;
				foreach (BusState old in buses)
				{
					if (old.Id == bus.Id)
					{
						state = old;
						break;
					}
				}

				state ??= new BusState { Id = bus.Id };
				state.Name = bus.Name;
				state.BaseVolumeDb = bus.VolumeDb;
				state.EffectiveVolumeDb = bus.VolumeDb;
				state.TargetGain = AudioMath.DbToGain(bus.VolumeDb);
				state.Mute = bus.Mute;
				var runners = new List<EffectRunner>();
				var bases = new List<AudioEffect>();
				foreach (AudioEffect effect in bus.Effects)
				{
					if (effect == null)
					{
						continue;
					}

					EffectRunner runner = null;
					foreach (EffectRunner old in state.Effects)
					{
						if (old.Effect.Id == effect.Id && old.Effect.GetType() == effect.GetType())
						{
							runner = old;
							runner.Effect = effect.Clone();
							break;
						}
					}

					runners.Add(runner ?? new EffectRunner(effect.Clone()));
					bases.Add(effect.Clone());
				}

				state.Effects = runners.ToArray();
				state.BaseEffects = bases.ToArray();
				newBuses.Add(state);
			}

			if (newBuses.Count == 0)
			{
				newBuses.Add(new BusState { Id = AudioProject.MasterBusId, Name = "Master" });
			}

			for (int i = 0; i < newBuses.Count; i++)
			{
				AudioBus bus = i < data.Buses.Count ? data.Buses[i] : null;
				newBuses[i].Parent = -1;
				if (bus != null && !string.IsNullOrEmpty(bus.ParentId))
				{
					for (int j = 0; j < newBuses.Count; j++)
					{
						if (j != i && newBuses[j].Id == bus.ParentId)
						{
							newBuses[i].Parent = j;
							break;
						}
					}
				}

				// A child listed before its parent would be summed before the parent is processed: route it to the master.
				if (newBuses[i].Parent >= i)
				{
					newBuses[i].Parent = i == 0 ? -1 : 0;
				}

				if (i > 0 && newBuses[i].Parent < 0)
				{
					newBuses[i].Parent = 0;
				}
			}

			buses = newBuses.ToArray();
			MaxVoices = Math.Max(1, data.MaxVoices);

			// Snapshots: definitions by id and their priorities; running ones take the new definition or end.
			snapshotDefinitions.Clear();
			snapshotPriorities.Clear();
			touchedEffects.Clear();
			for (int i = 0; i < data.Snapshots.Count; i++)
			{
				AudioSnapshot snapshot = data.Snapshots[i];
				if (snapshot == null || string.IsNullOrEmpty(snapshot.Id))
				{
					continue;
				}

				snapshotDefinitions[snapshot.Id] = snapshot;
				snapshotPriorities[snapshot.Id] = i;
				foreach (SnapshotBusValue value in snapshot.Buses)
				{
					int busIndex = FindBusIndex(value.BusId);
					if (busIndex < 0)
					{
						continue;
					}

					foreach (SnapshotEffectValue effectValue in value.Effects)
					{
						for (int e = 0; e < buses[busIndex].Effects.Length; e++)
						{
							if (buses[busIndex].Effects[e].Effect.Id == effectValue.EffectId)
							{
								touchedEffects.Add(new TouchedEffectValue { Bus = busIndex, Effect = e, Parameter = effectValue.Parameter });
							}
						}
					}
				}
			}

			foreach (SnapshotState state in snapshots)
			{
				AudioSnapshot definition;
				if (snapshotDefinitions.TryGetValue(state.Definition.Id, out definition))
				{
					state.Definition = definition;
					state.Priority = snapshotPriorities[definition.Id];
				}
				else
				{
					state.Envelope.Release(0.05f);
				}
			}

			foreach (EventPlayback instance in instances)
			{
				instance.OnGlobalsChanged(this);
			}
		}

		private void AdvanceGlobals(int frames)
		{
			double seconds = frames / (double)SampleRate;
			foreach (GlobalParameterState state in globals)
			{
				float speed = state.Definition.SeekSpeed;
				if (speed <= 0f || state.Current == state.Target)
				{
					state.Current = state.Target;
					continue;
				}

				float step = (float)(speed * seconds);
				state.Current = state.Current < state.Target ? Math.Min(state.Target, state.Current + step) : Math.Max(state.Target, state.Current - step);
			}
		}

		/// <summary>Voices beyond <see cref="MaxVoices" /> go virtual, the quietest first.</summary>
		private void LimitVoices()
		{
			voiceScratch.Clear();
			foreach (EventPlayback instance in instances)
			{
				instance.CollectVoices(voiceScratch);
			}

			if (voiceScratch.Count <= MaxVoices)
			{
				foreach (Voice voice in voiceScratch)
				{
					voice.Virtual = false;
				}

				return;
			}

			voiceScratch.Sort(byAudibility);
			for (int i = 0; i < voiceScratch.Count; i++)
			{
				voiceScratch[i].Virtual = i >= MaxVoices;
			}
		}

		/// <summary>Effects and gains of every bus (children into parents), then the master into the output block with a limiter.</summary>
		private void ProcessBuses(int frames)
		{
			if (buses.Length == 0)
			{
				Array.Clear(masterBlock, 0, frames * 2);
				return;
			}

			for (int i = buses.Length - 1; i >= 0; i--)
			{
				BusState bus = buses[i];
				float[] buffer = bus.Buffer;
				foreach (EffectRunner effect in bus.Effects)
				{
					effect.Process(buffer, frames);
				}

				float target = bus.Mute || bus.GameMute ? 0f : bus.TargetGain * bus.GameGain;
				float start = bus.CurrentGain;
				bus.CurrentGain = target;
				float peakLeft = 0f;
				float peakRight = 0f;
				if (start == target)
				{
					for (int f = 0; f < frames; f++)
					{
						buffer[f * 2] *= target;
						buffer[f * 2 + 1] *= target;
					}
				}
				else
				{
					float step = (target - start) / frames;
					float gain = start;
					for (int f = 0; f < frames; f++)
					{
						buffer[f * 2] *= gain;
						buffer[f * 2 + 1] *= gain;
						gain += step;
					}
				}

				for (int f = 0; f < frames; f++)
				{
					float l = Math.Abs(buffer[f * 2]);
					float r = Math.Abs(buffer[f * 2 + 1]);
					if (l > peakLeft) peakLeft = l;
					if (r > peakRight) peakRight = r;
				}

				bus.PeakLeft = peakLeft;
				bus.PeakRight = peakRight;
				if (bus.Parent >= 0 && bus.Parent < i)
				{
					float[] parent = buses[bus.Parent].Buffer;
					for (int v = 0; v < frames * 2; v++)
					{
						parent[v] += buffer[v];
					}
				}
			}

			// The master bus to the output with the master volume and a soft limiter.
			float[] master = buses[0].Buffer;
			float gainStart = masterCurrent;
			masterCurrent = masterGain;
			float gainStep = (masterGain - gainStart) / frames;
			float g = gainStart;
			float peakL = 0f;
			float peakR = 0f;
			for (int f = 0; f < frames; f++)
			{
				float l = Limit(master[f * 2] * g);
				float r = Limit(master[f * 2 + 1] * g);
				masterBlock[f * 2] = l;
				masterBlock[f * 2 + 1] = r;
				if (Math.Abs(l) > peakL) peakL = Math.Abs(l);
				if (Math.Abs(r) > peakR) peakR = Math.Abs(r);
				g += gainStep;
			}

			MasterPeakLeft = peakL;
			MasterPeakRight = peakR;
		}

		/// <summary>Transparent below 0.9, a soft knee above it, never beyond 1.</summary>
		private static float Limit(float value)
		{
			const float knee = 0.9f;
			float magnitude = Math.Abs(value);
			if (magnitude <= knee)
			{
				return value;
			}

			float over = (magnitude - knee) / (1f - knee);
			float limited = knee + (1f - knee) * MathF.Tanh(over);
			return value < 0f ? -limited : limited;
		}

		#endregion

		#region NestedTypes

		private sealed class GlobalParameterState
		{
			public AudioParameter Definition;
			public float Target;
			public float Current;
		}

		/// <summary>An effect value some snapshot sets: reset to the definition's value before the snapshots apply.</summary>
		private struct TouchedEffectValue
		{
			public int Bus;
			public int Effect;
			public string Parameter;
		}

		#endregion
	}
}
