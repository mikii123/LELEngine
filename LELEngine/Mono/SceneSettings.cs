using System;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     Per-scene lighting, saved with the scene: the environment (ambient, specular, background, skybox, exposure),
	///     the reach of the shadow map and where and how strongly global illumination lights the scene. How shadows
	///     and GI are computed (quality, GI method and its tunables) is project-wide (<see cref="ProjectSettings" />).
	///     <see cref="Activate" /> makes these objects the live settings (<see cref="Lighting.Environment" />,
	///     <see cref="Lighting.SceneShadows" />, <see cref="Lighting.SceneGI" />), so edits made at runtime or in the
	///     editor apply to the scene directly.
	/// </summary>
	[Serializable]
	public sealed class SceneSettings
	{
		#region PublicFields

		public EnvironmentSettings Environment = new EnvironmentSettings();
		public SceneShadowSettings Shadows = new SceneShadowSettings();
		public SceneGISettings GI = new SceneGISettings();

		#endregion

		#region PublicMethods

		public void Activate()
		{
			Lighting.Environment = Environment;
			Lighting.SceneShadows = Shadows;
			Lighting.SceneGI = GI;
			Lighting.GI.InvalidateStatic();
		}

		#endregion
	}

	/// <summary>The shadow map's reach in the scene (its resolution and biases are project-wide, see <see cref="ShadowSettings" />).</summary>
	[Serializable]
	public sealed class SceneShadowSettings
	{
		#region PublicFields

		/// <summary>Half extent of the orthographic shadow frustum around the camera, in world units.</summary>
		public float Distance = 60f;

		/// <summary>Half depth range of the shadow frustum along the light direction.</summary>
		public float DepthRange = 200f;

		#endregion
	}

	/// <summary>
	///     Global illumination in the scene: whether it is on, the volume it covers and how strongly it lights (the
	///     method and its tunables are project-wide, see <see cref="GlobalIlluminationSettings" />).
	/// </summary>
	[Serializable]
	public sealed class SceneGISettings
	{
		#region PublicFields

		public bool Enabled = true;

		/// <summary>World-space edge length of the cubic GI volume.</summary>
		public float GridSize = 32f;

		/// <summary>Volume center when <see cref="FollowCamera" /> is off.</summary>
		public Vector3 Center = Vector3.Zero;

		/// <summary>Center the volume on the camera (snapped to voxels) instead of <see cref="Center" />.</summary>
		public bool FollowCamera;

		public float DiffuseStrength = 1f;
		public float SpecularStrength = 1f;
		public float OcclusionStrength = 1f;

		/// <summary>
		///     Weight of indirect light fed back from the previous frame's radiance volume when lighting voxels.
		///     1 gives multi-bounce lighting converging over a few frames; 0 keeps a single bounce.
		/// </summary>
		public float BounceStrength = 1f;

		/// <summary>Radiance from outside the scene seen by cones that escape (linear RGB).</summary>
		public Vector3 SkyRadiance = Vector3.Zero;

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
