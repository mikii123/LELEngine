using System;
using System.Collections.Generic;
using Box2D.NET;
using OpenTK.Mathematics;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Constants;
using static Box2D.NET.B2Geometries;
using static Box2D.NET.B2Hulls;
using static Box2D.NET.B2MathFunction;
using static Box2D.NET.B2Shapes;
using static Box2D.NET.B2Types;
using static Box2D.NET.B2Worlds;
using B2Vector2 = Box2D.NET.B2Vec2;

namespace LELEngine
{
	/// <summary>
	///     Base of the 2D collision shapes. A collider belongs to the nearest <see cref="Rigidbody2D" /> up the
	///     hierarchy (its shapes are built in that body's space, including this object's offset, rotation and scale);
	///     without one it is a static collider in world space. Triggers overlap without colliding and report
	///     OnTrigger callbacks. Shape edits (inspector or properties) rebuild the Box2D shapes.
	/// </summary>
	[InspectorColor(InspectorColorKind.Physics)]
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
					// Box2D fixes the sensor flag when a shape is created: the shapes are rebuilt.
					m_IsTrigger = value;
					MarkDirty();
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
				foreach (B2ShapeId shape in shapes)
				{
					if (b2Shape_IsValid(shape))
					{
						b2Shape_SetDensity(shape, Math.Max(0f, value), false);
					}
				}

				attachedRigidbody?.MassChanged();
			}
		}

		/// <summary>The rigidbody this collider is attached to (null for static colliders or while not simulated).</summary>
		public Rigidbody2D attachedRigidbody { get; internal set; }

		public float friction => m_Material?.friction ?? attachedRigidbody?.sharedMaterial?.friction ?? ProjectSettings.Current.Physics2D.DefaultFriction;
		public float bounciness => m_Material?.bounciness ?? attachedRigidbody?.sharedMaterial?.bounciness ?? ProjectSettings.Current.Physics2D.DefaultBounciness;

		/// <summary>World-space bounds of the shapes (empty while not simulated).</summary>
		public Rect bounds
		{
			get
			{
				if (shapes.Count == 0)
				{
					return Rect.zero;
				}

				Vector2 min = new Vector2(float.MaxValue);
				Vector2 max = new Vector2(float.MinValue);
				foreach (B2ShapeId shape in shapes)
				{
					B2AABB aabb = b2Shape_GetAABB(shape);
					min = Vector2.ComponentMin(min, Physics2DMath.ToEngine(aabb.lowerBound));
					max = Vector2.ComponentMax(max, Physics2DMath.ToEngine(aabb.upperBound));
				}

				return Rect.MinMaxRect(min.X, min.Y, max.X, max.Y);
			}
		}

		/// <summary>The Box2D shapes, chain segments included (empty while not simulated).</summary>
		public IReadOnlyList<B2ShapeId> Shapes => shapes;

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

		/// <summary>The bounds of this object's mesh in its local space, in the simulation plane; false without a renderer or a mesh.</summary>
		protected bool TryGetMeshBounds(out Vector2 min, out Vector2 max)
		{
			Mesh mesh = gameObject?.GetComponent<MeshRenderer>()?.Mesh;
			PlaneBasis plane = Plane;
			Vector2 a = mesh != null ? plane.ToPlane(mesh.BoundsMin) : Vector2.Zero;
			Vector2 b = mesh != null ? plane.ToPlane(mesh.BoundsMax) : Vector2.Zero;
			min = Vector2.ComponentMin(a, b);
			max = Vector2.ComponentMax(a, b);
			if (mesh == null || (max.X <= min.X && max.Y <= min.Y))
			{
				min = max = Vector2.Zero;
				return false;
			}

			return true;
		}

		#endregion

		#region InternalFields

		internal PhysicsScene2D world;
		internal B2BodyId body;
		internal B2BodyId ownStaticBody;

		/// <summary>The simulation plane: the world's, or the project's outside a world.</summary>
		internal PlaneBasis Plane => world?.Plane ?? PlaneBasis.Current;

		/// <summary>Every shape of the collider, the segments of its chains included.</summary>
		internal readonly List<B2ShapeId> shapes = new List<B2ShapeId>();

		internal readonly List<B2ChainId> chains = new List<B2ChainId>();
		internal bool shapeDirty;

		#endregion

		#region PrivateFields

		private Matrix4 staticPose;

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
			foreach (B2ShapeId shape in shapes)
			{
				if (b2Shape_TestPoint(shape, p))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The point of this collider closest to a world position (the position itself when inside).</summary>
		public Vector2 ClosestPoint(Vector2 position)
		{
			if (shapes.Count == 0)
			{
				return position;
			}

			B2Vector2 p = Physics2DMath.ToB2(position);
			float best = float.MaxValue;
			Vector2 result = position;
			foreach (B2ShapeId shape in shapes)
			{
				if (b2Shape_TestPoint(shape, p))
				{
					return position;
				}

				B2Vector2 closest = b2Shape_GetClosestPoint(shape, p);
				float distance = Physics2DMath.DistanceSquared(closest, p);
				if (distance < best)
				{
					best = distance;
					result = Physics2DMath.ToEngine(closest);
				}
			}

			return result;
		}

		/// <summary>Casts this collider's shapes along a direction; hits against its own body are skipped.</summary>
		public int Cast(Vector2 direction, List<RaycastHit2D> results, float distance = float.PositiveInfinity, bool ignoreSiblingColliders = true)
		{
			results.Clear();
			if (world == null || shapes.Count == 0)
			{
				return 0;
			}

			var hits = new List<RaycastHit2D>();
			foreach (B2ShapeId shape in shapes)
			{
				B2ShapeProxy proxy = PhysicsScene2D.WorldProxy(shape);
				world.ShapeCastAll(proxy, direction, distance, -1, null, hits, false);
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

			Vector2 origin = transform != null ? world.Plane.ToPlane(transform.position) : Vector2.Zero;
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
		internal abstract void BuildShapes(List<ShapeGeometry> shapes, Vector2 scale, float angle, Vector2 translation);

		/// <summary>Creates the shapes on the body (owner null: a static body at the origin, shapes in world space).</summary>
		internal void CreateShapes(B2BodyId target, Transform owner)
		{
			Vector2 position;
			float angle;
			Vector2 scale;
			world.Plane.PlanarPose(transform, owner, out position, out angle, out scale);
			if (owner == null)
			{
				staticPose = transform.LocalToWorld;
			}

			var geometry = new List<ShapeGeometry>();
			try
			{
				BuildShapes(geometry, scale, angle, position);
			}
			catch (Exception e)
			{
				Debug.LogException(e, this);
			}

			B2ShapeDef def = b2DefaultShapeDef();
			def.userData = B2UserData.Ref(this);
			def.density = Math.Max(0f, m_Density);
			def.material.friction = friction;
			def.material.restitution = bounciness;
			def.isSensor = m_IsTrigger;
			def.filter = world.FilterFor(gameObject.layer);
			// Every shape is a sensor visitor (a trigger sees it) and reports contacts (OnCollision callbacks);
			// pairs ignored with Physics2D.IgnoreCollision go through the world's custom filter.
			def.enableSensorEvents = true;
			def.enableContactEvents = true;
			def.enableCustomFiltering = true;
			// The body's mass is computed once after all of its shapes exist (Rigidbody2D.MassChanged).
			def.updateBodyMass = false;

			foreach (ShapeGeometry shape in geometry)
			{
				switch (shape.Kind)
				{
					case ShapeKind.Circle:
						shapes.Add(b2CreateCircleShape(target, def, shape.Circle));
						break;
					case ShapeKind.Capsule:
						shapes.Add(b2CreateCapsuleShape(target, def, shape.Capsule));
						break;
					case ShapeKind.Polygon:
						shapes.Add(b2CreatePolygonShape(target, def, shape.Polygon));
						break;
					case ShapeKind.Segment:
						shapes.Add(b2CreateSegmentShape(target, def, shape.Segment));
						break;
					case ShapeKind.Chain:
						B2ChainDef chainDef = b2DefaultChainDef();
						chainDef.userData = def.userData;
						chainDef.points = shape.ChainPoints;
						chainDef.count = shape.ChainPoints.Length;
						chainDef.isLoop = false;
						chainDef.filter = def.filter;
						chainDef.enableSensorEvents = true;
						var material = b2DefaultSurfaceMaterial();
						material.friction = def.material.friction;
						material.restitution = def.material.restitution;
						chainDef.materials = new[] { material };
						chainDef.materialCount = 1;
						B2ChainId chain = b2CreateChain(target, chainDef);
						chains.Add(chain);
						int count = b2Chain_GetSegmentCount(chain);
						var segments = new B2ShapeId[count];
						b2Chain_GetSegments(chain, segments, count);
						shapes.AddRange(segments);
						break;
				}
			}

			if (b2Body_GetType(target) == B2BodyType.b2_dynamicBody)
			{
				b2Body_SetAwake(target, true);
			}
		}

		internal void DestroyShapes()
		{
			foreach (B2ChainId chain in chains)
			{
				if (b2Chain_IsValid(chain))
				{
					b2DestroyChain(chain);
				}
			}

			foreach (B2ShapeId shape in shapes)
			{
				// Chain segments went with their chain.
				if (b2Shape_IsValid(shape))
				{
					b2DestroyShape(shape, false);
				}
			}

			chains.Clear();
			shapes.Clear();
		}

		/// <summary>A static collider whose transform moved since its shapes were built (rebuilt before the step).</summary>
		internal bool StaticPoseChanged()
		{
			return ownStaticBody.index1 != 0 && transform != null && !MatrixEquals(transform.LocalToWorld, staticPose);
		}

		internal void MarkDirty()
		{
			shapeDirty = true;
			world?.RebuildCollider(this);
		}

		/// <summary>The layer's collision mask into the shapes (layer or matrix changed).</summary>
		internal void Refilter()
		{
			if (world == null || gameObject == null)
			{
				return;
			}

			B2Filter filter = world.FilterFor(gameObject.layer);
			foreach (B2ShapeId shape in shapes)
			{
				if (b2Shape_IsValid(shape))
				{
					b2Shape_SetFilter(shape, filter);
				}
			}
		}

		protected internal override void OnLayerChanged()
		{
			Refilter();
		}

		/// <summary>Places a collider-local point into the shape space.</summary>
		protected B2Vector2 Place(Vector2 local, Vector2 scale, float angle, Vector2 translation)
		{
			return Physics2DMath.Place(local + m_Offset, scale, angle, translation);
		}

		/// <summary>Adds a convex polygon (any winding, at most 8 vertices) as a shape; concave or larger ones are triangulated.</summary>
		private protected static void AddPolygon(List<ShapeGeometry> shapes, List<B2Vector2> points, float radius)
		{
			if (points.Count < 3)
			{
				return;
			}

			if (points.Count <= B2_MAX_POLYGON_VERTICES && Physics2DMath.IsConvex(points) && Math.Abs(Physics2DMath.SignedArea(points)) > 1e-6f)
			{
				AddHull(shapes, points.ToArray(), radius);
				return;
			}

			foreach (B2Vector2[] triangle in Physics2DMath.Triangulate(points))
			{
				AddHull(shapes, triangle, radius);
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>A convex polygon from its points (Box2D's hull drops collinear and duplicate points; a degenerate one is skipped).</summary>
		private static void AddHull(List<ShapeGeometry> shapes, B2Vector2[] points, float radius)
		{
			B2Hull hull = b2ComputeHull(points, points.Length);
			if (hull.count < 3)
			{
				return;
			}

			shapes.Add(new ShapeGeometry { Kind = ShapeKind.Polygon, Polygon = b2MakePolygon(hull, Math.Max(0f, radius)) });
		}

		/// <summary>Friction and bounciness into the live shapes (this collider's material, else its rigidbody's).</summary>
		internal void ApplyMaterial()
		{
			foreach (B2ShapeId shape in shapes)
			{
				if (b2Shape_IsValid(shape))
				{
					b2Shape_SetFriction(shape, friction);
					b2Shape_SetRestitution(shape, bounciness);
				}
			}
		}

		private static bool MatrixEquals(Matrix4 a, Matrix4 b)
		{
			return (a.Row0 - b.Row0).LengthSquared < 1e-10f && (a.Row1 - b.Row1).LengthSquared < 1e-10f && (a.Row2 - b.Row2).LengthSquared < 1e-10f && (a.Row3 - b.Row3).LengthSquared < 1e-10f;
		}

		#endregion
	}

	internal enum ShapeKind
	{
		Circle,
		Capsule,
		Polygon,
		Segment,
		Chain
	}

	/// <summary>One Box2D shape of a collider, in the space of the body it goes on.</summary>
	internal struct ShapeGeometry
	{
		public ShapeKind Kind;
		public B2Circle Circle;
		public B2Capsule Capsule;
		public B2Polygon Polygon;
		public B2Segment Segment;

		/// <summary>Chain points with a ghost point at each end (Box2D's open chain).</summary>
		public B2Vector2[] ChainPoints;
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

		internal override void BuildShapes(List<ShapeGeometry> shapes, Vector2 scale, float angle, Vector2 translation)
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

		internal override void BuildShapes(List<ShapeGeometry> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			var circle = new B2Circle(
				Place(Vector2.Zero, scale, angle, translation),
				Math.Max(0.005f, Math.Abs(m_Radius) * Math.Max(Math.Abs(scale.X), Math.Abs(scale.Y))));
			shapes.Add(new ShapeGeometry { Kind = ShapeKind.Circle, Circle = circle });
		}

		#endregion
	}

	/// <summary>A box with fully rounded ends (a Box2D capsule).</summary>
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

		internal override void BuildShapes(List<ShapeGeometry> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			float sx = Math.Abs(scale.X);
			float sy = Math.Abs(scale.Y);
			float width = Math.Abs(m_Size.X) * sx;
			float height = Math.Abs(m_Size.Y) * sy;
			bool vertical = m_Direction == CapsuleDirection2D.Vertical;
			float radius = Math.Max(0.005f, (vertical ? width : height) * 0.5f);
			float halfLength = Math.Max(0.005f, (vertical ? height : width) * 0.5f - radius);

			// The inner segment (already scaled: Place scales again, so it is divided out first) and the radius.
			Vector2 unscaled = new Vector2(1f / Math.Max(sx, 1e-6f), 1f / Math.Max(sy, 1e-6f));
			Vector2 a = vertical ? new Vector2(0f, -halfLength) : new Vector2(-halfLength, 0f);
			Vector2 b = -a;
			var capsule = new B2Capsule(Place(a * unscaled, scale, angle, translation), Place(b * unscaled, scale, angle, translation), radius);
			shapes.Add(new ShapeGeometry { Kind = ShapeKind.Capsule, Capsule = capsule });
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

		internal override void BuildShapes(List<ShapeGeometry> shapes, Vector2 scale, float angle, Vector2 translation)
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

	/// <summary>Open chain of line segments (for ground and walls); default is a horizontal unit segment.</summary>
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

		internal override void BuildShapes(List<ShapeGeometry> shapes, Vector2 scale, float angle, Vector2 translation)
		{
			if (m_Points == null || m_Points.Count < 2)
			{
				return;
			}

			var placed = new List<B2Vector2>(m_Points.Count);
			foreach (Vector2 point in m_Points)
			{
				B2Vector2 p = Place(point, scale, angle, translation);
				if (placed.Count == 0 || Physics2DMath.DistanceSquared(placed[placed.Count - 1], p) > 1e-6f)
				{
					placed.Add(p);
				}
			}

			if (placed.Count < 2)
			{
				return;
			}

			float radius = Math.Max(0f, m_EdgeRadius) * Math.Max(Math.Abs(scale.X), Math.Abs(scale.Y));
			if (radius > 0f)
			{
				// Thick edges: one capsule per segment.
				for (int i = 0; i + 1 < placed.Count; i++)
				{
					shapes.Add(new ShapeGeometry { Kind = ShapeKind.Capsule, Capsule = new B2Capsule(placed[i], placed[i + 1], radius) });
				}

				return;
			}

			if (placed.Count == 2)
			{
				shapes.Add(new ShapeGeometry { Kind = ShapeKind.Segment, Segment = new B2Segment(placed[0], placed[1]) });
				return;
			}

			// A chain gives smooth sliding over the joins (no ghost collisions between segments). Box2D's open chain
			// takes a ghost point before the first and after the last point (they shape the end normals only).
			B2Vector2 first = placed[0];
			B2Vector2 last = placed[placed.Count - 1];
			var chain = new B2Vector2[placed.Count + 2];
			chain[0] = first + (first - placed[1]);
			placed.CopyTo(chain, 1);
			chain[chain.Length - 1] = last + (last - placed[placed.Count - 2]);
			shapes.Add(new ShapeGeometry { Kind = ShapeKind.Chain, ChainPoints = chain });
		}

		#endregion
	}
}
