using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

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

		#endregion

		#region Constructors

		public SurfaceCacheDebugPass()
		{
			Enabled = false;
		}

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/SurfaceCacheDebug.shader");
		}

		public override void Execute(RenderContext context)
		{
			LumenScene scene = context.Renderer.LumenScene;
			GlobalIlluminationSettings gi = Lighting.GI;
			if (scene.SurfaceCache == null || gi.GlobalSdf == 0 || !context.DepthPrepassDone)
			{
				return;
			}

			context.SceneTarget.Bind();
			GLState.SetDepth(false, false);
			GLState.SetCull(false);

			program.Use();
			scene.SetUniforms(program);
			Lighting.SetSdfUniforms(program);
			program.SetTexture("SceneDepth", TextureTarget.Texture2D, context.SceneDepth.Handle, 0);
			program.SetTexture("NormalRoughness", TextureTarget.Texture2D, context.NormalRoughness.Handle, 1);
			program.SetTexture("Lighting", TextureTarget.Texture2D, ShowAlbedo ? scene.SurfaceCache.Albedo : scene.SurfaceCache.FinalLightingCurrent, 2);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetFloat("searchDistance", gi.SdfVoxelSize * 2f);

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
