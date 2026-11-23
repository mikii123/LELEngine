using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	internal sealed unsafe partial class VulkanTexture
	{
		/// <summary>The state of every subresource while they are all alike (<see cref="subresourceStates" /> null).</summary>
		private SyncState wholeState;

		/// <summary>The state of each subresource (index mip * <see cref="GpuTexture.Layers" /> + layer) once they differ.</summary>
		private SyncState[] subresourceStates;

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
			bool whole = mip == 0 && mips == MipLevels && layer == 0 && layers == Layers;
			if (subresourceStates == null)
			{
				if (whole)
				{
					if (Sync.Apply(ref wholeState, batch, Image, Aspect, 0, MipLevels, 0, Layers, layout, stages, write, discard))
					{
						device.SyncEpoch++;
					}

					return;
				}

				subresourceStates = new SyncState[MipLevels * Layers];
				Array.Fill(subresourceStates, wholeState);
			}

			bool changed = false;
			for (int m = mip; m < mip + mips; m++)
			{
				for (int l = layer; l < layer + layers; l++)
				{
					changed |= Sync.Apply(ref subresourceStates[m * Layers + l], batch, Image, Aspect, m, 1, l, 1, layout, stages, write, discard);
				}
			}

			if (changed)
			{
				device.SyncEpoch++;
			}

			if (whole)
			{
				// Used whole: the subresources are likely alike again.
				for (int i = 1; i < subresourceStates.Length; i++)
				{
					if (!subresourceStates[i].Equals(subresourceStates[0]))
					{
						return;
					}
				}

				wholeState = subresourceStates[0];
				subresourceStates = null;
			}
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
		/// <summary>What the commands recorded so far do to the buffer's storage.</summary>
		private SyncState state;

		/// <summary>The buffer lives in memory the CPU maps.</summary>
		internal bool IsHostVisible => hostVisible;

		/// <summary>Records a use of the buffer by stages into a batch: the memory dependency it needs, and the new state.</summary>
		internal void Require(SyncBatch batch, VkPipelineStageFlags2 stages, bool write)
		{
			if (Sync.Apply(ref state, batch, default, VkImageAspectFlags.None, 0, 0, 0, 0, VkImageLayout.Undefined, stages, write, false))
			{
				device.SyncEpoch++;
				if (write && hostVisible)
				{
					device.HostReads |= stages;
				}
			}
		}

		/// <summary>Whether a use of the buffer by stages has to wait for something before it.</summary>
		internal bool Needs(VkPipelineStageFlags2 stages, bool write)
		{
			return Sync.Needs(state, VkImageLayout.Undefined, stages, write);
		}

		/// <summary>The storage is new (or one the GPU has finished with): no use of it waits for anything.</summary>
		private void ForgetUses()
		{
			state = default;
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

			state = new SyncState { Writes = Sync.Transfer, WriteAccess = VkAccessFlags2.TransferWriteBit };
			device.SyncEpoch++;
			if (hostVisible)
			{
				device.HostReads |= Sync.Transfer;
			}

			return commands;
		}
	}
}
