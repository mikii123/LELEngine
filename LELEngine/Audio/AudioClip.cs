using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine.Audio
{
	/// <summary>
	///     An audio file of the project (".wav", ".ogg", ".mp3") as an asset: its format and length are read at once,
	///     the samples are decoded on first use (<see cref="Pcm" />) and kept. The editor plays clips from here; banks
	///     take their data from here when they are built. One instance per asset path (<see cref="Load" />).
	/// </summary>
	public sealed class AudioClip : IAsset
	{
		#region PublicFields

		public static readonly string[] Extensions = AudioDecoder.Extensions;

		public string AssetPath { get; internal set; }
		public string SubAssetName => null;
		public string name;

		public AudioFileFormat Format { get; private set; }
		public int Channels { get; private set; }
		public int SampleRate { get; private set; }

		/// <summary>Frames in the file (-1 when the header does not tell).</summary>
		public long Frames { get; private set; }

		public double Duration => SampleRate > 0 && Frames >= 0 ? (double)Frames / SampleRate : 0.0;

		/// <summary>Why the file could not be read, or null.</summary>
		public string Error { get; private set; }

		/// <summary>The import settings (the meta file's, or the defaults).</summary>
		public AudioImporter Importer => AssetDatabase.GetMeta(AssetPath).importer as AudioImporter ?? new AudioImporter();

		/// <summary>The decoded samples (decoded on first use; null when the file cannot be read).</summary>
		public PcmData Pcm
		{
			get
			{
				PcmData data = pcm;
				if (data != null || Error != null)
				{
					return data;
				}

				lock (sync)
				{
					if (pcm == null && Error == null)
					{
						try
						{
							PcmData decoded = AudioDecoder.DecodeFile(AssetDatabase.ToAbsolute(AssetPath), Importer.forceMono);
							Channels = decoded.Channels;
							SampleRate = decoded.SampleRate;
							Frames = decoded.Frames;
							pcm = decoded;
						}
						catch (Exception e)
						{
							Error = e.Message;
							Debug.LogError($"[Audio] Cannot decode {AssetPath}: {e.Message}");
						}
					}

					return pcm;
				}
			}
		}

		/// <summary>True once the samples were decoded.</summary>
		public bool IsDecoded => pcm != null;

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, DateTime> fileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		private readonly object sync = new object();
		private PcmData pcm;
		private float[] peaks;
		private int peakBuckets;

		#endregion

		#region Constructors

		private AudioClip(string assetPath)
		{
			AssetPath = assetPath;
			name = Path.GetFileNameWithoutExtension(assetPath);
			ReadInfo();
		}

		#endregion

		#region PublicMethods

		public static bool IsAudioFile(string path)
		{
			return AudioDecoder.IsAudioFile(path);
		}

		/// <summary>The clip of a root-relative asset path (created on first use), null for an empty path.</summary>
		public static AudioClip Load(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			assetPath = assetPath.Replace('\\', '/');
			lock (cache)
			{
				AudioClip clip;
				if (!cache.TryGetValue(assetPath, out clip))
				{
					clip = new AudioClip(assetPath);
					cache[assetPath] = clip;
					fileTimes[assetPath] = SafeWriteTime(AssetDatabase.ToAbsolute(assetPath));
				}

				return clip;
			}
		}

		/// <summary>The clip a reference names (null when the file is missing).</summary>
		public static AudioClip Load(AudioClipReference reference)
		{
			string path = reference?.Resolve();
			return path != null ? Load(path) : null;
		}

		public static void ClearCache()
		{
			lock (cache)
			{
				cache.Clear();
				fileTimes.Clear();
			}
		}

		/// <summary>A file or folder moved: the loaded clips keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (cache)
			{
				AssetDatabase.RekeyMoved(cache, fromPath, toPath, (clip, path) => clip.AssetPath = path);
				AssetDatabase.RekeyMoved(fileTimes, fromPath, toPath);
			}
		}

		/// <summary>Clips whose files changed on disk read them again on next use; returns their asset paths.</summary>
		public static List<string> ReloadChanged()
		{
			var changed = new List<string>();
			lock (cache)
			{
				foreach (KeyValuePair<string, AudioClip> entry in cache)
				{
					string file = AssetDatabase.ToAbsolute(entry.Key);
					DateTime time = SafeWriteTime(file);
					DateTime known;
					if (fileTimes.TryGetValue(entry.Key, out known) && known == time)
					{
						continue;
					}

					fileTimes[entry.Key] = time;
					entry.Value.Reset();
					changed.Add(entry.Key);
				}
			}

			return changed;
		}

		/// <summary>Drops the decoded samples (import settings changed); the header is read again.</summary>
		public void Reset()
		{
			lock (sync)
			{
				pcm = null;
				peaks = null;
				peakBuckets = 0;
				Error = null;
				ReadInfo();
			}
		}

		/// <summary>
		///     Waveform overview for the editor: <paramref name="buckets" /> pairs of (min, max) over the clip (channels
		///     mixed), cached per bucket count. Null until the clip is decoded.
		/// </summary>
		public float[] GetPeaks(int buckets)
		{
			PcmData data = Pcm;
			if (data == null || buckets <= 0)
			{
				return null;
			}

			lock (sync)
			{
				if (peaks != null && peakBuckets == buckets)
				{
					return peaks;
				}

				var result = new float[buckets * 2];
				int frames = data.Frames;
				int channels = data.Channels;
				for (int bucket = 0; bucket < buckets; bucket++)
				{
					int start = (int)((long)bucket * frames / buckets);
					int end = Math.Max(start + 1, (int)((long)(bucket + 1) * frames / buckets));
					float min = 0f;
					float max = 0f;
					for (int frame = start; frame < end && frame < frames; frame++)
					{
						float value = 0f;
						for (int channel = 0; channel < channels; channel++)
						{
							value += data.Samples[frame * channels + channel];
						}

						value /= channels;
						if (value < min) min = value;
						if (value > max) max = value;
					}

					result[bucket * 2] = min;
					result[bucket * 2 + 1] = max;
				}

				peaks = result;
				peakBuckets = buckets;
				return result;
			}
		}

		public override string ToString()
		{
			return name;
		}

		#endregion

		#region PrivateMethods

		private void ReadInfo()
		{
			string file = AssetDatabase.ToAbsolute(AssetPath);
			if (!File.Exists(file))
			{
				Error = "file not found";
				return;
			}

			try
			{
				AudioFileFormat format;
				int channels;
				int sampleRate;
				long frames;
				if (AudioDecoder.TryReadInfo(file, out format, out channels, out sampleRate, out frames))
				{
					Format = format;
					Channels = Importer.forceMono ? 1 : Math.Min(2, channels);
					SampleRate = sampleRate;
					Frames = frames;
				}
				else
				{
					Error = "unreadable audio file";
				}
			}
			catch (Exception e)
			{
				Error = e.Message;
			}
		}

		private static DateTime SafeWriteTime(string file)
		{
			try
			{
				return File.GetLastWriteTimeUtc(file);
			}
			catch (Exception)
			{
				return DateTime.MinValue;
			}
		}

		#endregion
	}
}
