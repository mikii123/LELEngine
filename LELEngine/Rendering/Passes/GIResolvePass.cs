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
	///     Falls back to per-pixel tracing in the material shaders when disabled or when the prepass did not run.
	/// </summary>
	public sealed class GIResolvePass : RenderPass
	{
		#region PublicFields

		public override string Name => "GIResolve";
		public RenderTexture Diffuse => diffuse;
		public RenderTexture Specular => specular;

		#endregion

		#region PrivateFields

		private Framebuffer target;
		private RenderTexture diffuse;
		private RenderTexture specular;
		private ShaderProgram program;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/GIResolve.shader");
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
			EnsureTarget(width, height);

			target.Bind();
			GLState.SetDepth(false, false);
			GLState.SetCull(false);

			program.Use();
			// The resolve itself always traces; make sure it does not try to read its own output.
			gi.ResolveActive = false;
			Lighting.SetGIUniforms(program, true);
			program.SetTexture("SceneDepth", TextureTarget.Texture2D, context.SceneDepth.Handle, 0);
			program.SetTexture("NormalRoughness", TextureTarget.Texture2D, context.NormalRoughness.Handle, 1);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetVector2("resolveTargetSize", new Vector2(width, height));
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("cameraForward", context.Camera.transform.forward);

			context.Fullscreen.Draw();

			GLState.SetDepth(true, true);
			GLState.SetCull(true);

			gi.ResolvedDiffuse = diffuse.Handle;
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
			target?.Delete();
			target = null;
			program?.Delete();
			program = null;
			Lighting.GI.ResolveActive = false;
		}

		#endregion

		#region PrivateMethods

		private void EnsureTarget(int width, int height)
		{
			if (target != null && target.Width == width && target.Height == height)
			{
				return;
			}

			target?.Delete();
			target = new Framebuffer(width, height);
			diffuse = target.AddColorAttachment(RenderTextureFormat.RGBA16F);
			specular = target.AddColorAttachment(RenderTextureFormat.RGBA16F);
			target.Validate();
		}

		#endregion
	}
}
