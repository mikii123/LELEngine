namespace LELEngine
{
	/// <summary>The kind of a <see cref="PhysicsShape2D" />.</summary>
	public enum PhysicsShapeType2D
	{
		/// <summary>One point, the centre, and the radius.</summary>
		Circle,

		/// <summary>Two points, the centres of its round ends, and their radius.</summary>
		Capsule,

		/// <summary>Its 3 to 8 corners (convex, counter-clockwise), rounded by the radius when that is above 0.</summary>
		Polygon,

		/// <summary>A segment's two points (an edge or a chain's segment), radius 0.</summary>
		Edge
	}

	/// <summary>
	///     One physics shape of a collider as the simulation holds it (<see cref="Collider2D.GetShapes" />): its kind, its
	///     points in a vertex list shared by the shapes, and its radius. Unity's PhysicsShape2D, read only.
	/// </summary>
	public struct PhysicsShape2D
	{
		public PhysicsShapeType2D shapeType;

		/// <summary>The index of its first point in the vertex list.</summary>
		public int vertexStartIndex;

		public int vertexCount;
		public float radius;
	}
}
