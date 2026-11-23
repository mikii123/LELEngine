using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace LELEngine.Graphics.Vulkan
{
	internal sealed unsafe class VulkanSampler : GpuSampler
	{
		private readonly VulkanDevice device;
		private VulkanSampler nearest;

		public readonly VkSampler Handle;

		public VulkanSampler(VulkanDevice device, in SamplerDescriptor descriptor)
		{
			this.device = device;
			Descriptor = descriptor;
			float anisotropy = Math.Min(descriptor.MaxAnisotropy, device.Capabilities.MaxAnisotropy);
			VkSamplerCreateInfo info = new VkSamplerCreateInfo
			{
				MagFilter = descriptor.Mag == SamplerFilter.Linear ? VkFilter.Linear : VkFilter.Nearest,
				MinFilter = descriptor.Min == SamplerFilter.Linear ? VkFilter.Linear : VkFilter.Nearest,
				MipmapMode = descriptor.Mip == SamplerMipFilter.Linear ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest,
				AddressModeU = Address(descriptor.AddressU),
				AddressModeV = Address(descriptor.AddressV),
				AddressModeW = Address(descriptor.AddressW),
				AnisotropyEnable = anisotropy > 1f,
				MaxAnisotropy = Math.Max(1f, anisotropy),
				CompareEnable = descriptor.Compare,
				CompareOp = (VkCompareOp)(int)descriptor.CompareFunction,
				MinLod = 0f,
				// Without a mip filter only the first level is read: the level of detail never gets past it.
				MaxLod = descriptor.Mip == SamplerMipFilter.None ? 0.25f : 1000f,
				BorderColor = descriptor.Border == SamplerBorder.OpaqueWhite ? VkBorderColor.FloatOpaqueWhite
					: descriptor.Border == SamplerBorder.OpaqueBlack ? VkBorderColor.FloatOpaqueBlack : VkBorderColor.FloatTransparentBlack
			};
			VkSampler sampler;
			VulkanDevice.Check(device.Vk->vkCreateSampler.Invoke(device.Handle, &info, null, &sampler), "vkCreateSampler");
			Handle = sampler;
		}

		/// <summary>The same sampler reading the nearest texel: for formats the device does not filter (integers).</summary>
		public VulkanSampler Nearest
		{
			get
			{
				if (nearest == null)
				{
					SamplerDescriptor descriptor = Descriptor;
					if (descriptor.Min == SamplerFilter.Nearest && descriptor.Mag == SamplerFilter.Nearest && descriptor.Mip != SamplerMipFilter.Linear && descriptor.MaxAnisotropy <= 1f)
					{
						nearest = this;
					}
					else
					{
						descriptor.Min = SamplerFilter.Nearest;
						descriptor.Mag = SamplerFilter.Nearest;
						descriptor.Mip = descriptor.Mip == SamplerMipFilter.None ? SamplerMipFilter.None : SamplerMipFilter.Nearest;
						descriptor.MaxAnisotropy = 1f;
						nearest = (VulkanSampler)device.GetSampler(descriptor);
					}
				}

				return nearest;
			}
		}

		private static VkSamplerAddressMode Address(SamplerAddress address)
		{
			switch (address)
			{
				case SamplerAddress.Repeat: return VkSamplerAddressMode.Repeat;
				case SamplerAddress.MirroredRepeat: return VkSamplerAddressMode.MirroredRepeat;
				case SamplerAddress.ClampToBorder: return VkSamplerAddressMode.ClampToBorder;
				default: return VkSamplerAddressMode.ClampToEdge;
			}
		}
	}

	/// <summary>
	///     A texture on Vulkan: an image whose levels and layers are each in the layout of their last use (the device
	///     tracks them: VulkanResourcesSync.cs), with image views made as they are first needed (the whole texture for a
	///     sampler, one level of one layer for an attachment, one level for an image binding).
	/// </summary>
	internal sealed unsafe partial class VulkanTexture : GpuTexture
	{
		private readonly VulkanDevice device;
		private readonly VmaAllocation allocation;
		private readonly Dictionary<long, VkImageView> views = new Dictionary<long, VkImageView>();
		private readonly bool backbuffer;
		private readonly bool packedDepth;

		public readonly VkImage Image;
		public readonly VkFormat VkFormat;

		/// <summary>The format of the views samplers read (an sRGB texture's decode; the image's own format otherwise).</summary>
		public readonly VkFormat SampledFormat;

		public readonly VkFormatFeatureFlags Features;
		public readonly VkImageAspectFlags Aspect;

		/// <summary>The submission that used the texture last (also through an upload).</summary>
		public long LastUse;

		/// <summary>The last submission whose stream of commands (not its uploads ahead of the stream) used the texture.</summary>
		public long MainUse;

		/// <param name="rawFormat">
		///     A format of Vulkan's the texture has instead of its texel format's (0: none): the device's stand-ins for
		///     unset images come in formats the engine has no name for.
		/// </param>
		public VulkanTexture(VulkanDevice device, in TextureDescriptor descriptor, bool backbuffer, int rawFormat = 0)
			: base(descriptor)
		{
			this.device = device;
			this.backbuffer = backbuffer;
			if (Format == TextureFormat.None)
			{
				throw new ArgumentException("A texture needs a format.");
			}

			FormatInfo format = device.Format(Format);
			VkFormat = format.Format;
			SampledFormat = format.SampledFormat;
			Features = format.Features;
			packedDepth = format.PackedDepth;
			if (rawFormat != 0)
			{
				VkFormat = (VkFormat)rawFormat;
				SampledFormat = VkFormat;
				Features = device.FormatFeatures(VkFormat);
				packedDepth = false;
			}

			bool depth = Format.IsDepth();
			Aspect = depth ? VkImageAspectFlags.DepthBit : VkImageAspectFlags.ColorBit;
			VkImageUsageFlags usage = VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit;
			if ((Features & VkFormatFeatureFlags.SampledImageBit) != 0)
			{
				usage |= VkImageUsageFlags.SampledBit;
			}
			else if ((Usage & TextureUsage.Sampled) != 0)
			{
				throw new NotSupportedException($"Texture '{Name}': this device does not sample {Format}.");
			}

			if ((Usage & TextureUsage.Storage) != 0)
			{
				if ((Features & VkFormatFeatureFlags.StorageImageBit) == 0)
				{
					throw new NotSupportedException($"Texture '{Name}': this device has no images of {Format} for shaders to read and write.");
				}

				usage |= VkImageUsageFlags.StorageBit;
			}

			if ((Usage & TextureUsage.Attachment) != 0)
			{
				VkFormatFeatureFlags needed = depth ? VkFormatFeatureFlags.DepthStencilAttachmentBit : VkFormatFeatureFlags.ColorAttachmentBit;
				if ((Features & needed) == 0)
				{
					throw new NotSupportedException($"Texture '{Name}': this device does not draw into {Format}.");
				}

				usage |= depth ? VkImageUsageFlags.DepthStencilAttachmentBit : VkImageUsageFlags.ColorAttachmentBit;
			}

			VkImageCreateFlags flags = VkImageCreateFlags.None;
			if (Kind == TextureKind.Cube)
			{
				flags |= VkImageCreateFlags.CubeCompatibleBit;
			}
			else if (Kind == TextureKind.Texture3D)
			{
				// One slice is drawn into, or bound as an image, through a 2D view.
				if ((Usage & TextureUsage.Attachment) != 0)
				{
					flags |= VkImageCreateFlags._2DArrayCompatibleBit;
				}

				// Only where asked for: such an image's slices are laid out apart, which samples it slower.
				if ((Usage & TextureUsage.SliceImages) != 0 && device.Has2DViewsOf3D)
				{
					flags |= VkImageCreateFlags._2DViewCompatibleBitEXT;
				}
			}

			// An sRGB texture's views come in two formats; naming both keeps the image compressed where the GPU can.
			VkFormat* viewFormats = stackalloc VkFormat[2] { VkFormat, SampledFormat };
			VkImageFormatListCreateInfo formatList = new VkImageFormatListCreateInfo { ViewFormatCount = 2, ViewFormats = viewFormats };
			bool twoFormats = SampledFormat != VkFormat;
			if (twoFormats)
			{
				flags |= VkImageCreateFlags.MutableFormatBit;
			}

			VkImageCreateInfo info = new VkImageCreateInfo
			{
				Next = twoFormats ? &formatList : null,
				Flags = flags,
				ImageType = Kind == TextureKind.Texture3D ? VkImageType._3D : VkImageType._2D,
				Format = VkFormat,
				Extent = new VkExtent3D { Width = Width, Height = Height, Depth = Kind == TextureKind.Texture3D ? Depth : 1 },
				MipLevels = MipLevels,
				ArrayLayers = Layers,
				Samples = VkSampleCountFlags._1Bit,
				Tiling = VkImageTiling.Optimal,
				Usage = usage,
				SharingMode = VkSharingMode.Exclusive,
				InitialLayout = VkImageLayout.Undefined
			};
			VmaAllocationCreateInfo allocationInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto };
			VkImage image;
			VmaAllocation handle;
			VulkanDevice.Check(Vma.PInvoke.vmaCreateImage(device.Allocator, &info, &allocationInfo, &image, &handle, default), "vmaCreateImage (" + Name + ", " + Format + ")");
			Image = image;
			allocation = handle;
			// Undefined until its first use takes it into the layout of that use.
		}

		public override bool IsBackbuffer => backbuffer;

		/// <summary>The device filters the format's texels (integer formats are only read one by one).</summary>
		public bool Filters => (Features & VkFormatFeatureFlags.SampledImageFilterLinearBit) != 0;

		public VkImageViewType NaturalViewType
		{
			get
			{
				switch (Kind)
				{
					case TextureKind.Texture3D: return VkImageViewType._3D;
					case TextureKind.Cube: return VkImageViewType.Cube;
					case TextureKind.Texture2DArray: return VkImageViewType._2DArray;
					default: return VkImageViewType._2D;
				}
			}
		}

		/// <summary>A command of the stream uses the texture.</summary>
		public void Use()
		{
			LastUse = MainUse = device.RecordingSerial;
		}

		/// <summary>The whole texture, as samplers read it (decoding sRGB).</summary>
		public VkImageView SampledView => View(NaturalViewType, 0, MipLevels, 0, Layers, true);

		/// <summary>One level of one layer, face or slice, as a render pass draws into it.</summary>
		public VkImageView AttachmentView(int mip, int layer)
		{
			return View(VkImageViewType._2D, mip, 1, layer, 1);
		}

		/// <param name="sampled">The view is read by samplers: in <see cref="SampledFormat" />, else in the image's own format.</param>
		public VkImageView View(VkImageViewType type, int baseMip, int mipCount, int baseLayer, int layerCount, bool sampled = false)
		{
			bool decoded = sampled && SampledFormat != VkFormat;
			long key = (long)(int)type | (long)baseMip << 4 | (long)mipCount << 10 | (long)baseLayer << 16 | (long)layerCount << 40 | (decoded ? 1L << 62 : 0);
			if (views.TryGetValue(key, out VkImageView view))
			{
				return view;
			}

			VkImageViewCreateInfo info = new VkImageViewCreateInfo
			{
				Image = Image,
				ViewType = type,
				Format = decoded ? SampledFormat : VkFormat,
				SubresourceRange = new VkImageSubresourceRange { AspectMask = Aspect, BaseMipLevel = baseMip, LevelCount = mipCount, BaseArrayLayer = baseLayer, LayerCount = layerCount }
			};
			VulkanDevice.Check(device.Vk->vkCreateImageView.Invoke(device.Handle, &info, null, &view), "vkCreateImageView (" + Name + ")");
			views.Add(key, view);
			return view;
		}

		public override void SetData(int mip, int x, int y, int z, int width, int height, int depth, void* data, long size)
		{
			if (backbuffer)
			{
				throw new InvalidOperationException("A window's back buffer takes no texel uploads: blit or draw into it.");
			}

			long expected = Format.LevelSize(width, height, depth);
			if (size < expected)
			{
				throw new ArgumentException($"Texture '{Name}': {size} bytes given for a {width} x {height} x {depth} box of {Format} ({expected} bytes).");
			}

			if (expected == 0)
			{
				return;
			}

			device.BeforeUpload(expected);
			byte* staging = device.AllocateTransient(expected, 16, out VkBuffer buffer, out long offset);
			if (packedDepth)
			{
				float* source = (float*)data;
				uint* packed = (uint*)staging;
				for (long i = 0, count = expected / 4; i < count; i++)
				{
					packed[i] = (uint)(Math.Clamp(source[i], 0f, 1f) * 16777215.0 + 0.5);
				}
			}
			else
			{
				Buffer.MemoryCopy(data, staging, expected, expected);
			}

			VkBufferImageCopy region = Region(mip, x, y, z, width, height, depth, offset);
			bool volume = Kind == TextureKind.Texture3D;
			VkCommandBuffer commands = device.CommandList.UploadCommands(this, mip, 1, volume ? 0 : z, volume ? 1 : depth, Covers(mip, x, y, z, width, height, depth));
			device.Vk->vkCmdCopyBufferToImage.Invoke(commands, buffer, Image, CopyDestinationLayout, 1, &region);
		}

		public override void GetData(int mip, int x, int y, int z, int width, int height, int depth, void* destination, long size)
		{
			long expected = Format.LevelSize(width, height, depth);
			if (size < expected)
			{
				throw new ArgumentException($"Texture '{Name}': a {width} x {height} x {depth} box of {Format} needs {expected} bytes, the destination has {size}.");
			}

			if (expected == 0)
			{
				return;
			}

			VulkanCommandList list = device.CommandList;
			list.EndRenderPass();
			VkBufferCreateInfo info = new VkBufferCreateInfo { Size = (Brutal.ByteSize64)(ulong)expected, Usage = VkBufferUsageFlags.TransferDstBit };
			VmaAllocationCreateInfo allocationInfo = new VmaAllocationCreateInfo
			{
				Usage = VmaMemoryUsage.Auto,
				Flags = VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.HostAccessRandomBit
			};
			VkBuffer buffer;
			VmaAllocation handle;
			VmaAllocationInfo made;
			VulkanDevice.Check(Vma.PInvoke.vmaCreateBuffer(device.Allocator, &info, &allocationInfo, &buffer, &handle, &made), "vmaCreateBuffer");
			VkBufferImageCopy region = Region(mip, x, y, z, width, height, depth, 0);
			bool volume = Kind == TextureKind.Texture3D;
			int layer = volume ? 0 : z;
			int layers = volume ? 1 : depth;
			// What is being recorded does not use the texture: the copy goes alone, and only what was sent before is waited for.
			bool aside = LastUse < device.RecordingSerial;
			VkCommandBuffer commands = aside ? list.AsideCommands(this, mip, 1, layer, layers) : list.TransferCommands(this, false, mip, 1, layer, layers, CopySourceLayout, false, false);
			device.Vk->vkCmdCopyImageToBuffer.Invoke(commands, Image, CopySourceLayout, buffer, 1, &region);
			list.TransfersToHost(commands);
			if (aside)
			{
				device.SubmitAsideAndWait(commands);
			}
			else
			{
				device.Submit();
				device.WaitFor(device.RecordingSerial - 1, long.MaxValue);
			}
			Vma.PInvoke.vmaInvalidateAllocation(device.Allocator, handle, default, (Brutal.ByteSize64)(ulong)expected);
			byte* mapped = (byte*)(void*)made.PMappedData;
			if (packedDepth)
			{
				UnpackDepth((uint*)mapped, (float*)destination, expected / 4);
			}
			else
			{
				Buffer.MemoryCopy(mapped, destination, size, expected);
			}

			Vma.PInvoke.vmaDestroyBuffer(device.Allocator, buffer, handle);
		}

		public override void GenerateMipmaps()
		{
			if (MipLevels <= 1 || backbuffer || IsDisposed)
			{
				return;
			}

			// As an upload: ahead of the stream when the stream has not used the texture in this submission.
			RecordMipmaps(MainUse != device.RecordingSerial);
		}

		/// <summary>Records the blits that fill each level from the one above: ahead of the stream (<paramref name="ahead" />) or in it.</summary>
		internal void RecordMipmaps(bool ahead)
		{
			if ((Features & VkFormatFeatureFlags.BlitSrcBit) == 0 || (Features & VkFormatFeatureFlags.BlitDstBit) == 0)
			{
				return;
			}

			VkFilter filter = Filters && !Format.IsInteger() && !Format.IsDepth() ? VkFilter.Linear : VkFilter.Nearest;
			if (SampledFormat != VkFormat)
			{
				RecordDecodedMipmaps(ahead, filter);
				return;
			}

			for (int level = 1; level < MipLevels; level++)
			{
				VkCommandBuffer commands = device.CommandList.MipCommands(this, ahead, level);
				VkImageBlit blit = LevelBlit(level);
				device.Vk->vkCmdBlitImage.Invoke(commands, Image, CopySourceLayout, Image, CopyDestinationLayout, 1, &blit, filter);
			}
		}

		/// <summary>The blit that fills a level of an image of this texture's size from the level above.</summary>
		private VkImageBlit LevelBlit(int level)
		{
			bool volume = Kind == TextureKind.Texture3D;
			VkImageBlit blit = default;
			blit.SrcSubresource = new VkImageSubresourceLayers { AspectMask = Aspect, MipLevel = level - 1, BaseArrayLayer = 0, LayerCount = Layers };
			blit.DstSubresource = new VkImageSubresourceLayers { AspectMask = Aspect, MipLevel = level, BaseArrayLayer = 0, LayerCount = Layers };
			VkOffset3D* source = (VkOffset3D*)&blit.SrcOffsets;
			VkOffset3D* target = (VkOffset3D*)&blit.DstOffsets;
			source[1] = new VkOffset3D { X = MipWidth(level - 1), Y = MipHeight(level - 1), Z = volume ? MipDepth(level - 1) : 1 };
			target[1] = new VkOffset3D { X = MipWidth(level), Y = MipHeight(level), Z = volume ? MipDepth(level) : 1 };
			return blit;
		}

		/// <summary>A layout change of levels of an image the device does not track (a scratch image), after the transfers before it.</summary>
		private void ScratchBarrier(VkCommandBuffer commands, VkImage image, int mip, int mips, VkImageLayout from, VkImageLayout to)
		{
			VkImageMemoryBarrier2 barrier = new VkImageMemoryBarrier2
			{
				SrcStageMask = from == VkImageLayout.Undefined ? VkPipelineStageFlags2.None : Sync.Transfer,
				SrcAccessMask = from == VkImageLayout.Undefined ? VkAccessFlags2.None : VkAccessFlags2.TransferWriteBit,
				DstStageMask = Sync.Transfer,
				DstAccessMask = VkAccessFlags2.TransferReadBit | VkAccessFlags2.TransferWriteBit,
				OldLayout = from,
				NewLayout = to,
				SrcQueueFamilyIndex = -1,
				DstQueueFamilyIndex = -1,
				Image = image,
				SubresourceRange = new VkImageSubresourceRange { AspectMask = Aspect, BaseMipLevel = mip, LevelCount = mips, BaseArrayLayer = 0, LayerCount = Layers }
			};
			VkDependencyInfo dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
			device.Vk->vkCmdPipelineBarrier2.Invoke(commands, &dependency);
		}

		/// <summary>
		///     The levels of an sRGB texture filtered in linear light, as samplers see the texels (and as OpenGL's mip
		///     generation filters them): the first level is copied into a scratch image of the sRGB format, whose blits
		///     decode and encode, and the levels made there are copied back. The scratch image goes with the submission.
		/// </summary>
		private void RecordDecodedMipmaps(bool ahead, VkFilter filter)
		{
			bool volume = Kind == TextureKind.Texture3D;
			VkImageCreateInfo info = new VkImageCreateInfo
			{
				ImageType = volume ? VkImageType._3D : VkImageType._2D,
				Format = SampledFormat,
				Extent = new VkExtent3D { Width = Width, Height = Height, Depth = volume ? Depth : 1 },
				MipLevels = MipLevels,
				ArrayLayers = Layers,
				Samples = VkSampleCountFlags._1Bit,
				Tiling = VkImageTiling.Optimal,
				Usage = VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit,
				SharingMode = VkSharingMode.Exclusive,
				InitialLayout = VkImageLayout.Undefined
			};
			VmaAllocationCreateInfo allocationInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto };
			// The scratch image lives until the submission has finished: it counts as the uploads' memory does.
			long scratchBytes = 0;
			for (int level = 0; level < MipLevels; level++)
			{
				scratchBytes += Format.LevelSize(MipWidth(level), MipHeight(level), volume ? MipDepth(level) : 1) * (volume ? 1 : Layers);
			}

			device.BeforeUpload(scratchBytes);
			VkImage scratch;
			VmaAllocation scratchAllocation;
			VulkanDevice.Check(Vma.PInvoke.vmaCreateImage(device.Allocator, &info, &allocationInfo, &scratch, &scratchAllocation, default), "vmaCreateImage (mip levels of " + Name + ")");
			device.AddRecordingMemory(scratchBytes);
			VkImageCopy* regions = stackalloc VkImageCopy[MipLevels];
			for (int level = 0; level < MipLevels; level++)
			{
				VkImageSubresourceLayers layers = new VkImageSubresourceLayers { AspectMask = Aspect, MipLevel = level, BaseArrayLayer = 0, LayerCount = Layers };
				regions[level] = new VkImageCopy
				{
					SrcSubresource = layers,
					DstSubresource = layers,
					Extent = new VkExtent3D { Width = MipWidth(level), Height = MipHeight(level), Depth = volume ? MipDepth(level) : 1 }
				};
			}

			// The first level into the scratch image, the levels made there one by one, and back.
			VulkanCommandList list = device.CommandList;
			VkCommandBuffer commands = list.TransferCommands(this, ahead, 0, 1, 0, Layers, CopySourceLayout, false, false);
			ScratchBarrier(commands, scratch, 0, MipLevels, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);
			device.Vk->vkCmdCopyImage.Invoke(commands, Image, CopySourceLayout, scratch, VkImageLayout.TransferDstOptimal, 1, regions);
			for (int level = 1; level < MipLevels; level++)
			{
				ScratchBarrier(commands, scratch, level - 1, 1, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal);
				VkImageBlit blit = LevelBlit(level);
				device.Vk->vkCmdBlitImage.Invoke(commands, scratch, VkImageLayout.TransferSrcOptimal, scratch, VkImageLayout.TransferDstOptimal, 1, &blit, filter);
			}

			ScratchBarrier(commands, scratch, MipLevels - 1, 1, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal);
			commands = list.TransferCommands(this, ahead, 1, MipLevels - 1, 0, Layers, CopyDestinationLayout, true, true);
			device.Vk->vkCmdCopyImage.Invoke(commands, scratch, VkImageLayout.TransferSrcOptimal, Image, CopyDestinationLayout, MipLevels - 1, regions + 1);
			device.Retire(RetiredKind.Image, scratch.VkHandle, scratchAllocation.VkHandle, device.RecordingSerial);
		}

		public override void Dispose()
		{
			if (IsDisposed)
			{
				return;
			}

			Forget();
			device.BindingEpoch++;
			foreach (VkImageView view in views.Values)
			{
				device.Retire(RetiredKind.ImageView, view.VkHandle, 0, LastUse);
			}

			views.Clear();
			device.Retire(RetiredKind.Image, Image.VkHandle, allocation.VkHandle, LastUse);
		}

		/// <summary>The region of a copy between a buffer and a box of a level (z: slice, layer or face).</summary>
		internal VkBufferImageCopy Region(int mip, int x, int y, int z, int width, int height, int depth, long bufferOffset)
		{
			bool volume = Kind == TextureKind.Texture3D;
			return new VkBufferImageCopy
			{
				BufferOffset = (Brutal.ByteSize64)(ulong)bufferOffset,
				ImageSubresource = new VkImageSubresourceLayers { AspectMask = Aspect, MipLevel = mip, BaseArrayLayer = volume ? 0 : z, LayerCount = volume ? 1 : depth },
				ImageOffset = new VkOffset3D { X = x, Y = y, Z = volume ? z : 0 },
				ImageExtent = new VkExtent3D { Width = width, Height = height, Depth = volume ? depth : 1 }
			};
		}

		/// <summary>Texels of a 24 bit depth format as the 32 bit floats the engine reads.</summary>
		internal static void UnpackDepth(uint* packed, float* floats, long count)
		{
			for (long i = 0; i < count; i++)
			{
				floats[i] = (packed[i] & 0xFFFFFF) / 16777215f;
			}
		}

		internal bool PackedDepth => packedDepth;
	}

	/// <summary>
	///     A buffer on Vulkan. Dynamic and read-back buffers live in memory the CPU sees and are written in place
	///     while no unfinished command uses them; everything else goes through a copy ordered with the commands.
	///     Replacing the storage of a buffer in use leaves the old storage to the commands that use it.
	/// </summary>
	internal sealed unsafe partial class VulkanBuffer : GpuBuffer
	{
		private const int MaxSpares = 4;

		/// <summary>Spare storages kept of a buffer of at most <see cref="SmallStorage" /> bytes.</summary>
		private const int MaxSmallSpares = 16;

		private const long SmallStorage = 64 * 1024;

		private readonly VulkanDevice device;
		private readonly bool hostVisible;
		private readonly List<Storage> outgoing = new List<Storage>();
		private readonly List<Storage> spares = new List<Storage>();
		private Storage storage;

		/// <summary>The submission that used the buffer's storage last (also through an upload).</summary>
		public long LastUse;

		/// <summary>The last submission whose stream of commands used the storage.</summary>
		public long MainUse;

		public VulkanBuffer(VulkanDevice device, in BufferDescriptor descriptor)
			: base(descriptor)
		{
			this.device = device;
			hostVisible = descriptor.Dynamic || (descriptor.Usage & BufferUsage.Readback) != 0;
			if (descriptor.Size > 0)
			{
				SetData(null, descriptor.Size);
			}
		}

		/// <summary>The buffer's storage now (null before the first data).</summary>
		public VkBuffer Handle => storage.Buffer;

		/// <summary>A command of the stream uses the buffer.</summary>
		public void Use()
		{
			LastUse = MainUse = device.RecordingSerial;
		}

		private bool Busy
		{
			get
			{
				if (LastUse > device.CompletedSerial)
				{
					device.Poll();
				}

				return LastUse > device.CompletedSerial;
			}
		}

		public override void SetData(void* data, long size)
		{
			if (IsDisposed)
			{
				throw new ObjectDisposedException(Name ?? "buffer");
			}

			if (size <= 0)
			{
				Release();
				Size = 0;
				return;
			}

			if (storage.Buffer.IsNull() || storage.Size != size || Busy)
			{
				Release();
				storage = TakeStorage(size);
				Size = size;
				ForgetUses();
				device.BindingEpoch++;
			}

			if (data != null)
			{
				Write(0, data, size);
			}
		}

		public override void SetSubData(long offset, void* data, long size)
		{
			if (size <= 0)
			{
				return;
			}

			if (offset < 0 || offset + size > Size)
			{
				throw new ArgumentOutOfRangeException(nameof(offset), $"Buffer '{Name}': {size} bytes at {offset} do not fit its {Size} bytes.");
			}

			Write(offset, data, size);
		}

		public override void Resize(long size, long keep)
		{
			if (size <= 0)
			{
				Release();
				Size = 0;
				return;
			}

			long copied = Math.Min(Math.Min(keep, Size), size);
			Storage old = storage;
			long oldLastUse = LastUse;
			long oldMainUse = MainUse;
			bool busy = Busy;
			Storage fresh = CreateStorage(size);
			long serial = device.RecordingSerial;
			if (copied > 0)
			{
				if (old.Mapped != null && fresh.Mapped != null && !busy)
				{
					Buffer.MemoryCopy(old.Mapped, fresh.Mapped, size, copied);
					LastUse = MainUse = 0;
					ForgetUses();
				}
				else
				{
					// Ordered with the commands that wrote the old storage.
					bool inStream = oldMainUse == serial;
					VkCommandBuffer commands = CopyIntoNewStorage(inStream);
					VkBufferCopy region = new VkBufferCopy { Size = (Brutal.ByteSize64)(ulong)copied };
					device.Vk->vkCmdCopyBuffer.Invoke(commands, old.Buffer, fresh.Buffer, 1, &region);
					oldLastUse = serial;
					LastUse = serial;
					MainUse = inStream ? serial : 0;
				}
			}
			else
			{
				LastUse = MainUse = 0;
				ForgetUses();
			}

			if (!old.Buffer.IsNull())
			{
				device.Retire(RetiredKind.Buffer, old.Buffer.VkHandle, old.Allocation.VkHandle, oldLastUse);
			}

			storage = fresh;
			Size = size;
			device.BindingEpoch++;
		}

		public override void GetData(long offset, void* destination, long size)
		{
			if (size <= 0)
			{
				return;
			}

			if (offset < 0 || offset + size > Size)
			{
				throw new ArgumentOutOfRangeException(nameof(offset), $"Buffer '{Name}': {size} bytes at {offset} are outside its {Size} bytes.");
			}

			VulkanCommandList list = device.CommandList;
			list.EndRenderPass();
			if (storage.Mapped != null)
			{
				if (LastUse > device.CompletedSerial)
				{
					device.WaitFor(LastUse, long.MaxValue);
				}

				Vma.PInvoke.vmaInvalidateAllocation(device.Allocator, storage.Allocation, (Brutal.ByteSize64)(ulong)offset, (Brutal.ByteSize64)(ulong)size);
				Buffer.MemoryCopy(storage.Mapped + offset, destination, size, size);
				return;
			}

			VkBufferCreateInfo info = new VkBufferCreateInfo { Size = (Brutal.ByteSize64)(ulong)size, Usage = VkBufferUsageFlags.TransferDstBit };
			VmaAllocationCreateInfo allocationInfo = new VmaAllocationCreateInfo
			{
				Usage = VmaMemoryUsage.Auto,
				Flags = VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.HostAccessRandomBit
			};
			VkBuffer buffer;
			VmaAllocation handle;
			VmaAllocationInfo made;
			VulkanDevice.Check(Vma.PInvoke.vmaCreateBuffer(device.Allocator, &info, &allocationInfo, &buffer, &handle, &made), "vmaCreateBuffer");
			// What is being recorded does not use the buffer: the copy goes alone, and only what was sent before is waited for.
			bool aside = LastUse < device.RecordingSerial;
			VkCommandBuffer commands = aside ? list.AsideCommands(this) : list.ReadCommands(this);
			VkBufferCopy region = new VkBufferCopy { SrcOffset = (Brutal.ByteSize64)(ulong)offset, Size = (Brutal.ByteSize64)(ulong)size };
			device.Vk->vkCmdCopyBuffer.Invoke(commands, storage.Buffer, buffer, 1, &region);
			list.TransfersToHost(commands);
			if (aside)
			{
				device.SubmitAsideAndWait(commands);
			}
			else
			{
				device.Submit();
				device.WaitFor(device.RecordingSerial - 1, long.MaxValue);
			}
			Vma.PInvoke.vmaInvalidateAllocation(device.Allocator, handle, default, (Brutal.ByteSize64)(ulong)size);
			Buffer.MemoryCopy((void*)made.PMappedData, destination, size, size);
			Vma.PInvoke.vmaDestroyBuffer(device.Allocator, buffer, handle);
		}

		public override void Dispose()
		{
			if (IsDisposed)
			{
				return;
			}

			IsDisposed = true;
			device.BindingEpoch++;
			Release();
			foreach (Storage spare in spares)
			{
				device.Retire(RetiredKind.Buffer, spare.Buffer.VkHandle, spare.Allocation.VkHandle, 0);
			}

			spares.Clear();
		}

		/// <summary>
		///     A storage of this buffer that the GPU has finished with: kept for the next time the buffer's storage is
		///     replaced (true), or left to be freed.
		/// </summary>
		internal bool TakeBack(nint handle, nint allocation)
		{
			for (int i = 0; i < outgoing.Count; i++)
			{
				if (outgoing[i].Buffer.VkHandle == handle)
				{
					Storage old = outgoing[i];
					outgoing.RemoveAt(i);
					// Small buffers rewritten several times a frame keep enough storages for every frame in flight.
					int kept = old.Size <= SmallStorage ? MaxSmallSpares : MaxSpares;
					if (!IsDisposed && old.Size == Size && spares.Count < kept)
					{
						spares.Add(old);
						return true;
					}

					return false;
				}
			}

			return false;
		}

		/// <summary>Gives the storage up: the commands that use it keep it until they have run.</summary>
		private void Release()
		{
			if (storage.Buffer.IsNull())
			{
				return;
			}

			bool recycle = Dynamic && !IsDisposed;
			if (recycle)
			{
				outgoing.Add(storage);
			}

			device.Retire(RetiredKind.Buffer, storage.Buffer.VkHandle, storage.Allocation.VkHandle, LastUse, recycle ? this : null);
			storage = default;
			LastUse = MainUse = 0;
			ForgetUses();
		}

		private Storage TakeStorage(long size)
		{
			for (int i = spares.Count - 1; i >= 0; i--)
			{
				Storage spare = spares[i];
				spares.RemoveAt(i);
				if (spare.Size == size)
				{
					return spare;
				}

				device.Retire(RetiredKind.Buffer, spare.Buffer.VkHandle, spare.Allocation.VkHandle, 0);
			}

			return CreateStorage(size);
		}

		private Storage CreateStorage(long size)
		{
			VkBufferUsageFlags usage = VkBufferUsageFlags.TransferSrcBit | VkBufferUsageFlags.TransferDstBit;
			if ((Usage & BufferUsage.Vertex) != 0)
			{
				usage |= VkBufferUsageFlags.VertexBufferBit;
			}

			if ((Usage & BufferUsage.Index) != 0)
			{
				usage |= VkBufferUsageFlags.IndexBufferBit;
			}

			if ((Usage & BufferUsage.Uniform) != 0)
			{
				usage |= VkBufferUsageFlags.UniformBufferBit;
			}

			if ((Usage & BufferUsage.Storage) != 0)
			{
				usage |= VkBufferUsageFlags.StorageBufferBit;
			}

			if ((Usage & BufferUsage.Indirect) != 0)
			{
				usage |= VkBufferUsageFlags.IndirectBufferBit;
			}

			if ((Usage & BufferUsage.AccelerationStructureInput) != 0 && device.HasRayQuery)
			{
				// Builds read the vertices and indices by address.
				usage |= VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKHR | VkBufferUsageFlags.ShaderDeviceAddressBit;
			}

			VkBufferCreateInfo info = new VkBufferCreateInfo { Size = (Brutal.ByteSize64)(ulong)size, Usage = usage };
			VmaAllocationCreateInfo allocationInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto };
			if (hostVisible)
			{
				allocationInfo.Flags = VmaAllocationCreateFlags.MappedBit
					| ((Usage & BufferUsage.Readback) != 0 ? VmaAllocationCreateFlags.HostAccessRandomBit : VmaAllocationCreateFlags.HostAccessSequentialWriteBit);
			}

			VkBuffer buffer;
			VmaAllocation handle;
			VmaAllocationInfo made;
			VulkanDevice.Check(Vma.PInvoke.vmaCreateBuffer(device.Allocator, &info, &allocationInfo, &buffer, &handle, &made), "vmaCreateBuffer (" + Name + ", " + size + " bytes)");
			return new Storage { Buffer = buffer, Allocation = handle, Mapped = hostVisible ? (byte*)(void*)made.PMappedData : null, Size = size };
		}

		/// <summary>Writes bytes into the storage, at once when no unfinished command uses it, else ordered with the commands.</summary>
		private void Write(long offset, void* data, long size)
		{
			if (storage.Mapped != null && !Busy)
			{
				Buffer.MemoryCopy(data, storage.Mapped + offset, storage.Size - offset, size);
				return;
			}

			device.BeforeUpload(size);
			byte* staging = device.AllocateTransient(size, 16, out VkBuffer source, out long sourceOffset);
			Buffer.MemoryCopy(data, staging, size, size);
			VkCommandBuffer commands = device.CommandList.UploadCommands(this);
			VkBufferCopy region = new VkBufferCopy
			{
				SrcOffset = (Brutal.ByteSize64)(ulong)sourceOffset,
				DstOffset = (Brutal.ByteSize64)(ulong)offset,
				Size = (Brutal.ByteSize64)(ulong)size
			};
			device.Vk->vkCmdCopyBuffer.Invoke(commands, source, storage.Buffer, 1, &region);
		}

		private struct Storage
		{
			public VkBuffer Buffer;
			public VmaAllocation Allocation;
			public byte* Mapped;
			public long Size;
		}
	}

	/// <summary>A fence on Vulkan: the number of the submission that holds everything recorded when it was made.</summary>
	internal sealed class VulkanFence : GpuFence
	{
		private readonly VulkanDevice device;
		private readonly long serial;

		public VulkanFence(VulkanDevice device, long serial)
		{
			this.device = device;
			this.serial = serial;
		}

		public override bool IsSignaled
		{
			get
			{
				if (serial > device.CompletedSerial && VulkanDevice.ContextOfThread != null)
				{
					device.Poll();
				}

				return serial <= device.CompletedSerial;
			}
		}

		public override bool Wait(long timeoutNanoseconds)
		{
			return device.WaitFor(serial, timeoutNanoseconds);
		}

		public override void Dispose()
		{ }
	}

	/// <summary>A timestamp on Vulkan: a query of the device's timestamp pools, another one for every stamp written.</summary>
	internal sealed class VulkanTimestamp : GpuTimestamp
	{
		private readonly VulkanDevice device;
		private int query = -1;
		private long serial;

		public VulkanTimestamp(VulkanDevice device)
		{
			this.device = device;
		}

		public override bool IsAvailable
		{
			get
			{
				if (query < 0)
				{
					return false;
				}

				if (serial > device.CompletedSerial)
				{
					device.Poll();
				}

				return serial <= device.CompletedSerial;
			}
		}

		public override long Nanoseconds
		{
			get
			{
				if (query < 0)
				{
					return 0;
				}

				device.WaitFor(serial, long.MaxValue);
				return device.ReadQuery(query, serial);
			}
		}

		/// <summary>The query the next stamp is written into (the one of the stamp before goes back when its commands have run).</summary>
		public int Begin()
		{
			if (query >= 0)
			{
				device.ReleaseQuery(query, serial);
			}

			query = device.TakeQuery();
			serial = device.RecordingSerial;
			return query;
		}

		public override void Dispose()
		{
			if (query >= 0)
			{
				device.ReleaseQuery(query, serial);
				query = -1;
			}
		}
	}
}
