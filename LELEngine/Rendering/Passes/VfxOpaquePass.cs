using LELEngine.VFX;
using OpenTK.Graphics.OpenGL4;

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

			context.SceneTarget.Bind();
			GLState.SetDepth(true, true, DepthFunction.Lequal);
			GLState.SetCull(false);
			VfxRuntime.DrawOpaque(context);
			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.Reset();
		}

		#endregion
	}
}
