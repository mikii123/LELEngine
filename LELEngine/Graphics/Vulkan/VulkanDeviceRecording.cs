using System.Collections.Generic;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The device's side of the recording threads (VulkanRecorder.cs): each stream's command buffers of a submission
	///     are the recorder's segments, recorded on its thread, and the device's own (<see cref="MainCommands" />),
	///     recorded where the commands are given, in the order the commands came.
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>The recorder of the stream being recorded for.</summary>
		internal VulkanRecorder Recorder => recordingStream.Recorder;

		/// <summary>Command buffers of the streams (not their setup buffers) sent: measured with LEL_VULKAN_STATS=1.</summary>
		internal long CommandBuffersSent;

		/// <summary>
		///     Opens a segment of the stream's recording thread for the commands written from now on, after the stream's own
		///     command buffer (which ends); the command list binds everything anew in it. False when the stream's commands
		///     are recorded where they are given (no recording thread, or the submission has as many segments as it takes).
		/// </summary>
		internal bool OpenRecordedSegment(VulkanStream stream)
		{
			VulkanRecorder recorder = stream.Recorder;
			if (!VulkanRecorder.Threaded || recorder.SegmentCount >= VulkanRecorder.MaxSegments)
			{
				return false;
			}

			if (stream.MainBegun)
			{
				EndDirectCommands(stream);
			}

			CommandList.ForgetBindings(stream);
			recorder.Open();
			return true;
		}

		/// <summary>
		///     The stream's command buffer the device records into where the commands are given: begun after the recording
		///     thread's open segment, which ends (rendering is suspended in it first).
		/// </summary>
		internal VkCommandBuffer DirectCommands(VulkanStream stream)
		{
			if (!stream.MainBegun)
			{
				VulkanRecorder recorder = stream.Recorder;
				if (recorder.IsOpen)
				{
					if (stream.Index == 0)
					{
						CommandList.SuspendRenderingForSegment();
					}

					recorder.Close();
					CommandList.ForgetBindings(stream);
				}

				stream.Main = BeginCommandBuffer(stream);
				stream.MainBegun = true;
			}

			return stream.Main;
		}

		/// <summary>Ends the stream's own command buffer: it takes its place in the sequence of the submission's segments.</summary>
		private void EndDirectCommands(VulkanStream stream)
		{
			Check(Vk->vkEndCommandBuffer.Invoke(stream.Main), "vkEndCommandBuffer");
			stream.Recorder.AddRecordedHere(stream.Main);
			stream.MainBegun = false;
		}

		/// <summary>
		///     The stream's command buffers after its setup buffer, in order, for the submission being sent: what the
		///     recording thread has still to record is waited for. Returns how many were written into <paramref name="buffers" />.
		/// </summary>
		private int EndStreamCommands(VulkanStream stream, VkCommandBufferSubmitInfo* buffers, List<VkCommandBuffer> owner)
		{
			if (stream.MainBegun)
			{
				EndDirectCommands(stream);
			}

			int count = stream.Recorder.Finish(buffers, out VulkanCommandPool pool);
			if (pool != null)
			{
				recording.CommandPools.Add(pool);
			}

			CommandBuffersSent += count;

			for (int i = 0; i < count; i++)
			{
				owner.Add(buffers[i].CommandBuffer);
			}

			return count;
		}

		/// <summary>The recorders of the streams are given up (their threads end); the GPU is idle.</summary>
		private void ReleaseRecorders()
		{
			graphicsStream?.Recorder?.Dispose();
			computeStream?.Recorder?.Dispose();
		}
	}
}
