using OpenTK;
using OpenTK.Graphics.OpenGL4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine
{
    sealed class VertexBuffer<TVertex>
    where TVertex : struct // vertices must be structs so we can copy them to GPU memory easily
    {
        private readonly int vertexSize;
        private TVertex[] vertices = new TVertex[4];
        private int[] indicedata = new int[] { };

        private int count;

        private readonly int indhandle;
        private readonly int verthandle;

        public VertexBuffer(int vertexSize)
        {
            this.vertexSize = vertexSize;

            // generate indices buffer
            this.indhandle = GL.GenBuffer();
            // generate the actual Vertex Buffer Object
            this.verthandle = GL.GenBuffer();
        }

        public void AddVertex(TVertex v)
        {
            // resize array if too small
            if (this.count == this.vertices.Length)
                Array.Resize(ref this.vertices, this.count * 2);
            // add vertex
            this.vertices[count] = v;
            this.count++;
        }

        public void SetIndices(int[] tab)
        {
            Array.Resize(ref this.indicedata, tab.Length);
            tab.CopyTo(indicedata, 0);
        }

        public void Bind()
        {
            // make this the active array buffer
            GL.BindBuffer(BufferTarget.ArrayBuffer, this.verthandle);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, this.indhandle);
        }

        public void BufferData()
        {
            // copy contained vertices to GPU memory
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(this.vertexSize * this.count), this.vertices, BufferUsageHint.StaticDraw);
            GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(indicedata.Length * sizeof(int)), indicedata, BufferUsageHint.StaticDraw);
        }

        public void Draw()
        {
            // draw buffered vertices as triangles
            GL.DrawArrays(PrimitiveType.Triangles, 0, this.count);
            //GL.DrawElements(BeginMode.Triangles, indicedata.Length, DrawElementsType.UnsignedInt, 0);
        }
    }
}
