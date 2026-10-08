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
		private int staticIds;
		private int globalIds;
		private int currentResolution;

		private List<VoxelRegion> previousRegions = new List<VoxelRegion>();
		private List<VoxelRegion> currentRegions = new List<VoxelRegion>();
		private readonly List<VoxelRegion> updateRegions = new List<VoxelRegion>();

		private const float FullUpdateCoverage = 0.5f;

		private Vector3 lastGridMin;
		private float lastGridSize;
		private int lastStaticCount = -1;
		private int frameIndex;
		private bool dynamicPending = true;

		// Static field state of this pass (each renderer has its own), see GlobalIlluminationSettings.StaticVersion.
		private bool staticDirty = true;
		private int builtStaticVersion = -1;

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
				gi.GlobalSdfObjectIds = 0;
				return;
			}

			if (gi.SdfResolution != currentResolution)
			{
				CreateVolumes(gi.SdfResolution);
			}

			gi.UpdateGridPlacement(context.CameraPosition);

			LumenScene scene = context.Renderer.LumenScene;
			frameIndex++;
			scene.Update(context.Renderer.FrameIndex, context.Renderers, gi);
			DetectStaticChanges(gi, scene);

			// The dynamic composite is a pure function of object placement: skip it while nothing moved. A move is
			// latched until a due frame, so an update interval > 1 cannot drop a change that happened in between.
			dynamicPending |= scene.DynamicChanged;
			bool dynamicDue = dynamicPending && (gi.DynamicUpdateInterval <= 1 || frameIndex % gi.DynamicUpdateInterval == 0);
			GpuProfiler profiler = context.Renderer.Profiler;

			if (staticDirty || gi.StaticVersion != builtStaticVersion)
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
				dynamicPending = false;
			}

			gi.GlobalSdf = globalSdf;
			gi.GlobalSdfObjectIds = globalIds;
		}

		public override void Dispose()
		{
			DeleteVolumes();
			compose?.Delete();
			compose = null;
			Lighting.GI.GlobalSdf = 0;
			Lighting.GI.GlobalSdfObjectIds = 0;
		}

		#endregion

		#region PrivateMethods

		private void CreateVolumes(int resolution)
		{
			resolution = Math.Max(8, resolution / 4 * 4);
			DeleteVolumes();

			staticSdf = CreateSdfTexture(resolution);
			globalSdf = CreateSdfTexture(resolution);
			staticIds = CreateIdTexture(resolution);
			globalIds = CreateIdTexture(resolution);
			currentResolution = resolution;
			Lighting.GI.SdfResolution = resolution;
			staticDirty = true;
			previousRegions.Clear();

			Console.WriteLine($"[SDF] Global distance field {resolution}^3 R16F + object ids R16UI (~{resolution * (long)resolution * resolution * 2 * 4 / (1024.0 * 1024.0):0} MB)");
		}

		private void DeleteVolumes()
		{
			foreach (int texture in new[] { staticSdf, globalSdf, staticIds, globalIds })
			{
				if (texture != 0)
				{
					GL.DeleteTexture(texture);
				}
			}
			staticSdf = 0;
			globalSdf = 0;
			staticIds = 0;
			globalIds = 0;
		}

		// Object index + 1 of the nearest surface per voxel (0 = nothing within the band); fetched by texel.
		private static int CreateIdTexture(int resolution)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16ui, resolution, resolution, resolution, 0, PixelFormat.RedInteger, PixelType.UnsignedShort, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
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
				staticDirty = true;
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
			GL.CopyImageSubData(
				staticIds, ImageTarget.Texture3D, 0, 0, 0, 0,
				globalIds, ImageTarget.Texture3D, 0, 0, 0, 0,
				currentResolution, currentResolution, currentResolution);

			previousRegions.Clear();
			staticDirty = false;
			builtStaticVersion = gi.StaticVersion;
			// A rebuilt static field changes the lighting as much as a dynamic move: caches leave their idle budgets.
			scene.MarkChanged();
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

			bool fromStatic = baseSdf != 0;
			int targetIds = target == staticSdf ? staticIds : globalIds;
			GL.BindImageTexture(0, fromStatic ? baseSdf : target, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R16f);
			GL.BindImageTexture(1, target, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);
			GL.BindImageTexture(2, fromStatic ? staticIds : targetIds, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R16ui);
			GL.BindImageTexture(3, targetIds, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16ui);

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
