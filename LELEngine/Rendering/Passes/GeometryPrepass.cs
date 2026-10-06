using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Thin G-buffer prepass: writes scene depth plus world normal and roughness for all opaque geometry.
	///     The opaque pass then shades with depth test LEQUAL and no depth writes (each pixel shaded once),
	///     and screen-space passes (GI resolve) get geometry without re-rasterizing the scene.
	///     Shares the depth texture with the scene target.
	/// </summary>
	public sealed class GeometryPrepass : RenderPass
	{
		#region PublicFields

		public override string Name => "GeometryPrepass";
		public RenderTexture NormalRoughness => normalRoughness;

		#endregion

		#region PrivateFields

		private Framebuffer gbuffer;
		private RenderTexture normalRoughness;
		private ShaderProgram program;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/GBuffer.shader");

			gbuffer = new Framebuffer(renderer.Width, renderer.Height);
			normalRoughness = gbuffer.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
			gbuffer.AttachSharedDepth(renderer.SceneDepth);
			gbuffer.Validate();
		}

		public override void Resize(int width, int height)
		{
			// The shared depth is resized by the renderer; only the normal target is ours.
			gbuffer.Resize(width, height);
		}

		public override void Execute(RenderContext context)
		{
			if (!context.Settings.DepthPrepass)
			{
				context.DepthPrepassDone = false;
				context.NormalRoughness = null;
				return;
			}

			// Depth was already cleared with the scene target (shared texture); clear only our color.
			gbuffer.Bind();
			GL.ClearColor(0f, 0f, 0f, 0f);
			GL.Clear(ClearBufferMask.ColorBufferBit);

			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(true);

			program.Use();
			program.SetMatrix4("viewMatrix", context.View);
			program.SetMatrix4("projectionMatrix", context.Projection);

			foreach (MeshRenderer renderer in context.Renderers)
			{
				Material.ResetStandardUniforms(program);
				float roughness;
				if (renderer.Material != null && renderer.Material.TryGetFloat("Roughness", out roughness))
				{
					program.SetFloat("Roughness", roughness);
				}

				renderer.RenderWith(program);
			}

			context.DepthPrepassDone = true;
			context.NormalRoughness = normalRoughness;
		}

		public override void Dispose()
		{
			gbuffer?.Delete();
			gbuffer = null;
			program?.Delete();
			program = null;
		}

		#endregion
	}
}
