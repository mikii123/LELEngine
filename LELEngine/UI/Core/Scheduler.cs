using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>A scheduled callback (returned by <see cref="IVisualElementScheduler.Execute(Action)" />).</summary>
	public interface IVisualElementScheduledItem
	{
		VisualElement element { get; }
		bool isActive { get; }

		/// <summary>Starts (or restarts) the item.</summary>
		void Resume();

		void Pause();

		/// <summary>Delays the first execution.</summary>
		IVisualElementScheduledItem StartingIn(long delayMs);

		/// <summary>Repeats every interval until paused or <see cref="Until" /> says so.</summary>
		IVisualElementScheduledItem Every(long intervalMs);

		IVisualElementScheduledItem Until(Func<bool> stopCondition);

		/// <summary>Repeats for this long, then stops.</summary>
		IVisualElementScheduledItem ForDuration(long durationMs);
	}

	/// <summary>Timing information handed to scheduled callbacks.</summary>
	public struct TimerState
	{
		public long start;
		public long now;
		public long deltaTime => now - start;
	}

	public interface IVisualElementScheduler
	{
		IVisualElementScheduledItem Execute(Action<TimerState> timerUpdateEvent);
		IVisualElementScheduledItem Execute(Action updateEvent);
	}

	/// <summary>Per-element scheduler: items run from the panel's update while the element is attached.</summary>
	internal sealed class VisualElementScheduler : IVisualElementScheduler
	{
		#region PrivateFields

		private readonly VisualElement element;
		internal readonly List<ScheduledItem> items = new List<ScheduledItem>();

		#endregion

		#region Constructors

		public VisualElementScheduler(VisualElement element)
		{
			this.element = element;
		}

		#endregion

		#region PublicMethods

		public IVisualElementScheduledItem Execute(Action<TimerState> timerUpdateEvent)
		{
			var item = new ScheduledItem(element, timerUpdateEvent);
			items.Add(item);
			item.Resume();
			return item;
		}

		public IVisualElementScheduledItem Execute(Action updateEvent)
		{
			return Execute(_ => updateEvent());
		}

		/// <summary>Runs the due items; false when none is active any more.</summary>
		public bool Update(long nowMs)
		{
			bool anyActive = false;
			for (int i = 0; i < items.Count; i++)
			{
				ScheduledItem item = items[i];
				if (!item.isActive)
				{
					continue;
				}

				item.Tick(nowMs);
				anyActive |= item.isActive;
			}

			items.RemoveAll(item => !item.isActive && item.finished);
			return anyActive;
		}

		#endregion
	}

	internal sealed class ScheduledItem : IVisualElementScheduledItem
	{
		#region PublicFields

		public VisualElement element { get; }
		public bool isActive { get; private set; }
		public bool finished;

		#endregion

		#region PrivateFields

		private readonly Action<TimerState> callback;
		private long delay;
		private long interval = -1;
		private long duration = -1;
		private Func<bool> stopCondition;
		private long startTime = -1;
		private long nextTime;
		private long lastTime;

		#endregion

		#region Constructors

		public ScheduledItem(VisualElement element, Action<TimerState> callback)
		{
			this.element = element;
			this.callback = callback;
		}

		#endregion

		#region PublicMethods

		public void Resume()
		{
			isActive = true;
			finished = false;
			startTime = -1;
			element.elementPanel?.RegisterScheduler(element);
		}

		public void Pause()
		{
			isActive = false;
		}

		public IVisualElementScheduledItem StartingIn(long delayMs)
		{
			delay = Math.Max(0, delayMs);
			startTime = -1;
			return this;
		}

		public IVisualElementScheduledItem Every(long intervalMs)
		{
			interval = Math.Max(0, intervalMs);
			return this;
		}

		public IVisualElementScheduledItem Until(Func<bool> stopCondition)
		{
			this.stopCondition = stopCondition;
			return this;
		}

		public IVisualElementScheduledItem ForDuration(long durationMs)
		{
			duration = durationMs;
			return this;
		}

		public void Tick(long now)
		{
			if (startTime < 0)
			{
				startTime = now;
				lastTime = now;
				nextTime = now + delay;
			}

			if (now < nextTime)
			{
				return;
			}

			var state = new TimerState { start = lastTime, now = now };
			lastTime = now;
			try
			{
				callback(state);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}

			bool stop = interval < 0 || (stopCondition != null && stopCondition()) || (duration >= 0 && now - startTime >= duration);
			if (stop)
			{
				isActive = false;
				finished = true;
				return;
			}

			nextTime = now + interval;
		}

		#endregion
	}
}
