using System;
using System.Collections.Generic;
using LELEngine.Rendering.DistanceField;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Builds the global signed distance field (Lumen's "global SDF"): a world-space R16F volume over the
	///     GI grid holding the distance to the nearest surface, clamped to a band of a few voxels.
	///
	///     Mesh distance fields are built once per (mesh, scale) into <see cref="DistanceFieldAtlas" />.
	///     Static renderers are composed into a cached field when they change; dynamic renderers are
	///     re-composed every frame, but only inside their band-padded bounds (previous and current), which
	///     is exact because the band limits how far an object can influence the field.
	///
	///     Consumers: Engine/DistanceField.glsl (sphere tracing) through Lighting.SetSdfUniforms.
	/// </summary>
	public sealed class GlobalDistanceFieldPass : RenderPass
	{
		#region PublicFields

		public override string Name => "GlobalSDF";
		public int GlobalSdf => globalSdf;
		public DistanceFieldAtlas Atlas => atlas;

		#endregion

		#region PrivateFields

		private DistanceFieldAtlas atlas;
		private ComputeShader compose;
		private int objectBuffer;
		private int staticSdf;
		private int globalSdf;
		private int currentResolution;

		private readonly List<MeshRenderer> staticRenderers = new List<MeshRenderer>();
		private readonly List<MeshRenderer> dynamicRenderers = new List<MeshRenderer>();
		private readonly List<SdfObjectData> objectData = new List<SdfObjectData>();

		private List<VoxelRegion> previousRegions = new List<VoxelRegion>();
		private List<VoxelRegion> currentRegions = new List<VoxelRegion>();
		private readonly List<VoxelRegion> updateRegions = new List<VoxelRegion>();

		private const float FullUpdateCoverage = 0.5f;

		private Vector3 lastGridMin;
		private float lastGridSize;
		private int lastStaticCount = -1;
		private int frameIndex;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			atlas = new DistanceFieldAtlas();
			compose = new ComputeShader("Engine/SdfCompose.shader");
			objectBuffer = GL.GenBuffer();
			CreateVolumes(Lighting.GI.SdfResolution);
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			if (!gi.Enabled || !gi.DistanceFieldEnabled)
			{
				gi.GlobalSdf = 0;
				return;
			}

			if (gi.SdfResolution != currentResolution)
			{
				CreateVolumes(gi.SdfResolution);
			}

			gi.UpdateGridPlacement(context.CameraPosition);
			PartitionRenderers(context.Renderers);
			DetectStaticChanges(gi);

			frameIndex++;
			bool dynamicDue = gi.DynamicUpdateInterval <= 1 || frameIndex % gi.DynamicUpdateInterval == 0;
			GpuProfiler profiler = context.Renderer.Profiler;

			if (gi.SdfStaticDirty)
			{
				profiler.Split("GlobalSDF.static");
				RebuildStaticField(gi);
				// The whole working field was replaced: dynamic objects must be stamped again this frame.
				dynamicDue = true;
			}

			if (dynamicDue)
			{
				profiler.Split("GlobalSDF.dynamic");
				UpdateDynamicField(gi);
			}

			gi.GlobalSdf = globalSdf;
		}

		public override void Dispose()
		{
			DeleteVolumes();
			atlas?.Delete();
			atlas = null;
			compose?.Delete();
			compose = null;
			if (objectBuffer != 0)
			{
				GL.DeleteBuffer(objectBuffer);
				objectBuffer = 0;
			}
			Lighting.GI.GlobalSdf = 0;
		}

		#endregion

		#region PrivateMethods

		private void CreateVolumes(int resolution)
		{
			resolution = Math.Max(8, resolution / 4 * 4);
			DeleteVolumes();

			staticSdf = CreateSdfTexture(resolution);
			globalSdf = CreateSdfTexture(resolution);
			currentResolution = resolution;
			Lighting.GI.SdfResolution = resolution;
			Lighting.GI.SdfStaticDirty = true;
			previousRegions.Clear();

			Console.WriteLine($"[SDF] Global distance field {resolution}^3 R16F (~{resolution * (long)resolution * resolution * 2 * 2 / (1024.0 * 1024.0):0} MB)");
		}

		private void DeleteVolumes()
		{
			if (staticSdf != 0)
			{
				GL.DeleteTexture(staticSdf);
				staticSdf = 0;
			}
			if (globalSdf != 0)
			{
				GL.DeleteTexture(globalSdf);
				globalSdf = 0;
			}
		}

		private static int CreateSdfTexture(int resolution)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16f, resolution, resolution, resolution, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		private void PartitionRenderers(IReadOnlyList<MeshRenderer> renderers)
		{
			staticRenderers.Clear();
			dynamicRenderers.Clear();
			foreach (MeshRenderer renderer in renderers)
			{
				if (!renderer.ContributesToGI || renderer.Mesh == null || renderer.Mesh.Verticies.Count < 3)
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

		private void DetectStaticChanges(GlobalIlluminationSettings gi)
		{
			bool changed =
				(gi.GridMin - lastGridMin).LengthSquared > 1e-6f
				|| Math.Abs(gi.GridSize - lastGridSize) > 1e-5f
				|| staticRenderers.Count != lastStaticCount;

			if (changed)
			{
				gi.SdfStaticDirty = true;
				lastGridMin = gi.GridMin;
				lastGridSize = gi.GridSize;
				lastStaticCount = staticRenderers.Count;
			}
		}

		private void RebuildStaticField(GlobalIlluminationSettings gi)
		{
			Compose(staticSdf, 0, staticRenderers, gi, VoxelRegion.Full(currentResolution));

			GL.CopyImageSubData(
				staticSdf, ImageTarget.Texture3D, 0, 0, 0, 0,
				globalSdf, ImageTarget.Texture3D, 0, 0, 0, 0,
				currentResolution, currentResolution, currentResolution);

			previousRegions.Clear();
			gi.SdfStaticDirty = false;
		}

		private void UpdateDynamicField(GlobalIlluminationSettings gi)
		{
			// An object influences the field up to one band beyond its bounds.
			int padding = gi.SdfBandVoxels + 1;
			currentRegions.Clear();
			foreach (MeshRenderer renderer in dynamicRenderers)
			{
				VoxelRegion region;
				if (VoxelRegion.TryFromRenderer(renderer, gi.GridMin, gi.SdfVoxelSize, currentResolution, padding, out region))
				{
					currentRegions.Add(region);
				}
			}

			// Where objects were and where they are: static distance, min'ed with the current dynamic objects.
			updateRegions.Clear();
			updateRegions.AddRange(previousRegions);
			updateRegions.AddRange(currentRegions);

			if (updateRegions.Count > 0)
			{
				if (RegionCoverage(updateRegions) > FullUpdateCoverage)
				{
					Compose(globalSdf, staticSdf, dynamicRenderers, gi, VoxelRegion.Full(currentResolution));
				}
				else
				{
					Compose(globalSdf, staticSdf, dynamicRenderers, gi, updateRegions);
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

		private void Compose(int target, int baseSdf, List<MeshRenderer> renderers, GlobalIlluminationSettings gi, VoxelRegion region)
		{
			updateRegions.Clear();
			updateRegions.Add(region);
			Compose(target, baseSdf, renderers, gi, updateRegions);
		}

		private void Compose(int target, int baseSdf, List<MeshRenderer> renderers, GlobalIlluminationSettings gi, List<VoxelRegion> regions)
		{
			UploadObjects(renderers);

			compose.Use();
			compose.Program.SetInt("objectCount", objectData.Count);
			compose.Program.SetTexture("SdfAtlas", TextureTarget.Texture3D, atlas.Texture, 0);
			compose.Program.SetVector3("atlasTexels", atlas.TexelCount);
			compose.Program.SetInt("useBase", baseSdf != 0 ? 1 : 0);
			compose.Program.SetVector3("gridMin", gi.GridMin);
			compose.Program.SetFloat("gridSize", gi.GridSize);
			compose.Program.SetInt("sdfResolution", currentResolution);
			compose.Program.SetFloat("maxDistance", gi.SdfBandVoxels * gi.SdfVoxelSize);

			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, objectBuffer);
			GL.BindImageTexture(0, baseSdf != 0 ? baseSdf : target, 0, true, 0, TextureAccess.ReadOnly, SizedInternalFormat.R16f);
			GL.BindImageTexture(1, target, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);

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

		// Builds (or fetches) each renderer's mesh field and uploads the per-object records.
		private void UploadObjects(List<MeshRenderer> renderers)
		{
			objectData.Clear();
			foreach (MeshRenderer renderer in renderers)
			{
				MeshDistanceField field = atlas.GetOrBuild(renderer.Mesh, renderer.transform.scale);
				if (field == null)
				{
					continue;
				}

				Transform t = renderer.transform;
				Matrix4 localToWorld = Matrix4.CreateFromQuaternion(t.rotation) * Matrix4.CreateTranslation(t.position);

				objectData.Add(new SdfObjectData
				{
					WorldToLocal = Matrix4.Invert(localToWorld),
					BoundsMin = new Vector4(field.BoundsMin, 0f),
					BoundsMax = new Vector4(field.BoundsMax, 0f),
					AtlasOrigin = new Vector4(0f, 0f, field.AtlasZ, 0f),
					AtlasSize = new Vector4(field.Size.X, field.Size.Y, field.Size.Z, 0f),
					Padding = new Vector4(field.TexelSize * DistanceFieldAtlas.Padding, 0f)
				});
			}

			SdfObjectData[] array = objectData.ToArray();
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, objectBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, Math.Max(1, array.Length) * SdfObjectData.Size, array.Length > 0 ? array : new SdfObjectData[1], BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
		}

		#endregion

		#region NestedTypes

		/// <summary>Mirrors SdfObject in Engine/SdfCompose.shader (std430, 144 bytes).</summary>
		private struct SdfObjectData
		{
			public const int Size = 144;

			public Matrix4 WorldToLocal;
			public Vector4 BoundsMin;
			public Vector4 BoundsMax;
			public Vector4 AtlasOrigin;
			public Vector4 AtlasSize;
			public Vector4 Padding;
		}

		#endregion
	}
}
