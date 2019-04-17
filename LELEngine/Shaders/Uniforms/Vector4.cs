using OpenTK.Graphics.OpenGL;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Vector4 : Uniform
	{
		#region PublicFields

		public OpenTK.Vector4 Vector;

		#endregion

		#region Constructors

		public Vector4(string name, OpenTK.Vector4 vector)
		{
			Name = name;
			Vector = vector;
		}

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			// get uniform location
			int handle = program.GetUniformLocation(Name);

			// set uniform value
			GL.Uniform4(handle, ref Vector);
		}

		#endregion
	}
}
