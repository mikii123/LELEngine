using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace LELEngine.Navigation.Builder
{
	/// <summary>
	///     Bakes a <see cref="NavMeshData" /> from triangle sources: the volume is split into tiles, every tile is
	///     voxelized (with a border so neighboring tiles agree), filtered for the agent, eroded by its radius, cut
	///     into layers and turned into polygons. Tiles build in parallel. Also rebuilds single tiles from their
	///     stored layers with obstacles carved out (dynamic carving).
	/// </summary>
	public static class NavMeshBuilder
	{
		#region PublicMethods

		/// <summary>
		///     Bakes the sources inside <paramref name="boundsMin" />..<paramref name="boundsMax" /> (surface-local).
		///     Returns an empty data object when nothing is walkable.
		/// </summary>
		public static NavMeshData Build(NavMeshBuildSettings settings, IReadOnlyList<NavMeshBuildSource> sources, IReadOnlyList<NavMeshAreaVolume> volumes, Vector3 boundsMin, Vector3 boundsMax, Action<string> log = null)
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			settings = settings.Clone();
			var data = new NavMeshData { Settings = settings };

			float cs = settings.CellSize;
			float ch = settings.CellHeight;
			int tileSize = Math.Max(8, settings.TileSize);
			var config = new Config
			{
				CellSize = cs,
				CellHeight = ch,
				WalkableHeight = (int)Math.Ceiling(settings.AgentHeight / ch),
				WalkableClimb = (int)Math.Floor(settings.AgentClimb / ch),
				WalkableRadius = (int)Math.Ceiling(settings.AgentRadius / cs),
				WalkableSlopeCos = (float)Math.Cos(settings.AgentSlope * Math.PI / 180.0),
				TileSize = tileSize,
				MinRegionCells = (int)Math.Ceiling(settings.MinRegionArea / (cs * cs)),
				MaxEdgeError = settings.MaxEdgeError,
				MaxEdgeLength = settings.MaxEdgeLength,
				VertsPerPoly = 6
			};
			config.BorderSize = config.WalkableRadius + 3;

			// Triangles in surface-local space.
			List<Triangle> triangles = CollectTriangles(sources);
			if (triangles.Count == 0)
			{
				log?.Invoke("NavMesh: no geometry to bake.");
				return data;
			}

			// The baked volume: padded so the agent's clearance above the top faces is inside.
			boundsMin.Y -= ch;
			boundsMax.Y += settings.AgentHeight + ch;
			data.BoundsMin = boundsMin;
			data.BoundsMax = boundsMax;
			int gridWidth = (int)Math.Ceiling((boundsMax.X - boundsMin.X) / cs);
			int gridHeight = (int)Math.Ceiling((boundsMax.Z - boundsMin.Z) / cs);
			data.TilesX = Math.Max(1, (gridWidth + tileSize - 1) / tileSize);
			data.TilesY = Math.Max(1, (gridHeight + tileSize - 1) / tileSize);
			data.TileWorldSize = tileSize * cs;

			if ((long)data.TilesX * data.TilesY > 65536)
			{
				log?.Invoke($"NavMesh: too many tiles ({data.TilesX} x {data.TilesY}); increase the voxel or tile size, or shrink the volume.");
				return data;
			}

			// Bin triangles into tiles (padded by the border).
			var tileTriangles = new List<int>[data.TilesX * data.TilesY];
			float pad = config.BorderSize * cs;
			for (int t = 0; t < triangles.Count; t++)
			{
				Triangle tri = triangles[t];
				int tx0 = Math.Max(0, (int)((tri.Min.X - pad - boundsMin.X) / data.TileWorldSize));
				int tx1 = Math.Min(data.TilesX - 1, (int)((tri.Max.X + pad - boundsMin.X) / data.TileWorldSize));
				int ty0 = Math.Max(0, (int)((tri.Min.Z - pad - boundsMin.Z) / data.TileWorldSize));
				int ty1 = Math.Min(data.TilesY - 1, (int)((tri.Max.Z + pad - boundsMin.Z) / data.TileWorldSize));
				for (int ty = ty0; ty <= ty1; ty++)
				{
					for (int tx = tx0; tx <= tx1; tx++)
					{
						(tileTriangles[tx + ty * data.TilesX] ??= new List<int>()).Add(t);
					}
				}
			}

			var results = new List<(NavMeshTileData tile, NavMeshLayerData layer)>[data.TilesX * data.TilesY];
			Parallel.For(0, results.Length, index =>
			{
				List<int> bin = tileTriangles[index];
				if (bin == null)
				{
					return;
				}

				int tx = index % data.TilesX;
				int ty = index / data.TilesX;
				results[index] = BuildTile(config, data, triangles, bin, volumes, tx, ty);
			});

			var tiles = new List<NavMeshTileData>();
			var layers = new List<NavMeshLayerData>();
			foreach (List<(NavMeshTileData tile, NavMeshLayerData layer)> result in results)
			{
				if (result == null)
				{
					continue;
				}

				foreach ((NavMeshTileData tile, NavMeshLayerData layer) in result)
				{
					tiles.Add(tile);
					layers.Add(layer);
				}
			}

			data.Tiles = tiles.ToArray();
			data.Layers = layers.ToArray();
			log?.Invoke($"NavMesh: {data.Tiles.Length} tiles, {data.PolygonCount} polygons, {triangles.Count} triangles, {watch.ElapsedMilliseconds} ms, {data.DataSize / 1024} KB");
			return data;
		}

		/// <summary>
		///     Rebuilds one polygon tile from its stored layer with the obstacle shapes (surface-local) carved out.
		///     Returns null when nothing walkable remains.
		/// </summary>
		public static NavMeshTileData RebuildTile(NavMeshData data, NavMeshLayerData layerData, IReadOnlyList<CarveShape> shapes)
		{
			NavMeshBuildSettings settings = data.Settings;
			float cs = settings.CellSize;
			float ch = settings.CellHeight;
			HeightfieldLayer layer = HeightfieldLayer.FromData(layerData, cs, ch);
			if (shapes != null)
			{
				// Shapes are inflated by the agent radius: the eroded navmesh keeps agents off the obstacle. Vertically a
				// shape blocks the floors whose agent space (the agent height above them) it overlaps, as in Unity.
				float inflate = settings.AgentRadius;
				int clearance = (int)Math.Ceiling(settings.AgentHeight / ch);
				foreach (CarveShape shape in shapes)
				{
					if (shape.IsCylinder)
					{
						layer.MarkCylinderArea(shape.Center - new Vector3(0f, shape.HalfExtents.Y, 0f), shape.Radius + inflate, shape.HalfExtents.Y * 2f, clearance, NavMeshArea.Null);
					}
					else
					{
						layer.MarkOrientedBoxArea(shape.Center, shape.AxisX, shape.AxisZ, shape.HalfExtents + new Vector3(inflate, 0f, inflate), clearance, NavMeshArea.Null);
					}
				}
			}

			int walkableClimb = (int)Math.Floor(settings.AgentClimb / ch);
			int minRegionCells = (int)Math.Ceiling(settings.MinRegionArea / (cs * cs));
			return TileBuilder.Build(layer, layerData.X, layerData.Y, layerData.Layer, walkableClimb, settings.MaxEdgeError, settings.MaxEdgeLength, minRegionCells, 6);
		}

		#endregion

		#region PrivateMethods

		private sealed class Config
		{
			public float CellSize;
			public float CellHeight;
			public int WalkableHeight;
			public int WalkableClimb;
			public int WalkableRadius;
			public float WalkableSlopeCos;
			public int TileSize;
			public int BorderSize;
			public int MinRegionCells;
			public float MaxEdgeError;
			public int MaxEdgeLength;
			public int VertsPerPoly;
		}

		private struct Triangle
		{
			public Vector3 A, B, C;
			public Vector3 Min, Max;
			public byte Area;
		}

		private static List<Triangle> CollectTriangles(IReadOnlyList<NavMeshBuildSource> sources)
		{
			var triangles = new List<Triangle>();
			foreach (NavMeshBuildSource source in sources)
			{
				if (source.Vertices == null || source.Vertices.Length < 3)
				{
					continue;
				}

				Matrix4 m = source.Transform;
				// Mirrored transforms flip the winding; keep the normals pointing the right way.
				bool flip = Vector3.Dot(Vector3.Cross(m.Row0.Xyz, m.Row1.Xyz), m.Row2.Xyz) < 0f;
				byte area = NavMeshArea.ToInternal(source.Area);
				int count = source.Indices != null ? source.Indices.Length / 3 : source.Vertices.Length / 3;
				for (int t = 0; t < count; t++)
				{
					int i0 = source.Indices != null ? source.Indices[t * 3] : t * 3;
					int i1 = source.Indices != null ? source.Indices[t * 3 + 1] : t * 3 + 1;
					int i2 = source.Indices != null ? source.Indices[t * 3 + 2] : t * 3 + 2;
					if (i0 >= source.Vertices.Length || i1 >= source.Vertices.Length || i2 >= source.Vertices.Length)
					{
						continue;
					}

					Vector3 a = Vector3.TransformPosition(source.Vertices[i0], m);
					Vector3 b = Vector3.TransformPosition(source.Vertices[flip ? i2 : i1], m);
					Vector3 c = Vector3.TransformPosition(source.Vertices[flip ? i1 : i2], m);
					triangles.Add(new Triangle
					{
						A = a, B = b, C = c, Area = area,
						Min = Vector3.ComponentMin(Vector3.ComponentMin(a, b), c),
						Max = Vector3.ComponentMax(Vector3.ComponentMax(a, b), c)
					});
				}
			}

			return triangles;
		}

		private static List<(NavMeshTileData, NavMeshLayerData)> BuildTile(Config config, NavMeshData data, List<Triangle> triangles, List<int> bin, IReadOnlyList<NavMeshAreaVolume> volumes, int tx, int ty)
		{
			float cs = config.CellSize;
			int size = config.TileSize + config.BorderSize * 2;
			Vector3 bmin = data.BoundsMin;
			Vector3 bmax = data.BoundsMax;
			bmin.X = data.BoundsMin.X + (tx * config.TileSize - config.BorderSize) * cs;
			bmin.Z = data.BoundsMin.Z + (ty * config.TileSize - config.BorderSize) * cs;
			bmax.X = bmin.X + size * cs;
			bmax.Z = bmin.Z + size * cs;

			var hf = new Heightfield(size, size, bmin, bmax, cs, config.CellHeight);
			var scratch = new float[7 * 3 * 4];
			foreach (int index in bin)
			{
				Triangle tri = triangles[index];
				Vector3 normal = Vector3.Cross(tri.B - tri.A, tri.C - tri.A);
				float length = normal.Length;
				byte area = NavMeshArea.Null;
				if (length > 1e-12f && tri.Area != NavMeshArea.Null && normal.Y / length > config.WalkableSlopeCos)
				{
					area = tri.Area;
				}

				Rasterizer.RasterizeTriangle(hf, tri.A, tri.B, tri.C, area, config.WalkableClimb, scratch);
			}

			HeightfieldFilters.FilterLowHangingWalkableObstacles(hf, config.WalkableClimb);
			HeightfieldFilters.FilterLedgeSpans(hf, config.WalkableHeight, config.WalkableClimb);
			HeightfieldFilters.FilterWalkableLowHeightSpans(hf, config.WalkableHeight);

			var chf = new CompactHeightfield(hf, config.WalkableHeight, config.WalkableClimb);
			if (chf.SpanCount == 0)
			{
				return null;
			}

			chf.ErodeWalkableArea(config.WalkableRadius);
			if (volumes != null)
			{
				foreach (NavMeshAreaVolume volume in volumes)
				{
					chf.MarkConvexPolyArea(volume.Polygon, volume.MinY, volume.MaxY, NavMeshArea.ToInternal(volume.Area));
				}
			}

			List<HeightfieldLayer> layers = LayerBuilder.Build(chf, config.BorderSize, config.WalkableHeight);
			var result = new List<(NavMeshTileData, NavMeshLayerData)>();
			for (int i = 0; i < layers.Count; i++)
			{
				HeightfieldLayer layer = layers[i];
				NavMeshLayerData layerData = layer.ToData(tx, ty, i);
				NavMeshTileData tile = TileBuilder.Build(layer, tx, ty, i, config.WalkableClimb, config.MaxEdgeError, config.MaxEdgeLength, config.MinRegionCells, config.VertsPerPoly);
				if (tile != null)
				{
					result.Add((tile, layerData));
				}
			}

			return result;
		}

		#endregion
	}

	/// <summary>An obstacle footprint in a surface's local space (vertical cylinder or box rotated about the vertical axis).</summary>
	public struct CarveShape
	{
		public bool IsCylinder;

		/// <summary>Center of the shape (cylinder: center of the axis).</summary>
		public Vector3 Center;

		public float Radius;

		/// <summary>Box half extents (y also gives the cylinder's half height).</summary>
		public Vector3 HalfExtents;

		/// <summary>Horizontal axes of the box in local xz (unit length).</summary>
		public Vector2 AxisX;

		public Vector2 AxisZ;

		/// <summary>Horizontal bounds of the shape (local), for tile lookups.</summary>
		public Vector3 BoundsMin;

		public Vector3 BoundsMax;
	}
}
