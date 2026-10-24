using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Vertices and indices written anew every frame (trails), in buffers of their own behind a vertex array of the
	///     standard layout (<see cref="VertexLayout" />), so any material shader draws them. Every upload orphans the
	///     buffers (the driver hands out fresh storage while the previous frame's draws still read the old one); they grow
	///     to the largest upload and never shrink.
	/// </summary>
	internal sealed class DynamicGeometry
	{
		#region PrivateFields

		private int vertexArray;
		private int vertexBuffer;
		private int indexBuffer;
		private int vertexCapacity;
		private int indexCapacity;

		#endregion

		#region PublicMethods

		public void Upload(Vertex[] vertices, int vertexCount, uint[] indices, int indexCount)
		{
			if (vertexCount == 0 || indexCount == 0)
			{
				return;
			}

			EnsureCreated();
			vertexCapacity = Math.Max(vertexCapacity, vertexCount);
			indexCapacity = Math.Max(indexCapacity, indexCount);

			// The element buffer binding belongs to the vertex array.
			GL.BindVertexArray(vertexArray);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
			GL.BufferData(BufferTarget.ArrayBuffer, vertexCapacity * Vertex.Size, IntPtr.Zero, BufferUsageHint.StreamDraw);
			GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertexCount * Vertex.Size, vertices);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
			GL.BufferData(BufferTarget.ElementArrayBuffer, indexCapacity * sizeof(uint), IntPtr.Zero, BufferUsageHint.StreamDraw);
			GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexCount * sizeof(uint), indices);
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
		}

		/// <summary>Draws a range of the uploaded indices with the bound program.</summary>
		public void Draw(int firstIndex, int indexCount)
		{
			if (vertexArray == 0 || indexCount == 0)
			{
				return;
			}

			GL.BindVertexArray(vertexArray);
			GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, (IntPtr)(firstIndex * sizeof(uint)));
		}

		public void Delete()
		{
			if (vertexArray != 0)
			{
				GL.DeleteVertexArray(vertexArray);
				GL.DeleteBuffer(vertexBuffer);
				GL.DeleteBuffer(indexBuffer);
			}

			vertexArray = vertexBuffer = indexBuffer = 0;
			vertexCapacity = indexCapacity = 0;
		}

		#endregion

		#region PrivateMethods

		private void EnsureCreated()
		{
			if (vertexArray != 0)
			{
				return;
			}

			vertexArray = GL.GenVertexArray();
			vertexBuffer = GL.GenBuffer();
			indexBuffer = GL.GenBuffer();
			GL.BindVertexArray(vertexArray);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
			foreach (VertexAttribute attribute in VertexLayout.CreateStandardAttributes())
			{
				attribute.Set();
			}

			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
		}

		#endregion
	}
}
