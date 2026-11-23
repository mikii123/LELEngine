using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Measurement hooks for temporal analysis of the image, read back without stalling the GPU (texels copied into
	///     buffers behind fences, read a few frames later and logged in frame order):
	///     - regions: the mean linear luminance of screen rectangles every frame, "[Regions] frame v0 v1 ...";
	///     - noise: per-pixel temporal statistics of the whole image over a window of frames, "[Noise] ...".
	///     The source is the HDR scene color or the GI's indirect diffuse (the buffer the GI resolve publishes).
	/// </summary>
	public sealed class FrameStatistics
	{
		#region PublicFields

		public enum Source
		{
			/// <summary>The HDR scene color (what the tonemapper reads).</summary>
			SceneColor,

			/// <summary>The GI's indirect diffuse (irradiance / pi) of the lighting camera's view.</summary>
			IndirectDiffuse,

			/// <summary>The geometry prepass's motion vectors (luminance weights of the world-space motion: a sanity check).</summary>
			Motion
		}

		public Source ReadSource = Source.SceneColor;

		/// <summary>Regions are set, a noise window runs or readbacks are still in flight.</summary>
		public bool Active => regions.Count > 0 || noiseRemaining > 0 || pending.Count > 0;

		#endregion

		#region PrivateFields

		// Readbacks in flight; past this many the oldest is waited for, so memory stays bounded and no frame is dropped.
		private const int MaxPending = 4;

		private readonly List<Vector4i> regions = new List<Vector4i>();
		private readonly Queue<Readback> pending = new Queue<Readback>();
		private readonly Stack<Readback> pool = new Stack<Readback>();
		private byte[] texels = new byte[0];
		private float[] scratch = new float[0];

		private int noiseRemaining;
		private int noiseFrames;
		private int noiseWidth;
		private int noiseHeight;
		private double[] noiseSum;
		private double[] noiseSumSquares;
		private double[] noiseFirstHalf;
		private double[] noiseSecondHalf;
		private int noiseCollected;

		#endregion

		#region PublicMethods

		/// <summary>Rectangles (x, y from the bottom left, width, height, in screen pixels) logged every frame; empty stops.</summary>
		public void SetRegions(IReadOnlyList<Vector4i> rects)
		{
			regions.Clear();
			regions.AddRange(rects);
		}

		/// <summary>Collects per-pixel temporal statistics of the next <paramref name="frames" /> frames, then logs them.</summary>
		public void StartNoise(int frames)
		{
			noiseFrames = Math.Max(2, frames);
			noiseRemaining = noiseFrames;
			noiseCollected = 0;
			noiseSum = null;
			Console.WriteLine($"[Noise] Collecting {noiseFrames} frames of {ReadSource}");
		}

		/// <summary>Requests this frame's readbacks and logs the finished ones. Call after the frame is complete.</summary>
		public void Capture(Renderer renderer)
		{
			Collect(false);
			if (regions.Count == 0 && noiseRemaining <= 0)
			{
				return;
			}

			int sourceWidth = renderer.Width;
			int sourceHeight = renderer.Height;
			GpuTexture source = FindSource(renderer, ref sourceWidth, ref sourceHeight);
			if (source == null)
			{
				return;
			}

			Readback readback = pool.Count > 0 ? pool.Pop() : new Readback();
			readback.Frame = renderer.FrameIndex;
			readback.Rects.Clear();
			float scaleX = sourceWidth / (float)renderer.Width;
			float scaleY = sourceHeight / (float)renderer.Height;
			foreach (Vector4i rect in regions)
			{
				readback.Rects.Add(ClampRect(new Vector4i((int)(rect.X * scaleX), (int)(rect.Y * scaleY), Math.Max(1, (int)(rect.Z * scaleX)), Math.Max(1, (int)(rect.W * scaleY))), sourceWidth, sourceHeight));
			}

			readback.FullFrame = noiseRemaining > 0;
			readback.Width = sourceWidth;
			readback.Height = sourceHeight;
			if (readback.FullFrame)
			{
				readback.Rects.Add(new Vector4i(0, 0, sourceWidth, sourceHeight));
				noiseRemaining--;
			}

			int floats = 0;
			foreach (Vector4i rect in readback.Rects)
			{
				floats += rect.Z * rect.W * 4;
			}

			// The buffer takes the texels as the texture stores them; they become floats when they are read.
			readback.Format = source.Format;
			int texelSize = source.Format.TexelSize();
			long bytes = (long)(floats / 4) * texelSize;
			readback.Buffer ??= Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Readback, 0, false, "Frame statistics readback"));
			if (readback.Buffer.Size < bytes)
			{
				readback.Buffer.Allocate(bytes);
			}

			CommandList commands = Gfx.Commands;
			long offset = 0;
			foreach (Vector4i rect in readback.Rects)
			{
				commands.CopyTextureToBuffer(source, 0, rect.X, rect.Y, 0, rect.Z, rect.W, 1, readback.Buffer, offset);
				offset += (long)rect.Z * rect.W * texelSize;
			}

			readback.Floats = floats;
			readback.Fence = Gfx.Device.CreateFence();
			pending.Enqueue(readback);

			if (pending.Count > MaxPending)
			{
				Collect(true);
			}
		}

		/// <summary>Waits for and logs every readback in flight (end of a measurement).</summary>
		public void Flush()
		{
			while (pending.Count > 0)
			{
				Collect(true);
			}
		}

		public void Dispose()
		{
			foreach (Readback readback in pending)
			{
				readback.Delete();
			}

			foreach (Readback readback in pool)
			{
				readback.Delete();
			}

			pending.Clear();
			pool.Clear();
		}

		#endregion

		#region PrivateMethods

		/// <summary>The texture of the source; its size goes to width / height. Null when it does not exist.</summary>
		private GpuTexture FindSource(Renderer renderer, ref int width, ref int height)
		{
			if (ReadSource == Source.SceneColor)
			{
				// The scene's size (super sampled: larger than the window's).
				width = renderer.RenderWidth;
				height = renderer.RenderHeight;
				return renderer.SceneColor.Gpu;
			}

			if (ReadSource == Source.Motion)
			{
				width = renderer.RenderWidth;
				height = renderer.RenderHeight;
				return renderer.GetPass<Passes.GeometryPrepass>()?.Motion?.Gpu;
			}

			GlobalIlluminationSettings gi = Lighting.GI;
			width = Math.Max(1, gi.ResolveWidth);
			height = Math.Max(1, gi.ResolveHeight);
			// LEGACY(gl): Lighting.cs publishes the GI's resolved diffuse as an OpenGL texture name
			// (GlobalIlluminationSettings.ResolvedDiffuse); the device's texture of that name is looked up.
			return gi.ResolveActive && gi.ResolvedDiffuse != 0 ? Graphics.OpenGL.GLDevice.Current.FindTexture(gi.ResolvedDiffuse) : null;
		}

		private static Vector4i ClampRect(Vector4i rect, int width, int height)
		{
			int x = Math.Clamp(rect.X, 0, width - 1);
			int y = Math.Clamp(rect.Y, 0, height - 1);
			int w = Math.Clamp(rect.Z, 1, width - x);
			int h = Math.Clamp(rect.W, 1, height - y);
			return new Vector4i(x, y, w, h);
		}

		/// <summary>Logs the finished readbacks in order; with <paramref name="wait" /> the oldest is waited for.</summary>
		private void Collect(bool wait)
		{
			while (pending.Count > 0)
			{
				Readback readback = pending.Peek();
				bool signaled = wait ? readback.Fence.Wait(1_000_000_000L) : readback.Fence.IsSignaled;
				if (!signaled)
				{
					return;
				}

				pending.Dequeue();
				readback.Fence.Dispose();
				readback.Fence = null;
				Process(readback);
				pool.Push(readback);
				wait = false;
			}
		}

		private void Process(Readback readback)
		{
			if (scratch.Length < readback.Floats)
			{
				scratch = new float[readback.Floats];
			}

			int count = readback.Floats / 4;
			int bytes = count * readback.Format.TexelSize();
			if (texels.Length < bytes)
			{
				texels = new byte[bytes];
			}

			readback.Buffer.GetData(0, new Span<byte>(texels, 0, bytes));
			ToFloats(readback.Format, new ReadOnlySpan<byte>(texels, 0, bytes), new Span<float>(scratch, 0, readback.Floats));

			int regionCount = readback.Rects.Count - (readback.FullFrame ? 1 : 0);
			int offset = 0;
			if (regionCount > 0)
			{
				StringBuilder line = new StringBuilder("[Regions] ");
				line.Append(readback.Frame.ToString(CultureInfo.InvariantCulture));
				for (int i = 0; i < regionCount; i++)
				{
					Vector4i rect = readback.Rects[i];
					int pixels = rect.Z * rect.W;
					double sum = 0;
					for (int p = 0; p < pixels; p++)
					{
						int index = offset + p * 4;
						sum += Luminance(scratch[index], scratch[index + 1], scratch[index + 2]);
					}

					offset += pixels * 4;
					line.Append(' ').Append((sum / pixels).ToString("0.000000", CultureInfo.InvariantCulture));
				}

				Console.WriteLine(line.ToString());
			}

			if (readback.FullFrame)
			{
				AccumulateNoise(readback, offset);
			}
		}

		private void AccumulateNoise(Readback readback, int offset)
		{
			int pixels = readback.Width * readback.Height;
			if (noiseSum == null || noiseWidth != readback.Width || noiseHeight != readback.Height)
			{
				noiseWidth = readback.Width;
				noiseHeight = readback.Height;
				noiseSum = new double[pixels];
				noiseSumSquares = new double[pixels];
				noiseFirstHalf = new double[pixels];
				noiseSecondHalf = new double[pixels];
				noiseCollected = 0;
			}

			bool first = noiseCollected < noiseFrames / 2;
			for (int p = 0; p < pixels; p++)
			{
				int index = offset + p * 4;
				double value = Luminance(scratch[index], scratch[index + 1], scratch[index + 2]);
				noiseSum[p] += value;
				noiseSumSquares[p] += value * value;
				if (first)
				{
					noiseFirstHalf[p] += value;
				}
				else
				{
					noiseSecondHalf[p] += value;
				}
			}

			noiseCollected++;
			if (noiseCollected < noiseFrames)
			{
				return;
			}

			// Pixels that are black on average (sky in the GI buffer) are left out: their statistics say nothing.
			int n = noiseCollected;
			int firstCount = n / 2;
			int secondCount = n - firstCount;
			double meanSum = 0;
			double stdSum = 0;
			double relativeStdSum = 0;
			double driftSum = 0;
			double relativeDriftSum = 0;
			int counted = 0;
			for (int p = 0; p < pixels; p++)
			{
				double mean = noiseSum[p] / n;
				if (mean < 1e-4)
				{
					continue;
				}

				double variance = Math.Max(0.0, noiseSumSquares[p] / n - mean * mean);
				double std = Math.Sqrt(variance);
				double drift = Math.Abs(noiseFirstHalf[p] / firstCount - noiseSecondHalf[p] / secondCount);
				meanSum += mean;
				stdSum += std;
				relativeStdSum += std / mean;
				driftSum += drift;
				relativeDriftSum += drift / mean;
				counted++;
			}

			counted = Math.Max(1, counted);
			Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
				"[Noise] {0} frames of {1}, {2} pixels: mean {3:0.000000}, temporal std {4:0.000000} ({5:0.00}%), half drift {6:0.000000} ({7:0.00}%)",
				n, ReadSource, counted, meanSum / counted, stdSum / counted, 100.0 * relativeStdSum / counted, driftSum / counted, 100.0 * relativeDriftSum / counted));
			noiseSum = null;
		}

		/// <summary>Texels as a texture stores them into four floats each (a format's missing components: 0, alpha 1).</summary>
		private static void ToFloats(TextureFormat format, ReadOnlySpan<byte> source, Span<float> floats)
		{
			int components = format.ComponentCount();
			int count = floats.Length / 4;
			switch (format)
			{
				case TextureFormat.RGBA16F:
					TexelConversion.FromHalf(MemoryMarshal.Cast<byte, ushort>(source), floats);
					return;
				case TextureFormat.RGBA32F:
					MemoryMarshal.Cast<byte, float>(source).CopyTo(floats);
					return;
				case TextureFormat.R16F:
				case TextureFormat.RG16F:
					ReadOnlySpan<ushort> halves = MemoryMarshal.Cast<byte, ushort>(source);
					for (int i = 0; i < count; i++)
					{
						for (int c = 0; c < 4; c++)
						{
							floats[i * 4 + c] = c < components ? (float)BitConverter.UInt16BitsToHalf(halves[i * components + c]) : c == 3 ? 1f : 0f;
						}
					}

					return;
				case TextureFormat.R32F:
				case TextureFormat.RG32F:
					ReadOnlySpan<float> values = MemoryMarshal.Cast<byte, float>(source);
					for (int i = 0; i < count; i++)
					{
						for (int c = 0; c < 4; c++)
						{
							floats[i * 4 + c] = c < components ? values[i * components + c] : c == 3 ? 1f : 0f;
						}
					}

					return;
				case TextureFormat.R8:
				case TextureFormat.RG8:
				case TextureFormat.RGBA8:
				case TextureFormat.SRGBA8:
					for (int i = 0; i < count; i++)
					{
						for (int c = 0; c < 4; c++)
						{
							floats[i * 4 + c] = c < components ? source[i * components + c] / 255f : c == 3 ? 1f : 0f;
						}
					}

					return;
				default:
					throw new NotSupportedException("Texels of " + format + " are not read as floats.");
			}
		}

		private static double Luminance(float r, float g, float b)
		{
			return 0.2126 * r + 0.7152 * g + 0.0722 * b;
		}

		#endregion

		#region NestedTypes

		private sealed class Readback
		{
			public readonly List<Vector4i> Rects = new List<Vector4i>();
			public int Frame;
			public GpuBuffer Buffer;
			public TextureFormat Format;
			public int Floats;
			public bool FullFrame;
			public int Width;
			public int Height;
			public GpuFence Fence;

			public void Delete()
			{
				Fence?.Dispose();
				Fence = null;
				Buffer?.Dispose();
				Buffer = null;
			}
		}

		#endregion
	}
}
