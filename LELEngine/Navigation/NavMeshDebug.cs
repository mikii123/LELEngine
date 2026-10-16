using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>Draws the navigation state with <see cref="DebugDraw" />: navmesh polygons, agents and obstacles.</summary>
	public static class NavMeshDebug
	{
		#region PublicFields

		/// <summary>Lift of the drawn polygons above the navmesh, so they do not fight the floor.</summary>
		public static float YOffset = 0.02f;

		public static Color4 WalkableFill = new Color4(0.1f, 0.55f, 1f, 0.2f);
		public static Color4 EdgeColor = new Color4(0.05f, 0.2f, 0.45f, 0.5f);
		public static Color4 BoundaryColor = new Color4(0.05f, 0.2f, 0.6f, 1f);
		public static Color4 AgentColor = new Color4(0.2f, 1f, 0.3f, 1f);
		public static Color4 PathColor = new Color4(1f, 0.9f, 0.2f, 1f);
		public static Color4 ObstacleColor = new Color4(1f, 0.3f, 0.2f, 1f);

		#endregion

		#region PublicMethods

		/// <summary>Everything: surfaces, agents, obstacles.</summary>
		public static void DrawAll()
		{
			DrawSurfaces();
			DrawAgents();
			DrawObstacles();
		}

		public static void DrawSurfaces()
		{
			Span<Vector3> verts = stackalloc Vector3[NavMeshQuery.MaxPolyVerts];
			foreach (NavSurface surface in NavMesh.World.Surfaces)
			{
				if (surface == null)
				{
					continue;
				}

				foreach (NavTile tile in surface.Tiles)
				{
					DrawTile(tile, verts);
				}
			}
		}

		public static void DrawAgents()
		{
			foreach (NavMeshAgent agent in NavMesh.Crowd.Agents)
			{
				DrawAgent(agent);
			}
		}

		public static void DrawAgent(NavMeshAgent agent)
		{
			if (agent == null)
			{
				return;
			}

			Vector3 pos = agent.Position + new Vector3(0f, YOffset, 0f);
			Color4 color = agent.isOnNavMesh ? AgentColor : ObstacleColor;
			DebugDraw.Circle(pos, agent.radius, color);
			DebugDraw.Line(pos, pos + new Vector3(0f, agent.height, 0f), color);
			if (agent.VelocityInternal.LengthSquared > 0.001f)
			{
				DebugDraw.Arrow(pos, pos + agent.VelocityInternal * 0.5f, color);
			}

			if (agent.hasPath && agent.CornerCount > 0)
			{
				Vector3 previous = pos;
				for (int i = 0; i < agent.CornerCount; i++)
				{
					Vector3 corner = agent.CornerVerts[i] + new Vector3(0f, YOffset, 0f);
					DebugDraw.Line(previous, corner, PathColor);
					previous = corner;
				}
			}

			if (agent.hasPath || agent.pathPending)
			{
				Vector3 target = agent.TargetPos + new Vector3(0f, YOffset, 0f);
				DebugDraw.Circle(target, 0.15f, PathColor, 12);
			}
		}

		public static void DrawObstacles()
		{
			foreach (NavMeshObstacle obstacle in NavMesh.Carver.Obstacles)
			{
				DrawObstacle(obstacle);
			}
		}

		public static void DrawObstacle(NavMeshObstacle obstacle)
		{
			if (obstacle == null || obstacle.transform == null)
			{
				return;
			}

			obstacle.GetWorldShape(out Vector3 center, out Vector2 ax, out Vector2 az, out Vector3 half, out float radius);
			Color4 color = obstacle.Carved ? ObstacleColor : new Color4(ObstacleColor.R, ObstacleColor.G, ObstacleColor.B, 0.5f);
			if (obstacle.shape == NavMeshObstacleShape.Capsule)
			{
				DebugDraw.Cylinder(center - new Vector3(0f, half.Y, 0f), radius, half.Y * 2f, color);
			}
			else
			{
				float yaw = (float)Math.Atan2(ax.Y, ax.X);
				Quaternion rotation = Quaternion.FromAxisAngle(Vector3.UnitY, -yaw);
				DebugDraw.WireBox(center, half, rotation, color);
			}
		}

		/// <summary>The volume of a surface (Collect Objects = Volume) and its bounds when baked.</summary>
		public static void DrawSurfaceBounds(NavMeshSurface surface)
		{
			if (surface == null || surface.transform == null)
			{
				return;
			}

			Transform t = surface.transform;
			Matrix4 toWorld = surface.SurfaceToWorld;
			if (surface.collectObjects == CollectObjects.Volume)
			{
				Vector3 center = Vector3.TransformPosition(surface.center, toWorld);
				DebugDraw.WireBox(center, surface.size * 0.5f, t.rotation, new Color4(0.4f, 0.8f, 1f, 0.8f));
			}

			NavMeshData data = surface.NavMeshData;
			if (data != null && !data.IsEmpty)
			{
				Vector3 center = Vector3.TransformPosition((data.BoundsMin + data.BoundsMax) * 0.5f, toWorld);
				DebugDraw.WireBox(center, (data.BoundsMax - data.BoundsMin) * 0.5f, t.rotation, new Color4(0.3f, 0.5f, 0.9f, 0.5f));
			}
		}

		#endregion

		#region PrivateMethods

		private static void DrawTile(NavTile tile, Span<Vector3> verts)
		{
			Vector3 lift = new Vector3(0f, YOffset, 0f);
			for (int p = 0; p < tile.PolyCount; p++)
			{
				int n = tile.GetPolyVerts(p, verts);
				for (int i = 0; i < n; i++)
				{
					verts[i] += lift;
				}

				int area = tile.Area(p);
				Color4 color = area == NavMeshArea.Walkable ? WalkableFill : AreaColor(area);
				DebugDraw.Polygon(verts.Slice(0, n), color);
				for (int i = 0; i < n; i++)
				{
					bool boundary = tile.Neighbor(p, i) < 0;
					DebugDraw.Line(verts[i], verts[(i + 1) % n], boundary ? BoundaryColor : EdgeColor);
				}
			}
		}

		private static Color4 AreaColor(int area)
		{
			float hue = (area * 0.618034f) % 1f;
			float r = Math.Abs(hue * 6f - 3f) - 1f;
			float g = 2f - Math.Abs(hue * 6f - 2f);
			float b = 2f - Math.Abs(hue * 6f - 4f);
			return new Color4(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), 0.35f);
		}

		#endregion
	}
}
