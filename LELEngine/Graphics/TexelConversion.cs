using System;

namespace LELEngine.Graphics
{
	/// <summary>
	///     Texels between what code computes with and what textures store: a texture takes and gives texels of its
	///     own format only (<see cref="GpuTexture.SetData(int, void*, long)" />), so 32 bit floats become halves for a
	///     16F texture here, three components become four, and back.
	/// </summary>
	public static class TexelConversion
	{
		/// <summary>32 bit floats as the 16 bit floats of R16F / RG16F / RGBA16F textures (rounded to the nearest).</summary>
		public static ushort[] ToHalf(ReadOnlySpan<float> values)
		{
			ushort[] halves = new ushort[values.Length];
			ToHalf(values, halves);
			return halves;
		}

		public static void ToHalf(ReadOnlySpan<float> values, Span<ushort> halves)
		{
			for (int i = 0; i < values.Length; i++)
			{
				halves[i] = BitConverter.HalfToUInt16Bits((Half)values[i]);
			}
		}

		/// <summary>The 16 bit floats of a 16F texture as 32 bit floats.</summary>
		public static float[] FromHalf(ReadOnlySpan<ushort> halves)
		{
			float[] values = new float[halves.Length];
			FromHalf(halves, values);
			return values;
		}

		public static void FromHalf(ReadOnlySpan<ushort> halves, Span<float> values)
		{
			for (int i = 0; i < halves.Length; i++)
			{
				values[i] = (float)BitConverter.UInt16BitsToHalf(halves[i]);
			}
		}

		/// <summary>Three bytes a texel as four, the fourth given (255: opaque).</summary>
		public static byte[] ExpandRgb(ReadOnlySpan<byte> rgb, byte alpha = 255)
		{
			int texels = rgb.Length / 3;
			byte[] rgba = new byte[texels * 4];
			for (int i = 0, s = 0, d = 0; i < texels; i++, s += 3, d += 4)
			{
				rgba[d] = rgb[s];
				rgba[d + 1] = rgb[s + 1];
				rgba[d + 2] = rgb[s + 2];
				rgba[d + 3] = alpha;
			}

			return rgba;
		}

		/// <summary>Three floats a texel as four, the fourth given.</summary>
		public static float[] ExpandRgb(ReadOnlySpan<float> rgb, float alpha = 1f)
		{
			int texels = rgb.Length / 3;
			float[] rgba = new float[texels * 4];
			for (int i = 0, s = 0, d = 0; i < texels; i++, s += 3, d += 4)
			{
				rgba[d] = rgb[s];
				rgba[d + 1] = rgb[s + 1];
				rgba[d + 2] = rgb[s + 2];
				rgba[d + 3] = alpha;
			}

			return rgba;
		}

		/// <summary>
		///     Reads a box of a texture as 32 bit floats, four a texel (missing components 0, alpha 1): for 8 bit, 16F
		///     and 32F colour formats and for depth (in red). Waits for the GPU.
		/// </summary>
		public static unsafe float[] ReadFloats(GpuTexture texture, int mip, int x, int y, int z, int width, int height, int depth = 1)
		{
			int texels = width * height * depth;
			float[] result = new float[texels * 4];
			TextureFormat format = texture.Format;
			int components = format.ComponentCount();
			long size = format.LevelSize(width, height, depth);
			byte[] raw = new byte[size];
			fixed (byte* pointer = raw)
			{
				texture.GetData(mip, x, y, z, width, height, depth, pointer, size);
				int stride = format.TexelSize();
				for (int i = 0; i < texels; i++)
				{
					byte* texel = pointer + (long)i * stride;
					float r = 0f, g = 0f, b = 0f, a = 1f;
					switch (format)
					{
						case TextureFormat.R8:
						case TextureFormat.RG8:
						case TextureFormat.RGBA8:
						case TextureFormat.SRGBA8:
							r = texel[0] / 255f;
							g = components > 1 ? texel[1] / 255f : 0f;
							b = components > 2 ? texel[2] / 255f : 0f;
							a = components > 3 ? texel[3] / 255f : 1f;
							break;
						case TextureFormat.R16F:
						case TextureFormat.RG16F:
						case TextureFormat.RGBA16F:
							ushort* halves = (ushort*)texel;
							r = (float)BitConverter.UInt16BitsToHalf(halves[0]);
							g = components > 1 ? (float)BitConverter.UInt16BitsToHalf(halves[1]) : 0f;
							b = components > 2 ? (float)BitConverter.UInt16BitsToHalf(halves[2]) : 0f;
							a = components > 3 ? (float)BitConverter.UInt16BitsToHalf(halves[3]) : 1f;
							break;
						case TextureFormat.R32F:
						case TextureFormat.RG32F:
						case TextureFormat.RGBA32F:
						case TextureFormat.Depth24:
						case TextureFormat.Depth32F:
							float* floats = (float*)texel;
							r = floats[0];
							g = components > 1 ? floats[1] : 0f;
							b = components > 2 ? floats[2] : 0f;
							a = components > 3 ? floats[3] : 1f;
							break;
						default:
							throw new NotSupportedException("Texels of " + format + " are not read as floats.");
					}

					result[i * 4] = r;
					result[i * 4 + 1] = g;
					result[i * 4 + 2] = b;
					result[i * 4 + 3] = a;
				}
			}

			return result;
		}

		/// <summary>
		///     Reads a rectangle of an 8 bit RGBA texture (or a window's back buffer) as bytes, four a texel; other
		///     colour formats are converted (clamped to 0..1). Waits for the GPU.
		/// </summary>
		public static unsafe byte[] ReadBytes(GpuTexture texture, int mip, int x, int y, int width, int height, int z = 0)
		{
			if (texture.Format == TextureFormat.RGBA8 || texture.Format == TextureFormat.SRGBA8)
			{
				byte[] direct = new byte[width * height * 4];
				fixed (byte* pointer = direct)
				{
					texture.GetData(mip, x, y, z, width, height, 1, pointer, direct.Length);
				}

				return direct;
			}

			float[] floats = ReadFloats(texture, mip, x, y, z, width, height);
			byte[] bytes = new byte[floats.Length];
			for (int i = 0; i < floats.Length; i++)
			{
				bytes[i] = (byte)(Math.Clamp(floats[i], 0f, 1f) * 255f + 0.5f);
			}

			return bytes;
		}
	}
}
