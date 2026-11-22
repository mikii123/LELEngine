using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A triangle that covers the whole target: three vertices without any buffer, positioned by the vertex stage
	///     from their index. Full-screen passes draw it with the program in use.
	/// </summary>
	public sealed class FullscreenQuad
	{
		#region PublicMethods

		public void Draw()
		{
			CommandList commands = GraphicsDevice.Current.Commands;
			commands.SetVertexInput(VertexInputLayout.Empty);
			commands.Draw(3);
			DrawStatistics.Draw(1);
		}

		public void Delete()
		{ }

		#endregion
	}
}
