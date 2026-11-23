using System;
using LELEngine.Graphics;

namespace LELEngine
{
	public enum FilterMode
	{
		/// <summary>Nearest texel (pixel art).</summary>
		Point,

		/// <summary>Linear filtering within a mip level.</summary>
		Bilinear,

		/// <summary>Linear filtering between mip levels as well.</summary>
		Trilinear
	}

	/// <summary>How texture coordinates outside 0..1 sample (Unity's TextureWrapMode; named apart from the graphics API's enum).</summary>
	public enum TextureWrap
	{
		Repeat,
		Clamp,
		Mirror
	}

	public enum TextureDimension
	{
		Tex2D,
		Tex3D,
		Cube
	}

	/// <summary>
	///     Base of every texture asset (<see cref="Texture2D" />, <see cref="Texture3D" />, <see cref="Cubemap" />).
	///     The GL texture is created on first use of <see cref="Handle" />, so textures can be loaded before the GL
	///     context exists (asset loading, style sheet parsing). Pixels stay in memory for re-uploads and reads.
	/// </summary>
	public abstract class Texture : IAsset
	{
		#region PublicFields

		public string AssetPath { get; internal set; }
		public virtual string SubAssetName => null;
		public string name;
		public int width { get; protected set; }
		public int height { get; protected set; }
		public abstract TextureDimension dimension { get; }

		public FilterMode filterMode
		{
			get => filter;
			set
			{
				if (filter != value)
				{
					filter = value;
					ParametersChanged();
				}
			}
		}

		public TextureWrap wrapMode
		{
			get => wrap;
			set
			{
				if (wrap != value)
				{
					wrap = value;
					ParametersChanged();
				}
			}
		}

		/// <summary>Anisotropic filtering level (1 = off), applied when the context supports it.</summary>
		public int anisoLevel
		{
			get => anisotropy;
			set
			{
				int clamped = Math.Clamp(value, 1, 16);
				if (anisotropy != clamped)
				{
					anisotropy = clamped;
					ParametersChanged();
				}
			}
		}

		/// <summary>Mip chain generated on upload.</summary>
		public bool mipmapEnabled { get; protected set; }

		/// <summary>The texture on the GPU; made from the pixels on first access (null when there are none, or the upload failed).</summary>
		public GpuTexture Gpu
		{
			get
			{
				if (gpu == null && !failed)
				{
					TryUpload();
				}

				return gpu;
			}
		}

		/// <summary>The OpenGL texture object: only for the code that still gives OpenGL commands itself.</summary>
		public int Handle => Gpu is Graphics.OpenGL.GLTexture texture ? texture.Handle : 0;

		public bool IsValid => !failed && (HasData || gpu != null);

		/// <summary>Linear filtering (bilinear) when scaled; off for pixel art (UI compatibility).</summary>
		public bool filterLinear
		{
			get => filterMode != FilterMode.Point;
			set => filterMode = value ? FilterMode.Bilinear : FilterMode.Point;
		}

		/// <summary>Repeat instead of clamping outside [0, 1] uv (UI compatibility).</summary>
		public bool wrapRepeat
		{
			get => wrapMode == TextureWrap.Repeat;
			set => wrapMode = value ? TextureWrap.Repeat : TextureWrap.Clamp;
		}

		/// <summary>Anisotropic filtering level applied to loaded textures when the context supports it (default of importers).</summary>
		public static float MaxAnisotropy = 8f;

		#endregion

		#region PrivateFields

		private FilterMode filter = FilterMode.Bilinear;
		private TextureWrap wrap = TextureWrap.Repeat;
		private int anisotropy = 1;
		protected GpuTexture gpu;
		protected bool failed;

		#endregion

		#region PublicMethods

		/// <summary>Frees the texture on the GPU; it is made again on the next use of <see cref="Gpu" />.</summary>
		public void Release()
		{
			gpu?.Dispose();
			gpu = null;
			failed = false;
		}

		/// <summary>Re-uploads the pixels (after SetPixels) on the next use.</summary>
		public void Apply()
		{
			Release();
		}

		public override string ToString()
		{
			return GetType().Name + "(" + (AssetPath ?? name) + " " + width + "x" + height + ")";
		}

		#endregion

		#region InternalMethods

		/// <summary>The OpenGL binding target of this texture type: only for the code that still gives OpenGL commands itself.</summary>
		internal TextureTarget Target => Kind == TextureKind.Cube ? TextureTarget.TextureCubeMap : Kind == TextureKind.Texture3D ? TextureTarget.Texture3D : TextureTarget.Texture2D;

		/// <summary>What kind of texture the GPU gets.</summary>
		internal abstract TextureKind Kind { get; }

		/// <summary>Texels baked by a game build (every level as the driver takes it): uploaded instead of the pixels.</summary>
		internal KtxFile Ktx { get; private set; }

		/// <summary>Takes a baked texture's levels (a build's KTX file): no pixels on the CPU unless the data is uncompressed and asked for.</summary>
		internal void SetKtx(KtxFile ktx, bool mipmaps)
		{
			Ktx = ktx;
			SetSize(ktx.Width, ktx.Height);
			SetMipmaps(ktx.Levels > 1 || (mipmaps && !ktx.IsCompressed));
			Release();
		}

		/// <summary>The texture uploads from its pixels again (they were set or read from the baked data).</summary>
		protected void ClearKtx()
		{
			Ktx = null;
		}

		/// <summary>Pixels are available for an upload.</summary>
		protected abstract bool HasData { get; }

		/// <summary>
		///     Makes the texture on the GPU from the pixels: its first mip level filled, with a whole mip chain when
		///     <see cref="mipmapEnabled" /> (the levels below are generated after).
		/// </summary>
		protected abstract GpuTexture UploadData();

		/// <summary>Mip levels a texture of this one's pixels is made with: all of them, or one.</summary>
		protected int UploadMipLevels => mipmapEnabled ? 0 : 1;

		/// <summary>Called when the pixel data changed shape (width / height); subclasses set the fields.</summary>
		protected void SetSize(int newWidth, int newHeight)
		{
			width = newWidth;
			height = newHeight;
		}

		protected void SetMipmaps(bool enabled)
		{
			mipmapEnabled = enabled;
		}

		/// <summary>How the texture is sampled: its filter, wrap and anisotropy as a sampler of the device.</summary>
		protected GpuSampler CreateSampler()
		{
			TextureKind kind = Kind;
			SamplerDescriptor descriptor = default;
			descriptor.CompareFunction = CompareFunction.LessEqual;
			switch (filter)
			{
				case FilterMode.Point:
					descriptor.Min = descriptor.Mag = SamplerFilter.Nearest;
					descriptor.Mip = mipmapEnabled ? SamplerMipFilter.Nearest : SamplerMipFilter.None;
					break;
				case FilterMode.Bilinear:
					descriptor.Min = descriptor.Mag = SamplerFilter.Linear;
					descriptor.Mip = mipmapEnabled ? SamplerMipFilter.Nearest : SamplerMipFilter.None;
					break;
				default:
					descriptor.Min = descriptor.Mag = SamplerFilter.Linear;
					descriptor.Mip = mipmapEnabled ? SamplerMipFilter.Linear : SamplerMipFilter.None;
					break;
			}

			SamplerAddress address;
			switch (wrap)
			{
				case TextureWrap.Clamp: address = SamplerAddress.ClampToEdge; break;
				case TextureWrap.Mirror: address = SamplerAddress.MirroredRepeat; break;
				default: address = SamplerAddress.Repeat; break;
			}

			if (kind == TextureKind.Cube)
			{
				// Faces always clamp; seams are handled by sampling across faces.
				address = SamplerAddress.ClampToEdge;
			}

			descriptor.AddressU = descriptor.AddressV = address;
			// A 2D texture has no third coordinate: the device's default for it, whatever the texture's wrap.
			descriptor.AddressW = kind == TextureKind.Texture2D ? SamplerAddress.Repeat : address;
			descriptor.MaxAnisotropy = kind != TextureKind.Texture3D ? Math.Max(1, anisotropy) : 1f;
			return GraphicsDevice.Current.GetSampler(descriptor);
		}

		/// <summary>Sampler state fields set without GL side effects (importers configure textures before upload).</summary>
		internal void SetSamplerState(FilterMode filterMode, TextureWrap wrapMode, int anisoLevel)
		{
			filter = filterMode;
			wrap = wrapMode;
			anisotropy = Math.Clamp(anisoLevel, 1, 16);
		}

		#endregion

		#region PrivateMethods

		private void TryUpload()
		{
			// HasData finishes an import still running on a worker thread (the wait is part of this first use).
			long started = FrameProfiler.Timestamp;
			if (!HasData && Ktx == null)
			{
				failed = true;
				return;
			}

			long waited = FrameProfiler.Timestamp;
			try
			{
				// A baked texture brings its mip levels (compressed data cannot generate them); one level only gets them generated.
				bool generate = mipmapEnabled && (Ktx == null || (Ktx.Levels == 1 && !Ktx.IsCompressed));
				gpu = Ktx != null ? Ktx.Upload(Kind, generate, AssetPath ?? name) : UploadData();
				gpu.Sampler = CreateSampler();
				if (generate)
				{
					gpu.GenerateMipmaps();
				}

				if (FrameProfiler.Enabled)
				{
					double waitMs = (waited - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
					string how = (Ktx != null ? "baked levels" : mipmapEnabled ? "pixels, mipmaps generated" : "pixels") + (waitMs >= 0.5 ? ", after waiting " + waitMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms for its import" : "");
					FrameProfiler.EventSince(started, "texture upload", AssetPath ?? name ?? GetType().Name, width + "x" + height + " " + how);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Texture] Cannot upload " + (AssetPath ?? name) + ": " + e.Message);
				gpu?.Dispose();
				failed = true;
				gpu = null;
			}
		}

		private void ParametersChanged()
		{
			if (gpu != null && GraphicsDevice.Current != null)
			{
				gpu.Sampler = CreateSampler();
			}
		}

		#endregion
	}
}
