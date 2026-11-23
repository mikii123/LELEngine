using System;
using System.Collections.Generic;
using Brutal.OpenGlApi;
using static Brutal.OpenGlApi.OpenGl;

namespace LELEngine.Graphics.OpenGL
{
	/// <summary>
	///     The commands on OpenGL: executed as they are given, over one context's state. What the context was last told
	///     is remembered, so only differences reach the driver. Vertex array and framebuffer objects are per context:
	///     one vertex array per vertex layout (the buffers are bound to its binding points per draw), one framebuffer
	///     object per set of attachments.
	/// </summary>
	internal sealed unsafe class GLCommandList : CommandList
	{
		private const int MaxVertexBuffers = 8;
		private const int MaxBufferBindings = 96;

		private readonly GLDevice device;
		private readonly GLContext context;
		private readonly OpenGLBackend gl;

		// The open pass.
		private bool inPass;
		private int passWidth;
		private int passHeight;
		private int passColorCount;
		private bool passHasDepth;
		private readonly TextureFormat[] passColorFormats = new TextureFormat[8];

		// What the context was last told.
		private RenderState applied;
		private bool appliedValid;
		private int boundProgram = -1;
		private int boundVertexArray = -1;
		private int boundFramebuffer = -1;
		private int activeUnit = -1;
		private readonly int[] unitTextures;
		private readonly TextureTarget[] unitTargets;
		private readonly int[] unitSamplers;
		private int viewportX = -1;
		private int viewportY;
		private int viewportWidth;
		private int viewportHeight;
		private bool scissorOn;
		private bool scissorApplied;
		private int scissorX;
		private int scissorY;
		private int scissorWidth;
		private int scissorHeight;
		private bool scissorRectApplied;
		private readonly BufferBinding[] uniformBuffers = new BufferBinding[MaxBufferBindings];
		private readonly BufferBinding[] storageBuffers = new BufferBinding[MaxBufferBindings];

		// What the next draw uses.
		private GLProgram program;
		private VertexInputLayout layout = VertexInputLayout.Empty;
		private readonly GLBuffer[] vertexBuffers = new GLBuffer[MaxVertexBuffers];
		private readonly long[] vertexOffsets = new long[MaxVertexBuffers];
		private GLBuffer indexBuffer;
		private IndexFormat indexFormat = IndexFormat.UInt32;

		// Container objects of this context.
		private readonly Dictionary<int, VertexArray> vertexArrays = new Dictionary<int, VertexArray>();
		private readonly Dictionary<FramebufferKey, int> framebuffers = new Dictionary<FramebufferKey, int>();
		private int readFramebuffer;
		private int drawFramebuffer;
		private int deletionsSeen;
		private GLSampler defaultSampler;

		/// <summary>A draw or dispatch wrote images or storage blocks: a barrier goes before whatever comes next.</summary>
		private bool barrierPending;

		private int deletedEpochSeen;

		public GLCommandList(GLDevice device, GLContext context)
		{
			this.device = device;
			this.context = context;
			gl = GLDevice.Gl;
			int units = device.ScratchUnit + 1;
			unitTextures = new int[units];
			unitTargets = new TextureTarget[units];
			unitSamplers = new int[units];
			deletionsSeen = device.TextureDeletions;
			// Texel rows are packed; cube maps filter across faces and points take their size from the shader, as on
			// every other API.
			gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
			gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
			gl.Enable(EnableCap.TextureCubeMapSeamless);
			gl.Enable(EnableCap.ProgramPointSize);
		}

		#region Render passes

		public override bool InRenderPass => inPass;
		public override int PassWidth => passWidth;
		public override int PassHeight => passHeight;
		public override int PassColorCount => passColorCount;

		public override void BeginRenderPass(in RenderPassDescriptor descriptor)
		{
			EndRenderPass();
			FlushBarriers();
			ForgetDeletedTextures();
			int colorCount = descriptor.Colors.Length;
			GpuTexture depth = descriptor.Depth.Texture;
			int framebuffer;
			if (colorCount == 1 && descriptor.Colors[0].Texture.IsBackbuffer)
			{
				if (depth != null)
				{
					throw new ArgumentException("A window's back buffer is drawn into without a depth attachment.");
				}

				framebuffer = 0;
				passWidth = descriptor.Colors[0].Texture.Width;
				passHeight = descriptor.Colors[0].Texture.Height;
			}
			else
			{
				FramebufferKey key = default;
				key.Count = colorCount;
				for (int i = 0; i < colorCount; i++)
				{
					ColorAttachment color = descriptor.Colors[i];
					if (color.Texture == null || color.Texture.IsBackbuffer || color.Texture.IsDisposed)
					{
						throw new ArgumentException($"Colour attachment {i} of a render pass is missing, disposed or a window's back buffer among other attachments.");
					}

					key.Set(i, ((GLTexture)color.Texture).Handle, color.Mip, color.Layer);
				}

				if (depth != null)
				{
					key.Set(8, ((GLTexture)depth).Handle, descriptor.Depth.Mip, descriptor.Depth.Layer);
				}

				GpuTexture first = colorCount > 0 ? descriptor.Colors[0].Texture : depth;
				int firstMip = colorCount > 0 ? descriptor.Colors[0].Mip : descriptor.Depth.Mip;
				if (first != null)
				{
					passWidth = first.MipWidth(firstMip);
					passHeight = first.MipHeight(firstMip);
				}
				else
				{
					passWidth = Math.Max(1, descriptor.Width);
					passHeight = Math.Max(1, descriptor.Height);
					key.Width = passWidth;
					key.Height = passHeight;
					// The sample count of a pass without attachments, in the key's count (which is 0 colours here).
					key.Count = Math.Max(1, descriptor.Samples) << 16;
				}

				if (!framebuffers.TryGetValue(key, out framebuffer))
				{
					framebuffer = CreateFramebuffer(descriptor, key);
					framebuffers.Add(key, framebuffer);
				}
			}

			BindFramebuffer(framebuffer);
			passColorCount = colorCount;
			passHasDepth = depth != null;
			for (int i = 0; i < colorCount; i++)
			{
				passColorFormats[i] = descriptor.Colors[i].Texture.Format;
			}

			inPass = true;
			SetViewport(0, 0, passWidth, passHeight);
			DisableScissor();
			for (int i = 0; i < colorCount; i++)
			{
				if (descriptor.Colors[i].Load == LoadOperation.Clear)
				{
					ClearColor(i, descriptor.Colors[i].Clear);
				}
			}

			if (depth != null && descriptor.Depth.Load == LoadOperation.Clear)
			{
				ClearDepth(descriptor.Depth.Clear);
			}
		}

		public override void EndRenderPass()
		{
			inPass = false;
		}

		public override void ClearColor(int attachment, in ClearValue value, int x = 0, int y = 0, int width = -1, int height = -1)
		{
			RequirePass("ClearColor");
			if (attachment < 0 || attachment >= passColorCount)
			{
				return;
			}

			if (!appliedValid || applied.ColorMask != ColorWriteMask.All)
			{
				gl.ColorMask(true, true, true, true);
				applied.ColorMask = ColorWriteMask.All;
			}

			BeginClearArea(x, y, width, height);
			if (passColorFormats[attachment].IsInteger())
			{
				uint* integers = stackalloc uint[4] { value.IntegerR, value.IntegerG, value.IntegerB, value.IntegerA };
				gl.ClearBufferuiv(ClearBuffer.Color, attachment, integers);
			}
			else
			{
				float* floats = stackalloc float[4] { value.R, value.G, value.B, value.A };
				gl.ClearBufferfv(ClearBuffer.Color, attachment, floats);
			}

			EndClearArea();
		}

		public override void ClearDepth(float depth = 1f, int x = 0, int y = 0, int width = -1, int height = -1)
		{
			RequirePass("ClearDepth");
			if (!passHasDepth)
			{
				return;
			}

			if (!appliedValid || !applied.DepthWrite)
			{
				gl.DepthMask(true);
				applied.DepthWrite = true;
			}

			BeginClearArea(x, y, width, height);
			gl.ClearBufferfv(ClearBuffer.Depth, 0, &depth);
			EndClearArea();
		}

		#endregion

		#region State

		public override void SetViewport(int x, int y, int width, int height)
		{
			if (x != viewportX || y != viewportY || width != viewportWidth || height != viewportHeight)
			{
				gl.Viewport(x, y, width, height);
				viewportX = x;
				viewportY = y;
				viewportWidth = width;
				viewportHeight = height;
			}
		}

		public override void SetScissor(int x, int y, int width, int height)
		{
			scissorOn = true;
			scissorX = x;
			scissorY = y;
			scissorWidth = Math.Max(0, width);
			scissorHeight = Math.Max(0, height);
			ApplyScissor();
		}

		public override void DisableScissor()
		{
			scissorOn = false;
			ApplyScissor();
		}

		#endregion

		#region Program and inputs

		public override GpuProgram Program => program;

		public override void SetProgram(GpuProgram value)
		{
			program = (GLProgram)value;
		}

		public override void SetVertexInput(VertexInputLayout value)
		{
			layout = value ?? VertexInputLayout.Empty;
		}

		public override void SetVertexBuffer(int index, GpuBuffer buffer, long offset = 0)
		{
			vertexBuffers[index] = (GLBuffer)buffer;
			vertexOffsets[index] = offset;
		}

		public override void SetIndexBuffer(GpuBuffer buffer, IndexFormat format = IndexFormat.UInt32)
		{
			indexBuffer = (GLBuffer)buffer;
			indexFormat = format;
		}

		public override void SetUniformBuffer(int binding, GpuBuffer buffer, long offset = 0, long size = -1)
		{
			BindBufferRange(BufferRangeTarget.UniformBuffer, uniformBuffers, binding, (GLBuffer)buffer, offset, size);
		}

		public override void SetStorageBuffer(int binding, GpuBuffer buffer, long offset = 0, long size = -1)
		{
			BindBufferRange(BufferRangeTarget.ShaderStorageBuffer, storageBuffers, binding, (GLBuffer)buffer, offset, size);
		}

		public override void SetImage(int binding, GpuTexture texture, int mip = 0, int layer = -1, ImageAccess access = ImageAccess.ReadWrite)
		{
			GLTexture image = (GLTexture)texture;
			if (image == null)
			{
				gl.BindImageTexture(binding, 0, 0, false, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba8);
				return;
			}

			TextureAccess glAccess = access == ImageAccess.Read ? TextureAccess.ReadOnly : access == ImageAccess.Write ? TextureAccess.WriteOnly : TextureAccess.ReadWrite;
			gl.BindImageTexture(binding, image.Handle, mip, layer < 0, Math.Max(0, layer), glAccess, (SizedInternalFormat)image.GL.Internal);
		}

		#endregion

		#region Draws

		public override void Draw(int vertexCount, int firstVertex = 0, int instanceCount = 1, int firstInstance = 0, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (vertexCount <= 0 || instanceCount <= 0 || !PrepareDraw(false))
			{
				return;
			}

			if (instanceCount == 1 && firstInstance == 0)
			{
				gl.DrawArrays(Mode(topology), firstVertex, vertexCount);
			}
			else
			{
				Native.glDrawArraysInstancedBaseInstance((uint)Mode(topology), firstVertex, vertexCount, instanceCount, (uint)firstInstance);
			}

			AfterDraw();
		}

		public override void DrawIndexed(int indexCount, int firstIndex = 0, int baseVertex = 0, int instanceCount = 1, int firstInstance = 0,
			PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (indexCount <= 0 || instanceCount <= 0 || !PrepareDraw(true))
			{
				return;
			}

			IntPtr offset = (IntPtr)((long)firstIndex * IndexSize);
			if (instanceCount == 1 && firstInstance == 0)
			{
				if (baseVertex == 0)
				{
					gl.DrawElements(Mode(topology), indexCount, IndexType, offset);
				}
				else
				{
					gl.DrawElementsBaseVertex(Mode(topology), indexCount, IndexType, offset, baseVertex);
				}
			}
			else
			{
				gl.DrawElementsInstancedBaseVertexBaseInstance(Mode(topology), indexCount, IndexType, offset, instanceCount, baseVertex, firstInstance);
			}

			AfterDraw();
		}

		public override void DrawIndexedRanges(ReadOnlySpan<int> indexCounts, ReadOnlySpan<int> firstIndices, ReadOnlySpan<int> baseVertices, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			int count = indexCounts.Length;
			if (count == 0 || !PrepareDraw(true))
			{
				return;
			}

			IntPtr* offsets = stackalloc IntPtr[count];
			for (int i = 0; i < count; i++)
			{
				offsets[i] = (IntPtr)((long)firstIndices[i] * IndexSize);
			}

			fixed (int* counts = indexCounts)
			fixed (int* bases = baseVertices)
			{
				gl.MultiDrawElementsBaseVertex(Mode(topology), counts, IndexType, offsets, count, bases);
			}

			AfterDraw();
		}

		public override void DrawIndirect(GpuBuffer arguments, long offset, int drawCount = 1, int stride = 16, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (drawCount <= 0 || !PrepareDraw(false))
			{
				return;
			}

			gl.BindBuffer(BufferTarget.DrawIndirectBuffer, ((GLBuffer)arguments).Handle);
			for (int i = 0; i < drawCount; i++)
			{
				gl.DrawArraysIndirect(Mode(topology), (IntPtr)(offset + (long)i * stride));
			}

			gl.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
			AfterDraw();
		}

		public override void DrawIndexedIndirect(GpuBuffer arguments, long offset, int drawCount = 1, int stride = 20, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (drawCount <= 0 || !PrepareDraw(true))
			{
				return;
			}

			gl.BindBuffer(BufferTarget.DrawIndirectBuffer, ((GLBuffer)arguments).Handle);
			if (drawCount == 1)
			{
				gl.DrawElementsIndirect(Mode(topology), IndexType, (IntPtr)offset);
			}
			else
			{
				gl.MultiDrawElementsIndirect(Mode(topology), IndexType, (IntPtr)offset, drawCount, stride);
			}

			gl.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
			AfterDraw();
		}

		public override void DrawIndexedIndirectCount(GpuBuffer arguments, long offset, GpuBuffer count, long countOffset, int maxDrawCount, int stride = 20,
			PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (maxDrawCount <= 0 || !PrepareDraw(true))
			{
				return;
			}

			gl.BindBuffer(BufferTarget.DrawIndirectBuffer, ((GLBuffer)arguments).Handle);
			gl.BindBuffer(BufferTarget.ParameterBuffer, ((GLBuffer)count).Handle);
			gl.MultiDrawElementsIndirectCount(Mode(topology), IndexType, (IntPtr)offset, (IntPtr)countOffset, maxDrawCount, stride);
			gl.BindBuffer(BufferTarget.ParameterBuffer, 0);
			gl.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
			AfterDraw();
		}

		#endregion

		#region Compute

		public override void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
		{
			if (!PrepareDispatch())
			{
				return;
			}

			gl.DispatchCompute(groupsX, groupsY, groupsZ);
			barrierPending = true;
		}

		public override void DispatchIndirect(GpuBuffer arguments, long offset)
		{
			if (!PrepareDispatch())
			{
				return;
			}

			gl.BindBuffer(BufferTarget.DispatchIndirectBuffer, ((GLBuffer)arguments).Handle);
			gl.DispatchComputeIndirect((IntPtr)offset);
			gl.BindBuffer(BufferTarget.DispatchIndirectBuffer, 0);
			barrierPending = true;
		}

		#endregion

		#region Copies

		public override void CopyBuffer(GpuBuffer source, long sourceOffset, GpuBuffer destination, long destinationOffset, long size)
		{
			EndRenderPass();
			FlushBarriers();
			gl.BindBuffer(BufferTarget.CopyReadBuffer, ((GLBuffer)source).Handle);
			gl.BindBuffer(BufferTarget.CopyWriteBuffer, ((GLBuffer)destination).Handle);
			gl.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, (IntPtr)sourceOffset, (IntPtr)destinationOffset, (IntPtr)size);
		}

		public override void CopyTexture(GpuTexture source, int sourceMip, int sourceX, int sourceY, int sourceZ, GpuTexture destination, int destinationMip, int destinationX,
			int destinationY, int destinationZ, int width, int height, int depth)
		{
			EndRenderPass();
			FlushBarriers();
			GLTexture from = (GLTexture)source;
			GLTexture to = (GLTexture)destination;
			gl.CopyImageSubData(from.Handle, (ImageTarget)from.Target, sourceMip, sourceX, sourceY, sourceZ, to.Handle, (ImageTarget)to.Target, destinationMip, destinationX, destinationY,
				destinationZ, width, height, depth);
		}

		public override void Blit(GpuTexture source, int sourceMip, int sourceLayer, int sourceX0, int sourceY0, int sourceX1, int sourceY1, GpuTexture destination,
			int destinationMip, int destinationLayer, int destinationX0, int destinationY0, int destinationX1, int destinationY1, SamplerFilter filter)
		{
			EndRenderPass();
			FlushBarriers();
			GLTexture from = (GLTexture)source;
			GLTexture to = (GLTexture)destination;
			bool depth = from.Format.IsDepth();
			if (readFramebuffer == 0)
			{
				readFramebuffer = gl.GenFramebuffer();
				drawFramebuffer = gl.GenFramebuffer();
			}

			FramebufferAttachment point = depth ? FramebufferAttachment.DepthAttachment : FramebufferAttachment.ColorAttachment0;
			int read = from.IsBackbuffer ? 0 : readFramebuffer;
			int draw = to.IsBackbuffer ? 0 : drawFramebuffer;
			gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
			gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
			boundFramebuffer = -1;
			if (read != 0)
			{
				Attach(FramebufferTarget.ReadFramebuffer, point, from, sourceMip, sourceLayer);
				gl.ReadBuffer(depth ? ReadBufferMode.None : ReadBufferMode.ColorAttachment0);
			}
			else
			{
				gl.ReadBuffer(ReadBufferMode.Back);
			}

			if (draw != 0)
			{
				Attach(FramebufferTarget.DrawFramebuffer, point, to, destinationMip, destinationLayer);
				gl.DrawBuffer(depth ? DrawBufferMode.None : DrawBufferMode.ColorAttachment0);
			}

			bool scissor = scissorApplied;
			if (scissor)
			{
				gl.Disable(EnableCap.ScissorTest);
			}

			if (!depth && (!appliedValid || applied.ColorMask != ColorWriteMask.All))
			{
				gl.ColorMask(true, true, true, true);
				applied.ColorMask = ColorWriteMask.All;
			}

			if (depth && (!appliedValid || !applied.DepthWrite))
			{
				gl.DepthMask(true);
				applied.DepthWrite = true;
			}

			gl.BlitFramebuffer(sourceX0, sourceY0, sourceX1, sourceY1, destinationX0, destinationY0, destinationX1, destinationY1,
				depth ? ClearBufferMask.DepthBufferBit : ClearBufferMask.ColorBufferBit, filter == SamplerFilter.Linear && !depth ? BlitFramebufferFilter.Linear : BlitFramebufferFilter.Nearest);
			if (scissor)
			{
				gl.Enable(EnableCap.ScissorTest);
			}

			// The scratch objects keep no reference to the textures.
			if (read != 0)
			{
				gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, point, TextureTarget.Texture2D, 0, 0);
			}

			if (draw != 0)
			{
				gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, point, TextureTarget.Texture2D, 0, 0);
			}
		}

		public override void ClearTexture(GpuTexture texture, int mip, in ClearValue value)
		{
			EndRenderPass();
			FlushBarriers();
			GLTexture target = (GLTexture)texture;
			if (target.Format.IsInteger())
			{
				uint* integers = stackalloc uint[4] { value.IntegerR, value.IntegerG, value.IntegerB, value.IntegerA };
				gl.ClearTexImage(target.Handle, mip, PixelFormat.RgbaInteger, PixelType.UnsignedInt, integers);
			}
			else if (target.Format.IsDepth())
			{
				float depth = value.R;
				gl.ClearTexImage(target.Handle, mip, PixelFormat.DepthComponent, PixelType.Float, &depth);
			}
			else
			{
				float* floats = stackalloc float[4] { value.R, value.G, value.B, value.A };
				gl.ClearTexImage(target.Handle, mip, PixelFormat.Rgba, PixelType.Float, floats);
			}
		}

		public override void GenerateMipmaps(GpuTexture texture)
		{
			if (texture.MipLevels <= 1)
			{
				return;
			}

			EndRenderPass();
			FlushBarriers();
			GLTexture target = (GLTexture)texture;
			BindForEdit(target);
			gl.GenerateMipmap((GenerateMipmapTarget)target.Target);
		}

		public override void CopyTextureToBuffer(GpuTexture source, int mip, int x, int y, int z, int width, int height, int depth, GpuBuffer destination, long destinationOffset)
		{
			EndRenderPass();
			GLBuffer buffer = (GLBuffer)destination;
			long size = source.Format.LevelSize(width, height, depth);
			if (destinationOffset < 0 || destinationOffset + size > buffer.Size)
			{
				throw new ArgumentException($"Buffer '{buffer.Name}' ({buffer.Size} bytes) cannot take {size} bytes of texels at {destinationOffset}.");
			}

			ReadTexture((GLTexture)source, mip, x, y, z, width, height, depth, null, (int)Math.Min(int.MaxValue, size), buffer, destinationOffset);
		}

		#endregion

		#region Queries

		public override void WriteTimestamp(GpuTimestamp timestamp)
		{
			((GLTimestamp)timestamp).Write();
		}

		#endregion

		#region For the device

		/// <summary>Binds a texture to the device's own unit for making, writing or reading it; draws never use that unit.</summary>
		internal void BindForEdit(GLTexture texture)
		{
			if (activeUnit != device.ScratchUnit)
			{
				gl.ActiveTexture(TextureUnit.Texture0 + device.ScratchUnit);
				activeUnit = device.ScratchUnit;
			}

			gl.BindTexture(texture.Target, texture.Handle);
		}

		/// <summary>Makes what shaders wrote into images and storage blocks visible to what comes next.</summary>
		internal void FlushBarriers()
		{
			if (barrierPending)
			{
				gl.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
				barrierPending = false;
			}
		}

		internal void ReadTexture(GLTexture texture, int mip, int x, int y, int z, int width, int height, int depth, void* destination, int size, GLBuffer packBuffer, long packOffset)
		{
			EndRenderPass();
			FlushBarriers();
			if (packBuffer != null)
			{
				gl.BindBuffer(BufferTarget.PixelPackBuffer, packBuffer.Handle);
				destination = (void*)packOffset;
			}

			if (texture.IsBackbuffer)
			{
				gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
				boundFramebuffer = -1;
				gl.ReadBuffer(ReadBufferMode.Back);
				gl.ReadPixels(x, y, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, destination);
			}
			else if (texture.Format.IsCompressed())
			{
				if (!device.IsAtLeast(4, 5))
				{
					throw new NotSupportedException("Reading a compressed texture back needs OpenGL 4.5.");
				}

				Native.glGetCompressedTextureSubImage((uint)texture.Handle, mip, x, y, z, width, height, depth, size, destination);
			}
			else if (device.IsAtLeast(4, 5))
			{
				gl.GetTextureSubImage(texture.Handle, mip, x, y, z, width, height, depth, texture.GL.Format, texture.GL.Type, size, destination);
			}
			else
			{
				// Before 4.5 a box is read layer by layer through a framebuffer object.
				if (readFramebuffer == 0)
				{
					readFramebuffer = gl.GenFramebuffer();
					drawFramebuffer = gl.GenFramebuffer();
				}

				bool isDepth = texture.Format.IsDepth();
				FramebufferAttachment point = isDepth ? FramebufferAttachment.DepthAttachment : FramebufferAttachment.ColorAttachment0;
				gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFramebuffer);
				boundFramebuffer = -1;
				long layerSize = texture.Format.LevelSize(width, height);
				for (int layer = 0; layer < depth; layer++)
				{
					Attach(FramebufferTarget.ReadFramebuffer, point, texture, mip, z + layer);
					gl.ReadBuffer(isDepth ? ReadBufferMode.None : ReadBufferMode.ColorAttachment0);
					gl.ReadPixels(x, y, width, height, texture.GL.Format, texture.GL.Type, (byte*)destination + layer * layerSize);
				}

				gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, point, TextureTarget.Texture2D, 0, 0);
			}

			if (packBuffer != null)
			{
				gl.BindBuffer(BufferTarget.PixelPackBuffer, 0);
			}
		}

		/// <summary>A texture or buffer is being deleted: nothing remembered may name it (OpenGL gives names out again).</summary>
		internal void Forget(int handle, bool buffer)
		{
			if (buffer)
			{
				for (int i = 0; i < MaxBufferBindings; i++)
				{
					if (uniformBuffers[i].Handle == handle)
					{
						uniformBuffers[i] = default;
					}

					if (storageBuffers[i].Handle == handle)
					{
						storageBuffers[i] = default;
					}
				}

				foreach (VertexArray array in vertexArrays.Values)
				{
					for (int i = 0; i < MaxVertexBuffers; i++)
					{
						if (array.Buffers[i] == handle)
						{
							array.Buffers[i] = 0;
						}
					}

					if (array.ElementBuffer == handle)
					{
						array.ElementBuffer = 0;
					}
				}

				return;
			}

			for (int i = 0; i < unitTextures.Length; i++)
			{
				if (unitTextures[i] == handle)
				{
					unitTextures[i] = 0;
				}
			}

			ForgetDeletedTextures();
		}

		internal void ForgetProgram(int handle)
		{
			if (boundProgram == handle)
			{
				boundProgram = -1;
			}
		}

		/// <summary>Deletes the context's own objects (the context is current and going away).</summary>
		internal void DeleteContainers()
		{
			foreach (VertexArray array in vertexArrays.Values)
			{
				gl.DeleteVertexArray(array.Handle);
			}

			foreach (int framebuffer in framebuffers.Values)
			{
				gl.DeleteFramebuffer(framebuffer);
			}

			vertexArrays.Clear();
			framebuffers.Clear();
			if (readFramebuffer != 0)
			{
				gl.DeleteFramebuffer(readFramebuffer);
				gl.DeleteFramebuffer(drawFramebuffer);
				readFramebuffer = drawFramebuffer = 0;
			}
		}

		#endregion

		#region Translation

		internal static int Compare(CompareFunction function)
		{
			return 0x200 + (int)function;
		}

		private static PrimitiveType Mode(PrimitiveTopology topology)
		{
			switch (topology)
			{
				case PrimitiveTopology.Lines: return PrimitiveType.Lines;
				case PrimitiveTopology.Points: return PrimitiveType.Points;
				case PrimitiveTopology.TriangleStrip: return PrimitiveType.TriangleStrip;
				case PrimitiveTopology.LineStrip: return PrimitiveType.LineStrip;
				default: return PrimitiveType.Triangles;
			}
		}

		private static int Factor(BlendFactor factor)
		{
			switch (factor)
			{
				case BlendFactor.Zero: return 0;
				case BlendFactor.One: return 1;
				case BlendFactor.SrcColor: return 0x300;
				case BlendFactor.OneMinusSrcColor: return 0x301;
				case BlendFactor.SrcAlpha: return 0x302;
				case BlendFactor.OneMinusSrcAlpha: return 0x303;
				case BlendFactor.DstAlpha: return 0x304;
				case BlendFactor.OneMinusDstAlpha: return 0x305;
				case BlendFactor.DstColor: return 0x306;
				case BlendFactor.OneMinusDstColor: return 0x307;
				case BlendFactor.SrcAlphaSaturate: return 0x308;
				case BlendFactor.ConstantColor: return 0x8001;
				case BlendFactor.OneMinusConstantColor: return 0x8002;
				case BlendFactor.ConstantAlpha: return 0x8003;
				default: return 0x8004;
			}
		}

		private static BlendEquationMode Equation(BlendOperation operation)
		{
			switch (operation)
			{
				case BlendOperation.Subtract: return (BlendEquationMode)0x800A;
				case BlendOperation.ReverseSubtract: return (BlendEquationMode)0x800B;
				case BlendOperation.Min: return (BlendEquationMode)0x8007;
				case BlendOperation.Max: return (BlendEquationMode)0x8008;
				default: return (BlendEquationMode)0x8006;
			}
		}

		private DrawElementsType IndexType => indexFormat == IndexFormat.UInt16 ? DrawElementsType.UnsignedShort : DrawElementsType.UnsignedInt;
		private int IndexSize => indexFormat == IndexFormat.UInt16 ? 2 : 4;

		#endregion

		#region Applying state

		private void RequirePass(string what)
		{
			if (!inPass)
			{
				throw new InvalidOperationException(what + " outside a render pass: begin one first (a dispatch, copy, blit or mip generation ends the open pass).");
			}
		}

		private bool PrepareDraw(bool indexed)
		{
			RequirePass("A draw");
			if (program == null || program.IsDisposed || !program.IsLinked)
			{
				return false;
			}

			if (indexed && indexBuffer == null)
			{
				throw new InvalidOperationException("An indexed draw without an index buffer.");
			}

			FlushBarriers();
			ApplyRenderState();
			ApplyProgram();
			ApplyVertexInput(indexed);
			return true;
		}

		private void AfterDraw()
		{
			if (program.WritesStorage)
			{
				barrierPending = true;
			}
		}

		private bool PrepareDispatch()
		{
			EndRenderPass();
			if (program == null || program.IsDisposed || !program.IsLinked || !program.IsCompute)
			{
				return false;
			}

			FlushBarriers();
			ApplyProgram();
			return true;
		}

		private void ApplyRenderState()
		{
			if (appliedValid && applied.Equals(state))
			{
				return;
			}

			bool all = !appliedValid;
			if (all || applied.DepthTest != state.DepthTest)
			{
				Toggle(EnableCap.DepthTest, state.DepthTest);
			}

			if (all || applied.DepthWrite != state.DepthWrite)
			{
				gl.DepthMask(state.DepthWrite);
			}

			if (all || applied.DepthCompare != state.DepthCompare)
			{
				gl.DepthFunc((DepthFunction)Compare(state.DepthCompare));
			}

			if (all || applied.Culling != state.Culling)
			{
				Toggle(EnableCap.CullFace, state.Culling != FaceCulling.None);
				if (state.Culling != FaceCulling.None)
				{
					gl.CullFace(state.Culling == FaceCulling.Front ? TriangleFace.Front : TriangleFace.Back);
				}
			}

			if (all || applied.Wireframe != state.Wireframe)
			{
				gl.PolygonMode(TriangleFace.FrontAndBack, state.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
			}

			if (all || applied.Blend != state.Blend)
			{
				Toggle(EnableCap.Blend, state.Blend);
			}

			if (state.Blend)
			{
				if (all || applied.SourceColor != state.SourceColor || applied.DestinationColor != state.DestinationColor || applied.SourceAlpha != state.SourceAlpha
					|| applied.DestinationAlpha != state.DestinationAlpha)
				{
					gl.BlendFuncSeparate((BlendingFactorSrc)Factor(state.SourceColor), (BlendingFactorDest)Factor(state.DestinationColor), (BlendingFactorSrc)Factor(state.SourceAlpha),
						(BlendingFactorDest)Factor(state.DestinationAlpha));
				}

				if (all || applied.ColorOperation != state.ColorOperation || applied.AlphaOperation != state.AlphaOperation)
				{
					gl.BlendEquationSeparate(Equation(state.ColorOperation), Equation(state.AlphaOperation));
				}
			}

			if (all || applied.ColorMask != state.ColorMask)
			{
				ColorWriteMask mask = state.ColorMask;
				gl.ColorMask((mask & ColorWriteMask.Red) != 0, (mask & ColorWriteMask.Green) != 0, (mask & ColorWriteMask.Blue) != 0, (mask & ColorWriteMask.Alpha) != 0);
			}

			if (all || applied.DepthBiasSlope != state.DepthBiasSlope || applied.DepthBiasConstant != state.DepthBiasConstant)
			{
				bool bias = state.DepthBiasSlope != 0f || state.DepthBiasConstant != 0f;
				Toggle(EnableCap.PolygonOffsetFill, bias);
				if (bias)
				{
					gl.PolygonOffset(state.DepthBiasSlope, state.DepthBiasConstant);
				}
			}

			// Blend factors of a state without blending are not applied: remember the ones the context has.
			RenderState now = state;
			if (!state.Blend && appliedValid)
			{
				now.SourceColor = applied.SourceColor;
				now.DestinationColor = applied.DestinationColor;
				now.SourceAlpha = applied.SourceAlpha;
				now.DestinationAlpha = applied.DestinationAlpha;
				now.ColorOperation = applied.ColorOperation;
				now.AlphaOperation = applied.AlphaOperation;
			}

			applied = now;
			appliedValid = true;
		}

		private void Toggle(EnableCap cap, bool on)
		{
			if (on)
			{
				gl.Enable(cap);
			}
			else
			{
				gl.Disable(cap);
			}
		}

		private void ApplyProgram()
		{
			GLProgram current = program;
			if (boundProgram != current.Handle)
			{
				gl.UseProgram(current.Handle);
				boundProgram = current.Handle;
			}

			ApplyProgramTextures(current);
		}

		private void ApplyProgramTextures(GLProgram current)
		{
			int units = current.UnitCount;
			for (int index = 0; index < units; index++)
			{
				// A program's samplers take the texture units from 0 on, in the order of their slots.
				int unit = index;
				GLTexture texture = current.Textures[index];
				int handle;
				TextureTarget target;
				if (texture != null && !texture.IsDisposed)
				{
					handle = texture.Handle;
					target = texture.Target;
				}
				else
				{
					texture = null;
					handle = 0;
					target = current.UnitTargets[index];
				}

				if (unitTextures[unit] != handle || unitTargets[unit] != target)
				{
					if (activeUnit != unit)
					{
						gl.ActiveTexture(TextureUnit.Texture0 + unit);
						activeUnit = unit;
					}

					gl.BindTexture(target, handle);
					unitTextures[unit] = handle;
					unitTargets[unit] = target;
				}

				GLSampler sampler = current.Samplers[index] ?? (GLSampler)texture?.Sampler ?? (defaultSampler ??= (GLSampler)Samplers.LinearClamp);
				if (unitSamplers[unit] != sampler.Handle)
				{
					gl.BindSampler(unit, sampler.Handle);
					unitSamplers[unit] = sampler.Handle;
				}
			}
		}

		private void ApplyVertexInput(bool indexed)
		{
			if (!vertexArrays.TryGetValue(layout.Id, out VertexArray array))
			{
				array = CreateVertexArray(layout);
				vertexArrays.Add(layout.Id, array);
			}

			if (boundVertexArray != array.Handle)
			{
				gl.BindVertexArray(array.Handle);
				boundVertexArray = array.Handle;
			}

			VertexBufferDescriptor[] buffers = layout.Buffers;
			for (int i = 0; i < buffers.Length; i++)
			{
				GLBuffer buffer = vertexBuffers[i];
				if (buffer == null)
				{
					throw new InvalidOperationException($"The vertex layout reads vertex buffer {i}, but none is set.");
				}

				if (array.Buffers[i] != buffer.Handle || array.Offsets[i] != vertexOffsets[i])
				{
					Native.glBindVertexBuffer((uint)i, new GlBuffer((uint)buffer.Handle), (nint)vertexOffsets[i], buffers[i].Stride);
					array.Buffers[i] = buffer.Handle;
					array.Offsets[i] = vertexOffsets[i];
				}
			}

			if (indexed && array.ElementBuffer != indexBuffer.Handle)
			{
				gl.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer.Handle);
				array.ElementBuffer = indexBuffer.Handle;
			}
		}

		private VertexArray CreateVertexArray(VertexInputLayout input)
		{
			VertexArray array = new VertexArray { Handle = gl.GenVertexArray() };
			gl.BindVertexArray(array.Handle);
			boundVertexArray = array.Handle;
			foreach (VertexAttributeDescriptor attribute in input.Attributes)
			{
				uint location = (uint)attribute.Location;
				gl.EnableVertexAttribArray(attribute.Location);
				switch (attribute.Format)
				{
					case VertexFormat.Float1:
					case VertexFormat.Float2:
					case VertexFormat.Float3:
					case VertexFormat.Float4:
						Native.glVertexAttribFormat(location, attribute.Format - VertexFormat.Float1 + 1, (uint)VertexAttribPointerType.Float, false, (uint)attribute.Offset);
						break;
					case VertexFormat.UByte4Normalized:
						Native.glVertexAttribFormat(location, 4, (uint)VertexAttribPointerType.UnsignedByte, true, (uint)attribute.Offset);
						break;
					case VertexFormat.Int1:
					case VertexFormat.Int2:
					case VertexFormat.Int3:
					case VertexFormat.Int4:
						Native.glVertexAttribIFormat(location, attribute.Format - VertexFormat.Int1 + 1, (uint)VertexAttribIntegerType.Int, (uint)attribute.Offset);
						break;
					default:
						Native.glVertexAttribIFormat(location, attribute.Format - VertexFormat.UInt1 + 1, (uint)VertexAttribIntegerType.UnsignedInt, (uint)attribute.Offset);
						break;
				}

				Native.glVertexAttribBinding(location, (uint)attribute.Buffer);
			}

			for (int i = 0; i < input.Buffers.Length; i++)
			{
				Native.glVertexBindingDivisor((uint)i, input.Buffers[i].PerInstance ? 1u : 0u);
			}

			return array;
		}

		private void BindBufferRange(BufferRangeTarget target, BufferBinding[] bindings, int binding, GLBuffer buffer, long offset, long size)
		{
			if (binding < 0 || binding >= MaxBufferBindings)
			{
				throw new ArgumentOutOfRangeException(nameof(binding), $"Buffer binding {binding} is outside 0..{MaxBufferBindings - 1}.");
			}

			int handle = buffer != null && !buffer.IsDisposed ? buffer.Handle : 0;
			// A buffer's storage can be replaced under the same name, with another size: a whole-buffer binding is
			// made again every time (it is what the driver does itself for glBindBufferBase).
			if (handle == 0 || (offset == 0 && size < 0))
			{
				gl.BindBufferBase(target, binding, handle);
				bindings[binding] = new BufferBinding { Handle = handle, Offset = 0, Size = -1 };
				return;
			}

			long range = size < 0 ? buffer.Size - offset : size;
			if (bindings[binding].Handle != handle || bindings[binding].Offset != offset || bindings[binding].Size != range)
			{
				gl.BindBufferRange(target, binding, handle, (IntPtr)offset, (IntPtr)range);
				bindings[binding] = new BufferBinding { Handle = handle, Offset = offset, Size = range };
			}
		}

		private void BindFramebuffer(int framebuffer)
		{
			if (boundFramebuffer != framebuffer)
			{
				gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
				boundFramebuffer = framebuffer;
			}
		}

		private void ApplyScissor()
		{
			if (scissorOn != scissorApplied)
			{
				Toggle(EnableCap.ScissorTest, scissorOn);
				scissorApplied = scissorOn;
			}

			if (scissorOn)
			{
				gl.Scissor(scissorX, scissorY, scissorWidth, scissorHeight);
				scissorRectApplied = true;
			}
		}

		/// <summary>A clear touches the whole attachment or the given rectangle, whatever the scissor of the draws is.</summary>
		private void BeginClearArea(int x, int y, int width, int height)
		{
			if (width < 0 || height < 0)
			{
				if (scissorApplied)
				{
					gl.Disable(EnableCap.ScissorTest);
				}

				return;
			}

			if (!scissorApplied)
			{
				gl.Enable(EnableCap.ScissorTest);
			}

			gl.Scissor(x, y, width, height);
		}

		private void EndClearArea()
		{
			// Back to the draws' scissor.
			scissorApplied = !scissorOn;
			ApplyScissor();
			if (!scissorOn && scissorRectApplied)
			{
				scissorRectApplied = false;
			}
		}

		private int CreateFramebuffer(in RenderPassDescriptor descriptor, in FramebufferKey key)
		{
			int framebuffer = gl.GenFramebuffer();
			BindFramebuffer(framebuffer);
			int colorCount = descriptor.Colors.Length;
			for (int i = 0; i < colorCount; i++)
			{
				ColorAttachment color = descriptor.Colors[i];
				Attach(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, (GLTexture)color.Texture, color.Mip, color.Layer);
			}

			if (descriptor.Depth.Texture != null)
			{
				Attach(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, (GLTexture)descriptor.Depth.Texture, descriptor.Depth.Mip, descriptor.Depth.Layer);
			}

			if (colorCount == 0)
			{
				if (descriptor.Depth.Texture == null)
				{
					gl.FramebufferParameteri(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultWidth, key.Width);
					gl.FramebufferParameteri(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultHeight, key.Height);
					if (key.Count >> 16 > 1)
					{
						// GL_FRAMEBUFFER_DEFAULT_SAMPLES
						gl.FramebufferParameteri(FramebufferTarget.Framebuffer, (FramebufferDefaultParameter)0x9313, key.Count >> 16);
					}
				}

				gl.DrawBuffer(DrawBufferMode.None);
				gl.ReadBuffer(ReadBufferMode.None);
			}
			else
			{
				DrawBuffersEnum* buffers = stackalloc DrawBuffersEnum[colorCount];
				for (int i = 0; i < colorCount; i++)
				{
					buffers[i] = DrawBuffersEnum.ColorAttachment0 + i;
				}

				gl.DrawBuffers(colorCount, buffers);
			}

			FramebufferErrorCode status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				throw new InvalidOperationException("The attachments of a render pass do not make a complete framebuffer: " + status);
			}

			return framebuffer;
		}

		private void Attach(FramebufferTarget target, FramebufferAttachment point, GLTexture texture, int mip, int layer)
		{
			if (texture.Kind == TextureKind.Texture3D || texture.Kind == TextureKind.Texture2DArray)
			{
				Native.glFramebufferTextureLayer((uint)target, (uint)point, (uint)texture.Handle, mip, layer);
			}
			else
			{
				gl.FramebufferTexture2D(target, point, texture.FaceTarget(layer), texture.Handle, mip);
			}
		}

		/// <summary>Framebuffer objects made for textures that are gone are deleted (OpenGL would give their names to new textures).</summary>
		private void ForgetDeletedTextures()
		{
			if (deletionsSeen == device.TextureDeletions)
			{
				return;
			}

			List<int> deleted = device.DeletedTextures;
			List<FramebufferKey> stale = null;
			lock (device.Sync)
			{
				deletionsSeen = device.TextureDeletions;
				// A context that fell behind the list of deleted names cannot tell which objects are stale: all go.
				bool all = deletedEpochSeen != device.DeletedTexturesEpoch;
				deletedEpochSeen = device.DeletedTexturesEpoch;
				foreach (KeyValuePair<FramebufferKey, int> pair in framebuffers)
				{
					if (all || pair.Key.Uses(deleted))
					{
						(stale ??= new List<FramebufferKey>()).Add(pair.Key);
					}
				}
			}

			if (stale == null)
			{
				return;
			}

			foreach (FramebufferKey key in stale)
			{
				int framebuffer = framebuffers[key];
				if (boundFramebuffer == framebuffer)
				{
					boundFramebuffer = -1;
				}

				gl.DeleteFramebuffer(framebuffer);
				framebuffers.Remove(key);
			}
		}

		#endregion

		private struct BufferBinding
		{
			public int Handle;
			public long Offset;
			public long Size;
		}

		private sealed class VertexArray
		{
			public int Handle;
			public readonly int[] Buffers = new int[MaxVertexBuffers];
			public readonly long[] Offsets = new long[MaxVertexBuffers];
			public int ElementBuffer;
		}

		/// <summary>The attachments of a pass: eight colours and the depth, each a texture, a mip level and a layer.</summary>
		private struct FramebufferKey : IEquatable<FramebufferKey>
		{
			public int Count;
			public int Width;
			public int Height;
			private fixed int textures[9];
			private fixed int levels[9];

			public void Set(int index, int texture, int mip, int layer)
			{
				textures[index] = texture;
				levels[index] = mip | layer << 8;
			}

			public bool Uses(List<int> deleted)
			{
				for (int i = 0; i < 9; i++)
				{
					if (textures[i] != 0 && deleted.Contains(textures[i]))
					{
						return true;
					}
				}

				return false;
			}

			public bool Equals(FramebufferKey other)
			{
				if (Count != other.Count || Width != other.Width || Height != other.Height)
				{
					return false;
				}

				for (int i = 0; i < 9; i++)
				{
					if (textures[i] != other.textures[i] || levels[i] != other.levels[i])
					{
						return false;
					}
				}

				return true;
			}

			public override bool Equals(object obj)
			{
				return obj is FramebufferKey other && Equals(other);
			}

			public override int GetHashCode()
			{
				HashCode hash = new HashCode();
				hash.Add(Count);
				hash.Add(Width);
				hash.Add(Height);
				for (int i = 0; i < 9; i++)
				{
					hash.Add(textures[i]);
					hash.Add(levels[i]);
				}

				return hash.ToHashCode();
			}
		}
	}
}
