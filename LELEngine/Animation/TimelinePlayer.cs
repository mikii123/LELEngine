using System;
using System.Collections.Generic;
using LELEngine.Audio;

namespace LELEngine.Animation
{
	/// <summary>Where a timeline's playback is.</summary>
	public enum TimelinePlaybackState
	{
		Stopped,
		Playing,

		/// <summary>The cursor waits at a sustain point until <see cref="TimelinePlayer.KeyOff" />; the clips keep playing.</summary>
		Sustaining,

		/// <summary>Fading out after a Stop with a fade.</summary>
		Stopping
	}

	public enum TimelineNotificationKind
	{
		Started,
		Stopped,

		/// <summary>The cursor reached or jumped to a destination marker (the text is its name).</summary>
		MarkerReached,

		/// <summary>The cursor stopped at a sustain point (the text is its name).</summary>
		Sustained
	}

	/// <summary>Something a timeline's playback tells the game (delivered after the frame's properties were written).</summary>
	public struct TimelineNotification
	{
		public TimelineNotificationKind Kind;
		public string Text;

		/// <summary>Seconds on the timeline.</summary>
		public double Position;
	}

	/// <summary>
	///     The playback of a <see cref="TimelineAsset" /> on objects: a cursor over the asset's clips and logic, moved in
	///     whole ticks so every boundary (a clip's ends, a loop's end, a transition) is handled exactly where it lies, as
	///     the audio system's event playback does. Every property a clip has keys for is mixed per channel and written:
	///     the playing clips by their weights over what the property holds without them (its own value, or the last value
	///     of the clip that ended before, which holds). A property no clip has touched yet is never written; one a script
	///     writes meanwhile goes back to the script as soon as no clip plays it. Used by the <see cref="Timeline" />
	///     component in the game and by the editor's window for its preview.
	/// </summary>
	public sealed class TimelinePlayer
	{
		#region PublicFields

		public const long TicksPerSecond = 48000;

		/// <summary>Solo counts (the editor, auditioning); player builds play every channel.</summary>
		public static bool AuditionSolo;

		public TimelineAsset Asset { get; }

		public TimelinePlaybackState State => state;

		public bool IsPlaying => state != TimelinePlaybackState.Stopped;

		/// <summary>The cursor and the clips stand still; the pose is still written.</summary>
		public bool Paused;

		/// <summary>The editor's preview: no animation events and no notifications reach the game's scripts.</summary>
		public bool Preview;

		/// <summary>Keeps running past the end of the content (a recording makes the timeline longer as it goes).</summary>
		public bool Persistent;

		/// <summary>Seconds on the timeline.</summary>
		public double Time => cursor / (double)TicksPerSecond;

		/// <summary>Raised after the frame's properties were written.</summary>
		public event Action<TimelineNotification> Notified;

		#endregion

		#region PrivateFields

		private const long NoTick = -1;
		private const long ToEnd = -1;
		private const long NoDestination = -2;

		private readonly Func<TimelineChannel, GameObject> resolveTarget;
		private readonly Random random = new Random();
		private readonly List<TimelineNotification> pending = new List<TimelineNotification>();
		private readonly List<(GameObject Target, AnimationEvent Event)> pendingEvents = new List<(GameObject, AnimationEvent)>();
		private readonly List<AnimationEvent> eventScratch = new List<AnimationEvent>();
		private readonly HashSet<string> capturing = new HashSet<string>(StringComparer.Ordinal);
		private TimelinePlaybackState state = TimelinePlaybackState.Stopped;
		private ChannelRuntime[] channels = Array.Empty<ChannelRuntime>();
		private ClipRuntime[] clips = Array.Empty<ClipRuntime>();
		private ClipRuntime[] clipsByEnd = Array.Empty<ClipRuntime>();
		private TransitionRuntime[] transitions = Array.Empty<TransitionRuntime>();
		private LoopRuntime[] loops = Array.Empty<LoopRuntime>();
		private PointRuntime[] points = Array.Empty<PointRuntime>();
		private int[] loopCounts = Array.Empty<int>();
		private BeatGrid grid;
		private AudioParameter[] parameters = Array.Empty<AudioParameter>();
		private float[] parameterTarget = Array.Empty<float>();
		private float[] parameterCurrent = Array.Empty<float>();
		private long signature = long.MinValue;
		private bool built;
		private long cursor;
		private long lengthTicks;
		private double tickCarry;
		private bool reachedEnd;
		private long sustainedAt = NoTick;
		private TransitionRuntime pendingTransition;
		private long pendingTransitionAt = NoTick;
		private int jumpDepth;
		private bool logicSuppressed;
		private double stopFade;
		private double stopElapsed;
		private bool soloActive;

		#endregion

		#region Constructors

		/// <param name="asset">The timeline played.</param>
		/// <param name="resolveTarget">The object of a channel (null: the channel writes nothing).</param>
		public TimelinePlayer(TimelineAsset asset, Func<TimelineChannel, GameObject> resolveTarget)
		{
			Asset = asset;
			this.resolveTarget = resolveTarget;
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- transport

		/// <summary>Starts (or restarts) at a time of the timeline.</summary>
		public void Play(double from = 0.0)
		{
			EnsureCurrent();
			foreach (ClipRuntime runtime in clips)
			{
				runtime.Voices.Clear();
				runtime.Triggered = false;
			}

			state = TimelinePlaybackState.Playing;
			Paused = false;
			reachedEnd = false;
			tickCarry = 0.0;
			pendingTransition = null;
			pendingTransitionAt = NoTick;
			sustainedAt = NoTick;
			Array.Clear(loopCounts, 0, loopCounts.Length);
			JumpTo(ToTicks(Math.Max(0.0, from)), 0.0, true);
			Notify(TimelineNotificationKind.Started, null, Time);
		}

		/// <summary>Stops: at once, or fading every clip out over <paramref name="fade" /> seconds towards what the properties hold without them.</summary>
		public void Stop(double fade = 0.0)
		{
			if (state == TimelinePlaybackState.Stopped)
			{
				return;
			}

			if (fade <= 0.0)
			{
				Finish();
				Flush();
				return;
			}

			if (state != TimelinePlaybackState.Stopping)
			{
				state = TimelinePlaybackState.Stopping;
				stopFade = fade;
				stopElapsed = 0.0;
			}
		}

		/// <summary>Moves the cursor (an event at the new place plays from there). <paramref name="logic" /> off (scrubbing): no transition or sustain point acts at the spot.</summary>
		public void Seek(double seconds, bool logic = true)
		{
			if (state == TimelinePlaybackState.Stopped)
			{
				return;
			}

			EnsureCurrent();
			if (state == TimelinePlaybackState.Sustaining)
			{
				state = TimelinePlaybackState.Playing;
			}

			logicSuppressed = !logic;
			try
			{
				JumpTo(ToTicks(Math.Max(0.0, seconds)), 0.0, false);
			}
			finally
			{
				logicSuppressed = false;
			}
		}

		/// <summary>Moves on from a sustain point.</summary>
		public void KeyOff()
		{
			if (state == TimelinePlaybackState.Sustaining)
			{
				state = TimelinePlaybackState.Playing;
			}
		}

		// ---------------------------------------------------------------- parameters

		/// <summary>Sets a parameter by name or id (clamped to its range; it moves there at its seek speed unless <paramref name="ignoreSeek" />).</summary>
		public void SetParameter(string nameOrId, float value, bool ignoreSeek = false)
		{
			EnsureCurrent();
			int index = FindParameter(nameOrId);
			if (index < 0)
			{
				return;
			}

			AudioParameter parameter = parameters[index];
			parameterTarget[index] = parameter.Clamp(value);
			if (ignoreSeek || parameter.SeekSpeed <= 0f)
			{
				parameterCurrent[index] = parameterTarget[index];
			}
		}

		/// <summary>A parameter's current value by name or id (NaN when there is none).</summary>
		public float GetParameter(string nameOrId)
		{
			EnsureCurrent();
			int index = FindParameter(nameOrId);
			return index >= 0 ? parameterCurrent[index] : float.NaN;
		}

		// ---------------------------------------------------------------- frames

		/// <summary>One frame: the cursor moves <paramref name="deltaTime" /> seconds, then the pose is written and the notifications go out.</summary>
		public void Advance(double deltaTime)
		{
			if (state == TimelinePlaybackState.Stopped)
			{
				return;
			}

			EnsureCurrent();
			RefreshBases();
			UpdateParameters(deltaTime);
			if (!Paused)
			{
				Step(deltaTime);
			}

			WritePose();
			Flush();
		}

		/// <summary>Writes the pose at the current position without moving (after a seek, while paused).</summary>
		public void ApplyPose()
		{
			if (state == TimelinePlaybackState.Stopped)
			{
				return;
			}

			EnsureCurrent();
			RefreshBases();
			WritePose();
		}

		/// <summary>Puts back what every written property held without the timeline (leaving the editor's preview, a Restore at the end).</summary>
		public void Restore()
		{
			foreach (ChannelRuntime channel in channels)
			{
				channel.Restore();
			}
		}

		/// <summary>The channel writes nothing while on: what its object does is being recorded.</summary>
		public void SetCapture(string channelId, bool capture)
		{
			if (capture)
			{
				capturing.Add(channelId);
			}
			else
			{
				capturing.Remove(channelId);
			}

			foreach (ChannelRuntime channel in channels)
			{
				if (channel.Channel.id == channelId)
				{
					channel.Capturing = capture;
				}
			}
		}

		/// <summary>The summed weight of the clips a channel played in the last frame (0: it is silent).</summary>
		public float ChannelLevel(string channelId)
		{
			foreach (ChannelRuntime channel in channels)
			{
				if (channel.Channel.id == channelId)
				{
					return channel.Level;
				}
			}

			return 0f;
		}

		/// <summary>The clip's weight in the last frame (0: not playing).</summary>
		public float ClipLevel(string clipId)
		{
			foreach (ClipRuntime runtime in clips)
			{
				if (runtime.Clip.id == clipId)
				{
					return runtime.Level;
				}
			}

			return 0f;
		}

		/// <summary>Properties of a channel's clips that could not be bound on its object ("path|Type|property").</summary>
		public IReadOnlyList<string> UnresolvedProperties(string channelId)
		{
			foreach (ChannelRuntime channel in channels)
			{
				if (channel.Channel.id == channelId && channel.Layout != null)
				{
					return channel.Layout.Unresolved;
				}
			}

			return Array.Empty<string>();
		}

		/// <summary>Rebuilds when the asset, a clip or a binding changed; the base values of properties already written are kept.</summary>
		public void EnsureCurrent()
		{
			long now = ComputeSignature();
			if (built && now == signature)
			{
				return;
			}

			signature = now;
			Build();
		}

		#endregion

		#region PrivateMethods

		private static long ToTicks(double seconds)
		{
			return (long)Math.Round(seconds * TicksPerSecond);
		}

		private long ComputeSignature()
		{
			unchecked
			{
				long value = Asset.Version;
				foreach (TimelineChannel channel in Asset.channels)
				{
					GameObject target = resolveTarget?.Invoke(channel);
					value = value * 31 + (target != null ? (long)target.Id : 0L);
					foreach (TimelineClip clip in channel.clips)
					{
						value = value * 31 + (clip.clip != null ? clip.clip.Version : 0);
					}
				}

				return value;
			}
		}

		private int FindParameter(string nameOrId)
		{
			for (int i = 0; i < parameters.Length; i++)
			{
				if (parameters[i].Id == nameOrId || parameters[i].Name == nameOrId)
				{
					return i;
				}
			}

			return -1;
		}

		// ---------------------------------------------------------------- build

		private void Build()
		{
			bool playing = state != TimelinePlaybackState.Stopped;
			BuildParameters();
			BuildChannels();
			BuildLogic();
			built = true;
			if (playing)
			{
				// Live edit: every clip agrees with the cursor again.
				long at = cursor;
				bool suppressed = logicSuppressed;
				logicSuppressed = true;
				JumpTo(at, 0.0, true);
				logicSuppressed = suppressed;
			}
		}

		private void BuildParameters()
		{
			AudioParameter[] old = parameters;
			float[] oldTarget = parameterTarget;
			float[] oldCurrent = parameterCurrent;
			parameters = Asset.parameters.ToArray();
			parameterTarget = new float[parameters.Length];
			parameterCurrent = new float[parameters.Length];
			for (int i = 0; i < parameters.Length; i++)
			{
				float value = parameters[i].Clamp(parameters[i].Default);
				parameterTarget[i] = value;
				parameterCurrent[i] = value;
				for (int k = 0; k < old.Length; k++)
				{
					if (old[k].Id == parameters[i].Id)
					{
						parameterTarget[i] = parameters[i].Clamp(oldTarget[k]);
						parameterCurrent[i] = parameters[i].Clamp(oldCurrent[k]);
						break;
					}
				}
			}
		}

		private void BuildChannels()
		{
			ChannelRuntime[] old = channels;
			soloActive = false;
			if (AuditionSolo)
			{
				foreach (TimelineChannel channel in Asset.channels)
				{
					soloActive |= channel.solo;
				}
			}

			var built = new List<ChannelRuntime>();
			var allClips = new List<ClipRuntime>();
			foreach (TimelineChannel channel in Asset.channels)
			{
				var runtime = new ChannelRuntime
				{
					Channel = channel,
					Target = resolveTarget?.Invoke(channel),
					Silent = channel.mute || (soloActive && !channel.solo),
					Capturing = capturing.Contains(channel.id)
				};
				var layout = new PoseLayout();
				foreach (TimelineClip clip in channel.clips)
				{
					layout.AddClip(clip.clip);
				}

				layout.Bind(runtime.Target);
				runtime.Attach(layout);
				foreach (TimelineClip clip in channel.clips)
				{
					channel.GetFades(clip, out double fadeIn, out double fadeOut);
					bool soft = clip.fadeOut.Length <= 0.0;
					var clipRuntime = new ClipRuntime
					{
						Clip = clip,
						Channel = runtime,
						StartTick = ToTicks(clip.start),
						EndTick = ToTicks(clip.End),
						FadeIn = fadeIn,
						FadeOut = soft ? 0.0 : fadeOut,
						OverlapOut = soft ? fadeOut : 0.0,
						Slots = KeyedSlots(clip.clip, layout),
						Sampler = clip.clip != null ? new ClipSampler(clip.clip, layout) : null,
						Conditions = CompileConditions(clip.conditions)
					};
					allClips.Add(clipRuntime);
				}

				built.Add(runtime);
			}

			// What the old channels wrote: base values carry over, and a property no clip animates any more goes back.
			foreach (ChannelRuntime previous in old)
			{
				ChannelRuntime match = null;
				foreach (ChannelRuntime candidate in built)
				{
					if (candidate.Channel.id == previous.Channel.id)
					{
						match = candidate;
						break;
					}
				}

				previous.CarryOver(match);
			}

			channels = built.ToArray();
			allClips.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));
			clips = allClips.ToArray();
			var byEnd = new List<ClipRuntime>(allClips);
			byEnd.Sort((a, b) => a.EndTick.CompareTo(b.EndTick));
			clipsByEnd = byEnd.ToArray();
		}

		private void BuildLogic()
		{
			grid = new BeatGrid(Asset.TempoMarkers);
			lengthTicks = ToTicks(Asset.Duration);
			var transitionList = new List<TransitionRuntime>();
			var loopList = new List<LoopRuntime>();
			var pointList = new List<PointRuntime>();
			foreach (AudioMarker marker in Asset.markers)
			{
				switch (marker)
				{
					case TransitionMarker transition:
					{
						long destination = NoDestination;
						if (transition.DestinationId == TransitionMarker.EndDestination)
						{
							destination = ToEnd;
						}
						else if (Asset.FindMarker(transition.DestinationId) is AudioMarker target)
						{
							destination = ToTicks(target.Position);
						}

						var region = transition as TransitionRegion;
						transitionList.Add(new TransitionRuntime
						{
							Marker = transition,
							IsRegion = region != null,
							Position = ToTicks(transition.Position),
							End = ToTicks(transition.End),
							Destination = destination,
							Quantization = region?.Quantization ?? QuantizationInterval.None,
							Conditions = CompileConditions(transition.Conditions)
						});
						break;
					}
					case LoopRegion loop:
						loopList.Add(new LoopRuntime { Start = ToTicks(loop.Position), End = ToTicks(loop.End), MaxLoops = loop.MaxLoops, Conditions = CompileConditions(loop.Conditions) });
						break;
					case DestinationMarker destination:
						pointList.Add(new PointRuntime { Position = ToTicks(destination.Position), Sustain = false, Name = destination.Name ?? "" });
						break;
					case SustainPoint sustain:
						pointList.Add(new PointRuntime { Position = ToTicks(sustain.Position), Sustain = true, Name = sustain.Name ?? "" });
						break;
				}
			}

			transitionList.Sort((a, b) => a.Position.CompareTo(b.Position));
			loopList.Sort((a, b) => a.End.CompareTo(b.End));
			pointList.Sort((a, b) => a.Position.CompareTo(b.Position));
			transitions = transitionList.ToArray();
			loops = loopList.ToArray();
			points = pointList.ToArray();
			loopCounts = new int[loops.Length];
			pendingTransition = null;
			pendingTransitionAt = NoTick;
		}

		/// <summary>The slots of the properties a clip has keys for.</summary>
		private static int[] KeyedSlots(AnimationClip clip, PoseLayout layout)
		{
			if (clip == null)
			{
				return Array.Empty<int>();
			}

			var slots = new List<int>();
			foreach (AnimationBinding binding in clip.bindings)
			{
				int slot = layout.SlotOf(binding);
				if (slot >= 0 && !binding.IsEmpty && !slots.Contains(slot))
				{
					slots.Add(slot);
				}
			}

			return slots.ToArray();
		}

		private CompiledCondition[] CompileConditions(List<AudioCondition> conditions)
		{
			if (conditions == null || conditions.Count == 0)
			{
				return Array.Empty<CompiledCondition>();
			}

			var result = new CompiledCondition[conditions.Count];
			for (int i = 0; i < result.Length; i++)
			{
				result[i] = new CompiledCondition { Parameter = Array.FindIndex(parameters, p => p.Id == conditions[i].ParameterId), Test = conditions[i] };
			}

			return result;
		}

		// ---------------------------------------------------------------- frame

		private void UpdateParameters(double deltaTime)
		{
			for (int i = 0; i < parameters.Length; i++)
			{
				float speed = parameters[i].SeekSpeed;
				if (speed <= 0f || parameterCurrent[i] == parameterTarget[i])
				{
					parameterCurrent[i] = parameterTarget[i];
					continue;
				}

				float step = (float)(speed * deltaTime);
				parameterCurrent[i] = parameterCurrent[i] < parameterTarget[i] ? Math.Min(parameterTarget[i], parameterCurrent[i] + step) : Math.Max(parameterTarget[i], parameterCurrent[i] - step);
			}
		}

		private void RefreshBases()
		{
			foreach (ChannelRuntime channel in channels)
			{
				channel.RefreshBase();
				channel.UpdateUnderlying();
			}
		}

		/// <summary>Moves the cursor, stopping at every boundary in between so each is handled at its exact tick.</summary>
		private void Step(double deltaTime)
		{
			tickCarry += Math.Max(0.0, deltaTime) * TicksPerSecond;
			long total = (long)tickCarry;
			tickCarry -= total;
			long done = 0;
			int guard = 0;
			while (done < total && guard++ < 4096 && state != TimelinePlaybackState.Stopped)
			{
				long remaining = total - done;
				bool advancing = state != TimelinePlaybackState.Sustaining;
				long next = advancing ? NextBoundary(cursor, cursor + remaining) : long.MaxValue;
				long count = next == long.MaxValue ? remaining : Math.Min(remaining, next - cursor);
				if (count > 0)
				{
					AdvanceVoices(count / (double)TicksPerSecond);
					if (advancing)
					{
						AdvanceCursor(cursor + count);
					}

					done += count;
				}

				if (advancing && next != long.MaxValue && cursor == next)
				{
					HandleBoundary();
				}
			}

			if (state == TimelinePlaybackState.Stopping)
			{
				stopElapsed += total / (double)TicksPerSecond;
			}

			CleanupVoices();
			if (state == TimelinePlaybackState.Stopping && stopElapsed >= stopFade)
			{
				Finish();
			}
			else if (state == TimelinePlaybackState.Playing && !Persistent && (cursor >= lengthTicks || reachedEnd) && !HasVoices())
			{
				Finish();
			}
		}

		private void AdvanceVoices(double seconds)
		{
			foreach (ClipRuntime runtime in clips)
			{
				foreach (Voice voice in runtime.Voices)
				{
					voice.Advance(seconds, runtime);
					if (!Preview && runtime.Sampler != null && runtime.Clip.clip.events.Count > 0 && runtime.Channel.Target != null && !runtime.Channel.Silent)
					{
						eventScratch.Clear();
						runtime.Sampler.CollectEvents((float)voice.PreviousPosition, (float)voice.Position, runtime.Clip.loop, voice.FirstStep, eventScratch);
						foreach (AnimationEvent e in eventScratch)
						{
							pendingEvents.Add((runtime.Channel.Target, e));
						}
					}

					voice.FirstStep = false;
				}
			}
		}

		private void CleanupVoices()
		{
			foreach (ClipRuntime runtime in clips)
			{
				runtime.Voices.RemoveAll(v => v.Finished);
			}
		}

		private bool HasVoices()
		{
			foreach (ClipRuntime runtime in clips)
			{
				if (runtime.Voices.Count > 0)
				{
					return true;
				}
			}

			return false;
		}

		private void Finish()
		{
			foreach (ClipRuntime runtime in clips)
			{
				runtime.Voices.Clear();
				runtime.Triggered = false;
			}

			pendingTransition = null;
			pendingTransitionAt = NoTick;
			state = TimelinePlaybackState.Stopped;
			Paused = false;
			Notify(TimelineNotificationKind.Stopped, null, Time);
		}

		private void Notify(TimelineNotificationKind kind, string text, double position)
		{
			if (!Preview)
			{
				pending.Add(new TimelineNotification { Kind = kind, Text = text, Position = position });
			}
		}

		/// <summary>The frame's animation events and notifications (after the writes: a handler may stop or seek the timeline).</summary>
		private void Flush()
		{
			if (pendingEvents.Count > 0)
			{
				var events = new List<(GameObject Target, AnimationEvent Event)>(pendingEvents);
				pendingEvents.Clear();
				foreach ((GameObject target, AnimationEvent e) in events)
				{
					AnimationEventDispatcher.Dispatch(target, e);
				}
			}

			if (pending.Count > 0)
			{
				var notifications = new List<TimelineNotification>(pending);
				pending.Clear();
				foreach (TimelineNotification notification in notifications)
				{
					Notified?.Invoke(notification);
				}
			}
		}

		// ---------------------------------------------------------------- pose

		private void WritePose()
		{
			foreach (ChannelRuntime channel in channels)
			{
				channel.UpdateUnderlying();
				channel.BeginMix();
			}

			float master = state == TimelinePlaybackState.Stopping ? (float)Math.Clamp(1.0 - stopElapsed / Math.Max(stopFade, 1e-6), 0.0, 1.0) : 1f;
			foreach (ClipRuntime runtime in clips)
			{
				ChannelRuntime channel = runtime.Channel;
				runtime.Level = 0f;
				if (channel.Silent || channel.Capturing || runtime.Sampler == null)
				{
					continue;
				}

				foreach (Voice voice in runtime.Voices)
				{
					if (voice.Finished)
					{
						continue;
					}

					float weight = voice.Weight(runtime) * master;
					if (weight > 0f)
					{
						float share = voice.OverlapShare(runtime);
						runtime.Sampler.Sample((float)runtime.Clip.WrapClipTime(voice.Position), weight * share, channel.Mix, channel.Underlying);
						channel.AddCover(runtime.Slots, weight);
						runtime.Level += weight * share;
						channel.Level += weight * share;
					}
				}
			}

			foreach (ChannelRuntime channel in channels)
			{
				if (!channel.Capturing)
				{
					channel.Write();
				}
			}
		}

		// ---------------------------------------------------------------- cursor

		/// <summary>The first tick in (<paramref name="from" />, <paramref name="to" />] where something happens, or long.MaxValue.</summary>
		private long NextBoundary(long from, long to)
		{
			long best = long.MaxValue;
			foreach (ClipRuntime runtime in clips)
			{
				if (runtime.StartTick > to)
				{
					break;
				}

				Consider(ref best, runtime.Triggered ? runtime.EndTick : runtime.StartTick, from, to);
			}

			foreach (TransitionRuntime transition in transitions)
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

			foreach (LoopRuntime loop in loops)
			{
				if (loop.End > to)
				{
					break;
				}

				Consider(ref best, loop.End, from, to);
			}

			foreach (PointRuntime point in points)
			{
				if (point.Position > to)
				{
					break;
				}

				if (point.Sustain && point.Position != sustainedAt)
				{
					Consider(ref best, point.Position, from, to);
				}
			}

			return best;
		}

		private static void Consider(ref long best, long tick, long from, long to)
		{
			if (tick > from && tick <= to && tick < best)
			{
				best = tick;
			}
		}

		private void AdvanceCursor(long to)
		{
			long from = cursor;
			cursor = to;
			foreach (PointRuntime point in points)
			{
				if (!point.Sustain && point.Position > from && point.Position <= to)
				{
					Notify(TimelineNotificationKind.MarkerReached, point.Name, point.Position / (double)TicksPerSecond);
				}
			}

			UpdatePendingTransition();
		}

		/// <summary>Transition regions: a condition that holds inside the region schedules the jump at the next grid point.</summary>
		private void UpdatePendingTransition()
		{
			if (logicSuppressed)
			{
				return;
			}

			if (pendingTransition != null)
			{
				if (cursor >= pendingTransition.Position && cursor < pendingTransition.End && ConditionsHold(pendingTransition.Conditions))
				{
					return;
				}

				pendingTransition = null;
				pendingTransitionAt = NoTick;
			}

			foreach (TransitionRuntime transition in transitions)
			{
				if (!transition.IsRegion || transition.Destination == NoDestination || cursor < transition.Position || cursor >= transition.End || !ConditionsHold(transition.Conditions))
				{
					continue;
				}

				long at = transition.Quantization == QuantizationInterval.None
					? cursor
					: ToTicks(grid.NextGridPoint(transition.Quantization, cursor / (double)TicksPerSecond, 1.0 / TicksPerSecond));
				at = Math.Max(at, cursor);
				if (at >= transition.End)
				{
					// No grid point before the region ends: no transition from this region.
					continue;
				}

				if (transition.Marker.Probability < 100f && random.NextDouble() * 100.0 >= transition.Marker.Probability)
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
				TransitionRuntime transition = pendingTransition;
				pendingTransition = null;
				pendingTransitionAt = NoTick;
				JumpTo(transition.Destination, transition.Marker.Crossfade, false);
			}
		}

		/// <summary>Everything that happens at the cursor's tick (loops and transitions first: a jump ends the handling).</summary>
		private void HandleBoundary()
		{
			long at = cursor;
			for (int i = 0; i < loops.Length; i++)
			{
				LoopRuntime loop = loops[i];
				if (loop.End != at || loop.Start >= loop.End)
				{
					continue;
				}

				if (!ConditionsHold(loop.Conditions) || (loop.MaxLoops > 0 && loopCounts[i] >= loop.MaxLoops))
				{
					continue;
				}

				loopCounts[i]++;
				JumpTo(loop.Start, 0.0, false);
				return;
			}

			foreach (TransitionRuntime transition in transitions)
			{
				if (transition.IsRegion || transition.Position != at || transition.Destination == NoDestination)
				{
					continue;
				}

				if (!ConditionsHold(transition.Conditions) || (transition.Marker.Probability < 100f && random.NextDouble() * 100.0 >= transition.Marker.Probability))
				{
					continue;
				}

				JumpTo(transition.Destination, transition.Marker.Crossfade, false);
				return;
			}

			if (pendingTransition != null && pendingTransitionAt == at)
			{
				TransitionRuntime transition = pendingTransition;
				pendingTransition = null;
				pendingTransitionAt = NoTick;
				if (ConditionsHold(transition.Conditions))
				{
					JumpTo(transition.Destination, transition.Marker.Crossfade, false);
					return;
				}
			}

			// Clips that end first: what they leave behind is the underlying value of what starts here.
			foreach (ClipRuntime runtime in clips)
			{
				if (runtime.StartTick > at)
				{
					break;
				}

				if (runtime.Triggered && runtime.EndTick == at)
				{
					Untrigger(runtime);
				}
			}

			foreach (ClipRuntime runtime in clips)
			{
				if (runtime.StartTick > at)
				{
					break;
				}

				if (!runtime.Triggered && runtime.StartTick == at)
				{
					Trigger(runtime, 0, 0.0);
				}
			}

			foreach (PointRuntime point in points)
			{
				if (point.Sustain && point.Position == at && sustainedAt != at && state == TimelinePlaybackState.Playing)
				{
					sustainedAt = at;
					state = TimelinePlaybackState.Sustaining;
					Notify(TimelineNotificationKind.Sustained, point.Name, at / (double)TicksPerSecond);
				}
			}
		}

		/// <summary>
		///     Moves the cursor to <paramref name="destination" /> (<see cref="ToEnd" />: past the end): clips left stop, clips
		///     entered start at the right offset, clips spanning both restart at the new offset. With a crossfade what
		///     played fades out while what plays at the destination fades in.
		/// </summary>
		private void JumpTo(long destination, double crossfade, bool initial)
		{
			if (destination < 0)
			{
				destination = Math.Max(lengthTicks, cursor);
				reachedEnd = true;
			}

			// A transition region whose destination lies inside itself would jump forever within one tick.
			if (jumpDepth >= 8)
			{
				cursor = destination;
				pendingTransition = null;
				pendingTransitionAt = NoTick;
				return;
			}

			jumpDepth++;
			try
			{
				foreach (ClipRuntime runtime in clips)
				{
					ReleaseVoices(runtime, crossfade);
					runtime.Triggered = false;
					if (runtime.StartTick <= destination && destination < runtime.EndTick)
					{
						Trigger(runtime, destination - runtime.StartTick, crossfade);
					}
				}

				cursor = destination;
				tickCarry = 0.0;
				AfterCursorJump(destination, initial);
			}
			finally
			{
				jumpDepth--;
			}
		}

		/// <summary>Bookkeeping after the cursor landed somewhere new: what holds there, loop counts, markers at the spot, transitions.</summary>
		private void AfterCursorJump(long destination, bool initial)
		{
			pendingTransition = null;
			pendingTransitionAt = NoTick;
			for (int i = 0; i < loops.Length; i++)
			{
				if (destination < loops[i].Start || destination >= loops[i].End)
				{
					loopCounts[i] = 0;
				}
			}

			if (sustainedAt != destination)
			{
				sustainedAt = NoTick;
			}

			ResolveHeld(destination);
			if (logicSuppressed)
			{
				return;
			}

			foreach (PointRuntime point in points)
			{
				if (point.Position != destination)
				{
					continue;
				}

				if (!point.Sustain)
				{
					Notify(TimelineNotificationKind.MarkerReached, point.Name, destination / (double)TicksPerSecond);
				}
				else if (state == TimelinePlaybackState.Playing && !initial)
				{
					sustainedAt = destination;
					state = TimelinePlaybackState.Sustaining;
					Notify(TimelineNotificationKind.Sustained, point.Name, destination / (double)TicksPerSecond);
				}
			}

			UpdatePendingTransition();
		}

		/// <summary>
		///     What holds at a position of the timeline: the last value of each property's latest clip that ended before it
		///     without fading out. The pose at a position does not depend on how the cursor got there.
		/// </summary>
		private void ResolveHeld(long at)
		{
			foreach (ChannelRuntime channel in channels)
			{
				channel.ClearHeld();
				channel.UpdateUnderlying();
			}

			foreach (ClipRuntime runtime in clipsByEnd)
			{
				if (runtime.EndTick > at)
				{
					break;
				}

				if (runtime.Sampler == null || runtime.FadeOut > 0.0 || runtime.Channel.Silent || !ConditionsHold(runtime.Conditions))
				{
					continue;
				}

				runtime.Channel.CaptureHeld(runtime, runtime.Clip.ClipTimeAt(runtime.Clip.End));
			}
		}

		private bool ConditionsHold(CompiledCondition[] conditions)
		{
			foreach (CompiledCondition condition in conditions)
			{
				if (condition.Parameter < 0 || !condition.Test.Test(parameterCurrent[condition.Parameter]))
				{
					return false;
				}
			}

			return true;
		}

		// ---------------------------------------------------------------- clips

		/// <summary>The cursor entered a clip's region: it plays when its conditions allow.</summary>
		private void Trigger(ClipRuntime runtime, long offset, double attack)
		{
			runtime.Triggered = true;
			if (runtime.Sampler == null || !ConditionsHold(runtime.Conditions))
			{
				return;
			}

			double seconds = offset / (double)TicksPerSecond;
			runtime.Voices.Add(new Voice
			{
				Position = runtime.Clip.startOffset + seconds * runtime.Clip.speed,
				PreviousPosition = runtime.Clip.startOffset + seconds * runtime.Clip.speed,
				RegionTime = seconds,
				Attack = attack,
				FirstStep = offset == 0
			});
		}

		/// <summary>The cursor reached the region's end: the last value holds unless the clip faded out.</summary>
		private void Untrigger(ClipRuntime runtime)
		{
			runtime.Triggered = false;
			foreach (Voice voice in runtime.Voices)
			{
				if (voice.Release >= 0.0)
				{
					continue;
				}

				if (runtime.FadeOut <= 0.0 && !runtime.Channel.Silent)
				{
					runtime.Channel.CaptureHeld(runtime, runtime.Clip.WrapClipTime(voice.Position));
				}

				voice.Finished = true;
			}
		}

		/// <summary>A jump leaves what was playing: gone at once, or fading out over the crossfade.</summary>
		private static void ReleaseVoices(ClipRuntime runtime, double fade)
		{
			foreach (Voice voice in runtime.Voices)
			{
				if (fade <= 0.0)
				{
					voice.Finished = true;
				}
				else if (voice.Release < 0.0 || fade < voice.Release - voice.ReleaseTime)
				{
					voice.ReleaseFrom = voice.Weight(runtime);
					voice.Release = fade;
					voice.ReleaseTime = 0.0;
				}
			}

			runtime.Voices.RemoveAll(v => v.Finished);
		}

		#endregion

		#region NestedTypes

		private struct CompiledCondition
		{
			public int Parameter;
			public AudioCondition Test;
		}

		private sealed class TransitionRuntime
		{
			public TransitionMarker Marker;
			public bool IsRegion;
			public long Position;
			public long End;
			public long Destination;
			public QuantizationInterval Quantization;
			public CompiledCondition[] Conditions;
		}

		private sealed class LoopRuntime
		{
			public long Start;
			public long End;
			public int MaxLoops;
			public CompiledCondition[] Conditions;
		}

		private sealed class PointRuntime
		{
			public long Position;
			public bool Sustain;
			public string Name;
		}

		/// <summary>One pass of a clip through its region (a crossfade shows two of them at once).</summary>
		private sealed class Voice
		{
			/// <summary>Seconds into the clip, unwrapped.</summary>
			public double Position;

			public double PreviousPosition;

			/// <summary>Seconds since the region's start.</summary>
			public double RegionTime;

			/// <summary>Seconds of the crossfade in, and the time into it.</summary>
			public double Attack;

			public double AttackTime;

			/// <summary>Seconds of the crossfade out (negative: not fading out), and the time into it.</summary>
			public double Release = -1.0;

			public double ReleaseTime;
			public float ReleaseFrom = 1f;
			public bool FirstStep;
			public bool Finished;

			public void Advance(double seconds, ClipRuntime runtime)
			{
				PreviousPosition = Position;
				Position += seconds * runtime.Clip.speed;
				RegionTime += seconds;
				AttackTime += seconds;
				if (Release >= 0.0)
				{
					ReleaseTime += seconds;
					if (ReleaseTime >= Release)
					{
						Finished = true;
					}
				}
			}

			public float Weight(ClipRuntime runtime)
			{
				if (Release >= 0.0)
				{
					return ReleaseFrom * (float)Math.Clamp(1.0 - ReleaseTime / Math.Max(Release, 1e-6), 0.0, 1.0);
				}

				float weight = 1f;
				if (runtime.FadeIn > 0.0 && RegionTime < runtime.FadeIn)
				{
					weight *= runtime.Clip.fadeIn.Evaluate(RegionTime / runtime.FadeIn);
				}

				double remaining = runtime.Clip.length - RegionTime;
				if (runtime.FadeOut > 0.0 && remaining < runtime.FadeOut)
				{
					weight *= runtime.Clip.fadeOut.Evaluate(remaining / runtime.FadeOut);
				}

				if (Attack > 0.0 && AttackTime < Attack)
				{
					weight *= (float)(AttackTime / Attack);
				}

				return weight;
			}

			/// <summary>The clip's share against the clip overlapping its end: it counts only between the two, a property the other has no keys for stays this clip's.</summary>
			public float OverlapShare(ClipRuntime runtime)
			{
				if (runtime.OverlapOut <= 0.0 || Release >= 0.0)
				{
					return 1f;
				}

				double remaining = runtime.Clip.length - RegionTime;
				return remaining < runtime.OverlapOut ? Math.Max(runtime.Clip.fadeOut.Evaluate(remaining / runtime.OverlapOut), 1e-4f) : 1f;
			}
		}

		private sealed class ClipRuntime
		{
			public TimelineClip Clip;
			public ChannelRuntime Channel;
			public long StartTick;
			public long EndTick;

			/// <summary>Fade in (the overlap with the clip before included) and fade out, towards what the property holds without the clip.</summary>
			public double FadeIn;

			public double FadeOut;

			/// <summary>A clip without a fade out, overlapped at its end: how long it shares its properties with the next clip.</summary>
			public double OverlapOut;

			/// <summary>The slots of the properties the clip has keys for.</summary>
			public int[] Slots;

			public ClipSampler Sampler;
			public CompiledCondition[] Conditions;
			public bool Triggered;
			public float Level;
			public readonly List<Voice> Voices = new List<Voice>();
		}

		/// <summary>The properties one channel writes and how each stands: what it holds without the timeline, what was written last, what holds after a clip.</summary>
		private sealed class ChannelRuntime
		{
			public TimelineChannel Channel;
			public GameObject Target;
			public PoseLayout Layout;
			public Pose Mix;
			public bool Silent;
			public bool Capturing;
			public float Level;

			/// <summary>Per float channel of the layout: what each property shows when no clip plays it (held value, else the base).</summary>
			public float[] Underlying;

			/// <summary>Per slot: how much the clips cover the underlying value (their weights without the shares of an overlap).</summary>
			private float[] cover;

			private Pose scratch;
			private bool[] written;
			private bool[] hasHeld;
			private float[] held;
			private object[] heldDiscrete;
			private readonly float[] current = new float[4];
			private readonly float[] result = new float[4];

			public void Attach(PoseLayout layout)
			{
				Layout = layout;
				Mix = new Pose(layout);
				scratch = new Pose(layout);
				written = new bool[layout.Slots.Count];
				hasHeld = new bool[layout.Slots.Count];
				held = new float[layout.FloatCount];
				heldDiscrete = new object[layout.DiscreteCount];
				Underlying = new float[layout.FloatCount];
				cover = new float[layout.Slots.Count];
				UpdateUnderlying();
			}

			public void BeginMix()
			{
				Mix.Clear();
				Array.Clear(cover, 0, cover.Length);
				Level = 0f;
			}

			public void AddCover(int[] slots, float weight)
			{
				foreach (int slot in slots)
				{
					cover[slot] += weight;
				}
			}

			/// <summary>Something else changed a property since the last write: that is its value now, and the property is free again until a clip plays it.</summary>
			public void RefreshBase()
			{
				foreach (PoseSlot slot in Layout.Slots)
				{
					BoundProperty target = slot.Target;
					if (target == null || !target.IsAlive)
					{
						continue;
					}

					if (slot.IsDiscrete)
					{
						int d = slot.DiscreteIndex;
						object value = target.ReadDiscrete();
						if (!Equals(value, Layout.LastWrittenDiscrete[d]))
						{
							Layout.DefaultDiscrete[d] = value;
							Layout.LastWrittenDiscrete[d] = value;
							written[slot.Index] = false;
							hasHeld[slot.Index] = false;
						}

						continue;
					}

					target.ReadChannels(current, 0);
					bool changed = false;
					for (int c = 0; c < slot.Channels; c++)
					{
						changed |= current[c] != Layout.LastWritten[slot.Offset + c];
					}

					if (changed)
					{
						Array.Copy(current, 0, Layout.DefaultValues, slot.Offset, slot.Channels);
						Array.Copy(current, 0, Layout.LastWritten, slot.Offset, slot.Channels);
						written[slot.Index] = false;
						hasHeld[slot.Index] = false;
					}
				}
			}

			public void UpdateUnderlying()
			{
				foreach (PoseSlot slot in Layout.Slots)
				{
					if (!slot.IsDiscrete)
					{
						Array.Copy(hasHeld[slot.Index] ? held : Layout.DefaultValues, slot.Offset, Underlying, slot.Offset, slot.Channels);
					}
				}
			}

			public void ClearHeld()
			{
				Array.Clear(hasHeld, 0, hasHeld.Length);
			}

			/// <summary>The clip's value at <paramref name="clipTime" /> holds for every property it has keys for.</summary>
			public void CaptureHeld(ClipRuntime runtime, double clipTime)
			{
				if (runtime.Sampler == null)
				{
					return;
				}

				scratch.Clear();
				runtime.Sampler.Sample((float)clipTime, 1f, scratch, Underlying);
				foreach (PoseSlot slot in Layout.Slots)
				{
					if (slot.IsDiscrete)
					{
						int d = slot.DiscreteIndex;
						if (scratch.DiscreteWeights[d] > 0f)
						{
							heldDiscrete[d] = scratch.Discrete[d];
							hasHeld[slot.Index] = true;
						}
					}
					else if (scratch.Weights[slot.Index] > 0f)
					{
						Array.Copy(scratch.Values, slot.Offset, held, slot.Offset, slot.Channels);
						hasHeld[slot.Index] = true;
					}
				}

				UpdateUnderlying();
			}

			/// <summary>
			///     Writes the mix: the playing clips, averaged by their weights, over the underlying value as far as they cover it
			///     (a sum of at least 1 is theirs alone). A property nothing plays gets the underlying value once it was written; one
			///     never played stays untouched.
			/// </summary>
			public void Write()
			{
				foreach (PoseSlot slot in Layout.Slots)
				{
					BoundProperty target = slot.Target;
					if (target == null || !target.IsAlive)
					{
						continue;
					}

					int s = slot.Index;
					if (slot.IsDiscrete)
					{
						int d = slot.DiscreteIndex;
						float discreteWeight = Mix.DiscreteWeights[d];
						if (discreteWeight <= 0f && !written[s] && !hasHeld[s])
						{
							continue;
						}

						object value = discreteWeight > 0f && cover[s] >= 0.5f ? Mix.Discrete[d] : hasHeld[s] ? heldDiscrete[d] : Layout.DefaultDiscrete[d];
						if (!Equals(value, Layout.LastWrittenDiscrete[d]))
						{
							target.WriteDiscrete(value);
							Layout.LastWrittenDiscrete[d] = target.ReadDiscrete();
						}

						written[s] = true;
						continue;
					}

					float weight = Mix.Weights[s];
					if (weight <= 0f && !written[s] && !hasHeld[s])
					{
						continue;
					}

					Blend(slot, weight);
					bool same = true;
					for (int c = 0; c < slot.Channels; c++)
					{
						same &= result[c] == Layout.LastWritten[slot.Offset + c];
					}

					written[s] = true;
					if (!same)
					{
						target.WriteChannels(result, 0);
						target.ReadChannels(Layout.LastWritten, slot.Offset);
					}
				}
			}

			private void Blend(PoseSlot slot, float weight)
			{
				int offset = slot.Offset;
				int channels = slot.Channels;
				float covered = Math.Min(cover[slot.Index], 1f);
				if (weight <= 0f)
				{
					Array.Copy(Underlying, offset, result, 0, channels);
				}
				else if (slot.IsRotation)
				{
					// Normalized when written. Where the clips do not cover it the underlying rotation takes the rest, on the same hemisphere.
					float scale = covered / weight;
					float rest = 1f - covered;
					float dot = 0f;
					for (int c = 0; c < 4; c++)
					{
						dot += Mix.Values[offset + c] * Underlying[offset + c];
					}

					float sign = dot < 0f ? -rest : rest;
					for (int c = 0; c < 4; c++)
					{
						result[c] = Mix.Values[offset + c] * scale + sign * Underlying[offset + c];
					}
				}
				else if (slot.IsContinuous)
				{
					float scale = covered / weight;
					float rest = 1f - covered;
					for (int c = 0; c < channels; c++)
					{
						result[c] = Mix.Values[offset + c] * scale + rest * Underlying[offset + c];
					}
				}
				else
				{
					// Ints, bools, enums: the heaviest clip, or the underlying value while it weighs more.
					Array.Copy(covered >= 0.5f ? Mix.Values : Underlying, offset, result, 0, channels);
				}
			}

			/// <summary>Puts back the base value of every property written.</summary>
			public void Restore()
			{
				foreach (PoseSlot slot in Layout.Slots)
				{
					BoundProperty target = slot.Target;
					if (target != null && target.IsAlive && written[slot.Index])
					{
						RestoreSlot(slot);
					}

					written[slot.Index] = false;
					hasHeld[slot.Index] = false;
				}

				UpdateUnderlying();
			}

			private void RestoreSlot(PoseSlot slot)
			{
				if (slot.IsDiscrete)
				{
					int d = slot.DiscreteIndex;
					slot.Target.WriteDiscrete(Layout.DefaultDiscrete[d]);
					Layout.LastWrittenDiscrete[d] = Layout.DefaultDiscrete[d];
				}
				else
				{
					slot.Target.WriteChannels(Layout.DefaultValues, slot.Offset);
					slot.Target.ReadChannels(Layout.LastWritten, slot.Offset);
				}
			}

			/// <summary>
			///     A rebuild replaced this channel: its base values and written flags go to the new one (<paramref name="next" />)
			///     where the object and the property stay; what no longer is animated there goes back to its base value.
			/// </summary>
			public void CarryOver(ChannelRuntime next)
			{
				foreach (PoseSlot slot in Layout.Slots)
				{
					if (slot.Target == null || !slot.Target.IsAlive)
					{
						continue;
					}

					PoseSlot successor = next != null && next.Target == Target ? next.Layout.FindSlot(slot.Key) : null;
					if (successor == null || successor.Target == null || successor.Channels != slot.Channels || successor.IsDiscrete != slot.IsDiscrete)
					{
						if (written[slot.Index])
						{
							RestoreSlot(slot);
						}

						continue;
					}

					next.written[successor.Index] = written[slot.Index];
					if (slot.IsDiscrete)
					{
						next.Layout.DefaultDiscrete[successor.DiscreteIndex] = Layout.DefaultDiscrete[slot.DiscreteIndex];
						next.Layout.LastWrittenDiscrete[successor.DiscreteIndex] = Layout.LastWrittenDiscrete[slot.DiscreteIndex];
					}
					else
					{
						Array.Copy(Layout.DefaultValues, slot.Offset, next.Layout.DefaultValues, successor.Offset, slot.Channels);
						Array.Copy(Layout.LastWritten, slot.Offset, next.Layout.LastWritten, successor.Offset, slot.Channels);
					}
				}

				next?.UpdateUnderlying();
			}
		}

		#endregion
	}
}
