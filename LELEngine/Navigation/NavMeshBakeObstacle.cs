using System;
using System.Collections.Generic;
using LELEngine.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>
	///     An obstacle only navmesh bakes see: its box or cylinder is cut out of the navmesh, with the agent's radius kept
	///     off it as off solid geometry, and nothing else happens. No collider, no carving while the game runs, no cost
	///     at all outside a bake: for places agents should keep away from that physics does not block (the space in
	///     front of a gate, a lane kept clear). A scene's bake (<see cref="SceneNavigation" />) and a
	///     <see cref="NavMeshSurface" /> both read it; with the Physics Colliders geometry its footprint cuts the
	///     simulation plane whatever its height.
	/// </summary>
	[ExecuteAlways]
	[InspectorColor(InspectorColorKind.Navigation)]
	public sealed class NavMeshBakeObstacle : Behaviour
	{
		#region PublicFields

		public NavMeshObstacleShape shape = NavMeshObstacleShape.Box;

		[Tooltip("Center of the shape in the object's local space.")]
		public Vector3 center;

		[Tooltip("Box size in local space.")]
		public Vector3 size = Vector3.One;

		[Tooltip("Cylinder radius in local space (Capsule: an upright cylinder).")]
		public float radius = 0.5f;

		[Tooltip("Cylinder height in local space.")]
		public float height = 2f;

		[Tooltip("The agent type it is cut out for (Project Settings > Navigation); -1: every agent type.")]
		public int affectedAgentTypeId = -1;

		/// <summary>The enabled bake obstacles of every loaded scene (edit mode too: the scene view draws them).</summary>
		public static IReadOnlyList<NavMeshBakeObstacle> All => all;

		#endregion

		#region PrivateFields

		private static readonly List<NavMeshBakeObstacle> all = new List<NavMeshBakeObstacle>();

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			if (!all.Contains(this))
			{
				all.Add(this);
			}
		}

		public override void OnDisable()
		{
			all.Remove(this);
		}

		protected internal override void ReleaseResources()
		{
			all.Remove(this);
		}

		/// <summary>Added in the editor: the shape fits the object's mesh, if it has one.</summary>
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
		}

		#endregion

		#region PublicMethods

		/// <summary>True when it is cut out of the navmesh of <paramref name="agentTypeId" />.</summary>
		public bool Affects(int agentTypeId)
		{
			return affectedAgentTypeId < 0 || affectedAgentTypeId == agentTypeId;
		}

		/// <summary>World radius and half height of the cylinder (at least as tall as it is wide, as a NavMesh Obstacle's).</summary>
		public void GetWorldCylinder(out Vector3 worldCenter, out float worldRadius, out float halfHeight)
		{
			Vector3 scale = transform.lossyScale;
			worldCenter = transform.TransformPoint(center);
			worldRadius = Math.Abs(radius) * Math.Max(Math.Abs(scale.X), Math.Abs(scale.Z));
			halfHeight = Math.Max(Math.Abs(height * scale.Y), worldRadius * 2f) * 0.5f;
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     Its footprint in a surface's space as an obstacle volume: the shape's outline seen from the surface's up,
		///     widened by <paramref name="margin" />; its height range, or <paramref name="minY" />..<paramref name="maxY" />
		///     when <paramref name="throughPlane" /> (it cuts the plane whatever its height). False when it has no area.
		/// </summary>
		internal bool GetFootprint(in Matrix4 worldToSurface, float margin, float tolerance, bool throughPlane, float minY, float maxY, out NavMeshAreaVolume volume)
		{
			volume = default;
			if (transform == null)
			{
				return false;
			}

			var points = new List<Vector2>(8);
			float low = float.MaxValue;
			float high = float.MinValue;
			float rounding = margin;
			if (shape == NavMeshObstacleShape.Box)
			{
				Matrix4 toSurface = transform.LocalToWorld * worldToSurface;
				Vector3 half = size * 0.5f;
				for (int i = 0; i < 8; i++)
				{
					Vector3 corner = center + new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z);
					Vector3 p = Vector3.TransformPosition(corner, toSurface);
					points.Add(new Vector2(p.X, p.Z));
					low = Math.Min(low, p.Y);
					high = Math.Max(high, p.Y);
				}
			}
			else
			{
				GetWorldCylinder(out Vector3 worldCenter, out float worldRadius, out float halfHeight);
				Vector3 up = transform.up * (halfHeight - worldRadius > 0f ? halfHeight - worldRadius : 0f);
				foreach (Vector3 end in new[] { worldCenter - up, worldCenter + up })
				{
					Vector3 p = Vector3.TransformPosition(end, worldToSurface);
					points.Add(new Vector2(p.X, p.Z));
				}

				Vector3 c = Vector3.TransformPosition(worldCenter, worldToSurface);
				low = c.Y - halfHeight;
				high = c.Y + halfHeight;
				rounding += worldRadius;
			}

			List<Vector2> hull = NavMeshShapes.ConvexHull(points);
			Vector2[] polygon = NavMeshShapes.Rounded(hull, rounding, tolerance);
			if (polygon.Length < 3)
			{
				return false;
			}

			volume = new NavMeshAreaVolume
			{
				Polygon = polygon,
				MinY = throughPlane ? minY : low,
				MaxY = throughPlane ? maxY : high,
				Area = NavMeshArea.NotWalkable,
				Obstacle = true
			};
			return true;
		}

		#endregion
	}
}
