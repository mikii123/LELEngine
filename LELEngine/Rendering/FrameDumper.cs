using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;

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
		///     Reads the currently bound (default) framebuffer and writes it. Call after the frame is complete,
		///     before the buffer swap.
		/// </summary>
		public void Capture(int width, int height)
		{
			if (remaining <= 0)
			{
				return;
			}

			int rowBytes = (width * 3 + 3) & ~3; // BMP rows are 4-byte aligned, as is GL's default pack alignment
			int size = rowBytes * height;
			if (pixels == null || pixels.Length != size)
			{
				pixels = new byte[size];
			}

			GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
			GL.ReadBuffer(ReadBufferMode.Back);
			GL.ReadPixels(0, 0, width, height, PixelFormat.Bgr, PixelType.UnsignedByte, pixels);

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
				// BITMAPINFOHEADER (positive height = bottom-up rows, which is GL's read order)
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
