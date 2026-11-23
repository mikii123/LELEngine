using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Draws the environment's skybox material (<see cref="EnvironmentSettings.Skybox" />) behind the opaque
	///     geometry: a fullscreen triangle at the far plane whose fragments turn their screen position into a view
	///     direction ("invViewProjection", view without translation). The material's shader samples whatever it wants
	///     (engine shaders: Engine/Skybox.shader for cubemaps, Engine/SkyboxPanoramic.shader for panoramas).
	/// </summary>
	public sealed class SkyboxPass : RenderPass
	{
		#region PublicFields

		public override string Name => "Skybox";

		public override RenderPassScope Scope => RenderPassScope.BaseCamera;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			Material sky = Lighting.Environment?.Skybox;
			ShaderProgram program = sky?.Program;
			if (program == null || !program.IsLinked)
			{
				return;
			}

			CommandList commands = Gfx.Commands;
			context.SceneTarget.Begin();
			commands.State.SetDepth(true, false, CompareFunction.LessEqual);
			commands.State.Culling = FaceCulling.None;

			// Directions only: the view matrix without its translation (row-vector convention: translation in row 3).
			Matrix4 view = context.View;
			view.Row3 = new Vector4(0f, 0f, 0f, 1f);
			Matrix4 invViewProjection = (view * context.Projection).Inverted();

			program.Use();
			program.SetMatrix4("invViewProjection", invViewProjection);
			program.SetVector3("CamPosition", context.CameraPosition);
			sky.SetUniforms(program);
			context.Fullscreen.Draw();

			commands.State.SetDepth(true, true, CompareFunction.Less);
			commands.State.Culling = FaceCulling.Back;
		}

		#endregion
	}
}
