using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace LELEngine.Volumetrics
{
	/// <summary>
	///     A made cloud on disk (".lelvol"): its density (R8 per voxel) and, when it was lit, its lit voxels (RGBA8) with the
	///     sun they were lit for, both at full resolution only (the mips and the occupancy are made on the GPU after the
	///     upload, in milliseconds). Each is stored in chunks of <see cref="SlicesPerChunk" /> z slices compressed apart
	///     (Deflate), so worker threads write and read the chunks in parallel. The editor writes one after it made a cloud
	///     (Library/VolumeCache), builds ship them (Data/Volumes), and a cloud of the same settings is read instead of made
	///     (<see cref="VolumeCache" />): New Frontier's storm (12.6 M voxels) took ~110-180 ms of the GPU to make and light.
	/// </summary>
	internal sealed class VolumeFile
	{
		#region PublicFields

		/// <summary>
		///     The version of what the kernels make (VolumetricGenerate / VolumetricLight.shader and their inputs): part of
		///     every key, so files made by an older engine are never read for a newer one. Bump it when they change.
		/// </summary>
		public const int GeneratorVersion = 1;

		public const string Extension = ".lelvol";

		/// <summary>The key of the density (<see cref="VolumetricObjectBase.DensityKey" />); the file is named after it.</summary>
		public string DensityKey;

		/// <summary>The key of the lit voxels' settings (<see cref="VolumetricObjectBase.LitKey" />), null when there are none.</summary>
		public string LitKey;

		/// <summary>Towards the sun in the object's local space, as the lit voxels were made for.</summary>
		public Vector3 Sun;

		public Vector3i Resolution;

		/// <summary>X x Y x Z bytes, x fastest.</summary>
		public byte[] Density;

		/// <summary>X x Y x Z x 4 bytes (RGBA), or null.</summary>
		public byte[] Lit;

		#endregion

		#region PrivateFields

		private const string Magic = "LELVOL";
		private const int FormatVersion = 1;
		private const int SlicesPerChunk = 8;

		#endregion

		#region PublicMethods

		public static string FileName(string densityKey)
		{
			return densityKey + Extension;
		}

		/// <summary>Writes the file (aside, then moved in: a reader never sees half of it). Compresses on worker threads.</summary>
		public void Write(string path)
		{
			byte[][] density = Compress(Density, 1);
			byte[][] lit = Lit != null && LitKey != null ? Compress(Lit, 4) : null;
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			string temporary = path + "." + Environment.ProcessId + ".tmp";
			using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
			using (var writer = new BinaryWriter(stream))
			{
				writer.Write(Magic.ToCharArray());
				writer.Write(FormatVersion);
				writer.Write(DensityKey);
				writer.Write(lit != null ? LitKey : "");
				writer.Write(Sun.X);
				writer.Write(Sun.Y);
				writer.Write(Sun.Z);
				writer.Write(Resolution.X);
				writer.Write(Resolution.Y);
				writer.Write(Resolution.Z);
				writer.Write(SlicesPerChunk);
				WriteChunks(writer, density);
				if (lit != null)
				{
					WriteChunks(writer, lit);
				}
			}

			File.Move(temporary, path, true);
		}

		/// <summary>
		///     Reads a file written by <see cref="Write" /> (its chunks unpacked on worker threads), or null when there is none,
		///     it is for another density key or engine version, or it is damaged.
		/// </summary>
		public static VolumeFile Read(string path, string densityKey)
		{
			if (!File.Exists(path))
			{
				return null;
			}

			try
			{
				using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
				using var reader = new BinaryReader(stream);
				if (new string(reader.ReadChars(Magic.Length)) != Magic || reader.ReadInt32() != FormatVersion)
				{
					return null;
				}

				var file = new VolumeFile { DensityKey = reader.ReadString() };
				if (file.DensityKey != densityKey)
				{
					return null;
				}

				string litKey = reader.ReadString();
				file.LitKey = litKey.Length > 0 ? litKey : null;
				file.Sun = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				file.Resolution = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
				int slicesPerChunk = reader.ReadInt32();
				long voxels = (long)file.Resolution.X * file.Resolution.Y * file.Resolution.Z;
				if (voxels <= 0 || voxels > int.MaxValue / 4 || slicesPerChunk <= 0)
				{
					return null;
				}

				file.Density = ReadChunks(reader, file.Resolution, slicesPerChunk, 1);
				if (file.LitKey != null)
				{
					file.Lit = ReadChunks(reader, file.Resolution, slicesPerChunk, 4);
				}

				return file;
			}
			catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is AggregateException)
			{
				Console.WriteLine("[Volumetrics] Cannot read " + path + ": " + e.Message);
				return null;
			}
		}

		/// <summary>The keys and sun of a file, its header only (false when there is none or it is not one).</summary>
		public static bool ReadHeader(string path, out string densityKey, out string litKey, out Vector3 sun)
		{
			densityKey = null;
			litKey = null;
			sun = default;
			if (!File.Exists(path))
			{
				return false;
			}

			try
			{
				using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
				using var reader = new BinaryReader(stream);
				if (new string(reader.ReadChars(Magic.Length)) != Magic || reader.ReadInt32() != FormatVersion)
				{
					return false;
				}

				densityKey = reader.ReadString();
				string lit = reader.ReadString();
				litKey = lit.Length > 0 ? lit : null;
				sun = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				return true;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return false;
			}
		}

		#endregion

		#region PrivateMethods

		private byte[][] Compress(byte[] data, int bytesPerVoxel)
		{
			int sliceBytes = Resolution.X * Resolution.Y * bytesPerVoxel;
			int chunks = (Resolution.Z + SlicesPerChunk - 1) / SlicesPerChunk;
			var result = new byte[chunks][];
			Parallel.For(0, chunks, chunk =>
			{
				int start = chunk * SlicesPerChunk * sliceBytes;
				int length = Math.Min(SlicesPerChunk, Resolution.Z - chunk * SlicesPerChunk) * sliceBytes;
				using var output = new MemoryStream();
				using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, true))
				{
					deflate.Write(data, start, length);
				}

				result[chunk] = output.ToArray();
			});
			return result;
		}

		private static void WriteChunks(BinaryWriter writer, byte[][] chunks)
		{
			writer.Write(chunks.Length);
			foreach (byte[] chunk in chunks)
			{
				writer.Write(chunk.Length);
				writer.Write(chunk);
			}
		}

		private static byte[] ReadChunks(BinaryReader reader, Vector3i resolution, int slicesPerChunk, int bytesPerVoxel)
		{
			int sliceBytes = resolution.X * resolution.Y * bytesPerVoxel;
			int chunkCount = reader.ReadInt32();
			if (chunkCount != (resolution.Z + slicesPerChunk - 1) / slicesPerChunk)
			{
				throw new InvalidDataException("unexpected chunk count");
			}

			var compressed = new byte[chunkCount][];
			for (int i = 0; i < chunkCount; i++)
			{
				compressed[i] = reader.ReadBytes(reader.ReadInt32());
			}

			var data = new byte[(long)sliceBytes * resolution.Z];
			Parallel.For(0, chunkCount, chunk =>
			{
				int start = chunk * slicesPerChunk * sliceBytes;
				int length = Math.Min(slicesPerChunk, resolution.Z - chunk * slicesPerChunk) * sliceBytes;
				using var deflate = new DeflateStream(new MemoryStream(compressed[chunk]), CompressionMode.Decompress);
				int read = 0;
				while (read < length)
				{
					int n = deflate.Read(data, start + read, length - read);
					if (n <= 0)
					{
						throw new InvalidDataException("truncated chunk");
					}

					read += n;
				}
			});
			return data;
		}

		#endregion
	}
}
