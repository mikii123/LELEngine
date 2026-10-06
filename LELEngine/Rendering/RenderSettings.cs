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
		///     Apply ACES tonemapping when resolving the HDR scene to the backbuffer.
		///     When false, HDR color is simply clamped (gamma is still applied).
		/// </summary>
		public bool Tonemap = true;

		public float Exposure = 1.0f;

		public Color4 ClearColor = new Color4(0f, 0f, 0f, 1f);

		#endregion
	}
}
