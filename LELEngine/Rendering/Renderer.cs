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
	///     Default pipeline: Shadows -> DepthPrepass -> Opaque -> PostRender callbacks -> PostProcess.
	/// </summary>
	public sealed class Renderer
	{
		#region PublicFields

		public RenderSettings Settings { get; } = new RenderSettings();
		public GpuProfiler Profiler { get; } = new GpuProfiler();
		public FrameDumper FrameDumper { get; } = new FrameDumper();

		/// <summary>GI view of the scene: distance fields, surface cache cards and the per-frame object tables.</summary>
		public LumenScene LumenScene { get; private set; }

		/// <summary>The frame's renderers on the GPU for GPU-driven drawing (<see cref="RenderSettings.GpuDriven" />).</summary>
		public GpuScene GpuScene { get; } = new GpuScene();

		/// <summary>Frames rendered so far; the one counter the passes share (per-pass counters can drift apart when passes are toggled).</summary>
		public int FrameIndex { get; private set; }
		public Framebuffer SceneTarget { get; private set; }
		public RenderTexture SceneColor => SceneTarget.ColorAttachments[0];
		public RenderTexture SceneDepth => SceneTarget.DepthAttachment;
		public ShaderProgram DepthOnlyProgram { get; private set; }
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
			AddPass(new ShadowPass());
			AddPass(new GlobalDistanceFieldPass());
			AddPass(new SurfaceCachePass());
			AddPass(new RadianceCachePass());
			AddPass(new VoxelGIPass());
			AddPass(new GeometryPrepass());
			AddPass(new GIResolvePass());
			AddPass(new ScreenProbeGatherPass());
			AddPass(new OpaquePass());
			AddPass(new PostRenderCallbackPass());
			AddPass(new VoxelDebugPass());
			AddPass(new SdfDebugPass());
			AddPass(new SurfaceCacheDebugPass());
			AddPass(new ProbeDebugPass());
			AddPass(new PostProcessPass());
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

		public void RenderFrame(Camera camera, IReadOnlyList<MeshRenderer> renderers, IReadOnlyList<Behaviour> behaviours, RenderQueue queue)
		{
			Lighting.BeginFrame(Settings);
			if (camera == null)
			{
				// Nothing to render from; present the clear color.
				BindOutput();
				GL.ClearColor(Settings.ClearColor);
				GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
				return;
			}

			camera.UpdateMatrices(Width / (float)Height);
			Camera.current = camera;

			// Frame dumps of a window read an offscreen copy: the window's own pixels are undefined where another
			// window covers it.
			bool dumpOffscreen = FrameDumper.Active && OutputTarget == null;
			if (dumpOffscreen)
			{
				EnsureDumpTarget();
				OutputTarget = dumpTarget;
			}

			RenderContext context = new RenderContext
			{
				Renderer = this,
				Settings = Settings,
				Camera = camera,
				View = camera.ViewMatrix,
				Projection = camera.ProjectionMatrix,
				ViewProjection = camera.ViewProjectionMatrix,
				CameraPosition = camera.transform.position,
				Width = Width,
				Height = Height,
				Renderers = renderers,
				Behaviours = behaviours,
				RenderQueue = queue,
				SceneTarget = SceneTarget,
				OutputTarget = OutputTarget,
				DepthOnlyProgram = DepthOnlyProgram,
				Fullscreen = Fullscreen
			};

			Profiler.BeginFrame();
			FrameIndex++;

			// GPU-driven geometry: the instance and material tables for this frame's culling and indirect draws.
			if (Settings.GpuDriven && GpuScene.IsSupported)
			{
				GpuScene.Build(renderers);
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

			int executed = 0;
			foreach (RenderPass pass in passes)
			{
				if (!pass.Enabled)
				{
					continue;
				}

				Profiler.Begin(pass.Name);
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
			}

			Profiler.EndFrame();

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
			// unloaded scene alive); the GPU scene drops its renderer references for the same reason.
			Camera.current = null;
			GpuScene.EndFrame();
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

			SceneTarget?.Delete();
			dumpTarget?.Delete();
			DepthOnlyProgram?.Delete();
			Fullscreen?.Delete();
			LumenScene?.Delete();
			Profiler.Dispose();
		}

		#endregion
	}
}
