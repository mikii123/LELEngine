namespace LELEngine.Volumetrics
{
	/// <summary>
	///     A high quality volumetric object, also for clouds the camera flies into (fog banks, clouds up close): marched
	///     at full resolution by default, with steps that grow with the distance from the camera (fine where the cloud
	///     fills the view from inside, coarse far away). See <see cref="VolumetricObjectBase" /> (the temporal filter,
	///     shadows, GI and lights are common to both); <see cref="VolumetricObject" /> is the cheaper preset for clouds
	///     seen from outside.
	/// </summary>
	public sealed class VolumetricObjectHQ : VolumetricObjectBase
	{
		#region PublicFields

		[Header("High quality")]
		[Tooltip("Steps grow with the distance from the camera: step = distance x this (between Min Step and Step Size)")]
		[Range(0.001f, 0.2f)]
		public float NearStepScale = 0.02f;

		[Tooltip("The smallest step near the camera, in voxels")]
		[Range(0.02f, 1f)]
		public float MinStep = 0.25f;

		#endregion

		#region Constructors

		public VolumetricObjectHQ()
		{
			Resolution = VolumetricResolution.Full;
			MaxSteps = 160;
			StepSize = 0.75f;
		}

		#endregion

		internal override bool HighQuality => true;
	}
}
