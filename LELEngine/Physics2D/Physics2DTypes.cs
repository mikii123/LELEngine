using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

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

	/// <summary>A layer selection as a bit mask (bit n = layer n), with the names of the built-in layers.</summary>
	[Serializable]
	public struct LayerMask
	{
		#region PublicFields

		public int value;

		#endregion

		#region PrivateFields

		private static readonly string[] names =
		{
			"Default", "TransparentFX", "Ignore Raycast", "", "Water", "UI", "", "",
			"", "", "", "", "", "", "", "",
			"", "", "", "", "", "", "", "",
			"", "", "", "", "", "", "", ""
		};

		#endregion

		#region PublicMethods

		public static implicit operator int(LayerMask mask)
		{
			return mask.value;
		}

		public static implicit operator LayerMask(int value)
		{
			return new LayerMask { value = value };
		}

		/// <summary>The mask with the named layers set (unknown names are ignored).</summary>
		public static int GetMask(params string[] layerNames)
		{
			int mask = 0;
			foreach (string name in layerNames)
			{
				int layer = NameToLayer(name);
				if (layer >= 0)
				{
					mask |= 1 << layer;
				}
			}

			return mask;
		}

		public static string LayerToName(int layer)
		{
			return layer >= 0 && layer < 32 ? names[layer] : "";
		}

		/// <summary>The layer with this name, or -1.</summary>
		public static int NameToLayer(string layerName)
		{
			for (int i = 0; i < names.Length; i++)
			{
				if (string.Equals(names[i], layerName, StringComparison.OrdinalIgnoreCase) && names[i].Length > 0)
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>Names a user layer (8..31); the name shows in inspectors and resolves with <see cref="NameToLayer" />.</summary>
		public static void SetLayerName(int layer, string name)
		{
			if (layer >= 0 && layer < 32)
			{
				names[layer] = name ?? "";
			}
		}

		/// <summary>Display name of a layer ("Layer 9" for unnamed ones).</summary>
		public static string DisplayName(int layer)
		{
			string name = LayerToName(layer);
			return name.Length > 0 ? name : "Layer " + layer;
		}

		public bool Contains(int layer)
		{
			return (value & (1 << layer)) != 0;
		}

		public override string ToString()
		{
			return "LayerMask(" + value + ")";
		}

		#endregion
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
