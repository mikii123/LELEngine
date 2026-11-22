using System;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Impostors
{
	/// <summary>
	///     How an impostor is baked (<see cref="ImpostorBaker" />): how many views, how large each, and from where.
	///     The atlases are <see cref="Frames" /> x <see cref="FrameSize" /> texels a side.
	/// </summary>
	[Serializable]
	public sealed class ImpostorSettings
	{
		#region PublicFields

		/// <summary>
		///     Views along a side of the grid (2 to 32): the object is captured from Frames x Frames directions and drawn
		///     from the three nearest to the view. More views: less change between them as the object turns, and a larger
		///     atlas (or smaller views).
		/// </summary>
		[Range(ImpostorMath.MinFrames, ImpostorMath.MaxFrames)]
		[Tooltip("Views along a side of the grid: the object is captured from Frames x Frames directions. More views change less between each other as the object turns")]
		public int Frames = 12;

		/// <summary>
		///     Texels along a side of one view (a power of two, 16 to 1024): how large the impostor can be on screen before
		///     it blurs. A power of two keeps the atlas's mip levels from mixing neighbouring views.
		/// </summary>
		[Tooltip("Texels along a side of one view (a power of two): how tall the impostor can be on screen, in pixels, before it blurs")]
		public int FrameSize = 128;

		/// <summary>
		///     Only the directions above the horizon (objects standing on the ground, never seen from below): the same
		///     number of views covers half the directions, twice as densely.
		/// </summary>
		[Tooltip("Capture only from above the horizon (objects on the ground that are never seen from below): the same views, twice as dense")]
		public bool Hemisphere;

		/// <summary>Capture samples along a texel's side (1 to 4): the views are drawn larger and filtered down.</summary>
		[Range(1, 4)]
		[Tooltip("Capture samples along a texel's side: the views are drawn larger and filtered down (smoother edges, a slower bake)")]
		public int Supersampling = 4;

		/// <summary>Texels left empty around the object in every view, so a filtered lookup never reaches the next view.</summary>
		[Range(1, 8)]
		[Tooltip("Texels left empty around the object in every view")]
		public int Border = 2;

		#endregion

		#region PublicMethods

		/// <summary>Texels along a side of the atlases.</summary>
		public int AtlasSize => Math.Clamp(Frames, ImpostorMath.MinFrames, ImpostorMath.MaxFrames) * ValidFrameSize(FrameSize);

		/// <summary>A copy with every value in its range (the frame size the nearest power of two).</summary>
		public ImpostorSettings Validated()
		{
			return new ImpostorSettings
			{
				Frames = Math.Clamp(Frames, ImpostorMath.MinFrames, ImpostorMath.MaxFrames),
				FrameSize = ValidFrameSize(FrameSize),
				Hemisphere = Hemisphere,
				Supersampling = Math.Clamp(Supersampling, 1, 4),
				Border = Math.Clamp(Border, 1, 8)
			};
		}

		public ImpostorSettings Clone()
		{
			return (ImpostorSettings)MemberwiseClone();
		}

		/// <summary>The power of two nearest to <paramref name="size" />, from 16 to 1024.</summary>
		public static int ValidFrameSize(int size)
		{
			int clamped = Math.Clamp(size, 16, 1024);
			int power = 16;
			while (power * 2 <= clamped)
			{
				power *= 2;
			}

			// The nearer of the two powers around it.
			return power < 1024 && clamped - power > power * 2 - clamped ? power * 2 : power;
		}

		#endregion
	}

	/// <summary>
	///     What a bake gives (<see cref="ImpostorBaker.Bake(System.Collections.Generic.IReadOnlyList{MeshRenderer}, Matrix4, ImpostorSettings)" />):
	///     the atlases' texels and what the shader needs to read them (Engine/Impostor.shader). The atlases are square,
	///     <see cref="AtlasSize" /> texels a side, RGBA of 8 bits, the bottom row first (as the engine's textures are
	///     stored); view (column, row) of the grid is the square of <see cref="FrameSize" /> texels at column x FrameSize,
	///     row x FrameSize.
	/// </summary>
	public sealed class ImpostorBake
	{
		#region PublicFields

		public int Frames;
		public int FrameSize;
		public bool Hemisphere;
		public int AtlasSize => Frames * FrameSize;

		/// <summary>The centre of the sphere every view looks at, in the object's space.</summary>
		public Vector3 Center;

		/// <summary>Half the width of a view: the radius of the object's bounding sphere and the border around it.</summary>
		public float Radius;

		/// <summary>The object's bounds, in its space (the proxy mesh is their box).</summary>
		public Vector3 BoundsMin;

		public Vector3 BoundsMax;

		/// <summary>
		///     rg: the surface normal's x and y in its view's axes (<see cref="ImpostorMath.FrameAxes" />; z, towards the
		///     view's camera, is rebuilt), b: the depth along the view's direction (0.5 at the sphere's centre, 1 a radius
		///     towards the camera), a: the coverage as a distance field (the outline at 0.5). Linear.
		/// </summary>
		public byte[] Shape;

		/// <summary>
		///     rgb: the colour the surface scatters (sRGB), a: its smoothness when there is a <see cref="Specular" /> atlas,
		///     else the occlusion of its indirect light.
		/// </summary>
		public byte[] Base;

		/// <summary>
		///     rgb: the specular colour (sRGB), a: the occlusion of the indirect light. Null when no surface has a specular
		///     colour of its own (materials lit with the scene's highlight): the impostor is then lit that way too.
		/// </summary>
		public byte[] Specular;

		/// <summary>
		///     With a <see cref="Specular" /> atlas: some of the surfaces have no specular colour (materials lit with the
		///     scene's highlight beside physically based ones). The shader then keeps the scene's highlight where the
		///     atlas is black (its MIXED_WORKFLOW variant).
		/// </summary>
		public bool SceneHighlights;

		/// <summary>rgb: the emitted radiance over <see cref="EmissionScale" /> (sRGB). Null when nothing emits.</summary>
		public byte[] Emission;

		/// <summary>The brightest emitted radiance: what a texel of 1 in <see cref="Emission" /> stands for.</summary>
		public float EmissionScale;

		/// <summary>The mean roughness of the surfaces (what an impostor without a specular atlas traces its reflections with).</summary>
		public float Roughness = 0.6f;

		/// <summary>Triangles of the meshes the impostor stands for.</summary>
		public int SourceTriangles;

		/// <summary>How long the bake took, in milliseconds.</summary>
		public double Milliseconds;

		#endregion
	}
}
