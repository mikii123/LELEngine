using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Lumen surface cache: captures every GI object's six cards (albedo, normal, emission, position) into the
	///     atlas and lights the atlas every frame: direct sun light with shadows plus radiosity (hemisphere rays
	///     reading the previous frame's final lighting) accumulated over time, giving multi-bounce lighting.
	///     Static objects are captured once; dynamic objects (whose materials may animate) every frame.
	/// </summary>
	public sealed class SurfaceCachePass : RenderPass
	{
		#region PublicFields

		public override string Name => "SurfaceCache";

		#endregion

		#region PrivateFields

		private ShaderProgram capture;
		private ComputeShader lighting;
		private GridShadowMap gridShadow;
		private int frameIndex;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			capture = new ShaderProgram("Engine/CardCapture.shader");
			lighting = new ComputeShader("Engine/CardLighting.shader");
			gridShadow = new GridShadowMap(Lighting.GI.GridShadowMapSize);
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			if (!gi.Enabled || gi.Mode != GIMode.Lumen)
			{
				return;
			}

			LumenScene scene = context.Renderer.LumenScene;
			frameIndex++;
			scene.Update(frameIndex, context.Renderers, gi);
			gridShadow.EnsureSize(gi.GridShadowMapSize);

			GpuProfiler profiler = context.Renderer.Profiler;

			profiler.Split("SurfaceCache.capture");
			CaptureCards(scene);

			profiler.Split("SurfaceCache.shadow");
			gridShadow.Render(context, context.Renderers, gi);

			profiler.Split("SurfaceCache.lighting");
			LightCards(context.Renderer, scene, gi);
		}

		public override void Dispose()
		{
			capture?.Delete();
			lighting?.Delete();
			gridShadow?.Delete();
		}

		#endregion

		#region PrivateMethods

		private void CaptureCards(LumenScene scene)
		{
			SurfaceCacheAtlas atlas = scene.SurfaceCache;
			bool bound = false;

			foreach (LumenScene.SceneObject o in scene.Objects)
			{
				if (o.Cards == null || (o.Cards.Captured && o.IsStatic))
				{
					continue;
				}

				if (!bound)
				{
					GL.BindFramebuffer(FramebufferTarget.Framebuffer, atlas.CaptureFramebuffer);
					GLState.SetDepth(true, true, DepthFunction.Less);
					GLState.SetCull(false);
					GLState.SetColorWrite(true);
					capture.Use();
					bound = true;
				}

				capture.SetVector3("objectScale", o.Field.Scale);
				SetMaterialUniforms(o.Renderer.Material);

				for (int i = 0; i < o.Cards.Cards.Length; i++)
				{
					SurfaceCard card = o.Cards.Cards[i];
					atlas.ClearRect(card.X, card.Y, card.Width, card.Height);
					GL.Viewport(card.X, card.Y, card.Width, card.Height);
					capture.SetMatrix4("cardViewProjection", card.ViewProjection);
					capture.SetInt("cardIndex", o.Cards.FirstCardIndex + i);
					o.Renderer.RenderWith(capture);
				}

				o.Cards.Captured = true;
			}

			if (bound)
			{
				GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
				GLState.SetCull(true);
			}
		}

		// Same conventional material parameters the voxelizer uses.
		private void SetMaterialUniforms(Material material)
		{
			Vector4 albedo;
			if (!material.TryGetVector4("Color", out albedo))
			{
				albedo = Vector4.One;
			}

			Vector4 emissive;
			if (!material.TryGetVector4("Emissive", out emissive))
			{
				emissive = Vector4.Zero;
			}

			int albedoMap = material.GetTextureHandle("DiffuseMap");
			if (albedoMap == 0)
			{
				albedoMap = material.GetTextureHandle("AlbedoMap");
			}

			capture.SetVector4("voxAlbedo", albedo);
			capture.SetVector4("voxEmissive", emissive);
			capture.SetInt("voxUseAlbedoMap", albedoMap != 0 ? 1 : 0);
			if (albedoMap != 0)
			{
				capture.SetTexture("voxAlbedoMap", TextureTarget.Texture2D, albedoMap, 0);
			}
		}

		private void LightCards(Renderer renderer, LumenScene scene, GlobalIlluminationSettings gi)
		{
			SurfaceCacheAtlas atlas = scene.SurfaceCache;
			ShaderProgram program = lighting.Program;

			// Last frame's final lighting becomes the radiosity input; this frame writes the other buffer.
			atlas.SwapFinalLighting();

			lighting.Use();
			scene.SetUniforms(program);
			Lighting.SetUniforms(program, false, false);
			gridShadow.SetUniforms(program);
			Lighting.SetSdfUniforms(program);
			// Radiosity rays read last frame's radiance cache beyond the near distance.
			RadianceCachePass radianceCache = renderer.GetPass<RadianceCachePass>();
			if (radianceCache != null && gi.RadianceCacheForRadiosity)
			{
				radianceCache.SetUniforms(program, 5);
			}
			else
			{
				program.SetFloat("rcNearDistance", 0f);
			}

			program.SetTexture("CardIndex", TextureTarget.Texture2D, atlas.CardIndex, 0);
			program.SetTexture("CardAlbedo", TextureTarget.Texture2D, atlas.Albedo, 1);
			program.SetTexture("CardNormal", TextureTarget.Texture2D, atlas.Normal, 2);
			program.SetTexture("CardEmissive", TextureTarget.Texture2D, atlas.Emissive, 3);
			program.SetTexture("FinalLightingPrev", TextureTarget.Texture2D, atlas.FinalLightingPrevious, 4);

			program.SetVector3("giSkyRadiance", gi.SkyRadiance);
			program.SetInt("radiosityRays", gi.RadiosityRays);
			program.SetFloat("radiosityBlend", gi.RadiosityBlend);
			program.SetInt("frameIndex", frameIndex);
			program.SetInt2("atlasRegion", atlas.Size, atlas.UsedRows);

			GL.BindImageTexture(0, atlas.IndirectLighting, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, atlas.FinalLightingCurrent, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

			lighting.DispatchThreads(atlas.Size, atlas.UsedRows, 1, 8, 8, 1);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		#endregion
	}
}
