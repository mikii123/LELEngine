using System;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Lumen final gather with screen-space radiance caching:
	///     1. Trace: a probe every <see cref="GlobalIlluminationSettings.ProbeSpacing" /> pixels, 64 jittered
	///        hemisphere rays each through the global distance field, hits lit from the surface cache.
	///     2. Filter: spatial filter across neighbouring probes on the same surface.
	///     3. SH: each probe is projected to second-order spherical harmonics.
	///     4. Integrate: per pixel (resolve resolution) interpolation of the four nearest probes with plane and
	///        normal tests, cosine convolution for diffuse, reflection lookup for rough specular, and temporal
	///        accumulation. Publishes the same resolve buffers material shaders already read.
	/// </summary>
	public sealed class ScreenProbeGatherPass : RenderPass
	{
		#region PublicFields

		public override string Name => "ProbeGather";

		#endregion

		#region PrivateFields

		private const int ProbeResolution = 8;

		private ShaderProgram anchors;
		private ComputeShader importance;
		private ShaderProgram trace;
		private ShaderProgram compose;
		private ShaderProgram filter;
		private ComputeShader sh;
		private ShaderProgram integrate;
		private int selectionTexture;

		// Anchors, composed (unfiltered) and filtered radiance are kept for one frame: the next frame
		// reprojects into them (importance PDF from the filtered map, history from the unfiltered one).
		private readonly Framebuffer[] anchorTargets = new Framebuffer[2];
		private readonly Framebuffer[] composeTargets = new Framebuffer[2];
		private readonly Framebuffer[] filterTargets = new Framebuffer[2];
		private int pingPong;
		private Framebuffer anchorTarget => anchorTargets[pingPong];
		private Framebuffer composeTarget => composeTargets[pingPong];
		private Framebuffer filterTarget => filterTargets[pingPong];
		private Framebuffer traceTarget;
		private int shTexture;
		private Vector2i previousProbeJitter;
		private readonly Framebuffer[] output = new Framebuffer[2];
		private int outputIndex;
		private bool historyValid;

		private int probesX;
		private int probesY;
		private int outputWidth;
		private int outputHeight;

		private Matrix4 previousViewProjection;
		private Vector3 previousCameraPosition;
		private Vector3 previousCameraForward;
		private int frameIndex;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			anchors = new ShaderProgram("Engine/ProbeAnchors.shader");
			importance = new ComputeShader("Engine/ProbeImportance.shader");
			trace = new ShaderProgram("Engine/ProbeTrace.shader");
			compose = new ShaderProgram("Engine/ProbeCompose.shader");
			filter = new ShaderProgram("Engine/ProbeFilter.shader");
			sh = new ComputeShader("Engine/ProbeSH.shader");
			integrate = new ShaderProgram("Engine/ProbeIntegrate.shader");
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			LumenScene scene = context.Renderer.LumenScene;
			bool active = gi.Enabled && gi.Mode == GIMode.Lumen && gi.GlobalSdf != 0 && context.DepthPrepassDone && context.NormalRoughness != null && scene.SurfaceCache != null;
			if (!active)
			{
				historyValid = false;
				return;
			}

			frameIndex++;
			int spacing = Math.Max(4, gi.ProbeSpacing);
			EnsureTargets(context.Width, context.Height, spacing, gi.ResolveScale);

			// Jitter: probe anchor inside its cell and ray direction inside its octahedral texel (R2 sequence).
			float anchorScale = gi.ProbeJitter ? MathHelper.Clamp(gi.ProbeAnchorJitter, 0f, 1f) : 0f;
			Vector2 r2 = R2(frameIndex);
			Vector2 anchor = new Vector2(0.5f + (r2.X - 0.5f) * anchorScale, 0.5f + (r2.Y - 0.5f) * anchorScale);
			Vector2i probeJitter = new Vector2i(
				MathHelper.Clamp((int)(anchor.X * spacing), 0, spacing - 1),
				MathHelper.Clamp((int)(anchor.Y * spacing), 0, spacing - 1));
			Vector2 directionJitter = gi.ProbeJitter && gi.ProbeDirectionJitter ? R2(frameIndex * 7 + 3) : new Vector2(0.5f, 0.5f);

			GpuProfiler profiler = context.Renderer.Profiler;
			GLState.SetDepth(false, false);
			GLState.SetCull(false);

			// ---- 0. probe anchors (position + normal per probe, read by every later stage)
			profiler.Split("ProbeGather.anchors");
			anchorTarget.Bind();
			anchors.Use();
			SetProbeUniforms(anchors, context, spacing, probeJitter);
			context.Fullscreen.Draw();

			int previousFiltered = filterTargets[1 - pingPong].ColorAttachments[0].Handle;
			int previousComposed = composeTargets[1 - pingPong].ColorAttachments[0].Handle;
			int previousAnchors = anchorTargets[1 - pingPong].ColorAttachments[0].Handle;

			// ---- 1a. history record + importance: reproject each probe, pick the 16 directions worth 2x2 rays
			profiler.Split("ProbeGather.importance");
			importance.Use();
			SetProbeUniforms(importance.Program, context, spacing, probeJitter);
			importance.Program.SetTexture("PreviousProbeRadiance", TextureTarget.Texture2D, previousFiltered, 3);
			importance.Program.SetTexture("PreviousProbeAnchorPosition", TextureTarget.Texture2D, previousAnchors, 4);
			importance.Program.SetMatrix4("previousViewProjection", previousViewProjection);
			importance.Program.SetInt2("previousProbeJitter", previousProbeJitter.X, previousProbeJitter.Y);
			importance.Program.SetInt("historyValid", historyValid ? 1 : 0);
			importance.Program.SetInt("importanceEnabled", gi.ProbeImportanceSampling ? 1 : 0);
			importance.Program.SetFloat("historyDistanceThreshold", gi.ProbeHistoryDistance);
			importance.Program.SetInt("frameIndex", frameIndex);
			GL.BindImageTexture(0, selectionTexture, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba32ui);
			importance.DispatchThreads(probesX, probesY, 1, 8, 8, 1);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			// ---- 1b. trace: one ray per fragment
			profiler.Split("ProbeGather.trace");
			traceTarget.Bind();
			trace.Use();
			scene.SetUniforms(trace);
			Lighting.SetSdfUniforms(trace);
			SetProbeUniforms(trace, context, spacing, probeJitter);
			RadianceCachePass radianceCache = context.Renderer.GetPass<RadianceCachePass>();
			if (radianceCache != null)
			{
				radianceCache.SetUniforms(trace, 7);
			}
			else
			{
				trace.SetFloat("rcNearDistance", 0f);
			}
			trace.SetTexture("FinalLighting", TextureTarget.Texture2D, scene.SurfaceCache.FinalLightingCurrent, 2);
			trace.SetTexture("ProbeSelection", TextureTarget.Texture2D, selectionTexture, 3);
			trace.SetVector3("giSkyRadiance", gi.SkyRadiance);
			trace.SetVector2("directionJitter", directionJitter);
			trace.SetMatrix4("viewProjection", context.ViewProjection);
			trace.SetVector3("cameraPosition", context.CameraPosition);
			trace.SetVector3("cameraForward", context.Camera.transform.forward);
			trace.SetFloat("screenTraceDistance", gi.ScreenTraceDistance);
			trace.SetInt("screenTraceSteps", Math.Max(1, gi.ScreenTraceSteps));
			trace.SetFloat("screenTraceThickness", gi.ScreenTraceThickness);
			trace.SetInt("frameIndex", frameIndex);
			context.Fullscreen.Draw();

			// ---- 1c. compose: rays -> octahedral map, temporal probe filter, history for untraced directions
			profiler.Split("ProbeGather.compose");
			composeTarget.Bind();
			compose.Use();
			SetProbeUniforms(compose, context, spacing, probeJitter);
			compose.SetTexture("RayResults", TextureTarget.Texture2D, traceTarget.ColorAttachments[0].Handle, 2);
			compose.SetTexture("PreviousProbeRadiance", TextureTarget.Texture2D, previousComposed, 3);
			compose.SetTexture("ProbeSelection", TextureTarget.Texture2D, selectionTexture, 4);
			compose.SetVector3("giSkyRadiance", gi.SkyRadiance);
			compose.SetFloat("probeHistoryWeight", gi.ProbeHistoryWeight);
			context.Fullscreen.Draw();

			// ---- 2. filter
			profiler.Split("ProbeGather.filter");
			filterTarget.Bind();
			filter.Use();
			SetProbeUniforms(filter, context, spacing, probeJitter);
			filter.SetTexture("ProbeRadiance", TextureTarget.Texture2D, composeTarget.ColorAttachments[0].Handle, 2);
			filter.SetFloat("planeTolerance", gi.ProbePlaneTolerance);
			filter.SetInt("filterRadius", Math.Max(1, gi.ProbeFilterRadius));
			filter.SetVector2("directionJitter", directionJitter);
			filter.SetFloat("angleThreshold", (float)Math.Cos(gi.ProbeFilterMaxAngle * Math.PI / 180.0));
			if (gi.ProbeSpatialFilter)
			{
				context.Fullscreen.Draw();
			}
			else
			{
				// Keep the filtered buffer valid as next frame's importance and history source.
				GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, composeTarget.Handle);
				GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, filterTarget.Handle);
				GL.BlitFramebuffer(0, 0, composeTarget.Width, composeTarget.Height, 0, 0, filterTarget.Width, filterTarget.Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
				GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			}
			int radianceSource = filterTarget.ColorAttachments[0].Handle;

			// ---- 3. spherical harmonics
			profiler.Split("ProbeGather.sh");
			sh.Use();
			SetProbeUniforms(sh.Program, context, spacing, probeJitter);
			sh.Program.SetTexture("ProbeRadiance", TextureTarget.Texture2D, radianceSource, 2);
			GL.BindImageTexture(0, shTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			sh.DispatchThreads(probesX, probesY, 1, 8, 8, 1);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			// ---- 4. integrate (+ temporal)
			profiler.Split("ProbeGather.integrate");
			Framebuffer target = output[outputIndex];
			Framebuffer history = output[1 - outputIndex];
			target.Bind();
			integrate.Use();
			SetProbeUniforms(integrate, context, spacing, probeJitter);
			integrate.SetTexture("ProbeSH", TextureTarget.Texture2DArray, shTexture, 2);
			integrate.SetTexture("HistoryDiffuse", TextureTarget.Texture2D, history.ColorAttachments[0].Handle, 3);
			integrate.SetTexture("HistorySpecular", TextureTarget.Texture2D, history.ColorAttachments[1].Handle, 4);
			integrate.SetVector3("cameraPosition", context.CameraPosition);
			integrate.SetVector3("cameraForward", context.Camera.transform.forward);
			integrate.SetMatrix4("previousViewProjection", previousViewProjection);
			integrate.SetVector3("previousCameraPosition", previousCameraPosition);
			integrate.SetVector3("previousCameraForward", previousCameraForward);
			integrate.SetFloat("temporalBlend", gi.ProbeTemporalBlend);
			integrate.SetInt("historyValid", historyValid ? 1 : 0);
			integrate.SetFloat("planeTolerance", gi.ProbePlaneTolerance);
			context.Fullscreen.Draw();

			GLState.SetDepth(true, true);
			GLState.SetCull(true);

			// Publish for the material shaders (same contract as GIResolvePass).
			gi.ResolvedDiffuse = target.ColorAttachments[0].Handle;
			gi.ResolvedSpecular = target.ColorAttachments[1].Handle;
			gi.ResolveWidth = outputWidth;
			gi.ResolveHeight = outputHeight;
			gi.ScreenWidth = context.Width;
			gi.ScreenHeight = context.Height;
			gi.ResolveActive = true;

			previousViewProjection = context.ViewProjection;
			previousCameraPosition = context.CameraPosition;
			previousCameraForward = context.Camera.transform.forward;
			previousProbeJitter = probeJitter;
			historyValid = true;
			outputIndex = 1 - outputIndex;
			pingPong = 1 - pingPong;
		}

		public override void Resize(int width, int height)
		{
			historyValid = false;
		}

		public override void Dispose()
		{
			anchors?.Delete();
			importance?.Delete();
			trace?.Delete();
			compose?.Delete();
			filter?.Delete();
			sh?.Delete();
			integrate?.Delete();
			traceTarget?.Delete();
			if (selectionTexture != 0)
			{
				GL.DeleteTexture(selectionTexture);
			}
			for (int i = 0; i < 2; i++)
			{
				anchorTargets[i]?.Delete();
				composeTargets[i]?.Delete();
				filterTargets[i]?.Delete();
				output[i]?.Delete();
			}
			if (shTexture != 0)
			{
				GL.DeleteTexture(shTexture);
			}
		}

		#endregion

		#region PrivateMethods

		private void SetProbeUniforms(ShaderProgram program, RenderContext context, int spacing, Vector2i probeJitter)
		{
			program.SetTexture("SceneDepth", TextureTarget.Texture2D, context.SceneDepth.Handle, 0);
			program.SetTexture("NormalRoughness", TextureTarget.Texture2D, context.NormalRoughness.Handle, 1);
			program.SetTexture("ProbeAnchorPosition", TextureTarget.Texture2D, anchorTarget.ColorAttachments[0].Handle, 5);
			program.SetTexture("ProbeAnchorNormal", TextureTarget.Texture2D, anchorTarget.ColorAttachments[1].Handle, 6);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetInt2("screenSize", context.Width, context.Height);
			program.SetInt2("probeCount", probesX, probesY);
			program.SetInt("probeSpacing", spacing);
			program.SetInt2("probeJitter", probeJitter.X, probeJitter.Y);
		}

		private void EnsureTargets(int width, int height, int spacing, float resolveScale)
		{
			int px = (width + spacing - 1) / spacing;
			int py = (height + spacing - 1) / spacing;
			int ow = Math.Max(1, (int)Math.Ceiling(width * resolveScale));
			int oh = Math.Max(1, (int)Math.Ceiling(height * resolveScale));
			if (px == probesX && py == probesY && ow == outputWidth && oh == outputHeight && traceTarget != null)
			{
				return;
			}

			probesX = px;
			probesY = py;
			outputWidth = ow;
			outputHeight = oh;
			historyValid = false;

			for (int i = 0; i < 2; i++)
			{
				anchorTargets[i]?.Delete();
				anchorTargets[i] = new Framebuffer(px, py);
				anchorTargets[i].AddColorAttachment(RenderTextureFormat.RGBA32F, false);
				anchorTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				anchorTargets[i].Validate();

				composeTargets[i]?.Delete();
				composeTargets[i] = new Framebuffer(px * ProbeResolution, py * ProbeResolution);
				composeTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				composeTargets[i].Validate();

				filterTargets[i]?.Delete();
				filterTargets[i] = new Framebuffer(px * ProbeResolution, py * ProbeResolution);
				filterTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				filterTargets[i].Validate();
			}

			traceTarget?.Delete();
			traceTarget = new Framebuffer(px * ProbeResolution, py * ProbeResolution);
			traceTarget.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
			traceTarget.Validate();

			if (selectionTexture != 0)
			{
				GL.DeleteTexture(selectionTexture);
			}
			selectionTexture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, selectionTexture);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32ui, px, py, 0, PixelFormat.RgbaInteger, PixelType.UnsignedInt, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);

			if (shTexture != 0)
			{
				GL.DeleteTexture(shTexture);
			}
			shTexture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2DArray, shTexture);
			GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba16f, px, py, 9, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture2DArray, 0);

			for (int i = 0; i < 2; i++)
			{
				output[i]?.Delete();
				output[i] = new Framebuffer(ow, oh);
				output[i].AddColorAttachment(RenderTextureFormat.RGBA16F);
				output[i].AddColorAttachment(RenderTextureFormat.RGBA16F);
				output[i].Validate();
			}

			Console.WriteLine($"[ProbeGather] {px}x{py} probes every {spacing} px, {px * ProbeResolution}x{py * ProbeResolution} rays, resolve {ow}x{oh}");
		}

		// Low-discrepancy 2D sequence (Roberts' R2).
		private static Vector2 R2(int index)
		{
			const double g = 1.32471795724474602596;
			double a1 = 1.0 / g;
			double a2 = 1.0 / (g * g);
			return new Vector2((float)((0.5 + a1 * index) % 1.0), (float)((0.5 + a2 * index) % 1.0));
		}

		#endregion
	}
}
