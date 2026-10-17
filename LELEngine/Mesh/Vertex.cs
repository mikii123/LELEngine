using OpenTK.Mathematics;

namespace LELEngine
{
	public struct Vertex
	{
		public const int Size = (3 + 4 + 2 + 3 + 3 + 3) * 4; // size of struct in bytes

		public readonly Vector3 position;
		public readonly Color4 color;
		public readonly Vector2 texcoord;
		public readonly Vector3 normal;
		public readonly Vector3 tangent;
		public readonly Vector3 bitangent;

		public Vertex(Vector3 position, Vector3 norm, Vector2 uv, Vector3 tang, Vector3 bitang)
			: this(position, norm, uv, tang, bitang, Color4.White)
		{
		}

		public Vertex(Vector3 position, Vector3 norm, Vector2 uv, Vector3 tang, Vector3 bitang, Color4 color)
		{
			this.position = position;
			texcoord = uv;
			normal = norm;
			this.color = color;
			tangent = tang;
			bitangent = bitang;
		}
	}
}
