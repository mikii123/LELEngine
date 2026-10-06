using System;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine
{
	/// <summary>
	///     Vertex + index buffer pair. Data is uploaded once (or whenever marked dirty), not every frame.
	/// </summary>
	internal sealed class VertexBuffer<TVertex>
		where TVertex : struct // vertices must be structs so we can copy them to GPU memory easily
	{
		#region PublicFields

		public int VertexCount => count;
		public int IndexCount => indicedata.Length;

		#endregion

		#region PrivateFields

		private readonly int vertexSize;

		private readonly int indhandle;
		private readonly int verthandle;
		private TVertex[] vertices = new TVertex[4];
		private int[] indicedata = { };

		private int count;
		private bool dirty = true;

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
			dirty = true;
		}

		public void SetIndices(int[] tab)
		{
			Array.Resize(ref indicedata, tab.Length);
			tab.CopyTo(indicedata, 0);
			dirty = true;
		}

		public void Bind()
		{
			// make this the active array buffer
			GL.BindBuffer(BufferTarget.ArrayBuffer, verthandle);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indhandle);
		}

		/// <summary>
		///     Uploads the CPU side data to the GPU if it changed since the last upload.
		///     Requires the buffers to be bound.
		/// </summary>
		public void BufferData()
		{
			if (!dirty)
			{
				return;
			}

			GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(vertexSize * count), vertices, BufferUsageHint.StaticDraw);
			GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(indicedata.Length * sizeof(int)), indicedata, BufferUsageHint.StaticDraw);
			dirty = false;
		}

		public void Draw()
		{
			// draw buffered vertices as triangles
			GL.DrawArrays(PrimitiveType.Triangles, 0, count);
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
