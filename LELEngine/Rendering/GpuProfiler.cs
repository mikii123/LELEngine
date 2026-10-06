using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     GPU time per named section using GL_TIME_ELAPSED queries. Results are read back a few frames
	///     later so the CPU never stalls on the GPU; <see cref="Results" /> lags by up to three frames.
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

		#endregion

		#region PrivateFields

		private const int FramesInFlight = 3;

		private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
		private readonly List<string> order = new List<string>();
		private readonly List<KeyValuePair<string, double>> results = new List<KeyValuePair<string, double>>();
		private Entry active;
		private int frame;

		#endregion

		#region PublicMethods

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
				entries[name] = entry;
				order.Add(name);
			}

			int slot = frame % FramesInFlight;
			if (entry.Issued[slot])
			{
				// Result from three frames ago still pending; skip timing this frame rather than stall.
				return;
			}

			GL.BeginQuery(QueryTarget.TimeElapsed, entry.Queries[slot]);
			entry.Issued[slot] = true;
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
			frame++;

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

		public void Dispose()
		{
			foreach (Entry entry in entries.Values)
			{
				GL.DeleteQueries(FramesInFlight, entry.Queries);
			}
			entries.Clear();
			order.Clear();
		}

		#endregion

		#region NestedTypes

		private sealed class Entry
		{
			public readonly int[] Queries = new int[FramesInFlight];
			public readonly bool[] Issued = new bool[FramesInFlight];
			public double Milliseconds;
			public bool UsedThisFrame;
		}

		#endregion
	}
}
