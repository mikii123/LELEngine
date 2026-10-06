using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Framebuffer object with any number of color attachments and an optional depth attachment.
	///     All attachments share the framebuffer's size and are resized together.
	/// </summary>
	public sealed class Framebuffer
	{
		#region PublicFields

		public int Handle { get; private set; }
		public int Width { get; private set; }
		public int Height { get; private set; }
		public IReadOnlyList<RenderTexture> ColorAttachments => colorAttachments;
		public RenderTexture DepthAttachment { get; private set; }

		#endregion

		#region PrivateFields

		private readonly List<RenderTexture> colorAttachments = new List<RenderTexture>();
		private bool ownsDepth = true;

		#endregion

		#region Constructors

		public Framebuffer(int width, int height)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);
			Handle = GL.GenFramebuffer();
		}

		#endregion

		#region PublicMethods

		public RenderTexture AddColorAttachment(RenderTextureFormat format, bool linearFilter = true)
		{
			if (format == RenderTextureFormat.Depth24 || format == RenderTextureFormat.Depth32F)
			{
				throw new ArgumentException("Use AddDepthAttachment for depth formats.", nameof(format));
			}

			RenderTexture texture = new RenderTexture(Width, Height, format, linearFilter);
			int index = colorAttachments.Count;
			colorAttachments.Add(texture);

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + index, TextureTarget.Texture2D, texture.Handle, 0);
			UpdateDrawBuffers();
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

			return texture;
		}

		public RenderTexture AddDepthAttachment(RenderTextureFormat format = RenderTextureFormat.Depth24, bool linearFilter = false, TextureWrapMode wrap = TextureWrapMode.ClampToEdge)
		{
			if (format != RenderTextureFormat.Depth24 && format != RenderTextureFormat.Depth32F)
			{
				throw new ArgumentException("Depth attachment requires a depth format.", nameof(format));
			}

			DepthAttachment = new RenderTexture(Width, Height, format, linearFilter, wrap);

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, DepthAttachment.Handle, 0);
			UpdateDrawBuffers();
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

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

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth.Handle, 0);
			UpdateDrawBuffers();
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		/// <summary>
		///     Throws if the framebuffer is not complete. Call after adding attachments.
		/// </summary>
		public void Validate()
		{
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
			FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				throw new InvalidOperationException("Framebuffer incomplete: " + status);
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
				texture.Resize(width, height);
			}
			if (ownsDepth)
			{
				DepthAttachment?.Resize(width, height);
			}
		}

		/// <summary>
		///     Binds for drawing and sets the viewport to cover the whole target.
		/// </summary>
		public void Bind()
		{
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, Handle);
			GL.Viewport(0, 0, Width, Height);
		}

		/// <summary>
		///     Binds the window's default framebuffer.
		/// </summary>
		public static void BindDefault(int width, int height)
		{
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			GL.Viewport(0, 0, width, height);
		}

		/// <summary>
		///     Copies this framebuffer's depth into another framebuffer (0 = default) of the same size.
		/// </summary>
		public void BlitDepthTo(int targetHandle, int targetWidth, int targetHeight)
		{
			GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, Handle);
			GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, targetHandle);
			GL.BlitFramebuffer(0, 0, Width, Height, 0, 0, targetWidth, targetHeight, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		public void Delete()
		{
			foreach (RenderTexture texture in colorAttachments)
			{
				texture.Delete();
			}
			colorAttachments.Clear();
			if (ownsDepth)
			{
				DepthAttachment?.Delete();
			}
			DepthAttachment = null;

			GL.DeleteFramebuffer(Handle);
			Handle = 0;
		}

		#endregion

		#region PrivateMethods

		private void UpdateDrawBuffers()
		{
			if (colorAttachments.Count == 0)
			{
				// depth-only target (shadow maps)
				GL.DrawBuffer(DrawBufferMode.None);
				GL.ReadBuffer(ReadBufferMode.None);
				return;
			}

			var buffers = new DrawBuffersEnum[colorAttachments.Count];
			for (int i = 0; i < buffers.Length; i++)
			{
				buffers[i] = DrawBuffersEnum.ColorAttachment0 + i;
			}
			GL.DrawBuffers(buffers.Length, buffers);
		}

		#endregion
	}
}
