using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Measurement hooks for temporal analysis of the image, read back without stalling the GPU (pixel pack buffers
	///     behind fences, read a few frames later and logged in frame order):
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
		private int readFramebuffer;
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
			if (!BindSource(renderer, ref sourceWidth, ref sourceHeight))
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

			if (readback.Buffer == 0)
			{
				readback.Buffer = GL.GenBuffer();
			}

			GL.BindBuffer(BufferTarget.PixelPackBuffer, readback.Buffer);
			if (readback.Capacity < floats)
			{
				readback.Capacity = floats;
				GL.BufferData(BufferTarget.PixelPackBuffer, floats * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamRead);
			}

			GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
			int offset = 0;
			foreach (Vector4i rect in readback.Rects)
			{
				GL.ReadPixels(rect.X, rect.Y, rect.Z, rect.W, PixelFormat.Rgba, PixelType.Float, (IntPtr)(offset * sizeof(float)));
				offset += rect.Z * rect.W * 4;
			}

			readback.Floats = floats;
			GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
			GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
			readback.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
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
			if (readFramebuffer != 0)
			{
				GL.DeleteFramebuffer(readFramebuffer);
				readFramebuffer = 0;
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>Binds the source as the read framebuffer; its size goes to width / height. False when it does not exist.</summary>
		private bool BindSource(Renderer renderer, ref int width, ref int height)
		{
			if (ReadSource == Source.SceneColor)
			{
				GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, renderer.SceneTarget.Handle);
				GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
				return true;
			}

			int texture;
			if (ReadSource == Source.Motion)
			{
				texture = renderer.GetPass<Passes.GeometryPrepass>()?.Motion?.Handle ?? 0;
			}
			else
			{
				GlobalIlluminationSettings gi = Lighting.GI;
				texture = gi.ResolveActive ? gi.ResolvedDiffuse : 0;
				width = Math.Max(1, gi.ResolveWidth);
				height = Math.Max(1, gi.ResolveHeight);
			}

			if (texture == 0)
			{
				return false;
			}

			if (readFramebuffer == 0)
			{
				readFramebuffer = GL.GenFramebuffer();
			}

			GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
			GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, 0);
			GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
			return true;
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
				WaitSyncStatus status = GL.ClientWaitSync(readback.Fence, wait ? ClientWaitSyncFlags.SyncFlushCommandsBit : ClientWaitSyncFlags.None, wait ? 1_000_000_000L : 0L);
				if (status != WaitSyncStatus.AlreadySignaled && status != WaitSyncStatus.ConditionSatisfied)
				{
					return;
				}

				pending.Dequeue();
				GL.DeleteSync(readback.Fence);
				readback.Fence = IntPtr.Zero;
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

			GL.BindBuffer(BufferTarget.PixelPackBuffer, readback.Buffer);
			IntPtr data = GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, readback.Floats * sizeof(float), BufferAccessMask.MapReadBit);
			if (data == IntPtr.Zero)
			{
				GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
				return;
			}

			Marshal.Copy(data, scratch, 0, readback.Floats);
			GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
			GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

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
			public int Buffer;
			public int Capacity;
			public int Floats;
			public bool FullFrame;
			public int Width;
			public int Height;
			public IntPtr Fence;

			public void Delete()
			{
				if (Fence != IntPtr.Zero)
				{
					GL.DeleteSync(Fence);
					Fence = IntPtr.Zero;
				}

				if (Buffer != 0)
				{
					GL.DeleteBuffer(Buffer);
					Buffer = 0;
				}
			}
		}

		#endregion
	}
}
