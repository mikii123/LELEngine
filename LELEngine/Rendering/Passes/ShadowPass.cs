using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Renders the scene depth from the directional light into a shadow map.
	///     The orthographic frustum follows the camera and is snapped to shadow-map texels to avoid shimmering.
	///     Results are published through <see cref="Lighting.LightSpaceMatrix" /> and <see cref="Lighting.ShadowMap" />.
	///     GPU-driven: the casters are culled against the light's view on the GPU and drawn by one indirect call.
	/// </summary>
	public sealed class ShadowPass : RenderPass
	{
		#region PublicFields

		public override string Name => "Shadows";
		public Framebuffer ShadowTarget => shadowTarget;

		#endregion

		#region PrivateFields

		private Framebuffer shadowTarget;
		private ShaderProgram instancedDepth;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			CreateTarget(Lighting.Shadows.MapSize);
			instancedDepth = new ShaderProgram("Engine/DepthOnly.shader", new[] { ShaderProgram.InstancedDefine });
		}

		public override void Execute(RenderContext context)
		{
			ShadowSettings settings = Lighting.Shadows;
			DirectionalLight light = DirectionalLight.This;

			if (!settings.Enabled || light == null)
			{
				Lighting.ShadowMap = null;
				return;
			}

			if (shadowTarget.Width != settings.MapSize)
			{
				shadowTarget.Delete();
				CreateTarget(settings.MapSize);
			}

			Matrix4 lightView;
			Matrix4 lightProjection;
			ComputeLightMatrices(context, light, settings, out lightView, out lightProjection);
			Lighting.LightSpaceMatrix = lightView * lightProjection;

			GpuScene gpu = context.Renderer.GpuScene;
			bool gpuDriven = gpu.Active && instancedDepth != null && instancedDepth.IsLinked;
			if (gpuDriven)
			{
				gpu.CullShadows(Lighting.LightSpaceMatrix);
			}

			shadowTarget.Bind();
			GLState.SetDepth(true, true);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.SetColorWrite(false);
			GL.Clear(ClearBufferMask.DepthBufferBit);

			GL.Enable(EnableCap.PolygonOffsetFill);
			GL.PolygonOffset(settings.PolygonOffsetFactor, settings.PolygonOffsetUnits);

			ShaderProgram program = gpuDriven ? instancedDepth : context.DepthOnlyProgram;
			program.Use();
			program.SetMatrix4("viewMatrix", lightView);
			program.SetMatrix4("projectionMatrix", lightProjection);

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
						renderer.RenderDepth(context.DepthOnlyProgram);
					}
				}
			}

			GL.Disable(EnableCap.PolygonOffsetFill);
			GLState.SetColorWrite(true);

			Lighting.ShadowMap = shadowTarget.DepthAttachment;
		}

		public override void Dispose()
		{
			shadowTarget?.Delete();
			shadowTarget = null;
			instancedDepth?.Delete();
			instancedDepth = null;
			Lighting.ShadowMap = null;
		}

		#endregion

		#region PrivateMethods

		private void CreateTarget(int size)
		{
			shadowTarget = new Framebuffer(size, size);
			RenderTexture depth = shadowTarget.AddDepthAttachment(RenderTextureFormat.Depth32F, true, TextureWrapMode.ClampToBorder);
			depth.SetBorderColor(1f, 1f, 1f, 1f);
			depth.SetShadowCompare(true);
			shadowTarget.Validate();
		}

		private static void ComputeLightMatrices(RenderContext context, DirectionalLight light, ShadowSettings settings, out Matrix4 lightView, out Matrix4 lightProjection)
		{
			Vector3 direction = light.transform.forward;
			if (direction.LengthSquared < 1e-6f)
			{
				direction = -Vector3.UnitY;
			}
			direction.Normalize();

			Vector3 up = Math.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

			// View from the light, origin at world origin. The frustum is positioned via the projection.
			lightView = Matrix4.LookAt(-direction, Vector3.Zero, up);

			// Focus the shadow frustum slightly ahead of the camera.
			Vector3 focus = context.CameraPosition + context.Camera.transform.forward * (settings.Distance * 0.5f);
			Vector3 lsFocus = Vector3.TransformPosition(focus, lightView);

			// Snap to texel grid so the map does not shimmer while the camera moves.
			float extent = settings.Distance;
			float texelSize = extent * 2f / settings.MapSize;
			lsFocus.X = (float)Math.Floor(lsFocus.X / texelSize) * texelSize;
			lsFocus.Y = (float)Math.Floor(lsFocus.Y / texelSize) * texelSize;

			float range = settings.DepthRange;
			lightProjection = Matrix4.CreateOrthographicOffCenter(
				lsFocus.X - extent, lsFocus.X + extent,
				lsFocus.Y - extent, lsFocus.Y + extent,
				-lsFocus.Z - range, -lsFocus.Z + range);
		}

		#endregion
	}
}
