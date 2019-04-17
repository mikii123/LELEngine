using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	internal sealed class VertexAttribute
	{
		#region PrivateFields

		private readonly string name;
		private readonly int size;
		private readonly VertexAttribPointerType type;
		private readonly bool normalize;
		private readonly int stride;
		private readonly int offset;

		#endregion

		#region Constructors

		public VertexAttribute(string name, int size, VertexAttribPointerType type, int stride, int offset, bool normalize = false)
		{
			this.name = name;
			this.size = size;
			this.type = type;
			this.stride = stride;
			this.offset = offset;
			this.normalize = normalize;
		}

		#endregion

		#region PublicMethods

		public void Set(ShaderProgram program)
		{
			// get location of attribute from shader program
			int index = program.GetAttributeLocation(name);

			// enable and set attribute
			GL.EnableVertexAttribArray(index);
			GL.VertexAttribPointer(
				index,
				size,
				type,
				normalize,
				stride,
				offset);
		}

		#endregion
	}
}
