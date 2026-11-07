using OpenTK.Mathematics;

namespace LELEngine.Volumetrics
{
	/// <summary>The shape a volumetric object's cloud fills, fitted into its <see cref="VolumetricObjectBase.Size" />.</summary>
	public enum VolumeShape
	{
		/// <summary>The box of the size.</summary>
		Box,

		/// <summary>A cylinder around the local Y axis: diameters along X and Z (an ellipse when they differ), height along Y.</summary>
		Cylinder,

		/// <summary>A sphere, an ellipsoid when the size's axes differ.</summary>
		Sphere
	}

	/// <summary>The noise a volumetric object's cloud is cut out of (then thresholded by the coverage).</summary>
	public enum VolumeNoise
	{
		/// <summary>Fractal gradient noise: soft, rolling banks.</summary>
		Perlin,

		/// <summary>The absolute value of gradient noise: puffy lumps with creases between them.</summary>
		Billow,

		/// <summary>The ridges of gradient noise: filaments and veins (nebulae).</summary>
		Ridged,

		/// <summary>Fractal cellular noise: bubbles and cells.</summary>
		Worley,

		/// <summary>Gradient noise carved by cellular billows: cumulus clouds, storm clouds.</summary>
		PerlinWorley
	}

	/// <summary>The resolution a volumetric object is marched at, against the screen's.</summary>
	public enum VolumetricResolution
	{
		Full,
		Half,
		Quarter
	}

	/// <summary>
	///     A light inside a volumetric object's cloud (a lightning flash, a lamp in fog, a fire in smoke): it lights the
	///     cloud around it through the cloud. Where it is and how big its core is are worked out into a light volume when
	///     they change; its colour and intensity are applied every frame, so it can flicker for free.
	/// </summary>
	[System.Serializable]
	public sealed class VolumeLight
	{
		[Tooltip("Where the light is, in the object's local units")]
		public Vector3 Position;

		[Tooltip("Radius of the light's bright core (local units); its glow reaches four times as far")]
		public float Radius = 10f;

		public Color4 Color = Color4.White;

		[Tooltip("Brightness right now (0: off); set it every frame to make the light flicker")]
		public float Intensity;
	}
}
