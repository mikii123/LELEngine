using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     "Lumen Scene" debug view: paints visible surfaces with their surface cache content
	///     (final lighting, or captured albedo). Disabled by default.
	/// </summary>
	public sealed class SurfaceCacheDebugPass : RenderPass
	{
		#region PublicFields

		public override string Name => "SurfaceCacheDebug";

		/// <summary>True shows the captured albedo instead of the cached lighting.</summary>
		public bool ShowAlbedo;

		#endregion

		#region PrivateFields

		private ShaderProgram program;

		// The program is the ray query variant (Lighting.HardwareRayTracingActive when it was made).
		private bool rayQueries;

		#endregion

		#region Constructors

		public SurfaceCacheDebugPass()
		{
			Enabled = false;
		}

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			LumenScene scene = context.Renderer.LumenScene;
			GlobalIlluminationSettings gi = Lighting.GI;
			if (scene.SurfaceCache == null || !Lighting.LumenTracingReady || !context.DepthPrepassDone)
			{
				return;
			}

			// Compiled when the view is first shown: the pixel's object from a short ray into the triangles, or from the
			// distance field's object ids.
			bool rays = Lighting.HardwareRayTracingActive;
			if (program != null && rayQueries != rays)
			{
				program.Delete();
				program = null;
			}

			rayQueries = rays;
			program ??= new ShaderProgram("Engine/SurfaceCacheDebug.shader", Lighting.RayTracingDefines);

			CommandList commands = Gfx.Commands;
			context.SceneTarget.Begin();
			commands.State.SetDepth(false, false);
			commands.State.Culling = FaceCulling.None;

			program.Use();
			scene.SetUniforms(program);
			Lighting.SetSdfUniforms(program);
			program.SetTexture("SceneDepth", context.SceneDepth);
			program.SetTexture("NormalRoughness", context.NormalRoughness);
			program.SetTexture("Lighting", ShowAlbedo ? scene.SurfaceCache.Albedo : scene.SurfaceCache.FinalLightingCurrent);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetFloat("searchDistance", rays ? SceneRayTracing.SurfaceReach : gi.SdfVoxelSize * 2f);

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
