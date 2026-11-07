using LELEngine.Volumetrics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Makes and lights the volumetric objects' clouds that need it (<see cref="VolumetricRuntime.Update" />: a share of
	///     the work per frame, on the GPU), once per frame before the cameras draw. The objects themselves draw in the
	///     transparent pass.
	/// </summary>
	public sealed class VolumetricPass : RenderPass
	{
		public override string Name => "Volumetrics";

		public override RenderPassScope Scope => RenderPassScope.Frame;

		public override void Execute(RenderContext context)
		{
			if (VolumetricRuntime.Objects.Count > 0)
			{
				VolumetricRuntime.Update();
			}
		}
	}
}
