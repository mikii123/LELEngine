using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

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
		#region PublicFields

		/// <summary>Texture unit of the copy in the drawing shader: above every unit the engine's lighting and the grab use.</summary>
		public const int PreferredTextureUnit = 22;

		/// <summary>The unit the copy binds to: <see cref="PreferredTextureUnit" />, or on a context with fewer units the last material unit.</summary>
		public static int TextureUnit => PreferredTextureUnit < GLCapabilities.MaxTextureUnits ? PreferredTextureUnit : Material.MaxTextureUnits - 1;

		#endregion

		#region PublicMethods

		/// <summary>Binds the depth copy (or tells the shader there is none) to a program that reads the scene's depth.</summary>
		public static void SetUniforms(ShaderProgram program, RenderContext context, VFX.VfxDepthCapture capture)
		{
			bool available = capture != null && capture.Valid && capture.Depth != null;
			program.SetVector4("lelSceneDepthParams", SceneGrab.DepthParams(context.Projection));
			program.SetFloat("lelSceneDepthAvailable", available ? 1f : 0f);
			if (available)
			{
				program.SetTexture("lelSceneDepth", TextureTarget.Texture2D, capture.Depth.Handle, TextureUnit);
			}
		}

		#endregion
	}
}
