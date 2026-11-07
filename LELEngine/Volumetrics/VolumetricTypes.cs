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

	/// <summary>
	///     One of the (up to 8) lights a cloud's light transport is worked out for: a light of its own
	///     (<see cref="VolumetricObjectBase.Lights" />: a core and a glow) or a scene <see cref="global::Light" /> that
	///     reaches the shape (physical falloff, its range and cone). The transport holds where the light goes through the
	///     cloud at unit brightness (times <see cref="Normalization" />); the light's colour and intensity are applied when the
	///     cloud is drawn.
	/// </summary>
	internal struct VolumeLightSlot
	{
		/// <summary>Local position; w: the core's radius (local units, own light) or the range (world units, scene light).</summary>
		public Vector4 PositionSize;

		/// <summary>x: 0 own light, 1 scene light; y: the source's radius (world); z: cos of the outer half angle (-2: no cone); w: 1 / (cos inner - cos outer).</summary>
		public Vector4 Params;

		/// <summary>xyz: the spot's direction (local); w: the factor the transport holds the light times (the colour is divided by it).</summary>
		public Vector4 Spot;

		/// <summary>The light of its own (null for a scene light).</summary>
		public VolumeLight Own;

		/// <summary>The scene light (null for a light of its own).</summary>
		public global::Light Scene;

		/// <summary>
		///     The light's colour x intensity right now (a scene light's times its volumetric strength), divided by the
		///     transport's factor (what the march multiplies the transport by).
		/// </summary>
		public Vector3 Color
		{
			get
			{
				if (Scene != null)
				{
					return Scene.IsDestroyed || !Scene.isActiveAndEnabled ? Vector3.Zero : Scene.Radiance * System.MathF.Max(Scene.VolumetricStrength, 0f) / System.MathF.Max(Spot.W, 1e-12f);
				}

				return Own != null ? new Vector3(Own.Color.R, Own.Color.G, Own.Color.B) * Own.Intensity : Vector3.Zero;
			}
		}
	}
}
