using System;
using System.Collections.Generic;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The recording of one of the device's queues: the command buffers of the queue's submission being recorded (one
	///     that runs ahead of the stream, for what the stream has not used yet, and the stream's own), what they need at
	///     their end, and the queue's timeline: each submission of a queue signals the next value of the queue's timeline
	///     semaphore, and a submission of the other queue that needs its work waits for that value.
	/// </summary>
	internal sealed class VulkanStream
	{
		/// <summary>0: the graphics queue (draws, presents, everything on a device without a compute queue), 1: the compute queue.</summary>
		public readonly int Index;

		public readonly VkQueue Queue;
		public readonly int Family;
		public readonly VkCommandPool Pool;

		/// <summary>Command buffers of the pool whose submissions have finished.</summary>
		public readonly Stack<VkCommandBuffer> Free = new Stack<VkCommandBuffer>();

		public VkCommandBuffer Main;
		public VkCommandBuffer Setup;
		public bool MainBegun;
		public bool SetupBegun;

		/// <summary>A command was recorded ahead of the stream outside the tracking of uses: the setup buffer ends with a barrier of everything.</summary>
		public bool SetupUntracked;

		/// <summary>The barriers of the setup command buffer.</summary>
		public readonly SyncBatch SetupBatch = new SyncBatch();

		/// <summary>Stages that wrote buffers the CPU reads (mapped ones): made visible to the host at the end of the submission.</summary>
		public VkPipelineStageFlags2 HostReads;

		/// <summary>The timestamp queries the submission writes: their values are copied at its end.</summary>
		public readonly List<int> WrittenQueries = new List<int>();

		/// <summary>Signalled with <see cref="SubmittedValue" /> by each submission (none on a device with one queue).</summary>
		public VkSemaphore Timeline;

		/// <summary>The value the last submission sent signals.</summary>
		public long SubmittedValue;

		/// <summary>The other queue's value the submission being recorded waits for (0: none).</summary>
		public long WaitValue;

		/// <summary>
		///     With LEL_VULKAN_QUEUE_SEMAPHORES=binary: the binary semaphores this queue's submissions signalled (with the
		///     timeline value of each) that the other queue has not waited for yet.
		/// </summary>
		public readonly List<(long Value, VkSemaphore Semaphore)> BinarySignals = new List<(long Value, VkSemaphore Semaphore)>();

		/// <summary>The highest value of the other queue's timeline a submission of this queue (sent or being recorded) waits for.</summary>
		public long WaitedValue;

		public VulkanStream(int index, VkQueue queue, int family, VkCommandPool pool)
		{
			Index = index;
			Queue = queue;
			Family = family;
			Pool = pool;
		}

		/// <summary>The value the submission being recorded will signal.</summary>
		public long RecordingValue => SubmittedValue + 1;

		public bool HasRecorded => MainBegun || SetupBegun;

		public bool IsCompute => Index == 1;
	}

	/// <summary>
	///     The device's queues: the graphics queue, and on a GPU that has one an asynchronous compute queue of a family of
	///     its own (AMD's: its asynchronous compute engines), which runs the dispatches, copies and builds the command list
	///     gives it (<see cref="CommandList.BeginAsyncCompute" />) beside the graphics queue's work. Resources the compute
	///     queue may use are shared by the two families (concurrent sharing: no ownership transfers); depth textures stay
	///     the graphics family's (their compression), and the commands that use them stay on the graphics queue.
	///     <para>
	///         Ordering: every resource notes where each queue last read and wrote it (<see cref="QueueUses" />) and keeps
	///         a barrier state per queue (<see cref="SyncState" />) of that queue's own uses, whose barriers order them
	///         within the queue; an image's layout is the image's, set in both states by the queue that changes it. A use
	///         that conflicts with the other queue's (a read after its write, a write or a layout change after its read or
	///         write) waits for that submission's timeline value: the other queue's recording is sent first when it holds
	///         the use, and this queue's recording is sent before the wait, so what it recorded before does not wait too.
	///         Uses in the middle of a command can split a submission: the frame's memory the command took before stays
	///         until the next submission has run too.
	///     </para>
	///     <para>
	///         Lifetimes stay the graphics queue's submissions' (<see cref="VulkanDevice.RecordingSerial" />): what the
	///         compute queue records while a graphics submission is recorded is sent before it, and that submission
	///         finishes when both queues have run their parts.
	///     </para>
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		private VulkanStream graphicsStream;
		private VulkanStream computeStream;
		private VulkanStream recordingStream;

		/// <summary>The value of the compute queue's timeline it has been seen to reach.</summary>
		private long computeCompleted;

		/// <summary>The commands being recorded are a readback's sent alone (<see cref="BeginAside" />): its uses are the graphics queue's, ordered by its own wait.</summary>
		internal bool AsideRecording;

		/// <summary>Waits of one queue for the other (each a split of a submission), and compute submissions: measured with LEL_VULKAN_STATS=1.</summary>
		internal long CrossQueueWaits;

		internal long ComputeSubmissions;

		/// <summary>The queue the commands are recorded for now.</summary>
		internal VulkanStream Recording => recordingStream;

		internal VulkanStream GraphicsStream => graphicsStream;

		/// <summary>The asynchronous compute queue's stream; null on a device without one.</summary>
		internal VulkanStream ComputeStream => computeStream;

		internal bool HasAsyncCompute => computeStream != null;

		/// <summary>LEL_VULKAN_TRACE_QUEUES=1: every submission of either queue and every wait between them is printed.</summary>
		private static readonly bool traceQueues = Environment.GetEnvironmentVariable("LEL_VULKAN_TRACE_QUEUES") == "1";

		/// <summary>
		///     LEL_VULKAN_QUEUE_SEMAPHORES=binary: each wait between the queues is made by binary semaphores too (each
		///     submission signals one, a wait waits for those of the other queue's submissions up to the value), for
		///     synchronization validation of layers that do not follow timeline semaphores between queues (1.3.290).
		/// </summary>
		private static readonly bool binaryQueueWaits = Environment.GetEnvironmentVariable("LEL_VULKAN_QUEUE_SEMAPHORES") == "binary";

		/// <summary>The semaphores of the submission being sent: its waits, then its signals.</summary>
		private readonly List<VkSemaphoreSubmitInfo> submitWaits = new List<VkSemaphoreSubmitInfo>();

		private readonly List<VkSemaphoreSubmitInfo> submitSignals = new List<VkSemaphoreSubmitInfo>();

		private VkSemaphoreSubmitInfo[] semaphoreInfos = new VkSemaphoreSubmitInfo[8];

		/// <summary>
		///     The semaphores a queue's submission waits for and signals for the other queue: its wait for the other's
		///     timeline value, its own value (with the binary semaphores of LEL_VULKAN_QUEUE_SEMAPHORES=binary).
		/// </summary>
		private void AddQueueSemaphores(VulkanStream stream)
		{
			if (computeStream == null)
			{
				return;
			}

			VulkanStream other = stream.Index == 0 ? computeStream : graphicsStream;
			if (stream.WaitValue > 0)
			{
				submitWaits.Add(new VkSemaphoreSubmitInfo { Semaphore = other.Timeline, Value = (nint)stream.WaitValue, StageMask = VkPipelineStageFlags2.AllCommandsBit });
				if (binaryQueueWaits)
				{
					for (int i = 0; i < other.BinarySignals.Count; i++)
					{
						(long value, VkSemaphore semaphore) = other.BinarySignals[i];
						if (value > stream.WaitValue)
						{
							continue;
						}

						submitWaits.Add(new VkSemaphoreSubmitInfo { Semaphore = semaphore, StageMask = VkPipelineStageFlags2.AllCommandsBit });
						// Unsignalled by the wait: free again once the submission has run.
						recording.BinaryWaited.Add(semaphore);
						other.BinarySignals.RemoveAt(i--);
					}
				}
			}

			submitSignals.Add(new VkSemaphoreSubmitInfo { Semaphore = stream.Timeline, Value = (nint)stream.RecordingValue, StageMask = VkPipelineStageFlags2.AllCommandsBit });
			if (binaryQueueWaits)
			{
				// Kept until the other queue waits for them (also after they have run: the validation layer learns the
				// order from the wait, not from the CPU having seen the timeline), at most a few hundred.
				if (stream.BinarySignals.Count >= 256)
				{
					Vk->vkQueueWaitIdle.Invoke(stream.Queue);
					foreach ((long _, VkSemaphore stale) in stream.BinarySignals)
					{
						Vk->vkDestroySemaphore.Invoke(Handle, stale, null);
					}

					stream.BinarySignals.Clear();
				}

				VkSemaphore semaphore = TakeSemaphore();
				stream.BinarySignals.Add((stream.RecordingValue, semaphore));
				submitSignals.Add(new VkSemaphoreSubmitInfo { Semaphore = semaphore, StageMask = VkPipelineStageFlags2.AllCommandsBit });
			}
		}

		/// <summary>A submission has run: the binary semaphores its waits unsignalled come back (LEL_VULKAN_QUEUE_SEMAPHORES=binary).</summary>
		private void ReleaseBinarySemaphores(List<VkSemaphore> waited)
		{
			foreach (VkSemaphore semaphore in waited)
			{
				ReturnSemaphore(semaphore);
			}

			waited.Clear();
		}

		/// <summary>Sends one batch of a queue's command buffers with the semaphores gathered (<see cref="submitWaits" />, <see cref="submitSignals" />).</summary>
		private void SendBatch(VkQueue queue, VkCommandBufferSubmitInfo* buffers, int count, VkFence fence)
		{
			int total = submitWaits.Count + submitSignals.Count;
			if (semaphoreInfos.Length < total)
			{
				semaphoreInfos = new VkSemaphoreSubmitInfo[total * 2];
			}

			submitWaits.CopyTo(semaphoreInfos, 0);
			submitSignals.CopyTo(semaphoreInfos, submitWaits.Count);
			if (traceQueues)
			{
				System.Text.StringBuilder line = new System.Text.StringBuilder($"[queues] batch to {(queue.VkHandle == Queue.VkHandle ? "graphics" : "compute")}:");
				for (int i = 0; i < count; i++)
				{
					line.Append($" buffer {buffers[i].CommandBuffer.VkHandle:x}");
				}

				foreach (VkSemaphoreSubmitInfo wait in submitWaits)
				{
					line.Append($" wait {wait.Semaphore.VkHandle:x}={(long)wait.Value}");
				}

				foreach (VkSemaphoreSubmitInfo signal in submitSignals)
				{
					line.Append($" signal {signal.Semaphore.VkHandle:x}={(long)signal.Value}");
				}

				Console.WriteLine(line);
			}

			fixed (VkSemaphoreSubmitInfo* infos = semaphoreInfos)
			{
				VkSubmitInfo2 submit = new VkSubmitInfo2
				{
					CommandBufferInfoCount = count,
					CommandBufferInfos = buffers,
					WaitSemaphoreInfoCount = submitWaits.Count,
					WaitSemaphoreInfos = infos,
					SignalSemaphoreInfoCount = submitSignals.Count,
					SignalSemaphoreInfos = infos + submitWaits.Count
				};
				Check(Vk->vkQueueSubmit2.Invoke(queue, 1, &submit, fence), "vkQueueSubmit2");
			}

			submitWaits.Clear();
			submitSignals.Clear();
		}

		/// <summary>The commands are recorded for the compute queue.</summary>
		internal bool RecordingCompute => recordingStream.Index == 1;

		/// <summary>The commands that follow are recorded for a queue.</summary>
		internal void Record(VulkanStream stream)
		{
			if (recordingStream != stream)
			{
				recordingStream = stream;
				// Programs note their resources' uses again: they are the other queue's from here on.
				SyncEpoch++;
			}
		}

		/// <summary>
		///     A family of compute queues apart from the graphics family, whose queues write timestamps; -1 when it is not
		///     asked for (<see cref="GraphicsDevice.RequestAsyncCompute" />), the GPU has none, or LEL_VULKAN_DISABLE names
		///     "asynccompute".
		/// </summary>
		private int FindComputeFamily(VkPhysicalDevice physical, int graphicsFamily, bool timelines)
		{
			string disabled = Environment.GetEnvironmentVariable("LEL_VULKAN_DISABLE") ?? "";
			if (disabled.IndexOf("asynccompute", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				Console.WriteLine("[Vulkan] Disabled by LEL_VULKAN_DISABLE: asynccompute");
				return -1;
			}

			if (!timelines || !RequestAsyncCompute)
			{
				return -1;
			}

			int count = 0;
			InstanceVk->vkGetPhysicalDeviceQueueFamilyProperties.Invoke(physical, &count, null);
			VkQueueFamilyProperties* families = stackalloc VkQueueFamilyProperties[Math.Max(1, count)];
			InstanceVk->vkGetPhysicalDeviceQueueFamilyProperties.Invoke(physical, &count, families);
			for (int i = 0; i < count; i++)
			{
				VkQueueFlags flags = families[i].QueueFlags;
				if (i != graphicsFamily && (flags & VkQueueFlags.ComputeBit) != 0 && (flags & VkQueueFlags.GraphicsBit) == 0 && families[i].QueueCount > 0 && families[i].TimestampValidBits >= 36)
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>The streams of the queues, once the logical device is made (the compute family -1: the graphics queue only).</summary>
		private void CreateStreams(int computeFamily)
		{
			graphicsStream = new VulkanStream(0, Queue, QueueFamily, CreateCommandPool(QueueFamily));
			recordingStream = graphicsStream;
			if (computeFamily < 0)
			{
				return;
			}

			VkQueue queue;
			Vk->vkGetDeviceQueue.Invoke(Handle, computeFamily, 0, &queue);
			computeStream = new VulkanStream(1, queue, computeFamily, CreateCommandPool(computeFamily));
			graphicsStream.Timeline = CreateTimeline();
			computeStream.Timeline = CreateTimeline();
			sharedFamilies[0] = QueueFamily;
			sharedFamilies[1] = computeFamily;
		}

		private VkCommandPool CreateCommandPool(int family)
		{
			VkCommandPoolCreateInfo info = new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit, QueueFamilyIndex = family };
			VkCommandPool pool;
			Check(Vk->vkCreateCommandPool.Invoke(Handle, &info, null, &pool), "vkCreateCommandPool");
			return pool;
		}

		private VkSemaphore CreateTimeline()
		{
			VkSemaphoreTypeCreateInfo type = new VkSemaphoreTypeCreateInfo { SemaphoreType = VkSemaphoreType.Timeline, InitialValue = 0 };
			VkSemaphoreCreateInfo info = new VkSemaphoreCreateInfo { Next = &type };
			VkSemaphore semaphore;
			Check(Vk->vkCreateSemaphore.Invoke(Handle, &info, null, &semaphore), "vkCreateSemaphore (timeline)");
			return semaphore;
		}

		/// <summary>The families a resource the compute queue may use is shared by.</summary>
		private readonly int[] sharedFamilies = new int[2];

		/// <summary>
		///     Makes a buffer usable by both queues (concurrent sharing between their families) on a device with an
		///     asynchronous compute queue; <paramref name="families" /> holds two ints for the call that makes it.
		/// </summary>
		internal void ShareBuffer(ref VkBufferCreateInfo info, int* families)
		{
			if (computeStream == null)
			{
				return;
			}

			families[0] = sharedFamilies[0];
			families[1] = sharedFamilies[1];
			info.SharingMode = VkSharingMode.Concurrent;
			info.QueueFamilyIndexCount = 2;
			info.QueueFamilyIndices = families;
		}

		/// <summary>As <see cref="ShareBuffer" />, for an image.</summary>
		internal void ShareImage(ref VkImageCreateInfo info, int* families)
		{
			if (computeStream == null)
			{
				return;
			}

			families[0] = sharedFamilies[0];
			families[1] = sharedFamilies[1];
			info.SharingMode = VkSharingMode.Concurrent;
			info.QueueFamilyIndexCount = 2;
			info.QueueFamilyIndices = families;
		}

		/// <summary>
		///     Orders a use of a resource by the queue being recorded for after the other queue's uses it conflicts with
		///     (<paramref name="write" />: a write or a layout change, after the other's reads and writes; else a read,
		///     after the other's writes), and notes it. The wait splits submissions: see the class's notes.
		/// </summary>
		internal void OrderAcrossQueues(ref QueueUses uses, bool write)
		{
			if (computeStream == null || AsideRecording)
			{
				return;
			}

			VulkanStream stream = recordingStream;
			bool graphics = stream.Index == 0;
			long otherWrite = graphics ? uses.ComputeWrite : uses.GraphicsWrite;
			long otherRead = graphics ? uses.ComputeRead : uses.GraphicsRead;
			long need = write ? Math.Max(otherWrite, otherRead) : otherWrite;
			if (need > stream.WaitedValue)
			{
				WaitForOtherQueue(stream, need);
			}

			long value = stream.RecordingValue;
			if (graphics)
			{
				if (write)
				{
					uses.GraphicsWrite = value;
				}
				else
				{
					uses.GraphicsRead = value;
				}
			}
			else if (write)
			{
				uses.ComputeWrite = value;
			}
			else
			{
				uses.ComputeRead = value;
			}
		}

		/// <summary>
		///     Asynchronous compute work begins: it starts after the graphics work sent so far, whatever its resources need.
		///     Without this the compute queue runs ahead into the graphics queue's frames before (the CPU records a frame
		///     ahead), beside their heaviest work, where on the 780M both queues' compute slowed down several times over.
		/// </summary>
		internal void ForkCompute()
		{
			VulkanStream stream = computeStream;
			if (stream.HasRecorded)
			{
				SubmitCompute();
			}

			long value = graphicsStream.SubmittedValue;
			if (value > stream.WaitedValue)
			{
				stream.WaitValue = Math.Max(stream.WaitValue, value);
				stream.WaitedValue = value;
			}
		}

		/// <summary>The submission of <paramref name="stream" /> being recorded waits for value <paramref name="value" /> of the other queue's timeline.</summary>
		private void WaitForOtherQueue(VulkanStream stream, long value)
		{
			VulkanStream other = stream.Index == 0 ? computeStream : graphicsStream;
			CrossQueueWaits++;
			if (traceQueues)
			{
				Console.WriteLine($"[queues] {(stream.Index == 0 ? "graphics" : "compute")} (recording {stream.RecordingValue}) waits for {(other.Index == 0 ? "graphics" : "compute")} {value} (sent {other.SubmittedValue})");
			}
			if (value > other.SubmittedValue)
			{
				// The other queue's recording holds the use: it goes to the GPU now.
				SubmitStream(other);
			}

			if (stream.HasRecorded)
			{
				// What this queue recorded before the use does not wait: it goes first.
				SubmitStream(stream);
			}

			stream.WaitValue = Math.Max(stream.WaitValue, value);
			stream.WaitedValue = Math.Max(stream.WaitedValue, value);
		}

		/// <summary>Sends a queue's recording in the middle of a command (a wait between the queues): the graphics queue's keeps the frame's memory for the command.</summary>
		private void SubmitStream(VulkanStream stream)
		{
			if (stream.Index == 1)
			{
				SubmitCompute();
				return;
			}

			// The command being recorded may read memory taken before the split (its uniforms, its upload's texels):
			// that memory stays until the next submission has finished too.
			Submission before = recording;
			Chunk keep = currentChunk;
			Submit();
			foreach (Chunk chunk in before.Chunks)
			{
				chunk.Users++;
				recording.Chunks.Add(chunk);
			}

			currentChunk = keep;
		}

		/// <summary>
		///     Sends what the compute queue recorded (nothing when it recorded nothing): it waits for the graphics queue's
		///     value it needs and signals its own. It belongs to the graphics submission being recorded, which finishes
		///     when both have run.
		/// </summary>
		internal void SubmitCompute()
		{
			VulkanStream stream = computeStream;
			if (stream == null || !stream.HasRecorded)
			{
				return;
			}

			VulkanStream before = recordingStream;
			recordingStream = stream;
			CommandList.BeforeSubmit(stream);
			VkCommandBufferSubmitInfo* buffers = stackalloc VkCommandBufferSubmitInfo[2];
			int count = EndCommandBuffers(stream, buffers, recording.ComputeCommands);
			AddQueueSemaphores(stream);
			VkFence fence = default;
			if (binaryQueueWaits)
			{
				// The validation layer learns that the compute work has run from a fence (it does not follow the timeline's
				// value read by the CPU): what it used may be reused after.
				fence = TakeFence();
				recording.ComputeFences.Add(fence);
			}

			SendBatch(stream.Queue, buffers, count, fence);
			if (traceQueues)
			{
				Console.WriteLine($"[queues] compute sends {stream.RecordingValue} ({count} command buffers), waiting for graphics {stream.WaitValue}");
			}
			stream.SubmittedValue++;
			stream.WaitValue = 0;
			recording.ComputeValue = stream.SubmittedValue;
			ComputeSubmissions++;
			recordingStream = before;
			CommandList.AfterSubmit(stream);
		}

		/// <summary>Ends a stream's command buffers into the submit infos (the setup buffer first) and notes them in <paramref name="owner" />; their count.</summary>
		private int EndCommandBuffers(VulkanStream stream, VkCommandBufferSubmitInfo* buffers, List<VkCommandBuffer> owner)
		{
			int count = 0;
			if (stream.SetupBegun)
			{
				if (stream.SetupUntracked)
				{
					GlobalBarrier(stream.Setup);
				}

				Check(Vk->vkEndCommandBuffer.Invoke(stream.Setup), "vkEndCommandBuffer");
				buffers[count++] = new VkCommandBufferSubmitInfo { CommandBuffer = stream.Setup };
				owner.Add(stream.Setup);
			}

			if (stream.MainBegun)
			{
				Check(Vk->vkEndCommandBuffer.Invoke(stream.Main), "vkEndCommandBuffer");
				buffers[count++] = new VkCommandBufferSubmitInfo { CommandBuffer = stream.Main };
				owner.Add(stream.Main);
			}

			stream.MainBegun = false;
			stream.SetupBegun = false;
			stream.SetupUntracked = false;
			return count;
		}

		/// <summary>Whether the compute queue has run its submissions up to a value (asks the semaphore when it does not know yet).</summary>
		private bool ComputeReached(long value)
		{
			if (value <= computeCompleted)
			{
				return true;
			}

			nint reached;
			Check(Vk->vkGetSemaphoreCounterValue.Invoke(Handle, computeStream.Timeline, &reached), "vkGetSemaphoreCounterValue");
			computeCompleted = Math.Max(computeCompleted, (long)reached);
			return value <= computeCompleted;
		}

		/// <summary>Waits until the compute queue has run its submissions up to a value; false when the time runs out.</summary>
		private bool WaitForCompute(long value, long timeoutNanoseconds)
		{
			if (ComputeReached(value))
			{
				return true;
			}

			VkSemaphore semaphore = computeStream.Timeline;
			nint target = (nint)value;
			VkSemaphoreWaitInfo info = new VkSemaphoreWaitInfo { SemaphoreCount = 1, Semaphores = &semaphore, Values = &target };
			VkResult result = Vk->vkWaitSemaphores.Invoke(Handle, &info, (nint)Math.Max(0, timeoutNanoseconds));
			if (result == VkResult.Timeout)
			{
				return false;
			}

			Check(result, "vkWaitSemaphores");
			computeCompleted = Math.Max(computeCompleted, value);
			return true;
		}

		/// <summary>The device is being given up: the compute queue's pool and the timelines go.</summary>
		private void ReleaseQueues()
		{
			foreach (VulkanStream stream in new[] { graphicsStream, computeStream })
			{
				if (stream == null)
				{
					continue;
				}

				stream.Free.Clear();
				foreach ((long _, VkSemaphore semaphore) in stream.BinarySignals)
				{
					Vk->vkDestroySemaphore.Invoke(Handle, semaphore, null);
				}

				stream.BinarySignals.Clear();
				if (!stream.Pool.IsNull())
				{
					// Frees the command buffers with it.
					Vk->vkDestroyCommandPool.Invoke(Handle, stream.Pool, null);
				}

				if (!stream.Timeline.IsNull())
				{
					Vk->vkDestroySemaphore.Invoke(Handle, stream.Timeline, null);
				}
			}

			graphicsStream = null;
			computeStream = null;
		}
	}
}
