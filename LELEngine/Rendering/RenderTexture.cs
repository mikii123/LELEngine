using System;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     GPU texture intended to be rendered into (color, depth, or storage image for compute).
	/// </summary>
	public sealed class RenderTexture
	{
		#region PublicFields

		public int Handle { get; private set; }
		public int Width { get; private set; }
		public int Height { get; private set; }
		public RenderTextureFormat Format { get; }
		public bool IsDepth => Format == RenderTextureFormat.Depth24 || Format == RenderTextureFormat.Depth32F;

		#endregion

		#region PrivateFields

		private readonly TextureMinFilter minFilter;
		private readonly TextureMagFilter magFilter;
		private readonly TextureWrapMode wrap;

		#endregion

		#region Constructors

		public RenderTexture(int width, int height, RenderTextureFormat format, bool linearFilter = true, TextureWrapMode wrap = TextureWrapMode.ClampToEdge)
		{
			Format = format;
			minFilter = linearFilter ? TextureMinFilter.Linear : TextureMinFilter.Nearest;
			magFilter = linearFilter ? TextureMagFilter.Linear : TextureMagFilter.Nearest;
			this.wrap = wrap;

			Handle = GL.GenTexture();
			Resize(width, height);
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Reallocates storage. Contents are lost.
		/// </summary>
		public void Resize(int width, int height)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);

			PixelInternalFormat internalFormat;
			PixelFormat pixelFormat;
			PixelType pixelType;
			GetFormat(Format, out internalFormat, out pixelFormat, out pixelType);

			GL.BindTexture(TextureTarget.Texture2D, Handle);
			GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, Width, Height, 0, pixelFormat, pixelType, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
			GL.BindTexture(TextureTarget.Texture2D, 0);
		}

		/// <summary>
		///     Enables hardware depth comparison (sampler2DShadow) for depth textures.
		/// </summary>
		public void SetShadowCompare(bool enabled)
		{
			GL.BindTexture(TextureTarget.Texture2D, Handle);
			if (enabled)
			{
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)All.Lequal);
			}
			else
			{
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
			}
			GL.BindTexture(TextureTarget.Texture2D, 0);
		}

		public void SetBorderColor(float r, float g, float b, float a)
		{
			GL.BindTexture(TextureTarget.Texture2D, Handle);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, new[] { r, g, b, a });
			GL.BindTexture(TextureTarget.Texture2D, 0);
		}

		public void Bind(int unit)
		{
			GL.ActiveTexture(TextureUnit.Texture0 + unit);
			GL.BindTexture(TextureTarget.Texture2D, Handle);
		}

		/// <summary>
		///     Binds the texture as a compute shader storage image.
		/// </summary>
		public void BindImage(int unit, TextureAccess access)
		{
			GL.BindImageTexture(unit, Handle, 0, false, 0, access, GetSizedFormat(Format));
		}

		public void Delete()
		{
			GL.DeleteTexture(Handle);
			Handle = 0;
		}

		#endregion

		#region PrivateMethods

		private static void GetFormat(RenderTextureFormat format, out PixelInternalFormat internalFormat, out PixelFormat pixelFormat, out PixelType pixelType)
		{
			switch (format)
			{
				case RenderTextureFormat.RGBA8:
					internalFormat = PixelInternalFormat.Rgba8;
					pixelFormat = PixelFormat.Rgba;
					pixelType = PixelType.UnsignedByte;
					break;
				case RenderTextureFormat.RGBA16F:
					internalFormat = PixelInternalFormat.Rgba16f;
					pixelFormat = PixelFormat.Rgba;
					pixelType = PixelType.Float;
					break;
				case RenderTextureFormat.RGBA32F:
					internalFormat = PixelInternalFormat.Rgba32f;
					pixelFormat = PixelFormat.Rgba;
					pixelType = PixelType.Float;
					break;
				case RenderTextureFormat.R32F:
					internalFormat = PixelInternalFormat.R32f;
					pixelFormat = PixelFormat.Red;
					pixelType = PixelType.Float;
					break;
				case RenderTextureFormat.RG16F:
					internalFormat = PixelInternalFormat.Rg16f;
					pixelFormat = PixelFormat.Rg;
					pixelType = PixelType.Float;
					break;
				case RenderTextureFormat.Depth24:
					internalFormat = PixelInternalFormat.DepthComponent24;
					pixelFormat = PixelFormat.DepthComponent;
					pixelType = PixelType.Float;
					break;
				case RenderTextureFormat.Depth32F:
					internalFormat = PixelInternalFormat.DepthComponent32f;
					pixelFormat = PixelFormat.DepthComponent;
					pixelType = PixelType.Float;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(format), format, null);
			}
		}

		private static SizedInternalFormat GetSizedFormat(RenderTextureFormat format)
		{
			switch (format)
			{
				case RenderTextureFormat.RGBA8: return SizedInternalFormat.Rgba8;
				case RenderTextureFormat.RGBA16F: return SizedInternalFormat.Rgba16f;
				case RenderTextureFormat.RGBA32F: return SizedInternalFormat.Rgba32f;
				case RenderTextureFormat.R32F: return SizedInternalFormat.R32f;
				case RenderTextureFormat.RG16F: return SizedInternalFormat.Rg16f;
				default:
					throw new InvalidOperationException("Format " + format + " cannot be bound as a storage image.");
			}
		}

		#endregion
	}

	public enum RenderTextureFormat
	{
		RGBA8,
		RGBA16F,
		RGBA32F,
		R32F,
		RG16F,
		Depth24,
		Depth32F
	}
}
