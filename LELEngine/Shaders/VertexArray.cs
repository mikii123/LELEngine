using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Vertex array object. The layout uses fixed attribute locations so it is valid for any shader program.
	/// </summary>
	internal sealed class VertexArray<TVertex>
		where TVertex : struct
	{
		#region PrivateFields

		private readonly int handle;

		#endregion

		#region Constructors

		public VertexArray(VertexBuffer<TVertex> vertexBuffer, params VertexAttribute[] attributes)
		{
			// create new vertex array object
			GL.GenVertexArrays(1, out handle);

			// bind the object so we can modify it
			Bind();

			// bind the vertex buffer object
			vertexBuffer.Bind();

			// set all attributes
			foreach (VertexAttribute attribute in attributes)
			{
				attribute.Set();
			}

			// unbind objects to reset state
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
		}

		#endregion

		#region PublicMethods

		public void Bind()
		{
			// bind for usage (modification or rendering)
			GL.BindVertexArray(handle);
		}

		public void Delete()
		{
			// Delete array
			GL.DeleteVertexArray(handle);

			// unbind objects to reset state
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
		}

		#endregion
	}
}
