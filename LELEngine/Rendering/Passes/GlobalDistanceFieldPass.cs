using System;
using System.Collections.Generic;
using LELEngine.Rendering.Lumen;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Builds the global signed distance field (Lumen's "global SDF"): a world-space R16F volume over the
	///     GI grid holding the distance to the nearest surface, clamped to a band of a few voxels.
	///
	///     Objects come from <see cref="LumenScene" /> (mesh distance fields built once per mesh and scale).
	///     Static objects are composed into a cached field when they change; dynamic objects are re-composed
	///     every frame, but only inside their band-padded bounds (previous and current), which is exact
	///     because the band limits how far an object can influence the field.
	///
	///     A volume that follows the camera moves in whole voxels. The fields then scroll: they are stored toroidally
	///     (grid voxel v in texel (v + scroll) mod resolution, see Engine/DistanceField.glsl), so everything still inside
	///     stays where it is and only the slabs that entered are composed (a full rebuild costs a few ms and would happen
	///     nearly every frame of fast flight).
	///
	///     Consumers: Engine/DistanceField.glsl (sphere tracing) through Lighting.SetSdfUniforms.
	/// </summary>
	public sealed class GlobalDistanceFieldPass : RenderPass
	{
		#region PublicFields

		public override string Name => "GlobalSDF";

		public override RenderPassScope Scope => RenderPassScope.Frame;
		public int GlobalSdf => globalSdf;

		#endregion

		#region PrivateFields

		private ComputeShader compose;
		private int staticSdf;
		private int globalSdf;
		private int staticIds;
		private int globalIds;
		private int currentResolution;

		// Where grid voxel 0 is stored (toroidal), and the move in voxels waiting to be applied this frame.
		private Vector3i scroll;
		private Vector3i pendingScroll;
		private readonly List<VoxelRegion> scrollRegions = new List<VoxelRegion>();

		private List<VoxelRegion> previousRegions = new List<VoxelRegion>();
		private List<VoxelRegion> currentRegions = new List<VoxelRegion>();
		private readonly List<VoxelRegion> updateRegions = new List<VoxelRegion>();

		private const float FullUpdateCoverage = 0.5f;

		private Vector3 lastGridMin;
		private float lastGridSize;
		private int lastStaticCount = -1;
		private int frameIndex;
		private bool dynamicPending = true;

		// Static field state of this pass (each renderer has its own), see GlobalIlluminationSettings.StaticVersion.
		private bool staticDirty = true;
		private int builtStaticVersion = -1;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			// Made when the GI first needs the field, freed when it stops (GI off, voxel cones without the SDF).
			if (!gi.DistanceFieldActive)
			{
				Release();
				gi.GlobalSdf = 0;
				gi.GlobalSdfObjectIds = 0;
				// Lumen without the distance field still has its surface cache in the scene.
				if (!gi.LumenActive)
				{
					context.Renderer.ReleaseLumenScene();
				}
				return;
			}

			compose ??= new ComputeShader("Engine/SdfCompose.shader");
			if (globalSdf == 0 || gi.SdfResolution != currentResolution)
			{
				CreateVolumes(gi.SdfResolution);
			}

			gi.UpdateGridPlacement(context.CameraPosition, context.CameraForward);

			LumenScene scene = context.Renderer.LumenScene;
			frameIndex++;
			scene.Update(context.Renderer.FrameIndex, context.GIRenderers ?? context.Renderers, gi);
			DetectStaticChanges(gi, scene);

			// The dynamic composite is a pure function of object placement: skip it while nothing moved. A move is
			// latched until a due frame, so an update interval > 1 cannot drop a change that happened in between.
			dynamicPending |= scene.DynamicChanged;
			bool dynamicDue = dynamicPending && (gi.DynamicUpdateInterval <= 1 || frameIndex % gi.DynamicUpdateInterval == 0);
			GpuProfiler profiler = context.Renderer.Profiler;

			if (staticDirty || gi.StaticVersion != builtStaticVersion)
			{
				profiler.Split("GlobalSDF.static");
				RebuildStaticField(gi, scene);
				// The whole working field was replaced: dynamic objects must be stamped again this frame.
				dynamicDue = true;
			}
			else if (pendingScroll != Vector3i.Zero)
			{
				profiler.Split("GlobalSDF.scroll");
				ScrollStaticField(gi, scene);
				dynamicDue = true;
			}

			pendingScroll = Vector3i.Zero;
			gi.SdfScroll = scroll;

			if (dynamicDue)
			{
				profiler.Split("GlobalSDF.dynamic");
				UpdateDynamicField(gi, scene);
				dynamicPending = false;
			}

			gi.GlobalSdf = globalSdf;
			gi.GlobalSdfObjectIds = globalIds;
		}

		public override void ReleaseUnusedResources()
		{
			if (!Lighting.GI.DistanceFieldActive)
			{
				Release();
			}
		}

		public override void Dispose()
		{
			Release();
			Lighting.GI.GlobalSdf = 0;
			Lighting.GI.GlobalSdfObjectIds = 0;
		}

		#endregion

		#region PrivateMethods

		private void Release()
		{
			if (compose == null && globalSdf == 0)
			{
				return;
			}

			DeleteVolumes();
			compose?.Delete();
			compose = null;
			currentResolution = 0;
			staticDirty = true;
			dynamicPending = true;
			lastStaticCount = -1;
			previousRegions.Clear();
			Console.WriteLine("[SDF] Global distance field released");
		}

		private void CreateVolumes(int resolution)
		{
			resolution = Math.Max(8, resolution / 4 * 4);
			DeleteVolumes();

			staticSdf = CreateSdfTexture(resolution);
			globalSdf = CreateSdfTexture(resolution);
			staticIds = CreateIdTexture(resolution);
			globalIds = CreateIdTexture(resolution);
			currentResolution = resolution;
			Lighting.GI.SdfResolution = resolution;
			staticDirty = true;
			previousRegions.Clear();

			Console.WriteLine($"[SDF] Global distance field {resolution}^3 R16F + object ids R16UI (~{resolution * (long)resolution * resolution * 2 * 4 / (1024.0 * 1024.0):0} MB)");
		}

		private void DeleteVolumes()
		{
			foreach (int texture in new[] { staticSdf, globalSdf, staticIds, globalIds })
			{
				if (texture != 0)
				{
					GL.DeleteTexture(texture);
				}
			}
			staticSdf = 0;
			globalSdf = 0;
			staticIds = 0;
			globalIds = 0;
			scroll = Vector3i.Zero;
			pendingScroll = Vector3i.Zero;
			Lighting.GI.SdfScroll = Vector3i.Zero;
		}

		// Object index + 1 of the nearest surface per voxel (0 = nothing within the band); fetched by texel.
		private static int CreateIdTexture(int resolution)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16ui, resolution, resolution, resolution, 0, PixelFormat.RedInteger, PixelType.UnsignedShort, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		private static int CreateSdfTexture(int resolution)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16f, resolution, resolution, resolution, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			// Toroidal storage: the sampling wraps, the clamp to the grid's edge is SampleSdf's.
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		private void DetectStaticChanges(GlobalIlluminationSettings gi, LumenScene scene)
		{
			bool moved = (gi.GridMin - lastGridMin).LengthSquared > 1e-6f;
			bool changed =
				Math.Abs(gi.GridSize - lastGridSize) > 1e-5f
				|| scene.StaticObjectCount != lastStaticCount;

			if (moved && !changed && !staticDirty)
			{
				// A move by whole voxels that keeps part of the field scrolls it; anything else rebuilds it.
				float voxel = gi.GridSize / currentResolution;
				Vector3 cells = (gi.GridMin - lastGridMin) / voxel;
				var move = new Vector3i((int)MathF.Round(cells.X), (int)MathF.Round(cells.Y), (int)MathF.Round(cells.Z));
				Vector3 rest = cells - new Vector3(move.X, move.Y, move.Z);
				bool whole = MathF.Abs(rest.X) < 1e-3f && MathF.Abs(rest.Y) < 1e-3f && MathF.Abs(rest.Z) < 1e-3f;
				bool overlaps = Math.Abs(move.X) < currentResolution && Math.Abs(move.Y) < currentResolution && Math.Abs(move.Z) < currentResolution;
				if (whole && overlaps)
				{
					pendingScroll = move;
				}
				else
				{
					changed = true;
				}
			}

			if (moved || changed)
			{
				staticDirty |= changed;
				lastGridMin = gi.GridMin;
				lastGridSize = gi.GridSize;
				lastStaticCount = scene.StaticObjectCount;
			}
		}

		// The volume moved by pendingScroll voxels. Grid voxel v is now what grid voxel v + move was, which is stored in
		// the same texel once the scroll takes the move: nothing is copied. The slabs that entered (their texels held the
		// voxels that left) are composed, the static field from the static objects and the working field from it and the
		// dynamic objects; the dynamic objects' stamps of the last frame move into the new grid coordinates.
		private void ScrollStaticField(GlobalIlluminationSettings gi, LumenScene scene)
		{
			int n = currentResolution;
			Vector3i move = pendingScroll;
			scroll = new Vector3i(Mod(scroll.X + move.X, n), Mod(scroll.Y + move.Y, n), Mod(scroll.Z + move.Z, n));

			// The slabs that entered, one per axis that moved (where two overlap, a corner is composed twice: harmless).
			scrollRegions.Clear();
			if (move.X != 0)
			{
				scrollRegions.Add(move.X > 0 ? new VoxelRegion(n - move.X, 0, 0, n, n, n) : new VoxelRegion(0, 0, 0, -move.X, n, n));
			}
			if (move.Y != 0)
			{
				scrollRegions.Add(move.Y > 0 ? new VoxelRegion(0, n - move.Y, 0, n, n, n) : new VoxelRegion(0, 0, 0, n, -move.Y, n));
			}
			if (move.Z != 0)
			{
				scrollRegions.Add(move.Z > 0 ? new VoxelRegion(0, 0, n - move.Z, n, n, n) : new VoxelRegion(0, 0, 0, n, n, -move.Z));
			}

			Compose(staticSdf, 0, 0, scene.StaticObjectCount, gi, scene, scrollRegions);
			Compose(globalSdf, staticSdf, scene.StaticObjectCount, scene.Objects.Count - scene.StaticObjectCount, gi, scene, scrollRegions);

			// Last frame's stamps, in this grid's voxels (the parts that left are gone with their slabs): the dynamic
			// update clears them from the working field where the objects are no longer.
			for (int i = 0; i < previousRegions.Count; i++)
			{
				VoxelRegion r = previousRegions[i];
				previousRegions[i] = new VoxelRegion(
					Math.Max(0, r.MinX - move.X), Math.Max(0, r.MinY - move.Y), Math.Max(0, r.MinZ - move.Z),
					Math.Min(n, r.MaxX - move.X), Math.Min(n, r.MaxY - move.Y), Math.Min(n, r.MaxZ - move.Z));
			}

			scene.MarkChanged();
		}

		private static int Mod(int value, int n)
		{
			int m = value % n;
			return m < 0 ? m + n : m;
		}

		private void RebuildStaticField(GlobalIlluminationSettings gi, LumenScene scene)
		{
			// Everything is composed again: the storage starts unscrolled.
			scroll = Vector3i.Zero;
			Compose(staticSdf, 0, 0, scene.StaticObjectCount, gi, scene, VoxelRegion.Full(currentResolution));

			GL.CopyImageSubData(
				staticSdf, ImageTarget.Texture3D, 0, 0, 0, 0,
				globalSdf, ImageTarget.Texture3D, 0, 0, 0, 0,
				currentResolution, currentResolution, currentResolution);
			GL.CopyImageSubData(
				staticIds, ImageTarget.Texture3D, 0, 0, 0, 0,
				globalIds, ImageTarget.Texture3D, 0, 0, 0, 0,
				currentResolution, currentResolution, currentResolution);

			previousRegions.Clear();
			staticDirty = false;
			builtStaticVersion = gi.StaticVersion;
			// A rebuilt static field changes the lighting as much as a dynamic move: caches leave their idle budgets.
			scene.MarkChanged();
		}

		private void UpdateDynamicField(GlobalIlluminationSettings gi, LumenScene scene)
		{
			// An object influences the field up to one band beyond its bounds.
			int padding = gi.SdfBandVoxels + 1;
			currentRegions.Clear();
			for (int i = scene.StaticObjectCount; i < scene.Objects.Count; i++)
			{
				VoxelRegion region;
				if (VoxelRegion.TryFromRenderer(scene.Objects[i].Renderer, gi.GridMin, gi.SdfVoxelSize, currentResolution, padding, out region))
				{
					currentRegions.Add(region);
				}
			}

			// Where objects were and where they are: static distance, min'ed with the current dynamic objects.
			updateRegions.Clear();
			updateRegions.AddRange(previousRegions);
			updateRegions.AddRange(currentRegions);

			int dynamicCount = scene.Objects.Count - scene.StaticObjectCount;
			if (updateRegions.Count > 0)
			{
				if (RegionCoverage(updateRegions) > FullUpdateCoverage)
				{
					Compose(globalSdf, staticSdf, scene.StaticObjectCount, dynamicCount, gi, scene, VoxelRegion.Full(currentResolution));
				}
				else
				{
					Compose(globalSdf, staticSdf, scene.StaticObjectCount, dynamicCount, gi, scene, updateRegions);
				}
			}

			List<VoxelRegion> swap = previousRegions;
			previousRegions = currentRegions;
			currentRegions = swap;
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

		private void Compose(int target, int baseSdf, int objectStart, int objectCount, GlobalIlluminationSettings gi, LumenScene scene, VoxelRegion region)
		{
			updateRegions.Clear();
			updateRegions.Add(region);
			Compose(target, baseSdf, objectStart, objectCount, gi, scene, updateRegions);
		}

		private void Compose(int target, int baseSdf, int objectStart, int objectCount, GlobalIlluminationSettings gi, LumenScene scene, List<VoxelRegion> regions)
		{
			compose.Use();
			scene.SetUniforms(compose.Program);
			compose.Program.SetInt("objectStart", objectStart);
			compose.Program.SetInt("objectCount", objectCount);
			compose.Program.SetInt("useBase", baseSdf != 0 ? 1 : 0);
			compose.Program.SetVector3("gridMin", gi.GridMin);
			compose.Program.SetFloat("gridSize", gi.GridSize);
			compose.Program.SetInt("sdfResolution", currentResolution);
			compose.Program.SetFloat("maxDistance", gi.SdfBandVoxels * gi.SdfVoxelSize);
			compose.Program.SetInt3("storageScroll", scroll.X, scroll.Y, scroll.Z);

			bool fromStatic = baseSdf != 0;
			int targetIds = target == staticSdf ? staticIds : globalIds;
			GL.BindImageTexture(0, fromStatic ? baseSdf : target, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R16f);
			GL.BindImageTexture(1, target, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);
			GL.BindImageTexture(2, fromStatic ? staticIds : targetIds, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R16ui);
			GL.BindImageTexture(3, targetIds, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16ui);

			foreach (VoxelRegion region in regions)
			{
				if (region.IsEmpty)
				{
					continue;
				}

				compose.Program.SetInt3("dstOffset", region.MinX, region.MinY, region.MinZ);
				compose.Program.SetInt3("dstSize", region.SizeX, region.SizeY, region.SizeZ);
				compose.DispatchThreads(region.SizeX, region.SizeY, region.SizeZ, 4, 4, 4);
			}

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		#endregion
	}
}
