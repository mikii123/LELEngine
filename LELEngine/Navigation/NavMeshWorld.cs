using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>
	///     Handle of a navmesh polygon: surface slot, tile, polygon index and a salt that changes whenever the tile
	///     is rebuilt (carving) or the surface removed, so stale handles are detected.
	/// </summary>
	public readonly struct PolyRef : IEquatable<PolyRef>
	{
		public readonly ulong Value;

		public PolyRef(ulong value)
		{
			Value = value;
		}

		public static readonly PolyRef Null = new PolyRef(0);

		public bool IsNull => Value == 0;
		public int Salt => (int)(Value >> 48);
		public int Surface => (int)((Value >> 36) & 0xfff);
		public int Tile => (int)((Value >> 16) & 0xfffff);
		public int Poly => (int)(Value & 0xffff);

		public static PolyRef Encode(int salt, int surface, int tile, int poly)
		{
			return new PolyRef(((ulong)(uint)salt << 48) | ((ulong)(uint)surface << 36) | ((ulong)(uint)tile << 16) | (uint)poly);
		}

		public bool Equals(PolyRef other) => Value == other.Value;
		public override bool Equals(object obj) => obj is PolyRef other && Value == other.Value;
		public override int GetHashCode() => Value.GetHashCode();
		public static bool operator ==(PolyRef a, PolyRef b) => a.Value == b.Value;
		public static bool operator !=(PolyRef a, PolyRef b) => a.Value != b.Value;
		public override string ToString() => IsNull ? "null" : $"s{Surface}/t{Tile}/p{Poly}";
	}

	/// <summary>A connection from a polygon edge to a polygon of another tile (or surface) along a part of the edge.</summary>
	internal struct NavLink
	{
		public PolyRef Ref;
		public int Edge;

		/// <summary>Overlap along this polygon's edge (0..1 from the edge's first vertex).</summary>
		public float TMin;

		public float TMax;
		public int Next;

		/// <summary>Polygon the link belongs to (packed into <see cref="Edge" /> with the edge index).</summary>
		public int Poly => Edge >> 8;

		public int EdgeIndex => Edge & 0xff;
	}

	/// <summary>Runtime form of a polygon tile: world-space vertices, bounds and the links to other tiles.</summary>
	internal sealed class NavTile
	{
		#region PublicFields

		public NavSurface Surface;
		public int Index;
		public int Salt = 1;
		public NavMeshTileData Data;
		public NavMeshLayerData LayerData;
		public Vector3[] WorldVerts;
		public Vector3[] PolyMin;
		public Vector3[] PolyMax;
		public Vector3 WorldMin;
		public Vector3 WorldMax;
		public int[] FirstLink;
		public readonly List<NavLink> Links = new List<NavLink>();

		/// <summary>The current polygons came from a carve rebuild (differ from the baked data).</summary>
		public bool Carved;

		#endregion

		#region PublicMethods

		public int PolyCount => Data.PolyCount;
		public int VertsPerPoly => Data.VertsPerPoly;

		public int PolyVertCount(int poly)
		{
			int nvp = Data.VertsPerPoly;
			int first = poly * nvp;
			for (int i = 0; i < nvp; i++)
			{
				if (Data.PolyVerts[first + i] < 0)
				{
					return i;
				}
			}

			return nvp;
		}

		/// <summary>Copies the world vertices of a polygon into <paramref name="buffer" />; returns the count.</summary>
		public int GetPolyVerts(int poly, Span<Vector3> buffer)
		{
			int nvp = Data.VertsPerPoly;
			int first = poly * nvp;
			int n = 0;
			for (int i = 0; i < nvp; i++)
			{
				int v = Data.PolyVerts[first + i];
				if (v < 0)
				{
					break;
				}

				buffer[n++] = WorldVerts[v];
			}

			return n;
		}

		public Vector3 Vertex(int poly, int corner)
		{
			return WorldVerts[Data.PolyVerts[poly * Data.VertsPerPoly + corner]];
		}

		public int Neighbor(int poly, int edge)
		{
			return Data.PolyNeighbors[poly * Data.VertsPerPoly + edge];
		}

		public int Area(int poly)
		{
			return Data.PolyAreas[poly];
		}

		public PolyRef Ref(int poly)
		{
			return PolyRef.Encode(Salt, Surface.Slot, Index, poly);
		}

		public void RebuildLinkChains()
		{
			Array.Fill(FirstLink, -1);
			for (int i = Links.Count - 1; i >= 0; i--)
			{
				NavLink link = Links[i];
				int poly = link.Edge >> 8;
				link.Next = FirstLink[poly];
				Links[i] = link;
				FirstLink[poly] = i;
			}
		}

		/// <summary>Transforms the local polygon data into world space with the surface's current matrix.</summary>
		public void UpdateWorld()
		{
			Matrix4 m = Surface.LocalToWorld;
			if (WorldVerts == null || WorldVerts.Length != Data.Vertices.Length)
			{
				WorldVerts = new Vector3[Data.Vertices.Length];
			}

			for (int i = 0; i < WorldVerts.Length; i++)
			{
				WorldVerts[i] = Vector3.TransformPosition(Data.Vertices[i], m);
			}

			if (PolyMin == null || PolyMin.Length != Data.PolyCount)
			{
				PolyMin = new Vector3[Data.PolyCount];
				PolyMax = new Vector3[Data.PolyCount];
			}

			WorldMin = new Vector3(float.MaxValue);
			WorldMax = new Vector3(float.MinValue);
			int nvp = Data.VertsPerPoly;
			for (int p = 0; p < Data.PolyCount; p++)
			{
				Vector3 min = new Vector3(float.MaxValue);
				Vector3 max = new Vector3(float.MinValue);
				for (int j = 0; j < nvp; j++)
				{
					int v = Data.PolyVerts[p * nvp + j];
					if (v < 0)
					{
						break;
					}

					min = Vector3.ComponentMin(min, WorldVerts[v]);
					max = Vector3.ComponentMax(max, WorldVerts[v]);
				}

				PolyMin[p] = min;
				PolyMax[p] = max;
				WorldMin = Vector3.ComponentMin(WorldMin, min);
				WorldMax = Vector3.ComponentMax(WorldMax, max);
			}

			if (Data.PolyCount == 0)
			{
				WorldMin = WorldMax = Vector3.TransformPosition(Data.BoundsMin, m);
			}

			if (FirstLink == null || FirstLink.Length != Data.PolyCount)
			{
				FirstLink = new int[Data.PolyCount];
				Array.Fill(FirstLink, -1);
			}
		}

		#endregion
	}

	/// <summary>A registered surface: its data, transform and runtime tiles.</summary>
	internal sealed class NavSurface
	{
		#region PublicFields

		public int Slot;
		public int Salt;
		public NavMeshSurface Component;
		public NavMeshData Data;
		public int AgentTypeId;
		public Matrix4 LocalToWorld;
		public Matrix4 WorldToLocal;
		public NavTile[] Tiles;

		/// <summary>Tile indices per grid cell (x + y * TilesX); layers of one cell share it.</summary>
		public List<int>[] Cells;

		public float LinkToleranceXZ;
		public float LinkToleranceY;

		#endregion

		#region PublicMethods

		public int CellIndex(int x, int y)
		{
			return x + y * Data.TilesX;
		}

		/// <summary>Tile cell range overlapping a world-space box.</summary>
		public bool CellRange(in Vector3 worldMin, in Vector3 worldMax, out int x0, out int y0, out int x1, out int y1)
		{
			Vector3 lmin = new Vector3(float.MaxValue);
			Vector3 lmax = new Vector3(float.MinValue);
			for (int i = 0; i < 8; i++)
			{
				Vector3 corner = new Vector3((i & 1) != 0 ? worldMax.X : worldMin.X, (i & 2) != 0 ? worldMax.Y : worldMin.Y, (i & 4) != 0 ? worldMax.Z : worldMin.Z);
				Vector3 local = Vector3.TransformPosition(corner, WorldToLocal);
				lmin = Vector3.ComponentMin(lmin, local);
				lmax = Vector3.ComponentMax(lmax, local);
			}

			float ts = Data.TileWorldSize;
			x0 = (int)Math.Floor((lmin.X - Data.BoundsMin.X) / ts);
			y0 = (int)Math.Floor((lmin.Z - Data.BoundsMin.Z) / ts);
			x1 = (int)Math.Floor((lmax.X - Data.BoundsMin.X) / ts);
			y1 = (int)Math.Floor((lmax.Z - Data.BoundsMin.Z) / ts);
			if (x1 < 0 || y1 < 0 || x0 >= Data.TilesX || y0 >= Data.TilesY || lmax.Y < Data.BoundsMin.Y - 1f || lmin.Y > Data.BoundsMax.Y + 1f)
			{
				return false;
			}

			x0 = Math.Max(0, x0);
			y0 = Math.Max(0, y0);
			x1 = Math.Min(Data.TilesX - 1, x1);
			y1 = Math.Min(Data.TilesY - 1, y1);
			return true;
		}

		#endregion
	}

	/// <summary>
	///     All registered navmesh surfaces as one navigable world: tiles of every surface in world space, linked
	///     across tile and surface boundaries where polygon edges touch, obstacle carving and the lookup of
	///     polygons by position.
	/// </summary>
	internal sealed class NavMeshWorld
	{
		#region PublicFields

		public readonly List<NavSurface> Surfaces = new List<NavSurface>();

		/// <summary>Bumped whenever tiles change (queries and agents revalidate).</summary>
		public int Version { get; private set; }

		public int SurfaceCount
		{
			get
			{
				int count = 0;
				foreach (NavSurface surface in Surfaces)
				{
					if (surface != null)
					{
						count++;
					}
				}

				return count;
			}
		}

		#endregion

		#region PrivateFields

		private const float EdgeGridCell = 4f;
		private readonly Dictionary<long, List<EdgeRef>> edgeGrid = new Dictionary<long, List<EdgeRef>>();
		private readonly List<EdgeRef> candidates = new List<EdgeRef>();
		private readonly HashSet<NavTile> touched = new HashSet<NavTile>();
		private int nextSalt = 1;

		private struct EdgeRef
		{
			public NavTile Tile;
			public int Poly;
			public int Edge;
		}

		#endregion

		#region PublicMethods

		public NavSurface AddSurface(NavMeshSurface component, NavMeshData data, int agentTypeId, Matrix4 localToWorld)
		{
			int slot = Surfaces.IndexOf(null);
			if (slot < 0)
			{
				slot = Surfaces.Count;
				Surfaces.Add(null);
			}

			var surface = new NavSurface { Slot = slot, Salt = nextSalt++, Component = component, Data = data, AgentTypeId = agentTypeId };
			if (nextSalt >= 0xffff)
			{
				nextSalt = 1;
			}

			SetTransform(surface, localToWorld);
			float cs = data.Settings.CellSize;
			surface.LinkToleranceXZ = cs * 0.75f;
			surface.LinkToleranceY = Math.Max(data.Settings.AgentClimb, data.Settings.CellHeight * 2f);
			surface.Tiles = new NavTile[data.Tiles.Length];
			surface.Cells = new List<int>[Math.Max(1, data.TilesX * data.TilesY)];
			for (int i = 0; i < data.Tiles.Length; i++)
			{
				NavMeshTileData tileData = data.Tiles[i];
				var tile = new NavTile { Surface = surface, Index = i, Salt = nextSalt, Data = tileData, LayerData = i < data.Layers.Length ? data.Layers[i] : null };
				surface.Tiles[i] = tile;
				int cell = surface.CellIndex(tileData.X, tileData.Y);
				(surface.Cells[cell] ??= new List<int>()).Add(i);
			}

			nextSalt = nextSalt + 1 >= 0xffff ? 1 : nextSalt + 1;
			Parallel.For(0, surface.Tiles.Length, i => surface.Tiles[i].UpdateWorld());
			Surfaces[slot] = surface;
			foreach (NavTile tile in surface.Tiles)
			{
				LinkTile(tile);
			}

			Version++;
			return surface;
		}

		public void RemoveSurface(NavSurface surface)
		{
			if (surface == null || surface.Slot >= Surfaces.Count || Surfaces[surface.Slot] != surface)
			{
				return;
			}

			foreach (NavTile tile in surface.Tiles)
			{
				UnlinkTile(tile);
			}

			Surfaces[surface.Slot] = null;
			surface.Tiles = Array.Empty<NavTile>();
			Version++;
		}

		/// <summary>The surface moved: world vertices and links are recomputed.</summary>
		public void UpdateTransform(NavSurface surface, Matrix4 localToWorld)
		{
			if (surface.LocalToWorld == localToWorld)
			{
				return;
			}

			foreach (NavTile tile in surface.Tiles)
			{
				UnlinkTile(tile);
			}

			SetTransform(surface, localToWorld);
			Parallel.For(0, surface.Tiles.Length, i => surface.Tiles[i].UpdateWorld());
			foreach (NavTile tile in surface.Tiles)
			{
				LinkTile(tile);
			}

			Version++;
		}

		/// <summary>Swaps the polygons of a tile (carving); null data empties the tile.</summary>
		public void ReplaceTile(NavTile tile, NavMeshTileData data, bool carved)
		{
			UnlinkTile(tile);
			tile.Data = data ?? new NavMeshTileData { X = tile.Data.X, Y = tile.Data.Y, Layer = tile.Data.Layer, BoundsMin = tile.Data.BoundsMin, BoundsMax = tile.Data.BoundsMax };
			tile.Carved = carved;
			tile.Salt = nextSalt;
			nextSalt = nextSalt + 1 >= 0xffff ? 1 : nextSalt + 1;
			tile.UpdateWorld();
			LinkTile(tile);
			Version++;
		}

		/// <summary>Resolves a handle; null when it is stale or invalid.</summary>
		public NavTile GetTile(PolyRef reference, out int poly)
		{
			poly = reference.Poly;
			if (reference.IsNull)
			{
				return null;
			}

			int slot = reference.Surface;
			if (slot >= Surfaces.Count)
			{
				return null;
			}

			NavSurface surface = Surfaces[slot];
			if (surface == null || reference.Tile >= surface.Tiles.Length)
			{
				return null;
			}

			NavTile tile = surface.Tiles[reference.Tile];
			if (tile.Salt != reference.Salt || poly >= tile.PolyCount)
			{
				return null;
			}

			return tile;
		}

		public bool IsValid(PolyRef reference)
		{
			return GetTile(reference, out _) != null;
		}

		/// <summary>Tiles (of surfaces matching the agent type, -1 for any) whose bounds overlap a world box.</summary>
		public void QueryTiles(in Vector3 min, in Vector3 max, int agentTypeId, List<NavTile> result)
		{
			foreach (NavSurface surface in Surfaces)
			{
				if (surface == null || (agentTypeId >= 0 && surface.AgentTypeId != agentTypeId) || surface.Tiles.Length == 0)
				{
					continue;
				}

				if (!surface.CellRange(min, max, out int x0, out int y0, out int x1, out int y1))
				{
					continue;
				}

				for (int y = y0; y <= y1; y++)
				{
					for (int x = x0; x <= x1; x++)
					{
						List<int> cell = surface.Cells[surface.CellIndex(x, y)];
						if (cell == null)
						{
							continue;
						}

						foreach (int index in cell)
						{
							NavTile tile = surface.Tiles[index];
							if (tile.PolyCount > 0 && NavMath.OverlapBounds(min, max, tile.WorldMin, tile.WorldMax))
							{
								result.Add(tile);
							}
						}
					}
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void SetTransform(NavSurface surface, Matrix4 localToWorld)
		{
			surface.LocalToWorld = localToWorld;
			surface.WorldToLocal = localToWorld.Inverted();
		}

		private static long GridKey(int x, int z)
		{
			return ((long)x << 32) ^ (uint)z;
		}

		private void ForEachGridCell(in Vector3 a, in Vector3 b, float pad, Action<long> action)
		{
			int x0 = (int)Math.Floor((Math.Min(a.X, b.X) - pad) / EdgeGridCell);
			int x1 = (int)Math.Floor((Math.Max(a.X, b.X) + pad) / EdgeGridCell);
			int z0 = (int)Math.Floor((Math.Min(a.Z, b.Z) - pad) / EdgeGridCell);
			int z1 = (int)Math.Floor((Math.Max(a.Z, b.Z) + pad) / EdgeGridCell);
			for (int z = z0; z <= z1; z++)
			{
				for (int x = x0; x <= x1; x++)
				{
					action(GridKey(x, z));
				}
			}
		}

		/// <summary>Registers the tile's boundary edges and connects them to touching edges of other tiles.</summary>
		private void LinkTile(NavTile tile)
		{
			if (tile.PolyCount == 0)
			{
				return;
			}

			touched.Clear();
			for (int p = 0; p < tile.PolyCount; p++)
			{
				int nv = tile.PolyVertCount(p);
				for (int e = 0; e < nv; e++)
				{
					if (tile.Neighbor(p, e) >= 0)
					{
						continue;
					}

					Vector3 a = tile.Vertex(p, e);
					Vector3 b = tile.Vertex(p, (e + 1) % nv);
					ConnectEdge(tile, p, e, a, b);

					var edge = new EdgeRef { Tile = tile, Poly = p, Edge = e };
					ForEachGridCell(a, b, 0f, key =>
					{
						if (!edgeGrid.TryGetValue(key, out List<EdgeRef> list))
						{
							list = new List<EdgeRef>();
							edgeGrid[key] = list;
						}

						list.Add(edge);
					});
				}
			}

			tile.RebuildLinkChains();
			foreach (NavTile other in touched)
			{
				other.RebuildLinkChains();
			}
		}

		private void ConnectEdge(NavTile tile, int poly, int edge, in Vector3 a, in Vector3 b)
		{
			float tolXZ = tile.Surface.LinkToleranceXZ;
			float tolY = tile.Surface.LinkToleranceY;
			candidates.Clear();
			ForEachGridCell(a, b, tolXZ, key =>
			{
				if (edgeGrid.TryGetValue(key, out List<EdgeRef> list))
				{
					candidates.AddRange(list);
				}
			});

			Vector3 ab = b - a;
			float length = NavMath.Len2D(ab);
			if (length < 1e-5f)
			{
				return;
			}

			float invLength = 1f / length;
			float dirX = ab.X * invLength;
			float dirZ = ab.Z * invLength;
			for (int i = 0; i < candidates.Count; i++)
			{
				EdgeRef candidate = candidates[i];
				if (candidate.Tile == tile)
				{
					continue;
				}

				bool duplicate = false;
				for (int k = 0; k < i; k++)
				{
					if (candidates[k].Tile == candidate.Tile && candidates[k].Poly == candidate.Poly && candidates[k].Edge == candidate.Edge)
					{
						duplicate = true;
						break;
					}
				}

				if (duplicate)
				{
					continue;
				}

				NavTile other = candidate.Tile;
				float otherTolXZ = Math.Max(tolXZ, other.Surface.LinkToleranceXZ);
				float otherTolY = Math.Max(tolY, other.Surface.LinkToleranceY);
				int onv = other.PolyVertCount(candidate.Poly);
				Vector3 c = other.Vertex(candidate.Poly, candidate.Edge);
				Vector3 d = other.Vertex(candidate.Poly, (candidate.Edge + 1) % onv);
				Vector3 cd = d - c;
				float cdLength = NavMath.Len2D(cd);
				if (cdLength < 1e-5f)
				{
					continue;
				}

				// Opposite directions (consistent winding on both sides), nearly parallel.
				float cosine = (dirX * cd.X + dirZ * cd.Z) / cdLength;
				if (cosine > -0.95f)
				{
					continue;
				}

				// Parameters of the candidate's ends along this edge.
				float tc = ((c.X - a.X) * dirX + (c.Z - a.Z) * dirZ) * invLength;
				float td = ((d.X - a.X) * dirX + (d.Z - a.Z) * dirZ) * invLength;
				float tmin = Math.Max(0f, Math.Min(tc, td));
				float tmax = Math.Min(1f, Math.Max(tc, td));
				if (tmax - tmin < 0.01f)
				{
					continue;
				}

				// Perpendicular distance of the candidate from this edge's line, measured at the overlap ends.
				float distC = Math.Abs((c.X - a.X) * dirZ - (c.Z - a.Z) * dirX);
				float distD = Math.Abs((d.X - a.X) * dirZ - (d.Z - a.Z) * dirX);
				if (Math.Max(distC, distD) > otherTolXZ)
				{
					continue;
				}

				// Heights at the overlap ends on both edges.
				float ya0 = a.Y + ab.Y * tmin;
				float ya1 = a.Y + ab.Y * tmax;
				float sc0 = (tmin - td) / (tc - td);
				float sc1 = (tmax - td) / (tc - td);
				float yc0 = d.Y + (c.Y - d.Y) * sc0;
				float yc1 = d.Y + (c.Y - d.Y) * sc1;
				if (Math.Abs(ya0 - yc0) > otherTolY || Math.Abs(ya1 - yc1) > otherTolY)
				{
					continue;
				}

				// Parameters along the candidate edge (c -> d).
				float ptmin = Math.Clamp(Math.Min(1f - sc0, 1f - sc1), 0f, 1f);
				float ptmax = Math.Clamp(Math.Max(1f - sc0, 1f - sc1), 0f, 1f);

				tile.Links.Add(new NavLink { Ref = other.Ref(candidate.Poly), Edge = (poly << 8) | edge, TMin = tmin, TMax = tmax });
				other.Links.Add(new NavLink { Ref = tile.Ref(poly), Edge = (candidate.Poly << 8) | candidate.Edge, TMin = ptmin, TMax = ptmax });
				touched.Add(other);
			}
		}

		/// <summary>Removes the tile's edges from the grid and every link to or from it.</summary>
		private void UnlinkTile(NavTile tile)
		{
			if (tile.PolyCount == 0 && tile.Links.Count == 0)
			{
				return;
			}

			var neighbors = new HashSet<NavTile>();
			foreach (NavLink link in tile.Links)
			{
				NavTile other = GetTile(link.Ref, out _);
				if (other != null)
				{
					neighbors.Add(other);
				}
			}

			foreach (NavTile other in neighbors)
			{
				other.Links.RemoveAll(link => link.Ref.Surface == tile.Surface.Slot && link.Ref.Tile == tile.Index && link.Ref.Salt == tile.Salt);
				other.RebuildLinkChains();
			}

			tile.Links.Clear();
			if (tile.FirstLink != null)
			{
				Array.Fill(tile.FirstLink, -1);
			}

			int nvp = tile.VertsPerPoly;
			for (int p = 0; p < tile.PolyCount; p++)
			{
				int nv = tile.PolyVertCount(p);
				for (int e = 0; e < nv; e++)
				{
					if (tile.Neighbor(p, e) >= 0)
					{
						continue;
					}

					Vector3 a = tile.Vertex(p, e);
					Vector3 b = tile.Vertex(p, (e + 1) % nv);
					ForEachGridCell(a, b, 0f, key =>
					{
						if (edgeGrid.TryGetValue(key, out List<EdgeRef> list))
						{
							list.RemoveAll(edge => edge.Tile == tile);
							if (list.Count == 0)
							{
								edgeGrid.Remove(key);
							}
						}
					});
				}
			}
		}

		#endregion
	}
}
