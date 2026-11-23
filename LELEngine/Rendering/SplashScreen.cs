using System;
using System.IO;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The player's splash screen (<see cref="SplashScreenSettings" />), drawn with as little as there is: a clear
	///     to the background color and a scaled blit of the logo. Nothing compiles, so the first image is up a moment
	///     after the window opens. The build bakes the logo over the background color (<see cref="WriteImage" />), so
	///     nothing blends, into a 24-bit BMP: its rows are bottom-up as a texture's are, their BGR bytes padded to 4
	///     become RGBA texels. The logo is mipmapped and blitted from the smallest level still as large as its
	///     rectangle, so a logo shown much smaller than baked does not alias.
	///     <see cref="Load" /> only reads the file (any thread); <see cref="Draw" /> and <see cref="Dispose" /> need the
	///     window's graphics context.
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
		private GpuTexture texture;
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

		/// <summary>Draws the splash screen into the window's back buffer of this size (in pixels); the caller presents it.</summary>
		public void Draw(int width, int height)
		{
			if (width <= 0 || height <= 0)
			{
				return;
			}

			if (file != null)
			{
				Upload();
			}

			// Whatever the loading left set neither clips nor masks the clear and the blit: a pass begins with the
			// whole target and clears every channel, a blit knows no scissor or mask.
			CommandList commands = Gfx.Commands;
			GpuTexture backbuffer = Gfx.Device.Backbuffer;
			commands.BeginRenderPass(backbuffer, null, LoadOperation.Clear, new ClearValue(background.X, background.Y, background.Z, 1f));
			commands.EndRenderPass();

			if (texture == null || imageSize <= 0f)
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

			commands.Blit(texture, level, 0, 0, 0, texture.MipWidth(level), texture.MipHeight(level), backbuffer, 0, 0, x, y, x + w, y + h, SamplerFilter.Linear);
		}

		public void Dispose()
		{
			file = null;
			texture?.Dispose();
			texture = null;
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

			// The file's rows of blue, green, red (padded to 4 bytes) as packed rows of opaque RGBA texels.
			int stride = (imageWidth * 3 + 3) & ~3;
			byte[] rgba = new byte[imageWidth * imageHeight * 4];
			for (int y = 0; y < imageHeight; y++)
			{
				int from = pixelOffset + y * stride;
				int to = y * imageWidth * 4;
				for (int x = 0; x < imageWidth; x++, from += 3, to += 4)
				{
					rgba[to] = data[from + 2];
					rgba[to + 1] = data[from + 1];
					rgba[to + 2] = data[from];
					rgba[to + 3] = 255;
				}
			}

			// Every mip level, down to one texel. The levels are only blitted from, never sampled by a shader.
			texture = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.RGBA8, imageWidth, imageHeight, 0, TextureUsage.Sampled, "Splash screen logo"));
			SamplerDescriptor sampler = new SamplerDescriptor(SamplerFilter.Linear, SamplerMipFilter.Linear, SamplerAddress.Repeat);
			sampler.Min = SamplerFilter.Nearest;
			texture.Sampler = Gfx.Device.GetSampler(sampler);
			texture.SetData(0, rgba);
			Gfx.Commands.GenerateMipmaps(texture);
			levels = texture.MipLevels;
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
