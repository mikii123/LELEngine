using LELEngine.Volumetrics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     The volumetric objects in the GI: makes this frame's media volume (<see cref="VolumetricMedia" />: what the
	///     clouds scatter and hide, coarse and world aligned) and publishes it as <see cref="Lighting.Media" /> for the GI
	///     passes that follow (voxel cone tracing's bounces and resolve, Lumen's radiosity and screen probes) and for the
	///     materials that trace the GI themselves. After the clouds were updated (<see cref="VolumetricPass" />) and the
	///     shadow maps drawn (the clouds take the scene's shadows), once per frame.
	/// </summary>
	public sealed class VolumetricMediaPass : RenderPass
	{
		#region PublicFields

		public override string Name => "VolumeMedia";

		public override RenderPassScope Scope => RenderPassScope.Frame;

		#endregion

		#region PrivateFields

		private readonly VolumetricMedia media = new VolumetricMedia();

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			media.Update(context);
		}

		public override void ReleaseUnusedResources()
		{
			if (!Lighting.GI.Enabled || Lighting.GI.VolumesInGI == VolumeGIMode.Off)
			{
				media.Release();
			}
		}

		public override void Dispose()
		{
			media.Dispose();
		}

		#endregion
	}
}
