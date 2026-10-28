using System;
using System.Collections.Generic;
using System.Reflection;

namespace LELEngine.Animation
{
	/// <summary>
	///     Marker of a class an <see cref="AnimationEvent" /> may carry as its payload: any [Serializable] class
	///     implementing it can be chosen in the clip editor and is stored with its type (polymorphic), so an event can
	///     hand a receiver a whole data object instead of Unity's one float, one int and one string.
	/// </summary>
	public interface IAnimationEventPayload
	{ }

	/// <summary>
	///     A component that wants every animation event of its GameObject's <see cref="Animator" /> (in addition to
	///     the method named by the event).
	/// </summary>
	public interface IAnimationEventReceiver
	{
		void OnAnimationEvent(AnimationEvent animationEvent);
	}

	/// <summary>
	///     A marker on a clip's timeline: when playback passes its time, the method named <see cref="function" /> is
	///     called on every component of the animated GameObject (a parameterless method, or one taking the event, its
	///     payload, its object reference, float, int or string), <see cref="IAnimationEventReceiver" />s get it, and
	///     <see cref="Animator.AnimationEventFired" /> is raised.
	/// </summary>
	[Serializable]
	public sealed class AnimationEvent
	{
		#region PublicFields

		public float time;

		/// <summary>Name of the method to call (empty: only the receivers and the Animator's event see it).</summary>
		public string function = "";

		public float floatParameter;
		public int intParameter;
		public string stringParameter = "";
		public EngineObject objectReference;

		/// <summary>Any serializable object (a class implementing <see cref="IAnimationEventPayload" />), stored with its type.</summary>
		public IAnimationEventPayload payload;

		/// <summary>
		///     The event fires only while the clip's weight in the blend is at least this (0: whenever the clip is
		///     sampled at all, 1: only when it plays alone).
		/// </summary>
		[Range(0f, 1f)] public float minimumWeight;

		/// <summary>Set while the event is dispatched: the clip it belongs to.</summary>
		[NonSerialized] public AnimationClip clip;

		/// <summary>Set while the event is dispatched: the animator playing it.</summary>
		[NonSerialized] public Animator animator;

		/// <summary>Set while the event is dispatched: the clip's weight in the blend at that moment.</summary>
		[NonSerialized] public float animatorClipWeight;

		/// <summary>Set while the event is dispatched: the layer the clip played on.</summary>
		[NonSerialized] public int layerIndex;

		#endregion

		#region PublicMethods

		public AnimationEvent Clone()
		{
			return (AnimationEvent)MemberwiseClone();
		}

		public override string ToString()
		{
			return $"{function}@{time:0.###}";
		}

		#endregion
	}

	/// <summary>
	///     Delivers animation events to the components of a GameObject: the method named by the event (resolved once
	///     per component type and name, then cached), the <see cref="IAnimationEventReceiver" />s, and the animator's
	///     own event.
	/// </summary>
	public static class AnimationEventDispatcher
	{
		#region PublicFields

		/// <summary>Log a warning for an event whose function no component implements (once per type and name).</summary>
		public static bool WarnUnhandled = true;

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly Dictionary<(Type, string), Invoker> invokers = new Dictionary<(Type, string), Invoker>();
		private static readonly HashSet<(string, string)> warned = new HashSet<(string, string)>();

		private sealed class Invoker
		{
			public MethodInfo Method;
			public Kind ArgumentKind;
			public Type ParameterType;
		}

		private enum Kind
		{
			None,
			Event,
			Payload,
			Object,
			Float,
			Int,
			String,
			Bool
		}

		#endregion

		#region PublicMethods

		/// <summary>Sends the event to the GameObject's components; true when something received it by name or interface.</summary>
		public static bool Dispatch(GameObject target, AnimationEvent animationEvent)
		{
			if (target == null || animationEvent == null || target.IsDestroyed)
			{
				return false;
			}

			bool handled = false;
			bool byName = !string.IsNullOrEmpty(animationEvent.function);
			IReadOnlyList<Behaviour> components = target.Components;
			for (int i = 0; i < components.Count; i++)
			{
				Behaviour component = components[i];
				if (component == null || component.IsDestroyed || !component.enabled)
				{
					continue;
				}

				if (component is IAnimationEventReceiver receiver)
				{
					handled = true;
					try
					{
						receiver.OnAnimationEvent(animationEvent);
					}
					catch (Exception e)
					{
						Debug.LogException(e, component);
					}
				}

				if (byName && !(component is Animator))
				{
					Invoker invoker = FindInvoker(component.GetType(), animationEvent.function);
					if (invoker != null)
					{
						handled = true;
						Invoke(component, invoker, animationEvent);
					}
				}
			}

			if (!handled && byName && WarnUnhandled)
			{
				lock (sync)
				{
					if (warned.Add((target.Name, animationEvent.function)))
					{
						Debug.LogWarning($"[Animation] '{target.Name}' has no receiver for the animation event '{animationEvent.function}' (clip {animationEvent.clip?.name ?? "?"} at {animationEvent.time:0.###} s)", target);
					}
				}
			}

			return handled;
		}

		/// <summary>Drops the cached method lookups (script reload).</summary>
		public static void ClearCache()
		{
			lock (sync)
			{
				invokers.Clear();
				warned.Clear();
			}
		}

		#endregion

		#region PrivateMethods

		private static Invoker FindInvoker(Type type, string function)
		{
			lock (sync)
			{
				if (invokers.TryGetValue((type, function), out Invoker cached))
				{
					return cached;
				}

				Invoker found = null;
				foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (method.Name != function || method.IsGenericMethod)
					{
						continue;
					}

					ParameterInfo[] parameters = method.GetParameters();
					Invoker candidate = null;
					if (parameters.Length == 0)
					{
						candidate = new Invoker { Method = method, ArgumentKind = Kind.None };
					}
					else if (parameters.Length == 1)
					{
						Type p = parameters[0].ParameterType;
						if (p == typeof(AnimationEvent)) candidate = new Invoker { Method = method, ArgumentKind = Kind.Event };
						else if (p == typeof(float)) candidate = new Invoker { Method = method, ArgumentKind = Kind.Float };
						else if (p == typeof(int)) candidate = new Invoker { Method = method, ArgumentKind = Kind.Int };
						else if (p == typeof(string)) candidate = new Invoker { Method = method, ArgumentKind = Kind.String };
						else if (p == typeof(bool)) candidate = new Invoker { Method = method, ArgumentKind = Kind.Bool };
						else if (typeof(EngineObject).IsAssignableFrom(p)) candidate = new Invoker { Method = method, ArgumentKind = Kind.Object, ParameterType = p };
						else if (!p.IsValueType || p.IsDefined(typeof(SerializableAttribute), false)) candidate = new Invoker { Method = method, ArgumentKind = Kind.Payload, ParameterType = p };
					}

					// The most specific receiver wins: the event, then the payload, then a reference, then the primitives,
					// last a parameterless method.
					if (candidate != null && (found == null || Priority(candidate.ArgumentKind) < Priority(found.ArgumentKind)))
					{
						found = candidate;
					}
				}

				invokers[(type, function)] = found;
				return found;
			}
		}

		private static int Priority(Kind kind)
		{
			return kind == Kind.None ? int.MaxValue : (int)kind;
		}

		private static void Invoke(Behaviour component, Invoker invoker, AnimationEvent e)
		{
			try
			{
				switch (invoker.ArgumentKind)
				{
					case Kind.None:
						invoker.Method.Invoke(component, null);
						break;
					case Kind.Event:
						invoker.Method.Invoke(component, new object[] { e });
						break;
					case Kind.Payload:
						if (e.payload != null && !invoker.ParameterType.IsInstanceOfType(e.payload))
						{
							Debug.LogWarning($"[Animation] Event '{e.function}': {component.GetType().Name}.{invoker.Method.Name} takes {invoker.ParameterType.Name}, the payload is {e.payload.GetType().Name}", component);
							return;
						}

						invoker.Method.Invoke(component, new object[] { e.payload });
						break;
					case Kind.Object:
						if (e.objectReference != null && !invoker.ParameterType.IsInstanceOfType(e.objectReference))
						{
							return;
						}

						invoker.Method.Invoke(component, new object[] { e.objectReference });
						break;
					case Kind.Float:
						invoker.Method.Invoke(component, new object[] { e.floatParameter });
						break;
					case Kind.Int:
						invoker.Method.Invoke(component, new object[] { e.intParameter });
						break;
					case Kind.String:
						invoker.Method.Invoke(component, new object[] { e.stringParameter });
						break;
					case Kind.Bool:
						invoker.Method.Invoke(component, new object[] { e.intParameter != 0 });
						break;
				}
			}
			catch (TargetInvocationException te) when (te.InnerException != null)
			{
				Debug.LogException(te.InnerException, component);
			}
			catch (Exception ex)
			{
				Debug.LogException(ex, component);
			}
		}

		#endregion
	}
}
