using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A layer of transparent draws at half the view's resolution (particle outputs with LowResolution, materials whose
	///     shader declares LowResolution On): a quarter of the pixels for large soft particles and surfaces, whose cost is
	///     the area they cover many times over. <see cref="Begin" />
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

			// The depth is drawn whole below.
			target.Begin(LoadOperation.Clear, ClearValue.OpaqueBlack, LoadOperation.DontCare);

			// The depth: written by a pass over the layer that leaves the colour alone.
			ref RenderState state = ref Gfx.Commands.State;
			state.Blend = false;
			state.Culling = FaceCulling.None;
			state.SetDepth(true, true, CompareFunction.Always);
			state.ColorMask = ColorWriteMask.None;
			depthProgram.Use();
			depthProgram.SetTexture("SceneDepth", sceneDepth);
			context.Fullscreen.Draw();
			state.ColorMask = ColorWriteMask.All;
			state.SetDepth(true, false, CompareFunction.LessEqual);
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
			context.SceneTarget.Begin();
			ref RenderState state = ref Gfx.Commands.State;
			state.DepthTest = false;
			state.DepthWrite = false;
			state.Culling = FaceCulling.None;
			state.SetBlend(BlendFactor.One, BlendFactor.SrcAlpha, BlendFactor.Zero, BlendFactor.One);
			compositeProgram.Use();
			compositeProgram.SetTexture("LayerColor", target.ColorAttachments[0]);
			compositeProgram.SetTexture("LayerDepth", target.DepthAttachment);
			compositeProgram.SetTexture("SceneDepth", sceneDepth);
			compositeProgram.SetVector4("depthParams", SceneGrab.DepthParams(context.Projection));
			compositeProgram.SetVector2("targetSize", new Vector2(context.SceneTarget.Width, context.SceneTarget.Height));
			context.Fullscreen.Draw();
			state.SetBlend(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);
			state.Blend = false;
			state.DepthTest = true;
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
			// The composite reads the depth as numbers: a depth attachment compares only when asked to.
			target.AddDepthAttachment(RenderTextureFormat.Depth32F);
			target.Validate();
		}

		#endregion
	}
}
