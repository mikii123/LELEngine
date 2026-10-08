using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Thin G-buffer prepass: writes scene depth plus world normal and roughness for all opaque geometry.
	///     The opaque pass then shades with depth test LEQUAL and no depth writes (each pixel shaded once),
	///     and screen-space passes (GI resolve) get geometry without re-rasterizing the scene.
	///     Shares the depth texture with the scene target. GPU-driven: every visible instance in one indirect call.
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
		private ShaderProgram instancedProgram;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/GBuffer.shader");
			instancedProgram = program.GetInstancedVariant();

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

			GpuScene gpu = context.Renderer.GpuScene;
			bool gpuDriven = gpu.Active && instancedProgram != null;
			if (gpuDriven)
			{
				gpu.EnsureCameraCulled(context.ViewProjection);
			}

			// Depth was already cleared with the scene target (shared texture); clear only our color.
			gbuffer.Bind();
			GL.ClearColor(0f, 0f, 0f, 0f);
			GL.Clear(ClearBufferMask.ColorBufferBit);

			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(true);

			ShaderProgram active = gpuDriven ? instancedProgram : program;
			active.Use();
			active.SetMatrix4("viewMatrix", context.View);
			active.SetMatrix4("projectionMatrix", context.Projection);

			if (gpuDriven)
			{
				gpu.DrawCameraAll();
			}
			else
			{
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
			}

			context.DepthPrepassDone = true;
			context.NormalRoughness = normalRoughness;
		}

		public override void Dispose()
		{
			gbuffer?.Delete();
			gbuffer = null;
			// Deletes the instanced variant too.
			program?.Delete();
			program = null;
			instancedProgram = null;
		}

		#endregion
	}
}
