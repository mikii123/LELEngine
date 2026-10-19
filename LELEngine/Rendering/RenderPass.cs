namespace LELEngine.Rendering
{
	/// <summary>
	///     When a pass runs in a frame rendered by a camera stack (<see cref="Camera.Stack" />): the frame passes once
	///     before any camera draws, then for each camera of the stack in order the passes of that camera, then the
	///     final passes. With one camera that is the order of the pass list.
	/// </summary>
	public enum RenderPassScope
	{
		/// <summary>Once per frame before the cameras draw, with the lighting camera's view (shadow maps, the GI volumes).</summary>
		Frame,

		/// <summary>For every camera of the stack, with its view and its layers (the geometry passes).</summary>
		EveryCamera,

		/// <summary>For the stack's lighting camera only (screen-space GI, debug views, callbacks).</summary>
		LightingCamera,

		/// <summary>For the first camera of the stack only, the one that cleared the image (the sky).</summary>
		BaseCamera,

		/// <summary>Once after every camera drew (post-processing, UI).</summary>
		Final
	}

	/// <summary>
	///     One stage of the frame. Passes are executed in order by <see cref="Renderer" />
	///     and communicate through <see cref="RenderContext" /> and the targets they own.
	/// </summary>
	public abstract class RenderPass
	{
		#region PublicFields

		public abstract string Name { get; }
		public bool Enabled = true;

		/// <summary>The pass also runs when the scene has no camera (screen-space overlays such as the UI).</summary>
		public virtual bool RunsWithoutCamera => false;

		/// <summary>When the pass runs in a camera stack (once, for the lighting camera, by default).</summary>
		public virtual RenderPassScope Scope => RenderPassScope.LightingCamera;

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

		/// <summary>
		///     Called when the rendered scene is replaced: drop every reference to the old scene's objects, so an
		///     unloaded scene (and the script assembly behind its components) can be collected.
		/// </summary>
		public virtual void ReleaseSceneReferences()
		{ }

		public virtual void Dispose()
		{ }

		#endregion
	}
}
