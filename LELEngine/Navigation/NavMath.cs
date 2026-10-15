using System;
using OpenTK.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>2D (xz plane) geometry helpers of the navigation queries (Detour's DetourCommon).</summary>
	internal static class NavMath
	{
		public const float Epsilon = 1e-4f;

		public static float QuaternionDot(in Quaternion a, in Quaternion b)
		{
			return a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
		}

		/// <summary>Twice the signed area of the triangle in the xz plane (positive when c is on the left of a->b looking down -y).</summary>
		public static float TriArea2D(in Vector3 a, in Vector3 b, in Vector3 c)
		{
			float abx = b.X - a.X;
			float abz = b.Z - a.Z;
			float acx = c.X - a.X;
			float acz = c.Z - a.Z;
			return acx * abz - abx * acz;
		}

		public static float Perp2D(in Vector3 u, in Vector3 v)
		{
			return u.Z * v.X - u.X * v.Z;
		}

		public static float Dot2D(in Vector3 u, in Vector3 v)
		{
			return u.X * v.X + u.Z * v.Z;
		}

		public static float Dist2D(in Vector3 a, in Vector3 b)
		{
			float dx = b.X - a.X;
			float dz = b.Z - a.Z;
			return (float)Math.Sqrt(dx * dx + dz * dz);
		}

		public static float DistSqr2D(in Vector3 a, in Vector3 b)
		{
			float dx = b.X - a.X;
			float dz = b.Z - a.Z;
			return dx * dx + dz * dz;
		}

		public static float Len2D(in Vector3 v)
		{
			return (float)Math.Sqrt(v.X * v.X + v.Z * v.Z);
		}

		public static bool Equal(in Vector3 a, in Vector3 b)
		{
			const float threshold = 1f / 16384f;
			return (a - b).LengthSquared < threshold * threshold;
		}

		/// <summary>Squared distance from p to the segment ab in the xz plane; t is the parameter of the closest point.</summary>
		public static float DistancePointSegmentSqr2D(in Vector3 p, in Vector3 a, in Vector3 b, out float t)
		{
			float pqx = b.X - a.X;
			float pqz = b.Z - a.Z;
			float dx = p.X - a.X;
			float dz = p.Z - a.Z;
			float d = pqx * pqx + pqz * pqz;
			t = pqx * dx + pqz * dz;
			if (d > 0f)
			{
				t /= d;
			}

			t = Math.Clamp(t, 0f, 1f);
			dx = a.X + t * pqx - p.X;
			dz = a.Z + t * pqz - p.Z;
			return dx * dx + dz * dz;
		}

		/// <summary>Point in a convex or concave polygon in the xz plane (vertex array with stride 1, count nverts).</summary>
		public static bool PointInPolygon(in Vector3 p, Vector3[] verts, int[] indices, int first, int nverts)
		{
			bool c = false;
			for (int i = 0, j = nverts - 1; i < nverts; j = i++)
			{
				Vector3 vi = verts[indices[first + i]];
				Vector3 vj = verts[indices[first + j]];
				if ((vi.Z > p.Z) != (vj.Z > p.Z) && p.X < (vj.X - vi.X) * (p.Z - vi.Z) / (vj.Z - vi.Z) + vi.X)
				{
					c = !c;
				}
			}

			return c;
		}

		/// <summary>Point in a convex polygon given as a span of vertices.</summary>
		public static bool PointInPolygon(in Vector3 p, ReadOnlySpan<Vector3> verts)
		{
			bool c = false;
			int n = verts.Length;
			for (int i = 0, j = n - 1; i < n; j = i++)
			{
				Vector3 vi = verts[i];
				Vector3 vj = verts[j];
				if ((vi.Z > p.Z) != (vj.Z > p.Z) && p.X < (vj.X - vi.X) * (p.Z - vi.Z) / (vj.Z - vi.Z) + vi.X)
				{
					c = !c;
				}
			}

			return c;
		}

		/// <summary>
		///     Height of the point inside a convex polygon: the polygon is fanned into triangles from its first vertex
		///     and the containing triangle interpolated. False when the point is outside every triangle.
		/// </summary>
		public static bool PolyHeight(in Vector3 p, ReadOnlySpan<Vector3> verts, out float height)
		{
			height = p.Y;
			for (int i = 2; i < verts.Length; i++)
			{
				if (ClosestHeightPointTriangle(p, verts[0], verts[i - 1], verts[i], out height))
				{
					return true;
				}
			}

			return false;
		}

		public static bool ClosestHeightPointTriangle(in Vector3 p, in Vector3 a, in Vector3 b, in Vector3 c, out float h)
		{
			const float eps = 1e-6f;
			Vector3 v0 = c - a;
			Vector3 v1 = b - a;
			Vector3 v2 = p - a;
			float dot00 = Dot2D(v0, v0);
			float dot01 = Dot2D(v0, v1);
			float dot11 = Dot2D(v1, v1);
			float dot02 = Dot2D(v0, v2);
			float dot12 = Dot2D(v1, v2);
			float denom = dot00 * dot11 - dot01 * dot01;
			if (Math.Abs(denom) < eps)
			{
				h = p.Y;
				return false;
			}

			float u = (dot11 * dot02 - dot01 * dot12) / denom;
			float v = (dot00 * dot12 - dot01 * dot02) / denom;
			if (u >= -eps && v >= -eps && u + v <= 1f + eps)
			{
				h = a.Y + v0.Y * u + v1.Y * v;
				return true;
			}

			h = p.Y;
			return false;
		}

		/// <summary>Intersection of a segment with a convex polygon in the xz plane: entry / exit parameters and edges.</summary>
		public static bool IntersectSegmentPoly2D(in Vector3 p0, in Vector3 p1, ReadOnlySpan<Vector3> verts, out float tmin, out float tmax, out int segMin, out int segMax)
		{
			const float eps = 0.00000001f;
			tmin = 0f;
			tmax = 1f;
			segMin = -1;
			segMax = -1;
			Vector3 dir = p1 - p0;
			int n = verts.Length;
			for (int i = 0, j = n - 1; i < n; j = i++)
			{
				Vector3 edge = verts[i] - verts[j];
				Vector3 diff = p0 - verts[j];
				float nn = Perp2D(edge, diff);
				float d = Perp2D(dir, edge);
				if (Math.Abs(d) < eps)
				{
					if (nn < 0f)
					{
						return false;
					}

					continue;
				}

				float t = nn / d;
				if (d < 0f)
				{
					if (t > tmin)
					{
						tmin = t;
						segMin = j;
						if (tmin > tmax)
						{
							return false;
						}
					}
				}
				else
				{
					if (t < tmax)
					{
						tmax = t;
						segMax = j;
						if (tmax < tmin)
						{
							return false;
						}
					}
				}
			}

			return true;
		}

		public static bool IntersectSegSeg2D(in Vector3 ap, in Vector3 aq, in Vector3 bp, in Vector3 bq, out float s, out float t)
		{
			Vector3 u = aq - ap;
			Vector3 v = bq - bp;
			Vector3 w = ap - bp;
			float d = Perp2D(u, v);
			s = 0f;
			t = 0f;
			if (Math.Abs(d) < 1e-6f)
			{
				return false;
			}

			s = Perp2D(v, w) / d;
			t = Perp2D(u, w) / d;
			return true;
		}

		/// <summary>Separating axis test of two convex polygons in the xz plane.</summary>
		public static bool OverlapPolyPoly2D(ReadOnlySpan<Vector3> a, ReadOnlySpan<Vector3> b)
		{
			const float eps = 1e-4f;
			for (int i = 0, j = a.Length - 1; i < a.Length; j = i++)
			{
				Vector3 va = a[j];
				Vector3 vb = a[i];
				float nx = vb.Z - va.Z;
				float nz = -(vb.X - va.X);
				ProjectPoly(a, nx, nz, out float amin, out float amax);
				ProjectPoly(b, nx, nz, out float bmin, out float bmax);
				if (!OverlapRange(amin, amax, bmin, bmax, eps))
				{
					return false;
				}
			}

			for (int i = 0, j = b.Length - 1; i < b.Length; j = i++)
			{
				Vector3 va = b[j];
				Vector3 vb = b[i];
				float nx = vb.Z - va.Z;
				float nz = -(vb.X - va.X);
				ProjectPoly(a, nx, nz, out float amin, out float amax);
				ProjectPoly(b, nx, nz, out float bmin, out float bmax);
				if (!OverlapRange(amin, amax, bmin, bmax, eps))
				{
					return false;
				}
			}

			return true;
		}

		public static bool OverlapBounds(in Vector3 amin, in Vector3 amax, in Vector3 bmin, in Vector3 bmax)
		{
			return amin.X <= bmax.X && amax.X >= bmin.X && amin.Y <= bmax.Y && amax.Y >= bmin.Y && amin.Z <= bmax.Z && amax.Z >= bmin.Z;
		}

		/// <summary>A uniformly distributed point in a convex polygon (fan triangles chosen by area).</summary>
		public static Vector3 RandomPointInConvexPoly(ReadOnlySpan<Vector3> verts, Span<float> areas, float s, float t)
		{
			float areaSum = 0f;
			for (int i = 2; i < verts.Length; i++)
			{
				areas[i] = Math.Max(0.001f, Math.Abs(TriArea2D(verts[0], verts[i - 1], verts[i])));
				areaSum += areas[i];
			}

			float threshold = s * areaSum;
			float acc = 0f;
			float u = 1f;
			int tri = verts.Length - 1;
			for (int i = 2; i < verts.Length; i++)
			{
				float dacc = areas[i];
				if (threshold >= acc && threshold < acc + dacc)
				{
					u = (threshold - acc) / dacc;
					tri = i;
					break;
				}

				acc += dacc;
			}

			float v = (float)Math.Sqrt(t);
			float a = 1f - v;
			float b = (1f - u) * v;
			float c = u * v;
			return verts[0] * a + verts[tri - 1] * b + verts[tri] * c;
		}

		private static void ProjectPoly(ReadOnlySpan<Vector3> poly, float nx, float nz, out float rmin, out float rmax)
		{
			rmin = rmax = poly[0].X * nx + poly[0].Z * nz;
			for (int i = 1; i < poly.Length; i++)
			{
				float d = poly[i].X * nx + poly[i].Z * nz;
				rmin = Math.Min(rmin, d);
				rmax = Math.Max(rmax, d);
			}
		}

		private static bool OverlapRange(float amin, float amax, float bmin, float bmax, float eps)
		{
			return !(amin + eps > bmax || amax - eps < bmin);
		}
	}
}
