using System;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Half-open voxel index box [Min, Max) inside a cubic grid. Shared by the voxel GI and
	///     global distance field passes to limit per-frame work to where dynamic objects are.
	/// </summary>
	internal readonly struct VoxelRegion
	{
		#region PublicFields

		public readonly int MinX, MinY, MinZ;
		public readonly int MaxX, MaxY, MaxZ;

		#endregion

		#region Constructors

		public VoxelRegion(int minX, int minY, int minZ, int maxX, int maxY, int maxZ)
		{
			MinX = minX;
			MinY = minY;
			MinZ = minZ;
			MaxX = maxX;
			MaxY = maxY;
			MaxZ = maxZ;
		}

		#endregion

		#region PublicMethods

		public int SizeX => MaxX - MinX;
		public int SizeY => MaxY - MinY;
		public int SizeZ => MaxZ - MinZ;
		public bool IsEmpty => SizeX <= 0 || SizeY <= 0 || SizeZ <= 0;
		public double Volume => IsEmpty ? 0 : (double)SizeX * SizeY * SizeZ;

		public static VoxelRegion Full(int resolution)
		{
			return new VoxelRegion(0, 0, 0, resolution, resolution, resolution);
		}

		/// <summary>Footprint of this level-0 region at a coarser mip level.</summary>
		public VoxelRegion AtLevel(int level, int levelSize)
		{
			int round = (1 << level) - 1;
			return new VoxelRegion(
				Math.Min(MinX >> level, levelSize), Math.Min(MinY >> level, levelSize), Math.Min(MinZ >> level, levelSize),
				Math.Min((MaxX + round) >> level, levelSize), Math.Min((MaxY + round) >> level, levelSize), Math.Min((MaxZ + round) >> level, levelSize));
		}

		/// <summary>
		///     Voxel-space bounding box of a renderer: world AABB of its mesh bounds, padded by the given
		///     number of voxels and clamped to the grid.
		/// </summary>
		public static bool TryFromRenderer(MeshRenderer renderer, Vector3 gridMin, float voxelSize, int resolution, int padding, out VoxelRegion region)
		{
			Matrix4 localToWorld = renderer.transform.LocalToWorld;
			Vector3 bmin = renderer.Mesh.BoundsMin;
			Vector3 bmax = renderer.Mesh.BoundsMax;

			Vector3 worldMin = new Vector3(float.MaxValue);
			Vector3 worldMax = new Vector3(float.MinValue);
			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = new Vector3(
					(i & 1) != 0 ? bmax.X : bmin.X,
					(i & 2) != 0 ? bmax.Y : bmin.Y,
					(i & 4) != 0 ? bmax.Z : bmin.Z);
				Vector3 world = Vector3.TransformPosition(corner, localToWorld);
				worldMin = Vector3.ComponentMin(worldMin, world);
				worldMax = Vector3.ComponentMax(worldMax, world);
			}

			Vector3 vmin = (worldMin - gridMin) / voxelSize;
			Vector3 vmax = (worldMax - gridMin) / voxelSize;

			int x0 = Math.Clamp((int)Math.Floor(vmin.X) - padding, 0, resolution);
			int y0 = Math.Clamp((int)Math.Floor(vmin.Y) - padding, 0, resolution);
			int z0 = Math.Clamp((int)Math.Floor(vmin.Z) - padding, 0, resolution);
			int x1 = Math.Clamp((int)Math.Ceiling(vmax.X) + padding, 0, resolution);
			int y1 = Math.Clamp((int)Math.Ceiling(vmax.Y) + padding, 0, resolution);
			int z1 = Math.Clamp((int)Math.Ceiling(vmax.Z) + padding, 0, resolution);

			region = new VoxelRegion(x0, y0, z0, x1, y1, z1);
			return !region.IsEmpty;
		}

		#endregion
	}
}
