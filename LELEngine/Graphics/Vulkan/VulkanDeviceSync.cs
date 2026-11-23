using System;
using Brutal.VulkanApi;

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

		private static readonly bool printStats = Environment.GetEnvironmentVariable("LEL_VULKAN_STATS") == "1";
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
				$"[Vulkan stats] per frame: barriers {(BarriersRecorded - statsBarriers) / (double)statsFrames:0.0}, renderings suspended for a barrier {(RenderingSuspensions - statsSuspensions) / (double)statsFrames:0.0}, descriptor sets written {(DescriptorSetsWritten - statsSets) / (double)statsFrames:0.0}, descriptor cache restarts {(DescriptorCacheResets - statsResets) / (double)statsFrames:0.00}");
			statsStart = now;
			statsFrames = 0;
			statsBarriers = BarriersRecorded;
			statsSuspensions = RenderingSuspensions;
			statsSets = DescriptorSetsWritten;
			statsResets = DescriptorCacheResets;
		}
	}
}
