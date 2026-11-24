using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Directional shadow maps whose orthographic frustums enclose the whole GI grid instead of following the camera.
	///     Used when lighting cached data (voxels, surface cache) that must not depend on the view. One tile of an atlas
	///     per directional light that both goes into the GI and casts shadows (<see cref="DirectionalLightBuffer" />), side
	///     by side: a scene with one such light has the one map it always had, a light that stays out of the GI or casts
	///     no shadows costs nothing here.
	/// </summary>
	internal sealed class GridShadowMap
	{
		#region PublicFields

		/// <summary>Transmittance of the transparent casters (<see cref="TransparentShadows" />), null when the last render drew none.</summary>
		public RenderTexture Transmittance { get; private set; }

		/// <summary>The size of one light's map (a tile of the atlas).</summary>
		public int Size { get; private set; }

		/// <summary>Lights the last render drew a map for.</summary>
		public int LightCount { get; private set; }

		#endregion

		#region PrivateFields

		private readonly Lighting.ShadowCascadeData cascades = new Lighting.ShadowCascadeData();
		private readonly ShadowUniforms uniforms = new ShadowUniforms();
		private readonly Matrix4[] views = new Matrix4[DirectionalLight.MaxLights];
		private readonly Matrix4[] projections = new Matrix4[DirectionalLight.MaxLights];
		private Framebuffer target;

		// The light's map being drawn: casters outside its view (off the grid, as the light sees it) are left out.
		private readonly ViewCulling culling = new ViewCulling();

		#endregion

		#region Constructors

		public GridShadowMap(int size)
		{
			Size = size;
		}

		#endregion

		#region PublicMethods

		public void EnsureSize(int size)
		{
			if (Size != size)
			{
				Size = size;
				DeleteTarget();
			}
		}

		/// <summary>
		///     Renders all shadow casters among <paramref name="casters" /> (the opaque renderers) and the frame's
		///     transparent casters from every directional light that lights the GI with shadows, covering the GI grid.
		///     Nothing is drawn (and nothing kept) while no light needs a map: shadows off, no light in the GI.
		///     <paramref name="opaqueCasters" /> false (the readers trace the opaque casters' shadows with ray queries):
		///     only the transparent casters' transmittance, over a cleared depth; nothing at all without such casters.
		/// </summary>
		public void Render(RenderContext context, IReadOnlyList<MeshRenderer> casters, GlobalIlluminationSettings gi, bool opaqueCasters = true)
		{
			IReadOnlyList<DirectionalLight> lights = DirectionalLightBuffer.Shining;
			cascades.Clear();
			int count = 0;
			for (int i = 0; i < lights.Count; i++)
			{
				DirectionalLight light = lights[i];
				if (!DirectionalLightBuffer.CastsShadows(i) || !light.AffectsGI || light.IndirectStrength <= 0f)
				{
					continue;
				}

				ComputeLightMatrices(gi, light.Towards, out views[count], out projections[count]);
				cascades.LightFirst[i] = count;
				cascades.LightCount[i] = 1;
				count++;
			}

			LightCount = count;
			Transmittance = null;
			// The transparent casters are asked about once (the question walks every transparent renderer); without the
			// opaque casters a frame without them draws nothing.
			bool transparentCasters = count > 0 && Lighting.Shadows.Enabled && TransparentShadows.HasCasters(context);
			if (count == 0 || !Lighting.Shadows.Enabled || (!opaqueCasters && !transparentCasters))
			{
				LightCount = 0;
				cascades.Clear();
				uniforms.Upload(cascades);
				return;
			}

			if (target == null || target.Width != Size * count || target.Height != Size)
			{
				DeleteTarget();
				target = new Framebuffer(Size * count, Size);
				RenderTexture depth = target.AddDepthAttachment(RenderTextureFormat.Depth32F, true, SamplerAddress.ClampToBorder);
				depth.SetBorderColor(1f, 1f, 1f, 1f);
				depth.SetShadowCompare(true);
				target.Validate();
			}

			for (int i = 0; i < count; i++)
			{
				var rect = new Vector4(i / (float)count, 0f, (i + 1) / (float)count, 1f);
				cascades.Rects[i] = rect;
				cascades.Matrices[i] = Lighting.ShadowTextureMatrix(views[i], projections[i], rect);
				// One map per light covers everything: a receiver never looks for another cascade.
				cascades.Spheres[i] = new Vector4(0f, 0f, 0f, float.MaxValue);
			}

			cascades.TileCount = count;
			uniforms.Upload(cascades);

			// The transmittance attachment has to be there when the pass begins.
			if (transparentCasters)
			{
				TransparentShadows.EnsureTransmittance(target);
			}

			CommandList commands = Gfx.Commands;
			ref RenderState state = ref commands.State;
			state.SetDepth(true, true);
			state.Culling = FaceCulling.Back;
			state.ColorMask = ColorWriteMask.None;
			target.Begin(LoadOperation.Load, default, LoadOperation.Clear, 1f);

			ShaderProgram program = context.DepthOnlyProgram;
			bool frustumCulling = context.Settings == null || context.Settings.FrustumCulling;
			for (int i = 0; i < count && opaqueCasters; i++)
			{
				ContinueTile(i);
				program.Use();
				program.SetMatrix4("viewMatrix", views[i]);
				program.SetMatrix4("projectionMatrix", projections[i]);
				culling.Begin(views[i] * projections[i], frustumCulling);
				foreach (MeshRenderer renderer in casters)
				{
					if (renderer.CastShadows && culling.IsVisible(renderer))
					{
						renderer.RenderShadowCastersWith(program);
					}
				}

				// The casters whose shader draws them itself (ShadowCaster Custom), each with its own program.
				bool own = false;
				foreach (MeshRenderer renderer in casters)
				{
					if (renderer.CastShadows && culling.IsVisible(renderer))
					{
						own |= renderer.RenderOwnShadowCasters(views[i], projections[i]);
					}
				}

				if (own)
				{
					commands.State.Culling = FaceCulling.Back;
				}
			}

			if (transparentCasters)
			{
				for (int i = 0; i < count; i++)
				{
					ContinueTile(i);
					culling.Begin(views[i] * projections[i], frustumCulling);
					Transmittance = TransparentShadows.DrawCasters(context, target, views[i], projections[i], i == 0, culling, new Box2i(i * Size, 0, (i + 1) * Size, Size)) ?? Transmittance;
				}
			}

			commands.State.ColorMask = ColorWriteMask.All;
			commands.EndRenderPass();
		}

		/// <summary>
		///     Gives a program these maps for Engine/Shadows.glsl: their layout to the shadow block's binding (the
		///     camera's is bound again by the next <see cref="Lighting.SetShadowUniforms" />), the depth and the
		///     transmittance to its samplers (no texture while no light has a map).
		/// </summary>
		public void SetUniforms(ShaderProgram program)
		{
			uniforms.Bind();
			bool enabled = LightCount > 0 && target != null && Lighting.Shadows.Enabled;
			program.SetInt("shadowsEnabled", enabled ? 1 : 0);
			program.SetTexture("ShadowMap", enabled ? target.DepthAttachment : null);
			TransparentShadows.SetUniforms(program, enabled ? Transmittance : null);
		}

		public void Delete()
		{
			DeleteTarget();
			uniforms.Delete();
		}

		#endregion

		#region PrivateMethods

		// Goes on drawing into a light's tile: the maps' pass is begun again (keeping what it holds) when something that
		// is not a draw has ended it.
		private void ContinueTile(int tile)
		{
			CommandList commands = Gfx.Commands;
			if (!commands.InRenderPass)
			{
				target.Begin();
			}

			commands.SetViewport(tile * Size, 0, Size, Size);
		}

		private void DeleteTarget()
		{
			target?.Delete();
			target = null;
			Transmittance = null;
		}

		// Orthographic light frustum that encloses the whole GI cube, seen from a light (the unit vector towards it).
		private static void ComputeLightMatrices(GlobalIlluminationSettings gi, Vector3 towards, out Matrix4 lightView, out Matrix4 lightProjection)
		{
			Vector3 direction = -towards;
			Vector3 up = Math.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
			Vector3 center = gi.GridMin + new Vector3(gi.GridSize * 0.5f);
			lightView = Matrix4.LookAt(center - direction * gi.GridSize, center, up);

			// A cube of edge s projects to at most s * sqrt(3) / 2 from its center in any direction.
			float extent = gi.GridSize * 0.87f;
			lightProjection = Matrix4.CreateOrthographicOffCenter(-extent, extent, -extent, extent, 0.01f, gi.GridSize * 2f);
		}

		#endregion
	}
}
