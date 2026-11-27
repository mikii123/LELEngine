using System;
using System.Collections.Generic;
using LELEngine.Mathematics;

namespace LELEngine
{
	public enum RigidbodyType2D
	{
		/// <summary>Moved by forces, gravity and collisions.</summary>
		Dynamic,

		/// <summary>Moved by its velocity (or MovePosition) only; infinite mass for collisions.</summary>
		Kinematic,

		/// <summary>Never moves.</summary>
		Static
	}

	public enum ForceMode2D
	{
		/// <summary>Continuous force (mass dependent), applied over the step.</summary>
		Force,

		/// <summary>Instant velocity change (mass dependent).</summary>
		Impulse
	}

	[Flags]
	public enum RigidbodyConstraints2D
	{
		None = 0,
		FreezePositionX = 1,
		FreezePositionY = 2,
		FreezeRotation = 4,
		FreezePosition = FreezePositionX | FreezePositionY,
		FreezeAll = FreezePosition | FreezeRotation
	}

	public enum CollisionDetectionMode2D
	{
		Discrete,

		/// <summary>Continuous collision detection against static and kinematic bodies (fast bodies do not tunnel).</summary>
		Continuous
	}

	public enum RigidbodySleepMode2D
	{
		NeverSleep,
		StartAwake,
		StartAsleep
	}

	public enum RigidbodyInterpolation2D
	{
		None,

		/// <summary>The transform follows the body smoothly between physics steps (rendering at a higher rate than physics).</summary>
		Interpolate,

		/// <summary>The transform runs ahead of the body by the time since the last step.</summary>
		Extrapolate
	}

	public enum CapsuleDirection2D
	{
		Vertical,
		Horizontal
	}

	/// <summary>One contact point of a collision.</summary>
	public struct ContactPoint2D
	{
		public Vector2 point;
		public Vector2 normal;
		public float separation;
		public float normalImpulse;
		public float tangentImpulse;
		public Collider2D collider;
		public Collider2D otherCollider;
		public Rigidbody2D rigidbody;
		public Rigidbody2D otherRigidbody;
		public Vector2 relativeVelocity;
		public bool enabled;
	}

	/// <summary>
	///     A touching contact about to be solved, from <see cref="collider" />'s view, as <see cref="Collider2D.preSolve" />
	///     gets it: before the solver of the step resolves it, so the callback can still let it through. The bodies' state
	///     is the step's start: their velocities are the last step's result (without this step's forces).
	/// </summary>
	public readonly struct PreSolveContact2D
	{
		/// <summary>The collider whose callback this is.</summary>
		public readonly Collider2D collider;

		/// <summary>The collider it touches.</summary>
		public readonly Collider2D otherCollider;

		/// <summary>The contact's deepest point, in world (simulation plane) coordinates.</summary>
		public readonly Vector2 point;

		/// <summary>Unit normal from <see cref="collider" /> towards <see cref="otherCollider" />.</summary>
		public readonly Vector2 normal;

		/// <summary>The velocity of <see cref="collider" />'s body at <see cref="point" /> (zero for a static collider).</summary>
		public readonly Vector2 pointVelocity;

		/// <summary>The velocity of the other body at <see cref="point" />.</summary>
		public readonly Vector2 otherPointVelocity;

		/// <summary>The mass of <see cref="collider" />'s body when it is dynamic; 0 when it cannot be moved (static, kinematic).</summary>
		public readonly float mass;

		/// <summary>The other body's mass when it is dynamic; 0 when it cannot be moved.</summary>
		public readonly float otherMass;

		public PreSolveContact2D(Collider2D collider, Collider2D otherCollider, Vector2 point, Vector2 normal, Vector2 pointVelocity, Vector2 otherPointVelocity, float mass, float otherMass)
		{
			this.collider = collider;
			this.otherCollider = otherCollider;
			this.point = point;
			this.normal = normal;
			this.pointVelocity = pointVelocity;
			this.otherPointVelocity = otherPointVelocity;
			this.mass = mass;
			this.otherMass = otherMass;
		}

		/// <summary>The other body's velocity relative to this one's at the point (as <see cref="ContactPoint2D.relativeVelocity" />).</summary>
		public Vector2 relativeVelocity => otherPointVelocity - pointVelocity;

		/// <summary>How fast the two close in along the normal (positive while they approach, negative while they part).</summary>
		public float approachSpeed => -Vector2.Dot(relativeVelocity, normal);
	}

	/// <summary>
	///     Decides a touching contact before the solver (<see cref="Collider2D.preSolve" />): false lets the colliders pass
	///     through each other in this step (no response, no OnCollision events while it says so). Called on the physics
	///     worker threads, several at once.
	/// </summary>
	public delegate bool PreSolveCallback2D(in PreSolveContact2D contact);

	/// <summary>Information about a collision given to OnCollisionEnter2D / Stay2D / Exit2D (from the receiving object's view).</summary>
	public sealed class Collision2D
	{
		#region PublicFields

		/// <summary>The other object's collider.</summary>
		public Collider2D collider;

		/// <summary>This object's collider.</summary>
		public Collider2D otherCollider;

		/// <summary>The other object's rigidbody (null for static colliders).</summary>
		public Rigidbody2D rigidbody;

		/// <summary>This object's rigidbody.</summary>
		public Rigidbody2D otherRigidbody;

		public GameObject gameObject => collider?.gameObject;
		public Transform transform => collider?.transform;
		public Vector2 relativeVelocity;
		public bool enabled = true;
		public ContactPoint2D[] contacts = Array.Empty<ContactPoint2D>();
		public int contactCount => contacts.Length;

		#endregion

		#region PublicMethods

		public ContactPoint2D GetContact(int index)
		{
			return contacts[index];
		}

		public int GetContacts(ContactPoint2D[] results)
		{
			int count = Math.Min(results.Length, contacts.Length);
			Array.Copy(contacts, results, count);
			return count;
		}

		public int GetContacts(List<ContactPoint2D> results)
		{
			results.Clear();
			results.AddRange(contacts);
			return contacts.Length;
		}

		#endregion
	}

	/// <summary>Result of a 2D cast or ray query. Converts to true when something was hit.</summary>
	public struct RaycastHit2D
	{
		public Collider2D collider;
		public Vector2 point;
		public Vector2 normal;
		public Vector2 centroid;
		public float distance;
		public float fraction;
		public Rigidbody2D rigidbody => collider?.attachedRigidbody;
		public Transform transform => collider?.transform;

		public static implicit operator bool(RaycastHit2D hit)
		{
			return hit.collider != null;
		}

		public override string ToString()
		{
			return collider != null ? $"Hit {collider.gameObject?.Name} at {point} (distance {distance:0.###})" : "No hit";
		}
	}

	/// <summary>Which colliders a query reports: layers and triggers.</summary>
	public struct ContactFilter2D
	{
		public bool useTriggers;
		public bool useLayerMask;
		public LayerMask layerMask;

		/// <summary>Everything: all layers, triggers included.</summary>
		public static ContactFilter2D NoFilter()
		{
			return new ContactFilter2D { useTriggers = true, useLayerMask = false, layerMask = -1 };
		}

		public ContactFilter2D SetLayerMask(LayerMask mask)
		{
			layerMask = mask;
			useLayerMask = true;
			return this;
		}

		public ContactFilter2D ClearLayerMask()
		{
			useLayerMask = false;
			return this;
		}

		public bool IsFilteringLayerMask(GameObject go)
		{
			return useLayerMask && go != null && (layerMask.value & (1 << go.layer)) == 0;
		}

		public bool IsFilteringTrigger(Collider2D collider)
		{
			return !useTriggers && collider != null && collider.isTrigger;
		}
	}

	[Serializable]
	public struct JointMotor2D
	{
		/// <summary>Target speed: degrees per second (hinge, wheel) or units per second (slider).</summary>
		public float motorSpeed;

		/// <summary>Maximum torque (or force for sliders) the motor applies to reach the speed.</summary>
		public float maxMotorTorque;
	}

	[Serializable]
	public struct JointAngleLimits2D
	{
		public float min;
		public float max;
	}

	[Serializable]
	public struct JointTranslationLimits2D
	{
		public float min;
		public float max;
	}

	[Serializable]
	public struct JointSuspension2D
	{
		[Range(0f, 1f)] public float dampingRatio;
		public float frequency;

		/// <summary>Direction of the suspension in degrees (90 = up).</summary>
		public float angle;
	}
}
