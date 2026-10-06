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

		/// <summary>
		///     Shallow copy. Value uniforms become independent; textures share the GPU handle.
		/// </summary>
		public Uniform Clone()
		{
			return (Uniform)MemberwiseClone();
		}

		#endregion
	}
}
