using System;
using OpenTK.Graphics.OpenGL4;

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

	/// <summary>How texture coordinates outside 0..1 sample (Unity's TextureWrapMode; named apart from OpenTK's enum).</summary>
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

		/// <summary>The GL texture handle; uploads the pixels on first access.</summary>
		public int Handle
		{
			get
			{
				if (handle == 0 && !failed)
				{
					TryUpload();
				}

				return handle;
			}
		}

		public bool IsValid => !failed && (HasData || handle != 0);

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
		protected int handle;
		protected bool failed;

		#endregion

		#region PublicMethods

		/// <summary>Deletes the GL texture; it is created again on the next use of <see cref="Handle" />.</summary>
		public void Release()
		{
			if (handle != 0)
			{
				GL.DeleteTexture(handle);
				handle = 0;
			}

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

		/// <summary>GL binding target of this texture type.</summary>
		internal abstract TextureTarget Target { get; }

		/// <summary>Pixels are available for an upload.</summary>
		protected abstract bool HasData { get; }

		/// <summary>Creates the GL texture from the pixels (the target is already bound by <see cref="TryUpload" />).</summary>
		protected abstract void UploadData();

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

		/// <summary>Sampler state (filter, wrap, anisotropy) of the bound texture.</summary>
		protected void ApplyParameters()
		{
			TextureTarget target = Target;
			TextureMinFilter min;
			TextureMagFilter mag;
			switch (filter)
			{
				case FilterMode.Point:
					min = mipmapEnabled ? TextureMinFilter.NearestMipmapNearest : TextureMinFilter.Nearest;
					mag = TextureMagFilter.Nearest;
					break;
				case FilterMode.Bilinear:
					min = mipmapEnabled ? TextureMinFilter.LinearMipmapNearest : TextureMinFilter.Linear;
					mag = TextureMagFilter.Linear;
					break;
				default:
					min = mipmapEnabled ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear;
					mag = TextureMagFilter.Linear;
					break;
			}

			GL.TexParameter(target, TextureParameterName.TextureMinFilter, (int)min);
			GL.TexParameter(target, TextureParameterName.TextureMagFilter, (int)mag);

			int wrapValue;
			switch (wrap)
			{
				case TextureWrap.Clamp: wrapValue = (int)TextureWrapMode.ClampToEdge; break;
				case TextureWrap.Mirror: wrapValue = (int)TextureWrapMode.MirroredRepeat; break;
				default: wrapValue = (int)TextureWrapMode.Repeat; break;
			}

			if (target == TextureTarget.TextureCubeMap)
			{
				// Faces always clamp; seams are handled by seamless cube map sampling.
				wrapValue = (int)TextureWrapMode.ClampToEdge;
			}

			GL.TexParameter(target, TextureParameterName.TextureWrapS, wrapValue);
			GL.TexParameter(target, TextureParameterName.TextureWrapT, wrapValue);
			if (target == TextureTarget.Texture3D || target == TextureTarget.TextureCubeMap)
			{
				GL.TexParameter(target, TextureParameterName.TextureWrapR, wrapValue);
			}

			if (Rendering.GLCapabilities.AnisotropicFiltering && target != TextureTarget.Texture3D)
			{
				// GL_TEXTURE_MAX_ANISOTROPY (core in 4.6, same value as the EXT extension).
				GL.TexParameter(target, (TextureParameterName)0x84FE, Math.Min(anisotropy, Rendering.GLCapabilities.MaxAnisotropy));
			}
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
			if (!HasData)
			{
				failed = true;
				return;
			}

			try
			{
				handle = GL.GenTexture();
				GL.BindTexture(Target, handle);
				GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
				UploadData();
				GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
				ApplyParameters();
				if (mipmapEnabled)
				{
					GL.GenerateMipmap((GenerateMipmapTarget)Target);
				}

				GL.BindTexture(Target, 0);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Texture] Cannot upload " + (AssetPath ?? name) + ": " + e.Message);
				if (handle != 0)
				{
					GL.DeleteTexture(handle);
				}

				failed = true;
				handle = 0;
			}
		}

		private void ParametersChanged()
		{
			if (handle == 0)
			{
				return;
			}

			try
			{
				GL.BindTexture(Target, handle);
				ApplyParameters();
				GL.BindTexture(Target, 0);
			}
			catch (Exception)
			{
				// No context on this thread: the parameters apply on the next upload.
			}
		}

		#endregion
	}
}
