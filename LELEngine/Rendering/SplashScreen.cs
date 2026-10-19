using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The player's splash screen (<see cref="SplashScreenSettings" />), drawn with as little GL as there is: a clear
	///     to the background color and a scaled blit of the logo. Nothing compiles, so the first image is up a moment
	///     after the window opens. The build bakes the logo over the background color (<see cref="WriteImage" />), so
	///     nothing blends, into a 24-bit BMP: its bottom-up BGR rows padded to 4 bytes are the layout glTexImage2D takes
	///     as it is. The logo is mipmapped and blitted from the smallest level still as large as its rectangle, so a
	///     logo shown much smaller than baked does not alias.
	///     <see cref="Load" /> only reads the file (any thread); <see cref="Draw" /> and <see cref="Dispose" /> need the
	///     window's GL context.
	/// </summary>
	public sealed class SplashScreen : IDisposable
	{
		#region PublicFields

		/// <summary>File name of the baked logo next to the build's executable.</summary>
		public const string ImageFileName = "Splash.bmp";

		/// <summary>Logos are baked at most this large on their longer side (halved until they fit).</summary>
		public const int MaxImageSize = 2048;

		/// <summary>Seconds the splash screen stays at least (0 without one).</summary>
		public float Duration { get; }

		#endregion

		#region PrivateFields

		private readonly Vector3 background;
		private readonly float imageSize;
		private readonly int imageWidth;
		private readonly int imageHeight;
		private readonly int pixelOffset;
		private byte[] file;
		private int texture;
		private int framebuffer;
		private int levels;

		#endregion

		#region Constructors

		private SplashScreen(float duration, Vector3 background, float imageSize, byte[] file, int pixelOffset, int imageWidth, int imageHeight)
		{
			Duration = duration;
			this.background = background;
			this.imageSize = imageSize;
			this.file = file;
			this.pixelOffset = pixelOffset;
			this.imageWidth = imageWidth;
			this.imageHeight = imageHeight;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     The splash screen of a build's boot.json (<paramref name="config" /> null: black, no minimum duration).
		///     Reads the logo file; one that cannot be read is left out (logged).
		/// </summary>
		public static SplashScreen Load(SplashScreenConfig config, string baseDirectory)
		{
			if (config == null)
			{
				return new SplashScreen(0f, Vector3.Zero, 0f, null, 0, 0, 0);
			}

			float[] color = config.BackgroundColor;
			Vector3 background = color != null && color.Length >= 3
				? new Vector3(Math.Clamp(color[0], 0f, 1f), Math.Clamp(color[1], 0f, 1f), Math.Clamp(color[2], 0f, 1f))
				: Vector3.Zero;

			byte[] data = null;
			int offset = 0;
			int width = 0;
			int height = 0;
			if (!string.IsNullOrEmpty(config.Image))
			{
				string path = Path.Combine(baseDirectory, config.Image);
				try
				{
					data = File.ReadAllBytes(path);
					if (!TryParseBmp(data, out offset, out width, out height))
					{
						Console.WriteLine("[SplashScreen] Not an uncompressed 24-bit BMP, drawn without the logo: " + path);
						data = null;
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("[SplashScreen] Cannot read " + path + " (" + e.Message + "), drawn without the logo");
					data = null;
				}
			}

			return new SplashScreen(Math.Max(0f, config.Duration), background, Math.Clamp(config.ImageSize, 0f, 1f), data, offset, width, height);
		}

		/// <summary>Draws the splash screen into the window's default framebuffer of this size (in pixels); the caller presents it.</summary>
		public void Draw(int width, int height)
		{
			if (width <= 0 || height <= 0)
			{
				return;
			}

			// Whatever the loading left enabled must not clip or mask the clear and the blit.
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			GL.Disable(EnableCap.ScissorTest);
			GL.ColorMask(true, true, true, true);
			GL.Viewport(0, 0, width, height);
			GL.ClearColor(background.X, background.Y, background.Z, 1f);
			GL.Clear(ClearBufferMask.ColorBufferBit);

			if (file != null)
			{
				Upload();
			}

			if (texture == 0 || imageSize <= 0f)
			{
				return;
			}

			float scale = Math.Min(width * imageSize / imageWidth, height * imageSize / imageHeight);
			int w = Math.Max(1, (int)Math.Round(imageWidth * scale));
			int h = Math.Max(1, (int)Math.Round(imageHeight * scale));
			int x = (width - w) / 2;
			int y = (height - h) / 2;

			// The smallest mip level still at least as large as the rectangle: the bilinear blit then skips no texels.
			int level = 0;
			while (level + 1 < levels && Math.Max(1, imageWidth >> (level + 1)) >= w && Math.Max(1, imageHeight >> (level + 1)) >= h)
			{
				level++;
			}

			GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
			GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, texture, level);
			GL.BlitFramebuffer(0, 0, Math.Max(1, imageWidth >> level), Math.Max(1, imageHeight >> level), x, y, x + w, y + h,
				ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		public void Dispose()
		{
			file = null;
			if (framebuffer != 0)
			{
				GL.DeleteFramebuffer(framebuffer);
				framebuffer = 0;
			}

			if (texture != 0)
			{
				GL.DeleteTexture(texture);
				texture = 0;
			}
		}

		/// <summary>
		///     Bakes a splash screen logo: RGBA8 pixels with straight alpha, bottom row first, composited over the
		///     background color (display RGB 0..1), halved until it fits <see cref="MaxImageSize" />, written as a 24-bit
		///     BMP.
		/// </summary>
		public static void WriteImage(string path, int width, int height, byte[] rgba, Vector3 background)
		{
			float r = Math.Clamp(background.X, 0f, 1f) * 255f;
			float g = Math.Clamp(background.Y, 0f, 1f) * 255f;
			float b = Math.Clamp(background.Z, 0f, 1f) * 255f;
			var rgb = new float[width * height * 3];
			for (int i = 0, j = 0; i < width * height; i++, j += 3)
			{
				float a = rgba[i * 4 + 3] / 255f;
				rgb[j] = rgba[i * 4] * a + r * (1f - a);
				rgb[j + 1] = rgba[i * 4 + 1] * a + g * (1f - a);
				rgb[j + 2] = rgba[i * 4 + 2] * a + b * (1f - a);
			}

			while (width > MaxImageSize || height > MaxImageSize)
			{
				rgb = Halve(rgb, ref width, ref height);
			}

			int stride = (width * 3 + 3) & ~3;
			var bmp = new byte[54 + stride * height];
			bmp[0] = (byte)'B';
			bmp[1] = (byte)'M';
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 2, 4), bmp.Length);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 10, 4), 54);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 14, 4), 40);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 18, 4), width);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 22, 4), height);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 26, 2), (short)1);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 28, 2), (short)24);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 34, 4), stride * height);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 38, 4), 2835);
			BitConverter.TryWriteBytes(new Span<byte>(bmp, 42, 4), 2835);
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					int source = (y * width + x) * 3;
					int target = 54 + y * stride + x * 3;
					bmp[target] = (byte)Math.Clamp((int)(rgb[source + 2] + 0.5f), 0, 255);
					bmp[target + 1] = (byte)Math.Clamp((int)(rgb[source + 1] + 0.5f), 0, 255);
					bmp[target + 2] = (byte)Math.Clamp((int)(rgb[source] + 0.5f), 0, 255);
				}
			}

			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
			File.WriteAllBytes(path, bmp);
		}

		#endregion

		#region PrivateMethods

		private void Upload()
		{
			byte[] data = file;
			file = null;

			texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb8, imageWidth, imageHeight, 0, PixelFormat.Bgr, PixelType.UnsignedByte, ref data[pixelOffset]);
			GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
			levels = 1 + (int)Math.Floor(Math.Log2(Math.Max(imageWidth, imageHeight)));
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, levels - 1);
			GL.BindTexture(TextureTarget.Texture2D, 0);

			framebuffer = GL.GenFramebuffer();
		}

		/// <summary>A 24-bit uncompressed bottom-up BMP (what <see cref="WriteImage" /> writes): where its pixels start and its size.</summary>
		private static bool TryParseBmp(byte[] data, out int offset, out int width, out int height)
		{
			offset = 0;
			width = 0;
			height = 0;
			if (data.Length < 54 || data[0] != 'B' || data[1] != 'M')
			{
				return false;
			}

			offset = BitConverter.ToInt32(data, 10);
			width = BitConverter.ToInt32(data, 18);
			height = BitConverter.ToInt32(data, 22);
			int bits = BitConverter.ToInt16(data, 28);
			int compression = BitConverter.ToInt32(data, 30);
			long stride = ((long)width * 3 + 3) & ~3L;
			return bits == 24 && compression == 0 && width > 0 && height > 0 && offset >= 54 && offset + stride * height <= data.Length;
		}

		/// <summary>2x2 box filter of RGB floats (an odd last row or column is dropped).</summary>
		private static float[] Halve(float[] rgb, ref int width, ref int height)
		{
			int w = Math.Max(1, width / 2);
			int h = Math.Max(1, height / 2);
			var result = new float[w * h * 3];
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int x0 = Math.Min(x * 2, width - 1);
					int x1 = Math.Min(x * 2 + 1, width - 1);
					int y0 = Math.Min(y * 2, height - 1);
					int y1 = Math.Min(y * 2 + 1, height - 1);
					for (int c = 0; c < 3; c++)
					{
						result[(y * w + x) * 3 + c] = 0.25f * (rgb[(y0 * width + x0) * 3 + c] + rgb[(y0 * width + x1) * 3 + c]
							+ rgb[(y1 * width + x0) * 3 + c] + rgb[(y1 * width + x1) * 3 + c]);
					}
				}
			}

			width = w;
			height = h;
			return result;
		}

		#endregion
	}
}
