using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Audio
{
	/// <summary>Conversions the audio system uses everywhere: decibels, semitones, smoothing.</summary>
	public static class AudioMath
	{
		#region PublicFields

		/// <summary>Volumes at or below this many decibels are silence (gain 0).</summary>
		public const float SilenceDb = -80f;

		/// <summary>Loudest volume a fader allows.</summary>
		public const float MaxDb = 10f;

		#endregion

		#region PublicMethods

		public static float DbToGain(float db)
		{
			return db <= SilenceDb ? 0f : MathF.Pow(10f, db / 20f);
		}

		public static float GainToDb(float gain)
		{
			return gain <= 0.0001f ? SilenceDb : Math.Max(SilenceDb, 20f * MathF.Log10(gain));
		}

		public static float SemitonesToRatio(float semitones)
		{
			return MathF.Pow(2f, semitones / 12f);
		}

		public static float RatioToSemitones(float ratio)
		{
			return ratio <= 0f ? -96f : 12f * MathF.Log2(ratio);
		}

		public static float Clamp01(float value)
		{
			return value < 0f ? 0f : value > 1f ? 1f : value;
		}

		public static float Lerp(float a, float b, float t)
		{
			return a + (b - a) * t;
		}

		/// <summary>Random value in [-range, range].</summary>
		public static float Spread(Random random, float range)
		{
			return range <= 0f ? 0f : (float)(random.NextDouble() * 2.0 - 1.0) * range;
		}

		#endregion
	}

	/// <summary>How a parameter's value behaves.</summary>
	public enum AudioParameterType
	{
		/// <summary>Any value between min and max.</summary>
		Continuous,

		/// <summary>Whole numbers between min and max.</summary>
		Discrete,

		/// <summary>Whole numbers with a name each (<see cref="AudioParameter.Labels" />).</summary>
		Labeled
	}

	/// <summary>Parameters the engine drives from an instance's 3D attributes and the listener.</summary>
	public enum BuiltInParameter
	{
		None,

		/// <summary>Distance between the instance and the listener, in world units.</summary>
		Distance,

		/// <summary>Horizontal angle of the instance around the listener, -180..180 degrees (0 = ahead, positive to the right).</summary>
		Direction,

		/// <summary>Vertical angle of the instance from the listener, -90..90 degrees.</summary>
		Elevation,

		/// <summary>Speed of the instance relative to the listener, in units per second.</summary>
		Speed
	}

	/// <summary>Beat grid steps a transition or trigger waits for (relative to the event's tempo markers).</summary>
	public enum QuantizationInterval
	{
		None,
		QuarterBeat,
		HalfBeat,
		Beat,
		TwoBeats,
		Bar,
		TwoBars,
		FourBars,
		EightBars
	}

	/// <summary>How a playlist picks its next entry.</summary>
	public enum PlaylistMode
	{
		/// <summary>Weighted random, repeats allowed.</summary>
		Random,

		/// <summary>Weighted random without repeating an entry until every other one played (also avoids playing the same entry twice in a row).</summary>
		Shuffle,

		/// <summary>In order, wrapping around; the position is kept per event instance.</summary>
		Sequential,

		/// <summary>In order, wrapping around; the position is shared by every instance of the event.</summary>
		SequentialGlobal
	}

	/// <summary>What happens when an event has as many instances playing as it allows and another one starts.</summary>
	public enum StealingMode
	{
		/// <summary>The oldest instance stops.</summary>
		Oldest,

		/// <summary>The quietest instance stops.</summary>
		Quietest,

		/// <summary>The instance farthest from the listener stops.</summary>
		Furthest,

		/// <summary>The new instance does not start.</summary>
		None
	}

	/// <summary>Distance attenuation shape of a spatialized event.</summary>
	public enum RolloffMode
	{
		/// <summary>Full volume at the minimum distance, silent at the maximum, linear in between.</summary>
		Linear,

		/// <summary>Linear in the squared distance fraction (quieter sooner).</summary>
		LinearSquared,

		/// <summary>1 / distance beyond the minimum distance, clamped at the maximum.</summary>
		Inverse,

		/// <summary>1 / distance blended to zero towards the maximum distance.</summary>
		InverseTapered,

		/// <summary>The event's own curve (<see cref="AudioSpatializer.CustomCurve" />) over the normalized distance.</summary>
		Custom
	}

	/// <summary>Where a multi-instrument or scatterer places its picks, when the event is spatialized.</summary>
	public enum AudioStopMode
	{
		/// <summary>Stop with the release fades of the event and its instruments.</summary>
		AllowFadeout,

		/// <summary>Stop at once.</summary>
		Immediate
	}

	/// <summary>A parameter of an event (local) or of the whole project (global): a named value the game sets.</summary>
	[Serializable]
	public sealed class AudioParameter
	{
		#region PublicFields

		public string Id;
		public string Name = "Parameter";
		public float Min;
		public float Max = 1f;
		public float Default;
		public AudioParameterType Type = AudioParameterType.Continuous;

		/// <summary>Names of the values of a <see cref="AudioParameterType.Labeled" /> parameter (index = value - Min).</summary>
		public List<string> Labels = new List<string>();

		/// <summary>Units per second the value moves towards what the game set; 0 = jumps at once.</summary>
		public float SeekSpeed;

		/// <summary>Driven by the engine from the instance's 3D attributes (the game cannot set it then).</summary>
		public BuiltInParameter BuiltIn = BuiltInParameter.None;

		#endregion

		#region PublicMethods

		public float Clamp(float value)
		{
			value = Math.Clamp(value, Math.Min(Min, Max), Math.Max(Min, Max));
			return Type == AudioParameterType.Continuous ? value : MathF.Round(value);
		}

		public override string ToString()
		{
			return Name;
		}

		#endregion
	}

	/// <summary>An audio file of the project (wav, ogg, mp3), by asset GUID with the path for messages and as a fallback.</summary>
	[Serializable]
	public sealed class AudioClipReference
	{
		#region PublicFields

		public string Guid;
		public string Path;

		#endregion

		#region PublicMethods

		public bool IsEmpty => string.IsNullOrEmpty(Guid) && string.IsNullOrEmpty(Path);

		/// <summary>The clip's root-relative asset path as the asset database knows it now (by GUID, then by the stored path), or null.</summary>
		public string Resolve()
		{
			if (IsEmpty)
			{
				return null;
			}

			string path = string.IsNullOrEmpty(Guid) ? null : AssetDatabase.GetPath(Guid);
			if (path == null && !string.IsNullOrEmpty(Path))
			{
				path = AssetDatabase.Resolve(Path, null);
			}

			return path;
		}

		public static AudioClipReference FromPath(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return new AudioClipReference();
			}

			return new AudioClipReference { Guid = AssetDatabase.GetGuid(assetPath), Path = assetPath.Replace('\\', '/') };
		}

		public string DisplayName => string.IsNullOrEmpty(Path) ? (string.IsNullOrEmpty(Guid) ? "(none)" : Guid) : System.IO.Path.GetFileName(Path);

		public override string ToString()
		{
			return DisplayName;
		}

		#endregion
	}

	/// <summary>
	///     A reference to an event of the project's audio project, as components store it: the event's GUID (stable
	///     across renames) with its path for display and as a fallback. The inspector draws it as an event picker.
	/// </summary>
	[Serializable]
	public struct AudioEventReference
	{
		#region PublicFields

		public string Guid;
		public string Path;

		#endregion

		#region PublicMethods

		public bool IsNull => string.IsNullOrEmpty(Guid) && string.IsNullOrEmpty(Path);

		public static AudioEventReference FromEvent(AudioEvent audioEvent)
		{
			return audioEvent == null ? default : new AudioEventReference { Guid = audioEvent.Id, Path = audioEvent.Path };
		}

		public static AudioEventReference FromPath(string path)
		{
			return new AudioEventReference { Path = path };
		}

		public override string ToString()
		{
			return IsNull ? "(none)" : Path ?? Guid;
		}

		#endregion
	}

	/// <summary>A fade at the edge of a region: how long it is and how it curves.</summary>
	[Serializable]
	public sealed class AudioFade
	{
		#region PublicFields

		/// <summary>Seconds the fade takes.</summary>
		public double Length;

		/// <summary>0 linear; towards -1 the gain stays low longer (ease in), towards 1 it rises quickly (ease out).</summary>
		public float Shape;

		#endregion

		#region PublicMethods

		/// <summary>Gain at a progress of 0 (silent end) .. 1 (full end).</summary>
		public float Evaluate(double progress)
		{
			return AudioAutomationCurve.Shape((float)Math.Clamp(progress, 0.0, 1.0), Shape);
		}

		public AudioFade Clone()
		{
			return new AudioFade { Length = Length, Shape = Shape };
		}

		#endregion
	}

	/// <summary>
	///     What happens between the source and the destination of a transition (FMOD's transition timeline): a
	///     timeline of its own. The sounds playing at the source keep playing into it (the source region, with a fade
	///     out), the destination's sounds start at <see cref="DestinationStart" /> (the destination region, with a
	///     fade in), and instruments placed on the transition timeline itself (sheet <see cref="SheetId" />) play as
	///     it runs. When it ends, playback continues on the destination timeline where the destination region got to.
	/// </summary>
	[Serializable]
	public sealed class TransitionTimeline
	{
		#region PublicFields

		public const string SheetPrefix = "transition:";

		/// <summary>Length of the transition timeline, seconds.</summary>
		public double Length = 2.0;

		/// <summary>The source's sounds keep playing until this time of the transition timeline.</summary>
		public double SourceLength = 1.0;

		/// <summary>The destination's sounds start at this time of the transition timeline.</summary>
		public double DestinationStart = 1.0;

		/// <summary>Fade out ending at <see cref="SourceLength" />.</summary>
		public AudioFade SourceFadeOut = new AudioFade { Length = 0.5 };

		/// <summary>Fade in starting at <see cref="DestinationStart" />.</summary>
		public AudioFade DestinationFadeIn = new AudioFade { Length = 0.5 };

		#endregion

		#region PublicMethods

		/// <summary>The sheet id of a transition's own instruments.</summary>
		public static string SheetId(string transitionId)
		{
			return SheetPrefix + transitionId;
		}

		/// <summary>The transition id a sheet id names, or null for other sheets.</summary>
		public static string TransitionOfSheet(string sheet)
		{
			return sheet != null && sheet.StartsWith(SheetPrefix, StringComparison.Ordinal) ? sheet.Substring(SheetPrefix.Length) : null;
		}

		/// <summary>Keeps the regions inside the timeline.</summary>
		public void Clamp()
		{
			Length = Math.Max(0.01, Length);
			SourceLength = Math.Clamp(SourceLength, 0.0, Length);
			DestinationStart = Math.Clamp(DestinationStart, 0.0, Length);
			SourceFadeOut ??= new AudioFade();
			DestinationFadeIn ??= new AudioFade();
			SourceFadeOut.Length = Math.Clamp(SourceFadeOut.Length, 0.0, SourceLength);
			DestinationFadeIn.Length = Math.Clamp(DestinationFadeIn.Length, 0.0, Length - DestinationStart);
		}

		public TransitionTimeline Clone()
		{
			return new TransitionTimeline { Length = Length, SourceLength = SourceLength, DestinationStart = DestinationStart, SourceFadeOut = SourceFadeOut?.Clone() ?? new AudioFade(), DestinationFadeIn = DestinationFadeIn?.Clone() ?? new AudioFade() };
		}

		#endregion
	}

	/// <summary>How a snapshot's values combine with the mixer's.</summary>
	public enum SnapshotMode
	{
		/// <summary>The snapshot's values replace the bus values (blended by the intensity; higher priority snapshots on top).</summary>
		Overriding,

		/// <summary>The snapshot's volumes add to the bus volumes (in decibels, scaled by the intensity); effect values override.</summary>
		Blending
	}

	/// <summary>A value a snapshot sets on an effect of a bus.</summary>
	[Serializable]
	public sealed class SnapshotEffectValue
	{
		#region PublicFields

		public string EffectId;
		public string Parameter;
		public float Value;

		#endregion
	}

	/// <summary>What a snapshot does to one bus: its volume when scoped, and effect parameters.</summary>
	[Serializable]
	public sealed class SnapshotBusValue
	{
		#region PublicFields

		public string BusId;

		/// <summary>The volume is part of the snapshot (else the bus keeps its own).</summary>
		public bool SetVolume = true;

		public float VolumeDb;
		public List<SnapshotEffectValue> Effects = new List<SnapshotEffectValue>();

		#endregion

		#region PublicMethods

		public SnapshotEffectValue FindEffect(string effectId, string parameter)
		{
			foreach (SnapshotEffectValue value in Effects)
			{
				if (value.EffectId == effectId && value.Parameter == parameter)
				{
					return value;
				}
			}

			return null;
		}

		#endregion
	}

	/// <summary>
	///     A mixer snapshot (as FMOD's): a set of bus volumes and effect values the game switches on and off with an
	///     intensity. Several active snapshots stack: the project's list order is the priority (first = highest).
	/// </summary>
	[Serializable]
	public sealed class AudioSnapshot
	{
		#region PublicFields

		public string Id;
		public string Name = "Snapshot";
		public SnapshotMode Mode = SnapshotMode.Overriding;

		/// <summary>Attack = fade in when started, release = fade out when stopped (the intensity's envelope).</summary>
		public AudioEnvelope Intensity = new AudioEnvelope { Attack = 0.2f, Release = 0.5f };

		public List<SnapshotBusValue> Buses = new List<SnapshotBusValue>();
		public string Notes = "";

		#endregion

		#region PublicMethods

		public SnapshotBusValue FindBus(string busId)
		{
			foreach (SnapshotBusValue bus in Buses)
			{
				if (bus.BusId == busId)
				{
					return bus;
				}
			}

			return null;
		}

		public override string ToString()
		{
			return Name;
		}

		#endregion
	}

	/// <summary>Attack, hold, decay, sustain, release of a volume: fades in when something starts, fades out when it stops.</summary>
	[Serializable]
	public sealed class AudioEnvelope
	{
		#region PublicFields

		/// <summary>Seconds from silence to full volume when triggered.</summary>
		public float Attack;

		/// <summary>Seconds at full volume before the decay.</summary>
		public float Hold;

		/// <summary>Seconds from full volume down to the sustain level.</summary>
		public float Decay;

		/// <summary>Level held after the decay, in decibels (0 = full volume).</summary>
		public float SustainDb;

		/// <summary>Seconds from the current level to silence when untriggered or stopped.</summary>
		public float Release;

		#endregion

		#region PublicMethods

		/// <summary>No shaping at all: the envelope can be skipped.</summary>
		public bool IsTrivial => Attack <= 0f && Hold <= 0f && Decay <= 0f && Release <= 0f && SustainDb >= 0f;

		public AudioEnvelope Clone()
		{
			return new AudioEnvelope { Attack = Attack, Hold = Hold, Decay = Decay, SustainDb = SustainDb, Release = Release };
		}

		#endregion
	}

	/// <summary>A test of a parameter's value: true while Min &lt;= value &lt;= Max.</summary>
	[Serializable]
	public sealed class AudioCondition
	{
		#region PublicFields

		public string ParameterId;
		public float Min;
		public float Max = 1f;

		#endregion

		#region PublicMethods

		public bool Test(float value)
		{
			return value >= Math.Min(Min, Max) - 1e-6f && value <= Math.Max(Min, Max) + 1e-6f;
		}

		#endregion
	}

	/// <summary>One point of an automation curve: the parameter value and the property value there.</summary>
	[Serializable]
	public sealed class AudioCurvePoint
	{
		#region PublicFields

		public float X;
		public float Y;

		/// <summary>Shape of the segment to the next point: 0 linear, towards -1 eases in (stays near this point longer), towards 1 eases out.</summary>
		public float Tension;

		#endregion
	}

	/// <summary>
	///     Automation: a property of a track, the event or an effect follows a curve over a parameter's value (the
	///     property's value becomes the curve's). Targets: <see cref="VolumeTarget" />, <see cref="PitchTarget" />,
	///     <see cref="PanTarget" />, or an effect parameter as "Effect:&lt;effectId&gt;:&lt;parameter&gt;".
	/// </summary>
	[Serializable]
	public sealed class AudioAutomationCurve
	{
		#region PublicFields

		public const string VolumeTarget = "Volume";
		public const string PitchTarget = "Pitch";
		public const string PanTarget = "Pan";
		public const string EffectPrefix = "Effect:";

		public string ParameterId;
		public string Target = VolumeTarget;
		public List<AudioCurvePoint> Points = new List<AudioCurvePoint>();

		#endregion

		#region PublicMethods

		public static string EffectTarget(string effectId, string parameter)
		{
			return EffectPrefix + effectId + ":" + parameter;
		}

		/// <summary>Splits an effect target into its effect id and parameter name; false for other targets.</summary>
		public static bool ParseEffectTarget(string target, out string effectId, out string parameter)
		{
			effectId = null;
			parameter = null;
			if (target == null || !target.StartsWith(EffectPrefix, StringComparison.Ordinal))
			{
				return false;
			}

			int colon = target.IndexOf(':', EffectPrefix.Length);
			if (colon < 0)
			{
				return false;
			}

			effectId = target.Substring(EffectPrefix.Length, colon - EffectPrefix.Length);
			parameter = target.Substring(colon + 1);
			return true;
		}

		/// <summary>The curve's value at <paramref name="x" /> (held flat before the first and after the last point).</summary>
		public float Evaluate(float x)
		{
			return Evaluate(Points, x);
		}

		public static float Evaluate(List<AudioCurvePoint> points, float x)
		{
			if (points == null || points.Count == 0)
			{
				return 0f;
			}

			if (points.Count == 1 || x <= points[0].X)
			{
				return points[0].Y;
			}

			AudioCurvePoint last = points[points.Count - 1];
			if (x >= last.X)
			{
				return last.Y;
			}

			for (int i = 0; i < points.Count - 1; i++)
			{
				AudioCurvePoint a = points[i];
				AudioCurvePoint b = points[i + 1];
				if (x >= a.X && x <= b.X)
				{
					float span = b.X - a.X;
					float t = span <= 1e-6f ? 1f : (x - a.X) / span;
					t = Shape(t, a.Tension);
					return a.Y + (b.Y - a.Y) * t;
				}
			}

			return last.Y;
		}

		/// <summary>Bends a linear 0..1 progression by a tension in -1..1.</summary>
		public static float Shape(float t, float tension)
		{
			if (Math.Abs(tension) < 1e-4f)
			{
				return t;
			}

			// Exponent 1 at tension 0, up to 8 (ease in) or 1/8 (ease out).
			float exponent = MathF.Pow(8f, Math.Clamp(tension, -1f, 1f));
			return MathF.Pow(Math.Clamp(t, 0f, 1f), exponent);
		}

		/// <summary>Keeps the points ordered by X (after an edit moved one).</summary>
		public void Sort()
		{
			Points.Sort((a, b) => a.X.CompareTo(b.X));
		}

		#endregion
	}

	/// <summary>
	///     A signal processor on a track or a bus. Parameters are addressed by name so automation curves can drive
	///     them (<see cref="ParameterNames" />, <see cref="GetParameter" />, <see cref="SetParameter" />).
	/// </summary>
	[Serializable]
	public abstract class AudioEffect
	{
		#region PublicFields

		public string Id;
		public bool Bypass;

		#endregion

		#region PublicMethods

		public abstract string DisplayName { get; }
		public abstract string[] ParameterNames { get; }
		public abstract float GetParameter(string name);
		public abstract void SetParameter(string name, float value);

		/// <summary>Range and unit of a parameter for the editor's widgets.</summary>
		public abstract void GetParameterRange(string name, out float min, out float max, out string unit);

		public abstract AudioEffect Clone();

		#endregion
	}

	/// <summary>Removes frequencies above the cutoff.</summary>
	[Serializable]
	public sealed class LowPassEffect : AudioEffect
	{
		#region PublicFields

		public const string Cutoff = "Cutoff";
		public const string Resonance = "Resonance";

		public float CutoffHz = 22000f;
		public float Q = 0.707f;

		private static readonly string[] Names = { Cutoff, Resonance };

		#endregion

		#region PublicMethods

		public override string DisplayName => "Low Pass";
		public override string[] ParameterNames => Names;

		public override float GetParameter(string name)
		{
			return name == Cutoff ? CutoffHz : name == Resonance ? Q : 0f;
		}

		public override void SetParameter(string name, float value)
		{
			if (name == Cutoff) CutoffHz = value;
			else if (name == Resonance) Q = value;
		}

		public override void GetParameterRange(string name, out float min, out float max, out string unit)
		{
			if (name == Cutoff)
			{
				min = 20f;
				max = 22000f;
				unit = "Hz";
			}
			else
			{
				min = 0.1f;
				max = 10f;
				unit = "";
			}
		}

		public override AudioEffect Clone()
		{
			return new LowPassEffect { Id = Id, Bypass = Bypass, CutoffHz = CutoffHz, Q = Q };
		}

		#endregion
	}

	/// <summary>Removes frequencies below the cutoff.</summary>
	[Serializable]
	public sealed class HighPassEffect : AudioEffect
	{
		#region PublicFields

		public const string Cutoff = "Cutoff";
		public const string Resonance = "Resonance";

		public float CutoffHz = 20f;
		public float Q = 0.707f;

		private static readonly string[] Names = { Cutoff, Resonance };

		#endregion

		#region PublicMethods

		public override string DisplayName => "High Pass";
		public override string[] ParameterNames => Names;

		public override float GetParameter(string name)
		{
			return name == Cutoff ? CutoffHz : name == Resonance ? Q : 0f;
		}

		public override void SetParameter(string name, float value)
		{
			if (name == Cutoff) CutoffHz = value;
			else if (name == Resonance) Q = value;
		}

		public override void GetParameterRange(string name, out float min, out float max, out string unit)
		{
			if (name == Cutoff)
			{
				min = 20f;
				max = 22000f;
				unit = "Hz";
			}
			else
			{
				min = 0.1f;
				max = 10f;
				unit = "";
			}
		}

		public override AudioEffect Clone()
		{
			return new HighPassEffect { Id = Id, Bypass = Bypass, CutoffHz = CutoffHz, Q = Q };
		}

		#endregion
	}

	/// <summary>Room reverberation (Freeverb).</summary>
	[Serializable]
	public sealed class ReverbEffect : AudioEffect
	{
		#region PublicFields

		public const string RoomSizeName = "Room Size";
		public const string DampingName = "Damping";
		public const string WidthName = "Width";
		public const string WetName = "Wet";
		public const string DryName = "Dry";

		public float RoomSize = 0.6f;
		public float Damping = 0.5f;
		public float Width = 1f;
		public float WetDb = -10f;
		public float DryDb;

		private static readonly string[] Names = { RoomSizeName, DampingName, WidthName, WetName, DryName };

		#endregion

		#region PublicMethods

		public override string DisplayName => "Reverb";
		public override string[] ParameterNames => Names;

		public override float GetParameter(string name)
		{
			switch (name)
			{
				case RoomSizeName: return RoomSize;
				case DampingName: return Damping;
				case WidthName: return Width;
				case WetName: return WetDb;
				case DryName: return DryDb;
				default: return 0f;
			}
		}

		public override void SetParameter(string name, float value)
		{
			switch (name)
			{
				case RoomSizeName: RoomSize = value; break;
				case DampingName: Damping = value; break;
				case WidthName: Width = value; break;
				case WetName: WetDb = value; break;
				case DryName: DryDb = value; break;
			}
		}

		public override void GetParameterRange(string name, out float min, out float max, out string unit)
		{
			if (name == WetName || name == DryName)
			{
				min = AudioMath.SilenceDb;
				max = AudioMath.MaxDb;
				unit = "dB";
			}
			else
			{
				min = 0f;
				max = 1f;
				unit = "";
			}
		}

		public override AudioEffect Clone()
		{
			return new ReverbEffect { Id = Id, Bypass = Bypass, RoomSize = RoomSize, Damping = Damping, Width = Width, WetDb = WetDb, DryDb = DryDb };
		}

		#endregion
	}

	/// <summary>Echo: the signal repeated after a delay, feeding back.</summary>
	[Serializable]
	public sealed class DelayEffect : AudioEffect
	{
		#region PublicFields

		public const string TimeName = "Time";
		public const string FeedbackName = "Feedback";
		public const string WetName = "Wet";
		public const string DryName = "Dry";
		public const string LowPassName = "Feedback Low Pass";

		public float DelaySeconds = 0.3f;
		public float Feedback = 0.4f;
		public float WetDb = -6f;
		public float DryDb;
		public float FeedbackLowPassHz = 8000f;

		private static readonly string[] Names = { TimeName, FeedbackName, WetName, DryName, LowPassName };

		#endregion

		#region PublicMethods

		public override string DisplayName => "Delay";
		public override string[] ParameterNames => Names;

		public override float GetParameter(string name)
		{
			switch (name)
			{
				case TimeName: return DelaySeconds;
				case FeedbackName: return Feedback;
				case WetName: return WetDb;
				case DryName: return DryDb;
				case LowPassName: return FeedbackLowPassHz;
				default: return 0f;
			}
		}

		public override void SetParameter(string name, float value)
		{
			switch (name)
			{
				case TimeName: DelaySeconds = value; break;
				case FeedbackName: Feedback = value; break;
				case WetName: WetDb = value; break;
				case DryName: DryDb = value; break;
				case LowPassName: FeedbackLowPassHz = value; break;
			}
		}

		public override void GetParameterRange(string name, out float min, out float max, out string unit)
		{
			switch (name)
			{
				case TimeName:
					min = 0.001f;
					max = 5f;
					unit = "s";
					break;
				case FeedbackName:
					min = 0f;
					max = 0.98f;
					unit = "";
					break;
				case LowPassName:
					min = 100f;
					max = 22000f;
					unit = "Hz";
					break;
				default:
					min = AudioMath.SilenceDb;
					max = AudioMath.MaxDb;
					unit = "dB";
					break;
			}
		}

		public override AudioEffect Clone()
		{
			return new DelayEffect { Id = Id, Bypass = Bypass, DelaySeconds = DelaySeconds, Feedback = Feedback, WetDb = WetDb, DryDb = DryDb, FeedbackLowPassHz = FeedbackLowPassHz };
		}

		#endregion
	}

	/// <summary>One sound of a playlist with its pick weight and its own trim.</summary>
	[Serializable]
	public sealed class AudioPlaylistEntry
	{
		#region PublicFields

		public AudioClipReference Clip = new AudioClipReference();
		public float Weight = 1f;
		public float VolumeDb;
		public float PitchSemitones;

		#endregion

		#region PublicMethods

		public AudioPlaylistEntry Clone()
		{
			return new AudioPlaylistEntry { Clip = new AudioClipReference { Guid = Clip?.Guid, Path = Clip?.Path }, Weight = Weight, VolumeDb = VolumeDb, PitchSemitones = PitchSemitones };
		}

		#endregion
	}

	/// <summary>
	///     Something that plays when the timeline cursor (or a parameter's value, on a parameter sheet) is inside its
	///     region: a sound, a playlist of sounds, a scatterer or another event.
	/// </summary>
	[Serializable]
	public abstract class AudioInstrument
	{
		#region PublicFields

		public string Id;

		/// <summary>Shown in the editor; empty: the sound's name.</summary>
		public string Name;

		/// <summary>The sheet the region lies on: null or empty for the timeline, else a parameter id (the region is a value range then).</summary>
		public string Sheet;

		/// <summary>Start of the trigger region: seconds on the timeline, parameter units on a parameter sheet.</summary>
		public double Start;

		/// <summary>Length of the trigger region (same units as <see cref="Start" />).</summary>
		public double Length = 1.0;

		/// <summary>
		///     Plays to its end once triggered, even after the cursor left the region (one-shots). Off: the sound follows
		///     the region: it starts at the offset the cursor enters at and stops when the cursor leaves (music, layers).
		/// </summary>
		public bool Async;

		/// <summary>Loops the sound while the region is triggered.</summary>
		public bool Loop;

		public float VolumeDb;
		public float PitchSemitones;

		/// <summary>Each trigger picks a volume offset in [-x, x] decibels.</summary>
		public float VolumeRandomDb;

		/// <summary>Each trigger picks a pitch offset in [-x, x] semitones.</summary>
		public float PitchRandomSemitones;

		/// <summary>Chance of the trigger playing, 0..100.</summary>
		public float Probability = 100f;

		/// <summary>Every condition must hold (on the trigger) for the instrument to play.</summary>
		public List<AudioCondition> Conditions = new List<AudioCondition>();

		/// <summary>Volume shaping of each trigger.</summary>
		public AudioEnvelope Envelope = new AudioEnvelope();

		/// <summary>Seconds into the sound the playback starts at (beyond what a cursor entering the region mid-way adds).</summary>
		public double StartOffset;

		/// <summary>Each trigger adds a random offset in [0, x] seconds.</summary>
		public double StartOffsetRandom;

		/// <summary>The trigger waits for the next beat grid point of the event's tempo.</summary>
		public QuantizationInterval Quantization = QuantizationInterval.None;

		/// <summary>Each trigger waits a random time in [min, max] seconds first.</summary>
		public double TriggerDelayMin;

		public double TriggerDelayMax;

		#endregion

		#region PublicMethods

		public double End => Start + Length;

		/// <summary>The instrument sits on the timeline (not on a parameter sheet or a transition timeline).</summary>
		public bool OnTimeline => string.IsNullOrEmpty(Sheet);

		/// <summary>The instrument sits on a transition timeline (<see cref="TransitionTimeline.SheetId" />).</summary>
		public bool OnTransition => TransitionTimeline.TransitionOfSheet(Sheet) != null;

		/// <summary>The region is in seconds (the timeline or a transition timeline), not in parameter units.</summary>
		public bool IsTimeBased => OnTimeline || OnTransition;

		public abstract string Kind { get; }

		/// <summary>Name for the editor: the given name, else the sound's.</summary>
		public abstract string DefaultName { get; }

		public string DisplayName => string.IsNullOrEmpty(Name) ? DefaultName : Name;

		/// <summary>Every sound the instrument can play (for banks and the editor).</summary>
		public abstract IEnumerable<AudioClipReference> Clips { get; }

		public AudioInstrument Clone()
		{
			AudioInstrument copy = CloneSpecific();
			copy.Id = Id;
			copy.Name = Name;
			copy.Sheet = Sheet;
			copy.Start = Start;
			copy.Length = Length;
			copy.Async = Async;
			copy.Loop = Loop;
			copy.VolumeDb = VolumeDb;
			copy.PitchSemitones = PitchSemitones;
			copy.VolumeRandomDb = VolumeRandomDb;
			copy.PitchRandomSemitones = PitchRandomSemitones;
			copy.Probability = Probability;
			copy.Conditions = new List<AudioCondition>();
			foreach (AudioCondition condition in Conditions)
			{
				copy.Conditions.Add(new AudioCondition { ParameterId = condition.ParameterId, Min = condition.Min, Max = condition.Max });
			}

			copy.Envelope = Envelope?.Clone() ?? new AudioEnvelope();
			copy.StartOffset = StartOffset;
			copy.StartOffsetRandom = StartOffsetRandom;
			copy.Quantization = Quantization;
			copy.TriggerDelayMin = TriggerDelayMin;
			copy.TriggerDelayMax = TriggerDelayMax;
			return copy;
		}

		#endregion

		#region ProtectedMethods

		protected abstract AudioInstrument CloneSpecific();

		#endregion
	}

	/// <summary>Plays one sound.</summary>
	[Serializable]
	public sealed class SingleInstrument : AudioInstrument
	{
		#region PublicFields

		public AudioClipReference Clip = new AudioClipReference();

		#endregion

		#region PublicMethods

		public override string Kind => "Single";
		public override string DefaultName => Clip != null && !Clip.IsEmpty ? System.IO.Path.GetFileNameWithoutExtension(Clip.Path ?? "") : "Single Instrument";

		public override IEnumerable<AudioClipReference> Clips
		{
			get
			{
				if (Clip != null && !Clip.IsEmpty)
				{
					yield return Clip;
				}
			}
		}

		#endregion

		#region ProtectedMethods

		protected override AudioInstrument CloneSpecific()
		{
			return new SingleInstrument { Clip = new AudioClipReference { Guid = Clip?.Guid, Path = Clip?.Path } };
		}

		#endregion
	}

	/// <summary>Plays one entry of a playlist per trigger.</summary>
	[Serializable]
	public sealed class MultiInstrument : AudioInstrument
	{
		#region PublicFields

		public List<AudioPlaylistEntry> Playlist = new List<AudioPlaylistEntry>();
		public PlaylistMode Mode = PlaylistMode.Shuffle;

		#endregion

		#region PublicMethods

		public override string Kind => "Multi";
		public override string DefaultName => "Multi Instrument";

		public override IEnumerable<AudioClipReference> Clips
		{
			get
			{
				foreach (AudioPlaylistEntry entry in Playlist)
				{
					if (entry.Clip != null && !entry.Clip.IsEmpty)
					{
						yield return entry.Clip;
					}
				}
			}
		}

		#endregion

		#region ProtectedMethods

		protected override AudioInstrument CloneSpecific()
		{
			var copy = new MultiInstrument { Mode = Mode };
			foreach (AudioPlaylistEntry entry in Playlist)
			{
				copy.Playlist.Add(entry.Clone());
			}

			return copy;
		}

		#endregion
	}

	/// <summary>Spawns sounds of its playlist at random intervals around the event, while triggered (ambiences).</summary>
	[Serializable]
	public sealed class ScattererInstrument : AudioInstrument
	{
		#region PublicFields

		public List<AudioPlaylistEntry> Playlist = new List<AudioPlaylistEntry>();
		public PlaylistMode Mode = PlaylistMode.Shuffle;

		/// <summary>Seconds between spawns, picked at random in [min, max].</summary>
		public double SpawnIntervalMin = 0.5;

		public double SpawnIntervalMax = 2.0;

		/// <summary>Spawns per trigger; 0 = until untriggered.</summary>
		public int SpawnTotal;

		/// <summary>World units from the event a spawn is placed at (a random point in the ring), when the event is spatialized.</summary>
		public float MinScatterDistance;

		public float MaxScatterDistance = 5f;

		/// <summary>Spawns playing at once at most (the oldest stops).</summary>
		public int Polyphony = 8;

		#endregion

		#region PublicMethods

		public override string Kind => "Scatterer";
		public override string DefaultName => "Scatterer Instrument";

		public override IEnumerable<AudioClipReference> Clips
		{
			get
			{
				foreach (AudioPlaylistEntry entry in Playlist)
				{
					if (entry.Clip != null && !entry.Clip.IsEmpty)
					{
						yield return entry.Clip;
					}
				}
			}
		}

		#endregion

		#region ProtectedMethods

		protected override AudioInstrument CloneSpecific()
		{
			var copy = new ScattererInstrument
			{
				Mode = Mode,
				SpawnIntervalMin = SpawnIntervalMin,
				SpawnIntervalMax = SpawnIntervalMax,
				SpawnTotal = SpawnTotal,
				MinScatterDistance = MinScatterDistance,
				MaxScatterDistance = MaxScatterDistance,
				Polyphony = Polyphony
			};
			foreach (AudioPlaylistEntry entry in Playlist)
			{
				copy.Playlist.Add(entry.Clone());
			}

			return copy;
		}

		#endregion
	}

	/// <summary>Starts an instance of another event (and stops it when untriggered, unless async).</summary>
	[Serializable]
	public sealed class EventInstrument : AudioInstrument
	{
		#region PublicFields

		public AudioEventReference Event;

		#endregion

		#region PublicMethods

		public override string Kind => "Event";
		public override string DefaultName => Event.IsNull ? "Event Instrument" : System.IO.Path.GetFileName(Event.Path ?? Event.Guid);

		public override IEnumerable<AudioClipReference> Clips
		{
			get { yield break; }
		}

		#endregion

		#region ProtectedMethods

		protected override AudioInstrument CloneSpecific()
		{
			return new EventInstrument { Event = Event };
		}

		#endregion
	}

	/// <summary>Switches a mixer snapshot on while its region is triggered.</summary>
	[Serializable]
	public sealed class SnapshotInstrument : AudioInstrument
	{
		#region PublicFields

		public string SnapshotId;

		#endregion

		#region PublicMethods

		public override string Kind => "Snapshot";
		public override string DefaultName => "Snapshot Instrument";

		public override IEnumerable<AudioClipReference> Clips
		{
			get { yield break; }
		}

		#endregion

		#region ProtectedMethods

		protected override AudioInstrument CloneSpecific()
		{
			return new SnapshotInstrument { SnapshotId = SnapshotId };
		}

		#endregion
	}

	/// <summary>
	///     A point (or region) on the event's logic track: a named destination, a transition, a loop region, a tempo
	///     change or a sustain point.
	/// </summary>
	[Serializable]
	public abstract class AudioMarker
	{
		#region PublicFields

		public string Id;
		public string Name;

		/// <summary>Position on the timeline, seconds.</summary>
		public double Position;

		#endregion

		#region PublicMethods

		public abstract string Kind { get; }

		/// <summary>Length of a region marker (0 for points).</summary>
		public virtual double Length => 0.0;

		public double End => Position + Length;

		public abstract AudioMarker Clone();

		#endregion
	}

	/// <summary>A named point transitions jump to.</summary>
	[Serializable]
	public sealed class DestinationMarker : AudioMarker
	{
		#region PublicMethods

		public override string Kind => "Destination";

		public override AudioMarker Clone()
		{
			return new DestinationMarker { Id = Id, Name = Name, Position = Position };
		}

		#endregion
	}

	/// <summary>
	///     When the cursor reaches it and its conditions hold, the cursor jumps to the destination (a destination
	///     marker, or <see cref="EndDestination" />: past the end, stopping the event).
	/// </summary>
	[Serializable]
	public class TransitionMarker : AudioMarker
	{
		#region PublicFields

		public const string EndDestination = "end";

		/// <summary>Id of the destination marker, or <see cref="EndDestination" />.</summary>
		public string DestinationId;

		public List<AudioCondition> Conditions = new List<AudioCondition>();

		/// <summary>Chance of the transition happening, 0..100.</summary>
		public float Probability = 100f;

		/// <summary>Seconds the sounds playing before the jump fade out over while the destination's sounds fade in (0 = cut); unused when a <see cref="Timeline" /> is set.</summary>
		public double Crossfade;

		/// <summary>The transition timeline (null: an instant jump, with <see cref="Crossfade" />).</summary>
		public TransitionTimeline Timeline;

		#endregion

		#region PublicMethods

		public override string Kind => "Transition";

		public override AudioMarker Clone()
		{
			var copy = new TransitionMarker { Id = Id, Name = Name, Position = Position, DestinationId = DestinationId, Probability = Probability, Crossfade = Crossfade, Timeline = Timeline?.Clone() };
			CopyConditions(copy);
			return copy;
		}

		#endregion

		#region ProtectedMethods

		protected void CopyConditions(TransitionMarker copy)
		{
			copy.Conditions = new List<AudioCondition>();
			foreach (AudioCondition condition in Conditions)
			{
				copy.Conditions.Add(new AudioCondition { ParameterId = condition.ParameterId, Min = condition.Min, Max = condition.Max });
			}
		}

		#endregion
	}

	/// <summary>
	///     While the cursor is inside the region and the conditions hold, the cursor jumps to the destination at the
	///     next beat grid point (<see cref="Quantization" />; none: at once).
	/// </summary>
	[Serializable]
	public sealed class TransitionRegion : TransitionMarker
	{
		#region PublicFields

		public double RegionLength = 4.0;
		public QuantizationInterval Quantization = QuantizationInterval.Bar;

		#endregion

		#region PublicMethods

		public override string Kind => "Transition Region";
		public override double Length => RegionLength;

		public override AudioMarker Clone()
		{
			var copy = new TransitionRegion { Id = Id, Name = Name, Position = Position, DestinationId = DestinationId, Probability = Probability, Crossfade = Crossfade, Timeline = Timeline?.Clone(), RegionLength = RegionLength, Quantization = Quantization };
			CopyConditions(copy);
			return copy;
		}

		#endregion
	}

	/// <summary>When the cursor reaches the region's end and the conditions hold, it jumps back to the region's start.</summary>
	[Serializable]
	public sealed class LoopRegion : AudioMarker
	{
		#region PublicFields

		public double RegionLength = 4.0;
		public List<AudioCondition> Conditions = new List<AudioCondition>();

		/// <summary>Loops at most this many times per pass; 0 = while the conditions hold.</summary>
		public int MaxLoops;

		#endregion

		#region PublicMethods

		public override string Kind => "Loop Region";
		public override double Length => RegionLength;

		public override AudioMarker Clone()
		{
			var copy = new LoopRegion { Id = Id, Name = Name, Position = Position, RegionLength = RegionLength, MaxLoops = MaxLoops };
			foreach (AudioCondition condition in Conditions)
			{
				copy.Conditions.Add(new AudioCondition { ParameterId = condition.ParameterId, Min = condition.Min, Max = condition.Max });
			}

			return copy;
		}

		#endregion
	}

	/// <summary>Tempo and time signature from this position on (the beat grid quantization and snapping use).</summary>
	[Serializable]
	public sealed class TempoMarker : AudioMarker
	{
		#region PublicFields

		public float Bpm = 120f;
		public int BeatsPerBar = 4;

		/// <summary>Note value of a beat (4 = quarter notes).</summary>
		public int BeatUnit = 4;

		#endregion

		#region PublicMethods

		public override string Kind => "Tempo";

		public double SecondsPerBeat => 60.0 / Math.Max(1f, Bpm);

		public override AudioMarker Clone()
		{
			return new TempoMarker { Id = Id, Name = Name, Position = Position, Bpm = Bpm, BeatsPerBar = BeatsPerBar, BeatUnit = BeatUnit };
		}

		#endregion
	}

	/// <summary>The cursor stops here (sounds keep playing) until the game calls KeyOff on the instance.</summary>
	[Serializable]
	public sealed class SustainPoint : AudioMarker
	{
		#region PublicMethods

		public override string Kind => "Sustain Point";

		public override AudioMarker Clone()
		{
			return new SustainPoint { Id = Id, Name = Name, Position = Position };
		}

		#endregion
	}

	/// <summary>3D placement of an event: distance attenuation and stereo panning from the listener's point of view.</summary>
	[Serializable]
	public sealed class AudioSpatializer
	{
		#region PublicFields

		public bool Enabled;

		/// <summary>Distance up to which the event plays at full volume.</summary>
		public float MinDistance = 1f;

		/// <summary>Distance at which the event is silent.</summary>
		public float MaxDistance = 25f;

		public RolloffMode Rolloff = RolloffMode.Inverse;

		/// <summary>Angular size of the source, degrees: 0 a point panned hard, 360 fills both speakers wherever it is.</summary>
		public float Spread;

		/// <summary>
		///     Doppler pitch shift from the velocities of the instance and the listener: 0 off, 1 physical (with the
		///     project's speed of sound), more exaggerates.
		/// </summary>
		public float DopplerScale;

		/// <summary>Volume (dB) over the normalized distance 0..1, for <see cref="RolloffMode.Custom" />.</summary>
		public List<AudioCurvePoint> CustomCurve = new List<AudioCurvePoint>();

		#endregion

		#region PublicMethods

		/// <summary>Gain (0..1) at a distance.</summary>
		public float Attenuation(float distance)
		{
			float min = Math.Max(0.001f, MinDistance);
			float max = Math.Max(min + 0.001f, MaxDistance);
			if (distance <= min)
			{
				return 1f;
			}

			if (distance >= max)
			{
				return 0f;
			}

			float t = (distance - min) / (max - min);
			switch (Rolloff)
			{
				case RolloffMode.Linear:
					return 1f - t;
				case RolloffMode.LinearSquared:
					return (1f - t) * (1f - t);
				case RolloffMode.Inverse:
					return min / distance;
				case RolloffMode.InverseTapered:
					return min / distance * (1f - t);
				case RolloffMode.Custom:
					return CustomCurve.Count == 0 ? 1f - t : AudioMath.DbToGain(AudioAutomationCurve.Evaluate(CustomCurve, t));
				default:
					return 1f - t;
			}
		}

		public AudioSpatializer Clone()
		{
			var copy = new AudioSpatializer { Enabled = Enabled, MinDistance = MinDistance, MaxDistance = MaxDistance, Rolloff = Rolloff, Spread = Spread, DopplerScale = DopplerScale };
			foreach (AudioCurvePoint point in CustomCurve)
			{
				copy.CustomCurve.Add(new AudioCurvePoint { X = point.X, Y = point.Y, Tension = point.Tension });
			}

			return copy;
		}

		#endregion
	}

	/// <summary>A track of an event: a lane of instruments mixed through its own volume, pan, effects and automation.</summary>
	[Serializable]
	public sealed class AudioTrack
	{
		#region PublicFields

		public string Id;
		public string Name = "Audio";
		public float VolumeDb;

		/// <summary>Stereo balance -1 (left) .. 1 (right), before spatialization.</summary>
		public float Pan;

		public float PitchSemitones;
		public bool Mute;

		/// <summary>Auditioning in the editor: while any track of the event is soloed, the others are silent. Bank files for builds drop it.</summary>
		public bool Solo;

		public Color4 Color = new Color4(0.35f, 0.55f, 0.8f, 1f);
		public List<AudioEffect> Effects = new List<AudioEffect>();
		public List<AudioAutomationCurve> Automation = new List<AudioAutomationCurve>();
		public List<AudioInstrument> Instruments = new List<AudioInstrument>();

		#endregion

		#region PublicMethods

		public AudioAutomationCurve FindAutomation(string target)
		{
			foreach (AudioAutomationCurve curve in Automation)
			{
				if (curve.Target == target)
				{
					return curve;
				}
			}

			return null;
		}

		public AudioEffect FindEffect(string id)
		{
			foreach (AudioEffect effect in Effects)
			{
				if (effect.Id == id)
				{
					return effect;
				}
			}

			return null;
		}

		#endregion
	}

	/// <summary>
	///     An event: what the game triggers. Tracks of instruments on a timeline (and on parameter sheets), logic
	///     markers, parameters, a master track with the event's volume, pitch, effects and automation, spatialization
	///     and polyphony rules. Referenced by GUID (<see cref="Id" />); the path is its name in folders.
	/// </summary>
	[Serializable]
	public sealed class AudioEvent
	{
		#region PublicFields

		public string Id;

		/// <summary>"Folder/Subfolder/Name".</summary>
		public string Path = "New Event";

		/// <summary>The bank the event ships in (<see cref="AudioProject.MasterBankId" /> by default).</summary>
		public string BankId = AudioProject.MasterBankId;

		/// <summary>The bus the event's master track feeds (<see cref="AudioProject.MasterBusId" /> by default).</summary>
		public string BusId = AudioProject.MasterBusId;

		public List<AudioParameter> Parameters = new List<AudioParameter>();
		public List<AudioTrack> Tracks = new List<AudioTrack>();

		/// <summary>The event's own volume, pitch, effects and automation (its Instruments list is unused).</summary>
		public AudioTrack Master = new AudioTrack { Id = "master", Name = "Master", Color = new Color4(0.6f, 0.6f, 0.62f, 1f) };

		public List<AudioMarker> Markers = new List<AudioMarker>();
		public AudioSpatializer Spatializer = new AudioSpatializer();

		/// <summary>Fade in when the instance starts, fade out when it stops with <see cref="AudioStopMode.AllowFadeout" />.</summary>
		public AudioEnvelope Envelope = new AudioEnvelope();

		/// <summary>Instances of the event playing at once at most; 0 = unlimited.</summary>
		public int MaxInstances;

		public StealingMode Stealing = StealingMode.Oldest;

		/// <summary>0 (most important) .. 256: which voices stay audible when there are too many.</summary>
		public int Priority = 128;

		/// <summary>Keeps playing after the timeline passed its last instrument (the game stops it).</summary>
		public bool Persistent;

		public string Notes = "";

		#endregion

		#region PublicMethods

		public string Name => Path == null ? "" : Path.Substring(Path.LastIndexOf('/') + 1);

		public string Folder
		{
			get
			{
				int slash = Path?.LastIndexOf('/') ?? -1;
				return slash > 0 ? Path.Substring(0, slash) : "";
			}
		}

		/// <summary>Length of the timeline content: the end of its last timeline instrument or marker.</summary>
		public double TimelineLength
		{
			get
			{
				double length = 0.0;
				foreach (AudioTrack track in Tracks)
				{
					foreach (AudioInstrument instrument in track.Instruments)
					{
						if (instrument.OnTimeline)
						{
							length = Math.Max(length, instrument.End);
						}
					}
				}

				foreach (AudioMarker marker in Markers)
				{
					length = Math.Max(length, marker.End);
				}

				return length;
			}
		}

		public AudioTrack FindTrack(string id)
		{
			if (id == Master.Id)
			{
				return Master;
			}

			foreach (AudioTrack track in Tracks)
			{
				if (track.Id == id)
				{
					return track;
				}
			}

			return null;
		}

		public AudioInstrument FindInstrument(string id, out AudioTrack owner)
		{
			foreach (AudioTrack track in Tracks)
			{
				foreach (AudioInstrument instrument in track.Instruments)
				{
					if (instrument.Id == id)
					{
						owner = track;
						return instrument;
					}
				}
			}

			owner = null;
			return null;
		}

		public AudioMarker FindMarker(string id)
		{
			foreach (AudioMarker marker in Markers)
			{
				if (marker.Id == id)
				{
					return marker;
				}
			}

			return null;
		}

		public AudioParameter FindLocalParameter(string id)
		{
			foreach (AudioParameter parameter in Parameters)
			{
				if (parameter.Id == id)
				{
					return parameter;
				}
			}

			return null;
		}

		/// <summary>Every sound the event uses (for banks).</summary>
		public IEnumerable<AudioClipReference> Clips
		{
			get
			{
				foreach (AudioTrack track in Tracks)
				{
					foreach (AudioInstrument instrument in track.Instruments)
					{
						foreach (AudioClipReference clip in instrument.Clips)
						{
							yield return clip;
						}
					}
				}
			}
		}

		/// <summary>Tempo markers in timeline order.</summary>
		public List<TempoMarker> TempoMarkers
		{
			get
			{
				var result = new List<TempoMarker>();
				foreach (AudioMarker marker in Markers)
				{
					if (marker is TempoMarker tempo)
					{
						result.Add(tempo);
					}
				}

				result.Sort((a, b) => a.Position.CompareTo(b.Position));
				return result;
			}
		}

		public override string ToString()
		{
			return Path;
		}

		#endregion
	}

	/// <summary>A mixer bus: events route into buses, buses into their parent, the master bus into the output.</summary>
	[Serializable]
	public sealed class AudioBus
	{
		#region PublicFields

		public string Id;
		public string Name = "Bus";

		/// <summary>Null for the master bus.</summary>
		public string ParentId;

		public float VolumeDb;
		public bool Mute;
		public List<AudioEffect> Effects = new List<AudioEffect>();

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return Name;
		}

		#endregion
	}

	/// <summary>A bank: a set of events shipped and loaded together.</summary>
	[Serializable]
	public sealed class AudioBank
	{
		#region PublicFields

		public string Id;
		public string Name = "Bank";

		/// <summary>Loaded when the audio system starts (the master bank always is).</summary>
		public bool AutoLoad = true;

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return Name;
		}

		#endregion
	}

	/// <summary>Beat grid arithmetic over an event's tempo markers (all positions in seconds).</summary>
	public sealed class BeatGrid
	{
		#region PrivateFields

		private readonly List<TempoMarker> markers;

		#endregion

		#region Constructors

		/// <param name="tempoMarkers">Sorted by position. Empty: a default 120 bpm, 4/4 grid from zero.</param>
		public BeatGrid(List<TempoMarker> tempoMarkers)
		{
			markers = tempoMarkers != null && tempoMarkers.Count > 0 ? tempoMarkers : new List<TempoMarker> { new TempoMarker { Position = 0, Bpm = 120f, BeatsPerBar = 4 } };
		}

		#endregion

		#region PublicMethods

		public bool HasTempo => markers.Count > 0;

		/// <summary>The tempo marker in force at a time.</summary>
		public TempoMarker At(double time)
		{
			TempoMarker current = markers[0];
			foreach (TempoMarker marker in markers)
			{
				if (marker.Position <= time + 1e-9)
				{
					current = marker;
				}
				else
				{
					break;
				}
			}

			return current;
		}

		/// <summary>Length of a quantization interval in seconds at a time.</summary>
		public double IntervalSeconds(QuantizationInterval interval, double time)
		{
			TempoMarker tempo = At(time);
			double beat = tempo.SecondsPerBeat;
			double bar = beat * Math.Max(1, tempo.BeatsPerBar);
			switch (interval)
			{
				case QuantizationInterval.QuarterBeat: return beat * 0.25;
				case QuantizationInterval.HalfBeat: return beat * 0.5;
				case QuantizationInterval.Beat: return beat;
				case QuantizationInterval.TwoBeats: return beat * 2.0;
				case QuantizationInterval.Bar: return bar;
				case QuantizationInterval.TwoBars: return bar * 2.0;
				case QuantizationInterval.FourBars: return bar * 4.0;
				case QuantizationInterval.EightBars: return bar * 8.0;
				default: return 0.0;
			}
		}

		/// <summary>
		///     The first grid point at or after <paramref name="time" /> for the interval, the grid anchored at the tempo
		///     marker in force. A point less than <paramref name="tolerance" /> seconds before the time counts as the time.
		/// </summary>
		public double NextGridPoint(QuantizationInterval interval, double time, double tolerance = 0.0005)
		{
			double step = IntervalSeconds(interval, time);
			if (step <= 0.0)
			{
				return time;
			}

			TempoMarker tempo = At(time);
			double offset = time - tempo.Position;
			double steps = Math.Ceiling((offset - tolerance) / step);
			double point = tempo.Position + Math.Max(0.0, steps) * step;
			// A tempo change before the point: the grid restarts there.
			foreach (TempoMarker marker in markers)
			{
				if (marker.Position > time + 1e-9 && marker.Position < point)
				{
					return marker.Position;
				}
			}

			return point;
		}

		/// <summary>Bar and beat numbers (1-based) at a time, and the fraction of the beat elapsed.</summary>
		public void BarBeat(double time, out int bar, out int beat, out double fraction)
		{
			TempoMarker tempo = At(time);
			int beatsPerBar = Math.Max(1, tempo.BeatsPerBar);
			double beats = Math.Max(0.0, time - tempo.Position) / tempo.SecondsPerBeat;
			int wholeBeats = (int)Math.Floor(beats + 1e-9);
			// Bars count on from the markers before this one.
			int barsBefore = 0;
			for (int i = 0; i < markers.Count && markers[i] != tempo; i++)
			{
				TempoMarker previous = markers[i];
				double span = markers[i + 1].Position - previous.Position;
				barsBefore += (int)Math.Floor(span / (previous.SecondsPerBeat * Math.Max(1, previous.BeatsPerBar)) + 1e-9);
			}

			bar = barsBefore + wholeBeats / beatsPerBar + 1;
			beat = wholeBeats % beatsPerBar + 1;
			fraction = beats - wholeBeats;
		}

		/// <summary>Snaps a time to the nearest grid point of the interval.</summary>
		public double Snap(QuantizationInterval interval, double time)
		{
			double step = IntervalSeconds(interval, time);
			if (step <= 0.0)
			{
				return time;
			}

			TempoMarker tempo = At(time);
			return tempo.Position + Math.Round((time - tempo.Position) / step) * step;
		}

		#endregion
	}
}
