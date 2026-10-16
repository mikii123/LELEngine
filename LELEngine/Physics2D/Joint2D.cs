using System;
using Box2DSharp.Dynamics;
using Box2DSharp.Dynamics.Joints;
using OpenTK.Mathematics;
using B2Vector2 = System.Numerics.Vector2;

namespace LELEngine
{
	/// <summary>
	///     Base of the 2D joints: constrains this object's <see cref="Rigidbody2D" /> to <see cref="connectedBody" />
	///     (or to the world when null). The Box2D joint exists while both bodies do; it is rebuilt when a setting
	///     that defines it changes. A joint whose reaction exceeds <see cref="breakForce" /> / <see cref="breakTorque" />
	///     breaks: OnJointBreak2D is sent and the component is destroyed.
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
		public Vector2 reactionForce => joint != null && world != null ? Physics2DMath.ToEngine(joint.GetReactionForce(1f / world.LastStepDeltaTime)) : Vector2.Zero;

		public float reactionTorque => joint != null && world != null ? joint.GetReactionTorque(1f / world.LastStepDeltaTime) : 0f;

		/// <summary>The Box2D joint (null while not simulated).</summary>
		public Joint Box2DJoint => joint;

		#endregion

		#region InternalFields

		internal Joint joint;
		internal PhysicsScene2D world;
		internal bool dirty = true;
		internal bool runtimeDirty;

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

		/// <summary>The Box2D definition; BodyA / BodyB / CollideConnected / UserData are filled by the world.</summary>
		internal abstract JointDef CreateDefinition(Body bodyA, Body bodyB);

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

		/// <summary>World scale of this object's XY axes (anchors are scaled like Unity does).</summary>
		protected Vector2 LocalScale(Transform t)
		{
			if (t == null)
			{
				return Vector2.One;
			}

			Vector3 scale = t.lossyScale;
			return new Vector2(scale.X, scale.Y);
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
		protected void ResolveAnchors(Body bodyA, Body bodyB, out B2Vector2 localA, out B2Vector2 localB, out B2Vector2 worldA, out B2Vector2 worldB)
		{
			Vector2 scaleA = LocalScale(transform);
			localA = new B2Vector2(m_Anchor.X * scaleA.X, m_Anchor.Y * scaleA.Y);
			worldA = bodyA.GetWorldPoint(ref localA);
			if (m_AutoConfigureConnectedAnchor)
			{
				worldB = worldA;
				localB = bodyB.GetLocalPoint(ref worldB);
				// Keep the configured value readable in the inspector.
				m_ConnectedAnchor = connectedBody != null ? Physics2DMath.ToEngine(localB) : Physics2DMath.ToEngine(worldB);
				return;
			}

			if (connectedBody != null)
			{
				Vector2 scaleB = LocalScale(connectedBody.transform);
				localB = new B2Vector2(m_ConnectedAnchor.X * scaleB.X, m_ConnectedAnchor.Y * scaleB.Y);
				worldB = bodyB.GetWorldPoint(ref localB);
			}
			else
			{
				worldB = Physics2DMath.ToB2(m_ConnectedAnchor);
				localB = bodyB.GetLocalPoint(ref worldB);
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

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(bodyA, bodyB, out localA, out localB, out worldA, out worldB);
			if (autoConfigureDistance)
			{
				distance = Math.Max(0.005f, B2Vector2.Distance(worldA, worldB));
			}

			float length = Math.Max(0.005f, distance);
			return new DistanceJointDef
			{
				LocalAnchorA = localA,
				LocalAnchorB = localB,
				Length = length,
				MinLength = maxDistanceOnly ? 0f : length,
				MaxLength = length,
				Stiffness = 0f,
				Damping = 0f
			};
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

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(bodyA, bodyB, out localA, out localB, out worldA, out worldB);
			if (autoConfigureDistance)
			{
				distance = Math.Max(0.005f, B2Vector2.Distance(worldA, worldB));
			}

			float stiffness;
			float damping;
			JointUtils.LinearStiffness(out stiffness, out damping, Math.Max(0f, frequency), dampingRatio, bodyA, bodyB);
			return new DistanceJointDef
			{
				LocalAnchorA = localA,
				LocalAnchorB = localB,
				Length = Math.Max(0.005f, distance),
				MinLength = 0f,
				MaxLength = float.MaxValue,
				Stiffness = stiffness,
				Damping = damping
			};
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
		public float jointAngle => joint is RevoluteJoint revolute ? revolute.GetJointAngle() * Physics2DMath.Rad2Deg : 0f;

		/// <summary>Current angular speed in degrees per second.</summary>
		public float jointSpeed => joint is RevoluteJoint revolute ? revolute.GetJointSpeed() * Physics2DMath.Rad2Deg : 0f;

		#endregion

		#region PublicMethods

		public float GetMotorTorque(float timeStep)
		{
			return joint is RevoluteJoint revolute ? revolute.GetMotorTorque(1f / Math.Max(1e-6f, timeStep)) : 0f;
		}

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(bodyA, bodyB, out localA, out localB, out worldA, out worldB);
			return new RevoluteJointDef
			{
				LocalAnchorA = localA,
				LocalAnchorB = localB,
				ReferenceAngle = bodyB.GetAngle() - bodyA.GetAngle(),
				EnableLimit = useLimits,
				LowerAngle = Math.Min(limits.min, limits.max) * Physics2DMath.Deg2Rad,
				UpperAngle = Math.Max(limits.min, limits.max) * Physics2DMath.Deg2Rad,
				EnableMotor = useMotor,
				MotorSpeed = motor.motorSpeed * Physics2DMath.Deg2Rad,
				MaxMotorTorque = motor.maxMotorTorque
			};
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!(joint is RevoluteJoint revolute))
			{
				return;
			}

			revolute.EnableLimit(useLimits);
			revolute.SetLimits(Math.Min(limits.min, limits.max) * Physics2DMath.Deg2Rad, Math.Max(limits.min, limits.max) * Physics2DMath.Deg2Rad);
			revolute.EnableMotor(useMotor);
			revolute.SetMotorSpeed(motor.motorSpeed * Physics2DMath.Deg2Rad);
			revolute.SetMaxMotorTorque(motor.maxMotorTorque);
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

		public float jointTranslation => joint is PrismaticJoint prismatic ? prismatic.GetJointTranslation() : 0f;
		public float jointSpeed => joint is PrismaticJoint prismatic ? prismatic.GetJointSpeed() : 0f;

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(bodyA, bodyB, out localA, out localB, out worldA, out worldB);
			if (autoConfigureAngle)
			{
				B2Vector2 delta = worldB - worldA;
				if (delta.LengthSquared() > 1e-8f)
				{
					angle = MathF.Atan2(delta.Y, delta.X) * Physics2DMath.Rad2Deg;
				}
			}

			float radians = angle * Physics2DMath.Deg2Rad;
			var worldAxis = new B2Vector2(MathF.Cos(radians), MathF.Sin(radians));
			return new PrismaticJointDef
			{
				LocalAnchorA = localA,
				LocalAnchorB = localB,
				LocalAxisA = bodyA.GetLocalVector(ref worldAxis),
				ReferenceAngle = bodyB.GetAngle() - bodyA.GetAngle(),
				EnableLimit = useLimits,
				LowerTranslation = Math.Min(limits.min, limits.max),
				UpperTranslation = Math.Max(limits.min, limits.max),
				EnableMotor = useMotor,
				MotorSpeed = motor.motorSpeed,
				MaxMotorForce = motor.maxMotorTorque
			};
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!(joint is PrismaticJoint prismatic))
			{
				return;
			}

			prismatic.EnableLimit(useLimits);
			prismatic.SetLimits(Math.Min(limits.min, limits.max), Math.Max(limits.min, limits.max));
			prismatic.EnableMotor(useMotor);
			prismatic.SetMotorSpeed(motor.motorSpeed);
			prismatic.SetMaxMotorForce(motor.maxMotorTorque);
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

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(bodyA, bodyB, out localA, out localB, out worldA, out worldB);
			float stiffness = 0f;
			float damping = 0f;
			if (frequency > 0f)
			{
				JointUtils.AngularStiffness(out stiffness, out damping, frequency, dampingRatio, bodyA, bodyB);
			}

			return new WeldJointDef
			{
				LocalAnchorA = localA,
				LocalAnchorB = localB,
				ReferenceAngle = bodyB.GetAngle() - bodyA.GetAngle(),
				Stiffness = stiffness,
				Damping = damping
			};
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

		public float jointTranslation => joint is WheelJoint wheel ? wheel.GetJointTranslation() : 0f;
		public float jointLinearSpeed => joint is WheelJoint wheel ? wheel.GetJointLinearSpeed() : 0f;
		public float jointAngle => joint is WheelJoint wheel ? wheel.GetJointAngle() * Physics2DMath.Rad2Deg : 0f;
		public float jointSpeed => joint is WheelJoint wheel ? wheel.GetJointAngularSpeed() * Physics2DMath.Rad2Deg : 0f;

		#endregion

		#region UnityMethods

		public override void OnValidate()
		{
			Retune();
		}

		#endregion

		#region InternalMethods

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(bodyA, bodyB, out localA, out localB, out worldA, out worldB);
			float radians = suspension.angle * Physics2DMath.Deg2Rad;
			var worldAxis = new B2Vector2(MathF.Cos(radians), MathF.Sin(radians));
			float stiffness;
			float damping;
			JointUtils.LinearStiffness(out stiffness, out damping, Math.Max(0f, suspension.frequency), suspension.dampingRatio, bodyA, bodyB);
			return new WheelJointDef
			{
				LocalAnchorA = localA,
				LocalAnchorB = localB,
				LocalAxisA = bodyA.GetLocalVector(ref worldAxis),
				EnableMotor = useMotor,
				MotorSpeed = motor.motorSpeed * Physics2DMath.Deg2Rad,
				MaxMotorTorque = motor.maxMotorTorque,
				Stiffness = stiffness,
				Damping = damping
			};
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!(joint is WheelJoint wheel))
			{
				return;
			}

			float stiffness;
			float damping;
			JointUtils.LinearStiffness(out stiffness, out damping, Math.Max(0f, suspension.frequency), suspension.dampingRatio, joint.BodyA, joint.BodyB);
			wheel.SetStiffness(stiffness);
			wheel.SetDamping(damping);
			wheel.EnableMotor(useMotor);
			wheel.SetMotorSpeed(motor.motorSpeed * Physics2DMath.Deg2Rad);
			wheel.SetMaxMotorTorque(motor.maxMotorTorque);
		}

		#endregion
	}

	/// <summary>Resists relative motion between the bodies (top-down friction).</summary>
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

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			B2Vector2 localA, localB, worldA, worldB;
			ResolveAnchors(bodyA, bodyB, out localA, out localB, out worldA, out worldB);
			return new FrictionJointDef { LocalAnchorA = localA, LocalAnchorB = localB, MaxForce = maxForce, MaxTorque = maxTorque };
		}

		internal override void ApplyRuntimeSettings()
		{
			if (joint is FrictionJoint friction)
			{
				friction.MaxForce = maxForce;
				friction.MaxTorque = maxTorque;
			}
		}

		#endregion
	}

	/// <summary>Keeps a relative position and angle between the bodies (a motor joint).</summary>
	public sealed class RelativeJoint2D : Joint2D
	{
		#region PublicFields

		public float maxForce = 10000f;
		public float maxTorque = 10000f;

		[Range(0f, 1f)] public float correctionScale = 0.3f;

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

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			if (autoConfigureOffset)
			{
				B2Vector2 positionB = bodyB.GetPosition();
				linearOffset = Physics2DMath.ToEngine(bodyA.GetLocalPoint(ref positionB));
				angularOffset = (bodyB.GetAngle() - bodyA.GetAngle()) * Physics2DMath.Rad2Deg;
			}

			return new MotorJointDef
			{
				LinearOffset = Physics2DMath.ToB2(linearOffset),
				AngularOffset = angularOffset * Physics2DMath.Deg2Rad,
				MaxForce = maxForce,
				MaxTorque = maxTorque,
				CorrectionFactor = correctionScale
			};
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!(joint is MotorJoint motor))
			{
				return;
			}

			B2Vector2 offset = Physics2DMath.ToB2(linearOffset);
			motor.SetLinearOffset(ref offset);
			motor.SetAngularOffset(angularOffset * Physics2DMath.Deg2Rad);
			motor.SetMaxForce(maxForce);
			motor.SetMaxTorque(maxTorque);
			motor.SetCorrectionFactor(correctionScale);
		}

		#endregion
	}

	/// <summary>Pulls the anchor towards a world target (a soft mouse joint).</summary>
	public sealed class TargetJoint2D : Joint2D
	{
		#region PublicFields

		[Tooltip("Point of this object (local space) pulled towards the target.")]
		public Vector2 anchor;

		public bool autoConfigureTarget = true;
		public Vector2 target;
		public float maxForce = 10000f;

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

		internal override JointDef CreateDefinition(Body bodyA, Body bodyB)
		{
			// Box2D's mouse joint moves body B: the ground is A and this body is B.
			Vector2 scale = LocalScale(transform);
			var local = new B2Vector2(anchor.X * scale.X, anchor.Y * scale.Y);
			B2Vector2 worldAnchor = bodyA.GetWorldPoint(ref local);
			if (autoConfigureTarget)
			{
				target = Physics2DMath.ToEngine(worldAnchor);
			}

			float stiffness;
			float damping;
			JointUtils.LinearStiffness(out stiffness, out damping, Math.Max(0.01f, frequency), dampingRatio, bodyB, bodyA);
			var def = new MouseJointDef { Target = Physics2DMath.ToB2(target), MaxForce = maxForce * Math.Max(0.0001f, bodyA.Mass), Stiffness = stiffness, Damping = damping };
			return def;
		}

		internal override void ApplyRuntimeSettings()
		{
			if (!(joint is MouseJoint mouse))
			{
				return;
			}

			B2Vector2 t = Physics2DMath.ToB2(target);
			mouse.SetTarget(ref t);
			mouse.MaxForce = maxForce * Math.Max(0.0001f, joint.BodyB.Mass);
			float stiffness;
			float damping;
			JointUtils.LinearStiffness(out stiffness, out damping, Math.Max(0.01f, frequency), dampingRatio, joint.BodyB, joint.BodyA);
			mouse.Stiffness = stiffness;
			mouse.Damping = damping;
		}

		#endregion
	}
}
