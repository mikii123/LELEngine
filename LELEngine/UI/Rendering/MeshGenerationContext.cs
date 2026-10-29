using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>A vertex of custom element geometry (<see cref="MeshGenerationContext.Allocate" />).</summary>
	public struct Vertex
	{
		/// <summary>Position in the element's local coordinates (z ignored).</summary>
		public Vector3 position;

		public Color4 tint;
		public Vector2 uv;
	}

	/// <summary>Vertex and index ranges of an allocation, to be filled by the element.</summary>
	public sealed class MeshWriteData
	{
		#region PublicFields

		public int vertexCount => vertices.Length;
		public int indexCount => indices.Length;

		#endregion

		#region InternalFields

		internal readonly Vertex[] vertices;
		internal readonly ushort[] indices;
		internal readonly Texture texture;
		internal int writtenVertices;
		internal int writtenIndices;

		#endregion

		#region Constructors

		internal MeshWriteData(int vertexCount, int indexCount, Texture texture)
		{
			vertices = new Vertex[vertexCount];
			indices = new ushort[indexCount];
			this.texture = texture;
		}

		#endregion

		#region PublicMethods

		public void SetNextVertex(Vertex vertex)
		{
			if (writtenVertices < vertices.Length)
			{
				vertices[writtenVertices++] = vertex;
			}
		}

		public void SetNextIndex(ushort index)
		{
			if (writtenIndices < indices.Length)
			{
				indices[writtenIndices++] = index;
			}
		}

		public void SetAllVertices(Vertex[] source)
		{
			int count = Math.Min(source.Length, vertices.Length);
			Array.Copy(source, vertices, count);
			writtenVertices = count;
		}

		public void SetAllIndices(ushort[] source)
		{
			int count = Math.Min(source.Length, indices.Length);
			Array.Copy(source, indices, count);
			writtenIndices = count;
		}

		#endregion
	}

	/// <summary>
	///     Handed to <see cref="VisualElement.generateVisualContent" /> when an element repaints: allocate triangles
	///     (local coordinates, y down) with an optional texture, or draw rectangles and text through the helpers.
	/// </summary>
	public sealed class MeshGenerationContext
	{
		#region PublicFields

		public VisualElement visualElement { get; }

		/// <summary>Simple drawing helpers on top of <see cref="Allocate" />.</summary>
		public Painter2D painter2D { get; }

		#endregion

		#region InternalFields

		internal readonly List<MeshWriteData> allocations = new List<MeshWriteData>();

		#endregion

		#region Constructors

		internal MeshGenerationContext(VisualElement element)
		{
			visualElement = element;
			painter2D = new Painter2D(this);
		}

		#endregion

		#region PublicMethods

		/// <summary>Allocates triangles drawn with the texture (null: solid colors). Fill every vertex and index.</summary>
		public MeshWriteData Allocate(int vertexCount, int indexCount, Texture texture = null)
		{
			if (vertexCount <= 0 || indexCount <= 0 || vertexCount > ushort.MaxValue)
			{
				throw new ArgumentOutOfRangeException(nameof(vertexCount));
			}

			var data = new MeshWriteData(vertexCount, indexCount, texture);
			allocations.Add(data);
			return data;
		}

		/// <summary>Draws a solid or textured rectangle in local coordinates.</summary>
		public void DrawRectangle(Rect rect, Color4 color, Texture texture = null, Rect? uv = null)
		{
			MeshWriteData data = Allocate(4, 6, texture);
			Rect u = uv ?? new Rect(0f, 0f, 1f, 1f);
			data.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMin, 0f), tint = color, uv = new Vector2(u.xMin, u.yMin) });
			data.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMin, 0f), tint = color, uv = new Vector2(u.xMax, u.yMin) });
			data.SetNextVertex(new Vertex { position = new Vector3(rect.xMax, rect.yMax, 0f), tint = color, uv = new Vector2(u.xMax, u.yMax) });
			data.SetNextVertex(new Vertex { position = new Vector3(rect.xMin, rect.yMax, 0f), tint = color, uv = new Vector2(u.xMin, u.yMax) });
			data.SetNextIndex(0);
			data.SetNextIndex(1);
			data.SetNextIndex(2);
			data.SetNextIndex(0);
			data.SetNextIndex(2);
			data.SetNextIndex(3);
		}

		#endregion
	}

	/// <summary>How a stroke ends (Unity's LineCap).</summary>
	public enum LineCap
	{
		Butt,
		Round,
		Square
	}

	/// <summary>How two stroke segments meet (Unity's LineJoin).</summary>
	public enum LineJoin
	{
		Miter,
		Bevel,
		Round
	}

	/// <summary>Fill rule of <see cref="Painter2D.Fill" /> (ear clipping of simple polygons: both rules draw the same).</summary>
	public enum FillRule
	{
		NonZero,
		OddEven
	}

	/// <summary>
	///     Vector drawing on an element, modelled on Unity's Painter2D: paths of lines, bezier curves and arcs, filled
	///     (any simple polygon) or stroked (caps, joins). Edges get a one pixel feather (<see cref="antialiasing" />),
	///     so curves and pies draw without jaggies. Coordinates are the element's local ones (y down).
	/// </summary>
	public sealed class Painter2D
	{
		#region PublicFields

		public Color4 fillColor = new Color4(1f, 1f, 1f, 1f);
		public Color4 strokeColor = new Color4(1f, 1f, 1f, 1f);
		public float lineWidth = 1f;
		public LineCap lineCap = LineCap.Butt;
		public LineJoin lineJoin = LineJoin.Miter;
		public float miterLimit = 10f;

		/// <summary>Feather the edges over one pixel (alpha fades out); off draws hard, aliased edges.</summary>
		public bool antialiasing = true;

		#endregion

		#region PrivateFields

		private const float Feather = 1f;

		private readonly MeshGenerationContext context;
		private readonly List<SubPath> subPaths = new List<SubPath>();
		private readonly List<Vertex> vertices = new List<Vertex>();
		private readonly List<ushort> indices = new List<ushort>();
		private readonly List<int> ears = new List<int>();
		private SubPath current;

		private sealed class SubPath
		{
			public readonly List<Vector2> points = new List<Vector2>();
			public bool closed;
		}

		#endregion

		#region Constructors

		internal Painter2D(MeshGenerationContext context)
		{
			this.context = context;
		}

		#endregion

		#region PublicMethods

		public void BeginPath()
		{
			subPaths.Clear();
			current = null;
		}

		/// <summary>Starts a new sub path at the point.</summary>
		public void MoveTo(Vector2 point)
		{
			current = new SubPath();
			current.points.Add(point);
			subPaths.Add(current);
		}

		public void LineTo(Vector2 point)
		{
			Current().points.Add(point);
		}

		/// <summary>A cubic bezier from the current point through two control points.</summary>
		public void BezierCurveTo(Vector2 control1, Vector2 control2, Vector2 point)
		{
			SubPath path = Current();
			Vector2 from = path.points[path.points.Count - 1];
			float length = Vector2.Distance(from, control1) + Vector2.Distance(control1, control2) + Vector2.Distance(control2, point);
			int segments = Segments(length);
			for (int i = 1; i <= segments; i++)
			{
				float t = i / (float)segments;
				float u = 1f - t;
				Vector2 p = u * u * u * from + 3f * u * u * t * control1 + 3f * u * t * t * control2 + t * t * t * point;
				path.points.Add(p);
			}
		}

		/// <summary>A quadratic bezier from the current point through one control point.</summary>
		public void QuadraticCurveTo(Vector2 control, Vector2 point)
		{
			SubPath path = Current();
			Vector2 from = path.points[path.points.Count - 1];
			float length = Vector2.Distance(from, control) + Vector2.Distance(control, point);
			int segments = Segments(length);
			for (int i = 1; i <= segments; i++)
			{
				float t = i / (float)segments;
				float u = 1f - t;
				Vector2 p = u * u * from + 2f * u * t * control + t * t * point;
				path.points.Add(p);
			}
		}

		public void ClosePath()
		{
			if (current != null)
			{
				current.closed = true;
			}
		}

		/// <summary>
		///     Adds an arc of a circle (angles in degrees, clockwise on screen). The arc joins the current point with a
		///     line, or starts a sub path when the path is empty.
		/// </summary>
		public void Arc(Vector2 center, float radius, float startAngle, float endAngle, int segments = 0)
		{
			if (segments <= 0)
			{
				segments = Math.Max(8, (int)(Math.Abs(endAngle - startAngle) / 360f * Math.Max(12f, radius)));
			}

			SubPath path = current ?? new SubPath();
			if (current == null)
			{
				subPaths.Add(path);
				current = path;
			}

			for (int i = 0; i <= segments; i++)
			{
				float angle = (startAngle + (endAngle - startAngle) * i / segments) * (float)(Math.PI / 180.0);
				path.points.Add(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius);
			}
		}

		/// <summary>Fills every sub path of three or more points as a polygon (ear clipping: concave shapes work).</summary>
		public void Fill(FillRule rule = FillRule.NonZero)
		{
			foreach (SubPath path in subPaths)
			{
				FillPolygon(path.points);
			}
		}

		/// <summary>Strokes every sub path with <see cref="lineWidth" />, <see cref="lineCap" /> and <see cref="lineJoin" />.</summary>
		public void Stroke()
		{
			foreach (SubPath path in subPaths)
			{
				StrokePolyline(path.points, path.closed);
			}
		}

		#endregion

		#region PrivateMethods

		private SubPath Current()
		{
			if (current == null)
			{
				MoveTo(Vector2.Zero);
			}

			return current;
		}

		private static int Segments(float length)
		{
			return Math.Clamp((int)Math.Ceiling(length / 3f), 4, 64);
		}

		// ------------------------------------------------------------------ fills

		private void FillPolygon(List<Vector2> source)
		{
			var polygon = new List<Vector2>(source.Count);
			foreach (Vector2 p in source)
			{
				// Repeated points (a closed path whose last point is the first) break the triangulation.
				if (polygon.Count == 0 || (polygon[polygon.Count - 1] - p).LengthSquared > 1e-8f)
				{
					polygon.Add(p);
				}
			}

			if (polygon.Count > 1 && (polygon[polygon.Count - 1] - polygon[0]).LengthSquared <= 1e-8f)
			{
				polygon.RemoveAt(polygon.Count - 1);
			}

			if (polygon.Count < 3)
			{
				return;
			}

			float area = SignedArea(polygon);
			if (Math.Abs(area) < 1e-6f)
			{
				return;
			}

			vertices.Clear();
			indices.Clear();
			foreach (Vector2 p in polygon)
			{
				vertices.Add(MakeVertex(p, fillColor));
			}

			Triangulate(polygon, area > 0f);
			if (antialiasing)
			{
				FeatherPolygon(polygon, area > 0f, fillColor);
			}

			Flush();
		}

		/// <summary>Ear clipping of a simple polygon; <paramref name="clockwise" /> is the screen orientation (y down).</summary>
		private void Triangulate(List<Vector2> polygon, bool clockwise)
		{
			ears.Clear();
			for (int i = 0; i < polygon.Count; i++)
			{
				ears.Add(i);
			}

			int guard = 0;
			while (ears.Count > 3 && guard++ < polygon.Count * polygon.Count)
			{
				bool clipped = false;
				for (int i = 0; i < ears.Count; i++)
				{
					int a = ears[(i + ears.Count - 1) % ears.Count];
					int b = ears[i];
					int c = ears[(i + 1) % ears.Count];
					if (!IsEar(polygon, a, b, c, clockwise))
					{
						continue;
					}

					AddTriangle(a, b, c);
					ears.RemoveAt(i);
					clipped = true;
					break;
				}

				if (!clipped)
				{
					// Not a simple polygon (self intersection): fan the rest so something draws.
					break;
				}
			}

			for (int i = 1; i + 1 < ears.Count; i++)
			{
				AddTriangle(ears[0], ears[i], ears[i + 1]);
			}
		}

		private bool IsEar(List<Vector2> polygon, int a, int b, int c, bool clockwise)
		{
			Vector2 pa = polygon[a], pb = polygon[b], pc = polygon[c];
			float cross = Cross(pb - pa, pc - pb);
			if (clockwise ? cross <= 0f : cross >= 0f)
			{
				return false;
			}

			foreach (int index in ears)
			{
				if (index == a || index == b || index == c)
				{
					continue;
				}

				if (PointInTriangle(polygon[index], pa, pb, pc))
				{
					return false;
				}
			}

			return true;
		}

		private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
		{
			float d1 = Cross(b - a, p - a);
			float d2 = Cross(c - b, p - b);
			float d3 = Cross(a - c, p - c);
			bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
			bool positive = d1 > 0f || d2 > 0f || d3 > 0f;
			return !(negative && positive);
		}

		/// <summary>A strip outside the polygon's edges from the fill colour to transparent.</summary>
		private void FeatherPolygon(List<Vector2> polygon, bool clockwise, Color4 color)
		{
			int count = polygon.Count;
			var clear = new Color4(color.R, color.G, color.B, 0f);
			int first = vertices.Count;
			for (int i = 0; i < count; i++)
			{
				Vector2 previous = polygon[(i + count - 1) % count];
				Vector2 p = polygon[i];
				Vector2 next = polygon[(i + 1) % count];
				Vector2 normal = VertexNormal(previous, p, next, clockwise);
				vertices.Add(MakeVertex(p + normal * Feather, clear));
			}

			for (int i = 0; i < count; i++)
			{
				int j = (i + 1) % count;
				// Inner edge i -> j (the polygon's own vertices), outer edge first + i -> first + j.
				AddTriangle(i, j, first + j);
				AddTriangle(i, first + j, first + i);
			}
		}

		/// <summary>Outward unit normal at a polygon vertex (mitre of the two edge normals, limited to 2).</summary>
		private static Vector2 VertexNormal(Vector2 previous, Vector2 p, Vector2 next, bool clockwise)
		{
			Vector2 n0 = EdgeNormal(previous, p, clockwise);
			Vector2 n1 = EdgeNormal(p, next, clockwise);
			Vector2 sum = n0 + n1;
			if (sum.LengthSquared < 1e-6f)
			{
				return n1;
			}

			sum.Normalize();
			float scale = 1f / Math.Max(0.5f, Vector2.Dot(sum, n1));
			return sum * scale;
		}

		/// <summary>Unit normal of edge a -> b pointing out of a polygon of the given orientation (y down).</summary>
		private static Vector2 EdgeNormal(Vector2 a, Vector2 b, bool clockwise)
		{
			Vector2 dir = b - a;
			if (dir.LengthSquared < 1e-10f)
			{
				return Vector2.Zero;
			}

			dir.Normalize();
			// Clockwise on screen (y down): the outside is on the left of the travel direction.
			return clockwise ? new Vector2(dir.Y, -dir.X) : new Vector2(-dir.Y, dir.X);
		}

		private static float SignedArea(List<Vector2> polygon)
		{
			float area = 0f;
			for (int i = 0; i < polygon.Count; i++)
			{
				Vector2 a = polygon[i];
				Vector2 b = polygon[(i + 1) % polygon.Count];
				area += a.X * b.Y - b.X * a.Y;
			}

			return area * 0.5f;
		}

		// ------------------------------------------------------------------ strokes

		private void StrokePolyline(List<Vector2> source, bool closed)
		{
			var points = new List<Vector2>(source.Count);
			foreach (Vector2 p in source)
			{
				if (points.Count == 0 || (points[points.Count - 1] - p).LengthSquared > 1e-8f)
				{
					points.Add(p);
				}
			}

			if (closed && points.Count > 2 && (points[points.Count - 1] - points[0]).LengthSquared <= 1e-8f)
			{
				points.RemoveAt(points.Count - 1);
			}

			if (points.Count < 2)
			{
				if (points.Count == 1 && lineCap == LineCap.Round)
				{
					vertices.Clear();
					indices.Clear();
					Disc(points[0], lineWidth * 0.5f, strokeColor);
					Flush();
				}

				return;
			}

			float half = Math.Max(0.1f, lineWidth * 0.5f);
			vertices.Clear();
			indices.Clear();

			int segments = closed ? points.Count : points.Count - 1;
			for (int i = 0; i < segments; i++)
			{
				Vector2 a = points[i];
				Vector2 b = points[(i + 1) % points.Count];
				Vector2 dir = b - a;
				dir.Normalize();
				if (!closed && lineCap == LineCap.Square)
				{
					if (i == 0)
					{
						a -= dir * half;
					}

					if (i == segments - 1)
					{
						b += dir * half;
					}
				}

				Segment(a, b, dir, half, strokeColor);
			}

			// Joins at the interior vertices (every vertex of a closed path).
			int firstJoin = closed ? 0 : 1;
			int lastJoin = closed ? points.Count - 1 : points.Count - 2;
			for (int i = firstJoin; i <= lastJoin; i++)
			{
				Vector2 previous = points[(i + points.Count - 1) % points.Count];
				Vector2 p = points[i];
				Vector2 next = points[(i + 1) % points.Count];
				Join(previous, p, next, half, strokeColor);
			}

			if (!closed && lineCap == LineCap.Round)
			{
				Disc(points[0], half, strokeColor);
				Disc(points[points.Count - 1], half, strokeColor);
			}

			Flush();
		}

		/// <summary>A quad along a -> b, with feather strips on both sides.</summary>
		private void Segment(Vector2 a, Vector2 b, Vector2 dir, float half, Color4 color)
		{
			var normal = new Vector2(-dir.Y, dir.X);
			int first = vertices.Count;
			vertices.Add(MakeVertex(a + normal * half, color));
			vertices.Add(MakeVertex(b + normal * half, color));
			vertices.Add(MakeVertex(b - normal * half, color));
			vertices.Add(MakeVertex(a - normal * half, color));
			AddTriangle(first, first + 1, first + 2);
			AddTriangle(first, first + 2, first + 3);

			if (!antialiasing)
			{
				return;
			}

			var clear = new Color4(color.R, color.G, color.B, 0f);
			int outer = vertices.Count;
			vertices.Add(MakeVertex(a + normal * (half + Feather), clear));
			vertices.Add(MakeVertex(b + normal * (half + Feather), clear));
			vertices.Add(MakeVertex(b - normal * (half + Feather), clear));
			vertices.Add(MakeVertex(a - normal * (half + Feather), clear));
			AddTriangle(first, outer, outer + 1);
			AddTriangle(first, outer + 1, first + 1);
			AddTriangle(first + 3, first + 2, outer + 2);
			AddTriangle(first + 3, outer + 2, outer + 3);
		}

		private void Join(Vector2 previous, Vector2 p, Vector2 next, float half, Color4 color)
		{
			Vector2 d0 = p - previous;
			Vector2 d1 = next - p;
			if (d0.LengthSquared < 1e-10f || d1.LengthSquared < 1e-10f)
			{
				return;
			}

			d0.Normalize();
			d1.Normalize();
			float turn = Cross(d0, d1);
			if (Math.Abs(turn) < 1e-4f && Vector2.Dot(d0, d1) > 0f)
			{
				return;
			}

			if (lineJoin == LineJoin.Round)
			{
				Disc(p, half, color);
				return;
			}

			// The outer corners of the two segments (the side away from the turn) and the vertex: a bevel triangle.
			float side = turn > 0f ? -1f : 1f;
			var n0 = new Vector2(-d0.Y, d0.X) * side;
			var n1 = new Vector2(-d1.Y, d1.X) * side;
			Vector2 c0 = p + n0 * half;
			Vector2 c1 = p + n1 * half;
			int first = vertices.Count;
			vertices.Add(MakeVertex(p, color));
			vertices.Add(MakeVertex(c0, color));
			vertices.Add(MakeVertex(c1, color));
			AddTriangle(first, first + 1, first + 2);

			if (lineJoin == LineJoin.Miter)
			{
				Vector2 bisector = n0 + n1;
				float cos = Vector2.Dot(n0, n1);
				float miterLength = 1f / (float)Math.Sqrt(Math.Max(1e-4f, (1f + cos) * 0.5f));
				if (bisector.LengthSquared > 1e-8f && miterLength <= miterLimit)
				{
					bisector.Normalize();
					Vector2 tip = p + bisector * (half * miterLength);
					int t = vertices.Count;
					vertices.Add(MakeVertex(tip, color));
					AddTriangle(first + 1, t, first + 2);
				}
			}
		}

		/// <summary>A filled disc with a feather ring (round caps and joins, sockets).</summary>
		private void Disc(Vector2 center, float radius, Color4 color)
		{
			int segments = Math.Max(8, (int)Math.Ceiling(radius * 2f));
			int centerIndex = vertices.Count;
			vertices.Add(MakeVertex(center, color));
			int ring = vertices.Count;
			for (int i = 0; i < segments; i++)
			{
				float angle = (float)(i * Math.PI * 2.0 / segments);
				vertices.Add(MakeVertex(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius, color));
			}

			for (int i = 0; i < segments; i++)
			{
				AddTriangle(centerIndex, ring + i, ring + (i + 1) % segments);
			}

			if (!antialiasing)
			{
				return;
			}

			var clear = new Color4(color.R, color.G, color.B, 0f);
			int outer = vertices.Count;
			for (int i = 0; i < segments; i++)
			{
				float angle = (float)(i * Math.PI * 2.0 / segments);
				vertices.Add(MakeVertex(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (radius + Feather), clear));
			}

			for (int i = 0; i < segments; i++)
			{
				int j = (i + 1) % segments;
				AddTriangle(ring + i, outer + i, outer + j);
				AddTriangle(ring + i, outer + j, ring + j);
			}
		}

		// ------------------------------------------------------------------ output

		private void AddTriangle(int a, int b, int c)
		{
			indices.Add((ushort)a);
			indices.Add((ushort)b);
			indices.Add((ushort)c);
		}

		private void Flush()
		{
			if (vertices.Count < 3 || indices.Count < 3 || vertices.Count > ushort.MaxValue)
			{
				return;
			}

			MeshWriteData data = context.Allocate(vertices.Count, indices.Count);
			foreach (Vertex v in vertices)
			{
				data.SetNextVertex(v);
			}

			foreach (ushort index in indices)
			{
				data.SetNextIndex(index);
			}

			vertices.Clear();
			indices.Clear();
		}

		private static Vertex MakeVertex(Vector2 p, Color4 color)
		{
			return new Vertex { position = new Vector3(p.X, p.Y, 0f), tint = color, uv = Vector2.Zero };
		}

		private static float Cross(Vector2 a, Vector2 b)
		{
			return a.X * b.Y - a.Y * b.X;
		}

		#endregion
	}
}
