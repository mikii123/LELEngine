using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Voxel cone tracing, stage 1: builds the radiance volume.
	///     Every frame the scene is rasterized into a 3D texture (dominant-axis projection in a geometry shader,
	///     imageStore in the fragment shader) storing directly lit diffuse radiance plus emission, then mipmapped.
	///     Material shaders sample it through Engine/VoxelConeTracing.glsl (uniforms via Lighting.SetGIUniforms).
	///     Needs the shadow map, so it runs after <see cref="ShadowPass" />.
	/// </summary>
	public sealed class VoxelGIPass : RenderPass
	{
		#region PublicFields

		public override string Name => "VoxelGI";
		public int VoxelTexture => voxelTexture;

		#endregion

		#region PrivateFields

		private int voxelTexture;
		private int currentResolution;
		private int framebuffer;
		private ShaderProgram voxelize;
		private ComputeShader clear;

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			voxelize = new ShaderProgram("Engine/Voxelize.shader");
			clear = new ComputeShader("Engine/VoxelClear.shader");

			// Attachment-less framebuffer: rasterization happens, but all output goes through imageStore.
			framebuffer = GL.GenFramebuffer();

			CreateVolume(Lighting.GI.Resolution);
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			if (!gi.Enabled)
			{
				gi.VoxelTexture = 0;
				return;
			}

			if (gi.Resolution != currentResolution)
			{
				CreateVolume(gi.Resolution);
			}

			UpdateGridPlacement(context, gi);

			ClearVolume();
			Voxelize(context, gi);

			GL.BindTexture(TextureTarget.Texture3D, voxelTexture);
			GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
			GL.BindTexture(TextureTarget.Texture3D, 0);

			gi.VoxelTexture = voxelTexture;
		}

		public override void Dispose()
		{
			if (voxelTexture != 0)
			{
				GL.DeleteTexture(voxelTexture);
				voxelTexture = 0;
			}
			if (framebuffer != 0)
			{
				GL.DeleteFramebuffer(framebuffer);
				framebuffer = 0;
			}
			voxelize?.Delete();
			clear?.Delete();
			Lighting.GI.VoxelTexture = 0;
		}

		#endregion

		#region PrivateMethods

		private void CreateVolume(int resolution)
		{
			resolution = Math.Max(8, resolution);
			// Must be a multiple of the clear shader's work group (8).
			resolution = resolution / 8 * 8;

			if (voxelTexture != 0)
			{
				GL.DeleteTexture(voxelTexture);
			}

			voxelTexture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, voxelTexture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.Rgba16f, resolution, resolution, resolution, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureBorderColor, new[] { 0f, 0f, 0f, 0f });
			// Allocate the mip chain once.
			GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
			GL.BindTexture(TextureTarget.Texture3D, 0);

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultWidth, resolution);
			GL.FramebufferParameter(FramebufferTarget.Framebuffer, FramebufferDefaultParameter.FramebufferDefaultHeight, resolution);
			FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				Console.WriteLine("[VoxelGI] Voxelization framebuffer incomplete: " + status);
			}

			currentResolution = resolution;
			Lighting.GI.Resolution = resolution;
			Console.WriteLine("[VoxelGI] Volume " + resolution + "^3 RGBA16F");
		}

		private static void UpdateGridPlacement(RenderContext context, GlobalIlluminationSettings gi)
		{
			Vector3 center = gi.Center;
			if (gi.FollowCamera)
			{
				// Snap to voxel size so the volume does not swim while the camera moves.
				float voxel = gi.VoxelSize;
				Vector3 c = context.CameraPosition;
				center = new Vector3(
					(float)Math.Floor(c.X / voxel) * voxel,
					(float)Math.Floor(c.Y / voxel) * voxel,
					(float)Math.Floor(c.Z / voxel) * voxel);
			}

			gi.GridMin = center - new Vector3(gi.GridSize * 0.5f);
		}

		private void ClearVolume()
		{
			clear.Use();
			GL.BindImageTexture(0, voxelTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);
			int groups = currentResolution / 8;
			clear.Dispatch(groups, groups, groups);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);
		}

		private void Voxelize(RenderContext context, GlobalIlluminationSettings gi)
		{
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.Viewport(0, 0, currentResolution, currentResolution);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.CullFace);
			GL.DepthMask(false);

			voxelize.Use();
			voxelize.SetVector3("voxelGridMin", gi.GridMin);
			voxelize.SetFloat("voxelGridSize", gi.GridSize);
			voxelize.SetInt("voxelResolution", currentResolution);
			Lighting.SetUniforms(voxelize, true, false);
			GL.BindImageTexture(0, voxelTexture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.Rgba16f);

			foreach (MeshRenderer renderer in context.Renderers)
			{
				if (!renderer.ContributesToGI || renderer.Material == null)
				{
					continue;
				}

				SetMaterialUniforms(renderer.Material);
				renderer.RenderWith(voxelize);
			}

			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);

			GL.DepthMask(true);
			GL.Enable(EnableCap.DepthTest);
			GL.Enable(EnableCap.CullFace);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		}

		// Materials describe themselves through conventional uniform names.
		private void SetMaterialUniforms(Material material)
		{
			Vector4 albedo;
			if (!material.TryGetVector4("Color", out albedo))
			{
				albedo = Vector4.One;
			}

			Vector4 emissive;
			if (!material.TryGetVector4("Emissive", out emissive))
			{
				emissive = Vector4.Zero;
			}

			int albedoMap = material.GetTextureHandle("DiffuseMap");
			if (albedoMap == 0)
			{
				albedoMap = material.GetTextureHandle("AlbedoMap");
			}

			voxelize.SetVector4("voxAlbedo", albedo);
			voxelize.SetVector4("voxEmissive", emissive);
			voxelize.SetInt("voxUseAlbedoMap", albedoMap != 0 ? 1 : 0);
			if (albedoMap != 0)
			{
				voxelize.SetTexture("voxAlbedoMap", TextureTarget.Texture2D, albedoMap, 0);
			}
		}

		#endregion
	}
}
