using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Shadows of the transparent renderers (translucent shadow maps). Every shadow map target gets a second
	///     attachment, the transmittance map: rgb = the light that passes the transparent casters at that texel
	///     (1 where there are none; a tinted pane multiplies in mix(1, albedo, opacity), two panes multiply twice),
	///     a = the nearest transparent caster's depth (1 where there are none). The casters draw into it after the
	///     opaque casters, depth tested against them but not writing depth, with multiplicative colour blending and
	///     a minimum blend on the alpha. Receivers (Engine/Shadows.glsl) multiply their opaque shadow by the
	///     transmittance when they lie behind that depth: a 35% glass casts a 35% shadow of its own colour, smooth,
	///     in the camera shadow map and in the shadow maps that light the voxels and the surface cache alike.
	/// </summary>
	internal static class TransparentShadows
	{
		#region PublicMethods

		/// <summary>The frame has transparent renderers that cast shadows (the transmittance map is worth drawing).</summary>
		public static bool HasCasters(RenderContext context)
		{
			IReadOnlyList<MeshRenderer> casters = context.TransparentRenderers;
			if (casters == null)
			{
				return false;
			}

			foreach (MeshRenderer renderer in casters)
			{
				if (renderer.CastShadows && renderer.Mesh != null && renderer.Material != null)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		///     Draws the frame's transparent casters into the transmittance attachment of <paramref name="target" />
		///     (the bound shadow map, its opaque casters already drawn): the attachment is added on first use and
		///     cleared every time. Returns it, or null when nothing was drawn (receivers then ignore it). Leaves the
		///     colour writes off, as the opaque casters had them.
		/// </summary>
		public static RenderTexture Draw(RenderContext context, Framebuffer target, Matrix4 lightView, Matrix4 lightProjection)
		{
			ShaderProgram program = context.TransparentShadowProgram;
			if (!HasCasters(context) || program == null || !program.IsLinked)
			{
				return null;
			}

			if (target.ColorAttachments.Count == 0)
			{
				target.AddColorAttachment(RenderTextureFormat.RGBA16F);
				target.Validate();
				target.Bind();
			}

			RenderTexture transmittance = target.ColorAttachments[0];
			GLState.SetColorWrite(true);
			GL.ClearBuffer(ClearBuffer.Color, 0, new[] { 1f, 1f, 1f, 1f });

			// Behind an opaque caster the light is gone already: depth tested, not written (casters do not hide each other).
			GL.DepthMask(false);
			GL.DepthFunc(DepthFunction.Less);
			GL.Enable(EnableCap.Blend);
			GL.BlendEquationSeparate(BlendEquationMode.FuncAdd, BlendEquationMode.Min);
			GL.BlendFuncSeparate(BlendingFactorSrc.Zero, BlendingFactorDest.SrcColor, BlendingFactorSrc.One, BlendingFactorDest.One);

			program.Use();
			program.SetMatrix4("viewMatrix", lightView);
			program.SetMatrix4("projectionMatrix", lightProjection);
			foreach (MeshRenderer renderer in context.TransparentRenderers)
			{
				Material material = renderer.Material;
				if (!renderer.CastShadows || renderer.Mesh == null || material == null)
				{
					continue;
				}

				material.GetStandardParameters(out Vector4 color, out Vector4 unusedEmissive, out float unusedRoughness);
				program.SetVector4("shadowTint", color);
				// A double-sided pane shadows from both sides; a closed object multiplies its entry and exit.
				material.RenderState.ApplyCull();
				renderer.RenderWith(program);
			}

			GL.BlendEquation(BlendEquationMode.FuncAdd);
			GL.Disable(EnableCap.Blend);
			GL.DepthMask(true);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);
			return transmittance;
		}

		/// <summary>Binds a transmittance map for Engine/Shadows.glsl (null: receivers use the opaque shadow only).</summary>
		public static void SetUniforms(ShaderProgram program, RenderTexture transmittance, int textureUnit)
		{
			program.SetInt("shadowTransmittanceEnabled", transmittance != null ? 1 : 0);
			if (transmittance != null)
			{
				program.SetTexture("ShadowTransmittance", TextureTarget.Texture2D, transmittance.Handle, textureUnit);
			}
		}

		#endregion
	}
}
