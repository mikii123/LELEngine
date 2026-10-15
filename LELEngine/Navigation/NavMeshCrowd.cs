using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>The polygon corridor an agent moves through, kept in sync with its position and target (Detour's dtPathCorridor).</summary>
	internal sealed class PathCorridor
	{
		#region PublicFields

		public const int MaxPath = 256;
		public Vector3 Pos;
		public Vector3 Target;
		public readonly List<PolyRef> Path = new List<PolyRef>(MaxPath);
		public int PathCount => Path.Count;
		public PolyRef FirstPoly => Path.Count > 0 ? Path[0] : PolyRef.Null;
		public PolyRef LastPoly => Path.Count > 0 ? Path[Path.Count - 1] : PolyRef.Null;

		#endregion

		#region PrivateFields

		private readonly List<PolyRef> visited = new List<PolyRef>(32);
		private readonly List<PolyRef> scratch = new List<PolyRef>(MaxPath);

		#endregion

		#region PublicMethods

		public void Reset(PolyRef reference, in Vector3 pos)
		{
			Pos = pos;
			Target = pos;
			Path.Clear();
			if (!reference.IsNull)
			{
				Path.Add(reference);
			}
		}

		/// <summary>Corners of the straightened corridor, dropping ones the agent already stands on.</summary>
		public int FindCorners(Vector3[] cornerVerts, byte[] cornerFlags, PolyRef[] cornerPolys, int maxCorners, NavMeshQuery query, NavMeshQueryFilter filter)
		{
			const float minTargetDist = 0.01f;
			if (Path.Count == 0)
			{
				return 0;
			}

			query.FindStraightPath(Pos, Target, Path, Path.Count, cornerVerts, cornerFlags, cornerPolys, out int ncorners, maxCorners);
			int start = 0;
			while (start < ncorners && NavMath.DistSqr2D(cornerVerts[start], Pos) <= minTargetDist * minTargetDist)
			{
				start++;
			}

			if (start > 0)
			{
				ncorners -= start;
				Array.Copy(cornerVerts, start, cornerVerts, 0, ncorners);
				Array.Copy(cornerFlags, start, cornerFlags, 0, ncorners);
				Array.Copy(cornerPolys, start, cornerPolys, 0, ncorners);
			}

			return ncorners;
		}

		/// <summary>Shortcuts the corridor start when the next corner is directly visible.</summary>
		public void OptimizePathVisibility(in Vector3 next, float pathOptimizationRange, NavMeshQuery query, NavMeshQueryFilter filter)
		{
			if (Path.Count == 0)
			{
				return;
			}

			float dist = NavMath.Dist2D(Pos, next);
			if (dist < 0.01f)
			{
				return;
			}

			dist = Math.Min(dist + 0.01f, pathOptimizationRange);
			Vector3 goal = Pos + (next - Pos) * (pathOptimizationRange / dist);
			query.Raycast(Path[0], Pos, goal, filter, out float t, out _, visited, 32);
			if (visited.Count > 1 && t > 0.99f)
			{
				MergeCorridorStartShortcut(Path, visited);
			}
		}

		/// <summary>Replans the start of the corridor with a short search (removes detours left by local movement).</summary>
		public bool OptimizePathTopology(NavMeshQuery query, NavMeshQueryFilter filter)
		{
			if (Path.Count < 3)
			{
				return false;
			}

			query.InitSlicedFindPath(Path[0], Path[Path.Count - 1], Pos, Target, filter);
			query.UpdateSlicedFindPath(32, out _);
			NavStatus status = query.FinalizeSlicedFindPathPartial(Path, Path.Count, scratch, 32);
			if (status.Succeeded() && scratch.Count > 0)
			{
				MergeCorridorStartShortcut(Path, scratch);
				return true;
			}

			return false;
		}

		public bool MovePosition(in Vector3 npos, NavMeshQuery query, NavMeshQueryFilter filter)
		{
			if (Path.Count == 0)
			{
				return false;
			}

			NavStatus status = query.MoveAlongSurface(Path[0], Pos, npos, filter, out Vector3 result, visited, 16);
			if (!status.Succeeded())
			{
				return false;
			}

			MergeCorridorStartMoved(Path, visited);
			float h = Pos.Y;
			if (query.GetPolyHeight(Path[0], result, out float height))
			{
				h = height;
			}

			result.Y = h;
			Pos = result;
			return true;
		}

		public bool MoveTargetPosition(in Vector3 npos, NavMeshQuery query, NavMeshQueryFilter filter)
		{
			if (Path.Count == 0)
			{
				return false;
			}

			NavStatus status = query.MoveAlongSurface(Path[Path.Count - 1], Target, npos, filter, out Vector3 result, visited, 16);
			if (!status.Succeeded())
			{
				return false;
			}

			MergeCorridorEndMoved(Path, visited);
			Target = result;
			return true;
		}

		public void SetCorridor(in Vector3 target, List<PolyRef> path)
		{
			Target = target;
			Path.Clear();
			Path.AddRange(path);
		}

		public void FixPathStart(PolyRef safeRef, in Vector3 safePos)
		{
			Pos = safePos;
			if (Path.Count < 3 && Path.Count > 0)
			{
				PolyRef last = Path[Path.Count - 1];
				Path.Clear();
				Path.Add(safeRef);
				Path.Add(PolyRef.Null);
				Path.Add(last);
			}
			else if (Path.Count > 0)
			{
				Path[0] = safeRef;
				Path[1] = PolyRef.Null;
			}
			else
			{
				Path.Add(safeRef);
			}
		}

		public bool IsValid(int maxLookAhead, NavMeshQuery query, NavMeshQueryFilter filter)
		{
			int n = Math.Min(Path.Count, maxLookAhead);
			for (int i = 0; i < n; i++)
			{
				if (!query.IsValidPolyRef(Path[i], filter))
				{
					return false;
				}
			}

			return true;
		}

		#endregion

		#region PrivateMethods

		private static void MergeCorridorStartMoved(List<PolyRef> path, List<PolyRef> visited)
		{
			int furthestPath = -1;
			int furthestVisited = -1;
			for (int i = path.Count - 1; i >= 0 && furthestPath < 0; i--)
			{
				for (int j = visited.Count - 1; j >= 0; j--)
				{
					if (path[i] == visited[j])
					{
						furthestPath = i;
						furthestVisited = j;
					}
				}
			}

			if (furthestPath == -1 || furthestVisited == -1)
			{
				return;
			}

			int req = visited.Count - furthestVisited;
			int orig = Math.Min(furthestPath + 1, path.Count);
			path.RemoveRange(0, orig);
			for (int i = req - 1; i >= 0; i--)
			{
				path.Insert(0, visited[visited.Count - 1 - i]);
			}

			if (path.Count > MaxPath)
			{
				path.RemoveRange(MaxPath, path.Count - MaxPath);
			}
		}

		private static void MergeCorridorEndMoved(List<PolyRef> path, List<PolyRef> visited)
		{
			int furthestPath = -1;
			int furthestVisited = -1;
			for (int i = 0; i < path.Count && furthestPath < 0; i++)
			{
				for (int j = visited.Count - 1; j >= 0; j--)
				{
					if (path[i] == visited[j])
					{
						furthestPath = i;
						furthestVisited = j;
					}
				}
			}

			if (furthestPath == -1 || furthestVisited == -1)
			{
				return;
			}

			int ppos = furthestPath + 1;
			int vpos = furthestVisited + 1;
			path.RemoveRange(ppos, path.Count - ppos);
			for (int i = vpos; i < visited.Count && path.Count < MaxPath; i++)
			{
				path.Add(visited[i]);
			}
		}

		private static void MergeCorridorStartShortcut(List<PolyRef> path, List<PolyRef> visited)
		{
			int furthestPath = -1;
			int furthestVisited = -1;
			for (int i = path.Count - 1; i >= 0 && furthestPath < 0; i--)
			{
				for (int j = visited.Count - 1; j >= 0; j--)
				{
					if (path[i] == visited[j])
					{
						furthestPath = i;
						furthestVisited = j;
					}
				}
			}

			if (furthestPath == -1 || furthestVisited <= 0)
			{
				return;
			}

			int req = furthestVisited;
			path.RemoveRange(0, furthestPath);
			for (int i = req - 1; i >= 0; i--)
			{
				path.Insert(0, visited[i]);
			}

			if (path.Count > MaxPath)
			{
				path.RemoveRange(MaxPath, path.Count - MaxPath);
			}
		}

		#endregion
	}

	/// <summary>The walls near an agent, used as segment obstacles by local avoidance (Detour's dtLocalBoundary).</summary>
	internal sealed class LocalBoundary
	{
		#region PublicFields

		public const int MaxSegments = 8;
		public const int MaxPolys = 16;
		public Vector3 Center = new Vector3(float.MaxValue);
		public readonly List<(Vector3 a, Vector3 b)> Segments = new List<(Vector3, Vector3)>(MaxSegments);

		#endregion

		#region PrivateFields

		private readonly List<float> distances = new List<float>(MaxSegments);
		private readonly List<PolyRef> polys = new List<PolyRef>(MaxPolys);
		private readonly List<(Vector3 a, Vector3 b)> polySegments = new List<(Vector3, Vector3)>(24);

		#endregion

		#region PublicMethods

		public void Reset()
		{
			Center = new Vector3(float.MaxValue);
			Segments.Clear();
			distances.Clear();
			polys.Clear();
		}

		public void Update(PolyRef reference, in Vector3 pos, float collisionQueryRange, NavMeshQuery query, NavMeshQueryFilter filter)
		{
			if (reference.IsNull)
			{
				Reset();
				return;
			}

			Center = pos;
			query.FindLocalNeighbourhood(reference, pos, collisionQueryRange, filter, polys, null, MaxPolys);
			Segments.Clear();
			distances.Clear();
			float rangeSqr = collisionQueryRange * collisionQueryRange;
			foreach (PolyRef poly in polys)
			{
				polySegments.Clear();
				query.GetPolyWallSegments(poly, filter, polySegments, 24);
				foreach ((Vector3 a, Vector3 b) segment in polySegments)
				{
					float distSqr = NavMath.DistancePointSegmentSqr2D(pos, segment.a, segment.b, out _);
					if (distSqr > rangeSqr)
					{
						continue;
					}

					AddSegment(distSqr, segment);
				}
			}
		}

		public bool IsValid(NavMeshQuery query, NavMeshQueryFilter filter)
		{
			if (polys.Count == 0)
			{
				return false;
			}

			foreach (PolyRef poly in polys)
			{
				if (!query.IsValidPolyRef(poly, filter))
				{
					return false;
				}
			}

			return true;
		}

		#endregion

		#region PrivateMethods

		private void AddSegment(float dist, (Vector3 a, Vector3 b) segment)
		{
			int at = distances.Count;
			while (at > 0 && distances[at - 1] > dist)
			{
				at--;
			}

			if (at >= MaxSegments)
			{
				return;
			}

			distances.Insert(at, dist);
			Segments.Insert(at, segment);
			if (Segments.Count > MaxSegments)
			{
				Segments.RemoveAt(Segments.Count - 1);
				distances.RemoveAt(distances.Count - 1);
			}
		}

		#endregion
	}

	/// <summary>Hash grid of agent positions for neighbor queries (Detour's dtProximityGrid).</summary>
	internal sealed class ProximityGrid
	{
		#region PrivateFields

		private readonly float cellSize;
		private readonly float invCellSize;
		private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
		private readonly Stack<List<int>> pool = new Stack<List<int>>();

		#endregion

		#region Constructors

		public ProximityGrid(float cellSize)
		{
			this.cellSize = cellSize;
			invCellSize = 1f / cellSize;
		}

		#endregion

		#region PublicMethods

		public void Clear()
		{
			foreach (List<int> list in cells.Values)
			{
				list.Clear();
				pool.Push(list);
			}

			cells.Clear();
		}

		public void AddItem(int id, float minx, float miny, float maxx, float maxy)
		{
			int iminx = (int)Math.Floor(minx * invCellSize);
			int iminy = (int)Math.Floor(miny * invCellSize);
			int imaxx = (int)Math.Floor(maxx * invCellSize);
			int imaxy = (int)Math.Floor(maxy * invCellSize);
			for (int y = iminy; y <= imaxy; y++)
			{
				for (int x = iminx; x <= imaxx; x++)
				{
					long key = ((long)x << 32) ^ (uint)y;
					if (!cells.TryGetValue(key, out List<int> list))
					{
						list = pool.Count > 0 ? pool.Pop() : new List<int>();
						cells[key] = list;
					}

					list.Add(id);
				}
			}
		}

		public int QueryItems(float minx, float miny, float maxx, float maxy, int[] ids, int maxIds)
		{
			int iminx = (int)Math.Floor(minx * invCellSize);
			int iminy = (int)Math.Floor(miny * invCellSize);
			int imaxx = (int)Math.Floor(maxx * invCellSize);
			int imaxy = (int)Math.Floor(maxy * invCellSize);
			int n = 0;
			for (int y = iminy; y <= imaxy; y++)
			{
				for (int x = iminx; x <= imaxx; x++)
				{
					long key = ((long)x << 32) ^ (uint)y;
					if (!cells.TryGetValue(key, out List<int> list))
					{
						continue;
					}

					foreach (int id in list)
					{
						bool duplicate = false;
						for (int i = 0; i < n; i++)
						{
							if (ids[i] == id)
							{
								duplicate = true;
								break;
							}
						}

						if (!duplicate)
						{
							if (n >= maxIds)
							{
								return n;
							}

							ids[n++] = id;
						}
					}
				}
			}

			return n;
		}

		#endregion
	}

	/// <summary>Sampling parameters of the velocity obstacle search (one set per avoidance quality).</summary>
	public sealed class ObstacleAvoidanceParams
	{
		public float VelBias = 0.4f;
		public float WeightDesVel = 2f;
		public float WeightCurVel = 0.75f;
		public float WeightSide = 0.75f;
		public float WeightToi = 2.5f;
		public float HorizTime = 2.5f;
		public int AdaptiveDivs = 7;
		public int AdaptiveRings = 2;
		public int AdaptiveDepth = 5;
	}

	/// <summary>
	///     Picks a velocity close to the desired one that avoids collisions with nearby agents (moving circles) and
	///     walls (segments) within the prediction horizon (Detour's dtObstacleAvoidanceQuery, adaptive sampling).
	/// </summary>
	internal sealed class ObstacleAvoidanceQuery
	{
		#region PrivateFields

		private struct Circle
		{
			public Vector3 P, Vel, DVel, Dp, Np;
			public float Rad;
		}

		private struct Segment
		{
			public Vector3 P, Q;
			public bool Touch;
		}

		private const int MaxCircles = 16;
		private const int MaxSegments = 16;
		private readonly Circle[] circles = new Circle[MaxCircles];
		private readonly Segment[] segments = new Segment[MaxSegments];
		private int circleCount;
		private int segmentCount;
		private ObstacleAvoidanceParams parameters;
		private float invHorizTime;
		private float invVmax;
		private readonly float[] pattern = new float[(32 * 4 + 1) * 2];

		#endregion

		#region PublicMethods

		public void Reset()
		{
			circleCount = 0;
			segmentCount = 0;
		}

		public void AddCircle(in Vector3 pos, float rad, in Vector3 vel, in Vector3 dvel)
		{
			if (circleCount >= MaxCircles)
			{
				return;
			}

			circles[circleCount++] = new Circle { P = pos, Rad = rad, Vel = vel, DVel = dvel };
		}

		public void AddSegment(in Vector3 p, in Vector3 q)
		{
			if (segmentCount >= MaxSegments)
			{
				return;
			}

			segments[segmentCount++] = new Segment { P = p, Q = q };
		}

		/// <summary>Samples velocities around the desired one in shrinking rings; returns the number of samples.</summary>
		public int SampleVelocityAdaptive(in Vector3 pos, float rad, float vmax, in Vector3 vel, in Vector3 dvel, out Vector3 nvel, ObstacleAvoidanceParams p)
		{
			Prepare(pos, dvel);
			parameters = p;
			invHorizTime = 1f / p.HorizTime;
			invVmax = vmax > 0f ? 1f / vmax : float.MaxValue;
			nvel = Vector3.Zero;

			int ndivs = Math.Clamp(p.AdaptiveDivs, 1, 32);
			int nrings = Math.Clamp(p.AdaptiveRings, 1, 4);
			int depth = Math.Max(1, p.AdaptiveDepth);
			float da = 1f / ndivs * MathHelper.TwoPi;
			float ca = (float)Math.Cos(da);
			float sa = (float)Math.Sin(da);

			// Desired direction and the same rotated by half a step.
			float ddx = dvel.X, ddz = dvel.Z;
			float dlen = (float)Math.Sqrt(ddx * ddx + ddz * ddz);
			if (dlen > 1e-6f)
			{
				ddx /= dlen;
				ddz /= dlen;
			}

			float hc = (float)Math.Cos(da * 0.5f);
			float hs = (float)Math.Sin(da * 0.5f);
			float rdx = ddx * hc - ddz * hs;
			float rdz = ddx * hs + ddz * hc;

			int npat = 0;
			pattern[npat * 2] = 0f;
			pattern[npat * 2 + 1] = 0f;
			npat++;
			for (int j = 0; j < nrings; j++)
			{
				float r = (float)(nrings - j) / nrings;
				pattern[npat * 2] = (j % 2 == 0 ? ddx : rdx) * r;
				pattern[npat * 2 + 1] = (j % 2 == 0 ? ddz : rdz) * r;
				int last1 = npat * 2;
				int last2 = last1;
				npat++;
				for (int i = 1; i < ndivs - 1; i += 2)
				{
					pattern[npat * 2] = pattern[last1] * ca + pattern[last1 + 1] * sa;
					pattern[npat * 2 + 1] = -pattern[last1] * sa + pattern[last1 + 1] * ca;
					pattern[npat * 2 + 2] = pattern[last2] * ca - pattern[last2 + 1] * sa;
					pattern[npat * 2 + 3] = pattern[last2] * sa + pattern[last2 + 1] * ca;
					last1 = npat * 2;
					last2 = last1 + 2;
					npat += 2;
				}

				if ((ndivs & 1) == 0)
				{
					pattern[npat * 2 + 2] = pattern[last2] * ca - pattern[last2 + 1] * sa;
					pattern[npat * 2 + 3] = pattern[last2] * sa + pattern[last2 + 1] * ca;
					npat++;
				}
			}

			float cr = vmax * (1f - p.VelBias);
			Vector3 res = dvel * p.VelBias;
			int ns = 0;
			for (int k = 0; k < depth; k++)
			{
				float minPenalty = float.MaxValue;
				Vector3 bvel = Vector3.Zero;
				for (int i = 0; i < npat; i++)
				{
					Vector3 vcand = new Vector3(res.X + pattern[i * 2] * cr, 0f, res.Z + pattern[i * 2 + 1] * cr);
					if (vcand.X * vcand.X + vcand.Z * vcand.Z > (vmax + 0.001f) * (vmax + 0.001f))
					{
						continue;
					}

					float penalty = ProcessSample(vcand, cr / 10f, pos, rad, vel, dvel, minPenalty);
					ns++;
					if (penalty < minPenalty)
					{
						minPenalty = penalty;
						bvel = vcand;
					}
				}

				res = bvel;
				cr *= 0.5f;
			}

			nvel = res;
			return ns;
		}

		#endregion

		#region PrivateMethods

		private void Prepare(in Vector3 pos, in Vector3 dvel)
		{
			for (int i = 0; i < circleCount; i++)
			{
				ref Circle cir = ref circles[i];
				Vector3 dp = cir.P - pos;
				dp.Y = 0f;
				if (dp.LengthSquared > 1e-12f)
				{
					dp.Normalize();
				}

				cir.Dp = dp;
				Vector3 dv = cir.DVel - dvel;
				float a = NavMath.TriArea2D(Vector3.Zero, dp, dv);
				cir.Np = a < 0.01f ? new Vector3(-dp.Z, 0f, dp.X) : new Vector3(dp.Z, 0f, -dp.X);
			}

			for (int i = 0; i < segmentCount; i++)
			{
				ref Segment seg = ref segments[i];
				const float r = 0.01f;
				seg.Touch = NavMath.DistancePointSegmentSqr2D(pos, seg.P, seg.Q, out _) < r * r;
			}
		}

		private static bool SweepCircleCircle(in Vector3 c0, float r0, in Vector3 v, in Vector3 c1, float r1, out float tmin, out float tmax)
		{
			const float eps = 0.0001f;
			tmin = tmax = 0f;
			Vector3 s = c1 - c0;
			float r = r0 + r1;
			float c = NavMath.Dot2D(s, s) - r * r;
			float a = NavMath.Dot2D(v, v);
			if (a < eps)
			{
				return false;
			}

			float b = NavMath.Dot2D(v, s);
			float d = b * b - a * c;
			if (d < 0f)
			{
				return false;
			}

			a = 1f / a;
			float rd = (float)Math.Sqrt(d);
			tmin = (b - rd) * a;
			tmax = (b + rd) * a;
			return true;
		}

		private static bool IntersectRaySegment(in Vector3 ap, in Vector3 u, in Vector3 bp, in Vector3 bq, out float t)
		{
			t = 0f;
			Vector3 v = bq - bp;
			Vector3 w = ap - bp;
			float d = NavMath.Perp2D(u, v);
			if (Math.Abs(d) < 1e-6f)
			{
				return false;
			}

			d = 1f / d;
			t = NavMath.Perp2D(v, w) * d;
			if (t < 0f || t > 1f)
			{
				return false;
			}

			float s = NavMath.Perp2D(u, w) * d;
			return s >= 0f && s <= 1f;
		}

		private float ProcessSample(in Vector3 vcand, float cs, in Vector3 pos, float rad, in Vector3 vel, in Vector3 dvel, float minPenalty)
		{
			float vpen = parameters.WeightDesVel * (NavMath.Dist2D(vcand, dvel) * invVmax);
			float vcpen = parameters.WeightCurVel * (NavMath.Dist2D(vcand, vel) * invVmax);
			float minPen = minPenalty - vpen - vcpen;
			float tThreshold = (parameters.WeightToi / minPen - 0.1f) * parameters.HorizTime;
			if (tThreshold - parameters.HorizTime > -float.Epsilon)
			{
				return minPenalty;
			}

			float tmin = parameters.HorizTime;
			float side = 0f;
			int nside = 0;
			for (int i = 0; i < circleCount; i++)
			{
				ref Circle cir = ref circles[i];
				Vector3 vab = vcand * 2f - vel - cir.Vel;
				side += Math.Clamp(Math.Min(NavMath.Dot2D(cir.Dp, vab) * 0.5f + 0.5f, NavMath.Dot2D(cir.Np, vab) * 2f), 0f, 1f);
				nside++;
				if (!SweepCircleCircle(pos, rad, vab, cir.P, cir.Rad, out float htmin, out float htmax))
				{
					continue;
				}

				if (htmin < 0f && htmax > 0f)
				{
					htmin = -htmin * 0.5f;
				}

				if (htmin >= 0f && htmin < tmin)
				{
					tmin = htmin;
					if (tmin < tThreshold)
					{
						return minPenalty;
					}
				}
			}

			for (int i = 0; i < segmentCount; i++)
			{
				ref Segment seg = ref segments[i];
				float htmin;
				if (seg.Touch)
				{
					Vector3 sdir = seg.Q - seg.P;
					Vector3 snorm = new Vector3(-sdir.Z, 0f, sdir.X);
					if (NavMath.Dot2D(snorm, vcand) < 0f)
					{
						continue;
					}

					htmin = 0f;
				}
				else if (!IntersectRaySegment(pos, vcand, seg.P, seg.Q, out htmin))
				{
					continue;
				}

				htmin *= 2f;
				if (htmin < tmin)
				{
					tmin = htmin;
					if (tmin < tThreshold)
					{
						return minPenalty;
					}
				}
			}

			if (nside > 0)
			{
				side /= nside;
			}

			float spen = parameters.WeightSide * side;
			float tpen = parameters.WeightToi * (1f / (0.1f + tmin * invHorizTime));
			return vpen + vcpen + spen + tpen;
		}

		#endregion
	}

	/// <summary>Queued long path searches computed a few iterations per frame (Detour's dtPathQueue).</summary>
	internal sealed class PathQueue
	{
		#region PublicFields

		public const int Invalid = 0;

		#endregion

		#region PrivateFields

		private sealed class PathRequest
		{
			public int Id;
			public PolyRef StartRef;
			public PolyRef EndRef;
			public Vector3 StartPos;
			public Vector3 EndPos;
			public NavMeshQueryFilter Filter;
			public NavStatus Status;
			public readonly List<PolyRef> Path = new List<PolyRef>(PathCorridor.MaxPath);
			public int KeepAlive;
		}

		private const int MaxKeepAlive = 2;
		private readonly NavMeshQuery query;
		private readonly List<PathRequest> requests = new List<PathRequest>();
		private PathRequest active;
		private int nextId = 1;

		#endregion

		#region Constructors

		public PathQueue(NavMeshWorld world)
		{
			query = new NavMeshQuery(world, 4096);
		}

		#endregion

		#region PublicMethods

		public int Request(PolyRef startRef, PolyRef endRef, in Vector3 startPos, in Vector3 endPos, NavMeshQueryFilter filter)
		{
			if (requests.Count >= 64)
			{
				return Invalid;
			}

			var request = new PathRequest { Id = nextId++, StartRef = startRef, EndRef = endRef, StartPos = startPos, EndPos = endPos, Filter = filter };
			if (nextId == int.MaxValue)
			{
				nextId = 1;
			}

			requests.Add(request);
			return request.Id;
		}

		/// <summary>Spends up to <paramref name="maxIters" /> A* iterations on the queued requests, oldest first.</summary>
		public void Update(int maxIters)
		{
			// Finished requests stay a couple of updates for their result to be collected.
			for (int i = requests.Count - 1; i >= 0; i--)
			{
				PathRequest request = requests[i];
				if (request == active || request.Status == 0)
				{
					continue;
				}

				request.KeepAlive++;
				if (request.KeepAlive > MaxKeepAlive)
				{
					requests.RemoveAt(i);
				}
			}

			int iterations = maxIters;
			while (iterations > 0)
			{
				if (active == null)
				{
					foreach (PathRequest request in requests)
					{
						if (request.Status == 0)
						{
							active = request;
							break;
						}
					}

					if (active == null)
					{
						return;
					}

					active.Status = query.InitSlicedFindPath(active.StartRef, active.EndRef, active.StartPos, active.EndPos, active.Filter);
				}

				if (active.Status.IsInProgress())
				{
					active.Status = query.UpdateSlicedFindPath(iterations, out int done);
					iterations -= Math.Max(1, done);
				}

				if (!active.Status.IsInProgress())
				{
					if (active.Status.Succeeded())
					{
						active.Status = query.FinalizeSlicedFindPath(active.Path, PathCorridor.MaxPath);
					}

					active = null;
				}
			}
		}

		public NavStatus GetRequestStatus(int id)
		{
			PathRequest request = Find(id);
			return request == null ? NavStatus.Failure : request.Status == 0 ? NavStatus.InProgress : request.Status;
		}

		public NavStatus GetPathResult(int id, List<PolyRef> path)
		{
			PathRequest request = Find(id);
			if (request == null || !request.Status.Succeeded())
			{
				return NavStatus.Failure;
			}

			path.Clear();
			path.AddRange(request.Path);
			NavStatus status = request.Status;
			requests.Remove(request);
			return status;
		}

		public void Clear()
		{
			requests.Clear();
			active = null;
		}

		#endregion

		#region PrivateMethods

		private PathRequest Find(int id)
		{
			foreach (PathRequest request in requests)
			{
				if (request.Id == id)
				{
					return request;
				}
			}

			return null;
		}

		#endregion
	}

	/// <summary>
	///     Moves every <see cref="NavMeshAgent" /> once per frame: path requests, corridor following, local
	///     avoidance between agents and against walls, collision resolution and sliding along the navmesh
	///     (Detour's dtCrowd).
	/// </summary>
	internal sealed class NavMeshCrowd
	{
		#region PublicFields

		public const int MaxNeighbors = 6;
		public const int MaxCorners = 4;
		public readonly List<NavMeshAgent> Agents = new List<NavMeshAgent>();
		public readonly NavMeshQuery Query;
		public readonly ObstacleAvoidanceParams[] AvoidanceParams =
		{
			new ObstacleAvoidanceParams { VelBias = 0.5f, AdaptiveDivs = 5, AdaptiveRings = 2, AdaptiveDepth = 1 },
			new ObstacleAvoidanceParams { VelBias = 0.5f, AdaptiveDivs = 5, AdaptiveRings = 2, AdaptiveDepth = 2 },
			new ObstacleAvoidanceParams { VelBias = 0.5f, AdaptiveDivs = 7, AdaptiveRings = 2, AdaptiveDepth = 3 },
			new ObstacleAvoidanceParams { VelBias = 0.5f, AdaptiveDivs = 7, AdaptiveRings = 3, AdaptiveDepth = 3 }
		};

		#endregion

		#region PrivateFields

		private const float CollisionResolveFactor = 0.7f;
		private const int CheckLookAhead = 10;
		private const float TargetReplanDelay = 1f;
		private const float TopologyOptimizeInterval = 0.5f;
		private const int MaxTopologyOptimizations = 1;

		private readonly NavMeshWorld world;
		private readonly PathQueue pathQueue;
		private readonly ProximityGrid grid = new ProximityGrid(2f);
		private readonly ObstacleAvoidanceQuery avoidance = new ObstacleAvoidanceQuery();
		private readonly List<PolyRef> scratchPath = new List<PolyRef>(PathCorridor.MaxPath);
		private readonly List<PolyRef> resultPath = new List<PolyRef>(PathCorridor.MaxPath);
		private readonly int[] neighborIds = new int[32];
		private readonly List<NavMeshAgent> queue = new List<NavMeshAgent>();
		private readonly Vector3 extents = new Vector3(2f, 4f, 2f);
		private static readonly Comparison<NavMeshAgent> byReplanTime = (a, b) => b.TargetReplanTime.CompareTo(a.TargetReplanTime);
		private static readonly Comparison<NavMeshAgent> byTopologyTime = (a, b) => b.TopologyOptTime.CompareTo(a.TopologyOptTime);

		#endregion

		#region Constructors

		public NavMeshCrowd(NavMeshWorld world)
		{
			this.world = world;
			Query = new NavMeshQuery(world, 4096);
			pathQueue = new PathQueue(world);
		}

		#endregion

		#region PublicMethods

		public void Add(NavMeshAgent agent)
		{
			if (!Agents.Contains(agent))
			{
				Agents.Add(agent);
				agent.CrowdIndex = Agents.Count - 1;
			}
		}

		public void Remove(NavMeshAgent agent)
		{
			int index = Agents.IndexOf(agent);
			if (index < 0)
			{
				return;
			}

			Agents.RemoveAt(index);
			for (int i = index; i < Agents.Count; i++)
			{
				Agents[i].CrowdIndex = i;
			}
		}

		public void Clear()
		{
			Agents.Clear();
			pathQueue.Clear();
		}

		public void Update(float dt)
		{
			if (Agents.Count == 0)
			{
				pathQueue.Clear();
				return;
			}

			CheckPathValidity(dt);
			UpdateMoveRequests();
			UpdateTopologyOptimization(dt);

			grid.Clear();
			for (int i = 0; i < Agents.Count; i++)
			{
				NavMeshAgent ag = Agents[i];
				Vector3 p = ag.Position;
				float r = ag.radius;
				grid.AddItem(i, p.X - r, p.Z - r, p.X + r, p.Z + r);
			}

			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State != AgentState.Walking)
				{
					continue;
				}

				float updateThreshold = ag.CollisionQueryRange * 0.25f;
				if (NavMath.DistSqr2D(ag.Position, ag.Boundary.Center) > updateThreshold * updateThreshold || !ag.Boundary.IsValid(Query, ag.Filter))
				{
					ag.Boundary.Update(ag.Corridor.FirstPoly, ag.Position, ag.CollisionQueryRange, Query, ag.Filter);
				}

				FindNeighbors(ag);
			}

			// Corners and visibility optimization.
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State != AgentState.Walking || ag.TargetState == TargetState.None || ag.TargetState == TargetState.Velocity)
				{
					continue;
				}

				ag.CornerCount = ag.Corridor.FindCorners(ag.CornerVerts, ag.CornerFlags, ag.CornerPolys, MaxCorners, Query, ag.Filter);
				if (ag.CornerCount > 0)
				{
					Vector3 target = ag.CornerVerts[Math.Min(1, ag.CornerCount - 1)];
					ag.Corridor.OptimizePathVisibility(target, ag.PathOptimizationRange, Query, ag.Filter);
				}
			}

			// Steering.
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State != AgentState.Walking)
				{
					continue;
				}

				ag.DesiredVelocityInternal = Vector3.Zero;
				if (ag.TargetState == TargetState.None || ag.isStopped)
				{
					continue;
				}

				Vector3 dvel;
				if (ag.TargetState == TargetState.Velocity)
				{
					dvel = ag.TargetPos;
				}
				else
				{
					dvel = SmoothSteerDirection(ag);
					float slowDownRadius = ag.radius * 2f;
					float distanceToGoal = DistanceToGoal(ag, slowDownRadius + ag.stoppingDistance);
					if (ag.AtDestination(distanceToGoal))
					{
						dvel = Vector3.Zero;
					}
					else
					{
						float speedScale = ag.autoBraking ? Math.Clamp((distanceToGoal - ag.stoppingDistance) / slowDownRadius, 0f, 1f) : 1f;
						dvel *= ag.speed * speedScale;
					}
				}

				ag.DesiredVelocityInternal = ag.ModifyDesiredVelocity(dvel);
			}

			// Velocity planning.
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State != AgentState.Walking)
				{
					continue;
				}

				Vector3 dvel = ag.DesiredVelocityInternal;
				if (ag.obstacleAvoidanceType != ObstacleAvoidanceType.NoObstacleAvoidance && (dvel.LengthSquared > 1e-6f || ag.VelocityInternal.LengthSquared > 1e-6f))
				{
					avoidance.Reset();
					for (int j = 0; j < ag.NeighborCount; j++)
					{
						NavMeshAgent nei = Agents[ag.Neighbors[j]];
						// Less important agents (higher priority value) give way: the important one does not steer around them.
						if (nei.avoidancePriority > ag.avoidancePriority)
						{
							continue;
						}

						avoidance.AddCircle(nei.Position, nei.radius, nei.VelocityInternal, nei.DesiredVelocityInternal);
					}

					foreach ((Vector3 a, Vector3 b) segment in ag.Boundary.Segments)
					{
						if (NavMath.TriArea2D(ag.Position, segment.a, segment.b) < 0f)
						{
							continue;
						}

						avoidance.AddSegment(segment.a, segment.b);
					}

					NavMeshCarver.AddAvoidanceObstacles(avoidance, ag.Position, ag.CollisionQueryRange);
					ObstacleAvoidanceParams parameters = AvoidanceParams[Math.Clamp((int)ag.obstacleAvoidanceType - 1, 0, AvoidanceParams.Length - 1)];
					parameters.HorizTime = NavMesh.AvoidancePredictionTime;
					avoidance.SampleVelocityAdaptive(ag.Position, ag.radius, ag.speed, ag.VelocityInternal, dvel, out Vector3 nvel, parameters);
					ag.NewVelocity = nvel;
				}
				else
				{
					ag.NewVelocity = dvel;
				}
			}

			// Integrate.
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State != AgentState.Walking)
				{
					continue;
				}

				float maxDelta = ag.acceleration * dt;
				Vector3 dv = ag.NewVelocity - ag.VelocityInternal;
				float ds = dv.Length;
				if (ds > maxDelta)
				{
					dv *= maxDelta / ds;
				}

				ag.VelocityInternal += dv;
				if (ag.VelocityInternal.Length > 0.0001f)
				{
					ag.Position += ag.VelocityInternal * dt;
				}
				else
				{
					ag.VelocityInternal = Vector3.Zero;
				}
			}

			// Collisions between agents.
			for (int iter = 0; iter < 4; iter++)
			{
				for (int idx0 = 0; idx0 < Agents.Count; idx0++)
				{
					NavMeshAgent ag = Agents[idx0];
					ag.Displacement = Vector3.Zero;
					if (ag.State != AgentState.Walking)
					{
						continue;
					}

					float w = 0f;
					Vector3 disp = Vector3.Zero;
					for (int j = 0; j < ag.NeighborCount; j++)
					{
						int idx1 = ag.Neighbors[j];
						NavMeshAgent nei = Agents[idx1];
						Vector3 diff = ag.Position - nei.Position;
						diff.Y = 0f;
						float dist = diff.LengthSquared;
						float radii = ag.radius + nei.radius;
						if (dist > radii * radii)
						{
							continue;
						}

						dist = (float)Math.Sqrt(dist);
						float pen = radii - dist;
						if (dist < 0.0001f)
						{
							diff = idx0 > idx1 ? new Vector3(-ag.DesiredVelocityInternal.Z, 0f, ag.DesiredVelocityInternal.X) : new Vector3(ag.DesiredVelocityInternal.Z, 0f, -ag.DesiredVelocityInternal.X);
							pen = 0.01f;
						}
						else
						{
							pen = 1f / dist * (pen * 0.5f) * CollisionResolveFactor;
						}

						disp += diff * pen;
						w += 1f;
					}

					if (w > 0.0001f)
					{
						ag.Displacement = disp * (1f / w);
					}
				}

				foreach (NavMeshAgent ag in Agents)
				{
					if (ag.State == AgentState.Walking)
					{
						ag.Position += ag.Displacement;
					}
				}
			}

			// Slide along the navmesh.
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State != AgentState.Walking)
				{
					continue;
				}

				ag.Corridor.MovePosition(ag.Position, Query, ag.Filter);
				ag.Position = ag.Corridor.Pos;
				if (ag.TargetState == TargetState.None || ag.TargetState == TargetState.Velocity)
				{
					ag.Corridor.Reset(ag.Corridor.FirstPoly, ag.Position);
					ag.PartialPath = false;
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void CheckPathValidity(float dt)
		{
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State == AgentState.Invalid)
				{
					// Off the navmesh: try to get on it again.
					if (Query.FindNearestPoly(ag.Position, extents, ag.Filter, out PolyRef reference, out Vector3 nearest))
					{
						ag.Position = nearest;
						ag.Corridor.Reset(reference, nearest);
						ag.Boundary.Reset();
						ag.State = AgentState.Walking;
						if (ag.TargetState != TargetState.None && ag.TargetState != TargetState.Velocity)
						{
							ag.RequestMoveTarget(ag.TargetRef, ag.TargetPos, true);
						}
					}

					continue;
				}

				ag.TargetReplanTime += dt;
				bool replan = false;
				PolyRef agentRef = ag.Corridor.FirstPoly;
				Vector3 agentPos = ag.Position;
				if (!Query.IsValidPolyRef(agentRef, ag.Filter))
				{
					if (!Query.FindNearestPoly(ag.Position, extents, ag.Filter, out agentRef, out agentPos))
					{
						ag.Corridor.Reset(PolyRef.Null, ag.Position);
						ag.Boundary.Reset();
						ag.State = AgentState.Invalid;
						continue;
					}

					ag.Corridor.FixPathStart(agentRef, agentPos);
					ag.Boundary.Reset();
					ag.Position = agentPos;
					replan = true;
				}

				if (ag.TargetState == TargetState.None || ag.TargetState == TargetState.Velocity)
				{
					continue;
				}

				if (ag.TargetState != TargetState.Failed)
				{
					if (!Query.IsValidPolyRef(ag.TargetRef, ag.Filter))
					{
						if (Query.FindNearestPoly(ag.TargetPos, extents, ag.Filter, out PolyRef targetRef, out Vector3 nearest))
						{
							ag.TargetRef = targetRef;
							ag.TargetPos = nearest;
						}
						else
						{
							ag.TargetRef = PolyRef.Null;
						}

						replan = true;
					}

					if (ag.TargetRef.IsNull)
					{
						ag.Corridor.Reset(agentRef, agentPos);
						ag.TargetState = TargetState.None;
						continue;
					}
				}

				if (!ag.Corridor.IsValid(CheckLookAhead, Query, ag.Filter))
				{
					replan = true;
				}

				if (ag.TargetState == TargetState.Valid && ag.TargetReplanTime > TargetReplanDelay && ag.Corridor.PathCount < CheckLookAhead && ag.Corridor.LastPoly != ag.TargetRef)
				{
					replan = true;
				}

				if (replan && ag.autoRepath && ag.TargetState != TargetState.None)
				{
					ag.RequestMoveTarget(ag.TargetRef, ag.TargetPos, true);
				}
			}
		}

		private void UpdateMoveRequests()
		{
			queue.Clear();
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State == AgentState.Invalid)
				{
					continue;
				}

				if (ag.TargetState == TargetState.Requesting)
				{
					List<PolyRef> path = ag.Corridor.Path;
					if (path.Count == 0)
					{
						ag.TargetState = TargetState.Failed;
						continue;
					}

					// Quick search toward the goal keeps the agent moving while the full path is computed.
					Query.InitSlicedFindPath(path[0], ag.TargetRef, ag.Position, ag.TargetPos, ag.Filter);
					Query.UpdateSlicedFindPath(20, out _);
					NavStatus status = ag.TargetReplan
						? Query.FinalizeSlicedFindPathPartial(path, path.Count, scratchPath, 32)
						: Query.FinalizeSlicedFindPath(scratchPath, 32);

					Vector3 reqPos = ag.Position;
					if (!status.Failed() && scratchPath.Count > 0)
					{
						if (scratchPath[scratchPath.Count - 1] != ag.TargetRef)
						{
							if (!Query.ClosestPointOnPoly(scratchPath[scratchPath.Count - 1], ag.TargetPos, out reqPos, out _))
							{
								scratchPath.Clear();
							}
						}
						else
						{
							reqPos = ag.TargetPos;
						}
					}
					else
					{
						scratchPath.Clear();
					}

					if (scratchPath.Count == 0)
					{
						reqPos = ag.Position;
						scratchPath.Add(path[0]);
					}

					ag.Corridor.SetCorridor(reqPos, scratchPath);
					ag.Boundary.Reset();
					ag.PartialPath = false;
					if (scratchPath[scratchPath.Count - 1] == ag.TargetRef)
					{
						ag.TargetState = TargetState.Valid;
						ag.TargetReplanTime = 0f;
					}
					else
					{
						ag.TargetState = TargetState.WaitingForQueue;
					}
				}

				if (ag.TargetState == TargetState.WaitingForQueue)
				{
					queue.Add(ag);
				}
			}

			// Longest waiting first.
			queue.Sort(byReplanTime);
			foreach (NavMeshAgent ag in queue)
			{
				ag.TargetPathQueueRef = pathQueue.Request(ag.Corridor.LastPoly, ag.TargetRef, ag.Corridor.Target, ag.TargetPos, ag.Filter);
				if (ag.TargetPathQueueRef != PathQueue.Invalid)
				{
					ag.TargetState = TargetState.WaitingForPath;
				}
			}

			pathQueue.Update(NavMesh.PathfindingIterationsPerFrame);

			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State == AgentState.Invalid || ag.TargetState != TargetState.WaitingForPath)
				{
					continue;
				}

				NavStatus status = pathQueue.GetRequestStatus(ag.TargetPathQueueRef);
				if (status.Failed())
				{
					ag.TargetPathQueueRef = PathQueue.Invalid;
					ag.TargetState = ag.TargetRef.IsNull ? TargetState.Failed : TargetState.Requesting;
					ag.TargetReplanTime = 0f;
					continue;
				}

				if (!status.Succeeded())
				{
					continue;
				}

				List<PolyRef> path = ag.Corridor.Path;
				Vector3 targetPos = ag.TargetPos;
				bool valid = true;
				status = pathQueue.GetPathResult(ag.TargetPathQueueRef, resultPath);
				if (status.Failed() || resultPath.Count == 0)
				{
					valid = false;
				}

				ag.PartialPath = status.IsPartial();
				if (valid && path.Count > 0 && path[path.Count - 1] != resultPath[0])
				{
					valid = false;
				}

				if (valid)
				{
					if (path.Count > 1)
					{
						// The agent kept moving: the corridor walked so far goes in front of the new path.
						resultPath.RemoveAt(0);
						resultPath.InsertRange(0, path);
						for (int j = 0; j < resultPath.Count - 1; j++)
						{
							if (resultPath[j] == resultPath[j + 1])
							{
								resultPath.RemoveAt(j + 1);
								j--;
							}
						}

						if (resultPath.Count > PathCorridor.MaxPath)
						{
							resultPath.RemoveRange(PathCorridor.MaxPath, resultPath.Count - PathCorridor.MaxPath);
						}
					}

					if (resultPath[resultPath.Count - 1] != ag.TargetRef)
					{
						if (Query.ClosestPointOnPoly(resultPath[resultPath.Count - 1], targetPos, out Vector3 nearest, out _))
						{
							targetPos = nearest;
						}
						else
						{
							valid = false;
						}
					}
				}

				if (valid)
				{
					ag.Corridor.SetCorridor(targetPos, resultPath);
					ag.Boundary.Reset();
					ag.TargetState = TargetState.Valid;
				}
				else
				{
					ag.TargetState = TargetState.Failed;
				}

				ag.TargetReplanTime = 0f;
			}
		}

		private void UpdateTopologyOptimization(float dt)
		{
			queue.Clear();
			foreach (NavMeshAgent ag in Agents)
			{
				if (ag.State != AgentState.Walking || ag.TargetState == TargetState.None || ag.TargetState == TargetState.Velocity)
				{
					continue;
				}

				ag.TopologyOptTime += dt;
				if (ag.TopologyOptTime >= TopologyOptimizeInterval)
				{
					queue.Add(ag);
				}
			}

			queue.Sort(byTopologyTime);
			for (int i = 0; i < Math.Min(MaxTopologyOptimizations, queue.Count); i++)
			{
				queue[i].Corridor.OptimizePathTopology(Query, queue[i].Filter);
				queue[i].TopologyOptTime = 0f;
			}
		}

		private void FindNeighbors(NavMeshAgent ag)
		{
			ag.NeighborCount = 0;
			float range = ag.CollisionQueryRange;
			Vector3 pos = ag.Position;
			int n = grid.QueryItems(pos.X - range, pos.Z - range, pos.X + range, pos.Z + range, neighborIds, neighborIds.Length);
			float rangeSqr = range * range;
			for (int i = 0; i < n; i++)
			{
				NavMeshAgent other = Agents[neighborIds[i]];
				if (other == ag)
				{
					continue;
				}

				Vector3 diff = pos - other.Position;
				if (Math.Abs(diff.Y) >= (ag.height + other.height) * 0.5f)
				{
					continue;
				}

				diff.Y = 0f;
				float distSqr = diff.LengthSquared;
				if (distSqr > rangeSqr)
				{
					continue;
				}

				// Insert sorted by distance; keep the nearest MaxNeighbors.
				int at = ag.NeighborCount;
				while (at > 0 && ag.NeighborDistances[at - 1] > distSqr)
				{
					at--;
				}

				if (at >= MaxNeighbors)
				{
					continue;
				}

				int count = Math.Min(ag.NeighborCount, MaxNeighbors - 1);
				for (int k = count; k > at; k--)
				{
					ag.Neighbors[k] = ag.Neighbors[k - 1];
					ag.NeighborDistances[k] = ag.NeighborDistances[k - 1];
				}

				ag.Neighbors[at] = neighborIds[i];
				ag.NeighborDistances[at] = distSqr;
				ag.NeighborCount = Math.Min(ag.NeighborCount + 1, MaxNeighbors);
			}
		}

		private static Vector3 SmoothSteerDirection(NavMeshAgent ag)
		{
			if (ag.CornerCount == 0)
			{
				return Vector3.Zero;
			}

			Vector3 p0 = ag.CornerVerts[0];
			Vector3 p1 = ag.CornerVerts[Math.Min(1, ag.CornerCount - 1)];
			Vector3 dir0 = p0 - ag.Position;
			Vector3 dir1 = p1 - ag.Position;
			dir0.Y = 0f;
			dir1.Y = 0f;
			float len0 = dir0.Length;
			float len1 = dir1.Length;
			if (len1 > 0.001f)
			{
				dir1 /= len1;
			}

			Vector3 dir = new Vector3(dir0.X - dir1.X * len0 * 0.5f, 0f, dir0.Z - dir1.Z * len0 * 0.5f);
			if (dir.LengthSquared > 1e-12f)
			{
				dir.Normalize();
			}

			return dir;
		}

		private static float DistanceToGoal(NavMeshAgent ag, float range)
		{
			if (ag.CornerCount == 0)
			{
				return range;
			}

			bool endOfPath = (ag.CornerFlags[ag.CornerCount - 1] & NavMeshQuery.StraightPathEnd) != 0;
			return endOfPath ? Math.Min(NavMath.Dist2D(ag.Position, ag.CornerVerts[ag.CornerCount - 1]), range) : range;
		}

		#endregion
	}
}
