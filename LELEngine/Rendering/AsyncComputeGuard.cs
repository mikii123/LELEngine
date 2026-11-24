using System;
using System.Collections.Generic;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Decides frame by frame whether a renderer gives the work that asks for it to the GPU's asynchronous compute queue
	///     (<see cref="RenderPass.AsyncCompute" />, <see cref="RenderContext.BeginAsyncCompute" />): on while that pays,
	///     measured on the renderer's own frames. Every <see cref="ProbeInterval" /> frames, or at once when the frames grow
	///     by half, a block of <see cref="ProbeFrames" /> frames runs the other way, and the time of those frames (from one
	///     frame's start to the next: what the GPU's work, both queues' waits and the CPU's add up to; the first
	///     <see cref="SkippedFrames" /> of a block and after it left out, the other way's work still in flight) is compared
	///     with the frames just before and after the block (a drift of the frame times cancels out). Frames without the
	///     compute queue that are faster by <see cref="LargeMargin" /> in one block, by <see cref="Margin" /> in two in a
	///     row, or by <see cref="SmallMargin" /> on average over the last <see cref="AveragedBlocks" />, turn it off (another
	///     program on the GPU holds the compute queue back, or a GPU whose queues only slow each other down); then a block
	///     with it is tried after <see cref="HoldFrames" /> frames, twice as long after each block that finds it still slower
	///     (up to <see cref="MaxHoldFrames" />), and it is on again when its frames are faster by <see cref="SmallMargin" />.
	///     Under vsync the frames take the display's period either way, unless the compute queue makes them miss it. It
	///     starts on for a GPU of its own and off for an integrated one (<see cref="Start" />). The picture is the same
	///     either way.
	/// </summary>
	public sealed class AsyncComputeGuard
	{
		#region PublicFields

		/// <summary>Frames between the blocks that try the compute queue off while it is on.</summary>
		public const int ProbeInterval = 300;

		/// <summary>Frames of a block that tries the other way.</summary>
		public const int ProbeFrames = 12;

		/// <summary>The first frames of a block, and the first after it, whose time is not counted.</summary>
		public const int SkippedFrames = 2;

		/// <summary>Frames before and after a block it is compared with.</summary>
		public const int CompareFrames = 16;

		/// <summary>
		///     How much faster the frames without the compute queue have to be in two blocks in a row to turn it off (the
		///     second tried soon after the first: <see cref="ConfirmInterval" />): more than the frames' own noise.
		/// </summary>
		public const double Margin = 0.08;

		/// <summary>
		///     How much faster they have to be in one block to turn it off at once (another program holding the compute queue
		///     back made the 780M's frames 30 to 90 % longer with it).
		/// </summary>
		public const double LargeMargin = 0.20;

		/// <summary>
		///     How much faster they have to be on average over the last <see cref="AveragedBlocks" /> blocks to turn it off (a
		///     GPU where the queues side by side only slow each other a little), and how much faster the frames with it have
		///     to be to turn it on again.
		/// </summary>
		public const double SmallMargin = 0.03;

		public const int AveragedBlocks = 4;

		/// <summary>Frames to the block that confirms a first one that found the frames without the compute queue faster.</summary>
		public const int ConfirmInterval = 60;

		/// <summary>Frames the compute queue rests before it is tried again, after it was turned off.</summary>
		public const int HoldFrames = 600;

		public const int MaxHoldFrames = 9600;

		/// <summary>The compute queue is tried off at once when its frames take this many times their time at the last decision.</summary>
		public const double GrowthTrigger = 1.5;

		/// <summary>A frame that took longer than this (milliseconds: a pause, a load) is not counted.</summary>
		public const double LongestFrame = 250;

		/// <summary>The compute queue is used now (the choice between the blocks that try the other way).</summary>
		public bool On => steadyOn;

		/// <summary>
		///     The choice before anything was measured: on (a GPU of its own, where the queues side by side fill each other's
		///     idle units), or off (an integrated GPU, where they mostly share the same few: tried after
		///     <see cref="HoldFrames" /> frames and kept when faster). Set before the first <see cref="Decide" />.
		/// </summary>
		public void Start(bool on)
		{
			steadyOn = on;
			nextProbe = on ? ProbeInterval : HoldFrames;
		}

		/// <summary>Times the choice changed.</summary>
		public int Switches { get; private set; }

		/// <summary>Blocks that tried the other way.</summary>
		public int Probes { get; private set; }

		#endregion

		#region PrivateFields

		private const int Ring = 256;

		// What each recent frame was: its number, and 0 a frame of the choice, 1 of a block, 2 not counted.
		private readonly int[] ringFrame = new int[Ring];
		private readonly byte[] ringKind = new byte[Ring];

		/// <summary>The latest times of the choice's frames.</summary>
		private readonly List<double> steady = new List<double>();

		// The block being tried: its frames, and the choice's frames before and after it.
		private readonly List<double> probe = new List<double>();
		private readonly List<double> around = new List<double>();
		private int afterCount;

		private readonly List<double> sorted = new List<double>();

		private bool steadyOn = true;

		/// <summary>Blocks in a row that found the frames without the compute queue faster by <see cref="Margin" />.</summary>
		private int strikes;

		/// <summary>The time of the frames without the compute queue over those with it, of the last blocks while it is on.</summary>
		private readonly List<double> ratios = new List<double>();

		private int probeStart = -1;
		private int nextProbe = ProbeInterval;
		private int hold = HoldFrames;
		private int lastFrame = -1;
		private long lastStart;

		/// <summary>The median time of the choice's frames at the last decision (0: none yet).</summary>
		private double reference;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Whether frame <paramref name="frame" /> (counted by the renderer, one more each frame) uses the compute queue;
		///     called as the frame starts, it takes the time of the frame before (from its start to this one's).
		/// </summary>
		public bool Decide(int frame)
		{
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			if (lastFrame >= 0 && frame == lastFrame + 1)
			{
				Note(lastFrame, (now - lastStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
			}

			lastFrame = frame;
			lastStart = now;
			if (probeStart >= 0)
			{
				bool complete = probe.Count >= ProbeFrames - SkippedFrames && afterCount >= CompareFrames;
				bool late = frame - probeStart > ProbeFrames + CompareFrames + 60;
				if (complete || (late && probe.Count >= 4 && around.Count >= 6))
				{
					Evaluate(frame);
				}
				else if (late)
				{
					// Too few frames counted (pauses, loads): the choice stays.
					EndProbe(frame, ProbeInterval);
				}
			}
			else if (frame >= nextProbe || Grown())
			{
				probeStart = frame;
				probe.Clear();
				around.Clear();
				afterCount = 0;
				// The choice's frames just before the block.
				for (int i = Math.Max(0, steady.Count - CompareFrames); i < steady.Count; i++)
				{
					around.Add(steady[i]);
				}

				Probes++;
			}

			bool probing = probeStart >= 0 && frame < probeStart + ProbeFrames;
			int index = frame & (Ring - 1);
			ringFrame[index] = frame;
			if (probing)
			{
				ringKind[index] = (byte)(frame - probeStart < SkippedFrames ? 2 : 1);
			}
			else
			{
				// The first frames after a block still have its work in flight.
				ringKind[index] = (byte)(probeStart >= 0 && frame - (probeStart + ProbeFrames) < SkippedFrames ? 2 : 0);
			}

			return probing ? !steadyOn : steadyOn;
		}

		#endregion

		#region PrivateMethods

		/// <summary>Counts the time of a frame for what it was.</summary>
		private void Note(int frame, double milliseconds)
		{
			int slot = frame & (Ring - 1);
			if (ringFrame[slot] != frame || milliseconds <= 0 || milliseconds > LongestFrame)
			{
				return;
			}

			if (ringKind[slot] == 1 && probeStart >= 0 && frame >= probeStart)
			{
				probe.Add(milliseconds);
			}
			else if (ringKind[slot] == 0)
			{
				steady.Add(milliseconds);
				if (steady.Count > CompareFrames * 2)
				{
					steady.RemoveAt(0);
				}

				if (probeStart >= 0 && frame >= probeStart + ProbeFrames && afterCount < CompareFrames)
				{
					around.Add(milliseconds);
					afterCount++;
				}
			}
		}

		/// <summary>The frames of the choice (on) take much longer than at the last decision: try the other way now.</summary>
		private bool Grown()
		{
			if (!steadyOn || reference <= 0 || steady.Count < 8)
			{
				return false;
			}

			sorted.Clear();
			for (int i = steady.Count - 8; i < steady.Count; i++)
			{
				sorted.Add(steady[i]);
			}

			return Median(sorted) > reference * GrowthTrigger;
		}

		/// <summary>A block that tried the other way is over: the choice stays or changes.</summary>
		private void Evaluate(int frame)
		{
			double tried = Median(probe);
			double usual = Median(around);
			bool change;
			if (steadyOn)
			{
				// Off when much faster without it, faster by the margin twice in a row (the second block soon after), or a
				// little faster on average over the last blocks.
				double ratio = tried / usual;
				ratios.Add(ratio);
				if (ratios.Count > AveragedBlocks)
				{
					ratios.RemoveAt(0);
				}

				double mean = 0;
				foreach (double value in ratios)
				{
					mean += value / ratios.Count;
				}

				bool faster = ratio < 1 - Margin;
				strikes = faster ? strikes + 1 : 0;
				change = ratio < 1 - LargeMargin || strikes >= 2 || (ratios.Count == AveragedBlocks && mean < 1 - SmallMargin);
				if (faster && !change)
				{
					reference = usual;
					EndProbe(frame, ConfirmInterval);
					return;
				}
			}
			else
			{
				change = tried < usual * (1 - SmallMargin);
			}

			if (change)
			{
				steadyOn = !steadyOn;
				strikes = 0;
				ratios.Clear();
				Switches++;
				Console.WriteLine(steadyOn
					? $"[AsyncCompute] On again: frames with the compute queue {tried:0.00} ms against {usual:0.00} ms without (median frame time)"
					: $"[AsyncCompute] Off: frames without the compute queue {tried:0.00} ms against {usual:0.00} ms with it (median frame time); tried again in {hold} frames");
				// The block's frames are the new choice's first.
				steady.Clear();
				steady.AddRange(probe);
				reference = tried;
				if (steadyOn)
				{
					hold = HoldFrames;
				}

				EndProbe(frame, steadyOn ? ProbeInterval : hold);
				return;
			}

			reference = usual;
			if (!steadyOn)
			{
				hold = Math.Min(hold * 2, MaxHoldFrames);
			}

			EndProbe(frame, steadyOn ? ProbeInterval : hold);
		}

		private void EndProbe(int frame, int wait)
		{
			probeStart = -1;
			probe.Clear();
			around.Clear();
			afterCount = 0;
			nextProbe = frame + wait;
		}

		private double Median(List<double> values)
		{
			if (!ReferenceEquals(values, sorted))
			{
				sorted.Clear();
				sorted.AddRange(values);
			}

			sorted.Sort();
			return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
		}

		#endregion
	}
}
