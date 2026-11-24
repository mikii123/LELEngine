using System.Collections.Generic;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     A command pool of a queue's family and the command buffers made from it. A submission's command buffers come
	///     from pools of its own (one per queue and per thread that records them), which are reset whole once the GPU has
	///     run it: one reset of a pool for all its buffers, where a pool of resettable buffers resets each in its
	///     <c>vkBeginCommandBuffer</c>. The buffers stay with the pool and are begun again after the reset.
	/// </summary>
	internal sealed class VulkanCommandPool
	{
		public readonly VkCommandPool Handle;
		public readonly VulkanStream Stream;
		private readonly List<VkCommandBuffer> buffers = new List<VkCommandBuffer>();
		private int used;

		public VulkanCommandPool(VkCommandPool handle, VulkanStream stream)
		{
			Handle = handle;
			Stream = stream;
		}

		/// <summary>A command buffer of the pool in the initial state (made when the pool has none left).</summary>
		public unsafe VkCommandBuffer Take(VulkanDevice device)
		{
			if (used == buffers.Count)
			{
				buffers.Add(device.AllocateCommandBuffer(Handle));
			}

			return buffers[used++];
		}

		/// <summary>The GPU has run every buffer taken: they are all reset, and taken again from the first.</summary>
		public unsafe void Reset(VulkanDevice device)
		{
			if (used > 0)
			{
				VulkanDevice.Check(device.Vk->vkResetCommandPool.Invoke(device.Handle, Handle, default), "vkResetCommandPool");
			}

			used = 0;
		}
	}

	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>Reset pools of each queue's family, by stream (<see cref="VulkanStream.Index" />).</summary>
		private readonly Stack<VulkanCommandPool>[] freeCommandPools = { new Stack<VulkanCommandPool>(), new Stack<VulkanCommandPool>() };

		/// <summary>Every pool made, for the device's end.</summary>
		private readonly List<VulkanCommandPool> allCommandPools = new List<VulkanCommandPool>();

		/// <summary>The pool the recording thread's command buffers of the submission being recorded come from, by stream.</summary>
		private readonly VulkanCommandPool[] submissionPools = new VulkanCommandPool[2];

		/// <summary>A reset pool of a stream's queue family (made when none is free).</summary>
		internal VulkanCommandPool TakeCommandPool(VulkanStream stream)
		{
			Stack<VulkanCommandPool> free = freeCommandPools[stream.Index];
			if (free.Count > 0)
			{
				return free.Pop();
			}

			// Transient: each buffer lives for one submission; no flag for resetting buffers one by one.
			VkCommandPoolCreateInfo info = new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlags.TransientBit, QueueFamilyIndex = stream.Family };
			VkCommandPool handle;
			Check(Vk->vkCreateCommandPool.Invoke(Handle, &info, null, &handle), "vkCreateCommandPool");
			VulkanCommandPool pool = new VulkanCommandPool(handle, stream);
			allCommandPools.Add(pool);
			return pool;
		}

		/// <summary>A new primary command buffer of a pool.</summary>
		internal VkCommandBuffer AllocateCommandBuffer(VkCommandPool pool)
		{
			return Brutal.VulkanApi.Abstractions.DeviceExtensions.AllocateCommandBuffer(device, new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }).Handle;
		}

		/// <summary>A command buffer of the submission being recorded for a stream, from the pool of the thread that records the device's commands.</summary>
		private VkCommandBuffer TakeSubmissionCommandBuffer(VulkanStream stream)
		{
			VulkanCommandPool pool = submissionPools[stream.Index] ??= TakeCommandPool(stream);
			return pool.Take(this);
		}

		/// <summary>The stream's command buffers are being sent: their pools are the submission's, reset when it has run.</summary>
		private void HandOverCommandPools(VulkanStream stream, Submission owner)
		{
			VulkanCommandPool pool = submissionPools[stream.Index];
			if (pool != null)
			{
				owner.CommandPools.Add(pool);
				submissionPools[stream.Index] = null;
			}
		}

		/// <summary>A submission has run: its pools are reset and free again.</summary>
		private void ResetCommandPools(Submission submission)
		{
			foreach (VulkanCommandPool pool in submission.CommandPools)
			{
				pool.Reset(this);
				freeCommandPools[pool.Stream.Index].Push(pool);
			}

			submission.CommandPools.Clear();
		}

		/// <summary>The device is being given up (the GPU idle): every pool goes, with its buffers.</summary>
		private void ReleaseCommandPools()
		{
			foreach (VulkanCommandPool pool in allCommandPools)
			{
				Vk->vkDestroyCommandPool.Invoke(Handle, pool.Handle, null);
			}

			allCommandPools.Clear();
			freeCommandPools[0].Clear();
			freeCommandPools[1].Clear();
			submissionPools[0] = null;
			submissionPools[1] = null;
		}
	}
}
