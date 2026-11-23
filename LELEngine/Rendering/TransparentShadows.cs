using System.Collections.Generic;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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
				if (!renderer.CastShadows || renderer.Mesh == null)
				{
					continue;
				}

				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					if (renderer.IsSubMeshTransparent(i) && renderer.IsSubMeshShadowCaster(i))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		///     Gives a shadow map target its transmittance attachment when it has none yet. A pass draws into the
		///     attachments its target had when it began: called before the shadow map's pass begins, in a frame that
		///     has casters (<see cref="HasCasters" />).
		/// </summary>
		public static void EnsureTransmittance(Framebuffer target)
		{
			if (target.ColorAttachments.Count == 0)
			{
				// 32-bit: a holds a depth that receivers compare with their own (half floats round it by ~1/2048, more than
				// the shadow bias: a transparent receiver then shadows itself in bands along the light's depth).
				target.AddColorAttachment(RenderTextureFormat.RGBA32F);
				target.Validate();
			}
		}

		/// <summary>
		///     Draws the frame's transparent casters into the transmittance attachment of <paramref name="target" />
		///     (the shadow map whose pass is open, begun after <see cref="EnsureTransmittance" />, its opaque casters
		///     already drawn) within the current viewport (a cascade's tile): the attachment is cleared when
		///     <paramref name="clear" /> is set (the first tile). Returns it, or null when nothing was drawn (receivers
		///     then ignore it). Leaves the colour writes off, as the opaque casters had them.
		/// </summary>
		public static RenderTexture Draw(RenderContext context, Framebuffer target, Matrix4 lightView, Matrix4 lightProjection, bool clear = true)
		{
			return HasCasters(context) ? DrawCasters(context, target, lightView, lightProjection, clear) : null;
		}

		/// <summary>
		///     <see cref="Draw" /> for a frame known to have casters (<see cref="HasCasters" />, asked once by a pass that
		///     draws several tiles: the question walks every transparent renderer). <paramref name="culling" />: the
		///     light's view, begun by the caller (null: every caster draws); casters outside it are left out.
		/// </summary>
		public static RenderTexture DrawCasters(RenderContext context, Framebuffer target, Matrix4 lightView, Matrix4 lightProjection, bool clear = true, ViewCulling culling = null)
		{
			ShaderProgram program = context.TransparentShadowProgram;
			if (program == null || !program.IsLinked)
			{
				return null;
			}

			if (target.ColorAttachments.Count == 0)
			{
				// The open pass has no transmittance attachment to draw into.
				return null;
			}

			CommandList commands = Gfx.Commands;
			RenderTexture transmittance = target.ColorAttachments[0];
			commands.State.ColorMask = ColorWriteMask.All;
			if (clear)
			{
				commands.ClearColor(0, new ClearValue(1f, 1f, 1f, 1f));
			}

			// Behind an opaque caster the light is gone already: depth tested, not written (casters do not hide each other).
			// The colour multiplies what is there; the alpha keeps the smaller depth.
			ref RenderState state = ref commands.State;
			state.DepthWrite = false;
			state.DepthCompare = CompareFunction.Less;
			state.SetBlend(BlendFactor.Zero, BlendFactor.SrcColor, BlendFactor.One, BlendFactor.One);
			state.ColorOperation = BlendOperation.Add;
			state.AlphaOperation = BlendOperation.Min;

			program.Use();
			program.SetMatrix4("viewMatrix", lightView);
			program.SetMatrix4("projectionMatrix", lightProjection);
			foreach (MeshRenderer renderer in context.TransparentRenderers)
			{
				if (!renderer.CastShadows || renderer.Mesh == null)
				{
					continue;
				}

				// Asked once it is known to cast: most transparent renderers (effects) do not.
				bool asked = false;
				for (int i = 0; i < renderer.SubMeshCount; i++)
				{
					Material material = renderer.GetMaterial(i);
					if (material == null || !material.IsTransparent || !material.RenderState.CastShadows)
					{
						continue;
					}

					if (!asked)
					{
						asked = true;
						if (culling != null && !culling.IsVisible(renderer))
						{
							break;
						}
					}

					material.GetStandardParameters(out Vector4 color, out Vector4 unusedEmissive, out float unusedRoughness);
					program.SetVector4("shadowTint", color);
					// A double-sided pane shadows from both sides; a closed object multiplies its entry and exit.
					material.RenderState.ApplyCull();
					renderer.RenderSubMeshWith(program, i);
				}
			}

			ref RenderState after = ref commands.State;
			after.ColorOperation = BlendOperation.Add;
			after.AlphaOperation = BlendOperation.Add;
			after.Blend = false;
			after.DepthWrite = true;
			after.Culling = FaceCulling.Back;
			after.ColorMask = ColorWriteMask.None;
			return transmittance;
		}

		/// <summary>
		///     Gives a program the transmittance map for Engine/Shadows.glsl (null: receivers use the opaque shadow only,
		///     and the sampler reads no texture).
		/// </summary>
		public static void SetUniforms(ShaderProgram program, RenderTexture transmittance)
		{
			program.SetInt("shadowTransmittanceEnabled", transmittance != null ? 1 : 0);
			program.SetTexture("ShadowTransmittance", transmittance);
		}

		/// <summary>
		///     Binds a transmittance map for Engine/Shadows.glsl (null: receivers use the opaque shadow only). The sampler
		///     gets its unit either way: left on unit 0 it shares it with a sampler of another type, and a program lighting
		///     the GI then did not run at all (no light went into the voxels or the cards of a frame without transparent
		///     casters).
		/// </summary>
		// LEGACY(gl): used by Lighting.cs
		public static void SetUniforms(ShaderProgram program, RenderTexture transmittance, int textureUnit)
		{
			program.SetInt("shadowTransmittanceEnabled", transmittance != null ? 1 : 0);
			if (transmittance != null)
			{
				program.SetTexture("ShadowTransmittance", TextureTarget.Texture2D, transmittance.Handle, textureUnit);
			}
			else
			{
				program.SetInt("ShadowTransmittance", textureUnit);
			}
		}

		#endregion
	}
}
