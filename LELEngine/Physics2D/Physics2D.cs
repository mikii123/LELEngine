using System;
using System.Collections.Generic;
using Box2D.NET;
using OpenTK.Mathematics;
using static Box2D.NET.B2Distances;
using B2Vector2 = Box2D.NET.B2Vec2;

namespace LELEngine
{
	public enum SimulationMode2D
	{
		/// <summary>Physics steps run with FixedUpdate (the default).</summary>
		FixedUpdate,

		/// <summary>One step per frame with the frame's delta time.</summary>
		Update,

		/// <summary>Steps only when <see cref="Physics2D.Simulate" /> is called.</summary>
		Script
	}

	/// <summary>
	///     Global 2D physics API (Unity's Physics2D): gravity and settings of the project (<see cref="ProjectSettings.Physics2D" />),
	///     layer collision control, and queries into the active scene's world (raycasts, overlaps, casts). Queries use
	///     the QueriesHitTriggers / QueriesStartInColliders settings unless a <see cref="ContactFilter2D" /> says otherwise.
	///     Positions and directions are 2D coordinates in the simulation plane (XY or XZ).
	/// </summary>
	public static class Physics2D
	{
		#region PublicFields

		/// <summary>All layers: the default layer mask of queries.</summary>
		public const int AllLayers = -1;

		public const int DefaultRaycastLayers = -1;
		public const int IgnoreRaycastLayer = 1 << 2;

		/// <summary>When physics steps run (see <see cref="Simulate" /> for manual stepping).</summary>
		public static SimulationMode2D simulationMode = SimulationMode2D.FixedUpdate;

		public static Vector2 gravity
		{
			get => Settings?.Gravity ?? new Vector2(0f, -9.81f);
			set
			{
				if (Settings != null)
				{
					Settings.Gravity = value;
				}
			}
		}

		/// <summary>Box2D v3 solver sub-steps per physics step (what the v3 solver uses instead of iterations).</summary>
		public static int subStepCount
		{
			get => Settings?.SubStepCount ?? 4;
			set
			{
				if (Settings != null) Settings.SubStepCount = Math.Clamp(value, 1, 16);
			}
		}

		public static bool queriesHitTriggers
		{
			get => Settings?.QueriesHitTriggers ?? true;
			set
			{
				if (Settings != null) Settings.QueriesHitTriggers = value;
			}
		}

		public static bool queriesStartInColliders
		{
			get => Settings?.QueriesStartInColliders ?? true;
			set
			{
				if (Settings != null) Settings.QueriesStartInColliders = value;
			}
		}

		#endregion

		#region PrivateFields

		private static readonly List<RaycastHit2D> hitBuffer = new List<RaycastHit2D>();
		private static readonly List<Collider2D> colliderBuffer = new List<Collider2D>();

		private static Physics2DSettings Settings => ProjectSettings.Current.Physics2D;

		/// <summary>The active scene's world (created on demand: queries on a scene without physics return nothing).</summary>
		private static PhysicsScene2D World => SceneManager.ActiveScene?.Physics2DIfCreated;

		#endregion

		#region PublicMethods

		/// <summary>Steps the active scene's world by hand (<see cref="SimulationMode2D.Script" />).</summary>
		public static bool Simulate(float step)
		{
			Scene scene = SceneManager.ActiveScene;
			if (scene == null || !scene.IsPlaying || step <= 0f)
			{
				return false;
			}

			scene.Physics2D.Step(step);
			return true;
		}

		// ---------------------------------------------------------------- collision control

		public static void IgnoreLayerCollision(int layer1, int layer2, bool ignore = true)
		{
			Settings?.IgnoreLayerCollision(layer1, layer2, ignore);
		}

		public static bool GetIgnoreLayerCollision(int layer1, int layer2)
		{
			return Settings?.GetIgnoreLayerCollision(layer1, layer2) ?? false;
		}

		/// <summary>The mask of layers a layer collides with.</summary>
		public static int GetLayerCollisionMask(int layer)
		{
			return Settings?.GetLayerCollisionMask(layer) ?? -1;
		}

		public static void IgnoreCollision(Collider2D collider1, Collider2D collider2, bool ignore = true)
		{
			PhysicsScene2D world = collider1?.gameObject?.scene?.Physics2D ?? World;
			world?.IgnoreCollision(collider1, collider2, ignore);
		}

		public static bool GetIgnoreCollision(Collider2D collider1, Collider2D collider2)
		{
			PhysicsScene2D world = collider1?.gameObject?.scene?.Physics2DIfCreated ?? World;
			return world != null && world.GetIgnoreCollision(collider1, collider2);
		}

		// ---------------------------------------------------------------- raycasts

		public static RaycastHit2D Raycast(Vector2 origin, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return default;
			}

			world.RaycastAll(origin, direction, distance, layerMask, null, hitBuffer, true);
			return hitBuffer.Count > 0 ? hitBuffer[0] : default;
		}

		public static int Raycast(Vector2 origin, Vector2 direction, ContactFilter2D filter, List<RaycastHit2D> results, float distance = float.PositiveInfinity)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				results.Clear();
				return 0;
			}

			return world.RaycastAll(origin, direction, distance, filter.useLayerMask ? filter.layerMask.value : AllLayers, filter.useTriggers, results, false);
		}

		public static RaycastHit2D[] RaycastAll(Vector2 origin, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<RaycastHit2D>();
			}

			world.RaycastAll(origin, direction, distance, layerMask, null, hitBuffer, false);
			return hitBuffer.ToArray();
		}

		public static int RaycastNonAlloc(Vector2 origin, Vector2 direction, RaycastHit2D[] results, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null || results == null)
			{
				return 0;
			}

			world.RaycastAll(origin, direction, distance, layerMask, null, hitBuffer, false);
			int count = Math.Min(results.Length, hitBuffer.Count);
			hitBuffer.CopyTo(0, results, 0, count);
			return count;
		}

		public static RaycastHit2D Linecast(Vector2 start, Vector2 end, int layerMask = DefaultRaycastLayers)
		{
			Vector2 delta = end - start;
			return Raycast(start, delta, delta.Length, layerMask);
		}

		public static RaycastHit2D[] LinecastAll(Vector2 start, Vector2 end, int layerMask = DefaultRaycastLayers)
		{
			Vector2 delta = end - start;
			return RaycastAll(start, delta, delta.Length, layerMask);
		}

		// ---------------------------------------------------------------- overlaps

		public static Collider2D OverlapPoint(Vector2 point, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return null;
			}

			world.OverlapPointAll(point, layerMask, null, colliderBuffer);
			return colliderBuffer.Count > 0 ? colliderBuffer[0] : null;
		}

		public static Collider2D[] OverlapPointAll(Vector2 point, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<Collider2D>();
			}

			world.OverlapPointAll(point, layerMask, null, colliderBuffer);
			return colliderBuffer.ToArray();
		}

		public static int OverlapPoint(Vector2 point, ContactFilter2D filter, List<Collider2D> results)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				results.Clear();
				return 0;
			}

			return world.OverlapPointAll(point, filter.useLayerMask ? filter.layerMask.value : AllLayers, filter.useTriggers, results);
		}

		public static Collider2D OverlapCircle(Vector2 point, float radius, int layerMask = DefaultRaycastLayers)
		{
			return First(OverlapCircleAll(point, radius, layerMask));
		}

		public static Collider2D[] OverlapCircleAll(Vector2 point, float radius, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<Collider2D>();
			}

			world.OverlapShapeAll(Circle(point, radius), layerMask, null, colliderBuffer);
			return colliderBuffer.ToArray();
		}

		public static int OverlapCircle(Vector2 point, float radius, ContactFilter2D filter, List<Collider2D> results)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				results.Clear();
				return 0;
			}

			return world.OverlapShapeAll(Circle(point, radius), filter.useLayerMask ? filter.layerMask.value : AllLayers, filter.useTriggers, results);
		}

		public static Collider2D OverlapBox(Vector2 point, Vector2 size, float angle, int layerMask = DefaultRaycastLayers)
		{
			return First(OverlapBoxAll(point, size, angle, layerMask));
		}

		public static Collider2D[] OverlapBoxAll(Vector2 point, Vector2 size, float angle, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<Collider2D>();
			}

			world.OverlapShapeAll(Box(point, size, angle), layerMask, null, colliderBuffer);
			return colliderBuffer.ToArray();
		}

		public static int OverlapBox(Vector2 point, Vector2 size, float angle, ContactFilter2D filter, List<Collider2D> results)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				results.Clear();
				return 0;
			}

			return world.OverlapShapeAll(Box(point, size, angle), filter.useLayerMask ? filter.layerMask.value : AllLayers, filter.useTriggers, results);
		}

		/// <summary>Colliders inside the axis-aligned rectangle spanned by two corners.</summary>
		public static Collider2D OverlapArea(Vector2 pointA, Vector2 pointB, int layerMask = DefaultRaycastLayers)
		{
			return First(OverlapAreaAll(pointA, pointB, layerMask));
		}

		public static Collider2D[] OverlapAreaAll(Vector2 pointA, Vector2 pointB, int layerMask = DefaultRaycastLayers)
		{
			Vector2 center = (pointA + pointB) * 0.5f;
			Vector2 size = new Vector2(Math.Abs(pointA.X - pointB.X), Math.Abs(pointA.Y - pointB.Y));
			return OverlapBoxAll(center, size, 0f, layerMask);
		}

		// ---------------------------------------------------------------- shape casts

		public static RaycastHit2D CircleCast(Vector2 origin, float radius, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return default;
			}

			world.ShapeCastAll(Circle(origin, radius), direction, distance, layerMask, null, hitBuffer, true);
			return hitBuffer.Count > 0 ? hitBuffer[0] : default;
		}

		public static RaycastHit2D[] CircleCastAll(Vector2 origin, float radius, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<RaycastHit2D>();
			}

			world.ShapeCastAll(Circle(origin, radius), direction, distance, layerMask, null, hitBuffer, false);
			return hitBuffer.ToArray();
		}

		public static RaycastHit2D BoxCast(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return default;
			}

			world.ShapeCastAll(Box(origin, size, angle), direction, distance, layerMask, null, hitBuffer, true);
			return hitBuffer.Count > 0 ? hitBuffer[0] : default;
		}

		public static RaycastHit2D[] BoxCastAll(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<RaycastHit2D>();
			}

			world.ShapeCastAll(Box(origin, size, angle), direction, distance, layerMask, null, hitBuffer, false);
			return hitBuffer.ToArray();
		}

		#endregion

		#region PrivateMethods

		private static Collider2D First(Collider2D[] colliders)
		{
			return colliders.Length > 0 ? colliders[0] : null;
		}

		/// <summary>A circle at a world point, as a query shape.</summary>
		private static B2ShapeProxy Circle(Vector2 center, float radius)
		{
			return b2MakeProxy(Physics2DMath.ToB2(center), 1, Math.Max(0.0001f, radius));
		}

		/// <summary>A box at a world point turned by an angle in degrees, as a query shape.</summary>
		private static B2ShapeProxy Box(Vector2 center, Vector2 size, float angle)
		{
			float hx = Math.Max(0.0001f, size.X * 0.5f);
			float hy = Math.Max(0.0001f, size.Y * 0.5f);
			float radians = angle * Physics2DMath.Deg2Rad;
			float c = MathF.Cos(radians);
			float s = MathF.Sin(radians);
			Span<B2Vector2> corners = stackalloc B2Vector2[4];
			corners[0] = Corner(center, -hx, -hy, c, s);
			corners[1] = Corner(center, hx, -hy, c, s);
			corners[2] = Corner(center, hx, hy, c, s);
			corners[3] = Corner(center, -hx, hy, c, s);
			return b2MakeProxy(corners, 4, 0f);
		}

		private static B2Vector2 Corner(Vector2 center, float x, float y, float c, float s)
		{
			return new B2Vector2(center.X + c * x - s * y, center.Y + s * x + c * y);
		}

		#endregion
	}
}
