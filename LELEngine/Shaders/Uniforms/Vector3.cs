using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Vector3 : Uniform
	{
		#region PublicFields

		public OpenTK.Mathematics.Vector3 Vector;

		#endregion

		#region Constructors

		public Vector3(string name, OpenTK.Mathematics.Vector3 vector)
		{
			Name = name;
			Vector = vector;
		}

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			int handle = program.GetUniformLocation(Name);
			if (handle >= 0) GL.Uniform3(handle, ref Vector);
		}

		#endregion
	}
}
