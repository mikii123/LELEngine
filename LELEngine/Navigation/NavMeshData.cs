using System;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>
	///     Area types of navmesh polygons (Unity numbering): 0 walkable, 1 not walkable, 2 jump, 3..31 user areas.
	///     Queries filter areas with a bit mask (bit i = area i) and weight them with per-area costs.
	/// </summary>
	public static class NavMeshArea
	{
		public const int Walkable = 0;
		public const int NotWalkable = 1;
		public const int Jump = 2;
		public const int Count = 32;
		public const int AllAreas = -1;

		/// <summary>Internal area byte of the builder: 0 is "no polygon", walkable areas are stored +1.</summary>
		internal const byte Null = 0;

		internal static byte ToInternal(int area)
		{
			return area == NotWalkable || area < 0 || area >= Count ? Null : (byte)(area + 1);
		}

		internal static int ToPublic(byte internalArea)
		{
			return internalArea == Null ? NotWalkable : internalArea - 1;
		}
	}

	/// <summary>
	///     Voxelization and agent parameters a surface is baked with (Unity's agent type settings). Distances in
	///     world units of the surface's local space.
	/// </summary>
	[Serializable]
	public sealed class NavMeshBuildSettings
	{
		#region PublicFields

		[Tooltip("Radius of the agents that use this surface: walkable area is eroded by it.")]
		public float AgentRadius = 0.5f;

		[Tooltip("Height of the agents: lower passages are not walkable.")]
		public float AgentHeight = 2f;

		[Tooltip("Steepest walkable slope, degrees.")]
		[Range(0f, 60f)]
		public float AgentSlope = 45f;

		[Tooltip("Highest step an agent climbs.")]
		public float AgentClimb = 0.4f;

		[Tooltip("Horizontal voxel size (0 = AgentRadius / 3).")]
		public float VoxelSize;

		[Tooltip("Vertical voxel size (0 = VoxelSize / 2).")]
		public float VoxelHeight;

		[Tooltip("Tile size in voxels; smaller tiles make dynamic carving cheaper.")]
		public int TileSize = 48;

		[Tooltip("Regions smaller than this (square units) are dropped unless they touch a tile edge.")]
		public float MinRegionArea = 2f;

		[Tooltip("Allowed deviation of the polygon edges from the voxel contour, in voxels.")]
		public float MaxEdgeError = 1.3f;

		[Tooltip("Longest polygon edge in voxels (0 = unlimited).")]
		public int MaxEdgeLength = 0;

		#endregion

		#region PublicMethods

		public float CellSize => VoxelSize > 0f ? VoxelSize : Math.Max(0.01f, AgentRadius / 3f);
		public float CellHeight => VoxelHeight > 0f ? VoxelHeight : CellSize * 0.5f;

		public NavMeshBuildSettings Clone()
		{
			return (NavMeshBuildSettings)MemberwiseClone();
		}

		#endregion
	}

	/// <summary>
	///     Baked navigation data of one surface, stored with the surface's GameObject in the scene. Everything is in
	///     the surface's local space: moving or rotating the surface moves the navmesh with it.
	/// </summary>
	[Serializable]
	public sealed class NavMeshData
	{
		#region PublicFields

		public const int CurrentVersion = 1;

		public int Version = CurrentVersion;
		public NavMeshBuildSettings Settings = new NavMeshBuildSettings();

		/// <summary>Minimum corner of the baked volume (local).</summary>
		public Vector3 BoundsMin;

		public Vector3 BoundsMax;

		/// <summary>Tile grid: tiles are <see cref="TileWorldSize" /> wide, (X, Y) counted from <see cref="BoundsMin" />.</summary>
		public int TilesX;

		public int TilesY;
		public float TileWorldSize;

		/// <summary>Polygon tiles (one per heightfield layer of a tile cell).</summary>
		public NavMeshTileData[] Tiles = Array.Empty<NavMeshTileData>();

		/// <summary>Compressed heightfield layers, one per polygon tile (same order), for dynamic carving.</summary>
		public NavMeshLayerData[] Layers = Array.Empty<NavMeshLayerData>();

		#endregion

		#region PublicMethods

		public bool IsEmpty => Tiles == null || Tiles.Length == 0;

		public int PolygonCount
		{
			get
			{
				int count = 0;
				if (Tiles != null)
				{
					foreach (NavMeshTileData tile in Tiles)
					{
						count += tile.PolyCount;
					}
				}

				return count;
			}
		}

		/// <summary>Size of the serialized arrays in bytes (an estimate shown by the editor).</summary>
		public long DataSize
		{
			get
			{
				long size = 0;
				if (Tiles != null)
				{
					foreach (NavMeshTileData tile in Tiles)
					{
						size += tile.Vertices.Length * 12L + tile.PolyVerts.Length * 4L + tile.PolyNeighbors.Length * 4L + tile.PolyAreas.Length;
					}
				}

				if (Layers != null)
				{
					foreach (NavMeshLayerData layer in Layers)
					{
						size += layer.Compressed.Length;
					}
				}

				return size;
			}
		}

		#endregion
	}

	/// <summary>Polygons of one tile layer in the surface's local space.</summary>
	[Serializable]
	public sealed class NavMeshTileData
	{
		#region PublicFields

		public int X;
		public int Y;
		public int Layer;
		public Vector3 BoundsMin;
		public Vector3 BoundsMax;
		public Vector3[] Vertices = Array.Empty<Vector3>();

		/// <summary>Vertices per polygon slot; unused slots are -1.</summary>
		public int VertsPerPoly = 6;

		public int PolyCount;

		/// <summary>PolyCount * VertsPerPoly vertex indices.</summary>
		public int[] PolyVerts = Array.Empty<int>();

		/// <summary>Per polygon edge (same layout as the vertices) the neighbor polygon in this tile, or -1 at the tile's boundary.</summary>
		public int[] PolyNeighbors = Array.Empty<int>();

		/// <summary>Public area index per polygon.</summary>
		public byte[] PolyAreas = Array.Empty<byte>();

		#endregion
	}

	/// <summary>
	///     A compressed heightfield layer: the walkable cells of one tile layer (height, area and neighbor
	///     connections per cell) the polygons are rebuilt from when obstacles carve the tile.
	/// </summary>
	[Serializable]
	public sealed class NavMeshLayerData
	{
		#region PublicFields

		public int X;
		public int Y;
		public int Layer;
		public int Width;
		public int Height;
		public int MinX;
		public int MaxX;
		public int MinY;
		public int MaxY;

		/// <summary>Height range of the layer in cells; cell heights are stored relative to HMin.</summary>
		public int HMin;

		public int HMax;
		public Vector3 BoundsMin;
		public Vector3 BoundsMax;

		/// <summary>RLE-compressed heights (two byte planes), areas and connections.</summary>
		public byte[] Compressed = Array.Empty<byte>();

		#endregion
	}

	/// <summary>Triangles fed to the builder, in the surface's local space.</summary>
	public struct NavMeshBuildSource
	{
		/// <summary>Vertex positions (object space when <see cref="Transform" /> is set, else surface-local).</summary>
		public Vector3[] Vertices;

		/// <summary>Triangle indices; null means every three consecutive vertices form a triangle.</summary>
		public int[] Indices;

		/// <summary>Object space to surface-local space (row-vector convention: v * M).</summary>
		public Matrix4 Transform;

		/// <summary>Public area index of the triangles (<see cref="NavMeshArea" />).</summary>
		public int Area;

		/// <summary>The source object, for diagnostics.</summary>
		public object Owner;
	}

	/// <summary>A convex volume (in the surface's local space) whose cells get an area.</summary>
	public struct NavMeshAreaVolume
	{
		/// <summary>Convex polygon in the xz plane (x, z pairs), counter-clockwise or clockwise.</summary>
		public Vector2[] Polygon;

		public float MinY;
		public float MaxY;
		public int Area;
	}
}
