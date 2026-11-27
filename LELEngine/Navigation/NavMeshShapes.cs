using System;
using System.Collections.Generic;
using LELEngine.Mathematics;

namespace LELEngine.Navigation
{
	/// <summary>
	///     Footprints of what a bake cuts out, in a surface's xz plane: convex hulls, rounded by a radius (the hull's
	///     Minkowski sum with a disc: circles, capsules, rounded polygons, a cell's worth of margin) and filled loops.
	/// </summary>
	internal static class NavMeshShapes
	{
		#region PublicMethods

		/// <summary>
		///     The margin that makes a footprint cover every voxel it touches: the bake marks a voxel by its centre, which lies
		///     within half a diagonal of any point of the voxel.
		/// </summary>
		public static float CoverMargin(float cellSize)
		{
			return cellSize * 0.7072f;
		}

		/// <summary>The convex hull of points, counter-clockwise (Andrew's monotone chain); 1 or 2 points for a point or a segment.</summary>
		public static List<Vector2> ConvexHull(IReadOnlyList<Vector2> points)
		{
			var sorted = new List<Vector2>(points);
			sorted.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
			var unique = new List<Vector2>(sorted.Count);
			foreach (Vector2 p in sorted)
			{
				if (unique.Count == 0 || (p - unique[unique.Count - 1]).LengthSquared > 1e-12f)
				{
					unique.Add(p);
				}
			}

			if (unique.Count < 3)
			{
				return unique;
			}

			var hull = new List<Vector2>(unique.Count + 1);
			for (int pass = 0; pass < 2; pass++)
			{
				int start = hull.Count;
				for (int k = 0; k < unique.Count; k++)
				{
					Vector2 p = pass == 0 ? unique[k] : unique[unique.Count - 1 - k];
					while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
					{
						hull.RemoveAt(hull.Count - 1);
					}

					hull.Add(p);
				}

				hull.RemoveAt(hull.Count - 1);
			}

			if (hull.Count < 3)
			{
				// Collinear points: the segment between the extremes.
				return new List<Vector2> { unique[0], unique[unique.Count - 1] };
			}

			return hull;
		}

		/// <summary>
		///     A convex hull (counter-clockwise; one point is a disc, two a capsule) widened by <paramref name="radius" />,
		///     its corners rounded: arcs whose chords stay within <paramref name="tolerance" /> of the circle.
		/// </summary>
		public static Vector2[] Rounded(IReadOnlyList<Vector2> hull, float radius, float tolerance)
		{
			if (hull.Count == 0)
			{
				return Array.Empty<Vector2>();
			}

			if (radius <= 1e-6f)
			{
				return hull.Count >= 3 ? ToArray(hull) : Array.Empty<Vector2>();
			}

			float step = 2f * MathF.Acos(Math.Clamp(1f - Math.Max(tolerance, radius * 1e-3f) / radius, -1f, 1f));
			step = Math.Max(step, MathF.PI * 2f / 64f);
			var result = new List<Vector2>();
			if (hull.Count == 1)
			{
				AddArc(result, hull[0], radius, 0f, MathF.PI * 2f, step, false);
				return result.ToArray();
			}

			int n = hull.Count;
			for (int i = 0; i < n; i++)
			{
				Vector2 previous = hull[(i + n - 1) % n];
				Vector2 current = hull[i];
				Vector2 next = hull[(i + 1) % n];
				float from = OutwardAngle(previous, current);
				float to = OutwardAngle(current, next);
				float sweep = to - from;
				while (sweep < 0f)
				{
					sweep += MathF.PI * 2f;
				}

				// Two points: each end turns half way round.
				if (n == 2)
				{
					sweep = MathF.PI;
				}

				AddArc(result, current, radius, from, sweep, step, true);
			}

			return result.ToArray();
		}

		/// <summary>Twice the signed area of a polygon: positive when it runs counter-clockwise.</summary>
		public static float SignedArea2(IReadOnlyList<Vector2> polygon)
		{
			float area = 0f;
			for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
			{
				area += polygon[j].X * polygon[i].Y - polygon[i].X * polygon[j].Y;
			}

			return area;
		}

		/// <summary>The triangles of a simple polygon (any winding, concave allowed) by ear clipping.</summary>
		public static List<Vector2[]> Triangulate(IReadOnlyList<Vector2> polygon)
		{
			var triangles = new List<Vector2[]>();
			var indices = new List<int>();
			bool ccw = SignedArea2(polygon) > 0f;
			for (int i = 0; i < polygon.Count; i++)
			{
				indices.Add(ccw ? i : polygon.Count - 1 - i);
			}

			// Drop repeated and collinear points (an outline traced on a grid has many).
			for (int i = 0; i < indices.Count && indices.Count > 3;)
			{
				Vector2 a = polygon[indices[(i + indices.Count - 1) % indices.Count]];
				Vector2 b = polygon[indices[i]];
				Vector2 c = polygon[indices[(i + 1) % indices.Count]];
				if ((b - a).LengthSquared < 1e-12f || MathF.Abs(Cross(a, b, c)) < 1e-9f * Math.Max(1f, (c - a).LengthSquared))
				{
					indices.RemoveAt(i);
					i = Math.Max(0, i - 1);
				}
				else
				{
					i++;
				}
			}

			int guard = indices.Count * indices.Count + 8;
			while (indices.Count > 3 && guard-- > 0)
			{
				bool clipped = false;
				for (int i = 0; i < indices.Count; i++)
				{
					int ia = indices[(i + indices.Count - 1) % indices.Count];
					int ib = indices[i];
					int ic = indices[(i + 1) % indices.Count];
					Vector2 a = polygon[ia];
					Vector2 b = polygon[ib];
					Vector2 c = polygon[ic];
					if (Cross(a, b, c) <= 0f)
					{
						continue;
					}

					bool ear = true;
					foreach (int k in indices)
					{
						if (k != ia && k != ib && k != ic && InTriangle(polygon[k], a, b, c))
						{
							ear = false;
							break;
						}
					}

					if (!ear)
					{
						continue;
					}

					triangles.Add(new[] { a, b, c });
					indices.RemoveAt(i);
					clipped = true;
					break;
				}

				if (!clipped)
				{
					// Self-touching or degenerate: what is left goes as a fan (a convex piece covers it).
					break;
				}
			}

			if (indices.Count == 3)
			{
				triangles.Add(new[] { polygon[indices[0]], polygon[indices[1]], polygon[indices[2]] });
			}
			else if (indices.Count > 3)
			{
				var rest = new List<Vector2>();
				foreach (int index in indices)
				{
					rest.Add(polygon[index]);
				}

				for (int i = 1; i + 1 < rest.Count; i++)
				{
					triangles.Add(new[] { rest[0], rest[i], rest[i + 1] });
				}
			}

			return triangles;
		}

		/// <summary>Bounds of a polygon.</summary>
		public static void Extend(IReadOnlyList<Vector2> polygon, ref Vector2 min, ref Vector2 max)
		{
			foreach (Vector2 p in polygon)
			{
				min = Vector2.ComponentMin(min, p);
				max = Vector2.ComponentMax(max, p);
			}
		}

		#endregion

		#region PrivateMethods

		private static float Cross(Vector2 o, Vector2 a, Vector2 b)
		{
			return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
		}

		private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
		{
			return Cross(a, b, p) >= 0f && Cross(b, c, p) >= 0f && Cross(c, a, p) >= 0f;
		}

		/// <summary>The angle of the outward normal of the edge a to b of a counter-clockwise polygon (to its right).</summary>
		private static float OutwardAngle(Vector2 a, Vector2 b)
		{
			Vector2 d = b - a;
			return MathF.Atan2(-d.X, d.Y);
		}

		private static void AddArc(List<Vector2> result, Vector2 centre, float radius, float from, float sweep, float step, bool includeEnd)
		{
			int count = Math.Max(1, (int)MathF.Ceiling(sweep / step - 1e-4f));
			int last = includeEnd ? count : count - 1;
			for (int k = 0; k <= last; k++)
			{
				float angle = from + sweep * k / count;
				result.Add(centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius);
			}
		}

		private static Vector2[] ToArray(IReadOnlyList<Vector2> points)
		{
			var array = new Vector2[points.Count];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = points[i];
			}

			return array;
		}

		#endregion
	}
}
