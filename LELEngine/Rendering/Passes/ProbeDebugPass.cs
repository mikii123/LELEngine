using LELEngine.Graphics;
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

		public override void Execute(RenderContext context)
		{
			ScreenProbeGatherPass gather = context.Renderer.GetPass<ScreenProbeGatherPass>();
			if (gather == null || !gather.ProbesReady || !context.DepthPrepassDone)
			{
				return;
			}

			// Compiled when the view is first shown.
			program ??= new ShaderProgram("Engine/ProbeDebug.shader");

			CommandList commands = Gfx.Commands;
			context.SceneTarget.Begin();
			commands.State.SetDepth(false, false);
			commands.State.Culling = FaceCulling.None;

			program.Use();
			gather.SetDebugUniforms(program, context);
			context.Fullscreen.Draw();

			commands.State.SetDepth(true, true);
			commands.State.Culling = FaceCulling.Back;
		}

		public override void Dispose()
		{
			program?.Delete();
		}

		#endregion
	}
}
