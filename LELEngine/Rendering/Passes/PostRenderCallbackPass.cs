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
			CommandList commands = Gfx.Commands;
			context.SceneTarget.Begin();
			commands.State.SetDepth(true, true);

			RenderContext.Current = context;
			try
			{
				foreach (Behaviour behaviour in context.Behaviours)
				{
					behaviour.PostRender();
					// A callback that ended the pass (a dispatch, a copy) leaves it open for the next one.
					if (!commands.InRenderPass)
					{
						context.SceneTarget.Begin();
					}
				}
			}
			finally
			{
				RenderContext.Current = null;
			}
		}

		#endregion
	}
}
