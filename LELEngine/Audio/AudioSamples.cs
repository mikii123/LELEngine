using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LELEngine.Audio
{
	/// <summary>How a sound's data is stored in a bank.</summary>
	public enum SampleEncoding : byte
	{
		/// <summary>Signed 16-bit little-endian interleaved PCM.</summary>
		Pcm16 = 0,

		/// <summary>32-bit float interleaved PCM.</summary>
		Float32 = 1,

		/// <summary>The encoded file as it is (wav, ogg or mp3 container), decoded when the bank loads or while streaming.</summary>
		EncodedFile = 2
	}

	/// <summary>
	///     One sound's data as the mixer reads it: PCM in memory (a slice of the bank's memory, or a float array of a
	///     decoded clip) or a stream opened on demand. Immutable once created, read by the audio thread.
	/// </summary>
	public sealed class SampleData
	{
		#region PublicFields

		/// <summary>Where the sound came from (asset path), for messages.</summary>
		public string Name;

		public int Channels;
		public int SampleRate;
		public long Frames;
		public SampleEncoding Encoding;

		/// <summary>Decoded while playing (<see cref="OpenStream" />) instead of held in memory.</summary>
		public bool Streaming;

		/// <summary>In-memory PCM16 (<see cref="Encoding" /> Pcm16): the bytes, from <see cref="Offset" />.</summary>
		public byte[] Memory;

		public int Offset;

		/// <summary>In-memory float PCM (<see cref="Encoding" /> Float32).</summary>
		public float[] Float;

		/// <summary>Opens a fresh decoder positioned at the sound's start (streaming sounds).</summary>
		public Func<IAudioStreamDecoder> OpenStream;

		/// <summary>The format of a streamed encoded file.</summary>
		public AudioFileFormat FileFormat;

		#endregion

		#region PublicMethods

		public double Duration => SampleRate > 0 ? Frames / (double)SampleRate : 0.0;

		/// <summary>Sample data over decoded floats (clips in the editor, decoded compressed bank sounds).</summary>
		public static SampleData FromPcm(string name, PcmData pcm)
		{
			return new SampleData { Name = name, Channels = pcm.Channels, SampleRate = pcm.SampleRate, Frames = pcm.Frames, Encoding = SampleEncoding.Float32, Float = pcm.Samples };
		}

		/// <summary>
		///     Copies frames [<paramref name="start" />, start + count) as interleaved stereo floats into
		///     <paramref name="destination" /> (mono duplicated). Frames outside [0, Frames) read as silence unless
		///     <paramref name="loop" />, which wraps them (in-memory data only; a stream's reader handles its own loop).
		/// </summary>
		public void Fetch(long start, int count, float[] destination, bool loop)
		{
			if (Frames <= 0)
			{
				Array.Clear(destination, 0, count * 2);
				return;
			}

			for (int i = 0; i < count; i++)
			{
				long frame = start + i;
				if (loop)
				{
					frame %= Frames;
					if (frame < 0)
					{
						frame += Frames;
					}
				}
				else if (frame < 0 || frame >= Frames)
				{
					destination[i * 2] = 0f;
					destination[i * 2 + 1] = 0f;
					continue;
				}

				float left;
				float right;
				if (Encoding == SampleEncoding.Float32)
				{
					int index = (int)(frame * Channels);
					left = Float[index];
					right = Channels > 1 ? Float[index + 1] : left;
				}
				else
				{
					int index = Offset + (int)(frame * Channels * 2);
					left = BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(Memory, index, 2)) * (1f / 32768f);
					right = Channels > 1 ? BinaryPrimitives.ReadInt16LittleEndian(new ReadOnlySpan<byte>(Memory, index + 2, 2)) * (1f / 32768f) : left;
				}

				destination[i * 2] = left;
				destination[i * 2 + 1] = right;
			}
		}

		#endregion
	}

	/// <summary>
	///     A streamed sound for one voice: a ring buffer the streaming thread fills from the decoder ahead of the
	///     mixer's reads. Single producer (the streaming thread), single consumer (the audio thread). The mixer reads
	///     any frame inside the buffered window; <see cref="Consume" /> releases frames it will not need again.
	/// </summary>
	public sealed class StreamingReader : IDisposable
	{
		#region PublicFields

		public readonly SampleData Sample;

		/// <summary>Loop the whole sound (the decoder wraps at the end).</summary>
		public volatile bool Loop;

		/// <summary>Frames the mixer asked for that were not buffered yet.</summary>
		public int Underruns;

		/// <summary>The decoder reached the end and does not loop: no more data will come.</summary>
		public bool EndReached => endReached;

		/// <summary>True when the decoder could not be opened (the voice plays silence).</summary>
		public bool Failed => failed;

		#endregion

		#region PrivateFields

		private const int HistoryFrames = 8;
		private readonly float[] ring;
		private readonly int capacityFrames;
		private readonly int channels;
		private IAudioStreamDecoder decoder;
		private long writeFrame;
		private long readFrame;
		private long seekRequest = -1;
		private volatile bool endReached;
		private volatile bool failed;
		private volatile bool disposed;
		private readonly float[] decodeBuffer;

		#endregion

		#region Constructors

		/// <param name="startFrame">The frame the voice starts at.</param>
		/// <param name="bufferSeconds">Frames held ahead of the reads.</param>
		public StreamingReader(SampleData sample, long startFrame, bool loop, double bufferSeconds = 1.0)
		{
			Sample = sample;
			channels = 2;
			capacityFrames = Math.Max(4096, (int)(sample.SampleRate * bufferSeconds));
			ring = new float[capacityFrames * channels];
			decodeBuffer = new float[4096 * Math.Max(1, sample.Channels)];
			Loop = loop;
			writeFrame = startFrame;
			readFrame = startFrame;
		}

		#endregion

		#region PublicMethods

		/// <summary>First frame still buffered.</summary>
		public long BufferedStart => Volatile.Read(ref readFrame);

		/// <summary>One past the last frame buffered.</summary>
		public long BufferedEnd => Volatile.Read(ref writeFrame);

		/// <summary>
		///     Audio thread: copies frames [<paramref name="start" />, start + count) as stereo floats; frames not buffered
		///     yet read as silence (counted as underruns), frames past the end as silence.
		/// </summary>
		public void Fetch(long start, int count, float[] destination)
		{
			long bufferedStart = BufferedStart;
			long bufferedEnd = BufferedEnd;
			bool underrun = false;
			long total = Sample.Frames;
			for (int i = 0; i < count; i++)
			{
				long frame = start + i;
				if (!Loop && total > 0 && frame >= total)
				{
					destination[i * 2] = 0f;
					destination[i * 2 + 1] = 0f;
					continue;
				}

				if (frame < bufferedStart || frame >= bufferedEnd)
				{
					destination[i * 2] = 0f;
					destination[i * 2 + 1] = 0f;
					if (frame >= 0 && !endReached)
					{
						underrun = true;
					}

					continue;
				}

				int slot = (int)(frame % capacityFrames) * channels;
				destination[i * 2] = ring[slot];
				destination[i * 2 + 1] = ring[slot + 1];
			}

			if (underrun)
			{
				Underruns++;
			}
		}

		/// <summary>Audio thread: frames before <paramref name="frame" /> (less a little history) may be overwritten.</summary>
		public void Consume(long frame)
		{
			long release = frame - HistoryFrames;
			if (release > Volatile.Read(ref readFrame))
			{
				Volatile.Write(ref readFrame, release);
			}
		}

		/// <summary>Audio thread: the voice jumped; the buffer refills from the new frame (reads meanwhile are silence).</summary>
		public void Seek(long frame)
		{
			Volatile.Write(ref seekRequest, Math.Max(0, frame));
			AudioStreamer.Wake();
		}

		/// <summary>Streaming thread: decodes ahead while there is room; false when nothing more will come.</summary>
		public bool Service()
		{
			if (disposed || failed)
			{
				return false;
			}

			try
			{
				if (decoder == null)
				{
					decoder = Sample.OpenStream?.Invoke();
					if (decoder == null)
					{
						failed = true;
						return false;
					}

					if (writeFrame > 0)
					{
						decoder.Seek(writeFrame % Math.Max(1, Sample.Frames));
					}
				}

				long seek = Interlocked.Exchange(ref seekRequest, -1);
				if (seek >= 0)
				{
					long target = Sample.Frames > 0 && Loop ? seek % Sample.Frames : seek;
					decoder.Seek(Math.Min(target, Math.Max(0, Sample.Frames)));
					// The window restarts at the new position.
					Volatile.Write(ref readFrame, seek);
					Volatile.Write(ref writeFrame, seek);
					endReached = false;
				}

				while (!disposed)
				{
					long start = Volatile.Read(ref readFrame);
					long end = Volatile.Read(ref writeFrame);
					int room = capacityFrames - (int)(end - start);
					if (room < 512)
					{
						return true;
					}

					int want = Math.Min(room, decodeBuffer.Length / Math.Max(1, Sample.Channels));
					int read = decoder.Read(decodeBuffer, 0, want);
					if (read <= 0)
					{
						if (Loop && Sample.Frames > 0)
						{
							decoder.Seek(0);
							read = decoder.Read(decodeBuffer, 0, want);
							if (read <= 0)
							{
								endReached = true;
								return false;
							}
						}
						else
						{
							endReached = true;
							return false;
						}
					}

					int sourceChannels = Sample.Channels;
					for (int i = 0; i < read; i++)
					{
						int slot = (int)((end + i) % capacityFrames) * channels;
						float left = decodeBuffer[i * sourceChannels];
						ring[slot] = left;
						ring[slot + 1] = sourceChannels > 1 ? decodeBuffer[i * sourceChannels + 1] : left;
					}

					Volatile.Write(ref writeFrame, end + read);
				}

				return true;
			}
			catch (Exception e)
			{
				failed = true;
				Debug.LogWarning("[Audio] Streaming " + Sample.Name + " failed: " + e.Message);
				return false;
			}
		}

		public void Dispose()
		{
			disposed = true;
			AudioStreamer.Unregister(this);
			try
			{
				decoder?.Dispose();
			}
			catch (Exception)
			{
				// Ignore.
			}

			decoder = null;
		}

		#endregion
	}

	/// <summary>The streaming thread: keeps every active <see cref="StreamingReader" /> filled ahead of the mixer.</summary>
	public static class AudioStreamer
	{
		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly List<StreamingReader> readers = new List<StreamingReader>();
		private static readonly AutoResetEvent wake = new AutoResetEvent(false);
		private static Thread thread;
		private static volatile bool stop;

		#endregion

		#region PublicMethods

		/// <summary>Readers being serviced.</summary>
		public static int ActiveCount
		{
			get
			{
				lock (sync)
				{
					return readers.Count;
				}
			}
		}

		/// <summary>Adds a reader (any thread) and fills it as soon as possible.</summary>
		public static void Register(StreamingReader reader)
		{
			lock (sync)
			{
				if (!readers.Contains(reader))
				{
					readers.Add(reader);
				}

				if (thread == null)
				{
					stop = false;
					thread = new Thread(Run) { Name = "Audio streaming", IsBackground = true, Priority = ThreadPriority.AboveNormal };
					thread.Start();
				}
			}

			wake.Set();
		}

		public static void Unregister(StreamingReader reader)
		{
			lock (sync)
			{
				readers.Remove(reader);
			}
		}

		/// <summary>Services the readers now (a seek is pending).</summary>
		public static void Wake()
		{
			wake.Set();
		}

		/// <summary>Stops the thread (the readers stay registered for a restart).</summary>
		public static void Shutdown()
		{
			Thread running;
			lock (sync)
			{
				running = thread;
				thread = null;
				stop = true;
			}

			wake.Set();
			running?.Join(1000);
		}

		#endregion

		#region PrivateMethods

		private static void Run()
		{
			var snapshot = new List<StreamingReader>();
			while (!stop)
			{
				snapshot.Clear();
				lock (sync)
				{
					snapshot.AddRange(readers);
				}

				foreach (StreamingReader reader in snapshot)
				{
					try
					{
						reader.Service();
					}
					catch (Exception e)
					{
						Debug.LogWarning("[Audio] Streaming error: " + e.Message);
					}
				}

				wake.WaitOne(snapshot.Count > 0 ? 10 : 200);
			}
		}

		#endregion
	}

	/// <summary>A read-only window on a file (a sound inside a bank), seekable within its bounds.</summary>
	public sealed class SubStream : Stream
	{
		#region PrivateFields

		private readonly Stream inner;
		private readonly long start;
		private readonly long length;
		private long position;

		#endregion

		#region Constructors

		public SubStream(Stream inner, long start, long length)
		{
			this.inner = inner;
			this.start = start;
			this.length = length;
			inner.Position = start;
		}

		#endregion

		#region PublicMethods

		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => length;

		public override long Position
		{
			get => position;
			set
			{
				position = Math.Clamp(value, 0, length);
				inner.Position = start + position;
			}
		}

		public override void Flush()
		{ }

		public override int Read(byte[] buffer, int offset, int count)
		{
			long remaining = length - position;
			if (remaining <= 0)
			{
				return 0;
			}

			if (inner.Position != start + position)
			{
				inner.Position = start + position;
			}

			int read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
			position += read;
			return read;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			long target = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? position + offset : length + offset;
			Position = target;
			return position;
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
			}

			base.Dispose(disposing);
		}

		#endregion
	}
}
