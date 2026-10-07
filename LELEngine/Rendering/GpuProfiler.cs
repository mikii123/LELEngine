using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     GPU time per named section using GL_TIME_ELAPSED queries, plus GL_TIMESTAMP pairs per section and per
	///     frame that show the frame's GPU timeline: idle before the first section (head), between sections
	///     (gaps) and after the last one (tail). Results are read back a few frames later so the CPU never
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

		/// <summary>Per section: wall time minus our execution time (GPU given to other contexts), in Begin() order.</summary>
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
		///     Sum over sections of (end stamp - start stamp). Exceeds <see cref="TotalMilliseconds" /> by the time
		///     the GPU spent inside our sections on something else: other contexts (desktop compositor, other
		///     applications) or stalls that GL_TIME_ELAPSED does not attribute to us.
		/// </summary>
		public double SectionWallMilliseconds { get; private set; }

		/// <summary>Sum of the sections' GL_TIME_ELAPSED results from the same frame as the timeline stamps.</summary>
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

		private readonly int[] frameStartQueries = new int[FramesInFlight];
		private readonly int[] frameEndQueries = new int[FramesInFlight];
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
				GL.GenQueries(FramesInFlight, frameStartQueries);
				GL.GenQueries(FramesInFlight, frameEndQueries);
				frameQueriesCreated = true;
			}

			int slot = frame % FramesInFlight;
			if (frameIssued[slot])
			{
				return;
			}

			GL.QueryCounter(frameStartQueries[slot], QueryCounterTarget.Timestamp);
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
				GL.GenQueries(FramesInFlight, entry.Queries);
				GL.GenQueries(FramesInFlight, entry.StartStamps);
				GL.GenQueries(FramesInFlight, entry.EndStamps);
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

			GL.QueryCounter(entry.StartStamps[slot], QueryCounterTarget.Timestamp);
			GL.BeginQuery(QueryTarget.TimeElapsed, entry.Queries[slot]);
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

			GL.EndQuery(QueryTarget.TimeElapsed);
			GL.QueryCounter(active.EndStamps[frame % FramesInFlight], QueryCounterTarget.Timestamp);
			active = null;
		}

		/// <summary>
		///     Ends the current section and starts a new one. Lets a pass break its time into sub-sections
		///     (GL timer queries cannot nest). Does nothing when no section is active.
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
				GL.QueryCounter(frameEndQueries[slot], QueryCounterTarget.Timestamp);
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
				GL.DeleteQueries(FramesInFlight, entry.Queries);
				GL.DeleteQueries(FramesInFlight, entry.StartStamps);
				GL.DeleteQueries(FramesInFlight, entry.EndStamps);
			}
			entries.Clear();
			order.Clear();
			if (frameQueriesCreated)
			{
				GL.DeleteQueries(FramesInFlight, frameStartQueries);
				GL.DeleteQueries(FramesInFlight, frameEndQueries);
				frameQueriesCreated = false;
			}
		}

		#endregion

		#region PrivateMethods

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

					int available;
					GL.GetQueryObject(entry.Queries[slot], GetQueryObjectParam.QueryResultAvailable, out available);
					if (available == 0)
					{
						continue;
					}

					long nanoseconds;
					GL.GetQueryObject(entry.Queries[slot], GetQueryObjectParam.QueryResult, out nanoseconds);
					entry.Milliseconds = nanoseconds / 1_000_000.0;
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

				int available;
				GL.GetQueryObject(frameEndQueries[slot], GetQueryObjectParam.QueryResultAvailable, out available);
				if (available == 0)
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
					GL.GetQueryObject(entry.EndStamps[slot], GetQueryObjectParam.QueryResultAvailable, out available);
					if (available == 0)
					{
						complete = false;
						break;
					}
				}
				if (!complete)
				{
					continue;
				}

				long frameStart, frameEnd;
				GL.GetQueryObject(frameStartQueries[slot], GetQueryObjectParam.QueryResult, out frameStart);
				GL.GetQueryObject(frameEndQueries[slot], GetQueryObjectParam.QueryResult, out frameEnd);
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

					long start, end, elapsed;
					GL.GetQueryObject(entry.StartStamps[slot], GetQueryObjectParam.QueryResult, out start);
					GL.GetQueryObject(entry.EndStamps[slot], GetQueryObjectParam.QueryResult, out end);
					GL.GetQueryObject(entry.Queries[slot], GetQueryObjectParam.QueryResult, out elapsed);
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
			public readonly int[] Queries = new int[FramesInFlight];
			public readonly int[] StartStamps = new int[FramesInFlight];
			public readonly int[] EndStamps = new int[FramesInFlight];
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
