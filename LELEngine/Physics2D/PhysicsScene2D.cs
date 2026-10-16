using System;
using System.Collections.Generic;
using Box2D.NET;
using OpenTK.Mathematics;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Contacts;
using static Box2D.NET.B2Distances;
using static Box2D.NET.B2Joints;
using static Box2D.NET.B2MathFunction;
using static Box2D.NET.B2Shapes;
using static Box2D.NET.B2Types;
using static Box2D.NET.B2Worlds;
using B2Vector2 = Box2D.NET.B2Vec2;

namespace LELEngine
{
	/// <summary>
	///     The 2D physics world of a scene (Box2D v3 through Box2D.NET): bodies of the scene's <see cref="Rigidbody2D" />
	///     components, shapes of its <see cref="Collider2D" /> components (static bodies for colliders without a
	///     rigidbody) and its <see cref="Joint2D" /> constraints. Stepped from the scene's fixed update; writes the
	///     body poses back to the transforms, delivers OnCollision / OnTrigger callbacks (from Box2D's contact and
	///     sensor events, read after the step) and answers the queries of <see cref="Physics2D" />. Colliders of child
	///     objects attach to the nearest ancestor rigidbody, as in Unity. Layers become shape filter bits (category =
	///     the layer, mask = its row of the collision matrix); pairs ignored with IgnoreCollision go through Box2D's
	///     custom filter.
	/// </summary>
	public sealed class PhysicsScene2D : IDisposable
	{
		#region PublicFields

		public Scene Scene { get; }
		public int bodyCount => bodies.Count;
		public int colliderCount => colliders.Count;
		public int jointCount => joints.Count;

		/// <summary>True while Box2D steps (components must not change the world then; changes are deferred).</summary>
		public bool IsStepping { get; private set; }

		#endregion

		#region InternalFields

		internal B2WorldId World { get; }

		/// <summary>The world plane of this simulation (the project's setting when the world was created).</summary>
		internal PlaneBasis Plane { get; }

		/// <summary>Static body at the origin: joints without a connected body attach here; colliders without a rigidbody use their own static bodies.</summary>
		internal B2BodyId GroundBody { get; }

		internal float LastStepDeltaTime { get; private set; } = 0.02f;

		#endregion

		#region PrivateFields

		private struct ContactEvent
		{
			public Collider2D A;
			public Collider2D B;
			public bool Begin;
			public bool Sensor;
			public ContactPoint2D[] Points;
			public Vector2 VelocityA;
			public Vector2 VelocityB;
		}

		private readonly List<Rigidbody2D> bodies = new List<Rigidbody2D>();
		private readonly List<Collider2D> colliders = new List<Collider2D>();
		private readonly List<Joint2D> joints = new List<Joint2D>();
		private readonly List<ContactEvent> events = new List<ContactEvent>();
		private readonly HashSet<(ulong, ulong)> ignoredPairs = new HashSet<(ulong, ulong)>();
		private readonly List<Behaviour> callbackBuffer = new List<Behaviour>();
		private readonly int[] filteredMatrix = new int[32];
		private readonly b2CustomFilterFcn customFilter;
		private readonly QueryState query = new QueryState();
		private B2ContactData[] contactBuffer = new B2ContactData[16];
		private B2ShapeId[] visitorBuffer = new B2ShapeId[16];

		/// <summary>
		///     Colliders of shapes destroyed recently, by shape id: Box2D reports the end of their contacts and sensor
		///     overlaps after they are gone (the shape id is no longer valid then). Kept for one step.
		/// </summary>
		private readonly Dictionary<long, (Collider2D collider, int step)> destroyedShapes = new Dictionary<long, (Collider2D, int)>();

		private readonly List<long> expired = new List<long>();
		private int stepIndex;
		private Vector2 appliedGravity;
		private bool disposed;

		private static Physics2DSettings Settings => ProjectSettings.Current.Physics2D;

		private static readonly b2OverlapResultFcn overlapCallback = (shape, context) => ((QueryState)context).OnOverlap(shape);
		private static readonly b2CastResultFcn rayCallback = (shape, point, normal, fraction, context) => ((QueryState)context).OnCast(shape, point, normal, fraction);

		#endregion

		#region Constructors

		internal PhysicsScene2D(Scene scene)
		{
			Scene = scene;
			Plane = PlaneBasis.Of(Settings.SimulationPlane);
			B2WorldDef def = b2DefaultWorldDef();
			appliedGravity = Settings.Gravity;
			def.gravity = Physics2DMath.ToB2(appliedGravity);
			def.enableContinuous = true;
			int workers = PhysicsTaskSystem.Resolve(Settings.WorkerCount);
			if (workers > 1)
			{
				// Box2D splits its step over the shared worker threads; the world's own worker count travels as the context.
				PhysicsTaskSystem tasks = PhysicsTaskSystem.Get(workers);
				def.workerCount = workers;
				def.enqueueTask = tasks.Enqueue;
				def.finishTask = tasks.Finish;
				def.userTaskContext = workers;
			}

			World = b2CreateWorld(def);
			customFilter = CustomFilter;
			query.Scene = this;
			Array.Copy(MatrixOf(Settings), filteredMatrix, 32);

			B2BodyDef ground = b2DefaultBodyDef();
			ground.type = B2BodyType.b2_staticBody;
			GroundBody = b2CreateBody(World, ground);
		}

		#endregion

		#region PublicMethods

		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			foreach (Joint2D joint in joints)
			{
				joint.joint = default;
				joint.world = null;
			}

			foreach (Collider2D collider in colliders)
			{
				collider.shapes.Clear();
				collider.chains.Clear();
				collider.ownStaticBody = default;
				collider.body = default;
				collider.attachedRigidbody = null;
				collider.world = null;
			}

			foreach (Rigidbody2D body in bodies)
			{
				body.body = default;
				body.world = null;
			}

			joints.Clear();
			colliders.Clear();
			bodies.Clear();
			events.Clear();
			destroyedShapes.Clear();
			b2DestroyWorld(World);
		}

		// ---------------------------------------------------------------- registration

		internal void AddRigidbody(Rigidbody2D rigidbody)
		{
			if (disposed || rigidbody.hasBody || rigidbody.gameObject == null)
			{
				return;
			}

			Transform transform = rigidbody.transform;
			Vector2 position;
			float angle;
			Vector2 scale;
			Plane.PlanarPose(transform, null, out position, out angle, out scale);

			B2BodyDef def = b2DefaultBodyDef();
			def.type = ToBox2D(rigidbody.bodyType);
			def.position = Physics2DMath.ToB2(position);
			def.rotation = b2MakeRot(angle);
			def.linearDamping = rigidbody.linearDamping;
			def.angularDamping = rigidbody.angularDamping;
			def.gravityScale = rigidbody.gravityScale;
			def.enableSleep = rigidbody.sleepMode != RigidbodySleepMode2D.NeverSleep;
			def.isAwake = rigidbody.sleepMode != RigidbodySleepMode2D.StartAsleep;
			def.motionLocks = rigidbody.MotionLocks();
			def.isBullet = rigidbody.collisionDetectionMode == CollisionDetectionMode2D.Continuous;
			def.isEnabled = rigidbody.simulated;
			def.userData = B2UserData.Ref(rigidbody);

			rigidbody.body = b2CreateBody(World, def);
			rigidbody.world = this;
			rigidbody.OnBodyCreated(position, angle);
			bodies.Add(rigidbody);

			// Colliders of this object and its descendants that have no nearer rigidbody move to this body.
			foreach (Collider2D collider in colliders.ToArray())
			{
				if (collider.attachedRigidbody == null && FindOwner(collider) == rigidbody)
				{
					DetachCollider(collider);
					AttachCollider(collider);
				}
			}

			rigidbody.ApplyBodySettings();
			foreach (Joint2D joint in joints)
			{
				if (joint.attachedRigidbody == rigidbody || joint.connectedBody == rigidbody)
				{
					joint.dirty = true;
				}
			}
		}

		internal void RemoveRigidbody(Rigidbody2D rigidbody)
		{
			if (disposed || !rigidbody.hasBody)
			{
				return;
			}

			var owned = new List<Collider2D>();
			foreach (Collider2D collider in colliders)
			{
				if (collider.attachedRigidbody == rigidbody)
				{
					owned.Add(collider);
				}
			}

			foreach (Collider2D collider in owned)
			{
				DetachCollider(collider);
			}

			// Box2D destroys the joints on the body with it; they come back when both bodies exist again.
			foreach (Joint2D joint in joints)
			{
				if (joint.hasJoint && (joint.attachedRigidbody == rigidbody || joint.connectedBody == rigidbody))
				{
					b2DestroyJoint(joint.joint, true);
					joint.joint = default;
					joint.dirty = true;
				}
			}

			b2DestroyBody(rigidbody.body);
			rigidbody.body = default;
			rigidbody.world = null;
			bodies.Remove(rigidbody);

			foreach (Collider2D collider in owned)
			{
				if (colliders.Contains(collider))
				{
					AttachCollider(collider);
				}
			}
		}

		internal void AddCollider(Collider2D collider)
		{
			if (disposed || collider.world != null || collider.gameObject == null)
			{
				return;
			}

			collider.world = this;
			colliders.Add(collider);
			AttachCollider(collider);
		}

		internal void RemoveCollider(Collider2D collider)
		{
			if (disposed || collider.world != this)
			{
				return;
			}

			DetachCollider(collider);
			colliders.Remove(collider);
			collider.world = null;
		}

		/// <summary>Recreates a collider's shapes (its shape, offset, trigger flag or scale changed).</summary>
		internal void RebuildCollider(Collider2D collider)
		{
			if (disposed || collider.world != this)
			{
				return;
			}

			if (IsStepping)
			{
				collider.shapeDirty = true;
				return;
			}

			DetachCollider(collider);
			AttachCollider(collider);
		}

		internal void AddJoint(Joint2D joint)
		{
			if (disposed || joint.gameObject == null)
			{
				return;
			}

			if (!joints.Contains(joint))
			{
				joints.Add(joint);
			}

			joint.world = this;
			joint.dirty = true;
			if (!IsStepping)
			{
				CreateJointsIfReady();
			}
		}

		internal void RemoveJoint(Joint2D joint)
		{
			if (joint.hasJoint && !disposed && b2Joint_IsValid(joint.joint))
			{
				b2DestroyJoint(joint.joint, true);
			}

			joint.joint = default;
			joint.world = null;
			joints.Remove(joint);
		}

		/// <summary>The rigidbody a collider belongs to: the nearest one up the hierarchy, when its body exists.</summary>
		internal Rigidbody2D FindOwner(Collider2D collider)
		{
			for (Transform t = collider.transform; t != null; t = t.parent)
			{
				Rigidbody2D rigidbody = t.gameObject.GetComponent<Rigidbody2D>();
				if (rigidbody != null)
				{
					return rigidbody.hasBody ? rigidbody : null;
				}
			}

			return null;
		}

		// ---------------------------------------------------------------- stepping

		/// <summary>One physics step: pending changes, Box2D, transforms, callbacks.</summary>
		internal void Step(float deltaTime)
		{
			if (disposed || deltaTime <= 0f)
			{
				return;
			}

			LastStepDeltaTime = deltaTime;
			stepIndex++;
			ApplyPendingChanges();
			if (Settings.Gravity != appliedGravity)
			{
				appliedGravity = Settings.Gravity;
				b2World_SetGravity(World, Physics2DMath.ToB2(appliedGravity));
			}

			foreach (Rigidbody2D rigidbody in bodies)
			{
				rigidbody.BeforeStep(deltaTime);
			}

			IsStepping = true;
			try
			{
				b2World_Step(World, deltaTime, Math.Clamp(Settings.SubStepCount, 1, 16));
			}
			finally
			{
				IsStepping = false;
			}

			foreach (Rigidbody2D rigidbody in bodies)
			{
				rigidbody.AfterStep(deltaTime);
			}

			CollectEvents();
			DispatchEvents();
			BreakJoints();
			ForgetDestroyedShapes();
		}

		/// <summary>Moves interpolating bodies' transforms between the last two steps (every frame, after the fixed steps).</summary>
		internal void ApplyInterpolation(float alpha)
		{
			if (disposed)
			{
				return;
			}

			foreach (Rigidbody2D rigidbody in bodies)
			{
				if (rigidbody.interpolation != RigidbodyInterpolation2D.None && rigidbody.hasBody)
				{
					rigidbody.Interpolate(alpha);
				}
			}
		}

		// ---------------------------------------------------------------- collision filtering

		/// <summary>The shape filter of a layer: its bit as the category, its row of the collision matrix as the mask.</summary>
		internal B2Filter FilterFor(int layer)
		{
			return new B2Filter(1UL << (layer & 31), (ulong)(uint)filteredMatrix[layer & 31], 0);
		}

		internal void IgnoreCollision(Collider2D a, Collider2D b, bool ignore)
		{
			if (a == null || b == null)
			{
				return;
			}

			(ulong, ulong) key = PairKey(a, b);
			bool changed = ignore ? ignoredPairs.Add(key) : ignoredPairs.Remove(key);
			if (!changed || disposed)
			{
				return;
			}

			// The custom filter runs only while there is something to ignore.
			b2World_SetCustomFilterCallback(World, ignoredPairs.Count > 0 ? customFilter : null, this);

			// Box2D filters a pair when it starts overlapping: resetting the shapes' proxies drops their contacts so
			// the pair is filtered again.
			ResetProxies(a);
			ResetProxies(b);
		}

		internal bool GetIgnoreCollision(Collider2D a, Collider2D b)
		{
			return a != null && b != null && ignoredPairs.Contains(PairKey(a, b));
		}

		// ---------------------------------------------------------------- queries

		internal bool QueriesHitTriggers => Settings.QueriesHitTriggers;

		internal int RaycastAll(Vector2 origin, Vector2 direction, float distance, int layerMask, bool? hitTriggers, List<RaycastHit2D> results, bool closestOnly)
		{
			results.Clear();
			if (disposed)
			{
				return 0;
			}

			bool triggers = hitTriggers ?? Settings.QueriesHitTriggers;
			if (direction.LengthSquared < 1e-12f)
			{
				direction = Vector2.UnitX;
			}

			direction.Normalize();
			if (float.IsInfinity(distance) || float.IsNaN(distance) || distance > 1e5f)
			{
				distance = 1e5f;
			}

			query.BeginCast(layerMask, triggers, Settings.QueriesStartInColliders, results, closestOnly, distance, origin, direction, false);
			b2World_CastRay(World, Physics2DMath.ToB2(origin), Physics2DMath.ToB2(direction * distance), QueryFilter(layerMask), rayCallback, query);
			query.End();
			Finish(results, closestOnly);
			return results.Count;
		}

		internal int OverlapPointAll(Vector2 point, int layerMask, bool? hitTriggers, List<Collider2D> results)
		{
			results.Clear();
			if (disposed)
			{
				return 0;
			}

			B2Vector2 p = Physics2DMath.ToB2(point);
			var aabb = new B2AABB(new B2Vector2(p.X - 0.001f, p.Y - 0.001f), new B2Vector2(p.X + 0.001f, p.Y + 0.001f));
			query.BeginOverlap(layerMask, hitTriggers ?? Settings.QueriesHitTriggers, results, p, true);
			b2World_OverlapAABB(World, aabb, QueryFilter(layerMask), overlapCallback, query);
			query.End();
			return results.Count;
		}

		/// <summary>Colliders overlapping a convex shape (points and radius in world space).</summary>
		internal int OverlapShapeAll(B2ShapeProxy proxy, int layerMask, bool? hitTriggers, List<Collider2D> results)
		{
			results.Clear();
			if (disposed)
			{
				return 0;
			}

			query.BeginOverlap(layerMask, hitTriggers ?? Settings.QueriesHitTriggers, results, default, false);
			b2World_OverlapShape(World, ref proxy, QueryFilter(layerMask), overlapCallback, query);
			query.End();
			return results.Count;
		}

		/// <summary>Sweeps a convex shape (world space) along a direction and reports what it would hit (circle and box casts).</summary>
		internal int ShapeCastAll(B2ShapeProxy proxy, Vector2 direction, float distance, int layerMask, bool? hitTriggers, List<RaycastHit2D> results, bool closestOnly)
		{
			results.Clear();
			if (disposed)
			{
				return 0;
			}

			if (direction.LengthSquared < 1e-12f)
			{
				direction = Vector2.UnitX;
			}

			direction.Normalize();
			if (float.IsInfinity(distance) || float.IsNaN(distance) || distance > 1e5f)
			{
				distance = 1e5f;
			}

			Vector2 origin = Physics2DMath.ToEngine(ProxyCenter(proxy));
			query.BeginCast(layerMask, hitTriggers ?? Settings.QueriesHitTriggers, Settings.QueriesStartInColliders, results, closestOnly, distance, origin, direction, true);
			b2World_CastShape(World, ref proxy, Physics2DMath.ToB2(direction * distance), QueryFilter(layerMask), rayCallback, query);
			query.End();
			Finish(results, closestOnly);
			return results.Count;
		}

		/// <summary>Colliders currently touching the collider (non-sensor contacts that are touching).</summary>
		internal void GetTouching(Collider2D collider, List<Collider2D> results)
		{
			results.Clear();
			if (disposed)
			{
				return;
			}

			foreach (B2ShapeId shape in collider.shapes)
			{
				int count = ContactsOf(shape);
				for (int i = 0; i < count; i++)
				{
					ref B2ContactData data = ref contactBuffer[i];
					if (data.manifold.pointCount <= 0)
					{
						continue;
					}

					B2ShapeId other = B2ShapeEquals(data.shapeIdA, shape) ? data.shapeIdB : data.shapeIdA;
					Collider2D otherCollider = ColliderOf(other);
					if (otherCollider != null && otherCollider != collider && !results.Contains(otherCollider))
					{
						results.Add(otherCollider);
					}
				}
			}
		}

		/// <summary>A shape as a convex proxy in world space (for casts of a collider's own shapes).</summary>
		internal static B2ShapeProxy WorldProxy(B2ShapeId shape)
		{
			B2Transform xf = b2Body_GetTransform(b2Shape_GetBody(shape));
			switch (b2Shape_GetType(shape))
			{
				case B2ShapeType.b2_circleShape:
				{
					B2Circle circle = b2Shape_GetCircle(shape);
					return b2MakeProxy(b2TransformPoint(xf, circle.center), 1, circle.radius);
				}
				case B2ShapeType.b2_capsuleShape:
				{
					B2Capsule capsule = b2Shape_GetCapsule(shape);
					return b2MakeProxy(b2TransformPoint(xf, capsule.center1), b2TransformPoint(xf, capsule.center2), 2, capsule.radius);
				}
				case B2ShapeType.b2_polygonShape:
				{
					B2Polygon polygon = b2Shape_GetPolygon(shape);
					Span<B2Vector2> points = stackalloc B2Vector2[polygon.count];
					for (int i = 0; i < polygon.count; i++)
					{
						points[i] = b2TransformPoint(xf, polygon.vertices[i]);
					}

					return b2MakeProxy(points, polygon.count, polygon.radius);
				}
				case B2ShapeType.b2_chainSegmentShape:
				{
					B2Segment segment = b2Shape_GetChainSegment(shape).segment;
					return b2MakeProxy(b2TransformPoint(xf, segment.point1), b2TransformPoint(xf, segment.point2), 2, 0f);
				}
				default:
				{
					B2Segment segment = b2Shape_GetSegment(shape);
					return b2MakeProxy(b2TransformPoint(xf, segment.point1), b2TransformPoint(xf, segment.point2), 2, 0f);
				}
			}
		}

		#endregion

		#region InternalMethods

		internal static B2BodyType ToBox2D(RigidbodyType2D type)
		{
			switch (type)
			{
				case RigidbodyType2D.Kinematic: return B2BodyType.b2_kinematicBody;
				case RigidbodyType2D.Static: return B2BodyType.b2_staticBody;
				default: return B2BodyType.b2_dynamicBody;
			}
		}

		/// <summary>The collider of a shape, also for a shape destroyed since the last step (its end events).</summary>
		internal Collider2D ColliderOf(B2ShapeId shape)
		{
			if (b2Shape_IsValid(shape))
			{
				return b2Shape_GetUserData(shape).GetRef<Collider2D>();
			}

			return destroyedShapes.TryGetValue(ShapeKey(shape), out (Collider2D collider, int step) entry) ? entry.collider : null;
		}

		/// <summary>Called before a collider destroys its shapes: their contacts end after they are gone.</summary>
		internal void ShapesDestroyed(Collider2D collider)
		{
			foreach (B2ShapeId shape in collider.shapes)
			{
				destroyedShapes[ShapeKey(shape)] = (collider, stepIndex);
			}
		}

		#endregion

		#region PrivateMethods

		private static (ulong, ulong) PairKey(Collider2D a, Collider2D b)
		{
			return a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
		}

		private static long ShapeKey(B2ShapeId shape)
		{
			return ((long)shape.generation << 32) | (uint)shape.index1;
		}

		private static bool B2ShapeEquals(B2ShapeId a, B2ShapeId b)
		{
			return a.index1 == b.index1 && a.generation == b.generation && a.world0 == b.world0;
		}

		/// <summary>Queries filter by layer natively: the query's mask is the layer mask, its category accepts every shape mask.</summary>
		private static B2QueryFilter QueryFilter(int layerMask)
		{
			return new B2QueryFilter(ulong.MaxValue, (ulong)(uint)layerMask);
		}

		private static int[] MatrixOf(Physics2DSettings settings)
		{
			var matrix = new int[32];
			for (int i = 0; i < 32; i++)
			{
				matrix[i] = settings.GetLayerCollisionMask(i);
			}

			return matrix;
		}

		private static B2Vector2 ProxyCenter(in B2ShapeProxy proxy)
		{
			float x = 0f;
			float y = 0f;
			for (int i = 0; i < proxy.count; i++)
			{
				x += proxy.points[i].X;
				y += proxy.points[i].Y;
			}

			return new B2Vector2(x / Math.Max(1, proxy.count), y / Math.Max(1, proxy.count));
		}

		private static void Finish(List<RaycastHit2D> results, bool closestOnly)
		{
			if (results.Count > 1)
			{
				results.Sort((a, b) => a.distance.CompareTo(b.distance));
			}

			if (closestOnly && results.Count > 1)
			{
				results.RemoveRange(1, results.Count - 1);
			}
		}

		private bool CustomFilter(B2ShapeId shapeA, B2ShapeId shapeB, object context)
		{
			if (ignoredPairs.Count == 0)
			{
				return true;
			}

			Collider2D a = ColliderOf(shapeA);
			Collider2D b = ColliderOf(shapeB);
			return a == null || b == null || !ignoredPairs.Contains(PairKey(a, b));
		}

		/// <summary>Drops a collider's contacts so its pairs are filtered again (a filter change resets the broad-phase proxy).</summary>
		private static void ResetProxies(Collider2D collider)
		{
			foreach (B2ShapeId shape in collider.shapes)
			{
				if (!b2Shape_IsValid(shape))
				{
					continue;
				}

				B2Filter filter = b2Shape_GetFilter(shape);
				B2Filter changed = filter;
				changed.groupIndex = filter.groupIndex == 0 ? int.MinValue : 0;
				b2Shape_SetFilter(shape, changed);
				b2Shape_SetFilter(shape, filter);
			}
		}

		private void AttachCollider(Collider2D collider)
		{
			Rigidbody2D owner = FindOwner(collider);
			B2BodyId body;
			if (owner != null)
			{
				body = owner.body;
			}
			else
			{
				// A static body at the origin; the shapes are built in world space.
				B2BodyDef def = b2DefaultBodyDef();
				def.type = B2BodyType.b2_staticBody;
				def.userData = B2UserData.Ref(collider);
				body = b2CreateBody(World, def);
				collider.ownStaticBody = body;
			}

			collider.attachedRigidbody = owner;
			collider.body = body;
			collider.CreateShapes(body, owner?.transform);
			collider.shapeDirty = false;
			owner?.MassChanged();
		}

		private void DetachCollider(Collider2D collider)
		{
			ShapesDestroyed(collider);
			collider.DestroyShapes();
			if (collider.ownStaticBody.index1 != 0)
			{
				b2DestroyBody(collider.ownStaticBody);
				collider.ownStaticBody = default;
			}

			Rigidbody2D owner = collider.attachedRigidbody;
			collider.attachedRigidbody = null;
			collider.body = default;
			owner?.MassChanged();
		}

		private void ApplyPendingChanges()
		{
			// A changed layer collision matrix goes into every shape's filter.
			int[] matrix = MatrixOf(Settings);
			bool matrixChanged = false;
			for (int i = 0; i < 32; i++)
			{
				if (matrix[i] != filteredMatrix[i])
				{
					filteredMatrix[i] = matrix[i];
					matrixChanged = true;
				}
			}

			foreach (Collider2D collider in colliders.ToArray())
			{
				if (collider.shapeDirty || collider.StaticPoseChanged())
				{
					DetachCollider(collider);
					AttachCollider(collider);
				}
				else if (matrixChanged)
				{
					collider.Refilter();
				}
			}

			foreach (Rigidbody2D rigidbody in bodies)
			{
				rigidbody.SyncToBody();
			}

			CreateJointsIfReady();
			foreach (Joint2D joint in joints)
			{
				if (!joint.hasJoint)
				{
					continue;
				}

				if (joint.runtimeDirty)
				{
					joint.runtimeDirty = false;
					joint.ApplyRuntimeSettings();
				}

				if (joint.breakForce != joint.appliedBreakForce || joint.breakTorque != joint.appliedBreakTorque)
				{
					b2Joint_SetForceThreshold(joint.joint, Threshold(joint.breakForce));
					b2Joint_SetTorqueThreshold(joint.joint, Threshold(joint.breakTorque));
					joint.appliedBreakForce = joint.breakForce;
					joint.appliedBreakTorque = joint.breakTorque;
				}
			}
		}

		/// <summary>A break force or torque as a Box2D joint event threshold (infinity: never reported).</summary>
		private static float Threshold(float value)
		{
			return float.IsNaN(value) || value >= float.MaxValue ? float.MaxValue : Math.Max(0f, value);
		}

		private void CreateJointsIfReady()
		{
			foreach (Joint2D joint in joints.ToArray())
			{
				if (!joint.dirty && joint.hasJoint)
				{
					continue;
				}

				if (joint.hasJoint)
				{
					if (b2Joint_IsValid(joint.joint))
					{
						b2DestroyJoint(joint.joint, true);
					}

					joint.joint = default;
				}

				Rigidbody2D attached = joint.attachedRigidbody;
				if (attached == null || !attached.hasBody)
				{
					continue;
				}

				B2BodyId bodyA = attached.body;
				B2BodyId bodyB = joint.connectedBody != null ? joint.connectedBody.body : GroundBody;
				if (bodyB.index1 == 0 || bodyA == bodyB)
				{
					continue;
				}

				B2JointDef def = b2DefaultJointDef();
				def.bodyIdA = bodyA;
				def.bodyIdB = bodyB;
				def.collideConnected = joint.enableCollision;
				def.userData = B2UserData.Ref(joint);
				// Box2D reports the joint in its joint events once its force or torque reaches these (see BreakJoints).
				def.forceThreshold = Threshold(joint.breakForce);
				def.torqueThreshold = Threshold(joint.breakTorque);
				joint.appliedBreakForce = joint.breakForce;
				joint.appliedBreakTorque = joint.breakTorque;
				try
				{
					joint.joint = joint.CreateJoint(this, def);
				}
				catch (Exception e)
				{
					Debug.LogException(e, joint);
					joint.joint = default;
				}

				if (!joint.hasJoint)
				{
					continue;
				}

				joint.dirty = false;
				joint.runtimeDirty = false;
				joint.ApplyRuntimeSettings();
			}
		}

		/// <summary>Joints whose force or torque reached their break threshold in the step (Box2D's joint events) break.</summary>
		private void BreakJoints()
		{
			B2JointEvents jointEvents = b2World_GetJointEvents(World);
			if (jointEvents.count == 0)
			{
				return;
			}

			var broken = new List<Joint2D>();
			for (int i = 0; i < jointEvents.count; i++)
			{
				Joint2D joint = jointEvents.jointEvents[i].userData.GetRef<Joint2D>();
				if (joint != null && joint.hasJoint && joint.world == this && !broken.Contains(joint))
				{
					broken.Add(joint);
				}
			}

			foreach (Joint2D joint in broken)
			{
				GameObject go = joint.gameObject;
				RemoveJoint(joint);
				if (go != null)
				{
					Send(go, PhysicsCallbacks.Mask.JointBreak, component => component.OnJointBreak2D(joint));
				}

				GameObject.Destroy(joint);
			}
		}

		/// <summary>Box2D's contact and sensor events of the step become the pending OnCollision / OnTrigger events.</summary>
		private void CollectEvents()
		{
			B2ContactEvents contacts = b2World_GetContactEvents(World);
			for (int i = 0; i < contacts.beginCount; i++)
			{
				ref B2ContactBeginTouchEvent e = ref contacts.beginEvents[i];
				Collider2D a = ColliderOf(e.shapeIdA);
				Collider2D b = ColliderOf(e.shapeIdB);
				if (a == null || b == null)
				{
					continue;
				}

				var record = new ContactEvent { A = a, B = b, Begin = true, Points = Array.Empty<ContactPoint2D>(), VelocityA = VelocityOf(a), VelocityB = VelocityOf(b) };
				if ((PhysicsCallbacks.AnyHandles(a.gameObject, PhysicsCallbacks.Mask.CollisionEnter) || PhysicsCallbacks.AnyHandles(b.gameObject, PhysicsCallbacks.Mask.CollisionEnter))
				    && b2Contact_IsValid(e.contactId))
				{
					B2ContactData data = b2Contact_GetData(e.contactId);
					record.Points = ContactPoints(data.manifold, a, b, record.VelocityA, record.VelocityB);
				}

				events.Add(record);
			}

			for (int i = 0; i < contacts.endCount; i++)
			{
				ref B2ContactEndTouchEvent e = ref contacts.endEvents[i];
				Collider2D a = ColliderOf(e.shapeIdA);
				Collider2D b = ColliderOf(e.shapeIdB);
				if (a == null || b == null)
				{
					continue;
				}

				events.Add(new ContactEvent { A = a, B = b, Begin = false, Points = Array.Empty<ContactPoint2D>(), VelocityA = VelocityOf(a), VelocityB = VelocityOf(b) });
			}

			B2SensorEvents sensors = b2World_GetSensorEvents(World);
			for (int i = 0; i < sensors.beginCount; i++)
			{
				ref B2SensorBeginTouchEvent e = ref sensors.beginEvents[i];
				AddSensorEvent(e.sensorShapeId, e.visitorShapeId, true);
			}

			for (int i = 0; i < sensors.endCount; i++)
			{
				ref B2SensorEndTouchEvent e = ref sensors.endEvents[i];
				AddSensorEvent(e.sensorShapeId, e.visitorShapeId, false);
			}
		}

		private void AddSensorEvent(B2ShapeId sensorShape, B2ShapeId visitorShape, bool begin)
		{
			Collider2D sensor = ColliderOf(sensorShape);
			Collider2D visitor = ColliderOf(visitorShape);
			// Box2D sensors also see static shapes; a trigger event (as in Unity) needs a dynamic body on one side.
			if (sensor != null && visitor != null && sensor != visitor && (IsDynamic(sensor) || IsDynamic(visitor)))
			{
				events.Add(new ContactEvent { A = sensor, B = visitor, Begin = begin, Sensor = true, Points = Array.Empty<ContactPoint2D>() });
			}
		}

		private static bool IsDynamic(Collider2D collider)
		{
			return collider.attachedRigidbody != null && collider.attachedRigidbody.bodyType == RigidbodyType2D.Dynamic;
		}

		/// <summary>The collider's body velocity at the start of the step (before the contact resolved it), zero for static ones.</summary>
		private static Vector2 VelocityOf(Collider2D collider)
		{
			return collider.attachedRigidbody?.stepStartVelocity ?? Vector2.Zero;
		}

		/// <summary>Contact points of a touching contact from A's view (Box2D's normal points from A to B).</summary>
		private static ContactPoint2D[] ContactPoints(in B2Manifold manifold, Collider2D a, Collider2D b, Vector2 velocityA, Vector2 velocityB)
		{
			int count = manifold.pointCount;
			if (count <= 0)
			{
				return Array.Empty<ContactPoint2D>();
			}

			Vector2 normal = Physics2DMath.ToEngine(manifold.normal);
			var points = new ContactPoint2D[count];
			for (int i = 0; i < count; i++)
			{
				B2ManifoldPoint point = manifold.points[i];
				points[i] = new ContactPoint2D
				{
					point = Physics2DMath.ToEngine(point.point),
					normal = normal,
					separation = point.separation,
					normalImpulse = point.normalImpulse,
					tangentImpulse = point.tangentImpulse,
					collider = a,
					otherCollider = b,
					rigidbody = a.attachedRigidbody,
					otherRigidbody = b.attachedRigidbody,
					relativeVelocity = velocityB - velocityA,
					enabled = true
				};
			}

			return points;
		}

		private void DispatchEvents()
		{
			if (events.Count > 0)
			{
				ContactEvent[] pending = events.ToArray();
				events.Clear();
				foreach (ContactEvent e in pending)
				{
					if (e.A.gameObject == null || e.B.gameObject == null)
					{
						continue;
					}

					if (e.Sensor)
					{
						PhysicsCallbacks.Mask flag = e.Begin ? PhysicsCallbacks.Mask.TriggerEnter : PhysicsCallbacks.Mask.TriggerExit;
						Send(e.A.gameObject, flag, component => Trigger(component, e.B, e.Begin));
						Send(e.B.gameObject, flag, component => Trigger(component, e.A, e.Begin));
					}
					else
					{
						PhysicsCallbacks.Mask flag = e.Begin ? PhysicsCallbacks.Mask.CollisionEnter : PhysicsCallbacks.Mask.CollisionExit;
						ContactEvent captured = e;
						Send(e.A.gameObject, flag, component => Collide(component, BuildCollision(captured, true), captured.Begin));
						Send(e.B.gameObject, flag, component => Collide(component, BuildCollision(captured, false), captured.Begin));
					}
				}
			}

			DispatchStay();
		}

		/// <summary>
		///     Stay callbacks: each collider whose object handles OnCollisionStay2D gets its touching contacts (from Box2D's
		///     per-shape contact data), and each trigger reports what overlaps it to whichever side handles OnTriggerStay2D.
		///     Skipped when nothing in the scene handles them.
		/// </summary>
		private void DispatchStay()
		{
			PhysicsCallbacks.Mask wanted = PhysicsCallbacks.Mask.None;
			foreach (Behaviour component in Scene.ActiveBehaviours)
			{
				wanted |= PhysicsCallbacks.Of(component.GetType()) & (PhysicsCallbacks.Mask.CollisionStay | PhysicsCallbacks.Mask.TriggerStay);
			}

			if (wanted == PhysicsCallbacks.Mask.None)
			{
				return;
			}

			foreach (Collider2D collider in colliders.ToArray())
			{
				GameObject go = collider.gameObject;
				if (go == null || collider.world != this)
				{
					continue;
				}

				if (collider.isTrigger)
				{
					if ((wanted & PhysicsCallbacks.Mask.TriggerStay) != 0)
					{
						TriggerStay(collider);
					}

					continue;
				}

				if ((wanted & PhysicsCallbacks.Mask.CollisionStay) == 0 || !PhysicsCallbacks.AnyHandles(go, PhysicsCallbacks.Mask.CollisionStay))
				{
					continue;
				}

				foreach (B2ShapeId shape in collider.shapes.ToArray())
				{
					int count = ContactsOf(shape);
					for (int i = 0; i < count; i++)
					{
						B2ContactData data = contactBuffer[i];
						if (data.manifold.pointCount <= 0)
						{
							continue;
						}

						bool selfIsA = B2ShapeEquals(data.shapeIdA, shape);
						Collider2D other = ColliderOf(selfIsA ? data.shapeIdB : data.shapeIdA);
						if (other == null || other.gameObject == null)
						{
							continue;
						}

						Collider2D a = selfIsA ? collider : other;
						Collider2D b = selfIsA ? other : collider;
						var record = new ContactEvent { A = a, B = b, Begin = true, VelocityA = VelocityOf(a), VelocityB = VelocityOf(b) };
						record.Points = ContactPoints(data.manifold, a, b, record.VelocityA, record.VelocityB);
						Send(go, PhysicsCallbacks.Mask.CollisionStay, component => component.OnCollisionStay2D(BuildCollision(record, selfIsA)));
					}
				}
			}
		}

		private void TriggerStay(Collider2D trigger)
		{
			bool handlesTrigger = PhysicsCallbacks.AnyHandles(trigger.gameObject, PhysicsCallbacks.Mask.TriggerStay);
			foreach (B2ShapeId shape in trigger.shapes.ToArray())
			{
				if (!b2Shape_IsValid(shape))
				{
					continue;
				}

				int capacity = b2Shape_GetSensorCapacity(shape);
				if (capacity <= 0)
				{
					continue;
				}

				if (visitorBuffer.Length < capacity)
				{
					visitorBuffer = new B2ShapeId[Math.Max(capacity, visitorBuffer.Length * 2)];
				}

				int count = b2Shape_GetSensorData(shape, visitorBuffer, capacity);
				for (int i = 0; i < count; i++)
				{
					Collider2D visitor = ColliderOf(visitorBuffer[i]);
					if (visitor == null || visitor.gameObject == null || trigger.gameObject == null || !(IsDynamic(trigger) || IsDynamic(visitor)))
					{
						continue;
					}

					if (handlesTrigger) Send(trigger.gameObject, PhysicsCallbacks.Mask.TriggerStay, component => component.OnTriggerStay2D(visitor));
					if (PhysicsCallbacks.AnyHandles(visitor.gameObject, PhysicsCallbacks.Mask.TriggerStay)) Send(visitor.gameObject, PhysicsCallbacks.Mask.TriggerStay, component => component.OnTriggerStay2D(trigger));
				}
			}
		}

		/// <summary>The shape's contacts into <see cref="contactBuffer" />; returns how many.</summary>
		private int ContactsOf(B2ShapeId shape)
		{
			if (!b2Shape_IsValid(shape))
			{
				return 0;
			}

			int capacity = b2Shape_GetContactCapacity(shape);
			if (capacity <= 0)
			{
				return 0;
			}

			if (contactBuffer.Length < capacity)
			{
				contactBuffer = new B2ContactData[Math.Max(capacity, contactBuffer.Length * 2)];
			}

			return b2Shape_GetContactData(shape, contactBuffer, capacity);
		}

		/// <summary>Destroyed shapes are remembered for the step after their destruction (when their end events arrive).</summary>
		private void ForgetDestroyedShapes()
		{
			if (destroyedShapes.Count == 0)
			{
				return;
			}

			expired.Clear();
			foreach (KeyValuePair<long, (Collider2D collider, int step)> entry in destroyedShapes)
			{
				if (entry.Value.step < stepIndex)
				{
					expired.Add(entry.Key);
				}
			}

			foreach (long key in expired)
			{
				destroyedShapes.Remove(key);
			}
		}

		private static void Trigger(Behaviour component, Collider2D other, bool begin)
		{
			if (begin) component.OnTriggerEnter2D(other);
			else component.OnTriggerExit2D(other);
		}

		private static void Collide(Behaviour component, Collision2D collision, bool begin)
		{
			if (begin) component.OnCollisionEnter2D(collision);
			else component.OnCollisionExit2D(collision);
		}

		/// <summary>The collision as seen from A (<paramref name="fromA" />) or from B: "collider" is the other object's collider.</summary>
		private static Collision2D BuildCollision(ContactEvent e, bool fromA)
		{
			Collider2D self = fromA ? e.A : e.B;
			Collider2D other = fromA ? e.B : e.A;
			var collision = new Collision2D
			{
				collider = other,
				otherCollider = self,
				rigidbody = other.attachedRigidbody,
				otherRigidbody = self.attachedRigidbody,
				relativeVelocity = fromA ? e.VelocityB - e.VelocityA : e.VelocityA - e.VelocityB
			};

			if (e.Points == null || e.Points.Length == 0)
			{
				return collision;
			}

			var points = new ContactPoint2D[e.Points.Length];
			for (int i = 0; i < points.Length; i++)
			{
				ContactPoint2D p = e.Points[i];
				if (!fromA)
				{
					p.normal = -p.normal;
					p.relativeVelocity = -p.relativeVelocity;
					(p.collider, p.otherCollider) = (p.otherCollider, p.collider);
					(p.rigidbody, p.otherRigidbody) = (p.otherRigidbody, p.rigidbody);
				}

				points[i] = p;
			}

			collision.contacts = points;
			return collision;
		}

		private void Send(GameObject go, PhysicsCallbacks.Mask flag, Action<Behaviour> call)
		{
			callbackBuffer.Clear();
			foreach (Behaviour component in go.Components)
			{
				if (!component.destroyed && component.enableCalled && PhysicsCallbacks.Has(component, flag))
				{
					callbackBuffer.Add(component);
				}
			}

			if (callbackBuffer.Count == 0)
			{
				return;
			}

			Behaviour[] targets = callbackBuffer.ToArray();
			foreach (Behaviour component in targets)
			{
				try
				{
					call(component);
				}
				catch (Exception e)
				{
					Debug.LogException(e, component);
				}
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>
		///     State of the running query, handed to Box2D's callbacks as their context (one per world, reused: queries
		///     allocate no delegates or closures).
		/// </summary>
		private sealed class QueryState
		{
			public PhysicsScene2D Scene;
			private bool triggers;
			private bool startInColliders;
			private List<Collider2D> colliderResults;
			private List<RaycastHit2D> hitResults;
			private bool closestOnly;
			private bool testPoint;
			private bool shapeCast;
			private B2Vector2 point;
			private float distance;
			private Vector2 origin;
			private Vector2 direction;

			public void BeginOverlap(int layerMask, bool triggers, List<Collider2D> results, B2Vector2 point, bool testPoint)
			{
				this.triggers = triggers;
				colliderResults = results;
				this.point = point;
				this.testPoint = testPoint;
			}

			public void BeginCast(int layerMask, bool triggers, bool startInColliders, List<RaycastHit2D> results, bool closestOnly, float distance, Vector2 origin, Vector2 direction, bool shapeCast)
			{
				this.triggers = triggers;
				this.startInColliders = startInColliders;
				hitResults = results;
				this.closestOnly = closestOnly;
				this.distance = distance;
				this.origin = origin;
				this.direction = direction;
				this.shapeCast = shapeCast;
			}

			public void End()
			{
				colliderResults = null;
				hitResults = null;
			}

			public bool OnOverlap(B2ShapeId shape)
			{
				Collider2D collider = Accept(shape);
				if (collider == null || colliderResults.Contains(collider))
				{
					return true;
				}

				if (!testPoint || b2Shape_TestPoint(shape, point))
				{
					colliderResults.Add(collider);
				}

				return true;
			}

			public float OnCast(B2ShapeId shape, B2Vector2 hitPoint, B2Vector2 normal, float fraction)
			{
				Collider2D collider = Accept(shape);
				if (collider == null)
				{
					return -1f;
				}

				// Box2D reports a cast that starts inside a shape as a hit at fraction 0 without a normal.
				Vector2 hitNormal = Physics2DMath.ToEngine(normal);
				if (fraction <= 0f && hitNormal == Vector2.Zero)
				{
					if (!startInColliders)
					{
						return -1f;
					}

					hitNormal = -direction;
				}

				Vector2 p = Physics2DMath.ToEngine(hitPoint);
				var hit = new RaycastHit2D
				{
					collider = collider,
					point = p,
					normal = hitNormal,
					fraction = fraction,
					distance = fraction * distance,
					centroid = shapeCast ? origin + direction * (fraction * distance) : p
				};

				if (closestOnly)
				{
					hitResults.Clear();
					hitResults.Add(hit);
					return fraction;
				}

				hitResults.Add(hit);
				return 1f;
			}

			/// <summary>The shape's collider when the query may report it (layers are filtered by Box2D already).</summary>
			private Collider2D Accept(B2ShapeId shape)
			{
				Collider2D collider = b2Shape_GetUserData(shape).GetRef<Collider2D>();
				if (collider == null || collider.destroyed || collider.gameObject == null)
				{
					return null;
				}

				return !triggers && b2Shape_IsSensor(shape) ? null : collider;
			}
		}

		#endregion
	}
}
