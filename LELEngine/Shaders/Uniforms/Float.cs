using LELEngine.Graphics;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Float : Uniform
	{
		#region PublicFields

		public float Value;

		#endregion

		#region Constructors

		public Float(string name, float value)
		{
			Name = name;
			Value = value;
		}

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			program.SetFloat(Name, Value);
		}

		#endregion
	}
}
