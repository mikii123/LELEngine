using System;
using System.Collections.Generic;
using System.Reflection;

namespace LELEngine
{
	/// <summary>
	///     Which physics callbacks a component type overrides, cached per type: the world only builds collision data
	///     and calls components that handle the event. Cleared before a script assembly unloads.
	/// </summary>
	internal static class PhysicsCallbacks
	{
		#region PublicFields

		[Flags]
		public enum Mask
		{
			None = 0,
			CollisionEnter = 1,
			CollisionStay = 2,
			CollisionExit = 4,
			TriggerEnter = 8,
			TriggerStay = 16,
			TriggerExit = 32,
			JointBreak = 64,
			AnyCollision = CollisionEnter | CollisionStay | CollisionExit,
			AnyTrigger = TriggerEnter | TriggerStay | TriggerExit
		}

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly Dictionary<Type, Mask> cache = new Dictionary<Type, Mask>();

		private static readonly (string name, Mask flag)[] methods =
		{
			("OnCollisionEnter2D", Mask.CollisionEnter),
			("OnCollisionStay2D", Mask.CollisionStay),
			("OnCollisionExit2D", Mask.CollisionExit),
			("OnTriggerEnter2D", Mask.TriggerEnter),
			("OnTriggerStay2D", Mask.TriggerStay),
			("OnTriggerExit2D", Mask.TriggerExit),
			("OnJointBreak2D", Mask.JointBreak)
		};

		#endregion

		#region PublicMethods

		public static Mask Of(Type type)
		{
			lock (sync)
			{
				Mask mask;
				if (!cache.TryGetValue(type, out mask))
				{
					mask = Compute(type);
					cache[type] = mask;
				}

				return mask;
			}
		}

		public static bool Has(Behaviour component, Mask flag)
		{
			return (Of(component.GetType()) & flag) != 0;
		}

		/// <summary>True when any component of the object handles one of the events in the mask.</summary>
		public static bool AnyHandles(GameObject go, Mask flags)
		{
			if (go == null)
			{
				return false;
			}

			foreach (Behaviour component in go.Components)
			{
				if (!component.dead && (Of(component.GetType()) & flags) != 0)
				{
					return true;
				}
			}

			return false;
		}

		public static void ClearCache()
		{
			lock (sync)
			{
				cache.Clear();
			}
		}

		#endregion

		#region PrivateMethods

		private static Mask Compute(Type type)
		{
			Mask mask = Mask.None;
			foreach ((string name, Mask flag) in methods)
			{
				for (Type t = type; t != null && t != typeof(Behaviour); t = t.BaseType)
				{
					MethodInfo method = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
					if (method != null)
					{
						mask |= flag;
						break;
					}
				}
			}

			return mask;
		}

		#endregion
	}
}
