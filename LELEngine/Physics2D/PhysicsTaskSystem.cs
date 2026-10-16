using System;
using System.Collections.Concurrent;
using System.Threading;
using Box2D.NET;

namespace LELEngine
{
	/// <summary>
	///     The task system Box2D v3 runs its step on (narrow phase, solver workers, body finalization, tree rebuild,
	///     sensors, continuous), in the manner of the enkiTS scheduler Box2D is built for: a task is cut into ranges
	///     that go into one shared queue; worker threads take ranges from it as they come, and the stepping thread
	///     takes them too while it waits in finishTask. Every thread has a fixed worker index (the stepping thread is
	///     0), which Box2D uses for its per-worker data. Tasks run asynchronously: Box2D overlaps single-range tasks
	///     (tree rebuild, island split) with its own work and runs one solver task per worker, the workers waiting for
	///     each other between stages. Shared by every world.
	/// </summary>
	internal sealed class PhysicsTaskSystem
	{
		#region PublicFields

		/// <summary>Workers including the stepping thread.</summary>
		public int WorkerCount { get; }

		public readonly b2EnqueueTaskCallback Enqueue;
		public readonly b2FinishTaskCallback Finish;

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static PhysicsTaskSystem shared;

		private readonly ConcurrentQueue<Range> queue = new ConcurrentQueue<Range>();
		private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
		private readonly ConcurrentBag<Batch> batchPool = new ConcurrentBag<Batch>();
		private readonly Thread[] threads;
		private volatile bool stopping;

		#endregion

		#region Constructors

		private PhysicsTaskSystem(int workerCount)
		{
			WorkerCount = workerCount;
			threads = new Thread[workerCount];
			for (int i = 1; i < workerCount; i++)
			{
				uint index = (uint)i;
				threads[i] = new Thread(() => Work(index)) { IsBackground = true, Name = "Physics2D worker " + i };
				threads[i].Start();
			}

			Enqueue = EnqueueTask;
			Finish = FinishTask;
		}

		#endregion

		#region PublicMethods

		/// <summary>The shared system with at least <paramref name="workerCount" /> workers (grown on demand, never shrunk).</summary>
		public static PhysicsTaskSystem Get(int workerCount)
		{
			lock (sync)
			{
				if (shared == null || shared.WorkerCount < workerCount)
				{
					shared?.Stop();
					shared = new PhysicsTaskSystem(workerCount);
				}

				return shared;
			}
		}

		/// <summary>Worker count for a setting: 0 = automatic (half the logical processors, the physical cores, at most 8).</summary>
		public static int Resolve(int setting)
		{
			if (setting > 0)
			{
				return Math.Clamp(setting, 1, 32);
			}

			return Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
		}

		#endregion

		#region PrivateMethods

		private object EnqueueTask(b2TaskCallback task, int itemCount, int minRange, object taskContext, object userContext)
		{
			int workers = userContext is int limit ? Math.Min(limit, WorkerCount) : WorkerCount;
			int ranges = Math.Clamp((itemCount + Math.Max(1, minRange) - 1) / Math.Max(1, minRange), 1, workers);
			if (!batchPool.TryTake(out Batch batch))
			{
				batch = new Batch();
			}

			batch.Begin(ranges);
			int size = itemCount / ranges;
			int remainder = itemCount % ranges;
			int start = 0;
			for (int r = 0; r < ranges; r++)
			{
				int end = start + size + (r < remainder ? 1 : 0);
				queue.Enqueue(new Range(task, start, end, taskContext, batch));
				start = end;
			}

			signal.Release(Math.Min(ranges, WorkerCount - 1));
			return batch;
		}

		/// <summary>The stepping thread runs queued ranges (as worker 0) until the task is done.</summary>
		private void FinishTask(object userTask, object userContext)
		{
			if (!(userTask is Batch batch))
			{
				return;
			}

			while (!batch.IsDone)
			{
				if (queue.TryDequeue(out Range range))
				{
					range.Run(0);
				}
				else
				{
					batch.Wait();
				}
			}

			batchPool.Add(batch);
		}

		private void Work(uint index)
		{
			while (true)
			{
				signal.Wait();
				if (stopping)
				{
					return;
				}

				while (queue.TryDequeue(out Range range))
				{
					range.Run(index);
				}
			}
		}

		private void Stop()
		{
			stopping = true;
			signal.Release(threads.Length);
		}

		#endregion

		#region NestedTypes

		private readonly struct Range
		{
			private readonly b2TaskCallback task;
			private readonly int start;
			private readonly int end;
			private readonly object context;
			private readonly Batch batch;

			public Range(b2TaskCallback task, int start, int end, object context, Batch batch)
			{
				this.task = task;
				this.start = start;
				this.end = end;
				this.context = context;
				this.batch = batch;
			}

			public void Run(uint worker)
			{
				try
				{
					task(start, end, worker, context);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					batch.Complete();
				}
			}
		}

		/// <summary>The ranges of one task not finished yet.</summary>
		private sealed class Batch
		{
			private int pending;
			private readonly ManualResetEventSlim done = new ManualResetEventSlim(false, 2000);

			public bool IsDone => Volatile.Read(ref pending) == 0;

			public void Begin(int count)
			{
				done.Reset();
				Volatile.Write(ref pending, count);
			}

			public void Complete()
			{
				if (Interlocked.Decrement(ref pending) == 0)
				{
					done.Set();
				}
			}

			/// <summary>Waits a little for the task (a range may become available to help with meanwhile).</summary>
			public void Wait()
			{
				done.Wait(1);
			}
		}

		#endregion
	}
}
