using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Voxel cone tracing at reduced screen resolution. Reads depth and normals from the geometry prepass,
	///     traces the cones once per low-res pixel and publishes two textures (diffuse irradiance + occlusion,
	///     specular + linear depth) that material shaders upsample depth-aware via Engine/VoxelConeTracing.glsl.
	///     The cone sets are jittered by a world-anchored hash (<see cref="GlobalIlluminationSettings.ConeJitter" />) and
	///     the result is filtered spatially (Engine/GIFilter.shader, <see cref="GlobalIlluminationSettings.ResolveFilterPasses" />
	///     edge-aware a-trous passes); nothing is kept between frames.
	///     Falls back to per-pixel tracing in the material shaders when disabled or when the prepass did not run.
	/// </summary>
	public sealed class GIResolvePass : RenderPass
	{
		#region PublicFields

		public override string Name => "GIResolve";
		public RenderTexture Diffuse => final;
		public RenderTexture Specular => specular;

		#endregion

		#region PrivateFields

		/// <summary>Normal agreement of the filter's taps: pow(dot(n, n'), power).</summary>
		private const float FilterNormalPower = 16f;

		/// <summary>Distance of a tap's point from the centre's plane at which its weight reaches 0, as a fraction of the depth.</summary>
		private const float FilterPlaneTolerance = 0.03f;

		// The trace writes diffuse, specular and the filter's guide (normal, depth); the filter passes alternate between
		// two diffuse-only targets.
		private Framebuffer traceTarget;
		private RenderTexture diffuse;
		private RenderTexture specular;
		private RenderTexture guide;
		private readonly Framebuffer[] filterTargets = new Framebuffer[2];
		private readonly RenderTexture[] filtered = new RenderTexture[2];
		private RenderTexture final;

		// Variants by the voxel mips the cones read, indexed by VoxelMipMode (compiled in, see Engine/VoxelRadiance.glsl).
		private readonly ShaderProgram[] programs = new ShaderProgram[3];
		private ShaderProgram filter;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			programs[(int)VoxelMipMode.Isotropic] = new ShaderProgram("Engine/GIResolve.shader", new[] { "LEL_VOXEL_ISO" });
			programs[(int)VoxelMipMode.Anisotropic] = new ShaderProgram("Engine/GIResolve.shader", new[] { "LEL_VOXEL_ANISO" });
			programs[(int)VoxelMipMode.SphericalHarmonics] = new ShaderProgram("Engine/GIResolve.shader", new[] { "LEL_VOXEL_SH" });
			filter = new ShaderProgram("Engine/GIFilter.shader");
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			bool active = gi.Enabled && gi.Mode == GIMode.VoxelConeTracing && gi.ScreenSpaceResolve && gi.VoxelTexture != 0 && context.DepthPrepassDone && context.NormalRoughness != null;
			if (!active)
			{
				gi.ResolveActive = false;
				return;
			}

			int width = Math.Max(1, (int)Math.Ceiling(context.Width * gi.ResolveScale));
			int height = Math.Max(1, (int)Math.Ceiling(context.Height * gi.ResolveScale));
			EnsureTargets(width, height);
			Matrix4 invViewProjection = Matrix4.Invert(context.ViewProjection);
			Vector3 cameraForward = context.Camera.transform.forward;

			traceTarget.Bind();
			GLState.SetDepth(false, false);
			GLState.SetCull(false);

			ShaderProgram program = programs[(int)Lighting.EffectiveVoxelMips(gi.VoxelVolume, Lighting.VoxelDirectionalTextureUnit)];
			program.Use();
			// The resolve itself always traces; make sure it does not try to read its own output.
			gi.ResolveActive = false;
			Lighting.SetGIUniforms(program, true);
			program.SetTexture("SceneDepth", TextureTarget.Texture2D, context.SceneDepth.Handle, 0);
			program.SetTexture("NormalRoughness", TextureTarget.Texture2D, context.NormalRoughness.Handle, 1);
			program.SetMatrix4("invViewProjection", invViewProjection);
			program.SetVector2("resolveTargetSize", new Vector2(width, height));
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("cameraForward", cameraForward);
			program.SetInt("giConeJitter", gi.ConeJitter ? 1 : 0);
			program.SetFloat("giConeJitterCell", gi.ConeJitterCellSize > 0f ? gi.ConeJitterCellSize : gi.VoxelSize * 0.5f);

			context.Fullscreen.Draw();

			final = diffuse;
			if (gi.ResolveFilterPasses > 0)
			{
				context.Renderer.Profiler.Split("GIResolve.filter");
				filter.Use();
				filter.SetTexture("Guide", TextureTarget.Texture2D, guide.Handle, 1);
				filter.SetVector2("resolveTargetSize", new Vector2(width, height));
				filter.SetVector2("screenSize", new Vector2(context.Width, context.Height));
				filter.SetVector3("cameraForward", cameraForward);
				// Rays through pixel centres (0, 0), (1, 0), (0, 1): the filter interpolates them linearly.
				Vector3 ray00 = PixelRay(invViewProjection, context.CameraPosition, 0.5f, 0.5f, context.Width, context.Height);
				filter.SetVector3("rayOrigin", ray00);
				filter.SetVector3("rayStepX", PixelRay(invViewProjection, context.CameraPosition, 1.5f, 0.5f, context.Width, context.Height) - ray00);
				filter.SetVector3("rayStepY", PixelRay(invViewProjection, context.CameraPosition, 0.5f, 1.5f, context.Width, context.Height) - ray00);
				filter.SetFloat("filterNormalPower", FilterNormalPower);
				filter.SetFloat("filterPlaneTolerance", FilterPlaneTolerance);
				for (int pass = 0; pass < gi.ResolveFilterPasses; pass++)
				{
					int destination = pass & 1;
					filterTargets[destination].Bind();
					filter.SetTexture("Diffuse", TextureTarget.Texture2D, final.Handle, 0);
					filter.SetInt("filterStep", 1 << pass);
					context.Fullscreen.Draw();
					final = filtered[destination];
				}
			}

			GLState.SetDepth(true, true);
			GLState.SetCull(true);

			gi.ResolvedDiffuse = final.Handle;
			gi.ResolvedSpecular = specular.Handle;
			gi.ResolvedNormals = context.NormalRoughness.Handle;
			gi.ResolveWidth = width;
			gi.ResolveHeight = height;
			gi.ScreenWidth = context.Width;
			gi.ScreenHeight = context.Height;
			gi.ResolveActive = true;
		}

		public override void Dispose()
		{
			traceTarget?.Delete();
			traceTarget = null;
			for (int i = 0; i < filterTargets.Length; i++)
			{
				filterTargets[i]?.Delete();
				filterTargets[i] = null;
			}
			for (int i = 0; i < programs.Length; i++)
			{
				programs[i]?.Delete();
				programs[i] = null;
			}
			filter?.Delete();
			filter = null;
			Lighting.GI.ResolveActive = false;
		}

		#endregion

		#region PrivateMethods

		// From the camera to the far plane through screen point (x, y) in pixels from the bottom left.
		private static Vector3 PixelRay(Matrix4 invViewProjection, Vector3 cameraPosition, float x, float y, int width, int height)
		{
			Vector4 far = new Vector4(x / width * 2f - 1f, y / height * 2f - 1f, 1f, 1f) * invViewProjection;
			return far.Xyz / far.W - cameraPosition;
		}

		private void EnsureTargets(int width, int height)
		{
			if (traceTarget != null && traceTarget.Width == width && traceTarget.Height == height)
			{
				return;
			}

			traceTarget?.Delete();
			traceTarget = new Framebuffer(width, height);
			diffuse = traceTarget.AddColorAttachment(RenderTextureFormat.RGBA16F);
			specular = traceTarget.AddColorAttachment(RenderTextureFormat.RGBA16F);
			guide = traceTarget.AddColorAttachment(RenderTextureFormat.RGBA16F, false);
			traceTarget.Validate();

			for (int i = 0; i < filterTargets.Length; i++)
			{
				filterTargets[i]?.Delete();
				filterTargets[i] = new Framebuffer(width, height);
				filtered[i] = filterTargets[i].AddColorAttachment(RenderTextureFormat.RGBA16F);
				filterTargets[i].Validate();
			}
		}

		#endregion
	}
}
