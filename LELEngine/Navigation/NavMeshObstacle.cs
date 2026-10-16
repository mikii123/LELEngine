using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LELEngine.Navigation.Builder;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	public enum NavMeshObstacleShape
	{
		Capsule,
		Box
	}

	/// <summary>
	///     A moving or temporary obstacle (Unity's NavMeshObstacle). Agents steer around it; with <see cref="carving" />
	///     it also cuts a hole into the navmesh (rebuilding the touched tiles in the background), so paths go around
	///     it. By default a moving obstacle stops carving until it has stood still for <see cref="carvingTimeToStationary" />.
	/// </summary>
	[ExecuteAlways]
	public sealed class NavMeshObstacle : Behaviour
	{
		#region PublicFields

		public NavMeshObstacleShape shape = NavMeshObstacleShape.Box;

		[Tooltip("Center of the shape in the object's local space.")]
		public Vector3 center;

		[Tooltip("Box size in local space.")]
		public Vector3 size = Vector3.One;

		[Tooltip("Capsule radius in local space.")]
		public float radius = 0.5f;

		[Tooltip("Capsule height in local space.")]
		public float height = 2f;

		[Header("Carving")]
		[Tooltip("Cut the obstacle out of the navmesh (paths go around; agents cannot enter).")]
		public bool carving = true;

		[Tooltip("Carve only after the obstacle stood still for a while; a moving obstacle is avoided dynamically.")]
		public bool carveOnlyStationary = true;

		[Tooltip("Distance the shape must move (translation, rotation or scale) before the hole is carved again; 0 re-carves on any change.")]
		public float carvingMoveThreshold = 0.1f;

		[Tooltip("Seconds without movement before a moving obstacle carves again.")]
		public float carvingTimeToStationary = 0.5f;

		/// <summary>The obstacle currently carves a hole into the navmesh.</summary>
		public bool IsCarving => Carved;

		#endregion

		#region InternalFields

		internal bool Registered;
		internal Vector3 CarvedPosition;
		internal Quaternion CarvedRotation;
		internal Vector3 CarvedScale;
		internal bool Carved;
		internal float StationaryTime;
		internal Vector3 LastPosition;
		internal Quaternion LastRotation;
		internal Vector3 LastScale;
		internal bool ShapeChanged;
		internal Vector3 CarvedMin;
		internal Vector3 CarvedMax;

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			if (!Registered)
			{
				Registered = true;
				NavMesh.Carver.Add(this);
			}
		}

		public override void OnDisable()
		{
			if (Registered)
			{
				Registered = false;
				NavMesh.Carver.Remove(this);
			}
		}

		protected internal override void ReleaseResources()
		{
			if (Registered)
			{
				Registered = false;
				NavMesh.Carver.Remove(this);
			}
		}

		/// <summary>Shape fields changed: the carved hole is rebuilt.</summary>
		public override void OnValidate()
		{
			ShapeChanged = true;
		}

		/// <summary>Added in the editor: the shape fits the object's mesh (as Unity's obstacle does).</summary>
		public override void Reset()
		{
			Mesh mesh = GetComponent<MeshRenderer>()?.Mesh;
			if (mesh == null)
			{
				return;
			}

			Vector3 min = mesh.BoundsMin;
			Vector3 max = mesh.BoundsMax;
			center = (min + max) * 0.5f;
			size = max - min;
			radius = Math.Max(size.X, size.Z) * 0.5f;
			height = size.Y;
			ShapeChanged = true;
		}

		#endregion

		#region PublicMethods

		/// <summary>World-space footprint of the obstacle (yaw-aligned; pitch and roll are ignored).</summary>
		public void GetWorldShape(out Vector3 worldCenter, out Vector2 axisX, out Vector2 axisZ, out Vector3 halfExtents, out float worldRadius)
		{
			Transform t = transform;
			Vector3 scale = t.lossyScale;
			worldCenter = t.TransformPoint(center);
			Vector3 right = t.right;
			Vector3 forward = t.forward;
			axisX = new Vector2(right.X, right.Z);
			axisZ = new Vector2(forward.X, forward.Z);
			if (axisX.LengthSquared < 1e-8f) axisX = Vector2.UnitX;
			if (axisZ.LengthSquared < 1e-8f) axisZ = Vector2.UnitY;
			axisX.Normalize();
			axisZ.Normalize();
			if (shape == NavMeshObstacleShape.Box)
			{
				halfExtents = new Vector3(Math.Abs(size.X * scale.X), Math.Abs(size.Y * scale.Y), Math.Abs(size.Z * scale.Z)) * 0.5f;
				worldRadius = Math.Max(halfExtents.X, halfExtents.Z);
			}
			else
			{
				worldRadius = radius * Math.Max(Math.Abs(scale.X), Math.Abs(scale.Z));
				halfExtents = new Vector3(worldRadius, Math.Max(height * Math.Abs(scale.Y), worldRadius * 2f) * 0.5f, worldRadius);
			}
		}

		/// <summary>World-space bounds of the footprint.</summary>
		public void GetWorldBounds(out Vector3 min, out Vector3 max)
		{
			GetWorldShape(out Vector3 c, out Vector2 ax, out Vector2 az, out Vector3 half, out float r);
			float ex = shape == NavMeshObstacleShape.Box ? Math.Abs(ax.X) * half.X + Math.Abs(az.X) * half.Z : r;
			float ez = shape == NavMeshObstacleShape.Box ? Math.Abs(ax.Y) * half.X + Math.Abs(az.Y) * half.Z : r;
			min = new Vector3(c.X - ex, c.Y - half.Y, c.Z - ez);
			max = new Vector3(c.X + ex, c.Y + half.Y, c.Z + ez);
		}

		#endregion
	}

	/// <summary>
	///     Dynamic carving: tracks the carving obstacles, rebuilds the tiles their footprints touch from the stored
	///     heightfield layers on a worker thread and swaps the polygons in on the main thread. Tiles no obstacle
	///     touches any more get their baked polygons back without a rebuild.
	/// </summary>
	internal sealed class NavMeshCarver
	{
		#region PrivateFields

		private readonly NavMeshWorld world;
		private readonly List<NavMeshObstacle> obstacles = new List<NavMeshObstacle>();

		public IReadOnlyList<NavMeshObstacle> Obstacles => obstacles;
		private readonly HashSet<NavTile> dirty = new HashSet<NavTile>();
		private readonly List<NavTile> queryTiles = new List<NavTile>();
		private Task<List<(NavTile tile, NavMeshTileData data, bool carved)>> job;
		private int jobWorldVersion;
		private static readonly List<NavMeshObstacle> registry = new List<NavMeshObstacle>();

		private struct ObstacleShape
		{
			public NavMeshObstacle Obstacle;
			public Vector3 WorldCenter;
			public Vector2 AxisX;
			public Vector2 AxisZ;
			public Vector3 HalfExtents;
			public float Radius;
			public bool IsCylinder;
			public Vector3 BoundsMin;
			public Vector3 BoundsMax;
		}

		#endregion

		#region Constructors

		public NavMeshCarver(NavMeshWorld world)
		{
			this.world = world;
		}

		#endregion

		#region PublicMethods

		public void Add(NavMeshObstacle obstacle)
		{
			if (!obstacles.Contains(obstacle))
			{
				obstacles.Add(obstacle);
				registry.Add(obstacle);
				obstacle.Carved = false;
				obstacle.StationaryTime = 0f;
				obstacle.LastPosition = obstacle.transform.position;
				obstacle.LastRotation = obstacle.transform.rotation;
				obstacle.LastScale = obstacle.transform.lossyScale;
			}
		}

		public void Remove(NavMeshObstacle obstacle)
		{
			if (obstacles.Remove(obstacle))
			{
				registry.Remove(obstacle);
				if (obstacle.Carved)
				{
					MarkCarvedTiles(obstacle);
					obstacle.Carved = false;
				}
			}
		}

		public void Clear()
		{
			obstacles.Clear();
			registry.Clear();
			dirty.Clear();
		}

		/// <summary>A surface was (re)added: tiles under carving obstacles must be carved.</summary>
		public void MarkSurface(NavSurface surface)
		{
			foreach (NavMeshObstacle obstacle in obstacles)
			{
				if (!obstacle.Carved)
				{
					continue;
				}

				obstacle.GetWorldBounds(out Vector3 min, out Vector3 max);
				MarkTiles(min, max, surface);
			}
		}

		/// <summary>Non-carving (or moving) obstacles near a position as circles / boxes for local avoidance.</summary>
		public static void AddAvoidanceObstacles(ObstacleAvoidanceQuery query, in Vector3 position, float range)
		{
			foreach (NavMeshObstacle obstacle in registry)
			{
				if (obstacle.Carved || obstacle.destroyed)
				{
					continue;
				}

				obstacle.GetWorldShape(out Vector3 center, out Vector2 ax, out Vector2 az, out Vector3 half, out float radius);
				if (Math.Abs(center.Y - position.Y) > half.Y + 2f || NavMath.Dist2D(center, position) > range + radius)
				{
					continue;
				}

				if (obstacle.shape == NavMeshObstacleShape.Capsule)
				{
					query.AddCircle(center, radius, Vector3.Zero, Vector3.Zero);
				}
				else
				{
					// Box edges as wall segments (counter-clockwise seen from above).
					Vector3 ex = new Vector3(ax.X, 0f, ax.Y) * half.X;
					Vector3 ez = new Vector3(az.X, 0f, az.Y) * half.Z;
					Vector3 c0 = center - ex - ez;
					Vector3 c1 = center + ex - ez;
					Vector3 c2 = center + ex + ez;
					Vector3 c3 = center - ex + ez;
					AddBoxSide(query, position, c0, c1);
					AddBoxSide(query, position, c1, c2);
					AddBoxSide(query, position, c2, c3);
					AddBoxSide(query, position, c3, c0);
				}
			}
		}

		public void Update(float dt, bool playing)
		{
			// Obstacles that moved: their old and new footprints need rebuilding.
			foreach (NavMeshObstacle obstacle in obstacles)
			{
				if (obstacle.destroyed || obstacle.transform == null)
				{
					continue;
				}

				Transform t = obstacle.transform;
				bool moved = Movement(obstacle, obstacle.LastPosition, obstacle.LastRotation, obstacle.LastScale) > obstacle.carvingMoveThreshold;
				if (moved)
				{
					obstacle.LastPosition = t.position;
					obstacle.LastRotation = t.rotation;
					obstacle.LastScale = t.lossyScale;
					obstacle.StationaryTime = 0f;
				}
				else
				{
					obstacle.StationaryTime += dt;
				}

				bool shouldCarve = obstacle.carving && obstacle.isActiveAndEnabled
					&& (!obstacle.carveOnlyStationary || obstacle.StationaryTime >= obstacle.carvingTimeToStationary);
				if (shouldCarve)
				{
					bool changed = !obstacle.Carved || obstacle.ShapeChanged
						|| Movement(obstacle, obstacle.CarvedPosition, obstacle.CarvedRotation, obstacle.CarvedScale) > obstacle.carvingMoveThreshold;
					if (changed)
					{
						if (obstacle.Carved)
						{
							MarkCarvedTiles(obstacle);
						}

						obstacle.CarvedPosition = t.position;
						obstacle.CarvedRotation = t.rotation;
						obstacle.CarvedScale = t.lossyScale;
						obstacle.Carved = true;
						obstacle.ShapeChanged = false;
						obstacle.GetWorldBounds(out obstacle.CarvedMin, out obstacle.CarvedMax);
						MarkTiles(obstacle.CarvedMin, obstacle.CarvedMax, null);
					}
				}
				else if (obstacle.Carved)
				{
					MarkCarvedTiles(obstacle);
					obstacle.Carved = false;
				}
			}

			if (job != null && job.IsCompleted)
			{
				List<(NavTile tile, NavMeshTileData data, bool carved)> results = job.IsFaulted ? null : job.Result;
				if (job.IsFaulted)
				{
					Debug.LogException(job.Exception?.GetBaseException() ?? job.Exception);
				}

				job = null;
				if (results != null)
				{
					foreach ((NavTile tile, NavMeshTileData data, bool carved) in results)
					{
						// The tile may have been replaced or removed meanwhile.
						if (tile.Surface.Tiles.Length > tile.Index && tile.Surface.Tiles[tile.Index] == tile)
						{
							world.ReplaceTile(tile, data, carved);
						}
					}
				}
			}

			if (job == null && dirty.Count > 0)
			{
				StartJob();
			}
		}

		#endregion

		#region PrivateMethods

		private static void AddBoxSide(ObstacleAvoidanceQuery query, in Vector3 position, in Vector3 a, in Vector3 b)
		{
			// Only the sides facing the agent matter (same convention as the navmesh walls).
			if (NavMath.TriArea2D(position, a, b) < 0f)
			{
				query.AddSegment(a, b);
			}
			else
			{
				query.AddSegment(b, a);
			}
		}

		/// <summary>
		///     How far (world units) the obstacle's shape moved since the given transform: translation plus how far its
		///     farthest point travels by the rotation and the scale change. One distance for <see cref="NavMeshObstacle.carvingMoveThreshold" />.
		/// </summary>
		private static float Movement(NavMeshObstacle obstacle, in Vector3 position, in Quaternion rotation, in Vector3 scale)
		{
			Transform t = obstacle.transform;
			Vector3 currentScale = t.lossyScale;
			// Local reach of the shape: from the pivot to its farthest point.
			Vector3 extent = obstacle.shape == NavMeshObstacleShape.Box ? obstacle.size * 0.5f : new Vector3(obstacle.radius, obstacle.height * 0.5f, obstacle.radius);
			Vector3 local = new Vector3(Math.Abs(obstacle.center.X), Math.Abs(obstacle.center.Y), Math.Abs(obstacle.center.Z)) + new Vector3(Math.Abs(extent.X), Math.Abs(extent.Y), Math.Abs(extent.Z));
			float distance = (t.position - position).Length;
			Quaternion currentRotation = t.rotation;
			if (currentRotation != rotation)
			{
				// Rotation angle times the reach: the arc the farthest point travels.
				float dot = Math.Min(1f, Math.Abs(NavMath.QuaternionDot(currentRotation, rotation)));
				float maxScale = Math.Max(Math.Abs(currentScale.X), Math.Max(Math.Abs(currentScale.Y), Math.Abs(currentScale.Z)));
				distance += 2f * (float)Math.Acos(dot) * (local * maxScale).Length;
			}

			if (currentScale != scale)
			{
				Vector3 delta = currentScale - scale;
				distance += new Vector3(Math.Abs(delta.X) * local.X, Math.Abs(delta.Y) * local.Y, Math.Abs(delta.Z) * local.Z).Length;
			}

			return distance;
		}

		private void MarkCarvedTiles(NavMeshObstacle obstacle)
		{
			// Bounds of the carved hole (the obstacle may have moved or changed shape since).
			MarkTiles(obstacle.CarvedMin, obstacle.CarvedMax, null);
		}

		private void MarkTiles(in Vector3 min, in Vector3 max, NavSurface only)
		{
			queryTiles.Clear();
			Vector3 pad = new Vector3(1f, 1f, 1f);
			world.QueryTiles(min - pad, max + pad, -1, queryTiles);
			foreach (NavTile tile in queryTiles)
			{
				if ((only == null || tile.Surface == only) && tile.LayerData != null)
				{
					dirty.Add(tile);
				}
			}

			// Empty tiles (fully carved) are not found by bounds: mark every layer of the cells in range.
			foreach (NavSurface surface in world.Surfaces)
			{
				if (surface == null || (only != null && surface != only) || !surface.CellRange(min - pad, max + pad, out int x0, out int y0, out int x1, out int y1))
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
							if (tile.LayerData != null)
							{
								dirty.Add(tile);
							}
						}
					}
				}
			}
		}

		private void StartJob()
		{
			// Snapshot the carving shapes in world space; the worker transforms them into each surface's space.
			var shapes = new List<ObstacleShape>();
			foreach (NavMeshObstacle obstacle in obstacles)
			{
				if (!obstacle.Carved || obstacle.destroyed)
				{
					continue;
				}

				Transform t = obstacle.transform;
				Vector3 position = t.position;
				Quaternion rotation = t.rotation;
				t.position = obstacle.CarvedPosition;
				t.rotation = obstacle.CarvedRotation;
				obstacle.GetWorldShape(out Vector3 c, out Vector2 ax, out Vector2 az, out Vector3 half, out float r);
				obstacle.GetWorldBounds(out Vector3 min, out Vector3 max);
				t.position = position;
				t.rotation = rotation;
				shapes.Add(new ObstacleShape { Obstacle = obstacle, WorldCenter = c, AxisX = ax, AxisZ = az, HalfExtents = half, Radius = r, IsCylinder = obstacle.shape == NavMeshObstacleShape.Capsule, BoundsMin = min, BoundsMax = max });
			}

			var batch = new List<NavTile>();
			foreach (NavTile tile in dirty)
			{
				batch.Add(tile);
				if (batch.Count >= NavMesh.CarveTilesPerJob)
				{
					break;
				}
			}

			foreach (NavTile tile in batch)
			{
				dirty.Remove(tile);
			}

			// Tiles no shape touches get their baked data back right away.
			var work = new List<(NavTile tile, List<CarveShape> shapes)>();
			foreach (NavTile tile in batch)
			{
				NavSurface surface = tile.Surface;
				if (surface.Tiles.Length <= tile.Index || surface.Tiles[tile.Index] != tile)
				{
					// The surface was removed meanwhile.
					continue;
				}

				var local = new List<CarveShape>();
				Vector3 tileMin = Vector3.TransformPosition(tile.LayerData.BoundsMin, surface.LocalToWorld);
				Vector3 tileMax = Vector3.TransformPosition(tile.LayerData.BoundsMax, surface.LocalToWorld);
				// Up by the agent height too: a shape above the floor still blocks the agents standing on it.
				Vector3 wmin = Vector3.ComponentMin(tileMin, tileMax) - new Vector3(1f);
				Vector3 wmax = Vector3.ComponentMax(tileMin, tileMax) + new Vector3(1f, 1f + surface.Data.Settings.AgentHeight, 1f);
				foreach (ObstacleShape shape in shapes)
				{
					if (!NavMath.OverlapBounds(wmin, wmax, shape.BoundsMin, shape.BoundsMax))
					{
						continue;
					}

					local.Add(ToLocal(shape, surface));
				}

				if (local.Count == 0)
				{
					if (tile.Carved)
					{
						world.ReplaceTile(tile, surface.Data.Tiles[tile.Index], false);
					}

					continue;
				}

				work.Add((tile, local));
			}

			if (work.Count == 0)
			{
				return;
			}

			jobWorldVersion = world.Version;
			job = Task.Run(() =>
			{
				var results = new (NavTile tile, NavMeshTileData data, bool carved)[work.Count];
				Parallel.For(0, work.Count, i =>
				{
					(NavTile tile, List<CarveShape> local) = work[i];
					NavMeshTileData data = NavMeshBuilder.RebuildTile(tile.Surface.Data, tile.LayerData, local);
					results[i] = (tile, data, true);
				});
				return new List<(NavTile, NavMeshTileData, bool)>(results);
			});
		}

		private static CarveShape ToLocal(in ObstacleShape shape, NavSurface surface)
		{
			Matrix4 m = surface.WorldToLocal;
			Vector3 center = Vector3.TransformPosition(shape.WorldCenter, m);
			Vector3 ax = Vector3.TransformVector(new Vector3(shape.AxisX.X, 0f, shape.AxisX.Y), m);
			Vector3 az = Vector3.TransformVector(new Vector3(shape.AxisZ.X, 0f, shape.AxisZ.Y), m);
			float scaleX = ax.Length;
			float scaleZ = az.Length;
			Vector2 axisX = scaleX > 1e-6f ? new Vector2(ax.X, ax.Z) / scaleX : Vector2.UnitX;
			Vector2 axisZ = scaleZ > 1e-6f ? new Vector2(az.X, az.Z) / scaleZ : Vector2.UnitY;
			if (axisX.LengthSquared > 1e-8f) axisX.Normalize();
			if (axisZ.LengthSquared > 1e-8f) axisZ.Normalize();
			float scaleY = Vector3.TransformVector(Vector3.UnitY, m).Length;
			Vector3 half = new Vector3(shape.HalfExtents.X * scaleX, shape.HalfExtents.Y * scaleY, shape.HalfExtents.Z * scaleZ);
			return new CarveShape
			{
				IsCylinder = shape.IsCylinder,
				Center = center,
				Radius = shape.Radius * Math.Max(scaleX, scaleZ),
				HalfExtents = half,
				AxisX = axisX,
				AxisZ = axisZ
			};
		}

		#endregion
	}
}
