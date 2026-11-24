using System;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The renderer's use of the GPU's asynchronous compute queue: which frames give it work (<see cref="AsyncCompute" />,
	///     <see cref="AsyncComputeGuard" />), the passes that ask for it whole (<see cref="RenderPass.AsyncCompute" />) and
	///     the parts of passes that ask for it (<see cref="RenderContext.BeginAsyncCompute" />).
	/// </summary>
	public sealed partial class Renderer
	{
		#region PublicFields

		/// <summary>Whether this renderer stops using the compute queue while it does not pay, and what it decided.</summary>
		public AsyncComputeGuard AsyncGuard { get; } = new AsyncComputeGuard();

		/// <summary>
		///     The frame being rendered gives the work that asks for it to the compute queue: the device has one, the
		///     settings allow it (<see cref="RenderSettings.AsyncCompute" />) and it pays (<see cref="AsyncGuard" />).
		/// </summary>
		public bool AsyncCompute => asyncFrame;

		#endregion

		#region PrivateFields

		private bool asyncFrame;

		/// <summary>The guard's first choice was made (<see cref="AsyncComputeGuard.Start" />).</summary>
		private bool guardStarted;

		/// <summary>The pass being executed.</summary>
		private RenderPass executing;

		/// <summary>A part of the pass being executed runs on the compute queue (<see cref="BeginAsyncPart" />; a pass may have several, one after the other).</summary>
		private bool asyncPart;

		/// <summary>
		///     LEL_ASYNC_PASSES_OFF=name,name with LEL_AB=async: the passes given to the compute queue in the blocks the A/B
		///     switches off (instead of none), to compare two choices of passes in one run; "default": the passes' own
		///     declarations (wholes and parts).
		/// </summary>
		private static readonly string[] asyncPassesOff = Environment.GetEnvironmentVariable("LEL_ASYNC_PASSES_OFF")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		#endregion

		#region PrivateMethods

		/// <summary>The passes LEL_ASYNC_PASSES (or LEL_ASYNC_PASSES_OFF in an A/B's off block) names; null: those that ask.</summary>
		private string[] ChosenAsyncPasses => ABExperiment == "async" && ABOff && asyncPassesOff != null ? asyncPassesOff.Length == 1 && asyncPassesOff[0] == "default" ? null : asyncPassesOff
			: asyncPasses;

		/// <summary>Whether this frame gives work to the compute queue (at the frame's start, after the profiler began it).</summary>
		private void DecideAsyncCompute()
		{
			bool allowed = Settings.AsyncCompute && Gfx.Capabilities.AsyncCompute;
			if (ABExperiment == "async")
			{
				// The in-run A/B switches it itself (or between two choices of passes).
				asyncFrame = allowed && (!ABOff || asyncPassesOff != null);
				return;
			}

			if (allowed && !guardStarted)
			{
				// A GPU of its own starts with the compute queue, an integrated one without (tried, and kept when faster);
				// LEL_ASYNC_GUARD_START=on|off chooses (to measure the guard).
				guardStarted = true;
				string start = Environment.GetEnvironmentVariable("LEL_ASYNC_GUARD_START");
				AsyncGuard.Start(start == "on" || (start != "off" && !Gfx.Capabilities.IntegratedGpu));
			}

			asyncFrame = allowed && (!Settings.AsyncComputeGuard || AsyncGuard.Decide(FrameIndex));
		}

		/// <summary>
		///     The part of the pass being executed that follows runs its compute work on the compute queue, when this frame
		///     uses it and the pass is not given to it whole already; false when it does not. LEL_ASYNC_PASSES names the passes
		///     whose parts may (all of them when it is not set).
		/// </summary>
		internal bool BeginAsyncPart()
		{
			RenderPass pass = executing;
			if (!asyncFrame || asyncPart || pass == null || Gfx.Commands.InAsyncCompute)
			{
				return false;
			}

			string[] chosen = ChosenAsyncPasses;
			if (chosen != null && Array.IndexOf(chosen, pass.Name) < 0)
			{
				return false;
			}

			// The section open now timed the graphics queue: the pass's next split begins one on the compute queue.
			Profiler.Pause();
			Gfx.Commands.BeginAsyncCompute();
			asyncPart = true;
			return true;
		}

		/// <summary>Ends the part begun by <see cref="BeginAsyncPart" />: the profiler's section open in it (timed on the compute queue) ends first.</summary>
		internal void EndAsyncPart()
		{
			if (!asyncPart)
			{
				return;
			}

			asyncPart = false;
			Profiler.Pause();
			Gfx.Commands.EndAsyncCompute();
		}

		#endregion
	}
}
