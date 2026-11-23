using System.Collections.Generic;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     GPU time per named section from a pair of stamps of the GPU's clock (its start and its end), plus a pair
	///     per frame; together they show the frame's GPU timeline: idle before the first section (head), between
	///     sections (gaps) and after the last one (tail). Results are read back a few frames later so the CPU never
	///     stalls on the GPU; they lag by up to three frames.
	/// </summary>
	public sealed class GpuProfiler
	{
		#region PublicFields

		public bool Enabled = true;

		/// <summary>Latest known milliseconds per section, in Begin() order.</summary>
		public IReadOnlyList<KeyValuePair<string, double>> Results
		{
			get
			{
				results.Clear();
				foreach (string name in order)
				{
					results.Add(new KeyValuePair<string, double>(name, entries[name].Milliseconds));
				}
				return results;
			}
		}

		/// <summary>
		///     GPU idle time before each section (its start stamp minus the previous section's end stamp), in
		///     Begin() order. Shows where barriers and target switches drain the pipeline.
		/// </summary>
		public IReadOnlyList<KeyValuePair<string, double>> Gaps
		{
			get
			{
				gaps.Clear();
				foreach (string name in order)
				{
					gaps.Add(new KeyValuePair<string, double>(name, entries[name].GapBeforeMilliseconds));
				}
				return gaps;
			}
		}

		/// <summary>
		///     Per section: wall time minus our execution time (GPU given to other contexts), in Begin() order. A
		///     section's time is the wall time between its stamps, so this is 0.
		/// </summary>
		public IReadOnlyList<KeyValuePair<string, double>> Foreign
		{
			get
			{
				foreign.Clear();
				foreach (string name in order)
				{
					foreign.Add(new KeyValuePair<string, double>(name, entries[name].ForeignMilliseconds));
				}
				return foreign;
			}
		}

		public double TotalMilliseconds
		{
			get
			{
				double sum = 0;
				foreach (Entry entry in entries.Values)
				{
					sum += entry.Milliseconds;
				}
				return sum;
			}
		}

		/// <summary>GPU wall-clock time from the frame start stamp to the frame end stamp.</summary>
		public double FrameWallMilliseconds { get; private set; }

		/// <summary>
		///     Sum over sections of (end stamp - start stamp): it includes what the GPU spent inside our sections on
		///     something else (the desktop compositor, other applications).
		/// </summary>
		public double SectionWallMilliseconds { get; private set; }

		/// <summary>Sum of the sections' times from the same frame as the timeline stamps.</summary>
		public double SectionElapsedMilliseconds { get; private set; }

		/// <summary>GPU time between the frame start stamp and the first section (idle: waiting for commands).</summary>
		public double HeadMilliseconds { get; private set; }

		/// <summary>GPU time between the last section's end and the frame end stamp (idle: presentation back-pressure).</summary>
		public double TailMilliseconds { get; private set; }

		#endregion

		#region PrivateFields

		private const int FramesInFlight = 5;

		/// <summary>Sections that could not be timed this frame because their query from FramesInFlight frames ago was still pending.</summary>
		public int SkippedSections { get; private set; }

		private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
		private readonly List<string> order = new List<string>();
		private readonly List<KeyValuePair<string, double>> results = new List<KeyValuePair<string, double>>();
		private readonly List<KeyValuePair<string, double>> gaps = new List<KeyValuePair<string, double>>();
		private readonly List<KeyValuePair<string, double>> foreign = new List<KeyValuePair<string, double>>();
		private Entry active;
		private int frame;

		private readonly GpuTimestamp[] frameStartQueries = new GpuTimestamp[FramesInFlight];
		private readonly GpuTimestamp[] frameEndQueries = new GpuTimestamp[FramesInFlight];
		private readonly bool[] frameIssued = new bool[FramesInFlight];
		private readonly int[] frameOfSlot = new int[FramesInFlight];
		private bool frameQueriesCreated;
		private bool frameOpen;

		#endregion

		#region PublicMethods

		/// <summary>Marks the start of the frame on the GPU timeline. Call before the first pass.</summary>
		public void BeginFrame()
		{
			if (!Enabled)
			{
				return;
			}

			if (!frameQueriesCreated)
			{
				Create(frameStartQueries);
				Create(frameEndQueries);
				frameQueriesCreated = true;
			}

			int slot = frame % FramesInFlight;
			if (frameIssued[slot])
			{
				return;
			}

			Gfx.Commands.WriteTimestamp(frameStartQueries[slot]);
			frameOfSlot[slot] = frame;
			frameOpen = true;
		}

		public void Begin(string name)
		{
			if (!Enabled || active != null)
			{
				return;
			}

			Entry entry;
			if (!entries.TryGetValue(name, out entry))
			{
				entry = new Entry();
				Create(entry.StartStamps);
				Create(entry.EndStamps);
				entries[name] = entry;
				order.Add(name);
			}

			int slot = frame % FramesInFlight;
			if (entry.Issued[slot])
			{
				// Result from FramesInFlight frames ago still pending; skip timing this frame rather than stall.
				SkippedSections++;
				return;
			}

			Gfx.Commands.WriteTimestamp(entry.StartStamps[slot]);
			entry.Issued[slot] = true;
			entry.StampFrame[slot] = frame;
			entry.UsedThisFrame = true;
			active = entry;
		}

		public void End()
		{
			if (active == null)
			{
				return;
			}

			Gfx.Commands.WriteTimestamp(active.EndStamps[frame % FramesInFlight]);
			active = null;
		}

		/// <summary>
		///     Ends the current section and starts a new one. Lets a pass break its time into sub-sections
		///     (sections do not nest). Does nothing when no section is active.
		/// </summary>
		public void Split(string name)
		{
			if (active == null)
			{
				return;
			}

			End();
			Begin(name);
		}

		/// <summary>
		///     Collects finished queries. Call once per frame after all sections ended.
		/// </summary>
		public void EndFrame()
		{
			if (frameOpen)
			{
				int slot = frame % FramesInFlight;
				Gfx.Commands.WriteTimestamp(frameEndQueries[slot]);
				frameIssued[slot] = true;
				frameOpen = false;
			}

			frame++;

			CollectSections();
			CollectTimeline();
		}

		/// <summary>Resets the skipped-section counter (call when the stats line was printed).</summary>
		public void ResetCounters()
		{
			SkippedSections = 0;
		}

		public void Dispose()
		{
			foreach (Entry entry in entries.Values)
			{
				Delete(entry.StartStamps);
				Delete(entry.EndStamps);
			}
			entries.Clear();
			order.Clear();
			active = null;
			if (frameQueriesCreated)
			{
				Delete(frameStartQueries);
				Delete(frameEndQueries);
				frameQueriesCreated = false;
				System.Array.Clear(frameIssued);
				frameOpen = false;
			}
		}

		#endregion

		#region PrivateMethods

		private static void Create(GpuTimestamp[] stamps)
		{
			for (int i = 0; i < stamps.Length; i++)
			{
				stamps[i] = Gfx.Device.CreateTimestamp();
			}
		}

		private static void Delete(GpuTimestamp[] stamps)
		{
			for (int i = 0; i < stamps.Length; i++)
			{
				stamps[i]?.Dispose();
				stamps[i] = null;
			}
		}

		private void CollectSections()
		{
			foreach (Entry entry in entries.Values)
			{
				// A section that did not run this frame costs nothing; do not keep showing its old time.
				if (!entry.UsedThisFrame)
				{
					entry.Milliseconds = 0;
				}
				entry.UsedThisFrame = false;

				for (int slot = 0; slot < FramesInFlight; slot++)
				{
					if (!entry.Issued[slot])
					{
						continue;
					}

					// The end stamp is written after the start stamp: once it is there, both are.
					if (!entry.EndStamps[slot].IsAvailable)
					{
						continue;
					}

					long nanoseconds = entry.EndStamps[slot].Nanoseconds - entry.StartStamps[slot].Nanoseconds;
					entry.Milliseconds = System.Math.Max(0L, nanoseconds) / 1_000_000.0;
					entry.Issued[slot] = false;
				}
			}
		}

		// Frame timeline from one consistent slot: the frame stamps and every section stamped in that frame.
		private void CollectTimeline()
		{
			if (!frameQueriesCreated)
			{
				return;
			}

			for (int slot = 0; slot < FramesInFlight; slot++)
			{
				if (!frameIssued[slot])
				{
					continue;
				}

				if (!frameEndQueries[slot].IsAvailable)
				{
					continue;
				}

				// Every section stamped in this frame must have finished too (their end stamps precede ours,
				// but check rather than assume).
				bool complete = true;
				foreach (string name in order)
				{
					Entry entry = entries[name];
					if (entry.StampFrame[slot] != frameOfSlot[slot])
					{
						continue;
					}
					if (!entry.EndStamps[slot].IsAvailable)
					{
						complete = false;
						break;
					}
				}
				if (!complete)
				{
					continue;
				}

				long frameStart = frameStartQueries[slot].Nanoseconds;
				long frameEnd = frameEndQueries[slot].Nanoseconds;
				frameIssued[slot] = false;

				long firstStart = -1;
				long previousEnd = -1;
				double sectionWall = 0;
				double sectionElapsed = 0;
				foreach (string name in order)
				{
					Entry entry = entries[name];
					if (entry.StampFrame[slot] != frameOfSlot[slot])
					{
						entry.GapBeforeMilliseconds = 0;
						continue;
					}

					long start = entry.StartStamps[slot].Nanoseconds;
					long end = entry.EndStamps[slot].Nanoseconds;
					long elapsed = System.Math.Max(0L, end - start);
					if (firstStart < 0)
					{
						firstStart = start;
					}
					entry.GapBeforeMilliseconds = previousEnd > 0 && start > previousEnd ? (start - previousEnd) / 1_000_000.0 : 0;
					if (end > start)
					{
						sectionWall += (end - start) / 1_000_000.0;
						// Wall time of the section not spent executing our commands: preemption by other contexts.
						entry.ForeignMilliseconds = System.Math.Max(0.0, (end - start - elapsed) / 1_000_000.0);
					}
					sectionElapsed += elapsed / 1_000_000.0;
					previousEnd = end;
				}

				FrameWallMilliseconds = (frameEnd - frameStart) / 1_000_000.0;
				SectionWallMilliseconds = sectionWall;
				SectionElapsedMilliseconds = sectionElapsed;
				HeadMilliseconds = firstStart > frameStart ? (firstStart - frameStart) / 1_000_000.0 : 0;
				TailMilliseconds = previousEnd > 0 && frameEnd > previousEnd ? (frameEnd - previousEnd) / 1_000_000.0 : 0;
			}
		}

		#endregion

		#region NestedTypes

		private sealed class Entry
		{
			public readonly GpuTimestamp[] StartStamps = new GpuTimestamp[FramesInFlight];
			public readonly GpuTimestamp[] EndStamps = new GpuTimestamp[FramesInFlight];
			public readonly bool[] Issued = new bool[FramesInFlight];
			public readonly int[] StampFrame = new int[FramesInFlight];
			public double Milliseconds;
			public double GapBeforeMilliseconds;
			public double ForeignMilliseconds;
			public bool UsedThisFrame;

			public Entry()
			{
				for (int i = 0; i < FramesInFlight; i++)
				{
					StampFrame[i] = -1;
				}
			}
		}

		#endregion
	}
}
