using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Writes scene depth for all opaque geometry before shading. The opaque pass then
	///     runs with depth test LEQUAL and depth writes disabled, so every fragment is shaded at most once.
	/// </summary>
	public sealed class DepthPrepass : RenderPass
	{
		#region PublicFields

		public override string Name => "DepthPrepass";

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			if (!context.Settings.DepthPrepass)
			{
				context.DepthPrepassDone = false;
				return;
			}

			context.SceneTarget.Bind();
			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);

			context.DepthOnlyProgram.Use();
			context.DepthOnlyProgram.SetMatrix4("viewMatrix", context.View);
			context.DepthOnlyProgram.SetMatrix4("projectionMatrix", context.Projection);

			foreach (MeshRenderer renderer in context.Renderers)
			{
				renderer.RenderDepth(context.DepthOnlyProgram);
			}

			GLState.SetColorWrite(true);
			context.DepthPrepassDone = true;
		}

		#endregion
	}
}
