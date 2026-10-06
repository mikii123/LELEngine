namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Invokes <see cref="Behaviour.PostRender" /> on every behaviour with the scene target bound,
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
			context.SceneTarget.Bind();
			GLState.SetDepth(true, true);

			foreach (Behaviour behaviour in context.Behaviours)
			{
				behaviour.PostRender();
				GLState.Reset();
			}
		}

		#endregion
	}
}
