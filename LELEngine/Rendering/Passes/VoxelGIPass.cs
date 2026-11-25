using System;
using System.Collections.Generic;
using System.Linq;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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

		/// <summary>
		///     The volume the materials read this frame: radiance premultiplied by occupancy (rgb) and occupancy (a), with
		///     isotropic mips. Null until the volume was lit once.
		/// </summary>
		public GpuTexture RadianceTexture => previousValid ? radiance[current] : null;

		/// <summary>The mips that volume has (what its directional textures hold).</summary>
		public VoxelMipMode MipMode => mipMode;

		/// <summary><see cref="VoxelMipMode.Anisotropic" />: six views side by side along x, level 0 = the volume's mip 1 (null otherwise).</summary>
		public GpuTexture AnisoTexture => previousValid && mipMode == VoxelMipMode.Anisotropic ? anisoRadiance[current] : null;

		/// <summary>Directional levels the cones read of <see cref="AnisoTexture" />.</summary>
		public int AnisoLevels => AnisoLevelsUsed(Lighting.GI);

		/// <summary><see cref="VoxelMipMode.SphericalHarmonics" />: the gradient of an axis (0..2), level 0 = the volume's mip 1 (null otherwise).</summary>
		public GpuTexture SHTexture(int axis)
		{
			return previousValid && mipMode == VoxelMipMode.SphericalHarmonics ? shGradients[current][axis] : null;
		}

		/// <summary>The textures of the volume the materials read this frame (empty until the volume was lit once).</summary>
		public VoxelVolumeTextures Volume => previousValid ? VolumeTextures(current) : default;

		/// <summary>True on frames where the static geometry cache was (re)built. Useful for stats.</summary>
		public bool RebuiltStaticThisFrame { get; private set; }

		#endregion

		#region PrivateFields

		private const int BlockSize = 8;

		/// <summary>Samples per pixel of the coverage voxelization (fewer when the device gives a pass fewer).</summary>
		private const int CoverageSamples = 8;

		/// <summary>Profiler sections of the injection passes (pass 0 = direct light, then one per bounce).</summary>
		private static readonly string[] InjectSections = { "VoxelGI.inject", "VoxelGI.bounce1", "VoxelGI.bounce2", "VoxelGI.bounce3", "VoxelGI.bounce4", "VoxelGI.bounceN" };

		private static readonly string[] MipSections = { "VoxelGI.mips", "VoxelGI.mips1", "VoxelGI.mips2", "VoxelGI.mips3", "VoxelGI.mips4", "VoxelGI.mipsN" };

		private GeometryVolume staticGeometry;
		private GeometryVolume dynamicGeometry;
		private AccumulatorVolume accumulators;

		// Ping-pong radiance: each injection pass reads the other volume for its bounce.
		private readonly GpuTexture[] radiance = new GpuTexture[2];
		private int current;
		private bool previousValid;

		// Directional mips of each radiance volume: six slabs side by side along x, level 0 = the volume's mip 1.
		private readonly GpuTexture[] anisoRadiance = new GpuTexture[2];
		private int anisoSlabSize;
		private int anisoLevels;

		// SH L1 gradients of each radiance volume (d.x, d.y, d.z terms), half resolution, level 0 = the volume's mip 1.
		private readonly GpuTexture[][] shGradients = { new GpuTexture[3], new GpuTexture[3] };
		private int shLevels;

		// The mips the volume has this frame (the setting, unless the device lets a program sample too few textures).
		private VoxelMipMode mipMode;

		// The mips the volumes were made for (their directional textures exist for this mode only).
		private VoxelMipMode volumeMipMode;

		// The bounce per 2x2x2 voxels (irradiance / pi of every cell), refilled every pass.
		private GpuTexture bounceCells;

		// Dynamic regions each radiance buffer held when it was last written; cleared before reuse.
		private readonly List<VoxelRegion>[] radianceRegions = { new List<VoxelRegion>(), new List<VoxelRegion>() };

		private GpuBuffer blockList;
		private int blockResolution;

		private int currentResolution;
		private int mipLevels;

		// Samples per pixel of the coverage voxelization's pass on this device.
		private int coverageSamples;
		private ShaderProgram voxelize;
		private ShaderProgram voxelizeCoverage;

		// The coverage voxelization rasterizing conservatively (LEL_VOXEL_CONSERVATIVE), where the device can.
		private ShaderProgram voxelizeConservative;

		// The same three programs' instanced variants: the renderers drawn GPU-driven from a table (GIDrawTable.Enabled), a
		// multi-draw per material.
		private ShaderProgram voxelizeInstanced;
		private ShaderProgram voxelizeCoverageInstanced;
		private ShaderProgram voxelizeConservativeInstanced;

		// The static cache's and the dynamic renderers' tables (one each: both may draw in a frame).
		private readonly GIDrawTable staticTable = new GIDrawTable("Voxel GI static geometry");
		private readonly GIDrawTable dynamicTable = new GIDrawTable("Voxel GI dynamic geometry");

		// The batches of the table being drawn: their materials in order, and the sub-meshes of each (reused; cleared after
		// each voxelization, so no scene reference is kept).
		private readonly List<Material> tableMaterials = new List<Material>();
		private readonly Dictionary<Material, int> tableMaterialSlots = new Dictionary<Material, int>();
		private readonly List<List<TablePart>> tableParts = new List<List<TablePart>>();
		private ComputeShader clearGeometry;
		private ComputeShader clearAccumulators;
		private ComputeShader resolveGeometry;
		private ComputeShader clearRadiance;
		private ComputeShader compactBlocks;
		// Variants by the mips the bounce reads, indexed by VoxelMipMode (compiled in, see Engine/VoxelRadiance.glsl); the
		// last three trace the scene's triangles where the first read the distance field (Lighting.VoxelRayTracingActive).
		private static readonly string[] MipModeDefines = { "LEL_VOXEL_ISO", "LEL_VOXEL_ANISO", "LEL_VOXEL_SH" };
		private readonly ComputeShader[] inject = new ComputeShader[6];
		private readonly ComputeShader[] bounce = new ComputeShader[6];

		// The scene whose triangles the bounce's cones trace this frame (the renderer's, while Lighting.VoxelRayTracingActive).
		private Lumen.LumenScene rayScene;

		// How the cones see the scene's geometry, as last said in the console.
		private string reportedCones;
		private ComputeShader mip;
		private ComputeShader mipBlocks;
		private ComputeShader anisoBase;
		private ComputeShader anisoMip;
		private ComputeShader shBase;
		private GridShadowMap gridShadow;

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
		private bool lastConservative;
		private float lastOccupancyScale;
		private VoxelCoverageSplat lastSplat;
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
				gi.VoxelTexture = null;
				gi.VoxelVolume = default;
				Lighting.VoxelRayScene = null;
				return;
			}

			EnsureCreated(gi);
			VoxelMipMode volumeMode = MipModeFor(gi);
			if (radiance[0] == null || gi.Resolution != currentResolution || volumeMode != volumeMipMode)
			{
				CreateVolumes(gi.Resolution, volumeMode);
			}
			// The coverage accumulators exist while coverage voxelization is on (made by VoxelizeCoverage).
			if (!gi.VoxelCoverage)
			{
				accumulators.Delete();
			}
			gridShadow.EnsureSize(gi.GridShadowMapSize);

			gi.UpdateGridPlacement(context.CameraPosition, context.CameraForward);
			PartitionRenderers(context.GIRenderers ?? context.Renderers);
			DetectGeometryChanges(gi);
			rayScene = Lighting.VoxelRayTracingActive ? context.Renderer.LumenScene : null;
			// The materials that trace their own cones (Lighting.RayQueryProgram) trace the same scene.
			Lighting.VoxelRayScene = rayScene;
			ReportCones(gi);

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
				// A grid-covering map per directional light that lights the GI with shadows.
				profiler.Split("VoxelGI.shadow");
				gridShadow.Render(context, context.GIRenderers ?? context.Renderers, gi);

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
					InjectLighting(target, gi, pass > 0, cells);
					radianceRegions[target].Clear();
					radianceRegions[target].AddRange(previousRegions);

					profiler.Split(MipSections[Math.Min(pass, MipSections.Length - 1)]);
					UpdateMips(target);

					current = target;
				}

				previousValid = true;
			}

			gi.VoxelTexture = RadianceTexture;
			gi.VoxelVolume = Volume;
		}

		public override void ReleaseSceneReferences()
		{
			staticRenderers.Clear();
			dynamicRenderers.Clear();
			lastStaticCount = -1;
			staticDirty = true;
			// The renderer's scene was replaced (InvalidateSceneCaches): the one the rays traced holds the old scene's
			// renderers until the next Execute takes the new one, which an idle editor may not run for a long time.
			ForgetRayScene();
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
			Lighting.GI.VoxelTexture = null;
			Lighting.GI.VoxelVolume = default;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- setup

		// What every configuration uses; the rest is made where it is needed (the variants of the mips the volume has,
		// the coverage voxelization, the directional mips).
		private void EnsureCreated(GlobalIlluminationSettings gi)
		{
			if (blockList != null)
			{
				return;
			}

			clearGeometry = new ComputeShader("Engine/VoxelClearGeometry.shader");
			clearRadiance = new ComputeShader("Engine/VoxelClearRadiance.shader");
			compactBlocks = new ComputeShader("Engine/VoxelCompactBlocks.shader");
			mip = new ComputeShader("Engine/VoxelMip.shader");
			mipBlocks = new ComputeShader("Engine/VoxelMipBlocks.shader");

			// Voxelization draws into passes without attachments: rasterization happens, but all output goes through images.
			coverageSamples = Math.Max(1, Math.Min(CoverageSamples, Gfx.Capabilities.MaxPassSamples));
			// The indirect dispatch's arguments and, behind them, the list the dispatched groups read.
			blockList = Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage | BufferUsage.Indirect, 0, true, "Voxel GI occupied blocks"));

			gridShadow = new GridShadowMap(gi.GridShadowMapSize);
		}

		private void Release()
		{
			if (blockList == null)
			{
				return;
			}

			DeleteVolumes();
			blockList.Dispose();
			blockList = null;
			gridShadow?.Delete();
			gridShadow = null;
			voxelize?.Delete();
			voxelizeCoverage?.Delete();
			voxelizeConservative?.Delete();
			voxelizeInstanced?.Delete();
			voxelizeCoverageInstanced?.Delete();
			voxelizeConservativeInstanced?.Delete();
			voxelizeInstanced = null;
			voxelizeCoverageInstanced = null;
			voxelizeConservativeInstanced = null;
			staticTable.Dispose();
			dynamicTable.Dispose();
			clearGeometry?.Delete();
			clearAccumulators?.Delete();
			resolveGeometry?.Delete();
			clearRadiance?.Delete();
			compactBlocks?.Delete();
			voxelize = null;
			voxelizeCoverage = null;
			voxelizeConservative = null;
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
			ForgetRayScene();
			reportedCones = null;
			Console.WriteLine("[VoxelGI] Volume released");
		}

		// Lets go of the scene the cones traced, and of what this pass published of it for the materials' cones.
		private void ForgetRayScene()
		{
			if (rayScene != null && ReferenceEquals(Lighting.VoxelRayScene, rayScene))
			{
				Lighting.VoxelRayScene = null;
			}

			rayScene = null;
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
			bounceCells = CreateVolumeTexture(resolution / 2, TextureFormat.RGBA16F, TextureUsage.Sampled | TextureUsage.Storage);

			current = 0;
			previousValid = false;
			mipLevels = 1 + (int)Math.Floor(Math.Log(resolution, 2));
			currentResolution = resolution;

			// Block list: 16-byte indirect dispatch header followed by one packed uint per occupied block.
			int blockCapacity = blockResolution * blockResolution * blockResolution;
			blockList.Allocate(16 + sizeof(uint) * blockCapacity);
			uint[] header = { 0u, 1u, 1u, 0u };
			blockList.SetSubData(0, header);

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
			string coverage = ConservativeVoxelization(Lighting.GI) ? "conservative rasterization" : $"{coverageSamples}x coverage samples";
			Console.WriteLine($"[VoxelGI] Volume {resolution}^3: geometry 2x(RGBA8+RGBA8+R11G11B10F), radiance 2xRGBA16F {mipLevels} mips{directional}, {blockResolution}^3 blocks, ~{megabytes:0} MB (+{voxels * 20 / (1024 * 1024)} MB coverage accumulators when used), {coverage}");
		}

		private void DeleteVolumes()
		{
			staticGeometry.Delete();
			dynamicGeometry.Delete();
			accumulators.Delete();
			for (int i = 0; i < 2; i++)
			{
				radiance[i]?.Dispose();
				radiance[i] = null;
				anisoRadiance[i]?.Dispose();
				anisoRadiance[i] = null;
				for (int axis = 0; axis < 3; axis++)
				{
					shGradients[i][axis]?.Dispose();
					shGradients[i][axis] = null;
				}
			}
			bounceCells?.Dispose();
			bounceCells = null;
		}

		private static GpuTexture CreateRadianceTexture(int resolution)
		{
			// The whole mip chain; levels are filled by the mip compute shader.
			GpuTexture texture = Gfx.Device.CreateTexture(TextureDescriptor.Texture3D(TextureFormat.RGBA16F, resolution, resolution, resolution, 0,
				TextureUsage.Sampled | TextureUsage.Storage, "Voxel radiance"));
			// Zero outside the volume.
			texture.Sampler = Samplers.Get(SamplerFilter.Linear, SamplerMipFilter.Linear, SamplerAddress.ClampToBorder);
			return texture;
		}

		// Six slabs of slabSize^3 side by side along x. The slabs' own edges are kept by the sampling code (it clamps x
		// inside the slab); y and z read zero outside the volume, as the isotropic mips do.
		private static GpuTexture CreateAnisoTexture(int slabSize, int levels)
		{
			GpuTexture texture = Gfx.Device.CreateTexture(TextureDescriptor.Texture3D(TextureFormat.RGBA16F, slabSize * 6, slabSize, slabSize, levels,
				TextureUsage.Sampled | TextureUsage.Storage, "Voxel radiance, directional"));
			SamplerDescriptor sampler = new SamplerDescriptor(SamplerFilter.Linear, SamplerMipFilter.Linear, SamplerAddress.ClampToBorder);
			sampler.AddressU = SamplerAddress.ClampToEdge;
			texture.Sampler = Gfx.Device.GetSampler(sampler);
			return texture;
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
				|| ConservativeVoxelization(gi) != lastConservative
				|| gi.CoverageOccupancyScale != lastOccupancyScale
				|| gi.CoverageSplat != lastSplat
				|| mode != lastMipMode;

			if (changed)
			{
				staticDirty = true;
				lastGridMin = gi.GridMin;
				lastGridSize = gi.GridSize;
				lastStaticCount = staticRenderers.Count;
				lastCoverage = gi.VoxelCoverage;
				lastConservative = ConservativeVoxelization(gi);
				lastOccupancyScale = gi.CoverageOccupancyScale;
				lastSplat = gi.CoverageSplat;
				lastMipMode = mode;
			}

			mipMode = mode;
		}

		// Directional mips are further textures for the material shaders to sample: without room for them the mips are isotropic.
		private VoxelMipMode MipModeFor(GlobalIlluminationSettings gi)
		{
			int textures = gi.VoxelMips == VoxelMipMode.SphericalHarmonics ? 3 : 1;
			if (!Lighting.VoxelDirectionalSamplersAvailable(textures) || (gi.VoxelMips == VoxelMipMode.Anisotropic && gi.AnisotropicMipLevels <= 0))
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
				Aniso = mipMode == VoxelMipMode.Anisotropic ? anisoRadiance[index] : null,
				AnisoLevels = AnisoLevelsUsed(Lighting.GI),
				SHX = mipMode == VoxelMipMode.SphericalHarmonics ? shGradients[index][0] : null,
				SHY = mipMode == VoxelMipMode.SphericalHarmonics ? shGradients[index][1] : null,
				SHZ = mipMode == VoxelMipMode.SphericalHarmonics ? shGradients[index][2] : null
			};
		}

		// The textures of radiance volume index for a program of the pass's own that traces it (Engine/VoxelRadiance.glsl):
		// the voxels with their isotropic mips, and the directional data of the mode.
		private void SetRadianceUniforms(ShaderProgram program, int index)
		{
			program.SetTexture("VoxelRadiance", radiance[index]);
			program.SetInt("giVoxelMips", (int)mipMode);
			if (mipMode == VoxelMipMode.Anisotropic)
			{
				program.SetInt("giVoxelAnisoLevels", AnisoLevelsUsed(Lighting.GI));
				program.SetTexture("VoxelRadianceAniso", anisoRadiance[index]);
			}
			else if (mipMode == VoxelMipMode.SphericalHarmonics)
			{
				program.SetTexture("VoxelRadianceSHX", shGradients[index][0]);
				program.SetTexture("VoxelRadianceSHY", shGradients[index][1]);
				program.SetTexture("VoxelRadianceSHZ", shGradients[index][2]);
			}
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
			Voxelize(staticGeometry, staticRenderers, staticTable, gi, null);

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
			int padding = gi.VoxelCoverage && gi.CoverageSplat == VoxelCoverageSplat.Quadratic ? 2 : 1;
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
				Voxelize(dynamicGeometry, dynamicRenderers, dynamicTable, gi, RegionCoverage(currentRegions) > FullClearCoverage ? null : currentRegions);
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
			target.BindImages(0, ImageAccess.Write, true);
			DispatchRegion(clearGeometry, region);
		}

		private void ClearGeometry(GeometryVolume target, List<VoxelRegion> regions)
		{
			if (regions.Count == 0)
			{
				return;
			}

			clearGeometry.Use();
			target.BindImages(0, ImageAccess.Write, true);
			foreach (VoxelRegion region in regions)
			{
				DispatchRegion(clearGeometry, region);
			}
		}

		// Radiance levels 0 and 1 and the directional level 0 are only rewritten for occupied blocks, so they must be
		// cleared explicitly.
		private void ClearRadiance(int index, VoxelRegion region)
		{
			if (region.IsEmpty)
			{
				return;
			}

			CommandList commands = Gfx.Commands;
			clearRadiance.Use();
			for (int level = 0; level <= 1; level++)
			{
				commands.SetImage(0, radiance[index], level, -1, ImageAccess.Write);
				DispatchRegion(clearRadiance, region.AtLevel(level, Math.Max(1, currentResolution >> level)));
			}

			// The directional textures of the volume's mips (only one kind exists).
			VoxelRegion slabRegion = region.AtLevel(1, anisoSlabSize);
			if (anisoRadiance[index] != null)
			{
				commands.SetImage(0, anisoRadiance[index], 0, -1, ImageAccess.Write);
				for (int slab = 0; slab < 6; slab++)
				{
					DispatchRegion(clearRadiance, slabRegion.OffsetX(slab * anisoSlabSize));
				}
			}

			for (int axis = 0; axis < 3 && shGradients[index][axis] != null; axis++)
			{
				commands.SetImage(0, shGradients[index][axis], 0, -1, ImageAccess.Write);
				DispatchRegion(clearRadiance, slabRegion);
			}
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

			CommandList commands = Gfx.Commands;
			clearRadiance.Use();
			for (int level = 0; level <= 1; level++)
			{
				commands.SetImage(0, radiance[index], level, -1, ImageAccess.Write);
				int levelSize = Math.Max(1, currentResolution >> level);
				foreach (VoxelRegion region in regions)
				{
					DispatchRegion(clearRadiance, region.AtLevel(level, levelSize));
				}
			}

			if (anisoRadiance[index] != null)
			{
				commands.SetImage(0, anisoRadiance[index], 0, -1, ImageAccess.Write);
				foreach (VoxelRegion region in regions)
				{
					VoxelRegion slabRegion = region.AtLevel(1, anisoSlabSize);
					for (int slab = 0; slab < 6; slab++)
					{
						DispatchRegion(clearRadiance, slabRegion.OffsetX(slab * anisoSlabSize));
					}
				}
			}

			for (int axis = 0; axis < 3 && shGradients[index][axis] != null; axis++)
			{
				commands.SetImage(0, shGradients[index][axis], 0, -1, ImageAccess.Write);
				foreach (VoxelRegion region in regions)
				{
					DispatchRegion(clearRadiance, region.AtLevel(1, anisoSlabSize));
				}
			}
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

		// regions: where the renderers can be (null = the whole volume); only the coverage path needs them. table: the one
		// the renderers are drawn from when the GI draws GPU-driven (GIDrawTable.Enabled).
		private void Voxelize(GeometryVolume target, IReadOnlyList<MeshRenderer> renderers, GIDrawTable table, GlobalIlluminationSettings gi, List<VoxelRegion> regions)
		{
			if (renderers.Count == 0)
			{
				return;
			}

			if (gi.VoxelCoverage)
			{
				VoxelizeCoverage(target, renderers, table, gi, regions);
				return;
			}

			voxelize ??= new ShaderProgram("Engine/Voxelize.shader");
			ShaderProgram program = PrepareTable(table, renderers, ref voxelizeInstanced, "Engine/Voxelize.shader", Array.Empty<string>()) ?? voxelize;

			CommandList commands = Gfx.Commands;
			commands.BeginRenderPass(currentResolution, currentResolution);
			commands.State.DepthTest = false;
			commands.State.Culling = FaceCulling.None;
			commands.State.DepthWrite = false;

			program.Use();
			program.SetVector3("voxelGridMin", gi.GridMin);
			program.SetFloat("voxelGridSize", gi.GridSize);
			program.SetInt("voxelResolution", currentResolution);
			target.BindImages(0, ImageAccess.Write, true);
			DrawRenderers(program, renderers, table);

			commands.State.DepthWrite = true;
			commands.State.DepthTest = true;
			commands.State.Culling = FaceCulling.Back;
			commands.EndRenderPass();
		}

		// Coverage voxelization: clear the accumulators where the renderers can be, rasterize them with multisampling
		// into the accumulators (block flags straight into the target), then resolve the accumulators into the target.
		private void VoxelizeCoverage(GeometryVolume target, IReadOnlyList<MeshRenderer> renderers, GIDrawTable table, GlobalIlluminationSettings gi, List<VoxelRegion> regions)
		{
			if (accumulators.AlbedoRG == null)
			{
				accumulators = AccumulatorVolume.Create(currentResolution);
			}
			if (clearAccumulators == null)
			{
				clearAccumulators = new ComputeShader("Engine/VoxelClearAccumulators.shader");
				resolveGeometry = new ComputeShader("Engine/VoxelResolveGeometry.shader");
			}

			// Conservative rasterization where the device has it (one fragment per touched column, the exact covered
			// area), else multisampling (the covered samples).
			bool conservative = ConservativeVoxelization(gi);
			ShaderProgram program = conservative
				? voxelizeConservative ??= new ShaderProgram("Engine/VoxelizeCoverage.shader", new[] { "LEL_VOXEL_CONSERVATIVE" })
				: voxelizeCoverage ??= new ShaderProgram("Engine/VoxelizeCoverage.shader");
			program = (conservative
				? PrepareTable(table, renderers, ref voxelizeConservativeInstanced, "Engine/VoxelizeCoverage.shader", new[] { "LEL_VOXEL_CONSERVATIVE" })
				: PrepareTable(table, renderers, ref voxelizeCoverageInstanced, "Engine/VoxelizeCoverage.shader", Array.Empty<string>())) ?? program;

			CommandList commands = Gfx.Commands;
			clearAccumulators.Use();
			accumulators.BindImages(0, ImageAccess.Write);
			DispatchRegions(clearAccumulators, regions);

			commands.BeginRenderPass(currentResolution, currentResolution, conservative ? 1 : coverageSamples);
			commands.State.DepthTest = false;
			commands.State.Culling = FaceCulling.None;
			commands.State.DepthWrite = false;
			commands.State.ConservativeRasterization = conservative;

			program.Use();
			program.SetVector3("voxelGridMin", gi.GridMin);
			program.SetFloat("voxelGridSize", gi.GridSize);
			program.SetInt("voxelResolution", currentResolution);
			program.SetInt("voxSamples", coverageSamples);
			program.SetInt("voxSplatWidth", (int)gi.CoverageSplat);
			accumulators.BindImages(0, ImageAccess.ReadWrite);
			commands.SetImage(5, target.Blocks, 0, -1, ImageAccess.Write);
			DrawRenderers(program, renderers, table);

			commands.State.ConservativeRasterization = false;
			commands.State.DepthWrite = true;
			commands.State.DepthTest = true;
			commands.State.Culling = FaceCulling.Back;
			commands.EndRenderPass();

			resolveGeometry.Use();
			target.BindImages(0, ImageAccess.Write, false);
			ShaderProgram resolve = resolveGeometry.Program;
			resolve.SetTexture("AccAlbedoRG", accumulators.AlbedoRG);
			resolve.SetTexture("AccAlbedoBW", accumulators.AlbedoBW);
			resolve.SetTexture("AccNormalXY", accumulators.NormalXY);
			resolve.SetTexture("AccNormalZ", accumulators.NormalZ);
			resolve.SetTexture("AccEmission", accumulators.Emission);
			resolve.SetFloat("occupancyScale", gi.CoverageOccupancyScale);
			DispatchRegions(resolveGeometry, regions);
		}

		// The coverage voxelization rasterizes conservatively: the setting asks and the device can.
		private static bool ConservativeVoxelization(GlobalIlluminationSettings gi)
		{
			return gi.ConservativeVoxelization && Gfx.Capabilities.ConservativeRasterization;
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

		// The renderers' opaque sub-meshes with the bound program: one multi-draw per material from the table when the
		// program is the table's instanced variant (PrepareTable built it), else one draw per sub-mesh.
		private void DrawRenderers(ShaderProgram program, IReadOnlyList<MeshRenderer> renderers, GIDrawTable table)
		{
			if (program.IsInstancedVariant)
			{
				for (int i = 0; i < tableMaterials.Count; i++)
				{
					// Materials describe themselves through conventional uniform names.
					GISurface.SetUniforms(program, tableMaterials[i]);
					table.Draw(i);
				}

				// No scene reference kept past the draw.
				tableMaterials.Clear();
				tableMaterialSlots.Clear();
				foreach (List<TablePart> parts in tableParts)
				{
					parts.Clear();
				}

				return;
			}

			foreach (MeshRenderer renderer in renderers)
			{
				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					Material material = renderer.GetMaterial(i);
					if (material == null || material.IsTransparent)
					{
						continue;
					}

					// Materials describe themselves through conventional uniform names.
					GISurface.SetUniforms(program, material);
					renderer.RenderSubMeshWith(program, i);
				}
			}
		}

		// The renderers' opaque sub-meshes (those DrawRenderers draws one by one) in the table, a batch per material in the
		// order the materials first come, uploaded; the voxelization program's instanced variant that draws them. Null
		// when they draw one by one: LEL_GI_GPU_DRAWS=0, no GPU-driven programs on the device, the variant not linked.
		private ShaderProgram PrepareTable(GIDrawTable table, IReadOnlyList<MeshRenderer> renderers, ref ShaderProgram instanced, string path, string[] defines)
		{
			if (!GIDrawTable.Enabled)
			{
				return null;
			}

			instanced ??= new ShaderProgram(path, defines.Append(ShaderProgram.InstancedDefine).ToArray());
			if (!instanced.IsLinked)
			{
				return null;
			}

			table.Clear();
			tableMaterials.Clear();
			tableMaterialSlots.Clear();
			foreach (MeshRenderer renderer in renderers)
			{
				int instance = -1;
				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					Material material = renderer.GetMaterial(i);
					if (material == null || material.IsTransparent)
					{
						continue;
					}

					if (instance < 0)
					{
						instance = table.AddInstance(renderer);
						if (instance < 0)
						{
							// No triangles: as GeometryPool.DrawSubMesh draws nothing for it.
							break;
						}
					}

					if (!tableMaterialSlots.TryGetValue(material, out int slot))
					{
						slot = tableMaterials.Count;
						tableMaterials.Add(material);
						tableMaterialSlots.Add(material, slot);
						if (tableParts.Count <= slot)
						{
							tableParts.Add(new List<TablePart>());
						}
					}

					tableParts[slot].Add(new TablePart { Instance = instance, Mesh = renderer.DrawnMesh, SubMesh = i });
				}
			}

			for (int slot = 0; slot < tableMaterials.Count; slot++)
			{
				foreach (TablePart part in tableParts[slot])
				{
					table.AddSubMesh(part.Instance, part.Mesh, part.SubMesh);
				}

				table.EndBatch();
			}

			table.Upload();
			return instanced;
		}

		// ---------------------------------------------------------------- lighting


		// Lists occupied blocks into the SSBO whose header is the indirect dispatch for injection.
		private void CompactOccupiedBlocks()
		{
			uint zero = 0;
			blockList.SetSubData(0, in zero);
			Gfx.Commands.SetStorageBuffer(0, blockList);

			compactBlocks.Use();
			compactBlocks.Program.SetInt("blockResolution", blockResolution);
			compactBlocks.Program.SetTexture("StaticBlocks", staticGeometry.Blocks);
			compactBlocks.Program.SetTexture("DynamicBlocks", dynamicGeometry.Blocks);
			compactBlocks.DispatchThreads(blockResolution, blockResolution, blockResolution, 4, 4, 4);
		}

		// The bounce of the next injection pass per 2x2x2 voxels, traced from radiance[current] (the previous pass).
		private void TraceBounceCells(GlobalIlluminationSettings gi)
		{
			ComputeShader shader = Variant(bounce, "Engine/VoxelBounceCells.shader");
			shader.Use();
			ShaderProgram program = shader.Program;
			SetTraceUniforms(program, gi);
			program.SetTexture("StaticAlbedo", staticGeometry.Albedo);
			program.SetTexture("StaticNormal", staticGeometry.Normal);
			program.SetTexture("DynamicAlbedo", dynamicGeometry.Albedo);
			program.SetTexture("DynamicNormal", dynamicGeometry.Normal);
			CommandList commands = Gfx.Commands;
			commands.SetImage(0, bounceCells, 0, -1, ImageAccess.Write);

			commands.SetStorageBuffer(0, blockList);
			shader.DispatchIndirect(blockList, 0);
		}

		// The program of a set of variants for the volume's mips, tracing the scene's triangles (ray queries) or the
		// distance field (made when first asked for).
		private ComputeShader Variant(ComputeShader[] variants, string path)
		{
			int index = (int)mipMode + (rayScene != null ? 3 : 0);
			return variants[index] ??= rayScene != null
				? new ComputeShader(path, MipModeDefines[(int)mipMode], ShaderProgram.RayQueryDefine)
				: new ComputeShader(path, MipModeDefines[(int)mipMode]);
		}

		// Says once how the cones see the scene's geometry, and again when that changes (the editor's settings).
		private void ReportCones(GlobalIlluminationSettings gi)
		{
			string cones;
			if (gi.TraceMode == GITraceMode.VoxelCones)
			{
				cones = "voxel cones (occlusion from the voxels)";
			}
			else if (rayScene != null)
			{
				int rays = Math.Clamp(Lighting.RayTracing.ConeRays, 1, RayTracingSettings.MaxConeRays);
				string materials = Lighting.MaterialConeRays ? "the materials' own cones too" : "the materials' own cones are voxel cones";
				string distance = Lighting.ConeHitPlane ? "the distance to the axis hit's plane" : "the distance along the axis";
				cones = gi.TraceMode == GITraceMode.SdfDetail
					? $"SDF detail through the scene's triangles (a ray query for every cone's near field; no distance fields; {materials})"
					: $"SDF cones through the scene's triangles ({rays} ray quer{(rays == 1 ? "y" : "ies")} a cone, {distance}; no distance fields; {materials})";
			}
			else
			{
				string why = !Lighting.HardwareRayTracing ? (gi.HardwareRayTracing ? "the graphics device has no ray queries" : "Hardware Ray Tracing is off") : "";
				cones = (gi.TraceMode == GITraceMode.SdfDetail ? "SDF detail through the distance fields" : "SDF cones through the distance fields") + (why.Length > 0 ? " (" + why + ")" : "");
			}

			if (cones != reportedCones)
			{
				reportedCones = cones;
				Console.WriteLine("[VoxelGI] Cones: " + cones);
			}
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
			SetRadianceUniforms(program, current);
			// The bounce traces like the materials: with SDF cones walls stop it, voxel cones would bring the light
			// from outside in through them. The SDF detail trace is not used here (binary, too sharp per voxel).
			if (rayScene != null)
			{
				// SDF cones through the scene's triangles (the programs are the ray query variants).
				Lighting.SetVoxelRayTracingUniforms(program, rayScene, gi.TraceMode == GITraceMode.SdfCones ? 2 : 0);
			}
			else
			{
				bool sdf = gi.GlobalSdf != null;
				int traceMode = Lighting.TraceModeUniform(sdf) == 2 ? 2 : 0;
				program.SetInt("giTraceMode", traceMode);
				if (traceMode == 2)
				{
					Lighting.SetSdfUniforms(program);
				}
			}

			Lighting.SetSkyUniforms(program);
			// The clouds, taken in with one cone per cell (ApplyCloudsToGather): surfaces lit by a cloud pass its light on.
			Lighting.SetMediaUniforms(program);
		}

		// One injection pass into radiance[target]; with bounce it lights from radiance[current] (the previous pass),
		// through the bounce cells or by tracing from every voxel.
		private void InjectLighting(int target, GlobalIlluminationSettings gi, bool bounce, bool cells)
		{
			ComputeShader shader = Variant(inject, "Engine/VoxelInject.shader");
			ShaderProgram program = shader.Program;

			shader.Use();

			// The lights' lists; shadows come from the grid-covering maps, not the camera's.
			Lighting.SetUniforms(program, false, false);
			gridShadow.SetUniforms(program);
			program.SetInt("injectShadowTaps", gi.SoftVoxelShadows ? 4 : 1);

			SetTraceUniforms(program, gi);
			program.SetFloat("giBounceStrength", bounce ? gi.BounceStrength : 0f);
			program.SetInt("giBounceCones", (int)gi.BounceConeLayout);
			program.SetInt("giBounceCells", cells ? 1 : 0);
			program.SetTexture("VoxelBounceCells", bounceCells);

			staticGeometry.BindImages(0, ImageAccess.Read, false);
			dynamicGeometry.BindImages(3, ImageAccess.Read, false);
			CommandList commands = Gfx.Commands;
			commands.SetImage(6, radiance[target], 0, -1, ImageAccess.Write);

			// One work group per occupied block, count taken from the compaction result.
			commands.SetStorageBuffer(0, blockList);
			shader.DispatchIndirect(blockList, 0);
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
		private void UpdateIsotropicMips(GpuTexture texture)
		{
			if (mipLevels > 1)
			{
				CommandList commands = Gfx.Commands;
				mipBlocks.Use();
				commands.SetImage(0, texture, 0, -1, ImageAccess.Read);
				commands.SetImage(1, texture, 1, -1, ImageAccess.Write);
				commands.SetStorageBuffer(0, blockList);
				mipBlocks.DispatchIndirect(blockList, 0);
			}

			BoxMips(texture, currentResolution, 2, mipLevels);
		}

		// Levels [firstLevel, levels) of a texture whose level 0 is baseSize^3, each the 2x2x2 average of the one below.
		private void BoxMips(GpuTexture texture, int baseSize, int firstLevel, int levels)
		{
			CommandList commands = Gfx.Commands;
			mip.Use();
			for (int level = firstLevel; level < levels; level++)
			{
				int levelSize = Math.Max(1, baseSize >> level);
				commands.SetImage(0, texture, level - 1, -1, ImageAccess.Read);
				commands.SetImage(1, texture, level, -1, ImageAccess.Write);
				mip.Program.SetInt3("dstOffset", 0, 0, 0);
				mip.Program.SetInt3("dstSize", levelSize, levelSize, levelSize);
				mip.DispatchThreads(levelSize, levelSize, levelSize, 4, 4, 4);
			}
		}

		// SH L1 gradients: level 0 (the volume's mip 1) from the voxels of occupied blocks and their normals, then plain
		// averages (the gradients are linear in the voxels).
		private void UpdateSHMips(int index)
		{
			shBase ??= new ComputeShader("Engine/VoxelSHBase.shader");
			shBase.Use();
			ShaderProgram program = shBase.Program;
			program.SetTexture("StaticAlbedo", staticGeometry.Albedo);
			program.SetTexture("StaticNormal", staticGeometry.Normal);
			program.SetTexture("DynamicAlbedo", dynamicGeometry.Albedo);
			program.SetTexture("DynamicNormal", dynamicGeometry.Normal);
			CommandList commands = Gfx.Commands;
			commands.SetImage(0, radiance[index], 0, -1, ImageAccess.Read);
			for (int axis = 0; axis < 3; axis++)
			{
				commands.SetImage(1 + axis, shGradients[index][axis], 0, -1, ImageAccess.Write);
			}
			commands.SetStorageBuffer(0, blockList);
			shBase.DispatchIndirect(blockList, 0);

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
			CommandList commands = Gfx.Commands;
			commands.SetImage(0, radiance[index], 0, -1, ImageAccess.Read);
			commands.SetImage(1, anisoRadiance[index], 0, -1, ImageAccess.Write);
			commands.SetStorageBuffer(0, blockList);
			anisoBase.DispatchIndirect(blockList, 0);

			anisoMip.Use();
			for (int level = 1; level < levels; level++)
			{
				int slabSize = anisoSlabSize >> level;
				commands.SetImage(0, anisoRadiance[index], level - 1, -1, ImageAccess.Read);
				commands.SetImage(1, anisoRadiance[index], level, -1, ImageAccess.Write);
				anisoMip.Program.SetInt("slabSize", slabSize);
				anisoMip.DispatchThreads(slabSize * 6, slabSize, slabSize, 4, 4, 4);
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>A sub-mesh of a table's instance (its drawn mesh) waiting for its material's batch.</summary>
		private struct TablePart
		{
			public int Instance;
			public Mesh Mesh;
			public int SubMesh;
		}

		/// <summary>Surface attributes of voxelized geometry (albedo + occupancy, normal, emission) plus block flags.</summary>
		private struct GeometryVolume
		{
			public GpuTexture Albedo;
			public GpuTexture Normal;
			public GpuTexture Emissive;

			/// <summary>R8 volume at 1/8 resolution: 1 where the 8^3 block contains any geometry.</summary>
			public GpuTexture Blocks;

			public static GeometryVolume Create(int resolution, int blockResolution)
			{
				// Albedo and normal are also fetched as textures (bounce cells, SH gradients), the block flags by the compaction.
				return new GeometryVolume
				{
					Albedo = CreateVolumeTexture(resolution, TextureFormat.RGBA8, TextureUsage.Sampled | TextureUsage.Storage),
					Normal = CreateVolumeTexture(resolution, TextureFormat.RGBA8, TextureUsage.Sampled | TextureUsage.Storage),
					Emissive = CreateVolumeTexture(resolution, TextureFormat.R11G11B10F, TextureUsage.Storage),
					Blocks = CreateVolumeTexture(blockResolution, TextureFormat.R8, TextureUsage.Sampled | TextureUsage.Storage)
				};
			}

			/// <summary>Binds albedo, normal, emissive (and optionally the block flags) as images from binding upwards.</summary>
			public void BindImages(int binding, ImageAccess access, bool includeBlocks)
			{
				CommandList commands = Gfx.Commands;
				commands.SetImage(binding, Albedo, 0, -1, access);
				commands.SetImage(binding + 1, Normal, 0, -1, access);
				commands.SetImage(binding + 2, Emissive, 0, -1, access);
				if (includeBlocks)
				{
					commands.SetImage(binding + 3, Blocks, 0, -1, access);
				}
			}

			public void Delete()
			{
				Albedo?.Dispose();
				Normal?.Dispose();
				Emissive?.Dispose();
				Blocks?.Dispose();
				Albedo = Normal = Emissive = Blocks = null;
			}
		}

		/// <summary>
		///     Integer accumulators of the coverage voxelization (Engine/VoxelizeCoverage.shader): weighted sums of albedo
		///     (r|g, b|weight as 16-bit fixed point), normal (x|y, z) and emission (RGB9E5). Shared by the static and the
		///     dynamic set, which are voxelized one after the other.
		/// </summary>
		private struct AccumulatorVolume
		{
			public GpuTexture AlbedoRG;
			public GpuTexture AlbedoBW;
			public GpuTexture NormalXY;
			public GpuTexture NormalZ;
			public GpuTexture Emission;

			public static AccumulatorVolume Create(int resolution)
			{
				// Written as images by the voxelization, fetched as textures by the resolve.
				const TextureUsage usage = TextureUsage.Sampled | TextureUsage.Storage;
				return new AccumulatorVolume
				{
					AlbedoRG = CreateVolumeTexture(resolution, TextureFormat.R32UI, usage),
					AlbedoBW = CreateVolumeTexture(resolution, TextureFormat.R32UI, usage),
					NormalXY = CreateVolumeTexture(resolution, TextureFormat.R32UI, usage),
					NormalZ = CreateVolumeTexture(resolution, TextureFormat.R32UI, usage),
					Emission = CreateVolumeTexture(resolution, TextureFormat.R32UI, usage)
				};
			}

			public void BindImages(int binding, ImageAccess access)
			{
				CommandList commands = Gfx.Commands;
				commands.SetImage(binding, AlbedoRG, 0, -1, access);
				commands.SetImage(binding + 1, AlbedoBW, 0, -1, access);
				commands.SetImage(binding + 2, NormalXY, 0, -1, access);
				commands.SetImage(binding + 3, NormalZ, 0, -1, access);
				commands.SetImage(binding + 4, Emission, 0, -1, access);
			}

			public void Delete()
			{
				AlbedoRG?.Dispose();
				AlbedoBW?.Dispose();
				NormalXY?.Dispose();
				NormalZ?.Dispose();
				Emission?.Dispose();
				AlbedoRG = AlbedoBW = NormalXY = NormalZ = Emission = null;
			}
		}

		private static GpuTexture CreateVolumeTexture(int resolution, TextureFormat format, TextureUsage usage)
		{
			return CreateTexture3D(resolution, resolution, resolution, format, usage);
		}

		// Accessed through image load/store or texelFetch only; no filtering, no mips.
		private static GpuTexture CreateTexture3D(int width, int height, int depth, TextureFormat format, TextureUsage usage)
		{
			GpuTexture texture = Gfx.Device.CreateTexture(TextureDescriptor.Texture3D(format, width, height, depth, 1, usage, "Voxel GI volume"));
			texture.Sampler = Samplers.PointRepeat;
			return texture;
		}

		#endregion
	}
}
