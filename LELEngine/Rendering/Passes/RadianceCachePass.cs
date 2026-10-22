using System;
using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Lumen world-space radiance cache: a grid of probes over the GI volume, each a full-sphere octahedral
	///     radiance map (with a border for seamless filtering) stored in a 2D atlas. Probes are traced through the
	///     global distance field at a higher resolution than stored (Lumen: 32x32 rays into 16x16 texels), the
	///     rays are averaged into the stored map and accumulated over time. Screen probe rays and radiosity rays
	///     read the cache beyond <see cref="GlobalIlluminationSettings.RadianceCacheNearDistance" />, which makes
	///     the far field temporally stable while the near field keeps per-ray detail.
	///
	///     Like Lumen, the update budget goes to the probes that matter: probes inside the static scene bounds
	///     ("interior") are refreshed round robin from a per-frame budget, the rest get an eighth of it.
	///
	///     Every updated probe's map is also projected to second-order spherical harmonics (Engine/RadianceCacheSH.shader):
	///     the GI of surfaces outside the depth buffer (transparent materials) interpolates those, the way Lumen lights
	///     translucency from a volume of probes. The cache is published through <see cref="Lighting.RadianceCache" />,
	///     and runs for the transparent renderers even when the setting is off (<see cref="Running" /> vs <see cref="Active" />).
	/// </summary>
	public sealed class RadianceCachePass : RenderPass
	{
		#region PublicFields

		public override string Name => "RadianceCache";

		public override RenderPassScope Scope => RenderPassScope.Frame;

		public int Atlas => atlas;

		/// <summary>
		///     The cache is updated this frame: the setting is on, or the frame has transparent renderers, whose GI is
		///     interpolated from these probes (Lumen has nothing else to light them from).
		/// </summary>
		public bool Running { get; private set; }

		/// <summary>
		///     The opaque pipeline reads the cache (the setting is on): screen probe and radiosity rays take the far field
		///     from it. Off while the cache only runs for the transparent renderers, so disabling the setting leaves the
		///     opaque image exactly as it was.
		/// </summary>
		public bool Active { get; private set; }

		/// <summary>True while the scene is quiet and the cache has converged: the probe budget is reduced.</summary>
		public bool Idle { get; private set; }
		public int InteriorProbeCount => interiorTotal;

		#endregion

		#region PrivateFields

		private const int ProbeListBinding = 3;
		private const int ProbeFramesBinding = 4;
		private const int TraceGroupSize = 16;

		// Probes updated first each frame because the lighting around them changed (LumenScene.ChangeRegions): stored
		// after the interior and exterior lists in the probe list buffer.
		private const int PriorityCapacity = 256;

		private const int SHLayers = 7;

		private ComputeShader trace;
		private ComputeShader resolve;
		private ComputeShader sh;

		private int atlas;
		private int accumulator; // fp32 copy of the atlas that the resolve accumulates into (see RadianceCacheResolve.shader)
		private int shTexture; // per-probe SH, one texel per probe tile (RadianceCacheSH.shader)
		private int atlasWidth;
		private int atlasHeight;
		private int probesPerAxis;
		private int probeResolution;
		private int probesPerRow;
		private int probeRows;
		private int probeCount;

		private int traceBuffer;
		private int traceSlots;
		private int traceSlotsPerRow;
		private int traceResolution;

		private int frameIndex;
		private Vector3 gridMin;
		private float probeSpacing;

		private int probeListBuffer;
		private int probeFramesBuffer;
		private int interiorTotal;
		private int exteriorTotal;
		private long tracedSinceChange;
		private int interiorNext;
		private int exteriorNext;
		private Vector3 listBoundsMin;
		private Vector3 listBoundsMax;
		private bool listValid;

		private int[] listPositions = new int[0]; // probe index -> its position in the interior + exterior list
		private readonly int[] priorityProbes = new int[PriorityCapacity];
		private readonly List<(float Distance, int Probe)> priorityCandidates = new List<(float Distance, int Probe)>();
		private readonly HashSet<int> priorityTaken = new HashSet<int>();
		private int priorityCount;

		// Frames since a dynamic object last moved: probes keep moving cells for two of their updates, so once this is
		// longer than two update periods of the slowest probes none is left and the lookups skip reading them.
		private int framesSinceMove = int.MaxValue / 2;
		private int movingCellsLifetime;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			LumenScene scene = context.Renderer.LumenScene;
			bool possible = gi.LumenActive && gi.GlobalSdf != 0 && scene.SurfaceCache != null;
			bool transparents = context.TransparentRenderers != null && context.TransparentRenderers.Count > 0;
			Running = possible && (gi.RadianceCacheEnabled || transparents);
			Active = Running && gi.RadianceCacheEnabled;
			if (!Running)
			{
				Idle = false;
				tracedSinceChange = 0;
				Lighting.RadianceCache = default;
				// Made when the cache first runs, freed when Lumen does not run.
				if (!gi.LumenActive)
				{
					Release();
				}
				return;
			}

			if (trace == null)
			{
				trace = new ComputeShader("Engine/RadianceCacheTrace.shader");
				resolve = new ComputeShader("Engine/RadianceCacheResolve.shader");
				sh = new ComputeShader("Engine/RadianceCacheSH.shader");
				probeListBuffer = GL.GenBuffer();
				probeFramesBuffer = GL.GenBuffer();
			}

			int perAxis = Math.Max(2, gi.RadianceCacheProbesPerAxis);
			int resolution = gi.RadianceCacheProbeResolution >= 16 ? 16 : 8;
			int traceRes = Math.Max(TraceGroupSize, gi.RadianceCacheTraceResolution / TraceGroupSize * TraceGroupSize);
			if (traceRes % resolution != 0)
			{
				traceRes = resolution * 2;
			}
			EnsureAtlas(perAxis, resolution);

			float spacing = gi.GridSize / (perAxis - 1);
			if (gi.GridMin != gridMin || spacing != probeSpacing)
			{
				gridMin = gi.GridMin;
				probeSpacing = spacing;
				ClearAtlas();
				listValid = false;
			}

			// The interior / exterior split follows the static scene bounds, padded by one probe spacing.
			Vector3 boundsMin = scene.HasStaticBounds ? scene.StaticBoundsMin - new Vector3(probeSpacing) : gridMin;
			Vector3 boundsMax = scene.HasStaticBounds ? scene.StaticBoundsMax + new Vector3(probeSpacing) : gridMin + new Vector3(gi.GridSize);
			if (!listValid || boundsMin != listBoundsMin || boundsMax != listBoundsMax)
			{
				BuildProbeList(boundsMin, boundsMax);
			}

			frameIndex++;

			// Change-driven budget: once the scene (object placement, materials, sun) has been quiet for a while and
			// the grid has been swept enough times at the full budget to converge, only a trickle of updates is
			// needed; any change restores the full budget at once.
			if (!scene.IsQuiet(1))
			{
				tracedSinceChange = 0;
			}
			long converged = (long)Math.Max(1, gi.RadianceCacheIdleAfterSweeps) * (interiorTotal + exteriorTotal);
			Idle = scene.IsQuiet(Math.Max(1, gi.RadianceCacheIdleAfterFrames)) && tracedSinceChange >= converged;
			int budget = Math.Max(1, Idle ? gi.RadianceCacheProbesPerFrame / Math.Max(1, gi.RadianceCacheIdleDivisor) : gi.RadianceCacheProbesPerFrame);
			int interiorCount = Math.Min(budget, interiorTotal);
			int exteriorCount = Math.Min(Math.Max(1, budget / 8), exteriorTotal);
			priorityCount = BuildPriority(scene, gi, interiorCount, exteriorCount);
			int count = priorityCount + interiorCount + exteriorCount;

			framesSinceMove = scene.DynamicChanged ? 0 : Math.Min(framesSinceMove + 1, int.MaxValue / 2);
			int interiorPeriod = interiorCount > 0 ? (interiorTotal + interiorCount - 1) / interiorCount : 0;
			int exteriorPeriod = exteriorCount > 0 ? (exteriorTotal + exteriorCount - 1) / exteriorCount : 0;
			movingCellsLifetime = 2 * Math.Max(interiorPeriod, exteriorPeriod) + 4;
			tracedSinceChange += count;
			Publish();
			if (count == 0)
			{
				return;
			}
			EnsureTraceBuffer(count, traceRes);

			GpuProfiler profiler = context.Renderer.Profiler;
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, ProbeListBinding, probeListBuffer);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, ProbeFramesBinding, probeFramesBuffer);

			// ---- 1. trace: one thread per ray into the scratch tiles
			profiler.Split("RadianceCache.trace");
			ShaderProgram traceProgram = trace.Program;
			trace.Use();
			scene.SetUniforms(traceProgram);
			Lighting.SetSdfUniforms(traceProgram);
			BindCache(traceProgram, 7);
			SetScheduleUniforms(traceProgram, interiorCount);
			traceProgram.SetTexture("FinalLighting", TextureTarget.Texture2D, scene.SurfaceCache.FinalLightingCurrent, 2);
			Lighting.SetSkyUniforms(traceProgram);
			traceProgram.SetInt("frameIndex", frameIndex);
			GL.BindImageTexture(0, traceBuffer, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			int groupsPerProbe = traceRes / TraceGroupSize * (traceRes / TraceGroupSize);
			trace.Dispatch(count * groupsPerProbe);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			// ---- 2. resolve: downsample into the stored maps and accumulate
			profiler.Split("RadianceCache.resolve");
			ShaderProgram resolveProgram = resolve.Program;
			resolve.Use();
			BindCache(resolveProgram, 7);
			SetScheduleUniforms(resolveProgram, interiorCount);
			resolveProgram.SetTexture("TraceBuffer", TextureTarget.Texture2D, traceBuffer, 1);
			resolveProgram.SetInt("frameIndex", frameIndex);
			resolveProgram.SetFloat("historyFrames", gi.RadianceCacheHistoryFrames);
			// Short history until the grid has been swept enough times since the last change for the multi-bounce
			// chain (cache -> cards -> cache) to settle; the long history would average that transient in and
			// then take thousands of frames to forget it.
			bool settled = tracedSinceChange >= converged;
			resolveProgram.SetFloat("maxHistorySamples", settled ? gi.RadianceCacheMaxHistorySamples : Math.Min(gi.RadianceCacheMaxHistorySamples, gi.RadianceCacheMovingHistorySamples));
			resolveProgram.SetFloat("changeThreshold", gi.RadianceCacheChangeThreshold);
			resolveProgram.SetInt("rcProbeCount", probeCount);
			GL.BindImageTexture(0, atlas, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, accumulator, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba32f);
			resolve.Dispatch(count);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

			// ---- 3. spherical harmonics of the updated probes (the volume lighting of transparent surfaces)
			profiler.Split("RadianceCache.sh");
			ShaderProgram shProgram = sh.Program;
			sh.Use();
			BindCache(shProgram, 7);
			SetScheduleUniforms(shProgram, interiorCount);
			GL.BindImageTexture(0, shTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			sh.Dispatch(count);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			if (interiorTotal > 0)
			{
				interiorNext = (interiorNext + interiorCount) % interiorTotal;
			}
			if (exteriorTotal > 0)
			{
				exteriorNext = (exteriorNext + exteriorCount) % exteriorTotal;
			}
		}

		/// <summary>
		///     Binds the cache for the opaque pipeline's Engine/RadianceCache.glsl consumers (shaders that define
		///     RC_MOVING_MASK also read the probes' moving cells from storage binding 4). When the cache is not
		///     <see cref="Active" /> the near distance is set to 0 and the shaders trace the whole distance field instead.
		/// </summary>
		public void SetUniforms(ShaderProgram program, int textureUnit)
		{
			if (!Active || atlas == 0)
			{
				program.SetFloat("rcNearDistance", 0f);
				return;
			}

			BindCache(program, textureUnit);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, ProbeFramesBinding, probeFramesBuffer);
		}

		#endregion

		#region PrivateMethods

		/// <summary>The atlas and the probe layout for this pass's own shaders and the opaque consumers (whenever the cache runs).</summary>
		private void BindCache(ShaderProgram program, int textureUnit)
		{
			program.SetTexture("RadianceCacheAtlas", TextureTarget.Texture2D, atlas, textureUnit);
			program.SetVector3("rcGridMin", gridMin);
			program.SetFloat("rcProbeSpacing", probeSpacing);
			program.SetInt("rcProbesPerAxis", probesPerAxis);
			program.SetInt("rcProbesPerRow", probesPerRow);
			program.SetInt("rcProbeResolution", probeResolution);
			program.SetVector2("rcAtlasSize", new Vector2(atlasWidth, atlasHeight));
			program.SetFloat("rcNearDistance", Lighting.GI.RadianceCacheNearDistance);
			program.SetInt("rcFarShortcut", Lighting.GI.RadianceCacheFarShortcut ? 1 : 0);
			program.SetInt("rcProbeCount", probeCount);
			program.SetInt("rcMovingRecent", framesSinceMove <= movingCellsLifetime ? 1 : 0);
		}

		#endregion

		#region PublicMethods

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

		private void Release()
		{
			if (Lighting.RadianceCache.Atlas != 0 && Lighting.RadianceCache.Atlas == atlas)
			{
				Lighting.RadianceCache = default;
			}
			trace?.Delete();
			resolve?.Delete();
			sh?.Delete();
			trace = null;
			resolve = null;
			sh = null;
			if (atlas != 0)
			{
				GL.DeleteTexture(atlas);
				atlas = 0;
			}
			if (accumulator != 0)
			{
				GL.DeleteTexture(accumulator);
				accumulator = 0;
			}
			if (shTexture != 0)
			{
				GL.DeleteTexture(shTexture);
				shTexture = 0;
			}
			if (traceBuffer != 0)
			{
				GL.DeleteTexture(traceBuffer);
				traceBuffer = 0;
			}
			if (probeListBuffer != 0)
			{
				GL.DeleteBuffer(probeListBuffer);
				probeListBuffer = 0;
			}
			if (probeFramesBuffer != 0)
			{
				GL.DeleteBuffer(probeFramesBuffer);
				probeFramesBuffer = 0;
			}
			// The next run starts like the first: atlas, trace buffer and probe lists rebuilt.
			probesPerAxis = 0;
			traceSlots = 0;
			probeSpacing = 0f;
			listValid = false;
		}

		/// <summary>This frame's cache for the material shaders' volume lighting (<see cref="Lighting.RadianceCache" />).</summary>
		private void Publish()
		{
			Lighting.RadianceCache = new Lighting.RadianceCacheBinding
			{
				Valid = true,
				Atlas = atlas,
				SH = shTexture,
				GridMin = gridMin,
				ProbeSpacing = probeSpacing,
				ProbesPerAxis = probesPerAxis,
				ProbesPerRow = probesPerRow,
				ProbeResolution = probeResolution,
				AtlasSize = new Vector2(atlasWidth, atlasHeight)
			};
		}

		private void SetScheduleUniforms(ShaderProgram program, int interiorCount)
		{
			program.SetInt("rcTraceResolution", traceResolution);
			program.SetInt("traceSlotsPerRow", traceSlotsPerRow);
			program.SetInt("sequenceOffset", 0);
			program.SetInt("priorityCount", priorityCount);
			program.SetInt("priorityOffset", probeCount);
			program.SetInt("interiorStart", interiorNext);
			program.SetInt("interiorCount", interiorCount);
			program.SetInt("interiorTotal", Math.Max(1, interiorTotal));
			program.SetInt("exteriorStart", exteriorNext);
			program.SetInt("exteriorTotal", Math.Max(1, exteriorTotal));
		}

		private void EnsureAtlas(int perAxis, int resolution)
		{
			if (atlas != 0 && perAxis == probesPerAxis && resolution == probeResolution)
			{
				return;
			}

			if (atlas != 0)
			{
				GL.DeleteTexture(atlas);
			}
			if (accumulator != 0)
			{
				GL.DeleteTexture(accumulator);
			}
			if (shTexture != 0)
			{
				GL.DeleteTexture(shTexture);
			}

			probesPerAxis = perAxis;
			probeResolution = resolution;
			probeCount = perAxis * perAxis * perAxis;
			probesPerRow = (int)Math.Ceiling(Math.Sqrt(probeCount));
			int rows = (probeCount + probesPerRow - 1) / probesPerRow;
			probeRows = rows;
			int tile = resolution + 2;
			atlasWidth = probesPerRow * tile;
			atlasHeight = rows * tile;

			atlas = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, atlas);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, atlasWidth, atlasHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);

			accumulator = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, accumulator);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, atlasWidth, atlasHeight, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);

			// 27 SH coefficient values + valid flag per probe in 7 RGBA layers, laid out like the atlas tiles.
			shTexture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2DArray, shTexture);
			GL.TexImage3D(TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba16f, probesPerRow, rows, SHLayers, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture2DArray, 0);
			ClearAtlas();

			listValid = false;
			Console.WriteLine($"[RadianceCache] {perAxis}^3 probes at {resolution}x{resolution}, atlas {atlasWidth}x{atlasHeight}");
		}

		// Alpha 0 marks "no data": consumers trace on until a probe has been resolved (no glClearTexImage in GL 4.3).
		// Every probe's last-update frame is reset to -1 and its update count to 0 so the first update after the
		// clear replaces the history.
		private void ClearAtlas()
		{
			if (GLCapabilities.ClearTexture)
			{
				GL.ClearTexImage(atlas, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
				GL.ClearTexImage(accumulator, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
				GL.ClearTexImage(shTexture, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			}
			else
			{
				float[] zeros = new float[atlasWidth * atlasHeight * 4];
				foreach (int texture in new[] { atlas, accumulator })
				{
					GL.BindTexture(TextureTarget.Texture2D, texture);
					GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, atlasWidth, atlasHeight, PixelFormat.Rgba, PixelType.Float, zeros);
				}
				GL.BindTexture(TextureTarget.Texture2D, 0);
				// The SH valid flags (layer 6 alpha) go to 0 with the rest.
				GL.BindTexture(TextureTarget.Texture2DArray, shTexture);
				GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, 0, probesPerRow, probeRows, SHLayers, PixelFormat.Rgba, PixelType.Float, zeros);
				GL.BindTexture(TextureTarget.Texture2DArray, 0);
			}

			int count = Math.Max(1, probeCount);
			int[] frames = new int[count * 3]; // [probe] last frame, [count + probe] update count, [2 count + probe] moving cells
			for (int i = 0; i < count; i++)
			{
				frames[i] = -1;
			}
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, probeFramesBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, frames.Length * sizeof(int), frames, BufferUsageHint.DynamicDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

			interiorNext = 0;
			exteriorNext = 0;
			tracedSinceChange = 0;
		}

		private void EnsureTraceBuffer(int slots, int resolution)
		{
			if (traceBuffer != 0 && slots <= traceSlots && resolution == traceResolution)
			{
				return;
			}

			if (traceBuffer != 0)
			{
				GL.DeleteTexture(traceBuffer);
			}

			traceSlots = Math.Max(slots, traceSlots);
			traceResolution = resolution;
			traceSlotsPerRow = (int)Math.Ceiling(Math.Sqrt(traceSlots));
			int rows = (traceSlots + traceSlotsPerRow - 1) / traceSlotsPerRow;

			traceBuffer = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, traceBuffer);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, traceSlotsPerRow * resolution, rows * resolution, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			Console.WriteLine($"[RadianceCache] trace buffer {traceSlots} probes x {resolution}x{resolution} rays");
		}

		/// <summary>
		///     The probes around this frame's change regions (LumenScene.ChangeRegions, padded by
		///     <see cref="GlobalIlluminationSettings.RadianceCacheChangeRadius" /> probe spacings), nearest first, up to
		///     <see cref="GlobalIlluminationSettings.RadianceCacheChangeProbesPerFrame" />, minus those the round robin updates
		///     this frame anyway (a probe traced twice in one frame would have two work groups writing its tile). Uploaded after
		///     the interior and exterior lists; the shaders take them first (SequenceProbe).
		/// </summary>
		private int BuildPriority(LumenScene scene, GlobalIlluminationSettings gi, int interiorCount, int exteriorCount)
		{
			IReadOnlyList<LumenScene.ChangeRegion> regions = scene.ChangeRegions;
			int budget = Math.Min(PriorityCapacity, gi.RadianceCacheChangeProbesPerFrame);
			if (regions.Count == 0 || budget <= 0 || listPositions.Length < probeCount)
			{
				return 0;
			}

			priorityCandidates.Clear();
			priorityTaken.Clear();
			float margin = Math.Max(0f, gi.RadianceCacheChangeRadius) * probeSpacing;
			foreach (LumenScene.ChangeRegion region in regions)
			{
				Vector3 lo = (region.Min - new Vector3(margin) - gridMin) / probeSpacing;
				Vector3 hi = (region.Max + new Vector3(margin) - gridMin) / probeSpacing;
				int x0 = Math.Max(0, (int)Math.Ceiling(lo.X)), x1 = Math.Min(probesPerAxis - 1, (int)Math.Floor(hi.X));
				int y0 = Math.Max(0, (int)Math.Ceiling(lo.Y)), y1 = Math.Min(probesPerAxis - 1, (int)Math.Floor(hi.Y));
				int z0 = Math.Max(0, (int)Math.Ceiling(lo.Z)), z1 = Math.Min(probesPerAxis - 1, (int)Math.Floor(hi.Z));
				Vector3 center = region.Center;
				for (int z = z0; z <= z1; z++)
				{
					for (int y = y0; y <= y1; y++)
					{
						for (int x = x0; x <= x1; x++)
						{
							int probe = x + (y + z * probesPerAxis) * probesPerAxis;
							Vector3 position = gridMin + new Vector3(x, y, z) * probeSpacing;
							priorityCandidates.Add(((position - center).LengthSquared, probe));
						}
					}
				}
			}

			priorityCandidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			int count = 0;
			foreach ((float _, int probe) in priorityCandidates)
			{
				if (count >= budget)
				{
					break;
				}

				if (!priorityTaken.Add(probe) || InRoundRobin(listPositions[probe], interiorCount, exteriorCount))
				{
					continue;
				}

				priorityProbes[count++] = probe;
			}

			if (count > 0)
			{
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, probeListBuffer);
				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)(probeCount * sizeof(int)), count * sizeof(int), priorityProbes);
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			}

			return count;
		}

		/// <summary>The probe at this list position is in this frame's round-robin window.</summary>
		private bool InRoundRobin(int position, int interiorCount, int exteriorCount)
		{
			if (position < interiorTotal)
			{
				int offset = (position - interiorNext + interiorTotal) % Math.Max(1, interiorTotal);
				return offset < interiorCount;
			}

			int exteriorOffset = (position - interiorTotal - exteriorNext + exteriorTotal) % Math.Max(1, exteriorTotal);
			return exteriorOffset < exteriorCount;
		}

		// Splits the probes into those inside the (padded) static scene bounds and the rest, uploaded as one
		// index list: interior first, exterior after.
		private void BuildProbeList(Vector3 boundsMin, Vector3 boundsMax)
		{
			List<int> interior = new List<int>();
			List<int> exterior = new List<int>();
			for (int index = 0; index < probeCount; index++)
			{
				int x = index % probesPerAxis;
				int y = (index / probesPerAxis) % probesPerAxis;
				int z = index / (probesPerAxis * probesPerAxis);
				Vector3 position = gridMin + new Vector3(x, y, z) * probeSpacing;
				bool inside = position.X >= boundsMin.X && position.Y >= boundsMin.Y && position.Z >= boundsMin.Z
					&& position.X <= boundsMax.X && position.Y <= boundsMax.Y && position.Z <= boundsMax.Z;
				(inside ? interior : exterior).Add(index);
			}

			interiorTotal = interior.Count;
			exteriorTotal = exterior.Count;
			interiorNext = 0;
			exteriorNext = 0;
			tracedSinceChange = 0;

			// The interior list, the exterior list, then room for the frame's priority probes.
			int[] list = new int[Math.Max(1, probeCount) + PriorityCapacity];
			interior.CopyTo(list, 0);
			exterior.CopyTo(list, interiorTotal);
			listPositions = new int[Math.Max(1, probeCount)];
			for (int i = 0; i < interiorTotal + exteriorTotal; i++)
			{
				listPositions[list[i]] = i;
			}
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, probeListBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, list.Length * sizeof(int), list, BufferUsageHint.DynamicDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

			listBoundsMin = boundsMin;
			listBoundsMax = boundsMax;
			listValid = true;
			Console.WriteLine($"[RadianceCache] {interiorTotal} interior probes, {exteriorTotal} exterior");
		}

		#endregion
	}
}
