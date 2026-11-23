using System.Collections.Generic;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>Pools the descriptor cache may fill before it starts again.</summary>
		private const int MaxCachePools = 16;

		/// <summary>The pools the sets of <see cref="DescriptorCache" /> are in (the last one is the current one).</summary>
		private readonly List<VkDescriptorPool> cachePools = new List<VkDescriptorPool>();

		/// <summary>The keys of the cached sets that name a buffer, an image view, an acceleration structure or a set layout, by its handle.</summary>
		private readonly Dictionary<nint, List<DescriptorKey>> keysNaming = new Dictionary<nint, List<DescriptorKey>>();

		/// <summary>The buffers, views, structures and the set layout the key being built names (<see cref="CacheDescriptorSet" />).</summary>
		internal readonly List<nint> NamedHandles = new List<nint>();

		/// <summary>Keeps a set written for a key (a key of its own, not the program's buffer), noting the objects it names.</summary>
		internal void CacheDescriptorSet(in DescriptorKey key, VkDescriptorSet set)
		{
			DescriptorCache.Add(key, set);
			foreach (nint handle in NamedHandles)
			{
				if (!keysNaming.TryGetValue(handle, out List<DescriptorKey> keys))
				{
					keys = new List<DescriptorKey>();
					keysNaming.Add(handle, keys);
				}

				keys.Add(key);
			}

			NamedHandles.Clear();
		}

		/// <summary>
		///     An object is destroyed: the cached sets that name it are not looked up again (its handle may come back as
		///     another object's). Objects of kinds the cache does not note make it start again.
		/// </summary>
		private void ForgetDescriptorSetsNaming(RetiredKind kind, nint handle)
		{
			if (kind != RetiredKind.Buffer && kind != RetiredKind.ImageView && kind != RetiredKind.SetLayout && kind != RetiredKind.AccelerationStructure)
			{
				ForgetDescriptorSets();
				return;
			}

			if (keysNaming.Remove(handle, out List<DescriptorKey> keys))
			{
				foreach (DescriptorKey key in keys)
				{
					DescriptorCache.Remove(key);
				}
			}
		}

		/// <summary>The barriers of a command buffer sent aside (<see cref="BeginAside" />).</summary>
		internal readonly SyncBatch AsideBatch = new SyncBatch();

		/// <summary>
		///     Gives up the cached descriptor sets (a resource they name is destroyed and its handle may come back as
		///     another's, or their pools are full): their pools are reset once the submission being recorded, which may
		///     still bind them, has finished.
		/// </summary>
		internal void ForgetDescriptorSets()
		{
			DescriptorCacheResets++;
			DescriptorCache.Clear();
			keysNaming.Clear();
			recording.Pools.AddRange(cachePools);
			cachePools.Clear();
			currentPool = default;
		}

		/// <summary>The device is being given up: the descriptor cache's pools go.</summary>
		private void ReleaseDescriptorCache()
		{
			DescriptorCache.Clear();
			keysNaming.Clear();
			foreach (VkDescriptorPool pool in cachePools)
			{
				Vk->vkDestroyDescriptorPool.Invoke(Handle, pool, null);
			}

			cachePools.Clear();
			currentPool = default;
		}

		/// <summary>Whether destroying an object of a kind can leave a descriptor set naming a handle that may come back as another object's.</summary>
		private static bool NamedByDescriptorSets(RetiredKind kind)
		{
			switch (kind)
			{
				case RetiredKind.Image:
				case RetiredKind.Pipeline:
				case RetiredKind.ShaderModule:
				case RetiredKind.PipelineLayout:
				case RetiredKind.Swapchain:
				case RetiredKind.Semaphore:
					return false;
				default:
					return true;
			}
		}

		/// <summary>
		///     A command buffer of its own, for a readback of a resource the submission being recorded has not used: sent
		///     alone (<see cref="SubmitAsideAndWait" />), so that the CPU waits for the submissions sent before and the
		///     readback, not for what is being recorded.
		/// </summary>
		internal VkCommandBuffer BeginAside()
		{
			return BeginCommandBuffer();
		}

		/// <summary>Sends a command buffer of <see cref="BeginAside" /> and waits until the GPU has executed it, and with it everything sent before.</summary>
		internal void SubmitAsideAndWait(VkCommandBuffer commands)
		{
			Check(Vk->vkEndCommandBuffer.Invoke(commands), "vkEndCommandBuffer");
			VkCommandBufferSubmitInfo buffer = new VkCommandBufferSubmitInfo { CommandBuffer = commands };
			VkSubmitInfo2 submit = new VkSubmitInfo2 { CommandBufferInfoCount = 1, CommandBufferInfos = &buffer };
			VkFence fence;
			if (freeFences.Count > 0)
			{
				fence = freeFences.Pop();
			}
			else
			{
				VkFenceCreateInfo fenceInfo = new VkFenceCreateInfo();
				Check(Vk->vkCreateFence.Invoke(Handle, &fenceInfo, null, &fence), "vkCreateFence");
			}

			Check(Vk->vkQueueSubmit2.Invoke(Queue, 1, &submit, fence), "vkQueueSubmit2");
			Check(Vk->vkWaitForFences.Invoke(Handle, 1, &fence, true, nint.MaxValue), "vkWaitForFences");
			Vk->vkResetFences.Invoke(Handle, 1, &fence);
			freeFences.Push(fence);
			freeCommandBuffers.Push(commands);
			Poll();
		}
	}
}
