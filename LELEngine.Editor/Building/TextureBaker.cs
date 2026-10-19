using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace LELEngine.Editor
{
	/// <summary>
	///     Bakes image assets for game builds into KTX files the player uploads as they are (<see cref="KtxFile" />): the
	///     import (shape, size, color space) with every mip level computed here (a box filter, in linear light for sRGB
	///     textures), BC7-compressed (<see cref="Bc7Encoder" />, or BCnEncoder's slower search for the high quality
	///     setting) unless the importer asks for no compression (<see cref="TextureCompression" />).
	///     HDR images stay RGBA16F, volumes RGBA8 (their mip levels come from the GPU). Bakes are cached in
	///     Library/TextureCache per asset, valid while the image, its meta file and this baker's version are the same.
	/// </summary>
	internal static class TextureBaker
	{
		#region PrivateFields

		private const int Version = 2;

		private static readonly float[] SrgbToLinear = BuildSrgbTable();

		#endregion

		#region PublicMethods

		/// <summary>The baked KTX file of an image asset; <paramref name="fromCache" /> when an earlier bake was still valid.</summary>
		public static byte[] Bake(ProjectInfo project, string assetPath, out bool fromCache)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			string guid = AssetDatabase.GetGuid(assetPath) ?? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assetPath)))[..32];
			string cacheFile = Path.Combine(project.LibraryPath, "TextureCache", guid + ".ktx");
			string stampFile = cacheFile + ".stamp";
			string stamp = Stamp(absolute);
			if (File.Exists(cacheFile) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
			{
				fromCache = true;
				return File.ReadAllBytes(cacheFile);
			}

			fromCache = false;
			TextureImporter importer = TextureAssets.GetImporter(assetPath);
			ImportedTexture imported = TextureAssets.Import(assetPath, importer) ?? throw new InvalidDataException("the image does not fit its import settings");
			byte[] ktx = Encode(imported, importer.compression);
			Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
			File.WriteAllBytes(cacheFile, ktx);
			File.WriteAllText(stampFile, stamp);
			return ktx;
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

		/// <summary>What the bake depends on: the image (size, time), its meta file (import settings) and this baker.</summary>
		private static string Stamp(string absolute)
		{
			var info = new FileInfo(absolute);
			string meta = absolute + AssetDatabase.MetaExtension;
			string metaHash = File.Exists(meta) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(meta))) : "";
			return $"{Version}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{metaHash}";
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
			var result = new byte[w * h * 4];
			for (int y = 0; y < h; y++)
			{
				int y0 = Math.Min(y * 2, height - 1);
				int y1 = Math.Min(y * 2 + 1, height - 1);
				for (int x = 0; x < w; x++)
				{
					int x0 = Math.Min(x * 2, width - 1);
					int x1 = Math.Min(x * 2 + 1, width - 1);
					int a = (y0 * width + x0) * 4;
					int b = (y0 * width + x1) * 4;
					int c = (y1 * width + x0) * 4;
					int d = (y1 * width + x1) * 4;
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
			}

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
