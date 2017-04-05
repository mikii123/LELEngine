using OpenTK;
using OpenTK.Graphics;

namespace LELEngine
{
    sealed class VertexTemplate
    {
        public const int Size = (3 + 4 + 2 + 3 + 3 + 3) * 4; // size of struct in bytes

        public Vector3 position;
        public Color4 color;
        public Vector2 texcoord;
        public Vector3 normal;
        public Vector3 tangent;
        public Vector3 bitangent;
    }
}
