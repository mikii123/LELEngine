using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A layer of transparent draws at half the view's resolution (particle outputs with LowResolution): a quarter of
	///     the pixels for large soft particles, whose cost is the area they cover many times over. <see cref="Begin" />
	///     binds the layer: its colour is cleared to nothing in front of the scene (alpha, what the scene shows through,
	///     1) and its depth is the farthest of each 2 x 2 of the opaque scene's, so particles hide behind what is in front of
	///     them and still reach the edges of it. The draws blend into it with their alpha taking its share of what shows
	///     through (VfxRuntime's low-resolution blend states). <see cref="End" /> lays it over the scene: each pixel takes
	///     the layer's four nearest texels weighted by how close the depth each was drawn for is to the pixel's own, so the
	///     outline of what is in front of the particles stays sharp at the full resolution.
	/// </summary>
	public sealed class LowResolutionTransparency
	{
		#region PublicFields

		/// <summary>The layer is bound and being drawn into.</summary>
		public bool Active { get; private set; }

		#endregion

		#region PrivateFields

		private Framebuffer target;
		private ShaderProgram depthProgram;
		private ShaderProgram compositeProgram;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Binds the layer for drawing, sized for the context's scene target, with its depth taken from
		///     <paramref name="sceneDepth" /> (a copy of the scene's depth: the scene target itself stays unbound).
		/// </summary>
		public void Begin(RenderContext context, RenderTexture sceneDepth)
		{
			int width = (context.SceneTarget.Width + 1) / 2;
			int height = (context.SceneTarget.Height + 1) / 2;
			EnsureTarget(width, height);
			depthProgram ??= new ShaderProgram("Engine/LowResolutionDepth.shader");
			compositeProgram ??= new ShaderProgram("Engine/LowResolutionComposite.shader");

			target.Bind();
			GL.ClearColor(0f, 0f, 0f, 1f);
			GL.Clear(ClearBufferMask.ColorBufferBit);

			// The depth: written by a pass over the layer that leaves the colour alone.
			GL.Disable(EnableCap.Blend);
			GL.Disable(EnableCap.CullFace);
			GL.Enable(EnableCap.DepthTest);
			GL.DepthFunc(DepthFunction.Always);
			GL.DepthMask(true);
			GL.ColorMask(false, false, false, false);
			depthProgram.Use();
			depthProgram.SetTexture("SceneDepth", TextureTarget.Texture2D, sceneDepth.Handle, 0);
			context.Fullscreen.Draw();
			GL.ColorMask(true, true, true, true);
			GL.DepthFunc(DepthFunction.Lequal);
			GL.DepthMask(false);
			Active = true;
		}

		/// <summary>Lays the layer over the scene target (bound again afterwards, blending off).</summary>
		public void End(RenderContext context, RenderTexture sceneDepth)
		{
			if (!Active)
			{
				return;
			}

			Active = false;
			context.SceneTarget.Bind();
			GL.Disable(EnableCap.DepthTest);
			GL.DepthMask(false);
			GL.Disable(EnableCap.CullFace);
			GL.Enable(EnableCap.Blend);
			GL.BlendFuncSeparate(BlendingFactorSrc.One, BlendingFactorDest.SrcAlpha, BlendingFactorSrc.Zero, BlendingFactorDest.One);
			compositeProgram.Use();
			compositeProgram.SetTexture("LayerColor", TextureTarget.Texture2D, target.ColorAttachments[0].Handle, 0);
			compositeProgram.SetTexture("LayerDepth", TextureTarget.Texture2D, target.DepthAttachment.Handle, 1);
			compositeProgram.SetTexture("SceneDepth", TextureTarget.Texture2D, sceneDepth.Handle, 2);
			compositeProgram.SetVector4("depthParams", SceneGrab.DepthParams(context.Projection));
			compositeProgram.SetVector2("targetSize", new Vector2(context.SceneTarget.Width, context.SceneTarget.Height));
			context.Fullscreen.Draw();
			GL.Disable(EnableCap.Blend);
			GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
			GL.Enable(EnableCap.DepthTest);
		}

		public void Delete()
		{
			target?.Delete();
			target = null;
			depthProgram?.Delete();
			compositeProgram?.Delete();
			depthProgram = null;
			compositeProgram = null;
			Active = false;
		}

		#endregion

		#region PrivateMethods

		private void EnsureTarget(int width, int height)
		{
			if (target != null && target.Width == width && target.Height == height)
			{
				return;
			}

			target?.Delete();
			target = new Framebuffer(width, height);
			target.AddColorAttachment(RenderTextureFormat.RGBA16F);
			RenderTexture depth = target.AddDepthAttachment(RenderTextureFormat.Depth32F);
			target.Validate();

			// The composite reads the depth as numbers.
			GL.BindTexture(TextureTarget.Texture2D, depth.Handle);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
			GL.BindTexture(TextureTarget.Texture2D, 0);
		}

		#endregion
	}
}
