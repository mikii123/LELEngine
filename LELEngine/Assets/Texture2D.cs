using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     An image: RGBA8 pixels (or RGBA float for HDR sources), stored with the first row at the bottom (GL
	///     convention, how the material importer stores files) or at the top (UI images, runtime textures). Color
	///     textures are stored as sRGB so sampling returns linear values for the HDR pipeline; data textures
	///     (normals, roughness) and UI images stay as they are.
	/// </summary>
	public sealed class Texture2D : Texture
	{
		#region PublicFields

		public override TextureDimension dimension => TextureDimension.Tex2D;

		/// <summary>Stored as sRGB (sampling returns linear color).</summary>
		public bool sRGB { get; private set; }

		/// <summary>Float pixels (HDR source such as .hdr).</summary>
		public bool isHdr => floatPixels != null;

		/// <summary>Row 0 of the pixel data is the bottom row (GL orientation); false for top-down images (UI).</summary>
		public bool bottomUp { get; private set; }

		/// <summary>1x1 white and black textures for materials without a texture assigned.</summary>
		public static Texture2D whiteTexture => white ??= new Texture2D(1, 1, new byte[] { 255, 255, 255, 255 }, "white");

		public static Texture2D blackTexture => black ??= new Texture2D(1, 1, new byte[] { 0, 0, 0, 255 }, "black");

		/// <summary>1x1 flat normal map (0.5, 0.5, 1).</summary>
		public static Texture2D normalTexture => normal ??= new Texture2D(1, 1, new byte[] { 128, 128, 255, 255 }, "normal");

		#endregion

		#region PrivateFields

		private static Texture2D white;
		private static Texture2D black;
		private static Texture2D normal;
		private byte[] pixels;
		private float[] floatPixels;

		#endregion

		#region Constructors

		/// <summary>A texture from RGBA8 pixels (row-major, top row first), as UI code creates them.</summary>
		public Texture2D(int width, int height, byte[] rgba, string name = null, bool mipmaps = false, bool srgb = false)
		{
			if (rgba == null || rgba.Length < width * height * 4)
			{
				throw new ArgumentException("Pixel data too small", nameof(rgba));
			}

			SetSize(width, height);
			pixels = rgba;
			this.name = name ?? "runtime";
			SetMipmaps(mipmaps);
			sRGB = srgb;
		}

		/// <summary>An empty (transparent black) texture to fill with <see cref="SetPixels(int, int, byte[])" /> or <see cref="SetPixel" />.</summary>
		public Texture2D(int width, int height)
			: this(width, height, new byte[Math.Max(1, width) * Math.Max(1, height) * 4], "runtime")
		{ }

		internal Texture2D()
		{ }

		#endregion

		#region PublicMethods

		/// <summary>Replaces the pixels (same or new size); the GL texture is re-uploaded on next use.</summary>
		public void SetPixels(int newWidth, int newHeight, byte[] rgba)
		{
			if (rgba == null || rgba.Length < newWidth * newHeight * 4)
			{
				throw new ArgumentException("Pixel data too small", nameof(rgba));
			}

			SetSize(newWidth, newHeight);
			pixels = rgba;
			floatPixels = null;
			Release();
		}

		/// <summary>Replaces the pixels with float RGBA data (HDR).</summary>
		public void SetPixelsFloat(int newWidth, int newHeight, float[] rgba)
		{
			if (rgba == null || rgba.Length < newWidth * newHeight * 4)
			{
				throw new ArgumentException("Pixel data too small", nameof(rgba));
			}

			SetSize(newWidth, newHeight);
			floatPixels = rgba;
			pixels = null;
			Release();
		}

		/// <summary>The RGBA8 pixels as stored (null for HDR textures); call <see cref="Texture.Apply" /> after editing them.</summary>
		public byte[] GetRawPixels()
		{
			return pixels;
		}

		public float[] GetRawFloatPixels()
		{
			return floatPixels;
		}

		/// <summary>Color of a pixel (0..1 per channel; x right, y up).</summary>
		public Color4 GetPixel(int x, int y)
		{
			if (x < 0 || y < 0 || x >= width || y >= height)
			{
				return default;
			}

			int row = bottomUp ? y : height - 1 - y;
			int index = (row * width + x) * 4;
			if (floatPixels != null)
			{
				return new Color4(floatPixels[index], floatPixels[index + 1], floatPixels[index + 2], floatPixels[index + 3]);
			}

			return pixels != null ? new Color4(pixels[index] / 255f, pixels[index + 1] / 255f, pixels[index + 2] / 255f, pixels[index + 3] / 255f) : default;
		}

		/// <summary>Sets a pixel (x right, y up); call <see cref="Texture.Apply" /> when done.</summary>
		public void SetPixel(int x, int y, Color4 color)
		{
			if (x < 0 || y < 0 || x >= width || y >= height)
			{
				return;
			}

			int row = bottomUp ? y : height - 1 - y;
			int index = (row * width + x) * 4;
			if (floatPixels != null)
			{
				floatPixels[index] = color.R;
				floatPixels[index + 1] = color.G;
				floatPixels[index + 2] = color.B;
				floatPixels[index + 3] = color.A;
				return;
			}

			if (pixels == null)
			{
				return;
			}

			pixels[index] = (byte)Math.Clamp(color.R * 255f + 0.5f, 0f, 255f);
			pixels[index + 1] = (byte)Math.Clamp(color.G * 255f + 0.5f, 0f, 255f);
			pixels[index + 2] = (byte)Math.Clamp(color.B * 255f + 0.5f, 0f, 255f);
			pixels[index + 3] = (byte)Math.Clamp(color.A * 255f + 0.5f, 0f, 255f);
		}

		/// <summary>Loads an image file (absolute path or asset path) for the UI: top row first, gamma storage, mipmaps; null when it cannot be read.</summary>
		public static Texture2D Load(string path, string assetPath = null)
		{
			return TextureAssets.LoadForUI(path, assetPath);
		}

		#endregion

		#region InternalMethods

		internal override TextureTarget Target => TextureTarget.Texture2D;
		protected override bool HasData => pixels != null || floatPixels != null;

		/// <summary>Fills this texture from decoded image data and importer settings (asset loading, reimport in place).</summary>
		internal void SetImage(ImageData image, bool srgb, bool bottomUpData, bool mipmaps)
		{
			SetSize(image.Width, image.Height);
			pixels = image.Pixels;
			floatPixels = image.FloatPixels;
			sRGB = srgb;
			bottomUp = bottomUpData;
			SetMipmaps(mipmaps);
			Release();
		}

		protected override void UploadData()
		{
			if (floatPixels != null)
			{
				GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0, PixelFormat.Rgba, PixelType.Float, floatPixels);
				return;
			}

			GL.TexImage2D(TextureTarget.Texture2D, 0, sRGB ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
		}

		#endregion
	}
}
