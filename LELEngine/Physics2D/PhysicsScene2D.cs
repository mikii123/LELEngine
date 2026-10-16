using System;
using System.Collections.Generic;
using Box2DSharp.Collision;
using Box2DSharp.Collision.Collider;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;
using Box2DSharp.Dynamics.Contacts;
using Box2DSharp.Dynamics.Joints;
using OpenTK.Mathematics;
using B2Transform = Box2DSharp.Common.Transform;
using B2Vector2 = System.Numerics.Vector2;

namespace LELEngine
{
	/// <summary>
	///     The 2D physics world of a scene (Box2D through Box2DSharp): bodies of the scene's <see cref="Rigidbody2D" />
	///     components, fixtures of its <see cref="Collider2D" /> components (static bodies for colliders without a
	///     rigidbody) and its <see cref="Joint2D" /> constraints. Stepped from the scene's fixed update; writes the
	///     body poses back to the transforms, delivers OnCollision / OnTrigger callbacks and answers the queries of
	///     <see cref="Physics2D" />. Colliders of child objects attach to the nearest ancestor rigidbody, as in Unity.
	/// </summary>
	public sealed class PhysicsScene2D : IDisposable, IContactListener, IContactFilter, IDestructionListener
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

		internal World World { get; }

		/// <summary>Static body at the origin: joints without a connected body attach here; colliders without a rigidbody use their own static bodies.</summary>
		internal Body GroundBody { get; }

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
		private readonly List<Collider2D> stayA = new List<Collider2D>();
		private readonly List<Collider2D> stayB = new List<Collider2D>();
		private readonly List<Behaviour> callbackBuffer = new List<Behaviour>();
		private static readonly GJkProfile gjkProfile = new GJkProfile();
		private bool disposed;

		private Physics2DSettings Settings => Scene.Settings.Physics2D;

		#endregion

		#region Constructors

		internal PhysicsScene2D(Scene scene)
		{
			Scene = scene;
			World = new World(Physics2DMath.ToB2(scene.Settings.Physics2D.Gravity));
			World.SetContactListener(this);
			World.SetContactFilter(this);
			World.DestructionListener = this;
			World.ContinuousPhysics = true;

			var ground = new BodyDef { BodyType = BodyType.StaticBody, Position = B2Vector2.Zero, Enabled = true, Awake = true, AllowSleep = true, GravityScale = 1f };
			GroundBody = World.CreateBody(ref ground);
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
				joint.joint = null;
				joint.world = null;
			}

			foreach (Collider2D collider in colliders)
			{
				collider.fixtures.Clear();
				collider.ownStaticBody = null;
				collider.body = null;
				collider.attachedRigidbody = null;
				collider.world = null;
			}

			foreach (Rigidbody2D body in bodies)
			{
				body.body = null;
				body.world = null;
			}

			joints.Clear();
			colliders.Clear();
			bodies.Clear();
			events.Clear();
			World.Dispose();
		}

		// ---------------------------------------------------------------- registration

		internal void AddRigidbody(Rigidbody2D rigidbody)
		{
			if (disposed || rigidbody.body != null || rigidbody.gameObject == null)
			{
				return;
			}

			Transform transform = rigidbody.transform;
			Vector2 position;
			float angle;
			Vector2 scale;
			Physics2DMath.PlanarPose(transform, null, out position, out angle, out scale);

			var def = new BodyDef
			{
				BodyType = ToBox2D(rigidbody.bodyType),
				Position = Physics2DMath.ToB2(position),
				Angle = angle,
				LinearDamping = rigidbody.linearDamping,
				AngularDamping = rigidbody.angularDamping,
				AllowSleep = rigidbody.sleepMode != RigidbodySleepMode2D.NeverSleep,
				Awake = rigidbody.sleepMode != RigidbodySleepMode2D.StartAsleep,
				FixedRotation = (rigidbody.constraints & RigidbodyConstraints2D.FreezeRotation) != 0,
				Bullet = rigidbody.collisionDetectionMode == CollisionDetectionMode2D.Continuous,
				Enabled = rigidbody.simulated,
				GravityScale = 1f,
				UserData = rigidbody
			};

			rigidbody.body = World.CreateBody(ref def);
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
			if (disposed || rigidbody.body == null)
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

			// Joints on this body are destroyed by Box2D (SayGoodbye nulls them); they come back when both bodies exist again.
			World.DestroyBody(rigidbody.body);
			rigidbody.body = null;
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

		/// <summary>Recreates a collider's fixtures (its shape, offset, trigger flag or scale changed).</summary>
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
			if (joint.joint != null && !disposed)
			{
				World.DestroyJoint(joint.joint);
			}

			joint.joint = null;
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
					return rigidbody.body != null ? rigidbody : null;
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
			ApplyPendingChanges();
			World.Gravity = Physics2DMath.ToB2(Settings.Gravity);

			foreach (Rigidbody2D rigidbody in bodies)
			{
				rigidbody.BeforeStep(deltaTime);
			}

			IsStepping = true;
			try
			{
				World.Step(deltaTime, Math.Max(1, Settings.VelocityIterations), Math.Max(1, Settings.PositionIterations));
			}
			finally
			{
				IsStepping = false;
			}

			foreach (Rigidbody2D rigidbody in bodies)
			{
				rigidbody.AfterStep(deltaTime);
			}

			DispatchEvents();
			CheckJointBreaks(deltaTime);
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
				if (rigidbody.interpolation != RigidbodyInterpolation2D.None && rigidbody.body != null)
				{
					rigidbody.Interpolate(alpha);
				}
			}
		}

		// ---------------------------------------------------------------- collision filtering

		internal void IgnoreCollision(Collider2D a, Collider2D b, bool ignore)
		{
			if (a == null || b == null)
			{
				return;
			}

			(ulong, ulong) key = PairKey(a, b);
			if (ignore)
			{
				ignoredPairs.Add(key);
			}
			else
			{
				ignoredPairs.Remove(key);
			}

			a.Refilter();
			b.Refilter();
		}

		internal bool GetIgnoreCollision(Collider2D a, Collider2D b)
		{
			return a != null && b != null && ignoredPairs.Contains(PairKey(a, b));
		}

		bool IContactFilter.ShouldCollide(Fixture fixtureA, Fixture fixtureB)
		{
			var a = fixtureA.UserData as Collider2D;
			var b = fixtureB.UserData as Collider2D;
			if (a == null || b == null)
			{
				return true;
			}

			GameObject goA = a.gameObject;
			GameObject goB = b.gameObject;
			if (goA == null || goB == null)
			{
				return false;
			}

			if (Settings.GetIgnoreLayerCollision(goA.layer, goB.layer))
			{
				return false;
			}

			return ignoredPairs.Count == 0 || !ignoredPairs.Contains(PairKey(a, b));
		}

		// ---------------------------------------------------------------- contact events

		void IContactListener.BeginContact(Contact contact)
		{
			Record(contact, true);
		}

		void IContactListener.EndContact(Contact contact)
		{
			Record(contact, false);
		}

		void IContactListener.PreSolve(Contact contact, in Manifold oldManifold)
		{ }

		void IContactListener.PostSolve(Contact contact, in ContactImpulse impulse)
		{ }

		void IDestructionListener.SayGoodbye(Joint joint)
		{
			if (joint.UserData is Joint2D component)
			{
				component.joint = null;
				component.dirty = true;
			}
		}

		void IDestructionListener.SayGoodbye(Fixture fixture)
		{
			if (fixture.UserData is Collider2D collider)
			{
				collider.fixtures.Remove(fixture);
			}
		}

		// ---------------------------------------------------------------- queries

		/// <summary>Colliders whose fixtures overlap the AABB, filtered by layer and trigger settings (callback returns false to stop).</summary>
		internal void QueryAabb(Vector2 min, Vector2 max, int layerMask, bool includeTriggers, Func<Fixture, bool> visit)
		{
			if (disposed)
			{
				return;
			}

			var aabb = new AABB(Physics2DMath.ToB2(min), Physics2DMath.ToB2(max));
			IQueryCallback callback = new QueryCollector(this, layerMask, includeTriggers, visit);
			World.QueryAABB(ref callback, ref aabb);
		}

		internal bool Accepts(Fixture fixture, int layerMask, bool includeTriggers)
		{
			var collider = fixture.UserData as Collider2D;
			if (collider == null || collider.destroyed || collider.gameObject == null)
			{
				return false;
			}

			if (!includeTriggers && fixture.IsSensor)
			{
				return false;
			}

			return (layerMask & (1 << collider.gameObject.layer)) != 0;
		}

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

			if (Settings.QueriesStartInColliders)
			{
				// Colliders containing the origin hit at distance 0 (Box2D ignores shapes the ray starts inside).
				var inside = new List<Collider2D>();
				OverlapPointAll(origin, layerMask, triggers, inside);
				foreach (Collider2D collider in inside)
				{
					results.Add(new RaycastHit2D { collider = collider, point = origin, normal = -direction, centroid = origin, distance = 0f, fraction = 0f });
				}

				if (closestOnly && results.Count > 0)
				{
					return 1;
				}
			}

			if (distance <= 0f)
			{
				return results.Count;
			}

			B2Vector2 p1 = Physics2DMath.ToB2(origin);
			B2Vector2 p2 = Physics2DMath.ToB2(origin + direction * distance);
			IRayCastCallback callback = new RayCollector(this, layerMask, triggers, results, closestOnly, distance, origin);
			World.RayCast(ref callback, ref p1, ref p2);
			results.Sort((a, b) => a.distance.CompareTo(b.distance));
			if (closestOnly && results.Count > 1)
			{
				results.RemoveRange(1, results.Count - 1);
			}

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
			QueryAabb(point - new Vector2(0.001f), point + new Vector2(0.001f), layerMask, hitTriggers ?? Settings.QueriesHitTriggers, fixture =>
			{
				var collider = (Collider2D)fixture.UserData;
				if (!results.Contains(collider) && fixture.TestPoint(ref p))
				{
					results.Add(collider);
				}

				return true;
			});
			return results.Count;
		}

		/// <summary>Colliders overlapping a shape placed with the given transform.</summary>
		internal int OverlapShapeAll(Shape shape, B2Transform transform, int layerMask, bool? hitTriggers, List<Collider2D> results)
		{
			results.Clear();
			if (disposed)
			{
				return 0;
			}

			AABB bounds;
			shape.ComputeAABB(out bounds, ref transform, 0);
			B2Transform xf = transform;
			QueryAabb(Physics2DMath.ToEngine(bounds.LowerBound), Physics2DMath.ToEngine(bounds.UpperBound), layerMask, hitTriggers ?? Settings.QueriesHitTriggers, fixture =>
			{
				var collider = (Collider2D)fixture.UserData;
				if (results.Contains(collider))
				{
					return true;
				}

				B2Transform bodyTransform = fixture.Body.GetTransform();
				int children = fixture.Shape.GetChildCount();
				for (int child = 0; child < children; child++)
				{
					if (CollisionUtils.TestOverlap(shape, 0, fixture.Shape, child, ref xf, ref bodyTransform, gjkProfile))
					{
						results.Add(collider);
						break;
					}
				}

				return true;
			});
			return results.Count;
		}

		/// <summary>Sweeps a shape along a direction and reports what it would hit (circle and box casts).</summary>
		internal int ShapeCastAll(Shape shape, B2Transform transform, Vector2 direction, float distance, int layerMask, bool? hitTriggers, List<RaycastHit2D> results, bool closestOnly)
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

			AABB start;
			shape.ComputeAABB(out start, ref transform, 0);
			B2Vector2 translation = Physics2DMath.ToB2(direction * distance);
			var end = new B2Transform(transform.Position + translation, transform.Rotation.Angle);
			AABB endBounds;
			shape.ComputeAABB(out endBounds, ref end, 0);
			Vector2 min = Vector2.ComponentMin(Physics2DMath.ToEngine(start.LowerBound), Physics2DMath.ToEngine(endBounds.LowerBound));
			Vector2 max = Vector2.ComponentMax(Physics2DMath.ToEngine(start.UpperBound), Physics2DMath.ToEngine(endBounds.UpperBound));

			var proxyB = new DistanceProxy();
			proxyB.Set(shape, 0);
			B2Transform xf = transform;
			Vector2 origin = Physics2DMath.ToEngine(transform.Position);
			QueryAabb(min, max, layerMask, hitTriggers ?? Settings.QueriesHitTriggers, fixture =>
			{
				var collider = (Collider2D)fixture.UserData;
				B2Transform bodyTransform = fixture.Body.GetTransform();
				int children = fixture.Shape.GetChildCount();
				for (int child = 0; child < children; child++)
				{
					var proxyA = new DistanceProxy();
					proxyA.Set(fixture.Shape, child);
					var input = new ShapeCastInput { ProxyA = proxyA, ProxyB = proxyB, TransformA = bodyTransform, TransformB = xf, TranslationB = translation };
					ShapeCastOutput output;
					if (DistanceAlgorithm.ShapeCast(out output, ref input) && output.Lambda >= 0f && output.Lambda <= 1f)
					{
						results.Add(new RaycastHit2D
						{
							collider = collider,
							point = Physics2DMath.ToEngine(output.Point),
							normal = Physics2DMath.ToEngine(output.Normal),
							fraction = output.Lambda,
							distance = output.Lambda * distance,
							centroid = origin + direction * (output.Lambda * distance)
						});
						break;
					}
				}

				return true;
			});

			results.Sort((a, b) => a.distance.CompareTo(b.distance));
			if (closestOnly && results.Count > 1)
			{
				results.RemoveRange(1, results.Count - 1);
			}

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

			foreach (Contact contact in World.ContactManager.ContactList)
			{
				if (!contact.IsTouching || !contact.IsEnabled)
				{
					continue;
				}

				var a = contact.FixtureA.UserData as Collider2D;
				var b = contact.FixtureB.UserData as Collider2D;
				if (a == collider && b != null && !results.Contains(b)) results.Add(b);
				else if (b == collider && a != null && !results.Contains(a)) results.Add(a);
			}
		}

		#endregion

		#region PrivateMethods

		private static BodyType ToBox2D(RigidbodyType2D type)
		{
			switch (type)
			{
				case RigidbodyType2D.Kinematic: return BodyType.KinematicBody;
				case RigidbodyType2D.Static: return BodyType.StaticBody;
				default: return BodyType.DynamicBody;
			}
		}

		private static (ulong, ulong) PairKey(Collider2D a, Collider2D b)
		{
			return a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
		}

		private void AttachCollider(Collider2D collider)
		{
			Rigidbody2D owner = FindOwner(collider);
			Body body;
			if (owner != null)
			{
				body = owner.body;
			}
			else
			{
				// A static body at the origin; the fixtures are built in world space.
				var def = new BodyDef { BodyType = BodyType.StaticBody, Position = B2Vector2.Zero, Enabled = true, Awake = true, AllowSleep = true, GravityScale = 1f, UserData = collider };
				body = World.CreateBody(ref def);
				collider.ownStaticBody = body;
			}

			collider.attachedRigidbody = owner;
			collider.body = body;
			collider.CreateFixtures(body, owner?.transform);
			collider.shapeDirty = false;
			owner?.MassChanged();
		}

		private void DetachCollider(Collider2D collider)
		{
			collider.DestroyFixtures();
			if (collider.ownStaticBody != null)
			{
				World.DestroyBody(collider.ownStaticBody);
				collider.ownStaticBody = null;
			}

			Rigidbody2D owner = collider.attachedRigidbody;
			collider.attachedRigidbody = null;
			collider.body = null;
			owner?.MassChanged();
		}

		private void ApplyPendingChanges()
		{
			foreach (Collider2D collider in colliders.ToArray())
			{
				if (collider.shapeDirty || collider.StaticPoseChanged())
				{
					DetachCollider(collider);
					AttachCollider(collider);
				}
			}

			foreach (Rigidbody2D rigidbody in bodies)
			{
				rigidbody.SyncToBody();
			}

			CreateJointsIfReady();
			foreach (Joint2D joint in joints)
			{
				if (joint.joint != null && joint.runtimeDirty)
				{
					joint.runtimeDirty = false;
					joint.ApplyRuntimeSettings();
				}
			}
		}

		private void CreateJointsIfReady()
		{
			foreach (Joint2D joint in joints.ToArray())
			{
				if (!joint.dirty && joint.joint != null)
				{
					continue;
				}

				if (joint.joint != null)
				{
					World.DestroyJoint(joint.joint);
					joint.joint = null;
				}

				Body bodyA = joint.attachedRigidbody?.body;
				Body bodyB = joint.connectedBody != null ? joint.connectedBody.body : GroundBody;
				if (bodyA == null || bodyB == null || bodyA == bodyB)
				{
					continue;
				}

				JointDef def;
				try
				{
					def = joint.CreateDefinition(bodyA, bodyB);
				}
				catch (Exception e)
				{
					Debug.LogException(e, joint);
					def = null;
				}

				if (def == null)
				{
					continue;
				}

				def.BodyA = bodyA;
				def.BodyB = bodyB;
				def.CollideConnected = joint.enableCollision;
				def.UserData = joint;
				joint.joint = World.CreateJoint(def);
				joint.dirty = false;
				joint.runtimeDirty = false;
				joint.ApplyRuntimeSettings();
			}
		}

		private void CheckJointBreaks(float deltaTime)
		{
			foreach (Joint2D joint in joints.ToArray())
			{
				if (joint.joint == null || (float.IsPositiveInfinity(joint.breakForce) && float.IsPositiveInfinity(joint.breakTorque)))
				{
					continue;
				}

				float inverseDt = 1f / deltaTime;
				Vector2 force = Physics2DMath.ToEngine(joint.joint.GetReactionForce(inverseDt));
				float torque = joint.joint.GetReactionTorque(inverseDt);
				if (force.Length > joint.breakForce || Math.Abs(torque) > joint.breakTorque)
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
		}

		private void Record(Contact contact, bool begin)
		{
			var a = contact.FixtureA.UserData as Collider2D;
			var b = contact.FixtureB.UserData as Collider2D;
			if (a == null || b == null)
			{
				return;
			}

			bool sensor = contact.FixtureA.IsSensor || contact.FixtureB.IsSensor;
			var record = new ContactEvent { A = a, B = b, Begin = begin, Sensor = sensor, Points = Array.Empty<ContactPoint2D>() };
			if (!sensor)
			{
				record.VelocityA = Physics2DMath.ToEngine(contact.FixtureA.Body.LinearVelocity);
				record.VelocityB = Physics2DMath.ToEngine(contact.FixtureB.Body.LinearVelocity);
				if (begin && (PhysicsCallbacks.AnyHandles(a.gameObject, PhysicsCallbacks.Mask.CollisionEnter) || PhysicsCallbacks.AnyHandles(b.gameObject, PhysicsCallbacks.Mask.CollisionEnter)))
				{
					record.Points = ContactPoints(contact, a, b);
				}
			}

			events.Add(record);
		}

		/// <summary>Contact points of a touching contact from A's view (normal from A to B).</summary>
		private static ContactPoint2D[] ContactPoints(Contact contact, Collider2D a, Collider2D b)
		{
			int count = contact.Manifold.PointCount;
			if (count <= 0)
			{
				return Array.Empty<ContactPoint2D>();
			}

			WorldManifold manifold;
			contact.GetWorldManifold(out manifold);
			Vector2 normal = Physics2DMath.ToEngine(manifold.Normal);
			Vector2 velocityA = Physics2DMath.ToEngine(contact.FixtureA.Body.LinearVelocity);
			Vector2 velocityB = Physics2DMath.ToEngine(contact.FixtureB.Body.LinearVelocity);
			var points = new ContactPoint2D[count];
			for (int i = 0; i < count; i++)
			{
				points[i] = new ContactPoint2D
				{
					point = Physics2DMath.ToEngine(manifold.Points[i]),
					normal = normal,
					separation = manifold.Separations[i],
					normalImpulse = contact.Manifold.Points[i].NormalImpulse,
					tangentImpulse = contact.Manifold.Points[i].TangentImpulse,
					collider = a,
					otherCollider = b,
					rigidbody = a.attachedRigidbody,
					otherRigidbody = b.attachedRigidbody,
					relativeVelocity = velocityB - velocityA,
					enabled = contact.IsEnabled
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

			// Stay callbacks for every touching contact.
			stayA.Clear();
			stayB.Clear();
			foreach (Contact contact in World.ContactManager.ContactList)
			{
				if (!contact.IsTouching || !contact.IsEnabled)
				{
					continue;
				}

				var a = contact.FixtureA.UserData as Collider2D;
				var b = contact.FixtureB.UserData as Collider2D;
				if (a == null || b == null || a.gameObject == null || b.gameObject == null)
				{
					continue;
				}

				bool sensor = contact.FixtureA.IsSensor || contact.FixtureB.IsSensor;
				if (sensor)
				{
					if (PhysicsCallbacks.AnyHandles(a.gameObject, PhysicsCallbacks.Mask.TriggerStay)) Send(a.gameObject, PhysicsCallbacks.Mask.TriggerStay, component => component.OnTriggerStay2D(b));
					if (PhysicsCallbacks.AnyHandles(b.gameObject, PhysicsCallbacks.Mask.TriggerStay)) Send(b.gameObject, PhysicsCallbacks.Mask.TriggerStay, component => component.OnTriggerStay2D(a));
					continue;
				}

				bool handlesA = PhysicsCallbacks.AnyHandles(a.gameObject, PhysicsCallbacks.Mask.CollisionStay);
				bool handlesB = PhysicsCallbacks.AnyHandles(b.gameObject, PhysicsCallbacks.Mask.CollisionStay);
				if (!handlesA && !handlesB)
				{
					continue;
				}

				var record = new ContactEvent
				{
					A = a,
					B = b,
					Begin = true,
					Points = ContactPoints(contact, a, b),
					VelocityA = Physics2DMath.ToEngine(contact.FixtureA.Body.LinearVelocity),
					VelocityB = Physics2DMath.ToEngine(contact.FixtureB.Body.LinearVelocity)
				};
				if (handlesA) Send(a.gameObject, PhysicsCallbacks.Mask.CollisionStay, component => component.OnCollisionStay2D(BuildCollision(record, true)));
				if (handlesB) Send(b.gameObject, PhysicsCallbacks.Mask.CollisionStay, component => component.OnCollisionStay2D(BuildCollision(record, false)));
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

			if (e.Points.Length == 0)
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

		private sealed class QueryCollector : IQueryCallback
		{
			private readonly PhysicsScene2D scene;
			private readonly int layerMask;
			private readonly bool triggers;
			private readonly Func<Fixture, bool> visit;

			public QueryCollector(PhysicsScene2D scene, int layerMask, bool triggers, Func<Fixture, bool> visit)
			{
				this.scene = scene;
				this.layerMask = layerMask;
				this.triggers = triggers;
				this.visit = visit;
			}

			public bool QueryCallback(Fixture fixture)
			{
				return !scene.Accepts(fixture, layerMask, triggers) || visit(fixture);
			}
		}

		private sealed class RayCollector : IRayCastCallback
		{
			private readonly PhysicsScene2D scene;
			private readonly int layerMask;
			private readonly bool triggers;
			private readonly List<RaycastHit2D> results;
			private readonly bool closestOnly;
			private readonly float distance;
			private readonly Vector2 origin;

			public RayCollector(PhysicsScene2D scene, int layerMask, bool triggers, List<RaycastHit2D> results, bool closestOnly, float distance, Vector2 origin)
			{
				this.scene = scene;
				this.layerMask = layerMask;
				this.triggers = triggers;
				this.results = results;
				this.closestOnly = closestOnly;
				this.distance = distance;
				this.origin = origin;
			}

			public float RayCastCallback(Fixture fixture, in B2Vector2 point, in B2Vector2 normal, float fraction)
			{
				if (!scene.Accepts(fixture, layerMask, triggers))
				{
					return -1f;
				}

				var hit = new RaycastHit2D
				{
					collider = (Collider2D)fixture.UserData,
					point = Physics2DMath.ToEngine(point),
					normal = Physics2DMath.ToEngine(normal),
					fraction = fraction,
					distance = fraction * distance,
					centroid = Physics2DMath.ToEngine(point)
				};

				if (closestOnly)
				{
					results.Clear();
					results.Add(hit);
					return fraction;
				}

				results.Add(hit);
				return 1f;
			}
		}

		#endregion
	}
}
