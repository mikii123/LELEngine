using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine
{
	public enum CubemapFace
	{
		PositiveX,
		NegativeX,
		PositiveY,
		NegativeY,
		PositiveZ,
		NegativeZ
	}

	/// <summary>
	///     Six square faces (RGBA8, or RGBA float for HDR sources) in GL order +X, -X, +Y, -Y, +Z, -Z, each stored
	///     with its top row first as the GL cube map convention wants. Imported from a cross, strip or equirectangular
	///     image (<see cref="TextureImporter" />), or filled by code; skyboxes and reflections sample it.
	/// </summary>
	public sealed class Cubemap : Texture
	{
		#region PublicFields

		public override TextureDimension dimension => TextureDimension.Cube;

		/// <summary>Edge length of a face in texels.</summary>
		public int size => width;

		public bool sRGB { get; private set; }
		public bool isHdr => floatFaces != null;

		#endregion

		#region PrivateFields

		private byte[][] faces;
		private float[][] floatFaces;
		private static bool seamlessEnabled;

		#endregion

		#region Constructors

		/// <summary>An empty (black) cubemap to fill with <see cref="SetPixels" />.</summary>
		public Cubemap(int size, string name = null, bool mipmaps = true, bool srgb = false)
		{
			SetSize(size, size);
			faces = new byte[6][];
			for (int i = 0; i < 6; i++)
			{
				faces[i] = new byte[size * size * 4];
				for (int p = 3; p < faces[i].Length; p += 4)
				{
					faces[i][p] = 255;
				}
			}

			this.name = name ?? "runtime";
			SetMipmaps(mipmaps);
			sRGB = srgb;
			SetSamplerState(FilterMode.Trilinear, TextureWrap.Clamp, 1);
		}

		internal Cubemap()
		{ }

		#endregion

		#region PublicMethods

		/// <summary>Replaces one face (RGBA8, size x size, top row first).</summary>
		public void SetPixels(CubemapFace face, byte[] rgba)
		{
			if (rgba == null || rgba.Length < size * size * 4)
			{
				throw new ArgumentException("Pixel data too small", nameof(rgba));
			}

			if (faces == null)
			{
				faces = new byte[6][];
				floatFaces = null;
			}

			faces[(int)face] = rgba;
			Release();
		}

		public byte[] GetPixels(CubemapFace face)
		{
			return faces?[(int)face];
		}

		public float[] GetFloatPixels(CubemapFace face)
		{
			return floatFaces?[(int)face];
		}

		/// <summary>
		///     A cubemap shaded by a function of the world direction (procedural skies, test environments): linear
		///     colors, stored as float (HDR) so values above 1 survive.
		/// </summary>
		public static Cubemap Generate(int size, Func<Vector3, Color4> shade, string name = "generated", bool mipmaps = true)
		{
			size = Math.Max(1, size);
			var faces = new float[6][];
			for (int face = 0; face < 6; face++)
			{
				var pixels = new float[size * size * 4];
				for (int y = 0; y < size; y++)
				{
					float b = (y + 0.5f) / size * 2f - 1f;
					for (int x = 0; x < size; x++)
					{
						float a = (x + 0.5f) / size * 2f - 1f;
						Color4 color = shade(TextureAssets.FaceDirection(face, a, b).Normalized());
						int index = (y * size + x) * 4;
						pixels[index] = color.R;
						pixels[index + 1] = color.G;
						pixels[index + 2] = color.B;
						pixels[index + 3] = color.A;
					}
				}

				faces[face] = pixels;
			}

			var cubemap = new Cubemap { name = name };
			cubemap.SetFaces(size, null, faces, false, mipmaps);
			cubemap.SetSamplerState(FilterMode.Trilinear, TextureWrap.Clamp, 1);
			return cubemap;
		}

		/// <summary>The six faces laid out as a horizontal cross (4 x 3 faces), for previews.</summary>
		public Texture2D CreatePreview()
		{
			int s = size;
			var image = new ImageData(s * 4, s * 3, new byte[s * 4 * s * 3 * 4]);
			(int x, int y)[] cells = { (2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (3, 1) };
			for (int face = 0; face < 6; face++)
			{
				ImageData source = faces != null && faces[face] != null
					? new ImageData(s, s, faces[face])
					: floatFaces != null && floatFaces[face] != null ? new ImageData(s, s, floatFaces[face]).ToLdr() : null;
				if (source != null)
				{
					image.Blit(source, cells[face].x * s, cells[face].y * s);
				}
			}

			var texture = new Texture2D();
			texture.name = (name ?? AssetPath) + " (preview)";
			texture.SetImage(image, false, false, false);
			texture.SetSamplerState(FilterMode.Bilinear, TextureWrap.Clamp, 1);
			return texture;
		}

		#endregion

		#region InternalMethods

		internal override TextureTarget Target => TextureTarget.TextureCubeMap;
		protected override bool HasData => faces != null || floatFaces != null;

		internal void SetFaces(int faceSize, byte[][] rgbaFaces, float[][] hdrFaces, bool srgb, bool mipmaps)
		{
			SetSize(faceSize, faceSize);
			faces = rgbaFaces;
			floatFaces = hdrFaces;
			sRGB = srgb;
			SetMipmaps(mipmaps);
			Release();
		}

		protected override void UploadData()
		{
			if (!seamlessEnabled)
			{
				GL.Enable(EnableCap.TextureCubeMapSeamless);
				seamlessEnabled = true;
			}

			for (int face = 0; face < 6; face++)
			{
				TextureTarget target = TextureTarget.TextureCubeMapPositiveX + face;
				if (floatFaces != null)
				{
					GL.TexImage2D(target, 0, PixelInternalFormat.Rgba16f, size, size, 0, PixelFormat.Rgba, PixelType.Float, floatFaces[face]);
				}
				else
				{
					GL.TexImage2D(target, 0, sRGB ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, faces[face]);
				}
			}
		}

		#endregion
	}
}
