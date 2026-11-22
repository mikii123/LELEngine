using System;
using System.Collections.Concurrent;
using System.Threading;
using Brutal.GlfwApi;
using Brutal.Numerics;
using LELEngine.Graphics;
using LELEngine.Windowing;

namespace LELEngine.Shaders
{
	/// <summary>
	///     The thread <see cref="ShaderCompilation" /> compiles on: a GL context of its own, shared with the main one (a
	///     hidden 1 x 1 GLFW window made on the main thread), so the main thread's frames go on while the driver compiles.
	///     Programs are built whole here (<see cref="ShaderProgram.StartBuild" /> on this thread compiles, links and ends
	///     with glFinish before the program counts as built), so the main context only ever sees finished programs. The
	///     context is current here only while an operation runs: GLFW may destroy the window (at exit) when it is idle.
	/// </summary>
	internal sealed class ShaderCompileThread
	{
		#region PrivateFields

		private readonly GlfwWindow window;
		private readonly Thread thread;
		private readonly BlockingCollection<(ShaderCompileOperation operation, bool gpuDriven)> queue = new BlockingCollection<(ShaderCompileOperation, bool)>();
		private readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);
		private int pending;

		#endregion

		#region Constructors

		private ShaderCompileThread(GlfwWindow window)
		{
			this.window = window;
			thread = new Thread(Run) { Name = "Shader compiler", IsBackground = true, Priority = ThreadPriority.BelowNormal };
			thread.Start();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Makes the thread with a context shared with the one current on this thread (the main thread: GLFW makes windows
		///     only there). Null when there is no context or the driver gives no shared one.
		/// </summary>
		public static ShaderCompileThread TryCreate(out string why)
		{
			why = null;
			NativeWindow current = NativeWindow.Current;
			if (current == null)
			{
				why = "no GL context on this thread";
				return null;
			}

			GL.GetInteger(GetPName.MajorVersion, out int major);
			GL.GetInteger(GetPName.MinorVersion, out int minor);
			Glfw.WindowHint(GlfwWindowHint.Visible, 0);
			Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_OPENGL_API);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, major);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, minor);
			Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_CORE_PROFILE);
			Glfw.WindowHint(GlfwWindowHint.OpenGlForwardCompat, 1);
			GlfwWindow hidden;
			try
			{
				hidden = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "LELEngine shader compiler", Size = new int2(1, 1), Share = current.Handle });
			}
			catch (InvalidOperationException)
			{
				// The window could not be made (GLFW's reason is in the log).
				hidden = null;
			}

			// GLFW's defaults back for the windows made later (the engine's windows set their own context hints anyway).
			Glfw.WindowHint(GlfwWindowHint.Visible, 1);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, 1);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, 0);
			Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_ANY_PROFILE);
			Glfw.WindowHint(GlfwWindowHint.OpenGlForwardCompat, 0);

			// glfwCreateWindow leaves the current context alone; checked all the same.
			if (Glfw.GetCurrentContext() != current.WindowPtr)
			{
				current.Context.MakeCurrent();
			}

			if (hidden == null)
			{
				why = "the driver gave no shared GL " + major + "." + minor + " context";
				return null;
			}

			return new ShaderCompileThread(hidden);
		}

		/// <summary>Queues an operation (main thread): it runs after the ones queued before.</summary>
		public void Enqueue(ShaderCompileOperation operation, bool gpuDriven)
		{
			lock (idle)
			{
				pending++;
				idle.Reset();
			}

			queue.Add((operation, gpuDriven));
		}

		/// <summary>Waits until the thread has nothing to do (dropped operations stop at their next variant); false on timeout.</summary>
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
				window.MakeContextCurrent();
				if (!configured)
				{
					configured = true;
					// This thread waits for each program anyway: the driver compiles on it, not on threads of its own (on the
					// 780M neither way kept the main context's frames going: 13.7 s with 0, 17.3 s with 64 threads, both
					// ~19 frames; the helper process does that).
					if (Rendering.GLCapabilities.ParallelShaderCompileKhr)
					{
						GL.Khr.MaxShaderCompilerThreads(0);
					}
					else if (Rendering.GLCapabilities.ParallelShaderCompileArb)
					{
						GL.Arb.MaxShaderCompilerThreads(0);
					}
				}

				try
				{
					operation.RunOnCompileThread(gpuDriven);
				}
				finally
				{
					Glfw.PInvoke.MakeContextCurrent(null);
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
