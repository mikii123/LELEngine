using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     The CPU side of a spawn context for one effect instance: starts and stops on events, runs its loop
	///     (optional delays and a loop count) and accumulates the particles its blocks ask for every frame.
	/// </summary>
	public sealed class VfxSpawnerState
	{
		#region PublicFields

		public readonly VfxSpawnerDescription Description;
		public bool Playing { get; private set; }

		/// <summary>Seconds since the current loop started.</summary>
		public float LoopTime { get; private set; }

		public int LoopIndex { get; private set; }

		#endregion

		#region PrivateFields

		private enum Phase
		{
			DelayBefore,
			Looping,
			DelayAfter,
			Finished
		}

		private Phase phase;
		private float phaseTime;
		private readonly float[] accumulators;
		private readonly bool[] burstDone;
		private readonly float[] nextBurst;

		#endregion

		#region Constructors

		public VfxSpawnerState(VfxSpawnerDescription description)
		{
			Description = description;
			accumulators = new float[description.Blocks.Count];
			burstDone = new bool[description.Blocks.Count];
			nextBurst = new float[description.Blocks.Count];
		}

		#endregion

		#region PublicMethods

		public void Start()
		{
			Playing = true;
			LoopIndex = 0;
			BeginLoop();
		}

		public void Stop()
		{
			Playing = false;
		}

		public void Reset()
		{
			Playing = false;
			LoopTime = 0f;
			LoopIndex = 0;
			phase = Phase.DelayBefore;
			phaseTime = 0f;
			Array.Clear(accumulators, 0, accumulators.Length);
		}

		/// <summary>Reacts to an event: starts when it is one of the start events, stops when it is one of the stop events.</summary>
		public void OnEvent(string eventName)
		{
			if (Description.StartEvents.Contains(eventName))
			{
				Start();
			}

			if (Description.StopEvents.Contains(eventName))
			{
				Stop();
			}
		}

		/// <summary>Advances the spawner by a frame; returns how many particles to spawn.</summary>
		public uint Advance(float deltaTime, VfxEvalContext context, float distanceMoved)
		{
			if (!Playing || deltaTime < 0f)
			{
				return 0;
			}

			context.SpawnerTime = LoopTime;
			bool constant = Description.LoopMode == VfxLoopMode.Constant;
			float duration = constant ? Math.Max(0f, Description.LoopDuration.Evaluate(context).Float) : float.PositiveInfinity;
			int loopCount = constant ? Description.LoopCount.Evaluate(context).Int : 0;
			float delayBefore = constant ? Math.Max(0f, Description.DelayBeforeLoop.Evaluate(context).Float) : 0f;
			float delayAfter = constant ? Math.Max(0f, Description.DelayAfterLoop.Evaluate(context).Float) : 0f;
			context.LoopProgress = float.IsInfinity(duration) || duration <= 0f ? 0f : Math.Clamp(LoopTime / duration, 0f, 1f);

			if (!constant)
			{
				phase = Phase.Looping;
			}

			uint spawned = 0;
			float remaining = deltaTime;
			int guard = 0;
			while (remaining > 0f && guard++ < 16)
			{
				switch (phase)
				{
					case Phase.DelayBefore:
					{
						float step = Math.Min(remaining, delayBefore - phaseTime);
						phaseTime += step;
						remaining -= step;
						if (phaseTime >= delayBefore - 1e-6f)
						{
							phase = Phase.Looping;
							phaseTime = 0f;
						}

						break;
					}
					case Phase.Looping:
					{
						float step = float.IsInfinity(duration) ? remaining : Math.Min(remaining, Math.Max(0f, duration - LoopTime));
						if (step <= 0f && !float.IsInfinity(duration))
						{
							step = 0f;
						}

						context.SpawnerTime = LoopTime;
						context.LoopProgress = float.IsInfinity(duration) || duration <= 0f ? 0f : Math.Clamp(LoopTime / duration, 0f, 1f);
						spawned += RunBlocks(step, context, distanceMoved * (deltaTime > 0f ? step / deltaTime : 0f));
						LoopTime += step;
						remaining -= step;
						if (!float.IsInfinity(duration) && LoopTime >= duration - 1e-6f)
						{
							phase = Phase.DelayAfter;
							phaseTime = 0f;
						}

						if (step <= 0f && !float.IsInfinity(duration))
						{
							// A zero-length loop: move on without spinning.
							phase = Phase.DelayAfter;
							phaseTime = 0f;
							if (remaining > 0f && delayAfter <= 0f && delayBefore <= 0f)
							{
								remaining = 0f;
							}
						}

						break;
					}
					case Phase.DelayAfter:
					{
						float step = Math.Min(remaining, delayAfter - phaseTime);
						phaseTime += step;
						remaining -= step;
						if (phaseTime >= delayAfter - 1e-6f)
						{
							LoopIndex++;
							if (loopCount > 0 && LoopIndex >= loopCount)
							{
								phase = Phase.Finished;
								Playing = false;
								return spawned;
							}

							BeginLoop();
						}

						break;
					}
					default:
						return spawned;
				}
			}

			return spawned;
		}

		#endregion

		#region PrivateMethods

		private void BeginLoop()
		{
			LoopTime = 0f;
			phase = Phase.DelayBefore;
			phaseTime = 0f;
			for (int i = 0; i < burstDone.Length; i++)
			{
				burstDone[i] = false;
				nextBurst[i] = float.NaN;
			}
		}

		private uint RunBlocks(float deltaTime, VfxEvalContext context, float distanceMoved)
		{
			uint total = 0;
			for (int i = 0; i < Description.Blocks.Count; i++)
			{
				VfxSpawnBlockDescription block = Description.Blocks[i];
				switch (block.Kind)
				{
					case VfxSpawnBlockKind.ConstantRate:
					case VfxSpawnBlockKind.VariableRate:
					{
						float rate = Math.Max(0f, block.Rate.Evaluate(context).Float);
						accumulators[i] += rate * deltaTime;
						uint n = (uint)Math.Floor(accumulators[i]);
						accumulators[i] -= n;
						total += n;
						break;
					}
					case VfxSpawnBlockKind.SpawnOverDistance:
					{
						float rate = Math.Max(0f, block.Rate.Evaluate(context).Float);
						accumulators[i] += rate * distanceMoved;
						uint n = (uint)Math.Floor(accumulators[i]);
						accumulators[i] -= n;
						total += n;
						break;
					}
					case VfxSpawnBlockKind.SingleBurst:
					{
						float delay = Math.Max(0f, block.Delay.Evaluate(context).Float);
						if (!burstDone[i] && LoopTime + deltaTime >= delay)
						{
							burstDone[i] = true;
							total += (uint)Math.Max(0, Math.Round(block.Count.Evaluate(context).Float));
						}

						break;
					}
					case VfxSpawnBlockKind.PeriodicBurst:
					{
						float delay = Math.Max(0f, block.Delay.Evaluate(context).Float);
						float period = Math.Max(0.001f, block.Period.Evaluate(context).Float);
						if (float.IsNaN(nextBurst[i]))
						{
							nextBurst[i] = delay;
						}

						int guard = 0;
						while (LoopTime + deltaTime >= nextBurst[i] && guard++ < 1024)
						{
							total += (uint)Math.Max(0, Math.Round(block.Count.Evaluate(context).Float));
							nextBurst[i] += period;
						}

						break;
					}
				}
			}

			return total;
		}

		#endregion
	}
}
