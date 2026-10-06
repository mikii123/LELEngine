using System;
using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Lumen world-space radiance cache: a grid of probes over the GI volume, each an 8x8 full-sphere
	///     octahedral radiance map (with a border for seamless filtering) stored in a 2D atlas. Probes are
	///     re-traced through the global distance field (hits lit from the surface cache) and accumulated over
	///     time. Screen probe rays and radiosity rays read it beyond
	///     <see cref="GlobalIlluminationSettings.RadianceCacheNearDistance" />, which makes the far field
	///     temporally stable while the near field keeps per-ray detail.
	///
	///     Like Lumen, the update budget goes to the probes that matter: probes inside the static scene bounds
	///     ("interior") are refreshed every frame when the budget allows, the rest only occasionally.
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

		private const int Resolution = 8;
		private const int Tile = Resolution + 2;
		private const int ProbeListBinding = 3;

		private ComputeShader update;
		private int atlas;
		private int atlasWidth;
		private int atlasHeight;
		private int probesPerAxis;
		private int probesPerRow;
		private int probeCount;
		private int frameIndex;
		private bool needsFullUpdate = true;
		private Vector3 gridMin;
		private float probeSpacing;

		private int probeListBuffer;
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
			update = new ComputeShader("Engine/RadianceCacheUpdate.shader");
			probeListBuffer = GL.GenBuffer();
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			LumenScene scene = context.Renderer.LumenScene;
			Active = gi.Enabled && gi.Mode == GIMode.Lumen && gi.RadianceCacheEnabled && gi.GlobalSdf != 0 && scene.SurfaceCache != null;
			if (!Active)
			{
				needsFullUpdate = true;
				return;
			}

			int perAxis = Math.Max(2, gi.RadianceCacheProbesPerAxis);
			EnsureAtlas(perAxis);

			float spacing = gi.GridSize / (perAxis - 1);
			if (gi.GridMin != gridMin || spacing != probeSpacing)
			{
				gridMin = gi.GridMin;
				probeSpacing = spacing;
				needsFullUpdate = true;
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
			int interiorCount = needsFullUpdate ? interiorTotal : Math.Min(budget, interiorTotal);
			int exteriorCount = needsFullUpdate ? exteriorTotal : Math.Min(Math.Max(1, budget / 8), exteriorTotal);
			int dispatchCount = interiorCount + exteriorCount;
			if (dispatchCount == 0)
			{
				return;
			}

			ShaderProgram program = update.Program;
			update.Use();
			scene.SetUniforms(program);
			Lighting.SetSdfUniforms(program);
			SetUniforms(program, 7);
			program.SetTexture("FinalLighting", TextureTarget.Texture2D, scene.SurfaceCache.FinalLightingCurrent, 2);
			program.SetVector3("giSkyRadiance", gi.SkyRadiance);
			program.SetInt("frameIndex", frameIndex);
			program.SetFloat("historyWeight", needsFullUpdate ? 0f : gi.RadianceCacheHistoryWeight);
			program.SetInt("interiorStart", needsFullUpdate ? 0 : interiorNext);
			program.SetInt("interiorCount", interiorCount);
			program.SetInt("interiorTotal", Math.Max(1, interiorTotal));
			program.SetInt("exteriorStart", needsFullUpdate ? 0 : exteriorNext);
			program.SetInt("exteriorTotal", Math.Max(1, exteriorTotal));

			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, ProbeListBinding, probeListBuffer);
			GL.BindImageTexture(0, atlas, 0, false, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
			update.Dispatch(dispatchCount);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			if (interiorTotal > 0)
			{
				interiorNext = (interiorNext + interiorCount) % interiorTotal;
			}
			if (exteriorTotal > 0)
			{
				exteriorNext = (exteriorNext + exteriorCount) % exteriorTotal;
			}
			needsFullUpdate = false;
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
			program.SetVector2("rcAtlasSize", new Vector2(atlasWidth, atlasHeight));
			program.SetFloat("rcNearDistance", Lighting.GI.RadianceCacheNearDistance);
		}

		public override void Dispose()
		{
			update?.Delete();
			if (atlas != 0)
			{
				GL.DeleteTexture(atlas);
				atlas = 0;
			}
			if (probeListBuffer != 0)
			{
				GL.DeleteBuffer(probeListBuffer);
				probeListBuffer = 0;
			}
		}

		#endregion

		#region PrivateMethods

		private void EnsureAtlas(int perAxis)
		{
			if (atlas != 0 && perAxis == probesPerAxis)
			{
				return;
			}

			if (atlas != 0)
			{
				GL.DeleteTexture(atlas);
			}

			probesPerAxis = perAxis;
			probeCount = perAxis * perAxis * perAxis;
			probesPerRow = (int)Math.Ceiling(Math.Sqrt(probeCount));
			int rows = (probeCount + probesPerRow - 1) / probesPerRow;
			atlasWidth = probesPerRow * Tile;
			atlasHeight = rows * Tile;

			// Zero-initialised: alpha 0 marks "no data" for readers until a probe has been traced (no glClearTexImage in GL 4.3).
			float[] zeros = new float[atlasWidth * atlasHeight * 4];
			atlas = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, atlas);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, atlasWidth, atlasHeight, 0, PixelFormat.Rgba, PixelType.Float, zeros);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);

			needsFullUpdate = true;
			listValid = false;
			Console.WriteLine($"[RadianceCache] {perAxis}^3 probes, atlas {atlasWidth}x{atlasHeight}");
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
			needsFullUpdate = true;
			Console.WriteLine($"[RadianceCache] {interiorTotal} interior probes, {exteriorTotal} exterior");
		}

		#endregion
	}
}
