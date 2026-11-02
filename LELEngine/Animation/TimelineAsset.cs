using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Audio;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>
	///     An animation clip placed on a channel, as an audio editor places a sound on a track: its region on the timeline,
	///     how far into the clip the region starts (a trim), the fades at its ends (towards what the property holds
	///     without the clip) and the conditions that decide, when the cursor enters the region, whether it plays.
	/// </summary>
	[Serializable]
	public sealed class TimelineClip
	{
		#region PublicFields

		public string id;
		public AnimationClip clip;

		/// <summary>Start of the region on the timeline, seconds.</summary>
		public double start;

		/// <summary>Length of the region on the timeline, seconds.</summary>
		public double length = 1.0;

		/// <summary>Seconds into the clip at the region's start.</summary>
		public double startOffset;

		/// <summary>Clip seconds per timeline second.</summary>
		public float speed = 1f;

		/// <summary>The clip repeats inside the region (else its last frame holds until the region ends).</summary>
		public bool loop;

		public AudioFade fadeIn = new AudioFade();
		public AudioFade fadeOut = new AudioFade();

		/// <summary>Every condition must hold when the cursor enters the region for the clip to play.</summary>
		public List<AudioCondition> conditions = new List<AudioCondition>();

		public double End => start + length;

		public string DisplayName => clip != null ? clip.name : "(no clip)";

		#endregion

		#region PublicMethods

		/// <summary>Seconds into the clip at a time of the timeline (wrapped when looping, held at the end otherwise).</summary>
		public double ClipTimeAt(double time)
		{
			return WrapClipTime(startOffset + (time - start) * Math.Max(speed, 1e-4f));
		}

		/// <summary>A position in the clip reduced to the time sampled: wrapped when looping, held at the clip's end otherwise.</summary>
		public double WrapClipTime(double position)
		{
			double clipLength = clip?.length ?? 0f;
			if (clipLength <= 1e-6)
			{
				return 0.0;
			}

			if (!loop)
			{
				return Math.Clamp(position, 0.0, clipLength);
			}

			double wrapped = position % clipLength;
			return wrapped < 0.0 ? wrapped + clipLength : wrapped;
		}

		/// <summary>The time of the timeline where a position in the clip plays (the first pass of a loop).</summary>
		public double TimelineTimeOf(double clipTime)
		{
			return start + (clipTime - startOffset) / Math.Max(speed, 1e-4f);
		}

		#endregion
	}

	/// <summary>
	///     One channel of a timeline: the clips of one object of the scene. The object is not stored here (an asset
	///     knows no scene): the <see cref="Timeline" /> component playing the asset binds channels to objects. Channels
	///     apply in list order, so a later channel on the same object and property wins.
	/// </summary>
	[Serializable]
	public sealed class TimelineChannel
	{
		#region PublicFields

		public string id;

		/// <summary>Name of the object it was last bound to (shown while it is not bound).</summary>
		public string name = "Channel";

		public Color4 color = new Color4(0.37f, 0.6f, 0.9f, 1f);

		/// <summary>The channel writes nothing.</summary>
		public bool mute;

		/// <summary>Auditioning in the editor: while any channel is soloed the others write nothing. Player builds ignore it.</summary>
		public bool solo;

		public List<TimelineClip> clips = new List<TimelineClip>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     A clip's fades with the overlap of its neighbours included: two clips that partly overlap crossfade over the
		///     overlap, as Unity's Timeline blends them (a clip inside another is not touched). The crossfade is between the two:
		///     a property only the first has keys for keeps its last key, one only the second has fades in from what it holds.
		/// </summary>
		public void GetFades(TimelineClip clip, out double fadeIn, out double fadeOut)
		{
			fadeIn = clip.fadeIn.Length;
			fadeOut = clip.fadeOut.Length;
			foreach (TimelineClip other in clips)
			{
				if (other == clip)
				{
					continue;
				}

				if (other.start > clip.start && other.start < clip.End && other.End > clip.End)
				{
					fadeOut = Math.Max(fadeOut, clip.End - other.start);
				}

				if (other.End > clip.start && other.End < clip.End && other.start < clip.start)
				{
					fadeIn = Math.Max(fadeIn, other.End - clip.start);
				}
			}
		}

		public override string ToString()
		{
			return name;
		}

		#endregion
	}

	/// <summary>
	///     A timeline asset (".timeline": readable JSON in the project, binary in builds): channels of animation clips, one
	///     channel per animated object, on a shared time line with the logic of an audio event: parameters the game sets,
	///     loop regions, transition markers and regions with conditions and a crossfade, named destination markers,
	///     sustain points and tempo markers (the marker classes are the audio model's, which keeps no sound in them).
	///     Played by a <see cref="Timeline" /> component, which binds the channels to objects of its scene. During playback
	///     only the properties the clips have keys for are written, everything else stays with its owner.
	/// </summary>
	public sealed class TimelineAsset : IAsset, ISerializationCallbackReceiver
	{
		#region PublicFields

		public const string Extension = ".timeline";

		public string AssetPath { get; internal set; }
		public string SubAssetName => null;

		public string name = "";

		/// <summary>Frames per second the editor snaps to and recordings are keyed at.</summary>
		public float frameRate = 30f;

		public List<AudioParameter> parameters = new List<AudioParameter>();
		public List<TimelineChannel> channels = new List<TimelineChannel>();
		public List<AudioMarker> markers = new List<AudioMarker>();

		public int FormatVersion = 1;

		/// <summary>Bumped by every edit (<see cref="MarkChanged" />); players rebuild when it changes.</summary>
		[NonSerialized] public int Version = 1;

		/// <summary>Raised by <see cref="MarkChanged" />.</summary>
		public event Action Changed;

		/// <summary>Length of the content in seconds: the end of the last clip or marker.</summary>
		public double Duration
		{
			get
			{
				double duration = 0.0;
				foreach (TimelineChannel channel in channels)
				{
					foreach (TimelineClip clip in channel.clips)
					{
						duration = Math.Max(duration, clip.End);
					}
				}

				foreach (AudioMarker marker in markers)
				{
					duration = Math.Max(duration, marker.End);
				}

				return duration;
			}
		}

		/// <summary>Tempo markers in timeline order.</summary>
		public List<TempoMarker> TempoMarkers
		{
			get
			{
				var result = new List<TempoMarker>();
				foreach (AudioMarker marker in markers)
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

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, TimelineAsset> cache = new Dictionary<string, TimelineAsset>(StringComparer.OrdinalIgnoreCase);
		private static readonly AssetFileStamps stamps = new AssetFileStamps();

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- files

		/// <summary>Loads (or returns the cached) timeline at a root-relative path. Unreadable files give an empty timeline.</summary>
		public static TimelineAsset Load(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			lock (cache)
			{
				if (cache.TryGetValue(assetPath, out TimelineAsset cached))
				{
					return cached;
				}
			}

			TimelineAsset asset = null;
			try
			{
				stamps.Note(assetPath);
				byte[] data = File.ReadAllBytes(AssetDatabase.ToAbsolute(assetPath));
				if (Documents.IsDocument(data))
				{
					asset = Documents.ReadObject(data, typeof(TimelineAsset), null, new SerializationContext { Location = assetPath }) as TimelineAsset;
				}
				else
				{
					Debug.LogError($"[Timeline] {assetPath} is not a timeline document");
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Timeline] Cannot load {assetPath}: {e.Message}");
			}

			asset = asset ?? new TimelineAsset();
			asset.AssetPath = assetPath.Replace('\\', '/');
			asset.name = Path.GetFileNameWithoutExtension(assetPath);
			lock (cache)
			{
				cache[assetPath] = asset;
			}

			return asset;
		}

		/// <summary>The cached timeline of an asset path when it was loaded (null otherwise).</summary>
		public static TimelineAsset FindLoaded(string assetPath)
		{
			lock (cache)
			{
				return assetPath != null && cache.TryGetValue(assetPath, out TimelineAsset asset) ? asset : null;
			}
		}

		/// <summary>Writes the timeline to its asset file (or a new one) in <see cref="Documents.FileFormat" />.</summary>
		public void Save(string assetPath = null)
		{
			assetPath = assetPath ?? AssetPath;
			if (string.IsNullOrEmpty(assetPath))
			{
				throw new InvalidOperationException("The timeline has no asset path");
			}

			AssetPath = assetPath.Replace('\\', '/');
			name = Path.GetFileNameWithoutExtension(AssetPath);
			Documents.WriteObjectFile(AssetDatabase.ToAbsolute(AssetPath), this);
			stamps.Note(AssetPath);
			AssetDatabase.EnsureMeta(AssetPath);
			lock (cache)
			{
				cache[AssetPath] = this;
			}
		}

		/// <summary>The timeline as a document (undo snapshots).</summary>
		public byte[] ToBytes(DocumentFormat format = DocumentFormat.Binary)
		{
			return Documents.WriteObject(this, format, new SerializationContext { Location = name ?? "TimelineAsset" });
		}

		/// <summary>Replaces the content from a document written by <see cref="ToBytes" /> and marks the timeline changed.</summary>
		public void ApplyBytes(byte[] data)
		{
			parameters.Clear();
			channels.Clear();
			markers.Clear();
			Documents.ReadObject(data, typeof(TimelineAsset), this, new SerializationContext { Location = name ?? "TimelineAsset" });
			MarkChanged();
		}

		/// <summary>Reads again, into the same objects (marked changed), the loaded timelines whose files changed on disk; returns them.</summary>
		public static List<TimelineAsset> ReloadChanged()
		{
			List<KeyValuePair<string, TimelineAsset>> loaded;
			lock (cache)
			{
				loaded = new List<KeyValuePair<string, TimelineAsset>>(cache);
			}

			return stamps.ReloadChanged(loaded, (asset, data) => asset.ApplyBytes(data), "[Timeline]");
		}

		public static void ClearCache()
		{
			lock (cache)
			{
				cache.Clear();
				stamps.Clear();
			}
		}

		/// <summary>A file or folder moved: the loaded timelines keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (cache)
			{
				stamps.Moved(fromPath, toPath);
				AssetDatabase.RekeyMoved(cache, fromPath, toPath, (asset, path) =>
				{
					asset.AssetPath = path;
					asset.name = Path.GetFileNameWithoutExtension(path);
				});
			}
		}

		/// <summary>Drops a cached timeline (its file was deleted or replaced) so the next use reloads it.</summary>
		public static void Forget(string assetPath)
		{
			lock (cache)
			{
				cache.Remove(assetPath);
				stamps.Forget(assetPath);
			}
		}

		// ---------------------------------------------------------------- editing

		/// <summary>Random id for a new channel, clip, marker or parameter.</summary>
		public static string NewId()
		{
			return AudioProject.NewId();
		}

		/// <summary>Call after any edit: repairs the data, bumps <see cref="Version" /> and raises <see cref="Changed" />.</summary>
		public void MarkChanged()
		{
			Validate();
			Version++;
			Changed?.Invoke();
		}

		/// <summary>Repairs the data (fades and regions that do not fit, a frame rate of zero).</summary>
		public void Validate()
		{
			parameters = parameters ?? new List<AudioParameter>();
			channels = channels ?? new List<TimelineChannel>();
			markers = markers ?? new List<AudioMarker>();
			if (frameRate <= 0f)
			{
				frameRate = 30f;
			}

			foreach (TimelineChannel channel in channels)
			{
				channel.clips = channel.clips ?? new List<TimelineClip>();
				foreach (TimelineClip clip in channel.clips)
				{
					clip.length = Math.Max(clip.length, 1e-3);
					clip.start = Math.Max(clip.start, 0.0);
					clip.startOffset = Math.Max(clip.startOffset, 0.0);
					clip.speed = Math.Max(clip.speed, 1e-3f);
					clip.fadeIn = clip.fadeIn ?? new AudioFade();
					clip.fadeOut = clip.fadeOut ?? new AudioFade();
					clip.fadeIn.Length = Math.Clamp(clip.fadeIn.Length, 0.0, clip.length);
					clip.fadeOut.Length = Math.Clamp(clip.fadeOut.Length, 0.0, clip.length);
					clip.conditions = clip.conditions ?? new List<AudioCondition>();
				}
			}
		}

		public TimelineChannel FindChannel(string id)
		{
			foreach (TimelineChannel channel in channels)
			{
				if (channel.id == id)
				{
					return channel;
				}
			}

			return null;
		}

		public TimelineClip FindClip(string id, out TimelineChannel owner)
		{
			foreach (TimelineChannel channel in channels)
			{
				foreach (TimelineClip clip in channel.clips)
				{
					if (clip.id == id)
					{
						owner = channel;
						return clip;
					}
				}
			}

			owner = null;
			return null;
		}

		public AudioMarker FindMarker(string id)
		{
			foreach (AudioMarker marker in markers)
			{
				if (marker.Id == id)
				{
					return marker;
				}
			}

			return null;
		}

		public AudioParameter FindParameter(string id)
		{
			foreach (AudioParameter parameter in parameters)
			{
				if (parameter.Id == id)
				{
					return parameter;
				}
			}

			return null;
		}

		public AudioParameter FindParameterByName(string parameterName)
		{
			foreach (AudioParameter parameter in parameters)
			{
				if (string.Equals(parameter.Name, parameterName, StringComparison.Ordinal))
				{
					return parameter;
				}
			}

			return null;
		}

		/// <summary>Every animation clip the timeline plays (each once).</summary>
		public List<AnimationClip> CollectClips()
		{
			var clips = new List<AnimationClip>();
			foreach (TimelineChannel channel in channels)
			{
				foreach (TimelineClip clip in channel.clips)
				{
					if (clip.clip != null && !clips.Contains(clip.clip))
					{
						clips.Add(clip.clip);
					}
				}
			}

			return clips;
		}

		public void OnBeforeSerialize()
		{ }

		public void OnAfterDeserialize()
		{
			Validate();
		}

		public override string ToString()
		{
			return $"{name} ({Duration:0.###} s, {channels.Count} channels)";
		}

		#endregion
	}
}
