using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Draws a single screen-covering triangle. The vertex shader generates positions from gl_VertexID,
	///     so an empty VAO is enough (core profile requires a bound VAO to draw).
	/// </summary>
	public sealed class FullscreenQuad
	{
		#region PrivateFields

		private int vao;

		#endregion

		#region Constructors

		public FullscreenQuad()
		{
			vao = GL.GenVertexArray();
		}

		#endregion

		#region PublicMethods

		public void Draw()
		{
			GL.BindVertexArray(vao);
			GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
			GL.BindVertexArray(0);
		}

		public void Delete()
		{
			GL.DeleteVertexArray(vao);
			vao = 0;
		}

		#endregion
	}
}
