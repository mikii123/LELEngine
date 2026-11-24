using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The command list's side of the device's two queues (VulkanDeviceQueues.cs): between
	///     <see cref="BeginAsyncCompute" /> and <see cref="EndAsyncCompute" /> each command goes to the compute queue when
	///     it can run there (dispatches, copies, colour clears, uploads, acceleration structure builds, timestamps, of
	///     resources shared with that queue), and to the graphics queue otherwise; outside, everything goes to the graphics
	///     queue. The order of their uses of resources is kept by the device whatever the queue.
	/// </summary>
	internal sealed unsafe partial class VulkanCommandList
	{
		private bool asyncCompute;

		/// <summary>What each queue's command buffer was last told of compute work.</summary>
		private readonly ComputeBindings[] computeBindings = { new ComputeBindings(), new ComputeBindings() };

		public override bool InAsyncCompute => asyncCompute;

		public override void BeginAsyncCompute()
		{
			if (asyncCompute)
			{
				throw new InvalidOperationException("BeginAsyncCompute inside asynchronous compute work: it is not nested.");
			}

			asyncCompute = device.HasAsyncCompute;
			if (asyncCompute)
			{
				device.ForkCompute();
			}
		}

		public override void EndAsyncCompute()
		{
			if (!asyncCompute)
			{
				return;
			}

			asyncCompute = false;
			UseGraphicsQueue();
			// Sent now: the compute queue starts on it while the work after it is recorded.
			device.SubmitCompute();
		}

		/// <summary>The commands that follow are the graphics queue's.</summary>
		internal void UseGraphicsQueue()
		{
			device.Record(device.GraphicsStream);
		}

		/// <summary>
		///     The next command goes to the compute queue when asynchronous compute work is being given and it can run there
		///     (<paramref name="possible" />), else to the graphics queue; true for the compute queue.
		/// </summary>
		private bool UseComputeQueue(bool possible)
		{
			bool compute = asyncCompute && possible;
			device.Record(compute ? device.ComputeStream : device.GraphicsStream);
			return compute;
		}

		/// <summary>Whether a program's descriptor set binds only textures the compute queue may use (no depth texture, nothing the graphics queue keeps).</summary>
		private static bool ComputeCanBind(VulkanProgram program)
		{
			for (int i = 0; i < program.TrackedCount; i++)
			{
				VulkanTexture texture = program.Tracked[i].Texture;
				if (texture != null && !texture.Concurrent)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		///     A dispatch given in asynchronous compute work runs on the graphics queue (its program binds a texture only that
		///     queue uses): counted for LEL_VULKAN_STATS=1, and with LEL_VULKAN_TRACE_QUEUES=1 printed with the texture.
		/// </summary>
		private void NoteComputeFallback(VulkanProgram program)
		{
			device.ComputeFallbacks++;
			if (!VulkanDevice.TraceQueues)
			{
				return;
			}

			for (int i = 0; i < program.TrackedCount; i++)
			{
				VulkanTexture texture = program.Tracked[i].Texture;
				if (texture != null && !texture.Concurrent)
				{
					Console.WriteLine($"[queues] a dispatch of asynchronous compute work runs on the graphics queue: it binds texture '{texture.Name}' ({texture.Format}, {texture.Usage})");
					return;
				}
			}
		}

		/// <summary>Runs a command of the device's own (a stand-in's clear, an empty top level's build) on the graphics queue, whatever queue is being recorded for.</summary>
		private void OnGraphicsQueue(Action record)
		{
			VulkanStream before = device.Recording;
			UseGraphicsQueue();
			try
			{
				record();
			}
			finally
			{
				device.Record(before);
			}
		}

		/// <summary>The compute pipeline, descriptor set and offsets a queue's command buffer has bound.</summary>
		private sealed class ComputeBindings
		{
			public VkPipeline Pipeline;
			public VkDescriptorSet Set;
			public VulkanProgram Program;
			public readonly int[] Offsets = new int[8];

			public void Forget()
			{
				Pipeline = default;
				Set = default;
				Program = null;
			}
		}
	}
}
