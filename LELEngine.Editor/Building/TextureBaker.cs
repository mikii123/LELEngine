using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace LELEngine.Editor
{
	/// <summary>How <see cref="TextureBaker.BakeFile(ProjectInfo,string,TextureImporter,bool,out BakeResult)" /> got its file.</summary>
	internal enum BakeResult
	{
		/// <summary>The Library's bake was current.</summary>
		Current,

		/// <summary>Taken from the library cache server (another editor baked it).</summary>
		Downloaded,

		/// <summary>Baked here.</summary>
		Baked
	}

	/// <summary>
	///     Bakes image assets for game builds into KTX files the player uploads as they are (<see cref="KtxFile" />): the
	///     import (shape, size, color space) with every mip level computed here (a box filter, in linear light for sRGB
	///     textures), BC7-compressed (<see cref="Bc7Encoder" />, or BCnEncoder's slower search for the high quality
	///     setting) unless the importer asks for no compression (<see cref="TextureCompression" />).
	///     HDR images stay RGBA16F, volumes RGBA8 (their mip levels come from the GPU). Bakes are cached in
	///     Library/TextureCache per asset, valid while the image's content, its meta file and this baker's version are the
	///     same. A bake missing there is taken from the library cache server when it has one for the same contents
	///     (<see cref="LibraryCacheServer" />, kind <see cref="ResultKind" />), and a bake made here is sent to it.
	/// </summary>
	internal static class TextureBaker
	{
		#region PublicFields

		/// <summary>The kind of the library cache server's entries that hold image bakes.</summary>
		public const string ResultKind = "textures";

		#endregion

		#region PrivateFields

		// 3 (2026-10-24): BC7 keeps the alpha of fully transparent texels at 0.
		private const int Version = 3;

		private static readonly float[] SrgbToLinear = BuildSrgbTable();

		// One lock per cache file: an asset is baked once however many threads ask (the editor's loads, a build).
		private static readonly ConcurrentDictionary<string, object> Locks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

		// The last stamp and server key per image, with the files' sizes and times they were hashed at (QuickKey).
		private static readonly ConcurrentDictionary<string, (string Quick, string Stamp, string Key)> Stamps = new ConcurrentDictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		/// <summary>The baked KTX file of an image asset; <paramref name="fromCache" /> when an earlier bake was still valid.</summary>
		public static byte[] Bake(ProjectInfo project, string assetPath, out bool fromCache)
		{
			return File.ReadAllBytes(BakeFile(project, assetPath, TextureAssets.GetImporter(assetPath), out fromCache));
		}

		/// <summary>
		///     The cache file of the image asset's current bake, baked now when there is none (<paramref name="importer" />:
		///     its import settings, as its meta file holds them). Any thread; one bake of an asset at a time.
		/// </summary>
		public static string BakeFile(ProjectInfo project, string assetPath, TextureImporter importer, out bool fromCache)
		{
			string file = BakeFile(project, assetPath, importer, true, out BakeResult result);
			fromCache = result != BakeResult.Baked;
			return file;
		}

		/// <summary>
		///     The cache file of the image asset's current bake, made now when there is none: taken from the library cache
		///     server when <paramref name="useServer" /> and it has the bake of these contents, else baked here (and sent to
		///     it). <paramref name="useServer" /> off: a Reimport, which wants the bake made here. Any thread; one bake of an
		///     asset at a time.
		/// </summary>
		public static string BakeFile(ProjectInfo project, string assetPath, TextureImporter importer, bool useServer, out BakeResult result)
		{
			string cacheFile = CacheFile(project, assetPath);
			lock (Lock(cacheFile))
			{
				if (IsCurrent(assetPath, cacheFile))
				{
					result = BakeResult.Current;
					return cacheFile;
				}

				// Taken before the image is read: an image changed during the bake leaves a stale stamp, not a stale bake.
				string stamp = Stamp(assetPath, out string resultKey);
				string quick = QuickKey(assetPath);
				LibraryCacheServer server = LibraryCacheServer.Instance;
				Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
				if (useServer && server.CanFetch && server.TryFetch(ResultKind, resultKey, cacheFile, KtxFile.IsKtxFile))
				{
					WriteReplacing(cacheFile + ".stamp", Encoding.UTF8.GetBytes(stamp + "\n" + quick));
					result = BakeResult.Downloaded;
					return cacheFile;
				}

				ImportedTexture imported = TextureAssets.Import(assetPath, importer) ?? throw new InvalidDataException("the image does not fit its import settings");
				byte[] ktx = Encode(imported, importer.compression);
				WriteReplacing(cacheFile, ktx);
				WriteReplacing(cacheFile + ".stamp", Encoding.UTF8.GetBytes(stamp + "\n" + quick));
				if (server.CanOffer)
				{
					server.Offer(ResultKind, resultKey, cacheFile);
				}

				result = BakeResult.Baked;
				return cacheFile;
			}
		}

		/// <summary>
		///     The cache file of the image asset's bake when it is current (its images, meta file and this baker's version
		///     are those it was baked from), else null; no bake is made. Any thread.
		/// </summary>
		public static string CurrentBake(ProjectInfo project, string assetPath)
		{
			string cacheFile = CacheFile(project, assetPath);
			lock (Lock(cacheFile))
			{
				return IsCurrent(assetPath, cacheFile) ? cacheFile : null;
			}
		}

		/// <summary>
		///     The library cache server's item for the image's bake (the import's prefetch): its key from the image's contents
		///     now, and the install that puts a downloaded bake into the Library with this machine's stamp, under the bake's
		///     lock. Any thread (it hashes the image; <see cref="BakeFile(ProjectInfo,string,TextureImporter,bool,out BakeResult)" />
		///     reuses the hash while the files' sizes and times stay the same).
		/// </summary>
		public static LibraryCacheServer.Item PrefetchItem(ProjectInfo project, string assetPath)
		{
			string cacheFile = CacheFile(project, assetPath);
			string quick = QuickKey(assetPath);
			string stamp = Stamp(assetPath, out string resultKey);
			return new LibraryCacheServer.Item(ResultKind, resultKey, downloaded =>
			{
				if (!KtxFile.IsKtxFile(downloaded))
				{
					return false;
				}

				lock (Lock(cacheFile))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
					File.Move(downloaded, cacheFile, true);
					WriteReplacing(cacheFile + ".stamp", Encoding.UTF8.GetBytes(stamp + "\n" + quick));
				}

				return true;
			}, assetPath);
		}

		/// <summary>Deletes the image asset's bake (Reimport: the next one is made from scratch). Any thread.</summary>
		public static void Forget(ProjectInfo project, string assetPath)
		{
			string cacheFile = CacheFile(project, assetPath);
			lock (Lock(cacheFile))
			{
				File.Delete(cacheFile + ".stamp");
				File.Delete(cacheFile);
			}
		}

		/// <summary>"BC7 sRGB 2048x2048, 12 levels" (for the build log).</summary>
		public static string Describe(KtxFile ktx)
		{
			string format = ktx.GlInternalFormat switch
			{
				KtxFile.CompressedSrgbAlphaBptc => "BC7 sRGB",
				KtxFile.CompressedRgbaBptc => "BC7",
				KtxFile.Srgb8Alpha8 => "RGBA8 sRGB",
				KtxFile.Rgba8 => "RGBA8",
				KtxFile.Rgba16F => "RGBA16F",
				_ => "0x" + ktx.GlInternalFormat.ToString("X4")
			};
			string shape = ktx.Faces == 6 ? " cube" : ktx.Depth > 0 ? "x" + ktx.Depth : "";
			return $"{format} {ktx.Width}x{ktx.Height}{shape}, {ktx.Levels} level{(ktx.Levels == 1 ? "" : "s")}";
		}

		#endregion

		#region PrivateMethods

		private static string CacheFile(ProjectInfo project, string assetPath)
		{
			string guid = AssetDatabase.GetGuid(assetPath) ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assetPath)))[..32];
			return Path.Combine(project.LibraryPath, "TextureCache", guid + ".ktx");
		}

		private static object Lock(string cacheFile)
		{
			return Locks.GetOrAdd(cacheFile, _ => new object());
		}

		/// <summary>
		///     The bake in the cache file is current. Its stamp file holds the stamp of the contents it was baked from and,
		///     on a second line, the sizes and times of those files then: while they are the same the contents are not
		///     read again; when only the times changed (a checkout) the contents decide, and the times are noted anew.
		/// </summary>
		private static bool IsCurrent(string assetPath, string cacheFile)
		{
			string stampFile = cacheFile + ".stamp";
			if (!File.Exists(cacheFile) || !File.Exists(stampFile))
			{
				return false;
			}

			string[] lines = File.ReadAllText(stampFile).Split('\n');
			string quick = QuickKey(assetPath);
			if (lines.Length > 1 && lines[1] == quick)
			{
				return true;
			}

			if (lines[0] != Stamp(assetPath))
			{
				return false;
			}

			WriteReplacing(stampFile, Encoding.UTF8.GetBytes(lines[0] + "\n" + quick));
			return true;
		}

		/// <summary>
		///     What the bake depends on: the image's content, its meta file (import settings) and this baker, and the other
		///     images of a six-file cubemap or a numbered volume. Contents, not file times: a fresh checkout (CI) gives every
		///     file a new time, which made every cached bake stale.
		/// </summary>
		private static string Stamp(string assetPath)
		{
			return Stamp(assetPath, out _);
		}

		/// <param name="resultKey">
		///     The bake's key on the library cache server, from the same contents: the meta file's hash with its line ends as
		///     LF (a checkout may give it CRLF on one machine and LF on another), so every machine finds the same entry.
		/// </param>
		private static string Stamp(string assetPath, out string resultKey)
		{
			// Hashed once per contents: the prefetch takes the key, the bake after it (a miss) the stamp.
			string quick = QuickKey(assetPath);
			if (Stamps.TryGetValue(assetPath, out (string Quick, string Stamp, string Key) known) && known.Quick == quick)
			{
				resultKey = known.Key;
				return known.Stamp;
			}

			string stamp = HashStamp(assetPath, out resultKey);
			Stamps[assetPath] = (quick, stamp, resultKey);
			return stamp;
		}

		private static string HashStamp(string assetPath, out string resultKey)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			string meta = absolute + AssetDatabase.MetaExtension;
			byte[] metaBytes = File.Exists(meta) ? File.ReadAllBytes(meta) : null;
			string metaHash = metaBytes != null ? Convert.ToHexString(SHA256.HashData(metaBytes)) : "";
			string metaTextHash = metaBytes != null ? LibraryCacheServer.TextHash(metaBytes) : "";
			var stamp = new StringBuilder();
			var identity = new StringBuilder("texture");
			using (FileStream image = File.OpenRead(absolute))
			{
				string imageHash = Convert.ToHexString(SHA256.HashData(image));
				stamp.Append($"{Version}|{image.Length}|{imageHash}|{metaHash}");
				identity.Append($"|{Version}|{image.Length}|{imageHash}|{metaTextHash}");
			}

			List<string> sources = TextureAssets.GetSourceFiles(assetPath);
			for (int i = 1; i < sources.Count; i++)
			{
				using FileStream other = File.OpenRead(AssetDatabase.ToAbsolute(sources[i]));
				string part = $"|{other.Length}|{Convert.ToHexString(SHA256.HashData(other))}";
				stamp.Append(part);
				identity.Append(part);
			}

			resultKey = LibraryCacheServer.Key(identity.ToString());
			return stamp.ToString();
		}

		/// <summary>The sizes and last write times of the files <see cref="Stamp" /> reads.</summary>
		private static string QuickKey(string assetPath)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			var key = new StringBuilder();
			key.Append(Version);
			foreach (string file in TextureAssets.GetSourceFiles(assetPath))
			{
				Append(AssetDatabase.ToAbsolute(file));
			}

			Append(absolute + AssetDatabase.MetaExtension);
			return key.ToString();

			void Append(string file)
			{
				var info = new FileInfo(file);
				key.Append('|').Append(info.Exists ? info.Length : -1).Append(':').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
			}
		}

		/// <summary>Writes a file whole or not at all: a reader on another thread never sees a part of it.</summary>
		private static void WriteReplacing(string file, byte[] bytes)
		{
			string temporary = file + "." + Environment.CurrentManagedThreadId + ".tmp";
			File.WriteAllBytes(temporary, bytes);
			File.Move(temporary, file, true);
		}

		private static byte[] Encode(ImportedTexture imported, TextureCompression compression)
		{
			bool compress = compression != TextureCompression.None;
			if (imported.Shape == TextureImporterShape.Texture3D)
			{
				// Volumes upload uncompressed; the GPU generates their mip levels.
				return KtxFile.Write(KtxFile.UnsignedByte, KtxFile.Rgba, imported.Srgb ? KtxFile.Srgb8Alpha8 : KtxFile.Rgba8,
					imported.Width, imported.Height, imported.Depth, new[] { new[] { imported.Voxels } });
			}

			ImageData[] faces = imported.Shape == TextureImporterShape.Cube ? imported.Faces : new[] { imported.Image };
			int width = faces[0].Width;
			int height = faces[0].Height;
			int levelCount = imported.Mipmaps ? 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height))) : 1;
			var levels = new List<byte[][]>(levelCount);
			for (int level = 0; level < levelCount; level++)
			{
				levels.Add(new byte[faces.Length][]);
			}

			// The engine's mode 6 encoder by default; BCnEncoder's search over every mode for the high quality setting.
			BcEncoder searchEncoder = null;
			if (compression == TextureCompression.BC7HighQuality && !imported.IsHdr)
			{
				searchEncoder = new BcEncoder(CompressionFormat.Bc7);
				searchEncoder.OutputOptions.GenerateMipMaps = false;
				searchEncoder.OutputOptions.Quality = CompressionQuality.Balanced;
				searchEncoder.Options.IsParallel = true;
			}

			bool encodeBc7 = compress && !imported.IsHdr;

			for (int face = 0; face < faces.Length; face++)
			{
				if (imported.IsHdr)
				{
					float[] pixels = faces[face].FloatPixels;
					int w = width;
					int h = height;
					for (int level = 0; level < levelCount; level++)
					{
						levels[level][face] = ToHalfBytes(pixels);
						if (level + 1 < levelCount)
						{
							pixels = HalveFloat(pixels, ref w, ref h);
						}
					}
				}
				else
				{
					byte[] pixels = faces[face].Pixels;
					int w = width;
					int h = height;
					for (int level = 0; level < levelCount; level++)
					{
						levels[level][face] = !encodeBc7 ? pixels
							: searchEncoder != null ? searchEncoder.EncodeToRawBytes(pixels, w, h, PixelFormat.Rgba32, 0, out _, out _)
							: Bc7Encoder.Encode(pixels, w, h);
						if (level + 1 < levelCount)
						{
							pixels = Halve(pixels, ref w, ref h, imported.Srgb);
						}
					}
				}
			}

			if (imported.IsHdr)
			{
				return KtxFile.Write(KtxFile.HalfFloat, KtxFile.Rgba, KtxFile.Rgba16F, width, height, 0, levels);
			}

			if (encodeBc7)
			{
				return KtxFile.Write(0, 0, imported.Srgb ? KtxFile.CompressedSrgbAlphaBptc : KtxFile.CompressedRgbaBptc, width, height, 0, levels);
			}

			return KtxFile.Write(KtxFile.UnsignedByte, KtxFile.Rgba, imported.Srgb ? KtxFile.Srgb8Alpha8 : KtxFile.Rgba8, width, height, 0, levels);
		}

		/// <summary>The next mip level of RGBA8 texels: a 2x2 box filter (color averaged in linear light for sRGB data).</summary>
		private static byte[] Halve(byte[] pixels, ref int width, ref int height, bool srgb)
		{
			int w = Math.Max(1, width / 2);
			int h = Math.Max(1, height / 2);
			int sourceWidth = width;
			int sourceHeight = height;
			var result = new byte[w * h * 4];
			// Rows apart (a 4096 x 4096 sRGB level converts 12 M values to and from linear light).
			Parallel.For(0, h, y =>
			{
				int y0 = Math.Min(y * 2, sourceHeight - 1);
				int y1 = Math.Min(y * 2 + 1, sourceHeight - 1);
				for (int x = 0; x < w; x++)
				{
					int x0 = Math.Min(x * 2, sourceWidth - 1);
					int x1 = Math.Min(x * 2 + 1, sourceWidth - 1);
					int a = (y0 * sourceWidth + x0) * 4;
					int b = (y0 * sourceWidth + x1) * 4;
					int c = (y1 * sourceWidth + x0) * 4;
					int d = (y1 * sourceWidth + x1) * 4;
					int target = (y * w + x) * 4;
					for (int channel = 0; channel < 3; channel++)
					{
						if (srgb)
						{
							float linear = 0.25f * (SrgbToLinear[pixels[a + channel]] + SrgbToLinear[pixels[b + channel]] + SrgbToLinear[pixels[c + channel]] + SrgbToLinear[pixels[d + channel]]);
							result[target + channel] = LinearToSrgb(linear);
						}
						else
						{
							result[target + channel] = (byte)((pixels[a + channel] + pixels[b + channel] + pixels[c + channel] + pixels[d + channel] + 2) / 4);
						}
					}

					result[target + 3] = (byte)((pixels[a + 3] + pixels[b + 3] + pixels[c + 3] + pixels[d + 3] + 2) / 4);
				}
			});

			width = w;
			height = h;
			return result;
		}

		private static float[] HalveFloat(float[] pixels, ref int width, ref int height)
		{
			int w = Math.Max(1, width / 2);
			int h = Math.Max(1, height / 2);
			var result = new float[w * h * 4];
			for (int y = 0; y < h; y++)
			{
				int y0 = Math.Min(y * 2, height - 1);
				int y1 = Math.Min(y * 2 + 1, height - 1);
				for (int x = 0; x < w; x++)
				{
					int x0 = Math.Min(x * 2, width - 1);
					int x1 = Math.Min(x * 2 + 1, width - 1);
					for (int channel = 0; channel < 4; channel++)
					{
						result[(y * w + x) * 4 + channel] = 0.25f * (pixels[(y0 * width + x0) * 4 + channel] + pixels[(y0 * width + x1) * 4 + channel]
							+ pixels[(y1 * width + x0) * 4 + channel] + pixels[(y1 * width + x1) * 4 + channel]);
					}
				}
			}

			width = w;
			height = h;
			return result;
		}

		private static byte[] ToHalfBytes(float[] pixels)
		{
			var bytes = new byte[pixels.Length * 2];
			Span<System.Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, System.Half>(bytes.AsSpan());
			for (int i = 0; i < pixels.Length; i++)
			{
				halves[i] = (System.Half)pixels[i];
			}

			return bytes;
		}

		private static float[] BuildSrgbTable()
		{
			var table = new float[256];
			for (int i = 0; i < 256; i++)
			{
				float c = i / 255f;
				table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
			}

			return table;
		}

		private static byte LinearToSrgb(float linear)
		{
			float c = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
			return (byte)Math.Clamp((int)(c * 255f + 0.5f), 0, 255);
		}

		#endregion
	}
}
