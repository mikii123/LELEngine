using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;

namespace LELEngine.Audio
{
	/// <summary>One sound of a bank: where its data lies in the file and how it is stored.</summary>
	[Serializable]
	public sealed class BankSampleEntry
	{
		#region PublicFields

		public int Id;
		public string Guid;
		public string Path;
		public SampleEncoding Encoding;
		public AudioFileFormat FileFormat;
		public int Channels;
		public int SampleRate;
		public long Frames;

		/// <summary>Position of the data in the bank file (absolute).</summary>
		public long Offset;

		public long Length;

		/// <summary>Decoded while playing from the file; else held in memory.</summary>
		public bool Streaming;

		/// <summary>Encoded data decoded to PCM when the bank loads (<see cref="AudioLoadType.CompressedInMemory" />).</summary>
		public bool DecodeOnLoad;

		/// <summary>Mix to one channel when decoding at load.</summary>
		public bool ForceMono;

		#endregion
	}

	/// <summary>
	///     What a bank holds besides the sample data: its events, and a copy of the project's global parameters and
	///     buses (every bank carries them, so any bank alone sets up the mixer).
	/// </summary>
	[Serializable]
	public sealed class BankDocument
	{
		#region PublicFields

		public string Name = AudioProject.MasterBankName;
		public string BankId = AudioProject.MasterBankId;
		public int Version = 1;
		public List<AudioEvent> Events = new List<AudioEvent>();
		public List<AudioParameter> GlobalParameters = new List<AudioParameter>();
		public List<AudioBus> Buses = new List<AudioBus>();
		public List<BankSampleEntry> Samples = new List<BankSampleEntry>();
		public int MaxVoices = 64;

		/// <summary>Every bank of the project (names and whether they load at start): the master bank's copy tells the player what to load.</summary>
		public List<AudioBank> Banks = new List<AudioBank>();

		/// <summary>The mixer snapshots, in priority order (every bank carries them, like the buses).</summary>
		public List<AudioSnapshot> Snapshots = new List<AudioSnapshot>();

		#endregion
	}

	/// <summary>A bank in memory: its compiled events and the data of its sounds.</summary>
	public sealed class LoadedBank : ISampleResolver
	{
		#region PublicFields

		public string Name;

		/// <summary>The bank file, null for a bank built from the project in the editor.</summary>
		public string FilePath;

		public BankDocument Document;
		public readonly List<CompiledEvent> Events = new List<CompiledEvent>();
		public readonly Dictionary<string, SampleData> SamplesByGuid = new Dictionary<string, SampleData>(StringComparer.Ordinal);
		public readonly Dictionary<string, SampleData> SamplesByPath = new Dictionary<string, SampleData>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Bytes of sample data held in memory.</summary>
		public long MemoryBytes;

		/// <summary>Streamed sounds (not in memory).</summary>
		public int StreamingCount;

		public readonly List<string> Warnings = new List<string>();

		#endregion

		#region PublicMethods

		public SampleData Resolve(AudioClipReference clip)
		{
			if (clip == null || clip.IsEmpty)
			{
				return null;
			}

			SampleData sample;
			if (!string.IsNullOrEmpty(clip.Guid) && SamplesByGuid.TryGetValue(clip.Guid, out sample))
			{
				return sample;
			}

			if (!string.IsNullOrEmpty(clip.Path) && SamplesByPath.TryGetValue(clip.Path.Replace('\\', '/'), out sample))
			{
				return sample;
			}

			return null;
		}

		public void AddSample(string guid, string path, SampleData sample)
		{
			if (!string.IsNullOrEmpty(guid))
			{
				SamplesByGuid[guid] = sample;
			}

			if (!string.IsNullOrEmpty(path))
			{
				SamplesByPath[path.Replace('\\', '/')] = sample;
			}

			if (sample.Streaming)
			{
				StreamingCount++;
			}
			else
			{
				MemoryBytes += sample.Encoding == SampleEncoding.Float32 ? (sample.Float?.LongLength ?? 0) * 4 : sample.Frames * sample.Channels * 2;
			}
		}

		public CompiledEvent FindEvent(string guid)
		{
			foreach (CompiledEvent compiled in Events)
			{
				if (compiled.Id == guid)
				{
					return compiled;
				}
			}

			return null;
		}

		/// <summary>Compiles the document's events against the loaded samples.</summary>
		public void CompileEvents()
		{
			Events.Clear();
			AudioProject stub = StubProject(Document);
			foreach (AudioEvent audioEvent in Document.Events)
			{
				try
				{
					CompiledEvent compiled = EventCompiler.Compile(audioEvent, stub, this);
					compiled.BankName = Name;
					Warnings.AddRange(compiled.Warnings);
					Events.Add(compiled);
				}
				catch (Exception e)
				{
					Warnings.Add($"{audioEvent.Path}: cannot compile ({e.Message})");
				}
			}
		}

		/// <summary>The project view a bank document gives (its globals, buses and one bank).</summary>
		public static AudioProject StubProject(BankDocument document)
		{
			var project = new AudioProject { name = document.Name };
			project.GlobalParameters.AddRange(document.GlobalParameters);
			project.Buses.AddRange(document.Buses);
			project.Snapshots.AddRange(document.Snapshots);
			project.Banks.Add(new AudioBank { Id = document.BankId ?? AudioProject.MasterBankId, Name = document.Name, AutoLoad = true });
			project.MaxVoices = document.MaxVoices;
			if (project.FindBus(AudioProject.MasterBusId) == null)
			{
				project.Buses.Insert(0, new AudioBus { Id = AudioProject.MasterBusId, Name = "Master" });
			}

			return project;
		}

		#endregion
	}

	/// <summary>
	///     The bank file format:
	///     <code>
	///     "LELBANK1" | int32 version | int32 document length | int64 in-memory end | int64 file length
	///     document (binary LELB <see cref="BankDocument" />, sample offsets absolute)
	///     sample data held in memory, each 16-byte aligned (PCM16 or encoded files decoded at load)
	///     sample data streamed from the file (encoded files or PCM16)
	///     </code>
	///     Loading reads the file up to the in-memory end in one read; the in-memory PCM16 sounds are slices of that
	///     memory (no copy); streamed sounds open the file on demand.
	/// </summary>
	public static class AudioBankFile
	{
		#region PublicFields

		public const string Extension = ".bank";
		public const int FormatVersion = 1;
		public static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("LELBANK1");
		public const int HeaderSize = 8 + 4 + 4 + 8 + 8;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Writes a bank: <paramref name="document" />'s sample entries get their offsets; <paramref name="data" />
		///     holds the bytes of each entry (same order). In-memory sounds first, streamed ones last.
		/// </summary>
		public static void Write(string path, BankDocument document, List<byte[]> data)
		{
			if (document.Samples.Count != data.Count)
			{
				throw new ArgumentException("One data block per sample entry is required");
			}

			// The document's size does not depend on the offsets (fixed-size values): lay out, then write.
			foreach (BankSampleEntry entry in document.Samples)
			{
				entry.Offset = 0;
				entry.Length = 0;
			}

			byte[] probe = Documents.WriteObject(document, DocumentFormat.Binary, new SerializationContext { Location = document.Name });
			long position = Align(HeaderSize + probe.Length);
			var order = new List<int>();
			for (int i = 0; i < document.Samples.Count; i++)
			{
				if (!document.Samples[i].Streaming)
				{
					order.Add(i);
				}
			}

			long inMemoryEnd = -1;
			for (int i = 0; i < document.Samples.Count; i++)
			{
				if (document.Samples[i].Streaming)
				{
					if (inMemoryEnd < 0)
					{
						inMemoryEnd = position;
					}

					order.Add(i);
				}
			}

			foreach (int index in order)
			{
				BankSampleEntry entry = document.Samples[index];
				entry.Offset = position;
				entry.Length = data[index].LongLength;
				position = Align(position + entry.Length);
				if (!entry.Streaming)
				{
					inMemoryEnd = position;
				}
			}

			if (inMemoryEnd < 0)
			{
				inMemoryEnd = position;
			}

			byte[] documentBytes = Documents.WriteObject(document, DocumentFormat.Binary, new SerializationContext { Location = document.Name });
			if (documentBytes.Length != probe.Length)
			{
				throw new InvalidOperationException("The bank document changed size between layout and write");
			}

			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
			using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
			{
				var writer = new BinaryWriter(stream);
				writer.Write(Magic);
				writer.Write(FormatVersion);
				writer.Write(documentBytes.Length);
				writer.Write(inMemoryEnd);
				writer.Write(position);
				writer.Write(documentBytes);
				Pad(stream, Align(stream.Position));
				foreach (int index in order)
				{
					BankSampleEntry entry = document.Samples[index];
					Pad(stream, entry.Offset);
					stream.Write(data[index], 0, data[index].Length);
				}

				Pad(stream, position);
			}
		}

		/// <summary>Reads the header and document of a bank without its sample data (listing, validation).</summary>
		public static BankDocument ReadDocument(string path)
		{
			using (FileStream stream = File.OpenRead(path))
			{
				var reader = new BinaryReader(stream);
				ReadHeader(reader, path, out int documentLength, out _, out _);
				byte[] documentBytes = reader.ReadBytes(documentLength);
				return Documents.ReadObject(documentBytes, typeof(BankDocument), null, new SerializationContext { Location = path }) as BankDocument;
			}
		}

		/// <summary>Loads a bank file: one read up to the in-memory end, compressed sounds decoded, events compiled. Any thread.</summary>
		public static LoadedBank Load(string path)
		{
			path = Path.GetFullPath(path);
			byte[] memory;
			int documentLength;
			long inMemoryEnd;
			using (FileStream stream = File.OpenRead(path))
			{
				var reader = new BinaryReader(stream);
				ReadHeader(reader, path, out documentLength, out inMemoryEnd, out long total);
				if (inMemoryEnd > int.MaxValue - 16)
				{
					throw new InvalidDataException("The bank's in-memory part is too large (" + inMemoryEnd + " bytes)");
				}

				stream.Position = 0;
				memory = new byte[inMemoryEnd];
				int read = 0;
				while (read < memory.Length)
				{
					int count = stream.Read(memory, read, memory.Length - read);
					if (count <= 0)
					{
						throw new InvalidDataException("The bank file is shorter than its header says");
					}

					read += count;
				}
			}

			var documentBytes = new byte[documentLength];
			Array.Copy(memory, HeaderSize, documentBytes, 0, documentLength);
			var document = Documents.ReadObject(documentBytes, typeof(BankDocument), null, new SerializationContext { Location = path }) as BankDocument;
			if (document == null)
			{
				throw new InvalidDataException("The bank holds no document");
			}

			var bank = new LoadedBank { Name = document.Name, FilePath = path, Document = document };
			foreach (BankSampleEntry entry in document.Samples)
			{
				SampleData sample;
				try
				{
					sample = CreateSample(entry, memory, path);
				}
				catch (Exception e)
				{
					bank.Warnings.Add($"{entry.Path}: {e.Message}");
					continue;
				}

				bank.AddSample(entry.Guid, entry.Path, sample);
			}

			bank.CompileEvents();
			return bank;
		}

		#endregion

		#region PrivateMethods

		private static void ReadHeader(BinaryReader reader, string path, out int documentLength, out long inMemoryEnd, out long total)
		{
			byte[] magic = reader.ReadBytes(Magic.Length);
			if (!magic.AsSpan().SequenceEqual(Magic))
			{
				throw new InvalidDataException(path + " is not a LELEngine audio bank");
			}

			int version = reader.ReadInt32();
			if (version != FormatVersion)
			{
				throw new InvalidDataException(path + " has bank format version " + version + "; this engine reads version " + FormatVersion);
			}

			documentLength = reader.ReadInt32();
			inMemoryEnd = reader.ReadInt64();
			total = reader.ReadInt64();
			if (documentLength < 0 || inMemoryEnd < HeaderSize + documentLength)
			{
				throw new InvalidDataException(path + " has a damaged header");
			}
		}

		private static SampleData CreateSample(BankSampleEntry entry, byte[] memory, string path)
		{
			var sample = new SampleData
			{
				Name = entry.Path,
				Channels = Math.Max(1, entry.Channels),
				SampleRate = Math.Max(1, entry.SampleRate),
				Frames = entry.Frames,
				Encoding = entry.Encoding,
				Streaming = entry.Streaming,
				FileFormat = entry.FileFormat
			};

			if (entry.Streaming)
			{
				long offset = entry.Offset;
				long length = entry.Length;
				AudioFileFormat format = entry.FileFormat;
				SampleEncoding encoding = entry.Encoding;
				int channels = sample.Channels;
				int rate = sample.SampleRate;
				long frames = entry.Frames;
				sample.OpenStream = () =>
				{
					var stream = new SubStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 15), offset, length);
					return encoding == SampleEncoding.EncodedFile ? AudioDecoder.OpenStream(stream, format, true) : new Pcm16StreamDecoder(stream, channels, rate, frames);
				};
				return sample;
			}

			if (entry.Offset + entry.Length > memory.Length)
			{
				throw new InvalidDataException("sample data outside the in-memory part");
			}

			if (entry.Encoding == SampleEncoding.Pcm16)
			{
				sample.Memory = memory;
				sample.Offset = (int)entry.Offset;
				return sample;
			}

			if (entry.Encoding == SampleEncoding.Float32)
			{
				var floats = new float[entry.Length / 4];
				Buffer.BlockCopy(memory, (int)entry.Offset, floats, 0, (int)entry.Length);
				sample.Float = floats;
				return sample;
			}

			// Encoded file decoded now.
			PcmData pcm = AudioDecoder.Decode(new MemoryStream(memory, (int)entry.Offset, (int)entry.Length, false), entry.FileFormat, entry.ForceMono);
			SampleData decoded = SampleData.FromPcm(entry.Path, pcm);
			decoded.FileFormat = entry.FileFormat;
			return decoded;
		}

		private static long Align(long position)
		{
			return (position + 15) & ~15L;
		}

		private static void Pad(Stream stream, long target)
		{
			while (stream.Position < target)
			{
				stream.WriteByte(0);
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>Streams raw PCM16 from a bank (a streamed sound stored decoded).</summary>
		private sealed class Pcm16StreamDecoder : IAudioStreamDecoder
		{
			private readonly Stream stream;
			private readonly int channels;
			private readonly byte[] bytes = new byte[8192];
			private long position;

			public Pcm16StreamDecoder(Stream stream, int channels, int sampleRate, long frames)
			{
				this.stream = stream;
				this.channels = Math.Max(1, channels);
				SampleRate = sampleRate;
				TotalFrames = frames;
			}

			public int Channels => channels;
			public int SampleRate { get; }
			public long TotalFrames { get; }
			public long Position => position;

			public int Read(float[] buffer, int offset, int frames)
			{
				int bytesPerFrame = channels * 2;
				int wanted = Math.Min(frames, bytes.Length / bytesPerFrame);
				int got = 0;
				int target = wanted * bytesPerFrame;
				while (got < target)
				{
					int count = stream.Read(bytes, got, target - got);
					if (count <= 0)
					{
						break;
					}

					got += count;
				}

				int readFrames = got / bytesPerFrame;
				for (int i = 0; i < readFrames * channels; i++)
				{
					buffer[offset + i] = BitConverter.ToInt16(bytes, i * 2) * (1f / 32768f);
				}

				position += readFrames;
				return readFrames;
			}

			public bool Seek(long frame)
			{
				frame = Math.Clamp(frame, 0, Math.Max(0, TotalFrames));
				stream.Position = frame * channels * 2;
				position = frame;
				return true;
			}

			public void Dispose()
			{
				stream.Dispose();
			}
		}

		#endregion
	}

	/// <summary>
	///     Builds banks from an audio project: the bank document (events of the bank, the globals, the sample table)
	///     and either a bank in memory for the editor (sounds from the project's clips, nothing written) or a bank file
	///     for a game build (sounds stored as their import settings say).
	/// </summary>
	public static class AudioBankCompiler
	{
		#region PublicMethods

		/// <summary>The sounds a bank's events use, one entry per distinct clip (by GUID, else path), with the clip that names each.</summary>
		public static List<AudioClipReference> CollectClips(AudioProject project, AudioBank bank)
		{
			var result = new List<AudioClipReference>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (AudioEvent audioEvent in project.EventsOfBank(bank.Id))
			{
				foreach (AudioClipReference clip in audioEvent.Clips)
				{
					string key = string.IsNullOrEmpty(clip.Guid) ? "path:" + (clip.Path ?? "") : "guid:" + clip.Guid;
					if (seen.Add(key))
					{
						result.Add(clip);
					}
				}
			}

			return result;
		}

		/// <summary>The document of a bank without sample entries (added by the builders).</summary>
		public static BankDocument CreateDocument(AudioProject project, AudioBank bank)
		{
			var document = new BankDocument
			{
				Name = bank.Name,
				BankId = bank.Id,
				MaxVoices = project.MaxVoices
			};
			foreach (AudioEvent audioEvent in project.EventsOfBank(bank.Id))
			{
				document.Events.Add(EventCompiler.CloneEvent(audioEvent));
			}

			foreach (AudioParameter parameter in project.GlobalParameters)
			{
				document.GlobalParameters.Add(parameter);
			}

			foreach (AudioBus bus in project.BusesInTreeOrder())
			{
				document.Buses.Add(bus);
			}

			foreach (AudioBank other in project.Banks)
			{
				document.Banks.Add(other);
			}

			foreach (AudioSnapshot snapshot in project.Snapshots)
			{
				document.Snapshots.Add(snapshot);
			}

			return document;
		}

		/// <summary>
		///     A bank built in memory from the project's clips (the editor's preview and play mode): nothing is written;
		///     sounds marked streaming stream from their asset files.
		/// </summary>
		public static LoadedBank BuildInMemory(AudioProject project, AudioBank bank)
		{
			BankDocument document = CreateDocument(project, bank);
			var result = new LoadedBank { Name = bank.Name, Document = document };
			foreach (AudioClipReference reference in CollectClips(project, bank))
			{
				AudioClip clip = AudioClip.Load(reference);
				if (clip == null)
				{
					result.Warnings.Add($"missing sound '{reference.DisplayName}'");
					continue;
				}

				SampleData sample = SampleFromClip(clip, out string error);
				if (sample == null)
				{
					result.Warnings.Add($"{clip.AssetPath}: {error}");
					continue;
				}

				result.AddSample(reference.Guid ?? AssetDatabase.GetGuid(clip.AssetPath), clip.AssetPath, sample);
				if (!string.IsNullOrEmpty(reference.Path) && !string.Equals(reference.Path, clip.AssetPath, StringComparison.OrdinalIgnoreCase))
				{
					result.SamplesByPath[reference.Path.Replace('\\', '/')] = sample;
				}
			}

			result.CompileEvents();
			return result;
		}

		/// <summary>Sample data of a project clip as the editor plays it (streaming clips open their file).</summary>
		public static SampleData SampleFromClip(AudioClip clip, out string error)
		{
			error = null;
			AudioImporter importer = clip.Importer;
			if (importer.loadType == AudioLoadType.Streaming)
			{
				if (clip.Error != null || clip.Frames <= 0)
				{
					error = clip.Error ?? "unreadable";
					return null;
				}

				string file = AssetDatabase.ToAbsolute(clip.AssetPath);
				AudioFileFormat format = clip.Format;
				return new SampleData
				{
					Name = clip.AssetPath,
					Channels = Math.Min(2, clip.Channels),
					SampleRate = clip.SampleRate,
					Frames = clip.Frames,
					Encoding = SampleEncoding.EncodedFile,
					Streaming = true,
					FileFormat = format,
					OpenStream = () => AudioDecoder.OpenStream(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 15), format, true)
				};
			}

			PcmData pcm = clip.Pcm;
			if (pcm == null)
			{
				error = clip.Error ?? "cannot decode";
				return null;
			}

			SampleData sample = SampleData.FromPcm(clip.AssetPath, pcm);
			sample.FileFormat = clip.Format;
			return sample;
		}

		/// <summary>
		///     Writes a bank file for a game build: every sound of the bank's events stored as its import settings say
		///     (PCM16 decoded, the encoded file to decode at load, or the encoded file to stream). Returns a summary line.
		/// </summary>
		public static string WriteFile(AudioProject project, AudioBank bank, string path, Action<string> warn)
		{
			BankDocument document = CreateDocument(project, bank);
			foreach (AudioEvent audioEvent in document.Events)
			{
				// Solo is for auditioning in the editor: a build plays every track.
				foreach (AudioTrack track in audioEvent.Tracks)
				{
					track.Solo = false;
				}
			}

			var data = new List<byte[]>();
			long pcmBytes = 0;
			long encodedBytes = 0;
			int streaming = 0;
			foreach (AudioClipReference reference in CollectClips(project, bank))
			{
				AudioClip clip = AudioClip.Load(reference);
				if (clip == null || clip.Error != null)
				{
					warn?.Invoke($"Bank {bank.Name}: the sound '{reference.DisplayName}' is missing or unreadable ({clip?.Error ?? "not found"}); its instruments stay silent.");
					continue;
				}

				AudioImporter importer = clip.Importer;
				var entry = new BankSampleEntry
				{
					Id = document.Samples.Count,
					Guid = reference.Guid ?? AssetDatabase.GetGuid(clip.AssetPath),
					Path = clip.AssetPath,
					FileFormat = clip.Format,
					Channels = Math.Min(2, clip.Channels),
					SampleRate = clip.SampleRate,
					Frames = clip.Frames,
					ForceMono = importer.forceMono
				};

				byte[] bytes;
				switch (importer.loadType)
				{
					case AudioLoadType.Streaming:
						entry.Streaming = true;
						entry.Encoding = SampleEncoding.EncodedFile;
						bytes = File.ReadAllBytes(AssetDatabase.ToAbsolute(clip.AssetPath));
						streaming++;
						encodedBytes += bytes.Length;
						break;
					case AudioLoadType.CompressedInMemory when clip.Format != AudioFileFormat.Wav:
						entry.Encoding = SampleEncoding.EncodedFile;
						entry.DecodeOnLoad = true;
						bytes = File.ReadAllBytes(AssetDatabase.ToAbsolute(clip.AssetPath));
						encodedBytes += bytes.Length;
						break;
					default:
					{
						PcmData pcm = clip.Pcm;
						if (pcm == null)
						{
							warn?.Invoke($"Bank {bank.Name}: cannot decode '{clip.AssetPath}' ({clip.Error}); its instruments stay silent.");
							continue;
						}

						entry.Encoding = SampleEncoding.Pcm16;
						entry.Channels = pcm.Channels;
						entry.SampleRate = pcm.SampleRate;
						entry.Frames = pcm.Frames;
						bytes = ToPcm16(pcm.Samples);
						pcmBytes += bytes.Length;
						break;
					}
				}

				document.Samples.Add(entry);
				data.Add(bytes);
			}

			AudioBankFile.Write(path, document, data);
			long size = new FileInfo(path).Length;
			return $"{bank.Name}{AudioBankFile.Extension}: {document.Events.Count} events, {document.Samples.Count} sounds ({pcmBytes / 1024.0 / 1024.0:0.0} MB PCM, {encodedBytes / 1024.0 / 1024.0:0.0} MB encoded, {streaming} streamed), {size / 1024.0 / 1024.0:0.00} MB";
		}

		#endregion

		#region PrivateMethods

		private static byte[] ToPcm16(float[] samples)
		{
			var bytes = new byte[samples.Length * 2];
			for (int i = 0; i < samples.Length; i++)
			{
				float v = Math.Clamp(samples[i], -1f, 1f);
				short s = (short)Math.Round(v * 32767f);
				bytes[i * 2] = (byte)s;
				bytes[i * 2 + 1] = (byte)(s >> 8);
			}

			return bytes;
		}

		#endregion
	}
}
