using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Resolves the HDR scene color to the window backbuffer: exposure, tonemapping, gamma.
	///     Further screen-space effects (SSAO, bloom, GI composition) plug in before this pass.
	/// </summary>
	public sealed class PostProcessPass : RenderPass
	{
		#region PublicFields

		public override string Name => "PostProcess";

		#endregion

		#region PrivateFields

		private ShaderProgram tonemap;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			tonemap = BuiltinShaders.CreateTonemap();
		}

		public override void Execute(RenderContext context)
		{
			context.Renderer.BindOutput();
			GLState.SetDepth(false, false);
			GLState.SetCull(false);

			tonemap.Use();
			tonemap.SetTexture("SceneColor", TextureTarget.Texture2D, context.SceneColor.Handle, 0);
			tonemap.SetFloat("Exposure", context.Settings.Exposure);
			tonemap.SetInt("TonemapEnabled", context.Settings.Tonemap ? 1 : 0);

			context.Fullscreen.Draw();

			GLState.SetDepth(true, true);
			GLState.SetCull(true);
		}

		public override void Dispose()
		{
			tonemap?.Delete();
			tonemap = null;
		}

		#endregion
	}
}
