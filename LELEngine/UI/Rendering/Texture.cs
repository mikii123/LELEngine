using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using StbImageSharp;

namespace LELEngine
{
	/// <summary>
	///     An image usable by the UI (backgrounds, cursors, <see cref="UIElements.Image" />): loaded from a png / jpg /
	///     tga / bmp asset, or created from pixels at runtime. The GL texture is created on first use, so textures can
	///     be loaded before the GL context exists (asset loading, style sheet parsing). Stored in gamma space and
	///     sampled as-is: the UI composes in the display's color space.
	/// </summary>
	public sealed class Texture : IAsset
	{
		#region PublicFields

		public string AssetPath { get; private set; }
		public string name;
		public int width { get; private set; }
		public int height { get; private set; }

		/// <summary>Linear filtering (bilinear) when scaled; off for pixel art.</summary>
		public bool filterLinear = true;

		/// <summary>Repeat instead of clamping outside [0, 1] uv (tiled backgrounds drawn with uv > 1).</summary>
		public bool wrapRepeat;

		/// <summary>The GL texture handle; uploads the pixels on first access.</summary>
		public int Handle
		{
			get
			{
				if (handle == 0 && !failed)
				{
					Upload();
				}

				return handle;
			}
		}

		public bool IsValid => !failed && (pixels != null || handle != 0);

		#endregion

		#region PrivateFields

		private byte[] pixels;
		private int handle;
		private bool failed;
		private bool mipmaps;

		#endregion

		#region Constructors

		private Texture()
		{ }

		/// <summary>A texture from RGBA8 pixels (row-major, top row first).</summary>
		public Texture(int width, int height, byte[] rgba, string name = null, bool mipmaps = false)
		{
			if (rgba == null || rgba.Length < width * height * 4)
			{
				throw new ArgumentException("Pixel data too small", nameof(rgba));
			}

			this.width = width;
			this.height = height;
			pixels = rgba;
			this.name = name ?? "runtime";
			this.mipmaps = mipmaps;
		}

		#endregion

		#region PublicMethods

		/// <summary>Loads an image file (absolute path or asset path); null when it cannot be read.</summary>
		public static Texture Load(string path, string assetPath = null)
		{
			try
			{
				ImageResult image;
				StbImage.stbi_set_flip_vertically_on_load(0);
				using (FileStream stream = File.OpenRead(path))
				{
					image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
				}

				return new Texture(image.Width, image.Height, image.Data, Path.GetFileName(path), true) { AssetPath = assetPath };
			}
			catch (Exception e)
			{
				Debug.LogWarning("[UI] Cannot load texture " + path + ": " + e.Message);
				return null;
			}
		}

		/// <summary>Replaces the pixels (same or new size); the GL texture is re-uploaded on next use.</summary>
		public void SetPixels(int newWidth, int newHeight, byte[] rgba)
		{
			width = newWidth;
			height = newHeight;
			pixels = rgba;
			failed = false;
			if (handle != 0)
			{
				GL.DeleteTexture(handle);
				handle = 0;
			}
		}

		public void Release()
		{
			if (handle != 0)
			{
				GL.DeleteTexture(handle);
				handle = 0;
			}
		}

		public override string ToString()
		{
			return "Texture(" + (AssetPath ?? name) + " " + width + "x" + height + ")";
		}

		#endregion

		#region PrivateMethods

		private void Upload()
		{
			if (pixels == null)
			{
				failed = true;
				return;
			}

			try
			{
				handle = GL.GenTexture();
				GL.BindTexture(TextureTarget.Texture2D, handle);
				GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
				GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
				GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(filterLinear ? (mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear) : TextureMinFilter.Nearest));
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)(filterLinear ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)(wrapRepeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)(wrapRepeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge));
				if (mipmaps)
				{
					GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
				}

				GL.BindTexture(TextureTarget.Texture2D, 0);
				// The pixels stay for re-uploads after a context loss or a project switch; UI images are small.
			}
			catch (Exception e)
			{
				Debug.LogWarning("[UI] Cannot upload texture " + name + ": " + e.Message);
				failed = true;
				handle = 0;
			}
		}

		#endregion
	}
}
