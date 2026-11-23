using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The opaque scene's depth for transparent materials that read it (Unity's _CameraDepthTexture, e.g. soft
	///     particles): shaders declaring <c>SceneDepth On</c> (<see cref="ShaderRenderState.SceneDepth" />) include
	///     Engine/SceneDepth.glsl, and the transparent pass copies the depth buffer once the opaques are drawn (the copy
	///     the particle effects read, <see cref="VFX.VfxRuntime.CaptureDepth" />), once per camera, and binds it to them.
	/// </summary>
	public static class SceneDepthTexture
	{
		#region PublicMethods

		/// <summary>
		///     Gives the depth copy (or tells the shader there is none) to a program that reads the scene's depth.
		///     <paramref name="pixelScale" />: scene pixels per pixel of the target drawn into (2 in the half-resolution layer).
		/// </summary>
		public static void SetUniforms(ShaderProgram program, RenderContext context, VFX.VfxDepthCapture capture, float pixelScale = 1f)
		{
			bool available = capture != null && capture.Valid && capture.Depth != null;
			program.SetVector4("lelSceneDepthParams", SceneGrab.DepthParams(context.Projection));
			program.SetFloat("lelSceneDepthAvailable", available ? 1f : 0f);
			program.SetFloat("lelSceneDepthPixelScale", pixelScale);
			if (available)
			{
				program.SetTexture("lelSceneDepth", capture.Depth);
			}
		}

		#endregion
	}
}
