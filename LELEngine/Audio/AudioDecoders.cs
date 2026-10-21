using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using NLayer;
using NVorbis;

namespace LELEngine.Audio
{
	/// <summary>The container of an audio file, told by its extension (<see cref="AudioDecoder.FormatOf" />) or its first bytes (<see cref="AudioDecoder.DetectFormat" />).</summary>
	public enum AudioFileFormat
	{
		Unknown,

		/// <summary>RIFF / WAVE (also RF64 up to 4 GB): PCM 8 / 16 / 24 / 32 bit and IEEE float 32 / 64 bit, read by the engine itself.</summary>
		Wav,

		/// <summary>Vorbis audio in an Ogg container, decoded by NVorbis.</summary>
		OggVorbis,

		/// <summary>MPEG audio layer I / II / III (ID3 tags skipped), decoded by NLayer's frame decoder.</summary>
		Mp3
	}

	/// <summary>Decoded PCM: interleaved float samples, nominally in [-1, 1] (MP3 decoding may overshoot a little).</summary>
	public sealed class PcmData
	{
		#region PublicFields

		/// <summary>Interleaved samples (left, right, left, right, ...), <see cref="Frames" /> x <see cref="Channels" /> floats.</summary>
		public float[] Samples;

		/// <summary>1 or 2; files with more channels keep their first two.</summary>
		public int Channels;

		/// <summary>Frames per second.</summary>
		public int SampleRate;

		/// <summary>Sample frames (one sample per channel each).</summary>
		public int Frames => Channels > 0 && Samples != null ? Samples.Length / Channels : 0;

		/// <summary>Length in seconds.</summary>
		public double Duration => SampleRate > 0 ? (double)Frames / SampleRate : 0;

		#endregion
	}

	/// <summary>
	///     A decoder that reads a file progressively (streaming playback): frames of interleaved floats, 1 or 2
	///     channels (sources with more channels give their first two). Positions are frames of the linear decode:
	///     <see cref="Seek" /> followed by <see cref="Read" /> gives the same samples as reading from the start would.
	///     Not thread safe: one thread uses it at a time (it may be handed from the loading thread to the mixer
	///     thread). Disposing it closes the source stream when the decoder owns it.
	/// </summary>
	public interface IAudioStreamDecoder : IDisposable
	{
		/// <summary>1 or 2.</summary>
		int Channels { get; }

		/// <summary>Frames per second.</summary>
		int SampleRate { get; }

		/// <summary>Frames in the source, -1 when unknown (a WAV stream of unknown length).</summary>
		long TotalFrames { get; }

		/// <summary>The frame the next <see cref="Read" /> starts at.</summary>
		long Position { get; }

		/// <summary>
		///     Reads up to <paramref name="frames" /> frames of interleaved floats into <paramref name="buffer" /> from float
		///     index <paramref name="offset" /> (the buffer holds at least offset + frames x <see cref="Channels" /> floats).
		///     Returns the frames read: the full count until the source ends, fewer at the end, 0 after it. Throws
		///     <see cref="InvalidDataException" /> on corrupt data.
		/// </summary>
		int Read(float[] buffer, int offset, int frames);

		/// <summary>Moves to the frame (clamped to [0, <see cref="TotalFrames" />]); false when the source cannot seek.</summary>
		bool Seek(long frame);
	}

	/// <summary>
	///     Reads audio files: WAV by the engine's own parser, Ogg Vorbis through NVorbis and MP3 through NLayer, as
	///     whole <see cref="PcmData" /> blocks or as streaming <see cref="IAudioStreamDecoder" />s. Bad data raises
	///     <see cref="InvalidDataException" /> with the format in the message; I/O errors pass through.
	/// </summary>
	public static class AudioDecoder
	{
		#region PublicFields

		/// <summary>File extensions the decoders take, lower case with the dot.</summary>
		public static readonly string[] Extensions = { ".wav", ".ogg", ".mp3" };

		#endregion

		#region PrivateFields

		/// <summary>Frames per read when a whole file is decoded.</summary>
		private const int DecodeChunkFrames = 4096;

		/// <summary>Capacity to start with when a decoder cannot tell its length.</summary>
		private const int UnknownLengthStartFrames = 48000 * 10;

		private const int FileBufferSize = 1 << 16;

		#endregion

		#region PublicMethods

		/// <summary>Whether the path has one of <see cref="Extensions" /> (case-insensitive); the content is not read.</summary>
		public static bool IsAudioFile(string path)
		{
			return FormatOf(path) != AudioFileFormat.Unknown;
		}

		/// <summary>The format the path's extension names, <see cref="AudioFileFormat.Unknown" /> for any other extension.</summary>
		public static AudioFileFormat FormatOf(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return AudioFileFormat.Unknown;
			}

			string extension = Path.GetExtension(path);
			if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
			{
				return AudioFileFormat.Wav;
			}

			if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
			{
				return AudioFileFormat.OggVorbis;
			}

			if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
			{
				return AudioFileFormat.Mp3;
			}

			return AudioFileFormat.Unknown;
		}

		/// <summary>
		///     The format the first bytes of a file tell: "RIFF" / "RF64" .... "WAVE", "OggS", an "ID3" tag or an MPEG
		///     frame sync (0xFFE0 mask with valid version and layer bits). 12 bytes decide every case.
		/// </summary>
		public static AudioFileFormat DetectFormat(ReadOnlySpan<byte> header)
		{
			if (header.Length >= 12 && (HasTag(header, 0, "RIFF") || HasTag(header, 0, "RF64")) && HasTag(header, 8, "WAVE"))
			{
				return AudioFileFormat.Wav;
			}

			if (header.Length >= 4 && HasTag(header, 0, "OggS"))
			{
				return AudioFileFormat.OggVorbis;
			}

			if (header.Length >= 3 && HasTag(header, 0, "ID3"))
			{
				return AudioFileFormat.Mp3;
			}

			if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x18) != 0x08 && (header[1] & 0x06) != 0)
			{
				return AudioFileFormat.Mp3;
			}

			return AudioFileFormat.Unknown;
		}

		/// <summary>
		///     Decodes a whole file. The format comes from the file's first bytes (WAV and Ogg are unmistakable), then
		///     from the extension, then from an MPEG frame sync. <paramref name="forceMono" /> averages the channels.
		/// </summary>
		public static PcmData DecodeFile(string path, bool forceMono = false)
		{
			using (FileStream stream = OpenRead(path))
			{
				AudioFileFormat format = ResolveFormat(stream, path);
				return Decode(stream, format, forceMono);
			}
		}

		/// <summary>
		///     Decodes a whole stream from its current position. <see cref="AudioFileFormat.Unknown" /> detects the format
		///     from the header, which needs a seekable stream; a stream that cannot seek is buffered in memory first
		///     unless it is a WAV.
		/// </summary>
		public static PcmData Decode(Stream stream, AudioFileFormat format, bool forceMono = false)
		{
			if (stream == null)
			{
				throw new ArgumentNullException(nameof(stream));
			}

			if (format == AudioFileFormat.Unknown)
			{
				if (!stream.CanSeek)
				{
					throw new ArgumentException("Detecting the audio format needs a seekable stream; pass the format", nameof(stream));
				}

				format = ResolveFormat(stream, null);
			}

			if (!stream.CanSeek && format != AudioFileFormat.Wav)
			{
				stream = CopyToMemory(stream);
			}

			using (IAudioStreamDecoder decoder = CreateDecoder(stream, format, false))
			{
				return ReadAll(decoder, forceMono);
			}
		}

		/// <summary>Decodes a whole file held in memory.</summary>
		public static PcmData Decode(byte[] data, AudioFileFormat format, bool forceMono = false)
		{
			if (data == null)
			{
				throw new ArgumentNullException(nameof(data));
			}

			return Decode(new MemoryStream(data, 0, data.Length, false), format, forceMono);
		}

		/// <summary>
		///     Opens a streaming decoder on a seekable stream at its current position (<see cref="AudioFileFormat.Unknown" />
		///     detects the format). With <paramref name="ownsStream" /> the decoder disposes the stream; on failure the
		///     stream is disposed too.
		/// </summary>
		public static IAudioStreamDecoder OpenStream(Stream stream, AudioFileFormat format, bool ownsStream)
		{
			if (stream == null)
			{
				throw new ArgumentNullException(nameof(stream));
			}

			if (!stream.CanSeek)
			{
				if (ownsStream)
				{
					stream.Dispose();
				}

				throw new ArgumentException("Streaming decoders need a seekable stream", nameof(stream));
			}

			try
			{
				if (format == AudioFileFormat.Unknown)
				{
					format = ResolveFormat(stream, null);
				}

				return CreateDecoder(stream, format, ownsStream);
			}
			catch (Exception)
			{
				if (ownsStream)
				{
					stream.Dispose();
				}

				throw;
			}
		}

		/// <summary>Opens a streaming decoder on a file; the decoder owns the file handle.</summary>
		public static IAudioStreamDecoder OpenFile(string path)
		{
			FileStream stream = OpenRead(path);
			try
			{
				AudioFileFormat format = ResolveFormat(stream, path);
				return CreateDecoder(stream, format, true);
			}
			catch (Exception)
			{
				stream.Dispose();
				throw;
			}
		}

		/// <summary>
		///     Header information without decoding the audio: channels as the decoders give them (at most 2), frames
		///     -1 when the file cannot tell (an MP3 counts its frames, which reads the file's frame headers). False for
		///     a missing, unreadable or unknown file.
		/// </summary>
		public static bool TryReadInfo(string path, out AudioFileFormat format, out int channels, out int sampleRate, out long frames)
		{
			format = AudioFileFormat.Unknown;
			channels = 0;
			sampleRate = 0;
			frames = -1;
			try
			{
				using (FileStream stream = OpenRead(path))
				{
					format = ResolveFormat(stream, path);
					if (format == AudioFileFormat.Wav)
					{
						WavHeader header = ReadWavHeader(stream);
						channels = Math.Min(2, header.Channels);
						sampleRate = header.SampleRate;
						frames = header.Frames;
						return true;
					}

					using (IAudioStreamDecoder decoder = CreateDecoder(stream, format, false))
					{
						channels = decoder.Channels;
						sampleRate = decoder.SampleRate;
						frames = decoder.TotalFrames;
						return true;
					}
				}
			}
			catch (Exception)
			{
				format = AudioFileFormat.Unknown;
				return false;
			}
		}

		/// <summary>The average of the channels of each frame; one channel returns <paramref name="interleaved" /> itself.</summary>
		public static float[] ToMono(float[] interleaved, int channels)
		{
			if (interleaved == null)
			{
				throw new ArgumentNullException(nameof(interleaved));
			}

			if (channels <= 1)
			{
				return interleaved;
			}

			int frames = interleaved.Length / channels;
			var mono = new float[frames];
			float scale = 1f / channels;
			int s = 0;
			for (int f = 0; f < frames; f++)
			{
				float sum = 0f;
				for (int c = 0; c < channels; c++)
				{
					sum += interleaved[s++];
				}

				mono[f] = sum * scale;
			}

			return mono;
		}

		/// <summary>Writes a WAV file (16-bit PCM when <paramref name="pcm16" />, else 32-bit IEEE float) from interleaved floats; used by tests and offline rendering.</summary>
		public static void WriteWav(string path, float[] samples, int channels, int sampleRate, bool pcm16)
		{
			using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, FileBufferSize))
			{
				WriteWav(stream, samples, channels, sampleRate, pcm16);
			}
		}

		/// <summary>
		///     Writes a WAV (16-bit PCM with the samples rounded and clipped, or 32-bit IEEE float with a fact chunk)
		///     to the stream at its current position; the stream stays open.
		/// </summary>
		public static void WriteWav(Stream stream, float[] samples, int channels, int sampleRate, bool pcm16)
		{
			if (stream == null)
			{
				throw new ArgumentNullException(nameof(stream));
			}

			if (samples == null)
			{
				throw new ArgumentNullException(nameof(samples));
			}

			if (channels < 1 || channels > ushort.MaxValue)
			{
				throw new ArgumentOutOfRangeException(nameof(channels));
			}

			if (sampleRate < 1)
			{
				throw new ArgumentOutOfRangeException(nameof(sampleRate));
			}

			int bytesPerSample = pcm16 ? 2 : 4;
			int frames = samples.Length / channels;
			int sampleCount = frames * channels;
			long dataSize = (long)sampleCount * bytesPerSample;
			int fmtSize = pcm16 ? 16 : 18;
			long riffSize = 4 + (8 + fmtSize) + (pcm16 ? 0 : 12) + (8 + dataSize + (dataSize & 1));
			if (riffSize > uint.MaxValue)
			{
				throw new ArgumentException("Too many samples for a WAV file", nameof(samples));
			}

			var header = new byte[12 + 8 + fmtSize + (pcm16 ? 0 : 12) + 8];
			Span<byte> h = header;
			int p = 0;
			WriteTag(h, ref p, "RIFF");
			BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(p), (uint)riffSize);
			p += 4;
			WriteTag(h, ref p, "WAVE");
			WriteTag(h, ref p, "fmt ");
			BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(p), (uint)fmtSize);
			p += 4;
			BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(p), (ushort)(pcm16 ? 1 : 3));
			p += 2;
			BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(p), (ushort)channels);
			p += 2;
			BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(p), (uint)sampleRate);
			p += 4;
			BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(p), (uint)((long)sampleRate * channels * bytesPerSample));
			p += 4;
			BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(p), (ushort)(channels * bytesPerSample));
			p += 2;
			BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(p), (ushort)(bytesPerSample * 8));
			p += 2;
			if (!pcm16)
			{
				BinaryPrimitives.WriteUInt16LittleEndian(h.Slice(p), 0);
				p += 2;
				WriteTag(h, ref p, "fact");
				BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(p), 4);
				p += 4;
				BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(p), (uint)frames);
				p += 4;
			}

			WriteTag(h, ref p, "data");
			BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(p), (uint)dataSize);
			p += 4;
			stream.Write(header, 0, p);

			var block = new byte[16384 * bytesPerSample];
			int written = 0;
			while (written < sampleCount)
			{
				int count = Math.Min(16384, sampleCount - written);
				Span<byte> b = block;
				if (pcm16)
				{
					for (int i = 0; i < count; i++)
					{
						float x = samples[written + i];
						int v = float.IsNaN(x) ? 0 : (int)MathF.Round(x * 32767f);
						if (v > short.MaxValue)
						{
							v = short.MaxValue;
						}
						else if (v < short.MinValue)
						{
							v = short.MinValue;
						}

						BinaryPrimitives.WriteInt16LittleEndian(b.Slice(i * 2), (short)v);
					}
				}
				else
				{
					for (int i = 0; i < count; i++)
					{
						BinaryPrimitives.WriteSingleLittleEndian(b.Slice(i * 4), samples[written + i]);
					}
				}

				stream.Write(block, 0, count * bytesPerSample);
				written += count;
			}

			if ((dataSize & 1) != 0)
			{
				stream.WriteByte(0);
			}

			stream.Flush();
		}

		#endregion

		#region PrivateMethods

		private static FileStream OpenRead(string path)
		{
			return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize);
		}

		/// <summary>
		///     The format of a seekable stream at its position: the header when it is unmistakable (WAV, Ogg), else the
		///     extension of <paramref name="path" /> (may be null), else an MPEG frame sync; the position is kept.
		/// </summary>
		private static AudioFileFormat ResolveFormat(Stream stream, string path)
		{
			AudioFileFormat detected = AudioFileFormat.Unknown;
			if (stream.CanSeek)
			{
				long position = stream.Position;
				Span<byte> header = stackalloc byte[12];
				int read = ReadUpTo(stream, header);
				stream.Position = position;
				detected = DetectFormat(header.Slice(0, read));
			}

			if (detected == AudioFileFormat.Wav || detected == AudioFileFormat.OggVorbis)
			{
				return detected;
			}

			AudioFileFormat byExtension = FormatOf(path);
			if (byExtension != AudioFileFormat.Unknown)
			{
				return byExtension;
			}

			if (detected != AudioFileFormat.Unknown)
			{
				return detected;
			}

			throw new InvalidDataException((path != null ? "'" + path + "'" : "The stream") + " is not a WAV, Ogg Vorbis or MP3 file");
		}

		private static IAudioStreamDecoder CreateDecoder(Stream stream, AudioFileFormat format, bool ownsStream)
		{
			switch (format)
			{
				case AudioFileFormat.Wav:
					return new WavStreamDecoder(stream, ownsStream);
				case AudioFileFormat.OggVorbis:
					return new VorbisStreamDecoder(stream, ownsStream);
				case AudioFileFormat.Mp3:
					return new Mp3StreamDecoder(stream, ownsStream);
				default:
					throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown audio format");
			}
		}

		private static PcmData ReadAll(IAudioStreamDecoder decoder, bool forceMono)
		{
			int channels = decoder.Channels;
			long total = decoder.TotalFrames;
			long capacityFrames = total >= 0 ? total + DecodeChunkFrames : UnknownLengthStartFrames;
			var samples = new float[CheckedLength(capacityFrames, channels)];
			long frames = 0;
			while (true)
			{
				if (frames == capacityFrames)
				{
					capacityFrames = capacityFrames + capacityFrames / 2 + DecodeChunkFrames;
					Array.Resize(ref samples, CheckedLength(capacityFrames, channels));
				}

				int want = (int)Math.Min(DecodeChunkFrames, capacityFrames - frames);
				int got = decoder.Read(samples, (int)(frames * channels), want);
				if (got <= 0)
				{
					break;
				}

				frames += got;
			}

			int length = (int)(frames * channels);
			if (samples.Length != length)
			{
				Array.Resize(ref samples, length);
			}

			if (forceMono && channels > 1)
			{
				samples = ToMono(samples, channels);
				channels = 1;
			}

			return new PcmData { Samples = samples, Channels = channels, SampleRate = decoder.SampleRate };
		}

		private static int CheckedLength(long frames, int channels)
		{
			long length = frames * channels;
			if (length > Array.MaxLength)
			{
				throw new InvalidDataException("The audio is too long to decode into memory (" + frames + " frames); stream it instead");
			}

			return (int)length;
		}

		private static MemoryStream CopyToMemory(Stream stream)
		{
			var memory = new MemoryStream();
			stream.CopyTo(memory);
			memory.Position = 0;
			return memory;
		}

		/// <summary>Reads until the span is full or the stream ends; returns the bytes read.</summary>
		internal static int ReadUpTo(Stream stream, Span<byte> buffer)
		{
			int total = 0;
			while (total < buffer.Length)
			{
				int read = stream.Read(buffer.Slice(total));
				if (read <= 0)
				{
					break;
				}

				total += read;
			}

			return total;
		}

		/// <summary>Reads up to <paramref name="buffer" />'s length at an absolute position of a seekable stream.</summary>
		internal static int ReadAt(Stream stream, long position, Span<byte> buffer)
		{
			if (stream.Position != position)
			{
				stream.Position = position;
			}

			return ReadUpTo(stream, buffer);
		}

		internal static void Skip(Stream stream, long count)
		{
			if (count <= 0)
			{
				return;
			}

			if (stream.CanSeek)
			{
				stream.Seek(count, SeekOrigin.Current);
				return;
			}

			Span<byte> scratch = stackalloc byte[1024];
			while (count > 0)
			{
				int read = stream.Read(scratch.Slice(0, (int)Math.Min(scratch.Length, count)));
				if (read <= 0)
				{
					return;
				}

				count -= read;
			}
		}

		internal static bool HasTag(ReadOnlySpan<byte> bytes, int offset, string tag)
		{
			if (bytes.Length < offset + tag.Length)
			{
				return false;
			}

			for (int i = 0; i < tag.Length; i++)
			{
				if (bytes[offset + i] != (byte)tag[i])
				{
					return false;
				}
			}

			return true;
		}

		private static void WriteTag(Span<byte> bytes, ref int position, string tag)
		{
			for (int i = 0; i < tag.Length; i++)
			{
				bytes[position++] = (byte)tag[i];
			}
		}

		/// <summary>
		///     Reads the RIFF header and the chunks up to the start of the data chunk (fmt parsed, LIST / fact / cue /
		///     smpl / bext / JUNK / ds64 and other chunks skipped with their pad bytes); the stream is left at the
		///     first sample. A data size of 0 or beyond the file means "to the end of the file" (streaming recorders
		///     write that), unless a chunk header follows an empty data chunk.
		/// </summary>
		internal static WavHeader ReadWavHeader(Stream stream)
		{
			long start = stream.CanSeek ? stream.Position : 0;
			Span<byte> riff = stackalloc byte[12];
			if (ReadUpTo(stream, riff) < 12 || !(HasTag(riff, 0, "RIFF") || HasTag(riff, 0, "RF64")) || !HasTag(riff, 8, "WAVE"))
			{
				throw new InvalidDataException("WAV: not a RIFF/WAVE file");
			}

			long consumed = 12;
			WavHeader header = null;
			Span<byte> chunk = stackalloc byte[8];
			while (ReadUpTo(stream, chunk) == 8)
			{
				consumed += 8;
				uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4));
				if (HasTag(chunk, 0, "fmt "))
				{
					if (size < 16)
					{
						throw new InvalidDataException("WAV: the fmt chunk is too short (" + size + " bytes)");
					}

					if (size > 4096)
					{
						throw new InvalidDataException("WAV: the fmt chunk size " + size + " is not plausible");
					}

					var fmt = new byte[size];
					if (ReadUpTo(stream, fmt) < fmt.Length)
					{
						throw new InvalidDataException("WAV: truncated fmt chunk");
					}

					consumed += size;
					header = ParseWavFormat(fmt);
					if ((size & 1) != 0)
					{
						Skip(stream, 1);
						consumed++;
					}
				}
				else if (HasTag(chunk, 0, "data"))
				{
					if (header == null)
					{
						throw new InvalidDataException("WAV: the data chunk comes before the fmt chunk");
					}

					header.DataStart = start + consumed;
					long length = size;
					if (stream.CanSeek)
					{
						long remaining = Math.Max(0, stream.Length - header.DataStart);
						if (size == 0 && LooksLikeChunkHeader(stream, remaining))
						{
							length = 0;
						}
						else if (size == 0 || size > remaining)
						{
							length = remaining;
						}
					}
					else if (size == 0 || size == uint.MaxValue)
					{
						length = -1;
					}

					if (length >= 0)
					{
						length -= length % header.BytesPerFrame;
					}

					header.DataLength = length;
					return header;
				}
				else
				{
					long skip = size + (size & 1);
					consumed += skip;
					Skip(stream, skip);
				}
			}

			throw new InvalidDataException(header == null ? "WAV: no fmt chunk" : "WAV: no data chunk");
		}

		private static WavHeader ParseWavFormat(byte[] fmt)
		{
			ReadOnlySpan<byte> f = fmt;
			int tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
			int channels = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(2));
			int sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(4));
			int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(12));
			int bits = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(14));
			if (tag == 0xFFFE)
			{
				// WAVE_FORMAT_EXTENSIBLE: the sub-format GUID's first two bytes are the plain format tag.
				if (fmt.Length < 40)
				{
					throw new InvalidDataException("WAV: the extensible fmt chunk is too short (" + fmt.Length + " bytes)");
				}

				tag = BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(24));
			}

			if (channels < 1)
			{
				throw new InvalidDataException("WAV: no channels");
			}

			if (sampleRate < 1)
			{
				throw new InvalidDataException("WAV: sample rate " + sampleRate);
			}

			int bytesPerSample = blockAlign > 0 && blockAlign % channels == 0 ? blockAlign / channels : (bits + 7) / 8;
			if (bytesPerSample < 1)
			{
				throw new InvalidDataException("WAV: " + bits + " bits per sample");
			}

			WavSampleFormat format;
			switch (tag)
			{
				case 1:
					switch (bytesPerSample)
					{
						case 1:
							format = WavSampleFormat.Pcm8;
							break;
						case 2:
							format = WavSampleFormat.Pcm16;
							break;
						case 3:
							format = WavSampleFormat.Pcm24;
							break;
						case 4:
							format = WavSampleFormat.Pcm32;
							break;
						default:
							throw new InvalidDataException("WAV: " + bytesPerSample * 8 + "-bit PCM is not supported (8, 16, 24 and 32 bits are)");
					}

					break;
				case 3:
					switch (bytesPerSample)
					{
						case 4:
							format = WavSampleFormat.Float32;
							break;
						case 8:
							format = WavSampleFormat.Float64;
							break;
						default:
							throw new InvalidDataException("WAV: " + bytesPerSample * 8 + "-bit float is not supported (32 and 64 bits are)");
					}

					break;
				default:
					throw new InvalidDataException("WAV: format tag 0x" + tag.ToString("X") + " (" + WavFormatTagName(tag) + ") is not supported; only PCM and IEEE float are");
			}

			return new WavHeader { Format = format, Channels = channels, SampleRate = sampleRate, BytesPerSample = bytesPerSample };
		}

		private static string WavFormatTagName(int tag)
		{
			switch (tag)
			{
				case 2:
					return "MS ADPCM";
				case 6:
					return "A-law";
				case 7:
					return "mu-law";
				case 0x11:
					return "IMA ADPCM";
				case 0x50:
					return "MPEG";
				case 0x55:
					return "MPEG Layer 3";
				default:
					return "unknown";
			}
		}

		/// <summary>Whether the next 8 bytes of a seekable stream read as a chunk header that fits in the remaining bytes; the position is kept.</summary>
		private static bool LooksLikeChunkHeader(Stream stream, long remaining)
		{
			if (remaining < 8)
			{
				return false;
			}

			long position = stream.Position;
			Span<byte> chunk = stackalloc byte[8];
			int read = ReadUpTo(stream, chunk);
			stream.Position = position;
			if (read < 8)
			{
				return false;
			}

			for (int i = 0; i < 4; i++)
			{
				if (chunk[i] < 0x20 || chunk[i] > 0x7E)
				{
					return false;
				}
			}

			uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4));
			return 8 + size + (size & 1) <= remaining;
		}

		#endregion
	}

	/// <summary>How a WAV file stores its samples.</summary>
	internal enum WavSampleFormat
	{
		Pcm8,
		Pcm16,
		Pcm24,
		Pcm32,
		Float32,
		Float64
	}

	/// <summary>The fmt chunk of a WAV file and where its data chunk is.</summary>
	internal sealed class WavHeader
	{
		#region PublicFields

		public WavSampleFormat Format;

		/// <summary>Channels in the file (the decoders give at most 2).</summary>
		public int Channels;

		public int SampleRate;

		/// <summary>Bytes of one sample's container (a 20-bit sample in 3 bytes has 3).</summary>
		public int BytesPerSample;

		/// <summary>Byte position of the first sample in the stream.</summary>
		public long DataStart;

		/// <summary>Bytes of whole frames in the data chunk; -1 when the stream cannot tell (not seekable, size 0).</summary>
		public long DataLength;

		public int BytesPerFrame => Channels * BytesPerSample;

		/// <summary>Frames in the data chunk, -1 when unknown.</summary>
		public long Frames => DataLength < 0 ? -1 : DataLength / BytesPerFrame;

		#endregion
	}

	/// <summary>Streams a WAV file's data chunk, converting to floats as it goes; seeks by frame arithmetic.</summary>
	internal sealed class WavStreamDecoder : IAudioStreamDecoder
	{
		#region PublicFields

		public int Channels { get; }
		public int SampleRate => header.SampleRate;
		public long TotalFrames => header.Frames;
		public long Position => position;

		#endregion

		#region PrivateFields

		private readonly Stream stream;
		private readonly bool ownsStream;
		private readonly WavHeader header;
		private long position;
		private byte[] bytes;

		#endregion

		#region Constructors

		public WavStreamDecoder(Stream stream, bool ownsStream)
		{
			this.stream = stream;
			this.ownsStream = ownsStream;
			header = AudioDecoder.ReadWavHeader(stream);
			Channels = Math.Min(2, header.Channels);
		}

		#endregion

		#region PublicMethods

		public int Read(float[] buffer, int offset, int frames)
		{
			if (buffer == null)
			{
				throw new ArgumentNullException(nameof(buffer));
			}

			if (frames <= 0)
			{
				return 0;
			}

			long total = header.Frames;
			if (total >= 0)
			{
				frames = (int)Math.Min(frames, total - position);
				if (frames <= 0)
				{
					return 0;
				}
			}

			if ((long)offset + (long)frames * Channels > buffer.Length)
			{
				throw new ArgumentException("The buffer is too small for " + frames + " frames", nameof(buffer));
			}

			int stride = header.BytesPerFrame;
			int byteCount = frames * stride;
			if (bytes == null || bytes.Length < byteCount)
			{
				bytes = new byte[Math.Max(byteCount, 4096 * stride)];
			}

			int read = AudioDecoder.ReadUpTo(stream, new Span<byte>(bytes, 0, byteCount));
			int readFrames = read / stride;
			Convert(bytes, readFrames, buffer, offset);
			position += readFrames;
			return readFrames;
		}

		public bool Seek(long frame)
		{
			if (!stream.CanSeek)
			{
				return false;
			}

			if (frame < 0)
			{
				frame = 0;
			}

			long total = header.Frames;
			if (total >= 0 && frame > total)
			{
				frame = total;
			}

			stream.Position = header.DataStart + frame * header.BytesPerFrame;
			position = frame;
			return true;
		}

		public void Dispose()
		{
			if (ownsStream)
			{
				stream.Dispose();
			}
		}

		#endregion

		#region PrivateMethods

		private void Convert(byte[] src, int frames, float[] dst, int dstOffset)
		{
			int bytesPerSample = header.BytesPerSample;
			int outChannels = Channels;
			int skip = header.BytesPerFrame - outChannels * bytesPerSample;
			ReadOnlySpan<byte> s = src;
			int i = 0;
			int d = dstOffset;
			switch (header.Format)
			{
				case WavSampleFormat.Pcm8:
					for (int f = 0; f < frames; f++, i += skip)
					{
						for (int c = 0; c < outChannels; c++, i++)
						{
							dst[d++] = (s[i] - 128) * (1f / 128f);
						}
					}

					break;
				case WavSampleFormat.Pcm16:
					for (int f = 0; f < frames; f++, i += skip)
					{
						for (int c = 0; c < outChannels; c++, i += 2)
						{
							dst[d++] = (short)(s[i] | (s[i + 1] << 8)) * (1f / 32768f);
						}
					}

					break;
				case WavSampleFormat.Pcm24:
					for (int f = 0; f < frames; f++, i += skip)
					{
						for (int c = 0; c < outChannels; c++, i += 3)
						{
							int v = ((s[i] << 8) | (s[i + 1] << 16) | (s[i + 2] << 24)) >> 8;
							dst[d++] = v * (1f / 8388608f);
						}
					}

					break;
				case WavSampleFormat.Pcm32:
					for (int f = 0; f < frames; f++, i += skip)
					{
						for (int c = 0; c < outChannels; c++, i += 4)
						{
							dst[d++] = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(i)) * (1f / 2147483648f);
						}
					}

					break;
				case WavSampleFormat.Float32:
					for (int f = 0; f < frames; f++, i += skip)
					{
						for (int c = 0; c < outChannels; c++, i += 4)
						{
							dst[d++] = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(i));
						}
					}

					break;
				case WavSampleFormat.Float64:
					for (int f = 0; f < frames; f++, i += skip)
					{
						for (int c = 0; c < outChannels; c++, i += 8)
						{
							dst[d++] = (float)BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(i));
						}
					}

					break;
			}
		}

		#endregion
	}

	/// <summary>
	///     A read-only, seekable view of up to two ranges of another stream, presented back to back from position 0:
	///     NVorbis is given the Ogg header pages followed by the pages from a seek target, which it decodes as one
	///     stream. Disposing the view leaves the source open.
	/// </summary>
	internal sealed class ViewStream : Stream
	{
		#region PublicFields

		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => headLength + tailLength;

		public override long Position
		{
			get => position;
			set
			{
				if (value < 0)
				{
					throw new ArgumentOutOfRangeException(nameof(value));
				}

				position = value;
			}
		}

		#endregion

		#region PrivateFields

		private readonly Stream source;
		private readonly long headStart;
		private readonly long headLength;
		private readonly long tailStart;
		private readonly long tailLength;
		private long position;

		#endregion

		#region Constructors

		/// <summary>A view of [headStart, headStart + headLength) followed by [tailStart, tailStart + tailLength) of <paramref name="source" />.</summary>
		public ViewStream(Stream source, long headStart, long headLength, long tailStart, long tailLength)
		{
			this.source = source;
			this.headStart = headStart;
			this.headLength = Math.Max(0, headLength);
			this.tailStart = tailStart;
			this.tailLength = Math.Max(0, tailLength);
		}

		#endregion

		#region PublicMethods

		public override int Read(byte[] buffer, int offset, int count)
		{
			return Read(new Span<byte>(buffer, offset, count));
		}

		public override int Read(Span<byte> buffer)
		{
			if (buffer.Length == 0 || position >= Length)
			{
				return 0;
			}

			long sourceOffset;
			long available;
			if (position < headLength)
			{
				sourceOffset = headStart + position;
				available = headLength - position;
			}
			else
			{
				sourceOffset = tailStart + (position - headLength);
				available = tailLength - (position - headLength);
			}

			int count = (int)Math.Min(buffer.Length, available);
			if (source.Position != sourceOffset)
			{
				source.Position = sourceOffset;
			}

			int read = source.Read(buffer.Slice(0, count));
			if (read > 0)
			{
				position += read;
			}

			return read;
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			switch (origin)
			{
				case SeekOrigin.Begin:
					Position = offset;
					break;
				case SeekOrigin.Current:
					Position = position + offset;
					break;
				case SeekOrigin.End:
					Position = Length + offset;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(origin));
			}

			return position;
		}

		public override void Flush()
		{
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		#endregion
	}

	/// <summary>One page of an Ogg stream as <see cref="OggPageIndex" /> records it.</summary>
	internal struct OggPage
	{
		/// <summary>Byte offset from the start of the Ogg data.</summary>
		public long Offset;

		public int Length;

		/// <summary>Position of the last sample completed on the page; -1 when no packet ends on it.</summary>
		public long Granule;

		/// <summary>Whether the page starts inside a packet begun on the page before (header flag 0x01).</summary>
		public bool Continued;

		/// <summary>Whether the page is the stream's last (header flag 0x04).</summary>
		public bool EndOfStream;

		/// <summary>Packets that end on the page (a packet continued from the page before counts here).</summary>
		public int Packets;
	}

	/// <summary>
	///     The pages of an Ogg stream's first logical stream: where they are, their granule positions, which is the
	///     first audio page and the Vorbis block sizes. Built on demand by <see cref="VorbisStreamDecoder" /> for seeking.
	/// </summary>
	internal sealed class OggPageIndex
	{
		#region PublicFields

		public readonly List<OggPage> Pages = new List<OggPage>();

		/// <summary>Index of the first page after the three Vorbis header packets; -1 when the stream has none.</summary>
		public int FirstAudioPage = -1;

		/// <summary>The short block size from the identification header; 0 when it was not found.</summary>
		public int BlockSize0;

		/// <summary>The long block size from the identification header; 0 when it was not found.</summary>
		public int BlockSize1;

		#endregion

		#region PublicMethods

		/// <summary>Scans the pages of <paramref name="length" /> bytes of Ogg data at <paramref name="start" /> (page offsets are relative to it).</summary>
		public static OggPageIndex Scan(Stream stream, long start, long length)
		{
			var index = new OggPageIndex();
			var header = new byte[27 + 255];
			long position = 0;
			int serial = 0;
			bool haveSerial = false;
			int headerPackets = 0;
			while (position + 27 <= length)
			{
				if (AudioDecoder.ReadAt(stream, start + position, new Span<byte>(header, 0, 27)) < 27 || !AudioDecoder.HasTag(header, 0, "OggS") || header[4] != 0)
				{
					break;
				}

				int segments = header[26];
				if (AudioDecoder.ReadUpTo(stream, new Span<byte>(header, 27, segments)) < segments)
				{
					break;
				}

				int body = 0;
				int packetsEnding = 0;
				for (int i = 0; i < segments; i++)
				{
					body += header[27 + i];
					if (header[27 + i] < 255)
					{
						packetsEnding++;
					}
				}

				int pageLength = 27 + segments + body;
				if (position + pageLength > length)
				{
					break;
				}

				int pageSerial = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(header, 14, 4));
				if (!haveSerial)
				{
					serial = pageSerial;
					haveSerial = true;
				}

				if (pageSerial == serial)
				{
					if (index.Pages.Count == 0 && body >= 30)
					{
						// The identification header: "\x01vorbis", version, channels, rate, three bit rates, block sizes.
						Span<byte> id = stackalloc byte[30];
						if (AudioDecoder.ReadUpTo(stream, id) == 30 && id[0] == 1 && AudioDecoder.HasTag(id, 1, "vorbis"))
						{
							index.BlockSize0 = 1 << (id[28] & 0x0F);
							index.BlockSize1 = 1 << (id[28] >> 4);
						}
					}

					index.Pages.Add(new OggPage
					{
						Offset = position,
						Length = pageLength,
						Granule = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(header, 6, 8)),
						Continued = (header[5] & 0x01) != 0,
						EndOfStream = (header[5] & 0x04) != 0,
						Packets = packetsEnding
					});
					if (index.FirstAudioPage < 0)
					{
						headerPackets += packetsEnding;
						if (headerPackets >= 3)
						{
							index.FirstAudioPage = index.Pages.Count;
						}
					}
				}

				position += pageLength;
			}

			if (index.FirstAudioPage >= index.Pages.Count)
			{
				index.FirstAudioPage = -1;
			}

			return index;
		}

		/// <summary>The first audio page whose granule position reaches <paramref name="granule" />; -1 when none does.</summary>
		public int FindPageCompleting(long granule)
		{
			for (int i = FirstAudioPage; i < Pages.Count; i++)
			{
				if (Pages[i].Granule >= granule)
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>The latest page at or before <paramref name="index" /> a decoder can start on: not continued, completing a packet, not before the first audio page.</summary>
		public int StartPageFor(int index)
		{
			int page = Math.Min(index, Pages.Count - 1);
			while (page > FirstAudioPage && (Pages[page].Continued || Pages[page].Granule < 0))
			{
				page--;
			}

			return Math.Max(page, FirstAudioPage);
		}

		/// <summary>Granule position completed before the page: the granule of the nearest earlier page that completes a packet, 0 before the first.</summary>
		public long GranuleBefore(int index)
		{
			for (int i = index - 1; i >= FirstAudioPage; i--)
			{
				if (Pages[i].Granule >= 0)
				{
					return Pages[i].Granule;
				}
			}

			return 0;
		}

		public bool IsLastPage(int index)
		{
			return Pages[index].EndOfStream || index == Pages.Count - 1;
		}

		#endregion
	}

	/// <summary>
	///     Ogg Vorbis through NVorbis; sources with more than two channels give their first two. Linear reading is
	///     NVorbis as it is, cut at the stream's final granule position. Seeking does not use NVorbis's own seek or
	///     its sample position (both are off by block-size transitions): a seek starts a fresh reader on a
	///     <see cref="ViewStream" /> (header pages + the stream from a page shortly before the target), measures the
	///     output of each packet (every packet gives a multiple of a quarter short block, and the bit count in the
	///     stream statistics changes with each new packet), which tells the block types, and takes the exact granule
	///     position from the first page boundary that settles it; then it reads up to the target.
	/// </summary>
	internal sealed class VorbisStreamDecoder : IAudioStreamDecoder
	{
		#region PublicFields

		public int Channels { get; }
		public int SampleRate { get; }

		/// <summary>The stream's final granule position: the frames a linear decode delivers.</summary>
		public long TotalFrames => totalSamples;

		public long Position => position;

		#endregion

		#region PrivateFields

		private const int ScratchFrames = 2048;

		/// <summary>The largest Vorbis block: slack for how far a page's output can run past its granule span.</summary>
		private const int MaxBlockSize = 8192;

		/// <summary>Pages before the one completing the target a seek starts on.</summary>
		private const int SeekPageSlack = 2;

		private readonly Stream stream;
		private readonly bool ownsStream;
		private readonly long streamStart;
		private readonly long streamLength;
		private readonly int sourceChannels;
		private readonly long totalSamples;
		private readonly List<int> packetLengths = new List<int>();
		private VorbisReader reader;
		private long position;
		private OggPageIndex pages;
		private bool pagesScanned;
		private float[] scratch;

		#endregion

		#region Constructors

		public VorbisStreamDecoder(Stream stream, bool ownsStream)
		{
			this.stream = stream;
			this.ownsStream = ownsStream;
			streamStart = stream.Position;
			streamLength = stream.Length - streamStart;
			try
			{
				reader = CreateReader(-1);
				sourceChannels = reader.Channels;
				SampleRate = reader.SampleRate;
				long total;
				try
				{
					total = reader.TotalSamples;
				}
				catch (Exception)
				{
					total = -1;
				}

				totalSamples = total < 0 ? -1 : total;
			}
			catch (Exception e) when (!(e is InvalidDataException))
			{
				reader?.Dispose();
				throw new InvalidDataException("Ogg Vorbis: " + e.Message, e);
			}

			if (sourceChannels < 1 || SampleRate < 1)
			{
				reader.Dispose();
				throw new InvalidDataException("Ogg Vorbis: the stream has no audio (" + sourceChannels + " channels at " + SampleRate + " Hz)");
			}

			Channels = Math.Min(2, sourceChannels);
		}

		#endregion

		#region PublicMethods

		public int Read(float[] buffer, int offset, int frames)
		{
			if (buffer == null)
			{
				throw new ArgumentNullException(nameof(buffer));
			}

			if (totalSamples >= 0)
			{
				frames = (int)Math.Min(frames, totalSamples - position);
			}

			if (frames <= 0)
			{
				return 0;
			}

			if ((long)offset + (long)frames * Channels > buffer.Length)
			{
				throw new ArgumentException("The buffer is too small for " + frames + " frames", nameof(buffer));
			}

			int done;
			try
			{
				done = ReadFrames(buffer, offset, frames);
			}
			catch (Exception e) when (!(e is InvalidDataException))
			{
				throw new InvalidDataException("Ogg Vorbis: " + e.Message, e);
			}

			position += done;
			return done;
		}

		public bool Seek(long frame)
		{
			if (frame < 0)
			{
				frame = 0;
			}

			if (totalSamples >= 0 && frame > totalSamples)
			{
				frame = totalSamples;
			}

			if (frame == position)
			{
				return true;
			}

			try
			{
				// A little ahead: just read on.
				if (frame > position && frame - position <= SampleRate)
				{
					position += DiscardFrames(frame - position);
					return true;
				}

				if (frame == 0)
				{
					Restart(-1);
					return true;
				}

				if (!EnsurePages() || pages.BlockSize0 < 64 || pages.BlockSize1 < pages.BlockSize0)
				{
					return SeekByReading(frame);
				}

				int completing = pages.FindPageCompleting(frame);
				if (completing < 0)
				{
					return SeekByReading(frame);
				}

				int start = pages.StartPageFor(completing - SeekPageSlack);
				while (start > pages.FirstAudioPage)
				{
					Restart(start);
					if (MeasureGranule(start, out long granule) && granule <= frame)
					{
						DiscardFrames(frame - granule);
						position = frame;
						return true;
					}

					// Not settled before the target (tiny pages, odd block patterns): start a page earlier.
					start = pages.StartPageFor(start - 1);
				}

				return SeekByReading(frame);
			}
			catch (Exception e) when (!(e is InvalidDataException))
			{
				throw new InvalidDataException("Ogg Vorbis: " + e.Message, e);
			}
		}

		public void Dispose()
		{
			reader?.Dispose();
			reader = null;
			if (ownsStream)
			{
				stream.Dispose();
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>A reader on the whole stream (<paramref name="startPage" /> -1) or on the header pages followed by the stream from that page.</summary>
		private VorbisReader CreateReader(int startPage)
		{
			ViewStream view;
			if (startPage < 0)
			{
				view = new ViewStream(stream, streamStart, streamLength, 0, 0);
			}
			else
			{
				long headerLength = pages.Pages[pages.FirstAudioPage].Offset;
				long tailOffset = pages.Pages[startPage].Offset;
				view = new ViewStream(stream, streamStart, headerLength, streamStart + tailOffset, streamLength - tailOffset);
			}

			return new VorbisReader(view, true);
		}

		/// <summary>Replaces the reader; from the start, the position is 0 again, from a page it is what the caller measures.</summary>
		private void Restart(int startPage)
		{
			reader?.Dispose();
			reader = null;
			reader = CreateReader(startPage);
			position = 0;
		}

		/// <summary>Reads from the current reader into the caller's buffer, reducing the channels.</summary>
		private int ReadFrames(float[] buffer, int offset, int frames)
		{
			if (reader == null)
			{
				throw new ObjectDisposedException(nameof(VorbisStreamDecoder));
			}

			int done = 0;
			if (sourceChannels == Channels)
			{
				while (done < frames)
				{
					int got = reader.ReadSamples(buffer, offset + done * Channels, (frames - done) * Channels) / Channels;
					if (got <= 0)
					{
						break;
					}

					done += got;
				}
			}
			else
			{
				EnsureScratch();
				int scratchFrames = scratch.Length / sourceChannels;
				while (done < frames)
				{
					int want = Math.Min(frames - done, scratchFrames);
					int got = reader.ReadSamples(scratch, 0, want * sourceChannels) / sourceChannels;
					if (got <= 0)
					{
						break;
					}

					KeepFirstChannels(scratch, got, sourceChannels, buffer, offset + done * Channels, Channels);
					done += got;
				}
			}

			return done;
		}

		/// <summary>Reads and drops frames; returns how many it could.</summary>
		private long DiscardFrames(long count)
		{
			EnsureScratch();
			int scratchFrames = scratch.Length / sourceChannels;
			long left = count;
			while (left > 0)
			{
				int want = (int)Math.Min(left, scratchFrames);
				int got = reader.ReadSamples(scratch, 0, want * sourceChannels) / sourceChannels;
				if (got <= 0)
				{
					break;
				}

				left -= got;
			}

			return count - left;
		}

		/// <summary>
		///     Reads from a reader just started on <paramref name="start" /> until a page boundary settles the stream
		///     position; gives the granule of the next frame the reader will deliver. False when the stream ends or
		///     the pages stay ambiguous first.
		/// </summary>
		private bool MeasureGranule(int start, out long granule)
		{
			granule = 0;
			int quarterBlock = pages.BlockSize0 / 4;
			int transition = (pages.BlockSize1 - pages.BlockSize0) / 4;
			EnsureScratch();
			packetLengths.Clear();
			packetLengths.Add(0); // The start page's first packet: a fresh decoder gives no output for it.
			long audioBits = reader.StreamStats.AudioBits;
			long delivered = 0;
			int page = start;
			long carrier = pages.Pages[start].Packets;
			int lastPage = Math.Min(start + 3, pages.Pages.Count - 1);
			long bound = (pages.Pages[lastPage].Granule >= 0 ? pages.Pages[lastPage].Granule : totalSamples) - pages.GranuleBefore(start) + 2 * MaxBlockSize;
			if (bound < 4 * MaxBlockSize)
			{
				bound = 4 * MaxBlockSize;
			}

			while (true)
			{
				int got = reader.ReadSamples(scratch, 0, quarterBlock * sourceChannels) / sourceChannels;
				if (got <= 0)
				{
					return false;
				}

				long bits = reader.StreamStats.AudioBits;
				if (bits != audioBits)
				{
					audioBits = bits;
					packetLengths.Add(0);
				}

				packetLengths[packetLengths.Count - 1] += got;
				delivered += got;
				int current = packetLengths.Count;
				while (page < pages.Pages.Count && carrier < current)
				{
					OggPage p = pages.Pages[page];
					if (p.Granule >= 0)
					{
						if (pages.IsLastPage(page))
						{
							// The end page's last packet is cut to the final granule: its length tells nothing.
							return false;
						}

						if (TryCarrierShift((int)carrier, quarterBlock, transition, out int shift))
						{
							long before = 0;
							for (int k = 0; k < carrier; k++)
							{
								before += packetLengths[k];
							}

							granule = p.Granule + shift - before + delivered;
							return true;
						}
					}

					page++;
					if (page < pages.Pages.Count)
					{
						carrier += pages.Pages[page].Packets;
					}
				}

				if (delivered > bound)
				{
					return false;
				}
			}
		}

		/// <summary>
		///     How far NVorbis's output of a page's last packet (1-based <paramref name="carrier" /> since the restart)
		///     runs past the page's granule position: a long block followed by a short one gets the transition's
		///     samples that the granule credits to the short block. The lengths NVorbis gave tell the block types:
		///     a short block gives half a short block, a long one half a long block plus a quarter of (previous minus
		///     next). False while the types stay ambiguous.
		/// </summary>
		private bool TryCarrierShift(int carrier, int quarterBlock, int transition, out int shift)
		{
			shift = 0;
			if (transition == 0)
			{
				return true;
			}

			if (carrier < 2 || carrier > packetLengths.Count)
			{
				return false;
			}

			int shortOutput = 2 * quarterBlock;
			int longOutput = shortOutput + 2 * transition;
			int length = packetLengths[carrier - 1];
			if (length == shortOutput)
			{
				return true;
			}

			if (carrier == 2)
			{
				// The previous packet gave no output (the first after the restart), so only the extremes are clear.
				if (length == longOutput + transition)
				{
					shift = transition;
					return true;
				}

				return length == longOutput - transition;
			}

			int previous = packetLengths[carrier - 2];
			if (previous <= 0)
			{
				return false;
			}

			int previousBlock = previous == shortOutput ? pages.BlockSize0 : pages.BlockSize1;
			int nextBlock = previousBlock + 4 * (longOutput - length);
			if (nextBlock == pages.BlockSize0)
			{
				shift = transition;
				return true;
			}

			return nextBlock == pages.BlockSize1;
		}

		private void EnsureScratch()
		{
			if (scratch == null)
			{
				scratch = new float[ScratchFrames * sourceChannels];
			}
		}

		private bool EnsurePages()
		{
			if (!pagesScanned)
			{
				pagesScanned = true;
				try
				{
					pages = OggPageIndex.Scan(stream, streamStart, streamLength);
				}
				catch (Exception)
				{
					pages = null;
				}
			}

			return pages != null && pages.FirstAudioPage >= 0;
		}

		/// <summary>The slow, always exact seek: from the start of the stream, reading up to the frame.</summary>
		private bool SeekByReading(long frame)
		{
			Restart(-1);
			position = DiscardFrames(frame);
			return true;
		}

		internal static void KeepFirstChannels(float[] src, int frames, int srcChannels, float[] dst, int dstOffset, int dstChannels)
		{
			int s = 0;
			int d = dstOffset;
			for (int f = 0; f < frames; f++, s += srcChannels)
			{
				for (int c = 0; c < dstChannels; c++)
				{
					dst[d++] = src[s + c];
				}
			}
		}

		#endregion
	}

	/// <summary>The 32-bit header of an MPEG audio frame, decoded.</summary>
	internal struct MpegHeader
	{
		#region PublicFields

		public MpegVersion Version;
		public MpegLayer Layer;
		public bool HasCrc;
		public int BitRateIndex;

		/// <summary>Bits per second; 0 for a free-format stream.</summary>
		public int BitRate;

		public int SampleRateIndex;
		public int SampleRate;
		public int Padding;
		public MpegChannelMode ChannelMode;
		public int ChannelModeExtension;
		public bool IsCopyrighted;

		/// <summary>Bytes of the frame including the header; 0 for a free-format stream.</summary>
		public int FrameLength;

		/// <summary>Samples per channel the frame decodes to.</summary>
		public int SampleCount;

		/// <summary>Bytes of layer III side information after the header (and CRC).</summary>
		public int SideInfoSize;

		public int Channels => ChannelMode == MpegChannelMode.Mono ? 1 : 2;

		#endregion

		#region PrivateFields

		// [MPEG-1, MPEG-2/2.5][layer I, II, III][index], kbit/s.
		private static readonly int[][][] BitRates =
		{
			new[]
			{
				new[] { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },
				new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },
				new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 }
			},
			new[]
			{
				new[] { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
				new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
				new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 }
			}
		};

		#endregion

		#region PublicMethods

		/// <summary>Decodes a big-endian header word; false when the sync, version, layer, bit rate or sample rate bits are not a valid frame's.</summary>
		public static bool TryParse(uint bits, out MpegHeader header)
		{
			header = default;
			if ((bits & 0xFFE00000) != 0xFFE00000)
			{
				return false;
			}

			int versionBits = (int)(bits >> 19) & 3;
			int layerBits = (int)(bits >> 17) & 3;
			int bitRateIndex = (int)(bits >> 12) & 0xF;
			int rateIndex = (int)(bits >> 10) & 3;
			if (versionBits == 1 || layerBits == 0 || bitRateIndex == 15 || rateIndex == 3)
			{
				return false;
			}

			header.Version = versionBits == 3 ? MpegVersion.Version1 : versionBits == 2 ? MpegVersion.Version2 : MpegVersion.Version25;
			header.Layer = layerBits == 3 ? MpegLayer.LayerI : layerBits == 2 ? MpegLayer.LayerII : MpegLayer.LayerIII;
			header.HasCrc = (bits & 0x10000) == 0;
			header.BitRateIndex = bitRateIndex;
			header.SampleRateIndex = rateIndex;
			int rate = rateIndex == 0 ? 44100 : rateIndex == 1 ? 48000 : 32000;
			if (versionBits == 2)
			{
				rate /= 2;
			}
			else if (versionBits == 0)
			{
				rate /= 4;
			}

			header.SampleRate = rate;
			header.Padding = (int)(bits >> 9) & 1;
			switch ((bits >> 6) & 3)
			{
				case 0:
					header.ChannelMode = MpegChannelMode.Stereo;
					break;
				case 1:
					header.ChannelMode = MpegChannelMode.JointStereo;
					break;
				case 2:
					header.ChannelMode = MpegChannelMode.DualChannel;
					break;
				default:
					header.ChannelMode = MpegChannelMode.Mono;
					break;
			}

			header.ChannelModeExtension = (int)(bits >> 4) & 3;
			header.IsCopyrighted = (bits & 8) != 0;
			bool mono = header.ChannelMode == MpegChannelMode.Mono;
			header.SampleCount = header.Layer == MpegLayer.LayerI ? 384 : header.Layer == MpegLayer.LayerIII && header.Version != MpegVersion.Version1 ? 576 : 1152;
			header.SideInfoSize = header.Layer == MpegLayer.LayerIII ? header.Version == MpegVersion.Version1 ? mono ? 17 : 32 : mono ? 9 : 17 : 0;
			if (bitRateIndex == 0)
			{
				return true;
			}

			int layerIndex = header.Layer == MpegLayer.LayerI ? 0 : header.Layer == MpegLayer.LayerII ? 1 : 2;
			header.BitRate = BitRates[versionBits == 3 ? 0 : 1][layerIndex][bitRateIndex] * 1000;
			header.FrameLength = header.Layer == MpegLayer.LayerI
				? (12 * header.BitRate / rate + header.Padding) * 4
				: header.SampleCount / 8 * header.BitRate / rate + header.Padding;
			return true;
		}

		/// <summary>Whether a frame belongs to the same stream as this one (same version, layer, sample rate and channel count).</summary>
		public bool SameStream(in MpegHeader other)
		{
			return Version == other.Version && Layer == other.Layer && SampleRate == other.SampleRate && Channels == other.Channels;
		}

		#endregion
	}

	/// <summary>
	///     The frames of an MPEG audio stream: where each one is, so a decoder can start at any of them. Built by
	///     reading every frame header once (ID3v2 tags and a Xing / Info / VBRI header frame skipped, garbage between
	///     frames resynchronized, free-format streams rejected). A LAME tag gives the encoder delay and padding.
	/// </summary>
	internal sealed class MpegFrameIndex
	{
		#region PublicFields

		/// <summary>The first audio frame's header: every indexed frame has its version, layer, sample rate and channels.</summary>
		public MpegHeader Format;

		public long[] Offsets = new long[256];
		public int[] Lengths = new int[256];
		public int Count;

		/// <summary>Samples the encoder added at the start per the LAME tag; -1 without a tag.</summary>
		public int EncoderDelay = -1;

		/// <summary>Samples the encoder added at the end per the LAME tag; -1 without a tag.</summary>
		public int EncoderPadding = -1;

		public int SamplesPerFrame => Format.SampleCount;

		#endregion

		#region PrivateFields

		/// <summary>Bytes searched for the first frame or after a lost sync before giving up.</summary>
		private const int SyncSearchLimit = 2 << 20;

		#endregion

		#region PublicMethods

		/// <summary>Indexes <paramref name="length" /> bytes of MPEG data at <paramref name="start" /> (offsets are relative to it).</summary>
		public static MpegFrameIndex Scan(Stream stream, long start, long length)
		{
			var index = new MpegFrameIndex();
			long position = SkipId3v2(stream, start, length);
			if (!FindFrame(stream, start, length, position, out position, out MpegHeader first))
			{
				throw new InvalidDataException("MP3: no MPEG audio frames found");
			}

			// A Xing / Info / VBRI header occupies the first frame; it is no audio.
			if (first.Layer == MpegLayer.LayerIII && index.ReadVbrHeader(stream, start + position, first))
			{
				long next = position + first.FrameLength;
				if (!FindFrame(stream, start, length, next, out position, out first))
				{
					throw new InvalidDataException("MP3: no MPEG audio frames after the VBR header");
				}
			}

			index.Format = first;
			Span<byte> word = stackalloc byte[4];
			while (position + 4 <= length)
			{
				bool valid = AudioDecoder.ReadAt(stream, start + position, word) == 4
				             && MpegHeader.TryParse(BinaryPrimitives.ReadUInt32BigEndian(word), out MpegHeader header)
				             && header.FrameLength > 0
				             && first.SameStream(header);
				if (!valid)
				{
					// Lost sync (a tag in the middle, damaged bytes): look for the next frame of this stream.
					if (!FindFrame(stream, start, length, position + 1, out long found, out MpegHeader resynced) || !first.SameStream(resynced))
					{
						break;
					}

					position = found;
					continue;
				}

				MpegHeader.TryParse(BinaryPrimitives.ReadUInt32BigEndian(word), out MpegHeader frame);
				if (position + frame.FrameLength > length)
				{
					break;
				}

				index.Add(position, frame.FrameLength);
				position += frame.FrameLength;
			}

			return index;
		}

		#endregion

		#region PrivateMethods

		private void Add(long offset, int length)
		{
			if (Count == Offsets.Length)
			{
				Array.Resize(ref Offsets, Count * 2);
				Array.Resize(ref Lengths, Count * 2);
			}

			Offsets[Count] = offset;
			Lengths[Count] = length;
			Count++;
		}

		/// <summary>Skips ID3v2 tags at the start; returns the position after them.</summary>
		private static long SkipId3v2(Stream stream, long start, long length)
		{
			long position = 0;
			Span<byte> head = stackalloc byte[10];
			while (position + 10 <= length)
			{
				if (AudioDecoder.ReadAt(stream, start + position, head) < 10 || !AudioDecoder.HasTag(head, 0, "ID3") || head[3] == 0xFF || head[4] == 0xFF)
				{
					break;
				}

				if ((head[6] | head[7] | head[8] | head[9]) >= 0x80)
				{
					break;
				}

				long size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
				position += 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
			}

			return Math.Min(position, length);
		}

		/// <summary>
		///     Finds the first valid frame header at or after <paramref name="from" /> that another valid header of the
		///     same stream follows (or that ends the data), within the search limit.
		/// </summary>
		private static bool FindFrame(Stream stream, long start, long length, long from, out long position, out MpegHeader header)
		{
			var window = new byte[16384];
			long limit = Math.Min(length, from + SyncSearchLimit);
			long at = from;
			Span<byte> next = stackalloc byte[4];
			while (at + 4 <= limit)
			{
				int read = AudioDecoder.ReadAt(stream, start + at, new Span<byte>(window, 0, (int)Math.Min(window.Length, limit - at)));
				if (read < 4)
				{
					break;
				}

				for (int i = 0; i + 4 <= read; i++)
				{
					if (window[i] != 0xFF || (window[i + 1] & 0xE0) != 0xE0)
					{
						continue;
					}

					if (!MpegHeader.TryParse(BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(window, i, 4)), out MpegHeader candidate) || candidate.FrameLength <= 0)
					{
						continue;
					}

					long candidateAt = at + i;
					long nextAt = candidateAt + candidate.FrameLength;
					bool confirmed;
					if (nextAt == length)
					{
						confirmed = true;
					}
					else if (nextAt + 4 <= length)
					{
						confirmed = AudioDecoder.ReadAt(stream, start + nextAt, next) == 4
						            && MpegHeader.TryParse(BinaryPrimitives.ReadUInt32BigEndian(next), out MpegHeader following)
						            && following.FrameLength > 0
						            && candidate.SameStream(following);
					}
					else
					{
						confirmed = false;
					}

					if (confirmed)
					{
						position = candidateAt;
						header = candidate;
						return true;
					}
				}

				at += read - 3;
			}

			position = from;
			header = default;
			return false;
		}

		/// <summary>Whether the frame at <paramref name="offset" /> is a Xing / Info or VBRI header frame; reads the LAME tag's delay and padding when there is one.</summary>
		private bool ReadVbrHeader(Stream stream, long offset, in MpegHeader header)
		{
			var frame = new byte[header.FrameLength];
			if (AudioDecoder.ReadAt(stream, offset, frame) < frame.Length)
			{
				return false;
			}

			int xing = 4 + (header.HasCrc ? 2 : 0) + header.SideInfoSize;
			if (xing + 8 <= frame.Length && (AudioDecoder.HasTag(frame, xing, "Xing") || AudioDecoder.HasTag(frame, xing, "Info")))
			{
				int p = xing + 4;
				uint flags = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(frame, p, 4));
				p += 4;
				if ((flags & 1) != 0)
				{
					p += 4;
				}

				if ((flags & 2) != 0)
				{
					p += 4;
				}

				if ((flags & 4) != 0)
				{
					p += 100;
				}

				if ((flags & 8) != 0)
				{
					p += 4;
				}

				// LAME tag: 9 bytes of encoder name ("LAME3.100", "Lavc60.3"), then 12 bytes of settings, then
				// 12 bits of encoder delay and 12 bits of padding.
				if (p + 24 <= frame.Length && IsLetter(frame[p]) && IsLetter(frame[p + 1]) && IsLetter(frame[p + 2]) && IsLetter(frame[p + 3]))
				{
					int delay = (frame[p + 21] << 4) | (frame[p + 22] >> 4);
					int padding = ((frame[p + 22] & 0x0F) << 8) | frame[p + 23];
					if (delay > 0 || padding > 0)
					{
						EncoderDelay = delay;
						EncoderPadding = padding;
					}
				}

				return true;
			}

			return 36 + 4 <= frame.Length && AudioDecoder.HasTag(frame, 36, "VBRI");
		}

		private static bool IsLetter(byte b)
		{
			return (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z');
		}

		#endregion
	}

	/// <summary>One MPEG frame's bytes for NLayer's <see cref="MpegFrameDecoder" />: the header decoded, the rest read bit by bit.</summary>
	internal sealed class MpegFrameData : IMpegFrame
	{
		#region PublicFields

		public int SampleRate => header.SampleRate;
		public int SampleRateIndex => header.SampleRateIndex;
		public int FrameLength => length;
		public int BitRate => header.BitRate;
		public MpegVersion Version => header.Version;
		public MpegLayer Layer => header.Layer;
		public MpegChannelMode ChannelMode => header.ChannelMode;
		public int ChannelModeExtension => header.ChannelModeExtension;
		public int SampleCount => header.SampleCount;
		public int BitRateIndex => header.BitRateIndex;
		public bool IsCopyrighted => header.IsCopyrighted;
		public bool HasCrc => header.HasCrc;
		public bool IsCorrupted => false;

		#endregion

		#region PrivateFields

		private byte[] data = new byte[4096];
		private int length;
		private MpegHeader header;
		private int readOffset;
		private ulong bitBucket;
		private int bitsRead;

		#endregion

		#region PublicMethods

		/// <summary>Reads the frame of <paramref name="frameLength" /> bytes at <paramref name="offset" />; throws <see cref="EndOfStreamException" /> when the stream ends inside it.</summary>
		public void Load(Stream stream, long offset, int frameLength)
		{
			if (data.Length < frameLength)
			{
				data = new byte[Math.Max(frameLength, data.Length * 2)];
			}

			if (AudioDecoder.ReadAt(stream, offset, new Span<byte>(data, 0, frameLength)) < frameLength)
			{
				throw new EndOfStreamException("MP3: the stream ends inside a frame");
			}

			if (!MpegHeader.TryParse(BinaryPrimitives.ReadUInt32BigEndian(data), out header))
			{
				throw new InvalidDataException("MP3: bad frame header");
			}

			length = frameLength;
			Reset();
		}

		public void Reset()
		{
			readOffset = 4 + (header.HasCrc ? 2 : 0);
			bitBucket = 0;
			bitsRead = 0;
		}

		public int ReadBits(int bitCount)
		{
			if (bitCount < 1 || bitCount > 32)
			{
				throw new ArgumentOutOfRangeException(nameof(bitCount));
			}

			while (bitsRead < bitCount)
			{
				if (readOffset >= length)
				{
					throw new EndOfStreamException("MP3: the frame has no more bits");
				}

				bitBucket = (bitBucket << 8) | data[readOffset++];
				bitsRead += 8;
			}

			int value = (int)((bitBucket >> (bitsRead - bitCount)) & ((1UL << bitCount) - 1));
			bitsRead -= bitCount;
			return value;
		}

		#endregion
	}

	/// <summary>
	///     MPEG audio: the engine's own frame index (<see cref="MpegFrameIndex" />) feeds NLayer's
	///     <see cref="MpegFrameDecoder" /> one frame at a time, so positions are exact frame arithmetic. A frame the
	///     decoder cannot decode (missing bit-reservoir data right after a seek, damage) becomes silence of its length
	///     instead of being dropped. A seek decodes a few frames before the target first (the reservoir and the
	///     filter banks settle), so the output matches a linear decode. With a LAME tag the encoder delay plus the
	///     decoder's 529 samples are skipped and the padding is trimmed (gapless playback).
	/// </summary>
	internal sealed class Mp3StreamDecoder : IAudioStreamDecoder
	{
		#region PublicFields

		public int Channels { get; }
		public int SampleRate { get; }
		public long TotalFrames { get; }
		public long Position => position;

		#endregion

		#region PrivateFields

		private const int MaxFrameFloats = 1152 * 2;

		/// <summary>Bytes of earlier frames decoded before a seek target: the bit reservoir reaches back at most 511.</summary>
		private const int PreRollBytes = 1024;

		private const int PreRollFrames = 2;

		/// <summary>Samples an MPEG layer III decoder's filter banks delay the output by.</summary>
		private const int DecoderDelay = 529;

		private readonly Stream stream;
		private readonly bool ownsStream;
		private readonly long streamStart;
		private readonly MpegFrameIndex index;
		private readonly MpegFrameDecoder decoder = new MpegFrameDecoder();
		private readonly MpegFrameData frame = new MpegFrameData();
		private readonly float[] frameBuffer = new float[MaxFrameFloats];
		private readonly long startSkip;
		private int bufferStart;
		private int bufferEnd;
		private int nextFrame;
		private long position;

		#endregion

		#region Constructors

		public Mp3StreamDecoder(Stream stream, bool ownsStream)
		{
			this.stream = stream;
			this.ownsStream = ownsStream;
			streamStart = stream.Position;
			try
			{
				index = MpegFrameIndex.Scan(stream, streamStart, stream.Length - streamStart);
			}
			catch (Exception e) when (!(e is InvalidDataException))
			{
				throw new InvalidDataException("MP3: " + e.Message, e);
			}

			if (index.Count == 0)
			{
				throw new InvalidDataException("MP3: no complete MPEG audio frames found");
			}

			Channels = index.Format.Channels;
			SampleRate = index.Format.SampleRate;
			long endTrim = 0;
			if (index.EncoderDelay >= 0)
			{
				startSkip = index.EncoderDelay + DecoderDelay;
				endTrim = Math.Max(0, index.EncoderPadding - DecoderDelay);
			}

			TotalFrames = Math.Max(0, (long)index.Count * index.SamplesPerFrame - startSkip - endTrim);
			if (startSkip > 0)
			{
				DiscardSamples(startSkip);
			}
		}

		#endregion

		#region PublicMethods

		public int Read(float[] buffer, int offset, int frames)
		{
			if (buffer == null)
			{
				throw new ArgumentNullException(nameof(buffer));
			}

			if (frames <= 0)
			{
				return 0;
			}

			frames = (int)Math.Min(frames, TotalFrames - position);
			if (frames <= 0)
			{
				return 0;
			}

			if ((long)offset + (long)frames * Channels > buffer.Length)
			{
				throw new ArgumentException("The buffer is too small for " + frames + " frames", nameof(buffer));
			}

			int channels = Channels;
			int done = 0;
			while (done < frames)
			{
				if (bufferStart == bufferEnd && !DecodeNextFrame())
				{
					break;
				}

				int take = Math.Min((bufferEnd - bufferStart) / channels, frames - done);
				Array.Copy(frameBuffer, bufferStart, buffer, offset + done * channels, take * channels);
				bufferStart += take * channels;
				done += take;
			}

			position += done;
			return done;
		}

		public bool Seek(long frame)
		{
			if (frame < 0)
			{
				frame = 0;
			}

			if (frame > TotalFrames)
			{
				frame = TotalFrames;
			}

			int samplesPerFrame = index.SamplesPerFrame;
			long targetSample = frame + startSkip;
			int target = (int)Math.Min(targetSample / samplesPerFrame, index.Count);

			// Decode a run of frames before the target and drop their output: the bit reservoir, the IMDCT overlap
			// and the synthesis filter bank then hold what a linear decode would have.
			int preRoll = target;
			int bytes = 0;
			while (preRoll > 0 && (target - preRoll < PreRollFrames || bytes < PreRollBytes))
			{
				preRoll--;
				bytes += index.Lengths[preRoll];
			}

			decoder.Reset();
			for (int f = preRoll; f < target; f++)
			{
				DecodeFrame(f);
			}

			nextFrame = target;
			bufferStart = 0;
			bufferEnd = 0;
			DiscardSamples(targetSample - (long)target * samplesPerFrame);
			position = frame;
			return true;
		}

		public void Dispose()
		{
			if (ownsStream)
			{
				stream.Dispose();
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>Decodes frame <paramref name="f" /> into the frame buffer; silence when the decoder cannot (returns the floats it gave).</summary>
		private int DecodeFrame(int f)
		{
			int expected = index.SamplesPerFrame * Channels;
			int floats;
			try
			{
				frame.Load(stream, streamStart + index.Offsets[f], index.Lengths[f]);
				floats = decoder.DecodeFrame(frame, frameBuffer, 0);
			}
			catch (EndOfStreamException)
			{
				throw;
			}
			catch (Exception)
			{
				decoder.Reset();
				floats = 0;
			}

			if (floats < expected)
			{
				Array.Clear(frameBuffer, Math.Max(0, floats), expected - Math.Max(0, floats));
			}

			return floats;
		}

		private bool DecodeNextFrame()
		{
			if (nextFrame >= index.Count)
			{
				return false;
			}

			try
			{
				DecodeFrame(nextFrame);
			}
			catch (EndOfStreamException)
			{
				nextFrame = index.Count;
				return false;
			}

			nextFrame++;
			bufferStart = 0;
			bufferEnd = index.SamplesPerFrame * Channels;
			return true;
		}

		/// <summary>Decodes and drops samples (frames of audio) from the current frame on.</summary>
		private void DiscardSamples(long count)
		{
			int channels = Channels;
			while (count > 0)
			{
				if (bufferStart == bufferEnd && !DecodeNextFrame())
				{
					return;
				}

				int take = (int)Math.Min((bufferEnd - bufferStart) / channels, count);
				bufferStart += take * channels;
				count -= take;
			}
		}

		#endregion
	}
}
