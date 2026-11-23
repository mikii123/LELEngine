using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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
		private GpuSampler nearestSampler;

		#endregion

		#region Constructors

		public VoxelDebugPass()
		{
			Enabled = false;
		}

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			// The volume this renderer's voxel pass lit (what it published for the materials this frame).
			VoxelGIPass volume = context.Renderer.GetPass<VoxelGIPass>();
			if (gi.VoxelTexture == 0 || volume == null || volume.RadianceTexture == null)
			{
				return;
			}

			// Made when the view is first shown.
			if (program == null)
			{
				program = new ShaderProgram("Engine/VoxelDebug.shader");

				// Point sampling makes individual voxels visible.
				nearestSampler = Samplers.Get(SamplerFilter.Nearest, SamplerMipFilter.Nearest, SamplerAddress.ClampToBorder);
			}

			context.SceneTarget.Begin();
			CommandList commands = Gfx.Commands;
			commands.State.SetDepth(false, false);
			commands.State.Culling = FaceCulling.None;
			commands.State.SetBlend(BlendFactor.One, BlendFactor.OneMinusSrcAlpha);

			program.Use();
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("voxelGridMin", gi.GridMin);
			program.SetFloat("voxelGridSize", gi.GridSize);
			program.SetInt("voxelResolution", gi.Resolution);
			program.SetFloat("debugMip", Mip);
			// The voxels and the directional data their mips have (Engine/VoxelRadiance.glsl), all point sampled; the
			// directional data only where the materials read it too (the context has their texture units).
			VoxelMipMode mode = Lighting.EffectiveVoxelMips(gi.VoxelVolume, Lighting.VoxelDirectionalTextureUnit);
			program.SetTexture("VoxelRadiance", volume.RadianceTexture, nearestSampler);
			program.SetInt("giVoxelMips", (int)mode);
			if (mode == VoxelMipMode.Anisotropic)
			{
				program.SetInt("giVoxelAnisoLevels", volume.AnisoLevels);
			}

			program.SetTexture("VoxelRadianceAniso", mode == VoxelMipMode.Anisotropic ? volume.AnisoTexture : null, nearestSampler);
			bool gradients = mode == VoxelMipMode.SphericalHarmonics;
			program.SetTexture("VoxelRadianceSHX", gradients ? volume.SHTexture(0) : null, nearestSampler);
			program.SetTexture("VoxelRadianceSHY", gradients ? volume.SHTexture(1) : null, nearestSampler);
			program.SetTexture("VoxelRadianceSHZ", gradients ? volume.SHTexture(2) : null, nearestSampler);

			context.Fullscreen.Draw();

			commands.State.Blend = false;
			commands.State.SetDepth(true, true);
			commands.State.Culling = FaceCulling.Back;
		}

		public override void Dispose()
		{
			program?.Delete();
			nearestSampler = null;
		}

		#endregion
	}
}
