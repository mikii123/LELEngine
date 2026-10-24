using System;
using System.Threading.Tasks;

namespace LELEngine.Editor
{
	/// <summary>
	///     A fast BC7 encoder for game builds: every block in mode 6 (one RGBA line, 7-bit endpoints with a p-bit each,
	///     4-bit indices), the line from the block's principal axis, refined by least squares over the chosen
	///     indices. Close to the source for most textures (about 1 dB below a multi-mode search) and two orders of
	///     magnitude faster than BCnEncoder's search, which <see cref="TextureCompression.BC7HighQuality" /> uses
	///     instead; blocks encode in parallel.
	/// </summary>
	internal static class Bc7Encoder
	{
		#region PrivateFields

		private static readonly int[] Weights = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

		#endregion

		#region PublicMethods

		/// <summary>BC7 blocks (16 bytes per 4x4 block, rows of blocks in the texels' row order) of RGBA8 texels.</summary>
		public static byte[] Encode(byte[] rgba, int width, int height)
		{
			int blocksX = (width + 3) / 4;
			int blocksY = (height + 3) / 4;
			var output = new byte[blocksX * blocksY * 16];
			Parallel.For(0, blocksY, blockY =>
			{
				Span<float> texels = stackalloc float[64];
				for (int blockX = 0; blockX < blocksX; blockX++)
				{
					// Texels past the edge repeat the last row / column (their decoded values are never sampled).
					for (int y = 0; y < 4; y++)
					{
						int sy = Math.Min(blockY * 4 + y, height - 1);
						for (int x = 0; x < 4; x++)
						{
							int sx = Math.Min(blockX * 4 + x, width - 1);
							int source = (sy * width + sx) * 4;
							int target = (y * 4 + x) * 4;
							texels[target] = rgba[source];
							texels[target + 1] = rgba[source + 1];
							texels[target + 2] = rgba[source + 2];
							texels[target + 3] = rgba[source + 3];
						}
					}

					EncodeBlock(texels, output.AsSpan((blockY * blocksX + blockX) * 16, 16));
				}
			});

			return output;
		}

		#endregion

		#region PrivateMethods

		private static void EncodeBlock(ReadOnlySpan<float> texels, Span<byte> block)
		{
			// Mean and covariance of the 16 RGBA texels.
			Span<float> mean = stackalloc float[4];
			for (int i = 0; i < 16; i++)
			{
				for (int c = 0; c < 4; c++)
				{
					mean[c] += texels[i * 4 + c];
				}
			}

			for (int c = 0; c < 4; c++)
			{
				mean[c] /= 16f;
			}

			Span<float> covariance = stackalloc float[16];
			for (int i = 0; i < 16; i++)
			{
				for (int a = 0; a < 4; a++)
				{
					float da = texels[i * 4 + a] - mean[a];
					for (int b = a; b < 4; b++)
					{
						covariance[a * 4 + b] += da * (texels[i * 4 + b] - mean[b]);
					}
				}
			}

			for (int a = 0; a < 4; a++)
			{
				for (int b = 0; b < a; b++)
				{
					covariance[a * 4 + b] = covariance[b * 4 + a];
				}
			}

			// Principal axis by power iteration, from the widest channel.
			Span<float> axis = stackalloc float[4];
			int widest = 0;
			for (int c = 1; c < 4; c++)
			{
				if (covariance[c * 5] > covariance[widest * 5])
				{
					widest = c;
				}
			}

			axis[widest] = 1f;
			Span<float> next = stackalloc float[4];
			for (int iteration = 0; iteration < 8; iteration++)
			{
				float length = 0f;
				for (int a = 0; a < 4; a++)
				{
					float sum = 0f;
					for (int b = 0; b < 4; b++)
					{
						sum += covariance[a * 4 + b] * axis[b];
					}

					next[a] = sum;
					length += sum * sum;
				}

				if (length < 1e-12f)
				{
					break;
				}

				float inverse = 1f / MathF.Sqrt(length);
				for (int a = 0; a < 4; a++)
				{
					axis[a] = next[a] * inverse;
				}
			}

			// The line's ends: the texels' extreme projections on the axis.
			float low = float.MaxValue;
			float high = float.MinValue;
			for (int i = 0; i < 16; i++)
			{
				float t = 0f;
				for (int c = 0; c < 4; c++)
				{
					t += (texels[i * 4 + c] - mean[c]) * axis[c];
				}

				low = Math.Min(low, t);
				high = Math.Max(high, t);
			}

			Span<float> end0 = stackalloc float[4];
			Span<float> end1 = stackalloc float[4];
			for (int c = 0; c < 4; c++)
			{
				end0[c] = Math.Clamp(mean[c] + axis[c] * low, 0f, 255f);
				end1[c] = Math.Clamp(mean[c] + axis[c] * high, 0f, 255f);
			}

			// Quantize, pick indices, refit the ends to them by least squares; keep the best of a few rounds.
			Span<int> q0 = stackalloc int[4];
			Span<int> q1 = stackalloc int[4];
			Span<byte> indices = stackalloc byte[16];
			Span<int> bestQ0 = stackalloc int[4];
			Span<int> bestQ1 = stackalloc int[4];
			Span<byte> bestIndices = stackalloc byte[16];
			int bestP0 = 0;
			int bestP1 = 0;
			float bestError = float.MaxValue;
			for (int round = 0; round < 3; round++)
			{
				int p0 = Quantize(end0, q0);
				int p1 = Quantize(end1, q1);
				float error = SelectIndices(texels, q0, q1, indices);
				if (error < bestError)
				{
					bestError = error;
					q0.CopyTo(bestQ0);
					q1.CopyTo(bestQ1);
					indices.CopyTo(bestIndices);
					bestP0 = p0;
					bestP1 = p1;
				}

				if (error <= 0f || !Refit(texels, indices, end0, end1))
				{
					break;
				}
			}

			// The anchor (first) index must have its top bit clear: swap the ends and mirror the indices.
			if (bestIndices[0] >= 8)
			{
				for (int c = 0; c < 4; c++)
				{
					(bestQ0[c], bestQ1[c]) = (bestQ1[c], bestQ0[c]);
				}

				(bestP0, bestP1) = (bestP1, bestP0);
				for (int i = 0; i < 16; i++)
				{
					bestIndices[i] = (byte)(15 - bestIndices[i]);
				}
			}

			Pack(bestQ0, bestQ1, bestP0, bestP1, bestIndices, block);
		}

		/// <summary>
		///     An end as 7 bits per channel and one shared p-bit: <paramref name="quantized" /> gets the 8-bit values it
		///     decodes to (c * 2 + p) with the p-bit of the lower error, which is returned. A fully transparent end keeps
		///     alpha 0 (p-bit 0): white with alpha 0, the usual particle texture, would otherwise get alpha 1 (the color's
		///     error is the larger one), and additive or premultiplied blending would draw the texture's whole square
		///     faintly. (Opaque ends are not held at 255: 254 does not show, and odd values only would halve the colors'
		///     precision.)
		/// </summary>
		private static int Quantize(ReadOnlySpan<float> end, Span<int> quantized)
		{
			int bestP = 0;
			float bestError = float.MaxValue;
			Span<int> candidate = stackalloc int[4];
			bool transparent = end[3] < 0.5f;
			for (int p = 0; p < (transparent ? 1 : 2); p++)
			{
				float error = 0f;
				for (int c = 0; c < 4; c++)
				{
					int q = Math.Clamp((int)MathF.Round((end[c] - p) * 0.5f), 0, 127);
					candidate[c] = q * 2 + p;
					float d = candidate[c] - end[c];
					error += d * d;
				}

				if (error < bestError)
				{
					bestError = error;
					bestP = p;
					candidate.CopyTo(quantized);
				}
			}

			return bestP;
		}

		/// <summary>
		///     Each texel's nearest of the 16 colors between the quantized ends; the summed squared error. A fully
		///     transparent texel weighs its alpha error 16 times: it takes a color of alpha 0 where the ends offer one,
		///     even when another is closer in color (its color does not show; any alpha does).
		/// </summary>
		private static float SelectIndices(ReadOnlySpan<float> texels, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, Span<byte> indices)
		{
			Span<float> palette = stackalloc float[64];
			for (int c = 0; c < 4; c++)
			{
				int e0 = q0[c];
				int e1 = q1[c];
				for (int w = 0; w < 16; w++)
				{
					palette[w * 4 + c] = ((64 - Weights[w]) * e0 + Weights[w] * e1 + 32) >> 6;
				}
			}

			float total = 0f;
			for (int i = 0; i < 16; i++)
			{
				float best = float.MaxValue;
				int bestIndex = 0;
				float alpha = texels[i * 4 + 3];
				float alphaWeight = alpha <= 0f ? 16f : 1f;
				for (int w = 0; w < 16; w++)
				{
					float dr = palette[w * 4] - texels[i * 4];
					float dg = palette[w * 4 + 1] - texels[i * 4 + 1];
					float db = palette[w * 4 + 2] - texels[i * 4 + 2];
					float da = palette[w * 4 + 3] - alpha;
					float d = dr * dr + dg * dg + db * db + alphaWeight * da * da;
					if (d < best)
					{
						best = d;
						bestIndex = w;
					}
				}

				indices[i] = (byte)bestIndex;
				total += best;
			}

			return total;
		}

		/// <summary>The ends that best reproduce the texels with these indices (least squares); false when the indices are all equal.</summary>
		private static bool Refit(ReadOnlySpan<float> texels, ReadOnlySpan<byte> indices, Span<float> end0, Span<float> end1)
		{
			float a00 = 0f;
			float a01 = 0f;
			float a11 = 0f;
			Span<float> b0 = stackalloc float[4];
			Span<float> b1 = stackalloc float[4];
			for (int i = 0; i < 16; i++)
			{
				float w = Weights[indices[i]] / 64f;
				float v = 1f - w;
				a00 += v * v;
				a01 += v * w;
				a11 += w * w;
				for (int c = 0; c < 4; c++)
				{
					b0[c] += v * texels[i * 4 + c];
					b1[c] += w * texels[i * 4 + c];
				}
			}

			float determinant = a00 * a11 - a01 * a01;
			if (MathF.Abs(determinant) < 1e-6f)
			{
				return false;
			}

			float inverse = 1f / determinant;
			for (int c = 0; c < 4; c++)
			{
				end0[c] = Math.Clamp((a11 * b0[c] - a01 * b1[c]) * inverse, 0f, 255f);
				end1[c] = Math.Clamp((a00 * b1[c] - a01 * b0[c]) * inverse, 0f, 255f);
			}

			return true;
		}

		/// <summary>Mode 6 bits: mode (6 zeros, a one), R0 R1 G0 G1 B0 B1 A0 A1 (7 bits each), P0 P1, then the indices (the anchor's 3 bits, 15 x 4 bits).</summary>
		private static void Pack(ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, int p0, int p1, ReadOnlySpan<byte> indices, Span<byte> block)
		{
			ulong low = 0;
			ulong high = 0;
			int position = 0;

			void Write(uint value, int bits)
			{
				for (int bit = 0; bit < bits; bit++, position++)
				{
					ulong set = (value >> bit) & 1u;
					if (position < 64)
					{
						low |= set << position;
					}
					else
					{
						high |= set << (position - 64);
					}
				}
			}

			Write(1u << 6, 7);
			for (int c = 0; c < 4; c++)
			{
				Write((uint)(q0[c] >> 1), 7);
				Write((uint)(q1[c] >> 1), 7);
			}

			Write((uint)p0, 1);
			Write((uint)p1, 1);
			Write(indices[0], 3);
			for (int i = 1; i < 16; i++)
			{
				Write(indices[i], 4);
			}

			BitConverter.TryWriteBytes(block.Slice(0, 8), low);
			BitConverter.TryWriteBytes(block.Slice(8, 8), high);
		}

		#endregion
	}
}
