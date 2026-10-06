using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Voxel cone tracing, stage 1: builds the radiance volume.
	///     The scene is rasterized into a 3D texture (dominant-axis projection in a geometry shader,
	///     imageStore in the fragment shader) storing directly lit diffuse radiance plus emission, then mipmapped.
	///     Material shaders sample it through Engine/VoxelConeTracing.glsl (uniforms via Lighting.SetGIUniforms).
	///
	///     Per-frame cost is proportional to the dynamic content, not the volume size:
	///     - Renderers marked <see cref="MeshRenderer.IsStatic" /> are voxelized once into a fully mipmapped cache.
	///     - Each frame only the voxel regions touched by dynamic renderers (last frame and this frame) are
	///       restored from the cache, re-voxelized and re-filtered through the mip chain (compute shaders).
	///       When those regions cover most of the volume, one whole-volume update is done instead.
	///     - Direct light inside the volume uses a dedicated shadow map covering the whole grid, so the cache
	///       does not depend on the camera-following scene shadow map.
	///     - Dynamic updates can run every N frames (<see cref="GlobalIlluminationSettings.DynamicUpdateInterval" />).
	/// </summary>
	public sealed class VoxelGIPass : RenderPass
	{
		#region PublicFields

		public override string Name => "VoxelGI";
		public int VoxelTexture => volume;

		/// <summary>True on frames where the static cache was (re)built. Useful for stats.</summary>
		public bool RebuiltStaticThisFrame { get; private set; }

		#endregion

		#region PrivateFields

		private int volume;
		private int staticVolume;
		private int currentResolution;
		private int mipLevels;
		private int framebuffer;
		private ShaderProgram voxelize;
		private ComputeShader clear;
		private ComputeShader mip;
		private ComputeShader restore;
		private Framebuffer gridShadow;

		/// <summary>Above this fraction of the volume touched by dynamic regions, a full update is cheaper.</summary>
		private const float FullUpdateCoverage = 0.5f;

		/// <summary>Mip levels this small are refiltered whole instead of per region (fewer dispatches).</summary>
		private const int SmallLevelSize = 16;

		private readonly List<MeshRenderer> staticRenderers = new List<MeshRenderer>();
		private readonly List<MeshRenderer> dynamicRenderers = new List<MeshRenderer>();

		// Voxel regions written by dynamic renderers; previous ones must be restored from the cache.
		private List<VoxelRegion> previousRegions = new List<VoxelRegion>();
		private List<VoxelRegion> currentRegions = new List<VoxelRegion>();
		private readonly List<VoxelRegion> mipRegions = new List<VoxelRegion>();
		private readonly List<VoxelRegion> fullRegionList = new List<VoxelRegion>();
		private bool fullSyncNeeded = true;

		// Static cache invalidation tracking
		private Vector3 lastLightDirection;
		private Color4 lastLightColor;
		private float lastLightStrength;
		private Vector3 lastGridMin;
		private float lastGridSize;
		private bool lastShadowsEnabled;
		private int lastStaticCount = -1;
		private int frameIndex;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			voxelize = new ShaderProgram("Engine/Voxelize.shader");
			clear = new ComputeShader("Engine/VoxelClear.shader");
			mip = new ComputeShader("Engine/VoxelMip.shader");
			restore = new ComputeShader("Engine/VoxelRestore.shader");

			// Attachment-less framebuffer: rasterization happens, but all output goes through imageStore.
			framebuffer = GL.GenFramebuffer();

			CreateVolumes(Lighting.GI.Resolution);
			CreateGridShadow(Lighting.GI.GridShadowMapSize);
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			RebuiltStaticThisFrame = false;

			if (!gi.Enabled)
			{
				gi.VoxelTexture = 0;
				return;
			}

			if (gi.Resolution != currentResolution)
			{
				CreateVolumes(gi.Resolution);
			}
			if (gridShadow.Width != gi.GridShadowMapSize)
			{
				gridShadow.Delete();
				CreateGridShadow(gi.GridShadowMapSize);
			}

			UpdateGridPlacement(context, gi);
			PartitionRenderers(context.Renderers);
			DetectStaticChanges(gi);

			frameIndex++;
			bool dynamicDue = gi.DynamicUpdateInterval <= 1 || frameIndex % gi.DynamicUpdateInterval == 0;
			if (!gi.StaticDirty && !dynamicDue)
			{
				gi.VoxelTexture = volume;
				return;
			}

			Matrix4 lightView;
			Matrix4 lightProjection;
			ComputeGridLightMatrices(gi, out lightView, out lightProjection);
			GpuProfiler profiler = context.Renderer.Profiler;

			if (gi.StaticDirty)
			{
				profiler.Split("VoxelGI.static");
				RebuildStaticCache(context, gi, lightView, lightProjection);
			}

			currentRegions.Clear();
			foreach (MeshRenderer renderer in dynamicRenderers)
			{
				VoxelRegion region;
				if (TryGetVoxelRegion(renderer, gi, out region))
				{
					currentRegions.Add(region);
				}
			}

			// Many or large dynamic objects: one whole-volume update beats dozens of overlapping region updates.
			bool fullUpdate = fullSyncNeeded || RegionCoverage(previousRegions) + RegionCoverage(currentRegions) > FullUpdateCoverage;

			profiler.Split("VoxelGI.restore");
			if (fullUpdate)
			{
				RestoreFull();
			}
			else
			{
				RestoreRegions(previousRegions);
			}

			if (dynamicRenderers.Count > 0)
			{
				profiler.Split("VoxelGI.dynamic");
				RenderGridShadow(context, context.Renderers, lightView, lightProjection);
				Voxelize(volume, dynamicRenderers, gi, lightView, lightProjection);
			}

			profiler.Split("VoxelGI.mips");
			if (fullUpdate)
			{
				if (dynamicRenderers.Count > 0)
				{
					UpdateMips(volume, fullRegionList);
				}
			}
			else
			{
				mipRegions.Clear();
				mipRegions.AddRange(previousRegions);
				mipRegions.AddRange(currentRegions);
				UpdateMips(volume, mipRegions);
			}
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			// Regions written this frame must be restored next frame.
			List<VoxelRegion> swap = previousRegions;
			previousRegions = currentRegions;
			currentRegions = swap;

			gi.VoxelTexture = volume;
		}

		public override void Dispose()
		{
			if (volume != 0)
			{
				GL.DeleteTexture(volume);
				volume = 0;
			}
			if (staticVolume != 0)
			{
				GL.DeleteTexture(staticVolume);
				staticVolume = 0;
			}
			if (framebuffer != 0)
			{
				GL.DeleteFramebuffer(framebuffer);
				framebuffer = 0;
			}
			gridShadow?.Delete();
			gridShadow = null;
			voxelize?.Delete();
			clear?.Delete();
			mip?.Delete();
			restore?.Delete();
			Lighting.GI.VoxelTexture = 0;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- setup

		private void CreateVolumes(int resolution)
		{
			resolution = Math.Max(8, resolution);
			// Must be a multiple of the clear shader's work group (8).
			resolution = resolution / 8 * 8;

			if (volume != 0)
			{
				GL.DeleteTexture(volume);
			}
			if (staticVolume != 0)
			{
				GL.DeleteTexture(staticVolume);
			}

			volume = CreateVolumeTexture(resolution);
			staticVolume = CreateVolumeTexture(resolution);
			mipLevels = 1 + (int)Math.Floor(Math.Log(resolution, 2));

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultWidth, resolution);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultHeight, resolution);
			FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				Console.WriteLine("[VoxelGI] Voxelization framebuffer incomplete: " + status);
			}

			currentResolution = resolution;
			Lighting.GI.Resolution = resolution;
			Lighting.GI.StaticDirty = true;
			fullSyncNeeded = true;
			previousRegions.Clear();
			fullRegionList.Clear();
			fullRegionList.Add(VoxelRegion.Full(resolution));
			Console.WriteLine("[VoxelGI] Volume " + resolution + "^3 RGBA16F, " + mipLevels + " mips (+ static cache)");
		}

		private static int CreateVolumeTexture(int resolution)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.Rgba16f, resolution, resolution, resolution, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureBorderColor, new[] { 0f, 0f, 0f, 0f });
			// Allocate the mip chain once; levels are filled by the mip compute shader.
			GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		private void CreateGridShadow(int size)
		{
			gridShadow = new Framebuffer(size, size);
			RenderTexture depth = gridShadow.AddDepthAttachment(RenderTextureFormat.Depth32F, true, TextureWrapMode.ClampToBorder);
			depth.SetBorderColor(1f, 1f, 1f, 1f);
			depth.SetShadowCompare(true);
			gridShadow.Validate();
		}

		// ---------------------------------------------------------------- per frame bookkeeping

		private static void UpdateGridPlacement(RenderContext context, GlobalIlluminationSettings gi)
		{
			Vector3 center = gi.Center;
			if (gi.FollowCamera)
			{
				// Snap to voxel size so the volume does not swim while the camera moves.
				float voxel = gi.VoxelSize;
				Vector3 c = context.CameraPosition;
				center = new Vector3(
					(float)Math.Floor(c.X / voxel) * voxel,
					(float)Math.Floor(c.Y / voxel) * voxel,
					(float)Math.Floor(c.Z / voxel) * voxel);
			}

			gi.GridMin = center - new Vector3(gi.GridSize * 0.5f);
		}

		private void PartitionRenderers(IReadOnlyList<MeshRenderer> renderers)
		{
			staticRenderers.Clear();
			dynamicRenderers.Clear();
			foreach (MeshRenderer renderer in renderers)
			{
				if (!renderer.ContributesToGI || renderer.Material == null || renderer.Mesh == null)
				{
					continue;
				}

				if (renderer.IsStatic)
				{
					staticRenderers.Add(renderer);
				}
				else
				{
					dynamicRenderers.Add(renderer);
				}
			}
		}

		// Anything that changes the direct lighting stored in static voxels invalidates the cache.
		private void DetectStaticChanges(GlobalIlluminationSettings gi)
		{
			Vector3 lightDirection = DirectionalLight.This != null ? DirectionalLight.This.transform.forward : -Vector3.UnitY;
			Color4 lightColor = Lighting.Directional.Color;
			float lightStrength = Lighting.Directional.Strength;
			bool shadowsEnabled = Lighting.Shadows.Enabled;

			bool changed =
				(lightDirection - lastLightDirection).LengthSquared > 1e-6f
				|| lightColor != lastLightColor
				|| Math.Abs(lightStrength - lastLightStrength) > 1e-5f
				|| (gi.GridMin - lastGridMin).LengthSquared > 1e-6f
				|| Math.Abs(gi.GridSize - lastGridSize) > 1e-5f
				|| shadowsEnabled != lastShadowsEnabled
				|| staticRenderers.Count != lastStaticCount;

			if (changed)
			{
				gi.StaticDirty = true;
				lastLightDirection = lightDirection;
				lastLightColor = lightColor;
				lastLightStrength = lightStrength;
				lastGridMin = gi.GridMin;
				lastGridSize = gi.GridSize;
				lastShadowsEnabled = shadowsEnabled;
				lastStaticCount = staticRenderers.Count;
			}
		}

		// Voxel-space bounding box of a renderer (world AABB of its mesh bounds, padded by one voxel).
		private static bool TryGetVoxelRegion(MeshRenderer renderer, GlobalIlluminationSettings gi, out VoxelRegion region)
		{
			Matrix4 localToWorld = renderer.transform.LocalToWorld;
			Vector3 bmin = renderer.Mesh.BoundsMin;
			Vector3 bmax = renderer.Mesh.BoundsMax;

			Vector3 worldMin = new Vector3(float.MaxValue);
			Vector3 worldMax = new Vector3(float.MinValue);
			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = new Vector3(
					(i & 1) != 0 ? bmax.X : bmin.X,
					(i & 2) != 0 ? bmax.Y : bmin.Y,
					(i & 4) != 0 ? bmax.Z : bmin.Z);
				Vector3 world = Vector3.TransformPosition(corner, localToWorld);
				worldMin = Vector3.ComponentMin(worldMin, world);
				worldMax = Vector3.ComponentMax(worldMax, world);
			}

			float voxel = gi.VoxelSize;
			Vector3 vmin = (worldMin - gi.GridMin) / voxel;
			Vector3 vmax = (worldMax - gi.GridMin) / voxel;

			int res = gi.Resolution;
			int x0 = Math.Clamp((int)Math.Floor(vmin.X) - 1, 0, res);
			int y0 = Math.Clamp((int)Math.Floor(vmin.Y) - 1, 0, res);
			int z0 = Math.Clamp((int)Math.Floor(vmin.Z) - 1, 0, res);
			int x1 = Math.Clamp((int)Math.Ceiling(vmax.X) + 1, 0, res);
			int y1 = Math.Clamp((int)Math.Ceiling(vmax.Y) + 1, 0, res);
			int z1 = Math.Clamp((int)Math.Ceiling(vmax.Z) + 1, 0, res);

			region = new VoxelRegion(x0, y0, z0, x1, y1, z1);
			return !region.IsEmpty;
		}

		// ---------------------------------------------------------------- volume work

		private void RebuildStaticCache(RenderContext context, GlobalIlluminationSettings gi, Matrix4 lightView, Matrix4 lightProjection)
		{
			RenderGridShadow(context, staticRenderers, lightView, lightProjection);
			ClearVolume(staticVolume);
			Voxelize(staticVolume, staticRenderers, gi, lightView, lightProjection);
			UpdateMips(staticVolume, fullRegionList);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			gi.StaticDirty = false;
			fullSyncNeeded = true;
			RebuiltStaticThisFrame = true;
		}

		private float RegionCoverage(List<VoxelRegion> regions)
		{
			double total = 0;
			foreach (VoxelRegion region in regions)
			{
				total += (double)region.SizeX * region.SizeY * region.SizeZ;
			}

			return (float)(total / ((double)currentResolution * currentResolution * currentResolution));
		}

		// Whole working volume <- static cache, all mip levels. Driver copy is fine for one big transfer.
		private void RestoreFull()
		{
			for (int level = 0; level < mipLevels; level++)
			{
				int size = Math.Max(1, currentResolution >> level);
				GL.CopyImageSubData(
					staticVolume, ImageTarget.Texture3D, level, 0, 0, 0,
					volume, ImageTarget.Texture3D, level, 0, 0, 0,
					size, size, size);
			}

			fullSyncNeeded = false;
			previousRegions.Clear();
		}

		// Brings the level-0 regions dynamic objects occupied last frame back to the static state.
		private void RestoreRegions(List<VoxelRegion> regions)
		{
			if (regions.Count == 0)
			{
				return;
			}

			restore.Use();
			GL.BindImageTexture(0, staticVolume, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, volume, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

			foreach (VoxelRegion region in regions)
			{
				restore.Program.SetInt3("dstOffset", region.MinX, region.MinY, region.MinZ);
				restore.Program.SetInt3("dstSize", region.SizeX, region.SizeY, region.SizeZ);
				restore.DispatchThreads(region.SizeX, region.SizeY, region.SizeZ, 4, 4, 4);
			}

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		// Orthographic light frustum that encloses the whole voxel cube.
		private static void ComputeGridLightMatrices(GlobalIlluminationSettings gi, out Matrix4 lightView, out Matrix4 lightProjection)
		{
			Vector3 direction = DirectionalLight.This != null ? DirectionalLight.This.transform.forward : -Vector3.UnitY;
			if (direction.LengthSquared < 1e-6f)
			{
				direction = -Vector3.UnitY;
			}
			direction.Normalize();
			Vector3 up = Math.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

			Vector3 center = gi.GridMin + new Vector3(gi.GridSize * 0.5f);
			lightView = Matrix4.LookAt(center - direction * gi.GridSize, center, up);

			// A cube of edge s projects to at most s * sqrt(3) / 2 from its center in any direction.
			float extent = gi.GridSize * 0.87f;
			lightProjection = Matrix4.CreateOrthographicOffCenter(-extent, extent, -extent, extent, 0.01f, gi.GridSize * 2f);
		}

		private void RenderGridShadow(RenderContext context, IReadOnlyList<MeshRenderer> casters, Matrix4 lightView, Matrix4 lightProjection)
		{
			ShadowSettings shadows = Lighting.Shadows;

			gridShadow.Bind();
			GLState.SetDepth(true, true);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);
			GL.Clear(ClearBufferMask.DepthBufferBit);

			GL.Enable(EnableCap.PolygonOffsetFill);
			GL.PolygonOffset(shadows.PolygonOffsetFactor, shadows.PolygonOffsetUnits);

			context.DepthOnlyProgram.Use();
			context.DepthOnlyProgram.SetMatrix4("viewMatrix", lightView);
			context.DepthOnlyProgram.SetMatrix4("projectionMatrix", lightProjection);

			foreach (MeshRenderer renderer in casters)
			{
				if (renderer.CastShadows)
				{
					renderer.RenderDepth(context.DepthOnlyProgram);
				}
			}

			GL.Disable(EnableCap.PolygonOffsetFill);
			GLState.SetColorWrite(true);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		private void ClearVolume(int texture)
		{
			clear.Use();
			GL.BindImageTexture(0, texture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			int groups = currentResolution / 8;
			clear.Dispatch(groups, groups, groups);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		private void Voxelize(int texture, IReadOnlyList<MeshRenderer> renderers, GlobalIlluminationSettings gi, Matrix4 lightView, Matrix4 lightProjection)
		{
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.Viewport(0, 0, currentResolution, currentResolution);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.CullFace);
			GL.DepthMask(false);

			voxelize.Use();
			voxelize.SetVector3("voxelGridMin", gi.GridMin);
			voxelize.SetFloat("voxelGridSize", gi.GridSize);
			voxelize.SetInt("voxelResolution", currentResolution);

			// Light color/direction from the scene; shadows from the grid-covering map instead of the camera one.
			Lighting.SetUniforms(voxelize, false, false);
			ShadowSettings shadows = Lighting.Shadows;
			voxelize.SetInt("shadowsEnabled", shadows.Enabled ? 1 : 0);
			voxelize.SetMatrix4("lightSpaceMatrix", lightView * lightProjection);
			voxelize.SetFloat("shadowNormalBias", shadows.NormalBias);
			voxelize.SetFloat("shadowDepthBias", shadows.DepthBias);
			voxelize.SetTexture("ShadowMap", TextureTarget.Texture2D, gridShadow.DepthAttachment.Handle, Lighting.ShadowMapTextureUnit);

			GL.BindImageTexture(0, texture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

			foreach (MeshRenderer renderer in renderers)
			{
				SetMaterialUniforms(renderer.Material);
				renderer.RenderWith(voxelize);
			}

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			GL.DepthMask(true);
			GL.Enable(EnableCap.DepthTest);
			GL.Enable(EnableCap.CullFace);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		// Re-filters the mip chain inside the given level-0 regions (each shrinks per level).
		// Levels are processed outermost so one barrier per level suffices; tiny levels are redone whole.
		private void UpdateMips(int texture, List<VoxelRegion> regions)
		{
			if (regions.Count == 0)
			{
				return;
			}

			mip.Use();
			for (int level = 1; level < mipLevels; level++)
			{
				int levelSize = Math.Max(1, currentResolution >> level);
				GL.BindImageTexture(0, texture, level - 1, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, texture, level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

				if (levelSize <= SmallLevelSize)
				{
					DispatchMip(0, 0, 0, levelSize, levelSize, levelSize);
				}
				else
				{
					foreach (VoxelRegion region in regions)
					{
						VoxelRegion dst = region.AtLevel(level, levelSize);
						if (!dst.IsEmpty)
						{
							DispatchMip(dst.MinX, dst.MinY, dst.MinZ, dst.SizeX, dst.SizeY, dst.SizeZ);
						}
					}
				}

				ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
			}
		}

		private void DispatchMip(int x, int y, int z, int sizeX, int sizeY, int sizeZ)
		{
			mip.Program.SetInt3("dstOffset", x, y, z);
			mip.Program.SetInt3("dstSize", sizeX, sizeY, sizeZ);
			mip.DispatchThreads(sizeX, sizeY, sizeZ, 4, 4, 4);
		}

		// Materials describe themselves through conventional uniform names.
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

			voxelize.SetVector4("voxAlbedo", albedo);
			voxelize.SetVector4("voxEmissive", emissive);
			voxelize.SetInt("voxUseAlbedoMap", albedoMap != 0 ? 1 : 0);
			if (albedoMap != 0)
			{
				voxelize.SetTexture("voxAlbedoMap", TextureTarget.Texture2D, albedoMap, 0);
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>Half-open voxel index box [Min, Max).</summary>
		private readonly struct VoxelRegion
		{
			public readonly int MinX, MinY, MinZ;
			public readonly int MaxX, MaxY, MaxZ;

			public VoxelRegion(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
			{
				MinX = minX;
				MinY = minY;
				MinZ = minZ;
				MaxX = maxX;
				MaxY = maxY;
				MaxZ = maxZ;
			}

			public int SizeX => MaxX - MinX;
			public int SizeY => MaxY - MinY;
			public int SizeZ => MaxZ - MinZ;
			public bool IsEmpty => SizeX <= 0 || SizeY <= 0 || SizeZ <= 0;

			public static VoxelRegion Full(int resolution)
			{
				return new VoxelRegion(0, 0, 0, resolution, resolution, resolution);
			}

			/// <summary>Footprint of this level-0 region at a coarser mip level.</summary>
			public VoxelRegion AtLevel(int level, int levelSize)
			{
				int round = (1 << level) - 1;
				return new VoxelRegion(
					Math.Min(MinX >> level, levelSize), Math.Min(MinY >> level, levelSize), Math.Min(MinZ >> level, levelSize),
					Math.Min((MaxX + round) >> level, levelSize), Math.Min((MaxY + round) >> level, levelSize), Math.Min((MaxZ + round) >> level, levelSize));
			}
		}

		#endregion
	}
}
