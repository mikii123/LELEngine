using System;
using System.Collections.Generic;
using Box2D.NET;
using OpenTK.Mathematics;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Ids;
using static Box2D.NET.B2MathFunction;
using B2Vector2 = Box2D.NET.B2Vec2;

namespace LELEngine
{
	/// <summary>
	///     A body in the scene's 2D physics world (Box2D): the colliders of this object and of its children without a
	///     rigidbody of their own become its shapes. Dynamic bodies move under forces, gravity and collisions in the
	///     project's simulation plane (XY or XZ, see <see cref="Physics2DSettings.SimulationPlane" />); the transform
	///     follows the body after every physics step (its position along the plane's normal and its rotation about
	///     other axes are kept). Moving the transform directly teleports the body. API modelled on Unity's Rigidbody2D.
	/// </summary>
	public sealed class Rigidbody2D : Behaviour
	{
		#region PublicFields

		// ---- settings: serialized fields behind properties (Unity's names); a setter reaches the live body at once

		[SerializeField] private RigidbodyType2D m_BodyType = RigidbodyType2D.Dynamic;

		[Tooltip("Friction and bounciness of the colliders that have no material of their own.")]
		[SerializeField] private PhysicsMaterial2D m_Material;

		[Tooltip("Off: the body and its colliders are removed from the simulation (no collisions, no movement).")]
		[SerializeField] private bool m_Simulated = true;

		[Tooltip("Mass from the colliders' densities; off: Mass is used.")]
		[SerializeField] private bool m_UseAutoMass = true;

		[Tooltip("Mass in kilograms when Use Auto Mass is off.")]
		[SerializeField] private float m_Mass = 1f;

		[Tooltip("Velocity lost per second (0 = none).")]
		[SerializeField] private float m_LinearDamping;

		[SerializeField] private float m_AngularDamping = 0.05f;
		[SerializeField] private float m_GravityScale = 1f;
		[SerializeField] private CollisionDetectionMode2D m_CollisionDetection = CollisionDetectionMode2D.Discrete;
		[SerializeField] private RigidbodySleepMode2D m_SleepingMode = RigidbodySleepMode2D.StartAwake;
		[SerializeField] private RigidbodyInterpolation2D m_Interpolate = RigidbodyInterpolation2D.None;
		[SerializeField] private RigidbodyConstraints2D m_Constraints = RigidbodyConstraints2D.None;

		public RigidbodyType2D bodyType
		{
			get => m_BodyType;
			set
			{
				m_BodyType = value;
				SettingsChanged();
			}
		}

		/// <summary>Material of the colliders that have none of their own.</summary>
		public PhysicsMaterial2D sharedMaterial
		{
			get => m_Material;
			set
			{
				m_Material = value;
				MaterialChanged();
			}
		}

		/// <summary>Off: the body and its colliders are removed from the simulation (no collisions, no movement).</summary>
		public bool simulated
		{
			get => m_Simulated;
			set
			{
				m_Simulated = value;
				SettingsChanged();
			}
		}

		/// <summary>Mass from the colliders' densities; off: <see cref="mass" /> is used.</summary>
		public bool useAutoMass
		{
			get => m_UseAutoMass;
			set
			{
				m_UseAutoMass = value;
				MassChanged();
			}
		}

		/// <summary>Mass in kilograms (used when <see cref="useAutoMass" /> is off).</summary>
		public float mass
		{
			get => hasBody && m_UseAutoMass ? b2Body_GetMass(body) : m_Mass;
			set
			{
				m_Mass = value;
				MassChanged();
			}
		}

		/// <summary>Velocity lost per second (0 = none).</summary>
		public float linearDamping
		{
			get => m_LinearDamping;
			set
			{
				m_LinearDamping = value;
				SettingsChanged();
			}
		}

		public float angularDamping
		{
			get => m_AngularDamping;
			set
			{
				m_AngularDamping = value;
				SettingsChanged();
			}
		}

		public float gravityScale
		{
			get => m_GravityScale;
			set
			{
				m_GravityScale = value;
				SettingsChanged();
			}
		}

		public CollisionDetectionMode2D collisionDetectionMode
		{
			get => m_CollisionDetection;
			set
			{
				m_CollisionDetection = value;
				SettingsChanged();
			}
		}

		public RigidbodySleepMode2D sleepMode
		{
			get => m_SleepingMode;
			set
			{
				m_SleepingMode = value;
				SettingsChanged();
			}
		}

		/// <summary>How the transform follows the body between physics steps (read every frame).</summary>
		public RigidbodyInterpolation2D interpolation
		{
			get => m_Interpolate;
			set => m_Interpolate = value;
		}

		public RigidbodyConstraints2D constraints
		{
			get => m_Constraints;
			set
			{
				m_Constraints = value;
				SettingsChanged();
			}
		}

		// ---- runtime state

		/// <summary>Linear velocity in units per second.</summary>
		public Vector2 velocity
		{
			get => hasBody ? Physics2DMath.ToEngine(b2Body_GetLinearVelocity(body)) : initialVelocity;
			set
			{
				initialVelocity = value;
				if (hasBody)
				{
					b2Body_SetLinearVelocity(body, Physics2DMath.ToB2(value));
					b2Body_SetAwake(body, true);
				}
			}
		}

		public Vector2 linearVelocity
		{
			get => velocity;
			set => velocity = value;
		}

		/// <summary>Linear damping (Unity's older name).</summary>
		public float drag
		{
			get => linearDamping;
			set => linearDamping = value;
		}

		public float angularDrag
		{
			get => angularDamping;
			set => angularDamping = value;
		}

		/// <summary>Angular velocity in degrees per second.</summary>
		public float angularVelocity
		{
			get => hasBody ? b2Body_GetAngularVelocity(body) * Physics2DMath.Rad2Deg : initialAngularVelocity;
			set
			{
				initialAngularVelocity = value;
				if (hasBody)
				{
					b2Body_SetAngularVelocity(body, value * Physics2DMath.Deg2Rad);
					b2Body_SetAwake(body, true);
				}
			}
		}

		/// <summary>Position of the body (its transform's position in the simulation plane); setting it teleports.</summary>
		public Vector2 position
		{
			get => hasBody ? Physics2DMath.ToEngine(b2Body_GetPosition(body)) : transform != null ? Plane.ToPlane(transform.position) : Vector2.Zero;
			set
			{
				if (hasBody)
				{
					B2Rot q = b2Body_GetRotation(body);
					b2Body_SetTransform(body, Physics2DMath.ToB2(value), q);
					float angle = b2Rot_GetAngle(q);
					WriteTransform(value, angle);
					currentPosition = previousPosition = value;
				}
				else if (transform != null)
				{
					transform.position = Plane.FromPlane(value, transform.position);
				}
			}
		}

		/// <summary>Rotation in the simulation plane in degrees (about Z in the XY plane, about -Y in the XZ plane).</summary>
		public float rotation
		{
			get => hasBody ? b2Rot_GetAngle(b2Body_GetRotation(body)) * Physics2DMath.Rad2Deg : transform != null ? Plane.AngleOf(transform.rotation) * Physics2DMath.Rad2Deg : 0f;
			set
			{
				float angle = value * Physics2DMath.Deg2Rad;
				if (hasBody)
				{
					B2Vector2 p = b2Body_GetPosition(body);
					b2Body_SetTransform(body, p, b2MakeRot(angle));
					WriteTransform(Physics2DMath.ToEngine(p), angle);
					currentAngle = previousAngle = angle;
				}
				else if (transform != null)
				{
					baseRotation = Plane.RemoveTurn(transform.rotation);
					transform.rotation = Plane.WithTurn(baseRotation, angle);
				}
			}
		}

		public float inertia => hasBody ? b2Body_GetRotationalInertia(body) : 0f;
		public Vector2 worldCenterOfMass => hasBody ? Physics2DMath.ToEngine(b2Body_GetWorldCenterOfMass(body)) : position;

		/// <summary>Center of mass in the body's local space; set to override the computed one.</summary>
		public Vector2 centerOfMass
		{
			get => hasBody ? Physics2DMath.ToEngine(b2Body_GetLocalCenterOfMass(body)) : customCenter ?? Vector2.Zero;
			set
			{
				customCenter = value;
				MassChanged();
			}
		}

		public bool freezeRotation
		{
			get => (constraints & RigidbodyConstraints2D.FreezeRotation) != 0;
			set => constraints = value ? constraints | RigidbodyConstraints2D.FreezeRotation : constraints & ~RigidbodyConstraints2D.FreezeRotation;
		}

		public bool isKinematic
		{
			get => bodyType == RigidbodyType2D.Kinematic;
			set => bodyType = value ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
		}

		public int attachedColliderCount
		{
			get
			{
				int count = 0;
				foreach (Collider2D collider in Colliders())
				{
					count++;
				}

				return count;
			}
		}

		/// <summary>The Box2D body, for code that needs the engine underneath (a null id while not simulated).</summary>
		public B2BodyId Box2DBody => body;

		#endregion

		#region InternalFields

		internal B2BodyId body;
		internal PhysicsScene2D world;

		/// <summary>True while the Box2D body exists.</summary>
		internal bool hasBody => body.index1 != 0;

		/// <summary>Linear velocity at the start of the last step: the "before the hit" velocity collision callbacks report.</summary>
		internal Vector2 stepStartVelocity;

		#endregion

		#region PrivateFields

		private Vector2 initialVelocity;
		private float initialAngularVelocity;
		private Vector2? customCenter;
		private bool settingsDirty;
		private bool massDirty;
		private Quaternion baseRotation = Quaternion.Identity;
		private Vector3 lastWrittenPosition;
		private Quaternion lastWrittenRotation = Quaternion.Identity;
		private Vector2 previousPosition;
		private Vector2 currentPosition;
		private float previousAngle;
		private float currentAngle;
		private Vector2? moveTarget;
		private float? moveAngle;
		private readonly List<Collider2D> colliderBuffer = new List<Collider2D>();

		private PlaneBasis Plane => world?.Plane ?? PlaneBasis.Current;

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			Scene scene = gameObject?.scene;
			if (scene != null && scene.IsPlaying)
			{
				scene.Physics2D.AddRigidbody(this);
			}
		}

		public override void OnDisable()
		{
			world?.RemoveRigidbody(this);
		}

		public override void OnDestroy()
		{
			world?.RemoveRigidbody(this);
		}

		/// <summary>Inspector edits (or a script writing the serialized state) reach the live body at once.</summary>
		public override void OnValidate()
		{
			massDirty = true;
			SettingsChanged();
			MaterialChanged();
		}

		#endregion

		#region PublicMethods

		public bool IsSleeping()
		{
			return hasBody && !b2Body_IsAwake(body);
		}

		public bool IsAwake()
		{
			return hasBody && b2Body_IsAwake(body);
		}

		public void Sleep()
		{
			if (hasBody)
			{
				b2Body_SetAwake(body, false);
			}
		}

		public void WakeUp()
		{
			if (hasBody)
			{
				b2Body_SetAwake(body, true);
			}
		}

		public void AddForce(Vector2 force, ForceMode2D mode = ForceMode2D.Force)
		{
			if (!hasBody)
			{
				return;
			}

			B2Vector2 f = Physics2DMath.ToB2(force);
			if (mode == ForceMode2D.Impulse) b2Body_ApplyLinearImpulseToCenter(body, f, true);
			else b2Body_ApplyForceToCenter(body, f, true);
		}

		public void AddForceAtPosition(Vector2 force, Vector2 worldPosition, ForceMode2D mode = ForceMode2D.Force)
		{
			if (!hasBody)
			{
				return;
			}

			B2Vector2 f = Physics2DMath.ToB2(force);
			B2Vector2 p = Physics2DMath.ToB2(worldPosition);
			if (mode == ForceMode2D.Impulse) b2Body_ApplyLinearImpulse(body, f, p, true);
			else b2Body_ApplyForce(body, f, p, true);
		}

		/// <summary>Force in the body's local axes.</summary>
		public void AddRelativeForce(Vector2 relativeForce, ForceMode2D mode = ForceMode2D.Force)
		{
			if (!hasBody)
			{
				return;
			}

			B2Vector2 f = b2Body_GetWorldVector(body, Physics2DMath.ToB2(relativeForce));
			if (mode == ForceMode2D.Impulse) b2Body_ApplyLinearImpulseToCenter(body, f, true);
			else b2Body_ApplyForceToCenter(body, f, true);
		}

		public void AddTorque(float torque, ForceMode2D mode = ForceMode2D.Force)
		{
			if (!hasBody)
			{
				return;
			}

			if (mode == ForceMode2D.Impulse) b2Body_ApplyAngularImpulse(body, torque, true);
			else b2Body_ApplyTorque(body, torque, true);
		}

		/// <summary>Moves a kinematic body to the position during the next step (dynamic bodies teleport).</summary>
		public void MovePosition(Vector2 target)
		{
			if (bodyType == RigidbodyType2D.Kinematic)
			{
				moveTarget = target;
			}
			else
			{
				position = target;
			}
		}

		/// <summary>Rotates a kinematic body to the angle (degrees) during the next step.</summary>
		public void MoveRotation(float angle)
		{
			if (bodyType == RigidbodyType2D.Kinematic)
			{
				moveAngle = angle * Physics2DMath.Deg2Rad;
			}
			else
			{
				rotation = angle;
			}
		}

		/// <summary>A world point in the body's local space.</summary>
		public Vector2 GetPoint(Vector2 worldPoint)
		{
			return hasBody ? Physics2DMath.ToEngine(b2Body_GetLocalPoint(body, Physics2DMath.ToB2(worldPoint))) : worldPoint - position;
		}

		/// <summary>A local point in world space.</summary>
		public Vector2 GetRelativePoint(Vector2 localPoint)
		{
			return hasBody ? Physics2DMath.ToEngine(b2Body_GetWorldPoint(body, Physics2DMath.ToB2(localPoint))) : position + localPoint;
		}

		public Vector2 GetVector(Vector2 worldVector)
		{
			return hasBody ? Physics2DMath.ToEngine(b2Body_GetLocalVector(body, Physics2DMath.ToB2(worldVector))) : worldVector;
		}

		public Vector2 GetRelativeVector(Vector2 localVector)
		{
			return hasBody ? Physics2DMath.ToEngine(b2Body_GetWorldVector(body, Physics2DMath.ToB2(localVector))) : localVector;
		}

		public Vector2 GetPointVelocity(Vector2 worldPoint)
		{
			return hasBody ? Physics2DMath.ToEngine(b2Body_GetWorldPointVelocity(body, Physics2DMath.ToB2(worldPoint))) : velocity;
		}

		public Vector2 GetRelativePointVelocity(Vector2 localPoint)
		{
			return hasBody ? Physics2DMath.ToEngine(b2Body_GetLocalPointVelocity(body, Physics2DMath.ToB2(localPoint))) : velocity;
		}

		public int GetAttachedColliders(List<Collider2D> results)
		{
			results.Clear();
			results.AddRange(Colliders());
			return results.Count;
		}

		public bool IsTouching(Collider2D other)
		{
			foreach (Collider2D collider in Colliders())
			{
				if (collider.IsTouching(other))
				{
					return true;
				}
			}

			return false;
		}

		public bool IsTouchingLayers(int layerMask = -1)
		{
			foreach (Collider2D collider in Colliders())
			{
				if (collider.IsTouchingLayers(layerMask))
				{
					return true;
				}
			}

			return false;
		}

		public bool OverlapPoint(Vector2 point)
		{
			foreach (Collider2D collider in Colliders())
			{
				if (collider.OverlapPoint(point))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Casts every attached collider along a direction; results sorted by distance.</summary>
		public int Cast(Vector2 direction, List<RaycastHit2D> results, float distance = float.PositiveInfinity)
		{
			results.Clear();
			var hits = new List<RaycastHit2D>();
			foreach (Collider2D collider in Colliders())
			{
				collider.Cast(direction, hits, distance, true);
				foreach (RaycastHit2D hit in hits)
				{
					if (hit.collider != null && hit.collider.attachedRigidbody != this)
					{
						results.Add(hit);
					}
				}
			}

			results.Sort((a, b) => a.distance.CompareTo(b.distance));
			return results.Count;
		}

		#endregion

		#region InternalMethods

		/// <summary>The colliders attached to this body (its object and children without their own rigidbody).</summary>
		internal IEnumerable<Collider2D> Colliders()
		{
			colliderBuffer.Clear();
			if (gameObject != null)
			{
				CollectColliders(gameObject, colliderBuffer, true);
			}

			return colliderBuffer;
		}

		internal void OnBodyCreated(Vector2 position, float angle)
		{
			baseRotation = Plane.RemoveTurn(transform.rotation);
			lastWrittenPosition = transform.position;
			lastWrittenRotation = transform.rotation;
			previousPosition = currentPosition = position;
			previousAngle = currentAngle = angle;
			if (initialVelocity != Vector2.Zero)
			{
				b2Body_SetLinearVelocity(body, Physics2DMath.ToB2(initialVelocity));
			}

			if (initialAngularVelocity != 0f)
			{
				b2Body_SetAngularVelocity(body, initialAngularVelocity * Physics2DMath.Deg2Rad);
			}

			settingsDirty = false;
			massDirty = true;
		}

		/// <summary>Pushes the serialized settings into the body (creation, inspector edits, property setters).</summary>
		internal void ApplyBodySettings()
		{
			if (!hasBody)
			{
				return;
			}

			B2BodyType type = PhysicsScene2D.ToBox2D(bodyType);
			if (b2Body_GetType(body) != type)
			{
				b2Body_SetType(body, type);
				massDirty = true;
			}

			b2Body_SetLinearDamping(body, linearDamping);
			b2Body_SetAngularDamping(body, angularDamping);
			b2Body_SetGravityScale(body, gravityScale);
			b2Body_EnableSleep(body, sleepMode != RigidbodySleepMode2D.NeverSleep);
			b2Body_SetMotionLocks(body, MotionLocks());
			b2Body_SetBullet(body, collisionDetectionMode == CollisionDetectionMode2D.Continuous);
			if (b2Body_IsEnabled(body) != simulated)
			{
				if (simulated) b2Body_Enable(body);
				else b2Body_Disable(body);
			}

			settingsDirty = false;
			if (massDirty)
			{
				ApplyMass();
			}
		}

		/// <summary>The constraints as Box2D motion locks (frozen axes take no velocity, so the solver never moves them).</summary>
		internal B2MotionLocks MotionLocks()
		{
			return new B2MotionLocks(
				(constraints & RigidbodyConstraints2D.FreezePositionX) != 0,
				(constraints & RigidbodyConstraints2D.FreezePositionY) != 0,
				(constraints & RigidbodyConstraints2D.FreezeRotation) != 0);
		}

		internal void MassChanged()
		{
			massDirty = true;
			if (hasBody && world != null && !world.IsStepping)
			{
				ApplyMass();
			}
		}

		/// <summary>A body setting changed: applied now, or before the next step while the world is stepping.</summary>
		private void SettingsChanged()
		{
			settingsDirty = true;
			if (hasBody && world != null && !world.IsStepping)
			{
				ApplyBodySettings();
			}
		}

		/// <summary>The colliders without a material of their own use this body's: their shapes take the new values.</summary>
		private void MaterialChanged()
		{
			if (!hasBody)
			{
				return;
			}

			foreach (Collider2D collider in Colliders())
			{
				collider.ApplyMaterial();
			}
		}

		/// <summary>Before a step: transform edits, settings and kinematic moves reach the body.</summary>
		internal void SyncToBody()
		{
			if (!hasBody || transform == null)
			{
				return;
			}

			if (settingsDirty || massDirty)
			{
				ApplyBodySettings();
			}

			Vector3 position = transform.position;
			Quaternion rotation = transform.rotation;
			bool moved = (position - lastWrittenPosition).LengthSquared > 1e-10f;
			bool rotated = !RotationEquals(rotation, lastWrittenRotation);
			if (moved || rotated)
			{
				// The transform was moved by a script or the editor: teleport the body.
				PlaneBasis plane = Plane;
				baseRotation = plane.RemoveTurn(rotation);
				float angle = plane.AngleOf(rotation);
				Vector2 planar = plane.ToPlane(position);
				b2Body_SetTransform(body, Physics2DMath.ToB2(planar), b2MakeRot(angle));
				b2Body_SetAwake(body, true);
				lastWrittenPosition = position;
				lastWrittenRotation = rotation;
				previousPosition = currentPosition = planar;
				previousAngle = currentAngle = angle;
			}
		}

		internal void BeforeStep(float deltaTime)
		{
			if (!hasBody)
			{
				return;
			}

			B2Transform pose = b2Body_GetTransform(body);
			previousPosition = Physics2DMath.ToEngine(pose.p);
			previousAngle = b2Rot_GetAngle(pose.q);
			stepStartVelocity = Physics2DMath.ToEngine(b2Body_GetLinearVelocity(body));

			if (bodyType == RigidbodyType2D.Kinematic && (moveTarget.HasValue || moveAngle.HasValue))
			{
				// Box2D sets the velocities that reach the target in this step.
				var target = new B2Transform(
					moveTarget.HasValue ? Physics2DMath.ToB2(moveTarget.Value) : pose.p,
					moveAngle.HasValue ? b2MakeRot(moveAngle.Value) : pose.q);
				b2Body_SetTargetTransform(body, target, deltaTime, true);
			}
		}

		internal void AfterStep(float deltaTime)
		{
			if (!hasBody || transform == null)
			{
				return;
			}

			if (bodyType == RigidbodyType2D.Kinematic)
			{
				if (moveTarget.HasValue)
				{
					b2Body_SetLinearVelocity(body, b2Vec2_zero);
					moveTarget = null;
				}

				if (moveAngle.HasValue)
				{
					b2Body_SetAngularVelocity(body, 0f);
					moveAngle = null;
				}
			}

			B2Transform pose = b2Body_GetTransform(body);
			currentPosition = Physics2DMath.ToEngine(pose.p);
			currentAngle = b2Rot_GetAngle(pose.q);
			if (interpolation == RigidbodyInterpolation2D.None || bodyType == RigidbodyType2D.Static)
			{
				WriteTransform(currentPosition, currentAngle);
			}
		}

		internal void Interpolate(float alpha)
		{
			if (!hasBody || transform == null || bodyType == RigidbodyType2D.Static)
			{
				return;
			}

			alpha = Math.Clamp(alpha, 0f, 1f);
			Vector2 position;
			float angle;
			if (interpolation == RigidbodyInterpolation2D.Extrapolate)
			{
				float ahead = alpha * (world?.LastStepDeltaTime ?? 0.02f);
				position = currentPosition + Physics2DMath.ToEngine(b2Body_GetLinearVelocity(body)) * ahead;
				angle = currentAngle + b2Body_GetAngularVelocity(body) * ahead;
			}
			else
			{
				position = Vector2.Lerp(previousPosition, currentPosition, alpha);
				float delta = currentAngle - previousAngle;
				while (delta > MathF.PI) delta -= 2f * MathF.PI;
				while (delta < -MathF.PI) delta += 2f * MathF.PI;
				angle = previousAngle + delta * alpha;
			}

			WriteTransform(position, angle);
		}

		#endregion

		#region PrivateMethods

		private static bool RotationEquals(Quaternion a, Quaternion b)
		{
			return Math.Abs(a.X - b.X) < 1e-6f && Math.Abs(a.Y - b.Y) < 1e-6f && Math.Abs(a.Z - b.Z) < 1e-6f && Math.Abs(a.W - b.W) < 1e-6f;
		}

		private void WriteTransform(Vector2 position, float angle)
		{
			PlaneBasis plane = Plane;
			transform.position = plane.FromPlane(position, transform.position);
			transform.rotation = plane.WithTurn(baseRotation, angle);
			lastWrittenPosition = transform.position;
			lastWrittenRotation = transform.rotation;
		}

		/// <summary>
		///     Mass from the shapes (they are created without updating it, so a body with many shapes is not recomputed
		///     once per shape), then the explicit mass or center when set.
		/// </summary>
		private void ApplyMass()
		{
			massDirty = false;
			if (!hasBody || b2Body_GetType(body) != B2BodyType.b2_dynamicBody)
			{
				return;
			}

			b2Body_ApplyMassFromShapes(body);
			if (useAutoMass && !customCenter.HasValue)
			{
				return;
			}

			B2MassData data = b2Body_GetMassData(body);
			if (!useAutoMass)
			{
				float target = Math.Max(0.0001f, mass);
				float factor = data.mass > 0f ? target / data.mass : 1f;
				data.rotationalInertia = data.mass > 0f ? data.rotationalInertia * factor : target * 0.1f;
				data.mass = target;
			}

			if (customCenter.HasValue)
			{
				data.center = Physics2DMath.ToB2(customCenter.Value);
			}

			b2Body_SetMassData(body, data);
		}

		private static void CollectColliders(GameObject go, List<Collider2D> results, bool root)
		{
			if (!root && go.GetComponent<Rigidbody2D>() != null)
			{
				return;
			}

			foreach (Behaviour component in go.Components)
			{
				if (component is Collider2D collider && !collider.destroyed)
				{
					results.Add(collider);
				}
			}

			if (go.transform == null)
			{
				return;
			}

			foreach (Transform child in go.transform.Children)
			{
				CollectColliders(child.gameObject, results, false);
			}
		}

		#endregion
	}
}
