using System;
using System.Diagnostics;
using LELEngine.Rendering;

namespace LELEngine.Editor
{
	/// <summary>
	///     What the Game view's debug overlay (<see cref="GameViewOverlay" />) shows over time: one sample per editor frame
	///     (<see cref="EndFrame" />: the time since the frame before, the game's update on the CPU and what it allocated, the
	///     Game view's render on the CPU and on the GPU, garbage collections), the last <see cref="Capacity" /> kept for the
	///     graphs; <see cref="Summarize" /> averages the last second for the numbers. Collected whether the overlay shows or
	///     not (a few numbers a frame), so turning it on shows the seconds before.
	/// </summary>
	internal sealed class GameViewStats
	{
		#region PublicFields

		/// <summary>Samples kept: five seconds at 60 frames a second.</summary>
		public const int Capacity = 300;

		/// <summary>Per sample: the time since the frame before (ms).</summary>
		public readonly float[] FrameMs = new float[Capacity];

		/// <summary>Per sample: the game's update on the CPU (ms; scripts, physics, scene loads; 0 while paused).</summary>
		public readonly float[] UpdateMs = new float[Capacity];

		/// <summary>Per sample: the Game view's render on the CPU (ms; 0 when it did not render that frame).</summary>
		public readonly float[] RenderMs = new float[Capacity];

		/// <summary>Per sample: the Game view's render on the GPU (ms, the timer queries' latest: a few frames behind; 0 when it did not render).</summary>
		public readonly float[] GpuMs = new float[Capacity];

		/// <summary>Per sample: what the game's update allocated on the main thread (KB).</summary>
		public readonly float[] AllocatedKB = new float[Capacity];

		/// <summary>Per sample: the highest generation a garbage collection reached since the sample before, plus one (0: none).</summary>
		public readonly byte[] Collections = new byte[Capacity];

		/// <summary>Samples kept (up to <see cref="Capacity" />).</summary>
		public int Count { get; private set; }

		#endregion

		#region PrivateFields

		// The next sample's slot.
		private int next;
		private long lastFrame;
		private long updateStarted;
		private long updateAllocated;
		private float updateMs;
		private float allocatedKB;
		private int renderedFrame;
		private readonly int[] collectionCounts = new int[3];

		#endregion

		#region PublicMethods

		/// <summary>The game's update starts (play mode: the scenes' frame; edit mode: the [ExecuteAlways] components).</summary>
		public void BeginUpdate()
		{
			updateStarted = Stopwatch.GetTimestamp();
			updateAllocated = GC.GetAllocatedBytesForCurrentThread();
		}

		/// <summary>The game's update ended: its time and allocations go to this frame's sample.</summary>
		public void EndUpdate()
		{
			if (updateStarted == 0)
			{
				return;
			}

			updateMs += Milliseconds(updateStarted, Stopwatch.GetTimestamp());
			allocatedKB += (GC.GetAllocatedBytesForCurrentThread() - updateAllocated) / 1024f;
			updateStarted = 0;
		}

		/// <summary>
		///     The editor's frame ended (the views rendered): its sample, with the Game view's render when
		///     <paramref name="gameView" /> rendered a frame since the sample before.
		/// </summary>
		public void EndFrame(Renderer gameView)
		{
			long now = Stopwatch.GetTimestamp();
			int collected = CollectedGeneration();
			if (lastFrame == 0)
			{
				// The first frame has no interval: it only starts the clock.
				lastFrame = now;
				updateMs = 0f;
				allocatedKB = 0f;
				renderedFrame = gameView?.LastFrame.Frame ?? 0;
				return;
			}

			float render = 0f;
			float gpu = 0f;
			if (gameView != null && gameView.LastFrame.Frame != renderedFrame)
			{
				renderedFrame = gameView.LastFrame.Frame;
				render = (float)gameView.LastFrame.CpuMilliseconds;
				gpu = (float)gameView.Profiler.TotalMilliseconds;
			}

			FrameMs[next] = Milliseconds(lastFrame, now);
			UpdateMs[next] = updateMs;
			RenderMs[next] = render;
			GpuMs[next] = gpu;
			AllocatedKB[next] = allocatedKB;
			Collections[next] = (byte)collected;
			next = (next + 1) % Capacity;
			Count = Math.Min(Count + 1, Capacity);
			lastFrame = now;
			updateMs = 0f;
			allocatedKB = 0f;
		}

		/// <summary>The slot of sample <paramref name="index" /> (0: the oldest kept, <see cref="Count" /> - 1: the newest).</summary>
		public int Slot(int index)
		{
			return (next - Count + index + Capacity * 2) % Capacity;
		}

		/// <summary>The samples of the last <paramref name="seconds" /> (at least one when there is one), averaged.</summary>
		public Summary Summarize(float seconds)
		{
			var summary = new Summary { FrameMin = float.MaxValue };
			float covered = 0f;
			for (int i = Count - 1; i >= 0 && covered < seconds * 1000f; i--)
			{
				int slot = Slot(i);
				float frame = FrameMs[slot];
				covered += frame;
				summary.Frames++;
				summary.FrameAverage += frame;
				summary.FrameMin = Math.Min(summary.FrameMin, frame);
				summary.FrameMax = Math.Max(summary.FrameMax, frame);
				summary.Update += UpdateMs[slot];
				summary.AllocatedKB += AllocatedKB[slot];
				if (RenderMs[slot] > 0f)
				{
					summary.Rendered++;
					summary.Render += RenderMs[slot];
					summary.Gpu += GpuMs[slot];
				}
			}

			if (summary.Frames == 0)
			{
				summary.FrameMin = 0f;
				return summary;
			}

			summary.Seconds = covered / 1000f;
			summary.FrameAverage /= summary.Frames;
			summary.Update /= summary.Frames;
			summary.AllocatedKB /= summary.Frames;
			if (summary.Rendered > 0)
			{
				summary.Render /= summary.Rendered;
				summary.Gpu /= summary.Rendered;
			}

			return summary;
		}

		#endregion

		#region PrivateMethods

		private static float Milliseconds(long from, long to)
		{
			return (float)((to - from) * 1000.0 / Stopwatch.Frequency);
		}

		// The highest generation collected since the last call, plus one (0: none).
		private int CollectedGeneration()
		{
			int collected = 0;
			for (int generation = 0; generation < collectionCounts.Length; generation++)
			{
				int count = GC.CollectionCount(generation);
				if (count != collectionCounts[generation])
				{
					collectionCounts[generation] = count;
					collected = generation + 1;
				}
			}

			return collected;
		}

		#endregion

		#region NestedTypes

		/// <summary>Averages of the samples of a stretch of time (<see cref="Summarize" />).</summary>
		public struct Summary
		{
			/// <summary>Samples in the stretch and the time they cover.</summary>
			public int Frames;

			public float Seconds;

			/// <summary>Frame times: average, shortest, longest (ms).</summary>
			public float FrameAverage;

			public float FrameMin;
			public float FrameMax;

			/// <summary>The game's update a frame (ms), and what it allocated a frame (KB).</summary>
			public float Update;

			public float AllocatedKB;

			/// <summary>Frames the Game view rendered in the stretch, and their CPU and GPU time on average (ms).</summary>
			public int Rendered;

			public float Render;
			public float Gpu;

			public float FramesPerSecond => Seconds > 0f ? Frames / Seconds : 0f;
		}

		#endregion
	}
}
