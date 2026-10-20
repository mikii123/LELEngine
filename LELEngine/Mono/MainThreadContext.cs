using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LELEngine
{
	/// <summary>
	///     The main thread's synchronization context (Unity's UnitySynchronizationContext): code awaiting on the main
	///     thread (an async Start, the rest of a method after <c>await Task.Delay</c>) continues there, in the
	///     <see cref="SceneManager.Update" /> of a later frame, and an exception escaping an async void method is logged
	///     instead of ending the process. <see cref="SceneManager" /> installs it on the main thread on its first update.
	/// </summary>
	internal sealed class MainThreadContext : SynchronizationContext
	{
		#region PublicFields

		/// <summary>The managed thread id of the main thread.</summary>
		public readonly int ThreadId;

		#endregion

		#region PrivateFields

		private readonly ConcurrentQueue<(SendOrPostCallback callback, object state, ManualResetEventSlim done)> queue =
			new ConcurrentQueue<(SendOrPostCallback, object, ManualResetEventSlim)>();

		#endregion

		#region Constructors

		public MainThreadContext(int threadId)
		{
			ThreadId = threadId;
		}

		#endregion

		#region PublicMethods

		public override SynchronizationContext CreateCopy()
		{
			return this;
		}

		public override void Post(SendOrPostCallback callback, object state)
		{
			queue.Enqueue((callback, state, null));
		}

		public override void Send(SendOrPostCallback callback, object state)
		{
			if (Environment.CurrentManagedThreadId == ThreadId)
			{
				callback(state);
				return;
			}

			using (var done = new ManualResetEventSlim())
			{
				queue.Enqueue((callback, state, done));
				done.Wait();
			}
		}

		/// <summary>Main thread: runs what was posted until now; what those post in turn waits for the next call.</summary>
		public void Pump()
		{
			int count = queue.Count;
			for (int i = 0; i < count && queue.TryDequeue(out (SendOrPostCallback callback, object state, ManualResetEventSlim done) item); i++)
			{
				try
				{
					item.callback(item.state);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					item.done?.Set();
				}
			}
		}

		/// <summary>Drops what was posted (play mode ended, the scripts reload): those continuations never run.</summary>
		public void Clear()
		{
			while (queue.TryDequeue(out (SendOrPostCallback callback, object state, ManualResetEventSlim done) item))
			{
				item.done?.Set();
			}
		}

		#endregion
	}
}
