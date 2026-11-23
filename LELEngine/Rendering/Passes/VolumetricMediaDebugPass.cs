using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Debug visualization of the volumetric objects in the GI (<see cref="Lighting.Media" />): what a ray of the GI
	///     from the camera sees of the clouds, over the GI's sky, in place of the scene. Disabled by default.
	/// </summary>
	public sealed class VolumetricMediaDebugPass : RenderPass
	{
		#region PublicFields

		public override string Name => "MediaDebug";

		/// <summary>
		///     Sixteen times this many voxel-long steps at the finest level a pixel's ray takes through the volume before
		///     the rest of the way is read as a cone of the GI reads it (0: all of it that way; many: the volume itself).
		/// </summary>
		public int Stretches = 24;

		/// <summary>
		///     The cone each pixel stands for in the coarse part (tan of its half angle): 0.002 a thin ray; 0.577 a diffuse
		///     cone of voxel cone tracing, 0.18 a ray of Lumen's screen probes (the picture is then the clouds blurred as
		///     wide as those are: its sum should stay what the thin rays' is).
		/// </summary>
		public float Aperture = 0.002f;

		#endregion

		#region PrivateFields

		private ShaderProgram program;

		#endregion

		#region Constructors

		public VolumetricMediaDebugPass()
		{
			Enabled = false;
		}

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			// Compiled when the view is first shown.
			program ??= new ShaderProgram("Engine/GIMediaDebug.shader");

			context.SceneTarget.Begin();
			CommandList commands = Gfx.Commands;
			commands.State.SetDepth(false, false);
			commands.State.Culling = FaceCulling.None;

			program.Use();
			Lighting.SetSkyUniforms(program);
			Lighting.SetMediaUniforms(program);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetInt("debugStretches", Stretches);
			program.SetFloat("debugAperture", Aperture);

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
