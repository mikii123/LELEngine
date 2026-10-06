using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Debug visualization: ray-marches the voxel radiance volume and composites it over the scene.
	///     Disabled by default; toggle <see cref="RenderPass.Enabled" /> and pick <see cref="Mip" />.
	/// </summary>
	public sealed class VoxelDebugPass : RenderPass
	{
		#region PublicFields

		public override string Name => "VoxelDebug";

		/// <summary>Mip level to display. 0 shows raw voxels.</summary>
		public float Mip;

		#endregion

		#region PrivateFields

		private ShaderProgram program;
		private int nearestSampler;

		#endregion

		#region Constructors

		public VoxelDebugPass()
		{
			Enabled = false;
		}

		#endregion

		#region PublicMethods

		public override void Initialize(Renderer renderer)
		{
			program = new ShaderProgram("Engine/VoxelDebug.shader");

			// Point sampling makes individual voxels visible.
			nearestSampler = GL.GenSampler();
			GL.SamplerParameter(nearestSampler, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapNearest);
			GL.SamplerParameter(nearestSampler, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.SamplerParameter(nearestSampler, SamplerParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
			GL.SamplerParameter(nearestSampler, SamplerParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
			GL.SamplerParameter(nearestSampler, SamplerParameterName.TextureWrapR, (int)TextureWrapMode.ClampToBorder);
		}

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			if (gi.VoxelTexture == 0)
			{
				return;
			}

			context.SceneTarget.Bind();
			GLState.SetDepth(false, false);
			GLState.SetCull(false);
			GLState.SetBlend(true, true);

			program.Use();
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("voxelGridMin", gi.GridMin);
			program.SetFloat("voxelGridSize", gi.GridSize);
			program.SetInt("voxelResolution", gi.Resolution);
			program.SetFloat("debugMip", Mip);
			program.SetTexture("VoxelRadiance", TextureTarget.Texture3D, gi.VoxelTexture, Lighting.VoxelTextureUnit);
			GL.BindSampler(Lighting.VoxelTextureUnit, nearestSampler);

			context.Fullscreen.Draw();

			GL.BindSampler(Lighting.VoxelTextureUnit, 0);
			GLState.SetBlend(false);
			GLState.SetDepth(true, true);
			GLState.SetCull(true);
		}

		public override void Dispose()
		{
			program?.Delete();
			if (nearestSampler != 0)
			{
				GL.DeleteSampler(nearestSampler);
				nearestSampler = 0;
			}
		}

		#endregion
	}
}
