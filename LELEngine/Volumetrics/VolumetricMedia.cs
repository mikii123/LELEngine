using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Volumetrics
{
	/// <summary>
	///     The volumetric objects in the GI, for one renderer (<see cref="Rendering.Passes.VolumetricMediaPass" />): a coarse
	///     world-aligned volume of what the clouds scatter and hide (a cube of rgba16f voxels: rgb the light sent out
	///     along a path across one voxel, premultiplied by a, how much of that path the clouds cover; and a second
	///     texture of the directional lights' part of that light, which a cloud sends mostly on forward), made again every
	///     frame from the clouds as they are lit right now (Engine/Volumetrics/VolumetricMedia.shader, one dispatch per
	///     cloud over the voxels its box covers), its levels put together from it (VolumetricMediaMip.shader: every
	///     level's voxel is the clouds in it as they look from outside, from each of the six sides, with how much they
	///     cover along each axis, taken from columns of the finest voxels put together front to back,
	///     VolumetricMediaColumns.shader; so a level covers and shows what its clouds do, and a thick cloud lit from one
	///     side is bright from there and dark from behind), and published as <see cref="Lighting.Media" />. The GI's cones
	///     (voxel cone tracing) and rays (Lumen's screen probes and radiosity) read it on their way (Engine/GIMedia.glsl),
	///     so a cloud lights the surfaces around it and hides the sky and the light behind it.
	///     It covers the clouds within <see cref="GlobalIlluminationSettings.VolumeMediaReach" /> GI volumes around the GI
	///     volume's centre, with <see cref="GlobalIlluminationSettings.VolumeMediaResolution" /> voxels along the longest
	///     side of that: cones that leave the GI volume go on through it, so clouds far larger than the GI volume count
	///     whole. The voxels are fixed in the world (the volume moves in whole voxels with a GI volume that follows the
	///     camera) and their size changes in steps, so the light does not swim as the camera or a cloud moves. The part
	///     in use sits in the middle of the texture, so its coarsest levels are centred on the clouds.
	///     LEL_MEDIA_LOOKS=1 logs every 300th frame how the clouds look from each side, from the finest voxels (on the
	///     CPU) and from the coarsest level: the two must agree.
	/// </summary>
	internal sealed class VolumetricMedia : IDisposable
	{
		#region PublicFields

		/// <summary>Voxels the last frame's volume has in use (0: no cloud contributes), and the clouds in it.</summary>
		public Vector3i Used { get; private set; }

		public int CloudCount { get; private set; }

		/// <summary>World units per voxel of the last frame's volume.</summary>
		public float Voxel { get; private set; }

		#endregion

		#region PrivateFields

		// The first level whose looks are taken from columns (the one above the finest is exact from its eight voxels).
		private const int FirstColumnsLevel = 2;

		private static ComputeShader shader;
		private static ComputeShader mipShader;
		private static ComputeShader columnsShader;

		private readonly List<(VolumetricObjectBase volume, Vector3 min, Vector3 max)> clouds = new List<(VolumetricObjectBase, Vector3, Vector3)>();
		private int texture;
		private int sunTexture;

		// The light's luminance as the clouds look from the + sides of x, y, z and from the - sides, and in a how much
		// they cover along x (plus) and along y (minus): only the levels above the finest are written and read (a voxel
		// of the finest is its cloud at one point).
		private int facePlusTexture;
		private int faceMinusTexture;

		// The columns the levels' looks are the means of (VolumetricMediaColumns.shader), a level's made from the level
		// below's: the even levels' in one texture, the odd ones' in the other (side x side across, three axes of
		// side >> level slices along).
		private int columnsEven;
		private int columnsOdd;
		private Vector3i size;
		private bool logged;

		private static readonly bool LogLooks = Environment.GetEnvironmentVariable("LEL_MEDIA_LOOKS") == "1";
		private int looksFrame;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Makes this frame's volume for the view's GI and publishes it (an inactive binding when the scene has no GI,
		///     the project switched the volumes off, or no cloud that contributes lies within reach).
		/// </summary>
		public void Update(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			Lighting.Media = default;
			Used = Vector3i.Zero;
			CloudCount = 0;
			if (!gi.Enabled || gi.VolumesInGI == VolumeGIMode.Off || Lighting.MediaFaceMinusTextureUnit >= GLCapabilities.MaxTextureUnits || VolumetricRuntime.Objects.Count == 0)
			{
				Release();
				return;
			}

			// The clouds that contribute (on the lighting camera's layers, as every GI contributor), their boxes in the world.
			clouds.Clear();
			var unionMin = new Vector3(float.MaxValue);
			var unionMax = new Vector3(float.MinValue);
			foreach (VolumetricObjectBase volume in VolumetricRuntime.Objects)
			{
				if (!volume.ContributeGI || !volume.isActiveAndEnabled || !volume.Ready || (context.CullingMask & (1 << (volume.gameObject.layer & 31))) == 0)
				{
					continue;
				}

				WorldBox(volume, out Vector3 min, out Vector3 max);
				clouds.Add((volume, min, max));
				unionMin = Vector3.ComponentMin(unionMin, min);
				unionMax = Vector3.ComponentMax(unionMax, max);
			}

			if (clouds.Count == 0)
			{
				Release();
				return;
			}

			// The reach: a cube around the GI volume's centre (this frame's: the volume may follow the camera).
			gi.UpdateGridPlacement(context.CameraPosition, context.CameraForward);
			float reach = gi.GridSize * Math.Clamp(gi.VolumeMediaReach, 1f, 64f);
			Vector3 center = gi.GridMin + new Vector3(gi.GridSize * 0.5f);
			Vector3 lo = Vector3.ComponentMax(unionMin, center - new Vector3(reach * 0.5f));
			Vector3 hi = Vector3.ComponentMin(unionMax, center + new Vector3(reach * 0.5f));
			if (hi.X <= lo.X || hi.Y <= lo.Y || hi.Z <= lo.Z)
			{
				// Every cloud is out of reach: the texture stays for when one comes back.
				return;
			}

			// The voxel: the longest side of what can be covered (the clouds' box, at most the reach) over the resolution,
			// rounded up to a quarter of a power of two, so a cloud that turns or grows a little keeps its voxels. Over
			// one voxel less than the resolution: the texture needs a voxel more than what is covered (below), and with
			// all of the resolution in use that one more doubled its side (eight times the memory and the work, for one
			// in ten sizes of cloud).
			Vector3 span = Vector3.ComponentMin(unionMax - unionMin, new Vector3(reach));
			int resolution = Math.Clamp(gi.VolumeMediaResolution, 8, 128);
			float raw = MathF.Max(MathF.Max(span.X, MathF.Max(span.Y, span.Z)) / (resolution - 1), 1e-4f);
			float voxel = MathF.Pow(2f, MathF.Ceiling(MathF.Log2(raw) * 4f - 1e-3f) / 4f);

			// The texture holds what can be covered wherever the reach's cube is (a voxel more: the grid is fixed in the
			// world). A cube, its side a power of two: every level then halves the one below exactly (the levels are put
			// together from pairs of voxels) and its voxels are cubes at every level, up to the one voxel that is the
			// whole volume (the reads count on a level's voxels being as long as they are wide: with a flat volume a
			// path through its thin side counted for a fraction of what it crossed). This frame's part starts on the
			// grid at or below the covered box's corner.
			int side = PowerOfTwo(Math.Max(Cells(span.X, voxel), Math.Max(Cells(span.Y, voxel), Cells(span.Z, voxel))) + 1);
			var needed = new Vector3i(side, side, side);
			if (texture == 0 || needed != size)
			{
				VolumetricData.DeleteTexture(ref texture);
				VolumetricData.DeleteTexture(ref sunTexture);
				VolumetricData.DeleteTexture(ref facePlusTexture);
				VolumetricData.DeleteTexture(ref faceMinusTexture);
				VolumetricData.DeleteTexture(ref columnsEven);
				VolumetricData.DeleteTexture(ref columnsOdd);
				// Nothing around the textures (a border of zeros): reads past their faces fade out, never repeat the last voxels.
				texture = VolumetricData.CreateTexture(SizedInternalFormat.Rgba16f, needed, true, true);
				sunTexture = VolumetricData.CreateTexture(SizedInternalFormat.Rgba16f, needed, true, true);
				facePlusTexture = VolumetricData.CreateTexture(SizedInternalFormat.Rgba16f, needed, true, true);
				faceMinusTexture = VolumetricData.CreateTexture(SizedInternalFormat.Rgba16f, needed, true, true);
				// Their finest level is never written: nothing undefined in it (it is not read either).
				GL.ClearTexImage(facePlusTexture, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
				GL.ClearTexImage(faceMinusTexture, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
				columnsEven = CreateColumns(side, FirstColumnsLevel);
				columnsOdd = CreateColumns(side, FirstColumnsLevel + 1);
				size = needed;
				logged = false;
			}

			var usedMin = new Vector3(MathF.Floor(lo.X / voxel) * voxel, MathF.Floor(lo.Y / voxel) * voxel, MathF.Floor(lo.Z / voxel) * voxel);
			var used = new Vector3i(
				Math.Clamp((int)MathF.Ceiling((hi.X - usedMin.X) / voxel - 1e-3f), 1, size.X),
				Math.Clamp((int)MathF.Ceiling((hi.Y - usedMin.Y) / voxel - 1e-3f), 1, size.Y),
				Math.Clamp((int)MathF.Ceiling((hi.Z - usedMin.Z) / voxel - 1e-3f), 1, size.Z));

			// The part in use sits in the middle of the texture (whole voxels from its corner: the grid stays the
			// world's): the coarsest levels' voxels are then centred on the clouds. With the clouds in the texture's
			// corner the one voxel of the coarsest level stood up to half the texture away from them, and a wide cone
			// met a flat storm at the far end of it too early, where it was narrower (2.4 times the storm's light).
			var usedStart = new Vector3i((size.X - used.X) / 2, (size.Y - used.Y) / 2, (size.Z - used.Z) / 2);
			Vector3 min3 = usedMin - new Vector3(usedStart.X, usedStart.Y, usedStart.Z) * voxel;

			// Empty, then every cloud added over the voxels its box covers.
			GL.ClearTexImage(texture, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.ClearTexImage(sunTexture, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			shader ??= new ComputeShader("Engine/Volumetrics/VolumetricMedia.shader");
			ShaderProgram program = shader.Program;
			shader.Use();
			// Every sampler on a unit of its own (unused ones too: two sampler types on one unit fail the dispatch).
			program.SetInt("ShadowMap", Lighting.ShadowMapTextureUnit);
			program.SetInt("ShadowTransmittance", Lighting.ShadowTransmittanceTextureUnit);
			program.SetInt("giSkybox", Lighting.SkyboxTextureUnit);
			program.SetVector3("mediaMin", min3);
			program.SetFloat("mediaVoxel", voxel);
			GL.BindImageTexture(0, texture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, sunTexture, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
			int written = 0;
			foreach ((VolumetricObjectBase volume, Vector3 min, Vector3 max) in clouds)
			{
				var first = new Vector3i(
					Math.Clamp((int)MathF.Floor((min.X - min3.X) / voxel), usedStart.X, usedStart.X + used.X),
					Math.Clamp((int)MathF.Floor((min.Y - min3.Y) / voxel), usedStart.Y, usedStart.Y + used.Y),
					Math.Clamp((int)MathF.Floor((min.Z - min3.Z) / voxel), usedStart.Z, usedStart.Z + used.Z));
				var end = new Vector3i(
					Math.Clamp((int)MathF.Ceiling((max.X - min3.X) / voxel), usedStart.X, usedStart.X + used.X),
					Math.Clamp((int)MathF.Ceiling((max.Y - min3.Y) / voxel), usedStart.Y, usedStart.Y + used.Y),
					Math.Clamp((int)MathF.Ceiling((max.Z - min3.Z) / voxel), usedStart.Z, usedStart.Z + used.Z));
				if (end.X <= first.X || end.Y <= first.Y || end.Z <= first.Z)
				{
					continue;
				}

				VolumetricData data = volume.Data;
				Matrix4 worldToLocal = volume.transform.LocalToWorld.Inverted();
				float worldScale = volume.WorldScale;
				VolumetricRuntime.SetCloudLightUniforms(program, volume, data, worldToLocal, true);
				VolumetricRuntime.SetDetailUniforms(program, volume);
				program.SetFloat("volPhaseG", Math.Clamp(volume.PhaseG, -0.95f, 0.95f));
				program.SetMatrix4("volWorldToLocal", worldToLocal);
				program.SetMatrix4("volLocalToWorld", volume.transform.LocalToWorld);
				program.SetVector3("volSize", data.Size);
				program.SetFloat("volVoxelWorld", data.Voxel * worldScale);
				program.SetFloat("volWorldScale", worldScale);
				program.SetFloat("volExposure", volume.Exposure);
				program.SetFloat("volOpacity", Math.Clamp(volume.Opacity, 0f, 1f));
				program.SetFloat("volGIContribution", MathF.Max(volume.GIContribution, 0f));
				program.SetInt3("mediaFirst", first.X, first.Y, first.Z);
				program.SetInt3("mediaEnd", end.X, end.Y, end.Z);
				GL.DispatchCompute((end.X - first.X + 3) / 4, (end.Y - first.Y + 3) / 4, (end.Z - first.Z + 3) / 4);
				// The next cloud adds to what this one wrote.
				GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
				written++;
			}

			GL.BindImageTexture(0, 0, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, 0, 0, true, 0, TextureAccess.ReadWrite, SizedInternalFormat.Rgba16f);
			VolumetricRuntime.UnbindCloudTextures();
			if (written == 0)
			{
				GL.UseProgram(0);
				return;
			}

			// The levels above: each voxel the clouds in it as they look from outside (VolumetricMediaMip.shader), not a
			// box filter: what a level covers and shows must be what the clouds in it cover and show. From the third
			// level on the looks are the means of that level's columns (VolumetricMediaColumns.shader), made first.
			GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
			context.Renderer.Profiler.Split("VolumeMedia.levels");
			mipShader ??= new ComputeShader("Engine/Volumetrics/VolumetricMediaMip.shader");
			columnsShader ??= new ComputeShader("Engine/Volumetrics/VolumetricMediaColumns.shader");
			ShaderProgram mips = mipShader.Program;
			ShaderProgram columns = columnsShader.Program;
			int levels = 1 + (int)MathF.Floor(MathF.Log2(Math.Max(size.X, Math.Max(size.Y, size.Z))));
			Vector3i source = size;
			for (int level = 0; level < levels - 1; level++)
			{
				int made = level + 1;
				int madeColumns = made % 2 == 0 ? columnsEven : columnsOdd;
				int across = ColumnsAcross(size.X, made);
				if (made >= FirstColumnsLevel)
				{
					columnsShader.Use();
					columns.SetTexture("Media", TextureTarget.Texture3D, texture, 0);
					columns.SetTexture("Columns", TextureTarget.Texture3D, made % 2 == 0 ? columnsOdd : columnsEven, 1);
					columns.SetInt("level", made);
					columns.SetInt("firstLevel", FirstColumnsLevel);
					columns.SetInt("side", size.X);
					columns.SetInt("across", across);
					columns.SetInt("acrossBelow", ColumnsAcross(size.X, made - 1));
					GL.BindImageTexture(0, madeColumns, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
					GL.DispatchCompute((across + 7) / 8, (across + 7) / 8, (size.X >> made) * 3);
					GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
				}

				var target = new Vector3i(Math.Max(source.X / 2, 1), Math.Max(source.Y / 2, 1), Math.Max(source.Z / 2, 1));
				Vector3i columnsSize = ColumnsSize(size.X, made % 2 == 0 ? FirstColumnsLevel : FirstColumnsLevel + 1);
				mipShader.Use();
				mips.SetTexture("Media", TextureTarget.Texture3D, texture, 0);
				mips.SetTexture("MediaSun", TextureTarget.Texture3D, sunTexture, 1);
				mips.SetTexture("Columns", TextureTarget.Texture3D, madeColumns, 2);
				mips.SetInt("sourceLevel", level);
				mips.SetInt3("sourceSize", source.X, source.Y, source.Z);
				mips.SetInt("side", size.X);
				mips.SetInt("columnsPerVoxel", Math.Max(across / Math.Max(size.X >> made, 1), 2));
				mips.SetVector3("columnsSize", new Vector3(columnsSize.X, columnsSize.Y, columnsSize.Z));
				GL.BindImageTexture(0, texture, made, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(1, sunTexture, made, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(2, facePlusTexture, made, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.BindImageTexture(3, faceMinusTexture, made, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
				GL.DispatchCompute((target.X + 3) / 4, (target.Y + 3) / 4, (target.Z + 3) / 4);
				GL.MemoryBarrier(MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.ShaderImageAccessBarrierBit);
				source = target;
			}

			GL.BindImageTexture(0, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(1, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(2, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.BindImageTexture(3, 0, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			GL.ActiveTexture(TextureUnit.Texture2);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			GL.ActiveTexture(TextureUnit.Texture1);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			GL.UseProgram(0);

			Used = used;
			CloudCount = written;
			Voxel = voxel;
			Lighting.Media = new Lighting.MediaBinding
			{
				Active = true,
				Texture = texture,
				SunTexture = sunTexture,
				FacePlusTexture = facePlusTexture,
				FaceMinusTexture = faceMinusTexture,
				Min = min3,
				Used = usedMin,
				InvSize = new Vector3(1f / (size.X * voxel), 1f / (size.Y * voxel), 1f / (size.Z * voxel)),
				Extent = new Vector3(used.X * voxel, used.Y * voxel, used.Z * voxel),
				Voxel = voxel,
				MaxLod = MathF.Floor(MathF.Log2(Math.Max(size.X, Math.Max(size.Y, size.Z))))
			};
			if (LogLooks && ++looksFrame % 300 == 0)
			{
				LogTopLooks(levels);
			}

			if (!logged)
			{
				logged = true;
				// Four rgba16f textures with their levels, and the columns (half the side across; three quarters and three
				// eighths of it along).
				double megabytes = size.X * (long)size.Y * size.Z * (4.0 * 8.0 * 8.0 / 7.0 + 0.28125 * 8.0) / (1024.0 * 1024.0);
				Console.WriteLine(FormattableString.Invariant($"[Volumetrics] GI media volume: {size.X}x{size.Y}x{size.Z} voxels of {voxel:0.###} units ({megabytes:0.0} MB), {written} clouds"));
			}
		}

		/// <summary>Frees the volume (the GI no longer reads clouds).</summary>
		public void Release()
		{
			if (texture != 0)
			{
				VolumetricData.DeleteTexture(ref texture);
				VolumetricData.DeleteTexture(ref sunTexture);
				VolumetricData.DeleteTexture(ref facePlusTexture);
				VolumetricData.DeleteTexture(ref faceMinusTexture);
				VolumetricData.DeleteTexture(ref columnsEven);
				VolumetricData.DeleteTexture(ref columnsOdd);
				size = Vector3i.Zero;
				Console.WriteLine("[Volumetrics] GI media volume released");
			}
		}

		public void Dispose()
		{
			Release();
		}

		#endregion

		#region PrivateMethods

		// Diagnostic (LEL_MEDIA_LOOKS=1, every 300th frame): how the clouds look from each of the six sides, worked out
		// from the finest level on the CPU (every column of voxels put together front to back, the mean over the face),
		// next to what the levels made of it (the one voxel of the coarsest level). The two must agree for a wide cone
		// to see a cloud as a camera on that side does.
		private void LogTopLooks(int levels)
		{
			int n = size.X;
			var fine = new float[n * n * n * 4];
			GL.GetTextureImage(texture, 0, PixelFormat.Rgba, PixelType.Float, fine.Length * sizeof(float), fine);
			var truth = new double[6];
			var covered = new double[3];
			for (int axis = 0; axis < 3; axis++)
			{
				int u = (axis + 1) % 3;
				int v = (axis + 2) % 3;
				var c = new int[3];
				for (int j = 0; j < n; j++)
				{
					for (int i = 0; i < n; i++)
					{
						c[u] = i;
						c[v] = j;
						for (int side = 0; side < 2; side++)
						{
							// side 0: seen from the + end (the last voxel in front), 1: from the - end.
							double through = 1.0;
							double light = 0.0;
							for (int k = 0; k < n; k++)
							{
								c[axis] = side == 0 ? n - 1 - k : k;
								int at = ((c[2] * n + c[1]) * n + c[0]) * 4;
								light += through * (0.2126 * fine[at] + 0.7152 * fine[at + 1] + 0.0722 * fine[at + 2]);
								through *= 1.0 - Math.Min(fine[at + 3], 1f);
							}

							truth[axis * 2 + side] += light;
							if (side == 0)
							{
								covered[axis] += 1.0 - through;
							}
						}
					}
				}
			}

			double faces = n * (double)n;
			var top = new float[4];
			var plus = new float[4];
			var minus = new float[4];
			GL.GetTextureImage(texture, levels - 1, PixelFormat.Rgba, PixelType.Float, top.Length * sizeof(float), top);
			GL.GetTextureImage(facePlusTexture, levels - 1, PixelFormat.Rgba, PixelType.Float, plus.Length * sizeof(float), plus);
			GL.GetTextureImage(faceMinusTexture, levels - 1, PixelFormat.Rgba, PixelType.Float, minus.Length * sizeof(float), minus);
			double trueMean = (truth[0] + truth[1] + truth[2] + truth[3] + truth[4] + truth[5]) / 6.0 / faces;
			double madeMean = (plus[0] + plus[1] + plus[2] + minus[0] + minus[1] + minus[2]) / 6.0;
			Console.WriteLine(FormattableString.Invariant(
				$"[Volumetrics] GI media looks +x -x +y -y +z -z: from the voxels {truth[0] / faces:0.00000} {truth[1] / faces:0.00000} {truth[2] / faces:0.00000} {truth[3] / faces:0.00000} {truth[4] / faces:0.00000} {truth[5] / faces:0.00000} (mean {trueMean:0.00000}, covered {covered[0] / faces:0.000} {covered[1] / faces:0.000} {covered[2] / faces:0.000}); from the levels {plus[0]:0.00000} {minus[0]:0.00000} {plus[1]:0.00000} {minus[1]:0.00000} {plus[2]:0.00000} {minus[2]:0.00000} (mean {madeMean:0.00000}, light {0.2126 * top[0] + 0.7152 * top[1] + 0.0722 * top[2]:0.00000}, covered {plus[3]:0.000} {minus[3]:0.000} {3.0 * top[3] - plus[3] - minus[3]:0.000})"));
		}

		// A texture of columns for the levels firstLevel, firstLevel + 2, ...: sized for the first of them (the later
		// ones use a corner of it). Its own filter averages four columns a fetch (no levels of its own).
		private static int CreateColumns(int side, int firstLevel)
		{
			int columns = VolumetricData.CreateTexture(SizedInternalFormat.Rgba16f, ColumnsSize(side, firstLevel), false, false);
			GL.TextureParameter(columns, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TextureParameter(columns, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			return columns;
		}

		// A level's columns along a side of the volume: two voxels of the finest level wide, until a voxel of the level
		// would have more than sixteen of them a side (the levels' voxels are the means of their columns, fetched in
		// one thread a voxel: thousands of them for the coarsest level's one voxel took longer than all the rest; with
		// eight a side a flat storm's dark side came out a third too bright, seen along its thin side).
		private static int ColumnsAcross(int side, int level)
		{
			return Math.Max(Math.Min(side / 2, 16 * (side >> level)), 1);
		}

		// Texels of the texture that holds a level's columns: across, across, and the three axes' slices along.
		private static Vector3i ColumnsSize(int side, int level)
		{
			int across = ColumnsAcross(side, level);
			return new Vector3i(across, across, Math.Max((side >> level) * 3, 1));
		}

		private static int Cells(float length, float voxel)
		{
			return Math.Max(1, (int)MathF.Ceiling(length / voxel - 1e-3f));
		}

		private static int PowerOfTwo(int count)
		{
			int size = 1;
			while (size < count)
			{
				size <<= 1;
			}

			return size;
		}

		// The world box around a cloud's shape (its local box's eight corners).
		private static void WorldBox(VolumetricObjectBase volume, out Vector3 min, out Vector3 max)
		{
			Matrix4 localToWorld = volume.transform.LocalToWorld;
			Vector3 half = volume.Data.Size * 0.5f;
			min = new Vector3(float.MaxValue);
			max = new Vector3(float.MinValue);
			for (int i = 0; i < 8; i++)
			{
				var corner = new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z);
				Vector3 world = Vector3.TransformPosition(corner, localToWorld);
				min = Vector3.ComponentMin(min, world);
				max = Vector3.ComponentMax(max, world);
			}
		}

		#endregion
	}
}
