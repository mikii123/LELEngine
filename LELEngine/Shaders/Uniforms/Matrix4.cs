using LELEngine.Graphics;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Matrix4 : Uniform
	{
		#region PublicFields

		public LELEngine.Mathematics.Matrix4 Matrix;

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
			program.SetMatrix4(Name, Matrix);
		}

		#endregion
	}
}
