using LELEngine.VFX;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Runs this frame's GPU particle simulation (every visual effect's initialize and update kernels, batched across
	///     effects) before any pass draws particles or casts their shadows. The CPU half (spawners, parameters) ran in
	///     the effects' LateUpdate; the first renderer of the frame dispatches the work, the others find nothing left.
	/// </summary>
	public sealed class VfxSimulationPass : RenderPass
	{
		#region PublicFields

		public override string Name => "VfxSimulate";

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			VfxRuntime.RunSimulation();
		}

		#endregion
	}
}
