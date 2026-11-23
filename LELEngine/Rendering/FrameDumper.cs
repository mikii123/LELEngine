using System;
using System.IO;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Saves consecutive back-buffer frames as 24-bit BMP files for offline temporal analysis
	///     (every rendered frame, unlike external screen capture which skips frames).
	/// </summary>
	public sealed class FrameDumper
	{
		#region PublicFields

		public bool Active => remaining > 0;
		public int Written { get; private set; }

		#endregion

		#region PrivateFields

		private string directory;
		private int remaining;
		private byte[] pixels;

		#endregion

		#region PublicMethods

		public void Start(string targetDirectory, int frameCount)
		{
			Directory.CreateDirectory(targetDirectory);
			directory = targetDirectory;
			remaining = frameCount;
			Written = 0;
			Console.WriteLine($"[FrameDump] Writing {frameCount} frames to {targetDirectory}");
		}

		/// <summary>
		///     Reads the lower left <paramref name="width" /> x <paramref name="height" /> pixels of a texture (the
		///     window's back buffer, or an offscreen target's first color attachment) and writes them. Call after the
		///     frame is complete, before the buffer swap.
		/// </summary>
		public void Capture(GpuTexture source, int width, int height)
		{
			if (remaining <= 0)
			{
				return;
			}

			int size = PrepareRows(width, height, out int rowBytes);

			// BMP stores blue, green, red; its rows are 4-byte aligned.
			byte[] rgba = TexelConversion.ReadBytes(source, 0, 0, 0, width, height);
			for (int y = 0; y < height; y++)
			{
				int from = y * width * 4;
				int to = y * rowBytes;
				for (int x = 0; x < width; x++, from += 4, to += 3)
				{
					pixels[to] = rgba[from + 2];
					pixels[to + 1] = rgba[from + 1];
					pixels[to + 2] = rgba[from];
				}
			}

			Write(width, height, size);
		}

		/// <summary>
		///     Reads the currently bound framebuffer (the default one's back buffer, or an offscreen target's first
		///     color attachment) and writes it. Call after the frame is complete, before the buffer swap.
		/// </summary>
		// LEGACY(gl): used by Renderer.cs, LELEngine.Editor/MainWindow.cs, Audio/AudioStudioWindow.cs, UIBuilder/BuilderWindow.cs
		public void Capture(int width, int height, bool offscreen = false)
		{
			if (remaining <= 0)
			{
				return;
			}

			if (!offscreen)
			{
				Capture(Gfx.Device.Backbuffer, width, height);
				return;
			}

			// Which texture the caller bound is known to OpenGL only: its pixels are read as OpenGL packs them.
			int size = PrepareRows(width, height, out _);
			GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
			GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
			GL.ReadPixels(0, 0, width, height, PixelFormat.Bgr, PixelType.UnsignedByte, pixels);
			// The device reads and writes texels with rows packed.
			GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
			Write(width, height, size);
		}

		#endregion

		#region PrivateMethods

		/// <summary>Makes <see cref="pixels" /> hold the frame's rows (padding bytes zero); returns their size in bytes.</summary>
		private int PrepareRows(int width, int height, out int rowBytes)
		{
			rowBytes = (width * 3 + 3) & ~3; // BMP rows are 4-byte aligned
			int size = rowBytes * height;
			if (pixels == null || pixels.Length != size)
			{
				pixels = new byte[size];
			}

			return size;
		}

		private void Write(int width, int height, int size)
		{
			string path = Path.Combine(directory, $"frame_{Written:000}.bmp");
			using (FileStream stream = File.Create(path))
			using (BinaryWriter writer = new BinaryWriter(stream))
			{
				// BITMAPFILEHEADER
				writer.Write((ushort)0x4D42);
				writer.Write((uint)(54 + size));
				writer.Write((ushort)0);
				writer.Write((ushort)0);
				writer.Write((uint)54);
				// BITMAPINFOHEADER (positive height = bottom-up rows, which is the order textures are read in)
				writer.Write((uint)40);
				writer.Write(width);
				writer.Write(height);
				writer.Write((ushort)1);
				writer.Write((ushort)24);
				writer.Write((uint)0);
				writer.Write((uint)size);
				writer.Write(2835);
				writer.Write(2835);
				writer.Write((uint)0);
				writer.Write((uint)0);
				writer.Write(pixels);
			}

			Written++;
			remaining--;
			if (remaining == 0)
			{
				Console.WriteLine($"[FrameDump] Done, {Written} frames");
			}
		}

		#endregion
	}
}
