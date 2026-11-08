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
			Lights
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

		#endregion

		#region PublicMethods

		/// <summary>Makes everything again on the next update, without waiting for the settings to settle.</summary>
		public void Invalidate()
		{
			invalidated = true;
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
					pendingSince = -1.0;
				}
			}
			else if (build == null && ready)
			{
				// The same cloud: light it again when the sun turned against it or the light settings changed, and work the
				// lights inside out again when they moved.
				bool relight = newLightHash != lightHash || Vector3.Dot(newSun, sun) < 0.99996f;
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

			return build != null ? Advance(budget) : 0;
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
				cpuDensity = new byte[Resolution.X * Resolution.Y * Resolution.Z];
				GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
				GL.BindTexture(TextureTarget.Texture3D, Density);
				GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
				GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.UnsignedByte, cpuDensity);
				GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
				GL.BindTexture(TextureTarget.Texture3D, 0);
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
			ready = false;
			Abandon();
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
				Sun = newSun
			};
			build.Density = CreateTexture(SizedInternalFormat.R8, resolution, true, true);
			build.Occupancy = CreateTexture(SizedInternalFormat.R8, build.Blocks, false, false);
			build.Lit = CreateTexture(SizedInternalFormat.Rgba8, resolution, true, false);
			CaptureLights(build);
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
				Occupancy = Occupancy
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
				Lit = Lit
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
			while (build != null && used < budget)
			{
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

						used += DispatchSlices(VolumetricRuntime.LightTransport, build.LightsResolution, budget - used, SetLightsUniforms, build.LightsA, SizedInternalFormat.Rgba16f, build.LightsB, 1);
						if (build.NextSlice >= build.LightsResolution.Z)
						{
							GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
							GenerateMipmaps(build.LightsA);
							GenerateMipmaps(build.LightsB);
							NextPhase(Phase.Idle);
						}

						break;
					default:
						Complete();
						break;
				}
			}

			return used;
		}

		private void NextPhase(Phase phase)
		{
			build.Phase = phase;
			build.NextSlice = 0;
		}

		// Dispatches as many z slices as the budget allows (at least one), each dispatch a short one.
		private long DispatchSlices(ComputeShader shader, Vector3i resolution, long budget, Action<ShaderProgram> setUniforms, int image, SizedInternalFormat format, int secondImage, int secondBinding)
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
			long used = 0;
			const long DispatchVoxels = 1 << 20;
			while (build.NextSlice < resolution.Z && (used == 0 || used < budget))
			{
				int slices = (int)Math.Clamp(DispatchVoxels / Math.Max(1, perSlice), 1, resolution.Z - build.NextSlice);
				program.SetInt("volSliceOffset", build.NextSlice);
				GL.DispatchCompute((resolution.X + 7) / 8, (resolution.Y + 7) / 8, slices);
				build.NextSlice += slices;
				used += slices * perSlice;
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
				cpuDensity = null;
			}

			if (b.Relight && Lit != b.Lit)
			{
				DeleteTexture(ref Lit);
				Lit = b.Lit;
				lightHash = b.LightHash;
				sun = b.Sun;
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

			ready = true;
			if (Debug)
			{
				LogStatistics(b);
			}
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
