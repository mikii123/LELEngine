using LELEngine.Graphics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Invokes <see cref="Behaviour.PostRender" /> on every behaviour with a pass into the scene target open,
	///     so custom draws land in the HDR scene before post-processing.
	/// </summary>
	public sealed class PostRenderCallbackPass : RenderPass
	{
		#region PublicFields

		public override string Name => "PostRenderCallbacks";

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			Renderer.BeginPass(context.SceneTarget);
			Gfx.Commands.State.SetDepth(true, true);

			foreach (Behaviour behaviour in context.Behaviours)
			{
				behaviour.PostRender();
				// LEGACY(gl): a callback that gave OpenGL commands leaves nothing bound for the next one
				GLState.Reset();
				// A callback that ended the pass (a dispatch, a copy) leaves it open for the next one.
				Renderer.ContinuePass();
			}
		}

		#endregion
	}
}
