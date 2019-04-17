using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Matrix4 : Uniform
	{
		#region PublicFields

		public OpenTK.Matrix4 Matrix;

		#endregion

		#region Constructors

		public Matrix4(string name)
		{
			Name = name;
		}

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			// get uniform location
			int handle = program.GetUniformLocation(Name);

			// set uniform value
			GL.UniformMatrix4(handle, false, ref Matrix);
		}

		#endregion
	}
}
