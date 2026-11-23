using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			// The view reads the field: built for it also while Lumen's rays trace the triangles (from the next frame).
			Lighting.RequestDistanceField();
			if (gi.GlobalSdf == null)
			{
				return;
			}

			// Compiled when the view is first shown.
			program ??= new ShaderProgram("Engine/SdfDebug.shader");

			context.SceneTarget.Begin();
			CommandList commands = Gfx.Commands;
			commands.State.SetDepth(false, false);
			commands.State.Culling = FaceCulling.None;

			program.Use();
			Lighting.SetSdfUniforms(program);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetVector3("cameraPosition", context.CameraPosition);
			// The view's own shading: from the first directional light that shines.
			program.SetVector3("lightDirection", Lighting.Directional.Direction.LengthSquared > 1e-12f ? Lighting.Directional.Direction : -Vector3.UnitY);

			context.Fullscreen.Draw();

			commands.State.SetDepth(true, true);
			commands.State.Culling = FaceCulling.Back;
		}

		public override void Dispose()
		{
			program?.Delete();
			program = null;
		}

		#endregion
	}
}
