using System;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine
{
	internal sealed class VertexBuffer<TVertex>
		where TVertex : struct // vertices must be structs so we can copy them to GPU memory easily
	{
		#region PrivateFields

		private readonly int vertexSize;

		private readonly int indhandle;
		private readonly int verthandle;
		private TVertex[] vertices = new TVertex[4];
		private int[] indicedata = { };

		private int count;

		#endregion

		#region Constructors

		public VertexBuffer(int vertexSize)
		{
			this.vertexSize = vertexSize;

			// generate indices buffer
			indhandle = GL.GenBuffer();
			// generate the actual Vertex Buffer Object
			verthandle = GL.GenBuffer();
		}

		#endregion

		#region PublicMethods

		public void AddVertex(TVertex v)
		{
			// resize array if too small
			if (count == vertices.Length)
			{
				Array.Resize(ref vertices, count * 2);
			}
			// add vertex
			vertices[count] = v;
			count++;
		}

		public void SetIndices(int[] tab)
		{
			Array.Resize(ref indicedata, tab.Length);
			tab.CopyTo(indicedata, 0);
		}

		public void Bind()
		{
			// make this the active array buffer
			GL.BindBuffer(BufferTarget.ArrayBuffer, verthandle);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indhandle);
		}

		public void BufferData()
		{
			// copy contained vertices to GPU memory
			GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(vertexSize * count), vertices, BufferUsageHint.StaticDraw);
			GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(indicedata.Length * sizeof(int)), indicedata, BufferUsageHint.StaticDraw);
		}

		public void Draw()
		{
			// draw buffered vertices as triangles
			GL.DrawArrays(PrimitiveType.Triangles, 0, count);
			//GL.DrawElements(BeginMode.Triangles, indicedata.Length, DrawElementsType.UnsignedInt, 0);
		}

		public void Delete()
		{
			// Delete buffers
			GL.DeleteBuffers(2, new[] { indhandle, verthandle });

			// unbind objects to reset state
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
		}

		#endregion
	}
}
