using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Audio
{
	public enum PlaybackState
	{
		/// <summary>Created, not started.</summary>
		Created,

		/// <summary>The timeline runs.</summary>
		Playing,

		/// <summary>Stopped at a sustain point until KeyOff.</summary>
		Sustaining,

		/// <summary>Fading out after Stop.</summary>
		Stopping,

		Stopped
	}

	/// <summary>
	///     One playing instance of an event, as the audio thread runs it: the timeline cursor (in output samples: every
	///     boundary it crosses is handled at the exact sample), the parameter values, the triggered instruments with
	///     their voices, the track buffers with effects and automation, the event envelope, the 3D gains and the Doppler
	///     shift, and transitions with a transition timeline (the source's sounds fading out while the destination's
	///     fade in). Created on the game thread, then owned by the mixer. The game's handle is <see cref="EventInstance" />.
	/// </summary>
	public sealed class EventPlayback
	{
		#region PublicFields

		public readonly int Id;
		public CompiledEvent Event { get; private set; }
		public EventPlayback Parent;
		public readonly int Depth;
		public Random Random;

		/// <summary>The playback is dropped once it stops (one-shots, released handles).</summary>
		public bool ReleaseWhenStopped;

		public bool Paused;
		public float VolumeGain = 1f;
		public float PitchRatio = 1f;

		// Published for the game thread.
		public volatile int StateValue;
		public volatile float TimelineSeconds;
		public volatile float PeakLeft;
		public volatile float PeakRight;
		public volatile int VoiceCount;
		public volatile int VirtualVoiceCount;

		/// <summary>Index of the transition whose timeline runs (-1 when none) and the time into it, seconds.</summary>
		public volatile int ActiveTransitionIndex = -1;

		public volatile float TransitionSeconds;

		/// <summary>Current values of the parameters, by index of <see cref="CompiledEvent.Parameters" /> (read by the game thread for display).</summary>
		public float[] PublishedParameters = Array.Empty<float>();

		/// <summary>Peak level per track (index of <see cref="CompiledEvent.Tracks" />) of the last block, for the editor's meters.</summary>
		public float[] TrackPeaks = Array.Empty<float>();

		#endregion

		#region PrivateFields

		private const long NoSample = -1;

		private PlaybackState state = PlaybackState.Created;
		private long cursor;
		private int busIndex = -1;
		private bool attached;
		private bool reachedEnd;
		private float[] paramTarget = Array.Empty<float>();
		private float[] paramCurrent = Array.Empty<float>();
		private bool[] paramIsGlobal = Array.Empty<bool>();
		private InstrumentRuntime[] instruments = Array.Empty<InstrumentRuntime>();
		private TrackRuntime[] tracks = Array.Empty<TrackRuntime>();
		private TrackRuntime master;
		private EnvelopeRunner envelope;
		private float envelopeLevel;
		private Vector3 position;
		private Vector3 velocity;
		private Vector3 forward = -Vector3.UnitZ;
		private bool has3D;
		private float attenuationTarget = 1f;
		private float attenuationCurrent = 1f;
		private float panLeft = 1f;
		private float panRight = 1f;
		private float dopplerCurrent = 1f;
		private float masterCurrentLeft;
		private float masterCurrentRight;
		private bool masterGainsInitialized;
		private CompiledTransition pendingTransition;
		private long pendingTransitionAt = NoSample;
		private int[] loopCounts = Array.Empty<int>();
		private long nextBeat = NoSample;
		private long sustainedAt = NoSample;
		private int nestedCounter;
		private int jumpDepth;
		private readonly List<EventPlayback> polyphonyScratch = new List<EventPlayback>();
		private bool failed;
		private bool warnedNested;

		// A transition timeline in progress.
		private CompiledTransition activeTransition;
		private long transitionCursor;
		private long transitionSource;
		private long transitionDestination;
		private bool transitionToEnd;
		private bool destinationActive;
		private bool sourceAlive;

		#endregion

		#region Constructors

		/// <summary>Builds the playback (buffers, instrument state) for an event; game thread or audio thread (nested).</summary>
		public EventPlayback(int id, CompiledEvent compiled, int depth, Random random, Mixer mixer)
		{
			Id = id;
			Depth = depth;
			Random = random ?? new Random(id);
			Build(compiled);
			if (mixer != null)
			{
				busIndex = mixer.FindBus(compiled.BusId);
			}
		}

		#endregion

		#region PublicMethods

		public PlaybackState State => state;
		public bool IsPlaying => state == PlaybackState.Playing || state == PlaybackState.Sustaining || state == PlaybackState.Stopping;
		public bool IsFinished => state == PlaybackState.Stopped && ReleaseWhenStopped;
		public long Cursor => cursor;
		public Vector3 Position => position;

		public int AllocateNestedId()
		{
			nestedCounter++;
			return -(Id * 64 + nestedCounter);
		}

		/// <summary>The mixer took the playback (audio thread).</summary>
		public void Attach(Mixer mixer)
		{
			attached = true;
			busIndex = mixer.FindBus(Event.BusId);
			RefreshGlobals(mixer);
		}

		public void OnGlobalsChanged(Mixer mixer)
		{
			busIndex = mixer.FindBus(Event.BusId);
			RefreshGlobals(mixer);
		}

		/// <summary>Starts (or restarts) the timeline at an offset.</summary>
		public void Start(Mixer mixer, long offset)
		{
			if (state == PlaybackState.Stopping || state == PlaybackState.Playing || state == PlaybackState.Sustaining)
			{
				// Restart: like FMOD, a start while playing restarts from the beginning.
				FreeVoices(mixer, 0.003f);
				StopNested(mixer, AudioStopMode.Immediate);
			}

			if (Event.MaxInstances > 0 && Parent == null)
			{
				int playing = mixer.CountInstances(Event, polyphonyScratch);
				if (playing >= Event.MaxInstances)
				{
					EventPlayback victim = PickVictim(mixer, polyphonyScratch);
					if (victim == null)
					{
						state = PlaybackState.Stopped;
						StateValue = (int)state;
						mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.StartRefused, InstanceId = Id });
						return;
					}

					victim.Stop(mixer, AudioStopMode.AllowFadeout);
				}
			}

			state = PlaybackState.Playing;
			reachedEnd = false;
			envelope = EnvelopeRunner.Start(Event.Envelope, 0f);
			envelopeLevel = envelope.Level;
			pendingTransition = null;
			pendingTransitionAt = NoSample;
			sustainedAt = NoSample;
			ClearTransition();
			Array.Clear(loopCounts, 0, loopCounts.Length);
			foreach (InstrumentRuntime runtime in instruments)
			{
				runtime.Triggered = false;
				runtime.PendingTriggerAt = NoSample;
				runtime.NextSpawnAt = NoSample;
				runtime.Spawned = 0;
				runtime.SourceExtended = false;
				runtime.DestinationFading = false;
			}

			UpdateSpatial(mixer);
			attenuationCurrent = attenuationTarget;
			JumpTo(mixer, 0, Math.Max(0, offset), 0.0, true);
			StateValue = (int)state;
			mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.Started, InstanceId = Id });
		}

		public void Stop(Mixer mixer, AudioStopMode mode)
		{
			if (state == PlaybackState.Stopped || state == PlaybackState.Created)
			{
				if (state == PlaybackState.Created)
				{
					state = PlaybackState.Stopped;
					StateValue = (int)state;
				}

				return;
			}

			StopNested(mixer, mode);
			if (mode == AudioStopMode.Immediate)
			{
				envelope.Release(0.002f);
			}
			else
			{
				envelope.Release(Math.Max(Event.Envelope?.Release ?? 0f, 0.005f));
			}

			if (state == PlaybackState.Sustaining)
			{
				sustainedAt = cursor;
			}

			state = PlaybackState.Stopping;
			StateValue = (int)state;
		}

		/// <summary>Resumes from a sustain point.</summary>
		public void KeyOff(Mixer mixer)
		{
			if (state == PlaybackState.Sustaining)
			{
				state = PlaybackState.Playing;
				StateValue = (int)state;
			}
		}

		public void SetTimelinePosition(Mixer mixer, long sample)
		{
			if (state == PlaybackState.Playing || state == PlaybackState.Sustaining)
			{
				if (state == PlaybackState.Sustaining)
				{
					state = PlaybackState.Playing;
				}

				if (activeTransition != null)
				{
					AbortTransition(mixer);
				}

				JumpTo(mixer, 0, Math.Max(0, sample), 0.0, false);
			}
		}

		public void SetParameter(Mixer mixer, string id, float value, bool ignoreSeek)
		{
			int index = Event.FindParameter(id);
			if (index < 0)
			{
				index = Event.FindParameterByName(id);
			}

			if (index < 0)
			{
				return;
			}

			CompiledParameter parameter = Event.Parameters[index];
			if (parameter.IsGlobal)
			{
				// Globals belong to the mixer.
				mixer.Post(MixerCommandKind.SetGlobalParameter, 0, value, ignoreSeek, parameter.Id);
				return;
			}

			paramTarget[index] = parameter.Clamp(value);
			if (ignoreSeek || parameter.SeekSpeed <= 0f)
			{
				paramCurrent[index] = paramTarget[index];
			}
		}

		public void Set3D(Vector3 newPosition, Vector3 newVelocity, Vector3 newForward, Vector3 up, bool enabled)
		{
			position = newPosition;
			velocity = newVelocity;
			forward = newForward;
			has3D = enabled;
			foreach (InstrumentRuntime runtime in instruments)
			{
				runtime.Nested?.Set3D(newPosition, newVelocity, newForward, up, enabled);
			}
		}

		/// <summary>Live update (editor): the event's description changed under the playing instance.</summary>
		public void ReplaceEvent(Mixer mixer, CompiledEvent compiled)
		{
			if (activeTransition != null)
			{
				AbortTransition(mixer);
			}

			CompiledEvent old = Event;
			InstrumentRuntime[] oldInstruments = instruments;
			float[] oldTarget = paramTarget;
			float[] oldCurrent = paramCurrent;
			Build(compiled);
			busIndex = mixer.FindBus(compiled.BusId);
			RefreshGlobals(mixer);

			// Parameter values carry over by id.
			for (int i = 0; i < compiled.Parameters.Length; i++)
			{
				int previous = old.FindParameter(compiled.Parameters[i].Id);
				if (previous >= 0 && previous < oldTarget.Length && !compiled.Parameters[i].IsGlobal)
				{
					paramTarget[i] = compiled.Parameters[i].Clamp(oldTarget[previous]);
					paramCurrent[i] = compiled.Parameters[i].Clamp(oldCurrent[previous]);
				}
			}

			// Instruments carry over their voices by id; vanished ones fade out.
			foreach (InstrumentRuntime previous in oldInstruments)
			{
				InstrumentRuntime current = null;
				for (int i = 0; i < instruments.Length; i++)
				{
					if (instruments[i].Instrument.Id == previous.Instrument.Id)
					{
						current = instruments[i];
						break;
					}
				}

				if (current == null || current.Instrument.Track < 0 || current.Instrument.Track >= tracks.Length)
				{
					// The instrument is gone: its voices fade out in the first instrument's fading list (or are dropped).
					MoveOrDrop(mixer, previous.Voices, 0.05f);
					MoveOrDrop(mixer, previous.Fading, -1f);
					MoveOrDrop(mixer, previous.SourceVoices, 0.05f);
					previous.Voices.Clear();
					previous.Fading.Clear();
					previous.SourceVoices.Clear();
					previous.DisposeReaders();
					previous.Nested?.Stop(mixer, AudioStopMode.AllowFadeout);
					mixer.StopOwnedSnapshot(previous.Snapshot, false);
					continue;
				}

				current.Triggered = previous.Triggered;
				current.PendingTriggerAt = previous.PendingTriggerAt;
				current.PendingFromJump = previous.PendingFromJump;
				current.NextSpawnAt = previous.NextSpawnAt;
				current.Spawned = previous.Spawned;
				current.PlaylistCursor = previous.PlaylistCursor;
				current.Nested = previous.Nested;
				current.Snapshot = previous.Snapshot;
				Retarget(previous.Voices, current, current.Voices);
				Retarget(previous.Fading, current, current.Fading);
				Retarget(previous.SourceVoices, current, current.Fading);
				previous.Voices.Clear();
				previous.Fading.Clear();
				previous.SourceVoices.Clear();
				// The new runtime prefetched its own readers: the old spare ones go.
				previous.DisposeReaders();
				// A region that no longer contains the cursor untriggers; one that now does triggers.
				if (current.Instrument.OnTimeline && state != PlaybackState.Stopped && state != PlaybackState.Created)
				{
					bool inside = current.Instrument.StartSample <= cursor && cursor < current.Instrument.EndSample;
					if (current.Triggered && !inside)
					{
						Untrigger(mixer, current, -1f);
					}
					else if (!current.Triggered && inside && !current.Instrument.Async)
					{
						Trigger(mixer, current, 0, cursor - current.Instrument.StartSample, 0.01f, true);
					}
				}
			}

			if (state != PlaybackState.Stopped && state != PlaybackState.Created)
			{
				foreach (InstrumentRuntime runtime in instruments)
				{
					if (runtime.Instrument.OnTimeline && !runtime.Triggered && !runtime.Instrument.Async && runtime.Instrument.StartSample <= cursor && cursor < runtime.Instrument.EndSample && runtime.PendingTriggerAt == NoSample)
					{
						Trigger(mixer, runtime, 0, cursor - runtime.Instrument.StartSample, 0.01f, true);
					}
				}
			}

			pendingTransition = null;
			pendingTransitionAt = NoSample;
			nextBeat = Event.HasTempo ? NextBeatAfter(cursor - 1) : NoSample;
		}

		/// <summary>One sub-block: parameters, automation, sheets, the timeline (or the transition timeline) with its voices, the track mix into the bus.</summary>
		public void Process(Mixer mixer, int frames, bool pausedAll)
		{
			if (!attached)
			{
				return;
			}

			if (state == PlaybackState.Created || state == PlaybackState.Stopped)
			{
				Publish();
				return;
			}

			double dt = frames / (double)Mixer.SampleRate;
			bool frozen = Paused || pausedAll;
			UpdateParameters(mixer, dt);
			UpdateSpatial(mixer);
			UpdateAutomation();
			ClearBuffers(frames);
			if (frozen)
			{
				attenuationCurrent = attenuationTarget;
				Publish();
				return;
			}

			UpdateSheets(mixer);
			int done = 0;
			int guard = 0;
			while (done < frames && guard++ < 4096)
			{
				int remaining = frames - done;
				if (activeTransition != null)
				{
					long boundary = NextTransitionBoundary(transitionCursor, transitionCursor + remaining);
					int segment = boundary == long.MaxValue ? remaining : (int)Math.Min(remaining, boundary - transitionCursor);
					if (segment > 0)
					{
						UpdateTransitionGains();
						RenderVoices(mixer, done, segment);
						AdvanceTransitionCursor(mixer, transitionCursor + segment);
						done += segment;
					}

					if (activeTransition != null && boundary != long.MaxValue && transitionCursor == boundary)
					{
						HandleTransitionBoundary(mixer);
					}

					continue;
				}

				bool advancing = state != PlaybackState.Sustaining;
				long next = advancing ? NextBoundary(cursor, cursor + remaining) : long.MaxValue;
				int count = next == long.MaxValue ? remaining : (int)Math.Min(remaining, next - cursor);
				if (count > 0)
				{
					RenderVoices(mixer, done, count);
					if (advancing)
					{
						AdvanceCursor(mixer, cursor + count);
					}

					done += count;
				}

				if (advancing && next != long.MaxValue && cursor == next && activeTransition == null)
				{
					HandleBoundary(mixer, done);
				}
			}

			MixTracks(mixer, frames);
			float before = envelopeLevel;
			envelopeLevel = envelope.Advance(Event.Envelope, dt, Mixer.MinimumRelease);
			WriteToBus(mixer, frames, before, envelopeLevel);
			CleanupVoices(mixer);

			if (state == PlaybackState.Stopping && envelope.IsDone)
			{
				Finish(mixer);
			}
			else if (state == PlaybackState.Playing && !Event.Persistent && activeTransition == null && (cursor >= Event.LengthSamples || reachedEnd) && !HasContent())
			{
				Finish(mixer);
			}

			Publish();
		}

		/// <summary>The instance threw while processing: it stops at once (logged once per instance).</summary>
		public void Fail(Mixer mixer, Exception e)
		{
			if (!failed)
			{
				failed = true;
				Debug.LogError($"[Audio] Event '{Event.Path}' failed and was stopped: {e.Message}\n{e.StackTrace}");
			}

			try
			{
				FreeVoices(mixer, 0f);
				StopNested(mixer, AudioStopMode.Immediate);
			}
			catch (Exception)
			{
				// Best effort.
			}

			state = PlaybackState.Stopped;
			ReleaseWhenStopped = true;
			StateValue = (int)state;
		}

		/// <summary>Frees everything the playback holds (the mixer drops it).</summary>
		public void Destroy(Mixer mixer)
		{
			FreeVoices(mixer, 0f);
			StopNested(mixer, AudioStopMode.Immediate);
			foreach (InstrumentRuntime runtime in instruments)
			{
				runtime.DisposeReaders();
			}
		}

		public void CollectVoices(List<Voice> result)
		{
			foreach (InstrumentRuntime runtime in instruments)
			{
				result.AddRange(runtime.Voices);
				result.AddRange(runtime.SourceVoices);
			}
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- setup

		private void Build(CompiledEvent compiled)
		{
			Event = compiled;
			paramTarget = new float[compiled.Parameters.Length];
			paramCurrent = new float[compiled.Parameters.Length];
			paramIsGlobal = new bool[compiled.Parameters.Length];
			for (int i = 0; i < compiled.Parameters.Length; i++)
			{
				paramTarget[i] = compiled.Parameters[i].Default;
				paramCurrent[i] = compiled.Parameters[i].Default;
				paramIsGlobal[i] = compiled.Parameters[i].IsGlobal;
			}

			PublishedParameters = new float[compiled.Parameters.Length];
			Array.Copy(paramCurrent, PublishedParameters, paramCurrent.Length);

			tracks = new TrackRuntime[compiled.Tracks.Length];
			for (int i = 0; i < tracks.Length; i++)
			{
				tracks[i] = new TrackRuntime(compiled.Tracks[i]);
			}

			TrackPeaks = new float[tracks.Length];
			master = new TrackRuntime(compiled.Master);
			instruments = new InstrumentRuntime[compiled.Instruments.Length];
			for (int i = 0; i < instruments.Length; i++)
			{
				instruments[i] = new InstrumentRuntime(compiled.Instruments[i]);
			}

			loopCounts = new int[compiled.Loops.Length];
		}

		private void RefreshGlobals(Mixer mixer)
		{
			for (int i = 0; i < paramIsGlobal.Length; i++)
			{
				if (paramIsGlobal[i])
				{
					float value = mixer.GlobalValue(Event.Parameters[i].Id);
					if (!float.IsNaN(value))
					{
						paramCurrent[i] = value;
						paramTarget[i] = value;
					}
				}
			}
		}

		private void Publish()
		{
			StateValue = (int)state;
			long shown = cursor;
			if (activeTransition != null && destinationActive && !transitionToEnd)
			{
				shown = transitionDestination + Math.Max(0, transitionCursor - activeTransition.Timeline.DestinationStart);
			}

			TimelineSeconds = (float)(shown / (double)Mixer.SampleRate);
			ActiveTransitionIndex = activeTransition != null ? activeTransition.Index : -1;
			TransitionSeconds = activeTransition != null ? (float)(transitionCursor / (double)Mixer.SampleRate) : 0f;
			int count = 0;
			int virtuals = 0;
			foreach (InstrumentRuntime runtime in instruments)
			{
				count += runtime.Voices.Count + runtime.SourceVoices.Count;
				foreach (Voice voice in runtime.Voices)
				{
					if (voice.Virtual)
					{
						virtuals++;
					}
				}
			}

			VoiceCount = count;
			VirtualVoiceCount = virtuals;
			if (PublishedParameters.Length == paramCurrent.Length)
			{
				Array.Copy(paramCurrent, PublishedParameters, paramCurrent.Length);
			}
		}

		// ---------------------------------------------------------------- per block

		private void UpdateParameters(Mixer mixer, double dt)
		{
			for (int i = 0; i < paramCurrent.Length; i++)
			{
				CompiledParameter parameter = Event.Parameters[i];
				if (parameter.IsGlobal)
				{
					float value = mixer.GlobalValue(parameter.Id);
					if (!float.IsNaN(value))
					{
						paramCurrent[i] = value;
						paramTarget[i] = value;
					}

					continue;
				}

				if (parameter.BuiltIn != BuiltInParameter.None)
				{
					paramCurrent[i] = parameter.Clamp(BuiltInValue(mixer, parameter.BuiltIn));
					paramTarget[i] = paramCurrent[i];
					continue;
				}

				float speed = parameter.SeekSpeed;
				if (speed <= 0f || paramCurrent[i] == paramTarget[i])
				{
					paramCurrent[i] = paramTarget[i];
					continue;
				}

				float step = (float)(speed * dt);
				paramCurrent[i] = paramCurrent[i] < paramTarget[i] ? Math.Min(paramTarget[i], paramCurrent[i] + step) : Math.Max(paramTarget[i], paramCurrent[i] - step);
			}
		}

		private float BuiltInValue(Mixer mixer, BuiltInParameter builtIn)
		{
			Vector3 d = position - mixer.ListenerPosition;
			switch (builtIn)
			{
				case BuiltInParameter.Distance:
					return has3D ? d.Length : 0f;
				case BuiltInParameter.Direction:
				{
					if (!has3D || d.LengthSquared < 1e-8f)
					{
						return 0f;
					}

					float x = Vector3.Dot(d, mixer.ListenerRight);
					float z = Vector3.Dot(d, mixer.ListenerForward);
					return MathHelper.RadiansToDegrees(MathF.Atan2(x, z));
				}
				case BuiltInParameter.Elevation:
				{
					if (!has3D || d.LengthSquared < 1e-8f)
					{
						return 0f;
					}

					float y = Vector3.Dot(d.Normalized(), mixer.ListenerUp);
					return MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(y, -1f, 1f)));
				}
				case BuiltInParameter.Speed:
					return has3D ? (velocity - mixer.ListenerVelocity).Length : 0f;
				default:
					return 0f;
			}
		}

		private void UpdateSpatial(Mixer mixer)
		{
			AudioSpatializer spatializer = Event.Spatializer;
			if (spatializer == null || !spatializer.Enabled || !has3D)
			{
				attenuationTarget = 1f;
				panLeft = 1f;
				panRight = 1f;
				dopplerCurrent += (1f - dopplerCurrent) * 0.2f;
				foreach (InstrumentRuntime runtime in instruments)
				{
					foreach (Voice voice in runtime.Voices)
					{
						voice.PanLeft = 1f;
						voice.PanRight = 1f;
					}

					foreach (Voice voice in runtime.SourceVoices)
					{
						voice.PanLeft = 1f;
						voice.PanRight = 1f;
					}
				}

				return;
			}

			Vector3 d = position - mixer.ListenerPosition;
			float distance = d.Length;
			attenuationTarget = spatializer.Attenuation(distance);
			ComputePan(mixer, d, distance, spatializer, out panLeft, out panRight);
			foreach (InstrumentRuntime runtime in instruments)
			{
				ApplyPan(mixer, runtime.Voices, d, spatializer);
				ApplyPan(mixer, runtime.SourceVoices, d, spatializer);
			}

			// Doppler: the velocities along the line between listener and source.
			float scale = Event.DopplerScale * mixer.DopplerScale;
			float target = 1f;
			if (scale > 0f && distance > 0.01f)
			{
				Vector3 direction = d / distance;
				float c = Math.Max(1f, mixer.SpeedOfSound);
				float listenerAlong = Vector3.Dot(mixer.ListenerVelocity, direction) * scale;
				float sourceAlong = Vector3.Dot(velocity, direction) * scale;
				float numerator = c + listenerAlong;
				float denominator = c + sourceAlong;
				if (denominator > 0.1f * c && numerator > 0f)
				{
					target = Math.Clamp(numerator / denominator, 0.25f, 4f);
				}
			}

			dopplerCurrent += (target - dopplerCurrent) * 0.2f;
		}

		private void ApplyPan(Mixer mixer, List<Voice> voices, Vector3 d, AudioSpatializer spatializer)
		{
			foreach (Voice voice in voices)
			{
				if (voice.HasOffset)
				{
					Vector3 od = d + voice.Offset;
					ComputePan(mixer, od, od.Length, spatializer, out voice.PanLeft, out voice.PanRight);
				}
				else
				{
					voice.PanLeft = panLeft;
					voice.PanRight = panRight;
				}
			}
		}

		private static void ComputePan(Mixer mixer, Vector3 d, float distance, AudioSpatializer spatializer, out float left, out float right)
		{
			float x = 0f;
			if (distance > 1e-4f)
			{
				Vector3 direction = d / distance;
				x = Vector3.Dot(direction, mixer.ListenerRight);
				// Closer than the minimum distance the source surrounds the listener.
				float min = Math.Max(0.001f, spatializer.MinDistance);
				if (distance < min)
				{
					x *= distance / min;
				}
			}

			x *= 1f - Math.Clamp(spatializer.Spread, 0f, 360f) / 360f;
			float angle = (Math.Clamp(x, -1f, 1f) + 1f) * (MathF.PI * 0.25f);
			// Constant power, scaled so a centered source keeps its level.
			left = MathF.Cos(angle) * 1.41421356f;
			right = MathF.Sin(angle) * 1.41421356f;
			left = Math.Min(1f, left);
			right = Math.Min(1f, right);
		}

		private void UpdateAutomation()
		{
			foreach (TrackRuntime track in tracks)
			{
				track.ApplyAutomation(paramCurrent);
			}

			master.ApplyAutomation(paramCurrent);
		}

		private void ClearBuffers(int frames)
		{
			foreach (TrackRuntime track in tracks)
			{
				Array.Clear(track.Buffer, 0, frames * 2);
			}

			Array.Clear(master.Buffer, 0, frames * 2);
		}

		/// <summary>Parameter sheet instruments trigger when their parameter enters the region and untrigger when it leaves.</summary>
		private void UpdateSheets(Mixer mixer)
		{
			if (state == PlaybackState.Stopping)
			{
				return;
			}

			foreach (InstrumentRuntime runtime in instruments)
			{
				CompiledInstrument instrument = runtime.Instrument;
				if (instrument.Sheet < 0 || instrument.Sheet >= paramCurrent.Length)
				{
					continue;
				}

				float value = paramCurrent[instrument.Sheet];
				bool inside = value >= instrument.Start - 1e-6 && value <= instrument.End + 1e-6;
				if (inside && !runtime.Triggered)
				{
					Trigger(mixer, runtime, 0, 0, 0f, false);
				}
				else if (!inside && runtime.Triggered)
				{
					Untrigger(mixer, runtime, -1f);
				}
			}
		}

		// ---------------------------------------------------------------- timeline

		/// <summary>The first sample in (<paramref name="from" />, <paramref name="to" />] where something happens, or long.MaxValue.</summary>
		private long NextBoundary(long from, long to)
		{
			// No closures here: this runs on the audio thread for every segment.
			long best = long.MaxValue;
			foreach (int index in Event.TimelineOrder)
			{
				InstrumentRuntime runtime = instruments[index];
				CompiledInstrument instrument = runtime.Instrument;
				if (instrument.StartSample > to)
				{
					break;
				}

				Consider(ref best, runtime.Triggered ? instrument.EndSample : instrument.StartSample, from, to);
			}

			foreach (InstrumentRuntime runtime in instruments)
			{
				if (runtime.PendingTriggerAt != NoSample)
				{
					Consider(ref best, runtime.PendingTriggerAt, from, to);
				}

				if (runtime.Triggered && runtime.NextSpawnAt != NoSample)
				{
					Consider(ref best, runtime.NextSpawnAt, from, to);
				}
			}

			foreach (CompiledTransition transition in Event.Transitions)
			{
				if (transition.Position > to)
				{
					break;
				}

				if (!transition.IsRegion)
				{
					Consider(ref best, transition.Position, from, to);
				}
			}

			if (pendingTransition != null)
			{
				Consider(ref best, pendingTransitionAt, from, to);
			}

			foreach (CompiledLoop loop in Event.Loops)
			{
				if (loop.End > to)
				{
					break;
				}

				Consider(ref best, loop.End, from, to);
			}

			foreach (CompiledMarkerPoint point in Event.Points)
			{
				if (point.Position > to)
				{
					break;
				}

				if (point.Kind == MarkerPointKind.Sustain && point.Position != sustainedAt)
				{
					Consider(ref best, point.Position, from, to);
				}
			}

			return best;
		}

		private static void Consider(ref long best, long sample, long from, long to)
		{
			if (sample > from && sample <= to && sample < best)
			{
				best = sample;
			}
		}

		/// <summary>Moves the cursor to <paramref name="to" /> (no boundary in between), firing beats and markers passed.</summary>
		private void AdvanceCursor(Mixer mixer, long to)
		{
			long from = cursor;
			cursor = to;
			NotifyPassedPoints(mixer, from, to);
			FireBeats(mixer, to);
			// Transition regions: a condition that holds inside the region schedules the jump at the next grid point.
			UpdatePendingTransition(mixer);
		}

		private void NotifyPassedPoints(Mixer mixer, long from, long to)
		{
			foreach (CompiledMarkerPoint point in Event.Points)
			{
				if (point.Kind == MarkerPointKind.Destination && point.Position > from && point.Position <= to)
				{
					mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.MarkerReached, InstanceId = Id, Text = point.Name ?? "", Position = point.Position / (double)Mixer.SampleRate });
				}
			}
		}

		private void FireBeats(Mixer mixer, long to)
		{
			if (!Event.HasTempo)
			{
				return;
			}

			int guard = 0;
			while (nextBeat != NoSample && nextBeat <= to && guard++ < 64)
			{
				NotifyBeat(mixer, nextBeat);
				nextBeat = NextBeatAfter(nextBeat);
			}
		}

		private void UpdatePendingTransition(Mixer mixer)
		{
			if (activeTransition != null)
			{
				return;
			}

			if (pendingTransition != null)
			{
				bool stillValid = cursor >= pendingTransition.Position && cursor < pendingTransition.End && ConditionsHold(pendingTransition.Conditions);
				if (!stillValid)
				{
					pendingTransition = null;
					pendingTransitionAt = NoSample;
				}
				else
				{
					return;
				}
			}

			foreach (CompiledTransition transition in Event.Transitions)
			{
				if (!transition.IsRegion || transition.Destination == -2 || cursor < transition.Position || cursor >= transition.End || !ConditionsHold(transition.Conditions))
				{
					continue;
				}

				long at = transition.Quantization == QuantizationInterval.None
					? cursor
					: EventCompiler.ToSamples(Event.Grid.NextGridPoint(transition.Quantization, cursor / (double)Mixer.SampleRate, 1.0 / Mixer.SampleRate), Mixer.SampleRate);
				if (at < cursor)
				{
					at = cursor;
				}

				if (at >= transition.End)
				{
					// No grid point before the region ends: no transition from this region.
					continue;
				}

				if (transition.Probability < 100f && Random.NextDouble() * 100.0 >= transition.Probability)
				{
					// Rolled out: the region is skipped this pass.
					continue;
				}

				pendingTransition = transition;
				pendingTransitionAt = at;
				break;
			}

			if (pendingTransition != null && pendingTransitionAt == cursor)
			{
				CompiledTransition transition = pendingTransition;
				pendingTransition = null;
				pendingTransitionAt = NoSample;
				TakeTransition(mixer, -1, transition);
			}
		}

		/// <summary>A transition fires: an instant jump, or the start of its transition timeline.</summary>
		private void TakeTransition(Mixer mixer, int blockOffset, CompiledTransition transition)
		{
			if (transition.Timeline != null)
			{
				BeginTransition(mixer, transition);
			}
			else
			{
				JumpTo(mixer, blockOffset, transition.Destination < 0 ? -1 : transition.Destination, transition.Crossfade, false);
			}
		}

		/// <summary>Everything that happens at the cursor's sample (loops and transitions first: a jump ends the handling).</summary>
		private void HandleBoundary(Mixer mixer, int blockOffset)
		{
			long at = cursor;
			for (int i = 0; i < Event.Loops.Length; i++)
			{
				CompiledLoop loop = Event.Loops[i];
				if (loop.End != at || loop.Start >= loop.End)
				{
					continue;
				}

				if (!ConditionsHold(loop.Conditions) || (loop.MaxLoops > 0 && loopCounts[i] >= loop.MaxLoops))
				{
					continue;
				}

				loopCounts[i]++;
				JumpTo(mixer, blockOffset, loop.Start, 0.0, false);
				return;
			}

			foreach (CompiledTransition transition in Event.Transitions)
			{
				if (transition.IsRegion || transition.Position != at || transition.Destination == -2)
				{
					continue;
				}

				if (!ConditionsHold(transition.Conditions) || (transition.Probability < 100f && Random.NextDouble() * 100.0 >= transition.Probability))
				{
					continue;
				}

				TakeTransition(mixer, blockOffset, transition);
				return;
			}

			if (pendingTransition != null && pendingTransitionAt == at)
			{
				CompiledTransition transition = pendingTransition;
				pendingTransition = null;
				pendingTransitionAt = NoSample;
				if (ConditionsHold(transition.Conditions))
				{
					TakeTransition(mixer, blockOffset, transition);
					return;
				}
			}

			foreach (int index in Event.TimelineOrder)
			{
				InstrumentRuntime runtime = instruments[index];
				CompiledInstrument instrument = runtime.Instrument;
				if (instrument.StartSample > at)
				{
					break;
				}

				if (!runtime.Triggered && instrument.StartSample == at)
				{
					Trigger(mixer, runtime, blockOffset, 0, 0f, false);
				}
				else if (runtime.Triggered && instrument.EndSample == at)
				{
					Untrigger(mixer, runtime, -1f);
				}
			}

			foreach (InstrumentRuntime runtime in instruments)
			{
				if (runtime.PendingTriggerAt == at)
				{
					runtime.PendingTriggerAt = NoSample;
					long offset = runtime.Instrument.OnTimeline && !runtime.Instrument.Async ? Math.Max(0, at - runtime.Instrument.StartSample) : 0;
					FireTrigger(mixer, runtime, blockOffset, offset, runtime.PendingAttack);
				}

				if (runtime.Triggered && runtime.NextSpawnAt == at)
				{
					SpawnScatter(mixer, runtime, blockOffset);
				}
			}

			foreach (CompiledMarkerPoint point in Event.Points)
			{
				if (point.Kind == MarkerPointKind.Sustain && point.Position == at && sustainedAt != at && state == PlaybackState.Playing)
				{
					sustainedAt = at;
					state = PlaybackState.Sustaining;
					mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.Sustained, InstanceId = Id, Text = point.Name ?? "", Position = at / (double)Mixer.SampleRate });
				}
			}
		}

		/// <summary>
		///     Moves the cursor to <paramref name="destination" /> (-1: past the end): instruments left are untriggered,
		///     ones entered are triggered at the right offset, ones spanning both restart at the new offset.
		/// </summary>
		private void JumpTo(Mixer mixer, int blockOffset, long destination, double crossfade, bool initial)
		{
			if (destination < 0)
			{
				destination = Math.Max(Event.LengthSamples, cursor);
				reachedEnd = true;
			}

			// A transition region whose destination lies inside itself would jump forever within one sample.
			if (jumpDepth >= 8)
			{
				cursor = destination;
				pendingTransition = null;
				pendingTransitionAt = NoSample;
				return;
			}

			jumpDepth++;
			try
			{
				JumpToCore(mixer, blockOffset, destination, crossfade, initial);
			}
			finally
			{
				jumpDepth--;
			}
		}

		private void JumpToCore(Mixer mixer, int blockOffset, long destination, double crossfade, bool initial)
		{
			float fade = (float)crossfade;
			foreach (int index in Event.TimelineOrder)
			{
				InstrumentRuntime runtime = instruments[index];
				CompiledInstrument instrument = runtime.Instrument;
				bool inside = instrument.StartSample <= destination && destination < instrument.EndSample;
				if (runtime.Triggered)
				{
					if (!inside)
					{
						Untrigger(mixer, runtime, fade > 0f ? fade : -1f);
					}
					else if (!instrument.Async && !initial)
					{
						// Timelocked: restart at the new offset (a short fade hides the cut).
						ReleaseVoices(mixer, runtime, fade > 0f ? fade : 0.003f);
						runtime.Nested?.SetTimelinePosition(mixer, destination - instrument.StartSample);
						if (runtime.Nested == null)
						{
							runtime.Triggered = false;
							Trigger(mixer, runtime, blockOffset, destination - instrument.StartSample, fade > 0f ? fade : 0.003f, true);
						}
					}
				}
				else if (inside)
				{
					Trigger(mixer, runtime, blockOffset, destination - instrument.StartSample, fade, !initial);
				}
			}

			cursor = destination;
			AfterCursorJump(mixer, destination, initial);
		}

		/// <summary>Bookkeeping after the cursor landed somewhere new: pending transitions, loop counts, beats, markers at the spot.</summary>
		private void AfterCursorJump(Mixer mixer, long destination, bool initial)
		{
			pendingTransition = null;
			pendingTransitionAt = NoSample;
			for (int i = 0; i < Event.Loops.Length; i++)
			{
				CompiledLoop loop = Event.Loops[i];
				if (destination < loop.Start || destination >= loop.End)
				{
					loopCounts[i] = 0;
				}
			}

			if (sustainedAt != destination)
			{
				sustainedAt = NoSample;
			}

			nextBeat = Event.HasTempo ? NextBeatAfter(destination - 1) : NoSample;
			foreach (CompiledMarkerPoint point in Event.Points)
			{
				if (point.Position == destination)
				{
					if (point.Kind == MarkerPointKind.Destination)
					{
						mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.MarkerReached, InstanceId = Id, Text = point.Name ?? "", Position = destination / (double)Mixer.SampleRate });
					}
					else if (point.Kind == MarkerPointKind.Sustain && state == PlaybackState.Playing && !initial)
					{
						sustainedAt = destination;
						state = PlaybackState.Sustaining;
					}
				}
			}

			if (Event.HasTempo && nextBeat == destination)
			{
				NotifyBeat(mixer, destination);
				nextBeat = NextBeatAfter(destination);
			}

			UpdatePendingTransition(mixer);
		}

		private long NextBeatAfter(long sample)
		{
			double seconds = (sample + 1) / (double)Mixer.SampleRate;
			double next = Event.Grid.NextGridPoint(QuantizationInterval.Beat, seconds, 0.0);
			long result = EventCompiler.ToSamples(next, Mixer.SampleRate);
			return result <= sample ? sample + 1 : result;
		}

		private void NotifyBeat(Mixer mixer, long sample)
		{
			double seconds = sample / (double)Mixer.SampleRate;
			Event.Grid.BarBeat(seconds + 1e-6, out int bar, out int beat, out _);
			mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.Beat, InstanceId = Id, Bar = bar, Beat = beat, Tempo = Event.Grid.At(seconds).Bpm, Position = seconds });
		}

		private bool ConditionsHold(CompiledCondition[] conditions)
		{
			foreach (CompiledCondition condition in conditions)
			{
				if (condition.Parameter < 0 || condition.Parameter >= paramCurrent.Length || !condition.Test(paramCurrent[condition.Parameter]))
				{
					return false;
				}
			}

			return true;
		}

		// ---------------------------------------------------------------- transition timelines

		/// <summary>
		///     The transition's timeline starts: the main cursor stops, the sounds playing on keep playing as the source
		///     (fading out towards the source region's end), the destination's sounds start at the destination region.
		/// </summary>
		private void BeginTransition(Mixer mixer, CompiledTransition transition)
		{
			CompiledTransitionTimeline timeline = transition.Timeline;
			activeTransition = transition;
			transitionCursor = 0;
			transitionSource = cursor;
			transitionToEnd = transition.Destination < 0;
			transitionDestination = transitionToEnd ? Event.LengthSamples : transition.Destination;
			destinationActive = false;
			sourceAlive = true;
			pendingTransition = null;
			pendingTransitionAt = NoSample;
			foreach (int index in Event.TimelineOrder)
			{
				InstrumentRuntime runtime = instruments[index];
				runtime.PendingTriggerAt = NoSample;
				runtime.DestinationFading = false;
				if (runtime.Triggered && !runtime.Instrument.Async)
				{
					// Timelocked sounds continue as the source until the source region ends.
					runtime.SourceExtended = true;
					runtime.SourceVoices.AddRange(runtime.Voices);
					runtime.Voices.Clear();
				}
			}

			foreach (int index in transition.Instruments)
			{
				InstrumentRuntime runtime = instruments[index];
				runtime.Triggered = false;
				runtime.PendingTriggerAt = NoSample;
			}

			if (timeline.DestinationStart == 0)
			{
				ActivateDestination(mixer);
			}

			ActiveTransitionIndex = transition.Index;
		}

		/// <summary>The destination region starts: the destination's sounds trigger as after a jump, fading in.</summary>
		private void ActivateDestination(Mixer mixer)
		{
			destinationActive = true;
			if (transitionToEnd)
			{
				return;
			}

			long destination = transitionDestination;
			foreach (int index in Event.TimelineOrder)
			{
				InstrumentRuntime runtime = instruments[index];
				CompiledInstrument instrument = runtime.Instrument;
				bool inside = instrument.StartSample <= destination && destination < instrument.EndSample;
				if (!inside)
				{
					continue;
				}

				if (runtime.SourceExtended)
				{
					// The same sound plays on as the source and starts again at the destination's offset.
					runtime.Triggered = false;
				}
				else if (runtime.Triggered && !instrument.Async)
				{
					runtime.Triggered = false;
				}

				if (!runtime.Triggered)
				{
					runtime.DestinationFading = true;
					TriggerNow(mixer, runtime, instrument.Async ? 0 : destination - instrument.StartSample);
				}
			}

			foreach (CompiledMarkerPoint point in Event.Points)
			{
				if (point.Kind == MarkerPointKind.Destination && point.Position == destination)
				{
					mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.MarkerReached, InstanceId = Id, Text = point.Name ?? "", Position = destination / (double)Mixer.SampleRate });
				}
			}

			nextBeat = Event.HasTempo ? NextBeatAfter(destination - 1) : NoSample;
			if (Event.HasTempo && nextBeat == destination)
			{
				NotifyBeat(mixer, destination);
				nextBeat = NextBeatAfter(destination);
			}
		}

		/// <summary>The destination's cursor while the transition runs (meaningful once the destination region started).</summary>
		private long DestinationCursor => transitionDestination + Math.Max(0, transitionCursor - activeTransition.Timeline.DestinationStart);

		/// <summary>The first sample of the transition timeline in (<paramref name="from" />, <paramref name="to" />] where something happens.</summary>
		private long NextTransitionBoundary(long from, long to)
		{
			CompiledTransitionTimeline timeline = activeTransition.Timeline;
			long best = long.MaxValue;
			Consider(ref best, timeline.Length, from, to);
			if (!destinationActive)
			{
				Consider(ref best, timeline.DestinationStart, from, to);
			}

			if (sourceAlive)
			{
				Consider(ref best, timeline.SourceLength, from, to);
				foreach (int index in Event.TimelineOrder)
				{
					InstrumentRuntime runtime = instruments[index];
					if (runtime.SourceExtended)
					{
						Consider(ref best, runtime.Instrument.EndSample - transitionSource, from, to);
					}
				}
			}

			foreach (int index in activeTransition.Instruments)
			{
				InstrumentRuntime runtime = instruments[index];
				Consider(ref best, runtime.Triggered ? runtime.Instrument.EndSample : runtime.Instrument.StartSample, from, to);
			}

			if (destinationActive && !transitionToEnd)
			{
				long shift = timeline.DestinationStart - transitionDestination;
				foreach (int index in Event.TimelineOrder)
				{
					InstrumentRuntime runtime = instruments[index];
					CompiledInstrument instrument = runtime.Instrument;
					Consider(ref best, (runtime.Triggered ? instrument.EndSample : instrument.StartSample) + shift, from, to);
				}
			}

			return best;
		}

		private void AdvanceTransitionCursor(Mixer mixer, long to)
		{
			long from = transitionCursor;
			transitionCursor = to;
			if (destinationActive && !transitionToEnd)
			{
				long shift = activeTransition.Timeline.DestinationStart - transitionDestination;
				NotifyPassedPoints(mixer, from - shift, to - shift);
				FireBeats(mixer, to - shift);
			}
		}

		private void HandleTransitionBoundary(Mixer mixer)
		{
			CompiledTransitionTimeline timeline = activeTransition.Timeline;
			long at = transitionCursor;
			if (at >= timeline.Length)
			{
				EndTransition(mixer);
				return;
			}

			if (!destinationActive && at == timeline.DestinationStart)
			{
				ActivateDestination(mixer);
			}

			if (sourceAlive)
			{
				if (at == timeline.SourceLength)
				{
					ReleaseSource(mixer, Mixer.MinimumRelease);
				}
				else
				{
					foreach (int index in Event.TimelineOrder)
					{
						InstrumentRuntime runtime = instruments[index];
						if (runtime.SourceExtended && runtime.Instrument.EndSample - transitionSource == at)
						{
							ReleaseSourceVoices(mixer, runtime, -1f);
						}
					}
				}
			}

			foreach (int index in activeTransition.Instruments)
			{
				InstrumentRuntime runtime = instruments[index];
				CompiledInstrument instrument = runtime.Instrument;
				if (!runtime.Triggered && instrument.StartSample == at)
				{
					runtime.Triggered = true;
					if (PassesTriggerChecks(instrument))
					{
						FireTrigger(mixer, runtime, 0, 0, 0f);
					}
				}
				else if (runtime.Triggered && instrument.EndSample == at)
				{
					Untrigger(mixer, runtime, -1f);
				}
			}

			if (destinationActive && !transitionToEnd)
			{
				long destinationCursor = DestinationCursor;
				foreach (int index in Event.TimelineOrder)
				{
					InstrumentRuntime runtime = instruments[index];
					CompiledInstrument instrument = runtime.Instrument;
					if (instrument.StartSample > destinationCursor)
					{
						break;
					}

					if (!runtime.Triggered && instrument.StartSample == destinationCursor)
					{
						runtime.DestinationFading = true;
						runtime.Triggered = true;
						if (PassesTriggerChecks(instrument))
						{
							FireTrigger(mixer, runtime, 0, 0, 0f);
						}
					}
					else if (runtime.Triggered && instrument.EndSample == destinationCursor)
					{
						Untrigger(mixer, runtime, -1f);
					}
				}
			}
		}

		/// <summary>The fades of the source and destination regions for this block.</summary>
		private void UpdateTransitionGains()
		{
			CompiledTransitionTimeline timeline = activeTransition.Timeline;
			float sourceGain = timeline.SourceGain(transitionCursor);
			float destinationGain = timeline.DestinationGain(transitionCursor);
			foreach (InstrumentRuntime runtime in instruments)
			{
				foreach (Voice voice in runtime.SourceVoices)
				{
					voice.FadeGain = sourceGain;
				}

				if (runtime.DestinationFading)
				{
					foreach (Voice voice in runtime.Voices)
					{
						voice.FadeGain = destinationGain;
					}
				}
			}
		}

		private void ReleaseSource(Mixer mixer, float fade)
		{
			sourceAlive = false;
			foreach (InstrumentRuntime runtime in instruments)
			{
				if (runtime.SourceExtended)
				{
					ReleaseSourceVoices(mixer, runtime, fade);
				}
			}
		}

		private void ReleaseSourceVoices(Mixer mixer, InstrumentRuntime runtime, float fade)
		{
			runtime.SourceExtended = false;
			foreach (Voice voice in runtime.SourceVoices)
			{
				voice.Envelope.Release(fade);
				runtime.Fading.Add(voice);
			}

			runtime.SourceVoices.Clear();
			if (runtime.Nested != null && !runtime.DestinationFading && !runtime.Triggered)
			{
				runtime.Nested.Stop(mixer, AudioStopMode.AllowFadeout);
				runtime.Nested = null;
			}
		}

		/// <summary>The transition timeline ended: playback continues on the destination timeline where the destination region got to.</summary>
		private void EndTransition(Mixer mixer)
		{
			CompiledTransitionTimeline timeline = activeTransition.Timeline;
			if (!destinationActive)
			{
				ActivateDestination(mixer);
			}

			long destination = transitionToEnd ? Math.Max(Event.LengthSamples, transitionDestination) : DestinationCursor;
			if (sourceAlive)
			{
				ReleaseSource(mixer, Mixer.MinimumRelease);
			}

			foreach (int index in activeTransition.Instruments)
			{
				InstrumentRuntime runtime = instruments[index];
				if (runtime.Triggered)
				{
					Untrigger(mixer, runtime, -1f);
				}
			}

			foreach (InstrumentRuntime runtime in instruments)
			{
				runtime.DestinationFading = false;
				foreach (Voice voice in runtime.Voices)
				{
					voice.FadeGain = 1f;
				}
			}

			activeTransition = null;
			ActiveTransitionIndex = -1;
			cursor = destination;
			if (transitionToEnd)
			{
				reachedEnd = true;
				foreach (int index in Event.TimelineOrder)
				{
					InstrumentRuntime runtime = instruments[index];
					if (runtime.Triggered)
					{
						Untrigger(mixer, runtime, -1f);
					}
				}
			}
			else
			{
				// The timeline instruments agree with the new cursor.
				foreach (int index in Event.TimelineOrder)
				{
					InstrumentRuntime runtime = instruments[index];
					CompiledInstrument instrument = runtime.Instrument;
					bool inside = instrument.StartSample <= cursor && cursor < instrument.EndSample;
					if (runtime.Triggered && !inside && !instrument.Async)
					{
						Untrigger(mixer, runtime, -1f);
					}
					else if (!runtime.Triggered && inside)
					{
						Trigger(mixer, runtime, 0, instrument.Async ? 0 : cursor - instrument.StartSample, 0f, true);
					}
				}
			}

			AfterCursorJump(mixer, cursor, false);
		}

		/// <summary>Drops a transition in progress (a seek or a live update): the destination region's sounds stay, the rest fades.</summary>
		private void AbortTransition(Mixer mixer)
		{
			if (activeTransition == null)
			{
				return;
			}

			long destination = destinationActive && !transitionToEnd ? DestinationCursor : transitionSource;
			ReleaseSource(mixer, Mixer.MinimumRelease);
			foreach (int index in activeTransition.Instruments)
			{
				InstrumentRuntime runtime = instruments[index];
				if (runtime.Triggered)
				{
					Untrigger(mixer, runtime, -1f);
				}
			}

			foreach (InstrumentRuntime runtime in instruments)
			{
				runtime.DestinationFading = false;
				foreach (Voice voice in runtime.Voices)
				{
					voice.FadeGain = 1f;
				}
			}

			activeTransition = null;
			ActiveTransitionIndex = -1;
			cursor = destination;
		}

		private void ClearTransition()
		{
			activeTransition = null;
			ActiveTransitionIndex = -1;
			transitionCursor = 0;
			destinationActive = false;
			sourceAlive = false;
			foreach (InstrumentRuntime runtime in instruments)
			{
				runtime.SourceVoices.Clear();
			}
		}

		// ---------------------------------------------------------------- instruments

		private bool PassesTriggerChecks(CompiledInstrument instrument)
		{
			if (instrument.Kind == InstrumentKind.Silent || !ConditionsHold(instrument.Conditions))
			{
				return false;
			}

			return !(instrument.Probability < 100f && Random.NextDouble() * 100.0 >= instrument.Probability);
		}

		/// <summary>The region was entered: the instrument plays (now, or at its quantized / delayed time) when its conditions allow.</summary>
		private void Trigger(Mixer mixer, InstrumentRuntime runtime, int blockOffset, long offset, float attackOverride, bool fromJump)
		{
			CompiledInstrument instrument = runtime.Instrument;
			runtime.Triggered = true;
			runtime.PendingTriggerAt = NoSample;
			if (!PassesTriggerChecks(instrument))
			{
				return;
			}

			long due = cursor;
			if (instrument.Quantization != QuantizationInterval.None)
			{
				double at = Event.Grid.NextGridPoint(instrument.Quantization, cursor / (double)Mixer.SampleRate, 1.0 / Mixer.SampleRate);
				due = Math.Max(due, EventCompiler.ToSamples(at, Mixer.SampleRate));
			}

			if (instrument.TriggerDelayMax > 0.0)
			{
				double delay = instrument.TriggerDelayMin + Random.NextDouble() * (instrument.TriggerDelayMax - instrument.TriggerDelayMin);
				due += EventCompiler.ToSamples(delay, Mixer.SampleRate);
			}

			if (due > cursor && activeTransition == null)
			{
				runtime.PendingTriggerAt = due;
				runtime.PendingAttack = attackOverride;
				runtime.PendingFromJump = fromJump;
				return;
			}

			FireTrigger(mixer, runtime, blockOffset, offset, attackOverride);
		}

		/// <summary>Triggers without quantization or delay (inside transition timelines).</summary>
		private void TriggerNow(Mixer mixer, InstrumentRuntime runtime, long offset)
		{
			runtime.Triggered = true;
			runtime.PendingTriggerAt = NoSample;
			if (PassesTriggerChecks(runtime.Instrument))
			{
				FireTrigger(mixer, runtime, 0, offset, 0f);
			}
		}

		private void FireTrigger(Mixer mixer, InstrumentRuntime runtime, int blockOffset, long offset, float attackOverride)
		{
			CompiledInstrument instrument = runtime.Instrument;
			switch (instrument.Kind)
			{
				case InstrumentKind.Single:
				case InstrumentKind.Multi:
				{
					CompiledPlaylistEntry entry = Pick(runtime);
					if (entry != null)
					{
						StartVoice(mixer, runtime, entry, blockOffset, instrument.Async ? 0 : offset, attackOverride, Vector3.Zero, false);
					}

					break;
				}
				case InstrumentKind.Scatterer:
					runtime.Spawned = 0;
					runtime.NextSpawnAt = cursor;
					SpawnScatter(mixer, runtime, blockOffset);
					break;
				case InstrumentKind.Event:
				{
					if (Depth >= 8)
					{
						break;
					}

					CompiledEvent nested = mixer.FindEvent(instrument.NestedEvent);
					if (nested == null)
					{
						if (!warnedNested)
						{
							warnedNested = true;
							Debug.LogWarning($"[Audio] Event '{Event.Path}': the nested event '{instrument.NestedEvent}' is not loaded");
						}

						break;
					}

					if (runtime.Nested != null)
					{
						runtime.Nested.Stop(mixer, AudioStopMode.AllowFadeout);
					}

					EventPlayback child = mixer.CreateNested(nested, this);
					child.Set3D(position, velocity, forward, Vector3.UnitY, has3D);
					child.Attach(mixer);
					child.Start(mixer, instrument.Async ? 0 : offset);
					runtime.Nested = child;
					break;
				}
				case InstrumentKind.Snapshot:
					mixer.StopOwnedSnapshot(runtime.Snapshot, false);
					runtime.Snapshot = mixer.StartOwnedSnapshot(instrument.SnapshotId);
					break;
			}
		}

		private void Untrigger(Mixer mixer, InstrumentRuntime runtime, float fade)
		{
			CompiledInstrument instrument = runtime.Instrument;
			runtime.Triggered = false;
			runtime.PendingTriggerAt = NoSample;
			runtime.NextSpawnAt = NoSample;
			runtime.DestinationFading = false;
			if (!instrument.Async || instrument.Loop)
			{
				ReleaseVoices(mixer, runtime, fade);
			}

			if (runtime.Nested != null)
			{
				if (!instrument.Async)
				{
					runtime.Nested.Stop(mixer, AudioStopMode.AllowFadeout);
				}

				runtime.Nested = null;
			}

			if (runtime.Snapshot != null)
			{
				mixer.StopOwnedSnapshot(runtime.Snapshot, false);
				runtime.Snapshot = null;
			}
		}

		private void ReleaseVoices(Mixer mixer, InstrumentRuntime runtime, float fade)
		{
			foreach (Voice voice in runtime.Voices)
			{
				voice.Envelope.Release(fade);
			}
		}

		private void SpawnScatter(Mixer mixer, InstrumentRuntime runtime, int blockOffset)
		{
			CompiledInstrument instrument = runtime.Instrument;
			if (instrument.SpawnTotal > 0 && runtime.Spawned >= instrument.SpawnTotal)
			{
				runtime.NextSpawnAt = NoSample;
				return;
			}

			CompiledPlaylistEntry entry = Pick(runtime);
			if (entry != null)
			{
				while (runtime.Voices.Count >= instrument.Polyphony)
				{
					Voice oldest = runtime.Voices[0];
					oldest.Envelope.Release(0.05f);
					runtime.Voices.RemoveAt(0);
					runtime.Fading.Add(oldest);
				}

				Vector3 offset = Vector3.Zero;
				bool hasOffset = false;
				if (instrument.MaxScatterDistance > 0f)
				{
					double angle = Random.NextDouble() * Math.PI * 2.0;
					float distance = instrument.MinScatterDistance + (float)Random.NextDouble() * (instrument.MaxScatterDistance - instrument.MinScatterDistance);
					offset = new Vector3((float)Math.Cos(angle) * distance, 0f, (float)Math.Sin(angle) * distance);
					hasOffset = true;
				}

				StartVoice(mixer, runtime, entry, blockOffset, 0, 0f, offset, hasOffset);
			}

			runtime.Spawned++;
			double interval = instrument.SpawnIntervalMin + Random.NextDouble() * (instrument.SpawnIntervalMax - instrument.SpawnIntervalMin);
			runtime.NextSpawnAt = cursor + Math.Max(1, EventCompiler.ToSamples(interval, Mixer.SampleRate));
		}

		private CompiledPlaylistEntry Pick(InstrumentRuntime runtime)
		{
			CompiledPlaylistEntry[] playlist = runtime.Instrument.Playlist;
			if (playlist.Length == 0)
			{
				return null;
			}

			if (playlist.Length == 1)
			{
				return playlist[0];
			}

			switch (runtime.Instrument.Mode)
			{
				case PlaylistMode.Sequential:
				{
					CompiledPlaylistEntry entry = playlist[runtime.PlaylistCursor % playlist.Length];
					runtime.PlaylistCursor = (runtime.PlaylistCursor + 1) % playlist.Length;
					return entry;
				}
				case PlaylistMode.SequentialGlobal:
				{
					int index = runtime.Instrument.Source != null ? SharedCursors.Next(runtime.Instrument.Source, playlist.Length) : 0;
					return playlist[index];
				}
				case PlaylistMode.Shuffle:
				{
					if (runtime.ShuffleBag.Count == 0)
					{
						for (int i = 0; i < playlist.Length; i++)
						{
							if (playlist[i].Weight > 0f && i != runtime.LastPick)
							{
								runtime.ShuffleBag.Add(i);
							}
						}

						if (runtime.ShuffleBag.Count == 0)
						{
							runtime.ShuffleBag.Add(runtime.LastPick >= 0 ? runtime.LastPick : 0);
						}
					}

					int pick = WeightedPick(runtime.ShuffleBag, playlist);
					runtime.ShuffleBag.Remove(pick);
					runtime.LastPick = pick;
					return playlist[pick];
				}
				default:
				{
					float total = 0f;
					foreach (CompiledPlaylistEntry entry in playlist)
					{
						total += entry.Weight;
					}

					if (total <= 0f)
					{
						return playlist[Random.Next(playlist.Length)];
					}

					double roll = Random.NextDouble() * total;
					foreach (CompiledPlaylistEntry entry in playlist)
					{
						roll -= entry.Weight;
						if (roll <= 0.0)
						{
							return entry;
						}
					}

					return playlist[playlist.Length - 1];
				}
			}
		}

		private int WeightedPick(List<int> candidates, CompiledPlaylistEntry[] playlist)
		{
			float total = 0f;
			foreach (int index in candidates)
			{
				total += playlist[index].Weight;
			}

			if (total <= 0f)
			{
				return candidates[Random.Next(candidates.Count)];
			}

			double roll = Random.NextDouble() * total;
			foreach (int index in candidates)
			{
				roll -= playlist[index].Weight;
				if (roll <= 0.0)
				{
					return index;
				}
			}

			return candidates[candidates.Count - 1];
		}

		private void StartVoice(Mixer mixer, InstrumentRuntime runtime, CompiledPlaylistEntry entry, int blockOffset, long timelineOffset, float attackOverride, Vector3 offset, bool hasOffset)
		{
			CompiledInstrument instrument = runtime.Instrument;
			SampleData sample = entry.Sample;
			if (sample == null || sample.Frames <= 0)
			{
				return;
			}

			double startSeconds = instrument.StartOffset + Random.NextDouble() * instrument.StartOffsetRandom + timelineOffset / (double)Mixer.SampleRate;
			long startFrame = (long)Math.Round(startSeconds * sample.SampleRate);
			if (instrument.Loop)
			{
				startFrame %= sample.Frames;
			}
			else if (startFrame >= sample.Frames)
			{
				return;
			}

			Voice voice = mixer.RentVoice();
			voice.Sample = sample;
			voice.Instrument = instrument.Index;
			voice.Track = instrument.Track;
			voice.Loop = instrument.Loop;
			voice.Gain = AudioMath.DbToGain(instrument.VolumeDb + entry.VolumeDb + AudioMath.Spread(Random, instrument.VolumeRandomDb));
			voice.BaseRatio = AudioMath.SemitonesToRatio(instrument.PitchSemitones + entry.PitchSemitones + AudioMath.Spread(Random, instrument.PitchRandomSemitones));
			voice.Position = startFrame;
			voice.EnvelopeShape = instrument.Envelope;
			voice.Envelope = EnvelopeRunner.Start(instrument.Envelope, attackOverride);
			voice.HasOffset = hasOffset;
			voice.Offset = offset;
			voice.PanLeft = panLeft;
			voice.PanRight = panRight;
			voice.FadeGain = runtime.DestinationFading && activeTransition != null ? activeTransition.Timeline.DestinationGain(transitionCursor) : 1f;
			if (sample.Streaming)
			{
				StreamingReader reader = runtime.TakeReader(sample);
				if (reader == null)
				{
					reader = new StreamingReader(sample, startFrame, instrument.Loop);
				}
				else
				{
					reader.Loop = instrument.Loop;
					if (startFrame != reader.BufferedStart || reader.BufferedEnd == reader.BufferedStart)
					{
						// Prefetched from another position: refill from the start frame.
						reader.Seek(startFrame);
					}
				}

				voice.Stream = reader;
				AudioStreamer.Register(reader);
			}

			runtime.Voices.Add(voice);
		}

		// ---------------------------------------------------------------- rendering

		private void RenderVoices(Mixer mixer, int blockOffset, int count)
		{
			double seconds = count / (double)Mixer.SampleRate;
			foreach (InstrumentRuntime runtime in instruments)
			{
				foreach (Voice voice in runtime.Voices)
				{
					RenderVoice(mixer, voice, blockOffset, count, seconds);
				}

				foreach (Voice voice in runtime.SourceVoices)
				{
					RenderVoice(mixer, voice, blockOffset, count, seconds);
				}

				foreach (Voice voice in runtime.Fading)
				{
					RenderVoice(mixer, voice, blockOffset, count, seconds);
				}
			}
		}

		private void RenderVoice(Mixer mixer, Voice voice, int blockOffset, int count, double seconds)
		{
			if (voice.Finished || voice.Track < 0 || voice.Track >= tracks.Length)
			{
				voice.Finished = true;
				return;
			}

			TrackRuntime track = tracks[voice.Track];
			SampleData sample = voice.Sample;
			float envStart = voice.Envelope.Level;
			float envEnd = voice.Envelope.Advance(voice.EnvelopeShape, seconds, Mixer.MinimumRelease);
			voice.Step = voice.BaseRatio * track.PitchRatio * master.PitchRatio * PitchRatio * dopplerCurrent * (sample.SampleRate / (double)Mixer.SampleRate);
			float targetLeft = voice.Gain * voice.FadeGain * voice.PanLeft;
			float targetRight = voice.Gain * voice.FadeGain * voice.PanRight;
			if (!voice.GainsInitialized)
			{
				voice.CurrentLeft = targetLeft;
				voice.CurrentRight = targetRight;
				voice.GainsInitialized = true;
			}

			voice.Audibility = voice.Gain * voice.FadeGain * envEnd * attenuationTarget * track.CurrentGain * Math.Max(voice.PanLeft, voice.PanRight);
			if (voice.Virtual)
			{
				AdvancePosition(voice, count * voice.Step);
			}
			else
			{
				float[] buffer = track.Buffer;
				int rendered = 0;
				float gainLeft = voice.CurrentLeft;
				float gainRight = voice.CurrentRight;
				while (rendered < count)
				{
					int chunk = Math.Min(count - rendered, (int)((mixer.FetchCapacityFrames - 8) / Math.Max(voice.Step, 0.01)));
					chunk = Math.Max(1, chunk);
					long baseFrame = (long)Math.Floor(voice.Position) - 1;
					int need = (int)Math.Ceiling(chunk * voice.Step) + 6;
					float[] source = mixer.Fetch(voice, baseFrame, need);
					double p = voice.Position - baseFrame;
					for (int i = 0; i < chunk; i++)
					{
						int index = (int)p;
						float t = (float)(p - index);
						int s0 = (index - 1) * 2;
						float fraction = (rendered + i + 1) / (float)count;
						float env = envStart + (envEnd - envStart) * fraction;
						float gl = gainLeft + (targetLeft - gainLeft) * fraction;
						float gr = gainRight + (targetRight - gainRight) * fraction;
						float left = Hermite(source[s0], source[s0 + 2], source[s0 + 4], source[s0 + 6], t);
						float right = Hermite(source[s0 + 1], source[s0 + 3], source[s0 + 5], source[s0 + 7], t);
						int o = (blockOffset + rendered + i) * 2;
						buffer[o] += left * gl * env;
						buffer[o + 1] += right * gr * env;
						p += voice.Step;
					}

					voice.Position = baseFrame + p;
					rendered += chunk;
					WrapPosition(voice);
				}

				voice.CurrentLeft = targetLeft;
				voice.CurrentRight = targetRight;
			}

			if (voice.Envelope.IsDone)
			{
				voice.Finished = true;
			}

			if (voice.Stream != null)
			{
				voice.Stream.Consume((long)voice.Position);
				if (voice.Stream.Underruns > 0)
				{
					mixer.StreamUnderruns += voice.Stream.Underruns;
					voice.Stream.Underruns = 0;
				}
			}
		}

		private static void AdvancePosition(Voice voice, double frames)
		{
			voice.Position += frames;
			WrapPosition(voice);
		}

		private static void WrapPosition(Voice voice)
		{
			long frames = voice.Sample.Frames;
			if (voice.Loop)
			{
				if (frames > 0 && voice.Position >= frames)
				{
					voice.Position %= frames;
				}
			}
			else if (voice.Position >= frames + 2)
			{
				voice.Finished = true;
			}
		}

		private static float Hermite(float y0, float y1, float y2, float y3, float t)
		{
			float c1 = 0.5f * (y2 - y0);
			float c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
			float c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
			return ((c3 * t + c2) * t + c1) * t + y1;
		}

		/// <summary>Track effects and gains into the master buffer, then the master's effects.</summary>
		private void MixTracks(Mixer mixer, int frames)
		{
			float[] masterBuffer = master.Buffer;
			float[] peaks = TrackPeaks;
			for (int t = 0; t < tracks.Length; t++)
			{
				TrackRuntime track = tracks[t];
				track.RunEffects(frames);
				float targetGain = track.Track.Silent ? 0f : AudioMath.DbToGain(track.VolumeDb);
				float startGain = track.CurrentGain;
				track.CurrentGain = targetGain;
				float pan = Math.Clamp(track.Pan, -1f, 1f);
				float panLeftGain = Math.Min(1f, 1f - pan);
				float panRightGain = Math.Min(1f, 1f + pan);
				float[] buffer = track.Buffer;
				float peak = 0f;
				for (int f = 0; f < frames; f++)
				{
					float gain = startGain + (targetGain - startGain) * (f + 1) / frames;
					float left = buffer[f * 2] * gain * panLeftGain;
					float right = buffer[f * 2 + 1] * gain * panRightGain;
					masterBuffer[f * 2] += left;
					masterBuffer[f * 2 + 1] += right;
					peak = Math.Max(peak, Math.Max(Math.Abs(left), Math.Abs(right)));
				}

				if (t < peaks.Length)
				{
					peaks[t] = peak;
				}
			}

			master.RunEffects(frames);
		}

		/// <summary>The master buffer through the event's volume, envelope and attenuation into the bus.</summary>
		private void WriteToBus(Mixer mixer, int frames, float envelopeStart, float envelopeEnd)
		{
			BusState bus = mixer.Bus(busIndex) ?? mixer.Bus(0);
			if (bus == null)
			{
				return;
			}

			float masterGain = master.Track.Mute ? 0f : AudioMath.DbToGain(master.VolumeDb);
			float pan = Math.Clamp(master.Pan, -1f, 1f);
			float targetLeft = masterGain * VolumeGain * envelopeEnd * attenuationTarget * Math.Min(1f, 1f - pan);
			float targetRight = masterGain * VolumeGain * envelopeEnd * attenuationTarget * Math.Min(1f, 1f + pan);
			if (!masterGainsInitialized)
			{
				masterGainsInitialized = true;
				masterCurrentLeft = masterGain * VolumeGain * envelopeStart * attenuationCurrent * Math.Min(1f, 1f - pan);
				masterCurrentRight = masterGain * VolumeGain * envelopeStart * attenuationCurrent * Math.Min(1f, 1f + pan);
			}

			float startLeft = masterCurrentLeft;
			float startRight = masterCurrentRight;
			float[] source = master.Buffer;
			float[] target = bus.Buffer;
			float peakLeft = 0f;
			float peakRight = 0f;
			for (int f = 0; f < frames; f++)
			{
				float fraction = (f + 1) / (float)frames;
				float gl = startLeft + (targetLeft - startLeft) * fraction;
				float gr = startRight + (targetRight - startRight) * fraction;
				float l = source[f * 2] * gl;
				float r = source[f * 2 + 1] * gr;
				target[f * 2] += l;
				target[f * 2 + 1] += r;
				if (Math.Abs(l) > peakLeft) peakLeft = Math.Abs(l);
				if (Math.Abs(r) > peakRight) peakRight = Math.Abs(r);
			}

			masterCurrentLeft = targetLeft;
			masterCurrentRight = targetRight;
			attenuationCurrent = attenuationTarget;
			PeakLeft = peakLeft;
			PeakRight = peakRight;
		}

		private void CleanupVoices(Mixer mixer)
		{
			foreach (InstrumentRuntime runtime in instruments)
			{
				CleanupList(mixer, runtime, runtime.Voices);
				CleanupList(mixer, runtime, runtime.SourceVoices);
				CleanupList(mixer, runtime, runtime.Fading);
				if (runtime.Nested != null && runtime.Nested.State == PlaybackState.Stopped)
				{
					runtime.Nested = null;
				}

				if (runtime.Snapshot != null && runtime.Snapshot.Done)
				{
					runtime.Snapshot = null;
				}
			}
		}

		private static void CleanupList(Mixer mixer, InstrumentRuntime runtime, List<Voice> voices)
		{
			for (int i = voices.Count - 1; i >= 0; i--)
			{
				Voice voice = voices[i];
				if (voice.Finished)
				{
					voices.RemoveAt(i);
					runtime.ReturnReader(voice);
					mixer.ReturnVoice(voice);
				}
			}
		}

		private void FreeVoices(Mixer mixer, float fade)
		{
			foreach (InstrumentRuntime runtime in instruments)
			{
				if (fade > 0f)
				{
					foreach (Voice voice in runtime.Voices)
					{
						voice.Envelope.Release(fade);
						runtime.Fading.Add(voice);
					}

					foreach (Voice voice in runtime.SourceVoices)
					{
						voice.Envelope.Release(fade);
						runtime.Fading.Add(voice);
					}

					runtime.Voices.Clear();
					runtime.SourceVoices.Clear();
					mixer.StopOwnedSnapshot(runtime.Snapshot, false);
					runtime.Snapshot = null;
					continue;
				}

				foreach (Voice voice in runtime.Voices)
				{
					runtime.ReturnReader(voice);
					mixer.ReturnVoice(voice);
				}

				foreach (Voice voice in runtime.SourceVoices)
				{
					runtime.ReturnReader(voice);
					mixer.ReturnVoice(voice);
				}

				foreach (Voice voice in runtime.Fading)
				{
					runtime.ReturnReader(voice);
					mixer.ReturnVoice(voice);
				}

				runtime.Voices.Clear();
				runtime.SourceVoices.Clear();
				runtime.Fading.Clear();
				runtime.Triggered = false;
				runtime.SourceExtended = false;
				runtime.DestinationFading = false;
				runtime.PendingTriggerAt = NoSample;
				runtime.NextSpawnAt = NoSample;
				mixer.StopOwnedSnapshot(runtime.Snapshot, true);
				runtime.Snapshot = null;
			}
		}

		private void MoveOrDrop(Mixer mixer, List<Voice> voices, float fade)
		{
			foreach (Voice voice in voices)
			{
				if (fade >= 0f)
				{
					voice.Envelope.Release(fade);
				}

				if (instruments.Length > 0 && tracks.Length > 0)
				{
					voice.Track = Math.Min(voice.Track, tracks.Length - 1);
					voice.Instrument = instruments[0].Instrument.Index;
					voice.FadeGain = 1f;
					instruments[0].Fading.Add(voice);
				}
				else
				{
					mixer.ReturnVoice(voice);
				}
			}
		}

		private static void Retarget(List<Voice> voices, InstrumentRuntime target, List<Voice> destination)
		{
			foreach (Voice voice in voices)
			{
				voice.Instrument = target.Instrument.Index;
				voice.Track = target.Instrument.Track;
				voice.FadeGain = 1f;
				destination.Add(voice);
			}
		}

		private void StopNested(Mixer mixer, AudioStopMode mode)
		{
			foreach (InstrumentRuntime runtime in instruments)
			{
				if (runtime.Nested != null)
				{
					runtime.Nested.Stop(mixer, mode);
					runtime.Nested.ReleaseWhenStopped = true;
					runtime.Nested = null;
				}
			}
		}

		private bool HasContent()
		{
			foreach (InstrumentRuntime runtime in instruments)
			{
				if (runtime.Voices.Count > 0 || runtime.Fading.Count > 0 || runtime.SourceVoices.Count > 0 || runtime.PendingTriggerAt != NoSample)
				{
					return true;
				}

				if (runtime.Nested != null && runtime.Nested.IsPlaying)
				{
					return true;
				}

				if (runtime.Instrument.Sheet >= 0 && runtime.Triggered)
				{
					return true;
				}

				if (runtime.Triggered && runtime.Instrument.Kind == InstrumentKind.Scatterer && runtime.NextSpawnAt != NoSample)
				{
					return true;
				}
			}

			return state == PlaybackState.Sustaining || activeTransition != null;
		}

		private void Finish(Mixer mixer)
		{
			FreeVoices(mixer, 0f);
			StopNested(mixer, AudioStopMode.Immediate);
			ClearTransition();
			state = PlaybackState.Stopped;
			StateValue = (int)state;
			mixer.Notify(new AudioNotification { Kind = AudioNotificationKind.Stopped, InstanceId = Id });
		}

		private EventPlayback PickVictim(Mixer mixer, List<EventPlayback> candidates)
		{
			EventPlayback victim = null;
			switch (Event.Stealing)
			{
				case StealingMode.None:
					return null;
				case StealingMode.Quietest:
				{
					float quietest = float.MaxValue;
					foreach (EventPlayback candidate in candidates)
					{
						float level = Math.Max(candidate.PeakLeft, candidate.PeakRight);
						if (level < quietest)
						{
							quietest = level;
							victim = candidate;
						}
					}

					break;
				}
				case StealingMode.Furthest:
				{
					float farthest = -1f;
					foreach (EventPlayback candidate in candidates)
					{
						float distance = (candidate.position - mixer.ListenerPosition).LengthSquared;
						if (distance > farthest)
						{
							farthest = distance;
							victim = candidate;
						}
					}

					break;
				}
				default:
					victim = candidates.Count > 0 ? candidates[0] : null;
					break;
			}

			return victim;
		}

		#endregion

		#region NestedTypes

		private sealed class InstrumentRuntime
		{
			public readonly CompiledInstrument Instrument;
			public bool Triggered;
			public long PendingTriggerAt = NoSample;
			public float PendingAttack;
			public bool PendingFromJump;
			public long NextSpawnAt = NoSample;
			public int Spawned;
			public int PlaylistCursor;
			public int LastPick = -1;
			public readonly List<int> ShuffleBag = new List<int>();
			public readonly List<Voice> Voices = new List<Voice>();

			/// <summary>Voices released by polyphony that finish their fade apart from the live ones.</summary>
			public readonly List<Voice> Fading = new List<Voice>();

			/// <summary>Voices playing on as a transition's source (fading out towards the source region's end).</summary>
			public readonly List<Voice> SourceVoices = new List<Voice>();

			/// <summary>The instrument continues as a transition's source.</summary>
			public bool SourceExtended;

			/// <summary>The instrument's voices get the destination fade of the transition in progress.</summary>
			public bool DestinationFading;

			public EventPlayback Nested;
			public SnapshotState Snapshot;

			/// <summary>Streaming readers created ahead for the streamed sounds of the playlist (one per entry).</summary>
			public readonly List<StreamingReader> Readers = new List<StreamingReader>();

			public InstrumentRuntime(CompiledInstrument instrument)
			{
				Instrument = instrument;
				if (instrument.Kind == InstrumentKind.Single || instrument.Kind == InstrumentKind.Multi)
				{
					foreach (CompiledPlaylistEntry entry in instrument.Playlist)
					{
						if (entry.Sample != null && entry.Sample.Streaming)
						{
							// Prefetched from the sound's start, so a trigger at the start plays at once.
							var reader = new StreamingReader(entry.Sample, 0, instrument.Loop);
							Readers.Add(reader);
							AudioStreamer.Register(reader);
						}
					}
				}
			}

			/// <summary>A reader prepared for the sample that no voice uses, or null.</summary>
			public StreamingReader TakeReader(SampleData sample)
			{
				for (int i = 0; i < Readers.Count; i++)
				{
					if (Readers[i].Sample == sample)
					{
						StreamingReader reader = Readers[i];
						Readers.RemoveAt(i);
						return reader;
					}
				}

				return null;
			}

			/// <summary>Drops the spare readers (the runtime is replaced or destroyed).</summary>
			public void DisposeReaders()
			{
				foreach (StreamingReader reader in Readers)
				{
					reader?.Dispose();
				}

				Readers.Clear();
			}

			/// <summary>A finished voice gives its reader back (or disposes an extra one).</summary>
			public void ReturnReader(Voice voice)
			{
				if (voice.Stream == null)
				{
					return;
				}

				AudioStreamer.Unregister(voice.Stream);
				if (Readers.Count < 4)
				{
					Readers.Add(voice.Stream);
					voice.Stream = null;
				}
				else
				{
					voice.Stream.Dispose();
					voice.Stream = null;
				}
			}
		}

		private sealed class TrackRuntime
		{
			public readonly CompiledTrack Track;
			public readonly float[] Buffer = new float[Mixer.BlockFrames * 2];
			public readonly EffectRunner[] Effects;
			public float VolumeDb;
			public float Pan;
			public float PitchRatio = 1f;
			public float CurrentGain;

			public TrackRuntime(CompiledTrack track)
			{
				Track = track;
				Effects = new EffectRunner[track.Effects.Length];
				for (int i = 0; i < Effects.Length; i++)
				{
					Effects[i] = new EffectRunner(track.Effects[i].Clone());
				}

				VolumeDb = track.VolumeDb;
				Pan = track.Pan;
				PitchRatio = AudioMath.SemitonesToRatio(track.PitchSemitones);
				CurrentGain = track.Silent ? 0f : AudioMath.DbToGain(track.VolumeDb);
			}

			public void ApplyAutomation(float[] parameters)
			{
				VolumeDb = Track.VolumeDb;
				Pan = Track.Pan;
				float pitch = Track.PitchSemitones;
				foreach (CompiledAutomation automation in Track.Automation)
				{
					if (automation.Parameter < 0 || automation.Parameter >= parameters.Length)
					{
						continue;
					}

					float value = automation.Curve.Evaluate(parameters[automation.Parameter]);
					switch (automation.Target)
					{
						case AudioAutomationCurve.VolumeTarget:
							VolumeDb = value;
							break;
						case AudioAutomationCurve.PitchTarget:
							pitch = value;
							break;
						case AudioAutomationCurve.PanTarget:
							Pan = value;
							break;
						default:
							if (automation.EffectIndex >= 0 && automation.EffectIndex < Effects.Length)
							{
								Effects[automation.EffectIndex].Effect.SetParameter(automation.EffectParameter, value);
							}

							break;
					}
				}

				PitchRatio = AudioMath.SemitonesToRatio(pitch);
			}

			public void RunEffects(int frames)
			{
				foreach (EffectRunner effect in Effects)
				{
					effect.Process(Buffer, frames);
				}
			}
		}

		/// <summary>Playlist positions shared by every instance of an instrument (<see cref="PlaylistMode.SequentialGlobal" />).</summary>
		private static class SharedCursors
		{
			private static readonly Dictionary<string, int> cursors = new Dictionary<string, int>(StringComparer.Ordinal);

			public static int Next(AudioInstrument instrument, int count)
			{
				string key = instrument.Id ?? "";
				int current;
				cursors.TryGetValue(key, out current);
				cursors[key] = (current + 1) % Math.Max(1, count);
				return current % Math.Max(1, count);
			}
		}

		#endregion
	}
}
