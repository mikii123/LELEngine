using System;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A texture that is rendered into (a colour or depth attachment of a <see cref="Framebuffer" />) and sampled.
	/// </summary>
	public sealed class RenderTexture
	{
		#region PublicFields

		/// <summary>The texture on the GPU (another one after <see cref="Resize" />).</summary>
		public GpuTexture Gpu { get; private set; }

		public int Width { get; private set; }
		public int Height { get; private set; }
		public RenderTextureFormat Format { get; }
		public bool IsDepth => Format == RenderTextureFormat.Depth24 || Format == RenderTextureFormat.Depth32F;

		#endregion

		#region PrivateFields

		private readonly bool linearFilter;
		private readonly SamplerAddress wrap;
		private readonly bool storage;
		private bool shadowCompare;
		private SamplerBorder border = SamplerBorder.TransparentBlack;

		#endregion

		#region Constructors

		/// <param name="storage">Shaders also read or write it as an image (<see cref="CommandList.SetImage" />).</param>
		public RenderTexture(int width, int height, RenderTextureFormat format, bool linearFilter = true, SamplerAddress wrap = SamplerAddress.ClampToEdge, bool storage = false)
		{
			Format = format;
			this.linearFilter = linearFilter;
			this.wrap = wrap;
			this.storage = storage;
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
			Gpu?.Dispose();
			TextureUsage usage = TextureUsage.Sampled | TextureUsage.Attachment | (storage ? TextureUsage.Storage : 0);
			Gpu = GraphicsDevice.Current.CreateTexture(TextureDescriptor.Texture2D(TextureFormatOf(Format), Width, Height, 1, usage));
			Gpu.Sampler = CreateSampler();
		}

		/// <summary>
		///     Sampling compares the stored depth with a reference (sampler2DShadow), for depth textures.
		/// </summary>
		public void SetShadowCompare(bool enabled)
		{
			shadowCompare = enabled;
			Gpu.Sampler = CreateSampler();
		}

		/// <summary>What sampling outside 0..1 reads with a border wrap: transparent black, opaque black or opaque white.</summary>
		public void SetBorderColor(float r, float g, float b, float a)
		{
			if (r == 1f && g == 1f && b == 1f && a == 1f)
			{
				border = SamplerBorder.OpaqueWhite;
			}
			else if (r == 0f && g == 0f && b == 0f)
			{
				border = a == 0f ? SamplerBorder.TransparentBlack : SamplerBorder.OpaqueBlack;
			}
			else
			{
				throw new ArgumentException("A border is transparent black, opaque black or opaque white.");
			}

			Gpu.Sampler = CreateSampler();
		}

		public void Delete()
		{
			Gpu?.Dispose();
			Gpu = null;
		}

		/// <summary>The device's texel format of a render texture format.</summary>
		public static TextureFormat TextureFormatOf(RenderTextureFormat format)
		{
			switch (format)
			{
				case RenderTextureFormat.RGBA8: return TextureFormat.RGBA8;
				case RenderTextureFormat.RGBA16F: return TextureFormat.RGBA16F;
				case RenderTextureFormat.RGBA32F: return TextureFormat.RGBA32F;
				case RenderTextureFormat.R32F: return TextureFormat.R32F;
				case RenderTextureFormat.RG16F: return TextureFormat.RG16F;
				case RenderTextureFormat.Depth24: return TextureFormat.Depth24;
				case RenderTextureFormat.Depth32F: return TextureFormat.Depth32F;
				default: throw new ArgumentOutOfRangeException(nameof(format), format, null);
			}
		}

		#endregion

		#region PrivateMethods

		private GpuSampler CreateSampler()
		{
			SamplerDescriptor descriptor = new SamplerDescriptor(linearFilter ? SamplerFilter.Linear : SamplerFilter.Nearest, SamplerMipFilter.None, wrap);
			// A 2D texture has no third coordinate.
			descriptor.AddressW = SamplerAddress.Repeat;
			descriptor.Border = border;
			descriptor.Compare = shadowCompare;
			return GraphicsDevice.Current.GetSampler(descriptor);
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
