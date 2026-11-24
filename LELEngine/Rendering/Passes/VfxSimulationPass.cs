using LELEngine.VFX;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Runs this frame's GPU particle simulation (every visual effect's initialize and update kernels, batched across
	///     effects) before any pass draws particles or casts their shadows. The CPU half (spawners, parameters) ran in
	///     the effects' LateUpdate; the first renderer of the frame dispatches the work, the others find nothing left.
	///     While the GI's rays trace the scene's triangles the effects that read the scene's surfaces trace the renderer's
	///     GI scene (its top level as the last frame built it) instead of a distance field (<see cref="VfxRuntime.SceneRays" />).
	/// </summary>
	public sealed class VfxSimulationPass : RenderPass
	{
		#region PublicFields

		public override string Name => "VfxSimulate";

		public override RenderPassScope Scope => RenderPassScope.Frame;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			Lumen.LumenScene scene = context.Renderer.LumenScene;
			VfxRuntime.SceneRays = Lighting.SceneRayTracingActive && scene?.RayTracing?.TopLevel != null ? scene : null;
			VfxRuntime.RunSimulation();
		}

		#endregion
	}
}
