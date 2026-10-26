using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine
{
	/// <summary>
	///     A KTX 1.1 texture file: the Khronos container of OpenGL texture data, every mip level (and cube face) in the
	///     GL format the driver takes, so loading one is a read and an upload with nothing to decode. Game builds write
	///     their textures as KTX under the images' own file names (BC7 blocks with the mip levels baked, see
	///     <see cref="TextureCompression" />); <see cref="TextureAssets" /> recognises them by the file identifier. Rows
	///     go bottom up (the texture's first row is v = 0, as GL samples it).
	/// </summary>
	public sealed class KtxFile
	{
		#region PublicFields

		public const int CompressedRgbaBptc = 0x8E8C;
		public const int CompressedSrgbAlphaBptc = 0x8E8D;
		public const int Rgba8 = 0x8058;
		public const int Srgb8Alpha8 = 0x8C43;
		public const int Rgba16F = 0x881A;
		public const int Rgba = 0x1908;
		public const int UnsignedByte = 0x1401;
		public const int HalfFloat = 0x140B;

		/// <summary>GL type of the texels; 0 for compressed formats.</summary>
		public int GlType { get; private set; }

		/// <summary>GL format of the texels (GL_RGBA); 0 for compressed formats.</summary>
		public int GlFormat { get; private set; }

		/// <summary>The texture's GL internal format (GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM, GL_RGBA16F, ...).</summary>
		public int GlInternalFormat { get; private set; }

		public int Width { get; private set; }
		public int Height { get; private set; }

		/// <summary>Slices of a 3D texture; 0 for 2D textures and cubemaps.</summary>
		public int Depth { get; private set; }

		/// <summary>6 for a cubemap, 1 otherwise.</summary>
		public int Faces { get; private set; }

		public int Levels { get; private set; }

		public bool IsCompressed => GlType == 0;
		public bool IsSrgb => GlInternalFormat == CompressedSrgbAlphaBptc || GlInternalFormat == Srgb8Alpha8;
		public bool IsHdr => GlInternalFormat == Rgba16F;

		/// <summary>Bytes of the texel data (without the header).</summary>
		public long DataSize { get; private set; }

		#endregion

		#region PrivateFields

		private static readonly byte[] Identifier = { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

		private byte[] data;

		// Per level, per face (or the whole 3D level): where the texels start in data and how many bytes they are.
		private int[][] offsets;
		private int[] sizes;

		#endregion

		#region PublicMethods

		public static bool IsKtx(ReadOnlySpan<byte> header)
		{
			return header.Length >= Identifier.Length && header.Slice(0, Identifier.Length).SequenceEqual(Identifier);
		}

		/// <summary>Whether the file starts with the KTX 1.1 identifier (a texture baked by a build).</summary>
		public static bool IsKtxFile(string path)
		{
			try
			{
				Span<byte> header = stackalloc byte[12];
				using (FileStream stream = File.OpenRead(path))
				{
					return stream.Read(header) == header.Length && IsKtx(header);
				}
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <summary>Parses a KTX 1.1 file (little endian, no array textures); the texels stay in <paramref name="file" />.</summary>
		public static KtxFile Read(byte[] file)
		{
			KtxFile ktx = ReadHeader(file);
			ktx.data = file;
			int position = 64 + BitConverter.ToInt32(file, 60);
			ktx.offsets = new int[ktx.Levels][];
			ktx.sizes = new int[ktx.Levels];
			for (int level = 0; level < ktx.Levels; level++)
			{
				int imageSize = BitConverter.ToInt32(file, position);
				position += 4;
				ktx.sizes[level] = imageSize;
				ktx.offsets[level] = new int[ktx.Faces];
				for (int face = 0; face < ktx.Faces; face++)
				{
					if (position + imageSize > file.Length)
					{
						throw new InvalidDataException("Truncated KTX file");
					}

					ktx.offsets[level][face] = position;
					position += Pad4(imageSize);
				}
			}

			ktx.DataSize = file.Length - 64;
			return ktx;
		}

		/// <summary>
		///     What a KTX 1.1 file holds, from its first 64 bytes: format, size, faces, depth and levels, without texels (a
		///     scene load shapes the texture from it and reads the file on a worker thread, TextureImportBatch).
		/// </summary>
		public static KtxFile ReadHeader(ReadOnlySpan<byte> header)
		{
			if (!IsKtx(header) || header.Length < 64)
			{
				throw new InvalidDataException("Not a KTX 1.1 file");
			}

			if (BitConverter.ToUInt32(header.Slice(12)) != 0x04030201)
			{
				throw new InvalidDataException("Big-endian KTX files are not supported");
			}

			if (BitConverter.ToInt32(header.Slice(48)) > 0)
			{
				throw new InvalidDataException("KTX array textures are not supported");
			}

			return new KtxFile
			{
				GlType = BitConverter.ToInt32(header.Slice(16)),
				GlFormat = BitConverter.ToInt32(header.Slice(24)),
				GlInternalFormat = BitConverter.ToInt32(header.Slice(28)),
				Width = BitConverter.ToInt32(header.Slice(36)),
				Height = Math.Max(1, BitConverter.ToInt32(header.Slice(40))),
				Depth = BitConverter.ToInt32(header.Slice(44)),
				Faces = Math.Max(1, BitConverter.ToInt32(header.Slice(52))),
				Levels = Math.Max(1, BitConverter.ToInt32(header.Slice(56)))
			};
		}

		/// <summary>
		///     Writes a KTX 1.1 file: <paramref name="levels" />[level][face] holds each mip level's texels (one entry
		///     per level for 2D and 3D textures, six for a cubemap).
		/// </summary>
		public static byte[] Write(int glType, int glFormat, int glInternalFormat, int width, int height, int depth, IReadOnlyList<byte[][]> levels)
		{
			int faces = levels[0].Length;
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream))
			{
				writer.Write(Identifier);
				writer.Write(0x04030201u);
				writer.Write(glType);
				writer.Write(glType == 0 ? 1 : glType == HalfFloat ? 2 : 1);
				writer.Write(glFormat);
				writer.Write(glInternalFormat);
				writer.Write(Rgba);
				writer.Write(width);
				writer.Write(height);
				writer.Write(depth);
				writer.Write(0);
				writer.Write(faces);
				writer.Write(levels.Count);

				// One key: rows go bottom up (KTX's default orientation is top down).
				byte[] key = System.Text.Encoding.ASCII.GetBytes("KTXorientation\0S=r,T=u\0");
				int keySize = 4 + Pad4(key.Length);
				writer.Write(keySize);
				writer.Write(key.Length);
				writer.Write(key);
				writer.Write(new byte[Pad4(key.Length) - key.Length]);

				foreach (byte[][] level in levels)
				{
					writer.Write(level[0].Length);
					foreach (byte[] face in level)
					{
						writer.Write(face);
						writer.Write(new byte[Pad4(face.Length) - face.Length]);
					}
				}

				writer.Flush();
				return stream.ToArray();
			}
		}

		/// <summary>
		///     Uploads every level to the bound texture of <paramref name="target" /> (Texture2D, TextureCubeMap or
		///     Texture3D) and limits sampling to them.
		/// </summary>
		public void Upload(TextureTarget target)
		{
			for (int level = 0; level < Levels; level++)
			{
				int w = Math.Max(1, Width >> level);
				int h = Math.Max(1, Height >> level);
				for (int face = 0; face < Faces; face++)
				{
					TextureTarget faceTarget = Faces == 6 ? TextureTarget.TextureCubeMapPositiveX + face : target;
					int offset = offsets[level][face];
					if (Depth > 0)
					{
						int d = Math.Max(1, Depth >> level);
						GL.TexImage3D(faceTarget, level, (PixelInternalFormat)GlInternalFormat, w, h, d, 0, (PixelFormat)GlFormat, (PixelType)GlType, ref data[offset]);
					}
					else if (IsCompressed)
					{
						GL.CompressedTexImage2D(faceTarget, level, (InternalFormat)GlInternalFormat, w, h, 0, sizes[level], ref data[offset]);
					}
					else
					{
						GL.TexImage2D(faceTarget, level, (PixelInternalFormat)GlInternalFormat, w, h, 0, (PixelFormat)GlFormat, (PixelType)GlType, ref data[offset]);
					}
				}
			}

			GL.TexParameter(target, TextureParameterName.TextureMaxLevel, Levels - 1);
		}

		/// <summary>The texels of one level and face (uncompressed data: CPU reads such as a sky panorama's conversion).</summary>
		public ReadOnlySpan<byte> GetLevel(int level, int face = 0)
		{
			return new ReadOnlySpan<byte>(data, offsets[level][face], sizes[level]);
		}

		#endregion

		#region PrivateMethods

		private static int Pad4(int size)
		{
			return (size + 3) & ~3;
		}

		#endregion
	}
}
