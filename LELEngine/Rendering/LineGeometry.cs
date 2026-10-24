using System;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>A point of a line to build: where it is, the way its strip faces, its width, color and texture u.</summary>
	internal struct LinePoint
	{
		public Vector3 Position;

		/// <summary>The strip's normal here: towards the camera (view alignment) or the transform's Z axis.</summary>
		public Vector3 Facing;

		public float Width;
		public Color4 Color;
		public float U;
	}

	/// <summary>
	///     Builds the triangles of lines (trails; Unity's line builder): every point gets two vertices half its width to
	///     either side, across the line and its facing direction, so the strip faces the camera or the transform's Z axis.
	///     Corner vertices round the outer side of every joint (a fan around the inner vertex), cap vertices round both
	///     ends (half discs). Vertices are in world space, in the standard layout (normal = facing, tangent = along the
	///     line towards its end, bitangent = across it in the direction v grows), and the triangles face the facing
	///     direction. Lines append to the same arrays, so every line of a view uploads in one go.
	/// </summary>
	internal sealed class LineGeometryBuilder
	{
		#region PublicFields

		public Vertex[] Vertices = new Vertex[256];
		public uint[] Indices = new uint[768];
		public int VertexCount;
		public int IndexCount;

		#endregion

		#region PrivateFields

		// Per point of the line being built: the vertices the segment before it ends on and the segment after it starts
		// from (the same pair unless the point has a rounded corner).
		private uint[] inLeft = new uint[64];
		private uint[] inRight = new uint[64];
		private uint[] outLeft = new uint[64];
		private uint[] outRight = new uint[64];

		private Vector3 boundsMin;
		private Vector3 boundsMax;

		#endregion

		#region PublicMethods

		public void Clear()
		{
			VertexCount = 0;
			IndexCount = 0;
		}

		/// <summary>
		///     Appends the triangles of one line through <paramref name="count" /> points (no two consecutive points at the
		///     same place). False, with nothing appended, for fewer than two points.
		/// </summary>
		public bool Build(LinePoint[] points, int count, int cornerVertices, int capVertices, float vScale, out int firstIndex, out int indexCount, out Vector3 min, out Vector3 max)
		{
			firstIndex = IndexCount;
			indexCount = 0;
			min = max = Vector3.Zero;
			if (count < 2)
			{
				return false;
			}

			EnsurePointCapacity(count);
			boundsMin = new Vector3(float.MaxValue);
			boundsMax = new Vector3(float.MinValue);
			Vector3 previousSide = Vector3.Zero;
			for (int i = 0; i < count; i++)
			{
				LinePoint point = points[i];
				Vector3 facing = SafeNormalize(point.Facing, Vector3.UnitY);
				Vector3 along = Along(points, count, i);
				Vector3 side = Vector3.Cross(along, facing);
				if (side.LengthSquared < 1e-12f)
				{
					// The line runs along the facing direction here (straight at the camera): keep the last side.
					side = previousSide.LengthSquared > 0f ? previousSide : Perpendicular(along);
				}

				side = side.Normalized();
				previousSide = side;
				float half = point.Width * 0.5f;

				if (!(cornerVertices > 0 && i > 0 && i < count - 1 && BuildCorner(points, i, facing, along, side, half, cornerVertices, vScale)))
				{
					uint left = AddVertex(point.Position + side * half, point, 0f, facing, along, side);
					uint right = AddVertex(point.Position - side * half, point, vScale, facing, along, side);
					inLeft[i] = outLeft[i] = left;
					inRight[i] = outRight[i] = right;
				}

				if (capVertices > 0 && (i == 0 || i == count - 1))
				{
					// Away from the line: before its start, past its end.
					BuildCap(point, i == 0 ? -along : along, facing, along, side, half, capVertices, vScale, inLeft[i], inRight[i]);
				}

				if (i > 0)
				{
					// left = position + side, side = along x facing: (L0, L1, R0) and (R0, L1, R1) face the facing direction.
					AddIndices(outLeft[i - 1], inLeft[i], outRight[i - 1]);
					AddIndices(outRight[i - 1], inLeft[i], inRight[i]);
				}
			}

			indexCount = IndexCount - firstIndex;
			min = boundsMin;
			max = boundsMax;
			return indexCount > 0;
		}

		#endregion

		#region PrivateMethods

		// A rounded joint: the inner side keeps one vertex, the outer side gets an arc from the side of the segment before
		// to the side of the segment after, filled by a fan from the inner vertex. False when the line runs straight on.
		private bool BuildCorner(LinePoint[] points, int i, Vector3 facing, Vector3 along, Vector3 side, float half, int cornerVertices, float vScale)
		{
			LinePoint point = points[i];
			Vector3 before = SafeNormalize(point.Position - points[i - 1].Position, along);
			Vector3 after = SafeNormalize(points[i + 1].Position - point.Position, along);
			Vector3 sideBefore = Vector3.Cross(before, facing);
			Vector3 sideAfter = Vector3.Cross(after, facing);
			if (sideBefore.LengthSquared < 1e-12f || sideAfter.LengthSquared < 1e-12f)
			{
				return false;
			}

			sideBefore.Normalize();
			sideAfter.Normalize();
			// Signed angle from one side to the other about the facing axis.
			float turn = MathF.Atan2(Vector3.Dot(Vector3.Cross(sideBefore, sideAfter), facing), Vector3.Dot(sideBefore, sideAfter));
			if (MathF.Abs(turn) < 1e-3f)
			{
				return false;
			}

			// The line bends towards +side: that side is inside the bend and the arc goes on the other one.
			float outer = Vector3.Dot(after, sideBefore) > 0f ? -1f : 1f;
			float outerV = outer > 0f ? 0f : vScale;
			uint inner = AddVertex(point.Position - side * (outer * half), point, vScale - outerV, facing, along, side);
			uint first = (uint)VertexCount;
			Vector3 axisCross = Vector3.Cross(facing, sideBefore);
			for (int k = 0; k <= cornerVertices + 1; k++)
			{
				float angle = turn * k / (cornerVertices + 1);
				Vector3 direction = sideBefore * MathF.Cos(angle) + axisCross * MathF.Sin(angle);
				AddVertex(point.Position + direction * (outer * half), point, outerV, facing, along, side);
			}

			uint last = (uint)VertexCount - 1;
			for (uint k = first; k < last; k++)
			{
				AddTriangle(inner, k, k + 1, facing);
			}

			if (outer > 0f)
			{
				inLeft[i] = first;
				outLeft[i] = last;
				inRight[i] = outRight[i] = inner;
			}
			else
			{
				inRight[i] = first;
				outRight[i] = last;
				inLeft[i] = outLeft[i] = inner;
			}

			return true;
		}

		// A half disc from the left vertex round the outward direction to the right vertex, fanned from the point.
		private void BuildCap(LinePoint point, Vector3 outward, Vector3 facing, Vector3 along, Vector3 side, float half, int capVertices, float vScale, uint left, uint right)
		{
			uint center = AddVertex(point.Position, point, vScale * 0.5f, facing, along, side);
			uint previous = left;
			for (int k = 1; k <= capVertices; k++)
			{
				float angle = MathF.PI * k / (capVertices + 1);
				float cos = MathF.Cos(angle);
				uint next = AddVertex(point.Position + (side * cos + outward * MathF.Sin(angle)) * half, point, vScale * (1f - cos) * 0.5f, facing, along, side);
				AddTriangle(center, previous, next, facing);
				previous = next;
			}

			AddTriangle(center, previous, right, facing);
		}

		// The line's direction at a point (towards its end): the mean of the segments meeting there.
		private static Vector3 Along(LinePoint[] points, int count, int i)
		{
			if (i == 0)
			{
				return SafeNormalize(points[1].Position - points[0].Position, Vector3.UnitZ);
			}

			Vector3 before = SafeNormalize(points[i].Position - points[i - 1].Position, Vector3.UnitZ);
			if (i == count - 1)
			{
				return before;
			}

			Vector3 after = SafeNormalize(points[i + 1].Position - points[i].Position, before);
			Vector3 sum = before + after;
			return sum.LengthSquared > 1e-8f ? sum.Normalized() : after;
		}

		private uint AddVertex(Vector3 position, LinePoint point, float v, Vector3 facing, Vector3 along, Vector3 side)
		{
			if (VertexCount == Vertices.Length)
			{
				Array.Resize(ref Vertices, Vertices.Length * 2);
			}

			Vertices[VertexCount] = new Vertex(position, facing, new Vector2(point.U, v), along, -side, point.Color);
			boundsMin = Vector3.ComponentMin(boundsMin, position);
			boundsMax = Vector3.ComponentMax(boundsMax, position);
			return (uint)VertexCount++;
		}

		private void AddIndices(uint a, uint b, uint c)
		{
			if (IndexCount + 3 > Indices.Length)
			{
				Array.Resize(ref Indices, Indices.Length * 2);
			}

			Indices[IndexCount++] = a;
			Indices[IndexCount++] = b;
			Indices[IndexCount++] = c;
		}

		// A triangle turned to face the facing direction (fans and caps, whose winding depends on the side).
		private void AddTriangle(uint a, uint b, uint c, Vector3 facing)
		{
			Vector3 pa = Vertices[a].position;
			Vector3 normal = Vector3.Cross(Vertices[b].position - pa, Vertices[c].position - pa);
			if (Vector3.Dot(normal, facing) < 0f)
			{
				(b, c) = (c, b);
			}

			AddIndices(a, b, c);
		}

		private void EnsurePointCapacity(int count)
		{
			if (inLeft.Length >= count)
			{
				return;
			}

			int size = Math.Max(count, inLeft.Length * 2);
			inLeft = new uint[size];
			inRight = new uint[size];
			outLeft = new uint[size];
			outRight = new uint[size];
		}

		private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
		{
			float length = value.Length;
			return length > 1e-12f ? value / length : fallback;
		}

		private static Vector3 Perpendicular(Vector3 direction)
		{
			Vector3 axis = MathF.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
			return Vector3.Cross(direction, axis);
		}

		#endregion
	}
}
