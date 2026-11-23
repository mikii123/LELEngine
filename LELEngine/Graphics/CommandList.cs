using System;

namespace LELEngine.Graphics
{
	/// <summary>
	///     The commands the GPU will execute, in the order they are given. Draws happen inside a render pass
	///     (<see cref="BeginRenderPass(in RenderPassDescriptor)" /> .. <see cref="EndRenderPass" />); dispatches, copies,
	///     blits, mip generation, texture clears and acceleration structure builds happen outside one and end an open
	///     pass. What a draw or dispatch uses is what was set when it is given: the program (with the values and
	///     textures it holds then), the render state, the vertex input, and the buffers, images and acceleration
	///     structures bound by number.
	///     <para>
	///         Uploads (<see cref="GpuBuffer.SetData(void*, long)" />, <see cref="GpuTexture.SetData(int, void*, long)" />)
	///         are ordered with the commands and may be given anywhere, also inside a pass.
	///     </para>
	/// </summary>
	public abstract unsafe class CommandList
	{
		protected RenderState state = RenderState.Default;
		private readonly ColorAttachment[] singleColor = new ColorAttachment[1];

		/// <summary>
		///     The fixed-function state the next draws run with. It lasts until changed, also over passes; a frame starts
		///     with <see cref="RenderState.Default" />.
		/// </summary>
		public ref RenderState State
		{
			get
			{
				OnStateAccess();
				return ref state;
			}
		}

		/// <summary>The state is about to be read or changed.</summary>
		protected virtual void OnStateAccess()
		{ }

		#region Render passes

		/// <summary>
		///     Starts drawing into the attachments (an open pass ends first). The viewport and the scissor become the
		///     whole target.
		/// </summary>
		public abstract void BeginRenderPass(in RenderPassDescriptor descriptor);

		/// <summary>Ends the open pass; nothing happens without one.</summary>
		public abstract void EndRenderPass();

		public abstract bool InRenderPass { get; }

		/// <summary>Size of the open pass's target.</summary>
		public abstract int PassWidth { get; }

		public abstract int PassHeight { get; }

		/// <summary>Clears a colour attachment of the open pass: all of it, or a rectangle.</summary>
		public abstract void ClearColor(int attachment, in ClearValue value, int x = 0, int y = 0, int width = -1, int height = -1);

		/// <summary>Clears the depth attachment of the open pass: all of it, or a rectangle.</summary>
		public abstract void ClearDepth(float depth = 1f, int x = 0, int y = 0, int width = -1, int height = -1);

		public void BeginRenderPass(GpuTexture color, GpuTexture depth = null, LoadOperation colorLoad = LoadOperation.Load, ClearValue clearColor = default,
			LoadOperation depthLoad = LoadOperation.Load, float clearDepth = 1f)
		{
			RenderPassDescriptor descriptor = default;
			if (color != null)
			{
				singleColor[0] = new ColorAttachment(color, colorLoad, clearColor);
				descriptor.Colors = singleColor;
			}

			if (depth != null)
			{
				descriptor.Depth = new DepthAttachment(depth, depthLoad, clearDepth);
			}

			BeginRenderPass(descriptor);
		}

		/// <summary>A pass without attachments: draws only run their shaders, over a target of the given size.</summary>
		public void BeginRenderPass(int width, int height, int samples = 1)
		{
			RenderPassDescriptor descriptor = default;
			descriptor.Width = width;
			descriptor.Height = height;
			descriptor.Samples = samples;
			BeginRenderPass(descriptor);
		}

		/// <summary>Clears every colour attachment of the open pass to one value.</summary>
		public void ClearColors(in ClearValue value)
		{
			int count = PassColorCount;
			for (int i = 0; i < count; i++)
			{
				ClearColor(i, value);
			}
		}

		/// <summary>Colour attachments of the open pass.</summary>
		public abstract int PassColorCount { get; }

		#endregion

		#region State

		public abstract void SetViewport(int x, int y, int width, int height);

		/// <summary>Draws touch only this rectangle (rows counted from the bottom) until <see cref="DisableScissor" />.</summary>
		public abstract void SetScissor(int x, int y, int width, int height);

		public abstract void DisableScissor();

		public void SetRenderState(in RenderState value)
		{
			OnStateAccess();
			state = value;
		}

		#endregion

		#region Program and inputs

		/// <summary>The program of the next draws or dispatches.</summary>
		public abstract void SetProgram(GpuProgram program);

		public abstract GpuProgram Program { get; }

		/// <summary>The layout of the vertex buffers of the next draws (<see cref="VertexInputLayout.Empty" />: none).</summary>
		public abstract void SetVertexInput(VertexInputLayout layout);

		public abstract void SetVertexBuffer(int index, GpuBuffer buffer, long offset = 0);
		public abstract void SetIndexBuffer(GpuBuffer buffer, IndexFormat format = IndexFormat.UInt32);

		/// <summary>The buffer behind the uniform blocks declared with this binding number (size -1: to its end).</summary>
		public abstract void SetUniformBuffer(int binding, GpuBuffer buffer, long offset = 0, long size = -1);

		/// <summary>The buffer behind the storage blocks declared with this binding number (size -1: to its end).</summary>
		public abstract void SetStorageBuffer(int binding, GpuBuffer buffer, long offset = 0, long size = -1);

		/// <summary>
		///     The texture behind the images declared with this binding number: one mip level, and either every layer
		///     or slice (layer -1) or one.
		/// </summary>
		public abstract void SetImage(int binding, GpuTexture texture, int mip = 0, int layer = -1, ImageAccess access = ImageAccess.ReadWrite);

		#endregion

		#region Draws

		public abstract void Draw(int vertexCount, int firstVertex = 0, int instanceCount = 1, int firstInstance = 0, PrimitiveTopology topology = PrimitiveTopology.Triangles);

		public abstract void DrawIndexed(int indexCount, int firstIndex = 0, int baseVertex = 0, int instanceCount = 1, int firstInstance = 0,
			PrimitiveTopology topology = PrimitiveTopology.Triangles);

		/// <summary>Draws whose arguments (vertex count, instance count, first vertex, first instance: 4 uints each) the GPU reads from a buffer.</summary>
		public abstract void DrawIndirect(GpuBuffer arguments, long offset, int drawCount = 1, int stride = 16, PrimitiveTopology topology = PrimitiveTopology.Triangles);

		/// <summary>Indexed draws whose arguments (index count, instance count, first index, base vertex, first instance: 5 uints each) the GPU reads from a buffer.</summary>
		public abstract void DrawIndexedIndirect(GpuBuffer arguments, long offset, int drawCount = 1, int stride = 20, PrimitiveTopology topology = PrimitiveTopology.Triangles);

		/// <summary>As <see cref="DrawIndexedIndirect" />, with the number of draws read from a buffer too (at most <paramref name="maxDrawCount" />).</summary>
		public abstract void DrawIndexedIndirectCount(GpuBuffer arguments, long offset, GpuBuffer count, long countOffset, int maxDrawCount, int stride = 20,
			PrimitiveTopology topology = PrimitiveTopology.Triangles);

		/// <summary>Several indexed draws of one mesh: index counts, first indices and base vertices per draw.</summary>
		public virtual void DrawIndexedRanges(ReadOnlySpan<int> indexCounts, ReadOnlySpan<int> firstIndices, ReadOnlySpan<int> baseVertices, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			for (int i = 0; i < indexCounts.Length; i++)
			{
				DrawIndexed(indexCounts[i], firstIndices[i], baseVertices[i], 1, 0, topology);
			}
		}

		#endregion

		#region Compute

		/// <summary>Runs the program's compute stage over a grid of work groups.</summary>
		public abstract void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1);

		/// <summary>A dispatch whose three group counts (uints) the GPU reads from a buffer.</summary>
		public abstract void DispatchIndirect(GpuBuffer arguments, long offset);

		#endregion

		#region Copies

		public abstract void CopyBuffer(GpuBuffer source, long sourceOffset, GpuBuffer destination, long destinationOffset, long size);

		/// <summary>Copies a box of texels between textures of formats with the same texel size (z: slice, layer or face).</summary>
		public abstract void CopyTexture(GpuTexture source, int sourceMip, int sourceX, int sourceY, int sourceZ, GpuTexture destination, int destinationMip, int destinationX,
			int destinationY, int destinationZ, int width, int height, int depth);

		/// <summary>
		///     Copies a rectangle of one layer with scaling and format conversion (colour to colour, depth to depth with
		///     <see cref="SamplerFilter.Nearest" />). A rectangle given backwards (x1 &lt; x0) mirrors.
		/// </summary>
		public abstract void Blit(GpuTexture source, int sourceMip, int sourceLayer, int sourceX0, int sourceY0, int sourceX1, int sourceY1, GpuTexture destination,
			int destinationMip, int destinationLayer, int destinationX0, int destinationY0, int destinationX1, int destinationY1, SamplerFilter filter);

		/// <summary>Fills a whole mip level (every layer or slice) with a value.</summary>
		public abstract void ClearTexture(GpuTexture texture, int mip, in ClearValue value);

		/// <summary>Fills every mip level below the first by filtering the level above.</summary>
		public abstract void GenerateMipmaps(GpuTexture texture);

		/// <summary>
		///     Copies a box of a mip level into a buffer, in the texture's own format with rows packed: read the buffer
		///     (<see cref="GpuBuffer.GetData(long, void*, long)" />) once a <see cref="GpuFence" /> made after this signals.
		/// </summary>
		public abstract void CopyTextureToBuffer(GpuTexture source, int mip, int x, int y, int z, int width, int height, int depth, GpuBuffer destination, long destinationOffset);

		public void CopyTexture(GpuTexture source, GpuTexture destination, int mip = 0)
		{
			CopyTexture(source, mip, 0, 0, 0, destination, mip, 0, 0, 0, source.MipWidth(mip), source.MipHeight(mip), source.MipDepth(mip));
		}

		public void Blit(GpuTexture source, GpuTexture destination, SamplerFilter filter = SamplerFilter.Nearest)
		{
			Blit(source, 0, 0, 0, 0, source.Width, source.Height, destination, 0, 0, 0, 0, destination.Width, destination.Height, filter);
		}

		#endregion

		#region Acceleration structures

		// A device without ray queries (DeviceCapabilities.RayQuery false) throws NotSupportedException for every command
		// of acceleration structures.

		/// <summary>
		///     Builds bottom levels from the triangles they were made with, as the buffers hold them when the builds run:
		///     from scratch, or (<paramref name="update" />) refitted to moved vertices of the same triangles (structures
		///     made with <see cref="AccelerationStructureFlags.AllowUpdate" /> and built before). Like a dispatch it ends an
		///     open pass; what draws and dispatches given after it trace sees the builds.
		/// </summary>
		public abstract void BuildBottomLevels(ReadOnlySpan<GpuAccelerationStructure> bottomLevels, bool update = false);

		/// <summary>
		///     Builds a top level from instances (at most its <see cref="GpuAccelerationStructure.MaxInstances" />): from
		///     scratch, or (<paramref name="update" />) refitted to new transforms of the same instances, the same count of
		///     the same bottom levels (made with <see cref="AccelerationStructureFlags.AllowUpdate" />, built before). The
		///     bottom levels are built already (given earlier). Ends an open pass.
		/// </summary>
		public abstract void BuildTopLevel(GpuAccelerationStructure topLevel, ReadOnlySpan<AccelerationStructureInstance> instances, bool update = false);

		/// <summary>
		///     Copies a built structure into another of its level: compacted into one made for that
		///     (<see cref="AccelerationStructureDescriptor.CompactedFrom" />). Ends an open pass. A top level that holds
		///     the source still holds the source: build it again with the copy.
		/// </summary>
		public abstract void CopyAccelerationStructure(GpuAccelerationStructure source, GpuAccelerationStructure destination);

		/// <summary>
		///     The top level behind the acceleration structures the shaders declare with this binding number
		///     (<c>layout(binding = N) uniform accelerationStructureEXT</c>); null: an empty one.
		/// </summary>
		public abstract void SetAccelerationStructure(int binding, GpuAccelerationStructure topLevel);

		/// <summary>Builds one bottom level (<see cref="BuildBottomLevels" />).</summary>
		public void BuildBottomLevel(GpuAccelerationStructure bottomLevel, bool update = false)
		{
			singleStructure[0] = bottomLevel;
			try
			{
				BuildBottomLevels(singleStructure, update);
			}
			finally
			{
				singleStructure[0] = null;
			}
		}

		private readonly GpuAccelerationStructure[] singleStructure = new GpuAccelerationStructure[1];

		#endregion

		#region Queries

		/// <summary>The GPU writes its clock into the stamp when it gets here.</summary>
		public abstract void WriteTimestamp(GpuTimestamp timestamp);

		#endregion
	}
}
