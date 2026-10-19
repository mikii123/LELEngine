using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

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
			ShaderProgram program = sky?.UsingShader;
			if (program == null || !program.IsLinked)
			{
				return;
			}

			context.SceneTarget.Bind();
			GLState.SetDepth(true, false, DepthFunction.Lequal);
			GLState.SetCull(false);

			// Directions only: the view matrix without its translation (row-vector convention: translation in row 3).
			Matrix4 view = context.View;
			view.Row3 = new Vector4(0f, 0f, 0f, 1f);
			Matrix4 invViewProjection = (view * context.Projection).Inverted();

			program.Use();
			program.SetMatrix4("invViewProjection", invViewProjection);
			program.SetVector3("CamPosition", context.CameraPosition);
			sky.SetUniforms(program);
			context.Fullscreen.Draw();

			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true);
		}

		#endregion
	}
}
