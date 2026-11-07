namespace LELEngine.Volumetrics
{
	/// <summary>
	///     A volumetric object for clouds seen from outside (a storm in the background, a nebula, a smoke column in the
	///     distance): marched at half resolution by default and upsampled with the opaque scene's depth (silhouettes in
	///     front stay sharp), even steps through the shape (at most <see cref="VolumetricObjectBase.MaxSteps" />), one
	///     texture read per step, empty space crossed in blocks, a fixed jitter pattern (no flicker, no history). It still
	///     draws with the camera inside, coarser than <see cref="VolumetricObjectHQ" />. See <see cref="VolumetricObjectBase" />.
	/// </summary>
	public sealed class VolumetricObject : VolumetricObjectBase
	{
		internal override bool HighQuality => false;
	}
}
