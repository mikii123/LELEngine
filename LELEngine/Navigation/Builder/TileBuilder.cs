using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Navigation.Builder
{
	/// <summary>
	///     Turns one heightfield layer into polygons: monotone regions, their contours (simplified) and a convex
	///     polygon mesh with adjacency (Recast's DetourTileCacheBuilder). Runs at bake time and again whenever an
	///     obstacle carves the tile, on the layer with the obstacle cells marked unwalkable.
	/// </summary>
	internal static class TileBuilder
	{
		#region PublicFields

		public const ushort NullRegion = 0xffff;
		public const int RegionWall = -1;
		public const int RegionPortalBase = -10;

		#endregion

		#region PublicMethods

		/// <summary>Polygons of a layer (local space). Returns null when the layer has no walkable cells.</summary>
		public static NavMeshTileData Build(HeightfieldLayer layer, int tileX, int tileY, int layerIndex, int walkableClimb, float maxError, int maxEdgeLength, int minRegionCells, int vertsPerPoly)
		{
			ushort[] regs = BuildRegions(layer, walkableClimb, minRegionCells);
			List<Contour> contours = BuildContours(layer, regs, walkableClimb, maxError, maxEdgeLength);
			if (contours.Count == 0)
			{
				return null;
			}

			PolyMesh mesh = BuildPolyMesh(contours, vertsPerPoly);
			if (mesh.PolyCount == 0)
			{
				return null;
			}

			var tile = new NavMeshTileData
			{
				X = tileX, Y = tileY, Layer = layerIndex, VertsPerPoly = vertsPerPoly, PolyCount = mesh.PolyCount,
				Vertices = new Vector3[mesh.VertexCount], PolyVerts = new int[mesh.PolyCount * vertsPerPoly], PolyNeighbors = new int[mesh.PolyCount * vertsPerPoly],
				PolyAreas = new byte[mesh.PolyCount]
			};
			Vector3 origin = layer.BoundsMin;
			Vector3 bmin = new Vector3(float.MaxValue);
			Vector3 bmax = new Vector3(float.MinValue);
			for (int i = 0; i < mesh.VertexCount; i++)
			{
				Vector3 v = new Vector3(origin.X + mesh.Verts[i * 3] * layer.CellSize, origin.Y + mesh.Verts[i * 3 + 1] * layer.CellHeight, origin.Z + mesh.Verts[i * 3 + 2] * layer.CellSize);
				tile.Vertices[i] = v;
				bmin = Vector3.ComponentMin(bmin, v);
				bmax = Vector3.ComponentMax(bmax, v);
			}

			for (int p = 0; p < mesh.PolyCount; p++)
			{
				for (int j = 0; j < vertsPerPoly; j++)
				{
					int v = mesh.Polys[p * vertsPerPoly * 2 + j];
					tile.PolyVerts[p * vertsPerPoly + j] = v == PolyMesh.NullIndex ? -1 : v;
					int n = mesh.Polys[p * vertsPerPoly * 2 + vertsPerPoly + j];
					tile.PolyNeighbors[p * vertsPerPoly + j] = n == PolyMesh.NullIndex ? -1 : n;
				}

				tile.PolyAreas[p] = (byte)NavMeshArea.ToPublic(mesh.Areas[p]);
			}

			tile.BoundsMin = bmin;
			tile.BoundsMax = bmax;
			return tile;
		}

		#endregion

		#region Regions

		private sealed class MonotoneRegion
		{
			public int Cells;
			public ushort RegionId;
			public byte Area;
			public bool TouchesPortal;
			public readonly List<ushort> Neighbors = new List<ushort>();
		}

		/// <summary>Monotone regions of the layer's walkable cells, merged where the merge keeps them simple.</summary>
		public static ushort[] BuildRegions(HeightfieldLayer layer, int walkableClimb, int minRegionCells)
		{
			int w = layer.Width;
			int h = layer.Height;
			var regs = new ushort[w * h];
			Array.Fill(regs, NullRegion);

			var sweepNei = new ushort[w + 1];
			var sweepNs = new int[w + 1];
			var sweepId = new ushort[w + 1];
			var prevCount = new List<int>();
			int regionCount = 0;

			for (int y = 0; y < h; y++)
			{
				prevCount.Clear();
				for (int i = 0; i < regionCount; i++)
				{
					prevCount.Add(0);
				}

				int sweeps = 0;
				for (int x = 0; x < w; x++)
				{
					int index = x + y * w;
					if (layer.Areas[index] == NavMeshArea.Null)
					{
						continue;
					}

					ushort sid = NullRegion;
					int con = layer.Cons[index] & 0xf;
					if ((con & 1) != 0 && x > 0)
					{
						int xi = index - 1;
						if (layer.Areas[xi] == layer.Areas[index] && regs[xi] != NullRegion)
						{
							sid = regs[xi];
						}
					}

					if (sid == NullRegion)
					{
						sid = (ushort)sweeps++;
						sweepNei[sid] = NullRegion;
						sweepNs[sid] = 0;
					}

					if ((con & 8) != 0 && y > 0)
					{
						int yi = index - w;
						ushort nr = regs[yi];
						if (nr != NullRegion && layer.Areas[yi] == layer.Areas[index])
						{
							if (sweepNs[sid] == 0)
							{
								sweepNei[sid] = nr;
							}

							if (sweepNei[sid] == nr)
							{
								sweepNs[sid]++;
								prevCount[nr]++;
							}
							else
							{
								sweepNei[sid] = NullRegion;
							}
						}
					}

					regs[index] = sid;
				}

				for (int i = 0; i < sweeps; i++)
				{
					if (sweepNei[i] != NullRegion && prevCount[sweepNei[i]] == sweepNs[i])
					{
						sweepId[i] = sweepNei[i];
					}
					else
					{
						sweepId[i] = (ushort)regionCount++;
					}
				}

				for (int x = 0; x < w; x++)
				{
					int index = x + y * w;
					if (regs[index] != NullRegion)
					{
						regs[index] = sweepId[regs[index]];
					}
				}
			}

			var regions = new MonotoneRegion[regionCount];
			for (int i = 0; i < regionCount; i++)
			{
				regions[i] = new MonotoneRegion { RegionId = (ushort)i };
			}

			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int index = x + y * w;
					ushort ri = regs[index];
					if (ri == NullRegion)
					{
						continue;
					}

					MonotoneRegion region = regions[ri];
					region.Cells++;
					region.Area = layer.Areas[index];
					if ((layer.Cons[index] >> 4) != 0)
					{
						region.TouchesPortal = true;
					}

					int con = layer.Cons[index] & 0xf;
					for (int dir = 0; dir < 4; dir++)
					{
						if ((con & (1 << dir)) == 0)
						{
							continue;
						}

						int ni = index + Dir.X[dir] + Dir.Z[dir] * w;
						ushort rai = regs[ni];
						if (rai != NullRegion && rai != ri && !region.Neighbors.Contains(rai))
						{
							region.Neighbors.Add(rai);
						}
					}
				}
			}

			// Merge adjacent regions of the same area when they touch along exactly one border (keeps contours simple).
			for (int i = 0; i < regionCount; i++)
			{
				MonotoneRegion region = regions[i];
				int merge = -1;
				int mergeCells = 0;
				foreach (ushort nei in region.Neighbors)
				{
					MonotoneRegion regn = regions[nei];
					if (region.RegionId == regn.RegionId || region.Area != regn.Area)
					{
						continue;
					}

					if (regn.Cells > mergeCells && CanMerge(region.RegionId, regn.RegionId, regions))
					{
						mergeCells = regn.Cells;
						merge = nei;
					}
				}

				if (merge != -1)
				{
					ushort oldId = region.RegionId;
					ushort newId = regions[merge].RegionId;
					foreach (MonotoneRegion other in regions)
					{
						if (other.RegionId == oldId)
						{
							other.RegionId = newId;
						}
					}
				}
			}

			// Drop small regions that do not continue into a neighbor tile or layer.
			var cellsPerId = new Dictionary<ushort, int>();
			var portalPerId = new Dictionary<ushort, bool>();
			foreach (MonotoneRegion region in regions)
			{
				cellsPerId.TryGetValue(region.RegionId, out int cells);
				cellsPerId[region.RegionId] = cells + region.Cells;
				portalPerId.TryGetValue(region.RegionId, out bool portal);
				portalPerId[region.RegionId] = portal || region.TouchesPortal;
			}

			var remap = new Dictionary<ushort, ushort>();
			for (int i = 0; i < regionCount; i++)
			{
				ushort id = regions[i].RegionId;
				if (remap.ContainsKey(id))
				{
					continue;
				}

				bool keep = cellsPerId[id] >= minRegionCells || portalPerId[id];
				remap[id] = keep ? (ushort)remap.Count : NullRegion;
			}

			// Compacted ids (NullRegion for dropped regions); NullRegion entries do not count toward the id space.
			ushort next = 0;
			var compact = new Dictionary<ushort, ushort>();
			foreach (KeyValuePair<ushort, ushort> pair in remap)
			{
				compact[pair.Key] = pair.Value == NullRegion ? NullRegion : next++;
			}

			for (int i = 0; i < regs.Length; i++)
			{
				if (regs[i] == NullRegion)
				{
					continue;
				}

				ushort id = compact[regions[regs[i]].RegionId];
				regs[i] = id;
				if (id == NullRegion)
				{
					layer.Areas[i] = NavMeshArea.Null;
				}
			}

			return regs;
		}

		private static bool CanMerge(ushort oldId, ushort newId, MonotoneRegion[] regions)
		{
			int count = 0;
			foreach (MonotoneRegion region in regions)
			{
				if (region.RegionId != oldId)
				{
					continue;
				}

				foreach (ushort nei in region.Neighbors)
				{
					if (regions[nei].RegionId == newId)
					{
						count++;
					}
				}
			}

			return count == 1;
		}

		#endregion

		#region Contours

		private sealed class Contour
		{
			/// <summary>Vertices as (x, y, z, r) in cell units; r is the neighbor region code of the edge starting at the vertex.</summary>
			public int[] Verts;

			public int Count;
			public ushort Region;
			public byte Area;
		}

		private sealed class TempContour
		{
			public readonly List<int> Verts = new List<int>();
			public readonly List<int> Poly = new List<int>();
			public int Count => Verts.Count / 4;
		}

		private static int GetNeighborRegion(HeightfieldLayer layer, ushort[] regs, int x, int y, int dir)
		{
			int w = layer.Width;
			int ia = x + y * w;
			int con = layer.Cons[ia] & 0xf;
			int portal = layer.Cons[ia] >> 4;
			int mask = 1 << dir;
			if ((con & mask) == 0)
			{
				return (portal & mask) != 0 ? RegionPortalBase - dir : RegionWall;
			}

			int ib = x + Dir.X[dir] + (y + Dir.Z[dir]) * w;
			return regs[ib] == NullRegion ? RegionWall : regs[ib];
		}

		private static bool WalkContour(HeightfieldLayer layer, ushort[] regs, int x, int y, TempContour contour)
		{
			int w = layer.Width;
			int h = layer.Height;
			contour.Verts.Clear();
			int startX = x;
			int startY = y;
			int startDir = -1;
			for (int i = 0; i < 4; i++)
			{
				int dir = (i + 3) & 3;
				int rn = GetNeighborRegion(layer, regs, x, y, dir);
				if (rn != regs[x + y * w])
				{
					startDir = dir;
					break;
				}
			}

			if (startDir == -1)
			{
				return true;
			}

			int curDir = startDir;
			int maxIter = w * h;
			int iter = 0;
			while (iter < maxIter)
			{
				int rn = GetNeighborRegion(layer, regs, x, y, curDir);
				int nx = x;
				int ny = y;
				int ndir = curDir;
				if (rn != regs[x + y * w])
				{
					// Solid edge: add the vertex at the edge start.
					int px = x;
					int pz = y;
					switch (curDir)
					{
						case 0: pz++; break;
						case 1: px++; pz++; break;
						case 2: px++; break;
					}

					AppendVertex(contour, px, layer.Heights[x + y * w], pz, rn);
					ndir = (curDir + 1) & 3;
				}
				else
				{
					nx = x + Dir.X[curDir];
					ny = y + Dir.Z[curDir];
					ndir = (curDir + 3) & 3;
				}

				if (iter > 0 && x == startX && y == startY && curDir == startDir)
				{
					break;
				}

				x = nx;
				y = ny;
				curDir = ndir;
				iter++;
			}

			// Remove the last vertex when it duplicates the first.
			int count = contour.Count;
			if (count > 1)
			{
				int a = 0;
				int b = (count - 1) * 4;
				if (contour.Verts[a] == contour.Verts[b] && contour.Verts[a + 2] == contour.Verts[b + 2])
				{
					contour.Verts.RemoveRange(b, 4);
				}
			}

			return true;
		}

		private static void AppendVertex(TempContour contour, int x, int y, int z, int r)
		{
			int count = contour.Count;
			if (count > 1)
			{
				int pa = (count - 2) * 4;
				int pb = (count - 1) * 4;
				if (contour.Verts[pb + 3] == r)
				{
					if (contour.Verts[pa] == contour.Verts[pb] && contour.Verts[pb] == x)
					{
						// Aligned along x: extend.
						contour.Verts[pb + 1] = y;
						contour.Verts[pb + 2] = z;
						return;
					}

					if (contour.Verts[pa + 2] == contour.Verts[pb + 2] && contour.Verts[pb + 2] == z)
					{
						contour.Verts[pb + 1] = y;
						contour.Verts[pb] = x;
						return;
					}
				}
			}

			contour.Verts.Add(x);
			contour.Verts.Add(y);
			contour.Verts.Add(z);
			contour.Verts.Add(r);
		}

		private static int GetCornerHeight(HeightfieldLayer layer, int x, int y, int z, int walkableClimb)
		{
			int w = layer.Width;
			int h = layer.Height;
			int height = 0;
			int n = 0;
			for (int dz = -1; dz <= 0; dz++)
			{
				for (int dx = -1; dx <= 0; dx++)
				{
					int px = x + dx;
					int pz = z + dz;
					if (px < 0 || pz < 0 || px >= w || pz >= h)
					{
						continue;
					}

					int index = px + pz * w;
					int lh = layer.Heights[index];
					if (lh != HeightfieldLayer.EmptyHeight && Math.Abs(lh - y) <= walkableClimb && layer.Areas[index] != NavMeshArea.Null)
					{
						height = Math.Max(height, lh);
						n++;
					}
				}
			}

			return n > 0 ? height : y;
		}

		private static float DistancePointSegment(int x, int z, int px, int pz, int qx, int qz)
		{
			float pqx = qx - px;
			float pqz = qz - pz;
			float dx = x - px;
			float dz = z - pz;
			float d = pqx * pqx + pqz * pqz;
			float t = pqx * dx + pqz * dz;
			if (d > 0f)
			{
				t /= d;
			}

			t = Math.Clamp(t, 0f, 1f);
			dx = px + t * pqx - x;
			dz = pz + t * pqz - z;
			return dx * dx + dz * dz;
		}

		private static void SimplifyContour(TempContour contour, float maxError, int maxEdgeLength)
		{
			List<int> verts = contour.Verts;
			List<int> poly = contour.Poly;
			poly.Clear();
			int count = contour.Count;

			// Hard points: where the neighbor code changes.
			for (int i = 0; i < count; i++)
			{
				int j = (i + 1) % count;
				if (verts[j * 4 + 3] != verts[i * 4 + 3])
				{
					poly.Add(i);
				}
			}

			if (poly.Count < 2)
			{
				// No transitions: start from the lower-left and upper-right vertices.
				int llx = verts[0], llz = verts[2], lli = 0;
				int urx = verts[0], urz = verts[2], uri = 0;
				for (int i = 1; i < count; i++)
				{
					int x = verts[i * 4];
					int z = verts[i * 4 + 2];
					if (x < llx || (x == llx && z < llz))
					{
						llx = x;
						llz = z;
						lli = i;
					}

					if (x > urx || (x == urx && z > urz))
					{
						urx = x;
						urz = z;
						uri = i;
					}
				}

				poly.Clear();
				poly.Add(lli);
				poly.Add(uri);
			}

			// Add points until every raw point is within the error of the simplified shape.
			float maxErrorSq = maxError * maxError;
			for (int i = 0; i < poly.Count;)
			{
				int ii = (i + 1) % poly.Count;
				int ai = poly[i];
				int ax = verts[ai * 4];
				int az = verts[ai * 4 + 2];
				int bi = poly[ii];
				int bx = verts[bi * 4];
				int bz = verts[bi * 4 + 2];

				float maxd = 0f;
				int maxi = -1;
				int ci, cinc, endi;
				if (bx > ax || (bx == ax && bz > az))
				{
					cinc = 1;
					ci = (ai + cinc) % count;
					endi = bi;
				}
				else
				{
					cinc = count - 1;
					ci = (bi + cinc) % count;
					endi = ai;
				}

				while (ci != endi)
				{
					float d = DistancePointSegment(verts[ci * 4], verts[ci * 4 + 2], ax, az, bx, bz);
					if (d > maxd)
					{
						maxd = d;
						maxi = ci;
					}

					ci = (ci + cinc) % count;
				}

				if (maxi != -1 && maxd > maxErrorSq)
				{
					poly.Insert(i + 1, maxi);
				}
				else
				{
					i++;
				}
			}

			// Split long edges.
			if (maxEdgeLength > 0)
			{
				for (int i = 0; i < poly.Count;)
				{
					int ii = (i + 1) % poly.Count;
					int ai = poly[i];
					int bi = poly[ii];
					int ax = verts[ai * 4], az = verts[ai * 4 + 2];
					int bx = verts[bi * 4], bz = verts[bi * 4 + 2];
					int maxi = -1;
					int ci = (ai + 1) % count;
					if ((bx - ax) * (bx - ax) + (bz - az) * (bz - az) > maxEdgeLength * maxEdgeLength && ci != bi)
					{
						int steps = bi < ai ? bi + (count - ai) : bi - ai;
						if (steps > 1)
						{
							maxi = (ai + steps / 2) % count;
						}
					}

					if (maxi != -1)
					{
						poly.Insert(i + 1, maxi);
					}
					else
					{
						i++;
					}
				}
			}

			// Rotate so the result starts at the lowest raw index, then rewrite the vertices.
			int start = 0;
			for (int i = 1; i < poly.Count; i++)
			{
				if (poly[i] < poly[start])
				{
					start = i;
				}
			}

			var result = new List<int>(poly.Count * 4);
			for (int i = 0; i < poly.Count; i++)
			{
				int j = (start + i) % poly.Count;
				int src = poly[j] * 4;
				result.Add(verts[src]);
				result.Add(verts[src + 1]);
				result.Add(verts[src + 2]);
				result.Add(verts[src + 3]);
			}

			verts.Clear();
			verts.AddRange(result);
		}

		private static List<Contour> BuildContours(HeightfieldLayer layer, ushort[] regs, int walkableClimb, float maxError, int maxEdgeLength)
		{
			int w = layer.Width;
			int h = layer.Height;
			var contours = new List<Contour>();
			var temp = new TempContour();
			var done = new HashSet<ushort>();

			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int index = x + y * w;
					ushort ri = regs[index];
					if (ri == NullRegion || done.Contains(ri))
					{
						continue;
					}

					done.Add(ri);
					WalkContour(layer, regs, x, y, temp);
					SimplifyContour(temp, maxError, maxEdgeLength);

					int count = temp.Count;
					if (count < 3)
					{
						continue;
					}

					var contour = new Contour { Verts = new int[count * 4], Count = count, Region = ri, Area = layer.Areas[index] };
					for (int i = 0, j = count - 1; i < count; j = i++)
					{
						int vx = temp.Verts[j * 4];
						int vy = temp.Verts[j * 4 + 1];
						int vz = temp.Verts[j * 4 + 2];
						contour.Verts[j * 4] = vx;
						contour.Verts[j * 4 + 1] = GetCornerHeight(layer, vx, vy, vz, walkableClimb);
						contour.Verts[j * 4 + 2] = vz;
						contour.Verts[j * 4 + 3] = temp.Verts[i * 4 + 3];
					}

					contours.Add(contour);
				}
			}

			return contours;
		}

		#endregion

		#region PolyMesh

		private sealed class PolyMesh
		{
			public const int NullIndex = -1;
			public int[] Verts;
			public int VertexCount;
			public int[] Polys;
			public int PolyCount;
			public byte[] Areas;
		}

		private const int VertexBucketCount = 1 << 12;

		private static int VertexHash(int x, int z)
		{
			uint h1 = 0x8da6b343;
			uint h3 = 0xcb1ab31f;
			uint n = h1 * (uint)x + h3 * (uint)z;
			return (int)(n & (VertexBucketCount - 1));
		}

		private static int AddVertex(int x, int y, int z, int[] verts, int[] firstVert, int[] nextVert, ref int count)
		{
			int bucket = VertexHash(x, z);
			int i = firstVert[bucket];
			while (i != PolyMesh.NullIndex)
			{
				if (verts[i * 3] == x && verts[i * 3 + 2] == z && Math.Abs(verts[i * 3 + 1] - y) <= 2)
				{
					return i;
				}

				i = nextVert[i];
			}

			i = count++;
			verts[i * 3] = x;
			verts[i * 3 + 1] = y;
			verts[i * 3 + 2] = z;
			nextVert[i] = firstVert[bucket];
			firstVert[bucket] = i;
			return i;
		}

		private static int Prev(int i, int n) => i - 1 >= 0 ? i - 1 : n - 1;
		private static int Next(int i, int n) => i + 1 < n ? i + 1 : 0;

		private static int Area2(int[] v, int a, int b, int c)
		{
			return (v[b] - v[a]) * (v[c + 2] - v[a + 2]) - (v[c] - v[a]) * (v[b + 2] - v[a + 2]);
		}

		private static bool Left(int[] v, int a, int b, int c) => Area2(v, a, b, c) < 0;
		private static bool LeftOn(int[] v, int a, int b, int c) => Area2(v, a, b, c) <= 0;
		private static bool Collinear(int[] v, int a, int b, int c) => Area2(v, a, b, c) == 0;

		private static bool IntersectProp(int[] v, int a, int b, int c, int d)
		{
			if (Collinear(v, a, b, c) || Collinear(v, a, b, d) || Collinear(v, c, d, a) || Collinear(v, c, d, b))
			{
				return false;
			}

			return (Left(v, a, b, c) ^ Left(v, a, b, d)) && (Left(v, c, d, a) ^ Left(v, c, d, b));
		}

		private static bool Between(int[] v, int a, int b, int c)
		{
			if (!Collinear(v, a, b, c))
			{
				return false;
			}

			if (v[a] != v[b])
			{
				return (v[a] <= v[c] && v[c] <= v[b]) || (v[a] >= v[c] && v[c] >= v[b]);
			}

			return (v[a + 2] <= v[c + 2] && v[c + 2] <= v[b + 2]) || (v[a + 2] >= v[c + 2] && v[c + 2] >= v[b + 2]);
		}

		private static bool Intersect(int[] v, int a, int b, int c, int d)
		{
			return IntersectProp(v, a, b, c, d) || Between(v, a, b, c) || Between(v, a, b, d) || Between(v, c, d, a) || Between(v, c, d, b);
		}

		private static bool VEqual(int[] v, int a, int b) => v[a] == v[b] && v[a + 2] == v[b + 2];

		private static bool Diagonalie(int i, int j, int n, int[] verts, int[] indices, bool loose)
		{
			int d0 = (indices[i] & 0x0fffffff) * 4;
			int d1 = (indices[j] & 0x0fffffff) * 4;
			for (int k = 0; k < n; k++)
			{
				int k1 = Next(k, n);
				if (k == i || k1 == i || k == j || k1 == j)
				{
					continue;
				}

				int p0 = (indices[k] & 0x0fffffff) * 4;
				int p1 = (indices[k1] & 0x0fffffff) * 4;
				if (VEqual(verts, d0, p0) || VEqual(verts, d1, p0) || VEqual(verts, d0, p1) || VEqual(verts, d1, p1))
				{
					continue;
				}

				if (loose ? IntersectProp(verts, d0, d1, p0, p1) : Intersect(verts, d0, d1, p0, p1))
				{
					return false;
				}
			}

			return true;
		}

		private static bool InCone(int i, int j, int n, int[] verts, int[] indices, bool loose)
		{
			int pi = (indices[i] & 0x0fffffff) * 4;
			int pj = (indices[j] & 0x0fffffff) * 4;
			int pi1 = (indices[Next(i, n)] & 0x0fffffff) * 4;
			int pin1 = (indices[Prev(i, n)] & 0x0fffffff) * 4;
			if (LeftOn(verts, pin1, pi, pi1))
			{
				return loose
					? LeftOn(verts, pi, pj, pin1) && LeftOn(verts, pj, pi, pi1)
					: Left(verts, pi, pj, pin1) && Left(verts, pj, pi, pi1);
			}

			return !(LeftOn(verts, pi, pj, pi1) && LeftOn(verts, pj, pi, pin1));
		}

		private static bool Diagonal(int i, int j, int n, int[] verts, int[] indices, bool loose)
		{
			return InCone(i, j, n, verts, indices, loose) && Diagonalie(i, j, n, verts, indices, loose);
		}

		/// <summary>Ear-clipping triangulation of a contour (x, y, z, r per vertex); returns the triangle count (negative when it failed part way).</summary>
		private static int Triangulate(int n, int[] verts, int[] indices, int[] tris)
		{
			int ntris = 0;
			int dst = 0;
			for (int i = 0; i < n; i++)
			{
				int i1 = Next(i, n);
				int i2 = Next(i1, n);
				if (Diagonal(i, i2, n, verts, indices, false))
				{
					indices[i1] |= unchecked((int)0x80000000);
				}
			}

			while (n > 3)
			{
				int minLen = -1;
				int mini = -1;
				for (int i = 0; i < n; i++)
				{
					int i1 = Next(i, n);
					if ((indices[i1] & unchecked((int)0x80000000)) != 0)
					{
						int p0 = (indices[i] & 0x0fffffff) * 4;
						int p2 = (indices[Next(i1, n)] & 0x0fffffff) * 4;
						int dx = verts[p2] - verts[p0];
						int dz = verts[p2 + 2] - verts[p0 + 2];
						int len = dx * dx + dz * dz;
						if (minLen < 0 || len < minLen)
						{
							minLen = len;
							mini = i;
						}
					}
				}

				if (mini == -1)
				{
					// Overlapping segments in the contour: try the loose diagonal test.
					minLen = -1;
					for (int i = 0; i < n; i++)
					{
						int i1 = Next(i, n);
						int i2 = Next(i1, n);
						if (Diagonal(i, i2, n, verts, indices, true))
						{
							int p0 = (indices[i] & 0x0fffffff) * 4;
							int p2 = (indices[Next(i1, n)] & 0x0fffffff) * 4;
							int dx = verts[p2] - verts[p0];
							int dz = verts[p2 + 2] - verts[p0 + 2];
							int len = dx * dx + dz * dz;
							if (minLen < 0 || len < minLen)
							{
								minLen = len;
								mini = i;
							}
						}
					}

					if (mini == -1)
					{
						return -ntris;
					}
				}

				int ii = mini;
				int ii1 = Next(ii, n);
				int ii2 = Next(ii1, n);
				tris[dst++] = indices[ii] & 0x0fffffff;
				tris[dst++] = indices[ii1] & 0x0fffffff;
				tris[dst++] = indices[ii2] & 0x0fffffff;
				ntris++;

				n--;
				for (int k = ii1; k < n; k++)
				{
					indices[k] = indices[k + 1];
				}

				if (ii1 >= n)
				{
					ii1 = 0;
				}

				ii = Prev(ii1, n);
				if (Diagonal(Prev(ii, n), ii1, n, verts, indices, false))
				{
					indices[ii] |= unchecked((int)0x80000000);
				}
				else
				{
					indices[ii] &= 0x0fffffff;
				}

				if (Diagonal(ii, Next(ii1, n), n, verts, indices, false))
				{
					indices[ii1] |= unchecked((int)0x80000000);
				}
				else
				{
					indices[ii1] &= 0x0fffffff;
				}
			}

			tris[dst++] = indices[0] & 0x0fffffff;
			tris[dst++] = indices[1] & 0x0fffffff;
			tris[dst++] = indices[2] & 0x0fffffff;
			ntris++;
			return ntris;
		}

		private static int CountPolyVerts(int[] polys, int offset, int nvp)
		{
			for (int i = 0; i < nvp; i++)
			{
				if (polys[offset + i] == PolyMesh.NullIndex)
				{
					return i;
				}
			}

			return nvp;
		}

		private static bool ULeft(int[] verts, int a, int b, int c)
		{
			return (verts[b] - verts[a]) * (verts[c + 2] - verts[a + 2]) - (verts[c] - verts[a]) * (verts[b + 2] - verts[a + 2]) < 0;
		}

		private static int GetPolyMergeValue(int[] polys, int pa, int pb, int[] verts, out int ea, out int eb, int nvp)
		{
			ea = -1;
			eb = -1;
			int na = CountPolyVerts(polys, pa, nvp);
			int nb = CountPolyVerts(polys, pb, nvp);
			if (na + nb - 2 > nvp)
			{
				return -1;
			}

			for (int i = 0; i < na; i++)
			{
				int va0 = polys[pa + i];
				int va1 = polys[pa + (i + 1) % na];
				if (va0 > va1) (va0, va1) = (va1, va0);
				for (int j = 0; j < nb; j++)
				{
					int vb0 = polys[pb + j];
					int vb1 = polys[pb + (j + 1) % nb];
					if (vb0 > vb1) (vb0, vb1) = (vb1, vb0);
					if (va0 == vb0 && va1 == vb1)
					{
						ea = i;
						eb = j;
						break;
					}
				}

				if (ea != -1)
				{
					break;
				}
			}

			if (ea == -1 || eb == -1)
			{
				return -1;
			}

			int va = polys[pa + (ea + na - 1) % na] * 3;
			int vb = polys[pa + ea] * 3;
			int vc = polys[pb + (eb + 2) % nb] * 3;
			if (!ULeft(verts, va, vb, vc))
			{
				return -1;
			}

			va = polys[pb + (eb + nb - 1) % nb] * 3;
			vb = polys[pb + eb] * 3;
			vc = polys[pa + (ea + 2) % na] * 3;
			if (!ULeft(verts, va, vb, vc))
			{
				return -1;
			}

			va = polys[pa + ea] * 3;
			vb = polys[pa + (ea + 1) % na] * 3;
			int dx = verts[va] - verts[vb];
			int dz = verts[va + 2] - verts[vb + 2];
			return dx * dx + dz * dz;
		}

		private static void MergePolys(int[] polys, int pa, int pb, int ea, int eb, int[] tmp, int nvp)
		{
			int na = CountPolyVerts(polys, pa, nvp);
			int nb = CountPolyVerts(polys, pb, nvp);
			Array.Fill(tmp, PolyMesh.NullIndex);
			int n = 0;
			for (int i = 0; i < na - 1; i++)
			{
				tmp[n++] = polys[pa + (ea + 1 + i) % na];
			}

			for (int i = 0; i < nb - 1; i++)
			{
				tmp[n++] = polys[pb + (eb + 1 + i) % nb];
			}

			Array.Copy(tmp, 0, polys, pa, nvp);
		}

		private static PolyMesh BuildPolyMesh(List<Contour> contours, int nvp)
		{
			int maxVertices = 0;
			int maxTris = 0;
			int maxVertsPerCont = 0;
			foreach (Contour contour in contours)
			{
				if (contour.Count < 3)
				{
					continue;
				}

				maxVertices += contour.Count;
				maxTris += contour.Count - 2;
				maxVertsPerCont = Math.Max(maxVertsPerCont, contour.Count);
			}

			var mesh = new PolyMesh
			{
				Verts = new int[Math.Max(1, maxVertices) * 3],
				Polys = new int[Math.Max(1, maxTris) * nvp * 2],
				Areas = new byte[Math.Max(1, maxTris)]
			};
			Array.Fill(mesh.Polys, PolyMesh.NullIndex);

			var firstVert = new int[VertexBucketCount];
			Array.Fill(firstVert, PolyMesh.NullIndex);
			var nextVert = new int[Math.Max(1, maxVertices)];
			var indices = new int[Math.Max(1, maxVertsPerCont)];
			var tris = new int[Math.Max(1, maxVertsPerCont) * 3];
			var polys = new int[(Math.Max(1, maxVertsPerCont) + 1) * nvp];
			var tmpPoly = new int[nvp];

			foreach (Contour contour in contours)
			{
				if (contour.Count < 3)
				{
					continue;
				}

				for (int j = 0; j < contour.Count; j++)
				{
					indices[j] = j;
				}

				int ntris = Triangulate(contour.Count, contour.Verts, indices, tris);
				if (ntris <= 0)
				{
					ntris = -ntris;
				}

				for (int j = 0; j < contour.Count; j++)
				{
					int v = j * 4;
					indices[j] = AddVertex(contour.Verts[v], contour.Verts[v + 1], contour.Verts[v + 2], mesh.Verts, firstVert, nextVert, ref mesh.VertexCount);
				}

				int npolys = 0;
				Array.Fill(polys, PolyMesh.NullIndex);
				for (int j = 0; j < ntris; j++)
				{
					int t0 = tris[j * 3], t1 = tris[j * 3 + 1], t2 = tris[j * 3 + 2];
					if (t0 != t1 && t0 != t2 && t1 != t2)
					{
						polys[npolys * nvp] = indices[t0];
						polys[npolys * nvp + 1] = indices[t1];
						polys[npolys * nvp + 2] = indices[t2];
						npolys++;
					}
				}

				if (npolys == 0)
				{
					continue;
				}

				if (nvp > 3)
				{
					while (true)
					{
						int bestMergeVal = 0;
						int bestPa = 0, bestPb = 0, bestEa = 0, bestEb = 0;
						for (int j = 0; j < npolys - 1; j++)
						{
							for (int k = j + 1; k < npolys; k++)
							{
								int v = GetPolyMergeValue(polys, j * nvp, k * nvp, mesh.Verts, out int ea, out int eb, nvp);
								if (v > bestMergeVal)
								{
									bestMergeVal = v;
									bestPa = j;
									bestPb = k;
									bestEa = ea;
									bestEb = eb;
								}
							}
						}

						if (bestMergeVal <= 0)
						{
							break;
						}

						MergePolys(polys, bestPa * nvp, bestPb * nvp, bestEa, bestEb, tmpPoly, nvp);
						Array.Copy(polys, (npolys - 1) * nvp, polys, bestPb * nvp, nvp);
						npolys--;
					}
				}

				for (int j = 0; j < npolys; j++)
				{
					if (mesh.PolyCount >= maxTris)
					{
						break;
					}

					int p = mesh.PolyCount * nvp * 2;
					for (int k = 0; k < nvp; k++)
					{
						mesh.Polys[p + k] = polys[j * nvp + k];
					}

					mesh.Areas[mesh.PolyCount] = contour.Area;
					mesh.PolyCount++;
				}
			}

			BuildMeshAdjacency(mesh, nvp);
			return mesh;
		}

		private static void BuildMeshAdjacency(PolyMesh mesh, int nvp)
		{
			int npolys = mesh.PolyCount;
			int nverts = mesh.VertexCount;
			int maxEdgeCount = npolys * nvp;
			var firstEdge = new int[nverts];
			var nextEdge = new int[maxEdgeCount];
			var edgeVert0 = new int[maxEdgeCount];
			var edgeVert1 = new int[maxEdgeCount];
			var edgePoly0 = new int[maxEdgeCount];
			var edgePoly1 = new int[maxEdgeCount];
			var edgePolyEdge0 = new int[maxEdgeCount];
			var edgePolyEdge1 = new int[maxEdgeCount];
			Array.Fill(firstEdge, -1);
			int edgeCount = 0;

			for (int i = 0; i < npolys; i++)
			{
				int t = i * nvp * 2;
				for (int j = 0; j < nvp; j++)
				{
					if (mesh.Polys[t + j] == PolyMesh.NullIndex)
					{
						break;
					}

					int v0 = mesh.Polys[t + j];
					int v1 = j + 1 >= nvp || mesh.Polys[t + j + 1] == PolyMesh.NullIndex ? mesh.Polys[t] : mesh.Polys[t + j + 1];
					if (v0 < v1)
					{
						edgeVert0[edgeCount] = v0;
						edgeVert1[edgeCount] = v1;
						edgePoly0[edgeCount] = i;
						edgePolyEdge0[edgeCount] = j;
						edgePoly1[edgeCount] = i;
						edgePolyEdge1[edgeCount] = -1;
						nextEdge[edgeCount] = firstEdge[v0];
						firstEdge[v0] = edgeCount;
						edgeCount++;
					}
				}
			}

			for (int i = 0; i < npolys; i++)
			{
				int t = i * nvp * 2;
				for (int j = 0; j < nvp; j++)
				{
					if (mesh.Polys[t + j] == PolyMesh.NullIndex)
					{
						break;
					}

					int v0 = mesh.Polys[t + j];
					int v1 = j + 1 >= nvp || mesh.Polys[t + j + 1] == PolyMesh.NullIndex ? mesh.Polys[t] : mesh.Polys[t + j + 1];
					if (v0 > v1)
					{
						for (int e = firstEdge[v1]; e != -1; e = nextEdge[e])
						{
							if (edgeVert1[e] == v0 && edgePoly0[e] == edgePoly1[e])
							{
								edgePoly1[e] = i;
								edgePolyEdge1[e] = j;
								break;
							}
						}
					}
				}
			}

			for (int e = 0; e < edgeCount; e++)
			{
				if (edgePoly0[e] != edgePoly1[e])
				{
					mesh.Polys[edgePoly0[e] * nvp * 2 + nvp + edgePolyEdge0[e]] = edgePoly1[e];
					mesh.Polys[edgePoly1[e] * nvp * 2 + nvp + edgePolyEdge1[e]] = edgePoly0[e];
				}
			}
		}

		#endregion
	}
}
