using System;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Lumen final gather with screen-space radiance caching:
	///     0. Anchors: a probe every <see cref="GlobalIlluminationSettings.ProbeSpacing" /> pixels, plus adaptive
	///        probes placed hierarchically where the uniform grid cannot be interpolated (thin geometry).
	///     1. Trace: 64 hemisphere rays per probe through the global distance field (hits lit from the surface
	///        cache, far field from the radiance cache), importance sampled from last frame's radiance.
	///     2. Filter: spatial filter across neighbouring probes on the same surface.
	///     3. SH: each probe is projected to second-order spherical harmonics.
	///     4. Integrate: per pixel (resolve resolution) interpolation of the surrounding probes with plane and
	///        normal tests, cosine convolution for diffuse, reflection lookup for rough specular, and temporal
	///        accumulation. Publishes the same resolve buffers material shaders already read.
	///
	///     Probe textures are atlases: the uniform grid in the top rows, adaptive probes in extra rows below.
	/// </summary>
	public sealed class ScreenProbeGatherPass : RenderPass
	{
		#region PublicFields

		public override string Name => "ProbeGather";

		/// <summary>Adaptive probes placed in a recent frame (read back every 60 frames for diagnostics).</summary>
		public int AdaptiveProbeCount { get; private set; }

		/// <summary>True once this frame's probes exist (for debug views running later in the frame).</summary>
		public bool ProbesReady { get; private set; }

		#endregion

		#region PrivateFields

		private const int ProbeResolution = 8;
		private const int MaxAdaptivePerTile = 16;
		private const int AdaptiveTileStride = 1 + 2 * MaxAdaptivePerTile;
		private const int AdaptiveBufferBinding = 5;
		private const int PreviousAdaptiveBufferBinding = 6;
		private const int AdaptiveLevelBinding = 7;

		private ShaderProgram anchors;
		private ComputeShader adaptiveClear;
		private ComputeShader adaptiveSnapshot;
		private ComputeShader placement;
		private GpuBuffer adaptiveLevelBuffer;
		private Matrix4 cachedViewProjection;
		private Matrix4 cachedInverseViewProjection = Matrix4.Identity;
		private ComputeShader importance;
		private ShaderProgram trace;
		private ComputeShader compose;
		private ComputeShader filter;
		private ComputeShader sh;
		private ShaderProgram integrate;
		private GpuTexture selectionTexture;
		private GpuTexture rayTableTexture;
		private GpuTexture probeChangeTexture;
		private GpuTexture adaptivePixelTexture;

		// Adaptive probe tile lists, double buffered: this frame's lists are built while last frame's are
		// searched for temporal history.
		private readonly GpuBuffer[] adaptiveBuffers = new GpuBuffer[2];
		private GpuBuffer adaptiveBuffer => adaptiveBuffers[pingPong];
		private GpuBuffer readbackBuffer;
		private GpuFence readbackFence;
		private readonly int[] readbackValue = new int[1];

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
		private RenderTexture filterScratch; // between the passes of the spatial filter
		private GpuTexture shTexture;
		private Vector2i previousProbeJitter;
		private readonly Framebuffer[] output = new Framebuffer[2];
		private int outputIndex;
		private bool historyValid;

		private int probesX;
		private int probesY;
		private int adaptiveRows;
		private int adaptiveMax;
		private int atlasRows;
		private int outputWidth;
		private int outputHeight;

		private Matrix4 previousViewProjection;
		private Vector3 previousCameraPosition;
		private Vector3 previousCameraForward;
		private int frameIndex;
		private int lastSpacing;
		private Vector2 currentDirectionJitter = new Vector2(0.5f, 0.5f);

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			LumenScene scene = context.Renderer.LumenScene;
			bool active = gi.LumenActive && gi.GlobalSdf != 0 && context.DepthPrepassDone && context.NormalRoughness != null && context.Motion != null && scene.SurfaceCache != null;
			if (!active)
			{
				historyValid = false;
				ProbesReady = false;
				// Made when the gather first runs, freed when Lumen does not run.
				if (!gi.LumenActive)
				{
					Release();
				}
				return;
			}

			if (anchors == null)
			{
				anchors = new ShaderProgram("Engine/ProbeAnchors.shader");
				adaptiveClear = new ComputeShader("Engine/ProbeAdaptiveClear.shader");
				adaptiveSnapshot = new ComputeShader("Engine/ProbeAdaptiveSnapshot.shader");
				placement = new ComputeShader("Engine/ProbePlacement.shader");
				GraphicsDevice device = Gfx.Device;
				adaptiveLevelBuffer = device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, "Screen probe adaptive levels"));
				importance = new ComputeShader("Engine/ProbeImportance.shader");
				trace = new ShaderProgram("Engine/ProbeTrace.shader");
				compose = new ComputeShader("Engine/ProbeCompose.shader");
				filter = new ComputeShader("Engine/ProbeFilter.shader");
				sh = new ComputeShader("Engine/ProbeSH.shader");
				integrate = new ShaderProgram("Engine/ProbeIntegrate.shader");
				adaptiveBuffers[0] = device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, "Screen probe adaptive tiles 0"));
				adaptiveBuffers[1] = device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, "Screen probe adaptive tiles 1"));
				readbackBuffer = device.CreateBuffer(new BufferDescriptor(BufferUsage.Readback, sizeof(int), false, "Screen probe adaptive count"));
			}

			frameIndex++;
			// Super sampled: as many probes and resolved pixels as in the window (the spacing in scene pixels times the scale,
			// kept a multiple of 4 for the adaptive levels' halves and quarters): soft light needs no more of them.
			float superSampling = Math.Max(1f, context.SuperSampling);
			int spacing = Math.Max(4, gi.ProbeSpacing);
			if (superSampling > 1f)
			{
				spacing = Math.Max(4, (int)MathF.Round(spacing * superSampling / 4f) * 4);
			}

			lastSpacing = spacing;
			EnsureTargets(context.Width, context.Height, spacing, MathHelper.Clamp(gi.ScreenProbeResolveScale, 0.25f, 1f) / superSampling, gi.ProbeAdaptivePlacement ? gi.ProbeAdaptiveFraction : 0f);

			// Jitter: probe anchor inside its cell and ray direction inside its octahedral texel (R2 sequence).
			float anchorScale = gi.ProbeJitter ? MathHelper.Clamp(gi.ProbeAnchorJitter, 0f, 1f) : 0f;
			Vector2 r2 = R2(frameIndex);
			Vector2 anchor = new Vector2(0.5f + (r2.X - 0.5f) * anchorScale, 0.5f + (r2.Y - 0.5f) * anchorScale);
			Vector2i probeJitter = new Vector2i(
				MathHelper.Clamp((int)(anchor.X * spacing), 0, spacing - 1),
				MathHelper.Clamp((int)(anchor.Y * spacing), 0, spacing - 1));
			Vector2 directionJitter = gi.ProbeJitter && gi.ProbeDirectionJitter ? R2(frameIndex * 7 + 3) : new Vector2(0.5f, 0.5f);
			currentDirectionJitter = directionJitter;

			GpuProfiler profiler = context.Renderer.Profiler;
			CommandList commands = Gfx.Commands;
			commands.State.SetDepth(false, false);
			commands.State.Culling = FaceCulling.None;

			// ---- 0a. uniform probe anchors (position + normal per probe, read by every later stage)
			profiler.Split("ProbeGather.anchors");
			anchorTarget.Begin();
			anchors.Use();
			SetProbeUniforms(anchors, context, spacing, probeJitter);
			context.Fullscreen.Draw();
			commands.EndRenderPass();

			// ---- 0b. adaptive probes: tile lists cleared on the GPU, then placed at spacing / 2 and spacing / 4 steps
			profiler.Split("ProbeGather.placement");
			commands.SetStorageBuffer(AdaptiveBufferBinding, adaptiveBuffer);
			commands.SetStorageBuffer(PreviousAdaptiveBufferBinding, adaptiveBuffers[1 - pingPong]);
			commands.SetStorageBuffer(AdaptiveLevelBinding, adaptiveLevelBuffer);
			adaptiveClear.Use();
			SetProbeUniforms(adaptiveClear.Program, context, spacing, probeJitter);
			adaptiveClear.DispatchThreads(probesX * probesY + 1, 1, 1, 64, 1, 1);
			if (adaptiveRows > 0)
			{
				placement.Use();
				SetProbeUniforms(placement.Program, context, spacing, probeJitter);
				placement.Program.SetFloat("planeTolerance", gi.ProbePlaneTolerance);
				placement.Program.SetFloat("minCoverage", gi.ProbeAdaptiveMinCoverage);
				placement.Program.SetInt("maxAdaptiveProbes", adaptiveMax);
				commands.SetImage(0, anchorTarget.ColorAttachments[0].Gpu, 0, 0, ImageAccess.Write);
				commands.SetImage(1, anchorTarget.ColorAttachments[1].Gpu, 0, 0, ImageAccess.Write);
				commands.SetImage(2, adaptivePixelTexture, 0, 0, ImageAccess.Write);
				bool firstLevel = true;
				for (int factor = spacing / 2; factor >= Math.Max(2, spacing / 4); factor /= 2)
				{
					// Freeze the tile counts so this level only sees probes from previous levels (deterministic set).
					adaptiveSnapshot.Use();
					SetProbeUniforms(adaptiveSnapshot.Program, context, spacing, probeJitter);
					adaptiveSnapshot.Program.SetInt("clearFlags", firstLevel ? 1 : 0);
					adaptiveSnapshot.DispatchThreads(probesX * probesY, 1, 1, 64, 1, 1);

					placement.Use();
					placement.Program.SetInt("placementFactor", factor);
					placement.Program.SetInt("refineOnlyFlagged", !firstLevel && gi.ProbeAdaptiveFlaggedRefine ? 1 : 0);
					placement.Program.SetInt("flagChildren", firstLevel && gi.ProbeAdaptiveFlaggedRefine ? 1 : 0);
					firstLevel = false;
					placement.DispatchThreads((context.Width + factor - 1) / factor, (context.Height + factor - 1) / factor, 1, 8, 8, 1);
				}

				// Diagnostics count, read back asynchronously: copy to a staging buffer behind a fence and read it
				// a few frames later. Reading the tile lists directly would drain the whole GPU queue once a second.
				if (readbackFence != null)
				{
					if (readbackFence.IsSignaled)
					{
						readbackBuffer.GetData(0, readbackValue);
						AdaptiveProbeCount = Math.Min(readbackValue[0], adaptiveMax);
						readbackFence.Dispose();
						readbackFence = null;
					}
				}
				else if (frameIndex % 60 == 0)
				{
					commands.CopyBuffer(adaptiveBuffer, 0, readbackBuffer, 0, sizeof(int));
					readbackFence = Gfx.Device.CreateFence();
				}
			}
			else
			{
				AdaptiveProbeCount = 0;
			}

			RenderTexture previousFiltered = filterTargets[1 - pingPong].ColorAttachments[0];
			RenderTexture previousComposed = composeTargets[1 - pingPong].ColorAttachments[0];
			RenderTexture previousAnchors = anchorTargets[1 - pingPong].ColorAttachments[0];
			RenderTexture previousAnchorNormals = anchorTargets[1 - pingPong].ColorAttachments[1];

			// ---- 1a. history record + importance: reproject each probe (motion vectors), assign its 64 rays
			profiler.Split("ProbeGather.importance");
			importance.Use();
			SetProbeUniforms(importance.Program, context, spacing, probeJitter);
			importance.Program.SetTexture("PreviousProbeRadiance", previousFiltered);
			importance.Program.SetTexture("PreviousProbeAnchorPosition", previousAnchors);
			importance.Program.SetTexture("PreviousProbeAnchorNormal", previousAnchorNormals);
			importance.Program.SetTexture("Motion", context.Motion);
			importance.Program.SetTexture("AdaptivePixel", adaptivePixelTexture);
			importance.Program.SetMatrix4("previousViewProjection", previousViewProjection);
			importance.Program.SetInt2("previousProbeJitter", previousProbeJitter.X, previousProbeJitter.Y);
			importance.Program.SetInt("historyValid", historyValid ? 1 : 0);
			importance.Program.SetInt("importanceEnabled", gi.ProbeImportanceSampling ? 1 : 0);
			importance.Program.SetFloat("historyDistanceThreshold", gi.ProbeHistoryDistance);
			importance.Program.SetFloat("mergeFraction", gi.ProbeImportanceMergeFraction);
			commands.SetImage(0, selectionTexture, 0, 0, ImageAccess.Write);
			commands.SetImage(1, rayTableTexture, 0, 0, ImageAccess.Write);
			importance.DispatchThreads(probesX, atlasRows, 1, 8, 8, 1);

			// ---- 1b. trace: one ray per fragment
			profiler.Split("ProbeGather.trace");
			traceTarget.Begin();
			trace.Use();
			scene.SetUniforms(trace);
			Lighting.SetSdfUniforms(trace);
			SetProbeUniforms(trace, context, spacing, probeJitter);
			RadianceCachePass radianceCache = context.Renderer.GetPass<RadianceCachePass>();
			if (radianceCache != null)
			{
				radianceCache.SetUniforms(trace);
			}
			else
			{
				trace.SetFloat("rcNearDistance", 0f);
			}
			trace.SetTexture("FinalLighting", scene.SurfaceCache.FinalLightingCurrent);
			trace.SetTexture("ProbeSelection", selectionTexture);
			trace.SetTexture("ProbeRayTable", rayTableTexture);
			Lighting.SetSkyUniforms(trace);
			// The clouds on the rays' way (the volumetric objects in the GI).
			Lighting.SetMediaUniforms(trace);
			trace.SetMatrix4("viewProjection", context.ViewProjection);
			trace.SetVector3("cameraPosition", context.CameraPosition);
			trace.SetVector3("cameraForward", context.Camera.transform.forward);
			trace.SetVector2("cameraClipPlanes", new Vector2(context.Camera.NearClip, context.Camera.FarClip));
			trace.SetFloat("screenTraceDistance", gi.ScreenTraceDistance);
			trace.SetInt("screenTraceSteps", Math.Max(1, gi.ScreenTraceSteps));
			trace.SetFloat("screenTraceThickness", gi.ScreenTraceThickness);
			trace.SetInt("frameIndex", frameIndex);
			context.Fullscreen.Draw();

			// ---- 1c. compose: rays -> octahedral map, sample-count accumulation with the per-direction change test and
			// the short history of directions that see moving objects; per probe, how much of it changed. One
			// workgroup per probe.
			profiler.Split("ProbeGather.compose");
			compose.Use();
			SetProbeUniforms(compose.Program, context, spacing, probeJitter);
			compose.Program.SetTexture("RayResults", traceTarget.ColorAttachments[0]);
			compose.Program.SetTexture("PreviousProbeRadiance", previousComposed);
			compose.Program.SetTexture("ProbeSelection", selectionTexture);
			compose.Program.SetTexture("PreviousProbeMeta", composeTargets[1 - pingPong].ColorAttachments[1]);
			compose.Program.SetTexture("PreviousProbeFast", composeTargets[1 - pingPong].ColorAttachments[2]);
			compose.Program.SetFloat("probeHistoryWeight", gi.ProbeHistoryWeight);
			compose.Program.SetFloat("probeMaxHistorySamples", gi.ProbeMaxHistorySamples);
			compose.Program.SetFloat("probeFastSamples", Math.Max(1f, gi.ProbeFastHistorySamples));
			compose.Program.SetFloat("probeMovingSamples", Math.Max(1f, gi.ProbeMovingHistorySamples));
			compose.Program.SetFloat("probeMovingFrames", gi.ProbeMovingFrames);
			compose.Program.SetFloat("changeSigma", gi.ProbeChangeSigma);
			compose.Program.SetFloat("changeFloor", gi.ProbeChangeFloor);
			compose.Program.SetFloat("probeJumpThreshold", gi.ProbeJumpThreshold);
			commands.SetImage(0, composeTarget.ColorAttachments[0].Gpu, 0, 0, ImageAccess.Write);
			commands.SetImage(1, composeTarget.ColorAttachments[1].Gpu, 0, 0, ImageAccess.Write);
			commands.SetImage(2, probeChangeTexture, 0, 0, ImageAccess.Write);
			commands.SetImage(3, composeTarget.ColorAttachments[2].Gpu, 0, 0, ImageAccess.Write);
			compose.Dispatch(probesX, atlasRows, 1);

			// ---- 2. filter (3x3 probe neighbourhood; one workgroup per probe)
			profiler.Split("ProbeGather.filter");
			if (gi.ProbeSpatialFilter)
			{
				// Several passes of the 3x3 filter (Lumen runs a few): each widens the kernel, which lowers the noise
				// without the lag a longer history would add. The passes alternate between the filtered map and a scratch
				// map so that the last one writes the filtered map.
				int passes = Math.Clamp(gi.ProbeSpatialFilterPasses, 1, 4);
				filter.Use();
				SetProbeUniforms(filter.Program, context, spacing, probeJitter);
				filter.Program.SetTexture("AdaptivePixel", adaptivePixelTexture);
				filter.Program.SetFloat("planeTolerance", gi.ProbePlaneTolerance);
				filter.Program.SetFloat("angleThreshold", (float)Math.Cos(gi.ProbeFilterMaxAngle * Math.PI / 180.0));
				RenderTexture source = composeTarget.ColorAttachments[0];
				for (int pass = 0; pass < passes; pass++)
				{
					RenderTexture destination = (passes - 1 - pass) % 2 == 0 ? filterTarget.ColorAttachments[0] : filterScratch;
					filter.Program.SetTexture("ProbeRadiance", source);
					commands.SetImage(0, destination.Gpu, 0, 0, ImageAccess.Write);
					filter.Dispatch(probesX, atlasRows, 1);
					source = destination;
				}
			}
			else
			{
				// Keep the filtered buffer valid as next frame's importance source.
				commands.Blit(composeTarget.ColorAttachments[0].Gpu, filterTarget.ColorAttachments[0].Gpu);
			}
			RenderTexture radianceSource = filterTarget.ColorAttachments[0];

			// ---- 3. spherical harmonics
			profiler.Split("ProbeGather.sh");
			sh.Use();
			SetProbeUniforms(sh.Program, context, spacing, probeJitter);
			sh.Program.SetTexture("ProbeRadiance", radianceSource);
			sh.Program.SetTexture("ProbeChange", probeChangeTexture);
			commands.SetImage(0, shTexture, 0, -1, ImageAccess.Write);
			sh.DispatchThreads(probesX, atlasRows, 1, 8, 8, 1);

			// ---- 4. integrate (+ temporal)
			profiler.Split("ProbeGather.integrate");
			Framebuffer target = output[outputIndex];
			Framebuffer history = output[1 - outputIndex];
			target.Begin();
			integrate.Use();
			SetProbeUniforms(integrate, context, spacing, probeJitter);
			integrate.SetTexture("ProbeSH", shTexture);
			integrate.SetTexture("HistoryDiffuse", history.ColorAttachments[0]);
			integrate.SetTexture("HistorySpecular", history.ColorAttachments[1]);
			integrate.SetTexture("HistoryMeta", history.ColorAttachments[2]);
			integrate.SetTexture("Motion", context.Motion);
			integrate.SetVector3("cameraPosition", context.CameraPosition);
			integrate.SetVector3("cameraForward", context.Camera.transform.forward);
			integrate.SetMatrix4("previousViewProjection", previousViewProjection);
			integrate.SetVector3("previousCameraPosition", previousCameraPosition);
			integrate.SetVector3("previousCameraForward", previousCameraForward);
			integrate.SetInt2("resolveSize", outputWidth, outputHeight);
			// The history weight setting is the steady state of the 1 / n accumulation: n = 1 / (1 - weight) frames.
			float blend = MathHelper.Clamp(gi.ProbeTemporalBlend, 0f, 0.99f);
			integrate.SetFloat("temporalMaxFrames", blend > 0f ? 1f / (1f - blend) : 0f);
			integrate.SetFloat("temporalMovingFrames", Math.Max(1f, gi.ProbeTemporalMovingFrames));
			integrate.SetInt("historyValid", historyValid ? 1 : 0);
			integrate.SetFloat("planeTolerance", gi.ProbePlaneTolerance);
			context.Fullscreen.Draw();
			commands.EndRenderPass();

			commands.State.SetDepth(true, true);
			commands.State.Culling = FaceCulling.Back;

			// Publish for the material shaders (same contract as GIResolvePass).
			// LEGACY(gl): Lighting.cs takes the resolve buffers as OpenGL texture objects
			gi.ResolvedDiffuse = target.ColorAttachments[0].Handle;
			gi.ResolvedSpecular = target.ColorAttachments[1].Handle;
			gi.ResolvedNormals = context.NormalRoughness.Handle;
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
			ProbesReady = true;
			outputIndex = 1 - outputIndex;
			pingPong = 1 - pingPong;
		}

		/// <summary>
		///     Uniforms for Engine/ProbeDebug.shader after this frame's gather: the probe layout, this frame's
		///     anchors (the buffers have already been swapped for the next frame) and the adaptive tile lists.
		/// </summary>
		public void SetDebugUniforms(ShaderProgram program, RenderContext context)
		{
			int current = 1 - pingPong;
			program.SetTexture("ProbeAnchorPosition", anchorTargets[current].ColorAttachments[0]);
			program.SetInt2("screenSize", context.Width, context.Height);
			program.SetInt2("probeCount", probesX, probesY);
			program.SetInt("probeAtlasRows", atlasRows);
			program.SetInt("probeSpacing", lastSpacing);
			program.SetInt2("probeJitter", previousProbeJitter.X, previousProbeJitter.Y);
			CommandList commands = Gfx.Commands;
			commands.SetStorageBuffer(AdaptiveBufferBinding, adaptiveBuffers[current]);
			commands.SetStorageBuffer(PreviousAdaptiveBufferBinding, adaptiveBuffers[pingPong]);
			commands.SetStorageBuffer(AdaptiveLevelBinding, adaptiveLevelBuffer);
		}

		public override void Resize(int width, int height)
		{
			historyValid = false;
		}

		public override void ReleaseUnusedResources()
		{
			if (!Lighting.GI.LumenActive)
			{
				Release();
			}
		}

		public override void Dispose()
		{
			Release();
		}

		#endregion

		#region PrivateMethods

		private void Release()
		{
			anchors?.Delete();
			adaptiveClear?.Delete();
			adaptiveSnapshot?.Delete();
			placement?.Delete();
			importance?.Delete();
			trace?.Delete();
			compose?.Delete();
			filter?.Delete();
			sh?.Delete();
			integrate?.Delete();
			anchors = null;
			adaptiveClear = null;
			adaptiveSnapshot = null;
			placement = null;
			importance = null;
			trace = null;
			compose = null;
			filter = null;
			sh = null;
			integrate = null;
			adaptiveLevelBuffer?.Dispose();
			adaptiveLevelBuffer = null;
			traceTarget?.Delete();
			traceTarget = null;
			filterScratch?.Delete();
			filterScratch = null;
			selectionTexture?.Dispose();
			selectionTexture = null;
			rayTableTexture?.Dispose();
			rayTableTexture = null;
			probeChangeTexture?.Dispose();
			probeChangeTexture = null;
			adaptivePixelTexture?.Dispose();
			adaptivePixelTexture = null;
			readbackFence?.Dispose();
			readbackFence = null;
			readbackBuffer?.Dispose();
			readbackBuffer = null;
			for (int i = 0; i < 2; i++)
			{
				adaptiveBuffers[i]?.Dispose();
				adaptiveBuffers[i] = null;
				anchorTargets[i]?.Delete();
				composeTargets[i]?.Delete();
				filterTargets[i]?.Delete();
				output[i]?.Delete();
				anchorTargets[i] = null;
				composeTargets[i] = null;
				filterTargets[i] = null;
				output[i] = null;
			}
			shTexture?.Dispose();
			shTexture = null;
			// The next run sizes its targets again (EnsureTargets) and starts without history.
			probesX = 0;
			probesY = 0;
			historyValid = false;
			ProbesReady = false;
			AdaptiveProbeCount = 0;
		}

		private void SetProbeUniforms(ShaderProgram program, RenderContext context, int spacing, Vector2i probeJitter)
		{
			program.SetTexture("SceneDepth", context.SceneDepth);
			program.SetTexture("NormalRoughness", context.NormalRoughness);
			program.SetTexture("ProbeAnchorPosition", anchorTarget.ColorAttachments[0]);
			program.SetTexture("ProbeAnchorNormal", anchorTarget.ColorAttachments[1]);
			// Inverted once per frame, not once per shader.
			if (context.ViewProjection != cachedViewProjection)
			{
				cachedViewProjection = context.ViewProjection;
				cachedInverseViewProjection = Matrix4.Invert(cachedViewProjection);
			}
			program.SetMatrix4("invViewProjection", cachedInverseViewProjection);
			program.SetInt2("screenSize", context.Width, context.Height);
			program.SetInt2("probeCount", probesX, probesY);
			program.SetInt("probeAtlasRows", atlasRows);
			program.SetInt("probeSpacing", spacing);
			program.SetInt2("probeJitter", probeJitter.X, probeJitter.Y);
			program.SetVector2("directionJitter", currentDirectionJitter);
		}

		private void EnsureTargets(int width, int height, int spacing, float resolveScale, float adaptiveFraction)
		{
			int px = (width + spacing - 1) / spacing;
			int py = (height + spacing - 1) / spacing;
			int ow = Math.Max(1, (int)Math.Ceiling(width * resolveScale));
			int oh = Math.Max(1, (int)Math.Ceiling(height * resolveScale));
			int wantedAdaptive = (int)(px * py * MathHelper.Clamp(adaptiveFraction, 0f, 4f));
			int rows = wantedAdaptive > 0 ? (wantedAdaptive + px - 1) / px : 0;
			if (px == probesX && py == probesY && rows == adaptiveRows && ow == outputWidth && oh == outputHeight && traceTarget != null)
			{
				return;
			}

			probesX = px;
			probesY = py;
			adaptiveRows = rows;
			adaptiveMax = rows * px;
			atlasRows = py + rows;
			outputWidth = ow;
			outputHeight = oh;
			historyValid = false;

			int rayWidth = px * ProbeResolution;
			int rayHeight = atlasRows * ProbeResolution;
			for (int i = 0; i < 2; i++)
			{
				anchorTargets[i]?.Delete();
				anchorTargets[i] = new Framebuffer(px, atlasRows);
				// The adaptive probes' anchors are written into the rows below the grid as images, as are the composed and
				// the filtered maps.
				anchorTargets[i].AddColorAttachment(RenderTextureFormat.RGBA32F, false, true);
				anchorTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false, true);
				anchorTargets[i].Validate();

				composeTargets[i]?.Delete();
				composeTargets[i] = new Framebuffer(rayWidth, rayHeight);
				composeTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false, true); // accumulated radiance
				composeTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false, true); // per texel: samples, second moment, moving-hit age, fast samples
				composeTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false, true); // fast history (anti-lag)
				composeTargets[i].Validate();

				filterTargets[i]?.Delete();
				filterTargets[i] = new Framebuffer(rayWidth, rayHeight);
				filterTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false, true);
				filterTargets[i].Validate();
			}

			traceTarget?.Delete();
			traceTarget = new Framebuffer(rayWidth, rayHeight);
			traceTarget.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
			traceTarget.Validate();

			filterScratch?.Delete();
			filterScratch = new RenderTexture(rayWidth, rayHeight, RenderTextureFormat.RGBA16F, false, SamplerAddress.ClampToEdge, true);

			GraphicsDevice device = Gfx.Device;
			const TextureUsage written = TextureUsage.Sampled | TextureUsage.Storage;
			selectionTexture?.Dispose();
			selectionTexture = device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.RGBA32UI, px, atlasRows, 1, written, "Screen probe selection"));
			selectionTexture.Sampler = Samplers.PointClamp;

			// Ray table: per ray of every probe, the direction it samples (Engine/ScreenProbes.glsl, PackRay).
			rayTableTexture?.Dispose();
			rayTableTexture = device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.R16UI, rayWidth, rayHeight, 1, written, "Screen probe ray table"));
			rayTableTexture.Sampler = Samplers.PointClamp;

			// Per probe: the fraction of its radiance that changed this frame (the per-pixel temporal filter's fast update).
			probeChangeTexture?.Dispose();
			probeChangeTexture = device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.R16F, px, atlasRows, 1, written, "Screen probe change"));
			probeChangeTexture.Sampler = Samplers.PointRepeat;

			adaptivePixelTexture?.Dispose();
			adaptivePixelTexture = device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.R32UI, px, Math.Max(1, rows), 1, written, "Screen probe adaptive pixels"));
			adaptivePixelTexture.Sampler = Samplers.PointClamp;

			// Tile lists: count + (index, pixel) pairs per uniform cell, see Engine/AdaptiveProbes.glsl. Zeroed so
			// the very first frame's "previous" lists are empty.
			uint[] tileZeros = new uint[1 + px * py * AdaptiveTileStride];
			for (int i = 0; i < 2; i++)
			{
				adaptiveBuffers[i].SetData(tileZeros);
			}
			adaptiveLevelBuffer.Allocate(Math.Max(1, px * py) * 2 * sizeof(uint));

			// 27 SH coefficient values + valid flag packed into 7 RGBA layers (Engine/ProbeSH.shader).
			shTexture?.Dispose();
			shTexture = device.CreateTexture(TextureDescriptor.Texture2DArray(TextureFormat.RGBA16F, px, atlasRows, 7, 1, written, "Screen probe SH"));
			shTexture.Sampler = Samplers.PointRepeat;

			for (int i = 0; i < 2; i++)
			{
				output[i]?.Delete();
				output[i] = new Framebuffer(ow, oh);
				output[i].AddColorAttachment(RenderTextureFormat.RGBA16F);
				output[i].AddColorAttachment(RenderTextureFormat.RGBA16F);
				// History record for the next frame's temporal filter: normal and frame count per texel.
				output[i].AddColorAttachment(RenderTextureFormat.RGBA16F, false);
				output[i].Validate();
			}

			Console.WriteLine($"[ProbeGather] {px}x{py} probes every {spacing} px + up to {adaptiveMax} adaptive, {rayWidth}x{rayHeight} rays, resolve {ow}x{oh}");
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
