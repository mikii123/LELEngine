using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The commands of one queue's stream as they are given, recorded into command buffers on a thread of its own. The
	///     command list decides everything on the thread that gives the commands (the ordering and its barriers, the
	///     pipelines, descriptor sets, push constants) and writes each Vulkan command it would record into memory of
	///     its own here; the recording thread takes them in batches and records them into the submission's command
	///     buffers, while the commands after them are being given. What the driver does for each command (on AMD's ~30-50 ns
	///     for a draw, a few for state) leaves the thread that gives them, as an OpenGL driver's own thread takes it.
	///     <para>
	///         A submission's commands of the stream are a sequence of segments, each one command buffer: segments
	///         recorded here, and segments the device records itself on the giving thread (<see cref="VulkanDevice.MainCommands" />:
	///         the commands that are not written here, such as acceleration structure builds and the present's blit). A
	///         segment ends where the other kind begins; the command list binds everything anew in the next (a command
	///         buffer starts with nothing bound), and rendering never spans two (it is suspended before). At a submission
	///         the giving thread waits until the recording thread has ended its segments, and sends them in order.
	///     </para>
	///     <para>
	///         LEL_VULKAN_RECORDING_THREAD=0: the commands are recorded where they are given (written here and recorded at
	///         once), for comparisons.
	///     </para>
	/// </summary>
	internal sealed unsafe partial class VulkanRecorder : IDisposable
	{
		/// <summary>Bytes of a block of written commands.</summary>
		private const int ChunkBytes = 256 * 1024;

		/// <summary>Commands written before a batch of them is handed to the recording thread.</summary>
		private const int HandOffCommands = 48;

		/// <summary>Batches the recording thread may be behind by (each at most <see cref="HandOffCommands" /> commands or a block).</summary>
		private const int RingSize = 4096;

		/// <summary>How long the recording thread looks for more work before it sleeps: 1 ms.</summary>
		private static readonly long SpinTicks = Stopwatch.Frequency / 1000;

		/// <summary>Segments of a stream in one submission from which on commands are recorded where they are given.</summary>
		internal const int MaxSegments = 48;

		/// <summary>Command buffers of a stream in one submission at most: the setup buffer and the segments (a few past the limit).</summary>
		internal const int MaxSubmitBuffers = MaxSegments + 4;

		/// <summary>LEL_VULKAN_RECORDING_THREAD=0: no recording thread.</summary>
		internal static readonly bool Threaded = Environment.GetEnvironmentVariable("LEL_VULKAN_RECORDING_THREAD") != "0";

		private readonly VulkanDevice device;
		private readonly VulkanStream stream;
		private readonly VkDevice.FunctionTable* vk;

		// Written commands: the block being written, where, and where the batch not handed off yet starts.
		private Chunk chunk;
		private byte* write;
		private byte* limit;
		private byte* batchStart;
		private int batchCommands;
		private readonly Stack<Chunk> freeChunks = new Stack<Chunk>();
		private readonly object chunkSync = new object();

		/// <summary>The segments of the submission being recorded, in order.</summary>
		private readonly List<Segment> segments = new List<Segment>();

		/// <summary>The segment the next written command goes into (null: none open).</summary>
		private Segment open;

		/// <summary>Whether the open segment's first batch was handed off (its command buffer begun by the thread).</summary>
		private bool openStarted;

		/// <summary>No segment of the thread is open: the command being written is recorded at once into the device's command buffer.</summary>
		private bool immediate;

		/// <summary>The pool the recording thread takes the submission's command buffers from.</summary>
		private VulkanCommandPool pool;

		// The hand-off: a ring of batches, the count handed off (the giving thread's) and done (the recording thread's).
		private readonly Batch[] ring = new Batch[RingSize];
		private long handed;
		private long done;
		private Thread thread;
		private readonly SemaphoreSlim work = new SemaphoreSlim(0);
		private int sleeping;
		private volatile bool stopping;
		private Exception failure;

		public VulkanRecorder(VulkanDevice device, VulkanStream stream)
		{
			this.device = device;
			this.stream = stream;
			vk = device.Vk;
			TakeChunk(ChunkBytes);
			batchStart = write;
		}

		/// <summary>Segments of the submission being recorded (the open one too).</summary>
		public int SegmentCount => segments.Count;

		/// <summary>Stopwatch ticks the recording thread spent recording, and the giving thread waiting for it at submissions (LEL_VULKAN_STATS=1).</summary>
		public long RecordingTicks => Volatile.Read(ref recordingTicks);

		public long WaitTicks;

		private long recordingTicks;

		/// <summary>A segment written here is open: the commands written go into it.</summary>
		public bool IsOpen => open != null;

		#region Writing commands

		/// <summary>
		///     Room for a command of <paramref name="payload" /> bytes after its header, in the open segment (one is opened
		///     after whatever the stream recorded before, unless the device records the stream's commands where they are
		///     given); <see cref="End" /> once it is written.
		/// </summary>
		private byte* Begin(Op op, int payload)
		{
			if (open == null)
			{
				immediate = !device.OpenRecordedSegment(stream);
			}

			int size = (sizeof(Header) + payload + 7) & ~7;
			if (write + size > limit)
			{
				NextChunk(size);
			}

			Header* header = (Header*)write;
			header->Op = op;
			header->Size = size;
			byte* body = write + sizeof(Header);
			write += size;
			return body;
		}

		/// <summary>A command was written: a full batch goes to the recording thread, or the command is recorded now.</summary>
		private void End()
		{
			if (immediate)
			{
				// Into the device's command buffer of the stream, and its bytes are free again.
				Replay(batchStart, write, device.DirectCommands(stream));
				write = batchStart;
				return;
			}

			if (++batchCommands >= HandOffCommands)
			{
				HandOff(BatchFlags.None);
			}
		}

		/// <summary>
		///     The commands that follow go into a segment of the recording thread, opened now when none is (or are recorded
		///     where they are given): before a draw or dispatch looks at what its command buffer has bound.
		/// </summary>
		public void Prepare()
		{
			if (open == null)
			{
				immediate = !device.OpenRecordedSegment(stream);
			}
		}

		/// <summary>What is written of the open segment goes to the recording thread now (a pass ended: the giving thread may do other work for a while).</summary>
		public void Publish()
		{
			if (batchCommands > 0)
			{
				HandOff(BatchFlags.None);
			}
		}

		/// <summary>Opens a segment for the commands written from now on (the device does, after the stream's segment before).</summary>
		internal void Open()
		{
			Segment segment = new Segment();
			segments.Add(segment);
			open = segment;
			openStarted = false;
			batchStart = write;
			batchCommands = 0;
		}

		/// <summary>Ends the open segment: what is written of it goes to the recording thread, which ends its command buffer.</summary>
		internal void Close()
		{
			if (open == null)
			{
				return;
			}

			HandOff(BatchFlags.EndSegment);
			open = null;
		}

		/// <summary>A segment the device recorded itself on the giving thread, in its place in the sequence (its command buffer ended).</summary>
		internal void AddRecordedHere(VkCommandBuffer buffer)
		{
			segments.Add(new Segment { Buffer = buffer });
		}

		/// <summary>
		///     The submission is being sent: the open segment ends, the giving thread waits until the recording thread has
		///     recorded everything, and the command buffers of every segment are written in order. Returns how many.
		/// </summary>
		internal int Finish(VkCommandBufferSubmitInfo* buffers, out VulkanCommandPool usedPool)
		{
			Close();
			WaitIdle();
			int count = 0;
			foreach (Segment segment in segments)
			{
				if (!segment.Buffer.IsNull())
				{
					buffers[count++] = new VkCommandBufferSubmitInfo { CommandBuffer = segment.Buffer };
				}
			}

			segments.Clear();
			usedPool = pool;
			pool = null;
			return count;
		}

		/// <summary>
		///     Takes a new block (the command does not fit the rest of this one): the recording thread gives the old one back
		///     once it has recorded what was written there.
		/// </summary>
		private void NextChunk(int size)
		{
			if (open != null)
			{
				HandOff(BatchFlags.EndChunk);
			}
			else
			{
				// Nothing open hands it over: what the thread still records of it is waited for.
				WaitIdle();
				ReturnChunk(chunk);
			}

			TakeChunk(Math.Max(ChunkBytes, size));
			batchStart = write;
		}

		private void TakeChunk(int size)
		{
			Chunk taken = null;
			lock (chunkSync)
			{
				if (freeChunks.Count > 0 && freeChunks.Peek().Size >= size)
				{
					taken = freeChunks.Pop();
				}
			}

			taken ??= new Chunk(size);
			chunk = taken;
			write = taken.Memory;
			limit = taken.Memory + taken.Size;
		}

		private void ReturnChunk(Chunk returned)
		{
			lock (chunkSync)
			{
				freeChunks.Push(returned);
			}
		}

		#endregion

		#region The recording thread

		/// <summary>Hands what was written since the last hand-off to the recording thread.</summary>
		private void HandOff(BatchFlags flags)
		{
			if (open == null || (write == batchStart && flags == BatchFlags.None))
			{
				return;
			}

			if (!openStarted)
			{
				flags |= BatchFlags.StartSegment;
				openStarted = true;
				pool ??= device.TakeCommandPool(stream);
			}

			if (thread == null)
			{
				// Above the normal priority: the giving thread waits for it at every submission.
				thread = new Thread(Run)
				{
					IsBackground = true,
					Priority = ThreadPriority.AboveNormal,
					Name = stream.Index == 0 ? "Vulkan recording (graphics queue)" : "Vulkan recording (compute queue)"
				};
				thread.Start();
			}

			long index = handed;
			while (index - Volatile.Read(ref done) >= RingSize)
			{
				// The recording thread is a whole ring behind: wait for it.
				Thread.Yield();
			}

			ref Batch batch = ref ring[index % RingSize];
			batch.Start = batchStart;
			batch.End = write;
			batch.Flags = flags;
			batch.Segment = open;
			batch.Chunk = chunk;
			batch.Pool = pool;
			// A full barrier between the count and the look at the thread's sleep (it does the same the other way).
			Interlocked.Exchange(ref handed, index + 1);
			if (Volatile.Read(ref sleeping) != 0)
			{
				work.Release();
			}

			batchStart = write;
			batchCommands = 0;
		}

		/// <summary>Waits until the recording thread has recorded everything handed to it; rethrows what failed there.</summary>
		private void WaitIdle()
		{
			if (thread == null)
			{
				return;
			}

			if (Volatile.Read(ref done) != handed)
			{
				long started = Stopwatch.GetTimestamp();
				SpinWait spin = new SpinWait();
				while (Volatile.Read(ref done) != handed)
				{
					spin.SpinOnce(-1);
				}

				WaitTicks += Stopwatch.GetTimestamp() - started;
			}

			Exception error = Interlocked.Exchange(ref failure, null);
			if (error != null)
			{
				throw new InvalidOperationException("Recording Vulkan commands failed on the recording thread: " + error.Message, error);
			}
		}

		private void Run()
		{
			VkCommandBuffer commands = default;
			while (true)
			{
				long index = Volatile.Read(ref done);
				if (index == Volatile.Read(ref handed))
				{
					if (!WaitForWork(index))
					{
						return;
					}

					continue;
				}

				ref Batch batch = ref ring[index % RingSize];
				long started = Stopwatch.GetTimestamp();
				try
				{
					if ((batch.Flags & BatchFlags.StartSegment) != 0)
					{
						commands = batch.Pool.Take(device);
						VkCommandBufferBeginInfo begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
						VulkanDevice.Check(vk->vkBeginCommandBuffer.Invoke(commands, &begin), "vkBeginCommandBuffer");
					}

					Replay(batch.Start, batch.End, commands);
					if ((batch.Flags & BatchFlags.EndSegment) != 0)
					{
						VulkanDevice.Check(vk->vkEndCommandBuffer.Invoke(commands), "vkEndCommandBuffer");
						batch.Segment.Buffer = commands;
						commands = default;
					}
				}
				catch (Exception e)
				{
					Interlocked.CompareExchange(ref failure, e, null);
				}

				if ((batch.Flags & BatchFlags.EndChunk) != 0)
				{
					ReturnChunk(batch.Chunk);
				}

				batch = default;
				Interlocked.Add(ref recordingTicks, Stopwatch.GetTimestamp() - started);
				Volatile.Write(ref done, index + 1);
			}
		}

		/// <summary>
		///     Spins for the next batch while one may come soon (<see cref="SpinTicks" />: the gaps between a frame's passes),
		///     then sleeps until one comes (between frames); false when the recorder is stopped. Woken from sleep, the
		///     thread starts late (tens to hundreds of microseconds), which the giving thread would wait for at a submission.
		/// </summary>
		private bool WaitForWork(long index)
		{
			long until = Stopwatch.GetTimestamp() + SpinTicks;
			SpinWait spin = new SpinWait();
			while (Stopwatch.GetTimestamp() < until)
			{
				if (Volatile.Read(ref handed) != index || stopping)
				{
					return !stopping;
				}

				spin.SpinOnce(-1);
			}

			Interlocked.Exchange(ref sleeping, 1);
			if (Volatile.Read(ref handed) == index && !stopping)
			{
				work.Wait();
			}

			Volatile.Write(ref sleeping, 0);
			return !stopping;
		}

		public void Dispose()
		{
			if (thread != null)
			{
				stopping = true;
				work.Release();
				thread.Join();
				thread = null;
			}

			lock (chunkSync)
			{
				while (freeChunks.Count > 0)
				{
					freeChunks.Pop().Free();
				}
			}

			chunk?.Free();
			chunk = null;
			work.Dispose();
		}

		#endregion

		/// <summary>A block of native memory commands are written into.</summary>
		private sealed class Chunk
		{
			public readonly byte* Memory;
			public readonly int Size;

			public Chunk(int size)
			{
				Size = size;
				Memory = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16);
			}

			public void Free()
			{
				NativeMemory.AlignedFree(Memory);
			}
		}

		/// <summary>One command buffer of the stream's sequence: recorded by the thread, or by the device on the giving thread.</summary>
		private sealed class Segment
		{
			/// <summary>The command buffer, ended: the recording thread's is set when it ends it.</summary>
			public VkCommandBuffer Buffer;
		}

		private struct Batch
		{
			public byte* Start;
			public byte* End;
			public BatchFlags Flags;
			public Segment Segment;
			public Chunk Chunk;
			public VulkanCommandPool Pool;
		}

		[Flags]
		private enum BatchFlags
		{
			None = 0,

			/// <summary>The segment's first batch: its command buffer is taken and begun.</summary>
			StartSegment = 1,

			/// <summary>The segment's last batch: its command buffer is ended.</summary>
			EndSegment = 2,

			/// <summary>The last batch in its block: the block is free once it is recorded.</summary>
			EndChunk = 4
		}

		private struct Header
		{
			public Op Op;
			public int Size;
		}
	}
}
