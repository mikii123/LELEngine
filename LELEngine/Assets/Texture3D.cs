using System;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine
{
	/// <summary>
	///     A volume texture: width x height x depth RGBA8 texels, slice 0 first. Imported from an image holding the
	///     slices as a grid of tiles (columns x rows, left to right then top to bottom; see <see cref="TextureImporter" />),
	///     or filled by code.
	/// </summary>
	public sealed class Texture3D : Texture
	{
		#region PublicFields

		public override TextureDimension dimension => TextureDimension.Tex3D;
		public int depth { get; private set; }
		public bool sRGB { get; private set; }

		#endregion

		#region PrivateFields

		private byte[] pixels;

		#endregion

		#region Constructors

		/// <summary>A volume from RGBA8 texels (slice by slice, each slice row-major with the first row at the bottom).</summary>
		public Texture3D(int width, int height, int depth, byte[] rgba, string name = null, bool mipmaps = false, bool srgb = false)
		{
			if (rgba == null || rgba.Length < width * height * depth * 4)
			{
				throw new ArgumentException("Pixel data too small", nameof(rgba));
			}

			SetSize(width, height);
			this.depth = depth;
			pixels = rgba;
			this.name = name ?? "runtime";
			SetMipmaps(mipmaps);
			sRGB = srgb;
			SetSamplerState(FilterMode.Bilinear, TextureWrap.Clamp, 1);
		}

		/// <summary>An empty (transparent black) volume to fill with <see cref="SetPixels" />.</summary>
		public Texture3D(int width, int height, int depth)
			: this(width, height, depth, new byte[Math.Max(1, width) * Math.Max(1, height) * Math.Max(1, depth) * 4])
		{ }

		internal Texture3D()
		{ }

		#endregion

		#region PublicMethods

		public void SetPixels(int newWidth, int newHeight, int newDepth, byte[] rgba)
		{
			if (rgba == null || rgba.Length < newWidth * newHeight * newDepth * 4)
			{
				throw new ArgumentException("Pixel data too small", nameof(rgba));
			}

			SetSize(newWidth, newHeight);
			depth = newDepth;
			pixels = rgba;
			Release();
		}

		public byte[] GetRawPixels()
		{
			return pixels;
		}

		/// <summary>One slice of the volume as a 2D texture (previews).</summary>
		public Texture2D GetSlice(int z)
		{
			z = Math.Clamp(z, 0, Math.Max(0, depth - 1));
			var slice = new byte[width * height * 4];
			Array.Copy(pixels, z * slice.Length, slice, 0, slice.Length);
			var texture = new Texture2D(width, height, slice, name + " [" + z + "]", false, sRGB);
			texture.SetImage(new ImageData(width, height, slice), sRGB, true, false);
			return texture;
		}

		#endregion

		#region InternalMethods

		internal override TextureTarget Target => TextureTarget.Texture3D;
		protected override bool HasData => pixels != null;

		internal void SetVolume(int newWidth, int newHeight, int newDepth, byte[] rgba, bool srgb, bool mipmaps)
		{
			SetSize(newWidth, newHeight);
			depth = newDepth;
			pixels = rgba;
			sRGB = srgb;
			SetMipmaps(mipmaps);
			ClearKtx();
			Release();
		}

		/// <summary>Fills this volume from a build's baked KTX file (uncompressed slices).</summary>
		internal void SetBaked(KtxFile ktx, bool mipmaps)
		{
			pixels = null;
			depth = ktx.Depth;
			sRGB = ktx.IsSrgb;
			SetKtx(ktx, mipmaps);
		}

		protected override void UploadData()
		{
			GL.TexImage3D(TextureTarget.Texture3D, 0, sRGB ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8, width, height, depth, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
		}

		#endregion
	}
}
