using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Resolves the HDR scene color to the window backbuffer: exposure, tonemapping, gamma. A super sampled scene
	///     (<see cref="RenderSettings.SuperSampling" />) is filtered down to the window here, after the tonemap and in
	///     linear light (Engine/SuperSampling.shader: across, then down, each in one pass).
	///     Further screen-space effects (SSAO, bloom, GI composition) plug in before this pass.
	/// </summary>
	public sealed class PostProcessPass : RenderPass
	{
		#region PublicFields

		public override string Name => "PostProcess";

		public override RenderPassScope Scope => RenderPassScope.Final;

		#endregion

		#region PrivateFields

		private ShaderProgram tonemap;
		private ShaderProgram superSampleAcross;
		private ShaderProgram superSampleDown;

		/// <summary>The scene tonemapped and filtered across: the window's width, the scene's height.</summary>
		private Framebuffer across;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			tonemap = BuiltinShaders.CreateTonemap();
		}

		public override void Execute(RenderContext context)
		{
			GLState.SetDepth(false, false);
			GLState.SetCull(false);
			GLState.SetBlend(false);
			if (context.Width != context.OutputWidth || context.Height != context.OutputHeight)
			{
				ResolveSuperSampled(context);
			}
			else
			{
				// Super sampling off: its target goes.
				across?.Delete();
				across = null;
				context.Renderer.BindOutput();
				tonemap.Use();
				tonemap.SetTexture("SceneColor", TextureTarget.Texture2D, context.SceneColor.Handle, 0);
				tonemap.SetFloat("Exposure", context.Settings.Exposure);
				tonemap.SetInt("TonemapEnabled", context.Settings.Tonemap ? 1 : 0);
				tonemap.SetInt("KeepAlpha", context.Settings.PreserveAlpha ? 1 : 0);
				context.Fullscreen.Draw();
			}

			GLState.SetDepth(true, true);
			GLState.SetCull(true);
		}

		public override void Dispose()
		{
			tonemap?.Delete();
			tonemap = null;
			superSampleAcross?.Delete();
			superSampleAcross = null;
			superSampleDown?.Delete();
			superSampleDown = null;
			across?.Delete();
			across = null;
		}

		#endregion

		#region PrivateMethods

		/// <summary>The super sampled scene down to the window: tonemapped and filtered across, then down.</summary>
		private void ResolveSuperSampled(RenderContext context)
		{
			superSampleAcross ??= new ShaderProgram("Engine/SuperSampling.shader", new[] { "SS_HORIZONTAL" });
			superSampleDown ??= new ShaderProgram("Engine/SuperSampling.shader");
			if (across == null)
			{
				across = new Framebuffer(context.OutputWidth, context.Height);
				across.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				across.Validate();
			}
			else if (across.Width != context.OutputWidth || across.Height != context.Height)
			{
				across.Resize(context.OutputWidth, context.Height);
			}

			SuperSamplingSettings settings = context.Settings.SuperSampling;
			int filter = (int)(settings?.Filter ?? SuperSamplingFilter.Gaussian);
			float smoothness = System.Math.Clamp(settings?.Smoothness ?? 0.33f, 0f, 1f);
			// Window pixels: 0.25 keeps the 2 x 2 average of 4x nearly a box, 0.75 is soft (DSR's 0..100 %).
			float sigma = 0.25f + 0.5f * smoothness;

			across.Bind();
			superSampleAcross.Use();
			superSampleAcross.SetTexture("ssSource", TextureTarget.Texture2D, context.SceneColor.Handle, 0);
			superSampleAcross.SetFloat("ssRatio", context.Width / (float)context.OutputWidth);
			superSampleAcross.SetInt("ssFilter", filter);
			superSampleAcross.SetFloat("ssSigma", sigma);
			superSampleAcross.SetFloat("Exposure", context.Settings.Exposure);
			superSampleAcross.SetInt("TonemapEnabled", context.Settings.Tonemap ? 1 : 0);
			context.Fullscreen.Draw();

			context.Renderer.BindOutput();
			superSampleDown.Use();
			superSampleDown.SetTexture("ssSource", TextureTarget.Texture2D, across.ColorAttachments[0].Handle, 0);
			superSampleDown.SetFloat("ssRatio", context.Height / (float)context.OutputHeight);
			superSampleDown.SetInt("ssFilter", filter);
			superSampleDown.SetFloat("ssSigma", sigma);
			context.Fullscreen.Draw();
		}

		#endregion
	}
}
