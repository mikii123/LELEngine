using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LELEngine
{
	/// <summary>What a display of <see cref="Progress" /> shows: the task running now, if any (a copy: any thread may read it).</summary>
	public readonly struct ProgressState
	{
		#region Constructors

		internal ProgressState(bool running, string title, string info, float fraction, bool cancelable, bool cancelRequested, double seconds, long version)
		{
			Running = running;
			Title = title;
			Info = info;
			Fraction = fraction;
			Cancelable = cancelable;
			CancelRequested = cancelRequested;
			Seconds = seconds;
			Version = version;
		}

		#endregion

		#region PublicFields

		/// <summary>A task is running.</summary>
		public bool Running { get; }

		/// <summary>The task's title (the outermost one's, when tasks run inside others).</summary>
		public string Title { get; }

		/// <summary>What it does now (the innermost task's report), or null.</summary>
		public string Info { get; }

		/// <summary>How far it is, 0..1; negative when not known (a bar going back and forth).</summary>
		public float Fraction { get; }

		/// <summary>It can be canceled (<see cref="Progress.RequestCancel" />).</summary>
		public bool Cancelable { get; }

		/// <summary>A cancel was asked for and the task has not ended yet.</summary>
		public bool CancelRequested { get; }

		/// <summary>Seconds since the outermost task began.</summary>
		public double Seconds { get; }

		/// <summary>Changes with every begin, report and end.</summary>
		public long Version { get; }

		#endregion
	}

	/// <summary>
	///     Progress of long work (Unity's progress bar): imports, loads, the editor's tasks. A task is a <see cref="Scope" />
	///     from <see cref="Begin" /> to its <see cref="Scope.Dispose" />, reporting how far it is; a task begun inside another
	///     is a part of the outer one's current step (an outer <c>Report(3, 10)</c> gives the inner one the 0.3..0.4 of the
	///     whole bar). The editor shows the running task over its window from a thread of its own, so the bar moves while the
	///     work blocks the main thread; games show nothing. Any thread may report; reads are copies (<see cref="Current" />).
	/// </summary>
	public static class Progress
	{
		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly List<Scope> scopes = new List<Scope>();
		private static long version;
		private static long started;
		private static bool cancelRequested;

		#endregion

		#region PublicFields

		/// <summary>A task is running.</summary>
		public static bool IsRunning
		{
			get
			{
				lock (sync)
				{
					return scopes.Count > 0;
				}
			}
		}

		/// <summary>The running task as a display shows it (title, what it does, how far, since when).</summary>
		public static ProgressState Current
		{
			get
			{
				lock (sync)
				{
					if (scopes.Count == 0)
					{
						return new ProgressState(false, null, null, -1f, false, false, 0, version);
					}

					// The bar: each task places the next one inside its current step.
					double start = 0;
					double size = 1;
					bool known = false;
					bool placed = true;
					bool cancelable = false;
					foreach (Scope scope in scopes)
					{
						cancelable |= scope.cancelable;
						// A task whose bar is not known places none of those inside it.
						if (!placed || scope.fraction < 0f)
						{
							placed = false;
							continue;
						}

						start += size * scope.fraction;
						size *= scope.step;
						known = true;
					}

					Scope outer = scopes[0];
					Scope inner = scopes[scopes.Count - 1];
					string info = inner.info ?? (inner != outer ? inner.title : null);
					return new ProgressState(true, outer.title, info, known ? (float)Math.Clamp(start, 0, 1) : -1f, cancelable, cancelRequested,
						Stopwatch.GetElapsedTime(started).TotalSeconds, version);
				}
			}
		}

		/// <summary>
		///     Raised on the thread that reported (begin, report, end): the editor keeps its window answering the system while
		///     its main thread works through a long task.
		/// </summary>
		public static event Action Reported;

		#endregion

		#region PublicMethods

		/// <summary>Begins a task (end it with <see cref="Scope.Dispose" />, best in a using); its bar starts unknown until it reports.</summary>
		/// <param name="cancelable">The display offers Cancel; the task reads <see cref="Scope.Canceled" /> and stops when it is set.</param>
		public static Scope Begin(string title, bool cancelable = false)
		{
			var scope = new Scope(title, cancelable);
			lock (sync)
			{
				if (scopes.Count == 0)
				{
					started = Stopwatch.GetTimestamp();
					cancelRequested = false;
				}

				scopes.Add(scope);
				version++;
			}

			Reported?.Invoke();
			return scope;
		}

		/// <summary>The display's Cancel: the running cancelable task sees <see cref="Scope.Canceled" /> and ends early.</summary>
		public static void RequestCancel()
		{
			lock (sync)
			{
				if (scopes.Count > 0)
				{
					cancelRequested = true;
					version++;
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void Changed()
		{
			lock (sync)
			{
				version++;
			}

			Reported?.Invoke();
		}

		private static void End(Scope scope)
		{
			lock (sync)
			{
				if (!scopes.Remove(scope))
				{
					return;
				}

				if (scopes.Count == 0)
				{
					cancelRequested = false;
				}

				version++;
			}

			Reported?.Invoke();
		}

		private static bool IsCancelRequested()
		{
			lock (sync)
			{
				return cancelRequested;
			}
		}

		#endregion

		/// <summary>One running task (<see cref="Begin" />); disposing it ends it.</summary>
		public sealed class Scope : IDisposable
		{
			#region InternalFields

			internal readonly string title;
			internal readonly bool cancelable;
			internal string info;
			internal float fraction = -1f;

			// The part of the bar a task begun inside this one gets (one step of a counted task).
			internal float step;

			#endregion

			#region Constructors

			internal Scope(string title, bool cancelable)
			{
				this.title = title;
				this.cancelable = cancelable;
			}

			#endregion

			#region PublicFields

			/// <summary>The display's Cancel was clicked: stop at the next good place (and leave things as they would be after).</summary>
			public bool Canceled => IsCancelRequested();

			#endregion

			#region PublicMethods

			/// <summary>How far the task is (0..1, negative: not known) and what it does now (null: keeps the last).</summary>
			public void Report(float progress, string what = null)
			{
				lock (sync)
				{
					fraction = progress < 0f ? -1f : Math.Clamp(progress, 0f, 1f);
					step = 0f;
					if (what != null)
					{
						info = what;
					}
				}

				Changed();
			}

			/// <summary><paramref name="done" /> of <paramref name="total" /> parts finished, <paramref name="what" /> the next one.</summary>
			public void Report(int done, int total, string what = null)
			{
				lock (sync)
				{
					fraction = total > 0 ? Math.Clamp(done / (float)total, 0f, 1f) : -1f;
					step = total > 0 ? 1f / total : 0f;
					if (what != null)
					{
						info = total > 1 ? what + "  (" + Math.Min(done + 1, total) + " / " + total + ")" : what;
					}
				}

				Changed();
			}

			/// <summary>Ends the task.</summary>
			public void Dispose()
			{
				End(this);
			}

			#endregion
		}
	}
}
