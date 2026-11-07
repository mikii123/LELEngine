namespace LELEngine.Volumetrics
{
	/// <summary>
	///     A high quality volumetric object, also for clouds the camera flies into (fog banks, clouds up close): marched
	///     at full resolution by default, with steps that grow with the distance from the camera (fine where the cloud
	///     fills the view from inside, coarse far away), a jitter that changes every frame and a temporal filter that
	///     blends the frames (smooth with few steps; the history follows the camera and the object), and the scene's sun
	///     shadows falling into the cloud. See <see cref="VolumetricObjectBase" />; <see cref="VolumetricObject" /> is the
	///     cheaper preset for clouds seen from outside.
	/// </summary>
	public sealed class VolumetricObjectHQ : VolumetricObjectBase
	{
		#region PublicFields

		[Header("High quality")]
		[Tooltip("Blend the frames (the jitter changes every frame): smooth with few steps; off: noisier, no history")]
		public bool TemporalFilter = true;

		[Tooltip("How much of the history a frame keeps (higher: smoother, slower to follow changes)")]
		[Range(0f, 0.98f)]
		public float HistoryWeight = 0.9f;

		[Tooltip("Steps grow with the distance from the camera: step = distance x this (between Min Step and Step Size)")]
		[Range(0.001f, 0.2f)]
		public float NearStepScale = 0.02f;

		[Tooltip("The smallest step near the camera, in voxels")]
		[Range(0.02f, 1f)]
		public float MinStep = 0.25f;

		[Tooltip("The scene's sun shadows fall into the cloud (ships shading a fog bank)")]
		public bool ReceiveShadows = true;

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
