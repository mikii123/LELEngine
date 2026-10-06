using System;
using System.Collections.Generic;
using LELEngine.Rendering.DistanceField;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Lumen
{
	/// <summary>
	///     One axis-aligned card of an object: an orthographic view of the mesh from one side, stored as a
	///     rectangle of the surface cache atlas. Geometry is in scaled-local space.
	/// </summary>
	public sealed class SurfaceCard
	{
		public int X, Y, Width, Height;
		public Vector3 AxisX, AxisY, AxisZ;
		public float ExtentU, ExtentV, ExtentDepth;
		public Vector3 Center;
		public Matrix4 ViewProjection;
	}

	/// <summary>
	///     The six cards of one renderer. The card table index is assigned once and never changes, so the
	///     card indices baked into the atlas stay valid.
	/// </summary>
	public sealed class SurfaceCardSet
	{
		public MeshRenderer Renderer;
		public int FirstCardIndex;
		public SurfaceCard[] Cards;
		public bool Captured;
	}

	/// <summary>
	///     Lumen surface cache storage: atlases for captured surface attributes and cached lighting, the card
	///     allocator (simple shelf packing) and the capture framebuffer.
	/// </summary>
	public sealed class SurfaceCacheAtlas
	{
		#region PublicFields

		public const int CardsPerObject = 6;
		public const ushort EmptyCardIndex = 0xFFFF;

		public int Size { get; }
		public int CardIndex { get; private set; }
		public int Albedo { get; private set; }
		public int Normal { get; private set; }
		public int Emissive { get; private set; }
		public int LocalPosition { get; private set; }
		public int IndirectLighting { get; private set; }
		public int FinalLightingCurrent => finalLighting[finalIndex];
		public int FinalLightingPrevious => finalLighting[1 - finalIndex];
		public int CaptureFramebuffer { get; private set; }

		/// <summary>Atlas rows in use; compute passes only visit these.</summary>
		public int UsedRows => Math.Min(Size, shelfY + shelfHeight);

		public int CardCount => cardCount;
		public IReadOnlyList<SurfaceCardSet> CardSets => sets;

		#endregion

		#region PrivateFields

		private readonly int[] finalLighting = new int[2];
		private int finalIndex;
		private int depthRenderbuffer;

		private readonly Dictionary<MeshRenderer, SurfaceCardSet> setsByRenderer = new Dictionary<MeshRenderer, SurfaceCardSet>();
		private readonly List<SurfaceCardSet> sets = new List<SurfaceCardSet>();
		private int cardCount;
		private int shelfX;
		private int shelfY;
		private int shelfHeight;

		#endregion

		#region Constructors

		public SurfaceCacheAtlas(int size)
		{
			Size = size;

			CardIndex = CreateTexture(PixelInternalFormat.R16ui, PixelFormat.RedInteger, PixelType.UnsignedShort);
			Albedo = CreateTexture(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
			Normal = CreateTexture(PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
			Emissive = CreateTexture(PixelInternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.Float);
			LocalPosition = CreateTexture(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
			IndirectLighting = CreateTexture(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
			finalLighting[0] = CreateTexture(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);
			finalLighting[1] = CreateTexture(PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);

			// Lighting atlases are filtered when looked up; attributes are fetched by texel.
			SetFilter(IndirectLighting, true);
			SetFilter(finalLighting[0], true);
			SetFilter(finalLighting[1], true);
			SetFilter(LocalPosition, true);

			depthRenderbuffer = GL.GenRenderbuffer();
			GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRenderbuffer);
			GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
			GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

			CaptureFramebuffer = GL.GenFramebuffer();
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, CaptureFramebuffer);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, Albedo, 0);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, Normal, 0);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, TextureTarget.Texture2D, Emissive, 0);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment3, TextureTarget.Texture2D, LocalPosition, 0);
			GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment4, TextureTarget.Texture2D, CardIndex, 0);
			GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depthRenderbuffer);
			GL.DrawBuffers(5, new[]
			{
				DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2,
				DrawBuffersEnum.ColorAttachment3, DrawBuffersEnum.ColorAttachment4
			});
			FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				Console.WriteLine("[SurfaceCache] Capture framebuffer incomplete: " + status);
			}

			// Start empty: no card anywhere, no lighting.
			ClearRect(0, 0, size, size);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			ClearLighting();

			Console.WriteLine($"[SurfaceCache] Atlas {size}x{size}: albedo, normal, emissive, position, indirect, 2x final (~{size * (long)size * 46 / (1024.0 * 1024.0):0} MB)");
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Returns the renderer's card set, allocating six cards from its scaled-local mesh bounds on first use.
		///     Null when the atlas is full.
		/// </summary>
		public SurfaceCardSet GetOrAllocate(MeshRenderer renderer, MeshDistanceField field, int texelsPerMeter, int maxCardSize)
		{
			SurfaceCardSet set;
			if (setsByRenderer.TryGetValue(renderer, out set))
			{
				return set;
			}

			Vector3 scale = field.Scale;
			Vector3 bmin = renderer.Mesh.BoundsMin * scale;
			Vector3 bmax = renderer.Mesh.BoundsMax * scale;
			Vector3 center = (bmin + bmax) * 0.5f;
			Vector3 size = bmax - bmin;

			var cards = new SurfaceCard[CardsPerObject];
			for (int i = 0; i < CardsPerObject; i++)
			{
				int axis = i / 2;
				float sign = (i & 1) == 0 ? 1f : -1f;
				Vector3 axisZ = Vector3.Zero;
				axisZ[axis] = sign;

				// U/V axes: X faces use (Z, Y), Y faces use (X, Z), Z faces use (X, Y).
				Vector3 axisX = axis == 0 ? Vector3.UnitZ : Vector3.UnitX;
				Vector3 axisY = axis == 1 ? Vector3.UnitZ : Vector3.UnitY;

				float extentU = Math.Max(Vector3.Dot(size, Abs(axisX)), 0.01f);
				float extentV = Math.Max(Vector3.Dot(size, Abs(axisY)), 0.01f);
				float extentDepth = Math.Max(Vector3.Dot(size, Abs(axisZ)), 0.01f) + 0.1f;

				int width = Math.Clamp((int)Math.Ceiling(extentU * texelsPerMeter), 4, maxCardSize);
				int height = Math.Clamp((int)Math.Ceiling(extentV * texelsPerMeter), 4, maxCardSize);

				int x, y;
				if (!Allocate(width, height, out x, out y))
				{
					Console.WriteLine("[SurfaceCache] Atlas full, object without cards");
					return null;
				}

				// Slightly larger ortho window than the mesh so edge texels are fully covered.
				float marginU = extentU / width;
				float marginV = extentV / height;
				float halfU = extentU * 0.5f + marginU;
				float halfV = extentV * 0.5f + marginV;

				Vector3 eye = center + axisZ * (extentDepth * 0.5f + 0.05f);
				Matrix4 view = Matrix4.LookAt(eye, center, axisY);
				Matrix4 projection = Matrix4.CreateOrthographicOffCenter(-halfU, halfU, -halfV, halfV, 0.01f, extentDepth + 0.2f);

				cards[i] = new SurfaceCard
				{
					X = x, Y = y, Width = width, Height = height,
					AxisX = axisX, AxisY = axisY, AxisZ = axisZ,
					ExtentU = halfU * 2f, ExtentV = halfV * 2f, ExtentDepth = extentDepth,
					Center = center,
					ViewProjection = view * projection
				};
			}

			set = new SurfaceCardSet { Renderer = renderer, FirstCardIndex = cardCount, Cards = cards };
			cardCount += CardsPerObject;
			sets.Add(set);
			setsByRenderer[renderer] = set;
			return set;
		}

		/// <summary>
		///     Clears a card rectangle of the capture attachments (requires the capture framebuffer to be bound).
		/// </summary>
		public void ClearRect(int x, int y, int width, int height)
		{
			GL.Enable(EnableCap.ScissorTest);
			GL.Scissor(x, y, width, height);
			float[] zero = { 0f, 0f, 0f, 0f };
			for (int i = 0; i < 4; i++)
			{
				GL.ClearBuffer(ClearBuffer.Color, i, zero);
			}
			GL.ClearBuffer(ClearBuffer.Color, 4, new uint[] { EmptyCardIndex, 0, 0, 0 });
			GL.ClearBuffer(ClearBuffer.Depth, 0, new[] { 1f });
			GL.Disable(EnableCap.ScissorTest);
		}

		public void SwapFinalLighting()
		{
			finalIndex = 1 - finalIndex;
		}

		public void ClearLighting()
		{
			var zeros = new float[Size * Size * 4];
			foreach (int texture in new[] { IndirectLighting, finalLighting[0], finalLighting[1] })
			{
				GL.BindTexture(TextureTarget.Texture2D, texture);
				GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Size, Size, PixelFormat.Rgba, PixelType.Float, zeros);
			}
			GL.BindTexture(TextureTarget.Texture2D, 0);
		}

		public void Delete()
		{
			foreach (int texture in new[] { CardIndex, Albedo, Normal, Emissive, LocalPosition, IndirectLighting, finalLighting[0], finalLighting[1] })
			{
				GL.DeleteTexture(texture);
			}
			GL.DeleteRenderbuffer(depthRenderbuffer);
			GL.DeleteFramebuffer(CaptureFramebuffer);
			sets.Clear();
			setsByRenderer.Clear();
		}

		#endregion

		#region PrivateMethods

		private bool Allocate(int width, int height, out int x, out int y)
		{
			if (shelfX + width > Size)
			{
				shelfY += shelfHeight;
				shelfX = 0;
				shelfHeight = 0;
			}
			if (shelfY + height > Size)
			{
				x = y = 0;
				return false;
			}

			x = shelfX;
			y = shelfY;
			shelfX += width;
			shelfHeight = Math.Max(shelfHeight, height);
			return true;
		}

		private int CreateTexture(PixelInternalFormat internalFormat, PixelFormat format, PixelType type)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, Size, Size, 0, format, type, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			return texture;
		}

		private static void SetFilter(int texture, bool linear)
		{
			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(linear ? TextureMinFilter.Linear : TextureMinFilter.Nearest));
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)(linear ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
			GL.BindTexture(TextureTarget.Texture2D, 0);
		}

		private static Vector3 Abs(Vector3 v)
		{
			return new Vector3(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
		}

		#endregion
	}
}
