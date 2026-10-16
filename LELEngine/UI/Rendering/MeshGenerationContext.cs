using System;
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

		internal readonly System.Collections.Generic.List<MeshWriteData> allocations = new System.Collections.Generic.List<MeshWriteData>();

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

	/// <summary>Vector drawing on an element: lines, rectangles, circles and polygons as triangles.</summary>
	public sealed class Painter2D
	{
		#region PublicFields

		public Color4 fillColor = new Color4(1f, 1f, 1f, 1f);
		public Color4 strokeColor = new Color4(1f, 1f, 1f, 1f);
		public float lineWidth = 1f;

		#endregion

		#region PrivateFields

		private readonly MeshGenerationContext context;
		private readonly System.Collections.Generic.List<Vector2> path = new System.Collections.Generic.List<Vector2>();
		private bool closed;

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
			path.Clear();
			closed = false;
		}

		public void MoveTo(Vector2 point)
		{
			path.Add(point);
		}

		public void LineTo(Vector2 point)
		{
			path.Add(point);
		}

		public void ClosePath()
		{
			closed = true;
		}

		/// <summary>Adds an arc of a circle (angles in degrees, clockwise on screen).</summary>
		public void Arc(Vector2 center, float radius, float startAngle, float endAngle, int segments = 0)
		{
			if (segments <= 0)
			{
				segments = Math.Max(8, (int)(Math.Abs(endAngle - startAngle) / 360f * Math.Max(12f, radius)));
			}

			for (int i = 0; i <= segments; i++)
			{
				float angle = (startAngle + (endAngle - startAngle) * i / segments) * (float)(Math.PI / 180.0);
				path.Add(center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius);
			}
		}

		/// <summary>Fills the path as a polygon (fan triangulation; concave shapes should be split).</summary>
		public void Fill()
		{
			if (path.Count < 3)
			{
				return;
			}

			int triangles = path.Count - 2;
			MeshWriteData data = context.Allocate(path.Count, triangles * 3);
			foreach (Vector2 p in path)
			{
				data.SetNextVertex(new Vertex { position = new Vector3(p.X, p.Y, 0f), tint = fillColor, uv = Vector2.Zero });
			}

			for (int i = 0; i < triangles; i++)
			{
				data.SetNextIndex(0);
				data.SetNextIndex((ushort)(i + 1));
				data.SetNextIndex((ushort)(i + 2));
			}
		}

		/// <summary>Strokes the path with quads of <see cref="lineWidth" />.</summary>
		public void Stroke()
		{
			int segments = path.Count - 1 + (closed && path.Count > 2 ? 1 : 0);
			if (segments <= 0)
			{
				return;
			}

			MeshWriteData data = context.Allocate(segments * 4, segments * 6);
			float half = lineWidth * 0.5f;
			for (int i = 0; i < segments; i++)
			{
				Vector2 a = path[i];
				Vector2 b = path[(i + 1) % path.Count];
				Vector2 dir = b - a;
				if (dir.LengthSquared < 1e-8f)
				{
					dir = Vector2.UnitX;
				}

				dir.Normalize();
				var normal = new Vector2(-dir.Y, dir.X) * half;
				int baseIndex = i * 4;
				data.SetNextVertex(new Vertex { position = new Vector3(a.X + normal.X, a.Y + normal.Y, 0f), tint = strokeColor });
				data.SetNextVertex(new Vertex { position = new Vector3(b.X + normal.X, b.Y + normal.Y, 0f), tint = strokeColor });
				data.SetNextVertex(new Vertex { position = new Vector3(b.X - normal.X, b.Y - normal.Y, 0f), tint = strokeColor });
				data.SetNextVertex(new Vertex { position = new Vector3(a.X - normal.X, a.Y - normal.Y, 0f), tint = strokeColor });
				data.SetNextIndex((ushort)baseIndex);
				data.SetNextIndex((ushort)(baseIndex + 1));
				data.SetNextIndex((ushort)(baseIndex + 2));
				data.SetNextIndex((ushort)baseIndex);
				data.SetNextIndex((ushort)(baseIndex + 2));
				data.SetNextIndex((ushort)(baseIndex + 3));
			}
		}

		#endregion
	}
}
