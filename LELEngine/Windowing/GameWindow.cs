using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace LELEngine.Windowing
{
	/// <summary>
	///     A window with a game loop: <see cref="Run" /> loads, then updates and renders a frame after another until
	///     the window closes.
	/// </summary>
	public class GameWindow : NativeWindow
	{
		private const double MaxFrequency = 500.0;

		private readonly Stopwatch watchUpdate = new Stopwatch();
		private double updateFrequency;
		private int slowUpdates;

		public event Action Load;
		public event Action Unload;
		public event Action<FrameEventArgs> UpdateFrame;
		public event Action<FrameEventArgs> RenderFrame;

		public GameWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
			: base(nativeWindowSettings)
		{
			UpdateFrequency = gameWindowSettings.UpdateFrequency;
		}

		/// <summary>The loop takes longer than its period (<see cref="VSyncMode.Adaptive" /> turns vsync off then).</summary>
		protected bool IsRunningSlowly { get; private set; }

		/// <summary>Seconds the last frame's interval took.</summary>
		public double UpdateTime { get; protected set; }

		/// <summary>Frames a second the loop aims for (1..500); 0 runs as fast as it can.</summary>
		public double UpdateFrequency
		{
			get => updateFrequency;
			set
			{
				if (value < 1.0)
				{
					updateFrequency = 0.0;
				}
				else
				{
					updateFrequency = Math.Min(value, MaxFrequency);
				}
			}
		}

		/// <summary>Milliseconds the system's sleep is accurate to (set by <see cref="Run" />).</summary>
		public int ExpectedSchedulerPeriod { get; set; } = 16;

		/// <summary>Runs the loop on the calling thread until the window closes.</summary>
		public virtual void Run()
		{
			// 8 ms: accurate enough for the loop's sleeps without the power cost of 1 ms timers.
			const int TimePeriod = 8;
			if (OperatingSystem.IsWindows())
			{
				// One core for the loop's thread: no timing jumps from moving between cores.
				SetThreadAffinityMask(GetCurrentThread(), new IntPtr(1));
				timeBeginPeriod(TimePeriod);
				ExpectedSchedulerPeriod = TimePeriod;
			}
			else
			{
				ExpectedSchedulerPeriod = 1;
			}

			// The context is current for the load and the first resize.
			Context?.MakeCurrent();
			OnLoad();
			OnResize(new ResizeEventArgs(ClientSize));

			watchUpdate.Start();
			while (!IsExiting)
			{
				double updatePeriod = UpdateFrequency == 0 ? 0 : 1 / UpdateFrequency;
				double elapsed = watchUpdate.Elapsed.TotalSeconds;
				if (elapsed > updatePeriod)
				{
					watchUpdate.Restart();
					NewInputFrame();
					ProcessWindowEvents(IsEventDriven);

					UpdateTime = elapsed;
					OnUpdateFrame(new FrameEventArgs(elapsed));
					OnRenderFrame(new FrameEventArgs(elapsed));

					const int MaxSlowUpdates = 80;
					const int SlowUpdatesThreshold = 45;
					if (updatePeriod < watchUpdate.Elapsed.TotalSeconds)
					{
						slowUpdates = Math.Min(slowUpdates + 1, MaxSlowUpdates);
					}
					else
					{
						slowUpdates = Math.Max(slowUpdates - 1, 0);
					}

					IsRunningSlowly = slowUpdates > SlowUpdatesThreshold;
					if (Context != null && VSync == VSyncMode.Adaptive)
					{
						Context.SwapInterval = IsRunningSlowly ? 0 : 1;
					}
				}

				double timeToNextUpdate = updatePeriod - watchUpdate.Elapsed.TotalSeconds;
				if (timeToNextUpdate > 0)
				{
					AccurateSleep(timeToNextUpdate, ExpectedSchedulerPeriod);
				}
			}

			OnUnload();
			if (OperatingSystem.IsWindows())
			{
				timeEndPeriod(TimePeriod);
			}
		}

		/// <summary>Presents what was drawn into the window's back buffer.</summary>
		public virtual void SwapBuffers()
		{
			if (Context == null)
			{
				throw new InvalidOperationException("A window without an OpenGL context has no buffers to swap: presentation is the graphics backend's.");
			}

			Context.SwapBuffers();
		}

		public override void Close()
		{
			base.Close();
		}

		protected virtual void OnLoad()
		{
			Load?.Invoke();
		}

		protected virtual void OnUnload()
		{
			Unload?.Invoke();
		}

		protected virtual void OnUpdateFrame(FrameEventArgs args)
		{
			UpdateFrame?.Invoke(args);
		}

		protected virtual void OnRenderFrame(FrameEventArgs args)
		{
			RenderFrame?.Invoke(args);
		}

		/// <summary>Seconds since the current frame's update started.</summary>
		public double TimeSinceLastUpdate()
		{
			return watchUpdate.Elapsed.TotalSeconds;
		}

		public void ResetTimeSinceLastUpdate()
		{
			watchUpdate.Restart();
		}

		/// <summary>Sleeps in whole scheduler periods, then yields until the time has passed.</summary>
		private static void AccurateSleep(double seconds, int expectedSchedulerPeriod)
		{
			const double Tolerance = 0.02;
			long target = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
			double milliseconds = seconds * 1000 - expectedSchedulerPeriod * Tolerance;
			int periods = (int)(milliseconds / expectedSchedulerPeriod);
			if (periods > 0)
			{
				Thread.Sleep(periods * expectedSchedulerPeriod);
			}

			while (Stopwatch.GetTimestamp() < target)
			{
				Thread.Yield();
			}
		}

		[DllImport("kernel32", SetLastError = true)]
		private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);

		[DllImport("kernel32")]
		private static extern IntPtr GetCurrentThread();

		[DllImport("winmm")]
		private static extern uint timeBeginPeriod(uint uPeriod);

		[DllImport("winmm")]
		private static extern uint timeEndPeriod(uint uPeriod);
	}
}
