using System;
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
		private ComputeShader radiosity;
		private ComputeShader lighting;
		private GridShadowMap gridShadow;
		private int frameIndex;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			capture = new ShaderProgram("Engine/CardCapture.shader");
			radiosity = new ComputeShader("Engine/CardRadiosityProbes.shader");
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
			scene.Update(context.Renderer.FrameIndex, context.Renderers, gi);
			gridShadow.EnsureSize(gi.GridShadowMapSize);

			GpuProfiler profiler = context.Renderer.Profiler;

			profiler.Split("SurfaceCache.capture");
			CaptureCards(scene);

			profiler.Split("SurfaceCache.shadow");
			gridShadow.Render(context, context.Renderers, gi);

			LightCards(context.Renderer, scene, gi);
		}

		public override void Dispose()
		{
			capture?.Delete();
			radiosity?.Delete();
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
				if (o.Cards == null)
				{
					continue;
				}

				// Captures are transform independent (scaled-local space) and emission intensity is applied at
				// lighting time, so a card set is recaptured only when the material inputs it stores change.
				long key = CaptureKey(o.Renderer.Material);
				if (o.Cards.Captured && o.Cards.CaptureKey == key)
				{
					continue;
				}
				o.Cards.CaptureKey = key;

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

		// Hash of the material inputs baked into the cards (albedo colour, emission colour, albedo map handle).
		private static long CaptureKey(Material material)
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

			unchecked
			{
				long hash = 17;
				hash = hash * 31 + albedo.X.GetHashCode();
				hash = hash * 31 + albedo.Y.GetHashCode();
				hash = hash * 31 + albedo.Z.GetHashCode();
				hash = hash * 31 + emissive.X.GetHashCode();
				hash = hash * 31 + emissive.Y.GetHashCode();
				hash = hash * 31 + emissive.Z.GetHashCode();
				hash = hash * 31 + albedoMap;
				return hash;
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
			GpuProfiler profiler = renderer.Profiler;

			// Last frame's final lighting becomes the radiosity input; this frame writes the other buffer.
			atlas.SwapFinalLighting();
			RadianceCachePass radianceCache = renderer.GetPass<RadianceCachePass>();
			int probeRows = (atlas.UsedRows + SurfaceCacheAtlas.RadiosityBlock - 1) / SurfaceCacheAtlas.RadiosityBlock;

			// ---- radiosity probes: 16 rays per 4x4 texel block, accumulated per probe. While the scene is quiet
			// the probes are converged, so only a slice of the rows is refreshed per frame (round robin).
			profiler.Split("SurfaceCache.radiosity");
			int rowStart = 0;
			int rowCount = probeRows;
			// A change in this frame restores the full refresh at once (the cache's Idle flag lags one frame).
			bool idle = (radianceCache != null && radianceCache.Active && gi.RadianceCacheForRadiosity ? radianceCache.Idle : scene.IsQuiet(Math.Max(1, gi.RadianceCacheIdleAfterFrames))) && scene.IsQuiet(1);
			if (idle && gi.RadiosityIdleDivisor > 1 && probeRows > 0)
			{
				// Slice `frameIndex % divisor` of the rows: every row exactly once per divisor frames.
				int divisor = gi.RadiosityIdleDivisor;
				int slice = frameIndex % divisor;
				rowStart = slice * probeRows / divisor;
				rowCount = Math.Max(0, (slice + 1) * probeRows / divisor - rowStart);
			}

			ShaderProgram probes = radiosity.Program;
			radiosity.Use();
			scene.SetUniforms(probes);
			Lighting.SetSdfUniforms(probes);
			// Rays read last frame's radiance cache beyond the near distance.
			if (radianceCache != null && gi.RadianceCacheForRadiosity)
			{
				radianceCache.SetUniforms(probes, 5);
			}
			else
			{
				probes.SetFloat("rcNearDistance", 0f);
			}
			probes.SetTexture("CardIndex", TextureTarget.Texture2D, atlas.CardIndex, 0);
			probes.SetTexture("CardNormal", TextureTarget.Texture2D, atlas.Normal, 2);
			probes.SetTexture("FinalLightingPrev", TextureTarget.Texture2D, atlas.FinalLightingPrevious, 4);
			probes.SetVector3("giSkyRadiance", gi.SkyRadiance);
			probes.SetInt("frameIndex", frameIndex);
			probes.SetFloat("radiosityBlend", gi.RadiosityBlend);
			// While the scene is changing (or has just stopped) the probes track with a short history; once it has
			// been quiet for a while they accumulate to the long one.
			bool settled = scene.IsQuiet(Math.Max(1, gi.RadianceCacheIdleAfterFrames));
			probes.SetFloat("radiosityMaxSamples", settled ? gi.RadiosityMaxHistorySamples : Math.Min(gi.RadiosityMaxHistorySamples, gi.RadiosityMovingHistorySamples));
			probes.SetInt2("probeRegion", atlas.ProbeGridSize, probeRows);
			probes.SetInt("probeRowStart", rowStart);
			GL.BindImageTexture(0, atlas.RadiosityProbes, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba32f);
			if (rowCount > 0)
			{
				radiosity.Dispatch(atlas.ProbeGridSize, rowCount, 1);
				ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
			}

			// ---- per texel: direct sun + interpolated radiosity
			profiler.Split("SurfaceCache.lighting");
			ShaderProgram program = lighting.Program;
			lighting.Use();
			scene.SetUniforms(program);
			Lighting.SetUniforms(program, false, false);
			gridShadow.SetUniforms(program);
			program.SetTexture("CardIndex", TextureTarget.Texture2D, atlas.CardIndex, 0);
			program.SetTexture("CardAlbedo", TextureTarget.Texture2D, atlas.Albedo, 1);
			program.SetTexture("CardNormal", TextureTarget.Texture2D, atlas.Normal, 2);
			program.SetTexture("CardEmissive", TextureTarget.Texture2D, atlas.Emissive, 3);
			program.SetTexture("RadiosityProbes", TextureTarget.Texture2D, atlas.RadiosityProbes, 4);
			program.SetInt2("atlasRegion", atlas.Size, atlas.UsedRows);
			GL.BindImageTexture(0, atlas.FinalLightingCurrent, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			lighting.DispatchThreads(atlas.Size, atlas.UsedRows, 1, 8, 8, 1);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		#endregion
	}
}
