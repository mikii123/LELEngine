using System;
using LELEngine.Rendering;

namespace LELEngine.Editor
{
	/// <summary>
	///     A renderer drawing into a texture shown by an editor panel (scene view, game view). Each view has its
	///     own renderer, so its GI histories (temporal accumulation, caches) follow its own camera.
	/// </summary>
	internal sealed class ViewRenderer : IDisposable
	{
		#region PublicFields

		public Renderer Renderer { get; }
		public int Width => output.Width;
		public int Height => output.Height;

		/// <summary>GL texture of the last rendered frame (bottom-up, tonemapped).</summary>
		public int Texture => output.ColorAttachments[0].Handle;

		#endregion

		#region PrivateFields

		private readonly Framebuffer output;

		#endregion

		#region Constructors

		public ViewRenderer(int width, int height)
		{
			width = Math.Max(16, width);
			height = Math.Max(16, height);
			Renderer = new Renderer(width, height);
			Renderer.AddDefaultPasses();
			output = new Framebuffer(width, height);
			output.AddColorAttachment(RenderTextureFormat.RGBA8);
			output.Validate();
			Renderer.OutputTarget = output;
		}

		#endregion

		#region PublicMethods

		public void EnsureSize(int width, int height)
		{
			width = Math.Max(16, width);
			height = Math.Max(16, height);
			if (width == output.Width && height == output.Height)
			{
				return;
			}

			output.Resize(width, height);
			Renderer.Resize(width, height);
		}

		public void Render(Camera camera, Scene scene)
		{
			if (scene == null)
			{
				Renderer.RenderFrame(null, Array.Empty<MeshRenderer>(), Array.Empty<Behaviour>(), RenderQueue.PerShader);
				return;
			}

			Renderer.RenderFrame(camera, scene.Renderers, scene.ActiveBehaviours, RenderQueue.PerShader);
		}

		public void Dispose()
		{
			Renderer.Dispose();
			output.Delete();
		}

		#endregion
	}
}
