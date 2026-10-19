using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Renders the scene depth from the directional light into a cascaded shadow map: the view up to the shadow
	///     distance is split into slices (<see cref="ShadowSettings.Cascades" />), each rendered into its own tile of
	///     one atlas, so the slices near the camera get small, sharp texels. A cascade's orthographic frustum encloses
	///     the bounding sphere of its slice (its size does not change as the camera turns) and is snapped to its texels
	///     (the shadows do not shimmer as the camera moves). Results are published through
	///     <see cref="Lighting.ShadowCascades" /> and <see cref="Lighting.ShadowMap" />.
	///     GPU-driven: per cascade the opaque casters are culled against its view on the GPU and drawn by one indirect
	///     call; the transparent casters follow into the map's transmittance attachment (<see cref="TransparentShadows" />,
	///     published as <see cref="Lighting.ShadowTransmittance" />).
	/// </summary>
	public sealed class ShadowPass : RenderPass
	{
		#region PublicFields

		public override string Name => "Shadows";

		public override RenderPassScope Scope => RenderPassScope.Frame;
		public Framebuffer ShadowTarget => shadowTarget;

		#endregion

		#region PrivateFields

		private readonly Matrix4[] projections = new Matrix4[Lighting.ShadowCascadeData.MaxCount];
		private Framebuffer shadowTarget;
		private ShaderProgram instancedDepth;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			instancedDepth = new ShaderProgram("Engine/DepthOnly.shader", new[] { ShaderProgram.InstancedDefine });
		}

		public override void Execute(RenderContext context)
		{
			ShadowSettings settings = Lighting.Shadows;
			Lighting.ShadowCascadeData cascades = Lighting.ShadowCascades;
			DirectionalLight light = DirectionalLight.This;

			if (!settings.Enabled || light == null || context.Camera == null)
			{
				cascades.Count = 0;
				Lighting.ShadowMap = null;
				Lighting.ShadowTransmittance = null;
				return;
			}

			// Tiles: one cascade fills the map, two sit side by side, four in 2 x 2.
			int count = settings.Cascades == ShadowCascadeCount.Four ? 4 : settings.Cascades == ShadowCascadeCount.Two ? 2 : 1;
			int columns = count == 1 ? 1 : 2;
			int rows = count == 4 ? 2 : 1;
			int tileSize = Math.Max(16, count == 1 ? settings.MapSize : settings.MapSize / 2);
			EnsureTarget(tileSize * columns, tileSize * rows);

			Matrix4 lightView = LightView(light);
			ComputeCascades(context, settings, lightView, count, columns, rows, tileSize, cascades);

			GpuScene gpu = context.Renderer.GpuScene;
			bool gpuDriven = gpu.Active && instancedDepth != null && instancedDepth.IsLinked;
			ShaderProgram program = gpuDriven ? instancedDepth : context.DepthOnlyProgram;

			shadowTarget.Bind();
			GLState.SetDepth(true, true);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);
			GL.Clear(ClearBufferMask.DepthBufferBit);

			for (int i = 0; i < count; i++)
			{
				SetTileViewport(i, columns, tileSize);
				if (gpuDriven)
				{
					// The culling dispatch binds its own program: the depth program is bound after it. The casters are
					// the lighting camera's layers (the tables hold every camera's renderers).
					gpu.CullShadows(lightView * projections[i], context.CullingMask);
				}

				program.Use();
				program.SetMatrix4("viewMatrix", lightView);
				program.SetMatrix4("projectionMatrix", projections[i]);

				if (gpuDriven)
				{
					gpu.DrawShadowCasters();
				}
				else
				{
					foreach (MeshRenderer renderer in context.Renderers)
					{
						if (renderer.CastShadows)
						{
							renderer.RenderWith(context.DepthOnlyProgram);
						}
					}
				}
			}

			// Transparent casters: what they let through, in the map's transmittance attachment (cleared by the first cascade).
			RenderTexture transmittance = null;
			for (int i = 0; i < count; i++)
			{
				SetTileViewport(i, columns, tileSize);
				transmittance = TransparentShadows.Draw(context, shadowTarget, lightView, projections[i], i == 0) ?? transmittance;
			}

			// Particle outputs that cast shadows (alpha tested, depth only).
			if (VFX.VfxRuntime.Instances.Count > 0)
			{
				GLState.SetColorWrite(false);
				for (int i = 0; i < count; i++)
				{
					SetTileViewport(i, columns, tileSize);
					VFX.VfxRuntime.DrawShadowCasters(context, lightView, projections[i]);
				}
			}

			GLState.SetColorWrite(true);

			Lighting.ShadowMap = shadowTarget.DepthAttachment;
			Lighting.ShadowTransmittance = transmittance;
		}

		public override void Dispose()
		{
			shadowTarget?.Delete();
			shadowTarget = null;
			instancedDepth?.Delete();
			instancedDepth = null;
			Lighting.ShadowCascades.Count = 0;
			Lighting.ShadowMap = null;
			Lighting.ShadowTransmittance = null;
		}

		#endregion

		#region PrivateMethods

		private void EnsureTarget(int width, int height)
		{
			if (shadowTarget != null && shadowTarget.Width == width && shadowTarget.Height == height)
			{
				return;
			}

			shadowTarget?.Delete();
			shadowTarget = new Framebuffer(width, height);
			RenderTexture depth = shadowTarget.AddDepthAttachment(RenderTextureFormat.Depth32F, true, TextureWrapMode.ClampToBorder);
			depth.SetBorderColor(1f, 1f, 1f, 1f);
			depth.SetShadowCompare(true);
			shadowTarget.Validate();
		}

		private static void SetTileViewport(int tile, int columns, int tileSize)
		{
			GL.Viewport(tile % columns * tileSize, tile / columns * tileSize, tileSize, tileSize);
		}

		/// <summary>View from the light, origin at the world origin: the cascades are positioned by their projections.</summary>
		private static Matrix4 LightView(DirectionalLight light)
		{
			Vector3 direction = light.transform.forward;
			if (direction.LengthSquared < 1e-6f)
			{
				direction = -Vector3.UnitY;
			}
			direction.Normalize();

			Vector3 up = Math.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
			return Matrix4.LookAt(-direction, Vector3.Zero, up);
		}

		/// <summary>
		///     Splits the view between the near plane and the shadow distance, fits every cascade's projection around
		///     its slice and publishes the cascades.
		/// </summary>
		private void ComputeCascades(RenderContext context, ShadowSettings settings, Matrix4 lightView, int count, int columns, int rows, int tileSize, Lighting.ShadowCascadeData cascades)
		{
			Camera camera = context.Camera;

			// Half extents of the view per unit of depth, from the projection itself (whatever its field of view convention).
			Matrix4 cameraProjection = camera.ProjectionMatrix;
			float tanX = 1f / cameraProjection.M11;
			float tanY = 1f / cameraProjection.M22;
			float halfDiagonal2 = tanX * tanX + tanY * tanY;

			Vector3 forward = camera.transform.forward;
			forward = forward.LengthSquared > 1e-6f ? forward.Normalized() : -Vector3.UnitZ;

			float near = Math.Max(camera.NearClip, 0.01f);
			float distance = Math.Max(settings.Distance, near + 0.1f);

			Span<float> bounds = stackalloc float[Lighting.ShadowCascadeData.MaxCount + 1];
			bounds[0] = near;
			if (count == 2)
			{
				bounds[1] = distance * settings.TwoCascadeSplit;
			}
			else if (count == 4)
			{
				bounds[1] = distance * settings.FourCascadeSplits.X;
				bounds[2] = distance * settings.FourCascadeSplits.Y;
				bounds[3] = distance * settings.FourCascadeSplits.Z;
			}

			for (int i = 1; i < count; i++)
			{
				// Increasing and inside the distance, whatever the settings say.
				bounds[i] = Math.Max(Math.Min(bounds[i], distance - 0.01f * (count - i)), bounds[i - 1] + 0.01f);
			}

			bounds[count] = distance;

			float depthRange = settings.DepthRange;
			for (int i = 0; i < count; i++)
			{
				// Smallest sphere around the slice: its center on the view axis, as far from the near corners as from the
				// far ones, or on the far plane when the near corners are inside the far plane's circle anyway.
				float n = bounds[i];
				float f = bounds[i + 1];
				float near2 = n * n * halfDiagonal2;
				float far2 = f * f * halfDiagonal2;
				float z = 0.5f * (n + f) + (far2 - near2) / (2f * (f - n));
				float radius;
				if (z >= f)
				{
					z = f;
					radius = MathF.Sqrt(far2);
				}
				else
				{
					radius = MathF.Sqrt((f - z) * (f - z) + far2);
				}

				Vector3 center = context.CameraPosition + forward * z;

				// The radius only depends on the view's shape, so the texel size is constant: snapping the center to the
				// texel grid keeps a static scene on the same texels while the camera moves.
				float texel = 2f * radius / tileSize;
				Vector3 lightCenter = Vector3.TransformPosition(center, lightView);
				lightCenter.X = MathF.Floor(lightCenter.X / texel) * texel;
				lightCenter.Y = MathF.Floor(lightCenter.Y / texel) * texel;

				float depth = Math.Max(depthRange, radius);
				projections[i] = Matrix4.CreateOrthographicOffCenter(
					lightCenter.X - radius, lightCenter.X + radius,
					lightCenter.Y - radius, lightCenter.Y + radius,
					-lightCenter.Z - depth, -lightCenter.Z + depth);

				cascades.Matrices[i] = Lighting.ShadowTextureMatrix(lightView, projections[i], i, columns, rows);

				// A receiver uses the first cascade it is well inside of: room for the filter, the snap and the normal offset.
				float inner = Math.Max(radius - texel * (5f + 2.5f * Math.Max(settings.NormalBiasTexels, 0f)), radius * 0.5f);
				cascades.Spheres[i] = new Vector4(center, inner * inner);
			}

			cascades.Count = count;
			cascades.FadeCenter = context.CameraPosition;
			cascades.FadeDistance = distance;
		}

		#endregion
	}
}
