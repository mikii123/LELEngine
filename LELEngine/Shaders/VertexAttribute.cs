using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Describes a single vertex attribute bound to a fixed location (see <see cref="VertexLayout" />).
	/// </summary>
	internal sealed class VertexAttribute
	{
		#region PrivateFields

		private readonly int location;
		private readonly int size;
		private readonly VertexAttribPointerType type;
		private readonly bool normalize;
		private readonly int stride;
		private readonly int offset;

		#endregion

		#region Constructors

		public VertexAttribute(int location, int size, VertexAttribPointerType type, int stride, int offset, bool normalize = false)
		{
			this.location = location;
			this.size = size;
			this.type = type;
			this.stride = stride;
			this.offset = offset;
			this.normalize = normalize;
		}

		#endregion

		#region PublicMethods

		public void Set()
		{
			GL.EnableVertexAttribArray(location);
			GL.VertexAttribPointer(
				location,
				size,
				type,
				normalize,
				stride,
				offset);
		}

		#endregion
	}
}
