using LELEngine.Graphics;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Vector3 : Uniform
	{
		#region PublicFields

		public LELEngine.Mathematics.Vector3 Vector;

		#endregion

		#region Constructors

		public Vector3(string name, LELEngine.Mathematics.Vector3 vector)
		{
			Name = name;
			Vector = vector;
		}

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			program.SetVector3(Name, Vector);
		}

		#endregion
	}
}
