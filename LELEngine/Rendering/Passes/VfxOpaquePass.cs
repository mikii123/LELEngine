using LELEngine.VFX;
using LELEngine.Graphics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Draws the opaque and alpha-clipped outputs of the visual effects after the opaque geometry (depth tested
	///     and written, no blending), so they are in the depth buffer before the sky and the transparents.
	/// </summary>
	public sealed class VfxOpaquePass : RenderPass
	{
		#region PublicFields

		public override string Name => "VfxOpaque";

		public override RenderPassScope Scope => RenderPassScope.EveryCamera;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			if (VfxRuntime.Instances.Count == 0)
			{
				return;
			}

			CommandList commands = Gfx.Commands;
			context.SceneTarget.Begin();
			commands.State.SetDepth(true, true, CompareFunction.LessEqual);
			commands.State.Culling = FaceCulling.None;
			VfxRuntime.DrawOpaque(context);
			commands.State.SetDepth(true, true, CompareFunction.Less);
			commands.State.Culling = FaceCulling.Back;
			commands.SetProgram(null);
		}

		#endregion
	}
}
