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
	/// </summary>
	public sealed class RadianceCachePass : RenderPass
	{
		#region PublicFields

		public override string Name => "RadianceCache";

		public int Atlas => atlas;
		public bool Active { get; private set; }
		public int InteriorProbeCount => interiorTotal;

		#endregion

		#region PrivateFields

		private const int ProbeListBinding = 3;
		private const int ProbeFramesBinding = 4;
		private const int TraceGroupSize = 16;

		private ComputeShader trace;
		private ComputeShader resolve;

		private int atlas;
		private int atlasWidth;
		private int atlasHeight;
		private int probesPerAxis;
		private int probeResolution;
		private int probesPerRow;
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
		private int interiorNext;
		private int exteriorNext;
		private Vector3 listBoundsMin;
		private Vector3 listBoundsMax;
		private bool listValid;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			trace = new ComputeShader("Engine/RadianceCacheTrace.shader");
			resolve = new ComputeShader("Engine/RadianceCacheResolve.shader");
			probeListBuffer = GL.GenBuffer();
			probeFramesBuffer = GL.GenBuffer();
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			LumenScene scene = context.Renderer.LumenScene;
			Active = gi.Enabled && gi.Mode == GIMode.Lumen && gi.RadianceCacheEnabled && gi.GlobalSdf != 0 && scene.SurfaceCache != null;
			if (!Active)
			{
				return;
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
			int budget = Math.Max(1, gi.RadianceCacheProbesPerFrame);
			int interiorCount = Math.Min(budget, interiorTotal);
			int exteriorCount = Math.Min(Math.Max(1, budget / 8), exteriorTotal);
			int count = interiorCount + exteriorCount;
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
			SetUniforms(traceProgram, 7);
			SetScheduleUniforms(traceProgram, interiorCount);
			traceProgram.SetTexture("FinalLighting", TextureTarget.Texture2D, scene.SurfaceCache.FinalLightingCurrent, 2);
			traceProgram.SetVector3("giSkyRadiance", gi.SkyRadiance);
			traceProgram.SetInt("frameIndex", frameIndex);
			GL.BindImageTexture(0, traceBuffer, 0, false, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			int groupsPerProbe = traceRes / TraceGroupSize * (traceRes / TraceGroupSize);
			trace.Dispatch(count * groupsPerProbe);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			// ---- 2. resolve: downsample into the stored maps and accumulate
			profiler.Split("RadianceCache.resolve");
			ShaderProgram resolveProgram = resolve.Program;
			resolve.Use();
			SetUniforms(resolveProgram, 7);
			SetScheduleUniforms(resolveProgram, interiorCount);
			resolveProgram.SetTexture("TraceBuffer", TextureTarget.Texture2D, traceBuffer, 1);
			resolveProgram.SetInt("frameIndex", frameIndex);
			resolveProgram.SetFloat("historyFrames", gi.RadianceCacheHistoryFrames);
			GL.BindImageTexture(0, atlas, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
			resolve.Dispatch(count);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

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
		///     Binds the cache for Engine/RadianceCache.glsl consumers. When the cache is unavailable the near
		///     distance is set to 0 and the shaders trace the whole distance field instead.
		/// </summary>
		public void SetUniforms(ShaderProgram program, int textureUnit)
		{
			if (!Active || atlas == 0)
			{
				program.SetFloat("rcNearDistance", 0f);
				return;
			}

			program.SetTexture("RadianceCacheAtlas", TextureTarget.Texture2D, atlas, textureUnit);
			program.SetVector3("rcGridMin", gridMin);
			program.SetFloat("rcProbeSpacing", probeSpacing);
			program.SetInt("rcProbesPerAxis", probesPerAxis);
			program.SetInt("rcProbesPerRow", probesPerRow);
			program.SetInt("rcProbeResolution", probeResolution);
			program.SetVector2("rcAtlasSize", new Vector2(atlasWidth, atlasHeight));
			program.SetFloat("rcNearDistance", Lighting.GI.RadianceCacheNearDistance);
		}

		public override void Dispose()
		{
			trace?.Delete();
			resolve?.Delete();
			if (atlas != 0)
			{
				GL.DeleteTexture(atlas);
				atlas = 0;
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
		}

		#endregion

		#region PrivateMethods

		private void SetScheduleUniforms(ShaderProgram program, int interiorCount)
		{
			program.SetInt("rcTraceResolution", traceResolution);
			program.SetInt("traceSlotsPerRow", traceSlotsPerRow);
			program.SetInt("sequenceOffset", 0);
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

			probesPerAxis = perAxis;
			probeResolution = resolution;
			probeCount = perAxis * perAxis * perAxis;
			probesPerRow = (int)Math.Ceiling(Math.Sqrt(probeCount));
			int rows = (probeCount + probesPerRow - 1) / probesPerRow;
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
			GL.BindTexture(TextureTarget.Texture2D, 0);
			ClearAtlas();

			listValid = false;
			Console.WriteLine($"[RadianceCache] {perAxis}^3 probes at {resolution}x{resolution}, atlas {atlasWidth}x{atlasHeight}");
		}

		// Alpha 0 marks "no data": consumers trace on until a probe has been resolved (no glClearTexImage in GL 4.3).
		// Every probe's last-update frame is reset to -1 so the first update after the clear replaces the history.
		private void ClearAtlas()
		{
			float[] zeros = new float[atlasWidth * atlasHeight * 4];
			GL.BindTexture(TextureTarget.Texture2D, atlas);
			GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, atlasWidth, atlasHeight, PixelFormat.Rgba, PixelType.Float, zeros);
			GL.BindTexture(TextureTarget.Texture2D, 0);

			int[] never = new int[Math.Max(1, probeCount)];
			for (int i = 0; i < never.Length; i++)
			{
				never[i] = -1;
			}
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, probeFramesBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, never.Length * sizeof(int), never, BufferUsageHint.DynamicDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

			interiorNext = 0;
			exteriorNext = 0;
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

			int[] list = new int[Math.Max(1, probeCount)];
			interior.CopyTo(list, 0);
			exterior.CopyTo(list, interiorTotal);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, probeListBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, list.Length * sizeof(int), list, BufferUsageHint.StaticDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

			listBoundsMin = boundsMin;
			listBoundsMax = boundsMax;
			listValid = true;
			Console.WriteLine($"[RadianceCache] {interiorTotal} interior probes, {exteriorTotal} exterior");
		}

		#endregion
	}
}
