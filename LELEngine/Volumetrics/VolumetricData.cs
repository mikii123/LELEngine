using System;
using System.Diagnostics;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Volumetrics
{
	/// <summary>
	///     The GPU side of a volumetric object: its cloud and how it is lit, made on the GPU from the object's settings.
	///     <list type="bullet">
	///         <item>Density: R8 per voxel with its mips (Engine/Volumetrics/VolumetricGenerate.shader).</item>
	///         <item>Occupancy: the densest voxel per block of 4^3 (VolumetricOccupancy.shader): the march jumps empty blocks.</item>
	///         <item>
	///             Lit: RGBA8 per voxel with its mips, what the march reads with one fetch a step: density, the sun's optical
	///             depth (for its direction against the object now), the ambient light's openness (VolumetricLight.shader).
	///         </item>
	///         <item>Lights A / B: the lights inside, at half resolution (VolumetricLightTransport.shader), only with lights.</item>
	///     </list>
	///     Work is done in steps of z slices, a share of it per frame (<see cref="VolumetricRuntime.WorkPerFrame" />), into new
	///     textures while the old ones keep drawing; they swap in when complete. A change of the shape or the noise makes the
	///     cloud again (after the settings held still for <see cref="SettleSeconds" />, so dragging a slider does not make one
	///     per frame), the sun turning against the object lights it again, a light inside moving works the lights out again.
	/// </summary>
	internal sealed class VolumetricData : IDisposable
	{
		#region PublicFields

		/// <summary>Seconds a changed shape must hold still before the cloud is made again (edit mode only).</summary>
		public const double SettleSeconds = 0.2;

		/// <summary>Seconds between two lightings while the sun keeps turning.</summary>
		public const double RelightInterval = 0.1;

		/// <summary>The largest optical depth the lit volume stores (sqrt encoded).</summary>
		public const float TauMax = 16f;

		/// <summary>Lit voxels made for a sun within this (cosine) of the current one are still right (no relight).</summary>
		public const float SunTolerance = 0.99996f;

		/// <summary>Bytes of a cloud read from its file uploaded per frame (the driver copies them at once).</summary>
		public static long UploadBytesPerFrame = 32L << 20;

		/// <summary>Seconds a made cloud must stay the drawn one before the editor reads it back into its file.</summary>
		public const double WriteDelay = 1.0;

		// What draws (complete textures).
		public int Density;
		public int Lit;
		public int Occupancy;
		public int LightsA;
		public int LightsB;
		public Vector3i Resolution;
		public Vector3i Blocks;
		public Vector3 Size;
		public VolumeShape Shape;

		/// <summary>Local units per voxel (the coarsest axis).</summary>
		public float Voxel;

		/// <summary>The lights 0..7 as their transport was worked out (the draw applies their colour and intensity); the count of them.</summary>
		public readonly VolumeLightSlot[] LightSlots = new VolumeLightSlot[VolumetricObjectBase.MaxLights];
		public int LightCount;

		/// <summary>The GI the cloud receives (Engine/Volumetrics/VolumetricGI.shader): rgba16f mean radiance + 1, its directional term.</summary>
		public int GIA;
		public int GIB;
		public Vector3i GIResolution;

		/// <summary>Every cell of the GI grid has been worked out at least once since it was made.</summary>
		public bool GIValid;

		#endregion

		#region PrivateFields

		private enum Phase
		{
			Idle,
			Generate,
			Light,
			Lights,

			// The density (and the lit voxels when they fit the sun) from a file (VolumeCache), then mips and occupancy.
			Upload
		}

		// A build in progress: its textures (swapped in when complete) and where it stands.
		private sealed class Build
		{
			public Phase Phase;
			public bool NewDensity;
			public bool Relight;
			public bool NewLights;
			public int Density;
			public int Occupancy;
			public int Lit;
			public int LightsA;
			public int LightsB;
			public Vector3i Resolution;
			public Vector3i Blocks;
			public Vector3i LightsResolution;
			public Vector3 Size;
			public VolumeShape Shape;
			public float Voxel;
			public int NextSlice;
			public int ShapeHash;
			public int LightHash;
			public int LightsHash;
			public Vector3 Sun;
			public readonly VolumeLightSlot[] Lights = new VolumeLightSlot[VolumetricObjectBase.MaxLights];
			public int LightCount;

			// When it started (Clock seconds), the frames it worked in, and with LEL_VOL_PROFILE=1 the GPU's milliseconds per phase.
			public double Started;
			public int Frames;
			public readonly double[] GpuMilliseconds = new double[5];

			// What it makes, as files know it (VolumeCache): the density's key and the lit voxels' settings.
			public string DensityKey;
			public string LitKey;

			// Read from a file: the file, and whether its lit voxels fit (else they are made on the GPU).
			public VolumeFile File;
			public bool UploadLit;

			// The GPU made the density or the lit voxels: the editor writes them into the cache.
			public bool Made;
		}

		// A made cloud read back for its file without stalling (pixel buffers and a fence, mapped a few frames later).
		private sealed class Readback
		{
			public string DensityKey;
			public string LitKey;
			public Vector3 Sun;
			public Vector3i Resolution;
			public int DensityBuffer;
			public int LitBuffer;
			public IntPtr Fence;
		}

		private static readonly Stopwatch Clock = Stopwatch.StartNew();

		private readonly VolumetricObjectBase owner;
		private Build build;
		private bool ready;
		private int shapeHash;
		private int lightHash;
		private int lightsHash;
		private Vector3 sun = Vector3.UnitY;
		private int pendingHash;
		private double pendingSince = -1.0;
		private double lastRelight = -1.0;
		private bool invalidated = true;
		private byte[] cpuDensity;
		private bool disposed;
		private readonly System.Collections.Generic.List<VolumeLightSlot> captureSlots = new System.Collections.Generic.List<VolumeLightSlot>();
		private readonly Vector4[] slotPositions = new Vector4[VolumetricObjectBase.MaxLights];
		private readonly Vector4[] slotParams = new Vector4[VolumetricObjectBase.MaxLights];
		private readonly Vector4[] slotSpots = new Vector4[VolumetricObjectBase.MaxLights];
		private readonly VolumeLightSlot[] movedSlots = new VolumeLightSlot[VolumetricObjectBase.MaxLights];
		private int giSlice;

		// The drawn transport's grid and what it was worked out with besides the slots (TransportSettingsHash).
		private Vector3i lightsResolution;
		private int transportSettingsHash;

		// A spare pair of transport textures (the ones the last swap replaced): the next whole rework of the lights writes into
		// them instead of making new ones (a light crossing into a big cloud's reach made and dropped ~25 MB of textures).
		private int spareLightsA;
		private int spareLightsB;
		private Vector3i spareLightsResolution;

		// The drawn cloud as files know it (VolumeCache).
		private string drawnDensityKey;
		private string drawnLitKey;

		// Rebuild() was asked: make the cloud, never take a kept one.
		private bool forceMake;

		// The first cloud's file being read (VolumeCache.Load), for this density key.
		private System.Threading.Tasks.Task<VolumeFile> pendingFile;
		private string pendingFileKey;

		// The editor: when the drawn cloud is read back for its file (Clock seconds; negative: nothing to write), and the
		// readback in flight.
		private double writeDue = -1.0;
		private Readback readback;

		#endregion

		#region Constructors

		public VolumetricData(VolumetricObjectBase owner)
		{
			this.owner = owner;
		}

		#endregion

		#region PublicProperties

		public bool Ready => ready && !disposed;

		/// <summary>A new cloud or light is being made (the old one keeps drawing).</summary>
		public bool Pending => build != null;

		/// <summary>Changes whenever another cloud (density) is drawn; unique across objects (a number never comes back).</summary>
		public int DensityVersion { get; private set; }

		private static int lastDensityVersion;

		#endregion

		#region PublicMethods

		/// <summary>Makes everything again on the next update, without waiting for the settings to settle (never a kept cloud).</summary>
		public void Invalidate()
		{
			invalidated = true;
			forceMake = true;
		}

		/// <summary>
		///     Starts the builds the object's settings and the sun ask for and advances the one in progress by up to
		///     <paramref name="budget" /> voxels of work. Returns the work used. On the GL thread, between frames' draws.
		/// </summary>
		public long Update(long budget, bool settle)
		{
			if (disposed)
			{
				return 0;
			}

			double now = Clock.Elapsed.TotalSeconds;
			int newShapeHash = owner.ShapeHash();
			int newLightHash = owner.LightHash();
			int newLightsHash = owner.LightsHash();
			Vector3 newSun = SunDirection();

			// The first cloud, unless Rebuild() asked to make it: one kept on the GPU by an object of the same settings, else
			// its file (VolumeCache: read on a worker thread, often since the scene was read), else it is made below.
			if (!ready && build == null && !forceMake)
			{
				if (TakeKeptCloud(newShapeHash, newLightHash, newLightsHash, newSun) || WaitForFile(newShapeHash, newLightHash, newLightsHash, newSun))
				{
					invalidated = false;
					return build != null ? Advance(budget) : 0;
				}
			}

			// A new cloud: at once the first time (or when asked), else once the settings held still.
			bool shapeChanged = invalidated || !ready && build == null || newShapeHash != shapeHash;
			if (shapeChanged && (build == null || build.ShapeHash != newShapeHash || invalidated))
			{
				bool now0 = invalidated || !ready || !settle;
				if (!now0)
				{
					if (pendingHash != newShapeHash || pendingSince < 0.0)
					{
						pendingHash = newShapeHash;
						pendingSince = now;
					}

					now0 = now - pendingSince >= SettleSeconds;
				}

				if (now0)
				{
					StartBuild(newShapeHash, newLightHash, newLightsHash, newSun, true);
					invalidated = false;
					forceMake = false;
					pendingSince = -1.0;
				}
			}
			else if (build == null && ready)
			{
				// The same cloud: light it again when the sun turned against it or the light settings changed, and work the
				// lights inside out again when they moved.
				bool relight = newLightHash != lightHash || Vector3.Dot(newSun, sun) < SunTolerance;
				if (relight && (!settle || now - lastRelight >= RelightInterval))
				{
					StartRelight(newLightHash, newLightsHash, newSun);
					lastRelight = now;
				}
				else if (newLightsHash != lightsHash && !TryUpdateLightsInPlace(newLightsHash))
				{
					StartLights(newLightsHash);
				}
			}

			long used = build != null ? Advance(budget) : 0;
			if (VolumeCache.Writable)
			{
				UpdateCacheWrite(now);
			}

			return used;
		}

		/// <summary>The density (0..1) at a local point, trilinear; 0 outside the shape's box or before the cloud is made.</summary>
		public float DensityAt(Vector3 local)
		{
			if (!Ready || Density == 0)
			{
				return 0f;
			}

			if (cpuDensity == null)
			{
				long readStart = Stopwatch.GetTimestamp();
				cpuDensity = new byte[Resolution.X * Resolution.Y * Resolution.Z];
				GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
				GL.BindTexture(TextureTarget.Texture3D, Density);
				GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
				GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.UnsignedByte, cpuDensity);
				GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
				GL.BindTexture(TextureTarget.Texture3D, 0);
				if (Profile)
				{
					Console.WriteLine($"[Volumetrics] {owner.gameObject?.Name}: density read back for the CPU in {(Stopwatch.GetTimestamp() - readStart) * 1000.0 / Stopwatch.Frequency:0.0} ms ({cpuDensity.Length / 1e6:0.0} MB)");
				}
			}

			Vector3 uvw = new Vector3(local.X / Size.X + 0.5f, local.Y / Size.Y + 0.5f, local.Z / Size.Z + 0.5f);
			if (uvw.X < 0f || uvw.Y < 0f || uvw.Z < 0f || uvw.X > 1f || uvw.Y > 1f || uvw.Z > 1f)
			{
				return 0f;
			}

			float fx = uvw.X * Resolution.X - 0.5f;
			float fy = uvw.Y * Resolution.Y - 0.5f;
			float fz = uvw.Z * Resolution.Z - 0.5f;
			int x0 = (int)MathF.Floor(fx);
			int y0 = (int)MathF.Floor(fy);
			int z0 = (int)MathF.Floor(fz);
			float tx = fx - x0;
			float ty = fy - y0;
			float tz = fz - z0;
			float sum = 0f;
			for (int k = 0; k < 8; k++)
			{
				int x = Math.Clamp(x0 + (k & 1), 0, Resolution.X - 1);
				int y = Math.Clamp(y0 + ((k >> 1) & 1), 0, Resolution.Y - 1);
				int z = Math.Clamp(z0 + ((k >> 2) & 1), 0, Resolution.Z - 1);
				float w = ((k & 1) != 0 ? tx : 1f - tx) * (((k >> 1) & 1) != 0 ? ty : 1f - ty) * (((k >> 2) & 1) != 0 ? tz : 1f - tz);
				sum += w * cpuDensity[(z * Resolution.Y + y) * Resolution.X + x];
			}

			return sum / 255f;
		}

		/// <summary>
		///     Works out a share of the GI the cloud receives (a full pass every GIUpdateFrames frames): per cell of a grid
		///     GICellSize voxels coarse, the GI's light from the six local directions, dimmed by the cloud towards each
		///     (Engine/Volumetrics/VolumetricGI.shader). Made again when the cloud's grid changes.
		/// </summary>
		public void UpdateGI()
		{
			if (!Ready || Density == 0)
			{
				return;
			}

			// Cells of GICellSize voxels, at most 64 a side.
			int cell = Math.Clamp(owner.GICellSize, 2, 32);
			var resolution = new Vector3i(Math.Clamp((Resolution.X + cell - 1) / cell, 1, 64), Math.Clamp((Resolution.Y + cell - 1) / cell, 1, 64), Math.Clamp((Resolution.Z + cell - 1) / cell, 1, 64));
			if (GIA == 0 || resolution != GIResolution)
			{
				DeleteTexture(ref GIA);
				DeleteTexture(ref GIB);
				GIA = CreateTexture(SizedInternalFormat.Rgba16f, resolution, false, false);
				GIB = CreateTexture(SizedInternalFormat.Rgba16f, resolution, false, false);
				// A coarse grid read between its cells: linear, never the cells' blocks.
				foreach (int texture in new[] { GIA, GIB })
				{
					GL.BindTexture(TextureTarget.Texture3D, texture);
					GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
					GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
				}

				GL.BindTexture(TextureTarget.Texture3D, 0);
				GIResolution = resolution;
				GIValid = false;
				giSlice = 0;
			}

			int slices = Math.Max(1, (resolution.Z + Math.Clamp(owner.GIUpdateFrames, 1, 16) - 1) / Math.Clamp(owner.GIUpdateFrames, 1, 16));
			slices = Math.Min(slices, resolution.Z - giSlice);
			ShaderProgram program = VolumetricRuntime.GIShader.Program;
			program.Use();
			// The GI's samplers keep units of their own (unused ones too: two sampler types on one unit fail the dispatch).
			program.SetInt("RadianceCacheSH", Lighting.VolumeProbeSHTextureUnit);
			program.SetInt("RadianceCacheAtlas", Lighting.VolumeProbeTextureUnit);
			program.SetInt("GlobalSdfObjectIds", Lighting.GlobalSdfObjectIdTextureUnit);
			program.SetInt("giSkybox", Lighting.SkyboxTextureUnit);
			Lighting.SetVolumeGIUniforms(program, true);
			program.SetTexture("Density", TextureTarget.Texture3D, Density, 0);
			program.SetInt3("volResolution", resolution.X, resolution.Y, resolution.Z);
			program.SetInt("volSliceOffset", giSlice);
			program.SetVector3("volSize", Size);
			program.SetFloat("volSigma", Sigma);
			program.SetFloat("volVoxel", Voxel);
			program.SetFloat("volReach", MathF.Max(owner.ShadowReach, 0.05f) * MathF.Max(owner.NoiseSize, 0.01f));
			program.SetMatrix4("volLocalToWorld", owner.transform.LocalToWorld);
			GL.BindImageTexture(0, GIA, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, GIB, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.DispatchCompute((resolution.X + 7) / 8, (resolution.Y + 7) / 8, slices);
			GL.BindImageTexture(0, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit);
			giSlice += slices;
			if (giSlice >= resolution.Z)
			{
				giSlice = 0;
				GIValid = true;
			}
		}

		/// <summary>
		///     The density (0..1) exactly at local points, from the noise the cloud is made of (the generate kernel's points
		///     variant, with the drawn cloud's grid), not read from the voxels: no interpolation, no 8-bit steps. On the GPU;
		///     waits for the result. False before the cloud is made.
		/// </summary>
		public bool EvaluateDensity(ReadOnlySpan<Vector3> local, Span<float> densities)
		{
			if (!Ready || local.Length == 0)
			{
				return false;
			}

			int count = Math.Min(local.Length, densities.Length);
			var points = new Vector4[count];
			for (int i = 0; i < count; i++)
			{
				points[i] = new Vector4(local[i], 0f);
			}

			// Other passes keep their own storage buffers bound: put theirs back afterwards.
			GL.GetInteger((GetIndexedPName)All.ShaderStorageBufferBinding, 0, out int previous0);
			GL.GetInteger((GetIndexedPName)All.ShaderStorageBufferBinding, 1, out int previous1);
			int input = GL.GenBuffer();
			int output = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, input);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, count * 16, points, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, output);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, count * 4, IntPtr.Zero, BufferUsageHint.StreamRead);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, input);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, output);

			ShaderProgram program = VolumetricRuntime.GeneratePoints.Program;
			program.Use();
			SetNoiseUniforms(program, Resolution, Size, Shape, Voxel);
			program.SetInt("volPointCount", count);
			GL.DispatchCompute((count + 63) / 64, 1, 1);
			GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit);

			var results = new float[count];
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, output);
			GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, count * 4, results);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, previous0);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, previous1);
			GL.DeleteBuffer(input);
			GL.DeleteBuffer(output);
			results.AsSpan().CopyTo(densities);
			return true;
		}

		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			if (readback != null)
			{
				DeleteReadback(readback);
				readback = null;
			}

			// A build in progress goes first (its textures that are not the drawn ones).
			Abandon();

			// A complete cloud waits for the next object of the same settings (a scene loaded again: play mode).
			if (ready && Density != 0 && drawnDensityKey != null)
			{
				long voxels = (long)Resolution.X * Resolution.Y * Resolution.Z;
				VolumeCache.Park(new VolumeCache.ParkedCloud
				{
					DensityKey = drawnDensityKey,
					LitKey = drawnLitKey,
					Sun = sun,
					Density = Density,
					Occupancy = Occupancy,
					Lit = Lit,
					Resolution = Resolution,
					Blocks = Blocks,
					Size = Size,
					Shape = Shape,
					Voxel = Voxel,
					CpuDensity = cpuDensity,
					// Density and lit voxels with their mips (a seventh more), the occupancy.
					Bytes = voxels * 5 * 8 / 7 + (long)Blocks.X * Blocks.Y * Blocks.Z
				});
				Density = 0;
				Occupancy = 0;
				Lit = 0;
			}

			ready = false;
			DeleteTexture(ref Density);
			DeleteTexture(ref Occupancy);
			DeleteTexture(ref Lit);
			DeleteTexture(ref LightsA);
			DeleteTexture(ref LightsB);
			DeleteTexture(ref spareLightsA);
			DeleteTexture(ref spareLightsB);
			DeleteTexture(ref GIA);
			DeleteTexture(ref GIB);
			cpuDensity = null;
		}

		#endregion

		#region PrivateMethods

		// Towards the sun in the object's local space (the same transform the march takes rays through).
		private Vector3 SunDirection()
		{
			DirectionalLight light = owner.EffectiveSun;
			Vector3 towards = light != null ? -light.transform.forward : Vector3.UnitY;
			Vector3 local = Vector3.TransformVector(towards, owner.transform.WorldToLocal);
			return local.LengthSquared > 1e-12f ? local.Normalized() : Vector3.UnitY;
		}

		private void StartBuild(int newShapeHash, int newLightHash, int newLightsHash, Vector3 newSun, bool newDensity)
		{
			Abandon();
			Vector3i resolution = VolumetricObjectBase.GridFor(owner.Size, owner.VoxelSize, out _, out _);
			Vector3 size = new Vector3(MathF.Max(owner.Size.X, 1e-3f), MathF.Max(owner.Size.Y, 1e-3f), MathF.Max(owner.Size.Z, 1e-3f));
			build = new Build
			{
				Phase = Phase.Generate,
				NewDensity = newDensity,
				Relight = true,
				NewLights = true,
				Resolution = resolution,
				Blocks = new Vector3i((resolution.X + 3) / 4, (resolution.Y + 3) / 4, (resolution.Z + 3) / 4),
				LightsResolution = new Vector3i(Math.Max(1, (resolution.X + 1) / 2), Math.Max(1, (resolution.Y + 1) / 2), Math.Max(1, (resolution.Z + 1) / 2)),
				Size = size,
				Shape = owner.Shape,
				Voxel = MathF.Max(size.X / resolution.X, MathF.Max(size.Y / resolution.Y, size.Z / resolution.Z)),
				ShapeHash = newShapeHash,
				LightHash = newLightHash,
				LightsHash = newLightsHash,
				Sun = newSun,
				Started = Clock.Elapsed.TotalSeconds,
				Made = true
			};
			build.DensityKey = owner.DensityKey();
			build.LitKey = owner.LitKey(build.DensityKey);
			build.Density = CreateTexture(SizedInternalFormat.R8, resolution, true, true);
			build.Occupancy = CreateTexture(SizedInternalFormat.R8, build.Blocks, false, false);
			build.Lit = CreateTexture(SizedInternalFormat.Rgba8, resolution, true, false);
			CaptureLights(build);
		}

		/// <summary>
		///     A cloud's density made now and read back for its file (a game build's bake; the GL thread, waits for the GPU).
		///     Without lit voxels: they need the scene's sun, so the player lights the cloud.
		/// </summary>
		internal static VolumeFile MakeDensity(VolumetricObjectBase volume)
		{
			var data = new VolumetricData(volume);
			try
			{
				Vector3i resolution = VolumetricObjectBase.GridFor(volume.Size, volume.VoxelSize, out _, out _);
				Vector3 size = new Vector3(MathF.Max(volume.Size.X, 1e-3f), MathF.Max(volume.Size.Y, 1e-3f), MathF.Max(volume.Size.Z, 1e-3f));
				data.build = new Build
				{
					Phase = Phase.Generate,
					NewDensity = true,
					Resolution = resolution,
					Size = size,
					Shape = volume.Shape,
					Voxel = MathF.Max(size.X / resolution.X, MathF.Max(size.Y / resolution.Y, size.Z / resolution.Z))
				};
				data.build.Density = CreateTexture(SizedInternalFormat.R8, resolution, true, true);
				data.DispatchSlices(VolumetricRuntime.Generate, resolution, long.MaxValue, data.SetGenerateUniforms, data.build.Density, SizedInternalFormat.R8, 0, 0);
				GL.UseProgram(0);
				GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
				var density = new byte[(long)resolution.X * resolution.Y * resolution.Z];
				GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
				GL.BindTexture(TextureTarget.Texture3D, data.build.Density);
				GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.UnsignedByte, density);
				GL.BindTexture(TextureTarget.Texture3D, 0);
				GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
				return new VolumeFile { DensityKey = volume.DensityKey(), Resolution = resolution, Density = density };
			}
			finally
			{
				data.Dispose();
			}
		}

		/// <summary>
		///     The first cloud from an object of the same settings that went away (its textures parked in VolumeCache): drawn at
		///     once when its lit voxels fit this object's sun, else relit. The lights' transport is worked out next.
		/// </summary>
		private bool TakeKeptCloud(int newShapeHash, int newLightHash, int newLightsHash, Vector3 newSun)
		{
			string densityKey = owner.DensityKey();
			VolumeCache.ParkedCloud cloud = VolumeCache.TryAdopt(densityKey);
			if (cloud == null)
			{
				return false;
			}

			Density = cloud.Density;
			Occupancy = cloud.Occupancy;
			Resolution = cloud.Resolution;
			Blocks = cloud.Blocks;
			Size = cloud.Size;
			Shape = cloud.Shape;
			Voxel = cloud.Voxel;
			shapeHash = newShapeHash;
			cpuDensity = cloud.CpuDensity;
			drawnDensityKey = densityKey;
			DensityVersion = System.Threading.Interlocked.Increment(ref lastDensityVersion);
			string litKey = owner.LitKey(densityKey);
			if (cloud.Lit != 0 && cloud.LitKey == litKey && Vector3.Dot(cloud.Sun, newSun) >= SunTolerance)
			{
				Lit = cloud.Lit;
				lightHash = newLightHash;
				sun = cloud.Sun;
				drawnLitKey = litKey;
				ready = true;
				Console.WriteLine($"[Volumetrics] {owner.gameObject?.Name}: kept {Resolution.X}x{Resolution.Y}x{Resolution.Z} from an object of the same settings (nothing made)");
			}
			else
			{
				if (cloud.Lit != 0)
				{
					GL.DeleteTexture(cloud.Lit);
				}

				StartRelight(newLightHash, newLightsHash, newSun);
			}

			return true;
		}

		/// <summary>
		///     The first cloud from its file (VolumeCache): true while the file is being read (nothing draws yet) and once its
		///     upload started; false when there is none (the cloud is made).
		/// </summary>
		private bool WaitForFile(int newShapeHash, int newLightHash, int newLightsHash, Vector3 newSun)
		{
			if (VolumeCache.Folder == null)
			{
				return false;
			}

			string densityKey = owner.DensityKey();
			if (pendingFile == null || pendingFileKey != densityKey)
			{
				pendingFile = VolumeCache.Load(densityKey);
				pendingFileKey = densityKey;
			}

			if (!pendingFile.IsCompleted)
			{
				return true;
			}

			VolumeFile file = pendingFile.IsCompletedSuccessfully ? pendingFile.Result : null;
			pendingFile = null;
			VolumeCache.Taken(densityKey);
			Vector3i resolution = VolumetricObjectBase.GridFor(owner.Size, owner.VoxelSize, out _, out _);
			if (file == null || file.Resolution != resolution || file.Density == null)
			{
				return false;
			}

			Abandon();
			Vector3 size = new Vector3(MathF.Max(owner.Size.X, 1e-3f), MathF.Max(owner.Size.Y, 1e-3f), MathF.Max(owner.Size.Z, 1e-3f));
			string litKey = owner.LitKey(densityKey);
			bool lit = file.Lit != null && file.LitKey == litKey && Vector3.Dot(file.Sun, newSun) >= SunTolerance;
			build = new Build
			{
				Phase = Phase.Upload,
				NewDensity = true,
				Relight = true,
				NewLights = true,
				Resolution = resolution,
				Blocks = new Vector3i((resolution.X + 3) / 4, (resolution.Y + 3) / 4, (resolution.Z + 3) / 4),
				LightsResolution = new Vector3i(Math.Max(1, (resolution.X + 1) / 2), Math.Max(1, (resolution.Y + 1) / 2), Math.Max(1, (resolution.Z + 1) / 2)),
				Size = size,
				Shape = owner.Shape,
				Voxel = MathF.Max(size.X / resolution.X, MathF.Max(size.Y / resolution.Y, size.Z / resolution.Z)),
				ShapeHash = newShapeHash,
				LightHash = newLightHash,
				LightsHash = newLightsHash,
				Sun = lit ? file.Sun : newSun,
				Started = Clock.Elapsed.TotalSeconds,
				DensityKey = densityKey,
				LitKey = litKey,
				File = file,
				UploadLit = lit,
				Made = !lit
			};
			build.Density = CreateTexture(SizedInternalFormat.R8, resolution, true, true);
			build.Occupancy = CreateTexture(SizedInternalFormat.R8, build.Blocks, false, false);
			build.Lit = CreateTexture(SizedInternalFormat.Rgba8, resolution, true, false);
			CaptureLights(build);
			return true;
		}

		// Uploads the next slices of the file's voxels (at most UploadBytesPerFrame a frame: the driver copies them at once).
		private long UploadSlices(long budget)
		{
			VolumeFile file = build.File;
			Vector3i r = build.Resolution;
			long sliceVoxels = (long)r.X * r.Y;
			int z = build.NextSlice;
			int count = (int)Math.Clamp(UploadBytesPerFrame / (sliceVoxels * (build.UploadLit ? 5 : 1)), 1, r.Z - z);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
			GL.BindTexture(TextureTarget.Texture3D, build.Density);
			GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, z, r.X, r.Y, count, PixelFormat.Red, PixelType.UnsignedByte, ref file.Density[z * sliceVoxels]);
			if (build.UploadLit)
			{
				GL.BindTexture(TextureTarget.Texture3D, build.Lit);
				GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, z, r.X, r.Y, count, PixelFormat.Rgba, PixelType.UnsignedByte, ref file.Lit[z * sliceVoxels * 4]);
			}

			GL.BindTexture(TextureTarget.Texture3D, 0);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
			build.NextSlice += count;
			// The frame's share is spent: the rest of this frame's budget goes too.
			return Math.Max(budget, 1);
		}

		// The same cloud, lit for a new sun (and the lights worked out again: they read the density only, but a relight swaps
		// the lit volume; the lights follow it so both stay in step).
		private void StartRelight(int newLightHash, int newLightsHash, Vector3 newSun)
		{
			Abandon();
			build = new Build
			{
				Phase = Phase.Light,
				NewDensity = false,
				Relight = true,
				NewLights = newLightsHash != lightsHash,
				Resolution = Resolution,
				Blocks = Blocks,
				LightsResolution = new Vector3i(Math.Max(1, (Resolution.X + 1) / 2), Math.Max(1, (Resolution.Y + 1) / 2), Math.Max(1, (Resolution.Z + 1) / 2)),
				Size = Size,
				Shape = Shape,
				Voxel = Voxel,
				ShapeHash = shapeHash,
				LightHash = newLightHash,
				LightsHash = newLightsHash,
				Sun = newSun,
				Density = Density,
				Occupancy = Occupancy,
				Started = Clock.Elapsed.TotalSeconds,
				DensityKey = drawnDensityKey,
				LitKey = drawnDensityKey != null ? owner.LitKey(drawnDensityKey) : null,
				Made = true
			};
			build.Lit = CreateTexture(SizedInternalFormat.Rgba8, Resolution, true, false);
			CaptureLights(build);
		}

		private void StartLights(int newLightsHash)
		{
			Abandon();
			build = new Build
			{
				Phase = Phase.Lights,
				NewDensity = false,
				Relight = false,
				NewLights = true,
				Resolution = Resolution,
				Blocks = Blocks,
				LightsResolution = new Vector3i(Math.Max(1, (Resolution.X + 1) / 2), Math.Max(1, (Resolution.Y + 1) / 2), Math.Max(1, (Resolution.Z + 1) / 2)),
				Size = Size,
				Shape = Shape,
				Voxel = Voxel,
				ShapeHash = shapeHash,
				LightHash = lightHash,
				LightsHash = newLightsHash,
				Sun = sun,
				Density = Density,
				Occupancy = Occupancy,
				Lit = Lit,
				Started = Clock.Elapsed.TotalSeconds,
				DensityKey = drawnDensityKey,
				LitKey = drawnLitKey
			};
			CaptureLights(build);
		}

		private void CaptureLights(Build b)
		{
			owner.GatherLightSlots(captureSlots);
			b.LightCount = Math.Min(captureSlots.Count, VolumetricObjectBase.MaxLights);
			for (int i = 0; i < VolumetricObjectBase.MaxLights; i++)
			{
				b.Lights[i] = i < b.LightCount ? captureSlots[i] : default;
			}
		}

		// Advances the build by up to budget voxels of work; swaps it in when complete.
		private long Advance(long budget)
		{
			long used = 0;
			build.Frames++;
			while (build != null && used < budget)
			{
				// LEL_VOL_PROFILE=1: each step's GPU time, the GPU drained before and after it (it stalls; measuring only).
				Build current = build;
				Phase phase = current.Phase;
				long profileStart = 0;
				if (Profile)
				{
					GL.Finish();
					profileStart = Stopwatch.GetTimestamp();
				}

				switch (build.Phase)
				{
					case Phase.Generate:
						used += DispatchSlices(VolumetricRuntime.Generate, build.Resolution, budget - used, SetGenerateUniforms, build.Density, SizedInternalFormat.R8, 0, 0);
						if (build.NextSlice >= build.Resolution.Z)
						{
							GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
							GenerateMipmaps(build.Density);
							BuildOccupancy();
							NextPhase(Phase.Light);
						}

						break;
					case Phase.Light:
						used += DispatchSlices(VolumetricRuntime.Light, build.Resolution, budget - used, SetLightUniforms, build.Lit, SizedInternalFormat.Rgba8, 0, 0);
						if (build.NextSlice >= build.Resolution.Z)
						{
							GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
							GenerateMipmaps(build.Lit);
							NextPhase(build.NewLights || build.NewDensity ? Phase.Lights : Phase.Idle);
						}

						break;
					case Phase.Lights:
						if (build.LightCount == 0)
						{
							NextPhase(Phase.Idle);
							break;
						}

						// Linear light in half floats: a scene light's falloff spans more than 8 bits hold. The spare pair when it fits.
						if (build.LightsA == 0)
						{
							if (spareLightsA != 0 && spareLightsResolution == build.LightsResolution)
							{
								build.LightsA = spareLightsA;
								build.LightsB = spareLightsB;
								spareLightsA = 0;
								spareLightsB = 0;
							}
							else
							{
								DeleteTexture(ref spareLightsA);
								DeleteTexture(ref spareLightsB);
								build.LightsA = CreateTexture(SizedInternalFormat.Rgba16f, build.LightsResolution, true, false);
								build.LightsB = CreateTexture(SizedInternalFormat.Rgba16f, build.LightsResolution, true, false);
							}
						}

						// A transport voxel costs about 0.7 of a made voxel per light (measured on a 12.6 M-voxel storm: eight lights
						// over its 1.6 M transport voxels took 68 ms of the GPU, its 12.6 M voxels made 72-94 ms): counted so, eight
						// lights spread over frames instead of filling one.
						used += DispatchSlices(VolumetricRuntime.LightTransport, build.LightsResolution, budget - used, SetLightsUniforms, build.LightsA, SizedInternalFormat.Rgba16f, build.LightsB, 1, 1.0 + TransportCostPerLight * build.LightCount);
						if (build.NextSlice >= build.LightsResolution.Z)
						{
							GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
							GenerateMipmaps(build.LightsA);
							GenerateMipmaps(build.LightsB);
							NextPhase(Phase.Idle);
						}

						break;
					case Phase.Upload:
						used += UploadSlices(budget - used);
						if (build.NextSlice >= build.Resolution.Z)
						{
							GenerateMipmaps(build.Density);
							BuildOccupancy();
							if (build.UploadLit)
							{
								GenerateMipmaps(build.Lit);
								NextPhase(build.NewLights ? Phase.Lights : Phase.Idle);
							}
							else
							{
								NextPhase(Phase.Light);
							}
						}

						break;
					default:
						Complete();
						break;
				}

				if (Profile && phase != Phase.Idle)
				{
					GL.Finish();
					current.GpuMilliseconds[(int)phase] += (Stopwatch.GetTimestamp() - profileStart) * 1000.0 / Stopwatch.Frequency;
				}
			}

			return used;
		}

		/// <summary>LEL_VOL_PROFILE=1: builds measure their phases on the GPU (draining it around each step) and log them.</summary>
		private static readonly bool Profile = Environment.GetEnvironmentVariable("LEL_VOL_PROFILE") == "1";

		private void NextPhase(Phase phase)
		{
			build.Phase = phase;
			build.NextSlice = 0;
		}

		// A transport voxel's cost per light, in made voxels (see Advance).
		private const double TransportCostPerLight = 0.7;

		// Dispatches as many z slices as the budget allows (at least one), each dispatch a short one; a voxel counts cost
		// voxels of the budget.
		private long DispatchSlices(ComputeShader shader, Vector3i resolution, long budget, Action<ShaderProgram> setUniforms, int image, SizedInternalFormat format, int secondImage, int secondBinding, double cost = 1.0)
		{
			ShaderProgram program = shader.Program;
			program.Use();
			setUniforms(program);
			GL.BindImageTexture(0, image, 0, true, 0, TextureAccess.WriteOnly, format);
			if (secondImage != 0)
			{
				GL.BindImageTexture(secondBinding, secondImage, 0, true, 0, TextureAccess.WriteOnly, format);
			}

			long perSlice = (long)resolution.X * resolution.Y;
			long costPerSlice = Math.Max(1, (long)(perSlice * cost));
			long used = 0;
			const long DispatchVoxels = 1 << 20;
			while (build.NextSlice < resolution.Z && (used == 0 || used < budget))
			{
				int slices = (int)Math.Clamp(DispatchVoxels / Math.Max(1, costPerSlice), 1, resolution.Z - build.NextSlice);
				program.SetInt("volSliceOffset", build.NextSlice);
				GL.DispatchCompute((resolution.X + 7) / 8, (resolution.Y + 7) / 8, slices);
				build.NextSlice += slices;
				used += slices * costPerSlice;
			}

			GL.BindImageTexture(0, 0, 0, true, 0, TextureAccess.WriteOnly, format);
			if (secondImage != 0)
			{
				GL.BindImageTexture(secondBinding, 0, 0, true, 0, TextureAccess.WriteOnly, format);
			}

			return used;
		}

		private void SetGenerateUniforms(ShaderProgram program)
		{
			SetNoiseUniforms(program, build.Resolution, build.Size, build.Shape, build.Voxel);
		}

		// The cloud's shape and noise for a grid (its voxel size sets how many octaves fit).
		private void SetNoiseUniforms(ShaderProgram program, Vector3i resolution, Vector3 size, VolumeShape shape, float voxel)
		{
			VolumetricObjectBase o = owner;
			float noiseSize = MathF.Max(o.NoiseSize, 0.01f);

			// The seed as an offset in noise space (the same numbers any .NET System.Random gives for the seed).
			var random = new Random(o.Seed);
			var offset = new Vector3((float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f, (float)random.NextDouble() * 1000f);

			// Octaves down to about three voxels: finer ones would only alias (the detail adds the fine grain).
			int fits = (int)MathF.Floor(1f + MathF.Log2(noiseSize / (3f * voxel)));
			int octaves = Math.Clamp(Math.Min(o.Octaves, Math.Max(fits, 2)), 1, 8);

			program.SetInt3("volResolution", resolution.X, resolution.Y, resolution.Z);
			program.SetVector3("volSize", size);
			program.SetInt("volShape", (int)shape);
			program.SetVector3("volNoiseOffset", offset);
			program.SetFloat("volNoiseSize", noiseSize);
			program.SetInt("volNoiseType", (int)o.Noise);
			program.SetInt("volOctaves", octaves);
			program.SetFloat("volGain", Math.Clamp(o.Roughness, 0.05f, 0.95f));
			program.SetFloat("volWarp", o.Warp);
			program.SetFloat("volThreshold", 1f - o.Coverage);
			program.SetFloat("volSoftness", MathF.Max(o.Softness, 0.02f));
			program.SetFloat("volClumpScale", o.ClumpSize > 0f ? o.ClumpSize * noiseSize : 0f);
			program.SetFloat("volClumpThreshold", 1f - o.ClumpCoverage);
			program.SetFloat("volClumpSoftness", MathF.Max(o.ClumpSoftness, 0.01f));
			program.SetFloat("volProfileScale", (o.ClumpSize > 0f ? o.ClumpSize : 2f) * noiseSize);
			program.SetFloat("volSideSoftness", MathF.Max(o.SideSoftness, 0.001f));
			program.SetFloat("volCapSoftness", MathF.Max(o.CapSoftness, 0.001f));
			program.SetFloat("volTopRoughness", o.TopRoughness);
			program.SetFloat("volBottomRoughness", o.BottomRoughness);
			program.SetFloat("volSideRoughness", o.SideRoughness);
		}

		private void SetLightUniforms(ShaderProgram program)
		{
			VolumetricObjectBase o = owner;
			float noiseSize = MathF.Max(o.NoiseSize, 0.01f);
			program.SetTexture("Density", TextureTarget.Texture3D, build.Density, 0);
			program.SetInt3("volResolution", build.Resolution.X, build.Resolution.Y, build.Resolution.Z);
			program.SetVector3("volSize", build.Size);
			program.SetVector3("volSunDirection", build.Sun);
			program.SetFloat("volSigma", Sigma);
			program.SetFloat("volShadowReach", MathF.Max(o.ShadowReach, 0.01f) * noiseSize);
			program.SetFloat("volVoxel", build.Voxel);
			program.SetFloat("volTauMax", TauMax);
		}

		private void SetLightsUniforms(ShaderProgram program)
		{
			SetTransportUniforms(program, build.Density, build.LightsResolution, build.Size, build.Voxel, build.Lights, build.LightCount, Vector3i.Zero, build.LightsResolution);
		}

		// The light transport kernel's inputs: the density it reads, its grid, the lights' slots and the box it works out.
		private void SetTransportUniforms(ShaderProgram program, int density, Vector3i resolution, Vector3 size, float voxel, VolumeLightSlot[] slots, int count, Vector3i regionMin, Vector3i regionMax)
		{
			program.SetTexture("Density", TextureTarget.Texture3D, density, 0);
			program.SetInt3("volResolution", resolution.X, resolution.Y, resolution.Z);
			program.SetInt3("volRegionMin", regionMin.X, regionMin.Y, regionMin.Z);
			program.SetInt3("volRegionMax", regionMax.X, regionMax.Y, regionMax.Z);
			program.SetVector3("volSize", size);
			program.SetFloat("volSigma", Sigma);
			program.SetFloat("volVoxel", voxel);
			for (int i = 0; i < VolumetricObjectBase.MaxLights; i++)
			{
				bool used = i < count;
				slotPositions[i] = used ? slots[i].PositionSize : Vector4.Zero;
				slotParams[i] = used ? slots[i].Params : new Vector4(0f, 0f, -2f, 0f);
				slotSpots[i] = used ? slots[i].Spot : new Vector4(0f, 0f, 0f, 1f);
			}

			program.SetVector4Array("volLights", slotPositions);
			program.SetVector4Array("volLightParams", slotParams);
			program.SetVector4Array("volLightSpots", slotSpots);
			program.SetFloat("volWorldScale", owner.WorldScale);
			program.SetFloat("volLightGlow", owner.LightGlow);
		}

		// What the whole transport depends on besides the lights' slots: a change of it reworks every voxel.
		private int TransportSettingsHash()
		{
			return HashCode.Combine(owner.LightGlow, owner.Density, owner.NoiseSize, owner.WorldScale);
		}

		/// <summary>
		///     Lights that moved, or changed their reach or cone, while the same lights stay in the same slots: a light's part of
		///     the transport changes only where it reached before and where it reaches now, so only that box is worked out
		///     again, in the drawn textures and at once (the box is small: a light dragged through a big cloud reworked all of
		///     it every frame, 2026-11-08, ~1.6 M voxels of eight lights and a new pair of 25 MB textures a frame for New
		///     Frontier's storm). False when the set of lights or the cloud's settings changed, or the box is large: the whole
		///     transport is made again then.
		/// </summary>
		private bool TryUpdateLightsInPlace(int newLightsHash)
		{
			if (LightsA == 0 || build != null || transportSettingsHash != TransportSettingsHash())
			{
				return false;
			}

			owner.GatherLightSlots(captureSlots);
			int count = Math.Min(captureSlots.Count, VolumetricObjectBase.MaxLights);
			if (count != LightCount || count == 0)
			{
				return false;
			}

			Vector3i resolution = lightsResolution;
			var lo = new Vector3i(int.MaxValue);
			var hi = new Vector3i(int.MinValue);
			for (int i = 0; i < count; i++)
			{
				VolumeLightSlot was = LightSlots[i];
				VolumeLightSlot now = captureSlots[i];
				if (was.Own != now.Own || was.Scene != now.Scene || was.Params.X != now.Params.X)
				{
					return false;
				}

				if (was.PositionSize == now.PositionSize && was.Params == now.Params && was.Spot == now.Spot)
				{
					continue;
				}

				IncludeReach(was, resolution, ref lo, ref hi);
				IncludeReach(now, resolution, ref lo, ref hi);
			}

			if (lo.X < hi.X && lo.Y < hi.Y && lo.Z < hi.Z)
			{
				Vector3i extent = hi - lo;
				long voxels = (long)extent.X * extent.Y * extent.Z;
				if (voxels > Math.Min(InPlaceMaxVoxels, (long)resolution.X * resolution.Y * resolution.Z / 3))
				{
					return false;
				}

				ShaderProgram program = VolumetricRuntime.LightTransport.Program;
				program.Use();
				captureSlots.CopyTo(0, movedSlots, 0, count);
				SetTransportUniforms(program, Density, resolution, Size, Voxel, movedSlots, count, lo, hi);
				program.SetInt("volSliceOffset", 0);
				GL.BindImageTexture(0, LightsA, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, LightsB, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.DispatchCompute((extent.X + 7) / 8, (extent.Y + 7) / 8, extent.Z);
				GL.BindImageTexture(0, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
				GenerateMipmaps(LightsA);
				GenerateMipmaps(LightsB);
				InPlaceUpdates++;
			}

			for (int i = 0; i < count; i++)
			{
				LightSlots[i] = captureSlots[i];
			}

			lightsHash = newLightsHash;
			return true;
		}

		/// <summary>Light transport boxes worked out in place since the start (diagnostics: a moving light should use these).</summary>
		public static long InPlaceUpdates { get; private set; }

		// The largest box (voxels of the transport grid) an in-place update works out at once.
		private const long InPlaceMaxVoxels = 2_000_000;

		// Widens the box (transport voxels, max exclusive) by where a slot's light reaches: an own light's glow ends at 8 core
		// radii, a scene light at its range; a voxel more on every side for the sampling between voxels.
		private void IncludeReach(VolumeLightSlot slot, Vector3i resolution, ref Vector3i lo, ref Vector3i hi)
		{
			Vector3 centre = slot.PositionSize.Xyz;
			float reach = slot.Params.X < 0.5f ? slot.PositionSize.W * 8f : slot.PositionSize.W / owner.WorldScale;
			Vector3 size = Size;
			for (int axis = 0; axis < 3; axis++)
			{
				float min = ((centre[axis] - reach) / size[axis] + 0.5f) * resolution[axis] - 0.5f;
				float max = ((centre[axis] + reach) / size[axis] + 0.5f) * resolution[axis] - 0.5f;
				int a = Math.Clamp((int)MathF.Floor(min) - 1, 0, resolution[axis]);
				int b = Math.Clamp((int)MathF.Ceiling(max) + 2, 0, resolution[axis]);
				lo[axis] = Math.Min(lo[axis], a);
				hi[axis] = Math.Max(hi[axis], b);
			}
		}

		/// <summary>Extinction per local unit at density 1 (the object's Density is an optical depth through one noise size).</summary>
		public float Sigma => owner.Density / MathF.Max(owner.NoiseSize, 0.01f);

		private void BuildOccupancy()
		{
			ShaderProgram program = VolumetricRuntime.OccupancyShader.Program;
			program.Use();
			program.SetTexture("Density", TextureTarget.Texture3D, build.Density, 0);
			program.SetInt3("volResolution", build.Resolution.X, build.Resolution.Y, build.Resolution.Z);
			program.SetInt3("volBlocks", build.Blocks.X, build.Blocks.Y, build.Blocks.Z);
			GL.BindImageTexture(0, build.Occupancy, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R8);
			GL.DispatchCompute((build.Blocks.X + 3) / 4, (build.Blocks.Y + 3) / 4, (build.Blocks.Z + 3) / 4);
			GL.BindImageTexture(0, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R8);
			GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
		}

		// The build is complete: its textures replace the drawn ones.
		private void Complete()
		{
			Build b = build;
			build = null;
			if (b.NewDensity)
			{
				if (Density != b.Density)
				{
					DeleteTexture(ref Density);
				}

				if (Occupancy != b.Occupancy)
				{
					DeleteTexture(ref Occupancy);
				}

				Density = b.Density;
				Occupancy = b.Occupancy;
				Resolution = b.Resolution;
				Blocks = b.Blocks;
				Size = b.Size;
				Shape = b.Shape;
				Voxel = b.Voxel;
				shapeHash = b.ShapeHash;
				drawnDensityKey = b.DensityKey;
				// Read from a file: the CPU's copy is the file's (no readback for DensityAt).
				cpuDensity = b.File?.Density;
				DensityVersion = System.Threading.Interlocked.Increment(ref lastDensityVersion);
			}

			if (b.Relight && Lit != b.Lit)
			{
				DeleteTexture(ref Lit);
				Lit = b.Lit;
				lightHash = b.LightHash;
				sun = b.Sun;
				drawnLitKey = b.LitKey;
			}

			// The editor keeps what the GPU made in its cache (read back once the cloud stayed the drawn one a moment).
			if (b.Made && b.DensityKey != null && VolumeCache.Writable && VolumeCache.Folder != null)
			{
				writeDue = Clock.Elapsed.TotalSeconds + WriteDelay;
			}

			if (b.NewLights || b.NewDensity)
			{
				if (LightsA != b.LightsA)
				{
					KeepSpareLights(ref LightsA, ref LightsB, lightsResolution);
				}

				LightsA = b.LightsA;
				LightsB = b.LightsB;
				lightsResolution = b.LightsResolution;
				transportSettingsHash = TransportSettingsHash();
				lightsHash = b.LightsHash;
				Array.Copy(b.Lights, LightSlots, LightSlots.Length);
				LightCount = b.LightCount;
			}

			// The first cloud of an object (how long it took to show), every build while profiling.
			if (!ready || Profile)
			{
				double ms = (Clock.Elapsed.TotalSeconds - b.Started) * 1000.0;
				long voxels = (long)b.Resolution.X * b.Resolution.Y * b.Resolution.Z;
				string gpu = Profile ? $"; GPU: upload {b.GpuMilliseconds[(int)Phase.Upload]:0.0} ms, generate {b.GpuMilliseconds[(int)Phase.Generate]:0.0} ms, light {b.GpuMilliseconds[(int)Phase.Light]:0.0} ms, lights {b.GpuMilliseconds[(int)Phase.Lights]:0.0} ms" : "";
				string how = b.File != null ? b.UploadLit ? "read from its file" : "read from its file and lit" : b.NewDensity ? "made" : b.Relight ? "lit" : "lights";
				Console.WriteLine($"[Volumetrics] {owner.gameObject?.Name}: {how} {b.Resolution.X}x{b.Resolution.Y}x{b.Resolution.Z} ({voxels / 1e6:0.0} M voxels, {b.LightCount} lights) in {b.Frames} frames, {ms:0} ms{gpu}");
			}

			ready = true;
			if (Debug)
			{
				LogStatistics(b);
			}
		}

		/// <summary>
		///     The editor (VolumeCache.Writable): a cloud the GPU made, still the drawn one WriteDelay later, is read back into
		///     pixel buffers (no stall) and, once the GPU is done with them, written to its file on a worker thread. Skipped
		///     when the cache already holds it.
		/// </summary>
		private void UpdateCacheWrite(double now)
		{
			if (readback != null)
			{
				FinishReadback();
				return;
			}

			if (writeDue < 0.0 || now < writeDue || build != null || !ready || Density == 0 || Lit == 0 || drawnDensityKey == null)
			{
				return;
			}

			writeDue = -1.0;
			if (VolumeCache.HasFile(drawnDensityKey, drawnLitKey, sun))
			{
				return;
			}

			long voxels = (long)Resolution.X * Resolution.Y * Resolution.Z;
			readback = new Readback { DensityKey = drawnDensityKey, LitKey = drawnLitKey, Sun = sun, Resolution = Resolution };
			readback.DensityBuffer = ReadInto(Density, voxels, PixelFormat.Red);
			readback.LitBuffer = drawnLitKey != null ? ReadInto(Lit, voxels * 4, PixelFormat.Rgba) : 0;
			readback.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
		}

		// Level 0 of a texture copied into a new pixel buffer by the GPU (asynchronously).
		private static int ReadInto(int texture, long bytes, PixelFormat format)
		{
			int buffer = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.PixelPackBuffer, buffer);
			GL.BufferData(BufferTarget.PixelPackBuffer, (IntPtr)bytes, IntPtr.Zero, BufferUsageHint.StreamRead);
			GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.GetTexImage(TextureTarget.Texture3D, 0, format, PixelType.UnsignedByte, IntPtr.Zero);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
			GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
			return buffer;
		}

		// The readback's buffers once the GPU filled them: copied out, the file written on a worker thread.
		private void FinishReadback()
		{
			WaitSyncStatus status = GL.ClientWaitSync(readback.Fence, ClientWaitSyncFlags.None, 0);
			if (status == WaitSyncStatus.TimeoutExpired)
			{
				return;
			}

			Readback r = readback;
			readback = null;
			byte[] density = null;
			byte[] lit = null;
			if (status != WaitSyncStatus.WaitFailed)
			{
				long voxels = (long)r.Resolution.X * r.Resolution.Y * r.Resolution.Z;
				density = MapOut(r.DensityBuffer, voxels);
				lit = r.LitBuffer != 0 ? MapOut(r.LitBuffer, voxels * 4) : null;
			}

			DeleteReadback(r);
			if (density == null)
			{
				return;
			}

			// Still the drawn cloud: its CPU copy for DensityAt comes free.
			if (cpuDensity == null && drawnDensityKey == r.DensityKey && Resolution == r.Resolution)
			{
				cpuDensity = density;
			}

			VolumeCache.Write(new VolumeFile { DensityKey = r.DensityKey, LitKey = lit != null ? r.LitKey : null, Sun = r.Sun, Resolution = r.Resolution, Density = density, Lit = lit });
		}

		private static byte[] MapOut(int buffer, long bytes)
		{
			GL.BindBuffer(BufferTarget.PixelPackBuffer, buffer);
			IntPtr mapped = GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, (IntPtr)bytes, MapBufferAccessMask.MapReadBit);
			byte[] data = null;
			if (mapped != IntPtr.Zero)
			{
				data = new byte[bytes];
				System.Runtime.InteropServices.Marshal.Copy(mapped, data, 0, (int)bytes);
				GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
			}

			GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
			return data;
		}

		private static void DeleteReadback(Readback r)
		{
			if (r.DensityBuffer != 0) GL.DeleteBuffer(r.DensityBuffer);
			if (r.LitBuffer != 0) GL.DeleteBuffer(r.LitBuffer);
			if (r.Fence != IntPtr.Zero) GL.DeleteSync(r.Fence);
		}

		/// <summary>LEL_VOL_DEBUG=1: every completed build logs its grid and how much cloud it holds.</summary>
		private static readonly bool Debug = Environment.GetEnvironmentVariable("LEL_VOL_DEBUG") == "1";

		private void LogStatistics(Build b)
		{
			DensityAt(Vector3.Zero);
			long cloud = 0;
			long sum = 0;
			int densest = 0;
			foreach (byte value in cpuDensity)
			{
				sum += value;
				densest = Math.Max(densest, value);
				if (value > 2)
				{
					cloud++;
				}
			}

			Console.WriteLine($"[Volumetrics] {owner.gameObject?.Name}: {(b.NewDensity ? "made" : b.Relight ? "lit" : "lights")} {Resolution.X}x{Resolution.Y}x{Resolution.Z} voxels of {Voxel:0.###}, cloud in {100.0 * cloud / cpuDensity.Length:0.0} %, mean density {sum / 255.0 / cpuDensity.Length:0.000}, densest {densest / 255.0:0.00}, sun (local) {sun}, lights {LightCount}");
		}

		// Drops the build in progress and the textures it made (those it shares with the drawn ones stay).
		private void Abandon()
		{
			if (build == null)
			{
				return;
			}

			Build b = build;
			build = null;
			if (b.Density != Density)
			{
				DeleteTexture(ref b.Density);
			}

			if (b.Occupancy != Occupancy)
			{
				DeleteTexture(ref b.Occupancy);
			}

			if (b.Lit != Lit)
			{
				DeleteTexture(ref b.Lit);
			}

			if (b.LightsA != LightsA)
			{
				KeepSpareLights(ref b.LightsA, ref b.LightsB, b.LightsResolution);
			}
		}

		// A pair of transport textures no longer drawn: kept as the spare when there is none, else deleted.
		private void KeepSpareLights(ref int a, ref int b, Vector3i resolution)
		{
			if (a == 0)
			{
				return;
			}

			if (spareLightsA == 0)
			{
				spareLightsA = a;
				spareLightsB = b;
				spareLightsResolution = resolution;
				a = 0;
				b = 0;
				return;
			}

			DeleteTexture(ref a);
			DeleteTexture(ref b);
		}

		private static int CreateTexture(SizedInternalFormat format, Vector3i size, bool mipmaps, bool zeroBorder)
		{
			int levels = mipmaps ? 1 + (int)MathF.Floor(MathF.Log2(Math.Max(size.X, Math.Max(size.Y, size.Z)))) : 1;
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexStorage3D(TextureTarget3d.Texture3D, levels, format, size.X, size.Y, size.Z);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)(mipmaps ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Nearest));
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)(mipmaps ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
			var wrap = (int)(zeroBorder ? TextureWrapMode.ClampToBorder : TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, wrap);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, wrap);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, wrap);
			if (zeroBorder)
			{
				GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureBorderColor, new[] { 0f, 0f, 0f, 0f });
			}

			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, levels - 1);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		private static void GenerateMipmaps(int texture)
		{
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
			GL.BindTexture(TextureTarget.Texture3D, 0);
		}

		private static void DeleteTexture(ref int texture)
		{
			if (texture != 0)
			{
				GL.DeleteTexture(texture);
				texture = 0;
			}
		}

		#endregion
	}
}
