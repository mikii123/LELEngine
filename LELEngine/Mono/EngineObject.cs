using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LELEngine
{
	/// <summary>
	///     Base of the scene objects: <see cref="GameObject" /> and every component (<see cref="Behaviour" />).
	///
	///     Destroying is two-phase. <c>Destroy()</c> kills the object (and a GameObject's whole subtree) for the game at
	///     once: <see cref="IsDestroyed" /> turns true, it gets no more Update / physics callbacks, queries and
	///     GetComponent skip it. At the end of the frame the engine runs OnDisable, OnDestroy and the
	///     <see cref="Destroyed" /> event for the whole batch, then releases the engine resources. While OnDestroy runs, the
	///     dying objects can still be used directly; after the end of the frame, using one throws
	///     <see cref="ObjectDestroyedException" />.
	///
	///     Game scripts see a destroyed object as null: the editor rewrites every null check in the compiled scripts
	///     (<c>?.</c>, <c>??</c>, <c>??=</c>, <c>== null</c>, <c>is null</c>, <c>is { }</c>, type patterns) into
	///     <see cref="IsAlive(EngineObject)" />, through interfaces and <c>object</c> too (<c>ReferenceEquals(x, null)</c>
	///     as well: C# compiles it the same way). <see cref="IsNullReference" /> is the plain reference test.
	/// </summary>
	[DebuggerDisplay("{DebuggerDisplay,nq}")]
	public abstract class EngineObject
	{
		#region PublicFields

		/// <summary>
		///     The editor and development runs record where Destroy was called, for <see cref="ObjectDestroyedException" />
		///     messages (a stack trace per Destroy call).
		/// </summary>
		public static bool RecordDestroyStackTraces;

		/// <summary>Destroy was called: the object is gone for the game (its end-of-frame cleanup may still be pending).</summary>
		public bool IsDestroyed => dead;

		/// <summary>
		///     Raised at the end of the frame the object was destroyed in, after its OnDestroy (for a GameObject: after
		///     the OnDestroy of its components). Exceptions of the handlers are logged.
		/// </summary>
		public event Action<EngineObject> Destroyed;

		#endregion

		#region InternalFields

		/// <summary>Destroy was called (the object is null for the game scripts).</summary>
		internal bool dead;

		/// <summary>The end-of-frame destroy ran: OnDestroy was called and the engine resources are released.</summary>
		internal bool destroyed;

		/// <summary>OnDestroy and the Destroyed event ran (they run once, before <see cref="destroyed" />).</summary>
		internal bool destroyCallbacksRun;

		/// <summary><see cref="Time.frameCount" /> of the Destroy call.</summary>
		internal long destroyFrame;

		/// <summary>Where Destroy was called (when <see cref="RecordDestroyStackTraces" />).</summary>
		internal string destroyTrace;

		/// <summary>
		///     The prefab instance this GameObject or component is part of (null: not part of one, or added to an
		///     instance); <see cref="prefabSourceId" /> is its id in the prefab (its own id is the instance's id XOR that).
		/// </summary>
		internal PrefabInstance prefabInstance;

		internal ulong prefabSourceId;

		#endregion

		#region PrivateFields

		private string DebuggerDisplay => dead ? DisplayName + " (destroyed)" : DisplayName;

		#endregion

		#region PublicMethods

		/// <summary>Not null and not destroyed.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool IsAlive(EngineObject obj)
		{
			return obj is not null && !obj.dead;
		}

		/// <summary>
		///     A plain reference test that also sees destroyed objects (every <c>== null</c> in game scripts, and
		///     <c>ReferenceEquals(x, null)</c>, which C# compiles the same way, treat them as null).
		/// </summary>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static bool IsNullReference(object obj)
		{
			return obj is null;
		}

		/// <summary>Not null, and not a destroyed engine object (the woven check for interfaces and object).</summary>
		[EditorBrowsable(EditorBrowsableState.Never)]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool IsAliveObject(object obj)
		{
			return obj is not null && (obj is not EngineObject engineObject || !engineObject.dead);
		}

		/// <summary>The woven <c>x == null</c> (one of the two is the null constant).</summary>
		[EditorBrowsable(EditorBrowsableState.Never)]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool NullCheckEquals(object a, object b)
		{
			return IsNullOrDestroyed(a) && IsNullOrDestroyed(b);
		}

		/// <summary>The woven <c>x != null</c> used as a value (cgt.un against the null constant).</summary>
		[EditorBrowsable(EditorBrowsableState.Never)]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static bool NullCheckNotEquals(object a, object b)
		{
			return !(IsNullOrDestroyed(a) && IsNullOrDestroyed(b));
		}

		#endregion

		#region InternalMethods

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool IsNullOrDestroyed(object obj)
		{
			return obj is null || (obj is EngineObject engineObject && engineObject.dead);
		}

		/// <summary>Name for messages and the debugger.</summary>
		internal abstract string DisplayName { get; }

		/// <summary>Marks the object dead (Destroy was called).</summary>
		internal void Kill(string trace)
		{
			dead = true;
			destroyFrame = Time.frameCount;
			destroyTrace = trace;
		}

		/// <summary>The caller of Destroy (frames of the engine's own Destroy overloads left out).</summary>
		internal static string CaptureDestroyTrace()
		{
			if (!RecordDestroyStackTraces)
			{
				return null;
			}

			StackFrame[] frames = new StackTrace(1, true).GetFrames();
			int first = 0;
			while (first < frames.Length - 1 && frames[first].GetMethod()?.DeclaringType?.Assembly == typeof(EngineObject).Assembly)
			{
				first++;
			}

			var text = new System.Text.StringBuilder();
			for (int i = first; i < frames.Length; i++)
			{
				System.Reflection.MethodBase method = frames[i].GetMethod();
				if (method == null)
				{
					continue;
				}

				text.Append("   at ").Append(method.DeclaringType?.FullName).Append('.').Append(method.Name);
				string file = frames[i].GetFileName();
				if (file != null)
				{
					text.Append(" in ").Append(file).Append(':').Append(frames[i].GetFileLineNumber());
				}

				text.Append('\n');
			}

			return text.ToString();
		}

		/// <summary>Throws when the object's end-of-frame destroy already ran.</summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal void ThrowIfDestroyed()
		{
			if (destroyed)
			{
				throw new ObjectDestroyedException(this);
			}
		}

		internal void RaiseDestroyed()
		{
			Action<EngineObject> handlers = Destroyed;
			Destroyed = null;
			if (handlers == null)
			{
				return;
			}

			foreach (Action<EngineObject> handler in handlers.GetInvocationList())
			{
				try
				{
					handler(this);
				}
				catch (Exception e)
				{
					Debug.LogException(e, this);
				}
			}
		}

		#endregion
	}

	/// <summary>A destroyed GameObject or component was used after the end of the frame it was destroyed in.</summary>
	public sealed class ObjectDestroyedException : InvalidOperationException
	{
		public ObjectDestroyedException(EngineObject obj)
			: base(BuildMessage(obj))
		{
			Object = obj;
		}

		/// <summary>The destroyed object.</summary>
		public EngineObject Object { get; }

		private static string BuildMessage(EngineObject obj)
		{
			string message = obj.DisplayName + " was destroyed in frame " + obj.destroyFrame + " and can no longer be used. "
				+ "Check it with '?.' or '== null' (destroyed objects are null for game scripts), or subscribe to its Destroyed event.";
			return obj.destroyTrace != null ? message + "\nDestroy was called at:\n" + obj.destroyTrace : message;
		}
	}

	/// <summary>
	///     Marks a script assembly whose null checks treat destroyed engine objects as null (written by the editor's
	///     weaver; see <see cref="EngineObject" />).
	/// </summary>
	[EditorBrowsable(EditorBrowsableState.Never)]
	[AttributeUsage(AttributeTargets.Assembly)]
	public sealed class NullChecksWovenAttribute : Attribute
	{
		public NullChecksWovenAttribute(int checks)
		{
			Checks = checks;
		}

		/// <summary>Number of null checks rewritten.</summary>
		public int Checks { get; }
	}
}
