using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Tunables for the frame pipeline. Shadow settings live in <see cref="Lighting.Shadows" />.
	/// </summary>
	public sealed class RenderSettings
	{
		#region PublicFields

		/// <summary>
		///     Render all opaque geometry depth-only first, then shade with depth test EQUAL.
		///     Avoids overdraw in the (expensive) material pass.
		/// </summary>
		public bool DepthPrepass = true;

		/// <summary>
		///     GPU-driven geometry (see <see cref="GpuScene" />): the shadow, depth prepass and opaque passes cull on the
		///     GPU and draw with multi-draw indirect calls. Off: one draw call per object from the CPU.
		/// </summary>
		public bool GpuDriven = true;

		/// <summary>
		///     Apply ACES tonemapping when resolving the HDR scene to the backbuffer.
		///     When false, HDR color is simply clamped (gamma is still applied).
		/// </summary>
		public bool Tonemap = true;

		public float Exposure = 1.0f;

		public Color4 ClearColor = new Color4(0f, 0f, 0f, 1f);

		/// <summary>
		///     glFlush after every pass so the GPU starts a frame while the CPU is still submitting it. The Intel
		///     driver otherwise kicks the frame off at SwapBuffers, leaving the GPU idle for the submission time.
		/// </summary>
		public bool FlushBetweenPasses = false;

		/// <summary>glFlush once after the first pass so the GPU starts the frame while the rest is submitted.</summary>
		public bool FlushAfterFirstPass = true;

		#endregion
	}
}
