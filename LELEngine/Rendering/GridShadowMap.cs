using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Directional shadow map whose orthographic frustum encloses the whole GI grid instead of following
	///     the camera. Used when lighting cached data (voxels, surface cache) that must not depend on the view.
	/// </summary>
	internal sealed class GridShadowMap
	{
		#region PublicFields

		public Matrix4 LightView { get; private set; }
		public Matrix4 LightProjection { get; private set; }
		public RenderTexture Depth => target.DepthAttachment;

		/// <summary>Transmittance of the transparent casters (<see cref="TransparentShadows" />), null when the last render drew none.</summary>
		public RenderTexture Transmittance { get; private set; }

		public int Size => target.Width;

		#endregion

		#region PrivateFields

		private Framebuffer target;

		#endregion

		#region Constructors

		public GridShadowMap(int size)
		{
			Create(size);
		}

		#endregion

		#region PublicMethods

		public void EnsureSize(int size)
		{
			if (target.Width != size)
			{
				target.Delete();
				Transmittance = null;
				Create(size);
			}
		}

		/// <summary>
		///     Renders all shadow casters among <paramref name="casters" /> (the opaque renderers) and the frame's
		///     transparent casters from the sun, covering the GI grid.
		/// </summary>
		public void Render(RenderContext context, IReadOnlyList<MeshRenderer> casters, GlobalIlluminationSettings gi)
		{
			Vector3 direction = DirectionalLight.This != null ? DirectionalLight.This.transform.forward : -Vector3.UnitY;
			if (direction.LengthSquared < 1e-6f)
			{
				direction = -Vector3.UnitY;
			}
			direction.Normalize();
			Vector3 up = Math.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

			Vector3 center = gi.GridMin + new Vector3(gi.GridSize * 0.5f);
			LightView = Matrix4.LookAt(center - direction * gi.GridSize, center, up);
			// A cube of edge s projects to at most s * sqrt(3) / 2 from its center in any direction.
			float extent = gi.GridSize * 0.87f;
			LightProjection = Matrix4.CreateOrthographicOffCenter(-extent, extent, -extent, extent, 0.01f, gi.GridSize * 2f);

			ShadowSettings shadows = Lighting.Shadows;

			target.Bind();
			GLState.SetDepth(true, true);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);
			GL.Clear(ClearBufferMask.DepthBufferBit);

			GL.Enable(EnableCap.PolygonOffsetFill);
			GL.PolygonOffset(shadows.PolygonOffsetFactor, shadows.PolygonOffsetUnits);

			context.DepthOnlyProgram.Use();
			context.DepthOnlyProgram.SetMatrix4("viewMatrix", LightView);
			context.DepthOnlyProgram.SetMatrix4("projectionMatrix", LightProjection);

			foreach (MeshRenderer renderer in casters)
			{
				if (renderer.CastShadows)
				{
					renderer.RenderDepth(context.DepthOnlyProgram);
				}
			}

			Transmittance = TransparentShadows.Draw(context, target, LightView, LightProjection);

			GL.Disable(EnableCap.PolygonOffsetFill);
			GLState.SetColorWrite(true);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		/// <summary>
		///     Binds this map for Engine/Shadows.glsl on the reserved shadow texture unit (the transmittance on
		///     <paramref name="transmittanceUnit" />, a unit the program does not use otherwise).
		/// </summary>
		public void SetUniforms(ShaderProgram program, int transmittanceUnit)
		{
			ShadowSettings shadows = Lighting.Shadows;
			program.SetInt("shadowsEnabled", shadows.Enabled ? 1 : 0);
			program.SetMatrix4("lightSpaceMatrix", LightView * LightProjection);
			program.SetFloat("shadowNormalBias", shadows.NormalBias);
			program.SetFloat("shadowDepthBias", shadows.DepthBias);
			program.SetTexture("ShadowMap", TextureTarget.Texture2D, target.DepthAttachment.Handle, Lighting.ShadowMapTextureUnit);
			TransparentShadows.SetUniforms(program, Transmittance, transmittanceUnit);
		}

		public void Delete()
		{
			target?.Delete();
			target = null;
			Transmittance = null;
		}

		#endregion

		#region PrivateMethods

		private void Create(int size)
		{
			target = new Framebuffer(size, size);
			RenderTexture depth = target.AddDepthAttachment(RenderTextureFormat.Depth32F, true, TextureWrapMode.ClampToBorder);
			depth.SetBorderColor(1f, 1f, 1f, 1f);
			depth.SetShadowCompare(true);
			target.Validate();
		}

		#endregion
	}
}
