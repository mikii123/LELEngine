using System;
using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using LELEngine.Rendering.Passes;
using LELEngine.Shaders;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Owns the frame pipeline: the HDR scene target, engine shaders and an ordered list of passes.
	///     Default pipeline: Shadows -> GI -> DepthPrepass -> Opaque -> Skybox -> Transparent -> PostRender callbacks
	///     -> PostProcess. A frame renders a camera stack (<see cref="Camera.Stack" />, one camera without overlays):
	///     the passes run by their <see cref="RenderPass.Scope" />. The frame's renderers are split per camera by its
	///     layers and by their materials' queues: the opaque sub-meshes feed every geometry pass, the transparent ones
	///     (<see cref="MeshRenderer.HasTransparentParts" />) only the transparent pass.
	/// </summary>
	public sealed partial class Renderer
	{
		#region PublicFields

		public RenderSettings Settings { get; } = new RenderSettings();
		public GpuProfiler Profiler { get; } = new GpuProfiler();
		public FrameDumper FrameDumper { get; } = new FrameDumper();

		/// <summary>Region luminance and temporal noise readbacks for automated measurements (see <see cref="FrameStatistics" />).</summary>
		public FrameStatistics Statistics { get; } = new FrameStatistics();

		/// <summary>GI view of the scene: distance fields, surface cache cards and the per-frame object tables.</summary>
		public LumenScene LumenScene { get; private set; }

		/// <summary>The frame's renderers on the GPU for GPU-driven drawing (<see cref="RenderSettings.GpuDriven" />).</summary>
		public GpuScene GpuScene { get; } = new GpuScene();

		/// <summary>Last frame's world matrices of the renderers, for the motion vectors of the geometry prepass.</summary>
		public ObjectMotion Motion { get; } = new ObjectMotion();

		/// <summary>Frames rendered so far; the one counter the passes share (per-pass counters can drift apart when passes are toggled).</summary>
		public int FrameIndex { get; private set; }

		/// <summary>
		///     What the last <see cref="RenderFrame" /> cost on the CPU and drew (draw calls, triangles, renderers, lights, sizes);
		///     its GPU time is <see cref="Profiler" />'s.
		/// </summary>
		public RenderFrameStatistics LastFrame { get; private set; }

		/// <summary>
		///     LEL_PASS_STATS=1: every renderer logs what each of its passes cost on the GPU, every 120th frame of its own
		///     (`[PassStats] ...`): the costs of a project's own scene, in the editor's views or a player.
		/// </summary>
		public static readonly bool PassStatistics = Environment.GetEnvironmentVariable("LEL_PASS_STATS") == "1";

		/// <summary>
		///     LEL_GPU_TIMELINE=1: every renderer logs, every 120th frame of its own, where each GPU section started and ended
		///     in the frame (`[GpuTimeline] ...`, milliseconds from the frame's start): the overlap of the asynchronous
		///     compute queue's sections with the graphics queue's.
		/// </summary>
		public static readonly bool GpuTimeline = Environment.GetEnvironmentVariable("LEL_GPU_TIMELINE") == "1";

		/// <summary>
		///     LEL_ASYNC_PASSES=name,name: the passes (by <see cref="RenderPass.Name" />) whose compute work goes to the
		///     asynchronous compute queue, in place of those that say so (<see cref="RenderPass.AsyncCompute" />): to measure
		///     which passes gain from it. Empty: none.
		/// </summary>
		private static readonly string[] asyncPasses = Environment.GetEnvironmentVariable("LEL_ASYNC_PASSES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		/// <summary>
		///     A measurement in one run, robust to what else the machine does: LEL_AB=async (the asynchronous compute queue),
		///     LEL_AB=prepass (the geometry prepass's background drawn instead of a clear) or LEL_AB=transmittance (the
		///     transparent shadows' maps cleared only where casters drew) is switched off for every other block of
		///     LEL_AB_FRAMES frames (default 30; the pictures of such a run are not to be compared), and every 600 frames the
		///     renderer logs the median frame time (wall clock from one frame's start to the next: the GPU's throughput when
		///     it is the bottleneck) and GPU frame wall of both, leaving out the first frames of each block (`[AB] ...`).
		/// </summary>
		public static readonly string ABExperiment = Environment.GetEnvironmentVariable("LEL_AB");

		/// <summary>
		///     LEL_FINISH_EACH_PASS=1: the console names every pass as it ends and the CPU waits for the GPU to finish it, so a
		///     pass that hangs the GPU or loses the device is the last one named (a diagnosis; very slow).
		/// </summary>
		public static readonly bool FinishEachPass = Environment.GetEnvironmentVariable("LEL_FINISH_EACH_PASS") == "1";

		private static readonly int abFrames = int.TryParse(Environment.GetEnvironmentVariable("LEL_AB_FRAMES"), out int frames) && frames > 4 ? frames : 30;

		/// <summary>The frame being rendered is in a block with <see cref="ABExperiment" /> switched off.</summary>
		public bool ABOff { get; private set; }

		private long abLastStart;
		private bool abLastOff;
		private readonly List<double>[] abIntervals = { new List<double>(), new List<double>() };
		private readonly List<double>[] abWalls = { new List<double>(), new List<double>() };

		/// <summary>Notes the frame time of the frame before (by its block) and picks this frame's block.</summary>
		private void NoteABFrame()
		{
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			int inBlock = renderedFrames % abFrames;
			if (abLastStart != 0 && inBlock > 3)
			{
				// The first frames of a block still have the other block's work in flight: left out.
				int side = abLastOff ? 1 : 0;
				abIntervals[side].Add((now - abLastStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
				abWalls[side].Add(Profiler.FrameWallMilliseconds);
			}

			abLastStart = now;
			ABOff = renderedFrames / abFrames % 2 == 1;
			abLastOff = ABOff;
			if (renderedFrames > 0 && renderedFrames % 600 == 0 && abIntervals[0].Count > 0 && abIntervals[1].Count > 0)
			{
				Console.WriteLine($"[AB] {ABExperiment} on: frame {Median(abIntervals[0]):0.000} ms, GPU wall {Median(abWalls[0]):0.000} ms ({abIntervals[0].Count} frames) | off: frame {Median(abIntervals[1]):0.000} ms, GPU wall {Median(abWalls[1]):0.000} ms ({abIntervals[1].Count} frames)");
			}
		}

		private static double Median(List<double> values)
		{
			List<double> sorted = new List<double>(values);
			sorted.Sort();
			return sorted[sorted.Count / 2];
		}

		public Framebuffer SceneTarget { get; private set; }
		public RenderTexture SceneColor => SceneTarget.ColorAttachments[0];
		public RenderTexture SceneDepth => SceneTarget.DepthAttachment;
		public ShaderProgram DepthOnlyProgram { get; private set; }

		/// <summary>Transparent shadow casters: transmittance and depth (Engine/DepthOnly.shader, LEL_SHADOW_TRANSMITTANCE).</summary>
		public ShaderProgram TransparentShadowProgram { get; private set; }

		public FullscreenQuad Fullscreen { get; private set; }
		public IReadOnlyList<RenderPass> Passes => passes;

		/// <summary>The output's (window's) size: what <see cref="Resize" /> sets, the final image and the UI.</summary>
		public int Width { get; private set; }
		public int Height { get; private set; }

		/// <summary>
		///     The scene's size (<see cref="SceneTarget" />, every scene pass): <see cref="Width" /> x <see cref="Height" />
		///     times the super sampling's scale along each axis (<see cref="RenderSettings.SuperSampling" />), the same
		///     without it.
		/// </summary>
		public int RenderWidth { get; private set; }
		public int RenderHeight { get; private set; }

		/// <summary>The super sampling's scale along each axis the scene renders at now (1: none).</summary>
		public float SuperSamplingScale { get; private set; } = 1f;

		/// <summary>
		///     Where the final (tonemapped) image goes: null renders to the window's default framebuffer, otherwise
		///     to this framebuffer's first color attachment (editor viewports). Its size should match
		///     <see cref="Width" /> x <see cref="Height" />.
		/// </summary>
		public Framebuffer OutputTarget { get; set; }

		/// <summary>
		///     Objects this renderer leaves out (null: none): true for an object nothing of it draws, in any camera of the
		///     stack, its shadows and GI included (mesh renderers, trails, custom renderers, visual effects, volumetric
		///     objects). Asked for every drawable object each frame, so keep it cheap. Other renderers of the same scene
		///     (another view, cameras rendering into textures) draw them as usual.
		/// </summary>
		public Func<GameObject, bool> HiddenObjects { get; set; }

		#endregion

		#region PrivateFields

		private readonly List<RenderPass> passes = new List<RenderPass>();
		private readonly Dictionary<Camera, TargetRenderer> targetRenderers = new Dictionary<Camera, TargetRenderer>();
		private readonly Dictionary<Camera, int> targetFrame = new Dictionary<Camera, int>();
		private readonly List<Camera> staleTargets = new List<Camera>();
		private bool isTargetRenderer;

		/// <summary>The cameras of the frame being rendered: the base camera, then its overlays.</summary>
		private readonly List<Camera> stack = new List<Camera>();

		/// <summary>The renderers of each camera of <see cref="stack" /> (same index), on its layers.</summary>
		private readonly List<CameraLists> cameraLists = new List<CameraLists>();

		/// <summary>The opaque renderers of every camera of a stack together (the GPU-driven tables), with more than one camera.</summary>
		private readonly List<MeshRenderer> stackOpaqueRenderers = new List<MeshRenderer>();

		private readonly Dictionary<(string, int), string> profileNames = new Dictionary<(string, int), string>();
		private Framebuffer dumpTarget;

		// The frame being rendered, for LastFrame: its cameras, the renderers in their lists, the GPU-driven instances; and the
		// frames rendered (RenderFrame calls: FrameIndex leaves out the frames without a camera).
		private int renderedFrames;
		private int frameCameras;
		private int frameOpaque;
		private int frameTransparent;
		private int frameGpuInstances;
		private int frameCulled;

		#endregion

		#region Constructors

		public Renderer(int width, int height)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);
			RenderWidth = Width;
			RenderHeight = Height;

			SceneTarget = new Framebuffer(Width, Height);
			SceneTarget.AddColorAttachment(RenderTextureFormat.RGBA16F);
			SceneTarget.AddDepthAttachment(RenderTextureFormat.Depth32F);
			SceneTarget.Validate();

			DepthOnlyProgram = BuiltinShaders.CreateDepthOnly();
			TransparentShadowProgram = new ShaderProgram("Engine/DepthOnly.shader", new[] { "LEL_SHADOW_TRANSMITTANCE" });
			Fullscreen = new FullscreenQuad();
			LumenScene = new LumenScene();
			Lighting.GIUsageChanged += ReleaseUnusedGI;

			Console.WriteLine("[Renderer] " + Gfx.Capabilities.ApiVersion + " | " + Gfx.Capabilities.Renderer);
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Installs the standard pass chain.
		/// </summary>
		public void AddDefaultPasses()
		{
			// First of every camera's passes: what they draw lit visits its cluster's lights.
			AddPass(new LightClusterPass());
			AddPass(new VfxSimulationPass());
			AddPass(new VolumetricPass());
			AddPass(new ShadowPass());
			AddPass(new VolumetricMediaPass());
			AddPass(new GlobalDistanceFieldPass());
			AddPass(new SurfaceCachePass());
			AddPass(new RadianceCachePass());
			AddPass(new VoxelGIPass());
			AddPass(new GeometryPrepass());
			AddPass(new RayTracedShadowPass());
			AddPass(new GIResolvePass());
			AddPass(new ScreenProbeGatherPass());
			AddPass(new RayTracedReflectionPass());
			AddPass(new OpaquePass());
			AddPass(new VfxOpaquePass());
			AddPass(new SkyboxPass());
			AddPass(new TransparentPass());
			AddPass(new PostRenderCallbackPass());
			AddPass(new DebugDrawPass());
			AddPass(new VoxelDebugPass());
			AddPass(new SdfDebugPass());
			AddPass(new VolumetricMediaDebugPass());
			AddPass(new SurfaceCacheDebugPass());
			AddPass(new ProbeDebugPass());
			AddPass(new PostProcessPass());
			AddPass(new UIRenderPass());
		}

		public void AddPass(RenderPass pass)
		{
			pass.Initialize(this);
			passes.Add(pass);
		}

		public void InsertPass(int index, RenderPass pass)
		{
			pass.Initialize(this);
			passes.Insert(index, pass);
		}

		/// <summary>
		///     Inserts a pass right before the first pass of the given type (or appends if none is found).
		/// </summary>
		public void InsertPassBefore<TPass>(RenderPass pass)
			where TPass : RenderPass
		{
			int index = passes.FindIndex(p => p is TPass);
			if (index < 0)
			{
				AddPass(pass);
			}
			else
			{
				InsertPass(index, pass);
			}
		}

		public TPass GetPass<TPass>()
			where TPass : RenderPass
		{
			foreach (RenderPass pass in passes)
			{
				if (pass is TPass typed)
				{
					return typed;
				}
			}

			return null;
		}

		public void RemovePass(RenderPass pass)
		{
			if (passes.Remove(pass))
			{
				pass.Dispose();
			}
		}

		/// <summary>The output's size (the window's); the scene's follows it with the super sampling.</summary>
		public void Resize(int width, int height)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);
			ResizeScene(true);
		}

		/// <summary>
		///     The scene's size from the output's and the super sampling's factor (a camera's texture renderer never
		///     super samples); its targets and the passes' resized when it changed (or <paramref name="always" />).
		/// </summary>
		private void ResizeScene(bool always)
		{
			float factor = isTargetRenderer || Settings.SuperSampling == null ? 1f : Settings.SuperSampling.Factor;
			(int width, int height) = SuperSamplingSettings.RenderSize(Width, Height, factor);
			float scale = SuperSamplingSettings.AxisScale(factor);
			if (!always && width == RenderWidth && height == RenderHeight)
			{
				return;
			}

			if (scale != SuperSamplingScale)
			{
				Console.WriteLine(scale > 1f
					? $"[Renderer] Super sampling {SuperSamplingSettings.Label(factor)}: the scene at {width}x{height} for {Width}x{Height}"
					: "[Renderer] Super sampling off");
			}

			SuperSamplingScale = scale;
			RenderWidth = width;
			RenderHeight = height;
			SceneTarget.Resize(RenderWidth, RenderHeight);
			foreach (RenderPass pass in passes)
			{
				pass.Resize(RenderWidth, RenderHeight);
			}
		}

		/// <summary>
		///     Renders <paramref name="camera" /> and, a base camera, the overlay cameras of its stack into one image: the
		///     frame passes once with the stack's lighting camera (<see cref="Camera.GetStackLightingCamera" />: shadow
		///     maps, GI volumes, its layers are the world), then for each camera in order the passes of its scope with its
		///     view and the renderers on its layers (an overlay camera clears the depth first when it asks to), then the
		///     final passes (post-processing, UI). The GPU-driven tables are built once from every camera's renderers;
		///     each camera culls them on the GPU with its frustum and layers. What the frame cost and drew:
		///     <see cref="LastFrame" />.
		/// </summary>
		public void RenderFrame(Camera camera, IReadOnlyList<MeshRenderer> renderers, IReadOnlyList<Behaviour> behaviours, RenderQueue queue)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			if (ABExperiment != null && !isTargetRenderer)
			{
				NoteABFrame();
			}

			long drawCalls = DrawStatistics.DrawCalls;
			long triangles = DrawStatistics.Triangles;
			long indirect = DrawStatistics.IndirectDrawCalls;
			frameCameras = 0;
			frameOpaque = 0;
			frameTransparent = 0;
			frameGpuInstances = 0;
			frameCulled = 0;
			try
			{
				RenderStack(camera, renderers, behaviours, queue);
			}
			finally
			{
				LastFrame = new RenderFrameStatistics
				{
					Frame = ++renderedFrames,
					CpuMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
					DrawCalls = DrawStatistics.DrawCalls - drawCalls,
					Triangles = DrawStatistics.Triangles - triangles,
					IndirectDrawCalls = DrawStatistics.IndirectDrawCalls - indirect,
					Cameras = frameCameras,
					OpaqueRenderers = frameOpaque,
					TransparentRenderers = frameTransparent,
					CulledRenderers = frameCulled,
					GpuInstances = frameGpuInstances,
					Lights = LightBuffer.Count,
					Width = Width,
					Height = Height,
					RenderWidth = RenderWidth,
					RenderHeight = RenderHeight
				};
			}
		}

		private void RenderStack(Camera camera, IReadOnlyList<MeshRenderer> renderers, IReadOnlyList<Behaviour> behaviours, RenderQueue queue)
		{
			// Static batches' parts enabled or disabled since the last frame: their triangles are hidden or shown before
			// anything draws.
			StaticBatch.ApplyChanges();
			// Objects may have moved since the last frame: the views' culling works the world bounds out again.
			ViewCulling.NewFrame();
			if (!isTargetRenderer)
			{
				using (FrameProfiler.Begin("Cameras with a target texture"))
				{
					RenderTargetCameras(camera, renderers, behaviours, queue);
				}
			}

			// A changed super sampling factor resizes the scene now.
			ResizeScene(false);
			using (FrameProfiler.Begin("Lighting.BeginFrame, LightBuffer"))
			{
				Lighting.BeginFrame(Settings);
				LightBuffer.Update();
			}
			if (camera == null)
			{
				// Nothing to render from; present the clear color and the screen-space passes (UI).
				BeginOutput(LoadOperation.Clear, Settings.ClearColor, LoadOperation.Clear);
				RenderContext overlayContext = new RenderContext
				{
					Renderer = this,
					Settings = Settings,
					Width = Width,
					Height = Height,
					OutputWidth = Width,
					OutputHeight = Height,
					Renderers = renderers,
					Behaviours = behaviours,
					RenderQueue = queue,
					SceneTarget = SceneTarget,
					OutputTarget = OutputTarget,
					DepthOnlyProgram = DepthOnlyProgram,
					TransparentShadowProgram = TransparentShadowProgram,
					Fullscreen = Fullscreen
				};
				foreach (RenderPass pass in passes)
				{
					if (pass.Enabled && pass.RunsWithoutCamera)
					{
						pass.Execute(overlayContext);
					}
				}

				return;
			}

			camera.GetRenderStack(stack);
			int lightingIndex = Math.Max(0, stack.IndexOf(camera.GetStackLightingCamera()));
			float aspect = Width / (float)Height;
			foreach (Camera view in stack)
			{
				view.UpdateMatrices(aspect);
			}

			// Frame dumps of a window read an offscreen copy: the window's own pixels are undefined where another
			// window covers it.
			bool dumpOffscreen = FrameDumper.Active && OutputTarget == null;
			if (dumpOffscreen)
			{
				EnsureDumpTarget();
				OutputTarget = dumpTarget;
			}

			// Levels of detail for this frame, seen from the lighting camera: every camera of the stack draws them.
			bool lods;
			using (FrameProfiler.Begin("LODGroup.Select"))
			{
				lods = LODGroup.Select(stack[lightingIndex]);
			}

			using (FrameProfiler.Begin("Partition (renderer lists)"))
			{
				Partition(renderers, lods, lightingIndex);
			}

			// Every camera's context, made before the frame executes: the passes declare what they make with them.
			contexts.Clear();
			for (int i = 0; i < stack.Count; i++)
			{
				contexts.Add(CreateContext(i, lightingIndex, behaviours, queue));
			}

			RenderContext lightingContext = contexts[lightingIndex];

			Profiler.BeginFrame();
			FrameIndex++;
			Motion.BeginFrame();
			DecideAsyncCompute();

			// GPU-driven geometry: the instance and material tables for this frame's culling and indirect draws (every
			// camera of the stack culls the same tables with its own frustum and layers).
			if (Settings.GpuDriven && GpuScene.IsSupported)
			{
				using (FrameProfiler.Begin("GpuScene.Build"))
				{
					GpuScene.Build(stack.Count == 1 ? cameraLists[0].Opaque : stackOpaqueRenderers, Motion);
				}

				frameGpuInstances = GpuScene.Active ? GpuScene.InstanceCount : 0;
			}
			else
			{
				GpuScene.EndFrame();
			}

			// Clear the scene target once per frame; passes begin their own passes into it when they need it.
			CommandList commands = Gfx.Commands;
			commands.State.SetDepth(true, true);
			commands.State.ColorMask = ColorWriteMask.All;
			SceneTarget.BeginCleared(Settings.ClearColor);

			// What every pass of the frame makes and uses: the frame's transient textures.
			using (FrameProfiler.Begin("Frame graph"))
			{
				PlanFrame(lightingIndex);
			}

			// Once per frame, seen from the lighting camera: shadow maps, GI volumes (its layers are the world).
			int executed = 0;
			Camera.current = stack[lightingIndex];
			Lighting.ScreenGIView = true;
			// No traced shadows of another renderer's view: this renderer's pass publishes its own.
			Lighting.RayShadows = default;
			executed = RunScope(RenderPassScope.Frame, lightingContext, executed);

			for (int i = 0; i < stack.Count; i++)
			{
				RenderContext context = contexts[i];
				if (i > 0 && stack[i].ClearDepth)
				{
					commands.State.SetDepth(true, true);
					SceneTarget.Begin(LoadOperation.Load, default, LoadOperation.Clear);
				}

				Camera.current = stack[i];
				// The lighting camera's view has the screen-space GI; the other cameras light from the GI volume.
				Lighting.ScreenGIView = i == lightingIndex;
				executed = RunCamera(context, i, executed);
				frameCulled += context.Culling.Culled;
			}

			Camera.current = stack[lightingIndex];
			Lighting.ScreenGIView = true;
			RunScope(RenderPassScope.Final, lightingContext, executed);
			Frame.EndFrame();

			Profiler.EndFrame();
			if (FrameProfiler.Enabled && !isTargetRenderer)
			{
				// The hitch profiler's GPU columns: the passes' total and the heaviest one, as the timer queries have them now.
				double total = 0;
				string heaviest = null;
				double heaviestMs = 0;
				foreach (KeyValuePair<string, double> pass in Profiler.Results)
				{
					total += pass.Value;
					if (pass.Value > heaviestMs)
					{
						heaviestMs = pass.Value;
						heaviest = pass.Key;
					}
				}

				FrameProfiler.NoteGpu(total, heaviest, heaviestMs);
			}

			if (PassStatistics && FrameIndex % 120 == 0)
			{
				// LEL_PASS_STATS=1: what each pass cost on the GPU (any host: the editor's views, a player), every 120th frame.
				string size = RenderWidth != Width || RenderHeight != Height ? $"{Width}x{Height} (scene {RenderWidth}x{RenderHeight})" : $"{Width}x{Height}";
				var line = new System.Text.StringBuilder($"[PassStats] {size} frame {FrameIndex}: GPU {Profiler.TotalMilliseconds:0.00} ms |");
				foreach (KeyValuePair<string, double> pass in Profiler.Results)
				{
					if (pass.Value >= 0.005)
					{
						line.Append($" {pass.Key} {pass.Value:0.00}");
					}
				}

				Console.WriteLine(line.ToString());
			}

			if (GpuTimeline && FrameIndex % 120 == 0)
			{
				// LEL_GPU_TIMELINE=1: where each section ran in the frame, both queues (an asynchronous compute section
				// overlaps the graphics sections it ran beside).
				var line = new System.Text.StringBuilder($"[GpuTimeline] frame {FrameIndex}: wall {Profiler.FrameWallMilliseconds:0.00} ms |");
				foreach ((string name, double start, double end) in Profiler.Timeline)
				{
					line.Append($" {name} {start:0.00}-{end:0.00}");
				}

				Console.WriteLine(line.ToString());
			}

			if (Statistics.Active)
			{
				Statistics.Capture(this);
			}

			if (FrameDumper.Active)
			{
				FrameDumper.Capture(OutputTarget != null ? OutputTarget.ColorAttachments[0].Gpu : Gfx.Device.Backbuffer, Width, Height);
			}

			if (dumpOffscreen)
			{
				OutputTarget = null;
				commands.Blit(dumpTarget.ColorAttachments[0].Gpu, 0, 0, 0, 0, Width, Height, Gfx.Device.Backbuffer, 0, 0, 0, 0, Width, Height, SamplerFilter.Nearest);
			}

			// Like Unity, the current camera is defined only while a frame renders (and a stale one would keep an
			// unloaded scene alive); the GPU scene and the renderer lists drop their references for the same reason.
			Camera.current = null;
			GpuScene.EndFrame();
			ClearLists();
			contexts.Clear();
		}

		/// <summary>The passes of one scope (frame, final) in list order.</summary>
		private int RunScope(RenderPassScope scope, RenderContext context, int executed)
		{
			foreach (RenderPass pass in passes)
			{
				if (pass.Enabled && pass.Scope == scope)
				{
					executed = Execute(pass, context, pass.Name, executed);
				}
			}

			return executed;
		}

		/// <summary>The passes of one camera of the stack in list order: every camera's, the lighting camera's, the base camera's.</summary>
		private int RunCamera(RenderContext context, int index, int executed)
		{
			foreach (RenderPass pass in passes)
			{
				if (!pass.Enabled)
				{
					continue;
				}

				bool runs = pass.Scope switch
				{
					RenderPassScope.EveryCamera => true,
					RenderPassScope.LightingCamera => context.IsLightingCamera,
					RenderPassScope.BaseCamera => context.IsBaseCamera,
					_ => false
				};

				if (runs)
				{
					executed = Execute(pass, context, ProfileName(pass, index), executed);
				}
			}

			return executed;
		}

		private int Execute(RenderPass pass, RenderContext context, string profileName, int executed)
		{
			if (context.Frame != null && Frame.EnterStep(pass))
			{
				// Nothing reads what the pass makes this frame.
				return executed;
			}

			// A pass whose compute work may overlap the graphics work around it gives it to the compute queue (its
			// timestamps too: its sections time that queue's work).
			string[] chosen = ChosenAsyncPasses;
			bool async = asyncFrame && (chosen != null ? Array.IndexOf(chosen, pass.Name) >= 0 : pass.AsyncCompute);
			CommandList commands = Gfx.Commands;
			if (async)
			{
				commands.BeginAsyncCompute();
			}

			executing = pass;
			Profiler.Begin(profileName);
			using (FrameProfiler.Begin(profileName))
			{
				pass.Execute(context);
			}

			// A part of the pass the pass gave to the compute queue ends with it (RenderContext.BeginAsyncCompute).
			EndAsyncPart();
			executing = null;
			Profiler.End();
			if (async)
			{
				commands.EndAsyncCompute();
			}

			executed++;

			// Hand work to the GPU early. The driver otherwise queues the frame's first commands until its
			// batch fills, leaving the GPU idle for milliseconds at the start of every frame; one flush
			// after the first pass starts it, flushing after every pass costs more than it gains.
			if (FinishEachPass)
			{
				// Finding the pass that hangs or loses the device: its name, then the GPU waited for.
				Console.WriteLine("[Renderer] finishing " + profileName);
				Gfx.Device.Finish();
			}
			else if (Settings.FlushBetweenPasses || (Settings.FlushAfterFirstPass && executed == 1))
			{
				Gfx.Device.Flush();
			}

			return executed;
		}

		/// <summary>A pass that runs for several cameras is timed under one name per camera (the profiler times a name once a frame).</summary>
		private string ProfileName(RenderPass pass, int cameraIndex)
		{
			if (cameraIndex == 0 || pass.Scope != RenderPassScope.EveryCamera)
			{
				return pass.Name;
			}

			if (!profileNames.TryGetValue((pass.Name, cameraIndex), out string name))
			{
				name = pass.Name + " [" + cameraIndex + "]";
				profileNames[(pass.Name, cameraIndex)] = name;
			}

			return name;
		}

		private RenderContext CreateContext(int index, int lightingIndex, IReadOnlyList<Behaviour> behaviours, RenderQueue queue)
		{
			Camera camera = stack[index];
			CameraLists lists = cameraLists[index];
			var context = new RenderContext
			{
				Renderer = this,
				Settings = Settings,
				Frame = Frame,
				CameraIndex = index,
				Camera = camera,
				CullingMask = lists.Mask,
				IsLightingCamera = index == lightingIndex,
				IsBaseCamera = index == 0,
				View = camera.ViewMatrix,
				Projection = camera.ProjectionMatrix,
				ViewProjection = camera.ViewProjectionMatrix,
				CameraPosition = camera.transform.position,
				CameraForward = camera.transform.forward,
				Width = RenderWidth,
				Height = RenderHeight,
				OutputWidth = Width,
				OutputHeight = Height,
				SuperSampling = SuperSamplingScale,
				Renderers = lists.Opaque,
				GIRenderers = index == lightingIndex && lists.HasGI ? lists.GI : lists.Opaque,
				TransparentRenderers = lists.Transparent,
				Behaviours = behaviours,
				HiddenObjects = HiddenObjects,
				RenderQueue = queue,
				SceneTarget = SceneTarget,
				OutputTarget = OutputTarget,
				DepthOnlyProgram = DepthOnlyProgram,
				TransparentShadowProgram = TransparentShadowProgram,
				Fullscreen = Fullscreen
			};
			context.Culling.Begin(context.ViewProjection, Settings.FrustumCulling);
			return context;
		}

		/// <summary>
		///     Splits the frame's renderers per camera of the stack, by the camera's layers and by their materials' queues
		///     (the lists are cleared when the frame ends): a renderer with opaque and transparent sub-meshes is in both
		///     lists of a camera, each pass draws its own sub-meshes. One pass over the renderers whatever the number of
		///     cameras; with more than one, the opaque renderers of all of them also go to the GPU-driven tables.
		///     <paramref name="lods" />: LOD groups picked their levels this frame (<see cref="LODGroup.Select" />): the
		///     levels not drawn are left out of every list, and the lighting camera gets its GI list (the groups' level 0
		///     instead of the drawn level).
		/// </summary>
		private void Partition(IReadOnlyList<MeshRenderer> renderers, bool lods, int lightingIndex)
		{
			while (cameraLists.Count < stack.Count)
			{
				cameraLists.Add(new CameraLists());
			}

			int union = 0;
			for (int i = 0; i < stack.Count; i++)
			{
				cameraLists[i].Clear();
				cameraLists[i].Mask = stack[i].CullingMask;
				cameraLists[i].HasGI = lods && i == lightingIndex;
				union |= cameraLists[i].Mask;
			}

			CameraLists lighting = cameraLists[lightingIndex];
			Func<GameObject, bool> hidden = HiddenObjects;
			stackOpaqueRenderers.Clear();
			bool several = stack.Count > 1;

			// The GI's own list: with LOD groups, and from the first renderer a static batch draws (see below).
			bool ownGI = lods;
			foreach (MeshRenderer renderer in renderers)
			{
				int bit = 1 << (renderer.gameObject.layer & 31);
				// Out of every list (cameras, shadows, GI): no camera sees its layer, the host hides its object (the
				// editor's scene visibility), or a script switched its drawing off (MeshRenderer.forceRenderingOff).
				if ((union & bit) == 0 || renderer.forceRenderingOff || hidden != null && hidden(renderer.gameObject))
				{
					continue;
				}

				bool opaque = renderer.HasOpaqueParts;
				bool transparent = renderer.HasTransparentParts;

				// A part of a static batch (a game build's static batching): the batch's renderer draws its mesh for the
				// cameras and the shadow maps, the GI keeps the part itself (the batch's renderer is not in the GI), so the
				// lighting is the same with batches and without.
				bool batched = renderer.batch != null && renderer.Batch != null;
				if (batched && !ownGI)
				{
					// Until now the GI's renderers were the lighting camera's opaque ones: from here on its list is its own.
					ownGI = true;
					lighting.HasGI = true;
					lighting.GI.AddRange(lighting.Opaque);
				}

				if (ownGI)
				{
					// Without LOD groups nothing is hidden or out of the GI by a level (LODGroup.Select moved the stamps on).
					if (opaque && (lighting.Mask & bit) != 0 && !LODGroup.IsOutOfGI(renderer))
					{
						lighting.GI.Add(renderer);
					}

					if (batched || LODGroup.IsHidden(renderer))
					{
						continue;
					}
				}

				for (int i = 0; i < stack.Count; i++)
				{
					CameraLists lists = cameraLists[i];
					if ((lists.Mask & bit) == 0)
					{
						continue;
					}

					if (opaque)
					{
						lists.Opaque.Add(renderer);
					}

					if (transparent)
					{
						lists.Transparent.Add(renderer);
					}
				}

				if (several && opaque)
				{
					stackOpaqueRenderers.Add(renderer);
				}
			}

			// What the frame's statistics count (LastFrame): the cameras and the renderers in their lists.
			frameCameras = stack.Count;
			for (int i = 0; i < stack.Count; i++)
			{
				frameOpaque += cameraLists[i].Opaque.Count;
				frameTransparent += cameraLists[i].Transparent.Count;
			}
		}

		private void ClearLists()
		{
			foreach (CameraLists lists in cameraLists)
			{
				lists.Clear();
			}

			stackOpaqueRenderers.Clear();
			stack.Clear();
		}

		// ------------------------------------------------------------------ cameras rendering into textures

		/// <summary>A renderer of one camera's <see cref="Camera.targetTexture" />: its own passes and scene target, the texture as the output.</summary>
		private sealed class TargetRenderer
		{
			public Renderer Renderer;
			public Framebuffer Output;
			public RenderTexture Texture;

			public void Release()
			{
				Output?.Delete();
				Output = null;
				Renderer?.Dispose();
				Renderer = null;
				Texture = null;
			}
		}

		/// <summary>
		///     The passes of a camera rendering into a texture: shadows, the geometry and the display transform. No GI,
		///     sky, VFX, debug views or UI: the texture shows the camera's layers lit by the scene's light.
		/// </summary>
		public void AddTargetTexturePasses()
		{
			AddPass(new LightClusterPass());
			AddPass(new ShadowPass());
			AddPass(new OpaquePass());
			AddPass(new TransparentPass());
			AddPass(new PostProcessPass());
		}

		/// <summary>
		///     Renders every enabled camera of the frame that has a <see cref="Camera.targetTexture" /> into it, each
		///     through a renderer of its own (<see cref="AddTargetTexturePasses" />), before the screen's frame. The
		///     textures are ready for the UI pass of this frame.
		/// </summary>
		private void RenderTargetCameras(Camera screenCamera, IReadOnlyList<MeshRenderer> renderers, IReadOnlyList<Behaviour> behaviours, RenderQueue queue)
		{
			if (targetRenderers.Count == 0 && !AnyTargetCamera(behaviours))
			{
				return;
			}

			if (behaviours != null)
			{
				foreach (Behaviour behaviour in behaviours)
				{
					if (!(behaviour is Camera camera) || camera == screenCamera || camera.targetTexture == null || camera.IsDestroyed || !camera.isActiveAndEnabled)
					{
						continue;
					}

					RenderTexture texture = camera.targetTexture;
					if (texture.Gpu == null || texture.Width <= 0 || texture.Height <= 0)
					{
						continue;
					}

					TargetRenderer target;
					if (!targetRenderers.TryGetValue(camera, out target))
					{
						target = new TargetRenderer();
						targetRenderers[camera] = target;
					}

					if (target.Texture != texture || target.Output == null || target.Output.Width != texture.Width || target.Output.Height != texture.Height)
					{
						target.Output?.Delete();
						target.Output = new Framebuffer(texture.Width, texture.Height);
						target.Output.AttachColor(texture);
						target.Output.Validate();
						target.Texture = texture;
						if (target.Renderer == null)
						{
							target.Renderer = new Renderer(texture.Width, texture.Height) { isTargetRenderer = true };
							target.Renderer.AddTargetTexturePasses();
						}
						else
						{
							target.Renderer.Resize(texture.Width, texture.Height);
						}

						target.Renderer.OutputTarget = target.Output;
					}

					RenderSettings settings = target.Renderer.Settings;
					settings.ClearColor = camera.backgroundColor;
					settings.KeepClearColor = true;
					settings.PreserveAlpha = true;
					settings.Tonemap = Settings.Tonemap;
					settings.Exposure = Settings.Exposure;
					settings.GpuDriven = Settings.GpuDriven;
					settings.FrustumCulling = Settings.FrustumCulling;
					settings.RenderUI = false;
					settings.DebugPrimitives = false;
					target.Renderer.RenderFrame(camera, renderers, Array.Empty<Behaviour>(), queue);
					targetFrame[camera] = FrameIndex;
				}
			}

			// Cameras gone, disabled or without a texture any more: free their renderers.
			if (targetRenderers.Count > 0)
			{
				staleTargets.Clear();
				foreach (KeyValuePair<Camera, TargetRenderer> pair in targetRenderers)
				{
					Camera camera = pair.Key;
					int lastFrame;
					bool rendered = targetFrame.TryGetValue(camera, out lastFrame) && lastFrame == FrameIndex;
					if (!rendered && (camera.IsDestroyed || camera.targetTexture == null || !camera.isActiveAndEnabled))
					{
						staleTargets.Add(camera);
					}
				}

				foreach (Camera camera in staleTargets)
				{
					targetRenderers[camera].Release();
					targetRenderers.Remove(camera);
					targetFrame.Remove(camera);
				}
			}
		}

		private static bool AnyTargetCamera(IReadOnlyList<Behaviour> behaviours)
		{
			if (behaviours == null)
			{
				return false;
			}

			foreach (Behaviour behaviour in behaviours)
			{
				if (behaviour is Camera camera && camera.targetTexture != null)
				{
					return true;
				}
			}

			return false;
		}

		private void ReleaseTargetRenderers()
		{
			foreach (TargetRenderer target in targetRenderers.Values)
			{
				target.Release();
			}

			targetRenderers.Clear();
			targetFrame.Clear();
		}

		private void EnsureDumpTarget()
		{
			if (dumpTarget != null && dumpTarget.Width == Width && dumpTarget.Height == Height)
			{
				return;
			}

			dumpTarget?.Delete();
			dumpTarget = new Framebuffer(Width, Height);
			dumpTarget.AddColorAttachment(RenderTextureFormat.RGBA8);
			dumpTarget.Validate();
		}

		/// <summary>
		///     Begins a render pass into the frame's final destination (<see cref="OutputTarget" />, or the window's back
		///     buffer with a viewport of the output's size). The colour is loaded or cleared as asked; the depth too when
		///     the output target has one (the back buffer has none).
		/// </summary>
		public void BeginOutput(LoadOperation color = LoadOperation.Load, ClearValue clearColor = default, LoadOperation depth = LoadOperation.Load, float clearDepth = 1f)
		{
			if (OutputTarget != null)
			{
				OutputTarget.Begin(color, clearColor, depth, clearDepth);
			}
			else
			{
				CommandList commands = Gfx.Commands;
				commands.BeginRenderPass(Gfx.Device.Backbuffer, null, color, clearColor);
				commands.SetViewport(0, 0, Width, Height);
			}
		}

		/// <summary>
		///     Drops everything the GI keeps per scene object (surface cache cards, distance field instances, object
		///     tables) and marks the static caches dirty. Call when the rendered scene is replaced (scene load,
		///     editor reload): the cached state is keyed by the old scene's renderers.
		/// </summary>
		public void InvalidateSceneCaches()
		{
			LumenScene?.Delete();
			LumenScene = new LumenScene();
			foreach (RenderPass pass in passes)
			{
				pass.ReleaseSceneReferences();
			}

			GpuScene.EndFrame();
			Motion.Clear();
			ClearLists();
			Lighting.GI.InvalidateStatic();
			// The new scene's meshes get their acceleration structures in its first frame, the load's, not over later ones.
			MeshAccelerationStructures.BuildEverythingNext();
		}

		// What the GI uses changed: free the rest now, also while this renderer does not draw (a hidden editor view).
		private void ReleaseUnusedGI()
		{
			foreach (RenderPass pass in passes)
			{
				pass.ReleaseUnusedResources();
			}

			GlobalIlluminationSettings gi = Lighting.GI;
			if (!gi.LumenActive)
			{
				LumenScene.ReleaseSurfaceCache();
				if (!gi.DistanceFieldActive && !Lighting.VoxelRayTracingActive)
				{
					ReleaseLumenScene();
				}
			}

			// The rays trace the triangles and nothing builds the global field: the objects keep no fields.
			if (Lighting.SceneRayTracingActive && !gi.DistanceFieldActive)
			{
				LumenScene.ReleaseDistanceFields();
			}

			// The GI's rays no longer trace with ray queries: the scene's top level goes, and the meshes' bottom levels when
			// no traced shadows use them either.
			if (!Lighting.SceneRayTracingActive)
			{
				LumenScene.ReleaseRayTracing();
				if (!Lighting.RayTracedLocalShadows && !Lighting.RayTracedDirectionalShadows)
				{
					MeshAccelerationStructures.Clear();
				}
			}
		}

		/// <summary>Frees the GI's scene (distance fields, surface cache, object tables) once no GI pass reads it.</summary>
		internal void ReleaseLumenScene()
		{
			if (LumenScene.Allocated)
			{
				LumenScene.Delete();
				LumenScene = new LumenScene();
				Console.WriteLine("[GI] Scene distance fields and tables released");
			}
		}

		public void Dispose()
		{
			Lighting.GIUsageChanged -= ReleaseUnusedGI;
			ReleaseTargetRenderers();
			foreach (RenderPass pass in passes)
			{
				pass.Dispose();
			}
			passes.Clear();
			GpuScene.Dispose();
			VFX.VfxRuntime.ReleaseRenderer(this);

			Frame.Release();
			SceneTarget?.Delete();
			dumpTarget?.Delete();
			DepthOnlyProgram?.Delete();
			TransparentShadowProgram?.Delete();
			Fullscreen?.Delete();
			LumenScene?.Delete();
			Profiler.Dispose();
			Statistics.Dispose();
		}

		#endregion

		#region NestedTypes

		/// <summary>One camera's renderers in a frame: on its layers, split by the materials' queues.</summary>
		private sealed class CameraLists
		{
			public readonly List<MeshRenderer> Opaque = new List<MeshRenderer>();
			public readonly List<MeshRenderer> Transparent = new List<MeshRenderer>();

			// The lighting camera's GI world when LOD groups are active (their level 0 in place of the drawn level).
			public readonly List<MeshRenderer> GI = new List<MeshRenderer>();
			public bool HasGI;
			public int Mask = -1;

			public void Clear()
			{
				Opaque.Clear();
				Transparent.Clear();
				GI.Clear();
				HasGI = false;
			}
		}

		#endregion
	}
}
