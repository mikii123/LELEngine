using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     Timestamps without a call into the driver per stamp: on AMD's driver a host reset of a query and a read of a
	///     query's result cost a microsecond or two each (per query, also when one call reads many), and a frame's
	///     profiler writes and reads dozens. Queries given back are reset together, a call per run of neighbours, when
	///     the free ones run out; the GPU copies the values of a submission's queries into mapped memory at its end,
	///     where a stamp reads its value once the submission has finished.
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>Queries given back whose last write has run, not reset yet (<see cref="ResetReleasedQueries" />).</summary>
		private readonly List<int> releasedQueries = new List<int>();

		/// <summary>The submission (serial) that writes each query, since it was last taken.</summary>
		private long[] queryWrites = Array.Empty<long>();

		/// <summary>Per pool, the memory its values are copied into: a 64-bit value per query, mapped.</summary>
		private readonly List<QueryValues> queryValues = new List<QueryValues>();

		private struct QueryValues
		{
			public VkBuffer Buffer;
			public VmaAllocation Allocation;
			public ulong* Values;
		}

		/// <summary>The submission being recorded for the queue being recorded for wrote timestamps: their values are copied at its end.</summary>
		internal bool HasWrittenQueries => recordingStream.WrittenQueries.Count > 0;

		/// <summary>The nanoseconds a query holds from the write of a submission that has finished (0 when it never ran).</summary>
		internal long ReadQuery(int query, long serial)
		{
			if (queryWrites[query] != serial)
			{
				return 0;
			}

			return (long)(queryValues[query / QueriesPerPool].Values[query % QueriesPerPool] * (double)TimestampPeriod);
		}

		/// <summary>
		///     Records the copies of the values of the queries the submission of the queue being recorded for wrote into
		///     their pools' memory, a copy per run of neighbours (outside rendering, at the submission's end).
		/// </summary>
		internal void CopyQueryResults(VkCommandBuffer commands)
		{
			List<int> writtenQueries = recordingStream.WrittenQueries;
			writtenQueries.Sort();
			int start = 0;
			for (int i = 1; i <= writtenQueries.Count; i++)
			{
				if (i < writtenQueries.Count && writtenQueries[i] == writtenQueries[i - 1] + 1 && writtenQueries[i] % QueriesPerPool != 0)
				{
					continue;
				}

				int first = writtenQueries[start];
				int index = first % QueriesPerPool;
				Vk->vkCmdCopyQueryPoolResults.Invoke(commands, queryPools[first / QueriesPerPool], index, i - start, queryValues[first / QueriesPerPool].Buffer,
					(Brutal.ByteSize64)(ulong)(index * sizeof(ulong)), (Brutal.ByteSize64)(ulong)sizeof(ulong), VkQueryResultFlags._64Bit | VkQueryResultFlags.WaitBit);
				start = i;
			}

			writtenQueries.Clear();
		}

		/// <summary>A new pool's memory for its values, and room in the per-query table.</summary>
		private void AddQueryValues()
		{
			VkBufferCreateInfo info = new VkBufferCreateInfo { Size = (Brutal.ByteSize64)(ulong)(QueriesPerPool * sizeof(ulong)), Usage = VkBufferUsageFlags.TransferDstBit };
			int* families = stackalloc int[2];
			ShareBuffer(ref info, families);
			VmaAllocationCreateInfo allocationInfo = new VmaAllocationCreateInfo
			{
				Usage = VmaMemoryUsage.Auto,
				Flags = VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.HostAccessRandomBit,
				RequiredFlags = VkMemoryPropertyFlags.HostCoherentBit
			};
			VkBuffer buffer;
			VmaAllocation allocation;
			VmaAllocationInfo made;
			Check(Vma.PInvoke.vmaCreateBuffer(Allocator, &info, &allocationInfo, &buffer, &allocation, &made), "vmaCreateBuffer (timestamps)");
			queryValues.Add(new QueryValues { Buffer = buffer, Allocation = allocation, Values = (ulong*)(void*)made.PMappedData });
			Array.Resize(ref queryWrites, queryPools.Count * QueriesPerPool);
		}

		/// <summary>Resets the queries given back since the last time, a call per run of neighbours in a pool, and makes them free.</summary>
		private void ResetReleasedQueries()
		{
			releasedQueries.Sort();
			int start = 0;
			for (int i = 1; i <= releasedQueries.Count; i++)
			{
				if (i < releasedQueries.Count && releasedQueries[i] == releasedQueries[i - 1] + 1 && releasedQueries[i] % QueriesPerPool != 0)
				{
					continue;
				}

				int first = releasedQueries[start];
				Vk->vkResetQueryPool.Invoke(Handle, queryPools[first / QueriesPerPool], first % QueriesPerPool, i - start);
				start = i;
			}

			// Taken lowest first: the queries of a frame stay neighbours.
			for (int i = releasedQueries.Count - 1; i >= 0; i--)
			{
				freeQueries.Push(releasedQueries[i]);
			}

			releasedQueries.Clear();
		}

		/// <summary>The device is being given up: the pools' memory goes.</summary>
		private void ReleaseQueryValues()
		{
			foreach (QueryValues values in queryValues)
			{
				Vma.PInvoke.vmaDestroyBuffer(Allocator, values.Buffer, values.Allocation);
			}

			queryValues.Clear();
			releasedQueries.Clear();
			graphicsStream?.WrittenQueries.Clear();
			computeStream?.WrittenQueries.Clear();
		}
	}
}
