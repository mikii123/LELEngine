using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LELEngine.Animation
{
	/// <summary>
	///     The animators of one scene and their update: every frame (and fixed step, for AnimatePhysics animators)
	///     the system rebuilds stale players on the main thread, advances every animator (on worker threads with
	///     <see cref="Parallel" /> when there are at least <see cref="ParallelThreshold" /> of them; animators whose
	///     controller has script conditions stay on the main thread), then writes the poses and delivers the callbacks
	///     in scene order on the main thread.
	/// </summary>
	public sealed class AnimationSystem
	{
		#region PublicFields

		/// <summary>Advance animators on worker threads (LEL_ANIM_PARALLEL=0 switches it off).</summary>
		public static bool UseParallel = Environment.GetEnvironmentVariable("LEL_ANIM_PARALLEL") != "0";

		/// <summary>Fewer animators than this advance on the main thread (the threading costs more than it saves).</summary>
		public static int ParallelThreshold = 8;

		/// <summary>Milliseconds of the last update (advance and apply).</summary>
		public static double LastUpdateMilliseconds;

		public static double LastAdvanceMilliseconds;
		public static int LastAnimatorCount;
		public static bool LastParallel;

		public readonly Scene Scene;

		/// <summary>The animators registered in the scene (enabled, on active objects).</summary>
		public IReadOnlyList<Animator> Animators => animators;

		#endregion

		#region PrivateFields

		private readonly List<Animator> animators = new List<Animator>();
		private readonly HashSet<Animator> registered = new HashSet<Animator>();
		private readonly List<Animator> frame = new List<Animator>();
		private readonly List<Animator> parallelBatch = new List<Animator>();
		private readonly List<Animator> serialBatch = new List<Animator>();
		private readonly Stopwatch stopwatch = new Stopwatch();

		#endregion

		#region Constructors

		internal AnimationSystem(Scene scene)
		{
			Scene = scene;
		}

		#endregion

		#region PublicMethods

		/// <summary>The animation system of a scene (created on first use).</summary>
		public static AnimationSystem Of(Scene scene)
		{
			return scene?.AnimationSystem;
		}

		/// <summary>Runs one update of the scene's animators of an update mode (called by the scene's phases).</summary>
		public void Update(float deltaTime, AnimatorUpdateMode mode)
		{
			if (animators.Count == 0)
			{
				return;
			}

			stopwatch.Restart();
			frame.Clear();
			parallelBatch.Clear();
			serialBatch.Clear();
			foreach (Animator animator in animators)
			{
				if (animator == null || animator.IsDestroyed || !animator.isActiveAndEnabled || animator.updateMode != mode)
				{
					continue;
				}

				// Stale players rebuild here, on the main thread (controllers and clips edited live, hierarchy changes).
				animator.EnsureBuilt();
				AnimatorPlayer player = animator.Player;
				if (player == null || !player.IsBuilt)
				{
					continue;
				}

				frame.Add(animator);
				if (animator.allowParallelUpdate && !player.Controller.HasScriptConditions)
				{
					parallelBatch.Add(animator);
				}
				else
				{
					serialBatch.Add(animator);
				}
			}

			bool parallel = UseParallel && parallelBatch.Count >= Math.Max(ParallelThreshold, 2);
			if (parallel)
			{
				List<Animator> batch = parallelBatch;
				Parallel.For(0, batch.Count, i => batch[i].Player.Advance(deltaTime));
			}
			else
			{
				foreach (Animator animator in parallelBatch)
				{
					animator.Player.Advance(deltaTime);
				}
			}

			foreach (Animator animator in serialBatch)
			{
				animator.Player.Advance(deltaTime);
			}

			LastAdvanceMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

			// Scene order for the writes and the callbacks, whatever thread advanced them.
			foreach (Animator animator in frame)
			{
				AnimatorPlayer player = animator.Player;
				if (player.LastError != null)
				{
					Debug.LogException(player.LastError, animator);
					continue;
				}

				try
				{
					player.Apply();
				}
				catch (Exception e)
				{
					Debug.LogException(e, animator);
				}

				animator.LogPending();
			}

			LastUpdateMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
			LastAnimatorCount = frame.Count;
			LastParallel = parallel;
		}

		#endregion

		#region InternalMethods

		internal static void Register(Animator animator)
		{
			Scene scene = animator?.gameObject?.scene;
			if (scene == null)
			{
				return;
			}

			AnimationSystem system = scene.AnimationSystem;
			if (system.registered.Add(animator))
			{
				system.animators.Add(animator);
			}
		}

		internal static void Unregister(Animator animator)
		{
			Scene scene = animator?.gameObject?.scene;
			AnimationSystem system = scene?.animation;
			if (system != null && system.registered.Remove(animator))
			{
				system.animators.Remove(animator);
			}
		}

		internal void Clear()
		{
			animators.Clear();
			registered.Clear();
			frame.Clear();
		}

		#endregion
	}
}
