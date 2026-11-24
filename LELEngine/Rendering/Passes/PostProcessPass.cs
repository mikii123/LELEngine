using LELEngine.Shaders;
using LELEngine.Graphics;

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

		/// <summary>The scene tonemapped and filtered across (a frame texture: the window's width, the scene's height).</summary>
		private const string Across = "PostProcess.across";

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			tonemap = BuiltinShaders.CreateTonemap();
		}

		public override void Setup(FrameGraph frame, RenderContext context)
		{
			// The pass writes the output: never skipped.
			frame.SideEffects();
			if (context.Width != context.OutputWidth || context.Height != context.OutputHeight)
			{
				// Written whole by the first filter, read by the second.
				frame.CreateTexture(Across, new FrameTextureDescription(context.OutputWidth, context.Height, RenderTextureFormat.RGBA16F, false));
			}
		}

		public override void Execute(RenderContext context)
		{
			CommandList commands = Gfx.Commands;
			commands.State.SetDepth(false, false);
			commands.State.Culling = FaceCulling.None;
			commands.State.Blend = false;
			if (context.Width != context.OutputWidth || context.Height != context.OutputHeight)
			{
				ResolveSuperSampled(context);
			}
			else
			{
				context.Renderer.BeginOutput();
				tonemap.Use();
				tonemap.SetTexture("SceneColor", context.SceneColor);
				tonemap.SetFloat("Exposure", context.Settings.Exposure);
				tonemap.SetInt("TonemapEnabled", context.Settings.Tonemap ? 1 : 0);
				tonemap.SetInt("KeepAlpha", context.Settings.PreserveAlpha ? 1 : 0);
				context.Fullscreen.Draw();
			}

			commands.State.SetDepth(true, true);
			commands.State.Culling = FaceCulling.Back;
		}

		public override void Dispose()
		{
			tonemap?.Delete();
			tonemap = null;
			superSampleAcross?.Delete();
			superSampleAcross = null;
			superSampleDown?.Delete();
			superSampleDown = null;
		}

		#endregion

		#region PrivateMethods

		/// <summary>The super sampled scene down to the window: tonemapped and filtered across, then down.</summary>
		private void ResolveSuperSampled(RenderContext context)
		{
			superSampleAcross ??= new ShaderProgram("Engine/SuperSampling.shader", new[] { "SS_HORIZONTAL" });
			superSampleDown ??= new ShaderProgram("Engine/SuperSampling.shader");
			Framebuffer across = context.Frame.Target(Across);

			SuperSamplingSettings settings = context.Settings.SuperSampling;
			int filter = (int)(settings?.Filter ?? SuperSamplingFilter.Gaussian);
			float smoothness = System.Math.Clamp(settings?.Smoothness ?? 0.33f, 0f, 1f);
			// Window pixels: 0.25 keeps the 2 x 2 average of 4x nearly a box, 0.75 is soft (DSR's 0..100 %).
			float sigma = 0.25f + 0.5f * smoothness;

			// Every pixel is drawn.
			across.Begin(LoadOperation.DontCare);
			superSampleAcross.Use();
			superSampleAcross.SetTexture("ssSource", context.SceneColor);
			superSampleAcross.SetFloat("ssRatio", context.Width / (float)context.OutputWidth);
			superSampleAcross.SetInt("ssFilter", filter);
			superSampleAcross.SetFloat("ssSigma", sigma);
			superSampleAcross.SetFloat("Exposure", context.Settings.Exposure);
			superSampleAcross.SetInt("TonemapEnabled", context.Settings.Tonemap ? 1 : 0);
			context.Fullscreen.Draw();

			context.Renderer.BeginOutput();
			superSampleDown.Use();
			superSampleDown.SetTexture("ssSource", across.ColorAttachments[0]);
			superSampleDown.SetFloat("ssRatio", context.Height / (float)context.OutputHeight);
			superSampleDown.SetInt("ssFilter", filter);
			superSampleDown.SetFloat("ssSigma", sigma);
			context.Fullscreen.Draw();
		}

		#endregion
	}
}
