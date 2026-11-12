using System;

namespace LELEngine
{
	/// <summary>
	///     How far each frame advances the game (<see cref="Time.deltaTime" />): the time between the frames the screen
	///     shows, made even. The median of the last intervals between presents (each with the jitter of the swap's return
	///     in it), and under vsync, when that median is a whole number of refresh periods, exactly that. What this takes
	///     away (a long frame: the time it was shown longer) is paid back a little every frame (a tenth of the step at
	///     most), so the game's clock stays with the real one and catches up without a jump. A stall longer than a
	///     quarter of a second is passed on as it is (nothing to make even).
	///     Snapping each interval on its own was tried first: when frames take longer than a refresh and the swap does
	///     not wait (a remote desktop's display), 36 ms frames snapped to 33.3 and the debt paid back made the steps
	///     uneven again.
	///     <para>
	///         Why: the game moved by the measured time of the frame before, with the CPU's noise in it, while the
	///         display shows frames at an even rate: under vsync at 60 Hz a camera flying at 78 m/s moved 0.9 m in one
	///         frame and 1.8 m in the next (an even 1.3 m was right), which the eye sees as stutter.
	///     </para>
	/// </summary>
	public sealed class FramePacing
	{
		#region PublicFields

		/// <summary>Seconds of one refresh of the display the window is on (0: not known).</summary>
		public double RefreshPeriod;

		/// <summary>Presents wait for the display's refresh.</summary>
		public bool VSync;

		/// <summary>A median interval within this share of a period from a whole number of periods is taken as that number.</summary>
		public const double SnapTolerance = 0.06;

		/// <summary>Intervals the median is taken of.</summary>
		public const int History = 8;

		/// <summary>Longer intervals are passed on as they are (and the history starts again).</summary>
		public const double MaxSmoothed = 0.25;

		/// <summary>The share of the time owed paid back each frame.</summary>
		public const double Payback = 0.1;

		/// <summary>Time owed up to this share of a step is not paid back (the measurements' own noise).</summary>
		public const double DebtDeadband = 0.5;

		/// <summary>Seconds the paced clock is behind the real one (what is still to be paid back).</summary>
		public double Debt => debt;

		#endregion

		#region PrivateFields

		private readonly double[] recent = new double[History];
		private readonly double[] sorted = new double[History];
		private int count;
		private int next;
		private double debt;

		#endregion

		#region PublicMethods

		/// <summary>The time the frame advances the game by, from <paramref name="interval" />: seconds between the last two presents.</summary>
		public double Next(double interval)
		{
			if (interval <= 0.0)
			{
				return 0.0;
			}

			if (interval > MaxSmoothed)
			{
				Reset();
				return interval;
			}

			// The median of the last intervals: the noise of single measurements goes, and a single long frame does
			// not change the step at all (the time it took is owed and paid back over the next frames).
			recent[next] = interval;
			next = (next + 1) % History;
			count = Math.Min(count + 1, History);
			if (count < History)
			{
				// The first frames (after the start, after a stall) as they are: a median of a few loading frames would
				// run the game fast for a while and then slow to pay it back.
				return interval;
			}

			Array.Copy(recent, sorted, count);
			Array.Sort(sorted, 0, count);
			double target = (count & 1) == 1 ? sorted[count / 2] : 0.5 * (sorted[count / 2 - 1] + sorted[count / 2]);

			// Under vsync a steady rate is a whole number of refreshes: exactly that. Not when the mean is off them (the
			// frames take longer than the refresh and the swap does not wait for it, a variable refresh rate display).
			if (VSync && RefreshPeriod > 0.0)
			{
				double periods = target / RefreshPeriod;
				double whole = Math.Max(1.0, Math.Round(periods));
				if (Math.Abs(periods - whole) <= SnapTolerance)
				{
					target = whole * RefreshPeriod;
				}
			}

			// The paid share is at most a tenth of the step, so the steps stay even. A debt within half a step is left
			// alone: the measurements' own noise (one present late, the next ones early: measured in a built game, 23.4
			// then 13.4 ms at 60 Hz) would otherwise make the steps uneven again; only lost frames and a drifting clock
			// run up more.
			debt += interval - target;
			double pay = Math.Abs(debt) > DebtDeadband * target ? Math.Clamp(debt * Payback, -0.1 * target, 0.1 * target) : 0.0;
			debt -= pay;
			return target + pay;
		}

		/// <summary>Forgets the history and the debt (a stall, a new window).</summary>
		public void Reset()
		{
			count = 0;
			next = 0;
			debt = 0.0;
		}

		#endregion
	}
}
