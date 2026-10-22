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
	///        clearing only the voxel regions they occupied last frame. With
	///        <see cref="GlobalIlluminationSettings.VoxelCoverage" /> triangles are rasterized with multisampling into
	///        integer accumulators (coverage-weighted, order independent) that a compute pass resolves into the set.
	///     2. Light injection (compute) combines both geometry sets into radiance: albedo * (sun with shadows from a
	///        grid-covering shadow map + bounce) + emission. It runs 1 + <see cref="GlobalIlluminationSettings.BounceIterations" />
	///        times per frame, ping-ponging between two volumes: the first pass without the bounce, each later pass
	///        tracing the one before, so every frame is lit from scratch (nothing accumulates over frames, no lag).
	///        Each pass is mipmapped (compute) for the cones that read it: isotropic mips, six directional views per
	///        level or SH L1 gradients (<see cref="GlobalIlluminationSettings.VoxelMips" />).
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

		public override RenderPassScope Scope => RenderPassScope.Frame;
		public int VoxelTexture => previousValid ? radiance[current] : 0;

		/// <summary>The textures of the volume the materials read this frame (empty until the volume was lit once).</summary>
		public VoxelVolumeTextures Volume => previousValid ? VolumeTextures(current) : default;

		/// <summary>True on frames where the static geometry cache was (re)built. Useful for stats.</summary>
		public bool RebuiltStaticThisFrame { get; private set; }

		#endregion

		#region PrivateFields

		private const int BlockSize = 8;

		/// <summary>Samples per pixel of the coverage voxelization (fewer when the context has fewer).</summary>
		private const int CoverageSamples = 8;

		/// <summary>Profiler sections of the injection passes (pass 0 = direct light, then one per bounce).</summary>
		private static readonly string[] InjectSections = { "VoxelGI.inject", "VoxelGI.bounce1", "VoxelGI.bounce2", "VoxelGI.bounce3", "VoxelGI.bounce4", "VoxelGI.bounceN" };

		private static readonly string[] MipSections = { "VoxelGI.mips", "VoxelGI.mips1", "VoxelGI.mips2", "VoxelGI.mips3", "VoxelGI.mips4", "VoxelGI.mipsN" };

		private GeometryVolume staticGeometry;
		private GeometryVolume dynamicGeometry;
		private AccumulatorVolume accumulators;

		// Ping-pong radiance: each injection pass reads the other volume for its bounce.
		private readonly int[] radiance = new int[2];
		private int current;
		private bool previousValid;

		// Directional mips of each radiance volume: six slabs side by side along x, level 0 = the volume's mip 1.
		private readonly int[] anisoRadiance = new int[2];
		private int anisoSlabSize;
		private int anisoLevels;

		// SH L1 gradients of each radiance volume (d.x, d.y, d.z terms), half resolution, level 0 = the volume's mip 1.
		private readonly int[][] shGradients = { new int[3], new int[3] };
		private int shLevels;

		// The mips the volume has this frame (the setting, unless the context lacks the texture units).
		private VoxelMipMode mipMode;

		// The mips the volumes were made for (their directional textures exist for this mode only).
		private VoxelMipMode volumeMipMode;

		// The bounce per 2x2x2 voxels (irradiance / pi of every cell), refilled every pass.
		private int bounceCells;

		// Dynamic regions each radiance buffer held when it was last written; cleared before reuse.
		private readonly List<VoxelRegion>[] radianceRegions = { new List<VoxelRegion>(), new List<VoxelRegion>() };

		private int blockList;
		private int blockResolution;

		private int currentResolution;
		private int mipLevels;
		private int framebuffer;
		private int coverageFramebuffer;
		private int coverageSamples;
		private ShaderProgram voxelize;
		private ShaderProgram voxelizeCoverage;
		private ComputeShader clearGeometry;
		private ComputeShader clearAccumulators;
		private ComputeShader resolveGeometry;
		private ComputeShader clearRadiance;
		private ComputeShader compactBlocks;
		// Variants by the mips the bounce reads, indexed by VoxelMipMode (compiled in, see Engine/VoxelRadiance.glsl).
		private static readonly string[] MipModeDefines = { "LEL_VOXEL_ISO", "LEL_VOXEL_ANISO", "LEL_VOXEL_SH" };
		private readonly ComputeShader[] inject = new ComputeShader[3];
		private readonly ComputeShader[] bounce = new ComputeShader[3];
		private ComputeShader mip;
		private ComputeShader mipBlocks;
		private ComputeShader anisoBase;
		private ComputeShader anisoMip;
		private ComputeShader shBase;
		private Framebuffer gridShadow;
		private RenderTexture gridShadowTransmittance;

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
		private bool lastCoverage;
		private float lastOccupancyScale;
		private int lastSplatWidth;
		private VoxelMipMode lastMipMode;
		private int frameIndex;

		// Static cache state of this pass (each renderer has its own): rebuilt when dirty or when the settings'
		// static version moved on (GlobalIlluminationSettings.InvalidateStatic).
		private bool staticDirty = true;
		private int builtStaticVersion = -1;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			RebuiltStaticThisFrame = false;

			// Made when voxel cone tracing first runs, freed when it does not (GI off, Lumen).
			if (!gi.VoxelConeTracingActive)
			{
				Release();
				gi.VoxelTexture = 0;
				gi.VoxelVolume = default;
				return;
			}

			EnsureCreated(gi);
			VoxelMipMode volumeMode = MipModeFor(gi);
			if (radiance[0] == 0 || gi.Resolution != currentResolution || volumeMode != volumeMipMode)
			{
				CreateVolumes(gi.Resolution, volumeMode);
			}
			// The coverage accumulators exist while coverage voxelization is on (made by VoxelizeCoverage).
			if (!gi.VoxelCoverage)
			{
				accumulators.Delete();
			}
			if (gridShadow.Width != gi.GridShadowMapSize)
			{
				gridShadow.Delete();
				gridShadowTransmittance = null;
				CreateGridShadow(gi.GridShadowMapSize);
			}

			gi.UpdateGridPlacement(context.CameraPosition, context.CameraForward);
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

				// The direct light, then one pass per bounce, each tracing the volume the pass before wrote.
				int passes = 1 + (gi.BounceStrength > 0f ? Math.Max(0, gi.BounceIterations) : 0);
				for (int pass = 0; pass < passes; pass++)
				{
					profiler.Split(InjectSections[Math.Min(pass, InjectSections.Length - 1)]);
					int target = 1 - current;
					bool cells = pass > 0 && gi.BounceCells;
					if (cells)
					{
						TraceBounceCells(gi);
					}

					// Voxels dynamic objects left since this buffer was last written are not revisited; zero them.
					ClearRadiance(target, radianceRegions[target]);
					InjectLighting(target, gi, lightView, lightProjection, pass > 0, cells);
					radianceRegions[target].Clear();
					radianceRegions[target].AddRange(previousRegions);

					profiler.Split(MipSections[Math.Min(pass, MipSections.Length - 1)]);
					UpdateMips(target);
					ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

					current = target;
				}

				previousValid = true;
			}

			gi.VoxelTexture = VoxelTexture;
			gi.VoxelVolume = Volume;
		}

		public override void ReleaseSceneReferences()
		{
			staticRenderers.Clear();
			dynamicRenderers.Clear();
			lastStaticCount = -1;
			staticDirty = true;
		}

		public override void ReleaseUnusedResources()
		{
			if (!Lighting.GI.VoxelConeTracingActive)
			{
				Release();
			}
		}

		public override void Dispose()
		{
			Release();
			Lighting.GI.VoxelTexture = 0;
			Lighting.GI.VoxelVolume = default;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- setup

		// What every configuration uses; the rest is made where it is needed (the variants of the mips the volume has,
		// the coverage voxelization, the directional mips).
		private void EnsureCreated(GlobalIlluminationSettings gi)
		{
			if (framebuffer != 0)
			{
				return;
			}

			clearGeometry = new ComputeShader("Engine/VoxelClearGeometry.shader");
			clearRadiance = new ComputeShader("Engine/VoxelClearRadiance.shader");
			compactBlocks = new ComputeShader("Engine/VoxelCompactBlocks.shader");
			mip = new ComputeShader("Engine/VoxelMip.shader");
			mipBlocks = new ComputeShader("Engine/VoxelMipBlocks.shader");

			// Attachment-less framebuffers: rasterization happens, but all output goes through images.
			framebuffer = GL.GenFramebuffer();
			coverageFramebuffer = GL.GenFramebuffer();
			coverageSamples = Math.Max(1, Math.Min(CoverageSamples, GL.GetInteger((GetPName)0x9318))); // GL_MAX_FRAMEBUFFER_SAMPLES
			blockList = GL.GenBuffer();

			CreateGridShadow(gi.GridShadowMapSize);
		}

		private void Release()
		{
			if (framebuffer == 0)
			{
				return;
			}

			DeleteVolumes();
			GL.DeleteFramebuffer(framebuffer);
			framebuffer = 0;
			if (coverageFramebuffer != 0)
			{
				GL.DeleteFramebuffer(coverageFramebuffer);
				coverageFramebuffer = 0;
			}
			if (blockList != 0)
			{
				GL.DeleteBuffer(blockList);
				blockList = 0;
			}
			gridShadow?.Delete();
			gridShadow = null;
			gridShadowTransmittance = null;
			voxelize?.Delete();
			voxelizeCoverage?.Delete();
			clearGeometry?.Delete();
			clearAccumulators?.Delete();
			resolveGeometry?.Delete();
			clearRadiance?.Delete();
			compactBlocks?.Delete();
			voxelize = null;
			voxelizeCoverage = null;
			clearGeometry = null;
			clearAccumulators = null;
			resolveGeometry = null;
			clearRadiance = null;
			compactBlocks = null;
			for (int i = 0; i < inject.Length; i++)
			{
				inject[i]?.Delete();
				bounce[i]?.Delete();
				inject[i] = null;
				bounce[i] = null;
			}
			mip?.Delete();
			mipBlocks?.Delete();
			anisoBase?.Delete();
			anisoMip?.Delete();
			shBase?.Delete();
			mip = null;
			mipBlocks = null;
			anisoBase = null;
			anisoMip = null;
			shBase = null;

			// The next run starts like the first: volumes made, static geometry voxelized, nothing lit yet.
			currentResolution = 0;
			previousValid = false;
			lastStaticCount = -1;
			previousRegions.Clear();
			currentRegions.Clear();
			Console.WriteLine("[VoxelGI] Volume released");
		}

		// The volume's resolution for a setting: a multiple of the block size.
		private static int VolumeResolution(int setting)
		{
			return Math.Max(BlockSize, setting) / BlockSize * BlockSize;
		}

		private void CreateVolumes(int resolution, VoxelMipMode mode)
		{
			resolution = VolumeResolution(resolution);

			DeleteVolumes();

			blockResolution = resolution / BlockSize;
			staticGeometry = GeometryVolume.Create(resolution, blockResolution);
			dynamicGeometry = GeometryVolume.Create(resolution, blockResolution);
			radiance[0] = CreateRadianceTexture(resolution);
			radiance[1] = CreateRadianceTexture(resolution);

			// Directional mips: as many levels as the slab size halves evenly (all of them for a power of two). Only the
			// textures of the mips the cones read exist.
			anisoSlabSize = resolution / 2;
			anisoLevels = 0;
			for (int size = anisoSlabSize; size >= 1; size >>= 1)
			{
				anisoLevels++;
				if ((size & 1) != 0)
				{
					break;
				}
			}
			shLevels = 1 + (int)Math.Floor(Math.Log(resolution / 2, 2));
			if (mode == VoxelMipMode.Anisotropic)
			{
				anisoRadiance[0] = CreateAnisoTexture(anisoSlabSize, anisoLevels);
				anisoRadiance[1] = CreateAnisoTexture(anisoSlabSize, anisoLevels);
			}
			else if (mode == VoxelMipMode.SphericalHarmonics)
			{
				for (int i = 0; i < 2; i++)
				{
					for (int axis = 0; axis < 3; axis++)
					{
						shGradients[i][axis] = CreateRadianceTexture(resolution / 2);
					}
				}
			}
			volumeMipMode = mode;
			bounceCells = CreateVolumeTexture(resolution / 2, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.Float);

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

			SetDefaultFramebufferSize(framebuffer, resolution, 0);
			SetDefaultFramebufferSize(coverageFramebuffer, resolution, coverageSamples);

			// Injection skips empty blocks, so the radiance volumes must start out zero.
			ClearRadiance(0, VoxelRegion.Full(resolution));
			ClearRadiance(1, VoxelRegion.Full(resolution));
			radianceRegions[0].Clear();
			radianceRegions[1].Clear();

			Lighting.GI.Resolution = resolution;
			staticDirty = true;
			previousRegions.Clear();

			long voxels = (long)resolution * resolution * resolution;
			int directionalTextures = mode == VoxelMipMode.Anisotropic ? 6 : mode == VoxelMipMode.SphericalHarmonics ? 3 : 0;
			string directional = mode == VoxelMipMode.Anisotropic ? $" + anisotropic {anisoLevels} levels" : mode == VoxelMipMode.SphericalHarmonics ? $" + SH gradients {shLevels} levels" : "";
			double megabytes = (voxels * 4 * 6 + voxels * 8 * 2 * 8 / 7.0 + voxels / 8 * directionalTextures * 8 * 2 * 8 / 7.0) / (1024.0 * 1024.0);
			Console.WriteLine($"[VoxelGI] Volume {resolution}^3: geometry 2x(RGBA8+RGBA8+R11G11B10F), radiance 2xRGBA16F {mipLevels} mips{directional}, {blockResolution}^3 blocks, ~{megabytes:0} MB (+{voxels * 20 / (1024 * 1024)} MB coverage accumulators when used), {coverageSamples}x coverage samples");
		}

		private static void SetDefaultFramebufferSize(int handle, int resolution, int samples)
		{
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, handle);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultWidth, resolution);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultHeight, resolution);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultSamples, samples);
			FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				Console.WriteLine("[VoxelGI] Voxelization framebuffer incomplete: " + status);
			}
		}

		private void DeleteVolumes()
		{
			staticGeometry.Delete();
			dynamicGeometry.Delete();
			accumulators.Delete();
			for (int i = 0; i < 2; i++)
			{
				if (radiance[i] != 0)
				{
					GL.DeleteTexture(radiance[i]);
					radiance[i] = 0;
				}
				if (anisoRadiance[i] != 0)
				{
					GL.DeleteTexture(anisoRadiance[i]);
					anisoRadiance[i] = 0;
				}
				for (int axis = 0; axis < 3; axis++)
				{
					if (shGradients[i][axis] != 0)
					{
						GL.DeleteTexture(shGradients[i][axis]);
						shGradients[i][axis] = 0;
					}
				}
			}
			if (bounceCells != 0)
			{
				GL.DeleteTexture(bounceCells);
				bounceCells = 0;
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

		// Six slabs of slabSize^3 side by side along x. The slabs' own edges are kept by the sampling code (it clamps x
		// inside the slab); y and z read zero outside the volume, as the isotropic mips do.
		private static int CreateAnisoTexture(int slabSize, int levels)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexStorage3D(TextureTarget3d.Texture3D, levels, SizedInternalFormat.Rgba16f, slabSize * 6, slabSize, slabSize);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureBorderColor, new[] { 0f, 0f, 0f, 0f });
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, levels - 1);
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
				if (!renderer.ContributesToGI || renderer.Materials.Count == 0 || renderer.Mesh == null)
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

		// Only grid placement, the static set and the volume's representation invalidate the geometry cache; lighting is
		// recomputed anyway.
		private void DetectGeometryChanges(GlobalIlluminationSettings gi)
		{
			VoxelMipMode mode = MipModeFor(gi);
			bool changed =
				(gi.GridMin - lastGridMin).LengthSquared > 1e-6f
				|| Math.Abs(gi.GridSize - lastGridSize) > 1e-5f
				|| staticRenderers.Count != lastStaticCount
				|| gi.VoxelCoverage != lastCoverage
				|| gi.CoverageOccupancyScale != lastOccupancyScale
				|| gi.CoverageSplatWidth != lastSplatWidth
				|| mode != lastMipMode;

			if (changed)
			{
				staticDirty = true;
				lastGridMin = gi.GridMin;
				lastGridSize = gi.GridSize;
				lastStaticCount = staticRenderers.Count;
				lastCoverage = gi.VoxelCoverage;
				lastOccupancyScale = gi.CoverageOccupancyScale;
				lastSplatWidth = gi.CoverageSplatWidth;
				lastMipMode = mode;
			}

			mipMode = mode;
		}

		// Directional mips need their own texture units in the material shaders: without them the mips are isotropic.
		private VoxelMipMode MipModeFor(GlobalIlluminationSettings gi)
		{
			int units = gi.VoxelMips == VoxelMipMode.SphericalHarmonics ? 3 : 1;
			bool unitsAvailable = Lighting.VoxelDirectionalTextureUnit + units - 1 < GLCapabilities.MaxTextureUnits;
			if (!unitsAvailable || (gi.VoxelMips == VoxelMipMode.Anisotropic && gi.AnisotropicMipLevels <= 0))
			{
				return VoxelMipMode.Isotropic;
			}

			return gi.VoxelMips;
		}

		// The textures of radiance volume index as the cones read them.
		private VoxelVolumeTextures VolumeTextures(int index)
		{
			return new VoxelVolumeTextures
			{
				Mode = mipMode,
				Radiance = radiance[index],
				Aniso = mipMode == VoxelMipMode.Anisotropic ? anisoRadiance[index] : 0,
				AnisoLevels = AnisoLevelsUsed(Lighting.GI),
				SHX = mipMode == VoxelMipMode.SphericalHarmonics ? shGradients[index][0] : 0,
				SHY = mipMode == VoxelMipMode.SphericalHarmonics ? shGradients[index][1] : 0,
				SHZ = mipMode == VoxelMipMode.SphericalHarmonics ? shGradients[index][2] : 0
			};
		}

		// Directional levels the cones read (built and used); the isotropic mips take over above them.
		private int AnisoLevelsUsed(GlobalIlluminationSettings gi)
		{
			return Math.Clamp(gi.AnisotropicMipLevels, 1, Math.Max(1, anisoLevels));
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
			Voxelize(staticGeometry, staticRenderers, gi, null);

			// Blocks that no longer hold geometry will not be revisited by injection: start both buffers clean.
			ClearRadiance(0, VoxelRegion.Full(currentResolution));
			ClearRadiance(1, VoxelRegion.Full(currentResolution));
			radianceRegions[0].Clear();
			radianceRegions[1].Clear();

			staticDirty = false;
			builtStaticVersion = gi.StaticVersion;
			RebuiltStaticThisFrame = true;
		}

		private void UpdateDynamicGeometry(GlobalIlluminationSettings gi)
		{
			currentRegions.Clear();
			// The B-spline splat reaches a voxel and a half past a surface.
			int padding = gi.VoxelCoverage && gi.CoverageSplatWidth >= 3 ? 2 : 1;
			foreach (MeshRenderer renderer in dynamicRenderers)
			{
				VoxelRegion region;
				if (VoxelRegion.TryFromRenderer(renderer, gi.GridMin, gi.VoxelSize, gi.Resolution, padding, out region))
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
				Voxelize(dynamicGeometry, dynamicRenderers, gi, RegionCoverage(currentRegions) > FullClearCoverage ? null : currentRegions);
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

		// Radiance levels 0 and 1 and the directional level 0 are only rewritten for occupied blocks, so they must be
		// cleared explicitly.
		private void ClearRadiance(int index, VoxelRegion region)
		{
			if (region.IsEmpty)
			{
				return;
			}

			clearRadiance.Use();
			for (int level = 0; level <= 1; level++)
			{
				GL.BindImageTexture(0, radiance[index], level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				DispatchRegion(clearRadiance, region.AtLevel(level, Math.Max(1, currentResolution >> level)));
			}

			// The directional textures of the volume's mips (only one kind exists).
			VoxelRegion slabRegion = region.AtLevel(1, anisoSlabSize);
			if (anisoRadiance[index] != 0)
			{
				GL.BindImageTexture(0, anisoRadiance[index], 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				for (int slab = 0; slab < 6; slab++)
				{
					DispatchRegion(clearRadiance, slabRegion.OffsetX(slab * anisoSlabSize));
				}
			}

			for (int axis = 0; axis < 3 && shGradients[index][axis] != 0; axis++)
			{
				GL.BindImageTexture(0, shGradients[index][axis], 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				DispatchRegion(clearRadiance, slabRegion);
			}
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		private void ClearRadiance(int index, List<VoxelRegion> regions)
		{
			if (regions.Count == 0)
			{
				return;
			}

			if (RegionCoverage(regions) > FullClearCoverage)
			{
				ClearRadiance(index, VoxelRegion.Full(currentResolution));
				return;
			}

			clearRadiance.Use();
			for (int level = 0; level <= 1; level++)
			{
				GL.BindImageTexture(0, radiance[index], level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				int levelSize = Math.Max(1, currentResolution >> level);
				foreach (VoxelRegion region in regions)
				{
					DispatchRegion(clearRadiance, region.AtLevel(level, levelSize));
				}
			}

			if (anisoRadiance[index] != 0)
			{
				GL.BindImageTexture(0, anisoRadiance[index], 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				foreach (VoxelRegion region in regions)
				{
					VoxelRegion slabRegion = region.AtLevel(1, anisoSlabSize);
					for (int slab = 0; slab < 6; slab++)
					{
						DispatchRegion(clearRadiance, slabRegion.OffsetX(slab * anisoSlabSize));
					}
				}
			}

			for (int axis = 0; axis < 3 && shGradients[index][axis] != 0; axis++)
			{
				GL.BindImageTexture(0, shGradients[index][axis], 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				foreach (VoxelRegion region in regions)
				{
					DispatchRegion(clearRadiance, region.AtLevel(1, anisoSlabSize));
				}
			}
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		// Shared by the region clear shaders: dstOffset / dstSize uniforms, 4^3 work groups.
		private static void DispatchRegion(ComputeShader shader, VoxelRegion region)
		{
			if (region.IsEmpty)
			{
				return;
			}

			shader.Program.SetInt3("dstOffset", region.MinX, region.MinY, region.MinZ);
			shader.Program.SetInt3("dstSize", region.SizeX, region.SizeY, region.SizeZ);
			shader.DispatchThreads(region.SizeX, region.SizeY, region.SizeZ, 4, 4, 4);
		}

		// regions: where the renderers can be (null = the whole volume); only the coverage path needs them.
		private void Voxelize(GeometryVolume target, IReadOnlyList<MeshRenderer> renderers, GlobalIlluminationSettings gi, List<VoxelRegion> regions)
		{
			if (renderers.Count == 0)
			{
				return;
			}

			if (gi.VoxelCoverage)
			{
				VoxelizeCoverage(target, renderers, gi, regions);
				return;
			}

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.Viewport(0, 0, currentResolution, currentResolution);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.CullFace);
			GL.DepthMask(false);

			voxelize ??= new ShaderProgram("Engine/Voxelize.shader");
			voxelize.Use();
			voxelize.SetVector3("voxelGridMin", gi.GridMin);
			voxelize.SetFloat("voxelGridSize", gi.GridSize);
			voxelize.SetInt("voxelResolution", currentResolution);
			target.BindImages(0, TextureAccess.WriteOnly, true);
			DrawRenderers(voxelize, renderers);

			// Geometry is read as images (injection) and block flags as textures (compaction).
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			GL.DepthMask(true);
			GL.Enable(EnableCap.DepthTest);
			GL.Enable(EnableCap.CullFace);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		// Coverage voxelization: clear the accumulators where the renderers can be, rasterize them with multisampling
		// into the accumulators (block flags straight into the target), then resolve the accumulators into the target.
		private void VoxelizeCoverage(GeometryVolume target, IReadOnlyList<MeshRenderer> renderers, GlobalIlluminationSettings gi, List<VoxelRegion> regions)
		{
			if (accumulators.AlbedoRG == 0)
			{
				accumulators = AccumulatorVolume.Create(currentResolution);
			}
			if (voxelizeCoverage == null)
			{
				voxelizeCoverage = new ShaderProgram("Engine/VoxelizeCoverage.shader");
				clearAccumulators = new ComputeShader("Engine/VoxelClearAccumulators.shader");
				resolveGeometry = new ComputeShader("Engine/VoxelResolveGeometry.shader");
			}

			clearAccumulators.Use();
			accumulators.BindImages(0, TextureAccess.WriteOnly);
			DispatchRegions(clearAccumulators, regions);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, coverageFramebuffer);
			GL.Viewport(0, 0, currentResolution, currentResolution);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.CullFace);
			GL.DepthMask(false);
			GL.Enable(EnableCap.Multisample);

			voxelizeCoverage.Use();
			voxelizeCoverage.SetVector3("voxelGridMin", gi.GridMin);
			voxelizeCoverage.SetFloat("voxelGridSize", gi.GridSize);
			voxelizeCoverage.SetInt("voxelResolution", currentResolution);
			voxelizeCoverage.SetInt("voxSamples", coverageSamples);
			voxelizeCoverage.SetInt("voxSplatWidth", gi.CoverageSplatWidth);
			accumulators.BindImages(0, TextureAccess.ReadWrite);
			GL.BindImageTexture(5, target.Blocks, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R8);
			DrawRenderers(voxelizeCoverage, renderers);

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
			GL.DepthMask(true);
			GL.Enable(EnableCap.DepthTest);
			GL.Enable(EnableCap.CullFace);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

			resolveGeometry.Use();
			target.BindImages(0, TextureAccess.WriteOnly, false);
			ShaderProgram program = resolveGeometry.Program;
			program.SetTexture("AccAlbedoRG", TextureTarget.Texture3D, accumulators.AlbedoRG, 0);
			program.SetTexture("AccAlbedoBW", TextureTarget.Texture3D, accumulators.AlbedoBW, 1);
			program.SetTexture("AccNormalXY", TextureTarget.Texture3D, accumulators.NormalXY, 2);
			program.SetTexture("AccNormalZ", TextureTarget.Texture3D, accumulators.NormalZ, 3);
			program.SetTexture("AccEmission", TextureTarget.Texture3D, accumulators.Emission, 4);
			program.SetFloat("occupancyScale", gi.CoverageOccupancyScale);
			DispatchRegions(resolveGeometry, regions);

			// Geometry is read as images (injection) and block flags as textures (compaction).
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		private void DispatchRegions(ComputeShader shader, List<VoxelRegion> regions)
		{
			if (regions == null)
			{
				DispatchRegion(shader, VoxelRegion.Full(currentResolution));
				return;
			}

			foreach (VoxelRegion region in regions)
			{
				DispatchRegion(shader, region);
			}
		}

		private void DrawRenderers(ShaderProgram program, IReadOnlyList<MeshRenderer> renderers)
		{
			foreach (MeshRenderer renderer in renderers)
			{
				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					Material material = renderer.GetMaterial(i);
					if (material == null || material.IsTransparent)
					{
						continue;
					}

					SetMaterialUniforms(program, material);
					renderer.RenderSubMeshWith(program, i);
				}
			}
		}

		// Materials describe themselves through conventional uniform names.
		private static void SetMaterialUniforms(ShaderProgram program, Material material)
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

			program.SetVector4("voxAlbedo", albedo);
			program.SetVector4("voxEmissive", emissive);
			program.SetInt("voxUseAlbedoMap", albedoMap != 0 ? 1 : 0);
			if (albedoMap != 0)
			{
				program.SetTexture("voxAlbedoMap", TextureTarget.Texture2D, albedoMap, 0);
			}

			int emissionMap = material.GetTextureHandle("EmissionMap");
			program.SetInt("voxUseEmissionMap", emissionMap != 0 ? 1 : 0);
			if (emissionMap != 0)
			{
				program.SetTexture("voxEmissionMap", TextureTarget.Texture2D, emissionMap, 1);
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
			gridShadow.Bind();
			GLState.SetDepth(true, true);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);
			GL.Clear(ClearBufferMask.DepthBufferBit);

			context.DepthOnlyProgram.Use();
			context.DepthOnlyProgram.SetMatrix4("viewMatrix", lightView);
			context.DepthOnlyProgram.SetMatrix4("projectionMatrix", lightProjection);

			foreach (MeshRenderer renderer in casters)
			{
				if (renderer.CastShadows)
				{
					renderer.RenderWith(context.DepthOnlyProgram);
				}
			}

			gridShadowTransmittance = TransparentShadows.Draw(context, gridShadow, lightView, lightProjection);

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

		// The bounce of the next injection pass per 2x2x2 voxels, traced from radiance[current] (the previous pass).
		private void TraceBounceCells(GlobalIlluminationSettings gi)
		{
			ComputeShader shader = bounce[(int)mipMode] ??= new ComputeShader("Engine/VoxelBounceCells.shader", MipModeDefines[(int)mipMode]);
			shader.Use();
			ShaderProgram program = shader.Program;
			SetTraceUniforms(program, gi);
			program.SetTexture("StaticAlbedo", TextureTarget.Texture3D, staticGeometry.Albedo, 4);
			program.SetTexture("StaticNormal", TextureTarget.Texture3D, staticGeometry.Normal, 5);
			program.SetTexture("DynamicAlbedo", TextureTarget.Texture3D, dynamicGeometry.Albedo, 6);
			program.SetTexture("DynamicNormal", TextureTarget.Texture3D, dynamicGeometry.Normal, 7);
			GL.BindImageTexture(0, bounceCells, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, blockList);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, blockList);
			GL.DispatchComputeIndirect(IntPtr.Zero);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, 0);

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		// Grid and cone parameters of the volume's own traces (bounce cells, per-voxel bounce): the previous pass's
		// radiance is the trace target.
		private void SetTraceUniforms(ShaderProgram program, GlobalIlluminationSettings gi)
		{
			program.SetVector3("voxelGridMin", gi.GridMin);
			program.SetFloat("voxelGridSize", gi.GridSize);
			program.SetInt("voxelResolution", currentResolution);
			program.SetFloat("giConeMaxDistance", gi.ConeMaxDistance);
			// Per-voxel bounces jitter per voxel, bounce cells per cell (their own hash).
			program.SetInt("giConeJitter", gi.ConeJitter ? 1 : 0);
			program.SetFloat("giConeJitterCell", gi.VoxelSize);
			// Directional data on units 12-14: 1 and 2 hold the shadow maps, 4-7 the geometry and the bounce cells, 8 and
			// 11 the distance field, 16 the sky.
			Lighting.SetVoxelRadianceUniforms(program, VolumeTextures(current), 0, 12);
			// The bounce traces like the materials: with SDF cones walls stop it, voxel cones would bring the light
			// from outside in through them. The SDF detail trace is not used here (binary, too sharp per voxel).
			bool sdf = gi.DistanceFieldEnabled && gi.GlobalSdf != 0;
			int traceMode = Lighting.TraceModeUniform(sdf) == 2 ? 2 : 0;
			program.SetInt("giTraceMode", traceMode);
			if (traceMode == 2)
			{
				Lighting.SetSdfUniforms(program);
			}

			Lighting.SetSkyUniforms(program);
		}

		// One injection pass into radiance[target]; with bounce it lights from radiance[current] (the previous pass),
		// through the bounce cells or by tracing from every voxel.
		private void InjectLighting(int target, GlobalIlluminationSettings gi, Matrix4 lightView, Matrix4 lightProjection, bool bounce, bool cells)
		{
			ComputeShader shader = inject[(int)mipMode] ??= new ComputeShader("Engine/VoxelInject.shader", MipModeDefines[(int)mipMode]);
			ShaderProgram program = shader.Program;

			shader.Use();

			// Sun color/direction; shadows come from the grid-covering map, not the camera one.
			Lighting.SetUniforms(program, false, false);
			Lighting.SetShadowMapUniforms(program, lightView, lightProjection, gridShadow.DepthAttachment.Handle, 1);
			TransparentShadows.SetUniforms(program, gridShadowTransmittance, 2);
			program.SetInt("injectShadowTaps", gi.InjectShadowTaps);

			SetTraceUniforms(program, gi);
			program.SetFloat("giBounceStrength", bounce ? gi.BounceStrength : 0f);
			program.SetInt("giBounceCones", gi.BounceCones);
			program.SetInt("giBounceCells", cells ? 1 : 0);
			program.SetTexture("VoxelBounceCells", TextureTarget.Texture3D, bounceCells, 4);

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

		// The mips the cones read: isotropic (always, except where the directional levels cover every level), then the
		// directional data of the mode.
		private void UpdateMips(int index)
		{
			switch (mipMode)
			{
				case VoxelMipMode.Anisotropic:
					int levels = AnisoLevelsUsed(Lighting.GI);
					if (1 + levels < mipLevels)
					{
						UpdateIsotropicMips(radiance[index]);
					}
					UpdateAnisoMips(index, levels);
					break;
				case VoxelMipMode.SphericalHarmonics:
					UpdateIsotropicMips(radiance[index]);
					UpdateSHMips(index);
					break;
				default:
					UpdateIsotropicMips(radiance[index]);
					break;
			}
		}

		// Box-filters the mip chain of a radiance volume. Level 1, which holds most of the work, is only
		// computed for occupied blocks (indirect dispatch over the block list); coarser levels run whole.
		private void UpdateIsotropicMips(int texture)
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

			BoxMips(texture, currentResolution, 2, mipLevels);
		}

		// Levels [firstLevel, levels) of a texture whose level 0 is baseSize^3, each the 2x2x2 average of the one below.
		private void BoxMips(int texture, int baseSize, int firstLevel, int levels)
		{
			mip.Use();
			for (int level = firstLevel; level < levels; level++)
			{
				int levelSize = Math.Max(1, baseSize >> level);
				GL.BindImageTexture(0, texture, level - 1, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, texture, level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				mip.Program.SetInt3("dstOffset", 0, 0, 0);
				mip.Program.SetInt3("dstSize", levelSize, levelSize, levelSize);
				mip.DispatchThreads(levelSize, levelSize, levelSize, 4, 4, 4);
				ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
			}
		}

		// SH L1 gradients: level 0 (the volume's mip 1) from the voxels of occupied blocks and their normals, then plain
		// averages (the gradients are linear in the voxels).
		private void UpdateSHMips(int index)
		{
			shBase ??= new ComputeShader("Engine/VoxelSHBase.shader");
			shBase.Use();
			ShaderProgram program = shBase.Program;
			program.SetTexture("StaticAlbedo", TextureTarget.Texture3D, staticGeometry.Albedo, 4);
			program.SetTexture("StaticNormal", TextureTarget.Texture3D, staticGeometry.Normal, 5);
			program.SetTexture("DynamicAlbedo", TextureTarget.Texture3D, dynamicGeometry.Albedo, 6);
			program.SetTexture("DynamicNormal", TextureTarget.Texture3D, dynamicGeometry.Normal, 7);
			GL.BindImageTexture(0, radiance[index], 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
			for (int axis = 0; axis < 3; axis++)
			{
				GL.BindImageTexture(1 + axis, shGradients[index][axis], 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			}
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, blockList);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, blockList);
			GL.DispatchComputeIndirect(IntPtr.Zero);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, 0);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);

			for (int axis = 0; axis < 3; axis++)
			{
				BoxMips(shGradients[index][axis], currentResolution / 2, 1, shLevels);
			}
		}

		// Directional mips: level 0 (the volume's mip 1) from the voxels of occupied blocks, then the coarser levels in
		// use whole, each slab from the same slab one level finer.
		private void UpdateAnisoMips(int index, int levels)
		{
			anisoBase ??= new ComputeShader("Engine/VoxelAnisoBase.shader");
			anisoMip ??= new ComputeShader("Engine/VoxelAnisoMip.shader");
			anisoBase.Use();
			anisoBase.Program.SetInt("slabSize", anisoSlabSize);
			GL.BindImageTexture(0, radiance[index], 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, anisoRadiance[index], 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, blockList);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, blockList);
			GL.DispatchComputeIndirect(IntPtr.Zero);
			GL.BindBuffer(BufferTarget.DispatchIndirectBuffer, 0);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);

			anisoMip.Use();
			for (int level = 1; level < levels; level++)
			{
				int slabSize = anisoSlabSize >> level;
				GL.BindImageTexture(0, anisoRadiance[index], level - 1, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, anisoRadiance[index], level, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				anisoMip.Program.SetInt("slabSize", slabSize);
				anisoMip.DispatchThreads(slabSize * 6, slabSize, slabSize, 4, 4, 4);
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
					Albedo = CreateVolumeTexture(resolution, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
					Normal = CreateVolumeTexture(resolution, PixelInternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
					Emissive = CreateVolumeTexture(resolution, PixelInternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.Float),
					Blocks = CreateVolumeTexture(blockResolution, PixelInternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte)
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
		}

		/// <summary>
		///     Integer accumulators of the coverage voxelization (Engine/VoxelizeCoverage.shader): weighted sums of albedo
		///     (r|g, b|weight as 16-bit fixed point), normal (x|y, z) and emission (RGB9E5). Shared by the static and the
		///     dynamic set, which are voxelized one after the other.
		/// </summary>
		private struct AccumulatorVolume
		{
			public int AlbedoRG;
			public int AlbedoBW;
			public int NormalXY;
			public int NormalZ;
			public int Emission;

			public static AccumulatorVolume Create(int resolution)
			{
				return new AccumulatorVolume
				{
					AlbedoRG = CreateVolumeTexture(resolution, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt),
					AlbedoBW = CreateVolumeTexture(resolution, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt),
					NormalXY = CreateVolumeTexture(resolution, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt),
					NormalZ = CreateVolumeTexture(resolution, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt),
					Emission = CreateVolumeTexture(resolution, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt)
				};
			}

			public void BindImages(int unit, TextureAccess access)
			{
				GL.BindImageTexture(unit, AlbedoRG, 0, true, 0, access, SizedInternalFormat.R32ui);
				GL.BindImageTexture(unit + 1, AlbedoBW, 0, true, 0, access, SizedInternalFormat.R32ui);
				GL.BindImageTexture(unit + 2, NormalXY, 0, true, 0, access, SizedInternalFormat.R32ui);
				GL.BindImageTexture(unit + 3, NormalZ, 0, true, 0, access, SizedInternalFormat.R32ui);
				GL.BindImageTexture(unit + 4, Emission, 0, true, 0, access, SizedInternalFormat.R32ui);
			}

			public void Delete()
			{
				if (AlbedoRG != 0) GL.DeleteTexture(AlbedoRG);
				if (AlbedoBW != 0) GL.DeleteTexture(AlbedoBW);
				if (NormalXY != 0) GL.DeleteTexture(NormalXY);
				if (NormalZ != 0) GL.DeleteTexture(NormalZ);
				if (Emission != 0) GL.DeleteTexture(Emission);
				AlbedoRG = AlbedoBW = NormalXY = NormalZ = Emission = 0;
			}
		}

		private static int CreateVolumeTexture(int resolution, PixelInternalFormat internalFormat, PixelFormat format, PixelType type)
		{
			return CreateTexture3D(resolution, resolution, resolution, internalFormat, format, type);
		}

		// Accessed through image load/store or texelFetch only; no filtering, no mips.
		private static int CreateTexture3D(int width, int height, int depth, PixelInternalFormat internalFormat, PixelFormat format, PixelType type)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, internalFormat, width, height, depth, 0, format, type, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		#endregion
	}
}
