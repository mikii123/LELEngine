using System;
using System.Collections.Concurrent;
using System.Threading;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;
using GlfwWindow = OpenTK.Windowing.GraphicsLibraryFramework.Window;

namespace LELEngine.Shaders
{
	/// <summary>
	///     The thread <see cref="ShaderCompilation" /> compiles on: a GL context of its own, shared with the main one (a
	///     hidden 1 x 1 GLFW window made on the main thread), so the main thread's frames go on while the driver compiles.
	///     Programs are built whole here (<see cref="ShaderProgram.StartBuild" /> on this thread compiles, links and ends
	///     with glFinish before the program counts as built), so the main context only ever sees finished programs. The
	///     context is current here only while an operation runs: GLFW may destroy the window (at exit) when it is idle.
	/// </summary>
	internal sealed unsafe class ShaderCompileThread
	{
		#region PrivateFields

		private readonly GlfwWindow* window;
		private readonly Thread thread;
		private readonly BlockingCollection<(ShaderCompileOperation operation, bool gpuDriven)> queue = new BlockingCollection<(ShaderCompileOperation, bool)>();
		private readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);
		private int pending;

		#endregion

		#region Constructors

		private ShaderCompileThread(GlfwWindow* window)
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
			GlfwWindow* current = GLFW.GetCurrentContext();
			if (current == null)
			{
				why = "no GL context on this thread";
				return null;
			}

			GL.GetInteger(GetPName.MajorVersion, out int major);
			GL.GetInteger(GetPName.MinorVersion, out int minor);
			GLFW.WindowHint(WindowHintBool.Visible, false);
			GLFW.WindowHint(WindowHintClientApi.ClientApi, ClientApi.OpenGlApi);
			GLFW.WindowHint(WindowHintInt.ContextVersionMajor, major);
			GLFW.WindowHint(WindowHintInt.ContextVersionMinor, minor);
			GLFW.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
			GLFW.WindowHint(WindowHintBool.OpenGLForwardCompat, true);
			GlfwWindow* hidden = GLFW.CreateWindow(1, 1, "LELEngine shader compiler", null, current);

			// GLFW's defaults back for the windows made later (OpenTK sets its own context hints for each anyway).
			GLFW.WindowHint(WindowHintBool.Visible, true);
			GLFW.WindowHint(WindowHintInt.ContextVersionMajor, 1);
			GLFW.WindowHint(WindowHintInt.ContextVersionMinor, 0);
			GLFW.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Any);
			GLFW.WindowHint(WindowHintBool.OpenGLForwardCompat, false);

			// glfwCreateWindow leaves the current context alone; checked all the same.
			if (GLFW.GetCurrentContext() != current)
			{
				GLFW.MakeContextCurrent(current);
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
				GLFW.MakeContextCurrent(window);
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
					GLFW.MakeContextCurrent(null);
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
