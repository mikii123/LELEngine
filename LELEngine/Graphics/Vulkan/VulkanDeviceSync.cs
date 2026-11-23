using System;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace LELEngine.Graphics.Vulkan
{
	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>
		///     Counts the changes of resources' states that can give a use a barrier to wait for (writes, layout changes),
		///     new passes and new submissions: a program whose resources were looked at in the same count needs no look
		///     again for its next draw.
		/// </summary>
		internal int SyncEpoch;

		/// <summary>
		///     Stages that wrote buffers the CPU reads (mapped ones): their writes are made visible to the host at the end
		///     of the submission.
		/// </summary>
		internal VkPipelineStageFlags2 HostReads;

		/// <summary>The barriers of the command buffer that runs before the stream's (<see cref="SetupCommands" />).</summary>
		internal readonly SyncBatch SetupBatch = new SyncBatch();

		/// <summary>A command was recorded ahead of the stream outside the tracking of uses (<see cref="BeginSetupCommand" />).</summary>
		private bool setupUntracked;

		/// <summary>Barriers recorded (both command buffers), and renderings ended for one in the middle of a pass: measured with LEL_VULKAN_STATS=1.</summary>
		internal long BarriersRecorded;

		internal long RenderingSuspensions;

		/// <summary>Descriptor sets written.</summary>
		internal long DescriptorSetsWritten;

		/// <summary>Times the descriptor cache started again (<see cref="ForgetDescriptorSets" />).</summary>
		internal long DescriptorCacheResets;

		private static readonly bool printStats = Environment.GetEnvironmentVariable("LEL_VULKAN_STATS") is "1" or "2";

		/// <summary>LEL_VULKAN_STATS=2: the stats also name what the renderings suspended in a pass waited for, by program.</summary>
		internal static readonly bool TraceSuspensions = Environment.GetEnvironmentVariable("LEL_VULKAN_STATS") == "2";

		private readonly System.Collections.Generic.Dictionary<string, int> suspensions = new System.Collections.Generic.Dictionary<string, int>();

		internal void NoteSuspension(string reason)
		{
			suspensions.TryGetValue(reason, out int count);
			suspensions[reason] = count + 1;
		}
		private long statsFrames;
		private long statsStart;
		private long statsBarriers;
		private long statsSuspensions;
		private long statsSets;
		private long statsResets;

		/// <summary>A frame was presented: with LEL_VULKAN_STATS=1 the counts of the last second are printed once a second.</summary>
		internal void CountFrame()
		{
			if (!printStats)
			{
				return;
			}

			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			if (statsStart == 0)
			{
				statsStart = now;
			}

			statsFrames++;
			double seconds = (now - statsStart) / (double)System.Diagnostics.Stopwatch.Frequency;
			if (seconds < 1.0)
			{
				return;
			}

			Console.WriteLine(
				$"[Vulkan stats] per frame: barriers {(BarriersRecorded - statsBarriers) / (double)statsFrames:0.0}, renderings suspended for a barrier {(RenderingSuspensions - statsSuspensions) / (double)statsFrames:0.0}, descriptor sets written {(DescriptorSetsWritten - statsSets) / (double)statsFrames:0.0}, descriptor cache restarts {(DescriptorCacheResets - statsResets) / (double)statsFrames:0.00}; {MemoryStatistics()}");
			if (suspensions.Count > 0)
			{
				var reasons = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(suspensions);
				reasons.Sort((a, b) => b.Value.CompareTo(a.Value));
				for (int i = 0; i < Math.Min(8, reasons.Count); i++)
				{
					Console.WriteLine($"[Vulkan stats]   suspended {reasons[i].Value / (double)statsFrames:0.0} a frame: {reasons[i].Key}");
				}

				suspensions.Clear();
			}

			statsStart = now;
			statsFrames = 0;
			statsBarriers = BarriersRecorded;
			statsSuspensions = RenderingSuspensions;
			statsSets = DescriptorSetsWritten;
			statsResets = DescriptorCacheResets;
		}

		/// <summary>"memory 812 MB in 14 blocks (790 MB allocated) of 8270 MB the system gives, uploads in flight 3 MB" (VMA's heaps together).</summary>
		internal string MemoryStatistics()
		{
			VmaBudget* budgets = stackalloc VmaBudget[16];
			Vma.PInvoke.vmaGetHeapBudgets(Allocator, budgets);
			int heaps = (int)Math.Min(16u, (uint)memoryHeapCount);
			ulong blocks = 0;
			ulong blockBytes = 0;
			ulong allocationBytes = 0;
			ulong budget = 0;
			for (int i = 0; i < heaps; i++)
			{
				blocks += budgets[i].Statistics.BlockCount;
				blockBytes += budgets[i].Statistics.BlockBytes.Value;
				allocationBytes += budgets[i].Statistics.AllocationBytes.Value;
				budget += budgets[i].Budget.Value;
			}

			const double MB = 1 << 20;
			return $"memory {blockBytes / MB:0} MB in {blocks} blocks ({allocationBytes / MB:0} MB allocated) of {budget / MB:0} MB the system gives, uploads in flight {(recordingMemory + pendingMemory) / MB:0} MB";
		}
	}
}
