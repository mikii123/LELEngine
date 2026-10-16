using System;
using System.Collections.Generic;
using System.Reflection;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;
using OpenTK.Mathematics;
using B2Vector2 = System.Numerics.Vector2;

namespace LELEngine
{
	/// <summary>Members of Box2DSharp that are not public but needed (shape radius, mass reset, gravity scale), reached through reflection once.</summary>
	internal static class Box2DAccess
	{
		#region PrivateFields

		private static readonly Action<Shape, float> setRadius = CreateSetter<Shape, float>("Radius");
		private static readonly Action<Body> resetMassData = CreateAction<Body>("ResetMassData");
		private static readonly FieldInfo gravityScale = typeof(Body).GetField("GravityScale", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

		#endregion

		#region PublicMethods

		/// <summary>Rounding radius of polygons and edges (Box2D's shape radius); false when the port does not allow it.</summary>
		public static bool SetRadius(Shape shape, float radius)
		{
			if (setRadius == null)
			{
				return false;
			}

			setRadius(shape, radius);
			return true;
		}

		/// <summary>Recomputes the body's mass from its fixtures (after an explicit mass was set).</summary>
		public static void ResetMassData(Body body)
		{
			resetMassData?.Invoke(body);
		}

		public static bool SetGravityScale(Body body, float scale)
		{
			if (gravityScale == null)
			{
				return false;
			}

			gravityScale.SetValue(body, scale);
			return true;
		}

		public static float GetGravityScale(Body body)
		{
			return gravityScale != null ? (float)gravityScale.GetValue(body) : 1f;
		}

		#endregion

		#region PrivateMethods

		private static Action<T, TValue> CreateSetter<T, TValue>(string property)
		{
			try
			{
				MethodInfo setter = typeof(T).GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetSetMethod(true);
				return setter != null ? (Action<T, TValue>)Delegate.CreateDelegate(typeof(Action<T, TValue>), setter) : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static Action<T> CreateAction<T>(string method)
		{
			try
			{
				MethodInfo info = typeof(T).GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
				return info != null ? (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), info) : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		#endregion
	}

	/// <summary>Conversions between the engine's 3D transforms / OpenTK vectors and Box2D's XY plane, plus polygon helpers.</summary>
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

		public static B2Vector2 ToB2(Vector3 v)
		{
			return new B2Vector2(v.X, v.Y);
		}

		public static Vector2 ToEngine(B2Vector2 v)
		{
			return new Vector2(v.X, v.Y);
		}

		/// <summary>Rotation of a transform about the Z axis in radians (the angle of its world X axis in the XY plane).</summary>
		public static float AngleOf(Quaternion rotation)
		{
			Vector3 right = rotation * Vector3.UnitX;
			return MathF.Atan2(right.Y, right.X);
		}

		/// <summary>The part of a rotation that is not about Z (kept while the body rotates in the plane).</summary>
		public static Quaternion RemoveZ(Quaternion rotation)
		{
			float angle = AngleOf(rotation);
			return (Quaternion.FromAxisAngle(Vector3.UnitZ, -angle) * rotation).Normalized();
		}

		public static Quaternion WithZ(Quaternion baseRotation, float angle)
		{
			return (Quaternion.FromAxisAngle(Vector3.UnitZ, angle) * baseRotation).Normalized();
		}

		/// <summary>
		///     Position, rotation and scale of a transform in the XY plane: in the frame of a rigidbody's body (its
		///     world position and Z rotation; the body's own scale and the child's offsets remain in the result) or in
		///     the world (<paramref name="owner" /> null).
		/// </summary>
		public static void PlanarPose(Transform transform, Transform owner, out Vector2 position, out float angle, out Vector2 scale)
		{
			Matrix4 matrix = transform.LocalToWorld;
			if (owner != null)
			{
				Vector3 ownerPosition = owner.position;
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
