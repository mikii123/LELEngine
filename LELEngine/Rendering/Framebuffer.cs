using System;
using System.Collections.Generic;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     What a render pass draws into: any number of colour attachments and an optional depth attachment, all of
	///     one size and resized together. <see cref="Begin" /> starts a pass into them.
	/// </summary>
	public sealed class Framebuffer
	{
		#region PublicFields

		public int Width { get; private set; }
		public int Height { get; private set; }
		public IReadOnlyList<RenderTexture> ColorAttachments => colorAttachments;
		public RenderTexture DepthAttachment { get; private set; }

		#endregion

		#region PrivateFields

		private readonly List<RenderTexture> colorAttachments = new List<RenderTexture>();
		private readonly HashSet<RenderTexture> borrowed = new HashSet<RenderTexture>();
		private ColorAttachment[] passColors = Array.Empty<ColorAttachment>();
		private bool ownsDepth = true;

		#endregion

		#region Constructors

		public Framebuffer(int width, int height)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);
		}

		#endregion

		#region PublicMethods

		/// <param name="storage">Shaders also read or write the attachment as an image.</param>
		/// <param name="asyncCompute">Asynchronous compute work reads or writes it too (<see cref="TextureUsage.AsyncCompute" />).</param>
		public RenderTexture AddColorAttachment(RenderTextureFormat format, bool linearFilter = true, bool storage = false, bool asyncCompute = false)
		{
			if (format == RenderTextureFormat.Depth24 || format == RenderTextureFormat.Depth32F)
			{
				throw new ArgumentException("Use AddDepthAttachment for depth formats.", nameof(format));
			}

			RenderTexture texture = new RenderTexture(Width, Height, format, linearFilter, SamplerAddress.ClampToEdge, storage, asyncCompute);
			colorAttachments.Add(texture);
			return texture;
		}

		/// <summary>
		///     Attaches a color target made elsewhere (a camera's <see cref="Camera.targetTexture" />). The framebuffer
		///     neither resizes nor deletes it; it must be this framebuffer's size.
		/// </summary>
		public void AttachColor(RenderTexture texture)
		{
			if (texture == null || texture.IsDepth)
			{
				throw new ArgumentException("A color render texture is needed.", nameof(texture));
			}

			colorAttachments.Add(texture);
			borrowed.Add(texture);
		}

		public RenderTexture AddDepthAttachment(RenderTextureFormat format = RenderTextureFormat.Depth24, bool linearFilter = false, SamplerAddress wrap = SamplerAddress.ClampToEdge)
		{
			if (format != RenderTextureFormat.Depth24 && format != RenderTextureFormat.Depth32F)
			{
				throw new ArgumentException("Depth attachment requires a depth format.", nameof(format));
			}

			if (ownsDepth)
			{
				DepthAttachment?.Delete();
			}

			DepthAttachment = new RenderTexture(Width, Height, format, linearFilter, wrap);
			ownsDepth = true;
			return DepthAttachment;
		}

		/// <summary>
		///     Uses a depth texture owned by another framebuffer (e.g. the scene depth) so passes can
		///     render extra attachments against the same depth. The texture must match this size.
		/// </summary>
		public void AttachSharedDepth(RenderTexture depth)
		{
			if (ownsDepth)
			{
				DepthAttachment?.Delete();
			}

			DepthAttachment = depth;
			ownsDepth = false;
		}

		/// <summary>
		///     Throws if the attachments cannot be drawn into together. Call after adding attachments.
		/// </summary>
		public void Validate()
		{
			foreach (RenderTexture texture in colorAttachments)
			{
				if (texture.Width != Width || texture.Height != Height)
				{
					throw new InvalidOperationException($"Framebuffer incomplete: a {texture.Width} x {texture.Height} colour attachment in a {Width} x {Height} framebuffer.");
				}
			}

			if (DepthAttachment != null && (DepthAttachment.Width != Width || DepthAttachment.Height != Height))
			{
				throw new InvalidOperationException($"Framebuffer incomplete: a {DepthAttachment.Width} x {DepthAttachment.Height} depth attachment in a {Width} x {Height} framebuffer.");
			}
		}

		public void Resize(int width, int height)
		{
			width = Math.Max(1, width);
			height = Math.Max(1, height);
			if (width == Width && height == Height)
			{
				return;
			}

			Width = width;
			Height = height;

			foreach (RenderTexture texture in colorAttachments)
			{
				if (!borrowed.Contains(texture))
				{
					texture.Resize(width, height);
				}
			}
			if (ownsDepth)
			{
				DepthAttachment?.Resize(width, height);
			}
		}

		/// <summary>
		///     Begins a render pass into the attachments (the viewport covers the whole target). Every colour
		///     attachment is loaded, or cleared to one value; the depth likewise.
		/// </summary>
		public void Begin(LoadOperation color = LoadOperation.Load, ClearValue clearColor = default, LoadOperation depth = LoadOperation.Load, float clearDepth = 1f)
		{
			RenderPassDescriptor descriptor = Describe(color, clearColor, depth, clearDepth);
			GraphicsDevice.Current.Commands.BeginRenderPass(descriptor);
		}

		/// <summary>Begins a pass that clears every attachment: the colours to one value, the depth to 1.</summary>
		public void BeginCleared(ClearValue clearColor = default, float clearDepth = 1f)
		{
			Begin(LoadOperation.Clear, clearColor, LoadOperation.Clear, clearDepth);
		}

		public void Delete()
		{
			foreach (RenderTexture texture in colorAttachments)
			{
				if (!borrowed.Contains(texture))
				{
					texture.Delete();
				}
			}
			colorAttachments.Clear();
			borrowed.Clear();
			if (ownsDepth)
			{
				DepthAttachment?.Delete();
			}
			DepthAttachment = null;
		}

		#endregion

		#region PrivateMethods

		private RenderPassDescriptor Describe(LoadOperation color, ClearValue clearColor, LoadOperation depth, float clearDepth)
		{
			if (passColors.Length != colorAttachments.Count)
			{
				passColors = new ColorAttachment[colorAttachments.Count];
			}

			for (int i = 0; i < passColors.Length; i++)
			{
				passColors[i] = new ColorAttachment(colorAttachments[i].Gpu, color, clearColor);
			}

			RenderPassDescriptor descriptor = default;
			descriptor.Colors = passColors;
			if (DepthAttachment != null)
			{
				descriptor.Depth = new DepthAttachment(DepthAttachment.Gpu, depth, clearDepth);
			}

			return descriptor;
		}

		#endregion
	}
}
