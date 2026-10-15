using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>Result of a path request.</summary>
	public enum NavMeshPathStatus
	{
		/// <summary>The path reaches the destination.</summary>
		PathComplete,

		/// <summary>The path ends as close to the destination as the navmesh allows.</summary>
		PathPartial,

		/// <summary>No path: a position is off the navmesh.</summary>
		PathInvalid
	}

	/// <summary>A path on the navmesh: the polygons and the straightened corner points.</summary>
	public sealed class NavMeshPath
	{
		#region PublicFields

		public NavMeshPathStatus Status { get; internal set; } = NavMeshPathStatus.PathInvalid;

		/// <summary>Corner points from the start to the end (world space); empty for an invalid path.</summary>
		public Vector3[] Corners => corners;

		#endregion

		#region InternalFields

		internal readonly List<PolyRef> Polys = new List<PolyRef>();
		internal Vector3 Start;
		internal Vector3 End;

		#endregion

		#region PrivateFields

		private Vector3[] corners = Array.Empty<Vector3>();

		#endregion

		#region PublicMethods

		public void ClearCorners()
		{
			corners = Array.Empty<Vector3>();
			Polys.Clear();
			Status = NavMeshPathStatus.PathInvalid;
		}

		/// <summary>Copies the corners into <paramref name="results" />; returns the count.</summary>
		public int GetCornersNonAlloc(Vector3[] results)
		{
			int n = Math.Min(results.Length, corners.Length);
			Array.Copy(corners, results, n);
			return n;
		}

		/// <summary>Length along the corners.</summary>
		public float Length
		{
			get
			{
				float length = 0f;
				for (int i = 1; i < corners.Length; i++)
				{
					length += (corners[i] - corners[i - 1]).Length;
				}

				return length;
			}
		}

		#endregion

		#region InternalMethods

		internal void SetCorners(Vector3[] buffer, int count)
		{
			corners = new Vector3[count];
			Array.Copy(buffer, corners, count);
		}

		#endregion
	}

	/// <summary>
	///     Navigation: the surfaces of the scene as one walkable world, pathfinding and position queries, agents
	///     with local avoidance and obstacles that carve the navmesh. Baked <see cref="NavMeshSurface" /> components
	///     register themselves while enabled; agents and obstacles likewise. Updated once per frame by the scene
	///     after the Update callbacks (before LateUpdate), like Unity's navigation step.
	/// </summary>
	public static class NavMesh
	{
		#region PublicFields

		public const int AllAreas = NavMeshArea.AllAreas;

		/// <summary>A* iterations spent per frame on queued agent paths.</summary>
		public static int PathfindingIterationsPerFrame = 100;

		/// <summary>How far ahead (seconds) local avoidance predicts collisions.</summary>
		public static float AvoidancePredictionTime = 2.5f;

		/// <summary>Carved tiles rebuilt per background job (latency against throughput).</summary>
		public static int CarveTilesPerJob = 32;

		/// <summary>Draw the navmesh, agents and obstacles in the game (debugging); the editor draws them in the scene view.</summary>
		public static bool DrawDebug
		{
			get => Rendering.DebugDraw.VisibleInGame;
			set => Rendering.DebugDraw.VisibleInGame = value;
		}

		/// <summary>Raised after surfaces were added or removed or tiles changed (carving).</summary>
		public static event Action NavMeshChanged;

		/// <summary>CPU time of the last navigation step (agents, carving bookkeeping) in milliseconds.</summary>
		public static double LastUpdateMilliseconds { get; private set; }

		/// <summary>Area names shown by the editor (index = area).</summary>
		public static readonly string[] AreaNames = CreateAreaNames();

		#endregion

		#region InternalFields

		internal static readonly NavMeshWorld World = new NavMeshWorld();
		internal static readonly float[] AreaCosts = CreateAreaCosts();
		internal static readonly NavMeshCrowd Crowd = new NavMeshCrowd(World);
		internal static readonly NavMeshCarver Carver = new NavMeshCarver(World);
		internal static readonly List<NavMeshSurface> Surfaces = new List<NavMeshSurface>();
		internal static readonly NavMeshQuery SharedQuery = new NavMeshQuery(World, 4096);
		internal static readonly Random Random = new Random(12345);

		#endregion

		#region PrivateFields

		private static readonly NavMeshQueryFilter sharedFilter = new NavMeshQueryFilter { AgentTypeId = -1 };
		private static readonly Vector3[] cornerBuffer = new Vector3[256];
		private static readonly byte[] cornerFlags = new byte[256];
		private static readonly PolyRef[] cornerRefs = new PolyRef[256];
		private static readonly List<PolyRef> pathBuffer = new List<PolyRef>();
		private static int lastWorldVersion = -1;

		#endregion

		#region PublicMethods

		/// <summary>Registered surfaces with baked data.</summary>
		public static int SurfaceCount => World.SurfaceCount;

		public static float GetAreaCost(int area)
		{
			return area >= 0 && area < AreaCosts.Length ? AreaCosts[area] : 1f;
		}

		/// <summary>Cost multiplier of an area for every query without its own costs (1 = distance).</summary>
		public static void SetAreaCost(int area, float cost)
		{
			if (area >= 0 && area < AreaCosts.Length)
			{
				AreaCosts[area] = Math.Max(0.01f, cost);
			}
		}

		public static int GetAreaFromName(string name)
		{
			for (int i = 0; i < AreaNames.Length; i++)
			{
				if (string.Equals(AreaNames[i], name, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>Nearest navmesh position within <paramref name="maxDistance" /> of the source (any agent type).</summary>
		public static bool SamplePosition(Vector3 source, out NavMeshHit hit, float maxDistance, int areaMask)
		{
			return SamplePosition(source, out hit, maxDistance, areaMask, -1);
		}

		public static bool SamplePosition(Vector3 source, out NavMeshHit hit, float maxDistance, int areaMask, int agentTypeId)
		{
			sharedFilter.AreaMask = areaMask;
			sharedFilter.AgentTypeId = agentTypeId;
			hit = default;
			Vector3 extents = new Vector3(maxDistance);
			if (!SharedQuery.FindNearestPoly(source, extents, sharedFilter, out PolyRef reference, out Vector3 nearest))
			{
				return false;
			}

			hit.Position = nearest;
			hit.Distance = (nearest - source).Length;
			if (hit.Distance > maxDistance)
			{
				return false;
			}

			hit.Normal = Vector3.UnitY;
			hit.Mask = 1 << SharedQuery.GetPolyArea(reference);
			hit.Hit = true;
			hit.Ref = reference;
			return true;
		}

		/// <summary>Path between two positions (the start and end snap to the navmesh within 2 units).</summary>
		public static bool CalculatePath(Vector3 sourcePosition, Vector3 targetPosition, int areaMask, NavMeshPath path)
		{
			return CalculatePath(sourcePosition, targetPosition, areaMask, -1, path);
		}

		public static bool CalculatePath(Vector3 sourcePosition, Vector3 targetPosition, int areaMask, int agentTypeId, NavMeshPath path)
		{
			sharedFilter.AreaMask = areaMask;
			sharedFilter.AgentTypeId = agentTypeId;
			return CalculatePath(SharedQuery, sharedFilter, sourcePosition, targetPosition, path);
		}

		internal static bool CalculatePath(NavMeshQuery query, NavMeshQueryFilter filter, Vector3 sourcePosition, Vector3 targetPosition, NavMeshPath path)
		{
			path.ClearCorners();
			Vector3 extents = new Vector3(2f, 4f, 2f);
			if (!query.FindNearestPoly(sourcePosition, extents, filter, out PolyRef startRef, out Vector3 start)
				|| !query.FindNearestPoly(targetPosition, extents, filter, out PolyRef endRef, out Vector3 end))
			{
				return false;
			}

			pathBuffer.Clear();
			NavStatus status = query.FindPath(startRef, endRef, start, end, filter, pathBuffer, 256);
			if (status.Failed() || pathBuffer.Count == 0)
			{
				return false;
			}

			Vector3 target = end;
			if (status.IsPartial())
			{
				query.ClosestPointOnPoly(pathBuffer[pathBuffer.Count - 1], end, out target, out _);
			}

			query.FindStraightPath(start, target, pathBuffer, pathBuffer.Count, cornerBuffer, cornerFlags, cornerRefs, out int count, cornerBuffer.Length);
			path.SetCorners(cornerBuffer, count);
			path.Polys.AddRange(pathBuffer);
			path.Start = start;
			path.End = target;
			path.Status = status.IsPartial() ? NavMeshPathStatus.PathPartial : NavMeshPathStatus.PathComplete;
			return true;
		}

		/// <summary>Traces a line along the navmesh; true when a wall blocks it before the target (hit holds where).</summary>
		public static bool Raycast(Vector3 sourcePosition, Vector3 targetPosition, out NavMeshHit hit, int areaMask)
		{
			return Raycast(sourcePosition, targetPosition, out hit, areaMask, -1);
		}

		public static bool Raycast(Vector3 sourcePosition, Vector3 targetPosition, out NavMeshHit hit, int areaMask, int agentTypeId)
		{
			sharedFilter.AreaMask = areaMask;
			sharedFilter.AgentTypeId = agentTypeId;
			hit = default;
			if (!SharedQuery.FindNearestPoly(sourcePosition, new Vector3(2f, 4f, 2f), sharedFilter, out PolyRef startRef, out Vector3 start))
			{
				hit.Position = sourcePosition;
				hit.Hit = true;
				return true;
			}

			NavStatus status = SharedQuery.Raycast(startRef, start, targetPosition, sharedFilter, out float t, out Vector3 normal, null);
			if (status.Failed())
			{
				hit.Position = start;
				hit.Hit = true;
				return true;
			}

			if (t >= float.MaxValue)
			{
				hit.Position = targetPosition;
				hit.Distance = (targetPosition - start).Length;
				hit.Hit = false;
				return false;
			}

			hit.Position = Vector3.Lerp(start, targetPosition, t);
			hit.Normal = normal;
			hit.Distance = (hit.Position - start).Length;
			hit.Hit = true;
			return true;
		}

		/// <summary>The nearest navmesh edge (wall) to the position.</summary>
		public static bool FindClosestEdge(Vector3 sourcePosition, out NavMeshHit hit, int areaMask)
		{
			sharedFilter.AreaMask = areaMask;
			sharedFilter.AgentTypeId = -1;
			hit = default;
			if (!SharedQuery.FindNearestPoly(sourcePosition, new Vector3(2f, 4f, 2f), sharedFilter, out PolyRef startRef, out Vector3 start))
			{
				return false;
			}

			NavStatus status = SharedQuery.FindDistanceToWall(startRef, start, 100f, sharedFilter, out float distance, out Vector3 position, out Vector3 normal);
			if (status.Failed())
			{
				return false;
			}

			hit.Position = position;
			hit.Normal = normal;
			hit.Distance = distance;
			hit.Hit = true;
			hit.Mask = 1 << SharedQuery.GetPolyArea(startRef);
			return true;
		}

		/// <summary>A random reachable point on the navmesh (for wandering agents, tests).</summary>
		public static bool FindRandomPoint(out Vector3 point, int areaMask = AllAreas, int agentTypeId = -1)
		{
			sharedFilter.AreaMask = areaMask;
			sharedFilter.AgentTypeId = agentTypeId;
			return SharedQuery.FindRandomPoint(sharedFilter, Random, out _, out point);
		}

		#endregion

		#region InternalMethods

		internal static void RegisterSurface(NavMeshSurface surface)
		{
			if (!Surfaces.Contains(surface))
			{
				Surfaces.Add(surface);
			}
		}

		internal static void UnregisterSurface(NavMeshSurface surface)
		{
			Surfaces.Remove(surface);
		}

		/// <summary>One navigation step: moved surfaces, obstacle carving and the agents.</summary>
		internal static void Update(Scene scene, float deltaTime)
		{
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			foreach (NavMeshSurface surface in Surfaces)
			{
				surface.SyncTransform();
			}

			Carver.Update(deltaTime, scene.IsPlaying);
			if (scene.IsPlaying)
			{
				Crowd.Update(deltaTime);
			}

			LastUpdateMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (DrawDebug)
			{
				NavMeshDebug.DrawAll();
			}

			if (World.Version != lastWorldVersion)
			{
				lastWorldVersion = World.Version;
				NavMeshChanged?.Invoke();
			}
		}

		/// <summary>Drops everything registered (the scene is gone).</summary>
		internal static void Reset()
		{
			Crowd.Clear();
			Carver.Clear();
		}

		#endregion

		#region PrivateMethods

		private static string[] CreateAreaNames()
		{
			var names = new string[NavMeshArea.Count];
			names[NavMeshArea.Walkable] = "Walkable";
			names[NavMeshArea.NotWalkable] = "Not Walkable";
			names[NavMeshArea.Jump] = "Jump";
			for (int i = 3; i < names.Length; i++)
			{
				names[i] = "User " + i;
			}

			return names;
		}

		private static float[] CreateAreaCosts()
		{
			var costs = new float[NavMeshArea.Count];
			Array.Fill(costs, 1f);
			costs[NavMeshArea.Jump] = 2f;
			return costs;
		}

		#endregion
	}
}
