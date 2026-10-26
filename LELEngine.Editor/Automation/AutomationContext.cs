using System;
using System.Collections.Generic;
using System.Threading;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     Where automation code runs: on the main thread, between two editor frames (<see cref="EditorAutomation.Pump" />).
	///     Code awaiting inside it (<see cref="EditorAutomation.NextFrame" />, an input sequence, a capture) continues there,
	///     never on a thread-pool thread, and never while a UI frame is being built.
	/// </summary>
	internal sealed class AutomationContext : SynchronizationContext
	{
		#region PrivateFields

		private readonly Queue<(SendOrPostCallback callback, object state)> queue = new Queue<(SendOrPostCallback, object)>();
		private readonly object sync = new object();

		#endregion

		#region PublicMethods

		public override void Post(SendOrPostCallback d, object state)
		{
			lock (sync)
			{
				queue.Enqueue((d, state));
			}
		}

		public override void Send(SendOrPostCallback d, object state)
		{
			d(state);
		}

		public override SynchronizationContext CreateCopy()
		{
			return this;
		}

		/// <summary>Runs what was posted, with this context current, until nothing is left (bounded: work that keeps posting waits for the next tick).</summary>
		public void Drain()
		{
			SynchronizationContext previous = Current;
			SetSynchronizationContext(this);
			try
			{
				for (int i = 0; i < 100000; i++)
				{
					(SendOrPostCallback callback, object state) item;
					lock (sync)
					{
						if (queue.Count == 0)
						{
							return;
						}

						item = queue.Dequeue();
					}

					try
					{
						item.callback(item.state);
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}
				}
			}
			finally
			{
				SetSynchronizationContext(previous);
			}
		}

		#endregion
	}
}
