using System;
using System.Collections.Generic;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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

		/// <summary>The scale the cards were laid out for (scaled-local space); captures draw the mesh at it.</summary>
		public Vector3 Scale;

		/// <summary>
		///     Material state the capture depends on (albedo colour, emission colour, albedo texture). Cards are
		///     captured in scaled-local space, so a moving or pulsing object needs no recapture: only a change of
		///     this key does.
		/// </summary>
		public long CaptureKey;
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

		/// <summary>Card texels per radiosity probe along each axis (one probe per 4x4 block, Lumen's layout).</summary>
		public const int RadiosityBlock = 4;

		public int Size { get; }
		public GpuTexture CardIndex { get; private set; }
		public GpuTexture Albedo { get; private set; }
		public GpuTexture Normal { get; private set; }
		public GpuTexture Emissive { get; private set; }
		public GpuTexture LocalPosition { get; private set; }
		public GpuTexture IndirectLighting { get; private set; }

		/// <summary>Radiosity probe atlas (Size / 4 squared, fp32): rgb irradiance / pi accumulated over frames, a = sample count (0 = none).</summary>
		public GpuTexture RadiosityProbes { get; private set; }

		/// <summary>
		///     Per radiosity probe (fp32): x = mean squared luminance of its updates (the change test's spread), y = updates
		///     since a ray hit a moving object, z = the change test's verdict on the previous update (+1 brighter, -1 darker).
		/// </summary>
		public GpuTexture RadiosityMeta { get; private set; }
		public int ProbeGridSize => Size / RadiosityBlock;
		public GpuTexture FinalLightingCurrent => finalLighting[finalIndex];
		public GpuTexture FinalLightingPrevious => finalLighting[1 - finalIndex];

		/// <summary>Atlas rows in use; compute passes only visit these.</summary>
		public int UsedRows => Math.Min(Size, shelfY + shelfHeight);

		public int CardCount => cardCount;
		public IReadOnlyList<SurfaceCardSet> CardSets => sets;

		#endregion

		#region PrivateFields

		private readonly GpuTexture[] finalLighting = new GpuTexture[2];
		private int finalIndex;
		private GpuTexture captureDepth;

		// The capture's attachments: albedo, normal, emissive, position, card index.
		private readonly ColorAttachment[] captureColors = new ColorAttachment[5];

		private readonly Dictionary<MeshRenderer, SurfaceCardSet> setsByRenderer = new Dictionary<MeshRenderer, SurfaceCardSet>();
		private readonly List<SurfaceCardSet> sets = new List<SurfaceCardSet>();
		private int cardCount;
		private int shelfX;
		private int shelfY;
		private int shelfHeight;
		private bool fullReported;

		#endregion

		#region Constructors

		public SurfaceCacheAtlas(int size)
		{
			Size = size;

			// Lighting atlases are filtered when looked up; attributes are fetched by texel. The cards are captured by draws
			// and read by the lighting's dispatches, which may run on the asynchronous compute queue.
			const TextureUsage captured = TextureUsage.Sampled | TextureUsage.Attachment | TextureUsage.AsyncCompute;
			const TextureUsage lit = TextureUsage.Sampled | TextureUsage.Storage;
			CardIndex = CreateTexture(TextureFormat.R16UI, Size, captured, false, "Surface cache card index");
			Albedo = CreateTexture(TextureFormat.RGBA8, Size, captured, false, "Surface cache albedo");
			Normal = CreateTexture(TextureFormat.RGBA8, Size, captured, false, "Surface cache normal");
			Emissive = CreateTexture(TextureFormat.R11G11B10F, Size, captured, false, "Surface cache emissive");
			LocalPosition = CreateTexture(TextureFormat.RGBA16F, Size, captured, true, "Surface cache position");
			IndirectLighting = CreateTexture(TextureFormat.RGBA16F, Size, TextureUsage.Sampled, true, "Surface cache indirect lighting");
			finalLighting[0] = CreateTexture(TextureFormat.RGBA16F, Size, lit, true, "Surface cache final lighting 0");
			finalLighting[1] = CreateTexture(TextureFormat.RGBA16F, Size, lit, true, "Surface cache final lighting 1");
			RadiosityProbes = CreateTexture(TextureFormat.RGBA32F, Size / RadiosityBlock, lit, false, "Surface cache radiosity probes");
			RadiosityMeta = CreateTexture(TextureFormat.RGBA32F, Size / RadiosityBlock, TextureUsage.Storage, false, "Surface cache radiosity meta");
			captureDepth = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.Depth24, Size, Size, 1, TextureUsage.Attachment, "Surface cache capture depth"));

			// Start empty: no card anywhere, no lighting.
			CommandList commands = Gfx.Commands;
			commands.BeginRenderPass(DescribeCapture(LoadOperation.Clear));
			commands.EndRenderPass();
			ClearLighting();

			Console.WriteLine($"[SurfaceCache] Atlas {size}x{size}: albedo, normal, emissive, position, indirect, 2x final (~{size * (long)size * 46 / (1024.0 * 1024.0):0} MB)");
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Returns the renderer's card set, allocating six cards from its mesh bounds at <paramref name="scale" />
		///     (scaled-local space) on first use. Null when the atlas is full.
		/// </summary>
		public SurfaceCardSet GetOrAllocate(MeshRenderer renderer, Vector3 scale, int texelsPerMeter, int maxCardSize)
		{
			SurfaceCardSet set;
			if (setsByRenderer.TryGetValue(renderer, out set))
			{
				return set;
			}

			Vector3 bmin = renderer.Mesh.BoundsMin * scale;
			Vector3 bmax = renderer.Mesh.BoundsMax * scale;
			Vector3 center = (bmin + bmax) * 0.5f;
			Vector3 size = bmax - bmin;

			// All six cards or none, and looked at before anything is made: an object the atlas has no room for is asked
			// about again every frame (nothing is kept per object without cards), so the answer costs a few sums. Before,
			// each such object made its cards, took the room of the first ones that fitted for nothing and wrote a line to
			// the console, every frame: with a full atlas and a thousand objects left over that was the frame's time.
			int roomX = shelfX;
			int roomY = shelfY;
			int roomHeight = shelfHeight;
			for (int i = 0; i < CardsPerObject; i++)
			{
				CardSize(size, i / 2, texelsPerMeter, maxCardSize, out int width, out int height);
				if (!Allocate(width, height, ref roomX, ref roomY, ref roomHeight, out _, out _))
				{
					ReportFull();
					return null;
				}
			}

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
				CardSize(size, axis, texelsPerMeter, maxCardSize, out int width, out int height);

				// There is room: looked at above.
				Allocate(width, height, ref shelfX, ref shelfY, ref shelfHeight, out int x, out int y);

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

			set = new SurfaceCardSet { Renderer = renderer, FirstCardIndex = cardCount, Cards = cards, Scale = scale };
			cardCount += CardsPerObject;
			sets.Add(set);
			setsByRenderer[renderer] = set;
			return set;
		}

		/// <summary>
		///     Begins a render pass into the capture attachments (albedo, normal, emissive, position, card index and the
		///     capture's depth), keeping their contents.
		/// </summary>
		public void BeginCapture()
		{
			Gfx.Commands.BeginRenderPass(DescribeCapture(LoadOperation.Load));
		}

		/// <summary>
		///     Clears a card rectangle of the capture attachments (inside the capture's pass: <see cref="BeginCapture" />).
		/// </summary>
		public void ClearRect(int x, int y, int width, int height)
		{
			CommandList commands = Gfx.Commands;
			for (int i = 0; i < 4; i++)
			{
				commands.ClearColor(i, ClearValue.Zero, x, y, width, height);
			}
			commands.ClearColor(4, ClearValue.Integer(EmptyCardIndex), x, y, width, height);
			commands.ClearDepth(1f, x, y, width, height);
		}

		public void SwapFinalLighting()
		{
			finalIndex = 1 - finalIndex;
		}

		public void ClearLighting()
		{
			CommandList commands = Gfx.Commands;
			foreach (GpuTexture texture in new[] { IndirectLighting, finalLighting[0], finalLighting[1], RadiosityProbes, RadiosityMeta })
			{
				commands.ClearTexture(texture, 0, ClearValue.Zero);
			}
		}

		public void Delete()
		{
			foreach (GpuTexture texture in new[] { CardIndex, Albedo, Normal, Emissive, LocalPosition, IndirectLighting, finalLighting[0], finalLighting[1], RadiosityProbes, RadiosityMeta, captureDepth })
			{
				texture?.Dispose();
			}
			sets.Clear();
			setsByRenderer.Clear();
		}

		#endregion

		#region PrivateMethods

		// A card's size in texels on the faces of one axis (X faces use (Z, Y), Y faces (X, Z), Z faces (X, Y)), for an
		// object of that size: texelsPerMeter along each side, at least 4 and at most maxCardSize, in whole probe blocks.
		private static void CardSize(Vector3 size, int axis, int texelsPerMeter, int maxCardSize, out int width, out int height)
		{
			float extentU = Math.Max(axis == 0 ? size.Z : size.X, 0.01f);
			float extentV = Math.Max(axis == 1 ? size.Z : size.Y, 0.01f);
			width = Math.Clamp((int)Math.Ceiling(extentU * texelsPerMeter), 4, maxCardSize);
			height = Math.Clamp((int)Math.Ceiling(extentV * texelsPerMeter), 4, maxCardSize);
			width = (width + RadiosityBlock - 1) / RadiosityBlock * RadiosityBlock;
			height = (height + RadiosityBlock - 1) / RadiosityBlock * RadiosityBlock;
		}

		// Shelf packing on the given state (the atlas's own, or a copy to see whether a set fits).
		private bool Allocate(int width, int height, ref int nextX, ref int nextY, ref int rowHeight, out int x, out int y)
		{
			// Cards are aligned to the 4x4 texel blocks of the radiosity probes so no probe block spans two cards.
			width = (width + RadiosityBlock - 1) / RadiosityBlock * RadiosityBlock;
			height = (height + RadiosityBlock - 1) / RadiosityBlock * RadiosityBlock;

			if (nextX + width > Size)
			{
				nextY += rowHeight;
				nextX = 0;
				rowHeight = 0;
			}
			if (nextY + height > Size)
			{
				x = y = 0;
				return false;
			}

			x = nextX;
			y = nextY;
			nextX += width;
			rowHeight = Math.Max(rowHeight, height);
			return true;
		}

		// Once per atlas: what it means and what to change.
		private void ReportFull()
		{
			if (fullReported)
			{
				return;
			}

			fullReported = true;
			Debug.LogWarning($"[SurfaceCache] The atlas ({Size}x{Size}) is full after {sets.Count} objects: the objects that do not fit have no cards and give the GI no bounced light. "
				+ "Raise Surface Cache Atlas Size or lower Surface Cache Texels Per Meter (Project Settings > Lighting), or take objects out of the GI (Contributes To GI).");
		}

		// A square texture of one level, clamped, sampled linearly or by the nearest texel.
		private static GpuTexture CreateTexture(TextureFormat format, int size, TextureUsage usage, bool linear, string name)
		{
			GpuTexture texture = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(format, size, size, 1, usage, name));
			texture.Sampler = linear ? Samplers.LinearClamp : Samplers.PointClamp;
			return texture;
		}

		// The capture's pass: every attachment kept, or emptied (no card anywhere, the depth far).
		private RenderPassDescriptor DescribeCapture(LoadOperation load)
		{
			captureColors[0] = new ColorAttachment(Albedo, load);
			captureColors[1] = new ColorAttachment(Normal, load);
			captureColors[2] = new ColorAttachment(Emissive, load);
			captureColors[3] = new ColorAttachment(LocalPosition, load);
			captureColors[4] = new ColorAttachment(CardIndex, load, ClearValue.Integer(EmptyCardIndex));
			RenderPassDescriptor descriptor = default;
			descriptor.Colors = captureColors;
			descriptor.Depth = new DepthAttachment(captureDepth, load);
			return descriptor;
		}

		private static Vector3 Abs(Vector3 v)
		{
			return new Vector3(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
		}

		#endregion
	}
}
