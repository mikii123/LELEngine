using System;
using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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

		public override RenderPassScope Scope => RenderPassScope.Frame;

		#endregion

		#region PrivateFields

		private ShaderProgram capture;
		private ComputeShader radiosity;
		private ComputeShader lighting;

		// The radiosity and lighting programs are the ray query variants (Lighting.HardwareRayTracingActive when they were made).
		private bool traceRayQueries;
		private GridShadowMap gridShadow;
		private int frameIndex;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			// Made when Lumen first runs, freed when it does not (GI off; the scene's surface cache goes with LumenScene).
			if (!gi.LumenActive)
			{
				Release();
				return;
			}

			if (capture != null && traceRayQueries != Lighting.HardwareRayTracingActive)
			{
				// The rays' way changed (the editor, in edit mode): the tracing programs of the other way.
				radiosity.Delete();
				lighting.Delete();
				traceRayQueries = Lighting.HardwareRayTracingActive;
				radiosity = new ComputeShader("Engine/CardRadiosityProbes.shader", Lighting.RayTracingDefines);
				lighting = new ComputeShader("Engine/CardLighting.shader", Lighting.RayTracingDefines);
			}

			if (capture == null)
			{
				traceRayQueries = Lighting.HardwareRayTracingActive;
				capture = new ShaderProgram("Engine/CardCapture.shader");
				radiosity = new ComputeShader("Engine/CardRadiosityProbes.shader", Lighting.RayTracingDefines);
				lighting = new ComputeShader("Engine/CardLighting.shader", Lighting.RayTracingDefines);
				gridShadow = new GridShadowMap(gi.GridShadowMapSize);
			}

			LumenScene scene = context.Renderer.LumenScene;
			frameIndex++;
			GpuProfiler profiler = context.Renderer.Profiler;
			// The scene's tables and acceleration structures (uploads and builds) beside the graphics work around them, on
			// the queue that reads them first (the cards' light below).
			context.BeginAsyncCompute();
			if (Lighting.HardwareRayTracingActive && !gi.DistanceFieldActive)
			{
				// No distance field pass placed the GI volume and updated the scene this frame (Lumen's rays trace the
				// triangles): here, with the scene's acceleration structures.
				gi.UpdateGridPlacement(context.CameraPosition, context.CameraForward);
				profiler.Split("RayTracing.scene");
			}

			scene.Update(context.Renderer.FrameIndex, context.GIRenderers ?? context.Renderers, gi);
			context.EndAsyncCompute();
			gridShadow.EnsureSize(gi.GridShadowMapSize);

			profiler.Split("SurfaceCache.capture");
			CaptureCards(scene);

			// With the directional lights' shadows traced on the cards the grid maps hold only the transparent casters.
			profiler.Split("SurfaceCache.shadow");
			gridShadow.Render(context, context.GIRenderers ?? context.Renderers, gi, !TracesDirectionalShadows);

			// The radiosity reads what is ready by now (the cards, last frame's light): beside the graphics work after it.
			context.BeginAsyncCompute();
			LightCards(context.Renderer, scene, gi);
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

		// The cards' directional shadows come from rays this frame: the lighting programs are the ray query variants and
		// the project asks for it (Lighting.SurfaceCacheTracedShadows).
		private bool TracesDirectionalShadows => traceRayQueries && Lighting.SurfaceCacheTracedShadows;

		private void Release()
		{
			capture?.Delete();
			radiosity?.Delete();
			lighting?.Delete();
			gridShadow?.Delete();
			capture = null;
			radiosity = null;
			lighting = null;
			gridShadow = null;
		}

		private void CaptureCards(LumenScene scene)
		{
			SurfaceCacheAtlas atlas = scene.SurfaceCache;
			CommandList commands = Gfx.Commands;
			bool bound = false;

			foreach (LumenScene.SceneObject o in scene.Objects)
			{
				if (o.Cards == null)
				{
					continue;
				}

				// Captures are transform independent (scaled-local space) and emission intensity is applied at
				// lighting time, so a card set is recaptured only when the material inputs it stores change.
				long key = CaptureKey(o.Renderer);
				if (o.Cards.Captured && o.Cards.CaptureKey == key)
				{
					continue;
				}
				o.Cards.CaptureKey = key;

				if (!bound)
				{
					atlas.BeginCapture();
					commands.State.SetDepth(true, true, CompareFunction.Less);
					commands.State.Culling = FaceCulling.None;
					commands.State.ColorMask = ColorWriteMask.All;
					capture.Use();
					bound = true;
				}

				capture.SetVector3("objectScale", o.Cards.Scale);
				MeshRenderer renderer = o.Renderer;
				for (int i = 0; i < o.Cards.Cards.Length; i++)
				{
					SurfaceCard card = o.Cards.Cards[i];
					atlas.ClearRect(card.X, card.Y, card.Width, card.Height);
					commands.SetViewport(card.X, card.Y, card.Width, card.Height);
					capture.SetMatrix4("cardViewProjection", card.ViewProjection);
					capture.SetInt("cardIndex", o.Cards.FirstCardIndex + i);

					// Every opaque sub-mesh with its own material inputs.
					for (int s = 0; s < renderer.SubMeshCount; s++)
					{
						Material material = renderer.GetMaterial(s);
						if (material == null || material.IsTransparent)
						{
							continue;
						}

						// The same conventional material inputs the voxelizer reads. The mesh as it is, in the object's
						// unmirrored scaled-local space (a mirroring renderer's mirrored twin would show only back faces
						// here, and the cards' normals would point inwards).
						GISurface.SetUniforms(capture, material);
						GeometryPool.DrawSubMesh(renderer.Mesh, s);
					}
				}

				o.Cards.Captured = true;
			}

			if (bound)
			{
				commands.EndRenderPass();
				commands.State.Culling = FaceCulling.Back;
			}
		}

		// Hash of the material inputs of every opaque sub-mesh, baked into the cards.
		private static long CaptureKey(MeshRenderer renderer)
		{
			unchecked
			{
				long hash = 17;
				for (int s = 0; s < renderer.SubMeshCount; s++)
				{
					Material material = renderer.GetMaterial(s);
					if (material != null && !material.IsTransparent)
					{
						hash = hash * 31 + GISurface.CaptureKey(material);
					}
				}

				return hash;
			}
		}

		private void LightCards(Renderer renderer, LumenScene scene, GlobalIlluminationSettings gi)
		{
			SurfaceCacheAtlas atlas = scene.SurfaceCache;
			CommandList commands = Gfx.Commands;
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
				radianceCache.SetUniforms(probes);
			}
			else
			{
				probes.SetFloat("rcNearDistance", 0f);
			}
			probes.SetTexture("CardIndex", atlas.CardIndex);
			probes.SetTexture("CardNormal", atlas.Normal);
			probes.SetTexture("FinalLightingPrev", atlas.FinalLightingPrevious);
			Lighting.SetSkyUniforms(probes);
			// The clouds on the rays' way (the volumetric objects in the GI): surfaces they light pass the light on.
			Lighting.SetMediaUniforms(probes);
			probes.SetInt("frameIndex", frameIndex);
			probes.SetFloat("radiosityBlend", gi.RadiosityBlend);
			// While the scene is changing (or has just stopped) the probes track with a short history; once it has
			// been quiet for a while they accumulate to the long one.
			bool settled = scene.IsQuiet(Math.Max(1, gi.RadianceCacheIdleAfterFrames));
			probes.SetFloat("radiosityMaxSamples", settled ? gi.RadiosityMaxHistorySamples : Math.Min(gi.RadiosityMaxHistorySamples, gi.RadiosityMovingHistorySamples));
			// The screen probes' change test and moving-object history, per radiosity probe.
			probes.SetFloat("movingSamples", Math.Max(1f, gi.ProbeMovingHistorySamples));
			probes.SetFloat("movingFrames", gi.ProbeMovingFrames);
			probes.SetFloat("changeSigma", gi.ProbeChangeSigma);
			probes.SetFloat("changeFloor", gi.ProbeChangeFloor);
			probes.SetInt2("probeRegion", atlas.ProbeGridSize, probeRows);
			probes.SetInt("probeRowStart", rowStart);
			commands.SetImage(0, atlas.RadiosityProbes, 0, 0, ImageAccess.ReadWrite);
			commands.SetImage(1, atlas.RadiosityMeta, 0, 0, ImageAccess.ReadWrite);
			if (rowCount > 0)
			{
				radiosity.Dispatch(atlas.ProbeGridSize, rowCount, 1);
			}

			// ---- per texel: direct sun + interpolated radiosity (the grid's shadow map is a depth texture: the graphics queue's)
			renderer.EndAsyncPart();
			profiler.Split("SurfaceCache.lighting");
			ShaderProgram program = lighting.Program;
			lighting.Use();
			scene.SetUniforms(program);
			Lighting.SetUniforms(program, false, false);
			// The local lights' shadow rays (GI shadows) trace the global distance field (or the scene's triangles).
			Lighting.SetSdfUniforms(program);
			gridShadow.SetUniforms(program);
			program.SetInt("cardTracedDirectional", TracesDirectionalShadows ? DirectionalLightBuffer.ShadowedMask : 0);
			program.SetTexture("CardIndex", atlas.CardIndex);
			program.SetTexture("CardAlbedo", atlas.Albedo);
			program.SetTexture("CardNormal", atlas.Normal);
			program.SetTexture("CardEmissive", atlas.Emissive);
			program.SetTexture("RadiosityProbes", atlas.RadiosityProbes);
			program.SetInt2("atlasRegion", atlas.Size, atlas.UsedRows);
			commands.SetImage(0, atlas.FinalLightingCurrent, 0, 0, ImageAccess.Write);
			lighting.DispatchThreads(atlas.Size, atlas.UsedRows, 1, 8, 8, 1);
		}

		#endregion
	}
}
