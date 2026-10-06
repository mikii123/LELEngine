using System;
using System.Collections.Generic;
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
		public Framebuffer SceneTarget { get; private set; }
		public RenderTexture SceneColor => SceneTarget.ColorAttachments[0];
		public RenderTexture SceneDepth => SceneTarget.DepthAttachment;
		public ShaderProgram DepthOnlyProgram { get; private set; }
		public FullscreenQuad Fullscreen { get; private set; }
		public IReadOnlyList<RenderPass> Passes => passes;
		public int Width { get; private set; }
		public int Height { get; private set; }

		#endregion

		#region PrivateFields

		private readonly List<RenderPass> passes = new List<RenderPass>();

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
			AddPass(new DepthPrepass());
			AddPass(new OpaquePass());
			AddPass(new PostRenderCallbackPass());
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
			if (camera == null)
			{
				// Nothing to render from; present the clear color.
				Framebuffer.BindDefault(Width, Height);
				GL.ClearColor(Settings.ClearColor);
				GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
				return;
			}

			camera.UpdateMatrices(Width / (float)Height);

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
				DepthOnlyProgram = DepthOnlyProgram,
				Fullscreen = Fullscreen
			};

			// Clear the scene target once per frame; passes bind it themselves when they need it.
			SceneTarget.Bind();
			GLState.SetDepth(true, true);
			GLState.SetColorWrite(true);
			GL.ClearColor(Settings.ClearColor);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			foreach (RenderPass pass in passes)
			{
				if (!pass.Enabled)
				{
					continue;
				}

				pass.Execute(context);
				GLState.Reset();
			}
		}

		public void Dispose()
		{
			foreach (RenderPass pass in passes)
			{
				pass.Dispose();
			}
			passes.Clear();

			SceneTarget?.Delete();
			DepthOnlyProgram?.Delete();
			Fullscreen?.Delete();
		}

		#endregion
	}
}
