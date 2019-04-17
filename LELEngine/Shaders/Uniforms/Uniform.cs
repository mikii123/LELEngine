namespace LELEngine.Shaders.Uniforms
{
	public class Uniform
	{
		#region PublicFields

		public string Name;

		#endregion

		#region PublicMethods

		public virtual void Set(ShaderProgram program)
		{ }

		#endregion
	}
}
