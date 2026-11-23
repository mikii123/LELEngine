using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Vector4 : Uniform
	{
		#region PublicFields

		public LELEngine.Mathematics.Vector4 Vector;

		#endregion

		#region Constructors

		public Vector4(string name, LELEngine.Mathematics.Vector4 vector)
		{
			Name = name;
			Vector = vector;
		}

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			program.SetVector4(Name, Vector);
		}

		#endregion
	}
}
