using System;
using System.Collections.Generic;
using Box2DSharp.Collision.Shapes;
using OpenTK.Mathematics;
using B2Transform = Box2DSharp.Common.Transform;
using B2Vector2 = System.Numerics.Vector2;

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
	///     Global 2D physics API (Unity's Physics2D): gravity and settings of the active scene, layer collision
	///     control, and queries into the active scene's world (raycasts, overlaps, casts). Queries use the scene's
	///     QueriesHitTriggers / QueriesStartInColliders settings unless a <see cref="ContactFilter2D" /> says otherwise.
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

		public static int velocityIterations
		{
			get => Settings?.VelocityIterations ?? 8;
			set
			{
				if (Settings != null) Settings.VelocityIterations = Math.Max(1, value);
			}
		}

		public static int positionIterations
		{
			get => Settings?.PositionIterations ?? 3;
			set
			{
				if (Settings != null) Settings.PositionIterations = Math.Max(1, value);
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

		private static Physics2DSettings Settings => SceneManager.ActiveScene?.Settings.Physics2D;

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

			world.OverlapShapeAll(Circle(radius), new B2Transform(Physics2DMath.ToB2(point), 0f), layerMask, null, colliderBuffer);
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

			return world.OverlapShapeAll(Circle(radius), new B2Transform(Physics2DMath.ToB2(point), 0f), filter.useLayerMask ? filter.layerMask.value : AllLayers, filter.useTriggers, results);
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

			world.OverlapShapeAll(Box(size), new B2Transform(Physics2DMath.ToB2(point), angle * Physics2DMath.Deg2Rad), layerMask, null, colliderBuffer);
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

			return world.OverlapShapeAll(Box(size), new B2Transform(Physics2DMath.ToB2(point), angle * Physics2DMath.Deg2Rad), filter.useLayerMask ? filter.layerMask.value : AllLayers, filter.useTriggers, results);
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

			world.ShapeCastAll(Circle(radius), new B2Transform(Physics2DMath.ToB2(origin), 0f), direction, distance, layerMask, null, hitBuffer, true);
			return hitBuffer.Count > 0 ? hitBuffer[0] : default;
		}

		public static RaycastHit2D[] CircleCastAll(Vector2 origin, float radius, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<RaycastHit2D>();
			}

			world.ShapeCastAll(Circle(radius), new B2Transform(Physics2DMath.ToB2(origin), 0f), direction, distance, layerMask, null, hitBuffer, false);
			return hitBuffer.ToArray();
		}

		public static RaycastHit2D BoxCast(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return default;
			}

			world.ShapeCastAll(Box(size), new B2Transform(Physics2DMath.ToB2(origin), angle * Physics2DMath.Deg2Rad), direction, distance, layerMask, null, hitBuffer, true);
			return hitBuffer.Count > 0 ? hitBuffer[0] : default;
		}

		public static RaycastHit2D[] BoxCastAll(Vector2 origin, Vector2 size, float angle, Vector2 direction, float distance = float.PositiveInfinity, int layerMask = DefaultRaycastLayers)
		{
			PhysicsScene2D world = World;
			if (world == null)
			{
				return Array.Empty<RaycastHit2D>();
			}

			world.ShapeCastAll(Box(size), new B2Transform(Physics2DMath.ToB2(origin), angle * Physics2DMath.Deg2Rad), direction, distance, layerMask, null, hitBuffer, false);
			return hitBuffer.ToArray();
		}

		#endregion

		#region PrivateMethods

		private static Collider2D First(Collider2D[] colliders)
		{
			return colliders.Length > 0 ? colliders[0] : null;
		}

		private static CircleShape Circle(float radius)
		{
			return new CircleShape { Position = B2Vector2.Zero, Radius = Math.Max(0.0001f, radius) };
		}

		private static PolygonShape Box(Vector2 size)
		{
			var box = new PolygonShape();
			box.SetAsBox(Math.Max(0.0001f, size.X * 0.5f), Math.Max(0.0001f, size.Y * 0.5f));
			return box;
		}

		#endregion
	}
}
