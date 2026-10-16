using System;
using Box2D.NET;
using OpenTK.Mathematics;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2DistanceJoints;
using static Box2D.NET.B2Joints;
using static Box2D.NET.B2MathFunction;
using static Box2D.NET.B2MotorJoints;
using static Box2D.NET.B2PrismaticJoints;
using static Box2D.NET.B2RevoluteJoints;
using static Box2D.NET.B2WeldJoints;
using static Box2D.NET.B2WheelJoints;
using B2Vector2 = Box2D.NET.B2Vec2;

namespace LELEngine
{
	/// <summary>
	///     Base of the 2D joints: constrains this object's <see cref="Rigidbody2D" /> to <see cref="connectedBody" />
	///     (or to the world when null). The Box2D joint exists while both bodies do; it is rebuilt when a setting
	///     that defines it changes. A joint whose force reaches <see cref="breakForce" /> or whose torque reaches
	///     <see cref="breakTorque" /> breaks (Box2D's joint event thresholds): OnJointBreak2D is sent and the component
	///     is destroyed.
	/// </summary>
	public abstract class Joint2D : Behaviour
	{
		#region PublicFields

		[SerializeField] private Rigidbody2D m_ConnectedBody;

		[Tooltip("The two connected bodies may collide with each other.")]
		[SerializeField] private bool m_EnableCollision;

		public float breakForce = float.PositiveInfinity;
		public float breakTorque = float.PositiveInfinity;

		public Rigidbody2D connectedBody
		{
			get => m_ConnectedBody;
			set
			{
				m_ConnectedBody = value;
				dirty = true;
			}
		}

		public bool enableCollision
		{
			get => m_EnableCollision;
			set
			{
				m_EnableCollision = value;
				dirty = true;
			}
		}

		public Rigidbody2D attachedRigidbody => gameObject?.GetComponent<Rigidbody2D>();

		/// <summary>Force the joint applied in the last step (world units).</summary>
		public Vector2 reactionForce => hasJoint ? Physics2DMath.ToEngine(b2Joint_GetConstraintForce(joint)) : Vector2.Zero;

		public float reactionTorque => hasJoint ? b2Joint_GetConstraintTorque(joint) : 0f;

		/// <summary>The Box2D joint (a null id while not simulated).</summary>
		public B2JointId Box2DJoint => joint;

		#endregion

		#region InternalFields

		internal B2JointId joint;
		internal PhysicsScene2D world;
		internal bool dirty = true;
		internal bool runtimeDirty;

		/// <summary>The break force and torque the Box2D joint's event thresholds were last set to.</summary>
		internal float appliedBreakForce;
		internal float appliedBreakTorque;

		/// <summary>True while the Box2D joint exists.</summary>
		internal bool hasJoint => joint.index1 != 0;

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			Scene scene = gameObject?.scene;
			if (scene != null && scene.IsPlaying)
			{
				scene.Physics2D.AddJoint(this);
			}
		}

		public override void OnDisable()
		{
			world?.RemoveJoint(this);
		}

		public override void OnDestroy()
		{
			world?.RemoveJoint(this);
		}

		public override void OnValidate()
		{
			dirty = true;
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     Creates the Box2D joint. <paramref name="baseDef" /> carries the bodies (A = this object's body, B = the
		///     connected body or the world's ground body), the collide-connected flag and the user data.
		/// </summary>
		internal abstract B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef);

		/// <summary>Settings that change without recreating the joint (motors, limits).</summary>
		internal virtual void ApplyRuntimeSettings()
		{ }

		/// <summary>Marks settings that need the joint rebuilt.</summary>
		protected void Rebuild()
		{
			dirty = true;
		}

		/// <summary>Marks settings applied to the live joint before the next step.</summary>
		protected void Retune()
		{
			runtimeDirty = true;
		}

		/// <summary>World scale of this object along the simulation plane's axes (anchors are scaled like Unity does).</summary>
		protected Vector2 LocalScale(Transform t)
		{
			if (t == null)
			{
				return Vector2.One;
			}

			return (world?.Plane ?? PlaneBasis.Current).ScaleOf(t.lossyScale);
		}

		/// <summary>
		///     Box2D v3 joints are placed by a local frame on each body. Both frames are given the same world rotation
		///     (<paramref name="worldAngle" />, radians), so the joint starts at angle zero (Unity measures hinge angles
		///     from the creation pose) and its x axis (slider and wheel axes) points along that angle.
		/// </summary>
		protected static void SetFrames(ref B2JointDef def, B2Vector2 localA, B2Vector2 localB, float worldAngle)
		{
			B2Rot world = b2MakeRot(worldAngle);
			def.localFrameA = new B2Transform(localA, b2InvMulRot(b2Body_GetRotation(def.bodyIdA), world));
			def.localFrameB = new B2Transform(localB, b2InvMulRot(b2Body_GetRotation(def.bodyIdB), world));
		}

		/// <summary>The joint's frames in world space: frame A's origin and x axis, frame B's origin, and B's angle relative to A (radians).</summary>
		protected void WorldFrames(out B2Vector2 originA, out B2Vector2 axis, out B2Vector2 originB, out float angle)
		{
			B2Transform a = b2MulTransforms(b2Body_GetTransform(b2Joint_GetBodyA(joint)), b2Joint_GetLocalFrameA(joint));
			B2Transform b = b2MulTransforms(b2Body_GetTransform(b2Joint_GetBodyB(joint)), b2Joint_GetLocalFrameB(joint));
			originA = a.p;
			axis = new B2Vector2(a.q.c, a.q.s);
			originB = b.p;
			angle = b2Rot_GetAngle(b2InvMulRot(a.q, b.q));
		}

		#endregion
	}

	/// <summary>A joint with an anchor on each body (local space of each object; the connected anchor is in world space without a connected body).</summary>
	public abstract class AnchoredJoint2D : Joint2D
	{
		#region PublicFields

		[SerializeField] private Vector2 m_Anchor;
		[SerializeField] private Vector2 m_ConnectedAnchor;

		[Tooltip("The connected anchor is placed where the anchor is when the joint is created.")]
		[SerializeField] private bool m_AutoConfigureConnectedAnchor = true;

		public Vector2 anchor
		{
			get => m_Anchor;
			set
			{
				m_Anchor = value;
				Rebuild();
			}
		}

		public Vector2 connectedAnchor
		{
			get => m_ConnectedAnchor;
			set
			{
				m_ConnectedAnchor = value;
				Rebuild();
			}
		}

		public bool autoConfigureConnectedAnchor
		{
			get => m_AutoConfigureConnectedAnchor;
			set
			{
				m_AutoConfigureConnectedAnchor = value;
				Rebuild();
			}
		}

		#endregion

		#region InternalMethods

		/// <summary>Anchor A in body A's local frame and anchor B in body B's frame (resolving the auto configuration).</summary>
		protected void ResolveAnchors(B2BodyId bodyA, B2BodyId bodyB, out B2Vector2 localA, out B2Vector2 localB, out B2Vector2 worldA, out B2Vector2 worldB)
		{
			Vector2 scaleA = LocalScale(transform);
			localA = new B2Vector2(m_Anchor.X * scaleA.X, m_Anchor.Y * scaleA.Y);
			worldA = b2Body_GetWorldPoint(bodyA, localA);
			if (m_AutoConfigureConnectedAnchor)
			{
				worldB = worldA;
				localB = b2Body_GetLocalPoint(bodyB, worldB);
				// Keep the configured value readable in the inspector.
				m_ConnectedAnchor = connectedBody != null ? Physics2DMath.ToEngine(localB) : Physics2DMath.ToEngine(worldB);
				return;
			}

			if (connectedBody != null)
			{
				Vector2 scaleB = LocalScale(connectedBody.transform);
				localB = new B2Vector2(m_ConnectedAnchor.X * scaleB.X, m_ConnectedAnchor.Y * scaleB.Y);
				worldB = b2Body_GetWorldPoint(bodyB, localB);
			}
			else
			{
				worldB = Physics2DMath.ToB2(m_ConnectedAnchor);
				localB = b2Body_GetLocalPoint(bodyB, worldB);
			}
		}

		#endregion
	}

	/// <summary>Keeps the anchors at a fixed distance (or at most that distance).</summary>
	public sealed class DistanceJoint2D : AnchoredJoint2D
	{
		#region PublicFields

		[Tooltip("The distance is measured between the anchors when the joint is created.")]
		public bool autoConfigureDistance = true;

		public float distance = 1f;

		[Tooltip("Only limits the maximum distance (a rope); off: a rigid rod.")]
		public bool maxDistanceOnly;

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(baseDef.bodyIdA, baseDef.bodyIdB, out localA, out localB, out worldA, out worldB);
			if (autoConfigureDistance)
			{
				distance = Math.Max(0.005f, MathF.Sqrt(Physics2DMath.DistanceSquared(worldA, worldB)));
			}

			float length = Math.Max(0.005f, distance);
			B2DistanceJointDef def = b2DefaultDistanceJointDef();
			def.@base = baseDef;
			SetFrames(ref def.@base, localA, localB, 0f);
			def.length = length;
			if (maxDistanceOnly)
			{
				// A rope: the spring is on but without stiffness, so only the upper limit holds.
				def.enableSpring = true;
				def.hertz = 0f;
				def.enableLimit = true;
				def.minLength = 0f;
				def.maxLength = length;
			}

			return b2CreateDistanceJoint(world.World, def);
		}

		#endregion
	}

	/// <summary>A spring between the anchors (frequency and damping) with a rest distance.</summary>
	public sealed class SpringJoint2D : AnchoredJoint2D
	{
		#region PublicFields

		public bool autoConfigureDistance = true;
		public float distance = 1f;

		[Range(0f, 1f)] public float dampingRatio;

		[Tooltip("Spring oscillation frequency in Hz (0 = rigid).")]
		public float frequency = 1f;

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(baseDef.bodyIdA, baseDef.bodyIdB, out localA, out localB, out worldA, out worldB);
			if (autoConfigureDistance)
			{
				distance = Math.Max(0.005f, MathF.Sqrt(Physics2DMath.DistanceSquared(worldA, worldB)));
			}

			B2DistanceJointDef def = b2DefaultDistanceJointDef();
			def.@base = baseDef;
			SetFrames(ref def.@base, localA, localB, 0f);
			def.length = Math.Max(0.005f, distance);
			def.enableSpring = frequency > 0f;
			def.hertz = Math.Max(0f, frequency);
			def.dampingRatio = dampingRatio;
			return b2CreateDistanceJoint(world.World, def);
		}

		#endregion
	}

	/// <summary>Rotation about the anchor, optionally motorized and limited.</summary>
	public sealed class HingeJoint2D : AnchoredJoint2D
	{
		#region PublicFields

		public bool useMotor;
		public JointMotor2D motor;
		public bool useLimits;

		[Tooltip("Angle limits in degrees relative to the starting angle.")]
		public JointAngleLimits2D limits = new JointAngleLimits2D { min = -45f, max = 45f };

		/// <summary>Current angle in degrees.</summary>
		public float jointAngle => hasJoint ? b2RevoluteJoint_GetAngle(joint) * Physics2DMath.Rad2Deg : 0f;

		/// <summary>Current angular speed in degrees per second.</summary>
		public float jointSpeed => hasJoint ? (b2Body_GetAngularVelocity(b2Joint_GetBodyB(joint)) - b2Body_GetAngularVelocity(b2Joint_GetBodyA(joint))) * Physics2DMath.Rad2Deg : 0f;

		#endregion

		#region PublicMethods

		public float GetMotorTorque(float timeStep)
		{
			return hasJoint ? b2RevoluteJoint_GetMotorTorque(joint) : 0f;
		}

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(baseDef.bodyIdA, baseDef.bodyIdB, out localA, out localB, out worldA, out worldB);
			B2RevoluteJointDef def = b2DefaultRevoluteJointDef();
			def.@base = baseDef;
			SetFrames(ref def.@base, localA, localB, 0f);
			def.enableLimit = useLimits;
			def.lowerAngle = LowerLimit();
			def.upperAngle = UpperLimit();
			def.enableMotor = useMotor;
			def.motorSpeed = motor.motorSpeed * Physics2DMath.Deg2Rad;
			def.maxMotorTorque = motor.maxMotorTorque;
			return b2CreateRevoluteJoint(world.World, def);
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!hasJoint)
			{
				return;
			}

			b2RevoluteJoint_EnableLimit(joint, useLimits);
			b2RevoluteJoint_SetLimits(joint, LowerLimit(), UpperLimit());
			b2RevoluteJoint_EnableMotor(joint, useMotor);
			b2RevoluteJoint_SetMotorSpeed(joint, motor.motorSpeed * Physics2DMath.Deg2Rad);
			b2RevoluteJoint_SetMaxMotorTorque(joint, motor.maxMotorTorque);
			b2Joint_WakeBodies(joint);
		}

		#endregion

		#region PrivateMethods

		// Box2D v3 keeps revolute limits within ±0.99π.
		private float LowerLimit()
		{
			return Math.Clamp(Math.Min(limits.min, limits.max) * Physics2DMath.Deg2Rad, -0.99f * MathF.PI, 0.99f * MathF.PI);
		}

		private float UpperLimit()
		{
			return Math.Clamp(Math.Max(limits.min, limits.max) * Physics2DMath.Deg2Rad, -0.99f * MathF.PI, 0.99f * MathF.PI);
		}

		#endregion
	}

	/// <summary>Translation along an axis (angle in degrees), optionally motorized and limited.</summary>
	public sealed class SliderJoint2D : AnchoredJoint2D
	{
		#region PublicFields

		[Tooltip("Direction of the slide in degrees (0 = along X).")]
		public float angle;

		public bool autoConfigureAngle = true;
		public bool useMotor;
		public JointMotor2D motor;
		public bool useLimits;
		public JointTranslationLimits2D limits = new JointTranslationLimits2D { min = -1f, max = 1f };

		public float jointTranslation => hasJoint ? b2PrismaticJoint_GetTranslation(joint) : 0f;
		public float jointSpeed => hasJoint ? b2PrismaticJoint_GetSpeed(joint) : 0f;

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(baseDef.bodyIdA, baseDef.bodyIdB, out localA, out localB, out worldA, out worldB);
			if (autoConfigureAngle)
			{
				B2Vector2 delta = worldB - worldA;
				if (delta.X * delta.X + delta.Y * delta.Y > 1e-8f)
				{
					angle = MathF.Atan2(delta.Y, delta.X) * Physics2DMath.Rad2Deg;
				}
			}

			B2PrismaticJointDef def = b2DefaultPrismaticJointDef();
			def.@base = baseDef;
			SetFrames(ref def.@base, localA, localB, angle * Physics2DMath.Deg2Rad);
			def.enableLimit = useLimits;
			def.lowerTranslation = Math.Min(limits.min, limits.max);
			def.upperTranslation = Math.Max(limits.min, limits.max);
			def.enableMotor = useMotor;
			def.motorSpeed = motor.motorSpeed;
			def.maxMotorForce = motor.maxMotorTorque;
			return b2CreatePrismaticJoint(world.World, def);
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!hasJoint)
			{
				return;
			}

			b2PrismaticJoint_EnableLimit(joint, useLimits);
			b2PrismaticJoint_SetLimits(joint, Math.Min(limits.min, limits.max), Math.Max(limits.min, limits.max));
			b2PrismaticJoint_EnableMotor(joint, useMotor);
			b2PrismaticJoint_SetMotorSpeed(joint, motor.motorSpeed);
			b2PrismaticJoint_SetMaxMotorForce(joint, motor.maxMotorTorque);
			b2Joint_WakeBodies(joint);
		}

		#endregion
	}

	/// <summary>Welds the bodies together (optionally with a little give: frequency and damping).</summary>
	public sealed class FixedJoint2D : AnchoredJoint2D
	{
		#region PublicFields

		[Range(0f, 1f)] public float dampingRatio;

		[Tooltip("Softness frequency in Hz (0 = rigid).")]
		public float frequency;

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(baseDef.bodyIdA, baseDef.bodyIdB, out localA, out localB, out worldA, out worldB);
			B2WeldJointDef def = b2DefaultWeldJointDef();
			def.@base = baseDef;
			SetFrames(ref def.@base, localA, localB, 0f);
			float hertz = Math.Max(0f, frequency);
			def.linearHertz = hertz;
			def.angularHertz = hertz;
			def.linearDampingRatio = dampingRatio;
			def.angularDampingRatio = dampingRatio;
			return b2CreateWeldJoint(world.World, def);
		}

		#endregion
	}

	/// <summary>A wheel: rotates freely about the anchor and moves along a suspension axis with a spring.</summary>
	public sealed class WheelJoint2D : AnchoredJoint2D
	{
		#region PublicFields

		public JointSuspension2D suspension = new JointSuspension2D { dampingRatio = 0.7f, frequency = 2f, angle = 90f };
		public bool useMotor;
		public JointMotor2D motor;

		/// <summary>Position of the wheel along the suspension axis.</summary>
		public float jointTranslation
		{
			get
			{
				if (!hasJoint)
				{
					return 0f;
				}

				WorldFrames(out B2Vector2 originA, out B2Vector2 axis, out B2Vector2 originB, out float _);
				return b2Dot(originB - originA, axis);
			}
		}

		/// <summary>Speed of the wheel along the suspension axis.</summary>
		public float jointLinearSpeed
		{
			get
			{
				if (!hasJoint)
				{
					return 0f;
				}

				WorldFrames(out B2Vector2 originA, out B2Vector2 axis, out B2Vector2 originB, out float _);
				B2Vector2 relative = b2Body_GetWorldPointVelocity(b2Joint_GetBodyB(joint), originB) - b2Body_GetWorldPointVelocity(b2Joint_GetBodyA(joint), originB);
				return b2Dot(relative, axis);
			}
		}

		/// <summary>Angle of the wheel in degrees.</summary>
		public float jointAngle
		{
			get
			{
				if (!hasJoint)
				{
					return 0f;
				}

				WorldFrames(out B2Vector2 _, out B2Vector2 _, out B2Vector2 _, out float angle);
				return angle * Physics2DMath.Rad2Deg;
			}
		}

		/// <summary>Angular speed of the wheel in degrees per second.</summary>
		public float jointSpeed => hasJoint ? (b2Body_GetAngularVelocity(b2Joint_GetBodyB(joint)) - b2Body_GetAngularVelocity(b2Joint_GetBodyA(joint))) * Physics2DMath.Rad2Deg : 0f;

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(baseDef.bodyIdA, baseDef.bodyIdB, out localA, out localB, out worldA, out worldB);
			B2WheelJointDef def = b2DefaultWheelJointDef();
			def.@base = baseDef;
			// The frames' x axis is the suspension axis.
			SetFrames(ref def.@base, localA, localB, suspension.angle * Physics2DMath.Deg2Rad);
			def.enableSpring = suspension.frequency > 0f;
			def.hertz = Math.Max(0f, suspension.frequency);
			def.dampingRatio = suspension.dampingRatio;
			def.enableMotor = useMotor;
			def.motorSpeed = motor.motorSpeed * Physics2DMath.Deg2Rad;
			def.maxMotorTorque = motor.maxMotorTorque;
			return b2CreateWheelJoint(world.World, def);
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!hasJoint)
			{
				return;
			}

			b2WheelJoint_EnableSpring(joint, suspension.frequency > 0f);
			b2WheelJoint_SetSpringHertz(joint, Math.Max(0f, suspension.frequency));
			b2WheelJoint_SetSpringDampingRatio(joint, suspension.dampingRatio);
			b2WheelJoint_EnableMotor(joint, useMotor);
			b2WheelJoint_SetMotorSpeed(joint, motor.motorSpeed * Physics2DMath.Deg2Rad);
			b2WheelJoint_SetMaxMotorTorque(joint, motor.maxMotorTorque);
			b2Joint_WakeBodies(joint);
		}

		#endregion
	}

	/// <summary>Resists relative motion between the bodies (top-down friction): a Box2D motor joint driving the relative velocity to zero.</summary>
	public sealed class FrictionJoint2D : AnchoredJoint2D
	{
		#region PublicFields

		public float maxForce = 1f;
		public float maxTorque = 1f;

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(baseDef.bodyIdA, baseDef.bodyIdB, out localA, out localB, out worldA, out worldB);
			B2MotorJointDef def = b2DefaultMotorJointDef();
			def.@base = baseDef;
			SetFrames(ref def.@base, localA, localB, 0f);
			def.maxVelocityForce = Math.Max(0f, maxForce);
			def.maxVelocityTorque = Math.Max(0f, maxTorque);
			return b2CreateMotorJoint(world.World, def);
		}

		internal override void ApplyRuntimeSettings()
		{
			if (hasJoint)
			{
				b2MotorJoint_SetMaxVelocityForce(joint, Math.Max(0f, maxForce));
				b2MotorJoint_SetMaxVelocityTorque(joint, Math.Max(0f, maxTorque));
			}
		}

		#endregion
	}

	/// <summary>
	///     Keeps a relative position and angle between the bodies: a Box2D motor joint whose linear and angular springs
	///     pull the connected body to the offset, with at most <see cref="maxForce" /> / <see cref="maxTorque" />.
	/// </summary>
	public sealed class RelativeJoint2D : Joint2D
	{
		#region PublicFields

		public float maxForce = 10000f;
		public float maxTorque = 10000f;

		[Tooltip("Stiffness of the pull towards the offset, in Hz.")]
		public float frequency = 3f;

		[Range(0f, 2f)] public float dampingRatio = 1f;

		[Tooltip("The offsets are taken from the bodies' relative placement when the joint is created.")]
		public bool autoConfigureOffset = true;

		public Vector2 linearOffset;

		/// <summary>Angular offset in degrees.</summary>
		public float angularOffset;

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			if (autoConfigureOffset)
			{
				linearOffset = Physics2DMath.ToEngine(b2Body_GetLocalPoint(baseDef.bodyIdA, b2Body_GetPosition(baseDef.bodyIdB)));
				angularOffset = b2Rot_GetAngle(b2InvMulRot(b2Body_GetRotation(baseDef.bodyIdA), b2Body_GetRotation(baseDef.bodyIdB))) * Physics2DMath.Rad2Deg;
			}

			B2MotorJointDef def = b2DefaultMotorJointDef();
			def.@base = baseDef;
			def.@base.localFrameA = OffsetFrame();
			def.@base.localFrameB = b2Transform_identity;
			Tune(ref def);
			return b2CreateMotorJoint(world.World, def);
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!hasJoint)
			{
				return;
			}

			B2MotorJointDef def = default;
			Tune(ref def);
			b2Joint_SetLocalFrameA(joint, OffsetFrame());
			b2MotorJoint_SetLinearHertz(joint, def.linearHertz);
			b2MotorJoint_SetAngularHertz(joint, def.angularHertz);
			b2MotorJoint_SetLinearDampingRatio(joint, def.linearDampingRatio);
			b2MotorJoint_SetAngularDampingRatio(joint, def.angularDampingRatio);
			b2MotorJoint_SetMaxSpringForce(joint, def.maxSpringForce);
			b2MotorJoint_SetMaxSpringTorque(joint, def.maxSpringTorque);
			b2Joint_WakeBodies(joint);
		}

		#endregion

		#region PrivateMethods

		private B2Transform OffsetFrame()
		{
			return new B2Transform(Physics2DMath.ToB2(linearOffset), b2MakeRot(angularOffset * Physics2DMath.Deg2Rad));
		}

		private void Tune(ref B2MotorJointDef def)
		{
			float hertz = Math.Max(0.01f, frequency);
			def.linearHertz = hertz;
			def.angularHertz = hertz;
			def.linearDampingRatio = Math.Max(0f, dampingRatio);
			def.angularDampingRatio = Math.Max(0f, dampingRatio);
			def.maxSpringForce = Math.Max(0f, maxForce);
			def.maxSpringTorque = Math.Max(0f, maxTorque);
		}

		#endregion
	}

	/// <summary>Pulls the anchor towards a world target with a spring of at most <see cref="maxForce" /> (a Box2D motor joint from the ground).</summary>
	public sealed class TargetJoint2D : Joint2D
	{
		#region PublicFields

		[Tooltip("Point of this object (local space) pulled towards the target.")]
		public Vector2 anchor;

		public bool autoConfigureTarget = true;
		public Vector2 target;

		[Tooltip("The most force the spring pulls with, in newtons.")]
		public float maxForce = 1000f;

		[Range(0f, 1f)] public float dampingRatio = 1f;
		public float frequency = 5f;

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override B2JointId CreateJoint(PhysicsScene2D world, B2JointDef baseDef)
		{
			// The ground holds the target and this body is pulled: the ground is A, this body B.
			B2BodyId body = baseDef.bodyIdA;
			Vector2 scale = LocalScale(transform);
			var local = new B2Vector2(anchor.X * scale.X, anchor.Y * scale.Y);
			if (autoConfigureTarget)
			{
				target = Physics2DMath.ToEngine(b2Body_GetWorldPoint(body, local));
			}

			B2MotorJointDef def = b2DefaultMotorJointDef();
			def.@base = baseDef;
			def.@base.bodyIdA = world.GroundBody;
			def.@base.bodyIdB = body;
			def.@base.localFrameA = new B2Transform(Physics2DMath.ToB2(target), b2Rot_identity);
			def.@base.localFrameB = new B2Transform(local, b2Rot_identity);
			def.linearHertz = Math.Max(0.01f, frequency);
			def.linearDampingRatio = dampingRatio;
			def.maxSpringForce = Math.Max(0f, maxForce);
			return b2CreateMotorJoint(world.World, def);
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!hasJoint)
			{
				return;
			}

			b2Joint_SetLocalFrameA(joint, new B2Transform(Physics2DMath.ToB2(target), b2Rot_identity));
			b2MotorJoint_SetLinearHertz(joint, Math.Max(0.01f, frequency));
			b2MotorJoint_SetLinearDampingRatio(joint, dampingRatio);
			b2MotorJoint_SetMaxSpringForce(joint, Math.Max(0f, maxForce));
			b2Joint_WakeBodies(joint);
		}

		#endregion
	}
}
