using OpenTK;
using OpenTK.Graphics;

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
        {
            this.position = position;
            this.texcoord = uv;
            this.normal = norm;
            this.color = Color4.White;
            this.tangent = tang;
            this.bitangent = bitang;
        }
    }
}