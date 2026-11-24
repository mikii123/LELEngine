using System;
using System.Collections.Generic;
using LELEngine.Graphics;
using LELEngine.Mathematics;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Reflections of glossy surfaces traced through the scene's triangles (<see cref="Lighting.RayTracedReflections" />:
	///     Lumen with its hardware ray tracing), after the screen probe gather: Engine/RayTracedReflectionTiles.shader flags
	///     the 8 x 8 tiles of the resolve that have a pixel below <see cref="RayTracingSettings.ReflectionMaxRoughness" />
	///     and lists them; Engine/RayTracedReflections.shader traces, over the listed tiles only (an indirect dispatch), one
	///     ray a pixel along the surface's GGX lobe, lights what it hits from the surface cache (as Lumen's rays do) and
	///     accumulates it over frames; Engine/RayTracedReflectionFilter.shader smooths it and blends it into the probes'
	///     rough reflection, written over the gather's specular (<see cref="GlobalIlluminationSettings.ResolvedSpecular" />)
	///     where the surface was traced, which the materials read. Tiles without a glossy pixel cost the classification's
	///     reads only. History: RGBA16F (the reflection, its frames) and one 32-bit word of the surface (Engine/HistoryMeta.glsl)
	///     a pixel, both kept for two frames, and a word a tile.
	/// </summary>
	public sealed class RayTracedReflectionPass : RenderPass
	{
		#region PublicFields

		public override string Name => "RayTracedReflections";

		/// <summary>
		///     The trace and its filter run over the tiles with a glossy pixel only (true), or over the whole resolve (false:
		///     to measure what the classification saves; TestGame rttiles=0).
		/// </summary>
		public static bool TileClassification = true;

		/// <summary>The side of a tile (pixels of the resolve), the work groups' size.</summary>
		public const int TileSize = 8;

		#endregion

		#region PrivateFields

		private static readonly string[] TileDefines = { "LEL_REFLECTION_TILES" };

		private ComputeShader classify;
		private ComputeShader trace;
		private ComputeShader filter;
		private ComputeShader tiledTrace;
		private ComputeShader tiledFilter;
		private readonly GpuTexture[] history = new GpuTexture[2];
		private readonly GpuTexture[] meta = new GpuTexture[2];
		private readonly GpuTexture[] tileFlags = new GpuTexture[2];
		private GpuBuffer tileList;
		private int current;
		private int width;
		private int height;
		private bool historyValid;
		private bool previousTiled;
		private int frameIndex;
		private Matrix4 previousViewProjection;
		private Vector3 previousCameraPosition;
		private Vector3 previousCameraForward;
		private static readonly uint[] EmptyListHeader = { 0u, 1u, 1u, 0u };

		#endregion

		#region PublicMethods

		public override void Setup(FrameGraph frame, RenderContext context)
		{
			// The reflections' history is reprojected with the prepass's motion vectors.
			if (Lighting.RayTracedReflections)
			{
				frame.Read(GeometryPrepass.MotionTexture);
			}
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			LumenScene scene = context.Renderer.LumenScene;
			ScreenProbeGatherPass gather = context.Renderer.GetPass<ScreenProbeGatherPass>();
			bool active = Lighting.RayTracedReflections && gather != null && gather.ProbesReady && gi.ResolveActive && gi.ResolvedSpecular != null
				&& scene.SurfaceCache != null && scene.RayTracing?.TopLevel != null && context.Motion != null && context.NormalRoughness != null;
			if (!active)
			{
				historyValid = false;
				if (!Lighting.RayTracedReflections)
				{
					Release();
				}

				return;
			}

			string[] defines = Lighting.RayTracingDefines;
			trace ??= new ComputeShader("Engine/RayTracedReflections.shader", defines);
			filter ??= new ComputeShader("Engine/RayTracedReflectionFilter.shader");
			bool tiled = TileClassification;
			if (tiled)
			{
				classify ??= new ComputeShader("Engine/RayTracedReflectionTiles.shader");
				tiledTrace ??= new ComputeShader("Engine/RayTracedReflections.shader", Concat(defines, TileDefines));
				tiledFilter ??= new ComputeShader("Engine/RayTracedReflectionFilter.shader", TileDefines);
			}

			EnsureTargets(gi.ResolveWidth, gi.ResolveHeight);
			frameIndex++;
			int previous = 1 - current;
			CommandList commands = Gfx.Commands;
			float maxRoughness = Math.Clamp(Lighting.RayTracing.ReflectionMaxRoughness, 0.05f, 1f);

			// ---- the tiles with a glossy pixel: flagged, and listed for the indirect dispatches
			if (tiled)
			{
				context.Renderer.Profiler.Split("RayTracedReflections.tiles");
				tileList.SetSubData(0, EmptyListHeader);
				ShaderProgram tiles = classify.Program;
				classify.Use();
				tiles.SetTexture("NormalRoughness", context.NormalRoughness);
				tiles.SetTexture("SceneDepth", context.SceneDepth);
				tiles.SetInt2("screenSize", context.Width, context.Height);
				tiles.SetInt2("outputSize", width, height);
				tiles.SetFloat("maxRoughness", maxRoughness);
				commands.SetStorageBuffer(0, tileList);
				commands.SetImage(0, tileFlags[current], 0, 0, ImageAccess.Write);
				classify.Dispatch(TilesX, TilesY);
			}

			context.Renderer.Profiler.Split("RayTracedReflections.trace");
			ComputeShader traceShader = tiled ? tiledTrace : trace;
			ShaderProgram program = traceShader.Program;
			traceShader.Use();
			scene.SetUniforms(program);
			Lighting.SetSdfUniforms(program);
			Lighting.SetSkyUniforms(program);
			program.SetTexture("SceneDepth", context.SceneDepth);
			program.SetTexture("NormalRoughness", context.NormalRoughness);
			program.SetTexture("Motion", context.Motion);
			program.SetTexture("ProbeSpecular", gi.ResolvedSpecular);
			program.SetTexture("FinalLighting", scene.SurfaceCache.FinalLightingCurrent);
			program.SetTexture("HistoryReflection", history[previous]);
			program.SetTexture("HistoryMeta", meta[previous]);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetMatrix4("previousViewProjection", previousViewProjection);
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("cameraForward", context.CameraForward);
			program.SetVector3("previousCameraPosition", previousCameraPosition);
			program.SetVector3("previousCameraForward", previousCameraForward);
			program.SetInt2("screenSize", context.Width, context.Height);
			program.SetInt2("outputSize", width, height);
			program.SetFloat("maxRoughness", maxRoughness);
			program.SetInt("frameIndex", frameIndex);
			// A history from a frame that traced every texel (or one that did not flag its tiles) is not taken.
			program.SetInt("historyValid", historyValid && previousTiled == tiled ? 1 : 0);
			commands.SetImage(1, history[current], 0, 0, ImageAccess.Write);
			commands.SetImage(2, meta[current], 0, 0, ImageAccess.Write);
			if (tiled)
			{
				program.SetTexture("PreviousTileFlags", tileFlags[previous]);
				commands.SetStorageBuffer(0, tileList);
				traceShader.DispatchIndirect(tileList, 0);
			}
			else
			{
				traceShader.DispatchThreads(width, height, 1, TileSize, TileSize, 1);
			}

			// Smoothed over the surface's pixels and blended into the probes' reflection, written over it where traced: the
			// materials read it there (the gather's next frame takes it as its history: on the traced surfaces it is
			// written over again).
			context.Renderer.Profiler.Split("RayTracedReflections.filter");
			ComputeShader filterShader = tiled ? tiledFilter : filter;
			ShaderProgram filterProgram = filterShader.Program;
			filterShader.Use();
			filterProgram.SetTexture("TracedReflection", history[current]);
			filterProgram.SetTexture("Meta", meta[current]);
			filterProgram.SetTexture("NormalRoughness", context.NormalRoughness);
			filterProgram.SetInt2("screenSize", context.Width, context.Height);
			filterProgram.SetInt2("outputSize", width, height);
			filterProgram.SetFloat("maxRoughness", maxRoughness);
			commands.SetImage(0, gi.ResolvedSpecular, 0, 0, ImageAccess.ReadWrite);
			if (tiled)
			{
				filterProgram.SetTexture("TileFlags", tileFlags[current]);
				commands.SetStorageBuffer(0, tileList);
				filterShader.DispatchIndirect(tileList, 0);
			}
			else
			{
				filterShader.DispatchThreads(width, height, 1, TileSize, TileSize, 1);
			}

			previousViewProjection = context.ViewProjection;
			previousCameraPosition = context.CameraPosition;
			previousCameraForward = context.CameraForward;
			historyValid = true;
			previousTiled = tiled;
			current = previous;
		}

		public override void Resize(int width, int height)
		{
			historyValid = false;
		}

		public override void ReleaseUnusedResources()
		{
			if (!Lighting.RayTracedReflections)
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

		private int TilesX => (width + TileSize - 1) / TileSize;

		private int TilesY => (height + TileSize - 1) / TileSize;

		private static string[] Concat(string[] a, string[] b)
		{
			var all = new List<string>(a);
			all.AddRange(b);
			return all.ToArray();
		}

		private void EnsureTargets(int newWidth, int newHeight)
		{
			if (newWidth == width && newHeight == height && history[0] != null)
			{
				return;
			}

			DeleteTargets();
			width = newWidth;
			height = newHeight;
			historyValid = false;
			const TextureUsage written = TextureUsage.Sampled | TextureUsage.Storage;
			for (int i = 0; i < 2; i++)
			{
				history[i] = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.RGBA16F, width, height, 1, written, "Traced reflections history " + i));
				history[i].Sampler = Samplers.PointClamp;
				meta[i] = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.R32UI, width, height, 1, written, "Traced reflections meta " + i));
				meta[i].Sampler = Samplers.PointClamp;
				// 32-bit: R32UI is a storage format every Vulkan device has (8-bit ones need an optional feature).
				tileFlags[i] = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.R32UI, TilesX, TilesY, 1, written, "Traced reflections tiles " + i));
				tileFlags[i].Sampler = Samplers.PointClamp;
			}

			// The indirect dispatch's arguments, then a word a tile.
			tileList = Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage | BufferUsage.Indirect, 0, true, "Traced reflections tiles"));
			tileList.Allocate(16 + sizeof(uint) * TilesX * TilesY);
			tileList.SetSubData(0, EmptyListHeader);
			Console.WriteLine($"[RayTracing] Traced reflections {width}x{height} (~{(long)width * height * (8 + 4) * 2 / 1048576.0:0} MB of history)");
		}

		private void DeleteTargets()
		{
			for (int i = 0; i < 2; i++)
			{
				history[i]?.Dispose();
				history[i] = null;
				meta[i]?.Dispose();
				meta[i] = null;
				tileFlags[i]?.Dispose();
				tileFlags[i] = null;
			}

			tileList?.Dispose();
			tileList = null;
			width = 0;
			height = 0;
		}

		private void Release()
		{
			if (trace == null && history[0] == null)
			{
				return;
			}

			trace?.Delete();
			trace = null;
			filter?.Delete();
			filter = null;
			classify?.Delete();
			classify = null;
			tiledTrace?.Delete();
			tiledTrace = null;
			tiledFilter?.Delete();
			tiledFilter = null;
			DeleteTargets();
			historyValid = false;
		}

		#endregion
	}
}
