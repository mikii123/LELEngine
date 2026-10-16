using System;
using System.Collections.Generic;
using Box2D.NET;
using OpenTK.Mathematics;
using static Box2D.NET.B2MathFunction;
using B2Vector2 = Box2D.NET.B2Vec2;

namespace LELEngine
{
	/// <summary>
	///     The world plane of a 2D simulation as an orthonormal basis: <see cref="U" /> and <see cref="V" /> are the
	///     world directions of 2D x and y, <see cref="Normal" /> (U × V) the axis bodies turn about. The same basis maps
	///     a collider's local 2D shape into its object's local space, so an unrotated object lies in the plane. The
	///     coordinate along the normal is left as it is.
	/// </summary>
	internal readonly struct PlaneBasis
	{
		#region PublicFields

		public static readonly PlaneBasis XY = new PlaneBasis(SimulationPlane2D.XY, Vector3.UnitX, Vector3.UnitY);

		/// <summary>The XY plane turned 90° about X (Y onto Z): 2D y is world Z and bodies turn about -Y, as in Unity's top-down convention.</summary>
		public static readonly PlaneBasis XZ = new PlaneBasis(SimulationPlane2D.XZ, Vector3.UnitX, Vector3.UnitZ);

		public readonly SimulationPlane2D Kind;
		public readonly Vector3 U;
		public readonly Vector3 V;
		public readonly Vector3 Normal;

		/// <summary>The project's plane (for components outside a physics world).</summary>
		public static PlaneBasis Current => Of(ProjectSettings.Current.Physics2D.SimulationPlane);

		#endregion

		#region Constructors

		private PlaneBasis(SimulationPlane2D kind, Vector3 u, Vector3 v)
		{
			Kind = kind;
			U = u;
			V = v;
			Normal = Vector3.Cross(u, v);
		}

		#endregion

		#region PublicMethods

		public static PlaneBasis Of(SimulationPlane2D plane)
		{
			return plane == SimulationPlane2D.XZ ? XZ : XY;
		}

		/// <summary>The 2D coordinates of a world (or local) point or direction.</summary>
		public Vector2 ToPlane(Vector3 v)
		{
			return new Vector2(Vector3.Dot(v, U), Vector3.Dot(v, V));
		}

		/// <summary>The 3D point of 2D coordinates; the coordinate along the normal comes from <paramref name="normalOf" />.</summary>
		public Vector3 FromPlane(Vector2 v, Vector3 normalOf)
		{
			return U * v.X + V * v.Y + Normal * Vector3.Dot(normalOf, Normal);
		}

		/// <summary>The 3D direction of a 2D vector.</summary>
		public Vector3 FromPlane(Vector2 v)
		{
			return U * v.X + V * v.Y;
		}

		/// <summary>Rotation about the normal in radians (the angle of the rotated U axis in the plane).</summary>
		public float AngleOf(Quaternion rotation)
		{
			Vector2 right = ToPlane(rotation * U);
			return MathF.Atan2(right.Y, right.X);
		}

		/// <summary>The part of a rotation that is not about the normal (kept while the body turns in the plane).</summary>
		public Quaternion RemoveTurn(Quaternion rotation)
		{
			return (Quaternion.FromAxisAngle(Normal, -AngleOf(rotation)) * rotation).Normalized();
		}

		public Quaternion WithTurn(Quaternion baseRotation, float angle)
		{
			return (Quaternion.FromAxisAngle(Normal, angle) * baseRotation).Normalized();
		}

		/// <summary>The (signed) scale of a transform along its local U and V axes.</summary>
		public Vector2 ScaleOf(Vector3 lossyScale)
		{
			return new Vector2(Vector3.Dot(lossyScale, Unsigned(U)), Vector3.Dot(lossyScale, Unsigned(V)));
		}

		/// <summary>
		///     Position, rotation and scale of a transform in the plane: in the frame of a rigidbody's body (its world
		///     position and rotation about the normal; the body's own scale and the child's offsets remain in the
		///     result) or in the world (<paramref name="owner" /> null).
		/// </summary>
		public void PlanarPose(Transform transform, Transform owner, out Vector2 position, out float angle, out Vector2 scale)
		{
			// Local-to-world in plane coordinates on both sides (row vectors: local 2D → local 3D → world → world 2D).
			Matrix4 basis = new Matrix4(new Vector4(U, 0f), new Vector4(V, 0f), new Vector4(Normal, 0f), Vector4.UnitW);
			Matrix4 matrix = basis * transform.LocalToWorld * Matrix4.Transpose(basis);
			if (owner != null)
			{
				Vector2 ownerPosition = ToPlane(owner.position);
				Matrix4 frame = Matrix4.CreateRotationZ(AngleOf(owner.rotation)) * Matrix4.CreateTranslation(ownerPosition.X, ownerPosition.Y, 0f);
				matrix = matrix * frame.Inverted();
			}

			Vector2 xAxis = new Vector2(matrix.M11, matrix.M12);
			Vector2 yAxis = new Vector2(matrix.M21, matrix.M22);
			position = new Vector2(matrix.M41, matrix.M42);
			angle = MathF.Atan2(xAxis.Y, xAxis.X);
			float sx = xAxis.Length;
			float sy = yAxis.Length;
			// A mirrored transform (negative determinant) flips the Y axis.
			if (xAxis.X * yAxis.Y - xAxis.Y * yAxis.X < 0f)
			{
				sy = -sy;
			}

			scale = new Vector2(sx, sy);
		}

		#endregion

		#region PrivateMethods

		private static Vector3 Unsigned(Vector3 axis)
		{
			return new Vector3(MathF.Abs(axis.X), MathF.Abs(axis.Y), MathF.Abs(axis.Z));
		}

		#endregion
	}

	/// <summary>Conversions between OpenTK vectors and Box2D's, plus polygon helpers.</summary>
	internal static class Physics2DMath
	{
		#region PublicFields

		public const float Deg2Rad = (float)(Math.PI / 180.0);
		public const float Rad2Deg = (float)(180.0 / Math.PI);

		#endregion

		#region PublicMethods

		public static B2Vector2 ToB2(Vector2 v)
		{
			return new B2Vector2(v.X, v.Y);
		}

		public static Vector2 ToEngine(B2Vector2 v)
		{
			return new Vector2(v.X, v.Y);
		}

		/// <summary>A Box2D transform from a position and an angle in radians.</summary>
		public static B2Transform Pose(Vector2 position, float angle)
		{
			return new B2Transform(ToB2(position), b2MakeRot(angle));
		}

		public static float Angle(B2Rot rotation)
		{
			return b2Rot_GetAngle(rotation);
		}

		public static float DistanceSquared(B2Vector2 a, B2Vector2 b)
		{
			float x = a.X - b.X;
			float y = a.Y - b.Y;
			return x * x + y * y;
		}

		/// <summary>A collider-local point placed in body space: scaled, rotated by the collider object's angle and translated.</summary>
		public static B2Vector2 Place(Vector2 local, Vector2 scale, float angle, Vector2 translation)
		{
			float x = local.X * scale.X;
			float y = local.Y * scale.Y;
			float c = MathF.Cos(angle);
			float s = MathF.Sin(angle);
			return new B2Vector2(c * x - s * y + translation.X, s * x + c * y + translation.Y);
		}

		/// <summary>Signed area of a polygon (positive for counter-clockwise).</summary>
		public static float SignedArea(IReadOnlyList<B2Vector2> points)
		{
			float area = 0f;
			for (int i = 0; i < points.Count; i++)
			{
				B2Vector2 a = points[i];
				B2Vector2 b = points[(i + 1) % points.Count];
				area += a.X * b.Y - b.X * a.Y;
			}

			return area * 0.5f;
		}

		public static bool IsConvex(IReadOnlyList<B2Vector2> points)
		{
			if (points.Count < 3)
			{
				return false;
			}

			bool positive = false;
			bool negative = false;
			for (int i = 0; i < points.Count; i++)
			{
				B2Vector2 a = points[i];
				B2Vector2 b = points[(i + 1) % points.Count];
				B2Vector2 c = points[(i + 2) % points.Count];
				float cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
				if (cross > 1e-6f) positive = true;
				else if (cross < -1e-6f) negative = true;
				if (positive && negative)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Ear-clipping triangulation of a simple polygon (any winding); degenerate triangles are dropped.</summary>
		public static List<B2Vector2[]> Triangulate(IReadOnlyList<B2Vector2> polygon)
		{
			var result = new List<B2Vector2[]>();
			var points = new List<B2Vector2>(polygon);
			if (points.Count < 3)
			{
				return result;
			}

			if (SignedArea(points) < 0f)
			{
				points.Reverse();
			}

			int guard = 0;
			while (points.Count > 3 && guard++ < 10000)
			{
				bool clipped = false;
				for (int i = 0; i < points.Count; i++)
				{
					B2Vector2 prev = points[(i + points.Count - 1) % points.Count];
					B2Vector2 current = points[i];
					B2Vector2 next = points[(i + 1) % points.Count];
					float cross = (current.X - prev.X) * (next.Y - current.Y) - (current.Y - prev.Y) * (next.X - current.X);
					if (cross <= 1e-9f)
					{
						continue; // Reflex or degenerate vertex.
					}

					bool contains = false;
					for (int j = 0; j < points.Count; j++)
					{
						if (j == i || j == (i + points.Count - 1) % points.Count || j == (i + 1) % points.Count)
						{
							continue;
						}

						if (PointInTriangle(points[j], prev, current, next))
						{
							contains = true;
							break;
						}
					}

					if (contains)
					{
						continue;
					}

					result.Add(new[] { prev, current, next });
					points.RemoveAt(i);
					clipped = true;
					break;
				}

				if (!clipped)
				{
					// Self-intersecting input: fan the rest.
					break;
				}
			}

			if (points.Count == 3)
			{
				result.Add(new[] { points[0], points[1], points[2] });
			}
			else
			{
				for (int i = 1; i + 1 < points.Count; i++)
				{
					result.Add(new[] { points[0], points[i], points[i + 1] });
				}
			}

			result.RemoveAll(triangle => Math.Abs(SignedArea(triangle)) < 1e-7f);
			return result;
		}

		#endregion

		#region PrivateMethods

		private static bool PointInTriangle(B2Vector2 p, B2Vector2 a, B2Vector2 b, B2Vector2 c)
		{
			float d1 = Sign(p, a, b);
			float d2 = Sign(p, b, c);
			float d3 = Sign(p, c, a);
			bool hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
			bool hasPositive = d1 > 0 || d2 > 0 || d3 > 0;
			return !(hasNegative && hasPositive);
		}

		private static float Sign(B2Vector2 p1, B2Vector2 p2, B2Vector2 p3)
		{
			return (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
		}

		#endregion
	}
}
