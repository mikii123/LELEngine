using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The commands on Vulkan: recorded into the device's stream of command buffers. A render pass is dynamic
	///     rendering over views of the attachments; a draw binds a pipeline made for the program, the state and the
	///     pass's formats (the rest of the state is dynamic), and a descriptor set written for everything the program
	///     reads. An upload given inside a pass that has to be ordered with the pass's draws suspends the rendering and
	///     the next draw takes it up again, keeping what was drawn. Barriers and layouts: VulkanCommandListSync.cs.
	/// </summary>
	internal sealed unsafe partial class VulkanCommandList : CommandList
	{
		public const int MaxBufferBindings = 96;
		public const int MaxImageBindings = 64;
		private const int MaxVertexBuffers = 8;
		private const int MaxColors = 8;

		private readonly VulkanDevice device;
		private readonly VkDevice.FunctionTable* vk;

		// The open pass, as the caller sees it, and whether rendering is begun in the command buffer.
		private bool passOpen;
		private bool rendering;
		private int passWidth;
		private int passHeight;
		private int passColorCount;
		private int passSamples = 1;
		private int passGeneration;
		private readonly TextureFormat[] passColorFormats = new TextureFormat[MaxColors];
		private readonly VulkanTexture[] passColorTextures = new VulkanTexture[MaxColors];
		private VulkanTexture passDepthTexture;

		/// <summary>
		///     What a unit of constant depth bias is multiplied by for the open pass's depth attachment: 4 for Depth24
		///     kept in 32 bit floats (a GPU without 24 bit depth). A float buffer's unit at the depths of a perspective
		///     view (0.5 .. 1) is 2^-24, where OpenGL's 24 bit depth takes 2^-22 on such a GPU at every depth (AMD's
		///     driver, measured): the same bias then moves the same depth there; nearer than 0.5 the float's unit, and
		///     so the bias, keeps halving.
		/// </summary>
		private float passBiasScale = 1f;

		private float appliedBiasScale = 1f;

		// The depth bias values the command buffer holds.
		private bool biasValuesSent;
		private float sentBiasConstant;
		private float sentBiasSlope;

		// What the next draw uses.
		private VulkanProgram program;
		private VertexInputLayout layout = VertexInputLayout.Empty;
		private readonly VulkanBuffer[] vertexBuffers = new VulkanBuffer[MaxVertexBuffers];
		private readonly long[] vertexOffsets = new long[MaxVertexBuffers];
		private VulkanBuffer indexBuffer;
		private IndexFormat indexFormat = IndexFormat.UInt32;
		private readonly BufferBinding[] uniformBuffers = new BufferBinding[MaxBufferBindings];
		private readonly BufferBinding[] storageBuffers = new BufferBinding[MaxBufferBindings];
		private readonly ImageBinding[] images = new ImageBinding[MaxImageBindings];
		private int viewportX;
		private int viewportY;
		private int viewportWidth;
		private int viewportHeight;
		private bool scissorOn;
		private int scissorX;
		private int scissorY;
		private int scissorWidth;
		private int scissorHeight;
		private bool stateChanged = true;

		// What the command buffer was last told.
		private VkPipeline boundPipeline;
		private VkPipeline boundComputePipeline;
		private VulkanProgram pipelineProgram;
		private VertexInputLayout pipelineLayout;
		private int pipelineGeneration = -1;
		private int pipelineTopologyClass = -1;
		private VkPipeline pipeline;
		private int pipelineDefaults = -1;
		private bool dynamicValid;
		private RenderState appliedState;
		private bool appliedBias;
		private int appliedTopology = -1;
		private bool viewportApplied;
		private bool scissorApplied;
		private VkRect2D appliedScissor;
		private readonly nint[] boundVertexBuffers = new nint[MaxVertexBuffers + 1];
		private readonly long[] boundVertexOffsets = new long[MaxVertexBuffers + 1];
		private nint boundIndexBuffer;
		private IndexFormat boundIndexFormat;
		private VkDescriptorSet boundSet;
		private VulkanProgram boundSetProgram;
		private VkDescriptorSet boundComputeSet;
		private VulkanProgram boundComputeProgram;
		private readonly int[] boundOffsets = new int[8];
		private readonly int[] boundComputeOffsets = new int[8];

		public VulkanCommandList(VulkanDevice device)
		{
			this.device = device;
			vk = device.Vk;
		}

		#region Render passes

		public override bool InRenderPass => passOpen;
		public override int PassWidth => passWidth;
		public override int PassHeight => passHeight;
		public override int PassColorCount => passColorCount;

		protected override void OnStateAccess()
		{
			stateChanged = true;
		}

		public override void BeginRenderPass(in RenderPassDescriptor descriptor)
		{
			EndRenderPass();
			OpenPass();
			int colorCount = descriptor.Colors.Length;
			if (colorCount > MaxColors)
			{
				throw new ArgumentException($"A render pass has at most {MaxColors} colour attachments.");
			}

			VulkanTexture depth = (VulkanTexture)descriptor.Depth.Texture;
			if (colorCount == 1 && descriptor.Colors[0].Texture != null && descriptor.Colors[0].Texture.IsBackbuffer && depth != null)
			{
				throw new ArgumentException("A window's back buffer is drawn into without a depth attachment.");
			}

			for (int i = 0; i < colorCount; i++)
			{
				ColorAttachment color = descriptor.Colors[i];
				if (color.Texture == null || color.Texture.IsDisposed || (color.Texture.IsBackbuffer && colorCount > 1))
				{
					throw new ArgumentException($"Colour attachment {i} of a render pass is missing, disposed or a window's back buffer among other attachments.");
				}

				VulkanTexture texture = (VulkanTexture)color.Texture;
				RequireAttachment(texture);
				passColorTextures[i] = texture;
				passColorFormats[i] = texture.Format;
				NoteColorAttachment(i, color);
			}

			for (int i = colorCount; i < MaxColors; i++)
			{
				passColorTextures[i] = null;
			}

			passDepthTexture = depth;
			passBiasScale = 1f;
			if (depth != null)
			{
				if (depth.IsDisposed)
				{
					throw new ArgumentException("The depth attachment of a render pass is disposed.");
				}

				RequireAttachment(depth);
				NoteDepthAttachment(descriptor.Depth);
				passBiasScale = depth.Format == TextureFormat.Depth24 && !depth.PackedDepth ? 4f : 1f;
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
			}

			passSamples = 1;
			if (first == null && descriptor.Samples > 1)
			{
				if (descriptor.Samples > device.Capabilities.MaxPassSamples || (descriptor.Samples & (descriptor.Samples - 1)) != 0)
				{
					throw new ArgumentException(
						$"A pass without attachments cannot have {descriptor.Samples} samples on this device (DeviceCapabilities.MaxPassSamples: {device.Capabilities.MaxPassSamples}).");
				}

				passSamples = descriptor.Samples;
			}

			passColorCount = colorCount;
			passGeneration++;
			passOpen = true;
			viewportX = 0;
			viewportY = 0;
			viewportWidth = passWidth;
			viewportHeight = passHeight;
			viewportApplied = false;
			scissorOn = false;
			scissorApplied = false;
		}

		public override void EndRenderPass()
		{
			if (passOpen && !passStarted && PassClears())
			{
				// A pass that only clears: its rendering begins and ends here.
				StartRendering();
			}

			SuspendRendering();
			passOpen = false;
		}

		public override void ClearColor(int attachment, in ClearValue value, int x = 0, int y = 0, int width = -1, int height = -1)
		{
			RequirePass("ClearColor");
			if (attachment < 0 || attachment >= passColorCount)
			{
				return;
			}

			if (!passStarted && CoversPass(x, y, width, height))
			{
				// Nothing drawn yet: the clear becomes the attachment's load operation (a fast clear where the GPU has one).
				passColorLoads[attachment] = LoadOperation.Clear;
				passColorClears[attachment] = value;
				return;
			}

			VkClearAttachment clear = new VkClearAttachment { AspectMask = VkImageAspectFlags.ColorBit, ColorAttachment = attachment };
			WriteClearValue(&clear.ClearValue, value, passColorFormats[attachment]);
			ClearArea(clear, x, y, width, height);
		}

		public override void ClearDepth(float depth = 1f, int x = 0, int y = 0, int width = -1, int height = -1)
		{
			RequirePass("ClearDepth");
			if (passDepthTexture == null)
			{
				return;
			}

			if (!passStarted && CoversPass(x, y, width, height))
			{
				passDepthLoad = LoadOperation.Clear;
				passDepthClear = depth;
				return;
			}

			if (depthReadOnly)
			{
				// A clear writes the depth: it renders writable again (the next draw that samples it goes back).
				SuspendRendering();
				depthReadOnly = false;
				stateChanged = true;
			}

			VkClearAttachment clear = new VkClearAttachment { AspectMask = VkImageAspectFlags.DepthBit };
			*(float*)&clear.ClearValue = depth;
			ClearArea(clear, x, y, width, height);
		}

		private void ClearArea(VkClearAttachment clear, int x, int y, int width, int height)
		{
			if (width < 0 || height < 0)
			{
				x = 0;
				y = 0;
				width = passWidth;
				height = passHeight;
			}

			int x1 = Math.Min(passWidth, x + width);
			int y1 = Math.Min(passHeight, y + height);
			x = Math.Max(0, x);
			y = Math.Max(0, y);
			if (x1 <= x || y1 <= y)
			{
				return;
			}

			EnsureRendering();
			VkClearRect rect = new VkClearRect
			{
				Rect = new VkRect2D { Offset = new VkOffset2D { X = x, Y = y }, Extent = new VkExtent2D { Width = x1 - x, Height = y1 - y } },
				BaseArrayLayer = 0,
				LayerCount = 1
			};
			vk->vkCmdClearAttachments.Invoke(device.MainCommands, 1, &clear, 1, &rect);
		}

		private static void WriteClearValue(VkClearValue* target, in ClearValue value, TextureFormat format)
		{
			if (format.IsInteger())
			{
				uint* integers = (uint*)target;
				integers[0] = value.IntegerR;
				integers[1] = value.IntegerG;
				integers[2] = value.IntegerB;
				integers[3] = value.IntegerA;
			}
			else
			{
				float* floats = (float*)target;
				floats[0] = value.R;
				floats[1] = value.G;
				floats[2] = value.B;
				floats[3] = value.A;
			}
		}

		private void RequireAttachment(VulkanTexture texture)
		{
			if ((texture.Usage & TextureUsage.Attachment) == 0)
			{
				throw new InvalidOperationException($"Texture '{texture.Name}' was made without TextureUsage.Attachment: a render pass cannot draw into it.");
			}
		}

		private static VkAttachmentLoadOp Load(LoadOperation operation)
		{
			return operation == LoadOperation.Clear ? VkAttachmentLoadOp.Clear : operation == LoadOperation.DontCare ? VkAttachmentLoadOp.DontCare : VkAttachmentLoadOp.Load;
		}

		/// <summary>Rendering goes on (begun for the first time, or after an upload, a barrier or a submission took it out of the command buffer).</summary>
		private void EnsureRendering()
		{
			if (!rendering)
			{
				StartRendering();
			}
		}

		/// <summary>Ends rendering in the command buffer; the pass stays open for the caller.</summary>
		private void SuspendRendering()
		{
			if (rendering)
			{
				vk->vkCmdEndRendering.Invoke(device.MainCommands);
				rendering = false;
			}
		}

		#endregion

		#region For the device

		/// <summary>
		///     The command buffer is about to be sent: rendering ends in it, its timestamps are copied where the CPU reads
		///     them, and what the CPU will read is made visible to it.
		/// </summary>
		internal void BeforeSubmit()
		{
			SuspendRendering();
			CopyTimestamps();
			FinishForHost();
		}

		/// <summary>A new command buffer follows: it knows nothing of what the last one was told.</summary>
		internal void AfterSubmit()
		{
			// Every program notes its resources' uses again in the new submission (which marks them as used by it).
			device.SyncEpoch++;
			boundPipeline = default;
			boundComputePipeline = default;
			dynamicValid = false;
			appliedTopology = -1;
			viewportApplied = false;
			scissorApplied = false;
			Array.Clear(boundVertexBuffers);
			boundIndexBuffer = 0;
			boundSet = default;
			boundSetProgram = null;
			boundComputeSet = default;
			boundComputeProgram = null;
		}

		/// <summary>
		///     The stream's command buffer for a command recorded outside the device's tracking of uses: rendering is
		///     suspended (an open pass goes on with the next draw), everything recorded before is made visible to the
		///     command, and the command to everything recorded after it.
		/// </summary>
		internal VkCommandBuffer BeginTransfer()
		{
			SuspendRendering();
			VkCommandBuffer commands = device.MainCommands;
			device.GlobalBarrier(commands);
			untracked = true;
			return commands;
		}

		/// <summary>As <see cref="BeginTransfer" />, for an upload given inside a pass: the caller's pass is not ended.</summary>
		internal VkCommandBuffer BeginTransferInPass()
		{
			return BeginTransfer();
		}

		/// <summary>Fills a stand-in texture with zeros, ahead of the stream.</summary>
		internal void ClearDummy(VulkanTexture texture)
		{
			VkImageLayout layout = texture.CopyDestinationLayout;
			VkCommandBuffer commands = TransferCommands(texture, true, 0, 1, 0, texture.Layers, layout, true, true);
			VkImageSubresourceRange range = new VkImageSubresourceRange { AspectMask = texture.Aspect, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = texture.Layers };
			if (texture.Format.IsDepth())
			{
				VkClearDepthStencilValue depth = new VkClearDepthStencilValue { Depth = 1f };
				vk->vkCmdClearDepthStencilImage.Invoke(commands, texture.Image, layout, &depth, 1, &range);
			}
			else
			{
				// What OpenGL reads from a sampler without a texture: black, opaque.
				VkClearColorValue color = default;
				if (!texture.Format.IsInteger())
				{
					((float*)&color)[3] = 1f;
				}

				vk->vkCmdClearColorImage.Invoke(commands, texture.Image, layout, &color, 1, &range);
			}
		}

		/// <summary>The buffer, offset and range behind a uniform or storage block of a program: what is set for its number, or zeros.</summary>
		internal void ResolveBuffer(bool storage, int number, int blockSize, string programName, string blockName, out VkBuffer buffer, out long offset, out long range)
		{
			BufferBinding binding = number < MaxBufferBindings ? (storage ? storageBuffers : uniformBuffers)[number] : default;
			VulkanBuffer bound = binding.Buffer;
			range = 0;
			offset = 0;
			if (bound != null && !bound.IsDisposed && !bound.Handle.IsNull())
			{
				if ((bound.Usage & (storage ? BufferUsage.Storage : BufferUsage.Uniform)) == 0)
				{
					throw new InvalidOperationException(
						$"Buffer '{bound.Name}' is bound as {(storage ? "storage" : "uniform")} block '{blockName}' of program '{programName}' but was made without BufferUsage.{(storage ? "Storage" : "Uniform")}.");
				}

				offset = binding.Offset;
				range = binding.Size < 0 ? bound.Size - offset : Math.Min(binding.Size, bound.Size - offset);
			}

			if (range <= 0)
			{
				bound = device.DummyBuffer(blockSize);
				offset = 0;
				range = bound.Size;
			}

			if (!storage)
			{
				range = Math.Min(range, (long)(uint)device.Limits.MaxUniformBufferRange);
			}

			buffer = bound.Handle;
			Track(new VulkanProgram.TrackedResource { Buffer = bound, Write = storage && Resolving != null && Resolving.WritesBuffer(number) }, buffer.VkHandle);
		}

		/// <summary>The view behind an image of a program: of the texture set for its number, or of a stand-in of the kind the shader expects.</summary>
		internal VkImageView ResolveImage(int number, TextureKind expected, SpirvBaseType type, int imageFormat, string programName, string imageName)
		{
			ImageBinding binding = number < MaxImageBindings ? images[number] : default;
			VulkanTexture texture = binding.Texture;
			if (texture == null || texture.IsDisposed)
			{
				texture = device.DummyTexture(expected, type, false, imageFormat);
				VkImageView standIn = texture.SampledView;
				Track(new VulkanProgram.TrackedResource { Texture = texture, Mips = 1, Layers = texture.Layers, Layout = VkImageLayout.General, Write = true }, standIn.VkHandle);
				return standIn;
			}

			if ((texture.Usage & TextureUsage.Storage) == 0)
			{
				throw new InvalidOperationException($"Texture '{texture.Name}' is bound as image '{imageName}' of program '{programName}' but was made without TextureUsage.Storage.");
			}

			int mip = Math.Clamp(binding.Mip, 0, texture.MipLevels - 1);
			int layer = binding.Layer;
			VkImageView view = default;
			switch (expected)
			{
				case TextureKind.Texture2D:
					if (texture.Kind == TextureKind.Texture3D)
					{
						if ((texture.Usage & TextureUsage.SliceImages) == 0)
						{
							throw new InvalidOperationException($"Texture '{texture.Name}' is bound as an image by one slice but was made without TextureUsage.SliceImages.");
						}

						if (device.Has2DViewsOf3D)
						{
							view = texture.View(VkImageViewType._2D, mip, 1, Math.Max(0, layer), 1);
						}
					}
					else
					{
						view = texture.View(VkImageViewType._2D, mip, 1, Math.Max(0, layer), 1);
					}

					break;
				case TextureKind.Texture2DArray:
					if (texture.Kind == TextureKind.Texture2DArray || texture.Kind == TextureKind.Cube)
					{
						view = layer < 0 ? texture.View(VkImageViewType._2DArray, mip, 1, 0, texture.Layers) : texture.View(VkImageViewType._2DArray, mip, 1, layer, 1);
					}

					break;
				case TextureKind.Texture3D:
					if (texture.Kind == TextureKind.Texture3D)
					{
						view = texture.View(VkImageViewType._3D, mip, 1, 0, 1);
					}

					break;
				default:
					if (texture.Kind == TextureKind.Cube)
					{
						view = texture.View(VkImageViewType.Cube, mip, 1, 0, 6);
					}

					break;
			}

			if (view.IsNull())
			{
				throw new InvalidOperationException(
					$"Texture '{texture.Name}' ({texture.Kind}, layer {layer}) cannot be bound as image '{imageName}' of program '{programName}', which the shader declares as {expected}.");
			}

			bool allLayers = layer < 0 || texture.Kind == TextureKind.Texture3D;
			Track(new VulkanProgram.TrackedResource
			{
				Texture = texture,
				Mip = mip,
				Mips = 1,
				Layer = allLayers ? 0 : layer,
				Layers = allLayers ? texture.Layers : 1,
				Layout = VkImageLayout.General,
				Write = Resolving == null || Resolving.WritesImage(number)
			}, view.VkHandle);
			return view;
		}

		#endregion

		#region State

		public override void SetViewport(int x, int y, int width, int height)
		{
			if (x != viewportX || y != viewportY || width != viewportWidth || height != viewportHeight)
			{
				viewportX = x;
				viewportY = y;
				viewportWidth = width;
				viewportHeight = height;
				viewportApplied = false;
				scissorApplied = false;
			}
		}

		public override void SetScissor(int x, int y, int width, int height)
		{
			scissorOn = true;
			scissorX = x;
			scissorY = y;
			scissorWidth = Math.Max(0, width);
			scissorHeight = Math.Max(0, height);
			scissorApplied = false;
		}

		public override void DisableScissor()
		{
			scissorOn = false;
			scissorApplied = false;
		}

		#endregion

		#region Program and inputs

		public override GpuProgram Program => program;

		public override void SetProgram(GpuProgram value)
		{
			program = (VulkanProgram)value;
		}

		public override void SetVertexInput(VertexInputLayout value)
		{
			layout = value ?? VertexInputLayout.Empty;
		}

		public override void SetVertexBuffer(int index, GpuBuffer buffer, long offset = 0)
		{
			vertexBuffers[index] = (VulkanBuffer)buffer;
			vertexOffsets[index] = offset;
		}

		public override void SetIndexBuffer(GpuBuffer buffer, IndexFormat format = IndexFormat.UInt32)
		{
			indexBuffer = (VulkanBuffer)buffer;
			indexFormat = format;
		}

		public override void SetUniformBuffer(int binding, GpuBuffer buffer, long offset = 0, long size = -1)
		{
			SetBuffer(uniformBuffers, binding, buffer, offset, size);
		}

		public override void SetStorageBuffer(int binding, GpuBuffer buffer, long offset = 0, long size = -1)
		{
			SetBuffer(storageBuffers, binding, buffer, offset, size);
		}

		private void SetBuffer(BufferBinding[] bindings, int binding, GpuBuffer buffer, long offset, long size)
		{
			if (binding < 0 || binding >= MaxBufferBindings)
			{
				throw new ArgumentOutOfRangeException(nameof(binding), $"Buffer binding {binding} is outside 0..{MaxBufferBindings - 1}.");
			}

			ref BufferBinding slot = ref bindings[binding];
			if (slot.Buffer != buffer || slot.Offset != offset || slot.Size != size)
			{
				slot.Buffer = (VulkanBuffer)buffer;
				slot.Offset = offset;
				slot.Size = size;
				device.BindingEpoch++;
			}
		}

		public override void SetImage(int binding, GpuTexture texture, int mip = 0, int layer = -1, ImageAccess access = ImageAccess.ReadWrite)
		{
			if (binding < 0 || binding >= MaxImageBindings)
			{
				throw new ArgumentOutOfRangeException(nameof(binding), $"Image binding {binding} is outside 0..{MaxImageBindings - 1}.");
			}

			ref ImageBinding slot = ref images[binding];
			if (slot.Texture != texture || slot.Mip != mip || slot.Layer != layer)
			{
				slot.Texture = (VulkanTexture)texture;
				slot.Mip = mip;
				slot.Layer = layer;
				device.BindingEpoch++;
			}
		}

		#endregion

		#region Draws

		public override void Draw(int vertexCount, int firstVertex = 0, int instanceCount = 1, int firstInstance = 0, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (vertexCount <= 0 || instanceCount <= 0 || !PrepareDraw(false, topology))
			{
				return;
			}

			vk->vkCmdDraw.Invoke(device.MainCommands, vertexCount, instanceCount, firstVertex, firstInstance);
		}

		public override void DrawIndexed(int indexCount, int firstIndex = 0, int baseVertex = 0, int instanceCount = 1, int firstInstance = 0,
			PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (indexCount <= 0 || instanceCount <= 0 || !PrepareDraw(true, topology))
			{
				return;
			}

			vk->vkCmdDrawIndexed.Invoke(device.MainCommands, indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
		}

		public override void DrawIndexedRanges(ReadOnlySpan<int> indexCounts, ReadOnlySpan<int> firstIndices, ReadOnlySpan<int> baseVertices, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			int count = indexCounts.Length;
			if (count == 0)
			{
				return;
			}

			if (!device.HasMultiDrawIndirect || count == 1)
			{
				base.DrawIndexedRanges(indexCounts, firstIndices, baseVertices, topology);
				return;
			}

			if (!PrepareDraw(true, topology))
			{
				return;
			}

			// One command for all: the arguments go into the frame's memory, and gl_DrawID counts the ranges.
			uint* arguments = (uint*)device.AllocateTransient(count * 20L, 4, out VkBuffer buffer, out long offset);
			for (int i = 0; i < count; i++)
			{
				arguments[i * 5] = (uint)indexCounts[i];
				arguments[i * 5 + 1] = 1;
				arguments[i * 5 + 2] = (uint)firstIndices[i];
				arguments[i * 5 + 3] = (uint)baseVertices[i];
				arguments[i * 5 + 4] = 0;
			}

			vk->vkCmdDrawIndexedIndirect.Invoke(device.MainCommands, buffer, (Brutal.ByteSize64)(ulong)offset, count, 20);
		}

		public override void DrawIndirect(GpuBuffer arguments, long offset, int drawCount = 1, int stride = 16, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (drawCount <= 0)
			{
				return;
			}

			VulkanBuffer buffer = RequireIndirect(arguments);
			if (!PrepareDraw(false, topology, buffer))
			{
				return;
			}

			if (device.HasMultiDrawIndirect || drawCount == 1)
			{
				vk->vkCmdDrawIndirect.Invoke(device.MainCommands, buffer.Handle, (Brutal.ByteSize64)(ulong)offset, drawCount, stride);
			}
			else
			{
				for (int i = 0; i < drawCount; i++)
				{
					vk->vkCmdDrawIndirect.Invoke(device.MainCommands, buffer.Handle, (Brutal.ByteSize64)(ulong)(offset + (long)i * stride), 1, stride);
				}
			}
		}

		public override void DrawIndexedIndirect(GpuBuffer arguments, long offset, int drawCount = 1, int stride = 20, PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (drawCount <= 0)
			{
				return;
			}

			VulkanBuffer buffer = RequireIndirect(arguments);
			if (!PrepareDraw(true, topology, buffer))
			{
				return;
			}

			if (device.HasMultiDrawIndirect || drawCount == 1)
			{
				vk->vkCmdDrawIndexedIndirect.Invoke(device.MainCommands, buffer.Handle, (Brutal.ByteSize64)(ulong)offset, drawCount, stride);
			}
			else
			{
				for (int i = 0; i < drawCount; i++)
				{
					vk->vkCmdDrawIndexedIndirect.Invoke(device.MainCommands, buffer.Handle, (Brutal.ByteSize64)(ulong)(offset + (long)i * stride), 1, stride);
				}
			}
		}

		public override void DrawIndexedIndirectCount(GpuBuffer arguments, long offset, GpuBuffer count, long countOffset, int maxDrawCount, int stride = 20,
			PrimitiveTopology topology = PrimitiveTopology.Triangles)
		{
			if (!device.HasIndirectCount)
			{
				throw new NotSupportedException("This device reads no draw counts from buffers (DeviceCapabilities.IndirectCount).");
			}

			if (maxDrawCount <= 0)
			{
				return;
			}

			VulkanBuffer buffer = RequireIndirect(arguments);
			VulkanBuffer counts = RequireIndirect(count);
			if (!PrepareDraw(true, topology, buffer, counts))
			{
				return;
			}

			vk->vkCmdDrawIndexedIndirectCount.Invoke(device.MainCommands, buffer.Handle, (Brutal.ByteSize64)(ulong)offset, counts.Handle, (Brutal.ByteSize64)(ulong)countOffset, maxDrawCount,
				stride);
		}

		/// <summary>The buffer of an indirect command's arguments (or count), checked.</summary>
		private VulkanBuffer RequireIndirect(GpuBuffer arguments)
		{
			VulkanBuffer buffer = (VulkanBuffer)arguments;
			if (buffer == null || buffer.IsDisposed || buffer.Handle.IsNull())
			{
				throw new InvalidOperationException("An indirect draw or dispatch without a buffer of arguments.");
			}

			if ((buffer.Usage & BufferUsage.Indirect) == 0)
			{
				throw new InvalidOperationException($"Buffer '{buffer.Name}' holds draw or dispatch arguments but was made without BufferUsage.Indirect.");
			}

			return buffer;
		}

		private void RequirePass(string what)
		{
			if (!passOpen)
			{
				throw new InvalidOperationException(what + " outside a render pass: begin one first (a dispatch, copy, blit or mip generation ends the open pass).");
			}
		}

		/// <param name="arguments">The buffer an indirect draw reads its arguments from (null: none).</param>
		/// <param name="counts">The buffer an indirect draw reads its count from (null: none).</param>
		private bool PrepareDraw(bool indexed, PrimitiveTopology topology, VulkanBuffer arguments = null, VulkanBuffer counts = null)
		{
			RequirePass("A draw");
			VulkanProgram current = program;
			if (current == null || !current.IsUsable || current.IsCompute)
			{
				return false;
			}

			if (indexed && (indexBuffer == null || indexBuffer.Handle.IsNull()))
			{
				throw new InvalidOperationException("An indexed draw without an index buffer.");
			}

			// Everything that may record commands of its own (stand-ins made on first use) comes before rendering:
			// the descriptor set first, then what the draw uses, with the barriers it needs.
			int* offsets = stackalloc int[Math.Max(1, current.BlockCount)];
			VkDescriptorSet set = current.PrepareDescriptors(this, offsets);
			set = NoteDraw(current, set, offsets, indexed, arguments, counts);
			VkCommandBuffer commands = device.MainCommands;
			int topologyClass = topology == PrimitiveTopology.Points ? 2 : topology == PrimitiveTopology.Lines || topology == PrimitiveTopology.LineStrip ? 1 : 0;
			if (stateChanged || pipelineProgram != current || pipelineLayout != layout || pipelineGeneration != passGeneration || pipelineTopologyClass != topologyClass)
			{
				PipelineKey key = default;
				key.LayoutId = layout.Id;
				key.ColorCount = (byte)passColorCount;
				key.DepthFormat = (byte)(passDepthTexture != null ? passDepthTexture.Format : TextureFormat.None);
				key.TopologyClass = (byte)topologyClass;
				key.Wireframe = state.Wireframe;
				key.Samples = (byte)passSamples;
				for (int i = 0; i < passColorCount; i++)
				{
					key.Colors |= (ulong)(byte)passColorFormats[i] << (i * 8);
				}

				key.State = (ulong)state.ColorMask;
				if (state.Blend)
				{
					key.State |= 16 | (ulong)state.SourceColor << 8 | (ulong)state.DestinationColor << 12 | (ulong)state.SourceAlpha << 16 | (ulong)state.DestinationAlpha << 20
						| (ulong)state.ColorOperation << 24 | (ulong)state.AlphaOperation << 28;
				}

				pipeline = current.GetPipeline(key, layout, state, passColorFormats, passColorCount, passDepthTexture != null ? passDepthTexture.Format : TextureFormat.None, out pipelineDefaults);
				pipelineProgram = current;
				pipelineLayout = layout;
				pipelineGeneration = passGeneration;
				pipelineTopologyClass = topologyClass;
			}

			if (boundPipeline.VkHandle != pipeline.VkHandle)
			{
				vk->vkCmdBindPipeline.Invoke(commands, VkPipelineBindPoint.Graphics, pipeline);
				boundPipeline = pipeline;
			}

			if (stateChanged || !dynamicValid || appliedBiasScale != passBiasScale)
			{
				ApplyDynamicState(commands);
				stateChanged = false;
			}

			int vulkanTopology = Topology(topology);
			if (appliedTopology != vulkanTopology)
			{
				vk->vkCmdSetPrimitiveTopology.Invoke(commands, (VkPrimitiveTopology)vulkanTopology);
				appliedTopology = vulkanTopology;
			}

			if (!viewportApplied)
			{
				VkViewport viewport = new VkViewport { X = viewportX, Y = viewportY, Width = Math.Max(1, viewportWidth), Height = Math.Max(1, viewportHeight), MinDepth = 0f, MaxDepth = 1f };
				vk->vkCmdSetViewportWithCount.Invoke(commands, 1, &viewport);
				viewportApplied = true;
			}

			if (!scissorApplied)
			{
				ApplyScissor(commands);
			}

			VertexBufferDescriptor[] buffers = layout.Buffers;
			for (int i = 0; i < buffers.Length; i++)
			{
				VulkanBuffer buffer = vertexBuffers[i];
				if (buffer == null || buffer.Handle.IsNull())
				{
					throw new InvalidOperationException($"The vertex layout reads vertex buffer {i}, but none is set.");
				}

				buffer.Use();
				BindVertexBuffer(commands, i, buffer.Handle, vertexOffsets[i]);
			}

			if (pipelineDefaults >= 0)
			{
				VulkanBuffer defaults = device.AttributeDefaults;
				defaults.Use();
				BindVertexBuffer(commands, pipelineDefaults, defaults.Handle, 0);
			}

			if (indexed)
			{
				indexBuffer.Use();
				nint handle = indexBuffer.Handle.VkHandle;
				if (boundIndexBuffer != handle || boundIndexFormat != indexFormat)
				{
					vk->vkCmdBindIndexBuffer.Invoke(commands, indexBuffer.Handle, default, indexFormat == IndexFormat.UInt16 ? VkIndexType.UInt16 : VkIndexType.UInt32);
					boundIndexBuffer = handle;
					boundIndexFormat = indexFormat;
				}
			}

			int blocks = current.BlockCount;
			bool bind = boundSet.VkHandle != set.VkHandle || boundSetProgram != current;
			for (int i = 0; i < blocks && !bind; i++)
			{
				bind = boundOffsets[i] != offsets[i];
			}

			if (bind)
			{
				vk->vkCmdBindDescriptorSets.Invoke(commands, VkPipelineBindPoint.Graphics, current.PipelineLayout, 0, 1, &set, blocks, offsets);
				boundSet = set;
				boundSetProgram = current;
				for (int i = 0; i < blocks; i++)
				{
					boundOffsets[i] = offsets[i];
				}
			}

			return true;
		}

		private void BindVertexBuffer(VkCommandBuffer commands, int index, VkBuffer buffer, long offset)
		{
			if (boundVertexBuffers[index] != buffer.VkHandle || boundVertexOffsets[index] != offset)
			{
				Brutal.ByteSize64 start = (Brutal.ByteSize64)(ulong)offset;
				vk->vkCmdBindVertexBuffers.Invoke(commands, index, 1, &buffer, &start);
				boundVertexBuffers[index] = buffer.VkHandle;
				boundVertexOffsets[index] = offset;
			}
		}

		private void ApplyDynamicState(VkCommandBuffer commands)
		{
			bool all = !dynamicValid;
			if (all || appliedState.Culling != state.Culling)
			{
				vk->vkCmdSetCullMode.Invoke(commands, state.Culling == FaceCulling.Back ? VkCullModeFlags.BackBit : state.Culling == FaceCulling.Front ? VkCullModeFlags.FrontBit : VkCullModeFlags.None);
			}

			if (all || appliedState.DepthTest != state.DepthTest)
			{
				vk->vkCmdSetDepthTestEnable.Invoke(commands, state.DepthTest);
			}

			// A depth attachment that renders read-only is never written.
			bool depthWrite = state.DepthWrite && !depthReadOnly;
			if (all || appliedDepthWrite != depthWrite)
			{
				vk->vkCmdSetDepthWriteEnable.Invoke(commands, depthWrite);
				appliedDepthWrite = depthWrite;
			}

			if (all || appliedState.DepthCompare != state.DepthCompare)
			{
				vk->vkCmdSetDepthCompareOp.Invoke(commands, (VkCompareOp)(int)state.DepthCompare);
			}

			// OpenGL's polygon offset of filled triangles: none for triangles drawn as lines.
			bool bias = (state.DepthBiasSlope != 0f || state.DepthBiasConstant != 0f) && !state.Wireframe;
			if (all || appliedBias != bias)
			{
				vk->vkCmdSetDepthBiasEnable.Invoke(commands, bias);
			}

			if (all)
			{
				biasValuesSent = false;
			}

			// The values matter only while the bias is on: they are sent then, when they differ from those sent last.
			float constant = state.DepthBiasConstant * passBiasScale;
			if (bias && (!biasValuesSent || sentBiasConstant != constant || sentBiasSlope != state.DepthBiasSlope))
			{
				vk->vkCmdSetDepthBias.Invoke(commands, constant, 0f, state.DepthBiasSlope);
				sentBiasConstant = constant;
				sentBiasSlope = state.DepthBiasSlope;
				biasValuesSent = true;
			}

			appliedBiasScale = passBiasScale;
			appliedBias = bias;
			appliedState = state;
			dynamicValid = true;
		}

		private void ApplyScissor(VkCommandBuffer commands)
		{
			int x0 = 0;
			int y0 = 0;
			int x1 = passWidth;
			int y1 = passHeight;
			if (scissorOn)
			{
				x0 = Math.Max(0, scissorX);
				y0 = Math.Max(0, scissorY);
				x1 = Math.Min(passWidth, scissorX + scissorWidth);
				y1 = Math.Min(passHeight, scissorY + scissorHeight);
			}

			if (viewportWidth <= 0 || viewportHeight <= 0)
			{
				x1 = x0;
			}

			VkRect2D rect = new VkRect2D { Offset = new VkOffset2D { X = x0, Y = y0 }, Extent = new VkExtent2D { Width = Math.Max(0, x1 - x0), Height = Math.Max(0, y1 - y0) } };
			appliedScissor = rect;
			vk->vkCmdSetScissorWithCount.Invoke(commands, 1, &rect);
			scissorApplied = true;
		}

		private static int Topology(PrimitiveTopology topology)
		{
			switch (topology)
			{
				case PrimitiveTopology.Lines: return 1;
				case PrimitiveTopology.Points: return 0;
				case PrimitiveTopology.TriangleStrip: return 4;
				case PrimitiveTopology.LineStrip: return 2;
				default: return 3;
			}
		}

		#endregion

		#region Compute

		public override void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
		{
			if (PrepareDispatch(null))
			{
				vk->vkCmdDispatch.Invoke(device.MainCommands, groupsX, groupsY, groupsZ);
			}
		}

		public override void DispatchIndirect(GpuBuffer arguments, long offset)
		{
			VulkanBuffer buffer = RequireIndirect(arguments);
			if (PrepareDispatch(buffer))
			{
				vk->vkCmdDispatchIndirect.Invoke(device.MainCommands, buffer.Handle, (Brutal.ByteSize64)(ulong)offset);
			}
		}

		/// <param name="arguments">The buffer an indirect dispatch reads its group counts from (null: none).</param>
		private bool PrepareDispatch(VulkanBuffer arguments)
		{
			EndRenderPass();
			VulkanProgram current = program;
			if (current == null || !current.IsUsable || !current.IsCompute)
			{
				return false;
			}

			int* offsets = stackalloc int[Math.Max(1, current.BlockCount)];
			VkDescriptorSet set = current.PrepareDescriptors(this, offsets);
			NoteDispatch(current, arguments);
			VkCommandBuffer commands = device.MainCommands;
			if (boundComputePipeline.VkHandle != current.ComputePipeline.VkHandle)
			{
				vk->vkCmdBindPipeline.Invoke(commands, VkPipelineBindPoint.Compute, current.ComputePipeline);
				boundComputePipeline = current.ComputePipeline;
			}

			int blocks = current.BlockCount;
			bool bind = boundComputeSet.VkHandle != set.VkHandle || boundComputeProgram != current;
			for (int i = 0; i < blocks && !bind; i++)
			{
				bind = boundComputeOffsets[i] != offsets[i];
			}

			if (bind)
			{
				vk->vkCmdBindDescriptorSets.Invoke(commands, VkPipelineBindPoint.Compute, current.PipelineLayout, 0, 1, &set, blocks, offsets);
				boundComputeSet = set;
				boundComputeProgram = current;
				for (int i = 0; i < blocks; i++)
				{
					boundComputeOffsets[i] = offsets[i];
				}
			}

			return true;
		}

		#endregion

		#region Copies

		public override void CopyBuffer(GpuBuffer source, long sourceOffset, GpuBuffer destination, long destinationOffset, long size)
		{
			EndRenderPass();
			if (size <= 0)
			{
				return;
			}

			VulkanBuffer from = (VulkanBuffer)source;
			VulkanBuffer to = (VulkanBuffer)destination;
			if (sourceOffset < 0 || sourceOffset + size > from.Size || destinationOffset < 0 || destinationOffset + size > to.Size)
			{
				throw new ArgumentException($"A copy of {size} bytes from {sourceOffset} of buffer '{from.Name}' ({from.Size}) to {destinationOffset} of '{to.Name}' ({to.Size}) is outside one of them.");
			}

			RequireBuffer(from, Sync.Transfer, false);
			RequireBuffer(to, Sync.Transfer, true);
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			VkBufferCopy region = new VkBufferCopy
			{
				SrcOffset = (Brutal.ByteSize64)(ulong)sourceOffset,
				DstOffset = (Brutal.ByteSize64)(ulong)destinationOffset,
				Size = (Brutal.ByteSize64)(ulong)size
			};
			vk->vkCmdCopyBuffer.Invoke(commands, from.Handle, to.Handle, 1, &region);
		}

		public override void CopyTexture(GpuTexture source, int sourceMip, int sourceX, int sourceY, int sourceZ, GpuTexture destination, int destinationMip, int destinationX,
			int destinationY, int destinationZ, int width, int height, int depth)
		{
			EndRenderPass();
			VulkanTexture from = (VulkanTexture)source;
			VulkanTexture to = (VulkanTexture)destination;
			bool fromVolume = from.Kind == TextureKind.Texture3D;
			bool toVolume = to.Kind == TextureKind.Texture3D;
			int fromLayer = fromVolume ? 0 : sourceZ;
			int fromLayers = fromVolume ? 1 : depth;
			int toLayer = toVolume ? 0 : destinationZ;
			int toLayers = toVolume ? 1 : depth;
			// A copy within one subresource reads and writes it in the general layout.
			bool within = from == to && sourceMip == destinationMip && fromLayer < toLayer + toLayers && toLayer < fromLayer + fromLayers;
			VkImageLayout fromLayout = within ? VkImageLayout.General : from.CopySourceLayout;
			VkImageLayout toLayout = within ? VkImageLayout.General : to.CopyDestinationLayout;
			RequireTexture(from, sourceMip, 1, fromLayer, fromLayers, fromLayout, Sync.Transfer, false);
			RequireTexture(to, destinationMip, 1, toLayer, toLayers, toLayout, Sync.Transfer, true,
				!within && to.Covers(destinationMip, destinationX, destinationY, destinationZ, width, height, depth));
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			VkImageCopy region = new VkImageCopy
			{
				SrcSubresource = new VkImageSubresourceLayers { AspectMask = from.Aspect, MipLevel = sourceMip, BaseArrayLayer = fromVolume ? 0 : sourceZ, LayerCount = fromVolume ? 1 : depth },
				SrcOffset = new VkOffset3D { X = sourceX, Y = sourceY, Z = fromVolume ? sourceZ : 0 },
				DstSubresource = new VkImageSubresourceLayers { AspectMask = to.Aspect, MipLevel = destinationMip, BaseArrayLayer = toVolume ? 0 : destinationZ, LayerCount = toVolume ? 1 : depth },
				DstOffset = new VkOffset3D { X = destinationX, Y = destinationY, Z = toVolume ? destinationZ : 0 },
				// Between slices and layers the box's depth counts both.
				Extent = new VkExtent3D { Width = width, Height = height, Depth = fromVolume || toVolume ? depth : 1 }
			};
			vk->vkCmdCopyImage.Invoke(commands, from.Image, fromLayout, to.Image, toLayout, 1, &region);
		}

		public override void Blit(GpuTexture source, int sourceMip, int sourceLayer, int sourceX0, int sourceY0, int sourceX1, int sourceY1, GpuTexture destination,
			int destinationMip, int destinationLayer, int destinationX0, int destinationY0, int destinationX1, int destinationY1, SamplerFilter filter)
		{
			EndRenderPass();
			VulkanTexture from = (VulkanTexture)source;
			VulkanTexture to = (VulkanTexture)destination;
			bool depth = from.Format.IsDepth();
			if (depth != to.Format.IsDepth())
			{
				throw new ArgumentException("A blit goes from colour to colour or from depth to depth.");
			}

			bool fromVolume = from.Kind == TextureKind.Texture3D;
			bool toVolume = to.Kind == TextureKind.Texture3D;
			bool blits = (from.Features & VkFormatFeatureFlags.BlitSrcBit) != 0 && (to.Features & VkFormatFeatureFlags.BlitDstBit) != 0;
			if (!blits)
			{
				// A device that does not blit the format (depth, mostly) copies, which it can for a box of one size.
				int width = sourceX1 - sourceX0;
				int height = sourceY1 - sourceY0;
				if (width <= 0 || height <= 0 || width != destinationX1 - destinationX0 || height != destinationY1 - destinationY0 || from.VkFormat != to.VkFormat)
				{
					throw new NotSupportedException($"This device does not blit {from.Format} to {to.Format} with scaling, mirroring or conversion.");
				}

				CopyTexture(source, sourceMip, sourceX0, sourceY0, sourceLayer, destination, destinationMip, destinationX0, destinationY0, destinationLayer, width, height, 1);
				return;
			}

			int fromLayer = fromVolume ? 0 : sourceLayer;
			int toLayer = toVolume ? 0 : destinationLayer;
			bool within = from == to && sourceMip == destinationMip && fromLayer == toLayer;
			VkImageLayout fromLayout = within ? VkImageLayout.General : from.CopySourceLayout;
			VkImageLayout toLayout = within ? VkImageLayout.General : to.CopyDestinationLayout;
			bool covers = !within && Math.Min(destinationX0, destinationX1) <= 0 && Math.Min(destinationY0, destinationY1) <= 0 && Math.Max(destinationX0, destinationX1) >= to.MipWidth(destinationMip)
				&& Math.Max(destinationY0, destinationY1) >= to.MipHeight(destinationMip) && (!toVolume || to.MipDepth(destinationMip) == 1);
			RequireTexture(from, sourceMip, 1, fromLayer, 1, fromLayout, Sync.Transfer, false);
			RequireTexture(to, destinationMip, 1, toLayer, 1, toLayout, Sync.Transfer, true, covers);
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			VkImageBlit blit = default;
			blit.SrcSubresource = new VkImageSubresourceLayers { AspectMask = from.Aspect, MipLevel = sourceMip, BaseArrayLayer = fromVolume ? 0 : sourceLayer, LayerCount = 1 };
			blit.DstSubresource = new VkImageSubresourceLayers { AspectMask = to.Aspect, MipLevel = destinationMip, BaseArrayLayer = toVolume ? 0 : destinationLayer, LayerCount = 1 };
			VkOffset3D* sourceOffsets = (VkOffset3D*)&blit.SrcOffsets;
			VkOffset3D* destinationOffsets = (VkOffset3D*)&blit.DstOffsets;
			int sourceZ = fromVolume ? sourceLayer : 0;
			int destinationZ = toVolume ? destinationLayer : 0;
			sourceOffsets[0] = new VkOffset3D { X = sourceX0, Y = sourceY0, Z = sourceZ };
			sourceOffsets[1] = new VkOffset3D { X = sourceX1, Y = sourceY1, Z = sourceZ + 1 };
			destinationOffsets[0] = new VkOffset3D { X = destinationX0, Y = destinationY0, Z = destinationZ };
			destinationOffsets[1] = new VkOffset3D { X = destinationX1, Y = destinationY1, Z = destinationZ + 1 };
			bool linear = filter == SamplerFilter.Linear && !depth && from.Filters && !from.Format.IsInteger();
			vk->vkCmdBlitImage.Invoke(commands, from.Image, fromLayout, to.Image, toLayout, 1, &blit, linear ? VkFilter.Linear : VkFilter.Nearest);
		}

		public override void ClearTexture(GpuTexture texture, int mip, in ClearValue value)
		{
			EndRenderPass();
			VulkanTexture target = (VulkanTexture)texture;
			VkImageLayout layout = target.CopyDestinationLayout;
			RequireTexture(target, mip, 1, 0, target.Layers, layout, Sync.Transfer, true, true);
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			VkImageSubresourceRange range = new VkImageSubresourceRange { AspectMask = target.Aspect, BaseMipLevel = mip, LevelCount = 1, BaseArrayLayer = 0, LayerCount = target.Layers };
			if (target.Format.IsDepth())
			{
				VkClearDepthStencilValue depth = new VkClearDepthStencilValue { Depth = value.R };
				vk->vkCmdClearDepthStencilImage.Invoke(commands, target.Image, layout, &depth, 1, &range);
			}
			else
			{
				VkClearValue color = default;
				WriteClearValue(&color, value, target.Format);
				vk->vkCmdClearColorImage.Invoke(commands, target.Image, layout, (VkClearColorValue*)&color, 1, &range);
			}
		}

		public override void GenerateMipmaps(GpuTexture texture)
		{
			if (texture.MipLevels <= 1)
			{
				return;
			}

			EndRenderPass();
			((VulkanTexture)texture).RecordMipmaps(false);
		}

		public override void CopyTextureToBuffer(GpuTexture source, int mip, int x, int y, int z, int width, int height, int depth, GpuBuffer destination, long destinationOffset)
		{
			EndRenderPass();
			VulkanTexture from = (VulkanTexture)source;
			VulkanBuffer buffer = (VulkanBuffer)destination;
			long size = source.Format.LevelSize(width, height, depth);
			if (destinationOffset < 0 || destinationOffset + size > buffer.Size)
			{
				throw new ArgumentException($"Buffer '{buffer.Name}' ({buffer.Size} bytes) cannot take {size} bytes of texels at {destinationOffset}.");
			}

			if (from.PackedDepth)
			{
				throw new NotSupportedException("Texels of a 24 bit depth texture are not copied into a buffer on this device: read them with GetData.");
			}

			bool volume = from.Kind == TextureKind.Texture3D;
			RequireTexture(from, mip, 1, volume ? 0 : z, volume ? 1 : depth, from.CopySourceLayout, Sync.Transfer, false);
			RequireBuffer(buffer, Sync.Transfer, true);
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			VkBufferImageCopy region = from.Region(mip, x, y, z, width, height, depth, destinationOffset);
			vk->vkCmdCopyImageToBuffer.Invoke(commands, from.Image, from.CopySourceLayout, buffer.Handle, 1, &region);
		}

		#endregion

		#region Queries

		public override void WriteTimestamp(GpuTimestamp timestamp)
		{
			VulkanTimestamp stamp = (VulkanTimestamp)timestamp;
			VkCommandBuffer commands = device.MainCommands;
			VkQueryPool pool = device.QueryPool(stamp.Begin(), out int index);
			vk->vkCmdWriteTimestamp2.Invoke(commands, VkPipelineStageFlags2.AllCommandsBit, pool, index);
		}

		#endregion

		private struct BufferBinding
		{
			public VulkanBuffer Buffer;
			public long Offset;
			public long Size;
		}

		private struct ImageBinding
		{
			public VulkanTexture Texture;
			public int Mip;
			public int Layer;
		}
	}
}
