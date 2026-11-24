using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	internal sealed unsafe partial class VulkanTexture
	{
		/// <summary>The state of every subresource while they are all alike (<see cref="subresourceStates" /> null), for the graphics queue's commands.</summary>
		private SyncState wholeState;

		/// <summary>The state of each subresource (index mip * <see cref="GpuTexture.Layers" /> + layer) once they differ.</summary>
		private SyncState[] subresourceStates;

		/// <summary>
		///     The same for the compute queue's commands (VulkanDeviceQueues.cs), from its first use there: its layouts are
		///     the graphics states' (a layout is the image's), its accesses its own.
		/// </summary>
		private SyncState computeWholeState;

		private SyncState[] computeSubresourceStates;
		private bool computeStates;

		/// <summary>Where the two queues last read and wrote the texture.</summary>
		private QueueUses queueUses;

		private bool generalMode;

		/// <summary>The last pass (<see cref="VulkanCommandList" />'s serial) that drew into the texture, and the lowest level it drew into.</summary>
		internal long ColorPass = -1;

		internal int ColorMip;

		/// <summary>
		///     The texture is sampled where a pass draws into the same level (a feedback loop OpenGL tolerates): from then on
		///     every use of it is in the general layout, which allows that.
		/// </summary>
		internal bool GeneralMode => generalMode;

		/// <summary>Shaders write the texture as an image: it stays in the general layout, which every use takes.</summary>
		private bool StaysGeneral => generalMode || (Usage & TextureUsage.Storage) != 0;

		/// <summary>The layout samplers read the texture in (also a depth attachment's while draws sample it).</summary>
		internal VkImageLayout SampledLayout => StaysGeneral ? VkImageLayout.General : VkImageLayout.ReadOnlyOptimal;

		/// <summary>The layout a render pass draws into the texture in.</summary>
		internal VkImageLayout AttachmentLayout => StaysGeneral ? VkImageLayout.General : VkImageLayout.AttachmentOptimal;

		/// <summary>The layout copies, blits and readbacks read the texture in.</summary>
		internal VkImageLayout CopySourceLayout => (Usage & TextureUsage.Storage) != 0 ? VkImageLayout.General : VkImageLayout.TransferSrcOptimal;

		/// <summary>The layout uploads, copies, blits and clears write the texture in.</summary>
		internal VkImageLayout CopyDestinationLayout => (Usage & TextureUsage.Storage) != 0 ? VkImageLayout.General : VkImageLayout.TransferDstOptimal;

		/// <summary>The first array layer of the subresources of a layer, face or slice (a 3D texture's slices are not subresources: it has one layer).</summary>
		internal int SubresourceLayer(int layer)
		{
			return Kind == TextureKind.Texture3D ? 0 : Math.Max(0, layer);
		}

		/// <summary>Whether a write of a box of a level overwrites its subresources whole (a 3D texture's level: every slice).</summary>
		internal bool Covers(int mip, int x, int y, int z, int width, int height, int depth)
		{
			return x == 0 && y == 0 && width >= MipWidth(mip) && height >= MipHeight(mip) && (Kind != TextureKind.Texture3D || (z == 0 && depth >= MipDepth(mip)));
		}

		/// <summary>
		///     Records a use of levels and layers of the texture into a batch: the barrier it needs (a layout change, else a
		///     memory dependency), and the new state.
		/// </summary>
		internal void Require(SyncBatch batch, int mip, int mips, int layer, int layers, VkImageLayout layout, VkPipelineStageFlags2 stages, bool write, bool discard)
		{
			if (!device.HasAsyncCompute || device.AsideRecording)
			{
				if (Apply(ref wholeState, ref subresourceStates, batch, mip, mips, layer, layers, layout, stages, write, discard))
				{
					device.SyncEpoch++;
				}

				return;
			}

			// Two queues: after the other queue's conflicting uses (a layout change counts as a write), in this queue's
			// own state; a write leaves the other queue nothing to wait for in its state but the timeline.
			bool changes = write || LayoutDiffers(mip, mips, layer, layers, layout);
			device.OrderAcrossQueues(ref queueUses, changes);
			bool changed;
			if (device.RecordingCompute)
			{
				if (!computeStates)
				{
					computeStates = true;
					computeWholeState = new SyncState { Layout = wholeState.Layout };
					if (subresourceStates != null)
					{
						computeSubresourceStates = new SyncState[subresourceStates.Length];
						for (int i = 0; i < subresourceStates.Length; i++)
						{
							computeSubresourceStates[i] = new SyncState { Layout = subresourceStates[i].Layout };
						}
					}
				}

				changed = Apply(ref computeWholeState, ref computeSubresourceStates, batch, mip, mips, layer, layers, layout, stages, write, discard);
				if (changes)
				{
					Settle(ref wholeState, ref subresourceStates, mip, mips, layer, layers, layout);
				}
			}
			else
			{
				changed = Apply(ref wholeState, ref subresourceStates, batch, mip, mips, layer, layers, layout, stages, write, discard);
				if (changes && computeStates)
				{
					Settle(ref computeWholeState, ref computeSubresourceStates, mip, mips, layer, layers, layout);
				}
			}

			if (changed)
			{
				device.SyncEpoch++;
			}
		}

		/// <summary>Whether a subresource of the range is in another layout than <paramref name="layout" /> (the layouts are the same in both queues' states).</summary>
		private bool LayoutDiffers(int mip, int mips, int layer, int layers, VkImageLayout layout)
		{
			if (subresourceStates == null)
			{
				return wholeState.Layout != layout;
			}

			for (int m = mip; m < mip + mips; m++)
			{
				for (int l = layer; l < layer + layers; l++)
				{
					if (subresourceStates[m * Layers + l].Layout != layout)
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		///     The other queue's state of a range the queue being recorded for took into <paramref name="layout" />: the
		///     image's layout is the new one there. What the other queue did itself stays in its state (its next use waits
		///     for this one by the timeline, and for its own uses by its barrier).
		/// </summary>
		private void Settle(ref SyncState whole, ref SyncState[] parts, int mip, int mips, int layer, int layers, VkImageLayout layout)
		{
			if (parts == null)
			{
				if (whole.Layout == layout)
				{
					return;
				}

				if (mip == 0 && mips == MipLevels && layer == 0 && layers == Layers)
				{
					whole.Layout = layout;
					return;
				}

				parts = new SyncState[MipLevels * Layers];
				Array.Fill(parts, whole);
			}

			for (int m = mip; m < mip + mips; m++)
			{
				for (int l = layer; l < layer + layers; l++)
				{
					parts[m * Layers + l].Layout = layout;
				}
			}
		}

		/// <summary>Records a use into one queue's states (<see cref="Require" />); true when it may give other uses a barrier to wait for.</summary>
		private bool Apply(ref SyncState whole, ref SyncState[] parts, SyncBatch batch, int mip, int mips, int layer, int layers, VkImageLayout layout, VkPipelineStageFlags2 stages,
			bool write, bool discard)
		{
			bool entire = mip == 0 && mips == MipLevels && layer == 0 && layers == Layers;
			if (parts == null)
			{
				if (entire)
				{
					return Sync.Apply(ref whole, batch, Image, Aspect, 0, MipLevels, 0, Layers, layout, stages, write, discard);
				}

				parts = new SyncState[MipLevels * Layers];
				Array.Fill(parts, whole);
			}

			bool changed = false;
			for (int m = mip; m < mip + mips; m++)
			{
				for (int l = layer; l < layer + layers; l++)
				{
					changed |= Sync.Apply(ref parts[m * Layers + l], batch, Image, Aspect, m, 1, l, 1, layout, stages, write, discard);
				}
			}

			if (entire)
			{
				// Used whole: the subresources are likely alike again.
				for (int i = 1; i < parts.Length; i++)
				{
					if (!parts[i].Equals(parts[0]))
					{
						return changed;
					}
				}

				whole = parts[0];
				parts = null;
			}

			return changed;
		}

		/// <summary>Whether a use of levels and layers of the texture has to wait for something before it.</summary>
		internal bool Needs(int mip, int mips, int layer, int layers, VkImageLayout layout, VkPipelineStageFlags2 stages, bool write)
		{
			if (subresourceStates == null)
			{
				return Sync.Needs(wholeState, layout, stages, write);
			}

			for (int m = mip; m < mip + mips; m++)
			{
				for (int l = layer; l < layer + layers; l++)
				{
					if (Sync.Needs(subresourceStates[m * Layers + l], layout, stages, write))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		///     Takes the texture into the general layout for every use from now on (it is sampled where a pass draws into
		///     it). Its views were written into descriptor sets with another layout: they are given up, and new ones made.
		/// </summary>
		internal void EnterGeneralMode()
		{
			if (generalMode)
			{
				return;
			}

			bool viewsStay = StaysGeneral;
			generalMode = true;
			if (viewsStay)
			{
				return;
			}

			foreach (VkImageView view in views.Values)
			{
				device.Retire(RetiredKind.ImageView, view.VkHandle, 0, Math.Max(LastUse, device.RecordingSerial));
			}

			views.Clear();
			device.BindingEpoch++;
		}

		/// <summary>Levels 0 .. <paramref name="mips" /> - 1 of the texture, as samplers read them (a texture a pass draws into a level below them).</summary>
		internal VkImageView SampledLevels(int mips)
		{
			return mips >= MipLevels ? SampledView : View(NaturalViewType, 0, mips, 0, Layers, true);
		}
	}

	internal sealed unsafe partial class VulkanBuffer
	{
		/// <summary>What the graphics queue's commands recorded so far do to the buffer's storage.</summary>
		private SyncState state;

		/// <summary>What the compute queue's commands do to it (VulkanDeviceQueues.cs).</summary>
		private SyncState computeState;

		/// <summary>Where the two queues last read and wrote the storage.</summary>
		private QueueUses queueUses;

		/// <summary>The buffer lives in memory the CPU maps.</summary>
		internal bool IsHostVisible => hostVisible;

		/// <summary>
		///     Records a use of the buffer by stages into a batch: the memory dependency it needs, and the new state (on a
		///     device with two queues, after the other queue's conflicting uses, in the state of the queue being recorded for).
		/// </summary>
		internal void Require(SyncBatch batch, VkPipelineStageFlags2 stages, bool write)
		{
			bool compute = false;
			if (device.HasAsyncCompute && !device.AsideRecording)
			{
				// The other queue's state keeps its own uses: its next use waits for this one by the timeline.
				device.OrderAcrossQueues(ref queueUses, write);
				compute = device.RecordingCompute;
			}

			if (Sync.Apply(ref compute ? ref computeState : ref state, batch, default, VkImageAspectFlags.None, 0, 0, 0, 0, VkImageLayout.Undefined, stages, write, false))
			{
				device.SyncEpoch++;
				if (write && hostVisible)
				{
					device.HostReads |= stages;
				}
			}
		}

		/// <summary>Whether a use of the buffer by stages of the graphics queue has to wait for something of that queue before it.</summary>
		internal bool Needs(VkPipelineStageFlags2 stages, bool write)
		{
			return Sync.Needs(state, VkImageLayout.Undefined, stages, write);
		}

		/// <summary>The storage is new (or one the GPU has finished with): no use of it waits for anything.</summary>
		private void ForgetUses()
		{
			state = default;
			computeState = default;
			queueUses = default;
		}

		/// <summary>
		///     The command buffer the copy of the storage into a new one goes into (in the stream, or ahead of it), after
		///     the barrier the copy's read needs. What the state says from then on is of the new storage, which the copy
		///     writes.
		/// </summary>
		private VkCommandBuffer CopyIntoNewStorage(bool inStream)
		{
			VkCommandBuffer commands;
			if (inStream)
			{
				commands = device.CommandList.ReadCommands(this);
			}
			else
			{
				Require(device.SetupBatch, Sync.Transfer, false);
				commands = device.SetupCommands;
				device.SetupBatch.Record(device, commands);
			}

			SyncState written = new SyncState { Writes = Sync.Transfer, WriteAccess = VkAccessFlags2.TransferWriteBit };
			bool compute = device.HasAsyncCompute && device.RecordingCompute;
			state = compute ? default : written;
			computeState = compute ? written : default;
			queueUses = default;
			if (device.HasAsyncCompute)
			{
				long value = device.Recording.RecordingValue;
				if (compute)
				{
					queueUses.ComputeWrite = value;
				}
				else
				{
					queueUses.GraphicsWrite = value;
				}
			}

			device.SyncEpoch++;
			if (hostVisible)
			{
				device.HostReads |= Sync.Transfer;
			}

			return commands;
		}
	}
}
