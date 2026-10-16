using System;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     Per-scene look: environment (ambient, specular, background, exposure), shadows and global illumination.
	///     Saved with the scene. <see cref="Activate" /> makes these objects the live settings
	///     (<see cref="Lighting.Environment" />, <see cref="Lighting.Shadows" />, <see cref="Lighting.GI" />), so
	///     edits made at runtime or in the editor apply to the scene directly.
	/// </summary>
	[Serializable]
	public sealed class SceneSettings
	{
		#region PublicFields

		public EnvironmentSettings Environment = new EnvironmentSettings();
		public ShadowSettings Shadows = new ShadowSettings();
		public GlobalIlluminationSettings GI = new GlobalIlluminationSettings();
		public Physics2DSettings Physics2D = new Physics2DSettings();

		#endregion

		#region PublicMethods

		public void Activate()
		{
			Lighting.Environment = Environment;
			Lighting.Shadows = Shadows;
			Lighting.GI = GI;
			GI.InvalidateStatic();
		}

		#endregion
	}

	/// <summary>2D physics of the scene (see <see cref="LELEngine.Physics2D" />): gravity, solver iterations, query options and the layer collision matrix.</summary>
	[Serializable]
	public sealed class Physics2DSettings
	{
		#region PublicFields

		public Vector2 Gravity = new Vector2(0f, -9.81f);

		[Range(1, 50)] public int VelocityIterations = 8;
		[Range(1, 50)] public int PositionIterations = 3;

		/// <summary>Queries (raycasts, overlaps) report trigger colliders.</summary>
		public bool QueriesHitTriggers = true;

		/// <summary>Raycasts starting inside a collider report it.</summary>
		public bool QueriesStartInColliders = true;

		/// <summary>Friction and bounciness of colliders without a material.</summary>
		[Range(0f, 1f)] public float DefaultFriction = 0.4f;
		[Range(0f, 1f)] public float DefaultBounciness;

		/// <summary>Per layer: the mask of layers it collides with (bit n = layer n); all by default.</summary>
		[HideInInspector] public int[] LayerCollisionMatrix = DefaultMatrix();

		#endregion

		#region PublicMethods

		public bool GetIgnoreLayerCollision(int layer1, int layer2)
		{
			EnsureMatrix();
			return (LayerCollisionMatrix[layer1 & 31] & (1 << (layer2 & 31))) == 0;
		}

		public void IgnoreLayerCollision(int layer1, int layer2, bool ignore)
		{
			EnsureMatrix();
			int a = layer1 & 31;
			int b = layer2 & 31;
			if (ignore)
			{
				LayerCollisionMatrix[a] &= ~(1 << b);
				LayerCollisionMatrix[b] &= ~(1 << a);
			}
			else
			{
				LayerCollisionMatrix[a] |= 1 << b;
				LayerCollisionMatrix[b] |= 1 << a;
			}
		}

		/// <summary>The mask of layers a layer collides with.</summary>
		public int GetLayerCollisionMask(int layer)
		{
			EnsureMatrix();
			return LayerCollisionMatrix[layer & 31];
		}

		#endregion

		#region PrivateMethods

		private static int[] DefaultMatrix()
		{
			var matrix = new int[32];
			for (int i = 0; i < 32; i++)
			{
				matrix[i] = -1;
			}

			return matrix;
		}

		private void EnsureMatrix()
		{
			if (LayerCollisionMatrix == null || LayerCollisionMatrix.Length != 32)
			{
				LayerCollisionMatrix = DefaultMatrix();
			}
		}

		#endregion
	}

	/// <summary>Ambient and specular terms, background color and the camera exposure / tonemapping.</summary>
	[Serializable]
	public sealed class EnvironmentSettings
	{
		#region PublicFields

		public Color4 AmbientColor = Color4.White;
		public float AmbientStrength;

		public float SpecularStrength;
		public float SpecularShine;

		/// <summary>Clear color of the scene target (what the camera sees where nothing is drawn and no skybox is set).</summary>
		public Color4 BackgroundColor = new Color4(0f, 0f, 0f, 1f);

		/// <summary>
		///     Material drawn behind the scene (a skybox shader such as Engine/Skybox.shader with a cubemap, or
		///     Engine/SkyboxPanoramic.shader with a panorama); null shows the background color.
		/// </summary>
		public Shaders.Material Skybox;

		public float Exposure = 1f;

		/// <summary>ACES tonemapping; off clamps the HDR color.</summary>
		public bool Tonemap = true;

		#endregion

		#region PublicMethods

		/// <summary>Copies the values into the uniform carriers and the renderer settings (every frame).</summary>
		public void ApplyTo(RenderSettings settings)
		{
			Lighting.Ambient.Color = AmbientColor;
			Lighting.Ambient.Strength = AmbientStrength;
			Lighting.Specular.Strength = SpecularStrength;
			Lighting.Specular.Shine = SpecularShine;
			if (settings != null)
			{
				settings.ClearColor = BackgroundColor;
				settings.Exposure = Exposure;
				settings.Tonemap = Tonemap;
			}
		}

		#endregion
	}
}
