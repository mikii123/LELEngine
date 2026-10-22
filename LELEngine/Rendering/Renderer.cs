using System;
using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using LELEngine.Rendering.Passes;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

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
	public sealed class Renderer
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
		public Framebuffer SceneTarget { get; private set; }
		public RenderTexture SceneColor => SceneTarget.ColorAttachments[0];
		public RenderTexture SceneDepth => SceneTarget.DepthAttachment;
		public ShaderProgram DepthOnlyProgram { get; private set; }

		/// <summary>Transparent shadow casters: transmittance and depth (Engine/DepthOnly.shader, LEL_SHADOW_TRANSMITTANCE).</summary>
		public ShaderProgram TransparentShadowProgram { get; private set; }

		public FullscreenQuad Fullscreen { get; private set; }
		public IReadOnlyList<RenderPass> Passes => passes;
		public int Width { get; private set; }
		public int Height { get; private set; }

		/// <summary>
		///     Where the final (tonemapped) image goes: null renders to the window's default framebuffer, otherwise
		///     to this framebuffer's first color attachment (editor viewports). Its size should match
		///     <see cref="Width" /> x <see cref="Height" />.
		/// </summary>
		public Framebuffer OutputTarget { get; set; }

		#endregion

		#region PrivateFields

		private readonly List<RenderPass> passes = new List<RenderPass>();

		/// <summary>The cameras of the frame being rendered: the base camera, then its overlays.</summary>
		private readonly List<Camera> stack = new List<Camera>();

		/// <summary>The renderers of each camera of <see cref="stack" /> (same index), on its layers.</summary>
		private readonly List<CameraLists> cameraLists = new List<CameraLists>();

		/// <summary>The opaque renderers of every camera of a stack together (the GPU-driven tables), with more than one camera.</summary>
		private readonly List<MeshRenderer> stackOpaqueRenderers = new List<MeshRenderer>();

		private readonly Dictionary<(string, int), string> profileNames = new Dictionary<(string, int), string>();
		private Framebuffer dumpTarget;

		#endregion

		#region Constructors

		public Renderer(int width, int height)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);

			SceneTarget = new Framebuffer(Width, Height);
			SceneTarget.AddColorAttachment(RenderTextureFormat.RGBA16F);
			SceneTarget.AddDepthAttachment(RenderTextureFormat.Depth32F);
			SceneTarget.Validate();

			DepthOnlyProgram = BuiltinShaders.CreateDepthOnly();
			TransparentShadowProgram = new ShaderProgram("Engine/DepthOnly.shader", new[] { "LEL_SHADOW_TRANSMITTANCE" });
			Fullscreen = new FullscreenQuad();
			LumenScene = new LumenScene();

			Console.WriteLine("[Renderer] GL " + GL.GetString(StringName.Version) + " | " + GL.GetString(StringName.Renderer));
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Installs the standard pass chain.
		/// </summary>
		public void AddDefaultPasses()
		{
			AddPass(new VfxSimulationPass());
			AddPass(new ShadowPass());
			AddPass(new GlobalDistanceFieldPass());
			AddPass(new SurfaceCachePass());
			AddPass(new RadianceCachePass());
			AddPass(new VoxelGIPass());
			AddPass(new GeometryPrepass());
			AddPass(new GIResolvePass());
			AddPass(new ScreenProbeGatherPass());
			AddPass(new OpaquePass());
			AddPass(new VfxOpaquePass());
			AddPass(new SkyboxPass());
			AddPass(new TransparentPass());
			AddPass(new PostRenderCallbackPass());
			AddPass(new DebugDrawPass());
			AddPass(new VoxelDebugPass());
			AddPass(new SdfDebugPass());
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

		public void Resize(int width, int height)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);
			SceneTarget.Resize(Width, Height);

			foreach (RenderPass pass in passes)
			{
				pass.Resize(Width, Height);
			}
		}

		/// <summary>
		///     Renders <paramref name="camera" /> and, a base camera, the overlay cameras of its stack into one image: the
		///     frame passes once with the stack's lighting camera (<see cref="Camera.GetStackLightingCamera" />: shadow
		///     maps, GI volumes, its layers are the world), then for each camera in order the passes of its scope with its
		///     view and the renderers on its layers (an overlay camera clears the depth first when it asks to), then the
		///     final passes (post-processing, UI). The GPU-driven tables are built once from every camera's renderers;
		///     each camera culls them on the GPU with its frustum and layers.
		/// </summary>
		public void RenderFrame(Camera camera, IReadOnlyList<MeshRenderer> renderers, IReadOnlyList<Behaviour> behaviours, RenderQueue queue)
		{
			Lighting.BeginFrame(Settings);
			if (camera == null)
			{
				// Nothing to render from; present the clear color and the screen-space passes (UI).
				BindOutput();
				GL.ClearColor(Settings.ClearColor);
				GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
				RenderContext overlayContext = new RenderContext
				{
					Renderer = this,
					Settings = Settings,
					Width = Width,
					Height = Height,
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
						GLState.Reset();
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

			Partition(renderers);
			RenderContext lightingContext = CreateContext(lightingIndex, lightingIndex, behaviours, queue);

			Profiler.BeginFrame();
			FrameIndex++;
			Motion.BeginFrame();

			// GPU-driven geometry: the instance and material tables for this frame's culling and indirect draws (every
			// camera of the stack culls the same tables with its own frustum and layers).
			if (Settings.GpuDriven && GpuScene.IsSupported)
			{
				GpuScene.Build(stack.Count == 1 ? cameraLists[0].Opaque : stackOpaqueRenderers, Motion);
			}
			else
			{
				GpuScene.EndFrame();
			}

			// Clear the scene target once per frame; passes bind it themselves when they need it.
			SceneTarget.Bind();
			GLState.SetDepth(true, true);
			GLState.SetColorWrite(true);
			GL.ClearColor(Settings.ClearColor);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			// Once per frame, seen from the lighting camera: shadow maps, GI volumes (its layers are the world).
			int executed = 0;
			Camera.current = stack[lightingIndex];
			Lighting.ScreenGIView = true;
			executed = RunScope(RenderPassScope.Frame, lightingContext, executed);

			for (int i = 0; i < stack.Count; i++)
			{
				RenderContext context = i == lightingIndex ? lightingContext : CreateContext(i, lightingIndex, behaviours, queue);
				if (i > 0 && stack[i].ClearDepth)
				{
					SceneTarget.Bind();
					GLState.SetDepth(true, true);
					GL.Clear(ClearBufferMask.DepthBufferBit);
				}

				Camera.current = stack[i];
				// The lighting camera's view has the screen-space GI; the other cameras light from the GI volume.
				Lighting.ScreenGIView = i == lightingIndex;
				executed = RunCamera(context, i, executed);
			}

			Camera.current = stack[lightingIndex];
			Lighting.ScreenGIView = true;
			RunScope(RenderPassScope.Final, lightingContext, executed);

			Profiler.EndFrame();

			if (Statistics.Active)
			{
				Statistics.Capture(this);
			}

			if (FrameDumper.Active)
			{
				BindOutput();
				FrameDumper.Capture(Width, Height, OutputTarget != null);
			}

			if (dumpOffscreen)
			{
				OutputTarget = null;
				GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, dumpTarget.Handle);
				GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
				GL.BlitFramebuffer(0, 0, Width, Height, 0, 0, Width, Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
				GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			}

			// Like Unity, the current camera is defined only while a frame renders (and a stale one would keep an
			// unloaded scene alive); the GPU scene and the renderer lists drop their references for the same reason.
			Camera.current = null;
			GpuScene.EndFrame();
			ClearLists();
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
			Profiler.Begin(profileName);
			pass.Execute(context);
			GLState.Reset();
			Profiler.End();
			executed++;

			// Hand work to the GPU early. The driver otherwise queues the frame's first commands until its
			// batch fills, leaving the GPU idle for milliseconds at the start of every frame; one flush
			// after the first pass starts it, flushing after every pass costs more than it gains.
			if (Settings.FlushBetweenPasses || (Settings.FlushAfterFirstPass && executed == 1))
			{
				GL.Flush();
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
			return new RenderContext
			{
				Renderer = this,
				Settings = Settings,
				Camera = camera,
				CullingMask = lists.Mask,
				IsLightingCamera = index == lightingIndex,
				IsBaseCamera = index == 0,
				View = camera.ViewMatrix,
				Projection = camera.ProjectionMatrix,
				ViewProjection = camera.ViewProjectionMatrix,
				CameraPosition = camera.transform.position,
				CameraForward = camera.transform.forward,
				Width = Width,
				Height = Height,
				Renderers = lists.Opaque,
				TransparentRenderers = lists.Transparent,
				Behaviours = behaviours,
				RenderQueue = queue,
				SceneTarget = SceneTarget,
				OutputTarget = OutputTarget,
				DepthOnlyProgram = DepthOnlyProgram,
				TransparentShadowProgram = TransparentShadowProgram,
				Fullscreen = Fullscreen
			};
		}

		/// <summary>
		///     Splits the frame's renderers per camera of the stack, by the camera's layers and by their materials' queues
		///     (the lists are cleared when the frame ends): a renderer with opaque and transparent sub-meshes is in both
		///     lists of a camera, each pass draws its own sub-meshes. One pass over the renderers whatever the number of
		///     cameras; with more than one, the opaque renderers of all of them also go to the GPU-driven tables.
		/// </summary>
		private void Partition(IReadOnlyList<MeshRenderer> renderers)
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
				union |= cameraLists[i].Mask;
			}

			stackOpaqueRenderers.Clear();
			bool several = stack.Count > 1;
			foreach (MeshRenderer renderer in renderers)
			{
				int bit = 1 << (renderer.gameObject.layer & 31);
				if ((union & bit) == 0)
				{
					continue;
				}

				bool opaque = renderer.HasOpaqueParts;
				bool transparent = renderer.HasTransparentParts;
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

		/// <summary>Binds the frame's final destination (<see cref="OutputTarget" /> or the default framebuffer).</summary>
		public void BindOutput()
		{
			if (OutputTarget != null)
			{
				OutputTarget.Bind();
			}
			else
			{
				Framebuffer.BindDefault(Width, Height);
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
		}

		public void Dispose()
		{
			foreach (RenderPass pass in passes)
			{
				pass.Dispose();
			}
			passes.Clear();
			GpuScene.Dispose();
			VFX.VfxRuntime.ReleaseRenderer(this);

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
			public int Mask = -1;

			public void Clear()
			{
				Opaque.Clear();
				Transparent.Clear();
			}
		}

		#endregion
	}
}
