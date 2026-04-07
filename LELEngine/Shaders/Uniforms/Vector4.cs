using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Vector4 : Uniform
	{
		#region PublicFields

		public OpenTK.Mathematics.Vector4 Vector;

		#endregion

		#region Constructors

		public Vector4(string name, OpenTK.Mathematics.Vector4 vector)
		{
			Name = name;
			Vector = vector;
		}

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			int handle = program.GetUniformLocation(Name);
			GL.Uniform4(handle, ref Vector);
		}

		#endregion
	}
}
