using System;
using System.Collections.Generic;
using Box2DSharp.Collision;
using Box2DSharp.Collision.Collider;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;
using OpenTK.Mathematics;
using B2Transform = Box2DSharp.Common.Transform;
using B2Vector2 = System.Numerics.Vector2;

namespace LELEngine
{
	/// <summary>
	///     Base of the 2D collision shapes. A collider belongs to the nearest <see cref="Rigidbody2D" /> up the
	///     hierarchy (its shapes are built in that body's space, including this object's offset, rotation and scale);
	///     without one it is a static collider in world space. Triggers overlap without colliding and report
	///     OnTrigger callbacks. Shape edits (inspector or properties) rebuild the Box2D fixtures.
	/// </summary>
	public abstract class Collider2D : Behaviour
	{
		#region PublicFields

		[Tooltip("Overlaps without physical response; sends OnTrigger callbacks instead of OnCollision.")]
		[SerializeField] private bool m_IsTrigger;

		[Tooltip("Local offset of the shape.")]
		[SerializeField] private Vector2 m_Offset;

		[SerializeField] private PhysicsMaterial2D m_Material;

		[Tooltip("Mass per unit of area (automatic mass of the rigidbody).")]
		[SerializeField] private float m_Density = 1f;

		public bool isTrigger
		{
			get => m_IsTrigger;
			set
			{
				if (m_IsTrigger != value)
				{
					m_IsTrigger = value;
					foreach (Fixture fixture in fixtures)
					{
						fixture.IsSensor = value;
					}
				}
			}
		}

		public Vector2 offset
		{
			get => m_Offset;
			set
			{
				if (m_Offset != value)
				{
					m_Offset = value;
					MarkDirty();
				}
			}
		}

		public PhysicsMaterial2D sharedMaterial
		{
			get => m_Material;
			set
			{
				m_Material = value;
				ApplyMaterial();
			}
		}

		public float density
		{
			get => m_Density;
			set
			{
				m_Density = value;
				foreach (Fixture fixture in fixtures)
				{
					fixture.Density = value;
				}

				attachedRigidbody?.MassChanged();
			}
		}

		/// <summary>The rigidbody this collider is attached to (null for static colliders or while not simulated).</summary>
		public Rigidbody2D attachedRigidbody { get; internal set; }

		public float friction => m_Material?.friction ?? attachedRigidbody?.sharedMaterial?.friction ?? gameObject?.scene?.Settings.Physics2D.DefaultFriction ?? 0.4f;
		public float bounciness => m_Material?.bounciness ?? attachedRigidbody?.sharedMaterial?.bounciness ?? gameObject?.scene?.Settings.Physics2D.DefaultBounciness ?? 0f;

		/// <summary>World-space bounds of the shapes (empty while not simulated).</summary>
		public Rect bounds
		{
			get
			{
				if (fixtures.Count == 0)
				{
					return Rect.zero;
				}

				Vector2 min = new Vector2(float.MaxValue);
				Vector2 max = new Vector2(float.MinValue);
				foreach (Fixture fixture in fixtures)
				{
					int children = fixture.Shape.GetChildCount();
					for (int child = 0; child < children; child++)
					{
						AABB aabb = fixture.GetAABB(child);
						min = Vector2.ComponentMin(min, Physics2DMath.ToEngine(aabb.LowerBound));
						max = Vector2.ComponentMax(max, Physics2DMath.ToEngine(aabb.UpperBound));
					}
				}

				return Rect.MinMaxRect(min.X, min.Y, max.X, max.Y);
			}
		}

		/// <summary>The Box2D fixtures (empty while not simulated).</summary>
		public IReadOnlyList<Fixture> Fixtures => fixtures;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Sizes the shape to the local bounds of this object's mesh (what Unity does when a collider is added
		///     in the editor). Nothing changes without a <see cref="MeshRenderer" /> or a mesh.
		/// </summary>
		public virtual void FitToRenderer()
		{
		}

		/// <summary>Added in the editor: the shape fits the object's mesh.</summary>
		public override void Reset()
		{
			FitToRenderer();
		}

		#endregion

		#region ProtectedMethods

		/// <summary>The XY bounds of this object's mesh in its local space; false without a renderer or a mesh.</summary>
		protected bool TryGetMeshBounds(out Vector2 min, out Vector2 max)
		{
			Mesh mesh = gameObject?.GetComponent<MeshRenderer>()?.Mesh;
			if (mesh == null || (mesh.BoundsMax.X <= mesh.BoundsMin.X && mesh.BoundsMax.Y <= mesh.BoundsMin.Y))
			{
				min = max = Vector2.Zero;
				return false;
			}

			min = new Vector2(mesh.BoundsMin.X, mesh.BoundsMin.Y);
			max = new Vector2(mesh.BoundsMax.X, mesh.BoundsMax.Y);
			return true;
		}

		#endregion

		#region InternalFields

		internal PhysicsScene2D world;
		internal Body body;
		internal Body ownStaticBody;
		internal readonly List<Fixture> fixtures = new List<Fixture>();
		internal bool shapeDirty;

		#endregion

		#region PrivateFields

		private Matrix4 staticPose;
		private static readonly GJkProfile gjkProfile = new GJkProfile();

		#endregion

		#region UnityMethods

		public override void OnEnable()
		{
			Scene scene = gameObject?.scene;
			if (scene != null && scene.IsPlaying)
			{
				scene.Physics2D.AddCollider(this);
			}
		}

		public override void OnDisable()
		{
			world?.RemoveCollider(this);
		}

		public override void OnDestroy()
		{
			world?.RemoveCollider(this);
		}

		public override void OnValidate()
		{
			MarkDirty();
		}

		#endregion

		#region PublicMethods

		/// <summary>True when the colliders touch (a non-trigger contact exists and is touching).</summary>
		public bool IsTouching(Collider2D other)
		{
			if (world == null || other == null)
			{
				return false;
			}

			var touching = new List<Collider2D>();
			world.GetTouching(this, touching);
			return touching.Contains(other);
		}

		public bool IsTouchingLayers(int layerMask = -1)
		{
			if (world == null)
			{
				return false;
			}

			var touching = new List<Collider2D>();
			world.GetTouching(this, touching);
			foreach (Collider2D collider in touching)
			{
				if (collider.gameObject != null && (layerMask & (1 << collider.gameObject.layer)) != 0)
				{
					return true;
				}
			}

			return false;
		}

		public bool OverlapPoint(Vector2 point)
		{
			B2Vector2 p = Physics2DMath.ToB2(point);
			foreach (Fixture fixture in fixtures)
			{
				if (fixture.TestPoint(ref p))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The point of this collider closest to a world position (the position itself when inside).</summary>
		public Vector2 ClosestPoint(Vector2 position)
		{
			if (fixtures.Count == 0)
			{
				return position;
			}

			B2Vector2 p = Physics2DMath.ToB2(position);
			var pointProxy = new DistanceProxy();
			pointProxy.Set(new[] { p }, 1, 0f);
			float best = float.MaxValue;
			Vector2 result = position;
			foreach (Fixture fixture in fixtures)
			{
				if (fixture.TestPoint(ref p))
				{
					return position;
				}

				B2Transform bodyTransform = fixture.Body.GetTransform();
				int children = fixture.Shape.GetChildCount();
				for (int child = 0; child < children; child++)
				{
					var proxy = new DistanceProxy();
					proxy.Set(fixture.Shape, child);
					var input = new DistanceInput { ProxyA = pointProxy, ProxyB = proxy, TransformA = new B2Transform(B2Vector2.Zero, 0f), TransformB = bodyTransform, UseRadii = true };
					var cache = new SimplexCache();
					DistanceOutput output;
					GJkProfile profile = gjkProfile;
					DistanceAlgorithm.Distance(out output, ref cache, ref input, ref profile);
					if (output.Distance < best)
					{
						best = output.Distance;
						result = Physics2DMath.ToEngine(output.PointB);
					}
				}
			}

			return result;
		}

		/// <summary>Casts this collider's shapes along a direction; hits against its own body are skipped.</summary>
		public int Cast(Vector2 direction, List<RaycastHit2D> results, float distance = float.PositiveInfinity, bool ignoreSiblingColliders = true)
		{
			results.Clear();
			if (world == null || fixtures.Count == 0)
			{
				return 0;
			}

			var hits = new List<RaycastHit2D>();
			foreach (Fixture fixture in fixtures)
			{
				B2Transform transform = fixture.Body.GetTransform();
				world.ShapeCastAll(fixture.Shape, transform, direction, distance, -1, null, hits, false);
				foreach (RaycastHit2D hit in hits)
				{
					if (hit.collider == this || (ignoreSiblingColliders && hit.collider.body == body))
					{
						continue;
					}

					results.Add(hit);
				}
			}

			results.Sort((a, b) => a.distance.CompareTo(b.distance));
			return results.Count;
		}

		/// <summary>Raycasts from this collider's position (hits against its own shapes are skipped).</summary>
		public int Raycast(Vector2 direction, List<RaycastHit2D> results, float distance = float.PositiveInfinity, int layerMask = -1)
		{
			results.Clear();
			if (world == null)
			{
				return 0;
			}

			Vector2 origin = transform != null ? transform.position.Xy : Vector2.Zero;
			var hits = new List<RaycastHit2D>();
			world.RaycastAll(origin, direction, distance, layerMask, null, hits, false);
			foreach (RaycastHit2D hit in hits)
			{
				if (hit.collider != this)
				{
					results.Add(hit);
				}
			}

			return results.Count;
		}

		#endregion

		#region InternalMethods

		/// <summary>Shapes of this collider in the given space (body space of the owner, or world space when static): scale, the object's rotation and offset applied.</summary>
		internal abstract void BuildShapes(List<Shape> shapes, Vector2 scale, float angle, Vector2 translation);

		/// <summary>Creates the fixtures on the body (owner null: a static body at the origin, shapes in world space).</summary>
		internal void CreateFixtures(Body target, Transform owner)
		{
			Vector2 position;
			float angle;
			Vector2 scale;
			Physics2DMath.PlanarPose(transform, owner, out position, out angle, out scale);
			if (owner == null)
			{
				staticPose = transform.LocalToWorld;
			}

			var shapes = new List<Shape>();
			try
			{
				BuildShapes(shapes, scale, angle, position);
			}
			catch (Exception e)
			{
				Debug.LogException(e, this);
			}

			foreach (Shape shape in shapes)
			{
				var def = new FixtureDef
				{
					Shape = shape,
					Density = Math.Max(0f, m_Density),
					Friction = friction,
					Restitution = bounciness,
					IsSensor = m_IsTrigger,
					Filter = new Filter { CategoryBits = 1, MaskBits = ushort.MaxValue, GroupIndex = 0 },
					UserData = this
				};
				fixtures.Add(target.CreateFixture(def));
			}

			if (target.BodyType == BodyType.DynamicBody)
			{
				target.IsAwake = true;
			}
		}

		internal void DestroyFixtures()
		{
			if (body != null)
			{
				foreach (Fixture fixture in fixtures.ToArray())
				{
					body.DestroyFixture(fixture);
				}
			}

			fixtures.Clear();
		}

		/// <summary>A static collider whose transform moved since its fixtures were built (rebuilt before the step).</summary>
		internal bool StaticPoseChanged()
		{
			return ownStaticBody != null && transform != null && !MatrixEquals(transform.LocalToWorld, staticPose);
		}

		internal void MarkDirty()
		{
			shapeDirty = true;
			world?.RebuildCollider(this);
		}

		internal void Refilter()
		{
			foreach (Fixture fixture in fixtures)
			{
				fixture.Refilter();
			}
		}

		protected internal override void OnLayerChanged()
		{
			Refilter();
		}

		/// <summary>Places a collider-local point into the fixture space.</summary>
		protected B2Vector2 Place(Vector2 local, Vector2 scale, float angle, Vector2 translation)
		{
			return Physics2DMath.Place(local + m_Offset, scale, angle, translation);
		}

		/// <summary>Adds a convex polygon (any winding, at most 8 vertices) as a shape; concave or larger ones are triangulated.</summary>
		protected static void AddPolygon(List<Shape> shapes, List<B2Vector2> points, float radius)
		{
			if (points.Count < 3)
			{
				return;
			}

			if (points.Count <= Box2DSharp.Common.Settings.MaxPolygonVertices && Physics2DMath.IsConvex(points) && Math.Abs(Physics2DMath.SignedArea(points)) > 1e-6f)
			{
				var polygon = new PolygonShape();
				polygon.Set(points.ToArray(), points.Count);
				if (radius > 0f)
				{
					Box2DAccess.SetRadius(polygon, radius);
				}

				shapes.Add(polygon);
				return;
			}

			foreach (B2Vector2[] triangle in Physics2DMath.Triangulate(points))
			{
				var polygon = new PolygonShape();
				polygon.Set(triangle, 3);
				if (radius > 0f)
				{
					Box2DAccess.SetRadius(polygon, radius);
				}

				shapes.Add(polygon);
			}
		}

		#endregion

		#region PrivateMethods

		private void ApplyMaterial()
		{
			foreach (Fixture fixture in fixtures)
			{
				fixture.Friction = friction;
				fixture.Restitution = bounciness;
			}
		}

		private static bool MatrixEquals(Matrix4 a, Matrix4 b)
		{
			return (a.Row0 - b.Row0).LengthSquared < 1e-10f && (a.Row1 - b.Row1).LengthSquared < 1e-10f && (a.Row2 - b.Row2).LengthSquared < 1e-10f && (a.Row3 - b.Row3).LengthSquared < 1e-10f;
		}

		#endregion
	}

	/// <summary>Axis-aligned box in the collider's local space (rotation and scale come from the transform).</summary>
	public sealed class BoxCollider2D : Collider2D
	{
		#region PublicFields

		[SerializeField] private Vector2 m_Size = new Vector2(1f, 1f);

		[Tooltip("Rounds the corners: the box is inflated by this radius.")]
		[SerializeField] private float m_EdgeRadius;

		public Vector2 size
		{
			get => m_Size;
			set
			{
				m_Size = value;
				MarkDirty();
			}
		}

		public float edgeRadius
		{
			get => m_EdgeRadius;
			set
			{
				m_EdgeRadius = value;
				MarkDirty();
			}
		}

		#endregion

		#region PublicMethods

		/// <inheritdoc />
		public override void FitToRenderer()
		{
			Vector2 min;
			Vector2 max;
			if (TryGetMeshBounds(out min, out max))
			{
				size = max - min;
				offset = (min + max) * 0.5f;
			}
		}

		#endregion

		#region InternalMethods

		internal override void BuildShapes(List<Shape> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			float hx = Math.Max(0.005f, Math.Abs(m_Size.X) * 0.5f);
			float hy = Math.Max(0.005f, Math.Abs(m_Size.Y) * 0.5f);
			var points = new List<B2Vector2>
			{
				Place(new Vector2(-hx, -hy), scale, angle, translation),
				Place(new Vector2(hx, -hy), scale, angle, translation),
				Place(new Vector2(hx, hy), scale, angle, translation),
				Place(new Vector2(-hx, hy), scale, angle, translation)
			};
			AddPolygon(shapes, points, Math.Max(0f, m_EdgeRadius) * Math.Max(Math.Abs(scale.X), Math.Abs(scale.Y)));
		}

		#endregion
	}

	/// <summary>Circle; a non-uniform scale uses the larger axis.</summary>
	public sealed class CircleCollider2D : Collider2D
	{
		#region PublicFields

		[SerializeField] private float m_Radius = 0.5f;

		public float radius
		{
			get => m_Radius;
			set
			{
				m_Radius = value;
				MarkDirty();
			}
		}

		#endregion

		#region PublicMethods

		/// <inheritdoc />
		public override void FitToRenderer()
		{
			Vector2 min;
			Vector2 max;
			if (TryGetMeshBounds(out min, out max))
			{
				radius = Math.Max(max.X - min.X, max.Y - min.Y) * 0.5f;
				offset = (min + max) * 0.5f;
			}
		}

		#endregion

		#region InternalMethods

		internal override void BuildShapes(List<Shape> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			var circle = new CircleShape
			{
				Position = Place(Vector2.Zero, scale, angle, translation),
				Radius = Math.Max(0.005f, Math.Abs(m_Radius) * Math.Max(Math.Abs(scale.X), Math.Abs(scale.Y)))
			};
			shapes.Add(circle);
		}

		#endregion
	}

	/// <summary>A box with fully rounded ends (a Box2D polygon with a radius).</summary>
	public sealed class CapsuleCollider2D : Collider2D
	{
		#region PublicFields

		[SerializeField] private Vector2 m_Size = new Vector2(1f, 2f);
		[SerializeField] private CapsuleDirection2D m_Direction = CapsuleDirection2D.Vertical;

		public Vector2 size
		{
			get => m_Size;
			set
			{
				m_Size = value;
				MarkDirty();
			}
		}

		public CapsuleDirection2D direction
		{
			get => m_Direction;
			set
			{
				m_Direction = value;
				MarkDirty();
			}
		}

		#endregion

		#region PublicMethods

		/// <inheritdoc />
		public override void FitToRenderer()
		{
			Vector2 min;
			Vector2 max;
			if (TryGetMeshBounds(out min, out max))
			{
				Vector2 extent = max - min;
				size = extent;
				direction = extent.Y >= extent.X ? CapsuleDirection2D.Vertical : CapsuleDirection2D.Horizontal;
				offset = (min + max) * 0.5f;
			}
		}

		#endregion

		#region InternalMethods

		internal override void BuildShapes(List<Shape> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			float sx = Math.Abs(scale.X);
			float sy = Math.Abs(scale.Y);
			float width = Math.Abs(m_Size.X) * sx;
			float height = Math.Abs(m_Size.Y) * sy;
			bool vertical = m_Direction == CapsuleDirection2D.Vertical;
			float radius = (vertical ? width : height) * 0.5f;
			float halfLength = Math.Max(0.01f, (vertical ? height : width) * 0.5f - radius);
			radius = Math.Max(0.005f, radius);

			// The inner segment as a thin box; the radius rounds it into a capsule.
			float thin = 0.01f;
			Vector2 unscaled = new Vector2(1f / Math.Max(sx, 1e-6f), 1f / Math.Max(sy, 1e-6f));
			Vector2 a = vertical ? new Vector2(-thin, -halfLength) : new Vector2(-halfLength, -thin);
			Vector2 b = vertical ? new Vector2(thin, -halfLength) : new Vector2(halfLength, -thin);
			Vector2 c = vertical ? new Vector2(thin, halfLength) : new Vector2(halfLength, thin);
			Vector2 d = vertical ? new Vector2(-thin, halfLength) : new Vector2(-halfLength, thin);
			var points = new List<B2Vector2>
			{
				Place(a * unscaled, scale, angle, translation),
				Place(b * unscaled, scale, angle, translation),
				Place(c * unscaled, scale, angle, translation),
				Place(d * unscaled, scale, angle, translation)
			};
			AddPolygon(shapes, points, radius);
		}

		#endregion
	}

	/// <summary>Free-form polygon (concave allowed: it is triangulated); default is a pentagon of radius 0.5.</summary>
	public sealed class PolygonCollider2D : Collider2D
	{
		#region PublicFields

		[SerializeField] private List<Vector2> m_Points = DefaultPoints();

		public List<Vector2> points
		{
			get => m_Points;
			set
			{
				m_Points = value ?? new List<Vector2>();
				MarkDirty();
			}
		}

		public int pointCount => m_Points?.Count ?? 0;

		#endregion

		#region PublicMethods

		public void SetPath(IEnumerable<Vector2> path)
		{
			points = new List<Vector2>(path);
		}

		public static List<Vector2> DefaultPoints()
		{
			var result = new List<Vector2>();
			for (int i = 0; i < 5; i++)
			{
				float a = MathF.PI * 0.5f + i * MathF.PI * 2f / 5f;
				result.Add(new Vector2(MathF.Cos(a) * 0.5f, MathF.Sin(a) * 0.5f));
			}

			return result;
		}

		#endregion

		#region InternalMethods

		internal override void BuildShapes(List<Shape> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			if (m_Points == null || m_Points.Count < 3)
			{
				return;
			}

			var placed = new List<B2Vector2>(m_Points.Count);
			foreach (Vector2 point in m_Points)
			{
				placed.Add(Place(point, scale, angle, translation));
			}

			AddPolygon(shapes, placed, 0f);
		}

		#endregion
	}

	/// <summary>Open chain of line segments (one-sided-free edges, for ground and walls); default is a horizontal unit segment.</summary>
	public sealed class EdgeCollider2D : Collider2D
	{
		#region PublicFields

		[SerializeField] private List<Vector2> m_Points = new List<Vector2> { new Vector2(-0.5f, 0f), new Vector2(0.5f, 0f) };

		[Tooltip("Thickness of the edges (rounded).")]
		[SerializeField] private float m_EdgeRadius;

		public List<Vector2> points
		{
			get => m_Points;
			set
			{
				m_Points = value ?? new List<Vector2>();
				MarkDirty();
			}
		}

		public int pointCount => m_Points?.Count ?? 0;
		public int edgeCount => Math.Max(0, pointCount - 1);

		public float edgeRadius
		{
			get => m_EdgeRadius;
			set
			{
				m_EdgeRadius = value;
				MarkDirty();
			}
		}

		#endregion

		#region InternalMethods

		internal override void BuildShapes(List<Shape> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			if (m_Points == null || m_Points.Count < 2)
			{
				return;
			}

			var placed = new List<B2Vector2>(m_Points.Count);
			foreach (Vector2 point in m_Points)
			{
				B2Vector2 p = Place(point, scale, angle, translation);
				if (placed.Count == 0 || B2Vector2.DistanceSquared(placed[placed.Count - 1], p) > 1e-6f)
				{
					placed.Add(p);
				}
			}

			if (placed.Count < 2)
			{
				return;
			}

			float radius = Math.Max(0f, m_EdgeRadius) * Math.Max(Math.Abs(scale.X), Math.Abs(scale.Y));
			if (radius > 0f || placed.Count == 2)
			{
				for (int i = 0; i + 1 < placed.Count; i++)
				{
					var edge = new EdgeShape();
					B2Vector2 a = placed[i];
					B2Vector2 b = placed[i + 1];
					edge.SetTwoSided(ref a, ref b);
					if (radius > 0f)
					{
						Box2DAccess.SetRadius(edge, radius);
					}

					shapes.Add(edge);
				}

				return;
			}

			// A chain gives smooth sliding over the joins (no ghost collisions between segments).
			var chain = new ChainShape();
			B2Vector2 first = placed[0];
			B2Vector2 last = placed[placed.Count - 1];
			B2Vector2 prev = first + (first - placed[1]);
			B2Vector2 next = last + (last - placed[placed.Count - 2]);
			chain.CreateChain(placed.ToArray(), placed.Count, prev, next);
			shapes.Add(chain);
		}

		#endregion
	}
}
