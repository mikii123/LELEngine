using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>Outcome of a navigation query (flags).</summary>
	[Flags]
	public enum NavStatus
	{
		Failure = 1 << 31,
		Success = 1 << 30,
		InProgress = 1 << 29,

		/// <summary>The result reaches only part of the way (the target is unreachable or the node budget ran out).</summary>
		PartialResult = 1 << 6,

		BufferTooSmall = 1 << 5,
		OutOfNodes = 1 << 4,
		InvalidParam = 1 << 3
	}

	public static class NavStatusExtensions
	{
		public static bool Succeeded(this NavStatus status) => (status & NavStatus.Success) != 0;
		public static bool Failed(this NavStatus status) => (status & NavStatus.Failure) != 0;
		public static bool IsInProgress(this NavStatus status) => (status & NavStatus.InProgress) != 0;
		public static bool IsPartial(this NavStatus status) => (status & NavStatus.PartialResult) != 0;
	}

	/// <summary>Which polygons a query may use (area mask, agent type) and how much each area costs.</summary>
	public sealed class NavMeshQueryFilter
	{
		#region PublicFields

		/// <summary>Bit i set: area i is walkable for the query (<see cref="NavMeshArea.AllAreas" /> for all).</summary>
		public int AreaMask = NavMeshArea.AllAreas;

		/// <summary>Surfaces baked for this agent type only; -1 for every surface.</summary>
		public int AgentTypeId = 0;

		/// <summary>Per-area cost multipliers; null uses the global <see cref="NavMesh.GetAreaCost" /> table.</summary>
		public float[] AreaCosts;

		#endregion

		#region PublicMethods

		public bool PassFilter(int area)
		{
			return (AreaMask & (1 << area)) != 0;
		}

		public float AreaCost(int area)
		{
			float[] costs = AreaCosts ?? NavMesh.AreaCosts;
			return area >= 0 && area < costs.Length ? costs[area] : 1f;
		}

		public float Cost(in Vector3 a, in Vector3 b, int area)
		{
			return (b - a).Length * AreaCost(area);
		}

		#endregion
	}

	/// <summary>A point on the navmesh found by a query, with the polygon it lies on.</summary>
	public struct NavMeshHit
	{
		public Vector3 Position;
		public Vector3 Normal;
		public float Distance;

		/// <summary>Area mask of the polygon hit (bit of its area).</summary>
		public int Mask;

		/// <summary>True when the query hit something (a wall for raycasts, a position for samples).</summary>
		public bool Hit;

		internal PolyRef Ref;
	}

	/// <summary>
	///     Pathfinding and lookups on the navmesh world (Detour's dtNavMeshQuery). Not thread safe: each thread
	///     needs its own query; the node pools are reused across calls (no allocations after warm-up).
	/// </summary>
	public sealed class NavMeshQuery
	{
		#region PublicFields

		public const int MaxPolyVerts = 8;

		#endregion

		#region PrivateFields

		private const float HeuristicScale = 0.999f;
		private const byte NodeOpen = 1;
		private const byte NodeClosed = 2;

		private readonly NavMeshWorld world;
		private readonly NodePool nodePool;
		private readonly NodeQueue openList;
		private readonly NodePool tinyPool = new NodePool(64, 32);
		private readonly List<NavTile> tiles = new List<NavTile>();
		private readonly int[] stack = new int[64];

		// Sliced query state.
		private PolyRef slicedStart;
		private PolyRef slicedEnd;
		private Vector3 slicedStartPos;
		private Vector3 slicedEndPos;
		private NavMeshQueryFilter slicedFilter;
		private int slicedLastBest = -1;
		private float slicedLastBestCost;
		private NavStatus slicedStatus;

		#endregion

		#region Constructors

		public NavMeshQuery(int maxNodes = 4096)
			: this(NavMesh.World, maxNodes)
		{ }

		internal NavMeshQuery(NavMeshWorld world, int maxNodes)
		{
			this.world = world;
			nodePool = new NodePool(maxNodes, Math.Max(256, maxNodes / 4));
			openList = new NodeQueue(nodePool, maxNodes);
		}

		#endregion

		#region Lookups

		/// <summary>True when the handle refers to an existing polygon the filter accepts.</summary>
		public bool IsValidPolyRef(PolyRef reference, NavMeshQueryFilter filter)
		{
			NavTile tile = world.GetTile(reference, out int poly);
			return tile != null && (filter == null || filter.PassFilter(tile.Area(poly)));
		}

		public int GetPolyArea(PolyRef reference)
		{
			NavTile tile = world.GetTile(reference, out int poly);
			return tile != null ? tile.Area(poly) : NavMeshArea.NotWalkable;
		}

		/// <summary>The polygon nearest to <paramref name="center" /> within the box of half extents, and the closest point on it.</summary>
		public bool FindNearestPoly(in Vector3 center, in Vector3 halfExtents, NavMeshQueryFilter filter, out PolyRef nearest, out Vector3 nearestPoint)
		{
			nearest = PolyRef.Null;
			nearestPoint = center;
			Vector3 min = center - halfExtents;
			Vector3 max = center + halfExtents;
			tiles.Clear();
			world.QueryTiles(min, max, filter.AgentTypeId, tiles);

			float nearestDistanceSqr = float.MaxValue;
			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			foreach (NavTile tile in tiles)
			{
				for (int p = 0; p < tile.PolyCount; p++)
				{
					if (!NavMath.OverlapBounds(min, max, tile.PolyMin[p], tile.PolyMax[p]) || !filter.PassFilter(tile.Area(p)))
					{
						continue;
					}

					int n = tile.GetPolyVerts(p, verts);
					ClosestPointOnPoly(verts.Slice(0, n), center, out Vector3 closest, out bool overPoly);
					// Prefer the polygon under the point: measure the vertical gap only when the point is above it.
					float d;
					Vector3 diff = center - closest;
					if (overPoly)
					{
						d = Math.Max(0f, Math.Abs(diff.Y) - halfExtents.Y) ;
						d = d * d + diff.X * diff.X + diff.Z * diff.Z;
						d *= 0.5f;
					}
					else
					{
						d = diff.LengthSquared;
					}

					if (d < nearestDistanceSqr)
					{
						nearestDistanceSqr = d;
						nearestPoint = closest;
						nearest = tile.Ref(p);
					}
				}
			}

			return !nearest.IsNull;
		}

		/// <summary>Polygons whose bounds overlap the box.</summary>
		public int QueryPolygons(in Vector3 center, in Vector3 halfExtents, NavMeshQueryFilter filter, List<PolyRef> result, int maxResult)
		{
			Vector3 min = center - halfExtents;
			Vector3 max = center + halfExtents;
			tiles.Clear();
			world.QueryTiles(min, max, filter.AgentTypeId, tiles);
			int count = 0;
			foreach (NavTile tile in tiles)
			{
				for (int p = 0; p < tile.PolyCount && count < maxResult; p++)
				{
					if (NavMath.OverlapBounds(min, max, tile.PolyMin[p], tile.PolyMax[p]) && filter.PassFilter(tile.Area(p)))
					{
						result.Add(tile.Ref(p));
						count++;
					}
				}
			}

			return count;
		}

		/// <summary>Closest point on the polygon to <paramref name="position" />; <paramref name="overPoly" /> when the position projects inside it.</summary>
		public bool ClosestPointOnPoly(PolyRef reference, in Vector3 position, out Vector3 closest, out bool overPoly)
		{
			NavTile tile = world.GetTile(reference, out int poly);
			if (tile == null)
			{
				closest = position;
				overPoly = false;
				return false;
			}

			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			int n = tile.GetPolyVerts(poly, verts);
			ClosestPointOnPoly(verts.Slice(0, n), position, out closest, out overPoly);
			return true;
		}

		/// <summary>Closest point on the polygon's boundary (or inside, keeping the height of the surface).</summary>
		public bool ClosestPointOnPolyBoundary(PolyRef reference, in Vector3 position, out Vector3 closest)
		{
			NavTile tile = world.GetTile(reference, out int poly);
			if (tile == null)
			{
				closest = position;
				return false;
			}

			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			int n = tile.GetPolyVerts(poly, verts);
			ReadOnlySpan<Vector3> polyVerts = verts.Slice(0, n);
			if (NavMath.PointInPolygon(position, polyVerts))
			{
				closest = position;
				return true;
			}

			float best = float.MaxValue;
			closest = polyVerts[0];
			for (int i = 0, j = n - 1; i < n; j = i++)
			{
				float d = NavMath.DistancePointSegmentSqr2D(position, polyVerts[j], polyVerts[i], out float t);
				if (d < best)
				{
					best = d;
					closest = Vector3.Lerp(polyVerts[j], polyVerts[i], t);
				}
			}

			return true;
		}

		/// <summary>Height of the polygon surface under the position (false when the position is outside the polygon).</summary>
		public bool GetPolyHeight(PolyRef reference, in Vector3 position, out float height)
		{
			height = position.Y;
			NavTile tile = world.GetTile(reference, out int poly);
			if (tile == null)
			{
				return false;
			}

			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			int n = tile.GetPolyVerts(poly, verts);
			return NavMath.PolyHeight(position, verts.Slice(0, n), out height);
		}

		#endregion

		#region Pathfinding

		/// <summary>Polygon path from start to end (A*). Partial when the end is unreachable.</summary>
		public NavStatus FindPath(PolyRef startRef, PolyRef endRef, in Vector3 startPos, in Vector3 endPos, NavMeshQueryFilter filter, List<PolyRef> path, int maxPath = 256)
		{
			NavStatus status = InitSlicedFindPath(startRef, endRef, startPos, endPos, filter);
			if (status.Failed())
			{
				return status;
			}

			UpdateSlicedFindPath(int.MaxValue, out _);
			return FinalizeSlicedFindPath(path, maxPath);
		}

		public NavStatus InitSlicedFindPath(PolyRef startRef, PolyRef endRef, in Vector3 startPos, in Vector3 endPos, NavMeshQueryFilter filter)
		{
			slicedStart = startRef;
			slicedEnd = endRef;
			slicedStartPos = startPos;
			slicedEndPos = endPos;
			slicedFilter = filter;
			slicedLastBest = -1;
			slicedStatus = NavStatus.Failure;
			if (!world.IsValid(startRef) || !world.IsValid(endRef))
			{
				return slicedStatus = NavStatus.Failure | NavStatus.InvalidParam;
			}

			if (startRef == endRef)
			{
				return slicedStatus = NavStatus.Success;
			}

			nodePool.Clear();
			openList.Clear();
			int start = nodePool.GetNode(startRef);
			nodePool.Pos[start] = startPos;
			nodePool.Parent[start] = -1;
			nodePool.Cost[start] = 0f;
			nodePool.Total[start] = (startPos - endPos).Length * HeuristicScale;
			nodePool.Flags[start] = NodeOpen;
			openList.Push(start);
			slicedLastBest = start;
			slicedLastBestCost = nodePool.Total[start];
			return slicedStatus = NavStatus.InProgress;
		}

		/// <summary>Runs up to <paramref name="maxIter" /> node expansions of the sliced search.</summary>
		public NavStatus UpdateSlicedFindPath(int maxIter, out int doneIters)
		{
			doneIters = 0;
			if (!slicedStatus.IsInProgress())
			{
				return slicedStatus;
			}

			if (!world.IsValid(slicedStart) || !world.IsValid(slicedEnd))
			{
				return slicedStatus = NavStatus.Failure;
			}

			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			while (doneIters < maxIter && !openList.Empty)
			{
				doneIters++;
				int best = openList.Pop();
				nodePool.Flags[best] = (byte)((nodePool.Flags[best] & ~NodeOpen) | NodeClosed);
				PolyRef bestRef = nodePool.Ref[best];
				if (bestRef == slicedEnd)
				{
					slicedLastBest = best;
					slicedStatus = NavStatus.Success;
					return slicedStatus;
				}

				NavTile bestTile = world.GetTile(bestRef, out int bestPoly);
				if (bestTile == null)
				{
					slicedStatus = NavStatus.Failure;
					return slicedStatus;
				}

				int parent = nodePool.Parent[best];
				PolyRef parentRef = parent >= 0 ? nodePool.Ref[parent] : PolyRef.Null;
				int nv = bestTile.GetPolyVerts(bestPoly, verts);

				// Internal neighbors, then links to other tiles.
				for (int edge = 0; edge < nv; edge++)
				{
					int neighbor = bestTile.Neighbor(bestPoly, edge);
					if (neighbor >= 0)
					{
						Vector3 mid = (verts[edge] + verts[(edge + 1) % nv]) * 0.5f;
						Expand(best, bestRef, parentRef, bestTile.Ref(neighbor), bestTile, neighbor, mid);
					}
				}

				for (int link = bestTile.FirstLink[bestPoly]; link >= 0; link = bestTile.Links[link].Next)
				{
					NavLink navLink = bestTile.Links[link];
					NavTile neighborTile = world.GetTile(navLink.Ref, out int neighborPoly);
					if (neighborTile == null)
					{
						continue;
					}

					int e = navLink.EdgeIndex;
					Vector3 left = Vector3.Lerp(verts[e], verts[(e + 1) % nv], navLink.TMin);
					Vector3 right = Vector3.Lerp(verts[e], verts[(e + 1) % nv], navLink.TMax);
					Expand(best, bestRef, parentRef, navLink.Ref, neighborTile, neighborPoly, (left + right) * 0.5f);
				}
			}

			if (openList.Empty)
			{
				slicedStatus = NavStatus.Success | NavStatus.PartialResult;
			}

			return slicedStatus;
		}

		/// <summary>The polygon path of the finished sliced search (partial: to the node closest to the end).</summary>
		public NavStatus FinalizeSlicedFindPath(List<PolyRef> path, int maxPath = 256)
		{
			path.Clear();
			if (slicedStatus.Failed())
			{
				return slicedStatus;
			}

			if (slicedStart == slicedEnd)
			{
				path.Add(slicedStart);
				return NavStatus.Success;
			}

			if (slicedLastBest < 0)
			{
				return NavStatus.Failure;
			}

			NavStatus status = slicedStatus & ~NavStatus.InProgress;
			if (nodePool.Ref[slicedLastBest] != slicedEnd)
			{
				status |= NavStatus.PartialResult;
			}

			AppendPathToNode(slicedLastBest, path, maxPath, ref status);
			return status | NavStatus.Success;
		}

		/// <summary>
		///     Finalizes a sliced search that may not have reached the end: the path goes to the furthest polygon of
		///     <paramref name="existing" /> the search visited, so an agent keeps a usable corridor while the full
		///     path is still computing.
		/// </summary>
		public NavStatus FinalizeSlicedFindPathPartial(IReadOnlyList<PolyRef> existing, int existingCount, List<PolyRef> path, int maxPath = 256)
		{
			path.Clear();
			if (existingCount == 0 || slicedStatus.Failed())
			{
				return NavStatus.Failure;
			}

			if (slicedStart == slicedEnd)
			{
				path.Add(slicedStart);
				return NavStatus.Success;
			}

			int node = -1;
			for (int i = existingCount - 1; i >= 0; i--)
			{
				node = nodePool.FindNode(existing[i]);
				if (node >= 0)
				{
					break;
				}
			}

			NavStatus status = NavStatus.Success | NavStatus.PartialResult;
			if (node < 0)
			{
				node = slicedLastBest;
			}

			if (node < 0)
			{
				return NavStatus.Failure;
			}

			AppendPathToNode(node, path, maxPath, ref status);
			return status;
		}

		private void Expand(int best, PolyRef bestRef, PolyRef parentRef, PolyRef neighborRef, NavTile neighborTile, int neighborPoly, in Vector3 edgeMid)
		{
			if (neighborRef == parentRef || neighborRef.IsNull)
			{
				return;
			}

			int area = neighborTile.Area(neighborPoly);
			if (!slicedFilter.PassFilter(area))
			{
				return;
			}

			int node = nodePool.GetNode(neighborRef);
			if (node < 0)
			{
				slicedStatus |= NavStatus.OutOfNodes;
				return;
			}

			if (nodePool.Flags[node] == 0)
			{
				nodePool.Pos[node] = edgeMid;
			}

			float cost;
			float heuristic;
			int bestArea = GetPolyArea(bestRef);
			if (neighborRef == slicedEnd)
			{
				float curCost = slicedFilter.Cost(nodePool.Pos[best], nodePool.Pos[node], bestArea);
				float endCost = slicedFilter.Cost(nodePool.Pos[node], slicedEndPos, area);
				cost = nodePool.Cost[best] + curCost + endCost;
				heuristic = 0f;
			}
			else
			{
				float curCost = slicedFilter.Cost(nodePool.Pos[best], nodePool.Pos[node], bestArea);
				cost = nodePool.Cost[best] + curCost;
				heuristic = (nodePool.Pos[node] - slicedEndPos).Length * HeuristicScale;
			}

			float total = cost + heuristic;
			byte flags = nodePool.Flags[node];
			if ((flags & NodeOpen) != 0 && total >= nodePool.Total[node])
			{
				return;
			}

			if ((flags & NodeClosed) != 0 && total >= nodePool.Total[node])
			{
				return;
			}

			nodePool.Parent[node] = best;
			nodePool.Cost[node] = cost;
			nodePool.Total[node] = total;
			nodePool.Flags[node] = (byte)(flags & ~NodeClosed);
			if ((flags & NodeOpen) != 0)
			{
				openList.Modify(node);
			}
			else
			{
				nodePool.Flags[node] |= NodeOpen;
				openList.Push(node);
			}

			if (heuristic < slicedLastBestCost)
			{
				slicedLastBestCost = heuristic;
				slicedLastBest = node;
			}
		}

		private void AppendPathToNode(int node, List<PolyRef> path, int maxPath, ref NavStatus status)
		{
			int length = 0;
			for (int n = node; n >= 0; n = nodePool.Parent[n])
			{
				length++;
			}

			int write = Math.Min(length, maxPath);
			if (length > maxPath)
			{
				status |= NavStatus.BufferTooSmall;
			}

			// Walk from the end, skipping the nodes that do not fit (the start of the path is kept).
			int skip = length - write;
			for (int i = 0; i < write; i++)
			{
				path.Add(PolyRef.Null);
			}

			int index = write - 1;
			for (int n = node; n >= 0; n = nodePool.Parent[n])
			{
				if (skip > 0)
				{
					skip--;
					continue;
				}

				path[index--] = nodePool.Ref[n];
			}
		}

		#endregion

		#region StraightPath

		public const byte StraightPathStart = 1;
		public const byte StraightPathEnd = 2;

		/// <summary>
		///     Straightens a polygon path with the funnel algorithm into corner points. Returns the point count;
		///     flags mark start / end, refs the polygon entered at each corner.
		/// </summary>
		public NavStatus FindStraightPath(in Vector3 startPos, in Vector3 endPos, IReadOnlyList<PolyRef> path, int pathCount, Vector3[] straightPath, byte[] straightFlags, PolyRef[] straightRefs, out int straightCount, int maxStraight)
		{
			straightCount = 0;
			if (pathCount == 0 || maxStraight == 0)
			{
				return NavStatus.Failure | NavStatus.InvalidParam;
			}

			NavStatus status = NavStatus.Success;
			if (!ClosestPointOnPolyBoundary(path[0], startPos, out Vector3 closestStart))
			{
				return NavStatus.Failure | NavStatus.InvalidParam;
			}

			ClosestPointOnPolyBoundary(path[pathCount - 1], endPos, out Vector3 closestEnd);
			if (!AppendVertex(closestStart, StraightPathStart, path[0], straightPath, straightFlags, straightRefs, ref straightCount, maxStraight))
			{
				return NavStatus.Success | NavStatus.BufferTooSmall;
			}

			if (pathCount > 1)
			{
				Vector3 portalApex = closestStart;
				Vector3 portalLeft = portalApex;
				Vector3 portalRight = portalApex;
				int apexIndex = 0;
				int leftIndex = 0;
				int rightIndex = 0;
				PolyRef leftPolyRef = path[0];
				PolyRef rightPolyRef = path[0];

				for (int i = 0; i < pathCount; i++)
				{
					Vector3 left;
					Vector3 right;
					if (i + 1 < pathCount)
					{
						if (!GetPortalPoints(path[i], path[i + 1], out left, out right))
						{
							// The next polygon is invalid: end the path at the current one.
							ClosestPointOnPolyBoundary(path[i], endPos, out closestEnd);
							AppendVertex(closestEnd, StraightPathEnd, PolyRef.Null, straightPath, straightFlags, straightRefs, ref straightCount, maxStraight);
							return NavStatus.Success | NavStatus.PartialResult;
						}

						if (i == 0 && NavMath.DistancePointSegmentSqr2D(portalApex, left, right, out _) < 0.001f * 0.001f)
						{
							continue;
						}
					}
					else
					{
						left = closestEnd;
						right = closestEnd;
					}

					// Right vertex.
					if (NavMath.TriArea2D(portalApex, portalRight, right) <= 0f)
					{
						if (NavMath.Equal(portalApex, portalRight) || NavMath.TriArea2D(portalApex, portalLeft, right) > 0f)
						{
							portalRight = right;
							rightPolyRef = i + 1 < pathCount ? path[i + 1] : PolyRef.Null;
							rightIndex = i;
						}
						else
						{
							portalApex = portalLeft;
							apexIndex = leftIndex;
							byte flags = leftPolyRef.IsNull ? StraightPathEnd : (byte)0;
							if (!AppendVertex(portalApex, flags, leftPolyRef, straightPath, straightFlags, straightRefs, ref straightCount, maxStraight))
							{
								return NavStatus.Success | NavStatus.BufferTooSmall;
							}

							portalLeft = portalApex;
							portalRight = portalApex;
							leftIndex = apexIndex;
							rightIndex = apexIndex;
							i = apexIndex;
							continue;
						}
					}

					// Left vertex.
					if (NavMath.TriArea2D(portalApex, portalLeft, left) >= 0f)
					{
						if (NavMath.Equal(portalApex, portalLeft) || NavMath.TriArea2D(portalApex, portalRight, left) < 0f)
						{
							portalLeft = left;
							leftPolyRef = i + 1 < pathCount ? path[i + 1] : PolyRef.Null;
							leftIndex = i;
						}
						else
						{
							portalApex = portalRight;
							apexIndex = rightIndex;
							byte flags = rightPolyRef.IsNull ? StraightPathEnd : (byte)0;
							if (!AppendVertex(portalApex, flags, rightPolyRef, straightPath, straightFlags, straightRefs, ref straightCount, maxStraight))
							{
								return NavStatus.Success | NavStatus.BufferTooSmall;
							}

							portalLeft = portalApex;
							portalRight = portalApex;
							leftIndex = apexIndex;
							rightIndex = apexIndex;
							i = apexIndex;
						}
					}
				}
			}

			if (!AppendVertex(closestEnd, StraightPathEnd, PolyRef.Null, straightPath, straightFlags, straightRefs, ref straightCount, maxStraight))
			{
				return NavStatus.Success | NavStatus.BufferTooSmall;
			}

			return status;
		}

		private static bool AppendVertex(in Vector3 pos, byte flags, PolyRef reference, Vector3[] straightPath, byte[] straightFlags, PolyRef[] straightRefs, ref int count, int max)
		{
			if (count > 0 && NavMath.Equal(straightPath[count - 1], pos))
			{
				// Same point: update the flags and reference.
				if (straightFlags != null) straightFlags[count - 1] = flags;
				if (straightRefs != null) straightRefs[count - 1] = reference;
				return true;
			}

			if (count >= max)
			{
				return false;
			}

			straightPath[count] = pos;
			if (straightFlags != null) straightFlags[count] = flags;
			if (straightRefs != null) straightRefs[count] = reference;
			count++;
			return true;
		}

		/// <summary>The shared edge (portal) between two connected polygons, left and right seen from the first.</summary>
		public bool GetPortalPoints(PolyRef from, PolyRef to, out Vector3 left, out Vector3 right)
		{
			left = right = Vector3.Zero;
			NavTile tile = world.GetTile(from, out int poly);
			if (tile == null)
			{
				return false;
			}

			int nv = tile.PolyVertCount(poly);
			NavTile toTile = world.GetTile(to, out int toPoly);
			if (toTile == tile)
			{
				for (int e = 0; e < nv; e++)
				{
					if (tile.Neighbor(poly, e) == toPoly)
					{
						left = tile.Vertex(poly, e);
						right = tile.Vertex(poly, (e + 1) % nv);
						return true;
					}
				}
			}

			for (int link = tile.FirstLink[poly]; link >= 0; link = tile.Links[link].Next)
			{
				NavLink navLink = tile.Links[link];
				if (navLink.Ref == to)
				{
					int e = navLink.EdgeIndex;
					Vector3 a = tile.Vertex(poly, e);
					Vector3 b = tile.Vertex(poly, (e + 1) % nv);
					left = Vector3.Lerp(a, b, navLink.TMin);
					right = Vector3.Lerp(a, b, navLink.TMax);
					return true;
				}
			}

			return false;
		}

		#endregion

		#region Raycast

		/// <summary>
		///     Walks the navmesh from the start polygon along the segment. <paramref name="t" /> is the fraction of the
		///     segment reached before a wall (float.MaxValue when the end is reached); the visited polygons go to
		///     <paramref name="path" /> when given.
		/// </summary>
		public NavStatus Raycast(PolyRef startRef, in Vector3 startPos, in Vector3 endPos, NavMeshQueryFilter filter, out float t, out Vector3 hitNormal, List<PolyRef> path, int maxPath = 64)
		{
			t = 0f;
			hitNormal = Vector3.Zero;
			path?.Clear();
			if (!world.IsValid(startRef))
			{
				return NavStatus.Failure | NavStatus.InvalidParam;
			}

			NavStatus status = NavStatus.Success;
			PolyRef curRef = startRef;
			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			int n = 0;
			int guard = 0;
			while (!curRef.IsNull && guard++ < 4096)
			{
				NavTile tile = world.GetTile(curRef, out int poly);
				if (tile == null)
				{
					break;
				}

				int nv = tile.GetPolyVerts(poly, verts);
				if (!NavMath.IntersectSegmentPoly2D(startPos, endPos, verts.Slice(0, nv), out float tmin, out float tmax, out int segMin, out int segMax))
				{
					// Missed the polygon: keep the t so far and report a hit.
					return status;
				}

				if (tmax > t)
				{
					t = tmax;
				}

				if (path != null)
				{
					if (n < maxPath)
					{
						path.Add(curRef);
						n++;
					}
					else
					{
						status |= NavStatus.BufferTooSmall;
					}
				}

				if (segMax == -1)
				{
					// The end is inside this polygon.
					t = float.MaxValue;
					return status;
				}

				PolyRef nextRef = PolyRef.Null;
				int neighbor = tile.Neighbor(poly, segMax);
				if (neighbor >= 0)
				{
					if (filter.PassFilter(tile.Area(neighbor)))
					{
						nextRef = tile.Ref(neighbor);
					}
				}
				else
				{
					// Which link's overlap contains the crossing point.
					Vector3 a = verts[segMax];
					Vector3 b = verts[(segMax + 1) % nv];
					Vector3 crossing = Vector3.Lerp(startPos, endPos, tmax);
					NavMath.DistancePointSegmentSqr2D(crossing, a, b, out float edgeT);
					for (int link = tile.FirstLink[poly]; link >= 0; link = tile.Links[link].Next)
					{
						NavLink navLink = tile.Links[link];
						if (navLink.EdgeIndex != segMax)
						{
							continue;
						}

						NavTile nextTile = world.GetTile(navLink.Ref, out int nextPoly);
						if (nextTile == null || !filter.PassFilter(nextTile.Area(nextPoly)))
						{
							continue;
						}

						if (edgeT >= navLink.TMin - 0.01f && edgeT <= navLink.TMax + 0.01f)
						{
							nextRef = navLink.Ref;
							break;
						}
					}
				}

				if (nextRef.IsNull)
				{
					// Wall: its normal (pointing back into the polygon).
					Vector3 va = verts[segMax];
					Vector3 vb = verts[(segMax + 1) % nv];
					float dx = vb.X - va.X;
					float dz = vb.Z - va.Z;
					hitNormal = new Vector3(dz, 0f, -dx);
					if (hitNormal.LengthSquared > 1e-12f)
					{
						hitNormal.Normalize();
					}

					return status;
				}

				curRef = nextRef;
			}

			return status;
		}

		#endregion

		#region Movement

		/// <summary>
		///     Moves from a position on the start polygon toward the end position, sliding along walls; the result is
		///     the position reached and the polygons visited (first is the start polygon).
		/// </summary>
		public NavStatus MoveAlongSurface(PolyRef startRef, in Vector3 startPos, in Vector3 endPos, NavMeshQueryFilter filter, out Vector3 resultPos, List<PolyRef> visited, int maxVisited = 16)
		{
			resultPos = startPos;
			visited.Clear();
			if (!world.IsValid(startRef))
			{
				return NavStatus.Failure | NavStatus.InvalidParam;
			}

			NavStatus status = NavStatus.Success;
			tinyPool.Clear();
			int startNode = tinyPool.GetNode(startRef);
			tinyPool.Parent[startNode] = -1;
			tinyPool.Flags[startNode] = NodeClosed;
			int stackCount = 0;
			stack[stackCount++] = startNode;

			Vector3 bestPos = startPos;
			float bestDist = float.MaxValue;
			int bestNode = -1;
			Vector3 searchPos = Vector3.Lerp(startPos, endPos, 0.5f);
			float searchRadius = (startPos - endPos).Length * 0.5f + 0.001f;
			float searchRadiusSqr = searchRadius * searchRadius;
			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			Span<PolyRef> neis = stackalloc PolyRef[8];

			int head = 0;
			while (head < stackCount)
			{
				int curNode = stack[head++];
				PolyRef curRef = tinyPool.Ref[curNode];
				NavTile tile = world.GetTile(curRef, out int poly);
				if (tile == null)
				{
					continue;
				}

				int nv = tile.GetPolyVerts(poly, verts);
				if (NavMath.PointInPolygon(endPos, verts.Slice(0, nv)))
				{
					bestNode = curNode;
					bestPos = endPos;
					break;
				}

				for (int i = 0, j = nv - 1; i < nv; j = i++)
				{
					// Neighbors across edge j.
					int nneis = 0;
					int neighbor = tile.Neighbor(poly, j);
					if (neighbor >= 0)
					{
						if (filter.PassFilter(tile.Area(neighbor)))
						{
							neis[nneis++] = tile.Ref(neighbor);
						}
					}
					else
					{
						for (int link = tile.FirstLink[poly]; link >= 0 && nneis < neis.Length; link = tile.Links[link].Next)
						{
							NavLink navLink = tile.Links[link];
							if (navLink.EdgeIndex != j)
							{
								continue;
							}

							NavTile nextTile = world.GetTile(navLink.Ref, out int nextPoly);
							if (nextTile != null && filter.PassFilter(nextTile.Area(nextPoly)))
							{
								neis[nneis++] = navLink.Ref;
							}
						}
					}

					if (nneis == 0)
					{
						// Wall edge: nearest point on it.
						float distSqr = NavMath.DistancePointSegmentSqr2D(endPos, verts[j], verts[i], out float tseg);
						if (distSqr < bestDist)
						{
							bestPos = Vector3.Lerp(verts[j], verts[i], tseg);
							bestDist = distSqr;
							bestNode = curNode;
						}

						continue;
					}

					for (int k = 0; k < nneis; k++)
					{
						int node = tinyPool.GetNode(neis[k]);
						if (node < 0 || (tinyPool.Flags[node] & NodeClosed) != 0)
						{
							continue;
						}

						float distSqr = NavMath.DistancePointSegmentSqr2D(searchPos, verts[j], verts[i], out _);
						if (distSqr > searchRadiusSqr)
						{
							continue;
						}

						if (stackCount < stack.Length)
						{
							tinyPool.Parent[node] = curNode;
							tinyPool.Flags[node] |= NodeClosed;
							stack[stackCount++] = node;
						}
					}
				}
			}

			if (bestNode >= 0)
			{
				int length = 0;
				for (int node = bestNode; node >= 0; node = tinyPool.Parent[node])
				{
					length++;
				}

				int write = Math.Min(length, maxVisited);
				if (length > maxVisited)
				{
					status |= NavStatus.BufferTooSmall;
				}

				for (int i = 0; i < write; i++)
				{
					visited.Add(PolyRef.Null);
				}

				// The last visited polygons are kept when the buffer is too small.
				int index = write - 1;
				for (int node = bestNode; node >= 0 && index >= 0; node = tinyPool.Parent[node])
				{
					visited[index--] = tinyPool.Ref[node];
				}
			}

			resultPos = bestPos;
			return status;
		}

		/// <summary>
		///     Polygons around <paramref name="centerPos" /> reachable within the radius, excluding ones that overlap
		///     already collected polygons (one layer of walkable space, for local avoidance).
		/// </summary>
		public NavStatus FindLocalNeighbourhood(PolyRef startRef, in Vector3 centerPos, float radius, NavMeshQueryFilter filter, List<PolyRef> result, List<PolyRef> parents, int maxResult)
		{
			result.Clear();
			parents?.Clear();
			if (!world.IsValid(startRef))
			{
				return NavStatus.Failure | NavStatus.InvalidParam;
			}

			tinyPool.Clear();
			int startNode = tinyPool.GetNode(startRef);
			tinyPool.Parent[startNode] = -1;
			tinyPool.Flags[startNode] = NodeClosed;
			int stackCount = 0;
			stack[stackCount++] = startNode;
			float radiusSqr = radius * radius;
			Span<Vector3> pa = stackalloc Vector3[MaxPolyVerts];
			Span<Vector3> pb = stackalloc Vector3[MaxPolyVerts];
			result.Add(startRef);
			parents?.Add(PolyRef.Null);
			NavStatus status = NavStatus.Success;

			int head = 0;
			while (head < stackCount)
			{
				int curNode = stack[head++];
				PolyRef curRef = tinyPool.Ref[curNode];
				NavTile tile = world.GetTile(curRef, out int poly);
				if (tile == null)
				{
					continue;
				}

				int nv = tile.PolyVertCount(poly);
				for (int edge = 0; edge < nv; edge++)
				{
					int neighbor = tile.Neighbor(poly, edge);
					if (neighbor >= 0)
					{
						VisitNeighbour(tile.Ref(neighbor), curNode, curRef, centerPos, radiusSqr, filter, result, parents, maxResult, ref stackCount, pa, pb, ref status);
					}
				}

				for (int link = tile.FirstLink[poly]; link >= 0; link = tile.Links[link].Next)
				{
					VisitNeighbour(tile.Links[link].Ref, curNode, curRef, centerPos, radiusSqr, filter, result, parents, maxResult, ref stackCount, pa, pb, ref status);
				}
			}

			return status;
		}

		private void VisitNeighbour(PolyRef neighbourRef, int curNode, PolyRef curRef, in Vector3 centerPos, float radiusSqr, NavMeshQueryFilter filter, List<PolyRef> result, List<PolyRef> parents, int maxResult, ref int stackCount, Span<Vector3> pa, Span<Vector3> pb, ref NavStatus status)
		{
			if (neighbourRef.IsNull)
			{
				return;
			}

			int node = tinyPool.GetNode(neighbourRef);
			if (node < 0 || (tinyPool.Flags[node] & NodeClosed) != 0)
			{
				return;
			}

			NavTile neighbourTile = world.GetTile(neighbourRef, out int neighbourPoly);
			if (neighbourTile == null || !filter.PassFilter(neighbourTile.Area(neighbourPoly)))
			{
				return;
			}

			if (!GetPortalPoints(curRef, neighbourRef, out Vector3 va, out Vector3 vb))
			{
				return;
			}

			if (NavMath.DistancePointSegmentSqr2D(centerPos, va, vb, out _) > radiusSqr)
			{
				return;
			}

			tinyPool.Flags[node] |= NodeClosed;
			tinyPool.Parent[node] = curNode;

			int npa = neighbourTile.GetPolyVerts(neighbourPoly, pa);
			bool overlap = false;
			for (int j = 0; j < result.Count; j++)
			{
				PolyRef pastRef = result[j];
				if (IsConnected(curRef, pastRef))
				{
					continue;
				}

				NavTile pastTile = world.GetTile(pastRef, out int pastPoly);
				if (pastTile == null)
				{
					continue;
				}

				int npb = pastTile.GetPolyVerts(pastPoly, pb);
				if (NavMath.OverlapPolyPoly2D(pa.Slice(0, npa), pb.Slice(0, npb)))
				{
					overlap = true;
					break;
				}
			}

			if (overlap)
			{
				return;
			}

			if (result.Count < maxResult)
			{
				result.Add(neighbourRef);
				parents?.Add(curRef);
			}
			else
			{
				status |= NavStatus.BufferTooSmall;
			}

			if (stackCount < stack.Length)
			{
				stack[stackCount++] = node;
			}
		}

		private bool IsConnected(PolyRef from, PolyRef to)
		{
			NavTile tile = world.GetTile(from, out int poly);
			if (tile == null)
			{
				return false;
			}

			NavTile toTile = world.GetTile(to, out int toPoly);
			if (toTile == tile)
			{
				int nv = tile.PolyVertCount(poly);
				for (int e = 0; e < nv; e++)
				{
					if (tile.Neighbor(poly, e) == toPoly)
					{
						return true;
					}
				}
			}

			for (int link = tile.FirstLink[poly]; link >= 0; link = tile.Links[link].Next)
			{
				if (tile.Links[link].Ref == to)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Wall segments of a polygon (edges, or parts of edges, with no walkable neighbor).</summary>
		public NavStatus GetPolyWallSegments(PolyRef reference, NavMeshQueryFilter filter, List<(Vector3 a, Vector3 b)> segments, int maxSegments)
		{
			NavTile tile = world.GetTile(reference, out int poly);
			if (tile == null)
			{
				return NavStatus.Failure | NavStatus.InvalidParam;
			}

			NavStatus status = NavStatus.Success;
			int nv = tile.PolyVertCount(poly);
			Span<float> intervalMin = stackalloc float[18];
			Span<float> intervalMax = stackalloc float[18];
			for (int i = 0, j = nv - 1; i < nv; j = i++)
			{
				Vector3 vj = tile.Vertex(poly, j);
				Vector3 vi = tile.Vertex(poly, i);
				int neighbor = tile.Neighbor(poly, j);
				if (neighbor >= 0)
				{
					if (!filter.PassFilter(tile.Area(neighbor)))
					{
						AddSegment(segments, vj, vi, maxSegments, ref status);
					}

					continue;
				}

				// Links cover parts of the edge; the rest is wall.
				int count = 0;
				intervalMin[count] = -1f;
				intervalMax[count++] = 0f;
				for (int link = tile.FirstLink[poly]; link >= 0; link = tile.Links[link].Next)
				{
					NavLink navLink = tile.Links[link];
					if (navLink.EdgeIndex != j)
					{
						continue;
					}

					NavTile nextTile = world.GetTile(navLink.Ref, out int nextPoly);
					if (nextTile == null || !filter.PassFilter(nextTile.Area(nextPoly)) || count >= 17)
					{
						continue;
					}

					// Insert sorted by TMin.
					int at = count;
					while (at > 0 && intervalMin[at - 1] > navLink.TMin)
					{
						intervalMin[at] = intervalMin[at - 1];
						intervalMax[at] = intervalMax[at - 1];
						at--;
					}

					intervalMin[at] = navLink.TMin;
					intervalMax[at] = navLink.TMax;
					count++;
				}

				intervalMin[count] = 1f;
				intervalMax[count++] = 2f;

				for (int k = 1; k < count; k++)
				{
					float imin = intervalMax[k - 1];
					float imax = intervalMin[k];
					if (imax - imin > 0.001f)
					{
						AddSegment(segments, Vector3.Lerp(vj, vi, imin), Vector3.Lerp(vj, vi, imax), maxSegments, ref status);
					}
				}
			}

			return status;
		}

		private static void AddSegment(List<(Vector3 a, Vector3 b)> segments, in Vector3 a, in Vector3 b, int max, ref NavStatus status)
		{
			if (segments.Count < max)
			{
				segments.Add((a, b));
			}
			else
			{
				status |= NavStatus.BufferTooSmall;
			}
		}

		/// <summary>Nearest wall within the radius of the center position (on the start polygon's connected area).</summary>
		public NavStatus FindDistanceToWall(PolyRef startRef, in Vector3 centerPos, float maxRadius, NavMeshQueryFilter filter, out float hitDistance, out Vector3 hitPos, out Vector3 hitNormal)
		{
			hitDistance = maxRadius;
			hitPos = centerPos;
			hitNormal = Vector3.Zero;
			if (!world.IsValid(startRef))
			{
				return NavStatus.Failure | NavStatus.InvalidParam;
			}

			nodePool.Clear();
			int startNode = nodePool.GetNode(startRef);
			nodePool.Parent[startNode] = -1;
			nodePool.Flags[startNode] = NodeClosed;
			var queue = new List<int> { startNode };
			float radiusSqr = maxRadius * maxRadius;
			int head = 0;
			while (head < queue.Count)
			{
				int curNode = queue[head++];
				PolyRef curRef = nodePool.Ref[curNode];
				NavTile tile = world.GetTile(curRef, out int poly);
				if (tile == null)
				{
					continue;
				}

				int nv = tile.PolyVertCount(poly);
				for (int i = 0, j = nv - 1; i < nv; j = i++)
				{
					Vector3 vj = tile.Vertex(poly, j);
					Vector3 vi = tile.Vertex(poly, i);
					bool solid = true;
					int neighbor = tile.Neighbor(poly, j);
					if (neighbor >= 0)
					{
						solid = !filter.PassFilter(tile.Area(neighbor));
						if (!solid)
						{
							Enqueue(tile.Ref(neighbor), curNode, centerPos, vj, vi, radiusSqr, queue);
						}
					}
					else
					{
						for (int link = tile.FirstLink[poly]; link >= 0; link = tile.Links[link].Next)
						{
							NavLink navLink = tile.Links[link];
							if (navLink.EdgeIndex != j)
							{
								continue;
							}

							NavTile nextTile = world.GetTile(navLink.Ref, out int nextPoly);
							if (nextTile != null && filter.PassFilter(nextTile.Area(nextPoly)))
							{
								solid = false;
								Enqueue(navLink.Ref, curNode, centerPos, Vector3.Lerp(vj, vi, navLink.TMin), Vector3.Lerp(vj, vi, navLink.TMax), radiusSqr, queue);
							}
						}
					}

					if (!solid)
					{
						continue;
					}

					float distSqr = NavMath.DistancePointSegmentSqr2D(centerPos, vj, vi, out float tseg);
					if (distSqr > radiusSqr)
					{
						continue;
					}

					radiusSqr = distSqr;
					hitPos = Vector3.Lerp(vj, vi, tseg);
				}
			}

			hitDistance = (float)Math.Sqrt(radiusSqr);
			hitNormal = centerPos - hitPos;
			hitNormal.Y = 0f;
			if (hitNormal.LengthSquared > 1e-12f)
			{
				hitNormal.Normalize();
			}

			return NavStatus.Success;
		}

		private void Enqueue(PolyRef reference, int parentNode, in Vector3 center, in Vector3 va, in Vector3 vb, float radiusSqr, List<int> queue)
		{
			int node = nodePool.GetNode(reference);
			if (node < 0 || (nodePool.Flags[node] & NodeClosed) != 0)
			{
				return;
			}

			if (NavMath.DistancePointSegmentSqr2D(center, va, vb, out _) > radiusSqr)
			{
				return;
			}

			nodePool.Flags[node] |= NodeClosed;
			nodePool.Parent[node] = parentNode;
			queue.Add(node);
		}

		#endregion

		#region Random

		/// <summary>A random point on any walkable polygon (polygons weighted by area).</summary>
		public bool FindRandomPoint(NavMeshQueryFilter filter, Random random, out PolyRef reference, out Vector3 point)
		{
			reference = PolyRef.Null;
			point = Vector3.Zero;
			NavTile chosenTile = null;
			int chosenPoly = -1;
			float areaSum = 0f;
			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			foreach (NavSurface surface in world.Surfaces)
			{
				if (surface == null || (filter.AgentTypeId >= 0 && surface.AgentTypeId != filter.AgentTypeId))
				{
					continue;
				}

				foreach (NavTile tile in surface.Tiles)
				{
					for (int p = 0; p < tile.PolyCount; p++)
					{
						if (!filter.PassFilter(tile.Area(p)))
						{
							continue;
						}

						int n = tile.GetPolyVerts(p, verts);
						float area = 0f;
						for (int i = 2; i < n; i++)
						{
							area += Math.Abs(NavMath.TriArea2D(verts[0], verts[i - 1], verts[i])) * 0.5f;
						}

						areaSum += area;
						if ((float)random.NextDouble() * areaSum <= area)
						{
							chosenTile = tile;
							chosenPoly = p;
						}
					}
				}
			}

			if (chosenTile == null)
			{
				return false;
			}

			int count = chosenTile.GetPolyVerts(chosenPoly, verts);
			Span<float> areas = stackalloc float[MaxPolyVerts];
			point = NavMath.RandomPointInConvexPoly(verts.Slice(0, count), areas, (float)random.NextDouble(), (float)random.NextDouble());
			reference = chosenTile.Ref(chosenPoly);
			return true;
		}

		/// <summary>A random point on the polygons reachable within the radius of the start position.</summary>
		public bool FindRandomPointAroundCircle(PolyRef startRef, in Vector3 centerPos, float maxRadius, NavMeshQueryFilter filter, Random random, out PolyRef reference, out Vector3 point)
		{
			reference = PolyRef.Null;
			point = centerPos;
			var polys = new List<PolyRef>();
			FindLocalNeighbourhood(startRef, centerPos, maxRadius, filter, polys, null, 128);
			if (polys.Count == 0)
			{
				return false;
			}

			Span<Vector3> verts = stackalloc Vector3[MaxPolyVerts];
			float areaSum = 0f;
			PolyRef chosen = PolyRef.Null;
			foreach (PolyRef candidate in polys)
			{
				NavTile tile = world.GetTile(candidate, out int poly);
				if (tile == null)
				{
					continue;
				}

				int n = tile.GetPolyVerts(poly, verts);
				float area = 0f;
				for (int i = 2; i < n; i++)
				{
					area += Math.Abs(NavMath.TriArea2D(verts[0], verts[i - 1], verts[i])) * 0.5f;
				}

				areaSum += area;
				if ((float)random.NextDouble() * areaSum <= area)
				{
					chosen = candidate;
				}
			}

			NavTile chosenTile = world.GetTile(chosen, out int chosenPoly);
			if (chosenTile == null)
			{
				return false;
			}

			int count = chosenTile.GetPolyVerts(chosenPoly, verts);
			Span<float> areas = stackalloc float[MaxPolyVerts];
			point = NavMath.RandomPointInConvexPoly(verts.Slice(0, count), areas, (float)random.NextDouble(), (float)random.NextDouble());
			reference = chosen;
			return true;
		}

		#endregion

		#region PrivateMethods

		internal static void ClosestPointOnPoly(ReadOnlySpan<Vector3> verts, in Vector3 position, out Vector3 closest, out bool overPoly)
		{
			if (NavMath.PointInPolygon(position, verts))
			{
				overPoly = true;
				closest = position;
				if (NavMath.PolyHeight(position, verts, out float h))
				{
					closest.Y = h;
				}
				else
				{
					closest.Y = verts[0].Y;
				}

				return;
			}

			overPoly = false;
			float best = float.MaxValue;
			closest = verts[0];
			int n = verts.Length;
			for (int i = 0, j = n - 1; i < n; j = i++)
			{
				float d = NavMath.DistancePointSegmentSqr2D(position, verts[j], verts[i], out float t);
				if (d < best)
				{
					best = d;
					closest = Vector3.Lerp(verts[j], verts[i], t);
				}
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>Search nodes keyed by polygon handle (open-addressed hash over fixed arrays).</summary>
		internal sealed class NodePool
		{
			public readonly PolyRef[] Ref;
			public readonly Vector3[] Pos;
			public readonly float[] Cost;
			public readonly float[] Total;
			public readonly int[] Parent;
			public readonly byte[] Flags;
			private readonly int[] first;
			private readonly int[] next;
			private readonly int hashMask;
			private int count;

			public NodePool(int maxNodes, int hashSize)
			{
				int size = 1;
				while (size < hashSize)
				{
					size <<= 1;
				}

				Ref = new PolyRef[maxNodes];
				Pos = new Vector3[maxNodes];
				Cost = new float[maxNodes];
				Total = new float[maxNodes];
				Parent = new int[maxNodes];
				Flags = new byte[maxNodes];
				first = new int[size];
				next = new int[maxNodes];
				hashMask = size - 1;
				Clear();
			}

			public void Clear()
			{
				Array.Fill(first, -1);
				count = 0;
			}

			private int Hash(PolyRef reference)
			{
				ulong a = reference.Value;
				a += ~(a << 31);
				a ^= a >> 20;
				a += a << 6;
				a ^= a >> 12;
				a += ~(a << 22);
				a ^= a >> 32;
				return (int)(a & (ulong)hashMask);
			}

			public int FindNode(PolyRef reference)
			{
				int bucket = Hash(reference);
				for (int i = first[bucket]; i >= 0; i = next[i])
				{
					if (Ref[i] == reference)
					{
						return i;
					}
				}

				return -1;
			}

			/// <summary>The node of a polygon, created when needed; -1 when the pool is full.</summary>
			public int GetNode(PolyRef reference)
			{
				int bucket = Hash(reference);
				for (int i = first[bucket]; i >= 0; i = next[i])
				{
					if (Ref[i] == reference)
					{
						return i;
					}
				}

				if (count >= Ref.Length)
				{
					return -1;
				}

				int node = count++;
				Ref[node] = reference;
				Pos[node] = Vector3.Zero;
				Cost[node] = 0f;
				Total[node] = 0f;
				Parent[node] = -1;
				Flags[node] = 0;
				next[node] = first[bucket];
				first[bucket] = node;
				return node;
			}
		}

		/// <summary>Binary heap of nodes ordered by total cost.</summary>
		internal sealed class NodeQueue
		{
			private readonly NodePool pool;
			private readonly int[] heap;
			private int size;

			public NodeQueue(NodePool pool, int capacity)
			{
				this.pool = pool;
				heap = new int[capacity + 1];
			}

			public bool Empty => size == 0;

			public void Clear()
			{
				size = 0;
			}

			public void Push(int node)
			{
				if (size >= heap.Length)
				{
					return;
				}

				size++;
				BubbleUp(size - 1, node);
			}

			public int Pop()
			{
				int result = heap[0];
				size--;
				TrickleDown(0, heap[size]);
				return result;
			}

			public void Modify(int node)
			{
				for (int i = 0; i < size; i++)
				{
					if (heap[i] == node)
					{
						BubbleUp(i, node);
						return;
					}
				}
			}

			private void BubbleUp(int i, int node)
			{
				int parent = (i - 1) / 2;
				while (i > 0 && pool.Total[heap[parent]] > pool.Total[node])
				{
					heap[i] = heap[parent];
					i = parent;
					parent = (i - 1) / 2;
				}

				heap[i] = node;
			}

			private void TrickleDown(int i, int node)
			{
				int child = i * 2 + 1;
				while (child < size)
				{
					if (child + 1 < size && pool.Total[heap[child]] > pool.Total[heap[child + 1]])
					{
						child++;
					}

					heap[i] = heap[child];
					i = child;
					child = i * 2 + 1;
				}

				BubbleUp(i, node);
			}
		}

		#endregion
	}
}
