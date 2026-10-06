using LELEngine.Shaders;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Screen probe placement view: draws the uniform probe anchors (white) and this frame's adaptive probes
	///     (red) over the scene. Disabled by default.
	/// </summary>
	public sealed class ProbeDebugPass : RenderPass
	{
		#region PublicFields

		public override string Name => "ProbeDebug";

		#endregion

		#region PrivateFields

		private ShaderProgram program;

		#endregion

		#region Constructors

		public ProbeDebugPass()
		{
			Enabled = false;
		}

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/ProbeDebug.shader");
		}

		public override void Execute(RenderContext context)
		{
			ScreenProbeGatherPass gather = context.Renderer.GetPass<ScreenProbeGatherPass>();
			if (gather == null || !gather.ProbesReady || !context.DepthPrepassDone)
			{
				return;
			}

			context.SceneTarget.Bind();
			GLState.SetDepth(false, false);
			GLState.SetCull(false);

			program.Use();
			gather.SetDebugUniforms(program, context);
			context.Fullscreen.Draw();

			GLState.SetDepth(true, true);
			GLState.SetCull(true);
		}

		public override void Dispose()
		{
			program?.Delete();
		}

		#endregion
	}
}
