using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Voxel cone tracing, stage 1: builds the radiance volume sampled by Engine/VoxelConeTracing.glsl.
	///
	///     Geometry and lighting are decoupled, in the spirit of Lumen's surface cache:
	///     1. Geometry voxelization writes surface attributes (albedo + occupancy, normal, emission) and 8^3
	///        block occupancy flags into 3D textures. Static renderers (<see cref="MeshRenderer.IsStatic" />)
	///        go into a cache built once; dynamic renderers are re-voxelized into a separate set each frame,
	///        clearing only the voxel regions they occupied last frame.
	///     2. Light injection (compute) combines both geometry sets into this frame's radiance:
	///        albedo * (sun with shadows from a grid-covering shadow map + bounce traced from the previous
	///        frame's radiance volume) + emission. The feedback yields multi-bounce lighting that converges
	///        over a few frames. The result is mipmapped (compute) for cone tracing.
	///        Injection runs only over occupied blocks: a compaction pass lists them in an SSBO that doubles
	///        as the indirect dispatch arguments. Voxels that became empty are zeroed by region instead.
	///
	///     Dynamic shadows, moving lights and emitters therefore affect the GI of static geometry at no
	///     re-voxelization cost, and the per-frame cost scales with occupied space rather than volume size.
	/// </summary>
	public sealed class VoxelGIPass : RenderPass
	{
		#region PublicFields

		public override string Name => "VoxelGI";
		public int VoxelTexture => previousValid ? radiance[current] : 0;

		/// <summary>True on frames where the static geometry cache was (re)built. Useful for stats.</summary>
		public bool RebuiltStaticThisFrame { get; private set; }

		#endregion

		#region PrivateFields

		private const int BlockSize = 8;

		private GeometryVolume staticGeometry;
		private GeometryVolume dynamicGeometry;

		// Double-buffered radiance: injection reads the previous frame's volume for the bounce.
		private readonly int[] radiance = new int[2];
		private int current;
		private bool previousValid;

		// Dynamic regions each radiance buffer held when it was last written; cleared before reuse.
		private readonly List<VoxelRegion>[] radianceRegions = { new List<VoxelRegion>(), new List<VoxelRegion>() };

		private int blockList;
		private int blockResolution;

		private int currentResolution;
		private int mipLevels;
		private int framebuffer;
		private ShaderProgram voxelize;
		private ComputeShader clearGeometry;
		private ComputeShader clearRadiance;
		private ComputeShader compactBlocks;
		private ComputeShader inject;
		private ComputeShader mip;
		private ComputeShader mipBlocks;
		private Framebuffer gridShadow;

		private readonly List<MeshRenderer> staticRenderers = new List<MeshRenderer>();
		private readonly List<MeshRenderer> dynamicRenderers = new List<MeshRenderer>();

		// Voxel regions written by dynamic renderers; previous ones must be cleared before re-voxelizing.
		private List<VoxelRegion> previousRegions = new List<VoxelRegion>();
		private List<VoxelRegion> currentRegions = new List<VoxelRegion>();

		/// <summary>Above this fraction of the volume touched by dynamic regions, a full clear is cheaper.</summary>
		private const float FullClearCoverage = 0.5f;

		// Geometry cache invalidation tracking
		private Vector3 lastGridMin;
		private float lastGridSize;
		private int lastStaticCount = -1;
		private int frameIndex;

		// Static cache state of this pass (each renderer has its own): rebuilt when dirty or when the settings'
		// static version moved on (GlobalIlluminationSettings.InvalidateStatic).
		private bool staticDirty = true;
		private int builtStaticVersion = -1;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			voxelize = new ShaderProgram("Engine/Voxelize.shader");
			clearGeometry = new ComputeShader("Engine/VoxelClearGeometry.shader");
			clearRadiance = new ComputeShader("Engine/VoxelClearRadiance.shader");
			compactBlocks = new ComputeShader("Engine/VoxelCompactBlocks.shader");
			inject = new ComputeShader("Engine/VoxelInject.shader");
			mip = new ComputeShader("Engine/VoxelMip.shader");
			mipBlocks = new ComputeShader("Engine/VoxelMipBlocks.shader");

			// Attachment-less framebuffer: rasterization happens, but all output goes through imageStore.
			framebuffer = GL.GenFramebuffer();
			blockList = GL.GenBuffer();

			CreateVolumes(Lighting.GI.Resolution);
			CreateGridShadow(Lighting.GI.GridShadowMapSize);
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			RebuiltStaticThisFrame = false;

			if (!gi.Enabled || gi.Mode != GIMode.VoxelConeTracing)
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

			gi.UpdateGridPlacement(context.CameraPosition);
			PartitionRenderers(context.Renderers);
			DetectGeometryChanges(gi);

			frameIndex++;
			bool dynamicDue = gi.DynamicUpdateInterval <= 1 || frameIndex % gi.DynamicUpdateInterval == 0;
			bool lightingDue = !previousValid || gi.LightingUpdateInterval <= 1 || frameIndex % gi.LightingUpdateInterval == 0;
			GpuProfiler profiler = context.Renderer.Profiler;

			if (staticDirty || gi.StaticVersion != builtStaticVersion)
			{
				profiler.Split("VoxelGI.static");
				RebuildStaticCache(gi);
			}

			if (dynamicDue)
			{
				profiler.Split("VoxelGI.dynamic");
				UpdateDynamicGeometry(gi);
			}

			if (lightingDue)
			{
				Matrix4 lightView;
				Matrix4 lightProjection;
				ComputeGridLightMatrices(gi, out lightView, out lightProjection);

				profiler.Split("VoxelGI.shadow");
				RenderGridShadow(context, context.Renderers, lightView, lightProjection);

				profiler.Split("VoxelGI.blocks");
				CompactOccupiedBlocks();

				profiler.Split("VoxelGI.inject");
				int target = 1 - current;
				// Voxels dynamic objects left since this buffer was last written are not revisited; zero them.
				ClearRadiance(radiance[target], radianceRegions[target]);
				InjectLighting(target, gi, lightView, lightProjection);
				radianceRegions[target].Clear();
				radianceRegions[target].AddRange(previousRegions);

				profiler.Split("VoxelGI.mips");
				UpdateMips(radiance[target]);
				ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

				current = target;
				previousValid = true;
			}

			gi.VoxelTexture = VoxelTexture;
		}

		public override void ReleaseSceneReferences()
		{
			staticRenderers.Clear();
			dynamicRenderers.Clear();
			lastStaticCount = -1;
			staticDirty = true;
		}

		public override void Dispose()
		{
			DeleteVolumes();
			if (framebuffer != 0)
			{
				GL.DeleteFramebuffer(framebuffer);
				framebuffer = 0;
			}
			if (blockList != 0)
			{
				GL.DeleteBuffer(blockList);
				blockList = 0;
			}
			gridShadow?.Delete();
			gridShadow = null;
			voxelize?.Delete();
			clearGeometry?.Delete();
			clearRadiance?.Delete();
			compactBlocks?.Delete();
			inject?.Delete();
			mip?.Delete();
			mipBlocks?.Delete();
			Lighting.GI.VoxelTexture = 0;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- setup

		private void CreateVolumes(int resolution)
		{
			resolution = Math.Max(BlockSize, resolution);
			// Must be a multiple of the block size.
			resolution = resolution / BlockSize * BlockSize;

			DeleteVolumes();

			blockResolution = resolution / BlockSize;
			staticGeometry = GeometryVolume.Create(resolution, blockResolution);
			dynamicGeometry = GeometryVolume.Create(resolution, blockResolution);
			radiance[0] = CreateRadianceTexture(resolution);
			radiance[1] = CreateRadianceTexture(resolution);
			current = 0;
			previousValid = false;
			mipLevels = 1 + (int)Math.Floor(Math.Log(resolution, 2));
			currentResolution = resolution;

			// Block list: 16-byte indirect dispatch header followed by one packed uint per occupied block.
			int blockCapacity = blockResolution * blockResolution * blockResolution;
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, blockList);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, 16 + sizeof(uint) * blockCapacity, IntPtr.Zero, BufferUsageHint.DynamicDraw);
			uint[] header = { 0u, 1u, 1u, 0u };
			GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, 16, header);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultWidth, resolution);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultHeight, resolution);
			FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				Console.WriteLine("[VoxelGI] Voxelization framebuffer incomplete: " + status);
			}

			// Injection skips empty blocks, so the radiance volumes must start out zero.
			ClearRadiance(radiance[0], VoxelRegion.Full(resolution));
			ClearRadiance(radiance[1], VoxelRegion.Full(resolution));
			radianceRegions[0].Clear();
			radianceRegions[1].Clear();

			Lighting.GI.Resolution = resolution;
			staticDirty = true;
			previousRegions.Clear();

			long voxels = (long)resolution * resolution * resolution;
			double megabytes = (voxels * 4 * 6 + voxels * 8 * 2 * 8 / 7.0) / (1024.0 * 1024.0);
			Console.WriteLine($"[VoxelGI] Volume {resolution}^3: geometry 2x(RGBA8+RGBA8+R11G11B10F), radiance 2xRGBA16F {mipLevels} mips, {blockResolution}^3 blocks, ~{megabytes:0} MB");
		}

		private void DeleteVolumes()
		{
			staticGeometry.Delete();
			dynamicGeometry.Delete();
			for (int i = 0; i < 2; i++)
			{
				if (radiance[i] != 0)
				{
					GL.DeleteTexture(radiance[i]);
					radiance[i] = 0;
				}
			}
		}

		private static int CreateRadianceTexture(int resolution)
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

		// Only grid placement and the static set invalidate the geometry cache; lighting is recomputed anyway.
		private void DetectGeometryChanges(GlobalIlluminationSettings gi)
		{
			bool changed =
				(gi.GridMin - lastGridMin).LengthSquared > 1e-6f
				|| Math.Abs(gi.GridSize - lastGridSize) > 1e-5f
				|| staticRenderers.Count != lastStaticCount;

			if (changed)
			{
				staticDirty = true;
				lastGridMin = gi.GridMin;
				lastGridSize = gi.GridSize;
				lastStaticCount = staticRenderers.Count;
			}
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

		// ---------------------------------------------------------------- geometry volumes

		private void RebuildStaticCache(GlobalIlluminationSettings gi)
		{
			ClearGeometry(staticGeometry, VoxelRegion.Full(currentResolution));
			Voxelize(staticGeometry, staticRenderers, gi);

			// Blocks that no longer hold geometry will not be revisited by injection: start both buffers clean.
			ClearRadiance(radiance[0], VoxelRegion.Full(currentResolution));
			ClearRadiance(radiance[1], VoxelRegion.Full(currentResolution));
			radianceRegions[0].Clear();
			radianceRegions[1].Clear();

			staticDirty = false;
			builtStaticVersion = gi.StaticVersion;
			RebuiltStaticThisFrame = true;
		}

		private void UpdateDynamicGeometry(GlobalIlluminationSettings gi)
		{
			currentRegions.Clear();
			foreach (MeshRenderer renderer in dynamicRenderers)
			{
				VoxelRegion region;
				if (VoxelRegion.TryFromRenderer(renderer, gi.GridMin, gi.VoxelSize, gi.Resolution, 1, out region))
				{
					currentRegions.Add(region);
				}
			}

			// Erase last frame's dynamic voxels, then write this frame's.
			if (RegionCoverage(previousRegions) > FullClearCoverage)
			{
				ClearGeometry(dynamicGeometry, VoxelRegion.Full(currentResolution));
			}
			else
			{
				ClearGeometry(dynamicGeometry, previousRegions);
			}

			if (dynamicRenderers.Count > 0)
			{
				Voxelize(dynamicGeometry, dynamicRenderers, gi);
			}

			List<VoxelRegion> swap = previousRegions;
			previousRegions = currentRegions;
			currentRegions = swap;
		}

		private void ClearGeometry(GeometryVolume target, VoxelRegion region)
		{
			if (region.IsEmpty)
			{
				return;
			}

			clearGeometry.Use();
			target.BindImages(0, TextureAccess.WriteOnly, true);
			DispatchRegion(clearGeometry, region);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		private void ClearGeometry(GeometryVolume target, List<VoxelRegion> regions)
		{
			if (regions.Count == 0)
			{
				return;
			}

			clearGeometry.Use();
			target.BindImages(0, TextureAccess.WriteOnly, true);
			foreach (VoxelRegion region in regions)
			{
				DispatchRegion(clearGeometry, region);
			}
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		// Radiance levels 0 and 1 are only rewritten for occupied blocks, so both must be cleared explicitly.
		private void ClearRadiance(int texture, VoxelRegion region)
		{
			if (region.IsEmpty)
			{
				return;
			}

			clearRadiance.Use();
			for (int level = 0; level <= 1; level++)
			{
				GL.BindImageTexture(0, texture, level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				DispatchRegion(clearRadiance, region.AtLevel(level, Math.Max(1, currentResolution >> level)));
			}
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		private void ClearRadiance(int texture, List<VoxelRegion> regions)
		{
			if (regions.Count == 0)
			{
				return;
			}

			if (RegionCoverage(regions) > FullClearCoverage)
			{
				ClearRadiance(texture, VoxelRegion.Full(currentResolution));
				return;
			}

			clearRadiance.Use();
			for (int level = 0; level <= 1; level++)
			{
				GL.BindImageTexture(0, texture, level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				int levelSize = Math.Max(1, currentResolution >> level);
				foreach (VoxelRegion region in regions)
				{
					DispatchRegion(clearRadiance, region.AtLevel(level, levelSize));
				}
			}
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		// Shared by the region clear shaders: dstOffset / dstSize uniforms, 4^3 work groups.
		private static void DispatchRegion(ComputeShader shader, VoxelRegion region)
		{
			shader.Program.SetInt3("dstOffset", region.MinX, region.MinY, region.MinZ);
			shader.Program.SetInt3("dstSize", region.SizeX, region.SizeY, region.SizeZ);
			shader.DispatchThreads(region.SizeX, region.SizeY, region.SizeZ, 4, 4, 4);
		}

		private void Voxelize(GeometryVolume target, IReadOnlyList<MeshRenderer> renderers, GlobalIlluminationSettings gi)
		{
			if (renderers.Count == 0)
			{
				return;
			}

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.Viewport(0, 0, currentResolution, currentResolution);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.CullFace);
			GL.DepthMask(false);

			voxelize.Use();
			voxelize.SetVector3("voxelGridMin", gi.GridMin);
			voxelize.SetFloat("voxelGridSize", gi.GridSize);
			voxelize.SetInt("voxelResolution", currentResolution);
			target.BindImages(0, TextureAccess.WriteOnly, true);

			foreach (MeshRenderer renderer in renderers)
			{
				SetMaterialUniforms(renderer.Material);
				renderer.RenderWith(voxelize);
			}

			// Geometry is read as images (injection) and block flags as textures (compaction).
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			GL.DepthMask(true);
			GL.Enable(EnableCap.DepthTest);
			GL.Enable(EnableCap.CullFace);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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

		// ---------------------------------------------------------------- lighting

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

		// Lists occupied blocks into the SSBO whose header is the indirect dispatch for injection.
		private void CompactOccupiedBlocks()
		{
			uint zero = 0;
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, blockList);
			GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, sizeof(uint), ref zero);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, blockList);

			compactBlocks.Use();
			compactBlocks.Program.SetInt("blockResolution", blockResolution);
			compactBlocks.Program.SetTexture("StaticBlocks", TextureTarget.Texture3D, staticGeometry.Blocks, 0);
			compactBlocks.Program.SetTexture("DynamicBlocks", TextureTarget.Texture3D, dynamicGeometry.Blocks, 1);
			compactBlocks.DispatchThreads(blockResolution, blockResolution, blockResolution, 4, 4, 4);

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.CommandBarrierBit);
		}

		private void InjectLighting(int target, GlobalIlluminationSettings gi, Matrix4 lightView, Matrix4 lightProjection)
		{
			ShadowSettings shadows = Lighting.Shadows;
			ShaderProgram program = inject.Program;

			inject.Use();

			// Sun color/direction; shadows come from the grid-covering map, not the camera one.
			Lighting.SetUniforms(program, false, false);
			program.SetInt("shadowsEnabled", shadows.Enabled ? 1 : 0);
			program.SetMatrix4("lightSpaceMatrix", lightView * lightProjection);
			program.SetFloat("shadowNormalBias", shadows.NormalBias);
			program.SetFloat("shadowDepthBias", shadows.DepthBias);
			program.SetTexture("ShadowMap", TextureTarget.Texture2D, gridShadow.DepthAttachment.Handle, 1);

			// Grid and cone parameters for the bounce; the previous frame's radiance is the trace target.
			program.SetVector3("voxelGridMin", gi.GridMin);
			program.SetFloat("voxelGridSize", gi.GridSize);
			program.SetInt("voxelResolution", currentResolution);
			program.SetFloat("giConeMaxDistance", gi.ConeMaxDistance);
			program.SetFloat("giBounceStrength", previousValid ? gi.BounceStrength : 0f);
			program.SetInt("giBounceCones", gi.BounceCones);
			program.SetTexture("VoxelRadiance", TextureTarget.Texture3D, radiance[current], 0);
			// The bounce traces like the materials: with SDF cones walls stop it, voxel cones would bring the light
			// from outside in through them. The SDF detail trace is not used here (binary, too sharp per voxel).
			bool sdf = gi.DistanceFieldEnabled && gi.GlobalSdf != 0;
			int traceMode = Lighting.TraceModeUniform(sdf) == 2 ? 2 : 0;
			program.SetInt("giTraceMode", traceMode);
			if (traceMode == 2)
			{
				Lighting.SetSdfUniforms(program);
			}

			program.SetVector3("giSkyRadiance", gi.SkyRadiance);

			staticGeometry.BindImages(0, TextureAccess.ReadOnly, false);
			dynamicGeometry.BindImages(3, TextureAccess.ReadOnly, false);
			GL.BindImageTexture(6, radiance[target], 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

			// One work group per occupied block, count taken from the compaction result.
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, blockList);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, blockList);
			GL.DispatchComputeIndirect(IntPtr.Zero);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, 0);

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		// Box-filters the mip chain of a radiance volume. Level 1, which holds most of the work, is only
		// computed for occupied blocks (indirect dispatch over the block list); coarser levels run whole.
		private void UpdateMips(int texture)
		{
			if (mipLevels > 1)
			{
				mipBlocks.Use();
				GL.BindImageTexture(0, texture, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, texture, 1, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, blockList);
				GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, blockList);
				GL.DispatchComputeIndirect(IntPtr.Zero);
				GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, 0);
				ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
			}

			mip.Use();
			for (int level = 2; level < mipLevels; level++)
			{
				int levelSize = Math.Max(1, currentResolution >> level);
				GL.BindImageTexture(0, texture, level - 1, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, texture, level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				mip.Program.SetInt3("dstOffset", 0, 0, 0);
				mip.Program.SetInt3("dstSize", levelSize, levelSize, levelSize);
				mip.DispatchThreads(levelSize, levelSize, levelSize, 4, 4, 4);
				ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>Surface attributes of voxelized geometry (albedo + occupancy, normal, emission) plus block flags.</summary>
		private struct GeometryVolume
		{
			public int Albedo;
			public int Normal;
			public int Emissive;

			/// <summary>R8 volume at 1/8 resolution: 1 where the 8^3 block contains any geometry.</summary>
			public int Blocks;

			public static GeometryVolume Create(int resolution, int blockResolution)
			{
				return new GeometryVolume
				{
					Albedo = CreateTexture(resolution, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
					Normal = CreateTexture(resolution, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
					Emissive = CreateTexture(resolution, PixelInternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.Float),
					Blocks = CreateTexture(blockResolution, PixelInternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte)
				};
			}

			/// <summary>Binds albedo, normal, emissive (and optionally the block flags) as images from unit upwards.</summary>
			public void BindImages(int unit, TextureAccess access, bool includeBlocks)
			{
				GL.BindImageTexture(unit, Albedo, 0, true, 0, access, SizedInternalFormat.Rgba8);
				GL.BindImageTexture(unit + 1, Normal, 0, true, 0, access, SizedInternalFormat.Rgba8);
				GL.BindImageTexture(unit + 2, Emissive, 0, true, 0, access, SizedInternalFormat.R11fG11fB10f);
				if (includeBlocks)
				{
					GL.BindImageTexture(unit + 3, Blocks, 0, true, 0, access, SizedInternalFormat.R8);
				}
			}

			public void Delete()
			{
				if (Albedo != 0) GL.DeleteTexture(Albedo);
				if (Normal != 0) GL.DeleteTexture(Normal);
				if (Emissive != 0) GL.DeleteTexture(Emissive);
				if (Blocks != 0) GL.DeleteTexture(Blocks);
				Albedo = Normal = Emissive = Blocks = 0;
			}

			private static int CreateTexture(int resolution, PixelInternalFormat internalFormat, PixelFormat format, PixelType type)
			{
				int texture = GL.GenTexture();
				GL.BindTexture(TextureTarget.Texture3D, texture);
				GL.TexImage3D(TextureTarget.Texture3D, 0, internalFormat, resolution, resolution, resolution, 0, format, type, IntPtr.Zero);
				// Accessed through image load/store or texelFetch only; no filtering, no mips.
				GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
				GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
				GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
				GL.BindTexture(TextureTarget.Texture3D, 0);
				return texture;
			}
		}

		#endregion
	}
}
