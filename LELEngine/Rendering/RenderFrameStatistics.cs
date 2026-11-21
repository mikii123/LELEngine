namespace LELEngine.Rendering
{
	/// <summary>
	///     What one frame of a renderer (<see cref="Renderer.RenderFrame" />, the cameras rendering into textures it renders
	///     first included) cost on the CPU and asked the GPU to draw: <see cref="Renderer.LastFrame" />. Its GPU time is the
	///     renderer's <see cref="Renderer.Profiler" /> (timer queries, a few frames behind).
	/// </summary>
	public struct RenderFrameStatistics
	{
		/// <summary>
		///     The frames the renderer has rendered, this one included (<see cref="Renderer.RenderFrame" /> calls, those without a
		///     camera too; 0: none yet): it changes with every frame.
		/// </summary>
		public int Frame;

		/// <summary>The frame's time on the CPU (milliseconds).</summary>
		public double CpuMilliseconds;

		/// <summary>Draw calls (<see cref="DrawStatistics" />: a multi-draw is one).</summary>
		public long DrawCalls;

		/// <summary>Triangles of the draw calls whose counts the CPU gives (not those of <see cref="IndirectDrawCalls" />).</summary>
		public long Triangles;

		/// <summary>Draw calls whose counts the GPU decides: the GPU-driven geometry's multi-draws, particle outputs.</summary>
		public long IndirectDrawCalls;

		/// <summary>Cameras of the stack the frame rendered (0: no camera; the clear colour and the UI only).</summary>
		public int Cameras;

		/// <summary>
		///     Renderers in the cameras' lists (on their layers, the levels of detail drawn, before frustum culling), summed over
		///     the stack: with opaque parts, with transparent parts (a renderer with both is in both).
		/// </summary>
		public int OpaqueRenderers;

		public int TransparentRenderers;

		/// <summary>
		///     What the cameras' frustums left out on the CPU (<see cref="ViewCulling" />), summed over the stack: mesh
		///     renderers drawn one by one (transparent, or opaque outside the GPU-driven tables), parts of static batches,
		///     trails, custom renderers. The GPU-driven instances the GPU culls are not in it (their number stays on the GPU).
		/// </summary>
		public int CulledRenderers;

		/// <summary>Instances of the GPU-driven geometry (culled on the GPU; 0 when the frame did not draw GPU-driven).</summary>
		public int GpuInstances;

		/// <summary>Lights in the list the shaders read this frame.</summary>
		public int Lights;

		/// <summary>The output's size (<see cref="Renderer.Width" /> x <see cref="Renderer.Height" />).</summary>
		public int Width;

		public int Height;

		/// <summary>The scene's size (<see cref="Renderer.RenderWidth" /> x <see cref="Renderer.RenderHeight" />: super sampled).</summary>
		public int RenderWidth;

		public int RenderHeight;
	}
}
