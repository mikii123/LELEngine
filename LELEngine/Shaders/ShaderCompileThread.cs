using System;
using System.Collections.Concurrent;
using System.Threading;
using LELEngine.Graphics;
using LELEngine.Windowing;

namespace LELEngine.Shaders
{
	/// <summary>
	///     The thread <see cref="ShaderCompilation" /> compiles on: a graphics context of its own beside the main one
	///     (<see cref="GraphicsDevice.CreateWorkerContext" />, made on the main thread), so the main thread's frames go on
	///     while programs build. Programs are built whole here (<see cref="ShaderProgram.StartBuild" /> on this thread
	///     compiles, links and ends with the device finishing before the program counts as built), so the main thread only
	///     ever sees finished programs. The context is current here only while an operation runs.
	/// </summary>
	internal sealed class ShaderCompileThread
	{
		#region PrivateFields

		private readonly GraphicsContext context;
		private readonly Thread thread;
		private readonly BlockingCollection<(ShaderCompileOperation operation, bool gpuDriven)> queue = new BlockingCollection<(ShaderCompileOperation, bool)>();
		private readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);
		private int pending;

		#endregion

		#region Constructors

		private ShaderCompileThread(GraphicsContext context)
		{
			this.context = context;
			thread = new Thread(Run) { Name = "Shader compiler", IsBackground = true, Priority = ThreadPriority.BelowNormal };
			thread.Start();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Makes the thread with a context beside the one current on this thread (the main thread: windows are made
		///     only there). Null when there is no context or the device gives none.
		/// </summary>
		public static ShaderCompileThread TryCreate(out string why)
		{
			why = null;
			if (!GraphicsDevice.HasContext)
			{
				why = "no graphics context on this thread";
				return null;
			}

			GraphicsContext worker = GraphicsDevice.Current.CreateWorkerContext();
			if (worker == null)
			{
				why = "the device gave no context for a second thread";
				return null;
			}

			return new ShaderCompileThread(worker);
		}

		public void Enqueue(ShaderCompileOperation operation, bool gpuDriven)
		{
			lock (idle)
			{
				pending++;
				idle.Reset();
			}

			queue.Add((operation, gpuDriven));
		}

		/// <summary>Waits until every queued operation has run (true), at most the given time.</summary>
		public bool WaitIdle(int milliseconds)
		{
			return idle.Wait(milliseconds);
		}

		#endregion

		#region PrivateMethods

		private void Run()
		{
			ShaderCompilation.MarkCompileThread();
			bool configured = false;
			while (true)
			{
				(ShaderCompileOperation operation, bool gpuDriven) = queue.Take();
				context.MakeCurrent();
				if (!configured)
				{
					configured = true;
					// This thread waits for each program anyway: the driver compiles on it, not on threads of its own (on the
					// 780M neither way kept the main context's frames going: 13.7 s with 0, 17.3 s with 64 threads, both
					// ~19 frames; the helper process does that).
					GraphicsDevice.Current.SetParallelShaderCompile(false);
				}

				try
				{
					operation.RunOnCompileThread(gpuDriven);
				}
				finally
				{
					context.MakeNoneCurrent();
					lock (idle)
					{
						if (--pending == 0)
						{
							idle.Set();
						}
					}
				}
			}
		}

		#endregion
	}
}
