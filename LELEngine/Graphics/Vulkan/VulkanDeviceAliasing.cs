using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     A block of device memory textures are placed in at offsets (<see cref="VulkanDevice.CreateTransientTextures" />):
	///     freed when the last of them is disposed, once the GPU has finished with all of them.
	/// </summary>
	internal sealed class VulkanHeap
	{
		public VmaAllocation Allocation;
		public long Size;

		/// <summary>Textures placed in it that are not disposed yet.</summary>
		public int Alive;

		/// <summary>The last submission any of its textures was used by.</summary>
		public long LastUse;
	}

	/// <summary>
	///     Transient textures that share memory: textures whose lifetimes within a frame do not overlap are placed in one
	///     block, at offsets where they overlap only textures that live at other times. Each texture keeps the others whose
	///     memory overlaps its own (<see cref="VulkanTexture.Aliases" />); the first use of one after another of them was used
	///     takes the memory over (<see cref="VulkanTexture.TakeMemory" />): its contents are undefined, and that use waits for
	///     the others' last uses on both queues (their stages in its barrier, a wait for the other queue's submissions).
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		public override GpuTexture[] CreateTransientTextures(ReadOnlySpan<TextureDescriptor> descriptors, ReadOnlySpan<(int First, int Last)> lifetimes, out long bytes,
			out long unsharedBytes)
		{
			int count = descriptors.Length;
			VulkanTexture[] textures = new VulkanTexture[count];
			VkMemoryRequirements[] needs = new VkMemoryRequirements[count];
			unsharedBytes = 0;
			for (int i = 0; i < count; i++)
			{
				textures[i] = new VulkanTexture(this, descriptors[i], false, 0, true);
				VkMemoryRequirements need;
				Vk->vkGetImageMemoryRequirements.Invoke(Handle, textures[i].Image, &need);
				needs[i] = need;
				unsharedBytes += (long)need.Size.Value;
			}

			// One block per kind of memory the images can live in (render targets: usually one), largest textures first.
			bytes = 0;
			bool[] placed = new bool[count];
			long[] offsets = new long[count];
			List<int> group = new List<int>();
			for (int first = 0; first < count; first++)
			{
				if (placed[first])
				{
					continue;
				}

				int types = needs[first].MemoryTypeBits;
				group.Clear();
				for (int i = first; i < count; i++)
				{
					if (!placed[i] && needs[i].MemoryTypeBits == types)
					{
						group.Add(i);
						placed[i] = true;
					}
				}

				group.Sort((a, b) => ((long)needs[b].Size.Value).CompareTo((long)needs[a].Size.Value));
				long size = Pack(group, needs, lifetimes, offsets, out long alignment);
				VulkanHeap heap = AllocateHeap(size, alignment, types);
				bytes += size;
				foreach (int i in group)
				{
					textures[i].BindPlaced(heap, offsets[i], (long)needs[i].Size.Value);
				}

				// The textures whose memory overlaps: each takes it over from the others at its first use after them.
				foreach (int i in group)
				{
					List<VulkanTexture> aliases = null;
					foreach (int j in group)
					{
						if (i != j && offsets[i] < offsets[j] + (long)needs[j].Size.Value && offsets[j] < offsets[i] + (long)needs[i].Size.Value)
						{
							(aliases ??= new List<VulkanTexture>()).Add(textures[j]);
						}
					}

					textures[i].Aliases = aliases?.ToArray();
				}
			}

			return textures;
		}

		/// <summary>
		///     Offsets for the textures of a group (largest first): each at the lowest offset, aligned, where its memory meets
		///     no texture placed before whose lifetime overlaps its own. The block's size and alignment.
		/// </summary>
		private static long Pack(List<int> group, VkMemoryRequirements[] needs, ReadOnlySpan<(int First, int Last)> lifetimes, long[] offsets, out long alignment)
		{
			long size = 0;
			alignment = 1;
			List<int> done = new List<int>();
			List<(long Start, long End)> taken = new List<(long Start, long End)>();
			foreach (int i in group)
			{
				long need = (long)needs[i].Size.Value;
				long align = Math.Max(1, (long)needs[i].Alignment.Value);
				alignment = Math.Max(alignment, align);
				taken.Clear();
				foreach (int j in done)
				{
					if (lifetimes[i].First <= lifetimes[j].Last && lifetimes[j].First <= lifetimes[i].Last)
					{
						taken.Add((offsets[j], offsets[j] + (long)needs[j].Size.Value));
					}
				}

				taken.Sort((a, b) => a.Start.CompareTo(b.Start));
				long offset = 0;
				foreach ((long start, long end) in taken)
				{
					if (offset + need <= start)
					{
						break;
					}

					offset = Math.Max(offset, (end + align - 1) / align * align);
				}

				offsets[i] = offset;
				size = Math.Max(size, offset + need);
				done.Add(i);
			}

			return size;
		}

		/// <summary>A block of device-local memory for placed textures (one allocation of its own).</summary>
		private VulkanHeap AllocateHeap(long size, long alignment, int types)
		{
			VkMemoryRequirements need = new VkMemoryRequirements { Size = (Brutal.ByteSize64)(ulong)size, Alignment = (Brutal.ByteSize64)(ulong)alignment, MemoryTypeBits = types };
			VmaAllocationCreateInfo info = new VmaAllocationCreateInfo
			{
				Flags = VmaAllocationCreateFlags.DedicatedMemoryBit,
				RequiredFlags = VkMemoryPropertyFlags.DeviceLocalBit
			};
			VmaAllocation allocation;
			VkResult result = Vma.PInvoke.vmaAllocateMemory(Allocator, &need, &info, &allocation, null);
			if (result != VkResult.Success)
			{
				// No device-local type among those the images take: any of them.
				info.RequiredFlags = default;
				Check(Vma.PInvoke.vmaAllocateMemory(Allocator, &need, &info, &allocation, null), "vmaAllocateMemory (transient textures)");
			}

			return new VulkanHeap { Allocation = allocation, Size = size };
		}

		/// <summary>The last placed texture of a heap was disposed: its memory goes once the GPU has finished with all of them.</summary>
		internal void ReleaseHeap(VulkanHeap heap)
		{
			Retire(RetiredKind.Memory, heap.Allocation.VkHandle, heap.Allocation.VkHandle, heap.LastUse);
		}
	}

	internal sealed unsafe partial class VulkanTexture
	{
		/// <summary>The block the texture is placed in (null: memory of its own), and where.</summary>
		private VulkanHeap heap;

		/// <summary>
		///     The textures whose memory overlaps this one's (placed in the same block for other times of the frame); null when
		///     none does.
		/// </summary>
		internal VulkanTexture[] Aliases;

		/// <summary>No texture of <see cref="Aliases" /> was used since this one last was: its memory holds its contents.</summary>
		private bool ownsMemory;

		/// <summary>Binds the image into a block at an offset (a texture made with placed set: <see cref="VulkanDevice.CreateTransientTextures" />).</summary>
		internal void BindPlaced(VulkanHeap block, long offset, long size)
		{
			VulkanDevice.Check(Vma.PInvoke.vmaBindImageMemory2(device.Allocator, block.Allocation, (Brutal.ByteSize64)(ulong)offset, Image, null), "vmaBindImageMemory2 (" + Name + ")");
			heap = block;
			block.Alive++;
		}

		/// <summary>The texture is disposed: its block is freed with the last of its textures.</summary>
		private void ReleasePlacement()
		{
			if (heap == null)
			{
				return;
			}

			heap.LastUse = Math.Max(heap.LastUse, LastUse);
			if (--heap.Alive == 0)
			{
				device.ReleaseHeap(heap);
			}

			heap = null;
		}

		/// <summary>
		///     Whether a use of the texture may be ordered ahead of the stream (in the submission's setup command buffer): the
		///     stream has not used it in the submission being recorded, and no texture sharing its memory was used since it was
		///     (its memory is taken over in the stream, after their uses there).
		/// </summary>
		internal bool StreamUnused => MainUse != device.RecordingSerial && (Aliases == null || ownsMemory);

		/// <summary>Whether the texture's next use takes its memory over from another texture placed over it.</summary>
		internal bool TakesMemory => Aliases != null && !ownsMemory;

		/// <summary>
		///     The first use after a texture sharing the memory: what the memory held is left behind (the layout is undefined),
		///     and the use waits for the other textures' last uses, in each queue's barrier (their stages and writes) and
		///     across the queues (their submissions).
		/// </summary>
		internal void TakeMemory()
		{
			// Its own last uses too: the others' barriers ordered them before theirs, but only over the memory each of them
			// covers.
			SyncState graphics = new SyncState();
			SyncState compute = new SyncState();
			Gather(ref graphics, wholeState, subresourceStates);
			if (computeStates)
			{
				Gather(ref compute, computeWholeState, computeSubresourceStates);
			}

			bool computeUsed = computeStates;
			QueueUses uses = queueUses;
			foreach (VulkanTexture other in Aliases)
			{
				Gather(ref graphics, other.wholeState, other.subresourceStates);
				if (other.computeStates)
				{
					Gather(ref compute, other.computeWholeState, other.computeSubresourceStates);
					computeUsed = true;
				}

				uses.GraphicsRead = Math.Max(uses.GraphicsRead, other.queueUses.GraphicsRead);
				uses.GraphicsWrite = Math.Max(uses.GraphicsWrite, other.queueUses.GraphicsWrite);
				uses.ComputeRead = Math.Max(uses.ComputeRead, other.queueUses.ComputeRead);
				uses.ComputeWrite = Math.Max(uses.ComputeWrite, other.queueUses.ComputeWrite);
				other.ownsMemory = false;
			}

			wholeState = graphics;
			subresourceStates = null;
			computeWholeState = compute;
			computeSubresourceStates = null;
			computeStates = computeUsed;
			queueUses = uses;
			ownsMemory = true;
			device.SyncEpoch++;
		}

		/// <summary>Adds a texture's last uses (whole or by subresource) to the state a taking over use waits for.</summary>
		private static void Gather(ref SyncState into, in SyncState whole, SyncState[] parts)
		{
			if (parts == null)
			{
				Gather(ref into, whole);
				return;
			}

			foreach (SyncState part in parts)
			{
				Gather(ref into, part);
			}
		}

		private static void Gather(ref SyncState into, in SyncState state)
		{
			into.Writes |= state.Writes | state.Reads;
			into.WriteAccess |= state.WriteAccess;
			into.AliasedMemory |= (state.Writes | state.Reads) != VkPipelineStageFlags2.None;
		}
	}
}
