using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     What the commands recorded so far do to a buffer, or to one subresource of an image: the layout it is in, the
	///     stages of its last write (or layout change), the stages that read it since, and the stages that write has been
	///     made visible to. A command that uses the resource compares its use with this, and the barrier the use needs is
	///     the difference (<see cref="Sync.Apply" />).
	/// </summary>
	internal struct SyncState : IEquatable<SyncState>
	{
		/// <summary>The image's layout (<see cref="VkImageLayout.Undefined" /> for buffers, and for images never used).</summary>
		public VkImageLayout Layout;

		/// <summary>The stages of the last write or layout change: none while nothing has to be waited for.</summary>
		public VkPipelineStageFlags2 Writes;

		/// <summary>The accesses of the last write, which a barrier after it makes available (none after a layout change, which is).</summary>
		public VkAccessFlags2 WriteAccess;

		/// <summary>The stages that read since the last write: a write or a layout change waits for them.</summary>
		public VkPipelineStageFlags2 Reads;

		/// <summary>The stages the last write is visible to: reads in them need no barrier.</summary>
		public VkPipelineStageFlags2 Visible;

		public bool Equals(SyncState other)
		{
			return Layout == other.Layout && Writes == other.Writes && WriteAccess == other.WriteAccess && Reads == other.Reads && Visible == other.Visible;
		}

		public override bool Equals(object obj)
		{
			return obj is SyncState other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(Layout, Writes, WriteAccess, Reads, Visible);
		}
	}

	/// <summary>
	///     How the device orders its commands: every use of a resource (a texture's levels and layers, a buffer) is
	///     compared with what came before it (<see cref="SyncState" />), and a barrier is recorded only where a use has to
	///     wait, with the stages and accesses of the uses on both sides and the layouts images are used in: attachments in
	///     <see cref="VkImageLayout.AttachmentOptimal" />, sampled textures in <see cref="VkImageLayout.ReadOnlyOptimal" />,
	///     copies in the transfer layouts, images shaders write in <see cref="VkImageLayout.General" />.
	/// </summary>
	internal static class Sync
	{
		/// <summary>The shader stages of draws.</summary>
		public const VkPipelineStageFlags2 GraphicsShaders = VkPipelineStageFlags2.PreRasterizationShadersBit | VkPipelineStageFlags2.FragmentShaderBit;

		public const VkPipelineStageFlags2 ComputeShader = VkPipelineStageFlags2.ComputeShaderBit;
		public const VkPipelineStageFlags2 ColorOutput = VkPipelineStageFlags2.ColorAttachmentOutputBit;
		public const VkPipelineStageFlags2 DepthTests = VkPipelineStageFlags2.EarlyFragmentTestsBit | VkPipelineStageFlags2.LateFragmentTestsBit;

		/// <summary>Copies, blits and clears.</summary>
		public const VkPipelineStageFlags2 Transfer = VkPipelineStageFlags2.AllTransferBit;

		public const VkPipelineStageFlags2 Host = VkPipelineStageFlags2.HostBit;

		/// <summary>
		///     Reads of vertex buffers. AMD's GPUs fetch vertices in the vertex shader, and their driver orders a barrier
		///     after the vertex input stage alone before the vertex shaders have read them (a buffer rewritten after a
		///     draw changed what the draw saw): the stages of the vertex shaders are counted with it.
		/// </summary>
		public const VkPipelineStageFlags2 VertexInput = VkPipelineStageFlags2.VertexAttributeInputBit | VkPipelineStageFlags2.PreRasterizationShadersBit;

		/// <summary>Reads of index buffers (with the vertex shaders, which run after the indices are read, as for <see cref="VertexInput" />).</summary>
		public const VkPipelineStageFlags2 IndexInput = VkPipelineStageFlags2.IndexInputBit | VkPipelineStageFlags2.PreRasterizationShadersBit;

		/// <summary>Arguments of indirect draws and dispatches.</summary>
		public const VkPipelineStageFlags2 Indirect = VkPipelineStageFlags2.DrawIndirectBit;

		/// <summary>Builds and copies of acceleration structures, and the queries of their compacted sizes.</summary>
		public const VkPipelineStageFlags2 AccelerationBuild = VkPipelineStageFlags2.AccelerationStructureBuildBitKHR;

		/// <summary>
		///     Everything a draw reads buffers with: a barrier before a draw makes a write visible to all of it, so the
		///     draws after it in the same pass read the buffer in other ways without one more.
		/// </summary>
		public const VkPipelineStageFlags2 DrawReads = GraphicsShaders | VertexInput | IndexInput | Indirect;

		private const VkPipelineStageFlags2 Shaders = GraphicsShaders | ComputeShader;

		/// <summary>What the stages read with.</summary>
		public static VkAccessFlags2 ReadAccess(VkPipelineStageFlags2 stages)
		{
			VkAccessFlags2 access = VkAccessFlags2.None;
			if ((stages & Shaders) != 0)
			{
				access |= VkAccessFlags2.ShaderSampledReadBit | VkAccessFlags2.ShaderStorageReadBit | VkAccessFlags2.UniformReadBit;
			}

			if ((stages & ColorOutput) != 0)
			{
				access |= VkAccessFlags2.ColorAttachmentReadBit;
			}

			if ((stages & DepthTests) != 0)
			{
				access |= VkAccessFlags2.DepthStencilAttachmentReadBit;
			}

			if ((stages & Transfer) != 0)
			{
				access |= VkAccessFlags2.TransferReadBit;
			}

			if ((stages & Host) != 0)
			{
				access |= VkAccessFlags2.HostReadBit;
			}

			if ((stages & VkPipelineStageFlags2.VertexAttributeInputBit) != 0)
			{
				access |= VkAccessFlags2.VertexAttributeReadBit;
			}

			if ((stages & VkPipelineStageFlags2.IndexInputBit) != 0)
			{
				access |= VkAccessFlags2.IndexReadBit;
			}

			if ((stages & Indirect) != 0)
			{
				access |= VkAccessFlags2.IndirectCommandReadBit;
			}

			if ((stages & AccelerationBuild) != 0)
			{
				// Build inputs (vertices, indices, instances) are shader reads; structures and build memory are read as structures.
				access |= VkAccessFlags2.ShaderReadBit | VkAccessFlags2.AccelerationStructureReadBitKHR;
			}

			return access;
		}

		/// <summary>What the stages write with.</summary>
		public static VkAccessFlags2 WriteAccess(VkPipelineStageFlags2 stages)
		{
			VkAccessFlags2 access = VkAccessFlags2.None;
			if ((stages & Shaders) != 0)
			{
				access |= VkAccessFlags2.ShaderStorageWriteBit;
			}

			if ((stages & ColorOutput) != 0)
			{
				access |= VkAccessFlags2.ColorAttachmentWriteBit;
			}

			if ((stages & DepthTests) != 0)
			{
				access |= VkAccessFlags2.DepthStencilAttachmentWriteBit;
			}

			if ((stages & Transfer) != 0)
			{
				access |= VkAccessFlags2.TransferWriteBit;
			}

			if ((stages & Host) != 0)
			{
				access |= VkAccessFlags2.HostWriteBit;
			}

			if ((stages & AccelerationBuild) != 0)
			{
				access |= VkAccessFlags2.AccelerationStructureWriteBitKHR;
			}

			return access;
		}

		/// <summary>Whether a use of a resource in a layout (buffers: undefined) has to wait for something before it.</summary>
		public static bool Needs(in SyncState state, VkImageLayout layout, VkPipelineStageFlags2 stages, bool write)
		{
			if (state.Layout != layout)
			{
				return true;
			}

			if (write)
			{
				return (state.Writes | state.Reads) != 0;
			}

			return state.Writes != 0 && (stages & ~state.Visible) != 0;
		}

		/// <summary>
		///     Records a use of a resource in a layout (buffers: <see cref="VkImageLayout.Undefined" />) by stages that read
		///     it or, with <paramref name="write" />, write it too: adds what has to come before the use to the batch (a
		///     barrier that changes the image's layout, else a memory dependency) and updates the state. A layout change of
		///     contents the use overwrites whole (<paramref name="discard" />) leaves them behind. True when the state
		///     changed in a way that may give other uses a barrier to wait for (a write or a layout change).
		/// </summary>
		/// <param name="extraRead">Accesses the stages read with besides their usual ones (acceleration structures that shaders trace).</param>
		public static bool Apply(ref SyncState state, SyncBatch batch, VkImage image, VkImageAspectFlags aspect, int mip, int mips, int layer, int layers, VkImageLayout layout,
			VkPipelineStageFlags2 stages, bool write, bool discard, VkAccessFlags2 extraRead = VkAccessFlags2.None)
		{
			VkAccessFlags2 access = ReadAccess(stages) | extraRead | (write ? WriteAccess(stages) : VkAccessFlags2.None);
			if (state.Layout != layout)
			{
				batch.AddImage(image, aspect, mip, mips, layer, layers, discard ? VkImageLayout.Undefined : state.Layout, layout, state.Writes | state.Reads, state.WriteAccess, stages,
					access);
				state.Layout = layout;
				state.Writes = stages;
				if (write)
				{
					state.WriteAccess = WriteAccess(stages);
					state.Reads = VkPipelineStageFlags2.None;
					state.Visible = VkPipelineStageFlags2.None;
				}
				else
				{
					// The layout change is the write the stages wait for: what reads later in other stages waits for them.
					state.WriteAccess = VkAccessFlags2.None;
					state.Reads = stages;
					state.Visible = stages;
				}

				return true;
			}

			if (write)
			{
				if ((state.Writes | state.Reads) != 0)
				{
					batch.AddMemory(state.Writes | state.Reads, state.WriteAccess, stages, access);
				}

				state.Writes = stages;
				state.WriteAccess = WriteAccess(stages);
				state.Reads = VkPipelineStageFlags2.None;
				state.Visible = VkPipelineStageFlags2.None;
				return true;
			}

			if (state.Writes != 0 && (stages & ~state.Visible) != 0)
			{
				batch.AddMemory(state.Writes, state.WriteAccess, stages, access);
				state.Visible |= stages;
			}

			state.Reads |= stages;
			return false;
		}
	}

	/// <summary>
	///     A resource of the device's own that commands use (the memory acceleration structure builds work in, a top
	///     level's instances, an acceleration structure), whose uses are ordered like a buffer's.
	/// </summary>
	internal sealed class SyncTarget
	{
		/// <summary>What the graphics queue's commands do to it.</summary>
		public SyncState State;

		/// <summary>What the compute queue's commands do to it.</summary>
		public SyncState ComputeState;

		/// <summary>The submissions of either queue that use it last.</summary>
		public QueueUses Queues;

		/// <summary>The last submission whose stream used it (its barriers then stay in the stream).</summary>
		public long MainUse;

		/// <summary>An acceleration structure: shaders read it as one (ray queries).</summary>
		public readonly bool AccelerationStructure;

		public SyncTarget(bool accelerationStructure = false)
		{
			AccelerationStructure = accelerationStructure;
		}

		/// <summary>The resource is new: no use of it waits for anything.</summary>
		public void Forget()
		{
			State = default;
			ComputeState = default;
			Queues = default;
		}
	}

	/// <summary>
	///     Where the two queues of a device with an asynchronous compute queue (<see cref="VulkanStream" />) last used a
	///     resource: the values of their timelines (a submission's number on its queue) of the last read and the last
	///     write (or layout change) of each. A use on one queue waits for the other's submission of a use it conflicts
	///     with (<see cref="VulkanDevice.OrderAcrossQueues" />): a read for the last write, a write for the last read and
	///     write. Reads on both queues run side by side.
	/// </summary>
	internal struct QueueUses
	{
		public long GraphicsRead;
		public long GraphicsWrite;
		public long ComputeRead;
		public long ComputeWrite;
	}

	/// <summary>
	///     The barriers to record before a command, gathered from the uses of its resources: image barriers for layout
	///     changes, one memory barrier for the rest. Recorded as one <c>vkCmdPipelineBarrier2</c>.
	/// </summary>
	internal sealed unsafe class SyncBatch
	{
		private VkImageMemoryBarrier2[] images = new VkImageMemoryBarrier2[16];
		private int imageCount;
		private VkMemoryBarrier2 memory = new VkMemoryBarrier2();
		private bool hasMemory;

		public bool IsEmpty => imageCount == 0 && !hasMemory;

		public void AddMemory(VkPipelineStageFlags2 source, VkAccessFlags2 sourceAccess, VkPipelineStageFlags2 destination, VkAccessFlags2 destinationAccess)
		{
			memory.SrcStageMask |= source;
			memory.SrcAccessMask |= sourceAccess;
			memory.DstStageMask |= destination;
			memory.DstAccessMask |= destinationAccess;
			hasMemory = true;
		}

		/// <summary>A layout change of a range of an image; joined to the last one when it continues its range with the same change.</summary>
		public void AddImage(VkImage image, VkImageAspectFlags aspect, int mip, int mips, int layer, int layers, VkImageLayout from, VkImageLayout to, VkPipelineStageFlags2 source,
			VkAccessFlags2 sourceAccess, VkPipelineStageFlags2 destination, VkAccessFlags2 destinationAccess)
		{
			if (imageCount > 0)
			{
				ref VkImageMemoryBarrier2 last = ref images[imageCount - 1];
				if (last.Image.VkHandle == image.VkHandle && last.OldLayout == from && last.NewLayout == to && last.SrcStageMask == source && last.SrcAccessMask == sourceAccess
					&& last.DstStageMask == destination && last.DstAccessMask == destinationAccess)
				{
					ref VkImageSubresourceRange range = ref last.SubresourceRange;
					if (range.BaseMipLevel == mip && range.LevelCount == mips && range.BaseArrayLayer + range.LayerCount == layer)
					{
						range.LayerCount += layers;
						return;
					}

					if (range.BaseArrayLayer == layer && range.LayerCount == layers && range.BaseMipLevel + range.LevelCount == mip)
					{
						range.LevelCount += mips;
						return;
					}
				}
			}

			if (imageCount == images.Length)
			{
				Array.Resize(ref images, images.Length * 2);
			}

			images[imageCount++] = new VkImageMemoryBarrier2
			{
				SrcStageMask = source,
				SrcAccessMask = sourceAccess,
				DstStageMask = destination,
				DstAccessMask = destinationAccess,
				OldLayout = from,
				NewLayout = to,
				SrcQueueFamilyIndex = -1,
				DstQueueFamilyIndex = -1,
				Image = image,
				SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, BaseMipLevel = mip, LevelCount = mips, BaseArrayLayer = layer, LayerCount = layers }
			};
		}

		/// <summary>Records the barriers (nothing when there are none) and empties the batch.</summary>
		public void Record(VulkanDevice device, VkCommandBuffer commands)
		{
			if (IsEmpty)
			{
				return;
			}

			VkMemoryBarrier2 global = memory;
			fixed (VkImageMemoryBarrier2* barriers = images)
			{
				VkDependencyInfo dependency = new VkDependencyInfo
				{
					MemoryBarrierCount = hasMemory ? 1 : 0,
					MemoryBarriers = &global,
					ImageMemoryBarrierCount = imageCount,
					ImageMemoryBarriers = barriers
				};
				device.Vk->vkCmdPipelineBarrier2.Invoke(commands, &dependency);
			}

			device.BarriersRecorded++;
			Clear();
		}

		public void Clear()
		{
			imageCount = 0;
			hasMemory = false;
			memory = new VkMemoryBarrier2();
		}
	}
}
