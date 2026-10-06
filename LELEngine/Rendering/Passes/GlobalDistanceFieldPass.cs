using System;
using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Builds the global signed distance field (Lumen's "global SDF"): a world-space R16F volume over the
	///     GI grid holding the distance to the nearest surface, clamped to a band of a few voxels.
	///
	///     Objects come from <see cref="LumenScene" /> (mesh distance fields built once per mesh and scale).
	///     Static objects are composed into a cached field when they change; dynamic objects are re-composed
	///     every frame, but only inside their band-padded bounds (previous and current), which is exact
	///     because the band limits how far an object can influence the field.
	///
	///     Consumers: Engine/DistanceField.glsl (sphere tracing) through Lighting.SetSdfUniforms.
	/// </summary>
	public sealed class GlobalDistanceFieldPass : RenderPass
	{
		#region PublicFields

		public override string Name => "GlobalSDF";
		public int GlobalSdf => globalSdf;

		#endregion

		#region PrivateFields

		private ComputeShader compose;
		private int staticSdf;
		private int globalSdf;
		private int currentResolution;

		private List<VoxelRegion> previousRegions = new List<VoxelRegion>();
		private List<VoxelRegion> currentRegions = new List<VoxelRegion>();
		private readonly List<VoxelRegion> updateRegions = new List<VoxelRegion>();

		private const float FullUpdateCoverage = 0.5f;

		private Vector3 lastGridMin;
		private float lastGridSize;
		private int lastStaticCount = -1;
		private int frameIndex;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			compose = new ComputeShader("Engine/SdfCompose.shader");
			CreateVolumes(Lighting.GI.SdfResolution);
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			if (!gi.Enabled || !gi.DistanceFieldEnabled)
			{
				gi.GlobalSdf = 0;
				return;
			}

			if (gi.SdfResolution != currentResolution)
			{
				CreateVolumes(gi.SdfResolution);
			}

			gi.UpdateGridPlacement(context.CameraPosition);

			LumenScene scene = context.Renderer.LumenScene;
			frameIndex++;
			scene.Update(frameIndex, context.Renderers, gi);
			DetectStaticChanges(gi, scene);

			bool dynamicDue = gi.DynamicUpdateInterval <= 1 || frameIndex % gi.DynamicUpdateInterval == 0;
			GpuProfiler profiler = context.Renderer.Profiler;

			if (gi.SdfStaticDirty)
			{
				profiler.Split("GlobalSDF.static");
				RebuildStaticField(gi, scene);
				// The whole working field was replaced: dynamic objects must be stamped again this frame.
				dynamicDue = true;
			}

			if (dynamicDue)
			{
				profiler.Split("GlobalSDF.dynamic");
				UpdateDynamicField(gi, scene);
			}

			gi.GlobalSdf = globalSdf;
		}

		public override void Dispose()
		{
			DeleteVolumes();
			compose?.Delete();
			compose = null;
			Lighting.GI.GlobalSdf = 0;
		}

		#endregion

		#region PrivateMethods

		private void CreateVolumes(int resolution)
		{
			resolution = Math.Max(8, resolution / 4 * 4);
			DeleteVolumes();

			staticSdf = CreateSdfTexture(resolution);
			globalSdf = CreateSdfTexture(resolution);
			currentResolution = resolution;
			Lighting.GI.SdfResolution = resolution;
			Lighting.GI.SdfStaticDirty = true;
			previousRegions.Clear();

			Console.WriteLine($"[SDF] Global distance field {resolution}^3 R16F (~{resolution * (long)resolution * resolution * 2 * 2 / (1024.0 * 1024.0):0} MB)");
		}

		private void DeleteVolumes()
		{
			if (staticSdf != 0)
			{
				GL.DeleteTexture(staticSdf);
				staticSdf = 0;
			}
			if (globalSdf != 0)
			{
				GL.DeleteTexture(globalSdf);
				globalSdf = 0;
			}
		}

		private static int CreateSdfTexture(int resolution)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16f, resolution, resolution, resolution, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		private void DetectStaticChanges(GlobalIlluminationSettings gi, LumenScene scene)
		{
			bool changed =
				(gi.GridMin - lastGridMin).LengthSquared > 1e-6f
				|| Math.Abs(gi.GridSize - lastGridSize) > 1e-5f
				|| scene.StaticObjectCount != lastStaticCount;

			if (changed)
			{
				gi.SdfStaticDirty = true;
				lastGridMin = gi.GridMin;
				lastGridSize = gi.GridSize;
				lastStaticCount = scene.StaticObjectCount;
			}
		}

		private void RebuildStaticField(GlobalIlluminationSettings gi, LumenScene scene)
		{
			Compose(staticSdf, 0, 0, scene.StaticObjectCount, gi, scene, VoxelRegion.Full(currentResolution));

			GL.CopyImageSubData(
				staticSdf, ImageTarget.Texture3D, 0, 0, 0, 0,
				globalSdf, ImageTarget.Texture3D, 0, 0, 0, 0,
				currentResolution, currentResolution, currentResolution);

			previousRegions.Clear();
			gi.SdfStaticDirty = false;
		}

		private void UpdateDynamicField(GlobalIlluminationSettings gi, LumenScene scene)
		{
			// An object influences the field up to one band beyond its bounds.
			int padding = gi.SdfBandVoxels + 1;
			currentRegions.Clear();
			for (int i = scene.StaticObjectCount; i < scene.Objects.Count; i++)
			{
				VoxelRegion region;
				if (VoxelRegion.TryFromRenderer(scene.Objects[i].Renderer, gi.GridMin, gi.SdfVoxelSize, currentResolution, padding, out region))
				{
					currentRegions.Add(region);
				}
			}

			// Where objects were and where they are: static distance, min'ed with the current dynamic objects.
			updateRegions.Clear();
			updateRegions.AddRange(previousRegions);
			updateRegions.AddRange(currentRegions);

			int dynamicCount = scene.Objects.Count - scene.StaticObjectCount;
			if (updateRegions.Count > 0)
			{
				if (RegionCoverage(updateRegions) > FullUpdateCoverage)
				{
					Compose(globalSdf, staticSdf, scene.StaticObjectCount, dynamicCount, gi, scene, VoxelRegion.Full(currentResolution));
				}
				else
				{
					Compose(globalSdf, staticSdf, scene.StaticObjectCount, dynamicCount, gi, scene, updateRegions);
				}
			}

			List<VoxelRegion> swap = previousRegions;
			previousRegions = currentRegions;
			currentRegions = swap;
		}

		private float RegionCoverage(List<VoxelRegion> regions)
		{
			double total = 0;
			foreach (VoxelRegion region in regions)
			{
				total += region.Volume;
			}

			return (float)(total / ((double)currentResolution * currentResolution * currentResolution));
		}

		private void Compose(int target, int baseSdf, int objectStart, int objectCount, GlobalIlluminationSettings gi, LumenScene scene, VoxelRegion region)
		{
			updateRegions.Clear();
			updateRegions.Add(region);
			Compose(target, baseSdf, objectStart, objectCount, gi, scene, updateRegions);
		}

		private void Compose(int target, int baseSdf, int objectStart, int objectCount, GlobalIlluminationSettings gi, LumenScene scene, List<VoxelRegion> regions)
		{
			compose.Use();
			scene.SetUniforms(compose.Program);
			compose.Program.SetInt("objectStart", objectStart);
			compose.Program.SetInt("objectCount", objectCount);
			compose.Program.SetInt("useBase", baseSdf != 0 ? 1 : 0);
			compose.Program.SetVector3("gridMin", gi.GridMin);
			compose.Program.SetFloat("gridSize", gi.GridSize);
			compose.Program.SetInt("sdfResolution", currentResolution);
			compose.Program.SetFloat("maxDistance", gi.SdfBandVoxels * gi.SdfVoxelSize);

			GL.BindImageTexture(0, baseSdf != 0 ? baseSdf : target, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R16f);
			GL.BindImageTexture(1, target, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);

			foreach (VoxelRegion region in regions)
			{
				if (region.IsEmpty)
				{
					continue;
				}

				compose.Program.SetInt3("dstOffset", region.MinX, region.MinY, region.MinZ);
				compose.Program.SetInt3("dstSize", region.SizeX, region.SizeY, region.SizeZ);
				compose.DispatchThreads(region.SizeX, region.SizeY, region.SizeZ, 4, 4, 4);
			}

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		#endregion
	}
}
