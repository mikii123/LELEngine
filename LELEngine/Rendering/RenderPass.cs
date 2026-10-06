namespace LELEngine.Rendering
{
	/// <summary>
	///     One stage of the frame. Passes are executed in order by <see cref="Renderer" />
	///     and communicate through <see cref="RenderContext" /> and the targets they own.
	/// </summary>
	public abstract class RenderPass
	{
		#region PublicFields

		public abstract string Name { get; }
		public bool Enabled = true;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Called once when the pass is added to a renderer. Allocate GPU resources here.
		/// </summary>
		public virtual void Initialize(Renderer renderer)
		{ }

		public abstract void Execute(RenderContext context);

		/// <summary>
		///     Called when the window (and scene target) changes size.
		/// </summary>
		public virtual void Resize(int width, int height)
		{ }

		public virtual void Dispose()
		{ }

		#endregion
	}
}
