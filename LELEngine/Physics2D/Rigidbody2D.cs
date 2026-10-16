using System;
using System.Collections.Generic;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;
using OpenTK.Mathematics;
using B2Vector2 = System.Numerics.Vector2;

namespace LELEngine
{
	/// <summary>
	///     A body in the scene's 2D physics world (Box2D): the colliders of this object and of its children without a
	///     rigidbody of their own become its shapes. Dynamic bodies move under forces, gravity and collisions in the
	///     XY plane; the transform follows the body after every physics step (its Z position and non-Z rotation are
	///     kept). Moving the transform directly teleports the body. API modelled on Unity's Rigidbody2D.
	/// </summary>
	public sealed class Rigidbody2D : Behaviour
	{
		#region PublicFields

		public RigidbodyType2D bodyType = RigidbodyType2D.Dynamic;
		public PhysicsMaterial2D sharedMaterial;

		/// <summary>Off: the body and its colliders are removed from the simulation (no collisions, no movement).</summary>
		public bool simulated = true;

		/// <summary>Mass from the colliders' densities; off: <see cref="mass" /> is used.</summary>
		public bool useAutoMass = true;

		[Tooltip("Mass in kilograms when Use Auto Mass is off.")]
		public float mass = 1f;

		[Tooltip("Velocity lost per second (0 = none).")]
		public float linearDamping;

		public float angularDamping = 0.05f;
		public float gravityScale = 1f;
		public CollisionDetectionMode2D collisionDetectionMode = CollisionDetectionMode2D.Discrete;
		public RigidbodySleepMode2D sleepMode = RigidbodySleepMode2D.StartAwake;
		public RigidbodyInterpolation2D interpolation = RigidbodyInterpolation2D.None;
		public RigidbodyConstraints2D constraints = RigidbodyConstraints2D.None;

		// ---- runtime state

		/// <summary>Linear velocity in units per second.</summary>
		public Vector2 velocity
		{
			get => body != null ? Physics2DMath.ToEngine(body.LinearVelocity) : initialVelocity;
			set
			{
				initialVelocity = value;
				if (body != null)
				{
					B2Vector2 v = Physics2DMath.ToB2(value);
					body.SetLinearVelocity(ref v);
					body.IsAwake = true;
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
			set
			{
				linearDamping = value;
				settingsDirty = true;
			}
		}

		public float angularDrag
		{
			get => angularDamping;
			set
			{
				angularDamping = value;
				settingsDirty = true;
			}
		}

		/// <summary>Angular velocity in degrees per second.</summary>
		public float angularVelocity
		{
			get => body != null ? body.AngularVelocity * Physics2DMath.Rad2Deg : initialAngularVelocity;
			set
			{
				initialAngularVelocity = value;
				if (body != null)
				{
					body.SetAngularVelocity(value * Physics2DMath.Deg2Rad);
					body.IsAwake = true;
				}
			}
		}

		/// <summary>Position of the body (its transform's XY); setting it teleports.</summary>
		public Vector2 position
		{
			get => body != null ? Physics2DMath.ToEngine(body.GetPosition()) : transform != null ? transform.position.Xy : Vector2.Zero;
			set
			{
				if (body != null)
				{
					B2Vector2 p = Physics2DMath.ToB2(value);
					body.SetTransform(ref p, body.GetAngle());
					WriteTransform(value, body.GetAngle());
					currentPosition = previousPosition = value;
				}
				else if (transform != null)
				{
					transform.position = new Vector3(value.X, value.Y, transform.position.Z);
				}
			}
		}

		/// <summary>Rotation about Z in degrees.</summary>
		public float rotation
		{
			get => body != null ? body.GetAngle() * Physics2DMath.Rad2Deg : transform != null ? Physics2DMath.AngleOf(transform.rotation) * Physics2DMath.Rad2Deg : 0f;
			set
			{
				float angle = value * Physics2DMath.Deg2Rad;
				if (body != null)
				{
					B2Vector2 p = body.GetPosition();
					body.SetTransform(ref p, angle);
					WriteTransform(Physics2DMath.ToEngine(p), angle);
					currentAngle = previousAngle = angle;
				}
				else if (transform != null)
				{
					baseRotation = Physics2DMath.RemoveZ(transform.rotation);
					transform.rotation = Physics2DMath.WithZ(baseRotation, angle);
				}
			}
		}

		public float inertia => body?.Inertia ?? 0f;
		public Vector2 worldCenterOfMass => body != null ? Physics2DMath.ToEngine(body.GetWorldCenter()) : position;

		/// <summary>Center of mass in the body's local space; set to override the computed one.</summary>
		public Vector2 centerOfMass
		{
			get => body != null ? Physics2DMath.ToEngine(body.GetLocalCenter()) : customCenter ?? Vector2.Zero;
			set
			{
				customCenter = value;
				MassChanged();
			}
		}

		public bool freezeRotation
		{
			get => (constraints & RigidbodyConstraints2D.FreezeRotation) != 0;
			set
			{
				constraints = value ? constraints | RigidbodyConstraints2D.FreezeRotation : constraints & ~RigidbodyConstraints2D.FreezeRotation;
				settingsDirty = true;
			}
		}

		public bool isKinematic
		{
			get => bodyType == RigidbodyType2D.Kinematic;
			set
			{
				bodyType = value ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
				settingsDirty = true;
			}
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

		/// <summary>The Box2D body, for code that needs the engine underneath (null while not simulated).</summary>
		public Body Box2DBody => body;

		#endregion

		#region InternalFields

		internal Body body;
		internal PhysicsScene2D world;

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
		private Vector2 frozenPosition;
		private Vector2? moveTarget;
		private float? moveAngle;
		private bool gravityScaleApplied;
		private readonly List<Collider2D> colliderBuffer = new List<Collider2D>();

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

		public override void OnValidate()
		{
			settingsDirty = true;
			massDirty = true;
		}

		#endregion

		#region PublicMethods

		public bool IsSleeping()
		{
			return body != null && !body.IsAwake;
		}

		public bool IsAwake()
		{
			return body != null && body.IsAwake;
		}

		public void Sleep()
		{
			if (body != null)
			{
				body.IsAwake = false;
			}
		}

		public void WakeUp()
		{
			if (body != null)
			{
				body.IsAwake = true;
			}
		}

		public void AddForce(Vector2 force, ForceMode2D mode = ForceMode2D.Force)
		{
			if (body == null)
			{
				return;
			}

			B2Vector2 f = Physics2DMath.ToB2(force);
			if (mode == ForceMode2D.Impulse) body.ApplyLinearImpulseToCenter(ref f, true);
			else body.ApplyForceToCenter(ref f, true);
		}

		public void AddForceAtPosition(Vector2 force, Vector2 worldPosition, ForceMode2D mode = ForceMode2D.Force)
		{
			if (body == null)
			{
				return;
			}

			B2Vector2 f = Physics2DMath.ToB2(force);
			B2Vector2 p = Physics2DMath.ToB2(worldPosition);
			if (mode == ForceMode2D.Impulse) body.ApplyLinearImpulse(ref f, ref p, true);
			else body.ApplyForce(ref f, ref p, true);
		}

		/// <summary>Force in the body's local axes.</summary>
		public void AddRelativeForce(Vector2 relativeForce, ForceMode2D mode = ForceMode2D.Force)
		{
			if (body == null)
			{
				return;
			}

			B2Vector2 local = Physics2DMath.ToB2(relativeForce);
			B2Vector2 f = body.GetWorldVector(ref local);
			if (mode == ForceMode2D.Impulse) body.ApplyLinearImpulseToCenter(ref f, true);
			else body.ApplyForceToCenter(ref f, true);
		}

		public void AddTorque(float torque, ForceMode2D mode = ForceMode2D.Force)
		{
			if (body == null)
			{
				return;
			}

			if (mode == ForceMode2D.Impulse) body.ApplyAngularImpulse(torque, true);
			else body.ApplyTorque(torque, true);
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
			if (body == null)
			{
				return worldPoint - position;
			}

			B2Vector2 p = Physics2DMath.ToB2(worldPoint);
			return Physics2DMath.ToEngine(body.GetLocalPoint(ref p));
		}

		/// <summary>A local point in world space.</summary>
		public Vector2 GetRelativePoint(Vector2 localPoint)
		{
			if (body == null)
			{
				return position + localPoint;
			}

			B2Vector2 p = Physics2DMath.ToB2(localPoint);
			return Physics2DMath.ToEngine(body.GetWorldPoint(ref p));
		}

		public Vector2 GetVector(Vector2 worldVector)
		{
			if (body == null)
			{
				return worldVector;
			}

			B2Vector2 v = Physics2DMath.ToB2(worldVector);
			return Physics2DMath.ToEngine(body.GetLocalVector(ref v));
		}

		public Vector2 GetRelativeVector(Vector2 localVector)
		{
			if (body == null)
			{
				return localVector;
			}

			B2Vector2 v = Physics2DMath.ToB2(localVector);
			return Physics2DMath.ToEngine(body.GetWorldVector(ref v));
		}

		public Vector2 GetPointVelocity(Vector2 worldPoint)
		{
			if (body == null)
			{
				return velocity;
			}

			B2Vector2 p = Physics2DMath.ToB2(worldPoint);
			return Physics2DMath.ToEngine(body.GetLinearVelocityFromWorldPoint(ref p));
		}

		public Vector2 GetRelativePointVelocity(Vector2 localPoint)
		{
			if (body == null)
			{
				return velocity;
			}

			B2Vector2 p = Physics2DMath.ToB2(localPoint);
			return Physics2DMath.ToEngine(body.GetLinearVelocityFromLocalPoint(ref p));
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
			baseRotation = Physics2DMath.RemoveZ(transform.rotation);
			lastWrittenPosition = transform.position;
			lastWrittenRotation = transform.rotation;
			previousPosition = currentPosition = position;
			previousAngle = currentAngle = angle;
			frozenPosition = position;
			if (initialVelocity != Vector2.Zero)
			{
				B2Vector2 v = Physics2DMath.ToB2(initialVelocity);
				body.SetLinearVelocity(ref v);
			}

			if (initialAngularVelocity != 0f)
			{
				body.SetAngularVelocity(initialAngularVelocity * Physics2DMath.Deg2Rad);
			}

			settingsDirty = false;
			massDirty = !useAutoMass || customCenter.HasValue;
		}

		/// <summary>Pushes the serialized settings into the body (creation, inspector edits, property setters).</summary>
		internal void ApplyBodySettings()
		{
			if (body == null)
			{
				return;
			}

			BodyType type = bodyType == RigidbodyType2D.Kinematic ? BodyType.KinematicBody : bodyType == RigidbodyType2D.Static ? BodyType.StaticBody : BodyType.DynamicBody;
			if (body.BodyType != type)
			{
				body.BodyType = type;
			}

			body.LinearDamping = linearDamping;
			body.AngularDamping = angularDamping;
			body.IsSleepingAllowed = sleepMode != RigidbodySleepMode2D.NeverSleep;
			body.IsFixedRotation = (constraints & RigidbodyConstraints2D.FreezeRotation) != 0;
			body.IsBullet = collisionDetectionMode == CollisionDetectionMode2D.Continuous;
			if (body.IsEnabled != simulated)
			{
				body.IsEnabled = simulated;
			}

			gravityScaleApplied = Box2DAccess.SetGravityScale(body, gravityScale);
			settingsDirty = false;
			if (massDirty)
			{
				ApplyMass();
			}
		}

		internal void MassChanged()
		{
			massDirty = true;
			if (body != null && world != null && !world.IsStepping)
			{
				ApplyMass();
			}
		}

		/// <summary>Before a step: transform edits, settings and kinematic moves reach the body.</summary>
		internal void SyncToBody()
		{
			if (body == null || transform == null)
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
			bool rotated = !rotated_Equal(rotation, lastWrittenRotation);
			if (moved || rotated)
			{
				// The transform was moved by a script or the editor: teleport the body.
				baseRotation = Physics2DMath.RemoveZ(rotation);
				float angle = Physics2DMath.AngleOf(rotation);
				B2Vector2 p = new B2Vector2(position.X, position.Y);
				body.SetTransform(ref p, angle);
				body.IsAwake = true;
				lastWrittenPosition = position;
				lastWrittenRotation = rotation;
				previousPosition = currentPosition = position.Xy;
				previousAngle = currentAngle = angle;
				frozenPosition = position.Xy;
			}
		}

		internal void BeforeStep(float deltaTime)
		{
			if (body == null)
			{
				return;
			}

			previousPosition = Physics2DMath.ToEngine(body.GetPosition());
			previousAngle = body.GetAngle();

			if (bodyType == RigidbodyType2D.Kinematic)
			{
				if (moveTarget.HasValue)
				{
					B2Vector2 v = Physics2DMath.ToB2((moveTarget.Value - previousPosition) / deltaTime);
					body.SetLinearVelocity(ref v);
				}

				if (moveAngle.HasValue)
				{
					float delta = moveAngle.Value - previousAngle;
					while (delta > MathF.PI) delta -= 2f * MathF.PI;
					while (delta < -MathF.PI) delta += 2f * MathF.PI;
					body.SetAngularVelocity(delta / deltaTime);
				}

				if (moveTarget.HasValue || moveAngle.HasValue)
				{
					body.IsAwake = true;
				}
			}
			else if (body.BodyType == BodyType.DynamicBody && gravityScale != 1f && body.IsAwake)
			{
				if (gravityScaleApplied && Math.Abs(Box2DAccess.GetGravityScale(body) - gravityScale) > 1e-6f)
				{
					Box2DAccess.SetGravityScale(body, gravityScale);
				}
				else if (!gravityScaleApplied)
				{
					// The port hides the gravity scale: the difference is applied as a force.
					B2Vector2 gravity = world.World.Gravity * (body.Mass * (gravityScale - 1f));
					body.ApplyForceToCenter(ref gravity, false);
				}
			}
		}

		internal void AfterStep(float deltaTime)
		{
			if (body == null || transform == null)
			{
				return;
			}

			if (bodyType == RigidbodyType2D.Kinematic)
			{
				if (moveTarget.HasValue)
				{
					B2Vector2 zero = B2Vector2.Zero;
					body.SetLinearVelocity(ref zero);
					moveTarget = null;
				}

				if (moveAngle.HasValue)
				{
					body.SetAngularVelocity(0f);
					moveAngle = null;
				}
			}

			B2Vector2 p = body.GetPosition();
			bool freezeX = (constraints & RigidbodyConstraints2D.FreezePositionX) != 0;
			bool freezeY = (constraints & RigidbodyConstraints2D.FreezePositionY) != 0;
			if (freezeX || freezeY)
			{
				B2Vector2 v = body.LinearVelocity;
				if (freezeX)
				{
					p.X = frozenPosition.X;
					v.X = 0f;
				}

				if (freezeY)
				{
					p.Y = frozenPosition.Y;
					v.Y = 0f;
				}

				body.SetTransform(ref p, body.GetAngle());
				body.SetLinearVelocity(ref v);
			}
			else
			{
				frozenPosition = Physics2DMath.ToEngine(p);
			}

			currentPosition = Physics2DMath.ToEngine(p);
			currentAngle = body.GetAngle();
			if (interpolation == RigidbodyInterpolation2D.None || bodyType == RigidbodyType2D.Static)
			{
				WriteTransform(currentPosition, currentAngle);
			}
		}

		internal void Interpolate(float alpha)
		{
			if (body == null || transform == null || bodyType == RigidbodyType2D.Static)
			{
				return;
			}

			alpha = Math.Clamp(alpha, 0f, 1f);
			Vector2 position;
			float angle;
			if (interpolation == RigidbodyInterpolation2D.Extrapolate)
			{
				float ahead = alpha * (world?.LastStepDeltaTime ?? 0.02f);
				position = currentPosition + Physics2DMath.ToEngine(body.LinearVelocity) * ahead;
				angle = currentAngle + body.AngularVelocity * ahead;
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

		private static bool rotated_Equal(Quaternion a, Quaternion b)
		{
			return Math.Abs(a.X - b.X) < 1e-6f && Math.Abs(a.Y - b.Y) < 1e-6f && Math.Abs(a.Z - b.Z) < 1e-6f && Math.Abs(a.W - b.W) < 1e-6f;
		}

		private void WriteTransform(Vector2 position, float angle)
		{
			Vector3 current = transform.position;
			var world = new Vector3(position.X, position.Y, current.Z);
			transform.position = world;
			transform.rotation = Physics2DMath.WithZ(baseRotation, angle);
			lastWrittenPosition = transform.position;
			lastWrittenRotation = transform.rotation;
		}

		private void ApplyMass()
		{
			massDirty = false;
			if (body == null || body.BodyType != BodyType.DynamicBody)
			{
				return;
			}

			Box2DAccess.ResetMassData(body);
			if (useAutoMass && !customCenter.HasValue)
			{
				return;
			}

			MassData data;
			body.GetMassData(out data);
			if (!useAutoMass)
			{
				float target = Math.Max(0.0001f, mass);
				float factor = data.Mass > 0f ? target / data.Mass : 1f;
				data.RotationInertia = data.Mass > 0f ? data.RotationInertia * factor : target * 0.1f;
				data.Mass = target;
			}

			if (customCenter.HasValue)
			{
				data.Center = Physics2DMath.ToB2(customCenter.Value);
			}

			body.SetMassData(ref data);
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
