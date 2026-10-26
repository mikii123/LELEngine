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
		public bool isHdr => floatPixels != null || Ktx?.IsHdr == true;

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
		private PendingTextureImport pendingImport;

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
			pendingImport = null;
			ClearKtx();
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
			pendingImport = null;
			ClearKtx();
			Release();
		}

		/// <summary>
		///     The RGBA8 pixels as stored (null for HDR textures, and for the compressed textures of game builds); call
		///     <see cref="Texture.Apply" /> after editing them.
		/// </summary>
		public byte[] GetRawPixels()
		{
			EnsureCpuPixels();
			return pixels;
		}

		public float[] GetRawFloatPixels()
		{
			EnsureCpuPixels();
			return floatPixels;
		}

		/// <summary>Color of a pixel (0..1 per channel; x right, y up).</summary>
		public Color4 GetPixel(int x, int y)
		{
			if (x < 0 || y < 0 || x >= width || y >= height)
			{
				return default;
			}

			EnsureCpuPixels();
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

			EnsureCpuPixels();
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

		protected override bool HasData
		{
			get
			{
				pendingImport?.Complete();
				return pixels != null || floatPixels != null;
			}
		}

		/// <summary>The import filling this texture on a worker thread (<see cref="TextureImportBatch" />), or null.</summary>
		internal PendingTextureImport PendingImport => pendingImport;

		/// <summary>Fills this texture from decoded image data and importer settings (asset loading, reimport in place).</summary>
		internal void SetImage(ImageData image, bool srgb, bool bottomUpData, bool mipmaps)
		{
			SetSize(image.Width, image.Height);
			pixels = image.Pixels;
			floatPixels = image.FloatPixels;
			sRGB = srgb;
			bottomUp = bottomUpData;
			SetMipmaps(mipmaps);
			pendingImport = null;
			ClearKtx();
			Release();
		}

		/// <summary>Fills this texture from a build's baked KTX file (rows bottom up; CPU pixels only when asked for and uncompressed).</summary>
		internal void SetBaked(KtxFile ktx, bool mipmaps)
		{
			pixels = null;
			floatPixels = null;
			sRGB = ktx.IsSrgb;
			bottomUp = true;
			pendingImport = null;
			SetKtx(ktx, mipmaps);
		}

		/// <summary>
		///     A texture a scene load fills on a worker thread (<see cref="TextureImportBatch" />): its size, color space and
		///     mipmaps come from the file's header now (rows bottom up, as every imported texture), its texels when
		///     <paramref name="import" /> completes (by the end of the load, or when something needs them first).
		/// </summary>
		internal void SetPendingImport(PendingTextureImport import, int newWidth, int newHeight, bool srgb, bool mipmaps)
		{
			SetSize(newWidth, newHeight);
			pixels = null;
			floatPixels = null;
			sRGB = srgb;
			bottomUp = true;
			SetMipmaps(mipmaps);
			pendingImport = import;
			ClearKtx();
			Release();
		}

		/// <summary>A failed import: the texture stays without texels (it does not upload).</summary>
		internal void ClearPendingImport()
		{
			pendingImport = null;
		}

		/// <summary>
		///     The CPU pixels of a baked texture, from its uncompressed first level (it then uploads from them; compressed
		///     data stays without CPU pixels). A pending import completes first.
		/// </summary>
		private void EnsureCpuPixels()
		{
			pendingImport?.Complete();
			KtxFile ktx = Ktx;
			if (ktx == null || ktx.IsCompressed || pixels != null || floatPixels != null)
			{
				return;
			}

			ReadOnlySpan<byte> level = ktx.GetLevel(0);
			if (ktx.IsHdr)
			{
				ReadOnlySpan<System.Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, System.Half>(level);
				floatPixels = new float[halves.Length];
				for (int i = 0; i < halves.Length; i++)
				{
					floatPixels[i] = (float)halves[i];
				}
			}
			else
			{
				pixels = level.ToArray();
			}

			SetMipmaps(mipmapEnabled || ktx.Levels > 1);
			ClearKtx();
			Release();
		}

		// Immutable storage with every mip level allocated up front, then level 0: GenerateMipmap fills levels that exist.
		// Measured on an RTX 5070 Ti with New Frontier's Planets scene (112 textures, 1.6 GB): 274 ms against 779-868 ms for
		// glTexImage2D, whose GenerateMipmap had to allocate the chain (518-530 ms of it). The handle is never respecified:
		// new pixels release it and the next use makes a new one.
		protected override void UploadData()
		{
			if (width <= 0 || height <= 0)
			{
				// Immutable storage takes no empty texture; glTexImage2D does.
				GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Math.Max(0, width), Math.Max(0, height), 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
				return;
			}

			int levels = mipmapEnabled ? 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height))) : 1;
			if (floatPixels != null)
			{
				GL.TexStorage2D(TextureTarget2d.Texture2D, levels, SizedInternalFormat.Rgba16f, width, height);
				GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.Float, floatPixels);
				return;
			}

			GL.TexStorage2D(TextureTarget2d.Texture2D, levels, sRGB ? SizedInternalFormat.Srgb8Alpha8 : SizedInternalFormat.Rgba8, width, height);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
		}

		#endregion
	}
}
