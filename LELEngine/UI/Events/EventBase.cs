using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LELEngine.UIElements
{
	/// <summary>Where the event is in its propagation (trickle down from the root, at the target, bubble up).</summary>
	public enum PropagationPhase
	{
		None,
		TrickleDown,
		AtTarget,
		DefaultActionAtTarget,
		BubbleUp,
		DefaultAction
	}

	public enum TrickleDown
	{
		NoTrickleDown,
		TrickleDown
	}

	[Flags]
	public enum EventModifiers
	{
		None = 0,
		Shift = 1,
		Control = 2,
		Alt = 4,
		Command = 8,
		Numeric = 16,
		CapsLock = 32,
		FunctionKey = 64
	}

	/// <summary>Receives events (elements and anything else that can be an event target).</summary>
	public interface IEventHandler
	{
		void SendEvent(EventBase e);
		void HandleEvent(EventBase evt);
		bool HasTrickleDownHandlers();
		bool HasBubbleUpHandlers();
	}

	public delegate void EventCallback<in TEventType>(TEventType evt);

	public delegate void EventCallback<in TEventType, in TCallbackArgs>(TEventType evt, TCallbackArgs userArgs);

	/// <summary>
	///     Base of all UI events. Events are pooled: get one with <c>XxxEvent.GetPooled()</c>, send it with
	///     <see cref="IEventHandler.SendEvent" /> and dispose it (a <c>using</c> block) when done.
	/// </summary>
	public abstract class EventBase : IDisposable
	{
		#region PublicFields

		/// <summary>Time the event was created, in milliseconds since the engine started.</summary>
		public long timestamp { get; private set; }

		/// <summary>Unique id of the event type (<see cref="EventBase{T}.TypeId" />).</summary>
		public abstract long eventTypeId { get; }

		/// <summary>The element the event is sent to (set by the sender).</summary>
		public IEventHandler target { get; set; }

		/// <summary>The element whose callbacks are running right now.</summary>
		public IEventHandler currentTarget { get; internal set; }

		public PropagationPhase propagationPhase { get; internal set; }

		/// <summary>The event goes up from the target to the root after the target phase.</summary>
		public bool bubbles { get; protected set; }

		/// <summary>The event goes down from the root to the target before the target phase.</summary>
		public bool tricklesDown { get; protected set; }

		public bool isPropagationStopped { get; private set; }
		public bool isImmediatePropagationStopped { get; private set; }
		public bool isDefaultPrevented { get; private set; }

		/// <summary>The default actions of the target were prevented or skipped.</summary>
		public bool skipDisabledElements { get; set; } = true;

		/// <summary>Set while the dispatcher owns the event (sent and not yet finished).</summary>
		internal bool dispatched;

		/// <summary>The element under the pointer picked for this event (pointer events; set by the dispatcher).</summary>
		internal VisualElement elementTarget => target as VisualElement;

		/// <summary>The event was handled by an element's default action (informative).</summary>
		public bool isHandled { get; internal set; }

		#endregion

		#region PrivateFields

		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static long nextTypeId = 1;

		#endregion

		#region PublicMethods

		/// <summary>Stops the event from reaching the next elements of its propagation (callbacks of the current element still run).</summary>
		public void StopPropagation()
		{
			isPropagationStopped = true;
		}

		/// <summary>Stops the event now: no further callbacks, on this or any other element.</summary>
		public void StopImmediatePropagation()
		{
			isPropagationStopped = true;
			isImmediatePropagationStopped = true;
		}

		/// <summary>Prevents the default actions of the target (a button's click, a field's text edit, ...).</summary>
		public void PreventDefault()
		{
			isDefaultPrevented = true;
		}

		public virtual void Dispose()
		{ }

		#endregion

		#region InternalMethods

		internal static long RegisterEventType()
		{
			return nextTypeId++;
		}

		/// <summary>Resets the event to the state of a fresh one (pooled events reuse instances).</summary>
		protected virtual void Init()
		{
			timestamp = clock.ElapsedMilliseconds;
			target = null;
			currentTarget = null;
			propagationPhase = PropagationPhase.None;
			bubbles = false;
			tricklesDown = false;
			isPropagationStopped = false;
			isImmediatePropagationStopped = false;
			isDefaultPrevented = false;
			skipDisabledElements = true;
			dispatched = false;
			isHandled = false;
		}

		internal void ResetForDispatch()
		{
			isPropagationStopped = false;
			isImmediatePropagationStopped = false;
		}

		#endregion
	}

	/// <summary>Generic base giving every event type an id and a pool.</summary>
	public abstract class EventBase<T> : EventBase
		where T : EventBase<T>, new()
	{
		#region PublicFields

		public static long TypeId() => typeId;

		public override long eventTypeId => typeId;

		#endregion

		#region PrivateFields

		private static readonly long typeId = RegisterEventType();
		private static readonly Stack<T> pool = new Stack<T>();
		private bool pooled;

		#endregion

		#region PublicMethods

		/// <summary>An event instance from the pool, reset to its initial state.</summary>
		public static T GetPooled()
		{
			T evt;
			lock (pool)
			{
				evt = pool.Count > 0 ? pool.Pop() : new T();
			}

			evt.pooled = false;
			evt.Init();
			return evt;
		}

		/// <summary>Returns the event to the pool. Safe to call more than once.</summary>
		public override void Dispose()
		{
			if (pooled || dispatched)
			{
				return;
			}

			pooled = true;
			// A pooled event lets go of its targets: kept until its next use, they held the elements of the last play
			// mode (their panel, document and scene) after the editor left it.
			Init();
			lock (pool)
			{
				if (pool.Count < 64)
				{
					pool.Push((T)this);
				}
			}
		}

		#endregion
	}

	/// <summary>Stores the callbacks registered on an element, by event type and phase.</summary>
	internal sealed class EventCallbackRegistry
	{
		#region PrivateFields

		private readonly List<EventCallbackFunctorBase> callbacks = new List<EventCallbackFunctorBase>();
		private int trickleDownCount;
		private bool invoking;

		#endregion

		#region PublicMethods

		public bool HasTrickleDown => trickleDownCount > 0;
		public bool HasBubbleUp => callbacks.Count - trickleDownCount > 0;

		public void Register<TEventType>(EventCallback<TEventType> callback, TrickleDown trickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			long id = EventBase<TEventType>.TypeId();
			for (int i = 0; i < callbacks.Count; i++)
			{
				if (callbacks[i].eventTypeId == id && callbacks[i].trickleDown == trickleDown && callbacks[i].IsCallback(callback))
				{
					return;
				}

			}

			Add(new EventCallbackFunctor<TEventType>(id, callback, trickleDown));
		}

		public void Register<TEventType, TArgs>(EventCallback<TEventType, TArgs> callback, TArgs userArgs, TrickleDown trickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			long id = EventBase<TEventType>.TypeId();
			for (int i = 0; i < callbacks.Count; i++)
			{
				if (callbacks[i].eventTypeId == id && callbacks[i].trickleDown == trickleDown && callbacks[i].IsCallback(callback))
				{
					callbacks[i].SetUserArgs(userArgs);
					return;
				}
			}

			Add(new EventCallbackFunctor<TEventType, TArgs>(id, callback, userArgs, trickleDown));
		}

		public bool Unregister<TEventType>(EventCallback<TEventType> callback, TrickleDown trickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			return Remove(EventBase<TEventType>.TypeId(), callback, trickleDown);
		}

		public bool Unregister<TEventType, TArgs>(EventCallback<TEventType, TArgs> callback, TrickleDown trickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			return Remove(EventBase<TEventType>.TypeId(), callback, trickleDown);
		}

		public void Invoke(EventBase evt, bool trickleDownPhase)
		{
			TrickleDown wanted = trickleDownPhase ? TrickleDown.TrickleDown : TrickleDown.NoTrickleDown;
			long id = evt.eventTypeId;
			bool wasInvoking = invoking;
			invoking = true;
			try
			{
				// Callbacks may unregister themselves (or others): the list is copied on write while invoking.
				List<EventCallbackFunctorBase> list = callbacks;
				int count = list.Count;
				for (int i = 0; i < count && i < list.Count; i++)
				{
					EventCallbackFunctorBase functor = list[i];
					if (functor.eventTypeId != id || functor.trickleDown != wanted || functor.removed)
					{
						continue;
					}

					functor.Invoke(evt);
					if (evt.isImmediatePropagationStopped)
					{
						return;
					}
				}
			}
			finally
			{
				invoking = wasInvoking;
				if (!invoking)
				{
					callbacks.RemoveAll(c => c.removed);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void Add(EventCallbackFunctorBase functor)
		{
			callbacks.Add(functor);
			if (functor.trickleDown == TrickleDown.TrickleDown)
			{
				trickleDownCount++;
			}
		}

		private bool Remove(long id, Delegate callback, TrickleDown trickleDown)
		{
			for (int i = 0; i < callbacks.Count; i++)
			{
				EventCallbackFunctorBase functor = callbacks[i];
				if (functor.eventTypeId == id && functor.trickleDown == trickleDown && !functor.removed && functor.IsCallback(callback))
				{
					if (trickleDown == TrickleDown.TrickleDown)
					{
						trickleDownCount--;
					}

					if (invoking)
					{
						functor.removed = true;
					}
					else
					{
						callbacks.RemoveAt(i);
					}

					return true;
				}
			}

			return false;
		}

		#endregion
	}

	internal abstract class EventCallbackFunctorBase
	{
		public readonly long eventTypeId;
		public readonly TrickleDown trickleDown;
		public bool removed;

		protected EventCallbackFunctorBase(long eventTypeId, TrickleDown trickleDown)
		{
			this.eventTypeId = eventTypeId;
			this.trickleDown = trickleDown;
		}

		public abstract void Invoke(EventBase evt);
		public abstract bool IsCallback(Delegate callback);

		public virtual void SetUserArgs(object args)
		{ }
	}

	internal sealed class EventCallbackFunctor<TEventType> : EventCallbackFunctorBase
		where TEventType : EventBase<TEventType>, new()
	{
		private readonly EventCallback<TEventType> callback;

		public EventCallbackFunctor(long eventTypeId, EventCallback<TEventType> callback, TrickleDown trickleDown)
			: base(eventTypeId, trickleDown)
		{
			this.callback = callback;
		}

		public override void Invoke(EventBase evt)
		{
			callback((TEventType)evt);
		}

		public override bool IsCallback(Delegate other)
		{
			return callback == (Delegate)other || callback.Equals(other);
		}
	}

	internal sealed class EventCallbackFunctor<TEventType, TArgs> : EventCallbackFunctorBase
		where TEventType : EventBase<TEventType>, new()
	{
		private readonly EventCallback<TEventType, TArgs> callback;
		private TArgs userArgs;

		public EventCallbackFunctor(long eventTypeId, EventCallback<TEventType, TArgs> callback, TArgs userArgs, TrickleDown trickleDown)
			: base(eventTypeId, trickleDown)
		{
			this.callback = callback;
			this.userArgs = userArgs;
		}

		public override void Invoke(EventBase evt)
		{
			callback((TEventType)evt, userArgs);
		}

		public override bool IsCallback(Delegate other)
		{
			return callback.Equals(other);
		}

		public override void SetUserArgs(object args)
		{
			userArgs = (TArgs)args;
		}
	}

	/// <summary>
	///     Event handler with callback registration (<see cref="RegisterCallback{TEventType}" />) and overridable
	///     default actions. Elements derive from it.
	/// </summary>
	public abstract class CallbackEventHandler : IEventHandler
	{
		#region PrivateFields

		private EventCallbackRegistry registry;

		#endregion

		#region PublicMethods

		/// <summary>Runs <paramref name="callback" /> for events of that type reaching this element (bubble-up / target, or trickle-down).</summary>
		public void RegisterCallback<TEventType>(EventCallback<TEventType> callback, TrickleDown useTrickleDown = TrickleDown.NoTrickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			if (callback == null)
			{
				throw new ArgumentNullException(nameof(callback));
			}

			(registry ??= new EventCallbackRegistry()).Register(callback, useTrickleDown);
		}

		public void RegisterCallback<TEventType, TUserArgsType>(EventCallback<TEventType, TUserArgsType> callback, TUserArgsType userArgs, TrickleDown useTrickleDown = TrickleDown.NoTrickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			if (callback == null)
			{
				throw new ArgumentNullException(nameof(callback));
			}

			(registry ??= new EventCallbackRegistry()).Register(callback, userArgs, useTrickleDown);
		}

		public void UnregisterCallback<TEventType>(EventCallback<TEventType> callback, TrickleDown useTrickleDown = TrickleDown.NoTrickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			registry?.Unregister(callback, useTrickleDown);
		}

		public void UnregisterCallback<TEventType, TUserArgsType>(EventCallback<TEventType, TUserArgsType> callback, TrickleDown useTrickleDown = TrickleDown.NoTrickleDown)
			where TEventType : EventBase<TEventType>, new()
		{
			registry?.Unregister(callback, useTrickleDown);
		}

		public abstract void SendEvent(EventBase e);

		/// <summary>Runs the callbacks of the event's current phase, then the matching default handler.</summary>
		public virtual void HandleEvent(EventBase evt)
		{
			if (evt == null)
			{
				return;
			}

			switch (evt.propagationPhase)
			{
				case PropagationPhase.TrickleDown:
					registry?.Invoke(evt, true);
					if (!evt.isImmediatePropagationStopped && !evt.isDefaultPrevented)
					{
						HandleEventTrickleDown(evt);
					}

					break;
				case PropagationPhase.AtTarget:
					registry?.Invoke(evt, true);
					if (!evt.isImmediatePropagationStopped)
					{
						HandleEventTrickleDown(evt);
						registry?.Invoke(evt, false);
					}

					break;
				case PropagationPhase.DefaultActionAtTarget:
					if (!evt.isDefaultPrevented)
					{
						ExecuteDefaultActionAtTarget(evt);
					}

					break;
				case PropagationPhase.BubbleUp:
					registry?.Invoke(evt, false);
					if (!evt.isImmediatePropagationStopped && !evt.isDefaultPrevented)
					{
						HandleEventBubbleUp(evt);
					}

					break;
				case PropagationPhase.DefaultAction:
					if (!evt.isDefaultPrevented)
					{
						HandleEventBubbleUp(evt);
						ExecuteDefaultAction(evt);
					}

					break;
			}
		}

		public bool HasTrickleDownHandlers()
		{
			return registry != null && registry.HasTrickleDown;
		}

		public bool HasBubbleUpHandlers()
		{
			return registry != null && registry.HasBubbleUp;
		}

		#endregion

		#region ProtectedMethods

		/// <summary>Default handling in the trickle-down phase (ancestors first, then the target).</summary>
		protected virtual void HandleEventTrickleDown(EventBase evt)
		{ }

		/// <summary>Default handling in the bubble-up phase (the target first, then its ancestors).</summary>
		protected virtual void HandleEventBubbleUp(EventBase evt)
		{ }

		/// <summary>Default action of the target, run right after the target's callbacks (before bubbling).</summary>
		protected virtual void ExecuteDefaultActionAtTarget(EventBase evt)
		{ }

		/// <summary>Default action of the target, run after the whole propagation.</summary>
		protected virtual void ExecuteDefaultAction(EventBase evt)
		{ }

		#endregion
	}
}
