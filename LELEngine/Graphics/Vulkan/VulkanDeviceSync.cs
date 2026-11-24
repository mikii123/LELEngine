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
		///     Stages that wrote buffers the CPU reads (mapped ones), on the queue being recorded for: their writes are
		///     made visible to the host at the end of its submission.
		/// </summary>
		internal VkPipelineStageFlags2 HostReads
		{
			get => recordingStream.HostReads;
			set => recordingStream.HostReads = value;
		}

		/// <summary>The barriers of the command buffer that runs before the stream's (<see cref="SetupCommands" />), of the queue being recorded for.</summary>
		internal SyncBatch SetupBatch => recordingStream.SetupBatch;

		/// <summary>Barriers recorded (both command buffers), and renderings ended for one in the middle of a pass: measured with LEL_VULKAN_STATS=1.</summary>
		internal long BarriersRecorded;

		internal long RenderingSuspensions;

		/// <summary>Descriptor sets written.</summary>
		internal long DescriptorSetsWritten;

		/// <summary>Times the descriptor cache started again (<see cref="ForgetDescriptorSets" />).</summary>
		internal long DescriptorCacheResets;

		private static readonly bool printStats = Environment.GetEnvironmentVariable("LEL_VULKAN_STATS") == "1";

		/// <summary>LEL_VULKAN_MEMORY=1 (with LEL_VULKAN_STATS=1): the textures alive after five seconds, by size, once.</summary>
		private static readonly bool printMemory = Environment.GetEnvironmentVariable("LEL_VULKAN_MEMORY") == "1";

		private int memoryReports;

		/// <summary>The textures alive by their texels' bytes (every level and layer): the largest, and the totals by what they are used for.</summary>
		private static void PrintTextures()
		{
			System.Collections.Generic.List<GpuTexture> alive = GpuTexture.Alive();
			System.Collections.Generic.List<(long Bytes, GpuTexture Texture)> sized = new System.Collections.Generic.List<(long Bytes, GpuTexture Texture)>();
			long attachments = 0;
			long storage = 0;
			long sampled = 0;
			foreach (GpuTexture texture in alive)
			{
				long bytes = 0;
				for (int mip = 0; mip < texture.MipLevels; mip++)
				{
					bytes += texture.MipDataSize(mip);
				}

				sized.Add((bytes, texture));
				if ((texture.Usage & TextureUsage.Attachment) != 0)
				{
					attachments += bytes;
				}
				else if ((texture.Usage & TextureUsage.Storage) != 0)
				{
					storage += bytes;
				}
				else
				{
					sampled += bytes;
				}
			}

			sized.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
			Console.WriteLine($"[Vulkan memory] {alive.Count} textures: render targets {attachments / 1048576.0:0.0} MB, storage images {storage / 1048576.0:0.0} MB, sampled only {sampled / 1048576.0:0.0} MB");
			for (int i = 0; i < Math.Min(30, sized.Count); i++)
			{
				GpuTexture texture = sized[i].Texture;
				Console.WriteLine($"[Vulkan memory]   {sized[i].Bytes / 1048576.0,7:0.00} MB  {texture.Name ?? "(unnamed)"}  {texture.Kind} {texture.Format} {texture.Width}x{texture.Height}x{texture.Depth} mips {texture.MipLevels} {texture.Usage}");
			}
		}
		private long statsFrames;
		private long statsStart;
		private long statsBarriers;
		private long statsSuspensions;
		private long statsSets;
		private long statsResets;
		private long statsWaits;
		private long statsComputeSubmissions;
		private long statsRecording;
		private long statsRecorderWait;
		private long statsBuffers;

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

			Brutal.VulkanApi.Vma.VmaTotalStatistics memory;
			Brutal.VulkanApi.Vma.Vma.PInvoke.vmaCalculateStatistics(Allocator, &memory);
			long recordingTicks = graphicsStream.Recorder.RecordingTicks + (computeStream?.Recorder.RecordingTicks ?? 0);
			long recorderWaitTicks = graphicsStream.Recorder.WaitTicks + (computeStream?.Recorder.WaitTicks ?? 0);
			double perFrameMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency / statsFrames;
			Console.WriteLine(
				$"[Vulkan stats] per frame: barriers {(BarriersRecorded - statsBarriers) / (double)statsFrames:0.0}, renderings suspended for a barrier {(RenderingSuspensions - statsSuspensions) / (double)statsFrames:0.0}, descriptor sets written {(DescriptorSetsWritten - statsSets) / (double)statsFrames:0.0}, descriptor cache restarts {(DescriptorCacheResets - statsResets) / (double)statsFrames:0.00}, compute queue submissions {(ComputeSubmissions - statsComputeSubmissions) / (double)statsFrames:0.0}, waits between the queues {(CrossQueueWaits - statsWaits) / (double)statsFrames:0.0}, async dispatches on the graphics queue {(ComputeFallbacks - statsFallbacks) / (double)statsFrames:0.0}, command buffers {(CommandBuffersSent - statsBuffers) / (double)statsFrames:0.0}, recording thread {(recordingTicks - statsRecording) * perFrameMs:0.000} ms (waited for {(recorderWaitTicks - statsRecorderWait) * perFrameMs:0.000} ms) | memory: {(double)(ulong)memory.Total.Statistics.AllocationBytes / (1 << 20):0.0} MB in {memory.Total.Statistics.AllocationCount} allocations, {(double)(ulong)memory.Total.Statistics.BlockBytes / (1 << 20):0.0} MB of blocks");
			statsRecording = recordingTicks;
			statsRecorderWait = recorderWaitTicks;
			statsBuffers = CommandBuffersSent;
			if (printMemory && ++memoryReports == 5)
			{
				PrintTextures();
			}

			statsStart = now;
			statsFrames = 0;
			statsBarriers = BarriersRecorded;
			statsSuspensions = RenderingSuspensions;
			statsSets = DescriptorSetsWritten;
			statsResets = DescriptorCacheResets;
			statsWaits = CrossQueueWaits;
			statsComputeSubmissions = ComputeSubmissions;
			statsFallbacks = ComputeFallbacks;
		}

		private long statsFallbacks;
	}
}
