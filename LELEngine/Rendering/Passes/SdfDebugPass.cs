using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Debug visualization: sphere-traces the global distance field from the camera and draws the hit
	///     surfaces shaded by their SDF normals. Disabled by default.
	/// </summary>
	public sealed class SdfDebugPass : RenderPass
	{
		#region PublicFields

		public override string Name => "SdfDebug";

		#endregion

		#region PrivateFields

		private ShaderProgram program;

		#endregion

		#region Constructors

		public SdfDebugPass()
		{
			Enabled = false;
		}

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/SdfDebug.shader");
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			if (gi.GlobalSdf == 0)
			{
				return;
			}

			context.SceneTarget.Bind();
			GLState.SetDepth(false, false);
			GLState.SetCull(false);

			program.Use();
			Lighting.SetSdfUniforms(program);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("lightDirection", DirectionalLight.This != null ? DirectionalLight.This.transform.forward : -Vector3.UnitY);

			context.Fullscreen.Draw();

			GLState.SetDepth(true, true);
			GLState.SetCull(true);
		}

		public override void Dispose()
		{
			program?.Delete();
			program = null;
		}

		#endregion
	}
}
