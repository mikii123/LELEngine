using System;
using System.Collections.Generic;
using LELEngine.Serialization;

namespace LELEngine.Audio
{
	/// <summary>Finds the sample data of a sound: a bank's table in the player, the project's clips in the editor.</summary>
	public interface ISampleResolver
	{
		/// <summary>The data of a clip, or null when the file is missing or unreadable.</summary>
		SampleData Resolve(AudioClipReference clip);
	}

	public enum InstrumentKind
	{
		Single,
		Multi,
		Scatterer,
		Event,
		Snapshot,

		/// <summary>No playable content (missing files): the region still triggers, nothing sounds.</summary>
		Silent
	}

	public enum MarkerPointKind
	{
		Destination,
		Sustain,
		Tempo
	}

	public sealed class CompiledParameter
	{
		public AudioParameter Source;
		public string Id;
		public string Name;
		public bool IsGlobal;
		public float Min;
		public float Max;
		public float Default;
		public float SeekSpeed;
		public BuiltInParameter BuiltIn;
		public AudioParameterType Type;

		public float Clamp(float value)
		{
			return Source != null ? Source.Clamp(value) : Math.Clamp(value, Math.Min(Min, Max), Math.Max(Min, Max));
		}
	}

	public sealed class CompiledCondition
	{
		public int Parameter;
		public float Min;
		public float Max;

		public bool Test(float value)
		{
			return value >= Math.Min(Min, Max) - 1e-6f && value <= Math.Max(Min, Max) + 1e-6f;
		}
	}

	public sealed class CompiledAutomation
	{
		public int Parameter;
		public string Target;
		public AudioAutomationCurve Curve;

		/// <summary>Index into the track's effects for an effect target, else -1.</summary>
		public int EffectIndex = -1;

		public string EffectParameter;
	}

	public sealed class CompiledTrack
	{
		public AudioTrack Source;
		public int Index;
		public string Id;
		public float VolumeDb;
		public float Pan;
		public float PitchSemitones;
		public bool Mute;
		public bool Solo;

		/// <summary>Silent: muted, or another track of the event is soloed.</summary>
		public bool Silent;

		public AudioEffect[] Effects = Array.Empty<AudioEffect>();
		public CompiledAutomation[] Automation = Array.Empty<CompiledAutomation>();
	}

	public sealed class CompiledPlaylistEntry
	{
		public SampleData Sample;
		public float Weight = 1f;
		public float VolumeDb;
		public float PitchSemitones;
		public string ClipPath;
	}

	public sealed class CompiledInstrument
	{
		public AudioInstrument Source;
		public int Index;
		public int Track;
		public string Id;
		public string Name;
		public InstrumentKind Kind;

		/// <summary>-1 on the timeline, else the index of the parameter whose sheet the region lies on.</summary>
		public int Sheet = -1;

		/// <summary>-1, or the index of the transition (<see cref="CompiledEvent.Transitions" />) whose timeline the region lies on.</summary>
		public int Transition = -1;

		public double Start;
		public double Length;
		public long StartSample;
		public long EndSample;
		public bool Async;
		public bool Loop;
		public float VolumeDb;
		public float PitchSemitones;
		public float VolumeRandomDb;
		public float PitchRandomSemitones;
		public float Probability = 100f;
		public CompiledCondition[] Conditions = Array.Empty<CompiledCondition>();
		public AudioEnvelope Envelope = new AudioEnvelope();
		public double StartOffset;
		public double StartOffsetRandom;
		public QuantizationInterval Quantization;
		public double TriggerDelayMin;
		public double TriggerDelayMax;
		public CompiledPlaylistEntry[] Playlist = Array.Empty<CompiledPlaylistEntry>();
		public PlaylistMode Mode;
		public double SpawnIntervalMin;
		public double SpawnIntervalMax;
		public int SpawnTotal;
		public float MinScatterDistance;
		public float MaxScatterDistance;
		public int Polyphony;
		public AudioEventReference NestedEvent;
		public string SnapshotId;

		public bool OnTimeline => Sheet < 0 && Transition < 0;
		public bool OnTransition => Transition >= 0;
		public double End => Start + Length;
	}

	/// <summary>A transition timeline in output samples.</summary>
	public sealed class CompiledTransitionTimeline
	{
		public TransitionTimeline Source;
		public long Length;
		public long SourceLength;
		public long DestinationStart;
		public long SourceFadeLength;
		public float SourceFadeShape;
		public long DestinationFadeLength;
		public float DestinationFadeShape;

		/// <summary>Gain of the source's sounds at a time of the transition timeline.</summary>
		public float SourceGain(long time)
		{
			if (time >= SourceLength)
			{
				return 0f;
			}

			if (SourceFadeLength <= 0 || time < SourceLength - SourceFadeLength)
			{
				return 1f;
			}

			return AudioAutomationCurve.Shape((float)((SourceLength - time) / (double)SourceFadeLength), SourceFadeShape);
		}

		/// <summary>Gain of the destination's sounds at a time of the transition timeline.</summary>
		public float DestinationGain(long time)
		{
			if (time < DestinationStart)
			{
				return 0f;
			}

			if (DestinationFadeLength <= 0 || time >= DestinationStart + DestinationFadeLength)
			{
				return 1f;
			}

			return AudioAutomationCurve.Shape((float)((time - DestinationStart) / (double)DestinationFadeLength), DestinationFadeShape);
		}
	}

	public sealed class CompiledTransition
	{
		public TransitionMarker Source;
		public int Index;
		public long Position;
		public long End;
		public bool IsRegion;
		public string DestinationId;

		/// <summary>Sample of the destination marker; -1 past the end (stop), -2 unknown (the transition is ignored).</summary>
		public long Destination;

		public CompiledCondition[] Conditions = Array.Empty<CompiledCondition>();
		public float Probability = 100f;
		public double Crossfade;
		public QuantizationInterval Quantization;

		/// <summary>The transition timeline, null for an instant jump.</summary>
		public CompiledTransitionTimeline Timeline;

		/// <summary>Indices of the instruments on the transition timeline, ordered by start.</summary>
		public int[] Instruments = Array.Empty<int>();
	}

	public sealed class CompiledLoop
	{
		public LoopRegion Source;
		public long Start;
		public long End;
		public CompiledCondition[] Conditions = Array.Empty<CompiledCondition>();
		public int MaxLoops;
	}

	public sealed class CompiledMarkerPoint
	{
		public AudioMarker Source;
		public MarkerPointKind Kind;
		public long Position;
		public string Name;
	}

	/// <summary>
	///     An event as the audio thread plays it: a private copy of the event data with every reference resolved
	///     (parameters to indices, clips to sample data, markers to sample positions) and the positions in output
	///     samples. Never changed after compilation; the editor compiles a new one for every edit.
	/// </summary>
	public sealed class CompiledEvent
	{
		#region PublicFields

		public AudioEvent Source;
		public string Id;
		public string Path;
		public string BankName;
		public string BusId;
		public int SampleRate;
		public CompiledParameter[] Parameters = Array.Empty<CompiledParameter>();
		public CompiledTrack[] Tracks = Array.Empty<CompiledTrack>();
		public CompiledTrack Master;
		public CompiledInstrument[] Instruments = Array.Empty<CompiledInstrument>();

		/// <summary>Indices of the timeline instruments ordered by start.</summary>
		public int[] TimelineOrder = Array.Empty<int>();

		public CompiledTransition[] Transitions = Array.Empty<CompiledTransition>();
		public CompiledLoop[] Loops = Array.Empty<CompiledLoop>();
		public CompiledMarkerPoint[] Points = Array.Empty<CompiledMarkerPoint>();
		public BeatGrid Grid;
		public bool HasTempo;
		public long LengthSamples;
		public AudioSpatializer Spatializer = new AudioSpatializer();
		public AudioEnvelope Envelope = new AudioEnvelope();
		public int MaxInstances;
		public StealingMode Stealing;
		public int Priority = 128;
		public bool Persistent;
		public float DopplerScale;
		public List<string> Warnings = new List<string>();

		/// <summary>Set by the compiler; grows with every compilation of the same event.</summary>
		public int CompileVersion;

		#endregion

		#region PublicMethods

		public int FindParameter(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return -1;
			}

			for (int i = 0; i < Parameters.Length; i++)
			{
				if (Parameters[i].Id == id)
				{
					return i;
				}
			}

			return -1;
		}

		public int FindParameterByName(string parameterName)
		{
			for (int i = 0; i < Parameters.Length; i++)
			{
				if (string.Equals(Parameters[i].Name, parameterName, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>The index of a transition by its marker id, or -1.</summary>
		public int FindTransition(string markerId)
		{
			for (int i = 0; i < Transitions.Length; i++)
			{
				if (Transitions[i].Source.Id == markerId)
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>Whether anything can keep an instance alive beyond its timeline (a looping or parameter sheet instrument, persistence).</summary>
		public bool HasParameterSheets
		{
			get
			{
				foreach (CompiledInstrument instrument in Instruments)
				{
					if (instrument.Sheet >= 0)
					{
						return true;
					}
				}

				return false;
			}
		}

		public override string ToString()
		{
			return Path;
		}

		#endregion
	}

	/// <summary>Turns an event of an audio project into a <see cref="CompiledEvent" />.</summary>
	public static class EventCompiler
	{
		#region PrivateFields

		private static int compileCounter;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Compiles an event with the project's global parameters, resolving its sounds through
		///     <paramref name="samples" /> (a missing sound leaves its playlist entry out and adds a warning).
		/// </summary>
		public static CompiledEvent Compile(AudioEvent source, AudioProject project, ISampleResolver samples, int sampleRate = AudioOutput.SampleRate)
		{
			if (source == null)
			{
				throw new ArgumentNullException(nameof(source));
			}

			AudioEvent copy = CloneEvent(source);
			var result = new CompiledEvent
			{
				Source = copy,
				Id = copy.Id,
				Path = copy.Path,
				BusId = copy.BusId ?? AudioProject.MasterBusId,
				BankName = project?.FindBank(copy.BankId)?.Name ?? AudioProject.MasterBankName,
				SampleRate = sampleRate,
				Spatializer = copy.Spatializer ?? new AudioSpatializer(),
				Envelope = copy.Envelope ?? new AudioEnvelope(),
				MaxInstances = copy.MaxInstances,
				Stealing = copy.Stealing,
				Priority = copy.Priority,
				Persistent = copy.Persistent,
				DopplerScale = Math.Max(0f, copy.Spatializer?.DopplerScale ?? 0f),
				CompileVersion = ++compileCounter
			};

			// Parameters: the event's own first, then every global one.
			var parameters = new List<CompiledParameter>();
			foreach (AudioParameter parameter in copy.Parameters)
			{
				parameters.Add(ToCompiled(parameter, false));
			}

			if (project != null)
			{
				foreach (AudioParameter parameter in project.GlobalParameters)
				{
					if (copy.FindLocalParameter(parameter.Id) == null)
					{
						parameters.Add(ToCompiled(parameter, true));
					}
				}
			}

			result.Parameters = parameters.ToArray();

			// Tracks.
			var tracks = new List<CompiledTrack>();
			for (int i = 0; i < copy.Tracks.Count; i++)
			{
				tracks.Add(CompileTrack(copy.Tracks[i], i, result));
			}

			result.Tracks = tracks.ToArray();
			result.Master = CompileTrack(copy.Master ?? new AudioTrack { Id = "master", Name = "Master" }, -1, result);
			bool anySolo = false;
			foreach (CompiledTrack track in result.Tracks)
			{
				anySolo |= track.Solo;
			}

			foreach (CompiledTrack track in result.Tracks)
			{
				track.Silent = track.Mute || (anySolo && !track.Solo);
			}

			result.Master.Silent = result.Master.Mute;

			// Markers first: instruments on transition timelines refer to them.
			CompileMarkers(copy, result, sampleRate);
			var transitionIndices = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < result.Transitions.Length; i++)
			{
				transitionIndices[result.Transitions[i].Source.Id] = i;
			}

			// Instruments.
			var instruments = new List<CompiledInstrument>();
			for (int t = 0; t < copy.Tracks.Count; t++)
			{
				foreach (AudioInstrument instrument in copy.Tracks[t].Instruments)
				{
					instruments.Add(CompileInstrument(instrument, t, instruments.Count, result, samples, sampleRate, transitionIndices));
				}
			}

			result.Instruments = instruments.ToArray();
			var timeline = new List<int>();
			var perTransition = new List<int>[result.Transitions.Length];
			foreach (CompiledInstrument instrument in result.Instruments)
			{
				if (instrument.OnTimeline)
				{
					timeline.Add(instrument.Index);
				}
				else if (instrument.OnTransition)
				{
					(perTransition[instrument.Transition] ??= new List<int>()).Add(instrument.Index);
				}
			}

			timeline.Sort((a, b) => result.Instruments[a].StartSample.CompareTo(result.Instruments[b].StartSample));
			result.TimelineOrder = timeline.ToArray();
			for (int i = 0; i < perTransition.Length; i++)
			{
				if (perTransition[i] != null)
				{
					perTransition[i].Sort((a, b) => result.Instruments[a].StartSample.CompareTo(result.Instruments[b].StartSample));
					result.Transitions[i].Instruments = perTransition[i].ToArray();
				}
			}

			result.LengthSamples = ToSamples(copy.TimelineLength, sampleRate);
			return result;
		}

		public static long ToSamples(double seconds, int sampleRate)
		{
			return (long)Math.Round(Math.Max(0.0, seconds) * sampleRate);
		}

		/// <summary>A private copy of an event (a document round trip: nothing shared with the editor's object).</summary>
		public static AudioEvent CloneEvent(AudioEvent source)
		{
			byte[] data = Documents.WriteObject(source, DocumentFormat.Binary, new SerializationContext { Location = source.Path ?? "AudioEvent" });
			var copy = Documents.ReadObject(data, typeof(AudioEvent), null, new SerializationContext { Location = source.Path ?? "AudioEvent" }) as AudioEvent;
			if (copy == null)
			{
				throw new InvalidOperationException("The event could not be copied");
			}

			copy.Master ??= new AudioTrack { Id = "master", Name = "Master" };
			copy.Spatializer ??= new AudioSpatializer();
			copy.Envelope ??= new AudioEnvelope();
			return copy;
		}

		#endregion

		#region PrivateMethods

		private static CompiledParameter ToCompiled(AudioParameter parameter, bool global)
		{
			return new CompiledParameter
			{
				Source = parameter,
				Id = parameter.Id,
				Name = parameter.Name,
				IsGlobal = global,
				Min = parameter.Min,
				Max = parameter.Max,
				Default = parameter.Clamp(parameter.Default),
				SeekSpeed = Math.Max(0f, parameter.SeekSpeed),
				BuiltIn = parameter.BuiltIn,
				Type = parameter.Type
			};
		}

		private static CompiledTrack CompileTrack(AudioTrack track, int index, CompiledEvent owner)
		{
			var result = new CompiledTrack
			{
				Source = track,
				Index = index,
				Id = track.Id,
				VolumeDb = track.VolumeDb,
				Pan = Math.Clamp(track.Pan, -1f, 1f),
				PitchSemitones = track.PitchSemitones,
				Mute = track.Mute,
				Solo = track.Solo,
				Silent = track.Mute
			};
			var effects = new List<AudioEffect>();
			foreach (AudioEffect effect in track.Effects)
			{
				if (effect != null)
				{
					effects.Add(effect);
				}
			}

			result.Effects = effects.ToArray();
			var automation = new List<CompiledAutomation>();
			foreach (AudioAutomationCurve curve in track.Automation)
			{
				if (curve == null || curve.Points == null || curve.Points.Count == 0)
				{
					continue;
				}

				int parameter = owner.FindParameter(curve.ParameterId);
				if (parameter < 0)
				{
					owner.Warnings.Add($"{owner.Path}: automation of '{curve.Target}' on track '{track.Name}' uses an unknown parameter");
					continue;
				}

				curve.Sort();
				var compiled = new CompiledAutomation { Parameter = parameter, Target = curve.Target, Curve = curve };
				if (AudioAutomationCurve.ParseEffectTarget(curve.Target, out string effectId, out string effectParameter))
				{
					for (int i = 0; i < result.Effects.Length; i++)
					{
						if (result.Effects[i].Id == effectId)
						{
							compiled.EffectIndex = i;
							compiled.EffectParameter = effectParameter;
						}
					}

					if (compiled.EffectIndex < 0)
					{
						owner.Warnings.Add($"{owner.Path}: automation on track '{track.Name}' targets a missing effect");
						continue;
					}
				}

				automation.Add(compiled);
			}

			result.Automation = automation.ToArray();
			return result;
		}

		private static void CompileMarkers(AudioEvent copy, CompiledEvent result, int sampleRate)
		{
			var destinations = new Dictionary<string, long>(StringComparer.Ordinal);
			var points = new List<CompiledMarkerPoint>();
			var tempos = new List<TempoMarker>();
			foreach (AudioMarker marker in copy.Markers)
			{
				switch (marker)
				{
					case DestinationMarker destination:
						destinations[destination.Id] = ToSamples(destination.Position, sampleRate);
						points.Add(new CompiledMarkerPoint { Source = marker, Kind = MarkerPointKind.Destination, Position = ToSamples(marker.Position, sampleRate), Name = marker.Name });
						break;
					case SustainPoint _:
						points.Add(new CompiledMarkerPoint { Source = marker, Kind = MarkerPointKind.Sustain, Position = ToSamples(marker.Position, sampleRate), Name = marker.Name });
						break;
					case TempoMarker tempo:
						tempos.Add(tempo);
						points.Add(new CompiledMarkerPoint { Source = marker, Kind = MarkerPointKind.Tempo, Position = ToSamples(marker.Position, sampleRate), Name = marker.Name });
						break;
				}
			}

			var transitions = new List<CompiledTransition>();
			var loops = new List<CompiledLoop>();
			foreach (AudioMarker marker in copy.Markers)
			{
				switch (marker)
				{
					case TransitionMarker transition:
					{
						long destination;
						if (transition.DestinationId == TransitionMarker.EndDestination)
						{
							destination = -1;
						}
						else if (transition.DestinationId == null || !destinations.TryGetValue(transition.DestinationId, out destination))
						{
							destination = -2;
							result.Warnings.Add($"{copy.Path}: transition '{marker.Name ?? marker.Id}' has no destination");
						}

						var region = transition as TransitionRegion;
						var compiled = new CompiledTransition
						{
							Source = transition,
							Position = ToSamples(transition.Position, sampleRate),
							End = region != null ? ToSamples(transition.Position + region.RegionLength, sampleRate) : ToSamples(transition.Position, sampleRate),
							IsRegion = region != null,
							DestinationId = transition.DestinationId,
							Destination = destination,
							Conditions = CompileConditions(transition.Conditions, result),
							Probability = transition.Probability,
							Crossfade = Math.Max(0.0, transition.Crossfade),
							Quantization = region?.Quantization ?? QuantizationInterval.None
						};
						if (transition.Timeline != null)
						{
							TransitionTimeline timeline = transition.Timeline;
							timeline.Clamp();
							compiled.Timeline = new CompiledTransitionTimeline
							{
								Source = timeline,
								Length = Math.Max(1, ToSamples(timeline.Length, sampleRate)),
								SourceLength = ToSamples(timeline.SourceLength, sampleRate),
								DestinationStart = ToSamples(timeline.DestinationStart, sampleRate),
								SourceFadeLength = ToSamples(timeline.SourceFadeOut.Length, sampleRate),
								SourceFadeShape = timeline.SourceFadeOut.Shape,
								DestinationFadeLength = ToSamples(timeline.DestinationFadeIn.Length, sampleRate),
								DestinationFadeShape = timeline.DestinationFadeIn.Shape
							};
						}

						transitions.Add(compiled);
						break;
					}
					case LoopRegion loop:
						loops.Add(new CompiledLoop
						{
							Source = loop,
							Start = ToSamples(loop.Position, sampleRate),
							End = ToSamples(loop.Position + loop.RegionLength, sampleRate),
							Conditions = CompileConditions(loop.Conditions, result),
							MaxLoops = loop.MaxLoops
						});
						break;
				}
			}

			transitions.Sort((a, b) => a.Position.CompareTo(b.Position));
			for (int i = 0; i < transitions.Count; i++)
			{
				transitions[i].Index = i;
			}

			loops.Sort((a, b) => a.End.CompareTo(b.End));
			points.Sort((a, b) => a.Position.CompareTo(b.Position));
			tempos.Sort((a, b) => a.Position.CompareTo(b.Position));
			result.Transitions = transitions.ToArray();
			result.Loops = loops.ToArray();
			result.Points = points.ToArray();
			result.HasTempo = tempos.Count > 0;
			result.Grid = new BeatGrid(tempos);
		}

		private static CompiledInstrument CompileInstrument(AudioInstrument instrument, int track, int index, CompiledEvent owner, ISampleResolver samples, int sampleRate, Dictionary<string, int> transitionIndices)
		{
			var result = new CompiledInstrument
			{
				Source = instrument,
				Index = index,
				Track = track,
				Id = instrument.Id,
				Name = instrument.DisplayName,
				Start = instrument.Start,
				Length = Math.Max(0.0, instrument.Length),
				Async = instrument.Async,
				Loop = instrument.Loop,
				VolumeDb = instrument.VolumeDb,
				PitchSemitones = instrument.PitchSemitones,
				VolumeRandomDb = Math.Abs(instrument.VolumeRandomDb),
				PitchRandomSemitones = Math.Abs(instrument.PitchRandomSemitones),
				Probability = Math.Clamp(instrument.Probability, 0f, 100f),
				Conditions = CompileConditions(instrument.Conditions, owner),
				Envelope = instrument.Envelope ?? new AudioEnvelope(),
				StartOffset = Math.Max(0.0, instrument.StartOffset),
				StartOffsetRandom = Math.Max(0.0, instrument.StartOffsetRandom),
				Quantization = instrument.Quantization,
				TriggerDelayMin = Math.Max(0.0, Math.Min(instrument.TriggerDelayMin, instrument.TriggerDelayMax)),
				TriggerDelayMax = Math.Max(0.0, Math.Max(instrument.TriggerDelayMin, instrument.TriggerDelayMax))
			};

			if (instrument.OnTransition)
			{
				string transitionId = TransitionTimeline.TransitionOfSheet(instrument.Sheet);
				int transition;
				if (transitionId != null && transitionIndices.TryGetValue(transitionId, out transition) && owner.Transitions[transition].Timeline != null)
				{
					result.Transition = transition;
					result.StartSample = ToSamples(instrument.Start, sampleRate);
					result.EndSample = Math.Max(result.StartSample + 1, ToSamples(instrument.Start + result.Length, sampleRate));
				}
				else
				{
					owner.Warnings.Add($"{owner.Path}: instrument '{instrument.DisplayName}' lies on the timeline of a missing transition; it never plays");
					result.Sheet = int.MaxValue;
				}
			}
			else if (!instrument.OnTimeline)
			{
				result.Sheet = owner.FindParameter(instrument.Sheet);
				if (result.Sheet < 0)
				{
					owner.Warnings.Add($"{owner.Path}: instrument '{instrument.DisplayName}' lies on a sheet of an unknown parameter; it never plays");
					result.Sheet = int.MaxValue;
				}
			}
			else
			{
				result.StartSample = ToSamples(instrument.Start, sampleRate);
				result.EndSample = Math.Max(result.StartSample + 1, ToSamples(instrument.Start + result.Length, sampleRate));
			}

			var playlist = new List<CompiledPlaylistEntry>();
			switch (instrument)
			{
				case SingleInstrument single:
					result.Kind = InstrumentKind.Single;
					AddEntry(playlist, single.Clip, 1f, 0f, 0f, owner, samples, instrument);
					break;
				case MultiInstrument multi:
					result.Kind = InstrumentKind.Multi;
					result.Mode = multi.Mode;
					foreach (AudioPlaylistEntry entry in multi.Playlist)
					{
						AddEntry(playlist, entry.Clip, entry.Weight, entry.VolumeDb, entry.PitchSemitones, owner, samples, instrument);
					}

					break;
				case ScattererInstrument scatterer:
					result.Kind = InstrumentKind.Scatterer;
					result.Mode = scatterer.Mode;
					result.SpawnIntervalMin = Math.Max(0.01, Math.Min(scatterer.SpawnIntervalMin, scatterer.SpawnIntervalMax));
					result.SpawnIntervalMax = Math.Max(result.SpawnIntervalMin, Math.Max(scatterer.SpawnIntervalMin, scatterer.SpawnIntervalMax));
					result.SpawnTotal = Math.Max(0, scatterer.SpawnTotal);
					result.MinScatterDistance = Math.Max(0f, Math.Min(scatterer.MinScatterDistance, scatterer.MaxScatterDistance));
					result.MaxScatterDistance = Math.Max(result.MinScatterDistance, Math.Max(scatterer.MinScatterDistance, scatterer.MaxScatterDistance));
					result.Polyphony = Math.Max(1, scatterer.Polyphony);
					foreach (AudioPlaylistEntry entry in scatterer.Playlist)
					{
						AddEntry(playlist, entry.Clip, entry.Weight, entry.VolumeDb, entry.PitchSemitones, owner, samples, instrument);
					}

					break;
				case EventInstrument nested:
					result.Kind = InstrumentKind.Event;
					result.NestedEvent = nested.Event;
					if (nested.Event.IsNull)
					{
						owner.Warnings.Add($"{owner.Path}: event instrument '{instrument.DisplayName}' names no event");
						result.Kind = InstrumentKind.Silent;
					}

					break;
				case SnapshotInstrument snapshot:
					result.Kind = InstrumentKind.Snapshot;
					result.SnapshotId = snapshot.SnapshotId;
					if (string.IsNullOrEmpty(snapshot.SnapshotId))
					{
						owner.Warnings.Add($"{owner.Path}: snapshot instrument '{instrument.DisplayName}' names no snapshot");
						result.Kind = InstrumentKind.Silent;
					}

					break;
				default:
					result.Kind = InstrumentKind.Silent;
					break;
			}

			result.Playlist = playlist.ToArray();
			if (result.Kind != InstrumentKind.Event && result.Kind != InstrumentKind.Snapshot && playlist.Count == 0)
			{
				result.Kind = InstrumentKind.Silent;
			}

			return result;
		}

		private static void AddEntry(List<CompiledPlaylistEntry> playlist, AudioClipReference clip, float weight, float volumeDb, float pitch, CompiledEvent owner, ISampleResolver samples, AudioInstrument instrument)
		{
			if (clip == null || clip.IsEmpty)
			{
				return;
			}

			SampleData sample = samples?.Resolve(clip);
			if (sample == null)
			{
				owner.Warnings.Add($"{owner.Path}: instrument '{instrument.DisplayName}' uses the missing sound '{clip.DisplayName}'");
				return;
			}

			playlist.Add(new CompiledPlaylistEntry { Sample = sample, Weight = Math.Max(0f, weight), VolumeDb = volumeDb, PitchSemitones = pitch, ClipPath = clip.Path });
		}

		private static CompiledCondition[] CompileConditions(List<AudioCondition> conditions, CompiledEvent owner)
		{
			if (conditions == null || conditions.Count == 0)
			{
				return Array.Empty<CompiledCondition>();
			}

			var result = new List<CompiledCondition>();
			foreach (AudioCondition condition in conditions)
			{
				if (condition == null)
				{
					continue;
				}

				int parameter = owner.FindParameter(condition.ParameterId);
				if (parameter < 0)
				{
					owner.Warnings.Add($"{owner.Path}: a condition uses an unknown parameter and is ignored");
					continue;
				}

				result.Add(new CompiledCondition { Parameter = parameter, Min = condition.Min, Max = condition.Max });
			}

			return result.ToArray();
		}

		#endregion
	}
}
