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
	///     One of the lights a cloud's light transport is worked out for: a light of its own
	///     (<see cref="VolumetricObjectBase.Lights" />: a core and a glow), a scene <see cref="global::DirectionalLight" />
	///     other than the cloud's sun (no falloff: the cloud on the way towards it), or a scene <see cref="global::Light" />
	///     that reaches the shape (physical falloff, its range and cone). Up to <see cref="VolumetricObjectBase.MaxLights" />
	///     of them are live: each has a channel of the transport that holds where its light goes through the cloud at unit
	///     brightness (times the slot's factor), and its colour and intensity are applied when the cloud is drawn. The
	///     scene lights beyond those are baked: all of them summed into one texture with their colour in it
	///     (<see cref="BakedColor" />), worked out again around a light when it changes.
	/// </summary>
	internal struct VolumeLightSlot
	{
		/// <summary>A baked light's colour as its part of the baked texture was worked out with (<see cref="Color" /> then).</summary>
		public Vector3 BakedColor;

		/// <summary>Params.x of a light of the cloud's own.</summary>
		public const float KindOwn = 0f;

		/// <summary>Params.x of a scene light (point, spot).</summary>
		public const float KindScene = 1f;

		/// <summary>Params.x of a directional light.</summary>
		public const float KindDirectional = 2f;

		/// <summary>
		///     Local position; w: the core's radius (local units, own light) or the range (world units, scene light). A
		///     directional light: the local unit vector towards it; w: 1.
		/// </summary>
		public Vector4 PositionSize;

		/// <summary>x: 0 own light, 1 scene light, 2 directional light; y: the source's radius (world); z: cos of the outer half angle (-2: no cone); w: 1 / (cos inner - cos outer).</summary>
		public Vector4 Params;

		/// <summary>xyz: the spot's direction (local); w: the factor the transport holds the light times (the colour is divided by it).</summary>
		public Vector4 Spot;

		/// <summary>The light of its own (null for a scene light).</summary>
		public VolumeLight Own;

		/// <summary>The scene light (null for a light of its own).</summary>
		public global::Light Scene;

		/// <summary>The directional light (null for the other kinds).</summary>
		public global::DirectionalLight Directional;

		/// <summary>A directional light's slot: the march scatters it as the sun (albedo, phase, the softer orders), not as a glow.</summary>
		public bool IsDirectional => Params.X > 1.5f;

		/// <summary>
		///     The light's colour x intensity right now (a scene light's times its volumetric strength), divided by the
		///     transport's factor (what the march multiplies the transport by).
		/// </summary>
		public Vector3 Color
		{
			get
			{
				if (Directional != null)
				{
					return Directional.Shines && Directional.AffectsVolumes ? Directional.Radiance * System.MathF.Max(Directional.VolumetricStrength, 0f) : Vector3.Zero;
				}

				if (Scene != null)
				{
					return Scene.IsDestroyed || !Scene.isActiveAndEnabled ? Vector3.Zero : Scene.Radiance * System.MathF.Max(Scene.VolumetricStrength, 0f) / System.MathF.Max(Spot.W, 1e-12f);
				}

				return Own != null ? new Vector3(Own.Color.R, Own.Color.G, Own.Color.B) * Own.Intensity : Vector3.Zero;
			}
		}

		/// <summary>
		///     How much of the light the GI takes (the cloud's light in the GI, VolumetricMedia): a scene light's Indirect
		///     Strength, 0 with its Affects GI off; a light of the cloud's own counts whole.
		/// </summary>
		public float IndirectStrength
		{
			get
			{
				if (Directional != null)
				{
					return Directional.AffectsGI ? System.MathF.Max(Directional.IndirectStrength, 0f) : 0f;
				}

				if (Scene != null)
				{
					return Scene.AffectsGI ? System.MathF.Max(Scene.IndirectStrength, 0f) : 0f;
				}

				return 1f;
			}
		}
	}
}
