using OpenTK.Graphics.OpenGL4;

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
			int handle = program.GetUniformLocation(Name);
			if (handle >= 0) GL.Uniform1(handle, Value);
		}

		#endregion
	}
}
